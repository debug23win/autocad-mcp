using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using CadMcp.AutoCAD;
using CadMcp.Core;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CadMcp.CoreProbe.DimensionProbe))]
namespace CadMcp.CoreProbe;

public static class DimensionProbe
{
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private static void Inspect(Database db)
    {
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var space = (BlockTableRecord)tr.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        var counts = new Dictionary<Type, int>();
        foreach (ObjectId id in space)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Dimension dimension) continue;
            var type = dimension.GetType();
            counts[type] = counts.GetValueOrDefault(type) + 1;
            double expected = dimension switch { RadialDimension => 20, DiametricDimension => 40, _ => 100 };
            if (Math.Abs(dimension.Measurement - expected) > 1e-7)
                throw new System.Exception("Incorrect measurement: " + type.Name);
            if (!dimension.DimensionText.StartsWith("D", StringComparison.Ordinal))
                throw new System.Exception("Dimension text override was lost");
            if (dimension is RadialDimension or DiametricDimension && Math.Abs(dimension.Dimtsz) > 1e-8)
                throw new System.Exception("Radial dimension incorrectly retains oblique ticks");
            if (dimension.DimBlockId.IsNull || ((BlockTableRecord)tr.GetObject(dimension.DimBlockId, OpenMode.ForRead)).Cast<ObjectId>().Count() == 0)
                throw new System.Exception("Dimension has no generated native block");
            _ = Reader.Read(dimension, tr);
        }
        foreach (var type in new[] { typeof(AlignedDimension), typeof(RotatedDimension), typeof(RadialDimension), typeof(DiametricDimension) })
            if (counts.GetValueOrDefault(type) != 24) throw new System.Exception("Missing dimensions of type " + type.Name);
        _ = Wire.Element(Catalog.Read(db, tr));
    }

    [CommandMethod("CADMCPDIMENSIONPROBE")]
    public static void Run()
    {
        var output = Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT"); if (output is null) return;
        SetErrorMode(0x0002 | 0x8000);
        var checks = new List<string>(); string? failure = null;
        void Phase(string phase) => File.WriteAllText(output + ".phase", phase);
        try
        {
            var doc = App.DocumentManager.MdiActiveDocument;
            doc.Database.Insunits = UnitsValue.Millimeters;
            var operations = new List<object>
            {
                new { op = "spds_dimstyle", name = "DIMENSION_TICKS", drawing_scale = 1, ticks = true },
                new { op = "spds_dimstyle", name = "DIMENSION_ARROWS", drawing_scale = 1, ticks = false }
            };
            for (int i = 0; i < 24; i++)
            {
                double x = i * 150;
                string style = i % 2 == 0 ? "DIMENSION_TICKS" : "DIMENSION_ARROWS";
                operations.Add(new { op = "dimension_aligned", id = "aligned" + i, first = new[] { x, 0d }, second = new[] { x + 100, 0d }, position = new[] { x, -20d }, style });
                operations.Add(new { op = "dimension_rotated", id = "rotated" + i, first = new[] { x, 100d }, second = new[] { x + 100, 100d }, position = new[] { x, 80d }, style });
                operations.Add(new { op = "dimension_radius", id = "radius" + i, center = new[] { x, 200d }, chord = new[] { x + 20, 200d }, position = new[] { x + 50, 210d }, style });
                operations.Add(new { op = "dimension_diameter", id = "diameter" + i, first = new[] { x - 20, 300d }, second = new[] { x + 20, 300d }, position = new[] { x + 60, 310d }, style });
            }
            Phase("Creating 96 dimensions and eight title blocks in one drawing");
            var result = Wire.Element(Edits.Execute(doc, EditPlan.Parse(JsonSerializer.Serialize(operations)), CancellationToken.None));
            var sheets = new List<object>();
            for (int i = 0; i < 8; i++)
            {
                string layout = "DIMENSION_SHEET_" + i;
                sheets.Add(new { op = "layout_create", name = layout });
                sheets.Add(new { op = "spds_sheet", layout, format = "A3", designation = "DIM-" + i, drawing_title = "Dimension and title-block lifetime test" });
            }
            Edits.Execute(doc, EditPlan.Parse(JsonSerializer.Serialize(sheets)), CancellationToken.None);
            var handles = result.GetProperty("results").EnumerateArray().Where(r => r.Text("id") is not null).Select(r => r.Text("handle")!).ToArray();
            Collect();
            checks.Add("96 dimensions and eight title blocks survive native-wrapper collection");
            Phase("Editing dimension text and recomputing dimension blocks");
            var edits = handles.Select((handle, i) => new { op = "set", handle, text = "D" + i + " <>" }).ToArray();
            Edits.Execute(doc, EditPlan.Parse(JsonSerializer.Serialize(edits)), CancellationToken.None);
            Collect();
            for (int cycle = 0; cycle < 3; cycle++)
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (var handle in handles)
                        ((Dimension)tr.GetObject(NativeTables.Resolve(doc.Database, handle), OpenMode.ForWrite)).RecomputeDimensionBlock(true);
                    tr.Commit();
                }
                doc.Editor.Regen(); Collect(); Inspect(doc.Database);
            }
            checks.Add("Aligned, rotated, radial and diametric dimensions retain correct measurements through three regeneration/collection cycles");
            Phase("Saving and reopening the mixed dimension/title-block drawing");
            var saved = Path.ChangeExtension(output, ".dwg");
            doc.Database.SaveAs(saved, DwgVersion.Current);
            using (var reopened = new Database(false, true))
            {
                reopened.ReadDwgFile(saved, FileOpenMode.OpenForReadAndAllShare, true, ""); reopened.CloseInput(true);
                Inspect(reopened);
            }
            checks.Add("All 96 native dimension blocks save and reopen with intact measurements and text");
            Collect(); Inspect(doc.Database);
            checks.Add("Final catalog and dimension readback complete after transient wrappers are collected");
        }
        catch (System.Exception error) { failure = error.ToString(); }
        File.WriteAllText(output, JsonSerializer.Serialize(new { checks, failure }));
    }

    [CommandMethod("CADMCPDIMENSIONVERIFY")]
    public static void Verify()
    {
        var output = Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT"); if (output is null) return;
        var checks = new List<string>(); string? failure = null;
        try
        {
            Collect();
            var doc = App.DocumentManager.MdiActiveDocument;
            Inspect(doc.Database);
            checks.Add("Native UNDO/REDO and regeneration retain all 96 readable dimensions");
            if (Convert.ToInt32(App.GetSystemVariable("DBMOD")) != 0) throw new System.Exception("QSAVE did not complete");
            checks.Add("Command-s QSAVE completes after mixed dimensions, title blocks and UNDO/REDO");
        }
        catch (System.Exception error) { failure = error.ToString(); }
        File.WriteAllText(output + ".verify", JsonSerializer.Serialize(new { checks, failure }));
    }
}
