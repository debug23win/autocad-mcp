using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Providers;

/// <summary>Bounded SSE parser: arbitrary byte boundaries, CRLF, multiline data and UTF-8 are supported.</summary>
internal static class PerplexityStream
{
    internal static async IAsyncEnumerable<JsonElement> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        const int maximumEventChars = 32 * 1024 * 1024;
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        byte[] bytes = new byte[8192];
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var line = new StringBuilder();
        var data = new StringBuilder();
        bool first = true;
        while (true)
        {
            int length = await stream.ReadAsync(bytes, ct);
            int decoded;
            try { decoded = decoder.GetChars(bytes, 0, length, chars, 0, flush: length == 0); }
            catch (DecoderFallbackException) { throw new IOException("Perplexity returned invalid UTF-8"); }
            for (int index = 0; index < decoded; index++)
            {
                char value = chars[index];
                if (first) { first = false; if (value == '\uFEFF') continue; }
                if (value != '\n')
                {
                    line.Append(value);
                    if (line.Length + data.Length > maximumEventChars) throw new IOException("Perplexity SSE event exceeded its size limit");
                    continue;
                }
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                string current = line.ToString(); line.Clear();
                if (current.Length == 0)
                {
                    if (data.Length == 0) continue;
                    string body = data.ToString(); data.Clear();
                    if (body == "[DONE]") yield break;
                    yield return Parse(body);
                }
                else if (current.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    int offset = current.Length > 5 && current[5] == ' ' ? 6 : 5;
                    data.Append(current.AsSpan(offset));
                }
                // Comments, IDs, retry hints and event names carry no data and cannot complete a response.
            }
            if (length == 0)
            {
                // A truncated SSE frame is never trusted, even when it happens to contain parseable JSON.
                if (line.Length > 0 || data.Length > 0) throw new IOException("Perplexity ended in the middle of an SSE event");
                yield break;
            }
        }
    }

    private static JsonElement Parse(string value)
    {
        try { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
        catch (JsonException) { throw new IOException(UiText.T("Perplexity вернул некорректное событие потока.")); }
    }
}

internal sealed class PerplexitySources
{
    private sealed record Source(string Id, string Title, string Url);
    private readonly Dictionary<string, Source> sources = new(StringComparer.Ordinal);
    private readonly HashSet<string> ambiguous = new(StringComparer.Ordinal);

    internal void Add(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        { foreach (var item in value.EnumerateArray()) Add(item); return; }
        if (value.ValueKind != JsonValueKind.Object) return;
        string? type = ProviderJson.Text(value, "type");
        if (type is "search_results" or "response.reasoning.search_results")
        {
            foreach (string key in new[] { "results", "search_results" })
            {
                var results = ProviderJson.Child(value, key);
                if (results.ValueKind != JsonValueKind.Array) continue;
                foreach (var result in results.EnumerateArray()) AddSource(result);
            }
        }
        if (type == "url_citation" && ProviderJson.Text(value, "url") is { } citationUrl && !sources.Values.Any(source => source.Url == citationUrl))
            AddSource(Wire.Element(new { id = "link-" + (sources.Count + 1), url = citationUrl, title = ProviderJson.Text(value, "title") }));
        if (value.TryGetProperty("output", out var output)) Add(output);
        if (value.TryGetProperty("item", out var itemValue)) Add(itemValue);
        if (value.TryGetProperty("content", out var content)) Add(content);
        if (value.TryGetProperty("annotations", out var annotations)) Add(annotations);
    }

    private void AddSource(JsonElement result)
    {
        var idValue = ProviderJson.Child(result, "id");
        string? id = idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : idValue.ValueKind == JsonValueKind.Number ? idValue.GetRawText() : null;
        string? url = ProviderJson.Text(result, "url");
        if (id is null || id.Length > 80 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_') ||
            url is null || url.Length > 4096 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)) return;
        if (sources.Count >= 100 || ambiguous.Contains(id)) return;
        if (sources.TryGetValue(id, out var existing) && existing.Url != uri.AbsoluteUri)
        { sources.Remove(id); ambiguous.Add(id); return; }
        string title = ProviderJson.Text(result, "title") ?? uri.Host;
        title = title.ReplaceLineEndings(" ").Replace("[", "(", StringComparison.Ordinal).Replace("]", ")", StringComparison.Ordinal)
            .Replace("<", "", StringComparison.Ordinal).Replace(">", "", StringComparison.Ordinal).Replace("\\", "", StringComparison.Ordinal);
        if (title.Length > 200) title = title[..200];
        sources[id] = new(id, title, uri.AbsoluteUri.Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal));
    }

    internal string Markdown(int responseNumber)
    {
        if (sources.Count == 0) return "";
        var text = new StringBuilder("\n\n" + UiText.T("Источники Perplexity, ответ") + " " + responseNumber + ":\n\n");
        foreach (var source in sources.Values) text.Append("- [").Append(source.Id).Append("] [").Append(source.Title).Append("](<").Append(source.Url).Append(">)\n");
        // Do not emit Markdown reference definitions: IDs restart on the next HTTP response, while the UI
        // renders the whole multi-step answer as one Markdown message. Shared [1] definitions would cross-link sources.
        return text.ToString();
    }
}
