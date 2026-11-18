using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CadMcp.Core;

namespace CadMcp.Providers;

public sealed record SubscriptionModel(string Id, string DisplayName, bool IsDefault);
internal enum SubscriptionCli { Qwen, Kimi }

/// <summary>
/// ACP client for the official subscription CLIs. Each turn resumes the CLI's own session. There is
/// no API-key fallback, shell auto-approval, or retry of a prompt after uncertain delivery.
/// </summary>
public abstract class SubscriptionAcpProvider : IChatProvider
{
    private readonly ProviderOptions options;
    private readonly SubscriptionCli cli;
    private readonly string? kimiHome;
    private int sending;
    private string Name => cli == SubscriptionCli.Qwen ? "Qwen Code" : "Kimi Code";
    public string? SessionId { get; set; }
    internal TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(90);
    internal TimeSpan EventSilenceTimeout { get; set; } = TimeSpan.FromMinutes(5);

    private protected SubscriptionAcpProvider(ProviderOptions options, SubscriptionCli cli, string? kimiHome = null)
    { this.options = options; this.cli = cli; this.kimiHome = kimiHome; }

    public string[] Arguments() => cli == SubscriptionCli.Qwen
        ? ["--acp", "--bare", "--auth-type", "qwen-subscription", "--approval-mode", "default", "--allowed-mcp-server-names", "cad",
            "--exclude-tools", "read_file", "edit", "notebook_edit", "run_shell_command"]
        : ["acp"];

