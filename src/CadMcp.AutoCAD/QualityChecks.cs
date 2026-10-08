using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

/// <summary>Projects AutoCAD curves to XY polylines for the topology check, with the chord deviation of the sampling.</summary>
internal static class CurveSampler
{
    private const int MaxPoints = 4096;

    public static bool IsCurve(Entity entity) => entity is Line or Arc or Circle or Polyline or Polyline2d or Polyline3d or Ellipse or Spline;

    /// <summary>Sampled curve, or null for other entities and unreadable geometry.</summary>
    public static TopologyCurve? Sample(Entity entity, double tolerance)
    {
        try
        {
            string handle = entity.Handle.ToString(), layer = entity.Layer;
            switch (entity)
            {
                case Line line:
                    return new(handle, layer, [[line.StartPoint.X, line.StartPoint.Y], [line.EndPoint.X, line.EndPoint.Y]], false);
                case Arc arc when Planar(arc.Normal):
                {
                    var points = ArcSampling.Arc(arc.Center.X, arc.Center.Y, arc.Radius, arc.StartAngle, arc.EndAngle, tolerance);
                    // Exact ends, so lines snapped to the arc's ends coincide with it.
                    points[0] = [arc.StartPoint.X, arc.StartPoint.Y]; points[^1] = [arc.EndPoint.X, arc.EndPoint.Y];
                    return new(handle, layer, points, false, ArcSampling.Sagitta(arc.Radius, arc.TotalAngle, points.Length - 1));
                }
                case Circle circle when Planar(circle.Normal):
                {
                    var ring = ArcSampling.Arc(circle.Center.X, circle.Center.Y, circle.Radius, 0, 2 * Math.PI, tolerance);
                    return new(handle, layer, ring[..^1], true, ArcSampling.Sagitta(circle.Radius, 2 * Math.PI, ring.Length - 1));
                }
                case Polyline polyline when Planar(polyline.Normal):
                {
                    var points = new List<double[]>();
                    // Straight segments are exact; only the chords of arc segments deviate from the curve.
                    var segmentDeviations = new List<double>();
                    double deviation = 0;
                    int count = polyline.NumberOfVertices;
                    for (int i = 0; i < count; i++)
                    {
                        var start = polyline.GetPoint2dAt(i);
                        if (i == 0) points.Add([start.X, start.Y]);
                        if (i == count - 1 && !polyline.Closed) break;
                        var end = polyline.GetPoint2dAt((i + 1) % count);
                        double bulge = polyline.GetBulgeAt(i);
                        var segment = ArcSampling.Bulge(start.X, start.Y, end.X, end.Y, bulge, tolerance);
                        points.AddRange(segment);
                        double chord = start.GetDistanceTo(end), theta = 4 * Math.Atan(Math.Abs(bulge)), sagitta = 0;
                        if (theta > 1e-12 && chord > 0) sagitta = ArcSampling.Sagitta(chord / (2 * Math.Sin(theta / 2)), theta, segment.Length);
                        deviation = Math.Max(deviation, sagitta);
                        segmentDeviations.AddRange(Enumerable.Repeat(sagitta, segment.Length));
                    }
                    // The closing segment returns to the first vertex; the curve is marked closed instead.
                    if (polyline.Closed && points.Count > 1) points.RemoveAt(points.Count - 1);
                    return new(handle, layer, points, polyline.Closed, deviation, segmentDeviations);
                }
                case Arc or Circle or Polyline or Polyline2d or Polyline3d or Ellipse or Spline:
                    return Adaptive((Curve)entity, tolerance);
                default: return null;
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
    }

    // Normal +Z: object coordinates equal WCS in XY, so angles and bulges apply directly.
    private static bool Planar(Vector3d normal) => normal.Z > 0 && normal.IsParallelTo(Vector3d.ZAxis);

    /// <summary>Recursive subdivision in parameter space until the curve stays within tolerance of each chord.</summary>
    private static TopologyCurve Adaptive(Curve curve, double tolerance)
    {
        double start = curve.StartParam, end = curve.EndParam;
        bool closed = curve.Closed;
        // Polyline parameters count vertices, so every vertex starts its own interval.
        int intervals = curve is Polyline or Polyline2d or Polyline3d ? (int)Math.Clamp(Math.Round(end - start), 1, MaxPoints / 4)
            : curve is Spline spline ? Math.Clamp(spline.NumControlPoints * 2, 16, 256) : 32;
        var points = new List<double[]>();
        var segmentDeviations = new List<double>();
        double deviation = 0;
        Point3d At(double t) => curve.GetPointAtParameter(Math.Clamp(t, start, end));
        static double Off(Point3d a, Point3d b, Point3d p)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, length2 = dx * dx + dy * dy;
            double t = length2 <= 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length2, 0, 1);
            double x = a.X + dx * t - p.X, y = a.Y + dy * t - p.Y;
            return Math.Sqrt(x * x + y * y);
        }
        void Split(double a, double b, Point3d pa, Point3d pb, int depth)
        {
            var q1 = At(a + (b - a) / 4); var mid = At((a + b) / 2); var q3 = At(a + (b - a) * 3 / 4);
            double off = Math.Max(Off(pa, pb, mid), Math.Max(Off(pa, pb, q1), Off(pa, pb, q3)));
            if (off > tolerance && depth < 12 && points.Count < MaxPoints)
            {
                Split(a, (a + b) / 2, pa, mid, depth + 1);
                Split((a + b) / 2, b, mid, pb, depth + 1);
                return;
            }
            deviation = Math.Max(deviation, off);
            segmentDeviations.Add(off);
            points.Add([pb.X, pb.Y]);
        }
        var previous = curve.StartPoint;
        points.Add([previous.X, previous.Y]);
        for (int i = 1; i <= intervals; i++)
        {
            double a = start + (end - start) * (i - 1) / intervals, b = start + (end - start) * i / intervals;
            var pb = i == intervals ? curve.EndPoint : At(b);
            Split(a, b, previous, pb, 0);
            previous = pb;
        }
        if (closed && points.Count > 1) points.RemoveAt(points.Count - 1);
        return new(curve.Handle.ToString(), curve.Layer, points, closed, deviation, segmentDeviations);
    }
}

