using System.Diagnostics;
using CadMcp.Providers;
namespace CadMcp.Tests;
public sealed class ProviderWatchdogTests
{
    [Theory]
    [InlineData("SILENT", false)]
    [InlineData("SILENT_ACTIVE", true)]
    [InlineData("SILENT_ACTIVE_NO_INIT", true)]
    public async Task Claude_bounds_startup_and_active_silence_without_replaying(string prompt, bool active)
    {
        var provider = new ClaudeProvider(TestEnvironment.ProviderOptions) {
            StartupTimeout = active ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(350),
            EventSilenceTimeout = TimeSpan.FromMilliseconds(350)
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(provider.SendAsync(prompt, timeout.Token)));
        Assert.Contains("Claude Code", error.Message); Assert.Contains("повтора", error.Message);
        Assert.Contains(active ? "не передавал события" : "не начал задачу", error.Message);
    }

    [Fact]
    public async Task Claude_hidden_events_refresh_liveness_without_exposing_service_text()
    {
        var provider = new ClaudeProvider(TestEnvironment.ProviderOptions) { StartupTimeout = TimeSpan.FromSeconds(5), EventSilenceTimeout = TimeSpan.FromSeconds(1) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = await TestEnvironment.Collect(provider.SendAsync("HEARTBEAT", timeout.Token));
        Assert.True(events.Count(e => e.Kind == "heartbeat") >= 2);
        Assert.Equal("Готово", Assert.Single(events, e => e.Kind == "text").Text);
        Assert.DoesNotContain(events, e => e.Text.Contains("hidden service output", StringComparison.Ordinal));
    }
    [Fact]
    public async Task Silent_startup_is_bounded_and_stops_without_replaying_the_user_request()
    {
        var provider = new CodexProvider(TestEnvironment.ProviderOptions) { StartupTimeout = TimeSpan.FromMilliseconds(300) };
        using var outer = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var clock = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(provider.SendAsync("SILENT", outer.Token)));
        Assert.Contains("не начал задачу", error.Message); Assert.Contains("автоматического повтора", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task Started_but_silent_turn_is_bounded_separately_from_startup()
    {
        var provider = new CodexProvider(TestEnvironment.ProviderOptions) { StartupTimeout = TimeSpan.FromSeconds(5), EventSilenceTimeout = TimeSpan.FromMilliseconds(300) };
        using var outer = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<IOException>(() => TestEnvironment.Collect(provider.SendAsync("SILENT_ACTIVE", outer.Token)));
        Assert.Contains("не передавал события", error.Message); Assert.Contains("AutoCAD", error.Message);
    }
    [Fact]
    public async Task Hidden_protocol_events_keep_the_turn_alive_without_leaking_service_output()
    {
        var provider = new CodexProvider(TestEnvironment.ProviderOptions) { StartupTimeout = TimeSpan.FromSeconds(5), EventSilenceTimeout = TimeSpan.FromSeconds(1) };
        using var outer = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = await TestEnvironment.Collect(provider.SendAsync("HEARTBEAT", outer.Token));
        Assert.True(events.Count(e => e.Kind == "heartbeat") >= 2);
        Assert.Equal("Готово", Assert.Single(events, e => e.Kind == "text").Text);
        Assert.DoesNotContain(events, e => e.Text.Contains("hidden service output", StringComparison.Ordinal));
    }
}
