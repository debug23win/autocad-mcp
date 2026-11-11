using System.Text;
using System.Text.Json;
using CadMcp.AutoCAD;
using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class ConservativeReviewFixTests
{
    [Theory]
    [InlineData("cad_context", "PROJECT_SCOPE_MISMATCH")]
    [InlineData("cad_focus", "HELPER_READ_ONLY")]
    [InlineData("cad_reference_point", "REFERENCE_NOT_FOUND")]
    public async Task Actual_MCP_keeps_expected_host_diagnostics_and_rejects_before_contacting_CAD(string tool, string code)
    {
        var environment = new Dictionary<string, string> { ["CAD_MCP_SESSION_ID"] = "s", ["CAD_MCP_DOCUMENT_ID"] = "d", ["CAD_MCP_READ_ONLY"] = "1" };
        await using var host = await McpHostProcess.StartAsync("no-cad-worker-" + Guid.NewGuid().ToString("N"), environment);
        object arguments = tool switch
        {
            "cad_context" => new { session_id = "s", document_id = "foreign" },
            "cad_focus" => new { session_id = "s", document_id = "d", expected_revision = 1, handle = "A" },
            _ => new { reference_id = "missing", points_json = "[[0,0]]" }
        };
        var reply = await host.CallAsync(tool, arguments);
        Assert.True(reply.GetProperty("isError").GetBoolean());
        using var diagnostic = JsonDocument.Parse(reply.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal(code, diagnostic.RootElement.Text("code"));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.RootElement.Text("message")));
    }

    [Fact]
    public async Task Archived_CSharp_status_excludes_binary_but_preserves_durable_replay_identity()
    {
        using var folder = new TempFolder();
        string session = Guid.NewGuid().ToString("N"), operations = folder.File("operations");
        string directory = Path.Combine(operations, session);
        const string binary = "ARTIFACT";
        var request = new Request("r", "cad_csharp", session, "d", 5,
            Wire.Element(new { operation_id = "op", code = "source", source_sha256 = "hash", assembly_base64 = string.Concat(Enumerable.Repeat(binary, 131072)) }));
        var journal = new OperationJournal(directory);
        journal.Begin("op", request); journal.Complete("op", new Response("r", "completed", new { operation_id = "op" }, session, "d", 6));
        var broker = new Broker(folder.File("workers"), operations);
        var response = await broker.DispatchAsync(request with { Operation = "cad_operation_status", Data = Wire.Element(new { operation_id = "op" }), ExpectedRevision = null }, default);
        var data = Wire.Element(response.Data!); var original = data.GetProperty("original_request");
        Assert.True(data.GetProperty("historical").GetBoolean());
        Assert.False(original.GetProperty("data").TryGetProperty("assembly_base64", out _));
        Assert.Equal("source", original.GetProperty("data").Text("code"));
        Assert.Equal("hash", original.GetProperty("data").Text("source_sha256"));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(response, Wire.Json).Length < 4096);
        var restored = new OperationJournal(directory);
        Assert.True(restored.Find("op")!.Request!.Data.Text("assembly_base64")!.Length > 1000000);
        var replay = JsonSerializer.Deserialize<Request>(original.GetRawText(), Wire.Json)!;
        Assert.Equal("completed", restored.Begin("op", replay)!.Result!.Status);
        Assert.Equal("OPERATION_ID_CONFLICT", Assert.Throws<CadFault>(() => restored.Begin("op", replay with { ExpectedRevision = 6 })).Code);
    }

    [Fact]
    public void Valid_xref_stream_PDF_skips_classic_repair_without_changing_the_file()
    {
        using var folder = new TempFolder(); string path = folder.File("stream.pdf");
        byte[] bytes = File.ReadAllBytes(Path.Combine(TestEnvironment.RepositoryRoot, "tests", "CadMcp.Tests", "Fixtures", "pdf", "xref-stream.pdf"));
        File.WriteAllBytes(path, bytes);
        Assert.Equal("passed", Wire.Element(PdfVerification.Check(path)).Text("state"));
        Assert.False(PdfRepair.NormalizeStructure(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Unsupported_broken_PDF_is_not_accepted_or_modified()
    {
        using var folder = new TempFolder(); string path = folder.File("broken.pdf");
        byte[] bytes = Encoding.ASCII.GetBytes("%PDF-1.5\n" + new string('x', 200) + "\n%%EOF\n");
        File.WriteAllBytes(path, bytes);
        Assert.ThrowsAny<Exception>(() => PdfRepair.NormalizeStructure(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_classic_PDF_repair_preserves_normal_files_and_repairs_duplicate_page_modes(bool duplicateModes)
    {
        using var folder = new TempFolder(); string path = folder.File("classic.pdf");
        using (var stream = File.Create(path))
        {
            void Write(string value) => stream.Write(Encoding.ASCII.GetBytes(value));
            Write("%PDF-1.4\n"); var offsets = new List<long> { 0 };
            string content = "0 0 m 10 10 l S\n";
            string[] objects = ["<< /Type /Catalog /Pages 2 0 R" + (duplicateModes ? " /PageMode /UseNone /PageMode /UseOutlines" : "") + " >>",
                "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << >> /Contents 4 0 R >>",
                "<< /Length " + content.Length + " >>\nstream\n" + content + "endstream"];
            for (int i = 0; i < objects.Length; i++) { offsets.Add(stream.Position); Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
            long xref = stream.Position;
            Write("xref\n0 5\n0000000000 65535 f \n");
            foreach (long offset in offsets.Skip(1)) Write($"{offset:D10} 00000 n \n");
            Write($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        }
        byte[] before = File.ReadAllBytes(path);
        Assert.Equal(duplicateModes, PdfRepair.NormalizeStructure(path));
        Assert.Equal("passed", Wire.Element(PdfVerification.Check(path)).Text("state"));
        Assert.False(PdfRepair.NormalizeStructure(path)); // repairing twice never appends again
        if (!duplicateModes) Assert.Equal(before, File.ReadAllBytes(path));
    }
}
