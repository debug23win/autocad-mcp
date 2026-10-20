using System.Reflection;
using System.Text.Json;
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
        var store = new ChatStateStore(root);
        store.Save(new(0, "codex.exe", "claude.exe", "host.exe", Environment.CurrentDirectory, "kept-session", "", "test-key", "gpt-6-astra", "ultra"));
        var panel = new ChatPanel(store);
        var models = new[] {
            new CodexModel("gpt-6-astra", "GPT-6-Astra", "medium", new[] { "low", "medium", "high", "ultra" }, true),
            new CodexModel("gpt-6-sol", "GPT-6-Sol", "low", new[] { "low", "medium", "high", "ultra" }, false),
            new CodexModel("gpt-6-luna", "GPT-6-Luna", "medium", new[] { "low", "medium", "high" }, false) };
        panel.ApplyModels(models);
        ComboBox Field(string name) => (ComboBox)typeof(ChatPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        var model = Field("model"); var reasoning = Field("reasoning");
        Assert((string)model.SelectedValue == "gpt-6-astra" && (string)reasoning.SelectedValue == "ultra", "Saved model not shown");
        model.SelectedValue = "gpt-6-luna";
        Assert((string)reasoning.SelectedValue == "", "Unsupported effort was retained after changing model");
        model.SelectedValue = "gpt-6-sol"; reasoning.SelectedValue = "high";
        var saved = store.Load()!;
        Assert(saved.CodexModel == "gpt-6-sol" && saved.CodexReasoningEffort == "high" && saved.SessionId == "kept-session", "Selection discarded history/settings");
        var restored = new ChatPanel(store); restored.ApplyModels(models);
        Assert((string)((ComboBox)typeof(ChatPanel).GetField("model", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(restored)!).SelectedValue == "gpt-6-sol", "Reopened panel lost model");
        Field("provider").SelectedIndex = 1;
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
        Field("provider").SelectedIndex = 0;
        Assert(model.IsEnabled && reasoning.IsEnabled, "Codex controls remained disabled");
        typeof(ChatPanel).GetField("acceptingInput", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel,true);
        typeof(ChatPanel).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel,new object[]{true});
        Button ButtonField(string name)=>(Button)typeof(ChatPanel).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(panel)!;
        Assert(ButtonField("send").IsEnabled&&ButtonField("attach").IsEnabled&&!model.IsEnabled&&ButtonField("send").Content.ToString()=="Дополнить","Running chat prevents adding instructions or files");
        typeof(ChatPanel).GetField("acceptingInput", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel,false);
        typeof(ChatPanel).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel,new object[]{false});
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
        var darkImage = new RenderTargetBitmap(480, 850, 96, 96, PixelFormats.Pbgra32); darkImage.Render(panel);
        var darkEncoder = new PngBitmapEncoder(); darkEncoder.Frames.Add(BitmapFrame.Create(darkImage));
        string darkPath = Path.Combine(root, "panel-dark.png");
        using (var file = File.Create(darkPath)) darkEncoder.Save(file);
        Console.WriteLine("PASS real WPF model/effort selection, attachment chips/removal, persistence and provider controls");
        Console.WriteLine("UI image: " + path);
        Console.WriteLine("Dark UI image: " + darkPath);
        var browserProbe = new ChatBrowser(Path.Combine(root, "browser-data"));
        var oldProject = new ChatStateStore(Path.Combine(root, "old-project"));
        oldProject.Save(new(0, "codex.exe", "claude.exe", "host.exe", root, "old-project-session", "Project A history"));
        var reboundPanel = new ChatPanel(oldProject);
        var nextProject = new ChatStateStore(Path.Combine(root, "next-project"));
        string nextDirectory = Path.Combine(root, "next-drawing-folder"); Directory.CreateDirectory(nextDirectory);
        var draft = (System.Windows.Controls.TextBox)typeof(ChatPanel).GetField("input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reboundPanel)!;
        draft.Text = "Unsent Save As draft";
        var runningField = typeof(ChatPanel).GetField("running", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using (var active = new CancellationTokenSource())
        {
            runningField.SetValue(reboundPanel, active);
            Assert(!reboundPanel.RebindProject(nextProject, root) && nextProject.Load() is null, "A running chat moved projects before its turn ended");
            runningField.SetValue(reboundPanel, null);
        }
        Assert(reboundPanel.RebindProject(nextProject, nextDirectory), "Save As did not rebind the chat");
        Assert(nextProject.Load()!.Directory == nextDirectory, "Save As retained the former project's working directory");
        Assert(oldProject.Load()!.SessionId == "old-project-session" && nextProject.Load()!.SessionId is null, "Save As shared a CLI writer between projects");
        Assert(nextProject.Load()!.Transcript.Contains("Project A history") && draft.Text == "Unsent Save As draft", "Save As lost history or the unsent draft");
        Console.WriteLine("PASS project rebinding: deferred while running, independent CLI session, history and draft preserved");
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
}
