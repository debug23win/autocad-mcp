using System.Diagnostics;
using System.Text.Json;
using CadMcp.Core;
using CadMcp.Providers;
using DocumentFormat.OpenXml.Packaging;

namespace CadMcp.Tests;

public sealed class ChatOutputTests
{
    [Fact]
    public void Html_chat_escapes_input_and_Word_export_embeds_images()
    {
        using var folder = new TempFolder();
        var png = folder.File("view.png");
        File.WriteAllBytes(png, Convert.FromBase64String(TestEnvironment.Png));
        var lines = new[] { new ChatLine("user", "<script>alert(1)</script>\n\nФото", Images: new[] { new ChatImage("view.png", png, 1, 1) }),
            new ChatLine("assistant", "Площадь $A=ab$ и **размер** 3,05 м", Steps: new[] { "Завершено: cad_search" }, ReasoningSummary: "Сверяю геометрию") };
        var html = ChatMarkup.ConversationHtml(lines, folder.Path);
        Assert.True(!html.Contains("<script>") && html.Contains("&lt;script&gt;"), "HTML injection escaped incorrectly");
        Assert.Contains("https://cadmcp-assets.local/view.png", html);
        Assert.True(html.Contains("class=\"math\"") && html.Contains("A=ab"), "Math lost: " + html);
        Assert.DoesNotContain("cad_search", html);
        Assert.DoesNotContain("cad_search", ChatMarkup.PlainTranscript(lines));
        var path = folder.File("chat.docx");
        ChatWordExporter.Save(path, lines);
        using var document = WordprocessingDocument.Open(path, false);
        Assert.Single(document.MainDocumentPart!.ImageParts);
        Assert.Contains("3,05 м", document.MainDocumentPart.Document.Body!.InnerText);
        Assert.DoesNotContain("cad_search", document.MainDocumentPart.Document.Body.InnerText);
        var wordErrors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(document).ToArray();
        Assert.True(wordErrors.Length == 0, "Invalid Word document: " + string.Join(";", wordErrors.Select(e => e.Description + " @ " + e.Path?.XPath)));
    }

    [Theory]
    [InlineData("![chart](https://example.com/leak?d=secret)")]
    [InlineData("![](https://example.com/leak?d=secret)")]
    [InlineData("![ref][1]\n\n[1]: https://example.com/leak?d=secret")]
    public void Model_text_never_loads_remote_images(string markdown)
    {
        var html = ChatMarkup.MarkdownHtml(markdown);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("<a href=\"https://example.com/leak?d=secret\"", html);
        Assert.Matches("<a [^>]*>[^<]+</a>", html);
    }

    [Fact]
    public void Cad_activity_is_readable_and_visual_work_requests_a_preview()
    {
        Assert.Equal("Изучаю чертёж", CadToolActivity.Describe("Выполняю mcp__cad__cad_search"));
        Assert.Equal("Изменяю чертёж", CadToolActivity.Describe("Выполняю mcp__cad__cad_edit"));
        Assert.Equal("Проверяю вид чертежа", CadToolActivity.Describe("Выполняю mcp__cad__cad_render"));
        Assert.True(CadToolActivity.ProducesVisibleResult("Завершено: mcp__cad__cad_edit"));
        Assert.False(CadToolActivity.ProducesVisibleResult("Ошибка: mcp__cad__cad_edit"));
    }

    [Fact]
    public void Word_exports_editable_engineering_equations_and_tables()
    {
        using var folder = new TempFolder();
        string path = Environment.GetEnvironmentVariable("CADMCP_DOCX_FIXTURE") ?? folder.File("native-math.docx");
        ChatWordExporter.Save(path, [new ChatLine("user", "Проверь прогиб и напряжение балки"), new ChatLine("assistant", "## Проверка балки\n\nНапряжение $\\sigma=\\frac{M}{W}$ сравниваем с допускаемым.\n\n$$ f=\\frac{5qL^4}{384EI} \\leq \\frac{L}{250} $$\n\n$$ r=\\sqrt[3]{x^2+y^2},\\quad A=\\begin{bmatrix}1&2\\\\3&4\\end{bmatrix} $$\n\n| Параметр | Значение |\n|---|---|\n| Пролёт | **5000 мм** |\n| Формула | $A=ab$ |")]);
        using var doc = WordprocessingDocument.Open(path, false);
        var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(doc).ToArray();
        Assert.True(errors.Length == 0, string.Join(";", errors.Select(e => e.Description)));
        var xml = doc.MainDocumentPart!.Document.OuterXml;
        Assert.True(xml.Contains("m:f") && xml.Contains("m:rad") && xml.Contains("m:m") && xml.Contains("m:sSup") && !xml.Contains("\\frac"), "Native OMML missing");
        Assert.Single(doc.MainDocumentPart.Document.Body!.Elements<DocumentFormat.OpenXml.Wordprocessing.Table>());
    }
}

