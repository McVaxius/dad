namespace dad.Models;

public sealed class DadMiniStatusSnapshot
{
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsCoordinator { get; set; }
    public string RoleText { get; set; } = string.Empty;
    public DadAuthorityViewState Authority { get; set; } = new();
    public int ConnectedWorkerCount { get; set; }
    public string TransportStatus { get; set; } = string.Empty;
    public string TransportError { get; set; } = string.Empty;
    public DadRunResult VisibleRun { get; set; } = DadRunResult.Idle();
    internal DadRunResult DisplayRun { get; set; } = DadRunResult.Idle();
    internal DadActivityDisplaySource DisplaySource { get; set; }
    public DadSchedulerQueueSnapshot SchedulerQueue { get; set; } = new();
    public DadScheduleSnapshot Schedule { get; set; } = new();
    public DadWorkerExecutionStatus LocalWorker { get; set; } = new();
    public DadParticipantSnapshot LocalParticipant { get; set; } = new();
    public List<DadParticipantSnapshot> ConnectedParticipants { get; set; } = [];
    public string RecentFailure { get; set; } = string.Empty;
    public DadStopAllStatus? LastStopAll { get; set; }
    public DadWakeTakeoverResultDto? LocalTakeover { get; set; }
}

public static class DadMiniStatusSnapshotBuilder
{
    public static DadMiniStatusSnapshot Build(
        bool isCoordinator,
        DadAuthorityViewState authority,
        DadPeerTransportSnapshot transport,
        DadRunResult visibleRun,
        DadSchedulerQueueSnapshot schedulerQueue,
        DadScheduleSnapshot schedule,
        DadWorkerExecutionStatus localWorker,
        DadParticipantSnapshot localParticipant,
        DadStopAllStatus? lastStopAll,
        IEnumerable<DadRunResult>? runHistory = null,
        DadWakeTakeoverResultDto? localTakeover = null)
        => BuildWithActivityDisplay(
            isCoordinator,
            authority,
            transport,
            visibleRun,
            schedulerQueue,
            schedule,
            localWorker,
            localParticipant,
            lastStopAll,
            runHistory,
            localTakeover,
            new DadActivityDisplaySelection(visibleRun, DadActivityDisplaySource.ExistingDadState));

    internal static DadMiniStatusSnapshot BuildWithActivityDisplay(
        bool isCoordinator,
        DadAuthorityViewState authority,
        DadPeerTransportSnapshot transport,
        DadRunResult visibleRun,
        DadSchedulerQueueSnapshot schedulerQueue,
        DadScheduleSnapshot schedule,
        DadWorkerExecutionStatus localWorker,
        DadParticipantSnapshot localParticipant,
        DadStopAllStatus? lastStopAll,
        IEnumerable<DadRunResult>? runHistory,
        DadWakeTakeoverResultDto? localTakeover,
        DadActivityDisplaySelection activityDisplay)
    {
        var participants = transport.KnownParticipants
            .Where(static participant => participant.State != DadParticipantState.Stale)
            .Select(static participant => participant.Clone())
            .ToList();
        var failure = ResolveRecentFailure(visibleRun, schedulerQueue, schedule, runHistory);
        return new DadMiniStatusSnapshot
        {
            GeneratedAtUtc = DateTime.UtcNow,
            IsCoordinator = isCoordinator,
            RoleText = isCoordinator ? "Coordinator" : "Client",
            Authority = authority,
            ConnectedWorkerCount = transport.ConnectedPeerCount,
            TransportStatus = string.IsNullOrWhiteSpace(transport.ConnectionStatus)
                ? transport.Availability
                : transport.ConnectionStatus,
            TransportError = FirstNonEmpty(
                transport.LastAuthOrProtocolError,
                transport.LastTransportTimeoutSummary,
                transport.Availability.StartsWith("Unavailable", StringComparison.OrdinalIgnoreCase)
                    ? transport.Availability
                    : string.Empty),
            VisibleRun = visibleRun.Clone(),
            DisplayRun = activityDisplay.Run.Clone(),
            DisplaySource = activityDisplay.Source,
            SchedulerQueue = schedulerQueue,
            Schedule = schedule,
            LocalWorker = localWorker.Clone(),
            LocalParticipant = localParticipant.Clone(),
            ConnectedParticipants = participants,
            RecentFailure = failure,
            LastStopAll = lastStopAll?.Clone(),
            LocalTakeover = localTakeover?.Clone(),
        };
    }

    private static string ResolveRecentFailure(
        DadRunResult visibleRun,
        DadSchedulerQueueSnapshot schedulerQueue,
        DadScheduleSnapshot schedule,
        IEnumerable<DadRunResult>? runHistory)
    {
        var runFailure = new[] { visibleRun }
            .Concat(runHistory ?? [])
            .Where(static run => run.Status is DadRunStatus.Failed or DadRunStatus.PartialFailure or DadRunStatus.TimedOut or DadRunStatus.Rejected)
            .OrderByDescending(static run => run.CompletedAtUtc ?? DateTime.MinValue)
            .Select(static run => FirstNonEmpty(run.FailureReason, run.BlockedReason, run.Summary))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(runFailure))
            return runFailure;

        var schedulerFailure = schedulerQueue.RecentResults
            .Where(static result => !result.Success && result.FinalPhase != DadSchedulerPresetPhase.Cancelled)
            .OrderByDescending(static result => result.CompletedAtUtc)
            .Select(static result => FirstNonEmpty(result.BlockedReason, result.Summary))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(schedulerFailure))
            return schedulerFailure;

        return schedule.RecentResults
            .Where(static result => !result.Success && result.Status != DadScheduleRunStatus.Cancelled)
            .OrderByDescending(static result => result.CompletedAtUtc)
            .Select(static result => FirstNonEmpty(result.BlockedReason, result.Summary))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
