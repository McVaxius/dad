namespace dad.Services;

public enum DadQueueOwnershipClaim
{
    Acquired,
    AlreadyOwned,
    Rejected,
}

public sealed class DadQueueOwnershipGate
{
    private string activeRunId = string.Empty;

    public string ActiveRunId => activeRunId;
    public bool IsOwned => !string.IsNullOrWhiteSpace(activeRunId);

    public DadQueueOwnershipClaim TryClaim(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return DadQueueOwnershipClaim.Rejected;

        if (string.IsNullOrWhiteSpace(activeRunId))
        {
            activeRunId = runId;
            return DadQueueOwnershipClaim.Acquired;
        }

        return string.Equals(activeRunId, runId, StringComparison.OrdinalIgnoreCase)
            ? DadQueueOwnershipClaim.AlreadyOwned
            : DadQueueOwnershipClaim.Rejected;
    }

    public bool IsOwnedBy(string runId)
        => !string.IsNullOrWhiteSpace(runId) &&
           string.Equals(activeRunId, runId, StringComparison.OrdinalIgnoreCase);

    public bool Release(string runId)
    {
        if (!IsOwnedBy(runId))
            return false;

        activeRunId = string.Empty;
        return true;
    }

    public void Release()
        => activeRunId = string.Empty;
}

public static class DadDutyLifecycleRules
{
    public const int SoulCrystalEquippedSlotIndex = 13;

    public static bool ObserveDutyCompleted(
        bool enteredDuty,
        bool alreadyCompleted,
        bool freshCompletionEvidence)
        => alreadyCompleted || enteredDuty && freshCompletionEvidence;

    public static bool IsAbandonedExit(
        bool enteredDuty,
        bool dutyCompleted,
        bool exitedRequestedDuty)
        => enteredDuty && exitedRequestedDuty && !dutyCompleted;

    public static bool IsCompletedExit(
        bool enteredDuty,
        bool dutyCompleted,
        bool exitedRequestedDuty)
        => enteredDuty && exitedRequestedDuty && dutyCompleted;

    public static bool IsExitCompletionGraceExpired(
        DateTime deadlineUtc,
        DateTime now)
        => deadlineUtc != DateTime.MinValue && now >= deadlineUtc;

    public static DadDutyExitDecision EvaluateExit(
        bool enteredDuty,
        bool dutyCompleted,
        bool exitedRequestedDuty,
        DateTime currentGraceDeadlineUtc,
        DateTime nowUtc,
        TimeSpan graceDuration)
    {
        if (!enteredDuty || !exitedRequestedDuty)
            return new(DadDutyExitDisposition.None, DateTime.MinValue);
        if (dutyCompleted)
            return new(DadDutyExitDisposition.Completed, DateTime.MinValue);
        if (currentGraceDeadlineUtc == DateTime.MinValue)
            return new(DadDutyExitDisposition.WaitingForCompletion, nowUtc + graceDuration);
        return nowUtc >= currentGraceDeadlineUtc
            ? new(DadDutyExitDisposition.Abandoned, currentGraceDeadlineUtc)
            : new(DadDutyExitDisposition.WaitingForCompletion, currentGraceDeadlineUtc);
    }

    public static bool IsAddonReadyForMutation(bool visible, bool ready)
        => visible && ready;

    public static DadEquippedDurabilityMinimum ObserveEquippedDurability(
        DadEquippedDurabilityMinimum current,
        int slotIndex,
        uint itemId,
        uint condition)
    {
        if (slotIndex == SoulCrystalEquippedSlotIndex || itemId == 0)
            return current;

        return new DadEquippedDurabilityMinimum(
            Found: true,
            MinimumPercent: Math.Min(current.MinimumPercent, (int)(condition / 300)));
    }
}

public enum DadDutyExitDisposition
{
    None = 0,
    Completed = 1,
    WaitingForCompletion = 2,
    Abandoned = 3,
}

public readonly record struct DadDutyExitDecision(
    DadDutyExitDisposition Disposition,
    DateTime GraceDeadlineUtc);

public readonly record struct DadEquippedDurabilityMinimum(
    bool Found,
    int MinimumPercent)
{
    public static DadEquippedDurabilityMinimum Empty => new(false, 100);
}

public static class DadMogtomeStatusRules
{
    public static bool TryValidateRunId(
        string expectedRunId,
        string observedRunId,
        string operation,
        out string reason)
    {
        if (string.IsNullOrWhiteSpace(expectedRunId))
        {
            reason = $"MOGTOME {operation} result was rejected because the active DAD run ID is missing.";
            return false;
        }

        if (!string.Equals(expectedRunId, observedRunId, StringComparison.Ordinal))
        {
            reason = $"MOGTOME {operation} result did not exactly match active DAD run '{expectedRunId}'.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool IsAcknowledgedStop(
        bool accepted,
        bool dadOwned,
        bool isRunning,
        bool isTerminal,
        string failureReason)
        => accepted &&
           dadOwned &&
           !isRunning &&
           isTerminal &&
           string.IsNullOrWhiteSpace(failureReason);
}

public static class DadNativeChatCommandRules
{
    public static bool TryNormalize(string? command, out string normalized, out string reason)
    {
        var raw = command ?? string.Empty;
        normalized = string.Empty;
        if (raw.Contains('\r') || raw.Contains('\n') || raw.Contains('\0'))
        {
            reason = "Native chat command must be a single line without null characters.";
            return false;
        }

        normalized = raw.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            reason = "Native chat command is empty.";
            return false;
        }

        if (normalized.Length == 1 || !normalized.StartsWith("/", StringComparison.Ordinal))
        {
            reason = "Native chat command must contain a slash command.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
