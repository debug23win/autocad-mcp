using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CadMcp.Core;

namespace CadMcp.Providers;

/// <summary>Starts the official subscription CLIs without interpreting arguments in a shell.</summary>
internal static class SubscriptionProcess
{
    private const int ManifestLimit = 128 * 1024;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private sealed record Launch(string Executable, string? EntryPoint = null, string? QwenLauncher = null, bool Bun = false);

    internal static ProcessStartInfo Create(ProviderOptions options, IReadOnlyList<string> arguments,
        string package, string command, bool interactive = false)
    {
        string executable = ResolveExecutable(options.Executable);
        var launch = ResolveLaunch(executable, package, command, new HashSet<string>(PathComparer));
        var info = ProviderProcess.StartInfo(options with { Executable = launch.Executable },
            launch.EntryPoint is null ? arguments : new[] { launch.EntryPoint }.Concat(arguments));
        if (launch.QwenLauncher is not null) info.Environment["QWEN_CODE_LAUNCHER_PATH"] = launch.QwenLauncher;
        if (launch.Bun && (!info.Environment.TryGetValue("QWEN_TUI_RENDERER", out var renderer) || string.IsNullOrEmpty(renderer)))
            info.Environment["QWEN_TUI_RENDERER"] = "opentui";
        if (interactive)
        {
            info.CreateNoWindow = false;
            info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = false;
            info.StandardInputEncoding = info.StandardOutputEncoding = info.StandardErrorEncoding = null;
        }
        return info;
    }

    internal static string ResolveExecutable(string executable, IEnumerable<string>? searchDirectories = null)
    {
        if (string.IsNullOrWhiteSpace(executable)) throw Missing(executable);
        string command = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        return FindExecutable(executable, searchDirectories ?? SubscriptionCliSetup.SearchDirectories(command)) ?? throw Missing(executable);
    }

