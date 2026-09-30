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
    [McpServerTool(Name = "cad_catalog", ReadOnly = true), Description("Read layers, local blocks and attributes, layouts, text/dimension styles and linetypes at the current revision. Use these names before inserting or editing objects.")]
    public static Task<CallToolResult> Catalog(string session_id, string document_id, long expected_revision, CancellationToken ct) => Call("cad_catalog", session_id, document_id, new { }, expected_revision, ct);
    [McpServerTool(Name = "cad_edit_help", ReadOnly = true), Description("Read the native edit operation contract and examples before using cad_edit. Coordinates are WCS drawing units; angles are degrees.")]
    public static CallToolResult EditHelp() => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new {
        operations = EditPlan.Fields, coordinates = "WCS drawing units, [x,y] or [x,y,z]", angles = "degrees around WCS Z",
        contract = "operations_json is a JSON array of 1..100 objects. Every object has op. Optional id names an entity for later target references. Existing entities use handle; handle and target are mutually exclusive. Create a missing layer first. Read cad_catalog for block/style names. Repeat only the EXACT request with the SAME operation_id; do not change expected_revision when replaying. Read cad_operation_status after an ambiguous response. After success use returned revision for further calls. Locked layers, xref and specialized objects are not edited through the native path. The whole native batch rolls back on any pre-commit error. Do not repeat unknown mutations automatically.",
        notes = new { polyline = "points share WCS Z; bulges: one number per vertex; closed:boolean; width:nonnegative constant segment width",
            block = "name:existing local block; scale:positive scalar or [sx,sy,sz]; attributes:{TAG:string}",
            block_define = "name:new local definition; base_point:WCS insertion base; handles:1..100 current-space top-level source entities. Source entities remain; native C# clones them into the definition",
            layout_create = "Create an empty paper-space layout with the supplied name. Add title block and viewports separately before export",
            ellipse = "major_axis is a nonzero WCS XY vector from the center; radius_ratio is >0 and <=1; optional start/end angles default to 0/360 degrees",
            hatch = "boundaries:[handle or earlier id], closed polylines/circles; first is outer, others are islands; pattern defaults SOLID",
            set = "Only properties supported by the entity type are accepted; text is DBText/MText/Dimension; attributed blocks move/rotate through transform tools; use attributes to change tag values",
            cylinder = "center is center of bottom face, height positive along WCS Z", box = "center is solid center",
            color_index = "ACI 0..256 for entities (0 ByBlock, 256 ByLayer); layers 1..255", lineweight = "hundredths of mm; -1 ByLayer, -2 ByBlock, -3 default" },
        example = new object[] { new { op = "layer", name = "Сеть", color_index = 3 }, new { op = "line", id = "pipe", start = new[] { 0, 0, 0 }, end = new[] { 100, 0, 0 }, layer = "Сеть" }, new { op = "move", target = "pipe", displacement = new[] { 0, 50, 0 } } }
    }, Wire.Json) }] };
    [McpServerTool(Name = "cad_edit", ReadOnly = false, Destructive = true, Idempotent = false), Description("Create/edit DWG entities in one native C# transaction, immediately as requested. Read cad_edit_help first. Includes geometry, blocks and definitions, layouts, dimensions and transforms. Supply a unique operation_id for status/replay. Returns handles and database readback. Never automatically retry an unknown mutation.")]
    public static Task<CallToolResult> Edit(string session_id, string document_id, long expected_revision, string operation_id, string operations_json, CancellationToken ct) =>
        Call("cad_edit", session_id, document_id, new { operation_id, operations_json }, expected_revision, ct);
    [McpServerTool(Name = "cad_export", ReadOnly = false, Destructive = false, Idempotent = false), Description("Export the current DWG through native C# AutoCAD APIs. format pdf plots one named layout (or current layout) through DWG To PDF.pc3; optional media_name selects a paper size. format dxf exports the drawing database. path must be an absolute path ending in .pdf or .dxf; existing files are never overwritten. Returns output size and SHA-256 after checking the file. Supply a unique operation_id and inspect cad_operation_status after an ambiguous response.")]
    public static Task<CallToolResult> Export(string session_id, string document_id, long expected_revision, string operation_id, string format, string path, CancellationToken ct,
        string? layout = null, string? media_name = null) =>
        Call("cad_export", session_id, document_id, new { operation_id, format, path, layout, media_name }, expected_revision, ct);
    [McpServerTool(Name = "cad_lisp", ReadOnly = false, Destructive = true, Idempotent = false), Description("Last-resort fallback for AutoCAD/vendor commands unavailable through native C# tools. Executes code inside progn; supply all command-s arguments, no interactive prompts. Returns QUEUED: poll cad_operation_status and verify resulting geometry or files. Errors can leave partial changes. Never retry with a new id after an uncertain result.")]
    public static Task<CallToolResult> Lisp(string session_id, string document_id, long expected_revision, string operation_id, string code, CancellationToken ct) =>
        Call("cad_lisp", session_id, document_id, new { operation_id, code }, expected_revision, ct);
    [McpServerTool(Name = "cad_operation_status", ReadOnly = true), Description("Read state and full result of a mutation by operation_id: queued/running/completed/failed/not_found. No revision needed. Records last for the current worker session, capped at 128 with no eviction. not_found after restart does not prove an operation did not run: inspect drawings before retrying.")]
    public static Task<CallToolResult> OperationStatus(string session_id, string document_id, string operation_id, CancellationToken ct) =>
        Call("cad_operation_status", session_id, document_id, new { operation_id }, null, ct);
    [McpServerTool(Name = "cad_snapshot", ReadOnly = true), Description("Materialize a bounded snapshot of current-space top-level entities. Reports unsupported/custom objects and truncation; does not expand nested blocks.")]
    public static Task<CallToolResult> Snapshot(string session_id, string document_id, long expected_revision, CancellationToken ct, int limit = 2000) =>
        Call("cad_snapshot", session_id, document_id, new { limit }, expected_revision, ct);
    [McpServerTool(Name = "cad_entity_get", ReadOnly = true), Description("Read one top-level entity by hexadecimal handle. A stale expected_revision does not block reading; the response has the current revision. Block and custom object geometry may be partial.")]
    public static Task<CallToolResult> Entity(string session_id, string document_id, long expected_revision, string handle, CancellationToken ct) =>
        Call("cad_entity_get", session_id, document_id, new { handle }, expected_revision, ct);
    [McpServerTool(Name = "cad_query", ReadOnly = true), Description("Filter an immutable materialized snapshot by layer/type/text. Cursor is a numeric offset within the snapshot. Returns captured_revision and historical=true after DWG changes; do not treat historical geometry as current.")]
    public static Task<CallToolResult> Query(string session_id, string document_id, long expected_revision, string snapshot_id, CancellationToken ct,
        string? layer = null, string? type = null, string? text = null, int offset = 0, int limit = 100) =>
        Call("cad_query", session_id, document_id, new { snapshot_id, layer, type, text, offset, limit }, expected_revision, ct);
    [McpServerTool(Name = "cad_search", ReadOnly = true), Description("Search the drawing database, including entities beyond snapshot limits. options_json: scope current/model/layouts/all/selection/view, layer/type/text filters, WCS bounds:{min:[x,y,z],max:[x,y,z]}, offset, limit 1..500, expand_blocks, max_depth 0..12, details. Returns paths, root handles, world coordinates, current revision and next_offset. A stale expected_revision does not block reading. Nested/xref paths are read-only; focus root_handle. View uses a conservative WCS envelope, not exact pixel geometry.")]
    public static Task<CallToolResult> Search(string session_id, string document_id, long expected_revision, string options_json, CancellationToken ct) =>
        Call("cad_search", session_id, document_id, new { options_json }, expected_revision, ct);
    [McpServerTool(Name = "cad_result_get", ReadOnly = true), Description("Read a bounded JSON text page of an archived large CAD result. The captured revision is historical; do not use old geometry as current facts. Supply archive_id, offset and limit 1..32000 characters; concatenate text pages to reconstruct the JSON.")]
    public static Task<CallToolResult> Result(string session_id, string document_id, long expected_revision, string archive_id, CancellationToken ct, int offset = 0, int limit = 16000) =>
        Call("cad_result_get", session_id, document_id, new { archive_id, offset, limit }, expected_revision, ct);
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
