using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace CadMcp.Providers;

public sealed record ProviderOptions(string Executable, string McpExecutable, string WorkingDirectory,
    string? Model = null, string? ReasoningEffort = null, IReadOnlyList<string>? McpArguments = null);
public sealed record ChatEvent(string Kind, string Text);
public interface IChatProvider
{
    string? SessionId { get; set; }
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
    public string[] Arguments(bool imageInput = false)
    {
        var config = JsonSerializer.Serialize(new { mcpServers = new { cad = new { command = options.McpExecutable, args = Array.Empty<string>() } } });
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--mcp-config", config, "--strict-mcp-config", "--allowedTools", "mcp__cad__*", "--append-system-prompt", CadAgent.Instructions };
        if (SessionId is not null) args.AddRange(["--resume", SessionId]);
        if (imageInput) args.AddRange(["--input-format", "stream-json"]);
        return args.ToArray();
    }
    public async IAsyncEnumerable<ChatEvent> SendAsync(string prompt, [EnumeratorCancellation] CancellationToken ct, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        attachments ??= Array.Empty<ChatAttachment>();
        bool imageInput = attachments.Any(f => f.Kind == AttachmentKind.Image);
        using var p = Process.Start(ProviderProcess.StartInfo(options, Arguments(imageInput))) ?? throw new IOException("Cannot start Claude Code");
        using var registration = ct.Register(() => ProviderProcess.Stop(p));
        var stderr = ProviderProcess.DrainErrors(p);
        bool completed = false, streamed = false;
        try
        {
            string userPrompt = ChatAttachments.AddToPrompt(prompt, attachments);
            if (imageInput)
            {
                var content = new List<object> { new { type = "text", text = userPrompt } };
                foreach (var file in attachments.Where(f => f.Kind == AttachmentKind.Image))
                    content.Add(new { type = "image", source = new { type = "base64", media_type = ChatAttachments.ImageMediaType(file.Path), data = Convert.ToBase64String(ChatAttachments.ImageBytes(file)) } });
                await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content } }).AsMemory(), ct);
            }
            else await p.StandardInput.WriteAsync(userPrompt.AsMemory(), ct);
            p.StandardInput.Close();
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                using var json = JsonDocument.Parse(line); var e = json.RootElement;
                string? type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (e.TryGetProperty("session_id", out var sid)) { SessionId = sid.GetString(); yield return new("session", SessionId!); }
                if (type == "stream_event" && e.TryGetProperty("event", out var ev) && ev.TryGetProperty("delta", out var delta) && delta.TryGetProperty("text", out var text))
                { streamed = true; yield return new("text", text.GetString() ?? ""); }
                if (type == "assistant" && e.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var blocks))
                    foreach (var block in blocks.EnumerateArray())
                        if (block.TryGetProperty("type", out var bt) && bt.GetString() == "tool_use" && block.TryGetProperty("name", out var tool))
                            yield return new("step", "Выполняю " + (tool.GetString() ?? "CAD tool"));
                if (type == "user") yield return new("cad_result", e.GetRawText());
                if (type == "result")
                {
                    completed = true;
                    bool failed = e.TryGetProperty("is_error", out var err) && err.GetBoolean();
                    if (failed) throw new IOException("Claude Code failed: " + e.ToString());
                    if (!streamed && e.TryGetProperty("result", out var result)) yield return new("text", result.GetString() ?? "");
                    yield return new("completed", "completed");
                }
            }
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0 || !completed) throw new IOException("Claude Code ended without success. " + await stderr);
        }
        finally { await ProviderProcess.FinishAsync(p, stderr); }
    }
}