public sealed class AttachmentTests
{
    [Fact]
    public void Dropped_files_become_bounded_text_and_image_inputs()
    {
        using var root = new TempFolder();
        File.WriteAllText(root.File("plan.txt"), "Чертёж ✓");
        File.WriteAllBytes(root.File("view.png"), Convert.FromBase64String(TestEnvironment.Png));
        File.WriteAllBytes(root.File("base.dwg"), [1, 2, 3]);
        var files = ChatAttachments.Capture(new[] { "plan.txt", "view.png", "base.dwg" }.Select(name => ChatAttachments.Inspect(root.File(name))).ToArray());
        Assert.True(files.Count == 3 && files[0].Text == "Чертёж ✓" && files[1].Kind == AttachmentKind.Image && files[2].Kind == AttachmentKind.File, "Wrong attachment types/content");
        var prompt = ChatAttachments.AddToPrompt("Review", files);
        // The model opens other files by path; photo calibration takes the image path.
        Assert.Contains(JsonSerializer.Serialize(root.File("base.dwg")), prompt);
        Assert.Contains(JsonSerializer.Serialize(root.File("view.png")), prompt);
        File.WriteAllText(root.File("bad.png"), "not an image");
        Assert.Throws<InvalidDataException>(() => ChatAttachments.Inspect(root.File("bad.png")));
    }

    [Fact]
    public void A_text_file_that_grew_too_large_is_sent_by_path_only()
    {
        using var root = new TempFolder();
        File.WriteAllText(root.File("grows.txt"), "small");
        var inspected = ChatAttachments.Inspect(root.File("grows.txt"));
        File.WriteAllText(root.File("grows.txt"), new string('x', (int)ChatAttachments.MaximumTextBytes + 10));
        // Capture inspects again and sees a file too large for text; it is then passed by path only.
        var captured = ChatAttachments.Capture([inspected]);
        Assert.Equal(AttachmentKind.File, captured[0].Kind);
        Assert.Null(captured[0].Text);
    }
}

public sealed class ChatStateTests
{
    private static ChatState State(string session = "retained-session") => new(0, "codex.exe", "claude.exe", "host.exe", "CAD", session, "Сеть ✓", "matching-key");

    [Fact]
    public void History_keeps_the_session_and_survives_a_torn_newer_file()
    {
        using var root = new TempFolder();
        var store = new ChatStateStore(root.Path); var state = State();
        store.Save(state); Assert.Equal(state, store.Load());
        var torn = root.File("chat-torn.json"); File.WriteAllText(torn, "{partial"); File.SetLastWriteTimeUtc(torn, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(state, store.Load());
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp"));
    }

    [Fact]
    public void Model_settings_keep_history_and_migrate_only_the_standard_Codex_path()
    {
        using var root = new TempFolder();
        var store = new ChatStateStore(root.Path);
        var state = new ChatState(0, "desktop.exe", "claude.exe", "host.exe", "CAD", "retained-session", "Сеть ✓", "0|desktop.exe|host.exe|CAD", "alternative-model", "high");
        store.Save(state); Assert.Equal(state, store.Load());
        var migrated = ChatStateStore.UseBundledCodex(state, "bundled.exe", "desktop.exe");
        Assert.True(migrated.SessionId == state.SessionId && migrated.AdapterKey == "0|bundled.exe|host.exe|CAD" && migrated.CodexModel == state.CodexModel, "Upgrade discarded conversation");
        Assert.Equal("custom.exe", ChatStateStore.UseBundledCodex(state with { CodexExecutable = "custom.exe" }, "bundled.exe", "desktop.exe").CodexExecutable);
        var legacy = JsonSerializer.Deserialize<ChatState>("""{"Provider":0,"CodexExecutable":"codex.exe","ClaudeExecutable":"claude.exe","Host":"host.exe","Directory":"CAD","SessionId":"legacy","Transcript":"old"}""")!;
        Assert.True(ChatStateStore.Valid(legacy) && legacy.CodexModel is null && legacy.CodexReasoningEffort is null, "Old history incompatible");
    }

    [Fact]
    public void Large_history_keeps_the_newest_messages_within_the_size_limit()
    {
        using var root = new TempFolder();
        var store = new ChatStateStore(root.Path);
        var messages = Enumerable.Range(0, 150).Select(i => new ChatLine(i % 2 == 0 ? "user" : "assistant", i + ":" + new string('я', 30000))).ToArray();
        store.Save(State() with { Messages = messages });
        var file = Directory.GetFiles(root.Path, "chat-*.json").Single();
        Assert.True(new FileInfo(file).Length <= 3 * 1024 * 1024, "History exceeded its size limit");
        var loaded = store.Load()!.Messages!;
        Assert.True(loaded.Count is > 1 and < 150, "History was not trimmed");
        Assert.StartsWith("149:", loaded[^1].Text);
        Assert.Equal(messages[^loaded.Count].Text, loaded[0].Text);
    }

    [Fact]
    public void A_chat_of_another_running_process_is_shown_without_resuming_its_session()
    {
        using var root = new TempFolder();
        // A running process other than this one: the fake Claude Code waiting for its first message.
        using var other = Process.Start(new ProcessStartInfo(TestEnvironment.FakeCli, "-p") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true })!;
        try
        {
            File.WriteAllText(root.File("chat-" + other.Id + ".json"), JsonSerializer.Serialize(State("other-session")));
            var restored = new ChatStateStore(root.Path).Load()!;
            Assert.Equal("Сеть ✓", restored.Transcript);
            Assert.Null(restored.SessionId);
        }
        finally { other.Kill(); other.WaitForExit(); }
    }

