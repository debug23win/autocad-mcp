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
    private void Changed(object sender, ObjectEventArgs e) => Touch((Database)sender);
    private void Erased(object sender, ObjectErasedEventArgs e) => Touch((Database)sender);
    private void Touch(Database db)
    {
        // Document.Database may return a different managed wrapper on each access.
        // Match the native database identity, retaining the wrapper subscribed to events.
        foreach (var state in states.Values)
            if (ReferenceEquals(state.Database, db) || state.Database.UnmanagedObject == db.UnmanagedObject) state.Revision++;
    }
    public DocumentState Register(Document d)
    {
        if (states.TryGetValue(d, out var state)) return state;
        state = new(d.Database); states.Add(d, state);
        state.Database.ObjectAppended += Changed; state.Database.ObjectModified += Changed; state.Database.ObjectErased += Erased;
        return state;
    }
    private void Remove(Document d)
    {
        if (!states.TryGetValue(d, out var state)) return;
        state.Database.ObjectAppended -= Changed; state.Database.ObjectModified -= Changed; state.Database.ObjectErased -= Erased;
        states.Remove(d);
    }
    public Document Active(Request r, bool checkRevision = true)
    {
        if (r.SessionId != SessionId) throw new CadFault("SESSION_MISMATCH", "Native worker session changed");
        var d = App.DocumentManager.MdiActiveDocument ?? throw new CadFault("NO_DOCUMENT", "No active drawing");
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
