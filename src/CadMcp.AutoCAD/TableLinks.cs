using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

// Native fields remain in DWG; the Xrecord stores identities and dependencies, never duplicate numeric values.
internal static class TableLinks
{
    private const string Key = "CADMCP_TABLE_LINKS_V1";
    internal sealed record Cell(string Id, string Table, int Row, int Column);
    internal sealed record Reference(string Name, string Cell);
    internal sealed record Formula(string Cell, string Expression, Reference[] References, int Precision, bool Local, string? BoundExpression = null);
    internal sealed record Graph(List<Cell> Cells, List<Formula> Formulas);
    private static readonly Regex Address = new(@"\b[A-Z]{1,2}[1-9][0-9]*\b", RegexOptions.CultureInvariant);
    internal static Graph Load(Database db, Transaction tr)
    {
        var dictionary = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!dictionary.Contains(Key)) return new([], []);
        var record = (Xrecord)tr.GetObject(dictionary.GetAt(Key), OpenMode.ForRead);
        using var data = record.Data;
        return JsonSerializer.Deserialize<Graph>(string.Concat(data.AsArray().Select(v => (string)v.Value)), Wire.Json)
            ?? throw new CadFault("TABLE_GRAPH_INVALID", "Cannot read table dependencies");
    }
    internal static void Save(Database db, Transaction tr, Graph graph)
    {
        var dictionary = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        Xrecord record;
        if (dictionary.Contains(Key)) record = (Xrecord)tr.GetObject(dictionary.GetAt(Key), OpenMode.ForWrite);
        else { dictionary.UpgradeOpen(); record = new(); dictionary.SetAt(Key, record); tr.AddNewlyCreatedDBObject(record, true); }
        string json = JsonSerializer.Serialize(graph, Wire.Json);
        using var buffer = new ResultBuffer(Enumerable.Range(0, (json.Length + 999) / 1000)
            .Select(i => new TypedValue((int)DxfCode.Text, json.Substring(i * 1000, Math.Min(1000, json.Length - i * 1000)))).ToArray());
        record.Data = buffer;
    }
    private static Cell Identity(Graph graph, Table table, int row, int column)
    {
        string handle = table.Handle.ToString();
        string? identity;
        try{identity=table.Cells[row,column].GetCustomData("CADMCP_CELL_ID") as string;}catch{identity=null;}
        if(identity is null || graph.Cells.Any(c=>c.Id==identity && c.Table!=handle)){identity=Guid.NewGuid().ToString("N");if(!table.IsWriteEnabled)table.UpgradeOpen();table.Cells[row,column].SetCustomData("CADMCP_CELL_ID",identity);}
        var cell = graph.Cells.FirstOrDefault(c => c.Id==identity);
        if(cell is not null)return cell;
        cell = new(identity, handle, row, column); graph.Cells.Add(cell); return cell;
    }
    internal static void Register(Table table, Database db, Transaction tr, JsonElement item, Dictionary<string, ObjectId> aliases)
    {
        var graph = Load(db, tr); var (row, col) = DraftingPlan.Address(item.Text("cell")!);
        var target = Identity(graph, table, row, col); graph.Formulas.RemoveAll(f => f.Cell == target.Id);
        if (item.Text("formula") is { } expression)
        {
            bool local = !item.TryGetProperty("references", out var references) || references.GetArrayLength() == 0;
            var refs = new List<Reference>();
            if (local)
            {
                foreach (Match match in Address.Matches(expression).DistinctBy(m => m.Value))
                {
                    var (r, c) = DraftingPlan.Address(match.Value);
                    if (r >= table.Rows.Count || c >= table.Columns.Count) throw new CadFault("BROKEN_TABLE_REFERENCE", match.Value);
                    refs.Add(new(match.Value, Identity(graph, table, r, c).Id));
                }
                // Range interiors also participate in cycle detection and evaluation order.
                foreach (Match range in Regex.Matches(expression, @"([A-Z]{1,2}[1-9][0-9]*):([A-Z]{1,2}[1-9][0-9]*)"))
                {
                    var (r1, c1) = DraftingPlan.Address(range.Groups[1].Value); var (r2, c2) = DraftingPlan.Address(range.Groups[2].Value);
                    for (int r = Math.Min(r1,r2); r <= Math.Max(r1,r2); r++) for (int c = Math.Min(c1,c2); c <= Math.Max(c1,c2); c++)
                        refs.Add(new("@range", Identity(graph, table, r, c).Id));
                }
            }
            else foreach (var reference in references.EnumerateArray())
            {
                var sourceId = reference.Text("table_target") is { } alias ? aliases[alias] : NativeTables.Resolve(db, reference.Text("table_handle")!);
                var source = tr.GetObject(sourceId, OpenMode.ForRead) as Table ?? throw new CadFault("INVALID_TABLE_REFERENCE", "Native Table required");
                var (r, c) = DraftingPlan.Address(reference.Text("cell")!);
                if (r >= source.Rows.Count || c >= source.Columns.Count) throw new CadFault("BROKEN_TABLE_REFERENCE", reference.Text("cell")!);
                refs.Add(new(reference.Text("name")!, Identity(graph, source, r, c).Id));
            }
            var formula = new Formula(target.Id, expression, refs.ToArray(), DraftingPlan.Integer(item, "precision", 0, 8, 3), local);
            graph.Formulas.Add(formula with { BoundExpression = local ? null : Bind(formula, graph.Cells.ToDictionary(c=>c.Id), db, tr) });
        }
        Order(graph);
        var needed=graph.Formulas.Select(f=>f.Cell).Concat(graph.Formulas.SelectMany(f=>f.References.Select(r=>r.Cell))).ToHashSet();graph.Cells.RemoveAll(c=>!needed.Contains(c.Id));
        Save(db, tr, graph);
    }
    private static Formula[] Order(Graph graph)
    {
        var formulas = graph.Formulas.ToDictionary(f => f.Cell); var states = new Dictionary<string,int>(); var result = new List<Formula>();
        void Visit(Formula formula)
        {
            if (states.GetValueOrDefault(formula.Cell) == 1) throw new CadFault("CYCLIC_FORMULA", "Table dependency cycle; no changes committed");
            if (states.GetValueOrDefault(formula.Cell) == 2) return;
            states[formula.Cell] = 1;
            foreach (var reference in formula.References) if (formulas.TryGetValue(reference.Cell, out var source)) Visit(source);
            states[formula.Cell] = 2; result.Add(formula);
        }
        foreach (var formula in graph.Formulas) Visit(formula);
        return result.ToArray();
    }
    internal static void Resize(Table table, Database db, Transaction tr, JsonElement op)
    {
        var graph = Load(db, tr); bool rows = op.Text("op") == "table_rows", insert = op.Text("action") == "insert";
        int index = op.GetProperty("index").GetInt32(), count = op.GetProperty("count").GetInt32();
        var deleted = graph.Cells.Where(c => c.Table == table.Handle.ToString() && !insert && (rows ? c.Row : c.Column) >= index && (rows ? c.Row : c.Column) < index + count).Select(c => c.Id).ToHashSet();
        if (graph.Formulas.Any(f => !deleted.Contains(f.Cell) && f.References.Any(r => deleted.Contains(r.Cell))))
            throw new CadFault("REFERENCED_CELL_DELETE", "A deleted row/column supplies a formula. Remove its dependent formulas first.");
        graph.Formulas.RemoveAll(f => deleted.Contains(f.Cell)); graph.Cells.RemoveAll(c => deleted.Contains(c.Id));
        for(int n=0;n<graph.Cells.Count;n++)
        {
            var c=graph.Cells[n]; int value=rows?c.Row:c.Column;
            if(c.Table==table.Handle.ToString() && value>=index)graph.Cells[n]=rows?c with {Row=c.Row+(insert?count:-count)}:c with {Column=c.Column+(insert?count:-count)};
        }
        Save(db,tr,graph);
    }
    internal static object Recalculate(Database db, Transaction tr)
    {
        var graph=Load(db,tr); Rebind(graph,db,tr); var cells=graph.Cells.ToDictionary(c=>c.Id); var updated=new HashSet<ObjectId>();
        foreach(var formula in Order(graph))
        {
            var identity=cells[formula.Cell]; var table=Table(identity,db,tr); table.UpgradeOpen(); var target=table.Cells[identity.Row,identity.Column];
            if(formula.Local)
            {
                var addressMap=formula.References.Where(r=>r.Name!="@range").ToDictionary(r=>r.Name,r=>cells[r.Cell]);
                string localExpression=Address.Replace(formula.Expression,m=>addressMap.TryGetValue(m.Value,out var c)?DraftingPlan.Address(c.Row,c.Column):m.Value);
                if(target.Contents[0].Formula.Trim('=','(',')',' ')!=localExpression.Trim('=','(',')',' ')){target.FieldId=ObjectId.Null;target.Contents[0].Formula=localExpression;table.GenerateLayout();table.RecomputeTableBlock(true);}
            }
            else
            {
                string expression=Bind(formula,cells,db,tr);
                Field field;
                if(!target.FieldId.IsNull && formula.BoundExpression==expression)field=(Field)tr.GetObject(target.FieldId,OpenMode.ForWrite);
                else
                {
                    target.FieldId=ObjectId.Null;
                    field=new Field("%<\\AcExpr ("+expression[1..]+") \\f \"%lu2%pr"+formula.Precision+"\">%");
                    db.AddDBObject(field);tr.AddNewlyCreatedDBObject(field,true);target.FieldId=field.ObjectId;
                    graph.Formulas[graph.Formulas.IndexOf(formula)]=formula with{BoundExpression=expression};
                }
                field.EvaluationOption=FieldEvaluationOptions.Automatic;field.Evaluate((int)FieldEvaluationContext.Demand,db);
                if(field.Value is null||field.GetStringValue().Contains("####",StringComparison.Ordinal))throw new CadFault("FORMULA_EVALUATION_FAILED",identity.Table+":"+DraftingPlan.Address(identity.Row,identity.Column)+" "+field.EvaluationStatus+" "+field.GetFieldCode(FieldCodeFlags.AddMarkers)+" refs="+string.Join(";",formula.References.Select(r=>{var c=cells[r.Cell];var t=Table(c,db,tr);return c.Table+":"+DraftingPlan.Address(c.Row,c.Column)+" value="+t.Cells[c.Row,c.Column].Value+" formula="+t.Cells[c.Row,c.Column].Contents[0].Formula;})));
                table.GenerateLayout();table.RecomputeTableBlock(true);
            }
            updated.Add(table.ObjectId);
        }
        if(graph.Formulas.Count>0)Save(db,tr,graph);
        return new {formulas=graph.Formulas.Count,updated_tables=updated.Select(id=>id.Handle.ToString()).ToArray(),cycle_check="passed",references="stable_cell_identity"};
    }
    private static void Rebind(Graph graph,Database db,Transaction tr)
    {
        foreach(var group in graph.Cells.GroupBy(c=>c.Table).ToArray())
        {
            var table=tr.GetObject(NativeTables.Resolve(db,group.Key),OpenMode.ForRead) as Table??throw new CadFault("BROKEN_TABLE_REFERENCE",group.Key);
            var wanted=group.Select(c=>c.Id).ToHashSet();var found=new Dictionary<string,(int Row,int Column)>();
            for(int r=0;r<table.Rows.Count;r++)for(int c=0;c<table.Columns.Count;c++)
            {string? id;try{id=table.Cells[r,c].GetCustomData("CADMCP_CELL_ID") as string;}catch{id=null;}if(id is not null&&wanted.Contains(id)){if(!found.TryAdd(id,(r,c)))throw new CadFault("DUPLICATE_CELL_ID","Copied cell identities require rebuilding affected formulas");}}
            foreach(var cell in group)
            {if(!found.TryGetValue(cell.Id,out var location))throw new CadFault("BROKEN_TABLE_REFERENCE","Tracked cell was deleted outside CAD MCP: "+group.Key+":"+DraftingPlan.Address(cell.Row,cell.Column));graph.Cells[graph.Cells.IndexOf(cell)]=cell with{Row=location.Row,Column=location.Column};}
        }
    }
    private static string Bind(Formula formula, Dictionary<string,Cell> cells,Database db,Transaction tr)
    {
        string expression=formula.Expression;
        foreach(var reference in formula.References)
        {
            var c=cells[reference.Cell];var source=Table(c,db,tr);
            expression=expression.Replace("{{"+reference.Name+"}}","Table(%<\\_ObjId "+source.ObjectId.OldIdPtr.ToInt64()+">%)."+DraftingPlan.Address(c.Row,c.Column),StringComparison.Ordinal);
        }
        return expression;
    }
    private static Table Table(Cell cell,Database db,Transaction tr)
    {
        var table=tr.GetObject(NativeTables.Resolve(db,cell.Table),OpenMode.ForRead) as Table??throw new CadFault("BROKEN_TABLE_REFERENCE",cell.Table);
        if(cell.Row>=table.Rows.Count||cell.Column>=table.Columns.Count)throw new CadFault("BROKEN_TABLE_REFERENCE","Manual structural edit invalidated "+cell.Table+":"+DraftingPlan.Address(cell.Row,cell.Column));
        return table;
    }
    internal static object Inspect(Database db,Transaction tr)=>Load(db,tr);
}
