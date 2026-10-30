extern alias DadRuntime;
extern alias DalamudApi;

using System.Reflection;
using System.Runtime.CompilerServices;
using DalamudApi::Dalamud.Plugin.Ipc;
using DalamudApi::Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResponsiveRepairStatusIsAuthoritativeDespiteInstalledPluginMetadata(bool metadataLoaded)
    {
        var reads = 0;
        var status = ReadStatus(metadataLoaded, () =>
        {
            reads++;
            return """{"utilityRunning":true,"utilityTask":"NPC repair","utilityMode":"npc","utilityStatus":"Travelling"}""";
        });
        Assert.Equal(1, reads);
        Assert.True(status.Available);
        Assert.True(status.Readable);
        Assert.True(status.RepairRunning);
        Assert.Equal("npc", status.UtilityMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BrokenRepairStatusNeverBecomesReadableIdle(bool metadataLoaded)
    {
        Assert.False(ReadStatus(metadataLoaded, () => "").Readable);
        Assert.False(ReadStatus(metadataLoaded, () => "{}").Readable);
        var failed = ReadStatus(metadataLoaded, () => throw new InvalidOperationException("fixture endpoint unavailable"));
        Assert.Equal(metadataLoaded, failed.Available);
        Assert.False(failed.Readable);
    }

    [Theory]
    [InlineData("npc", true)]
    [InlineData("npc-no-inn", false)]
    [InlineData("npc-yes-inn-gridania", true)]
    public void RepairInvocationPreservesModeAndExplicitAcceptance(string mode, bool accepted)
    {
        var calls = 0;
        var service = RepairService(requested => { Assert.Equal(mode, requested); calls++; return accepted; });
        var result = service.StartRepair(mode);
        Assert.Equal(1, calls);
        Assert.Equal(accepted ? M.DadAdsRepairInvocationOutcome.Accepted : M.DadAdsRepairInvocationOutcome.ExplicitFalse, result.Outcome);
    }

    [Fact]
    public void RepairInvocationExceptionRemainsUncertainAndIsNotReplayed()
    {
        var calls = 0;
        var service = RepairService(_ => { calls++; throw new InvalidOperationException("fixture acceptance unknown"); });
        Assert.Equal(M.DadAdsRepairInvocationOutcome.Uncertain, service.StartRepair("npc").Outcome);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(ContentsFinderQueueState.Pending)]
    [InlineData(ContentsFinderQueueState.Queued)]
    [InlineData(ContentsFinderQueueState.Ready)]
    [InlineData(ContentsFinderQueueState.Accepted)]
    public void NativeQueueActivityBlocksSettlementEvenWhenConditionFlagsAreClear(ContentsFinderQueueState state)
    {
        var safety = NativeSettlement(1, 1, state);
        Assert.True(safety.Readable);
        Assert.False(safety.Safe);
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(1u, 0u)]
    [InlineData(1u, 2u)]
    public void NativeTerritoryMustAgreeBeforeSettlement(uint territory, uint nativeTerritory)
    {
        var safety = NativeSettlement(territory, nativeTerritory, default(ContentsFinderQueueState));
        Assert.False(safety.Readable);
        Assert.False(safety.Safe);
    }

    [Fact]
    public void MissingOrUnknownNativeQueueTruthCannotAuthorizeRecovery()
    {
        Assert.False(NativeSettlement(1, 1, null).Safe);
        Assert.False(NativeSettlement(1, 1, (ContentsFinderQueueState)byte.MaxValue).Safe);
        Assert.True(NativeSettlement(1, 1, default(ContentsFinderQueueState)).Safe);
    }

    [Fact]
    public void NativeQueueAndWorldSettlementInterruptTheRuntimeRecoveryWindow()
    {
        using var fixture = new Fixture(); fixture.Tick(0);
        fixture.Ads = M.DadAdsRepairObservation.Running(true, "NPC repair", "npc"); fixture.Tick(1);
        fixture.Ads = M.DadAdsRepairObservation.Idle("fixture failure") with
            { LastFailure = "fixture failure", CompletedAtUtc = Now.AddSeconds(10) };
        fixture.Tick(10);
        fixture.Safety = NativeSettlement(1, 1, ContentsFinderQueueState.Pending); fixture.Tick(11);
        fixture.Safety = NativeSettlement(1, 1, default(ContentsFinderQueueState)); fixture.Tick(12);
        fixture.Safety = NativeSettlement(1, 2, default(ContentsFinderQueueState)); fixture.Tick(13);
        fixture.Safety = NativeSettlement(1, 1, default(ContentsFinderQueueState));
        for (var second = 14; second < 19; second++) fixture.Tick(second);
        Assert.Equal(1, fixture.Starts);
        fixture.Tick(19);
        Assert.Equal(2, fixture.Starts);
    }

    private static M.DadAdsRepairObservation ReadStatus(bool metadataLoaded, Func<string> read)
        => (M.DadAdsRepairObservation)typeof(S.DadDutySupportAdsService)
            .GetMethod("ReadRepairStatus", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [metadataLoaded, read])!;

    private static M.DadRepairRetrySafetyObservation NativeSettlement(uint territory, uint nativeTerritory, ContentsFinderQueueState? queue)
        => (M.DadRepairRetrySafetyObservation)typeof(S.DadDutySupportAdsService)
            .GetMethod("ReadRepairRetryNativeSettlement", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [territory, nativeTerritory, queue])!;

    private static S.DadDutySupportAdsService RepairService(Func<string, bool> invoke)
    {
        var endpoint = DispatchProxy.Create<ICallGateSubscriber<string, bool>, RepairStartProxy>();
        ((RepairStartProxy)endpoint).Call = invoke;
        var service = (S.DadDutySupportAdsService)RuntimeHelpers.GetUninitializedObject(typeof(S.DadDutySupportAdsService));
        typeof(S.DadDutySupportAdsService).GetField("startRepair", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, endpoint);
        return service;
    }

    public class RepairStartProxy : DispatchProxy
    {
        public Func<string, bool> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == "InvokeFunc" ? Call((string)args![0]!) : throw new NotSupportedException(method?.Name);
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
