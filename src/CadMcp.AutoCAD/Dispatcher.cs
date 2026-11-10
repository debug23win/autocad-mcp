using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.ApplicationServices;
using CadMcp.Core;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadMcp.AutoCAD;

internal sealed class Dispatcher(Documents documents) : IDisposable
{
    private readonly DocumentWorkQueue<(Request Request, CancellationToken Token, TaskCompletionSource<Response> Source, DateTimeOffset Enqueued)> queue = new();
    private readonly ConcurrentDictionary<string, DrawingAvailability> availability = new();
    private readonly string[] csharpReferences = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?.Split(Path.PathSeparator) ?? [])
        .Concat([typeof(Database).Assembly.Location, typeof(Document).Assembly.Location, typeof(App).Assembly.Location, typeof(Wire).Assembly.Location])
        .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private readonly object mutationGate = new();
    private readonly OperationControl control = new();
    private readonly ConcurrentDictionary<string, string> mutationOwners = new();
    private readonly ConcurrentDictionary<string, string> benchmarkDocuments = new();
    private readonly Dictionary<string, Document?> benchmarkOriginal = new();
    private readonly Dictionary<string, (Request Request, CancellationToken Token)> benchmarkGates = new();
    private readonly SnapshotStore snapshots = new();
    private readonly OperationJournal journal = new(Wire.DataDirectory("operations", documents.SessionId));
    private readonly ResultArchive archive = new(Wire.DataDirectory("results", documents.SessionId));
    private readonly RevisionCache<JsonElement> catalogCache = new();
    private readonly RevisionCache<JsonElement> searchCache = new(8);
    private sealed record RenderFrame(string DocumentId, long Revision, int Width, int Height, DateTimeOffset CapturedAt, ImageRegistration? Registration);
    private readonly ConcurrentDictionary<string, RenderFrame> renderFrames = new();
    private readonly ConcurrentQueue<string> renderOrder = new();
    private sealed record LispJob(string Id, Request Request, Document Document, string Code, IReadOnlyList<LispFinding> Findings)
    {
        /// <summary>Set when AutoCAD began evaluating the script; from then on only its own result ends the job.</summary>
        public volatile bool Started;
        /// <summary>The script waits for the user's confirmation; nothing has been sent to AutoCAD yet.</summary>
        public volatile bool AwaitingApproval;
        /// <summary>Confirmed, and sent to AutoCAD at the next Idle in which its drawing is active and no command runs.</summary>
        public volatile bool PendingStart;
        /// <summary>AutoCAD reported the end of the outermost AutoLISP evaluation after the script started.</summary>
        public volatile bool EvaluationEnded;
        /// <summary>AutoLISP evaluations under way in the job's drawing; nested ones come in pairs. CAD thread only.</summary>
        public int LispDepth;
        public DateTimeOffset ApprovalDeadline;
        /// <summary>The script must begin before this time, or it fails without having run.</summary>
        public DateTimeOffset StartBy = Request.Deadline ?? DateTimeOffset.UtcNow.AddSeconds(30);
        public IDisposable? Approval;
    }
    /// <summary>Shows the confirmation for a waiting script; returns a handle that closes it. Null in Core Console.</summary>
    public Func<LispApprovalRequest, Action<bool, bool>, IDisposable>? ApprovalPresenter { get; set; }
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(10);
    // Keys preview hashes, so cad_edit accepts only a hash this worker produced for the plan.
    private readonly byte[] previewKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    // Written on the CAD thread, read by pipe threads for receipts.
    private volatile LispJob? lisp;
    /// <summary>Operation id of the agent's script AutoCAD is evaluating; CADMCPLISP refuses to run meanwhile.</summary>
    internal static volatile string? RunningLispScript;
    // CAD thread health, written on the CAD thread and read by pipe threads for diagnostics.
    private IntPtr mainWindow;
    private bool inIdle;
    private long lastIdleTicks = DateTime.UtcNow.Ticks, modalSinceTicks;
    private int modalDepth;
    private volatile bool quitting;
    private volatile string? busyReason;
    private void RefreshAvailability()
    {
        var active = App.DocumentManager.MdiActiveDocument;
        bool globalBusy = quitting || ModalFor is not null || lisp is { Started: true };
        bool activation = App.DocumentManager.DocumentActivationEnabled && App.DocumentManager.IsApplicationContext;
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (Document doc in App.DocumentManager)
        {
            var state = documents.Register(doc); live.Add(state.Id);
            string command = doc.CommandInProgress;
            bool quiet = string.IsNullOrWhiteSpace(command) && (!ReferenceEquals(active, doc) || doc.Editor.IsQuiescent);
            bool ownLisp = lisp is { } job && ReferenceEquals(job.Document, doc);
            bool canSwitch = active is null || ReferenceEquals(active, doc) || active.Editor.IsQuiescent || activation;
            string? reason = globalBusy ? "AutoCAD is modal, closing or evaluating AutoLISP" : !quiet ? "Command " + command + " is active in " + Path.GetFileName(doc.Name) : ownLisp ? "This drawing is reserved for AutoLISP" : !canSwitch ? "AutoCAD currently disables document activation" : null;
            availability[state.Id] = new(state.Id, Path.GetFileName(doc.Name), !globalBusy && quiet && !ownLisp, !globalBusy && quiet && !ownLisp && canSwitch, command, reason, DateTimeOffset.UtcNow);
        }
        foreach (string id in availability.Keys) if (!live.Contains(id)) availability.TryRemove(id, out _);
    }
    private bool CanRun(Request request)
    {
        if (ModalFor is not null || lisp is { Started: true }) return false;
        if (request.Operation == "cad_documents") return true;
        if (request.Operation == "benchmark_create") return App.DocumentManager.MdiActiveDocument?.Editor.IsQuiescent != false;
        if (request.DocumentId is null || !availability.TryGetValue(request.DocumentId, out var ready)) return true; // let Execute report scope/closed errors
        bool writing = !CadOperations.ReadOnly.Contains(request.Operation) || CadOperations.ActivatesDocument(request.Operation);
        bool available = writing ? ready.WriteAvailable : ready.ReadAvailable;
        if (request.Operation == "cad_render" && App.DocumentManager.MdiActiveDocument?.Editor.IsQuiescent == false) available = false;
        if (!available) busyReason = ready.Reason;
        return available;
    }
    public void Start()
    {
        RefreshAvailability();
        App.Idle += Idle; documents.Closing += DocumentClosing;
        App.EnterModal += EnterModal; App.LeaveModal += LeaveModal; App.BeginQuit += BeginQuit; App.QuitAborted += QuitAborted;
        // Core Console has no main window; Wake is then a no-op and Idle still runs on its own schedule.
        try { mainWindow = App.MainWindow?.Handle ?? IntPtr.Zero; } catch (System.Exception) { mainWindow = IntPtr.Zero; }
    }
    private void EnterModal(object? sender, EventArgs e) { if (Interlocked.Increment(ref modalDepth) == 1) Interlocked.Exchange(ref modalSinceTicks, DateTime.UtcNow.Ticks); }
    private void LeaveModal(object? sender, EventArgs e) { if (Interlocked.Decrement(ref modalDepth) <= 0) { Interlocked.Exchange(ref modalDepth, 0); Wake(); } }
    private void BeginQuit(object? sender, EventArgs e) => quitting = true;
    private void QuitAborted(object? sender, EventArgs e) => quitting = false;
    private TimeSpan? ModalFor => Volatile.Read(ref modalDepth) > 0 ? DateTime.UtcNow - new DateTime(Interlocked.Read(ref modalSinceTicks), DateTimeKind.Utc) : null;
    /// <summary>
    /// AutoCAD raises Idle only after processing a window message. A request queued by a pipe thread while
    /// AutoCAD sits idle in the background would otherwise wait for the next mouse move or timer.
    /// </summary>
    private void Wake()
    {
        if (mainWindow != IntPtr.Zero) NativeMethods.PostMessage(mainWindow, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
    }
    /// <summary>Why queued work may not run; reported in cad_runtime_status and expiry errors. Safe on any thread.</summary>
    internal object Health()
    {
        var idle = new DateTime(Interlocked.Read(ref lastIdleTicks), DateTimeKind.Utc);
        var oldest = queue.TryPeek(out var first) ? (DateTimeOffset.UtcNow - first.Enqueued).TotalSeconds : (double?)null;
        var job = lisp;
        return new { last_idle_utc = idle, seconds_since_idle = Math.Round((DateTime.UtcNow - idle).TotalSeconds, 1), modal_dialog = ModalFor is { } modal ? new { open = true, seconds = Math.Round(modal.TotalSeconds, 1) } : null,
            quitting, busy_reason = busyReason, oldest_queued_seconds = oldest is null ? (double?)null : Math.Round(oldest.Value, 1),
            lisp = job is null ? null : new { operation_id = job.Id, started = job.Started, awaiting_approval = job.AwaitingApproval, waiting_for_idle_drawing = job.PendingStart } };
    }
    private string Blocker()
    {
        if (ModalFor is { } modal) return "AutoCAD shows a modal dialog (" + Math.Round(modal.TotalSeconds) + " s); ask the user to close it";
        if (quitting) return "AutoCAD is closing";
        var idle = DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastIdleTicks), DateTimeKind.Utc);
        if (busyReason is { } reason) return "the drawing was busy: " + reason;
        return idle > TimeSpan.FromSeconds(5) ? "the AutoCAD UI thread did not become idle for " + Math.Round(idle.TotalSeconds) + " s" : "the queue was busy";
    }
    public async Task<Response> Enqueue(Request r, CancellationToken ct)
    {
        if (r.Operation == "csharp_references")
        {
            if (r.SessionId != documents.SessionId || r.DocumentId is null || !availability.ContainsKey(r.DocumentId)) throw new CadFault("DOCUMENT_CLOSED", "Pinned drawing is unavailable");
            var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).Select(a => a.Location).Where(File.Exists);
            var references = csharpReferences.Concat(loaded).Concat(new[] { Path.Combine(Path.GetDirectoryName(typeof(Database).Assembly.Location)!, "AcDbMgdBrep.dll") }).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new(r.RequestId, "completed", new { references }, documents.SessionId, r.DocumentId);
        }
        if (r.OwnerId?.StartsWith("benchmark-", StringComparison.Ordinal) == true && r.Operation is not ("benchmark_create" or "cad_documents"))
        {
            string run = r.OwnerId["benchmark-".Length..];
            if (r.SessionId != documents.SessionId || !benchmarkDocuments.TryGetValue(run, out var target) || r.DocumentId != target)
                throw new CadFault("BENCHMARK_SCOPE_MISMATCH", "Benchmark requests may access only their own isolated test drawing");
        }
        // Receipts use no AutoCAD API and remain readable while its UI thread is building geometry.
        if (r.Operation is "cad_operation_status" or "cad_operation_list") return OperationStatus(r);
        if (r.Operation is "cad_cancel" or "cad_runtime_status" or "cad_diagnostics")
        {
            if (r.SessionId != documents.SessionId) throw new CadFault("SESSION_MISMATCH", "Worker session changed");
            if (r.Operation == "cad_cancel")
            {
                // The flag is set first, so the Idle that Wake raises already sees the cancellation.
                var requested = control.Cancel(r);
                Wake();
                return new(r.RequestId, "completed", new { cancellation_requested = requested, boundary = "transaction/action boundary; native kernel calls finish first; running AutoLISP requires Esc" }, documents.SessionId, r.DocumentId);
            }
            return new(r.RequestId, "completed", r.Operation == "cad_diagnostics" ? WorkSafety.Diagnostics(documents.SessionId,r.DocumentId) : new { operations = control.Snapshot(r), queued = queue.Count, cad_thread = Health(), document_availability = r.DocumentId is { } targetId && availability.TryGetValue(targetId, out var ready) ? ready : null }, documents.SessionId, r.DocumentId);
        }
        ct.ThrowIfCancellationRequested();
        var source = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (MutationRecovery.IsMutation(r.Operation))
        {
            string id = EditPlan.RequiredText(r.Data, "operation_id");
            if (r.Operation == "cad_edit") { EditPlan.Parse(EditPlan.RequiredText(r.Data, "operations_json")); DrawingVerification.Parse(r.Data.Text("expectations_json")); EditPlan.RequirePreviewed(r.Data, r.DocumentId, r.ExpectedRevision, previewKey); }
            lock (mutationGate)
            {
                // Reading an existing receipt must also work when the queue is full, a dialog is open or AutoCAD quits.
                if (journal.Find(id) is not null) return Replay(r, id, journal.Begin(id, r)!);
                ThrowIfUnavailable();
                if (r.DocumentId is { } docId && availability.TryGetValue(docId, out var ready) && !ready.WriteAvailable && DateTimeOffset.UtcNow - ready.ObservedAt < TimeSpan.FromSeconds(3))
                    throw new CadFault("DOCUMENT_BUSY", "Operation was not accepted and no changes started. " + ready.Reason + ". Check cad_runtime_status.document_availability before submitting; do not interrupt the user's command.");
                if (queue.Count >= 32) throw new CadFault("BUSY", "CAD queue is full");
                if (journal.Begin(id, r) is { } prior) return Replay(r, id, prior);
                mutationOwners[id] = r.RequestId;
                // Transport cancellation must not destroy the only receipt of an accepted change.
                queue.Enqueue((r, control.Accept(id, r), source, DateTimeOffset.UtcNow));
            }
        }
        else { ThrowIfUnavailable(); if (queue.Count >= 32) throw new CadFault("BUSY", "CAD queue is full"); queue.Enqueue((r, ct, source, DateTimeOffset.UtcNow)); }
        Wake();
        return await Portable.Await(source.Task, ct).ConfigureAwait(false);
    }
    /// <summary>AutoCAD raises no Idle inside a modal dialog; fail now with the reason instead of expiring silently.</summary>
    private void ThrowIfUnavailable()
    {
        if (quitting) throw new CadFault("AUTOCAD_QUITTING", "AutoCAD is closing; no new work is accepted");
        if (ModalFor is { } modal && modal > TimeSpan.FromSeconds(1)) throw new CadFault("APPLICATION_MODAL", "AutoCAD shows a modal dialog; ask the user to close it, then retry");
    }
    private Response Replay(Request r, string id, OperationJournal.Entry entry) => entry.Result is { } result
        ? result with { RequestId = r.RequestId }
        : new(r.RequestId, "pending", new { operation_id = id, state = mutationOwners.ContainsKey(id) || lisp?.Id == id ? entry.State : "unknown",
            replayed = true, requires_poll = "cad_operation_status", mutation_repeated = false }, documents.SessionId, entry.DocumentId);
    private Response OperationStatus(Request r)
    {
        if (r.SessionId != documents.SessionId) throw new CadFault("SESSION_MISMATCH", "Native worker session changed");
        if (r.Operation == "cad_operation_list")
        {
            DateTimeOffset since = DateTimeOffset.TryParse(r.Data.Text("since"), out var value) ? value : DateTimeOffset.MinValue;
            var unreadable = new List<string>();
            var recent = journal.Recent(r.DocumentId ?? "", since, r.Data.Number("limit", 50), r.OwnerId, unreadable);
            return new(r.RequestId, "completed", new { operations = recent.Select(p => OperationJournal.Summary(p.Id, p.Entry,
                p.Entry.State is "completed" or "failed" or "cancelled" || mutationOwners.ContainsKey(p.Id) || lisp?.Id == p.Id)),
                truncated = recent.Count == r.Data.Number("limit", 50),
                unreadable_receipts = unreadable.Count == 0 ? null : unreadable }, documents.SessionId, r.DocumentId);
        }
        string id = EditPlan.RequiredText(r.Data, "operation_id"); var entry = journal.Find(id);
        if (entry is not null && entry.DocumentId != r.DocumentId) throw new CadFault("DOCUMENT_MISMATCH", "Operation belongs to a different drawing");
        bool active = entry?.State is "completed" or "failed" or "cancelled" || mutationOwners.ContainsKey(id) || lisp?.Id == id;
        return new(r.RequestId, "completed", new { operation_id = id, state = entry is null ? "not_found" : active ? entry.State : "unknown",
            phase = control.PhaseOf(id) ?? entry?.Phase, result = entry?.Result, original_request = entry?.Request, persistence = journal.Persistence,
            retry = "Replay only the original exact request with its operation_id. Unknown/not_found never proves no changes." }, documents.SessionId, r.DocumentId);
    }
    private void Idle(object? sender, EventArgs e)
    {
        // An operation that pumps messages (plotting, a dialog, document activation) can raise Idle again;
        // a nested call must never start a second operation inside the first.
        if (inIdle) return;
        inIdle = true;
        // Last resort: an exception leaving an AutoCAD event handler can end acad.exe.
        try { RunIdle(); }
        catch (System.Exception error) { System.Diagnostics.Trace.WriteLine("CAD MCP idle: " + error); }
        finally { inIdle = false; }
    }
    private void RequireBenchmarkSession(Request request)
    {
        if (request.OwnerId is { } owner && owner.StartsWith("benchmark-", StringComparison.Ordinal) && benchmarkDocuments.TryGetValue(owner[10..], out var fixture))
            BenchmarkIsolation.RequireFixtureOnly(App.DocumentManager.Cast<Document>().Select(d => documents.Register(d).Id), fixture);
    }
    private void RunIdle()
    {
        Interlocked.Exchange(ref lastIdleTicks, DateTime.UtcNow.Ticks);
        // While AutoCAD closes its drawings no work starts; queued requests expire or are failed when the worker stops.
        if (quitting) return;
        // All database access occurs here, on the CAD application thread.
        CheckLisp();
        RefreshAvailability();
        if (!queue.TryTake(p => p.Request.DocumentId ?? "", p => CanRun(p.Request), p => p.Token.IsCancellationRequested || p.Request.Deadline <= DateTimeOffset.UtcNow, out var pending))
        {
            var doc=App.DocumentManager.MdiActiveDocument;
            if(doc is not null&&doc.Editor.IsQuiescent&&lisp is null)RecalculateTables(doc);
            return;
        }
        if (pending.Token.IsCancellationRequested || pending.Request.Deadline <= DateTimeOffset.UtcNow)
        {
            var expired = Response.Fail(pending.Request, pending.Token.IsCancellationRequested ? "CANCELLED" : "DEADLINE_EXPIRED",
                "Request stopped before execution; no drawing changes were started" + (pending.Token.IsCancellationRequested ? "" : ": " + Blocker()));
            if (MutationRecovery.IsMutation(pending.Request.Operation))
            { string id = EditPlan.RequiredText(pending.Request.Data, "operation_id"); expired = journal.Complete(id, expired); mutationOwners.TryRemove(id, out _); control.Complete(id); }
            pending.Source.TrySetResult(expired);
            if (!queue.IsEmpty) Wake();
            return;
        }
        busyReason = null;
        var job = pending;
        Response response;
        var originalDocument=App.DocumentManager.MdiActiveDocument;
        bool activate=CadOperations.ActivatesDocument(job.Request.Operation);
        // A script this request queued runs in the drawing it activated; any other request gives the tab back.
        var lispBefore=lisp;
        try
        {
            RequireBenchmarkSession(job.Request);
            if (!BenchmarkTrace.Record(job.Request, "begin")) throw new CadFault("BENCHMARK_TRACE_UNAVAILABLE", "Could not persist the benchmark native-call trace; this CAD operation was not started");
            if(activate){var target=documents.Active(job.Request,checkRevision:false,allowInactive:true);if(!string.IsNullOrWhiteSpace(target.CommandInProgress)||ReferenceEquals(target,originalDocument)&&!target.Editor.IsQuiescent)throw new CadFault("DOCUMENT_BUSY","Target drawing has an active command");App.DocumentManager.MdiActiveDocument=target;}
            response = Execute(job.Request, job.Token);
        }
        catch (CadFault error) { response = Response.Fail(job.Request, error.Code, error.Message); }
        catch (System.Exception error) { response = Response.Fail(job.Request, "CAD_ERROR", error.Message); }
        finally { if(activate && (lisp is null || ReferenceEquals(lisp,lispBefore)) && originalDocument is not null && !originalDocument.IsDisposed)try{App.DocumentManager.MdiActiveDocument=originalDocument;}catch(System.Exception restoreError){System.Diagnostics.Trace.WriteLine("Restore document: "+restoreError.Message);} }
        if (MutationRecovery.IsMutation(job.Request.Operation) && response.Status != "queued")
        {
            string id = EditPlan.RequiredText(job.Request.Data, "operation_id");
            if (journal.Find(id)?.Result is null) response = journal.Complete(id, response);
            mutationOwners.TryRemove(id, out _); control.Complete(id);
        }
        _ = BenchmarkTrace.Record(job.Request, "end", response.Error?.Code);
        job.Source.TrySetResult(response);
        // The next queued request needs another Idle, which an otherwise idle AutoCAD would not raise soon.
        if (!queue.IsEmpty) Wake();
    }
    private Response Execute(Request r, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (r.Operation == "benchmark_create") return CreateBenchmark(r);
        if (r.Operation == "benchmark_activate" || r.Operation == "benchmark_gate" || r.Operation == "benchmark_settle_gate") return BenchmarkControl(r);
        if (r.Operation == "cad_documents") return new(r.RequestId,"completed",documents.Catalog(),documents.SessionId,r.DocumentId);
        if (r.Operation == "cad_csharp") return RunCSharp(r, ct);
        if (r.Operation is "cad_operation_status" or "cad_operation_list") return OperationStatus(r);
        if (r.Operation is "cad_edit" or "cad_lisp" or "cad_export" or "cad_publish") return Mutate(r, ct);
        bool readOnly = CadOperations.ReadOnly.Contains(r.Operation);
        var doc = documents.Active(r, checkRevision: !readOnly, allowInactive: readOnly); var state = documents.Register(doc);
        using var reading = readOnly ? documents.ReadScope(doc) : null;
        // A write lock is itself an AutoCAD command/undo boundary, even if this
        // request only opens objects ForRead. The chat polls cad_context every
        // three seconds: locking it for write can invalidate the user's Redo.
        // ObjectARX permits ForRead access without locking the document. These
        // reads run synchronously on the CAD thread while the editor is quiescent.
        // Preview is the exception: its rolled-back transaction still writes.
        using var locked = !readOnly || r.Operation == "cad_edit_preview" ? doc.LockDocument() : null;
        using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
        object data; string status = "completed";
        switch (r.Operation)
        {
            case "cad_context":
                bool isActive=ReferenceEquals(doc,App.DocumentManager.MdiActiveDocument);
                using (var view = isActive?doc.Editor.GetCurrentView():null)
                    data = new { name = doc.Name,active=isActive,insunits_code=(int)doc.Database.Insunits,current_layout=ReadingMetadata.LayoutOf(doc.Database.CurrentSpaceId,tr)?.LayoutName,
                        current_space_handle=doc.Database.CurrentSpaceId.Handle.ToString(),acad_version = Convert.ToString(App.GetSystemVariable("ACADVER")), dark_theme=Convert.ToInt32(App.GetSystemVariable("COLORTHEME"))==0, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                        units = doc.Database.Insunits.ToString(), space = doc.Database.CurrentSpaceId == ((BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace] ? "model" : "paper",
                        selection = isActive?Selection(doc):new{available=false,reason="inactive_document_editor"},
                        vertical_managed_assemblies_loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name)
                            .Where(n => n is not null && (n.StartsWith("Aecc", StringComparison.OrdinalIgnoreCase) || n.StartsWith("AcM", StringComparison.OrdinalIgnoreCase)))
                            .Distinct().Take(50).ToArray(),
                        ucs_to_wcs = isActive?doc.Editor.CurrentUserCoordinateSystem.ToArray():null,
                        editor_context_available=isActive,
                        view = view is null?(object)new{available=false,reason="inactive_document_editor"}:new {available=true,width = view.Width, height = view.Height, perspective = view.PerspectiveEnabled },
                        document_state = DrawingReview.DocumentState(doc),
                        capabilities = CadOperations.WorkerOperations,
                        editing = new { coordinates = "WCS", units = "drawing_units", angles = "degrees", native_transaction = true, lisp_atomic = false, operation_records = journal.Count, journal = journal.Persistence, pending_lisp = lisp?.Id,
                            lisp_policy = LispPolicy.Name(CadSettings.EffectiveLispPolicy()), lisp_policy_owner = "user (AutoCAD command CADMCPLISP)" },
                        cache = new { catalog_hits = catalogCache.Hits, catalog_misses = catalogCache.Misses, search_hits = searchCache.Hits, search_misses = searchCache.Misses, invalidation = "document_revision_and_space", render_cached = false,
                            table_dependency_error = state.TableError, ignored_read_side_effect_events = state.ReadSideEffectEvents },
                        limitations = new[] { "preview_render_unverified", "Civil3D_Map3D_SPDS_special_geometry_partial", "native_edits_current_space_only" } };
                break;
            case "cad_review":
                ObjectId[]? reviewIds=null;
                if(r.Data.Text("handles_json") is {} reviewJson){using var parsed=JsonDocument.Parse(reviewJson);reviewIds=parsed.RootElement.EnumerateArray().Select(h=>NativeTables.Resolve(doc.Database,h.GetString()!)).ToArray();}
                if(r.Data.Text("options_json") is {} reviewOptionsJson){data=ReviewOptions.Run(doc.Database,tr,reviewOptionsJson,reviewIds,ct);break;}
                reviewIds??=((BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForRead)).Cast<ObjectId>().Where(id=>!id.IsErased).Take(251).ToArray();
                data=DrawingQuality.Review(doc.Database,tr,reviewIds,ct);break;
            case "cad_takeoff": data = DrawingInsight.Takeoff(doc.Database, tr, r.Data, ct); break;
            case "cad_text_units": data = TextUnits.Read(doc.Database, tr, r.Data, ct); break;
            case "cad_properties": data = ObjectInspection.Properties(doc.Database, tr, r.Data, ct); break;
            case "cad_proxies": data = ObjectInspection.Proxies(doc.Database, tr, r.Data, ct); break;
            case "cad_outline": data = DrawingInsight.Outline(doc.Database, tr, System.IO.Path.GetFileName(doc.Name), ct, DraftingPlan.Integer(r.Data, "text_sample", 0, 200, 40), DraftingPlan.Integer(r.Data, "layer_limit", 1, 5000, 100)); break;
            case "cad_file_inspect": data = DrawingInsight.InspectFile(EditPlan.RequiredText(r.Data, "path"), ct, r.Data); break;
            case "cad_changes": data = DrawingInsight.Changes(state, tr, r.Data); break;
            case "cad_edit_preview":
                var previewPlan = EditPlan.RequiredText(r.Data, "operations_json");
                var previewOperations = EditPlan.Parse(previewPlan);
                var stateBeforePreview = DrawingReview.DocumentState(doc);
                data = new { plan_hash = EditPlan.Hash(previewPlan, r.Data.Text("expectations_json"), state.Id, state.Revision, previewKey), previewed_revision = state.Revision,
                    document_state_before_preview = stateBeforePreview,
                    apply = "cad_edit with the same operations_json and expectations_json, preview_hash and expected_revision runs exactly this plan",
                    result = Edits.Execute(doc, previewOperations, ct, DrawingVerification.Parse(r.Data.Text("expectations_json")), preview: true) };
                break;
            case "cad_solid_get":
                data=SolidModeling.Inspect(tr.GetObject(NativeTables.Resolve(doc.Database,r.Data.Text("handle")!),OpenMode.ForRead) as Solid3d ?? throw new CadFault("INVALID_SOLID","Solid3d required"));break;
            case "cad_assembly_get":
                data=StructuralAssemblies.Inspect(doc.Database,tr,tr.GetObject(NativeTables.Resolve(doc.Database,r.Data.Text("handle")!),OpenMode.ForRead) as BlockReference ?? throw new CadFault("INVALID_ASSEMBLY","BlockReference required"));break;
            case "cad_table_dependencies":data=TableLinks.Inspect(doc.Database,tr);break;
            case "cad_release_check":
                using(var parsed=JsonDocument.Parse(EditPlan.RequiredText(r.Data,"layouts_json")))data=DrawingQuality.Release(doc.Database,tr,parsed.RootElement.EnumerateArray().Select(v=>v.GetString()!).ToArray());break;
            case "cad_table_get":
                var tableId = NativeTables.Resolve(doc.Database, r.Data.Text("handle")!);
                if (tr.GetObject(tableId, OpenMode.ForRead) is not Table table) throw new CadFault("INVALID_TABLE", "Handle must identify a native Table");
                data = NativeTables.Read(table, tr,
                    DraftingPlan.Integer(r.Data, "first_row", 0, 499, 0), DraftingPlan.Integer(r.Data, "first_column", 0, 49, 0),
                    DraftingPlan.Integer(r.Data, "row_count", 1, 100, 20), DraftingPlan.Integer(r.Data, "column_count", 1, 50, 20));
                break;
            case "cad_catalog":
                bool catalogHit = catalogCache.TryGet(state.Id, "catalog", state.Revision, out var catalog);
                if (!catalogHit) { catalog = Wire.Element(Catalog.Read(doc.Database, tr)); catalogCache.Put(state.Id, "catalog", state.Revision, catalog); }
                data = new { catalog, cached = catalogHit }; break;
            case "cad_verify":
                var reviewOptions = r.Data.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                reviewOptions["verified_document_id"] = state.Id;
                data = DrawingReview.Verify(doc, tr, Wire.Element(reviewOptions), journal); break;
            case "cad_vertical_capabilities":data=VerticalEditing.Capabilities(doc.Database);break;
            case "cad_vertical_catalog":
                data = Verticals.Catalog(doc.Database, tr); break;
            case "cad_vertical_get":
                double[][]? samplePoints = null;
                if (r.Data.Text("sample_points_json") is { } sampleJson)
                {
                    using var samples = JsonDocument.Parse(sampleJson);
                    if (samples.RootElement.ValueKind != JsonValueKind.Array || samples.RootElement.GetArrayLength() is < 1 or > 100)
                        throw new CadFault("INVALID_SAMPLE_POINTS", "Supply 1..100 [x,y] WCS points");
                    samplePoints = samples.RootElement.EnumerateArray().Select(p =>
                    {
                        if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 2) throw new CadFault("INVALID_SAMPLE_POINTS", "Expected [x,y]");
                        var values = p.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out double n) && double.IsFinite(n)
                            ? n : throw new CadFault("INVALID_SAMPLE_POINTS", "Coordinates must be finite numbers")).ToArray();
                        return values;
                    }).ToArray();
                }
                data = Verticals.Inspect(doc.Database, tr, EditPlan.RequiredText(r.Data, "handle"), samplePoints);
                break;
            case "cad_render":
