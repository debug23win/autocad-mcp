using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Providers;

/// <summary>Starts the official subscription CLIs without interpreting arguments in a shell.</summary>
internal static class SubscriptionProcess
{
    internal static ProcessStartInfo Create(ProviderOptions options, IReadOnlyList<string> arguments,
        string package, string command, bool interactive = false)
    {
        string executable = ResolveExecutable(options.Executable);
        string? entryPoint = null;
        if (Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // npm's Windows shim is a batch program. Resolve its official package's bin entry instead
            // of passing a path (which may contain %, &, or spaces) through cmd.exe.
            (executable, entryPoint) = ResolveNpmEntry(executable, package, command);
        }
        var info = ProviderProcess.StartInfo(options with { Executable = executable },
            entryPoint is null ? arguments : new[] { entryPoint }.Concat(arguments));
        if (interactive)
        {
            info.CreateNoWindow = false;
            info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = false;
            info.StandardInputEncoding = info.StandardOutputEncoding = info.StandardErrorEncoding = null;
        }
        return info;
    }

    internal static string ResolveExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) throw new IOException(UiText.T("Не указан путь к CLI."));
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(executable);
        var extensions = OperatingSystem.IsWindows() && Path.GetExtension(executable).Length == 0
            ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (string extension in extensions)
        {
            string path = Path.Combine(directory.Trim('"'), executable + extension);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        // Let Process.Start report a missing native command. A named npm shim must be found above.
        return executable;
    }

    internal static (string Executable, string EntryPoint) ResolveNpmEntry(string shim, string package, string command)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(shim))!;
        string packageDirectory = Path.Combine(directory, "node_modules", package.Replace('/', Path.DirectorySeparatorChar));
        string manifest = Path.Combine(packageDirectory, "package.json");
        if (!File.Exists(manifest) || new FileInfo(manifest).Length > 128 * 1024)
            throw new IOException(UiText.T("Не найден установленный npm-пакет CLI рядом с ") + shim + ". " + package);
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
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
}