/// <summary>
/// Glyph coverage of text styles. A character missing from an SHX font is drawn as "?"; with a
/// Cyrillic text in txt.shx the sheet is unreadable although the database text is correct.
/// </summary>
internal static class FontCoverage
{
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, ShxGlyphs? Glyphs)> Shx = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, HashSet<int>? Codes)> TrueType = new(StringComparer.OrdinalIgnoreCase);

    public static string? Resolve(Database db, string font)
    {
        if (string.IsNullOrWhiteSpace(font)) return null;
        try
        {
            if (Path.IsPathFullyQualified(font) && File.Exists(font)) return font;
            var windows = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), Path.GetFileName(font));
            if (File.Exists(windows)) return windows;
            var found = HostApplicationServices.Current.FindFile(font, db, FindFileHint.FontFile);
            return File.Exists(found) ? found : null;
        }
        catch (System.Exception) { return null; }
    }

    /// <summary>Characters the style's font cannot draw; null when the font cannot be analyzed (big font, inline font changes, unknown file).</summary>
    public static IReadOnlyList<char>? Missing(Database db, TextStyleTableRecord style, string displayed, int codePage)
    {
        if (!string.IsNullOrWhiteSpace(style.BigFontFileName)) return null;
        var path = Resolve(db, style.FileName);
        if (path is null) return null;
        var stamp = File.GetLastWriteTimeUtc(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".shx")
        {
            var entry = Shx.AddOrUpdate(path, _ => (stamp, Load(path)), (_, old) => old.Stamp == stamp ? old : (stamp, Load(path)));
            return entry.Glyphs?.Missing(displayed, codePage);
        }
        if (extension is ".ttf" or ".otf")
        {
            var entry = TrueType.AddOrUpdate(path, _ => (stamp, Glyphs(path)), (_, old) => old.Stamp == stamp ? old : (stamp, Glyphs(path)));
            if (entry.Codes is null) return null;
            return displayed.Distinct().Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && !char.IsSurrogate(c) && !entry.Codes.Contains(c)).ToArray();
        }
        return null;
    }

    private static ShxGlyphs? Load(string path)
    {
        try { var info = new FileInfo(path); return info.Length > 32 * 1024 * 1024 ? null : ShxGlyphs.Parse(File.ReadAllBytes(path)); }
        catch (System.Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static HashSet<int>? Glyphs(string path)
    {
#if CORE_CONSOLE
        return null;
#else
        try { return new System.Windows.Media.GlyphTypeface(new Uri(path)).CharacterToGlyphMap.Keys.ToHashSet(); }
        catch (System.Exception e) when (e is IOException or UriFormatException or ArgumentException or NotSupportedException or FileFormatException or UnauthorizedAccessException) { return null; }
#endif
    }

    /// <summary>ANSI code page used for regular SHX fonts; DWGCODEPAGE stores the same value as SYSCODEPAGE.</summary>
    public static int DrawingCodePage()
    {
        try
        {
            var value = Convert.ToString(Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("SYSCODEPAGE")) ?? "";
            var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
            return int.TryParse(digits, out int page) ? page : 0;
        }
        catch (System.Exception) { return 0; }
    }

    /// <summary>Displayed text and style of a text-bearing entity; null for MText with inline font changes.</summary>
    public static (string Text, ObjectId Style)? TextOf(Entity entity) => entity switch
    {
        DBText text => (CadText.Normalize(text.TextString), text.TextStyleId),
        MText mtext when !mtext.Contents.Contains("\\f", StringComparison.Ordinal) && !mtext.Contents.Contains("\\F", StringComparison.Ordinal) => (CadText.Normalize(mtext.Text), mtext.TextStyleId),
        _ => null
    };
}

/// <summary>Dimension text that hides or contradicts the measured value.</summary>
internal static class DimensionChecks
{
    public static IEnumerable<(string Code, string Severity, string Message)> Check(Dimension dimension, Transaction tr)
    {
        if (dimension is OrdinateDimension) yield break;
        bool angular = dimension is LineAngularDimension2 or Point3AngularDimension;
        if (!angular && Math.Abs(dimension.Measurement) < 1e-9)
            yield return ("DIMENSION_ZERO", "warning", "Dimension measures zero; its definition points coincide");
        var displayed = Displayed(dimension, tr);
        if (CadText.RepeatedDiameter(dimension.DimensionText, mtext: true) || CadText.RepeatedDiameter(displayed, mtext: true))
            yield return ("DIMENSION_DIAMETER_REPEATED", "warning", "Two diameter signs in a row: the style or dimension type already adds Ø; remove the typed %%c");
        if (angular || string.IsNullOrWhiteSpace(dimension.DimensionText)) yield break;
        int precision; double rounding, factor; int units;
        using (var style = dimension.GetDimstyleData()) { precision = style.Dimdec; rounding = style.Dimrnd; units = style.Dimlunit; factor = style.Dimlfac; }
        // DIMLFAC scales the displayed value; a negative value applies only to dimensions in paper space.
        bool paper = tr.GetObject(dimension.OwnerId, OpenMode.ForRead) is BlockTableRecord { IsLayout: true } owner &&
            !string.Equals(owner.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase);
        double scale = factor > 1e-12 ? factor : factor < -1e-12 && paper ? -factor : 1;
        double shown = dimension.Measurement * scale;
        // Only decimal units are compared numerically; architectural and fractional texts are reported as fixed.
        string? code = units is 2 or 6 ? CadText.DimensionOverride(dimension.DimensionText, shown, precision, rounding)
            : dimension.DimensionText.Contains("<>", StringComparison.Ordinal) ? null : "DIMENSION_TEXT_FIXED";
        if (code == "DIMENSION_TEXT_MISMATCH")
            yield return (code, "warning", "Dimension text \"" + CadText.Normalize(dimension.DimensionText, true) + "\" does not show the measured " +
                shown.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture) + "; use <> or correct the geometry");
        else if (code == "DIMENSION_TEXT_FIXED")
            yield return (code, "info", "Dimension text is typed and will not follow geometry changes; prefer <> with a prefix or suffix");
        else if (code == "DIMENSION_TEXT_REPLACED")
            yield return (code, "info", "Dimension shows text instead of its measurement");
    }

    private static string Displayed(Dimension dimension, Transaction tr)
    {
        try
        {
            if (dimension.DimBlockId.IsNull || tr.GetObject(dimension.DimBlockId, OpenMode.ForRead) is not BlockTableRecord block) return "";
            var parts = new List<string>();
            foreach (ObjectId id in block)
            {
                if (parts.Count >= 16) break;
                var item = tr.GetObject(id, OpenMode.ForRead);
                if (item is MText m) parts.Add(m.Contents); else if (item is DBText t) parts.Add(t.TextString);
            }
            return string.Join("\\P", parts);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return ""; }
    }
}

