using System.Diagnostics;
using System.Text.Json;
using CadMcp.Core;
using CadMcp.Providers;

Console.InputEncoding = new System.Text.UTF8Encoding(false);
Console.OutputEncoding = new System.Text.UTF8Encoding(false);

// Child mode simulates only provider wire protocols; never invokes a real CLI/model.
if (args.Contains("app-server"))
{
    while (Console.ReadLine() is { } line)
    {
        var e = JsonDocument.Parse(line).RootElement;
        var method = e.GetProperty("method").GetString();
        if (method == "initialize") Console.WriteLine("{\"id\":1,\"result\":{}}");
        if (method is "thread/start" or "thread/resume") Console.WriteLine("{\"id\":2,\"result\":{\"thread\":{\"id\":\"test-thread\"}}}");
        if (method == "turn/start")
        {
            var prompt = e.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString();
            if (prompt == "SILENT") { await Task.Delay(TimeSpan.FromMinutes(1)); return; }
            Console.WriteLine("{\"id\":3,\"result\":{}}");
            Console.WriteLine("{\"method\":\"item/agentMessage/delta\",\"params\":{\"delta\":\"Сеть ✓\"}}");
            Console.WriteLine("{\"method\":\"turn/completed\",\"params\":{\"turn\":{\"status\":\"completed\"}}}");
        }
    }
    return;
}
if (args.Contains("-p"))
{
    string prompt = Console.In.ReadToEnd();
    if (prompt == "SILENT") { await Task.Delay(TimeSpan.FromMinutes(1)); return; }
    Console.WriteLine("{\"type\":\"system\",\"session_id\":\"test-claude\"}");
    Console.WriteLine("{\"type\":\"stream_event\",\"event\":{\"delta\":{\"text\":\"Сеть ✓\"}}}");
    Console.WriteLine("{\"type\":\"result\",\"is_error\":false,\"result\":\"Сеть ✓\"}");
    return;
}

