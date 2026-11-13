using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Providers;

/// <summary>Codex 0.159 schema, process/thread scoped. Does not write the user's config.toml.</summary>
public static class ComputerUseConfiguration
{
    public const string DeniedReviewerToml = "[computer_use]\ndefault_app_access = \"deny\"\n[computer_use.windows]\nexes = []\naumids = {}\n[computer_use.macos]\nbundle_ids = {}\n";
    public static object Value(ComputerUseTarget? target, IReadOnlyDictionary<string, string>? deniedAumids = null)
    {
        if (target is not null && !ComputerUseAccessStore.Valid(target)) throw new InvalidDataException("Invalid Computer Use target");
        return new { default_app_access = "deny", windows = new { aumids = deniedAumids ?? new Dictionary<string, string>(), exes = target is null ? Array.Empty<object>() : new object[] {
            new { publisher_name = target.PublisherName, product_name = target.ProductName, binary_name = target.BinaryName, access = "allow" } } },
            macos = new { bundle_ids = new Dictionary<string, string>() } };
    }
    public static Dictionary<string, object> ThreadOverrides(ComputerUseTarget? target, IReadOnlyDictionary<string, string>? deniedAumids = null) => new() { ["computer_use"] = Value(target, deniedAumids) };
    public static Dictionary<string, string> DeniedAumids(JsonElement value)
    {
        if (!value.TryGetProperty("windows", out var windows) || !windows.TryGetProperty("aumids", out var map) || map.ValueKind != JsonValueKind.Object) return new();
        return map.EnumerateObject().ToDictionary(p => p.Name, _ => "deny", StringComparer.Ordinal);
    }
    public static string ReviewerToml(IReadOnlyDictionary<string, string>? aumids) => aumids is not { Count: > 0 } ? DeniedReviewerToml :
        DeniedReviewerToml.Replace("aumids = {}", "aumids = {" + string.Join(",", aumids.Keys.Select(k => JsonSerializer.Serialize(k) + "=\"deny\"")) + "}", StringComparison.Ordinal);
    public static bool Matches(JsonElement value, ComputerUseTarget? target)
    {
        if (value.ValueKind != JsonValueKind.Object || value.Text("default_app_access") != "deny" || !value.TryGetProperty("windows", out var windows) || windows.ValueKind != JsonValueKind.Object) return false;
        if (!windows.TryGetProperty("exes", out var exes) || exes.ValueKind == JsonValueKind.Null) return target is null;
        if (exes.ValueKind != JsonValueKind.Array) return false;
        if (target is null) return exes.GetArrayLength() == 0;
        return exes.GetArrayLength() == 1 && exes[0].Text("publisher_name") == target.PublisherName && exes[0].Text("product_name") == target.ProductName &&
            exes[0].Text("binary_name") == target.BinaryName && exes[0].Text("access") == "allow";
    }
    public static IEnumerable<string> Arguments(ComputerUseTarget? target)
    {
        _ = Value(target);
        yield return "-c"; yield return "computer_use.default_app_access=\"deny\"";
        yield return "-c"; yield return "computer_use.windows.exes=" + ExesToml(target);
        // Explicit empty maps keep this scoped to desktop executable identities.
        yield return "-c"; yield return "computer_use.windows.aumids={}";
    }
    private static string ExesToml(ComputerUseTarget? target) => target is null ? "[]" :
        "[{publisher_name=" + JsonSerializer.Serialize(target.PublisherName) + ",product_name=" + JsonSerializer.Serialize(target.ProductName) +
        ",binary_name=" + JsonSerializer.Serialize(target.BinaryName) + ",access=\"allow\"}]";
}
