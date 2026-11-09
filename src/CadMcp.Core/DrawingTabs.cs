namespace CadMcp.Core;

public sealed record DrawingTabDocument(string DocumentId, string ProjectKey, string Name, bool Active = false);
public sealed record DrawingTabSlot(string Key, DrawingTabDocument Document, bool Open);

/// <summary>Stable chat slots across document incarnations. Closing a chat hides only that live document.</summary>
public sealed class DrawingTabs
{
    private readonly List<DrawingTabSlot> slots = [];
    private readonly HashSet<string> hidden = new(StringComparer.Ordinal);
    public IReadOnlyList<DrawingTabSlot> Slots => slots.ToArray();
    public IReadOnlyList<DrawingTabSlot> Refresh(IEnumerable<DrawingTabDocument> documents, bool revealHidden = false)
    {
        var live = documents.ToArray();
        var ids = live.Select(d => d.DocumentId).ToHashSet(StringComparer.Ordinal);
        if (revealHidden) hidden.Clear(); else hidden.IntersectWith(ids);
        for (int i = 0; i < slots.Count; i++) slots[i] = slots[i] with { Open = ids.Contains(slots[i].Document.DocumentId) };
        foreach (var document in live)
        {
            if (hidden.Contains(document.DocumentId)) continue;
            int at = slots.FindIndex(s => s.Document.DocumentId == document.DocumentId);
            if (at < 0) at = slots.FindIndex(s => !s.Open && s.Document.ProjectKey == document.ProjectKey);
            if (at < 0) slots.Add(new(document.DocumentId, document, true));
            else slots[at] = slots[at] with { Document = document, Open = true };
        }
        return Slots;
    }
    public void Close(string key)
    {
        int at = slots.FindIndex(s => s.Key == key);
        if (at < 0) return;
        if (slots[at].Open) hidden.Add(slots[at].Document.DocumentId);
        slots.RemoveAt(at);
    }
}
