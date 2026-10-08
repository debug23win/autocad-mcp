using System.Globalization;
using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;
using Region = Autodesk.AutoCAD.DatabaseServices.Region;

namespace CadMcp.AutoCAD;

/// <summary>
/// Everyday drafting edits inside the cad_edit transaction, validated beforehand by <see cref="ModifyPlan"/>.
/// Geometry is computed natively (no command line), so a failure anywhere rolls the whole batch back.
/// </summary>
internal static class ModifyOperations
{
    /// <summary>Main: the entity an id names (Null when the operation has no single result); Touched: objects to read back.</summary>
    internal sealed record Outcome(ObjectId Main, IReadOnlyCollection<ObjectId> Touched, object Detail);

    private const double Epsilon = 1e-9;

    public static Outcome Execute(Database db, Transaction tr, JsonElement op, Dictionary<string, ObjectId> aliases, CancellationToken ct) => EditPlan.RequiredText(op, "op") switch
    {
        "text_replace" => TextReplace(db, tr, op, ct),
        "offset" => Offset(db, tr, op, aliases),
        "explode" => Explode(db, tr, op, aliases),
        "join" => Join(db, tr, op, aliases),
        "array_rect" or "array_polar" => ArrayCopies(db, tr, op, aliases, ct),
        "fillet" or "chamfer" => Corner(db, tr, op, aliases),
        "polyline_fillet" => RoundPolyline(db, tr, op, aliases),
        "text_translate" => TextUnits.Translate(db, tr, op, ct),
        "text_fit" => TextUnits.Fit(db, tr, op, aliases, ct),
        "trim" => Trim(db, tr, op, aliases),
        "extend" => Extend(db, tr, op, aliases),
        "dimension_angular" => AngularDimension(db, tr, op),
        "mleader" => Leader(db, tr, op),
        "xref_attach" => AttachXref(db, tr, op),
        "block_import" => ImportBlocks(db, tr, op),
        "layer_merge" => MergeLayers(db, tr, op, ct),
        "xdata_set" => SetXData(db, tr, op, aliases),
        "xrecord_set" => SetXRecord(db, tr, op, aliases),
        "field_text" => FieldText(db, tr, op, aliases),
        var kind => throw new CadFault("INVALID_OPERATION", kind)
    };

