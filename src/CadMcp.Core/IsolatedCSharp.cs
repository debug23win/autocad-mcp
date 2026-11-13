using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CadMcp.Core;

public sealed record IsolatedCSharpRequest(string RunId, string Nonce, string OperationId, int ParentProcessId,
    string SourceSha256, string AssemblySha256, string InputSha256, DateTimeOffset Deadline);
public sealed record IsolatedCSharpReceipt(string RunId, string Nonce, string OperationId, int ProcessId,
    string State, string SourceSha256, string InputSha256, string? OutputSha256 = null, long? OutputBytes = null,
    JsonElement? Value = null, Fault? Error = null, string[]? ChangedHandles = null, int? ChangedEntityCount = null, bool ChangedHandlesOmitted = false);
public sealed record IsolatedCSharpOutcome(bool Success, string Directory, int? ProcessId, int? ExitCode,
    string? LastPhase, IsolatedCSharpReceipt? Receipt, Fault? Error, bool ChildOutcomeUnknown, bool ProcessStillRunning = false)
{
    public string? ExitCodeHex => ExitCode is { } code ? $"0x{unchecked((uint)code):X8}" : null;
}

/// <summary>One execution on one retained copy. No process lookup, fallback, replay or import into the live DWG.</summary>
public sealed class IsolatedCSharpJob(string directory, IsolatedCSharpRequest request)
{
    public const string EnvironmentKey = "CADMCP_ISOLATED_CSHARP_JOB";
    public string Directory { get; } = Path.GetFullPath(directory);
    public IsolatedCSharpRequest Request { get; } = request;
    public string FilePath(string name) => Path.Combine(Directory, name);
    public static string HashFile(string path) { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return Convert.ToHexString(SHA256.HashData(file)); }
    public void Write(string name, object value)
    {
        string path = FilePath(name), temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, value, Wire.Json); file.Flush(true); }
        Portable.ReplaceFile(temp, path);
    }
    public void Phase(string phase) => Write("phase.json", new { Request.RunId, Request.OperationId, process_id = Environment.ProcessId, phase, at = DateTimeOffset.UtcNow });
    public static IsolatedCSharpJob Create(string root, string checkpoint, string checkpointHash, byte[] assembly,
        string sourceHash, string operationId, TimeSpan timeout)
    {
        LocalWorkPolicy.RequireLocal(root);
        string run = Guid.NewGuid().ToString("N"), directory = Path.Combine(root, run);
        System.IO.Directory.CreateDirectory(directory);
        File.Copy(checkpoint, Path.Combine(directory, "input.dwg"), overwrite: false);
        string inputHash = HashFile(Path.Combine(directory, "input.dwg"));
        if (!inputHash.Equals(checkpointHash, StringComparison.OrdinalIgnoreCase)) throw new CadFault("CSHARP_ISOLATION_INPUT_CHANGED", "Checkpoint hash changed; no child was started");
        File.WriteAllBytes(Path.Combine(directory, "script.dll"), assembly);
        var request = new IsolatedCSharpRequest(run, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), operationId,
            Environment.ProcessId, sourceHash, Convert.ToHexString(SHA256.HashData(assembly)), inputHash, DateTimeOffset.UtcNow.Add(timeout));
        var job = new IsolatedCSharpJob(directory, request); job.Write("request.json", request); job.Phase("prepared"); return job;
    }
    public static IsolatedCSharpJob Load(string path)
    {
        if (Path.GetFileName(path) != "request.json") throw new InvalidDataException("Unexpected isolated request filename");
        var request = JsonSerializer.Deserialize<IsolatedCSharpRequest>(File.ReadAllText(path), Wire.Json) ?? throw new InvalidDataException("Missing isolated request");
        if (request.ParentProcessId == Environment.ProcessId || request.Deadline <= DateTimeOffset.UtcNow || request.Nonce.Length != 64)
            throw new InvalidDataException("Invalid or expired isolated request");
        var job = new IsolatedCSharpJob(Path.GetDirectoryName(Path.GetFullPath(path))!, request);
        if (HashFile(job.FilePath("input.dwg")) != request.InputSha256 || HashFile(job.FilePath("script.dll")) != request.AssemblySha256)
            throw new InvalidDataException("Isolated input or assembly hash mismatch");
        return job;
    }
    public ProcessStartInfo AutoCADStart(string executable, string plugin, IEnumerable<string> parentArguments)
    {
        if (!Path.GetFileName(executable).Equals("acad.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable) || !File.Exists(plugin))
            throw new CadFault("CSHARP_ISOLATION_UNAVAILABLE", "A matching acad.exe and plugin are required; in-process execution is disabled");
        // The installed ApplicationPlugins directory is already trusted by AutoCAD. Never lower SECURELOAD/TRUSTEDPATHS.
        if (plugin.IndexOfAny(['\r', '\n', '"']) >= 0) throw new InvalidDataException("Invalid plugin path");
        File.WriteAllText(FilePath("startup.scr"), "_NETLOAD\n\"" + plugin.Replace('\\', '/') + "\"\n", new UTF8Encoding(false));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, WindowStyle = ProcessWindowStyle.Minimized };
        string cache = Path.Combine(start.WorkingDirectory, "UserDataCache");
        if (System.IO.Directory.Exists(cache)) start.WorkingDirectory = cache;
        string[] args = parentArguments.ToArray();
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i].Equals("/product", StringComparison.OrdinalIgnoreCase) || args[i].Equals("/language", StringComparison.OrdinalIgnoreCase))
            { start.ArgumentList.Add(args[i]); start.ArgumentList.Add(args[++i]); }
        start.ArgumentList.Add("/nologo"); start.ArgumentList.Add("/b"); start.ArgumentList.Add(FilePath("startup.scr"));
        start.Environment[EnvironmentKey] = FilePath("request.json");
        return start;
    }
    public async Task<IsolatedCSharpOutcome> RunAsync(ProcessStartInfo start, CancellationToken ct)
    {
        int? pid = null, exit = null; Process? process = null; bool unknown = false, stillRunning = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (Request.Deadline <= DateTimeOffset.UtcNow) throw new TimeoutException("Isolated job expired before launch");
            using (var claim = new FileStream(FilePath("launch.claim"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                claim.Flush(true);
            start.Environment[EnvironmentKey] = FilePath("request.json");
            process = Process.Start(start) ?? throw new IOException("acad.exe did not start"); pid = process.Id; unknown = true;
            if (pid == Request.ParentProcessId) throw new InvalidDataException("Child PID equals parent PID");
            Write("started.json", new { Request.RunId, Request.OperationId, parent_process_id = Request.ParentProcessId, process_id = pid, at = DateTimeOffset.UtcNow });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Request.Deadline - DateTimeOffset.UtcNow);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Kill only the Process returned by Start, never another AutoCAD or its descendants.
                if (!process.HasExited) process.Kill(entireProcessTree: false);
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false); } catch (OperationCanceledException) { stillRunning = !process.HasExited; }
                if (process.HasExited) exit = process.ExitCode;
                return Outcome(ct.IsCancellationRequested ? "CSHARP_ISOLATED_CANCELLED" : "CSHARP_ISOLATED_TIMEOUT", "Isolated AutoCAD was stopped; the live drawing was not used for script execution. Child/external effects require reconciliation.");
            }
            exit = process.ExitCode;
            if (exit != 0) return Outcome("CSHARP_ISOLATED_PROCESS_EXITED", "Isolated AutoCAD exited abnormally. Inspect exit_code_hex, last_phase and retained artifacts; no automatic retry.");
            var receiptPath = FilePath("result.json");
            if (!File.Exists(receiptPath)) return Outcome("CSHARP_ISOLATED_RECEIPT_MISSING", "AutoCAD exited without a final receipt. Output is unconfirmed; no automatic retry.");
            if (new FileInfo(receiptPath).Length > 512 * 1024) throw new InvalidDataException("Oversized isolated receipt");
            var receipt = JsonSerializer.Deserialize<IsolatedCSharpReceipt>(File.ReadAllText(receiptPath), Wire.Json) ?? throw new InvalidDataException("Empty isolated receipt");
            if (receipt.RunId != Request.RunId || receipt.Nonce != Request.Nonce || receipt.OperationId != Request.OperationId ||
                receipt.ProcessId != pid || receipt.SourceSha256 != Request.SourceSha256 || receipt.InputSha256 != Request.InputSha256)
                throw new InvalidDataException("Isolated receipt identity mismatch");
            if (receipt.State == "failed" && receipt.Error is not null)
                return new(false, Directory, pid, exit, LastPhase(), receipt, receipt.Error, true);
            if (receipt.State != "completed" || receipt.Error is not null || !File.Exists(FilePath("result.dwg")) ||
                new FileInfo(FilePath("result.dwg")).Length == 0 || new FileInfo(FilePath("result.dwg")).Length != receipt.OutputBytes || HashFile(FilePath("result.dwg")) != receipt.OutputSha256)
                throw new InvalidDataException("Isolated output is missing or does not match its receipt");
            return new(true, Directory, pid, exit, LastPhase(), receipt, null, false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (process is not null)
                try { if (!process.HasExited) process.Kill(entireProcessTree: false); stillRunning = !process.HasExited; } catch { stillRunning = true; }
            return Outcome("CSHARP_ISOLATION_FAILED", error.Message);
        }
        finally { process?.Dispose(); }
        IsolatedCSharpOutcome Outcome(string code, string message) => new(false, Directory, pid, exit, LastPhase(), null, new(code, message), unknown, stillRunning);
    }
    private string? LastPhase()
    {
        try { using var json = JsonDocument.Parse(File.ReadAllText(FilePath("phase.json"))); return json.RootElement.Text("phase"); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
