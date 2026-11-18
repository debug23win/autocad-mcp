using System.Windows.Input;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Windows;
using Autodesk.Windows;
using CadMcp.Core;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(CadMcp.AutoCAD.Plugin))]
[assembly: CommandClass(typeof(CadMcp.AutoCAD.Plugin))]

namespace CadMcp.AutoCAD;

public sealed class Plugin : IExtensionApplication
{
    private static Documents? documents;
    private static Dispatcher? dispatcher;
    private static PipeServer? server;
    private static PaletteSet? palette;
    private static ChatWorkspace? panel;
    private static RibbonTab? ribbon;
    private static string? descriptorPath;
    public void Initialize()
    {
        if (IsolatedCSharpWorker.TryStart()) return;
        try
        {
            App.Idle += AddRibbon;
        }
        catch (System.Exception e)
        {
            Terminate(); App.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCAD MCP initialization failed: " + e.Message);
        }
    }
    // Called only by the explicit chat command/ribbon action, on AutoCAD's UI thread.
    private static void EnsureWorker()
    {
        if (server is not null) return;
        try
        {
            documents = new();
            dispatcher = new(documents) { ApprovalPresenter = LispApprovalWindow.Show };
            dispatcher.Start();
            string pipe = "cadmcp-worker-" + Environment.ProcessId + "-" + documents.SessionId;
            server = new(pipe, dispatcher.Enqueue);
            server.Start();
            descriptorPath = Wire.PublishWorker(new WorkerDescriptor(documents.SessionId, pipe, Environment.ProcessId, Wire.Version));
        }
        catch
        {
            StopWorker();
            throw;
        }
    }
    private static void StopWorker()
    {
        void Step(Action action) { try { action(); } catch (System.Exception error) { System.Diagnostics.Trace.WriteLine("CAD MCP worker shutdown: " + error.Message); } }
        Step(() => server?.Dispose()); server = null;
        Step(() => dispatcher?.Dispose()); dispatcher = null;
        Step(() => documents?.Dispose()); documents = null;
        if (descriptorPath is { } path) Step(() => File.Delete(path));
        descriptorPath = null;
    }
    private static void AddRibbon(object? sender, EventArgs e)
    {
        if (ComponentManager.Ribbon is not { } control) return;
        App.Idle -= AddRibbon;
        // An AutoCAD event handler: nothing may escape it.
        try
        {
            ribbon = new RibbonTab { Id = "CADMCP_READ_PROTOTYPE", Title = "CAD MCP" };
            var source = new RibbonPanelSource { Title = UiText.T("Ассистент") };
            source.Items.Add(new RibbonButton { Text = UiText.T("Открыть чат"), ShowText = true, CommandHandler = new OpenChat() });
            ribbon.Panels.Add(new RibbonPanel { Source = source }); control.Tabs.Add(ribbon);
        }
        catch (System.Exception error) { ribbon = null; App.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCAD MCP ribbon: " + error.Message + "; use CADMCPCHAT"); }
    }
    [CommandMethod("CADMCPCHAT", CommandFlags.Session)]
    public static void ShowChat()
    {
        // Also runs from the ribbon button's handler, where an exception would reach AutoCAD's UI.
        try
        {
            EnsureWorker();
            if (palette is null)
            {
                palette = new PaletteSet(UiText.T("CAD MCP — ассистент чертежей"));
                panel = new ChatWorkspace
                {
                    CadSessionId = documents?.SessionId,
                    DarkThemeProvider = () => Convert.ToInt32(App.GetSystemVariable("COLORTHEME")) == 0
                };
                palette.AddVisual(UiText.T("Чат"), panel);
                palette.MinimumSize = new System.Drawing.Size(360, 480);
            }
            palette.Visible = true;
            _ = panel!.Refresh();
        }
        catch (System.Exception error) { App.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCAD MCP chat: " + error.Message); }
    }
    /// <summary>The user's AutoLISP policy for agents. Agents cannot change it; CAD_MCP_LISP_POLICY overrides it for unattended runs.</summary>
    [CommandMethod("CADMCPLISP", CommandFlags.Session)]
    public static void LispPolicyCommand() => LispPolicyPrompt.Run();
    [LispFunction("CADMCPBEGIN")]
    public static string? BeginLisp(ResultBuffer args) => LispCallbacks.Begin(dispatcher, args);
    [LispFunction("CADMCPFINISH")]
    public static int FinishLisp(ResultBuffer args) => LispCallbacks.Finish(dispatcher, args);
    public void Terminate()
    {
        App.Idle -= AddRibbon;
        // Runs while AutoCAD shuts down: each step is independent, and none may end the process.
        void Step(Action action) { try { action(); } catch (System.Exception error) { System.Diagnostics.Trace.WriteLine("CAD MCP shutdown: " + error.Message); } }
        Step(() => panel?.Stop()); StopWorker();
        Step(() => { if (ribbon is not null) ComponentManager.Ribbon?.Tabs.Remove(ribbon); });
        Step(() => palette?.Dispose()); palette = null;
        panel = null; ribbon = null;
    }
    private sealed class OpenChat : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => ShowChat();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
