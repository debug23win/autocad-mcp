using System.Text.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CadMcp.Providers;
using CadMcp.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;

namespace CadMcp.AutoCAD;

internal sealed class ChatPanel : UserControl
{
    private readonly ComboBox provider = new() { ItemsSource = new[] { "Codex", "Claude Code" }, SelectedIndex = 0 };
    private sealed record Choice(string Id, string Label);
    private readonly ComboBox model = new() { DisplayMemberPath = "Label", SelectedValuePath = "Id" };
    private readonly ComboBox reasoning = new() { DisplayMemberPath = "Label", SelectedValuePath = "Id" };
    private readonly Button refreshModels = new() { Content = "Обновить список моделей", Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBox executable = new() { Text = "codex.exe" };
    private readonly TextBox host = new() { Text = "Полный путь к CadMcp.Host.exe" };
    private readonly TextBox directory = new() { Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
    private readonly TextBox input = new() { AcceptsReturn = true, Height = 80, TextWrapping = TextWrapping.Wrap };
    private readonly List<ChatAttachment> attachments = new();
    private readonly WrapPanel attachmentItems = new();
    private readonly Button attach = new() { Content = "Прикрепить файл" };
    private readonly TextBox transcript = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button send = new() { Content = "Отправить" };
    private readonly TextBlock activity = new() { Text = "Готово", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock drawing = new() { Text = "Чертёж: подключение ещё не проверено", TextWrapping = TextWrapping.Wrap };
    private readonly System.Windows.Controls.ListBox objects = new() { DisplayMemberPath = "Label", MaxHeight = 90 };
    private readonly System.Windows.Threading.DispatcherTimer contextTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Response? cadContext;
    private bool readingContext;
    public string? CadSessionId { get; set; }
    private readonly ChatStateStore store;
    private readonly StringBuilder streamedText = new();
    private readonly System.Windows.Threading.DispatcherTimer flushTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private string codexExecutable = "codex.exe", claudeExecutable = "claude.exe";
    private ChatState? restored;
    private CancellationTokenSource? running;
    private IChatProvider? adapter;
    private string? adapterKey;
    private string? adapterSelection;
    private string? chosenModel, chosenEffort;
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
        var focus = new Button { Content = "Показать объект" }; focus.Click += async (_, _) => await FocusObject(); cadButtons.Children.Add(focus);
        foreach (var pair in new (string Label, FrameworkElement Control)[] { ("Провайдер", provider), ("Модель Codex", model), ("Глубина рассуждений", reasoning) })
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
            SetBusy(false);
            if (!restoring && provider.SelectedIndex == 0 && IsLoaded) _ = RefreshModels();
        };
        model.SelectionChanged += (_, _) =>
        {
            if (settingChoices) return;
            chosenModel = model.SelectedValue as string;
            var selected = SelectedCatalogModel();
            if (selected is not null && !string.IsNullOrEmpty(chosenEffort) && !selected.Efforts.Contains(chosenEffort)) chosenEffort = null;
            PopulateEfforts(); Persist();
        };
        reasoning.SelectionChanged += (_, _) => { if (!settingChoices) { chosenEffort = reasoning.SelectedValue as string; Persist(); } };
        refreshModels.Click += async (_, _) => await RefreshModels();
        executable.LostKeyboardFocus += async (_, _) => { if (provider.SelectedIndex == 0 && running is null) await RefreshModels(); };
        directory.LostKeyboardFocus += async (_, _) => { if (provider.SelectedIndex == 0 && running is null) await RefreshModels(); };
        Loaded += async (_, _) => { if (!firstLoad) { firstLoad = true; if (provider.SelectedIndex == 0) await RefreshModels(); } };
        Loaded += async (_, _) => { contextTimer.Start(); await RefreshCad(); };
        Unloaded += (_, _) => contextTimer.Stop();
        contextTimer.Tick += async (_, _) => { if (running is null) await RefreshCad(); };
        ApplyModels(catalog);
        SetBusy(false);
        settings.Children.Add(activity);
        flushTimer.Tick += (_, _) => FlushText();
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(new TextBlock { Text = "Перетащите файлы в это окно или нажмите «Прикрепить файл»" });
        bottom.Children.Add(new ScrollViewer { Content = attachmentItems, MaxHeight = 85, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        bottom.Children.Add(input);
        bottom.Children.Add(objects);
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
        var clear = new Button { Content = "Новый диалог" }; clear.Click += (_, _) => { if (running is null) { adapter = null; restored = null; transcript.Clear(); attachments.Clear(); RefreshAttachments(); Persist(); } }; row.Children.Add(clear);
        var save = new Button { Content = "Сохранить историю" }; save.Click += (_, _) => SaveHistory(); row.Children.Add(save);
        var load = new Button { Content = "Открыть историю" }; load.Click += (_, _) => LoadHistory(); bottom.Children.Add(load);
        root.Children.Add(transcript);
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
            catch (System.Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
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
                if (descriptors.Count != 1) { cadContext = null; objects.ItemsSource = null; drawing.Text = descriptors.Count == 0 ? "AutoCAD не подключён: загрузите CAD MCP" : "Открыто несколько сессий: запустите чат из нужного AutoCAD"; return; }
                CadSessionId = descriptors[0].SessionId;
            }
            var context = await PipeClient.CallAsync(Wire.BrokerPipe, new(Guid.NewGuid().ToString("N"), "cad_context", CadSessionId), timeout.Token);
            if (context.Error is { } error) throw new IOException(error.Message);
            if (cadContext?.DocumentId != context.DocumentId) objects.ItemsSource = null;
            cadContext = context;
            if (context.Data is JsonElement data)
            {
                drawing.Text = "Чертёж: " + Path.GetFileName(data.Text("name")) + " · " + data.Text("units");
                if (data.TryGetProperty("editing", out var edit) && edit.TryGetProperty("pending_lisp", out var pending) && pending.ValueKind == JsonValueKind.String)
                    drawing.Text += " · CAD-скрипт ещё выполняется: " + pending.GetString();
            }
        }
        catch (System.Exception error) { cadContext = null; objects.ItemsSource = null; drawing.Text = "AutoCAD занят или недоступен: " + error.Message; }
        finally { readingContext = false; }
    }
    private async Task FocusObject()
    {
        if (running is not null || objects.SelectedItem is not CadCard card || card.Erased) return;
        await RefreshCad();
        if (cadContext is not { } context || context.SessionId != card.Session || context.DocumentId != card.Document) { activity.Text = "Этот объект относится к другому чертежу"; return; }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var reply = await PipeClient.CallAsync(Wire.BrokerPipe, new(Guid.NewGuid().ToString("N"), "cad_focus", card.Session, card.Document, context.Revision, Wire.Element(new { handle = card.FocusHandle })), timeout.Token);
            activity.Text = reply.Error is null ? "Объект выделен в AutoCAD" : reply.Error.Message;
        }
        catch (System.Exception error) { LogError(error); activity.Text = error.Message; }
    }
    private async Task RunAccount(bool diagnostic)
    {
        if (running is not null) return;
        if (provider.SelectedIndex != 0) { activity.Text = "Вход в Claude Code: выполните claude auth login в официальном CLI"; return; }
        running = new(); SetBusy(true);
        try
        {
            running.CancelAfter(TimeSpan.FromMinutes(diagnostic ? 1 : 5));
            var options = new ProviderOptions(executable.Text, host.Text, directory.Text);
            using var process = System.Diagnostics.Process.Start(ProviderProcess.StartInfo(options, diagnostic ? new[] { "login", "status" } : new[] { "login" }))!;
            using var cancel = running.Token.Register(() => ProviderProcess.Stop(process));
            var stderr = ProviderProcess.DrainErrors(process); var stdout = process.StandardOutput.ReadToEndAsync(running.Token);
            activity.Text = diagnostic ? "Проверка входа…" : "Завершите вход в открывшемся браузере";
            await process.WaitForExitAsync(running.Token); await stdout; string detail = await stderr;
            activity.Text = process.ExitCode == 0 ? "Codex: вход выполнен" : "Нужно войти в Codex: " + detail;
        }
        catch (OperationCanceledException) { activity.Text = "Вход или проверка остановлены"; }
        catch (System.Exception error) { LogError(error); activity.Text = "Подключение: " + error.Message; }
        finally { running.Dispose(); running = null; SetBusy(false); }
        await RefreshCad();
        if (!diagnostic) await RefreshModels();
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
        var defaultModel = catalog.FirstOrDefault(m => m.IsDefault) ?? catalog.FirstOrDefault();
        var choices = new List<Choice> { new("", "Автоматически" + (defaultModel is null ? "" : " — " + defaultModel.DisplayName)) };
        choices.AddRange(catalog.Select(m => new Choice(m.Id, m.DisplayName)));
        if (!string.IsNullOrEmpty(chosenModel) && !catalog.Any(m => m.Id == chosenModel)) choices.Add(new(chosenModel, chosenModel + " — недоступна"));
        model.ItemsSource = choices; model.SelectedValue = chosenModel ?? "";
        settingChoices = false; PopulateEfforts();
    }
    private void PopulateEfforts()
    {
        settingChoices = true;
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
        model.IsEnabled = reasoning.IsEnabled = refreshModels.IsEnabled = !busy && provider.SelectedIndex == 0;
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
        if (!File.Exists(host.Text) || !Directory.Exists(directory.Text)) { transcript.AppendText("\nУкажите существующие MCP host и рабочую папку.\n"); return; }
        running = new(); SetBusy(true);
        activity.Text = "Подключение…"; flushTimer.Start();
        try
        {
            var files = ChatAttachments.Capture(attachments);
            await BrokerBootstrap.EnsureAsync(host.Text, running.Token);
            await RefreshCad();
            var key = provider.SelectedIndex + "|" + executable.Text + "|" + host.Text + "|" + directory.Text;
            var selection = (chosenModel ?? "") + "|" + (chosenEffort ?? "");
            if (adapter is null || adapterKey != key || adapterSelection != selection)
            {
                var previousSession = adapterKey == key ? adapter?.SessionId : null;
                var options = new ProviderOptions(executable.Text, host.Text, directory.Text, chosenModel, chosenEffort);
                adapter = provider.SelectedIndex == 0 ? new CodexProvider(options) : new ClaudeProvider(options); adapterKey = key;
                adapterSelection = selection;
                adapter.SessionId = previousSession ?? (restored?.AdapterKey == key ? restored.SessionId : null);
                restored = null;
            }
            var prompt = string.IsNullOrWhiteSpace(input.Text) ? "Изучи приложенные файлы и скажи, чем можешь помочь." : input.Text;
            input.Clear(); transcript.AppendText("\nВы: " + prompt + (files.Count == 0 ? "" : " · вложения: " + string.Join(", ", files.Select(f => f.Name))) + "\nАссистент: ");
            if (cadContext is { } target) prompt += "\nPanel target (verify fresh with CAD tools): session_id=" + target.SessionId + ", document_id=" + target.DocumentId + ". Work only in this drawing; if it changed, report the change.";
            await foreach (var item in adapter.SendAsync(prompt, running.Token, files))
            {
                if (item.Kind == "text") streamedText.Append(item.Text);
                if (item.Kind == "model") { FlushText(); transcript.AppendText("[" + item.Text); activity.Text = "Модель: " + item.Text; }
                if (item.Kind == "effort") { FlushText(); transcript.AppendText(" · " + EffortLabel(item.Text) + "]\n"); activity.Text += ", " + EffortLabel(item.Text); }
                if (item.Kind == "status") activity.Text = "Работа: " + item.Text;
                if (item.Kind == "cad_result") { var cards = CadCards.Parse(item.Text); if (cards.Count != 0) objects.ItemsSource = cards; }
                if (item.Kind == "session") Persist();
            }
            FlushText(); transcript.AppendText("\n"); attachments.Clear(); RefreshAttachments(); activity.Text = "Готово";
        }
        catch (OperationCanceledException) { FlushText(); transcript.AppendText("\nОстановлено. Уже принятые CAD-запросы могут завершиться. Для AutoLISP проверьте состояние; Esc в AutoCAD прерывает выполняемый скрипт.\n"); activity.Text = "Остановлено"; }
        catch (System.Exception e) { LogError(e); FlushText(); transcript.AppendText("\nОшибка: " + e.Message + "\n"); activity.Text = "Ошибка; подробности в журнале"; }
        finally { flushTimer.Stop(); FlushText(); Persist(); running.Dispose(); running = null; SetBusy(false); }
    }
    private void FlushText()
    { if (streamedText.Length == 0) return; transcript.AppendText(streamedText.ToString()); streamedText.Clear(); transcript.ScrollToEnd(); }
    private ChatState State()
    {
        if (provider.SelectedIndex == 0) codexExecutable = executable.Text; else claudeExecutable = executable.Text;
        return new(provider.SelectedIndex, codexExecutable, claudeExecutable, host.Text, directory.Text, adapter?.SessionId ?? restored?.SessionId, transcript.Text, adapterKey ?? restored?.AdapterKey, chosenModel, chosenEffort);
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
        transcript.Text = state.Transcript; restored = state; adapter = null; adapterKey = null; attachments.Clear(); RefreshAttachments();
        chosenModel = state.CodexModel; chosenEffort = state.CodexReasoningEffort;
        ApplyModels(Array.Empty<CodexModel>());
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
}
