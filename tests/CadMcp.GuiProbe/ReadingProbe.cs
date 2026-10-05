using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadMcp.AutoCAD;
using CadMcp.Core;
using App=Autodesk.AutoCAD.ApplicationServices.Core.Application;
[assembly:CommandClass(typeof(CadMcp.GuiProbe.ReadingProbe))]
namespace CadMcp.GuiProbe;

// External config supplies the private benchmark paths. No source DWG is opened or saved here.
public static class ReadingProbe
{
    private static Documents? documents;
    private static CadMcp.AutoCAD.Dispatcher? dispatcher;
    private static PipeServer? server;
    private static Document? fixture;
    [CommandMethod("CADMCPREADBENCH011O",CommandFlags.Session)]
    public static async void Run()
    {
        if(documents is not null)return;
        var folder=Path.GetDirectoryName(typeof(ReadingProbe).Assembly.Location)!;
        using var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"probe-config.json")));
        string root=config.RootElement.GetProperty("output").GetString()!;Directory.CreateDirectory(root);
        PreviewRendering.DiagnosticTrace=phase=>File.AppendAllText(Path.Combine(root,"render-phases.txt"),DateTime.UtcNow.ToString("O")+" "+phase+Environment.NewLine);
        var original=App.DocumentManager.MdiActiveDocument;int originalDbmod=Convert.ToInt32(App.GetSystemVariable("DBMOD"));
        var checks=new List<string>();string? failure=null;
        void Check(bool ok,string text){if(!ok)throw new InvalidOperationException(text);checks.Add(text);File.WriteAllText(Path.Combine(root,"phase.txt"),text);}
        try
        {
            string path=Path.Combine(root,"reading-fixture-"+Guid.NewGuid().ToString("N")+".dwg");
            using(var db=new Database(true,true))
            {
                db.Insunits=UnitsValue.Millimeters;
                using(var tr=db.TransactionManager.StartTransaction())
                {
                    var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);
                    var model=(BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
                    var layouts=(DBDictionary)tr.GetObject(db.LayoutDictionaryId,OpenMode.ForRead);
                    var paper=layouts.Cast<System.Collections.DictionaryEntry>().Select(e=>(Layout)tr.GetObject((ObjectId)e.Value!,OpenMode.ForWrite)).First(l=>!l.ModelType);
                    paper.LayoutName="Reading sheet";
                    var ps=(BlockTableRecord)tr.GetObject(paper.BlockTableRecordId,OpenMode.ForWrite);
                    void Add(BlockTableRecord owner,Entity entity){owner.AppendEntity(entity);tr.AddNewlyCreatedDBObject(entity,true);}
                    Add(model,new Line(new(1000,2000,0),new(5200,2000,0)));
                    Add(model,new Line(new(1000,2000,0),new(1000,2600,0)));
                    Add(model,new Line(new(5200,2000,0),new(5200,2600,0)));
                    Add(model,new Line(new(1000,2600,0),new(5200,2600,0)));
                    var solid=new Solid3d();solid.CreateBox(400,400,400);solid.TransformBy(Matrix3d.Displacement(new Vector3d(3000,2300,200)));Add(model,solid);
                    for(int i=0;i<3;i++)
                    {
                        var dim=new RotatedDimension(0,new(0,i*10,0),new(4.2,i*10,0),new(0,i*10+5,0),"",db.Dimstyle);
                        dim.SetDatabaseDefaults(db);dim.Dimdec=2;dim.Dimzin=0;dim.Dimlfac=i==0?1000:i==1?-1000:1;
                        if(i==2)dim.DimensionText="length=<>";
                        Add(model,dim);dim.RecomputeDimensionBlock(true);
                    }
                    var pd=new RotatedDimension(0,new(0,0,0),new(4.2,0,0),new(0,5,0),"",db.Dimstyle);
                    pd.SetDatabaseDefaults(db);pd.Dimdec=2;pd.Dimzin=0;pd.Dimlfac=-1000;Add(ps,pd);pd.RecomputeDimensionBlock(true);
                    Add(ps,new MText{Location=new(10,105,0),TextHeight=4,Contents="Offscreen sheet: 4200 model rectangle"});
                    var vp=new Viewport{CenterPoint=new(110,55,0),Width=200,Height=90};Add(ps,vp);
                    vp.ViewDirection=Vector3d.ZAxis;vp.ViewTarget=Point3d.Origin;vp.ViewCenter=new(3100,2300);vp.ViewHeight=2400;vp.On=true;vp.Locked=true;
                    tr.Commit();
                }
                db.SaveAs(path,DwgVersion.Current);
            }
            fixture=DocumentCollectionExtension.Open(App.DocumentManager,path,false);
            App.DocumentManager.MdiActiveDocument=original;
            documents=new Documents();foreach(Document d in App.DocumentManager)documents.Register(d).TablesDirty=false;
            dispatcher=new(documents);dispatcher.Start();
            string pipe="cadmcp-reading-"+Guid.NewGuid().ToString("N");
            server=new PipeServer(pipe,(r,ct)=>r.Operation=="broker_ping"?Task.FromResult(new Response(r.RequestId,"completed",new{ready=true})):dispatcher.Enqueue(r,ct));server.Start();
            File.WriteAllText(Path.Combine(root,"connection.json"),JsonSerializer.Serialize(new{pipe,session=documents.SessionId,documents=documents.Catalog()},Wire.Json));
            async Task<Response> Call(Document doc,string op,object data)
            {
                var result=await dispatcher!.Enqueue(new(Guid.NewGuid().ToString("N"),op,documents!.SessionId,documents.Register(doc).Id,Data:Wire.Element(data),OwnerId:"reading-benchmark"),CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(45));
                File.WriteAllText(Path.Combine(root,checks.Count+"-"+op+".json"),JsonSerializer.Serialize(result,Wire.Json));
                if(result.Error is not null)throw new InvalidOperationException(op+": "+JsonSerializer.Serialize(result.Error));return result;
            }
            JsonElement Result(Response response){var d=Wire.Element(response.Data!);return d.TryGetProperty("result",out var r)?r:d;}
            var context=await Call(fixture,"cad_context",new{});
            Check(!Wire.Element(context.Data!).GetProperty("active").GetBoolean(),"Inactive document context succeeds without an editor view");
            Check(ReferenceEquals(App.DocumentManager.MdiActiveDocument,original),"Context keeps the previously active drawing active");
            var search=Result(await Call(fixture,"cad_search",new{options_json="{\"scope\":\"model\",\"type\":\"RotatedDimension\",\"details\":true}"}));
            var dims=search.GetProperty("entities").EnumerateArray().ToArray();
            Check(dims.Length==3,"Model dimension search returns the three fixture dimensions");
            Check(Math.Abs(dims[0].GetProperty("geometric_measurement").GetDouble()-4.2)<1e-6&&Math.Abs(dims[0].GetProperty("scaled_measurement").GetDouble()-4200)<1e-6&&dims[0].Text("formatted_text")!.Contains("4200"),"Native DIMLFAC 1000 formats geometric 4.2 as 4200 without double scaling");
            Check(Math.Abs(dims[1].GetProperty("scaled_measurement").GetDouble()-4.2)<1e-6&&dims[1].Text("formatted_text")!.Contains("4.20"),"Negative DIMLFAC does not scale a model dimension");
            Check(dims[2].Text("formatted_text")!.Contains("length="),"Native formatting preserves explicit dimension text overrides");
            var sheet=Result(await Call(fixture,"cad_search",new{options_json="{\"scope\":\"layout\",\"layout_name\":\"Reading sheet\",\"details\":true}"}));
            var sheetEntities=sheet.GetProperty("entities").EnumerateArray().ToArray();
            Check(sheetEntities.All(e=>e.Text("layout_name")=="Reading sheet"),"Named layout search reads only the requested paper space");
            Check(sheetEntities.Count(e=>e.Text("type")=="Viewport"&&e.GetProperty("is_paper_overall").GetBoolean())==1,"Inactive paper space has exactly one explicitly marked overall viewport");
            var paperDim=sheetEntities.Single(e=>e.Text("type")=="RotatedDimension");
            Check(paperDim.Text("formatted_text")!.Contains("4200"),"Negative DIMLFAC applies on paper dimensions");
            var viewport=sheetEntities.Single(e=>e.Text("type")=="Viewport"&&Math.Abs(e.GetProperty("paper_width").GetDouble()-200)<1e-6);
            var vpRead=Result(await Call(fixture,"cad_entity_get",new{handle=viewport.Text("handle")}));
            // entity_get wraps the entity in 'entity', search returns flat records.
            if(vpRead.TryGetProperty("entity",out var wrapped))vpRead=wrapped;
            Check(vpRead.Text("layout_name")=="Reading sheet","Entity get reads a viewport outside the current space");
            var center=viewport.GetProperty("model_view_center_wcs").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
            Check(Math.Abs(center[0]-3100)<1e-6&&Math.Abs(center[1]-2300)<1e-6,"Viewport center uses DCS center rather than ViewTarget");
            var modelRegion=Result(await Call(fixture,"cad_search",new{options_json=JsonSerializer.Serialize(new{scope="viewport",viewport_handle=viewport.Text("handle"),details=true})}));
            Check(modelRegion.GetProperty("entities").EnumerateArray().Count(e=>e.Text("type")=="Line")==4,"Viewport search reads the four model rectangle edges");
            App.DocumentManager.MdiActiveDocument=fixture;int beforeDbmod=Convert.ToInt32(App.GetSystemVariable("DBMOD"));
            double beforeWidth;using(var v=fixture.Editor.GetCurrentView())beforeWidth=v.Width;
            App.DocumentManager.MdiActiveDocument=original;
            foreach(var item in new[]{("Reading sheet","top"),("Model","top"),("Model","isometric"),("Model","front"),("Model","back"),("Model","left"),("Model","right")})
            {
                var rendered=Wire.Element((await Call(fixture,"cad_render",new{width=1024,height=768,layout_name=item.Item1,view_name=item.Item2})).Data!);
                File.WriteAllBytes(Path.Combine(root,"fixture-"+item.Item1+"-"+item.Item2+".png"),Convert.FromBase64String(rendered.Text("image_base64")!));
                Check(rendered.Text("image_base64")!.Length>100,"Offscreen native preview succeeds: "+item.Item1+" "+item.Item2);
                if(item.Item1=="Reading sheet")Check(rendered.GetProperty("view").GetProperty("viewports").GetArrayLength()==1,"Sheet composition renders its model viewport without the overall paper view");
            }
            App.DocumentManager.MdiActiveDocument=fixture;
            File.WriteAllText(Path.Combine(root,"dbmod.json"),JsonSerializer.Serialize(new{before=beforeDbmod,after=Convert.ToInt32(App.GetSystemVariable("DBMOD"))}));
            Check(Convert.ToInt32(App.GetSystemVariable("DBMOD"))==beforeDbmod,"Offscreen previews preserve fixture DBMOD");
            using(var v=fixture.Editor.GetCurrentView())Check(Math.Abs(v.Width-beforeWidth)<1e-9,"Offscreen previews preserve the live view width");
            App.DocumentManager.MdiActiveDocument=original;
            foreach(var value in config.RootElement.GetProperty("drawings").EnumerateArray())
            {
                string name=value.GetString()!;var doc=App.DocumentManager.Cast<Document>().Single(d=>string.Equals(d.Name,name,StringComparison.OrdinalIgnoreCase));
                var read=await Call(doc,"cad_context",new{});Check(read.Error is null,"Real inactive benchmark DWG context reads: "+Path.GetFileName(name));
                var catalog=await Call(doc,"cad_catalog",new{});
                File.WriteAllText(Path.Combine(root,"catalog-"+Array.IndexOf(config.RootElement.GetProperty("drawings").EnumerateArray().Select(x=>x.GetString()).ToArray(),name)+".json"),JsonSerializer.Serialize(catalog,Wire.Json));
            }
            Check(ReferenceEquals(App.DocumentManager.MdiActiveDocument,original)&&Convert.ToInt32(App.GetSystemVariable("DBMOD"))==originalDbmod,"The previously active drawing and DBMOD remain unchanged");
        }
        catch(System.Exception error){failure=error.ToString();}
        finally
        {
            if(original is not null&&!original.IsDisposed)App.DocumentManager.MdiActiveDocument=original;
            File.WriteAllText(Path.Combine(root,"native-reading-checks.json"),JsonSerializer.Serialize(new{checks,failure,original_dbmod=originalDbmod},Wire.Json));
            original?.Editor.WriteMessage("\nCAD MCP reading check: "+root+"\n");
        }
    }
    [CommandMethod("CADMCPREADBENCHSTOP011O",CommandFlags.Session)]
    public static void Stop(){server?.Dispose();dispatcher?.Dispose();documents?.Dispose();server=null;dispatcher=null;documents=null;if(fixture is not null&&!fixture.IsDisposed){fixture.CloseAndDiscard();fixture=null;}}
}




