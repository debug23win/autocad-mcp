using CadMcp.Core;
using System.Text;
using System.Text.Json;

namespace CadMcp.Providers;

/// <summary>Keep commentary out of the transcript. A completed item supplies canonical text, not another delta.</summary>
internal sealed class CodexAnswerBuffer
{
    private sealed class Message
    {
        public string? Phase;
        public StringBuilder Text = new();
    }
    private readonly Dictionary<string, Message> messages = new(StringComparer.Ordinal);
    private readonly List<string> order = [];
    private string latest = "legacy";
    private Message Get(string id)
    {
        if (!messages.TryGetValue(id, out var message)) { message = new(); messages.Add(id, message); order.Add(id); }
        return message;
    }
    public void Item(JsonElement item, bool completed)
    {
        if (ProviderJson.Text(item, "type") != "agentMessage" || ProviderJson.Text(item, "id") is not { } id) return;
        latest = id;
        var message = Get(id);
        message.Phase = ProviderJson.Text(item, "phase") ?? message.Phase;
        if (completed && ProviderJson.Text(item, "text") is { } text) { message.Text.Clear(); message.Text.Append(text); }
    }
    public void Delta(JsonElement parameters) =>
        Get(ProviderJson.Text(parameters, "itemId") ?? latest).Text.Append(ProviderJson.Text(parameters, "delta"));

    public string Finish(JsonElement turn)
    {
        if (ProviderJson.Child(turn, "items") is { ValueKind: JsonValueKind.Array } items)
            foreach (var item in items.EnumerateArray()) Item(item, true);
        var nonempty = order.Select(id => messages[id]).Where(m => m.Text.Length > 0).ToArray();
        // Older servers do not expose phase. Only their last message is treated as the answer.
        return (nonempty.LastOrDefault(m => m.Phase == "final_answer") ?? nonempty.LastOrDefault(m => m.Phase is null))?.Text.ToString()
            ?? UiText.T("Codex завершил задачу без итогового ответа.");
    }
}
