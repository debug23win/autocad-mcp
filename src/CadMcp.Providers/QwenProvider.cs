using System.Diagnostics;

namespace CadMcp.Providers;

/// <summary>Qwen Code's official account/subscription authentication, through its ACP interface.</summary>
public sealed class QwenProvider(ProviderOptions options) : SubscriptionAcpProvider(options, SubscriptionCli.Qwen)
{
    public static string[] LoginArguments() => [];
    public static ProcessStartInfo CreateLoginStartInfo(ProviderOptions options) =>
        SubscriptionProcess.Create(options, LoginArguments(), "@qwen-code/qwen-code", "qwen", interactive: true);
    public static Task<string> CheckConnectionAsync(ProviderOptions options, CancellationToken ct) =>
        new QwenProvider(options).CheckAsync(ct);
    public static Task<IReadOnlyList<SubscriptionModel>> ReadModelsAsync(ProviderOptions options, CancellationToken ct) =>
        new QwenProvider(options).ReadModelsCoreAsync(ct);
}
