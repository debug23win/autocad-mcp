using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CadMcp.Core;
using CadMcp.Providers;
using UserControl = System.Windows.Controls.UserControl;
using TabControl = System.Windows.Controls.TabControl;
using Button = System.Windows.Controls.Button;

namespace CadMcp.AutoCAD;

public sealed class ChatWorkspace : UserControl
{
    private sealed class Entry(TabItem tab, ChatPanel panel, DrawingTabSlot slot)
    {
        public TabItem Tab = tab;
        public ChatPanel Panel = panel;
        public DrawingTabSlot Slot = slot;
        public TextBlock Label = new();
    }
    private readonly TabControl tabs = new();
    private readonly TextBlock status = new();
    private readonly DrawingTabs tabState = new();
    private readonly Dictionary<string, Entry> projects = new();
    private readonly List<Entry> retiring = [];
    private readonly string host, brokerPipe;
    private readonly string? projectStoreRoot;
    private readonly Button benchmark = new() { Content = UiText.T("Бенчмарк"), Margin = new Thickness(4), ToolTip = UiText.T("Полный нативный прогон в отдельном AutoCAD без открытых DWG; при первой неожиданной ошибке останавливается") };
    private readonly Button stopBenchmark = new() { Content = UiText.T("Остановить бенчмарк"), Margin = new Thickness(4), IsEnabled = false };
    private CancellationTokenSource? benchmarkCancellation;
    private string? benchmarkDocument;
    private bool refreshing, darkTheme = true;
    private readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    public string? CadSessionId { get; set; }
    public Func<bool>? DarkThemeProvider { get; set; }