#if CORE_CONSOLE
                throw new CadFault("GUI_REQUIRED", "CapturePreviewImage requires the interactive AutoCAD document");
#else
                int width = r.Data.Number("width", 1024), height = r.Data.Number("height", 768);
                if (width < 128 || width > 1600 || height < 128 || height > 1600)
                    throw new CadFault("INVALID_IMAGE_SIZE", "Image dimensions must be 128..1600");
                using (var bitmap = PreviewRendering.Capture(doc,tr,r.Data,width,height,ct,out var previewMetadata))
                using (var png = new MemoryStream())
                {
                    bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                    if (png.Length > 4 * 1024 * 1024) throw new CadFault("IMAGE_TOO_LARGE", "Request smaller dimensions");
                    var imageId = Guid.NewGuid().ToString("N");
                    var capturedAt = DateTimeOffset.UtcNow;
                    renderFrames[imageId] = new RenderFrame(state.Id, state.Revision, bitmap.Width, bitmap.Height, capturedAt, null);
                    renderOrder.Enqueue(imageId);
                    while (renderFrames.Count > 8 && renderOrder.TryDequeue(out var old)) renderFrames.TryRemove(old, out _);
                    data = new { mime_type = "image/png", image_base64 = Convert.ToBase64String(png.ToArray()), width = bitmap.Width, height = bitmap.Height,
                        image_id = imageId, pixel_origin = "top_left", coordinate_mapping = "requires_cad_image_register_control_points",
                        source = "AutoCAD.GraphicsSystem.offscreen", captured_at = DateTimeOffset.UtcNow,
                        requested_view = r.Data.Text("view_name") ?? "current", framed_handles = r.Data.Text("handles_json"),
                        view = previewMetadata,
                        limitations = new[] { "preview_fidelity_requires_live_validation", "control_points_required_for_exact_pixel_to_world_mapping" } };
                }
                status = "partial";
                break;
