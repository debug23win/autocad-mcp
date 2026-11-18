using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using CadMcp.Core;
using CadMcp.Host;
using CadMcp.Providers;
namespace CadMcp.Tests;
public sealed class PreferencesAndCompilerTests
{
    [Fact]
    public void Provider_models_and_progress_preference_survive_a_new_store_instance()
    {
        using var folder = new TempFolder(); string path = folder.File("preferences.json");
        var chosen = new ChatPreferences(1, "codex-model", "high", "claude-model", "max", false);
        new ChatPreferencesStore(path).Save(chosen);
        Assert.Equal(chosen, new ChatPreferencesStore(path).Load());
        var switched = chosen with { Provider = 0, CodexModel = "other-model", CodexEffort = "medium" };
        new ChatPreferencesStore(path).Save(switched);
        Assert.Equal(switched, new ChatPreferencesStore(path).Load());
        Assert.Equal("claude-model", new ChatPreferencesStore(path).Load()!.ClaudeModel);
        Assert.Empty(Directory.GetFiles(folder.Path, "*.tmp"));
    }
    [Fact]
    public void Damaged_preferences_do_not_break_chat_startup()
    {
        using var folder = new TempFolder(); string path = folder.File("preferences.json");
        File.WriteAllText(path, "{broken"); Assert.Null(new ChatPreferencesStore(path).Load());
        File.WriteAllText(path, "{\"provider\":99}"); Assert.Null(new ChatPreferencesStore(path).Load());
    }
    [Fact]
    public void Perplexity_preferences_preserve_all_provider_choices_and_read_legacy_files()
    {
        using var folder = new TempFolder(); string path = folder.File("preferences.json");
        var selected = new ChatPreferences(2, "codex-model", "high", "claude-model", "max", false,
            "vendor/api-model", "medium");
        var store = new ChatPreferencesStore(path);
        store.Save(selected);
        Assert.Equal(selected, store.Load());
        store.Save(selected with { Provider = 1 });
        Assert.Equal("vendor/api-model", store.Load()!.PerplexityModel);
        Assert.Equal("medium", store.Load()!.PerplexityEffort);
        Assert.Equal("codex-model", store.Load()!.CodexModel);
        File.WriteAllText(path, """{"provider":1,"codex_model":"old-codex","codex_effort":"high","claude_model":"opus","claude_effort":"max","show_progress":true}""");
        var legacy = store.Load()!;
        Assert.Equal("opus", legacy.ClaudeModel);
        Assert.Null(legacy.PerplexityModel);
        Assert.Null(legacy.PerplexityEffort);
        Assert.False(legacy.CodexApiKeyMode);
        Assert.False(legacy.ClaudeApiKeyMode);
        Assert.Throws<InvalidDataException>(() => store.Save(selected with { Provider = 99 }));
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void API_billing_modes_persist_independently_with_provider_model_choices(bool codexApi, bool claudeApi)
    {
        using var folder = new TempFolder();
        string path = folder.File("preferences.json");
        var preferences = new ChatPreferences(0, "chosen-codex", "high", "chosen-claude", "max",
            PerplexityModel: "chosen-perplexity", QwenModel: "chosen-qwen", KimiModel: "chosen-kimi",
            CodexApiKeyMode: codexApi, ClaudeApiKeyMode: claudeApi);
        new ChatPreferencesStore(path).Save(preferences);
        var reopened = new ChatPreferencesStore(path);
        Assert.Equal(preferences, reopened.Load());
        reopened.Save(preferences with { Provider = 1 });
        var switched = reopened.Load()!;
        Assert.Equal(codexApi, switched.CodexApiKeyMode);
        Assert.Equal(claudeApi, switched.ClaudeApiKeyMode);
        Assert.Equal(preferences.CodexModel, switched.CodexModel);
        Assert.Equal(preferences.ClaudeModel, switched.ClaudeModel);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(codexApi, json.RootElement.GetProperty("codex_api_key_mode").GetBoolean());
        Assert.Equal(claudeApi, json.RootElement.GetProperty("claude_api_key_mode").GetBoolean());
        Assert.DoesNotContain(json.RootElement.EnumerateObject(), property =>
            property.Name is "codex_api_key" or "claude_api_key" or "api_key" or "authentication");
        Assert.Empty(Directory.GetFiles(folder.Path, "*.tmp"));
    }
    [Fact]
    public void Imported_chat_state_cannot_supply_API_keys_or_billing_preferences()
    {
        var state = new ChatState(0, "trusted-codex.exe", "trusted-claude.exe", "host.exe", "workspace", "old-session", "Сохранённый разговор");
        var untrusted = JsonNode.Parse(JsonSerializer.Serialize(state))!.AsObject();
        untrusted["CodexApiKeyMode"] = true;
        untrusted["ClaudeApiKeyMode"] = true;
        untrusted["CodexApiKey"] = "fake-imported-openai-key";
        untrusted["ClaudeApiKey"] = "fake-imported-anthropic-key";
        untrusted["Authentication"] = new JsonObject { ["ApiKey"] = "fake-imported-authentication-key" };
        var imported = JsonSerializer.Deserialize<ChatState>(untrusted.ToJsonString())!;
        Assert.Equal(state, imported);
        string exported = JsonSerializer.Serialize(imported);
        Assert.DoesNotContain("fake-imported-", exported);
        Assert.DoesNotContain("ApiKeyMode", exported);
        Assert.DoesNotContain("Authentication", exported);
        // Keys and billing preferences are not part of the history format, even as optional properties.
        Assert.DoesNotContain(typeof(ChatState).GetProperties(), property => property.Name.Contains("ApiKey", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(ChatPreferences).GetProperties(), property =>
            property.Name.Contains("Key", StringComparison.Ordinal) && property.PropertyType != typeof(bool));
    }
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Subscription_CLI_preferences_keep_each_providers_selection(int provider)
    {
        using var folder = new TempFolder();
        var store = new ChatPreferencesStore(folder.File("preferences.json"));
        var selected = new ChatPreferences(provider, "codex", "high", "claude", "max",
            PerplexityModel: "vendor/api", QwenModel: "qwen-account-model", KimiModel: "kimi-account-model");
        store.Save(selected);
        Assert.Equal(selected, store.Load());
        store.Save(selected with { Provider = 0 });
        Assert.Equal(selected.QwenModel, store.Load()!.QwenModel);
        Assert.Equal(selected.KimiModel, store.Load()!.KimiModel);
    }
    [Fact]
    public void Real_compiler_emits_and_executes_ordinary_csharp_without_an_installed_sdk()
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        const string code = "using System.Linq; public static class CadMcpScript { public static object Run() => new[] { 1, 2, 3 }.Select(x => x*x).Sum(); }";
        byte[] bytes = CSharpReferenceCompiler.Compile(code, paths);
        var load = new AssemblyLoadContext("compiler-verification", true);
        try { using var memory = new MemoryStream(bytes); var assembly = load.LoadFromStream(memory); Assert.Equal(14, assembly.GetType("CadMcpScript")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)); }
        finally { load.Unload(); }
        var fault = Assert.Throws<CadFault>(() => CSharpReferenceCompiler.Compile("public class {", paths));
        Assert.Equal("CSHARP_COMPILE_FAILED", fault.Code);
    }
    [Fact]
    public void Progress_is_readable_and_copied_answer_stays_the_final_answer()
    {
        using var folder = new TempFolder();
        ChatLine[] lines = [new("assistant", "Готово", Steps: ["Читаю страницы PDF", "Проверяю размеры", "Завершено: cad_search"])];
        string html = ChatMarkup.ConversationHtml(lines, folder.Path);
        Assert.Contains("Ход работы", html); Assert.Contains("Читаю страницы PDF", html); Assert.DoesNotContain("cad_search", html);
        Assert.Equal("Готово", ChatMarkup.AnswerText(lines, 0));
        Assert.True(html.IndexOf("progress-notes", StringComparison.Ordinal) < html.IndexOf("class='content'", StringComparison.Ordinal));
    }
}
