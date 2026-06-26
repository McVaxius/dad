using System.Collections.Concurrent;

namespace dad.Services;

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
