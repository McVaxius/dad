using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadRepairRecoveryTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 13, 0, 0, DateTimeKind.Utc);
    private static readonly DadPreDutyRepairPolicy Policy = new()
        { Enabled = true, ThresholdPercent = 35, Mode = DadPreDutyRepairMode.NpcWithInnFallback };
    private static readonly DadAdsRepairObservation Running = DadAdsRepairObservation.Running(true, "NPC repair", "npc");
    private static DadAdsRepairObservation Failure(int seconds = 10) => DadAdsRepairObservation.Idle("Unowned NPC UI appeared during repair") with
        { LastFailure = "Unowned NPC UI appeared during repair", CompletedAtUtc = Now.AddSeconds(seconds) };
    private static readonly DadRepairRetrySafetyObservation Safe = new(true, true, "settled");
    private static DadPreDutyRepairDecision Tick(DadPreDutyRepairGate gate, int seconds, DadAdsRepairObservation ads,
        DadRepairRetrySafetyObservation safety = default, int durability = 30)
        => gate.Evaluate(Policy, DadModuleId.DutySupport, new(), DadEquippedDurabilityObservation.ReadableAt(durability),
            ads, Now.AddSeconds(seconds), safety);

    private static DadPreDutyRepairGate Started()
    {
        var gate = new DadPreDutyRepairGate();
        Assert.Equal(DadPreDutyRepairAction.InvokeAds, Tick(gate, 0, DadAdsRepairObservation.Idle()).Action);
        gate.RecordInvocationResult(new(DadAdsRepairInvocationOutcome.Accepted, "accepted"), Now);
        Tick(gate, 1, Running);
        return gate;
    }

    [Fact]
    public void ExplicitOwnedFailureIsSurfacedInsteadOfSilentlyWaitingForDurabilityTimeout()
    {
        var gate = new DadPreDutyRepairGate();
        Assert.Equal(DadPreDutyRepairAction.InvokeAds, Tick(gate, 0, DadAdsRepairObservation.Idle()).Action);
        gate.RecordInvocationResult(new(DadAdsRepairInvocationOutcome.Accepted, "accepted"), Now);
        Tick(gate, 1, Running);
        var decision = Tick(gate, 10, Failure());
        Assert.Equal(DadPreDutyRepairAction.Wait, decision.Action);
        Assert.Contains("Unowned NPC UI", decision.Summary);
        Assert.Contains("recovery", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OneRetryAfterContinuousSafeSettleThenSecondFailureIsTerminalWithItsCause()
    {
        var gate = Started();
        Tick(gate, 10, Failure());
        for (var second = 11; second < 16; second++)
            Assert.Equal(DadPreDutyRepairAction.Wait, Tick(gate, second, Failure(), Safe).Action);
        Assert.Equal(DadPreDutyRepairAction.InvokeAds, Tick(gate, 16, Failure(), Safe).Action);
        gate.RecordInvocationResult(new(DadAdsRepairInvocationOutcome.Accepted, "accepted retry"), Now.AddSeconds(16));
        Assert.Equal(DadPreDutyRepairAction.Wait, Tick(gate, 17, Running).Action);
        var rejected = Tick(gate, 20, Failure(20));
        Assert.Equal(DadPreDutyRepairAction.Reject, rejected.Action);
        Assert.Contains("failed again", rejected.Summary);
        Assert.Contains("Unowned NPC UI", rejected.Summary);
        Assert.Equal(DadPreDutyRepairAction.Reject, Tick(gate, 21, Failure(20), Safe).Action);
        Assert.Equal(2, gate.InvocationCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void BusyOrUnreadableTravelAndUiNeverAllowRetry(bool readable, bool safe)
    {
        var gate = Started();
        Tick(gate, 10, Failure());
        for (var second = 11; second < 40; second++)
            Assert.Equal(DadPreDutyRepairAction.Wait, Tick(gate, second, Failure(), new(readable, safe, "travel/menu pending")).Action);
        var rejected = Tick(gate, 40, Failure(), Safe);
        Assert.Equal(DadPreDutyRepairAction.Reject, rejected.Action);
        Assert.Contains("30 seconds", rejected.Summary);
        Assert.Contains("Unowned NPC UI", rejected.Summary);
        Assert.Equal(1, gate.InvocationCount);
    }

    [Fact]
    public void BusyInterruptionOrObservationGapRestartsContinuousSettleProof()
    {
        var gate = Started();
        Tick(gate, 10, Failure());
        Tick(gate, 11, Failure(), Safe);
        Tick(gate, 12, Failure(), Safe);
        Tick(gate, 13, Failure(), new(true, false, "SelectString visible"));
        Tick(gate, 14, Failure(), Safe);
        Tick(gate, 15, Failure(), Safe);
        Assert.Equal(DadPreDutyRepairAction.Wait, Tick(gate, 20, Failure(), Safe).Action); // No observations during gap.
        for (var second = 21; second < 25; second++) Tick(gate, second, Failure(), Safe);
        Assert.Equal(DadPreDutyRepairAction.InvokeAds, Tick(gate, 25, Failure(), Safe).Action);
    }

    [Theory]
    [InlineData("old")]
    [InlineData("future")]
    [InlineData("missing")]
    [InlineData("success")]
    [InlineData("contradictory-status")]
    [InlineData("no-running-proof")]
    [InlineData("foreign-utility")]
    public void AmbiguousOrStaleTerminalMetadataCannotAuthorizeReplay(string scenario)
    {
        var gate = Started();
        if (scenario == "no-running-proof")
        {
            gate = new(); Tick(gate, 0, DadAdsRepairObservation.Idle());
            gate.RecordInvocationResult(new(DadAdsRepairInvocationOutcome.Accepted, "accepted"), Now);
        }
        if (scenario == "foreign-utility") Tick(gate, 2, DadAdsRepairObservation.Running(false, "extract materia", ""));
        var failure = scenario switch
        {
            "old" => Failure(-10), "future" => Failure(90), "missing" => Failure() with { CompletedAtUtc = null },
            "success" => Failure() with { LastSuccess = "a different task succeeded" },
            "contradictory-status" => Failure() with { Summary = "Idle" }, _ => Failure(),
        };
        for (var second = 10; second < 25; second++)
            Assert.Equal(DadPreDutyRepairAction.Wait, Tick(gate, second, failure, Safe).Action);
        Assert.False(gate.NeedsRecoverySafety);
        Assert.Equal(1, gate.InvocationCount);
    }

    [Fact]
    public void AnotherCompletionDuringRecoveryFailsWithoutStartingMoreWork()
    {
        var gate = Started(); Tick(gate, 10, Failure());
        var rejected = Tick(gate, 11, Failure(11), Safe);
        Assert.Equal(DadPreDutyRepairAction.Reject, rejected.Action);
        Assert.Contains("ownership is uncertain", rejected.Summary);
    }

    [Fact]
    public void HealthyGearSkipsRetryOnlyAfterTravelAndUiHaveSettled()
    {
        var gate = Started(); Tick(gate, 10, Failure());
        Assert.Equal(DadPreDutyRepairAction.Wait, Tick(gate, 11, Failure(), default, 100).Action);
        for (var second = 12; second < 17; second++) Tick(gate, second, Failure(), Safe, 100);
        Assert.Equal(DadPreDutyRepairAction.Ready, Tick(gate, 17, Failure(), Safe, 100).Action);
        Assert.Equal(DadPreDutyRepairAction.Ready, Tick(gate, 18, Failure(), Safe, 100).Action);
        Assert.Equal(1, gate.InvocationCount);
    }

    [Theory]
    [InlineData(DadAdsRepairInvocationOutcome.ExplicitFalse)]
    [InlineData(DadAdsRepairInvocationOutcome.Uncertain)]
    public void DeclinedOrUncertainRecoveryInvocationIsNotRetriedAgain(DadAdsRepairInvocationOutcome result)
    {
        var gate = Started(); Tick(gate, 10, Failure());
        for (var second = 11; second <= 16; second++) Tick(gate, second, Failure(), Safe);
        gate.RecordInvocationResult(new(result, "recovery result"), Now.AddSeconds(16));
        Assert.Equal(DadPreDutyRepairAction.Reject, Tick(gate, 17, Failure(), Safe).Action);
        Assert.Equal(2, gate.InvocationCount);
    }

    [Fact]
    public void RetryDoesNotRefreshOriginalOverallDeadline()
    {
        var gate = Started(); Tick(gate, 170, Failure(170));
        for (var second = 171; second <= 176; second++) Tick(gate, second, Failure(170), Safe);
        gate.RecordInvocationResult(new(DadAdsRepairInvocationOutcome.Accepted, "retry"), Now.AddSeconds(176));
        var rejected = Tick(gate, 180, Running);
        Assert.Equal(DadPreDutyRepairAction.Reject, rejected.Action);
        Assert.Contains("180-second", rejected.Summary);
        Assert.Contains("Unowned NPC UI", rejected.Summary);
    }

    [Theory]
    [InlineData(DadPreDutyRepairMode.NpcThenInn, "npc-yes-inn-uldah", false)]
    [InlineData(DadPreDutyRepairMode.NpcThenInn, "npc-yes-inn", false)]
    [InlineData(DadPreDutyRepairMode.NpcThenInn, "npc-yes-inn-limsa", true)]
    [InlineData(DadPreDutyRepairMode.NpcWithInnFallback, "npc", true)]
    public void RecoveryRequiresTheExactRequestedRoute(DadPreDutyRepairMode mode, string observedMode, bool recover)
    {
        var policy = new DadPreDutyRepairPolicy
        {
            Enabled = true, ThresholdPercent = 35, Mode = mode, InnDestination = DadRepairInnDestination.Limsa,
        };
        var gate = new DadPreDutyRepairGate();
        DadPreDutyRepairDecision Observe(int seconds, DadAdsRepairObservation ads)
            => gate.Evaluate(policy, DadModuleId.DutySupport, new(),
                DadEquippedDurabilityObservation.ReadableAt(30), ads, Now.AddSeconds(seconds), Safe);
        Assert.Equal(DadPreDutyRepairAction.InvokeAds, Observe(0, DadAdsRepairObservation.Idle()).Action);
        gate.RecordInvocationResult(new(DadAdsRepairInvocationOutcome.Accepted, "accepted"), Now);
        Observe(1, DadAdsRepairObservation.Running(true, "NPC repair", observedMode));
        Observe(10, Failure());
        var recoveryRequested = false;
        for (var second = 11; second <= 16; second++)
            recoveryRequested |= Observe(second, Failure()).Action == DadPreDutyRepairAction.InvokeAds;
        Assert.Equal(recover, recoveryRequested);
        Assert.Equal(1, gate.InvocationCount);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(10, true)]
    [InlineData(11, true)]
    public void FailureCompletionCannotPredateTheLatestRunningObservation(int completedSeconds, bool recover)
    {
        var gate = Started();
        Tick(gate, 10, Running);
        Tick(gate, 11, Failure(completedSeconds));
        var recoveryRequested = false;
        for (var second = 12; second <= 17; second++)
            recoveryRequested |= Tick(gate, second, Failure(completedSeconds), Safe).Action == DadPreDutyRepairAction.InvokeAds;
        Assert.Equal(recover, recoveryRequested);
        Assert.Equal(1, gate.InvocationCount);
    }

    [Fact]
    public void StatusParserPreservesTerminalProofButNeverInventsMissingFields()
    {
        var parsed = DadAdsRepairStatusRules.Parse("""
            {"utilityRunning":false,"utilityStatus":"NPC failed","utilityLastFailure":"NPC failed",
             "utilityLastSuccess":"","utilityCompletedAtUtc":"2026-10-29T13:00:10Z"}
            """);
        Assert.Equal("NPC failed", parsed.LastFailure);
        Assert.Equal(Now.AddSeconds(10), parsed.CompletedAtUtc);
        var legacy = DadAdsRepairStatusRules.Parse("{\"utilityRunning\":false}");
        Assert.True(legacy.Readable);
        Assert.Null(legacy.CompletedAtUtc);
        Assert.Empty(legacy.LastFailure);
        Assert.False(DadAdsRepairStatusRules.Parse("[]").Readable);
        Assert.False(DadAdsRepairStatusRules.Parse("{bad").Readable);
        Assert.Null(DadAdsRepairStatusRules.Parse("{\"utilityRunning\":false,\"utilityCompletedAtUtc\":\"invalid\"}").CompletedAtUtc);
    }
}
