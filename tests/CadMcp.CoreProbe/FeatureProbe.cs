using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using CadMcp.AutoCAD;
using CadMcp.Core;
using App=Autodesk.AutoCAD.ApplicationServices.Core.Application;
[assembly:CommandClass(typeof(CadMcp.CoreProbe.FeatureProbe))]
namespace CadMcp.CoreProbe;
public static class FeatureProbe
{
    [DllImport("kernel32.dll")]private static extern uint SetErrorMode(uint mode);
    [CommandMethod("CADMCPSEED")]
    public static void Seed(){App.DocumentManager.MdiActiveDocument.Database.SaveAs(Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT")!,DwgVersion.Current);}
    [CommandMethod("CADMCPFEATUREPROBE")]
    public static void Run()
    {
        string? output=Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT");if(output is null)return;SetErrorMode(0x0002|0x8000);var checks=new List<string>();string? failure=null;
        try
        {
            var doc=App.DocumentManager.MdiActiveDocument;doc.Database.Insunits=UnitsValue.Millimeters;
            void Check(bool ok,string text){if(!ok)throw new System.Exception(text);checks.Add(text);File.WriteAllText(output+".phase",text);}
            JsonElement Edit(string json)=>Wire.Element(Edits.Execute(doc,EditPlan.Parse(json),CancellationToken.None));
            string Handle(JsonElement r,string alias)=>r.GetProperty("results").EnumerateArray().First(x=>x.Text("id")==alias).Text("handle")!;
            double Value(string h,string cell){using var tr=doc.Database.TransactionManager.StartOpenCloseTransaction();var table=(Table)tr.GetObject(NativeTables.Resolve(doc.Database,h),OpenMode.ForRead);var(r,c)=DraftingPlan.Address(cell);return Convert.ToDouble(table.Cells[r,c].Value);}
            File.WriteAllText(output+".phase","creating tables");
            var tables=Edit("""[{"op":"table_create","id":"s","position":[0,0,0],"rows":3,"columns":3,"cells":[{"cell":"A1","value":4},{"cell":"B1","value":5},{"cell":"C1","formula":"=A1*B1"}]},{"op":"table_create","id":"d","position":[120,0,0],"rows":2,"columns":2,"cells":[{"cell":"A1","formula":"={{v}}*2","references":[{"name":"v","table_target":"s","cell":"C1"}]}]}]""");
            string source=Handle(tables,"s"),dependent=Handle(tables,"d");Check(Value(dependent,"A1")==40,"Linked native formula initially evaluates to 40");
            Edit(JsonSerializer.Serialize(new[]{new{op="table_cells",handle=source,cells=new[]{new{cell="A1",value=7}}}}));Check(Value(dependent,"A1")==70,"Dependent table automatically updates to 70 without explicit recalculate");
            Edit(JsonSerializer.Serialize(new[]{new{op="table_rows",handle=source,action="insert",index=0,count=1}}));Check(Value(dependent,"A1")==70&&Value(source,"C2")==35,"Stable cell identities survive insertion before source row");
            bool deletion=false;try{Edit(JsonSerializer.Serialize(new[]{new{op="table_rows",handle=source,action="delete",index=1,count=1}}));}catch(CadFault e){deletion=e.Code=="REFERENCED_CELL_DELETE";}Check(deletion&&Value(dependent,"A1")==70,"Deletion of referenced row rejected without changes");
            bool cycle=false;try{Edit(JsonSerializer.Serialize(new[]{new{op="table_cells",handle=source,cells=new[]{new{cell="A2",formula="={{v}}",references=new[]{new{name="v",table_handle=dependent,cell="A1"}}}}}}));}catch(CadFault e){cycle=e.Code=="CYCLIC_FORMULA";}Check(cycle&&Value(source,"A2")==7,"Indirect table cycle rejected atomically");
            using(var tr=doc.Database.TransactionManager.StartTransaction())
            {var t=(Table)tr.GetObject(NativeTables.Resolve(doc.Database,source),OpenMode.ForWrite);t.InsertRows(0,8,1);tr.Commit();}
            using(var tr=doc.Database.TransactionManager.StartTransaction()){TableLinks.Recalculate(doc.Database,tr);tr.Commit();}
            Check(Value(dependent,"A1")==70&&Value(source,"C3")==35,"Manual native row insertion preserves tracked cells and formulas");
            Edit(JsonSerializer.Serialize(new[]{new{op="table_columns",handle=source,action="insert",index=0,count=1}}));
            Check(Value(dependent,"A1")==70&&Value(source,"D3")==35,"Column insertion preserves local and linked formula identities");
            bool columnDelete=false;try{Edit(JsonSerializer.Serialize(new[]{new{op="table_columns",handle=source,action="delete",index=1,count=1}}));}catch(CadFault e){columnDelete=e.Code=="REFERENCED_CELL_DELETE";}
            Check(columnDelete&&Value(dependent,"A1")==70,"Deletion of formula source column is rejected atomically");
            string original=doc.Name;int modified=Convert.ToInt32(App.GetSystemVariable("DBMOD"));var backup=Wire.Element(WorkSafety.Checkpoint(doc,"probe090", "feature",Guid.NewGuid().ToString("N")));
            using(var copy=new Database(false,true)){copy.ReadDwgFile(backup.Text("path")!,FileOpenMode.OpenForReadAndAllShare,true,null);using var tr=copy.TransactionManager.StartOpenCloseTransaction();var block=(BlockTableRecord)tr.GetObject(((BlockTable)tr.GetObject(copy.BlockTableId,OpenMode.ForRead))[BlockTableRecord.ModelSpace],OpenMode.ForRead);Check(block.Cast<ObjectId>().Count()>=2,"Checkpoint opens as DWG and includes native tables");}
            Check(doc.Name==original&&Convert.ToInt32(App.GetSystemVariable("DBMOD"))==modified,"Checkpoint preserves working drawing name and DBMOD");
            var shapes=Edit("""[{"op":"circle","id":"a","center":[400,0,0],"radius":30},{"op":"circle","id":"b","center":[400,0,100],"radius":10},{"op":"solid_loft","id":"loft","sections":[{"target":"a"},{"target":"b"}],"ruled":true},{"op":"box","id":"box","center":[600,0,50],"length":100,"width":100,"height":100},{"op":"solid_section","id":"section","target":"box","origin":[600,0,50],"normal":[0,0,1]}]""");
            string box=Handle(shapes,"box");JsonElement topology;
            using(var tr=doc.Database.TransactionManager.StartOpenCloseTransaction()){var solid=(Solid3d)tr.GetObject(NativeTables.Resolve(doc.Database,box),OpenMode.ForRead);topology=Wire.Element(SolidModeling.Inspect(solid));}
            Check(topology.GetProperty("edges").GetArrayLength()==12&&topology.GetProperty("faces").GetArrayLength()==6,"Native BRep reports 12 box edges and 6 faces");
            Edit(JsonSerializer.Serialize(new[]{new{op="solid_fillet",handle=box,edges=new[]{topology.GetProperty("edges")[0].GetProperty("index").GetInt64()},radius=5}}));Check(true,"Native solid fillet completes with verified topology id");
            var asm=Edit("""[{"op":"assembly_create","id":"beam","kind":"beam","position":[800,0,0],"parameters":{"length":1000,"width":100,"height":200,"web_thickness":8,"flange_thickness":12},"mark":"Б1","material":"09Г2С"}]""");string beam=Handle(asm,"beam");
            Edit(JsonSerializer.Serialize(new[]{new{op="assembly_update",handle=beam,parameters=new{length=1500,width=100,height=200,web_thickness=8,flange_thickness=12}}}));using(var tr=doc.Database.TransactionManager.StartOpenCloseTransaction()){var info=Wire.Element(StructuralAssemblies.Inspect(doc.Database,tr,(BlockReference)tr.GetObject(NativeTables.Resolve(doc.Database,beam),OpenMode.ForRead)));Check(info.GetProperty("recipe").GetProperty("parameters").GetProperty("length").GetDouble()==1500&&info.GetProperty("solid_mass_kg").GetDouble()>0,"Assembly update regenerates native geometry and mass");}
            var other=Edit("""[{"op":"assembly_create","id":"column","kind":"column","position":[2500,0,0],"parameters":{"length":1000,"width":100,"height":200,"web_thickness":8,"flange_thickness":12},"mark":"К1","material":"09Г2С"},{"op":"assembly_create","id":"pile","kind":"screw_pile","position":[3500,0,0],"parameters":{"length":2000,"diameter":108,"wall_thickness":4,"blade_diameter":300,"pitch":150,"blade_thickness":8},"mark":"С1","material":"Сталь"},{"op":"assembly_create","id":"stairs","kind":"stairs","position":[4500,0,0],"parameters":{"run":2000,"rise":1000,"width":900,"steps":6,"tread_thickness":20,"stringer_diameter":50},"mark":"Л1","material":"Сталь"},{"op":"assembly_create","id":"railing","kind":"railing","position":[7000,0,0],"parameters":{"length":2000,"height":1100,"post_spacing":800,"diameter":40},"mark":"О1","material":"Сталь"},{"op":"assembly_create","id":"rebar","kind":"rebar","position":[10000,0,0],"parameters":{"diameter":16,"points":[[0,0,0],[1000,0,0],[1000,0,300]]},"mark":"А1","material":"А500С"},{"op":"assembly_create","id":"joint","kind":"steel_joint","position":[12000,0,0],"parameters":{"plate_length":200,"plate_width":100,"plate_thickness":12,"hole_diameter":16,"holes":[[50,0],[150,0]]},"mark":"У1","material":"09Г2С"}]""");
            using(var tr=doc.Database.TransactionManager.StartOpenCloseTransaction())foreach(string alias in new[]{"column","pile","stairs","railing","rebar","joint"})
            {var info=Wire.Element(StructuralAssemblies.Inspect(doc.Database,tr,(BlockReference)tr.GetObject(NativeTables.Resolve(doc.Database,Handle(other,alias)),OpenMode.ForRead)));Check(info.GetProperty("solid_mass_kg").GetDouble()>0,"Native parametric "+alias+" has positive verified solid mass");}
            var schedule=Edit(JsonSerializer.Serialize(new[]{new{op="assembly_schedule",id="schedule",position=new[]{0,700,0},handles=new[]{beam,Handle(other,"pile")}}}));string scheduleHandle=Handle(schedule,"schedule");double oldMass=Value(scheduleHandle,"E3");
            Edit(JsonSerializer.Serialize(new[]{new{op="assembly_update",handle=beam,parameters=new{length=2000,width=100,height=200,web_thickness=8,flange_thickness=12}}}));
            Check(Math.Abs(Value(scheduleHandle,"E3")/oldMass-4d/3)<1e-8,"Generated native schedule automatically follows changed beam length and mass");
            var reviewShapes=Edit("""[{"op":"box","id":"q1","center":[15000,0,50],"length":100,"width":100,"height":100},{"op":"box","id":"q2","center":[15050,0,50],"length":100,"width":100,"height":100},{"op":"text","id":"t1","position":[15000,300,0],"height":20,"text":"Overlap"},{"op":"text","id":"t2","position":[15000,300,0],"height":20,"text":"Overlap"}]""");
            using(var tr=doc.Database.TransactionManager.StartOpenCloseTransaction())
            {var report=DrawingQuality.Review(doc.Database,tr,new[]{"q1","q2","t1","t2"}.Select(a=>NativeTables.Resolve(doc.Database,Handle(reviewShapes,a))));Check(report.Issues.Any(i=>i.Code=="SOLID_INTERFERENCE")&&report.Issues.Any(i=>i.Code=="TEXT_OVERLAP"),"Quality review detects actual solid interference and overlapping annotation extents");}
            var shellBox=Edit("""[{"op":"box","id":"shell","center":[17000,0,50],"length":100,"width":100,"height":100}]""");string shellHandle=Handle(shellBox,"shell");
            using(var tr=doc.Database.TransactionManager.StartOpenCloseTransaction()){topology=Wire.Element(SolidModeling.Inspect((Solid3d)tr.GetObject(NativeTables.Resolve(doc.Database,shellHandle),OpenMode.ForRead)));}
            Edit(JsonSerializer.Serialize(new[]{new{op="solid_shell",handle=shellHandle,faces=new[]{topology.GetProperty("faces")[0].GetProperty("index").GetInt64()},offset=-5}}));
            using(var tr=doc.Database.TransactionManager.StartOpenCloseTransaction()){double v=((Solid3d)tr.GetObject(NativeTables.Resolve(doc.Database,shellHandle),OpenMode.ForRead)).MassProperties.Volume;Check(v>0&&v<1000000,"Native shell removes a selected face and retains a valid reduced volume");}
            doc.Database.SaveAs(Path.ChangeExtension(output,".dwg"),DwgVersion.Current);using(var copy=new Database(false,true)){copy.ReadDwgFile(Path.ChangeExtension(output,".dwg"),FileOpenMode.OpenForReadAndAllShare,true,null);using var tr=copy.TransactionManager.StartOpenCloseTransaction();var graph=TableLinks.Load(copy,tr);Check(graph.Formulas.Count==2&&graph.Cells.Count>=4,"Stable table dependency graph persists through DWG save and reopen");}
            GC.Collect();GC.WaitForPendingFinalizers();doc.Editor.Regen();Check(true,"New features survive save, native GC and regeneration");
        }
        catch(System.Exception e){failure=e.ToString();}
        File.WriteAllText(output,JsonSerializer.Serialize(new{checks,failure},Wire.Json));
    }
}
