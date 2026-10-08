using System.Text.Json;

namespace CadMcp.Core;

/// <summary>
/// Validation of everyday drafting edits: text replacement, offset, explode, join, arrays, fillet and
/// chamfer, trim and extend, angular dimensions, multileaders, external references, block import,
/// layer merging, XData/XRecord and object-property fields. Runs before any transaction is opened.
/// </summary>
public static class ModifyPlan
{
    public static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>
    {
        ["text_replace"] = "find replace handles layers scope layout match_case whole_word include max_changes",
        ["offset"] = "handle target distance side_point layer color_index",
        ["explode"] = "handle target keep_original attributes_as_text layer",
        ["join"] = "handle target others",
        ["array_rect"] = "items rows columns row_spacing column_spacing angle_deg",
        ["array_polar"] = "items center count angle_deg rotate_items",
        ["fillet"] = "handle target other_handle other_target radius layer color_index",
        ["chamfer"] = "handle target other_handle other_target distance other_distance layer color_index",
        ["polyline_fillet"] = "handle target radius vertices",
        ["trim"] = "handle target boundaries pick_point",
        ["extend"] = "handle target boundaries end",
        ["dimension_angular"] = "center first second position text style layer color_index layout",
        ["mleader"] = "points text height style text_style layer color_index layout",
        ["xref_attach"] = "path name position scale rotation_deg overlay layer color_index",
        ["block_import"] = "path names overwrite",
        ["layer_merge"] = "mapping create_missing include_blocks purge",
        ["xdata_set"] = "handle target app values",
        ["xrecord_set"] = "handle target dictionary key values delete",
        ["field_text"] = "position object_handle object_target property precision factor prefix suffix height style layer color_index layout"
    };

    /// <summary>Operations whose result can be named with id and used as a later target.</summary>
    public static readonly IReadOnlySet<string> Aliasable = new HashSet<string>(StringComparer.Ordinal)
        { "offset", "join", "fillet", "chamfer", "extend", "dimension_angular", "mleader", "xref_attach", "field_text" };

    /// <summary>Operations whose effects reach outside the drawing transaction (files, loaded references); cad_edit_preview rejects them.</summary>
    /// <summary>
    /// Application and dictionary name prefixes owned by AutoCAD, its verticals and Autodesk add-ins (ACAD, ACAD_LAYOUT,
    /// AcDbAttr, AcadAnnotative, AEC_..., AeccDb..., ACMAP...). Names that merely begin alike, such as AECOM or ACADEMY,
    /// stay usable.
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedPrefixes = ["ACAD_", "ACDB", "ACADANNO", "ACAEC", "AEC_", "AECC", "ADSK_", "AUTODESK", "ACMAP"];
    public static bool Reserved(string name) => name.Equals("ACAD", StringComparison.OrdinalIgnoreCase) ||
        ReservedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    public static readonly IReadOnlySet<string> OutsideTransaction = new HashSet<string>(StringComparer.Ordinal) { "xref_attach", "block_import" };

    /// <summary>Object properties a field may show; names follow the AutoCAD field dialog.</summary>
    public static readonly IReadOnlyList<string> FieldProperties = ["Length", "Area", "ArcLength", "Circumference", "Perimeter", "Radius", "Diameter"];

    public static readonly IReadOnlyList<string> TextKinds = ["text", "mtext", "attributes", "dimensions", "tables", "block_definitions"];
    public static readonly IReadOnlyList<string> ValueTypes = ["string", "real", "int16", "int32", "distance", "scale", "point", "handle"];
    public const int MaxCopies = 2000, MaxXDataBytes = 16000;

    public static bool Supports(string kind) => Fields.ContainsKey(kind);

