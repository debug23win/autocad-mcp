using System.Text.Json;

namespace CadMcp.Core;

/// <summary>Shared, CAD-independent validation performed before opening a write transaction.</summary>
public static class EditPlan
{
    public static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>
    {
        ["layer"] = "name color_index locked off",
        ["line"] = "start end layer color_index",
        ["circle"] = "center radius layer color_index",
        ["point"] = "position layer color_index",
        ["ellipse"] = "center major_axis radius_ratio start_angle_deg end_angle_deg layer color_index",
        ["arc"] = "center radius start_angle_deg end_angle_deg layer color_index",
        ["polyline"] = "points closed bulges width layer color_index",
        ["polyline3d"] = "points closed layer color_index",
        ["spline"] = "fit_points degree closed layer color_index",
        ["rectangle"] = "first second layer color_index",
        ["text"] = "position text height rotation_deg style layer color_index",
        ["mtext"] = "position text height width rotation_deg style layer color_index",
        ["block"] = "name position scale rotation_deg attributes layer color_index",
        ["block_define"] = "name base_point handles",
        ["layout_create"] = "name",
        ["layout_copy"] = "source name",
        ["layout_configure"] = "name device media_name plot_style paper_units paper_rotation",
        ["viewport"] = "layout center width height model_center model_height twist_deg locked layer color_index",
        ["image_attach"] = "path name control_points layer color_index layout",
        ["civil_tin_create"] = "name vertices",
        ["civil_tin_add_points"] = "handle vertices",
        ["dimension_aligned"] = "first second position text style layer color_index",
        ["hatch"] = "boundaries pattern scale angle_deg layer color_index",
        ["box"] = "center length width height layer color_index",
        ["cylinder"] = "center radius height layer color_index",
        ["sphere"] = "center radius layer color_index",
        ["cone"] = "center radius height layer color_index",
        ["wedge"] = "center length width height layer color_index",
        ["torus"] = "center major_radius minor_radius layer color_index",
        ["extrude"] = "handle target direction layer color_index",
        ["sweep"] = "handle target path_handle path_target layer color_index",
        ["revolve"] = "handle target axis_start axis_end angle_deg layer color_index",
        ["solid_boolean"] = "handle target tool_handle tool_target operation keep_tool",
        ["mesh"] = "vertices faces layer color_index",
        ["move"] = "handle target displacement",
        ["copy"] = "handle target displacement layer",
        ["rotate"] = "handle target center angle_deg",
        ["rotate3d"] = "handle target axis_start axis_end angle_deg",
        ["scale"] = "handle target center factor",
        ["mirror"] = "handle target first second",
        ["erase"] = "handle target",
        ["set"] = "handle target layer color_index linetype linetype_scale lineweight visible text height width rotation_deg position start end center radius closed attributes layout"
    }.Concat(ExtendedPlan.Fields).Concat(DraftingPlan.Fields).ToDictionary(p => p.Key, p => p.Value);
    public static JsonElement[] Parse(string json)
    {
        if (json.Length > 65536) throw new CadFault("PLAN_TOO_LARGE", "Edit plan must be at most 65536 characters");
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() is < 1 or > 100)
            throw new CadFault("INVALID_PLAN", "Expected 1..100 operations");
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<JsonElement>();
        foreach (var op in doc.RootElement.EnumerateArray())
        {
            string kind = RequiredText(op, "op");
            if (!Fields.TryGetValue(kind, out var fields)) throw new CadFault("INVALID_OPERATION", kind);
            var allowed = fields.Split(' ').Concat(["op", "id"]).ToHashSet(StringComparer.Ordinal);
            if (kind is "line" or "circle" or "point" or "ellipse" or "arc" or "polyline" or "polyline3d" or "spline" or "rectangle" or
                "text" or "mtext" or "block" or "dimension_aligned" or "box" or "cylinder" or "sphere" or
                "cone" or "wedge" or "torus" or "extrude" or "sweep" or "revolve" or "mesh") allowed.Add("layout");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in op.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new CadFault("DUPLICATE_FIELD", p.Name);
                if (!allowed.Contains(p.Name)) throw new CadFault("UNKNOWN_FIELD", kind + ": " + p.Name);
                if (!ExtendedPlan.Fields.ContainsKey(kind) && (!DraftingPlan.Supports(kind) || p.Name is "op" or "id" or "layer" or "color_index" or "layout" or "target" or "style" or "text_style")) ValidateValue(kind, p.Name, p.Value);
            }
            if (ExtendedPlan.Fields.ContainsKey(kind)) ExtendedPlan.Validate(op);
            if (DraftingPlan.Supports(kind)) DraftingPlan.Validate(op, aliases);
            if (op.TryGetProperty("target", out var target) && !aliases.Contains(target.GetString() ?? ""))
                throw new CadFault("UNKNOWN_TARGET", "target must refer to an earlier operation id");
            if (op.TryGetProperty("tool_target", out var toolTarget) && !aliases.Contains(toolTarget.GetString() ?? ""))
                throw new CadFault("UNKNOWN_TARGET", "tool_target must refer to an earlier operation id");
            if (op.TryGetProperty("path_target", out var pathTarget) && !aliases.Contains(pathTarget.GetString() ?? ""))
                throw new CadFault("UNKNOWN_TARGET", "path_target must refer to an earlier operation id");
            if (op.TryGetProperty("handle", out _) && op.TryGetProperty("target", out _))
                throw new CadFault("INVALID_TARGET", "Use handle or target, not both");
            if (op.TryGetProperty("tool_handle", out _) && op.TryGetProperty("tool_target", out _))
                throw new CadFault("INVALID_TARGET", "Use tool_handle or tool_target, not both");
            if (op.TryGetProperty("path_handle", out _) && op.TryGetProperty("path_target", out _))
                throw new CadFault("INVALID_TARGET", "Use path_handle or path_target, not both");
            if (kind is "move" or "copy" or "rotate" or "rotate3d" or "scale" or "mirror" or "erase" or "set" or "extrude" or "sweep" or "revolve" or "solid_boolean")
                if (!op.TryGetProperty("handle", out _) && !op.TryGetProperty("target", out _))
                    throw new CadFault("INVALID_TARGET", "Expected handle or target");
            if (kind == "solid_boolean" && !op.TryGetProperty("tool_handle", out _) && !op.TryGetProperty("tool_target", out _))
                throw new CadFault("INVALID_TARGET", "solid_boolean needs tool_handle or tool_target");
            if (kind == "sweep" && !op.TryGetProperty("path_handle", out _) && !op.TryGetProperty("path_target", out _))
                throw new CadFault("INVALID_TARGET", "sweep needs path_handle or path_target");
            var required = kind switch
            {
                "layer" or "layout_create" or "layout_configure" => "name", "layout_copy" => "source name",
                "viewport" => "layout center width height model_center model_height",
                "image_attach" => "path control_points",
                "civil_tin_create" => "name vertices", "civil_tin_add_points" => "handle vertices",
                "line" => "start end", "circle" => "center radius", "point" => "position",
                "ellipse" => "center major_axis radius_ratio",
                "arc" => "center radius start_angle_deg end_angle_deg", "polyline" or "polyline3d" => "points",
                "spline" => "fit_points",
                "rectangle" => "first second", "text" or "mtext" => "position text height",
                "block" => "name position", "block_define" => "name base_point handles", "dimension_aligned" => "first second position",
                "hatch" => "boundaries", "box" or "wedge" => "center length width height", "cylinder" or "cone" => "center radius height",
                "sphere" => "center radius", "torus" => "center major_radius minor_radius",
                "extrude" => "direction", "sweep" => "", "revolve" => "axis_start axis_end angle_deg",
                "solid_boolean" => "operation",
                "mesh" => "vertices faces",
                "move" or "copy" => "displacement", "rotate" => "center angle_deg", "rotate3d" => "axis_start axis_end angle_deg", "scale" => "center factor",
                "mirror" => "first second", _ => ""
            };
            foreach (var field in required.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (!op.TryGetProperty(field, out _)) throw new CadFault("MISSING_FIELD", kind + ": " + field);
            if (kind == "mesh") ValidateMesh(op);
            if (kind == "spline" && op.TryGetProperty("degree", out var degree) && degree.GetInt32() >= op.GetProperty("fit_points").GetArrayLength())
                throw new CadFault("INVALID_SPLINE", "degree must be smaller than fit point count");
            if (op.TryGetProperty("id", out var id))
            {
                var value = id.GetString();
                if (string.IsNullOrWhiteSpace(value) || !aliases.Add(value!)) throw new CadFault("DUPLICATE_ID", "Operation ids must be nonempty and unique");
            }
            result.Add(op.Clone());
        }
        return result.ToArray();
    }
    public static string RequiredText(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new CadFault("INVALID_PARAMETER", name + " must be a nonempty string");
        return value.GetString()!;
    }
    public static double Numeric(JsonElement e, string name, double? fallback = null)
    {
        if (!e.TryGetProperty(name, out var value)) return fallback ?? throw new CadFault("MISSING_FIELD", name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !(!double.IsNaN(number) && !double.IsInfinity(number))) throw new CadFault("INVALID_PARAMETER", name + " must be finite");
        return number;
    }
    public static double[] Point(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 2 or > 3)
            throw new CadFault("INVALID_POINT", "Expected [x,y] or [x,y,z] in WCS drawing units");
        var coords = value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && (!double.IsNaN(n) && !double.IsInfinity(n))
            ? n : throw new CadFault("INVALID_POINT", "Coordinates must be finite numbers")).ToArray();
        return [coords[0], coords[1], coords.Length == 3 ? coords[2] : 0];
    }
    private static void ValidateValue(string kind, string key, JsonElement v)
    {
        if (key is "start" or "end" or "center" or "position" or "first" or "second" or "axis_start" or "axis_end" or "displacement" or "direction" or "base_point" or "major_axis" or "model_center") { Point(v); return; }
        if (key == "handles")
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 1 or > 100 ||
                v.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || !long.TryParse(x.GetString(), System.Globalization.NumberStyles.HexNumber, null, out var h) || h <= 0))
                throw new CadFault("INVALID_HANDLES", "Expected 1..100 hexadecimal entity handles");
            return;
        }
        if (key == "control_points")
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 3 or > 20)
                throw new CadFault("INVALID_CONTROL_POINTS", "Supply 3..20 pixel/WCS control point pairs");
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("pixel", out var pixel) ||
                    pixel.ValueKind != JsonValueKind.Array || pixel.GetArrayLength() != 2 ||
                    pixel.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number || !x.TryGetDouble(out var n) || !double.IsFinite(n)) ||
                    !item.TryGetProperty("world", out var world))
                    throw new CadFault("INVALID_CONTROL_POINTS", "Each control point needs pixel:[x,y] and world:[x,y,z]");
                Point(world);
            }
            return;
        }
        if (key == "vertices")
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 3 or > 5000)
                throw new CadFault("INVALID_TIN_POINTS", "Supply 3..5000 three-dimensional WCS points");
            foreach (var point in v.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 3) throw new CadFault("INVALID_TIN_POINTS", "TIN points need [x,y,z]");
                Point(point);
            }
            return;
        }
        if (key == "fit_points")
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 3 or > 2000)
                throw new CadFault("INVALID_SPLINE", "Supply 3..2000 WCS fit points");
            foreach (var point in v.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 3)
                    throw new CadFault("INVALID_SPLINE", "Fit points need [x,y,z]");
                Point(point);
            }
            return;
        }
        if (key == "degree")
        {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n is < 1 or > 11)
                throw new CadFault("INVALID_SPLINE", "degree must be an integer from 1 to 11");
            return;
        }
        if (key == "faces")
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 1 or > 2000)
                throw new CadFault("INVALID_MESH_FACES", "Supply 1..2000 triangle or quadrilateral faces");
            foreach (var face in v.EnumerateArray())
                if (face.ValueKind != JsonValueKind.Array || face.GetArrayLength() is < 3 or > 4 ||
                    face.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out var n) || n < 0))
                    throw new CadFault("INVALID_MESH_FACES", "Each face needs 3 or 4 zero-based vertex indices");
            return;
        }
        if (key == "points")
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 2 or > 2000) throw new CadFault("INVALID_POINTS", "Expected 2..2000 vertices");
            if (kind == "polyline3d")
            {
                if (v.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 3))
                    throw new CadFault("INVALID_POINTS", "3D polyline vertices need [x,y,z]");
                foreach (var point in v.EnumerateArray()) Point(point);
                return;
            }
            var points = v.EnumerateArray().Select(Point).ToArray();
            if (points.Any(p => Math.Abs(p[2] - points[0][2]) > 1e-8)) throw new CadFault("NONPLANAR_POLYLINE", "Polyline vertices must share WCS Z; use polyline3d for spatial paths");
            return;
        }
        if (key is "closed" or "locked" or "off" or "visible" or "keep_tool")
        { if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new CadFault("INVALID_PARAMETER", key + " must be boolean"); return; }
        if (key == "attributes")
        {
            if (v.ValueKind != JsonValueKind.Object || v.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String))
                throw new CadFault("INVALID_ATTRIBUTES", "Expected an object of tag:string pairs");
            return;
        }
        if (key == "boundaries")
        { if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 1 or > 32 || v.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) throw new CadFault("INVALID_BOUNDARIES", "Expected 1..32 handles or operation ids"); return; }
        if (key == "bulges")
        { if (v.ValueKind != JsonValueKind.Array || v.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number || double.IsNaN(x.GetDouble()) || double.IsInfinity(x.GetDouble()))) throw new CadFault("INVALID_BULGES", "Expected finite numbers"); return; }
        if (key == "scale" && v.ValueKind == JsonValueKind.Array)
        { if (v.GetArrayLength() != 3 || Point(v).Any(x => x <= 0)) throw new CadFault("INVALID_SCALE", "Scale must contain three positive factors"); return; }
        if (key is "radius" or "radius_ratio" or "major_radius" or "minor_radius" or "height" or "width" or "length" or "factor" or "scale" or "angle_deg" or "start_angle_deg" or "end_angle_deg" or "rotation_deg" or "twist_deg" or "model_height" or "paper_rotation" or "linetype_scale" or "lineweight" or "color_index")
        {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var n) || !(!double.IsNaN(n) && !double.IsInfinity(n))) throw new CadFault("INVALID_PARAMETER", key + " must be finite");
            if (key is "radius" or "major_radius" or "minor_radius" or "height" or "length" or "factor" or "scale" or "model_height" or "linetype_scale" && n <= 0) throw new CadFault("INVALID_PARAMETER", key + " must be positive");
            if (key == "width" && n < 0) throw new CadFault("INVALID_PARAMETER", "width must be nonnegative");
            if (key == "paper_rotation" && n is not (0 or 90 or 180 or 270)) throw new CadFault("INVALID_PARAMETER", "paper_rotation must be 0, 90, 180 or 270 degrees");
            if (key == "radius_ratio" && (n <= 0 || n > 1)) throw new CadFault("INVALID_PARAMETER", "radius_ratio must be >0 and <=1");
            if (key == "color_index" && (n != Math.Truncate(n) || n < 0 || n > 256)) throw new CadFault("INVALID_COLOR", "ACI color must be an integer 0..256");
            return;
        }
        if (v.ValueKind != JsonValueKind.String || (key != "text" && string.IsNullOrWhiteSpace(v.GetString())))
            throw new CadFault("INVALID_PARAMETER", key + " must be a string");
        if ((key is "handle" or "tool_handle" or "path_handle") &&
            (!long.TryParse(v.GetString(), System.Globalization.NumberStyles.HexNumber, null, out var h) || h <= 0))
            throw new CadFault("INVALID_HANDLE", "Expected a positive hexadecimal handle");
        if (key == "operation" && v.GetString() is not ("union" or "subtract" or "intersect"))
            throw new CadFault("INVALID_PARAMETER", "operation must be union, subtract or intersect");
    }
    private static void ValidateMesh(JsonElement op)
    {
        var vertices = op.GetProperty("vertices").EnumerateArray().Select(Point).ToArray();
        foreach (var face in op.GetProperty("faces").EnumerateArray())
        {
            var indices = face.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (indices.Any(i => i >= vertices.Length) || indices.Distinct().Count() != indices.Length)
                throw new CadFault("INVALID_MESH_FACES", "Face indices must be distinct and refer to supplied vertices");
            var a = vertices[indices[0]]; var b = vertices[indices[1]]; var c = vertices[indices[2]];
            var ab = new[] { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
            var ac = new[] { c[0] - a[0], c[1] - a[1], c[2] - a[2] };
            var cross = new[] { ab[1] * ac[2] - ab[2] * ac[1], ab[2] * ac[0] - ab[0] * ac[2], ab[0] * ac[1] - ab[1] * ac[0] };
            var areaSquared = cross.Sum(x => x * x);
            var scale = Math.Max(ab.Sum(x => x * x), ac.Sum(x => x * x));
            if (areaSquared <= scale * scale * 1e-24)
                throw new CadFault("DEGENERATE_MESH_FACE", "The first three face vertices must form a nonzero area");
        }
    }
}
