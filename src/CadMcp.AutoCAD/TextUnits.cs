using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;
using GiTextStyle = Autodesk.AutoCAD.GraphicsInterface.TextStyle;

namespace CadMcp.AutoCAD;

/// <summary>
/// Text of a drawing as translation units (cad_text_units), and the edits that write translations (text_translate) and
/// fit text into a frame (text_fit). A unit is a TEXT, MTEXT, attribute, multileader, table cell, or a paragraph of
/// stacked TEXT lines; fitting shrinks the width factor first, then the height, within limits.
/// </summary>
internal static class TextUnits
{
    /// <summary>String widths in a text style's own font at width factor 1, cached per style, height and obliquing.</summary>
    private sealed class Measure : IDisposable
    {
        private readonly Dictionary<(ObjectId Style, double Height, double Oblique), GiTextStyle?> styles = new();
        private readonly Dictionary<(ObjectId, double, double, string), double> widths = new();
        public bool Estimated { get; private set; }

        public double Width(ObjectId styleId, double height, double oblique, string stored)
        {
            if (stored.Length == 0 || !(height > 0)) return 0;
            var key = (styleId, height, oblique, stored);
            if (widths.TryGetValue(key, out double cached)) return cached;
            double width;
            if (!styles.TryGetValue((styleId, height, oblique), out var style))
            {
                style = new GiTextStyle();
                try
                {
                    style.FromTextStyleTableRecord(styleId);
                    style.TextSize = height; style.XScale = 1; style.ObliquingAngle = oblique;
                }
                catch (System.Exception) { style.Dispose(); style = null; }
                // A style that cannot be loaded is remembered as such and estimated, not loaded again for every string.
                styles[(styleId, height, oblique)] = style;
            }
            try
            {
                if (style is null) throw new InvalidOperationException();
                // Not raw: %%c, %%d, %%p and %%nnn are measured as the characters they show.
                var box = style.ExtentsBox(stored, false, false, null);
                width = Math.Max(0, box.MaxPoint.X - box.MinPoint.X);
            }
            catch (System.Exception)
            {
                // A font AutoCAD cannot load: an estimate of 0.6 height per character keeps fitting possible and is reported.
                Estimated = true;
                width = 0.6 * height * CadText.Normalize(stored).Length;
            }
            if (widths.Count < 50000) widths[key] = width;
            return width;
        }

        public void Dispose() { foreach (var style in styles.Values) style?.Dispose(); }
    }

    private static string H(ObjectId id) => id.Handle.ToString();
    private static bool Justified(DBText text) => text.HorizontalMode != TextHorizontalMode.TextLeft || text.VerticalMode != TextVerticalMode.TextBase;
    private static bool SelfFitting(DBText text) => text.HorizontalMode is TextHorizontalMode.TextFit or TextHorizontalMode.TextAlign;
    private static bool Flat(Entity entity) => entity is DBText t ? t.Normal.IsParallelTo(Vector3d.ZAxis) && t.Normal.Z > 0 : true;
    private static double[] Point(Point3d p) => [Math.Round(p.X, 6), Math.Round(p.Y, 6), Math.Round(p.Z, 6)];

    private sealed record Unit(string Key, string Kind, string Text, string Layer, double[] Position, object Frame, string[]? Lines = null, double? Height = null,
        double? WidthFactor = null, double? RotationDeg = null, string? Style = null, string? Tag = null, bool Fields = false, bool Locked = false);

