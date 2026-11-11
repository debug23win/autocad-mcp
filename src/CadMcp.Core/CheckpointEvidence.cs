using System.Security.Cryptography;
using System.Text.Json;

namespace CadMcp.Core;

public static class CheckpointEvidence
{
    /// <summary>Disk evidence only; the checkpoint is never opened in AutoCAD or modified.</summary>
    public static object Inspect(JsonElement checkpoint, bool verifyHash = false, CancellationToken ct = default)
    {
        string? path = checkpoint.Text("path");
        try
        {
            if (path is null || !File.Exists(path)) return new { present = false, hash_state = "not_available" };
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            bool? sizeMatches = checkpoint.TryGetProperty("bytes", out var bytes) && bytes.TryGetInt64(out long expected) ? expected == file.Length : null;
            if (!verifyHash) return new { present = true, bytes = file.Length, size_matches = sizeMatches, hash_state = "not_checked" };
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920]; int read;
            while ((read = file.Read(buffer)) != 0) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
            string actual = Convert.ToHexString(hash.GetHashAndReset());
            return new { present = true, bytes = file.Length, size_matches = sizeMatches, sha256 = actual,
                hash_state = string.Equals(actual, checkpoint.Text("sha256"), StringComparison.OrdinalIgnoreCase) ? "passed" : "failed" };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new { present = (bool?)null, hash_state = "unverified", error = error.Message }; }
    }
}
