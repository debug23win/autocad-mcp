using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CadMcp.Providers;

namespace CadMcp.Tests;

public sealed class ProviderAuthenticationTests
{
    private const string Key = "sk-cad-fixture-secret-123456789";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("key\n")]
    [InlineData("key\rvalue")]
    [InlineData("key\tvalue")]
    [InlineData("key\0value")]
    public void Empty_or_control_character_credentials_are_rejected(string? key) =>
        Assert.Throws<ArgumentException>(() => new ProviderAuthentication(key!));

    [Fact]
    public void Credentials_have_a_size_limit_and_never_appear_in_object_diagnostics()
    {
        Assert.Throws<ArgumentException>(() => new ProviderAuthentication(new string('x', 4097)));
        _ = new ProviderAuthentication(new string('x', 4096));
        var authentication = new ProviderAuthentication(" " + Key + " ");

        Assert.True(authentication.Matches(new ProviderAuthentication(Key)));
        Assert.False(authentication.Matches(new ProviderAuthentication(Key.ToUpperInvariant())));
        Assert.False(authentication.Matches(null));
        Assert.DoesNotContain(Key, authentication.ToString());
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(authentication));
        Assert.DoesNotContain(Key, (TestEnvironment.ProviderOptions with { Authentication = authentication }).ToString());
        Assert.DoesNotContain(typeof(ProviderAuthentication).GetProperties(), property => property.Name.Equals("ApiKey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Codex_key_is_sent_only_in_the_login_request_after_configuration_verification()
    {
        var authentication = new ProviderAuthentication(Key);
        Assert.DoesNotContain(authentication.CodexArguments, argument => argument.Contains(Key, StringComparison.Ordinal));
        Assert.Contains("cli_auth_credentials_store=\"ephemeral\"", authentication.CodexArguments);
        Assert.Contains("model_provider=\"openai\"", authentication.CodexArguments);
        Assert.Contains("openai_base_url=\"\"", authentication.CodexArguments);
        Assert.DoesNotContain(authentication.CodexArguments, argument => argument.Contains("forced_login_method", StringComparison.Ordinal));

        authentication.VerifyCodexConfiguration(ValidConfig());
        var request = JsonSerializer.SerializeToElement(authentication.CodexLoginRequest(7));
        Assert.Equal(7, request.GetProperty("id").GetInt32());
        Assert.Equal("account/login/start", request.GetProperty("method").GetString());
        Assert.Equal("apiKey", request.GetProperty("params").GetProperty("type").GetString());
        Assert.Equal(Key, request.GetProperty("params").GetProperty("apiKey").GetString());
        authentication.VerifyCodexLogin(JsonSerializer.SerializeToElement(new { type = "apiKey" }));
        Assert.Throws<IOException>(() => authentication.VerifyCodexLogin(JsonSerializer.SerializeToElement(new { type = "chatgpt" })));
        Assert.Throws<IOException>(() => authentication.VerifyCodexLogin(JsonSerializer.SerializeToElement(new { })));
    }

    [Theory]
    [InlineData("cli_auth_credentials_store", "file")]
    [InlineData("cli_auth_credentials_store", null)]
    [InlineData("model_provider", "custom-gateway")]
    [InlineData("model_provider", null)]
    [InlineData("openai_base_url", "https://proxy.example/v1")]
    [InlineData("openai_base_url", "https://api.openai.com/v1")]
    [InlineData("chatgpt_base_url", "https://proxy.example/backend-api/")]
    [InlineData("chatgpt_base_url", null)]
    public void Codex_configuration_cannot_persist_the_key_or_select_alternate_routing(string name, string? value)
    {
        var authentication = new ProviderAuthentication(Key);
        var result = JsonNode.Parse(ValidConfig().GetRawText())!.AsObject();
        result["config"]![name] = value;
        var error = Assert.Throws<IOException>(() => authentication.VerifyCodexConfiguration(JsonSerializer.SerializeToElement(result)));
        Assert.DoesNotContain(Key, error.ToString());
    }

    [Fact]
    public void Codex_custom_catalogs_are_rejected_before_the_key_is_sent()
    {
        var authentication = new ProviderAuthentication(Key);
        foreach (bool remote in new[] { false, true })
        {
            var result = JsonNode.Parse(ValidConfig().GetRawText())!.AsObject();
            if (remote)
                result["config"]!["model_providers"] = new JsonObject { ["openai"] = new JsonObject { ["model_catalog_url"] = "https://chatgpt.com/custom-catalog" } };
            else result["config"]!["model_catalog_json"] = "C:/custom-models.json";
            Assert.Throws<IOException>(() => authentication.VerifyCodexConfiguration(JsonSerializer.SerializeToElement(result)));
        }
        Assert.Throws<IOException>(() => authentication.VerifyCodexConfiguration(JsonSerializer.SerializeToElement(new { })));
    }

    [Fact]
    public void Codex_clears_alternate_authentication_without_changing_process_or_policy_settings()
    {
        var authentication = new ProviderAuthentication(Key);
        var info = EmptyProcess();
        string[] credentials =
        [
            "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_BASE_URL",
            "OPENAI_FEDERATION_RULE_ID", "OPENAI_IDENTITY_TOKEN_FILE", "OPENAI_WORKLOAD_IDENTITY_CONTEXT",
            "OPENAI_ORGANIZATION", "OPENAI_PROJECT", "CODEX_INTERNAL_ORIGINATOR_OVERRIDE",
            "CODEX_AGENT_IDENTITY_AUTHAPI_BASE_URL", "CODEX_AGENT_IDENTITY_JWKS_BASE_URL", "CODEX_AUTHAPI_BASE_URL",
            "CODEX_REFRESH_TOKEN_URL_OVERRIDE", "CODEX_REVOKE_TOKEN_URL_OVERRIDE", "CODEX_APP_SERVER_LOGIN_CLIENT_ID"
        ];
        foreach (string name in credentials) info.Environment[name] = "ambient";
        info.Environment["openai_api_key"] = "lowercase ambient";
        info.Environment["CAD_MCP_READ_ONLY"] = "1";
        info.Environment["HTTPS_PROXY"] = "https://corporate.example";
        info.Environment["SSL_CERT_FILE"] = "corporate.pem";

        authentication.ConfigureCodex(info);

        foreach (string name in credentials) Assert.False(info.Environment.ContainsKey(name));
        Assert.False(info.Environment.ContainsKey("openai_api_key"));
        Assert.Equal("1", info.Environment["CAD_MCP_READ_ONLY"]);
        Assert.Equal("https://corporate.example", info.Environment["HTTPS_PROXY"]);
        Assert.Equal("corporate.pem", info.Environment["SSL_CERT_FILE"]);
        Assert.DoesNotContain(info.Environment.Values, value => value?.Contains(Key, StringComparison.Ordinal) == true);
        Assert.Empty(info.ArgumentList);
    }

    [Fact]
    public void Claude_uses_the_selected_key_and_official_endpoint_in_its_process_only()
    {
        var authentication = new ProviderAuthentication(Key);
        var info = EmptyProcess();
        string[] alternatives =
        [
            "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_CUSTOM_HEADERS", "ANTHROPIC_PROFILE", "ANTHROPIC_FEDERATION_RULE_ID",
            "ANTHROPIC_ORGANIZATION_ID", "ANTHROPIC_WORKSPACE_ID", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_OAUTH_REFRESH_TOKEN",
            "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "CLAUDE_CODE_USE_MANTLE",
            "CLAUDE_CODE_USE_ANTHROPIC_AWS", "CLAUDE_CODE_USE_ANTHROPIC_GOOGLE_CLOUD", "ANTHROPIC_FOUNDRY_AUTH_TOKEN"
        ];
        foreach (string name in alternatives) info.Environment[name] = "ambient";
        info.Environment["ANTHROPIC_API_KEY"] = "other-key";
        info.Environment["ANTHROPIC_BASE_URL"] = "https://proxy.example";
        info.Environment["CAD_MCP_READ_ONLY"] = "1";
        info.Environment["CLAUDE_CODE_USE_POWERSHELL_TOOL"] = "0";
        info.Environment["DISABLE_TELEMETRY"] = "1";

        authentication.ConfigureClaude(info);

        foreach (string name in alternatives) Assert.False(info.Environment.ContainsKey(name));
        Assert.Equal(Key, info.Environment["ANTHROPIC_API_KEY"]);
        Assert.Equal("https://api.anthropic.com", info.Environment["ANTHROPIC_BASE_URL"]);
        Assert.Equal("1", info.Environment["CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST"]);
        Assert.Equal("1", info.Environment["CLAUDE_CODE_SUBPROCESS_ENV_SCRUB"]);
        Assert.Equal("1", info.Environment["CAD_MCP_READ_ONLY"]);
        Assert.Equal("0", info.Environment["CLAUDE_CODE_USE_POWERSHELL_TOOL"]);
        Assert.Equal("1", info.Environment["DISABLE_TELEMETRY"]);
        Assert.Empty(info.ArgumentList);
    }

    [Fact]
    public void Redaction_handles_raw_escaped_and_split_stream_credentials()
    {
        const string key = "sk-fixture-quote\"slash\\<>-end";
        var authentication = new ProviderAuthentication(key);
        string[] representations =
        [
            key, JsonSerializer.Serialize(key)[1..^1],
            JsonSerializer.Serialize(key, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })[1..^1],
            Uri.EscapeDataString(key)
        ];
        foreach (string representation in representations)
        {
            Assert.Equal("before [REDACTED] after", authentication.Redact("before " + representation + " after"));
            for (int cut = 1; cut < representation.Length; cut++)
            {
                var stream = authentication.CreateStreamRedactor();
                string first = stream.Push("before " + representation[..cut]);
                Assert.Equal("before ", first);
                string complete = first + stream.Push(representation[cut..] + " after") + stream.Flush();
                Assert.Equal("before [REDACTED] after", complete);
                Assert.DoesNotContain(key, complete);
            }
        }
    }

    private static ProcessStartInfo EmptyProcess()
    {
        var info = new ProcessStartInfo("fixture") { UseShellExecute = false };
        info.Environment.Clear();
        return info;
    }

    private static JsonElement ValidConfig() => JsonSerializer.SerializeToElement(new
    {
        config = new
        {
            cli_auth_credentials_store = "ephemeral", model_provider = "openai", openai_base_url = "",
            chatgpt_base_url = "https://chatgpt.com/backend-api/", model_catalog_json = (string?)null, model_providers = new { }
        }
    });
}