    /// <summary>cad_text_units: translation units in a scope, paged by offset and limit.</summary>
    internal static object Read(Database db, Transaction tr, JsonElement data, CancellationToken ct)
    {
        var include = (data.Text("include") ?? "text,mtext,attributes,tables,mleaders").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        string[] kinds = ["text", "mtext", "attributes", "tables", "mleaders", "block_definitions"];
        if (include.Count == 0 || include.Any(k => !kinds.Contains(k))) throw new CadFault("INVALID_PARAMETER", "include lists " + string.Join(", ", kinds));
        string[]? layers = null;
        if (data.Text("layers_json") is { } layersJson)
        {
            using var parsed = JsonDocument.Parse(layersJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Array || parsed.RootElement.GetArrayLength() is < 1 or > 50) throw new CadFault("INVALID_PARAMETER", "layers_json is an array of 1..50 layer names or patterns");
            layers = parsed.RootElement.EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
        }
        bool group = data.TryGetProperty("group_lines", out var groupValue) ? groupValue.ValueKind != JsonValueKind.False : true;
        int offset = DraftingPlan.Integer(data, "offset", 0, 1_000_000, 0), limit = DraftingPlan.Integer(data, "limit", 1, 2000, 500);
        var lockedLayers = new Dictionary<ObjectId, bool>();
        bool LayerLocked(Entity e)
        {
            if (!lockedLayers.TryGetValue(e.LayerId, out bool locked)) lockedLayers[e.LayerId] = locked = ((LayerTableRecord)tr.GetObject(e.LayerId, OpenMode.ForRead)).IsLocked;
            return locked;
        }
        // An attribute is written with the block reference that holds it: either layer locked keeps it as it is.
        bool Locked(Entity e, Entity? holder = null) => LayerLocked(e) || holder is not null && LayerLocked(holder);
        bool OnLayer(Entity e) => layers is null || layers.Any(pattern => CadText.Like(e.Layer, pattern));

        using var measure = new Measure();
        var units = new List<Unit>();
        var single = new List<DBText>();
        var tablesCut = new List<string>();
        void AddText(DBText text, string kind, Entity? holder = null)
        {
            string raw = text.TextString ?? "";
            if (CadText.Normalize(raw).Trim().Length == 0) return;
            if (kind == "text" && group && !text.HasFields && Flat(text) && !SelfFitting(text)) { single.Add(text); return; }
            units.Add(new(H(text.ObjectId), kind, CadText.Normalize(raw), text.Layer, Point(text.Position), new { width = Round(measure.Width(text.TextStyleId, text.Height, text.Oblique, raw) * text.WidthFactor), height = Round(text.Height) },
                Height: Round(text.Height), WidthFactor: Round(text.WidthFactor), RotationDeg: Round(text.Rotation * 180 / Math.PI), Style: text.TextStyleName,
                Tag: text is AttributeReference a ? a.Tag : null, Fields: text.HasFields, Locked: Locked(text, holder)));
        }
        // owner holds the text: a multileader or an attribute keeps its fields itself, not in the copy of its MText.
        void MTextUnit(Entity owner, MText mtext, string kind, string? tag = null, Entity? holder = null)
        {
            string displayed = CadText.Normalize(mtext.Contents, true);
            if (displayed.Trim().Length == 0) return;
            units.Add(new(H(owner.ObjectId), kind, displayed, owner.Layer, Point(mtext.Location),
                new { width = Round(mtext.ActualWidth), height = Round(mtext.ActualHeight), column_width = Round(mtext.Width) },
                Height: Round(mtext.TextHeight), RotationDeg: Round(mtext.Rotation * 180 / Math.PI), Style: mtext.TextStyleName, Tag: tag, Fields: owner.HasFields || mtext.HasFields,
                Locked: Locked(owner, holder)));
        }
        void Visit(Entity entity, bool inDefinition)
        {
            ct.ThrowIfCancellationRequested();
            if (!OnLayer(entity)) return;
            switch (entity)
            {
                case AttributeDefinition: break;
                case DBText text when include.Contains(inDefinition ? "block_definitions" : "text"): AddText(text, inDefinition ? "block_text" : "text"); break;
                case MText mtext when include.Contains(inDefinition ? "block_definitions" : "mtext"): MTextUnit(mtext, mtext, inDefinition ? "block_mtext" : "mtext"); break;
                case MLeader leader when !inDefinition && include.Contains("mleaders") && leader.ContentType == ContentType.MTextContent && leader.MText is { } content:
                    using (content) MTextUnit(leader, content, "mleader");
                    break;
                case Table table when !inDefinition && include.Contains("tables"):
                    if (table.Rows.Count > 500 || table.Columns.Count > 50) tablesCut.Add(H(table.ObjectId));
                    var merges = NativeTables.MergedRanges(table);
                    for (int r = 0; r < Math.Min(table.Rows.Count, 500); r++)
                        for (int c = 0; c < Math.Min(table.Columns.Count, 50); c++)
                        {
                            if (merges.Any(m => r >= m.TopRow && r <= m.BottomRow && c >= m.LeftColumn && c <= m.RightColumn && (m.TopRow != r || m.LeftColumn != c))) continue;
                            var cell = table.Cells[r, c];
                            string raw = cell.TextString ?? "";
                            string displayed = CadText.Normalize(raw, true);
                            if (displayed.Trim().Length == 0) continue;
                            bool computed = !cell.FieldId.IsNull || cell.Contents.Count > 0 && cell.Contents[0].HasFormula;
                            units.Add(new(TextTranslation.CellUnit(H(table.ObjectId), r, c), "table_cell", displayed, table.Layer, Point(table.Position),
                                new { width = Round(table.Columns[c].Width), height = Round(table.Rows[r].Height) }, Fields: computed, Locked: Locked(table)));
                        }
                    break;
                case BlockReference reference when !inDefinition && include.Contains("attributes"):
                    foreach (ObjectId id in reference.AttributeCollection)
                    {
                        if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not AttributeReference attribute || attribute.Invisible) continue;
                        if (attribute.IsMTextAttribute) { using var content = attribute.MTextAttribute; MTextUnit(attribute, content, "attribute_mtext", attribute.Tag, reference); }
                        else AddText(attribute, "attribute", reference);
                    }
                    break;
            }
        }

