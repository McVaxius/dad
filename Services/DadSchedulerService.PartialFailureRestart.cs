using dad.Models;

namespace dad.Services;

public sealed partial class DadSchedulerService
{
    internal bool HasPendingPartialFailureRestart => configuration.ActiveScheduleRun is
        { IsActive: true, PartialFailureRestartPendingSinceUtc: not null };

    private bool TryReservePartialFailureRestart(
        DadScheduleDefinition schedule, DadScheduleRunState state, DadRunResult run, DateTime now)
    {
        if (!configuration.PluginEnabled || !configuration.RunAsServerDad ||
            !DadSchedulePartialFailureRestartRules.CanReserve(schedule, state, run))
            return false;
        var reason = $"Schedule '{state.ScheduleName}' encountered PartialFailure at '{state.CurrentPresetName}'. " +
                     $"Debug restart {state.PartialFailureRestartCount + 1}/{schedule.PartialFailureRestartLimit} reserved; waiting for cleanup.";
        var reserved = state.Clone();
        reserved.PartialFailureRestartCount++;
        reserved.PartialFailureRestartPendingSinceUtc = now;
        reserved.PartialFailureRestartSource = run.Clone();
        reserved.Summary = reason;
        reserved.UpdatedAtUtc = now;
        configuration.ActiveScheduleRun = reserved;
        if (currentState.PlannerRequestId == run.RequestId ||
            !string.IsNullOrWhiteSpace(state.ActiveSchedulerJobId) && currentState.JobId == state.ActiveSchedulerJobId)
            CancelNonTerminalTakeovers(reason);
        // Keep the failure visible in history. The active waiting reservation remains
        // cancellable; a future restart is a different round, not a successful old run.
        FinalizeScheduleRun(DadScheduleRules.BlockRun(reserved, reason, now,
            DadScheduleFailureKind.EntryTerminalFailure));
        configuration.Save();
        log.Warning("[dad] Reserved debug partial-failure schedule restart {Attempt}/{Limit}; failed request {RequestId}.",
            reserved.PartialFailureRestartCount, schedule.PartialFailureRestartLimit, run.RequestId);
        return true;
    }

    private void ProcessPartialFailureRestart(
        DadScheduleDefinition schedule, DadScheduleRunState state, DadRunResult visibleRun, DateTime now)
    {
        var source = state.PartialFailureRestartSource;
        if (!configuration.PluginEnabled || !configuration.RunAsServerDad)
        {
            BlockActiveScheduleRun("Partial-failure restart stopped because enabled Coordinator ownership was lost.",
                DadScheduleFailureKind.Cancellation);
            return;
        }
        var cleanupPending = HasPendingCleanup || source == null ||
                             retainsPlannerCancellationContext?.Invoke(source.RequestId) == true;
        var admission = GetAdmissionBlocker(CanRunScheduleHere(schedule));
        var busy = IsSchedulerActive || configuration.SchedulerQueue.Count != 0 ||
                   visibleRun.Status is DadRunStatus.Queued or DadRunStatus.WaitingForParticipants or DadRunStatus.Running;
        // An abandoned duty must have the existing exact teardown proof before a new
        // round can let normal daily preflight skip any already-completed character.
        var latest = source == null ? null : ResolveScheduleDadRunResult(source.RequestId, visibleRun) ?? source;
        if (latest != null && (latest.Status != DadRunStatus.PartialFailure ||
                               latest.CancellationState != DadRunCancellationState.None))
        {
            BlockActiveScheduleRun("Partial-failure restart refused a changed or cancelled source run.",
                DadScheduleFailureKind.Cancellation);
            return;
        }
        if (latest?.CurrentExecutorStatus.DutyExitedWithoutCompletion == true &&
            !latest.CurrentExecutorStatus.DutyExitPartyTeardownCompleted)
            cleanupPending = true;
        var decision = DadSchedulePartialFailureRestartRules.EvaluateReadiness(
            schedule, state, cleanupPending, busy, admission, now);
        state.Summary = decision.Summary;
        state.UpdatedAtUtc = now;
        if (decision.Action == DadScheduleRestartAction.Block)
        {
            BlockActiveScheduleRun(decision.Summary, DadScheduleFailureKind.EntryTerminalFailure);
            return;
        }
        if (decision.Action != DadScheduleRestartAction.Restart)
        {
            configuration.ActiveScheduleRun = state;
            return;
        }
        var validation = ValidateWholeSchedule(schedule);
        if (!string.IsNullOrWhiteSpace(validation))
        {
            BlockActiveScheduleRun(validation, DadScheduleFailureKind.PreStartRejected);
            return;
        }
        var restarted = BeginScheduleRun(schedule, dryRun: false, state.ManualRun,
            "schedule-debug-partial-restart", now);
        if (restarted.IsActive)
        {
            restarted.RetriedFromRunId = state.RunId;
            restarted.PartialFailureRestartCount = state.PartialFailureRestartCount;
            restarted.Summary = $"Debug partial-failure restart {state.PartialFailureRestartCount}/{schedule.PartialFailureRestartLimit}; ordinary daily/level checks remain enabled.";
            configuration.ActiveScheduleRun = restarted;
        }
        configuration.Save();
        MarkPlannerUiRevisionDirty();
    }
}
