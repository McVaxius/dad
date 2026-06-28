using dad.Services;
using Xunit;

namespace dad.Tests;

// B6: a forced (user-driven) roster refresh must coalesce onto an in-flight op instead of being silently
// dropped; a non-forced periodic request still respects the throttle and the in-flight dedupe.
public sealed class DadRosterRefreshDedupeTests
{
    [Fact]
    public void IdleUnthrottledRequestQueues()
    {
        Assert.Equal(
            DadRosterRefreshDispatch.Queue,
            DadRosterRefreshDedupe.DecideRosterRefresh(force: false, throttled: false, operationInFlight: false));
    }

    [Fact]
    public void NonForcedThrottledRequestSkips()
    {
        Assert.Equal(
            DadRosterRefreshDispatch.SkipThrottled,
            DadRosterRefreshDedupe.DecideRosterRefresh(force: false, throttled: true, operationInFlight: false));
    }

    [Fact]
    public void ForcedRequestBypassesThrottle()
    {
        Assert.Equal(
            DadRosterRefreshDispatch.Queue,
            DadRosterRefreshDedupe.DecideRosterRefresh(force: true, throttled: true, operationInFlight: false));
    }

    [Fact]
    public void ForcedRequestCoalescesOntoInFlightOp()
    {
        Assert.Equal(
            DadRosterRefreshDispatch.CoalesceOntoInFlight,
            DadRosterRefreshDedupe.DecideRosterRefresh(force: true, throttled: false, operationInFlight: true));
    }

    [Fact]
    public void NonForcedRequestDefersToInFlightOp()
    {
        Assert.Equal(
            DadRosterRefreshDispatch.SkipThrottled,
            DadRosterRefreshDedupe.DecideRosterRefresh(force: false, throttled: false, operationInFlight: true));
    }
}
