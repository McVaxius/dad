namespace dad.Models;

public enum DadAuthorityViewKind
{
    LocalOnly,
    NoRemoteAuthority,
    RemoteIdle,
    RemoteQueued,
    RemoteWaiting,
    RemoteRunning,
    RemoteCompleted,
    RemoteCancelled,
    RemoteRejected,
    RemoteStale,
}

public sealed record DadAuthorityViewState
{
    public DadAuthorityViewKind Kind { get; init; } = DadAuthorityViewKind.NoRemoteAuthority;
    public bool HasRemoteAuthority { get; init; }
    public bool IsFresh { get; init; }
    public DateTime? LastSuccessfulRefreshUtc { get; init; }
    public string StateText { get; init; } = "No remote authority";
    public string TimelineText { get; init; } = "No Server Dad authority discovered.";
    public string FreshnessText { get; init; } = "Remote freshness unavailable.";
    public string ClientPerspectiveText { get; init; } = "observer";
    public string OwnershipText { get; init; } = "Authority not discovered.";
    public string PayloadText { get; init; } = "No active dad task payload.";
    public string AuthorityWorkerText { get; init; } = "(none)";
    public string AuthorityEndpointText { get; init; } = "(none)";
    public string DtrText { get; init; } = "NoAuth";
    public DadRunResult PreferredRun { get; init; } = DadRunResult.Idle();
}

public static class DadAuthorityViewBuilder
{
    public static DadAuthorityViewState Build(
        DadRunResult localRun,
        DadRunResult authorityRun,
        DadPeerTransportSnapshot transport,
        DadWorkerSessionId localWorkerSessionId,
        bool localOnlyModeEnabled,
        DateTime? lastSuccessfulRefreshUtc,
        DateTime utcNow,
        TimeSpan staleThreshold)
    {
        var authorityWorker = authorityRun.AuthorityWorkerSessionId.IsEmpty
            ? transport.AuthorityWorkerSessionId
            : authorityRun.AuthorityWorkerSessionId;
        var authorityEndpoint = string.IsNullOrWhiteSpace(authorityRun.AuthorityEndpoint)
            ? transport.AuthorityEndpoint
            : authorityRun.AuthorityEndpoint;
        var authorityRole = authorityRun.WorkerRole != DadWorkerRole.None
            ? authorityRun.WorkerRole
            : transport.AuthorityRole;
        var hasRemoteAuthority = !authorityWorker.IsEmpty || !string.IsNullOrWhiteSpace(authorityEndpoint);
        var payloadText = authorityRun.Request?.DescribeRequestedWork() ?? "No active dad task payload.";
        var clientPerspective = ResolveClientPerspective(localRun, authorityRun, localWorkerSessionId, localOnlyModeEnabled);
        var freshnessText = BuildFreshnessText(lastSuccessfulRefreshUtc, utcNow, staleThreshold, hasRemoteAuthority);
        var isFresh = hasRemoteAuthority &&
                      lastSuccessfulRefreshUtc.HasValue &&
                      utcNow - lastSuccessfulRefreshUtc.Value <= staleThreshold;

        var kind = ResolveKind(localRun, authorityRun, localOnlyModeEnabled, hasRemoteAuthority, isFresh);
        var timelineText = BuildTimelineText(kind, localRun, authorityRun, payloadText, clientPerspective);
        var dtrText = BuildDtrText(kind, localRun);
        var ownershipText = $"{DadStatusText.FormatAuthorityStatus(authorityRole, authorityWorker, authorityEndpoint, authorityRun.AuthorityMode)} | {freshnessText}";
        var preferredRun = ResolvePreferredRun(kind, localRun, authorityRun);

        return new DadAuthorityViewState
        {
            Kind = kind,
            HasRemoteAuthority = hasRemoteAuthority,
            IsFresh = isFresh,
            LastSuccessfulRefreshUtc = lastSuccessfulRefreshUtc,
            StateText = FormatKind(kind),
            TimelineText = timelineText,
            FreshnessText = freshnessText,
            ClientPerspectiveText = clientPerspective,
            OwnershipText = ownershipText,
            PayloadText = payloadText,
            AuthorityWorkerText = authorityWorker.IsEmpty ? "(none)" : authorityWorker.ToString(),
            AuthorityEndpointText = string.IsNullOrWhiteSpace(authorityEndpoint) ? "(none)" : authorityEndpoint,
            DtrText = dtrText,
            PreferredRun = preferredRun,
        };
    }

