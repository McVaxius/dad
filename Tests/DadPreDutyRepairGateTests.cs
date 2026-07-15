using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadPreDutyRepairGateTests
{
    private static readonly DateTime Now = new(2026, 7, 15, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PolicyDefaultsDisabledAtSeventyFivePercentWithSelfMode()
    {
        var policy = new DadPreDutyRepairPolicy();

        Assert.False(policy.Enabled);
        Assert.Equal(75, policy.ThresholdPercent);
        Assert.Equal(DadPreDutyRepairMode.Self, policy.Mode);
        Assert.Equal("self", policy.AdsMode);
    }

    [Theory]
    [InlineData(DadPreDutyRepairMode.Self, "self")]
    [InlineData(DadPreDutyRepairMode.NpcExcludingInns, "npc-no-inn")]
    [InlineData(DadPreDutyRepairMode.NearbyNpcNoTeleportOrInn, "npc-no-teleport-no-inn")]
    public void PolicyMapsModesToExactAdsContracts(DadPreDutyRepairMode mode, string expected)
    {
        var policy = EnabledPolicy();
        policy.Mode = mode;

        Assert.Equal(expected, policy.AdsMode);
    }

    [Fact]
    public void DisabledAndBlundervillePoliciesSkipAdsEntirely()
    {
        var disabled = new DadPreDutyRepairGate().Evaluate(
            new DadPreDutyRepairPolicy(),
            DadModuleId.PremadeDuty,
            new DadRunRequest(),
            DadEquippedDurabilityObservation.Unreadable("not consulted"),
            DadAdsRepairObservation.Absent(),
            Now);
        var blunderville = new DadPreDutyRepairGate().Evaluate(
            EnabledPolicy(),
            DadModuleId.Blunderville,
            new DadRunRequest(),
            DadEquippedDurabilityObservation.ReadableAt(1),
            DadAdsRepairObservation.Absent(),
            Now);

        Assert.Equal(DadPreDutyRepairAction.Ready, disabled.Action);
        Assert.Equal(DadPreDutyRepairAction.Ready, blunderville.Action);
    }

    [Fact]
    public void MixedAppliesOnlyWhenAQueueCapableChildExists()
    {
        var policy = EnabledPolicy();

        Assert.False(DadPreDutyRepairRules.IsRequired(policy, DadModuleId.Mixed, new DadRunRequest()));
        Assert.True(DadPreDutyRepairRules.IsRequired(
            policy,
            DadModuleId.Mixed,
            new DadRunRequest { PremadeDuty = new DadPremadeDutyTask() }));
    }

    [Theory]
    [InlineData(75, DadPreDutyRepairAction.Ready)]
    [InlineData(76, DadPreDutyRepairAction.Ready)]
    [InlineData(74, DadPreDutyRepairAction.InvokeAds)]
    public void RepairIsRequiredOnlyStrictlyBelowThreshold(int durability, DadPreDutyRepairAction expected)
    {
        var decision = new DadPreDutyRepairGate().Evaluate(
            EnabledPolicy(),
            DadModuleId.PremadeDuty,
            new DadRunRequest(),
            DadEquippedDurabilityObservation.ReadableAt(durability),
            DadAdsRepairObservation.Idle(),
            Now);

        Assert.Equal(expected, decision.Action);
    }

    [Fact]
    public void UnreadableDurabilityAndAdsReceiveOnlyFiveSecondsOfGrace()
    {
        var durabilityGate = new DadPreDutyRepairGate();
        Assert.Equal(DadPreDutyRepairAction.Wait, durabilityGate.Evaluate(
            EnabledPolicy(), DadModuleId.PremadeDuty, new DadRunRequest(),
            DadEquippedDurabilityObservation.Unreadable("inventory transition"),
            DadAdsRepairObservation.Absent(), Now).Action);
        Assert.Equal(DadPreDutyRepairAction.Reject, durabilityGate.Evaluate(
            EnabledPolicy(), DadModuleId.PremadeDuty, new DadRunRequest(),
            DadEquippedDurabilityObservation.Unreadable("inventory transition"),
            DadAdsRepairObservation.Absent(), Now.AddSeconds(5)).Action);

        var adsGate = new DadPreDutyRepairGate();
        Assert.Equal(DadPreDutyRepairAction.Wait, adsGate.Evaluate(
            EnabledPolicy(), DadModuleId.PremadeDuty, new DadRunRequest(),
            DadEquippedDurabilityObservation.ReadableAt(20),
            DadAdsRepairObservation.Unreadable("IPC transition"), Now).Action);
        Assert.Equal(DadPreDutyRepairAction.Reject, adsGate.Evaluate(
            EnabledPolicy(), DadModuleId.PremadeDuty, new DadRunRequest(),
            DadEquippedDurabilityObservation.ReadableAt(20),
            DadAdsRepairObservation.Unreadable("IPC transition"), Now.AddSeconds(5)).Action);
    }

    [Fact]
    public void MissingAdsFailsImmediatelyWhenRepairIsNeeded()
    {
        var decision = new DadPreDutyRepairGate().Evaluate(
            EnabledPolicy(),
            DadModuleId.PremadeDuty,
            new DadRunRequest(),
            DadEquippedDurabilityObservation.ReadableAt(20),
            DadAdsRepairObservation.Absent(),
            Now);

        Assert.Equal(DadPreDutyRepairAction.Reject, decision.Action);
        Assert.Contains("not loaded", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingRepairIsAdoptedAndUnrelatedUtilityIsWaitedOut()
    {
        var adopted = new DadPreDutyRepairGate();
        var unrelated = new DadPreDutyRepairGate();

        Assert.Equal(DadPreDutyRepairAction.Wait, adopted.Evaluate(
            EnabledPolicy(), DadModuleId.PremadeDuty, new DadRunRequest(),
            DadEquippedDurabilityObservation.ReadableAt(20),
            DadAdsRepairObservation.Running(true, "repair", "self"), Now).Action);
        Assert.Equal(0, adopted.InvocationCount);
        Assert.Equal(DadPreDutyRepairAction.Wait, unrelated.Evaluate(
            EnabledPolicy(), DadModuleId.PremadeDuty, new DadRunRequest(),
            DadEquippedDurabilityObservation.ReadableAt(20),
            DadAdsRepairObservation.Running(false, "extract materia", ""), Now).Action);
        Assert.Equal(0, unrelated.InvocationCount);
    }

    [Fact]
    public void ExplicitFalseRetriesAtMostThreeTimesThirtySecondsApart()
    {
        var gate = new DadPreDutyRepairGate();
        var policy = EnabledPolicy();
        var request = new DadRunRequest();
        var durability = DadEquippedDurabilityObservation.ReadableAt(20);
        var ads = DadAdsRepairObservation.Idle();

        Assert.Equal(DadPreDutyRepairAction.InvokeAds, gate.Evaluate(policy, DadModuleId.PremadeDuty, request, durability, ads, Now).Action);
        gate.RecordInvocationResult(new(DadAdsRepairInvocationOutcome.ExplicitFalse, "false"), Now);
        Assert.Equal(DadPreDutyRepairAction.Wait, gate.Evaluate(policy, DadModuleId.PremadeDuty, request, durability, ads, Now.AddSeconds(29)).Action);
        Assert.Equal(DadPreDutyRepairAction.InvokeAds, gate.Evaluate(policy, DadModuleId.PremadeDuty, request, durability, ads, Now.AddSeconds(30)).Action);
        gate.RecordInvocationResult(new(DadAdsRepairInvocationOutcome.ExplicitFalse, "false"), Now.AddSeconds(30));
        Assert.Equal(DadPreDutyRepairAction.InvokeAds, gate.Evaluate(policy, DadModuleId.PremadeDuty, request, durability, ads, Now.AddSeconds(60)).Action);
        gate.RecordInvocationResult(new(DadAdsRepairInvocationOutcome.ExplicitFalse, "false"), Now.AddSeconds(60));

        Assert.Equal(3, gate.InvocationCount);
        Assert.Equal(DadPreDutyRepairAction.Reject, gate.Evaluate(policy, DadModuleId.PremadeDuty, request, durability, ads, Now.AddSeconds(60)).Action);
    }

    [Theory]
    [InlineData(DadAdsRepairInvocationOutcome.Accepted, DadPreDutyRepairAction.Wait)]
    [InlineData(DadAdsRepairInvocationOutcome.Uncertain, DadPreDutyRepairAction.Reject)]
    public void AcceptedOrUncertainInvocationIsNeverRetried(
        DadAdsRepairInvocationOutcome outcome,
        DadPreDutyRepairAction expected)
    {
        var gate = new DadPreDutyRepairGate();
        var policy = EnabledPolicy();
        var request = new DadRunRequest();
        var durability = DadEquippedDurabilityObservation.ReadableAt(20);
        var ads = DadAdsRepairObservation.Idle();
        Assert.Equal(DadPreDutyRepairAction.InvokeAds, gate.Evaluate(policy, DadModuleId.PremadeDuty, request, durability, ads, Now).Action);

        gate.RecordInvocationResult(new(outcome, "result"), Now);
        var decision = gate.Evaluate(policy, DadModuleId.PremadeDuty, request, durability, ads, Now.AddSeconds(90));

        Assert.Equal(expected, decision.Action);
        Assert.Equal(1, gate.InvocationCount);
    }

    [Fact]
    public void DurabilityTruthIsTheOnlySuccessCondition()
    {
        var gate = new DadPreDutyRepairGate();
        var policy = EnabledPolicy();
        var request = new DadRunRequest();
        var low = DadEquippedDurabilityObservation.ReadableAt(20);
        var idle = DadAdsRepairObservation.Idle();
        Assert.Equal(DadPreDutyRepairAction.InvokeAds, gate.Evaluate(policy, DadModuleId.PremadeDuty, request, low, idle, Now).Action);
        gate.RecordInvocationResult(new(DadAdsRepairInvocationOutcome.Accepted, "accepted"), Now);

        Assert.Equal(DadPreDutyRepairAction.Wait, gate.Evaluate(
            policy, DadModuleId.PremadeDuty, request, low, idle, Now.AddSeconds(1)).Action);
        Assert.Equal(DadPreDutyRepairAction.Ready, gate.Evaluate(
            policy, DadModuleId.PremadeDuty, request,
            DadEquippedDurabilityObservation.ReadableAt(75), idle, Now.AddSeconds(2)).Action);
    }

    [Fact]
    public void UnrelatedUtilitySharesTheSingleOverallTimeout()
    {
        var gate = new DadPreDutyRepairGate();
        var policy = EnabledPolicy();
        var durability = DadEquippedDurabilityObservation.ReadableAt(20);
        var busy = DadAdsRepairObservation.Running(false, "other utility", "");

        Assert.Equal(DadPreDutyRepairAction.Wait, gate.Evaluate(
            policy, DadModuleId.PremadeDuty, new DadRunRequest(), durability, busy, Now).Action);
        Assert.Equal(DadPreDutyRepairAction.Reject, gate.Evaluate(
            policy, DadModuleId.PremadeDuty, new DadRunRequest(), durability, busy, Now.AddSeconds(180)).Action);
    }

    private static DadPreDutyRepairPolicy EnabledPolicy()
        => new() { Enabled = true, ThresholdPercent = 75, Mode = DadPreDutyRepairMode.Self };
}
