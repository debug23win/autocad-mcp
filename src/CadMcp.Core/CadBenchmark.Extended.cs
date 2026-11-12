using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;

namespace CadMcp.Core;

public sealed partial class CadBenchmark
{
    // The archive table lies below every other fixture text: cad_outline samples texts from the top of the
    // model down, and texts above would push the Unicode "Ø114" texts out of its sample.
    internal const int ArchivePayloadLength = 6_000;
    internal const int ArchiveRows = 100;
    internal const string ArchiveMarker = "ARCHIVE_PAYLOAD_";
    internal static readonly string CSharpFixture = $$$"""
using System;
using System.Threading;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
public static class CadMcpScript {
 public static object Run(Database database, Transaction transaction, CancellationToken cancellationToken) {
  var layers=(LayerTable)transaction.GetObject(database.LayerTableId,OpenMode.ForWrite);
  foreach(var name in new[]{"B_REALCS","B_SOLID","B_ARCHIVE","B_REVIEW"}) if(!layers.Has(name)){using var layer=new LayerTableRecord{Name=name};layers.Add(layer);transaction.AddNewlyCreatedDBObject(layer,true);}
  var blocks=(BlockTable)transaction.GetObject(database.BlockTableId,OpenMode.ForRead);
  var model=(BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
  string Add(Entity entity,string layer){using(entity){entity.Layer=layer;model.AppendEntity(entity);transaction.AddNewlyCreatedDBObject(entity,true);return entity.Handle.ToString();}}
  var line=Add(new Line(new Point3d(9000,0,0),new Point3d(9100,0,0)),"B_REALCS");
  var circle=Add(new Circle(new Point3d(9050,50,0),Vector3d.ZAxis,10),"B_REALCS");
  var box=new Solid3d();box.CreateBox(10,20,30);box.TransformBy(Matrix3d.Displacement(new Vector3d(9200,0,0)));var solid=Add(box,"B_SOLID");
  var duplicateA=Add(new Line(new Point3d(9500,0,0),new Point3d(9600,0,0)),"B_REVIEW");
  var duplicateB=Add(new Line(new Point3d(9500,0,0),new Point3d(9600,0,0)),"B_REVIEW");
  cancellationToken.ThrowIfCancellationRequested();
  var archiveTable=new Table();archiveTable.SetDatabaseDefaults(database);archiveTable.TableStyle=database.Tablestyle;
  archiveTable.SetSize({{{ArchiveRows}}},1);archiveTable.Position=new Point3d(12000,-3000,0);
  archiveTable.Columns[0].Width=200;archiveTable.Cells.TextHeight=2;
  for(int i=0;i<{{{ArchiveRows}}};i++){cancellationToken.ThrowIfCancellationRequested();archiveTable.Rows[i].Height=8;archiveTable.Cells[i,0].Value="{{{ArchiveMarker}}}"+i.ToString("D3")+"_"+new string('X',{{{ArchivePayloadLength}}});}
  archiveTable.GenerateLayout();
  var archive=Add(archiveTable,"B_ARCHIVE");
  return new{line,circle,solid,duplicateA,duplicateB,archive};
 }
}
""";
    private sealed partial class Run
    {
        private JsonElement extra, frame, snapshot;
        private string? referenceId, fullRoot, referencePdf, referencePng, sheet;
        private bool extraReady, frameReady, snapshotReady, sheetConfigured;
        private long? snapshotRevision;

