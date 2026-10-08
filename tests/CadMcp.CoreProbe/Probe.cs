using System.Text.Json;
using System.IO;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;
using CadMcp.AutoCAD;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CadMcp.CoreProbe.Probe))]
namespace CadMcp.CoreProbe;
public static class Probe
{
    [CommandMethod("CADMCPTEXTREGRESSION")]
    public static void TextRegression()
    {
        var output = Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        var checks = new List<string>();
        string? failure = null;
        try
        {
            var doc = App.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("No active drawing");
            void Assert(bool passed, string description)
            {
                if (!passed) throw new InvalidOperationException(description);
                checks.Add(description);
            }
            TextProbe.Check(doc, Assert);
            TextProbe.CheckRegression(doc, Assert, Environment.GetEnvironmentVariable("CADMCP_DYNAMIC_TEXT_FIXTURE"));
        }
        catch (System.Exception error) { failure = error.ToString(); }
        File.WriteAllText(output, JsonSerializer.Serialize(new { checks, failure }));
    }

    private static Documents? lispDocuments;
    private static Dispatcher? lispDispatcher;
    private static string? lispDocumentId;
    private static Response Invoke(Request request)
    {
        try { return (Response)typeof(Dispatcher).GetMethod("Execute", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(lispDispatcher, [request, CancellationToken.None])!; }
        catch (System.Reflection.TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    [LispFunction("CADMCPBEGIN")]
    public static string? Begin(ResultBuffer args) => lispDispatcher?.BeginLisp((string)args.AsArray()[0].Value);
    [LispFunction("CADMCPFINISH")]
    public static int Finish(ResultBuffer args)
    { var a = args.AsArray(); lispDispatcher?.FinishLisp((string)a[0].Value, Convert.ToInt32(a[1].Value) == 1, (string)a[2].Value); return 0; }
    [CommandMethod("CADMCPREADPROBE")]
    public static void ReadCurrentDrawing()
    {
        var output = Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        try
        {
            using var documents = new Documents();
            using var dispatcher = new Dispatcher(documents);
            lispDispatcher = dispatcher;
            var current = App.DocumentManager.MdiActiveDocument ?? throw new System.Exception("No active drawing");
            var state = documents.Register(current);
            int dbmodBefore = Convert.ToInt32(App.GetSystemVariable("DBMOD"));
            Response Call(string operation, object data, long? revision, string? document) => Invoke(new(
                Guid.NewGuid().ToString("N"), operation, documents.SessionId, document, revision,
                Wire.Element(data), DateTimeOffset.UtcNow.AddSeconds(25)));
            var context = Call("cad_context", new { }, null, null);
            var catalog = Call("cad_catalog", new { }, context.Revision, context.DocumentId);
            var search = Call("cad_search", new { options_json = "{\"scope\":\"current\",\"limit\":5}" }, context.Revision, context.DocumentId);
            var snapshot = Call("cad_snapshot", new { limit = 5 }, context.Revision, context.DocumentId);
            var result = new
            {
                drawing = Path.GetFileName(current.Name),
                context = new { context.Status, context.Revision, error = context.Error?.Code },
                catalog = new { catalog.Status, catalog.Revision, error = catalog.Error?.Code },
                search = new { search.Status, search.Revision, error = search.Error?.Code },
                snapshot = new { snapshot.Status, snapshot.Revision, error = snapshot.Error?.Code },
                actual_revision = state.Revision,
                dbmod_before = dbmodBefore,
                dbmod_after = Convert.ToInt32(App.GetSystemVariable("DBMOD")),
                ignored_read_side_effect_events = state.ReadSideEffectEvents
            };
            File.WriteAllText(output, JsonSerializer.Serialize(result));
        }
        catch (System.Exception error) { File.WriteAllText(output, JsonSerializer.Serialize(new { failure = error.ToString() })); }
        finally { lispDispatcher = null; }
    }
    [CommandMethod("CADMCPCORELISP")]
    public static void StartLisp()
    {
        var output = Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT"); if (output is null) return;
        try
        {
            lispDocuments = new(); lispDispatcher = new(lispDocuments);
            var doc = App.DocumentManager.MdiActiveDocument; var state = lispDocuments.Register(doc); lispDocumentId = state.Id;
            var reply = Invoke(new("lisp-test", "cad_lisp", lispDocuments.SessionId, state.Id, state.Revision,
                Wire.Element(new { operation_id = "core_lisp", code = "(+ 1 2)" }), DateTimeOffset.UtcNow.AddSeconds(25)));
            if (reply.Status != "queued") throw new System.Exception("LISP was not queued");
        }
        catch (System.Exception error) { File.WriteAllText(output + ".lisp", JsonSerializer.Serialize(new { failure = error.ToString() })); }
    }
    [CommandMethod("CADMCPCORELISPVERIFY")]
    public static void VerifyLisp()
    {
        var output = Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT"); if (output is null || lispDocuments is null) return;
        try
        {
            var reply = Invoke(new("lisp-status", "cad_operation_status", lispDocuments.SessionId, lispDocumentId,
                Data: Wire.Element(new { operation_id = "core_lisp" })));
            var data = Wire.Element(reply.Data!);
            if (data.Text("state") != "completed" || data.GetProperty("result").GetProperty("data").Text("return_value") != "3")
                throw new System.Exception("Real AutoLISP result was not completed: " + data.GetRawText());
            File.WriteAllText(output + ".lisp", JsonSerializer.Serialize(new { checks = new[] { "Production AutoLISP wrapper, managed callbacks and durable journal complete with result 3" }, failure = (string?)null }));
        }
        catch (System.Exception error) { File.WriteAllText(output + ".lisp", JsonSerializer.Serialize(new { failure = error.ToString() })); }
        finally { lispDispatcher?.Dispose(); lispDocuments.Dispose(); lispDispatcher = null; lispDocuments = null; }
    }
    [CommandMethod("CADMCPCOREPROBE")]
    public static void Run()
    {
        var output = Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT"); if (string.IsNullOrEmpty(output)) return;
        var checks = new List<string>(); string? failure = null;
        try
        {
            var doc = App.DocumentManager.MdiActiveDocument;
            void Assert(bool condition, string message) { if (!condition) throw new System.Exception(message); checks.Add(message); }
            using var documents = new Documents();
            var state = documents.Register(doc);
            var plan = EditPlan.Parse("""
                [{"op":"layer","name":"CADMCP_TEST","color_index":3},
                 {"op":"line","id":"line","start":[0,0,0],"end":[100,0,0],"layer":"CADMCP_TEST"},
                 {"op":"circle","center":[50,50,0],"radius":10},
                 {"op":"polyline","points":[[0,100],[50,100],[50,150]]},
                 {"op":"rectangle","id":"rect","first":[100,100],"second":[150,150]},
                 {"op":"text","position":[0,50],"text":"Сеть ✓","height":2.5},
                 {"op":"mtext","position":[0,60],"text":"Колодец","height":2.5,"width":30},
                 {"op":"arc","center":[100,50],"radius":20,"start_angle_deg":0,"end_angle_deg":90},
                 {"op":"dimension_aligned","first":[0,0],"second":[100,0],"position":[0,-10]},
                 {"op":"hatch","boundaries":["rect"]},
                 {"op":"box","center":[200,0,0],"length":10,"width":20,"height":30},
                 {"op":"cylinder","center":[250,0,0],"radius":10,"height":20}]
                """);
            var result = Wire.Element(Edits.Execute(doc, plan, default));
            Assert(result.GetProperty("entities").GetArrayLength() == 11, "Creation/readback of 11 entities: lines, text, dimension, hatch, solids");
            var entities = result.GetProperty("entities").EnumerateArray().ToArray();
            Assert(entities.Any(e => e.Text("text") == "Сеть ✓"), "Unicode text retained");
            var line = entities.First(e => e.Text("type") == "Line").Text("handle")!;
            var circle = entities.First(e => e.Text("type") == "Circle").Text("handle")!;
            var extended = Wire.Element(Edits.Execute(doc, EditPlan.Parse(JsonSerializer.Serialize(new object[] {
                new { op = "point", position = new[] { 300, 0, 0 } },
                new { op = "ellipse", center = new[] { 320, 0, 0 }, major_axis = new[] { 20, 0, 0 }, radius_ratio = 0.5 },
                new { op = "block_define", name = "CADMCP_NATIVE_BLOCK", base_point = new[] { 0, 0, 0 }, handles = new[] { line, circle } },
                new { op = "layout_create", name = "CADMCP_A3" } })), default));
            Assert(extended.GetProperty("entities").EnumerateArray().Any(e => e.Text("type") == "Ellipse"), "Native ellipse and point created with structured readback");
            var spatial = Wire.Element(Edits.Execute(doc, EditPlan.Parse("""
                [{"op":"polyline3d","points":[[400,100,0],[420,100,20],[440,100,0]]},
                 {"op":"mesh","vertices":[[400,0,0],[420,0,0],[400,20,0],[400,0,20]],
                  "faces":[[0,2,1],[0,1,3],[1,2,3],[2,0,3]]}]
                """), default));
            Assert(spatial.GetProperty("entities").EnumerateArray().Any(e => e.Text("type") == "Polyline3d" &&
                e.GetProperty("vertex_count").GetInt32() == 3 && e.GetProperty("vertices")[1][2].GetDouble() == 20),
                "Native 3D polyline retained spatial vertices");
            Assert(spatial.GetProperty("entities").EnumerateArray().Any(e => e.Text("type") == "SubDMesh" &&
                e.GetProperty("vertex_count").GetInt32() == 4 && e.GetProperty("face_count").GetInt32() == 4),
                "Native C# mesh retained four faces and structured readback");
            var smoothPath = Wire.Element(Edits.Execute(doc, EditPlan.Parse("""
                [{"op":"spline","fit_points":[[450,100,0],[460,110,5],[470,90,10],[480,100,15]],"degree":3}]
                """), default));
            Assert(smoothPath.GetProperty("entities")[0].Text("type") == "Spline" &&
                smoothPath.GetProperty("entities")[0].GetProperty("fit_point_count").GetInt32() == 4,
                "Native C# spatial spline retained fit points");
            var modeled = Wire.Element(Edits.Execute(doc, EditPlan.Parse("""
                [{"op":"rectangle","id":"section","first":[500,0,0],"second":[520,20,0]},
                 {"op":"extrude","id":"beam","target":"section","direction":[0,0,30]},
                 {"op":"cylinder","id":"hole","center":[510,10,0],"radius":3,"height":30},
                 {"op":"solid_boolean","target":"beam","tool_target":"hole","operation":"subtract"}]
                """), default));
            var beamHandle = modeled.GetProperty("results")[1].Text("handle");
            Assert(modeled.GetProperty("entities").EnumerateArray().Any(e => e.Text("handle") == beamHandle &&
                e.GetProperty("volume").GetDouble() is > 0 and < 12000),
                "Native C# extrusion and solid subtraction produce measured volume");
            Assert(modeled.GetProperty("results")[3].GetProperty("tool_erased").GetBoolean() &&
                modeled.GetProperty("entities").EnumerateArray().Any(e => e.TryGetProperty("erased", out var erased) && erased.GetBoolean()),
                "Native solid Boolean consumes the tool in the same transaction");
            var merged = Wire.Element(Edits.Execute(doc, EditPlan.Parse("""
                [{"op":"box","id":"first","center":[900,0,0],"length":10,"width":10,"height":10},
                 {"op":"box","id":"second","center":[905,0,0],"length":10,"width":10,"height":10},
                 {"op":"solid_boolean","target":"first","tool_target":"second","operation":"union","keep_tool":true}]
                """), default));
            Assert(merged.GetProperty("entities").EnumerateArray().Any(e => e.Text("type") == "Solid3d" &&
                Math.Abs(e.GetProperty("volume").GetDouble() - 1500) < 1e-5),
                "Native C# union computes overlapping solid volume");
            var intersected = Wire.Element(Edits.Execute(doc, EditPlan.Parse("""
                [{"op":"box","id":"first","center":[930,0,0],"length":10,"width":10,"height":10},
                 {"op":"box","id":"second","center":[935,0,0],"length":10,"width":10,"height":10},
                 {"op":"solid_boolean","target":"first","tool_target":"second","operation":"intersect"}]
                """), default));
            Assert(intersected.GetProperty("entities").EnumerateArray().Any(e => e.Text("type") == "Solid3d" &&
                Math.Abs(e.GetProperty("volume").GetDouble() - 500) < 1e-5),
                "Native C# intersection computes common solid volume");
            var primitives = Wire.Element(Edits.Execute(doc, EditPlan.Parse("""
                [{"op":"sphere","center":[600,0,0],"radius":5},
                 {"op":"cone","center":[620,0,0],"radius":5,"height":12},
                 {"op":"wedge","center":[640,0,0],"length":10,"width":8,"height":12},
                 {"op":"torus","center":[670,0,0],"major_radius":10,"minor_radius":3}]
                """), default));
            Assert(primitives.GetProperty("entities").GetArrayLength() == 4 &&
                primitives.GetProperty("entities").EnumerateArray().All(e => e.Text("type") == "Solid3d" && e.GetProperty("volume").GetDouble() > 0),
                "Native C# sphere, cone, wedge and torus created as 3D solids");
            var swept = Wire.Element(Edits.Execute(doc, EditPlan.Parse("""
                [{"op":"circle","id":"section","center":[700,0,0],"radius":2},
                 {"op":"line","id":"path","start":[700,0,0],"end":[700,0,20]},
                 {"op":"sweep","target":"section","path_target":"path"}]
                """), default));
            Assert(swept.GetProperty("entities").EnumerateArray().Any(e => e.Text("type") == "Solid3d" && e.GetProperty("volume").GetDouble() > 0),
                "Native C# sweep builds a solid along a path");
            var revolved = Wire.Element(Edits.Execute(doc, EditPlan.Parse("""
                [{"op":"rectangle","id":"section","first":[720,0,0],"second":[724,5,0]},
                 {"op":"revolve","target":"section","axis_start":[715,0,0],"axis_end":[715,5,0],"angle_deg":360}]
                """), default));
            Assert(revolved.GetProperty("entities").EnumerateArray().Any(e => e.Text("type") == "Solid3d" && e.GetProperty("volume").GetDouble() > 0),
                "Native C# revolve builds a solid around an axis");
            var rotated3d = Wire.Element(Edits.Execute(doc, EditPlan.Parse("""
                [{"op":"line","id":"upright","start":[800,0,0],"end":[800,10,0]},
                 {"op":"rotate3d","target":"upright","axis_start":[800,0,0],"axis_end":[810,0,0],"angle_deg":90}]
                """), default));
            Assert(rotated3d.GetProperty("entities")[0].Text("type") == "Line" &&
                Math.Abs(rotated3d.GetProperty("entities")[0].GetProperty("end")[2].GetDouble() - 10) < 1e-8,
                "Native C# 3D rotation preserves the requested spatial axis");
            using (var catalogTransaction = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var nativeCatalog = Wire.Element(Catalog.Read(doc.Database, catalogTransaction));
                Assert(nativeCatalog.GetProperty("layouts").EnumerateArray().Any(x => x.Text("name") == "CADMCP_A3") &&
                    nativeCatalog.GetProperty("blocks").EnumerateArray().Any(x => x.Text("name") == "CADMCP_NATIVE_BLOCK"), "Native block definition and layout persisted");
            }
            string a3;
            using (var paper = new PlotSettings(false))
            {
                var validator = PlotSettingsValidator.Current;
                validator.SetPlotConfigurationName(paper, "DWG To PDF.pc3", null);
                validator.RefreshLists(paper);
                a3 = validator.GetCanonicalMediaNameList(paper).Cast<string>()
                    .First(x => x.Contains("A3", StringComparison.OrdinalIgnoreCase));
            }
            var sheet = Wire.Element(Edits.Execute(doc, EditPlan.Parse(JsonSerializer.Serialize(new object[] {
                new { op = "layout_configure", name = "CADMCP_A3", media_name = a3, paper_rotation = 90, paper_units = "millimeters" },
                new { op = "viewport", layout = "CADMCP_A3", center = new[] { 150, 100, 0 }, width = 200, height = 150,
                    model_center = new[] { 50, 50, 0 }, model_height = 75, locked = true },
                new { op = "block", name = "CADMCP_NATIVE_BLOCK", layout = "CADMCP_A3", position = new[] { 10, 10, 0 } }
            })), default));
            var viewportResult = sheet.GetProperty("entities").EnumerateArray().FirstOrDefault(e => e.Text("type") == "Viewport");
            Assert(viewportResult.ValueKind == JsonValueKind.Object && Math.Abs(viewportResult.GetProperty("custom_scale").GetDouble() - 2) < 1e-8 &&
                viewportResult.GetProperty("locked").GetBoolean(),
                "Native paper-space viewport created at exact model scale");
            using (var configured = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var layout = Sheets.Layout(doc.Database, configured, "CADMCP_A3");
                var paperSpace = (BlockTableRecord)configured.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                Assert(layout.CanonicalMediaName == a3 && layout.PlotRotation == PlotRotation.Degrees090 &&
                    paperSpace.Cast<ObjectId>().Any(id => configured.GetObject(id, OpenMode.ForRead) is BlockReference),
                    "Paper size, rotation and title-block insertion retained on named layout");
            }
            Edits.Execute(doc, EditPlan.Parse("[{\"op\":\"layout_copy\",\"source\":\"CADMCP_A3\",\"name\":\"CADMCP_SHEET_2\"}]"), default);
            using (var copied = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var copiedLayout = Sheets.Layout(doc.Database, copied, "CADMCP_SHEET_2");
                var copiedSpace = (BlockTableRecord)copied.GetObject(copiedLayout.BlockTableRecordId, OpenMode.ForRead);
                Assert(copiedSpace.Cast<ObjectId>().Any(id => copied.GetObject(id, OpenMode.ForRead) is Viewport),
                    "Layout copy preserves the viewport and sheet contents");
            }
            var nativeDxf = Wire.Element(Exports.Execute(doc, "dxf", output + ".dxf", null, null, default));
            Assert(nativeDxf.GetProperty("bytes").GetInt64() > 0 && File.Exists(output + ".dxf"), "Native DXF export verified by file size and SHA-256");
            var nativePdf = Wire.Element(Exports.Execute(doc, "pdf", output + ".pdf", "CADMCP_A3", null, default));
            Assert(nativePdf.GetProperty("bytes").GetInt64() > 0 && File.Exists(output + ".pdf"), "Native PDF layout export verified by file size and SHA-256");
            Assert(!PdfRepair.NormalizeStructure(output + ".pdf"), "PDF structural normalization is idempotent");
            var publishFolder = output + ".publish";
            Directory.CreateDirectory(publishFolder);
            var published = Wire.Element(Exports.Publish(doc, publishFolder, "[\"CADMCP_A3\",\"CADMCP_SHEET_2\"]", default));
            Assert(published.Text("status") == "completed" && published.GetProperty("files").GetArrayLength() == 2 &&
                File.Exists(published.Text("manifest_path")) && File.Exists(published.Text("csv_path")),
                "Two native layout PDFs and JSON/CSV file register produced");
            var rasterPath = output + ".png";
            using (var bitmap = new System.Drawing.Bitmap(100, 50))
            {
                using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                graphics.Clear(System.Drawing.Color.White);
                graphics.DrawLine(System.Drawing.Pens.Black, 0, 0, 99, 49);
                bitmap.Save(rasterPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            var rasterPlan = JsonSerializer.Serialize(new[] { new {
                op = "image_attach", path = rasterPath, name = "CADMCP_REGISTERED_IMAGE",
                control_points = new[] {
                    new { pixel = new[] { 0, 0 }, world = new[] { 1000, 2000, 0 } },
                    new { pixel = new[] { 100, 0 }, world = new[] { 1100, 2000, 0 } },
                    new { pixel = new[] { 0, 50 }, world = new[] { 1000, 1950, 0 } } } } });
            var rasterResult = Wire.Element(Edits.Execute(doc, EditPlan.Parse(rasterPlan), default));
            var raster = rasterResult.GetProperty("entities")[0];
            Assert(raster.Text("type") == "RasterImage" && raster.Text("source_path") == rasterPath &&
                Math.Abs(rasterResult.GetProperty("results")[0].GetProperty("image_registration").GetProperty("rms_error").GetDouble()) < 1e-7,
                "Raster attached in WCS with pixel control-point registration");
            var skewPlan = JsonSerializer.Serialize(new[] { new {
                op = "image_attach", path = rasterPath, name = "CADMCP_SKEWED_IMAGE",
                control_points = new[] {
                    new { pixel = new[] { 0, 0 }, world = new[] { 2000, 2000, 0 } },
                    new { pixel = new[] { 100, 0 }, world = new[] { 2100, 2020, 0 } },
                    new { pixel = new[] { 0, 50 }, world = new[] { 2025, 1950, 0 } } } } });
            var skewed = Wire.Element(Edits.Execute(doc, EditPlan.Parse(skewPlan), default));
            Assert(skewed.GetProperty("entities")[0].Text("type") == "RasterImage", "Skewed raster affine orientation stored in DWG");
            var dimension = entities.First(e => e.Text("type") == "AlignedDimension");
            Assert(dimension.GetProperty("geometry").GetProperty("XLine1Point")[0].GetDouble() == 0 && Math.Abs(dimension.GetProperty("measurement").GetDouble() - 100) < 1e-8, "Dimension geometry and measurement read back");
            var changed = Wire.Element(Edits.Execute(doc, EditPlan.Parse(JsonSerializer.Serialize(new object[] {
                new { op = "move", handle = line, displacement = new[] { 10, 20, 0 } },
                new { op = "copy", id = "copy", handle = line, displacement = new[] { 0, 10, 0 } },
                new { op = "rotate", target = "copy", center = new[] { 0, 0, 0 }, angle_deg = 90 },
                new { op = "scale", target = "copy", center = new[] { 0, 0, 0 }, factor = 2 },
                new { op = "mirror", target = "copy", first = new[] { 0, 0, 0 }, second = new[] { 100, 0, 0 } },
                new { op = "set", handle = line, color_index = 1 }, new { op = "erase", target = "copy" } })), default));
            Assert(changed.GetProperty("entities").EnumerateArray().Any(e => e.Text("handle") == line && e.GetProperty("start")[0].GetDouble() == 10 && e.GetProperty("start")[1].GetDouble() == 20), "Move and property edit verified in native database");
            Assert(changed.GetProperty("entities").EnumerateArray().Any(e => e.TryGetProperty("erased", out var erased) && erased.GetBoolean()), "Copy, rotate, scale, mirror and delete committed");
            JsonElement Search(object options)
            { using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction(); return Wire.Element(DrawingSearch.Read(doc, tr, Wire.Element(options), default)); }
            int before = Search(new { scope = "all", limit = 100 }).GetProperty("entities").GetArrayLength();
            try
            {
                Edits.Execute(doc, EditPlan.Parse("[{\"op\":\"line\",\"start\":[777,777],\"end\":[888,888]},{\"op\":\"line\",\"start\":[0,0],\"end\":[1,1],\"layer\":\"MISSING_LAYER\"}]"), default);
                throw new System.Exception("Expected missing layer fault");
            }
            catch (CadFault error) when (error.Code == "LAYER_NOT_FOUND") { }
            Assert(Search(new { scope = "all", limit = 100 }).GetProperty("entities").GetArrayLength() == before, "Failed second edit rolls back first entity");
            var acceptedPlan = EditPlan.Parse("""[{"op":"line","id":"checked","start":[700,700,0],"end":[800,700,0]}]""");
            var acceptance = DrawingVerification.Parse("""{"entity_count":1,"checks":[{"target":"checked","property":"length","expected":101,"tolerance":0.01}]}""");
            try { Edits.Execute(doc, acceptedPlan, default, acceptance); throw new System.Exception("Expected acceptance rollback"); }
            catch (CadFault error) when (error.Code == "ACCEPTANCE_FAILED") { }
            Assert(Search(new { scope = "all", limit = 100 }).GetProperty("entities").GetArrayLength() == before, "Wrong measured length rolls back the native edit before commit");
            acceptance = DrawingVerification.Parse("""{"entity_count":1,"checks":[{"target":"checked","property":"length","expected":100,"tolerance":0.01}]}""");
            var accepted = Wire.Element(Edits.Execute(doc, acceptedPlan, default, acceptance));
            var checkedHandle = accepted.GetProperty("entities")[0].Text("handle")!;
            Assert(accepted.GetProperty("acceptance").Text("state") == "passed", "Actual native length accepted with tolerance");
            Edits.Execute(doc, EditPlan.Parse(JsonSerializer.Serialize(new[] {new {op="scale",handle=checkedHandle,center=new[]{700,700,0},factor=2}})), default);
            using (var reviewTransaction = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var currentReview = Wire.Element(DrawingReview.Verify(doc, reviewTransaction, Wire.Element(new {
                    handles_json = JsonSerializer.Serialize(new[]{checkedHandle}),
                    expectations_json = JsonSerializer.Serialize(new {checks=new[]{new{handle=checkedHandle,property="length",expected=100,tolerance=.01}}})
                }), new OperationJournal()));
                Assert(currentReview.GetProperty("verification").Text("state") == "failed" && currentReview.GetProperty("entities")[0].GetProperty("length").GetDouble() == 200,
                    "Fresh verification detects a changed entity rather than replaying old readback");
                Assert(currentReview.GetProperty("document_state").Text("disk_save") == "unsaved", "Native verification reports unsaved DWG honestly");
            }
            using (var receiptDispatcher = new Dispatcher(documents))
            {
                var queuedId = Guid.NewGuid().ToString("N");
                using var transportCancelled = new CancellationTokenSource();
                var request = new Request("queued", "cad_edit", documents.SessionId, state.Id, state.Revision,
                    Wire.Element(new {operation_id=queuedId,operations_json="[{\"op\":\"line\",\"start\":[900,900,0],\"end\":[1000,900,0]}]"}));
                var waiting = receiptDispatcher.Enqueue(request, transportCancelled.Token);
                transportCancelled.Cancel();
                try { waiting.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                var status = Task.Run(() => receiptDispatcher.Enqueue(new Request("status", "cad_operation_status", documents.SessionId, state.Id,
                    Data:Wire.Element(new{operation_id=queuedId})), default)).GetAwaiter().GetResult();
                Assert(Wire.Element(status.Data!).Text("state") == "queued", "Receipt can be read on a background thread while CAD queue is not pumping");
                var replay = receiptDispatcher.Enqueue(request with{RequestId="replay"}, default).GetAwaiter().GetResult();
                Assert(replay.Status == "pending", "Transport cancellation and exact replay do not enqueue a duplicate mutation");
            }
            try
            {
                Edits.Execute(doc, EditPlan.Parse("[{\"op\":\"layout_create\",\"name\":\"CADMCP_ROLLBACK_LAYOUT\"},{\"op\":\"line\",\"start\":[0,0],\"end\":[1,1],\"layer\":\"MISSING_LAYER\"}]"), default);
                throw new System.Exception("Expected layout rollback fault");
            }
            catch (CadFault error) when (error.Code == "LAYER_NOT_FOUND") { }
            using (var rollbackTransaction = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var layoutDictionary = (DBDictionary)rollbackTransaction.GetObject(doc.Database.LayoutDictionaryId, OpenMode.ForRead);
                Assert(!layoutDictionary.Contains("CADMCP_ROLLBACK_LAYOUT"), "Failed batch rolls back native layout creation");
            }
            try { documents.Active(new("stale", "cad_edit", documents.SessionId, state.Id, state.Revision - 1)); throw new System.Exception("Expected revision conflict"); }
            catch (CadFault error) when (error.Code == "REVISION_CONFLICT") { checks.Add("Stale document revision rejected by actual worker registry"); }
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var table = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForWrite);
                var leaf = new BlockTableRecord { Name = "CADMCP_LEAF" }; table.Add(leaf); tr.AddNewlyCreatedDBObject(leaf, true);
                var nestedLine = new Line(new(1, 2, 0), new(4, 2, 0)); leaf.AppendEntity(nestedLine); tr.AddNewlyCreatedDBObject(nestedLine, true);
                var attributeDefinition = new AttributeDefinition(new(1, 2, 0), "Nested", "TAG", "", doc.Database.Textstyle) { Height = 2.5 }; leaf.AppendEntity(attributeDefinition); tr.AddNewlyCreatedDBObject(attributeDefinition, true);
                var branch = new BlockTableRecord { Name = "CADMCP_BRANCH" }; table.Add(branch); tr.AddNewlyCreatedDBObject(branch, true);
                var child = new BlockReference(new(10, 20, 0), leaf.ObjectId); branch.AppendEntity(child); tr.AddNewlyCreatedDBObject(child, true);
                var attribute = new AttributeReference(); attribute.SetAttributeFromBlock(attributeDefinition, child.BlockTransform); child.AttributeCollection.AppendAttribute(attribute); tr.AddNewlyCreatedDBObject(attribute, true);
                var model = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                foreach (var point in new[] { new Point3d(100, 200, 0), new Point3d(1000, 2000, 0) })
                { var insert = new BlockReference(point, branch.ObjectId); model.AppendEntity(insert); tr.AddNewlyCreatedDBObject(insert, true); }
                for (int i = 0; i < 2500; i++)
                { var e = new Line(new(i, 500, 0), new(i, 501, 0)); model.AppendEntity(e); tr.AddNewlyCreatedDBObject(e, true); }
                var late = new DBText { Position = new(9000, 9000, 0), TextString = "CADMCP_LATE_2500", Height = 2.5 }; model.AppendEntity(late); tr.AddNewlyCreatedDBObject(late, true);
                tr.Commit();
            }
            var nested = Search(new { scope = "model", type = "Line", expand_blocks = true, details = true, bounds = new { min = new[] { 110, 221, -1 }, max = new[] { 115, 223, 1 } } });
            var nestedItem = nested.GetProperty("entities").EnumerateArray().Single();
            Assert(nestedItem.GetProperty("start")[0].GetDouble() == 111 && nestedItem.GetProperty("start")[1].GetDouble() == 222 && nestedItem.GetProperty("depth").GetInt32() == 2, "Nested block geometry transformed to WCS and isolated by bounds");
            var nestedBlock = Search(new { scope = "model", type = "BlockReference", expand_blocks = true, details = true }).GetProperty("entities").EnumerateArray().First(e => e.GetProperty("depth").GetInt32() == 1);
            Assert(nestedBlock.GetProperty("attribute_details")[0].GetProperty("position")[0].GetDouble() == 111 && nestedBlock.GetProperty("attribute_details")[0].GetProperty("position")[1].GetDouble() == 222, "Nested attribute positions returned in WCS");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var lateSearch = Search(new { scope = "model", text = "CADMCP_LATE_2500", limit = 10 }); watch.Stop();
            Assert(lateSearch.GetProperty("entities").GetArrayLength() == 1 && lateSearch.GetProperty("visited").GetInt32() > 2500, "Search reaches entities beyond snapshot limit of 2000");
            checks.Add("Targeted search of 2500+ entities: " + watch.ElapsedMilliseconds + " ms");
            var page = Search(new { scope = "model", type = "Line", offset = 2400, limit = 100 });
            Assert(page.GetProperty("entities").GetArrayLength() == 100 && page.GetProperty("pagination").GetProperty("next_offset").GetInt32() == 2500, "Large database pagination stays bounded and stable");
            var detail = Search(new { scope = "model", type = "AlignedDimension", details = true });
            Assert(detail.GetProperty("entities")[0].TryGetProperty("geometry", out _), "Search returns full dimension detail on demand");
            Assert(Search(new { scope = "selection" }).GetProperty("entities").GetArrayLength() == 0, "Empty selection safely returns no objects");
            using (var verticalRead = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var verticals = Wire.Element(Verticals.Catalog(doc.Database, verticalRead));
                Assert(verticals.TryGetProperty("civil3d", out _) && verticals.TryGetProperty("map3d", out _),
                    "Optional Civil 3D and Map 3D adapters report availability safely");
                if (!verticals.GetProperty("civil3d").GetProperty("available").GetBoolean())
                {
                    try
                    {
                        Verticals.EditTin(doc.Database, verticalRead, EditPlan.Parse("[{\"op\":\"civil_tin_create\",\"name\":\"Probe\",\"vertices\":[[0,0,0],[1,0,0],[0,1,1]]}]")[0]);
                        throw new System.Exception("Expected Civil 3D capability fault");
                    }
                    catch (CadFault error) when (error.Code == "CIVIL3D_REQUIRED")
                    { checks.Add("Civil TIN creation is explicitly unavailable outside Civil 3D"); }
                }
            }
            TextProbe.Check(doc, Assert);
        }
        catch (System.Exception error) { failure = error.ToString(); }
        File.WriteAllText(output, JsonSerializer.Serialize(new { checks, failure }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
