using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Tests;

// Independent controlled observations for the full MCP inventory. These are
// transport/contract tests, never native CAD execution or proof of vendor APIs.
public sealed partial class CadBenchmarkTests
{
    private sealed class FullFixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "cad-benchmark-independent-" + Guid.NewGuid().ToString("N"));
        public readonly string RunId = "independent-run-" + Guid.NewGuid().ToString("N");
        public string RunRoot => Wire.DataDirectory("benchmarks", RunId);
        public string FilesRoot => Path.Combine(RunRoot, "files");
        public readonly FakeCad Cad = new();
        public Func<Request, Response?>? Observation;
        public FullFixture(string mode = "")
        {
            Directory.CreateDirectory(Root);
            Cad = new FakeCad(mode) { FixturePath = Path.Combine(Root, "fixture.dwg") };
            File.WriteAllText(Path.Combine(Root, "checkpoint.dwg"), "independent checkpoint placeholder, not a DWG");
            Cad.Override = Reply;
        }
        private Response? Reply(Request r)
        {
            if (Observation?.Invoke(r) is { } observed) return observed;
            if (r.Operation == "cad_sessions") return SessionsObservation(r, "valid");
            if (r.Operation == "cad_proxies") return Scoped(r, new { handles_scanned = 250, samples = Array.Empty<object>() });
            if (r.Operation == "cad_vertical_catalog") return Scoped(r, new { civil3d = new { available = false }, map3d = new { available = false } });
            if (r.Operation == "cad_vertical_get") return Scoped(r, new { handle = r.Data.Text("handle"), civil3d = false, type = "Line" });
            if (r.Operation == "cad_catalog") return Scoped(r, new { catalog = new { pdf_devices = new[] { new { name = "DWG To PDF.pc3", media = new[] { "ISO_full_bleed_A3_(420.00_x_297.00_MM)" } } }, truncated = false } });
            if (r.Operation == "cad_csharp")
                return Scoped(r, new
                {
                    checkpoint = new { path = Path.Combine(Root, "checkpoint.dwg") },
                    source_sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r.Data.Text("code")!))),
                    result = new { value = new { line = "E01", circle = "E02", solid = "E03", duplicateA = "E04", duplicateB = "E05", archive = "E06" } }
                });
            if (r.Operation == "cad_entity_get" && r.Data.Text("handle") == "E01")
                return Scoped(r, new { handle = "E01", type = "Line", layer = "B_REALCS", length = 100, start = new[] { 9000, 0, 0 }, end = new[] { 9100, 0, 0 } });
            if (r.Operation == "cad_entity_get" && r.Data.Text("handle") == "E02")
                return Scoped(r, new { handle = "E02", type = "Circle", layer = "B_REALCS", radius = 10, center = new[] { 9050, 50, 0 } });
            return null;
        }
        public Task<CadBenchmarkResult> Run(params string[] inventory) =>
            new CadBenchmark(Cad.Send, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(150), inventory, RunId).RunAsync("session");
        public void Dispose()
        {
            Directory.Delete(Root, true);
            if (Directory.Exists(RunRoot)) Directory.Delete(RunRoot, true);
        }
    }
    private static Response Scoped(Request request, object data, string status = "completed") =>
        new(request.RequestId, status, data, "session", "fixture", 1);
    private static CadBenchmarkCase ToolCase(CadBenchmarkResult result, string tool) => Assert.Single(result.Cases, c => c.Id == "tool_" + tool);

    [Fact]
    public async Task Full_inventory_evidence_citations_do_not_inflate_actual_request_count()
    {
        using var fixture = new FullFixture();
        var result = await fixture.Run("cad_edit", "cad_entity_get", "cad_search", "cad_table_get", "cad_takeoff", "cad_lisp");
        Assert.Equal(14, result.Passed);
        Assert.Equal(6, result.ToolsPassed);
        Assert.Equal(fixture.Cad.Requests.Count, result.CadCalls);
        Assert.Single(fixture.Cad.Requests, r => r.Operation == "cad_lisp");
    }

    [Fact]
    public async Task Every_actual_advertised_tool_dispatches_to_an_explicit_verdict_case()
    {
        // This checks dispatch/inventory drift only. Deliberately unsupported
        // fake observations fail cases; they are never claimed as native PASS.
        var inventory = typeof(CadMcp.Host.CadTools).GetMethods()
            .SelectMany(method => method.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)
                .Cast<ModelContextProtocol.Server.McpServerToolAttribute>())
            .Select(attribute => attribute.Name!).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(inventory);
        Assert.Equal(inventory.Length, inventory.Distinct(StringComparer.Ordinal).Count());
        foreach (string tool in inventory)
        {
            using var fixture = new FullFixture();
            fixture.Observation = r => r.Operation == "cad_documents" && fixture.Cad.Requests.Count(q => q.Operation == "cad_documents") > 1
                ? Scoped(r, new[] { new { document_id = "fixture" }, new { document_id = "user-drawing" } }) : null;
            var result = await fixture.Run(tool == "cad_csharp" ? [tool] : ["cad_csharp", tool]);
            Assert.Equal("passed", ToolCase(result, "cad_csharp").Status);
            var verdict = ToolCase(result, tool);
            Assert.False(verdict.ErrorCode is "BENCHMARK_CASE_MISSING" or "NOT_RUN", tool + ": " + verdict.Message);
            if (verdict.Status == "passed") { Assert.NotEmpty(verdict.Checks); Assert.All(verdict.Checks, check => Assert.True(check.Passed)); }
        }
    }

    private static Response SessionsObservation(Request request, string mode) => Scoped(request, new[]
    {
        new
        {
            worker = new { session_id = "session", process_id = 42 }, reachable = true,
            context = new
            {
                request_id = "probe-context", status = mode == "failed" ? "failed" : "completed",
                session_id = mode == "foreign_session" ? "another-session" : "session",
                document_id = mode == "foreign_document" ? "another-document" : "fixture",
                error = mode == "failed" ? new { code = "BENCHMARK_SCOPE_MISMATCH", message = "Probe received no fixture scope" } : null
            }
        }
    });

    [Theory]
    [InlineData("valid", "passed")]
    [InlineData("failed", "failed")]
    [InlineData("foreign_session", "failed")]
    [InlineData("foreign_document", "failed")]
    public async Task Reachable_session_requires_a_successful_context_for_the_exact_fixture(string mode, string expected)
    {
        using var fixture = new FullFixture();
        fixture.Observation = request => request.Operation == "cad_sessions" ? SessionsObservation(request, mode) : null;
        var result = await fixture.Run("cad_sessions");
        Assert.Equal(expected, ToolCase(result, "cad_sessions").Status);
        Assert.Single(fixture.Cad.Requests, request => request.Operation == "cad_sessions");
        Assert.Equal(expected == "passed" ? 1 : 0, result.ToolsPassed);
    }

    [Theory]
    [InlineData("cad_vertical_catalog")]
    [InlineData("cad_vertical_get")]
    [InlineData("cad_proxies")]
    public async Task Missing_vendor_or_proxy_fixture_is_skipped_never_passed(string tool)
    {
        using var fixture = new FullFixture();
        var result = await fixture.Run(tool);
        Assert.Equal("skipped", ToolCase(result, tool).Status);
        Assert.Equal(0, result.ToolsPassed);
        Assert.Equal(1, result.ToolsSkipped);
        Assert.Equal(8, result.Passed);
        Assert.False(result.Complete);
    }

    [Fact]
    public async Task Newly_advertised_unimplemented_tool_cannot_silently_pass()
    {
        using var fixture = new FullFixture();
        var result = await fixture.Run("cad_future_tool");
        Assert.Equal("failed", ToolCase(result, "cad_future_tool").Status);
        Assert.Equal("BENCHMARK_CASE_MISSING", ToolCase(result, "cad_future_tool").ErrorCode);
        Assert.Equal(0, result.ToolsPassed);
    }

    [Fact]
    public async Task Unknown_base_outcome_stops_all_inventory_cases_without_retries()
    {
        using var fixture = new FullFixture("lost_first_mutation");
        var result = await fixture.Run("cad_csharp", "cad_publish", "cad_lisp");
        Assert.Equal(1, result.Unknown);
        Assert.All(result.Cases.Where(c => c.Id.StartsWith("tool_", StringComparison.Ordinal)), c => Assert.Equal("skipped", c.Status));
        Assert.Single(fixture.Cad.Requests, r => r.Operation == "cad_edit");
        Assert.DoesNotContain(fixture.Cad.Requests, r => r.Operation is "cad_csharp" or "cad_publish" or "cad_lisp");
        Assert.False(File.Exists(Path.Combine(fixture.FilesRoot, "benchmark-reference.pdf")));
    }

    [Fact]
    public async Task Successful_verify_uses_the_real_report_evaluator_and_worker_envelope()
    {
        using var fixture = new FullFixture();
        fixture.Observation = r =>
        {
            if (r.Operation != "cad_verify") return null;
            var entities = new[]
            {
                Wire.Element(new { handle = "E01", type = "Line", length = 100, start = new[] { 9000, 0, 0 }, end = new[] { 9100, 0, 0 } }),
                Wire.Element(new { handle = "E02", type = "Circle", radius = 10, center = new[] { 9050, 50, 0 } })
            };
            var report = DrawingVerification.Evaluate(entities, "Millimeters", DrawingVerification.Parse(r.Data.Text("expectations_json")));
            Assert.Equal(3, report.Passed);
            return Scoped(r, new { verification = report, entities, source = "current_AutoCAD_database" });
        };
        var result = await fixture.Run("cad_csharp", "cad_verify");
        Assert.Equal("passed", ToolCase(result, "cad_verify").Status);
    }

    [Theory]
    [InlineData("actual")]
    [InlineData("expected")]
    [InlineData("readback_handle")]
    public async Task Claimed_passed_verification_still_requires_exact_targets_and_consistent_measurements(string corruption)
    {
        using var fixture = new FullFixture();
        fixture.Observation = r =>
        {
            if (r.Operation != "cad_verify") return null;
            var entities = new[]
            {
                Wire.Element(new { handle = "E01", type = "Line", length = 100, start = new[] { 9000, 0, 0 }, end = new[] { 9100, 0, 0 } }),
                Wire.Element(new { handle = "E02", type = "Circle", radius = 10, center = new[] { 9050, 50, 0 } })
            };
            var report = DrawingVerification.Evaluate(entities, "Millimeters", DrawingVerification.Parse(r.Data.Text("expectations_json")));
            if (corruption == "readback_handle") entities[0] = Wire.Element(new { handle = "E99", type = "Line", length = 100 });
            else report = report with { Checks = report.Checks.Select(check => check.Label.Contains("length", StringComparison.Ordinal)
                ? corruption == "actual" ? check with { Actual = 99 } : check with { Expected = 101 } : check).ToArray() };
            Assert.Equal(3, report.Passed); // Poisoned report still claims every check passed.
            return Scoped(r, new { verification = report, entities, source = "current_AutoCAD_database" });
        };
        var result = await fixture.Run("cad_csharp", "cad_verify");
        Assert.Equal("failed", ToolCase(result, "cad_verify").Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deliberately_bounded_snapshot_and_query_use_actual_partial_metadata_and_store_contract(bool query)
    {
        using var fixture = new FullFixture();
        var store = new SnapshotStore();
        var entities = Enumerable.Range(0, 10).Select(i => Wire.Element(new { handle = (0xAA0 + i).ToString("X"), type = "DBPoint", access = "structured", layer = "B_PAGES", position = new[] { i, 0, 0 } })).ToArray();
        var snapshot = store.Add("fixture", 1, entities, truncated: true, scope: "model", limit: 10);
        fixture.Observation = r =>
        {
            if (r.Operation == "cad_snapshot") return Scoped(r, new { snapshot_id = snapshot.Id, captured = 10, truncated = true, units = "Millimeters", scope = "current_space_top_level", cached = false }, "partial");
            if (r.Operation == "cad_query") return Scoped(r, store.Query(snapshot.Id, "fixture", 1, r.Data));
            if (r.Operation == "cad_search" && r.Data.Text("options_json") is { } options && !options.Contains("layer", StringComparison.Ordinal))
                return Scoped(r, new { result = new { entities = entities.Take(3), pagination = new { next_offset = 3 } }, cached = false });
            return null;
        };
        string tool = query ? "cad_query" : "cad_snapshot";
        var result = await fixture.Run(tool);
        Assert.Equal("passed", ToolCase(result, tool).Status);
        Assert.Single(fixture.Cad.Requests, r => r.Operation == "cad_snapshot");
    }

    [Fact]
    public async Task Stalled_read_times_out_and_prevents_later_writes_without_retries()
    {
        using var fixture = new FullFixture();
        async Task<Response> Send(Request request, CancellationToken ct)
        {
            if (request.Operation != "cad_outline") return await fixture.Cad.Send(request, ct);
            fixture.Cad.Requests.Add(request);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable");
        }
        var result = await new CadBenchmark(Send, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(150),
            ["cad_csharp", "cad_outline", "cad_publish"], fixture.RunId).RunAsync("session").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("unknown", ToolCase(result, "cad_outline").Status);
        Assert.Equal("skipped", ToolCase(result, "cad_publish").Status);
        Assert.Single(fixture.Cad.Requests, r => r.Operation == "cad_outline");
        Assert.DoesNotContain(fixture.Cad.Requests, r => r.Operation == "cad_publish");
        Assert.Equal(fixture.Cad.Requests.Count, result.CadCalls);
    }

    [Fact]
    public async Task Unknown_real_csharp_receipt_stops_the_extended_run_without_resubmission()
    {
        using var fixture = new FullFixture();
        string? operation = null;
        fixture.Observation = r =>
        {
            if (r.Operation == "cad_csharp") { operation = r.Data.Text("operation_id"); return Scoped(r, new { operation_id = operation }, "queued"); }
            if (r.Operation == "cad_operation_status" && r.Data.Text("operation_id") == operation)
                return Scoped(r, new { operation_id = operation, state = "unknown" });
            return null;
        };
        var result = await fixture.Run("cad_csharp", "cad_export", "cad_publish");
        Assert.Equal("unknown", ToolCase(result, "cad_csharp").Status);
        Assert.Equal("skipped", ToolCase(result, "cad_export").Status);
        Assert.Equal("skipped", ToolCase(result, "cad_publish").Status);
        Assert.Single(fixture.Cad.Requests, r => r.Operation == "cad_csharp");
        Assert.DoesNotContain(fixture.Cad.Requests, r => r.Operation is "cad_export" or "cad_publish");
    }

    [Fact]
    public async Task Unverified_report_with_a_passed_counter_is_not_a_successful_verification()
    {
        using var fixture = new FullFixture();
        fixture.Observation = r => r.Operation == "cad_verify" ? Scoped(r, new
        {
            verification = new { state = "unverified", overall_state = "unverified", passed = 0, failed = 0, unverified = 3,
                checks = new[] { new { label = "Live entity count", status = "unverified", expected = 2 } } },
            entities = new[] { new { handle = "E01" }, new { handle = "E02" } }, source = "current_AutoCAD_database"
        }) : null;
        var result = await fixture.Run("cad_csharp", "cad_verify");
        Assert.Equal("passed", ToolCase(result, "cad_csharp").Status);
        Assert.Equal("failed", ToolCase(result, "cad_verify").Status);
        Assert.Equal(1, result.ToolsPassed);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("truncated")]
    [InlineData("archive_id")]
    public async Task Incomplete_completed_verification_payload_cannot_pass(string flag)
    {
        using var fixture = new FullFixture();
        fixture.Observation = r => r.Operation == "cad_verify" ? Scoped(r, new Dictionary<string, object>
        {
            ["verification"] = new { state = "passed", overall_state = "passed", entity_count = 2, passed = 3, failed = 0, unverified = 0,
                checks = new[] { new { label = "Live entity count", status = "passed", actual = 2, expected = 2 }, new { label = "length", status = "passed", actual = 100, expected = 100 }, new { label = "radius", status = "passed", actual = 10, expected = 10 } } },
            ["entities"] = new object[] { new { handle = "E01", length = 100 }, new { handle = "E02", radius = 10 } },
            ["source"] = "current_AutoCAD_database",
            [flag] = flag == "archive_id" ? "not-reconstructed" : true
        }) : null;
        var result = await fixture.Run("cad_csharp", "cad_verify");
        Assert.Equal("failed", ToolCase(result, "cad_verify").Status);
    }

    [Fact]
    public async Task Publish_creates_its_own_output_directory_before_invoking_native_publisher()
    {
        using var fixture = new FullFixture();
        bool called = false, existed = false;
        string? folder = null;
        fixture.Observation = r =>
        {
            if (r.Operation != "cad_publish") return null;
            called = true; folder = r.Data.Text("output_folder"); existed = Directory.Exists(folder);
            return Response.Fail(r, "PLOT_DEVICE_UNAVAILABLE", "Controlled unavailable-device result");
        };
        var result = await fixture.Run("cad_publish");
        Assert.True(called);
        Assert.Equal(Path.Combine(fixture.FilesRoot, "published"), folder);
        Assert.True(existed, "cad_publish requires an existing output_folder before native execution");
        Assert.Equal("skipped", ToolCase(result, "cad_publish").Status);
        Assert.Equal(0, result.ToolsPassed);
    }

    [Fact]
    public async Task Missing_pdf_driver_is_skipped_before_publication_and_not_counted_as_coverage()
    {
        using var fixture = new FullFixture();
        fixture.Observation = r => r.Operation == "cad_catalog" ? Scoped(r, new { catalog = new { pdf_devices = Array.Empty<object>(), truncated = false } }) : null;
        var result = await fixture.Run("cad_publish");
        Assert.Equal("skipped", ToolCase(result, "cad_publish").Status);
        Assert.Equal("PDF_DEVICE_UNAVAILABLE", ToolCase(result, "cad_publish").ErrorCode);
        Assert.Equal(0, result.ToolsPassed);
        Assert.DoesNotContain(fixture.Cad.Requests, r => r.Operation == "cad_publish");
    }

    [Fact]
    public async Task Extended_generated_edit_preview_and_assembly_plans_pass_production_parsers()
    {
        using var fixture = new FullFixture();
        fixture.Observation = r => r.Operation switch
        {
            "cad_assembly_get" => Scoped(r, new { solid_volume_m3 = .00252, solid_mass_kg = 19.782, recipe = new { mark = "B-1", material = "S235" } }),
            "cad_edit_preview" => Scoped(r, new { plan_hash = new string('A', 64) }),
            "cad_publish" => Response.Fail(r, "PLOT_DEVICE_UNAVAILABLE", "Controlled unavailable-device result"),
            _ => null
        };
        var result = await fixture.Run("cad_assembly_get", "cad_edit_preview", "cad_publish");
        Assert.Equal("passed", ToolCase(result, "cad_assembly_get").Status);
        Assert.Equal("passed", ToolCase(result, "cad_edit_preview").Status);
        var requests = fixture.Cad.Requests.Where(r => r.Operation is "cad_edit" or "cad_edit_preview").ToArray();
        foreach (var request in requests)
        {
            Assert.NotEmpty(EditPlan.Parse(request.Data.Text("operations_json")!));
            DrawingVerification.Parse(request.Data.Text("expectations_json"));
        }
        Assert.Contains(requests, r => r.Data.Text("operations_json")!.Contains("assembly_create", StringComparison.Ordinal));
        Assert.Contains(requests, r => r.Data.Text("operations_json")!.Contains("layout_create", StringComparison.Ordinal));
        Assert.Single(requests, r => r.Operation == "cad_edit_preview");
        Assert.All(result.Cases.Where(c => c.Status == "passed"), c => { Assert.NotEmpty(c.Checks); Assert.All(c.Checks, check => Assert.True(check.Passed)); });
    }

    [Fact]
    public async Task Large_table_range_is_archived_and_reconstructed_without_entity_or_search_clipping()
    {
        using var fixture = new FullFixture();
        var table = Wire.Element(ArchiveEntity());
        Assert.True(table.GetRawText().Length > 512 * 1024);
        Assert.All(table.GetProperty("cells").EnumerateArray(), cell => Assert.True(ReadPayloadBudget.Bytes(cell) < ReadPayloadBudget.EntityBytes));
        var archive = new ResultArchive(Path.Combine(fixture.Root, "actual-archive"));
        fixture.Observation = r =>
        {
            if (r.Operation == "cad_table_get" && r.Data.Text("handle") == "E06")
            {
                Assert.Equal(CadBenchmark.ArchiveRows, r.Data.Number("row_count", 0));
                Assert.Equal(1, r.Data.Number("column_count", 0));
                string id = archive.Put(r.SessionId!, r.DocumentId!, 1, table);
                return Scoped(r, new { archive_id = id, captured_revision = 1 }, "partial");
            }
            return r.Operation == "cad_result_get"
                ? Scoped(r, archive.Read(r.Data.Text("archive_id")!, r.SessionId!, r.DocumentId!, r.Data.Number("offset", 0), r.Data.Number("limit", 32000))) : null;
        };
        var result = await fixture.Run("cad_csharp", "cad_result_get");
        Assert.Equal("passed", ToolCase(result, "cad_result_get").Status);
        Assert.True(fixture.Cad.Requests.Count(r => r.Operation == "cad_result_get") > 1);
        Assert.DoesNotContain(fixture.Cad.Requests, r => r.Operation == "cad_search" && (r.Data.Text("options_json")?.Contains("B_ARCHIVE", StringComparison.Ordinal) ?? false));
        Assert.Contains(fixture.Cad.Requests, r => r.Operation == "cad_table_get" && r.Data.Text("handle") == "E06");
    }

    [Fact]
    public async Task Archive_page_limit_exhaustion_is_not_complete_reconstruction_even_if_prefix_is_valid_json()
    {
        using var fixture = new FullFixture();
        string raw = JsonSerializer.Serialize(ArchiveEntity(shortText: true));
        fixture.Observation = r => ArchiveResponse(r, raw, endless: true);
        var result = await fixture.Run("cad_csharp", "cad_result_get");
        Assert.Equal("failed", ToolCase(result, "cad_result_get").Status);
    }
    [Fact]
    public async Task Small_inline_table_does_not_claim_archive_coverage()
    {
        using var fixture = new FullFixture();
        string raw = JsonSerializer.Serialize(ArchiveEntity(shortText: true));
        fixture.Observation = r => ArchiveResponse(r, raw, endless: false);
        var result = await fixture.Run("cad_csharp", "cad_result_get");
        Assert.Equal("ARCHIVE_NOT_CREATED", ToolCase(result, "cad_result_get").ErrorCode);
        Assert.DoesNotContain(fixture.Cad.Requests, r => r.Operation == "cad_result_get");
    }

    [Theory]
    [InlineData("handle")]
    [InlineData("text")]
    [InlineData("duplicate_cell")]
    public async Task Archive_reconstruction_rejects_wrong_identity_or_incomplete_content(string field)
    {
        using var fixture = new FullFixture();
        var entity = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(ArchiveEntity()))!;
        if (field == "handle") entity[field] = "incorrect";
        else
        {
            var cells = JsonSerializer.Deserialize<Dictionary<string, object>[]>(JsonSerializer.Serialize(entity["cells"]))!;
            if (field == "text") cells[0]["text"] = "incorrect";
            else cells[1] = cells[0];
            entity["cells"] = cells;
        }
        string raw = JsonSerializer.Serialize(entity);
        fixture.Observation = r => ArchiveResponse(r, raw, endless: false);
        var result = await fixture.Run("cad_csharp", "cad_result_get");
        Assert.Equal("failed", ToolCase(result, "cad_result_get").Status);
        Assert.Contains(fixture.Cad.Requests, r => r.Operation == "cad_result_get");
    }

    private static object ArchiveEntity(bool shortText = false)
    {
        return new { handle = "E06", rows = CadBenchmark.ArchiveRows, columns = 1, next_row = (int?)null,
            cells = Enumerable.Range(0, CadBenchmark.ArchiveRows).Select(i => new { row = i, column = 0,
                text = CadBenchmark.ArchiveMarker + i.ToString("D3") + "_" + (shortText ? "" : new string('X', CadBenchmark.ArchivePayloadLength)) }).ToArray() };
    }
    private static Response? ArchiveResponse(Request r, string raw, bool endless)
    {
        if (r.Operation == "cad_table_get" && r.Data.Text("handle") == "E06")
            return endless || raw.Length > 512 * 1024
                ? Scoped(r, new { archive_id = "fixture-archive", captured_revision = 1 }, "partial")
                : Scoped(r, JsonSerializer.Deserialize<JsonElement>(raw));
        if (r.Operation != "cad_result_get") return null;
        int offset = r.Data.Number("offset", 0), limit = r.Data.Number("limit", 32000);
        string text = offset >= raw.Length ? "" : raw.Substring(offset, Math.Min(limit, raw.Length - offset));
        int? next = endless ? offset + limit : offset + text.Length < raw.Length ? offset + text.Length : null;
        return Scoped(r, new { archive_id = "fixture-archive", historical = true, offset, text, next_offset = next });
    }
}