    public ChatWorkspace(string? hostExecutable = null, string? pipeName = null, string? historyRoot = null)
    {
        host = hostExecutable ?? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(ChatWorkspace).Assembly.Location)!, "..", "Host", "CadMcp.Host.exe"));
        brokerPipe = pipeName ?? Wire.BrokerPipe; projectStoreRoot = historyRoot;
        var root = new DockPanel(); var header = new WrapPanel();
        var refresh = new Button { Content = UiText.T("Открытые DWG"), Margin = new Thickness(4), ToolTip = UiText.T("Обновить список и вернуть закрытые вкладки чатов") };
        refresh.Click += async (_, _) => await Refresh(revealHidden: true);
        benchmark.Click += async (_, _) => await RunBenchmark();
        stopBenchmark.Click += (_, _) => benchmarkCancellation?.Cancel();
        header.Children.Add(refresh); header.Children.Add(benchmark); header.Children.Add(stopBenchmark); header.Children.Add(status);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header); root.Children.Add(tabs); Content = root;
        ChatTheme.Apply(this, true);
        Loaded += async (_, _) => { timer.Start(); await Refresh(); };
        Unloaded += (_, _) => timer.Stop(); timer.Tick += async (_, _) => await Refresh();
    }

    private ChatPanel Panel(DrawingTabSlot slot)
    {
        string storeRoot = projectStoreRoot is null ? Wire.DataDirectory("chat", "projects", slot.Document.ProjectKey) : Path.Combine(projectStoreRoot, slot.Document.ProjectKey);
        var panel = new ChatPanel(new ChatStateStore(storeRoot), Path.IsPathFullyQualified(slot.Document.Name) ? Path.GetDirectoryName(slot.Document.Name) : null, brokerPipe)
        { CadSessionId = CadSessionId, CadDocumentId = slot.Document.DocumentId, DarkThemeProvider = () => DarkThemeProvider?.Invoke() ?? darkTheme };
        panel.ResetDocumentSession(); return panel;
    }

    public async Task Refresh(bool revealHidden = false)
    {
        if (refreshing) return; refreshing = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await BrokerBootstrap.EnsureAsync(host, timeout.Token, brokerPipe);
            if (CadSessionId is null)
            {
                var workers = new Broker(Wire.WorkerRoot).Discover();
                if (workers.Count != 1) throw new IOException(UiText.T("Откройте чат из нужного AutoCAD")); CadSessionId = workers[0].SessionId;
            }
            var reply = await PipeClient.CallAsync(brokerPipe, new(Guid.NewGuid().ToString("N"), "cad_documents", CadSessionId), timeout.Token);
            if (reply.Error is { } error) throw new IOException(error.Message);
            var data = Wire.Element(reply.Data!);
            var drawings = data.EnumerateArray().Select(d => new DrawingTabDocument(d.Text("document_id")!, d.Text("project_key")!, d.Text("name")!, d.GetProperty("active").GetBoolean())).ToArray();
            if (data.GetArrayLength() > 0) darkTheme = data[0].GetProperty("dark_theme").GetBoolean();
            TabItem? active = null;
            retiring.RemoveAll(p => !p.Panel.IsWorking);
            foreach (var slot in tabState.Refresh(drawings, revealHidden))
            {
                if (!projects.TryGetValue(slot.Key, out var entry))
                {
                    if (retiring.Any(p => p.Slot.Document.ProjectKey == slot.Document.ProjectKey && p.Panel.IsWorking))
                    { status.Text = UiText.T("Дожидаюсь остановки прежнего чата"); continue; }
                    var panel = Panel(slot); entry = new(new TabItem { Content = panel }, panel, slot);
                    var tabHeader = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal }; tabHeader.Children.Add(entry.Label);
                    var close = new Button { Content = "×", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(7, 0, 0, 0), ToolTip = UiText.T("Закрыть вкладку чата; DWG останется открыт в AutoCAD") };
                    string key = slot.Key; close.Click += (_, e) => { e.Handled = true; CloseTab(key); };
                    tabHeader.Children.Add(close); entry.Tab.Header = tabHeader; projects.Add(slot.Key, entry); tabs.Items.Add(entry.Tab);
                }
                else if (entry.Slot.Document.DocumentId != slot.Document.DocumentId)
                {
                    if (entry.Panel.IsWorking) { entry.Panel.Stop(); status.Text = UiText.T("Завершаю прежний чат перед перепривязкой DWG"); continue; }
                    entry.Panel.Stop(); entry.Panel.CloseView(); entry.Panel = Panel(slot); entry.Tab.Content = entry.Panel;
                }
                if (entry.Slot.Open && !slot.Open) entry.Panel.Stop();
                entry.Slot = slot; entry.Panel.SetDocumentAvailable(slot.Open);
                entry.Panel.SetBenchmarkBusy(benchmarkCancellation is not null && slot.Document.DocumentId == benchmarkDocument);
                entry.Label.Text = Path.GetFileName(slot.Document.Name) + (!slot.Open ? UiText.T(" (закрыт)") : entry.Panel.IsWorking ? UiText.T(" · выполняется") : "");
                entry.Tab.ToolTip = slot.Document.Name;
                bool dark = DarkThemeProvider?.Invoke() ?? darkTheme;
                ChatTheme.Apply(this, dark);
                var background = dark ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(57, 67, 81)) : System.Windows.Media.Brushes.White;
                var foreground = dark ? System.Windows.Media.Brushes.Gainsboro : System.Windows.Media.Brushes.Black;
                Background = tabs.Background = entry.Tab.Background = background; Foreground = tabs.Foreground = entry.Tab.Foreground = foreground;
                if (slot.Open && slot.Document.Active) active = entry.Tab;
            }
            if (tabs.SelectedItem is null || projects.Values.Any(p => ReferenceEquals(p.Tab, tabs.SelectedItem) && !p.Slot.Open))
                if (active is not null) tabs.SelectedItem = active;
            if (benchmarkCancellation is null) status.Text = UiText.T("Каждая вкладка работает со своим DWG");
        }
        catch (System.Exception e) { status.Text = e.Message; }
        finally { refreshing = false; }
    }

    private void CloseTab(string key)
    {
        if (!projects.Remove(key, out var entry)) return;
        if (entry.Slot.Document.DocumentId == benchmarkDocument) benchmarkCancellation?.Cancel();
        entry.Panel.Stop(); entry.Panel.CloseView();
        if (entry.Panel.IsWorking) retiring.Add(entry);
        tabState.Close(key); tabs.Items.Remove(entry.Tab);
    }

    private async Task RunBenchmark()
    {
        if (benchmarkCancellation is not null) return;
        if (projects.Values.Any(p => p.Panel.IsWorking)) { status.Text = UiText.T("Сначала дождитесь завершения задач в чатах"); return; }
        using var cancellation = new CancellationTokenSource(); benchmarkCancellation = cancellation;
        benchmark.IsEnabled = false; stopBenchmark.IsEnabled = true;
        try
        {
            await Refresh(); if (CadSessionId is null) throw new IOException(UiText.T("AutoCAD не подключён"));
            status.Text = UiText.T("Нативный бенчмарк: только отдельный AutoCAD без открытых DWG. Не открывайте файлы во время прогона.");
            string runId = Guid.NewGuid().ToString("N");
            var benchmarkClock = System.Diagnostics.Stopwatch.StartNew();
            await using var mcp = await BenchmarkMcpSession.StartAsync(host, brokerPipe, runId, cancellation.Token);
            var runner = new CadBenchmark((r, ct) => r.Operation.StartsWith("benchmark_", StringComparison.Ordinal) ? PipeClient.CallAsync(brokerPipe, r, ct) : mcp.CallAsync(r, ct), toolInventory: mcp.Tools, runId: runId);
            var result = await runner.RunAsync(CadSessionId, progress => { benchmarkDocument = progress.DocumentId ?? benchmarkDocument; status.Text = progress.Message; }, cancellation.Token);
            result = result with { ElapsedMs = benchmarkClock.Elapsed.TotalMilliseconds };
            string folder = Wire.DataDirectory("benchmarks", result.RunId);
            string path = Path.Combine(folder, "report.json");
            string summary = result.Markdown();
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(path, JsonSerializer.Serialize(new { result, mcp_inventory = mcp.Tools, mcp_calls = mcp.Evidence }, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
                summary += UiText.T("\n\nJSON-отчёт: `") + path + UiText.T("`\nНативная трассировка: `") + Path.Combine(folder, "native-trace.jsonl") + UiText.T("` (если worker начал выполнение).");
            }
            catch (System.Exception error) when (error is IOException or UnauthorizedAccessException)
            { summary += UiText.T("\n\nJSON-отчёт не сохранён: ") + error.Message; }
            summary += AnswerMetrics.Footer(TimeSpan.FromMilliseconds(result.ElapsedMs), new ProviderTokenUsage(0, 0, 0, UiText.T("модели не вызывались")));
            await Refresh(); var target = projects.Values.FirstOrDefault(p => p.Slot.Document.DocumentId == result.DocumentId);
            if (target is not null) { target.Panel.AddBenchmarkReport(summary); tabs.SelectedItem = target.Tab; }
            else System.Windows.MessageBox.Show(summary, UiText.T("Результат бенчмарка"));
            status.Text = UiText.Format($"Бенчмарк: {result.Passed}/{result.Total}, {result.ElapsedMs / 1000.0:0.0} с");
        }
        catch (System.Exception error) { status.Text = UiText.T("Бенчмарк не завершён: ") + error.Message; }
        finally
        {
            benchmarkCancellation = null; benchmarkDocument = null; benchmark.IsEnabled = true; stopBenchmark.IsEnabled = false;
            foreach (var entry in projects.Values) entry.Panel.SetBenchmarkBusy(false);
        }
    }

    public void Stop() { timer.Stop(); benchmarkCancellation?.Cancel(); foreach (var entry in projects.Values) entry.Panel.Stop(); }
    internal IReadOnlyDictionary<string, ChatPanel> ProjectPanels => projects.Values.Where(p => p.Slot.Open).ToDictionary(p => p.Slot.Document.DocumentId, p => p.Panel);
    internal int TabCount => projects.Count;
    internal void CloseProject(string document) { var entry = projects.Values.FirstOrDefault(p => p.Slot.Document.DocumentId == document); if (entry is not null) CloseTab(entry.Slot.Key); }
    internal void SelectProject(string document) { var entry = projects.Values.FirstOrDefault(p => p.Slot.Document.DocumentId == document); if (entry is not null) tabs.SelectedItem = entry.Tab; }
}
