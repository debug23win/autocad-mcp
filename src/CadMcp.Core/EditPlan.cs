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
        ["rectangle"] = "first second layer color_index",
        ["text"] = "position text height rotation_deg style layer color_index",
        ["mtext"] = "position text height width rotation_deg style layer color_index",
        ["block"] = "name position scale rotation_deg attributes layer color_index",
        ["block_define"] = "name base_point handles",
        ["layout_create"] = "name",
        ["dimension_aligned"] = "first second position text style layer color_index",
        ["hatch"] = "boundaries pattern scale angle_deg layer color_index",
        ["box"] = "center length width height layer color_index",
        ["cylinder"] = "center radius height layer color_index",
        ["move"] = "handle target displacement",
        ["copy"] = "handle target displacement layer",
        ["rotate"] = "handle target center angle_deg",
        ["scale"] = "handle target center factor",
        ["mirror"] = "handle target first second",
        ["erase"] = "handle target",
        ["set"] = "handle target layer color_index linetype linetype_scale lineweight visible text height width rotation_deg position start end center radius closed attributes"
    };
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
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in op.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new CadFault("DUPLICATE_FIELD", p.Name);
                if (!allowed.Contains(p.Name)) throw new CadFault("UNKNOWN_FIELD", kind + ": " + p.Name);
                ValidateValue(p.Name, p.Value);
            }
            if (op.TryGetProperty("target", out var target) && !aliases.Contains(target.GetString() ?? ""))
                throw new CadFault("UNKNOWN_TARGET", "target must refer to an earlier operation id");
            if (op.TryGetProperty("handle", out _) && op.TryGetProperty("target", out _))
                throw new CadFault("INVALID_TARGET", "Use handle or target, not both");
            if (kind is "move" or "copy" or "rotate" or "scale" or "mirror" or "erase" or "set")
                if (!op.TryGetProperty("handle", out _) && !op.TryGetProperty("target", out _))
                    throw new CadFault("INVALID_TARGET", "Expected handle or target");
            var required = kind switch
            {
                "layer" or "layout_create" => "name", "line" => "start end", "circle" => "center radius", "point" => "position",
                "ellipse" => "center major_axis radius_ratio",
                "arc" => "center radius start_angle_deg end_angle_deg", "polyline" => "points",
                "rectangle" => "first second", "text" or "mtext" => "position text height",
                "block" => "name position", "block_define" => "name base_point handles", "dimension_aligned" => "first second position",
                "hatch" => "boundaries", "box" => "center length width height", "cylinder" => "center radius height",
                "move" or "copy" => "displacement", "rotate" => "center angle_deg", "scale" => "center factor",
                "mirror" => "first second", _ => ""
            };
            foreach (var field in required.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (!op.TryGetProperty(field, out _)) throw new CadFault("MISSING_FIELD", kind + ": " + field);
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
        if (!value.TryGetDouble(out double number) || !(!double.IsNaN(number) && !double.IsInfinity(number))) throw new CadFault("INVALID_PARAMETER", name + " must be finite");
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
    private static void ValidateValue(string key, JsonElement v)
    {
        if (key is "start" or "end" or "center" or "position" or "first" or "second" or "displacement" or "base_point" or "major_axis") { Point(v); return; }
        if (key == "handles")
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 1 or > 100 ||
                v.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || !long.TryParse(x.GetString(), System.Globalization.NumberStyles.HexNumber, null, out var h) || h <= 0))
                throw new CadFault("INVALID_HANDLES", "Expected 1..100 hexadecimal entity handles");
            return;
        }
        if (key == "points")
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 2 or > 2000) throw new CadFault("INVALID_POINTS", "Expected 2..2000 vertices");
            var points = v.EnumerateArray().Select(Point).ToArray();
            if (points.Any(p => Math.Abs(p[2] - points[0][2]) > 1e-8)) throw new CadFault("NONPLANAR_POLYLINE", "Polyline vertices must share WCS Z; use AutoLISP for 3D polylines");
            return;
        }
        if (key is "closed" or "locked" or "off" or "visible")
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
        if (key is "radius" or "radius_ratio" or "height" or "width" or "length" or "factor" or "scale" or "angle_deg" or "start_angle_deg" or "end_angle_deg" or "rotation_deg" or "linetype_scale" or "lineweight" or "color_index")
        {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var n) || !(!double.IsNaN(n) && !double.IsInfinity(n))) throw new CadFault("INVALID_PARAMETER", key + " must be finite");
            if (key is "radius" or "height" or "length" or "factor" or "scale" or "linetype_scale" && n <= 0) throw new CadFault("INVALID_PARAMETER", key + " must be positive");
            if (key == "width" && n < 0) throw new CadFault("INVALID_PARAMETER", "width must be nonnegative");
            if (key == "radius_ratio" && (n <= 0 || n > 1)) throw new CadFault("INVALID_PARAMETER", "radius_ratio must be >0 and <=1");
            if (key == "color_index" && (n != Math.Truncate(n) || n < 0 || n > 256)) throw new CadFault("INVALID_COLOR", "ACI color must be an integer 0..256");
            return;
        }
        if (v.ValueKind != JsonValueKind.String || (key != "text" && string.IsNullOrWhiteSpace(v.GetString())))
            throw new CadFault("INVALID_PARAMETER", key + " must be a string");
        if (key == "handle" && (!long.TryParse(v.GetString(), System.Globalization.NumberStyles.HexNumber, null, out var h) || h <= 0))
            throw new CadFault("INVALID_HANDLE", "Expected a positive hexadecimal handle");
    }
}