    [Fact]
    public void Files_of_finished_processes_are_pruned_beyond_a_few_fallbacks()
    {
        using var root = new TempFolder();
        var finished = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            using var process = Process.Start(new ProcessStartInfo(TestEnvironment.FakeCli) { UseShellExecute = false, RedirectStandardError = true })!;
            process.WaitForExit(); finished.Add(process.Id);
        }
        for (int i = 0; i < finished.Count; i++)
        {
            var file = root.File("chat-" + finished[i] + ".json");
            File.WriteAllText(file, JsonSerializer.Serialize(State("old-" + i)));
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-10 + i));
        }
        File.WriteAllText(root.File("chat-notes.json"), "{}");
        new ChatStateStore(root.Path).Save(State());
        var remaining = Directory.GetFiles(root.Path, "chat-*.json").Select(Path.GetFileName).ToHashSet();
        Assert.Contains("chat-" + Environment.ProcessId + ".json", remaining);
        Assert.Contains("chat-" + finished[3] + ".json", remaining);
        Assert.Contains("chat-" + finished[2] + ".json", remaining);
        Assert.DoesNotContain("chat-" + finished[0] + ".json", remaining);
        Assert.Contains("chat-notes.json", remaining);
    }
}

public sealed class SubagentPolicyTests
{
    [Fact]
    public void Custom_Codex_roles_all_receive_pinned_read_only_overrides()
    {
        using var root = new TempFolder(); Directory.CreateDirectory(Path.Combine(root.Path, ".codex", "agents"));
        File.WriteAllText(Path.Combine(root.Path, ".codex", "config.toml"), "[agents.'planner']\n[agents.\"drafter\"]\n");
        File.WriteAllText(Path.Combine(root.Path, ".codex", "agents", "inspector.toml"), "name = 'inspector'\n");
        var option = new ProviderOptions("unused", "unused", root.Path, OwnerId: "owner", CadSessionId: "session", CadDocumentId: "drawing");
        var arguments = CadSubagentPolicy.Arguments(option);
        foreach (string role in new[] { "default", "worker", "explorer", "cad_reviewer", "planner", "drafter", "inspector" })
        {
            string assignment = arguments.Single(a => a.StartsWith("agents." + role + ".config_file="));
            string path = JsonSerializer.Deserialize<string>(assignment[(assignment.IndexOf('=') + 1)..])!;
            string config = File.ReadAllText(path);
            Assert.True(config.Contains("sandbox_mode = \"read-only\"") && config.Contains("CAD_MCP_READ_ONLY = \"1\"") && config.Contains("CAD_MCP_DOCUMENT_ID = \"drawing\""), "Custom role acquired write access or lost its document scope");
        }
    }

    [Fact]
    public void An_unusual_custom_role_is_reported_instead_of_refusing_the_chat()
    {
        using var root = new TempFolder(); Directory.CreateDirectory(Path.Combine(root.Path, ".codex", "agents"));
        File.WriteAllText(Path.Combine(root.Path, ".codex", "config.toml"), "[agents.\"bad name!\"]\n");
        File.WriteAllText(Path.Combine(root.Path, ".codex", "agents", "unnamed.toml"), "description = 'no name'\n");
        var warnings = new List<string>();
        var roles = CadSubagentPolicy.Roles(new ProviderOptions("unused", "unused", root.Path), warnings);
        Assert.Contains("cad_reviewer", roles);
        Assert.DoesNotContain("bad name!", roles);
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void Primary_thread_binding_is_replaced_atomically()
    {
        var options = new ProviderOptions("unused", "unused", Path.GetTempPath(), OwnerId: Guid.NewGuid().ToString("N"), CadSessionId: "s", CadDocumentId: "d");
        string path = CadSubagentPolicy.PrimaryThreadFile(options);
        try
        {
            CadSubagentPolicy.BindPrimary(options, "first");
            // The MCP host opens the file this way while the chat may replace it.
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                CadSubagentPolicy.BindPrimary(options, "second");
            Assert.Equal("second", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
        }
        finally { File.Delete(path); }
    }
}
