using System.Text.Json;

namespace CadMcp.TestCli;

/// <summary>Offline ACP fixture. It never authenticates, calls a model, or opens AutoCAD.</summary>
internal static class FakeSubscriptionAcp
{
    public static async Task<int> RunAsync(string[] args)
    {
        bool kimi = args.FirstOrDefault() == "acp";
        string prefix = kimi ? "kimi-code/" : "";
        string session = "acp-fixture-session";
        string selectedModel = prefix + "fixture-default";
        string trace = Path.Combine(Environment.CurrentDirectory, "acp-fixture.jsonl");
        void Record(string direction, JsonElement value) => File.AppendAllText(trace,
            JsonSerializer.Serialize(new { direction, value }) + Environment.NewLine);
        void Write(object value)
        {
            var json = JsonSerializer.SerializeToElement(value);
            Record("out", json);
            Console.WriteLine(json.GetRawText());
        }
        JsonElement? Read()
        {
            string? line = Console.ReadLine();
            if (line is null) return null;
            using var json = JsonDocument.Parse(line);
            var result = json.RootElement.Clone();
            Record("in", result);
            return result;
        }
        void Reply(JsonElement id, object result) => Write(new { jsonrpc = "2.0", id, result });
        void Error(JsonElement id, string message) => Write(new { jsonrpc = "2.0", id, error = new { code = -32000, message } });
        void Update(object update, string? target = null) => Write(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = target ?? session, update } });
        object Models() => new
        {
            currentModelId = selectedModel,
            availableModels = new[]
            {
                new { modelId = prefix + "fixture-default", name = "Fixture default" },
                new { modelId = prefix + "fixture-alt", name = "Fixture alternative" }
            }
        };
        object Modes() => new
        {
            currentModeId = "default",
            availableModes = new[] { new { id = "default", name = "Manual" }, new { id = "plan", name = "Plan" } }
        };
        object[] ConfigOptions() => [new
        {
            id = "model", name = "Model", category = "model", type = "select", currentValue = selectedModel,
            options = new[] { new { value = prefix + "fixture-default", name = "Fixture default" }, new { value = prefix + "fixture-alt", name = "Fixture alternative" } }
        }];
        async Task<JsonElement> Reverse(string id, string method, object parameters)
        {
            Write(new { jsonrpc = "2.0", id, method, @params = parameters });
            while (Read() is { } answer)
            {
                if (answer.TryGetProperty("id", out var received) && received.ValueKind == JsonValueKind.String && received.GetString() == id)
                    return answer;
                await Task.Yield();
            }
            throw new IOException("ACP client closed before replying to " + method);
        }
        bool Allowed(JsonElement reply) => reply.TryGetProperty("result", out var result)
            && result.TryGetProperty("outcome", out var outcome)
            && outcome.TryGetProperty("optionId", out var option)
            && option.GetString() == "allow-once";
        object[] permissionOptions =
        [
            new { optionId = "allow-always", name = "Always", kind = "allow_always" },
            new { optionId = "allow-once", name = "Once", kind = "allow_once" },
            new { optionId = "reject-once", name = "Reject", kind = "reject_once" }
        ];
        while (Read() is { } message)
        {
            if (!message.TryGetProperty("method", out var methodValue)) continue;
            string? method = methodValue.GetString();
            var id = message.TryGetProperty("id", out var requestId) ? requestId : default;
            var parameters = message.TryGetProperty("params", out var values) ? values : default;
            switch (method)
            {
                case "initialize":
                    if (parameters.GetProperty("protocolVersion").GetInt32() != 1) throw new IOException("Unsupported ACP fixture version");
                    var capabilities = parameters.GetProperty("clientCapabilities");
                    if (capabilities.TryGetProperty("terminal", out var terminal) && terminal.ValueKind == JsonValueKind.True)
                        throw new IOException("Adapter advertised unrestricted terminal access");
                    if (capabilities.TryGetProperty("fs", out var fs) && fs.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.True))
                        throw new IOException("Adapter advertised unrestricted filesystem access");
                    Reply(id, new
                    {
                        protocolVersion = 1,
                        agentCapabilities = new { loadSession = true, promptCapabilities = new { image = true, embeddedContext = true } },
                        agentInfo = new { name = kimi ? "Kimi Code CLI" : "Qwen Code", version = "0.28.0" },
                        authMethods = new[] { new { id = kimi ? "kimi-code" : "qwen-subscription", name = "Account subscription" } }
                    });
                    break;
                case "authenticate":
                    Error(id, "The adapter must not reset or replace existing CLI authentication");
                    break;
                case "session/new":
                case "session/load":
                    if (parameters.GetProperty("cwd").GetString()!.Contains("acp-auth-failure", StringComparison.Ordinal))
                    { Error(id, "Authentication required: log in with your subscription account"); break; }
                    if (method == "session/load" && parameters.GetProperty("sessionId").GetString() == "missing-session")
                    { Error(id, "Session not found"); break; }
                    if (method == "session/load") session = parameters.GetProperty("sessionId").GetString()!;
                    var servers = parameters.GetProperty("mcpServers");
                    foreach (var server in servers.EnumerateArray())
                    {
                        if (server.GetProperty("name").GetString() != "cad") throw new IOException("Unrelated MCP server injected");
                        var env = server.GetProperty("env").EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("value").GetString());
                        foreach (var key in new[] { "CAD_MCP_OWNER_ID", "CAD_MCP_SESSION_ID", "CAD_MCP_DOCUMENT_ID" })
                            if (!env.ContainsKey(key)) throw new IOException("Missing scoped MCP variable: " + key);
                    }
                    if (method == "session/load")
                        Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "OLD REPLAY MUST STAY HIDDEN" } });
                    if (kimi) Reply(id, new { sessionId = session, configOptions = ConfigOptions(), modes = Modes() });
                    else Reply(id, new { sessionId = session, models = Models(), modes = Modes() });
                    break;
                case "session/set_mode":
                    if (parameters.GetProperty("modeId").GetString() != "default") throw new IOException("Automatic permissions selected");
                    Reply(id, new { });
                    break;
                case "session/set_model":
                    selectedModel = parameters.GetProperty("modelId").GetString()!;
                    if (selectedModel != prefix + "fixture-default" && selectedModel != prefix + "fixture-alt")
                    { Error(id, "Unavailable model"); break; }
                    Reply(id, new { });
                    break;
                case "session/set_config_option":
                    if (parameters.GetProperty("configId").GetString() != "model") throw new IOException("Unsupported config picker");
                    selectedModel = parameters.GetProperty("value").GetString()!;
                    if (selectedModel != prefix + "fixture-default" && selectedModel != prefix + "fixture-alt")
                    { Error(id, "Unavailable model"); break; }
                    Reply(id, new { configOptions = ConfigOptions() });
                    break;
                case "session/prompt":
                    string prompt = string.Join("\n", parameters.GetProperty("prompt").EnumerateArray()
                        .Where(p => p.TryGetProperty("text", out _)).Select(p => p.GetProperty("text").GetString()));
                    if (prompt.Contains("ACP_EOF", StringComparison.Ordinal))
                    {
                        Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "Незавершённый ответ" } });
                        return 0;
                    }
                    if (prompt.Contains("ACP_CANCEL", StringComparison.Ordinal))
                    {
                        Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "Начато" } });
                        while (Read() is { } cancel)
                            if (cancel.TryGetProperty("method", out var cancelMethod) && cancelMethod.GetString() == "session/cancel")
                            { Reply(id, new { stopReason = "cancelled" }); return 0; }
                        return 0;
                    }
                    if (prompt.Contains("ACP_PERMISSIONS", StringComparison.Ordinal))
                    {
                        object cad = kimi
                            ? new { toolCallId = "cad-1", title = "mcp__cad__cad_edit", kind = "other", status = "pending" }
                            : new { toolCallId = "cad-1", title = "Edit drawing", kind = "other", status = "pending", _meta = new { toolName = "mcp__cad__cad_edit" } };
                        if (!Allowed(await Reverse("server-cad", "session/request_permission", new { sessionId = session, toolCall = cad, options = permissionOptions })))
                            throw new IOException("Scoped CAD call was not approved once");
                        object spoofed = new { toolCallId = "bash-1", title = "mcp__cad__cad_edit", kind = "execute", status = "pending", _meta = new { toolName = "Bash" } };
                        if (Allowed(await Reverse("server-spoof", "session/request_permission", new { sessionId = session, toolCall = spoofed, options = permissionOptions })))
                            throw new IOException("Spoofed Bash was approved");
                        if (Allowed(await Reverse("server-foreign", "session/request_permission", new { sessionId = "another-session", toolCall = cad, options = permissionOptions })))
                            throw new IOException("Another session was approved");
                        foreach (string blocked in new[] { "fs/read_text_file", "fs/write_text_file", "terminal/create" })
                        {
                            var denied = await Reverse("server-" + blocked, blocked, new { sessionId = session, path = "unrelated.txt", command = "ignored" });
                            if (!denied.TryGetProperty("error", out _)) throw new IOException("Unexpected client capability: " + blocked);
                        }
                    }
                    if (prompt.Contains("ACP_ATTACHMENTS", StringComparison.Ordinal)
                        && (!prompt.Contains("Чертёж ✓", StringComparison.Ordinal)
                            || !parameters.GetProperty("prompt").EnumerateArray().Any(p => p.TryGetProperty("type", out var type) && type.GetString() == "image")))
                        throw new IOException("Missing captured text or image input");
                    Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "FOREIGN SESSION MUST STAY HIDDEN" } }, "foreign-session");
                    object toolStart = kimi
                        ? new { sessionUpdate = "tool_call", toolCallId = "cad-2", title = "mcp__cad__cad_edit", kind = "other", status = "in_progress" }
                        : new { sessionUpdate = "tool_call", toolCallId = "cad-2", title = "Edit drawing", kind = "other", status = "in_progress", _meta = new { toolName = "mcp__cad__cad_edit" } };
                    Update(toolStart);
                    Update(new { sessionUpdate = "tool_call_update", toolCallId = "cad-2", status = "completed", content = new[] { new { type = "content", content = new { type = "text", text = "confirmed" } } } });
                    // Context occupancy is not billed usage and must never become a token-spending counter.
                    Update(new { sessionUpdate = "usage_update", used = 9999, size = 128000 });
                    Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "Подписка " } });
                    Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "✓" } });
                    Reply(id, new { stopReason = "end_turn" });
                    break;
                case "session/cancel":
                    break;
                default:
                    if (id.ValueKind != JsonValueKind.Undefined) Error(id, "Unsupported fixture method " + method);
                    break;
            }
        }
        return 0;
    }
}
