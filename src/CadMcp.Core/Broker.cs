using System.Text.Json;

namespace CadMcp.Core;

public sealed class Broker(string descriptorRoot)
{
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
        if (request.Operation == "broker_ping") return new(request.RequestId, "completed", new { version = "0.3.2-preview" });
        if (request.Operation == "cad_sessions")
        {
            var reachable = new List<object>();
            foreach (var w in Discover())
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probe.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    var reply = await PipeClient.CallAsync(w.PipeName, request with { Operation = "cad_context", SessionId = w.SessionId }, probe.Token);
                    reachable.Add(new { worker = w, context = reply, reachable = true });
                }
                catch (Exception e) when (e is IOException or OperationCanceledException) { reachable.Add(new { worker = w, reachable = false }); }
            }
            return new(request.RequestId, "completed", reachable);
        }
        if (string.IsNullOrEmpty(request.SessionId)) throw new CadFault("SESSION_REQUIRED", "Choose a session from cad_sessions");
        var worker = Discover().SingleOrDefault(w => w.SessionId == request.SessionId)
            ?? throw new CadFault("SESSION_NOT_FOUND", "CAD session is unavailable");
        return await PipeClient.CallAsync(worker.PipeName, request, ct);
    }
}
