extern alias DadRuntime;
extern alias DalamudApi;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Linq.Expressions;
using DalamudApi::Dalamud.Plugin.Services;
using Xunit;
using Scheduler = DadRuntime::dad.Services.DadSchedulerService;
using M = DadRuntime::dad.Models;

namespace dad.Tests;

public sealed class DadAnyJobStartReadinessRuntimeTests
{
    [Fact]
    public void FinalStartReadinessLossWaitsWithoutRecordingTerminalJobOrConsumingStartClaim()
    {
        var (scheduler, config, state, request) = Fixture();
        var calls = 0;
        Func<M.DadRunRequest, M.DadRunResult> start = attempt =>
        {
            calls++;
            return M.DadRunResult.StartReadinessPending(attempt, "Exact Any job character is temporarily not world-ready.");
        };
        Tick(scheduler, request, start);
        Assert.Equal(1, calls);
        Assert.Equal(M.DadSchedulerPresetPhase.WaitingForHeartbeat, state.Phase);
        Assert.False(state.PlannerStarted);
        Assert.Null(state.CompletedAtUtc);
        Assert.Empty(config.SchedulerHistory);
        // The existing refresh cadence must prevent command dispatch every frame.
        Tick(scheduler, request, start);
        Assert.Equal(1, calls);
        Set(scheduler, "nextRefreshUtc", DateTime.MinValue);
        Tick(scheduler, request, attempt =>
        {
            calls++;
            Assert.Equal(request.RequestId, attempt.RequestId);
            return M.DadRunResult.FromRequest(attempt, M.DadRunStatus.WaitingForParticipants, "Accepted after readiness recovered.");
        });
        Assert.Equal(2, calls);
        Assert.True(state.PlannerStarted);
        Assert.Equal(M.DadSchedulerPresetPhase.StartedPlanner, state.Phase);
        Assert.Single(config.SchedulerHistory);
    }

