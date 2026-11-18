using System.Text.Json;
using CadMcp.Providers;

namespace CadMcp.Tests;

public sealed class SubscriptionProviderTests
{
    private static SubscriptionAcpProvider Provider(bool kimi, TempFolder root, string? model = null) => kimi
        ? new KimiProvider(TestEnvironment.ProviderOptions with { WorkingDirectory = root.Path, Model = model, OwnerId = "owner", CadSessionId = "drawing-session", CadDocumentId = "document" }, root.File("kimi-home"))
        : new QwenProvider(TestEnvironment.ProviderOptions with { WorkingDirectory = root.Path, Model = model, OwnerId = "owner", CadSessionId = "drawing-session", CadDocumentId = "document" });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Subscription_streams_text_and_scoped_CAD_activity_and_resumes_without_replay(bool kimi)
    {
        using var root = new TempFolder();
        var provider = Provider(kimi, root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var first = await TestEnvironment.Collect(provider.SendAsync("ACP_PERMISSIONS", timeout.Token));
        Assert.Equal("Подписка ✓", string.Concat(first.Where(e => e.Kind == "text").Select(e => e.Text)));
        Assert.Contains(first, e => e.Kind == "step" && e.Tool == "cad_edit" && e.Phase == "completed");
        Assert.Contains(first, e => e.Kind == "completed");
        Assert.DoesNotContain(first, e => e.Kind is "input_ready" or "usage");
        Assert.Equal("acp-fixture-session", provider.SessionId);
        var second = await TestEnvironment.Collect(provider.SendAsync("follow-up", timeout.Token));
        Assert.Equal("Подписка ✓", string.Concat(second.Where(e => e.Kind == "text").Select(e => e.Text)));
        Assert.DoesNotContain(second, e => e.Text.Contains("REPLAY", StringComparison.Ordinal) || e.Text.Contains("FOREIGN", StringComparison.Ordinal));
        Assert.Equal("rejected", (await provider.SteerAsync(new("extra", []), timeout.Token)).State);
        var trace = Trace(provider);
        Assert.Single(trace, e => Method(e) == "session/load");
        Assert.DoesNotContain(trace, e => Method(e) == "authenticate");
        var setup = trace.First(e => Method(e) == "session/new").GetProperty("value").GetProperty("params");
        var cad = Assert.Single(setup.GetProperty("mcpServers").EnumerateArray());
        Assert.False(cad.TryGetProperty("type", out _)); // ACP stdio has no type discriminator.
        var env = cad.GetProperty("env").EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("value").GetString());
        Assert.Equal("owner", env["CAD_MCP_OWNER_ID"]);
        Assert.Equal("drawing-session", env["CAD_MCP_SESSION_ID"]);
        Assert.Equal("document", env["CAD_MCP_DOCUMENT_ID"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalog_is_authenticated_metadata_without_prompt_or_tools(bool kimi)
    {
        using var root = new TempFolder();
        var provider = Provider(kimi, root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var models = await provider.ReadModelsCoreAsync(timeout.Token);
        string prefix = kimi ? "kimi-code/" : "";
        Assert.Equal(new[] { prefix + "fixture-default", prefix + "fixture-alt" }, models.Select(m => m.Id));
        Assert.True(models[0].IsDefault);
        Assert.False(models[1].IsDefault);
        var trace = Trace(provider);
        Assert.DoesNotContain(trace, e => Method(e) is "session/prompt" or "authenticate");
        Assert.Empty(trace.Single(e => Method(e) == "session/new").GetProperty("value").GetProperty("params").GetProperty("mcpServers").EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_advertised_models_are_selected_and_captured_attachments_reach_ACP(bool kimi)
    {
        using var root = new TempFolder();
        string model = (kimi ? "kimi-code/" : "") + "fixture-alt";
        var provider = Provider(kimi, root, model);
        File.WriteAllText(root.File("drawing.txt"), "Чертёж ✓");
        File.WriteAllBytes(root.File("view.png"), Convert.FromBase64String(TestEnvironment.Png));
        // Direct IChatProvider callers need the same capture and size checks as the WPF composer.
        var attachments = new[] { root.File("drawing.txt"), root.File("view.png") }.Select(ChatAttachments.Inspect).ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = await TestEnvironment.Collect(provider.SendAsync("ACP_ATTACHMENTS", timeout.Token, attachments));
        Assert.Contains(events, e => e.Kind == "model" && e.Text == model);
        Assert.Contains(events, e => e.Kind == "completed");
        string method = kimi ? "session/set_config_option" : "session/set_model";
        string field = kimi ? "value" : "modelId";
        Assert.Equal(model, Trace(provider).Single(e => Method(e) == method).GetProperty("value").GetProperty("params").GetProperty(field).GetString());
        using var rejectedRoot = new TempFolder();
        var rejected = Provider(kimi, rejectedRoot, "unadvertised-model");
        await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(rejected.SendAsync("do not send", timeout.Token)));
        Assert.DoesNotContain(Trace(rejected), e => Method(e) == "session/prompt");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EOF_or_missing_session_is_not_success_and_never_restarts_a_prompt(bool kimi)
    {
        using var root = new TempFolder();
        var provider = Provider(kimi, root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<ChatEvent>();
        await Assert.ThrowsAsync<IOException>(async () => { await foreach (var item in provider.SendAsync("ACP_EOF", timeout.Token)) events.Add(item); });
        Assert.Contains(events, e => e.Kind == "text" && e.Text == "Незавершённый ответ");
        Assert.DoesNotContain(events, e => e.Kind == "completed");
        Assert.Single(Trace(provider), e => Method(e) == "session/prompt");
        provider.SessionId = "missing-session";
        await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(provider.SendAsync("do not retry", timeout.Token)));
        Assert.Single(Trace(provider), e => Method(e) == "session/new");
        Assert.Single(Trace(provider), e => Method(e) == "session/prompt");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_ends_the_turn_without_reporting_completion(bool kimi)
    {
        using var root = new TempFolder();
        var provider = Provider(kimi, root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<ChatEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in provider.SendAsync("ACP_CANCEL", timeout.Token))
            {
                events.Add(item);
                if (item.Kind == "text") timeout.Cancel();
            }
        });
        Assert.Contains(events, e => e.Kind == "text" && e.Text == "Начато");
        Assert.DoesNotContain(events, e => e.Kind == "completed");
        Assert.Contains(Trace(provider), e => Method(e) == "session/cancel");
    }

    [Fact]
    public async Task Authentication_failure_reports_login_without_changing_credentials_or_prompting()
    {
        using var root = new TempFolder();
        string cwd = root.File("acp-auth-failure");
        Directory.CreateDirectory(cwd);
        var provider = new QwenProvider(TestEnvironment.ProviderOptions with { WorkingDirectory = cwd });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<IOException>(() => provider.CheckAsync(timeout.Token));
        Assert.Contains("/auth", error.Message);
        Assert.DoesNotContain(Trace(provider), e => Method(e) is "session/prompt" or "authenticate");
    }

    [Fact]
    public void Grouped_config_models_keep_current_selection_and_exclude_Kimi_API_models()
    {
        var session = JsonSerializer.SerializeToElement(new { configOptions = new[] { new
        {
            id = "model", category = "model", type = "select", currentValue = "kimi-code/new-model",
            options = new object[] { new { name = "Managed", options = new[] { new { value = "kimi-code/new-model", name = "New model" } } },
                new { value = "api-key-provider/paid-model", name = "Metered model" } }
        } } });
        var model = Assert.Single(SubscriptionAcpProtocol.Models(session, SubscriptionCli.Kimi));
        Assert.Equal(new SubscriptionModel("kimi-code/new-model", "New model", true), model);
        Assert.Empty(SubscriptionAcpProtocol.Models(session, SubscriptionCli.Qwen)); // No verified Qwen auth metadata.
    }

    [Fact]
    public void Qwen_picker_joins_opaque_ids_to_subscription_auth_and_rejects_missing_or_conflicting_metadata()
    {
        var session = JsonSerializer.SerializeToElement(new
        {
            configOptions = new[] { new
            {
                id = "model", category = "model", type = "select", currentValue = "opaque-subscription-route",
                options = new[] { new { value = "opaque-subscription-route", name = "Subscription" },
                    new { value = "paid-route(qwen-subscription)", name = "Misleading API label" },
                    new { value = "no-metadata(qwen-subscription)", name = "Unverified" },
                    new { value = "conflicting-route", name = "Conflicting metadata" } }
            } },
            models = new { availableModels = new object[]
            {
                new { modelId = "opaque-subscription-route", _meta = new { authType = "qwen-subscription" } },
                new { modelId = "paid-route(qwen-subscription)", _meta = new { authType = "openai" } },
                new { modelId = "no-metadata(qwen-subscription)" },
                new { modelId = "conflicting-route", _meta = new { authType = "qwen-subscription" } },
                new { modelId = "conflicting-route", _meta = new { authType = "openai" } }
            } }
        });
        Assert.Equal(new SubscriptionModel("opaque-subscription-route", "Subscription", true),
            Assert.Single(SubscriptionAcpProtocol.Models(session, SubscriptionCli.Qwen)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Qwen_cannot_send_with_a_selected_or_restored_API_key_model(bool paidCurrent)
    {
        using var root = new TempFolder();
        string cwd = paidCurrent ? root.File("acp-paid-current") : root.Path;
        Directory.CreateDirectory(cwd);
        var provider = new QwenProvider(TestEnvironment.ProviderOptions with { WorkingDirectory = cwd, Model = paidCurrent ? null : "paid-openai" })
            { SessionId = paidCurrent ? "acp-fixture-session" : null };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(provider.SendAsync("must never reach a paid model", timeout.Token)));
        Assert.Contains(Trace(provider), e => Method(e) == (paidCurrent ? "session/load" : "session/new"));
        Assert.DoesNotContain(Trace(provider), e => Method(e) is "session/prompt" or "session/set_model" or "session/set_config_option");
    }

    [Fact]
    public void Permission_gate_never_uses_prose_or_arguments_and_never_persists_approval()
    {
        object[] choices = [new { kind = "allow_always", optionId = "always" }, new { kind = "allow_once", optionId = "once" }, new { kind = "reject_once", optionId = "reject" }];
        JsonElement Request(object tool, string session = "session") => JsonSerializer.SerializeToElement(new { sessionId = session, toolCall = tool, options = choices });
        string? Choice(JsonElement request, SubscriptionCli cli = SubscriptionCli.Qwen, string active = "session", bool prompting = true) =>
            JsonSerializer.SerializeToElement(SubscriptionAcpProtocol.PermissionResponse(request, cli, active, prompting)).GetProperty("outcome").TryGetProperty("optionId", out var id) ? id.GetString() : null;
        var cad = Request(new { title = "description", _meta = new { toolName = "mcp__cad__cad_edit" } });
        Assert.Equal("once", Choice(cad));
        Assert.Equal("reject", Choice(cad, active: "different-session"));
        Assert.Equal("reject", Choice(cad, prompting: false));
        var spoof = Request(new { title = "mcp__cad__cad_edit", rawInput = new { command = "cad_edit" }, _meta = new { toolName = "Bash" } });
        Assert.Equal("reject", Choice(spoof));
        Assert.Equal("reject", Choice(spoof, SubscriptionCli.Kimi));
        Assert.Equal("reject", Choice(Request(new { title = "mcp__cad__cad_edit", kind = "execute" }), SubscriptionCli.Kimi));
        Assert.Equal("reject", Choice(Request(new { title = "mcp__cad__cad_edit" })));
        Assert.Equal("once", Choice(Request(new { title = "mcp__cad__cad_edit" }), SubscriptionCli.Kimi));
        Assert.Equal("reject", Choice(Request(new { _meta = new { toolName = "mcp__foreign__cad_edit" } })));
        Assert.Equal("reject", Choice(Request(new { _meta = new { toolName = "mcp__cad__cad_edit", serverId = "foreign" } })));
        Assert.Equal("reject", Choice(Request(new { _meta = new { toolName = "mcp__cad__unregistered_tool" } })));
    }

    [Fact]
    public void Kimi_isolation_preserves_login_sections_and_reasserts_only_CAD_tools()
    {
        string config = "default_model = \"kimi-code/test\"\n[tools]\nenabled = [\"Bash\"]\ndisabled = []\n[providers.kimi-code]\ntype = \"kimi\"\n";
        string protectedConfig = KimiConfiguration.ProtectTools(config);
        Assert.Contains(KimiConfiguration.ToolsSection, protectedConfig);
        Assert.DoesNotContain("Bash", protectedConfig);
        Assert.Contains("[providers.kimi-code]", protectedConfig);
        Assert.Equal(protectedConfig, KimiConfiguration.ProtectTools(protectedConfig));
        using var root = new TempFolder();
        var provider = Provider(true, root);
        var info = provider.StartInfo();
        Assert.Equal(root.File("kimi-home"), info.Environment["KIMI_CODE_HOME"]);
        Assert.StartsWith(root.File("kimi-home"), info.WorkingDirectory);
        Assert.Contains(KimiConfiguration.ToolsSection, File.ReadAllText(root.File("kimi-home/config.toml")));
        info.Environment["KIMI_MODEL_NAME"] = "paid-model";
        info.Environment["KIMI_MODEL_API_KEY"] = "do-not-use";
        info.Environment["KIMI_CODE_BASE_URL"] = "https://unrelated.invalid";
        KimiConfiguration.ApplyEnvironment(info, root.File("kimi-home"));
        Assert.False(info.Environment.ContainsKey("KIMI_MODEL_API_KEY"));
        Assert.False(info.Environment.ContainsKey("KIMI_MODEL_NAME"));
        Assert.False(info.Environment.ContainsKey("KIMI_CODE_BASE_URL"));
        Assert.Equal(new[] { "acp" }, provider.Arguments());
    }

    [Fact]
    public void Npm_shim_resolution_uses_package_bin_without_a_command_shell()
    {
        using var root = new TempFolder();
        string folder = root.File("with spaces & percent% PATH");
        string package = Path.Combine(folder, "node_modules", "@qwen-code", "qwen-code");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(folder, "qwen.cmd"), "never execute this batch file");
        File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@qwen-code/qwen-code\",\"bin\":{\"qwen\":\"cli-entry.js\"}}");
        File.WriteAllText(Path.Combine(package, "cli-entry.js"), "");
        string node = Path.Combine(folder, OperatingSystem.IsWindows() ? "node.exe" : "node");
        File.WriteAllText(node, "");
        var options = TestEnvironment.ProviderOptions with { Executable = Path.Combine(folder, "qwen.cmd"), WorkingDirectory = root.Path };
        var info = new QwenProvider(options).StartInfo();
        Assert.Equal(node, info.FileName);
        Assert.Equal(Path.Combine(package, "cli-entry.js"), info.ArgumentList[0]);
        Assert.False(info.UseShellExecute);
        Assert.Contains("qwen-subscription", info.ArgumentList);
        Assert.Contains("--bare", info.ArgumentList);
        Assert.DoesNotContain("--yolo", info.ArgumentList);
        Assert.Contains("--exclude-tools", info.ArgumentList);
        foreach (string tool in new[] { "read_file", "edit", "notebook_edit", "run_shell_command" }) Assert.Contains(tool, info.ArgumentList);
        File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@qwen-code/qwen-code\",\"bin\":{\"qwen\":\"../../../escape.js\"}}");
        Assert.Throws<IOException>(() => new QwenProvider(options).StartInfo());
    }

    [Fact]
    public void Codex_only_thread_binding_is_removed_but_read_only_policy_remains()
    {
        string? priorThread = Environment.GetEnvironmentVariable("CAD_MCP_PRIMARY_THREAD_FILE");
        string? priorReadOnly = Environment.GetEnvironmentVariable("CAD_MCP_READ_ONLY");
        try
        {
            Environment.SetEnvironmentVariable("CAD_MCP_PRIMARY_THREAD_FILE", "codex-thread-only.txt");
            Environment.SetEnvironmentVariable("CAD_MCP_READ_ONLY", "1");
            using var root = new TempFolder();
            foreach (bool kimi in new[] { false, true })
            {
                var info = Provider(kimi, root).StartInfo();
                Assert.False(info.Environment.ContainsKey("CAD_MCP_PRIMARY_THREAD_FILE"));
                Assert.Equal("1", info.Environment["CAD_MCP_READ_ONLY"]);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAD_MCP_PRIMARY_THREAD_FILE", priorThread);
            Environment.SetEnvironmentVariable("CAD_MCP_READ_ONLY", priorReadOnly);
        }
    }

    private static JsonElement[] Trace(SubscriptionAcpProvider provider) => File.ReadAllLines(Path.Combine(provider.StartInfo().WorkingDirectory, "acp-fixture.jsonl"))
        .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
    private static string? Method(JsonElement row) => row.GetProperty("direction").GetString() == "in" && row.GetProperty("value").TryGetProperty("method", out var method) ? method.GetString() : null;
}
