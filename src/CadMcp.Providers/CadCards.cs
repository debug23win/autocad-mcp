using System.Text.Json;

namespace CadMcp.Providers;

public sealed record CadCard(string Session, string Document, string Handle, string Type, bool Erased, string? RootHandle = null)
{
    public string Label => (Erased ? "Удалён: " : "") + Type + " · " + Handle;
    public string FocusHandle => RootHandle ?? Handle;
}
public static class CadCards
{
    public static IReadOnlyList<CadCard> Parse(string json)
    {
        var cards = new List<CadCard>();
        void Visit(JsonElement value, string session, string document, int depth)
        {
            if (depth > 16 || cards.Count >= 200) return;
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString() ?? "";
                if (text.Length > 1024 * 1024 || !text.TrimStart().StartsWith("{")) return;
                try { using var parsed = JsonDocument.Parse(text); Visit(parsed.RootElement, session, document, depth + 1); } catch (JsonException) { }
            }
            if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Visit(item, session, document, depth + 1);
            if (value.ValueKind != JsonValueKind.Object) return;
            if (value.TryGetProperty("session_id", out var s) && s.ValueKind == JsonValueKind.String) session = s.GetString()!;
            if (value.TryGetProperty("document_id", out var d) && d.ValueKind == JsonValueKind.String) document = d.GetString()!;
            if (session.Length != 0 && document.Length != 0 && value.TryGetProperty("handle", out var handle) && handle.ValueKind == JsonValueKind.String)
                cards.Add(new(session, document, handle.GetString()!, value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "Объект",
                    value.TryGetProperty("erased", out var e) && e.ValueKind == JsonValueKind.True,
                    value.TryGetProperty("root_handle", out var root) && root.ValueKind == JsonValueKind.String ? root.GetString() : null));
            foreach (var property in value.EnumerateObject()) Visit(property.Value, session, document, depth + 1);
        }
        try { using var parsed = JsonDocument.Parse(json); Visit(parsed.RootElement, "", "", 0); } catch (JsonException) { }
        return cards.Distinct().ToArray();
    }
}
