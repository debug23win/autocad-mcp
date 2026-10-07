using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class TransportTests
{
    [Fact]
    public async Task Frame_roundtrip_keeps_unicode()
    {
        using var stream = new MemoryStream(); var bytes = System.Text.Encoding.UTF8.GetBytes("СПДС — сеть ✓");
        await Frames.WriteAsync(stream, bytes, default); stream.Position = 0;
        Assert.Equal(bytes, await Frames.ReadAsync(stream, default));
    }

    [Fact]
    public async Task Oversized_and_truncated_frames_are_rejected()
    {
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Frames.ReadAsync(new MemoryStream(BitConverter.GetBytes(Frames.MaximumBytes + 1)), default));
        await Assert.ThrowsAnyAsync<EndOfStreamException>(() => Frames.ReadAsync(new MemoryStream([3, 0, 0, 0, 1]), default));
        await Assert.ThrowsAnyAsync<EndOfStreamException>(() => Frames.ReadAsync(new MemoryStream([3, 0]), default));
    }

    [Fact]
    public async Task Pipe_stays_responsive_during_a_long_request_and_rejects_expired_requests()
    {
        string pipe = "cadmcp-test-" + Guid.NewGuid().ToString("N");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new PipeServer(pipe, async (r, ct) =>
        {
            if (r.Operation == "slow") { started.SetResult(); await release.Task.WaitAsync(ct); }
            return new(r.RequestId, "completed", "ok");
        });
        server.Start();
        var slow = PipeClient.CallAsync(pipe, new("slow", "slow"), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var quickTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var quick = await PipeClient.CallAsync(pipe, new("quick", "broker_ping"), quickTimeout.Token);
        Assert.Equal("completed", quick.Status);
        release.SetResult();
        Assert.Equal("completed", (await slow).Status);
        var expired = await PipeClient.CallAsync(pipe, new("expired", "read", Deadline: DateTimeOffset.UtcNow.AddSeconds(-1)), default);
        Assert.Equal("DEADLINE_EXPIRED", expired.Error?.Code);
    }

    [Fact]
    public async Task Stopping_a_server_lets_requests_in_progress_finish_and_refuses_new_ones()
    {
        string pipe = "cadmcp-drain-" + Guid.NewGuid().ToString("N");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new PipeServer(pipe, async (r, ct) =>
        {
            if (r.Operation == "slow") { started.SetResult(); await Task.Delay(500, ct); }
            return new(r.RequestId, "completed", "done");
        });
        server.Start();
        var slow = PipeClient.CallAsync(pipe, new("slow", "slow"), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = server.StopAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("completed", (await slow).Status);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(server.Completion.IsCompleted);
        using var refused = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PipeClient.CallAsync(pipe, new("late", "broker_ping"), refused.Token));
    }

    [Fact]
    public async Task Malformed_replies_surface_as_transport_failures()
    {
        string pipe = "cadmcp-mismatch-" + Guid.NewGuid().ToString("N");
        using var server = new PipeServer(pipe, (r, ct) => Task.FromResult(new Response("another-request", "completed")));
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAnyAsync<IOException>(() => PipeClient.CallAsync(pipe, new("mine", "broker_ping"), timeout.Token));
        Assert.IsType<InvalidDataException>(error.InnerException);
    }

    [Fact]
    public void Numbers_must_be_integers()
    {
        var data = Wire.Element(new { limit = "50", width = 512, empty = (int?)null });
        Assert.Equal("INVALID_NUMBER", Assert.Throws<CadFault>(() => data.Number("limit", 10)).Code);
        Assert.Equal(512, data.Number("width", 1));
        Assert.Equal(7, data.Number("missing", 7));
        Assert.Equal(7, Wire.Element(new { }).Number("empty", 7));
    }

    [Fact]
    public void Version_has_no_source_revision()
    {
        Assert.DoesNotContain('+', Wire.Version);
        Assert.Matches(@"^\d+\.\d+\.\d+", Wire.Version);
    }
}

