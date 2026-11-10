using System.Text.Json;
using CadMcp.Core;
namespace CadMcp.Tests;
public sealed class BenchmarkTraceTests
{
    [Fact]
    public void Durable_trace_contains_metadata_but_no_code_arguments_or_geometry()
    {
        string run = Guid.NewGuid().ToString("N"), folder = Wire.DataDirectory("benchmarks", run);
        try
        {
            var request = new Request("req", "cad_csharp", "session", "fixture", 1, Wire.Element(new { code = "PRIVATE_CODE", password = "PRIVATE_PASSWORD", geometry = "PRIVATE_GEOMETRY" }), OwnerId: "benchmark-" + run);
            Assert.True(BenchmarkTrace.Record(request, "begin")); Assert.True(BenchmarkTrace.Record(request, "end"));
            string path = Path.Combine(folder, "native-trace.jsonl"), text = File.ReadAllText(path);
            Assert.DoesNotContain("PRIVATE_", text);
            var rows = File.ReadAllLines(path).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToArray();
            Assert.Equal(new[] { "begin", "end" }, rows.Select(r => r.Text("phase"))); Assert.All(rows, r => Assert.Equal("req", r.Text("request_id")));
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.False(BenchmarkTrace.Record(request, "blocked")); // Caller decides whether this is pre-execution or post-commit.
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task Snapshot_query_stays_managed_after_source_document_is_disposed()
    {
        var store = new SnapshotStore(); Snapshot snapshot;
        using (var input = JsonDocument.Parse("[{\"handle\":\"A\",\"layer\":\"TEST\",\"type\":\"Line\",\"access\":\"structured\",\"length\":100}]"))
            snapshot = store.Add("fixture", 1, input.RootElement.EnumerateArray(), false, "model", 10);
        var result = await Task.Run(() => Wire.Element(store.Query(snapshot.Id, "fixture", 1, Wire.Element(new { offset = 0, limit = 1 }))));
        Assert.Equal("A", result.GetProperty("entities")[0].Text("handle")); Assert.False(result.GetProperty("historical").GetBoolean());
        Assert.True(Wire.Element(store.Query(snapshot.Id, "fixture", 2, Wire.Element(new { }))).GetProperty("historical").GetBoolean());
        Assert.Throws<CadFault>(() => store.Query(snapshot.Id, "another-document", 1, Wire.Element(new { })));
    }
}
