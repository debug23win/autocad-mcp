using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CadMcp.Core;

namespace CadMcp.Tests;

// Independent protocol/evidence tests. Fake CAD supplies controlled observations;
// these tests do not execute AutoCAD and do not claim native compatibility.
public sealed class CadBenchmarkTests
{
    private static Task<CadBenchmarkResult> Run(FakeCad cad, CancellationToken ct = default) =>
        new CadBenchmark(cad.Send, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(80)).RunAsync("session", ct: ct);

    [Fact]
    public async Task Positive_control_reads_actual_evidence_and_reports_consistent_totals_and_timing()
    {
        var cad = new FakeCad();
        var watch = Stopwatch.StartNew();
        var result = await Run(cad);
        Assert.Equal(8, result.Total);
        Assert.Equal(8, result.Passed);
        Assert.True(result.Complete);
        Assert.Equal(result.Total, result.Passed + result.Failed + result.Skipped + result.Unknown);
        Assert.InRange(result.ElapsedMs, 0, watch.Elapsed.TotalMilliseconds);
        Assert.True(result.ElapsedMs >= result.Cases.Sum(c => c.ElapsedMs));
        Assert.Equal(cad.Requests.Count, result.CadCalls);
        Assert.Equal(2, result.SetupCalls!.Count);
        Assert.All(result.Cases, item =>
        {
            Assert.NotEmpty(item.Checks);
            Assert.All(item.Checks, check => Assert.True(check.Passed, check.Name));
            Assert.InRange(item.ApprovalWaitMs, 0, item.ElapsedMs);
            Assert.Contains(item.Calls, c => c.Request.Operation is "cad_search" or "cad_entity_get" or "cad_table_get");
        });
        var mutations = cad.Requests.Where(r => r.Operation is "cad_edit" or "cad_lisp").ToArray();
        Assert.All(mutations, r => { Assert.Equal("fixture", r.DocumentId); Assert.Equal("session", r.SessionId); Assert.NotNull(r.ExpectedRevision); });
        Assert.Equal(mutations.Length, mutations.Select(r => r.Data.Text("operation_id")).Distinct().Count());
        Assert.Single(mutations, r => r.Operation == "cad_lisp");
        Assert.Contains(result.Cases, c => c.Language == "C#");
        Assert.Contains(result.Cases, c => c.Language == "AutoLISP");
    }

    [Fact]
    public async Task Generated_native_plans_and_expectations_pass_the_real_production_validators()
    {
        var cad = new FakeCad();
        Assert.True((await Run(cad)).Complete);
        var edits = cad.Requests.Where(r => r.Operation == "cad_edit").ToArray();
        Assert.NotEmpty(edits);
        foreach (var request in edits)
        {
            var operations = EditPlan.Parse(request.Data.Text("operations_json")!);
            Assert.NotEmpty(operations);
            DrawingVerification.Parse(request.Data.Text("expectations_json"));
        }
    }