    public Task<InputReceipt> SteerAsync(ChatInput input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new InputReceipt("rejected", Name + UiText.T(": дополнение во время текущего хода не поддерживается. Отправьте его после завершения ответа.")));
    }

    internal ProcessStartInfo StartInfo()
    {
        var launch = options;
        if (cli == SubscriptionCli.Kimi)
            launch = options with { WorkingDirectory = KimiConfiguration.Prepare(kimiHome!, options.WorkingDirectory) };
        var info = SubscriptionProcess.Create(launch, Arguments(),
            cli == SubscriptionCli.Qwen ? "@qwen-code/qwen-code" : "@moonshot-ai/kimi-code",
            cli == SubscriptionCli.Qwen ? "qwen" : "kimi");
        // Only Codex supplies the matching _meta.threadId. Keeping its inherited thread binding
        // would make the correctly pinned ACP MCP host reject every mutation as a foreign writer.
        info.Environment.Remove("CAD_MCP_PRIMARY_THREAD_FILE");
        if (cli == SubscriptionCli.Kimi) KimiConfiguration.ApplyEnvironment(info, kimiHome!);
        return info;
    }

    public async IAsyncEnumerable<ChatEvent> SendAsync(string prompt, [EnumeratorCancellation] CancellationToken ct,
        IReadOnlyList<ChatAttachment>? attachments = null)
    {
        if (Interlocked.Exchange(ref sending, 1) != 0) throw new InvalidOperationException(UiText.T("Ход этого провайдера уже выполняется."));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var events = Channel.CreateBounded<ChatEvent>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = false });
        async Task Produce()
        {
            try
            {
                await RunAsync(prompt, attachments ?? [], item => events.Writer.WriteAsync(item, lifetime.Token).AsTask(), lifetime.Token);
                events.Writer.TryComplete();
            }
            catch (Exception error) { events.Writer.TryComplete(error); }
        }
        Task producer = Produce();
        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(ct)) yield return item;
        }
        finally
        {
            lifetime.Cancel();
            await producer;
            Volatile.Write(ref sending, 0);
        }
    }

    private async Task RunAsync(string prompt, IReadOnlyList<ChatAttachment> attachments, Func<ChatEvent, Task> emit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var blocks = PromptBlocks(prompt, ChatAttachments.Capture(attachments));
        await emit(new("status", UiText.T("Подключаю ") + Name + UiText.T(" через подписку…")));
        var start = StartInfo();
        await using var connection = new SubscriptionAcpConnection(start, Name, cli, emit, StartupTimeout, EventSilenceTimeout);
        var initialization = await InitializeAsync(connection, ct);
        if (attachments.Any(a => a.Kind == AttachmentKind.Image) &&
            ProviderJson.Child(ProviderJson.Child(ProviderJson.Child(initialization, "agentCapabilities"), "promptCapabilities"), "image").ValueKind != JsonValueKind.True)
            throw new IOException(Name + UiText.T(" не объявил поддержку изображений через ACP; вложения не отправлены."));
        string? saved = SessionId;
        if (saved is not null && ProviderJson.Child(ProviderJson.Child(initialization, "agentCapabilities"), "loadSession").ValueKind != JsonValueKind.True)
            throw new IOException(Name + UiText.T(" не поддерживает восстановление диалога через ACP. Обновите официальный CLI."));
        var parameters = SessionParameters(start.WorkingDirectory, includeCad: true, saved);
        // History notifications emitted by session/load are deliberately ignored: the UI already has
        // that history, and none of its old tool results can become a new approval or activity event.
        var session = await connection.RequestAsync(saved is null ? "session/new" : "session/load", parameters, ct);
        string id = saved ?? ProviderJson.Text(session, "sessionId") ?? throw new IOException(Name + UiText.T(" не вернул идентификатор диалога."));
        SessionId = id;
        connection.SessionId = id;
        await emit(new("session", id));
        await SetManualModeAsync(connection, session, ct);
        session = await SelectModelAsync(connection, session, ct);
        string? model = SubscriptionAcpProtocol.CurrentModel(session);
        if (model is not null) await emit(new("model", model));
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
            await SelectEffortAsync(connection, session, options.ReasoningEffort, ct);
        connection.DeliverUpdates = true;
        connection.PromptActive = true;
        var response = await connection.RequestAsync("session/prompt", new { sessionId = id, prompt = blocks }, ct, prompt: true);
        connection.PromptActive = false;
        string? stop = ProviderJson.Text(response, "stopReason");
        if (stop != "end_turn")
        {
            ct.ThrowIfCancellationRequested();
            throw new IOException(Name + UiText.T(" завершил ход без полного ответа: ") + (stop ?? "missing stopReason") +
                UiText.T(". Проверьте квитанции CAD; автоматического повтора нет."));
        }
        ct.ThrowIfCancellationRequested();
        await emit(new("completed", "completed"));
    }

    private async Task<JsonElement> InitializeAsync(SubscriptionAcpConnection connection, CancellationToken ct)
    {
        var result = await connection.RequestAsync("initialize", new
        {
            protocolVersion = 1,
            clientInfo = new { name = "CAD MCP", version = Wire.Version },
            clientCapabilities = new { fs = new { readTextFile = false, writeTextFile = false }, terminal = false }
        }, ct);
        if (ProviderJson.Child(result, "protocolVersion") is not { ValueKind: JsonValueKind.Number } version || !version.TryGetInt32(out int n) || n != 1)
            throw new IOException(Name + UiText.T(" использует неподдерживаемую версию ACP. Обновите официальный CLI."));
        if (cli == SubscriptionCli.Kimi && ProviderJson.Text(ProviderJson.Child(result, "agentInfo"), "name") != "Kimi Code CLI")
            throw new IOException(UiText.T("Нужен современный Kimi Code CLI: официальный standalone kimi.exe или npm-пакет @moonshot-ai/kimi-code. Старый Python kimi-cli не поддерживается."));
        return result;
    }

    private object SessionParameters(string cwd, bool includeCad, string? saved = null)
    {
        object[] servers = includeCad ? [new
        {
            name = "cad", command = options.McpExecutable, args = options.McpArguments ?? Array.Empty<string>(),
            env = options.CadEnvironment.Select(pair => new { name = pair.Key, value = pair.Value }).ToArray()
        }] : [];
        if (saved is null) return new { cwd, mcpServers = servers };
        return new { sessionId = saved, cwd, mcpServers = servers };
    }

    private async Task SetManualModeAsync(SubscriptionAcpConnection connection, JsonElement session, CancellationToken ct)
    {
        var modes = ProviderJson.Child(ProviderJson.Child(session, "modes"), "availableModes");
        if (modes.ValueKind != JsonValueKind.Array || !modes.EnumerateArray().Any(mode => ProviderJson.Text(mode, "id") == "default"))
            throw new IOException(Name + UiText.T(" не объявил ручной режим разрешений ACP. Запуск остановлен."));
        await connection.RequestAsync("session/set_mode", new { sessionId = connection.SessionId, modeId = "default" }, ct);
    }

    private async Task<JsonElement> SelectModelAsync(SubscriptionAcpConnection connection, JsonElement session, CancellationToken ct)
    {
        var models = SubscriptionAcpProtocol.Models(session, cli);
        string? selected = options.Model;
        if (string.IsNullOrWhiteSpace(selected))
        {
            string? current = SubscriptionAcpProtocol.CurrentModel(session);
            if (current is null || !models.Any(m => m.Id == current))
                throw new IOException(Name + UiText.T(" не подтвердил, что текущая модель использует подписку. Выберите модель подписки; запрос к модели не отправлен."));
            return session;
        }
        if (!models.Any(model => model.Id == selected))
            throw new IOException(Name + UiText.T(": выбранная модель недоступна для текущего входа: ") + selected);
        if (SubscriptionAcpProtocol.FindOption(session, "model") is { } option)
        {
            var changed = await connection.RequestAsync("session/set_config_option", new { sessionId = connection.SessionId,
                configId = ProviderJson.Text(option, "id"), value = selected }, ct);
            if (SubscriptionAcpProtocol.CurrentModel(changed) != selected)
                throw new IOException(Name + UiText.T(" не подтвердил выбранную модель."));
            return changed;
        }
        await connection.RequestAsync("session/set_model", new { sessionId = connection.SessionId, modelId = selected }, ct);
        // The legacy extension's successful response has no body. The explicitly advertised id is
        // now selected; retain available models for callers and report that acknowledged selection.
        return JsonSerializer.SerializeToElement(new { models = new { currentModelId = selected,
            availableModels = models.Select(m => new { modelId = m.Id, name = m.DisplayName }) } });
    }

    private async Task SelectEffortAsync(SubscriptionAcpConnection connection, JsonElement session, string effort, CancellationToken ct)
    {
        var option = SubscriptionAcpProtocol.FindOption(session, "thought_level");
        if (option is null || !SubscriptionAcpProtocol.OptionValues(option.Value).Any(v => v.Id == effort))
            throw new IOException(Name + UiText.T(" не объявил выбранный уровень рассуждения: ") + effort);
        await connection.RequestAsync("session/set_config_option", new { sessionId = connection.SessionId,
            configId = ProviderJson.Text(option.Value, "id"), value = effort }, ct);
    }

    internal async Task<IReadOnlyList<SubscriptionModel>> ReadModelsCoreAsync(CancellationToken ct)
    {
        var start = StartInfo();
        await using var connection = new SubscriptionAcpConnection(start, Name, cli, _ => Task.CompletedTask, StartupTimeout, EventSilenceTimeout);
        await InitializeAsync(connection, ct);
        // session/new is an authenticated metadata operation. No prompt, model inference, or CAD tool
        // is requested by the connection check/catalog reader.
        var session = await connection.RequestAsync("session/new", SessionParameters(start.WorkingDirectory, includeCad: false), ct);
        connection.SessionId = ProviderJson.Text(session, "sessionId");
        var models = SubscriptionAcpProtocol.Models(session, cli);
        if (models.Count == 0) throw new IOException(Name + UiText.T(" не вернул доступные модели подписки. Выполните вход в официальный CLI."));
        return models;
    }

    internal async Task<string> CheckAsync(CancellationToken ct)
    {
        var models = await ReadModelsCoreAsync(ct);
        var current = models.FirstOrDefault(m => m.IsDefault);
        if (current is null)
            throw new IOException(Name + UiText.T(" не подтвердил, что текущая модель использует подписку. Выберите модель подписки; запрос к модели не отправлен."));
        return Name + UiText.T(": вход подтверждён; моделей доступно: ") + models.Count +
            UiText.T(". Текущая модель: ") + current.DisplayName + UiText.T(". Запрос к модели не выполнялся.");
    }

    internal static IReadOnlyList<object> PromptBlocks(string prompt, IReadOnlyList<ChatAttachment> attachments)
    {
        var result = new List<object> { new { type = "text", text = "CAD MCP integration instructions:\n" + CadAgent.Instructions +
            "\n\nOnly the supplied CAD MCP tools are available. Do not invoke local shell, filesystem writers, external agents or alternate model providers.\n\nUser request:\n" +
            ChatAttachments.AddToPrompt(prompt, attachments) } };
        foreach (var file in attachments.Where(file => file.Kind == AttachmentKind.Image))
            result.Add(new { type = "image", mimeType = ChatAttachments.ImageMediaType(file.Path), data = Convert.ToBase64String(ChatAttachments.ImageBytes(file)) });
        return result;
    }
}

