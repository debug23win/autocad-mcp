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
        try
        {
            documents = new(); dispatcher = new(documents) { ApprovalPresenter = LispApprovalWindow.Show }; dispatcher.Start();
            var pipe = "cadmcp-worker-" + Environment.ProcessId + "-" + documents.SessionId;
            server = new(pipe, dispatcher.Enqueue); server.Start();
            descriptorPath = Wire.PublishWorker(new WorkerDescriptor(documents.SessionId, pipe, Environment.ProcessId, Wire.Version));
            App.Idle += AddRibbon;
        }
        catch (System.Exception e)
        {
            Terminate(); App.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCAD MCP initialization failed: " + e.Message);
        }
    }
    private static void AddRibbon(object? sender, EventArgs e)
    {
        // An AutoCAD event handler: nothing may escape it.
        try
        {
            if (ComponentManager.Ribbon is not { } control) return;
            App.Idle -= AddRibbon;
            ribbon = new RibbonTab { Id = "CADMCP_READ_PROTOTYPE", Title = "CAD MCP" };
            var source = new RibbonPanelSource { Title = "Ассистент" };
            source.Items.Add(new RibbonButton { Text = "Открыть чат", ShowText = true, CommandHandler = new OpenChat() });
            ribbon.Panels.Add(new RibbonPanel { Source = source }); control.Tabs.Add(ribbon);
        }
        catch (System.Exception error) { App.Idle -= AddRibbon; ribbon = null; System.Diagnostics.Trace.WriteLine("CAD MCP ribbon: " + error); }
    }
    [CommandMethod("CADMCPCHAT", CommandFlags.Session)]
    public static void ShowChat()
    {
        // Also runs from the ribbon button's handler, where an exception would reach AutoCAD's UI.
        try
        {
            if (palette is null)
            {
                palette = new PaletteSet("CAD MCP — ассистент чертежей");
                panel = new ChatWorkspace
                {
                    CadSessionId = documents?.SessionId,
                    DarkThemeProvider = () => Convert.ToInt32(App.GetSystemVariable("COLORTHEME")) == 0
                };
                palette.AddVisual("Чат", panel);
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
    /// <summary>The user's autosave choice: a recovery copy (default), saving the DWG itself, or off. Agents cannot change it.</summary>
    [CommandMethod("CADMCPAUTOSAVE", CommandFlags.Session)]
    public static void AutoSaveCommand() => AutoSavePrompt.Run();
    [LispFunction("CADMCPBEGIN")]
    public static string? BeginLisp(ResultBuffer args) => LispCallbacks.Begin(dispatcher, args);
    [LispFunction("CADMCPFINISH")]
    public static int FinishLisp(ResultBuffer args) => LispCallbacks.Finish(dispatcher, args);
    public void Terminate()
    {
        App.Idle -= AddRibbon;
        // Runs while AutoCAD shuts down: each step is independent, and none may end the process.
        void Step(Action action) { try { action(); } catch (System.Exception error) { System.Diagnostics.Trace.WriteLine("CAD MCP shutdown: " + error.Message); } }
        Step(() => panel?.Stop()); Step(() => server?.Dispose()); Step(() => dispatcher?.Dispose()); Step(() => documents?.Dispose());
        Step(() => { if (ribbon is not null) ComponentManager.Ribbon?.Tabs.Remove(ribbon); });
        Step(() => palette?.Dispose()); palette = null;
        if (descriptorPath is not null) try { File.Delete(descriptorPath); } catch (IOException) { }
    }
    private sealed class OpenChat : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => ShowChat();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
