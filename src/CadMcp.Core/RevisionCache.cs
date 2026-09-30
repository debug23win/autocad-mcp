namespace CadMcp.Core;

public sealed class RevisionCache<T>(int capacity = 4)
{
    private sealed record Entry(string Document, string Scope, long Revision, T Value);
    private readonly List<Entry> entries = [];
    public int Hits { get; private set; }
    public int Misses { get; private set; }
    public bool TryGet(string document, string scope, long revision, out T value)
    {
        var entry = entries.LastOrDefault(e => e.Document == document && e.Scope == scope && e.Revision == revision);
        if (entry is not null) { Hits++; value = entry.Value; return true; }
        Misses++; value = default!; return false;
    }
    public void Put(string document, string scope, long revision, T value)
    {
        entries.RemoveAll(e => e.Document == document && e.Scope == scope);
        if (entries.Count >= capacity) entries.RemoveAt(0);
        entries.Add(new(document, scope, revision, value));
    }
}
