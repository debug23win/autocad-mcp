using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CadMcp.Core;
using CadMcp.Providers;

namespace CadMcp.Tests;

internal static class CodexApprovalProbe
{
    public static async Task RunAsync(string cli)
    {
        const string session = "approval-fixture-session", document = "approval-fixture-document";
        string pipe = "cadmcp-approval-probe-" + Guid.NewGuid().ToString("N");
        var calls = new ConcurrentQueue<string>();
        using var broker = new PipeServer(pipe, (r, ct) =>
        {
            calls.Enqueue(r.Operation);
            Console.WriteLine("CAD fixture received: " + r.Operation);
            object data = r.Operation switch
            {
                "cad_sessions" => new { sessions = new[] { new { session_id = session, document_id = document, revision = 1, name = "Synthetic approval fixture" } } },
                "cad_context" => new { name = "Synthetic approval fixture", units = "Millimeters", space = "model", selection = new { handles = Array.Empty<string>() }, capabilities = new[] { "cad_edit", "cad_lisp", "cad_focus" } },
                "cad_catalog" => new { catalog = new { layers = new[] { new { name = "0", locked = false } }, blocks = Array.Empty<object>(), text_styles = Array.Empty<object>() } },
                "cad_edit" => new { fixture = true, operation_id = r.Data.Text("operation_id"), handles = new[] { "A" }, entities = new[] { new { handle = "A", type = "Line", start = new[] { 0, 0, 0 }, end = new[] { 10, 0, 0 } } } },
                "cad_lisp" => new { fixture = true, state = "completed", result = 3 },
                "cad_focus" => new { fixture = true, selected = true, handle = "A" },
                "cad_operation_status" => new { fixture = true, state = "completed", result = 3 },
                _ => new { fixture = true }
            };
            return Task.FromResult(new Response(r.RequestId, "completed", data, session, document, 1));
        });
        broker.Start();
        var work = Path.GetFullPath(Path.Combine(".runtime", "approval-probe-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(work);
        string host = Path.GetFullPath("src/CadMcp.Host/bin/Release/net8.0-windows/CadMcp.Host.exe");
        var options = new ProviderOptions(cli, host, work);
        // Use production permission arguments, routing only this test's MCP host to a private pipe.
        var arguments = new CodexProvider(options).Arguments()[..^1].Concat(new[] {
            "-c", "mcp_servers.cad.args=" + JsonSerializer.Serialize(new[] { "--broker-pipe", pipe }), "app-server" });
        var process = Process.Start(ProviderProcess.StartInfo(options, arguments)) ?? throw new IOException("Cannot start official Codex");
        var stderr = ProviderProcess.DrainErrors(process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var cancellation = timeout.Token.Register(() => ProviderProcess.Stop(process));
        async Task Send(object body)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(body).AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
        }
        async Task<JsonElement> Read()
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("Codex stdout closed");
            return JsonDocument.Parse(line).RootElement.Clone();
        }
        async Task<JsonElement> Rpc(int id, string method, object parameters)
        {
            await Send(new { id, method, @params = parameters });
            while (true)
            {
                var reply = await Read();
                if (reply.TryGetProperty("id", out var responseId) && !reply.TryGetProperty("method", out _) && responseId.GetInt32() == id)
                {
                    if (reply.TryGetProperty("error", out var error)) throw new IOException("Codex RPC: " + error);
                    return reply.GetProperty("result").Clone();
                }
            }
        }
        bool succeeded = false;
        try
        {
            await Rpc(1, "initialize", new { clientInfo = new { name = "cad_mcp_approval_probe", title = "CAD MCP approval probe", version = "0.2.1" } });
            await Send(new { method = "initialized", @params = new { } });
            var config = await Rpc(90, "config/read", new { cwd = work, includeLayers = false });
            var cad = config.GetProperty("config").GetProperty("mcp_servers").GetProperty("cad");
            foreach (string tool in new[] { "cad_edit", "cad_lisp", "cad_focus" })
                if (cad.GetProperty("tools").GetProperty(tool).GetProperty("approval_mode").GetString() != "approve")
                    throw new Exception("Effective config did not approve " + tool);
            Console.WriteLine("PASS official CLI effective CAD permissions");
            var models = new List<JsonElement>();
            string? cursor = null;
            do
            {
                var catalog = await Rpc(4, "model/list", new { limit = 100, includeHidden = false, cursor });
                models.AddRange(catalog.GetProperty("data").EnumerateArray().Where(m => !m.TryGetProperty("hidden", out var hidden) || !hidden.GetBoolean()).Select(m => m.Clone()));
                cursor = catalog.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            } while (!string.IsNullOrEmpty(cursor));
            var selected = models.FirstOrDefault(m => m.TryGetProperty("isDefault", out var d) && d.GetBoolean());
            if (selected.ValueKind == JsonValueKind.Undefined) selected = models.First();
            string model = selected.GetProperty("model").GetString()!;
            string effort = selected.GetProperty("defaultReasoningEffort").GetString()!;
            Console.WriteLine("Official CLI model: " + model);
            string? threadId = null;
            foreach (string mode in new[] { "new", "resume" })
            {
                var thread = threadId is null
                    ? await Rpc(2, "thread/start", new { model, cwd = work, developerInstructions = CadAgent.Instructions, approvalPolicy = "never", sandbox = "read-only" })
                    : await Rpc(2, "thread/resume", new { model, threadId, cwd = work, developerInstructions = CadAgent.Instructions, approvalPolicy = "never", sandbox = "read-only" });
                threadId = thread.GetProperty("thread").GetProperty("id").GetString()!;
                var before = calls.ToArray().Length;
                string prompt = $"Integration test: this CAD server is a recording fixture, with no AutoCAD/drawings connected. Only call cad MCP tools; do not use shell, files, web or other servers. Read sessions/context and edit_help/catalog, then call all three tools: cad_edit with operation_id probe_{mode}_edit and operations_json [{{\"op\":\"line\",\"start\":[0,0,0],\"end\":[10,0,0]}}]; cad_lisp with operation_id probe_{mode}_lisp and code (+ 1 2); cad_focus with handle A. For all use the fresh fixture session/document IDs and revision 1. Check cad_operation_status. I authorize these three fixture calls immediately without confirmations. Report the tool results briefly.";
                await Rpc(3, "turn/start", new { threadId, model, effort, input = new[] { new { type = "text", text = prompt } } });
                while (true)
                {
                    var e = await Read();
                    if (!e.TryGetProperty("method", out var method)) continue;
                    string? name = method.GetString();
                    if (e.TryGetProperty("id", out var requestId))
                    {
                        await Send(new { id = requestId.Clone(), error = new { code = -32601, message = "Test must not require approval" } });
                        throw new Exception("Unexpected server request: " + name);
                    }
                    if (name == "error")
                    {
                        var parameters = e.GetProperty("params");
                        if (!parameters.TryGetProperty("willRetry", out var retry) || !retry.GetBoolean())
                            throw new IOException("Codex turn: " + CodexProvider.ErrorMessage(parameters));
                    }
                    if (name == "item/agentMessage/delta") Console.Write(e.GetProperty("params").GetProperty("delta").GetString());
                    if (name == "item/completed" && e.GetProperty("params").TryGetProperty("item", out var item) && item.TryGetProperty("type", out var type) && type.GetString() == "mcpToolCall")
                        Console.WriteLine("MCP result: " + item.GetProperty("tool").GetString() + " " + (item.TryGetProperty("status", out var status) ? status.GetString() : "unknown"));
                    if (name == "turn/completed")
                    {
                        if (e.GetProperty("params").GetProperty("turn").GetProperty("status").GetString() != "completed") throw new Exception("Turn failed");
                        break;
                    }
                }
                var turnCalls = calls.ToArray()[before..];
                foreach (var tool in new[] { "cad_edit", "cad_lisp", "cad_focus" })
                    if (!turnCalls.Contains(tool)) throw new Exception("Official CLI never reached fixture: " + tool + " (" + mode + ")");
                Console.WriteLine("\nPASS official CLI CAD edits without approval: " + mode);
                if (mode == "new")
                {
                    // Production starts a fresh app-server for each message. Verify cold
                    // resume, rather than retaining permissions on an already loaded thread.
                    ProviderProcess.Stop(process);
                    await process.WaitForExitAsync(timeout.Token);
                    await stderr;
                    process.Dispose();
                    process = Process.Start(ProviderProcess.StartInfo(options, arguments)) ?? throw new IOException("Cannot restart official Codex");
                    stderr = ProviderProcess.DrainErrors(process);
                    await Rpc(1, "initialize", new { clientInfo = new { name = "cad_mcp_approval_probe", title = "CAD MCP approval probe", version = "0.2.1" } });
                    await Send(new { method = "initialized", @params = new { } });
                    Console.WriteLine("Restarted official CLI before thread/resume");
                }
            }
            File.WriteAllText(Path.Combine(work, "result.json"), JsonSerializer.Serialize(new { passed = true, model, modes = new[] { "new", "resume" }, calls = calls.ToArray(), actual_drawings_changed = false }));
            Console.WriteLine("Result: " + Path.Combine(work, "result.json"));
            succeeded = true;
        }
        finally
        {
            ProviderProcess.Stop(process);
            var errors = await stderr;
            process.Dispose();
            if (!succeeded && !string.IsNullOrWhiteSpace(errors)) Console.Error.WriteLine(errors);
        }
    }
}
