namespace dad.Models;

public enum DadScheduleRestartAction { Wait, Restart, Block }
public readonly record struct DadScheduleRestartDecision(DadScheduleRestartAction Action, string Summary);

public static class DadSchedulePartialFailureRestartRules
{
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CleanupBudget = TimeSpan.FromMinutes(15);

    public static bool CanReserve(DadScheduleDefinition schedule, DadScheduleRunState state, DadRunResult run)
        => schedule.RestartOnPartialFailure && state.IsActive && !state.DryRun &&
           state.Phase == DadScheduleRunPhase.WaitingForDadRun &&
           state.PartialFailureRestartPendingSinceUtc == null &&
           state.PartialFailureRestartCount >= 0 &&
           state.PartialFailureRestartCount < Math.Clamp(schedule.PartialFailureRestartLimit, 1, 20) &&
           schedule.ScheduleId == state.ScheduleId && schedule.Revision == state.ScheduleRevisionAtStart &&
           !string.IsNullOrWhiteSpace(state.ActivePlannerRequestId) &&
           state.ActivePlannerRequestId == run.RequestId &&
           run.Status == DadRunStatus.PartialFailure && run.CompletedAtUtc.HasValue &&
           run.CancellationState == DadRunCancellationState.None &&
           run.ScheduleFailureKind is DadScheduleFailureKind.None or DadScheduleFailureKind.EntryTerminalFailure &&
           run.ModuleId is DadModuleId.PremadeDuty or DadModuleId.DailyMsq or DadModuleId.Duty &&
           run.StopProgress.CompletedRuns == 0 && run.CompletedTaskCount == 0 &&
           run.Request is { } request && request.GetConfiguredTaskCount() == 1;

    public static DadScheduleRestartDecision EvaluateReadiness(
        DadScheduleDefinition schedule, DadScheduleRunState state, bool cleanupPending,
        bool otherWorkActive, string admissionBlocker, DateTime nowUtc)
    {
        if (!schedule.RestartOnPartialFailure || state.PartialFailureRestartPendingSinceUtc is not { } pending ||
            state.PartialFailureRestartSource is not { Status: DadRunStatus.PartialFailure } source ||
            source.RequestId != state.ActivePlannerRequestId || !state.IsActive || state.DryRun ||
            state.PartialFailureRestartCount < 1 || state.PartialFailureRestartCount > Math.Clamp(schedule.PartialFailureRestartLimit, 1, 20) ||
            schedule.ScheduleId != state.ScheduleId || schedule.Revision != state.ScheduleRevisionAtStart)
            return new(DadScheduleRestartAction.Block, "Partial-failure restart lost its exact opt-in schedule/run boundary.");
        if (nowUtc < pending || nowUtc - pending > CleanupBudget)
            return new(DadScheduleRestartAction.Block, "Partial-failure restart stopped because safe cleanup was not proven within fifteen minutes.");
        if (cleanupPending || otherWorkActive || !string.IsNullOrWhiteSpace(admissionBlocker))
            return new(DadScheduleRestartAction.Wait, "Partial-failure restart is waiting for exact run, party and wake cleanup and ordinary scheduler admission.");
        if (nowUtc - pending < RestartDelay)
            return new(DadScheduleRestartAction.Wait, "Partial-failure restart is waiting for its thirty-second restart delay.");
        return new(DadScheduleRestartAction.Restart, "Partial-failure cleanup is complete; restarting the schedule with fresh IDs and ordinary preflight.");
    }
}
