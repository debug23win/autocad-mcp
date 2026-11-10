using System.Text.Json;
using System.Security.Cryptography;
using CadMcp.Core;
using CadMcp.Host;
using CadMcp.Providers;

namespace CadMcp.Tests;

public sealed class ReviewRegressionTests
{
    private static Request Edit => new("original", "cad_edit", "s", "d", 1,
        Wire.Element(new { operation_id = "original-op", operations_json = "[]" }), OwnerId: "owner");
    private static Response Receipt => new("original", "completed", new { document_state = new { disk_save = "saved" } }, "s", "d", 2);

    [Theory]
    [InlineData("TIMEOUT")]
    [InlineData("INTERNAL_ERROR")]
    [InlineData("WORKER_TRANSPORT_LOST")]
    [InlineData("RESPONSE_TOO_LARGE")]
    public async Task Ambiguous_failure_reads_original_receipt_without_repeating_the_write(string code)
    {
        int writes = 0, polls = 0;
        var result = await MutationRecovery.CallAsync(Edit, (r, _) =>
        {
            if (r.Operation == "cad_edit") { writes++; return Task.FromResult(Response.Fail(r, code, "response lost after acceptance")); }
            polls++; Assert.Equal("owner", r.OwnerId); Assert.Equal("d", r.DocumentId); Assert.Null(r.ExpectedRevision); Assert.Null(r.Deadline);
            Assert.Equal("original-op", r.Data.Text("operation_id"));
            return Task.FromResult(new Response(r.RequestId, "completed", new { state = "completed", result = Receipt }));
        }, default);
        Assert.Equal(1, writes); Assert.Equal(1, polls); Assert.Equal("completed", result.Status); Assert.Equal(2, result.Revision);
    }

    [Fact]
    public async Task Historical_recovery_preserves_provenance_and_never_confirms_current_disk_save()
    {
        var recovered = await MutationRecovery.CallAsync(Edit, (r, _) => r.Operation == "cad_edit"
            ? Task.FromException<Response>(new IOException("lost"))
            : Task.FromResult(new Response(r.RequestId, "completed", new { state = "completed", historical = true, worker_reachable = false, result = Receipt })), default);
        var data = Wire.Element(recovered.Data!);
        Assert.True(data.GetProperty("historical").GetBoolean()); Assert.False(data.GetProperty("worker_reachable").GetBoolean());
        Assert.False(data.GetProperty("current_state_verified").GetBoolean()); Assert.Null(recovered.Revision);
        Assert.Equal("unknown", data.GetProperty("document_state").Text("disk_save"));
        Assert.Equal("saved", data.GetProperty("historical_document_state").Text("disk_save"));
    }

