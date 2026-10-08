using System.Diagnostics;
using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Providers;

public sealed record CodexModel(string Id, string DisplayName, string DefaultEffort, IReadOnlyList<string> Efforts, bool IsDefault);
public sealed record CodexSelection(CodexModel Model, string Effort);

public static class CodexCatalog
{
    public static IReadOnlyList<CodexModel> Parse(IEnumerable<JsonElement> entries)
    {
        var result = new List<CodexModel>();
        foreach (var entry in entries)
        {
            // A malformed entry is skipped; it must not hide the rest of the catalog.
            if (entry.ValueKind != JsonValueKind.Object || Flag(entry, "hidden")) continue;
            string? id = Text(entry, "model");
            if (string.IsNullOrWhiteSpace(id) || result.Any(m => m.Id == id)) continue;
            string name = Text(entry, "displayName") ?? id;
            var efforts = entry.TryGetProperty("supportedReasoningEfforts", out var supported) && supported.ValueKind == JsonValueKind.Array
                ? supported.EnumerateArray().Select(e => Text(e, "reasoningEffort")).OfType<string>().Where(e => !string.IsNullOrWhiteSpace(e)).Distinct().ToList()
                : new List<string>();
            string effort = Text(entry, "defaultReasoningEffort") ?? efforts.FirstOrDefault() ?? "medium";
            if (efforts.Count == 0) efforts.Add(effort);
            if (!efforts.Contains(effort)) effort = efforts[0];
            result.Add(new(id, name, effort, efforts, Flag(entry, "isDefault")));
        }
        if (result.Count == 0) throw new IOException("Codex не вернул доступных моделей. Проверьте вход в официальный Codex.");
        return result;
    }
    private static string? Text(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Flag(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    public static CodexSelection Select(IReadOnlyList<CodexModel> models, string? requestedModel, string? requestedEffort)
    {
        var model = string.IsNullOrEmpty(requestedModel)
            ? models.FirstOrDefault(m => m.IsDefault) ?? models.FirstOrDefault()
            : models.FirstOrDefault(m => m.Id == requestedModel);
        if (model is null) throw new IOException("Выбранная модель «" + requestedModel + "» недоступна. Обновите список моделей и выберите другую.");
        string effort = string.IsNullOrEmpty(requestedEffort) ? model.DefaultEffort : requestedEffort;
        if (!model.Efforts.Contains(effort)) throw new IOException("Модель «" + model.DisplayName + "» не поддерживает уровень «" + effort + "». Выберите другой уровень рассуждений.");
        return new(model, effort);
    }
    public static async Task<IReadOnlyList<CodexModel>> ReadAsync(ProviderOptions options, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        using var process = Process.Start(ProviderProcess.StartInfo(options, new CodexProvider(options).Arguments())) ?? throw new IOException("Не удалось запустить Codex");
        using var cancellation = token.Register(() => ProviderProcess.Stop(process));
        var stderr = ProviderProcess.DrainErrors(process);
        var lines = new ProviderLines(process.StandardOutput);
        var entries = new List<JsonElement>();
        var cursors = new HashSet<string>();
        async Task Send(object data)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(data).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }
        try
        {
            await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "cad_mcp", title = "CAD MCP", version = Wire.Version } } });
            while (await lines.ReadAsync(token) is { } json)
            {
                using (json)
                {
                    var reply = json.RootElement;
                    if (reply.ValueKind != JsonValueKind.Object) continue;
                    if (reply.TryGetProperty("error", out _)) throw new IOException("Codex: " + CodexProvider.ErrorMessage(reply));
                    if (!reply.TryGetProperty("id", out var id) || reply.TryGetProperty("method", out _) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int replyId)) continue;
                    if (replyId == 1)
                    {
                        await Send(new { method = "initialized", @params = new { } });
                        await Send(new { id = 4, method = "model/list", @params = new { limit = 100, includeHidden = false } });
                    }
                    else if (replyId == 4)
                    {
                        if (!reply.TryGetProperty("result", out var page) || !page.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                            throw new IOException("Codex вернул каталог моделей в неизвестном формате");
                        entries.AddRange(data.EnumerateArray().Select(e => e.Clone()));
                        if (entries.Count > 1000) throw new IOException("Каталог моделей Codex слишком велик");
                        if (page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(next.GetString()))
                        {
                            string cursor = next.GetString()!;
                            if (!cursors.Add(cursor)) throw new IOException("Codex повторил страницу каталога моделей");
                            await Send(new { id = 4, method = "model/list", @params = new { limit = 100, includeHidden = false, cursor } });
                        }
                        else return Parse(entries);
                    }
                }
            }
            throw new IOException("Codex завершился до получения моделей. " + await stderr + lines.Skipped);
        }
        finally { await ProviderProcess.FinishAsync(process, stderr); }
    }
}
