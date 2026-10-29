extern alias DadRuntime;
extern alias DalamudApi;

using System.Reflection;
using DalamudApi::Dalamud.Plugin.Services;
using Xunit;
using M = DadRuntime::dad.Models;
using S = DadRuntime::dad.Services;

namespace dad.Tests;

public sealed class DadRepairRecoveryRuntimeTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 13, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RuntimeConsumesTerminalStatusWaitsForRealSafetyProbeAndStartsOnlyOneRetry()
    {
        using var fixture = new Fixture();
        Assert.Equal(M.DadPreDutyRepairAction.Wait, fixture.Tick(0).Action);
        Assert.Equal(1, fixture.Starts);
        fixture.Ads = M.DadAdsRepairObservation.Running(true, "NPC repair", "npc");
        fixture.Tick(1);
        fixture.Ads = M.DadAdsRepairStatusRules.Parse("""
            {"utilityRunning":false,"utilityStatus":"Unowned NPC UI","utilityLastFailure":"Unowned NPC UI",
             "utilityCompletedAtUtc":"2026-10-29T13:00:10Z"}
            """);
        Assert.Contains("Unowned NPC UI", fixture.Tick(10).Summary);
        fixture.Safety = new(true, false, "Lifestream busy");
        fixture.Tick(11);
        fixture.Safety = new(true, false, "SelectString visible");
        fixture.Tick(12);
        fixture.Safety = new(true, true, "settled");
        for (var second = 13; second < 18; second++) fixture.Tick(second);
        Assert.Equal(1, fixture.Starts);
        fixture.Tick(18);
        Assert.Equal(2, fixture.Starts);
        fixture.Tick(18); // Runtime poll cadence blocks reentrant/same-time dispatch.
        fixture.Tick(19); // Previous failure timestamp cannot consume the new attempt.
        Assert.Equal(2, fixture.Starts);
        fixture.Ads = M.DadAdsRepairObservation.Running(true, "NPC repair", "npc");
        fixture.Tick(20);
        fixture.Durability = 100;
        fixture.Ads = M.DadAdsRepairObservation.Idle();
        Assert.Equal(M.DadPreDutyRepairAction.Ready, fixture.Tick(21).Action);
        Assert.True(fixture.SafetyReads > 0);
    }

    [Fact]
    public void ResetCancelsPendingRecoveryWithoutStartingAnyMoreWork()
    {
        using var fixture = new Fixture(); fixture.Tick(0);
        fixture.Ads = M.DadAdsRepairObservation.Running(true, "NPC repair", "npc"); fixture.Tick(1);
        fixture.Ads = M.DadAdsRepairObservation.Idle("failure") with
            { LastFailure = "failure", CompletedAtUtc = Now.AddSeconds(10) };
        fixture.Tick(10);
        fixture.Service.Reset();
        for (var second = 11; second < 30; second++) fixture.Tick(second);
        Assert.Equal(1, fixture.Starts);
        Assert.Equal(0, fixture.SafetyReads);
        fixture.Service.Begin(fixture.Request, M.DadModuleId.DutySupport, Now.AddSeconds(30));
        fixture.Tick(30); // Explicit new run starts normally; old failure isn't a retry authorization.
        Assert.Equal(2, fixture.Starts);
    }

    [Fact]
    public void NativeSafetyProbeCannotTreatUnavailableOrBusyLifestreamAsSettled()
    {
        Assert.False(S.DadDutySupportAdsService.ReadRepairRetrySafety(new(false, true, false, "unavailable")).Readable);
        var busy = S.DadDutySupportAdsService.ReadRepairRetrySafety(new(true, true, false, "busy"));
        Assert.True(busy.Readable);
        Assert.False(busy.Safe);
    }

    private sealed class Fixture : IDisposable
    {
        public int Starts;
        public int SafetyReads;
        public int Durability = 30;
        public M.DadAdsRepairObservation Ads = M.DadAdsRepairObservation.Idle();
        public M.DadRepairRetrySafetyObservation Safety = new(true, true, "settled");
        public M.DadRunRequest Request { get; } = new()
        {
            RequestId = "recovery-run",
            PreDutyRepairPolicy = new() { Enabled = true, ThresholdPercent = 35, Mode = M.DadPreDutyRepairMode.NpcWithInnFallback },
        };
        public S.DadPreDutyRepairRuntimeService Service { get; }
        public Fixture()
        {
            var constructor = typeof(S.DadPreDutyRepairRuntimeService)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var log = DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>();
            Service = (S.DadPreDutyRepairRuntimeService)constructor.Invoke([
                new Func<M.DadEquippedDurabilityObservation>(() => M.DadEquippedDurabilityObservation.ReadableAt(Durability)),
                new Func<M.DadAdsRepairObservation>(() => Ads),
                new Func<string, M.DadAdsRepairInvocationResult>(mode =>
                {
                    Assert.Equal("npc", mode); Starts++;
                    return new(M.DadAdsRepairInvocationOutcome.Accepted, "accepted");
                }),
                new Func<M.DadRepairRetrySafetyObservation>(() => { SafetyReads++; return Safety; }), log]);
            Service.Begin(Request, M.DadModuleId.DutySupport, Now);
        }
        public M.DadPreDutyRepairDecision Tick(int seconds) => Service.Update(Now.AddSeconds(seconds));
        public void Dispose() => Service.Reset();
    }
}
