using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CadMcp.Core;
using CadMcp.Providers;

namespace CadMcp.Tests;

public sealed class ProviderTests
{
    private static readonly ProviderOptions Options = TestEnvironment.ProviderOptions;
    private static IChatProvider Make(string name) => name == "Codex" ? new CodexProvider(Options) : new ClaudeProvider(Options);

    [Fact]
    public void Nested_Codex_errors_are_readable()
    {
        var error = JsonSerializer.SerializeToElement(new { error = new { message = JsonSerializer.Serialize(new { error = new { message = "Model is unavailable for this account" } }) } });
        Assert.Equal("Model is unavailable for this account", CodexProvider.ErrorMessage(error));
        Assert.Contains("Выберите другую модель", CodexProvider.FriendlyError("Selected model is at capacity. Please try a different model."));
    }

    [Fact]
    public async Task Codex_and_Claude_receive_text_and_images_on_their_wire_formats()
    {
        using var root = new TempFolder();
        File.WriteAllText(root.File("plan.txt"), "Чертёж ✓");
        File.WriteAllBytes(root.File("view.png"), Convert.FromBase64String(TestEnvironment.Png));
        var files = ChatAttachments.Capture(new[] { "plan.txt", "view.png" }.Select(name => ChatAttachments.Inspect(root.File(name))).ToArray());
        foreach (IChatProvider provider in new IChatProvider[] { new CodexProvider(Options), new ClaudeProvider(Options) })
            Assert.Contains(await TestEnvironment.Collect(provider.SendAsync("ATTACHMENT", default, files)), item => item.Kind == "completed");
    }

    [Fact]
    public async Task Model_discovery_paginates_and_skips_hidden_and_malformed_entries()
    {
        var models = await CodexCatalog.ReadAsync(Options, default);
        Assert.Equal(new[] { "available-model", "alternative-model" }, models.Select(m => m.Id));
        var selected = CodexCatalog.Select(models, "alternative-model", "high");
        Assert.True(selected.Model.Id == "alternative-model" && selected.Effort == "high", "Explicit selection lost");
        Assert.Equal("available-model", CodexCatalog.Select(models, null, null).Model.Id);
        Assert.Throws<IOException>(() => CodexCatalog.Select(models, "hidden-model", null));
        Assert.Throws<IOException>(() => CodexCatalog.Select(models, "alternative-model", "ultra"));
    }

    [Fact]
    public void Catalog_entries_with_missing_or_null_fields_do_not_break_the_list()
    {
        var entries = JsonDocument.Parse("""
            [{"model":"no-effort"},
             {"model":"null-hidden","hidden":null,"isDefault":null,"defaultReasoningEffort":null,"supportedReasoningEfforts":[{"reasoningEffort":null},{"reasoningEffort":"low"}]},
             {"model":42}, "text", {"hidden":true,"model":"hidden"}]
            """).RootElement.EnumerateArray().ToArray();
        var models = CodexCatalog.Parse(entries);
        Assert.Equal(new[] { "no-effort", "null-hidden" }, models.Select(m => m.Id));
        Assert.Equal("low", models[1].DefaultEffort);
    }

    [Fact]
    public async Task Requested_model_and_effort_reach_new_and_resumed_Codex_turns()
    {
        var provider = new CodexProvider(Options with { Model = "alternative-model", ReasoningEffort = "high" });
        foreach (int turn in new[] { 1, 2 })
        {
            var events = await TestEnvironment.Collect(provider.SendAsync("SELECT", default));
            Assert.True(events.Any(e => e.Kind == "model" && e.Text == "alternative-model") && events.Any(e => e.Kind == "effort" && e.Text == "high"), "Wrong selected model/effort");
            Assert.Equal("test-thread", provider.SessionId);
        }
        var switched = new CodexProvider(Options) { SessionId = provider.SessionId };
        await TestEnvironment.Collect(switched.SendAsync("Read", default));
        Assert.Equal(provider.SessionId, switched.SessionId);
    }

