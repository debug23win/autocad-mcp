using System.Collections.Concurrent;

namespace CadMcp.Core;

/// <summary>Transport disconnection is not cancellation. Only the originating chat can request cancellation.</summary>
public sealed class OperationControl : IDisposable
{
    private sealed record Job(Request Request, CancellationTokenSource Cancellation, DateTimeOffset Accepted)
    { public volatile string Phase = "queued"; }
    private readonly ConcurrentDictionary<string, Job> jobs = new();
    public CancellationToken Accept(string id, Request request)
    {
        var job = new Job(request, new(), DateTimeOffset.UtcNow);
        if (!jobs.TryAdd(id, job)) { job.Cancellation.Dispose(); throw new CadFault("OPERATION_EXISTS", id); }
        return job.Cancellation.Token;
    }
    public void Phase(string id, string phase) { if (jobs.TryGetValue(id, out var job)) job.Phase = phase; }
    /// <summary>Current phase of an accepted, unfinished operation; null once it completed.</summary>
    public string? PhaseOf(string id) => jobs.TryGetValue(id, out var job) ? job.Phase : null;
    public string[] Cancel(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.OwnerId)) throw new CadFault("OWNER_REQUIRED", "Cancellation requires the originating chat owner");
        string? id = request.Data.Text("operation_id");
        var selected = jobs.Where(p => p.Value.Request.OwnerId == request.OwnerId && p.Value.Request.SessionId == request.SessionId
            && p.Value.Request.DocumentId == request.DocumentId && (id is null || p.Key == id)).ToArray();
        foreach (var pair in selected) try { pair.Value.Cancellation.Cancel(); } catch(ObjectDisposedException) { }
        return selected.Select(p => p.Key).ToArray();
    }
    public bool Cancelled(string id)=>jobs.TryGetValue(id,out var job)&&job.Cancellation.IsCancellationRequested;
    public object Snapshot(Request? scope=null) => jobs.Where(p=>scope is null || (p.Value.Request.OwnerId==scope.OwnerId&&p.Value.Request.DocumentId==scope.DocumentId)).Select(p => new { operation_id = p.Key, session_id = p.Value.Request.SessionId,
        document_id = p.Value.Request.DocumentId, phase = p.Value.Phase, accepted_at = p.Value.Accepted,
        cancellation_requested = p.Value.Cancellation.IsCancellationRequested }).ToArray();
    // A token can still be held by an in-flight callback, so a completed source is disposed only
    // after 128 newer operations have completed (oldest first), or on worker shutdown.
    private readonly ConcurrentQueue<CancellationTokenSource> retired = new();
    public void Complete(string id)
    {
        if (!jobs.TryRemove(id, out var job)) return;
        retired.Enqueue(job.Cancellation);
        while (retired.Count > 128 && retired.TryDequeue(out var old)) old.Dispose();
    }
    public void Dispose() { foreach (var j in jobs.Values) j.Cancellation.Cancel(); foreach (var j in jobs.Values) j.Cancellation.Dispose(); foreach (var c in retired) c.Dispose(); }
}
