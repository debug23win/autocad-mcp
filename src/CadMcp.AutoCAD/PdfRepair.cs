using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

internal static class PdfRepair
{
    /// <summary>
    /// AutoCAD 2025 pdfplot17 can emit XMP without /Length and duplicate /PageMode.
    /// Append standards-compliant replacement objects and an incremental xref;
    /// all page/content objects and drawing graphics remain byte-for-byte unchanged.
    /// </summary>
    public static bool NormalizeStructure(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        long originalLength = stream.Length;
        if (originalLength < 100) throw new CadFault("PDF_INVALID", "AutoCAD PDF is too short");
        int tailLength = (int)Math.Min(originalLength, 4 * 1024 * 1024);
        var tail = new byte[tailLength];
        stream.Position = originalLength - tailLength;
        stream.ReadExactly(tail);
        var text = Encoding.Latin1.GetString(tail);
        if (!text.Contains("%%EOF", StringComparison.Ordinal)) throw new CadFault("PDF_INVALID", "AutoCAD PDF has no EOF marker");
        if (text.Contains("%CADMCP_PDF_REPAIR", StringComparison.Ordinal)) return false;
        var trailer = Regex.Match(text, @"trailer\s*<<(?<fields>.*?)>>\s*startxref\s*(?<offset>\d+)",
            RegexOptions.Singleline | RegexOptions.RightToLeft | RegexOptions.CultureInvariant);
        if (!trailer.Success) throw new CadFault("PDF_INVALID", "AutoCAD PDF has no readable trailer");
        string fields = trailer.Groups["fields"].Value;
        string size = Find(fields, @"/Size\s+(\d+)");
        string root = Find(fields, @"/Root\s+(\d+\s+\d+\s+R)");
        int rootNumber = int.Parse(Find(root, @"^(\d+)"), System.Globalization.CultureInfo.InvariantCulture);
        var infoMatch = Regex.Match(fields, @"/Info\s+(\d+\s+\d+\s+R)");
        var idMatch = Regex.Match(fields, @"/ID\s*(\[.*?\])", RegexOptions.Singleline);
        string info = infoMatch.Success ? " /Info " + infoMatch.Groups[1].Value : "";
        string id = idMatch.Success ? " /ID " + idMatch.Groups[1].Value : "";
        var replacements = new SortedDictionary<int, byte[]>();
        var objects = Regex.Matches(text, @"(?m)^(?<number>\d+) 0 obj\r?\n(?<dictionary><<[^>]{1,500}>>)stream\r?\n", RegexOptions.CultureInvariant);
        Match? metadata = null;
        foreach (Match candidate in objects)
            if (candidate.Groups["dictionary"].Value.Contains("/Type/Metadata", StringComparison.Ordinal) &&
                candidate.Groups["dictionary"].Value.Contains("/Subtype/XML", StringComparison.Ordinal) &&
                !candidate.Groups["dictionary"].Value.Contains("/Length", StringComparison.Ordinal))
                metadata = candidate;
        if (metadata is not null)
        {
            int contentStart = metadata.Index + metadata.Length;
            int contentEnd = text.IndexOf("endstream", contentStart, StringComparison.Ordinal);
            if (contentEnd < 0) throw new CadFault("PDF_INVALID", "Metadata stream has no endstream marker");
            int number = int.Parse(metadata.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var content = tail.AsSpan(contentStart, contentEnd - contentStart);
            using var replacement = new MemoryStream();
            Write(replacement, $"{number} 0 obj\n<< /Type /Metadata /Subtype /XML /Length {content.Length} >>\nstream\n");
            replacement.Write(content);
            if (content.Length == 0 || content[^1] is not (byte)'\n') Write(replacement, "\n");
            Write(replacement, "endstream\nendobj\n");
            replacements[number] = replacement.ToArray();
        }
        var rootStart = Regex.Match(text, $@"(?m)^{rootNumber} 0 obj\r?\n", RegexOptions.CultureInvariant);
        if (rootStart.Success)
        {
            int end = text.IndexOf("endobj", rootStart.Index + rootStart.Length, StringComparison.Ordinal);
            if (end < 0) throw new CadFault("PDF_INVALID", "Catalog object is incomplete");
            string body = text[(rootStart.Index + rootStart.Length)..end].TrimEnd('\r', '\n');
            var modes = Regex.Matches(body, @"/PageMode\s*/[A-Za-z]+", RegexOptions.CultureInvariant);
            if (modes.Count > 1)
            {
                for (int i = modes.Count - 2; i >= 0; i--) body = body.Remove(modes[i].Index, modes[i].Length);
                replacements[rootNumber] = Encoding.Latin1.GetBytes($"{rootNumber} 0 obj\n{body}\nendobj\n");
            }
        }
        if (replacements.Count == 0) return false;
        try
        {
            stream.Position = originalLength;
            Write(stream, "\n%CADMCP_PDF_REPAIR\n");
            var offsets = new SortedDictionary<int, long>();
            foreach (var (number, bytes) in replacements)
            { offsets[number] = stream.Position; stream.Write(bytes); }
            long xrefOffset = stream.Position;
            Write(stream, "xref\n0 1\n0000000000 65535 f \n");
            foreach (var (number, offset) in offsets) Write(stream, $"{number} 1\n{offset:D10} 00000 n \n");
            Write(stream, $"trailer\n<< /Size {size} /Root {root}{info}{id} /Prev {trailer.Groups["offset"].Value} >>\n" +
                $"startxref\n{xrefOffset}\n%%EOF\n");
            stream.Flush(true);
            return true;
        }
        catch
        {
            stream.SetLength(originalLength);
            throw;
        }
    }

    private static string Find(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
        if (!match.Success) throw new CadFault("PDF_INVALID", "Required PDF trailer field is missing");
        return match.Groups[1].Value;
    }

    private static void Write(Stream stream, string text) => stream.Write(Encoding.ASCII.GetBytes(text));
}