public sealed class CodexProvider(ProviderOptions options) : IChatProvider
{
    public string? SessionId { get; set; }
    public string[] Arguments() => ["-c", "mcp_servers.cad.command=" + JsonSerializer.Serialize(options.McpExecutable),
        "-c", "mcp_servers.cad.args=" + JsonSerializer.Serialize(options.McpArguments ?? Array.Empty<string>()),
        "-c", "mcp_servers.cad.enabled=true",
        // The user authorized direct CAD edits. `never` disables approval dialogs, but does
        // not approve side-effecting MCP tools. Grant the local CAD server explicitly;
        // keep the filesystem sandbox and other servers' policies unchanged.
        "-c", "mcp_servers.cad.default_tools_approval_mode=\"approve\"",
        "-c", "mcp_servers.cad.tools.cad_edit.approval_mode=\"approve\"",
        "-c", "mcp_servers.cad.tools.cad_export.approval_mode=\"approve\"",
        "-c", "mcp_servers.cad.tools.cad_publish.approval_mode=\"approve\"",
        "-c", "mcp_servers.cad.tools.cad_lisp.approval_mode=\"approve\"",
        "-c", "mcp_servers.cad.tools.cad_focus.approval_mode=\"approve\"", "app-server"];
    public async IAsyncEnumerable<ChatEvent> SendAsync(string prompt, [EnumeratorCancellation] CancellationToken ct, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        attachments ??= Array.Empty<ChatAttachment>();
        using var p = Process.Start(ProviderProcess.StartInfo(options, Arguments())) ?? throw new IOException("Cannot start Codex");
        using var registration = ct.Register(() => ProviderProcess.Stop(p));
        var stderr = ProviderProcess.DrainErrors(p);
        var models = new List<JsonElement>();
        string? model = null, effort = null;
        int resumeRetries = 0;
        async Task Send(object data) { await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(data).AsMemory(), ct); await p.StandardInput.FlushAsync(ct); }
        async Task Resume() => await Send(new { id = 2, method = "thread/resume", @params = new { model, threadId = SessionId, cwd = options.WorkingDirectory, developerInstructions = CadAgent.Instructions, approvalPolicy = "never", sandbox = "read-only" } });
        try
        {
            await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "cad_mcp", title = "CAD MCP", version = "0.4.0-preview" } } });
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                using var doc = JsonDocument.Parse(line); var e = doc.RootElement;
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
                        yield return new("session", SessionId!);
                        var input = new List<object> { new { type = "text", text = ChatAttachments.AddToPrompt(prompt, attachments) } };
                        foreach (var file in attachments.Where(f => f.Kind == AttachmentKind.Image)) input.Add(new { type = "localImage", path = file.Path });
                        await Send(new { id = 3, method = "turn/start", @params = new { threadId = SessionId, model, effort, input } });
                    }
                }
                if (e.TryGetProperty("method", out var method))
                {
                    string? name = method.GetString();
                    if (e.TryGetProperty("id", out var serverRequest))
                    {
                        // Interactive approvals/authentication require a richer UI in stage 3B.
                        await Send(new { id = serverRequest.Clone(), error = new { code = -32601, message = "Interactive request unsupported by prototype UI" } });
                        throw new IOException("Codex requires an interactive action: " + name);
                    }
                    if (name == "item/agentMessage/delta") yield return new("text", e.GetProperty("params").GetProperty("delta").GetString() ?? "");
                    // Codex exposes a summary of reasoning, never the private reasoning text.
                    if (name == "item/reasoning/summaryTextDelta")
                        yield return new("reasoning_summary", e.GetProperty("params").GetProperty("delta").GetString() ?? "");
                    if (name == "item/started" && e.GetProperty("params").TryGetProperty("item", out var item))
                    {
                        string? type = item.TryGetProperty("type", out var itemType) ? itemType.GetString() : null;
                        if (type == "mcpToolCall")
                            yield return new("step", "Выполняю " + (item.TryGetProperty("tool", out var tool) ? tool.GetString() ?? "CAD tool" : "CAD tool"));
                    }
                    if (name == "item/completed" && e.GetProperty("params").TryGetProperty("item", out var completedItem) &&
                        completedItem.TryGetProperty("type", out var completedType) && completedType.GetString() == "mcpToolCall")
                    {
                        var tool = completedItem.TryGetProperty("tool", out var toolName) ? toolName.GetString() ?? "CAD tool" : "CAD tool";
                        var result = completedItem.TryGetProperty("status", out var toolStatus) ? toolStatus.GetString() : null;
                        yield return new("step", (result == "failed" ? "Ошибка: " : "Завершено: ") + tool);
                        yield return new("cad_result", completedItem.GetRawText());
                    }
                    if (name == "error")
                    {
                        var parameters = e.GetProperty("params");
                        if (parameters.TryGetProperty("willRetry", out var retry) && retry.GetBoolean()) yield return new("status", "Codex восстанавливает соединение");
                        else throw new IOException("Codex: " + FriendlyError(ErrorMessage(parameters)));
                    }
                    if (name == "turn/completed")
                    {
                        var turn = e.GetProperty("params").GetProperty("turn");
                        if (turn.GetProperty("status").GetString() != "completed") throw new IOException("Codex turn did not complete: " + turn);
                        yield return new("completed", "completed"); yield break;
                    }
                }
            }
            throw new IOException("Codex exited before turn completion. " + await stderr);
        }
        finally { await ProviderProcess.FinishAsync(p, stderr); }
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
