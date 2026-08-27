using dad.Services;

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
    public DadMiniAutoPartySnapshot AutoParty { get; set; } = new();
}

public sealed class DadMiniAutoPartySnapshot
{
    public bool Enabled { get; set; }
    public bool EndpointReady { get; set; }
    public string EndpointState { get; set; } = string.Empty;
    public int ActivePairingCount { get; set; }
    public int OnlinePairingCount { get; set; }
    public int PrivateDirectoryListingCount { get; set; }
    public string DirectoryState { get; set; } = string.Empty;
    public bool DirectoryRefreshEligible { get; set; }
    public bool DirectoryRefreshInProgress { get; set; }
    public TimeSpan DirectoryRefreshCooldownRemaining { get; set; }
    public bool ExactFormationRecognized { get; set; }
    public string ExactFormationPhase { get; set; } = string.Empty;
    public string ExactFormationSummary { get; set; } = string.Empty;
    public bool GuardedDisbandComplete { get; set; }
    public bool CanGuardedDisband { get; set; }
    public string GuardedDisbandBlocker { get; set; } = string.Empty;
    public string FirstBlocker { get; set; } = string.Empty;
}

internal static class DadMiniAutoPartyProjection
{
    internal static DadMiniAutoPartySnapshot Build(
        bool dadEnabled,
        DadAutoPartyConfiguration configuration,
        DadAutoPartyEndpointSnapshot endpoint,
        DadAutoPartyDirectorySnapshot directory,
        DadCrewFormationStatus formation,
        bool refreshInProgress,
        TimeSpan refreshCooldownRemaining,
        DadPairedDirectoryRefreshResult lastRefresh,
        bool canGuardedDisband,
        string guardedDisbandBlocker)
    {
        var activePairings = configuration.Pairings
            .Where(static pairing => pairing.IsActive)
            .ToList();
        var activeIslands = activePairings
            .Select(static pairing => pairing.IslandId)
            .ToHashSet(StringComparer.Ordinal);
        var onlinePairings = activeIslands.Count(directory.OnlineIslandIds.Contains);
        var privateListings = directory.Listings.Count(listing =>
            activeIslands.Contains(listing.SharingIslandId));
        var endpointReady = configuration.IsRegistrationActive &&
                            endpoint.State == DadAutoPartyEndpointConnectionState.Ready;
        var exactFormation =
            DadAutoPartyFreeformRules.IsFreeformGroupId(formation.SourceGroupId) &&
            string.Equals(formation.SourceGroupId, formation.EffectiveGroupId, StringComparison.Ordinal);
        var directoryState = refreshInProgress
            ? "Refreshing paired directory."
            : privateListings > 0
                ? $"{privateListings} private listing(s) from reciprocal pairings."
                : lastRefresh.CompletedAtUtc != DateTime.MinValue
                    ? lastRefresh.OperatorStatus
                    : "Paired directory has not been refreshed yet.";
        var firstBlocker = !dadEnabled
            ? "Enable DAD."
            : !configuration.Enabled
                ? "Enable AutoParty."
                : !endpointReady
                    ? "Complete endpoint registration and wait for mailbox readiness."
                    : activePairings.Count == 0
                        ? "Complete reciprocal pairing with another DAD."
                        : privateListings == 0
                            ? "Refresh the paired private directory and resolve its first listing blocker."
                            : formation.Phase == DadCrewFormationPhase.Blocked && exactFormation
                                ? string.IsNullOrWhiteSpace(formation.BlockedReason)
                                    ? formation.Summary
                                    : formation.BlockedReason
                                : !exactFormation
                                    ? "Create the first exact formation from the full AutoParty window."
                                    : string.Empty;

        return new DadMiniAutoPartySnapshot
        {
            Enabled = configuration.Enabled,
            EndpointReady = endpointReady,
            EndpointState = endpointReady
                ? "Ready"
                : $"{endpoint.State} | {endpoint.SafeCode}",
            ActivePairingCount = activePairings.Count,
            OnlinePairingCount = onlinePairings,
            PrivateDirectoryListingCount = privateListings,
            DirectoryState = directoryState,
            DirectoryRefreshEligible = dadEnabled &&
                                       configuration.Enabled &&
                                       endpointReady &&
                                       activePairings.Count > 0 &&
                                       !refreshInProgress &&
                                       refreshCooldownRemaining <= TimeSpan.Zero,
            DirectoryRefreshInProgress = refreshInProgress,
            DirectoryRefreshCooldownRemaining = refreshCooldownRemaining,
            ExactFormationRecognized = exactFormation,
            ExactFormationPhase = exactFormation ? formation.Phase.ToString() : "Idle",
            ExactFormationSummary = exactFormation
                ? formation.Summary
                : "No exact AutoParty freeform formation is active or retained.",
            GuardedDisbandComplete = exactFormation && formation.Phase == DadCrewFormationPhase.Completed,
            CanGuardedDisband = canGuardedDisband,
            GuardedDisbandBlocker = guardedDisbandBlocker,
            FirstBlocker = firstBlocker,
        };
    }
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