internal static class SubscriptionAcpProtocol
{
    private const string CadPrefix = "mcp__cad__";
    private static readonly HashSet<string> CadTools = CadOperations.ReadOnly.Concat(CadOperations.WorkerOperations).Concat(CadOperations.HostTools).ToHashSet(StringComparer.Ordinal);

    internal static string? CurrentModel(JsonElement session)
    {
        var option = FindOption(session, "model");
        return option is null ? ProviderJson.Text(ProviderJson.Child(session, "models"), "currentModelId") : ProviderJson.Text(option.Value, "currentValue");
    }

    internal static JsonElement? FindOption(JsonElement session, string category)
    {
        var options = ProviderJson.Child(session, "configOptions");
        if (options.ValueKind != JsonValueKind.Array) return null;
        foreach (var option in options.EnumerateArray())
            if (ProviderJson.Text(option, "type") == "select" && (ProviderJson.Text(option, "category") == category || ProviderJson.Text(option, "id") == category)) return option;
        return null;
    }

    internal static IReadOnlyList<SubscriptionModel> Models(JsonElement session, SubscriptionCli cli)
    {
        string? current = CurrentModel(session);
        var option = FindOption(session, "model");
        var available = ProviderJson.Child(ProviderJson.Child(session, "models"), "availableModels");
        // Qwen advertises ALL configured providers even when started with --auth-type
        // qwen-subscription; choosing an API-key route switches authentication. Its model picker
        // options carry opaque IDs, while the accompanying models table supplies trusted authType.
        // Join by that exact ID, failing closed when the route has no subscription metadata.
        var qwenSubscriptions = new HashSet<string>(StringComparer.Ordinal);
        if (cli == SubscriptionCli.Qwen && available.ValueKind == JsonValueKind.Array)
            foreach (var group in available.EnumerateArray().Where(m => ProviderJson.Text(m, "modelId") is { Length: > 0 })
                .GroupBy(m => ProviderJson.Text(m, "modelId")!, StringComparer.Ordinal))
                if (group.All(m => ProviderJson.Text(ProviderJson.Child(m, "_meta"), "authType") == "qwen-subscription"))
                    qwenSubscriptions.Add(group.Key);
        IEnumerable<SubscriptionModel> models;
        if (option is not null) models = OptionValues(option.Value);
        else
        {
            models = available.ValueKind == JsonValueKind.Array ? available.EnumerateArray()
                .Where(m => ProviderJson.Text(m, "modelId") is { Length: > 0 })
                .Select(m => new SubscriptionModel(ProviderJson.Text(m, "modelId")!, ProviderJson.Text(m, "name") ?? ProviderJson.Text(m, "modelId")!, false)) : [];
        }
        return models.Where(m => cli == SubscriptionCli.Qwen ? qwenSubscriptions.Contains(m.Id) : m.Id.StartsWith("kimi-code/", StringComparison.Ordinal))
            .DistinctBy(m => m.Id).Select(m => m with { IsDefault = m.Id == current }).ToArray();
    }

