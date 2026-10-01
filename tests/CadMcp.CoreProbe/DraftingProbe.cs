using System.Text.Json;
using System.IO;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.DatabaseServices;
using CadMcp.Core;
using CadMcp.AutoCAD;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CadMcp.CoreProbe.DraftingProbe))]
namespace CadMcp.CoreProbe;
public static class DraftingProbe
{
    [CommandMethod("CADMCPDRAFTINGPROBE")]
    public static void Run()
    {
        string? output=Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT");if(output is null)return;
        var checks=new List<string>();string? failure=null;
        try
        {
            var doc=App.DocumentManager.MdiActiveDocument;doc.Database.Insunits=UnitsValue.Millimeters;
            void Assert(bool ok,string text){if(!ok)throw new System.Exception(text);checks.Add(text);}
            JsonElement Edit(string json)=>Wire.Element(Edits.Execute(doc,EditPlan.Parse(json),CancellationToken.None));
            string Handle(JsonElement result,string alias)=>result.GetProperty("results").EnumerateArray().First(x=>x.Text("id")==alias).Text("handle")!;
            double Value(string handle,string address)
            {
                using var tr=doc.Database.TransactionManager.StartTransaction();
                var table=(Table)tr.GetObject(NativeTables.Resolve(doc.Database,handle),OpenMode.ForRead);
                var (r,c)=DraftingPlan.Address(address);return Convert.ToDouble(table.Cells[r,c].Value,System.Globalization.CultureInfo.InvariantCulture);
            }
            var result=Edit("""
              [{"op":"table_create","id":"source","position":[0,100],"rows":3,"columns":3,"cells":[{"cell":"A1","value":4},{"cell":"B1","value":2.5},{"cell":"C1","formula":"=A1*B1"}]},
               {"op":"table_create","id":"dependent","position":[120,100],"rows":3,"columns":2,"cells":[{"cell":"A1","formula":"={{mass}}*2","references":[{"name":"mass","table_target":"source","cell":"C1"}],"precision":3}]},
               {"op":"spds_dimstyle","name":"CADMCP_SPDS_TEST","drawing_scale":1},
               {"op":"dimension_rotated","id":"dimension","first":[0,0],"second":[120,0],"position":[0,-15],"style":"CADMCP_SPDS_TEST"},
               {"op":"spds_axis","id":"axis","first":[0,-30],"second":[0,120],"label":"А"},
               {"op":"spds_level","id":"level","position":[120,0],"elevation":1.25},
               {"op":"layout_create","name":"SPDS_TEST"},
               {"op":"spds_sheet","id":"sheet","layout":"SPDS_TEST","format":"A3","designation":"TEST-КМ","project":"Проверка штатных объектов","building":"Контрольный пример","drawing_title":"Спецификация","organization":"CAD MCP","sheet":"1","sheets":"1"},
               {"op":"spds_table","id":"spec","layout":"SPDS_TEST","position":[100,230],"template":"specification","data":[["1","ГОСТ 8239","Двутавр",4,12.5,""]]}]
              """);
            string source=Handle(result,"source"),dependent=Handle(result,"dependent"),sheet=Handle(result,"sheet");
            Assert(Math.Abs(Value(source,"C1")-10)<1e-8,"Native local table formula evaluates to 10");
            Assert(Math.Abs(Value(dependent,"A1")-20)<1e-8,"Native inter-table AcExpr evaluates to 20");
            Edit(JsonSerializer.Serialize(new object[]{new{op="table_cells",handle=source,cells=new[]{new{cell="A1",value=6}}},new{op="table_recalculate",handle=dependent}}));
            Assert(Math.Abs(Value(source,"C1")-15)<1e-8,"Local formula updates after source-cell editing");
            Assert(Math.Abs(Value(dependent,"A1")-30)<1e-8,"Inter-table field recalculates after source editing");
            Edit(JsonSerializer.Serialize(new object[]{new{op="table_merge",handle=source,first_row=1,first_column=0,last_row=1,last_column=2},new{op="table_rows",handle=source,action="insert",index=3,count=1},new{op="table_columns",handle=dependent,action="insert",index=2,count=1}}));
            Assert(Math.Abs(Value(source,"C1")-15)<1e-8,"Local formula survives native row insertion");
            using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                var table=(Table)tr.GetObject(NativeTables.Resolve(doc.Database,source),OpenMode.ForRead);
                Assert(table.Rows.Count==4&&table.Cells[1,0].IsMerged==true,"Native row insertion and merged cells remain editable");
                var read=Wire.Element(NativeTables.Read(table,tr));
                Assert(read.GetProperty("cells").EnumerateArray().Any(c=>c.Text("cell")=="C1"&&c.Text("formula") is {} f&&f.StartsWith('=')&&f.Contains("A1*B1")),"Table readback reports native formula and A1 addresses");
                var frame=(BlockReference)tr.GetObject(NativeTables.Resolve(doc.Database,sheet),OpenMode.ForRead);
                Assert(frame.AttributeCollection.Cast<ObjectId>().Select(i=>(AttributeReference)tr.GetObject(i,OpenMode.ForRead)).Any(a=>a.Tag=="DESIGNATION"&&a.TextString=="TEST-КМ"),"SPDS form 3 has editable native designation attribute");
                var spec=(Table)tr.GetObject(NativeTables.Resolve(doc.Database,Handle(result,"spec")),OpenMode.ForRead);
                Assert(Math.Abs(spec.Width-185)<1e-8&&Math.Abs(spec.Rows[0].Height-15)<1e-8&&Math.Abs(spec.Rows[1].Height-8)<1e-8,$"Specification form 7 uses 185 mm width, 15 mm header and 8 mm row: {spec.Width}, {spec.Rows[0].Height}, {spec.Rows[1].Height}");
                var dim=(Dimension)tr.GetObject(NativeTables.Resolve(doc.Database,Handle(result,"dimension")),OpenMode.ForRead);
                Assert(Math.Abs(dim.Measurement-120)<1e-8&&Math.Abs(dim.Dimtsz-1.5)<1e-8,"Native SPDS dimension measures 120 mm with oblique ticks");
                var axis=(BlockReference)tr.GetObject(NativeTables.Resolve(doc.Database,Handle(result,"axis")),OpenMode.ForRead);
                var axisDef=(BlockTableRecord)tr.GetObject(axis.BlockTableRecord,OpenMode.ForRead);
                Assert(axisDef.Cast<ObjectId>().Select(i=>tr.GetObject(i,OpenMode.ForRead)).OfType<Line>().Any(l=>l.Linetype=="CENTER2"),"Native axis uses a centre dash-dot linetype");
            }
            bool rollback=false;
            try{Edit(JsonSerializer.Serialize(new object[]{new{op="table_cells",handle=source,cells=new[]{new{cell="A1",value=999}}},new{op="table_cells",handle=source,cells=new[]{new{cell="ZZ1",value=2}}}}));}
            catch(CadFault){rollback=true;}
            Assert(rollback&&Value(source,"A1")==6,"Invalid table mutation rolls back the complete native batch");
            Edit(JsonSerializer.Serialize(new object[]{new{op="table_cells",handle=Handle(result,"spec"),layout="SPDS_TEST",cells=new[]{new{cell="A2",value="2"}}},new{op="set",handle=sheet,layout="SPDS_TEST",attributes=new{DRAWING_TITLE="Спецификация: проверено"}}}));
            using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                var spec=(Table)tr.GetObject(NativeTables.Resolve(doc.Database,Handle(result,"spec")),OpenMode.ForRead);
                var frame=(BlockReference)tr.GetObject(NativeTables.Resolve(doc.Database,sheet),OpenMode.ForRead);
                Assert(spec.Cells[1,0].TextString=="2"&&frame.AttributeCollection.Cast<ObjectId>().Select(i=>(AttributeReference)tr.GetObject(i,OpenMode.ForRead)).Any(a=>a.Tag=="DRAWING_TITLE"&&a.TextString.Contains("проверено")),"Paper-layout table and title-block attributes edit without switching the active drawing space");
            }
            var destination=Path.ChangeExtension(output,".dwg");doc.Database.SaveAs(destination,DwgVersion.Current);
            Assert(File.Exists(destination),"Drafting sample saved as a native DWG");
            Edit("""[{"op":"layout_configure","name":"SPDS_TEST","device":"DWG To PDF.pc3","media_name":"ISO_full_bleed_A3_(420.00_x_297.00_MM)","paper_units":"millimeters","paper_rotation":0}]""");
            var pdf=Path.ChangeExtension(output,".pdf");Exports.Execute(doc,"pdf",pdf,"SPDS_TEST",null,CancellationToken.None);
            Assert(File.Exists(pdf)&&new FileInfo(pdf).Length>1000,"Native SPDS sheet plots to a nonempty PDF");
        }
        catch(System.Exception error){failure=error.ToString();}
        File.WriteAllText(output,JsonSerializer.Serialize(new{checks,failure}));
    }
}
