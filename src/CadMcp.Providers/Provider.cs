using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Providers;

public sealed record ProviderOptions(string Executable, string McpExecutable, string WorkingDirectory,
    string? Model = null, string? ReasoningEffort = null, IReadOnlyList<string>? McpArguments = null,
    int MaxSubagents = 3, string? OwnerId = null, string? CadSessionId = null, string? CadDocumentId = null)
{
    public Dictionary<string,string> CadEnvironment => new() { ["CAD_MCP_OWNER_ID"] = OwnerId ?? "", ["CAD_MCP_SESSION_ID"] = CadSessionId ?? "", ["CAD_MCP_DOCUMENT_ID"] = CadDocumentId ?? "" };
}
public sealed record ChatEvent(string Kind, string Text);
public interface IChatProvider
{
    string? SessionId { get; set; }
    Task<InputReceipt> SteerAsync(ChatInput input, CancellationToken ct);
    IAsyncEnumerable<ChatEvent> SendAsync(string prompt, CancellationToken ct, IReadOnlyList<ChatAttachment>? attachments = null);
}

public static class ProviderProcess
{
    public static ProcessStartInfo StartInfo(ProviderOptions options, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(options.Executable)
        {
            WorkingDirectory = options.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        return info;
    }
    public static void Stop(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    public static async Task StopAndWaitAsync(Process p)
    {
        Stop(p);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException) { }
    }
    public static async Task FinishAsync(Process p, Task stderr)
    {
        await StopAndWaitAsync(p);
        try { await stderr.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { }
    }
    public static async Task<string> DrainErrors(Process p)
    {
        var text = new StringBuilder();
        while (await p.StandardError.ReadLineAsync() is { } line)
            if (text.Length < 8192) text.AppendLine(line[..Math.Min(line.Length, 1024)]);
        return text.ToString();
    }
    /// <summary>Observe a read that is abandoned when the process is stopped, so its fault is not reported as unobserved.</summary>
    internal static void Forget(Task? task) => task?.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
}

internal static class ProviderJson
{
    public static string? Text(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public static int? Id(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out int value) ? value : null;
    public static JsonElement Child(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) ? value : default;
}

public sealed class ClaudeProvider(ProviderOptions options) : IChatProvider
{
    public string? SessionId { get; set; }
    private readonly LiveInput live = new();
    /// <summary>Quiet period after a result that did not list its messages, before merged messages are assumed answered.</summary>
    internal TimeSpan QuietAfterResult { get; set; } = TimeSpan.FromSeconds(30);
    internal TimeSpan AcknowledgementTimeout { get => live.AcknowledgementTimeout; set => live.AcknowledgementTimeout = value; }
    public Task<InputReceipt> SteerAsync(ChatInput input, CancellationToken ct) => live.SendAsync(input, ct);
    public string[] Arguments()
    {
        if (options.MaxSubagents is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(options.MaxSubagents));
        var config = JsonSerializer.Serialize(new { mcpServers = new { cad = new { command = options.McpExecutable, args = options.McpArguments ?? Array.Empty<string>(), env = options.CadEnvironment } } });
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--mcp-config", config, "--strict-mcp-config", "--allowedTools", "mcp__cad__*" };
        // --allowedTools takes a list: the helper permission must directly follow it, before any other option.
        if (options.MaxSubagents > 0) args.Add("Agent(cad_researcher)");
        if (!string.IsNullOrWhiteSpace(options.Model)) args.AddRange(["--model", options.Model]);
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort)) args.AddRange(["--effort", options.ReasoningEffort]);
        if (options.MaxSubagents > 0)
        {
            var readOnlyEnvironment = new Dictionary<string, string>(options.CadEnvironment) { ["CAD_MCP_READ_ONLY"] = "1" };
            args.AddRange(["--agents", JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["cad_primary"] = new
                {
                    description="CAD coordinator and sole drawing writer",
                    prompt=CadAgent.Instructions,
                    tools=new[]{"mcp__cad__*","Agent(cad_researcher)","WebSearch"}
                },
                ["cad_researcher"] = new
                {
                    description = "Read-only CAD researcher for independent drawing inspection, calculations and quality checks. Use for parallel subtasks; report evidence to the primary assistant.",
                    prompt = "Analyze the assigned CAD question independently. Use CAD tools only to read the drawing. Do not edit, run AutoLISP, export or publish. Return concise findings, measurements, assumptions and uncertainty to the primary assistant.",
                    mcpServers=new object[]{new Dictionary<string,object>{["cad"] = new {command=options.McpExecutable,args=options.McpArguments??Array.Empty<string>(),env=readOnlyEnvironment}}},
                    tools = CadOperations.ReadOnly.Concat(CadOperations.HostTools).Order(StringComparer.Ordinal).Select(tool => "mcp__cad__" + tool).Append("WebSearch").ToArray()
                }
            }),"--agent","cad_primary"]);
        }
        // The selected agent's prompt replaces the default system prompt, so with an agent the instructions
        // are not appended a second time. That also keeps the command line far below the Windows limit.
        else args.AddRange(["--disallowedTools", "Agent", "--append-system-prompt", CadAgent.Instructions]);
        if (SessionId is not null) args.AddRange(["--resume", SessionId]);
        args.AddRange(["--input-format", "stream-json", "--replay-user-messages"]);
        return args.ToArray();
    }
    public async IAsyncEnumerable<ChatEvent> SendAsync(string prompt, [EnumeratorCancellation] CancellationToken ct, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        attachments ??= Array.Empty<ChatAttachment>();
        var start = ProviderProcess.StartInfo(options, Arguments());
        if (options.MaxSubagents > 0) start.Environment["CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS"] = options.MaxSubagents.ToString();
        using var p = Process.Start(start) ?? throw new IOException("Cannot start Claude Code");
        using var registration = ct.Register(() => ProviderProcess.Stop(p));
        var stderr = ProviderProcess.DrainErrors(p);
        var lines = new ProviderLines(p.StandardOutput);
        Task<JsonDocument?>? reading = null;
        bool completed = false, streamed = false;
        var pendingTools = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var turns = new ClaudeTurns(Guid.NewGuid().ToString());
            var acknowledgements = new ConcurrentDictionary<string,int>();
            using var writes = new SemaphoreSlim(1,1);
            async Task WriteInput(ChatInput input,string uuid,CancellationToken token)
            {
                var message = LiveInput.ClaudeMessage(input,uuid);
                await writes.WaitAsync(token);
                try { await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(),token); await p.StandardInput.FlushAsync(token); }
                finally { writes.Release(); }
            }
            await WriteInput(new(prompt,attachments),Guid.NewGuid().ToString(),ct);
            live.Open(async (id,input,token) =>
            {
                string uuid=Guid.NewGuid().ToString();acknowledgements[uuid]=id;
                try { await WriteInput(input,uuid,token); }
                catch { acknowledgements.TryRemove(uuid,out _);throw; }
            });
            yield return new("input_ready","Claude принимает дополнения");
            // After a result, Claude may still owe answers to acknowledged follow-ups, or follow-ups may still
            // await acknowledgement. Then the output is polled so the turn can finish without another line.
            bool turnActive = true;
            DateTimeOffset lastResult = DateTimeOffset.MinValue;
            while (true)
            {
                reading ??= lines.ReadAsync(ct);
                if (!turnActive && !reading.IsCompleted)
                {
                    await Task.WhenAny(reading, Task.Delay(250, ct));
                    if (!reading.IsCompleted)
                    {
                        if (turns.Settled(DateTimeOffset.UtcNow - lastResult >= QuietAfterResult) && live.TryClose())
                        { completed = true; yield return new("completed","completed"); yield break; }
                        continue;
                    }
                }
                var json = await reading; reading = null;
                if (json is null) break;
                using (json)
                {
                    var e = json.RootElement;
                    string? type = ProviderJson.Text(e, "type");
                    // session_id repeats on nearly every streamed line; report it only when it changes.
                    if (ProviderJson.Text(e, "session_id") is { } sid && sid != SessionId) { SessionId = sid; yield return new("session", sid); }
                    if (type is "stream_event" or "assistant" or "user") turnActive = true;
                    if (type == "user" && ProviderJson.Text(e, "uuid") is {} uuid && acknowledgements.TryRemove(uuid,out int receiptId))
                    {
                        turns.Acknowledge(uuid);
                        live.Resolve(receiptId,new("accepted","Claude принял дополнение; оно включено в очередь этой сессии"));
                    }
                    if (type == "stream_event" && ProviderJson.Text(ProviderJson.Child(ProviderJson.Child(e, "event"), "delta"), "text") is { } text)
                    { streamed = true; yield return new("text", text); }
                    if (type == "assistant" && ProviderJson.Child(ProviderJson.Child(e, "message"), "content") is { ValueKind: JsonValueKind.Array } blocks)
                        foreach (var block in blocks.EnumerateArray())
                            if (ProviderJson.Text(block, "type") == "tool_use" && ProviderJson.Text(block, "name") is { } toolName)
                            {
                                if (ProviderJson.Text(block, "id") is { } id) pendingTools[id] = toolName;
                                yield return toolName is "Agent" or "Task"
                                    ? new("status", "Помощники анализируют задачу") : new("step", "Выполняю " + toolName);
                            }
                    if (type == "user" && ProviderJson.Child(ProviderJson.Child(e, "message"), "content") is { ValueKind: JsonValueKind.Array } results)
                        foreach (var resultBlock in results.EnumerateArray())
                            if (ProviderJson.Text(resultBlock, "type") == "tool_result" &&
                                ProviderJson.Text(resultBlock, "tool_use_id") is { } id && pendingTools.Remove(id, out var toolName))
                            {
                                bool failed = ProviderJson.Child(resultBlock, "is_error").ValueKind == JsonValueKind.True;
                                yield return toolName is "Agent" or "Task"
                                    ? new("status", "Объединяю результаты помощников")
                                    : new("step", (failed ? "Ошибка: " : "Завершено: ") + toolName);
                            }
                    if (type == "result")
                    {
                        if (ProviderJson.Child(e, "is_error").ValueKind == JsonValueKind.True)
                            throw new IOException("Claude Code: " + (ProviderJson.Text(e, "result") ?? e.ToString()));
                        if (!streamed && ProviderJson.Text(e, "result") is { } result) yield return new("text", result);
                        var answered = ProviderJson.Child(e, "user_message_uuids") is { ValueKind: JsonValueKind.Array } listed
                            ? listed.EnumerateArray().Select(u => u.ValueKind == JsonValueKind.String ? u.GetString() : null).OfType<string>().ToArray() : null;
                        turns.Result(answered);
                        turnActive = false; lastResult = DateTimeOffset.UtcNow; streamed = false;
                        if (turns.Settled(false) && live.TryClose())
                        { completed=true;yield return new("completed","completed");yield break; }
                        yield return new("response_completed","Продолжаю с учётом дополнения");
                    }
                }
            }
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0 || !completed) throw new IOException("Claude Code ended without success. " + await stderr + lines.Skipped);
        }
        finally { live.Close(); await ProviderProcess.FinishAsync(p, stderr); ProviderProcess.Forget(reading); }
    }
}

