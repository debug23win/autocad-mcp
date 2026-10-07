using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CadMcp.Core;

/// <summary>
/// Displayed text of AutoCAD strings: control codes (%%c, %%d, %%p, %%nnn), Unicode escapes (\U+XXXX)
/// and MText formatting. Search, review and dimension checks compare what the user sees, so "Ø20"
/// finds "%%c20" and a formatted MText.
/// </summary>
public static class CadText
{
    public const char Diameter = 'Ø', Degree = '°', PlusMinus = '±';

    /// <summary>Displayed text. With <paramref name="mtext"/>, MText formatting codes are removed as well.</summary>
    public static string Normalize(string? text, bool mtext = false)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var value = mtext ? StripMText(text) : text;
        var result = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '%' && i + 2 < value.Length && value[i + 1] == '%')
            {
                char code = char.ToLowerInvariant(value[i + 2]);
                if (code == 'c') { result.Append(Diameter); i += 2; continue; }
                if (code == 'd') { result.Append(Degree); i += 2; continue; }
                if (code == 'p') { result.Append(PlusMinus); i += 2; continue; }
                if (code is 'u' or 'o') { i += 2; continue; }
                if (code == '%') { result.Append('%'); i += 2; continue; }
                if (char.IsAsciiDigit(code) && i + 4 < value.Length && char.IsAsciiDigit(value[i + 3]) && char.IsAsciiDigit(value[i + 4]))
                { result.Append((char)int.Parse(value.AsSpan(i + 2, 3), CultureInfo.InvariantCulture)); i += 4; continue; }
            }
            if (c == '\\' && i + 6 < value.Length && (value[i + 1] is 'U' or 'u') && value[i + 2] == '+' &&
                int.TryParse(value.AsSpan(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int unicode))
            { result.Append((char)unicode); i += 6; continue; }
            result.Append(c);
        }
        return result.ToString();
    }

    /// <summary>Plain text of MText contents: formatting codes and grouping removed, paragraphs as new lines, stacks as a/b.</summary>
    public static string StripMText(string contents)
    {
        var result = new StringBuilder(contents.Length);
        for (int i = 0; i < contents.Length; i++)
        {
            char c = contents[i];
            if (c is '{' or '}') continue;
            if (c != '\\' || i + 1 >= contents.Length) { result.Append(c); continue; }
            char code = contents[++i];
            switch (code)
            {
                // \X separates the text above a dimension line from the text below it.
                case 'P' or 'N' or 'X': result.Append('\n'); break;
                case '~': result.Append(' '); break;
                case '\\' or '{' or '}': result.Append(code); break;
                case 'L' or 'l' or 'O' or 'o' or 'K' or 'k': break;
                case 'U' or 'u': result.Append('\\').Append(code); break;
                // \M+nXXXX: a double-byte character of an Asian code page.
                case 'M' or 'm' when i + 6 < contents.Length && contents[i + 1] == '+': result.Append('\uFFFD'); i += 6; break;
                case 'S':
                    int end = contents.IndexOf(';', i + 1);
                    if (end < 0) { i = contents.Length; break; }
                    result.Append(contents[(i + 1)..end].Replace('^', '/').Replace('#', '/').Trim());
                    i = end; break;
                default:
                    // \A \C \c \f \F \H \Q \T \W \p ... carry a value terminated by ';'.
                    int stop = contents.IndexOf(';', i + 1);
                    i = stop < 0 ? contents.Length : stop; break;
            }
        }
        return result.ToString();
    }

    /// <summary>Case-insensitive search in displayed text. A needle that displays as nothing (only "%%u") matches nothing.</summary>
    public static bool Contains(string? haystack, string needle, bool mtext = false) =>
        Normalize(needle) is { Length: > 0 } displayed && Normalize(haystack, mtext).Contains(displayed, StringComparison.OrdinalIgnoreCase);

    // ASCII digits only: \d also matches full-width and other Unicode digits, which double.Parse rejects.
    private static readonly Regex NumberPattern = new(@"(?<![0-9.,])[-+]?[0-9]+(?:[.,][0-9]+)?", RegexOptions.CultureInvariant);
    private static readonly Regex GroupedPattern = new(@"(?<![0-9.,])[-+]?[0-9]{1,3}(?:[ \u00A0\u202F][0-9]{3})+(?:[.,][0-9]+)?(?![0-9])", RegexOptions.CultureInvariant);
    /// <summary>Numbers of a displayed text; "1 200" yields 1, 200 and 1200, since digit grouping is ambiguous.</summary>
    public static IReadOnlyList<double> Numbers(string text) =>
        NumberPattern.Matches(text).Concat(GroupedPattern.Matches(text))
            .Select(m => double.Parse(new string(m.Value.Where(c => c is not (' ' or '\u00A0' or '\u202F')).ToArray()).Replace(',', '.'), CultureInfo.InvariantCulture))
            .Distinct().ToArray();

    /// <summary>
    /// Classifies a dimension text override against the measurement shown with the given precision:
    /// null when the measurement is displayed (empty override or "&lt;&gt;"), DIMENSION_TEXT_MISMATCH when
    /// every number differs from the measured value, DIMENSION_TEXT_FIXED when it matches but will not
    /// follow future geometry changes, DIMENSION_TEXT_REPLACED when the text has no number at all.
    /// </summary>
    public static string? DimensionOverride(string? overrideText, double measurement, int precision, double rounding = 0)
    {
        if (string.IsNullOrWhiteSpace(overrideText) || overrideText.Contains("<>", StringComparison.Ordinal)) return null;
        var numbers = Numbers(Normalize(overrideText, mtext: true));
        if (numbers.Count == 0) return "DIMENSION_TEXT_REPLACED";
        double shown = rounding > 0 ? Math.Round(measurement / rounding, MidpointRounding.AwayFromZero) * rounding : measurement;
        shown = Math.Round(shown, Math.Clamp(precision, 0, 8), MidpointRounding.AwayFromZero);
        double allowed = 0.5 * Math.Pow(10, -Math.Clamp(precision, 0, 8)) + Math.Abs(measurement) * 1e-9;
        return numbers.Any(n => Math.Abs(Math.Abs(n) - Math.Abs(shown)) <= allowed || Math.Abs(Math.Abs(n) - Math.Abs(measurement)) <= allowed)
            ? "DIMENSION_TEXT_FIXED" : "DIMENSION_TEXT_MISMATCH";
    }

    private static readonly Regex RepeatedDiameterPattern = new(@"[Ø⌀∅]\s*[Ø⌀∅]", RegexOptions.CultureInvariant);
    /// <summary>Two diameter symbols in a row, typically a style prefix plus a typed "%%c".</summary>
    public static bool RepeatedDiameter(string? displayed, bool mtext = false) => RepeatedDiameterPattern.IsMatch(Normalize(displayed, mtext));

    private static readonly ConcurrentDictionary<string, (Regex Regex, bool Negate)> LikePatterns = new(StringComparer.Ordinal);

    /// <summary>
    /// AutoCAD-style name pattern, case-insensitive: * any text, ? one character, # one digit, a comma separates
    /// alternatives, a leading ~ negates the whole pattern ("Сети*,ВК?", "~0"), and ` takes the next character
    /// literally. Other characters, including . @ and [ ], match themselves, so an exact layer name such as
    /// "КЖ.Арматура" matches only that layer.
    /// </summary>
    public static bool Like(string? value, string pattern)
    {
        if (value is null) return false;
        if (LikePatterns.Count > 1024) LikePatterns.Clear();
        var (regex, negate) = LikePatterns.GetOrAdd(pattern, CompileLike);
        try { return regex.IsMatch(value) != negate; }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static (Regex, bool) CompileLike(string pattern)
    {
        bool negate = pattern.StartsWith('~');
        var body = negate ? pattern[1..] : pattern;
        var alternatives = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c == ',') { alternatives.Add(current.ToString().Trim()); current.Clear(); continue; }
            current.Append(c);
            if (c == '`' && i + 1 < body.Length) current.Append(body[++i]);
        }
        alternatives.Add(current.ToString().Trim());
        var regex = new StringBuilder("^(?:");
        for (int a = 0; a < alternatives.Count; a++)
        {
            if (a > 0) regex.Append('|');
            var part = alternatives[a];
            for (int i = 0; i < part.Length; i++)
                regex.Append(part[i] switch
                {
                    '*' => ".*",
                    '?' => ".",
                    '#' => "[0-9]",
                    '`' when i + 1 < part.Length => Regex.Escape(part[++i].ToString()),
                    var c => Regex.Escape(c.ToString())
                });
        }
        regex.Append(")$");
        return (new Regex(regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1)), negate);
    }

    /// <summary>
    /// Replaces <paramref name="find"/> in stored AutoCAD text. Ø, ° and ± in the search also match the
    /// control codes %%c, %%d and %%p, and are written back in the form the text used. With
    /// <paramref name="mtext"/>, only plain text runs between formatting codes change, so fonts, colours,
    /// stacks and paragraphs survive; a match interrupted by formatting is not replaced.
    /// </summary>
    public static (string Text, int Count) Replace(string raw, string find, string replace, bool matchCase = false, bool wholeWord = false, bool mtext = false)
    {
        if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(find)) return (raw ?? "", 0);
        string encodedFind = Encode(find), encodedReplace = Encode(replace);
        // In MText a backslash or brace of the new text would start a formatting code.
        if (mtext) { replace = EscapeMText(replace); encodedReplace = EscapeMText(encodedReplace); }
        // The stored (encoded) form is tried first: "50%" must match the whole "50%%%", not stop inside its code.
        var pattern = (encodedFind != find ? "(?<code>" + Regex.Escape(encodedFind) + ")|" : "") + "(?<plain>" + Regex.Escape(find) + ")";
        if (wholeWord) pattern = @"(?<![\p{L}\p{N}_])(?:" + pattern + @")(?![\p{L}\p{N}_])";
        var regex = new Regex(pattern, (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        int count = 0;
        // A match must not begin or end inside a control code (%%c, %%nnn, \U+XXXX): "C" in "%%c108" is not text. A
        // rejected match is retried one character later, so "cc" is still found in "%%ccc".
        string Run(string text)
        {
            var boundaries = CodeBoundaries(text);
            var output = new StringBuilder(text.Length);
            int copied = 0;
            for (var m = regex.Match(text); m.Success; )
            {
                if (m.Length == 0 || !boundaries[m.Index] || !boundaries[m.Index + m.Length])
                {
                    m = m.Index + 1 <= text.Length ? regex.Match(text, m.Index + 1) : Match.Empty;
                    continue;
                }
                output.Append(text, copied, m.Index - copied).Append(m.Groups["code"].Success ? encodedReplace : replace);
                copied = m.Index + m.Length;
                count++;
                m = regex.Match(text, copied);
            }
            return output.Append(text, copied, text.Length - copied).ToString();
        }
        if (!mtext) return (Run(raw), count);
        var result = new StringBuilder(raw.Length);
        foreach (var (segment, isText) in MTextSegments(raw)) result.Append(isText ? Run(segment) : segment);
        return (result.ToString(), count);
    }

    private static string EscapeMText(string text) => text.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");

    /// <summary>For each position 0..length, whether it lies between characters rather than inside a control code.</summary>
    private static bool[] CodeBoundaries(string text)
    {
        var boundary = new bool[text.Length + 1];
        Array.Fill(boundary, true);
        for (int i = 0; i < text.Length; i++)
        {
            int length = 0;
            // Only the codes Normalize displays as something else; "100%% wide" is plain text.
            if (text[i] == '%' && i + 2 < text.Length && text[i + 1] == '%')
                length = char.IsAsciiDigit(text[i + 2]) && i + 4 < text.Length && char.IsAsciiDigit(text[i + 3]) && char.IsAsciiDigit(text[i + 4]) ? 5
                    : char.ToLowerInvariant(text[i + 2]) is 'c' or 'd' or 'p' or 'u' or 'o' or '%' ? 3 : 0;
            else if (text[i] == '\\' && i + 6 < text.Length && text[i + 1] is 'U' or 'u' && text[i + 2] == '+' && text.AsSpan(i + 3, 4).ToString().All(char.IsAsciiHexDigit))
                length = 7;
            for (int k = 1; k < length; k++) boundary[i + k] = false;
            if (length > 0) i += length - 1;
        }
        return boundary;
    }

    // A single % of the displayed text is stored as %%% as well; text that already holds codes is left as typed.
    private static string Encode(string text) => (text.Contains("%%", StringComparison.Ordinal) ? text : text.Replace("%", "%%%"))
        .Replace("Ø", "%%c").Replace("ø", "%%c").Replace("⌀", "%%c").Replace("∅", "%%c")
        .Replace(Degree.ToString(), "%%d").Replace(PlusMinus.ToString(), "%%p");

    /// <summary>MText contents split into plain text runs and formatting tokens, in order.</summary>
    public static IEnumerable<(string Segment, bool IsText)> MTextSegments(string contents)
    {
        var run = new StringBuilder();
        for (int i = 0; i < contents.Length; i++)
        {
            char c = contents[i];
            if (c is '{' or '}')
            {
                if (run.Length > 0) { yield return (run.ToString(), true); run.Clear(); }
                yield return (c.ToString(), false); continue;
            }
            if (c != '\\' || i + 1 >= contents.Length) { run.Append(c); continue; }
            if (run.Length > 0) { yield return (run.ToString(), true); run.Clear(); }
            char code = contents[i + 1];
            int end;
            if (code is 'U' or 'u' && i + 6 < contents.Length && contents[i + 2] == '+') end = i + 6;
            else if (code is 'M' or 'm' && i + 7 < contents.Length && contents[i + 2] == '+') end = i + 7;
            else if (code is 'P' or 'N' or '~' or 'L' or 'l' or 'O' or 'o' or 'K' or 'k' or 'X' or '\\' or '{' or '}') end = i + 1;
            else { int stop = contents.IndexOf(';', i + 2); end = stop < 0 ? contents.Length - 1 : stop; }
            yield return (contents[i..(end + 1)], false);
            i = end;
        }
        if (run.Length > 0) yield return (run.ToString(), true);
    }

    private static readonly Regex UnresolvedField = new(@"#{4,}", RegexOptions.CultureInvariant);
    /// <summary>AutoCAD displays "####" for a field it cannot evaluate.</summary>
    public static bool HasUnresolvedField(string? displayed) => displayed is not null && UnresolvedField.IsMatch(displayed);
}

