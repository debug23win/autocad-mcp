namespace CadMcp.Core;

/// <summary>FIFO within each drawing; a blocked drawing never holds up ready work in another drawing.</summary>
public sealed class DocumentWorkQueue<T>
{
    private readonly List<T> items = [];
    private readonly object gate = new();
    public int Count { get { lock (gate) return items.Count; } }
    public bool IsEmpty => Count == 0;
    public void Enqueue(T item) { lock (gate) items.Add(item); }
    public bool TryPeek(out T item) { lock (gate) { item = items.Count == 0 ? default! : items[0]; return items.Count > 0; } }
    public bool TryDequeue(out T item) => TryTake(_ => "", _ => true, _ => false, out item);
    public bool TryTake(Func<T, string> document, Func<T, bool> ready, Func<T, bool> expired, out T item)
    {
        lock (gate)
        {
            var blocked = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                var candidate = items[i]; string key = document(candidate);
                if (expired(candidate) || !blocked.Contains(key) && ready(candidate))
                { item = candidate; items.RemoveAt(i); return true; }
                blocked.Add(key);
            }
            item = default!; return false;
        }
    }
}

public sealed record DrawingAvailability(string DocumentId, string Name, bool ReadAvailable, bool WriteAvailable,
    string CommandInProgress, string? Reason, DateTimeOffset ObservedAt);
