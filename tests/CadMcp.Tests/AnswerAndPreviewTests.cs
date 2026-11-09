using CadMcp.Core;
using CadMcp.Providers;
using System.Text.Json;

namespace CadMcp.Tests;

public sealed class AnswerAndPreviewTests
{
    [Fact]
    public void Copy_uses_the_exact_answer_without_rendered_metadata_or_another_message()
    {
        const string markdown = "Ответ ✓\n\n```csharp\nvar x = 1;\n```\n$A_1$";
        ChatLine[] lines = [new("user", "question"), new("assistant", markdown, "model", "high"), new("assistant", "")];
        Assert.Equal(markdown, ChatMarkup.AnswerText(lines, 1));
        foreach (int index in new[] { -1, 0, 2, 3 }) Assert.Null(ChatMarkup.AnswerText(lines, index));
        using var folder = new TempFolder();
        string html = ChatMarkup.ConversationHtml(lines, folder.Path);
        Assert.Contains("class='copy-answer' data-index='1'", html);
        Assert.DoesNotContain("class='copy-answer' data-index='0'", html);
    }

    [Fact]
    public void Automatic_preview_excludes_definition_geometry_and_never_mixes_spaces()
    {
        JsonElement[] entities = [
            Wire.Element(new { handle = "inside", space = "block_definition", owner_handle = "DEF" }),
            Wire.Element(new { handle = "paper", space = "paper", owner_handle = "PS" }),
            Wire.Element(new { handle = "block", space = "model", owner_handle = "MS" }),
            Wire.Element(new { handle = "gone", space = "model", owner_handle = "MS", erased = true })];
        Assert.Equal(new[] { "block" }, CadResultSummary.PreviewHandles(entities, "MS"));
        Assert.Equal(new[] { "paper" }, CadResultSummary.PreviewHandles(entities, "PS"));
        Assert.Empty(CadResultSummary.PreviewHandles(entities.Take(1), "MS"));
    }
}
