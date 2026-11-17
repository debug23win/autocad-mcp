using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using CadMcp.Core;
using CadMcp.Providers;

namespace CadMcp.Tests;

public sealed class PerplexityProviderTests
{
    private const string Key = "pplx-test-secret-do-not-log";
    private static ProviderOptions Options => new("", "unused-host", Path.GetTempPath(), MaxSubagents: 0, OwnerId: "owner-a", CadSessionId: "session-a", CadDocumentId: "document-a");
    private static JsonElement Message(string text) => Wire.Element(new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } });
    private static JsonElement Call(string name = "cad_context", string id = "call-1", string arguments = "{}") =>
        Wire.Element(new { type = "function_call", name, call_id = id, arguments, thought_signature = "keep-the-signature" });
    private static JsonElement Complete(params JsonElement[] output) => Wire.Element(new
    {
        id = "response-test", status = "completed", output, usage = new { input_tokens = 10, output_tokens = 5, total_tokens = 15 }
    });
    private static HttpResponseMessage Json(JsonElement value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value.GetRawText(), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(params JsonElement[] events) => new(HttpStatusCode.OK)
    { Content = new StringContent(string.Concat(events.Select(item => "data: " + item.GetRawText() + "\r\n\r\n")) + "data: [DONE]\r\n\r\n", Encoding.UTF8, "text/event-stream") };
    private static JsonElement Terminal(JsonElement response) => Wire.Element(new { type = "response.completed", response });

    private sealed class FakeMcp : IPerplexityMcpSession
    {
        public List<string> Calls { get; } = [];
        public Func<string, JsonElement, CancellationToken, Task<JsonElement>>? Handler { get; init; }
        public IReadOnlyList<JsonElement> Tools { get; } = new[] { "cad_context", "cad_edit", "cad_render" }.Select(name => JsonSerializer.SerializeToElement(new
        { name, description = "fixture " + name, inputSchema = new { type = "object", properties = new { } } })).ToArray();
        public string Instructions => "Server instructions: verify the DWG after editing.";
        public Task<JsonElement> CallAsync(string name, JsonElement arguments, CancellationToken ct)
        {
            Calls.Add(name);
            return Handler?.Invoke(name, arguments, ct) ?? Task.FromResult(JsonSerializer.SerializeToElement(new { content = new[] { new { type = "text", text = "{\"ok\":true}" } }, isError = false }));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HttpFixture(Func<int, JsonElement, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        public List<string> Paths { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(Key, request.Headers.Authorization?.Parameter);
            Assert.Equal("https", request.RequestUri?.Scheme);
            Assert.Equal("api.perplexity.ai", request.RequestUri?.Host);
            var body = request.Content is null ? Wire.Element(new { }) : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement.Clone();
            Requests.Add(body); Paths.Add(request.RequestUri!.AbsolutePath);
            Assert.DoesNotContain(Key, body.GetRawText());
            return await handler(Requests.Count, body, ct);
        }
    }

    private static PerplexityProvider Provider(TempFolder root, HttpClient client, FakeMcp mcp, ProviderOptions? options = null) =>
        new(options ?? Options, Key, client, (_, _) => Task.FromResult<IPerplexityMcpSession>(mcp), root.Path);

    [Fact]
    public async Task Local_tool_loop_preserves_schemas_images_sources_signatures_and_cumulative_usage()
    {
        using var root = new TempFolder();
        var mcp = new FakeMcp
        {
            Handler = (name, _, _) => Task.FromResult(name == "cad_render"
                ? JsonSerializer.SerializeToElement(new { content = new object[] { new { type = "text", text = "Viewport" }, new { type = "image", mimeType = "image/png", data = TestEnvironment.Png } } })
                : Wire.Element(new { content = new[] { new { type = "text", text = "Drawing context" } } }))
        };
        using var http = new HttpFixture((number, body, token) =>
        {
            Assert.False(body.TryGetProperty("effort", out _));
            Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Contains("Server instructions", body.GetProperty("instructions").GetString());
            Assert.Equal(4, body.GetProperty("tools").GetArrayLength());
            Assert.Equal("web_search", body.GetProperty("tools")[0].GetProperty("type").GetString());
            if (number == 1) return Task.FromResult(Json(Complete(Call(), Call("cad_render", "call-2"))));
            var input = body.GetProperty("input").EnumerateArray().ToArray();
            Assert.Equal("keep-the-signature", input.First(i => i.Text("type") == "function_call").GetProperty("thought_signature").GetString());
            var image = input.Single(i => i.Text("type") == "function_call_output" && i.Text("call_id") == "call-2");
            Assert.Equal("cad_render", image.GetProperty("name").GetString());
            Assert.Contains(image.GetProperty("output").EnumerateArray(), i => i.Text("type") == "input_image" && i.Text("image_url") == "data:image/png;base64," + TestEnvironment.Png);
            return Task.FromResult(Sse(
                Wire.Element(new { type = "response.reasoning.search_results", results = new[] { new { id = 1, url = "https://example.com/one", title = "Source one" } } }),
                Wire.Element(new { type = "response.reasoning.search_results", results = new[] { new { id = 2, url = "https://example.com/two", title = "Source two" } } }),
                Wire.Element(new { type = "response.output_text.delta", delta = "Готово [1][web:2]" }),
                Terminal(Complete(Message("Готово [1][web:2]")))));
        });
        using var client = new HttpClient(http);
        var provider = Provider(root, client, mcp, Options with { Model = "openai/test-model", ReasoningEffort = "high" });
        var events = await TestEnvironment.Collect(provider.SendAsync("Inspect", default));
        Assert.Equal(new[] { "cad_context", "cad_render" }, mcp.Calls);
        Assert.Contains(events, e => e.Kind == "step" && e.Tool == "cad_render" && e.Phase == "completed");
        Assert.Equal(30, events.Last(e => e.Kind == "usage").Usage?.Total);
        Assert.Equal(20, events.Last(e => e.Kind == "usage").Usage?.Input);
        Assert.Equal(10, events.Last(e => e.Kind == "usage").Usage?.Output);
        string answer = string.Concat(events.Where(e => e.Kind == "text").Select(e => e.Text));
        Assert.Equal(1, answer.Split("Готово", StringSplitOptions.None).Length - 1);
        Assert.Contains("https://example.com/one", answer); Assert.Contains("https://example.com/two", answer);
        Assert.Contains(events, e => e.Kind == "completed");
        Assert.All(http.Paths, path => Assert.Equal("/v1/agent", path));
        Assert.DoesNotContain(Key, File.ReadAllText(root.File(provider.SessionId + ".json")));
    }

    [Theory]
    [InlineData("response.failed")]
    [InlineData("response.incomplete")]
    [InlineData("response.cancelled")]
    public async Task Incomplete_stream_never_executes_an_advertised_call(string terminalType)
    {
        using var root = new TempFolder(); var mcp = new FakeMcp();
        using var http = new HttpFixture((_, _, _) => Task.FromResult(Sse(
            Wire.Element(new { type = "response.output_item.done", item = Call("cad_edit") }),
            Wire.Element(new { type = terminalType, response = new { error = new { message = Key } } }))));
        using var client = new HttpClient(http);
        var error = await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(Provider(root, client, mcp).SendAsync("edit", default)));
        Assert.Empty(mcp.Calls); Assert.DoesNotContain(Key, error.ToString()); Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Truncated_stream_is_not_success_and_does_not_execute_tools()
    {
        using var root = new TempFolder(); var mcp = new FakeMcp();
        using var http = new HttpFixture((_, _, _) => Task.FromResult(Sse(Wire.Element(new { type = "response.output_item.done", item = Call("cad_edit") }))));
        using var client = new HttpClient(http);
        await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(Provider(root, client, mcp).SendAsync("edit", default)));
        Assert.Empty(mcp.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Http_errors_do_not_retry_or_disclose_the_response_body(int status)
    {
        using var root = new TempFolder(); var mcp = new FakeMcp();
        using var http = new HttpFixture((_, _, _) => Task.FromResult(Json(Wire.Element(new { error = Key }), (HttpStatusCode)status)));
        using var client = new HttpClient(http);
        var error = await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(Provider(root, client, mcp).SendAsync("test", default)));
        Assert.Contains(status.ToString(), error.Message); Assert.DoesNotContain(Key, error.ToString()); Assert.Single(http.Requests); Assert.Empty(mcp.Calls);
    }

    [Fact]
    public async Task Reused_call_id_does_not_repeat_a_mutation()
    {
        using var root = new TempFolder(); var mcp = new FakeMcp();
        using var http = new HttpFixture((_, _, _) => Task.FromResult(Json(Complete(Call("cad_edit")))));
        using var client = new HttpClient(http);
        await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(Provider(root, client, mcp).SendAsync("edit", default)));
        Assert.Equal(new[] { "cad_edit" }, mcp.Calls); Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task New_adapter_resumes_local_context_after_model_change_without_reexecuting_old_tools()
    {
        using var root = new TempFolder(); var mcp = new FakeMcp();
        using var http = new HttpFixture((number, body, _) =>
        {
            if (number == 3)
            {
                Assert.Equal("anthropic/test-model", body.GetProperty("model").GetString());
                Assert.Contains("First answer", body.GetProperty("input").GetRawText());
                Assert.Contains("function_call_output", body.GetProperty("input").GetRawText());
            }
            return Task.FromResult(Json(number == 1 ? Complete(Call()) : Complete(Message(number == 2 ? "First answer" : "Second answer"))));
        });
        using var client = new HttpClient(http);
        var first = Provider(root, client, mcp);
        await TestEnvironment.Collect(first.SendAsync("First question", default));
        var second = Provider(root, client, mcp, Options with { Model = "anthropic/test-model" }) ; second.SessionId = first.SessionId;
        await TestEnvironment.Collect(second.SendAsync("Follow-up", default));
        Assert.Equal(first.SessionId, second.SessionId); Assert.Single(mcp.Calls);
    }

    [Fact]
    public async Task Interrupted_tool_is_recorded_as_unknown_on_resume_and_never_replayed()
    {
        using var root = new TempFolder();
        var broken = new FakeMcp { Handler = (_, _, _) => throw new IOException("Transport ended after submission") };
        using var http = new HttpFixture((number, body, _) =>
        {
            if (number == 2) Assert.Contains("CAD_CALL_OUTCOME_UNKNOWN", body.GetProperty("input").GetRawText());
            return Task.FromResult(Json(number == 1 ? Complete(Call("cad_edit")) : Complete(Message("Inspect the receipt first"))));
        });
        using var client = new HttpClient(http);
        var first = Provider(root, client, broken);
        await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(first.SendAsync("edit", default)));
        var mcp = new FakeMcp(); var second = Provider(root, client, mcp); second.SessionId = first.SessionId;
        var events = await TestEnvironment.Collect(second.SendAsync("Continue", default));
        Assert.Single(broken.Calls); Assert.Empty(mcp.Calls); Assert.Contains(events, e => e.Kind == "problem");
    }

    [Fact]
    public async Task Queued_addition_is_consumed_before_the_turn_completes()
    {
        using var root = new TempFolder(); var mcp = new FakeMcp(); PerplexityProvider? provider = null;
        using var http = new HttpFixture(async (number, body, ct) =>
        {
            if (number == 1)
            {
                var receipt = await provider!.SteerAsync(new("Also inspect layers", []), ct);
                Assert.Equal("queued", receipt.State);
            }
            else Assert.Contains("Also inspect layers", body.GetProperty("input").GetRawText());
            return Json(Complete(Message(number == 1 ? "First" : "Both requests answered")));
        });
        using var client = new HttpClient(http); provider = Provider(root, client, mcp);
        var events = await TestEnvironment.Collect(provider.SendAsync("Inspect", default));
        Assert.Equal(2, http.Requests.Count); Assert.Single(events, e => e.Kind == "completed");
        Assert.Equal("unavailable", (await provider.SteerAsync(new("Too late", []), default)).State);
    }

    [Fact]
    public async Task Missing_key_fails_before_starting_mcp_or_sending_http()
    {
        using var root = new TempFolder(); int starts = 0;
        using var client = new HttpClient(new HttpFixture((_, _, _) => throw new InvalidOperationException("Must not run")));
        var provider = new PerplexityProvider(Options, "", client, (_, _) => { starts++; return Task.FromResult<IPerplexityMcpSession>(new FakeMcp()); }, root.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => TestEnvironment.Collect(provider.SendAsync("hello", default)));
        Assert.Equal(0, starts); Assert.Empty(Directory.GetFiles(root.Path));
    }

    [Fact]
    public async Task Timeout_and_user_cancellation_stop_without_retries_or_tool_calls()
    {
        foreach (bool cancelByUser in new[] { false, true })
        {
            using var root = new TempFolder(); var mcp = new FakeMcp();
            using var user = new CancellationTokenSource();
            using var http = new HttpFixture(async (_, _, ct) => { if (cancelByUser) user.Cancel(); await Task.Delay(5000, ct); return Json(Complete(Message("late"))); });
            using var client = new HttpClient(http);
            var provider = Provider(root, client, mcp); provider.RequestTimeout = TimeSpan.FromMilliseconds(100);
            if (cancelByUser) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestEnvironment.Collect(provider.SendAsync("hello", user.Token)));
            else await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(provider.SendAsync("hello", user.Token)));
            Assert.Single(http.Requests); Assert.Empty(mcp.Calls);
        }
    }

    [Fact]
    public void Text_and_images_are_sent_in_supported_api_parts_while_references_retain_local_paths()
    {
        using var root = new TempFolder();
        File.WriteAllText(root.File("plan.txt"), "Dimensions: 12 x 5");
        File.WriteAllBytes(root.File("view.png"), Convert.FromBase64String(TestEnvironment.Png));
        File.WriteAllText(root.File("plan.pdf"), "placeholder");
        var attachments = ChatAttachments.Capture(new[] { "plan.txt", "view.png", "plan.pdf" }.Select(name => ChatAttachments.Inspect(root.File(name))).ToArray());
        var input = PerplexityProvider.UserInput("Inspect", attachments);
        Assert.Equal("message", input.GetProperty("type").GetString());
        Assert.Contains("Dimensions: 12 x 5", input.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Contains("cad_pdf_read", input.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("input_image", input.GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64," + TestEnvironment.Png, input.GetProperty("content")[1].GetProperty("image_url").GetString());
    }

    [Fact]
    public async Task Catalog_uses_models_endpoint_and_keeps_only_valid_unique_ids()
    {
        using var http = new HttpFixture((_, _, _) => Task.FromResult(Json(Wire.Element(new { data = new object[]
        { new { id = "z/model" }, new { id = "a/model" }, new { id = "z/model" }, new { id = "bad\nmodel" }, new { id = 3 } } }))));
        using var client = new HttpClient(http);
        var models = await PerplexityCatalog.LoadAsync(Key, client, default);
        Assert.Equal(new[] { "a/model", "z/model" }, models.Select(m => m.Id)); Assert.Equal("/v1/models", Assert.Single(http.Paths));
        Assert.Throws<ArgumentException>(() => PerplexityCatalog.ValidateSelection("model", "fast"));
        PerplexityCatalog.ValidateSelection("custom/future-model", null);
    }

    [Fact]
    public async Task Missing_usage_in_final_response_marks_the_previous_counters_as_partial()
    {
        using var root = new TempFolder(); var mcp = new FakeMcp();
        using var http = new HttpFixture((number, _, _) => Task.FromResult(Json(number == 1 ? Complete(Call())
            : Wire.Element(new { status = "completed", output = new[] { Message("Final without usage") } }))));
        using var client = new HttpClient(http);
        var events = await TestEnvironment.Collect(Provider(root, client, mcp).SendAsync("test", default));
        var usage = events.Last(e => e.Kind == "usage").Usage!;
        Assert.Equal(15, usage.Total); Assert.Contains("неполные данные", usage.Scope);
    }

    [Fact]
    public void Repeated_source_numbers_stay_local_to_each_response_without_global_markdown_definitions()
    {
        var first = new PerplexitySources(); var second = new PerplexitySources();
        first.Add(Wire.Element(new { type = "search_results", results = new[] { new { id = 1, title = "First", url = "https://example.com/first" } } }));
        second.Add(Wire.Element(new { type = "search_results", results = new[] { new { id = 1, title = "Second", url = "https://example.com/second" } } }));
        string firstText = first.Markdown(1), secondText = second.Markdown(2);
        Assert.Contains("ответ 1", firstText); Assert.Contains("ответ 2", secondText);
        Assert.DoesNotContain("[1]:", firstText + secondText); Assert.DoesNotContain("[web:1]:", firstText + secondText);
        Assert.Contains("https://example.com/first", firstText); Assert.Contains("https://example.com/second", secondText);
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], ct);
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Delay(TimeSpan.FromSeconds(5), ct); return 0; }
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/event-stream")]
    public async Task Stalled_response_body_is_reported_as_timeout_not_user_cancellation(string mediaType)
    {
        using var root = new TempFolder(); var mcp = new FakeMcp();
        using var http = new HttpFixture((_, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };
            response.Content.Headers.ContentType = new(mediaType);
            return Task.FromResult(response);
        });
        using var client = new HttpClient(http);
        var provider = Provider(root, client, mcp); provider.RequestTimeout = TimeSpan.FromMilliseconds(100);
        await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(provider.SendAsync("test", default)));
        Assert.Single(http.Requests); Assert.Empty(mcp.Calls);
    }

    [Fact]
    public async Task Invalid_arguments_and_unknown_functions_return_errors_without_reaching_cad()
    {
        using var root = new TempFolder(); var mcp = new FakeMcp();
        using var http = new HttpFixture((number, body, _) =>
        {
            if (number == 2)
            {
                var results = body.GetProperty("input").EnumerateArray().Where(item => item.Text("type") == "function_call_output").ToArray();
                Assert.Equal(2, results.Length);
                Assert.All(results, result => Assert.Contains("isError=true", result.GetProperty("output")[0].GetProperty("text").GetString()));
            }
            return Task.FromResult(Json(number == 1 ? Complete(Call(arguments: "not json"), Call("unknown_tool", "call-2")) : Complete(Message("No edits made"))));
        });
        using var client = new HttpClient(http);
        var events = await TestEnvironment.Collect(Provider(root, client, mcp).SendAsync("test", default));
        Assert.Empty(mcp.Calls); Assert.Contains(events, e => e.Kind == "completed"); Assert.Equal(2, events.Count(e => e.Kind == "problem"));
    }

    [Fact]
    public async Task Sse_handles_fragmented_utf8_and_multiline_data_but_rejects_truncated_frames()
    {
        string sse = ": heartbeat\r\nevent: custom\r\ndata: {\"type\":\"response.output_text.delta\",\r\ndata: \"delta\":\"Чертёж 🚜\"}\r\n\r\ndata: [DONE]\r\n\r\n";
        await using var stream = new FragmentedStream(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sse)).ToArray());
        var output = new List<JsonElement>();
        await foreach (var item in PerplexityStream.ReadAsync(stream, default)) output.Add(item);
        Assert.Equal("Чертёж 🚜", Assert.Single(output).GetProperty("delta").GetString());
        await using var truncated = new MemoryStream("data: {\"type\":\"response.completed\"}\n"u8.ToArray());
        await Assert.ThrowsAsync<IOException>(async () => { await foreach (var item in PerplexityStream.ReadAsync(truncated, default)) { } });
    }

    [Fact]
    public void Mcp_process_drops_inherited_provider_credentials_and_codex_identity_but_keeps_cad_scope()
    {
        string? previousKey = Environment.GetEnvironmentVariable("PERPLEXITY_API_KEY");
        string? previousBinding = Environment.GetEnvironmentVariable("CAD_MCP_PRIMARY_THREAD_FILE");
        try
        {
            Environment.SetEnvironmentVariable("PERPLEXITY_API_KEY", Key);
            Environment.SetEnvironmentVariable("CAD_MCP_PRIMARY_THREAD_FILE", "codex-private-thread.txt");
            var start = PerplexityMcpSession.StartInfo(Options with { McpArguments = ["--broker-pipe", "test-pipe"] });
            Assert.False(start.Environment.ContainsKey("PERPLEXITY_API_KEY")); Assert.False(start.Environment.ContainsKey("CAD_MCP_PRIMARY_THREAD_FILE"));
            Assert.Equal("owner-a", start.Environment["CAD_MCP_OWNER_ID"]); Assert.Equal("document-a", start.Environment["CAD_MCP_DOCUMENT_ID"]);
            Assert.Equal(new[] { "--broker-pipe", "test-pipe" }, start.ArgumentList);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERPLEXITY_API_KEY", previousKey);
            Environment.SetEnvironmentVariable("CAD_MCP_PRIMARY_THREAD_FILE", previousBinding);
        }
    }

    [Fact]
    public async Task Real_stdio_mcp_forwards_owner_and_document_and_enforces_foreign_drawing_denial()
    {
        string pipe = "cadmcp-perplexity-" + Guid.NewGuid().ToString("N"); var requests = new ConcurrentQueue<Request>();
        using var broker = new PipeServer(pipe, (request, _) => { requests.Enqueue(request); return Task.FromResult(new Response(request.RequestId, "completed", new { ok = true })); });
        broker.Start();
        var options = Options with { McpExecutable = TestEnvironment.HostExecutable, McpArguments = ["--broker-pipe", pipe] };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var mcp = await PerplexityMcpSession.StartAsync(options, timeout.Token);
        Assert.Equal(49, mcp.Tools.Count); Assert.Contains("cad_edit_preview", mcp.Instructions);
        var result = await mcp.CallAsync("cad_context", Wire.Element(new { session_id = "session-a" }), timeout.Token);
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.GetRawText());
        Assert.Equal("owner-a", requests.Last().OwnerId); Assert.Equal("document-a", requests.Last().DocumentId);
        int count = requests.Count;
        var foreign = await mcp.CallAsync("cad_context", Wire.Element(new { session_id = "session-a", document_id = "foreign" }), timeout.Token);
        Assert.True(foreign.GetProperty("isError").GetBoolean()); Assert.Equal(count, requests.Count);
    }
}