    [Fact]
    public void RepeatedPreAdmissionReadinessWaitsDoNotAdvanceScheduleOrSpendDutyRetryBudget()
    {
        var (scheduler, config, state, request) = Fixture();
        var schedule = new M.DadScheduleDefinition
        {
            ScheduleId = "schedule", Revision = 1,
            Entries = [new() { EntryId = "entry", GroupId = "preset" }],
        };
        config.Schedules.Add(schedule);
        config.ActiveScheduleRun = new()
        {
            RunId = "schedule-run", ScheduleId = "schedule", ScheduleRevisionAtStart = 1,
            Status = M.DadScheduleRunStatus.Running, Phase = M.DadScheduleRunPhase.WaitingForScheduler,
            CurrentEntryId = "entry", CurrentGroupId = "preset", ActiveSchedulerJobId = "job",
        };
        var calls = 0;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            Set(scheduler, "nextRefreshUtc", DateTime.MinValue);
            Tick(scheduler, request, frozen =>
            {
                calls++;
                return M.DadRunResult.StartReadinessPending(frozen, "Waiting for live readiness.");
            });
            Assert.Equal(M.DadSchedulerPresetPhase.WaitingForHeartbeat, state.Phase);
            Assert.Equal(M.DadScheduleRunStatus.Running, config.ActiveScheduleRun.Status);
            Assert.Equal(0, config.ActiveScheduleRun.CurrentEntryIndex);
            Assert.Equal(0, config.ActiveScheduleRun.CompletedEntryExecutions);
            Assert.Equal(0, config.ActiveScheduleRun.DutyExitRetryCount);
            Assert.Empty(config.SchedulerHistory);
            Assert.Empty(config.ScheduleHistory);
        }
        Assert.Equal(100, calls);
    }

    [Theory]
    [InlineData(0)] // generic rejection
    [InlineData(1)] // missing result
    [InlineData(2)] // uncertain exception
    public void OrdinaryRejectedOrUncertainStartStillStopsWithoutReplay(int kind)
    {
        var (scheduler, config, state, request) = Fixture();
        var calls = 0;
        Func<M.DadRunRequest, M.DadRunResult> start = frozen =>
        {
            calls++;
            return kind switch
            {
                0 => M.DadRunResult.Rejected(frozen, "A hard identity contradiction."),
                1 => null!,
                _ => throw new InvalidOperationException("Acceptance is uncertain."),
            };
        };
        Tick(scheduler, request, start);
        Assert.Equal(M.DadSchedulerPresetPhase.Blocked, state.Phase);
        Tick(scheduler, request, start);
        Assert.Equal(1, calls);
        Assert.Single(config.SchedulerHistory);
    }

    [Fact]
    public void ChangedStrictContractAfterWaitStopsInsteadOfStartingDifferentWork()
    {
        var (scheduler, _, state, request) = Fixture();
        var calls = 0;
        Func<M.DadRunRequest, M.DadRunResult> start = frozen =>
        {
            calls++;
            return M.DadRunResult.StartReadinessPending(frozen, "Waiting.");
        };
        Tick(scheduler, request, start);
        var changed = request.Clone();
        changed.PremadeDuty = new() { ContentFinderConditionId = 4 };
        Set(scheduler, "nextRefreshUtc", DateTime.MinValue);
        Tick(scheduler, changed, start);
        Assert.Equal(1, calls);
        Assert.Equal(M.DadSchedulerPresetPhase.Blocked, state.Phase);
        Assert.Contains("changed the frozen execution contract", state.BlockedReason);
    }

    [Fact]
    public void CancelDuringReadinessWaitDoesNotReplayPlannerStart()
    {
        var (scheduler, _, state, request) = Fixture();
        var calls = 0;
        Func<M.DadRunRequest, M.DadRunResult> start = frozen =>
        {
            calls++;
            return M.DadRunResult.StartReadinessPending(frozen, "Waiting.");
        };
        Tick(scheduler, request, start);
        scheduler.Cancel("Operator cancelled.");
        Set(scheduler, "nextRefreshUtc", DateTime.MinValue);
        Tick(scheduler, request, start);
        Assert.Equal(1, calls);
        Assert.Equal(M.DadSchedulerPresetPhase.Cancelled, state.Phase);
    }

    private static void Tick(Scheduler scheduler, M.DadRunRequest request,
        Func<M.DadRunRequest, M.DadRunResult> start)
    {
        Func<string, M.DadPlannerGroup?> group = _ => null;
        Func<string, M.DadPlannerRunRequestPreview?> preview = _ => new()
        {
            CanStart = true, CanSchedule = true, Request = request.Clone(),
        };
        Func<M.DadRunResult> visible = M.DadRunResult.Idle;
        scheduler.Update(group, preview, start, visible);
    }

    private static (Scheduler, DadRuntime::dad.Configuration, M.DadSchedulerPresetState, M.DadRunRequest) Fixture()
    {
        var config = new DadRuntime::dad.Configuration { RunAsServerDad = true, ClientAccountId = "account" };
        var intelligence = (DadRuntime::dad.Services.DadCharacterIntelligenceService)
            RuntimeHelpers.GetUninitializedObject(typeof(DadRuntime::dad.Services.DadCharacterIntelligenceService));
        Set(intelligence, "<CurrentPool>k__BackingField", new M.DadCharacterPool());
        var presence = (DadRuntime::dad.Services.DadPresenceService)
            RuntimeHelpers.GetUninitializedObject(typeof(DadRuntime::dad.Services.DadPresenceService));
        Set(presence, "configuration", config);
        Set(presence, "configManager", RuntimeHelpers.GetUninitializedObject(typeof(DadRuntime::dad.Services.ConfigManager)));
        var local = new M.DadParticipantSnapshot
        {
            WorkerSessionId = new("local-worker"), ManagedAccountKey = new("account"),
            ActiveCharacterKey = new("Local@World"), IsAvailable = true, WorldReadyStable = true,
            Character = new() { CharacterKey = "Local@World", ContentId = 1, Readiness = M.DadReadinessState.Ready },
        };
        Set(presence, "<CurrentParticipant>k__BackingField", local);
        Set(presence, "<WorkerSessionId>k__BackingField", local.WorkerSessionId);
        Set(presence, "<ClientInstanceId>k__BackingField", "local-worker");
        Set(presence, "dependencySnapshotProvider", (Func<M.DadDependencySnapshot>)(() => new()));
        var observationType = typeof(Scheduler).Assembly.GetType("dad.Services.DadLiveRuntimeObservation")!;
        var observation = Activator.CreateInstance(observationType, [true, local.Character.Clone(), false, null, "", null])!;
        Set(presence, "observeLiveRuntime", Expression.Lambda(typeof(Func<>).MakeGenericType(observationType),
            Expression.Constant(observation, observationType)).Compile());
        // This fixture fences the outer ready gates; production manifest tests below
        // separately exercise exact roster/identity and the late world-readiness loss.
        var scheduler = new Scheduler(config, null!, null!, intelligence, presence, null!, null!, null!,
            DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>());
        var request = new M.DadRunRequest { RequestId = "frozen-request" };
        var state = new M.DadSchedulerPresetState
        {
            SchedulerRunId = "scheduler", JobId = "job", GroupId = "preset", PresetName = "Any job preset",
            Phase = M.DadSchedulerPresetPhase.ReadyToStart, PlannerRequestId = request.RequestId,
        };
        Set(scheduler, "currentState", state);
        Set(scheduler, "activeJob", new M.DadScheduledCrewJob { JobId = "job", GroupId = "preset" });
        Set(scheduler, "frozenPlannerRequest", request);
        return (scheduler, config, state, request);
    }

    private static void Set(object instance, string field, object value)
        => instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
}