    [Fact]
    public async Task Generated_lisp_passes_real_validators_and_contains_owned_model_geometry_on_the_test_layer()
    {
        var cad = new FakeCad();
        Assert.True((await Run(cad)).Complete);
        var request = Assert.Single(cad.Requests, r => r.Operation == "cad_lisp");
        string code = request.Data.Text("code")!;
        LispScript.ValidateBody(code);
        var findings = LispPolicy.Scan(code);
        LispPolicy.EnsureRunnable(findings);
        Assert.DoesNotContain(findings, finding => finding.Severity is LispPolicy.Blocked or LispPolicy.Risky);

        // Parse the literal data only. No AutoLISP is executed by this test.
        var forms = LispForms(code);
        var creation = Descendants(forms).Where(form => form.FirstOrDefault() is string name && name == "entmake").ToArray();
        Assert.Equal(3, creation.Length);
        Assert.DoesNotContain(Descendants(forms), form => form.FirstOrDefault() is string name && name is "entmakex" or "command" or "command-s" or "setvar" or "load");
        var records = creation.Select(form =>
        {
            Assert.Equal(2, form.Count);
            var quote = Assert.IsType<List<object>>(form[1]);
            Assert.Equal("quote", quote[0]);
            var data = Assert.IsType<List<object>>(quote[1]);
            return data.Cast<List<object>>().ToDictionary(entry => int.Parse((string)entry[0], CultureInfo.InvariantCulture));
        }).ToDictionary(record => Scalar(record[0]));
        Assert.Equal(new[] { "CIRCLE", "LAYER", "LINE" }, records.Keys.Order().ToArray());
        Assert.Equal("B_LISP", Scalar(records["LAYER"][2]));
        foreach (string type in new[] { "LINE", "CIRCLE" })
        {
            Assert.Equal("B_LISP", Scalar(records[type][8]));
            // No paper-space flag is allowed. The runner separately verifies Model
            // before the mutation; entmake then attaches graphics to that space.
            if (records[type].TryGetValue(67, out var paper)) Assert.Equal("0", Scalar(paper));
        }
        Assert.Equal(new[] { 0d, 5000d, 0d }, Vector(records["LINE"][10]));
        Assert.Equal(new[] { 100d, 5000d, 0d }, Vector(records["LINE"][11]));
        Assert.Equal(new[] { 50d, 5050d, 0d }, Vector(records["CIRCLE"][10]));
        Assert.Equal(10, double.Parse(Scalar(records["CIRCLE"][40]), CultureInfo.InvariantCulture));
        static string Scalar(List<object> pair) { Assert.Equal(".", pair[1]); return (string)pair[2]; }
        static double[] Vector(List<object> entry) => entry.Skip(1).Cast<string>().Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    }

    private static List<object> LispForms(string code)
    {
        var tokens = Regex.Matches(code, "\"(?:\\\\.|[^\"])*\"|[()]|'|[^\\s()']+").Select(m => m.Value).ToArray();
        int at = 0;
        object Read()
        {
            string token = tokens[at++];
            if (token == "'") return new List<object> { "quote", Read() };
            if (token != "(") return token.StartsWith('"') ? JsonSerializer.Deserialize<string>(token)! : token;
            var list = new List<object>();
            while (tokens[at] != ")") list.Add(Read());
            at++; return list;
        }
        var forms = new List<object>();
        while (at < tokens.Length) forms.Add(Read());
        return forms;
    }

    private static IEnumerable<List<object>> Descendants(List<object> forms)
    {
        foreach (var form in forms.OfType<List<object>>())
        {
            yield return form;
            foreach (var nested in Descendants(form)) yield return nested;
        }
    }

    [Theory]
    [InlineData("missing_fixture_marker")]
    [InlineData("wrong_run_id")]
    [InlineData("existing_document")]
    [InlineData("wrong_fixture_session")]
    public async Task Invalid_fixture_never_reaches_a_mutation(string mode)
    {
        var cad = new FakeCad(mode);
        var result = await Run(cad);
        Assert.False(result.Complete);
        Assert.NotNull(result.SetupError);
        Assert.Equal(8, result.Skipped);
        Assert.DoesNotContain(cad.Requests, r => r.Operation is "cad_edit" or "cad_lisp");
    }

    [Theory]
    [InlineData("wrong_polyline_area", "polyline")]
    [InlineData("partial_polyline", "polyline")]
    [InlineData("duplicate_long_block_lines", "blocks")]
    [InlineData("duplicate_page_handle", "paging")]
    [InlineData("repeated_page_offset", "paging")]
    [InlineData("wrong_table_value", "tables")]
    [InlineData("wrong_lisp_geometry", "lisp")]
    public async Task A_successful_mutation_receipt_cannot_hide_wrong_or_incomplete_observations(string mode, string test)
    {
        var result = await Run(new FakeCad(mode));
        Assert.Equal("failed", result.Cases.Single(c => c.Id == test).Status);
        Assert.False(result.Complete);
    }

    [Fact]
    public async Task Unknown_accepted_mutation_stops_all_later_writes_without_resending_it()
    {
        var cad = new FakeCad("lost_first_mutation");
        var result = await Run(cad);
        Assert.Equal(1, result.Unknown);
        Assert.Equal(7, result.Skipped);
        Assert.Single(cad.Requests, r => r.Operation is "cad_edit" or "cad_lisp");
        Assert.Single(cad.Requests, r => r.Operation == "cad_operation_status");
        Assert.False(result.Complete);
    }

