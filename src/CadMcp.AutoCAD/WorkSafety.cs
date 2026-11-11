using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

internal static class WorkSafety
{
    private static readonly object Sync = new();
    private static readonly string Root = Wire.DataDirectory("recovery");
    internal static bool Required(Request request, JsonElement[]? operations) => request.Operation is "cad_lisp" or "cad_csharp" || operations is { Length: >= 25 }
        || operations?.Any(p => EditPlan.IsNonAtomic(p.Text("op") ?? "") || p.Text("op") is "erase" or "solid_boolean" or "solid_shell" or "assembly_update"
            or "text_replace" or "layer_merge" or "explode" or "trim" or "join" or "block_import" || p.Text("action") == "delete") == true;
    internal static object Checkpoint(Document doc, string session, string document, string operation)
    {
        string directory = Path.Combine(Root, session, Portable.Hash(System.Text.Encoding.UTF8.GetBytes(document)));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N") + ".dwg");
        try
        {
            // Wblock() clones the whole database, including layouts. Never SaveAs the live database.
            // Forced copy: a fast wblock could otherwise move objects out of the live drawing, and disposing the copy would free them.
            doc.Database.ForceWblockDatabaseCopy();
            using var copy = doc.Database.Wblock();
            copy.SaveAs(path, DwgVersion.Current);
            var info = new FileInfo(path);
            if (info.Length == 0) throw new IOException("Empty checkpoint");
            using var checkpointFile = File.OpenRead(path);
            using var hash = SHA256.Create();
            var record = new { path, sha256 = Convert.ToHexString(hash.ComputeHash(checkpointFile)), bytes = info.Length, operation_id = operation,
                original = doc.Name, created_at = DateTimeOffset.UtcNow, retention = "retained_until_user_cleanup", restore = "Open this copy separately; do not overwrite the working drawing" };
            File.WriteAllText(path + ".json", JsonSerializer.Serialize(record, Wire.Json));
            // Receipts may still refer to a failed/unknown operation. Never delete its recovery copy
            // merely because newer operations arrived; cleanup is an explicit user action.
            return record;
        }
        catch (System.Exception e) { throw new CadFault("CHECKPOINT_FAILED", "Required DWG checkpoint failed; drawing was not changed: " + e.Message); }
    }
    // The record this process wrote last, so per-action progress does not re-read and parse the file.
    private static Dictionary<string, JsonElement>? last;
    private static string? lastSession;
    internal static void Start(string session, Request request, object? checkpoint)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Path.Combine(Root, session));
            var record = Wire.Element(new { session_id = session, document_id = request.DocumentId, operation_id = request.Data.Text("operation_id"),
                operation = request.Operation, owner_id = request.OwnerId, started_at = DateTimeOffset.UtcNow,
                plugin_version = typeof(WorkSafety).Assembly.GetName().Version?.ToString(), process_id = Environment.ProcessId, checkpoint, phase = "accepted" });
            Save(session, record.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()), durable: true);
        }
    }
    /// <summary>
    /// Record progress. Intermediate phases are written without forcing them to disk: they survive a CAD crash,
    /// and only an operating system failure could lose them. The final phase is flushed.
    /// </summary>
    internal static void Phase(string session, string operation, string phase, bool final = false)
    {
        try { lock (Sync)
        {
            string path = Path.Combine(Root, session, "last-operation.json");
            var data = lastSession == session ? last : null;
            if (data is null)
            {
                if (!File.Exists(path)) return;
                data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path), Wire.Json)!;
            }
            if (data.GetValueOrDefault("operation_id").ValueKind != JsonValueKind.String || data["operation_id"].GetString() != operation) return;
            data["phase"] = Wire.Element(phase); data["last_signal_at"] = Wire.Element(DateTimeOffset.UtcNow);
            Save(session, data, final);
        } } catch(System.Exception e) when(e is IOException or UnauthorizedAccessException or JsonException){System.Diagnostics.Trace.WriteLine("Operation diagnostic: "+e.Message);}
    }
    private static void Save(string session, Dictionary<string, JsonElement> value, bool durable)
    {
        string path = Path.Combine(Root, session, "last-operation.json");
        using (var stream = new FileStream(path + ".tmp", FileMode.Create)) { JsonSerializer.Serialize(stream, value, Wire.Json); stream.Flush(durable); }
        Portable.ReplaceFile(path + ".tmp", path);
        last = value; lastSession = session;
    }
    internal static object Diagnostics(string session, string? document = null, bool verifyCheckpoints = false, CancellationToken ct = default)
    {
        JsonElement[] records;
        lock (Sync)
            records = Directory.Exists(Root) ? Directory.EnumerateFiles(Root, "last-operation.json", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).Select(p => { try { return JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(p), Wire.Json); } catch { return Wire.Element(new { unreadable = p }); } })
                .Where(p => document is null || p.Text("session_id") == session && p.Text("document_id") == document).Take(10).ToArray() : [];
        // Hashing can be slow. It must never hold the lock used by the CAD thread's progress writer.
        var evidence = records.Select(record =>
        {
            ct.ThrowIfCancellationRequested();
            var fields = record.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
            if (record.TryGetProperty("checkpoint", out var checkpoint) && checkpoint.ValueKind == JsonValueKind.Object)
                fields["checkpoint_evidence"] = CheckpointEvidence.Inspect(checkpoint, verifyCheckpoints, ct);
            return fields;
        }).ToArray();
        return new { session_id = session, directory = Root, last_operations = evidence,
            retention = "Recovery DWGs remain until explicit user cleanup; receipts do not guarantee a manually removed file still exists.",
            interpretation = "Non-terminal phase from a previous session means interrupted/unknown, never proof of rollback. Check checkpoint and receipt before retrying." };
    }
}
