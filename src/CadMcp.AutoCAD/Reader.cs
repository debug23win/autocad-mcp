using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

internal static class Reader
{
    private static double[] P(Point3d p) => [p.X, p.Y, p.Z];
    public static JsonElement Read(Entity e, Transaction tr)
    {
        var item = new Dictionary<string, object?>
        {
            ["handle"] = e.Handle.ToString(), ["type"] = e.GetType().Name,
            ["class_name"] = e.GetRXClass().Name, ["dxf_name"] = e.GetRXClass().DxfName,
            ["layer"] = e.Layer, ["visible"] = e.Visible,
            ["color_index"] = e.ColorIndex, ["linetype"] = e.Linetype, ["linetype_scale"] = e.LinetypeScale, ["lineweight"] = (int)e.LineWeight,
            ["access"] = "partial", ["coordinate_system"] = "WCS", ["angle_units"] = "radians", ["owner_handle"] = e.OwnerId.IsNull ? null : e.OwnerId.Handle.ToString()
        };
        try { var b = e.GeometricExtents; item["bounds"] = new { min = P(b.MinPoint), max = P(b.MaxPoint) }; }
        catch (Autodesk.AutoCAD.Runtime.Exception) { item["bounds_unavailable"] = true; }
        switch (e)
        {
            case Line line:
                item["start"] = P(line.StartPoint); item["end"] = P(line.EndPoint); item["length"] = line.Length; item["access"] = "structured"; break;
            case Circle circle:
                item["center"] = P(circle.Center); item["radius"] = circle.Radius; item["normal"] = new[] { circle.Normal.X, circle.Normal.Y, circle.Normal.Z }; item["access"] = "structured"; break;
            case DBPoint point:
                item["position"] = P(point.Position); item["access"] = "structured"; break;
            case Ellipse ellipse:
                item["center"] = P(ellipse.Center); item["major_axis"] = new[] { ellipse.MajorAxis.X, ellipse.MajorAxis.Y, ellipse.MajorAxis.Z };
                item["radius_ratio"] = ellipse.RadiusRatio; item["start_angle"] = ellipse.StartAngle; item["end_angle"] = ellipse.EndAngle;
                item["access"] = "structured"; break;
            case Arc arc:
                item["center"] = P(arc.Center); item["radius"] = arc.Radius; item["start"] = P(arc.StartPoint); item["end"] = P(arc.EndPoint);
                item["length"] = arc.Length; item["start_angle"] = arc.StartAngle; item["end_angle"] = arc.EndAngle;
                item["normal"] = new[] { arc.Normal.X, arc.Normal.Y, arc.Normal.Z }; item["access"] = "structured"; break;
            case Polyline poly:
                var vertices = new List<object>();
                for (int i = 0; i < Math.Min(poly.NumberOfVertices, 2000); i++)
                    vertices.Add(new { point = P(poly.GetPoint3dAt(i)), bulge = poly.GetBulgeAt(i), start_width = poly.GetStartWidthAt(i), end_width = poly.GetEndWidthAt(i) });
                item["vertices"] = vertices; item["vertex_count"] = poly.NumberOfVertices; item["closed"] = poly.Closed; item["length"] = poly.Length;
                item["normal"] = new[] { poly.Normal.X, poly.Normal.Y, poly.Normal.Z }; item["vertices_truncated"] = poly.NumberOfVertices > 2000;
                item["access"] = poly.NumberOfVertices <= 2000 ? "structured" : "partial"; break;
            case Polyline3d spatial:
                var spatialVertices = spatial.Cast<ObjectId>().Take(2001)
                    .Select(id => tr.GetObject(id, OpenMode.ForRead))
                    .OfType<PolylineVertex3d>().Select(v => P(v.Position)).ToArray();
                item["vertices"] = spatialVertices.Take(2000).ToArray();
                item["vertex_count"] = spatialVertices.Length;
                item["vertices_truncated"] = spatialVertices.Length > 2000;
                item["closed"] = spatial.Closed;
                item["access"] = spatialVertices.Length <= 2000 ? "structured" : "partial"; break;
            case Spline spline:
                item["degree"] = spline.Degree;
                item["closed"] = spline.Closed;
                item["fit_point_count"] = spline.NumFitPoints;
                item["fit_points"] = Enumerable.Range(0, Math.Min(spline.NumFitPoints, 2000))
                    .Select(i => P(spline.GetFitPointAt(i))).ToArray();
                item["access"] = spline.NumFitPoints <= 2000 ? "structured" : "partial"; break;
            case SubDMesh mesh:
                var meshVertices = mesh.Vertices.Cast<Point3d>().Select(P).ToArray();
                var meshFaceArray = mesh.FaceArray.Cast<int>().ToArray();
                item["vertices"] = meshVertices;
                item["face_array"] = meshFaceArray;
                item["vertex_count"] = mesh.NumberOfVertices;
                item["face_count"] = mesh.NumberOfFaces;
                item["smooth_level"] = mesh.SmoothLevel;
                item["access"] = "structured"; break;
            case DBText text:
                item["text"] = text.TextString; item["position"] = P(text.Position); item["rotation"] = text.Rotation; item["height"] = text.Height; item["access"] = "structured"; break;
            case MText text:
                item["text"] = text.Text; item["raw_text"] = text.Contents; item["position"] = P(text.Location); item["height"] = text.TextHeight; item["width"] = text.Width; item["rotation"] = text.Rotation; item["access"] = "structured"; break;
            case Dimension dim:
                item["measurement"] = dim.Measurement; item["text_override"] = dim.DimensionText; item["text"] = dim.DimensionText;
                item["dimension_style"] = ((DimStyleTableRecord)tr.GetObject(dim.DimensionStyle, OpenMode.ForRead)).Name;
                item["text_position"] = P(dim.TextPosition);
                var points = new Dictionary<string, object>();
                foreach (var name in new[] { "XLine1Point", "XLine2Point", "DimLinePoint", "Center", "CenterPoint", "ChordPoint", "FarChordPoint", "ArcPoint", "XLine1Start", "XLine1End", "XLine2Start", "XLine2End" })
                    if (dim.GetType().GetProperty(name)?.GetValue(dim) is Point3d point) points[name] = P(point);
                item["geometry"] = points; item["limitations"] = new[] { "formatted_dimension_text_requires_style_evaluation" }; break;
            case BlockReference block:
                var definition = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead);
                item["name"] = definition.Name; item["is_xref"] = definition.IsFromExternalReference;
                item["transform"] = block.BlockTransform.ToArray(); item["position"] = P(block.Position);
                item["rotation"] = block.Rotation; item["scale"] = new[] { block.ScaleFactors.X, block.ScaleFactors.Y, block.ScaleFactors.Z };
                item["dynamic"] = block.IsDynamicBlock;
                if (block.IsDynamicBlock)
                {
                    item["effective_name"] = ((BlockTableRecord)tr.GetObject(block.DynamicBlockTableRecord, OpenMode.ForRead)).Name;
                    item["dynamic_properties"] = block.DynamicBlockReferencePropertyCollection.Cast<DynamicBlockReferenceProperty>().Take(100)
                        .Select(p => new { name = p.PropertyName, value = p.Value is double or int or short or string ? p.Value : p.Value?.ToString(), read_only = p.ReadOnly,
                            units = p.UnitsType.ToString(), allowed_values = p.GetAllowedValues().Take(100).Select(v => v is double or int or short or string ? v : v?.ToString()).ToArray() }).ToArray();
                }
                var attributes = new Dictionary<string, string>();
                var attributeDetails = new List<object>();
                foreach (ObjectId id in block.AttributeCollection)
                    if (tr.GetObject(id, OpenMode.ForRead) is AttributeReference a)
                    { attributes[a.Tag + ":" + a.Handle] = a.TextString; if (attributeDetails.Count < 100) attributeDetails.Add(new { tag = a.Tag, handle = a.Handle.ToString(), text = a.TextString, position = P(a.Position) }); }
                item["attributes"] = attributes; item["text"] = string.Join(" ", attributes.Values);
                item["attribute_details"] = attributeDetails; item["attribute_details_truncated"] = attributes.Count > 100;
                item["limitations"] = new[] { "use_cad_search_expand_blocks_for_nested_geometry" }; break;
            case Solid3d solid:
                item["volume"] = solid.MassProperties.Volume; item["access"] = "partial"; item["limitations"] = new[] { "solid_topology_not_expanded" }; break;
            case Hatch hatch:
                item["pattern"] = hatch.PatternName; item["pattern_scale"] = hatch.PatternScale; item["pattern_angle"] = hatch.PatternAngle;
                item["loops"] = hatch.NumberOfLoops; item["access"] = "partial"; item["limitations"] = new[] { "hatch_boundary_geometry_not_expanded" }; break;
            case Viewport viewport:
                item["paper_center"] = P(viewport.CenterPoint); item["paper_width"] = viewport.Width; item["paper_height"] = viewport.Height;
                item["model_target"] = P(viewport.ViewTarget); item["model_view_height"] = viewport.ViewHeight;
                item["custom_scale"] = viewport.CustomScale; item["twist"] = viewport.TwistAngle;
                item["locked"] = viewport.Locked; item["on"] = viewport.On; item["access"] = "structured"; break;
            case RasterImage raster:
                var imageDefinition = (RasterImageDef)tr.GetObject(raster.ImageDefId, OpenMode.ForRead);
                var orientation = raster.Orientation;
                item["source_path"] = imageDefinition.SourceFileName;
                item["image_name"] = raster.Name;
                item["image_width"] = raster.ImageWidth; item["image_height"] = raster.ImageHeight;
                item["orientation"] = new { origin = P(orientation.Origin),
                    x_axis = new[] { orientation.Xaxis.X, orientation.Xaxis.Y, orientation.Xaxis.Z },
                    y_axis = new[] { orientation.Yaxis.X, orientation.Yaxis.Y, orientation.Yaxis.Z } };
                item["pixel_to_model_transform"] = raster.PixelToModelTransform.ToArray();
                item["access"] = "structured"; break;
            default:
                item["vendor_assembly"] = e.GetType().Assembly.GetName().Name;
                var specialized = ReadSpecializedMetadata(e);
                if (specialized.Count > 0) item["specialized_properties"] = specialized;
                item["limitations"] = new[] { "specialized_geometry_unavailable", "requires_visual_or_vendor_adapter" }; break;
        }
        var layer = (LayerTableRecord)tr.GetObject(e.LayerId, OpenMode.ForRead);
        item["layer_off"] = layer.IsOff; item["layer_frozen"] = layer.IsFrozen;
        return Wire.Element(item);
    }

    private static Dictionary<string, object> ReadSpecializedMetadata(Entity entity)
    {
        // Civil 3D, Map 3D and SPDS entities stay read-only here. Access only bounded, scalar
        // public properties; never invoke methods or enumerate vendor-owned collections.
        string[] names = ["Name", "Description", "StyleName", "SurfaceName", "AlignmentName", "ProfileName",
            "StartingStation", "EndingStation", "StartStation", "EndStation", "Length", "Area", "Elevation",
            "MinimumElevation", "MaximumElevation", "NumberOfPoints", "NumberOfTriangles", "IsReferenceObject"];
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        var type = entity.GetType();
        foreach (var name in names)
        {
            var property = type.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (property?.GetMethod is null || property.GetIndexParameters().Length != 0) continue;
            try
            {
                object? value = property.GetValue(entity);
                if (value is string text && text.Length <= 500) result[name] = text;
                else if (value is bool or int or long or double or float or decimal or short &&
                    (value is not double d || double.IsFinite(d)) && (value is not float f || float.IsFinite(f))) result[name] = value;
                else if (value is Enum) result[name] = value.ToString()!;
            }
            catch (System.Exception) { /* Vendor property may require a separate context; report only reliable values. */ }
        }
        return result;
    }
}
