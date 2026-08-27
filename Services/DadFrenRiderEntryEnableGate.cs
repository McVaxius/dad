namespace dad.Services;

internal enum DadFrenRiderEntryEnableStatus
{
    Sent,
    AlreadySent,
    PendingRetry,
    Failed,
}

internal readonly record struct DadFrenRiderCommandResult(bool Succeeded, string FailureReason)
{
    public static DadFrenRiderCommandResult Success()
        => new(true, string.Empty);

    public static DadFrenRiderCommandResult Failure(string reason)
        => new(false, string.IsNullOrWhiteSpace(reason) ? "command processing failed" : reason);
}

internal sealed class DadFrenRiderEntryEnableGate
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, EntryEnableState> states = new(StringComparer.Ordinal);

    public DadFrenRiderEntryEnableStatus Apply(
        string runKey,
        string operationLabel,
        string command,
        DateTime now,
        Func<DadFrenRiderCommandResult> sendCommand,
        out string summary)
        => ApplyAtBoundary(
            runKey,
            operationLabel,
            command,
            "after duty entry",
            now,
            sendCommand,
            out summary);

    public DadFrenRiderEntryEnableStatus ApplyAtBoundary(
        string runKey,
        string operationLabel,
        string command,
        string activationBoundary,
        DateTime now,
        Func<DadFrenRiderCommandResult> sendCommand,
        out string summary)
    {
        if (string.IsNullOrWhiteSpace(runKey))
            runKey = "(unknown-run)";
        if (string.IsNullOrWhiteSpace(operationLabel))
            operationLabel = "this duty operation";
        if (string.IsNullOrWhiteSpace(activationBoundary))
            activationBoundary = "at the requested boundary";

        if (!states.TryGetValue(runKey, out var state))
        {
            state = new EntryEnableState();
            states[runKey] = state;
        }

        if (state.Sent)
        {
            summary = BuildSuccessSummary(command, operationLabel, activationBoundary);
            return DadFrenRiderEntryEnableStatus.AlreadySent;
        }

        if (state.Failed)
        {
            summary = state.LastSummary;
            return DadFrenRiderEntryEnableStatus.Failed;
        }

        if (state.AttemptCount > 0 && now < state.NextAttemptUtc)
        {
            summary = state.LastSummary;
            return DadFrenRiderEntryEnableStatus.PendingRetry;
        }

        if (state.AttemptCount == 0)
            state.FirstAttemptUtc = now;

        state.AttemptCount++;
        var result = sendCommand();
        if (result.Succeeded)
        {
            state.Sent = true;
            summary = BuildSuccessSummary(command, operationLabel, activationBoundary);
            state.LastSummary = summary;
            return DadFrenRiderEntryEnableStatus.Sent;
        }

        var failure = string.IsNullOrWhiteSpace(result.FailureReason)
            ? "command processing failed"
            : result.FailureReason;
        if (now - state.FirstAttemptUtc >= RetryWindow)
        {
            state.Failed = true;
            summary = $"Use FrenRider mode failed to send {command} {activationBoundary} for {operationLabel}: {failure}.";
            state.LastSummary = summary;
            return DadFrenRiderEntryEnableStatus.Failed;
        }

        state.NextAttemptUtc = now + RetryInterval;
        summary = $"Use FrenRider mode could not send {command} {activationBoundary} for {operationLabel}: {failure}. Retrying once per second.";
        state.LastSummary = summary;
        return DadFrenRiderEntryEnableStatus.PendingRetry;
    }

    private sealed class EntryEnableState
    {
        public bool Sent { get; set; }
        public bool Failed { get; set; }
        public int AttemptCount { get; set; }
        public DateTime FirstAttemptUtc { get; set; } = DateTime.MinValue;
        public DateTime NextAttemptUtc { get; set; } = DateTime.MinValue;
        public string LastSummary { get; set; } = string.Empty;
    }

    private static string BuildSuccessSummary(string command, string operationLabel, string activationBoundary)
        => $"Use FrenRider mode sent {command} {activationBoundary} for {operationLabel}.";
}
