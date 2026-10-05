using System.Collections.Concurrent;
using CadMcp.Core;
using CadMcp.Providers;
namespace CadMcp.Tests;

internal static class SubagentProbe
{
    public static async Task Run(string providerName,string executable)
    {
        var calls=new ConcurrentQueue<Request>();string pipe="cad-subagent-probe-"+Guid.NewGuid().ToString("N");
        using var server=new PipeServer(pipe,(r,ct)=>{calls.Enqueue(r);return Task.FromResult(new Response(r.RequestId,"completed",new{name="Synthetic read-only agent fixture",units="Millimeters",space="model"},"fixture-session","fixture-document",1));});server.Start();
        string root=Path.Combine(Environment.CurrentDirectory,".runtime","agent-fixture");Directory.CreateDirectory(root);
        var options=new ProviderOptions(executable,Path.GetFullPath("src/CadMcp.Host/bin/Release/net8.0-windows/CadMcp.Host.exe"),root,MaxSubagents:2,McpArguments:["--broker-pipe",pipe],OwnerId:"fixture-owner",CadSessionId:"fixture-session",CadDocumentId:"fixture-document");
        IChatProvider provider=providerName=="claude"?new ClaudeProvider(options):new CodexProvider(options);
        string role=providerName=="claude"?"cad_researcher":"worker";
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string prompt="This is an integration fixture with no AutoCAD or real DWG connected. Do not use shell, files, web or other MCP servers. Spawn exactly one "+role+" subagent. Its ONLY task: call cad_context(session_id=fixture-session,document_id=fixture-document), inspect access.read_only, and return that value. Wait for the subagent. Then the PARENT must call cad_context and cad_edit once with session_id=fixture-session, document_id=fixture-document, expected_revision=1, operation_id=fixture, operations_json=[{\"op\":\"line\",\"start\":[0,0],\"end\":[1,1]}]. I authorize this synthetic recorded parent edit. Report child read_only, parent read_only and parent edit result. This is a short task but delegation is explicitly required for this integration test.";
        await foreach(var item in provider.SendAsync(prompt,timeout.Token))if(item.Kind is "text" or "step")Console.WriteLine(item.Kind+": "+item.Text);
        if(calls.Count(r=>MutationRecovery.IsMutation(r.Operation))!=1)throw new Exception("Expected exactly one parent fixture edit");
        if(!calls.Any(r=>r.Operation=="cad_context"&&r.Data.Text("client_access")=="read_only"))throw new Exception("No independently read-only helper reached the fixture");
        if(!calls.Any(r=>r.Operation=="cad_context"&&r.Data.Text("client_access")=="primary"))throw new Exception("The primary writer also lost write access");
        Console.WriteLine("PASS actual "+providerName+" subagent uses independently enforced read-only MCP host");
    }
}
