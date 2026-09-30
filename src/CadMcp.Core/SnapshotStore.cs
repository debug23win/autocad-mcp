using System.Text.Json;

namespace CadMcp.Core;

public sealed record Snapshot(string Id, string DocumentId, long Revision, DateTimeOffset Created,
    IReadOnlyList<JsonElement> Entities, bool Truncated, string Scope = "", int Limit = 0);
public sealed class SnapshotStore
{
    private readonly Dictionary<string, Snapshot> snapshots = new();
    public Snapshot? Reuse(string document, string scope, long revision, int limit) =>
        snapshots.Values.LastOrDefault(s => s.DocumentId == document && s.Scope == scope && s.Revision == revision && s.Limit == limit);
    public Snapshot Add(string document, long revision, IEnumerable<JsonElement> entities, bool truncated, string scope = "", int limit = 0)
    {
        var bounded = new List<JsonElement>(); int chars = 0;
        foreach (var original in entities)
        {
            var e = original;
            int size = e.GetRawText().Length;
            if (size > 256 * 1024)
            {
                e = Wire.Element(new { handle = e.Text("handle"), type = e.Text("type"), layer = e.Text("layer"),
                    access = "partial", limitation = "entity_payload_limit" });
                size = e.GetRawText().Length;
            }
            if (chars + size > 8 * 1024 * 1024) { truncated = true; break; }
            bounded.Add(e.Clone()); chars += size;
        }
        var snapshot = new Snapshot(Guid.NewGuid().ToString("N"), document, revision,
            DateTimeOffset.UtcNow, bounded, truncated, scope, limit);
        if (snapshots.Count >= 4) snapshots.Remove(snapshots.Values.OrderBy(s => s.Created).First().Id);
        snapshots.Add(snapshot.Id, snapshot);
        return snapshot;
    }
    public object Query(string id, string document, long revision, JsonElement filter)
    {
        if (!snapshots.TryGetValue(id, out var s) || s.DocumentId != document)
            throw new CadFault("SNAPSHOT_NOT_FOUND", "Create a new snapshot for this document");
        int offset = filter.Number("offset", 0), limit = filter.Number("limit", 100);
        if (offset < 0 || limit < 1 || limit > 500) throw new CadFault("INVALID_PAGE", "offset >= 0; limit 1..500");
        var layer = filter.Text("layer"); var type = filter.Text("type"); var text = filter.Text("text");
        var filtered = s.Entities.Where(e =>
            (layer is null || string.Equals(e.Text("layer"), layer, StringComparison.OrdinalIgnoreCase)) &&
            (type is null || string.Equals(e.Text("type"), type, StringComparison.OrdinalIgnoreCase)) &&
            (text is null || (e.Text("text")?.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0))).ToArray();
        var pageList = new List<JsonElement>(); int chars = 0;
        foreach (var e in filtered.Skip(offset).Take(limit))
        {
            int size = e.GetRawText().Length;
            if (chars + size > 512 * 1024 && pageList.Count > 0) break;
            chars += size; pageList.Add(e);
        }
        var page = pageList.ToArray();
        return new { snapshot_id = s.Id, captured_revision = s.Revision, historical = s.Revision != revision,
            entities = page, matching_in_snapshot = filtered.Length,
            pagination = new { next_offset = offset + page.Length < filtered.Length ? (int?)(offset + page.Length) : null, snapshot_truncated = s.Truncated } };
    }
}
