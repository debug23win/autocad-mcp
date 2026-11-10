using System.Globalization;
using System.Text.Json;
namespace CadMcp.Providers;

public sealed record CodexLimitWindow(double RemainingPercent, int? DurationMinutes, DateTimeOffset? ResetsAt);
public sealed record CodexRateLimits(CodexLimitWindow? Primary, CodexLimitWindow? Secondary, bool? OrdinaryUsageAllowed, DateTimeOffset ObservedAt)
{
    public static CodexRateLimits? Parse(JsonElement data)
    {
        var snapshot = ProviderJson.Child(data, "rateLimits");
        if (ProviderJson.Child(ProviderJson.Child(data, "rateLimitsByLimitId"), "codex") is { ValueKind: JsonValueKind.Object } codex) snapshot = codex;
        CodexLimitWindow? Window(string key)
        {
            var window = ProviderJson.Child(snapshot, key);
            if (ProviderJson.Child(window, "usedPercent") is not { ValueKind: JsonValueKind.Number } percent || !percent.TryGetDouble(out double used) || !double.IsFinite(used) || used < 0 || used > 100) return null;
            int? minutes = ProviderJson.Child(window, "windowDurationMins") is { ValueKind: JsonValueKind.Number } duration && duration.TryGetInt32(out int m) && m > 0 ? m : null;
            DateTimeOffset? reset = null;
            if (AnswerMetrics.Count(window, "resetsAt") is { } unix) try { reset = DateTimeOffset.FromUnixTimeSeconds(unix); } catch (ArgumentOutOfRangeException) { }
            return new(100 - used, minutes, reset);
        }
        var primary = Window("primary"); var secondary = Window("secondary");
        bool? allowed = ProviderJson.Child(data, "ordinaryUsageAllowed").ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        return primary is null && secondary is null && allowed is null ? null : new(primary, secondary, allowed, DateTimeOffset.UtcNow);
    }
    public string Display()
    {
        string Format(CodexLimitWindow window, string fallback)
        {
            string label = window.DurationMinutes is { } m ? m % 1440 == 0 ? m / 1440 + " дн" : m % 60 == 0 ? m / 60 + " ч" : m + " мин" : fallback;
            return label + ": осталось " + window.RemainingPercent.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + "%" +
                (window.ResetsAt is { } reset ? " (сброс " + reset.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture) + ")" : "");
        }
        return "Лимиты Codex: " + string.Join(" · ", new[] { Primary is { } p ? Format(p, "основной") : null, Secondary is { } s ? Format(s, "дополнительный") : null,
            OrdinaryUsageAllowed == false ? "доступ по подписке ограничен" : null }.OfType<string>());
    }
}
