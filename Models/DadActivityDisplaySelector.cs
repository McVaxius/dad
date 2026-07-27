namespace dad.Models;

internal enum DadActivityDisplaySource
{
    ExistingDadState = 0,
    VisibleDadRun = 1,
    AuthorityDadRun = 2,
    LocalDadRun = 3,
    Scheduler = 4,
    Schedule = 5,
}

internal readonly record struct DadActivityDisplaySelection(
    DadRunResult Run,
    DadActivityDisplaySource Source);

internal static class DadActivityDisplaySelector
{
    public static DadActivityDisplaySelection Select(
        DadVisibleRunState runState,
        DadSchedulerPresetState scheduler,
        DadScheduleRunState schedule)
    {
        if (IsBusy(runState.VisibleRun))
            return SelectRun(runState.VisibleRun, DadActivityDisplaySource.VisibleDadRun);
        if (IsBusy(runState.AuthorityRun))
            return SelectRun(runState.AuthorityRun, DadActivityDisplaySource.AuthorityDadRun);
        if (IsBusy(runState.LocalRun))
            return SelectRun(runState.LocalRun, DadActivityDisplaySource.LocalDadRun);
        if (scheduler?.IsActive == true)
            return new DadActivityDisplaySelection(ProjectScheduler(scheduler), DadActivityDisplaySource.Scheduler);
        if (schedule?.IsActive == true)
            return new DadActivityDisplaySelection(ProjectSchedule(schedule), DadActivityDisplaySource.Schedule);

        return SelectRun(SelectExisting(runState), DadActivityDisplaySource.ExistingDadState);
    }

    private static DadActivityDisplaySelection SelectRun(
        DadRunResult? run,
        DadActivityDisplaySource source)
        => new((run ?? DadRunResult.Idle()).Clone(), source);

    private static DadRunResult SelectExisting(DadVisibleRunState runState)
        => runState.VisibleRun.Status != DadRunStatus.Idle
            ? runState.VisibleRun
            : runState.AuthorityRun.Status != DadRunStatus.Idle
                ? runState.AuthorityRun
                : runState.LocalRun;

    private static DadRunResult ProjectScheduler(DadSchedulerPresetState scheduler)
        => new()
        {
            RequestId = FirstNonEmpty(scheduler.JobId, scheduler.SchedulerRunId),
            Status = DadRunStatus.Running,
            Phase = DadRunPhase.WaitingForReadiness,
            RequestedBy = scheduler.RequestedBy,
            ActiveTaskName = FirstNonEmpty(scheduler.PresetName, "Scheduler"),
            ActiveTaskStatus = FirstNonEmpty(scheduler.Summary, scheduler.Phase.ToString()),
            Summary = FirstNonEmpty(
                scheduler.Summary,
                $"Scheduler is running {FirstNonEmpty(scheduler.PresetName, scheduler.JobId, "active work")}."),
        };

    private static DadRunResult ProjectSchedule(DadScheduleRunState schedule)
        => new()
        {
            RequestId = schedule.RunId,
            Status = DadRunStatus.Running,
            Phase = DadRunPhase.Planning,
            RequestedBy = schedule.RequestedBy,
            RequestedTaskCount = Math.Max(0, schedule.TotalEntryExecutions),
            CompletedTaskCount = Math.Max(0, schedule.CompletedEntryExecutions),
            ActiveTaskIndex = Math.Max(0, schedule.CurrentEntryIndex + 1),
            TotalTaskCount = Math.Max(0, schedule.TotalEntryExecutions),
            ActiveTaskName = FirstNonEmpty(schedule.CurrentPresetName, schedule.ScheduleName, "Schedule"),
            ActiveTaskStatus = FirstNonEmpty(schedule.Summary, schedule.Phase.ToString()),
            Summary = FirstNonEmpty(
                schedule.Summary,
                $"Schedule {FirstNonEmpty(schedule.ScheduleName, schedule.RunId, "run")} is active between entries."),
        };

    private static bool IsBusy(DadRunResult? run)
        => run?.Status is DadRunStatus.Queued or DadRunStatus.WaitingForParticipants or DadRunStatus.Running;

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
