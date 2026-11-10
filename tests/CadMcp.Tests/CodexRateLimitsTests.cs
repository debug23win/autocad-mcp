using System.Text.Json;
using CadMcp.Providers;
namespace CadMcp.Tests;
public sealed class CodexRateLimitsTests
{
    [Fact]
    public void Shows_actual_remaining_percent_and_real_window_duration_without_inferred_access()
    {
        var data = JsonSerializer.SerializeToElement(new { rateLimits = new { primary = new { usedPercent = 15.5, windowDurationMins = 300, resetsAt = 1900000000L }, secondary = new { usedPercent = 100, windowDurationMins = 10080 } } });
        var limits = CodexRateLimits.Parse(data)!;
        Assert.Null(limits.OrdinaryUsageAllowed); Assert.Equal(84.5, limits.Primary!.RemainingPercent);
        Assert.Contains("5 ч: осталось 84,5%", limits.Display()); Assert.Contains("7 дн: осталось 0%", limits.Display()); Assert.Contains("сброс", limits.Display());
    }
    [Fact]
    public void Multi_bucket_codex_limits_take_precedence_and_backend_denial_is_not_overridden()
    {
        var data = JsonSerializer.SerializeToElement(new { ordinaryUsageAllowed = false, rateLimits = new { primary = new { usedPercent = 5 } }, rateLimitsByLimitId = new { codex = new { primary = new { usedPercent = 50 } } } });
        var limits = CodexRateLimits.Parse(data)!; Assert.Equal(50, limits.Primary!.RemainingPercent); Assert.Contains("доступ по подписке ограничен", limits.Display());
    }
    [Fact]
    public void Missing_and_invalid_quota_is_unavailable_instead_of_zero()
    {
        Assert.Null(CodexRateLimits.Parse(JsonSerializer.SerializeToElement(new { })));
        Assert.Null(CodexRateLimits.Parse(JsonSerializer.SerializeToElement(new { rateLimits = new { primary = new { usedPercent = -2 } } })));
    }
    [Fact]
    public async Task Provider_reads_real_account_rate_limit_protocol()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = await TestEnvironment.Collect(new CodexProvider(TestEnvironment.ProviderOptions).SendAsync("Read", timeout.Token));
        var limits = Assert.Single(events, e => e.Kind == "rate_limits").RateLimits!;
        Assert.Equal(85, limits.Primary!.RemainingPercent); Assert.Equal(70, limits.Secondary!.RemainingPercent);
    }
}
