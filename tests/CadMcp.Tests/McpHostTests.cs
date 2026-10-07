using System.Collections.Concurrent;
using System.Text.Json;
using CadMcp.Core;
using CadMcp.Host;

namespace CadMcp.Tests;

public sealed class McpHostTests
{
    [Fact]
    public async Task Initialize_list_call_and_image_over_the_real_stdio_SDK()
    {
        string pipe = "cadmcp-mcp-test-" + Guid.NewGuid().ToString("N");
        using var fakeBroker = new PipeServer(pipe, (r, ct) => Task.FromResult(r.Operation == "cad_render"
            ? new Response(r.RequestId, "partial", new { image_base64 = r.Data.Number("width", 0) == 999 ? "not base64!" : TestEnvironment.Png, source = "fixture" })
            : new Response(r.RequestId, "completed", new { fixture = true, operation = r.Operation, payload = r.Data })));
        fakeBroker.Start();
        await using var host = await McpHostProcess.StartAsync(pipe);
        var list = await host.RequestAsync("tools/list", new { });
        var tools = list.GetProperty("tools").EnumerateArray().ToArray();
        var names = tools.Select(t => t.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(38, names.Length);
        foreach (var expected in new[] { "cad_steel_catalog", "cad_search", "cad_result_get", "cad_edit", "cad_export", "cad_publish", "cad_lisp", "cad_operation_status", "cad_render", "cad_image_register",
            "cad_image_point", "cad_vertical_catalog", "cad_vertical_get", "cad_verify", "cad_operation_list", "cad_reference_calibrate", "cad_reference_point", "cad_reference_compare" })
            Assert.Contains(expected, names);
        // The read-only operation list that limits helpers must match the tools' own read-only annotations.
        foreach (var tool in tools)
        {
            string name = tool.GetProperty("name").GetString()!;
            bool annotated = tool.TryGetProperty("annotations", out var annotations) && annotations.TryGetProperty("readOnlyHint", out var hint) && hint.GetBoolean();
            Assert.True(annotated == (CadOperations.ReadOnly.Contains(name) || CadOperations.HostTools.Contains(name)), "Read-only classification differs for " + name);
        }
        var call = await host.CallAsync("cad_sessions", new { });
        Assert.Contains("fixture", call.GetProperty("content")[0].GetProperty("text").GetString());
        var render = await host.CallAsync("cad_render", new { session_id = "s", document_id = "d", expected_revision = 1 });
        Assert.Equal("image", render.GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal(Convert.FromBase64String(TestEnvironment.Png), Convert.FromBase64String(render.GetProperty("content")[1].GetProperty("data").GetString()!));
        Assert.DoesNotContain(TestEnvironment.Png, render.GetProperty("content")[0].GetProperty("text").GetString());
        var broken = await host.CallAsync("cad_render", new { session_id = "s", document_id = "d", expected_revision = 1, width = 999 });
        Assert.True(broken.GetProperty("isError").GetBoolean());
        Assert.Contains("INVALID_RENDER_IMAGE", broken.GetProperty("content")[0].GetProperty("text").GetString());
        var help = await host.CallAsync("cad_edit_help", new { });
        using (var contract = JsonDocument.Parse(help.GetProperty("content")[0].GetProperty("text").GetString()!))
        {
            Assert.True(contract.RootElement.GetProperty("operations").TryGetProperty("dimension_aligned", out _), "Missing edit contract");
            Assert.Contains("operations_json", contract.RootElement.GetProperty("contract").GetString());
        }
        var spds = await host.CallAsync("cad_spds_help", new { });
        using (var contract = JsonDocument.Parse(spds.GetProperty("content")[0].GetProperty("text").GetString()!))
            Assert.True(contract.RootElement.GetProperty("templates").GetArrayLength() > 0 && contract.RootElement.GetProperty("standards").GetArrayLength() == 3);
        var edit = await host.CallAsync("cad_edit", new { session_id = "s", document_id = "d", expected_revision = 1, operation_id = "change1", operations_json = "[{\"op\":\"text\",\"text\":\"Сеть ✓\"}]" });
        using var editBody = JsonDocument.Parse(edit.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal("cad_edit", editBody.RootElement.GetProperty("data").GetProperty("operation").GetString());
        Assert.Equal("change1", editBody.RootElement.GetProperty("data").GetProperty("payload").GetProperty("operation_id").GetString());
        var status = await host.CallAsync("cad_operation_status", new { session_id = "s", document_id = "d", operation_id = "change1" });
        Assert.Contains("cad_operation_status", status.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_project_pin_holds_for_read_edit_render_and_review_helpers(bool helper)
    {
        string pipe = "cadmcp-pin-test-" + Guid.NewGuid().ToString("N"); var requests = new ConcurrentQueue<Request>();
        using var broker = new PipeServer(pipe, (r, ct) => { requests.Enqueue(r); return Task.FromResult(new Response(r.RequestId, "completed", new { document_id = r.DocumentId, owner_id = r.OwnerId })); }); broker.Start();
        var environment = new Dictionary<string, string> { ["CAD_MCP_SESSION_ID"] = "s", ["CAD_MCP_DOCUMENT_ID"] = "d", ["CAD_MCP_OWNER_ID"] = "chat-a" };
        if (helper) environment["CAD_MCP_READ_ONLY"] = "1";
        await using var host = await McpHostProcess.StartAsync(pipe, environment);
        var context = await host.CallAsync("cad_context", new { session_id = "s" });
        Assert.False(context.TryGetProperty("isError", out var er) && er.GetBoolean(), "Pinned context rejected");
        Assert.True(requests.Last().DocumentId == "d" && requests.Last().OwnerId == "chat-a", "Scope/owner not forwarded");
        using (var contextBody = JsonDocument.Parse(context.GetProperty("content")[0].GetProperty("text").GetString()!))
            Assert.Equal(helper, contextBody.RootElement.GetProperty("data").GetProperty("access").GetProperty("read_only").GetBoolean());
        var denied = new List<(string, object)>
        {
            ("cad_context", new { session_id = "s", document_id = "foreign" }),
            ("cad_render", new { session_id = "s", document_id = "foreign", expected_revision = 0 }),
            ("cad_edit", new { session_id = "s", document_id = helper ? "d" : "foreign", expected_revision = 0, operation_id = "pin", operations_json = "[{\"op\":\"line\",\"start\":[0,0],\"end\":[1,1]}]" })
        };
        // Helpers also may not focus, cancel or run anything that is not declared read-only.
        if (helper) denied.AddRange([("cad_focus", new { session_id = "s", document_id = "d", expected_revision = 0, handle = "A" }), ("cad_cancel", new { session_id = "s", document_id = "d" })]);
        foreach (var (name, arguments) in denied)
        {
            int before = requests.Count;
            var reply = await host.CallAsync(name, arguments);
            Assert.True(reply.GetProperty("isError").GetBoolean() && requests.Count == before, "Foreign DWG or helper mutation reached CAD broker: " + name);
        }
    }

    [Fact]
    public async Task Inherited_Codex_connections_enforce_primary_thread_metadata_and_fail_closed()
    {
        using var folder = new TempFolder();
        string pipe = "cad-meta-test-" + Guid.NewGuid().ToString("N"), binding = folder.File("primary.txt"); File.WriteAllText(binding, "primary"); int calls = 0;
        using var broker = new PipeServer(pipe, (r, ct) => { Interlocked.Increment(ref calls); return Task.FromResult(new Response(r.RequestId, "completed", new { ok = true })); }); broker.Start();
        await using var host = await McpHostProcess.StartAsync(pipe, new Dictionary<string, string> { ["CAD_MCP_PRIMARY_THREAD_FILE"] = binding });
        foreach (string? thread in new string?[] { "primary", "child", null })
        {
            var result = await host.CallAsync("cad_edit", new { session_id = "s", document_id = "d", expected_revision = 0, operation_id = "fake", operations_json = "[{\"op\":\"line\",\"start\":[0,0],\"end\":[1,1]}]" },
                thread is null ? null : new { threadId = thread });
            bool failed = result.TryGetProperty("isError", out var er) && er.GetBoolean();
            Assert.True(failed == (thread != "primary"), "MCP caller identity was not enforced");
        }
        Assert.Equal(1, calls);
        File.Delete(binding);
        var blocked = await host.CallAsync("cad_focus", new { session_id = "s", document_id = "d", expected_revision = 0, handle = "A" }, new { threadId = "primary" });
        Assert.True(blocked.GetProperty("isError").GetBoolean() && calls == 1, "Missing primary binding opened write access");
    }

    [Fact]
    public void Preview_copies_are_bounded_by_count_and_age()
    {
        using var folder = new TempFolder();
        for (int i = 0; i < PreviewFiles.MaximumFiles + 10; i++)
        {
            var file = folder.File(i.ToString("D3") + ".png");
            File.WriteAllBytes(file, [1]);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-i));
        }
        var stale = folder.File("stale.png"); File.WriteAllBytes(stale, [1]); File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - PreviewFiles.MaximumAge - TimeSpan.FromHours(1));
        string saved = PreviewFiles.Save(Guid.NewGuid().ToString("N"), Convert.FromBase64String(TestEnvironment.Png), folder.Path);
        var remaining = Directory.GetFiles(folder.Path, "*.png");
        Assert.Equal(PreviewFiles.MaximumFiles, remaining.Length);
        Assert.Contains(saved, remaining);
        Assert.DoesNotContain(stale, remaining);
    }
}
