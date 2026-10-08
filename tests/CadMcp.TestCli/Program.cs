using System.Text.Json;
using CadMcp.Providers;

// Test command line for CAD MCP.
// - "app-server" and "-p" simulate the Codex and Claude Code wire protocols for the automated tests.
//   They never start a real CLI or model.
// - The --live-* options are opt-in probes that run the official CLIs against a recording CAD fixture.
//   They are excluded from the automated tests and run from the repository root.
Console.InputEncoding = new System.Text.UTF8Encoding(false);
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
if (args.Contains("--live-subagent-test"))
{
    int index = Array.IndexOf(args, "--live-subagent-test");
    await CadMcp.TestCli.SubagentProbe.Run(args[index + 1], args[index + 2]);
    return 0;
}
if (args.Contains("--live-codex-model-test"))
{
    var resumeIndex = Array.IndexOf(args, "--resume-conversation");
    await CadMcp.TestCli.CodexModelProbe.RunAsync(args[Array.IndexOf(args, "--live-codex-model-test") + 1], resumeIndex < 0 ? null : args[resumeIndex + 1]);
    return 0;
}
if (args.Contains("--live-codex-approval-test"))
{
    await CadMcp.TestCli.CodexApprovalProbe.RunAsync(args[Array.IndexOf(args, "--live-codex-approval-test") + 1]);
    return 0;
}
if (args.Contains("--cli-config-check"))
{
    string cli = args[Array.IndexOf(args, "--cli-config-check") + 1];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
    var models = await CodexCatalog.ReadAsync(new(cli, Path.GetFullPath("src/CadMcp.Host/bin/Release/net8.0-windows/CadMcp.Host.exe"), Environment.CurrentDirectory,
        OwnerId: "config-probe", CadSessionId: "fixture-session", CadDocumentId: "fixture-document"), timeout.Token);
    Console.WriteLine("Official CLI parsed pinned MCP and custom read-only reviewer config; model count: " + models.Count);
    return 0;
}
if (args.Contains("app-server")) { await FakeCodex.RunAsync(args); return 0; }
if (args.Contains("-p")) { await FakeClaude.RunAsync(); return 0; }
Console.Error.WriteLine("Usage: CadMcp.TestCli app-server | -p | --live-subagent-test <codex|claude> <cli> | --live-codex-model-test <cli> [--resume-conversation <id>] | --live-codex-approval-test <cli> | --cli-config-check <cli>");
return 2;

