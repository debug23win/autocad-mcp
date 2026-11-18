using CadMcp.Providers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CadMcp.Tests;

public sealed class DocxAttachmentTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string P = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string C = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.";

    [Fact]
    public void Captures_Cyrillic_paragraphs_tabs_breaks_and_table_cells_without_changing_the_file()
    {
        using var root = new TempFolder();
        Directory.CreateDirectory(root.File("!Школа 1360"));
        string path = Create(root, "!Школа 1360/Исходные данные (ред. 2).DOCX", """
            <w:p><w:r><w:t>Школа </w:t></w:r><w:r><w:t>№ 1360</w:t><w:tab/><w:t>Москва</w:t><w:br/><w:t>Этаж 2</w:t></w:r></w:p>
            <w:p><w:r><w:t>Помещения</w:t></w:r></w:p>
            <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Кабинет</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Площадь, м²</w:t></w:r></w:p></w:tc></w:tr>
            <w:tr><w:tc><w:p><w:r><w:t>Библиотека</w:t></w:r></w:p><w:p><w:r><w:t>2-й этаж</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>48,5</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            """);
        byte[] before = SHA256.HashData(File.ReadAllBytes(path));
        var inspected = ChatAttachments.Inspect(path);
        Assert.Equal(AttachmentKind.Document, inspected.Kind);
        Assert.Null(inspected.Text);
        var captured = Assert.Single(ChatAttachments.Capture([inspected]));
        Assert.Contains("Школа № 1360\tМосква\nЭтаж 2\nПомещения\n", captured.Text);
        Assert.Contains("[Table]\n[Row]\n[Cell]\nКабинет\n[/Cell]\n[Cell]\nПлощадь, м²\n[/Cell]\n[/Row]", captured.Text);
        Assert.Contains("[Cell]\nБиблиотека\n2-й этаж\n[/Cell]\n[Cell]\n48,5\n[/Cell]", captured.Text);
        Assert.EndsWith("[/Table]", captured.Text);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Single(Directory.EnumerateFiles(root.File("!Школа 1360")));
    }

    [Fact]
    public void Reads_referenced_headers_footers_and_notes_once_with_explicit_labels()
    {
        using var root = new TempFolder();
        string body = """
            <w:p><w:r><w:t>Требование</w:t><w:footnoteReference w:id="2"/><w:endnoteReference w:id="3"/></w:r></w:p>
            <w:sectPr><w:headerReference w:type="default" r:id="h1"/><w:headerReference w:type="first" r:id="h1"/><w:footerReference w:type="default" r:id="f1"/></w:sectPr>
            """;
        var extra = new Dictionary<string, string>
        {
            ["word/_rels/document.xml.rels"] = Relationships(
                Relation("h1", "header", "header1.xml") + Relation("f1", "footer", "footer1.xml") +
                Relation("n1", "footnotes", "footnotes.xml") + Relation("n2", "endnotes", "endnotes.xml")),
            ["word/header1.xml"] = $"<w:hdr xmlns:w=\"{W}\"><w:p><w:r><w:t>Задание на проектирование</w:t></w:r></w:p></w:hdr>",
            ["word/footer1.xml"] = $"<w:ftr xmlns:w=\"{W}\"><w:p><w:r><w:t>Приложение А</w:t></w:r></w:p></w:ftr>",
            ["word/footnotes.xml"] = $"<w:footnotes xmlns:w=\"{W}\"><w:footnote w:id=\"-1\" w:type=\"separator\"><w:p><w:r><w:t>separator</w:t></w:r></w:p></w:footnote><w:footnote w:id=\"2\"><w:p><w:r><w:t>Норма 2026</w:t></w:r></w:p></w:footnote><w:footnote w:id=\"9\"><w:p><w:r><w:t>unused note</w:t></w:r></w:p></w:footnote></w:footnotes>",
            ["word/endnotes.xml"] = $"<w:endnotes xmlns:w=\"{W}\"><w:endnote w:id=\"3\"><w:p><w:r><w:t>Ссылка на приложение</w:t></w:r></w:p></w:endnote></w:endnotes>"
        };
        string path = Create(root, "notes.docx", body, extra);
        string text = CaptureText(path);
        Assert.Contains("Требование[Footnote 2][Endnote 3]", text);
        Assert.Contains("[Header: word/header1.xml]\nЗадание на проектирование", text);
        Assert.Equal(1, text.Split("Задание на проектирование").Length - 1);
        Assert.Contains("[Footer: word/footer1.xml]\nПриложение А", text);
        Assert.Contains("[Footnote 2]\nНорма 2026", text);
        Assert.Contains("[Endnote 3]\nСсылка на приложение", text);
        Assert.DoesNotContain("separator", text);
        Assert.DoesNotContain("unused note", text);
    }

    [Fact]
    public void Keeps_current_text_and_link_labels_without_following_external_links_or_evaluating_fields()
    {
        using var root = new TempFolder();
        string privatePath = root.File("unattached-private.txt");
        File.WriteAllText(privatePath, "PRIVATE CONTENT MUST NOT BE READ");
        var extra = new Dictionary<string, string>
        {
            ["word/_rels/document.xml.rels"] = Relationships(Relation("link", "hyperlink", new Uri(privatePath).AbsoluteUri, true))
        };
        string path = Create(root, "links.docx", """
            <w:p><w:r><w:t>Текущий </w:t></w:r><w:del><w:r><w:t>удалённый</w:t></w:r></w:del><w:ins><w:r><w:t>текст</w:t></w:r></w:ins></w:p>
            <w:p><w:moveFrom><w:r><w:t>старое место</w:t></w:r></w:moveFrom><w:moveTo><w:r><w:t>новое место</w:t></w:r></w:moveTo></w:p>
            <w:p><w:hyperlink r:id="link"><w:r><w:t>Название ссылки</w:t></w:r></w:hyperlink><w:r><w:instrText>INCLUDETEXT private.txt</w:instrText></w:r><w:r><w:t>Сохранённый результат поля</w:t></w:r></w:p>
            """, extra);
        string text = CaptureText(path);
        Assert.Contains("Текущий текст", text);
        Assert.Contains("новое место", text);
        Assert.Contains("Название ссылкиСохранённый результат поля", text);
        Assert.DoesNotContain("удалённый", text);
        Assert.DoesNotContain("старое место", text);
        Assert.DoesNotContain("INCLUDETEXT", text);
        Assert.DoesNotContain("PRIVATE CONTENT", text);
    }

    [Fact]
    public void Reads_strict_WordprocessingML_and_the_internal_office_document_relationship()
    {
        using var root = new TempFolder();
        string path = Create(root, "strict.docx", "<w:p><w:r><w:t>Строгий формат ✓</w:t></w:r></w:p>");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.GetEntry("word/document.xml")!.Delete();
            Add(archive, "custom/body.xml", Document("<w:p><w:r><w:t>Строгий формат ✓</w:t></w:r></w:p>").Replace(W, "http://purl.oclc.org/ooxml/wordprocessingml/main"));
            archive.GetEntry("_rels/.rels")!.Delete();
            Add(archive, "_rels/.rels", Relationships(Relation("root", "officeDocument", "custom/body.xml")).Replace(R, "http://purl.oclc.org/ooxml/officeDocument/relationships"));
            archive.GetEntry("[Content_Types].xml")!.Delete();
            Add(archive, "[Content_Types].xml", $"<Types xmlns=\"{C}\"><Override PartName=\"/custom/body.xml\" ContentType=\"{ContentType}document.main+xml\"/></Types>");
        }
        Assert.Equal("Строгий формат ✓", CaptureText(path));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("perplexity")]
    [InlineData("subscription")]
    public void Every_provider_payload_gets_inline_docx_text_and_exact_manifest_without_directory_search(string provider)
    {
        using var root = new TempFolder();
        string path = Create(root, "Школа 1360.docx", "<w:p><w:r><w:t>Огнестойкость: REI 120</w:t></w:r></w:p>");
        // Also exercise raw Inspect input: AddToPrompt must never turn an uncaptured DOCX into a binary Read request.
        ChatAttachment[] files = [ChatAttachments.Inspect(path)];
        string prompt = provider switch
        {
            "claude" => JsonSerializer.SerializeToElement(LiveInput.ClaudeMessage(new("Прочитай", files), "input-docx")).GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()!,
            "codex" => JsonSerializer.SerializeToElement(LiveInput.CodexContent(new("Прочитай", files)))[0].GetProperty("text").GetString()!,
            "perplexity" => PerplexityProvider.UserInput("Прочитай", files).GetProperty("content")[0].GetProperty("text").GetString()!,
            _ => JsonSerializer.SerializeToElement(SubscriptionAcpProvider.PromptBlocks("Прочитай", files))[0].GetProperty("text").GetString()!
        };
        Assert.Contains("do not Glob/search parent or sibling directories", prompt);
        var manifest = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]).RootElement;
        var file = manifest[0];
        Assert.Equal(path, file.GetProperty("path").GetString());
        Assert.Equal("document", file.GetProperty("kind").GetString());
        Assert.Equal("Огнестойкость: REI 120", file.GetProperty("content").GetString());
        Assert.Contains("Do not Read the binary .docx", file.GetProperty("read_with").GetString());
        Assert.Contains("no OCR", file.GetProperty("read_with").GetString());
    }

    [Fact]
    public void Formatting_a_captured_document_uses_the_snapshot_without_reopening_the_file()
    {
        using var root = new TempFolder();
        string path = Create(root, "snapshot.docx", "<w:p><w:r><w:t>Исходное задание</w:t></w:r></w:p>");
        var captured = ChatAttachments.Capture([ChatAttachments.Inspect(path)]);
        File.Delete(path);
        var recaptured = ChatAttachments.Capture(captured);
        Assert.Same(captured[0], recaptured[0]);
        Assert.Contains("Исходное задание", ChatAttachments.AddToPrompt("Review", recaptured));
        // Adding Document must not renumber previously persisted kinds.
        Assert.Equal(0, (int)AttachmentKind.Image);
        Assert.Equal(1, (int)AttachmentKind.Text);
        Assert.Equal(2, (int)AttachmentKind.File);
        Assert.Equal(3, (int)AttachmentKind.Pdf);
        Assert.Equal(4, (int)AttachmentKind.Drawing);
    }

    [Fact]
    public void Provider_capture_preserves_queued_text_even_if_the_file_was_replaced()
    {
        using var root = new TempFolder();
        string path = root.File("task.txt");
        File.WriteAllText(path, "Исходное задание");
        var captured = ChatAttachments.Capture([ChatAttachments.Inspect(path)]);
        File.WriteAllText(path, "Другая задача после отправки");
        Assert.Equal("Исходное задание", Assert.Single(ChatAttachments.Capture(captured)).Text);
        Assert.Equal("Другая задача после отправки", Assert.Single(ChatAttachments.Capture([ChatAttachments.Inspect(path)])).Text);
    }

    [Fact]
    public void Restored_snapshots_still_enforce_format_and_text_budgets()
    {
        using var root = new TempFolder();
        string path = Create(root, "restore.docx", "<w:p><w:r><w:t>Сохранённое задание</w:t></w:r></w:p>");
        var original = ChatAttachments.Capture([ChatAttachments.Inspect(path)]);
        var restored = JsonSerializer.Deserialize<ChatAttachment[]>(JsonSerializer.Serialize(original))!;
        File.Delete(path);
        Assert.Equal("Сохранённое задание", Assert.Single(ChatAttachments.Capture(restored)).Text);
        Assert.Throws<InvalidDataException>(() => ChatAttachments.Capture([restored[0] with { Kind = AttachmentKind.Text }]));
        Assert.Throws<InvalidDataException>(() => ChatAttachments.Capture([restored[0] with { Text = new string('x', (int)ChatAttachments.MaximumTextBytes + 1) }]));
        var large = restored[0] with { Text = new string('x', (int)ChatAttachments.MaximumTextBytes) };
        Assert.Throws<InvalidDataException>(() => ChatAttachments.Capture([large, large, large]));
    }

    [Theory]
    [InlineData("not-zip")]
    [InlineData("truncated")]
    [InlineData("missing-body")]
    [InlineData("macro-content-type")]
    [InlineData("duplicate-part")]
    [InlineData("outside-package")]
    [InlineData("external-main")]
    public void Rejects_invalid_or_unsupported_packages_instead_of_sending_a_binary_path(string variant)
    {
        using var root = new TempFolder();
        string path = Create(root, variant + ".docx", "<w:p><w:r><w:t>Задание</w:t></w:r></w:p>");
        if (variant == "not-zip") File.WriteAllText(path, "This is not a ZIP/OOXML document");
        else if (variant == "truncated")
        { using var file = File.OpenWrite(path); file.SetLength(file.Length - 10); }
        else using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            if (variant == "missing-body")
            { archive.GetEntry("word/document.xml")!.Delete(); Add(archive, "word/document.xml", $"<w:document xmlns:w=\"{W}\"/>"); }
            else if (variant == "macro-content-type")
            { archive.GetEntry("[Content_Types].xml")!.Delete(); Add(archive, "[Content_Types].xml", $"<Types xmlns=\"{C}\"><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.ms-word.document.macroEnabled.main+xml\"/></Types>"); }
            else if (variant == "duplicate-part") Add(archive, "WORD/DOCUMENT.XML", Document("<w:p/>"));
            else
            {
                archive.GetEntry("_rels/.rels")!.Delete();
                string target = variant == "outside-package" ? "../document.xml" : "https://example.invalid/private.docx";
                Add(archive, "_rels/.rels", Relationships(Relation("root", "officeDocument", target, variant == "external-main")));
            }
        }
        var error = Assert.Throws<InvalidDataException>(() => CaptureText(path));
        Assert.Contains("DOCX", error.Message);
        Assert.Contains(Path.GetFileName(path), error.Message);
    }

    [Fact]
    public void Prohibits_DTD_and_external_entities_before_reading_their_content()
    {
        using var root = new TempFolder();
        string privatePath = root.File("private.txt");
        File.WriteAllText(privatePath, "SECRET ENTITY CONTENT");
        string path = Create(root, "entity.docx", "<w:p><w:r><w:t>&private;</w:t></w:r></w:p>");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.GetEntry("word/document.xml")!.Delete();
            Add(archive, "word/document.xml", $"<!DOCTYPE document [<!ENTITY private SYSTEM \"{new Uri(privatePath).AbsoluteUri}\">]>" + Document("<w:p><w:r><w:t>&private;</w:t></w:r></w:p>"));
        }
        var error = Assert.Throws<InvalidDataException>(() => CaptureText(path));
        Assert.Contains("DTD", error.Message);
        Assert.DoesNotContain("SECRET ENTITY CONTENT", error.ToString());
    }

    [Fact]
    public void Rejects_documents_that_exceed_the_compressed_file_limit_even_after_inspection()
    {
        using var root = new TempFolder();
        string path = Create(root, "grew.docx", "<w:p/>");
        var inspected = ChatAttachments.Inspect(path);
        using (var file = File.OpenWrite(path)) file.SetLength(ChatAttachments.MaximumDocumentBytes + 1);
        Assert.Throws<InvalidDataException>(() => ChatAttachments.Inspect(path));
        Assert.Throws<InvalidDataException>(() => ChatAttachments.Capture([inspected]));
    }

    [Fact]
    public void Rejects_excessive_inflated_XML_without_silently_truncating_text()
    {
        using var root = new TempFolder();
        string path = Create(root, "large-part.docx", "<w:p/>");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.GetEntry("word/document.xml")!.Delete();
            AddRepeated(archive, "word/document.xml", DocxTextReader.MaximumPartBytes + 1);
        }
        Assert.True(new FileInfo(path).Length < 128 * 1024);
        Assert.Throws<InvalidDataException>(() => CaptureText(path));
    }

    [Fact]
    public void Rejects_archive_expansion_over_the_package_limit_including_unread_media()
    {
        using var root = new TempFolder();
        string path = Create(root, "large-media.docx", "<w:p/>");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update)) AddRepeated(archive, "word/media/oversized.bin", DocxTextReader.MaximumExpandedBytes);
        var error = Assert.Throws<InvalidDataException>(() => CaptureText(path));
        Assert.Contains("64", error.Message);
    }

    [Fact]
    public void Rejects_excessive_entry_count_before_building_the_package_object_graph()
    {
        using var root = new TempFolder();
        string path = Create(root, "entries.docx", "<w:p/>");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            for (int i = 0; i < DocxTextReader.MaximumEntries; i++) archive.CreateEntry("unused/" + i + ".bin");
        var error = Assert.Throws<InvalidDataException>(() => CaptureText(path));
        Assert.Contains("2048", error.Message);
    }

    [Fact]
    public void Does_not_trust_a_forged_uncompressed_ZIP_size()
    {
        using var root = new TempFolder();
        string path = Create(root, "forged-length.docx", "<w:p><w:r><w:t>Полное содержимое</w:t></w:r></w:p>");
        byte[] bytes = File.ReadAllBytes(path);
        byte[] name = Encoding.UTF8.GetBytes("word/document.xml");
        for (int i = 0; i <= bytes.Length - 46; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) != 0x02014b50) continue;
            int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 28));
            if (length == name.Length && bytes.AsSpan(i + 46, length).SequenceEqual(name))
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24), 1);
        }
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => CaptureText(path));
    }

    [Fact]
    public void Bounds_XML_depth_and_extracted_UTF8_text()
    {
        using var root = new TempFolder();
        string nested = string.Concat(Enumerable.Repeat("<w:sdt>", DocxTextReader.MaximumDepth + 1)) + "<w:p/>" + string.Concat(Enumerable.Repeat("</w:sdt>", DocxTextReader.MaximumDepth + 1));
        string deepPath = Create(root, "deep.docx", nested);
        Assert.Throws<InvalidDataException>(() => CaptureText(deepPath));
        string textPath = Create(root, "long-text.docx", "<w:p><w:r><w:t>" + new string('Я', checked((int)ChatAttachments.MaximumTextBytes / 2 + 1)) + "</w:t></w:r></w:p>");
        var error = Assert.Throws<InvalidDataException>(() => CaptureText(textPath));
        Assert.Contains("512", error.Message);
    }

    [Fact]
    public void Counts_extracted_text_together_with_text_attachments_using_UTF8_size()
    {
        using var root = new TempFolder();
        string body = "<w:p><w:r><w:t>" + new string('Я', 200000) + "</w:t></w:r></w:p>";
        string doc1 = Create(root, "one.docx", body), doc2 = Create(root, "two.docx", body);
        File.WriteAllText(root.File("text.txt"), new string('x', 300000), new UTF8Encoding(false));
        var files = new[] { doc1, doc2, root.File("text.txt") }.Select(ChatAttachments.Inspect).ToArray();
        Assert.All(files.Take(2), file => Assert.True(file.Size < 10000));
        Assert.Throws<InvalidDataException>(() => ChatAttachments.Capture(files));
    }

    private static string CaptureText(string path) => Assert.Single(ChatAttachments.Capture([ChatAttachments.Inspect(path)])).Text!;
    private static string Document(string body) => $"<w:document xmlns:w=\"{W}\" xmlns:r=\"{R}\"><w:body>{body}</w:body></w:document>";
    private static string Relationships(string items) => $"<Relationships xmlns=\"{P}\">{items}</Relationships>";
    private static string Relation(string id, string kind, string target, bool external = false) =>
        $"<Relationship Id=\"{id}\" Type=\"{R}/{kind}\" Target=\"{System.Security.SecurityElement.Escape(target)}\"" + (external ? " TargetMode=\"External\"" : "") + "/>";

    private static string Create(TempFolder root, string name, string body, IReadOnlyDictionary<string, string>? extra = null)
    {
        string path = root.File(name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var types = new StringBuilder($"<Types xmlns=\"{C}\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"{ContentType}document.main+xml\"/>");
        foreach (var part in extra ?? new Dictionary<string, string>())
        {
            string? kind = part.Key switch { "word/header1.xml" => "header", "word/footer1.xml" => "footer", "word/footnotes.xml" => "footnotes", "word/endnotes.xml" => "endnotes", _ => null };
            if (kind is not null) types.Append($"<Override PartName=\"/{part.Key}\" ContentType=\"{ContentType}{kind}+xml\"/>");
            Add(archive, part.Key, part.Value);
        }
        types.Append("</Types>");
        Add(archive, "[Content_Types].xml", types.ToString());
        Add(archive, "_rels/.rels", Relationships(Relation("root", "officeDocument", "word/document.xml")));
        Add(archive, "word/document.xml", Document(body));
        return path;
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.SmallestSize).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void AddRepeated(ZipArchive archive, string name, long length)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.SmallestSize).Open();
        byte[] chunk = new byte[81920];
        Array.Fill(chunk, (byte)' ');
        for (long offset = 0; offset < length; offset += chunk.Length) stream.Write(chunk, 0, (int)Math.Min(chunk.Length, length - offset));
    }
}
