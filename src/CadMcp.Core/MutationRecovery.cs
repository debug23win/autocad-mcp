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
            if (IsMutation(request.Operation) && response.Error is { } failure &&
                failure.Code is "TIMEOUT" or "WORKER_TRANSPORT_LOST" or "INTERNAL_ERROR" or "RESPONSE_TOO_LARGE")
                throw new IOException(failure.Message);
            return response;
        }
        catch (Exception error) when (IsMutation(request.Operation) && !ct.IsCancellationRequested && Wire.IsTransportFailure(error))
        {
            string id = EditPlan.RequiredText(request.Data, "operation_id");
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var status = await send(request with { RequestId = Guid.NewGuid().ToString("N"), Operation = "cad_operation_status",
                    ExpectedRevision = null, Deadline = null, Data = Wire.Element(new { operation_id = id }) }, probe.Token);
                return Reconcile(request, status);
            }
            catch (Exception e) when (Wire.IsTransportFailure(e))
            {
                return new(request.RequestId, "unknown", new { operation_id = id, response_lost = true, mutation_resent = false,
                    requires_poll = "cad_operation_status", retry = "Do not repeat: acceptance and execution are unknown." },
                    request.SessionId, request.DocumentId, Error: new("OPERATION_UNCERTAIN", "CAD response was lost; operation status could not be read. " + error.Message));
            }
        }
    }
    /// <summary>Project receipt evidence without converting historical execution into current DWG state.</summary>
    public static Response Reconcile(Request request, Response status, bool responseLost = true)
    {
        string id = EditPlan.RequiredText(request.Data, "operation_id");
        var data = Wire.Element(status.Data ?? new { });
        if (data.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidDataException("Malformed operation status payload");
        if (status.Error is null && data.TryGetProperty("result", out var receipt) && receipt.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var result = System.Text.Json.JsonSerializer.Deserialize<Response>(receipt.GetRawText(), Wire.Json);
            if (result is not null)
            {
                if (result.SessionId is { } session && session != request.SessionId || result.DocumentId is { } document && document != request.DocumentId)
                    throw new InvalidDataException("Recovered receipt belongs to a different session or document");
                if (data.TryGetProperty("historical", out var historical) && historical.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    var payload = Wire.Element(result.Data ?? new { });
                    var fields = payload.ValueKind == System.Text.Json.JsonValueKind.Object
                        ? payload.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
                        : new Dictionary<string, object?> { ["historical_result"] = payload };
                    if (fields.Remove("document_state", out var previous)) fields["historical_document_state"] = previous;
                    fields["document_state"] = new { disk_save = "unknown", evidence = "historical_receipt_only", automatic_save = false };
                    fields["historical"] = true; fields["worker_reachable"] = false;
                    fields["current_state_verified"] = false; fields["requires_reconciliation"] = true;
                    return result with { RequestId = request.RequestId, Data = fields, Revision = null };
                }
                return result with { RequestId = request.RequestId };
            }
        }
        string state = data.Text("state") ?? "unknown";
        bool pending = status.Error is null && state is "queued" or "running";
        return new(request.RequestId, pending ? "pending" : "unknown", new { operation_id = id, state,
            response_lost = responseLost, requires_poll = "cad_operation_status", mutation_resent = false,
            retry = "Retain the original operation_id and request. Wait/reconcile; never create a replacement mutation." }, request.SessionId, request.DocumentId,
            Error: pending ? null : new("OPERATION_UNCERTAIN", "No conclusive operation receipt is available."));
    }

}