public sealed class OperationTests
{
    [Fact]
    public void Every_tool_is_classified_once_and_mutations_are_never_read_only()
    {
        foreach (var operation in new[] { "cad_edit", "cad_lisp", "cad_export", "cad_publish", "cad_focus", "cad_cancel" })
            Assert.DoesNotContain(operation, CadOperations.ReadOnly);
        Assert.Empty(CadOperations.ReadOnly.Intersect(CadOperations.HostTools));
        Assert.Contains("cad_focus", CadOperations.WorkerOperations);
        Assert.DoesNotContain("cad_sessions", CadOperations.WorkerOperations);
        Assert.True(CadOperations.ActivatesDocument("cad_edit") && !CadOperations.ActivatesDocument("cad_vertical_get") && !CadOperations.ActivatesDocument("cad_search"));
    }

    [Fact]
    public void Cancellation_is_isolated_between_chats_and_documents()
    {
        using var control = new OperationControl(); var data = Wire.Element(new { });
        var a = new Request("a", "cad_edit", "s", "d1", Data: data, OwnerId: "chat1"); var b = a with { RequestId = "b", OwnerId = "chat2" }; var c = a with { RequestId = "c", DocumentId = "d2" };
        var ta = control.Accept("a", a); var tb = control.Accept("b", b); var tc = control.Accept("c", c);
        var stopped = control.Cancel(a with { Operation = "cad_cancel" });
        Assert.Equal(new[] { "a" }, stopped);
        Assert.True(ta.IsCancellationRequested); Assert.False(tb.IsCancellationRequested); Assert.False(tc.IsCancellationRequested);
        control.Complete("a");
        Assert.DoesNotContain(Wire.Element(control.Snapshot()).EnumerateArray(), x => x.Text("operation_id") == "a");
    }

    [Fact]
    public void Completing_many_operations_never_disposes_a_recent_token()
    {
        using var control = new OperationControl(); var request = new Request("r", "cad_edit", "s", "d", Data: Wire.Element(new { }), OwnerId: "o");
        var tokens = new List<CancellationToken>();
        for (int i = 0; i < 300; i++) { tokens.Add(control.Accept("op" + i, request)); control.Complete("op" + i); }
        // WaitHandle throws once the source is disposed. Recent sources stay usable; old ones are released.
        foreach (var token in tokens.TakeLast(128)) _ = token.WaitHandle;
        Assert.Throws<ObjectDisposedException>(() => tokens[0].WaitHandle);
    }
}

public sealed class LispTests
{
    [Fact]
    public void Wrapper_is_balanced_isolates_variables_and_prevents_id_injection()
    {
        var script = LispScript.Wrap("operation_123"); int depth = 0; bool quoted = false, escaped = false;
        foreach (char c in script)
        {
            if (quoted) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; continue; }
            if (c == '"') quoted = true; else if (c == '(') depth++; else if (c == ')') depth--;
            Assert.True(depth >= 0, "LISP closes an unopened expression");
        }
        Assert.True(depth == 0 && !quoted && script.Contains("cadmcpbegin") && script.Contains("cadmcpfinish") && script.Contains("/ code value"), "Broken LISP evaluator");
        Assert.ThrowsAny<ArgumentException>(() => LispScript.Wrap("x\") (erase)"));
    }

    [Theory]
    [InlineData("(command-s \"_.LINE\" '(0 0) '(1 1) \"\")")]
    [InlineData("(setq a \"text with ) and ( inside\") (princ a)")]
    [InlineData("; a comment with )\n(princ 1)")]
    [InlineData(";| block ) comment |; (princ \"\\\"quoted\\\"\")")]
    public void Balanced_code_is_accepted(string code) => LispScript.ValidateBody(code);

    [Theory]
    [InlineData("(foo))(bar")]
    [InlineData("(foo")]
    [InlineData("(princ \"unterminated)")]
    [InlineData(";| open block comment (princ 1)")]
    public void Code_that_would_be_partly_dropped_is_rejected(string code) =>
        Assert.Equal("LISP_UNBALANCED", Assert.Throws<CadFault>(() => LispScript.ValidateBody(code)).Code);
}