        string scope = data.Text("scope") ?? "current";
        var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        if (data.Text("handles_json") is { } handlesJson)
        {
            scope = "handles";
            using var parsed = JsonDocument.Parse(handlesJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Array || parsed.RootElement.GetArrayLength() is < 1 or > 5000) throw new CadFault("INVALID_HANDLES", "handles_json is an array of 1..5000 handles");
            foreach (var value in parsed.RootElement.EnumerateArray())
                if (tr.GetObject(Edits.Handle(db, value.GetString() ?? ""), OpenMode.ForRead) is Entity entity) Visit(entity, false);
        }
        else
        {
            ObjectId[] spaces = scope switch
            {
                "current" => [db.CurrentSpaceId],
                "model" => [blocks[BlockTableRecord.ModelSpace]],
                "layout" => [Sheets.Layout(db, tr, data.Text("layout_name") ?? throw new CadFault("INVALID_PARAMETER", "layout_name is required with scope layout")).BlockTableRecordId],
                "all" => blocks.Cast<ObjectId>().Where(id => ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).IsLayout).ToArray(),
                _ => throw new CadFault("INVALID_PARAMETER", "scope is current, model, layout or all")
            };
            foreach (var space in spaces)
                foreach (ObjectId id in (BlockTableRecord)tr.GetObject(space, OpenMode.ForRead))
                    if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is Entity entity) Visit(entity, false);
            if (include.Contains("block_definitions"))
                foreach (ObjectId blockId in blocks)
                {
                    var block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
                    if (block.IsLayout || block.IsFromExternalReference || block.IsDependent || block.IsAnonymous) continue;
                    foreach (ObjectId id in block)
                        if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is Entity entity and (DBText or MText)) Visit(entity, true);
                }
        }

        // Stacked TEXT lines of one paragraph become one unit, keyed by their handles from top to bottom.
        var lines = single.Select(t => new TextLine(t.Position.X, t.Position.Y, t.Rotation, t.Height, measure.Width(t.TextStyleId, t.Height, t.Oblique, t.TextString) * t.WidthFactor, t.TextStyleName, t.Layer,
            H(t.OwnerId), t.Position.Z)).ToArray();
        foreach (var paragraph in TextTranslation.Paragraphs(lines))
        {
            var texts = paragraph.Select(i => single[i]).ToArray();
            var first = texts[0];
            var shown = texts.Select(t => CadText.Normalize(t.TextString)).ToArray();
            // The lines stack across their direction: the frame height is measured perpendicular to the rotation.
            double across = Math.Abs((texts[^1].Position - first.Position).DotProduct(new Vector3d(-Math.Sin(first.Rotation), Math.Cos(first.Rotation), 0)));
            units.Add(new(string.Join("+", texts.Select(t => H(t.ObjectId))), texts.Length == 1 ? "text" : "paragraph", string.Join(" ", shown.Select(s => s.Trim())), first.Layer, Point(first.Position),
                new { width = Round(paragraph.Max(i => lines[i].Width)), height = Round(across + first.Height), lines = texts.Length },
                texts.Length == 1 ? null : shown, Round(first.Height), Round(first.WidthFactor), Round(first.Rotation * 180 / Math.PI), first.TextStyleName, Locked: Locked(first)));
        }

        var page = units.Skip(offset).Take(limit).ToArray();
        return new
        {
            scope, include = include.Order(StringComparer.Ordinal).ToArray(), units_total = units.Count, distinct_texts = units.Select(u => u.Text).Distinct(StringComparer.Ordinal).Count(),
            offset, next_offset = offset + page.Length < units.Count ? offset + page.Length : (int?)null,
            units = page.Select(u => new
            {
                unit = u.Key, kind = u.Kind, text = u.Text, lines = u.Lines, layer = u.Layer, style = u.Style, tag = u.Tag, height = u.Height, width_factor = u.WidthFactor,
                rotation_deg = u.RotationDeg, position = u.Position, frame = u.Frame, fields = u.Fields ? true : (bool?)null, locked = u.Locked ? true : (bool?)null
            }).ToArray(),
            widths_estimated = measure.Estimated ? true : (bool?)null,
            tables_truncated = tablesCut.Count == 0 ? null : new { handles = tablesCut.Take(20).ToArray(), count = tablesCut.Count, note = "Only the first 500 rows and 50 columns of these tables are listed" },
            usage = "Translate each unit's text and apply with cad_edit text_translate {unit, text, source}; units with fields or on locked layers are not written"
        };
    }

    private static double Round(double value) => Math.Round(value, 6);

    private static void Unlocked(Transaction tr, Entity entity)
    {
        Edits.RequireUnlocked(tr, entity);
        // An attribute is locked with the block reference that holds it.
        if (entity is AttributeReference && tr.GetObject(entity.OwnerId, OpenMode.ForRead) is Entity reference) Edits.RequireUnlocked(tr, reference);
        if (entity.OwnerId.IsValid && tr.GetObject(entity.OwnerId, OpenMode.ForRead) is BlockTableRecord owner && (owner.IsFromExternalReference || owner.IsDependent))
            throw new CadFault("XREF_OBJECT", "Text of an external reference cannot be changed: " + H(entity.ObjectId));
    }

    /// <summary>text_translate: writes translated units, fitting them into their original frames unless fit is none.</summary>
    internal static ModifyOperations.Outcome Translate(Database db, Transaction tr, JsonElement op, CancellationToken ct)
    {
        bool fit = (op.Text("fit") ?? "shrink") == "shrink";
        double minWidth = EditPlan.Numeric(op, "min_width_factor", 0.7), minHeight = EditPlan.Numeric(op, "min_height_ratio", 0.6);
        using var measure = new Measure();
        var touched = new List<ObjectId>();
        var results = new List<object>();
        var skipped = new List<object>();
        var definitions = new HashSet<ObjectId>();
        int overflow = 0, simplified = 0, written = 0;
        foreach (var item in op.GetProperty("units").EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            string key = item.GetProperty("unit").GetString()!, text = item.GetProperty("text").GetString()!;
            string? source = item.Text("source");
            var (handles, row, column) = TextTranslation.ParseUnit(key);
            var objects = handles.Select(h => tr.GetObject(Edits.Handle(db, h), OpenMode.ForRead)).ToArray();
            string current = Displayed(objects, row, column);
            // The text the translation was made from: a unit changed since it was read is not overwritten.
            if (source is not null && !string.Equals(Collapse(source), Collapse(current), StringComparison.Ordinal))
                throw new CadFault("TEXT_CHANGED", "Unit " + key + " now reads \"" + Clip(current) + "\", not the source given; read cad_text_units again");
            if (objects.OfType<Entity>().Any(HasFields) || row >= 0 && objects[0] is Table computed && (!computed.Cells[row, column].FieldId.IsNull || computed.Cells[row, column].Contents.Count > 0 && computed.Cells[row, column].Contents[0].HasFormula))
            {
                skipped.Add(new { unit = key, reason = "field or formula" });
                continue;
            }
            if (objects.OfType<Entity>().Any(entity => OnLockedLayer(tr, entity)))
            {
                skipped.Add(new { unit = key, reason = "locked_layer" });
                continue;
            }
            foreach (var entity in objects.OfType<Entity>()) Unlocked(tr, entity);
            object detail = objects switch
            {
                [Table table] when row >= 0 => Cell(table, row, column, text, ref simplified),
                [_] when row >= 0 => throw new CadFault("INVALID_UNIT", key + " names a table cell of an object that is not a table"),
                [AttributeReference { IsMTextAttribute: true } attribute] => WriteAttributeMText(attribute, text, fit, minHeight, ref overflow, ref simplified),
                [DBText single] => Lines([single], text, fit, minWidth, minHeight, measure, db, ref overflow),
                [MText mtext] => WriteMText(mtext, null, text, fit, minHeight, ref overflow, ref simplified),
                [MLeader leader] => WriteLeader(leader, text, ref simplified),
                _ when objects.Length > 1 && objects.All(o => o is DBText) => Lines(objects.Cast<DBText>().ToArray(), text, fit, minWidth, minHeight, measure, db, ref overflow),
                _ => throw new CadFault("INVALID_UNIT", key + " is not a text, multiline text, attribute, multileader, table cell or paragraph of texts")
            };
            touched.AddRange(objects.Select(o => o is AttributeReference a ? a.OwnerId : o.ObjectId));
            definitions.UnionWith(objects.Where(o => o is not AttributeReference).Select(o => o.OwnerId));
            written++;
            if (results.Count < 200) results.Add(new { unit = key, before = Clip(current), after = Clip(text), detail });
        }
        var (updated, notUpdated) = UpdateDynamicBlocks(tr, definitions);
        return new(ObjectId.Null, touched.Distinct().ToArray(), new
        {
            translated = written, units = results, units_truncated = results.Count < written,
            skipped = skipped.Count == 0 ? null : skipped, overflow_units = overflow == 0 ? (int?)null : overflow,
            formatting_simplified = simplified == 0 ? (int?)null : simplified, widths_estimated = measure.Estimated ? true : (bool?)null,
            dynamic_blocks_updated = updated, dynamic_blocks_not_updated = notUpdated
        });
    }

    private static bool LayerLocked(Transaction tr, Entity entity) => ((LayerTableRecord)tr.GetObject(entity.LayerId, OpenMode.ForRead)).IsLocked;
    private static bool OnLockedLayer(Transaction tr, Entity entity) =>
        LayerLocked(tr, entity) || entity is AttributeReference && tr.GetObject(entity.OwnerId, OpenMode.ForRead) is Entity reference && LayerLocked(tr, reference);

    /// <summary>
    /// A dynamic block shows its references through anonymous copies of the definition; after its text changes they are
    /// rebuilt, as saving the block editor does. Names of the blocks that could not be rebuilt are reported.
    /// </summary>
    internal static (string[]? Updated, string[]? NotUpdated) UpdateDynamicBlocks(Transaction tr, IEnumerable<ObjectId> owners)
    {
        var updated = new List<string>();
        var failed = new List<string>();
        foreach (var id in owners)
        {
            if (id.IsNull || tr.GetObject(id, OpenMode.ForRead) is not BlockTableRecord { IsLayout: false, IsDynamicBlock: true } block) continue;
            try
            {
                if (!block.IsWriteEnabled) block.UpgradeOpen();
                block.UpdateAnonymousBlocks();
                updated.Add(block.Name);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception e) { failed.Add(block.Name + ": " + e.ErrorStatus); }
        }
        return (updated.Count == 0 ? null : updated.ToArray(), failed.Count == 0 ? null : failed.ToArray());
    }

    // Fields live on the object that shows them (the multileader, the attribute); the copy of a multileader's MText is checked as well.
    private static bool HasFields(Entity entity) => entity.HasFields || entity is MLeader { ContentType: ContentType.MTextContent } leader && LeaderMTextHasFields(leader);
    private static bool LeaderMTextHasFields(MLeader leader) { if (leader.MText is not { } content) return false; using (content) return content.HasFields; }
    private static string Collapse(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Clip(string text) => text.Length <= 300 ? text : text[..300] + "…";

    private static string Displayed(DBObject[] objects, int row, int column) => objects switch
    {
        [Table table] when row >= 0 => row < table.Rows.Count && column < table.Columns.Count ? CadText.Normalize(table.Cells[row, column].TextString ?? "", true)
            : throw new CadFault("INVALID_UNIT", "The table has no cell " + row + "," + column),
        [AttributeReference { IsMTextAttribute: true } attribute] => Read(attribute),
        [MText mtext] => CadText.Normalize(mtext.Contents, true),
        [MLeader leader] when leader.ContentType == ContentType.MTextContent && leader.MText is { } content => Read(content),
        _ when objects.All(o => o is DBText) => string.Join(" ", objects.Cast<DBText>().Select(t => CadText.Normalize(t.TextString).Trim())),
        _ => throw new CadFault("INVALID_UNIT", "The unit names an object without translatable text")
    };
    private static string Read(AttributeReference attribute) { using var content = attribute.MTextAttribute; return CadText.Normalize(content.Contents, true); }
    private static string Read(MText content) { using (content) return CadText.Normalize(content.Contents, true); }

    /// <summary>One TEXT or a paragraph of stacked TEXT lines: the words wrap across the lines, then the frame decides the fit.</summary>
    private static object Lines(DBText[] texts, string text, bool fit, double minWidth, double minHeight, Measure measure, Database db, ref int overflow)
    {
        var first = texts[0];
        // Also reject old or manually assembled unit keys that cross spaces; source text alone cannot prove a paragraph.
        if (texts.Length > 1 && texts.Any(t => t.OwnerId != first.OwnerId || t.Position.Z != first.Position.Z || !Flat(t)))
            throw new CadFault("INVALID_UNIT", "Paragraph lines must belong to the same space or block definition and WCS elevation; read cad_text_units again");
        double height = first.Height, factor = first.WidthFactor;
        double Natural(string displayed) => measure.Width(first.TextStyleId, height, first.Oblique, TextTranslation.ForText(displayed));
        // The frame is the widest original line; Fit and Aligned texts fit themselves between their points.
        double frame = texts.Max(t => measure.Width(t.TextStyleId, t.Height, t.Oblique, t.TextString) * t.WidthFactor);
        var result = fit && !texts.Any(SelfFitting)
            ? TextTranslation.FitLines(text, texts.Length, frame, factor, minWidth, minHeight, Natural)
            : new TextFit(TextTranslation.Wrap(text, texts.Length, texts.Length == 1 ? double.MaxValue : frame / factor, Natural, force: true)!, factor, 1, false);
        var lines = result.Lines.Length >= texts.Length ? result.Lines : result.Lines.Concat(Enumerable.Repeat("", texts.Length - result.Lines.Length)).ToArray();
        for (int i = 0; i < texts.Length; i++)
        {
            var t = texts[i];
            if (!t.IsWriteEnabled) t.UpgradeOpen();
            // A line the text no longer needs is left blank; a space stands in where AutoCAD refuses an empty string.
            try { t.TextString = TextTranslation.ForText(lines[i]); }
            catch (Autodesk.AutoCAD.Runtime.Exception) when (lines[i].Length == 0) { t.TextString = " "; }
            if (result.WidthFactor != factor) t.WidthFactor = result.WidthFactor;
            if (result.HeightScale < 1) t.Height = t.Height * result.HeightScale;
            if (Justified(t)) t.AdjustAlignment(db);
        }
        if (result.Overflow) overflow++;
        return new
        {
            kind = texts.Length == 1 ? "text" : "paragraph", lines = texts.Length == 1 ? null : lines, width_factor = Round(result.WidthFactor), height_scale = Round(result.HeightScale),
            frame_width = Round(frame), overflow = result.Overflow ? true : (bool?)null, emptied_lines = lines.Count(l => l.Length == 0) is int empty and > 0 ? empty : (int?)null
        };
    }

    /// <summary>Writes MText contents and, when fitting, shrinks the height until the text takes no more room than before.</summary>
    private static object WriteMText(MText mtext, AttributeReference? attribute, string text, bool fit, double minHeight, ref int overflow, ref int simplified)
    {
        double width = mtext.ActualWidth, height = mtext.ActualHeight, textHeight = mtext.TextHeight;
        string contents = TextTranslation.ForMText(mtext.Contents, text, out bool lost);
        if (lost) simplified++;
        if (attribute is null) mtext.UpgradeOpen();
        mtext.Contents = contents;
        double scale = 1;
        bool tooLarge = false;
        if (fit && width > 0 && height > 0 && textHeight > 0)
        {
            for (int i = 0; i < 6; i++)
            {
                double w = mtext.ActualWidth, h = mtext.ActualHeight;
                bool wraps = mtext.Width > 0;
                double needed = wraps ? (h <= height * 1.001 ? 1 : Math.Sqrt(height / h)) : Math.Min(w <= width * 1.001 ? 1 : width / w, h <= height * 1.001 ? 1 : height / h);
                if (needed >= 0.999) { tooLarge = false; break; }
                tooLarge = true;
                double next = Math.Max(minHeight, scale * needed * 0.99);
                if (next >= scale - 1e-6) break;
                scale = next;
                mtext.TextHeight = textHeight * scale;
                mtext.Contents = TextTranslation.ScaleHeights(contents, scale);
            }
            tooLarge = mtext.ActualHeight > height * 1.001 || mtext.Width <= 0 && mtext.ActualWidth > width * 1.001;
        }
        if (tooLarge) overflow++;
        return new { kind = attribute is null ? "mtext" : "attribute_mtext", height_scale = Round(scale), frame = new { width = Round(width), height = Round(height) }, overflow = tooLarge ? true : (bool?)null, formatting_simplified = lost ? true : (bool?)null };
    }

    private static object WriteAttributeMText(AttributeReference attribute, string text, bool fit, double minHeight, ref int overflow, ref int simplified)
    {
        attribute.UpgradeOpen();
        using var content = attribute.MTextAttribute;
        var detail = WriteMText(content, attribute, text, fit, minHeight, ref overflow, ref simplified);
        // Set back as a whole; UpdateMTextAttribute here would rebuild it from the old single-line text and height.
        attribute.MTextAttribute = content;
        return detail;
    }

    private static object WriteLeader(MLeader leader, string text, ref int simplified)
    {
        if (leader.ContentType != ContentType.MTextContent || leader.MText is not { } content) throw new CadFault("INVALID_UNIT", "The multileader holds no text");
        using (content)
        {
            content.Contents = TextTranslation.ForMText(content.Contents, text, out bool lost);
            if (lost) simplified++;
            leader.UpgradeOpen();
            leader.MText = content;
            return new { kind = "mleader", formatting_simplified = lost ? true : (bool?)null, fit = "not_applied" };
        }
    }

    private static object Cell(Table table, int row, int column, string text, ref int simplified)
    {
        foreach (var m in NativeTables.MergedRanges(table))
            if (row >= m.TopRow && row <= m.BottomRow && column >= m.LeftColumn && column <= m.RightColumn && (m.TopRow != row || m.LeftColumn != column))
                throw new CadFault("INVALID_UNIT", "Cell " + row + "," + column + " is hidden in a merged range; its text is in cell " + m.TopRow + "," + m.LeftColumn);
        if (!table.IsWriteEnabled) table.UpgradeOpen();
        table.Cells[row, column].TextString = TextTranslation.ForMText(table.Cells[row, column].TextString ?? "", text, out bool lost);
        if (lost) simplified++;
        return new { kind = "table_cell", formatting_simplified = lost ? true : (bool?)null, fit = "not_applied: the row grows to its text" };
    }

    /// <summary>
    /// text_fit: a TEXT or attribute narrows its width factor, then its height, until it is no wider than width; an MTEXT
    /// wraps at width and, with height, shrinks its text height until it is no taller.
    /// </summary>
    internal static ModifyOperations.Outcome Fit(Database db, Transaction tr, JsonElement op, Dictionary<string, ObjectId> aliases, CancellationToken ct)
    {
        double width = EditPlan.Numeric(op, "width"), minWidth = EditPlan.Numeric(op, "min_width_factor", 0.7), minHeight = EditPlan.Numeric(op, "min_height_ratio", 0.6);
        double? height = op.TryGetProperty("height", out var h) && h.ValueKind != JsonValueKind.Null ? EditPlan.Numeric(op, "height") : null;
        var ids = op.TryGetProperty("handles", out var handles) ? handles.EnumerateArray().Select(v => Edits.Handle(db, v.GetString()!)).ToArray() : [Edits.Resolve(db, tr, op, aliases)];
        using var measure = new Measure();
        var results = new List<object>();
        int overflow = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var entity = tr.GetObject(id, OpenMode.ForRead) as Entity ?? throw new CadFault("INVALID_TEXT_FIT", "Not an entity: " + H(id));
            Unlocked(tr, entity);
            switch (entity)
            {
                case AttributeReference { IsMTextAttribute: true } attribute:
                    attribute.UpgradeOpen();
                    using (var content = attribute.MTextAttribute)
                    {
                        results.Add(new { handle = H(id), detail = Column(content, width, height, minHeight, ref overflow) });
                        attribute.MTextAttribute = content;
                    }
                    break;
                case DBText text:
                    if (SelfFitting(text)) { results.Add(new { handle = H(id), detail = (object)"Fit or Aligned text fits itself between its points" }); break; }
                    double before = measure.Width(text.TextStyleId, text.Height, text.Oblique, text.TextString) * text.WidthFactor;
                    var result = TextTranslation.FitLines(CadText.Normalize(text.TextString), 1, width, text.WidthFactor, minWidth, minHeight,
                        s => measure.Width(text.TextStyleId, text.Height, text.Oblique, TextTranslation.ForText(s)));
                    text.UpgradeOpen();
                    if (result.WidthFactor != text.WidthFactor) text.WidthFactor = result.WidthFactor;
                    if (result.HeightScale < 1) text.Height *= result.HeightScale;
                    if (Justified(text)) text.AdjustAlignment(db);
                    if (result.Overflow) overflow++;
                    results.Add(new { handle = H(id), detail = new { kind = "text", width_before = Round(before), width_factor = Round(result.WidthFactor), height_scale = Round(result.HeightScale), overflow = result.Overflow ? true : (bool?)null } });
                    break;
                case MText mtext:
                    mtext.UpgradeOpen();
                    results.Add(new { handle = H(id), detail = Column(mtext, width, height, minHeight, ref overflow) });
                    break;
                default: throw new CadFault("INVALID_TEXT_FIT", "text_fit takes TEXT, MTEXT and attributes: " + H(id));
            }
        }
        var (updated, notUpdated) = UpdateDynamicBlocks(tr, ids.Select(id => tr.GetObject(id, OpenMode.ForRead)).Where(o => o is not AttributeReference).Select(o => o.OwnerId).Distinct());
        return new(ids.Length == 1 ? ids[0] : ObjectId.Null, ids.Select(id => tr.GetObject(id, OpenMode.ForRead) is AttributeReference a ? a.OwnerId : id).Distinct().ToArray(),
            new
            {
                fitted = results.Count, results, overflow_texts = overflow == 0 ? (int?)null : overflow, widths_estimated = measure.Estimated ? true : (bool?)null,
                dynamic_blocks_updated = updated, dynamic_blocks_not_updated = notUpdated
            });
    }

    private static object Column(MText mtext, double width, double? height, double minHeight, ref int overflow)
    {
        mtext.Width = width;
        double textHeight = mtext.TextHeight, scale = 1;
        string contents = mtext.Contents;
        if (height is { } limit && textHeight > 0)
            for (int i = 0; i < 8 && mtext.ActualHeight > limit * 1.001; i++)
            {
                double next = Math.Max(minHeight, scale * Math.Sqrt(limit / mtext.ActualHeight) * 0.99);
                if (next >= scale - 1e-6) break;
                scale = next;
                mtext.TextHeight = textHeight * scale;
                mtext.Contents = TextTranslation.ScaleHeights(contents, scale);
            }
        bool tooLarge = height is { } max && mtext.ActualHeight > max * 1.001;
        if (tooLarge) overflow++;
        return new { kind = "mtext", column_width = Round(width), height_scale = Round(scale), actual_height = Round(mtext.ActualHeight), overflow = tooLarge ? true : (bool?)null };
    }
}
