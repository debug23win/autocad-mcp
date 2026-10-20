namespace CadMcp.Providers;

public sealed record ChatPatch(int Index, string Html);
public sealed record ChatUpdate(int Count, IReadOnlyList<ChatPatch> Patches, bool Dark, int Selected, long Sequence);

/// <summary>Only changed immutable messages cross the browser boundary; completed replies are not re-parsed.</summary>
public sealed class ChatPatchBuilder
{
    private ChatLine[] previous = [];
    private bool? dark;
    private int selected = -1;
    private long sequence;
    public void Reset() { previous = []; dark = null; selected = -1; }
    public ChatUpdate? Build(IReadOnlyList<ChatLine> messages, string assets, bool isDark, int selectedIndex)
    {
        var patches = new List<ChatPatch>();
        for (int i = 0; i < messages.Count; i++)
            if (i >= previous.Length || messages[i] != previous[i]) patches.Add(new(i, ChatMarkup.MessageHtml(messages[i], i, assets)));
        if (patches.Count == 0 && previous.Length == messages.Count && dark == isDark && selected == selectedIndex) return null;
        previous = messages.ToArray(); dark = isDark; selected = selectedIndex;
        return new(messages.Count, patches, isDark, selectedIndex, ++sequence);
    }
}
