using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

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
}

public sealed class ClaudeProvider(ProviderOptions options) : IChatProvider
{
    public string? SessionId { get; set; }
    private readonly LiveInput live = new();
    public Task<InputReceipt> SteerAsync(ChatInput input, CancellationToken ct) => live.SendAsync(input, ct);
    public string[] Arguments(bool imageInput = false)
    {
        if (options.MaxSubagents is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(options.MaxSubagents));
        var config = JsonSerializer.Serialize(new { mcpServers = new { cad = new { command = options.McpExecutable, args = options.McpArguments ?? Array.Empty<string>(), env = options.CadEnvironment } } });
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--mcp-config", config, "--strict-mcp-config", "--allowedTools", "mcp__cad__*" };
        if (!string.IsNullOrWhiteSpace(options.Model)) args.AddRange(["--model", options.Model]);
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort)) args.AddRange(["--effort", options.ReasoningEffort]);
        if (options.MaxSubagents > 0)
        {
            args.Add("Agent(cad_researcher)");
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
                    mcpServers=new object[]{new Dictionary<string,object>{["cad"] = new {command=options.McpExecutable,args=options.McpArguments??Array.Empty<string>(),env=options.CadEnvironment.Concat(new[]{new KeyValuePair<string,string>("CAD_MCP_READ_ONLY","1")}).ToDictionary(p=>p.Key,p=>p.Value)}}},
                    tools = new[] { "mcp__cad__cad_steel_catalog", "mcp__cad__cad_vertical_capabilities", "mcp__cad__cad_documents", "mcp__cad__cad_sessions", "mcp__cad__cad_review", "mcp__cad__cad_solid_get", "mcp__cad__cad_assembly_get", "mcp__cad__cad_table_dependencies", "mcp__cad__cad_release_check", "mcp__cad__cad_runtime_status", "mcp__cad__cad_context", "mcp__cad__cad_catalog", "mcp__cad__cad_search",
                        "mcp__cad__cad_table_get", "mcp__cad__cad_spds_help", "mcp__cad__cad_snapshot", "mcp__cad__cad_query", "mcp__cad__cad_result_get", "mcp__cad__cad_entity_get",
                        "mcp__cad__cad_vertical_catalog", "mcp__cad__cad_vertical_get", "mcp__cad__cad_render",
                        "mcp__cad__cad_image_register", "mcp__cad__cad_image_point", "mcp__cad__cad_edit_help", "mcp__cad__cad_verify",
                        "mcp__cad__cad_operation_status", "mcp__cad__cad_operation_list", "mcp__cad__cad_reference_calibrate",
                        "mcp__cad__cad_reference_point", "mcp__cad__cad_reference_compare", "WebSearch" }
                }
            }),"--agent","cad_primary"]);
        }
        else args.AddRange(["--disallowedTools", "Agent"]);
        args.AddRange(["--append-system-prompt", CadAgent.Instructions]);
        if (SessionId is not null) args.AddRange(["--resume", SessionId]);
        args.AddRange(["--input-format", "stream-json", "--replay-user-messages"]);
        return args.ToArray();
    }
    public async IAsyncEnumerable<ChatEvent> SendAsync(string prompt, [EnumeratorCancellation] CancellationToken ct, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        attachments ??= Array.Empty<ChatAttachment>();
        bool imageInput = true;
        var start = ProviderProcess.StartInfo(options, Arguments(imageInput));
        if (options.MaxSubagents > 0) start.Environment["CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS"] = options.MaxSubagents.ToString();
        using var p = Process.Start(start) ?? throw new IOException("Cannot start Claude Code");
        using var registration = ct.Register(() => ProviderProcess.Stop(p));
        var stderr = ProviderProcess.DrainErrors(p);
        bool completed = false, streamed = false;
        var pendingTools = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            int submitted = 1, resultsReceived = 0;
            var acknowledgements = new System.Collections.Concurrent.ConcurrentDictionary<string,int>();
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
                Interlocked.Increment(ref submitted);
                try { await WriteInput(input,uuid,token); }
                catch { Interlocked.Decrement(ref submitted);acknowledgements.TryRemove(uuid,out _);throw; }
            });
            yield return new("input_ready","Claude принимает дополнения");
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                using var json = JsonDocument.Parse(line); var e = json.RootElement;
                string? type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (e.TryGetProperty("session_id", out var sid)) { SessionId = sid.GetString(); yield return new("session", SessionId!); }
                if (type == "user" && e.TryGetProperty("uuid",out var replayId) && replayId.GetString() is {} uuid && acknowledgements.TryRemove(uuid,out int receiptId))
                    live.Resolve(receiptId,new("accepted","Claude принял дополнение; оно включено в очередь этой сессии"));
                if (type == "stream_event" && e.TryGetProperty("event", out var ev) && ev.TryGetProperty("delta", out var delta) && delta.TryGetProperty("text", out var text))
                { streamed = true; yield return new("text", text.GetString() ?? ""); }
                if (type == "assistant" && e.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var blocks))
                    foreach (var block in blocks.EnumerateArray())
                        if (block.TryGetProperty("type", out var bt) && bt.GetString() == "tool_use" && block.TryGetProperty("name", out var tool))
                        {
                            string toolName = tool.GetString() ?? "CAD tool";
                            if (block.TryGetProperty("id", out var toolId) && toolId.GetString() is { } id) pendingTools[id] = toolName;
                            yield return toolName is "Agent" or "Task"
                                ? new("status", "Помощники анализируют задачу") : new("step", "Выполняю " + toolName);
                        }
                if (type == "user" && e.TryGetProperty("message", out var toolMessage) &&
                    toolMessage.TryGetProperty("content", out var results) && results.ValueKind == JsonValueKind.Array)
                    foreach (var resultBlock in results.EnumerateArray())
                        if (resultBlock.TryGetProperty("type", out var resultType) && resultType.GetString() == "tool_result" &&
                            resultBlock.TryGetProperty("tool_use_id", out var toolId) &&
                            toolId.GetString() is { } id && pendingTools.Remove(id, out var toolName))
                        {
                            bool failed = resultBlock.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True;
                            yield return toolName is "Agent" or "Task"
                                ? new("status", "Объединяю результаты помощников")
                                : new("step", (failed ? "Ошибка: " : "Завершено: ") + toolName);
                        }
                if (type == "user") yield return new("cad_result", e.GetRawText());
                if (type == "result")
                {
                    resultsReceived++;
                    bool failed = e.TryGetProperty("is_error", out var err) && err.GetBoolean();
                    if (failed) throw new IOException("Claude Code: " + (e.TryGetProperty("result",out var failureMessage)?failureMessage.GetString():e.ToString()));
                    if (!streamed && e.TryGetProperty("result", out var result)) yield return new("text", result.GetString() ?? "");
                    if (resultsReceived >= Volatile.Read(ref submitted))
                    { completed=true;live.Close();yield return new("completed","completed");yield break; }
                    streamed=false;
                    yield return new("response_completed","Продолжаю с учётом дополнения");
                }
            }
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0 || !completed) throw new IOException("Claude Code ended without success. " + await stderr);
        }
        finally { live.Close(); await ProviderProcess.FinishAsync(p, stderr); }
    }
}

