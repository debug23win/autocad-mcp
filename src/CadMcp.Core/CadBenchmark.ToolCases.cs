using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CadMcp.Core;
public sealed partial class CadBenchmark
{
    private sealed partial class Run
    {
        private async Task DownloadCase(CancellationToken ct)
        {
            byte[] pdf = File.ReadAllBytes(referencePdf!);
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serve = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(serverCancellation.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                string first = await reader.ReadLineAsync(serverCancellation.Token) ?? "";
                if (!first.StartsWith("GET /reference.pdf ", StringComparison.Ordinal)) throw new IOException("Unexpected reference download request");
                for (int i = 0; i < 100; i++) if (string.IsNullOrEmpty(await reader.ReadLineAsync(serverCancellation.Token))) break;
                byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/pdf\r\nContent-Length: " + pdf.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, serverCancellation.Token); await stream.WriteAsync(pdf, serverCancellation.Token);
            }, serverCancellation.Token);
            try
            {
                var d = await Tool("cad_web_download", new { url = "http://127.0.0.1:" + port + "/reference.pdf" }, ct);
                await serve.WaitAsync(TimeSpan.FromSeconds(10), ct);
                string path = d.Text("path") ?? throw new Fault("DOWNLOAD_FILE_MISSING", "Downloader did not return a local path");
                Is("downloaded exact bytes", File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(pdf), d);
                Is("downloaded exact SHA256", string.Equals(d.Text("sha256"), Convert.ToHexString(SHA256.HashData(pdf)), StringComparison.OrdinalIgnoreCase), d);
                var read = await Tool("cad_pdf_read", new { path, render = false }, ct); Number("downloaded PDF readable", 2, read.GetProperty("page_count").GetDouble());
            }
            finally { serverCancellation.Cancel(); listener.Stop(); try { await serve; } catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { } }
        }
        private async Task FileInspectCase(CancellationToken ct)
        {
            var list = await Tool("cad_operation_list", new { limit = 100 }, ct);
            string? operationId = list.GetProperty("operations").EnumerateArray().FirstOrDefault(o => o.Text("operation") == "cad_csharp" && o.Text("state") == "completed").Text("operation_id");
            if (operationId is null) throw new Fault("PREREQUISITE_MISSING", "Nonempty saved checkpoint is required", skipped: true);
            var status = await Tool("cad_operation_status", new { operation_id = operationId }, ct);
            string path = status.GetProperty("result").GetProperty("data").GetProperty("checkpoint").Text("path")!;
            byte[] before = SHA256.HashData(File.ReadAllBytes(path));
            var page1 = await Tool("cad_file_inspect", new { path, layer = "B_CSHARP", scope = "model", limit = 1 }, ct, allowIncomplete: true);
            var first = page1.GetProperty("entities")[0]; Number("saved line length", 100, first.GetProperty("length").GetDouble());
            Number("saved-file next page", 1, page1.GetProperty("next_offset").GetDouble());
            var page2 = await Tool("cad_file_inspect", new { path, layer = "B_CSHARP", scope = "model", limit = 1, offset = 1 }, ct, allowIncomplete: true);
            Number("saved circle radius", 10, page2.GetProperty("entities")[0].GetProperty("radius").GetDouble());
            Is("reference bytes unchanged", before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), path);
        }
        private async Task CancelCase(CancellationToken ct)
        {
            var gate = await Tool("benchmark_gate", new { }, ct); string operation = gate.Text("operation_id")!;
            var pending = await Tool("cad_operation_status", new { operation_id = operation }, ct);
            Is("real accepted operation waits before execution", pending.Text("state") == "queued" && pending.Text("phase") == "benchmark_waiting_gate", pending);
            await Tool("cad_cancel", new { operation_id = operation }, ct);
            var settled = await Tool("benchmark_settle_gate", new { }, ct); Is("cancellation observed before gate release", settled.GetProperty("cancelled_before_execution").GetBoolean(), settled);
            var receipt = await Tool("cad_operation_status", new { operation_id = operation }, ct);
            Is("durable cancelled receipt", receipt.Text("state") == "failed" || receipt.Text("state") == "cancelled", receipt);
            Is("cancellation code", receipt.GetProperty("result").GetProperty("error").Text("code") == "CANCELLED", receipt);
            var after = await Search(new { scope = "model", bounds = new { min = new[] { 29999, 29999, -1 }, max = new[] { 30101, 30001, 1 } }, limit = 500 }, ct);
            Number("cancelled line never created", 0, after.GetProperty("entities").GetArrayLength());
        }
        private async Task FocusCase(CancellationToken ct)
        {
            await Tool("benchmark_activate", new { restore = false }, ct);
            try
            {
                var before = await Call("cad_context", new { }, ct); Success(before);
                await Tool("cad_focus", new { handle = Extra("line") }, ct);
                var after = await Call("cad_context", new { }, ct); Success(after);
                Is("focus retains fixture scope and revision", before.Revision == after.Revision && after.DocumentId == document, after);
                Is("only requested handle selected", Data(after).GetProperty("selection").GetProperty("handles").EnumerateArray().Select(h => h.GetString()).SequenceEqual(new[] { Extra("line") }), Data(after));
            }
            finally { using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await Tool("benchmark_activate", new { restore = true }, restore.Token); }
        }
        private async Task PreviewCase(CancellationToken ct)
        {
            var d = await Tool("cad_edit_preview", new { operations_json = "[{\"op\":\"line\",\"start\":[25000,25000],\"end\":[25100,25000]}]" }, ct);
            Is("preview has plan proof", d.Text("plan_hash") is { Length: 64 }, d);
            var read = await Search(new { scope = "model", bounds = new { min = new[] { 24999, 24999, -1 }, max = new[] { 25101, 25001, 1 } }, limit = 500 }, ct);
            Number("preview did not commit line", 0, read.GetProperty("entities").GetArrayLength());
        }
        private async Task PropertiesCase(CancellationToken ct)
        {
            var d = await Tool("cad_properties", new { handles_json = JsonSerializer.Serialize(new[] { Extra("line") }), max_properties = 300 }, ct);
            var item = d.GetProperty("objects")[0]; Is("correct native properties object", item.Text("handle") == Extra("line") && item.Text("type") == "Line", item);
            var palette = item.GetProperty("palette");
            if (palette.TryGetProperty("error", out var error)) throw new Fault("OPM_UNAVAILABLE", error.ToString(), skipped: true);
            if (palette.TryGetProperty("truncated", out var cut) && cut.GetBoolean()) throw new Fault("INCOMPLETE_EVIDENCE", "Properties palette was truncated");
            bool length = palette.GetProperty("categories").EnumerateArray().SelectMany(c => c.GetProperty("properties").EnumerateArray()).Any(p =>
            {
                string name = p.Text("name") ?? "";
                if (!name.Contains("length", StringComparison.OrdinalIgnoreCase) && !name.Contains("длина", StringComparison.OrdinalIgnoreCase) && !name.Contains("länge", StringComparison.OrdinalIgnoreCase)) return false;
                var value = p.GetProperty("value");
                if (value.ValueKind == JsonValueKind.Number) return Math.Abs(value.GetDouble() - 100) < 1e-6;
                string text = (value.GetString() ?? "").Split(' ', '\u00a0')[0];
                return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) && Math.Abs(n - 100) < 1e-6 ||
                    double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), out n) && Math.Abs(n - 100) < 1e-6;
            });
            Is("actual palette length property equals 100", length, palette);
        }
        private async Task ReviewCase(CancellationToken ct)
        {
            var d = await Tool("cad_review", new { handles_json = JsonSerializer.Serialize(new[] { Extra("duplicateA"), Extra("duplicateB") }), options_json = "{\"checks\":[\"topology\"]}" }, ct);
            var topology = d.GetProperty("topology");
            Is("known duplicate pair is detected", topology.GetProperty("findings").EnumerateArray().Any(f => f.Text("code") == "DUPLICATE_GEOMETRY" &&
                f.GetProperty("handles").EnumerateArray().Select(h => h.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(new[] { Extra("duplicateA"), Extra("duplicateB") })), topology);
            var clean = await Tool("cad_review", new { handles_json = JsonSerializer.Serialize(new[] { Extra("line"), Extra("circle") }), options_json = "{\"checks\":[\"topology\"]}" }, ct);
            Is("clean control does not report duplicates", clean.GetProperty("topology").GetProperty("findings").EnumerateArray().All(f => f.Text("code") != "DUPLICATE_GEOMETRY"), clean);
        }
        private async Task CatalogCase(CancellationToken ct)
        {
            var d = (await Tool("cad_catalog", new { }, ct)).GetProperty("catalog");
            Is("known unlocked layer", d.GetProperty("layers").EnumerateArray().Any(l => l.Text("name") == "B_CSHARP" && !l.GetProperty("locked").GetBoolean() && !l.GetProperty("off").GetBoolean()), d);
            Is("known block definition", d.GetProperty("blocks").EnumerateArray().Any(b => b.Text("name") == "B_DEF"), d);
        }
        private async Task SessionsCase(CancellationToken ct)
        {
            var d = await Tool("cad_sessions", new { }, ct);
            Is("live worker and process discovered", d.EnumerateArray().Any(s => s.GetProperty("worker").Text("session_id") == session && s.GetProperty("worker").GetProperty("process_id").GetInt32() > 0 && s.GetProperty("reachable").GetBoolean() && s.TryGetProperty("context", out var context) && context.Text("status") == "completed" && context.Text("session_id") == session && context.Text("document_id") == document), d);
        }
        private async Task DiagnosticsCase(CancellationToken ct)
        {
            await EnsureSheet(ct);
            var d = await Tool("cad_diagnostics", new { }, ct);
            Is("diagnostics scope", d.Text("session_id") == session, d);
            var record = d.GetProperty("last_operations").EnumerateArray().Single(r => r.Text("document_id") == document && r.Text("session_id") == session);
            Is("own completed safety record", record.Text("owner_id") == Owner && record.Text("operation_id")!.StartsWith("bench_" + id, StringComparison.Ordinal) && record.Text("phase") == "completed", record);
            var checkpoint = record.GetProperty("checkpoint"); string path = checkpoint.Text("path")!;
            Is("checkpoint exists with matching SHA256", File.Exists(path) && string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), checkpoint.Text("sha256"), StringComparison.OrdinalIgnoreCase), checkpoint);
        }
        private async Task TextUnitsCase(CancellationToken ct)
        {
            var d = await Tool("cad_text_units", new { scope = "model", layers_json = "[\"B_TEXT\"]", group_lines = false, limit = 50 }, ct);
            var units = d.GetProperty("units").EnumerateArray().ToArray(); Number("exact text unit count", 3, units.Length);
            Is("all three exact Unicode replacements", units.All(u => u.Text("text") == "Труба Ø114" && u.Text("layer") == "B_TEXT"), units);
        }
        private async Task TableDependenciesCase(CancellationToken ct)
        {
            var tables = (await Search(new { scope = "model", layer = "B_TABLES", limit = 500, details = true }, ct)).GetProperty("entities").EnumerateArray().ToArray();
            Number("two linked tables", 2, tables.Length); var handles = tables.Select(t => t.Text("handle")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var d = await Tool("cad_table_dependencies", new { }, ct); var cells = d.GetProperty("cells").EnumerateArray().ToDictionary(c => c.Text("id")!, c => c.Text("table")!);
            Is("persisted identities for both real tables", handles.IsSubsetOf(cells.Values.ToHashSet(StringComparer.OrdinalIgnoreCase)), cells);
            Is("actual cross-table mass reference", d.GetProperty("formulas").EnumerateArray().Any(f => cells.TryGetValue(f.Text("cell")!, out var target) && f.GetProperty("references").EnumerateArray().Any(r => r.Text("name") == "mass" && cells.TryGetValue(r.Text("cell")!, out var source) && source != target && handles.Contains(source) && handles.Contains(target))), d);
        }
        private async Task CompareCase(CancellationToken ct)
        {
            await EnsureFrame(ct); string path = frame.Text("local_image_path")!;
            const string same = "[[0.1,0.1],[0.9,0.1],[0.9,0.9],[0.1,0.9]]";
            var match = (await Tool("cad_reference_compare", new { reference_image_path = path, rendered_image_path = path, reference_contour_json = same, rendered_contour_json = same }, ct)).GetProperty("comparison");
            Number("identical supplied contour IoU", 1, match.GetProperty("silhouette_iou").GetDouble()); Number("identical contour deviation", 0, match.GetProperty("maximum_sampled_boundary_deviation").GetDouble());
            var mismatch = (await Tool("cad_reference_compare", new { reference_image_path = path, rendered_image_path = path, reference_contour_json = "[[0.1,0.1],[0.2,0.1],[0.2,0.2],[0.1,0.2]]", rendered_contour_json = "[[0.7,0.7],[0.8,0.7],[0.8,0.8],[0.7,0.8]]" }, ct)).GetProperty("comparison");
            Number("disjoint supplied contour IoU", 0, mismatch.GetProperty("silhouette_iou").GetDouble()); Is("disjoint contour control fails", mismatch.Text("state") == "failed" && mismatch.GetProperty("maximum_sampled_boundary_deviation").GetDouble() > .5, mismatch);
        }
        private async Task AssemblyCase(CancellationToken ct)
        {
            var made = await Edit([new { op = "layer", name = "B_ASSEMBLY" }, new { op = "assembly_create", id = "beam", kind = "beam", position = new[] { 12000, 0, 0 }, parameters = new { length = 1000, width = 100, height = 200, web_thickness = 5, flange_thickness = 8, root_radius = 0 }, mark = "B-1", material = "S235", density = 7850, show_annotations = false, layer = "B_ASSEMBLY" }], ct);
            string handle = Handle(made, "beam"); var d = await Tool("cad_assembly_get", new { handle }, ct);
            Number("I-beam exact solid volume m3", .00252, d.GetProperty("solid_volume_m3").GetDouble()); Number("I-beam exact solid mass kg", 19.782, d.GetProperty("solid_mass_kg").GetDouble());
            Is("persisted assembly recipe", d.GetProperty("recipe").Text("mark") == "B-1" && d.GetProperty("recipe").Text("material") == "S235", d);
        }
        private async Task VerifyCase(CancellationToken ct)
        {
            string line = Extra("line"), circle = Extra("circle");
            var d = await Tool("cad_verify", new { handles_json = JsonSerializer.Serialize(new[] { line, circle }), expectations_json = JsonSerializer.Serialize(new { entity_count = 2,
                checks = new object[] { new { handle = line, property = "length", expected = 100 }, new { handle = circle, property = "radius", expected = 10 } } }) }, ct);
            var v = d.GetProperty("verification"); var verified = v.GetProperty("checks").EnumerateArray().ToArray();
            Is("exact verification status and counts", v.Text("state") == "passed" && v.Text("overall_state") == "passed" && v.GetProperty("entity_count").GetInt32() == 2 &&
                v.GetProperty("passed").GetInt32() == 3 && v.GetProperty("failed").GetInt32() == 0 && v.GetProperty("unverified").GetInt32() == 0 && verified.Length == 3 && verified.All(c => c.Text("status") == "passed"), v);
            foreach (var (label, expected) in new[] { ("Live entity count", 2d), (line + ": length", 100d), (circle + ": radius", 10d) })
            {
                var check = verified.Single(c => c.Text("label") == label);
                Number(label + " expected", expected, check.GetProperty("expected").GetDouble()); Number(label + " actual", expected, check.GetProperty("actual").GetDouble());
            }
            var entities = d.GetProperty("entities").EnumerateArray().ToArray();
            Is("readback exact handle set", entities.Length == 2 && entities.Select(e => e.Text("handle")).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(new[] { line, circle }), entities);
            Number("verify line readback", 100, entities.Single(e => e.Text("handle") == line).GetProperty("length").GetDouble());
            Number("verify circle readback", 10, entities.Single(e => e.Text("handle") == circle).GetProperty("radius").GetDouble());
        }
        private async Task ArchiveCase(CancellationToken ct)
        {
            Extra("line"); // require the large MText fixture created by real C#
            var response = await ReadCall("cad_search", new { options_json = "{\"scope\":\"model\",\"layer\":\"B_ARCHIVE\",\"details\":true,\"limit\":500}" }, ct);
            if (response.Error is not null || response.Status is not ("completed" or "partial")) Success(response);
            var envelope = Data(response);
            string archive = envelope.Text("archive_id") ?? throw new Fault("ARCHIVE_NOT_CREATED", "Large response did not create a real archive");
            int offset = 0; var text = new StringBuilder(); bool eof = false;
            for (int i = 0; i < 200; i++)
            {
                var page = await Tool("cad_result_get", new { archive_id = archive, offset, limit = 32000 }, ct);
                Is("archive identity and historical scope", page.Text("archive_id") == archive && page.GetProperty("historical").GetBoolean(), page);
                text.Append(page.Text("text"));
                if (!page.TryGetProperty("next_offset", out var next) || next.ValueKind == JsonValueKind.Null) { eof = true; break; }
                int position = next.GetInt32(); if (position <= offset) throw new Fault("ARCHIVE_PAGING", "Archive cursor did not advance"); offset = position;
            }
            if (!eof) throw new Fault("INCOMPLETE_EVIDENCE", "Archive did not reach terminal EOF within the bounded page count");
            using var parsed = JsonDocument.Parse(text.ToString()); var entities = Body(parsed.RootElement).GetProperty("entities").EnumerateArray().ToArray();
            Number("all archived MText reconstructed", 200, entities.Length);
            Is("unique archive handles", entities.Select(e => e.Text("handle")).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 200, entities.Length);
            foreach (int index in Enumerable.Range(0, 200)) Is("archive marker " + index, entities.Count(e => e.GetRawText().Contains("ARCHIVE_" + index.ToString("D3") + "_", StringComparison.Ordinal)) == 1, index);
        }
        private async Task ExportCase(CancellationToken ct)
        {
            string path = Path.Combine(fullRoot!, "benchmark-export.dxf");
            await Mutate("cad_export", new { format = "dxf", path }, ct);
            Is("DXF file actually exists", File.Exists(path) && new FileInfo(path).Length > 100, path);
            byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
            var d = await Tool("cad_file_inspect", new { path, layer = "B_REALCS", limit = 10 }, ct);
            var entities = d.GetProperty("entities").EnumerateArray().ToArray(); Number("DXF geometry count", 2, entities.Length);
            Number("DXF line actual length", 100, entities.Single(e => e.Text("type") == "Line").GetProperty("length").GetDouble()); Number("DXF circle actual radius", 10, entities.Single(e => e.Text("type") == "Circle").GetProperty("radius").GetDouble());
            Is("DXF inspection did not alter source", hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), path);
        }
        private async Task PublishCase(CancellationToken ct)
        {
            await EnsureSheet(ct, configure: true);
            // A test sheet must contain actual content before publication.
            await Edit([new { op = "line", start = new[] { 10, 10 }, end = new[] { 110, 10 }, layout = sheet }], ct);
            string folder = Path.Combine(fullRoot!, "published");
            Directory.CreateDirectory(folder);
            JsonElement result;
            try { result = await Mutate("cad_publish", new { output_folder = folder, layouts_json = JsonSerializer.Serialize(new[] { sheet }) }, ct); }
            catch (Fault fault) when (fault.Code is "PLOT_DEVICE_UNAVAILABLE" or "PLOT_MEDIA_UNAVAILABLE" or "PLOT_DEVICE_NOT_FOUND") { throw new Fault(fault.Code, fault.Message, skipped: true); }
            string[] pdfs = Directory.GetFiles(folder, "*.pdf"); Number("one actual published sheet", 1, pdfs.Length);
            var verified = Wire.Element(PdfVerification.Check(pdfs[0], 1));
            Is("published PDF has page content", verified.GetProperty("warnings").GetArrayLength() == 0, verified);
            Is("published file register names actual PDF", result.GetRawText().Contains(Path.GetFileName(pdfs[0]), StringComparison.Ordinal), result);
        }
        private async Task ConditionalCase(string tool, CancellationToken ct)
        {
            if (tool == "cad_proxies")
            {
                var observed = await Tool(tool, new { max_samples = 20 }, ct);
                Is("actual proxy scan completed", observed.GetProperty("handles_scanned").GetInt64() > 0, observed);
                throw new Fault("CONDITIONAL_FIXTURE_REQUIRED", "Proxy enumeration executed, but this generated fixture has no proprietary proxy object for positive detail/graphics evidence", skipped: true);
            }
            var catalog = await Tool("cad_vertical_catalog", new { }, ct);
            if (tool == "cad_vertical_catalog")
            {
                Is("explicit vendor availability", catalog.GetProperty("civil3d").TryGetProperty("available", out _) && catalog.GetProperty("map3d").TryGetProperty("available", out _), catalog);
            }
            else
            {
                var observed = await Tool(tool, new { handle = Extra("line") }, ct);
                Is("ordinary line is not a Civil object", observed.GetProperty("civil3d").ValueKind == JsonValueKind.False && observed.Text("handle") == Extra("line"), observed);
            }
            throw new Fault("CONDITIONAL_FIXTURE_REQUIRED", "Vendor discovery/read executed; a real Civil/Map object fixture is required for positive vendor-geometry or OD evidence", skipped: true);
        }
    }
}
