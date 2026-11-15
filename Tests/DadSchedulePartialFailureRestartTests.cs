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

public sealed class DadSchedulePartialFailureRestartTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DebugSettingDefaultsOffAndCloneAndJsonPreserveExplicitOperatorChoice()
    {
        Assert.False(new DadScheduleDefinition().RestartOnPartialFailure);
        Assert.False(JsonSerializer.Deserialize<DadScheduleDefinition>("{}")!.RestartOnPartialFailure);
        var schedule = new DadScheduleDefinition { RestartOnPartialFailure = true, PartialFailureRestartLimit = 7 };
        Assert.True(schedule.Clone().RestartOnPartialFailure);
        var roundTrip = JsonSerializer.Deserialize<DadScheduleDefinition>(JsonSerializer.Serialize(schedule))!;
        Assert.True(roundTrip.RestartOnPartialFailure);
        Assert.Equal(7, roundTrip.PartialFailureRestartLimit);
        Assert.Equal(20, new DadScheduleDefinition { PartialFailureRestartLimit = 999 }.Normalize().PartialFailureRestartLimit);
    }

    [Fact]
    public void RealSchedulerPreservesFailureThenRestartsTheWholeChainWithFreshIds()
    {
        var (scheduler, config, schedule, run) = Fixture();
        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, run, Now);
        Assert.True(config.ActiveScheduleRun.IsActive);
        Assert.Equal(1, config.ActiveScheduleRun.PartialFailureRestartCount);
        Assert.NotNull(config.ActiveScheduleRun.PartialFailureRestartPendingSinceUtc);
        Assert.Equal(1, config.ActiveScheduleRun.CurrentEntryIndex);
        Assert.False(Assert.Single(config.ScheduleHistory).Success);
        Assert.Empty(config.SchedulerQueue);

        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, run, Now.AddSeconds(31));
        var restarted = config.ActiveScheduleRun;
        Assert.True(restarted.IsActive);
        Assert.NotEqual("original-schedule-run", restarted.RunId);
        Assert.Equal("original-schedule-run", restarted.RetriedFromRunId);
        Assert.Equal(1, restarted.PartialFailureRestartCount);
        Assert.Equal(0, restarted.CurrentEntryIndex);
        Assert.Equal(0, restarted.CompletedEntryExecutions);
        Assert.Empty(restarted.ActivePlannerRequestId);
        Assert.Empty(restarted.ActiveSchedulerJobId);
        Assert.Null(restarted.PartialFailureRestartPendingSinceUtc);
        Assert.Single(config.ScheduleHistory);
        Assert.Empty(config.SchedulerQueue); // Normal Update materializes the first fresh ordinary job.
    }

    [Fact]
    public void RealRestartWaitsForPartyAndCoordinatorCleanupAndOrdinaryAdmission()
    {
        var (scheduler, config, schedule, run) = Fixture();
        var cleanup = true;
        Invoke(scheduler, "ConfigureScheduledRunCancellation", (Action<string>)(_ => { }),
            (Func<string, bool>)(_ => cleanup));
        scheduler.ConfigureAdmissionBlocker(() => cleanup ? "Synthetic cleanup" : string.Empty);
        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, run, Now);
        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, run, Now.AddSeconds(31));
        Assert.Equal("original-schedule-run", config.ActiveScheduleRun.RunId);
        cleanup = false;
        run.CurrentExecutorStatus.DutyExitPartyTeardownCompleted = false;
        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, run, Now.AddSeconds(32));
        Assert.Equal("original-schedule-run", config.ActiveScheduleRun.RunId);
        run.CurrentExecutorStatus.DutyExitPartyTeardownCompleted = true;
        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, run, Now.AddSeconds(33));
        Assert.NotEqual("original-schedule-run", config.ActiveScheduleRun.RunId);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("limit")]
    [InlineData("off")]
    [InlineData("dry-run")]
    [InlineData("completed-duty")]
    public void IneligibleFailuresNeverReserveAWholeScheduleRestart(string scenario)
    {
        var (scheduler, config, schedule, run) = Fixture();
        switch (scenario)
        {
            case "cancel":
                run.Status = DadRuntime::dad.Models.DadRunStatus.Cancelled;
                run.ScheduleFailureKind = DadRuntime::dad.Models.DadScheduleFailureKind.Cancellation;
                break;
            case "limit": config.ActiveScheduleRun.PartialFailureRestartCount = 3; break;
            case "off": schedule.RestartOnPartialFailure = false; break;
            case "dry-run": config.ActiveScheduleRun.DryRun = true; break;
            case "completed-duty": run.StopProgress.CompletedRuns = 1; break;
        }
        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, run, Now);
        Assert.Null(config.ActiveScheduleRun.PartialFailureRestartPendingSinceUtc);
        Assert.Equal("original-schedule-run", config.ActiveScheduleRun.RunId);
    }

    [Fact]
    public void StopAllCancelsThePersistedRestartReservationWithoutRearmingIt()
    {
        var (scheduler, config, schedule, run) = Fixture();
        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, run, Now);
        scheduler.StopAll("Synthetic manual stop", TimeSpan.FromSeconds(2));
        Invoke(scheduler, "TryProcessWaitingDadRun", schedule, run, Now.AddMinutes(2));
        Assert.False(config.ActiveScheduleRun.IsActive);
        Assert.Equal(DadRuntime::dad.Models.DadScheduleRunStatus.Cancelled, config.ActiveScheduleRun.Status);
        Assert.Equal("original-schedule-run", config.ActiveScheduleRun.RunId);
        Assert.Empty(config.SchedulerQueue);
    }

    [Fact]
    public void LostSettingsOrOverlongCleanupNeverRestart()
    {
        foreach (var timeout in new[] { false, true })
        {
            var (scheduler, config, schedule, run) = Fixture();
            Invoke(scheduler, "TryProcessWaitingDadRun", schedule, run, Now);
            if (!timeout) schedule.RestartOnPartialFailure = false;
            Invoke(scheduler, "TryProcessWaitingDadRun", schedule, run, Now.AddMinutes(timeout ? 16 : 1));
            Assert.False(config.ActiveScheduleRun.IsActive);
            Assert.Equal("original-schedule-run", config.ActiveScheduleRun.RunId);
            Assert.Empty(config.SchedulerQueue);
        }
    }

    private static (Scheduler, DadRuntime::dad.Configuration, RuntimeSchedule, RuntimeRun) Fixture()
    {
        var config = new DadRuntime::dad.Configuration { RunAsServerDad = true, PluginEnabled = true };
        var scheduler = new Scheduler(config, null!, null!, null!, null!, null!, null!, null!,
            DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>());
        var schedule = new RuntimeSchedule
        {
            ScheduleId = "schedule", Revision = 1, RestartOnPartialFailure = true,
            Entries = [new() { EntryId = "first", GroupId = "first" }, new() { EntryId = "second", GroupId = "second" }],
        };
        config.Schedules.Add(schedule);
        config.PlannerGroups.Add(new() { GroupId = "first", DisplayName = "First" });
        config.PlannerGroups.Add(new() { GroupId = "second", DisplayName = "Second" });
        config.ActiveScheduleRun = new RuntimeState
        {
            RunId = "original-schedule-run", ScheduleId = "schedule", ScheduleRevisionAtStart = 1,
            Status = DadRuntime::dad.Models.DadScheduleRunStatus.Running,
            Phase = DadRuntime::dad.Models.DadScheduleRunPhase.WaitingForDadRun,
            CurrentEntryIndex = 1, CurrentEntryId = "second", CurrentGroupId = "second",
            ActivePlannerRequestId = "failed-run", TotalEntryExecutions = 2, CompletedEntryExecutions = 1,
        };
        return (scheduler, config, schedule, DadScheduleDutyExitRetryRuntimeTests.FailedRun());
    }

    private static void Invoke(Scheduler scheduler, string method, params object[] arguments)
        => typeof(Scheduler).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(scheduler, arguments);
}
