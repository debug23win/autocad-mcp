using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class LispIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_requires_native_isolation_before_submitting_Lisp(bool isolated)
    {
        string pipe = "lisp-isolation-" + Guid.NewGuid().ToString("N");
        var calls = new List<string>();
        using var broker = new PipeServer(pipe, (r, _) =>
        {
            calls.Add(r.Operation);
            if (r.Operation == "csharp_references")
                return Task.FromResult(new Response(r.RequestId, "completed", new { lisp_execution_mode = isolated ? "isolated_acad" : "in_process" }));
            Assert.Equal("cad_lisp", r.Operation);
            Assert.Equal("(princ)", r.Data.Text("code"));
            return Task.FromResult(new Response(r.RequestId, "queued", new { operation_id = "lisp-test", execution_mode = "isolated_acad" }));
        });
        broker.Start(); await using var host = await McpHostProcess.StartAsync(pipe);
        var reply = await host.CallAsync("cad_lisp", new { session_id = "s", document_id = "d", expected_revision = 1, operation_id = "lisp-test", code = "(princ)" });
        if (isolated)
        {
            Assert.False(reply.TryGetProperty("isError", out var failed) && failed.GetBoolean());
            Assert.Equal(new[] { "csharp_references", "cad_lisp" }, calls);
        }
        else
        {
            Assert.True(reply.GetProperty("isError").GetBoolean());
            Assert.Contains("LISP_ISOLATION_UNAVAILABLE", reply.GetRawText());
            Assert.Equal(new[] { "csharp_references" }, calls);
        }
    }
}
