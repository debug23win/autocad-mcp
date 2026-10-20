using CadMcp.Providers;
using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class ChatPatchTests
{
    [Fact]
    public void Streaming_patches_only_the_changed_reply_and_theme_selection_send_no_history()
    {
        using var folder = new TempFolder(); var builder = new ChatPatchBuilder();
        ChatLine[] lines = [new("user", "Размеры"), new("assistant", "Считаю")];
        Assert.Equal(2, builder.Build(lines, folder.Path, true, -1)!.Patches.Count);
        lines[1] = lines[1] with { Text = "Считаю $L=5$" };
        var delta = builder.Build(lines, folder.Path, true, -1)!;
        Assert.Equal(1, Assert.Single(delta.Patches).Index);
        Assert.Contains("L=5", delta.Patches[0].Html);
        Assert.Null(builder.Build(lines, folder.Path, true, -1));
        Assert.Empty(builder.Build(lines, folder.Path, false, 1)!.Patches);
        Assert.Equal(0, builder.Build([], folder.Path, false, -1)!.Count);
        Assert.Equal(2, builder.Build(lines, folder.Path, false, -1)!.Patches.Count);
    }

    [Fact]
    public void Preview_cache_expires_and_never_crosses_document_revision_or_view()
    {
        var clock = new Clock(); var cache = new RevisionCache<string>(2, TimeSpan.FromSeconds(2), clock);
        cache.Put("A", "top", 1, "png");
        Assert.True(cache.TryGet("A", "top", 1, out _));
        Assert.False(cache.TryGet("B", "top", 1, out _));
        Assert.False(cache.TryGet("A", "isometric", 1, out _));
        Assert.False(cache.TryGet("A", "top", 2, out _));
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.False(cache.TryGet("A", "top", 1, out _));
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
