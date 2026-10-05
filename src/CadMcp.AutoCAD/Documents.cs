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
    public bool Recalculating { get; set; }
    public string? TableError { get; set; }
    public bool HistoryCommand { get; set; }
    public long ReadSideEffectEvents { get; set; }
}
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
    private void Destroyed(object sender, DocumentCollectionEventArgs e) => Remove(e.Document);
    private void Changed(object sender, ObjectEventArgs e)
    {
        var db=(Database)sender;Touch(db);
        if(e.DBObject is Table or BlockReference)foreach(var state in states.Values)if(state.Database.UnmanagedObject==db.UnmanagedObject && state.ReadDepth==0&&!state.Recalculating&&!state.HistoryCommand)state.TablesDirty=true;
    }
    private void Erased(object sender, ObjectErasedEventArgs e)
    {var db=(Database)sender;Touch(db);if(e.DBObject is Table or BlockReference)foreach(var state in states.Values)if(state.Database.UnmanagedObject==db.UnmanagedObject&&state.ReadDepth==0&&!state.Recalculating&&!state.HistoryCommand)state.TablesDirty=true;}
    private void CommandStarting(object sender,CommandEventArgs e)
    {
        if(sender is Document doc && states.TryGetValue(doc,out var state) && e.GlobalCommandName.TrimStart('_','.').ToUpperInvariant() is "UNDO" or "U" or "REDO" or "MREDO")
        {state.HistoryCommand=true;state.TablesDirty=false;}
    }
    private void CommandFinished(object sender,CommandEventArgs e)
    {
        if(sender is Document doc && states.TryGetValue(doc,out var state) && state.HistoryCommand && e.GlobalCommandName.TrimStart('_','.').ToUpperInvariant() is "UNDO" or "U" or "REDO" or "MREDO")
        {state.HistoryCommand=false;state.TablesDirty=false;state.TableError=null;}
    }
    private void Touch(Database db)
    {
        // Document.Database may return a different managed wrapper on each access.
        // Match the native database identity, retaining the wrapper subscribed to events.
        foreach (var state in states.Values)
            if (ReferenceEquals(state.Database, db) || state.Database.UnmanagedObject == db.UnmanagedObject)
            {
                // Some object enablers emit ObjectModified while a CAD MCP read closes
                // its transaction. This is synchronous on the CAD thread, not a user edit.
                if (state.ReadDepth != 0) state.ReadSideEffectEvents++;
                else state.Revision++;
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
        state = new(d.Database) { TablesDirty=true }; states.Add(d, state);
        d.CommandWillStart+=CommandStarting;d.CommandEnded+=CommandFinished;d.CommandCancelled+=CommandFinished;d.CommandFailed+=CommandFinished;
        state.Database.ObjectAppended += Changed; state.Database.ObjectModified += Changed; state.Database.ObjectErased += Erased;
        return state;
    }
    private void Remove(Document d)
    {
        if (!states.TryGetValue(d, out var state)) return;
        d.CommandWillStart-=CommandStarting;d.CommandEnded-=CommandFinished;d.CommandCancelled-=CommandFinished;d.CommandFailed-=CommandFinished;
        state.Database.ObjectAppended -= Changed; state.Database.ObjectModified -= Changed; state.Database.ObjectErased -= Erased;
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
