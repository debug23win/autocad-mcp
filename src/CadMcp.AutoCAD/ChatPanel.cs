using System.Text.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CadMcp.Providers;
using CadMcp.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using Color = System.Windows.Media.Color;
using SystemColors = System.Windows.SystemColors;
using Brushes = System.Windows.Media.Brushes;

namespace CadMcp.AutoCAD;

internal sealed class ChatPanel : UserControl
{
    private readonly ComboBox provider = new() { ItemsSource = new[] { "Codex", "Claude Code" }, SelectedIndex = 0 };
    private sealed record Choice(string Id, string Label);
    private readonly ComboBox model = new() { DisplayMemberPath = "Label", SelectedValuePath = "Id" };
    private readonly ComboBox reasoning = new() { DisplayMemberPath = "Label", SelectedValuePath = "Id" };
    private readonly ComboBox agentCount = new() { ItemsSource = new[] { "Выключены", "До 2 помощников", "До 3 помощников", "До 4 помощников" }, SelectedIndex = 2 };
    private readonly Button refreshModels = new() { Content = "Обновить список моделей", Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBox executable = new() { Text = "codex.exe" };
    private readonly TextBox host = new() { Text = "Полный путь к CadMcp.Host.exe" };
    private readonly TextBox directory = new() { Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
    private readonly TextBox input = new() { AcceptsReturn = true, Height = 80, TextWrapping = TextWrapping.Wrap };
    private readonly List<ChatAttachment> attachments = new();
    private readonly WrapPanel attachmentItems = new();
    private readonly Button attach = new() { Content = "Прикрепить файл" };
    private readonly ChatBrowser transcript = new();
    private readonly List<ChatLine> messages = new();
    private readonly Button send = new() { Content = "Отправить" };
    private readonly TextBlock activity = new() { Text = "Готово", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock drawing = new() { Text = "Чертёж: подключение ещё не проверено", TextWrapping = TextWrapping.Wrap };
    private readonly System.Windows.Threading.DispatcherTimer contextTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly System.Windows.Threading.DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Response? cadContext;
    private bool readingContext;
    public string? CadSessionId { get; set; }
    public Func<bool>? DarkThemeProvider { get; set; }
    private bool darkTheme;
    private int selectedAnswer = -1;
    private int currentAssistantIndex = -1;
    private DateTimeOffset turnStarted, lastSignal;
    private string currentActivity = "Готово";
    private readonly SolidColorBrush panelBrush = new(), controlBrush = new(), foregroundBrush = new(), borderBrush = new();
    private readonly ChatStateStore store;
    private readonly StringBuilder streamedText = new();
    private readonly System.Windows.Threading.DispatcherTimer flushTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private string codexExecutable = "codex.exe", claudeExecutable = "claude.exe";
    private ChatState? restored;
    private CancellationTokenSource? running;
    private IChatProvider? adapter;
    private string? adapterKey;
    private string? adapterSelection;
    private string? chosenModel, chosenEffort, chosenClaudeModel, chosenClaudeEffort;
    private int maxSubagents = 3;
    private IReadOnlyList<CodexModel> catalog = Array.Empty<CodexModel>();
    private bool settingChoices, firstLoad, restoring;
    public ChatPanel() : this(null) { }
    internal ChatPanel(ChatStateStore? stateStore)
    {
        store = stateStore ?? new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CadMcp", "chat"));
        var assemblyDir = Path.GetDirectoryName(typeof(ChatPanel).Assembly.Location)!;
        var installedHost = Path.GetFullPath(Path.Combine(assemblyDir, "..", "Host", "CadMcp.Host.exe"));
        if (File.Exists(installedHost)) host.Text = installedHost;
        var codexPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        var bundledCodex = Path.GetFullPath(Path.Combine(assemblyDir, "..", "Tools", "Codex", "bin", "codex.exe"));
        var claudePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(codexPath)) codexExecutable = codexPath;
        if (File.Exists(bundledCodex)) codexExecutable = bundledCodex;
        if (File.Exists(claudePath)) claudeExecutable = claudePath;
        executable.Text = codexExecutable;
        try { if (store.Load() is { } state) Restore(File.Exists(bundledCodex) ? ChatStateStore.UseBundledCodex(state, bundledCodex, codexPath) : state); }
        catch (System.Exception e) { activity.Text = "История не восстановлена: " + e.Message; }
        InstallThemeStyles();
        var root = new DockPanel { Margin = new Thickness(8), AllowDrop = true }; Content = root;
        root.PreviewDragOver += (_, e) =>
        {
            e.Effects = running is null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        root.PreviewDrop += (_, e) =>
        {
            e.Handled = true;
            if (running is null && e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddAttachments(paths);
        };
        var settings = new StackPanel(); DockPanel.SetDock(settings, Dock.Top); root.Children.Add(settings);
        settings.Children.Add(drawing);
        var cadButtons = new WrapPanel(); settings.Children.Add(cadButtons);
        var connect = new Button { Content = "Проверить подключение" }; connect.Click += async (_, _) => await RefreshCad(); cadButtons.Children.Add(connect);
        var selection = new Button { Content = "Использовать выделение" }; selection.Click += async (_, _) =>
        {
            await RefreshCad();
            if (cadContext?.Data is JsonElement data && data.TryGetProperty("selection", out var selected))
                input.AppendText("\nИспользуй текущее выделение в чертеже «" + data.Text("name") + "»: " + selected.GetRawText() + ". Перед работой перечитай актуальный контекст.");
        }; cadButtons.Children.Add(selection);
        foreach (var pair in new (string Label, FrameworkElement Control)[] { ("Провайдер", provider), ("Модель", model), ("Глубина рассуждений", reasoning), ("Помощники", agentCount) })
        { settings.Children.Add(new TextBlock { Text = pair.Label }); settings.Children.Add(pair.Control); }
        settings.Children.Add(refreshModels);
        var connection = new StackPanel();
        foreach (var pair in new (string Label, FrameworkElement Control)[] { ("Официальный CLI (.exe)", executable), ("MCP host (.exe)", host), ("Рабочая папка", directory) })
        { connection.Children.Add(new TextBlock { Text = pair.Label }); connection.Children.Add(pair.Control); }
        settings.Children.Add(new Expander { Header = "Настройки подключения", Content = connection, Margin = new Thickness(0, 4, 0, 4) });
        var accountButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var login = new Button { Content = "Войти" }; login.Click += async (_, _) => await RunAccount(false); accountButtons.Children.Add(login);
        var diagnose = new Button { Content = "Диагностика" }; diagnose.Click += async (_, _) => await RunAccount(true); accountButtons.Children.Add(diagnose);
        var log = new Button { Content = "Журнал ошибок" }; log.Click += (_, _) =>
        {
            var path = LogPath(); if (!File.Exists(path)) { activity.Text = "Журнал ошибок пуст"; return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }; accountButtons.Children.Add(log); settings.Children.Add(accountButtons);
        int previousProvider = provider.SelectedIndex;
        provider.SelectionChanged += (_, _) =>
        {
            if (previousProvider == 0) codexExecutable = executable.Text; else claudeExecutable = executable.Text;
            executable.Text = provider.SelectedIndex == 0 ? codexExecutable : claudeExecutable;
            previousProvider = provider.SelectedIndex; adapter = null;
            ApplyProviderChoices();
            SetBusy(false);
            if (!restoring && provider.SelectedIndex == 0 && IsLoaded) _ = RefreshModels();
        };
        model.SelectionChanged += (_, _) =>
        {
            if (settingChoices) return;
            if (provider.SelectedIndex == 0)
            {
                chosenModel = model.SelectedValue as string;
                var selected = SelectedCatalogModel();
                if (selected is not null && !string.IsNullOrEmpty(chosenEffort) && !selected.Efforts.Contains(chosenEffort)) chosenEffort = null;
            }
            else chosenClaudeModel = model.SelectedValue as string;
            PopulateEfforts(); Persist();
        };
        reasoning.SelectionChanged += (_, _) =>
        {
            if (settingChoices) return;
            if (provider.SelectedIndex == 0) chosenEffort = reasoning.SelectedValue as string;
            else chosenClaudeEffort = reasoning.SelectedValue as string;
            Persist();
        };
        agentCount.SelectionChanged += (_, _) =>
        {
            if (restoring) return;
            maxSubagents = agentCount.SelectedIndex switch { 1 => 2, 2 => 3, 3 => 4, _ => 0 };
            Persist();
        };
        refreshModels.Click += async (_, _) => await RefreshModels();
        executable.LostKeyboardFocus += async (_, _) => { if (provider.SelectedIndex == 0 && running is null) await RefreshModels(); };
        directory.LostKeyboardFocus += async (_, _) => { if (provider.SelectedIndex == 0 && running is null) await RefreshModels(); };
        Loaded += async (_, _) => { if (!firstLoad) { firstLoad = true; if (provider.SelectedIndex == 0) await RefreshModels(); } };
        Loaded += async (_, _) => { ApplyTheme(); contextTimer.Start(); await RefreshCad(); };
        Unloaded += (_, _) => contextTimer.Stop();
        contextTimer.Tick += async (_, _) => { ApplyTheme(); if (running is null) await RefreshCad(); };
        ApplyProviderChoices();
        SetBusy(false);
        settings.Children.Add(activity);
        flushTimer.Tick += (_, _) => FlushText();
        progressTimer.Tick += (_, _) => UpdateProgress();
        transcript.Selected += index => { selectedAnswer = index; activity.Text = "Ответ выбран для сохранения в Word"; };
        transcript.OpenLink += url =>
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Scheme is "http" or "https")
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
        };
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(new TextBlock { Text = "Перетащите файлы в это окно или нажмите «Прикрепить файл»" });
        bottom.Children.Add(new ScrollViewer { Content = attachmentItems, MaxHeight = 85, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        bottom.Children.Add(input);
        input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
            { e.Handled = true; await Send(); }
        };
        bottom.Children.Add(new TextBlock { Text = "Enter — отправить; Shift+Enter — новая строка" });
        var row = new WrapPanel(); bottom.Children.Add(row);
        row.Children.Add(attach); attach.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Приложить файлы", Filter = "Все файлы (*.*)|*.*", Multiselect = true };
            if (dialog.ShowDialog() == true) AddAttachments(dialog.FileNames);
        };
        row.Children.Add(send); send.Click += async (_, _) => await Send();
        var stop = new Button { Content = "Стоп" }; stop.Click += (_, _) => Stop(); row.Children.Add(stop);
        var clear = new Button { Content = "Новый диалог" }; clear.Click += (_, _) => { if (running is null) { adapter = null; restored = null; messages.Clear(); selectedAnswer = -1; RenderChat(); attachments.Clear(); RefreshAttachments(); Persist(); } }; row.Children.Add(clear);
        var save = new Button { Content = "Сохранить историю" }; save.Click += (_, _) => SaveHistory(); row.Children.Add(save);
        var saveWord = new Button { Content = "Диалог в Word" }; saveWord.Click += (_, _) => SaveWord(false); row.Children.Add(saveWord);
        var saveAnswer = new Button { Content = "Ответ в Word" }; saveAnswer.Click += (_, _) => SaveWord(true); row.Children.Add(saveAnswer);
        var load = new Button { Content = "Открыть историю" }; load.Click += (_, _) => LoadHistory(); bottom.Children.Add(load);
        root.Children.Add(transcript);
        ApplyTheme();
        RenderChat();
    }
    public void Stop() { running?.Cancel(); FlushText(); Persist(); }
    private void AddAttachments(IEnumerable<string> paths)
    {
        int added = 0;
        var errors = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var item = ChatAttachments.Inspect(path);
                if (attachments.Any(a => string.Equals(a.Path, item.Path, StringComparison.OrdinalIgnoreCase))) continue;
                if (attachments.Count >= ChatAttachments.MaximumCount) throw new InvalidDataException("Можно приложить не более 10 файлов");
                attachments.Add(item); added++;
            }
            catch (System.Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { errors.Add(error.Message); }
        }
        RefreshAttachments();
        activity.Text = errors.Count > 0 ? string.Join("; ", errors.Take(2)) : added == 0 ? "Файлы уже приложены" : "Приложено файлов: " + attachments.Count;
    }
    private void RefreshAttachments()
    {
        attachmentItems.Children.Clear();
        foreach (var file in attachments)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 5, 2), ToolTip = file.Path };
            chip.Children.Add(new TextBlock { Text = file.Name + " (" + (file.Size < 1024 ? file.Size + " Б" : Math.Ceiling(file.Size / 1024d) + " КБ") + ")", VerticalAlignment = VerticalAlignment.Center, MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis });
            var remove = new Button { Content = "×", ToolTip = "Убрать вложение", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(4, 0, 4, 0) };
            remove.Click += (_, _) => { attachments.Remove(file); RefreshAttachments(); activity.Text = "Вложение убрано"; };
            chip.Children.Add(remove); attachmentItems.Children.Add(chip);
        }
    }
    private static string LogPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CadMcp", "logs", "panel.log");
    private static void LogError(System.Exception error)
    {
        try { var path = LogPath(); Directory.CreateDirectory(Path.GetDirectoryName(path)!); if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.WriteAllText(path, ""); File.AppendAllText(path, DateTimeOffset.Now + " " + error + Environment.NewLine); } catch (System.Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    private async Task RefreshCad()
    {
        if (readingContext || !File.Exists(host.Text)) return;
        readingContext = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await BrokerBootstrap.EnsureAsync(host.Text, timeout.Token);
            if (CadSessionId is null)
            {
                var descriptors = new Broker(Wire.WorkerRoot).Discover();
                if (descriptors.Count != 1) { cadContext = null; drawing.Text = descriptors.Count == 0 ? "AutoCAD не подключён: загрузите CAD MCP" : "Открыто несколько сессий: запустите чат из нужного AutoCAD"; return; }
                CadSessionId = descriptors[0].SessionId;
            }
            var context = await PipeClient.CallAsync(Wire.BrokerPipe, new(Guid.NewGuid().ToString("N"), "cad_context", CadSessionId), timeout.Token);
            if (context.Error is { } error) throw new IOException(error.Message);
            cadContext = context;
            if (context.Data is JsonElement data)
            {
                drawing.Text = "Чертёж: " + Path.GetFileName(data.Text("name")) + " · " + data.Text("units");
                if (data.TryGetProperty("editing", out var edit) && edit.TryGetProperty("pending_lisp", out var pending) && pending.ValueKind == JsonValueKind.String)
                    drawing.Text += " · CAD-скрипт ещё выполняется: " + pending.GetString();
            }
        }
        catch (System.Exception error) { cadContext = null; drawing.Text = "AutoCAD занят или недоступен: " + error.Message; }
        finally { readingContext = false; }
    }
    private async Task RunAccount(bool diagnostic)
    {
        if (running is not null) return;
        running = new(); SetBusy(true);
        try
        {
            running.CancelAfter(TimeSpan.FromMinutes(diagnostic ? 1 : 5));
            var options = new ProviderOptions(executable.Text, host.Text, directory.Text);
            bool codex = provider.SelectedIndex == 0;
            var command = codex ? diagnostic ? new[] { "login", "status" } : new[] { "login" }
                : diagnostic ? new[] { "auth", "status" } : new[] { "auth", "login" };
            using var process = System.Diagnostics.Process.Start(ProviderProcess.StartInfo(options, command))!;
            using var cancel = running.Token.Register(() => ProviderProcess.Stop(process));
            var stderr = ProviderProcess.DrainErrors(process); var stdout = process.StandardOutput.ReadToEndAsync(running.Token);
            activity.Text = diagnostic ? "Проверка входа…" : "Завершите вход в открывшемся браузере";
            await process.WaitForExitAsync(running.Token); await stdout; string detail = await stderr;
            string providerName = codex ? "Codex" : "Claude Code";
            activity.Text = process.ExitCode == 0 ? providerName + ": вход выполнен"
                : "Нужно войти в " + providerName + ": " + detail;
        }
        catch (OperationCanceledException) { activity.Text = "Вход или проверка остановлены"; }
        catch (System.Exception error) { LogError(error); activity.Text = "Подключение: " + error.Message; }
        finally { running.Dispose(); running = null; SetBusy(false); }
        await RefreshCad();
        if (!diagnostic && provider.SelectedIndex == 0) await RefreshModels();
    }
    private static string EffortLabel(string effort) => effort switch
    {
        "none" => "Без дополнительных рассуждений", "minimal" => "Минимальная", "low" => "Низкая",
        "medium" => "Средняя", "high" => "Высокая", "xhigh" => "Очень высокая", "max" => "Максимальная",
        "ultra" => "Ultra — с делегированием задач", _ => effort
    };
    private CodexModel? SelectedCatalogModel() => string.IsNullOrEmpty(chosenModel)
        ? catalog.FirstOrDefault(m => m.IsDefault) ?? catalog.FirstOrDefault()
        : catalog.FirstOrDefault(m => m.Id == chosenModel);
    internal void ApplyModels(IReadOnlyList<CodexModel> models)
    {
        catalog = models; settingChoices = true;
        if (provider.SelectedIndex != 0) { settingChoices = false; return; }
        var defaultModel = catalog.FirstOrDefault(m => m.IsDefault) ?? catalog.FirstOrDefault();
        var choices = new List<Choice> { new("", "Автоматически" + (defaultModel is null ? "" : " — " + defaultModel.DisplayName)) };
        choices.AddRange(catalog.Select(m => new Choice(m.Id, m.DisplayName)));
        if (!string.IsNullOrEmpty(chosenModel) && !catalog.Any(m => m.Id == chosenModel)) choices.Add(new(chosenModel, chosenModel + " — недоступна"));
        model.ItemsSource = choices; model.SelectedValue = chosenModel ?? "";
        settingChoices = false; PopulateEfforts();
    }
    private void ApplyProviderChoices()
    {
        if (provider.SelectedIndex == 0) { ApplyModels(catalog); return; }
        settingChoices = true;
        model.ItemsSource = new[] { new Choice("", "Автоматически"), new Choice("sonnet", "Claude Sonnet"),
            new Choice("opus", "Claude Opus"), new Choice("haiku", "Claude Haiku") };
        model.SelectedValue = chosenClaudeModel ?? "";
        settingChoices = false;
        PopulateEfforts();
    }
    private void PopulateEfforts()
    {
        settingChoices = true;
        if (provider.SelectedIndex != 0)
        {
            reasoning.ItemsSource = new[] { new Choice("", "По умолчанию"), new Choice("low", "Низкая"),
                new Choice("medium", "Средняя"), new Choice("high", "Высокая"),
                new Choice("xhigh", "Очень высокая"), new Choice("max", "Максимальная") };
            reasoning.SelectedValue = chosenClaudeEffort ?? "";
            settingChoices = false;
            return;
        }
        var selected = SelectedCatalogModel();
        var choices = new List<Choice> { new("", "По умолчанию" + (selected is null ? "" : " — " + EffortLabel(selected.DefaultEffort))) };
        if (selected is not null) choices.AddRange(selected.Efforts.Select(e => new Choice(e, EffortLabel(e))));
        if (!string.IsNullOrEmpty(chosenEffort) && !choices.Any(c => c.Id == chosenEffort)) choices.Add(new(chosenEffort, chosenEffort + " — недоступна"));
        reasoning.ItemsSource = choices; reasoning.SelectedValue = chosenEffort ?? "";
        settingChoices = false;
    }
    private void SetBusy(bool busy)
    {
        send.IsEnabled = attach.IsEnabled = provider.IsEnabled = executable.IsEnabled = host.IsEnabled = directory.IsEnabled = !busy;
        model.IsEnabled = reasoning.IsEnabled = !busy;
        refreshModels.IsEnabled = !busy && provider.SelectedIndex == 0;
        agentCount.IsEnabled = !busy;
    }
    private async Task RefreshModels()
    {
        if (running is not null || provider.SelectedIndex != 0) return;
        running = new(); SetBusy(true); activity.Text = "Загрузка доступных моделей…";
        try
        {
            if (!Directory.Exists(directory.Text)) throw new IOException("Укажите существующую рабочую папку в настройках подключения");
            var models = await CodexCatalog.ReadAsync(new(executable.Text, host.Text, directory.Text), running.Token);
            ApplyModels(models);
            activity.Text = !string.IsNullOrEmpty(chosenModel) && !catalog.Any(m => m.Id == chosenModel)
                ? "Сохранённая модель недоступна. Выберите другую модель." : "Доступно моделей: " + models.Count;
            Persist();
        }
        catch (OperationCanceledException) { activity.Text = "Загрузка моделей остановлена или истекло время ожидания"; }
        catch (System.Exception e) { activity.Text = "Модели не загружены: " + e.Message; }
        finally { running.Dispose(); running = null; SetBusy(false); }
    }
    private async Task Send()
    {
        if (running is not null || (string.IsNullOrWhiteSpace(input.Text) && attachments.Count == 0)) return;
        if (!File.Exists(host.Text) || !Directory.Exists(directory.Text))
        { activity.Text = "Укажите существующие MCP host и рабочую папку."; return; }
        running = new(); SetBusy(true);
        turnStarted = lastSignal = DateTimeOffset.Now;
        currentActivity = "Подключение";
        activity.Text = "Подключение…"; flushTimer.Start(); progressTimer.Start();
        string? initialDocumentId = null, initialSessionId = null;
        bool visibleCadResult = false;
        try
        {
            var files = ChatAttachments.Capture(attachments);
            await BrokerBootstrap.EnsureAsync(host.Text, running.Token);
            await RefreshCad();
            initialDocumentId = cadContext?.DocumentId; initialSessionId = cadContext?.SessionId;
            var key = provider.SelectedIndex + "|" + executable.Text + "|" + host.Text + "|" + directory.Text;
            var selectedModel = provider.SelectedIndex == 0 ? chosenModel : chosenClaudeModel;
            var selectedEffort = provider.SelectedIndex == 0 ? chosenEffort : chosenClaudeEffort;
            var selection = (selectedModel ?? "") + "|" + (selectedEffort ?? "") + "|" + maxSubagents;
            if (adapter is null || adapterKey != key || adapterSelection != selection)
            {
                var previousSession = adapterKey == key ? adapter?.SessionId : null;
                var options = new ProviderOptions(executable.Text, host.Text, directory.Text, selectedModel, selectedEffort,
                    MaxSubagents: maxSubagents);
                adapter = provider.SelectedIndex == 0 ? new CodexProvider(options) : new ClaudeProvider(options); adapterKey = key;
                adapterSelection = selection;
                adapter.SessionId = previousSession ?? (restored?.AdapterKey == key ? restored.SessionId : null);
                restored = null;
            }
            var prompt = string.IsNullOrWhiteSpace(input.Text) ? "Изучи приложенные файлы и скажи, чем можешь помочь." : input.Text;
            var images = files.Where(f => f.Kind == AttachmentKind.Image).Select(transcript.SaveImage).ToArray();
            messages.Add(new ChatLine("user", prompt, Images: images));
            currentAssistantIndex = messages.Count;
            messages.Add(new ChatLine("assistant", ""));
            input.Clear(); RenderChat();
            if (cadContext is { } target) prompt += "\nPanel target (verify fresh with CAD tools): session_id=" + target.SessionId + ", document_id=" + target.DocumentId + ". Work only in this drawing; if it changed, report the change.";
            await foreach (var item in adapter.SendAsync(prompt, running.Token, files))
            {
                lastSignal = DateTimeOffset.Now;
                if (item.Kind == "text") streamedText.Append(item.Text);
                if (item.Kind == "model") { FlushText(); UpdateAssistant(line => line with { Model = item.Text }); currentActivity = "Модель: " + item.Text; }
                if (item.Kind == "effort") { FlushText(); UpdateAssistant(line => line with { Effort = EffortLabel(item.Text) }); }
                if (item.Kind == "reasoning_summary") UpdateAssistant(line => line with { ReasoningSummary = (line.ReasoningSummary ?? "") + item.Text });
                if (item.Kind == "step")
                {
                    currentActivity = CadToolActivity.Describe(item.Text);
                    visibleCadResult |= CadToolActivity.ProducesVisibleResult(item.Text);
                }
                if (item.Kind == "status") currentActivity = item.Text;
                if (item.Kind == "session") Persist();
                UpdateProgress();
            }
            FlushText();
            await FinalizeCadResult(initialSessionId, initialDocumentId, visibleCadResult);
            attachments.Clear(); RefreshAttachments(); activity.Text = "Готово";
        }
        catch (OperationCanceledException)
        {
            FlushText(); UpdateAssistant(line => line with { Text = line.Text + "\n\nОстановлено. Уже принятые CAD-запросы могут завершиться." }); activity.Text = "Остановлено";
            await FinalizeCadResult(initialSessionId, initialDocumentId, false);
        }
        catch (System.Exception e)
        {
            LogError(e); FlushText(); UpdateAssistant(line => line with { Text = line.Text + "\n\nОшибка: " + e.Message }); activity.Text = "Ошибка; подробности в журнале";
            await FinalizeCadResult(initialSessionId, initialDocumentId, false);
        }
        finally { progressTimer.Stop(); flushTimer.Stop(); FlushText(); Persist(); running.Dispose(); running = null; currentAssistantIndex = -1; SetBusy(false); }
    }
    private async Task FinalizeCadResult(string? session, string? document, bool preview)
    {
        if (session is null || document is null) return;
        currentActivity = "Проверяю результат и сохранение DWG"; UpdateProgress();
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var response=await PipeClient.CallAsync(Wire.BrokerPipe,new(Guid.NewGuid().ToString("N"),"cad_operation_list",session,document,
                Data:Wire.Element(new{since=turnStarted.ToString("O"),limit=50})),timeout.Token);
            if(response.Error is not null)throw new IOException(response.Error.Message);
            if(response.Data is not JsonElement data || !data.TryGetProperty("operations",out var ops) || ops.GetArrayLength()==0)
            { if(preview)await AttachPreview(document); return; }
            var records=ops.EnumerateArray().ToArray(); JsonElement? review=null;
            var completed=records.FirstOrDefault(o=>o.Text("state")=="completed" && o.Text("operation")=="cad_edit");
            if(completed.ValueKind==JsonValueKind.Object)
            {
                await RefreshCad();
                if(cadContext is { } context && context.DocumentId==document && context.SessionId==session)
                {
                    var checkedResult=await PipeClient.CallAsync(Wire.BrokerPipe,new(Guid.NewGuid().ToString("N"),"cad_verify",session,document,context.Revision,
                        Wire.Element(new{operation_id=completed.Text("operation_id")})),timeout.Token);
                    if(checkedResult.Error is null && checkedResult.Data is JsonElement checkedData) review=checkedData;
                }
            }
            var summary=CadResultSummary.Describe(data,review);
            UpdateAssistant(line=>line with{Text=line.Text+"\n\n"+summary});
            bool pending=records.Any(o=>o.Text("state") is "queued" or "running" or "unknown");
            if(!pending && (preview || completed.ValueKind==JsonValueKind.Object))await AttachPreview(document);
        }
        catch(System.Exception error) when(error is IOException or OperationCanceledException or InvalidOperationException)
        {
            LogError(error); UpdateAssistant(line=>line with{Text=line.Text+"\n\nПроверка результата плагином не завершена: "+error.Message+". Сохранение DWG не подтверждено."});
        }
    }
    private async Task AttachPreview(string? initialDocumentId)
    {
        currentActivity = "Получаю итоговый вид чертежа";
        UpdateProgress();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await RefreshCad();
            var context = cadContext ?? throw new IOException("подключение к AutoCAD недоступно");
            if (initialDocumentId is null || context.DocumentId != initialDocumentId)
                throw new IOException("активный чертёж изменился");
            var result = await PipeClient.CallAsync(Wire.BrokerPipe,
                new(Guid.NewGuid().ToString("N"), "cad_render", context.SessionId, context.DocumentId,
                    context.Revision, Wire.Element(new { width = 1024, height = 768 })), timeout.Token);
            if (result.Error is not null) throw new IOException(result.Error.Message);
            if (result.Data is not JsonElement data) throw new IOException("AutoCAD не вернул изображение");
            var preview = transcript.SavePreview(data.Text("image_base64") ?? throw new IOException("изображение пусто"),
                data.Number("width", 1024), data.Number("height", 768));
            UpdateAssistant(line => line with { Images = (line.Images ?? Array.Empty<ChatImage>()).Append(preview).ToArray() });
        }
        catch (System.Exception error) when (error is IOException or OperationCanceledException or FormatException or InvalidDataException)
        {
            LogError(error);
            UpdateAssistant(line => line with { Text = line.Text + "\n\nИтоговый вид не получен: " + error.Message + ". Проверьте открытый чертёж перед сохранением." });
        }
    }
    private void FlushText()
    {
        if (streamedText.Length == 0) return;
        string text = streamedText.ToString(); streamedText.Clear();
        UpdateAssistant(line => line with { Text = line.Text + text });
    }
    private void UpdateAssistant(Func<ChatLine, ChatLine> update)
    {
        if (currentAssistantIndex < 0 || currentAssistantIndex >= messages.Count) return;
        messages[currentAssistantIndex] = update(messages[currentAssistantIndex]);
        RenderChat();
    }
    private void RenderChat() => transcript.Update(messages, darkTheme, selectedAnswer);
    private void UpdateProgress()
    {
        if (running is null || !progressTimer.IsEnabled) return;
        int elapsed = (int)(DateTimeOffset.Now - turnStarted).TotalSeconds;
        int silence = (int)(DateTimeOffset.Now - lastSignal).TotalSeconds;
        activity.Text = currentActivity + " · " + elapsed + " с" +
            (silence >= 15 ? " · нет новых событий " + silence + " с; можно остановить" : "");
    }
    private ChatState State()
    {
        if (provider.SelectedIndex == 0) codexExecutable = executable.Text; else claudeExecutable = executable.Text;
        return new(provider.SelectedIndex, codexExecutable, claudeExecutable, host.Text, directory.Text,
            adapter?.SessionId ?? restored?.SessionId, ChatMarkup.PlainTranscript(messages),
            adapterKey ?? restored?.AdapterKey, chosenModel, chosenEffort, messages.ToArray(), maxSubagents,
            chosenClaudeModel, chosenClaudeEffort);
    }
    private void Persist()
    { try { store.Save(State()); } catch (System.Exception e) { activity.Text = "История не сохранена: " + e.Message; } }
    private void Restore(ChatState state)
    {
        if (!ChatStateStore.Valid(state)) throw new InvalidDataException("Файл не соответствует формату истории CAD MCP");
        restoring = true;
        try
        {
        provider.SelectedIndex = state.Provider;
        codexExecutable = state.CodexExecutable; claudeExecutable = state.ClaudeExecutable;
        executable.Text = state.Provider == 0 ? codexExecutable : claudeExecutable;
        if (File.Exists(state.Host)) host.Text = state.Host;
        if (Directory.Exists(state.Directory)) directory.Text = state.Directory;
        messages.Clear();
        if (state.Messages is { Count: > 0 }) messages.AddRange(state.Messages);
        else if (!string.IsNullOrWhiteSpace(state.Transcript)) messages.Add(new ChatLine("history", state.Transcript));
        selectedAnswer = -1; RenderChat(); restored = state; adapter = null; adapterKey = null; attachments.Clear(); RefreshAttachments();
        chosenModel = state.CodexModel; chosenEffort = state.CodexReasoningEffort;
        chosenClaudeModel = state.ClaudeModel; chosenClaudeEffort = state.ClaudeReasoningEffort;
        maxSubagents = state.MaxSubagents;
        agentCount.SelectedIndex = maxSubagents switch { 2 => 1, 3 => 2, 4 => 3, _ => 0 };
        ApplyProviderChoices();
        activity.Text = "История восстановлена; актуальный чертёж будет прочитан заново";
        }
        finally { restoring = false; }
    }
    private void SaveHistory()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "cad-mcp-chat.json" };
        if (dialog.ShowDialog() == true)
            try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(State(), new JsonSerializerOptions { WriteIndented = true })); }
            catch (System.Exception e) { activity.Text = "История не экспортирована: " + e.Message; }
    }
    private void LoadHistory()
    {
        if (running is not null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "История CAD MCP (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 4 * 1024 * 1024) throw new InvalidDataException("Файл истории слишком велик");
            Restore(JsonSerializer.Deserialize<ChatState>(File.ReadAllText(dialog.FileName)) ?? throw new InvalidDataException("Пустой файл истории")); Persist();
            if (provider.SelectedIndex == 0) _ = RefreshModels();
        }
        catch (System.Exception e) { activity.Text = "История не открыта: " + e.Message; }
    }
    private void SaveWord(bool answerOnly)
    {
        IReadOnlyList<ChatLine> selected = messages;
        if (answerOnly)
        {
            int index = selectedAnswer >= 0 && selectedAnswer < messages.Count && messages[selectedAnswer].Role == "assistant"
                ? selectedAnswer : messages.FindLastIndex(line => line.Role == "assistant");
            if (index < 0) { activity.Text = "В диалоге пока нет ответа для сохранения"; return; }
            selected = new[] { messages[index] };
        }
        if (selected.Count == 0) { activity.Text = "Диалог пока пуст"; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Документ Word (*.docx)|*.docx",
            FileName = answerOnly ? "cad-mcp-answer.docx" : "cad-mcp-chat.docx"
        };
        if (dialog.ShowDialog() != true) return;
        try { ChatWordExporter.Save(dialog.FileName, selected); activity.Text = "Сохранено в Word: " + dialog.FileName; }
        catch (System.Exception error) { LogError(error); activity.Text = "Не удалось сохранить Word: " + error.Message; }
    }
    private void InstallThemeStyles()
    {
        Background = panelBrush;
        Foreground = foregroundBrush;
    }
    private void ApplyTheme()
    {
        bool next;
        try { next = DarkThemeProvider?.Invoke() ?? SystemColors.WindowColor.R < 128; }
        catch (System.Exception) { next = SystemColors.WindowColor.R < 128; }
        if (darkTheme == next && panelBrush.Color.A != 0) return;
        darkTheme = next;
        panelBrush.Color = next ? Color.FromRgb(37, 40, 45) : Color.FromRgb(245, 246, 248);
        controlBrush.Color = next ? Color.FromRgb(47, 52, 59) : Colors.White;
        foregroundBrush.Color = next ? Color.FromRgb(239, 242, 245) : Color.FromRgb(32, 35, 42);
        borderBrush.Color = next ? Color.FromRgb(82, 89, 99) : Color.FromRgb(190, 197, 207);
        void Paint(DependencyObject node)
        {
            if (node is ChatBrowser) return;
            if (node is System.Windows.Controls.Control control && node != this)
            {
                control.Background = controlBrush;
                control.Foreground = foregroundBrush;
                control.BorderBrush = borderBrush;
                // Windows' default ComboBox template keeps a light selection field
                // even when hosted in a dark AutoCAD palette.
                if (control is ComboBox && next) control.Foreground = Brushes.Black;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Paint(child);
        }
        Paint(this);
        RenderChat();
    }
}
