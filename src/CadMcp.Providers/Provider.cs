using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace CadMcp.Providers;

public sealed record ProviderOptions(string Executable, string McpExecutable, string WorkingDirectory);
public sealed record ChatEvent(string Kind, string Text);
public interface IChatProvider
{
    string? SessionId { get; set; }
    IAsyncEnumerable<ChatEvent> SendAsync(string prompt, CancellationToken ct);
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
        catch (InvalidOperationException) { }
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
    public string[] Arguments()
    {
        var config = JsonSerializer.Serialize(new { mcpServers = new { cad = new { command = options.McpExecutable, args = Array.Empty<string>() } } });
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--mcp-config", config, "--strict-mcp-config", "--allowedTools", "mcp__cad__*" };
        if (SessionId is not null) args.AddRange(["--resume", SessionId]);
        return args.ToArray();
    }
    public async IAsyncEnumerable<ChatEvent> SendAsync(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        using var p = Process.Start(ProviderProcess.StartInfo(options, Arguments())) ?? throw new IOException("Cannot start Claude Code");
        using var registration = ct.Register(() => ProviderProcess.Stop(p));
        var stderr = ProviderProcess.DrainErrors(p);
        bool completed = false, streamed = false;
        try
        {
            await p.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            p.StandardInput.Close();
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                using var json = JsonDocument.Parse(line); var e = json.RootElement;
                string? type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (e.TryGetProperty("session_id", out var sid)) { SessionId = sid.GetString(); yield return new("session", SessionId!); }
                if (type == "stream_event" && e.TryGetProperty("event", out var ev) && ev.TryGetProperty("delta", out var delta) && delta.TryGetProperty("text", out var text))
                { streamed = true; yield return new("text", text.GetString() ?? ""); }
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
        finally { ProviderProcess.Stop(p); await stderr; }
    }
}

public sealed class CodexProvider(ProviderOptions options) : IChatProvider
{
    public string? SessionId { get; set; }
    public string[] Arguments() => ["-c", "mcp_servers.cad.command=" + JsonSerializer.Serialize(options.McpExecutable),
        "-c", "mcp_servers.cad.args=[]", "app-server"];
    public async IAsyncEnumerable<ChatEvent> SendAsync(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        using var p = Process.Start(ProviderProcess.StartInfo(options, Arguments())) ?? throw new IOException("Cannot start Codex");
        using var registration = ct.Register(() => ProviderProcess.Stop(p));
        var stderr = ProviderProcess.DrainErrors(p);
        async Task Send(object data) { await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(data).AsMemory(), ct); await p.StandardInput.FlushAsync(ct); }
        try
        {
            await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "cad_mcp", title = "CAD MCP", version = "0.1.0" } } });
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                using var doc = JsonDocument.Parse(line); var e = doc.RootElement;
                if (e.TryGetProperty("error", out var error)) throw new IOException("Codex: " + error);
                if (e.TryGetProperty("id", out var id) && !e.TryGetProperty("method", out _))
                {
                    if (id.GetInt32() == 1)
                    {
                        await Send(new { method = "initialized", @params = new { } });
                        if (SessionId is null)
                            await Send(new { id = 2, method = "thread/start", @params = new { cwd = options.WorkingDirectory, approvalPolicy = "never", sandbox = "read-only" } });
                        else
                            await Send(new { id = 2, method = "thread/resume", @params = new { threadId = SessionId, cwd = options.WorkingDirectory, approvalPolicy = "never", sandbox = "read-only" } });
                    }
                    else if (id.GetInt32() == 2)
                    {
                        SessionId = e.GetProperty("result").GetProperty("thread").GetProperty("id").GetString();
                        yield return new("session", SessionId!);
                        await Send(new { id = 3, method = "turn/start", @params = new { threadId = SessionId,
                            input = new[] { new { type = "text", text = prompt } } } });
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
                    if (name == "error") throw new IOException("Codex event: " + e.GetProperty("params"));
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
        finally { ProviderProcess.Stop(p); await stderr; }
    }
}