        private async Task<JsonElement> Tool(string name, object arguments, CancellationToken ct, bool partial = false, bool allowIncomplete = false)
        {
            var context = await Call("cad_context", new { }, ct); Success(context);
            var response = await Call(name, arguments, ct, context.Revision);
            if (response.Error is not null || response.Status != "completed" && !(partial && response.Status == "partial")) Success(response);
            var value = Data(response);
            foreach (var node in new[] { value, Body(value), value.ValueKind == JsonValueKind.Object && value.TryGetProperty("catalog", out var catalog) ? catalog : default })
            {
                if (node.ValueKind != JsonValueKind.Object) continue;
                if (name != "cad_result_get" && node.TryGetProperty("archive_id", out _)) throw new Fault("INCOMPLETE_EVIDENCE", "Archived data must be reconstructed before verification");
                if (!allowIncomplete && (node.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True || node.TryGetProperty("partial", out var limited) && limited.ValueKind == JsonValueKind.True || node.TryGetProperty("complete", out var complete) && complete.ValueKind == JsonValueKind.False || name != "cad_result_get" && node.TryGetProperty("historical", out var historical) && historical.ValueKind == JsonValueKind.True))
                    throw new Fault("INCOMPLETE_EVIDENCE", "Partial or historical response cannot prove this case");
            }
            return value;
        }
        private static JsonElement Body(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("result", out var result) ? result : value;
        private void Is(string label, bool condition, object actual) => Check(label, condition, true, actual);
        private string Extra(string key) => extraReady ? extra.GetProperty(key).GetString()! : throw new Fault("PREREQUISITE_MISSING", "Real C# fixture is unavailable", skipped: true);
        private async Task EnsureFrame(CancellationToken ct)
        {
            if (frameReady) return;
            frame = await Tool("cad_render", new { width = 800, height = 600, view_name = "top", handles_json = JsonSerializer.Serialize(new[] { Extra("line"), Extra("circle") }) }, ct, true);
            Is("real MCP image", frame.TryGetProperty("mcp_images", out var images) && images.GetArrayLength() == 1 && images[0].GetProperty("valid_png_header").GetBoolean(), frame);
            Is("PNG decodes and contains visible variation", images[0].GetProperty("decoded").GetBoolean() && images[0].GetProperty("non_uniform").GetBoolean(), images);
            Number("render width", 800, frame.GetProperty("width").GetDouble()); Number("render height", 600, frame.GetProperty("height").GetDouble());
            frameReady = true;
        }
        private async Task EnsureSnapshot(CancellationToken ct)
        {
            if (snapshotReady) return;
            snapshot = await Tool("cad_snapshot", new { limit = 10 }, ct, partial: true, allowIncomplete: true);
            Is("bounded snapshot id", snapshot.Text("snapshot_id") is { Length: > 0 }, snapshot);
            Number("bounded snapshot captures ten", 10, snapshot.GetProperty("captured").GetDouble());
            Is("snapshot truncation", snapshot.GetProperty("truncated").GetBoolean(), snapshot);
            var context = await Call("cad_context", new { }, ct); Success(context); snapshotRevision = context.Revision;
            snapshotReady = true;
        }
        private async Task EnsureSheet(CancellationToken ct, bool configure = false)
        {
            if (sheet is null) { await Edit([new { op = "layout_create", name = "B_FULL_SHEET" }], ct); sheet = "B_FULL_SHEET"; }
            if (configure && !sheetConfigured)
            {
                var catalog = await Tool("cad_catalog", new { }, ct);
                var settings = catalog.GetProperty("catalog");
                var drivers = settings.GetProperty("pdf_devices").EnumerateArray().Where(p => p.Text("name") == "DWG To PDF.pc3" && p.TryGetProperty("media", out var media) && media.GetArrayLength() > 0).ToArray();
                if (drivers.Length == 0) throw new Fault("PDF_DEVICE_UNAVAILABLE", "No PDF driver/media available in this AutoCAD installation", skipped: true);
                var driver = drivers.FirstOrDefault(p => p.Text("name") == "DWG To PDF.pc3"); if (driver.ValueKind == JsonValueKind.Undefined) driver = drivers[0];
                var names = driver.GetProperty("media").EnumerateArray().Select(p => p.GetString()!).ToArray();
                string mediaName = names.FirstOrDefault(n => n == "ISO_full_bleed_A3_(420.00_x_297.00_MM)") ?? names.FirstOrDefault(n => n.Contains("A3", StringComparison.OrdinalIgnoreCase)) ?? names[0];
                await Edit([new { op = "layout_configure", name = sheet, device = driver.Text("name"), media_name = mediaName, paper_units = "millimeters", paper_rotation = 0 }], ct);
                sheetConfigured = true;
            }
        }
        private async Task Extended(List<CadBenchmarkCase> results, bool stopped, CancellationToken ct)
        {
            if (!stopped && fixturePath is not null)
            {
                fullRoot = Wire.DataDirectory("benchmarks", id, "files");
                Directory.CreateDirectory(fullRoot);
                referencePdf = Path.Combine(fullRoot, "benchmark-reference.pdf");
                var pdf = new PdfDocumentBuilder(); var font = pdf.AddStandard14Font(Standard14Font.Helvetica);
                pdf.AddPage(PageSize.A4).AddText("BENCHMARK PDF PAGE ONE 123", 14, new PdfPoint(30, 50), font);
                pdf.AddPage(PageSize.A4).AddText("BENCHMARK PDF PAGE TWO 456", 14, new PdfPoint(30, 50), font);
                using var file = new FileStream(referencePdf, FileMode.CreateNew, FileAccess.Write);
                file.Write(pdf.Build());
            }
            var originalCases = results.ToArray();
            foreach (string tool in toolInventory!.Distinct(StringComparer.Ordinal).OrderBy(t => t == "cad_csharp" ? 0 : 1).ThenBy(t => t, StringComparer.Ordinal))
            {
                string caseId = "tool_" + tool;
                if (stopped || ct.IsCancellationRequested) { results.Add(new(caseId, tool, "MCP", "skipped", 0, 0, "NOT_RUN", "Stopped after a preceding failure, unknown outcome or cancellation", [], [])); continue; }
                progress?.Invoke(new($"[{results.Count + 1}/{Definitions.Length + toolInventory!.Count}] {tool}", document, "case_started"));
                checks.Clear(); calls.Clear(); approvalMs = 0; var watch = Stopwatch.StartNew();
                string status = "passed"; string? errorCode = null, message = null;
                try
                {
                    string? baseCase = tool switch { "cad_edit" or "cad_entity_get" or "cad_takeoff" => "polyline", "cad_search" => "paging", "cad_table_get" => "tables", "cad_lisp" => "lisp", _ => null };
                    if (baseCase is not null)
                    {
                        var evidence = originalCases.Single(c => c.Id == baseCase);
                        if (evidence.Status != "passed") throw new Fault("PREREQUISITE_MISSING", "Functional scenario " + baseCase + " did not pass", skipped: true);
                        Is("verified functional evidence: " + baseCase, evidence.Checks.Count > 0 && evidence.Checks.All(c => c.Passed) && evidence.Calls.Any(c => c.Request.Operation == tool && c.Response?.Error is null), evidence.Checks);
                    }
                    else await ToolCase(tool, ct);
                    if (checks.Count == 0) throw new Fault("EMPTY_VERIFICATION", "A tool invocation without assertions cannot pass");
                }
                catch (Fault error) { status = error.Unknown ? "unknown" : error.Skipped ? "skipped" : "failed"; errorCode = error.Code; message = error.Message; stopped |= error.Unknown || !error.Skipped; }
                catch (OperationCanceledException error) { status = "unknown"; errorCode = "CANCELLED_WAIT"; message = error.Message; stopped = true; }
                catch (Exception error) { bool uncertain = Wire.IsTransportFailure(error); status = uncertain ? "unknown" : "failed"; errorCode = uncertain ? "BENCHMARK_TRANSPORT_UNCERTAIN" : error is CadFault fault ? fault.Code : "BENCHMARK_ERROR"; message = error.Message; stopped = true; }
                results.Add(new(caseId, tool, "MCP", status, watch.Elapsed.TotalMilliseconds, approvalMs, errorCode, message, checks.ToArray(), calls.ToArray()));
                progress?.Invoke(new($"{tool}: {status}" + (errorCode is null ? "" : " · " + errorCode), document, "case_finished"));
            }
        }

        private async Task ToolCase(string tool, CancellationToken ct)
        {
            switch (tool)
            {
                case "cad_csharp":
                {
                    var made = await Mutate(tool, new { code = CSharpFixture }, ct);
                    Is("required checkpoint", made.TryGetProperty("checkpoint", out var cp) && cp.Text("path") is { Length: > 0 } path && File.Exists(path), made);
                    extra = made.GetProperty("result").GetProperty("value"); extraReady = true;
                    var line = await Tool("cad_entity_get", new { handle = Extra("line") }, ct);
                    Number("compiled C# line length", 100, line.GetProperty("length").GetDouble()); Point("compiled C# line start", line.GetProperty("start"), 9000, 0);
                    var circle = await Tool("cad_entity_get", new { handle = Extra("circle") }, ct); Number("compiled C# circle radius", 10, circle.GetProperty("radius").GetDouble());
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CSharpFixture)));
                    Is("compiled source hash", made.Text("source_sha256") == hash, made); break;
                }
                case "cad_documents":
                {
                    var response = await Call(tool, new { }, ct); Success(response); var docs = Data(response).EnumerateArray().ToArray();
                    Number("isolated document identity", 1, docs.Count(d => d.Text("document_id") == document));
                    Is("unique document ids", docs.Select(d => d.Text("document_id")).Distinct().Count() == docs.Length, docs); break;
                }
                case "cad_context":
                { var d = await Tool(tool, new { }, ct); Is("millimeters and Model", d.Text("units") == "Millimeters" && d.Text("space") == "model", d); break; }
                case "cad_catalog":
                { await CatalogCase(ct); break; }
                case "cad_outline":
                { var d = await Tool(tool, new { text_sample = 40 }, ct); Is("nonempty fixture entity count", d.GetProperty("model_entities").GetInt32() >= 20, d); Is("fixture Unicode text", d.GetRawText().Contains("114", StringComparison.Ordinal), d); break; }
                case "cad_sessions":
                { await SessionsCase(ct); break; }
                case "cad_runtime_status":
                { var d = await Tool(tool, new { }, ct); var a = d.GetProperty("document_availability"); Is("availability belongs to fixture", a.Text("document_id") == document && a.GetProperty("read_available").GetBoolean(), a); break; }
                case "cad_diagnostics":
                { await DiagnosticsCase(ct); break; }
                case "cad_operation_list":
                { var d = await Tool(tool, new { limit = 100 }, ct); var operations = d.GetProperty("operations").EnumerateArray().ToArray(); Is("own completed operation receipts", operations.Any(o => o.Text("state") == "completed") && operations.All(o => o.Text("owner_id") == Owner && o.Text("document_id") == document), d); break; }
                case "cad_operation_status":
                { var list = await Tool("cad_operation_list", new { limit = 100 }, ct); string operation = list.GetProperty("operations").EnumerateArray().First(o => o.Text("state") == "completed").Text("operation_id")!; var d = await Tool(tool, new { operation_id = operation }, ct); Is("exact completed receipt", d.Text("operation_id") == operation && d.Text("state") == "completed" && (!d.GetProperty("result").TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null), d); break; }
                case "cad_snapshot": snapshotReady = false; await EnsureSnapshot(ct); break;
                case "cad_query":
                { await EnsureSnapshot(ct); var expected = (await Search(new { scope = "model", limit = 3, details = true }, ct, true)).GetProperty("entities").EnumerateArray().Select(e => e.Text("handle")).ToArray(); var d = await Tool(tool, new { snapshot_id = snapshot.Text("snapshot_id"), offset = 0, limit = 3 }, ct); Is("query captured snapshot identity and revision", d.Text("snapshot_id") == snapshot.Text("snapshot_id") && d.GetProperty("captured_revision").GetInt64() == snapshotRevision && !d.GetProperty("historical").GetBoolean(), d); var found = d.GetProperty("entities").EnumerateArray().Select(e => e.Text("handle")).ToArray(); Is("three exact live handles", found.Length == 3 && found.SequenceEqual(expected), found); Number("query next offset", 3, d.GetProperty("pagination").GetProperty("next_offset").GetDouble()); break; }
                case "cad_changes":
                { var d = await Tool(tool, new { since_revision = 0L, limit = 500, source = "cad_mcp" }, ct); Is("actual fixture change handles", d.GetRawText().Contains(Extra("line"), StringComparison.OrdinalIgnoreCase), d); break; }
                case "cad_text_units":
                { await TextUnitsCase(ct); break; }
                case "cad_edit_help":
                { var d = await Tool(tool, new { }, ct); Is("real operation contract", d.GetProperty("operations").TryGetProperty("line", out _) && d.GetProperty("operations").TryGetProperty("table_create", out _) && d.GetProperty("operations").TryGetProperty("box", out _), d); break; }
                case "cad_spds_help":
                { var d = await Tool(tool, new { }, ct); Is("SPDS templates and standards", d.GetProperty("templates").GetArrayLength() > 0 && d.GetProperty("standards").GetArrayLength() == 3, d); break; }
                case "cad_steel_catalog":
                { var d = await Tool(tool, new { }, ct); Number("steel density", 7850, d.GetProperty("density_kg_m3").GetDouble()); Is("known steel section", d.GetProperty("sections").EnumerateArray().Any(s => (s.Text("code") ?? s.Text("Code")) == "20Б1"), d); break; }
                case "cad_solid_get":
                { var d = await Tool(tool, new { handle = Extra("solid") }, ct); Number("actual box volume", 6000, d.GetProperty("volume").GetDouble()); Is("native topology", d.GetProperty("edges").GetArrayLength() >= 12 && d.GetProperty("faces").GetArrayLength() >= 6, d); break; }
                case "cad_verify":
                { await VerifyCase(ct); break; }
                case "cad_render": frameReady = false; await EnsureFrame(ct); break;
                case "cad_image_register":
                { await EnsureFrame(ct); var d = await Tool(tool, new { image_id = frame.Text("image_id"), control_points_json = "[{\"pixel\":[0,0],\"world\":[0,0,0]},{\"pixel\":[100,0],\"world\":[100,0,0]},{\"pixel\":[0,100],\"world\":[0,-100,0]}]" }, ct); Number("affine residual", 0, d.GetProperty("rms_error").GetDouble()); break; }
                case "cad_image_point":
                { await ToolCase("cad_image_register", ct); var d = await Tool(tool, new { image_id = frame.Text("image_id"), points_json = "[[50,50]]" }, ct); Point("registered control point", d.GetProperty("coordinates")[0], 50, -50); break; }
                case "cad_reference_calibrate":
                { await EnsureFrame(ct); referencePng = frame.Text("local_image_path"); var d = await Tool(tool, new { image_path = referencePng, control_points_json = "[{\"pixel\":[0,0],\"world\":[0,0,0]},{\"pixel\":[100,0],\"world\":[100,0,0]}]" }, ct); referenceId = d.Text("reference_id"); Is("reference bound to real image", referenceId is { Length: > 0 }, d); Number("reference residual", 0, d.GetProperty("rms_error").GetDouble()); break; }
                case "cad_reference_point":
                { if (referenceId is null) await ToolCase("cad_reference_calibrate", ct); var d = await Tool(tool, new { reference_id = referenceId, points_json = "[[50,50]]" }, ct); Point("reference point", d.GetProperty("coordinates")[0], 50, -50); break; }
                case "cad_reference_compare":
                { await CompareCase(ct); break; }
                case "cad_pdf_read":
                { var d = await Tool(tool, new { path = referencePdf, page = 1, max_chars = 12, render = true }, ct); Number("PDF pages", 2, d.GetProperty("page_count").GetDouble()); Is("actual PDF text page", d.Text("text") == "BENCHMARK PD", d); Is("actual page image", d.GetProperty("mcp_images")[0].GetProperty("valid_png_header").GetBoolean() && !d.TryGetProperty("render_error", out _), d); var second = await Tool(tool, new { path = referencePdf, page = 2, render = false }, ct); Is("second PDF page", second.Text("text")!.Contains("PAGE TWO 456", StringComparison.Ordinal), second); break; }
                case "cad_web_download": await DownloadCase(ct); break;
                case "cad_file_inspect": await FileInspectCase(ct); break;
                case "cad_cancel": await CancelCase(ct); break;
                case "cad_focus": await FocusCase(ct); break;
                case "cad_edit_preview": await PreviewCase(ct); break;
                case "cad_properties": await PropertiesCase(ct); break;
                case "cad_review": await ReviewCase(ct); break;
                case "cad_assembly_get": await AssemblyCase(ct); break;
                case "cad_table_dependencies":
                { await TableDependenciesCase(ct); break; }
                case "cad_result_get": await ArchiveCase(ct); break;
                case "cad_export": await ExportCase(ct); break;
                case "cad_release_check":
                { await EnsureSheet(ct); var d = await Tool(tool, new { layouts_json = JsonSerializer.Serialize(new[] { sheet }) }, ct); Is("sheet preflight names exact fixture", d.GetProperty("expected_layouts").EnumerateArray().Any(n => n.GetString() == sheet), d); Is("missing title block not declared ready", d.GetProperty("errors").GetArrayLength() + d.GetProperty("warnings").GetArrayLength() > 0, d); break; }
                case "cad_publish": await PublishCase(ct); break;
                case "cad_vertical_capabilities":
                { var d = await Tool(tool, new { }, ct); Is("explicit Civil and Map availability", d.GetRawText().Contains("civil", StringComparison.OrdinalIgnoreCase) && d.GetRawText().Contains("map", StringComparison.OrdinalIgnoreCase), d); break; }
                case "cad_vertical_catalog":
                case "cad_vertical_get":
                case "cad_proxies": await ConditionalCase(tool, ct); break;
                default: throw new Fault("BENCHMARK_CASE_MISSING", "tools/list advertised a tool without an implemented verification scenario: " + tool);
            }
        }
    }
}
