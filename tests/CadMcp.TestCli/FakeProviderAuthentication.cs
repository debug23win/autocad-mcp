using System.Text.Json;
using System.Text.Json.Nodes;
using CadMcp.Core;

// Strict protocol fixtures only. No real login, external endpoint or model is used.
internal static class FakeProviderAuthentication
{
    private static string Mode => Path.GetFileName(Environment.CurrentDirectory);
    private static void Trace(string method) => File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "auth-protocol.txt"), method + "\n");
    private static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
    public static int ClaudeVersion()
    {
        if (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is not null) throw new Exception("Version probe received an API key");
        Trace("version");
        Console.WriteLine(Mode == "auth-claude-old" ? "2.1.299 (Claude Code)" : "2.1.327 (Claude Code)");
        return 0;
    }
    public static async Task CodexAsync(string[] args)
    {
        foreach (string name in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL", "OPENAI_FEDERATION_RULE_ID" })
            if (Environment.GetEnvironmentVariable(name) is not null) throw new Exception("Inherited API authentication/routing was not removed: " + name);
        bool configured = false, authenticated = false, listed = false;
        string? key = null;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var json = JsonDocument.Parse(line); var request = json.RootElement;
            string method = request.GetProperty("method").GetString()!; Trace(method);
            int id = request.TryGetProperty("id", out var idValue) ? idValue.GetInt32() : 0;
            if (method == "initialize") { Write(new { id, result = new { } }); continue; }
            if (method == "initialized") continue;
            if (method == "config/read")
            {
                configured = true;
                var computer = FakeCodex.ComputerConfig(args);
                if (Mode == "auth-cua-reject") computer["default_app_access"] = "allow";
                var config = new JsonObject { ["computer_use"] = computer };
                foreach (string name in new[] { "cli_auth_credentials_store", "model_provider", "openai_base_url", "chatgpt_base_url" })
                {
                    string argument = args.LastOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal)) ?? throw new Exception("Missing authentication override: " + name);
                    config[name] = JsonNode.Parse(argument[(name.Length + 1)..]);
                }
                if (Mode == "auth-config-reject") config["model_provider"] = "custom";
                Write(new { id, result = new { config } }); continue;
            }
            if (method == "account/login/start")
            {
                if (!configured || Mode is "auth-config-reject" or "auth-cua-reject") throw new Exception("API key sent before configuration checks");
                var parameters = request.GetProperty("params");
                if (parameters.GetProperty("type").GetString() != "apiKey") throw new Exception("Wrong login type");
                key = parameters.GetProperty("apiKey").GetString()!;
                if (string.IsNullOrEmpty(key) || args.Any(value => value.Contains(key, StringComparison.Ordinal))) throw new Exception("Key missing or exposed in process arguments");
                if (Mode == "auth-login-reject") { Write(new { id, error = new { code = -32600, message = "API login rejected " + key } }); continue; }
                if (Mode == "auth-login-shape") { Write(new { id, result = new { type = "chatgpt" } }); continue; }
                authenticated = true; Write(new { id, result = new { type = "apiKey" } }); continue;
            }
            if (method == "model/list")
            {
                if (!authenticated) throw new Exception("Model discovery started before API login confirmation");
                if (Mode is "auth-key-echo" or "auth-raw-exit")
                {
                    Console.WriteLine("echoed stdout credential " + key); Console.Error.WriteLine("echoed stderr credential " + key);
                    if (Mode == "auth-raw-exit") return;
                    Write(new { id, error = new { code = -32600, message = JsonSerializer.Serialize(new { error = new { message = "catalog refused " + key } }) } }); continue;
                }
                listed = true;
                Write(new { id, result = new { data = new[]
                {
                    new { model = "available-model", isDefault = true, hidden = false, defaultReasoningEffort = "medium", supportedReasoningEfforts = new[] { new { reasoningEffort = "medium" } } },
                    new { model = "alternative-model", isDefault = false, hidden = false, defaultReasoningEffort = "high", supportedReasoningEfforts = new[] { new { reasoningEffort = "high" } } }
                } } }); continue;
            }
            if (method is "thread/start" or "thread/resume")
            {
                if (!configured || !authenticated || !listed) throw new Exception("Thread started before authentication/model checks");
                Write(new { id, result = new { thread = new { id = "test-thread" } } }); continue;
            }
            if (method == "turn/start")
            {
                if (!configured || !authenticated || !listed) throw new Exception("Turn started before authentication/model checks");
                Write(new { id, result = new { turn = new { id = "api-turn" } } });
                string prompt = request.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString()!;
                if (prompt == "AUTH_STREAM")
                {
                    int middle = key!.Length / 2;
                    foreach (string delta in new[] { "before " + key[..middle], key[middle..] + " after" })
                    {
                        Write(new { method = "item/reasoning/summaryTextDelta", @params = new { delta, threadId = "test-thread" } });
                        Write(new { method = "item/agentMessage/delta", @params = new { delta, threadId = "test-thread" } });
                    }
                }
                else Write(new { method = "item/agentMessage/delta", @params = new { delta = "API turn ✓", threadId = "test-thread" } });
                Write(new { method = "turn/completed", @params = new { threadId = "test-thread", turn = new { status = "completed" } } }); return;
            }
            throw new Exception("Unexpected API protocol method (including subscription rate limits): " + method);
        }
    }
    public static string? ClaudeKey(string[] args)
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST") != "1") return null;
        Trace("claude-start");
        string key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? throw new Exception("Claude API key missing");
        if (Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL") != "https://api.anthropic.com" ||
            Environment.GetEnvironmentVariable("CLAUDE_CODE_SUBPROCESS_ENV_SCRUB") != "1" ||
            Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN") is not null ||
            Environment.GetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS") is not null ||
            args.Any(value => value.Contains(key, StringComparison.Ordinal)))
            throw new Exception("Claude API routing, auth isolation or argument secrecy failed");
        return key;
    }
    public static bool ClaudeCatalogError(JsonElement input, string? key)
    {
        if (key is null || Mode != "auth-key-echo") return false;
        Write(new { type = "control_response", response = new { subtype = "error", request_id = input.GetProperty("request_id").GetString(), error = "Invalid API key " + key } });
        return true;
    }
    public static bool ClaudeRuntime(string prompt, string key)
    {
        if (Mode is "auth-key-echo" or "auth-raw-exit")
        {
            Console.WriteLine("echoed stdout credential " + key); Console.Error.WriteLine("echoed stderr credential " + key);
            if (Mode != "auth-raw-exit") Write(new { type = "result", is_error = true, errors = new[] { "Invalid API key " + key } });
            return true;
        }
        if (prompt != "AUTH_STREAM") return false;
        Write(new { type = "rate_limit_event", rate_limit_info = new { status = "allowed_warning", rateLimitType = "seven_day", utilization = 0.9 } });
        int middle = key.Length / 2;
        foreach (string delta in new[] { "before " + key[..middle], key[middle..] + " after" })
        {
            Write(new { type = "stream_event", @event = new { delta = new { thinking = delta } } });
            Write(new { type = "stream_event", @event = new { delta = new { text = delta } } });
        }
        Write(new { type = "result", is_error = false, result = "before " + key + " after" });
        return true;
    }
    public static void ValidateFollowup(string text, string[] args)
    {
        if (text == "Уточнение ✓") return;
        if (!text.StartsWith("Уточнение ✓", StringComparison.Ordinal)) throw new Exception("Follow-up lost its text");
        ValidateDocx(text, args);
    }
    public static void ValidateDocx(string text, string[] args)
    {
        using var manifest = JsonDocument.Parse(text[(text.LastIndexOf('\n') + 1)..]);
        var document = manifest.RootElement.EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "document");
        if (document.GetProperty("content").GetString()?.Contains("Документ ✓", StringComparison.Ordinal) != true)
            throw new Exception("DOCX text was not captured before the provider boundary");
        if (args.Any(argument => argument.Contains("Read(/", StringComparison.Ordinal) && argument.Contains(".docx", StringComparison.OrdinalIgnoreCase)))
            throw new Exception("Captured DOCX unnecessarily received a filesystem read permission");
    }
}
