using System.Diagnostics;
using System.Text.Json;
using CadMcp.Core;
using CadMcp.Providers;

// Tests start processes, pipes and brokers and measure time; running them one at a time keeps them deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CadMcp.Tests;

/// <summary>Paths of the programs the tests start, built in the same configuration as the tests.</summary>
internal static class TestEnvironment
{
    public static readonly string RepositoryRoot = FindRoot();
    // For example bin/Release/net8.0-windows: every project builds into the same relative folder.
    private static readonly string OutputFolder = Path.GetRelativePath(Path.Combine(RepositoryRoot, "tests", "CadMcp.Tests"), AppContext.BaseDirectory);
    private static string Executable(string project, string name) =>
        Path.Combine(RepositoryRoot, project, OutputFolder, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
    public static string HostExecutable => Executable(Path.Combine("src", "CadMcp.Host"), "CadMcp.Host");
    /// <summary>Simulates the Codex and Claude Code wire protocols; never starts a real CLI.</summary>
    public static string FakeCli => Executable(Path.Combine("tests", "CadMcp.TestCli"), "CadMcp.TestCli");
    public static ProviderOptions ProviderOptions => new(FakeCli, @"C:\CAD test\host.exe", Path.GetTempPath());
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CadMcp.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository root with CadMcp.sln not found above " + AppContext.BaseDirectory);
    }
    public const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6wAAAABJRU5ErkJggg==";
    public static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var result = new List<ChatEvent>();
        await foreach (var item in events) result.Add(item);
        return result;
    }
}

/// <summary>A temporary folder removed after the test.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cadmcp-test-" + Guid.NewGuid().ToString("N"));
    public TempFolder() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

/// <summary>A CadMcp.Host process speaking MCP over stdio.</summary>
internal sealed class McpHostProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> stderr;
    private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
    private int next = 100;
    private McpHostProcess(Process process) { this.process = process; stderr = ProviderProcess.DrainErrors(process); }
    public static async Task<McpHostProcess> StartAsync(string brokerPipe, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = ProviderProcess.StartInfo(new(TestEnvironment.HostExecutable, "unused", Path.GetTempPath()), ["--broker-pipe", brokerPipe]);
        foreach (var pair in environment ?? new Dictionary<string, string>()) info.Environment[pair.Key] = pair.Value;
        var host = new McpHostProcess(Process.Start(info)!);
        await host.RequestAsync("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
        await host.process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await host.process.StandardInput.FlushAsync();
        return host;
    }
    public async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        int id = Interlocked.Increment(ref next);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        await process.StandardInput.FlushAsync();
        while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            var reply = JsonDocument.Parse(line).RootElement.Clone();
            if (!reply.TryGetProperty("id", out var rid) || rid.ValueKind != JsonValueKind.Number || rid.GetInt32() != id) continue;
            Assert.False(reply.TryGetProperty("error", out var error), "MCP error: " + error);
            return reply.GetProperty("result").Clone();
        }
        throw new IOException("MCP host closed stdout: " + await stderr);
    }
    public Task<JsonElement> CallAsync(string tool, object arguments, object? meta = null) =>
        RequestAsync("tools/call", meta is null ? new { name = tool, arguments } : new { name = tool, arguments, _meta = meta });
    public async ValueTask DisposeAsync()
    {
        process.StandardInput.Close();
        await ProviderProcess.FinishAsync(process, stderr);
        process.Dispose();
        timeout.Dispose();
    }
}
