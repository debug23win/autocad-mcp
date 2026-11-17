using System.Diagnostics;
using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Providers;

internal interface IPerplexityMcpSession : IAsyncDisposable
{
    IReadOnlyList<JsonElement> Tools { get; }
    string Instructions { get; }
    Task<JsonElement> CallAsync(string name, JsonElement arguments, CancellationToken ct);
}

/// <summary>One local MCP process per provider turn. CAD remains local and receives the panel's existing ownership/policy context.</summary>
internal sealed class PerplexityMcpSession : IPerplexityMcpSession
{
    private readonly Process process;
    private readonly ProviderLines lines;
    private readonly Task<string> errors;
    private int nextId;
    private bool disposed;
    public IReadOnlyList<JsonElement> Tools { get; private set; } = [];
    public string Instructions { get; private set; } = "";

    private PerplexityMcpSession(Process process)
    {
        this.process = process;
        lines = new(process.StandardOutput.BaseStream);
        errors = ProviderProcess.DrainErrors(process);
    }

    internal static ProcessStartInfo StartInfo(ProviderOptions options)
    {
        var start = ProviderProcess.StartInfo(options with { Executable = options.McpExecutable }, options.McpArguments ?? []);
        start.Environment.Remove("CAD_MCP_PRIMARY_THREAD_FILE");
        // A local CAD host never needs the provider key, including a key inherited from AutoCAD's environment.
        start.Environment.Remove("PERPLEXITY_API_KEY");
        foreach (var variable in options.CadEnvironment) start.Environment[variable.Key] = variable.Value;
        return start;
    }

    internal static async Task<IPerplexityMcpSession> StartAsync(ProviderOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var session = new PerplexityMcpSession(Process.Start(StartInfo(options)) ?? throw new IOException("Cannot start the CAD MCP host"));
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(TimeSpan.FromSeconds(60));
            var initialized = await session.RequestAsync("initialize", new
            {
                protocolVersion = "2025-11-25", capabilities = new { },
                clientInfo = new { name = "cad_mcp_perplexity", version = Wire.Version }
            }, startup.Token);
            session.Instructions = ProviderJson.Text(initialized, "instructions") ?? "";
            await session.WriteAsync(new { jsonrpc = "2.0", method = "notifications/initialized" }, startup.Token);
            var descriptors = new List<JsonElement>();
            string? cursor = null;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var page = await session.RequestAsync("tools/list", cursor is null ? new { } : (object)new { cursor }, startup.Token);
                var tools = ProviderJson.Child(page, "tools");
                if (tools.ValueKind != JsonValueKind.Array) throw new IOException("CAD MCP returned an invalid tools list");
                foreach (var tool in tools.EnumerateArray())
                {
                    string name = ProviderJson.Text(tool, "name") ?? "";
                    if (!name.StartsWith("cad_", StringComparison.Ordinal) || name.Length > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                        throw new IOException("CAD MCP advertised an invalid CAD tool name");
                    if (ProviderJson.Child(tool, "inputSchema").ValueKind != JsonValueKind.Object || descriptors.Any(t => ProviderJson.Text(t, "name") == name))
                        throw new IOException("CAD MCP advertised an invalid or duplicated CAD tool schema");
                    descriptors.Add(tool.Clone());
                }
                cursor = ProviderJson.Text(page, "nextCursor");
                if (descriptors.Count > 128 || cursor is not null && (!cursors.Add(cursor) || cursors.Count > 20))
                    throw new IOException("CAD MCP tools pagination exceeded its limit");
            } while (cursor is not null);
            if (descriptors.Count == 0) throw new IOException("CAD MCP advertised no tools");
            session.Tools = descriptors;
            return session;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await session.DisposeAsync();
            throw new IOException(UiText.T("Локальный CAD MCP не завершил подключение вовремя."));
        }
        catch { await session.DisposeAsync(); throw; }
    }

    public async Task<JsonElement> CallAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        if (!Tools.Any(tool => ProviderJson.Text(tool, "name") == name)) throw new InvalidOperationException("CAD tool was not advertised");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        try { return await RequestAsync("tools/call", new { name, arguments }, deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new IOException(UiText.T("CAD MCP не подтвердил результат вовремя. Исход операции неизвестен; автоматического повтора нет.")); }
    }

    private async Task WriteAsync(object value, CancellationToken ct)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
    {
        // Provider executes requests sequentially. A cancelled/broken request ends this process; it is never resent.
        int id = ++nextId;
        using var stop = ct.Register(() => ProviderProcess.Stop(process));
        await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, ct);
        while (await lines.ReadAsync(ct) is { } json)
        {
            using (json)
            {
                var root = json.RootElement;
                if (ProviderJson.Text(root, "method") is { } serverMethod && ProviderJson.Child(root, "id").ValueKind is JsonValueKind.Number or JsonValueKind.String)
                {
                    // No roots/sampling/elicitation capabilities were advertised. Do not hang on a server request.
                    object reply = serverMethod == "ping"
                        ? new { jsonrpc = "2.0", id = root.GetProperty("id"), result = new { } }
                        : new { jsonrpc = "2.0", id = root.GetProperty("id"), error = new { code = -32601, message = "Client capability not available" } };
                    await WriteAsync(reply, ct);
                    continue;
                }
                if (ProviderJson.Id(root) != id) continue;
                if (root.TryGetProperty("error", out var error))
                {
                    if (method == "tools/call") return JsonSerializer.SerializeToElement(new { isError = true, content = new[] { new { type = "text", text = error.GetRawText() } } });
                    throw new IOException("CAD MCP rejected " + method);
                }
                if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                    throw new IOException("CAD MCP returned an invalid response");
                return result.Clone();
            }
        }
        ct.ThrowIfCancellationRequested();
        throw new IOException(UiText.T("Соединение с CAD MCP завершилось до ответа. Исход операции неизвестен; автоматического повтора нет."));
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await ProviderProcess.FinishAsync(process, errors);
        process.Dispose();
    }
}
