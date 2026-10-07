using System.Diagnostics;
using System.Text.Json;

namespace CadMcp.Core;

public sealed class Broker(string descriptorRoot, string? receiptRoot = null)
{
    private Response? ArchivedReceipt(Request r)
    {
        if (r.Operation != "cad_operation_status" || !Guid.TryParseExact(r.SessionId, "N", out _)) return null;
        string path = Path.Combine(receiptRoot ?? Wire.DataDirectory("operations"), r.SessionId!);
        if (!Directory.Exists(path)) return null;
        var journal = new OperationJournal(path);
        string id = EditPlan.RequiredText(r.Data, "operation_id"); var entry = journal.Find(id);
        if (entry is null) return null;
        if (entry.DocumentId != r.DocumentId) throw new CadFault("DOCUMENT_MISMATCH", "Archived operation belongs to a different drawing");
        return new(r.RequestId, "completed", new { operation_id = id,
            state = entry.State is "queued" or "running" ? "unknown" : entry.State, result = entry.Result,
            original_request = entry.Request, historical = true, worker_reachable = false,
            retry = "Do not replay in a new worker session. Historical receipt does not prove current geometry or disk save." }, r.SessionId, r.DocumentId);
    }
    public IReadOnlyList<WorkerDescriptor> Discover()
    {
        if (!Directory.Exists(descriptorRoot)) return [];
        var workers = new List<(WorkerDescriptor Worker, DateTime Written)>();
        foreach (var path in Directory.EnumerateFiles(descriptorRoot, "*.json"))
        {
            try
            {
                var written = File.GetLastWriteTimeUtc(path);
                var w = JsonSerializer.Deserialize<WorkerDescriptor>(File.ReadAllText(path), Wire.Json);
                if (w is null || !w.PipeName.StartsWith("cadmcp-worker-", StringComparison.Ordinal)) continue;
                // A crashed CAD process leaves its descriptor behind; it would block session selection forever.
                if (!WorkerProcessAlive(w.ProcessId, written)) { File.Delete(path); continue; }
                workers.Add((w, written));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        // A restarted worker can reuse a session id; the newest descriptor wins.
        return workers.OrderByDescending(p => p.Written).DistinctBy(p => p.Worker.SessionId).Select(p => p.Worker).ToArray();
    }
    internal static bool WorkerProcessAlive(int processId, DateTime descriptorWrittenUtc)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return false; }
        using (process)
        {
            try
            {
                if (process.HasExited) return false;
                // A reused process id belongs to a process started after the descriptor was written.
                return process.StartTime.ToUniversalTime() <= descriptorWrittenUtc.AddSeconds(5);
            }
            // Without access to the process details keep the descriptor; the pipe probe decides.
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return true; }
        }
    }
    public async Task<Response> DispatchAsync(Request request, CancellationToken ct)
    {
        if (request.Operation == "broker_ping") return new(request.RequestId, "completed", new { version = Wire.Version });
        if (request.Operation == "cad_sessions")
        {
            async Task<object> ProbeWorker(WorkerDescriptor w)
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probe.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    var reply = await PipeClient.CallAsync(w.PipeName, request with { Operation = "cad_context", SessionId = w.SessionId }, probe.Token);
                    return new { worker = w, context = reply, reachable = true };
                }
                catch (Exception e) when (Wire.IsTransportFailure(e))
                { return new { worker = w, context = (Response?)null, reachable = false }; }
            }
            var reachable = await Task.WhenAll(Discover().Select(ProbeWorker));
            return new(request.RequestId, "completed", reachable);
        }
        if (string.IsNullOrEmpty(request.SessionId)) throw new CadFault("SESSION_REQUIRED", "Choose a session from cad_sessions");
        var worker = Discover().FirstOrDefault(w => w.SessionId == request.SessionId);
        if (worker is null) return ArchivedReceipt(request) ?? throw new CadFault("SESSION_NOT_FOUND", "CAD session is unavailable");
        try { return await PipeClient.CallAsync(worker.PipeName, request, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested && Wire.IsTransportFailure(e))
        { if (ArchivedReceipt(request) is { } archived) return archived; throw; }
    }
}
