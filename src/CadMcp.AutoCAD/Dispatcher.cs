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
    private readonly ConcurrentQueue<(Request Request, CancellationToken Token, TaskCompletionSource<Response> Source)> queue = new();
    private readonly SnapshotStore snapshots = new();
    private readonly OperationJournal journal = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CadMcp", "operations", documents.SessionId));
    private readonly ResultArchive archive = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CadMcp", "results", documents.SessionId));
    private readonly RevisionCache<JsonElement> catalogCache = new();
    private readonly RevisionCache<JsonElement> searchCache = new(8);
    private sealed record LispJob(string Id, Request Request, Document Document, string Code);
    private LispJob? lisp;
    public void Start() => App.Idle += Idle;
    public async Task<Response> Enqueue(Request r, CancellationToken ct)
    {
        if (queue.Count >= 32) throw new CadFault("BUSY", "CAD queue is full");
        var source = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue((r, ct, source));
        return await Portable.Await(source.Task, ct);
    }
    private void Idle(object? sender, EventArgs e)
    {
        // All database access occurs here, on the CAD application thread.
        if (!queue.TryPeek(out var pending)) return;
        if (pending.Token.IsCancellationRequested || pending.Request.Deadline <= DateTimeOffset.UtcNow)
        { queue.TryDequeue(out _); pending.Source.TrySetCanceled(); return; }
        var active = App.DocumentManager.MdiActiveDocument;
        if (pending.Request.Operation != "cad_operation_status" && active is not null && !active.Editor.IsQuiescent) return;
        if (!queue.TryDequeue(out var job)) return;
        try { job.Source.TrySetResult(Execute(job.Request, job.Token)); }
        catch (CadFault error) { job.Source.TrySetResult(Response.Fail(job.Request, error.Code, error.Message)); }
        catch (System.Exception error) { job.Source.TrySetResult(Response.Fail(job.Request, "CAD_ERROR", error.Message)); }
    }
    private Response Execute(Request r, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (r.Operation == "cad_operation_status")
        {
            if (r.SessionId != documents.SessionId) throw new CadFault("SESSION_MISMATCH", "Native worker session changed");
            var entry = journal.Find(EditPlan.RequiredText(r.Data, "operation_id"));
            if (entry is not null && entry.DocumentId != r.DocumentId) throw new CadFault("DOCUMENT_MISMATCH", "Operation belongs to a different drawing");
            return new(r.RequestId, "completed", new { state = entry?.State ?? "not_found", result = entry?.Result,
                persistence = journal.Persistence, retry = "Never automatically retry an unknown mutation; inspect drawing first" }, documents.SessionId, r.DocumentId);
        }
        if (r.Operation is "cad_edit" or "cad_lisp") return Mutate(r, ct);
        bool readOnly = r.Operation is "cad_context" or "cad_catalog" or "cad_render" or "cad_snapshot" or
            "cad_query" or "cad_search" or "cad_result_get" or "cad_entity_get";
        var doc = documents.Active(r, checkRevision: !readOnly); var state = documents.Register(doc);
        using var reading = readOnly ? documents.ReadScope(doc) : null;
        using var locked = doc.LockDocument();
        using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
        object data; string status = "completed";
        switch (r.Operation)
        {
            case "cad_context":
                using (var view = doc.Editor.GetCurrentView())
                    data = new { name = doc.Name, acad_version = Convert.ToString(App.GetSystemVariable("ACADVER")), runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                        units = doc.Database.Insunits.ToString(), space = doc.Database.CurrentSpaceId == ((BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace] ? "model" : "paper",
                        selection = Selection(doc),
                        ucs_to_wcs = doc.Editor.CurrentUserCoordinateSystem.ToArray(),
                        view = new { width = view.Width, height = view.Height, perspective = view.PerspectiveEnabled },
                        capabilities = new[] { "cad_context", "cad_snapshot", "cad_query", "cad_search", "cad_result_get", "cad_entity_get", "cad_focus", "cad_render", "cad_catalog", "cad_edit", "cad_lisp", "cad_operation_status" },
                        editing = new { coordinates = "WCS", units = "drawing_units", angles = "degrees", native_transaction = true, lisp_atomic = false, operation_records = journal.Count, journal = journal.Persistence, pending_lisp = lisp?.Id },
                        cache = new { catalog_hits = catalogCache.Hits, catalog_misses = catalogCache.Misses, search_hits = searchCache.Hits, search_misses = searchCache.Misses, invalidation = "document_revision_and_space", render_cached = false,
                            ignored_read_side_effect_events = state.ReadSideEffectEvents },
                        limitations = new[] { "preview_render_unverified", "SPDS_special_properties_unverified", "native_edits_current_space_only" } };
                break;
            case "cad_catalog":
                bool catalogHit = catalogCache.TryGet(state.Id, "catalog", state.Revision, out var catalog);
                if (!catalogHit) { catalog = Wire.Element(Catalog.Read(doc.Database, tr)); catalogCache.Put(state.Id, "catalog", state.Revision, catalog); }
                data = new { catalog, cached = catalogHit }; break;
            case "cad_render":
#if CORE_CONSOLE
                throw new CadFault("GUI_REQUIRED", "CapturePreviewImage requires the interactive AutoCAD document");
#else
                int width = r.Data.Number("width", 1024), height = r.Data.Number("height", 768);
                if (width < 128 || width > 1600 || height < 128 || height > 1600)
                    throw new CadFault("INVALID_IMAGE_SIZE", "Image dimensions must be 128..1600");
                using (var view = doc.Editor.GetCurrentView())
                using (var bitmap = doc.CapturePreviewImage((uint)width, (uint)height))
                using (var png = new MemoryStream())
                {
                    bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                    if (png.Length > 4 * 1024 * 1024) throw new CadFault("IMAGE_TOO_LARGE", "Request smaller dimensions");
                    data = new { mime_type = "image/png", image_base64 = Convert.ToBase64String(png.ToArray()), width, height,
                        source = "AutoCAD.Document.CapturePreviewImage", captured_at = DateTimeOffset.UtcNow,
                        view = new { width = view.Width, height = view.Height, center = new[] { view.CenterPoint.X, view.CenterPoint.Y },
                            target = new[] { view.Target.X, view.Target.Y, view.Target.Z }, direction = new[] { view.ViewDirection.X, view.ViewDirection.Y, view.ViewDirection.Z },
                            twist = view.ViewTwist, perspective = view.PerspectiveEnabled },
                        limitations = new[] { "preview_fidelity_requires_live_validation", "pixel_to_world_mapping_not_provided" } };
                }
                status = "partial";
                break;
#endif
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
                        if (tr.GetObject(id, OpenMode.ForRead) is Entity entity) entities.Add(Reader.Read(entity, tr));
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
                if (tr.GetObject(objectId, OpenMode.ForRead) is not Entity found || found.OwnerId != doc.Database.CurrentSpaceId)
                    throw new CadFault("UNSUPPORTED_SCOPE", "Only current-space top-level entities are supported");
                if (r.Operation == "cad_focus") { doc.Editor.SetImpliedSelection([objectId]); data = new { handle = found.Handle.ToString(), selected = true }; }
                else { var entityData = Reader.Read(found, tr); data = entityData; if (entityData.Text("access") != "structured") status = "partial"; }
                break;
            default: throw new CadFault("UNSUPPORTED_OPERATION", r.Operation);
        }
        if (!readOnly && r.ExpectedRevision.HasValue && r.ExpectedRevision != state.Revision)
            throw new CadFault("REVISION_CONFLICT", "Document changed during operation; discard result and refresh context");
        if (r.Operation != "cad_render" && Wire.Element(data).GetRawText().Length > 512 * 1024)
        {
            var archiveId = archive.Put(documents.SessionId, state.Id, state.Revision, data);
            data = new { archive_id = archiveId, captured_revision = state.Revision, detail = "Result archived; use cad_result_get to read bounded JSON text pages" }; status = "partial";
        }
        return new(r.RequestId, status, data, documents.SessionId, state.Id, state.Revision);
    }
    private static object SnapshotMetadata(Snapshot snapshot, string units, bool cached) => new { snapshot_id = snapshot.Id, captured = snapshot.Entities.Count,
        truncated = snapshot.Truncated, coverage = snapshot.Entities.GroupBy(x => x.Text("access") ?? "unsupported").ToDictionary(x => x.Key, x => x.Count()),
        units, scope = "current_space_top_level", cached };
    private static object Selection(Document doc)
    {
        try
        {
            var selection = doc.Editor.SelectImplied();
            var ids = selection.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK ? selection.Value.GetObjectIds() : [];
            return new { handles = ids.Take(500).Select(id => id.Handle.ToString()), count = ids.Length, truncated = ids.Length > 500, source = "implied_selection", status = selection.Status.ToString() };
        }
        catch (Autodesk.AutoCAD.Runtime.Exception e)
        { return new { handles = Array.Empty<string>(), available = false, source = "implied_selection", error = e.ErrorStatus.ToString() }; }
    }
    private Response Mutate(Request r, CancellationToken ct)
    {
        var doc = documents.Active(r, checkRevision: false);
        var state = documents.Register(doc);
        string id = EditPlan.RequiredText(r.Data, "operation_id");
        if (journal.Find(id) is not null)
        {
            var prior = journal.Begin(id, r)!;
            return prior.Result is { } result ? result with { RequestId = r.RequestId, Data = new { replayed = true, result = result.Data } }
                : new(r.RequestId, "queued", new { operation_id = id, state = prior.State, replayed = true }, documents.SessionId, state.Id, state.Revision);
        }
        if (lisp is not null) throw new CadFault("BUSY_LISP", "An AutoLISP operation is queued/running; read its status before another mutation");
        documents.Active(r);
        JsonElement[]? operations = null;
        string? code = null;
        if (r.Operation == "cad_edit") operations = EditPlan.Parse(EditPlan.RequiredText(r.Data, "operations_json"));
        else
        {
            code = EditPlan.RequiredText(r.Data, "code");
            if (code.Length > 65536) throw new CadFault("LISP_TOO_LARGE", "AutoLISP code limit is 65536 characters");
        }
        journal.Begin(id, r);
        try
        {
            using var locked = doc.LockDocument();
            if (code is not null)
            {
                lisp = new(id, r, doc, code);
                doc.CommandCancelled += LispInterrupted;
                doc.CommandFailed += LispInterrupted;
                doc.SendStringToExecute(LispScript.Wrap(id), false, false, false);
                return new(r.RequestId, "queued", new { operation_id = id, state = "queued", requires_poll = "cad_operation_status", rollback = "not_atomic", cancellation = "Esc in AutoCAD; queued scripts are not cancelled by stopping the model" }, documents.SessionId, state.Id, state.Revision);
            }
            journal.Running(id);
            var data = Edits.Execute(doc, operations!, ct);
            var response = new Response(r.RequestId, "completed", new { operation_id = id, result = data }, documents.SessionId, state.Id, state.Revision);
            return journal.Complete(id, response);
        }
        catch (System.Exception error)
        {
            if (lisp?.Id == id) { doc.CommandCancelled -= LispInterrupted; doc.CommandFailed -= LispInterrupted; lisp = null; }
            var fault = error is CadFault f ? f.Code : error is OperationCanceledException ? "CANCELLED" : "CAD_ERROR";
            var response = new Response(r.RequestId, "failed", new { operation_id = id, transaction = "not_committed" }, documents.SessionId, state.Id, state.Revision, new(fault, error.Message));
            return journal.Complete(id, response);
        }
    }
    public string? BeginLisp(string id)
    {
        if (lisp is not { } job || job.Id != id) return null;
        try
        {
            if (job.Request.Deadline <= DateTimeOffset.UtcNow) throw new CadFault("DEADLINE_EXPIRED", "LISP did not start before the request deadline");
            if (!ReferenceEquals(documents.Active(job.Request), job.Document)) throw new CadFault("DOCUMENT_MISMATCH", "Drawing changed before LISP execution");
            journal.Running(id);
            return "(progn\n" + job.Code + "\n)";
        }
        catch (System.Exception error) { FinishLisp(id, false, error.Message, started: false); return null; }
    }
    public void FinishLisp(string id, bool success, string text, bool started = true)
    {
        if (lisp is not { } job || job.Id != id) return;
        bool sameDocument = !job.Document.IsDisposed && ReferenceEquals(Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument, job.Document);
        var revision = job.Document.IsDisposed ? (long?)null : documents.Register(job.Document).Revision;
        var response = new Response(job.Request.RequestId, success && sameDocument ? "completed" : "failed",
            new { operation_id = id, return_value = text.Substring(0, Math.Min(text.Length, 16384)), truncated = text.Length > 16384, execution_started = started,
                partial_changes_possible = started && (!success || !sameDocument), rollback = "not_atomic", verification = "read affected entities with cad_entity_get or cad_snapshot", undo = "UNDO command group; script may override grouping" },
            documents.SessionId, job.Request.DocumentId, revision,
            success && sameDocument ? null : new("LISP_FAILED", sameDocument ? text.Substring(0, Math.Min(text.Length, 16384)) : "LISP changed or closed the active document; reconcile drawings"));
        job.Document.CommandCancelled -= LispInterrupted; job.Document.CommandFailed -= LispInterrupted;
        journal.Complete(id, response); lisp = null;
    }
    private void LispInterrupted(object? sender, CommandEventArgs e)
    {
        if (lisp is { } job && ReferenceEquals(sender, job.Document))
            FinishLisp(job.Id, false, "CAD command cancelled or failed: " + e.GlobalCommandName, journal.Find(job.Id)?.State == "running");
    }
    public void Dispose()
    {
        App.Idle -= Idle;
        if (lisp is { } script) FinishLisp(script.Id, false, "Worker stopped; reconcile any changes before retrying");
        while (queue.TryDequeue(out var job)) job.Source.TrySetCanceled();
    }
}