    [Fact]
    public async Task Codex_retries_an_active_writer_and_releases_the_provider_process()
    {
        var provider = new CodexProvider(Options) { SessionId = "retry-thread" };
        var events = await TestEnvironment.Collect(provider.SendAsync("Read", default));
        Assert.Contains(events, e => e.Kind == "completed");
        Assert.Equal("test-thread", provider.SessionId);
    }

    [Fact]
    public async Task An_unavailable_model_fails_instead_of_silently_selecting_another()
    {
        var provider = new CodexProvider(Options with { Model = "missing-model" });
        await Assert.ThrowsAnyAsync<IOException>(() => TestEnvironment.Collect(provider.SendAsync("Read", default)));
        Assert.Null(provider.SessionId);
    }

    [Fact]
    public async Task Codex_declines_an_interactive_server_request_and_finishes_the_turn()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = await TestEnvironment.Collect(new CodexProvider(Options).SendAsync("REQUEST", timeout.Token));
        Assert.Contains(events, e => e.Kind == "status" && e.Text.Contains("item/tool/requestUserInput"));
        Assert.Equal("Продолжил", string.Concat(events.Where(e => e.Kind == "text").Select(e => e.Text)));
        Assert.Contains(events, e => e.Kind == "completed");
    }

    [Theory]
    [InlineData("Codex")]
    [InlineData("Claude")]
    public async Task Stream_session_and_unicode(string name)
    {
        var provider = Make(name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var events = await TestEnvironment.Collect(provider.SendAsync("Read", timeout.Token));
        Assert.Equal("Сеть ✓", string.Concat(events.Where(e => e.Kind == "text").Select(e => e.Text)));
        Assert.True(provider.SessionId is not null && events.Any(e => e.Kind == "completed"), "Session missing");
        // Claude repeats session_id on nearly every line; the chat must hear about it once.
        Assert.Single(events, e => e.Kind == "session");
        if (name == "Codex")
        {
            Assert.Contains(events, e => e.Kind == "model" && e.Text == "available-model");
            Assert.Contains(events, e => e.Kind == "reasoning_summary" && e.Text.Contains("размеры"));
            Assert.Equal(2, events.Count(e => e.Kind == "step" && e.Text.Contains("cad_search")));
            Assert.Contains(events, e => e.Kind == "status" && e.Text.Contains("Помощники"));
        }
        else Assert.Contains(events, e => e.Kind == "step" && e.Text == "Завершено: mcp__cad__cad_edit");
        Assert.DoesNotContain(events, e => e.Kind == "cad_result");
        events = await TestEnvironment.Collect(provider.SendAsync("Resume", timeout.Token));
        Assert.Contains(events, e => e.Kind == "completed");
    }

    [Theory]
    [InlineData("Codex")]
    [InlineData("Claude")]
    public async Task Stopping_a_silent_process_does_not_hang(string name)
    {
        var provider = Make(name); using var timeout = new CancellationTokenSource(400); var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestEnvironment.Collect(provider.SendAsync("SILENT", timeout.Token)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), "Stop hung waiting for stdout");
    }

    [Fact]
    public async Task Cancelled_process_EOF_is_cancellation_instead_of_a_provider_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var lines = new ProviderLines(new CancelledEofReader(cancellation));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lines.ReadAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    private sealed class CancelledEofReader(CancellationTokenSource cancellation) : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken ct = default)
        {
            // Simulate the real stdout race: process termination wins the read, returning EOF after cancellation.
            cancellation.Cancel();
            return ValueTask.FromResult(0);
        }
    }

    private static async Task<(List<ChatEvent> Events, InputReceipt? Receipt)> Steer(IChatProvider provider, string prompt, TimeSpan limit)
    {
        using var timeout = new CancellationTokenSource(limit);
        Task<InputReceipt>? receipt = null; var events = new List<ChatEvent>();
        await foreach (var e in provider.SendAsync(prompt, timeout.Token))
        {
            events.Add(e);
            if (e.Kind == "input_ready" && receipt is null) receipt = provider.SteerAsync(new("Уточнение ✓", Array.Empty<ChatAttachment>()), timeout.Token);
        }
        return (events, receipt is null ? null : await receipt);
    }

    [Theory]
    [InlineData("Codex")]
    [InlineData("Claude")]
    public async Task An_active_task_accepts_a_follow_up_without_a_second_writer(string name)
    {
        var live = Make(name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal("unavailable", (await live.SteerAsync(new("before", Array.Empty<ChatAttachment>()), timeout.Token)).State);
        var (events, receipt) = await Steer(live, "STEER", TimeSpan.FromSeconds(10));
        Assert.Equal("accepted", receipt?.State);
        Assert.Contains("Учёл уточнение", string.Concat(events.Where(e => e.Kind == "text").Select(e => e.Text)));
        Assert.Equal("unavailable", (await live.SteerAsync(new("after", Array.Empty<ChatAttachment>()), timeout.Token)).State);
    }

    [Fact]
    public async Task A_rejected_Codex_follow_up_keeps_the_original_response()
    {
        var (events, receipt) = await Steer(new CodexProvider(Options), "STEER_REJECT", TimeSpan.FromSeconds(10));
        Assert.Contains(events, e => e.Kind == "completed");
        Assert.Equal("rejected", receipt?.State);
    }

    [Fact]
    public async Task Claude_finishes_when_one_result_answers_merged_messages()
    {
        var sw = Stopwatch.StartNew();
        var (events, receipt) = await Steer(new ClaudeProvider(Options), "MERGE", TimeSpan.FromSeconds(10));
        Assert.Equal("accepted", receipt?.State);
        Assert.Contains(events, e => e.Kind == "completed");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "Waited for a second result that never comes");
    }

    [Fact]
    public async Task Claude_finishes_after_a_quiet_period_when_a_merged_result_does_not_list_its_messages()
    {
        var provider = new ClaudeProvider(Options) { QuietAfterResult = TimeSpan.FromSeconds(1) };
        var (events, receipt) = await Steer(provider, "MERGE_LEGACY", TimeSpan.FromSeconds(10));
        Assert.Equal("accepted", receipt?.State);
        Assert.Contains(events, e => e.Kind == "completed");
    }

    [Fact]
    public async Task Claude_finishes_when_a_follow_up_is_never_acknowledged()
    {
        var provider = new ClaudeProvider(Options) { AcknowledgementTimeout = TimeSpan.FromSeconds(1) };
        var (events, receipt) = await Steer(provider, "NOACK", TimeSpan.FromSeconds(10));
        Assert.Equal("uncertain", receipt?.State);
        Assert.Contains(events, e => e.Kind == "completed");
    }

    [Fact]
    public void Claude_turn_accounting()
    {
        var legacy = new ClaudeTurns("initial");
        legacy.Acknowledge("follow-up");
        legacy.Result(null);
        Assert.False(legacy.Settled(false), "A result without a list answers one message");
        Assert.True(legacy.Settled(true), "A quiet period after such a result ends the wait");
        legacy.Result(null);
        Assert.True(legacy.Settled(false));
        var listed = new ClaudeTurns("initial");
        listed.Acknowledge("a"); listed.Acknowledge("b");
        listed.Result(["a"]);
        Assert.False(listed.Settled(true), "A listed result is exact; quiet time does not answer b");
        listed.Result(["b"]);
        Assert.True(listed.Settled(false));
    }

    [Fact]
    public async Task Provider_lines_skip_text_and_oversized_lines()
    {
        var input = "not json\n" + new string('x', 50) + "\n{\"a\":1}\r\n\n{\"b\":2}";
        var lines = new ProviderLines(new StringReader(input), maximumChars: 20);
        using (var first = await lines.ReadAsync(default)) Assert.Equal(1, first!.RootElement.GetProperty("a").GetInt32());
        using (var second = await lines.ReadAsync(default)) Assert.Equal(2, second!.RootElement.GetProperty("b").GetInt32());
        Assert.Null(await lines.ReadAsync(default));
        Assert.Contains("not json", lines.Skipped);
        Assert.Contains("longer than 20", lines.Skipped);
    }

    [Fact]
    public void Claude_arguments_keep_paths_isolate_the_mcp_config_and_restrict_helpers()
    {
        var a = new ClaudeProvider(Options).Arguments();
        var config = JsonDocument.Parse(a[Array.IndexOf(a, "--mcp-config") + 1]);
        Assert.Equal(Options.McpExecutable, config.RootElement.GetProperty("mcpServers").GetProperty("cad").GetProperty("command").GetString());
        Assert.Contains("--strict-mcp-config", a);
        Assert.Equal("cad_primary", a[Array.IndexOf(a, "--agent") + 1]);
        using var helper = JsonDocument.Parse(a[Array.IndexOf(a, "--agents") + 1]);
        var helperTools = helper.RootElement.GetProperty("cad_researcher").GetProperty("tools").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal("1", helper.RootElement.GetProperty("cad_researcher").GetProperty("mcpServers")[0].GetProperty("cad").GetProperty("env").Text("CAD_MCP_READ_ONLY"));
        Assert.True(helperTools.Contains("mcp__cad__cad_search") && !helperTools.Contains("mcp__cad__cad_edit") && !helperTools.Contains("mcp__cad__cad_lisp") && !helperTools.Contains("mcp__cad__cad_focus"), "Claude helper must be read-only");
        // The agent prompt replaces the default system prompt; appending it again would duplicate it.
        Assert.DoesNotContain("--append-system-prompt", a);
        // Both agents may search the web and read; reading reaches the working folder and attached files only.
        var primaryTools = helper.RootElement.GetProperty("cad_primary").GetProperty("tools").EnumerateArray().Select(x => x.GetString()).ToArray();
        foreach (var tool in new[] { "WebSearch", "Read", "Glob", "Grep" })
            Assert.True(primaryTools.Contains(tool) && helperTools.Contains(tool), tool + " missing from the Claude agents");
        Assert.DoesNotContain("--settings", a);
        var single = new ClaudeProvider(Options with { MaxSubagents = 0 }).Arguments();
        Assert.True(single.Contains("--disallowedTools") && !single.Contains("--agents") && single.Contains("--append-system-prompt"), "Claude helper opt-out missing");
        Assert.Equal("Read,Glob,Grep,WebSearch", single[Array.IndexOf(single, "--tools") + 1]);
        Assert.Equal("WebSearch", single[Array.IndexOf(single, "--allowedTools") + 2]);
        var selected = new ClaudeProvider(Options with { Model = "opus", ReasoningEffort = "high" }).Arguments();
        Assert.Equal("opus", selected[Array.IndexOf(selected, "--model") + 1]);
        Assert.Equal("high", selected[Array.IndexOf(selected, "--effort") + 1]);
        // --allowedTools takes a list; the helper and web search permissions must not be read as a prompt after another option.
        Assert.Equal("Agent(cad_researcher)", selected[Array.IndexOf(selected, "--allowedTools") + 2]);
        Assert.Equal("WebSearch", selected[Array.IndexOf(selected, "--allowedTools") + 3]);
    }

    [Fact]
    public void Claude_reads_exactly_the_attached_files_wherever_they_are()
    {
        // Claude Code matches //absolute paths, Windows drives normalized to /c/..., gitignore pattern characters escaped.
        Assert.Equal(@"Read(//c/Users/Имя/Docs/План (ред. 2) \[a\].pdf)", ClaudeProvider.ReadRule(@"C:\Users\Имя\Docs\План (ред. 2) [a].pdf"));
        Assert.Equal(@"Read(//d/Проект/plan.dwg)", ClaudeProvider.ReadRule("D:/Проект/plan.dwg"));
        Assert.Equal(@"Read(//home/u/f\*\?.txt)", ClaudeProvider.ReadRule("/home/u/f*?.txt"));
        // Network and relative paths get no rule.
        Assert.Null(ClaudeProvider.ReadRule(@"\\server\share\plan.pdf"));
        Assert.Null(ClaudeProvider.ReadRule("//server/share/plan.pdf"));
        Assert.Null(ClaudeProvider.ReadRule("plan.pdf"));
        var files = new[] { new ChatAttachment(@"C:\A (1)\b (2).pdf", "b (2).pdf", 10, AttachmentKind.File), new ChatAttachment(@"\\server\x.pdf", "x.pdf", 1, AttachmentKind.File) };
        var arguments = new ClaudeProvider(Options).Arguments(files);
        // The rules travel as settings JSON: --allowedTools would split "A (1)/b (2).pdf" at its spaces.
        using var settings = JsonDocument.Parse(arguments[Array.IndexOf(arguments, "--settings") + 1]);
        Assert.Equal(new[] { "Read(//c/A (1)/b (2).pdf)" }, settings.RootElement.GetProperty("permissions").GetProperty("allow").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Claude_model_list_and_levels_come_from_the_installed_CLI()
    {
        var models = await ClaudeCatalog.ReadAsync(Options, default);
        Assert.Equal(new[] { "default", "opus", "fable", "legacy" }, models.Select(m => m.Id));
        Assert.True(models[0].IsDefault && models.Skip(1).All(m => !m.IsDefault), "The default entry is not recognized");
        Assert.Equal("Opus 5.5 · Best for everyday, complex tasks", models[1].Label);
        Assert.Equal("Legacy", models[3].Label);
        Assert.Equal(new[] { "low", "medium", "high" }, models[2].Efforts);
        Assert.Empty(models[3].Efforts);
        Assert.Equal(new ClaudeSelection("opus", "max"), ClaudeCatalog.Select(models, "opus", "max"));
        Assert.Equal(new ClaudeSelection(null, "xhigh"), ClaudeCatalog.Select(models, "", "xhigh"));
        Assert.Throws<IOException>(() => ClaudeCatalog.Select(models, "fable", "max"));
        Assert.Throws<IOException>(() => ClaudeCatalog.Select(models, "legacy", "low"));
        Assert.Throws<IOException>(() => ClaudeCatalog.Select(models, "retired", null));
        // Without a list nothing is checked: the previous behaviour for a CLI that cannot list its models.
        Assert.Equal(new ClaudeSelection("custom", "high"), ClaudeCatalog.Select([], "custom", "high"));
    }

    [Fact]
    public void Claude_model_entries_with_missing_fields_do_not_break_the_list()
    {
        var models = ClaudeCatalog.Parse(JsonDocument.Parse("""
            [{"value":"a"}, {"displayName":"no value"}, {"value":42}, "text", {"value":"a","displayName":"duplicate"},
             {"value":"b","supportsEffort":true,"supportedEffortLevels":[null,"low","",7,"low"]},
             {"value":"c","supportsEffort":true}]
            """).RootElement);
        Assert.Equal(new[] { "a", "b", "c" }, models.Select(m => m.Id));
        Assert.Equal("a", models[0].DisplayName);
        Assert.Equal(new[] { "low" }, models[1].Efforts);
        Assert.Empty(models[2].Efforts);
        Assert.Empty(ClaudeCatalog.Parse(default));
    }

    [Fact]
    public async Task Claude_reports_its_model_level_reasoning_and_limit_without_helper_text()
    {
        var events = await TestEnvironment.Collect(new ClaudeProvider(Options with { Model = "opus", ReasoningEffort = "high" }).SendAsync("THINK", default));
        Assert.Contains(events, e => e.Kind == "model" && e.Text == "claude-opus-5-5");
        Assert.Contains(events, e => e.Kind == "effort" && e.Text == "high");
        Assert.Contains(events, e => e.Kind == "status" && e.Text == "Claude рассуждает");
        Assert.Contains(events, e => e.Kind == "status" && e.Text.Contains("120"));
        Assert.Contains(events, e => e.Kind == "reasoning_summary" && e.Text == "Сверяю размеры");
        Assert.Contains(events, e => e.Kind == "status" && e.Text.Contains("90%") && e.Text.Contains("недельного"));
        Assert.Equal("Готово", string.Concat(events.Where(e => e.Kind == "text").Select(e => e.Text)));
        Assert.Contains(events, e => e.Kind == "completed");
    }

    [Fact]
    public async Task Claude_starts_a_new_conversation_when_the_saved_one_is_gone()
    {
        var provider = new ClaudeProvider(Options) { SessionId = "missing-session" };
        var events = await TestEnvironment.Collect(provider.SendAsync("hello", default));
        Assert.Contains(events, e => e.Kind == "status" && e.Text.Contains("Прежний диалог Claude Code не найден"));
        Assert.Contains(events, e => e.Kind == "completed");
        Assert.Equal("test-claude", provider.SessionId);
    }

    [Fact]
    public void Claude_errors_are_readable()
    {
        var failed = JsonDocument.Parse("""{"type":"result","subtype":"error_during_execution","is_error":true,"errors":["Turn failed",{"message":"second"}]}""").RootElement;
        Assert.Equal("Turn failed; second", ClaudeProvider.ResultError(failed));
        Assert.Contains("error_during_execution", ClaudeProvider.ResultError(JsonDocument.Parse("""{"subtype":"error_during_execution"}""").RootElement));
        Assert.Equal("Model text", ClaudeProvider.ResultError(JsonDocument.Parse("""{"result":"Model text","errors":["ignored"]}""").RootElement));
        Assert.Contains("Обновите список моделей", ClaudeProvider.FriendlyError("There's an issue with the selected model (x). It may not exist or you may not have access to it."));
        Assert.Contains("«Войти»", ClaudeProvider.FriendlyError("Invalid API key · Please run /login"));
        Assert.Equal("Something else", ClaudeProvider.FriendlyError("Something else"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Command_lines_stay_below_the_Windows_limit(int helpers)
    {
        var options = Options with { MaxSubagents = helpers, McpExecutable = @"C:\Users\Пользователь с длинным именем\AppData\Roaming\Autodesk\ApplicationPlugins\CadMcp.AutoCAD2025.bundle\Contents\Host\CadMcp.Host.exe",
            OwnerId = Guid.NewGuid().ToString("N"), CadSessionId = Guid.NewGuid().ToString("N"), CadDocumentId = Guid.NewGuid().ToString("N"), Model = "claude-opus", ReasoningEffort = "high" };
        // Ten attachments with long Cyrillic paths add their read rules to the Claude command line.
        var files = Enumerable.Range(1, 10).Select(i => new ChatAttachment(@"C:\Users\Пользователь с длинным именем\Documents\Проект жилого дома (стадия П)\Раздел " + i + @" — пояснительная записка [ред. 2].pdf", "f.pdf", 1, AttachmentKind.File)).ToArray();
        foreach (var arguments in new[] { new ClaudeProvider(options) { SessionId = Guid.NewGuid().ToString() }.Arguments(files), new CodexProvider(options).Arguments() })
            Assert.InRange(WindowsCommandLineLength(options.Executable, arguments), 0, 24000);
    }

    /// <summary>Length after the quoting that Windows process creation applies to each argument.</summary>
    private static int WindowsCommandLineLength(string executable, IEnumerable<string> arguments)
    {
        int length = executable.Length + 3;
        foreach (var argument in arguments)
        {
            int backslashes = 0; length += 3;
            foreach (char c in argument)
            {
                if (c == '\\') { backslashes++; length++; continue; }
                if (c == '"') length += backslashes + 1;
                backslashes = 0; length++;
            }
            length += backslashes;
        }
        return length;
    }

    [Fact]
    public void Codex_grants_CAD_tools_without_global_permission_changes()
    {
        var arguments = new CodexProvider(Options).Arguments();
        Assert.True(arguments.Contains("mcp_servers.cad.enabled=true") && arguments.Contains("mcp_servers.cad.default_tools_approval_mode=\"approve\""), "CAD server permission missing");
        Assert.DoesNotContain(arguments, a => a.Contains("danger-full-access") || a.Contains("bypass-approvals") || a.StartsWith("apps."));
        var command = arguments.Single(a => a.StartsWith("mcp_servers.cad.command="));
        Assert.Equal(Options.McpExecutable, JsonSerializer.Deserialize<string>(command["mcp_servers.cad.command=".Length..]));
        Assert.True(arguments.Contains("agents.enabled=true") && arguments.Contains("agents.max_concurrent_threads_per_session=3"), "Subagent limit missing");
        Assert.Contains("agents.enabled=false", new CodexProvider(Options with { MaxSubagents = 0 }).Arguments());
    }
}
