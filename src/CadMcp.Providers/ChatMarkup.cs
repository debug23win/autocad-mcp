using System.Net;
using System.Text;
using Markdig;

namespace CadMcp.Providers;

public sealed record ChatImage(string Name, string Path, int Width = 0, int Height = 0);
public sealed record ChatLine(string Role, string Text, string? Model = null, string? Effort = null,
    IReadOnlyList<ChatImage>? Images = null, IReadOnlyList<string>? Steps = null, string? ReasoningSummary = null);

public static class ChatMarkup
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().DisableHtml().Build();

    public static string MarkdownHtml(string text) => Markdown.ToHtml(text ?? "", Pipeline);

    public static string ConversationHtml(IReadOnlyList<ChatLine> messages, string assetDirectory)
    {
        var html = new StringBuilder();
        for (int index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            string role = message.Role is "user" or "assistant" or "progress" ? message.Role : "history";
            html.Append("<article class='message ").Append(role).Append("' data-index='").Append(index).Append("'>");
            html.Append("<header>").Append(role switch { "user" => "Вы", "assistant" => "Ассистент", "progress" => "Ход работы", _ => "История" });
            if (!string.IsNullOrWhiteSpace(message.Model)) html.Append(" <small>").Append(WebUtility.HtmlEncode(message.Model)).Append("</small>");
            if (!string.IsNullOrWhiteSpace(message.Effort)) html.Append(" <small>· ").Append(WebUtility.HtmlEncode(message.Effort)).Append("</small>");
            html.Append("</header><div class='content'>").Append(MarkdownHtml(message.Text)).Append("</div>");
            if (message.Images is { Count: > 0 })
            {
                html.Append("<div class='images'>");
                foreach (var file in message.Images)
                {
                    string label = WebUtility.HtmlEncode(file.Name);
                    string root = Path.GetFullPath(assetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    string? candidate = null;
                    try { candidate = Path.GetFullPath(file.Path); } catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
                    if (candidate is not null && candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                    {
                        string url = "https://cadmcp-assets.local/" + Uri.EscapeDataString(Path.GetFileName(candidate));
                        html.Append("<figure><img src='").Append(url).Append("' alt='").Append(label).Append("'><figcaption>").Append(label).Append("</figcaption></figure>");
                    }
                    else html.Append("<p class='missing-image'>Изображение недоступно: ").Append(label).Append("</p>");
                }
                html.Append("</div>");
            }
            if (!string.IsNullOrWhiteSpace(message.ReasoningSummary))
                html.Append("<section class='reasoning'><strong>Краткий ход рассуждений</strong>")
                    .Append(MarkdownHtml(message.ReasoningSummary)).Append("</section>");
            if (message.Steps is { Count: > 0 })
            {
                html.Append("<section class='steps'><strong>Действия</strong><ul>");
                foreach (var step in message.Steps) html.Append("<li>").Append(WebUtility.HtmlEncode(step)).Append("</li>");
                html.Append("</ul></section>");
            }
            html.Append("</article>");
        }
        return html.ToString();
    }

    public static string PlainTranscript(IReadOnlyList<ChatLine> messages)
    {
        var value = new StringBuilder();
        foreach (var message in messages)
        {
            value.AppendLine(message.Role switch { "user" => "Вы:", "assistant" => "Ассистент:", _ => "Ход работы:" });
            value.AppendLine(message.Text);
            if (message.Images is { Count: > 0 }) value.AppendLine("Вложения: " + string.Join(", ", message.Images.Select(x => x.Name)));
            if (!string.IsNullOrWhiteSpace(message.ReasoningSummary)) value.AppendLine("Краткий ход рассуждений: " + message.ReasoningSummary);
            if (message.Steps is { Count: > 0 }) foreach (var step in message.Steps) value.AppendLine("• " + step);
            value.AppendLine();
        }
        return value.ToString();
    }
}
