namespace CadMcp.Core
{
    public static class Portable
    {
        public static bool AsciiLetterOrDigit(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
        public static bool AsciiHexDigit(char c) => c is >= 'A' and <= 'F' or >= 'a' and <= 'f' or >= '0' and <= '9';
        public static string Hash(byte[] data) { using var hash = System.Security.Cryptography.SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", ""); }
        public static void ReplaceFile(string temp, string path) { if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
        public static async Task<T> Await<T>(Task<T> task, CancellationToken ct)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult(true)))
            { if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task) throw new OperationCanceledException(ct); return await task.ConfigureAwait(false); }
        }
    }
}
