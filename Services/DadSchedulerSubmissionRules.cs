using dad.Models;

namespace dad.Services;

internal enum DadSchedulerEnqueueDisposition
{
    Added,
    AlreadyActive,
    AlreadyPending,
    TerminalBlocked,
}

internal sealed class DadSchedulerEnqueueResult
{
    public DadSchedulerEnqueueDisposition Disposition { get; init; }
    public DadScheduledCrewJob Job { get; init; } = new();
}

internal static class DadSchedulerSubmissionRules
{
    public static DadSchedulerEnqueueResult? FindDuplicate(
        string groupId,
        DadScheduledCrewJob? activeJob,
        bool activeState,
        IEnumerable<DadScheduledCrewJob>? pendingJobs,
        IEnumerable<DadScheduledCrewJob>? cancellationCleanupJobs)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return null;

        var cleanup = (cancellationCleanupJobs ?? []).FirstOrDefault(job =>
            string.Equals(job.GroupId, groupId, StringComparison.OrdinalIgnoreCase));
        if (cleanup != null)
        {
            return new DadSchedulerEnqueueResult
            {
                Disposition = DadSchedulerEnqueueDisposition.AlreadyPending,
                Job = cleanup.Clone(),
            };
        }

        if (activeState && activeJob != null &&
            string.Equals(activeJob.GroupId, groupId, StringComparison.OrdinalIgnoreCase))
        {
            return new DadSchedulerEnqueueResult
            {
                Disposition = DadSchedulerEnqueueDisposition.AlreadyActive,
                Job = activeJob.Clone(),
            };
        }

        var pending = (pendingJobs ?? []).FirstOrDefault(job =>
            string.Equals(job.GroupId, groupId, StringComparison.OrdinalIgnoreCase));
        return pending == null
            ? null
            : new DadSchedulerEnqueueResult
            {
                Disposition = DadSchedulerEnqueueDisposition.AlreadyPending,
                Job = pending.Clone(),
            };
    }

    public static bool CanStartNextQueuedJob(bool cancellationCleanupPending)
        => !cancellationCleanupPending;

    public static DadSchedulerEnqueueDisposition ResolveAfterUpdate(
        DadSchedulerEnqueueDisposition initial,
        string jobId,
        DadSchedulerQueueSnapshot snapshot)
    {
        if (initial != DadSchedulerEnqueueDisposition.Added || string.IsNullOrWhiteSpace(jobId))
            return initial;

        var terminal = snapshot.RecentResults.FirstOrDefault(result =>
            string.Equals(result.JobId, jobId, StringComparison.OrdinalIgnoreCase));
        return terminal?.FinalPhase is DadSchedulerPresetPhase.Blocked or DadSchedulerPresetPhase.TimedOut
            ? DadSchedulerEnqueueDisposition.TerminalBlocked
            : initial;
    }

    public static string BuildFeedback(
        DadSchedulerEnqueueDisposition disposition,
        DadScheduledCrewJob job,
        DadSchedulerQueueSnapshot snapshot)
    {
        var jobId = string.IsNullOrWhiteSpace(job.JobId) ? "(unknown)" : job.JobId;
        var terminal = snapshot.RecentResults.FirstOrDefault(result =>
            string.Equals(result.JobId, job.JobId, StringComparison.OrdinalIgnoreCase));
        var cancellationCleanup = job.StatusSummary.StartsWith(
            "Cancellation cleanup pending",
            StringComparison.OrdinalIgnoreCase);
        var phase = cancellationCleanup
            ? "Cancellation cleanup"
            : string.Equals(snapshot.ActiveState.JobId, job.JobId, StringComparison.OrdinalIgnoreCase)
            ? snapshot.ActiveState.Phase.ToString()
            : terminal?.FinalPhase.ToString() ?? "Pending";
        var reason = terminal == null
            ? string.Equals(snapshot.ActiveState.JobId, job.JobId, StringComparison.OrdinalIgnoreCase)
                ? FirstNonEmpty(snapshot.ActiveState.BlockedReason, snapshot.ActiveState.Summary, job.StatusSummary)
                : FirstNonEmpty(job.BlockedReason, job.StatusSummary)
            : FirstNonEmpty(terminal.BlockedReason, terminal.Summary);

        return disposition switch
        {
            DadSchedulerEnqueueDisposition.AlreadyActive =>
                $"Preset already active: phase {phase}, Job ID {jobId}. {reason}",
            DadSchedulerEnqueueDisposition.AlreadyPending =>
                $"Preset already pending: phase {phase}, Job ID {jobId}. {reason}",
            DadSchedulerEnqueueDisposition.TerminalBlocked =>
                $"Preset submission terminally blocked: phase {phase}, Job ID {jobId}. {reason}",
            _ => $"Preset submission added: phase {phase}, Job ID {jobId}. {reason}",
        };
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
