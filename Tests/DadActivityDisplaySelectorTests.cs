using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadActivityDisplaySelectorTests
{
    [Fact]
    public void BusyVisibleRunWinsEveryOtherSource()
    {
        var selection = Select(
            visible: Run("visible", DadRunStatus.Running),
            authority: Run("authority", DadRunStatus.Running),
            local: Run("local", DadRunStatus.Running),
            schedulerActive: true,
            scheduleActive: true);

        Assert.Equal(DadActivityDisplaySource.VisibleDadRun, selection.Source);
        Assert.Equal("visible", selection.Run.RequestId);
    }

    [Fact]
    public void BusyAuthorityRunWinsLocalSchedulerAndSchedule()
    {
        var selection = Select(
            visible: Run("visible-terminal", DadRunStatus.Completed),
            authority: Run("authority", DadRunStatus.WaitingForParticipants),
            local: Run("local", DadRunStatus.Running),
            schedulerActive: true,
            scheduleActive: true);

        Assert.Equal(DadActivityDisplaySource.AuthorityDadRun, selection.Source);
        Assert.Equal("authority", selection.Run.RequestId);
    }

    [Fact]
    public void BusyLocalRunWinsSchedulerAndSchedule()
    {
        var selection = Select(
            visible: Run("visible-terminal", DadRunStatus.Completed),
            authority: Run("authority-terminal", DadRunStatus.Failed),
            local: Run("local", DadRunStatus.Queued),
            schedulerActive: true,
            scheduleActive: true);

        Assert.Equal(DadActivityDisplaySource.LocalDadRun, selection.Source);
        Assert.Equal("local", selection.Run.RequestId);
    }

    [Fact]
    public void ActiveSchedulerProjectsRunningAndWinsActiveSchedule()
    {
        var selection = Select(
            visible: Run("visible-terminal", DadRunStatus.Completed),
            authority: DadRunResult.Idle(),
            local: DadRunResult.Idle(),
            schedulerActive: true,
            scheduleActive: true);

        Assert.Equal(DadActivityDisplaySource.Scheduler, selection.Source);
        Assert.Equal(DadRunStatus.Running, selection.Run.Status);
        Assert.Equal("scheduler-job", selection.Run.RequestId);
        Assert.Equal("Scheduler summary.", selection.Run.Summary);
    }

    [Fact]
    public void ActiveScheduleProjectsRunningWhenSchedulerIsInactive()
    {
        var selection = Select(
            visible: Run("visible-terminal", DadRunStatus.Cancelled),
            authority: DadRunResult.Idle(),
            local: DadRunResult.Idle(),
            schedulerActive: false,
            scheduleActive: true);

        Assert.Equal(DadActivityDisplaySource.Schedule, selection.Source);
        Assert.Equal(DadRunStatus.Running, selection.Run.Status);
        Assert.Equal("schedule-run", selection.Run.RequestId);
        Assert.Equal("Schedule summary.", selection.Run.Summary);
    }

    [Fact]
    public void ExistingTerminalRunIsPreservedWhenNoWorkIsActive()
    {
        var selection = Select(
            visible: Run("terminal", DadRunStatus.PartialFailure),
            authority: DadRunResult.Idle(),
            local: DadRunResult.Idle(),
            schedulerActive: false,
            scheduleActive: false);

        Assert.Equal(DadActivityDisplaySource.ExistingDadState, selection.Source);
        Assert.Equal("terminal", selection.Run.RequestId);
        Assert.Equal(DadRunStatus.PartialFailure, selection.Run.Status);
    }

    [Fact]
    public void ExistingIdleRunIsPreservedWhenNothingElseIsVisible()
    {
        var selection = Select(
            visible: DadRunResult.Idle(),
            authority: DadRunResult.Idle(),
            local: DadRunResult.Idle(),
            schedulerActive: false,
            scheduleActive: false);

        Assert.Equal(DadActivityDisplaySource.ExistingDadState, selection.Source);
        Assert.Equal(DadRunStatus.Idle, selection.Run.Status);
    }

    private static DadActivityDisplaySelection Select(
        DadRunResult visible,
        DadRunResult authority,
        DadRunResult local,
        bool schedulerActive,
        bool scheduleActive)
    {
        var runState = new DadVisibleRunState(
            local,
            authority,
            visible,
            IsRemoteAuthorityView: false,
            new DadAuthorityViewState());
        var scheduler = new DadSchedulerPresetState
        {
            JobId = "scheduler-job",
            PresetName = "Scheduler preset",
            Phase = schedulerActive
                ? DadSchedulerPresetPhase.WaitingForDependencies
                : DadSchedulerPresetPhase.Idle,
            Summary = "Scheduler summary.",
        };
        var schedule = new DadScheduleRunState
        {
            RunId = "schedule-run",
            ScheduleName = "Daily schedule",
            Status = scheduleActive ? DadScheduleRunStatus.Running : DadScheduleRunStatus.Idle,
            Phase = scheduleActive ? DadScheduleRunPhase.StartingEntry : DadScheduleRunPhase.Idle,
            Summary = "Schedule summary.",
        };

        return DadActivityDisplaySelector.Select(runState, scheduler, schedule);
    }

    private static DadRunResult Run(string requestId, DadRunStatus status)
        => new()
        {
            RequestId = requestId,
            Status = status,
            Summary = requestId,
        };
}
