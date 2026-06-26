using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadTransportRuntimeTests
{
    [Fact]
    public void HeartbeatDirtyMarksCoalesceIntoOnePublishPerThrottleWindow()
    {
        var now = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);
        var coalescer = new DadRosterPublishCoalescer(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(250));

        for (var index = 0; index < 50; index++)
            coalescer.MarkDirty($"heartbeat {index}", fast: false, now);

        Assert.False(coalescer.TryFlush(now.AddMilliseconds(999), out _));
        Assert.True(coalescer.TryFlush(now.AddSeconds(1), out var reason));
        Assert.Equal("heartbeat 49", reason);
        Assert.Equal(49, coalescer.CoalescedCount);
        Assert.False(coalescer.TryFlush(now.AddSeconds(2), out _));
    }

    [Fact]
    public void FastDirtyMarkFlushesBeforeNormalThrottleWindow()
    {
        var now = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);
        var coalescer = new DadRosterPublishCoalescer(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(250));

        coalescer.MarkDirty("client connected", fast: true, now);

        Assert.False(coalescer.TryFlush(now.AddMilliseconds(249), out _));
        Assert.True(coalescer.TryFlush(now.AddMilliseconds(250), out var reason));
        Assert.Equal("client connected", reason);
    }

    [Fact]
    public void BoundedFrameworkEventQueueDrainsAtMostRequestedCount()
    {
        var queue = new DadBoundedFrameworkEventQueue(maxBacklog: 10);
        var drained = 0;
        for (var index = 0; index < 6; index++)
            Assert.True(queue.Enqueue(() => drained++));

        Assert.Equal(2, queue.Drain(2));
        Assert.Equal(2, drained);
        Assert.Equal(4, queue.Count);

        Assert.Equal(4, queue.Drain(10));
        Assert.Equal(6, drained);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void BoundedFrameworkEventQueueDropsExcessBacklog()
    {
        var queue = new DadBoundedFrameworkEventQueue(maxBacklog: 2);

        Assert.True(queue.Enqueue(() => { }));
        Assert.True(queue.Enqueue(() => { }));
        Assert.False(queue.Enqueue(() => { }));

        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.DroppedCount);
    }
}