    private static DadAuthorityViewKind ResolveKind(
        DadRunResult localRun,
        DadRunResult authorityRun,
        bool localOnlyModeEnabled,
        bool hasRemoteAuthority,
        bool isFresh)
    {
        if (localOnlyModeEnabled)
            return DadAuthorityViewKind.LocalOnly;

        if (!hasRemoteAuthority)
            return DadAuthorityViewKind.NoRemoteAuthority;

        if (!isFresh)
            return DadAuthorityViewKind.RemoteStale;

        return authorityRun.Status switch
        {
            DadRunStatus.Queued => DadAuthorityViewKind.RemoteQueued,
            DadRunStatus.WaitingForParticipants => DadAuthorityViewKind.RemoteWaiting,
            DadRunStatus.Running => DadAuthorityViewKind.RemoteRunning,
            DadRunStatus.Completed or DadRunStatus.PartialFailure or DadRunStatus.TimedOut or DadRunStatus.Failed => DadAuthorityViewKind.RemoteCompleted,
            DadRunStatus.Cancelled => DadAuthorityViewKind.RemoteCancelled,
            DadRunStatus.Rejected => DadAuthorityViewKind.RemoteRejected,
            _ when localRun.WorkerRole == DadWorkerRole.ServerDad && localRun.Role == DadOrchestrationRole.Leader => DadAuthorityViewKind.NoRemoteAuthority,
            _ => DadAuthorityViewKind.RemoteIdle,
        };
    }

    private static string ResolveClientPerspective(
        DadRunResult localRun,
        DadRunResult authorityRun,
        DadWorkerSessionId localWorkerSessionId,
        bool localOnlyModeEnabled)
    {
        if (localOnlyModeEnabled)
            return "isolated local-only";

        if (localRun.LocalOnlyEnabled && localRun.Status != DadRunStatus.Idle)
            return "worker";

        var localParticipant = authorityRun.Participants.FirstOrDefault(candidate =>
            string.Equals(candidate.WorkerSessionId, localWorkerSessionId.ToString(), StringComparison.OrdinalIgnoreCase));
        if (localParticipant != null && localParticipant.Role != DadOrchestrationRole.None)
            return "worker";

        if (localRun.Role != DadOrchestrationRole.None && localRun.Status != DadRunStatus.Idle)
            return "worker";

        if (localRun.WorkerRole == DadWorkerRole.ServerDad && localRun.Role == DadOrchestrationRole.Leader)
            return "worker";

        return "observer";
    }

    private static string BuildFreshnessText(DateTime? lastSuccessfulRefreshUtc, DateTime utcNow, TimeSpan staleThreshold, bool hasRemoteAuthority)
    {
        if (!hasRemoteAuthority)
            return "No remote authority endpoint/session discovered.";

        if (!lastSuccessfulRefreshUtc.HasValue)
            return "Remote status not refreshed yet.";

        var age = utcNow - lastSuccessfulRefreshUtc.Value;
        var ageText = $"{Math.Max(0, age.TotalSeconds):F1}s";
        return age <= staleThreshold
            ? $"Fresh ({ageText} since last Server Dad refresh)."
            : $"Stale ({ageText} since last Server Dad refresh).";
    }

