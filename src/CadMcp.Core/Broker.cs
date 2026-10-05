using System.Text.Json;

namespace CadMcp.Core;

public sealed class Broker(string descriptorRoot, string? receiptRoot = null)
{
    private Response? ArchivedReceipt(Request r)
    {
        if (r.Operation != "cad_operation_status" || !Guid.TryParseExact(r.SessionId, "N", out _)) return null;
        string path = Path.Combine(receiptRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CadMcp", "operations"), r.SessionId!);
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
        var workers = new List<WorkerDescriptor>();
        foreach (var path in Directory.EnumerateFiles(descriptorRoot, "*.json"))
        {
            try
            {
                var w = JsonSerializer.Deserialize<WorkerDescriptor>(File.ReadAllText(path), Wire.Json);
                if (w is not null && w.PipeName.StartsWith("cadmcp-worker-", StringComparison.Ordinal)) workers.Add(w);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return workers;
    }
    public async Task<Response> DispatchAsync(Request request, CancellationToken ct)
    {
        if (request.Operation == "broker_ping") return new(request.RequestId, "completed", new { version = "0.10.1-preview" });
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
                catch (Exception e) when (e is IOException or OperationCanceledException)
                { return new { worker = w, context = (Response?)null, reachable = false }; }
            }
            var reachable = await Task.WhenAll(Discover().Select(ProbeWorker));
            return new(request.RequestId, "completed", reachable);
        }
        if (string.IsNullOrEmpty(request.SessionId)) throw new CadFault("SESSION_REQUIRED", "Choose a session from cad_sessions");
        var worker = Discover().SingleOrDefault(w => w.SessionId == request.SessionId);
        if (worker is null) return ArchivedReceipt(request) ?? throw new CadFault("SESSION_NOT_FOUND", "CAD session is unavailable");
        try { return await PipeClient.CallAsync(worker.PipeName, request, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is IOException or OperationCanceledException)
        { if (ArchivedReceipt(request) is { } archived) return archived; throw; }
    }
}