/// <summary>Codex app-server protocol, as far as the CAD MCP chat uses it.</summary>
internal static class FakeCodex
{
    public static async Task RunAsync(string[] args)
    {
        string? threadModel = null;
        int writerErrors = 0;
        foreach (var tool in new[] { "cad_edit", "cad_lisp", "cad_focus" })
            if (!args.Contains($"mcp_servers.cad.tools.{tool}.approval_mode=\"approve\""))
                throw new Exception("CAD editing permission was not configured: " + tool);
        // Real CLIs can print warnings that are not JSON; the client must skip them.
        Console.WriteLine("Codex fixture: this banner is not JSON");
        while (Console.ReadLine() is { } line)
        {
            var e = JsonDocument.Parse(line).RootElement;
            var method = e.GetProperty("method").GetString();
            if (method == "initialize") Console.WriteLine("{\"id\":1,\"result\":{}}");
            if (method == "model/list")
            {
                bool secondPage = e.GetProperty("params").TryGetProperty("cursor", out _);
                Console.WriteLine(JsonSerializer.Serialize(new { id = 4, result = new {
                    data = secondPage ? new object[] {
                        new { model = "available-model", isDefault = true, hidden = false, defaultReasoningEffort = "medium", supportedReasoningEfforts = new[] { new { reasoningEffort = "medium" } } },
                        new { model = "alternative-model", isDefault = false, hidden = false, defaultReasoningEffort = "low", supportedReasoningEfforts = new[] { new { reasoningEffort = "low" }, new { reasoningEffort = "high" } } } }
                        // A malformed entry must be skipped, not break the catalog.
                        : new object[] { new { model = "hidden-model", isDefault = true, hidden = true, defaultReasoningEffort = "low" }, new { model = (string?)null, hidden = (bool?)null } },
                    nextCursor = secondPage ? null : "page2" } }));
            }
            if (method is "thread/start" or "thread/resume" or "turn/start")
            {
                if (!e.GetProperty("params").TryGetProperty("model", out var model) || model.GetString() is not ("available-model" or "alternative-model"))
                    throw new Exception("Provider inherited unavailable configured model");
            }
            if (method is "thread/start" or "thread/resume")
            {
                threadModel = e.GetProperty("params").GetProperty("model").GetString();
                if (!e.GetProperty("params").GetProperty("developerInstructions").GetString()!.Contains("cad_edit_help")) throw new Exception("Missing stable CAD instructions");
                if (e.GetProperty("params").GetProperty("approvalPolicy").GetString() != "never" ||
                    e.GetProperty("params").GetProperty("sandbox").GetString() != "read-only")
                    throw new Exception("CAD permission fix widened filesystem permissions");
            }
            if (method == "thread/resume" && e.GetProperty("params").GetProperty("threadId").GetString() == "retry-thread" && writerErrors++ == 0)
                Console.WriteLine("{\"id\":2,\"error\":{\"code\":-32600,\"message\":\"thread already has an active writer\"}}");
            else if (method is "thread/start" or "thread/resume") Console.WriteLine("{\"id\":2,\"result\":{\"thread\":{\"id\":\"test-thread\"}}}");
            if (method != "turn/start") continue;
            var inputs = e.GetProperty("params").GetProperty("input");
            var prompt = inputs[0].GetProperty("text").GetString()!;
            if (prompt.StartsWith("ATTACHMENT"))
            {
                if (!prompt.Contains("Чертёж ✓") || !inputs.EnumerateArray().Any(x => x.GetProperty("type").GetString() == "localImage" && File.Exists(x.GetProperty("path").GetString())))
                    throw new Exception("Codex did not receive text and native image attachment");
            }
            bool explicitSelection = prompt == "SELECT";
            if (e.GetProperty("params").GetProperty("model").GetString() != (explicitSelection ? "alternative-model" : "available-model") ||
                threadModel != (explicitSelection ? "alternative-model" : "available-model") ||
                e.GetProperty("params").GetProperty("effort").GetString() != (explicitSelection ? "high" : "medium")) throw new Exception("Wrong requested model or effort on wire");
            if (prompt is "STEER" or "STEER_REJECT")
            {
                Console.WriteLine("{\"id\":3,\"result\":{\"turn\":{\"id\":\"live-turn\"}}}");
                var next = JsonDocument.Parse(Console.ReadLine()!).RootElement;
                if (next.GetProperty("method").GetString() != "turn/steer" || next.GetProperty("params").GetProperty("expectedTurnId").GetString() != "live-turn" || next.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString() != "Уточнение ✓") throw new Exception("Invalid same-turn steering contract");
                int inputId = next.GetProperty("id").GetInt32();
                Console.WriteLine(prompt == "STEER" ? JsonSerializer.Serialize(new { id = inputId, result = new { turnId = "live-turn" } }) : JsonSerializer.Serialize(new { id = inputId, error = new { code = -32600, message = "turn already completed" } }));
                Console.WriteLine("{\"method\":\"item/agentMessage/delta\",\"params\":{\"delta\":\"Учёл уточнение\"}}");
                Console.WriteLine("{\"method\":\"turn/completed\",\"params\":{\"turn\":{\"status\":\"completed\"}}}");
                return;
            }
            if (prompt == "SILENT") { await Task.Delay(TimeSpan.FromMinutes(1)); return; }
            Console.WriteLine("{\"id\":3,\"result\":{}}");
            if (prompt == "REQUEST")
            {
                // A server-initiated request must be declined without ending the turn.
                Console.WriteLine("{\"id\":\"srv-1\",\"method\":\"item/tool/requestUserInput\",\"params\":{\"threadId\":\"test-thread\"}}");
                var reply = JsonDocument.Parse(Console.ReadLine()!).RootElement;
                if (reply.GetProperty("id").GetString() != "srv-1" || !reply.TryGetProperty("error", out _)) throw new Exception("Server request was not declined");
                Console.WriteLine("{\"method\":\"item/agentMessage/delta\",\"params\":{\"delta\":\"Продолжил\"}}");
                Console.WriteLine("{\"method\":\"turn/completed\",\"params\":{\"turn\":{\"status\":\"completed\"}}}");
                continue;
            }
            Console.WriteLine("{\"method\":\"item/reasoning/summaryTextDelta\",\"params\":{\"delta\":\"Проверяю размеры чертежа\",\"itemId\":\"r1\",\"summaryIndex\":0,\"threadId\":\"test-thread\",\"turnId\":\"t1\"}}");
            Console.WriteLine("{\"method\":\"item/started\",\"params\":{\"item\":{\"type\":\"mcpToolCall\",\"tool\":\"cad_search\"}}}");
            Console.WriteLine("{\"method\":\"item/completed\",\"params\":{\"item\":{\"type\":\"mcpToolCall\",\"tool\":\"cad_search\",\"status\":\"completed\"}}}");
            Console.WriteLine("{\"method\":\"item/started\",\"params\":{\"threadId\":\"test-thread\",\"item\":{\"type\":\"collabAgentToolCall\",\"tool\":\"spawnAgent\"}}}");
            Console.WriteLine("{\"method\":\"item/agentMessage/delta\",\"params\":{\"threadId\":\"child-thread\",\"delta\":\"CHILD-ONLY\"}}");
            Console.WriteLine("{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"child-thread\",\"turn\":{\"status\":\"completed\"}}}");
            Console.WriteLine("{\"method\":\"item/completed\",\"params\":{\"threadId\":\"test-thread\",\"item\":{\"type\":\"collabAgentToolCall\",\"tool\":\"wait\",\"status\":\"completed\"}}}");
            Console.WriteLine("{\"method\":\"item/agentMessage/delta\",\"params\":{\"delta\":\"Сеть ✓\"}}");
            Console.WriteLine("{\"method\":\"turn/completed\",\"params\":{\"turn\":{\"status\":\"completed\"}}}");
        }
    }
}

