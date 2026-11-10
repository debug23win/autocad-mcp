using System.Text.Json;
using CadMcp.Core;
using CadMcp.Providers;
namespace CadMcp.Tests;
public sealed partial class CadBenchmarkTests
{
    [Fact]
    public async Task Baseline_eight_cases_pass_through_actual_MCP_schemas_and_transport()
    {
        string run = Guid.NewGuid().ToString("N"), pipe = "benchmark-schema-" + run;
        var cad = new FakeCad(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var broker = new PipeServer(pipe, cad.Send); broker.Start();
        await using var mcp = await BenchmarkMcpSession.StartAsync(TestEnvironment.HostExecutable, pipe, run, timeout.Token);
        var runner = new CadBenchmark((r, ct) => r.Operation.StartsWith("benchmark_", StringComparison.Ordinal) ? cad.Send(r, ct) : mcp.CallAsync(r, ct),
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(20), runId: run);
        var result = await runner.RunAsync("session", ct: timeout.Token);
        Assert.True(result.Complete, result.Markdown()); Assert.Equal(8, result.Passed);
        Assert.All(cad.Requests.Where(r => r.Operation is "cad_search" or "cad_entity_get" or "cad_table_get"), r => Assert.NotNull(r.ExpectedRevision));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_read_transport_failure_stops_without_another_mutation(bool responseFailure)
    {
        var cad = new FakeCad();
        cad.Override = r => r.Operation == "cad_entity_get" ? responseFailure ? Response.Fail(r, "INTERNAL_ERROR", "Pipe is broken") : throw new IOException("connection lost") : null;
        var result = await Run(cad);
        Assert.Equal("unknown", result.Cases[0].Status); Assert.Equal(7, result.Skipped);
        Assert.Single(cad.Requests, r => r.Operation == "cad_edit"); Assert.DoesNotContain(cad.Requests, r => r.Operation == "cad_lisp");
    }
    [Fact]
    public async Task First_bad_readback_stops_before_later_cases_even_if_the_error_is_managed()
    {
        var cad = new FakeCad("wrong_polyline_area"); var result = await Run(cad);
        Assert.Equal("failed", result.Cases[0].Status); Assert.Equal(7, result.Skipped);
        Assert.Single(cad.Requests, r => r.Operation == "cad_edit");
    }
    [Fact]
    public async Task Existing_DWG_blocks_before_fixture_creation_and_any_CAD_write()
    {
        var cad = new FakeCad("existing_document"); var result = await Run(cad);
        Assert.NotNull(result.SetupError); Assert.Equal(8, result.Skipped);
        Assert.DoesNotContain(cad.Requests, r => r.Operation is "benchmark_create" or "cad_edit" or "cad_lisp");
    }
    [Fact]
    public void Native_isolation_guard_rejects_every_other_document_even_when_names_or_saved_state_look_harmless()
    {
        BenchmarkIsolation.RequireEmpty(0); BenchmarkIsolation.RequireFixtureOnly(["owned"], "owned");
        Assert.Equal("BENCHMARK_SESSION_NOT_EMPTY", Assert.Throws<CadFault>(() => BenchmarkIsolation.RequireEmpty(1)).Code);
        Assert.Equal("BENCHMARK_SESSION_CHANGED", Assert.Throws<CadFault>(() => BenchmarkIsolation.RequireFixtureOnly(["owned", "other"], "owned")).Code);
        Assert.Throws<CadFault>(() => BenchmarkIsolation.RequireFixtureOnly([], "owned"));
        Assert.Throws<CadFault>(() => BenchmarkIsolation.RequireFixtureOnly(["other"], "owned"));
    }
    [Fact]
    public async Task MCP_required_argument_validation_names_the_missing_revision_before_invocation()
    {
        string pipe = "benchmark-schema-negative-" + Guid.NewGuid().ToString("N"); int brokerCalls = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var broker = new PipeServer(pipe, (r, ct) => { brokerCalls++; return Task.FromResult(new Response(r.RequestId, "completed")); }); broker.Start();
        await using var mcp = await BenchmarkMcpSession.StartAsync(TestEnvironment.HostExecutable, pipe, Guid.NewGuid().ToString("N"), timeout.Token);
        var error = await Assert.ThrowsAsync<CadFault>(() => mcp.CallAsync(new(Guid.NewGuid().ToString("N"), "cad_entity_get", "s", "d", Data: Wire.Element(new { handle = "A" })), timeout.Token));
        Assert.Equal("BENCHMARK_ARGUMENTS_INVALID", error.Code); Assert.Contains("expected_revision", error.Message); Assert.Equal(0, brokerCalls);
    }
}

public sealed partial class CadBenchmarkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_connection_loss_is_unknown_and_no_later_native_tool_is_called(bool responseFailure)
    {
        using var fixture = new FullFixture(); var store = new SnapshotStore();
        var entities = Enumerable.Range(0, 10).Select(i => Wire.Element(new { handle = (0xAA0 + i).ToString("X"), type = "DBPoint", access = "structured", layer = "B_PAGES", position = new[] { i, 0, 0 } })).ToArray();
        var snapshot = store.Add("fixture", 1, entities, true, "model", 10);
        fixture.Observation = r =>
        {
            if (r.Operation == "cad_snapshot") return Scoped(r, new { snapshot_id = snapshot.Id, captured = 10, truncated = true }, "partial");
            if (r.Operation == "cad_search" && r.Data.Text("options_json") is { } json && !json.Contains("layer", StringComparison.Ordinal))
                return Scoped(r, new { result = new { entities = entities.Take(3), pagination = new { next_offset = 3 } } });
            if (r.Operation == "cad_query") return responseFailure ? Response.Fail(r, "INTERNAL_ERROR", "Pipe is broken") : throw new IOException("Worker process exited");
            return null;
        };
        var result = await fixture.Run("cad_csharp", "cad_query", "cad_review");
        Assert.Equal("unknown", ToolCase(result, "cad_query").Status); Assert.Equal("skipped", ToolCase(result, "cad_review").Status);
        Assert.DoesNotContain(fixture.Cad.Requests, r => r.Operation == "cad_review");
    }
}
