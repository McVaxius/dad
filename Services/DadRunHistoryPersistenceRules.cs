using dad.Models;

namespace dad.Services;

/// <summary>
/// Defines the compact, backward-compatible DadRunResult shape stored in Configuration.RunHistory.
/// Current runs remain full fidelity; only durable history snapshots discard runtime-heavy state.
/// </summary>
internal static class DadRunHistoryPersistenceRules
{
    public const int MaximumEntries = 50;

    public static DadRunResult CreateSnapshot(DadRunResult source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new DadRunResult
        {
            RequestId = source.RequestId,
            Status = source.Status,
            Phase = source.Phase,
            Role = source.Role,
            WorkerRole = source.WorkerRole,
            AuthorityMode = source.AuthorityMode,
            CancellationState = source.CancellationState,
            ModuleId = source.ModuleId,
            TransportMode = source.TransportMode,
            LocalOnlyEnabled = source.LocalOnlyEnabled,
            LeaderClientInstanceId = string.Empty,
            AuthorityWorkerSessionId = new DadWorkerSessionId(string.Empty),
            AuthorityEndpoint = string.Empty,
            LocalClientInstanceId = string.Empty,
            LocalWorkerSessionId = new DadWorkerSessionId(string.Empty),
            RequestedBy = source.RequestedBy,
            RequestedTaskCount = source.RequestedTaskCount,
            CompletedTaskCount = source.CompletedTaskCount,
            ActiveTaskIndex = source.ActiveTaskIndex,
            TotalTaskCount = source.TotalTaskCount,
            ActiveTaskName = source.ActiveTaskName,
            ActiveTaskStatus = source.ActiveTaskStatus,
            BlockedReason = source.BlockedReason,
            FailureReason = source.FailureReason,
            Summary = source.Summary,
            ScheduleFailureKind = source.ScheduleFailureKind,
            Request = null,
            StopProgress = CloneStopProgress(source.StopProgress),
            CurrentExecutorStatus = CreateEmptyExecutorStatus(),
            Participants = [],
            Leases = [],
            StepResults = (source.StepResults ?? [])
                .Where(static step => step != null)
                .Select(CloneStepResult)
                .ToList(),
            Warnings = source.Warnings == null ? [] : [..source.Warnings],
            CompletedAtUtc = source.CompletedAtUtc,
        };
    }

    public static bool CompactLegacyHistory(List<DadRunResult>? history)
    {
        if (history == null)
            return false;

        var changed = false;
        for (var index = history.Count - 1; index >= 0; index--)
        {
            var entry = history[index];
            if (entry == null)
            {
                history.RemoveAt(index);
                changed = true;
                continue;
            }

            if (IsCompactSnapshot(entry))
                continue;

            history[index] = CreateSnapshot(entry);
            changed = true;
        }

        if (history.Count > MaximumEntries)
        {
            history.RemoveRange(MaximumEntries, history.Count - MaximumEntries);
            changed = true;
        }

        return changed;
    }

    public static DadRunResult InsertSnapshot(List<DadRunResult> history, DadRunResult source)
    {
        ArgumentNullException.ThrowIfNull(history);
        var snapshot = CreateSnapshot(source);
        history.Insert(0, snapshot);
        if (history.Count > MaximumEntries)
            history.RemoveRange(MaximumEntries, history.Count - MaximumEntries);
        return snapshot;
    }

    public static bool IsCompactSnapshot(DadRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Request == null
               && string.IsNullOrWhiteSpace(result.LeaderClientInstanceId)
               && result.AuthorityWorkerSessionId.IsEmpty
               && string.IsNullOrWhiteSpace(result.AuthorityEndpoint)
               && string.IsNullOrWhiteSpace(result.LocalClientInstanceId)
               && result.LocalWorkerSessionId.IsEmpty
               && result.StopProgress != null
               && IsEmptyExecutorStatus(result.CurrentExecutorStatus)
               && result.Participants is { Count: 0 }
               && result.Leases is { Count: 0 }
               && result.StepResults != null
               && result.StepResults.All(static step => step != null && IsEmptyExecutorStatus(step.ExecutorStatus))
               && result.Warnings != null;
    }

    private static DadRunStopProgress CloneStopProgress(DadRunStopProgress? source)
        => source == null
            ? DadRunStopProgress.FromPolicy(null)
            : new DadRunStopProgress
            {
                StopPolicy = (source.StopPolicy ?? new DadRunStopPolicy()).Clone(),
                StartedRuns = source.StartedRuns,
                CompletedRuns = source.CompletedRuns,
                SafetyCap = source.SafetyCap,
                CurrentLevel = source.CurrentLevel,
                RestedExperience = source.RestedExperience,
                StopReached = source.StopReached,
                SafetyCapReached = source.SafetyCapReached,
                Summary = source.Summary,
            };

    private static DadRunStepResultDto CloneStepResult(DadRunStepResultDto source)
        => new()
        {
            RunId = source.RunId,
            ModuleId = source.ModuleId,
            StepName = source.StepName,
            ParticipantState = source.ParticipantState,
            Success = source.Success,
            Deferred = source.Deferred,
            TimedOut = source.TimedOut,
            Summary = source.Summary,
            FailureReason = source.FailureReason,
            BlockedReason = source.BlockedReason,
            ExecutorStatus = CreateEmptyExecutorStatus(),
            ModuleBlockers = (source.ModuleBlockers ?? [])
                .Where(static blocker => blocker != null)
                .Select(static blocker => blocker.Clone())
                .ToList(),
            ReportedAtUtc = source.ReportedAtUtc,
        };

    private static DadModuleExecutionStatusDto CreateEmptyExecutorStatus()
        => new() { UpdatedAtUtc = DateTime.MinValue };

    private static bool IsEmptyExecutorStatus(DadModuleExecutionStatusDto? status)
        => status != null
           && string.IsNullOrWhiteSpace(status.RunId)
           && status.ModuleId == DadModuleId.None
           && string.IsNullOrWhiteSpace(status.DisplayName)
           && status.Phase == DadRunPhase.Idle
           && status.Status == DadRunStatus.Idle
           && string.IsNullOrWhiteSpace(status.StepName)
           && !status.IsActive
           && !status.CanStart
           && !status.Deferred
           && status.RetryAttempt == 0
           && status.MaxRetryAttempts == 0
           && !status.StartedAtUtc.HasValue
           && status.UpdatedAtUtc == DateTime.MinValue
           && !status.CompletedAtUtc.HasValue
           && string.IsNullOrWhiteSpace(status.Summary)
           && string.IsNullOrWhiteSpace(status.FailureReason)
           && string.IsNullOrWhiteSpace(status.BlockedReason)
           && status.Blockers is { Count: 0 };
}
