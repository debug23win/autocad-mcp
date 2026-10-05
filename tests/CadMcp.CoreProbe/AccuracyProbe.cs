using System.Text.Json;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using CadMcp.AutoCAD;
using CadMcp.Core;
using App=Autodesk.AutoCAD.ApplicationServices.Core.Application;
[assembly:CommandClass(typeof(CadMcp.CoreProbe.AccuracyProbe))]
namespace CadMcp.CoreProbe;
public static class AccuracyProbe
{
    [CommandMethod("CADMCPACCURACY")]
    public static void Run()
    {
        string? output=Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT");if(output is null)return;
        var checks=new List<string>();string? failure=null;
        try
        {
            var doc=App.DocumentManager.MdiActiveDocument;doc.Database.Insunits=UnitsValue.Millimeters;
            void Check(bool ok,string text){if(!ok)throw new System.Exception(text);checks.Add(text);File.WriteAllText(output+".phase",text);}
            JsonElement Edit(object plan,string? expectations=null)=>Wire.Element(Edits.Execute(doc,EditPlan.Parse(JsonSerializer.Serialize(plan)),CancellationToken.None,DrawingVerification.Parse(expectations)));
            int Count(){using var tr=doc.Database.TransactionManager.StartOpenCloseTransaction();return ((BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForRead)).Cast<ObjectId>().Count();}
            foreach(var profile in SteelSections.Catalog)
            {
                var result=Edit(new[]{new{op="assembly_create",id="p",kind="beam",position=new[]{0,checks.Count*500d,0},parameters=new{length=1000,profile=profile.Code},mark=profile.Code,material="09Г2С",show_annotations=false}});
                var assembly=result.GetProperty("entities")[0].GetProperty("assembly");double mass=assembly.GetProperty("solid_mass_kg").GetDouble();
                Check(Math.Abs(mass/profile.TheoreticalKgPerM-1)<1e-6,"Nominal "+profile.Code+" includes native section/root radii and correct metre mass");
            }
            var rebar=Edit(new[]{new{op="assembly_create",id="a",kind="rebar",position=new[]{10000,0,0},parameters=new{diameter=16,bend_radius=64,points=new[]{new[]{0,0,0},new[]{1000,0,0},new[]{1000,500,0}}},mark="А1",material="А500С",show_annotations=false}});
            var info=rebar.GetProperty("entities")[0].GetProperty("assembly");double length=1500-128+Math.PI/2*64;
            Check(Math.Abs(info.GetProperty("centerline_length").GetDouble()-length)<1e-8,"Rebar tangent centerline length follows circular bend radius");
            Check(Math.Abs(info.GetProperty("solid_mass_kg").GetDouble()/(Math.PI*64*length*1e-9*7850)-1)<1e-6,"Native curved rebar includes exact bend volume without corner overlap");
            var pile=Edit(new[]{new{op="assembly_create",id="pile",kind="screw_pile",position=new[]{12000,0,0},parameters=new{length=2000,diameter=108,wall_thickness=4,blade_diameter=300,pitch=150,blade_thickness=8},mark="С1",material="09Г2С",show_annotations=false}});
            info=pile.GetProperty("entities")[0].GetProperty("assembly");double shaft=Math.PI*(54*54-50*50)*2000*1e-9,blade=Math.PI*(150*150-54*54)*8*1e-9;
            Check(info.GetProperty("solid_volume_m3").GetDouble()>shaft,"Helical blade contributes positive native solid volume");
            string pileHandle=pile.GetProperty("results").EnumerateArray().First(r=>r.Text("id")=="pile").Text("handle")!;
            using(var tr=doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var reference=(BlockReference)tr.GetObject(NativeTables.Resolve(doc.Database,pileHandle),OpenMode.ForRead);var block=(BlockTableRecord)tr.GetObject(reference.BlockTableRecord,OpenMode.ForRead);
                var components=block.Cast<ObjectId>().Select(id=>tr.GetObject(id,OpenMode.ForRead)).OfType<Solid3d>().ToArray();
                Check(!components[0].CheckInterference(components[1]),"Volumetric helical blade is trimmed against the shaft without double-counted material");
            }
            Check(Math.Abs(info.GetProperty("solid_volume_m3").GetDouble()/(shaft+blade)-1)<.01,"Ruled helical blade total volume agrees with analytic annulus within one percent");
            int before=Count();
            try{Edit(new[]{new{op="box",id="x",center=new[]{20000,0,0},length=100,width=200,height=300}},"""{"task":"Measured envelope","units":"Millimeters","bounds_size":[100,200,301],"type_counts":{"Solid3d":1}}""");throw new System.Exception("Wrong model envelope was committed");}catch(CadFault e)when(e.Code=="ACCEPTANCE_FAILED"){}
            Check(Count()==before,"Whole model envelope requirement rejects and rolls back an incorrect dimension");
            var accepted=Edit(new[]{new{op="box",id="x",center=new[]{20000,0,0},length=100,width=200,height=300}},"""{"task":"Measured envelope","units":"Millimeters","bounds_size":[100,200,300],"type_counts":{"Solid3d":1},"checks":[{"target":"x","property":"volume","minimum":5999999,"maximum":6000001}],"review_views":["front","isometric"],"visual_requirements":["Confirm silhouette against reference"]}""");
            Check(accepted.GetProperty("acceptance").Text("overall_state")=="visual_review_required","Passing measured contract never claims that unreviewed visual fidelity passed");
            doc.Database.SaveAs(Path.ChangeExtension(output,".dwg"),DwgVersion.Current);GC.Collect();GC.WaitForPendingFinalizers();doc.Editor.Regen();
            Check(true,"Accurate assemblies survive save/GC/regeneration");
        }
        catch(System.Exception e){failure=e.ToString();}
        File.WriteAllText(output,JsonSerializer.Serialize(new{checks,failure},Wire.Json));
    }
}