public sealed class CodexProvider(ProviderOptions options) : IChatProvider
{
    public string? SessionId { get; set; }
    private readonly LiveInput live = new();
    public Task<InputReceipt> SteerAsync(ChatInput input, CancellationToken ct) => live.SendAsync(input, ct);
    public string[] Arguments() => Arguments(null);
    internal string[] Arguments(ICollection<string>? warnings)
    {
        if (options.MaxSubagents is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(options.MaxSubagents));
        return ["-c", "mcp_servers.cad.command=" + JsonSerializer.Serialize(options.McpExecutable),
        "-c", "mcp_servers.cad.args=" + JsonSerializer.Serialize(options.McpArguments ?? Array.Empty<string>()),
        "-c", "mcp_servers.cad.enabled=true",
        "-c", "mcp_servers.cad.env.CAD_MCP_PRIMARY_THREAD_FILE="+JsonSerializer.Serialize(CadSubagentPolicy.PrimaryThreadFile(options)),
        .. options.CadEnvironment.SelectMany(p => new[] {"-c", "mcp_servers.cad.env." + p.Key + "=" + JsonSerializer.Serialize(p.Value)}),
        .. (options.MaxSubagents>0?CadSubagentPolicy.Arguments(options, warnings):Array.Empty<string>()),
        "-c", "agents.enabled=" + (options.MaxSubagents > 0 ? "true" : "false"),
        "-c", "agents.max_concurrent_threads_per_session=" + Math.Max(1, options.MaxSubagents),
        // The user authorized direct CAD edits. `never` disables approval dialogs, but does
        // not approve side-effecting MCP tools. Grant the local CAD server explicitly;
        // keep the filesystem sandbox and other servers' policies unchanged.
        "-c", "mcp_servers.cad.default_tools_approval_mode=\"approve\"",
        "-c", "mcp_servers.cad.tools.cad_edit.approval_mode=\"approve\"",
        "-c", "mcp_servers.cad.tools.cad_export.approval_mode=\"approve\"",
        "-c", "mcp_servers.cad.tools.cad_publish.approval_mode=\"approve\"",
        "-c", "mcp_servers.cad.tools.cad_lisp.approval_mode=\"approve\"",
        "-c", "mcp_servers.cad.tools.cad_focus.approval_mode=\"approve\"", "app-server"];
    }
    public async IAsyncEnumerable<ChatEvent> SendAsync(string prompt, [EnumeratorCancellation] CancellationToken ct, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        attachments ??= Array.Empty<ChatAttachment>();
        var warnings = new List<string>();
        using var p = Process.Start(ProviderProcess.StartInfo(options, Arguments(warnings))) ?? throw new IOException("Cannot start Codex");
        using var registration = ct.Register(() => ProviderProcess.Stop(p));
        var stderr = ProviderProcess.DrainErrors(p);
        var lines = new ProviderLines(p.StandardOutput);
        foreach (var warning in warnings) yield return new("status", warning);
        var models = new List<JsonElement>();
        string? model = null, effort = null;
        int resumeRetries = 0;
        using var writes = new SemaphoreSlim(1,1);
        async Task Send(object data)
        {
            await writes.WaitAsync(ct);
            try { await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(data).AsMemory(),ct);await p.StandardInput.FlushAsync(ct); }
            finally { writes.Release(); }
        }
        void Activate(string turnId)
        {
            live.Open((id,input,token)=> Send(new { id,method="turn/steer",@params=new {threadId=SessionId,expectedTurnId=turnId,input=LiveInput.CodexContent(input)} }));
        }
        async Task Resume() => await Send(new { id = 2, method = "thread/resume", @params = new { model, threadId = SessionId, cwd = options.WorkingDirectory, developerInstructions = CadAgent.Instructions, approvalPolicy = "never", sandbox = "read-only" } });
        try
        {
            await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "cad_mcp", title = "CAD MCP", version = Wire.Version } } });
            while (await lines.ReadAsync(ct) is { } doc)
            {
                using (doc)
                {
                    var e = doc.RootElement;
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    bool isRequestOrEvent = e.TryGetProperty("method", out var method);
                    int? replyId = ProviderJson.Id(e);
                    if (replyId is >= 100 && !isRequestOrEvent)
                    {
                        if (e.TryGetProperty("error",out var inputError)) live.Resolve(replyId.Value,new("rejected",ErrorMessage(inputError)));
                        else live.Resolve(replyId.Value,new("accepted","Дополнение принято в текущую задачу"));
                        continue;
                    }
                    if (!isRequestOrEvent && e.TryGetProperty("error", out var error))
                    {
                        string message = ErrorMessage(error);
                        if (replyId == 2 && SessionId is not null && message.Contains("already has an active writer", StringComparison.OrdinalIgnoreCase) && resumeRetries < 3)
                        {
                            resumeRetries++;
                            await Task.Delay(TimeSpan.FromMilliseconds(300 * resumeRetries), ct);
                            await Resume();
                            continue;
                        }
                        throw new IOException("Codex: " + FriendlyError(message));
                    }
                    if (replyId is { } id && !isRequestOrEvent)
                    {
                        var result = ProviderJson.Child(e, "result");
                        if (id == 1)
                        {
                            await Send(new { method = "initialized", @params = new { } });
                            await Send(new { id = 4, method = "model/list", @params = new { limit = 100, includeHidden = false } });
                        }
                        else if (id == 4)
                        {
                            if (ProviderJson.Child(result, "data") is not { ValueKind: JsonValueKind.Array } data)
                                throw new IOException("Codex вернул каталог моделей в неизвестном формате");
                            models.AddRange(data.EnumerateArray().Select(m => m.Clone()));
                            if (ProviderJson.Text(result, "nextCursor") is { Length: > 0 } cursor)
                            {
                                await Send(new { id = 4, method = "model/list", @params = new { limit = 100, includeHidden = false, cursor } });
                                continue;
                            }
                            var selection = CodexCatalog.Select(CodexCatalog.Parse(models), options.Model, options.ReasoningEffort);
                            model = selection.Model.Id;
                            effort = selection.Effort;
                            yield return new("model", model!);
                            yield return new("effort", effort!);
                            if (SessionId is null)
                                await Send(new { id = 2, method = "thread/start", @params = new { model, cwd = options.WorkingDirectory, developerInstructions = CadAgent.Instructions, approvalPolicy = "never", sandbox = "read-only" } });
                            else await Resume();
                        }
                        else if (id == 2)
                        {
                            SessionId = ProviderJson.Text(ProviderJson.Child(result, "thread"), "id") ?? throw new IOException("Codex did not return a thread id");
                            CadSubagentPolicy.BindPrimary(options,SessionId);
                            yield return new("session", SessionId);
                            var input = new List<object> { new { type = "text", text = ChatAttachments.AddToPrompt(prompt, attachments) } };
                            foreach (var file in attachments.Where(f => f.Kind == AttachmentKind.Image)) input.Add(new { type = "localImage", path = file.Path });
                            await Send(new { id = 3, method = "turn/start", @params = new { threadId = SessionId, model, effort, input } });
                        }
                        else if (id == 3 && ProviderJson.Text(ProviderJson.Child(result, "turn"), "id") is { } startedTurn)
                        { Activate(startedTurn);yield return new("input_ready","Дополнения доступны"); }
                        continue;
                    }
                    if (!isRequestOrEvent) continue;
                    string? name = method.ValueKind == JsonValueKind.String ? method.GetString() : null;
                    var parameters = ProviderJson.Child(e, "params");
                    string? eventThread = ProviderJson.Text(parameters, "threadId");
                    bool rootEvent = eventThread is null || eventThread == SessionId;
                    if (e.TryGetProperty("id", out var serverRequest))
                    {
                        // Approvals are disabled and the panel has no UI for interactive requests. Decline the
                        // request and let Codex continue: the model sees the refusal instead of losing the turn.
                        await Send(new { id = serverRequest.Clone(), error = new { code = -32000, message = "CAD MCP declined " + name + ": interactive requests are disabled in this chat" } });
                        yield return new("status", "Codex запросил интерактивное действие (" + name + "); CAD MCP его отклонил");
                        continue;
                    }
                    if (name == "turn/started" && rootEvent && ProviderJson.Text(ProviderJson.Child(parameters, "turn"), "id") is { } activeTurn)
                    { Activate(activeTurn);yield return new("input_ready","Дополнения доступны"); }
                    if (name == "item/agentMessage/delta" && rootEvent) yield return new("text", ProviderJson.Text(parameters, "delta") ?? "");
                    // Codex exposes a summary of reasoning, never the private reasoning text.
                    if (name == "item/reasoning/summaryTextDelta" && rootEvent)
                        yield return new("reasoning_summary", ProviderJson.Text(parameters, "delta") ?? "");
                    var item = ProviderJson.Child(parameters, "item");
                    string? itemType = ProviderJson.Text(item, "type");
                    if (name == "item/started" && rootEvent)
                    {
                        if (itemType == "mcpToolCall") yield return new("step", "Выполняю " + (ProviderJson.Text(item, "tool") ?? "CAD tool"));
                        if (itemType == "collabAgentToolCall") yield return new("status", "Помощники анализируют задачу");
                    }
                    if (name == "item/completed" && rootEvent && itemType == "mcpToolCall")
                        yield return new("step", (ProviderJson.Text(item, "status") == "failed" ? "Ошибка: " : "Завершено: ") + (ProviderJson.Text(item, "tool") ?? "CAD tool"));
                    if (name == "item/completed" && rootEvent && itemType == "collabAgentToolCall")
                        yield return new("status", "Объединяю результаты помощников");
                    if (name == "error")
                    {
                        if (ProviderJson.Child(parameters, "willRetry").ValueKind == JsonValueKind.True) yield return new("status", "Codex восстанавливает соединение");
                        else throw new IOException("Codex: " + FriendlyError(ErrorMessage(parameters)));
                    }
                    if (name == "turn/completed" && rootEvent)
                    {
                        live.Close();
                        var turn = ProviderJson.Child(parameters, "turn");
                        if (ProviderJson.Text(turn, "status") != "completed") throw new IOException("Codex turn did not complete: " + turn);
                        yield return new("completed", "completed"); yield break;
                    }
                }
            }
            throw new IOException("Codex exited before turn completion. " + await stderr + lines.Skipped);
        }
        finally { live.Close(); await ProviderProcess.FinishAsync(p, stderr); }
    }
    public static string FriendlyError(string message)
    {
        if (message.Contains("Selected model is at capacity", StringComparison.OrdinalIgnoreCase))
            return "Выбранная модель сейчас перегружена. Выберите другую модель в панели или повторите позже.";
        if (message.Contains("already has an active writer", StringComparison.OrdinalIgnoreCase))
            return "Этот диалог Codex занят другим процессом. Закройте второе окно этого диалога или начните новый диалог.";
        return message;
    }
    public static string ErrorMessage(JsonElement value)
    {
        for (int i = 0; i < 5; i++)
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("error", out var error)) { value = error; continue; }
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("message", out var message)) { value = message; continue; }
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString() ?? "";
                try { using var parsed = JsonDocument.Parse(text); value = parsed.RootElement.Clone(); continue; }
                catch (JsonException) { return text; }
            }
            break;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "Codex error" : value.ToString();
    }
}
