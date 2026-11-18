using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Providers;

/// <summary>
/// An explicitly selected API key, kept in this process only. A null authentication in
/// <see cref="ProviderOptions"/> keeps the official CLI's existing account sign-in.
/// </summary>
public sealed class ProviderAuthentication
{
    private const string Hidden = "[REDACTED]";
    internal const string CodexChatGptBaseUrl = "https://chatgpt.com/backend-api/";
    internal const string ClaudeBaseUrl = "https://api.anthropic.com";
    // Conservative compatibility baseline, not the first version with the host flag.
    // Official CHANGELOG 2.1.300 confirms host-managed settings transport isolation.
    public const string MinimumClaudeVersion = "2.1.300";
    private readonly string apiKey;
    private readonly string[] secretForms;
    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public ProviderAuthentication(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 4096 || apiKey.Any(char.IsControl))
            throw new ArgumentException(UiText.T("Введите API-ключ длиной до 4096 символов, без переводов строк и управляющих символов."), nameof(apiKey));
        this.apiKey = apiKey.Trim();
        secretForms = new[]
        {
            this.apiKey,
            JsonSerializer.Serialize(this.apiKey)[1..^1],
            JsonSerializer.Serialize(this.apiKey, RelaxedJson)[1..^1],
            Uri.EscapeDataString(this.apiKey)
        }.Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length).ToArray();
    }

    public bool Matches(ProviderAuthentication? other) => other is not null && string.Equals(apiKey, other.apiKey, StringComparison.Ordinal);
    public override string ToString() => "API key (redacted)";

    /// <summary>Remove the credential before any UI message, log truncation, or persistence.</summary>
    public string Redact(string text)
    {
        foreach (string secret in secretForms) text = text.Replace(secret, Hidden, StringComparison.Ordinal);
        return text;
    }

    /// <summary>Only non-secret process overrides. Managed login restrictions still apply.</summary>
    public IReadOnlyList<string> CodexArguments { get; } = Array.AsReadOnly(new[]
    {
        "-c", "cli_auth_credentials_store=\"ephemeral\"",
        "-c", "model_provider=\"openai\"",
        // An explicit /v1 URL disables Codex-native API-key model discovery. Empty means
        // the built-in OpenAI default, with routing selected by the credential's type.
        "-c", "openai_base_url=\"\"",
        "-c", "chatgpt_base_url=\"" + CodexChatGptBaseUrl + "\""
    });

    private static readonly string[] CodexAuthenticationEnvironment =
    [
        "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_BASE_URL",
        "OPENAI_FEDERATION_RULE_ID", "OPENAI_IDENTITY_TOKEN_FILE", "OPENAI_WORKLOAD_IDENTITY_CONTEXT",
        "OPENAI_ORGANIZATION", "OPENAI_PROJECT", "CODEX_INTERNAL_ORIGINATOR_OVERRIDE",
        "CODEX_AGENT_IDENTITY_AUTHAPI_BASE_URL", "CODEX_AGENT_IDENTITY_JWKS_BASE_URL", "CODEX_AUTHAPI_BASE_URL",
        "CODEX_REFRESH_TOKEN_URL_OVERRIDE", "CODEX_REVOKE_TOKEN_URL_OVERRIDE", "CODEX_APP_SERVER_LOGIN_CLIENT_ID"
    ];

    /// <summary>Codex receives the key later over stdio, after verifying effective routing and ephemeral storage.</summary>
    public void ConfigureCodex(ProcessStartInfo info) => RemoveEnvironment(info, CodexAuthenticationEnvironment);

    private static readonly string[] ClaudeAuthenticationEnvironment =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS",
        "ANTHROPIC_PROFILE", "ANTHROPIC_FEDERATION_RULE_ID", "ANTHROPIC_ORGANIZATION_ID", "ANTHROPIC_WORKSPACE_ID",
        "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_OAUTH_REFRESH_TOKEN", "CLAUDE_CODE_OAUTH_SCOPES",
        "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "CLAUDE_CODE_USE_MANTLE",
        "CLAUDE_CODE_USE_ANTHROPIC_AWS", "CLAUDE_CODE_USE_ANTHROPIC_GOOGLE_CLOUD",
        "ANTHROPIC_AWS_API_KEY", "ANTHROPIC_AWS_BASE_URL", "ANTHROPIC_AWS_WORKSPACE_ID",
        "ANTHROPIC_BEDROCK_BASE_URL", "ANTHROPIC_BEDROCK_MANTLE_BASE_URL",
        "ANTHROPIC_FOUNDRY_API_KEY", "ANTHROPIC_FOUNDRY_AUTH_TOKEN", "ANTHROPIC_FOUNDRY_BASE_URL", "ANTHROPIC_FOUNDRY_RESOURCE",
        "ANTHROPIC_GOOGLE_CLOUD_BASE_URL", "ANTHROPIC_GOOGLE_CLOUD_LOCATION", "ANTHROPIC_GOOGLE_CLOUD_PROJECT", "ANTHROPIC_GOOGLE_CLOUD_WORKSPACE_ID",
        "ANTHROPIC_VERTEX_BASE_URL", "ANTHROPIC_VERTEX_PROJECT_ID"
    ];

    /// <summary>
    /// The official CLI uses ANTHROPIC_API_KEY in print mode. Host-managed routing prevents
    /// settings.env from replacing it; managed permissions and model allowlists remain in force.
    /// https://code.claude.com/docs/en/env-vars
    /// </summary>
    public void ConfigureClaude(ProcessStartInfo info)
    {
        RemoveEnvironment(info, ClaudeAuthenticationEnvironment);
        info.Environment["CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST"] = "1";
        info.Environment["ANTHROPIC_BASE_URL"] = ClaudeBaseUrl;
        info.Environment["ANTHROPIC_API_KEY"] = apiKey;
        // The Claude process needs the key; its CAD MCP and tool subprocesses do not.
        info.Environment["CLAUDE_CODE_SUBPROCESS_ENV_SCRUB"] = "1";
    }

    /// <summary>Check host-managed routing support without giving the version process an API key.</summary>
    public async Task EnsureClaudeCompatibilityAsync(ProviderOptions options, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var info = ProviderProcess.StartInfo(options, ["--version"]);
        RemoveEnvironment(info, ClaudeAuthenticationEnvironment);
        info.Environment["CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST"] = "1";
        using var process = Process.Start(info) ?? throw new IOException(UiText.T("Не удалось проверить версию Claude Code без передачи API-ключа."));
        using var cancellation = timeout.Token.Register(() => ProviderProcess.Stop(process));
        var errors = ProviderProcess.DrainErrors(process, Redact);
        try
        {
            string versionLine = (await process.StandardOutput.ReadLineAsync(timeout.Token) ?? "").Trim();
            await process.WaitForExitAsync(timeout.Token);
            const string suffix = " (Claude Code)";
            if (process.ExitCode != 0 || !versionLine.EndsWith(suffix, StringComparison.Ordinal) ||
                !Version.TryParse(versionLine[..^suffix.Length], out var version) ||
                version < Version.Parse(MinimumClaudeVersion))
                throw new IOException(UiText.T("Для API-ключа требуется Claude Code 2.1.300 или новее. Обновите выбранный Claude Code. Ключ не передан."));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException(UiText.T("Не удалось проверить версию Claude Code без передачи API-ключа."));
        }
        finally { await ProviderProcess.FinishAsync(process, errors); }
    }

    private static void RemoveEnvironment(ProcessStartInfo info, IEnumerable<string> names)
    {
        var keys = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (string name in info.Environment.Keys.Where(keys.Contains).ToArray()) info.Environment.Remove(name);
    }

    internal void VerifyCodexConfiguration(JsonElement result)
    {
        var config = ProviderJson.Child(result, "config");
        if (config.ValueKind != JsonValueKind.Object ||
            ProviderJson.Text(config, "cli_auth_credentials_store") != "ephemeral" ||
            ProviderJson.Text(config, "model_provider") != "openai" ||
            HasValue(ProviderJson.Child(config, "openai_base_url")) ||
            ProviderJson.Text(config, "chatgpt_base_url")?.TrimEnd('/') != CodexChatGptBaseUrl.TrimEnd('/'))
            throw new IOException(UiText.T("Ключ не передан: Codex не подтвердил временное хранение ключа и официальный OpenAI API. Обновите Codex CLI и проверьте настройки или политику организации."));

        var catalogUrl = ProviderJson.Child(ProviderJson.Child(ProviderJson.Child(config, "model_providers"), "openai"), "model_catalog_url");
        if (HasValue(catalogUrl) || HasValue(ProviderJson.Child(config, "model_catalog_json")))
            throw new IOException(UiText.T("Ключ не передан: для входа через API-ключ отключите пользовательский model_catalog_url или model_catalog_json в настройках Codex. Каталог должен поступать от OpenAI."));
    }

    private static bool HasValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => false,
        JsonValueKind.String => !string.IsNullOrEmpty(value.GetString()),
        _ => true
    };

    internal object CodexLoginRequest(int id) => new { id, method = "account/login/start", @params = new { type = "apiKey", apiKey } };

    internal void VerifyCodexLogin(JsonElement result)
    {
        if (ProviderJson.Text(result, "type") != "apiKey")
            throw new IOException(UiText.T("Codex не подтвердил вход через API-ключ. Запрос модели не отправлен."));
    }

    internal ProviderSecretStream CreateStreamRedactor() => new(secretForms, Redact);
}

