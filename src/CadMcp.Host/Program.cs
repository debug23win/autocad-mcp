using CadMcp.Core;
using CadMcp.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Contains("--version")) { Console.WriteLine("CAD MCP 0.9.0-preview (AutoCAD / Map 3D / Civil 3D 2025–2027)"); return; }
int pipeOption = Array.IndexOf(args, "--broker-pipe");
string pipeName = pipeOption >= 0 ? args[pipeOption + 1] : Wire.BrokerPipe;
if (args.Contains("--stop-broker"))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    try { await PipeClient.CallAsync(pipeName, new(Guid.NewGuid().ToString("N"), "broker_stop"), timeout.Token); }
    catch (Exception e) when (e is IOException or OperationCanceledException) { Console.Error.WriteLine("Broker unavailable: " + e.Message); }
    return;
}
if (args.Contains("--broker"))
{
    using var mutex = new Mutex(true, @"Local\" + pipeName, out bool owns);
    if (!owns) return;
    var broker = new Broker(Wire.WorkerRoot);
    PipeServer? brokerServer = null;
    using var server = new PipeServer(pipeName, async (request, ct) =>
    {
        if (request.Operation == "broker_stop")
        {
            _ = Task.Run(async () => { await Task.Delay(300); brokerServer?.Dispose(); });
            return new Response(request.RequestId, "completed", new { stopping = true });
        }
        return await broker.DispatchAsync(request, ct);
    });
    brokerServer = server;
    server.Start();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Dispose(); };
    await server.Completion;
    return;
}
if (pipeOption >= 0) CadTools.BrokerPipe = pipeName;
else await BrokerBootstrap.EnsureAsync(Path.Combine(AppContext.BaseDirectory, "CadMcp.Host.exe"), CancellationToken.None);
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<CadTools>();
await builder.Build().RunAsync();
