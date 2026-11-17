using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Providers;

public sealed record PerplexityModel(string Id, string DisplayName);

/// <summary>The Agent API catalog. No subscription CLI, credentials or generated requests are used to list models.</summary>
public static class PerplexityCatalog
{
    public const string DefaultModel = "openai/gpt-6.1-sol";
    // These are reasoning.effort values, NOT Perplexity's tool-enabling top-level effort presets.
    public static IReadOnlyList<string> Efforts { get; } = Array.AsReadOnly(new[] { "low", "medium", "high", "xhigh" });

    public static Task<IReadOnlyList<PerplexityModel>> LoadAsync(string apiKey, CancellationToken ct) =>
        LoadAsync(apiKey, PerplexityHttp.Client, ct);

    internal static async Task<IReadOnlyList<PerplexityModel>> LoadAsync(string apiKey, HttpClient client, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = PerplexityHttp.Request(HttpMethod.Get, "models", apiKey);
        using var response = await PerplexityHttp.SendAsync(client, request, timeout.Token, ct);
        using var json = await PerplexityHttp.ReadJsonAsync(response, timeout.Token, 2 * 1024 * 1024);
        var data = ProviderJson.Child(json.RootElement, "data");
        if (data.ValueKind != JsonValueKind.Array) throw new IOException(UiText.T("Perplexity вернул некорректный список моделей."));
        return data.EnumerateArray().Select(item => ProviderJson.Text(item, "id"))
            .OfType<string>().Where(ValidModel).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(id => new PerplexityModel(id, id)).ToArray();
    }

    public static void ValidateSelection(string? model, string? effort)
    {
        if (!string.IsNullOrEmpty(model) && !ValidModel(model)) throw new ArgumentException(UiText.T("Некорректный идентификатор модели Perplexity."), nameof(model));
        if (!string.IsNullOrEmpty(effort) && !Efforts.Contains(effort, StringComparer.Ordinal))
            throw new ArgumentException(UiText.T("Неподдерживаемая глубина рассуждений Perplexity."), nameof(effort));
    }

    private static bool ValidModel(string value) => value.Length is > 0 and <= 200 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or ':');
}

internal static class PerplexityHttp
{
    internal static readonly Uri Endpoint = new("https://api.perplexity.ai/v1/");
    internal static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static HttpRequestMessage Request(HttpMethod method, string resource, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException(UiText.T("Укажите API-ключ Perplexity в настройках или PERPLEXITY_API_KEY."));
        if (apiKey.Length > 4096 || apiKey.Any(c => c <= ' ' || c > '~')) throw new InvalidOperationException(UiText.T("Некорректный API-ключ Perplexity."));
        var request = new HttpRequestMessage(method, new Uri(Endpoint, resource));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken deadline, CancellationToken userCancellation)
    {
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline); }
        catch (OperationCanceledException) when (!userCancellation.IsCancellationRequested)
        { throw new IOException(UiText.T("Perplexity не ответил за отведённое время. Автоматического повтора нет.")); }
        catch (HttpRequestException)
        { throw new IOException(UiText.T("Не удалось подключиться к Perplexity. Проверьте соединение. Автоматического повтора нет.")); }
        if (response.IsSuccessStatusCode) return response;
        var code = response.StatusCode;
        response.Dispose();
        // The API body may echo submitted data or a credential. It is deliberately never included in logs/UI.
        string message = code switch
        {
            HttpStatusCode.Unauthorized => UiText.T("API-ключ Perplexity не принят (401). Проверьте ключ."),
            HttpStatusCode.Forbidden => UiText.T("Perplexity отказал в доступе (403). Проверьте права ключа и доступ к модели."),
            HttpStatusCode.PaymentRequired => UiText.T("Perplexity требует пополнить баланс API (402)."),
            HttpStatusCode.TooManyRequests => UiText.T("Достигнут лимит Perplexity API (429). Повторите позже; автоматического повтора нет."),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => UiText.T("Perplexity отклонил параметры запроса. Проверьте модель и глубину рассуждений; при большом диалоге начните новый."),
            _ => UiText.T("Ошибка Perplexity API. Автоматического повтора нет.")
        };
        throw new IOException(message + " HTTP " + (int)code + ".");
    }

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct, int maximumBytes = 32 * 1024 * 1024)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        byte[] bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + count > maximumBytes) throw new IOException(UiText.T("Ответ Perplexity превысил допустимый размер."));
            buffer.Write(bytes, 0, count);
        }
        try { return JsonDocument.Parse(buffer.ToArray()); }
        catch (JsonException) { throw new IOException(UiText.T("Perplexity вернул некорректный JSON.")); }
    }
}
