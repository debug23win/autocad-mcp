# Arbitrary C# execution

`cad_csharp` compiles C# 12 and executes it in the pinned document. Prefer built-in `cad_edit` operations for ordinary geometry. The compiler ships with the MCP host; neither the .NET SDK nor Visual Studio is required. References come from framework/AutoCAD/loaded managed assemblies in the current CAD process; compilation happens outside AutoCAD.

Since 0.11.6, reference candidates are checked for CLR metadata: native DLLs without .NET metadata and corrupt/inaccessible files are skipped. They no longer cause the generic "PE image does not have metadata" failure. If no managed references remain, the tool stops before submitting a CAD operation.

Parameters: `session_id`, `document_id`, fresh `expected_revision`, unique `operation_id`, and complete `code`:

```csharp
using System.Threading;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
public static class CadMcpScript
{
    public static object Run(Database database, Transaction transaction,
                             CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var model = (BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
        using var line = new Line(new Point3d(0,0,0), new Point3d(100,0,0));
        model.AppendEntity(line);
        transaction.AddNewlyCreatedDBObject(line, true);
        return new { handle = line.Handle.ToString() };
    }
}
```

Use the supplied database/transaction and return DTOs/handles, never live DBObjects. Do not call CAD APIs on background threads or present a queued `SendStringToExecute` command as completed. Execution is synchronous; cancellation is cooperative. Check the token in long loops: the native kernel cannot be safely interrupted forcibly.

A DWG checkpoint is required before loading the assembly; checkpoint failure prevents execution. Responses/receipts include the checkpoint, source SHA-256, and changed handles. Compilation failure does not execute code. Resolve uncertain outcomes using the original operation_id, without retrying under a new ID. Read results back with cad_entity_get/search/verify.

Code runs with full process trust. Failure rolls back the supplied host transaction, but cannot guarantee rollback of independent commits, files, external APIs, or event handlers. Checkpoints preserve the DWG database, not every external file. Never close/save unrelated documents. Read-only assistants cannot call cad_csharp.

The primary Codex session receives workspace-write in the chosen working directory and network access; assistant profiles remain read-only. Directory permissions allow source-file storage; cad_csharp provides execution inside CAD.

## Replay and complete verification

The host checks the original operation status before requesting references or compiling. An existing receipt is returned only after matching source, source hash, session, document and original expected revision; changed requests return `OPERATION_ID_CONFLICT`. Pending/unknown existing operations are not compiled or submitted again. Historical receipts retain their provenance and cannot confirm the current drawing or disk save.

Compilation uses a source-derived assembly name, ordered references and deterministic output. The journal fingerprints the public source request and scope, excluding generated `assembly_base64`; the exact received assembly remains in the original request. Newly accepted source uses references from the running worker. Replay returns the old receipt even if those references are no longer available.

A serializable user return value over 256 KiB is rejected before the supplied transaction commits. This limit does not undo external full-trust effects. Changed handles are returned in full only when the change journal covers the operation and the list fits 192 KiB; otherwise `changed_handles_omitted:true` explicitly forbids whole-operation verification. `cad_verify(operation_id=...)` rejects incomplete lists and more than 500 targets with `VERIFY_TOO_LARGE`. Read the complete change evidence and verify explicit groups of up to 500 handles; account for all groups before claiming task-wide verification.
