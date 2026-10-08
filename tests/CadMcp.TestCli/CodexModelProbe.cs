using System.Collections.Concurrent;
using System.Text.Json;
using CadMcp.Core;
using CadMcp.Providers;

namespace CadMcp.TestCli;

internal static class CodexModelProbe
{
    public static async Task RunAsync(string cli, string? resumeConversation = null)
    {
        const string session = "model-fixture-session", document = "model-fixture-document", marker = "CAD_MODEL_SWITCH_4827";
        string pipe = "cadmcp-model-probe-" + Guid.NewGuid().ToString("N");
        var calls = new ConcurrentQueue<string>();
        using var broker = new PipeServer(pipe, (r, ct) =>
        {
            calls.Enqueue(r.Operation); Console.WriteLine("Fixture received: " + r.Operation);
            object data = r.Operation switch
            {
                "cad_sessions" => new { sessions = new[] { new { session_id = session, document_id = document, revision = 1, name = "Synthetic fixture" } } },
                "cad_context" => new { name = "Synthetic fixture", units = "Millimeters", space = "model", selection = new { handles = Array.Empty<string>() } },
                "cad_catalog" => new { catalog = new { layers = new[] { new { name = "0", locked = false } } } },
                "cad_edit" => new { fixture = true, handles = new[] { "A" }, entities = new[] { new { handle = "A", type = "Line", start = new[] { 0, 0, 0 }, end = new[] { 10, 0, 0 } } } },
                "cad_lisp" => new { fixture = true, state = "completed", result = 3 },
                "cad_focus" => new { fixture = true, selected = true, handle = "A" },
                "cad_operation_status" => new { fixture = true, state = "completed", result = 3 },
                _ => new { fixture = true }
            };
            return Task.FromResult(new Response(r.RequestId, "completed", data, session, document, 1));
        });
        broker.Start();
        var work = Path.GetFullPath(Path.Combine(".runtime", "model-probe-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(work);
        string host = Path.GetFullPath("src/CadMcp.Host/bin/Release/net8.0-windows/CadMcp.Host.exe");
        var options = new ProviderOptions(cli, host, work, McpArguments: new[] { "--broker-pipe", pipe });
        using var catalogTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var catalog = await CodexCatalog.ReadAsync(options, catalogTimeout.Token);
        if (!catalog.Any(m => m.Id == "gpt-6-sol")) throw new IOException("GPT-6 Sol unavailable in this account");
        Console.WriteLine("PASS real catalog includes GPT-6 Sol; models: " + catalog.Count);
        var store = new ChatStateStore(Path.Combine(work, "chat"));
        string? conversation = resumeConversation;
        var results = new List<object>();
        foreach (var setting in new[] { (Model: "gpt-6-astra", Effort: "low"), (Model: "gpt-6-sol", Effort: "medium") })
        {
            if (resumeConversation is not null && setting.Model == "gpt-6-astra") continue;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            store.Save(new ChatState(0, cli, "claude.exe", host, work, conversation, "fixture history", "fixture-key", setting.Model, setting.Effort));
            var saved = store.Load()!;
            var provider = new CodexProvider(options with { Model = saved.CodexModel, ReasoningEffort = saved.CodexReasoningEffort }) { SessionId = saved.SessionId };
            int before = calls.Count;
            var events = new List<ChatEvent>();
            string id = setting.Model.EndsWith("sol") ? "sol" : "astra";
            string memory = conversation is null ? "Remember this conversation marker: " + marker + "." : "First print the conversation marker I asked you to remember in my previous message. Do not invent or search for it.";
            string prompt = memory + $" Integration test: CAD tools use a recording fixture, no actual AutoCAD/drawings. Only call the cad tools, no shell/files/web/other servers. Read sessions/context/edit_help/catalog, then call cad_edit operation_id model_{id}_edit with operations_json [{{\"op\":\"line\",\"start\":[0,0,0],\"end\":[10,0,0]}}], cad_lisp operation_id model_{id}_lisp code (+ 1 2), cad_focus handle A. Use fresh fixture IDs and revision 1. Poll operation status. I authorize these calls immediately without confirmations. Report results briefly.";
            await foreach (var e in provider.SendAsync(prompt, timeout.Token))
            {
                events.Add(e);
                if (e.Kind == "text") Console.Write(e.Text);
                if (e.Kind is "model" or "effort") Console.WriteLine(e.Kind + ": " + e.Text);
            }
            if (!events.Any(e => e.Kind == "completed") || !events.Any(e => e.Kind == "model" && e.Text == setting.Model) || !events.Any(e => e.Kind == "effort" && e.Text == setting.Effort)) throw new Exception("Wrong actual model selection");
            if (conversation is not null && provider.SessionId != conversation) throw new Exception("Model switch lost conversation id");
            if (conversation is not null && !string.Concat(events.Where(e => e.Kind == "text").Select(e => e.Text)).Contains(marker)) throw new Exception("Resumed model did not retain conversation marker");
            conversation = provider.SessionId;
            foreach (string tool in new[] { "cad_edit", "cad_lisp", "cad_focus" })
                if (!calls.ToArray()[before..].Contains(tool)) throw new Exception("Selected model did not reach CAD tool: " + tool);
            results.Add(new { model = setting.Model, effort = setting.Effort, resumed = id == "sol", success = true });
            File.WriteAllText(Path.Combine(work, "result.json"), JsonSerializer.Serialize(new { results, conversation, actual_drawings_changed = false }));
            Console.WriteLine("\nPASS actual selected model and CAD permissions: " + setting.Model);
        }
        File.WriteAllText(Path.Combine(work, "result.json"), JsonSerializer.Serialize(new { passed = true, results, preserved_conversation = true, actual_drawings_changed = false }));
        Console.WriteLine("Result: " + Path.Combine(work, "result.json"));
    }
}
