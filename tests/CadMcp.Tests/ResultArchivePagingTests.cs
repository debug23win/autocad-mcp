using System.Text;
using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class ResultArchivePagingTests
{
    [Theory]
    [InlineData(4096)]
    [InlineData(32001)]
    [InlineData(int.MaxValue)]
    public void Real_archive_pages_reconstruct_the_JSON_and_cap_oversized_requests(int requestedLimit)
    {
        using var folder = new TempFolder();
        var archive = new ResultArchive(folder.Path);
        var value = new { text = new string('x', 70000), label = "Отверстия ✓", count = 37 };
        string id = archive.Put("s", "d", 42, value);
        int offset = 0, pages = 0;
        var joined = new StringBuilder();
        while (true)
        {
            var page = Wire.Element(archive.Read(id, "s", "d", offset, requestedLimit));
            string text = page.GetProperty("text").GetString()!;
            Assert.Equal(offset, page.GetProperty("offset").GetInt32());
            Assert.Equal(requestedLimit, page.GetProperty("requested_limit").GetInt32());
            Assert.Equal(Math.Min(requestedLimit, 32000), page.GetProperty("limit").GetInt32());
            Assert.Equal(32000, page.GetProperty("max_limit").GetInt32());
            Assert.Equal(text.Length, page.GetProperty("returned_characters").GetInt32());
            Assert.True(page.GetProperty("historical").GetBoolean());
            Assert.Equal(42, page.GetProperty("captured_revision").GetInt64());
            Assert.InRange(text.Length, 1, 32000);
            joined.Append(text); pages++;
            if (Next(page) is not { } next) break;
            Assert.Equal(offset + text.Length, next);
            Assert.True(next > offset); offset = next;
        }
        Assert.True(pages >= 3);
        Assert.Equal(Wire.Element(value).GetRawText(), joined.ToString());
        using var parsed = JsonDocument.Parse(joined.ToString());
        Assert.Equal(value.text, parsed.RootElement.GetProperty("text").GetString());
        Assert.Equal(value.label, parsed.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    public void Final_and_empty_end_pages_have_no_next_offset_and_past_end_is_invalid()
    {
        using var folder = new TempFolder();
        var archive = new ResultArchive(folder.Path);
        string id = archive.Put("s", "d", 4, new { text = "sample" });
        var whole = Wire.Element(archive.Read(id, "s", "d", 0, int.MaxValue));
        int length = whole.GetProperty("total_characters").GetInt32();
        var last = Wire.Element(archive.Read(id, "s", "d", length - 3, int.MaxValue));
        Assert.Equal(whole.GetProperty("text").GetString()![^3..], last.GetProperty("text").GetString());
        Assert.Null(Next(last));
        var end = Wire.Element(archive.Read(id, "s", "d", length, int.MaxValue));
        Assert.Equal("", end.GetProperty("text").GetString()); Assert.Equal(0, end.GetProperty("returned_characters").GetInt32()); Assert.Null(Next(end));
        Assert.Equal("INVALID_PAGE", Assert.Throws<CadFault>(() => archive.Read(id, "s", "d", length + 1, 16000)).Code);
        Assert.Equal("INVALID_PAGE", Assert.Throws<CadFault>(() => archive.Read(id, "s", "d", int.MaxValue, 16000)).Code);
    }

    [Theory]
    [InlineData(-1, 16000)]
    [InlineData(int.MinValue, 16000)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(0, int.MinValue)]
    public void Negative_offsets_and_nonpositive_limits_remain_invalid(int offset, int limit)
    {
        using var folder = new TempFolder();
        var archive = new ResultArchive(folder.Path);
        string id = archive.Put("s", "d", 1, new { text = "sample" });
        var error = Assert.Throws<CadFault>(() => archive.Read(id, "s", "d", offset, limit));
        Assert.Equal("INVALID_PAGE", error.Code); Assert.Contains("offset >= 0", error.Message); Assert.Contains("limit >= 1", error.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(12)]
    public void Serialized_pages_preserve_literal_Unicode_and_make_progress_for_a_one_unit_limit(int requestedLimit)
    {
        using var folder = new TempFolder();
        var archive = new ResultArchive(folder.Path);
        const string json = "{\"text\":\"ab😀cd𐐷終\"}";
        string id = LiteralUnicodeArchive(folder, archive, json);
        var joined = new StringBuilder(); int offset = 0;
        var strictUtf8 = new UTF8Encoding(false, true);
        while (true)
        {
            // Serialize each response as the pipe/MCP host does, before joining client-side.
            var page = Wire.Element(archive.Read(id, "s", "d", offset, requestedLimit));
            string text = page.GetProperty("text").GetString()!;
            Assert.NotEmpty(strictUtf8.GetBytes(text));
            Assert.InRange(text.Length, 1, Math.Max(2, requestedLimit));
            Assert.Equal(requestedLimit, page.GetProperty("requested_limit").GetInt32());
            Assert.InRange(page.GetProperty("limit").GetInt32(), requestedLimit, Math.Max(2, requestedLimit));
            joined.Append(text);
            if (Next(page) is not { } next) break;
            Assert.Equal(offset + text.Length, next); Assert.True(next > offset); offset = next;
        }
        Assert.Equal(json, joined.ToString());
        using var parsed = JsonDocument.Parse(joined.ToString());
        Assert.Equal("ab😀cd𐐷終", parsed.RootElement.GetProperty("text").GetString());
        int insidePair = json.IndexOf("😀", StringComparison.Ordinal) + 1;
        var error = Assert.Throws<CadFault>(() => archive.Read(id, "s", "d", insidePair, 16000));
        Assert.Equal("INVALID_PAGE", error.Code); Assert.Contains("next_offset", error.Message);
    }

    [Fact]
    public void Unicode_boundary_does_not_exceed_the_hard_page_limit()
    {
        using var folder = new TempFolder();
        var archive = new ResultArchive(folder.Path);
        string json = "{\"text\":\"" + new string('x', 31990) + "😀tail\"}";
        string id = LiteralUnicodeArchive(folder, archive, json);
        var first = Wire.Element(archive.Read(id, "s", "d", 0, int.MaxValue));
        Assert.Equal(31999, first.GetProperty("text").GetString()!.Length);
        Assert.Equal(31999, Next(first)); Assert.Equal(32000, first.GetProperty("limit").GetInt32());
        var last = Wire.Element(archive.Read(id, "s", "d", Next(first)!.Value, int.MaxValue));
        Assert.StartsWith("😀", last.GetProperty("text").GetString()); Assert.Null(Next(last));
        Assert.Equal(json, first.GetProperty("text").GetString() + last.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Actual_MCP_schema_describes_paging_and_the_tool_preserves_defaults_and_caps_large_pages()
    {
        using var folder = new TempFolder();
        var archive = new ResultArchive(folder.Path);
        var value = new { text = new string('x', 70000) };
        string id = archive.Put("s", "d", 7, value), pipe = "archive-pages-" + Guid.NewGuid().ToString("N");
        var received = new List<Request>();
        using var broker = new PipeServer(pipe, (request, _) =>
        {
            Assert.Equal("cad_result_get", request.Operation); received.Add(request);
            try { return Task.FromResult(new Response(request.RequestId, "completed", archive.Read(request.Data.Text("archive_id")!, request.SessionId!, request.DocumentId!, request.Data.Number("offset", 0), request.Data.Number("limit", 16000)), "s", "d")); }
            catch (CadFault error) { return Task.FromResult(Response.Fail(request, error.Code, error.Message)); }
        });
        broker.Start(); await using var host = await McpHostProcess.StartAsync(pipe);
        var tools = await host.RequestAsync("tools/list", new { });
        var tool = tools.GetProperty("tools").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "cad_result_get");
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        var properties = tool.GetProperty("inputSchema").GetProperty("properties");
        Assert.Contains(">= 0", properties.GetProperty("offset").GetProperty("description").GetString());
        Assert.Contains("next_offset", properties.GetProperty("offset").GetProperty("description").GetString());
        Assert.Contains("16000", properties.GetProperty("limit").GetProperty("description").GetString());
        Assert.Contains("32000", properties.GetProperty("limit").GetProperty("description").GetString());

        var defaults = await host.CallAsync("cad_result_get", new { session_id = "s", document_id = "d", expected_revision = 7, archive_id = id });
        Assert.False(defaults.TryGetProperty("isError", out var failed) && failed.GetBoolean());
        Assert.Equal(0, received[^1].Data.Number("offset", -1)); Assert.Equal(16000, received[^1].Data.Number("limit", -1));
        var joined = new StringBuilder(); int offset = 0;
        while (true)
        {
            var result = await host.CallAsync("cad_result_get", new { session_id = "s", document_id = "d", expected_revision = 7, archive_id = id, offset, limit = int.MaxValue });
            Assert.False(result.TryGetProperty("isError", out failed) && failed.GetBoolean());
            using var body = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
            var page = body.RootElement.GetProperty("data");
            Assert.Equal(32000, page.GetProperty("limit").GetInt32()); Assert.Equal(int.MaxValue, page.GetProperty("requested_limit").GetInt32());
            joined.Append(page.GetProperty("text").GetString());
            if (Next(page) is not { } next) break;
            Assert.True(next > offset); offset = next;
        }
        Assert.Equal(Wire.Element(value).GetRawText(), joined.ToString());
        var invalid = await host.CallAsync("cad_result_get", new { session_id = "s", document_id = "d", expected_revision = 7, archive_id = id, offset = -1 });
        Assert.True(invalid.GetProperty("isError").GetBoolean()); Assert.Contains("INVALID_PAGE", invalid.GetRawText());
    }

    private static int? Next(JsonElement page) => page.TryGetProperty("next_offset", out var next) && next.ValueKind == JsonValueKind.Number ? next.GetInt32() : null;
    private static string LiteralUnicodeArchive(TempFolder folder, ResultArchive archive, string json)
    {
        string id = archive.Put("s", "d", 7, new { });
        // Older/other valid archive writers can keep literal non-BMP characters instead of JSON escapes.
        File.WriteAllText(folder.File(id + ".json"), "{\"session\":\"s\",\"document\":\"d\",\"revision\":7,\"value\":" + json + "}", new UTF8Encoding(false));
        return id;
    }
}
