using CadMcp.Core;
using CadMcp.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Contains("--version")) { Console.WriteLine("CAD MCP " + Wire.Version + " (AutoCAD / Map 3D / Civil 3D 2025–2027)"); return 0; }
if (args.Contains("--register-client") || args.Contains("--unregister-client")) return ClientRegistration.Run(args);
if (args.Contains("--grade")) return Benchmark.Grade(args);
if (args.Contains("--grade-selfcheck")) return Benchmark.SelfCheck(args);
int pipeOption = Array.IndexOf(args, "--broker-pipe");
if (pipeOption >= 0 && pipeOption + 1 >= args.Length) { Console.Error.WriteLine("--broker-pipe requires a pipe name"); return 2; }
string pipeName = pipeOption >= 0 ? args[pipeOption + 1] : Wire.BrokerPipe;
if (args.Contains("--stop-broker"))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    try { await PipeClient.CallAsync(pipeName, new(Guid.NewGuid().ToString("N"), "broker_stop"), timeout.Token); }
    catch (Exception e) when (Wire.IsTransportFailure(e)) { Console.Error.WriteLine("Broker unavailable: " + e.Message); }
    return 0;
}
if (args.Contains("--broker"))
{
    // One broker per pipe name. A stopping broker holds the name until it has drained and exited;
    // BrokerBootstrap waits for that before it starts a replacement.
    using var mutex = new Mutex(true, @"Local\" + pipeName, out bool owns);
    if (!owns) return 0;
    // Requests in progress get this long to finish after broker_stop.
    var drain = TimeSpan.FromSeconds(10);
    var broker = new Broker(Wire.WorkerRoot);
    PipeServer? brokerServer = null;
    using var server = new PipeServer(pipeName, async (request, ct) =>
    {
        if (request.Operation == "broker_stop")
        {
            _ = Task.Run(async () => { await Task.Delay(300); if (brokerServer is { } running) await running.StopAsync(drain); });
            return new Response(request.RequestId, "completed", new { stopping = true, drain_seconds = drain.TotalSeconds });
        }
        return await broker.DispatchAsync(request, ct);
    });
    brokerServer = server;
    server.Start();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; _ = server.StopAsync(drain); };
    await server.Completion;
    return 0;
}
if (pipeOption >= 0) CadTools.BrokerPipe = pipeName;
else await BrokerBootstrap.EnsureAsync(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "CadMcp.Host.exe" : "CadMcp.Host"), CancellationToken.None);
if (args.Contains("--capture-evidence")) return await Benchmark.CaptureAsync(args);
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMcpServer(options => options.ServerInstructions = CadTools.ServerInstructions).WithStdioServerTransport().WithTools<CadTools>()
    .WithRequestFilters(filters=>filters.AddCallToolFilter(next=>async(context,ct)=>
    {using var access=CadAccess.Scope(context.Params?.Meta);return await next(context,ct);}));
await builder.Build().RunAsync();
return 0;
