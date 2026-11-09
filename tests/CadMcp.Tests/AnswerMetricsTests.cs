using System.Text.Json;
using CadMcp.Providers;

namespace CadMcp.Tests;

public sealed class AnswerMetricsTests
{
    private static JsonElement Usage(long total, long input, long output, string turn = "new", string thread = "thread") =>
        JsonSerializer.SerializeToElement(new { threadId = thread, turnId = turn, tokenUsage = new { total = new { totalTokens = total, inputTokens = input, outputTokens = output } } });

    [Fact]
    public void Fresh_codex_snapshots_are_not_added_and_helpers_are_excluded()
    {
        var tracker = new CodexUsageTracker(false);
        Assert.Equal(100, tracker.Observe(Usage(100, 80, 20), "thread", "new")!.Total);
        Assert.Equal(100, tracker.Observe(Usage(100, 80, 20), "thread", "new")!.Total);
        Assert.Null(tracker.Observe(Usage(900, 800, 100, thread: "helper"), "thread", "new"));
        Assert.Equal(120, tracker.Observe(Usage(120, 90, 30), "thread", "new")!.Total);
    }

    [Fact]
    public void Resume_requires_baseline_and_subtracts_prior_history()
    {
        var tracker = new CodexUsageTracker(true);
        Assert.Null(tracker.Observe(Usage(120, 90, 30), "thread", "new"));
        Assert.Equal(new ProviderTokenUsage(20, 10, 10, "Codex, основной агент"), tracker.Observe(Usage(100, 80, 20, "old"), "thread", "new"));
        Assert.Equal(30, tracker.Observe(Usage(130, 95, 35), "thread", "new")!.Total);
        Assert.Equal(30, tracker.Observe(Usage(110, 85, 25, "old"), "thread", "new")!.Total);
        Assert.Null(tracker.Observe(Usage(90, 70, 20), "thread", "new"));
    }

    [Fact]
    public void Replay_before_turn_activation_is_a_baseline()
    {
        var tracker = new CodexUsageTracker(true);
        Assert.Null(tracker.Observe(Usage(100, 80, 20, "old"), "thread", null));
        Assert.Equal(20, tracker.Observe(Usage(120, 90, 30), "thread", "new")!.Total);
    }

    [Fact]
    public void Claude_cache_tokens_are_included_once_and_bad_data_is_not_zero()
    {
        var result = JsonSerializer.SerializeToElement(new { usage = new { input_tokens = 10, output_tokens = 5, cache_read_input_tokens = 100, cache_creation_input_tokens = 20 } });
        Assert.Equal(new ProviderTokenUsage(135, 130, 5, "последний результат Claude"), AnswerMetrics.Claude(result));
        Assert.Null(AnswerMetrics.Claude(JsonSerializer.SerializeToElement(new { usage = new { input_tokens = -1, output_tokens = 5 } })));
        Assert.Null(AnswerMetrics.Claude(JsonSerializer.SerializeToElement(new { usage = new { input_tokens = long.MaxValue, output_tokens = 5 } })));
        Assert.Null(AnswerMetrics.Claude(JsonSerializer.SerializeToElement(new { })));
    }

    [Fact]
    public void Footer_distinguishes_missing_usage_zero_and_partial_counts()
    {
        Assert.Contains("1,5 с · Токены: нет данных", AnswerMetrics.Footer(TimeSpan.FromSeconds(1.5), null));
        Assert.Contains("Токены: 0", AnswerMetrics.Footer(TimeSpan.Zero, new(0, 0, 0, "модели не вызывались")));
        Assert.Contains("неполные данные", AnswerMetrics.Footer(TimeSpan.Zero, new(10, 8, 2, "Codex"), false));
    }

    [Theory]
    [InlineData("Codex")]
    [InlineData("Claude")]
    public async Task Provider_emits_real_protocol_usage(string name)
    {
        IChatProvider provider = name == "Codex" ? new CodexProvider(TestEnvironment.ProviderOptions) : new ClaudeProvider(TestEnvironment.ProviderOptions);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = await TestEnvironment.Collect(provider.SendAsync("USAGE", timeout.Token));
        var usage = Assert.Single(events, e => e.Kind == "usage").Usage;
        Assert.NotNull(usage);
        Assert.Equal(name == "Codex" ? 100 : 135, usage.Total);
        Assert.Contains(events, e => e.Kind == "completed");
    }
}
