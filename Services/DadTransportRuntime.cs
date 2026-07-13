using System.Collections.Concurrent;
using dad.Models;

namespace dad.Services;

internal enum DadHubHandshakeRole
{
    Server = 0,
    Client = 1,
}

internal sealed class DadHubHandshakeState
{
    private readonly DadHubHandshakeRole role;
    private int ready;

    public DadHubHandshakeState(DadHubHandshakeRole role)
    {
        this.role = role;
    }

    public bool IsReady => Volatile.Read(ref ready) != 0;

    public bool CanSend(DadHubFrameKind kind)
    {
        if (IsReady)
            return kind is not DadHubFrameKind.Hello and not DadHubFrameKind.HelloAck;

        return kind == DadHubFrameKind.Error ||
               role == DadHubHandshakeRole.Server && kind == DadHubFrameKind.HelloAck ||
               role == DadHubHandshakeRole.Client && kind == DadHubFrameKind.Hello;
    }

    public void MarkReadyAfterHelloAck()
        => Interlocked.Exchange(ref ready, 1);
}

internal static class DadHubTransportRouting
{
    public static bool IsRoutable(bool socketOpen, DadHubHandshakeState handshake)
        => socketOpen && handshake.IsReady;

    public static bool CanQueue(DadWorkerSessionId targetWorkerSessionId, bool connectionRoutable)
        => !targetWorkerSessionId.IsEmpty && connectionRoutable;
}

internal static class DadReconnectPolicy
{
    public static TimeSpan GetBackoff(int attempt, TimeSpan maximum)
    {
        var normalizedAttempt = Math.Max(1, attempt);
        var seconds = Math.Pow(2, Math.Min(normalizedAttempt - 1, 4));
        return TimeSpan.FromSeconds(Math.Min(Math.Max(1, maximum.TotalSeconds), seconds));
    }

    public static bool ShouldContinue(bool pluginEnabled, bool roleCancellationRequested)
        => pluginEnabled && !roleCancellationRequested;

    public static bool IsInboundStale(DateTime lastInboundUtc, DateTime nowUtc, TimeSpan staleAfter)
        => nowUtc - lastInboundUtc >= staleAfter;
}

internal readonly record struct DadReadinessHeartbeatTicket(bool HasReadinessEdge, long Revision);

internal sealed class DadReadinessHeartbeatCoalescer
{
    private readonly object gate = new();
    private bool pending;
    private long pendingRevision;

    public bool HasPending
    {
        get
        {
            lock (gate)
                return pending;
        }
    }

    public void MarkPending(long revision)
    {
        lock (gate)
        {
            pending = true;
            pendingRevision = Math.Max(pendingRevision, revision);
        }
    }

    public bool TryCapture(
        DateTime nowUtc,
        DateTime nextPeriodicHeartbeatUtc,
        out DadReadinessHeartbeatTicket ticket)
    {
        lock (gate)
        {
            ticket = new DadReadinessHeartbeatTicket(pending, pendingRevision);
            return pending || nowUtc >= nextPeriodicHeartbeatUtc;
        }
    }

    public void Acknowledge(DadReadinessHeartbeatTicket ticket)
    {
        if (!ticket.HasReadinessEdge)
            return;

        lock (gate)
        {
            // Preserve a newer edge that arrived after the sender captured its snapshot.
            if (pending && pendingRevision <= ticket.Revision)
                pending = false;
        }
    }
}

internal sealed class DadInboundRequestGate
{
    private readonly int maximum;
    private int active;

    public DadInboundRequestGate(int maximum)
    {
        this.maximum = Math.Max(1, maximum);
    }

    public int Active => Volatile.Read(ref active);

    public bool TryEnter()
    {
        var count = Interlocked.Increment(ref active);
        if (count <= maximum)
            return true;
        Interlocked.Decrement(ref active);
        return false;
    }

    public void Exit()
        => Interlocked.Decrement(ref active);
}

internal sealed class DadDeferredDisposalSemaphore : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim semaphore;
    private int lifetimeLeaseCount;
    private bool shutdownRequested;
    private bool physicallyDisposed;

    public DadDeferredDisposalSemaphore(int initialCount, int maximumCount)
    {
        semaphore = new SemaphoreSlim(initialCount, maximumCount);
    }

    internal int LifetimeLeaseCount
    {
        get
        {
            lock (gate)
                return lifetimeLeaseCount;
        }
    }

    internal bool ShutdownRequested
    {
        get
        {
            lock (gate)
                return shutdownRequested;
        }
    }

    internal bool IsPhysicallyDisposed
    {
        get
        {
            lock (gate)
                return physicallyDisposed;
        }
    }

    public async ValueTask<DadDeferredDisposalSemaphoreLease?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (shutdownRequested)
                return null;

            // Count the operation before it waits. Shutdown can then reject new work while
            // leaving the physical semaphore alive for every waiter and holder to unwind.
            lifetimeLeaseCount++;
        }

        try
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new DadDeferredDisposalSemaphoreLease(this);
        }
        catch
        {
            ReleaseLifetimeLease(releaseSemaphore: false);
            throw;
        }
    }

    public void Dispose()
    {
        SemaphoreSlim? disposeNow = null;
        lock (gate)
        {
            if (shutdownRequested)
                return;

            shutdownRequested = true;
            if (lifetimeLeaseCount == 0)
            {
                physicallyDisposed = true;
                disposeNow = semaphore;
            }
        }

        disposeNow?.Dispose();
    }

    internal void ReleaseLease()
        => ReleaseLifetimeLease(releaseSemaphore: true);

    private void ReleaseLifetimeLease(bool releaseSemaphore)
    {
        // The current lifetime lease keeps the semaphore alive through Release().
        if (releaseSemaphore)
            semaphore.Release();

        SemaphoreSlim? disposeNow = null;
        lock (gate)
        {
            lifetimeLeaseCount--;
            if (lifetimeLeaseCount < 0)
                throw new InvalidOperationException("Dad semaphore lifetime lease count became negative.");

            if (shutdownRequested && lifetimeLeaseCount == 0 && !physicallyDisposed)
            {
                physicallyDisposed = true;
                disposeNow = semaphore;
            }
        }

        disposeNow?.Dispose();
    }
}

