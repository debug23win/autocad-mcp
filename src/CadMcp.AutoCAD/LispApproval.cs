using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CadMcp.Core;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using SystemColors = System.Windows.SystemColors;
using EditorInput = Autodesk.AutoCAD.EditorInput;

namespace CadMcp.AutoCAD;

/// <summary>
/// Modeless confirmation of a cad_lisp script. AutoLISP can start programs, write files and load code,
/// so the user sees the exact code and the reviewer's findings before anything is sent to AutoCAD.
/// Closing the window declines.
/// </summary>
internal sealed class LispApprovalWindow : Window
{
    private Action<bool, bool>? decided;
    private readonly CheckBox remember = new()
    {
        Content = "Не спрашивать до перезапуска AutoCAD, если в коде нет опасных функций", Margin = new Thickness(0, 8, 0, 0)
    };

    private LispApprovalWindow(LispApprovalRequest request, Action<bool, bool> decided)
    {
        this.decided = decided;
        Title = "CAD MCP — подтверждение AutoLISP";
        Width = 640; Height = 520; MinWidth = 420; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false; Topmost = true;
        var root = new DockPanel { Margin = new Thickness(12) };
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Агент просит выполнить AutoLISP в чертеже «" + request.Drawing + "».", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Операция " + request.OperationId + ". AutoLISP выполняется без отката транзакции; изменения можно отменить командой ОТМЕНИТЬ (U).", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) });
        foreach (var finding in request.Findings)
            header.Children.Add(new TextBlock
            {
                Text = (finding.Severity == LispPolicy.Risky ? "⚠ " : "• ") + Describe(finding), TextWrapping = TextWrapping.Wrap,
                Foreground = finding.Severity == LispPolicy.Risky ? Brushes.DarkRed : SystemColors.ControlTextBrush, Margin = new Thickness(0, 2, 0, 0)
            });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        DockPanel.SetDock(remember, Dock.Bottom); root.Children.Add(remember);
        // Not a default button: Enter in the code box must never approve by accident.
        var run = new Button { Content = "Выполнить", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0) };
        var decline = new Button { Content = "Отклонить", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        run.Click += (_, _) => Decide(true);
        decline.Click += (_, _) => Decide(false);
        buttons.Children.Add(run); buttons.Children.Add(decline);
        root.Children.Add(new TextBox
        {
            Text = request.Code, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 8, 0, 0)
        });
        Content = root;
        Closed += (_, _) => Decide(false);
    }

    private static string Describe(LispFinding finding) => finding.Code switch
    {
        "LISP_EXTERNAL_PROCESS" => "Запуск внешних программ или объектов: " + finding.Detail,
        "LISP_FILE_WRITE" => "Работа с файлами на диске: " + finding.Detail,
        "LISP_CODE_LOADING" => "Загрузка и запуск стороннего кода: " + finding.Detail,
        "LISP_REGISTRY" => "Изменение реестра или настроек: " + finding.Detail,
        "LISP_DYNAMIC_CODE" => "Код, собираемый во время выполнения (его нельзя проверить заранее): " + finding.Detail,
        "LISP_REACTOR" => "Реактор, который будет выполнять код позже: " + finding.Detail,
        "LISP_SEND_COMMAND" => "Команды, передаваемые строкой: " + finding.Detail,
        "LISP_DIALOG_COMMAND" => "Команда может открыть диалог и ждать пользователя: " + finding.Detail,
        _ => finding.Detail
    };

    private void Decide(bool approved)
    {
        var callback = decided;
        if (callback is null) return;
        decided = null;
        bool rememberChoice = approved && remember.IsChecked == true;
        if (IsVisible) Close();
        callback(approved, rememberChoice);
    }

    /// <summary>Shows the confirmation on the AutoCAD UI thread; disposing the handle closes it without a decision.</summary>
    public static IDisposable Show(LispApprovalRequest request, Action<bool, bool> decided)
    {
        var window = new LispApprovalWindow(request, decided);
        App.ShowModelessWindow(window);
        window.Activate();
        return new Handle(window);
    }

    private sealed class Handle(LispApprovalWindow window) : IDisposable
    {
        public void Dispose()
        {
            // The job already ended (cancelled, timed out, drawing closed); close without reporting a decision.
            window.decided = null;
            if (window.IsVisible) window.Close();
        }
    }
}

/// <summary>Command CADMCPLISP: the user's policy for AutoLISP requested by agents, stored in the CAD MCP settings.</summary>
internal static class LispPolicyPrompt
{
    public static void Run()
    {
        var editor = App.DocumentManager.MdiActiveDocument?.Editor;
        if (editor is null) return;
        var current = CadSettings.StoredLispPolicy();
        var options = new EditorInput.PromptKeywordOptions("\nAutoLISP, запрошенный агентами CAD MCP") { AppendKeywordsToMessage = true };
        options.Keywords.Add("Ask", "Спрашивать", "Спрашивать");
        options.Keywords.Add("AutoSafe", "Безопасный", "Безопасный");
        options.Keywords.Add("Allow", "Разрешать", "Разрешать");
        options.Keywords.Add("Deny", "Запретить", "Запретить");
        options.Keywords.Default = current switch { LispPolicyMode.AutoSafe => "AutoSafe", LispPolicyMode.Allow => "Allow", LispPolicyMode.Deny => "Deny", _ => "Ask" };
        var result = editor.GetKeywords(options);
        if (result.Status != EditorInput.PromptStatus.OK) return;
        var mode = result.StringResult switch { "AutoSafe" => LispPolicyMode.AutoSafe, "Allow" => LispPolicyMode.Allow, "Deny" => LispPolicyMode.Deny, _ => LispPolicyMode.Ask };
        try { CadSettings.StoreLispPolicy(mode); CadSettings.SessionLispPolicy = null; }
        catch (System.Exception error) when (error is IOException or UnauthorizedAccessException) { editor.WriteMessage("\nCAD MCP: не удалось сохранить настройку: " + error.Message); return; }
        editor.WriteMessage("\nCAD MCP: AutoLISP — " + LispPolicy.Label(mode) +
            (Environment.GetEnvironmentVariable("CAD_MCP_LISP_POLICY") is { Length: > 0 } environment ? ". Внимание: переменная CAD_MCP_LISP_POLICY=" + environment + " имеет приоритет" : ""));
    }
}
