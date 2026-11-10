using CadMcp.Core;
using CadMcp.Providers;
namespace CadMcp.Tests;
public sealed class ReferenceIntegrationTests
{
    [Theory]
    [InlineData("cad_sessions")]
    [InlineData("cad_documents")]
    [InlineData("cad_diagnostics")]
    public async Task Actual_benchmark_host_preserves_explicit_document_scope_for_session_tools(string tool)
    {
        string pipe = "benchmark-scope-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var broker = new PipeServer(pipe, (r, ct) =>
        {
            Assert.Equal(tool, r.Operation); Assert.Equal("fixture-session", r.SessionId); Assert.Equal("fixture-document", r.DocumentId);
            Assert.StartsWith("benchmark-", r.OwnerId);
            return Task.FromResult(new Response(r.RequestId, "completed", new { observed = true }, r.SessionId, r.DocumentId));
        });
        broker.Start();
        await using var mcp = await BenchmarkMcpSession.StartAsync(TestEnvironment.HostExecutable, pipe, Guid.NewGuid().ToString("N"), timeout.Token);
        var response = await mcp.CallAsync(new(Guid.NewGuid().ToString("N"), tool, "fixture-session", "fixture-document", Data: Wire.Element(new { })), timeout.Token);
        Assert.Null(response.Error); Assert.Equal("fixture-document", response.DocumentId); Assert.Equal("fixture-session", response.SessionId);
    }
    [Fact]
    public async Task Actual_MCP_host_advertises_readers_and_delivers_a_real_protected_PDF_image()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var mcp = await BenchmarkMcpSession.StartAsync(TestEnvironment.HostExecutable, "reader-only-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), timeout.Token);
        Assert.Contains("cad_pdf_read", mcp.Tools); Assert.Contains("cad_web_download", mcp.Tools); Assert.Contains("cad_csharp", mcp.Tools);
        string path = Path.Combine(TestEnvironment.RepositoryRoot, "tests", "CadMcp.Tests", "Fixtures", "pdf", "copy-protected.pdf");
        var response = await mcp.CallAsync(new(Guid.NewGuid().ToString("N"), "cad_pdf_read", "test-session", "test-document", Data: Wire.Element(new { path, render = true })), timeout.Token);
        Assert.Null(response.Error);
        var data = Wire.Element(response.Data!); Assert.Contains("Restricted drawing", data.Text("text"));
        var image = Assert.Single(data.GetProperty("mcp_images").EnumerateArray());
        Assert.True(image.GetProperty("decoded").GetBoolean()); Assert.True(image.GetProperty("non_uniform").GetBoolean());
        Assert.Single(mcp.Evidence); Assert.Equal("cad_pdf_read", mcp.Evidence[0].Text("tool"));
        File.Delete(data.Text("local_image_path")!);
    }
    [Fact]
    public void PNG_verification_rejects_signature_only_and_detects_uniform_images()
    {
        Assert.False(PngInspection.Read([137, 80, 78, 71, 13, 10, 26, 10]).Decoded);
        var tiny = PngInspection.Read(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg=="));
        Assert.True(tiny.Decoded); Assert.Equal(1, tiny.Width); Assert.Equal(1, tiny.Height); Assert.False(tiny.NonUniform);
    }
}
