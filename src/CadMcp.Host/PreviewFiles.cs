using CadMcp.Core;

namespace CadMcp.Host;

/// <summary>Local copies of rendered previews for tools that need a file path. Old copies are removed.</summary>
internal static class PreviewFiles
{
    internal const int MaximumFiles = 64;
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);
    public static string Save(string imageId, byte[] png, string? folder = null)
    {
        folder ??= Wire.DataDirectory("previews");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, imageId + ".png");
        File.WriteAllBytes(path, png);
        Prune(folder, path);
        return path;
    }
    internal static void Prune(string folder, string keep)
    {
        var cutoff = DateTime.UtcNow - MaximumAge;
        var files = new DirectoryInfo(folder).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        for (int i = 0; i < files.Length; i++)
        {
            if (i < MaximumFiles && files[i].LastWriteTimeUtc >= cutoff) continue;
            if (string.Equals(files[i].FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
            try { files[i].Delete(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