    [Fact]
    public async Task Real_worker_disconnect_after_persisting_receipt_recovers_without_a_second_write()
    {
        using var folder = new TempFolder(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string session = Guid.NewGuid().ToString("N"), pipe = "cadmcp-worker-" + Guid.NewGuid().ToString("N");
        string workers = folder.File("workers"), operations = folder.File("operations"); Directory.CreateDirectory(workers);
        string descriptor = Path.Combine(workers, session + ".json");
        File.WriteAllText(descriptor, JsonSerializer.Serialize(new WorkerDescriptor(session, pipe, Environment.ProcessId, "test"), Wire.Json));
        var journal = new OperationJournal(Path.Combine(operations, session)); int accepted = 0;
        using var worker = new System.IO.Pipes.NamedPipeServerStream(pipe, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        var closing = Task.Run(async () =>
        {
            await worker.WaitForConnectionAsync(timeout.Token);
            var request = JsonSerializer.Deserialize<Request>((await Frames.ReadAsync(worker, timeout.Token))!, Wire.Json)!;
            journal.Begin("original-op", request); accepted++;
            journal.Complete("original-op", Receipt with { SessionId = session });
            File.Delete(descriptor); worker.Disconnect(); // receipt durable, response deliberately never sent
        });
        var broker = new Broker(workers, operations); var replies = new List<Response>();
        var recovered = await MutationRecovery.CallAsync(Edit with { SessionId = session }, async (request, ct) =>
        { var response = await broker.DispatchAsync(request, ct); replies.Add(response); return response; }, timeout.Token);
        await closing;
        Assert.Equal(1, accepted); Assert.Equal("WORKER_TRANSPORT_LOST", replies[0].Error?.Code);
        Assert.Equal("completed", recovered.Status); Assert.Equal(2, replies.Count);
        Assert.True(Wire.Element(recovered.Data!).GetProperty("historical").GetBoolean());
        Assert.Equal("unknown", Wire.Element(recovered.Data!).GetProperty("document_state").Text("disk_save"));
    }

    [Fact]
    public async Task Missing_or_foreign_receipt_keeps_unknown_outcome()
    {
        foreach (var result in new[] { new { state = "not_found", result = (Response?)null }, new { state = "completed", result = (Response?)(Receipt with { DocumentId = "another" }) } })
        {
            var response = await MutationRecovery.CallAsync(Edit, (r, _) => r.Operation == "cad_edit"
                ? Task.FromException<Response>(new IOException("lost"))
                : Task.FromResult(new Response(r.RequestId, "completed", result)), default);
            Assert.Equal("unknown", response.Status); Assert.Equal("OPERATION_UNCERTAIN", response.Error?.Code);
        }
    }

    [Fact]
    public void CSharp_identity_is_source_request_not_recompiled_transport_bytes()
    {
        using var folder = new TempFolder();
        var journal = new OperationJournal(folder.Path);
        Request Script(string code, string bytes) => Edit with { Operation = "cad_csharp", Data = Wire.Element(new { operation_id = "original-op", code, assembly_base64 = bytes, source_sha256 = Portable.Hash(System.Text.Encoding.UTF8.GetBytes(code)) }) };
        journal.Begin("original-op", Script("source", "AAAA")); journal.Complete("original-op", Receipt);
        journal = new OperationJournal(folder.Path);
        Assert.Equal(JsonSerializer.Serialize(Receipt, Wire.Json), JsonSerializer.Serialize(journal.Begin("original-op", Script("source", "BBBB"))!.Result, Wire.Json));
        Assert.Equal("OPERATION_ID_CONFLICT", Assert.Throws<CadFault>(() => journal.Begin("original-op", Script("changed", "BBBB"))).Code);
        Assert.Equal("OPERATION_ID_CONFLICT", Assert.Throws<CadFault>(() => journal.Begin("original-op", Script("source", "BBBB") with { ExpectedRevision = 2 })).Code);
        Assert.Equal("OPERATION_ID_CONFLICT", Assert.Throws<CadFault>(() => journal.Begin("original-op", Script("source", "BBBB") with { DocumentId = "other" })).Code);
    }

    [Fact]
    public void Compilation_is_deterministic_for_the_same_source_and_reference_set()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        const string code = "public static class Script { public static object Run() => 1; }";
        Assert.Equal(CSharpReferenceCompiler.Compile(code, references), CSharpReferenceCompiler.Compile(code, references.Reverse()));
    }

    [Fact]
    public async Task Actual_MCP_CSharp_replay_returns_a_single_accepted_operation()
    {
        string pipe = "review-replay-" + Guid.NewGuid().ToString("N"); int accepted = 0, compilations = 0;
        var journal = new OperationJournal(); var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        using var broker = new PipeServer(pipe, (r, _) =>
        {
            if (r.Operation == "cad_operation_status")
            {
                var entry = journal.Find("original-op");
                return Task.FromResult(new Response(r.RequestId, "completed", new { state = entry?.State ?? "not_found", original_request = OperationJournal.ReplayRequest(entry?.Request), result = entry?.Result }, "s", "d"));
            }
            if (r.Operation == "csharp_references") { compilations++; return Task.FromResult(new Response(r.RequestId, "completed", new { references }, "s", "d")); }
            Assert.Equal("cad_csharp", r.Operation);
            var previous = journal.Begin("original-op", r);
            if (previous is not null) return Task.FromResult(previous.Result! with { RequestId = r.RequestId });
            accepted++;
            return Task.FromResult(journal.Complete("original-op", new Response(r.RequestId, "completed", new { operation_id = "original-op", accepted }, "s", "d", 2)));
        });
        broker.Start(); await using var host = await McpHostProcess.StartAsync(pipe);
        var arguments = new { session_id = "s", document_id = "d", expected_revision = 1, operation_id = "original-op", code = "public static class Script { public static object Run() => 1; }" };
        foreach (int _ in Enumerable.Range(0, 2))
        {
            var reply = await host.CallAsync("cad_csharp", arguments);
            Assert.False(reply.TryGetProperty("isError", out var failed) && failed.GetBoolean());
        }
        Assert.Equal(1, accepted); Assert.Equal(1, compilations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CSharp_historical_replay_needs_no_compiler_and_rejects_changed_source(bool changed)
    {
        string pipe = "review-historical-csharp-" + Guid.NewGuid().ToString("N"); int calls = 0;
        const string code = "no longer compilable with currently available references";
        var original = Edit with { Operation = "cad_csharp", Data = Wire.Element(new { operation_id = "original-op", code, source_sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code))) }) };
        using var broker = new PipeServer(pipe, (r, _) =>
        {
            calls++; Assert.Equal("cad_operation_status", r.Operation);
            return Task.FromResult(new Response(r.RequestId, "completed", new { state = "completed", original_request = original, result = Receipt, historical = true, worker_reachable = false }, "s", "d"));
        });
        broker.Start(); await using var host = await McpHostProcess.StartAsync(pipe);
        var reply = await host.CallAsync("cad_csharp", new { session_id = "s", document_id = "d", expected_revision = 1, operation_id = "original-op", code = changed ? code + ";" : code });
        using var json = JsonDocument.Parse(reply.GetProperty("content")[0].GetProperty("text").GetString()!);
        if (changed) { Assert.Equal("OPERATION_ID_CONFLICT", json.RootElement.Text("code")); Assert.True(json.RootElement.GetProperty("requires_reconciliation").GetBoolean()); }
        else { var data = json.RootElement.GetProperty("data"); Assert.True(data.GetProperty("historical").GetBoolean()); Assert.Equal("unknown", data.GetProperty("document_state").Text("disk_save")); }
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CSharp_unavailable_preflight_receipt_is_unknown_without_compiling_or_claiming_rollback()
    {
        string pipe = "review-csharp-status-" + Guid.NewGuid().ToString("N"); int calls = 0;
        using var broker = new PipeServer(pipe, (r, _) =>
        {
            calls++; Assert.Equal("cad_operation_status", r.Operation);
            return Task.FromResult(Response.Fail(r, "INTERNAL_ERROR", "receipt connection broken"));
        });
        broker.Start(); await using var host = await McpHostProcess.StartAsync(pipe);
        var reply = await host.CallAsync("cad_csharp", new { session_id = "s", document_id = "d", expected_revision = 1, operation_id = "original-op", code = "source" });
        using var json = JsonDocument.Parse(reply.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal("unknown", json.RootElement.Text("status"));
        Assert.Equal("OPERATION_UNCERTAIN", json.RootElement.GetProperty("error").Text("code"));
        Assert.False(json.RootElement.GetProperty("data").GetProperty("mutation_resent").GetBoolean());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Snapshot_stops_materializing_native_style_input_when_the_byte_budget_is_used()
    {
        int visited = 0;
        IEnumerable<JsonElement> Source()
        {
            for (int i = 0; i < 100; i++) { visited++; yield return Wire.Element(new { handle = i.ToString(), text = new string('X', 200000), access = "structured" }); }
        }
        var snapshot = new SnapshotStore().Add("d", 1, Source(), false, "model", 5000);
        Assert.True(snapshot.Truncated); Assert.InRange(visited, 2, 50);
        Assert.True(snapshot.Entities.Sum(ReadPayloadBudget.Bytes) <= ReadPayloadBudget.SnapshotBytes);
    }

    [Fact]
    public void Vertex_pages_are_bounded_and_large_entity_clipping_is_explicit()
    {
        Assert.Equal((0, 2000, (int?)2000), ReadPayloadBudget.VertexPage(1000000, 0, 2000));
        Assert.Equal((999999, 1, (int?)null), ReadPayloadBudget.VertexPage(1000000, 999999, 2000));
        Assert.Throws<CadFault>(() => ReadPayloadBudget.VertexPage(10, 11, 1));
        Assert.Throws<CadFault>(() => ReadPayloadBudget.VertexPage(10, 0, 2001));
        var bounded = ReadPayloadBudget.BoundEntity(Wire.Element(new { handle = "A", vertex_count = 1000000, text = new string('Ж', 100000) }));
        Assert.Equal("partial", bounded.Text("access")); Assert.False(bounded.GetProperty("geometry_complete").GetBoolean());
        Assert.Equal(1000000, bounded.GetProperty("vertex_count").GetInt32());
    }

    [Fact]
    public void Unrelated_broken_table_components_are_not_selected_but_dependencies_are_transitive()
    {
        (string, string)[] edges = [("A", "B"), ("B", "C"), ("D", "missing")];
        Assert.True(TableDependencyScope.Connected(edges, ["a"]).SetEquals(["A", "B", "C"]));
        Assert.True(TableDependencyScope.Connected(edges, ["C"]).SetEquals(["A", "B", "C"]));
        Assert.True(TableDependencyScope.Connected(edges, ["D"]).SetEquals(["D", "missing"]));
    }

    [Theory]
    [InlineData(500, false, false)]
    [InlineData(500, true, true)]
    [InlineData(501, false, true)]
    [InlineData(2000, false, true)]
    public void Verification_refuses_incomplete_or_oversized_whole_operation_targets(int count, bool omitted, bool reject)
    {
        var result = Wire.Element(new { changed_handles = Enumerable.Range(0, count).Select(i => i.ToString("X")).ToArray(), changed_handles_omitted = omitted });
        if (reject) Assert.Equal("VERIFY_TOO_LARGE", Assert.Throws<CadFault>(() => DrawingVerification.OperationHandles(result)).Code);
        else Assert.Equal(count, DrawingVerification.OperationHandles(result).Length);
    }

    [Fact]
    public async Task Unsupported_Map_acceptance_is_rejected_by_real_MCP_before_any_worker_call()
    {
        string pipe = "review-map-" + Guid.NewGuid().ToString("N"); int calls = 0;
        using var broker = new PipeServer(pipe, (r, _) => { calls++; return Task.FromResult(new Response(r.RequestId, "completed")); });
        broker.Start(); await using var host = await McpHostProcess.StartAsync(pipe);
        var reply = await host.CallAsync("cad_edit", new { session_id = "s", document_id = "d", expected_revision = 1, operation_id = "map-test", operations_json = "[{\"op\":\"map_coordinate_system\",\"code\":\"LL84\"}]", expectations_json = "{\"entity_count\":1,\"enforce\":true}" });
        Assert.True(reply.GetProperty("isError").GetBoolean());
        Assert.Contains("UNSUPPORTED_MAP_EXPECTATIONS", reply.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Checkpoint_evidence_detects_removed_and_modified_recovery_files()
    {
        using var folder = new TempFolder(); string path = folder.File("checkpoint.dwg"); File.WriteAllText(path, "test checkpoint");
        var metadata = Wire.Element(new { path, bytes = new FileInfo(path).Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) });
        Assert.Equal("not_checked", Wire.Element(CheckpointEvidence.Inspect(metadata)).Text("hash_state"));
        Assert.Equal("passed", Wire.Element(CheckpointEvidence.Inspect(metadata, true)).Text("hash_state"));
        File.WriteAllText(path, "changed"); Assert.Equal("failed", Wire.Element(CheckpointEvidence.Inspect(metadata, true)).Text("hash_state"));
        File.Delete(path); Assert.False(Wire.Element(CheckpointEvidence.Inspect(metadata, true)).GetProperty("present").GetBoolean());
    }

    [Fact]
    public void Tool_phases_do_not_depend_on_translated_display_text()
    {
        Assert.True(CadToolActivity.ProducesVisibleResult("mcp__cad__cad_csharp", "completed"));
        Assert.False(CadToolActivity.ProducesVisibleResult("cad_csharp", "failed"));
        Assert.False(CadToolActivity.ProducesVisibleResult("cad_context", "completed"));
    }
}
