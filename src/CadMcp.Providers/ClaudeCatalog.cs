using System.Diagnostics;
using System.Text.Json;

namespace CadMcp.Providers;

/// <summary>
/// A model the installed Claude Code offers to this account: an alias such as <c>opus</c> that the CLI resolves to
/// its current model, or <c>default</c>. Efforts are the levels the CLI accepts for it; none when it takes no effort.
/// </summary>
public sealed record ClaudeModel(string Id, string DisplayName, string? Description, string? ResolvedModel, IReadOnlyList<string> Efforts, bool IsDefault)
{
    /// <summary>"Opus 5.5 · Best for everyday, complex tasks": the description names the current version.</summary>
    public string Label => string.IsNullOrWhiteSpace(Description) ? DisplayName : Description!;
}

public sealed record ClaudeSelection(string? Model, string? Effort);

/// <summary>
/// Models and effort levels as the installed Claude Code reports them. The CLI answers the stream-json
/// <c>initialize</c> control request with the models of the signed-in account; no model is called.
/// </summary>
public static class ClaudeCatalog
{
    /// <summary>The levels of <c>claude --effort</c>; offered when the model list cannot be read.</summary>
    public static readonly IReadOnlyList<string> AllEfforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>Aliases offered when the model list cannot be read; the CLI resolves each to its current model.</summary>
    public static readonly IReadOnlyList<ClaudeModel> Fallback =
    [
        new("sonnet", "Claude Sonnet", null, null, AllEfforts, false),
        new("opus", "Claude Opus", null, null, AllEfforts, false),
        new("fable", "Claude Fable", null, null, AllEfforts, false),
        new("haiku", "Claude Haiku", null, null, AllEfforts, false)
    ];

    public static IReadOnlyList<ClaudeModel> Parse(JsonElement models)
    {
        var result = new List<ClaudeModel>();
        if (models.ValueKind != JsonValueKind.Array) return result;
        foreach (var entry in models.EnumerateArray())
        {
            if (ProviderJson.Text(entry, "value") is not { Length: > 0 } id || result.Any(m => m.Id == id)) continue;
            // A model that takes no effort, or a CLI that does not say, gets no level: --effort is then not passed.
            var efforts = Flag(entry, "supportsEffort") && entry.TryGetProperty("supportedEffortLevels", out var levels) && levels.ValueKind == JsonValueKind.Array
                ? levels.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!).Where(l => l.Length > 0).Distinct().ToArray()
                : [];
            result.Add(new(id, ProviderJson.Text(entry, "displayName") ?? id, ProviderJson.Text(entry, "description"), ProviderJson.Text(entry, "resolvedModel"), efforts, id == "default"));
        }
        return result;
    }

    private static bool Flag(JsonElement entry, string key) => entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Checks a saved choice against the list: a model the account no longer offers or a level the model does not
    /// accept is an error the user can fix, not something to drop silently. An empty list checks nothing.
    /// </summary>
    public static ClaudeSelection Select(IReadOnlyList<ClaudeModel> models, string? model, string? effort)
    {
        model = string.IsNullOrEmpty(model) ? null : model;
        effort = string.IsNullOrEmpty(effort) ? null : effort;
        if (models.Count == 0) return new(model, effort);
        var selected = model is null ? models.FirstOrDefault(m => m.IsDefault) : models.FirstOrDefault(m => m.Id == model);
        if (selected is null && model is not null)
            throw new IOException("Модель «" + model + "» недоступна в этой версии Claude Code или для этого аккаунта. Обновите список моделей и выберите другую.");
        if (effort is not null && selected is not null && !selected.Efforts.Contains(effort))
            throw new IOException(selected.Efforts.Count == 0
                ? "Модель «" + selected.DisplayName + "» не принимает глубину рассуждений. Выберите «По умолчанию»."
                : "Модель «" + selected.DisplayName + "» не поддерживает уровень «" + effort + "». Выберите другой уровень рассуждений.");
        return new(model, effort);
    }

    public static async Task<IReadOnlyList<ClaudeModel>> ReadAsync(ProviderOptions options, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        // No MCP server and no saved conversation: only the account and its models are needed.
        string[] arguments = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--strict-mcp-config", "--no-session-persistence"];
        using var process = Process.Start(ProviderProcess.StartInfo(options, arguments)) ?? throw new IOException("Не удалось запустить Claude Code");
        using var cancellation = token.Register(() => ProviderProcess.Stop(process));
        var stderr = ProviderProcess.DrainErrors(process);
        var lines = new ProviderLines(process.StandardOutput);
        const string requestId = "cad_mcp_models";
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { type = "control_request", request_id = requestId, request = new { subtype = "initialize" } }).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            while (await lines.ReadAsync(token) is { } json)
            {
                using (json)
                {
                    var line = json.RootElement;
                    var response = ProviderJson.Child(line, "response");
                    if (ProviderJson.Text(line, "type") != "control_response" || ProviderJson.Text(response, "request_id") != requestId) continue;
                    if (ProviderJson.Text(response, "subtype") != "success")
                        throw new IOException("Claude Code не выдал список моделей: " + (ProviderJson.Text(response, "error") ?? response.ToString()));
                    return Parse(ProviderJson.Child(ProviderJson.Child(response, "response"), "models"));
                }
            }
            throw new IOException("Claude Code завершился до выдачи списка моделей. " + await stderr + lines.Skipped);
        }
        finally { await ProviderProcess.FinishAsync(process, stderr); }
    }
}
