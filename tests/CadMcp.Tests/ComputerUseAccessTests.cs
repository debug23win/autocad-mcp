using System.Text.Json;
using CadMcp.Core;
using CadMcp.Providers;

namespace CadMcp.Tests;

public sealed class ComputerUseAccessTests
{
    internal static ComputerUseTarget Target => new(Path.Combine(Path.GetTempPath(), "CAD with spaces", "acad.exe"), "AutoCAD 2027",
        "CN=\"Autodesk, Inc.\", O=Autodesk", "AutoCAD", "acad.exe", new string('A', 64));
    [Fact]
    public void Access_defaults_off_and_changes_to_binary_or_publisher_require_a_new_grant()
    {
        using var folder = new TempFolder(); var store = new ComputerUseAccessStore(folder.File("access.json"));
        Assert.Null(store.Permission(Target)); store.Set(Target, true); Assert.True(store.Permission(Target)!.Allowed);
        Assert.Null(store.Permission(Target with { Sha256 = new string('B', 64) }));
        Assert.Null(store.Permission(Target with { PublisherName = "CN=Someone Else" }));
        store.RevokeAll(); Assert.False(store.Permission(Target)!.Allowed);
        File.WriteAllText(folder.File("access.json"), "{damaged"); Assert.Empty(store.Read());
        Assert.Throws<InvalidDataException>(() => store.Set(Target with { PublisherName = "*" }, true));
    }
    [Fact]
    public void Only_one_exact_executable_identity_is_granted_and_helpers_receive_denial()
    {
        var value = Wire.Element(ComputerUseConfiguration.Value(Target));
        Assert.Equal("deny", value.Text("default_app_access"));
        var exes = value.GetProperty("windows").GetProperty("exes"); Assert.Single(exes.EnumerateArray());
        Assert.Equal(Target.PublisherName, exes[0].Text("publisher_name")); Assert.Equal("acad.exe", exes[0].Text("binary_name"));
        Assert.True(ComputerUseConfiguration.Matches(value, Target)); Assert.False(ComputerUseConfiguration.Matches(value, null));
        Assert.True(ComputerUseConfiguration.Matches(Wire.Element(ComputerUseConfiguration.Value(null)), null));
        Assert.Contains(JsonSerializer.Serialize(Target.PublisherName), string.Join(" ", ComputerUseConfiguration.Arguments(Target)));
        Assert.Contains("exes = []", ComputerUseConfiguration.DeniedReviewerToml); Assert.DoesNotContain("access=\"allow\"", ComputerUseConfiguration.DeniedReviewerToml);
        var global = Wire.Element(new { windows = new { aumids = new Dictionary<string, string> { ["Existing.Package!App"] = "allow" } } });
        var denied = ComputerUseConfiguration.DeniedAumids(global); Assert.Equal("deny", denied["Existing.Package!App"]);
        Assert.Contains("\"Existing.Package!App\"=\"deny\"", ComputerUseConfiguration.ReviewerToml(denied));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Codex_applies_app_access_to_new_and_resumed_threads_without_granting_global_apps(bool resume)
    {
        var options = TestEnvironment.ProviderOptions with { ComputerUseTarget = Target, McpArguments = ["global-cua-app"] };
        var provider = new CodexProvider(options) { SessionId = resume ? "existing-thread" : null };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var events = await TestEnvironment.Collect(provider.SendAsync("Hello", timeout.Token));
        Assert.Contains(events, e => e.Kind == "computer_use_status" && e.Text.Contains(Target.DisplayName));
        Assert.Contains(events, e => e.Kind == "completed");
    }
    [Fact]
    public async Task Unconfirmed_configuration_stops_before_a_model_turn()
    {
        var provider = new CodexProvider(TestEnvironment.ProviderOptions with { ComputerUseTarget = Target, McpArguments = ["cua-policy-mismatch"] });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var seen = new List<ChatEvent>();
        var error = await Assert.ThrowsAsync<IOException>(async () => { await foreach (var item in provider.SendAsync("Hello", timeout.Token)) seen.Add(item); });
        Assert.Contains("Computer Use", error.Message); Assert.DoesNotContain(seen, e => e.Kind is "input_ready" or "text" or "completed");
    }
    [Fact]
    public async Task Revocation_interrupts_an_active_provider_without_cancelling_the_callers_token()
    {
        int allowed = 1;
        var provider = new CodexProvider(TestEnvironment.ProviderOptions with { ComputerUseTarget = Target, ComputerUseAccessStillValid = () => Volatile.Read(ref allowed) == 1 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in provider.SendAsync("STEER", timeout.Token))
                if (item.Kind == "input_ready") Interlocked.Exchange(ref allowed, 0);
        });
        Assert.False(timeout.IsCancellationRequested);
    }
}