/// <summary>
/// cad_review with options_json: the scope (handles, current space, model or a named layout), a layer filter
/// and the checks to run. "standard" is the selected-scope review (up to 250 entities); "topology" checks the
/// linework of the scope as a network: dangling ends, near misses, T-junctions, crossings without a node,
/// overlaps, duplicates and self-intersections.
/// </summary>
internal static class ReviewOptions
{
    private static readonly string[] Known = ["checks", "scope", "layout_name", "layers", "tolerance", "gap_tolerance", "endpoints", "crossings", "max_entities", "max_findings"];

    public static object Run(Database db, Transaction tr, string optionsJson, ObjectId[]? handles, CancellationToken ct)
    {
        using var parsed = JsonDocument.Parse(optionsJson);
        var options = parsed.RootElement;
        if (options.ValueKind != JsonValueKind.Object) throw Invalid("options_json must be an object");
        foreach (var property in options.EnumerateObject())
            if (!Known.Contains(property.Name)) throw Invalid("Unknown option " + property.Name + "; expected " + string.Join(", ", Known));
        var checks = Strings(options, "checks") ?? ["standard"];
        if (checks.Length == 0 || checks.Any(c => c is not ("standard" or "topology"))) throw Invalid("checks must list standard and/or topology");
        var layers = Strings(options, "layers");
        if (layers is { Length: 0 or > 50 }) throw Invalid("layers must list 1..50 names or patterns (* ? # , ~)");

