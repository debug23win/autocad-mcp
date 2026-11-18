using CadMcp.Providers;

namespace CadMcp.Tests;

public sealed class ApiCatalogAuthenticationTests
{
    private const string Key = "sk-api-catalog-fixture-quote\"slash\\-end";

    [Fact]
    public async Task Codex_catalog_verifies_routing_then_logs_in_before_listing_models()
    {
        using var folder = new TempFolder();
        var models = await CodexCatalog.ReadAsync(Options(folder.Path), default);

        Assert.Equal(new[] { "available-model", "alternative-model" }, models.Select(model => model.Id));
        Assert.Equal(new[] { "initialize", "initialized", "config/read", "account/login/start", "model/list" }, Trace(folder.Path));
        Assert.DoesNotContain(Key, File.ReadAllText(folder.File("auth-protocol.txt")));
    }

    [Theory]
    [InlineData("auth-config-reject", false)]
    [InlineData("auth-login-reject", true)]
    [InlineData("auth-login-shape", true)]
    public async Task Codex_catalog_stops_at_a_failed_authentication_gate(string mode, bool keySent)
    {
        using var folder = new TempFolder();
        string directory = Directory.CreateDirectory(folder.File(mode)).FullName;
        var error = await Assert.ThrowsAsync<IOException>(() => CodexCatalog.ReadAsync(Options(directory), default));

        var trace = Trace(directory);
        Assert.Equal(keySent, trace.Contains("account/login/start"));
        Assert.DoesNotContain("model/list", trace);
        Assert.DoesNotContain("thread/start", trace);
        Assert.DoesNotContain(Key, error.ToString());
    }

    [Theory]
    [InlineData("auth-key-echo")]
    [InlineData("auth-raw-exit")]
    public async Task Codex_catalog_redacts_nested_errors_and_unstructured_process_output(string mode)
    {
        using var folder = new TempFolder();
        string directory = Directory.CreateDirectory(folder.File(mode)).FullName;
        var error = await Assert.ThrowsAsync<IOException>(() => CodexCatalog.ReadAsync(Options(directory), default));

        Assert.DoesNotContain(Key, error.ToString());
        Assert.DoesNotContain("sk-api-catalog-fixture", error.ToString());
        Assert.Contains("[REDACTED]", error.Message);
    }

    [Fact]
    public async Task Claude_catalog_checks_the_CLI_without_a_key_then_starts_the_selected_API_mode()
    {
        using var folder = new TempFolder();
        var models = await ClaudeCatalog.ReadAsync(Options(folder.Path), default);

        Assert.NotEmpty(models);
        Assert.Equal(new[] { "version", "claude-start" }, Trace(folder.Path));
        Assert.DoesNotContain(Key, File.ReadAllText(folder.File("auth-protocol.txt")));
    }

    [Fact]
    public async Task Claude_catalog_rejects_an_old_CLI_before_starting_with_the_key()
    {
        using var folder = new TempFolder();
        string directory = Directory.CreateDirectory(folder.File("auth-claude-old")).FullName;
        var error = await Assert.ThrowsAsync<IOException>(() => ClaudeCatalog.ReadAsync(Options(directory), default));

        Assert.Equal(new[] { "version" }, Trace(directory));
        Assert.Contains(ProviderAuthentication.MinimumClaudeVersion, error.Message);
        Assert.DoesNotContain(Key, error.ToString());
    }

    [Fact]
    public async Task Claude_catalog_error_does_not_expose_the_environment_key()
    {
        using var folder = new TempFolder();
        string directory = Directory.CreateDirectory(folder.File("auth-key-echo")).FullName;
        var error = await Assert.ThrowsAsync<IOException>(() => ClaudeCatalog.ReadAsync(Options(directory), default));

        Assert.Contains("[REDACTED]", error.Message);
        Assert.DoesNotContain(Key, error.ToString());
    }

    private static ProviderOptions Options(string directory) => TestEnvironment.ProviderOptions with
    {
        WorkingDirectory = directory, Authentication = new ProviderAuthentication(Key)
    };

    private static string[] Trace(string directory) => File.ReadAllLines(Path.Combine(directory, "auth-protocol.txt"));
}
