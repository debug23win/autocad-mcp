using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadMcp.AutoCAD;

internal sealed class UndoGroup : IDisposable
{
    private readonly object? document = null;
    public bool Grouped => document is not null;
    public UndoGroup(Document doc)
    {
        // Core Console has no ActiveX document. Its caller must validate Undo separately in GUI CAD.
        int flags = Convert.ToInt32(App.GetSystemVariable("UNDOCTL"));
        if ((flags & 1) == 0 || (flags & 8) != 0) return;
#if !CORE_CONSOLE
        try { document = Autodesk.AutoCAD.ApplicationServices.DocumentExtension.GetAcadDocument(doc); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return; }
#endif
        if (document is not null) document.GetType().InvokeMember("StartUndoMark", BindingFlags.InvokeMethod, null, document, null);
    }
    public void Dispose()
    {
        // EndUndoMark is bookkeeping after the database transaction. Never report a committed
        // drawing change as rolled back just because closing the COM undo mark failed.
        if (document is not null)
            try { document.GetType().InvokeMember("EndUndoMark", BindingFlags.InvokeMethod, null, document, null); }
            catch (System.Exception error) { App.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCAD MCP: EndUndoMark: " + error.Message); }
    }
}
