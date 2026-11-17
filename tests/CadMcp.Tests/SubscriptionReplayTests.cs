using CadMcp.Core;
using CadMcp.Providers;

namespace CadMcp.Tests;

public sealed class SubscriptionReplayTests
{
    [Fact]
    public async Task Advertised_CAD_mutations_require_durable_replay_identity_and_disallow_safe_retry_annotations()
    {
        // The official Kimi CLI can resend identical MCP arguments after reconnecting. Its
        // original operation_id must reach the worker's durable acceptance gate unchanged.
        // Qwen additionally uses these annotations to refuse automatic mutation replay.
        using var root = new TempFolder();
        var options = new ProviderOptions("unused", TestEnvironment.HostExecutable, root.Path,
            McpArguments: ["--broker-pipe", "cadmcp-schema-only-" + Guid.NewGuid().ToString("N")],
            OwnerId: "schema-review", CadSessionId: "pinned-session", CadDocumentId: "pinned-document");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var mcp = await PerplexityMcpSession.StartAsync(options, timeout.Token);

        // Focus and cancellation change editor/control state without changing DWG geometry or
        // writing files. Every other advertised writer must participate in the receipt gate.
        var mutations = mcp.Tools.Where(tool =>
            !tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean() &&
            tool.GetProperty("name").GetString() is not ("cad_focus" or "cad_cancel"))
            .OrderBy(tool => tool.GetProperty("name").GetString(), StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "cad_csharp", "cad_edit", "cad_export", "cad_lisp", "cad_publish" },
            mutations.Select(tool => tool.GetProperty("name").GetString()));

        foreach (var tool in mutations)
        {
            string name = tool.GetProperty("name").GetString()!;
            Assert.True(CadOperations.IsMutation(name), name + " must use the durable operation journal.");
            Assert.False(tool.GetProperty("annotations").GetProperty("idempotentHint").GetBoolean());
            var schema = tool.GetProperty("inputSchema");
            Assert.Contains("operation_id", schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal("string", schema.GetProperty("properties").GetProperty("operation_id").GetProperty("type").GetString());
        }
    }
}