/// <summary>Retains only a possible credential prefix between streamed text deltas.</summary>
internal sealed class ProviderSecretStream
{
    private readonly (string Value, int[] Prefix)[] patterns;
    private readonly Func<string, string> redact;
    private string pending = "";

    public ProviderSecretStream(IEnumerable<string> secrets, Func<string, string> redact)
    {
        this.redact = redact;
        patterns = secrets.Select(secret => (secret, PrefixTable(secret))).ToArray();
    }

    public string Push(string text)
    {
        string value = redact(pending + text);
        int retained = 0;
        foreach (var pattern in patterns)
        {
            int matched = 0;
            foreach (char character in value)
            {
                while (matched > 0 && pattern.Value[matched] != character) matched = pattern.Prefix[matched - 1];
                if (pattern.Value[matched] == character) matched++;
                if (matched == pattern.Value.Length) matched = pattern.Prefix[matched - 1];
            }
            retained = Math.Max(retained, matched);
        }
        pending = retained == 0 ? "" : value[^retained..];
        return retained == 0 ? value : value[..^retained];
    }

    public string Flush()
    {
        string value = redact(pending);
        pending = "";
        return value;
    }

    private static int[] PrefixTable(string value)
    {
        var table = new int[value.Length];
        for (int index = 1, matched = 0; index < value.Length; index++)
        {
            while (matched > 0 && value[index] != value[matched]) matched = table[matched - 1];
            if (value[index] == value[matched]) matched++;
            table[index] = matched;
        }
        return table;
    }
}
