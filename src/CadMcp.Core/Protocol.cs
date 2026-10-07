using System.Reflection;
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

    /// <summary>Product version from Directory.Build.props, without the source revision the SDK appends.</summary>
    public static string Version { get; } = ReadVersion();
    private static string ReadVersion()
    {
        string? value = typeof(Wire).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        int metadata = value.IndexOf('+');
        return metadata < 0 ? value : value[..metadata];
    }

    /// <summary>Per-user data folder shared by the plugin, the broker and the chat.</summary>
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CadMcp");
    public static string DataDirectory(params string[] parts) => Path.Combine([DataRoot, .. parts]);
    public static string WorkerRoot => DataDirectory("workers");
    public static string BrokerPipe
    {
        get
        {
            // The product runs on Windows; other platforms only host the automated tests.
            if (!OperatingSystem.IsWindows()) return "cadmcp-broker-" + Environment.UserName;
            using var identity = WindowsIdentity.GetCurrent();
            return "cadmcp-broker-" + identity.User!.Value.Replace('-', '_');
        }
    }

    /// <summary>Publish a worker descriptor atomically so the broker never reads a partial file.</summary>
    public static string PublishWorker(WorkerDescriptor worker)
    {
        Directory.CreateDirectory(WorkerRoot);
        string path = Path.Combine(WorkerRoot, worker.SessionId + ".json"), temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(worker, Json));
        File.Move(temp, path, true);
        return path;
    }

    /// <summary>Failures that mean a pipe call did not deliver a trustworthy response.</summary>
    public static bool IsTransportFailure(Exception error) =>
        error is IOException or OperationCanceledException or TimeoutException or InvalidDataException or JsonException;

    public static string? Text(this JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) ? p.GetString() : null;
    public static int Number(this JsonElement e, string key, int fallback)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var p) || p.ValueKind == JsonValueKind.Null) return fallback;
        return p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int value)
            ? value : throw new CadFault("INVALID_NUMBER", key + " must be an integer");
    }
}
