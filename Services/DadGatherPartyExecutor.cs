using dad.Models;

namespace dad.Services;

public sealed class DadGatherPartyExecutor(IDadGatherClientService? native) : IDadModuleExecutor
{
    private DadGatherWorkerRun? worker;
    private DadModuleExecutionStatusDto status = new();
    private string commandRunId = string.Empty;
    public bool HasOwnedWork => worker?.HasOwnership == true;
    public string ExecutorId => "DadGatherPartyExecutor";
    public DadModuleId ModuleId => DadModuleId.GatherParty;

    public DadModuleExecutionStatusDto CanStart(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var error = string.Empty;
        var valid = DadGatherOrderRules.TryValidate(plan.Request, participants, out error);
        if (valid && participants.Count(participant => participant.IsLocalClient) != 1)
        {
            valid = false;
            error = "Gathering requires one exact local worker assignment.";
        }
        if (valid && worker?.HasOwnership == true)
        {
            valid = false;
            error = "An owned gathering operation still needs cleanup.";
        }
        if (valid && (native == null || !native.TryCreateSession(out _, out error)))
        {
            valid = false;
            if (string.IsNullOrEmpty(error)) error = "GBR gathering is unavailable on this worker.";
        }
        return new()
        {
            RunId = plan.Request.RequestId, ModuleId = ModuleId, DisplayName = "GBR Gather Party", StepName = ExecutorId,
            CanStart = valid, Status = valid ? DadRunStatus.Running : DadRunStatus.Failed,
            Phase = DadRunPhase.QueuePreparing, UpdatedAtUtc = DadClock.UtcNow,
            Summary = valid ? "Ready to gather this worker's finite allocation." : error,
            FailureReason = valid ? string.Empty : error, BlockedReason = valid ? string.Empty : error,
        };
    }

    public DadRunStepResultDto Start(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants)
    {
        // Commands are immutable and deduplicated by worker admission; still never re-enable
        // a native operation if Start is observed twice for the same finite run.
        if (commandRunId == plan.Request.RequestId && worker != null) return BuildStep();
        var candidate = CanStart(plan, participants);
        if (worker?.HasOwnership == true) return BuildStep(candidate);
        status = candidate;
        status.StartedAtUtc = DadClock.UtcNow;
        if (!status.CanStart) { status.CompletedAtUtc = DadClock.UtcNow; return BuildStep(); }
        if (!native!.TryCreateSession(out var session, out var error) || session == null)
        {
            status.Status = DadRunStatus.Failed;
            status.Summary = status.FailureReason = status.BlockedReason = error;
            status.CompletedAtUtc = DadClock.UtcNow;
            return BuildStep();
        }
        commandRunId = plan.Request.RequestId;
        var local = participants.Single(participant => participant.IsLocalClient);
        worker = new(session);
        worker.Start(commandRunId, plan.Request.GatherOrders.Where(order => order.WorkerSessionId == local.WorkerSessionId.Value).ToArray(), DadClock.UtcNow);
        ApplyWorker();
        return BuildStep();
    }

    public DadRunStepResultDto Update()
    {
        worker?.Update(DadClock.UtcNow);
        ApplyWorker();
        return BuildStep();
    }

    public DadRunStepResultDto Cancel(string reason)
    {
        worker?.Cancel(reason);
        ApplyWorker();
        return BuildStep();
    }

    public DadModuleExecutionStatusDto GetStatus() => status.Clone();

    private void ApplyWorker()
    {
        if (worker == null) return;
        status.UpdatedAtUtc = DadClock.UtcNow;
        status.IsActive = worker.IsActive;
        status.Phase = worker.IsActive ? DadRunPhase.InDutyOrTask : DadRunPhase.Finalizing;
        status.Status = worker.State switch
        {
            DadGatherWorkerRunState.Completed => DadRunStatus.Completed,
            DadGatherWorkerRunState.Cancelled => DadRunStatus.Cancelled,
            DadGatherWorkerRunState.Failed => DadRunStatus.Failed,
            _ => DadRunStatus.Running,
        };
        status.Summary = worker.Summary;
        status.GatherCounts = new(worker.Counts);
        status.FailureReason = status.Status == DadRunStatus.Failed ? status.Summary : string.Empty;
        status.BlockedReason = status.FailureReason;
        if (!status.IsActive) status.CompletedAtUtc = DadClock.UtcNow;
    }

    private DadRunStepResultDto BuildStep() => BuildStep(status);

    private static DadRunStepResultDto BuildStep(DadModuleExecutionStatusDto status) => new()
    {
        RunId = status.RunId, ModuleId = DadModuleId.GatherParty, StepName = "GBR Gather Party",
        ParticipantState = status.Status switch
        {
            DadRunStatus.Completed => DadParticipantState.Completed,
            DadRunStatus.Cancelled => DadParticipantState.Cancelled,
            DadRunStatus.Failed => DadParticipantState.Failed,
            _ => DadParticipantState.Running,
        },
        Success = status.Status is DadRunStatus.Running or DadRunStatus.Completed,
        Summary = status.Summary, FailureReason = status.FailureReason, BlockedReason = status.BlockedReason,
        ExecutorStatus = status.Clone(), ReportedAtUtc = DadClock.UtcNow,
    };
}
