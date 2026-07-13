using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadTransportRuntimeTests
{
    [Fact]
    public void ReadinessEdgeBypassesHeartbeatDeadlineAndCoalescesLatestRevision()
    {
        var now = DateTime.UtcNow;
        var coalescer = new DadReadinessHeartbeatCoalescer();

        Assert.False(coalescer.TryCapture(now, now.AddSeconds(5), out _));
        coalescer.MarkPending(1);
        coalescer.MarkPending(2);

        Assert.True(coalescer.TryCapture(now, now.AddSeconds(5), out var ticket));
        Assert.True(ticket.HasReadinessEdge);
        Assert.Equal(2, ticket.Revision);
        coalescer.Acknowledge(ticket);
        Assert.False(coalescer.HasPending);
        Assert.False(coalescer.TryCapture(now, now.AddSeconds(5), out _));
    }

    [Fact]
    public void ReadinessHeartbeatAcknowledgeDoesNotLoseNewerEdge()
    {
        var now = DateTime.UtcNow;
        var coalescer = new DadReadinessHeartbeatCoalescer();
        coalescer.MarkPending(3);
        Assert.True(coalescer.TryCapture(now, now.AddSeconds(5), out var sending));

        coalescer.MarkPending(4);
        coalescer.Acknowledge(sending);

        Assert.True(coalescer.HasPending);
        Assert.True(coalescer.TryCapture(now, now.AddSeconds(5), out var final));
        Assert.Equal(4, final.Revision);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("outbound")]
    public async Task DeferredSemaphoreShutdownWaitsForHeldAndQueuedLeases(string leaseKind)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var semaphore = new DadDeferredDisposalSemaphore(1, 1);
        var held = await semaphore.TryAcquireAsync(cancellation.Token);
        Assert.NotNull(held);

        var queuedTask = semaphore.TryAcquireAsync(cancellation.Token).AsTask();
        Assert.True(SpinWait.SpinUntil(() => semaphore.LifetimeLeaseCount == 2, TimeSpan.FromSeconds(1)));

        semaphore.Dispose();

        Assert.True(semaphore.ShutdownRequested);
        Assert.False(semaphore.IsPhysicallyDisposed);
        Assert.Null(await semaphore.TryAcquireAsync(cancellation.Token));

        var releaseException = Record.Exception(held!.Dispose);
        Assert.Null(releaseException);
        var queued = await queuedTask;
        Assert.NotNull(queued);
        Assert.False(semaphore.IsPhysicallyDisposed);

        releaseException = Record.Exception(queued!.Dispose);
        Assert.Null(releaseException);
        Assert.True(semaphore.IsPhysicallyDisposed);
        Assert.Equal(0, semaphore.LifetimeLeaseCount);
        Assert.False(string.IsNullOrWhiteSpace(leaseKind));
    }

    [Fact]
    public async Task CancelledSemaphoreWaiterStillAllowsFinalDeferredDisposal()
    {
        var semaphore = new DadDeferredDisposalSemaphore(1, 1);
        var held = await semaphore.TryAcquireAsync(CancellationToken.None);
        using var waiterCancellation = new CancellationTokenSource();
        var waiter = semaphore.TryAcquireAsync(waiterCancellation.Token).AsTask();
        Assert.True(SpinWait.SpinUntil(() => semaphore.LifetimeLeaseCount == 2, TimeSpan.FromSeconds(1)));

        semaphore.Dispose();
        waiterCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(semaphore.IsPhysicallyDisposed);
        held!.Dispose();
        Assert.True(semaphore.IsPhysicallyDisposed);
    }

    [Fact]
    public void ReconnectBackoffCapsButNeverRunsOutOfAttempts()
    {
        var delays = Enumerable.Range(1, 12)
            .Select(attempt => DadReconnectPolicy.GetBackoff(attempt, TimeSpan.FromSeconds(10)))
            .Select(static delay => delay.TotalSeconds)
            .ToList();

        Assert.Equal([1, 2, 4, 8, 10, 10, 10, 10, 10, 10, 10, 10], delays);
        Assert.True(DadReconnectPolicy.ShouldContinue(pluginEnabled: true, roleCancellationRequested: false));
        Assert.False(DadReconnectPolicy.ShouldContinue(pluginEnabled: false, roleCancellationRequested: false));
        Assert.False(DadReconnectPolicy.ShouldContinue(pluginEnabled: true, roleCancellationRequested: true));
    }

    [Fact]
    public void InboundLivenessExpiresAtExistingStaleThreshold()
    {
        var lastInbound = new DateTime(2026, 7, 11, 20, 0, 0, DateTimeKind.Utc);

        Assert.False(DadReconnectPolicy.IsInboundStale(lastInbound, lastInbound.AddSeconds(14), TimeSpan.FromSeconds(15)));
        Assert.True(DadReconnectPolicy.IsInboundStale(lastInbound, lastInbound.AddSeconds(15), TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void InboundRequestGateRejectsOverflowWithoutBlockingReader()
    {
        var gate = new DadInboundRequestGate(2);

        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        Assert.Equal(2, gate.Active);

        gate.Exit();
        Assert.True(gate.TryEnter());
        Assert.Equal(2, gate.Active);
    }

    [Fact]
    public void PhysicalConnectionIsNotRoutableBeforeHelloAck()
    {
        var handshake = new DadHubHandshakeState(DadHubHandshakeRole.Client);

        Assert.False(DadHubTransportRouting.IsRoutable(socketOpen: true, handshake));

        handshake.MarkReadyAfterHelloAck();

        Assert.True(DadHubTransportRouting.IsRoutable(socketOpen: true, handshake));
        Assert.False(DadHubTransportRouting.IsRoutable(socketOpen: false, handshake));
    }

    [Fact]
    public void ServerCannotSendApplicationFrameBeforeHelloAck()
    {
        var handshake = new DadHubHandshakeState(DadHubHandshakeRole.Server);

        Assert.True(handshake.CanSend(DadHubFrameKind.HelloAck));
        Assert.False(handshake.CanSend(DadHubFrameKind.Request));
        Assert.False(handshake.CanSend(DadHubFrameKind.Heartbeat));
        Assert.False(handshake.CanSend(DadHubFrameKind.Notification));

        handshake.MarkReadyAfterHelloAck();

        Assert.True(handshake.CanSend(DadHubFrameKind.Request));
        Assert.True(handshake.CanSend(DadHubFrameKind.Heartbeat));
        Assert.True(handshake.CanSend(DadHubFrameKind.Notification));
    }

    [Theory]
    [InlineData("roster-catalog-request")]
    [InlineData("profile-catalog-request")]
    [InlineData("wake-takeover-request")]
    public void EagerOperationsCannotOvertakeHandshake(string messageType)
    {
        var handshake = new DadHubHandshakeState(DadHubHandshakeRole.Server);
        var target = new dad.Models.DadWorkerSessionId("client-x");

        Assert.False(DadHubTransportRouting.CanQueue(
            target,
            DadHubTransportRouting.IsRoutable(socketOpen: true, handshake)));
        Assert.False(handshake.CanSend(DadHubFrameKind.Request));
        Assert.False(string.IsNullOrWhiteSpace(messageType));
    }

    [Fact]
    public void AuthorityPollingDoesNotQueueWhileDisconnected()
    {
        var target = new dad.Models.DadWorkerSessionId("coordinator-w");

        Assert.False(DadHubTransportRouting.CanQueue(target, connectionRoutable: false));
        Assert.False(DadHubTransportRouting.CanQueue(new dad.Models.DadWorkerSessionId(string.Empty), connectionRoutable: true));
    }

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