    public static void Validate(JsonElement op, IReadOnlySet<string> aliases)
    {
        string kind = EditPlan.RequiredText(op, "op");
        if (op.TryGetProperty("id", out _) && !Aliasable.Contains(kind)) throw new CadFault("INVALID_ALIAS", kind + " results cannot be referenced by id");
        bool Has(string name) => op.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;
        void Require(params string[] names) { foreach (var name in names) if (!Has(name)) throw new CadFault("MISSING_FIELD", kind + ": " + name); }
        void One(string handle, string target, bool required = true)
        {
            if (Has(handle) && Has(target)) throw new CadFault("INVALID_TARGET", kind + ": use " + handle + " or " + target + ", not both");
            if (required && !Has(handle) && !Has(target)) throw new CadFault("INVALID_TARGET", kind + ": supply " + handle + " or " + target);
        }

        foreach (var p in op.EnumerateObject())
        {
            var v = p.Value;
            switch (p.Name)
            {
                case "op": case "id": break;
                case "handle": case "other_handle": case "object_handle": Handle(v, p.Name); break;
                case "target": case "other_target": case "object_target":
                    if (v.ValueKind != JsonValueKind.String || !aliases.Contains(v.GetString()!)) throw new CadFault("UNKNOWN_TARGET", p.Name + " must refer to an earlier operation id");
                    break;
                case "center": case "first": case "second": case "position": case "side_point": case "pick_point": EditPlan.Point(v); break;
                case "points":
                    if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 2 or > 20) throw new CadFault("INVALID_POINTS", "points must hold 2..20 WCS points");
                    foreach (var point in v.EnumerateArray()) EditPlan.Point(point);
                    break;
                case "items": case "others": case "boundaries": References(v, p.Name, aliases); break;
                case "handles":
                    if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 1 or > 5000) throw new CadFault("INVALID_HANDLES", "handles must hold 1..5000 hexadecimal handles");
                    foreach (var handle in v.EnumerateArray()) Handle(handle, "handles");
                    break;
                case "layers": Strings(v, p.Name, 1, 50); break;
                case "names": if (Strings(v, p.Name, 1, 100).Distinct(StringComparer.OrdinalIgnoreCase).Count() != v.GetArrayLength()) throw new CadFault("INVALID_PARAMETER", "names must be unique"); break;
                case "include":
                    if (Strings(v, p.Name, 1, TextKinds.Count).Any(k => !TextKinds.Contains(k))) throw new CadFault("INVALID_PARAMETER", "include lists " + string.Join(", ", TextKinds));
                    break;
                case "mapping":
                    if (v.ValueKind != JsonValueKind.Object || v.EnumerateObject().Count() is < 1 or > 200) throw new CadFault("INVALID_MAPPING", "mapping is an object of 1..200 source:target layer names");
                    foreach (var pair in v.EnumerateObject())
                    {
                        if (string.IsNullOrWhiteSpace(pair.Name) || pair.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(pair.Value.GetString()))
                            throw new CadFault("INVALID_MAPPING", "Layer names must be nonempty strings");
                        if (string.Equals(pair.Name, pair.Value.GetString(), StringComparison.OrdinalIgnoreCase)) throw new CadFault("INVALID_MAPPING", "A layer cannot merge into itself: " + pair.Name);
                    }
                    break;
                case "values": break;
                case "distance": case "other_distance": case "row_spacing": case "column_spacing": case "angle_deg": case "radius":
                case "height": case "factor": case "scale": case "rotation_deg":
                    EditPlan.Numeric(op, p.Name); break;
                case "rows": case "columns": DraftingPlan.Integer(op, p.Name, 1, 100); break;
                case "vertices":
                    if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 1 or > 10000) throw new CadFault("INVALID_PARAMETER", "vertices must list 1..10000 vertex indices");
                    var indices = new HashSet<int>();
                    foreach (var index in v.EnumerateArray())
                        if (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out int vertex) || vertex < 0 || !indices.Add(vertex))
                            throw new CadFault("INVALID_PARAMETER", "vertices are distinct indices from 0");
                    break;
                case "count": DraftingPlan.Integer(op, p.Name, 2, 500); break;
                case "precision": DraftingPlan.Integer(op, p.Name, 0, 8); break;
                case "max_changes": DraftingPlan.Integer(op, p.Name, 1, 5000); break;
                case "color_index": DraftingPlan.Integer(op, p.Name, 0, 256); break;
                case "match_case": case "whole_word": case "keep_original": case "attributes_as_text": case "rotate_items": case "overlay":
                case "overwrite": case "create_missing": case "include_blocks": case "purge": case "delete":
                    if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new CadFault("INVALID_PARAMETER", p.Name + " must be true or false");
                    break;
                case "find": Text(v, p.Name, 1, 1000); break;
                case "replace": Text(v, p.Name, 0, 5000); break;
                case "text": Text(v, p.Name, 0, 5000); break;
                case "prefix": case "suffix": Text(v, p.Name, 0, 200); break;
                case "scope":
                    if (v.GetString() is not ("current" or "model" or "layout" or "all")) throw new CadFault("INVALID_PARAMETER", "scope must be current, model, layout or all");
                    break;
                case "end": if (v.GetString() is not ("start" or "end")) throw new CadFault("INVALID_PARAMETER", "end must be start or end"); break;
                case "path":
                    var path = Text(v, p.Name, 1, 1024);
                    if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) throw new CadFault("INVALID_PATH", "path must be an absolute .dwg file path");
                    break;
                case "property":
                    if (!FieldProperties.Contains(Text(v, p.Name, 1, 64), StringComparer.OrdinalIgnoreCase)) throw new CadFault("INVALID_PARAMETER", "property must be one of " + string.Join(", ", FieldProperties));
                    break;
                case "app": case "dictionary": case "key":
                    var name = Text(v, p.Name, 1, 255);
                    if (name.IndexOfAny(['\r', '\n', '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`']) >= 0) throw new CadFault("INVALID_PARAMETER", p.Name + " contains a character not allowed in AutoCAD names");
                    // AutoCAD and the vertical products keep their own data under these names (dimension overrides
                    // in ACAD XData, layouts in ACAD_LAYOUT); writing there breaks their objects.
                    if (p.Name is "app" or "dictionary" && Reserved(name))
                        throw new CadFault("RESERVED_NAME", p.Name + " " + name + " belongs to AutoCAD or a vertical product; use a name of your own such as CADMCP");
                    break;
                default: Text(v, p.Name, 1, 255); break; // name, layer, style, text_style, layout
            }
        }

        switch (kind)
        {
            case "text_replace":
                Require("find", "replace");
                if (Has("handles") && Has("scope")) throw new CadFault("INVALID_PARAMETER", "Use handles or scope, not both");
                if (op.Text("scope") == "layout" ? !Has("layout") : Has("layout")) throw new CadFault("INVALID_PARAMETER", "layout is required with scope layout and only then");
                break;
            case "offset":
                One("handle", "target"); Require("distance");
                if (Math.Abs(EditPlan.Numeric(op, "distance")) < 1e-12) throw new CadFault("INVALID_PARAMETER", "distance must be nonzero");
                break;
            case "explode": One("handle", "target"); break;
            case "join": One("handle", "target"); Require("others"); break;
            case "array_rect":
            {
                Require("items", "rows", "columns");
                int rows = DraftingPlan.Integer(op, "rows", 1, 100), columns = DraftingPlan.Integer(op, "columns", 1, 100);
                if (rows * columns < 2) throw new CadFault("INVALID_ARRAY", "An array needs at least two positions");
                if (rows > 1) Require("row_spacing");
                if (columns > 1) Require("column_spacing");
                Copies(op, rows * columns);
                break;
            }
            case "array_polar":
            {
                Require("items", "center", "count");
                double angle = EditPlan.Numeric(op, "angle_deg", 360);
                if (Math.Abs(angle) < 1e-9 || Math.Abs(angle) > 360) throw new CadFault("INVALID_ARRAY", "angle_deg must be nonzero and at most 360 in magnitude");
                Copies(op, DraftingPlan.Integer(op, "count", 2, 500));
                break;
            }
            case "fillet":
                One("handle", "target"); One("other_handle", "other_target"); Require("radius");
                if (EditPlan.Numeric(op, "radius") < 0) throw new CadFault("INVALID_PARAMETER", "radius must be zero or positive");
                break;
            case "chamfer":
                One("handle", "target"); One("other_handle", "other_target"); Require("distance");
                if (EditPlan.Numeric(op, "distance") <= 0 || EditPlan.Numeric(op, "other_distance", 1) <= 0) throw new CadFault("INVALID_PARAMETER", "Chamfer distances must be positive");
                break;
            case "polyline_fillet":
                One("handle", "target"); Require("radius");
                if (EditPlan.Numeric(op, "radius") <= 0) throw new CadFault("INVALID_PARAMETER", "radius must be positive");
                break;
            case "trim": One("handle", "target"); Require("boundaries", "pick_point"); break;
            case "extend": One("handle", "target"); Require("boundaries", "end"); break;
            case "dimension_angular": Require("center", "first", "second", "position"); break;
            case "mleader":
                Require("points", "text");
                if (Has("height") && EditPlan.Numeric(op, "height") <= 0) throw new CadFault("INVALID_PARAMETER", "height must be positive");
                break;
            case "xref_attach":
                Require("path", "position");
                if (Has("scale") && EditPlan.Numeric(op, "scale") <= 0) throw new CadFault("INVALID_PARAMETER", "scale must be positive");
                break;
            case "block_import": Require("path", "names"); break;
            case "layer_merge": Require("mapping"); break;
            case "xdata_set":
                One("handle", "target"); Require("app", "values");
                Values(op.GetProperty("values"), 0, 200, xdata: true);
                break;
            case "xrecord_set":
                One("handle", "target", required: false); Require("key");
                bool delete = Has("delete") && op.GetProperty("delete").GetBoolean();
                if (delete == Has("values")) throw new CadFault("INVALID_PARAMETER", "xrecord_set needs values, or delete:true without values");
                if (!delete) Values(op.GetProperty("values"), 1, 500, xdata: false);
                break;
            case "field_text":
                Require("position", "property"); One("object_handle", "object_target");
                if (Has("factor") && EditPlan.Numeric(op, "factor") <= 0) throw new CadFault("INVALID_PARAMETER", "factor must be positive");
                if (Has("height") && EditPlan.Numeric(op, "height") <= 0) throw new CadFault("INVALID_PARAMETER", "height must be positive");
                break;
        }
    }

    private static void Copies(JsonElement op, int positions)
    {
        long copies = (long)op.GetProperty("items").GetArrayLength() * (positions - 1);
        if (copies > MaxCopies) throw new CadFault("ARRAY_TOO_LARGE", "An array may create at most " + MaxCopies + " copies; this one would create " + copies);
    }

    private static void Handle(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String || !long.TryParse(value.GetString(), System.Globalization.NumberStyles.HexNumber, null, out long handle) || handle <= 0)
            throw new CadFault("INVALID_HANDLE", name + " must be a positive hexadecimal handle");
    }

    /// <summary>Handles or ids of earlier operations; an id wins when a string could be both.</summary>
    private static void References(JsonElement value, string name, IReadOnlySet<string> aliases)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 500) throw new CadFault("INVALID_REFERENCES", name + " must hold 1..500 handles or earlier ids");
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) throw new CadFault("INVALID_REFERENCES", name + " entries must be strings");
            string text = item.GetString()!;
            if (!aliases.Contains(text) && (!long.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out long handle) || handle <= 0))
                throw new CadFault("UNKNOWN_TARGET", name + ": " + text + " is neither a handle nor an earlier id");
        }
        if (value.EnumerateArray().Select(v => v.GetString()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.GetArrayLength())
            throw new CadFault("INVALID_REFERENCES", name + " must not repeat an entity");
    }

    private static string[] Strings(JsonElement value, string name, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < minimum || value.GetArrayLength() > maximum ||
            value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())))
            throw new CadFault("INVALID_PARAMETER", name + " must hold " + minimum + ".." + maximum + " nonempty strings");
        return value.EnumerateArray().Select(v => v.GetString()!).ToArray();
    }

    private static string Text(JsonElement value, string name, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String) throw new CadFault("INVALID_PARAMETER", name + " must be a string");
        var text = value.GetString()!;
        if (text.Length < minimum || text.Length > maximum || minimum > 0 && string.IsNullOrWhiteSpace(text))
            throw new CadFault("INVALID_PARAMETER", name + " must have " + minimum + ".." + maximum + " characters");
        return text;
    }

    /// <summary>
    /// Typed values for XData and XRecords: {type, value} with type string, real, int16, int32, distance,
    /// scale, point or handle. XData strings hold at most 255 characters without line breaks, and one
    /// application's XData stays under 16 KB.
    /// </summary>
    public static int Values(JsonElement values, int minimum, int maximum, bool xdata)
    {
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() < minimum || values.GetArrayLength() > maximum)
            throw new CadFault("INVALID_VALUES", "values must hold " + minimum + ".." + maximum + " {type,value} items");
        int bytes = 0;
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(p => p.Name is not ("type" or "value")) || item.Text("type") is not { } type || !ValueTypes.Contains(type) || !item.TryGetProperty("value", out var value))
                throw new CadFault("INVALID_VALUES", "Each value is {type,value} with type " + string.Join(", ", ValueTypes));
            switch (type)
            {
                case "string":
                    if (value.ValueKind != JsonValueKind.String) throw new CadFault("INVALID_VALUES", "string values must be strings");
                    var text = value.GetString()!;
                    if (xdata && (text.Length > 255 || text.IndexOfAny(['\r', '\n']) >= 0)) throw new CadFault("INVALID_VALUES", "XData strings hold at most 255 characters without line breaks");
                    if (!xdata && text.Length > 2048) throw new CadFault("INVALID_VALUES", "XRecord strings hold at most 2048 characters");
                    bytes += 3 + System.Text.Encoding.UTF8.GetByteCount(text); break;
                case "real": case "distance": case "scale":
                    if (value.ValueKind != JsonValueKind.Number || !double.IsFinite(value.GetDouble())) throw new CadFault("INVALID_VALUES", type + " values must be finite numbers");
                    bytes += 10; break;
                case "int16":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int small) || small is < short.MinValue or > short.MaxValue) throw new CadFault("INVALID_VALUES", "int16 values must be integers from -32768 to 32767");
                    bytes += 4; break;
                case "int32":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)) throw new CadFault("INVALID_VALUES", "int32 values must be 32-bit integers");
                    bytes += 6; break;
                case "point": EditPlan.Point(value); bytes += 26; break;
                case "handle": Handle(value, "handle value"); bytes += 10; break;
            }
        }
        if (xdata && bytes > MaxXDataBytes) throw new CadFault("XDATA_TOO_LARGE", "One application's XData must stay under 16 KB");
        return bytes;
    }
}