        string scope = handles is not null ? "handles" : options.Text("scope") ?? "current";
        string? layoutName = options.Text("layout_name");
        ObjectId[] ids;
        if (handles is not null) ids = handles;
        else
        {
            ObjectId space = scope switch
            {
                "current" => db.CurrentSpaceId,
                "model" => ((BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace],
                "layout" => LayoutSpace(db, tr, layoutName ?? throw Invalid("scope layout requires layout_name")),
                _ => throw Invalid("scope must be current, model or layout")
            };
            ids = ((BlockTableRecord)tr.GetObject(space, OpenMode.ForRead)).Cast<ObjectId>().Where(id => !id.IsErased).ToArray();
        }
        var entities = new List<Entity>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (id.IsNull || id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
            if (layers is null || layers.Any(pattern => CadText.Like(entity.Layer, pattern))) entities.Add(entity);
        }

        DrawingQuality.Report? standard = null;
        if (checks.Contains("standard"))
        {
            if (entities.Count > 250) throw new CadFault("REVIEW_TOO_LARGE", entities.Count + " entities in scope; the standard review takes up to 250, narrow it with handles or layers, or run only the topology check");
            standard = DrawingQuality.Review(db, tr, entities.Select(e => e.ObjectId), ct);
        }
        var topology = checks.Contains("topology") ? Network(entities, options, ct) : default;
        string state = new[] { standard?.State ?? "passed", topology.State ?? "passed" }.OrderBy(s => s switch { "unverified" => 0, "review_required" => 1, _ => 2 }).First();
        return new
        {
            state, checks, scope = new { kind = scope, layout_name = scope == "layout" ? layoutName : null, layers, entities = entities.Count },
            entities = standard?.Entities, solid_pairs = standard?.SolidPairs, issues = standard?.Issues, limitations = standard?.Limitations, topology = topology.Body
        };
    }

