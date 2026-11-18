using CadMcp.Core;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace CadMcp.Providers;

/// <summary>
/// A text snapshot of one attached DOCX. Package parts are never extracted to disk and relationships
/// are resolved only inside the bounded ZIP snapshot. Word, macros, field updates and external links
/// are not invoked. XML is streamed so a small compressed file cannot create an unbounded DOM.
/// </summary>
internal static class DocxTextReader
{
    internal const long MaximumFileBytes = 32 * 1024 * 1024;
    internal const long MaximumExpandedBytes = 64 * 1024 * 1024;
    internal const long MaximumPartBytes = 16 * 1024 * 1024;
    internal const int MaximumEntries = 2048;
    internal const int MaximumDepth = 128;
    private const long MaximumMetadataBytes = 1024 * 1024;
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string StrictWordNamespace = "http://purl.oclc.org/ooxml/wordprocessingml/main";
    private const string OfficeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string StrictOfficeRelationships = "http://purl.oclc.org/ooxml/officeDocument/relationships/";
    private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string ContentTypePrefix = "application/vnd.openxmlformats-officedocument.wordprocessingml.";

    public static string Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (file.Length > MaximumFileBytes) throw Limit("Файл DOCX больше 32 МиБ");
            var bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
            if (file.ReadByte() != -1) throw Invalid("Файл DOCX изменился во время чтения; приложите его повторно");
            ValidateDirectory(bytes);
            using var snapshot = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(snapshot, ZipArchiveMode.Read);
            var package = new Package(archive);
            string main = package.MainPart();
            var text = new BoundedText();
            var references = new References();
            package.Extract(main, "document", text, references);
            var relationships = package.Relationships(main);
            var included = new HashSet<string>(StringComparer.Ordinal) { main };
            foreach (var reference in references.Headers)
            {
                var relationship = relationships.SingleOrDefault(r => r.Id == reference.Key);
                if (relationship is null || !IsRelationship(relationship.Type, reference.Value)) throw InvalidPackage();
                string part = package.RelatedPart(main, relationship, reference.Value);
                if (!included.Add(part)) continue;
                text.Append("\n[" + (reference.Value == "header" ? "Header" : "Footer") + ": " + part + "]\n");
                package.Extract(part, reference.Value == "header" ? "hdr" : "ftr", text, new References());
            }
            foreach (var notes in new[] { (Kind: "footnotes", Ids: references.Footnotes), (Kind: "endnotes", Ids: references.Endnotes) })
            {
                if (notes.Ids.Count == 0) continue;
                var matches = relationships.Where(r => IsRelationship(r.Type, notes.Kind)).ToArray();
                if (matches.Length != 1) throw InvalidPackage();
                string part = package.RelatedPart(main, matches[0], notes.Kind);
                text.Append("\n");
                package.Extract(part, notes.Kind, text, new References(), notes.Ids);
            }
            return text.ToString().TrimEnd();
        }
        catch (DocumentException error)
        { throw new InvalidDataException(error.Message + ": " + Path.GetFileName(path), error); }
        catch (XmlException error)
        { throw new InvalidDataException(UiText.T("DOCX содержит недопустимый XML; DTD и внешние сущности запрещены") + ": " + Path.GetFileName(path), error); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or EndOfStreamException)
        { throw new InvalidDataException(UiText.T("Не удалось прочитать DOCX. Нужен исправный документ .docx без пароля; .doc и .docm не поддерживаются") + ": " + Path.GetFileName(path), error); }
    }

    // Check the central directory before ZipArchive materializes entries. An archive with hundreds of
    // thousands of empty entries must be rejected before it allocates one object per entry.
    private static void ValidateDirectory(ReadOnlySpan<byte> bytes)
    {
        int end = -1;
        for (int i = bytes.Length - 22; i >= Math.Max(0, bytes.Length - 65557); i--)
            if (U32(bytes, i) == 0x06054b50 && i + 22 + U16(bytes, i + 20) == bytes.Length) { end = i; break; }
        if (end < 0 || U16(bytes, end + 4) != 0 || U16(bytes, end + 6) != 0) throw InvalidPackage();
        int entries = U16(bytes, end + 10);
        if (entries > MaximumEntries) throw Limit("В DOCX больше 2048 частей");
        if (entries != U16(bytes, end + 8)) throw InvalidPackage();
        long size = U32(bytes, end + 12), offset = U32(bytes, end + 16);
        if (offset + size > end) throw InvalidPackage();
        int position = checked((int)offset), stop = checked((int)(offset + size)), count = 0;
        while (position < stop)
        {
            if (stop - position < 46 || U32(bytes, position) != 0x02014b50) throw InvalidPackage();
            if (++count > MaximumEntries) throw Limit("В DOCX больше 2048 частей");
            int length = 46 + U16(bytes, position + 28) + U16(bytes, position + 30) + U16(bytes, position + 32);
            if (length > stop - position) throw InvalidPackage();
            position += length;
        }
        if (count != entries) throw InvalidPackage();
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static bool IsWord(string ns) => ns is WordNamespace or StrictWordNamespace;
    private static bool IsRelationship(string value, string kind) => value == OfficeRelationships + kind || value == StrictOfficeRelationships + kind;
    private static string? WordAttribute(XmlReader reader, string name) => reader.GetAttribute(name, reader.NamespaceURI);
    private static DocumentException Invalid(string message) => new(UiText.T(message));
    private static DocumentException InvalidPackage() => Invalid("DOCX повреждён или не является поддерживаемым документом Word");
    private static DocumentException Limit(string message) => Invalid(message);
    private sealed class DocumentException(string message) : Exception(message) { }
    private sealed record Relationship(string Id, string Type, string Target, bool External);
    private sealed class References
    {
        public Dictionary<string, string> Headers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Footnotes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Endnotes { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Package
    {
        private readonly Dictionary<string, ZipArchiveEntry> parts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> overrides = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> defaults = new(StringComparer.OrdinalIgnoreCase);
        private readonly ReadBudget budget = new();

        public Package(ZipArchive archive)
        {
            if (archive.Entries.Count > MaximumEntries) throw Limit("В DOCX больше 2048 частей");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                if (!ValidPartName(entry.FullName) || !names.Add(entry.FullName)) throw InvalidPackage();
                if (entry.Length < 0 || entry.Length > MaximumExpandedBytes - expanded) throw Limit("Распакованное содержимое DOCX больше 64 МиБ");
                expanded += entry.Length;
                parts.Add(entry.FullName, entry);
            }
            ReadXml("[Content_Types].xml", MaximumMetadataBytes, reader =>
            {
                bool root = false;
                while (ReadNode(reader))
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.Depth == 0)
                    { if (reader.LocalName != "Types" || reader.NamespaceURI != ContentTypesNamespace) throw InvalidPackage(); root = true; }
                    if (reader.Depth != 1 || reader.NamespaceURI != ContentTypesNamespace) continue;
                    string type = reader.GetAttribute("ContentType") ?? throw InvalidPackage();
                    if (reader.LocalName == "Override")
                    {
                        string part = ResolvePart("", reader.GetAttribute("PartName") ?? throw InvalidPackage());
                        if (!overrides.TryAdd(part, type)) throw InvalidPackage();
                    }
                    else if (reader.LocalName == "Default")
                    {
                        string extension = reader.GetAttribute("Extension") ?? throw InvalidPackage();
                        if (!defaults.TryAdd(extension, type)) throw InvalidPackage();
                    }
                    if (overrides.Count + defaults.Count > MaximumEntries) throw Limit("В DOCX больше 2048 частей");
                }
                if (!root) throw InvalidPackage();
            });
        }

        public string MainPart()
        {
            var roots = Relationships("").Where(r => IsRelationship(r.Type, "officeDocument")).ToArray();
            if (roots.Length != 1) throw InvalidPackage();
            return RelatedPart("", roots[0], "document.main");
        }

        public string RelatedPart(string source, Relationship relationship, string kind)
        {
            if (relationship.External) throw Invalid("Внешние части документа DOCX не читаются; сохраните самостоятельную копию документа");
            string part = ResolvePart(source, relationship.Target);
            string? type = overrides.GetValueOrDefault(part) ?? defaults.GetValueOrDefault(Path.GetExtension(part).TrimStart('.'));
            if (!parts.ContainsKey(part) || type != ContentTypePrefix + kind + "+xml") throw InvalidPackage();
            return part;
        }

        public List<Relationship> Relationships(string source)
        {
            int slash = source.LastIndexOf('/');
            string part = source.Length == 0 ? "_rels/.rels" : source[..(slash + 1)] + "_rels/" + source[(slash + 1)..] + ".rels";
            var result = new List<Relationship>();
            if (!parts.ContainsKey(part))
            { if (source.Length == 0) throw InvalidPackage(); return result; }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            ReadXml(part, MaximumMetadataBytes, reader =>
            {
                bool root = false;
                while (ReadNode(reader))
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.Depth == 0)
                    { if (reader.LocalName != "Relationships" || reader.NamespaceURI != PackageRelationships) throw InvalidPackage(); root = true; }
                    if (reader.Depth != 1 || reader.LocalName != "Relationship" || reader.NamespaceURI != PackageRelationships) continue;
                    string id = reader.GetAttribute("Id") ?? throw InvalidPackage();
                    string type = reader.GetAttribute("Type") ?? throw InvalidPackage();
                    string target = reader.GetAttribute("Target") ?? throw InvalidPackage();
                    string mode = reader.GetAttribute("TargetMode") ?? "Internal";
                    if (!ids.Add(id) || mode is not ("Internal" or "External")) throw InvalidPackage();
                    result.Add(new(id, type, target, mode == "External"));
                    if (result.Count > MaximumEntries) throw Limit("В DOCX больше 2048 частей");
                }
                if (!root) throw InvalidPackage();
            });
            return result;
        }

        public void Extract(string part, string rootName, BoundedText text, References references, HashSet<string>? notes = null)
        {
            var foundNotes = new HashSet<string>(StringComparer.Ordinal);
            ReadXml(part, MaximumPartBytes, reader =>
            {
                bool root = false, body = rootName != "document";
                int bodyDepth = -1, skipDepth = -1, textDepth = -1;
                while (ReadNode(reader))
                {
                    if (reader.Depth == 0 && reader.NodeType == XmlNodeType.Element)
                    { if (reader.LocalName != rootName || !IsWord(reader.NamespaceURI)) throw InvalidPackage(); root = true; }
                    if (skipDepth >= 0)
                    { if (reader.Depth == skipDepth && reader.NodeType == XmlNodeType.EndElement) skipDepth = -1; continue; }
                    if (reader.NodeType == XmlNodeType.Element && IsWord(reader.NamespaceURI))
                    {
                        string name = reader.LocalName;
                        if (rootName == "document" && name == "body" && reader.Depth == 1)
                        { body = true; bodyDepth = reader.IsEmptyElement ? -1 : reader.Depth; continue; }
                        if (rootName == "document" && bodyDepth < 0) continue;
                        // Text deleted by tracked changes and field instructions are not the current document text.
                        if (name is "del" or "moveFrom")
                        { if (!reader.IsEmptyElement) skipDepth = reader.Depth; continue; }
                        if (notes is not null && (name is "footnote" or "endnote"))
                        {
                            string id = WordAttribute(reader, "id") ?? throw InvalidPackage();
                            string? type = WordAttribute(reader, "type");
                            if (!notes.Contains(id) || type is "separator" or "continuationSeparator" or "continuationNotice")
                            { if (!reader.IsEmptyElement) skipDepth = reader.Depth; continue; }
                            if (!foundNotes.Add(id)) throw InvalidPackage();
                            text.Append("[" + (name == "footnote" ? "Footnote " : "Endnote ") + id + "]\n");
                        }
                        switch (name)
                        {
                            case "t": textDepth = reader.IsEmptyElement ? -1 : reader.Depth; break;
                            case "tab": text.Append("\t"); break;
                            case "br": case "cr": text.Append("\n"); break;
                            case "noBreakHyphen": text.Append("\u2011"); break;
                            case "softHyphen": text.Append("\u00ad"); break;
                            case "tbl": text.Append(reader.IsEmptyElement ? "\n[Table]\n[/Table]\n" : "\n[Table]\n"); break;
                            case "tr": text.Append(reader.IsEmptyElement ? "[Row]\n[/Row]\n" : "[Row]\n"); break;
                            case "tc": text.Append(reader.IsEmptyElement ? "[Cell]\n[/Cell]\n" : "[Cell]\n"); break;
                            case "p" when reader.IsEmptyElement: text.Append("\n"); break;
                            case "headerReference": case "footerReference":
                                string relationshipId = reader.GetAttribute("id", OfficeRelationships.TrimEnd('/')) ?? reader.GetAttribute("id", StrictOfficeRelationships.TrimEnd('/')) ?? throw InvalidPackage();
                                string kind = name == "headerReference" ? "header" : "footer";
                                if (references.Headers.TryGetValue(relationshipId, out string? previous) && previous != kind) throw InvalidPackage();
                                references.Headers[relationshipId] = kind;
                                break;
                            case "footnoteReference": case "endnoteReference":
                                string noteId = WordAttribute(reader, "id") ?? throw InvalidPackage();
                                (name == "footnoteReference" ? references.Footnotes : references.Endnotes).Add(noteId);
                                text.Append("[" + (name == "footnoteReference" ? "Footnote " : "Endnote ") + noteId + "]");
                                break;
                        }
                    }
                    else if (textDepth >= 0 && (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace))
                        text.Append(reader.Value);
                    else if (reader.NodeType == XmlNodeType.EndElement && IsWord(reader.NamespaceURI))
                    {
                        if (rootName == "document" && reader.Depth == bodyDepth && reader.LocalName == "body") { bodyDepth = -1; continue; }
                        if (rootName == "document" && bodyDepth < 0) continue;
                        if (reader.Depth == textDepth) textDepth = -1;
                        switch (reader.LocalName)
                        {
                            case "p": text.Append("\n"); break;
                            case "tc": text.Append("[/Cell]\n"); break;
                            case "tr": text.Append("[/Row]\n"); break;
                            case "tbl": text.Append("[/Table]\n"); break;
                        }
                    }
                }
                if (!root || !body || (notes is not null && !notes.SetEquals(foundNotes))) throw InvalidPackage();
            });
        }

        private void ReadXml(string part, long limit, Action<XmlReader> read)
        {
            if (!parts.TryGetValue(part, out var entry)) throw InvalidPackage();
            if (entry.Length > limit) throw Limit("XML-часть DOCX превышает допустимый размер");
            using var input = new LimitedPartStream(entry.Open(), limit, budget);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = limit,
                MaxCharactersFromEntities = 1024,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });
            read(reader);
            if (input.BytesRead != entry.Length) throw InvalidPackage();
        }
    }

    private static bool ReadNode(XmlReader reader)
    {
        bool result = reader.Read();
        if (result && reader.Depth > MaximumDepth) throw Limit("XML в DOCX имеет слишком большую глубину вложенности");
        return result;
    }

    private static bool ValidPartName(string name) => name.Length is > 0 and <= 1024 &&
        !name.StartsWith('/') && !name.Any(c => c is '\\' or ':' or '?' or '#' || char.IsControl(c)) &&
        name.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));

    private static string ResolvePart(string source, string target)
    {
        string value;
        try { value = Uri.UnescapeDataString(target); }
        catch (UriFormatException) { throw InvalidPackage(); }
        if (value.StartsWith("//", StringComparison.Ordinal) || value.Any(c => c is '\\' or ':' or '?' or '#' || char.IsControl(c))) throw InvalidPackage();
        string combined = value.StartsWith('/') ? value[1..] : source[..(source.LastIndexOf('/') + 1)] + value;
        var segments = new List<string>();
        foreach (string segment in combined.Split('/'))
        {
            if (segment == ".") continue;
            if (segment == "..")
            { if (segments.Count == 0) throw InvalidPackage(); segments.RemoveAt(segments.Count - 1); }
            else if (segment.Length == 0) throw InvalidPackage();
            else segments.Add(segment);
        }
        string part = string.Join('/', segments);
        if (!ValidPartName(part)) throw InvalidPackage();
        return part;
    }

    private sealed class BoundedText
    {
        private readonly StringBuilder value = new();
        private long bytes;
        public void Append(string text)
        {
            bytes += Encoding.UTF8.GetByteCount(text);
            if (bytes > ChatAttachments.MaximumTextBytes) throw Limit("Извлечённый текст DOCX больше 512 КиБ; разделите документ на несколько файлов");
            value.Append(text);
        }
        public override string ToString() => value.ToString();
    }

    private sealed class ReadBudget { public long Bytes; }
    private sealed class LimitedPartStream(Stream inner, long limit, ReadBudget budget) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int maximum = checked((int)Math.Min(buffer.Length, Math.Min(limit - BytesRead + 1, MaximumExpandedBytes - budget.Bytes + 1)));
            int count = inner.Read(buffer[..maximum]);
            BytesRead += count; budget.Bytes += count;
            if (BytesRead > limit) throw Limit("XML-часть DOCX превышает допустимый размер");
            if (budget.Bytes > MaximumExpandedBytes) throw Limit("Распакованное содержимое DOCX больше 64 МиБ");
            return count;
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
