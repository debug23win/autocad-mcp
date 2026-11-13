using System.Text.Json;

namespace CadMcp.Core;

/// <summary>User-owned checkbox, separate from chat history and from agent-supplied tool arguments.</summary>
public sealed record CSharpExecutionPolicy(bool Allowed, string Revision)
{
    public static string DefaultPath => Environment.GetEnvironmentVariable("CAD_MCP_CSHARP_POLICY_FILE") ?? Wire.DataDirectory("csharp-policy.json");
    public static CSharpExecutionPolicy Read(string? path = null)
    {
        try
        {
            var policy = JsonSerializer.Deserialize<CSharpExecutionPolicy>(File.ReadAllText(path ?? DefaultPath), Wire.Json);
            return policy is { Revision.Length: > 0 } ? policy : new(false, "missing");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(false, "unavailable"); }
    }
    public static void Store(bool allowed, string? path = null)
    {
        string file = Path.GetFullPath(path ?? DefaultPath); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(new CSharpExecutionPolicy(allowed, Guid.NewGuid().ToString("N")), Wire.Json)); File.Move(temp, file, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void RequireAllowed()
    {
        if (!Allowed) throw new CadFault("CSHARP_DISABLED", "C# is disabled. The user must enable the Allow C# checkbox; agents must not change this setting.");
    }
}
