# C# preflight

`cad_csharp` compiles against the connected AutoCAD runtime's references, then
checks the entry point and resolved symbols before submitting the assembly to CAD.
Rejected scripts return `CSHARP_PREFLIGHT_FAILED` with source locations. They do
not start a new CAD mutation. Existing operation receipts are reconciled first.

The conservative checks reject direct AutoCAD cloning (including method groups),
explicit CAD transaction lifecycle/Dispose calls, selected save/close/command
APIs, background task/thread APIs, reflection, native interop, dynamic dispatch
and unsafe constructs. Use the supplied database, transaction and cancellation
token synchronously. The adapter retains document locking and transaction commit.
Cloning requires a reviewed native implementation that validates ObjectId
ownership, destination owner and database lifetimes; it is currently blocked in
scripts rather than claimed safe by compilation.

This is a preflight, not a security sandbox or proof against native crashes.
Indirect calls through other libraries and external side effects are not fully
covered. The user must enable the **Allow C#** checkbox; it is off by default and
is independent of imported chat history. Both host and native worker enforce it.

## Process isolation and replacement

The main worker creates a retained checkpoint, stages a private input DWG and
compiled assembly, and starts the matching installed `acad.exe` directly with
shell execution disabled. The child receives a unique request/nonce and never
publishes a broker descriptor, chat or ribbon. It runs the script once, commits
only its own transaction, saves `result.dwg`, reopens it for readback, writes a
bounded final receipt and quits. A launch claim prevents a second execution.

The parent stays responsive and polls the owned Process. A nonzero exit,
timeout, missing receipt, identity mismatch or output hash mismatch fails the
operation, even if a receipt existed before a later crash. PID, signed/hex exit
code, last phase and artifact directory are exposed to the agent. An exit code
alone is not a stack or proof of the root cause. Cancellation/timeout kills only
the owned process, never another AutoCAD or an entire process tree. Files are
retained for reconciliation; no automatic script retry or in-process fallback.

After a clean child exit the CAD UI thread checks the original document identity,
revision, filename, disk hash, idle state and unchanged user permission. It stages
the result on the destination volume, closes the original (whose complete unsaved
state is retained in the checkpoint), atomically replaces the disk file with a
retained backup, and opens it again. Logical document/project identities remain
stable, while revision advances and old change history expires. Unsaved untitled
drawings receive a local retained `applied.dwg` path. The script is not rerun.

If reopening fails, disk rollback and opening the pre-operation checkpoint are
attempted; failures explicitly require manual recovery. If the user edited the
original or revoked permission while the child ran, no replacement starts and
the verified output remains separate. Undo history does not survive reopening;
recovery uses the retained DWGs. This isolates script execution, not all native
snapshot/file-loading APIs in the main process and not arbitrary external effects.
Additional AutoCAD startup/licensing/security dialogs can cause a timeout;
security settings are never relaxed to bypass them.

OneDrive is forbidden for mutable drawings, output destinations and agent working
folders, including known account roots and directory junctions. Reference reading
remains allowed. The default agent workspace is `%LOCALAPPDATA%/CadMcp/workspace`.

Managed process, receipt, replacement and policy tests do not replace a live
AutoCAD validation of startup, crash handling, rollback and document rebinding.