#endif
            case "cad_image_register":
                var frameId = EditPlan.RequiredText(r.Data, "image_id");
                if (!renderFrames.TryGetValue(frameId, out var frame) || frame.DocumentId != state.Id)
                    throw new CadFault("IMAGE_NOT_FOUND", "Render image id is unknown or belongs to another drawing");
                using (var controls = JsonDocument.Parse(EditPlan.RequiredText(r.Data, "control_points_json")))
                {
                    var fit = ImageRegistration.Fit(controls.RootElement, frame.Width, frame.Height);
                    renderFrames[frameId] = frame with { Registration = fit };
                    data = new { image_id = frameId, width = frame.Width, height = frame.Height,
                        pixel_origin = "top_left", coordinate_system = "WCS", model = "2D_affine",
                        pixel_to_world_x = fit.X, pixel_to_world_y = fit.Y, world_z = fit.Z,
                        control_points = fit.ControlPointCount, rms_error = fit.RmsError, max_error = fit.MaxError,
                        world_top_left = fit.PixelToWorld(0, 0), world_top_right = fit.PixelToWorld(frame.Width, 0),
                        world_bottom_left = fit.PixelToWorld(0, frame.Height), captured_revision = frame.Revision,
                        historical = frame.Revision != state.Revision };
                }
                break;
            case "cad_image_point":
                var mappedId = EditPlan.RequiredText(r.Data, "image_id");
                if (!renderFrames.TryGetValue(mappedId, out var mapped) || mapped.DocumentId != state.Id || mapped.Registration is null)
                    throw new CadFault("IMAGE_NOT_REGISTERED", "Render image requires cad_image_register with control points");
                using (var points = JsonDocument.Parse(EditPlan.RequiredText(r.Data, "points_json")))
                {
                    if (points.RootElement.ValueKind != JsonValueKind.Array || points.RootElement.GetArrayLength() is < 1 or > 100)
                        throw new CadFault("INVALID_POINTS", "Supply 1..100 [x,y] pairs");
                    string direction = r.Data.Text("direction") ?? "pixel_to_world";
                    if (direction is not ("pixel_to_world" or "world_to_pixel")) throw new CadFault("INVALID_DIRECTION", direction);
                    var converted = points.RootElement.EnumerateArray().Select(point =>
                    {
                        if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2 ||
                            !point[0].TryGetDouble(out var x) || !point[1].TryGetDouble(out var y) || !double.IsFinite(x) || !double.IsFinite(y))
                            throw new CadFault("INVALID_POINT", "Expected finite [x,y] coordinates");
                        if (direction == "pixel_to_world" && (x < 0 || x > mapped.Width || y < 0 || y > mapped.Height))
                            throw new CadFault("POINT_OUTSIDE_IMAGE", "Pixel is outside the rendered image");
                        return direction == "pixel_to_world" ? mapped.Registration.PixelToWorld(x, y) : mapped.Registration.WorldToPixel(x, y);
                    }).ToArray();
                    data = new { image_id = mappedId, direction, coordinates = converted, captured_revision = mapped.Revision,
                        historical = mapped.Revision != state.Revision, rms_error = mapped.Registration.RmsError,
                        pixel_origin = "top_left", coordinate_system = "WCS" };
                }
                break;
            case "cad_snapshot":
                int limit = r.Data.Number("limit", 2000);
                if (limit < 1 || limit > 5000) throw new CadFault("INVALID_LIMIT", "limit must be 1..5000");
                var scopeId = doc.Database.CurrentSpaceId.Handle.ToString();
                var cachedSnapshot = snapshots.Reuse(state.Id, scopeId, state.Revision, limit);
                if (cachedSnapshot is not null)
                {
                    data = SnapshotMetadata(cachedSnapshot, doc.Database.Insunits.ToString(), true);
                    if (cachedSnapshot.Truncated || cachedSnapshot.Entities.Any(x => x.Text("access") != "structured")) status = "partial";
                    break;
                }
                var entities = new List<JsonElement>(); bool truncated = false;
                var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entities.Count == limit) { truncated = true; break; }
                    try
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is Entity entity)
                        {
                            if (!BenchmarkTrace.Record(r, "snapshot_entity_begin:" + id.Handle.ToString())) throw new CadFault("BENCHMARK_TRACE_UNAVAILABLE", "Could not persist snapshot native-read trace");
                            entities.Add(Reader.Read(entity, tr));
                            _ = BenchmarkTrace.Record(r, "snapshot_entity_end:" + id.Handle.ToString());
                        }
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception error)
                    { entities.Add(Wire.Element(new { handle = id.Handle.ToString(), access = "unsupported", error = error.ErrorStatus.ToString() })); }
                }
                var snapshot = snapshots.Add(state.Id, state.Revision, entities, truncated, scopeId, limit);
                // Return metadata only; pages are bounded and fetched through cad_query.
                data = SnapshotMetadata(snapshot, doc.Database.Insunits.ToString(), false);
                if (snapshot.Truncated || snapshot.Entities.Any(x => x.Text("access") != "structured")) status = "partial";
                break;
            case "cad_query":
                data = snapshots.Query(r.Data.Text("snapshot_id") ?? "", state.Id, state.Revision, r.Data); break;
            case "cad_search":
                using (var options = JsonDocument.Parse(r.Data.Text("options_json") ?? "{}"))
                {
                    string scope = options.RootElement.Text("scope") ?? "current";
                    bool cacheable = scope is "current" or "model" or "layouts" or "all";
                    string key = "search:" + doc.Database.CurrentSpaceId.Handle + ":" + options.RootElement.GetRawText();
                    JsonElement search = default;
                    bool hit = cacheable && searchCache.TryGet(state.Id, key, state.Revision, out search);
                    if (!hit)
                    {
                        search = Wire.Element(DrawingSearch.Read(doc, tr, options.RootElement, ct));
                        if (cacheable && search.GetRawText().Length <= 512 * 1024) searchCache.Put(state.Id, key, state.Revision, search);
                    }
                    data = new { result = search, cached = hit };
                }
                break;
            case "cad_result_get":
                data = archive.Read(EditPlan.RequiredText(r.Data, "archive_id"), documents.SessionId, state.Id, r.Data.Number("offset", 0), r.Data.Number("limit", 16000)); break;
            case "cad_entity_get":
            case "cad_focus":
                if (!long.TryParse(r.Data.Text("handle"), System.Globalization.NumberStyles.HexNumber, null, out long value))
                    throw new CadFault("INVALID_HANDLE", "Expected hexadecimal handle");
                if (!doc.Database.TryGetObjectId(new Handle(value), out var objectId) || objectId.IsErased)
                    throw new CadFault("ENTITY_NOT_FOUND", "Handle does not resolve in this document");
                if (tr.GetObject(objectId, OpenMode.ForRead) is not Entity found || ReadingMetadata.LayoutOf(found.OwnerId,tr) is null || r.Operation=="cad_focus"&&found.OwnerId!=doc.Database.CurrentSpaceId)
                    throw new CadFault("UNSUPPORTED_SCOPE", "Entity reads support top-level model/layout objects; focus requires current space. Read nested instances through cad_search expand_blocks.");
                if (r.Operation == "cad_focus") { doc.Editor.SetImpliedSelection([objectId]); data = new { handle = found.Handle.ToString(), selected = true }; }
                else { var entityData = Reader.Read(found, tr); data = entityData; if (entityData.Text("access") != "structured") status = "partial"; }
                break;
            default: throw new CadFault("UNSUPPORTED_OPERATION", r.Operation);
        }
        if (!readOnly && r.ExpectedRevision.HasValue && r.ExpectedRevision != state.Revision)
            throw new CadFault("REVISION_CONFLICT", "Document changed during operation; discard result and refresh context");
        // Materialize all CAD-backed values while the document lock and transaction
        // are still alive. The pipe serializes the response on a background thread.
        var detachedData = Wire.Element(data);
        if (r.Operation != "cad_render" && detachedData.GetRawText().Length > 512 * 1024)
        {
            var archiveId = archive.Put(documents.SessionId, state.Id, state.Revision, detachedData);
            detachedData = Wire.Element(new { archive_id = archiveId, captured_revision = state.Revision, detail = "Result archived; use cad_result_get to read bounded JSON text pages" }); status = "partial";
        }
        return new(r.RequestId, status, detachedData, documents.SessionId, state.Id, state.Revision);
    }
    private Response CreateBenchmark(Request request)
    {
#if CORE_CONSOLE
        throw new CadFault("GUI_REQUIRED", "The built-in benchmark creates an isolated drawing in interactive AutoCAD");
#else
        if (request.SessionId != documents.SessionId) throw new CadFault("SESSION_MISMATCH", "AutoCAD session changed");
        string run = EditPlan.RequiredText(request.Data, "run_id");
        if (!Guid.TryParseExact(run, "N", out _) || request.OwnerId != "benchmark-" + run)
            throw new CadFault("INVALID_BENCHMARK_RUN", "Expected a fresh benchmark run identifier");
        if (benchmarkDocuments.ContainsKey(run)) throw new CadFault("BENCHMARK_RUN_EXISTS", "The test drawing for this run already exists; do not create it again");
        BenchmarkIsolation.RequireEmpty(App.DocumentManager.Cast<Document>().Count());
        var original = App.DocumentManager.MdiActiveDocument;
        string folder = Wire.DataDirectory("benchmarks", documents.SessionId, run);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "CADMCP_Benchmark_" + run[..8] + ".dwg");
        try
        {
            using (var seed = new Database(true, true))
            { seed.Insunits = UnitsValue.Millimeters; seed.SaveAs(path, DwgVersion.Current); }
            var doc = DocumentCollectionExtension.Open(App.DocumentManager, path, false);
            var state = documents.Register(doc);
            benchmarkDocuments[run] = state.Id;
            benchmarkOriginal[run] = original;
            return new(request.RequestId, "completed", new { benchmark_fixture = true, run_id = run, path, units = "Millimeters" }, documents.SessionId, state.Id, state.Revision);
        }
        finally
        {
            if (original is not null && !original.IsDisposed) App.DocumentManager.MdiActiveDocument = original;
        }
