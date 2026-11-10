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
