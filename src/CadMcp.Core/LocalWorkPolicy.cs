using Microsoft.Win32;

namespace CadMcp.Core;

/// <summary>Synced folders may be read as references, but are never CAD workspaces or mutation destinations.</summary>
public static class LocalWorkPolicy
{
    public static string DefaultWorkspace => Wire.DataDirectory("workspace");
    public static bool IsOneDrive(string path, IEnumerable<string>? knownRoots = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool Under(string candidate, string root)
        {
            string normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return candidate.Equals(normalized, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        string[] roots = (knownRoots ?? OneDriveRoots()).Where(r => !string.IsNullOrWhiteSpace(r)).ToArray();
        bool Synced(string candidate) => candidate.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) || part.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase))
            || roots.Any(root => Under(candidate, root));
        if (Synced(full)) return true;
        // Resolve directory junctions too: a local-looking alias must not bypass the synced-root guard.
        for (var directory = new DirectoryInfo(File.Exists(full) ? Path.GetDirectoryName(full)! : full); directory is not null; directory = directory.Parent)
        {
            try { if (directory.Exists && directory.ResolveLinkTarget(returnFinalTarget: true) is { } target && Synced(target.FullName)) return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { throw new CadFault("WORKSPACE_PATH_UNVERIFIED", "Cannot verify workspace junction: " + e.Message); }
        }
        return false;
    }
    public static void RequireLocal(string path)
    {
        if (IsOneDrive(path)) throw new CadFault("ONEDRIVE_WORKSPACE_BLOCKED", "Editing in OneDrive is disabled. Save a local DWG outside OneDrive and select a local agent working folder. Reference files may still be read.");
    }
    private static IEnumerable<string> OneDriveRoots()
    {
        var roots = new List<string>();
        foreach (string name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } root) roots.Add(root);
        if (OperatingSystem.IsWindows())
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            if (accounts is not null)
                foreach (string name in accounts.GetSubKeyNames())
                { using var account = accounts.OpenSubKey(name); if (account?.GetValue("UserFolder") is string root) roots.Add(root); }
        }
        return roots;
    }
}
