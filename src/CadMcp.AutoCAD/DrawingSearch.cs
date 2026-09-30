using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

internal static class DrawingSearch
{
    public static object Read(Document doc, Transaction tr, JsonElement options, CancellationToken ct)
    {
        string scope = options.Text("scope") ?? "current";
        if (scope is not ("current" or "model" or "layouts" or "all" or "selection" or "view")) throw new CadFault("INVALID_SCOPE", "current/model/layouts/all/selection/view");
        int offset = options.Number("offset", 0), limit = options.Number("limit", 100), depthLimit = options.Number("max_depth", 6);
        if (offset < 0 || limit is < 1 or > 500 || depthLimit is < 0 or > 12) throw new CadFault("INVALID_PAGE", "offset >= 0; limit 1..500; max_depth 0..12");
        bool expand = options.TryGetProperty("expand_blocks", out var expandValue) && expandValue.GetBoolean();
        bool details = options.TryGetProperty("details", out var detailValue) && detailValue.GetBoolean();
        var table = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
        var selection = scope == "selection" ? doc.Editor.SelectImplied() : null;
        var roots = scope == "selection" ? selection?.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK ? selection.Value.GetObjectIds() : []
            : (scope is "current" or "view" ? new[] { doc.Database.CurrentSpaceId } : table.Cast<ObjectId>().Where(id =>
                ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).IsLayout && (scope != "model" || id == table[BlockTableRecord.ModelSpace]) &&
                (scope != "layouts" || id != table[BlockTableRecord.ModelSpace])).ToArray())
                .SelectMany(id => ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Cast<ObjectId>()).ToArray();
        Extents3d? area = null;
        if (options.TryGetProperty("bounds", out var box))
        {
            var min = new Point3d(EditPlan.Point(box.GetProperty("min"))); var max = new Point3d(EditPlan.Point(box.GetProperty("max")));
            if (min.X > max.X || min.Y > max.Y || min.Z > max.Z) throw new CadFault("INVALID_BOUNDS", "Bounds min must not exceed max");
            area = new(min, max);
        }
        if (scope == "view")
        {
            using var view = doc.Editor.GetCurrentView();
            if (view.PerspectiveEnabled) throw new CadFault("PERSPECTIVE_VIEW", "Use selection or explicit WCS bounds for a perspective view");
            var matrix = Matrix3d.PlaneToWorld(view.ViewDirection);
            matrix = Matrix3d.Displacement(view.Target - Point3d.Origin) * matrix;
            matrix = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * matrix;
            var corners = new[] { new Point3d(view.CenterPoint.X - view.Width / 2, view.CenterPoint.Y - view.Height / 2, 0), new Point3d(view.CenterPoint.X + view.Width / 2, view.CenterPoint.Y - view.Height / 2, 0), new Point3d(view.CenterPoint.X + view.Width / 2, view.CenterPoint.Y + view.Height / 2, 0), new Point3d(view.CenterPoint.X - view.Width / 2, view.CenterPoint.Y + view.Height / 2, 0) }.Select(p => p.TransformBy(matrix)).ToArray();
            area = new(new Point3d(corners.Min(p => p.X), corners.Min(p => p.Y), -1e100), new Point3d(corners.Max(p => p.X), corners.Max(p => p.Y), 1e100));
        }
        string? layer = options.Text("layer"), type = options.Text("type"), text = options.Text("text");
        var page = new List<JsonElement>(); int matched = 0, visited = 0, chars = 0; bool more = false;
        var stack = new HashSet<ObjectId>();
        IEnumerable<JsonElement> Walk(ObjectId id, Matrix3d transform, string parent, string top, string inheritedLayer, int depth, bool xref)
        {
            ct.ThrowIfCancellationRequested(); visited++;
            Entity? entity;
            try { entity = tr.GetObject(id, OpenMode.ForRead) as Entity; }
            catch (Autodesk.AutoCAD.Runtime.Exception) { yield break; }
            if (entity is null || entity.IsErased) yield break;
            string path = parent.Length == 0 ? entity.Handle.ToString() : parent + "/" + entity.Handle;
            string root = top.Length == 0 ? entity.Handle.ToString() : top;
            string effectiveLayer = entity.Layer == "0" && inheritedLayer.Length != 0 ? inheritedLayer : entity.Layer;
            var layerRecord = (LayerTableRecord)tr.GetObject(entity.LayerId, OpenMode.ForRead);
            if (scope == "view" && (!entity.Visible || layerRecord.IsOff || layerRecord.IsFrozen)) yield break;
            bool candidate = (layer is null || string.Equals(effectiveLayer, layer, StringComparison.OrdinalIgnoreCase)) &&
                (type is null || string.Equals(entity.GetType().Name, type, StringComparison.OrdinalIgnoreCase) || string.Equals(entity.GetRXClass().DxfName, type, StringComparison.OrdinalIgnoreCase));
            var fields = new Dictionary<string, object?>();
            if (candidate)
            {
                if (details)
                {
                    bool transformed = true;
                    try
                    {
                        if (entity is BlockReference || transform.IsEqualTo(Matrix3d.Identity))
                            foreach (var property in Reader.Read(entity, tr).EnumerateObject()) fields[property.Name] = property.Value.Clone();
                        else
                        {
                            using var copy = (Entity)entity.Clone();
                            try { copy.TransformBy(transform); } catch (Autodesk.AutoCAD.Runtime.Exception) { transformed = false; }
                            foreach (var property in Reader.Read(transformed ? copy : entity, tr).EnumerateObject()) fields[property.Name] = property.Value.Clone();
                        }
                        fields["coordinate_system"] = transformed ? "WCS" : "local";
                        if (!transformed) { fields["access"] = "partial"; fields["transform_limitation"] = "entity_cannot_be_transformed_without_changing_type"; }
                    }
                    catch (System.Exception error) when (error is Autodesk.AutoCAD.Runtime.Exception or System.Reflection.TargetInvocationException)
                    { fields["access"] = "partial"; fields["read_error"] = error.Message; }
                }
                else
                {
                    fields["access"] = "summary"; fields["coordinate_system"] = "WCS";
                    if (entity is DBText dbText) fields["text"] = dbText.TextString;
                    if (entity is MText mText) fields["text"] = mText.Text;
                    if (entity is Dimension dimension) fields["text"] = dimension.DimensionText;
                    if (entity is BlockReference reference)
                    {
                        var definition = (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
                        fields["name"] = definition.Name; fields["is_xref"] = definition.IsFromExternalReference;
                        fields["effective_name"] = reference.IsDynamicBlock ? ((BlockTableRecord)tr.GetObject(reference.DynamicBlockTableRecord, OpenMode.ForRead)).Name : definition.Name;
                        fields["text"] = string.Join(" ", reference.AttributeCollection.Cast<ObjectId>().Select(a => (tr.GetObject(a, OpenMode.ForRead) as AttributeReference)?.TextString));
                    }
                }
                // A clone has no database owner/handle; preserve identity from the resident entity.
                fields["handle"] = entity.Handle.ToString(); fields["owner_handle"] = entity.OwnerId.Handle.ToString();
                fields["type"] = entity.GetType().Name; fields["dxf_name"] = entity.GetRXClass().DxfName; fields["layer"] = entity.Layer;
                fields["visible"] = entity.Visible; fields["layer_off"] = layerRecord.IsOff; fields["layer_frozen"] = layerRecord.IsFrozen;
                fields["path"] = path; fields["root_handle"] = root; fields["effective_layer"] = effectiveLayer; fields["depth"] = depth; fields["from_xref"] = xref;
                fields["world_transform"] = transform.ToArray();
                if (entity is BlockReference blockReference)
                {
                    var point = blockReference.Position.TransformBy(transform);
                    fields["position"] = new[] { point.X, point.Y, point.Z };
                    fields["definition_to_wcs"] = (transform * blockReference.BlockTransform).ToArray();
                    fields["block_properties_coordinate_system"] = "rotation_scale_and_transform_relative_to_parent";
                    if (fields.TryGetValue("attribute_details", out var attributeValue) && attributeValue is JsonElement attributes)
                        fields["attribute_details"] = attributes.EnumerateArray().Select(attribute =>
                        {
                            var properties = attribute.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                            var position = new Point3d(attribute.GetProperty("position").EnumerateArray().Select(p => p.GetDouble()).ToArray()).TransformBy(transform);
                            properties["position"] = new[] { position.X, position.Y, position.Z }; return properties;
                        }).ToArray();
                }
                try { var extent = entity.GeometricExtents; extent.TransformBy(transform); fields["bounds"] = new { min = new[] { extent.MinPoint.X, extent.MinPoint.Y, extent.MinPoint.Z }, max = new[] { extent.MaxPoint.X, extent.MaxPoint.Y, extent.MaxPoint.Z } }; }
                catch (Autodesk.AutoCAD.Runtime.Exception) { }
                if (!fields.ContainsKey("access")) fields["access"] = "partial";
                yield return Wire.Element(fields);
            }
            if (!expand || entity is not BlockReference block || depth >= depthLimit || !stack.Add(block.BlockTableRecord)) yield break;
            try
            {
                var definition = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead);
                foreach (ObjectId child in definition)
                    foreach (var item in Walk(child, transform * block.BlockTransform, path, root, effectiveLayer, depth + 1, xref || definition.IsFromExternalReference)) yield return item;
            }
            finally { stack.Remove(block.BlockTableRecord); }
        }
        bool Match(JsonElement item)
        {
            if (layer is not null && !string.Equals(item.Text("effective_layer"), layer, StringComparison.OrdinalIgnoreCase)) return false;
            if (type is not null && !string.Equals(item.Text("type"), type, StringComparison.OrdinalIgnoreCase) && !string.Equals(item.Text("dxf_name"), type, StringComparison.OrdinalIgnoreCase)) return false;
            if (text is not null && !(item.Text("text")?.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            if (scope == "view" && ((!item.GetProperty("visible").GetBoolean()) || item.GetProperty("layer_off").GetBoolean() || item.GetProperty("layer_frozen").GetBoolean())) return false;
            if (area is { } bounds && item.TryGetProperty("bounds", out var b))
            {
                var min = b.GetProperty("min").EnumerateArray().Select(v => v.GetDouble()).ToArray(); var max = b.GetProperty("max").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                if (max[0] < bounds.MinPoint.X || min[0] > bounds.MaxPoint.X || max[1] < bounds.MinPoint.Y || min[1] > bounds.MaxPoint.Y || max[2] < bounds.MinPoint.Z || min[2] > bounds.MaxPoint.Z) return false;
            }
            return true;
        }
        foreach (var root in roots)
        {
            foreach (var item in Walk(root, Matrix3d.Identity, "", "", "", 0, false))
            {
                if (!Match(item)) continue;
                if (matched++ < offset) continue;
                var value = details ? item : Wire.Element(item.EnumerateObject().Where(p => new[] { "handle", "root_handle", "path", "type", "dxf_name", "name", "effective_name", "is_xref", "layer", "effective_layer", "text", "bounds", "access", "depth", "from_xref", "coordinate_system", "transform_limitation" }.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value));
                int size = value.GetRawText().Length;
                if (page.Count == limit || (chars + size > 512 * 1024 && page.Count != 0)) { more = true; break; }
                page.Add(value); chars += size;
            }
            if (more) break;
        }
        return new { entities = page, scope, visited, pagination = new { next_offset = more ? (int?)(offset + page.Count) : null }, expanded_blocks = expand,
            limitations = new[] { "depth_bounded", "view_filter_is_conservative_wcs_envelope", "special_properties_require_vendor_adapter" } };
    }
}