    internal static string? FindExecutable(string executable, IEnumerable<string> searchDirectories)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(executable) ? Path.GetFullPath(executable) : null;
        string command = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        // Saved bare qwen.cmd/kimi.cmd values predate standalone installation support.
        bool accountCli = command is "qwen" or "kimi";
        var names = accountCli
            ? Path.GetExtension(executable).Length == 0
                ? new[] { command + ".exe", command + ".cmd", command + ".bat", executable }
                : new[] { executable, command + ".exe", command + ".cmd", command + ".bat", command }
            : OperatingSystem.IsWindows() && Path.GetExtension(executable).Length == 0
                ? new[] { executable + ".exe", executable + ".cmd", executable + ".bat", executable }
                : new[] { executable };
        var seen = new HashSet<string>(PathComparer);
        foreach (string raw in searchDirectories)
        {
            string directory = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            if (!Path.IsPathRooted(directory) || !seen.Add(directory)) continue;
            foreach (string name in names.Distinct(PathComparer))
            {
                try
                {
                    string path = Path.Combine(directory, name);
                    if (File.Exists(path)) return Path.GetFullPath(path);
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    private static IOException Missing(string executable) => new("CLI_NOT_FOUND: " + executable + ". " +
        UiText.T("Команда CLI не найдена. Нажмите «Установить CLI», затем «Найти CLI», либо укажите существующий полный путь к официальному CLI. Для npm-установки также требуется Node.js."));

    private static Launch ResolveLaunch(string executable, string package, string command, HashSet<string> visited)
    {
        if (!Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase)) return new(executable);
        if (!visited.Add(executable) || visited.Count > 4)
            throw new IOException(UiText.T("Цепочка запуска CLI зациклена или слишком длинна. Укажите путь к установленному CLI."));
        string directory = Path.GetDirectoryName(executable)!;
        if (command == "qwen" && package == "@qwen-code/qwen-code")
        {
            string root = Path.GetFullPath(Path.Combine(directory, ".."));
            string manifest = Path.Combine(root, "manifest.json");
            if (Path.GetFileName(directory).Equals("bin", StringComparison.OrdinalIgnoreCase) && File.Exists(manifest))
            {
                using var json = ReadManifest(manifest);
                if (ProviderJson.Text(json.RootElement, "name") != package)
                    throw new IOException(UiText.T("Манифест CLI не соответствует официальному пакету: ") + package);
                string? runtime = ProviderJson.Text(json.RootElement, "runtime");
                if (runtime is not (null or "node" or "bun"))
                    throw new IOException(UiText.T("Неизвестная среда выполнения установленного Qwen Code."));
                bool bun = runtime == "bun";
                string engine = Path.Combine(root, bun ? "bun" : "node", bun ? "bun.exe" : "node.exe");
                string entry = Path.Combine(root, "lib", "cli-entry.js");
                if (!File.Exists(engine)) throw Missing(engine);
                if (!File.Exists(entry)) throw new IOException(UiText.T("Точка входа CLI отсутствует: ") + entry);
                return new(engine, entry, executable, bun);
            }
            string? forward = QwenForwardTarget(executable);
            if (forward is not null) return ResolveLaunch(ResolveExecutable(forward), package, command, visited);
        }
        var npm = ResolveNpmEntry(executable, package, command);
        return new(npm.Executable, npm.EntryPoint);
    }

    private static string? QwenForwardTarget(string shim)
    {
        string[] lines = ReadBounded(shim, 32 * 1024).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (lines.Length is < 2 or > 3 || !lines[0].Equals("@echo off", StringComparison.OrdinalIgnoreCase)) return null;
        if (lines.Length == 3 && !lines[1].Equals("REM Qwen Code current-session shim. Generated by install-qwen-standalone.ps1.", StringComparison.OrdinalIgnoreCase)) return null;
        var match = Regex.Match(lines[^1], "^call \"([^\"]+)\" %\\*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        string target = match.Groups[1].Value;
        // The path is literal; variables and command fragments are never expanded or executed.
        if (!Path.IsPathFullyQualified(target)) return null;
        return target;
    }

    internal static (string Executable, string EntryPoint) ResolveNpmEntry(string shim, string package, string command)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(shim))!;
        string nodeModules = Path.GetFileName(directory).Equals(".bin", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "node_modules", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory)! : Path.Combine(directory, "node_modules");
        string packageDirectory = Path.Combine(nodeModules, package.Replace('/', Path.DirectorySeparatorChar));
        string manifest = Path.Combine(packageDirectory, "package.json");
        if (!File.Exists(manifest))
            throw new IOException(UiText.T("Не распознана установка CLI: ") + shim + ". " +
                UiText.T("Укажите официальный standalone CLI или переустановите npm-пакет: ") + package);
        using var json = ReadManifest(manifest);
        if (ProviderJson.Text(json.RootElement, "name") != package)
            throw new IOException(UiText.T("Манифест CLI не соответствует официальному пакету: ") + package);
        var bin = ProviderJson.Child(json.RootElement, "bin");
        string? relative = bin.ValueKind == JsonValueKind.String ? bin.GetString() : ProviderJson.Text(bin, command);
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new IOException(UiText.T("В npm-пакете отсутствует корректная точка входа CLI: ") + package);
        string entry = Path.GetFullPath(Path.Combine(packageDirectory, relative));
        string root = Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar;
        if (!entry.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(entry))
            throw new IOException(UiText.T("Точка входа CLI находится вне установленного npm-пакета или отсутствует."));
        string localNode = Path.Combine(directory, OperatingSystem.IsWindows() ? "node.exe" : "node");
        string node = File.Exists(localNode) ? localNode : ResolveExecutable(OperatingSystem.IsWindows() ? "node.exe" : "node");
        return (node, entry);
    }

    private static JsonDocument ReadManifest(string path)
    {
        try
        {
            var json = JsonDocument.Parse(ReadBounded(path, ManifestLimit), new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind == JsonValueKind.Object) return json;
            json.Dispose();
        }
        catch (JsonException error) { throw new IOException(UiText.T("Некорректный манифест CLI: ") + path, error); }
        throw new IOException(UiText.T("Некорректный манифест CLI: ") + path);
    }

    private static string ReadBounded(string path, int maximum)
    {
        using var stream = File.OpenRead(path);
        var bytes = new byte[maximum + 1];
        int count = 0;
        while (count < bytes.Length)
        {
            int read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > maximum) throw new IOException(UiText.T("Файл запуска CLI превышает допустимый размер: ") + path);
        return Encoding.UTF8.GetString(bytes, 0, count).TrimStart('\uFEFF');
    }
}
