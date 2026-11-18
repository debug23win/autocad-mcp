using System.Text.Json;
using CadMcp.Providers;

namespace CadMcp.Tests;

public sealed class CodexCatalogTests
{
    [Fact]
    public void Gpt_62_models_preserve_the_catalogs_default_and_advertised_efforts()
    {
        // App-server metadata is authoritative: account-specific defaults and effort
        // availability must survive a CLI model rollout without an application update.
        using var response = JsonDocument.Parse("""
            [
              {"id":"sol-row","model":"gpt-6.2-sol","displayName":"GPT-6.2 Sol","isDefault":false,
               "defaultReasoningEffort":"medium","supportedReasoningEfforts":[
                 {"reasoningEffort":"low"},{"reasoningEffort":"medium"},{"reasoningEffort":"high"}]},
              {"id":"astra-row","model":"gpt-6.2-astra","displayName":"GPT-6.2 Astra","isDefault":true,
               "defaultReasoningEffort":"low","supportedReasoningEfforts":[
                 {"reasoningEffort":"low"},{"reasoningEffort":"medium"},{"reasoningEffort":"high"},
                 {"reasoningEffort":"xhigh"},{"reasoningEffort":"max"},{"reasoningEffort":"ultra"}]}
            ]
            """);
        var models = CodexCatalog.Parse(response.RootElement.EnumerateArray());

        Assert.Equal(new[] { "gpt-6.2-sol", "gpt-6.2-astra" }, models.Select(model => model.Id));
        Assert.Equal("GPT-6.2 Astra", models[1].DisplayName);
        var recommended = CodexCatalog.Select(models, null, null);
        Assert.Equal("gpt-6.2-astra", recommended.Model.Id);
        Assert.Equal("low", recommended.Effort);
        Assert.Equal("ultra", CodexCatalog.Select(models, "gpt-6.2-astra", "ultra").Effort);
        Assert.Throws<IOException>(() => CodexCatalog.Select(models, "gpt-6.2-sol", "ultra"));
    }

    [Fact]
    public void New_model_ids_and_efforts_need_no_local_allowlist()
    {
        using var response = JsonDocument.Parse("""
            [{"model":"future-account-model","displayName":"Account model","isDefault":true,
              "defaultReasoningEffort":"future-effort","supportedReasoningEfforts":[{"reasoningEffort":"future-effort"}]}]
            """);

        var models = CodexCatalog.Parse(response.RootElement.EnumerateArray());
        var selection = CodexCatalog.Select(models, "future-account-model", "future-effort");

        Assert.Single(models);
        Assert.Equal("future-account-model", selection.Model.Id);
        Assert.Equal("future-effort", selection.Effort);
    }

    [Fact]
    public void A_missing_or_hidden_new_model_is_not_invented_or_replaced()
    {
        using var response = JsonDocument.Parse("""
            [{"model":"account-model","isDefault":true,"defaultReasoningEffort":"medium"},
             {"model":"gpt-6.2-sol","hidden":true,"defaultReasoningEffort":"low"}]
            """);
        var models = CodexCatalog.Parse(response.RootElement.EnumerateArray());

        Assert.Equal("account-model", Assert.Single(models).Id);
        Assert.Throws<IOException>(() => CodexCatalog.Select(models, "gpt-6.2-sol", null));
        Assert.Throws<IOException>(() => CodexCatalog.Select(models, "gpt-6.2-astra", null));
        Assert.Throws<IOException>(() => CodexCatalog.Parse(Array.Empty<JsonElement>()));
    }
}
