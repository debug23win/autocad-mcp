using System.Text;
using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Host;

/// <summary>
/// Command line of the agent benchmark: grade recorded runs (optionally against a baseline release), check that
/// no task passes an empty run, and capture the drawing evidence after a run through the running CAD worker.
/// </summary>
internal static class Benchmark
{
    private static JsonElement Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    /// <summary>--grade tasks.json runs.json [baseline-runs.json]</summary>
    public static int Grade(string[] args)
    {
        int at = Array.IndexOf(args, "--grade");
        if (at < 0 || at + 2 >= args.Length) { Console.Error.WriteLine("Usage: --grade tasks.json runs.json [baseline-runs.json]"); return 2; }
        var tasks = AgentEvaluation.ReadTasks(Load(args[at + 1]));
        var runs = AgentEvaluation.ReadRuns(Load(args[at + 2]));
        var baseline = at + 3 < args.Length && !args[at + 3].StartsWith("--", StringComparison.Ordinal) ? AgentEvaluation.ReadRuns(Load(args[at + 3])) : null;
        var report = AgentEvaluation.Report(tasks, runs, baseline);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
        return Wire.Element(report).Text("grader_self_check") == "passed" ? 0 : 3;
    }

    /// <summary>--grade-selfcheck tasks.json: every task must fail a run in which the agent did nothing.</summary>
    public static int SelfCheck(string[] args)
    {
        int at = Array.IndexOf(args, "--grade-selfcheck");
        if (at < 0 || at + 1 >= args.Length) { Console.Error.WriteLine("Usage: --grade-selfcheck tasks.json"); return 2; }
        var tasks = AgentEvaluation.ReadTasks(Load(args[at + 1]));
        var passing = tasks.Where(t => AgentEvaluation.Grade(t, AgentEvaluation.Empty(t)).Passed).Select(t => t.Id).ToArray();
        Console.WriteLine(passing.Length == 0 ? "All " + tasks.Count + " tasks fail an empty run." : "Tasks passing an empty run: " + string.Join(", ", passing));
        return passing.Length == 0 ? 0 : 3;
    }

    /// <summary>
    /// --capture-evidence session_id document_id since_revision output.json [tasks.json task_id]: what the run changed,
    /// quantities and outline, plus the task's own evidence queries.
    /// </summary>
    public static async Task<int> CaptureAsync(string[] args)
    {
        int at = Array.IndexOf(args, "--capture-evidence");
        if (at < 0 || at + 4 >= args.Length || !long.TryParse(args[at + 3], out long since))
        { Console.Error.WriteLine("Usage: --capture-evidence session_id document_id since_revision output.json [tasks.json task_id]"); return 2; }
        string session = args[at + 1], document = args[at + 2], output = args[at + 4];
        var queries = new List<(string Name, string Operation, object Data)>
        {
            ("changes", "cad_changes", new { since_revision = since, limit = 2000 }),
            ("takeoff", "cad_takeoff", new { scope = "all", include = "lengths,areas,blocks,attributes", max_rows = 5000 }),
            // Every layer, so moving objects off any layer is visible.
            ("outline", "cad_outline", new { text_sample = 200, layer_limit = 5000 })
        };
        if (at + 6 < args.Length)
        {
            var task = AgentEvaluation.ReadTasks(Load(args[at + 5])).FirstOrDefault(t => t.Id == args[at + 6]);
            if (task is null) { Console.Error.WriteLine("Task " + args[at + 6] + " not found"); return 2; }
            foreach (var query in task.EvidenceQueries ?? []) queries.Add((query.Name, query.Operation, query.Data));
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var evidence = new Dictionary<string, JsonElement>();
        try
        {
            foreach (var (name, operation, data) in queries)
            {
                var response = await CadTools.RequestAsync(operation, session, document, data, timeout.Token);
                if (response.Error is not null) { Console.Error.WriteLine(operation + ": " + response.Error.Code + " " + response.Error.Message); return 1; }
                var value = Wire.Element(response.Data ?? new { });
                // A result over the response limit is archived; read it back whole.
                if (value.Text("archive_id") is { } archived) value = await UnarchiveAsync(archived, session, document, timeout.Token);
                // Missing entries would let "absent" and "count" checks pass and hide deletions: refuse to grade them.
                if (Incomplete(value) is { } reason)
                {
                    Console.Error.WriteLine(name + " (" + operation + ") is incomplete: " + reason + ". Use a smaller fixture drawing or narrower evidence queries.");
                    return 1;
                }
                evidence[name] = value;
            }
        }
        catch (CadFault fault) { Console.Error.WriteLine(fault.Code + ": " + fault.Message); return 1; }
        File.WriteAllText(output, JsonSerializer.Serialize(evidence, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
        Console.WriteLine("Evidence written to " + output);
        return 0;
    }

    /// <summary>Why captured evidence does not cover the whole drawing, or null.</summary>
    public static string? Incomplete(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (value.TryGetProperty("partial", out var partial) && partial.ValueKind == JsonValueKind.True) return "the read stopped at its entity limit";
        if (value.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True) return "the result was truncated";
        if (value.TryGetProperty("complete", out var complete) && complete.ValueKind == JsonValueKind.False) return "the change log dropped older changes";
        if (value.TryGetProperty("attributes_truncated", out var attributes) && attributes.ValueKind == JsonValueKind.True) return "attribute rows reached max_rows";
        if (value.TryGetProperty("layer_count", out var layerCount) && value.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array
            && layerCount.TryGetInt32(out int count) && count > layers.GetArrayLength()) return "the outline lists only the first " + layers.GetArrayLength() + " of " + count + " layers";
        if (value.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("pagination", out var page) && page.ValueKind == JsonValueKind.Object && page.TryGetProperty("next_offset", out var next) && next.ValueKind == JsonValueKind.Number)
                return "the search has more pages; raise limit in its options";
            return Incomplete(result);
        }
        return null;
    }

    private static async Task<JsonElement> UnarchiveAsync(string archiveId, string session, string document, CancellationToken ct)
    {
        var text = new StringBuilder();
        for (int? offset = 0; offset is { } at;)
        {
            var page = await CadTools.RequestAsync("cad_result_get", session, document, new { archive_id = archiveId, offset = at, limit = 32000 }, ct);
            if (page.Error is not null) throw new CadFault(page.Error.Code, page.Error.Message);
            var data = Wire.Element(page.Data ?? new { });
            text.Append(data.Text("text"));
            offset = data.TryGetProperty("next_offset", out var next) && next.ValueKind == JsonValueKind.Number ? next.GetInt32() : null;
        }
        using var parsed = JsonDocument.Parse(text.ToString());
        return parsed.RootElement.Clone();
    }
}
