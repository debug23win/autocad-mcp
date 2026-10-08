namespace CadMcp.Core;

/// <summary>
/// The single list of CAD operation names and how the MCP host and the CAD worker treat them.
/// New operations are not read-only until they are added to <see cref="ReadOnly"/>.
/// </summary>
public static class CadOperations
{
    /// <summary>
    /// Operations that only read drawings, receipts or worker state; cad_edit_preview computes an edit in a
    /// transaction that is always rolled back. Read-only helpers may call only these.
    /// </summary>
    public static readonly IReadOnlySet<string> ReadOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        "cad_sessions", "cad_documents", "cad_context", "cad_runtime_status", "cad_diagnostics",
        "cad_operation_status", "cad_operation_list",
        "cad_catalog", "cad_snapshot", "cad_query", "cad_search", "cad_result_get", "cad_entity_get",
        "cad_table_get", "cad_table_dependencies", "cad_review", "cad_release_check", "cad_solid_get", "cad_assembly_get",
        "cad_verify", "cad_render", "cad_image_register", "cad_image_point", "cad_edit_preview",
        "cad_takeoff", "cad_outline", "cad_file_inspect", "cad_changes", "cad_text_units", "cad_properties", "cad_proxies",
        "cad_vertical_capabilities", "cad_vertical_catalog", "cad_vertical_get"
    };

    /// <summary>
    /// What read-only review helpers may call: the read-only operations except cad_edit_preview, whose rolled-back
    /// transaction still marks the drawing modified and is seen by other add-ins.
    /// </summary>
    public static readonly IReadOnlySet<string> HelperAllowed = ReadOnly.Where(o => o != "cad_edit_preview").ToHashSet(StringComparer.Ordinal);

    /// <summary>Read-only tools answered by the MCP host itself, without a CAD worker.</summary>
    public static readonly IReadOnlyList<string> HostTools =
        ["cad_steel_catalog", "cad_spds_help", "cad_edit_help", "cad_reference_calibrate", "cad_reference_point", "cad_reference_compare"];

    /// <summary>Operations that can change a drawing or write files. They carry an operation_id receipt.</summary>
    public static bool IsMutation(string operation) => operation is "cad_edit" or "cad_lisp" or "cad_export" or "cad_publish";

    /// <summary>
    /// Operations that run only in the active editor document, so the worker activates the target first.
    /// Vertical reads resolve the Civil/Map document of the requested database and do not switch tabs.
    /// </summary>
    public static bool ActivatesDocument(string operation) => IsMutation(operation) || operation == "cad_edit_preview";

    /// <summary>Operations a CAD worker accepts, as advertised by cad_context.</summary>
    public static IReadOnlyList<string> WorkerOperations { get; } = ReadOnly.Where(o => o != "cad_sessions")
        .Concat(["cad_focus", "cad_cancel", "cad_edit", "cad_lisp", "cad_export", "cad_publish"])
        .Order(StringComparer.Ordinal).ToArray();
}
