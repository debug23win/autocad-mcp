// Adapted from beiming183-cloud/AutoCAD-MCP DocumentRegistry.cs (MIT).
// Copyright (c) 2024 AutoCAD MCP Server Contributors. See licenses/beiming-MIT.txt.
// Changes: read-only contract, explicit event disposal, no mutation event suppression.
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadMcp.Core;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadMcp.AutoCAD;

internal sealed class DocumentState(Database database)
{
    public Database Database { get; } = database;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public long Revision { get; set; }
    public int ReadDepth { get; set; }
    public bool TablesDirty { get; set; }
    /// <summary>Tables and block references changed since the last recalculation; null when too many to track.</summary>
    public HashSet<long>? ChangedObjects { get; private set; } = [];
    public void TableChanged(long handle)
    {
        TablesDirty = true;
        if (ChangedObjects is { Count: >= 4096 }) ChangedObjects = null;
        ChangedObjects?.Add(handle);
    }
    /// <summary>Returns the changes recorded so far and starts a new record.</summary>
    public HashSet<long>? TakeTableChanges()
    {
        var changed = ChangedObjects;
        TablesDirty = false; ChangedObjects = [];
        return changed;
    }
    public bool Recalculating { get; set; }
    public string? TableError { get; set; }
    public bool HistoryCommand { get; set; }
    public long ReadSideEffectEvents { get; set; }

