using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CadMcp.Core;

/// <summary>A single-line text for grouping into paragraphs: the start of its baseline, rotation in radians, height and measured width.</summary>
public readonly record struct TextLine(double X, double Y, double Rotation, double Height, double Width, string Style, string Layer);

/// <summary>Lines of a text after fitting, with the width factor and height scale that make them fit; Overflow when even the limits do not.</summary>
public sealed record TextFit(string[] Lines, double WidthFactor, double HeightScale, bool Overflow);

/// <summary>
/// Pure parts of translating and fitting drawing text: unit keys, writing new text into TEXT and MTEXT without breaking
/// their codes, grouping stacked TEXT lines into paragraphs, and fitting a text into its original frame by width factor,
/// then height.
/// </summary>
public static class TextTranslation
{
    private static readonly Regex HandlePattern = new(@"^[0-9A-Fa-f]{1,16}\z", RegexOptions.CultureInvariant);

    /// <summary>A handle as one spelling: upper case, without leading zeros, so "01f" and "1F" name the same object.</summary>
    private static string Canonical(string handle) { string trimmed = handle.TrimStart('0'); return (trimmed.Length == 0 ? "0" : trimmed).ToUpperInvariant(); }

    /// <summary>
    /// A unit key: a handle (TEXT, MTEXT, attribute, multileader), handle@row,column (table cell), or handles joined by "+"
    /// (a paragraph of TEXT lines from top to bottom).
    /// </summary>
    public static (string[] Handles, int Row, int Column) ParseUnit(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4000) throw new CadFault("INVALID_UNIT", "unit must be a key from cad_text_units");
        int at = key.IndexOf('@');
        if (at >= 0)
        {
            var parts = key[(at + 1)..].Split(',');
            if (!HandlePattern.IsMatch(key[..at]) || parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int row)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int column))
                throw new CadFault("INVALID_UNIT", "A table cell unit is handle@row,column: " + key);
            return ([Canonical(key[..at])], row, column);
        }
        var handles = key.Split('+');
        if (handles.Length > 200 || handles.Any(h => !HandlePattern.IsMatch(h))) throw new CadFault("INVALID_UNIT", "A unit is a handle or up to 200 handles joined by +: " + key);
        var canonical = handles.Select(Canonical).ToArray();
        if (canonical.Distinct(StringComparer.Ordinal).Count() != canonical.Length) throw new CadFault("INVALID_UNIT", "A unit names one text twice: " + key);
        return (canonical, -1, -1);
    }

    /// <summary>The one spelling of a unit key, for telling whether two keys name the same unit.</summary>
    public static string CanonicalUnit(string key)
    {
        var (handles, row, column) = ParseUnit(key);
        return row >= 0 ? CellUnit(handles[0], row, column) : string.Join("+", handles);
    }

    public static string CellUnit(string handle, int row, int column) => handle + "@" + row.ToString(CultureInfo.InvariantCulture) + "," + column.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Displayed text as TEXT stores it: one line, and percent signs written so that no "%%" pair reads as a control code.
    /// </summary>
    public static string ForText(string text)
    {
        string line = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        if (line.Contains("%%", StringComparison.Ordinal)) line = line.Replace("%", "%%%");
        // TEXT reads \U+XXXX and \M+NXXXX as characters: a backslash typed before them is written as its own code.
        return UnicodeCodeAhead.Replace(line, @"\U+005C");
    }

    private static readonly Regex UnicodeCodeAhead = new(@"\\(?=[Uu]\+[0-9A-Fa-f]{4}|[Mm]\+[0-9A-Fa-f]{5})", RegexOptions.CultureInvariant);

    // Codes that format MText; the other backslash sequences (\U+, \M+, \\, \{, \}, \~, \S stacks) show characters, and
    // \P, \N and \X break lines.
    private static bool Formatting(string segment) => segment is "{" or "}" ||
        segment.Length > 1 && segment[0] == '\\' && segment[1] is 'f' or 'F' or 'C' or 'c' or 'H' or 'Q' or 'T' or 'W' or 'A' or 'p' or 'L' or 'l' or 'O' or 'o' or 'K' or 'k';

    /// <summary>
    /// MText contents with new displayed text: the formatting that opens the contents (font, height, colour, alignment) is
    /// kept for the whole text, line breaks become paragraphs and codes in the new text are escaped. Simplified tells that
    /// formatting inside the old text (a word in another colour or font) could not be carried over.
    /// </summary>
    public static string ForMText(string contents, string text, out bool simplified)
    {
        var segments = CadText.MTextSegments(contents ?? "").ToList();
        bool Shown(int i) => segments[i].IsText ? segments[i].Segment.Trim().Length > 0 : !Formatting(segments[i].Segment) && segments[i].Segment is not ("\\P" or "\\N" or "\\X");
        int first = -1, last = -1;
        for (int i = 0; i < segments.Count; i++) if (Shown(i)) { if (first < 0) first = i; last = i; }
        // Each brace's partner, so a group that closes before the last character can be told from one that holds it all.
        var partner = Enumerable.Repeat(-1, segments.Count).ToArray();
        var braces = new Stack<int>();
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i].IsText) continue;
            if (segments[i].Segment == "{") braces.Push(i);
            else if (segments[i].Segment == "}" && braces.Count > 0) { int open = braces.Pop(); partner[open] = i; partner[i] = open; }
        }
        // The opening formatting kept for the whole text: codes before the first character that are outside any group or in
        // groups still open after the last character. A group closing earlier formatted only part of the text.
        var leading = new StringBuilder();
        var kept = new Stack<bool>();
        int depth = 0;
        bool dropped = false;
        for (int i = 0; i < (first < 0 ? segments.Count : first); i++)
        {
            var (segment, isText) = segments[i];
            if (isText) continue;
            if (segment == "{")
            {
                bool keep = (partner[i] < 0 || partner[i] > last) && (kept.Count == 0 || kept.Peek());
                kept.Push(keep);
                if (keep) { leading.Append('{'); depth++; } else dropped = true;
            }
            else if (segment == "}")
            {
                if (partner[i] < 0 || kept.Count == 0) continue;
                if (kept.Pop()) { leading.Append('}'); depth--; }
            }
            else if (Formatting(segment))
            {
                if (kept.Count == 0 || kept.Peek()) leading.Append(segment); else dropped = true;
            }
        }
        simplified = dropped || first >= 0 && Enumerable.Range(first + 1, Math.Max(0, last - first)).Any(i => !segments[i].IsText && Formatting(segments[i].Segment));
        string body = CadText.EscapeMText((text ?? "").Replace("\r\n", "\n").Replace('\r', '\n'));
        if (body.Contains("%%", StringComparison.Ordinal)) body = body.Replace("%", "%%%");
        return leading + body.Replace("\n", "\\P") + new string('}', Math.Max(0, depth));
    }

    private static readonly Regex AbsoluteHeight = new(@"\\H(?<value>[0-9]*\.?[0-9]+)(?<relative>[xX]?);", RegexOptions.CultureInvariant);

    /// <summary>MText contents with every absolute \H height scaled; relative heights (\H0.8x;) follow the text height.</summary>
    public static string ScaleHeights(string contents, double factor)
    {
        var result = new StringBuilder(contents.Length);
        foreach (var (segment, isText) in CadText.MTextSegments(contents))
        {
            if (isText) { result.Append(segment); continue; }
            result.Append(AbsoluteHeight.Replace(segment, m => m.Groups["relative"].Value.Length > 0 ? m.Value
                : "\\H" + (double.Parse(m.Groups["value"].Value, CultureInfo.InvariantCulture) * factor).ToString("0.###############", CultureInfo.InvariantCulture) + ";"));
        }
        return result.ToString();
    }

    /// <summary>
    /// Groups single-line texts stacked as one paragraph: same rotation, style, layer and height, consecutive baselines
    /// 0.8–2.5 heights apart with even spacing, and left edges, centres or right edges in line. Each group lists the
    /// lines from top to bottom; a line on its own is a group of one.
    /// </summary>
    public static IReadOnlyList<int[]> Paragraphs(IReadOnlyList<TextLine> lines)
    {
        int n = lines.Count;
        var next = Enumerable.Repeat(-1, n).ToArray();
        var hasPrevious = new bool[n];
        var u = new double[n]; var v = new double[n];
        // Lines that can share a paragraph: style, layer and rotation (to a tenth of a degree) alike. Each bucket is measured
        // in the frame of its own rotation, so distant coordinates do not turn a small angle into a large offset.
        static long Tenths(double r) { double degrees = r * 180 / Math.PI % 360; if (degrees < 0) degrees += 360; return (long)Math.Round(degrees * 10) % 3600; }
        var buckets = Enumerable.Range(0, n).Where(i => lines[i].Height > 0 && double.IsFinite(lines[i].X) && double.IsFinite(lines[i].Y) && double.IsFinite(lines[i].Rotation))
            .GroupBy(i => (Style: lines[i].Style.ToUpperInvariant(), Layer: lines[i].Layer.ToUpperInvariant(), Angle: Tenths(lines[i].Rotation)));
        foreach (var bucket in buckets)
        {
            double r = lines[bucket.First()].Rotation, cos = Math.Cos(r), sin = Math.Sin(r);
            var members = bucket.ToArray();
            // A common origin keeps the projections small and exact.
            double ox = lines[members[0]].X, oy = lines[members[0]].Y;
            foreach (int i in members) { double dx = lines[i].X - ox, dy = lines[i].Y - oy; u[i] = dx * cos + dy * sin; v[i] = -dx * sin + dy * cos; }
            var sorted = members.OrderByDescending(i => v[i]).ThenBy(i => u[i]).ToArray();
            var mode = new Dictionary<int, int>(); var spacing = new Dictionary<int, double>();
            for (int k = 0; k < sorted.Length; k++)
            {
                int a = sorted[k], best = -1, bestMode = -1;
                double h = lines[a].Height, bestGap = double.MaxValue;
                for (int m = k + 1; m < sorted.Length && v[a] - v[sorted[m]] <= 2.5 * h; m++)
                {
                    int b = sorted[m];
                    double gap = v[a] - v[b];
                    if (hasPrevious[b] || gap < 0.8 * h || gap >= bestGap || Math.Abs(lines[a].Height - lines[b].Height) > 0.05 * Math.Max(lines[a].Height, lines[b].Height)) continue;
                    // The edges in line, as a mask: left 1, centre 2, right 4. A paragraph keeps an edge shared by all its lines.
                    double tolerance = 0.6 * Math.Max(lines[a].Height, lines[b].Height);
                    int edges = (Math.Abs(u[a] - u[b]) <= tolerance ? 1 : 0)
                        | (Math.Abs(u[a] + lines[a].Width / 2 - u[b] - lines[b].Width / 2) <= tolerance ? 2 : 0)
                        | (Math.Abs(u[a] + lines[a].Width - u[b] - lines[b].Width) <= tolerance ? 4 : 0);
                    if (hasPrevious[a]) edges &= mode[a];
                    if (edges == 0) continue;
                    // A paragraph keeps an even line spacing.
                    if (hasPrevious[a] && Math.Abs(gap - spacing[a]) > 0.25 * spacing[a]) continue;
                    best = b; bestGap = gap; bestMode = edges;
                }
                if (best < 0) continue;
                next[a] = best; hasPrevious[best] = true; mode[best] = bestMode; spacing[best] = bestGap;
            }
        }
        var groups = new List<int[]>();
        foreach (int start in Enumerable.Range(0, n).Where(i => !hasPrevious[i]))
        {
            var group = new List<int>();
            for (int i = start; i >= 0; i = next[i]) group.Add(i);
            // Paragraphs longer than a unit key holds are split.
            for (int offset = 0; offset < group.Count; offset += 200) groups.Add(group.Skip(offset).Take(200).ToArray());
        }
        return groups;
    }

    /// <summary>
    /// Fits text into lineCount lines of frameWidth: words wrap first; when they do not fit at the current width factor,
    /// the width factor shrinks down to minWidthFactor, then the height down to minHeightRatio. measure gives a string's
    /// width at width factor 1 and the original height. Lines beyond what the text needs stay empty.
    /// </summary>
    public static TextFit FitLines(string text, int lineCount, double frameWidth, double widthFactor, double minWidthFactor, double minHeightRatio, Func<string, double> measure)
    {
        if (lineCount < 1) throw new ArgumentOutOfRangeException(nameof(lineCount));
        text = Merged(text, lineCount);
        if (!(frameWidth > 0) || !(widthFactor > 0)) return new(Pad(Wrap(text, lineCount, double.MaxValue, measure, force: true)!, lineCount), widthFactor, 1, false);
        double floor = Math.Min(minWidthFactor, widthFactor);
        string[]? At(double factor, double scale) => Wrap(text, lineCount, frameWidth / (factor * scale), measure);
        if (At(widthFactor, 1) is { } plain) return new(Pad(plain, lineCount), widthFactor, 1, false);
        if (At(floor, 1) is not null)
        {
            // The largest width factor that still fits; fitting only gets easier as it shrinks.
            double low = floor, high = widthFactor;
            for (int i = 0; i < 40 && high - low > 1e-4 * widthFactor; i++) { double mid = (low + high) / 2; if (At(mid, 1) is null) high = mid; else low = mid; }
            return new(Pad(At(low, 1)!, lineCount), low, 1, false);
        }
        if (At(floor, minHeightRatio) is not null)
        {
            double low = minHeightRatio, high = 1;
            for (int i = 0; i < 40 && high - low > 1e-4; i++) { double mid = (low + high) / 2; if (At(floor, mid) is null) high = mid; else low = mid; }
            return new(Pad(At(floor, low)!, lineCount), floor, low, false);
        }
        // The words do not wrap into the lines: the last line takes the rest. Shrink only as far as the widest line needs,
        // and report overflow only when it does not fit even at the limits.
        var forced = Wrap(text, lineCount, frameWidth / (floor * minHeightRatio), measure, force: true)!;
        double widest = forced.Max(line => measure(line)), product = widest > 0 ? frameWidth / widest : double.MaxValue;
        if (product >= widthFactor) return new(Pad(forced, lineCount), widthFactor, 1, false);
        if (product >= floor) return new(Pad(forced, lineCount), product, 1, false);
        if (product >= floor * minHeightRatio) return new(Pad(forced, lineCount), floor, product / floor, false);
        return new(Pad(forced, lineCount), floor, minHeightRatio, true);
    }

    /// <summary>
    /// Text with at most lineCount explicit lines: the line breaks beyond them become spaces. Blank lines at the end carry
    /// no text and take no line.
    /// </summary>
    private static string Merged(string? text, int lineCount)
    {
        var all = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int count = all.Length;
        while (count > 1 && string.IsNullOrWhiteSpace(all[count - 1])) count--;
        var lines = all.Take(count).ToArray();
        return lines.Length <= lineCount ? string.Join("\n", lines) : string.Join("\n", lines.Take(lineCount - 1).Append(string.Join(" ", lines.Skip(lineCount - 1))));
    }

    private static string[] Pad(string[] lines, int count) => lines.Length >= count ? lines : lines.Concat(Enumerable.Repeat("", count - lines.Length)).ToArray();

    /// <summary>
    /// Greedy word wrap into at most lineCount lines no wider than width; null when it does not fit. With force, the words
    /// that do not fit go on the last line. Ideographs wrap between characters; explicit line breaks are kept.
    /// </summary>
    public static string[]? Wrap(string text, int lineCount, double width, Func<string, double> measure, bool force = false)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        bool tooWide = false, pendingSpace = false;
        double limit = width * (1 + 1e-9);
        // With force, the last line takes every remaining word; its line breaks become spaces.
        bool Last() => force && lines.Count >= lineCount - 1;
        foreach (var (token, spaced, breaks) in Tokens(text))
        {
            if (breaks)
            {
                if (Last()) { pendingSpace = current.Length > 0; continue; }
                lines.Add(current.ToString()); current.Clear(); pendingSpace = false;
                continue;
            }
            string candidate = current.Length == 0 ? token : current + (spaced || pendingSpace ? " " : "") + token;
            pendingSpace = false;
            double measured = measure(candidate);
            if (current.Length == 0 || Last() || measured <= limit)
            {
                if (measured > limit) tooWide = true;
                current.Clear().Append(candidate);
                continue;
            }
            lines.Add(current.ToString());
            current.Clear().Append(token);
            if (measure(token) > limit) tooWide = true;
        }
        lines.Add(current.ToString());
        return !tooWide && lines.Count <= lineCount || force ? lines.ToArray() : null;
    }

    private static IEnumerable<(string Token, bool Spaced, bool Breaks)> Tokens(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int l = 0; l < lines.Length; l++)
        {
            if (l > 0) yield return ("", false, true);
            bool spaced = false;
            var run = new StringBuilder();
            foreach (char c in lines[l])
            {
                // Spaces separate words; a no-break space joins them.
                if (char.IsWhiteSpace(c) && c is not ('\u00A0' or '\u202F' or '\u2007'))
                {
                    if (run.Length > 0) { yield return (run.ToString(), spaced, false); run.Clear(); }
                    spaced = true;
                    continue;
                }
                // Ideographs break between characters; a run of other characters stays one word.
                if (Wide(c))
                {
                    if (run.Length > 0) { yield return (run.ToString(), spaced, false); run.Clear(); spaced = false; }
                    yield return (c.ToString(), spaced, false);
                    spaced = false;
                    continue;
                }
                run.Append(c);
            }
            if (run.Length > 0) yield return (run.ToString(), spaced, false);
        }
    }

    private static bool Wide(char c) => c is >= '\u2E80' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF' or >= '\u3040' and <= '\u30FF' or >= '\uAC00' and <= '\uD7AF';
}