    private static (string? State, object? Body) Network(List<Entity> entities, JsonElement options, CancellationToken ct)
    {
        int maxEntities = Integer(options, "max_entities", 1, 50_000, 20_000), maxFindings = Integer(options, "max_findings", 1, 5_000, 500);
        var curves = entities.Where(CurveSampler.IsCurve).ToArray();
        if (curves.Length > maxEntities)
            throw new CadFault("REVIEW_TOO_LARGE", curves.Length + " curves in scope; narrow it with layers or raise max_entities (up to 50000)");
        double diagonal = DrawingQuality.Diagonal(curves);
        double tolerance = Number(options, "tolerance") ?? Math.Max(1e-6, diagonal * 1e-9);
        double gap = Number(options, "gap_tolerance") ?? 0;
        if (!double.IsFinite(tolerance) || tolerance <= 0) throw Invalid("tolerance must be a positive distance in drawing units");
        if (!double.IsFinite(gap) || gap < 0) throw Invalid("gap_tolerance must be zero or a positive distance in drawing units");
        // Arcs and splines are sampled more coarsely than the tolerance; each curve reports its chord deviation,
        // so an endpoint lying on an arc between samples still counts as touching it.
        double sampling = Math.Max(tolerance, diagonal * 1e-5);
        var sampled = new List<TopologyCurve>(curves.Length);
        var unreadable = new List<string>();
        long sampledPoints = 0;
        int skipped = 0;
        foreach (var curve in curves)
        {
            ct.ThrowIfCancellationRequested();
            // The check takes up to 200000 segments; sampling stops once that is exceeded.
            if (sampledPoints > 250_000) { skipped++; continue; }
            if (CurveSampler.Sample(curve, sampling) is { } sample) { sampled.Add(sample); sampledPoints += sample.Points.Count; } else unreadable.Add(curve.Handle.ToString());
        }
        var report = skipped > 0
            ? new TopologyReport("unverified", sampled.Count, 0, new Dictionary<string, int> { ["TOPOLOGY_LIMIT"] = 1 },
                [new("TOPOLOGY_LIMIT", Topology.Unverified, [], [0, 0], null, 1, "Curves too dense for one check (over 250000 sampled points; " + skipped + " curves not sampled); narrow the scope by layers")], false, [])
            : Topology.Analyze(sampled, new(tolerance, gap, Bool(options, "endpoints", true), Bool(options, "crossings", true), MaxFindings: maxFindings), ct);
        string state = unreadable.Count > 0 && report.State == "passed" ? "unverified" : report.State;
        return (state, new
        {
            state, curves = report.Curves, segments = report.Segments, counts = report.Counts, findings = report.Findings, truncated = report.Truncated,
            tolerance, gap_tolerance = gap, sampling_tolerance = sampling, other_entities = entities.Count - curves.Length,
            unreadable = unreadable.Count == 0 ? null : unreadable.Take(100).ToArray(), skipped_by_limit = skipped == 0 ? (int?)null : skipped,
            limitations = report.Limitations.Concat(["Network checks treat every curve of the scope alike; filter by layers to check one network",
                "DANGLING_END, T_JUNCTION and UNNODED_CROSSING are notes: open ends and crossings are often intended"]).ToArray()
        });
    }

    private static ObjectId LayoutSpace(Database db, Transaction tr, string name)
    {
        var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        if (!layouts.Contains(name)) throw new CadFault("LAYOUT_NOT_FOUND", name);
        return ((Layout)tr.GetObject(layouts.GetAt(name), OpenMode.ForRead)).BlockTableRecordId;
    }

    private static CadFault Invalid(string message) => new("INVALID_REVIEW_OPTIONS", message);

    private static string[]? Strings(JsonElement options, string name)
    {
        if (!options.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())))
            throw Invalid(name + " must be an array of nonempty strings");
        return value.EnumerateArray().Select(v => v.GetString()!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static double? Number(JsonElement options, string name)
    {
        if (!options.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number) throw Invalid(name + " must be a number");
        return value.GetDouble();
    }

    private static int Integer(JsonElement options, string name, int minimum, int maximum, int fallback)
    {
        if (!options.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) || number < minimum || number > maximum)
            throw Invalid(name + " must be an integer from " + minimum + " to " + maximum);
        return number;
    }

    private static bool Bool(JsonElement options, string name, bool fallback)
    {
        if (!options.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Invalid(name + " must be true or false") };
    }
}
