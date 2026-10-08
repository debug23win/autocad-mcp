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
        // A check that cannot run must be reported as unverified, never silently counted as passed.
        var texts=new List<(Entity Text,Extents3d Box)>();
        foreach(var text in entities.Where(e=>e is DBText or MText))
            try{texts.Add((text,text.GeometricExtents));}catch(System.Exception e){Add("TEXT_EXTENTS_UNVERIFIED","unverified","Text overlap not checked: "+e.Message,text);}
        for(int i=0;i<texts.Count;i++)for(int j=i+1;j<texts.Count;j++)
            if(texts[i].Text.OwnerId==texts[j].Text.OwnerId&&Overlap(texts[i].Box,texts[j].Box,false))Add("TEXT_OVERLAP","warning","Annotation bounding boxes overlap; inspect actual glyphs",texts[i].Text,texts[j].Text);
        foreach(var dimension in entities.OfType<Dimension>())
        {
            ct.ThrowIfCancellationRequested();
            try{foreach(var (code,severity,message) in DimensionChecks.Check(dimension,tr).ToArray())Add(code,severity,message,dimension);}
            catch(System.Exception e) when (e is not OperationCanceledException){Add("DIMENSION_CHECK_UNVERIFIED","unverified","Dimension text not checked: "+e.Message,dimension);}
        }
        // These checks describe the result; a failure inside one is reported, never allowed to undo the edit under review.
        try{GlyphCoverage(db,tr,entities,issues);}
        catch(System.Exception e) when (e is not OperationCanceledException){issues.Add(new("TEXT_GLYPHS_UNVERIFIED","unverified",[],"Fonts not checked: "+e.Message));}
        try{TopologyLite(entities,issues,ct);}
        catch(System.Exception e) when (e is not OperationCanceledException){issues.Add(new("TOPOLOGY_UNVERIFIED","unverified",[],"Curve geometry not checked: "+e.Message));}
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
                    if(w>0&&h>0)try{var ext=entity.GeometricExtents;if(ext.MinPoint.X<-.01||ext.MinPoint.Y<-.01||ext.MaxPoint.X>w+.01||ext.MaxPoint.Y>h+.01)Add("OUTSIDE_PAPER","warning","Entity exceeds configured paper boundary (paper millimetres)",entity);}
                    catch(System.Exception e){Add("PAPER_CHECK_UNVERIFIED","unverified","Paper boundary not checked: "+e.Message,entity);}
                    else Add("PAPER_UNCONFIGURED","unverified","No plot media configured",entity);
                }
            }
        }
        // Notes (severity info) describe the drawing without requiring action.
        return new(issues.Any(i=>i.Severity=="unverified")?"unverified":issues.Any(i=>i.Severity=="warning")?"review_required":"passed",entities.Length,pairs,issues,
            ["Bounding boxes are conservative for rotated text", "Intentional structural intersections require reviewer judgement", "Only the explicitly selected scope is checked; normative structural design is not verified",
             "Glyph coverage is checked for regular and Unicode SHX and TrueType fonts; big fonts and inline MText font changes are not checked"]);
    }
    // Characters the style's font cannot draw are shown as "?" on the sheet although the stored text is correct.
    private static void GlyphCoverage(Database db,Transaction tr,IReadOnlyList<Entity> entities,List<Issue> issues)
    {
        var byStyle=new Dictionary<ObjectId,(HashSet<char> Characters,List<string> Handles)>();
        void Collect(ObjectId style,string text,Entity owner)
        {
            if(style.IsNull||text.Length==0)return;
            if(!byStyle.TryGetValue(style,out var entry))byStyle[style]=entry=(new(),new());
            entry.Characters.UnionWith(text);
            string handle=owner.Handle.ToString();
            if(entry.Handles.Count<20&&!entry.Handles.Contains(handle))entry.Handles.Add(handle);
        }
        foreach(var entity in entities)
        {
            if(FontCoverage.TextOf(entity) is {} text)Collect(text.Style,text.Text,entity);
            else if(entity is BlockReference reference)
                foreach(ObjectId id in reference.AttributeCollection)
                    if(!id.IsErased&&tr.GetObject(id,OpenMode.ForRead) is AttributeReference{Invisible:false} attribute)
                        Collect(attribute.TextStyleId,CadText.Normalize(attribute.TextString,attribute.IsMTextAttribute),entity);
        }
        if(byStyle.Count==0)return;
        int codePage=FontCoverage.DrawingCodePage();
        foreach(var (styleId,(characters,handles)) in byStyle)
        {
            if(tr.GetObject(styleId,OpenMode.ForRead) is not TextStyleTableRecord style)continue;
            IReadOnlyList<char>? missing;
            try{missing=FontCoverage.Missing(db,style,new string(characters.ToArray()),codePage);}
            catch(System.Exception e) when (e is IOException or UnauthorizedAccessException or Autodesk.AutoCAD.Runtime.Exception)
            {issues.Add(new("TEXT_GLYPHS_UNVERIFIED","unverified",handles.ToArray(),"Font of text style \""+style.Name+"\" not checked: "+e.Message));continue;}
            if(missing is null||missing.Count==0)continue;
            bool shx=string.Equals(Path.GetExtension(FontCoverage.Resolve(db,style.FileName)),".shx",StringComparison.OrdinalIgnoreCase);
            issues.Add(new("TEXT_GLYPHS_MISSING",shx?"warning":"info",handles.ToArray(),"Font \""+style.FileName+"\" of text style \""+style.Name+"\" has no glyphs for: "+string.Join(" ",missing.Take(40))+
                (shx?". AutoCAD draws them as '?'; use a font that covers them (a Unicode SHX or a TrueType font)":". Windows may substitute another font; check the rendered sheet")));
        }
    }
    // Duplicates, zero-length curves, self-intersections and overlaps among the reviewed curves (no network rules).
    // Only curves lying in a plane parallel to XY are checked, each elevation on its own: a vertical line or a
    // circle standing in the XZ plane is valid 3D geometry, not a zero-length or self-overlapping curve.
    private static void TopologyLite(IReadOnlyList<Entity> entities,List<Issue> issues,CancellationToken ct)
    {
        var curves=entities.Where(CurveSampler.IsCurve).ToArray();
        if(curves.Length==0)return;
        double diagonal=Diagonal(curves),tolerance=Math.Max(1e-9,diagonal*1e-9);
        var levels=new Dictionary<long,List<TopologyCurve>>();
        var unreadable=new List<string>();int spatial=0;
        foreach(var curve in curves)
        {
            ct.ThrowIfCancellationRequested();
            double? elevation;
            try{elevation=Elevation(curve,tolerance);}catch(Autodesk.AutoCAD.Runtime.Exception){unreadable.Add(curve.Handle.ToString());continue;}
            if(elevation is not {} z){spatial++;continue;}
            if(CurveSampler.Sample(curve,Math.Max(tolerance,diagonal*1e-4)) is not {} sample){unreadable.Add(curve.Handle.ToString());continue;}
            long level=(long)Math.Round(z/Math.Max(tolerance*16,1e-6));
            if(!levels.TryGetValue(level,out var list))levels[level]=list=new();
            list.Add(sample);
        }
        if(unreadable.Count>0)issues.Add(new("TOPOLOGY_UNVERIFIED","unverified",unreadable.Take(20).ToArray(),"Geometry of "+unreadable.Count+" curve(s) could not be read; duplicates and self-intersections not checked for them"));
        if(spatial>0)issues.Add(new("TOPOLOGY_3D_SKIPPED","info",[],spatial+" curve(s) not parallel to the XY plane were not checked for duplicates and self-intersections"));
        foreach(var level in levels.Values)
        {
            TopologyReport report;
            try{report=Topology.Analyze(level,new(tolerance,0,Endpoints:false,Crossings:false,MaxFindings:100),ct);}
            catch(CadFault e){issues.Add(new("TOPOLOGY_UNVERIFIED","unverified",[],"Curve geometry not checked: "+e.Message));continue;}
            foreach(var finding in report.Findings)
                issues.Add(finding.Code=="TOPOLOGY_LIMIT"?new("TOPOLOGY_UNVERIFIED","unverified",[],"Curves too dense for the duplicate and self-intersection check; run cad_review with options_json {\"checks\":[\"topology\"]} on parts of them")
                    :new(finding.Code,finding.Severity,finding.Handles,finding.Message));
        }
    }
    /// <summary>
    /// The elevation of a curve lying in a plane parallel to XY, or null. Judged from the geometry, not the extents,
    /// which a thickness stretches along Z although the curve itself is flat.
    /// </summary>
    private static double? Elevation(Entity curve,double tolerance)
    {
        static bool Up(Vector3d normal)=>normal.IsParallelTo(Vector3d.ZAxis);
        // Absolute tolerance, or a relative one far from the origin; never the tolerance scaled by the elevation.
        double Flat(double z)=>Math.Max(Math.Max(tolerance,1e-9),1e-9*Math.Max(1,Math.Abs(z)));
        switch(curve)
        {
            case Line line: return Math.Abs(line.StartPoint.Z-line.EndPoint.Z)<=Flat(line.StartPoint.Z)?line.StartPoint.Z:null;
            case Arc arc: return Up(arc.Normal)?arc.Center.Z:null;
            case Circle circle: return Up(circle.Normal)?circle.Center.Z:null;
            case Ellipse ellipse: return Up(ellipse.Normal)?ellipse.Center.Z:null;
            case Polyline polyline: return Up(polyline.Normal)?polyline.StartPoint.Z:null;
            case Polyline2d polyline: return Up(polyline.Normal)?polyline.StartPoint.Z:null;
            default:
                // 3D polylines and splines have no thickness: their extents show whether they leave the plane.
                var box=curve.GeometricExtents;
                return box.MaxPoint.Z-box.MinPoint.Z<=Flat(box.MaxPoint.Z)?box.MinPoint.Z:null;
        }
    }
    internal static double Diagonal(IEnumerable<Entity> entities)
    {
        double minX=double.MaxValue,minY=double.MaxValue,maxX=double.MinValue,maxY=double.MinValue;
        foreach(var entity in entities)
            try{var box=entity.GeometricExtents;minX=Math.Min(minX,box.MinPoint.X);minY=Math.Min(minY,box.MinPoint.Y);maxX=Math.Max(maxX,box.MaxPoint.X);maxY=Math.Max(maxY,box.MaxPoint.Y);}
            catch(Autodesk.AutoCAD.Runtime.Exception){}
        return maxX<minX?0:Math.Sqrt((maxX-minX)*(maxX-minX)+(maxY-minY)*(maxY-minY));
    }
    private static bool Overlap(Extents3d a,Extents3d b,bool volume)=>a.MaxPoint.X>b.MinPoint.X+1e-7&&b.MaxPoint.X>a.MinPoint.X+1e-7&&a.MaxPoint.Y>b.MinPoint.Y+1e-7&&b.MaxPoint.Y>a.MinPoint.Y+1e-7&&(!volume||a.MaxPoint.Z>b.MinPoint.Z+1e-7&&b.MaxPoint.Z>a.MinPoint.Z+1e-7);
    internal static object Release(Database db,Transaction tr,string[] names)
    {
        var errors=new List<object>();var warnings=new List<object>();var notes=new List<object>();var designation=new List<object>();
        var numbering=new List<(string Layout,string Designation,string? Sheet,string? Sheets)>();
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
            else{var review=Review(db,tr,ids);foreach(var issue in review.Issues)(issue.Severity=="info"?notes:warnings).Add(new{layout=name,issue});}
            var titles=new List<Dictionary<string,string>>();
            foreach(var reference in ids.Select(id=>tr.GetObject(id,OpenMode.ForRead)).OfType<BlockReference>())
            {
                var tags=new Dictionary<string,string>();foreach(ObjectId a in reference.AttributeCollection){var attr=(AttributeReference)tr.GetObject(a,OpenMode.ForRead);tags[attr.Tag]=attr.TextString;}
                if(tags.ContainsKey("DESIGNATION")){titles.Add(tags);designation.Add(new{layout=name,tags});numbering.Add((name,tags["DESIGNATION"],tags.GetValueOrDefault("SHEET"),tags.GetValueOrDefault("SHEETS")));}
            }
            // A field that cannot be evaluated prints as ####.
            foreach(var (handle,text) in SheetTexts(tr,ids))
                if(CadText.HasUnresolvedField(text))warnings.Add(new{code="FIELD_UNRESOLVED",layout=name,handle,text=text.Length>120?text[..120]+"…":text});
            PlotChecks(layout,name,warnings,notes);
            if(titles.Count!=1)warnings.Add(new{code="TITLE_COUNT",layout=name,count=titles.Count});
            foreach(var title in titles)if(string.IsNullOrWhiteSpace(title["DESIGNATION"]))warnings.Add(new{code="DESIGNATION_EMPTY",layout=name});
            if(string.IsNullOrWhiteSpace(layout.PlotConfigurationName)||layout.PlotConfigurationName=="None")errors.Add(new{code="PLOT_DEVICE_MISSING",layout=name});
        }
        var duplicates=designation.Select(Wire.Element).Select(d=>new{layout=d.Text("layout"),designation=d.GetProperty("tags").Text("DESIGNATION"),sheet=d.GetProperty("tags").Text("SHEET")}).GroupBy(d=>(d.designation,d.sheet)).Where(g=>!string.IsNullOrWhiteSpace(g.Key.designation)&&g.Count()>1).Select(g=>new{g.Key,layouts=g.Select(d=>d.layout).ToArray()}).ToArray();
        if(duplicates.Length>0)warnings.Add(new{code="DUPLICATE_SHEET_DESIGNATION",duplicates});
        SheetNumbering(numbering,warnings,notes);
        // Notes describe the set (scales, plot areas, numbering gaps) and do not change the state.
        return new{state=errors.Count>0?"failed":warnings.Count>0?"review_required":"passed",errors,warnings,notes,titles=designation,expected_layouts=names,scope="release preflight; appearance still requires rendered PDF review"};
    }
    private static IEnumerable<(string Handle,string Text)> SheetTexts(Transaction tr,IEnumerable<ObjectId> ids)
    {
        foreach(var id in ids)
            switch(tr.GetObject(id,OpenMode.ForRead))
            {
                case DBText text:yield return (text.Handle.ToString(),text.TextString);break;
                case MText mtext:yield return (mtext.Handle.ToString(),mtext.Text);break;
                case BlockReference reference:
                    foreach(ObjectId a in reference.AttributeCollection)
                        if(tr.GetObject(a,OpenMode.ForRead) is AttributeReference{Invisible:false} attribute)yield return (reference.Handle.ToString(),attribute.TextString);
                    break;
            }
    }
    private static void PlotChecks(Layout layout,string name,List<object> warnings,List<object> notes)
    {
        if(layout.PlotType==PlotType.Display)warnings.Add(new{code="PLOT_AREA_DISPLAY",layout=name,detail="The plot area is the current display, so the result depends on the last view; plot the layout"});
        else if(layout.PlotType!=PlotType.Layout)notes.Add(new{code="PLOT_AREA_NOT_LAYOUT",layout=name,plot_type=layout.PlotType.ToString()});
        if(layout.UseStandardScale&&layout.StdScaleType==StdScaleType.ScaleToFit)
            warnings.Add(new{code="PLOT_SCALE_FIT",layout=name,detail="Scaled to fit: the printed sheet is not at its drawing scale; plot layouts at 1:1"});
        else
        {
            var custom=layout.CustomPrintScale;
            double ratio=layout.UseStandardScale?layout.StdScale:custom.Denominator==0?0:custom.Numerator/custom.Denominator;
            if(ratio>0&&Math.Abs(ratio-1)>1e-6)notes.Add(new{code="PLOT_SCALE_NOT_1_TO_1",layout=name,scale=ratio,paper_units=layout.PlotPaperUnits.ToString()});
        }
    }
    // Sheet numbers (SHEET) and totals (SHEETS, usually only on the first sheet) per designation.
    private static void SheetNumbering(IEnumerable<(string Layout,string Designation,string? Sheet,string? Sheets)> titles,List<object> warnings,List<object> notes)
    {
        static int? Number(string? value)=>int.TryParse(value?.Trim(),System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out int n)&&n>0?n:null;
        foreach(var group in titles.Where(t=>!string.IsNullOrWhiteSpace(t.Designation)).GroupBy(t=>t.Designation.Trim(),StringComparer.OrdinalIgnoreCase))
        {
            var totals=group.Select(t=>Number(t.Sheets)).OfType<int>().Distinct().OrderBy(n=>n).ToArray();
            if(totals.Length>1)warnings.Add(new{code="SHEET_TOTAL_INCONSISTENT",designation=group.Key,totals,layouts=group.Select(t=>t.Layout).ToArray()});
            var numbers=group.Select(t=>(t.Layout,Number:Number(t.Sheet))).Where(t=>t.Number is not null).Select(t=>(t.Layout,Number:t.Number!.Value)).ToArray();
            if(totals.Length>0&&numbers.Where(n=>n.Number>totals[^1]).Select(n=>n.Layout).ToArray() is {Length:>0} beyond)
                warnings.Add(new{code="SHEET_TOTAL_TOO_SMALL",designation=group.Key,total=totals[^1],layouts=beyond});
            var distinct=numbers.Select(n=>n.Number).Distinct().OrderBy(n=>n).ToArray();
            if(distinct.Length>1&&distinct[^1]-distinct[0]<10000&&Enumerable.Range(distinct[0],distinct[^1]-distinct[0]+1).Except(distinct).Take(50).ToArray() is {Length:>0} missing)
                notes.Add(new{code="SHEET_NUMBER_GAP",designation=group.Key,missing,detail="Sheets with these numbers are not among the checked layouts; include them or confirm they are elsewhere"});
        }
    }
}