    internal static IEnumerable<SubscriptionModel> OptionValues(JsonElement option)
    {
        var values = ProviderJson.Child(option, "options");
        if (values.ValueKind != JsonValueKind.Array) yield break;
        foreach (var value in values.EnumerateArray())
        {
            if (ProviderJson.Text(value, "value") is { Length: > 0 } id)
                yield return new(id, ProviderJson.Text(value, "name") ?? id, false);
            else foreach (var nested in OptionValues(value)) yield return nested;
        }
    }

    internal static string? PermissionTool(JsonElement toolCall, SubscriptionCli cli)
    {
        var meta = ProviderJson.Child(toolCall, "_meta");
        string? name = ProviderJson.Text(meta, "toolName");
        // Qwen supplies canonical identity in _meta.toolName; its title is model-facing prose.
        // Kimi's current official ACP approval.ts sets title directly to req.toolName and supplies
        // no identity metadata. This narrowly scoped fallback is only for that checked CLI dialect.
        if (cli == SubscriptionCli.Kimi)
        {
            if (ProviderJson.Text(toolCall, "kind") is { } kind && kind != "other") return null;
            string? title = ProviderJson.Text(toolCall, "title");
            if (name is not null && name != title) return null;
            name = title;
        }
        if (name is null || !name.StartsWith(CadPrefix, StringComparison.Ordinal)) return null;
        string tool = name[CadPrefix.Length..];
        if (!CadTools.Contains(tool)) return null;
        if (ProviderJson.Text(meta, "serverId") is { } server && server != "cad") return null;
        if (ProviderJson.Text(meta, "provenance") is { } provenance && provenance != "mcp") return null;
        return tool;
    }

