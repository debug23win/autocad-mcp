using System.IO.Compression;
using System.Text;
using CadMcp.Providers;

namespace CadMcp.Tests;

public sealed class ProviderApiRuntimeTests
{
    private static ProviderOptions Options(TempFolder folder, string mode, string key)
    {
        string directory = folder.File(mode); Directory.CreateDirectory(directory);
        return TestEnvironment.ProviderOptions with { WorkingDirectory = directory, MaxSubagents = 0, Authentication = new(key) };
    }
    private static IChatProvider Provider(string name, ProviderOptions options) => name == "Codex" ? new CodexProvider(options) : new ClaudeProvider(options);
    private static string[] Trace(ProviderOptions options) => File.ReadAllLines(Path.Combine(options.WorkingDirectory, "auth-protocol.txt"));

    [Theory]
    [InlineData("Codex")]
    [InlineData("Claude")]
    public async Task API_runtime_redacts_split_credentials_without_breaking_JSON_or_streamed_answers(string name)
    {
        using var folder = new TempFolder();
        const string key = "sk-fixture-quote\"slash\\credential-12345";
        var options = Options(folder, "auth-stream", key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var events = await TestEnvironment.Collect(Provider(name, options).SendAsync("AUTH_STREAM", timeout.Token));
        Assert.Contains(events, item => item.Kind == "completed");
        string answer = string.Concat(events.Where(item => item.Kind == "text").Select(item => item.Text));
        string summaries = string.Concat(events.Where(item => item.Kind == "reasoning_summary").Select(item => item.Text));
        Assert.Equal("before [REDACTED] after", answer); Assert.Equal("before [REDACTED] after", summaries);
        Assert.DoesNotContain(key, string.Join("\n", events.Select(item => item.Text)));
        Assert.DoesNotContain(events, item => item.Kind == "rate_limits" || item.Text.Contains("лимита подписки", StringComparison.Ordinal));
        Assert.DoesNotContain(key, string.Join("\n", new CodexProvider(options).Arguments()));
        Assert.DoesNotContain(key, options.ToString());
        string[] trace = Trace(options);
        if (name == "Codex")
        {
            Assert.True(Array.IndexOf(trace, "config/read") < Array.IndexOf(trace, "account/login/start"));
            Assert.True(Array.IndexOf(trace, "account/login/start") < Array.IndexOf(trace, "model/list"));
            Assert.True(Array.IndexOf(trace, "model/list") < Array.IndexOf(trace, "turn/start"));
            Assert.DoesNotContain("account/rateLimits/read", trace);
        }
        else Assert.Equal(new[] { "version", "claude-start" }, trace);
    }

    [Theory]
    [InlineData("auth-config-reject", false)]
    [InlineData("auth-cua-reject", false)]
    [InlineData("auth-login-reject", true)]
    [InlineData("auth-login-shape", true)]
    public async Task Codex_stops_before_a_model_turn_when_configuration_or_API_authentication_is_not_confirmed(string mode, bool keyWasSubmitted)
    {
        using var folder = new TempFolder(); const string key = "sk-fixture-gate-credential";
        var options = Options(folder, mode, key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var error = await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(new CodexProvider(options).SendAsync("test", timeout.Token)));
        Assert.DoesNotContain(key, error.ToString());
        string[] trace = Trace(options);
        Assert.Equal(keyWasSubmitted, trace.Contains("account/login/start"));
        Assert.DoesNotContain("model/list", trace); Assert.DoesNotContain("thread/start", trace); Assert.DoesNotContain("turn/start", trace);
    }

    [Theory]
    [InlineData("Codex", "auth-key-echo")]
    [InlineData("Codex", "auth-raw-exit")]
    [InlineData("Claude", "auth-key-echo")]
    [InlineData("Claude", "auth-raw-exit")]
    public async Task API_errors_and_truncated_stdout_stderr_never_expose_the_credential(string name, string mode)
    {
        using var folder = new TempFolder();
        string key = "sk-fixture-private-" + new string('Q', 1500);
        var options = Options(folder, mode, key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var error = await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(Provider(name, options).SendAsync("test", timeout.Token)));
        Assert.DoesNotContain(key, error.ToString()); Assert.DoesNotContain(key[..64], error.ToString());
        Assert.Contains("[REDACTED]", error.Message);
        if (name == "Claude" && mode == "auth-key-echo")
        {
            Assert.Contains("API", error.Message); Assert.DoesNotContain("нажмите «Войти»", error.Message);
        }
    }

    [Fact]
    public async Task Claude_rejects_an_old_CLI_before_starting_a_process_with_the_API_key()
    {
        using var folder = new TempFolder(); const string key = "sk-fixture-old-cli";
        var options = Options(folder, "auth-claude-old", key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var error = await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(new ClaudeProvider(options).SendAsync("test", timeout.Token)));
        Assert.DoesNotContain(key, error.ToString()); Assert.Equal(new[] { "version" }, Trace(options));
    }

    [Fact]
    public async Task Selected_API_authentication_replaces_inherited_auth_and_routing_in_the_child_only()
    {
        string[] variables = ["OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL", "OPENAI_FEDERATION_RULE_ID", "ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_CUSTOM_HEADERS"];
        var previous = variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (string variable in variables) Environment.SetEnvironmentVariable(variable, "inherited-must-not-win");
            foreach (string name in new[] { "Codex", "Claude" })
            {
                using var folder = new TempFolder();
                var options = Options(folder, "auth-stream", "sk-explicit-fixture-key");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var events = await TestEnvironment.Collect(Provider(name, options).SendAsync("AUTH_STREAM", timeout.Token));
                Assert.Contains(events, item => item.Kind == "completed");
            }
            foreach (string variable in variables) Assert.Equal("inherited-must-not-win", Environment.GetEnvironmentVariable(variable));
        }
        finally { foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }

    [Theory]
    [InlineData("Codex", false)]
    [InlineData("Codex", true)]
    [InlineData("Claude", false)]
    [InlineData("Claude", true)]
    public async Task Provider_send_captures_raw_DOCX_or_uses_the_saved_snapshot_without_extra_file_permissions(string name, bool captured)
    {
        using var folder = new TempFolder(); string path = Docx(folder);
        IReadOnlyList<ChatAttachment> attachments = [ChatAttachments.Inspect(path)];
        if (captured) { attachments = ChatAttachments.Capture(attachments); File.Delete(path); }
        var provider = Provider(name, TestEnvironment.ProviderOptions with { MaxSubagents = 0 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var events = await TestEnvironment.Collect(provider.SendAsync("DOCX_CAPTURE", timeout.Token, attachments));
        Assert.Contains(events, item => item.Kind == "completed");
    }

    [Theory]
    [InlineData("Codex")]
    [InlineData("Claude")]
    public async Task Provider_followup_captures_DOCX_before_writing_the_live_input(string name)
    {
        using var folder = new TempFolder(); string path = Docx(folder);
        var provider = Provider(name, TestEnvironment.ProviderOptions with { MaxSubagents = 0 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<InputReceipt>? receipt = null; bool completed = false;
        await foreach (var item in provider.SendAsync("STEER", timeout.Token))
        {
            if (item.Kind == "input_ready" && receipt is null)
            {
                receipt = provider.SteerAsync(new("Уточнение ✓", [ChatAttachments.Inspect(path)]), timeout.Token);
                File.Delete(path); // The caller's snapshot must already be independent of the original file.
            }
            completed |= item.Kind == "completed";
        }
        Assert.True(completed); Assert.NotNull(receipt); Assert.Equal("accepted", (await receipt).State);
    }

    private static string Docx(TempFolder folder)
    {
        string path = folder.File("brief.docx");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var stream = archive.CreateEntry(name).Open(); using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(text);
        }
        Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Add("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"main\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Add("word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Документ ✓</w:t></w:r></w:p></w:body></w:document>");
        return path;
    }
}
