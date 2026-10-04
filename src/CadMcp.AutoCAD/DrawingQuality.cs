using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

internal static class DrawingQuality
{
    internal sealed record Issue(string Code,string Severity,string[] Handles,string Message);
    internal sealed record Report(string State,int Entities,int SolidPairs,IReadOnlyList<Issue> Issues,string[] Limitations);
    internal static Report Review(Database db,Transaction tr,IEnumerable<ObjectId> ids,CancellationToken ct=default)
    {
        var selected=ids.Distinct().Where(id=>!id.IsNull&&!id.IsErased).Take(251).ToArray();
        if(selected.Length>250)throw new CadFault("REVIEW_TOO_LARGE","Review up to 250 entities per scope");
        var entities=selected.Select(id=>tr.GetObject(id,OpenMode.ForRead)).OfType<Entity>().Where(e=>!e.IsErased).ToArray();
        var issues=new List<Issue>();int pairs=0;
        void Add(string code,string severity,string message,params Entity[] affected)=>issues.Add(new(code,severity,affected.Select(e=>e.Handle.ToString()).ToArray(),message));
        var solids=entities.OfType<Solid3d>().ToArray();
        for(int i=0;i<solids.Length;i++)for(int j=i+1;j<solids.Length;j++)
        {
            ct.ThrowIfCancellationRequested();
            if(!Overlap(solids[i].GeometricExtents,solids[j].GeometricExtents,true))continue;
            if(++pairs>2000){issues.Add(new("SOLID_REVIEW_LIMIT","unverified",[],"Split scope to check remaining pairs"));break;}
            try{if(solids[i].CheckInterference(solids[j]))Add("SOLID_INTERFERENCE","warning","Native solid volumes intersect; review whether the joint is intentional",solids[i],solids[j]);}
            catch(System.Exception e){Add("INTERFERENCE_UNVERIFIED","unverified",e.Message,solids[i],solids[j]);}
        }
        var texts=entities.Where(e=>e is DBText or MText).ToArray();
        for(int i=0;i<texts.Length;i++)for(int j=i+1;j<texts.Length;j++)
            try{if(texts[i].OwnerId==texts[j].OwnerId&&Overlap(texts[i].GeometricExtents,texts[j].GeometricExtents,false))Add("TEXT_OVERLAP","warning","Annotation bounding boxes overlap; inspect actual glyphs",texts[i],texts[j]);}catch(System.Exception){ }
        foreach(var entity in entities)
        {
            ct.ThrowIfCancellationRequested();
            if(entity is Table table)
            {
                var merges=NativeTables.MergedRanges(table);
                for(int r=0;r<table.Rows.Count;r++)for(int c=0;c<table.Columns.Count;c++)
                {
                    var cell=table.Cells[r,c];if(string.IsNullOrWhiteSpace(cell.TextString))continue;
                    var range=merges.FirstOrDefault(m=>r>=m.TopRow&&r<=m.BottomRow&&c>=m.LeftColumn&&c<=m.RightColumn);
                    if(range is not null&&(range.TopRow!=r||range.LeftColumn!=c))continue;
                    double width=range is null?table.Columns[c].Width:Enumerable.Range(range.LeftColumn,range.RightColumn-range.LeftColumn+1).Sum(n=>table.Columns[n].Width);
                    double height=range is null?table.Rows[r].Height:Enumerable.Range(range.TopRow,range.BottomRow-range.TopRow+1).Sum(n=>table.Rows[n].Height);
                    using var text=new MText();text.SetDatabaseDefaults(db);text.TextStyleId=cell.TextStyleId??db.Textstyle;
                    text.TextHeight=cell.TextHeight??2.5;text.Width=Math.Max(width-1.6,.01);text.Contents=cell.TextString;
                    try{if(text.ActualHeight>height-1+1e-6||text.ActualWidth>width-1.6+1e-6)Add("TABLE_TEXT_OVERFLOW","warning","Text exceeds usable cell "+DraftingPlan.Address(r,c),table);}
                    catch(System.Exception e){Add("TABLE_FIT_UNVERIFIED","unverified",e.Message,table);}
                }
            }
            var owner=tr.GetObject(entity.OwnerId,OpenMode.ForRead) as BlockTableRecord;
            if(owner?.IsLayout==true)
            {
                var layout=(Layout)tr.GetObject(owner.LayoutId,OpenMode.ForRead);
                if(!layout.ModelType && entity is not Viewport{Number:1})
                {
                    var size=layout.PlotPaperSize;double w=size.X,h=size.Y;
                    if(layout.PlotRotation is PlotRotation.Degrees090 or PlotRotation.Degrees270)(w,h)=(h,w);
                    if(w>0&&h>0)try{var ext=entity.GeometricExtents;if(ext.MinPoint.X<-.01||ext.MinPoint.Y<-.01||ext.MaxPoint.X>w+.01||ext.MaxPoint.Y>h+.01)Add("OUTSIDE_PAPER","warning","Entity exceeds configured paper boundary (paper millimetres)",entity);}catch(System.Exception){ }
                    else Add("PAPER_UNCONFIGURED","unverified","No plot media configured",entity);
                }
            }
        }
        return new(issues.Count==0?"passed":issues.Any(i=>i.Severity=="unverified")?"unverified":"review_required",entities.Length,pairs,issues,
            ["Bounding boxes are conservative for rotated text", "Intentional structural intersections require reviewer judgement", "Only the explicitly selected scope is checked; normative structural design is not verified"]);
    }
    private static bool Overlap(Extents3d a,Extents3d b,bool volume)=>a.MaxPoint.X>b.MinPoint.X+1e-7&&b.MaxPoint.X>a.MinPoint.X+1e-7&&a.MaxPoint.Y>b.MinPoint.Y+1e-7&&b.MaxPoint.Y>a.MinPoint.Y+1e-7&&(!volume||a.MaxPoint.Z>b.MinPoint.Z+1e-7&&b.MaxPoint.Z>a.MinPoint.Z+1e-7);
    internal static object Release(Database db,Transaction tr,string[] names)
    {
        var errors=new List<object>();var warnings=new List<object>();var designation=new List<object>();
        var layouts=(DBDictionary)tr.GetObject(db.LayoutDictionaryId,OpenMode.ForRead);var blocks=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);
        foreach(ObjectId id in blocks)
        {
            var block=(BlockTableRecord)tr.GetObject(id,OpenMode.ForRead);
            if(block.IsFromExternalReference&&block.XrefStatus!=XrefStatus.Resolved)errors.Add(new{code="XREF_UNRESOLVED",name=block.Name,path=block.PathName,status=block.XrefStatus.ToString()});
        }
        var styles=(TextStyleTable)tr.GetObject(db.TextStyleTableId,OpenMode.ForRead);
        foreach(ObjectId id in styles)
        {
            var style=(TextStyleTableRecord)tr.GetObject(id,OpenMode.ForRead);
            foreach(var font in new[]{style.FileName,style.BigFontFileName}.Where(f=>!string.IsNullOrWhiteSpace(f)))
            {
                bool available=File.Exists(font)||File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),Path.GetFileName(font)));
                if(!available)try{available=File.Exists(HostApplicationServices.Current.FindFile(font,db,FindFileHint.FontFile));}catch(System.Exception){ }
                if(!available)warnings.Add(new{code="FONT_UNRESOLVED",style=style.Name,font});
            }
        }
        foreach(string name in names)
        {
            if(!layouts.Contains(name)){errors.Add(new{code="LAYOUT_MISSING",layout=name});continue;}
            var layout=(Layout)tr.GetObject(layouts.GetAt(name),OpenMode.ForRead);
            if(layout.ModelType){errors.Add(new{code="MODEL_IS_NOT_SHEET",layout=name});continue;}
            var space=(BlockTableRecord)tr.GetObject(layout.BlockTableRecordId,OpenMode.ForRead);
            var ids=space.Cast<ObjectId>().Where(id=>!id.IsErased).ToArray();
            if(ids.Length>250)warnings.Add(new{code="SHEET_REVIEW_PAGINATE",layout=name,count=ids.Length});
            else{var review=Review(db,tr,ids);foreach(var issue in review.Issues)warnings.Add(new{layout=name,issue});}
            var titles=new List<Dictionary<string,string>>();
            foreach(var reference in ids.Select(id=>tr.GetObject(id,OpenMode.ForRead)).OfType<BlockReference>())
            {
                var tags=new Dictionary<string,string>();foreach(ObjectId a in reference.AttributeCollection){var attr=(AttributeReference)tr.GetObject(a,OpenMode.ForRead);tags[attr.Tag]=attr.TextString;}
                if(tags.ContainsKey("DESIGNATION")){titles.Add(tags);designation.Add(new{layout=name,tags});}
            }
            if(titles.Count!=1)warnings.Add(new{code="TITLE_COUNT",layout=name,count=titles.Count});
            foreach(var title in titles)if(string.IsNullOrWhiteSpace(title["DESIGNATION"]))warnings.Add(new{code="DESIGNATION_EMPTY",layout=name});
            if(string.IsNullOrWhiteSpace(layout.PlotConfigurationName)||layout.PlotConfigurationName=="None")errors.Add(new{code="PLOT_DEVICE_MISSING",layout=name});
        }
        var duplicates=designation.Select(Wire.Element).Select(d=>new{layout=d.Text("layout"),designation=d.GetProperty("tags").Text("DESIGNATION"),sheet=d.GetProperty("tags").Text("SHEET")}).GroupBy(d=>(d.designation,d.sheet)).Where(g=>!string.IsNullOrWhiteSpace(g.Key.designation)&&g.Count()>1).Select(g=>new{g.Key,layouts=g.Select(d=>d.layout).ToArray()}).ToArray();
        if(duplicates.Length>0)warnings.Add(new{code="DUPLICATE_SHEET_DESIGNATION",duplicates});
        return new{state=errors.Count>0?"failed":warnings.Count>0?"review_required":"passed",errors,warnings,titles=designation,expected_layouts=names,scope="release preflight; appearance still requires rendered PDF review"};
    }
}
