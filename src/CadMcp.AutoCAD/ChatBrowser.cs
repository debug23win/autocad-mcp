using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CadMcp.Providers;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TextBox = System.Windows.Controls.TextBox;

namespace CadMcp.AutoCAD;

internal sealed class ChatBrowser : Grid
{
    private readonly WebView2 browser = new();
    private readonly TextBox fallback = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
    private IReadOnlyList<ChatLine> messages = Array.Empty<ChatLine>();
    private bool dark, ready;
    private int selected = -1;
    public string AssetRoot { get; }
    private readonly string userData;
    public event Action<int>? Selected;
    public event Action<string>? OpenLink;
    public ChatBrowser(string? dataRoot = null)
    {
        dataRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CadMcp");
        AssetRoot = Path.Combine(dataRoot, "chat", "assets");
        userData = Path.Combine(dataRoot, "WebView2");
        Children.Add(browser);
        Children.Add(fallback);
        Loaded += async (_, _) => await Initialize();
    }
    private async Task Initialize()
    {
        if (ready || fallback.Visibility == Visibility.Visible) return;
        try
        {
            var resources = Path.Combine(Path.GetDirectoryName(typeof(ChatBrowser).Assembly.Location)!, "Resources", "Chat");
            if (!File.Exists(Path.Combine(resources, "chat.html"))) throw new FileNotFoundException("HTML-ресурсы чата не установлены");
            Directory.CreateDirectory(AssetRoot);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await browser.EnsureCoreWebView2Async(environment);
            browser.CoreWebView2.SetVirtualHostNameToFolderMapping("cadmcp.local", resources, CoreWebView2HostResourceAccessKind.DenyCors);
            browser.CoreWebView2.SetVirtualHostNameToFolderMapping("cadmcp-assets.local", AssetRoot, CoreWebView2HostResourceAccessKind.Allow);
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            browser.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                try
                {
                    using var json = JsonDocument.Parse(args.WebMessageAsJson);
                    var root = json.RootElement;
                    if (root.GetProperty("type").GetString() == "select") Selected?.Invoke(root.GetProperty("index").GetInt32());
                    else if (root.GetProperty("type").GetString() == "link") OpenLink?.Invoke(root.GetProperty("url").GetString() ?? "");
                }
                catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
            };
            browser.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!args.Uri.StartsWith("https://cadmcp.local/", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
            };
            browser.CoreWebView2.NavigationCompleted += (_, _) => { ready = true; Render(); };
            browser.Source = new Uri("https://cadmcp.local/chat.html");
        }
        catch (Exception error)
        {
            browser.Visibility = Visibility.Collapsed;
            fallback.Visibility = Visibility.Visible;
            fallback.Text = "HTML-представление недоступно: " + error.Message + Environment.NewLine + ChatMarkup.PlainTranscript(messages);
        }
    }
    public ChatImage SaveImage(ChatAttachment file)
    {
        Directory.CreateDirectory(AssetRoot);
        string extension = Path.GetExtension(file.Name).ToLowerInvariant();
        string destination = Path.Combine(AssetRoot, Guid.NewGuid().ToString("N") + extension);
        File.Copy(file.Path, destination);
        try
        {
            using var stream = File.OpenRead(destination);
            var image = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            return new ChatImage(file.Name, destination, image.PixelWidth, image.PixelHeight);
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or IOException)
        { return new ChatImage(file.Name, destination); }
    }
    public ChatImage SavePreview(string base64, int width, int height)
    {
        if (base64.Length > 6 * 1024 * 1024) throw new InvalidDataException("Предпросмотр слишком велик");
        byte[] bytes = Convert.FromBase64String(base64);
        if (bytes.Length > 4 * 1024 * 1024 || bytes.Length < 8 ||
            !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("AutoCAD вернул некорректное изображение");
        Directory.CreateDirectory(AssetRoot);
        string destination = Path.Combine(AssetRoot, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(destination, bytes);
        return new ChatImage("Текущий вид чертежа после работы", destination, width, height);
    }
    public void Update(IReadOnlyList<ChatLine> lines, bool isDark, int selectedIndex)
    {
        messages = lines.ToArray(); dark = isDark; selected = selectedIndex;
        fallback.Text = ChatMarkup.PlainTranscript(messages);
        browser.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(37, 40, 45) : System.Drawing.Color.FromArgb(245, 246, 248);
        if (ready) Render();
    }
    private void Render()
    {
        if (!ready) return;
        try
        {
            var json = JsonSerializer.Serialize(new { html = ChatMarkup.ConversationHtml(messages, AssetRoot), dark, selected });
            browser.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception error) { Debug.WriteLine("CAD MCP chat render: " + error); }
    }
}
