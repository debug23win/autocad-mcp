# Isolated AutoLISP

Agent `cad_lisp` requests execute once in a separate matching `acad.exe`, on a retained DWG checkpoint. The primary process never sends the agent body to its command line. Existing CADMCPLISP policy and user confirmation still apply; the Allow C# checkbox is independent.

The worker opens `input.dwg`, evaluates the usual `progn`/Undo/system-variable wrapper in document command context, and reports through child-local Lisp callbacks. Saving and reopening `result.dwg` happens on a later application Idle, after the evaluation has finished. The parent requires a clean process exit, a nonce/PID/input-bound receipt and matching saved-output hash/size before applying anything. It rejects old native plugins without the isolation capability.

Success uses the guarded replacement already used by C#: unchanged document revision, original path and disk hash, idle UI, retained checkpoint and backup. The script is never run a second time. Replacement reopens the document and resets its Undo history. If the user edits the original while the child works, verified output remains separate. Failure, missing callbacks, timeout or abnormal exit prevents replacement. Inspect the original operation_id; do not resubmit under another id.

Receipts expose child PID, exit code/hex, last phase and the retained artifact directory. A Windows access violation can therefore be observed as an abnormal child exit without requiring the primary AutoCAD to survive that same exception. A Windows fatal-error dialog may keep the child alive until the deadline; timeout is then reported, not an invented crash diagnosis.

This is process isolation, not a sandbox. Scripts retain filesystem, COM and process access; drawing-specific loaded LSP functions and session variables are not copied from the primary session. Use self-contained code with explicit input paths. Vendor commands require the corresponding product/modules in the child. Shared profile changes and writes to external files are not rolled back. Native checkpoint creation and reopening in the primary process still use AutoCAD code and are not covered by script-process isolation.

Managed tests exercise successful and fatal child exits, receipt integrity, script tampering and prevention of a second launch. Actual AutoCAD/Lisp callbacks, native DWG save/reopen and fatal-error dialogs require live licensed AutoCAD validation.