/// <summary>Claude Code stream-json protocol with --input-format stream-json and --replay-user-messages.</summary>
internal static class FakeClaude
{
    public static async Task RunAsync()
    {
        var input = JsonDocument.Parse(Console.ReadLine()!).RootElement;
        string initial = input.GetProperty("uuid").GetString()!;
        var blocks = input.GetProperty("message").GetProperty("content");
        string prompt = blocks[0].GetProperty("text").GetString()!;
        if (prompt.StartsWith("ATTACHMENT") && !blocks.EnumerateArray().Any(x => x.GetProperty("type").GetString() == "image" && Convert.FromBase64String(x.GetProperty("source").GetProperty("data").GetString()!).Length > 8))
            throw new Exception("Claude image input missing");
        // Real CLIs can print warnings that are not JSON; the client must skip them.
        Console.WriteLine("Claude fixture: this banner is not JSON");
        JsonElement ReadFollowup()
        {
            var next = JsonDocument.Parse(Console.ReadLine()!).RootElement;
            if (next.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString() != "Уточнение ✓") throw new Exception("Claude live input lost Unicode");
            return next;
        }
        switch (prompt)
        {
            case "STEER":
            {
                // One result per message: the follow-up is acknowledged during the first turn and answered by the second.
                var next = ReadFollowup();
                Console.WriteLine(next.GetRawText());
                Console.WriteLine("{\"type\":\"result\",\"is_error\":false,\"result\":\"Первый результат\"}");
                Console.WriteLine("{\"type\":\"result\",\"is_error\":false,\"result\":\"Учёл уточнение\"}");
                return;
            }
            case "MERGE" or "MERGE_LEGACY":
            {
                // Queued messages merged into one turn: a single result answers both. The process stays open.
                var next = ReadFollowup();
                Console.WriteLine(next.GetRawText());
                Console.WriteLine(prompt == "MERGE"
                    ? JsonSerializer.Serialize(new { type = "result", is_error = false, result = "Учёл уточнение", user_message_uuids = new[] { initial, next.GetProperty("uuid").GetString() } })
                    : "{\"type\":\"result\",\"is_error\":false,\"result\":\"Учёл уточнение\"}");
                await Task.Delay(TimeSpan.FromMinutes(1));
                return;
            }
            case "NOACK":
            {
                // The follow-up is never acknowledged; the turn must still finish.
                ReadFollowup();
                Console.WriteLine("{\"type\":\"result\",\"is_error\":false,\"result\":\"Готово\"}");
                await Task.Delay(TimeSpan.FromMinutes(1));
                return;
            }
            case "SILENT": await Task.Delay(TimeSpan.FromMinutes(1)); return;
        }
        Console.WriteLine("{\"type\":\"system\",\"session_id\":\"test-claude\"}");
        Console.WriteLine("{\"type\":\"assistant\",\"session_id\":\"test-claude\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"tool-1\",\"name\":\"mcp__cad__cad_edit\"}]}}");
        Console.WriteLine("{\"type\":\"user\",\"session_id\":\"test-claude\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tool-1\",\"content\":\"ok\"}]}}");
        Console.WriteLine("{\"type\":\"stream_event\",\"session_id\":\"test-claude\",\"event\":{\"delta\":{\"text\":\"Сеть \"}}}");
        Console.WriteLine("{\"type\":\"stream_event\",\"session_id\":\"test-claude\",\"event\":{\"delta\":{\"text\":\"✓\"}}}");
        Console.WriteLine("{\"type\":\"result\",\"session_id\":\"test-claude\",\"is_error\":false,\"result\":\"Сеть ✓\"}");
    }
}
