using System.Security.Cryptography;
using System.Text.Json;

namespace CadMcp.Core;

/// <summary>Durable receipts. Only terminal entries backed by disk leave the hot cache.</summary>
public sealed class OperationJournal
{
    public sealed record Entry(string Hash, string DocumentId, string State, Response? Result,
        Request? Request = null, DateTimeOffset? CreatedAt = null, DateTimeOffset? UpdatedAt = null);
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly string? directory;
    private readonly HashSet<string> recorded = new(StringComparer.Ordinal);
    // Listing metadata, so cad_operation_list does not re-read every receipt on each call.
    private readonly record struct Listing(string DocumentId, string? OwnerId, DateTimeOffset? CreatedAt);
    private readonly Dictionary<string, Listing> listings = new(StringComparer.Ordinal);
    public int Count { get { lock (sync) return recorded.Count; } }
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
        lock (sync) return FindCore(id);
    }
    private Entry? FindCore(string id)
    {
        Validate(id);
        if (entries.TryGetValue(id, out var cached)) return cached;
        if (directory is null || !recorded.Contains(id)) return null;
        try { var entry = ReadEntry(id); Cache(id, entry); return entry; }
        catch (Exception e) when (e is JsonException or IOException)
        { throw new CadFault("JOURNAL_UNREADABLE", "Cannot verify recorded operation; inspect drawing before retrying: " + e.Message); }
    }
    private Entry ReadEntry(string id) =>
        JsonSerializer.Deserialize<Entry>(File.ReadAllText(Path.Combine(directory!, id + ".json")), Wire.Json)
            ?? throw new JsonException("Empty operation receipt");
    private void Cache(string id, Entry entry)
    {
        if (directory is not null && entries.Count >= 128)
        {
            var terminal = entries.FirstOrDefault(p => p.Value.State is "completed" or "failed" or "cancelled");
            if (terminal.Key is not null) entries.Remove(terminal.Key);
        }
        entries[id] = entry;
        listings[id] = new(entry.DocumentId, entry.Request?.OwnerId, entry.CreatedAt);
    }
    private void Save(string id, Entry entry)
    {
        entry = entry with { UpdatedAt = DateTimeOffset.UtcNow };
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
        lock (sync) return BeginCore(id, request);
    }
    private Entry? BeginCore(string id, Request request)
    {
        Validate(id);
        var hash = Portable.Hash(JsonSerializer.SerializeToUtf8Bytes(new
            { request.Operation, request.SessionId, request.DocumentId, request.ExpectedRevision, request.Data }, Wire.Json));
        if (Find(id) is { } prior)
        {
            if (prior.Hash != hash) throw new CadFault("OPERATION_ID_CONFLICT", "Operation id belongs to a different request; read its status");
            return prior;
        }
        Save(id, new(hash, request.DocumentId!, "queued", null, request, DateTimeOffset.UtcNow));
        return null;
    }
    public void Running(string id) { lock (sync) Save(id, (Find(id) ?? throw new InvalidOperationException("Unknown operation")) with { State = "running" }); }
    public Response Complete(string id, Response result)
    {
        lock (sync) return CompleteCore(id, result);
    }
    private Response CompleteCore(string id, Response result)
    {
        var entry = (Find(id) ?? throw new InvalidOperationException("Unknown operation")) with { State = result.Error?.Code == "CANCELLED" ? "cancelled" : result.Error is null ? "completed" : "failed", Result = result };
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
    /// <summary>
    /// Newest receipts of one drawing. An unreadable receipt is skipped and its id is added to
    /// <paramref name="unreadable"/>; it never hides the readable ones.
    /// </summary>
    public IReadOnlyList<(string Id, Entry Entry)> Recent(string document, DateTimeOffset since, int limit = 50, string? owner = null, ICollection<string>? unreadable = null)
    {
        if (limit is < 1 or > 100) throw new CadFault("INVALID_LIMIT", "Operation list limit must be 1..100");
        lock (sync)
        {
            var result = new List<(string Id, Entry Entry)>();
            foreach (var id in recorded.Where(id => Listed(id, unreadable) is { } l && l.DocumentId == document
                    && (owner is null || l.OwnerId == owner) && (l.CreatedAt ?? DateTimeOffset.MinValue) >= since)
                .OrderByDescending(id => listings[id].CreatedAt).ThenBy(id => id, StringComparer.Ordinal))
            {
                if (result.Count == limit) break;
                // Read without caching: listing must not evict the hot receipts of operations in progress.
                try { result.Add((id, entries.TryGetValue(id, out var cached) ? cached : ReadEntry(id))); }
                catch (Exception e) when (e is JsonException or IOException) { unreadable?.Add(id); }
            }
            return result;
        }
    }
    private Listing? Listed(string id, ICollection<string>? unreadable)
    {
        if (listings.TryGetValue(id, out var known)) return known;
        try
        {
            var entry = ReadEntry(id);
            return listings[id] = new(entry.DocumentId, entry.Request?.OwnerId, entry.CreatedAt);
        }
        catch (Exception e) when (e is JsonException or IOException) { unreadable?.Add(id); return null; }
    }
    public static object Summary(string id, Entry entry, bool active = true)
    {
        var outer = Wire.Element(entry.Result?.Data ?? new { });
        var result = outer.TryGetProperty("result", out var r) ? r : outer;
        return new { operation_id = id, document_id = entry.DocumentId, owner_id=entry.Request?.OwnerId,
            state = !active && (entry.State is "queued" or "running") ? "unknown" : entry.State,
            operation = entry.Request?.Operation, created_at = entry.CreatedAt, updated_at = entry.UpdatedAt,
            error = entry.Result?.Error, acceptance = result.TryGetProperty("acceptance", out var a) ? (JsonElement?)a.Clone() : null,
            changed_entities = result.TryGetProperty("entity_count", out var count) && count.TryGetInt32(out int total) ? total
                : result.TryGetProperty("entities", out var listed) && listed.ValueKind == JsonValueKind.Array ? (int?)listed.GetArrayLength() : null,
            handles = result.TryGetProperty("changed_handles", out var changed) && changed.ValueKind == JsonValueKind.Array ? changed.EnumerateArray().Select(h => h.GetString()).OfType<string>().ToArray()
                : result.TryGetProperty("entities", out var es) && es.ValueKind == JsonValueKind.Array ? es.EnumerateArray().Where(e => e.Text("handle") is not null).Select(e => e.Text("handle")).ToArray() : null,
            document_state = outer.TryGetProperty("document_state", out var d) ? (JsonElement?)d.Clone() : null,
            transaction = result.Text("transaction"), historical = !active };
    }
}
