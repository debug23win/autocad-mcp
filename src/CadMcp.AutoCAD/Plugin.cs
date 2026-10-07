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
            documents = new(); dispatcher = new(documents); dispatcher.Start();
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
        if (ComponentManager.Ribbon is not { } control) return;
        App.Idle -= AddRibbon;
        ribbon = new RibbonTab { Id = "CADMCP_READ_PROTOTYPE", Title = "CAD MCP" };
        var source = new RibbonPanelSource { Title = "Ассистент" };
        source.Items.Add(new RibbonButton { Text = "Открыть чат", ShowText = true, CommandHandler = new OpenChat() });
        ribbon.Panels.Add(new RibbonPanel { Source = source }); control.Tabs.Add(ribbon);
    }
    [CommandMethod("CADMCPCHAT", CommandFlags.Session)]
    public static void ShowChat()
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
    [LispFunction("CADMCPBEGIN")]
    public static string? BeginLisp(ResultBuffer args) => LispCallbacks.Begin(dispatcher, args);
    [LispFunction("CADMCPFINISH")]
    public static int FinishLisp(ResultBuffer args) => LispCallbacks.Finish(dispatcher, args);
    public void Terminate()
    {
        App.Idle -= AddRibbon;
        panel?.Stop(); server?.Dispose(); dispatcher?.Dispose(); documents?.Dispose();
        if (ribbon is not null) ComponentManager.Ribbon?.Tabs.Remove(ribbon);
        palette?.Dispose(); palette = null;
        if (descriptorPath is not null) try { File.Delete(descriptorPath); } catch (IOException) { }
    }
    private sealed class OpenChat : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => ShowChat();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
