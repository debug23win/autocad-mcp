using System.Text;
using System.Text.Json;
namespace CadMcp.Core;

/// <summary>Durable native-call breadcrumbs, without source code, arguments or geometry.</summary>
public static class BenchmarkTrace
{
    public static bool Record(Request request, string phase, string? code = null)
    {
        if (request.OwnerId is not { } owner || !owner.StartsWith("benchmark-", StringComparison.Ordinal) || !Guid.TryParseExact(owner[10..], "N", out _)) return true;
        try
        {
            string folder = Wire.DataDirectory("benchmarks", owner[10..]); Directory.CreateDirectory(folder);
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, version = Wire.Version, phase, request_id = request.RequestId,
                operation = request.Operation, session_id = request.SessionId, document_id = request.DocumentId, expected_revision = request.ExpectedRevision, code }, Wire.Json) + "\n");
            using var file = new FileStream(Path.Combine(folder, "native-trace.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
            file.Write(bytes); file.Flush(true); return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
