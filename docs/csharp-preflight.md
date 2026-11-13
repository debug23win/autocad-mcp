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
Indirect calls through other libraries, implicit disposal, data-dependent CAD
errors and native API defects are not fully covered. The mandatory checkpoint,
operation receipts and independent DWG verification remain necessary. No script
is executed as a test in the user's active drawing. An isolated AutoCAD process
and disposable drawing would be needed for runtime trial execution; this change
does not implement that facility.
