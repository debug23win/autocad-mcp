using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using CadMcp.AutoCAD;
using CadMcp.Core;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CadMcp.CoreProbe.StabilityProbe))]
namespace CadMcp.CoreProbe;
public static class StabilityProbe
{
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    [CommandMethod("CADMCPSTABILITYPROBE")]
    public static void Run()
    {
        var output=Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT");if(output is null)return;
        SetErrorMode(0x0002|0x8000);
        var checks=new List<string>();string? failure=null;
        void Phase(string phase)=>File.WriteAllText(output+".phase",phase);
        try
        {
            if(Environment.GetEnvironmentVariable("CADMCP_STABILITY_SEED")=="1")
            {
                using var seed=new Database(true,true);
                seed.SaveAs(Path.ChangeExtension(output,".dwg"),DwgVersion.Current);
                File.WriteAllText(output,JsonSerializer.Serialize(new{checks=new[]{"Created an isolated empty DWG seed"},failure=(string?)null}));
                return;
            }
            var doc=App.DocumentManager.MdiActiveDocument;doc.Database.Insunits=UnitsValue.Millimeters;
            Phase("Creating eight native SPDS title blocks");
            var operations=new List<object>();
            for(int i=0;i<8;i++)
            {
                string layout="STABILITY_"+i;
                operations.Add(new{op="layout_create",name=layout});
                operations.Add(new{op="spds_sheet",layout,format="A1",designation="TEST-КЖ-"+i,project="Проверка многолистового комплекта",building="Тест",drawing_title="Планы и разрезы",organization="CAD MCP",sheet=(i+1).ToString(),sheets="8"});
            }
            Edits.Execute(doc,EditPlan.Parse(JsonSerializer.Serialize(operations)),CancellationToken.None);
            Phase("Collecting transient native wrappers after title-block creation");
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
            checks.Add("Eight title blocks survive collection of transient native wrappers");
            Phase("Reading catalog and native attribute text after collection");
            object detached;
            using(var tr=doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                detached=Catalog.Read(doc.Database,tr);
                var catalog=Wire.Element(detached);
                if(catalog.GetProperty("layouts").GetArrayLength()<9)throw new System.Exception("Missing sheet layouts");
                checks.Add("Catalog reads all eight generated sheets and their attribute definitions");
            }
            Phase("Serializing catalog after its transaction closes");
            var serialized=Wire.Element(detached);
            if(!serialized.GetRawText().Contains("TEST-"))throw new System.Exception("Catalog lost attribute defaults");
            checks.Add("Catalog is detached from the database transaction before serialization");
            Phase("Serializing a worker response off the CAD thread after its transaction closes");
            using(var documents=new Documents())
            using(var dispatcher=new Dispatcher(documents))
            {
                var state=documents.Register(doc);
                var request=new Request("stability-read","cad_catalog",documents.SessionId,state.Id,state.Revision,Wire.Element(new{}));
                var execute=typeof(Dispatcher).GetMethod("Execute",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                var response=(Response)execute.Invoke(dispatcher,new object[]{request,CancellationToken.None})!;
                if(response.Data is not JsonElement)throw new System.Exception("Native response retains a deferred object graph");
                var transport=Task.Run(()=>Wire.Element(response)).GetAwaiter().GetResult();
                if(transport.Text("status")!="completed")throw new System.Exception("Native transport response is incomplete");
            }
            checks.Add("Worker response serializes on a background thread after its CAD transaction closes");
            Phase("Saving and reopening a title-block drawing after collection");
            var dwg=Path.ChangeExtension(output,".dwg");doc.Database.SaveAs(dwg,DwgVersion.Current);
            using(var reopened=new Database(false,true))
            {
                reopened.ReadDwgFile(dwg,FileOpenMode.OpenForReadAndAllShare,true,"");reopened.CloseInput(true);
                using var tr=reopened.TransactionManager.StartOpenCloseTransaction();
                var reopenedCatalog=Wire.Element(Catalog.Read(reopened,tr));
                if(!reopenedCatalog.GetRawText().Contains("TEST-"))throw new System.Exception("Reopened title blocks lost attribute text");
            }
            checks.Add("Eight native title blocks save and reopen with intact attributes");
            Phase("Final native-wrapper collection");
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
            checks.Add("Final wrapper collection completes without a native access violation");
        }
        catch(System.Exception error){failure=error.ToString();}
        File.WriteAllText(output,JsonSerializer.Serialize(new{checks,failure}));
    }
    [CommandMethod("CADMCPSTABILITYVERIFY")]
    public static void VerifyAfterUndoAndSave()
    {
        var output=Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT");if(output is null)return;
        var checks=new List<string>();string? failure=null;
        try
        {
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
            checks.Add("Native wrapper collection after UNDO and REDO completes");
            var doc=App.DocumentManager.MdiActiveDocument;
            using(var tr=doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var catalog=Wire.Element(Catalog.Read(doc.Database,tr));
                var layouts=catalog.GetProperty("layouts").EnumerateArray().Count(l=>l.Text("name")?.StartsWith("STABILITY_",StringComparison.Ordinal)==true);
                if(layouts!=8)throw new System.Exception("UNDO/REDO did not restore eight sheet layouts");
            }
            checks.Add("Native UNDO and REDO restore all eight sheet layouts and readable attributes");
            if(Convert.ToInt32(App.GetSystemVariable("DBMOD"))!=0)throw new System.Exception("QSAVE did not complete");
            checks.Add("AutoLISP command-s QSAVE completes after title-block creation and UNDO/REDO");
        }
        catch(System.Exception error){failure=error.ToString();}
        File.WriteAllText(output+".verify",JsonSerializer.Serialize(new{checks,failure}));
    }
}
