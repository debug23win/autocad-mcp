using System.Diagnostics;
using System.Runtime.Versioning;

namespace CadMcp.Core;

[SupportedOSPlatform("windows")]
public static class BrokerBootstrap
{
    public static async Task EnsureAsync(string executable, CancellationToken ct, string? pipeName = null)
    {
        pipeName ??= Wire.BrokerPipe;
        async Task<bool> Probe()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(500);
            try
            {
                var response = await PipeClient.CallAsync(pipeName, new(Guid.NewGuid().ToString("N"), "broker_ping"), timeout.Token);
                return response.Status == "completed";
            }
            catch (Exception e) when (e is IOException or OperationCanceledException)
            { ct.ThrowIfCancellationRequested(); return false; }
        }
        if (await Probe()) return;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))! };
        start.ArgumentList.Add("--broker");
        start.ArgumentList.Add("--broker-pipe"); start.ArgumentList.Add(pipeName);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start CAD MCP broker");
        for (int i = 0; i < 10; i++)
        {
            if (await Probe()) return;
            await Task.Delay(100, ct);
        }
        throw new IOException("CAD MCP broker did not become ready");
    }
}