    internal static object PermissionResponse(JsonElement parameters, SubscriptionCli cli, string? activeSession, bool prompting)
    {
        bool allow = prompting && activeSession is not null && ProviderJson.Text(parameters, "sessionId") == activeSession &&
            PermissionTool(ProviderJson.Child(parameters, "toolCall"), cli) is not null;
        var options = ProviderJson.Child(parameters, "options");
        if (options.ValueKind == JsonValueKind.Array)
        {
            string kind = allow ? "allow_once" : "reject_once";
            foreach (var option in options.EnumerateArray())
                if (ProviderJson.Text(option, "kind") == kind && ProviderJson.Text(option, "optionId") is { Length: > 0 } id)
                    return new { outcome = new { outcome = "selected", optionId = id } };
        }
        return new { outcome = new { outcome = "cancelled" } };
    }
}

/// <summary>One JSONL ACP connection. Reverse RPC is restricted to per-call CAD approvals.</summary>
internal sealed class SubscriptionAcpConnection : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> stderr;
    private readonly ProviderLines lines;
    private readonly string name;
    private readonly SubscriptionCli cli;
    private readonly Func<ChatEvent, Task> emit;
    private readonly TimeSpan startupTimeout, silenceTimeout;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly Dictionary<string, string> toolNames = new(StringComparer.Ordinal);
    private readonly Task reading;
    private Exception? readFailure;
    private int next;
    internal string? SessionId { get; set; }
    internal bool DeliverUpdates { get; set; }
    internal bool PromptActive { get; set; }

    internal SubscriptionAcpConnection(ProcessStartInfo start, string name, SubscriptionCli cli, Func<ChatEvent, Task> emit,
        TimeSpan startupTimeout, TimeSpan silenceTimeout)
    {
        this.name = name; this.cli = cli; this.emit = emit; this.startupTimeout = startupTimeout; this.silenceTimeout = silenceTimeout;
        process = Process.Start(start) ?? throw new IOException(UiText.T("Не удалось запустить ") + name);
        stderr = ProviderProcess.DrainErrors(process);
        lines = new ProviderLines(process.StandardOutput.BaseStream);
        reading = ReadLoopAsync();
    }

    internal async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct, bool prompt = false)
    {
        int id = Interlocked.Increment(ref next);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            if (reading.IsCompleted) throw readFailure ?? new IOException(name + UiText.T(" закрыл соединение ACP."));
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, ct);
            async Task<JsonElement> Reply()
            {
                await Task.WhenAny(completion.Task, reading);
                if (completion.Task.IsCompleted) return await completion.Task;
                throw readFailure ?? new IOException(name + UiText.T(" закрыл соединение ACP."));
            }
            return prompt ? await Reply().WaitAsync(ct) : await Reply().WaitAsync(startupTimeout, ct);
        }
        catch (TimeoutException error) { throw new IOException(name + UiText.T(" не ответил на запрос ACP вовремя. Автоматического повтора нет."), error); }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task WriteAsync(object value, CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), ct); await process.StandardInput.FlushAsync(ct); }
        finally { writes.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                // Handshake requests have their own startup deadline in RequestAsync. A read may
                // begin between those replies and the prompt, so never capture the shorter startup
                // budget here: the first model token can legitimately take longer than CLI startup.
                deadline.CancelAfter(silenceTimeout);
                JsonDocument? json;
                try { json = await lines.ReadAsync(deadline.Token); }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                { throw new IOException(name + UiText.T(" слишком долго не передавал события. Проверьте квитанции CAD; автоматического повтора нет.")); }
                if (json is null) { await ThrowClosedAsync(); break; }
                using (json)
                {
                    var root = json.RootElement;
                    string? method = ProviderJson.Text(root, "method");
                    var parameters = ProviderJson.Child(root, "params");
                    if (method is not null)
                    {
                        if (ProviderJson.Child(root, "id") is { ValueKind: JsonValueKind.Number or JsonValueKind.String } requestId)
                            await HandleRequestAsync(requestId, method, parameters);
                        else if (method == "session/update" && DeliverUpdates && ProviderJson.Text(parameters, "sessionId") == SessionId)
                            await UpdateAsync(ProviderJson.Child(parameters, "update"));
                        continue;
                    }
                    if (ProviderJson.Id(root) is { } id && pending.TryRemove(id, out var completion))
                    {
                        if (ProviderJson.Child(root, "error") is { ValueKind: JsonValueKind.Object } error)
                        {
                            string text = ProviderJson.Text(error, "message") ?? "ACP error";
                            if (ProviderJson.Child(error, "code").TryGetInt32Safe() is -32000 or 401)
                                text += cli == SubscriptionCli.Kimi ? UiText.T(". Выполните вход Kimi Code из CAD MCP; требуется подписка Kimi.") :
                                    UiText.T(". Выполните /auth в Qwen Code и выберите Qwen Account with Subscription.");
                            completion.TrySetException(new IOException(name + ": " + text));
                        }
                        else if (ProviderJson.Child(root, "result") is { ValueKind: not JsonValueKind.Undefined } result)
                            completion.TrySetResult(result.Clone());
                        else completion.TrySetException(new IOException(name + UiText.T(" вернул некорректный ответ ACP.")));
                    }
                }
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            failure ??= new IOException(name + UiText.T(" закрыл соединение ACP до ответа. Автоматического повтора нет."));
            readFailure = failure;
            foreach (var item in pending.ToArray()) if (pending.TryRemove(item.Key, out var completion)) completion.TrySetException(failure);
        }
    }

    private async Task HandleRequestAsync(JsonElement id, string method, JsonElement parameters)
    {
        if (method == "session/request_permission")
        {
            string? tool = SubscriptionAcpProtocol.PermissionTool(ProviderJson.Child(parameters, "toolCall"), cli);
            object response = SubscriptionAcpProtocol.PermissionResponse(parameters, cli, SessionId, PromptActive);
            await WriteAsync(new { jsonrpc = "2.0", id, result = response }, lifetime.Token);
            if (PromptActive && ProviderJson.Text(parameters, "sessionId") == SessionId && tool is not null &&
                ProviderJson.Text(ProviderJson.Child(parameters, "toolCall"), "toolCallId") is { } callId)
                toolNames[callId] = tool;
            if (PromptActive && ProviderJson.Text(parameters, "sessionId") == SessionId &&
                SubscriptionAcpProtocol.PermissionTool(ProviderJson.Child(parameters, "toolCall"), cli) is null)
                await emit(new("problem", name + UiText.T(": запрос встроенного или постороннего инструмента отклонён. В этом чате доступны только инструменты CAD MCP.")));
            return;
        }
        await WriteAsync(new { jsonrpc = "2.0", id, error = new { code = -32601,
            message = "CAD MCP does not provide filesystem, terminal, or other client execution methods." } }, lifetime.Token);
    }

    private async Task UpdateAsync(JsonElement update)
    {
        string? type = ProviderJson.Text(update, "sessionUpdate");
        if (type == "agent_message_chunk")
        {
            var content = ProviderJson.Child(update, "content");
            if (ProviderJson.Text(content, "type") == "text" && ProviderJson.Text(content, "text") is { } text) await emit(new("text", text));
        }
        else if (type == "agent_thought_chunk")
        {
            // Do not display or persist raw provider reasoning; a liveness signal is sufficient.
            await emit(new("heartbeat", ""));
        }
        else if (type is "tool_call" or "tool_call_update")
        {
            string? id = ProviderJson.Text(update, "toolCallId");
            string? tool = SubscriptionAcpProtocol.PermissionTool(update, cli);
            if (id is not null && tool is not null) toolNames[id] = tool;
            if (tool is null && id is not null) toolNames.TryGetValue(id, out tool);
            if (tool is null) { await emit(new("heartbeat", "")); return; }
            string? status = ProviderJson.Text(update, "status");
            string phase = status is "completed" or "failed" ? status : "running";
            await emit(new("step", CadToolActivity.Describe(tool, phase), Tool: tool, Phase: phase));
            if (status == "failed") await emit(new("problem", name + UiText.T(": ошибка инструмента ") + tool + ". " + FailureText(update)));
        }
        else if (type == "config_option_update" && SubscriptionAcpProtocol.CurrentModel(update) is { } model)
            await emit(new("model", model));
        else if (type == "usage_update")
        {
            // ACP usage_update reports occupied context, not billable input/output tokens.
            var used = ProviderJson.Child(update, "used");
            var size = ProviderJson.Child(update, "size");
            if (used.ValueKind == JsonValueKind.Number && size.ValueKind == JsonValueKind.Number)
                await emit(new("status", name + UiText.T(": контекст ") + used + " / " + size));
        }
        else await emit(new("heartbeat", ""));
    }

    private static string FailureText(JsonElement update)
    {
        var content = ProviderJson.Child(update, "content");
        if (content.ValueKind != JsonValueKind.Array) return "";
        string text = string.Join("\n", content.EnumerateArray().Select(item => ProviderJson.Text(ProviderJson.Child(item, "content"), "text")).OfType<string>());
        return text.Length <= 2000 ? text : text[..2000];
    }

    private async Task ThrowClosedAsync()
    {
        string errors = stderr.IsCompleted ? await stderr : "";
        throw new IOException(name + UiText.T(" закрыл поток ACP до завершения запроса. Автоматического повтора нет.") +
            (errors.Length == 0 ? "" : "\n" + errors) + (lines.Skipped.Length == 0 ? "" : "\n" + lines.Skipped));
    }

    public async ValueTask DisposeAsync()
    {
        if (PromptActive && SessionId is not null)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await WriteAsync(new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId = SessionId } }, cancel.Token); }
            catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException) { }
        }
        lifetime.Cancel();
        try { process.StandardInput.Close(); } catch (IOException) { }
        // EOF lets the official CLI flush its own session history before the process-tree fallback.
        using (var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            try { await process.WaitForExitAsync(grace.Token); }
            catch (OperationCanceledException) { }
        }
        await ProviderProcess.FinishAsync(process, stderr);
        await reading;
        process.Dispose();
        lifetime.Dispose();
        writes.Dispose();
    }
}

internal static class SubscriptionJsonExtensions
{
    internal static int? TryGetInt32Safe(this JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;
}