    [Theory]
    [InlineData("wrong_read_document")]
    [InlineData("missing_read_scope")]
    [InlineData("wrong_read_request")]
    public async Task An_unscoped_or_mismatched_read_stops_later_writes(string mode)
    {
        var cad = new FakeCad(mode);
        var result = await Run(cad);
        Assert.Equal(1, result.Unknown);
        Assert.Equal(7, result.Skipped);
        Assert.Single(cad.Requests, r => r.Operation is "cad_edit" or "cad_lisp");
    }

    [Theory]
    [InlineData("wrong_units")]
    [InlineData("paper_space")]
    public async Task A_changed_fixture_context_blocks_the_first_write(string mode)
    {
        var cad = new FakeCad(mode);
        var result = await Run(cad);
        Assert.Equal(1, result.Unknown);
        Assert.Equal(7, result.Skipped);
        Assert.DoesNotContain(cad.Requests, r => r.Operation is "cad_edit" or "cad_lisp");
    }

    [Theory]
    [InlineData("lisp_denied", "skipped")]
    [InlineData("lisp_declined_approval", "skipped")]
    [InlineData("lisp_failed", "failed")]
    [InlineData("lisp_receipt_missing", "unknown")]
    [InlineData("lisp_forever_queued", "unknown")]
    [InlineData("lisp_wrong_receipt_operation", "unknown")]
    [InlineData("lisp_inconsistent_receipt", "unknown")]
    [InlineData("lisp_wrong_terminal_scope", "unknown")]
    public async Task Non_successful_lisp_receipts_never_count_as_passed(string mode, string expected)
    {
        var cad = new FakeCad(mode);
        var result = await Run(cad);
        Assert.Equal(expected, result.Cases.Single(c => c.Id == "lisp").Status);
        Assert.False(result.Complete);
        Assert.Equal(7, result.Passed);
        Assert.Single(cad.Requests, r => r.Operation == "cad_lisp");
    }

    [Fact]
    public async Task Queued_lisp_requires_a_terminal_receipt_then_separate_geometry_read()
    {
        var cad = new FakeCad("lisp_queued_success");
        var result = await Run(cad);
        var item = result.Cases.Single(c => c.Id == "lisp");
        Assert.Equal("passed", item.Status);
        Assert.True(item.ApprovalWaitMs > 0);
        Assert.InRange(item.ApprovalWaitMs, 0, item.ElapsedMs);
        Assert.True(cad.ReceiptPolls >= 2);
        int lastReceipt = item.Calls.ToList().FindLastIndex(c => c.Request.Operation == "cad_operation_status");
        Assert.Contains(item.Calls.Skip(lastReceipt + 1), c => c.Request.Operation == "cad_search");
    }

    [Fact]
    public async Task Cancellation_after_a_mutation_was_accepted_is_unknown_not_success_or_safe_rollback()
    {
        using var stop = new CancellationTokenSource();
        var cad = new FakeCad("cancel_first_mutation") { Cancel = stop.Cancel };
        var result = await Run(cad, stop.Token);
        Assert.Equal("unknown", result.Cases[0].Status);
        Assert.Equal(7, result.Skipped);
        Assert.Single(cad.Requests, r => r.Operation is "cad_edit" or "cad_lisp");
    }

