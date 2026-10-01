using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

internal static class NativeTables
{
    public static Table Create(Database db,Transaction tr,JsonElement op)
    {
        var table=new Table { TableStyle=Style(db,tr,op.Text("style")), Position=P(op.GetProperty("position")) };
        try
        {
            int rows=op.GetProperty("rows").GetInt32(),columns=op.GetProperty("columns").GetInt32();
            table.SetSize(rows,columns);
            table.Cells.TextHeight=DraftingPlan.Positive(op,"text_height",2.5);
            table.Cells.TextStyleId=db.Textstyle;
            table.Cells.Alignment=CellAlignment.MiddleCenter;
            table.Cells.DataFormat="%lu2%pr3";
            table.Cells.Borders.Top.Margin=.5;table.Cells.Borders.Bottom.Margin=.5;
            table.Cells.Borders.Left.Margin=.8;table.Cells.Borders.Right.Margin=.8;
            // Default table styles merge the title row. A generic matrix must start unmerged.
            for(int r=0;r<rows;r++)for(int c=0;c<columns;c++)if(table.Cells[r,c].IsMerged==true)table.UnmergeCells(table.Cells[r,c].GetMergeRange());
            for(int c=0;c<columns;c++)table.Columns[c].Width=op.TryGetProperty("column_widths",out var widths)?widths[c].GetDouble():DraftingPlan.Positive(op,"column_width",30);
            for(int r=0;r<rows;r++)table.Rows[r].Height=op.TryGetProperty("row_heights",out var heights)?heights[r].GetDouble():DraftingPlan.Positive(op,"row_height",8);
            return table;
        }
        catch {table.Dispose();throw;}
    }
    private static ObjectId Style(Database db,Transaction tr,string? name)
    {
        if(name is null)return db.Tablestyle;
        var dictionary=(DBDictionary)tr.GetObject(db.TableStyleDictionaryId,OpenMode.ForRead);
        return dictionary.Contains(name)?dictionary.GetAt(name):throw new CadFault("TABLE_STYLE_NOT_FOUND",name);
    }
    private static Point3d P(JsonElement value){var p=EditPlan.Point(value);return new(p[0],p[1],p[2]);}
    public static void Populate(Table table,Database db,Transaction tr,JsonElement op,Dictionary<string,ObjectId> aliases)
    {
        if(op.TryGetProperty("cells",out var cells))SetCells(table,db,tr,cells,aliases);
        if(op.TryGetProperty("merges",out var merges))foreach(var range in merges.EnumerateArray())Merge(table,range[0].GetInt32(),range[1].GetInt32(),range[2].GetInt32(),range[3].GetInt32(),false);
        table.GenerateLayout();table.RecomputeTableBlock(true);
    }
    public static void SetCells(Table table,Database db,Transaction tr,JsonElement cells,Dictionary<string,ObjectId> aliases)
    {
        var mergedRanges=MergedRanges(table);
        foreach(var item in cells.EnumerateArray())
        {
            var (r,c)=DraftingPlan.Address(item.Text("cell")!);Bounds(table,r,c);
            var range=mergedRanges.FirstOrDefault(m=>r>=m.TopRow&&r<=m.BottomRow&&c>=m.LeftColumn&&c<=m.RightColumn);
            if(range is not null&&(range.TopRow!=r||range.LeftColumn!=c))throw new CadFault("MERGED_CELL","Edit the top-left cell of a merged range");
            var cell=table.Cells[r,c];
            cell.FieldId=ObjectId.Null;
            if(item.TryGetProperty("value",out var value))
            {
                cell.Value=value.ValueKind switch{JsonValueKind.Number=>value.GetDouble(),JsonValueKind.Null=>"",_=>value.GetString()!};
                if(value.ValueKind==JsonValueKind.Number)cell.DataFormat=value.TryGetInt64(out _)?"%lu2%pr0":"%lu2%pr3";
            }
            else
            {
                string formula=item.Text("formula")!;
                if(!item.TryGetProperty("references",out var refs)||refs.GetArrayLength()==0)cell.Contents[0].Formula=formula;
                else
                {
                    foreach(var reference in refs.EnumerateArray())
                    {
                        ObjectId sourceId=reference.Text("table_target") is {} target ? aliases[target] : Resolve(db,reference.Text("table_handle")!);
                        if(tr.GetObject(sourceId,OpenMode.ForRead) is not Table source)throw new CadFault("INVALID_TABLE_REFERENCE","Reference must identify a native Table");
                        var (sr,sc)=DraftingPlan.Address(reference.Text("cell")!);Bounds(source,sr,sc);
                        if(sourceId==table.ObjectId&&sr==r&&sc==c)throw new CadFault("CYCLIC_FORMULA","A cell cannot reference itself");
                        string native="Table(%<\\_ObjId "+sourceId.OldIdPtr.ToInt64().ToString(CultureInfo.InvariantCulture)+">%)."+DraftingPlan.Address(sr,sc);
                        formula=formula.Replace("{{"+reference.Text("name")+"}}",native,StringComparison.Ordinal);
                    }
                    int precision=DraftingPlan.Integer(item,"precision",0,8,3);
                    var field=new Field("%<\\AcExpr ("+formula[1..]+") \\f \"%lu2%pr"+precision+"\">%");
                    db.AddDBObject(field);tr.AddNewlyCreatedDBObject(field,true);
                    field.EvaluationOption=FieldEvaluationOptions.Automatic;
                    cell.FieldId=field.ObjectId;
                    field.Evaluate((int)FieldEvaluationContext.Demand,db);
                    if(field.Value is null || field.GetStringValue().Contains("####",StringComparison.Ordinal))throw new CadFault("FORMULA_EVALUATION_FAILED","Native formula could not be evaluated; no changes committed");
                }
            }
            if(item.Text("alignment") is {} alignment)cell.Alignment=alignment switch{"left"=>CellAlignment.MiddleLeft,"right"=>CellAlignment.MiddleRight,_=>CellAlignment.MiddleCenter};
            if(item.TryGetProperty("precision",out var precisionValue))cell.DataFormat="%lu2%pr"+precisionValue.GetInt32();
        }
        table.GenerateLayout();table.RecomputeTableBlock(true);
    }
    public static void Recalculate(Table table,Database db,Transaction tr)
    {
        for(int r=0;r<table.Rows.Count;r++)for(int c=0;c<table.Columns.Count;c++)
        {
            var cell=table.Cells[r,c];
            // Local formulas have AutoCAD-managed internal fields. Evaluating those as
            // standalone AcExpr fields destroys their formula after structural edits.
            if(cell.Contents.Count>0&&cell.Contents[0].HasFormula)continue;
            var id=cell.FieldId;
            if(!id.IsNull&&tr.GetObject(id,OpenMode.ForWrite) is Field field && field.GetFieldCode(FieldCodeFlags.AddMarkers).Contains("\\AcExpr",StringComparison.Ordinal))
                field.Evaluate((int)FieldEvaluationContext.Demand,db);
        }
        table.GenerateLayout();table.RecomputeTableBlock(true);
    }
    public static void Merge(Table table,int firstRow,int firstCol,int lastRow,int lastCol,bool unmerge)
    {
        Bounds(table,firstRow,firstCol);Bounds(table,lastRow,lastCol);
        if(lastRow<firstRow||lastCol<firstCol)throw new CadFault("INVALID_CELL_RANGE","Last cell must follow first cell");
        var range=CellRange.Create(table,firstRow,firstCol,lastRow,lastCol);
        if(unmerge)table.UnmergeCells(range);else table.MergeCells(range);
    }
    public static void Resize(Table table,JsonElement op)
    {
        bool rows=op.Text("op")=="table_rows",insert=op.Text("action")=="insert";
        int index=op.GetProperty("index").GetInt32(),count=op.GetProperty("count").GetInt32(),existing=rows?table.Rows.Count:table.Columns.Count;
        if(index>existing||(!insert&&(index+count>existing||count==existing)))throw new CadFault("INVALID_CELL_RANGE","Cannot delete all rows/columns or resize outside the table");
        int nr=table.Rows.Count+(rows?(insert?count:-count):0),nc=table.Columns.Count+(!rows?(insert?count:-count):0);
        if(nr>500||nc>50||nr*nc>5000)throw new CadFault("TABLE_TOO_LARGE","Table exceeds bounded dimensions");
        double textHeight=table.Cells[Math.Min(index,table.Rows.Count-1),0].TextHeight??2.5;
        var textStyle=table.Cells[Math.Min(index,table.Rows.Count-1),0].TextStyleId;
        if(rows){if(insert)table.InsertRows(index,DraftingPlan.Positive(op,"height",8),count);else table.DeleteRows(index,count);}
        else{if(insert)table.InsertColumns(index,DraftingPlan.Positive(op,"width",30),count);else table.DeleteColumns(index,count);}
        if(insert)
        {
            var added=rows?CellRange.Create(table,index,0,index+count-1,nc-1):CellRange.Create(table,0,index,nr-1,index+count-1);
            added.TextHeight=textHeight;added.TextStyleId=textStyle;added.Alignment=CellAlignment.MiddleCenter;
            if(rows)for(int r=index;r<index+count;r++)table.Rows[r].Height=DraftingPlan.Positive(op,"height",8);
            else for(int c=index;c<index+count;c++)table.Columns[c].Width=DraftingPlan.Positive(op,"width",30);
        }
        table.GenerateLayout();table.RecomputeTableBlock(true);
    }
    public static Table Template(Database db,Transaction tr,JsonElement op)
    {
        var template=SpdsTemplates.Find(op.Text("template")!);double scale=DraftingPlan.Positive(op,"scale",1);
        var data=op.GetProperty("data");int title=op.Text("title") is null?0:1;
        var table=Create(db,tr,Wire.Element(new {position=op.GetProperty("position"),rows=data.GetArrayLength()+title+1,columns=template.Widths.Length,
            column_widths=template.Widths.Select(w=>w*scale),row_height=template.RowHeight*scale,text_height=2.5*scale,style=op.Text("style")}));
        try
        {
            if(title==1){Merge(table,0,0,0,template.Widths.Length-1,false);table.Cells[0,0].TextString=op.Text("title");table.Rows[0].Height=10*scale;}
            table.Rows[title].Height=template.HeaderHeight*scale;
            for(int c=0;c<template.Headers.Length;c++)table.Cells[title,c].TextString=template.Headers[c]=="Примечание"&&template.Widths[c]<=20?"{\\W0.8;Примечание}":template.Headers[c].Replace("\n","\\P");
            int r=title+1;
            foreach(var row in data.EnumerateArray()){int c=0;foreach(var value in row.EnumerateArray()){table.Cells[r,c].Value=value.ValueKind switch{JsonValueKind.Number=>value.GetDouble(),JsonValueKind.Null=>"",_=>value.GetString()!};
                if(value.ValueKind==JsonValueKind.Number)table.Cells[r,c].DataFormat=value.TryGetInt64(out _)?"%lu2%pr0":"%lu2%pr3";
                if(c is 1 or 2)table.Cells[r,c].Alignment=CellAlignment.MiddleLeft;c++;}r++;}
            return table;
        }
        catch{table.Dispose();throw;}
    }
    public static ObjectId Resolve(Database db,string handle)
    {if(!long.TryParse(handle,NumberStyles.HexNumber,null,out long h)||!db.TryGetObjectId(new Handle(h),out var id)||id.IsErased)throw new CadFault("TABLE_NOT_FOUND",handle);return id;}
    private static void Bounds(Table table,int row,int column)
    {if(row<0||row>=table.Rows.Count||column<0||column>=table.Columns.Count)throw new CadFault("CELL_OUT_OF_RANGE",DraftingPlan.Address(row,column));}
    public static object Read(Table table,Transaction tr,int firstRow=0,int firstColumn=0,int rowCount=20,int columnCount=20)
    {
        if(firstRow<0||firstColumn<0||rowCount is <1 or >100||columnCount is <1 or >50||rowCount*columnCount>500)throw new CadFault("INVALID_TABLE_RANGE","Read at most 500 cells; paginate by row/column");
        int endRow=Math.Min(table.Rows.Count,firstRow+rowCount),endCol=Math.Min(table.Columns.Count,firstColumn+columnCount);
        var cells=new List<object>();var mergedRanges=MergedRanges(table);
        for(int r=firstRow;r<endRow;r++)for(int c=firstColumn;c<endCol;c++)
        {
            var cell=table.Cells[r,c];var fieldId=cell.FieldId;string? code=null;object? value=cell.Value;
            if(!fieldId.IsNull&&tr.GetObject(fieldId,OpenMode.ForRead) is Field field)code=field.GetFieldCode(FieldCodeFlags.AddMarkers);
            string? formula=cell.Contents.Count>0&&cell.Contents[0].HasFormula?cell.Contents[0].Formula:null;
            if(formula is not null&&!formula.StartsWith('='))formula="="+formula;
            var merged=mergedRanges.FirstOrDefault(m=>r>=m.TopRow&&r<=m.BottomRow&&c>=m.LeftColumn&&c<=m.RightColumn);
            cells.Add(new {cell=DraftingPlan.Address(r,c),row=r,column=c,text=cell.TextString,value,
                formula,field_code=code,
                alignment=cell.Alignment.ToString(),text_height=cell.TextHeight,merged=merged is null?null:new[]{merged.TopRow,merged.LeftColumn,merged.BottomRow,merged.RightColumn}});
        }
        var owner=tr.GetObject(table.OwnerId,OpenMode.ForRead) as BlockTableRecord;
        string? layout=owner?.IsLayout==true?((Layout)tr.GetObject(owner.LayoutId,OpenMode.ForRead)).LayoutName:null;
        return new {handle=table.Handle.ToString(),layout,rows=table.Rows.Count,columns=table.Columns.Count,
            position=new[]{table.Position.X,table.Position.Y,table.Position.Z},width=table.Width,height=table.Height,
            column_widths=table.Columns.Select(c=>c.Width).ToArray(),row_heights=table.Rows.Select(r=>r.Height).ToArray(),
            table_style=table.TableStyleName,cells,next_row=endRow<table.Rows.Count?(int?)endRow:null,source="native_AutoCAD_Table"};
    }
    private static CellRange[] MergedRanges(Table table)
    {
        var ranges=new List<CellRange>();
        for(int r=0;r<table.Rows.Count;r++)for(int c=0;c<table.Columns.Count;c++)
            if(table.Cells[r,c].IsMerged==true)ranges.Add(table.Cells[r,c].GetMergeRange());
        return ranges.ToArray();
    }
}
