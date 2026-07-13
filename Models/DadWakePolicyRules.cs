namespace dad.Models;

public sealed class DadWakePolicyDecision
{
    public bool CanSchedule { get; init; }
    public bool Ready { get; init; }
    public bool ShouldRequestTakeover { get; init; }
    public DadWakeTakeoverStage Stage { get; init; } = DadWakeTakeoverStage.None;
    public string Summary { get; init; } = string.Empty;
    public string BlockedReason { get; init; } = string.Empty;
}

public static class DadWakePolicyRules
{
    public const string LoadCharacterStubReason = "Load character (stub) is not implemented; this policy sends no commands.";

    public static DadWakePolicyDecision Evaluate(
        DadSchedulerWakePolicy policy,
        bool sameAccountClientConnected,
        bool correctCharacter,
        bool postArReady,
        DadWakeTakeoverStatus takeoverStatus = DadWakeTakeoverStatus.Pending,
        string takeoverBlocker = "",
        bool autoRetainerAvailable = true,
        bool autoRetainerBusy = false,
        bool multiModeEnabled = false)
    {
        if (policy == DadSchedulerWakePolicy.LoadCharacterIfOnline)
        {
            return Blocked(LoadCharacterStubReason);
        }

        if (policy == DadSchedulerWakePolicy.AlreadyOnlineOnly)
        {
            if (!sameAccountClientConnected)
                return Blocked("Already online requires the configured same-account Dad client to be connected.");
            if (!correctCharacter)
                return Blocked("Already online requires the configured character; Dad will not relog it.");
            if (!postArReady)
                return Blocked("Already online requires the configured character to be post-AR ready; Dad will send no AutoRetainer commands.");
            if (!autoRetainerAvailable)
                return Blocked("Already online requires readable AutoRetainer state; Dad will send no commands.");
            if (autoRetainerBusy)
                return Blocked("Already online requires AutoRetainer to be idle; Dad will send no commands.");
            if (multiModeEnabled)
                return Blocked("Already online requires AutoRetainer Multi Mode to already be disabled; Dad will send no commands.");

            return new DadWakePolicyDecision
            {
                CanSchedule = true,
                Ready = true,
                Stage = DadWakeTakeoverStage.Ready,
                Summary = "Configured character is already online and post-AR ready.",
            };
        }

        if (policy != DadSchedulerWakePolicy.LaunchIfOffline)
            return Blocked($"Unsupported wake policy value {(int)policy}.");

        if (!sameAccountClientConnected)
        {
            return new DadWakePolicyDecision
            {
                CanSchedule = true,
                Stage = DadWakeTakeoverStage.WaitingForClient,
                Summary = "Waiting for the same-account Dad client connection; Dad will not launch a process.",
            };
        }

        if (takeoverStatus == DadWakeTakeoverStatus.Blocked)
            return Blocked(string.IsNullOrWhiteSpace(takeoverBlocker) ? "Wake takeover was blocked." : takeoverBlocker);

        var ready = takeoverStatus == DadWakeTakeoverStatus.Ready &&
                    correctCharacter &&
                    postArReady &&
                    !multiModeEnabled;
        return new DadWakePolicyDecision
        {
            CanSchedule = true,
            Ready = ready,
            ShouldRequestTakeover = true,
            Stage = ready ? DadWakeTakeoverStage.Ready : DadWakeTakeoverStage.VerifyingTakeover,
            Summary = ready
                ? "AutoRetainer takeover verified on the configured character."
                : "Same-account Dad client connected; requesting typed AutoRetainer takeover.",
        };
    }

    public static bool IsParticipantReadyTimedOut(
        DateTime startedAtUtc,
        DateTime nowUtc,
        int timeoutSeconds)
    {
        startedAtUtc = EnsureUtc(startedAtUtc);
        nowUtc = EnsureUtc(nowUtc);
        var timeout = TimeSpan.FromSeconds(Math.Max(30, timeoutSeconds <= 0 ? 300 : timeoutSeconds));
        return nowUtc - startedAtUtc >= timeout;
    }

    public static bool IsValidCharacterKey(DadCharacterKey key)
    {
        var value = key.Value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\r') || value.Contains('\n'))
            return false;

        var split = value.Split('@', StringSplitOptions.TrimEntries);
        return split.Length == 2 && split.All(static part => !string.IsNullOrWhiteSpace(part));
    }

    public static string BuildOperationKey(DadWakeTakeoverRequestDto request)
        => string.Join(
            ":",
            string.IsNullOrWhiteSpace(request.OperationToken)
                ? request.SchedulerRunId?.Trim() ?? string.Empty
                : request.OperationToken.Trim(),
            request.SlotId?.Trim() ?? string.Empty,
            request.AccountKey.Value?.Trim() ?? string.Empty,
            request.CharacterKey.Value?.Trim() ?? string.Empty);

    private static DadWakePolicyDecision Blocked(string reason)
        => new()
        {
            Stage = DadWakeTakeoverStage.Blocked,
            BlockedReason = reason,
            Summary = reason,
        };

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

public static class DadWakeCrewBarrierPolicy
{
    public static bool CanCommitReset(IReadOnlyCollection<DadWakeTakeoverPhase> phases)
        => phases.Count > 0 && phases.All(static phase =>
            phase >= DadWakeTakeoverPhase.Prepared && phase != DadWakeTakeoverPhase.Blocked && phase != DadWakeTakeoverPhase.Cancelled);

    public static bool CanCommitRelog(IReadOnlyCollection<DadWakeTakeoverPhase> phases)
        => phases.Count > 0 && phases.All(static phase =>
            phase >= DadWakeTakeoverPhase.ResetVerified && phase != DadWakeTakeoverPhase.Blocked && phase != DadWakeTakeoverPhase.Cancelled);
}
