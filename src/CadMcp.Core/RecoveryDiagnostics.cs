using System.Text.Json;

namespace CadMcp.Core;

/// <summary>Reads persisted evidence without accessing a live CAD object or another project's history.</summary>
public static class RecoveryDiagnostics
{
    public static JsonElement[] Read(string root, string session, string? document, string? projectKey, int limit = 10)
    {
        if (!Directory.Exists(root)) return [];
        var records = new List<(DateTime Written, JsonElement Value)>();
        foreach (var path in Directory.EnumerateFiles(root, "last-operation*.json", SearchOption.AllDirectories))
        {
            try
            {
                var value = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path), Wire.Json);
                bool sameDocument = value.Text("session_id") == session && value.Text("document_id") == document;
                string? recordedProject = value.Text("project_key");
                if (recordedProject is null && value.TryGetProperty("checkpoint", out var checkpoint) && checkpoint.ValueKind == JsonValueKind.Object
                    && checkpoint.Text("original") is { } original && Path.IsPathFullyQualified(original))
                    recordedProject = ProjectIdentity.ForDrawing(original, "");
                if (document is not null && !sameDocument && (projectKey is null || projectKey != recordedProject)) continue;
                records.Add((File.GetLastWriteTimeUtc(path), value));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
            {
                // A malformed record cannot be assigned to a pinned project. Preserve other readable evidence.
                if (document is null) records.Add((DateTime.MinValue, Wire.Element(new { unreadable = path })));
            }
        }
        return records.OrderByDescending(r => r.Written).Take(limit).Select(r => r.Value).ToArray();
    }
}