public sealed class CodexProvider(ProviderOptions options) : IChatProvider
{
    public string? SessionId { get; set; }
    private readonly LiveInput live = new();
    public Task<InputReceipt> SteerAsync(ChatInput input, CancellationToken ct) => live.SendAsync(input, ct);
    public string[] Arguments()
    {
        if (options.MaxSubagents is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(options.MaxSubagents));
        return ["-c", "mcp_servers.cad.command=" + JsonSerializer.Serialize(options.McpExecutable),
        "-c", "mcp_servers.cad.args=" + JsonSerializer.Serialize(options.McpArguments ?? Array.Empty<string>()),
        "-c", "mcp_servers.cad.enabled=true",
        "-c", "mcp_servers.cad.env.CAD_MCP_PRIMARY_THREAD_FILE="+JsonSerializer.Serialize(CadSubagentPolicy.PrimaryThreadFile(options)),
        .. options.CadEnvironment.SelectMany(p => new[] {"-c", "mcp_servers.cad.env." + p.Key + "=" + JsonSerializer.Serialize(p.Value)}),
        .. (options.MaxSubagents>0?CadSubagentPolicy.Arguments(options):Array.Empty<string>()),
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
        using var p = Process.Start(ProviderProcess.StartInfo(options, Arguments())) ?? throw new IOException("Cannot start Codex");
        using var registration = ct.Register(() => ProviderProcess.Stop(p));
        var stderr = ProviderProcess.DrainErrors(p);
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
        string? activeTurnId=null;
        void Activate(string turnId)
        {
            activeTurnId=turnId;
            live.Open((id,input,token)=> Send(new { id,method="turn/steer",@params=new {threadId=SessionId,expectedTurnId=turnId,input=LiveInput.CodexContent(input)} }));
        }
        async Task Resume() => await Send(new { id = 2, method = "thread/resume", @params = new { model, threadId = SessionId, cwd = options.WorkingDirectory, developerInstructions = CadAgent.Instructions, approvalPolicy = "never", sandbox = "read-only" } });
        try
        {
            await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "cad_mcp", title = "CAD MCP", version = "0.10.0-preview" } } });
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                using var doc = JsonDocument.Parse(line); var e = doc.RootElement;
                if (e.TryGetProperty("id",out var inputId) && inputId.ValueKind==JsonValueKind.Number && inputId.TryGetInt32(out int receiptId) && receiptId>=100 && !e.TryGetProperty("method",out _))
                {
                    if (e.TryGetProperty("error",out var inputError)) live.Resolve(receiptId,new("rejected",ErrorMessage(inputError)));
                    else live.Resolve(receiptId,new("accepted","Дополнение принято в текущую задачу"));
                    continue;
                }
                if (e.TryGetProperty("error", out var error))
                {
                    string message = ErrorMessage(error);
                    if (e.TryGetProperty("id", out var failedId) && failedId.ValueKind == JsonValueKind.Number && failedId.GetInt32() == 2 &&
                        SessionId is not null && message.Contains("already has an active writer", StringComparison.OrdinalIgnoreCase) && resumeRetries < 3)
                    {
                        resumeRetries++;
                        await Task.Delay(TimeSpan.FromMilliseconds(300 * resumeRetries), ct);
                        await Resume();
                        continue;
                    }
                    throw new IOException("Codex: " + FriendlyError(message));
                }
                if (e.TryGetProperty("id", out var id) && !e.TryGetProperty("method", out _))
                {
                    if (id.GetInt32() == 1)
                    {
                        await Send(new { method = "initialized", @params = new { } });
                        await Send(new { id = 4, method = "model/list", @params = new { limit = 100, includeHidden = false } });
                    }
                    else if (id.GetInt32() == 4)
                    {
                        var catalog = e.GetProperty("result");
                        models.AddRange(catalog.GetProperty("data").EnumerateArray().Where(m =>
                            !m.TryGetProperty("hidden", out var hidden) || !hidden.GetBoolean()).Select(m => m.Clone()));
                        if (catalog.TryGetProperty("nextCursor", out var cursor) && cursor.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(cursor.GetString()))
                        {
                            await Send(new { id = 4, method = "model/list", @params = new { limit = 100, includeHidden = false, cursor = cursor.GetString() } });
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
                    else if (id.GetInt32() == 2)
                    {
                        SessionId = e.GetProperty("result").GetProperty("thread").GetProperty("id").GetString();
                        CadSubagentPolicy.BindPrimary(options,SessionId!);
                        yield return new("session", SessionId!);
                        var input = new List<object> { new { type = "text", text = ChatAttachments.AddToPrompt(prompt, attachments) } };
                        foreach (var file in attachments.Where(f => f.Kind == AttachmentKind.Image)) input.Add(new { type = "localImage", path = file.Path });
                        await Send(new { id = 3, method = "turn/start", @params = new { threadId = SessionId, model, effort, input } });
                    }
                }
                if (e.TryGetProperty("id",out var turnStartId) && turnStartId.ValueKind==JsonValueKind.Number && turnStartId.GetInt32()==3 &&
                    e.TryGetProperty("result",out var turnStartResult) && turnStartResult.TryGetProperty("turn",out var startedTurn))
                { Activate(startedTurn.GetProperty("id").GetString()!);yield return new("input_ready","Дополнения доступны"); }
                if (e.TryGetProperty("method", out var method))
                {
                    string? name = method.GetString();
                    var parameters = e.TryGetProperty("params", out var eventParameters) ? eventParameters : default;
                    string? eventThread = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("threadId", out var thread)
                        ? thread.GetString() : null;
                    bool rootEvent = eventThread is null || eventThread == SessionId;
                    if (e.TryGetProperty("id", out var serverRequest))
                    {
                        // Interactive approvals/authentication require a richer UI in stage 3B.
                        await Send(new { id = serverRequest.Clone(), error = new { code = -32601, message = "Interactive request unsupported by prototype UI" } });
                        throw new IOException("Codex requires an interactive action: " + name);
                    }
                    if (name == "turn/started" && rootEvent && parameters.TryGetProperty("turn",out var activeTurn))
                    { Activate(activeTurn.GetProperty("id").GetString()!);yield return new("input_ready","Дополнения доступны"); }
                    if (name == "item/agentMessage/delta" && rootEvent) yield return new("text", parameters.GetProperty("delta").GetString() ?? "");
                    // Codex exposes a summary of reasoning, never the private reasoning text.
                    if (name == "item/reasoning/summaryTextDelta" && rootEvent)
                        yield return new("reasoning_summary", parameters.GetProperty("delta").GetString() ?? "");
                    if (name == "item/started" && rootEvent && parameters.TryGetProperty("item", out var item))
                    {
                        string? type = item.TryGetProperty("type", out var itemType) ? itemType.GetString() : null;
                        if (type == "mcpToolCall")
                            yield return new("step", "Выполняю " + (item.TryGetProperty("tool", out var tool) ? tool.GetString() ?? "CAD tool" : "CAD tool"));
                        if (type == "collabAgentToolCall") yield return new("status", "Помощники анализируют задачу");
                    }
                    if (name == "item/completed" && rootEvent && parameters.TryGetProperty("item", out var completedItem) &&
                        completedItem.TryGetProperty("type", out var completedType) && completedType.GetString() == "mcpToolCall")
                    {
                        var tool = completedItem.TryGetProperty("tool", out var toolName) ? toolName.GetString() ?? "CAD tool" : "CAD tool";
                        var result = completedItem.TryGetProperty("status", out var toolStatus) ? toolStatus.GetString() : null;
                        yield return new("step", (result == "failed" ? "Ошибка: " : "Завершено: ") + tool);
                        yield return new("cad_result", completedItem.GetRawText());
                    }
                    if (name == "item/completed" && rootEvent && parameters.TryGetProperty("item", out var collabItem) &&
                        collabItem.TryGetProperty("type", out var collabType) && collabType.GetString() == "collabAgentToolCall")
                        yield return new("status", "Объединяю результаты помощников");
                    if (name == "error")
                    {
                        if (parameters.TryGetProperty("willRetry", out var retry) && retry.GetBoolean()) yield return new("status", "Codex восстанавливает соединение");
                        else throw new IOException("Codex: " + FriendlyError(ErrorMessage(parameters)));
                    }
                    if (name == "turn/completed" && rootEvent)
                    {
                        live.Close();activeTurnId=null;
                        var turn = e.GetProperty("params").GetProperty("turn");
                        if (turn.GetProperty("status").GetString() != "completed") throw new IOException("Codex turn did not complete: " + turn);
                        yield return new("completed", "completed"); yield break;
                    }
                }
            }
            throw new IOException("Codex exited before turn completion. " + await stderr);
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
