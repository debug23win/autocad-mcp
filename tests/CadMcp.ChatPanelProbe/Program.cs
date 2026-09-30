using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CadMcp.AutoCAD;
using CadMcp.Providers;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using Size = System.Windows.Size;

internal static class Program
{
    [STAThread]
    private static void Main()
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
        Assert(!model.IsEnabled && !reasoning.IsEnabled, "Codex controls applied to Claude");
        Field("provider").SelectedIndex = 0;
        Assert(model.IsEnabled && reasoning.IsEnabled, "Codex controls remained disabled");
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
        panel.Background = System.Windows.Media.Brushes.White;
        panel.Measure(new Size(480, 850)); panel.Arrange(new Rect(0, 0, 480, 850)); panel.UpdateLayout();
        var image = new RenderTargetBitmap(480, 850, 96, 96, PixelFormats.Pbgra32); image.Render(panel);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        string path = Path.Combine(root, "panel.png");
        using (var file = File.Create(path)) encoder.Save(file);
        Console.WriteLine("PASS real WPF model/effort selection, attachment chips/removal, persistence and provider controls");
        Console.WriteLine("UI image: " + path);
    }
}
