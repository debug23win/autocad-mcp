using System.Diagnostics;

namespace CadMcp.Core;

public static class BrokerBootstrap
{
    private static readonly TimeSpan StartupLimit = TimeSpan.FromSeconds(40);
    public static async Task EnsureAsync(string executable, CancellationToken ct, string? pipeName = null)
    {
        pipeName ??= Wire.BrokerPipe;
        var deadline = DateTime.UtcNow + StartupLimit;
        bool started = false;
        while (true)
        {
            if (await PingAsync(pipeName, ct) is { } running)
            {
                if (!IsOlder(running, Wire.Version)) return;
                // A broker from an older release survived an upgrade: let it drain and exit, then start this release.
                await StopAsync(pipeName, ct);
            }
            // The broker holds a named mutex for its whole life, including while it drains requests after a stop.
            // Start one only when nobody holds the name; a second broker would exit at once.
            else if (!BrokerAlive(pipeName))
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))! };
                start.ArgumentList.Add("--broker");
                start.ArgumentList.Add("--broker-pipe"); start.ArgumentList.Add(pipeName);
                using var process = Process.Start(start) ?? throw new IOException("Cannot start CAD MCP broker");
                started = true;
            }
            if (DateTime.UtcNow > deadline)
                throw new IOException(started ? "CAD MCP broker did not become ready after startup" : "CAD MCP broker is unresponsive and holds its pipe name");
            await Task.Delay(150, ct);
        }
    }
    private static bool BrokerAlive(string pipeName)
    {
        try
        {
            if (!Mutex.TryOpenExisting(@"Local\" + pipeName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }
    /// <summary>Returns the running broker version, or null when no broker answers.</summary>
    private static async Task<string?> PingAsync(string pipeName, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(500);
        try
        {
            var response = await PipeClient.CallAsync(pipeName, new(Guid.NewGuid().ToString("N"), "broker_ping"), timeout.Token);
            return response.Status != "completed" ? null : Wire.Element(response.Data ?? new { }).Text("version") ?? "unknown";
        }
        catch (Exception e) when (Wire.IsTransportFailure(e))
        { ct.ThrowIfCancellationRequested(); return null; }
    }
    private static async Task StopAsync(string pipeName, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { await PipeClient.CallAsync(pipeName, new(Guid.NewGuid().ToString("N"), "broker_stop"), timeout.Token); }
        catch (Exception e) when (Wire.IsTransportFailure(e)) { ct.ThrowIfCancellationRequested(); }
    }
    /// <summary>
    /// Only a strictly older numeric release is replaced. Equal, newer or unreadable versions are kept,
    /// so two installed releases cannot keep stopping each other's broker.
    /// </summary>
    public static bool IsOlder(string running, string current) =>
        System.Version.TryParse(running.Split('-', '+')[0], out var old) &&
        System.Version.TryParse(current.Split('-', '+')[0], out var mine) && old < mine;
}