int passed = 0;
async Task Test(string name, Func<Task> test)
{
    await test(); Console.WriteLine("PASS " + name); passed++;
}
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
await Test("frame Unicode roundtrip", async () =>
{
    using var stream = new MemoryStream(); var bytes = System.Text.Encoding.UTF8.GetBytes("СПДС — сеть ✓");
    await Frames.WriteAsync(stream, bytes, default); stream.Position = 0;
    Assert((await Frames.ReadAsync(stream, default))!.SequenceEqual(bytes), "Frame corrupt");
});
await Test("reject oversized/truncated frame", async () =>
{
    await Throws<InvalidDataException>(() => Frames.ReadAsync(new MemoryStream(BitConverter.GetBytes(Frames.MaximumBytes + 1)), default));
    await Throws<EndOfStreamException>(() => Frames.ReadAsync(new MemoryStream([3, 0, 0, 0, 1]), default));
    await Throws<EndOfStreamException>(() => Frames.ReadAsync(new MemoryStream([3, 0]), default));
});
await Test("snapshot revision and document isolation", async () =>
{
    var store = new SnapshotStore(); var s = store.Add("d1", 4, [Wire.Element(new { handle = "A", layer = "Сеть", text = "Колодец" })], true);
    await Throws<CadFault>(() => Task.FromResult(store.Query(s.Id, "d2", 4, Wire.Element(new { }))));
    await Throws<CadFault>(() => Task.FromResult(store.Query(s.Id, "d1", 5, Wire.Element(new { }))));
    await Throws<CadFault>(() => Task.FromResult(store.Query(s.Id, "d1", 4, Wire.Element(new { limit = -1 }))));
    var result = Wire.Element(store.Query(s.Id, "d1", 4, Wire.Element(new { text = "колод", limit = 1 })));
    Assert(result.GetProperty("entities").GetArrayLength() == 1, "Unicode filter");
    Assert(result.GetProperty("pagination").GetProperty("snapshot_truncated").GetBoolean(), "Hidden truncation");
});
await Test("stable pagination and bounded snapshot retention", async () =>
{
    var store = new SnapshotStore(); var items = Enumerable.Range(0, 5).Select(i => Wire.Element(new { handle = i.ToString(), layer = "A" }));
    var s = store.Add("d", 1, items, false);
    var page = Wire.Element(store.Query(s.Id, "d", 1, Wire.Element(new { offset = 2, limit = 2 })));
    Assert(page.GetProperty("entities")[0].GetProperty("handle").GetString() == "2", "Wrong offset");
    Assert(page.GetProperty("pagination").GetProperty("next_offset").GetInt32() == 4, "Wrong next cursor");
    for (int i = 0; i < 4; i++) store.Add("d", 1, [], false);
    await Throws<CadFault>(() => Task.FromResult(store.Query(s.Id, "d", 1, Wire.Element(new { }))));
});
await Test("pipe serialization, parallel clients, expired requests", async () =>
{
    string pipe = "cadmcp-test-" + Guid.NewGuid().ToString("N"); int active = 0, peak = 0;
    using var server = new PipeServer(pipe, async (r, ct) =>
    {
        int now = Interlocked.Increment(ref active); peak = Math.Max(peak, now);
        try { await Task.Delay(20, ct); return new(r.RequestId, "completed", "ok"); }
        finally { Interlocked.Decrement(ref active); }
    });
    server.Start();
    var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => PipeClient.CallAsync(pipe, new(i.ToString(), "read"), default)));
    Assert(replies.All(r => r.Status == "completed") && peak == 1, "Requests were not serialized");
    var expired = await PipeClient.CallAsync(pipe, new("expired", "read", Deadline: DateTimeOffset.UtcNow.AddSeconds(-1)), default);
    Assert(expired.Error?.Code == "DEADLINE_EXPIRED", "Expired request ran");
});
await Test("broker rejects missing session", async () =>
{
    var broker = new Broker(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
    Assert(broker.Discover().Count == 0, "Unexpected workers");
    await Throws<CadFault>(() => broker.DispatchAsync(new("r", "cad_snapshot"), default));
});
var options = new ProviderOptions(Environment.ProcessPath!, @"C:\CAD test\host.exe", Environment.CurrentDirectory);
foreach (var name in new[] { "Codex", "Claude" })
{
    IChatProvider Make() => name == "Codex" ? new CodexProvider(options) : new ClaudeProvider(options);
    await Test(name + " stream/session/Unicode", async () =>
    {
        var provider = Make(); var events = new List<ChatEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await foreach (var e in provider.SendAsync("Read", timeout.Token)) events.Add(e);
        Assert(string.Concat(events.Where(e => e.Kind == "text").Select(e => e.Text)) == "Сеть ✓", "Lost/duplicated text");
        Assert(provider.SessionId is not null && events.Any(e => e.Kind == "completed"), "Session missing");
    });
    await Test(name + " cancel silent process", async () =>
    {
        var provider = Make(); using var timeout = new CancellationTokenSource(400); var sw = Stopwatch.StartNew();
        await Throws<OperationCanceledException>(async () => { await foreach (var e in provider.SendAsync("SILENT", timeout.Token)) { } });
        Assert(sw.Elapsed < TimeSpan.FromSeconds(4), "Stop hung waiting for stdout");
    });
}
await Test("provider arguments preserve paths and isolate Claude MCP config", () =>
{
    var a = new ClaudeProvider(options).Arguments();
    var config = JsonDocument.Parse(a[Array.IndexOf(a, "--mcp-config") + 1]);
    Assert(config.RootElement.GetProperty("mcpServers").GetProperty("cad").GetProperty("command").GetString() == options.McpExecutable, "Path escaped incorrectly");
    Assert(a.Contains("--strict-mcp-config"), "Claude config not isolated");
    return Task.CompletedTask;
});
await Test("MCP initialize/list/call/image over actual stdio SDK", async () =>
{
    string pipe = "cadmcp-mcp-test-" + Guid.NewGuid().ToString("N");
    const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6wAAAABJRU5ErkJggg==";
    using var fakeBroker = new PipeServer(pipe, (r, ct) => Task.FromResult(r.Operation == "cad_render"
        ? new Response(r.RequestId, "partial", new { image_base64 = png, source = "fixture" })
        : new Response(r.RequestId, "completed", new { fixture = true })));
    fakeBroker.Start();
    var hostPath = Path.GetFullPath("src/CadMcp.Host/bin/Release/net8.0-windows/CadMcp.Host.exe");
    var info = ProviderProcess.StartInfo(new(hostPath, "unused", Environment.CurrentDirectory), ["--broker-pipe", pipe]);
    using var process = Process.Start(info)!; var stderr = ProviderProcess.DrainErrors(process);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    async Task<JsonElement> Rpc(object body, int id)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(body)); await process.StandardInput.FlushAsync();
        while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            var reply = JsonDocument.Parse(line).RootElement.Clone();
            if (reply.TryGetProperty("id", out var rid) && rid.GetInt32() == id)
            { Assert(!reply.TryGetProperty("error", out _), reply.ToString()); return reply.GetProperty("result").Clone(); }
        }
        throw new Exception("MCP stdout closed");
    }
    try
    {
        await Rpc(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "test", version = "1" } } }, 1);
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"); await process.StandardInput.FlushAsync();
        var list = await Rpc(new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } }, 2);
        var names = list.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        Assert(names.Length == 7 && names.Contains("cad_render") && names.Contains("cad_snapshot"), "Wrong tools");
        var call = await Rpc(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "cad_sessions", arguments = new { } } }, 3);
        Assert(call.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("fixture"), "Tool did not reach broker");
        var render = await Rpc(new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new { name = "cad_render", arguments = new { session_id = "s", document_id = "d", expected_revision = 1 } } }, 4);
        Assert(render.GetProperty("content")[1].GetProperty("type").GetString() == "image", "Image not sent as MCP image");
        Assert(Convert.FromBase64String(render.GetProperty("content")[1].GetProperty("data").GetString()!).SequenceEqual(Convert.FromBase64String(png)), "Image bytes changed");
        Assert(!render.GetProperty("content")[0].GetProperty("text").GetString()!.Contains(png), "Base64 duplicated in text");
    }
    finally { ProviderProcess.Stop(process); await stderr; }
});
Console.WriteLine($"Completed: {passed} tests. No AutoCAD or real provider process started.");
