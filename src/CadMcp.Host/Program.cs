using CadMcp.Core;
using CadMcp.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Contains("--broker"))
{
    using var mutex = new Mutex(true, @"Local\" + Wire.BrokerPipe, out bool owns);
    if (!owns) return;
    var broker = new Broker(Wire.WorkerRoot);
    using var server = new PipeServer(Wire.BrokerPipe, broker.DispatchAsync);
    server.Start();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Dispose(); };
    await server.Completion;
    return;
}
int pipeOption = Array.IndexOf(args, "--broker-pipe");
if (pipeOption >= 0) CadTools.BrokerPipe = args[pipeOption + 1];
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<CadTools>();
await builder.Build().RunAsync();
