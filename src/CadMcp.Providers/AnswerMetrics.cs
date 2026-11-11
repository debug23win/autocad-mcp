using CadMcp.Core;
using System.Globalization;
using System.Text.Json;

namespace CadMcp.Providers;

public sealed record ProviderTokenUsage(long Total, long Input, long Output, string Scope);

public static class AnswerMetrics
{
    public static string Footer(TimeSpan elapsed, ProviderTokenUsage? usage, bool completed = true)
    {
        string tokens = usage is null ? UiText.T("нет данных") :
            UiText.Format($"{usage.Total.ToString("N0", CultureInfo.GetCultureInfo(CadMcp.Core.UiText.Language == "en" ? "en-US" : "ru-RU"))} (вход {usage.Input}, выход {usage.Output}; {usage.Scope}{(completed ? "" : UiText.T("; неполные данные"))})");
        return UiText.Format($"\n\n---\nВремя задачи: {elapsed.TotalSeconds.ToString("0.0", CultureInfo.GetCultureInfo(CadMcp.Core.UiText.Language == "en" ? "en-US" : "ru-RU"))} с · Токены: {tokens}");
    }

    internal static long? Count(JsonElement e, string key) =>
        ProviderJson.Child(e, key) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out long value) && value >= 0 ? value : null;

    internal static ProviderTokenUsage? Claude(JsonElement result)
    {
        var u = ProviderJson.Child(result, "usage");
        if (Count(u, "input_tokens") is not { } input || Count(u, "output_tokens") is not { } output) return null;
        // Claude's cache counters are separate from input_tokens. Optional fields may be absent, never malformed.
        long? Optional(string key) => ProviderJson.Child(u, key).ValueKind == JsonValueKind.Undefined ? 0 : Count(u, key);
        if (Optional("cache_read_input_tokens") is not { } read || Optional("cache_creation_input_tokens") is not { } write) return null;
        try { input = checked(input + read + write); return new(checked(input + output), input, output, UiText.T("последний результат Claude")); }
        catch (OverflowException) { return null; }
    }
}

/// <summary>Codex total counters belong to the thread; replay on resume supplies the baseline, not new spending.</summary>
internal sealed class CodexUsageTracker(bool resumed)
{
    private ProviderTokenUsage? baseline = resumed ? null : new(0, 0, 0, "");
    private ProviderTokenUsage? current;
    public ProviderTokenUsage? Observe(JsonElement parameters, string? threadId, string? activeTurn)
    {
        if (threadId is null || ProviderJson.Text(parameters, "threadId") != threadId) return null;
        var total = ProviderJson.Child(ProviderJson.Child(parameters, "tokenUsage"), "total");
        if (AnswerMetrics.Count(total, "totalTokens") is not { } all || AnswerMetrics.Count(total, "inputTokens") is not { } input ||
            AnswerMetrics.Count(total, "outputTokens") is not { } output) return null;
        var value = new ProviderTokenUsage(all, input, output, UiText.T("Codex, основной агент"));
        if (ProviderJson.Text(parameters, "turnId") is not { } turn) return null;
        if (activeTurn is null || turn != activeTurn)
        {
            if (current is null || baseline is null) baseline = value;
        }
        else current = value;
        if (current is null || baseline is null || current.Total < baseline.Total || current.Input < baseline.Input || current.Output < baseline.Output) return null;
        return new(current.Total - baseline.Total, current.Input - baseline.Input, current.Output - baseline.Output, current.Scope);
    }
}
