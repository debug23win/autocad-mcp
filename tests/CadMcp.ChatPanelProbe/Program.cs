using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CadMcp.AutoCAD;
using CadMcp.Providers;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using Size = System.Windows.Size;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try { Run(); }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); Environment.ExitCode = 1; }
    }
    private static void Run()
    {
        var root = Path.GetFullPath(Path.Combine(".runtime", "panel-probe-" + Guid.NewGuid().ToString("N")));
        Environment.SetEnvironmentVariable("CAD_MCP_CSHARP_POLICY_FILE", Path.Combine(root, "csharp-policy.json"));
        Environment.SetEnvironmentVariable("CAD_MCP_COMPUTER_USE_ACCESS_FILE", Path.Combine(root, "computer-use-access.json"));
        ProbeAuthentication(Path.Combine(root, "authentication"));
        var store = new ChatStateStore(root);
        store.Save(new(0, "codex.exe", "claude.exe", "host.exe", Environment.CurrentDirectory, "kept-session", "", "test-key", "gpt-6-astra", "ultra"));
        var preferences = new ChatPreferencesStore(Path.Combine(root, "preferences.json"));
        var panel = new ChatPanel(store, preferences: preferences);
        var models = new[] {
            new CodexModel("gpt-6-astra", "GPT-6-Astra", "medium", new[] { "low", "medium", "high", "ultra" }, true),
            new CodexModel("gpt-6-sol", "GPT-6-Sol", "low", new[] { "low", "medium", "high", "ultra" }, false),
            new CodexModel("gpt-6-luna", "GPT-6-Luna", "medium", new[] { "low", "medium", "high" }, false) };
        panel.ApplyModels(models);
        ComboBox Field(string name) => (ComboBox)typeof(ChatPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        var model = Field("model"); var reasoning = Field("reasoning");
        var accountCliSetup = (WrapPanel)typeof(ChatPanel).GetField("accountCliSetup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var findAccountCli = (Button)typeof(ChatPanel).GetField("findAccountCli", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var installAccountCli = (Button)typeof(ChatPanel).GetField("installAccountCli", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        void AssertAccountCliSetup(bool visible, string name)
        {
            Assert(accountCliSetup.Visibility == (visible ? Visibility.Visible : Visibility.Collapsed), name + " has incorrect CLI setup visibility");
            if (!visible) return;
            Assert(accountCliSetup.Children.Contains(findAccountCli) && accountCliSetup.Children.Contains(installAccountCli) && findAccountCli.IsEnabled && installAccountCli.IsEnabled,
                name + " is missing available Find/Install CLI controls");
            // Inspect state only: an installer or real CLI must never be started by the UI probe.
            typeof(ChatPanel).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, new object[] { true });
            Assert(accountCliSetup.Visibility == Visibility.Visible && !findAccountCli.IsEnabled && !installAccountCli.IsEnabled,
                name + " allows CLI setup during an active operation");
            typeof(ChatPanel).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, new object[] { false });
            Assert(findAccountCli.IsEnabled && installAccountCli.IsEnabled, name + " did not restore CLI setup after completion");
        }
        AssertAccountCliSetup(false, "Codex");
        var csharp = (System.Windows.Controls.CheckBox)typeof(ChatPanel).GetField("allowCSharp", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        Assert(csharp.IsChecked == false && !CadMcp.Core.CSharpExecutionPolicy.Read().Allowed, "C# must start disabled");
        csharp.IsChecked = true;
        Assert(CadMcp.Core.CSharpExecutionPolicy.Read().Allowed, "C# checkbox did not save user permission");
        csharp.IsChecked = false;
        Assert(!CadMcp.Core.CSharpExecutionPolicy.Read().Allowed, "C# checkbox did not revoke user permission");
        var computerTarget = new CadMcp.Core.ComputerUseTarget(Path.Combine(root, "acad.exe"), "AutoCAD 2027", "CN=Autodesk", "AutoCAD", "acad.exe", new string('A', 64));
        var accessStore = new CadMcp.Core.ComputerUseAccessStore();
        Assert(accessStore.Permission(computerTarget) is null, "Computer Use must start without a grant");
        accessStore.Set(computerTarget, true);
        var revokeComputer = (Button)typeof(ChatPanel).GetField("computerUseRevoke", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        revokeComputer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(accessStore.Permission(computerTarget)?.Allowed == false, "Computer Use revoke control did not revoke access");
        for (int choice = 0; choice < 2; choice++)
        {
            var dialog = new ComputerUseAccessWindow(computerTarget, choice == 1, choice == 1, true);
            int clicked = choice;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                dialog.UpdateLayout();
                var stack = (StackPanel)dialog.Content; var row = (WrapPanel)stack.Children[stack.Children.Count - 1];
                var buttons = row.Children.OfType<Button>().ToArray();
                Assert(buttons[clicked].IsEnabled && !buttons[1 - clicked].IsEnabled, "Computer Use dialog enables the wrong action");
                if (clicked == 0)
                {
                    var bitmap = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(dialog);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(root, "panel-computer-use.png")); png.Save(file);
                }
                buttons[clicked].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Assert(dialog.ShowDialog() == true && dialog.Choice == (choice == 0), "Computer Use dialog lost the explicit allow/revoke choice");
        }

        Assert((string)model.SelectedValue == "gpt-6-astra" && (string)reasoning.SelectedValue == "ultra", "Saved model not shown");
        model.SelectedValue = "gpt-6-luna";
        Assert((string)reasoning.SelectedValue == "", "Unsupported effort was retained after changing model");
        model.SelectedValue = "gpt-6-sol"; reasoning.SelectedValue = "high";
        var saved = store.Load()!;
        Assert(saved.CodexModel == "gpt-6-sol" && saved.CodexReasoningEffort == "high" && saved.SessionId == "kept-session", "Selection discarded history/settings");
        var restored = new ChatPanel(store, preferences: preferences); restored.ApplyModels(models);
        Assert((string)((ComboBox)typeof(ChatPanel).GetField("model", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(restored)!).SelectedValue == "gpt-6-sol", "Reopened panel lost model");
        string projectDirectory = Path.Combine(root, "project"); Directory.CreateDirectory(projectDirectory);
        var newProject = new ChatPanel(new ChatStateStore(Path.Combine(root, "new-project-history")), projectDirectory, preferences: preferences);
        var projectPath = (System.Windows.Controls.TextBox)typeof(ChatPanel).GetField("directory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(newProject)!;
        Assert(projectPath.Text == projectDirectory, "Global model preferences displaced the new drawing working directory");
        Field("provider").SelectedIndex = 1;
        AssertAccountCliSetup(false, "Claude Code");
        int Items(ComboBox box) => ((System.Collections.IEnumerable)box.ItemsSource).Cast<object>().Count();
        string Label(ComboBox box, int index) => (string)box.Items[index].GetType().GetProperty("Label")!.GetValue(box.Items[index])!;
        // Until Claude Code lists its models, the aliases are offered with every level.
        Assert(model.IsEnabled && reasoning.IsEnabled && Items(model) == 5 && Items(reasoning) == 6, "Claude model controls are missing");
        panel.ApplyClaudeModels(new[] {
            new ClaudeModel("default", "Default (recommended)", "Sonnet 5.5 · Efficient for routine tasks", "claude-sonnet-5-5", ClaudeCatalog.AllEfforts, true),
            new ClaudeModel("opus", "Opus", "Opus 5.5 · Best for everyday, complex tasks", "claude-opus-5-5", ClaudeCatalog.AllEfforts, false),
            new ClaudeModel("haiku", "Haiku", "Haiku 5.5 · Fastest for quick answers", "claude-haiku-5-5", new[] { "low", "medium" }, false) });
        Assert(Items(model) == 3 && Label(model, 0).Contains("Sonnet 5.5") && Label(model, 1).StartsWith("Opus 5.5"), "Claude models do not come from the CLI list");
        model.SelectedValue = "opus"; reasoning.SelectedValue = "max";
        model.SelectedValue = "haiku";
        Assert((string)reasoning.SelectedValue == "" && Items(reasoning) == 3, "Claude levels do not follow the selected model");
        reasoning.SelectedValue = "medium";
        Assert(store.Load()!.ClaudeModel == "haiku" && store.Load()!.ClaudeReasoningEffort == "medium", "Claude selection was not saved");
        var executable = (System.Windows.Controls.TextBox)typeof(ChatPanel).GetField("executable", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        string claudeExecutable = executable.Text;
        Field("provider").SelectedIndex = 2;
        AssertAccountCliSetup(false, "Perplexity");
        var apiKey = (PasswordBox)typeof(ChatPanel).GetField("perplexityApiKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var apiConnection = (StackPanel)typeof(ChatPanel).GetField("perplexityConnection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var computerUse = (Button)typeof(ChatPanel).GetField("computerUseAccess", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        Assert(executable.Visibility == Visibility.Collapsed && Field("agentCount").Visibility == Visibility.Collapsed && computerUse.Visibility == Visibility.Collapsed && apiConnection.Visibility == Visibility.Visible,
            "Perplexity shows unsupported CLI, helper or Computer Use controls");
        apiKey.Password = "test-key-never-export-this";
        panel.ApplyPerplexityModels(new[] { new PerplexityModel("vendor/api-model", "API model"), new PerplexityModel("vendor/other-model", "Other model") });
        Assert(Items(model) == 3 && Items(reasoning) == 5, "Perplexity model/effort catalog missing");
        model.SelectedValue = "vendor/api-model"; reasoning.SelectedValue = "high";
        var apiState = store.Load()!;
        Assert(apiState.Provider == 2 && apiState.PerplexityModel == "vendor/api-model" && apiState.PerplexityReasoningEffort == "high", "Perplexity selection was not saved");
        Assert(apiState.ClaudeExecutable == claudeExecutable && apiState.ClaudeModel == "haiku" && apiState.CodexModel == "gpt-6-sol", "Perplexity overwrote another provider's settings");
        string exportedApiState = JsonSerializer.Serialize(typeof(ChatPanel).GetMethod("State", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null));
        Assert(!exportedApiState.Contains(apiKey.Password) && !JsonSerializer.Serialize(preferences.Load()).Contains(apiKey.Password), "Perplexity key leaked into exported history or preferences");
        var apiRestored = new ChatPanel(store, preferences: preferences);
        var restoredApiKey = (PasswordBox)typeof(ChatPanel).GetField("perplexityApiKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(apiRestored)!;
        var restoredApiModel = (ComboBox)typeof(ChatPanel).GetField("model", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(apiRestored)!;
        Assert(restoredApiKey.Password.Length == 0 && (string)restoredApiModel.SelectedValue == "vendor/api-model", "Perplexity restored a secret or lost the saved model without an online catalog");
        Field("provider").SelectedIndex = 3;
        AssertAccountCliSetup(true, "Qwen Code");
        executable.Text = @"C:\CAD tools\qwen.cmd";
        Assert(executable.Visibility == Visibility.Visible && apiConnection.Visibility == Visibility.Collapsed && reasoning.Visibility == Visibility.Collapsed && Field("agentCount").Visibility == Visibility.Collapsed,
            "Qwen subscription controls show API key, unsupported reasoning or helpers");
        Assert(Items(model) == 1 && (string)model.SelectedValue == "", "Qwen must default to the CLI's own model without invented choices");
        panel.ApplySubscriptionModels(3, new[] { new SubscriptionModel("qwen/default", "Qwen account default", true), new SubscriptionModel("qwen/selected", "Qwen account alternative", false) });
        Assert(Items(model) == 3 && Label(model, 0).Contains("Qwen account default"), "Qwen models were not taken from the CLI catalog");
        model.SelectedValue = "qwen/selected";
        Field("provider").SelectedIndex = 4;
        AssertAccountCliSetup(true, "Kimi Code");
        executable.Text = @"C:\CAD tools\kimi.cmd";
        Assert(reasoning.Visibility == Visibility.Collapsed && computerUse.Visibility == Visibility.Collapsed, "Kimi shows unsupported controls");
        panel.ApplySubscriptionModels(4, new[] { new SubscriptionModel("kimi-code/default", "Kimi account default", true), new SubscriptionModel("kimi-code/selected", "Kimi account alternative", false) });
        model.SelectedValue = "kimi-code/selected";
        typeof(ChatPanel).GetMethod("Persist", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
        var subscriptionState = store.Load()!;
        Assert(subscriptionState.QwenExecutable == @"C:\CAD tools\qwen.cmd" && subscriptionState.KimiExecutable == @"C:\CAD tools\kimi.cmd", "Subscription CLI executable paths overwrite one another");
        Assert(subscriptionState.QwenModel == "qwen/selected" && subscriptionState.KimiModel == "kimi-code/selected", "Subscription model choices were not kept independently");
        var untrustedHistory = subscriptionState with { Provider = 3, CodexExecutable = "untrusted-codex.exe", ClaudeExecutable = "untrusted-claude.exe",
            QwenExecutable = "untrusted-qwen.cmd", KimiExecutable = "untrusted-kimi.cmd", Host = "untrusted-host.exe", Directory = @"C:\untrusted" };
        typeof(ChatPanel).GetMethod("Restore", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, new object[] { untrustedHistory, true });
        var safeImport = (ChatState)typeof(ChatPanel).GetMethod("State", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null)!;
        Assert(safeImport.QwenExecutable == subscriptionState.QwenExecutable && safeImport.KimiExecutable == subscriptionState.KimiExecutable && safeImport.ClaudeExecutable == subscriptionState.ClaudeExecutable && safeImport.CodexExecutable == subscriptionState.CodexExecutable && safeImport.Host == subscriptionState.Host,
            "Imported history replaced trusted executable paths");
        typeof(ChatPanel).GetField("acceptingInput", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel, false);
        typeof(ChatPanel).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, new object[] { true });
        var accountSend = (Button)typeof(ChatPanel).GetField("send", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        Assert(!accountSend.IsEnabled && accountSend.Content.ToString() == "Отправить", "Subscription CLI without steering advertises an active follow-up action");
        typeof(ChatPanel).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, new object[] { false });
        Field("provider").SelectedIndex = 1;
        Assert(executable.Text == claudeExecutable && (string)model.SelectedValue == "haiku" && (string)reasoning.SelectedValue == "medium", "Returning from Perplexity lost Claude executable/model/effort");
        Field("provider").SelectedIndex = 0;
        Assert(model.IsEnabled && reasoning.IsEnabled && executable.Visibility == Visibility.Visible && (string)model.SelectedValue == "gpt-6-sol" && (string)reasoning.SelectedValue == "high", "Codex controls/settings were not restored");
        typeof(ChatPanel).GetField("acceptingInput", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel,true);
        typeof(ChatPanel).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel,new object[]{true});
        Button ButtonField(string name)=>(Button)typeof(ChatPanel).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(panel)!;
        Assert(ButtonField("send").IsEnabled&&ButtonField("attach").IsEnabled&&!model.IsEnabled&&ButtonField("send").Content.ToString()=="Дополнить","Running chat prevents adding instructions or files");
        typeof(ChatPanel).GetField("acceptingInput", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel,false);
        typeof(ChatPanel).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel,new object[]{false});
        var flow = new ChatPanel(new ChatStateStore(Path.Combine(root, "followup-flow")), preferences: new ChatPreferencesStore(Path.Combine(root, "flow-preferences.json")));
        object FlowField(string name) => typeof(ChatPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(flow)!;
        void FlowSet(string name, object value) => typeof(ChatPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(flow, value);
        var flowMessages = (List<ChatLine>)FlowField("messages");
        flowMessages.Add(new("user", "Исходная задача")); flowMessages.Add(new("assistant", "", Steps: new[] { "Изучаю чертёж" }));
        FlowSet("currentAssistantIndex", 1); FlowSet("acceptingInput", true);
        ((System.Windows.Controls.TextBox)FlowField("input")).Text = "Уточнение задачи";
        ((Task)typeof(ChatPanel).GetMethod("AddFollowup", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, null)!).GetAwaiter().GetResult();
        Assert(flowMessages.Count == 4 && flowMessages[1].Delivery == "Продолжение задачи — ниже." && flowMessages[1].Steps!.Contains("Изучаю чертёж"), "Steering left an unexplained empty assistant card or erased progress");
        typeof(ChatPanel).GetMethod("ProviderCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, null);
        Assert(!(bool)FlowField("acceptingInput") && !((Button)FlowField("send")).IsEnabled && !((Button)FlowField("attach")).IsEnabled, "Final CAD verification accepted input into an already closed provider turn");
        typeof(ChatPanel).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, new object[] { false });
        Assert(((Button)FlowField("send")).IsEnabled && ((Button)FlowField("send")).Content.ToString() == "Отправить", "A completed turn did not restore a fresh Send action");
        ((System.Windows.Controls.CheckBox)FlowField("showProgress")).IsChecked = false;
        typeof(ChatPanel).GetMethod("RecordProblem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, new object[] { "Код: CSHARP_FAILED\nОшибка: PE image does not have metadata" });
        Assert(flowMessages[3].Text.Contains("CSHARP_FAILED") && flowMessages[3].Text.Contains("Проблема при выполнении"), "Task errors were hidden with progress disabled");
        Assert(ChatMarkup.AnswerText(flowMessages, 3)!.Contains("CSHARP_FAILED"), "Copying the answer omitted the diagnostic");
        ((System.Windows.Threading.DispatcherTimer)FlowField("progressTimer")).Start();
        typeof(ChatPanel).GetMethod("FinishTurn", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flow, new object[] { "Остановлено" });
        Assert(!((Button)FlowField("stop")).IsEnabled && !((System.Windows.Threading.DispatcherTimer)FlowField("progressTimer")).IsEnabled && ((TextBlock)FlowField("activity")).Text == "Остановлено", "Stopped task still looked like active work");
        var note = Path.Combine(root, "plan.txt");
        var picture = Path.Combine(root, "view.png");
        File.WriteAllText(note, "Чертёж ✓");
        File.WriteAllBytes(picture, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6wAAAABJRU5ErkJggg=="));
        typeof(ChatPanel).GetMethod("AddAttachments", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, new object[] { new[] { note, picture } });
        var chips = (WrapPanel)typeof(ChatPanel).GetField("attachmentItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        Assert(chips.Children.Count == 2 && ((TextBlock)((StackPanel)chips.Children[0]).Children[0]).Text.Contains("plan.txt"), "Dropped files are not visible in the composer");
        var remove = (Button)((StackPanel)chips.Children[0]).Children[1];
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(chips.Children.Count == 1, "Attachment remove button did not work");
        typeof(ChatPanel).GetMethod("AddAttachments", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, new object[] { new[] { note } });
        panel.Measure(new Size(480, 850)); panel.Arrange(new Rect(0, 0, 480, 850)); panel.UpdateLayout();
        var image = new RenderTargetBitmap(480, 850, 96, 96, PixelFormats.Pbgra32); image.Render(panel);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        string path = Path.Combine(root, "panel.png");
        using (var file = File.Create(path)) encoder.Save(file);
        panel.DarkThemeProvider = () => true;
        typeof(ChatPanel).GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
        panel.UpdateLayout();
        Assert(model.Template is not null && model.Foreground is SolidColorBrush foreground && foreground.Color.R > 180, "Dark ComboBox uses a real dark template and readable light foreground");
        Assert(panel.Resources.Contains("Cad.ThemeInstalled"), "Native controls did not receive the shared theme");
        var toggle = (System.Windows.Controls.Primitives.ToggleButton)model.Template!.FindName("Toggle", model);
        toggle.ApplyTemplate();
        var chrome = (Border)toggle.Template.FindName("Box", toggle);
        Assert(chrome.Background is SolidColorBrush background && background.Color == System.Windows.Media.Color.FromRgb(48,57,70), "ComboBox selection field still has a light native background");
        var darkImage = new RenderTargetBitmap(480, 850, 96, 96, PixelFormats.Pbgra32); darkImage.Render(panel);
        var darkEncoder = new PngBitmapEncoder(); darkEncoder.Frames.Add(BitmapFrame.Create(darkImage));
        string darkPath = Path.Combine(root, "panel-dark.png");
        using (var file = File.Create(darkPath)) darkEncoder.Save(file);
        var captionContent = new Border(); captionContent.SetResourceReference(Border.BackgroundProperty, "Cad.Panel");
        var captionWindow = new ChatWindow(captionContent) { Width = 480, Height = 180, Left = -2000, Top = -2000, ShowInTaskbar = false };
        try
        {
            captionWindow.Show(); captionWindow.UpdateLayout();
            var caption = (Border)typeof(ChatWindow).GetField("caption", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(captionWindow)!;
            Assert(caption.Background is SolidColorBrush cb && cb.Color == System.Windows.Media.Color.FromRgb(57, 67, 81), "Caption is black instead of the AutoCAD palette");
            Assert(System.Windows.Shell.WindowChrome.GetWindowChrome(captionWindow).CaptionHeight == 32 && captionWindow.ResizeMode == ResizeMode.CanResize, "Custom caption lost draggable or resizable window chrome");
            void SaveCaption(string name)
            {
                captionWindow.UpdateLayout(); var bitmap = new RenderTargetBitmap(480, 180, 96, 96, PixelFormats.Pbgra32); bitmap.Render(captionWindow);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(root, name)); png.Save(file);
            }
            SaveCaption("panel-caption-dark.png");
            captionWindow.ApplyHeaderTheme(false);
            Assert(caption.Background is SolidColorBrush lb && lb.Color == System.Windows.Media.Color.FromRgb(245, 246, 248), "Caption does not follow a light theme change");
            SaveCaption("panel-caption-light.png");
        }
        finally { captionWindow.Close(); }
        Console.WriteLine("PASS visible tool diagnostics with progress disabled, stopped task state and palette-colored native caption");
        Console.WriteLine("PASS real WPF model/effort selection, attachment chips/removal, persistence and provider controls");
        Console.WriteLine("UI image: " + path);
        Console.WriteLine("Dark UI image: " + darkPath);
        if (Environment.GetCommandLineArgs().Contains("--native-theme-only")) return;
        var browserProbe = new ChatBrowser(Path.Combine(root, "browser-data"));
        browserProbe.Update(new[]
        {
            new ChatLine("user", "Покажи формулу $A=ab$ и фото", Images: new[] { browserProbe.SaveImage(ChatAttachments.Inspect(path)) }),
            new ChatLine("assistant", "Площадь **проверена**: $$A=3{,}05\\cdot 4=12{,}2\\,\\mathrm{м}^2$$",
                Steps: new[] { "Завершено: cad_search" }, ReasoningSummary: "Сверяю размеры")
        }, true, 1);
        Exception? browserError = null;
        var app = new System.Windows.Application();
        var window = new Window { Width = 650, Height = 500, Left = -2000, Top = -2000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, Content = browserProbe };
        window.Loaded += async (_, _) =>
        {
            try
            {
                var ready = typeof(ChatBrowser).GetField("ready", BindingFlags.Instance | BindingFlags.NonPublic)!;
                for (int i = 0; i < 100 && !(bool)ready.GetValue(browserProbe)!; i++) await Task.Delay(100);
                if (!(bool)ready.GetValue(browserProbe)!)
                {
                    var fallback = (System.Windows.Controls.TextBox)typeof(ChatBrowser).GetField("fallback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(browserProbe)!;
                    throw new Exception("WebView2 did not load HTML: " + fallback.Text);
                }
                var view = (WebView2)typeof(ChatBrowser).GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(browserProbe)!;
                async Task<bool> Js(string expression) => JsonSerializer.Deserialize<bool>(await view.CoreWebView2.ExecuteScriptAsync(expression));
                var lines = Enumerable.Range(0, 40).Select(i => new ChatLine(i % 2 == 0 ? "user" : "assistant",
                    "Сообщение " + i + "\n\n" + string.Join("\n\n", Enumerable.Repeat("Видимый текст и размеры **3,05 м**. Формула $A=ab$.", 4)))).ToList();
                lines.Add(new ChatLine("assistant", "Ответ поступает…"));
                browserProbe.Update(lines, true, -1); await Task.Delay(250);
                await view.CoreWebView2.ExecuteScriptAsync("window.scrollTo(0,1800); window.dispatchEvent(new Event('scroll')); window.keptCard=document.querySelectorAll('article')[4]; window.keptScroll=document.scrollingElement.scrollTop;");
                await Task.Delay(100);
                for (int i = 0; i < 15; i++)
                {
                    lines[^1] = new ChatLine("assistant", string.Join("\n\n", Enumerable.Repeat("Новая часть ответа " + i, i + 1)));
                    browserProbe.Update(lines, i % 2 == 0, -1); await Task.Delay(40);
                }
                await Task.Delay(150);
                Assert(await Js("Math.abs(document.scrollingElement.scrollTop-window.keptScroll)<3"), "Streaming or theme switch moved the reader to another position");
                Assert(await Js("window.keptCard===document.querySelectorAll('article')[4]"), "Unchanged message DOM was replaced");
                Assert(await Js("[...document.querySelectorAll('.content p')].every(p=>getComputedStyle(p).visibility==='visible' && getComputedStyle(p).opacity!=='0' && p.getBoundingClientRect().height>0)"), "Text became hidden while streaming");
                Assert(await Js("(()=>{const p=document.querySelector('.content p');return getComputedStyle(p).color!==getComputedStyle(p.closest('.message')).backgroundColor})()"), "Text has the same color as its background");
                await view.CoreWebView2.ExecuteScriptAsync("window.scrollTo(0,document.scrollingElement.scrollHeight); window.dispatchEvent(new Event('scroll'));");
                await Task.Delay(100);
                lines[^1] = new ChatLine("assistant", string.Join("\n\n", Enumerable.Repeat("Окончательный результат с формулой $$A=ab$$", 30)));
                browserProbe.Update(lines, true, -1); await Task.Delay(200);
                Assert(await Js("document.scrollingElement.scrollHeight-document.scrollingElement.scrollTop-window.innerHeight<3"), "Chat stopped following the answer at the bottom");
                Assert(await Js("document.querySelectorAll('.katex').length>0"), "Formula rendering disappeared");
                Console.WriteLine("PASS real WebView2 streaming: stable scroll, preserved messages, visible text, theme changes, bottom follow and formulas");
                string htmlPath = Path.Combine(root, "html-dark.png");
                using (var file = File.Create(htmlPath))
                    await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                Console.WriteLine("HTML UI image: " + htmlPath);
            }
            catch (Exception error) { browserError = error; }
            finally { window.Close(); }
        };
        app.Run(window);
        if (browserError is not null) throw browserError;
    }

    private static void ProbeAuthentication(string root)
    {
        // No loaded WPF surface or provider process: catalogs are fixtures, and the only Send calls
        // exercise the blank-key validation that must return before starting the host/CLI.
        const string codexKey = "cad-probe-openai-not-a-real-key";
        const string claudeKey = "cad-probe-anthropic-not-a-real-key";
        const string environmentKey = "cad-probe-environment-key-one";
        const string changedEnvironmentKey = "cad-probe-environment-key-two";
        string? oldOpenAi = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        string? oldAnthropic = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object? Value(ChatPanel panel, string name) => typeof(ChatPanel).GetField(name, flags)!.GetValue(panel);
            T Control<T>(ChatPanel panel, string name) where T : class => (T)Value(panel, name)!;
            void Set(ChatPanel panel, string name, object? value) => typeof(ChatPanel).GetField(name, flags)!.SetValue(panel, value);
            object? Call(ChatPanel panel, string name, params object[] args) => typeof(ChatPanel).GetMethod(name, flags)!.Invoke(panel, args);
            void Check(bool condition, string message) { if (!condition) throw new Exception("Authentication probe: " + message); }
            var store = new ChatStateStore(root);
            var preferences = new ChatPreferencesStore(Path.Combine(root, "preferences.json"));
            store.Save(new(0, "trusted-codex.exe", "trusted-claude.exe", typeof(Program).Assembly.Location, root,
                "subscription-session", "", Messages: new[] { new ChatLine("user", "Сохранённая задача"), new ChatLine("assistant", "Сохранённый ответ") }));
            var panel = new ChatPanel(store, preferences: preferences);
            var provider = Control<ComboBox>(panel, "provider");
            var mode = Control<ComboBox>(panel, "authenticationMode");
            var codexPassword = Control<PasswordBox>(panel, "codexApiKey");
            var claudePassword = Control<PasswordBox>(panel, "claudeApiKey");
            var model = Control<ComboBox>(panel, "model");
            var executable = Control<System.Windows.Controls.TextBox>(panel, "executable");
            var messages = Control<List<ChatLine>>(panel, "messages");
            int originalCount = messages.Count;
            Check(mode.SelectedIndex == 0 && Call(panel, "SelectedAuthentication") is null, "Legacy settings unexpectedly enabled API billing");
            Check(Control<StackPanel>(panel, "authenticationConnection").Visibility == Visibility.Visible &&
                Control<StackPanel>(panel, "codexApiConnection").Visibility == Visibility.Collapsed, "Default CLI authentication controls are incorrect");
            Check(Control<StackPanel>(panel, "codexApiConnection").Children.OfType<TextBlock>().Any(text => text.Text.Contains("OPENAI_API_KEY") && text.Text.Contains("ChatGPT")) &&
                Control<StackPanel>(panel, "claudeApiConnection").Children.OfType<TextBlock>().Any(text => text.Text.Contains("ANTHROPIC_API_KEY") && text.Text.Contains("Claude")),
                "API setup/billing guidance is missing");
            var codexModels = new[] { new CodexModel("probe-codex", "Probe Codex", "medium", new[] { "low", "medium", "high" }, true) };
            panel.ApplyModels(codexModels);
            model.SelectedValue = "probe-codex";
            Control<ComboBox>(panel, "reasoning").SelectedValue = "high";
            executable.Text = "trusted-codex-fixture.exe";
            Set(panel, "adapter", new CodexProvider(new ProviderOptions("unused.exe", "unused-host.exe", root)) { SessionId = "old-account-session" });
            mode.SelectedIndex = 1;
            Check(Value(panel, "adapter") is null && Value(panel, "restored") is null && Control<IReadOnlyList<CodexModel>>(panel, "catalog").Count == 0,
                "Changing billing mode retained the account session or catalog");
            Check(messages.Count == originalCount && messages[0].Text == "Сохранённая задача", "Changing billing mode erased visible history");
            Check(Control<StackPanel>(panel, "codexApiConnection").Visibility == Visibility.Visible &&
                Control<StackPanel>(panel, "claudeApiConnection").Visibility == Visibility.Collapsed && Control<TextBlock>(panel, "quota").Visibility == Visibility.Collapsed,
                "Codex API controls show the wrong key or subscription quota");
            Control<System.Windows.Controls.TextBox>(panel, "host").Text = typeof(Program).Assembly.Location;
            Control<System.Windows.Controls.TextBox>(panel, "directory").Text = root;
            Control<System.Windows.Controls.TextBox>(panel, "input").Text = "Проверка пустого ключа";
            ((Task)Call(panel, "Send")!).GetAwaiter().GetResult();
            Check(Control<TextBlock>(panel, "activity").Text.Contains("OPENAI_API_KEY") && Value(panel, "running") is null && messages.Count == originalCount,
                "Blank OpenAI API key was not rejected before startup");
            codexPassword.Password = codexKey;
            var entered = (ProviderAuthentication)Call(panel, "SelectedAuthentication")!;
            Check(entered.Matches(new ProviderAuthentication(codexKey)), "Entered OpenAI key was not selected");
            panel.ApplyModels(codexModels);
            var connectionExpander = ((DockPanel)panel.Content).Children.OfType<StackPanel>()
                .SelectMany(stack => stack.Children.OfType<Expander>()).Single();
            connectionExpander.IsExpanded = true;
            Control<TextBlock>(panel, "activity").Text = "Готово";
            panel.Measure(new Size(560, 1250)); panel.Arrange(new Rect(0, 0, 560, 1250)); panel.UpdateLayout();
            var authenticationImage = new RenderTargetBitmap(560, 1250, 96, 96, PixelFormats.Pbgra32);
            authenticationImage.Render(panel);
            var authenticationEncoder = new PngBitmapEncoder(); authenticationEncoder.Frames.Add(BitmapFrame.Create(authenticationImage));
            string authenticationImagePath = Path.Combine(Path.GetDirectoryName(root)!, "panel-api-auth.png");
            using (var imageFile = File.Create(authenticationImagePath)) authenticationEncoder.Save(imageFile);
            Console.WriteLine("API authentication UI image: " + authenticationImagePath);
            string invalidDocument = Path.Combine(root, "unreadable.docx");
            File.WriteAllText(invalidDocument, "not a DOCX ZIP package");
            Call(panel, "AddAttachments", (object)new[] { invalidDocument });
            Control<System.Windows.Controls.TextBox>(panel, "input").Text = "Изучи приложенный документ";
            ((Task)Call(panel, "Send")!).GetAwaiter().GetResult();
            Check(Control<TextBlock>(panel, "activity").Text.Contains("unreadable.docx") &&
                Control<System.Windows.Controls.TextBox>(panel, "input").Text == "Изучи приложенный документ" &&
                Control<List<ChatAttachment>>(panel, "attachments").Count == 1 && messages.Count == originalCount && Value(panel, "running") is null,
                "Invalid DOCX failed silently, started a provider, or discarded the unsent input/attachment");
            Control<List<ChatAttachment>>(panel, "attachments").Clear();
            Call(panel, "RefreshAttachments");

            provider.SelectedIndex = 1;
            Check(mode.SelectedIndex == 0 && Call(panel, "SelectedAuthentication") is null, "Codex API mode changed Claude billing mode");
            executable.Text = "trusted-claude-fixture.exe";
            panel.ApplyClaudeModels(new[] {
                new ClaudeModel("default", "Default", "Probe Claude default", "probe-claude", new[] { "low", "medium", "high" }, true),
                new ClaudeModel("probe-claude", "Probe Claude", "Probe Claude", "probe-claude", new[] { "low", "medium", "high" }, false) });
            model.SelectedValue = "probe-claude";
            Control<ComboBox>(panel, "reasoning").SelectedValue = "medium";
            mode.SelectedIndex = 1;
            ((Task)Call(panel, "Send")!).GetAwaiter().GetResult();
            Check(Control<TextBlock>(panel, "activity").Text.Contains("ANTHROPIC_API_KEY") && Value(panel, "running") is null && messages.Count == originalCount,
                "Blank Anthropic API key was not rejected before startup");
            claudePassword.Password = claudeKey;
            Check(((ProviderAuthentication)Call(panel, "SelectedAuthentication")!).Matches(new ProviderAuthentication(claudeKey)), "Entered Anthropic key was not selected");
            claudePassword.Password = "";
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", " " + claudeKey + " ");
            Check(((ProviderAuthentication)Call(panel, "SelectedAuthentication")!).Matches(new ProviderAuthentication(claudeKey)), "Empty Anthropic password did not use the environment key");
            claudePassword.Password = claudeKey;
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Check(Control<StackPanel>(panel, "claudeApiConnection").Visibility == Visibility.Visible && Control<StackPanel>(panel, "codexApiConnection").Visibility == Visibility.Collapsed,
                "Claude API mode exposes the wrong provider key");
            for (int i = 2; i < 5; i++)
            {
                provider.SelectedIndex = i;
                Check(Control<StackPanel>(panel, "authenticationConnection").Visibility == Visibility.Collapsed,
                    "OpenAI/Anthropic authentication controls are shown for another provider");
            }
            provider.SelectedIndex = 0;
            Check(mode.SelectedIndex == 1 && executable.Text == "trusted-codex-fixture.exe" && (string)model.SelectedValue == "probe-codex" &&
                (string)Control<ComboBox>(panel, "reasoning").SelectedValue == "high", "Provider switching lost Codex mode/path/model/effort");
            provider.SelectedIndex = 1;
            Check(mode.SelectedIndex == 1 && executable.Text == "trusted-claude-fixture.exe" && (string)model.SelectedValue == "probe-claude" &&
                (string)Control<ComboBox>(panel, "reasoning").SelectedValue == "medium", "Provider switching lost Claude mode/path/model/effort");
            provider.SelectedIndex = 0;
            Call(panel, "SetBusy", true);
            Check(!mode.IsEnabled && !codexPassword.IsEnabled && !claudePassword.IsEnabled && !provider.IsEnabled && Control<TextBlock>(panel, "quota").Visibility == Visibility.Collapsed,
                "Active API task permits credentials/billing changes or shows subscription quota");
            Call(panel, "SetBusy", false);
            mode.SelectedIndex = 0;
            Check(Control<TextBlock>(panel, "quota").Visibility == Visibility.Visible && Call(panel, "SelectedAuthentication") is null,
                "Returning to CLI settings retained API authentication");
            mode.SelectedIndex = 1;

            // A changed environment key must invalidate an existing adapter just like editing the password box.
            codexPassword.Password = "";
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", environmentKey);
            Check(((ProviderAuthentication)Call(panel, "SelectedAuthentication")!).Matches(new ProviderAuthentication(environmentKey)), "Empty password did not use the environment key");
            Set(panel, "adapter", new CodexProvider(new ProviderOptions("unused.exe", "unused-host.exe", root)) { SessionId = "old-environment-session" });
            Set(panel, "restored", store.Load());
            panel.ApplyModels(codexModels);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", changedEnvironmentKey);
            var changed = (ProviderAuthentication)Call(panel, "SelectedAuthentication")!;
            Check(changed.Matches(new ProviderAuthentication(changedEnvironmentKey)) && Value(panel, "adapter") is null && Value(panel, "restored") is null &&
                Control<IReadOnlyList<CodexModel>>(panel, "catalog").Count == 0 && messages.Count == originalCount,
                "Environment credential change reused an account session/catalog or erased the transcript");
            codexPassword.Password = codexKey;
            Call(panel, "SelectedAuthentication");
            string diagnostic = "Probe failure: " + codexKey + " / " + claudeKey + " / " + changedEnvironmentKey;
            string safe = (string)Call(panel, "SafeDiagnostic", diagnostic)!;
            Check(!safe.Contains(codexKey) && !safe.Contains(claudeKey) && !safe.Contains(changedEnvironmentKey) && safe.Contains("[REDACTED]"), "Diagnostic redaction exposed a key");
            Set(panel, "currentAssistantIndex", messages.Count - 1);
            Call(panel, "RecordProblem", diagnostic);
            Call(panel, "LogError", new IOException(diagnostic));
            Call(panel, "Persist");
            string exported = JsonSerializer.Serialize(Call(panel, "State"));
            string savedPreferences = File.ReadAllText(Path.Combine(root, "preferences.json"));
            string savedState = File.ReadAllText(Directory.EnumerateFiles(root, "chat-*.json").Single());
            string logPath = (string)typeof(ChatPanel).GetMethod("LogPath", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            string log = File.ReadAllText(logPath);
            foreach (string secret in new[] { codexKey, claudeKey, changedEnvironmentKey })
                Check(!exported.Contains(secret) && !savedPreferences.Contains(secret) && !savedState.Contains(secret) && !log.Contains(secret),
                    "An API key leaked into exported state, preferences, saved history or the error log");
            Check(preferences.Load() is { CodexApiKeyMode: true, ClaudeApiKeyMode: true }, "Billing choices were not persisted independently");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            var reopened = new ChatPanel(store, preferences: preferences);
            Check(Control<ComboBox>(reopened, "authenticationMode").SelectedIndex == 1 && Control<PasswordBox>(reopened, "codexApiKey").Password.Length == 0 &&
                Control<PasswordBox>(reopened, "claudeApiKey").Password.Length == 0, "Reopening restored a credential or lost the explicit billing choice");

            // Imported fields cannot authorize API billing or restore keys; API session ids are also historical.
            var importedJson = JsonNode.Parse(exported)!.AsObject();
            importedJson["CodexApiKeyMode"] = true;
            importedJson["ClaudeApiKeyMode"] = true;
            importedJson["CodexApiKey"] = codexKey;
            importedJson["ClaudeApiKey"] = claudeKey;
            importedJson["SessionId"] = "untrusted-api-session";
            importedJson["AdapterKey"] = "0|trusted-codex-fixture.exe|host|directory|auth:api";
            var imported = JsonSerializer.Deserialize<ChatState>(importedJson.ToJsonString())!;
            var importPanel = new ChatPanel(new ChatStateStore(Path.Combine(root, "imported")), preferences: new ChatPreferencesStore(Path.Combine(root, "imported-preferences.json")));
            Call(importPanel, "Restore", imported, true);
            var afterImport = (ChatState)Call(importPanel, "State")!;
            Check(Control<ComboBox>(importPanel, "authenticationMode").SelectedIndex == 0 && Call(importPanel, "SelectedAuthentication") is null &&
                Control<PasswordBox>(importPanel, "codexApiKey").Password.Length == 0 && Control<PasswordBox>(importPanel, "claudeApiKey").Password.Length == 0 &&
                afterImport.SessionId is null && afterImport.AdapterKey is null && afterImport.Messages?.Count == messages.Count,
                "Imported history enabled API billing, restored a credential/API session, or discarded visible messages");
            Console.WriteLine("PASS WPF API authentication: independent modes, blank-key validation, scoped session invalidation, secret redaction and safe history import");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", oldOpenAi);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldAnthropic);
        }
    }
}