    [Fact]
    public async Task Double_start_is_rejected_before_the_second_fixture_is_created()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cad = new FakeCad();
        async Task<Response> Send(Request request, CancellationToken ct)
        {
            if (request.Operation == "cad_documents") { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
            return await cad.Send(request, ct);
        }
        var runner = new CadBenchmark(Send, TimeSpan.Zero, TimeSpan.FromMilliseconds(80));
        var first = runner.RunAsync("session");
        await entered.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync("session"));
        release.TrySetResult();
        await first;
        Assert.Single(cad.Requests, r => r.Operation == "benchmark_create");
    }

    private sealed class FakeCad(string mode = "")
    {
        public readonly List<Request> Requests = [];
        public Action? Cancel;
        public int ReceiptPolls;
        private readonly Dictionary<string, string> aliases = [];
        private readonly HashSet<string> erased = [];
        private int nextHandle = 100;
        private bool tableChanged;
        private Request? lisp;
        private Response Reply(Request r, object data, string status = "completed", string? doc = "fixture") =>
            new(r.RequestId, status, data, "session", doc, 1);

        public Task<Response> Send(Request r, CancellationToken ct)
        {
            Requests.Add(r);
            if (r.Operation == "cad_documents") return Task.FromResult(Reply(r, new[] { new { document_id = "user-drawing" } }, doc: null));
            if (r.Operation == "benchmark_create")
            {
                var data = new { benchmark_fixture = mode != "missing_fixture_marker", run_id = mode == "wrong_run_id" ? "wrong" : r.Data.Text("run_id"), path = "benchmark.dwg" };
                var reply = Reply(r, data, doc: mode == "existing_document" ? "user-drawing" : "fixture");
                return Task.FromResult(mode == "wrong_fixture_session" ? reply with { SessionId = "foreign" } : reply);
            }
            if (r.Operation == "cad_context") return Task.FromResult(Reply(r, new { units = mode == "wrong_units" ? "Inches" : "Millimeters", space = mode == "paper_space" ? "paper" : "model" }));
            if (r.Operation == "cad_edit")
            {
                if (Requests.Count(q => q.Operation == "cad_edit") == 1)
                {
                    if (mode == "lost_first_mutation") throw new IOException("accepted edit response lost");
                    if (mode == "cancel_first_mutation") { Cancel!(); throw new OperationCanceledException(ct); }
                }
                using var plan = JsonDocument.Parse(r.Data.Text("operations_json")!);
                var results = new List<object>();
                foreach (var op in plan.RootElement.EnumerateArray())
                {
                    if (op.Text("id") is { } alias)
                    {
                        string handle = (++nextHandle).ToString("X"); aliases[alias] = handle;
                        results.Add(new { id = alias, handle });
                    }
                    if (op.Text("op") == "erase") erased.Add(op.Text("handle")!);
                    if (op.Text("op") == "table_cells") tableChanged = true;
                }
                return Task.FromResult(Reply(r, new { result = new { transaction = "committed", results } }));
            }
            if (r.Operation == "cad_entity_get")
            {
                string handle = r.Data.Text("handle")!;
                if (erased.Contains(handle)) return Task.FromResult(Response.Fail(r, "ENTITY_NOT_FOUND", "erased"));
                if (aliases.GetValueOrDefault("poly") == handle)
                {
                    object data = new { closed = true, vertex_count = 4, length = 600, area = mode == "wrong_polyline_area" ? 19999 : 20000, partial = mode == "partial_polyline" };
                    var reply = Reply(r, data, doc: mode == "wrong_read_document" ? "user-drawing" : "fixture");
                    if (mode == "missing_read_scope") reply = reply with { SessionId = null, DocumentId = null };
                    if (mode == "wrong_read_request") reply = reply with { RequestId = "another-request" };
                    return Task.FromResult(reply);
                }
                if (aliases.GetValueOrDefault("source") == handle)
                    return Task.FromResult(Reply(r, new { type = "Line", layer = "B_TRANSFORM", length = 100, start = new[] { 4050, 0, 0 }, end = new[] { 4150, 0, 0 } }));
                return Task.FromResult(Reply(r, new { type = "Line", layer = "B_TRANSFORM", length = 200, start = new[] { 4000, 200, 0 }, end = new[] { 4000, 400, 0 } }));
            }
            if (r.Operation == "cad_takeoff") return Task.FromResult(Reply(r, new { total_length = 600, total_area = 20000 }));
            if (r.Operation == "cad_table_get")
            {
                bool source = r.Data.Text("handle") == aliases["source"];
                double value = source ? tableChanged ? 15 : 10 : tableChanged ? mode == "wrong_table_value" ? 29 : 30 : 20;
                return Task.FromResult(Reply(r, new { cells = new[] { new { cell = source ? "C1" : "A1", value } } }));
            }
            if (r.Operation == "cad_lisp")
            {
                lisp = r;
                if (mode == "lisp_denied") return Task.FromResult(Response.Fail(r, "LISP_DISABLED", "disabled"));
                return Task.FromResult(Reply(r, new { }, mode.StartsWith("lisp_", StringComparison.Ordinal) && mode != "wrong_lisp_geometry" ? "queued" : "completed"));
            }
            if (r.Operation == "cad_operation_status")
            {
                ReceiptPolls++;
                if (lisp is null) return Task.FromResult(Reply(r, new { operation_id = r.Data.Text("operation_id"), state = "unknown" }));
                if (mode == "lisp_forever_queued" || mode is "lisp_queued_success" or "lisp_declined_approval" && ReceiptPolls == 1)
                    return Task.FromResult(Reply(r, new { operation_id = r.Data.Text("operation_id"), state = "queued", phase = "awaiting_user_approval" }));
                if (mode == "lisp_receipt_missing") return Task.FromResult(Reply(r, new { operation_id = r.Data.Text("operation_id"), state = "completed" }));
                var terminal = Reply(lisp, new { return_value = "ok" });
                if (mode == "lisp_failed") terminal = terminal with { Status = "failed", Error = new("LISP_FAILED", "script failed") };
                if (mode == "lisp_declined_approval") terminal = Response.Fail(lisp, "LISP_DENIED", "declined by user");
                if (mode == "lisp_wrong_terminal_scope") terminal = terminal with { DocumentId = "user-drawing" };
                return Task.FromResult(Reply(r, new { operation_id = mode == "lisp_wrong_receipt_operation" ? "foreign-operation" : r.Data.Text("operation_id"), state = mode is "lisp_failed" or "lisp_inconsistent_receipt" or "lisp_declined_approval" ? "failed" : "completed", result = terminal }));
            }
            if (r.Operation == "cad_search") return Task.FromResult(Reply(r, Search(r)));
            throw new InvalidOperationException("Unexpected benchmark request: " + r.Operation);
        }

        private object Search(Request r)
        {
            using var doc = JsonDocument.Parse(r.Data.Text("options_json")!); var options = doc.RootElement;
            string layer = options.Text("layer")!;
            var entities = new List<object>(); var pagination = new Dictionary<string, object>();
            if (layer == "B_TEXT" && options.Text("text") == "Ø114")
                entities.AddRange(new object[] { new { type = "DBText", text = "Труба Ø114" }, new { type = "DBText", text = "Труба Ø114" }, new { type = "MText", text = "Труба Ø114" } });
            if (layer == "B_BLOCKS")
                foreach (var (alias, x, rotated) in new[] { ("one", 3500, false), ("two", 3700, true) })
                {
                    entities.Add(new { root_handle = aliases[alias], depth = 1, length = 100, start = new[] { x, 0, 0 }, end = new[] { rotated ? x : x + 100, rotated ? 100 : 0, 0 } });
                    bool duplicate = mode == "duplicate_long_block_lines";
                    entities.Add(new { root_handle = aliases[alias], depth = 1, length = duplicate ? 100 : 50, start = new[] { x, 0, 0 }, end = duplicate ? new[] { rotated ? x : x + 100, rotated ? 100 : 0, 0 } : new[] { rotated ? x - 50 : x, rotated ? 0 : 50, 0 } });
                }
            if (layer == "B_PAGES")
            {
                int offset = options.Number("offset", 0), count = Math.Min(5, 12 - offset);
                for (int n = offset; n < offset + count; n++)
                {
                    int index = mode == "duplicate_page_handle" && n == 5 ? 0 : n;
                    entities.Add(new { handle = aliases["p" + index], type = "DBPoint", layer = "B_PAGES", position = new[] { 6000 + 10 * index, 0, 0 } });
                }
                if (offset + count < 12) pagination["next_offset"] = mode == "repeated_page_offset" && offset > 0 ? offset : offset + count;
            }
            if (layer is "B_CSHARP" or "B_LISP")
            {
                int y = layer == "B_CSHARP" ? 4000 : 5000;
                entities.Add(new { type = "Line", length = mode == "wrong_lisp_geometry" && layer == "B_LISP" ? 99 : 100, start = new[] { 0, y, 0 }, end = new[] { 100, y, 0 } });
                entities.Add(new { type = "Circle", radius = 10, center = new[] { 50, y + 50, 0 } });
            }
            return new { result = new { entities, pagination } };
        }
    }
}
