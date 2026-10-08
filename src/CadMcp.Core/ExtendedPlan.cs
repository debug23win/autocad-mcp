using System.Text.Json;
namespace CadMcp.Core;

public static class ExtendedPlan
{
    public static readonly IReadOnlyDictionary<string,string> Fields=new Dictionary<string,string>
    {
        ["solid_loft"]="sections guides path ruled layer color_index layout",
        ["solid_fillet"]="handle target edges radius",
        ["solid_chamfer"]="handle target edges base_face base_distance other_distance",
        ["solid_shell"]="handle target faces offset",
        ["solid_section"]="handle target origin normal layer color_index layout",
        ["assembly_create"]="kind position parameters mark material density show_annotations layer color_index layout",
        ["assembly_update"]="handle target parameters mark material density show_annotations",
        ["assembly_schedule"]="position handles title text_height column_widths layer layout",
        ["civil_alignment_create"]="name layer style label_set site points radii",
        ["civil_alignment_add_line"]="handle start end",
        ["civil_profile_create"]="name alignment_handle layer_handle style_handle label_set_handle layer style label_set points",
        ["civil_profile_from_surface"]="name alignment_handle surface_handle layer_handle style_handle label_set_handle layer style label_set",
        ["civil_profile_add_tangent"]="handle start end",
        ["civil_network_create"]="name parts_list_handle parts_list surface_handle alignment_handle",
        ["civil_network_add_pipe"]="handle family_handle size_handle family size start end apply_rules",
        ["civil_network_add_structure"]="handle family_handle size_handle family size position rotation_deg apply_rules",
        ["civil_set"]="handle properties",
        ["map_coordinate_system"]="code force",
        ["map_od_table"]="name description fields",
        ["map_od_add"]="handle table values",
        ["map_od_update"]="handle table record_index values"
    };
    public static void Validate(JsonElement op)
    {
        string kind=op.Text("op")!;
        void Require(params string[] names){foreach(var name in names)if(!op.TryGetProperty(name,out _))throw new CadFault("MISSING_FIELD",kind+":"+name);}
        // A Civil object is named by its handle or, alternatively, by its name in the drawing.
        void OneOf(string handle,string name){if(op.TryGetProperty(handle,out _)==op.TryGetProperty(name,out _))throw new CadFault("MISSING_FIELD",kind+": supply "+handle+" or "+name+", not both");}
        if(kind is "solid_fillet" or "solid_chamfer" or "solid_shell" or "solid_section" or "assembly_update")
            if((op.Text("handle") is null)==(op.Text("target") is null))throw new CadFault("INVALID_TARGET","Supply one handle or earlier target");
        switch(kind)
        {
            case "solid_loft":Require("sections");if(op.GetProperty("sections").GetArrayLength() is <2 or >30)throw new CadFault("INVALID_LOFT","2..30 cross sections required");break;
            case "solid_fillet":Require("edges","radius");DraftingPlan.Positive(op,"radius",0);break;
            case "solid_chamfer":Require("edges","base_face","base_distance","other_distance");DraftingPlan.Positive(op,"base_distance",0);DraftingPlan.Positive(op,"other_distance",0);break;
            case "solid_shell":Require("faces","offset");if(Math.Abs(EditPlan.Numeric(op,"offset"))<1e-10)throw new CadFault("INVALID_SHELL","Nonzero offset required");break;
            case "solid_section":Require("origin","normal");break;
            case "assembly_create":Require("kind","position","parameters","mark","material");break;
            case "assembly_update":Require("parameters");break;
            case "assembly_schedule":Require("position","handles");break;
            case "civil_alignment_create":
                Require("name","layer","style","label_set","points");
                if(op.GetProperty("points").ValueKind!=JsonValueKind.Array)throw new CadFault("INVALID_POINTS","points must be an array of WCS points");
                var pis=op.GetProperty("points").EnumerateArray().Select(EditPlan.Point).Select(p=>(p[0],p[1])).ToArray();
                if(pis.Length is <2 or >500)throw new CadFault("INVALID_POINTS","2..500 PI points required");
                // An empty or missing site makes a siteless alignment.
                if(op.TryGetProperty("site",out var site)&&site.ValueKind!=JsonValueKind.String)throw new CadFault("INVALID_PARAMETER","site must be a site name or empty");
                double[]? radii=null;
                if(op.TryGetProperty("radii",out var radiusList))
                {
                    if(radiusList.ValueKind!=JsonValueKind.Array||radiusList.EnumerateArray().Any(r=>r.ValueKind!=JsonValueKind.Number))throw new CadFault("INVALID_RADII","radii must be an array of numbers");
                    radii=radiusList.EnumerateArray().Select(r=>r.GetDouble()).ToArray();
                }
                // Geometry that cannot be built is rejected before Civil 3D is called.
                AlignmentGeometry.Compute(pis,radii);
                break;
            case "civil_alignment_add_line" or "civil_profile_add_tangent":Require("handle","start","end");break;
            case "civil_profile_create":Require("name","alignment_handle","points");OneOf("layer_handle","layer");OneOf("style_handle","style");OneOf("label_set_handle","label_set");break;
            case "civil_profile_from_surface":Require("name","alignment_handle","surface_handle");OneOf("layer_handle","layer");OneOf("style_handle","style");OneOf("label_set_handle","label_set");break;
            case "civil_network_create":Require("name");OneOf("parts_list_handle","parts_list");break;
            case "civil_network_add_pipe":Require("handle","start","end");OneOf("family_handle","family");OneOf("size_handle","size");break;
            case "civil_network_add_structure":Require("handle","position");OneOf("family_handle","family");OneOf("size_handle","size");break;
            case "civil_set":Require("handle","properties");break;
            case "map_coordinate_system":Require("code");break;
            case "map_od_table":Require("name","fields");break;
            case "map_od_add" or "map_od_update":Require("handle","table","values");break;
        }
        foreach(var p in op.EnumerateObject())
        {
            if(p.Name is "origin" or "normal" or "position" or "start" or "end")EditPlan.Point(p.Value);
            if(p.Name is "edges" or "faces")
                if(p.Value.ValueKind!=JsonValueKind.Array||p.Value.GetArrayLength()>100||p.Value.EnumerateArray().Any(v=>!v.TryGetInt64(out var n)||n<=0))throw new CadFault("INVALID_SUBENTITIES","Use positive ids from cad_solid_get, at most 100");
            if(p.Name.EndsWith("_handle",StringComparison.Ordinal)||p.Name=="handle")
                if(!long.TryParse(p.Value.GetString(),System.Globalization.NumberStyles.HexNumber,null,out long n)||n<=0)throw new CadFault("INVALID_HANDLE",p.Name);
        }
        if((kind.StartsWith("civil_",StringComparison.Ordinal)||kind.StartsWith("map_",StringComparison.Ordinal))&&op.TryGetProperty("id",out _))throw new CadFault("INVALID_ALIAS","Vertical operations return handles in detail; use them in a subsequent request");
        foreach(var p in op.EnumerateObject())
        {
            if(p.Name is "show_annotations" or "apply_rules" or "ruled" or "force" && p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new CadFault("INVALID_BOOLEAN",p.Name);
            if(p.Name is "parameters" or "properties" or "values" && p.Value.ValueKind!=JsonValueKind.Object)throw new CadFault("INVALID_PARAMETER",p.Name+" must be an object");
            if(p.Name is "mark" or "material" or "name" or "code" or "layer" or "style" or "label_set" or "table" or "title" or "layout" or "parts_list" or "family" or "size")EditPlan.RequiredText(op,p.Name);
        }
        if(op.TryGetProperty("density",out _))DraftingPlan.Positive(op,"density",0);
        if(op.TryGetProperty("color_index",out var aci)&&(!aci.TryGetInt32(out var c)||c is <0 or >256))throw new CadFault("INVALID_COLOR","ACI 0..256 required");
        if(kind=="solid_loft")foreach(string field in new[]{"sections","guides","path"})if(op.TryGetProperty(field,out var selections))
        {
            foreach(var selector in field=="path"?new[]{selections}:selections.EnumerateArray().ToArray())
                if(selector.ValueKind!=JsonValueKind.Object || selector.EnumerateObject().Any(p=>p.Name is not ("handle" or "target")) || (selector.Text("handle") is null)==(selector.Text("target") is null))throw new CadFault("INVALID_LOFT","Selectors require exactly one handle or target");
        }
        ValidateTree(op,0);
    }
    private static void ValidateTree(JsonElement e,int depth)
    {
        if(depth>8)throw new CadFault("INVALID_PLAN","Nested parameters exceed depth limit");
        if(e.ValueKind==JsonValueKind.Number&&!double.IsFinite(e.GetDouble()))throw new CadFault("INVALID_PARAMETER","Finite numbers required");
        if(e.ValueKind==JsonValueKind.Array){if(e.GetArrayLength()>1000)throw new CadFault("INVALID_PLAN","Array limit 1000");foreach(var v in e.EnumerateArray())ValidateTree(v,depth+1);}
        if(e.ValueKind==JsonValueKind.Object){var keys=new HashSet<string>();foreach(var p in e.EnumerateObject()){if(!keys.Add(p.Name))throw new CadFault("DUPLICATE_FIELD",p.Name);ValidateTree(p.Value,depth+1);}}
    }
}
