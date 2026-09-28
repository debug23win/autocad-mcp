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
            ["access"] = "partial", ["coordinate_system"] = "WCS", ["owner_handle"] = e.OwnerId.Handle.ToString()
        };
        try { var b = e.GeometricExtents; item["bounds"] = new { min = P(b.MinPoint), max = P(b.MaxPoint) }; }
        catch (Autodesk.AutoCAD.Runtime.Exception) { item["bounds_unavailable"] = true; }
        switch (e)
        {
            case Line line:
                item["start"] = P(line.StartPoint); item["end"] = P(line.EndPoint); item["length"] = line.Length; item["access"] = "structured"; break;
            case Circle circle:
                item["center"] = P(circle.Center); item["radius"] = circle.Radius; item["normal"] = new[] { circle.Normal.X, circle.Normal.Y, circle.Normal.Z }; item["access"] = "structured"; break;
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
            case DBText text:
                item["text"] = text.TextString; item["position"] = P(text.Position); item["rotation"] = text.Rotation; item["access"] = "structured"; break;
            case MText text:
                item["text"] = text.Text; item["raw_text"] = text.Contents; item["position"] = P(text.Location); item["access"] = "structured"; break;
            case Dimension dim:
                item["measurement"] = dim.Measurement; item["text_override"] = dim.DimensionText; item["text"] = dim.DimensionText;
                item["limitations"] = new[] { "dimension_geometry_not_expanded" }; break;
            case BlockReference block:
                var definition = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead);
                item["name"] = definition.Name; item["is_xref"] = definition.IsFromExternalReference;
                item["transform"] = block.BlockTransform.ToArray(); item["position"] = P(block.Position);
                item["dynamic"] = block.IsDynamicBlock;
                var attributes = new Dictionary<string, string>();
                foreach (ObjectId id in block.AttributeCollection)
                    if (tr.GetObject(id, OpenMode.ForRead) is AttributeReference a) attributes[a.Tag + ":" + a.Handle] = a.TextString;
                item["attributes"] = attributes; item["text"] = string.Join(" ", attributes.Values);
                item["limitations"] = new[] { "nested_geometry_not_expanded", "dynamic_properties_not_read" }; break;
            default:
                item["limitations"] = new[] { "specialized_properties_unavailable", "requires_visual_or_vendor_adapter" }; break;
        }
        var layer = (LayerTableRecord)tr.GetObject(e.LayerId, OpenMode.ForRead);
        item["layer_off"] = layer.IsOff; item["layer_frozen"] = layer.IsFrozen;
        return Wire.Element(item);
    }
}
