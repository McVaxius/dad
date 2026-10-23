extern alias DadRuntime;
extern alias DalamudApi;

using System.Reflection;
using System.Runtime.CompilerServices;
using DalamudApi::Dalamud.Plugin.Services;
using Xunit;
using Coordinator = DadRuntime::dad.Services.DadCoordinatorService;
using Presence = DadRuntime::dad.Services.DadPresenceService;
using Transport = DadRuntime::dad.Services.DadTransportService;
using M = DadRuntime::dad.Models;

namespace dad.Tests;

public sealed class DadDutyExitRetryTeardownRuntimeTests
{
    [Fact]
    public void RealCoordinatorSettlesAllFourTeardownsBeforePublishingRetryEvidence()
    {
        var coordinator = Fixture(allTeardownsSucceeded: true);
        var failure = new M.DadRunStepResultDto { FailureReason = "Duty exited without completion.", Summary = "Abandoned duty." };
        Assert.True((bool)Invoke(coordinator, "TryBeginDutyExitRetryTeardown", failure)!);
        Assert.Equal(M.DadRunPhase.Finalizing, coordinator.CurrentResult.Phase);
        Assert.False(coordinator.CurrentResult.IsTerminal);
        Assert.False(coordinator.CurrentResult.CurrentExecutorStatus.DutyExitPartyTeardownCompleted);
        Assert.True((bool)Get(coordinator, "successfulPartyBoundarySettled")!);

        // Stop at the existing worker-cancellation boundary: this headless test
        // neither sends game commands nor pretends to acknowledge native cleanup.
        Set(coordinator, "localWorkerFinalizationPending", true);
        Invoke(coordinator, "UpdateDutyExitRetryTeardown");
        Assert.True(coordinator.CurrentResult.CurrentExecutorStatus.DutyExitPartyTeardownCompleted);
        Assert.True(coordinator.CurrentResult.CurrentExecutorStatus.DutyExitedWithoutCompletion);
        Assert.NotEqual(M.DadRunStatus.Completed, coordinator.CurrentResult.Status);
        var pending = Get(coordinator, "pendingDutyExitFailure")!;
        Assert.Equal(M.DadRunStatus.PartialFailure, pending.GetType().GetProperty("Status")!.GetValue(pending));
    }

    [Fact]
    public void RealCoordinatorDoesNotPublishRetryProofWhenAnyFollowerTeardownFails()
    {
        var coordinator = Fixture(allTeardownsSucceeded: false);
        Set(coordinator, "localWorkerFinalizationPending", true);
        Assert.True((bool)Invoke(coordinator, "TryBeginDutyExitRetryTeardown", new M.DadRunStepResultDto())!);
        Assert.False((bool)Get(coordinator, "successfulPartyBoundarySettled")!);
        Assert.False(coordinator.CurrentResult.CurrentExecutorStatus.DutyExitPartyTeardownCompleted);
        Assert.NotEqual(M.DadRunStatus.Completed, coordinator.CurrentResult.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealCoordinatorDoesNotStartRecoveryTeardownForUnrelatedOrContradictoryFailures(bool unknownLeader)
    {
        var coordinator = Fixture(allTeardownsSucceeded: true);
        if (unknownLeader)
            coordinator.CurrentResult.ScheduleFailureKind = M.DadScheduleFailureKind.MissingOrUnknownLeaderState;
        else
            coordinator.CurrentResult.CurrentExecutorStatus.DutyExitedWithoutCompletion = false;
        Assert.False((bool)Invoke(coordinator, "TryBeginDutyExitRetryTeardown", new M.DadRunStepResultDto())!);
        Assert.Null(Get(coordinator, "pendingDutyExitFailure"));
        Assert.False((bool)Get(coordinator, "successfulPartyBoundarySettled")!);
    }

    internal static Coordinator Fixture(bool allTeardownsSucceeded)
    {
        var config = new DadRuntime::dad.Configuration { RunAsServerDad = true };
        var presence = (Presence)RuntimeHelpers.GetUninitializedObject(typeof(Presence));
        Set(presence, "<WorkerSessionId>k__BackingField", new M.DadWorkerSessionId("worker-0"));
        var transport = (Transport)RuntimeHelpers.GetUninitializedObject(typeof(Transport));
        Set(transport, "<CurrentTransport>k__BackingField", new M.DadPeerTransportSnapshot());
        var log = DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>();
        var constructor = typeof(Coordinator).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var args = constructor.GetParameters().Select(parameter => parameter.Name switch
        {
            "configuration" => (object)config,
            "presenceService" => presence,
            "transportService" => transport,
            "claimService" => new DadRuntime::dad.Services.DadClaimService(),
            "log" => log,
            _ => null,
        }).ToArray();
        var coordinator = (Coordinator)constructor.Invoke(args);
        var run = DadScheduleDutyExitRetryRuntimeTests.FailedRun();
        run.Status = M.DadRunStatus.Running;
        run.Phase = M.DadRunPhase.TearingDownParty;
        run.CompletedAtUtc = null;
        run.CurrentExecutorStatus.DutyExitPartyTeardownCompleted = false;
        typeof(Coordinator).GetProperty("CurrentResult")!.SetValue(coordinator, run);
        config.ActiveScheduleRun = new()
        {
            Status = M.DadScheduleRunStatus.Running, Phase = M.DadScheduleRunPhase.WaitingForDadRun,
            ActivePlannerRequestId = run.RequestId,
        };
        Set(coordinator, "activePlan", new M.DadRunPlan
        {
            Request = run.Request!, Orchestration = run.Request!.Orchestration,
            CompositeModuleId = M.DadModuleId.PremadeDuty, RequiredParticipantCount = 4,
        });
        var manifestType = typeof(Coordinator).Assembly.GetType("dad.Models.DadRunSlotManifest")!;
        Set(coordinator, "activeSlotManifest", Activator.CreateInstance(manifestType)!);
        var instructions = Enumerable.Range(1, 4).Select(index => new M.DadAssemblyInstructionDto
        {
            RunId = run.RequestId, SlotId = $"Slot{index}",
            InstructionKind = index == 1 ? M.DadAssemblyInstructionKind.DisbandParty : M.DadAssemblyInstructionKind.LeaveParty,
        }).ToList();
        Set(coordinator, "activePartyTeardownInstructions", instructions);
        var results = (Dictionary<string, M.DadRunStepResultDto>)Get(coordinator, "partyTeardownTerminalResults")!;
        foreach (var instruction in instructions)
            results[instruction.SlotId] = new()
            {
                RunId = run.RequestId, Success = allTeardownsSucceeded || instruction.SlotId != "Slot4",
                Summary = "Exact native teardown response.",
            };
        return coordinator;
    }

    private static object? Invoke(object instance, string method, params object[] arguments)
        => instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, arguments);
    private static object? Get(object instance, string field)
        => instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
    private static void Set(object instance, string field, object value)
        => instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
}
