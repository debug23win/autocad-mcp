using System.Collections.Concurrent;
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
    public void Start() => App.Idle += Idle;
    public async Task<Response> Enqueue(Request r, CancellationToken ct)
    {
        if (queue.Count >= 32) throw new CadFault("BUSY", "CAD queue is full");
        var source = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue((r, ct, source));
        return await source.Task.WaitAsync(ct);
    }
    private void Idle(object? sender, EventArgs e)
    {
        // All database access occurs here, on the CAD application thread.
        if (!queue.TryPeek(out var pending)) return;
        if (pending.Token.IsCancellationRequested || pending.Request.Deadline <= DateTimeOffset.UtcNow)
        { queue.TryDequeue(out _); pending.Source.TrySetCanceled(); return; }
        var active = App.DocumentManager.MdiActiveDocument;
        if (active is not null && !active.Editor.IsQuiescent) return;
        if (!queue.TryDequeue(out var job)) return;
        try { job.Source.TrySetResult(Execute(job.Request, job.Token)); }
        catch (CadFault error) { job.Source.TrySetResult(Response.Fail(job.Request, error.Code, error.Message)); }
        catch (System.Exception error) { job.Source.TrySetResult(Response.Fail(job.Request, "CAD_ERROR", error.Message)); }
    }
    private Response Execute(Request r, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var doc = documents.Active(r); var state = documents.Register(doc);
        using var locked = doc.LockDocument();
        using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
        object data; string status = "completed";
        switch (r.Operation)
        {
            case "cad_context":
                using (var view = doc.Editor.GetCurrentView())
                    data = new { name = doc.Name, units = doc.Database.Insunits.ToString(), space = doc.Database.CurrentSpaceId == ((BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace] ? "model" : "paper",
                        ucs_to_wcs = doc.Editor.CurrentUserCoordinateSystem.ToArray(),
                        view = new { width = view.Width, height = view.Height, perspective = view.PerspectiveEnabled },
                        capabilities = new[] { "cad_context", "cad_snapshot", "cad_query", "cad_entity_get", "cad_focus", "cad_render" },
                        limitations = new[] { "preview_render_unverified", "top_level_entities_only", "SPDS_special_properties_unverified", "current_space_only" } };
                break;
            case "cad_render":
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
            case "cad_snapshot":
                int limit = r.Data.Number("limit", 2000);
                if (limit < 1 || limit > 5000) throw new CadFault("INVALID_LIMIT", "limit must be 1..5000");
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
                var snapshot = snapshots.Add(state.Id, state.Revision, entities, truncated);
                var coverage = snapshot.Entities.GroupBy(x => x.Text("access") ?? "unsupported").ToDictionary(x => x.Key, x => x.Count());
                // Return metadata only; pages are bounded and fetched through cad_query.
                data = new { snapshot_id = snapshot.Id, captured = snapshot.Entities.Count, truncated = snapshot.Truncated, coverage, units = doc.Database.Insunits.ToString(), scope = "current_space_top_level" };
                if (snapshot.Truncated || snapshot.Entities.Any(x => x.Text("access") != "structured")) status = "partial";
                break;
            case "cad_query":
                data = snapshots.Query(r.Data.Text("snapshot_id") ?? "", state.Id, state.Revision, r.Data); break;
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
        if (r.ExpectedRevision.HasValue && r.ExpectedRevision != state.Revision)
            throw new CadFault("REVISION_CONFLICT", "Document changed during operation; discard result and refresh context");
        return new(r.RequestId, status, data, documents.SessionId, state.Id, state.Revision);
    }
    public void Dispose()
    {
        App.Idle -= Idle;
        while (queue.TryDequeue(out var job)) job.Source.TrySetCanceled();
    }
}
