using System.Text.Json;
using System.Text.RegularExpressions;

namespace CadMcp.Core;

public static class DraftingPlan
{
    public static readonly IReadOnlyDictionary<string,string> Fields = new Dictionary<string,string>
    {
        ["table_create"]="position rows columns column_widths row_heights row_height column_width text_height style cells merges layer color_index layout",
        ["table_cells"]="handle target cells layout",
        ["table_merge"]="handle target first_row first_column last_row last_column unmerge layout",
        ["table_rows"]="handle target action index count height layout",
        ["table_columns"]="handle target action index count width layout",
        ["table_recalculate"]="handle target layout",
        ["spds_table"]="position template data title scale style layer color_index layout",
        ["spds_dimstyle"]="name drawing_scale measurement_factor text_height text_style ticks",
        ["spds_sheet"]="layout format orientation designation project building drawing_title organization stage sheet sheets signatures text_style",
        ["spds_axis"]="first second label bubble_diameter scale layer color_index layout",
        ["spds_level"]="position elevation direction scale text_style layer color_index layout",
        ["dimension_rotated"]="first second position angle_deg text style layer color_index layout",
        ["dimension_radius"]="center chord position text style layer color_index layout",
        ["dimension_diameter"]="first second position text style layer color_index layout"
    };
    public static bool Supports(string kind)=>Fields.ContainsKey(kind);
    public static int Integer(JsonElement op,string key,int min,int max,int? fallback=null)
    {
        if(!op.TryGetProperty(key,out var v) && fallback.HasValue)return fallback.Value;
        if(v.ValueKind!=JsonValueKind.Number || !v.TryGetInt32(out int n) || n<min || n>max)throw new CadFault("INVALID_DRAFTING_PLAN",$"{key} must be {min}..{max}");
        return n;
    }
    public static double Positive(JsonElement op,string key,double fallback)
    { double n=EditPlan.Numeric(op,key,fallback); if(n<=0)throw new CadFault("INVALID_DRAFTING_PLAN",key+" must be positive");return n; }
    public static (int Row,int Column) Address(string address)
    {
        var m=Regex.Match(address,@"^\$?([A-Za-z]{1,3})\$?([1-9][0-9]{0,3})$");
        if(!m.Success)throw new CadFault("INVALID_CELL","Use an A1 cell address");
        int c=0; foreach(char x in m.Groups[1].Value.ToUpperInvariant())c=c*26+x-'A'+1;
        return (int.Parse(m.Groups[2].Value)-1,c-1);
    }
    public static string Address(int row,int column)
    { string c="";for(int n=column+1;n>0;n=(n-1)/26)c=(char)('A'+(n-1)%26)+c;return c+(row+1); }
    public static void Validate(JsonElement op,HashSet<string> aliases)
    {
        string kind=EditPlan.RequiredText(op,"op");
        if(kind is "table_cells" or "table_merge" or "table_rows" or "table_columns" or "table_recalculate")
            if(op.TryGetProperty("handle",out _)==op.TryGetProperty("target",out _))throw new CadFault("INVALID_TARGET","Use one handle or target");
        foreach(var p in op.EnumerateObject())
        {
            if(p.Name is "name" or "title" or "designation" or "project" or "building" or "drawing_title" or "organization" or "stage" or "sheet" or "sheets" or "direction" or "orientation" or "template" or "label" or "text" or "action" or "format")
                if(p.Value.ValueKind!=JsonValueKind.String||p.Value.GetString()!.Length>5000)throw new CadFault("INVALID_DRAFTING_PLAN",p.Name+" must be text (at most 5000 characters)");
            if(p.Name is "position" or "first" or "second" or "chord" or "center")EditPlan.Point(p.Value);
            if(p.Name is "angle_deg" or "elevation")EditPlan.Numeric(op,p.Name);
            if(p.Name is "scale" or "text_height" or "row_height" or "column_width" or "drawing_scale" or "measurement_factor" or "bubble_diameter")Positive(op,p.Name,1);
            if(p.Name is "unmerge" or "ticks" && p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new CadFault("INVALID_DRAFTING_PLAN",p.Name+" must be Boolean");
            if(p.Name is "handle" && (!long.TryParse(p.Value.GetString(),System.Globalization.NumberStyles.HexNumber,null,out var h)||h<=0))throw new CadFault("INVALID_HANDLE","Use a hexadecimal table handle");
        }
        if(kind=="table_create")
        {
            EditPlan.Point(op.GetProperty("position"));int rows=Integer(op,"rows",1,500),cols=Integer(op,"columns",1,50);
            if(rows*cols>5000)throw new CadFault("TABLE_TOO_LARGE","At most 5000 cells");
            Sizes(op,"column_widths",cols);Sizes(op,"row_heights",rows);
            if(op.TryGetProperty("cells",out var items))foreach(var cell in items.EnumerateArray())
            {var (r,c)=Address(EditPlan.RequiredText(cell,"cell"));if(r>=rows||c>=cols)throw new CadFault("CELL_OUT_OF_RANGE","Cell is outside the declared table");}
            if(op.TryGetProperty("merges",out var merges))
            {
                if(merges.ValueKind!=JsonValueKind.Array||merges.GetArrayLength()>200)throw new CadFault("INVALID_CELL_RANGE","At most 200 merge ranges");
                foreach(var range in merges.EnumerateArray())
                {if(range.ValueKind!=JsonValueKind.Array||range.GetArrayLength()!=4||range.EnumerateArray().Any(v=>v.ValueKind!=JsonValueKind.Number||!v.TryGetInt32(out var n)||n<0))throw new CadFault("INVALID_CELL_RANGE","Merge needs four zero-based indices");
                 if(range[0].GetInt32()>range[2].GetInt32()||range[1].GetInt32()>range[3].GetInt32()||range[2].GetInt32()>=rows||range[3].GetInt32()>=cols)throw new CadFault("INVALID_CELL_RANGE","Merge is outside table");}
            }
        }
        if(kind is "table_create" or "table_cells")
        {
            if(op.TryGetProperty("cells",out var cells))Cells(cells,aliases);
            else if(kind=="table_cells")throw new CadFault("INVALID_CELLS","Provide cells");
        }
        if(kind=="table_merge")
        { foreach(string k in new[]{"first_row","first_column","last_row","last_column"})Integer(op,k,0,499); }
        if(kind is "table_rows" or "table_columns")
        {
            if(op.Text("action") is not ("insert" or "delete"))throw new CadFault("INVALID_DRAFTING_PLAN","action: insert/delete");
            Integer(op,"index",0,500);Integer(op,"count",1,500);
            if(op.Text("action")=="insert")Positive(op,kind=="table_rows"?"height":"width",8);
        }
        if(kind=="spds_table")
        {
            SpdsTemplates.Find(EditPlan.RequiredText(op,"template"));EditPlan.Point(op.GetProperty("position"));
            var data=op.GetProperty("data");if(data.ValueKind!=JsonValueKind.Array||data.GetArrayLength()>200)throw new CadFault("INVALID_CELLS","data: 0..200 rows of cell values");
            int cols=SpdsTemplates.Find(op.Text("template")!).Widths.Length;
            foreach(var row in data.EnumerateArray())
            { if(row.ValueKind!=JsonValueKind.Array||row.GetArrayLength()!=cols)throw new CadFault("INVALID_CELLS","Each row must match template columns"); foreach(var cell in row.EnumerateArray())Scalar(cell); }
        }
        if(kind=="spds_dimstyle")EditPlan.RequiredText(op,"name");
        if(kind=="spds_sheet")
        {
            EditPlan.RequiredText(op,"layout");if(op.Text("format") is not ("A0" or "A1" or "A2" or "A3" or "A4"))throw new CadFault("INVALID_FORMAT","format: A0..A4");
            if(op.Text("orientation") is not (null or "portrait" or "landscape"))throw new CadFault("INVALID_FORMAT","orientation: portrait/landscape");
            if(op.TryGetProperty("signatures",out var s) && (s.ValueKind!=JsonValueKind.Object||s.EnumerateObject().Any(p=>p.Value.ValueKind!=JsonValueKind.String)))throw new CadFault("INVALID_SIGNATURES","signatures: role/name/sign/date text pairs");
        }
        if(kind=="spds_axis") { EditPlan.Point(op.GetProperty("first"));EditPlan.Point(op.GetProperty("second"));EditPlan.RequiredText(op,"label"); }
        if(kind=="spds_level") { EditPlan.Point(op.GetProperty("position"));EditPlan.Numeric(op,"elevation");if(op.Text("direction") is not (null or "left" or "right"))throw new CadFault("INVALID_DRAFTING_PLAN","direction: left/right"); }
        if(kind.StartsWith("dimension_"))
        { EditPlan.Point(op.GetProperty("position"));if(kind=="dimension_radius"){EditPlan.Point(op.GetProperty("center"));EditPlan.Point(op.GetProperty("chord"));}else{EditPlan.Point(op.GetProperty("first"));EditPlan.Point(op.GetProperty("second"));} }
    }
    private static void Sizes(JsonElement op,string key,int count)
    {
        if(!op.TryGetProperty(key,out var list))return;
        if(list.ValueKind!=JsonValueKind.Array||list.GetArrayLength()!=count ||list.EnumerateArray().Any(v=>v.ValueKind!=JsonValueKind.Number||!v.TryGetDouble(out var n)||!double.IsFinite(n)||n<=0))throw new CadFault("INVALID_TABLE_SIZE",key+" must match the row/column count with positive sizes");
    }
    private static void Scalar(JsonElement value)
    {
        if(value.ValueKind is JsonValueKind.String or JsonValueKind.Null)return;
        if(value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out var n)&&double.IsFinite(n))return;
        throw new CadFault("INVALID_CELLS","Cell values must be text, finite numbers or null");
    }
    private static void Cells(JsonElement list,HashSet<string> aliases)
    {
        if(list.ValueKind!=JsonValueKind.Array||list.GetArrayLength() is <1 or >1000)throw new CadFault("INVALID_CELLS","Provide 1..1000 cell edits");
        var used=new HashSet<string>();
        foreach(var cell in list.EnumerateArray())
        {
            var address=EditPlan.RequiredText(cell,"cell");Address(address);
            if(!used.Add(address.ToUpperInvariant()))throw new CadFault("INVALID_CELLS","Duplicate cell edit");
            var keys=cell.EnumerateObject().Select(p=>p.Name).ToArray();
            if(keys.Any(k=>k is not ("cell" or "value" or "formula" or "references" or "precision" or "alignment")))throw new CadFault("INVALID_CELLS","Unknown cell property");
            if(cell.TryGetProperty("value",out var v)==cell.TryGetProperty("formula",out _))throw new CadFault("INVALID_CELLS","Use one value or formula");
            if(cell.TryGetProperty("value",out v))Scalar(v);
            if(cell.TryGetProperty("formula",out var formula))
            {
                string text=EditPlan.RequiredText(cell,"formula");if(!text.StartsWith('=')||text.Length>2048||text.Contains("%<")||text.Contains('"'))throw new CadFault("INVALID_FORMULA","Use = arithmetic or native table functions, and {{name}} for declared references");
                var refs=new HashSet<string>();
                if(cell.TryGetProperty("references",out var references))
                {
                    if(references.ValueKind!=JsonValueKind.Array||references.GetArrayLength()>20)throw new CadFault("INVALID_FORMULA","At most 20 table references");
                    foreach(var r in references.EnumerateArray())
                    {
                        string name=EditPlan.RequiredText(r,"name");if(!Regex.IsMatch(name,@"^[A-Za-z][A-Za-z0-9_]{0,31}$")||!refs.Add(name))throw new CadFault("INVALID_FORMULA","Reference names must be unique identifiers");
                        Address(EditPlan.RequiredText(r,"cell"));
                        if(r.TryGetProperty("table_handle",out _)==r.TryGetProperty("table_target",out _))throw new CadFault("INVALID_FORMULA","Use table_handle or table_target");
                        if(r.Text("table_target") is {} target && !aliases.Contains(target))throw new CadFault("UNKNOWN_TARGET",target);
                        if(r.Text("table_handle") is {} handle && (!long.TryParse(handle,System.Globalization.NumberStyles.HexNumber,null,out long h)||h<=0))throw new CadFault("INVALID_HANDLE",handle);
                    }
                }
                var tokens=Regex.Matches(text,@"\{\{([^{}]+)\}\}").Select(m=>m.Groups[1].Value).ToHashSet();
                if(!tokens.SetEquals(refs)||Regex.Replace(text,@"\{\{[^{}]+\}\}","").IndexOfAny(['{','}'])>=0)throw new CadFault("INVALID_FORMULA","Every {{name}} needs one reference; unused references are invalid");
            }
            if(cell.TryGetProperty("precision",out _))Integer(cell,"precision",0,8);
            if(cell.Text("alignment") is not (null or "left" or "center" or "right"))throw new CadFault("INVALID_CELLS","alignment: left/center/right");
        }
    }
}

