namespace dad.Services;

internal sealed class DadDeferredSaveGate
{
    private readonly TimeSpan quietPeriod;
    private readonly TimeSpan maximumDelay;
    private readonly object gate = new();
    private DateTime? firstDirtyUtc;
    private DateTime? lastDirtyUtc;

    public DadDeferredSaveGate(TimeSpan quietPeriod, TimeSpan maximumDelay)
    {
        this.quietPeriod = quietPeriod;
        this.maximumDelay = maximumDelay;
    }

    public bool IsPending
    {
        get
        {
            lock (gate)
                return firstDirtyUtc.HasValue;
        }
    }

    public void MarkDirty(DateTime nowUtc)
    {
        lock (gate)
        {
            firstDirtyUtc ??= nowUtc;
            lastDirtyUtc = nowUtc;
        }
    }

    public bool IsDue(DateTime nowUtc)
    {
        lock (gate)
            return IsDueUnsafe(nowUtc);
    }

    public bool TryConsumeDue(DateTime nowUtc, bool force = false)
    {
        lock (gate)
        {
            if (!firstDirtyUtc.HasValue || !force && !IsDueUnsafe(nowUtc))
                return false;

            firstDirtyUtc = null;
            lastDirtyUtc = null;
            return true;
        }
    }

    public void Discard()
    {
        lock (gate)
        {
            firstDirtyUtc = null;
            lastDirtyUtc = null;
        }
    }

    private bool IsDueUnsafe(DateTime nowUtc)
        => firstDirtyUtc.HasValue &&
           lastDirtyUtc.HasValue &&
           (nowUtc - lastDirtyUtc.Value >= quietPeriod ||
            nowUtc - firstDirtyUtc.Value >= maximumDelay);
}