#endif
    }
    private Response BenchmarkControl(Request request)
    {
        string run = request.OwnerId?.StartsWith("benchmark-", StringComparison.Ordinal) == true ? request.OwnerId[10..] : "";
        if (!benchmarkDocuments.TryGetValue(run, out var target) || request.DocumentId != target) throw new CadFault("BENCHMARK_SCOPE_MISMATCH", "Benchmark control requires its own registered fixture");
        var doc = documents.Active(request, false, true); var state = documents.Register(doc);
        if (request.Operation == "benchmark_activate")
        {
            bool restore = request.Data.TryGetProperty("restore", out var flag) && flag.GetBoolean();
            if (!restore) benchmarkOriginal[run] = App.DocumentManager.MdiActiveDocument;
            var next = restore ? benchmarkOriginal.GetValueOrDefault(run) : doc;
            if (next is not null && !next.IsDisposed) App.DocumentManager.MdiActiveDocument = next;
            return new(request.RequestId, "completed", new { restored = restore, active_document_id = next is null ? null : documents.Register(next).Id }, documents.SessionId, state.Id, state.Revision);
        }
        string id = "benchmark_cancel_" + run;
        if (request.Operation == "benchmark_gate")
        {
            if (benchmarkGates.ContainsKey(run)) throw new CadFault("BENCHMARK_GATE_EXISTS", "Cancellation fixture already exists");
            var planned = request with { RequestId = Guid.NewGuid().ToString("N"), Operation = "cad_edit", ExpectedRevision = state.Revision,
                Data = Wire.Element(new { operation_id = id, operations_json = "[{\"op\":\"line\",\"start\":[30000,30000],\"end\":[30100,30000]}]" }) };
            journal.Begin(id, planned); mutationOwners[id] = planned.RequestId;
            benchmarkGates[run] = (planned, control.Accept(id, planned)); control.Phase(id, "benchmark_waiting_gate");
            return new(request.RequestId, "completed", new { operation_id = id, state = "queued", gate = "closed; nothing executed" }, documents.SessionId, state.Id, state.Revision);
        }
        if (!benchmarkGates.TryGetValue(run, out var gate)) throw new CadFault("BENCHMARK_GATE_MISSING", "Cancellation fixture is unavailable");
        if (!gate.Token.IsCancellationRequested) throw new CadFault("BENCHMARK_CANCEL_FAILED", "Expected actual cancellation before releasing the gate");
        benchmarkGates.Remove(run);
        var cancelled = Response.Fail(gate.Request, "CANCELLED", "Own benchmark operation cancelled at the gate before execution");
        journal.Complete(id, cancelled); mutationOwners.TryRemove(id, out _); control.Complete(id);
        return new(request.RequestId, "completed", new { operation_id = id, cancelled_before_execution = true }, documents.SessionId, state.Id, state.Revision);
    }
    private static object SnapshotMetadata(Snapshot snapshot, string units, bool cached) => new { snapshot_id = snapshot.Id, captured = snapshot.Entities.Count,
        truncated = snapshot.Truncated, coverage = snapshot.Entities.GroupBy(x => x.Text("access") ?? "unsupported").ToDictionary(x => x.Key, x => x.Count()),
        units, scope = "current_space_top_level", cached };
    private static object Selection(Document doc)
    {
        try
        {
            var selection = doc.Editor.SelectImplied();
            using var selected = selection.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK ? selection.Value : null;
            var ids = selected?.GetObjectIds() ?? [];
            return new { handles = ids.Take(500).Select(id => id.Handle.ToString()).ToArray(), count = ids.Length, truncated = ids.Length > 500, source = "implied_selection", status = selection.Status.ToString() };
        }
        catch (Autodesk.AutoCAD.Runtime.Exception e)
        { return new { handles = Array.Empty<string>(), available = false, source = "implied_selection", error = e.ErrorStatus.ToString() }; }
    }
    private Response Mutate(Request r, CancellationToken ct)
    {
        var doc = documents.Active(r, checkRevision: false);
        var state = documents.Register(doc);
        string id = EditPlan.RequiredText(r.Data, "operation_id");
        if (journal.Find(id) is not null && (!mutationOwners.TryGetValue(id, out var owner) || owner != r.RequestId))
        {
            var prior = journal.Begin(id, r)!;
            return prior.Result is { } result ? result with { RequestId = r.RequestId, Data = new { replayed = true, result = result.Data } }
                : new(r.RequestId, "queued", new { operation_id = id, state = prior.State, replayed = true }, documents.SessionId, state.Id, state.Revision);
        }
        if (lisp is { } pending && (pending.Started || ReferenceEquals(pending.Document, doc))) throw new CadFault("BUSY_LISP", pending.AwaitingApproval
            ? "AutoLISP operation " + pending.Id + " waits for the user's confirmation in AutoCAD; no other change runs until the user confirms or declines it or it is withdrawn with cad_cancel"
            : "An AutoLISP operation is queued/running; read its status before another mutation");
        documents.Active(r);
        JsonElement[]? operations = null;
        string? code = null;
        string? exportFormat = null, exportPath = null, exportLayout = null, exportMedia = null;
        string? publishFolder = null, publishLayouts = null;
        if (r.Operation == "cad_edit") { operations = EditPlan.Parse(EditPlan.RequiredText(r.Data, "operations_json")); DrawingVerification.Parse(r.Data.Text("expectations_json")); EditPlan.RequirePreviewed(r.Data, r.DocumentId, r.ExpectedRevision, previewKey); }
        else if (r.Operation == "cad_export")
        {
            exportFormat = EditPlan.RequiredText(r.Data, "format");
            exportPath = EditPlan.RequiredText(r.Data, "path");
            exportLayout = r.Data.Text("layout");
            exportMedia = r.Data.Text("media_name");
        }
        else if (r.Operation == "cad_publish")
        {
            publishFolder = EditPlan.RequiredText(r.Data, "output_folder");
            publishLayouts = EditPlan.RequiredText(r.Data, "layouts_json");
        }
        IReadOnlyList<LispFinding> lispFindings = [];
        string lispDecision = "run";
        if (r.Operation == "cad_lisp")
        {
            code = EditPlan.RequiredText(r.Data, "code");
            if (code.Length > 65536) throw new CadFault("LISP_TOO_LARGE", "AutoLISP code limit is 65536 characters");
            LispScript.ValidateBody(code);
            lispFindings = LispPolicy.Scan(code);
            LispPolicy.EnsureRunnable(lispFindings);
            // The policy belongs to the user (CADMCPLISP); an agent cannot change it.
            lispDecision = LispPolicy.Decide(CadSettings.EffectiveLispPolicy(), lispFindings);
            if (lispDecision == "deny") throw new CadFault("LISP_DISABLED", "The user disabled AutoLISP for CAD MCP (command CADMCPLISP). Use native cad_edit operations or ask the user");
            if (lispDecision == "ask" && ApprovalPresenter is null) throw new CadFault("LISP_APPROVAL_UNAVAILABLE", "AutoLISP needs the user's confirmation, which is unavailable here; set CAD_MCP_LISP_POLICY=allow for unattended runs");
        }
        bool mapOperation=operations?.Any(p=>p.Text("op")?.StartsWith("map_",StringComparison.Ordinal)==true)==true;
        if(mapOperation && operations!.Length!=1)throw new CadFault("MAP_SINGLE_OPERATION","Map API operations must run singly with a checkpoint");
        bool outsideTransaction=operations?.Any(p=>EditPlan.IsNonAtomic(p.Text("op")!))==true;
        journal.Begin(id, r);
        bool committed = false;
        object? checkpoint = null;
        // The commit is written to the receipt and the recovery record, flushed, before the drawing is redrawn:
        // if AutoCAD fails after it, both show that the change was committed in memory.
        void Progress(string phase)
        {
            if (phase == "committed")
            {
                committed = true;
                try { journal.Phase(id, phase); } catch (System.Exception error) { System.Diagnostics.Trace.WriteLine("Operation receipt: " + error.Message); }
            }
            WorkSafety.Phase(documents.SessionId, id, phase, final: committed);
        }
        try
        {
            using var locked = doc.LockDocument();
            ct.ThrowIfCancellationRequested();
            control.Phase(id, "checkpoint");
            if(WorkSafety.Required(r,operations)){using var reading=documents.ReadScope(doc);checkpoint=WorkSafety.Checkpoint(doc,documents.SessionId,state.Id,id);}
            WorkSafety.Start(documents.SessionId, r, checkpoint);
            if (code is not null)
            {
                var job = new LispJob(id, r, doc, code, lispFindings);
                lisp = job;
                var findings = lispFindings.Count == 0 ? null : lispFindings;
                if (lispDecision == "ask")
                {
                    job.AwaitingApproval = true; job.ApprovalDeadline = DateTimeOffset.UtcNow + ApprovalTimeout;
                    control.Phase(id, "awaiting_user_approval");
                    job.Approval = ApprovalPresenter!(new(id, System.IO.Path.GetFileName(doc.Name), code, lispFindings),
                        (approved, remember) => ApprovalDecided(job, approved, remember));
                    return new(r.RequestId, "queued", new { operation_id = id, state = "queued", approval = "waiting_for_user_confirmation_in_AutoCAD",
                        approval_timeout_minutes = ApprovalTimeout.TotalMinutes, findings, requires_poll = "cad_operation_status", rollback = "not_atomic",
                        cancellation = "The user can decline; cad_cancel withdraws the request before it runs" }, documents.SessionId, state.Id, state.Revision);
                }
                StartLisp(job);
                return new(r.RequestId, "queued", new { operation_id = id, state = "queued", findings, requires_poll = "cad_operation_status", rollback = "not_atomic", cancellation = "Esc in AutoCAD; queued scripts are not cancelled by stopping the model" }, documents.SessionId, state.Id, state.Revision);
            }
            journal.Running(id); control.Phase(id, "running");
            state.Author = id;
            object data;
            if(mapOperation)
            {
                using var undo=new UndoGroup(doc);data=new {transaction="map_api_not_atomic",result=VerticalEditing.Map(doc.Database,operations![0]),checkpoint};
            }
            else data = exportFormat is not null ? Exports.Execute(doc, exportFormat, exportPath!, exportLayout, exportMedia, ct)
                : publishFolder is not null ? Exports.Publish(doc, publishFolder, publishLayouts!, ct)
                : Edits.Execute(doc, operations!, ct, DrawingVerification.Parse(r.Data.Text("expectations_json")), Progress);
            // A native edit has already refreshed every generated schedule and linked table in its own transaction.
            if (operations is not null && !mapOperation) state.TakeTableChanges();
            WorkSafety.Phase(documents.SessionId, id, "completed", final: true);
            state.Author = null;
            string status = publishFolder is not null && Wire.Element(data).Text("status") == "partial" ? "partial" : "completed";
            var response = new Response(r.RequestId, status, new { operation_id = id, result = data, checkpoint, document_state = DrawingReview.DocumentState(doc) }, documents.SessionId, state.Id, state.Revision);
            return journal.Complete(id, response);
        }
        catch (System.Exception error)
        {
            if (lisp?.Id == id) { Unsubscribe(doc); lisp = null; }
            if (state.Author == id) state.Author = null;
            var fault = error is CadFault f ? f.Code : error is OperationCanceledException ? "CANCELLED" : "CAD_ERROR";
            var response = new Response(r.RequestId, "failed", new { operation_id = id, checkpoint, transaction = committed ? "committed" : mapOperation ? "map_api_outcome_unknown" : outsideTransaction ? "outside_transaction_outcome_unknown" : exportFormat is not null || publishFolder is not null ? "file_output_may_be_partial" : "not_committed", partial_changes_possible=committed||mapOperation||outsideTransaction }, documents.SessionId, state.Id, state.Revision, new(fault, error.Message));
            return journal.Complete(id, response);
        }
    }
    private Response RunCSharp(Request request, CancellationToken ct)
    {
        var doc = documents.Active(request); var state = documents.Register(doc);
        string id = EditPlan.RequiredText(request.Data, "operation_id");
        long beforeRevision = state.Revision;
        object? checkpoint = null; bool started = false, committed = false;
        System.Runtime.Loader.AssemblyLoadContext? load = null;
        try
        {
            string code = EditPlan.RequiredText(request.Data, "code");
            if (code.Length > 262144) throw new CadFault("INVALID_CSHARP", "Source exceeds 262144 characters");
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));
            if (request.Data.Text("source_sha256") != hash) throw new CadFault("CSHARP_SOURCE_MISMATCH", "Source hash changed");
            byte[] bytes = Convert.FromBase64String(EditPlan.RequiredText(request.Data, "assembly_base64"));
            if (bytes.Length > 4 * 1024 * 1024) throw new CadFault("CSHARP_TOO_LARGE", "Assembly exceeds 4 MiB");
            using var locked = doc.LockDocument(); ct.ThrowIfCancellationRequested();
            control.Phase(id, "checkpoint");
            using (documents.ReadScope(doc)) checkpoint = WorkSafety.Checkpoint(doc, documents.SessionId, state.Id, id);
            WorkSafety.Start(documents.SessionId, request, checkpoint);
            journal.Running(id); control.Phase(id, "running"); state.Author = id;
            using var undo = new UndoGroup(doc);
            using var transaction = doc.Database.TransactionManager.StartTransaction();
            started = true;
            load = new System.Runtime.Loader.AssemblyLoadContext("cadmcp-script-" + Guid.NewGuid().ToString("N"), isCollectible: true);
            using var stream = new MemoryStream(bytes);
            var assembly = load.LoadFromStream(stream);
            var method = assembly.GetType("CadMcpScript")?.GetMethod("Run", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                null, [typeof(Database), typeof(Transaction), typeof(CancellationToken)], null)
                ?? throw new CadFault("CSHARP_ENTRYPOINT", "Expected public static object CadMcpScript.Run(Database, Transaction, CancellationToken)");
            if (method.ReturnType != typeof(object))
                throw new CadFault("CSHARP_ENTRYPOINT", "Run must return object synchronously, not Task or ValueTask; return a completed serializable DTO");
            object? result;
            try { result = method.Invoke(null, [doc.Database, transaction, ct]); }
            catch (System.Reflection.TargetInvocationException error) { throw error.InnerException ?? error; }
            ct.ThrowIfCancellationRequested();
            if (result is Task or ValueTask || result is not null && result.GetType().IsGenericType && result.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>))
                throw new CadFault("CSHARP_ASYNC_RESULT", "Run returned an asynchronous operation. Return a completed DTO; do not schedule CAD work outside the supplied transaction");
            var detached = Wire.Element(result ?? new { });
            transaction.Commit(); committed = true;
            journal.Phase(id, "committed");
            WorkSafety.Phase(documents.SessionId, id, "committed", final: true);
            var changes = state.ChangesSince(beforeRevision).Where(c => c.Entity).Select(c => c.Handle.ToString("X")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var response = new Response(request.RequestId, "completed", new { operation_id = id, checkpoint, source_sha256 = hash, result = new { value = detached, changed_handles = changes.Take(500).ToArray(), changed_handles_omitted = changes.Length > 500 },
                transaction = "committed", rollback = "full_trust_code_external_effects_not_atomic", document_state = DrawingReview.DocumentState(doc) }, documents.SessionId, state.Id, state.Revision);
            return journal.Complete(id, response);
        }
        catch (System.Exception error)
        {
            return journal.Complete(id, new Response(request.RequestId, "failed", new { operation_id = id, checkpoint, transaction = committed ? "committed" : "not_committed", partial_changes_possible = started,
                note = "The host transaction aborts on failure; arbitrary code may have committed or changed external state. Reconcile before retrying." }, documents.SessionId, state.Id, state.Revision,
                new(error is CadFault fault ? fault.Code : error is OperationCanceledException ? "CANCELLED" : "CSHARP_EXECUTION_FAILED", error.Message)));
        }
        finally { if (state.Author == id) state.Author = null; load?.Unload(); }
    }
    private void StartLisp(LispJob job)
    {
        RequireBenchmarkSession(job.Request);
        if (!BenchmarkTrace.Record(job.Request, "lisp_queued")) throw new CadFault("BENCHMARK_TRACE_UNAVAILABLE", "Could not persist AutoLISP scheduling trace; script was not sent");
        job.Document.CommandCancelled += LispInterrupted;
        job.Document.CommandFailed += LispInterrupted;
        job.Document.LispWillStart += LispEvaluationStarting;
        job.Document.LispEnded += LispEvaluationEnded;
        job.Document.LispCancelled += LispEvaluationEnded;
        job.Document.SendStringToExecute(LispScript.Wrap(job.Id), false, false, false);
    }
    private void LispEvaluationStarting(object? sender, LispWillStartEventArgs e)
    {
        if (lisp is { } job && ReferenceEquals(sender, job.Document)) job.LispDepth++;
    }
    /// <summary>
    /// The wrapper reports its result before its evaluation ends, so a started job that is still open afterwards
    /// lost its report (an error escaped the wrapper). CheckLisp ends it once the drawing is idle again.
    /// </summary>
    private void LispEvaluationEnded(object? sender, EventArgs e)
    {
        if (lisp is { } job && ReferenceEquals(sender, job.Document) && --job.LispDepth <= 0 && job.Started) job.EvaluationEnded = true;
    }
    /// <summary>The user's answer, on the CAD thread. A late answer for a finished job is ignored.</summary>
    private void ApprovalDecided(LispJob job, bool approved, bool remember)
    {
        if (!ReferenceEquals(lisp, job) || !job.AwaitingApproval) return;
        job.AwaitingApproval = false; job.Approval = null;
        if (!approved) { FinishLisp(job.Id, false, "The user declined this AutoLISP operation; nothing ran", started: false, code: "LISP_DENIED"); return; }
        try { RequireBenchmarkSession(job.Request); }
        catch (CadFault error) { FinishLisp(job.Id, false, error.Message, started: false, code: error.Code); return; }
        if (remember) CadSettings.RelaxForSession();
        // The window does not block AutoCAD: the user may have edited the drawing the script was written for.
        if (job.Document.IsDisposed || documents.Register(job.Document).Revision != job.Request.ExpectedRevision)
        {
            FinishLisp(job.Id, false, "The drawing changed while the script waited for confirmation; nothing ran. Read the drawing again and resubmit", started: false, code: "REVISION_CONFLICT");
            return;
        }
        control.Phase(job.Id, "queued");
        // Sent from Idle, not from the button: the user may be in the middle of a command, whose prompt would
        // otherwise receive the script text.
        job.StartBy = DateTimeOffset.UtcNow.AddSeconds(30);
        job.PendingStart = true;
        Wake();
    }
    public string? BeginLisp(string id)
    {
        if (lisp is not { } job || job.Id != id) return null;
        try
        {
            if(control.Cancelled(id))throw new CadFault("CANCELLED","Queued AutoLISP cancelled before execution");
            if (job.AwaitingApproval) throw new CadFault("LISP_NOT_APPROVED", "The script started before the user confirmed it");
            if (job.StartBy <= DateTimeOffset.UtcNow) throw new CadFault("DEADLINE_EXPIRED", "LISP did not start before the request deadline");
            if (!ReferenceEquals(documents.Active(job.Request), job.Document)) throw new CadFault("DOCUMENT_MISMATCH", "Drawing changed before LISP execution");
            RequireBenchmarkSession(job.Request);
            if (!BenchmarkTrace.Record(job.Request, "lisp_begin")) throw new CadFault("BENCHMARK_TRACE_UNAVAILABLE", "Could not persist AutoLISP execution trace; body was not started");
            journal.Running(id);
            job.Started = true;
            RunningLispScript = id;
            documents.Register(job.Document).Author = id;
            return "(progn\n" + job.Code + "\n)";
        }
        catch (System.Exception error) { FinishLisp(id, false, error.Message, started: false, code: (error as CadFault)?.Code); return null; }
    }
    public void FinishLisp(string id, bool success, string text, bool started = true, string? code = null)
    {
        if (lisp is not { } job || job.Id != id) return;
        try
        {
        job.AwaitingApproval = false;
        try { job.Approval?.Dispose(); } catch (System.Exception error) { System.Diagnostics.Trace.WriteLine("CAD MCP: closing AutoLISP confirmation: " + error.Message); }
        job.Approval = null;
        bool sameDocument = !job.Document.IsDisposed && ReferenceEquals(Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument, job.Document);
        var revision = job.Document.IsDisposed ? (long?)null : documents.Register(job.Document).Revision;
        var response = new Response(job.Request.RequestId, success && sameDocument ? "completed" : "failed",
            new { operation_id = id, return_value = text.Substring(0, Math.Min(text.Length, 16384)), truncated = text.Length > 16384, execution_started = started,
                partial_changes_possible = started && (!success || !sameDocument), rollback = "not_atomic", verification = "read affected entities with cad_entity_get or cad_snapshot", undo = "UNDO command group; script may override grouping" },
            documents.SessionId, job.Request.DocumentId, revision,
            success && sameDocument ? null : new(code ?? (!started && control.Cancelled(id)?"CANCELLED":"LISP_FAILED"), sameDocument || !started ? text.Substring(0, Math.Min(text.Length, 16384)) : "LISP changed or closed the active document; reconcile drawings"));
        Unsubscribe(job.Document);
        if (!job.Document.IsDisposed && documents.Register(job.Document) is { } lispState && lispState.Author == id) lispState.Author = null;
        journal.Complete(id, response); _ = BenchmarkTrace.Record(job.Request, started ? "lisp_end" : "lisp_not_started", response.Error?.Code); mutationOwners.TryRemove(id, out _); control.Complete(id); WorkSafety.Phase(documents.SessionId, id, success ? "completed" : "failed", final: true);
        }
        // Cleared last, so status readers see the job as active until its receipt is written, and cleared
        // even after a failure, so a broken callback cannot block every later mutation.
        finally { if (ReferenceEquals(lisp, job)) lisp = null; if (RunningLispScript == id) RunningLispScript = null; }
    }
    private void Unsubscribe(Document doc)
    {
        if (doc.IsDisposed) return;
        doc.CommandCancelled -= LispInterrupted; doc.CommandFailed -= LispInterrupted;
        doc.LispWillStart -= LispEvaluationStarting; doc.LispEnded -= LispEvaluationEnded; doc.LispCancelled -= LispEvaluationEnded;
    }
    private void LispInterrupted(object? sender, CommandEventArgs e)
    {
        // Once the script runs, commands it calls may fail or be cancelled inside its own error handling;
        // the script still reports its result through cadmcpfinish. Only a job that has not started ends here.
        if (lisp is { Started: false } job && ReferenceEquals(sender, job.Document))
            FinishLisp(job.Id, false, "CAD command cancelled or failed before AutoLISP started: " + e.GlobalCommandName, started: false);
    }
    /// <summary>
    /// A queued script may never start: its drawing closed, or the command line was cancelled or busy. A started
    /// script cannot report when its drawing closes. Either way the job would block every later mutation.
    /// </summary>
    private void CheckLisp()
    {
        if (lisp is not { } job) return;
        if (!job.Started)
        {
            try { RequireBenchmarkSession(job.Request); }
            catch (CadFault error) { FinishLisp(job.Id, false, error.Message, started: false, code: error.Code); return; }
        }
        if (job.Document.IsDisposed)
            FinishLisp(job.Id, false, "The drawing closed before the AutoLISP operation reported its result; reconcile the drawing", job.Started);
        else if (!job.Started && control.Cancelled(job.Id))
            FinishLisp(job.Id, false, "Queued AutoLISP cancelled before execution", started: false);
        else if (job.AwaitingApproval && DateTimeOffset.UtcNow > job.ApprovalDeadline)
            FinishLisp(job.Id, false, "The user did not confirm the AutoLISP operation in time; nothing ran", started: false, code: "LISP_APPROVAL_TIMEOUT");
        else if (job.PendingStart && DateTimeOffset.UtcNow <= job.StartBy)
        {
            if (!ReferenceEquals(App.DocumentManager.MdiActiveDocument, job.Document) || !job.Document.Editor.IsQuiescent) return;
            job.PendingStart = false;
            try { StartLisp(job); }
            catch (System.Exception error) { Unsubscribe(job.Document); FinishLisp(job.Id, false, error.Message, started: false, code: (error as CadFault)?.Code); }
        }
        else if (job.PendingStart)
            FinishLisp(job.Id, false, "The confirmed AutoLISP operation could not start: its drawing was not active or AutoCAD was busy with a command; nothing ran", started: false, code: "DEADLINE_EXPIRED");
        else if (!job.Started && !job.AwaitingApproval && DateTimeOffset.UtcNow > job.StartBy + TimeSpan.FromSeconds(10))
            FinishLisp(job.Id, false, "AutoLISP did not start before the request deadline", started: false);
        else if (job.Started && job.EvaluationEnded && job.Document.Editor.IsQuiescent)
            FinishLisp(job.Id, false, "AutoLISP ended without reporting its result (an error outside the script's own error handling); reconcile the drawing", started: true);
    }
    private void DocumentClosing(Document doc)
    {
        try
        {
            if (lisp is { } job && ReferenceEquals(job.Document, doc))
                FinishLisp(job.Id, false, "The drawing was closed before the AutoLISP operation reported its result; reconcile the drawing", job.Started);
        }
        catch (System.Exception error) { System.Diagnostics.Trace.WriteLine("CAD MCP: closing drawing with AutoLISP job: " + error.Message); }
    }
    /// <summary>
    /// Keep generated schedules and linked tables current after the user edits their sources by hand.
    /// Runs only when a changed table or block reference takes part in them, so drawings without such
    /// tables, and unrelated edits, never get an extra write or undo step.
    /// </summary>
    private void RecalculateTables(Document doc)
    {
        var state = documents.Register(doc);
        if (!state.TablesDirty || state.HistoryCommand) return;
        var changed = state.TakeTableChanges();
        state.Recalculating = true;
        try
        {
            using var locked = doc.LockDocument();
            HashSet<long> managed;
            using (documents.ReadScope(doc))
            using (var check = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                managed = TableLinks.LinkedTableHandles(doc.Database, check);
                managed.UnionWith(StructuralAssemblies.ScheduleHandles(doc.Database, check));
            }
            if (managed.Count == 0 || changed is not null && !changed.Overlaps(managed)) return;
            using var undo = new UndoGroup(doc);
            using var tr = doc.Database.TransactionManager.StartTransaction();
            state.Author = "table_recalculation";
            var warnings = StructuralAssemblies.RefreshSchedules(doc.Database, tr);
            TableLinks.Recalculate(doc.Database, tr);
            tr.Commit();
            state.TableError = warnings.Count == 0 ? null : string.Join("; ", warnings);
        }
        catch (System.Exception error) { state.TableError = error.Message; System.Diagnostics.Trace.WriteLine("Table auto-recalculate: " + error.Message); }
        finally { state.Recalculating = false; if (state.Author == "table_recalculation") state.Author = null; }
    }
    public void Dispose()
    {
        App.Idle -= Idle; documents.Closing -= DocumentClosing; control.Dispose();
        App.EnterModal -= EnterModal; App.LeaveModal -= LeaveModal; App.BeginQuit -= BeginQuit; App.QuitAborted -= QuitAborted;
        if (lisp is { } script) FinishLisp(script.Id, false, "Worker stopped; reconcile any changes before retrying");
        while (queue.TryDequeue(out var job))
        {
            var response = Response.Fail(job.Request, "WORKER_STOPPED", "Queued operation did not start before the worker stopped");
            if (MutationRecovery.IsMutation(job.Request.Operation)) journal.Complete(EditPlan.RequiredText(job.Request.Data, "operation_id"), response);
            job.Source.TrySetResult(response);
        }
    }
}

internal static class NativeMethods
{
    internal const uint WM_NULL = 0;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

/// <summary>AutoLISP callbacks of the wrapper script. An exception here must neither reach AutoCAD nor leave a job pending.</summary>
internal static class LispCallbacks
{
    public static string? Begin(Dispatcher? dispatcher, ResultBuffer? args)
    {
        try { return dispatcher is not null && args?.AsArray() is [{ Value: string id }, ..] ? dispatcher.BeginLisp(id) : null; }
        catch (System.Exception error) { System.Diagnostics.Trace.WriteLine("CAD MCP: cadmcpbegin: " + error.Message); return null; }
    }
    public static int Finish(Dispatcher? dispatcher, ResultBuffer? args)
    {
        try
        {
            if (dispatcher is not null && args?.AsArray() is [{ Value: string id }, { Value: var status }, { Value: var text }])
                dispatcher.FinishLisp(id, Convert.ToInt32(status) == 1, Convert.ToString(text) ?? "");
        }
        catch (System.Exception error) { System.Diagnostics.Trace.WriteLine("CAD MCP: cadmcpfinish: " + error.Message); }
        return 0;
    }
}
