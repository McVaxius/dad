extern alias DadRuntime;
extern alias DalamudApi;

using System.Reflection;
using System.Text.Json;
using DalamudApi::Dalamud.Plugin.Services;
using dad.Models;
using Xunit;
using Scheduler = DadRuntime::dad.Services.DadSchedulerService;
using RuntimeState = DadRuntime::dad.Models.DadScheduleRunState;
using RuntimeRun = DadRuntime::dad.Models.DadRunResult;
using RuntimeSchedule = DadRuntime::dad.Models.DadScheduleDefinition;

namespace dad.Tests;

public sealed class DadScheduleDutyExitRetryRuntimeTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RealSchedulerReservesFirstExitFailureAndBlocksSecondWithoutCompletingEntry()
    {
        var (scheduler, config, schedule) = Fixture();
        var run = FailedRun();
        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, run, Now);
        Assert.Equal(1, config.ActiveScheduleRun.DutyExitRetryCount);
        Assert.NotNull(config.ActiveScheduleRun.DutyExitRetrySource);
        Assert.Empty(config.SchedulerQueue);
        Assert.Empty(config.ScheduleHistory);
        Assert.Equal(0, config.ActiveScheduleRun.CompletedEntryExecutions);

        // Simulate the ordinary scheduler having admitted the reserved fresh request.
        config.ActiveScheduleRun.ActivePlannerRequestId = "retry-run";
        run.RequestId = "retry-run";
        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, run, Now.AddMinutes(1));
        Assert.Equal(DadRuntime::dad.Models.DadScheduleRunStatus.Blocked, config.ActiveScheduleRun.Status);
        Assert.Equal(1, config.ActiveScheduleRun.DutyExitRetryCount);
        Assert.Equal(0, config.ActiveScheduleRun.CompletedEntryExecutions);
        Assert.Single(config.ScheduleHistory);
        Assert.Contains("already consumed", config.ActiveScheduleRun.Summary);
        Assert.Empty(config.SchedulerQueue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealSchedulerAdvancesOneEntryWhenNormalRetryPreflightSkipsAfterProvenPartyTeardown(bool dailyReward)
    {
        var (scheduler, config, schedule) = Fixture();
        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, FailedRun(), Now);
        var state = config.ActiveScheduleRun;
        state.ActiveSchedulerJobId = "retry-job";
        state.Phase = DadRuntime::dad.Models.DadScheduleRunPhase.WaitingForScheduler;
        config.SchedulerHistory.Add(new()
        {
            JobId = "retry-job", Success = true,
            FinalPhase = DadRuntime::dad.Models.DadSchedulerPresetPhase.Skipped,
            SkipKind = dailyReward ? DadRuntime::dad.Models.DadSchedulerSkipKind.DailyRouletteReward
                : DadRuntime::dad.Models.DadSchedulerSkipKind.LevelSeek,
        });
        Invoke(scheduler, "ProcessScheduleSchedulerJob", schedule, state, RuntimeRun.Idle(), Now.AddSeconds(2));
        Assert.Equal(1, config.ActiveScheduleRun.CompletedEntryExecutions);
        Assert.Equal(1, config.ActiveScheduleRun.SkippedEntryExecutions);
        Assert.Equal(1, config.ActiveScheduleRun.CurrentEntryIndex);
        Assert.Equal(0, config.ActiveScheduleRun.DutyExitRetryCount);
        Assert.Null(config.ActiveScheduleRun.DutyExitRetrySource);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealSchedulerNeverAdvancesRetrySkipWithoutPartyTeardownProof(bool dailyReward)
    {
        var (scheduler, config, schedule) = Fixture();
        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, FailedRun(), Now);
        var state = config.ActiveScheduleRun;
        state.DutyExitRetrySource!.CurrentExecutorStatus.DutyExitPartyTeardownCompleted = false;
        state.ActiveSchedulerJobId = "retry-job";
        state.Phase = DadRuntime::dad.Models.DadScheduleRunPhase.WaitingForScheduler;
        config.SchedulerHistory.Add(new()
        {
            JobId = "retry-job", Success = true,
            FinalPhase = DadRuntime::dad.Models.DadSchedulerPresetPhase.Skipped,
            SkipKind = dailyReward ? DadRuntime::dad.Models.DadSchedulerSkipKind.DailyRouletteReward
                : DadRuntime::dad.Models.DadSchedulerSkipKind.LevelSeek,
        });
        Invoke(scheduler, "ProcessScheduleSchedulerJob", schedule, state, RuntimeRun.Idle(), Now.AddSeconds(2));
        Assert.Equal(DadRuntime::dad.Models.DadScheduleRunStatus.Blocked, config.ActiveScheduleRun.Status);
        Assert.Equal(0, config.ActiveScheduleRun.CompletedEntryExecutions);
        Assert.Equal(0, config.ActiveScheduleRun.SkippedEntryExecutions);
        Assert.Equal(0, config.ActiveScheduleRun.CurrentEntryIndex);
        Assert.Contains("teardown", config.ActiveScheduleRun.Summary);
    }

    [Fact]
    public void OrdinaryPreflightSkipDoesNotNeedOrRequestPartyTeardown()
    {
        var (scheduler, config, schedule) = Fixture();
        var state = config.ActiveScheduleRun;
        state.ActiveSchedulerJobId = "ordinary-job";
        config.SchedulerHistory.Add(new()
        {
            JobId = "ordinary-job", Success = true,
            FinalPhase = DadRuntime::dad.Models.DadSchedulerPresetPhase.Skipped,
            SkipKind = DadRuntime::dad.Models.DadSchedulerSkipKind.LevelSeek,
        });
        Invoke(scheduler, "ProcessScheduleSchedulerJob", schedule, state, RuntimeRun.Idle(), Now);
        Assert.Equal(1, config.ActiveScheduleRun.CurrentEntryIndex);
        Assert.Equal(1, config.ActiveScheduleRun.SkippedEntryExecutions);
    }

    [Fact]
    public void RealSchedulerMaterializesANewOrdinaryJobWithoutReusingFailedRequest()
    {
        var (scheduler, config, schedule) = Fixture();
        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, FailedRun(), Now);
        var state = config.ActiveScheduleRun;
        var oldJobId = state.ActiveSchedulerJobId;
        config.PlannerGroups.Add(new() { GroupId = "preset", DisplayName = "Sastasha" });
        Invoke(scheduler, "MaterializeScheduleEntryJob", schedule, state, Now.AddSeconds(1));
        var job = Assert.Single(config.SchedulerQueue);
        Assert.NotEqual(oldJobId, job.JobId);
        Assert.Equal(DadRuntime::dad.Models.DadSchedulerJobType.ScheduledPreset, job.JobType);
        Assert.Equal("schedule-run", job.ScheduleRunId);
        Assert.Equal("entry", job.ScheduleEntryId);
        Assert.Equal(1, job.ScheduleRepeatIteration);
        Assert.Empty(state.ActivePlannerRequestId);
        Assert.Equal(1, state.DutyExitRetryCount);
        Assert.Equal(Now, state.DutyExitRetryPendingSinceUtc);
        Assert.Equal(0, state.CompletedEntryExecutions);
    }

    [Fact]
    public void RealSchedulerDoesNotRetryCancellationEvenWithOldExitEvidence()
    {
        var (scheduler, config, schedule) = Fixture();
        var run = FailedRun();
        run.Status = DadRuntime::dad.Models.DadRunStatus.Cancelled;
        run.ScheduleFailureKind = DadRuntime::dad.Models.DadScheduleFailureKind.Cancellation;
        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, run, Now);
        Assert.Equal(0, config.ActiveScheduleRun.DutyExitRetryCount);
        Assert.Equal(DadRuntime::dad.Models.DadScheduleFailureKind.Cancellation, config.ActiveScheduleRun.FailureKind);
        Assert.Empty(config.SchedulerQueue);
    }

    [Fact]
    public void QueuedAutomaticRetryCannotStartAfterItsScheduleWasBlocked()
    {
        var (scheduler, config, schedule) = Fixture();
        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, FailedRun(), Now);
        config.ActiveScheduleRun.Status = DadRuntime::dad.Models.DadScheduleRunStatus.Blocked;
        config.ActiveScheduleRun.Phase = DadRuntime::dad.Models.DadScheduleRunPhase.Blocked;
        var job = new DadRuntime::dad.Models.DadScheduledCrewJob { GroupId = "preset", Enabled = true };
        config.ActiveScheduleRun.ActiveSchedulerJobId = job.JobId;
        config.SchedulerQueue.Add(job);
        var callbacks = 0;
        Func<string, DadRuntime::dad.Models.DadPlannerGroup?> group = _ => { callbacks++; return null; };
        Func<string, DadRuntime::dad.Models.DadPlannerRunRequestPreview?> preview = _ => { callbacks++; return null; };
        Invoke(scheduler, "TryStartNextQueuedJob", group, preview);
        Assert.Empty(config.SchedulerQueue);
        Assert.Equal(0, callbacks);
    }

    private static (Scheduler, DadRuntime::dad.Configuration, RuntimeSchedule) Fixture()
    {
        var config = new DadRuntime::dad.Configuration { RunAsServerDad = true };
        var scheduler = new Scheduler(config, null!, null!, null!, null!, null!, null!, null!,
            DispatchProxy.Create<IPluginLog, QuietLog>());
        var schedule = new RuntimeSchedule
        {
            ScheduleId = "schedule", Revision = 1,
            Entries = [new() { EntryId = "entry", GroupId = "preset" }, new() { EntryId = "next", GroupId = "next" }],
        };
        config.Schedules.Add(schedule);
        config.ActiveScheduleRun = new RuntimeState
        {
            RunId = "schedule-run", ScheduleId = "schedule", ScheduleRevisionAtStart = 1,
            Status = DadRuntime::dad.Models.DadScheduleRunStatus.Running,
            Phase = DadRuntime::dad.Models.DadScheduleRunPhase.WaitingForDadRun,
            CurrentEntryId = "entry", CurrentGroupId = "preset", ActivePlannerRequestId = "failed-run", TotalEntryExecutions = 2,
        };
        return (scheduler, config, schedule);
    }

    internal static RuntimeRun FailedRun()
    {
        var rows = Enumerable.Range(1, 4).Select(i => new DadRosterCharacterRef
        {
            AccountKey = new($"account-{i}"), CharacterKey = new($"character-{i}"), ContentId = (ulong)i,
        }).ToList();
        var run = new DadRunResult
        {
            RequestId = "failed-run", Status = DadRunStatus.PartialFailure, ModuleId = DadModuleId.PremadeDuty,
            ScheduleFailureKind = DadScheduleFailureKind.EntryTerminalFailure, CompletedAtUtc = Now,
            CurrentExecutorStatus = new() { DutyExitedWithoutCompletion = true, DutyExitPartyTeardownCompleted = true },
            Request = new() { PremadeDuty = new() { ContentFinderConditionId = 4 },
                Orchestration = new() { RequiredRosterCharacters = rows } },
            Participants = rows.Select((row, i) => new DadParticipantSnapshot
            {
                WorkerSessionId = new($"worker-{i}"), ManagedAccountKey = row.AccountKey, ActiveCharacterKey = row.CharacterKey,
                Character = new() { ContentId = row.ContentId, CharacterKey = row.CharacterKey.Value },
            }).ToList(),
        };
        return JsonSerializer.Deserialize<RuntimeRun>(JsonSerializer.Serialize(run))!;
    }

    private static void Invoke(Scheduler scheduler, string method, params object[] arguments)
        => typeof(Scheduler).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(scheduler, arguments);

    public class QuietLog : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.ReturnType == typeof(void) ? null : Activator.CreateInstance(targetMethod.ReturnType);
    }
}
