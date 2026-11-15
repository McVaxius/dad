namespace dad.Models;

/// <summary>Requires continuously observed safe idle UI; gaps/unsafe frames discard proof.</summary>
public sealed class DadEmptyCharacterSelectRecoveryGate
{
    public static readonly TimeSpan StableDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumObservationGap = TimeSpan.FromSeconds(15);
    public const int MaximumAttempts = 5;
    private DateTime? safeSinceUtc;
    private DateTime? lastObservationUtc;
    private readonly TimeSpan delay;
    public int AttemptCount { get; private set; }

    public DadEmptyCharacterSelectRecoveryGate(TimeSpan? stableDelay = null)
        => delay = stableDelay ?? StableDelay;

    public bool Observe(bool safe, DateTime nowUtc)
    {
        if (!safe)
        {
            ResetProof();
            return false;
        }
        if (!lastObservationUtc.HasValue || nowUtc < lastObservationUtc.Value ||
            nowUtc - lastObservationUtc.Value > MaximumObservationGap)
            safeSinceUtc = nowUtc;
        lastObservationUtc = nowUtc;
        return AttemptCount < MaximumAttempts && safeSinceUtc.HasValue &&
               nowUtc - safeSinceUtc.Value >= delay;
    }

    public void RecordAttempt()
    {
        AttemptCount = Math.Min(MaximumAttempts, AttemptCount + 1);
        ResetProof();
    }

    public void ResetProof()
    {
        safeSinceUtc = null;
        lastObservationUtc = null;
    }
}
