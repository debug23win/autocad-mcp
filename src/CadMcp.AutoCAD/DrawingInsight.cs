using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;
using Region = Autodesk.AutoCAD.DatabaseServices.Region;

namespace CadMcp.AutoCAD;

/// <summary>
/// Read-only summaries that save the agent from paging through entities: quantity takeoff, a deterministic
/// drawing outline and the outline of a DWG that is not open in AutoCAD.
/// </summary>
internal static class DrawingInsight
{
    private const int EntityLimit = 500_000;
    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static IEnumerable<(ObjectId Space, string Name)> Spaces(Database db, Transaction tr, string scope, string? layoutName)
    {
        var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        switch (scope)
        {
            case "current": yield return (db.CurrentSpaceId, SpaceName(tr, db.CurrentSpaceId)); break;
            case "model": yield return (blocks[BlockTableRecord.ModelSpace], "Model"); break;
            case "layout":
                // Read-only lookup: this summary must not open the layout for write.
                var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                string requested = layoutName ?? throw new CadFault("INVALID_PARAMETER", "scope layout requires layout_name");
                if (!layouts.Contains(requested)) throw new CadFault("LAYOUT_NOT_FOUND", requested);
                yield return (((Layout)tr.GetObject(layouts.GetAt(requested), OpenMode.ForRead)).BlockTableRecordId, requested); break;
            case "all":
                foreach (var (id, name) in Layouts(db, tr)) yield return (id, name);
                break;
            default: throw new CadFault("INVALID_PARAMETER", "scope must be current, model, layout or all");
        }
    }

    private static string SpaceName(Transaction tr, ObjectId space) =>
        tr.GetObject(space, OpenMode.ForRead) is BlockTableRecord { IsLayout: true } record ? ((Layout)tr.GetObject(record.LayoutId, OpenMode.ForRead)).LayoutName : "?";

    /// <summary>Model first, then paper layouts in tab order.</summary>
    private static IEnumerable<(ObjectId Space, string Name)> Layouts(Database db, Transaction tr) =>
        ((DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead)).Cast<DBDictionaryEntry>()
            .Select(e => (Layout)tr.GetObject(e.Value, OpenMode.ForRead))
            .OrderBy(l => l.ModelType ? -1 : l.TabOrder).ThenBy(l => l.LayoutName, StringComparer.Ordinal)
            .Select(l => (l.BlockTableRecordId, l.ModelType ? "Model" : l.LayoutName)).ToArray();

    private static string EffectiveName(BlockReference reference, Transaction tr) =>
        ((BlockTableRecord)tr.GetObject(reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord, OpenMode.ForRead)).Name;

