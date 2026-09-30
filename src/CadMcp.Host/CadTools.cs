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
    [McpServerTool(Name = "cad_vertical_catalog", ReadOnly = true), Description("Read the optional Civil 3D and Map 3D product APIs in the running CAD process: bounded lists of surfaces, alignments and pipe networks; Map coordinate systems and object-data table names. Reports unavailable explicitly if a vertical API is not loaded. No vendor DLL is bundled.")]
    public static Task<CallToolResult> VerticalCatalog(string session_id, string document_id, long expected_revision, CancellationToken ct) =>
        Call("cad_vertical_catalog", session_id, document_id, new { }, expected_revision, ct);
    [McpServerTool(Name = "cad_vertical_get", ReadOnly = true), Description("Inspect a Civil 3D surface/alignment/profile/network or Map 3D object-data-bearing entity by hexadecimal handle. For Civil surfaces, optional sample_points_json is 1..100 [x,y] WCS pairs and returns elevations or per-point errors. Bounded child collections and object-data values are included when the relevant product API is loaded.")]
    public static Task<CallToolResult> VerticalGet(string session_id, string document_id, long expected_revision, string handle, CancellationToken ct, string? sample_points_json = null) =>
        Call("cad_vertical_get", session_id, document_id, new { handle, sample_points_json }, expected_revision, ct);
    [McpServerTool(Name = "cad_edit_help", ReadOnly = true), Description("Read the native edit operation contract and examples before using cad_edit. Coordinates are WCS drawing units; angles are degrees.")]
    public static CallToolResult EditHelp() => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new {
        operations = EditPlan.Fields, coordinates = "WCS drawing units, [x,y] or [x,y,z]", angles = "degrees around WCS Z",
        contract = "operations_json is a JSON array of 1..100 objects. Every object has op. Optional id names an entity for later target references. Existing entities use handle; handle and target are mutually exclusive. Create a missing layer first. Read cad_catalog for block/style names. Native C# supports meshes, spatial curves, 3D solids, extrude, sweep, revolve and solid Boolean operations; use these before cad_lisp. Repeat only the EXACT request with the SAME operation_id; do not change expected_revision when replaying. Read cad_operation_status after an ambiguous response. After success use returned revision for further calls. Locked layers, xref and most specialized objects are not edited through the native path. The whole native batch rolls back on any pre-commit error. Do not repeat unknown mutations automatically.",
        notes = new { polyline = "points share WCS Z; bulges: one number per vertex; closed:boolean; width:nonnegative constant segment width",
            polyline3d = "points is 2..2000 full [x,y,z] WCS points; optional closed:boolean",
            spline = "fit_points is 3..2000 full [x,y,z] WCS points; optional degree 1..11 below point count, closed:boolean; zero fit tolerance",
            mesh = "vertices is 3..5000 full [x,y,z] WCS points; faces is 1..2000 arrays of 3 or 4 distinct zero-based vertex indices. Produces an unsmoothed native SubDMesh, not an inferred mesh from a photograph",
            extrude = "handle/target identifies an existing closed planar polyline or circle; direction:[dx,dy,dz] is a nonzero WCS extrusion vector; source profile remains",
            sweep = "handle/target identifies an existing closed profile; path_handle/path_target identifies an existing path curve; profile should start on and be oriented for the path; source entities remain",
            revolve = "handle/target identifies an existing closed planar profile; axis_start/axis_end define the WCS axis; angle_deg has magnitude at most 360; source remains",
            solid_boolean = "handle/target is the primary Solid3d, tool_handle/tool_target is the other Solid3d; operation union/subtract/intersect; keep_tool defaults false; entire result is one transaction",
            rotate3d = "handle/target identifies an entity; axis_start/axis_end define the WCS rotation axis; angle_deg is signed",
            sphere_cone_wedge_torus = "sphere center is geometric center; cone center is bottom center; wedge/torus center is bounding-box center; torus minor_radius < major_radius",
            block = "name:existing local block; scale:positive scalar or [sx,sy,sz]; attributes:{TAG:string}",
            block_define = "name:new local definition; base_point:WCS insertion base; handles:1..100 current-space top-level source entities. Source entities remain; native C# clones them into the definition",
            layout_create = "Create an empty paper-space layout with the supplied name. Add title block and viewports separately before export",
            layout_copy = "Copy a complete existing paper-space layout including sheet contents and plot settings",
            layout_configure = "Set print device, media, plot style, paper units and rotation for a named paper-space layout",
            viewport = "Place a locked paper-space model viewport on a named layout. center/width/height are paper coordinates; model_center/model_height define WCS view and scale",
            image_attach = "Attach PNG/JPEG/TIFF/BMP by absolute path. control_points has 3..20 {pixel:[x,y],world:[x,y,z]} anchors; pixel origin top-left, all WCS Z equal. Affine residual and orientation are returned. Source file remains externally referenced",
            civil_tin_create = "In Civil 3D only, create a native TIN surface from 3..5000 non-collinear WCS [x,y,z] vertices using the current template's default surface style",
            civil_tin_add_points = "In Civil 3D only, add 3..5000 WCS [x,y,z] vertices to an existing TIN surface by handle",
            ellipse = "major_axis is a nonzero WCS XY vector from the center; radius_ratio is >0 and <=1; optional start/end angles default to 0/360 degrees",
            hatch = "boundaries:[handle or earlier id], closed polylines/circles; first is outer, others are islands; pattern defaults SOLID",
            set = "Only properties supported by the entity type are accepted; text is DBText/MText/Dimension; attributed blocks move/rotate through transform tools; use attributes to change tag values",
            cylinder = "center is center of bottom face, height positive along WCS Z", box = "center is solid center",
            color_index = "ACI 0..256 for entities (0 ByBlock, 256 ByLayer); layers 1..255", lineweight = "hundredths of mm; -1 ByLayer, -2 ByBlock, -3 default" },
        example = new object[] { new { op = "layer", name = "Сеть", color_index = 3 }, new { op = "line", id = "pipe", start = new[] { 0, 0, 0 }, end = new[] { 100, 0, 0 }, layer = "Сеть" }, new { op = "move", target = "pipe", displacement = new[] { 0, 50, 0 } } }
    }, Wire.Json) }] };
    [McpServerTool(Name = "cad_edit", ReadOnly = false, Destructive = true, Idempotent = false), Description("Create/edit DWG entities in one native C# transaction, immediately as requested. Read cad_edit_help first. Includes polygon meshes, 3D polylines and splines, solid primitives/extrude/sweep/revolve/Boolean operations, blocks, layouts, dimensions and transforms. Supply a unique operation_id for status/replay. Returns handles and database readback. Never automatically retry an unknown mutation.")]
    public static Task<CallToolResult> Edit(string session_id, string document_id, long expected_revision, string operation_id, string operations_json, CancellationToken ct) =>
        Call("cad_edit", session_id, document_id, new { operation_id, operations_json }, expected_revision, ct);
    [McpServerTool(Name = "cad_export", ReadOnly = false, Destructive = false, Idempotent = false), Description("Export the current DWG through native C# AutoCAD APIs. format pdf plots one named layout (or current layout) through DWG To PDF.pc3; optional media_name selects a paper size. format dxf exports the drawing database. path must be an absolute path ending in .pdf or .dxf; existing files are never overwritten. Returns output size and SHA-256 after checking the file. Supply a unique operation_id and inspect cad_operation_status after an ambiguous response.")]
    public static Task<CallToolResult> Export(string session_id, string document_id, long expected_revision, string operation_id, string format, string path, CancellationToken ct,
        string? layout = null, string? media_name = null) =>
        Call("cad_export", session_id, document_id, new { operation_id, format, path, layout, media_name }, expected_revision, ct);
    [McpServerTool(Name = "cad_publish", ReadOnly = false, Destructive = false, Idempotent = false), Description("Plot 1..100 named paper layouts into separate PDFs using native AutoCAD C#. output_folder must already exist and contain no conflicting output or CAD MCP manifest. layouts_json is a JSON array of layout names in release order. Writes JSON and CSV file registers with sizes and SHA-256; on a partial failure, the manifests record the PDFs already produced. Supply a unique operation_id and inspect cad_operation_status after an ambiguous response.")]
    public static Task<CallToolResult> Publish(string session_id, string document_id, long expected_revision, string operation_id, string output_folder, string layouts_json, CancellationToken ct) =>
        Call("cad_publish", session_id, document_id, new { operation_id, output_folder, layouts_json }, expected_revision, ct);
    [McpServerTool(Name = "cad_lisp", ReadOnly = false, Destructive = true, Idempotent = false), Description("Last-resort fallback only for a specific AutoCAD/vendor operation absent from cad_edit_help and not expressible with native C# composition. Native C# includes meshes and advanced solid modeling. Executes code inside progn; supply all command-s arguments, no interactive prompts. Returns QUEUED: poll cad_operation_status and verify resulting geometry or files. Errors can leave partial changes. Never retry with a new id after an uncertain result.")]
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
    [McpServerTool(Name = "cad_render", ReadOnly = true), Description("Return a real AutoCAD preview image with an image_id and actual pixel dimensions. Pixel origin is top-left. For an exact image-to-WCS transform, call cad_image_register with at least 3 known pixel/WCS control points from this specific image; preview cropping is not assumed from viewport metadata.")]
    public static async Task<CallToolResult> Render(string session_id, string document_id, long expected_revision, CancellationToken ct, int width = 1024, int height = 768)
    {
        var response = await PipeClient.CallAsync(BrokerPipe, new(Guid.NewGuid().ToString("N"), "cad_render", session_id, document_id, expected_revision, Wire.Element(new { width, height })), ct);
        if (response.Error is not null) return new() { IsError = true, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(response, Wire.Json) }] };
        var data = (JsonElement)response.Data!;
        var metadata = data.EnumerateObject().Where(p => p.Name != "image_base64").ToDictionary(p => p.Name, p => p.Value);
        return new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(response with { Data = metadata }, Wire.Json) },
            ImageContentBlock.FromBytes(Convert.FromBase64String(data.GetProperty("image_base64").GetString()!), "image/png")] };
    }
    [McpServerTool(Name = "cad_image_register", ReadOnly = true), Description("Calibrate one cad_render image to the DWG WCS XY plane using 3..20 point pairs. control_points_json: [{\"pixel\":[x,y],\"world\":[x,y,z]},...]. Pixels have top-left origin; WCS Z must be common. Returns 2D affine transform, corners and RMS/max residual. Registration is kept for up to 8 images in the current worker session and does not modify the DWG.")]
    public static Task<CallToolResult> ImageRegister(string session_id, string document_id, long expected_revision, string image_id, string control_points_json, CancellationToken ct) =>
        Call("cad_image_register", session_id, document_id, new { image_id, control_points_json }, expected_revision, ct);
    [McpServerTool(Name = "cad_image_point", ReadOnly = true), Description("Convert 1..100 coordinates through a previously calibrated cad_render image. points_json is an array of [x,y] pairs; direction is pixel_to_world (default) or world_to_pixel. Returns captured_revision and historical flag so older images are not mistaken for the current drawing.")]
    public static Task<CallToolResult> ImagePoint(string session_id, string document_id, long expected_revision, string image_id, string points_json, CancellationToken ct, string direction = "pixel_to_world") =>
        Call("cad_image_point", session_id, document_id, new { image_id, points_json, direction }, expected_revision, ct);
}
