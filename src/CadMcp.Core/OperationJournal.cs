using System.Security.Cryptography;
using System.Text.Json;

namespace CadMcp.Core;

/// <summary>Durable receipts. Only terminal entries backed by disk leave the hot cache.</summary>
public sealed class OperationJournal
{
    public sealed record Entry(string Hash, string DocumentId, string State, Response? Result);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly string? directory;
    private readonly HashSet<string> recorded = new(StringComparer.Ordinal);
    public int Count => recorded.Count;
    public string Persistence => directory is null ? "memory" : "disk_worker_session";
    public OperationJournal(string? directory = null)
    {
        this.directory = directory;
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json")) recorded.Add(Path.GetFileNameWithoutExtension(path));
    }
    private static void Validate(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 96 || id.Any(c => !Portable.AsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new CadFault("INVALID_OPERATION_ID", "Use 1..96 ASCII letters, digits, hyphens or underscores; retain this id for retries");
    }
    public Entry? Find(string id)
    {
        Validate(id);
        if (entries.TryGetValue(id, out var cached)) return cached;
        if (directory is null || !recorded.Contains(id)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(Path.Combine(directory, id + ".json")), Wire.Json)
                ?? throw new JsonException("Empty operation receipt");
            Cache(id, entry); return entry;
        }
        catch (Exception e) when (e is JsonException or IOException)
        { throw new CadFault("JOURNAL_UNREADABLE", "Cannot verify recorded operation; inspect drawing before retrying: " + e.Message); }
    }
    private void Cache(string id, Entry entry)
    {
        if (directory is not null && entries.Count >= 128)
        {
            var terminal = entries.FirstOrDefault(p => p.Value.State is "completed" or "failed" or "cancelled");
            if (terminal.Key is not null) entries.Remove(terminal.Key);
        }
        entries[id] = entry;
    }
    private void Save(string id, Entry entry)
    {
        if (directory is not null)
        {
            var path = Path.Combine(directory, id + ".json"); var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, entry, Wire.Json); stream.Flush(true); }
            Portable.ReplaceFile(temp, path);
        }
        Cache(id, entry); recorded.Add(id);
    }
    public Entry? Begin(string id, Request request)
    {
        Validate(id);
        var hash = Portable.Hash(JsonSerializer.SerializeToUtf8Bytes(new
            { request.Operation, request.SessionId, request.DocumentId, request.ExpectedRevision, request.Data }, Wire.Json));
        if (Find(id) is { } prior)
        {
            if (prior.Hash != hash) throw new CadFault("OPERATION_ID_CONFLICT", "Operation id belongs to a different request; read its status");
            return prior;
        }
        Save(id, new(hash, request.DocumentId!, "queued", null));
        return null;
    }
    public void Running(string id) => Save(id, (Find(id) ?? throw new InvalidOperationException("Unknown operation")) with { State = "running" });
    public Response Complete(string id, Response result)
    {
        var entry = (Find(id) ?? throw new InvalidOperationException("Unknown operation")) with { State = result.Error is null ? "completed" : "failed", Result = result };
        try { Save(id, entry); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The drawing may already be committed. Keep its receipt in memory and leave the
            // on-disk running receipt intact, so a restart cannot silently repeat the edit.
            var fields = Wire.Element(result.Data ?? new { }).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
            fields["journal_warning"] = "Receipt could not be persisted; inspect drawing after restarting before retrying: " + error.Message;
            result = result with { Data = fields };
            Cache(id, entry with { Result = result });
        }
        return result;
    }
}