    private static double? CurveLength(Entity entity)
    {
        try
        {
            return entity switch
            {
                Line line => line.Length,
                Circle circle => 2 * Math.PI * circle.Radius,
                Polyline polyline => polyline.Length,
                Curve curve and (Arc or Polyline2d or Polyline3d or Spline or Ellipse) => Math.Abs(curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam)),
                _ => null
            };
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return double.NaN; }
    }

    private static double? Area(Entity entity)
    {
        try
        {
            return entity switch
            {
                Circle circle => Math.PI * circle.Radius * circle.Radius,
                Polyline polyline when polyline.Closed || EndsMeet(polyline) => polyline.Area,
                Polyline2d polyline when polyline.Closed || EndsMeet(polyline) => polyline.Area,
                Ellipse { Closed: true } ellipse => ellipse.Area,
                Spline spline when spline.Closed || EndsMeet(spline) => spline.Area,
                Hatch hatch => hatch.Area,
                Region region => region.Area,
                _ => null
            };
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return double.NaN; }
    }

    /// <summary>An open curve drawn back to its start point (by object snap) encloses an area like a closed one.</summary>
    private static bool EndsMeet(Curve curve)
    {
        if (curve is Polyline { NumberOfVertices: < 3 } || curve is Polyline2d && curve.EndParam < 2) return false;
        var (start, end) = (curve.StartPoint, curve.EndPoint);
        return start.DistanceTo(end) <= 1e-9 * Math.Max(1, Math.Max(start.GetAsVector().Length, end.GetAsVector().Length)) && curve.GetDistanceAtParameter(curve.EndParam) > 0;
    }

    // ---------------------------------------------------------------- takeoff

    public static object Takeoff(Database db, Transaction tr, JsonElement options, CancellationToken ct)
    {
        string scope = options.Text("scope") ?? "current";
        var layers = options.TryGetProperty("layers_json", out var layerJson) && layerJson.ValueKind == JsonValueKind.String ? Patterns(layerJson.GetString()!) : null;
        var include = options.Text("include") is { } includeText
            ? includeText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(["lengths", "areas", "blocks"], StringComparer.OrdinalIgnoreCase);
        var unknown = include.Except(["lengths", "areas", "blocks", "attributes"], StringComparer.OrdinalIgnoreCase).ToArray();
        if (unknown.Length > 0) throw new CadFault("INVALID_PARAMETER", "include lists lengths, areas, blocks, attributes; unknown " + string.Join(", ", unknown));
        int maxRows = DraftingPlan.Integer(options, "max_rows", 1, 5000, 1000);
        bool csv = options.Text("format") is "csv";
        var lengths = new SortedDictionary<string, (int Count, double Length, SortedDictionary<string, (int Count, double Length)> Types)>(StringComparer.Ordinal);
        var areas = new SortedDictionary<string, (int Count, double Area, SortedDictionary<string, (int Count, double Area)> Types)>(StringComparer.Ordinal);
        var blocks = new SortedDictionary<string, (int Count, SortedDictionary<string, int> Layers)>(StringComparer.Ordinal);
        var rows = new List<(string Handle, string Name, string Layer, string Space, SortedDictionary<string, string> Values)>();
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        int visited = 0, unmeasured = 0;
        bool partial = false, rowsDropped = false;
        var spaceNames = new List<string>();
        foreach (var (spaceId, spaceName) in Spaces(db, tr, scope, options.Text("layout_name")))
        {
            spaceNames.Add(spaceName);
            foreach (ObjectId id in (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead))
            {
                ct.ThrowIfCancellationRequested();
                if (++visited > EntityLimit) { partial = true; break; }
                if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
                if (layers is not null && !layers.Any(p => CadText.Like(entity.Layer, p))) continue;
                string type = entity.GetRXClass().DxfName;
                if (include.Contains("lengths") && CurveLength(entity) is { } length)
                {
                    if (!double.IsFinite(length)) unmeasured++;
                    else
                    {
                        var entry = lengths.GetValueOrDefault(entity.Layer, (0, 0, new(StringComparer.Ordinal)));
                        var byType = entry.Types.GetValueOrDefault(type);
                        entry.Types[type] = (byType.Count + 1, byType.Length + length);
                        lengths[entity.Layer] = (entry.Count + 1, entry.Length + length, entry.Types);
                    }
                }
                if (include.Contains("areas") && Area(entity) is { } area)
                {
                    if (!double.IsFinite(area)) unmeasured++;
                    else
                    {
                        var entry = areas.GetValueOrDefault(entity.Layer, (0, 0, new(StringComparer.Ordinal)));
                        var byType = entry.Types.GetValueOrDefault(type);
                        entry.Types[type] = (byType.Count + 1, byType.Area + area);
                        areas[entity.Layer] = (entry.Count + 1, entry.Area + area, entry.Types);
                    }
                }
                if (entity is BlockReference reference and not Table && (include.Contains("blocks") || include.Contains("attributes")))
                {
                    string name = EffectiveName(reference, tr);
                    var entry = blocks.GetValueOrDefault(name, (0, new(StringComparer.Ordinal)));
                    entry.Layers[entity.Layer] = entry.Layers.GetValueOrDefault(entity.Layer) + 1;
                    blocks[name] = (entry.Count + 1, entry.Layers);
                    if (include.Contains("attributes") && reference.AttributeCollection.Count > 0 && rows.Count >= maxRows) rowsDropped = true;
                    else if (include.Contains("attributes") && reference.AttributeCollection.Count > 0)
                    {
                        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
                        foreach (ObjectId attributeId in reference.AttributeCollection)
                            if (tr.GetObject(attributeId, OpenMode.ForRead) is AttributeReference attribute) { values[attribute.Tag] = attribute.TextString; tags.Add(attribute.Tag); }
                        rows.Add((entity.Handle.ToString(), name, entity.Layer, spaceName, values));
                    }
                }
            }
            if (partial) break;
        }
        var lengthRows = lengths.Select(p => new { layer = p.Key, count = p.Value.Count, length = Math.Round(p.Value.Length, 6),
            by_type = p.Value.Types.ToDictionary(t => t.Key, t => new { count = t.Value.Count, length = Math.Round(t.Value.Length, 6) }) }).ToArray();
        var areaRows = areas.Select(p => new { layer = p.Key, count = p.Value.Count, area = Math.Round(p.Value.Area, 6),
            by_type = p.Value.Types.ToDictionary(t => t.Key, t => new { count = t.Value.Count, area = Math.Round(t.Value.Area, 6) }) }).ToArray();
        var blockRows = blocks.OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new { name = p.Key, count = p.Value.Count, layers = p.Value.Layers }).ToArray();
        object? csvTables = null;
        if (csv)
        {
            static string Cell(string value) => value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
            var builder = new Dictionary<string, string>();
            if (include.Contains("lengths")) builder["lengths"] = "layer;count;length\n" + string.Concat(lengths.Select(p => Cell(p.Key) + ";" + p.Value.Count + ";" + F(p.Value.Length) + "\n"));
            if (include.Contains("areas")) builder["areas"] = "layer;count;area\n" + string.Concat(areas.Select(p => Cell(p.Key) + ";" + p.Value.Count + ";" + F(p.Value.Area) + "\n"));
            if (include.Contains("blocks")) builder["blocks"] = "name;count\n" + string.Concat(blockRows.Select(b => Cell(b.name) + ";" + b.count + "\n"));
            if (include.Contains("attributes"))
                builder["attributes"] = "handle;block;layer;space;" + string.Join(";", tags.Select(Cell)) + "\n" +
                    string.Concat(rows.Select(r => r.Handle + ";" + Cell(r.Name) + ";" + Cell(r.Layer) + ";" + Cell(r.Space) + ";" + string.Join(";", tags.Select(t => Cell(r.Values.GetValueOrDefault(t) ?? ""))) + "\n"));
            if (builder.Values.Sum(v => v.Length) > 256 * 1024) throw new CadFault("RESULT_TOO_LARGE", "CSV exceeds 256 KB; narrow the scope with layers or lower max_rows");
            csvTables = builder;
        }
        return new
        {
            units = db.Insunits.ToString(), length_units = "drawing units", area_units = "square drawing units", scope, spaces = spaceNames, layers = layers,
            total_length = include.Contains("lengths") ? Math.Round(lengths.Values.Sum(v => v.Length), 6) : (double?)null,
            total_area = include.Contains("areas") ? Math.Round(areas.Values.Sum(v => v.Area), 6) : (double?)null,
            lengths = include.Contains("lengths") ? lengthRows : null, areas = include.Contains("areas") ? areaRows : null,
            blocks = include.Contains("blocks") || include.Contains("attributes") ? blockRows : null,
            attribute_columns = include.Contains("attributes") ? tags.ToArray() : null,
            attributes = include.Contains("attributes") ? rows.Select(r => new { handle = r.Handle, block = r.Name, layer = r.Layer, space = r.Space, values = r.Values }).ToArray() : null,
            attributes_truncated = include.Contains("attributes") && rowsDropped ? true : (bool?)null,
            csv = csvTables, unmeasured = unmeasured == 0 ? (int?)null : unmeasured, partial = partial ? true : (bool?)null,
            limitations = new[] { "top-level entities only; geometry inside blocks is counted as block references",
                "circle lengths are circumferences; areas use closed curves (and open ones drawn back to their start point), hatches and regions",
                "a hatch and its boundary on one layer are both counted: areas[].by_type separates them", "lengths of 3D curves are true 3D lengths" }
        };
    }

    private static string[] Patterns(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.ValueKind != JsonValueKind.Array || parsed.RootElement.GetArrayLength() is < 1 or > 50 ||
            parsed.RootElement.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())))
            throw new CadFault("INVALID_PARAMETER", "layers_json must be an array of 1..50 layer names or patterns");
        return parsed.RootElement.EnumerateArray().Select(v => v.GetString()!).ToArray();
    }

    // ---------------------------------------------------------------- outline

    /// <summary>
    /// A deterministic summary of a drawing: units, layouts with their sheets, layers, entity types, blocks,
    /// external references, styles and a sample of texts. Equal drawings give equal outlines.
    /// </summary>
    public static object Outline(Database db, Transaction tr, string? name, CancellationToken ct, int textSample = 40, int layerLimit = 100)
    {
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var layerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var layouts = new List<object>();
        var blockCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var texts = new List<(string Space, double Y, double X, string Text, string Layer)>();
        var modelTypes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int visited = 0;
        bool partial = false;
        foreach (var (spaceId, spaceName) in Layouts(db, tr))
        {
            var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
            var layout = (Layout)tr.GetObject(space.LayoutId, OpenMode.ForRead);
            var types = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int entities = 0, viewports = 0;
            var titles = new List<SortedDictionary<string, string>>();
            foreach (ObjectId id in space)
            {
                ct.ThrowIfCancellationRequested();
                if (++visited > EntityLimit) { partial = true; break; }
                if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
                entities++;
                string type = entity.GetRXClass().DxfName;
                types[type] = types.GetValueOrDefault(type) + 1;
                layerCounts[entity.Layer] = layerCounts.GetValueOrDefault(entity.Layer) + 1;
                switch (entity)
                {
                    case Viewport viewport: if (!ReadingMetadata.IsOverallViewport(viewport, tr)) viewports++; break;
                    case DBText text: texts.Add((spaceName, text.Position.Y, text.Position.X, text.TextString, text.Layer)); break;
                    case MText mtext: texts.Add((spaceName, mtext.Location.Y, mtext.Location.X, mtext.Text, mtext.Layer)); break;
                    case BlockReference reference and not Table:
                        string blockName = EffectiveName(reference, tr);
                        blockCounts[blockName] = blockCounts.GetValueOrDefault(blockName) + 1;
                        if (!layout.ModelType && reference.AttributeCollection.Count > 0)
                        {
                            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
                            foreach (ObjectId a in reference.AttributeCollection)
                                if (tr.GetObject(a, OpenMode.ForRead) is AttributeReference attribute) values[attribute.Tag] = attribute.TextString;
                            if (values.ContainsKey("DESIGNATION") || values.ContainsKey("ОБОЗНАЧЕНИЕ")) titles.Add(values);
                        }
                        break;
                }
            }
            if (layout.ModelType) foreach (var (type, count) in types) modelTypes[type] = count;
            else
            {
                var paper = layout.PlotPaperSize;
                layouts.Add(new
                {
                    name = layout.LayoutName, tab_order = layout.TabOrder, entities, viewports,
                    paper_mm = paper.X > 0 ? new[] { Math.Round(paper.X, 1), Math.Round(paper.Y, 1) } : null,
                    device = layout.PlotConfigurationName, media = layout.CanonicalMediaName, titles = titles.Count == 0 ? null : titles, types
                });
            }
            if (partial) break;
        }
        var layerRows = layerTable.Cast<ObjectId>().Select(id => (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead))
            .Select(l => new { name = l.Name, entities = layerCounts.GetValueOrDefault(l.Name), color = l.Color.ColorIndex, off = l.IsOff, frozen = l.IsFrozen, locked = l.IsLocked, dependent = l.IsDependent })
            .OrderByDescending(l => l.entities).ThenBy(l => l.name, StringComparer.Ordinal).ToArray();
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var definitions = blockTable.Cast<ObjectId>().Select(id => (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).ToArray();
        var xrefs = definitions.Where(b => b.IsFromExternalReference)
            .Select(b => new { name = b.Name, path = b.PathName, status = b.XrefStatus.ToString(), overlay = b.IsFromOverlayReference })
            .OrderBy(x => x.name, StringComparer.Ordinal).ToArray();
        var blocks = blockCounts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(50)
            .Select(p => new { name = p.Key, references = p.Value }).ToArray();
        // Used anywhere, including inside other blocks and through the anonymous copies of a dynamic block.
        bool Referenced(BlockTableRecord block) => block.GetBlockReferenceIds(true, false).Cast<ObjectId>().Any(id => !id.IsErased) ||
            block.IsDynamicBlock && block.GetAnonymousBlockIds().Cast<ObjectId>().Any(id => ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).GetBlockReferenceIds(true, false).Cast<ObjectId>().Any(r => !r.IsErased));
        int unused = definitions.Count(b => !b.IsLayout && !b.IsAnonymous && !b.IsFromExternalReference && !b.IsDependent && !blockCounts.ContainsKey(b.Name) && !Referenced(b));
        var styles = ((TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead)).Cast<ObjectId>().Select(id => (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead))
            .Where(s => !s.IsShapeFile && !string.IsNullOrEmpty(s.Name)).Select(s => new { name = s.Name, font = s.FileName, big_font = string.IsNullOrEmpty(s.BigFontFileName) ? null : s.BigFontFileName })
            .OrderBy(s => s.name, StringComparer.Ordinal).ToArray();
        var dimStyles = ((DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead)).Cast<ObjectId>().Select(id => ((DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name)
            .Order(StringComparer.Ordinal).ToArray();
        var textSampleRows = texts.Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .OrderBy(t => t.Space == "Model" ? 0 : 1).ThenBy(t => t.Space, StringComparer.Ordinal).ThenByDescending(t => Math.Round(t.Y, 3)).ThenBy(t => Math.Round(t.X, 3)).ThenBy(t => t.Text, StringComparer.Ordinal)
            .Take(textSample).Select(t => new { space = t.Space, text = t.Text.Length > 120 ? t.Text[..120] + "…" : t.Text, layer = t.Layer }).ToArray();
        Point3d min = db.Extmin, max = db.Extmax;
        bool validExtents = min.X <= max.X && min.Y <= max.Y && Math.Abs(min.X) < 1e19 && Math.Abs(max.X) < 1e19;
        int modelEntities = modelTypes.Values.Sum();
        var summary = new StringBuilder();
        summary.Append(name ?? "drawing").Append(": units ").Append(db.Insunits).Append(", model ").Append(modelEntities).Append(" entities");
        if (validExtents) summary.Append(", extents ").Append(F(max.X - min.X)).Append(" x ").Append(F(max.Y - min.Y));
        summary.Append(", ").Append(layouts.Count).Append(" sheets, ").Append(layerRows.Length).Append(" layers, ").Append(blocks.Length).Append(" block names in use");
        if (xrefs.Length > 0) summary.Append(", ").Append(xrefs.Length).Append(" external references (").Append(xrefs.Count(x => x.status != "Resolved")).Append(" unresolved)");
        summary.Append('.');
        if (modelTypes.Count > 0) summary.Append(" Model: ").Append(string.Join(", ", modelTypes.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(8).Select(p => p.Key + " " + p.Value))).Append('.');
        var busy = layerRows.Where(l => l.entities > 0).Take(8).ToArray();
        if (busy.Length > 0) summary.Append(" Main layers: ").Append(string.Join(", ", busy.Select(l => l.name + " " + l.entities))).Append('.');
        return new
        {
            name, summary = summary.ToString(), units = db.Insunits.ToString(), measurement = db.Measurement.ToString(),
            model_extents = validExtents ? new { min = new[] { min.X, min.Y, min.Z }, max = new[] { max.X, max.Y, max.Z } } : null,
            model_entities = modelEntities, model_types = modelTypes, sheets = layouts, layers = layerRows.Take(layerLimit).ToArray(), layer_count = layerRows.Length,
            blocks, unused_block_definitions = unused, external_references = xrefs, text_styles = styles, dimension_styles = dimStyles,
            text_sample = textSampleRows, partial = partial ? true : (bool?)null,
            limitations = new[] { "model extents come from the drawing header and may be stale until the next regeneration", "nested block contents are not counted" }
        };
    }

    /// <summary>Outline of a DWG on disk that is not open in AutoCAD, read into a separate database.</summary>
    public static object InspectFile(string path, CancellationToken ct)
    {
        // Local and mapped drives only: opening \\host\share makes Windows authenticate to whatever host a text names.
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || !path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            throw new CadFault("INVALID_PATH", "path must be an absolute .dwg path on a local or mapped drive; network (\\\\server\\share) and device paths are refused");
        path = Path.GetFullPath(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new CadFault("FILE_NOT_FOUND", path);
        // The file is read on the AutoCAD thread, which cannot be interrupted meanwhile.
        if (info.Length > 200L * 1024 * 1024) throw new CadFault("FILE_TOO_LARGE", "cad_file_inspect reads drawings up to 200 MB; open larger ones in AutoCAD");
        bool open = false;
        foreach (Autodesk.AutoCAD.ApplicationServices.Document document in Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager)
            try { open |= Path.IsPathFullyQualified(document.Name) && string.Equals(Path.GetFullPath(document.Name), path, StringComparison.OrdinalIgnoreCase); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
        using var db = new Database(false, true);
        try { db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, ""); db.CloseInput(true); }
        catch (Autodesk.AutoCAD.Runtime.Exception error) { throw new CadFault("DWG_READ_FAILED", error.ErrorStatus + ": " + error.Message); }
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var outline = Outline(db, tr, Path.GetFileName(path), ct);
        return new { path, bytes = info.Length, modified_utc = info.LastWriteTimeUtc, saved_version = db.OriginalFileVersion.ToString(), outline, opened_in_editor = open,
            note = open ? "The drawing is open in AutoCAD: this outline shows the file as last saved; read the open drawing for unsaved changes" : null };
    }

    // ---------------------------------------------------------------- changes

    private sealed record ChangeRow(string Handle, string Effect, string Type, string? Layer, long Revision, string[] By, DateTimeOffset At);

    /// <summary>
    /// What changed since a revision, one entry per object with its net effect and who changed it: a CAD MCP
    /// operation id, table_recalculation, or user (the person or another program, including UNDO).
    /// </summary>
    public static object Changes(DocumentState state, Transaction tr, JsonElement options)
    {
        if (!options.TryGetProperty("since_revision", out var value) || !value.TryGetInt64(out long since)) throw new CadFault("INVALID_PARAMETER", "since_revision is required");
        if (since < 0 || since > state.Revision) throw new CadFault("INVALID_PARAMETER", "since_revision must be between 0 and the current revision " + state.Revision);
        int limit = DraftingPlan.Integer(options, "limit", 1, 2000, 500);
        bool all = options.TryGetProperty("include_non_entities", out var flag) && flag.ValueKind == JsonValueKind.True;
        string source = options.Text("source") ?? "all";
        if (source is not ("all" or "user" or "cad_mcp")) throw new CadFault("INVALID_PARAMETER", "source must be all, user or cad_mcp");
        var window = state.ChangesSince(since).Where(c => all || c.Entity)
            .Where(c => source == "all" || (source == "user") == (c.Author is null)).ToArray();
        var layerNames = new Dictionary<ObjectId, string?>();
        string? LayerName(ObjectId id)
        {
            // Only ids of this drawing are opened; an id of a drawing that has since been freed is never touched.
            if (id.IsNull || id.Database is not { } owner || owner.UnmanagedObject != state.Database.UnmanagedObject) return null;
            if (layerNames.TryGetValue(id, out var known)) return known;
            try { known = tr.GetObject(id, OpenMode.ForRead, true) is LayerTableRecord layer ? layer.Name : null; }
            catch (Autodesk.AutoCAD.Runtime.Exception) { known = null; }
            return layerNames[id] = known;
        }
        static bool Added(string kind) => kind is "appended" or "reappended";
        static bool Removed(string kind) => kind is "erased" or "unappended";
        var objects = window.GroupBy(c => c.Handle).Select(g =>
        {
            var first = g.First(); var last = g.Last();
            // Created and then unappended (UNDO of the creation, or an aborted transaction) leaves nothing behind.
            if (Added(first.Kind) && last.Kind == "unappended") return null;
            string effect = Added(first.Kind) ? Removed(last.Kind) ? "added_then_erased" : "added"
                : Removed(last.Kind) ? "erased" : last.Kind is "unerased" or "reappended" ? "restored" : "modified";
            return new ChangeRow(g.Key.ToString("X"), effect, last.Type, last.LayerName ?? LayerName(last.Layer), last.Revision, g.Select(c => c.Author ?? "user").Distinct().ToArray(), last.At);
        }).OfType<ChangeRow>().OrderBy(c => c.Revision).ToArray();
        return new
        {
            since_revision = since, current_revision = state.Revision, complete = since >= state.DroppedThrough,
            counts = objects.GroupBy(c => c.Effect).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            by = objects.SelectMany(c => c.By).GroupBy(a => a).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            changes = objects.Take(limit).ToArray(), truncated = objects.Length > limit ? true : (bool?)null,
            note = "user means the person or another program (including UNDO); the log covers this AutoCAD session and the last 20000 events"
        };
    }
}
