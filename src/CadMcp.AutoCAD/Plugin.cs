using System.Windows.Input;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
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
    /// <summary>The user's AutoLISP policy for agents. Agents cannot change it; CAD_MCP_LISP_POLICY overrides it for unattended runs.</summary>
    [CommandMethod("CADMCPLISP", CommandFlags.Session)]
    public static void LispPolicyCommand()
    {
        var editor = App.DocumentManager.MdiActiveDocument?.Editor;
        if (editor is null) return;
        var current = CadSettings.StoredLispPolicy();
        var options = new PromptKeywordOptions("\nAutoLISP, запрошенный агентами CAD MCP") { AppendKeywordsToMessage = true };
        options.Keywords.Add("Ask", "Спрашивать", "Спрашивать");
        options.Keywords.Add("AutoSafe", "Безопасный", "Безопасный");
        options.Keywords.Add("Allow", "Разрешать", "Разрешать");
        options.Keywords.Add("Deny", "Запретить", "Запретить");
        options.Keywords.Default = current switch { LispPolicyMode.AutoSafe => "AutoSafe", LispPolicyMode.Allow => "Allow", LispPolicyMode.Deny => "Deny", _ => "Ask" };
        var result = editor.GetKeywords(options);
        if (result.Status != PromptStatus.OK) return;
        var mode = result.StringResult switch { "AutoSafe" => LispPolicyMode.AutoSafe, "Allow" => LispPolicyMode.Allow, "Deny" => LispPolicyMode.Deny, _ => LispPolicyMode.Ask };
        try { CadSettings.StoreLispPolicy(mode); CadSettings.SessionLispPolicy = null; }
        catch (System.Exception error) when (error is IOException or UnauthorizedAccessException) { editor.WriteMessage("\nCAD MCP: не удалось сохранить настройку: " + error.Message); return; }
        editor.WriteMessage("\nCAD MCP: AutoLISP — " + LispPolicy.Label(mode) +
            (Environment.GetEnvironmentVariable("CAD_MCP_LISP_POLICY") is { Length: > 0 } environment ? ". Внимание: переменная CAD_MCP_LISP_POLICY=" + environment + " имеет приоритет" : ""));
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
