using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CadMcp.Core;

namespace CadMcp.Providers;

/// <summary>The current Node.js Kimi Code CLI, using its official membership login and ACP.</summary>
public sealed class KimiProvider : SubscriptionAcpProvider
{
    public KimiProvider(ProviderOptions options) : this(options, KimiConfiguration.Home) { }
    internal KimiProvider(ProviderOptions options, string home) : base(options, SubscriptionCli.Kimi, home) { }
    public static string[] LoginArguments() => ["login"];
    public static ProcessStartInfo CreateLoginStartInfo(ProviderOptions options)
    {
        string home = KimiConfiguration.Home;
        string cwd = KimiConfiguration.Prepare(home, options.WorkingDirectory);
        var info = SubscriptionProcess.Create(options with { WorkingDirectory = cwd }, LoginArguments(), "@moonshot-ai/kimi-code", "kimi", interactive: true);
        KimiConfiguration.ApplyEnvironment(info, home);
        return info;
    }
    public static Task<string> CheckConnectionAsync(ProviderOptions options, CancellationToken ct) =>
        new KimiProvider(options).CheckAsync(ct);
    public static Task<IReadOnlyList<SubscriptionModel>> ReadModelsAsync(ProviderOptions options, CancellationToken ct) =>
        new KimiProvider(options).ReadModelsCoreAsync(ct);
}

internal static class KimiConfiguration
{
    private static readonly object Gate = new();
    internal static string Home => Wire.DataDirectory("providers", "kimi");
    internal const string ToolsSection = "[tools]\nenabled = [\"mcp__cad__*\"]\ndisabled = []\n";
    private const string InitialConfig = "# Managed by CAD MCP. Sign in with the CAD MCP login button.\n" +
        "default_permission_mode = \"manual\"\nbuiltin_product_skills = false\nauto_session_title = false\n\n" + ToolsSection +
        "\n[watch]\nenabled = false\n\n[workspace]\nrequire_trust = true\n";

    internal static string Prepare(string home, string workingDirectory)
    {
        LocalWorkPolicy.RequireLocal(home);
        LocalWorkPolicy.RequireLocal(workingDirectory);
        // A dedicated cwd prevents unrelated project MCP settings, hooks and permission rules from
        // being inherited. The stable hash retains per-working-folder session separation.
        string workspace = Path.Combine(home, "workspaces", Portable.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(workingDirectory)))[..24]);
        lock (Gate)
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(workspace);
            string configPath = Path.Combine(home, "config.toml");
            if (File.Exists(configPath) && new FileInfo(configPath).Length > 1024 * 1024) throw new IOException(UiText.T("Конфигурация Kimi Code слишком велика."));
            bool existed = File.Exists(configPath);
            string original = existed ? File.ReadAllText(configPath) : InitialConfig;
            string config = original;
            if (config.Length > 1024 * 1024) throw new IOException(UiText.T("Конфигурация Kimi Code слишком велика."));
            // This home belongs to the integration; the CLI owns its OAuth/model sections. Keep those
            // intact and reassert our tool allowlist after login rewrites config.toml.
            config = ProtectTools(config);
            if (Regex.IsMatch(config, @"(?m)^\s*\[\[\s*hooks(?:[.\]\s])") || File.Exists(Path.Combine(home, "mcp.json")))
                throw new IOException(UiText.T("В отдельной конфигурации Kimi для CAD MCP обнаружены дополнительные hooks или MCP-серверы. Удалите их перед подключением."));
            if (!existed || original != config)
            {
                // Login runs in the official CLI, outside this lock. Never replace a newer config
                // written by that process while checking the tool policy.
                if (existed && File.ReadAllText(configPath) != original)
                    throw new IOException(UiText.T("Конфигурация Kimi изменилась во время подключения. Повторите подключение после завершения входа."));
                string temp = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temp, config, new UTF8Encoding(false)); File.Move(temp, configPath, overwrite: true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        return workspace;
    }

    internal static string ProtectTools(string config)
    {
        var header = Regex.Match(config, @"(?m)^[ \t]*\[tools\][ \t]*(?:#[^\r\n]*)?\r?$");
        if (!header.Success) return config.TrimEnd() + "\n\n" + ToolsSection;
        int bodyStart = header.Index + header.Length;
        var next = Regex.Match(config[bodyStart..], @"(?m)^[ \t]*\[");
        int end = next.Success ? bodyStart + next.Index : config.Length;
        return config[..header.Index] + ToolsSection + "\n" + config[end..];
    }

    internal static void ApplyEnvironment(ProcessStartInfo info, string home)
    {
        foreach (string key in info.Environment.Keys.ToArray())
            if (key.StartsWith("KIMI_MODEL_", StringComparison.OrdinalIgnoreCase) || key is "KIMI_CODE_BASE_URL" or "KIMI_CODE_OAUTH_HOST" or "KIMI_OAUTH_HOST" or "KIMI_CODE_CUSTOM_HEADERS")
                info.Environment.Remove(key);
        info.Environment["KIMI_CODE_HOME"] = home;
        info.Environment["KIMI_CODE_WATCH"] = "0";
        info.Environment["KIMI_CODE_TRUST_WORKSPACE"] = "0";
        info.Environment["KIMI_CODE_BUILTIN_PRODUCT_SKILLS"] = "0";
        info.Environment["KIMI_CODE_BACKGROUND_KEEP_ALIVE_ON_EXIT"] = "0";
    }
}