    private static string BuildTimelineText(
        DadAuthorityViewKind kind,
        DadRunResult localRun,
        DadRunResult authorityRun,
        string payloadText,
        string clientPerspective)
        => kind switch
        {
            DadAuthorityViewKind.LocalOnly => "Local-only mode enabled. This client is isolated from Server Dad authority.",
            DadAuthorityViewKind.NoRemoteAuthority when localRun.WorkerRole == DadWorkerRole.ServerDad && localRun.Role == DadOrchestrationRole.Leader
                => "This instance owns Server Dad authority locally.",
            DadAuthorityViewKind.NoRemoteAuthority => "No Server Dad authority discovered on localhost.",
            DadAuthorityViewKind.RemoteIdle => $"Server Dad idle. This client is {clientPerspective}.",
            DadAuthorityViewKind.RemoteQueued => $"Server Dad queued {payloadText}. This client is {clientPerspective}.",
            DadAuthorityViewKind.RemoteWaiting => $"Server Dad waiting on participants for {payloadText}. This client is {clientPerspective}.",
            DadAuthorityViewKind.RemoteRunning => $"Server Dad running {payloadText}. This client is {clientPerspective}.",
            DadAuthorityViewKind.RemoteCompleted => $"Server Dad {authorityRun.Status.ToString().ToLowerInvariant()} {payloadText}. This client is {clientPerspective}.",
            DadAuthorityViewKind.RemoteCancelled => $"Server Dad cancelled {payloadText}. This client is {clientPerspective}.",
            DadAuthorityViewKind.RemoteRejected => $"Server Dad rejected {payloadText}. This client is {clientPerspective}.",
            DadAuthorityViewKind.RemoteStale => $"Server Dad status is stale for {payloadText}. Last known result was {authorityRun.Status}/{authorityRun.Phase}.",
            _ => "Authority view unavailable.",
        };

    private static string BuildDtrText(DadAuthorityViewKind kind, DadRunResult localRun)
    {
        if (localRun.LocalOnlyEnabled && localRun.Status != DadRunStatus.Idle)
            return "LocalRun";

        return kind switch
        {
            DadAuthorityViewKind.LocalOnly => "LocalOnly",
            DadAuthorityViewKind.NoRemoteAuthority when localRun.Status != DadRunStatus.Idle => "LocalRun",
            DadAuthorityViewKind.NoRemoteAuthority => "NoAuth",
            DadAuthorityViewKind.RemoteIdle => "RIdle",
            DadAuthorityViewKind.RemoteQueued => "RQueue",
            DadAuthorityViewKind.RemoteWaiting => "RWait",
            DadAuthorityViewKind.RemoteRunning => "RRun",
            DadAuthorityViewKind.RemoteCompleted => "RDone",
            DadAuthorityViewKind.RemoteCancelled => "RCancel",
            DadAuthorityViewKind.RemoteRejected => "RReject",
            DadAuthorityViewKind.RemoteStale => "RStale",
            _ => "DAD",
        };
    }

    private static DadRunResult ResolvePreferredRun(DadAuthorityViewKind kind, DadRunResult localRun, DadRunResult authorityRun)
    {
        if (localRun.Status != DadRunStatus.Idle)
            return localRun;

        return kind is DadAuthorityViewKind.NoRemoteAuthority or DadAuthorityViewKind.LocalOnly
            ? localRun
            : authorityRun;
    }

    private static string FormatKind(DadAuthorityViewKind kind)
        => kind switch
        {
            DadAuthorityViewKind.LocalOnly => "Local-only",
            DadAuthorityViewKind.NoRemoteAuthority => "No remote authority",
            DadAuthorityViewKind.RemoteIdle => "Remote idle",
            DadAuthorityViewKind.RemoteQueued => "Remote queued",
            DadAuthorityViewKind.RemoteWaiting => "Remote waiting",
            DadAuthorityViewKind.RemoteRunning => "Remote running",
            DadAuthorityViewKind.RemoteCompleted => "Remote completed",
            DadAuthorityViewKind.RemoteCancelled => "Remote cancelled",
            DadAuthorityViewKind.RemoteRejected => "Remote rejected",
            DadAuthorityViewKind.RemoteStale => "Remote stale",
            _ => kind.ToString(),
        };
}
