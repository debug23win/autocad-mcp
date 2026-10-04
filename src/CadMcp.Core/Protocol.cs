using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Principal;

namespace CadMcp.Core;

public sealed record Request(string RequestId, string Operation, string? SessionId = null,
    string? DocumentId = null, long? ExpectedRevision = null, JsonElement Data = default,
    DateTimeOffset? Deadline = null, string? OwnerId = null);
public sealed record Fault(string Code, string Message);
public sealed record Response(string RequestId, string Status, object? Data = null,
    string? SessionId = null, string? DocumentId = null, long? Revision = null, Fault? Error = null)
{
    public string SchemaVersion => "0.1";
    public static Response Fail(Request r, string code, string message) =>
        new(r.RequestId, code == "REVISION_CONFLICT" ? "conflict" : "failed", Error: new(code, message));
}
public sealed record WorkerDescriptor(string SessionId, string PipeName, int ProcessId, string Version);
public sealed class CadFault(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
public static class Wire
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
    public static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Json);
    public static string WorkerRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CadMcp", "workers");
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string BrokerPipe
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return "cadmcp-broker-" + identity.User!.Value.Replace('-', '_'); }
    }
    public static string? Text(this JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) ? p.GetString() : null;
    public static int Number(this JsonElement e, string key, int fallback) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) ? p.GetInt32() : fallback;
}