internal sealed class DadDeferredDisposalSemaphoreLease : IDisposable
{
    private DadDeferredDisposalSemaphore? owner;

    internal DadDeferredDisposalSemaphoreLease(DadDeferredDisposalSemaphore owner)
    {
        this.owner = owner;
    }

    public void Dispose()
        => Interlocked.Exchange(ref owner, null)?.ReleaseLease();
}

internal sealed class DadRosterPublishCoalescer
{
    public static readonly TimeSpan DefaultThrottleWindow = TimeSpan.FromMilliseconds(1000);
    public static readonly TimeSpan DefaultFastWindow = TimeSpan.FromMilliseconds(250);

    private readonly object gate = new();
    private readonly TimeSpan throttleWindow;
    private readonly TimeSpan fastWindow;
    private bool dirty;
    private string pendingReason = string.Empty;
    private DateTime nextPublishUtc = DateTime.MinValue;
    private DateTime lastPublishUtc = DateTime.MinValue;
    private long coalescedCount;

    public DadRosterPublishCoalescer()
        : this(DefaultThrottleWindow, DefaultFastWindow)
    {
    }

    public DadRosterPublishCoalescer(TimeSpan throttleWindow, TimeSpan fastWindow)
    {
        this.throttleWindow = throttleWindow;
        this.fastWindow = fastWindow;
    }

    public long CoalescedCount
    {
        get
        {
            lock (gate)
                return coalescedCount;
        }
    }

    public string LastPublishReason { get; private set; } = string.Empty;

    public DateTime? LastPublishUtc
    {
        get
        {
            lock (gate)
                return lastPublishUtc == DateTime.MinValue ? null : lastPublishUtc;
        }
    }

    public bool IsDirty
    {
        get
        {
            lock (gate)
                return dirty;
        }
    }

    public DateTime NextPublishUtc
    {
        get
        {
            lock (gate)
                return nextPublishUtc;
        }
    }

    public void MarkDirty(string reason, bool fast, DateTime nowUtc)
    {
        lock (gate)
        {
            if (dirty)
                coalescedCount++;

            dirty = true;
            if (!string.IsNullOrWhiteSpace(reason))
                pendingReason = reason.Trim();

            var delay = fast ? fastWindow : throttleWindow;
            var candidate = lastPublishUtc == DateTime.MinValue
                ? nowUtc + delay
                : lastPublishUtc + delay;

            if (nextPublishUtc == DateTime.MinValue || candidate < nextPublishUtc)
                nextPublishUtc = candidate;
        }
    }

    public bool TryFlush(DateTime nowUtc, out string reason)
    {
        lock (gate)
        {
            reason = string.Empty;
            if (!dirty || nowUtc < nextPublishUtc)
                return false;

            reason = string.IsNullOrWhiteSpace(pendingReason)
                ? "Dad Coordinator roster changed."
                : pendingReason;
            dirty = false;
            pendingReason = string.Empty;
            nextPublishUtc = DateTime.MinValue;
            lastPublishUtc = nowUtc;
            LastPublishReason = reason;
            return true;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            dirty = false;
            pendingReason = string.Empty;
            nextPublishUtc = DateTime.MinValue;
            lastPublishUtc = DateTime.MinValue;
            coalescedCount = 0;
            LastPublishReason = string.Empty;
        }
    }
}

internal sealed class DadBoundedFrameworkEventQueue
{
    private readonly ConcurrentQueue<Action> queue = new();
    private readonly int maxBacklog;
    private long droppedCount;

    public DadBoundedFrameworkEventQueue(int maxBacklog)
    {
        this.maxBacklog = Math.Max(1, maxBacklog);
    }

    public int Count => queue.Count;

    public long DroppedCount => Interlocked.Read(ref droppedCount);

    public bool Enqueue(Action action)
    {
        if (queue.Count >= maxBacklog)
        {
            Interlocked.Increment(ref droppedCount);
            return false;
        }

        queue.Enqueue(action);
        return true;
    }

    public int Drain(int maxCount, Action<Exception>? onException = null)
    {
        var drained = 0;
        while (drained < Math.Max(0, maxCount) && queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                onException?.Invoke(ex);
            }

            drained++;
        }

        return drained;
    }

    public void Clear()
    {
        while (queue.TryDequeue(out _))
        {
        }
    }
}
