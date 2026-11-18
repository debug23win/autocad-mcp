using System.Diagnostics;
using CadMcp.Core;

namespace CadMcp.Providers;

/// <summary>Discovery and explicit user-started installation of the official account CLIs.</summary>
public static class SubscriptionCliSetup
{
    public static string FindExecutable(string command)
    {
        RequireCommand(command);
        return SubscriptionProcess.FindExecutable(command, SearchDirectories(command)) ?? command;
    }

    public static ProcessStartInfo CreateInstallStartInfo(string command)
    {
        string url = RequireCommand(command) switch
        {
            "qwen" => "https://qwen-code-assets.oss-cn-hangzhou.aliyuncs.com/installation/install-qwen-standalone.ps1",
            _ => "https://code.kimi.com/kimi-code/install.ps1"
        };
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var info = new ProcessStartInfo(string.IsNullOrEmpty(system) ? "powershell.exe" :
            Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = false,
            WorkingDirectory = LocalWorkPolicy.DefaultWorkspace
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NoExit");
        info.ArgumentList.Add("-Command");
        // Only these fixed vendor URLs are accepted. No model/user text is interpreted as code.
        info.ArgumentList.Add("Invoke-RestMethod -Uri '" + url + "' | Invoke-Expression");
        return info;
    }

    private static string RequireCommand(string command) => command is "qwen" or "kimi" ? command :
        throw new ArgumentException("Only the official qwen and kimi installers are supported.", nameof(command));

    internal static IEnumerable<string> SearchDirectories(string command)
    {
        var paths = new List<string>();
        void Add(string? path) { if (!string.IsNullOrWhiteSpace(path)) paths.Add(path); }
        void AddUnder(string? root, params string[] parts)
        {
            if (!string.IsNullOrWhiteSpace(root)) Add(Path.Combine(new[] { root }.Concat(parts).ToArray()));
        }
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (command == "qwen")
        {
            Add(Environment.GetEnvironmentVariable("QWEN_INSTALL_BIN_DIR"));
            AddUnder(Environment.GetEnvironmentVariable("QWEN_INSTALL_ROOT"), "bin");
            AddUnder(Environment.GetEnvironmentVariable("QWEN_INSTALL_LIB_DIR"), "bin");
            AddUnder(local, "qwen-code", "bin");
            AddUnder(local, "qwen-code", "qwen-code", "bin");
        }
        if (command == "kimi")
        {
            AddUnder(Environment.GetEnvironmentVariable("KIMI_INSTALL_DIR"), "bin");
            AddUnder(user, ".kimi-code", "bin");
        }
        // Read the registry-backed PATH as well: AutoCAD may predate a CLI installation.
        AddPaths(Environment.GetEnvironmentVariable("PATH"));
        if (OperatingSystem.IsWindows())
        {
            AddPaths(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            AddPaths(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        }
        AddUnder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        Add(Environment.GetEnvironmentVariable("PNPM_HOME"));
        AddUnder(local, "pnpm");
        AddUnder(user, ".local", "bin");
        AddUnder(user, ".bun", "bin");
        AddUnder(user, ".volta", "bin");
        AddUnder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs");
        AddUnder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs");
        return paths;

        void AddPaths(string? path)
        {
            foreach (string item in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) Add(item);
        }
    }
}
