using System.Text.Json;
using CadMcp.Providers;

namespace CadMcp.Tests;

public sealed class CodexAnswerBufferTests
{
    private static JsonElement Item(string id, string? phase, string text) => JsonSerializer.SerializeToElement(new { type = "agentMessage", id, phase, text });
    [Fact]
    public void Completed_text_replaces_deltas_and_only_final_answer_is_shown()
    {
        var buffer = new CodexAnswerBuffer();
        buffer.Item(Item("comment", "commentary", "План"), true);
        buffer.Item(Item("answer", "final_answer", ""), false);
        buffer.Delta(JsonSerializer.SerializeToElement(new { itemId = "answer", delta = "Итог" }));
        buffer.Item(Item("answer", "final_answer", "Итог ✓"), true);
        Assert.Equal("Итог ✓", buffer.Finish(default));
    }

    [Fact]
    public void Legacy_phase_uses_last_message_and_commentary_is_never_a_final_answer()
    {
        var buffer = new CodexAnswerBuffer();
        buffer.Item(Item("first", null, "Промежуточно"), true);
        buffer.Item(Item("last", null, "Готово"), true);
        Assert.Equal("Готово", buffer.Finish(default));
        var commentary = new CodexAnswerBuffer(); commentary.Item(Item("only", "commentary", "Проверю"), true);
        Assert.DoesNotContain("Проверю", commentary.Finish(default));
    }

    [Fact]
    public void Final_turn_snapshot_supplies_answer_when_deltas_are_missing()
    {
        var buffer = new CodexAnswerBuffer();
        Assert.Equal("Ответ", buffer.Finish(JsonSerializer.SerializeToElement(new { items = new[] { Item("comment", "commentary", "Не показывать"), Item("final", "final_answer", "Ответ") } })));
    }

    [Fact]
    public async Task Provider_hides_commentary_and_helper_dialogue_until_turn_completes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = await TestEnvironment.Collect(new CodexProvider(TestEnvironment.ProviderOptions).SendAsync("FINAL_ONLY", timeout.Token));
        Assert.Equal("Итог ✓", Assert.Single(events, e => e.Kind == "text").Text);
        Assert.Equal("completed", events[^1].Kind);
        Assert.DoesNotContain(events, e => e.Kind == "text" && (e.Text.Contains("План") || e.Text.Contains("Помощник")));
    }
}
