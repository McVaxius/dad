namespace dad.Services;

// B6: pure decision for how a roster-catalog refresh request should be dispatched. Splitting this out keeps
// the dedupe/attach semantics unit-testable: a forced (user-driven) request must coalesce onto an in-flight
// op instead of being silently dropped, while a non-forced periodic request still respects the throttle.
public enum DadRosterRefreshDispatch
{
    /// <summary>No op is in flight and the throttle (if any) permits a new pull; queue it.</summary>
    Queue,

    /// <summary>An op is already in flight; reuse it. Its completion delivers the result and bumps the cache revision.</summary>
    CoalesceOntoInFlight,

    /// <summary>Throttled and not forced; skip silently until the throttle window elapses.</summary>
    SkipThrottled,
}

public static class DadRosterRefreshDedupe
{
    public static DadRosterRefreshDispatch DecideRosterRefresh(bool force, bool throttled, bool operationInFlight)
    {
        if (operationInFlight)
        {
            // A forced request piggybacks on the in-flight op rather than no-opping; a non-forced request
            // would have nothing extra to add, so it also defers to the in-flight op.
            return force
                ? DadRosterRefreshDispatch.CoalesceOntoInFlight
                : DadRosterRefreshDispatch.SkipThrottled;
        }

        if (!force && throttled)
            return DadRosterRefreshDispatch.SkipThrottled;

        return DadRosterRefreshDispatch.Queue;
    }
}
