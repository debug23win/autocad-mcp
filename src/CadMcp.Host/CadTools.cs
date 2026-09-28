using System.ComponentModel;
using System.Text.Json;
using CadMcp.Core;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

namespace CadMcp.Host;

[McpServerToolType]
public sealed class CadTools
{
    public static string BrokerPipe { get; set; } = Wire.BrokerPipe;
    private static async Task<CallToolResult> Call(string operation, string? session, string? document, object data, long? revision, CancellationToken ct)
    {
        var response = await PipeClient.CallAsync(BrokerPipe,
            new(Guid.NewGuid().ToString("N"), operation, session, document, revision, Wire.Element(data)), ct);
        return new() { IsError = response.Error is not null, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(response, Wire.Json) }] };
    }
    [McpServerTool(Name = "cad_sessions", ReadOnly = true), Description("List CAD workers and current document contexts. Requires the local broker.")]
    public static Task<CallToolResult> Sessions(CancellationToken ct) => Call("cad_sessions", null, null, new { }, null, ct);
    [McpServerTool(Name = "cad_context", ReadOnly = true), Description("Read active document identity, revision, units and supported capabilities.")]
    public static Task<CallToolResult> Context(string session_id, CancellationToken ct) => Call("cad_context", session_id, null, new { }, null, ct);
    [McpServerTool(Name = "cad_snapshot", ReadOnly = true), Description("Materialize a bounded snapshot of current-space top-level entities. Reports unsupported/custom objects and truncation; does not expand nested blocks.")]
    public static Task<CallToolResult> Snapshot(string session_id, string document_id, long expected_revision, CancellationToken ct, int limit = 2000) =>
        Call("cad_snapshot", session_id, document_id, new { limit }, expected_revision, ct);
    [McpServerTool(Name = "cad_entity_get", ReadOnly = true), Description("Read one top-level entity by hexadecimal handle at an expected revision. Block and custom object geometry may be partial.")]
    public static Task<CallToolResult> Entity(string session_id, string document_id, long expected_revision, string handle, CancellationToken ct) =>
        Call("cad_entity_get", session_id, document_id, new { handle }, expected_revision, ct);
    [McpServerTool(Name = "cad_query", ReadOnly = true), Description("Filter an immutable materialized snapshot by layer/type/text. Cursor is a numeric offset within the snapshot. Returns stale error after DWG changes.")]
    public static Task<CallToolResult> Query(string session_id, string document_id, long expected_revision, string snapshot_id, CancellationToken ct,
        string? layer = null, string? type = null, string? text = null, int offset = 0, int limit = 100) =>
        Call("cad_query", session_id, document_id, new { snapshot_id, layer, type, text, offset, limit }, expected_revision, ct);
    [McpServerTool(Name = "cad_focus", ReadOnly = false), Description("Select a top-level entity in the active document without modifying DWG geometry. Changes implied selection only.")]
    public static Task<CallToolResult> Focus(string session_id, string document_id, long expected_revision, string handle, CancellationToken ct) =>
        Call("cad_focus", session_id, document_id, new { handle }, expected_revision, ct);
    [McpServerTool(Name = "cad_render", ReadOnly = true), Description("Return a real AutoCAD preview image and view metadata at an expected revision. Preview fidelity must be checked in CAD; no pixel/world mapping is asserted.")]
    public static async Task<CallToolResult> Render(string session_id, string document_id, long expected_revision, CancellationToken ct, int width = 1024, int height = 768)
    {
        var response = await PipeClient.CallAsync(BrokerPipe, new(Guid.NewGuid().ToString("N"), "cad_render", session_id, document_id, expected_revision, Wire.Element(new { width, height })), ct);
        if (response.Error is not null) return new() { IsError = true, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(response, Wire.Json) }] };
        var data = (JsonElement)response.Data!;
        var metadata = data.EnumerateObject().Where(p => p.Name != "image_base64").ToDictionary(p => p.Name, p => p.Value);
        return new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(response with { Data = metadata }, Wire.Json) },
            ImageContentBlock.FromBytes(Convert.FromBase64String(data.GetProperty("image_base64").GetString()!), "image/png")] };
    }
}
