using CadMcp.Core;
namespace CadMcp.Tests;
public sealed class DrawingTabsTests
{
    [Fact]
    public void Reopening_the_same_file_reuses_one_slot_and_new_document_identity()
    {
        var tabs = new DrawingTabs();
        var first = Assert.Single(tabs.Refresh([new("old", "path-key", "A.dwg")]));
        Assert.False(Assert.Single(tabs.Refresh([])).Open);
        var reopened = Assert.Single(tabs.Refresh([new("new", "path-key", "A.dwg")]));
        Assert.Equal(first.Key, reopened.Key); Assert.Equal("new", reopened.Document.DocumentId); Assert.True(reopened.Open);
    }
    [Fact]
    public void Closing_a_chat_stays_hidden_until_explicit_refresh_or_new_document_incarnation()
    {
        var tabs = new DrawingTabs(); var live = new DrawingTabDocument("one", "path", "A.dwg");
        tabs.Refresh([live]); tabs.Close("one"); Assert.Empty(tabs.Refresh([live]));
        Assert.Single(tabs.Refresh([live], revealHidden: true)); tabs.Close("one");
        Assert.Single(tabs.Refresh([live with { DocumentId = "two" }]));
    }
    [Fact]
    public void Save_as_updates_the_project_key_and_equal_basenames_do_not_merge()
    {
        var tabs = new DrawingTabs();
        tabs.Refresh([new("one", "dir1", "A.dwg"), new("two", "dir2", "A.dwg")]);
        Assert.Equal(2, tabs.Refresh([new("one", "saved-as", "B.dwg"), new("two", "dir2", "A.dwg")]).Count);
        var result = tabs.Refresh([new("three", "saved-as", "B.dwg"), new("two", "dir2", "A.dwg")]);
        Assert.Equal(2, result.Count); Assert.Equal("three", result.Single(s => s.Key == "one").Document.DocumentId);
    }
}
