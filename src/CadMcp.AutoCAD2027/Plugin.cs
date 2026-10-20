using System.Diagnostics;
using Trace = System.Diagnostics.Trace;
using System.Windows.Input;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.DatabaseServices;
using CadMcp.Core;
using CadMcp.AutoCAD;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(CadMcp.AutoCAD2027.Plugin))]
[assembly: CommandClass(typeof(CadMcp.AutoCAD2027.Plugin))]
namespace CadMcp.AutoCAD2027;
public sealed class Plugin : IExtensionApplication
{
    private static Documents? documents;
    private static Dispatcher? dispatcher;
    private static PipeServer? server;
    private static string? descriptor;
    private static object? ribbon;
    private static Process? chatClient;
    [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr window,int command);
    public void Initialize()
    {
        try
        {
        documents = new(); dispatcher = new(documents) { ApprovalPresenter = LispApprovalWindow.Show }; dispatcher.Start();
        int pid = Environment.ProcessId; string pipe = "cadmcp-worker-" + pid + "-" + documents.SessionId;
        server = new(pipe, dispatcher.Enqueue); server.Start();
        descriptor = Wire.PublishWorker(new WorkerDescriptor(documents.SessionId, pipe, pid, Wire.Version));
        App.Idle += AddRibbon;
        }
        catch (System.Exception error) { Terminate(); Trace.WriteLine("CAD MCP initialization: " + error); }
    }
    private static void AddRibbon(object? sender, EventArgs e)
    {
        try
        {
            dynamic? control = Type.GetType("Autodesk.Windows.ComponentManager, AdWindows")?.GetProperty("Ribbon")?.GetValue(null);
            if (control is null) return;
            App.Idle -= AddRibbon;
            dynamic Make(string name) => Activator.CreateInstance(Type.GetType("Autodesk.Windows." + name + ", AdWindows", true)!)!;
            dynamic tab = Make("RibbonTab"); tab.Id = "CADMCP_ASSISTANT"; tab.Title = "CAD MCP";
            dynamic source = Make("RibbonPanelSource"); source.Title = "Ассистент";
            dynamic button = Make("RibbonButton"); button.Text = "Открыть чат"; button.ShowText = true; button.CommandHandler = new OpenChat(); source.Items.Add(button);
            dynamic panel = Make("RibbonPanel"); panel.Source = source; tab.Panels.Add(panel); control.Tabs.Add(tab); ribbon = tab;
        }
        catch (System.Exception error) { App.Idle -= AddRibbon; Trace.WriteLine("CAD MCP ribbon: " + error); }
    }
    [CommandMethod("CADMCPCHAT", CommandFlags.Session)]
    public static void ShowChat()
    {
        if (documents is null) { App.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCAD MCP worker did not initialize"); return; }
        try
        {
        if(chatClient is not null && !chatClient.HasExited){chatClient.Refresh();ShowWindow(chatClient.MainWindowHandle,9);SetForegroundWindow(chatClient.MainWindowHandle);return;}
        chatClient?.Dispose();
        string root = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
        var start = new ProcessStartInfo(Path.GetFullPath(Path.Combine(root, "..", "Client", "CadMcp.Client.exe"))) { UseShellExecute = false };
        start.ArgumentList.Add("--session"); start.ArgumentList.Add(documents!.SessionId); chatClient=Process.Start(start);
        }
        catch (System.Exception error) { App.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCAD MCP chat: " + error.Message); }
    }
    [CommandMethod("CADMCPLISP", CommandFlags.Session)]
    public static void LispPolicyCommand() => LispPolicyPrompt.Run();
    [CommandMethod("CADMCPAUTOSAVE", CommandFlags.Session)]
    public static void AutoSaveCommand() => AutoSavePrompt.Run();
    [LispFunction("CADMCPBEGIN")]
    public static string? Begin(ResultBuffer args) => LispCallbacks.Begin(dispatcher, args);
    [LispFunction("CADMCPFINISH")]
    public static int Finish(ResultBuffer args) => LispCallbacks.Finish(dispatcher, args);
    public void Terminate()
    {
        void Step(Action action) { try { action(); } catch (System.Exception error) { Trace.WriteLine("CAD MCP shutdown: " + error); } }
        Step(() => App.Idle -= AddRibbon);
        Step(() => server?.Dispose()); server = null;
        Step(() => dispatcher?.Dispose()); dispatcher = null;
        Step(() => documents?.Dispose()); documents = null;
        Step(() => { if (descriptor is not null) File.Delete(descriptor); }); descriptor = null;
        Step(() => { dynamic? control = Type.GetType("Autodesk.Windows.ComponentManager, AdWindows")?.GetProperty("Ribbon")?.GetValue(null); if (ribbon is not null) control?.Tabs.Remove((dynamic)ribbon); }); ribbon = null;
        Step(() => chatClient?.Dispose()); chatClient = null;
    }
    private sealed class OpenChat : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => ShowChat();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
