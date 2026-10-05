using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using CadMcp.AutoCAD;
using CadMcp.Core;
using App=Autodesk.AutoCAD.ApplicationServices.Core.Application;
[assembly:CommandClass(typeof(CadMcp.GuiProbe.MdiProbe))]
namespace CadMcp.GuiProbe;

public static class MdiProbe
{
    private static bool running;
    [CommandMethod("CADMCPGUISTABILITY",CommandFlags.Session)]
    public static async void Run()
    {
        if(running)return;running=true;
        string root=Path.Combine(Path.GetDirectoryName(typeof(MdiProbe).Assembly.Location)!,"probe-output",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string output=Path.Combine(root,"result.json");var checks=new List<string>();string? failure=null;
        var original=App.DocumentManager.MdiActiveDocument;int originalDbmod=Convert.ToInt32(App.GetSystemVariable("DBMOD"));
        var owned=new List<Document>();
        void Check(bool ok,string text){if(!ok)throw new InvalidOperationException(text);checks.Add(text);File.WriteAllText(Path.Combine(root,"phase.txt"),text);}
        try
        {
            foreach(string name in new[]{"project-A","project-B"})
            {
                string path=Path.Combine(root,name+".dwg");using(var seed=new Database(true,true)){seed.Insunits=UnitsValue.Millimeters;seed.SaveAs(path,DwgVersion.Current);}
                owned.Add(DocumentCollectionExtension.Open(App.DocumentManager,path,false));
            }
            App.DocumentManager.MdiActiveDocument=original;
            using var documents=new Documents();foreach(Document existing in App.DocumentManager)documents.Register(existing).TablesDirty=false;
            using var dispatcher=new CadMcp.AutoCAD.Dispatcher(documents);dispatcher.Start();
            var a=owned[0];var b=owned[1];var sa=documents.Register(a);var sb=documents.Register(b);
            Check(sa.Id!=sb.Id,"MDI documents have independent stable live identities");
            string pipe="cadmcp-gui-probe-"+Guid.NewGuid().ToString("N");using var worker=new PipeServer(pipe,(request,ct)=>request.Operation=="broker_ping"?Task.FromResult(new Response(request.RequestId,"completed",Wire.Element(new{ready=true}))):dispatcher.Enqueue(request,ct));worker.Start();
            var workspace=new ChatWorkspace(pipeName:pipe,historyRoot:Path.Combine(root,"chat")){CadSessionId=documents.SessionId};
            await workspace.Refresh();
            Check(workspace.ProjectPanels.TryGetValue(sa.Id,out var panelA)&&workspace.ProjectPanels.TryGetValue(sb.Id,out var panelB)&&!ReferenceEquals(panelA,panelB),"Real WPF workspace creates distinct panels for the two MDI DWGs");
            for(int n=0;n<20;n++){workspace.SelectProject(n%2==0?sa.Id:sb.Id);App.DocumentManager.MdiActiveDocument=n%2==0?a:b;await workspace.Refresh();}
            Check(workspace.ProjectPanels[sa.Id].CadDocumentId==sa.Id&&workspace.ProjectPanels[sb.Id].CadDocumentId==sb.Id,"Twenty real DWG/chat tab switches preserve the document bindings");workspace.Stop();App.DocumentManager.MdiActiveDocument=original;
            async Task<Response> Submit(Document doc,string owner,string id,string json)
            {
                var state=documents.Register(doc);var request=new Request(Guid.NewGuid().ToString("N"),"cad_edit",documents.SessionId,state.Id,state.Revision,Wire.Element(new{operation_id=id,operations_json=json}),OwnerId:owner);
                return await dispatcher.Enqueue(request,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            }
            for(int n=0;n<30;n++)
            {
                var aa=Submit(a,"chat-A","a"+n,JsonSerializer.Serialize(new[]{new{op="line",start=new[]{n*20,0,0},end=new[]{n*20+10,0,0}}}));
                var bb=Submit(b,"chat-B","b"+n,JsonSerializer.Serialize(new[]{new{op="circle",center=new[]{n*20,0,0},radius=5}}));
                var replies=await Task.WhenAll(aa,bb);
                Check(replies.All(r=>r.Error is null),"Parallel chat requests serialize successfully in MDI cycle "+n);
                Check(replies[0].DocumentId==sa.Id&&replies[1].DocumentId==sb.Id,"MDI result scope remains correct in cycle "+n);
                Check(ReferenceEquals(App.DocumentManager.MdiActiveDocument,original),"Background CAD actions restore the user's active DWG in cycle "+n);
            }
            int Count(Document doc){using var tr=doc.Database.TransactionManager.StartOpenCloseTransaction();return ((BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForRead)).Cast<ObjectId>().Count();}
            Check(Count(a)==30&&Count(b)==30,"Parallel edits remain in their assigned drawings");
            App.DocumentManager.MdiActiveDocument=a;
            await App.DocumentManager.ExecuteInCommandContextAsync(async _=>{
                await a.Editor.CommandAsync("_.UNDO","1");Check(Count(a)==29&&Count(b)==30,"Native Undo affects only one batch in project A");
                await a.Editor.CommandAsync("_.REDO");Check(Count(a)==30,"Native Redo remains available after Undo");
            },null);
            App.DocumentManager.MdiActiveDocument=original;
            // Owned cancellation is tested before a queued native edit has a chance to execute.
            var canceled=Submit(a,"chat-A","cancelled","[{\"op\":\"line\",\"start\":[0,100,0],\"end\":[10,100,0]}]");
            var cancelRequest=new Request(Guid.NewGuid().ToString("N"),"cad_cancel",documents.SessionId,sa.Id,Data:Wire.Element(new{operation_id="cancelled"}),OwnerId:"chat-A");
            await dispatcher.Enqueue(cancelRequest,CancellationToken.None);
            var independent=Submit(b,"chat-B","independent","[{\"op\":\"circle\",\"center\":[0,100,0],\"radius\":5}]");
            Check((await canceled).Error?.Code=="CANCELLED"&&(await independent).Error is null&&Count(a)==30&&Count(b)==31,"Stopping project A preserves independent project B work");
            var tables=await Submit(a,"chat-A","tables","""[{"op":"table_create","id":"s","position":[0,200,0],"rows":3,"columns":3,"cells":[{"cell":"A1","value":4},{"cell":"B1","value":5},{"cell":"C1","formula":"=A1*B1"}]},{"op":"table_create","id":"d","position":[120,200,0],"rows":2,"columns":2,"cells":[{"cell":"A1","formula":"={{v}}*2","references":[{"name":"v","table_target":"s","cell":"C1"}]}]}]""");
            Check(tables.Error is null,"Native tables with inter-table formulas create in full AutoCAD");
            var tableData=Wire.Element(tables.Data!);var result=tableData.TryGetProperty("result",out var inner)?inner:tableData;
            string Handle(string alias)=>result.GetProperty("results").EnumerateArray().First(x=>x.Text("id")==alias).Text("handle")!;
            string source=Handle("s"),dependent=Handle("d");
            double Value(string handle){using var tr=a.Database.TransactionManager.StartOpenCloseTransaction();var table=(Table)tr.GetObject(NativeTables.Resolve(a.Database,handle),OpenMode.ForRead);return Convert.ToDouble(table.Cells[0,0].Value);}
            Check(Value(dependent)==40,"Inter-table formula evaluates to 40");
            var update=await Submit(a,"chat-A","table-update",JsonSerializer.Serialize(new[]{new{op="table_cells",handle=source,cells=new[]{new{cell="A1",value=7}}}}));
            Check(update.Error is null&&Value(dependent)==70,"Editing the source updates linked table to 70 atomically");
            App.DocumentManager.MdiActiveDocument=a;
            await App.DocumentManager.ExecuteInCommandContextAsync(async _=>{await a.Editor.CommandAsync("_.UNDO","1");Check(Value(dependent)==40,"Native Undo restores linked table value");},null);
            await Task.Delay(700);Check(!documents.Register(a).TablesDirty,"Undo does not schedule an automatic write that destroys Redo");
            await App.DocumentManager.ExecuteInCommandContextAsync(async _=>{await a.Editor.CommandAsync("_.REDO");Check(Value(dependent)==70,"Linked tables survive idle and native Redo");},null);
            App.DocumentManager.MdiActiveDocument=original;
            var sheet=await Submit(b,"chat-B","sheet","""[{"op":"layout_create","name":"Probe A3"},{"op":"layout_configure","name":"Probe A3","device":"DWG To PDF.pc3","media_name":"ISO_full_bleed_A3_(420.00_x_297.00_MM)","paper_units":"millimeters","paper_rotation":0},{"op":"viewport","layout":"Probe A3","center":[210,148.5,0],"width":370,"height":240,"model_center":[300,0,0],"model_height":300,"locked":true}]""");
            Check(sheet.Error is null,"A3 print settings and locked paper-space viewport create in project B");
            var rendered=await dispatcher.Enqueue(new(Guid.NewGuid().ToString("N"),"cad_render",documents.SessionId,sb.Id,Data:Wire.Element(new{width=640,height=480,view_name="top"})),CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            Check(rendered.Error is null&&Wire.Element(rendered.Data!).Text("image_base64")?.Length>100,"Actual raster preview renders in full AutoCAD");
            string pdf=Path.Combine(root,"sheet.pdf");
            var exported=await dispatcher.Enqueue(new(Guid.NewGuid().ToString("N"),"cad_export",documents.SessionId,sb.Id,sb.Revision,Wire.Element(new{operation_id="pdf",format="pdf",path=pdf,layout="Probe A3"}),OwnerId:"chat-B"),CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            Check(exported.Error is null&&File.Exists(pdf),"Native PDF plot completes while project A remains open");
            Check(Wire.Element(PdfVerification.Check(pdf)).Number("page_count",0)==1,"Strict parser verifies the exported PDF page");
            foreach(var doc in owned){App.DocumentManager.MdiActiveDocument=doc;await App.DocumentManager.ExecuteInCommandContextAsync(async _=>{await doc.Editor.CommandAsync("_.QSAVE");},null);}
            GC.Collect();GC.WaitForPendingFinalizers();
            Check(File.Exists(a.Name)&&File.Exists(b.Name),"Both test DWGs save after concurrent edits and cancellation");
            App.DocumentManager.MdiActiveDocument=original;
            Check(Convert.ToInt32(App.GetSystemVariable("DBMOD"))==originalDbmod,"The user's original active drawing retains its initial DBMOD");
            string saved=a.Name;a.CloseAndDiscard();owned.Remove(a);var reopened=DocumentCollectionExtension.Open(App.DocumentManager,saved,false);owned.Add(reopened);
            Check(documents.Register(reopened).Id!=sa.Id,"Reopened DWG receives a fresh identity; old requests cannot target it");
            using(var tr=reopened.Database.TransactionManager.StartOpenCloseTransaction()){var content=(BlockTableRecord)tr.GetObject(reopened.Database.CurrentSpaceId,OpenMode.ForRead);Check(content.Cast<ObjectId>().Count()==32,"Saved/reopened test drawing retains all geometry and tables");}
        }
        catch(System.Exception e){failure=e.ToString();}
        finally
        {
            try{if(original is not null&&!original.IsDisposed)App.DocumentManager.MdiActiveDocument=original;}catch(System.Exception e){failure??=e.ToString();}
            File.WriteAllText(output,JsonSerializer.Serialize(new{checks,failure,created_documents=owned.Select(d=>d.Name).ToArray()},Wire.Json));
            original?.Editor.WriteMessage("\nCAD MCP GUI probe: "+output+"\n");running=false;
            // Keep only the probe's own DWGs open for inspection. Never close a user document.
        }
    }
}