/// <summary>
/// Character codes defined by an AutoCAD SHX font. Regular fonts ("shapes 1.0/1.1") index shapes by
/// code-page byte; Unicode fonts ("unifont 1.0") by Unicode code point. Big fonts are not analyzed.
/// </summary>
public sealed class ShxGlyphs
{
    public bool Unicode { get; }
    public IReadOnlySet<int> Codes { get; }
    private ShxGlyphs(bool unicode, HashSet<int> codes) { Unicode = unicode; Codes = codes; }

    /// <summary>Parses the glyph index; returns null for big fonts or anything that is not a well-formed SHX.</summary>
    public static ShxGlyphs? Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            if (Starts(data, "AutoCAD-86 shapes 1.")) return ParseShapes(data);
            if (Starts(data, "AutoCAD-86 unifont 1.0")) return ParseUnifont(data);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException) { }
        return null;
    }
    private static bool Starts(ReadOnlySpan<byte> data, string signature) =>
        data.Length > signature.Length && data[..signature.Length].SequenceEqual(Encoding.ASCII.GetBytes(signature));
    private static int U16(ReadOnlySpan<byte> data, int offset) => data[offset] | data[offset + 1] << 8;

    private static ShxGlyphs? ParseShapes(ReadOnlySpan<byte> data)
    {
        // "AutoCAD-86 shapes 1.x\r\n" then 0x1A, first and last shape number, count and an index of (number, size).
        int offset = data.IndexOf((byte)0x1A);
        if (offset < 0 || offset > 32) return null;
        offset++;
        int count = U16(data, offset + 4); offset += 6;
        if (count <= 0 || offset + count * 4 > data.Length) return null;
        var codes = new HashSet<int>();
        for (int i = 0; i < count; i++) codes.Add(U16(data, offset + i * 4));
        codes.Remove(0); // shape 0 is the font definition
        return new(false, codes);
    }

    private static ShxGlyphs? ParseUnifont(ReadOnlySpan<byte> data)
    {
        // "AutoCAD-86 unifont 1.0\r\n" then 0x1A, record count (u32), definition size (u16), definition, then
        // records of (code u16, size u16, size bytes).
        int offset = data.IndexOf((byte)0x1A);
        if (offset < 0 || offset > 32) return null;
        offset++;
        int definition = U16(data, offset + 4); offset += 6 + definition;
        var codes = new HashSet<int>();
        while (offset + 4 <= data.Length)
        {
            int code = U16(data, offset), size = U16(data, offset + 2);
            offset += 4 + size;
            if (offset > data.Length) return null;
            codes.Add(code);
        }
        return codes.Count == 0 ? null : new(true, codes);
    }

    /// <summary>
    /// Characters of <paramref name="displayed"/> the font cannot draw. AutoCAD substitutes its own
    /// symbols for %%c, %%d and %%p, so those are not checked. For regular fonts, characters outside
    /// ASCII are mapped through the drawing code page (for example 1251).
    /// </summary>
    public IReadOnlyList<char> Missing(string displayed, int codePage)
    {
        Encoding? encoding = null;
        if (!Unicode && codePage > 0)
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { encoding = null; }
        var missing = new List<char>();
        foreach (char c in displayed.Distinct())
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || char.IsSurrogate(c) || c is CadText.Diameter or CadText.Degree or CadText.PlusMinus) continue;
            int code = c;
            if (!Unicode && c > 127)
            {
                if (encoding is null) continue;
                try { var bytes = encoding.GetBytes([c]); if (bytes.Length != 1) { missing.Add(c); continue; } code = bytes[0]; }
                catch (EncoderFallbackException) { missing.Add(c); continue; }
            }
            if (!Codes.Contains(code)) missing.Add(c);
        }
        return missing;
    }
}
