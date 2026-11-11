using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadMcp.AutoCAD;
using CadMcp.Core;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CadMcp.CoreProbe.ReviewSafetyProbe))]
namespace CadMcp.CoreProbe;

/// <summary>Dedicated Core Console fixture only. Never runs against an interactive working DWG.</summary>
public static class ReviewSafetyProbe
{
    [CommandMethod("CADMCPREVIEWSAFETY")]
    public static void Run()
    {
        string? output = Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        var checks = new List<string>(); var checkpoints = new List<string>(); string? failure = null;
        try
        {
            if (!Process.GetCurrentProcess().ProcessName.Equals("accoreconsole", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This probe requires a dedicated Core Console process");
            var doc = App.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("No scratch drawing");
            if (Path.IsPathFullyQualified(doc.Name) && !Path.GetFullPath(doc.Name).StartsWith(Path.GetDirectoryName(Path.GetFullPath(output)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing a drawing outside the isolated probe directory");
            using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var blocks = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                if (((BlockTableRecord)tr.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead)).Cast<ObjectId>().Any())
                    throw new InvalidOperationException("Probe requires a new empty model");
            }
            void Check(bool pass, string label) { if (!pass) throw new InvalidOperationException(label); checks.Add(label); File.WriteAllText(output + ".phase", label); }
            doc.Database.Insunits = UnitsValue.Millimeters;
            ObjectId meshId;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            using (var points = new Point3dCollection())
            {
                const int row = 1500;
                for (int y = 0; y < 2; y++) for (int x = 0; x < row; x++) points.Add(new Point3d(x, y, 0));
                var faces = new Int32Collection();
                for (int x = 0; x < row - 1; x++) foreach (int value in new[] { 4, x, x + 1, row + x + 1, row + x }) faces.Add(value);
                using var mesh = new SubDMesh(); mesh.SetSubDMesh(points, faces, 0);
                var blocks = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var model = (BlockTableRecord)tr.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                meshId = model.AppendEntity(mesh); tr.AddNewlyCreatedDBObject(mesh, true); tr.Commit();
            }
            using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var mesh = (SubDMesh)tr.GetObject(meshId, OpenMode.ForRead);
                var first = Reader.Read(mesh, tr, Wire.Element(new { vertex_offset = 0, vertex_limit = 128 }));
                Check(first.Text("access") == "partial" && first.GetProperty("vertex_count").GetInt32() == 3000 && first.GetProperty("vertices").GetArrayLength() == 128 && first.GetProperty("next_vertex_offset").GetInt32() == 128, "Large mesh returns bounded first vertex page");
                var last = Reader.Read(mesh, tr, Wire.Element(new { vertex_offset = 2990, vertex_limit = 128 }));
                Check(last.GetProperty("vertices").GetArrayLength() == 10 && !last.TryGetProperty("next_vertex_offset", out _) && !last.GetProperty("face_array_complete").GetBoolean(), "Final vertex page retains explicit incomplete face geometry");
            }
            JsonElement Edit(string plan) => Wire.Element(Edits.Execute(doc, EditPlan.Parse(plan), CancellationToken.None));
            var tables = Edit("""[{"op":"table_create","id":"source","position":[10000,0],"rows":3,"columns":2,"cells":[{"cell":"A1","value":5}]},{"op":"table_create","id":"dependent","position":[10100,0],"rows":3,"columns":2,"cells":[{"cell":"A1","formula":"={{source}}*2","references":[{"name":"source","table_target":"source","cell":"A1"}]}]}]""");
            string Handle(string alias) => tables.GetProperty("results").EnumerateArray().First(r => r.Text("id") == alias).Text("handle")!;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            { tr.GetObject(NativeTables.Resolve(doc.Database, Handle("source")), OpenMode.ForWrite).Erase(); tr.Commit(); }
            var line = Edit("""[{"op":"line","start":[20000,0],"end":[20100,0]}]""");
            Check(line.GetProperty("entities")[0].GetProperty("length").GetDouble() == 100, "Deleted table source does not block unrelated line creation");
            try
            {
                Edit(JsonSerializer.Serialize(new[] { new { op = "table_cells", handle = Handle("dependent"), cells = new[] { new { cell = "B1", value = 7 } } } }));
                throw new InvalidOperationException("Affected broken table component was silently accepted");
            }
            catch (CadFault error) when (error.Code is "TABLE_NOT_FOUND" or "BROKEN_TABLE_REFERENCE")
            { Check(true, "Affected broken component fails instead of hiding missing source"); }
            Edit(JsonSerializer.Serialize(new[] { new { op = "table_cells", handle = Handle("dependent"), cells = new[] { new { cell = "A1", value = 10 } } } }));
            Check(true, "Explicit constant assignment removes the orphan formula without automatic deletion");
            var journal = new OperationJournal();
            var request = new Request("r", "cad_csharp", "s", "d", Data: Wire.Element(new { operation_id = "many" }));
            journal.Begin("many", request);
            journal.Complete("many", new Response("r", "completed", new { result = new { changed_handles = Enumerable.Range(0, 500).Select(i => i.ToString("X")).ToArray(), changed_handles_omitted = true } }));
            try
            {
                using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
                DrawingReview.Verify(doc, tr, Wire.Element(new { operation_id = "many", verified_document_id = "d" }), journal);
                throw new InvalidOperationException("Truncated operation handles were accepted");
            }
            catch (CadFault error) when (error.Code == "VERIFY_TOO_LARGE")
            { Check(true, "Native verification rejects incomplete C# targets before resolving handles"); }
            string session = Guid.NewGuid().ToString("N");
            JsonElement firstCheckpoint = default;
            for (int i = 0; i < 11; i++)
            {
                var record = Wire.Element(WorkSafety.Checkpoint(doc, session, "owned-review-fixture", "checkpoint-" + i));
                checkpoints.Add(record.Text("path")!); if (i == 0) firstCheckpoint = record;
            }
            Check(File.Exists(checkpoints[0]), "Recovery copy survives more than ten later checkpoints");
            Check(Wire.Element(CheckpointEvidence.Inspect(firstCheckpoint, true)).Text("hash_state") == "passed", "Retained checkpoint hash matches recorded bytes");
        }
        catch (System.Exception error) { failure = error.ToString(); }
        if (failure is null)
            try { foreach (string path in checkpoints) { File.Delete(path); File.Delete(path + ".json"); } }
            catch (System.Exception error) { failure = "Owned fixture cleanup failed: " + error; }
        File.WriteAllText(output, JsonSerializer.Serialize(new { checks, failure, retained_failure_checkpoints = failure is null ? Array.Empty<string>() : checkpoints.ToArray() }));
    }
}
