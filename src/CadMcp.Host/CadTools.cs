using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CadMcp.Core;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

namespace CadMcp.Host;

[McpServerToolType]
public sealed class CadTools
{
    [McpServerTool(Name="cad_steel_catalog",ReadOnly=true),Description("Read nominal steel section codes, millimetre dimensions, root radii and theoretical mass at 7850 kg/m3. Use exact profile in assembly_create beam/column; the native adapter converts catalog mm to DWG units. Grade and structural capacity are separate. Catalog source links are included.")]
    public static object SteelCatalog()=>new {sections=SteelSections.Catalog,units="millimetres",density_kg_m3=7850,scope="nominal geometry; verify availability and order tolerances with the manufacturer"};
    public static string BrokerPipe { get; set; } = Wire.BrokerPipe;
    private static readonly string Owner = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CAD_MCP_OWNER_ID")) ? Guid.NewGuid().ToString("N") : Environment.GetEnvironmentVariable("CAD_MCP_OWNER_ID")!;
    private static async Task<Response> ScopedResponse(string operation, string? session, string? document, object data, long? revision, CancellationToken ct)
    {
        // Allowlist: an operation is available to read-only helpers only after it is declared read-only.
        if(CadAccess.ReadOnly&&!CadOperations.HelperAllowed.Contains(operation))throw new CadFault("HELPER_READ_ONLY","Review helpers cannot mutate drawings, focus, publish or cancel work");
        string? pinnedSession=Environment.GetEnvironmentVariable("CAD_MCP_SESSION_ID"), pinnedDocument=Environment.GetEnvironmentVariable("CAD_MCP_DOCUMENT_ID");
        if(string.IsNullOrWhiteSpace(pinnedSession))pinnedSession=null;if(string.IsNullOrWhiteSpace(pinnedDocument))pinnedDocument=null;
        if(pinnedSession is not null && session is not null && pinnedSession!=session)throw new CadFault("PROJECT_SCOPE_MISMATCH","This chat is pinned to another AutoCAD session");
        if(pinnedDocument is not null && document is not null && pinnedDocument!=document)throw new CadFault("PROJECT_SCOPE_MISMATCH","This chat and its helpers can access only their assigned DWG");
        session ??= pinnedSession;document ??= pinnedDocument;
        if(operation=="cad_sessions" && pinnedSession is not null)operation="cad_context";
        bool readOnlyClient=CadAccess.ReadOnly;
        if(operation=="cad_context")data=new{client_access=readOnlyClient?"read_only":"primary"};
        var response = await MutationRecovery.CallAsync(
            new(Guid.NewGuid().ToString("N"), operation, session, document, revision, Wire.Element(data), OwnerId: Owner),
            (r, token) => PipeClient.CallAsync(BrokerPipe, r, token), ct);
        if(operation=="cad_documents" && pinnedDocument is not null && response.Error is null)
            response=response with {Data=Wire.Element(response.Data!).EnumerateArray().Where(d=>d.Text("document_id")==pinnedDocument).Select(d=>d.Clone()).ToArray()};
        if(operation=="cad_context"&&response.Error is null&&Wire.Element(response.Data!).ValueKind==JsonValueKind.Object)
        {
            var fields=Wire.Element(response.Data!).EnumerateObject().ToDictionary(p=>p.Name,p=>(object?)p.Value.Clone());
            fields["access"]=new{read_only=readOnlyClient,document_pin=pinnedDocument,changes=readOnlyClient?"server rejects mutations, focus and cancellation":"primary writer"};
            response=response with{Data=fields};
        }
        return response;
    }
    /// <summary>A read through the same scoping as the tools, for the benchmark's evidence capture.</summary>
    internal static Task<Response> RequestAsync(string operation, string session, string document, object data, CancellationToken ct) => ScopedResponse(operation, session, document, data, null, ct);
    private static async Task<CallToolResult> Call(string operation,string? session,string? document,object data,long? revision,CancellationToken ct)
    {
        return Result(await ScopedResponse(operation,session,document,data,revision,ct));
    }
    private static CallToolResult Result(Response response) =>
        new() { IsError=response.Error is not null,Content=[new TextContentBlock {Text=JsonSerializer.Serialize(response,Wire.Json)}] };
    [McpServerTool(Name = "cad_vertical_capabilities",ReadOnly=true),Description("Read actually loaded Civil/Map API versions and exact supported method signatures before editing vertical objects. Missing API is explicitly unavailable.")]
    public static Task<CallToolResult> VerticalCapabilities(string session_id,string document_id,CancellationToken ct)=>Call("cad_vertical_capabilities",session_id,document_id,new{},null,ct);
    [McpServerTool(Name = "cad_documents", ReadOnly = true), Description("List open DWGs with document ids, project keys and active status. Pin every task to a specific document_id; concurrent chats are isolated.")]
    public static Task<CallToolResult> Documents(string session_id,CancellationToken ct)=>Call("cad_documents",session_id,null,new{},null,ct);
    [McpServerTool(Name = "cad_cancel", ReadOnly = false), Description("Cancel this chat owner queued/running operations in a drawing. Native calls finish at a safe boundary; a running Lisp command needs Esc. Does not cancel another chat work.")]
    public static Task<CallToolResult> Cancel(string session_id,string document_id,CancellationToken ct,string? operation_id=null)=>Call("cad_cancel",session_id,document_id,new{operation_id},null,ct);
    [McpServerTool(Name = "cad_runtime_status", ReadOnly = true), Description("Read worker operation phases and cancellation status without waiting for the CAD UI thread. cad_thread reports the last idle time, an open modal dialog, the command in progress and an AutoLISP job waiting for the user's confirmation.")]
    public static Task<CallToolResult> RuntimeStatus(string session_id,string document_id,CancellationToken ct)=>Call("cad_runtime_status",session_id,document_id,new{},null,ct);
    [McpServerTool(Name = "cad_diagnostics", ReadOnly = true), Description("Read durable last-operation phase, plugin version and DWG checkpoint locations, including previous worker sessions. A nonterminal old phase means unknown outcome; never automatically retry it.")]
    public static Task<CallToolResult> Diagnostics(string session_id,CancellationToken ct)=>Call("cad_diagnostics",session_id,null,new{},null,ct);
    [McpServerTool(Name = "cad_review", ReadOnly = true), Description("Check up to 250 selected entities (or current space): actual solid interferences, annotation extents, native table text fit, configured paper boundaries, dimension texts that hide or contradict the measurement, characters missing from the text style's font (shown as '?'), duplicate, zero-length, overlapping or self-intersecting curves. Severity info is a note and does not change the state. " +
        "options_json (optional) {checks:[\"standard\",\"topology\"], scope:\"current\"|\"model\"|\"layout\", layout_name, layers:[names or patterns with * ? # , ~], tolerance, gap_tolerance, endpoints, crossings, max_entities<=50000, max_findings<=5000}: topology checks the linework of the whole scope as a network (dangling ends, near misses up to gap_tolerance, T-junctions, crossings without a node, overlaps, duplicates, self-intersections; XY, top-level curves). Review warnings and intentional joints; inspect the actual rendered view too.")]
    public static Task<CallToolResult> Review(string session_id,string document_id,CancellationToken ct,string? handles_json=null,string? options_json=null)=>Call("cad_review",session_id,document_id,new{handles_json,options_json},null,ct);
    [McpServerTool(Name = "cad_solid_get", ReadOnly = true), Description("Read native Solid3d volume, edge and face topology ids at the current revision. Refresh after each fillet/chamfer/shell; ids are not stable through topology changes.")]
    public static Task<CallToolResult> SolidGet(string session_id,string document_id,string handle,CancellationToken ct)=>Call("cad_solid_get",session_id,document_id,new{handle},null,ct);
    [McpServerTool(Name = "cad_assembly_get", ReadOnly = true), Description("Read parametric native structural assembly recipe, mark, material, volume and mass scope. Geometry is not a structural capacity calculation.")]
    public static Task<CallToolResult> AssemblyGet(string session_id,string document_id,string handle,CancellationToken ct)=>Call("cad_assembly_get",session_id,document_id,new{handle},null,ct);
    [McpServerTool(Name = "cad_table_dependencies", ReadOnly = true), Description("Read persisted stable cell identities and table formula dependency graph. CAD MCP edits automatically recalculate linked tables; deletion of referenced rows is rejected.")]
    public static Task<CallToolResult> TableDependencies(string session_id,string document_id,CancellationToken ct)=>Call("cad_table_dependencies",session_id,document_id,new{},null,ct);
    [McpServerTool(Name = "cad_release_check", ReadOnly = true), Description("Check a sheet set before publishing: missing external references/fonts and glyphs, title/designation, sheet numbers and totals, unresolved fields (####), plot device, plot area and scale, table fit and paper boundaries. Errors fail, warnings need review, notes are informational. layouts_json is an array of exact layout names.")]
    public static Task<CallToolResult> ReleaseCheck(string session_id,string document_id,string layouts_json,CancellationToken ct)=>Call("cad_release_check",session_id,document_id,new{layouts_json},null,ct);
    [McpServerTool(Name = "cad_sessions", ReadOnly = true), Description("List CAD workers and current document contexts. Requires the local broker.")]
    public static Task<CallToolResult> Sessions(CancellationToken ct) => Call("cad_sessions", null, null, new { }, null, ct);
    [McpServerTool(Name = "cad_context", ReadOnly = true), Description("Read the assigned DWG identity, revision, INSUNITS, current layout, disk state and capabilities. Inactive DWGs remain readable; editor view/selection/UCS are explicitly unavailable without activating them. INSUNITS alone does not establish physical project units.")]
    public static Task<CallToolResult> Context(string session_id, CancellationToken ct, string? document_id=null) => Call("cad_context", session_id, document_id, new { }, null, ct);
    [McpServerTool(Name = "cad_catalog", ReadOnly = true), Description("Read layers, local blocks and attributes, layouts, text/dimension styles and linetypes at the current revision. Use these names before inserting or editing objects.")]
    public static Task<CallToolResult> Catalog(string session_id, string document_id, long expected_revision, CancellationToken ct) => Call("cad_catalog", session_id, document_id, new { }, expected_revision, ct);
    [McpServerTool(Name = "cad_vertical_catalog", ReadOnly = true), Description("Read the optional Civil 3D and Map 3D product APIs in the running CAD process: bounded lists of surfaces, alignments and pipe networks; Map coordinate systems and object-data table names. Reports unavailable explicitly if a vertical API is not loaded. No vendor DLL is bundled.")]
    public static Task<CallToolResult> VerticalCatalog(string session_id, string document_id, long expected_revision, CancellationToken ct) =>
        Call("cad_vertical_catalog", session_id, document_id, new { }, expected_revision, ct);
    [McpServerTool(Name = "cad_vertical_get", ReadOnly = true), Description("Inspect a Civil 3D surface/alignment/profile/network or Map 3D object-data-bearing entity by hexadecimal handle. For Civil surfaces, optional sample_points_json is 1..100 [x,y] WCS pairs and returns elevations or per-point errors. Bounded child collections and object-data values are included when the relevant product API is loaded.")]
    public static Task<CallToolResult> VerticalGet(string session_id, string document_id, long expected_revision, string handle, CancellationToken ct, string? sample_points_json = null) =>
        Call("cad_vertical_get", session_id, document_id, new { handle, sample_points_json }, expected_revision, ct);
    [McpServerTool(Name = "cad_table_get", ReadOnly = true), Description("Read a native AutoCAD Table with A1 addresses, values, formulas, field codes, merged ranges and sizes. Zero-based first_row/first_column; at most 500 cells per page. Read values before constructing linked formulas.")]
    public static Task<CallToolResult> TableGet(string session_id, string document_id, long expected_revision, string handle, CancellationToken ct,
        int first_row = 0, int first_column = 0, int row_count = 20, int column_count = 20) =>
        Call("cad_table_get", session_id, document_id, new { handle, first_row, first_column, row_count, column_count }, expected_revision, ct);
    [McpServerTool(Name = "cad_spds_help", ReadOnly = true), Description("Read native Table/formula and SPDS drafting contract, verified form dimensions and KJ/KM working templates. All output uses standard editable AutoCAD objects through C#, without SPDS GraphiCS dependencies. Read before drafting structural sheets.")]
    public static CallToolResult SpdsHelp() => new() { Content = [new TextContentBlock { Text = HelpContract("spds-help.json", new()
        { ["operations"] = JsonSerializer.SerializeToNode(DraftingPlan.Fields, Wire.Json), ["templates"] = JsonSerializer.SerializeToNode(SpdsTemplates.Tables, Wire.Json) }) }] };
    [McpServerTool(Name = "cad_edit_help", ReadOnly = true), Description("Read the native edit operation contract and examples before using cad_edit. Coordinates are WCS drawing units; angles are degrees.")]
    public static CallToolResult EditHelp() => new() { Content = [new TextContentBlock { Text = HelpContract("edit-help.json", new()
        { ["operations"] = JsonSerializer.SerializeToNode(EditPlan.Fields, Wire.Json) }) }] };
    /// <summary>Rules for any MCP client, sent in the initialize response; the in-AutoCAD chat has its own fuller prompt.</summary>
    public static string ServerInstructions { get; } = LoadText("server-instructions.md");
    private static string LoadText(string resource)
    {
        using var stream = typeof(CadTools).Assembly.GetManifestResourceStream("CadMcp.Host.Resources." + resource)
            ?? throw new InvalidOperationException("Missing resource " + resource);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
    }
    /// <summary>Static contract text lives in Resources/*.json; operation lists come from the validating code.</summary>
    private static string HelpContract(string resource, JsonObject generated)
    {
        using var stream = typeof(CadTools).Assembly.GetManifestResourceStream("CadMcp.Host.Resources." + resource)
            ?? throw new InvalidOperationException("Missing help resource " + resource);
        var contract = JsonNode.Parse(stream)!.AsObject();
        foreach (var (key, value) in contract.ToArray()) { contract.Remove(key); generated[key] = value; }
        return generated.ToJsonString(Wire.Json);
    }
    [McpServerTool(Name = "cad_edit", ReadOnly = false, Destructive = true, Idempotent = false), Description("Create/edit DWG entities in one native C# transaction, immediately as requested. Read cad_edit_help first. Includes polygon meshes, 3D polylines and splines, solid primitives/extrude/sweep/revolve/Boolean operations, blocks, layouts, dimensions, transforms, text replacement, offset, explode, join, arrays, fillet/chamfer, polyline corner rounding, trim/extend, text translation and fitting, multileaders, fields, layer merging, XData and XRecords. Supply a unique operation_id for status/replay. Returns handles and database readback. Optional preview_hash (from cad_edit_preview) runs only that exact plan. Never automatically retry an unknown mutation.")]
    public static Task<CallToolResult> Edit(string session_id, string document_id, long expected_revision, string operation_id, string operations_json, CancellationToken ct, string? expectations_json = null, string? preview_hash = null) =>
        Call("cad_edit", session_id, document_id, new { operation_id, operations_json, expectations_json, preview_hash }, expected_revision, ct);
    [McpServerTool(Name = "cad_edit_preview", ReadOnly = true), Description("Dry run of a cad_edit plan: the same native transaction runs, results are read back, checked against expectations_json and reviewed, then rolled back, so the drawing is unchanged (handles are provisional). Use it before bulk or risky edits such as text_replace, layer_merge, explode or trim. Returns plan_hash; pass it as preview_hash with the same plan to cad_edit. Layout, viewport, image, external reference, block import and Civil/Map operations cannot be previewed.")]
    public static Task<CallToolResult> EditPreview(string session_id, string document_id, string operations_json, CancellationToken ct, string? expectations_json = null) =>
        Call("cad_edit_preview", session_id, document_id, new { operations_json, expectations_json }, null, ct);
    [McpServerTool(Name = "cad_export", ReadOnly = false, Destructive = false, Idempotent = false), Description("Export the current DWG through native C# AutoCAD APIs. format pdf plots one named layout (or current layout) through DWG To PDF.pc3; optional media_name selects a paper size. format dxf exports the drawing database. path must be an absolute path ending in .pdf or .dxf; existing files are never overwritten. Returns output size and SHA-256 after checking the file. Supply a unique operation_id and inspect cad_operation_status after an ambiguous response.")]
    public static Task<CallToolResult> Export(string session_id, string document_id, long expected_revision, string operation_id, string format, string path, CancellationToken ct,
        string? layout = null, string? media_name = null) =>
        Call("cad_export", session_id, document_id, new { operation_id, format, path, layout, media_name }, expected_revision, ct);
    [McpServerTool(Name = "cad_publish", ReadOnly = false, Destructive = false, Idempotent = false), Description("Plot 1..100 named paper layouts into separate PDFs using native AutoCAD C#. output_folder must already exist and contain no conflicting output or CAD MCP manifest. layouts_json is a JSON array of layout names in release order. Writes JSON and CSV file registers with sizes and SHA-256; on a partial failure, the manifests record the PDFs already produced. Supply a unique operation_id and inspect cad_operation_status after an ambiguous response.")]
    public static Task<CallToolResult> Publish(string session_id, string document_id, long expected_revision, string operation_id, string output_folder, string layouts_json, CancellationToken ct) =>
        Call("cad_publish", session_id, document_id, new { operation_id, output_folder, layouts_json }, expected_revision, ct);
    [McpServerTool(Name = "cad_lisp", ReadOnly = false, Destructive = true, Idempotent = false), Description("Last-resort fallback only for a specific AutoCAD/vendor operation absent from cad_edit_help and not expressible with native C# composition. Native C# includes meshes and advanced solid modeling. Executes code inside progn; supply all command-s arguments, no interactive prompts; prefer -COMMAND line forms. Use global command and option names with an underscore prefix (\"_.-LAYER\", \"_C\") so the script also works in localized AutoCAD. Running object snaps are suspended and FILEDIA/CMDDIA are 0 while it runs. The user's policy may require confirmation in AutoCAD: the response then says waiting_for_user_confirmation_in_AutoCAD; tell the user and poll cad_operation_status. LISP_DENIED/LISP_DISABLED mean the user refused: do not work around it. Opening, closing or switching drawings is rejected. Returns QUEUED: poll cad_operation_status and verify resulting geometry or files. Errors can leave partial changes. Never retry with a new id after an uncertain result.")]
    public static Task<CallToolResult> Lisp(string session_id, string document_id, long expected_revision, string operation_id, string code, CancellationToken ct) =>
        Call("cad_lisp", session_id, document_id, new { operation_id, code }, expected_revision, ct);
    [McpServerTool(Name = "cad_operation_status", ReadOnly = true), Description("Read a durable operation receipt immediately even while AutoCAD is busy: queued/running/completed/failed/unknown/not_found. Returns the original exact request for safe replay, never create a fresh operation_id after timeout. Archived receipts remain readable after worker restart but are historical and cannot confirm current geometry or disk save.")]
    public static Task<CallToolResult> OperationStatus(string session_id, string document_id, string operation_id, CancellationToken ct) =>
        Call("cad_operation_status", session_id, document_id, new { operation_id }, null, ct);
    [McpServerTool(Name = "cad_operation_list", ReadOnly = true), Description("List up to 100 recent operations for a drawing, including state, acceptance checks, changed-entity counts and disk-save evidence. since is an optional ISO-8601 time. Receipts are accessible while CAD is busy; do not mistake them for fresh geometry.")]
    public static Task<CallToolResult> OperationList(string session_id, string document_id, CancellationToken ct, string? since = null, int limit = 50) =>
        Call("cad_operation_list", session_id, document_id, new { since, limit }, null, ct);
    [McpServerTool(Name = "cad_verify", ReadOnly = true), Description("Read actual current entities again after editing. Supply handles_json (1..500 hexadecimal strings), or a completed native edit operation_id. expectations_json: {units,entity_count,checks:[{handle or target,property,expected,tolerance}]}. Properties: length/radius/area/volume/measurement/layer/type/closed/vertex_count/face_count/position/center/start/end/bounds.min/bounds.max/bounds.size; distance uses first/second:{handle or target,point}. targets refer to original edit ids. Returns passed/failed/unverified checks, aggregate bounds and current DBMOD/file save state. Geometry readback does not verify visual fidelity.")]
    public static Task<CallToolResult> Verify(string session_id, string document_id, long expected_revision, CancellationToken ct,
        string? handles_json = null, string? operation_id = null, string? expectations_json = null) =>
        Call("cad_verify", session_id, document_id, new { handles_json, operation_id, expectations_json }, expected_revision, ct);
    [McpServerTool(Name = "cad_snapshot", ReadOnly = true), Description("Materialize a bounded snapshot of current-space top-level entities. Reports unsupported/custom objects and truncation; does not expand nested blocks.")]
    public static Task<CallToolResult> Snapshot(string session_id, string document_id, long expected_revision, CancellationToken ct, int limit = 2000) =>
        Call("cad_snapshot", session_id, document_id, new { limit }, expected_revision, ct);
    [McpServerTool(Name = "cad_entity_get", ReadOnly = true), Description("Read one top-level entity from any model/paper layout by hexadecimal handle without switching layouts. Dimensions include native formatted text, effective style and measurement factor; viewports include DCS/WCS centers and conservative model search bounds. A stale expected_revision does not block reading. Nested instances use cad_search with expand_blocks; custom object geometry may be partial.")]
    public static Task<CallToolResult> Entity(string session_id, string document_id, long expected_revision, string handle, CancellationToken ct) =>
        Call("cad_entity_get", session_id, document_id, new { handle }, expected_revision, ct);
    [McpServerTool(Name = "cad_query", ReadOnly = true), Description("Filter an immutable materialized snapshot by layer/type/text. Cursor is a numeric offset within the snapshot. Returns captured_revision and historical=true after DWG changes; do not treat historical geometry as current.")]
    public static Task<CallToolResult> Query(string session_id, string document_id, long expected_revision, string snapshot_id, CancellationToken ct,
        string? layer = null, string? type = null, string? text = null, int offset = 0, int limit = 100) =>
        Call("cad_query", session_id, document_id, new { snapshot_id, layer, type, text, offset, limit }, expected_revision, ct);
    [McpServerTool(Name = "cad_search", ReadOnly = true), Description("Search DWG entities beyond snapshot limits. options_json: scope current/model/layout/layouts/all/selection/view/viewport; scope layout requires exact layout_name; scope viewport requires viewport_handle and reads its model-space XY envelope, honoring frozen layers. Optional layer/type/text/handle filters, WCS bounds:{min:[x,y,z],max:[x,y,z]}, offset, limit 1..500, expand_blocks, max_depth 0..12, details. Returns layout_name, space_handle, paths, root handles, WCS geometry and dimension formatting. Inactive DWGs support database scopes; view/selection require the active editor. A stale revision does not block reading. View/viewport envelopes are conservative, not exact clipping/visibility; unsupported viewport projections report an explicit error.")]
    public static Task<CallToolResult> Search(string session_id, string document_id, long expected_revision, string options_json, CancellationToken ct) =>
        Call("cad_search", session_id, document_id, new { options_json }, expected_revision, ct);
    [McpServerTool(Name = "cad_result_get", ReadOnly = true), Description("Read a bounded JSON text page of an archived large CAD result. The captured revision is historical; do not use old geometry as current facts. Supply archive_id, offset and limit 1..32000 characters; concatenate text pages to reconstruct the JSON.")]
    public static Task<CallToolResult> Result(string session_id, string document_id, long expected_revision, string archive_id, CancellationToken ct, int offset = 0, int limit = 16000) =>
        Call("cad_result_get", session_id, document_id, new { archive_id, offset, limit }, expected_revision, ct);
    [McpServerTool(Name = "cad_focus", ReadOnly = false), Description("Select a top-level entity in the active document without modifying DWG geometry. Changes implied selection only.")]
    public static Task<CallToolResult> Focus(string session_id, string document_id, long expected_revision, string handle, CancellationToken ct) =>
        Call("cad_focus", session_id, document_id, new { handle }, expected_revision, ct);
    [McpServerTool(Name = "cad_render", ReadOnly = true), Description("Return a real offscreen AutoCAD preview, local_image_path, image_id and pixel dimensions. Optional layout_name selects model or an exact sheet; handles_json frames 1..500 top-level entities from one space. bounds_json:{min:[x,y,z],max:[x,y,z]} frames an explicit region. view_name current/front/back/left/right/top/isometric or view_direction_json:[x,y,z] chooses an orthographic model view; paper sheets use current/top. The live camera is unchanged. Pixel origin is top-left. Use cad_image_register with known image points for calibrated image-to-WCS mapping; do not infer preview cropping from viewport metadata. " +
        "Images cost context: prefer text tools first, crop with handles_json or bounds_json, keep width/height small (512x384 is often enough); attach=false saves the PNG and returns only its path and metadata (image_cost.approx_tokens tells the price).")]
    public static async Task<CallToolResult> Render(string session_id, string document_id, long expected_revision, CancellationToken ct, int width = 1024, int height = 768,
        string? handles_json = null, string view_name = "current", string? view_direction_json = null, string? layout_name = null, string? bounds_json = null, bool attach = true)
    {
        var response = await ScopedResponse("cad_render",session_id,document_id,new {width,height,handles_json,view_name,view_direction_json,layout_name,bounds_json},expected_revision,ct);
        if (response.Error is not null) return Result(response);
        Response Failed(string code, string message) => response with { Status = "failed", Data = null, Error = new(code, message) };
        if (response.Data is not JsonElement { ValueKind: JsonValueKind.Object } data) return Result(Failed("INVALID_RENDER_RESPONSE", "AutoCAD returned no preview data"));
        byte[] image;
        try { image = Convert.FromBase64String(data.Text("image_base64") ?? throw new FormatException("image_base64 is missing")); }
        catch (Exception error) when (error is FormatException or InvalidOperationException)
        { return Result(Failed("INVALID_RENDER_IMAGE", "AutoCAD returned an unreadable preview: " + error.Message)); }
        var metadata = data.EnumerateObject().Where(p => p.Name != "image_base64").ToDictionary(p => p.Name, p => p.Value.Clone());
        if (data.Text("image_id") is { } imageId && Guid.TryParseExact(imageId, "N", out _))
        {
            try { metadata["local_image_path"] = Wire.Element(PreviewFiles.Save(imageId, image)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { metadata["preview_file_warning"] = Wire.Element(error.Message); }
        }
        // Rough vision cost of an image: about one token per 750 pixels.
        metadata["image_cost"] = Wire.Element(new { attached = attach || !metadata.ContainsKey("local_image_path"), approx_tokens = (int)Math.Ceiling(width * (double)height / 750) });
        var text = new TextContentBlock { Text = JsonSerializer.Serialize(response with { Data = metadata }, Wire.Json) };
        // Without a saved file the image is the only copy, so it is attached anyway.
        if (!attach && metadata.ContainsKey("local_image_path")) return new() { Content = [text] };
        return new() { Content = [text, ImageContentBlock.FromBytes(image, "image/png")] };
    }
    [McpServerTool(Name = "cad_takeoff", ReadOnly = true), Description("Quantity takeoff of top-level entities: curve lengths and closed-area totals per layer (with per-type breakdown), block references counted by effective (dynamic) name, and optionally an attribute table per block reference. scope current/model/layout(+layout_name)/all; layers_json is an array of layer names or patterns (* ? # , ~); include is a comma list of lengths, areas, blocks, attributes (default lengths,areas,blocks); format csv adds semicolon-separated tables. Values are in drawing units; geometry inside blocks is not exploded.")]
    public static Task<CallToolResult> Takeoff(string session_id, string document_id, CancellationToken ct, string scope = "current", string? layout_name = null,
        string? layers_json = null, string? include = null, int max_rows = 1000, string format = "json") =>
        Call("cad_takeoff", session_id, document_id, new { scope, layout_name, layers_json, include, max_rows, format }, null, ct);
    [McpServerTool(Name = "cad_text_units", ReadOnly = true), Description("Text of the drawing as translation units, for translating or rewriting it: TEXT, MTEXT, block attributes, multileaders and table cells, with TEXT lines stacked as one paragraph joined into one unit. Each unit has a key, the displayed text, its frame (width and height) and fit data. scope current/model/layout(+layout_name)/all or handles_json; layers_json patterns (* ? # , ~); include is a comma list of text, mtext, attributes, tables, mleaders, block_definitions (default all but block_definitions); group_lines false keeps every TEXT line separate; offset/limit page the units. Apply translations with cad_edit text_translate, giving each unit's text back as source.")]
    public static Task<CallToolResult> TextUnitsTool(string session_id, string document_id, CancellationToken ct, string scope = "current", string? layout_name = null,
        string? layers_json = null, string? handles_json = null, string? include = null, bool group_lines = true, int offset = 0, int limit = 500) =>
        Call("cad_text_units", session_id, document_id, new { scope, layout_name, layers_json, handles_json, include, group_lines, offset, limit }, null, ct);
    [McpServerTool(Name = "cad_properties", ReadOnly = true), Description("The Properties palette (OPM) of 1..20 objects by handle (handles_json, any object of the drawing): every property the palette shows, by category, including properties that Civil 3D, Map 3D, SPDS and other object enablers add, with values as this AutoCAD shows them (names in its language). Use it for objects cad_entity_get reads only partly: custom and vertical objects and proxies. max_properties caps each object (default 300).")]
    public static Task<CallToolResult> Properties(string session_id, string document_id, string handles_json, CancellationToken ct, int max_properties = 300) =>
        Call("cad_properties", session_id, document_id, new { handles_json, max_properties }, null, ct);
    [McpServerTool(Name = "cad_proxies", ReadOnly = true), Description("Proxy objects of the drawing, left by applications that are not loaded: proxy entities and proxy objects grouped by original class, DXF name and application, with counts, owners (model, layouts, blocks, dictionaries), kind of stored graphics, the operations their proxy flags allow, and sample handles. With handles_json (1..20 handles), details of those proxies with their stored graphics exploded in memory: part types, texts and extents. Nothing in the drawing changes.")]
    public static Task<CallToolResult> Proxies(string session_id, string document_id, CancellationToken ct, string? handles_json = null, int max_samples = 20) =>
        Call("cad_proxies", session_id, document_id, new { handles_json, max_samples }, null, ct);
    [McpServerTool(Name = "cad_outline", ReadOnly = true), Description("Deterministic summary of the drawing in one call: units, model entity types and extents, sheets with paper size, plot device, viewports and title-block attributes, layers by use, blocks by references, external references, text and dimension styles, and a sample of texts. Read it first to orient yourself instead of paging through entities or rendering images.")]
    public static Task<CallToolResult> Outline(string session_id, string document_id, CancellationToken ct, int text_sample = 40) =>
        Call("cad_outline", session_id, document_id, new { text_sample }, null, ct);
    [McpServerTool(Name = "cad_file_inspect", ReadOnly = true), Description("Outline of a DWG on disk that is not open in AutoCAD (absolute .dwg path, up to 512 MB), read into a separate database without opening a drawing tab: units, sheets, layers, blocks, external references, styles and a text sample. Nothing is modified. Use it to find the right file or block library before opening or importing from it.")]
    public static Task<CallToolResult> FileInspect(string session_id, string document_id, string path, CancellationToken ct) =>
        Call("cad_file_inspect", session_id, document_id, new { path }, null, ct);
    [McpServerTool(Name = "cad_changes", ReadOnly = true), Description("Objects changed since a revision (from cad_context or a previous call) with their net effect added/modified/erased/restored, type, layer and who changed them: a CAD MCP operation_id, table_recalculation or user (the person or another program, including UNDO). source all/user/cad_mcp; include_non_entities adds layers, styles and dictionaries. Use it at the start of a turn to see what the user edited by hand, and after cad_lisp to see its exact effect. complete=false means older changes were dropped from the in-session log.")]
    public static Task<CallToolResult> Changes(string session_id, string document_id, long since_revision, CancellationToken ct, int limit = 500, string source = "all", bool include_non_entities = false) =>
        Call("cad_changes", session_id, document_id, new { since_revision, limit, source, include_non_entities }, null, ct);
    private sealed record CalibratedReference(ReferenceImage Image, PhotoReference Fit);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CalibratedReference> References = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> ReferenceOrder = new();
    [McpServerTool(Name = "cad_reference_calibrate", ReadOnly = true), Description("Calibrate a user reference image by its actual pixel dimensions and SHA256. model similarity needs 2..20 known pixel/world anchors for an orthographic image; projective needs 4..20 coplanar anchors for perspective. control_points_json:[{pixel:[x,y],world:[x,y,z]}]. Coordinates follow displayed EXIF orientation and top-left origin. Returns planar transform and residuals, not hidden 3D geometry. One known segment cannot calibrate arbitrary perspective.")]
    public static CallToolResult ReferenceCalibrate(string image_path, string control_points_json, string model = "similarity", bool invert_pixel_y = true)
    {
        var image = ReferenceImage.Read(image_path); using var controls = JsonDocument.Parse(control_points_json);
        var fit = PhotoReference.Fit(controls.RootElement, image.Width, image.Height, model, invert_pixel_y);
        string id=Guid.NewGuid().ToString("N"); References[id]=new(image,fit); ReferenceOrder.Enqueue(id);
        while(References.Count>32&&ReferenceOrder.TryDequeue(out var old))References.TryRemove(old,out _);
        return new() { Content=[new TextContentBlock { Text=JsonSerializer.Serialize(new { reference_id=id, image=image.Metadata,
            model, invert_pixel_y, matrix=fit.Matrix, world_z=fit.Z, rms_error=fit.RmsError, max_error=fit.MaxError,
            scale_evidence="user_supplied_known_world_anchors", lifetime="MCP_host_session",
            limitations=new[]{"calibration_applies_to_one_known_plane","hidden_geometry_and_full_3D_scale_are_not_inferred"} },Wire.Json) }] };
    }
    [McpServerTool(Name = "cad_reference_point", ReadOnly = true), Description("Map 1..100 [x,y] pairs through a photo calibration. direction pixel_to_world or world_to_pixel. Verifies source image hash before using the transform; returns world XYZ on the calibrated plane.")]
    public static CallToolResult ReferencePoint(string reference_id, string points_json, string direction = "pixel_to_world")
    {
        if(!References.TryGetValue(reference_id,out var reference))throw new CadFault("REFERENCE_NOT_FOUND","Recalibrate this reference in the current MCP host session");
        if(ReferenceImage.Read(reference.Image.Path).Sha256!=reference.Image.Sha256)throw new CadFault("REFERENCE_CHANGED","Reference image changed; recalibrate before mapping");
        if(direction is not("pixel_to_world" or "world_to_pixel"))throw new CadFault("INVALID_DIRECTION",direction);
        using var points=JsonDocument.Parse(points_json);
        if(points.RootElement.ValueKind!=JsonValueKind.Array||points.RootElement.GetArrayLength() is <1 or >100)throw new CadFault("INVALID_POINTS","Supply 1..100 pairs");
        var mapped=points.RootElement.EnumerateArray().Select(p=> { if(p.ValueKind!=JsonValueKind.Array||p.GetArrayLength()!=2)throw new CadFault("INVALID_POINTS","Supply [x,y]");
            var v=EditPlan.Point(p); return direction=="pixel_to_world"?reference.Fit.PixelToWorld(v[0],v[1]):reference.Fit.WorldToPixel(v[0],v[1]); }).ToArray();
        return new(){Content=[new TextContentBlock{Text=JsonSerializer.Serialize(new{reference_id,direction,coordinates=mapped,rms_error=reference.Fit.RmsError},Wire.Json)}]};
    }
    [McpServerTool(Name = "cad_reference_compare", ReadOnly = true), Description("Compare reference and real rendered silhouettes in comparable views. Supply actual reference_image_path and rendered_image_path (cad_render returns local_image_path), plus two polygons of normalized [0..1,0..1] coordinates traced from those images. Computes approximate silhouette IoU and sampled boundary deviation. This checks supplied contours, not automatic vision or hidden 3D/detail fidelity. Review both images and major features before claiming a match.")]
    public static CallToolResult ReferenceCompare(string reference_image_path, string rendered_image_path, string reference_contour_json,
        string rendered_contour_json, double minimum_iou = .85, double maximum_deviation = .05)
    {
        var reference=ReferenceImage.Read(reference_image_path); var rendered=ReferenceImage.Read(rendered_image_path);
        using var a=JsonDocument.Parse(reference_contour_json); using var b=JsonDocument.Parse(rendered_contour_json);
        var result=ReferenceComparison.Compare(a.RootElement,b.RootElement,minimum_iou,maximum_deviation);
        return new(){Content=[new TextContentBlock{Text=JsonSerializer.Serialize(new{reference=reference.Metadata,rendered=rendered.Metadata,comparison=result},Wire.Json)}]};
    }
    [McpServerTool(Name = "cad_image_register", ReadOnly = true), Description("Calibrate one cad_render image to the DWG WCS XY plane using 3..20 point pairs. control_points_json: [{\"pixel\":[x,y],\"world\":[x,y,z]},...]. Pixels have top-left origin; WCS Z must be common. Returns 2D affine transform, corners and RMS/max residual. Registration is kept for up to 8 images in the current worker session and does not modify the DWG.")]
    public static Task<CallToolResult> ImageRegister(string session_id, string document_id, long expected_revision, string image_id, string control_points_json, CancellationToken ct) =>
        Call("cad_image_register", session_id, document_id, new { image_id, control_points_json }, expected_revision, ct);
    [McpServerTool(Name = "cad_image_point", ReadOnly = true), Description("Convert 1..100 coordinates through a previously calibrated cad_render image. points_json is an array of [x,y] pairs; direction is pixel_to_world (default) or world_to_pixel. Returns captured_revision and historical flag so older images are not mistaken for the current drawing.")]
    public static Task<CallToolResult> ImagePoint(string session_id, string document_id, long expected_revision, string image_id, string points_json, CancellationToken ct, string direction = "pixel_to_world") =>
        Call("cad_image_point", session_id, document_id, new { image_id, points_json, direction }, expected_revision, ct);
}
