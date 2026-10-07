using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CadMcp.Core;

namespace CadMcp.Host;

/// <summary>
/// Registers this MCP host with Claude Desktop (its JSON configuration) and Claude Code (its own CLI), so the
/// installer can offer both as options. Other servers and settings are always kept, a damaged configuration is
/// never overwritten, and removal only touches an entry that points to this host.
/// </summary>
internal static class ClientRegistration
{
    public const string ServerName = "cad";

    public static string DesktopConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");

    /// <summary>The configuration with the cad server added or replaced; null when the existing text is not a JSON object.</summary>
    public static string? AddToDesktopConfig(string? existing, string hostPath)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(existing)) root = new();
        else
            try { if (JsonNode.Parse(existing) is JsonObject parsed) root = parsed; else return null; }
            catch (JsonException) { return null; }
        if (root["mcpServers"] is not JsonObject servers)
        {
            if (root["mcpServers"] is not null) return null;
            root["mcpServers"] = servers = new JsonObject();
        }
        servers[ServerName] = new JsonObject { ["command"] = hostPath, ["args"] = new JsonArray() };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The configuration without the cad server when it runs this host; unchanged text otherwise, null when damaged.</summary>
    public static string? RemoveFromDesktopConfig(string? existing, string hostPath)
    {
        if (string.IsNullOrWhiteSpace(existing)) return existing ?? "";
        JsonObject root;
        try { if (JsonNode.Parse(existing) is JsonObject parsed) root = parsed; else return null; }
        catch (JsonException) { return null; }
        if (root["mcpServers"] is not JsonObject servers || servers[ServerName] is not JsonObject entry) return existing;
        // A server of the same name that runs another program belongs to someone else.
        if (!string.Equals(entry["command"]?.GetValue<string>(), hostPath, StringComparison.OrdinalIgnoreCase)) return existing;
        servers.Remove(ServerName);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>cmd.exe arguments running a claude.cmd shim with arguments; /s keeps the inner quotes intact.</summary>
    public static string ShellArguments(string program, IEnumerable<string> arguments) =>
        "/d /s /c \"" + string.Join(" ", new[] { program }.Concat(arguments).Select(Quote)) + "\"";

    private static string Quote(string value) => value.Length > 0 && value.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^']) < 0 ? value : "\"" + value.Replace("\"", "\\\"") + "\"";

    /// <summary>The Claude Code executable or npm shim on PATH, or null.</summary>
    public static string? FindClaude(string? path = null)
    {
        foreach (var folder in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : ["claude"])
            {
                string candidate;
                try { candidate = Path.Combine(folder.Trim('"'), name); } catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    public static int Run(string[] args)
    {
        string hostPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CadMcp.Host.exe");
        bool remove = args.Contains("--unregister-client");
        int option = Array.FindIndex(args, a => a is "--register-client" or "--unregister-client");
        string target = option >= 0 && option + 1 < args.Length ? args[option + 1] : "";
        var targets = target == "all" ? new[] { "claude-desktop", "claude-code" } : [target];
        int status = 0;
        foreach (var client in targets)
        {
            int result = client switch
            {
                "claude-desktop" => Desktop(hostPath, remove),
                "claude-code" => Code(hostPath, remove),
                _ => Fail("Unknown client " + client + "; use claude-desktop, claude-code or all")
            };
            status = Math.Max(status, result);
        }
        return status;
    }

    private static int Fail(string message) { Console.Error.WriteLine(message); return 2; }

    private static int Desktop(string hostPath, bool remove)
    {
        string path = DesktopConfigPath;
        string? existing = File.Exists(path) ? File.ReadAllText(path) : null;
        if (remove && existing is null) return 0;
        string? updated = remove ? RemoveFromDesktopConfig(existing, hostPath) : AddToDesktopConfig(existing, hostPath);
        if (updated is null) return Fail("Claude Desktop configuration " + path + " is not a valid JSON object; it was left unchanged");
        if (updated == existing) { Console.WriteLine("Claude Desktop: nothing to change"); return 0; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // The first backup keeps the user's original configuration.
        if (existing is not null && !File.Exists(path + ".cadmcp.bak")) File.Copy(path, path + ".cadmcp.bak");
        var temp = path + ".tmp";
        File.WriteAllText(temp, updated);
        if (File.Exists(path)) Portable.ReplaceFile(temp, path); else File.Move(temp, path);
        Console.WriteLine("Claude Desktop: " + (remove ? "removed " : "registered ") + ServerName + " in " + path + "; restart Claude Desktop");
        return 0;
    }

    private static int Code(string hostPath, bool remove)
    {
        if (FindClaude() is not { } claude) return remove ? 0 : Fail("Claude Code (claude) is not on PATH; register later with: claude mcp add --scope user " + ServerName + " -- \"" + hostPath + "\"");
        int Claude(params string[] arguments)
        {
            var start = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("cmd.exe", ShellArguments(claude, arguments))
                : new ProcessStartInfo(claude);
            if (!claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.UseShellExecute = false; start.CreateNoWindow = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000)) { try { process.Kill(true); } catch (InvalidOperationException) { } return -1; }
            Console.Write(output.Result); Console.Error.Write(error.Result);
            return process.ExitCode;
        }
        // Only a registration this host made is removed on uninstall; the marker records it.
        string marker = Wire.DataDirectory("claude-code-registration.txt");
        if (remove)
        {
            if (!File.Exists(marker) || !string.Equals(File.ReadAllText(marker).Trim(), hostPath, StringComparison.OrdinalIgnoreCase)) return 0;
            Claude("mcp", "remove", "--scope", "user", ServerName);
            File.Delete(marker);
            return 0;
        }
        // Replacing keeps the registration pointing at this installation.
        int removed = Claude("mcp", "remove", "--scope", "user", ServerName);
        int added = Claude("mcp", "add", "--scope", "user", ServerName, "--", hostPath);
        if (added != 0) return Fail("claude mcp add failed with exit code " + added + (removed == 0 ? "" : " (no previous registration)"));
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, hostPath);
        Console.WriteLine("Claude Code: registered " + ServerName + " for the current user");
        return 0;
    }
}
