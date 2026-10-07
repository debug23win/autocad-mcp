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
                if (char.IsDigit(code) && i + 4 < value.Length && char.IsDigit(value[i + 3]) && char.IsDigit(value[i + 4]))
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
                case 'P' or 'N': result.Append('\n'); break;
                case '~': result.Append(' '); break;
                case '\\' or '{' or '}': result.Append(code); break;
                case 'L' or 'l' or 'O' or 'o' or 'K' or 'k': break;
                case 'U' or 'u': result.Append('\\').Append(code); break;
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

    /// <summary>Case-insensitive search in displayed text.</summary>
    public static bool Contains(string? haystack, string needle, bool mtext = false) =>
        Normalize(haystack, mtext).Contains(Normalize(needle), StringComparison.OrdinalIgnoreCase);

    private static readonly Regex NumberPattern = new(@"(?<![\d.,])[-+]?\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant);
    private static readonly Regex GroupedPattern = new(@"(?<![\d.,])[-+]?\d{1,3}(?:[ \u00A0\u202F]\d{3})+(?:[.,]\d+)?(?!\d)", RegexOptions.CultureInvariant);
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

    /// <summary>
    /// AutoCAD-style name pattern, case-insensitive: * any text, ? one character, # one digit, a comma separates
    /// alternatives and a leading ~ negates the whole pattern ("Сети*,ВК?", "~0").
    /// </summary>
    public static bool Like(string? value, string pattern)
    {
        if (value is null) return false;
        bool negate = pattern.StartsWith('~');
        var body = negate ? pattern[1..] : pattern;
        bool matched = body.Split(',').Any(part =>
            Regex.IsMatch(value, "^" + string.Concat(part.Trim().Select(c => c switch { '*' => ".*", '?' => ".", '#' => "[0-9]", _ => Regex.Escape(c.ToString()) })) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1)));
        return matched != negate;
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