    /// <summary>The CAD MCP operation changing the drawing right now; null means the user or another program.</summary>
    public string? Author { get; set; }
    private readonly LinkedList<DrawingChange> changes = new();
    /// <summary>Changes up to this revision were dropped from the bounded log.</summary>
    public long DroppedThrough { get; private set; }
    public void Record(long handle, string kind, string type, ObjectId layer, string? layerName, bool entity)
    {
        // Consecutive events for one object (an edit opens it several times) collapse into one entry.
        if (changes.Last?.Value is { } last && last.Handle == handle && last.Kind == kind && last.Author == Author)
        {
            changes.Last.Value = layer.IsNull && layerName is null ? last with { Revision = Revision } : last with { Revision = Revision, Layer = layer, LayerName = layerName };
            return;
        }
        changes.AddLast(new DrawingChange(Revision, handle, kind, type, layer, layerName, entity, Author, DateTimeOffset.UtcNow));
        while (changes.Count > 20000) { DroppedThrough = changes.First!.Value.Revision; changes.RemoveFirst(); }
    }
    public IReadOnlyList<DrawingChange> ChangesSince(long revision) => changes.Where(c => c.Revision > revision).ToArray();
}
/// <summary>
/// One logged change. The layer is kept as an id and named when the log is read, so the event costs no lookups; an
/// object copied in from another drawing names its layer at once, before that drawing is freed.
/// </summary>
internal sealed record DrawingChange(long Revision, long Handle, string Kind, string Type, ObjectId Layer, string? LayerName, bool Entity, string? Author, DateTimeOffset At);
internal sealed class Documents : IDisposable
{
    private readonly Dictionary<Document, DocumentState> states = new();
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public Documents()
    {
        App.DocumentManager.DocumentCreated += Created;
        App.DocumentManager.DocumentToBeDestroyed += Destroyed;
        foreach (Document d in App.DocumentManager) Register(d);
    }
    private void Created(object sender, DocumentCollectionEventArgs e) => Register(e.Document);
    /// <summary>Raised on the CAD thread before a drawing closes, while it is still valid.</summary>
    public event Action<Document>? Closing;
    private void Destroyed(object sender, DocumentCollectionEventArgs e) { Closing?.Invoke(e.Document); Remove(e.Document); }
    private void Appended(object sender, ObjectEventArgs e) => Track((Database)sender, e.DBObject, "appended");
    private void Modified(object sender, ObjectEventArgs e) => Track((Database)sender, e.DBObject, "modified");
    private void Erased(object sender, ObjectErasedEventArgs e) => Track((Database)sender, e.DBObject, e.Erased ? "erased" : "unerased");
    // UNDO and REDO of a creation, and the abort of a transaction that created objects, unappend and reappend them.
    private void Unappended(object sender, ObjectEventArgs e) => Touch((Database)sender, e.DBObject, "unappended");
    private void Reappended(object sender, ObjectEventArgs e) => Touch((Database)sender, e.DBObject, "reappended");
    private static readonly Dictionary<IntPtr, string> DxfNames = new();
    /// <summary>The DXF name, cached per native class (several classes can share one managed wrapper).</summary>
    private static string DxfName(DBObject changed)
    {
        var rxClass = changed.GetRXClass();
        if (DxfNames.TryGetValue(rxClass.UnmanagedObject, out var name)) return name;
        return DxfNames[rxClass.UnmanagedObject] = rxClass.DxfName ?? changed.GetType().Name;
    }
    private void Track(Database db, DBObject changed, string kind)
    {
        Touch(db, changed, kind);
        if (changed is not (Table or BlockReference)) return;
        foreach (var state in states.Values)
            if (state.Database.UnmanagedObject == db.UnmanagedObject && state.ReadDepth == 0 && !state.Recalculating && !state.HistoryCommand)
                state.TableChanged(changed.Handle.Value);
    }
    private void CommandStarting(object sender,CommandEventArgs e)
    {
        if(sender is Document doc && states.TryGetValue(doc,out var state) && e.GlobalCommandName.TrimStart('_','.').ToUpperInvariant() is "UNDO" or "U" or "REDO" or "MREDO")
        {state.HistoryCommand=true;state.TakeTableChanges();}
    }
    private void CommandFinished(object sender,CommandEventArgs e)
    {
        if(sender is Document doc && states.TryGetValue(doc,out var state) && state.HistoryCommand && e.GlobalCommandName.TrimStart('_','.').ToUpperInvariant() is "UNDO" or "U" or "REDO" or "MREDO")
        {state.HistoryCommand=false;state.TakeTableChanges();state.TableError=null;}
    }
    private void Touch(Database db, DBObject changed, string kind)
    {
        // Document.Database may return a different managed wrapper on each access.
        // Match the native database identity, retaining the wrapper subscribed to events.
        foreach (var state in states.Values)
            if (ReferenceEquals(state.Database, db) || state.Database.UnmanagedObject == db.UnmanagedObject)
            {
                // Some object enablers emit ObjectModified while a CAD MCP read closes
                // its transaction. This is synchronous on the CAD thread, not a user edit.
                if (state.ReadDepth != 0) { state.ReadSideEffectEvents++; continue; }
                state.Revision++;
                // The change log must never disturb the edit that raised the event.
                try
                {
                    var layer = (changed as Entity)?.LayerId ?? ObjectId.Null;
                    // An object copied in from another drawing still points at that drawing's layer until it is remapped.
                    bool foreign = !layer.IsNull && layer.Database is { } owner && owner.UnmanagedObject != db.UnmanagedObject;
                    state.Record(changed.Handle.Value, kind, DxfName(changed), foreign ? ObjectId.Null : layer, foreign ? ((Entity)changed).Layer : null, changed is Entity);
                }
                catch (System.Exception error) { System.Diagnostics.Trace.WriteLine("CAD MCP change log: " + error.Message); }
            }
    }
    public IDisposable ReadScope(Document document)
    {
        var state = Register(document);
        state.ReadDepth++;
        return new ReadGuard(state);
    }
    private sealed class ReadGuard(DocumentState state) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            state.ReadDepth--;
        }
    }
    public DocumentState Register(Document d)
    {
        if (states.TryGetValue(d, out var state)) return state;
        // Linked tables are recalculated after changes, not when a drawing is opened: opening must not modify it.
        state = new(d.Database); states.Add(d, state);
        d.CommandWillStart+=CommandStarting;d.CommandEnded+=CommandFinished;d.CommandCancelled+=CommandFinished;d.CommandFailed+=CommandFinished;
        state.Database.ObjectAppended += Appended; state.Database.ObjectModified += Modified; state.Database.ObjectErased += Erased;
        state.Database.ObjectUnappended += Unappended; state.Database.ObjectReappended += Reappended;
        return state;
    }
    private void Remove(Document d)
    {
        if (!states.TryGetValue(d, out var state)) return;
        d.CommandWillStart-=CommandStarting;d.CommandEnded-=CommandFinished;d.CommandCancelled-=CommandFinished;d.CommandFailed-=CommandFinished;
        state.Database.ObjectAppended -= Appended; state.Database.ObjectModified -= Modified; state.Database.ObjectErased -= Erased;
        state.Database.ObjectUnappended -= Unappended; state.Database.ObjectReappended -= Reappended;
        states.Remove(d);
    }
    public object Catalog() => states.Select(p=>new {document_id=p.Value.Id,name=p.Key.Name,active=ReferenceEquals(p.Key,App.DocumentManager.MdiActiveDocument),revision=p.Value.Revision,dark_theme=Convert.ToInt32(App.GetSystemVariable("COLORTHEME"))==0,
        project_key=System.IO.Path.IsPathFullyQualified(p.Key.Name)?Portable.Hash(System.Text.Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(p.Key.Name).ToUpperInvariant())):p.Value.Id}).ToArray();
    public Document Active(Request r, bool checkRevision = true, bool allowInactive = false)
    {
        if (r.SessionId != SessionId) throw new CadFault("SESSION_MISMATCH", "Native worker session changed");
        var d = allowInactive && r.DocumentId is not null ? states.FirstOrDefault(p=>p.Value.Id==r.DocumentId).Key ?? throw new CadFault("DOCUMENT_CLOSED", "The requested drawing is no longer open") : App.DocumentManager.MdiActiveDocument ?? throw new CadFault("NO_DOCUMENT", "No active drawing");
        var state = Register(d);
        if (r.Operation != "cad_context")
        {
            if (r.DocumentId != state.Id) throw new CadFault("DOCUMENT_MISMATCH", "Requested drawing is not active");
            if (checkRevision && r.ExpectedRevision != state.Revision) throw new CadFault("REVISION_CONFLICT", "Refresh document context");
        }
        return d;
    }
    public void Dispose()
    {
        App.DocumentManager.DocumentCreated -= Created;
        App.DocumentManager.DocumentToBeDestroyed -= Destroyed;
        foreach (var d in states.Keys.ToArray()) Remove(d);
    }
}
