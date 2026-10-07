namespace CadMcp.Core;

/// <summary>Recover the receipt, never resend a mutation after losing its transport response.</summary>
public static class MutationRecovery
{
    public static bool IsMutation(string operation) => CadOperations.IsMutation(operation);
    public static async Task<Response> CallAsync(Request request, Func<Request, CancellationToken, Task<Response>> send, CancellationToken ct)
    {
        try
        {
            var response = await send(request, ct);
            if (IsMutation(request.Operation) && response.Error?.Code == "TIMEOUT") throw new IOException(response.Error.Message);
            return response;
        }
        catch (Exception error) when (IsMutation(request.Operation) && !ct.IsCancellationRequested && Wire.IsTransportFailure(error))
        {
            string id = EditPlan.RequiredText(request.Data, "operation_id");
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var status = await send(new(Guid.NewGuid().ToString("N"), "cad_operation_status", request.SessionId,
                    request.DocumentId, Data: Wire.Element(new { operation_id = id })), probe.Token);
                var data = Wire.Element(status.Data ?? new { });
                if (data.TryGetProperty("result", out var receipt) && receipt.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    var result = System.Text.Json.JsonSerializer.Deserialize<Response>(receipt.GetRawText(), Wire.Json);
                    if (result is not null) return result with { RequestId = request.RequestId };
                }
                string state = data.Text("state") ?? "unknown";
                bool pending = status.Error is null && state is "queued" or "running";
                return new(request.RequestId, pending ? "pending" : "unknown", new { operation_id = id, state,
                    response_lost = true, requires_poll = "cad_operation_status", mutation_resent = false,
                    retry = "Retain the original operation_id and request. Wait/reconcile; never create a replacement mutation." }, request.SessionId, request.DocumentId,
                    Error: pending ? null : new("OPERATION_UNCERTAIN", "Lost CAD response: no conclusive operation receipt is available."));
            }
            catch (Exception e) when (Wire.IsTransportFailure(e))
            {
                return new(request.RequestId, "unknown", new { operation_id = id, response_lost = true, mutation_resent = false,
                    requires_poll = "cad_operation_status", retry = "Do not repeat: acceptance and execution are unknown." },
                    request.SessionId, request.DocumentId, Error: new("OPERATION_UNCERTAIN", "CAD response was lost; operation status could not be read. " + error.Message));
            }
        }
    }
}
