using CadMcp.Core;
namespace CadMcp.Tests;
public sealed class DocumentWorkQueueTests
{
    private sealed record Job(string Id, string Document, bool Ready = true, bool Expired = false);
    [Fact]
    public void Busy_drawing_cannot_hold_up_other_drawings_and_fifo_is_preserved()
    {
        var queue = new DocumentWorkQueue<Job>();
        queue.Enqueue(new("A1", "A", false)); queue.Enqueue(new("A2", "A")); queue.Enqueue(new("B1", "B")); queue.Enqueue(new("C1", "C")); queue.Enqueue(new("B2", "B"));
        bool Take(out Job job) => queue.TryTake(j => j.Document, j => j.Ready, j => j.Expired, out job);
        Assert.True(Take(out var first)); Assert.Equal("B1", first.Id);
        Assert.True(Take(out var second)); Assert.Equal("C1", second.Id);
        Assert.True(Take(out var third)); Assert.Equal("B2", third.Id);
        Assert.False(Take(out _)); Assert.Equal(2, queue.Count);
        Assert.True(queue.TryDequeue(out var blocked)); Assert.Equal("A1", blocked.Id);
        Assert.True(Take(out var resumed)); Assert.Equal("A2", resumed.Id); Assert.True(queue.IsEmpty);
    }
    [Fact]
    public void Expired_requests_are_settled_even_when_their_drawing_is_busy()
    {
        var queue = new DocumentWorkQueue<Job>(); queue.Enqueue(new("blocked", "A", false)); queue.Enqueue(new("expired", "A", false, true));
        Assert.True(queue.TryTake(j => j.Document, j => j.Ready, j => j.Expired, out var expired)); Assert.Equal("expired", expired.Id);
        Assert.Equal(1, queue.Count); Assert.True(queue.TryPeek(out var first)); Assert.Equal("blocked", first.Id);
    }
}
