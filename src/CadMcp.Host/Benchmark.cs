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
            ("outline", "cad_outline", new { text_sample = 200 })
        };
        if (at + 6 < args.Length)
        {
            var task = AgentEvaluation.ReadTasks(Load(args[at + 5])).FirstOrDefault(t => t.Id == args[at + 6]);
            if (task is null) { Console.Error.WriteLine("Task " + args[at + 6] + " not found"); return 2; }
            foreach (var query in task.EvidenceQueries ?? []) queries.Add((query.Name, query.Operation, query.Data));
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var evidence = new Dictionary<string, object?>();
        foreach (var (name, operation, data) in queries)
        {
            var response = await CadTools.RequestAsync(operation, session, document, data, timeout.Token);
            if (response.Error is not null) { Console.Error.WriteLine(operation + ": " + response.Error.Code + " " + response.Error.Message); return 1; }
            evidence[name] = response.Data;
        }
        File.WriteAllText(output, JsonSerializer.Serialize(evidence, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
        Console.WriteLine("Evidence written to " + output);
        return 0;
    }
}
