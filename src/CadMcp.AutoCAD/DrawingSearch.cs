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
        if (scope is not ("current" or "model" or "layout" or "layouts" or "all" or "selection" or "view" or "viewport")) throw new CadFault("INVALID_SCOPE", "current/model/layout/layouts/all/selection/view/viewport");
        int offset = options.Number("offset", 0), limit = options.Number("limit", 100), depthLimit = options.Number("max_depth", 6);
        if (offset < 0 || limit is < 1 or > 500 || depthLimit is < 0 or > 12) throw new CadFault("INVALID_PAGE", "offset >= 0; limit 1..500; max_depth 0..12");
        bool expand = options.TryGetProperty("expand_blocks", out var expandValue) && expandValue.GetBoolean();
        bool details = options.TryGetProperty("details", out var detailValue) && detailValue.GetBoolean();
        var table = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
        string? layoutName=options.Text("layout_name"),handleFilter=options.Text("handle");
        if(scope=="layout"&&layoutName is null)throw new CadFault("LAYOUT_REQUIRED","Use layout_name from cad_catalog");
        var chosenLayout=layoutName is null?null:ReadingMetadata.LayoutByName(doc.Database,tr,layoutName);
        if(chosenLayout is not null && scope is not ("layout" or "current" or "all" or "layouts"))throw new CadFault("INVALID_SCOPE_OPTIONS","layout_name is supported with scope layout/current/all/layouts; viewport search selects model space itself");
        Viewport? selectedViewport=null;
        if(scope=="viewport")selectedViewport=tr.GetObject(NativeTables.Resolve(doc.Database,EditPlan.RequiredText(options,"viewport_handle")),OpenMode.ForRead) as Viewport??throw new CadFault("INVALID_VIEWPORT","Viewport handle required");
        if(scope=="selection"&&!ReferenceEquals(doc,Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument))throw new CadFault("EDITOR_SELECTION_UNAVAILABLE","Use database scopes for an inactive DWG");
        var selection = scope == "selection" ? doc.Editor.SelectImplied() : null;
        var roots = scope == "selection" ? selection?.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK ? selection.Value.GetObjectIds() : []
            : (chosenLayout is not null?new[]{chosenLayout.BlockTableRecordId}:scope is "current" or "view" ? new[] { doc.Database.CurrentSpaceId } : table.Cast<ObjectId>().Where(id =>
                ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).IsLayout && (scope is not ("model" or "viewport") || id == table[BlockTableRecord.ModelSpace]) &&
                (scope != "layouts" || id != table[BlockTableRecord.ModelSpace])).ToArray())
                .SelectMany(id => ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Cast<ObjectId>()).ToArray();
        Extents3d? area = null;
        var viewportFrozen=selectedViewport?.GetFrozenLayers().Cast<ObjectId>().ToHashSet();
        if(selectedViewport is not null)
        {
            var bounds=ReadingMetadata.SearchBounds(selectedViewport,tr)??throw new CadFault("VIEWPORT_PROJECTION_UNSUPPORTED","Viewport search needs a model viewport with an orthographic top view; read target_plane_corners_wcs for other views");
            var viewportBox=Wire.Element(bounds);area=new(new Point3d(EditPlan.Point(viewportBox.GetProperty("min"))),new Point3d(EditPlan.Point(viewportBox.GetProperty("max"))));
        }
        if (options.TryGetProperty("bounds", out var box))
        {
            var min = new Point3d(EditPlan.Point(box.GetProperty("min"))); var max = new Point3d(EditPlan.Point(box.GetProperty("max")));
            if (min.X > max.X || min.Y > max.Y || min.Z > max.Z) throw new CadFault("INVALID_BOUNDS", "Bounds min must not exceed max");
            if(area is {} existing)
            {
                min=new Point3d(Math.Max(min.X,existing.MinPoint.X),Math.Max(min.Y,existing.MinPoint.Y),Math.Max(min.Z,existing.MinPoint.Z));
                max=new Point3d(Math.Min(max.X,existing.MaxPoint.X),Math.Min(max.Y,existing.MaxPoint.Y),Math.Min(max.Z,existing.MaxPoint.Z));
                if(min.X>max.X||min.Y>max.Y||min.Z>max.Z)throw new CadFault("BOUNDS_OUTSIDE_VIEWPORT","Requested bounds do not intersect the selected viewport envelope");
            }
            area = new(min, max);
        }
        if (scope == "view")
        {
            if(!ReferenceEquals(doc,Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument))throw new CadFault("EDITOR_VIEW_UNAVAILABLE","Use named layout or viewport search for inactive drawings");
            using var view = doc.Editor.GetCurrentView();
            if (view.PerspectiveEnabled) throw new CadFault("PERSPECTIVE_VIEW", "Use selection or explicit WCS bounds for a perspective view");
            var matrix = Matrix3d.PlaneToWorld(view.ViewDirection);
            matrix = Matrix3d.Displacement(view.Target - Point3d.Origin) * matrix;
            matrix = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * matrix;
            var corners = new[] { new Point3d(view.CenterPoint.X - view.Width / 2, view.CenterPoint.Y - view.Height / 2, 0), new Point3d(view.CenterPoint.X + view.Width / 2, view.CenterPoint.Y - view.Height / 2, 0), new Point3d(view.CenterPoint.X + view.Width / 2, view.CenterPoint.Y + view.Height / 2, 0), new Point3d(view.CenterPoint.X - view.Width / 2, view.CenterPoint.Y + view.Height / 2, 0) }.Select(p => p.TransformBy(matrix)).ToArray();
            area = new(new Point3d(corners.Min(p => p.X), corners.Min(p => p.Y), -1e100), new Point3d(corners.Max(p => p.X), corners.Max(p => p.Y), 1e100));
        }
        string? layer = options.Text("layer"), type = options.Text("type"), text = options.Text("text");
        var page = new List<JsonElement>(); int matched = 0, visited = 0, chars = 0, unknownBounds = 0; bool more = false;
        var stack = new HashSet<ObjectId>();
        Layout? rootLayout=null;
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
            if ((scope is "view" or "viewport") && (!entity.Visible || layerRecord.IsOff || layerRecord.IsFrozen||viewportFrozen?.Contains(entity.LayerId)==true)) yield break;
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
                fields["layout_name"]=rootLayout?.LayoutName;fields["space"]=rootLayout?.ModelType==true?"model":"paper";
                fields["space_handle"]=rootLayout?.BlockTableRecordId.Handle.ToString();
                fields["type"] = entity.GetType().Name; fields["dxf_name"] = entity.GetRXClass().DxfName; fields["layer"] = entity.Layer;
                fields["visible"] = entity.Visible; fields["layer_off"] = layerRecord.IsOff; fields["layer_frozen"] = layerRecord.IsFrozen;
                fields["path"] = path; fields["root_handle"] = root; fields["effective_layer"] = effectiveLayer; fields["depth"] = depth; fields["from_xref"] = xref;
                fields["world_transform"] = transform.ToArray();
                if(entity is Dimension sourceDimension)
                {
                    var source=new Dictionary<string,object?>();ReadingMetadata.Dimension(sourceDimension,tr,source);
                    foreach(var pair in source)if(pair.Key!="geometric_measurement"||!fields.ContainsKey(pair.Key))fields[pair.Key]=pair.Value;
                    if(source.TryGetValue("geometric_measurement",out var sourceGeometry))fields["source_geometric_measurement"]=sourceGeometry;
                    fields["source_measurement"]=sourceDimension.Measurement;
                    fields["dimension_coordinates"]=transform.IsEqualTo(Matrix3d.Identity)?"WCS":"geometry transformed to WCS; displayed text retains source dimension settings";
                }
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
            if(handleFilter is not null&&!string.Equals(item.Text("handle"),handleFilter,StringComparison.OrdinalIgnoreCase))return false;
            if (layer is not null && !string.Equals(item.Text("effective_layer"), layer, StringComparison.OrdinalIgnoreCase)) return false;
            if (type is not null && !string.Equals(item.Text("type"), type, StringComparison.OrdinalIgnoreCase) && !string.Equals(item.Text("dxf_name"), type, StringComparison.OrdinalIgnoreCase)) return false;
            // Raw text, or the displayed text: "Ø108" finds "%%c108" and formatted MText.
            if (text is not null && item.Text("text") is var value && !(value?.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || CadText.Contains(value, text) || CadText.Contains(value, text, mtext: true))) return false;
            if ((scope is "view" or "viewport") && ((!item.GetProperty("visible").GetBoolean()) || item.GetProperty("layer_off").GetBoolean() || item.GetProperty("layer_frozen").GetBoolean())) return false;
            if(area.HasValue&&!item.TryGetProperty("bounds",out _)){unknownBounds++;return false;}
            if (area is { } bounds && item.TryGetProperty("bounds", out var b))
            {
                var min = b.GetProperty("min").EnumerateArray().Select(v => v.GetDouble()).ToArray(); var max = b.GetProperty("max").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                if (max[0] < bounds.MinPoint.X || min[0] > bounds.MaxPoint.X || max[1] < bounds.MinPoint.Y || min[1] > bounds.MaxPoint.Y || max[2] < bounds.MinPoint.Z || min[2] > bounds.MaxPoint.Z) return false;
            }
            return true;
        }
        foreach (var root in roots)
        {
            rootLayout=tr.GetObject(root,OpenMode.ForRead) is Entity rootEntity?ReadingMetadata.LayoutOf(rootEntity.OwnerId,tr):null;
            foreach (var item in Walk(root, Matrix3d.Identity, "", "", "", 0, false))
            {
                if (!Match(item)) continue;
                if (matched++ < offset) continue;
                var value = details ? item : Wire.Element(item.EnumerateObject().Where(p => new[] { "handle", "owner_handle","space_handle","space","layout_name","root_handle", "path", "type", "dxf_name", "name", "effective_name", "is_xref", "layer", "effective_layer", "text", "bounds", "access", "depth", "from_xref", "coordinate_system", "transform_limitation" }.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value));
                int size = value.GetRawText().Length;
                if (page.Count == limit || (chars + size > 512 * 1024 && page.Count != 0)) { more = true; break; }
                page.Add(value); chars += size;
            }
            if (more) break;
        }
        return new { entities = page, scope, visited, bounds_unavailable_excluded=unknownBounds,pagination = new { next_offset = more ? (int?)(offset + page.Count) : null }, expanded_blocks = expand,
            limitations = new[] { "depth_bounded", "view_filter_is_conservative_wcs_envelope", "bounded_search_excludes_entities_without_extents_use_unbounded_scope_for_those", "special_properties_require_vendor_adapter" } };
    }
}