    private static Point3d P(JsonElement value) { var p = EditPlan.Point(value); return new(p[0], p[1], p[2]); }
    private static Point3d P(JsonElement op, string field) => P(op.GetProperty(field));
    private static double N(JsonElement op, string field, double? fallback = null) => EditPlan.Numeric(op, field, fallback);
    private static bool Bool(JsonElement op, string field, bool fallback = false) => op.TryGetProperty(field, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetBoolean() : fallback;
    private static string H(ObjectId id) => id.Handle.ToString();
    private static ObjectId Reference(Database db, string key, Dictionary<string, ObjectId> aliases) => aliases.TryGetValue(key, out var id) ? id : Edits.Handle(db, key);
    private static ObjectId Other(Database db, JsonElement op, string handle, string target, Dictionary<string, ObjectId> aliases) =>
        op.Text(target) is { } alias ? aliases.TryGetValue(alias, out var id) ? id : throw new CadFault("UNKNOWN_TARGET", alias) : Edits.Handle(db, EditPlan.RequiredText(op, handle));

    private static T Append<T>(Transaction tr, BlockTableRecord space, T entity) where T : Entity
    {
        if (!space.IsWriteEnabled) space.UpgradeOpen();
        space.AppendEntity(entity); tr.AddNewlyCreatedDBObject(entity, true);
        return entity;
    }

    /// <summary>Optional layer and colour of a result; the result must not land on a locked layer.</summary>
    private static void Style(Database db, Transaction tr, Entity entity, JsonElement op)
    {
        if (op.Text("layer") is { } layer) entity.LayerId = Edits.Layer(db, tr, layer);
        if (op.TryGetProperty("color_index", out var color)) entity.ColorIndex = color.GetInt32();
        Edits.RequireUnlocked(tr, entity);
    }

    private static BlockTableRecord TargetSpace(Database db, Transaction tr, JsonElement op) =>
        op.Text("layout") is { } layout ? Sheets.Space(db, tr, layout) : (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

    private static string Clip(string text) => text.Length > 200 ? text[..200] + "…" : text;

    // ---------------------------------------------------------------- text

    private static Outcome TextReplace(Database db, Transaction tr, JsonElement op, CancellationToken ct)
    {
        string find = op.GetProperty("find").GetString()!, replace = op.GetProperty("replace").GetString()!;
        bool matchCase = Bool(op, "match_case"), wholeWord = Bool(op, "whole_word");
        var include = op.TryGetProperty("include", out var kinds) ? kinds.EnumerateArray().Select(v => v.GetString()!).ToHashSet() : ["text", "mtext", "attributes"];
        var layers = op.TryGetProperty("layers", out var patterns) ? patterns.EnumerateArray().Select(v => v.GetString()!).ToArray() : null;
        int maxChanges = DraftingPlan.Integer(op, "max_changes", 1, 5000, 500);
        var touched = new List<ObjectId>();
        var changes = new List<object>();
        var definitions = new HashSet<ObjectId>();
        var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int replacements = 0, formatted = 0;
        bool truncated = false;
        void Skip(string reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
        bool OnLayer(Entity e) => layers is null || layers.Any(pattern => CadText.Like(e.Layer, pattern));
        bool Locked(Entity e) => ((LayerTableRecord)tr.GetObject(e.LayerId, OpenMode.ForRead)).IsLocked;
        // Replaces in one stored string; returns false when nothing changed or the change budget is spent.
        bool Change(Entity owner, string kind, string raw, bool mtext, Action<string> write)
        {
            var (text, count) = CadText.Replace(raw, find, replace, matchCase, wholeWord, mtext);
            if (count == 0)
            {
                // The displayed text matches, but formatting codes interrupt the stored text: left unchanged.
                if (mtext && CadText.Replace(CadText.Normalize(raw, true), find, replace, matchCase, wholeWord).Count > 0) formatted++;
                return false;
            }
            if (touched.Count >= maxChanges) { truncated = true; return false; }
            write(text);
            replacements += count;
            if (changes.Count < 200) changes.Add(new { handle = H(owner.ObjectId), kind, before = Clip(CadText.Normalize(raw, mtext)), after = Clip(CadText.Normalize(text, mtext)), count });
            return true;
        }
        void Visit(Entity entity)
        {
            ct.ThrowIfCancellationRequested();
            if (!OnLayer(entity)) return;
            bool changed = false;
            switch (entity)
            {
                case AttributeDefinition definition when include.Contains("block_definitions"):
                    if (definition.HasFields) { Skip("field"); break; }
                    if (Locked(definition)) { Skip("locked_layer"); break; }
                    changed = Change(definition, "attribute_definition", definition.TextString, false, text => { definition.UpgradeOpen(); definition.TextString = text; });
                    break;
                case AttributeDefinition: break;
                case DBText text when include.Contains("text"):
                    if (text.HasFields) { Skip("field"); break; }
                    if (Locked(text)) { Skip("locked_layer"); break; }
                    changed = Change(text, "text", text.TextString, false, value => { text.UpgradeOpen(); text.TextString = value; });
                    break;
                case MText mtext when include.Contains("mtext"):
                    if (mtext.HasFields) { Skip("field"); break; }
                    if (Locked(mtext)) { Skip("locked_layer"); break; }
                    changed = Change(mtext, "mtext", mtext.Contents, true, value => { mtext.UpgradeOpen(); mtext.Contents = value; });
                    break;
                case Dimension dimension when include.Contains("dimensions"):
                    if (string.IsNullOrWhiteSpace(dimension.DimensionText)) break;
                    if (dimension.HasFields) { Skip("field"); break; }
                    if (Locked(dimension)) { Skip("locked_layer"); break; }
                    changed = Change(dimension, "dimension", dimension.DimensionText, true, value => { dimension.UpgradeOpen(); dimension.DimensionText = value; dimension.RecomputeDimensionBlock(true); });
                    break;
                case Table table when include.Contains("tables"):
                    if (Locked(table)) { Skip("locked_layer"); break; }
                    var merges = NativeTables.MergedRanges(table);
                    for (int r = 0; r < Math.Min(table.Rows.Count, 500); r++)
                        for (int c = 0; c < Math.Min(table.Columns.Count, 50); c++)
                        {
                            // A merged range holds its text in the top-left cell; the others must not be written.
                            if (merges.Any(m => r >= m.TopRow && r <= m.BottomRow && c >= m.LeftColumn && c <= m.RightColumn && (m.TopRow != r || m.LeftColumn != c))) continue;
                            var cell = table.Cells[r, c];
                            string raw = cell.TextString ?? "";
                            if (raw.Length == 0) continue;
                            if (!cell.FieldId.IsNull || cell.Contents.Count > 0 && cell.Contents[0].HasFormula) { if (CadText.Contains(raw, find, true)) Skip("field_or_formula"); continue; }
                            int row = r, column = c;
                            changed |= Change(table, "table_cell " + DraftingPlan.Address(r, c), raw, true, value => { table.UpgradeOpen(); table.Cells[row, column].TextString = value; });
                        }
                    break;
                case BlockReference reference when include.Contains("attributes"):
                    foreach (ObjectId id in reference.AttributeCollection)
                    {
                        if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not AttributeReference attribute) continue;
                        if (attribute.HasFields) { if (CadText.Contains(attribute.TextString, find, attribute.IsMTextAttribute)) Skip("field"); continue; }
                        if (Locked(attribute) || Locked(reference)) { if (CadText.Contains(attribute.TextString, find, attribute.IsMTextAttribute)) Skip("locked_layer"); continue; }
                        changed |= Change(reference, "attribute " + attribute.Tag, attribute.TextString, attribute.IsMTextAttribute, value =>
                        {
                            attribute.UpgradeOpen(); attribute.TextString = value;
                            if (attribute.IsMTextAttribute) attribute.UpdateMTextAttribute();
                        });
                    }
                    break;
            }
            if (changed)
            {
                touched.Add(entity.ObjectId);
                definitions.Add(entity.OwnerId);
            }
        }

        string scope;
        if (op.TryGetProperty("handles", out var handles))
        {
            scope = "handles";
            foreach (var value in handles.EnumerateArray())
            {
                var id = Edits.Handle(db, value.GetString()!);
                if (tr.GetObject(id, OpenMode.ForRead) is Entity entity) Visit(entity);
                if (truncated) break;
            }
        }
        else
        {
            scope = op.Text("scope") ?? "current";
            var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var spaces = scope switch
            {
                "current" => [db.CurrentSpaceId],
                "model" => [blocks[BlockTableRecord.ModelSpace]],
                "layout" => [Sheets.Layout(db, tr, op.Text("layout")!).BlockTableRecordId],
                _ => blocks.Cast<ObjectId>().Where(id => ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).IsLayout).ToArray()
            };
            foreach (var space in spaces)
            {
                foreach (ObjectId id in (BlockTableRecord)tr.GetObject(space, OpenMode.ForRead))
                {
                    if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is Entity entity) Visit(entity);
                    if (truncated) break;
                }
                if (truncated) break;
            }
        }
        if (include.Contains("block_definitions") && !truncated)
        {
            // Definitions change every reference; inserted attribute values are separate and stay as they are.
            foreach (ObjectId blockId in (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))
            {
                var block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
                if (block.IsLayout || block.IsFromExternalReference || block.IsDependent || block.IsAnonymous) continue;
                foreach (ObjectId id in block)
                {
                    if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is Entity entity and (DBText or MText)) Visit(entity);
                    if (truncated) break;
                }
                if (truncated) break;
            }
        }
        var (updated, notUpdated) = TextUnits.UpdateDynamicBlocks(tr, definitions);
        return new(ObjectId.Null, touched, new
        {
            find, replace, scope, replacements, changed_objects = touched.Count, changes, changes_truncated = changes.Count < touched.Count,
            skipped = skipped.Count == 0 ? null : skipped, formatted_matches_left = formatted == 0 ? (int?)null : formatted,
            dynamic_blocks_updated = updated, dynamic_blocks_not_updated = notUpdated,
            truncated, note = truncated ? "max_changes reached; run again to continue" : null
        });
    }

    // ---------------------------------------------------------------- curves

    private static Outcome Offset(Database db, Transaction tr, JsonElement op, Dictionary<string, ObjectId> aliases)
    {
        if (Edits.Editable(db, tr, Edits.Resolve(db, tr, op, aliases), false) is not Curve source || source is Polyline3d)
            throw new CadFault("INVALID_OFFSET", "Offset needs a line, arc, circle, 2D polyline, ellipse or spline");
        double distance = N(op, "distance");
        DBObjectCollection curves;
        if (op.TryGetProperty("side_point", out var sideValue))
        {
            // The side is the one the side point lies on, seen from the nearest point of the curve. Each offset is
            // tried on its own: a side AutoCAD cannot build (inside a small circle) is never replaced by the other.
            var side = P(sideValue);
            var foot = source.GetClosestPointTo(side, false);
            var toward = side - foot;
            if (toward.Length < Epsilon) throw new CadFault("INVALID_OFFSET", "side_point lies on the curve; pick a point on the side to offset to");
            DBObjectCollection? chosen = null;
            foreach (double candidate in new[] { Math.Abs(distance), -Math.Abs(distance) })
            {
                DBObjectCollection offset;
                try { offset = source.GetOffsetCurves(candidate); }
                catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                var near = offset.Cast<DBObject>().OfType<Curve>().Select(c => { try { return (Point3d?)c.GetClosestPointTo(foot, false); } catch (Autodesk.AutoCAD.Runtime.Exception) { return null; } })
                    .OfType<Point3d>().OrderBy(p => p.DistanceTo(foot)).FirstOrDefault(foot);
                if (near != foot && (near - foot).DotProduct(toward) > 0) { chosen = offset; distance = candidate; break; }
                Dispose(offset);
            }
            curves = chosen ?? throw new CadFault("OFFSET_FAILED", "AutoCAD produced no offset curve on the side of side_point; the distance may exceed a curvature radius");
        }
        else curves = source.GetOffsetCurves(distance);
        var space = (BlockTableRecord)tr.GetObject(source.OwnerId, OpenMode.ForRead);
        var created = new List<ObjectId>();
        try
        {
            foreach (DBObject item in curves)
            {
                if (item is not Entity curve) { item.Dispose(); continue; }
                Style(db, tr, curve, op);
                created.Add(Append(tr, space, curve).ObjectId);
            }
        }
        catch { foreach (DBObject item in curves) if (item.ObjectId.IsNull) item.Dispose(); throw; }
        if (created.Count == 0) throw new CadFault("OFFSET_FAILED", "AutoCAD produced no offset curve; the distance may exceed a curvature radius");
        return new(created[0], created, new { source_handle = H(source.ObjectId), handles = created.Select(H).ToArray(), distance });
    }

    private static void Dispose(DBObjectCollection items) { foreach (DBObject item in items) item.Dispose(); items.Dispose(); }

    private static Outcome Explode(Database db, Transaction tr, JsonElement op, Dictionary<string, ObjectId> aliases)
    {
        bool keep = Bool(op, "keep_original"), attributesAsText = Bool(op, "attributes_as_text", true);
        var source = Edits.Editable(db, tr, Edits.Resolve(db, tr, op, aliases), !keep);
        var parts = new DBObjectCollection();
        source.Explode(parts);
        var items = parts.Cast<DBObject>().ToList();
        var space = (BlockTableRecord)tr.GetObject(source.OwnerId, OpenMode.ForRead);
        var created = new List<ObjectId>();
        NestedReferences? nestedReferences = null;
        int nestedAttributes = 0, nestedSkipped = 0;
        int attributeTexts = 0;
        try
        {
            if (source is BlockReference reference && reference is not Table && attributesAsText)
                // Attribute references hold the values; exploding would otherwise leave the definitions' tags.
                foreach (ObjectId id in reference.AttributeCollection)
                    if (tr.GetObject(id, OpenMode.ForRead) is AttributeReference { Invisible: false } attribute)
                    {
                        items.Add(attribute.IsMTextAttribute ? MTextFrom(attribute) : TextFrom(attribute));
                        attributeTexts++;
                    }
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item is AttributeDefinition definition && attributesAsText)
                {
                    // A constant attribute has no reference; its value becomes plain text.
                    if (definition.Constant && !definition.Invisible) { items[i] = item = TextFrom(definition); definition.Dispose(); }
                    else { definition.Dispose(); continue; }
                }
                if (item is not Entity entity) { item.Dispose(); continue; }
                if (op.Text("layer") is { } layer) entity.LayerId = Edits.Layer(db, tr, layer);
                Edits.RequireUnlocked(tr, entity);
                Append(tr, space, entity);
                // Explode copies nested block references without their attribute values; carry them over as EXPLODE does.
                if (source is BlockReference outer && entity is BlockReference nested)
                {
                    nestedReferences ??= new NestedReferences(tr, outer);
                    var (copied, skipped) = nestedReferences.CopyAttributes(tr, nested);
                    nestedAttributes += copied; nestedSkipped += skipped;
                }
                if (entity is DBText text && (text.HorizontalMode != TextHorizontalMode.TextLeft || text.VerticalMode != TextVerticalMode.TextBase)) text.AdjustAlignment(db);
                created.Add(entity.ObjectId);
            }
        }
        catch { foreach (var item in items) if (item.ObjectId.IsNull && !item.IsDisposed) item.Dispose(); throw; }
        if (created.Count == 0) throw new CadFault("EXPLODE_FAILED", source.GetType().Name + " produced nothing to explode");
        if (!keep) source.Erase();
        return new(ObjectId.Null, created.Append(source.ObjectId).ToArray(), new
        {
            source_handle = H(source.ObjectId), source_erased = !keep, count = created.Count, handles = created.Take(500).Select(H).ToArray(),
            handles_truncated = created.Count > 500, attributes_as_text = attributeTexts,
            nested_attributes = nestedAttributes == 0 ? (int?)null : nestedAttributes, nested_attributes_not_copied = nestedSkipped == 0 ? (int?)null : nestedSkipped
        });
    }

    /// <summary>
    /// The attributed block references inside an exploded block's definition, read once per explode, so their
    /// values can follow the copies Explode makes without them (as the EXPLODE command keeps them).
    /// </summary>
    private sealed class NestedReferences
    {
        private readonly Matrix3d transform;
        private readonly Dictionary<ObjectId, List<(ObjectId Id, Point3d Position)>> byBlock = new();
        private readonly HashSet<ObjectId> used = new();

        public NestedReferences(Transaction tr, BlockReference outer)
        {
            transform = outer.BlockTransform;
            // Every nested reference takes part in matching, in definition order, so a copy without attributes never
            // takes the values of a neighbour of the same block.
            foreach (ObjectId id in (BlockTableRecord)tr.GetObject(outer.BlockTableRecord, OpenMode.ForRead))
                if (!id.IsErased && id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(BlockReference))) &&
                    tr.GetObject(id, OpenMode.ForRead) is BlockReference nested and not Table)
                {
                    if (!byBlock.TryGetValue(nested.BlockTableRecord, out var list)) byBlock[nested.BlockTableRecord] = list = new();
                    list.Add((id, nested.Position.TransformBy(transform)));
                }
        }

        /// <summary>Copies the values onto <paramref name="copy"/>; returns the attributes copied and those that could not be (a non-uniform scale).</summary>
        public (int Copied, int Skipped) CopyAttributes(Transaction tr, BlockReference copy)
        {
            if (copy.AttributeCollection.Count > 0 || !byBlock.TryGetValue(copy.BlockTableRecord, out var candidates)) return (0, 0);
            double tolerance = 1e-9 * Math.Max(1, copy.Position.GetAsVector().Length);
            var match = candidates.FirstOrDefault(c => !used.Contains(c.Id) && c.Position.DistanceTo(copy.Position) <= tolerance);
            if (match.Id.IsNull) return (0, 0);
            used.Add(match.Id);
            int copied = 0, skipped = 0;
            var original = (BlockReference)tr.GetObject(match.Id, OpenMode.ForRead);
            foreach (ObjectId attributeId in original.AttributeCollection)
            {
                if (attributeId.IsErased || tr.GetObject(attributeId, OpenMode.ForRead) is not AttributeReference attribute) continue;
                var clone = (AttributeReference)attribute.Clone();
                // A value that cannot follow the block's transform (non-uniform scale) is left out, never failing the explode.
                try { clone.TransformBy(transform); }
                catch (Autodesk.AutoCAD.Runtime.Exception) { clone.Dispose(); skipped++; continue; }
                try { copy.AttributeCollection.AppendAttribute(clone); tr.AddNewlyCreatedDBObject(clone, true); copied++; }
                catch { if (clone.ObjectId.IsNull) clone.Dispose(); throw; }
            }
            return (copied, skipped);
        }
    }

    private static DBText TextFrom(DBText source)
    {
        var text = new DBText();
        text.SetPropertiesFrom(source);
        text.Normal = source.Normal; text.Thickness = source.Thickness; text.TextStyleId = source.TextStyleId;
        text.Height = source.Height; text.WidthFactor = source.WidthFactor; text.Oblique = source.Oblique; text.Rotation = source.Rotation;
        text.IsMirroredInX = source.IsMirroredInX; text.IsMirroredInY = source.IsMirroredInY;
        text.TextString = source.TextString; text.Position = source.Position;
        text.HorizontalMode = source.HorizontalMode; text.VerticalMode = source.VerticalMode;
        if (source.HorizontalMode != TextHorizontalMode.TextLeft || source.VerticalMode != TextVerticalMode.TextBase) text.AlignmentPoint = source.AlignmentPoint;
        return text;
    }

    private static MText MTextFrom(AttributeReference attribute)
    {
        var text = attribute.MTextAttribute;
        text.SetPropertiesFrom(attribute);
        return text;
    }

    private static Outcome Join(Database db, Transaction tr, JsonElement op, Dictionary<string, ObjectId> aliases)
    {
        var baseId = Edits.Resolve(db, tr, op, aliases);
        var primary = Edits.Editable(db, tr, baseId, true);
        var otherIds = op.GetProperty("others").EnumerateArray().Select(v => Reference(db, v.GetString()!, aliases)).ToArray();
        if (otherIds.Contains(baseId) || otherIds.Distinct().Count() != otherIds.Length) throw new CadFault("INVALID_JOIN", "List each other curve once and not the base");
        var others = otherIds.Select(id => Edits.Editable(db, tr, id, true, primary.OwnerId)).ToArray();
        if (others.Any(o => o is not Curve)) throw new CadFault("INVALID_JOIN", "Only curves can be joined");
        Entity target = primary;
        bool converted = false;
        if (primary is Line or Arc)
        {
            // Lines and arcs join into a polyline, as the JOIN command does for mixed segments.
            target = Append(tr, (BlockTableRecord)tr.GetObject(primary.OwnerId, OpenMode.ForRead), ToPolyline((Curve)primary));
            converted = true;
        }
        if (target is not (Polyline or Polyline3d or Spline)) throw new CadFault("INVALID_JOIN", "The base must be a line, arc, polyline, 3D polyline or spline");
        IntegerCollection joined;
        try { joined = target.JoinEntities(others); }
        catch (Autodesk.AutoCAD.Runtime.Exception error) { throw new CadFault("JOIN_FAILED", "AutoCAD could not join (" + error.ErrorStatus + "); curves must meet end to end in one plane"); }
        var joinedEntities = joined.Cast<int>().Where(i => i >= 0 && i < others.Length).Select(i => others[i]).Distinct().ToArray();
        if (joinedEntities.Length == 0) throw new CadFault("JOIN_FAILED", "None of the curves meets the base end to end");
        foreach (var entity in joinedEntities) entity.Erase();
        if (converted) primary.Erase();
        var erased = joinedEntities.Select(e => e.ObjectId).Concat(converted ? [primary.ObjectId] : []).ToArray();
        return new(target.ObjectId, erased.Prepend(target.ObjectId).ToArray(), new
        {
            handle = H(target.ObjectId), converted_to_polyline = converted, joined = joinedEntities.Select(e => H(e.ObjectId)).ToArray(),
            not_joined = others.Except(joinedEntities).Select(e => H(e.ObjectId)).ToArray()
        });
    }

    private static Polyline ToPolyline(Curve curve)
    {
        var polyline = new Polyline();
        polyline.SetPropertiesFrom(curve);
        switch (curve)
        {
            case Line line:
                if (Math.Abs(line.StartPoint.Z - line.EndPoint.Z) > 1e-8) throw new CadFault("INVALID_JOIN", "A sloped 3D line cannot become a 2D polyline; use a 3D polyline as the base");
                polyline.Elevation = line.StartPoint.Z;
                polyline.AddVertexAt(0, new Point2d(line.StartPoint.X, line.StartPoint.Y), 0, 0, 0);
                polyline.AddVertexAt(1, new Point2d(line.EndPoint.X, line.EndPoint.Y), 0, 0, 0);
                break;
            case Arc arc:
                if (!(arc.Normal.Z > 0 && arc.Normal.IsParallelTo(Vector3d.ZAxis))) throw new CadFault("INVALID_JOIN", "Only arcs in a WCS XY plane can become a polyline");
                polyline.Elevation = arc.Center.Z;
                polyline.AddVertexAt(0, new Point2d(arc.StartPoint.X, arc.StartPoint.Y), Math.Tan(arc.TotalAngle / 4), 0, 0);
                polyline.AddVertexAt(1, new Point2d(arc.EndPoint.X, arc.EndPoint.Y), 0, 0, 0);
                break;
        }
        return polyline;
    }

    private static Outcome ArrayCopies(Database db, Transaction tr, JsonElement op, Dictionary<string, ObjectId> aliases, CancellationToken ct)
    {
        string kind = op.Text("op")!;
        var sources = op.GetProperty("items").EnumerateArray().Select(v => Edits.Editable(db, tr, Reference(db, v.GetString()!, aliases), false)).ToArray();
        // One transform per new position; polar arrays without rotation translate each item's centre.
        var positions = new List<Func<Entity, Matrix3d>>();
        if (kind == "array_rect")
        {
            int rows = DraftingPlan.Integer(op, "rows", 1, 100), columns = DraftingPlan.Integer(op, "columns", 1, 100);
            double rowSpacing = N(op, "row_spacing", 0), columnSpacing = N(op, "column_spacing", 0), angle = N(op, "angle_deg", 0) * Math.PI / 180;
            var x = new Vector3d(Math.Cos(angle), Math.Sin(angle), 0); var y = new Vector3d(-Math.Sin(angle), Math.Cos(angle), 0);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < columns; c++)
                    if (r != 0 || c != 0) { var shift = Matrix3d.Displacement(x * (c * columnSpacing) + y * (r * rowSpacing)); positions.Add(_ => shift); }
        }
        else
        {
            int count = DraftingPlan.Integer(op, "count", 2, 500);
            double total = N(op, "angle_deg", 360) * Math.PI / 180;
            // A full circle spreads the items evenly; a partial angle places the last item on its end.
            double step = Math.Abs(Math.Abs(total) - 2 * Math.PI) < 1e-9 ? total / count : total / (count - 1);
            var center = P(op, "center");
            bool rotate = Bool(op, "rotate_items", true);
            // Without rotation the whole selection moves as one: every item follows the centre of all of them.
            Point3d middle = Point3d.Origin;
            if (!rotate)
            {
                var box = sources[0].GeometricExtents;
                foreach (var item in sources.Skip(1)) box.AddExtents(item.GeometricExtents);
                middle = box.MinPoint + (box.MaxPoint - box.MinPoint) / 2;
            }
            for (int i = 1; i < count; i++)
            {
                var rotation = Matrix3d.Rotation(step * i, Vector3d.ZAxis, center);
                var shift = Matrix3d.Displacement(middle.TransformBy(rotation) - middle);
                positions.Add(rotate ? _ => rotation : _ => shift);
            }
        }
        var created = new List<ObjectId>();
        foreach (var position in positions)
        {
            ct.ThrowIfCancellationRequested();
            var mapping = new IdMapping();
            db.DeepCloneObjects(new ObjectIdCollection(sources.Select(s => s.ObjectId).ToArray()), sources[0].OwnerId, mapping, false);
            foreach (var source in sources)
            {
                var copy = (Entity)tr.GetObject(mapping[source.ObjectId].Value, OpenMode.ForWrite);
                Edits.RequireUnlocked(tr, copy);
                Edits.Transform(copy, position(source), tr);
                created.Add(copy.ObjectId);
            }
        }
        return new(ObjectId.Null, created, new { kind, copies = created.Count, handles = created.Take(500).Select(H).ToArray(), handles_truncated = created.Count > 500, associative = false });
    }

    /// <summary>Rounds corners of a lightweight polyline in place, as FILLET's Polyline option; the polyline keeps its handle.</summary>
    private static Outcome RoundPolyline(Database db, Transaction tr, JsonElement op, Dictionary<string, ObjectId> aliases)
    {
        if (Edits.Editable(db, tr, Edits.Resolve(db, tr, op, aliases), true) is not Polyline polyline)
            throw new CadFault("INVALID_POLYLINE", "polyline_fillet rounds a lightweight polyline (LWPOLYLINE); convert an old-style 2D polyline with CONVERTPOLY, or use fillet for two lines");
        int count = polyline.NumberOfVertices;
        var vertices = new PolylineVertex[count];
        double extent = 0;
        for (int i = 0; i < count; i++)
        {
            var point = polyline.GetPoint2dAt(i);
            vertices[i] = new(point.X, point.Y, polyline.GetBulgeAt(i), polyline.GetStartWidthAt(i), polyline.GetEndWidthAt(i));
            extent = Math.Max(extent, Math.Max(Math.Abs(point.X), Math.Abs(point.Y)));
        }
        int[]? at = op.TryGetProperty("vertices", out var list) ? list.EnumerateArray().Select(v => v.GetInt32()).ToArray() : null;
        double radius = N(op, "radius");
        var result = PolylineFillet.Apply(vertices, polyline.Closed, radius, at, Math.Max(1e-9, extent * 1e-12));
        if (result.Filleted.Count == 0)
            throw new CadFault("FILLET_NOT_APPLIED", "No corner could be rounded: " + (result.Skipped.Count == 0 ? "the polyline has no corner between two straight segments"
                : string.Join("; ", result.Skipped.Take(10).Select(s => "vertex " + s.Vertex + ": " + s.Reason))));
        // The polyline is rewritten in place: existing vertices take the new values, the rest are added or removed.
        for (int i = 0; i < result.Vertices.Count; i++)
        {
            var v = result.Vertices[i];
            if (i < count)
            {
                polyline.SetPointAt(i, new Point2d(v.X, v.Y));
                polyline.SetBulgeAt(i, v.Bulge);
                polyline.SetStartWidthAt(i, v.StartWidth);
                polyline.SetEndWidthAt(i, v.EndWidth);
            }
            else polyline.AddVertexAt(i, new Point2d(v.X, v.Y), v.Bulge, v.StartWidth, v.EndWidth);
        }
        for (int i = count - 1; i >= result.Vertices.Count; i--) polyline.RemoveVertexAt(i);
        return new(polyline.ObjectId, [polyline.ObjectId], new
        {
            handle = H(polyline.ObjectId), radius, vertices_before = count, vertices_after = polyline.NumberOfVertices, length = polyline.Length,
            filleted = result.Filleted, skipped = result.Skipped.Count == 0 ? null : result.Skipped.Take(200).Select(s => new { vertex = s.Vertex, reason = s.Reason }).ToArray()
        });
    }

    /// <summary>Fillet (radius 0 makes a sharp corner) or chamfer of two lines in one WCS XY plane.</summary>
    private static Outcome Corner(Database db, Transaction tr, JsonElement op, Dictionary<string, ObjectId> aliases)
    {
        string kind = op.Text("op")!;
        var firstId = Edits.Resolve(db, tr, op, aliases);
        var secondId = Other(db, op, "other_handle", "other_target", aliases);
        if (firstId == secondId) throw new CadFault("INVALID_CORNER", "Use two different lines");
        if (Edits.Editable(db, tr, firstId, true) is not Line first || Edits.Editable(db, tr, secondId, true, ((Entity)tr.GetObject(firstId, OpenMode.ForRead)).OwnerId) is not Line second)
            throw new CadFault("INVALID_CORNER", kind + " joins two lines; for polylines edit the vertices");
        double z = first.StartPoint.Z;
        if (new[] { first.EndPoint.Z, second.StartPoint.Z, second.EndPoint.Z }.Any(v => Math.Abs(v - z) > 1e-8)) throw new CadFault("INVALID_CORNER", "Both lines must lie in one WCS XY plane");
        var corner = Intersection(first, second) ?? throw new CadFault("PARALLEL_LINES", "The lines are parallel and have no corner");
        // Each line keeps the end farther from the corner.
        bool firstKeepsStart = first.StartPoint.DistanceTo(corner) >= first.EndPoint.DistanceTo(corner);
        bool secondKeepsStart = second.StartPoint.DistanceTo(corner) >= second.EndPoint.DistanceTo(corner);
        var far1 = firstKeepsStart ? first.StartPoint : first.EndPoint;
        var far2 = secondKeepsStart ? second.StartPoint : second.EndPoint;
        double length1 = far1.DistanceTo(corner), length2 = far2.DistanceTo(corner);
        if (length1 < Epsilon || length2 < Epsilon) throw new CadFault("INVALID_CORNER", "A line ends at the corner on both sides");
        var u1 = (far1 - corner) / length1; var u2 = (far2 - corner) / length2;
        double theta = u1.GetAngleTo(u2);
        if (theta < 1e-9 || Math.PI - theta < 1e-9) throw new CadFault("PARALLEL_LINES", "The lines are collinear");
        double d1, d2;
        if (kind == "fillet") { double radius = N(op, "radius"); d1 = d2 = radius / Math.Tan(theta / 2); }
        else { d1 = N(op, "distance"); d2 = N(op, "other_distance", d1); }
        if (d1 >= length1 - 1e-9 || d2 >= length2 - 1e-9) throw new CadFault(kind == "fillet" ? "FILLET_TOO_LARGE" : "CHAMFER_TOO_LARGE", "The corner would consume a whole line");
        var t1 = corner + u1 * d1; var t2 = corner + u2 * d2;
        if (firstKeepsStart) first.EndPoint = t1; else first.StartPoint = t1;
        if (secondKeepsStart) second.EndPoint = t2; else second.StartPoint = t2;
        Entity? joint = null;
        if (kind == "chamfer") joint = new Line(t1, t2);
        else if (N(op, "radius") > 0)
        {
            double radius = N(op, "radius");
            var center = corner + (u1 + u2).GetNormal() * (radius / Math.Sin(theta / 2));
            double a1 = Math.Atan2(t1.Y - center.Y, t1.X - center.X), a2 = Math.Atan2(t2.Y - center.Y, t2.X - center.X);
            // The fillet is the short arc between the tangent points, counter-clockwise from start to end.
            bool counterClockwise = (t1.X - center.X) * (t2.Y - center.Y) - (t1.Y - center.Y) * (t2.X - center.X) > 0;
            joint = new Arc(new Point3d(center.X, center.Y, z), Vector3d.ZAxis, radius, counterClockwise ? a1 : a2, counterClockwise ? a2 : a1);
        }
        var touched = new List<ObjectId> { first.ObjectId, second.ObjectId };
        if (joint is not null)
        {
            try
            {
                joint.SetPropertiesFrom(first);
                Style(db, tr, joint, op);
                Append(tr, (BlockTableRecord)tr.GetObject(first.OwnerId, OpenMode.ForRead), joint);
            }
            catch { if (joint.ObjectId.IsNull) joint.Dispose(); throw; }
            touched.Insert(0, joint.ObjectId);
        }
        return new(joint?.ObjectId ?? first.ObjectId, touched, new
        {
            handle = joint is null ? null : H(joint.ObjectId), lines = new[] { H(first.ObjectId), H(second.ObjectId) },
            corner = new[] { corner.X, corner.Y, corner.Z }, angle_deg = theta * 180 / Math.PI
        });
    }

    private static Point3d? Intersection(Line a, Line b)
    {
        double x1 = a.StartPoint.X, y1 = a.StartPoint.Y, x2 = a.EndPoint.X, y2 = a.EndPoint.Y;
        double x3 = b.StartPoint.X, y3 = b.StartPoint.Y, x4 = b.EndPoint.X, y4 = b.EndPoint.Y;
        double denominator = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
        double scale = Math.Max(a.Length * b.Length, Epsilon);
        if (Math.Abs(denominator) < scale * 1e-12) return null;
        double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / denominator;
        return new Point3d(x1 + t * (x2 - x1), y1 + t * (y2 - y1), a.StartPoint.Z);
    }

    private static Outcome Trim(Database db, Transaction tr, JsonElement op, Dictionary<string, ObjectId> aliases)
    {
        if (Edits.Editable(db, tr, Edits.Resolve(db, tr, op, aliases), true) is not Curve curve) throw new CadFault("INVALID_TRIM", "trim needs a curve");
        var boundaries = op.GetProperty("boundaries").EnumerateArray().Select(v => Edits.Editable(db, tr, Reference(db, v.GetString()!, aliases), false)).ToArray();
        if (boundaries.Any(b => b.ObjectId == curve.ObjectId)) throw new CadFault("INVALID_TRIM", "A curve cannot trim itself");
        var points = new Point3dCollection();
        foreach (var boundary in boundaries) curve.IntersectWith(boundary, Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero);
        double start = curve.StartParam, end = curve.EndParam, tolerance = Math.Max(Epsilon, (end - start) * 1e-9);
        var parameters = new List<double>();
        foreach (Point3d point in points)
        {
            double parameter;
            try { parameter = curve.GetParameterAtPoint(curve.GetClosestPointTo(point, false)); }
            catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
            if (!curve.Closed && (parameter <= start + tolerance || parameter >= end - tolerance)) continue;
            if (parameters.All(p => Math.Abs(p - parameter) > tolerance)) parameters.Add(parameter);
        }
        parameters.Sort();
        if (parameters.Count == 0 || curve.Closed && parameters.Count < 2)
            throw new CadFault("TRIM_NO_INTERSECTION", curve.Closed ? "A closed curve needs two crossings with the boundaries" : "The boundaries do not cross the curve between its ends");
        var pick = P(op, "pick_point");
        // As TRIM does, only the span between the crossings on either side of the pick point goes; the curve is
        // split nowhere else. On a closed curve the span may wrap past its start.
        double picked;
        try { picked = curve.GetParameterAtPoint(curve.GetClosestPointTo(pick, false)); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { throw new CadFault("TRIM_FAILED", "The pick point could not be located on the curve"); }
        double? below = parameters.Where(p => p < picked).Select(p => (double?)p).LastOrDefault(), above = parameters.Where(p => p > picked).Select(p => (double?)p).FirstOrDefault();
        if (curve.Closed) { below ??= parameters[^1]; above ??= parameters[0]; }
        if (parameters.Any(p => Math.Abs(p - picked) <= tolerance)) throw new CadFault("TRIM_FAILED", "The pick point lies on a crossing; pick a point inside the part to remove");
        var cuts = new[] { below, above }.OfType<double>().Distinct().Order().ToArray();
        if (cuts.Length == 0 || curve.Closed && cuts.Length < 2) throw new CadFault("TRIM_FAILED", "No crossing bounds the part at the pick point");
        var pieces = curve.GetSplitCurves(new DoubleCollection(cuts));
        var list = pieces.Cast<DBObject>().OfType<Curve>().ToList();
        int removed = -1; double best = double.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            double distance;
            try { distance = list[i].GetClosestPointTo(pick, false).DistanceTo(pick); } catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
            if (distance < best) { best = distance; removed = i; }
        }
        if (removed < 0) { Dispose(pieces); throw new CadFault("TRIM_FAILED", "AutoCAD could not split the curve"); }
        double removedLength = Length(list[removed]);
        var space = (BlockTableRecord)tr.GetObject(curve.OwnerId, OpenMode.ForRead);
        var created = new List<ObjectId>();
        try
        {
            for (int i = 0; i < list.Count; i++)
                if (i == removed) list[i].Dispose(); else created.Add(Append(tr, space, list[i]).ObjectId);
        }
        catch { foreach (var piece in list) if (piece.ObjectId.IsNull && !piece.IsDisposed) piece.Dispose(); throw; }
        curve.Erase();
        return new(ObjectId.Null, created.Append(curve.ObjectId).ToArray(), new { source_handle = H(curve.ObjectId), handles = created.Select(H).ToArray(), removed_length = removedLength, cuts = cuts.Length, crossings = parameters.Count });
    }

    private static double Length(Curve curve)
    {
        try { return Math.Abs(curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam)); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return double.NaN; }
    }

    private static Outcome Extend(Database db, Transaction tr, JsonElement op, Dictionary<string, ObjectId> aliases)
    {
        if (Edits.Editable(db, tr, Edits.Resolve(db, tr, op, aliases), true) is not Curve curve || curve is not (Line or Arc or Polyline) || curve.Closed)
            throw new CadFault("INVALID_EXTEND", "extend supports open lines, arcs and 2D polylines");
        // Arc angles and polyline segments are measured in the WCS XY plane.
        if (curve is Arc { Normal: var arcNormal } && !(arcNormal.Z > 0 && arcNormal.IsParallelTo(Vector3d.ZAxis)) ||
            curve is Polyline { Normal: var polylineNormal } && !(polylineNormal.Z > 0 && polylineNormal.IsParallelTo(Vector3d.ZAxis)))
            throw new CadFault("INVALID_EXTEND", "Only arcs and polylines in a WCS XY plane can be extended");
        bool atStart = op.Text("end") == "start";
        var boundaries = op.GetProperty("boundaries").EnumerateArray().Select(v => Edits.Editable(db, tr, Reference(db, v.GetString()!, aliases), false)).ToArray();
        if (boundaries.Any(b => b.ObjectId == curve.ObjectId)) throw new CadFault("INVALID_EXTEND", "A curve cannot be its own boundary");
        var candidates = new Point3dCollection();
        foreach (var boundary in boundaries) curve.IntersectWith(boundary, Intersect.ExtendThis, candidates, IntPtr.Zero, IntPtr.Zero);
        var endPoint = atStart ? curve.StartPoint : curve.EndPoint;
        // Distance travelled beyond the end along the curve's own extension, or null for points not on it.
        Func<Point3d, double?> beyond = curve switch
        {
            Arc arc => ArcTravel(arc.Center, arc.StartAngle, arc.EndAngle, atStart),
            Polyline polyline => EndSegment(polyline, atStart),
            _ => LineTravel(((Line)curve).StartPoint, ((Line)curve).EndPoint, atStart)
        };
        double best = double.MaxValue; Point3d? target = null;
        foreach (Point3d candidate in candidates)
            if (beyond(candidate) is { } travel && travel > 1e-9 && travel < best) { best = travel; target = candidate; }
        if (target is null) throw new CadFault("EXTEND_NO_INTERSECTION", "No boundary lies on the extension beyond the " + (atStart ? "start" : "end"));
        double before = Length(curve);
        curve.Extend(atStart, target.Value);
        // Arc travel is an angle; the length gained is measured along the curve.
        best = Length(curve) - before;
        return new(curve.ObjectId, [curve.ObjectId], new { handle = H(curve.ObjectId), end = atStart ? "start" : "end", from = new[] { endPoint.X, endPoint.Y, endPoint.Z }, to = new[] { target.Value.X, target.Value.Y, target.Value.Z }, added_length = best });
    }

    private static Func<Point3d, double?> LineTravel(Point3d start, Point3d end, bool atStart)
    {
        var origin = atStart ? start : end;
        var direction = (atStart ? start - end : end - start).GetNormal();
        return point =>
        {
            var offset = point - origin;
            double along = offset.DotProduct(direction);
            return (offset - direction * along).Length <= 1e-6 * Math.Max(1, Math.Abs(along)) ? along : null;
        };
    }

    private static Func<Point3d, double?> ArcTravel(Point3d center, double startAngle, double endAngle, bool atStart)
    {
        double sweep = endAngle - startAngle;
        while (sweep <= 0) sweep += 2 * Math.PI;
        return point =>
        {
            double angle = Math.Atan2(point.Y - center.Y, point.X - center.X);
            double travel = atStart ? startAngle - angle : angle - endAngle;
            travel %= 2 * Math.PI; if (travel < 0) travel += 2 * Math.PI;
            // Points on the existing arc are not an extension.
            return travel < 2 * Math.PI - sweep - 1e-9 ? travel : null;
        };
    }

    private static Func<Point3d, double?> EndSegment(Polyline polyline, bool atStart)
    {
        int index = atStart ? 0 : polyline.NumberOfVertices - 2;
        if (index < 0) throw new CadFault("INVALID_EXTEND", "The polyline needs at least two vertices");
        if (polyline.GetSegmentType(index) == SegmentType.Arc)
        {
            var arc = polyline.GetArcSegmentAt(index);
            var start = arc.StartPoint; var end = arc.EndPoint;
            double a0 = Math.Atan2(start.Y - arc.Center.Y, start.X - arc.Center.X), a1 = Math.Atan2(end.Y - arc.Center.Y, end.X - arc.Center.X);
            // CircularArc3d runs counter-clockwise about its normal; a clockwise bulge has the normal reversed.
            bool clockwise = arc.Normal.Z < 0;
            return clockwise ? ArcTravel(arc.Center, a1, a0, !atStart) : ArcTravel(arc.Center, a0, a1, atStart);
        }
        var line = polyline.GetLineSegmentAt(index);
        return LineTravel(line.StartPoint, line.EndPoint, atStart);
    }

    // ---------------------------------------------------------------- annotation

    private static ObjectId DimensionStyle(Database db, Transaction tr, string? name)
    {
        if (name is null) return db.Dimstyle;
        var styles = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
        return styles.Has(name) ? styles[name] : throw new CadFault("STYLE_NOT_FOUND", name);
    }

    private static Outcome AngularDimension(Database db, Transaction tr, JsonElement op)
    {
        Point3d center = P(op, "center"), first = P(op, "first"), second = P(op, "second"), position = P(op, "position");
        if (first.DistanceTo(center) < Epsilon || second.DistanceTo(center) < Epsilon) throw new CadFault("DEGENERATE_GEOMETRY", "first and second must differ from center");
        if ((first - center).GetAngleTo(second - center) < 1e-9) throw new CadFault("DEGENERATE_GEOMETRY", "The two legs must not coincide");
        var style = DimensionStyle(db, tr, op.Text("style"));
        var dimension = new Point3AngularDimension(center, first, second, position, op.Text("text") ?? "", style);
        try
        {
            dimension.SetDatabaseDefaults(db);
            dimension.DimensionStyle = style;
            Style(db, tr, dimension, op);
            Append(tr, TargetSpace(db, tr, op), dimension);
        }
        catch { if (dimension.ObjectId.IsNull) dimension.Dispose(); throw; }
        dimension.RecomputeDimensionBlock(true);
        return new(dimension.ObjectId, [dimension.ObjectId], new { handle = H(dimension.ObjectId), measurement_deg = dimension.Measurement * 180 / Math.PI });
    }

    private static Outcome Leader(Database db, Transaction tr, JsonElement op)
    {
        var points = op.GetProperty("points").EnumerateArray().Select(P).ToArray();
        if (points.Zip(points.Skip(1)).Any(pair => pair.First.DistanceTo(pair.Second) < Epsilon)) throw new CadFault("DEGENERATE_GEOMETRY", "Leader points must differ");
        var leader = new MLeader();
        try
        {
            leader.SetDatabaseDefaults(db);
            if (op.Text("style") is { } styleName)
            {
                var styles = (DBDictionary)tr.GetObject(db.MLeaderStyleDictionaryId, OpenMode.ForRead);
                leader.MLeaderStyle = styles.Contains(styleName) ? styles.GetAt(styleName) : throw new CadFault("STYLE_NOT_FOUND", styleName);
            }
            leader.ContentType = ContentType.MTextContent;
            // The leader keeps a copy of this MText.
            using var text = new MText();
            text.SetDatabaseDefaults(db);
            text.Contents = op.GetProperty("text").GetString()!;
            if (op.Text("text_style") is { } textStyle) { text.TextStyleId = Edits.TextStyle(db, tr, textStyle); leader.TextStyleId = text.TextStyleId; }
            if (op.TryGetProperty("height", out _)) { text.TextHeight = N(op, "height"); leader.TextHeight = text.TextHeight; }
            text.Location = points[^1];
            leader.MText = text;
            // The leader line ends at the last point (next to the text); earlier points are prepended, the first is the arrowhead.
            int line = leader.AddLeaderLine(points[^1]);
            for (int i = points.Length - 2; i >= 0; i--) leader.AddFirstVertex(line, points[i]);
            Style(db, tr, leader, op);
            Append(tr, TargetSpace(db, tr, op), leader);
        }
        catch { if (leader.ObjectId.IsNull) leader.Dispose(); throw; }
        return new(leader.ObjectId, [leader.ObjectId], new { handle = H(leader.ObjectId), arrowhead = new[] { points[0].X, points[0].Y, points[0].Z } });
    }

    private static readonly IReadOnlyDictionary<string, Func<Entity, bool>> PropertyOwners = new Dictionary<string, Func<Entity, bool>>(StringComparer.OrdinalIgnoreCase)
    {
        ["Length"] = e => e is Line or Polyline or Polyline2d or Polyline3d,
        ["Area"] = e => e is Circle or Polyline or Polyline2d or Hatch or Region or Ellipse or Spline,
        ["ArcLength"] = e => e is Arc,
        ["Circumference"] = e => e is Circle,
        ["Perimeter"] = e => e is Region,
        ["Radius"] = e => e is Circle or Arc,
        ["Diameter"] = e => e is Circle
    };

    /// <summary>MText whose value is a live field showing a property of another object, as the FIELD command makes.</summary>
    private static Outcome FieldText(Database db, Transaction tr, JsonElement op, Dictionary<string, ObjectId> aliases)
    {
        var objectId = Other(db, op, "object_handle", "object_target", aliases);
        if (objectId.IsErased || tr.GetObject(objectId, OpenMode.ForRead) is not Entity source) throw new CadFault("ENTITY_NOT_FOUND", H(objectId));
        string property = ModifyPlan.FieldProperties.First(p => string.Equals(p, op.Text("property"), StringComparison.OrdinalIgnoreCase));
        if (!PropertyOwners[property](source)) throw new CadFault("PROPERTY_NOT_AVAILABLE", source.GetType().Name + " has no " + property + " for a field");
        int precision = DraftingPlan.Integer(op, "precision", 0, 8, 2);
        double factor = N(op, "factor", 1);
        string format = "%lu2%pr" + precision + (Math.Abs(factor - 1) > 1e-15 ? "%ct8[" + factor.ToString("R", CultureInfo.InvariantCulture) + "]" : "");
        string code = "%<\\AcObjProp Object(%<\\_ObjId " + objectId.OldIdPtr.ToInt64().ToString(CultureInfo.InvariantCulture) + ">%)." + property + " \\f \"" + format + "\">%";
        static string Literal(string? text) => (text ?? "").Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");
        string contents = Literal(op.Text("prefix")) + code + Literal(op.Text("suffix"));
        var mtext = new MText();
        try
        {
            mtext.SetDatabaseDefaults(db);
            mtext.Location = P(op, "position");
            mtext.TextStyleId = Edits.TextStyle(db, tr, op.Text("style"));
            mtext.TextHeight = op.TryGetProperty("height", out _) ? N(op, "height") : db.Textsize;
            mtext.Attachment = AttachmentPoint.TopLeft;
            mtext.Contents = contents;
            Style(db, tr, mtext, op);
            Append(tr, TargetSpace(db, tr, op), mtext);
        }
        catch { if (mtext.ObjectId.IsNull) mtext.Dispose(); throw; }
        var field = new Field(contents, true);
        mtext.SetField(field);
        tr.AddNewlyCreatedDBObject(field, true);
        field.Evaluate();
        var status = field.EvaluationStatus;
        if (status.Status != FieldEvaluationStatus.Success)
            throw new CadFault("FIELD_EVALUATION_FAILED", property + " of " + source.GetType().Name + ": " + status.Status + " " + status.ErrorMessage);
        return new(mtext.ObjectId, [mtext.ObjectId], new { handle = H(mtext.ObjectId), object_handle = H(objectId), property, field_code = code, value = mtext.Text });
    }

    // ---------------------------------------------------------------- drawing structure

    private static void SameDrawing(Database db, string path)
    {
        if (!File.Exists(path)) throw new CadFault("FILE_NOT_FOUND", path);
        if (!string.IsNullOrEmpty(db.Filename) && string.Equals(Path.GetFullPath(path), Path.GetFullPath(db.Filename), StringComparison.OrdinalIgnoreCase))
            throw new CadFault("SAME_DRAWING", "The file is the drawing being edited");
    }

    private static Outcome AttachXref(Database db, Transaction tr, JsonElement op)
    {
        string path = op.Text("path")!;
        SameDrawing(db, path);
        string name = op.Text("name") ?? Path.GetFileNameWithoutExtension(path);
        SymbolUtilityServices.ValidateSymbolName(name, false);
        if (((BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead)).Has(name)) throw new CadFault("BLOCK_EXISTS", name + " already names a block or external reference");
        bool overlay = Bool(op, "overlay");
        var definitionId = overlay ? db.OverlayXref(path, name) : db.AttachXref(path, name);
        if (definitionId.IsNull) throw new CadFault("XREF_FAILED", "AutoCAD did not attach " + path);
        double scale = N(op, "scale", 1);
        var reference = new BlockReference(P(op, "position"), definitionId) { Rotation = N(op, "rotation_deg", 0) * Math.PI / 180, ScaleFactors = new Scale3d(scale) };
        try
        {
            reference.SetDatabaseDefaults(db);
            Style(db, tr, reference, op);
            Append(tr, (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead), reference);
        }
        catch { if (reference.ObjectId.IsNull) reference.Dispose(); throw; }
        var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
        return new(reference.ObjectId, [reference.ObjectId], new { handle = H(reference.ObjectId), name, path, overlay, status = definition.XrefStatus.ToString(), definition_handle = H(definitionId) });
    }

    private static Outcome ImportBlocks(Database db, Transaction tr, JsonElement op)
    {
        string path = op.Text("path")!;
        SameDrawing(db, path);
        var names = op.GetProperty("names").EnumerateArray().Select(v => v.GetString()!).ToArray();
        bool overwrite = Bool(op, "overwrite");
        using var source = new Database(false, true);
        source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
        source.CloseInput(true);
        var ids = new ObjectIdCollection();
        var missing = new List<string>();
        using (var sourceTransaction = source.TransactionManager.StartOpenCloseTransaction())
        {
            var table = (BlockTable)sourceTransaction.GetObject(source.BlockTableId, OpenMode.ForRead);
            foreach (var name in names)
            {
                if (!table.Has(name)) { missing.Add(name); continue; }
                var record = (BlockTableRecord)sourceTransaction.GetObject(table[name], OpenMode.ForRead);
                if (record.IsLayout || record.IsFromExternalReference || record.IsDependent) throw new CadFault("UNSUPPORTED_BLOCK", name + " is a layout, an external reference or a dependent block");
                ids.Add(record.ObjectId);
            }
        }
        if (missing.Count > 0) throw new CadFault("BLOCK_NOT_FOUND", Path.GetFileName(path) + " has no " + string.Join(", ", missing));
        var target = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var existing = names.Where(target.Has).ToArray();
        var mapping = new IdMapping();
        source.WblockCloneObjects(ids, db.BlockTableId, mapping, overwrite ? DuplicateRecordCloning.Replace : DuplicateRecordCloning.Ignore, false);
        return new(ObjectId.Null, [], new
        {
            path, imported = names.Except(existing).ToArray(), replaced = overwrite ? existing : [], kept_existing = overwrite ? [] : existing,
            note = existing.Length > 0 && !overwrite ? "Existing definitions with these names were kept; set overwrite to replace them" : null
        });
    }

    private static Outcome MergeLayers(Database db, Transaction tr, JsonElement op, CancellationToken ct)
    {
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        bool create = Bool(op, "create_missing"), includeBlocks = Bool(op, "include_blocks", true), purge = Bool(op, "purge", true);
        var map = new Dictionary<ObjectId, ObjectId>();
        var names = new Dictionary<ObjectId, string>();
        foreach (var pair in op.GetProperty("mapping").EnumerateObject())
        {
            if (!layers.Has(pair.Name)) throw new CadFault("LAYER_NOT_FOUND", pair.Name);
            var source = (LayerTableRecord)tr.GetObject(layers[pair.Name], OpenMode.ForRead);
            if (source.IsDependent) throw new CadFault("XREF_LAYER", pair.Name + " belongs to an external reference");
            if (source.IsLocked) throw new CadFault("LAYER_LOCKED", pair.Name + "; unlock it before merging");
            string targetName = pair.Value.GetString()!;
            ObjectId target;
            if (layers.Has(targetName)) target = Edits.Layer(db, tr, targetName);
            else if (create)
            {
                SymbolUtilityServices.ValidateSymbolName(targetName, false);
                var record = new LayerTableRecord { Name = targetName, Color = source.Color, LinetypeObjectId = source.LinetypeObjectId, LineWeight = source.LineWeight, IsPlottable = source.IsPlottable, Transparency = source.Transparency };
                if (!layers.IsWriteEnabled) layers.UpgradeOpen();
                target = layers.Add(record); tr.AddNewlyCreatedDBObject(record, true);
            }
            else throw new CadFault("LAYER_NOT_FOUND", targetName + "; set create_missing or add a layer operation first");
            if (map.ContainsKey(source.ObjectId)) throw new CadFault("INVALID_MAPPING", pair.Name + " is listed twice");
            map[source.ObjectId] = target; names[source.ObjectId] = source.Name;
        }
        if (map.Keys.Any(map.ContainsValue)) throw new CadFault("INVALID_MAPPING", "A layer cannot be both merged and a merge target");
        var zero = layers["0"];
        var moved = map.Keys.ToDictionary(id => id, _ => 0);
        void Move(Entity entity, bool inBlock)
        {
            // Layer 0 inside a block means "inherit the insertion layer"; it is never rewritten there.
            if (!map.TryGetValue(entity.LayerId, out var target) || inBlock && entity.LayerId == zero) return;
            moved[entity.LayerId]++;
            entity.UpgradeOpen(); entity.LayerId = target;
        }
        foreach (ObjectId blockId in (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))
        {
            var block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
            if (block.IsFromExternalReference || block.IsDependent || !block.IsLayout && !includeBlocks) continue;
            foreach (ObjectId id in block)
            {
                ct.ThrowIfCancellationRequested();
                if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
                Move(entity, !block.IsLayout);
                if (entity is BlockReference reference)
                    foreach (ObjectId attributeId in reference.AttributeCollection)
                        if (tr.GetObject(attributeId, OpenMode.ForRead) is AttributeReference attribute) Move(attribute, !block.IsLayout);
            }
        }
        var purged = new List<string>();
        if (purge)
        {
            var candidates = new ObjectIdCollection(map.Keys.Where(id => id != zero && id != db.Clayer && !string.Equals(names[id], "Defpoints", StringComparison.OrdinalIgnoreCase)).ToArray());
            if (candidates.Count > 0) db.Purge(candidates);
            foreach (ObjectId id in candidates) { var layer = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite); purged.Add(layer.Name); layer.Erase(); }
        }
        return new(ObjectId.Null, [], new
        {
            moved = moved.ToDictionary(p => names[p.Key], p => p.Value), purged,
            kept = map.Keys.Select(id => names[id]).Except(purged).ToArray(), include_blocks = includeBlocks
        });
    }

    // ---------------------------------------------------------------- data

    /// <summary>A top-level entity of model or paper space, open for write; XData and XRecords attach to any entity type.</summary>
    private static Entity Writable(Database db, Transaction tr, ObjectId id)
    {
        if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity) throw new CadFault("ENTITY_NOT_FOUND", H(id));
        if (tr.GetObject(entity.OwnerId, OpenMode.ForRead) is not BlockTableRecord { IsLayout: true }) throw new CadFault("UNSUPPORTED_SCOPE", "XData and XRecords attach to top-level entities in model or paper space");
        Edits.RequireUnlocked(tr, entity);
        entity.UpgradeOpen();
        return entity;
    }

    private static TypedValue[] TypedValues(JsonElement values, bool xdata) => values.EnumerateArray().Select(item =>
    {
        var value = item.GetProperty("value");
        return item.Text("type") switch
        {
            "string" => new TypedValue(xdata ? (int)DxfCode.ExtendedDataAsciiString : (int)DxfCode.Text, value.GetString()),
            "real" => new TypedValue(xdata ? (int)DxfCode.ExtendedDataReal : (int)DxfCode.Real, value.GetDouble()),
            "distance" => new TypedValue(xdata ? (int)DxfCode.ExtendedDataDist : 41, value.GetDouble()),
            "scale" => new TypedValue(xdata ? (int)DxfCode.ExtendedDataScale : 42, value.GetDouble()),
            "int16" => new TypedValue(xdata ? (int)DxfCode.ExtendedDataInteger16 : (int)DxfCode.Int16, (short)value.GetInt32()),
            "int32" => new TypedValue(xdata ? (int)DxfCode.ExtendedDataInteger32 : (int)DxfCode.Int32, value.GetInt32()),
            "point" => new TypedValue(xdata ? (int)DxfCode.ExtendedDataXCoordinate : (int)DxfCode.XCoordinate, P(value)),
            "handle" => new TypedValue(xdata ? (int)DxfCode.ExtendedDataHandle : (int)DxfCode.ArbitraryHandle, value.GetString()),
            var type => throw new CadFault("INVALID_VALUES", "Unknown value type " + type)
        };
    }).ToArray();

    private static Outcome SetXData(Database db, Transaction tr, JsonElement op, Dictionary<string, ObjectId> aliases)
    {
        var entity = Writable(db, tr, Edits.Resolve(db, tr, op, aliases));
        string app = op.Text("app")!;
        var apps = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (!apps.Has(app))
        {
            SymbolUtilityServices.ValidateSymbolName(app, false);
            apps.UpgradeOpen();
            var record = new RegAppTableRecord { Name = app };
            apps.Add(record); tr.AddNewlyCreatedDBObject(record, true);
        }
        var values = TypedValues(op.GetProperty("values"), xdata: true);
        // An application name alone removes that application's XData; other applications keep theirs.
        using (var buffer = new ResultBuffer([new TypedValue((int)DxfCode.ExtendedDataRegAppName, app), .. values]))
            try { entity.XData = buffer; }
            catch (Autodesk.AutoCAD.Runtime.Exception error) when (error.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.ExternalDataSizeExceeded)
            { throw new CadFault("XDATA_TOO_LARGE", "The entity's XData would exceed 16 KB"); }
        using var stored = entity.GetXDataForApplication(app);
        return new(ObjectId.Null, [entity.ObjectId], new { handle = H(entity.ObjectId), app, values = Math.Max(0, (stored?.AsArray().Length ?? 1) - 1), removed = values.Length == 0 });
    }

    private static Outcome SetXRecord(Database db, Transaction tr, JsonElement op, Dictionary<string, ObjectId> aliases)
    {
        string dictionaryName = op.Text("dictionary") ?? "CADMCP", key = op.Text("key")!;
        bool delete = Bool(op, "delete");
        Entity? owner = null;
        ObjectId rootId;
        if (op.Text("handle") is not null || op.Text("target") is not null)
        {
            owner = Writable(db, tr, Edits.Resolve(db, tr, op, aliases));
            if (owner.ExtensionDictionary.IsNull)
            {
                if (delete) return new(ObjectId.Null, [], new { owner_handle = H(owner.ObjectId), dictionary = dictionaryName, key, deleted = false });
                owner.CreateExtensionDictionary();
            }
            rootId = owner.ExtensionDictionary;
        }
        else rootId = db.NamedObjectsDictionaryId;
        var root = (DBDictionary)tr.GetObject(rootId, OpenMode.ForRead);
        DBDictionary dictionary;
        if (root.Contains(dictionaryName))
            dictionary = tr.GetObject(root.GetAt(dictionaryName), OpenMode.ForRead) as DBDictionary ?? throw new CadFault("NOT_A_DICTIONARY", dictionaryName);
        else
        {
            if (delete) return new(ObjectId.Null, [], new { owner_handle = owner is null ? null : H(owner.ObjectId), dictionary = dictionaryName, key, deleted = false });
            root.UpgradeOpen();
            dictionary = new DBDictionary();
            root.SetAt(dictionaryName, dictionary); tr.AddNewlyCreatedDBObject(dictionary, true);
        }
        var touched = owner is null ? Array.Empty<ObjectId>() : [owner.ObjectId];
        if (delete)
        {
            bool found = dictionary.Contains(key);
            if (found)
            {
                // Only data records are deleted; a dictionary, layout or other object at the key is left alone.
                if (tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead) is not Xrecord existing) throw new CadFault("NOT_AN_XRECORD", key);
                existing.UpgradeOpen(); existing.Erase();
            }
            return new(ObjectId.Null, touched, new { owner_handle = owner is null ? null : H(owner.ObjectId), dictionary = dictionaryName, key, deleted = found });
        }
        Xrecord xrecord;
        if (dictionary.Contains(key)) xrecord = tr.GetObject(dictionary.GetAt(key), OpenMode.ForWrite) as Xrecord ?? throw new CadFault("NOT_AN_XRECORD", key);
        else
        {
            if (!dictionary.IsWriteEnabled) dictionary.UpgradeOpen();
            xrecord = new Xrecord();
            dictionary.SetAt(key, xrecord); tr.AddNewlyCreatedDBObject(xrecord, true);
        }
        using (var data = new ResultBuffer(TypedValues(op.GetProperty("values"), xdata: false))) xrecord.Data = data;
        return new(ObjectId.Null, touched, new
        {
            owner_handle = owner is null ? null : H(owner.ObjectId), location = owner is null ? "named_objects_dictionary" : "extension_dictionary",
            dictionary = dictionaryName, key, handle = H(xrecord.ObjectId), values = op.GetProperty("values").GetArrayLength()
        });
    }
}
