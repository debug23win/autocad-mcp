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

    /// <summary>
    /// The configuration with the cad server added, or replaced when an earlier CAD MCP host registered it; null when
    /// the existing text is not a JSON object. A cad server running another program is kept and named in conflict.
    /// </summary>
    public static string? AddToDesktopConfig(string? existing, string hostPath, out string? conflict)
    {
        conflict = null;
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
        if (servers[ServerName] is { } current && !IsThisHost(Command(current), hostPath, anyInstallation: true))
        {
            conflict = Command(current) ?? current.ToJsonString();
            return existing;
        }
        servers[ServerName] = new JsonObject { ["command"] = hostPath, ["args"] = new JsonArray() };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? Command(JsonNode? entry) => entry is JsonObject server && server["command"] is JsonValue value && value.TryGetValue<string>(out var command) ? command : null;

    /// <summary>The command is this host, or with anyInstallation any CAD MCP host (another AutoCAD version or an earlier install).</summary>
    private static bool IsThisHost(string? command, string hostPath, bool anyInstallation) =>
        command is not null && (string.Equals(command, hostPath, StringComparison.OrdinalIgnoreCase) ||
            anyInstallation && string.Equals(Path.GetFileName(command.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)), "CadMcp.Host.exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>The configuration without the cad server when it runs this host; unchanged text otherwise, null when damaged.</summary>
    public static string? RemoveFromDesktopConfig(string? existing, string hostPath)
    {
        if (string.IsNullOrWhiteSpace(existing)) return existing ?? "";
        JsonObject root;
        try { if (JsonNode.Parse(existing) is JsonObject parsed) root = parsed; else return null; }
        catch (JsonException) { return null; }
        if (root["mcpServers"] is not JsonObject servers || servers[ServerName] is not JsonObject entry) return existing;
        // A server of the same name that runs another program belongs to someone else.
        if (!IsThisHost(Command(entry), hostPath, anyInstallation: false)) return existing;
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
            // One client's failure (a locked file, a missing CLI) must not skip the next one.
            int result;
            try
            {
                result = client switch
                {
                    "claude-desktop" => Desktop(hostPath, remove),
                    "claude-code" => Code(hostPath, remove),
                    _ => Fail("Unknown client " + client + "; use claude-desktop, claude-code or all")
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or JsonException)
            { result = Fail(client + ": " + error.Message); }
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
        string? conflict = null;
        string? updated = remove ? RemoveFromDesktopConfig(existing, hostPath) : AddToDesktopConfig(existing, hostPath, out conflict);
        if (updated is null) return Fail("Claude Desktop configuration " + path + " is not a valid JSON object; it was left unchanged");
        if (conflict is not null) return Fail("Claude Desktop already has a server named " + ServerName + " that runs " + conflict + "; it was left unchanged. Add CAD MCP under another name by hand: " + hostPath);
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
        (int Exit, string Output) Claude(bool echo, params string[] arguments)
        {
            var start = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("cmd.exe", ShellArguments(claude, arguments))
                : new ProcessStartInfo(claude);
            if (!claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.UseShellExecute = false; start.CreateNoWindow = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000)) { try { process.Kill(true); } catch (InvalidOperationException) { } return (-1, ""); }
            if (echo) { Console.Write(output.Result); Console.Error.Write(error.Result); }
            return (process.ExitCode, output.Result + error.Result);
        }
        // Only a registration this host made is removed on uninstall; the marker records it.
        string marker = Wire.DataDirectory("claude-code-registration.txt");
        if (remove)
        {
            if (!File.Exists(marker) || !string.Equals(File.ReadAllText(marker).Trim(), hostPath, StringComparison.OrdinalIgnoreCase)) return 0;
            Claude(true, "mcp", "remove", "--scope", "user", ServerName);
            File.Delete(marker);
            return 0;
        }
        // An existing cad server is replaced only when it runs a CAD MCP host (an earlier install or another
        // AutoCAD version); a server of the same name that runs something else is left alone.
        var (found, description) = Claude(false, "mcp", "get", ServerName);
        if (found == 0)
        {
            if (description.IndexOf("CadMcp.Host", StringComparison.OrdinalIgnoreCase) < 0)
                return Fail("Claude Code already has a server named " + ServerName + " that does not run CAD MCP; it was left unchanged. Add CAD MCP under another name: claude mcp add --scope user <name> -- \"" + hostPath + "\"");
            Claude(true, "mcp", "remove", "--scope", "user", ServerName);
        }
        var (added, _) = Claude(true, "mcp", "add", "--scope", "user", ServerName, "--", hostPath);
        if (added != 0) return Fail("claude mcp add failed with exit code " + added);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, hostPath);
        Console.WriteLine("Claude Code: registered " + ServerName + " for the current user");
        return 0;
    }
}
