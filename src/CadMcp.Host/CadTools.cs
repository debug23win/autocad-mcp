using System.ComponentModel;
using System.Text.Json;
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
        if(CadAccess.ReadOnly&&(MutationRecovery.IsMutation(operation)||operation is "cad_focus" or "cad_cancel"))throw new CadFault("HELPER_READ_ONLY","Review helpers cannot mutate drawings, focus, publish or cancel work");
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
    private static async Task<CallToolResult> Call(string operation,string? session,string? document,object data,long? revision,CancellationToken ct)
    {
        var response=await ScopedResponse(operation,session,document,data,revision,ct);
        return new() { IsError=response.Error is not null,Content=[new TextContentBlock {Text=JsonSerializer.Serialize(response,Wire.Json)}] };
    }
    [McpServerTool(Name = "cad_vertical_capabilities",ReadOnly=true),Description("Read actually loaded Civil/Map API versions and exact supported method signatures before editing vertical objects. Missing API is explicitly unavailable.")]
    public static Task<CallToolResult> VerticalCapabilities(string session_id,string document_id,CancellationToken ct)=>Call("cad_vertical_capabilities",session_id,document_id,new{},null,ct);
    [McpServerTool(Name = "cad_documents", ReadOnly = true), Description("List open DWGs with document ids, project keys and active status. Pin every task to a specific document_id; concurrent chats are isolated.")]
    public static Task<CallToolResult> Documents(string session_id,CancellationToken ct)=>Call("cad_documents",session_id,null,new{},null,ct);
    [McpServerTool(Name = "cad_cancel", ReadOnly = false), Description("Cancel this chat owner queued/running operations in a drawing. Native calls finish at a safe boundary; a running Lisp command needs Esc. Does not cancel another chat work.")]
    public static Task<CallToolResult> Cancel(string session_id,string document_id,CancellationToken ct,string? operation_id=null)=>Call("cad_cancel",session_id,document_id,new{operation_id},null,ct);
    [McpServerTool(Name = "cad_runtime_status", ReadOnly = true), Description("Read worker operation phases and cancellation status without waiting for the CAD UI thread.")]
    public static Task<CallToolResult> RuntimeStatus(string session_id,string document_id,CancellationToken ct)=>Call("cad_runtime_status",session_id,document_id,new{},null,ct);
    [McpServerTool(Name = "cad_diagnostics", ReadOnly = true), Description("Read durable last-operation phase, plugin version and DWG checkpoint locations, including previous worker sessions. A nonterminal old phase means unknown outcome; never automatically retry it.")]
    public static Task<CallToolResult> Diagnostics(string session_id,CancellationToken ct)=>Call("cad_diagnostics",session_id,null,new{},null,ct);
    [McpServerTool(Name = "cad_review", ReadOnly = true), Description("Check up to 250 selected entities (or current space): actual solid interferences, annotation extents, native table text fit and configured paper boundaries. Review warnings and intentional joints; inspect the actual rendered view too.")]
    public static Task<CallToolResult> Review(string session_id,string document_id,CancellationToken ct,string? handles_json=null)=>Call("cad_review",session_id,document_id,new{handles_json},null,ct);
    [McpServerTool(Name = "cad_solid_get", ReadOnly = true), Description("Read native Solid3d volume, edge and face topology ids at the current revision. Refresh after each fillet/chamfer/shell; ids are not stable through topology changes.")]
    public static Task<CallToolResult> SolidGet(string session_id,string document_id,string handle,CancellationToken ct)=>Call("cad_solid_get",session_id,document_id,new{handle},null,ct);
    [McpServerTool(Name = "cad_assembly_get", ReadOnly = true), Description("Read parametric native structural assembly recipe, mark, material, volume and mass scope. Geometry is not a structural capacity calculation.")]
    public static Task<CallToolResult> AssemblyGet(string session_id,string document_id,string handle,CancellationToken ct)=>Call("cad_assembly_get",session_id,document_id,new{handle},null,ct);
    [McpServerTool(Name = "cad_table_dependencies", ReadOnly = true), Description("Read persisted stable cell identities and table formula dependency graph. CAD MCP edits automatically recalculate linked tables; deletion of referenced rows is rejected.")]
    public static Task<CallToolResult> TableDependencies(string session_id,string document_id,CancellationToken ct)=>Call("cad_table_dependencies",session_id,document_id,new{},null,ct);
    [McpServerTool(Name = "cad_release_check", ReadOnly = true), Description("Check a sheet set before publishing: missing external references/fonts, title/designation and sheet numbers, plot device, table fit and paper boundaries. layouts_json is an array of exact layout names.")]
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
    public static CallToolResult SpdsHelp() => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new {
        operations = DraftingPlan.Fields, templates = SpdsTemplates.Tables,
        tables = "table_create: position, rows/columns, cells:[{cell:'A1',value:12}]. Formula starts with =. Local references use =A1*B1 or =SUM(A1:A3). Linked references use ={{qty}}*2 with references:[{name:'qty',table_handle:'HEX',cell:'B2'}] (or earlier table_target alias). These become native DWG AcExpr fields, not copied numbers. Linked tables created by CAD MCP recalculate automatically after source changes; verify values with cad_table_get. Avoid cycles. Merges use zero-based [first_row,first_column,last_row,last_column]. table_rows/columns insert/delete with index/count; linked-cell identities survive CAD MCP row/column insertion; delete referenced cells only after removing dependents.",
        drafting = "spds_table: template, position, data array, optional title/scale. Sheet and table coordinates on layouts are paper millimetres. In model space supply scale converting paper millimetres into drawing units. spds_dimstyle creates a new style with drawing_scale and millimetre measurement_factor inferred from INSUNITS or explicitly supplied; use it in native dimensions. Linear dimensions use ticks, radial/diametric dimensions arrow overrides. spds_axis and spds_level create editable blocks of native primitives; elevation is explicitly metres with 3 decimals. spds_sheet builds an editable attributed form-3 title block and frame on an existing A0..A4 layout. Configure plot media and locked viewports separately before cad_publish.",
        structural_workflow = "KJ: obtain actual dimensions, materials, reinforcement scheme and design data; create concrete geometry, sections, reinforcement, axes/elevations, dimensions, specification and reinforcement/steel schedules. KM: obtain profiles, steel grades, lengths and joint data; create member geometry, assembly/section drawings, marks, dimensions, specification and member/steel schedules. Link quantities/masses between native tables. Use existing C# line/polyline/block/hatch/solid tools. Never invent reinforcement, connections or structural design loads. VerifiedForm=false marks a project template, not a mandatory standard form. Complete structural/normative compliance needs project inputs and review, not just placing a table.",
        standards = new[] { "ГОСТ Р 21.101-2026", "ГОСТ 21.501-2018 (КЖ)", "ГОСТ 21.502-2016 (КМ)" }
    }, Wire.Json) }] };
    [McpServerTool(Name = "cad_edit_help", ReadOnly = true), Description("Read the native edit operation contract and examples before using cad_edit. Coordinates are WCS drawing units; angles are degrees.")]
    public static CallToolResult EditHelp() => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new {
        operations = EditPlan.Fields, coordinates = "WCS drawing units, [x,y] or [x,y,z]", angles = "degrees around WCS Z",
        contract = "operations_json is a JSON array of 1..100 objects. Every object has op. Optional id names an entity for later target references. Existing entities use handle; handle and target are mutually exclusive. Create a missing layer first. Read cad_catalog for block/style names. Native C# supports meshes, spatial curves, 3D solids, extrude, sweep, revolve and solid Boolean operations; use these before cad_lisp. Repeat only the EXACT request with the SAME operation_id; do not change expected_revision when replaying. Read cad_operation_status after an ambiguous response. After success use returned revision for further calls. Locked layers, xref and most specialized objects are not edited through the native path. The whole native batch rolls back on any pre-commit error. Do not repeat unknown mutations automatically.",
        acceptance = new { parameter = "expectations_json", enforcement = "Default enforce=true: failed or unavailable measurements roll back the native transaction before commit. Set enforce=false only for an explicitly diagnostic check. entity_count counts live entities affected by this batch, not the whole drawing.",
            example = new { units = "Millimeters", entity_count = 1, checks = new[] { new { target = "beam", property = "length", expected = 5000, tolerance = 0.01 } } },
            task_contract = "Optional task:string, bounds_size:[x,y,z], bounds_tolerance:number, type_counts:{nativeType:count}, review_views:[current/front/back/left/right/top/isometric], visual_requirements:[strings]. Numeric checks accept either expected or minimum/maximum (inclusive), plus tolerance. Assembly properties: assembly.solid_mass_kg, assembly.solid_volume_m3, assembly.centerline_length, assembly.profile_code. Bounds/counts refer to the supplied verification handles or affected batch; final task verification must include every final object. Visual requirements remain pending until actual renders are reviewed.",
            verification = "Use cad_verify after commit for fresh checks. Geometry acceptance does not prove visual similarity or disk save." },
        notes = new { assembly_profiles = "Read cad_steel_catalog. beam/column parameters:{length,profile:exact_catalog_code}; length uses drawing units, catalog millimetres convert using DWG units. Or supply explicit width,height,web_thickness,flange_thickness, optional root_radius. Do not combine profile with explicit dimensions. Rebar points are the sharp centerline vertices, bend_radius is the centerline radius (default 2*diameter), adjacent legs must accommodate tangency. Screw-pile blade is a native volumetric 48-segment ruled solid; mass includes blade. Existing version-1 assemblies retain their geometry until assembly_update, with legacy mass limitations reported.",
            polyline = "points share WCS Z; bulges: one number per vertex; closed:boolean; width:nonnegative constant segment width",
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
    public static Task<CallToolResult> Edit(string session_id, string document_id, long expected_revision, string operation_id, string operations_json, CancellationToken ct, string? expectations_json = null) =>
        Call("cad_edit", session_id, document_id, new { operation_id, operations_json, expectations_json }, expected_revision, ct);
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
    [McpServerTool(Name = "cad_render", ReadOnly = true), Description("Return a real offscreen AutoCAD preview, local_image_path, image_id and pixel dimensions. Optional layout_name selects model or an exact sheet; handles_json frames 1..500 top-level entities from one space. bounds_json:{min:[x,y,z],max:[x,y,z]} frames an explicit region. view_name current/front/back/left/right/top/isometric or view_direction_json:[x,y,z] chooses an orthographic model view; paper sheets use current/top. The live camera is unchanged. Pixel origin is top-left. Use cad_image_register with known image points for calibrated image-to-WCS mapping; do not infer preview cropping from viewport metadata.")]
    public static async Task<CallToolResult> Render(string session_id, string document_id, long expected_revision, CancellationToken ct, int width = 1024, int height = 768,
        string? handles_json = null, string view_name = "current", string? view_direction_json = null, string? layout_name = null, string? bounds_json = null)
    {
        var response = await ScopedResponse("cad_render",session_id,document_id,new {width,height,handles_json,view_name,view_direction_json,layout_name,bounds_json},expected_revision,ct);
        if (response.Error is not null) return new() { IsError = true, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(response, Wire.Json) }] };
        var data = (JsonElement)response.Data!;
        var metadata = data.EnumerateObject().Where(p => p.Name != "image_base64").ToDictionary(p => p.Name, p => p.Value);
        var imageId = data.Text("image_id");
        if (Guid.TryParseExact(imageId, "N", out _))
        {
            try
            {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CadMcp", "previews");
            Directory.CreateDirectory(folder); string path = Path.Combine(folder, imageId + ".png");
            File.WriteAllBytes(path, Convert.FromBase64String(data.Text("image_base64")!));
            metadata["local_image_path"] = Wire.Element(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { metadata["preview_file_warning"] = Wire.Element(error.Message); }
        }
        return new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(response with { Data = metadata }, Wire.Json) },
            ImageContentBlock.FromBytes(Convert.FromBase64String(data.GetProperty("image_base64").GetString()!), "image/png")] };
    }
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