public sealed record SpdsTableTemplate(string Id,string Standard,string Form,string[] Headers,double[] Widths,double HeaderHeight=15,double RowHeight=8,bool VerifiedForm=true);
public static class SpdsTemplates
{
    public static IReadOnlyList<SpdsTableTemplate> Tables {get;} = new SpdsTableTemplate[]
    {
        new("sheet_register","ГОСТ Р 21.101-2026","Б.1 / форма 1",["Лист","Наименование","Примечание"],[15,140,30]),
        new("document_register","ГОСТ Р 21.101-2026","Б.4 / форма 2 с исправлением обозначения графы",["Обозначение","Наименование","Примечание"],[15,140,30]),
        new("specification","ГОСТ Р 21.101-2026","И / форма 7",["Поз.","Обозначение","Наименование","Кол.","Масса\nед., кг","Примечание"],[15,60,65,10,15,20]),
        new("rebar_schedule","ГОСТ 21.501-2018","Рабочая ведомость арматуры: проектный шаблон",["Поз.","Класс / диаметр","Эскиз / длина, мм","Кол.","Масса ед., кг","Всего, кг"],[15,40,65,15,25,25],15,8,false),
        new("steel_consumption","ГОСТ 21.501-2018","Рабочая ведомость расхода стали: проектный шаблон",["Марка элемента","Класс стали","Диаметр / профиль","Масса, кг","Примечание"],[35,35,55,30,30],15,8,false),
        new("km_members","ГОСТ 21.502-2016","Рабочая ведомость элементов: проектный шаблон",["Марка","Профиль / стандарт","Сталь","Длина, мм","Кол.","Масса ед., кг","Всего, кг"],[20,65,30,25,15,25,25],15,8,false)
    };
    public static SpdsTableTemplate Find(string id)=>Tables.FirstOrDefault(t=>t.Id==id)??throw new CadFault("UNKNOWN_TEMPLATE",id);
}
