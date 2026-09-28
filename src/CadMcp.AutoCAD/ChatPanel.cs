using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CadMcp.Providers;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using UserControl = System.Windows.Controls.UserControl;

namespace CadMcp.AutoCAD;

internal sealed class ChatPanel : UserControl
{
    private readonly ComboBox provider = new() { ItemsSource = new[] { "Codex", "Claude Code" }, SelectedIndex = 0 };
    private readonly TextBox executable = new() { Text = "codex.exe" };
    private readonly TextBox host = new() { Text = "Полный путь к CadMcp.Host.exe" };
    private readonly TextBox directory = new() { Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
    private readonly TextBox input = new() { AcceptsReturn = true, Height = 80, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox transcript = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button send = new() { Content = "Отправить" };
    private CancellationTokenSource? running;
    private IChatProvider? adapter;
    private string? adapterKey;
    public ChatPanel()
    {
        var root = new DockPanel { Margin = new Thickness(8) }; Content = root;
        var settings = new StackPanel(); DockPanel.SetDock(settings, Dock.Top); root.Children.Add(settings);
        foreach (var pair in new (string Label, FrameworkElement Control)[] { ("Провайдер", provider), ("Официальный CLI (.exe)", executable), ("MCP host (.exe)", host), ("Рабочая папка", directory) })
        { settings.Children.Add(new TextBlock { Text = pair.Label }); settings.Children.Add(pair.Control); }
        settings.Children.Add(new TextBlock { Text = "Прототип: сначала запустите broker. Вход в подписку — через официальный CLI.", TextWrapping = TextWrapping.Wrap });
        provider.SelectionChanged += (_, _) => { executable.Text = provider.SelectedIndex == 0 ? "codex.exe" : "claude.exe"; adapter = null; };
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(input);
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal }; bottom.Children.Add(row);
        row.Children.Add(send); send.Click += async (_, _) => await Send();
        var stop = new Button { Content = "Стоп" }; stop.Click += (_, _) => Stop(); row.Children.Add(stop);
        var clear = new Button { Content = "Новый диалог" }; clear.Click += (_, _) => { if (running is null) { adapter = null; transcript.Clear(); } }; row.Children.Add(clear);
        var save = new Button { Content = "Сохранить историю" }; save.Click += (_, _) => SaveHistory(); row.Children.Add(save);
        root.Children.Add(transcript);
    }
    public void Stop() => running?.Cancel();
    private async Task Send()
    {
        if (running is not null || string.IsNullOrWhiteSpace(input.Text)) return;
        if (!File.Exists(host.Text) || !Directory.Exists(directory.Text)) { transcript.AppendText("\nУкажите существующие MCP host и рабочую папку.\n"); return; }
        running = new(); send.IsEnabled = false; provider.IsEnabled = false;
        try
        {
            var key = provider.SelectedIndex + "|" + executable.Text + "|" + host.Text + "|" + directory.Text;
            if (adapter is null || adapterKey != key)
            {
                var options = new ProviderOptions(executable.Text, host.Text, directory.Text);
                adapter = provider.SelectedIndex == 0 ? new CodexProvider(options) : new ClaudeProvider(options); adapterKey = key;
            }
            var prompt = input.Text; input.Clear(); transcript.AppendText("\nВы: " + prompt + "\nАссистент: ");
            await foreach (var item in adapter.SendAsync(prompt, running.Token))
            {
                if (item.Kind == "text") { transcript.AppendText(item.Text); transcript.ScrollToEnd(); }
            }
            transcript.AppendText("\n");
        }
        catch (OperationCanceledException) { transcript.AppendText("\nОстановлено. Уже принятые CAD-запросы могут завершиться до истечения своего срока.\n"); }
        catch (System.Exception e) { transcript.AppendText("\nОшибка: " + e.Message + "\n"); }
        finally { running.Dispose(); running = null; send.IsEnabled = true; provider.IsEnabled = true; }
    }
    private void SaveHistory()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "cad-mcp-chat.json" };
        if (dialog.ShowDialog() == true)
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { provider = provider.Text, session_id = adapter?.SessionId, transcript = transcript.Text }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
