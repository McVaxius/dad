namespace dad.Models;

public enum DadWakeTakeoverStatus
{
    Pending = 0,
    Ready = 1,
    RelogIssued = 2,
    Blocked = 3,
}

public enum DadWakeTakeoverStage
{
    None = 0,
    WaitingForClient = 1,
    WaitingForPostArReady = 2,
    WaitingForAutoRetainer = 3,
    DisablingMultiMode = 4,
    ResetIssued = 5,
    VerifyingTakeover = 6,
    RelogIssued = 7,
    WaitingForCharacter = 8,
    Ready = 9,
    Blocked = 10,
    WaitingForExternalAutomation = 11,
    AwaitingArHook = 12,
    PostprocessOwned = 13,
    Prepared = 14,
    ResetCommitted = 15,
    ResetVerified = 16,
    RelogCommitted = 17,
    ReturningHome = 18,
    WaitingForHomeWorld = 19,
}

public enum DadWakeTakeoverPhase
{
    AwaitingArHook = 0,
    PostprocessOwned = 1,
    Prepared = 2,
    ResetCommitted = 3,
    ResetVerified = 4,
    RelogCommitted = 5,
    WaitingForCharacter = 6,
    Ready = 7,
    Blocked = 8,
    Cancelled = 9,
}

public enum DadWakeTakeoverMessageKind
{
    Prepare = 0,
    Go = 1,
    Status = 2,
    Cancel = 3,
}

public enum DadWakeCommitKind
{
    None = 0,
    Reset = 1,
    Relog = 2,
}

public enum DadWakeAcknowledgementState
{
    Pending = 0,
    Accepted = 1,
    Executed = 2,
    Rejected = 3,
}

public enum DadWakeTakeoverCommand
{
    DisableAutoRetainer = 0,
    ResetAutoRetainer = 1,
    RelogCharacter = 2,
}

public sealed class DadWakeTakeoverRequestDto
{
    public string SchedulerRunId { get; set; } = string.Empty;
    public string SlotId { get; set; } = string.Empty;
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public string OperationToken { get; set; } = string.Empty;
    public DadWakeTakeoverMessageKind MessageKind { get; set; } = DadWakeTakeoverMessageKind.Prepare;
    public DadWakeCommitKind CommitKind { get; set; }
    public DateTime? ExecutionTimeUtc { get; set; }
}

public sealed class DadWakeTakeoverResultDto
{
    public string SchedulerRunId { get; set; } = string.Empty;
    public string SlotId { get; set; } = string.Empty;
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public DadWakeTakeoverStatus Status { get; set; } = DadWakeTakeoverStatus.Pending;
    public DadWakeTakeoverStage Stage { get; set; } = DadWakeTakeoverStage.None;
    public DadWakeTakeoverPhase Phase { get; set; } = DadWakeTakeoverPhase.AwaitingArHook;
    public string OperationToken { get; set; } = string.Empty;
    public DadWakeCommitKind CommitKind { get; set; }
    public DateTime? ExecutionTimeUtc { get; set; }
    public DadWakeAcknowledgementState AcknowledgementState { get; set; }
    public bool PostArReady { get; set; }
    public bool AutoRetainerAvailable { get; set; }
    public bool AutoRetainerBusy { get; set; }
    public bool MultiModeEnabled { get; set; }
    public bool RelogIssued { get; set; }
    public bool ExternalAutomationHeld { get; set; }
    public DadVermaxionReservationState VermaxionReservationState { get; set; } = DadVermaxionReservationState.NotLoaded;
    public string VermaxionReservationSummary { get; set; } = string.Empty;
    public DateTime? VermaxionReservationCreatedAtUtc { get; set; }
    public DateTime? VermaxionReservationUpdatedAtUtc { get; set; }
    public string ExternalAutomationActivity { get; set; } = string.Empty;
    public string ExternalAutomationState { get; set; } = string.Empty;
    public string ExternalAutomationSummary { get; set; } = string.Empty;
    public DateTime? ResetIssuedUtc { get; set; }
    public DateTime? TakeoverVerifiedUtc { get; set; }
    public DateTime? RelogIssuedUtc { get; set; }
    public DateTime? ReadyUtc { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string BlockedReason { get; set; } = string.Empty;
    public DadParticipantSnapshot Snapshot { get; set; } = new();

    public DadWakeTakeoverResultDto Clone()
        => new()
        {
            SchedulerRunId = SchedulerRunId,
            SlotId = SlotId,
            AccountKey = AccountKey,
            CharacterKey = CharacterKey,
            Status = Status,
            Stage = Stage,
            Phase = Phase,
            OperationToken = OperationToken,
            CommitKind = CommitKind,
            ExecutionTimeUtc = ExecutionTimeUtc,
            AcknowledgementState = AcknowledgementState,
            PostArReady = PostArReady,
            AutoRetainerAvailable = AutoRetainerAvailable,
            AutoRetainerBusy = AutoRetainerBusy,
            MultiModeEnabled = MultiModeEnabled,
            RelogIssued = RelogIssued,
            ExternalAutomationHeld = ExternalAutomationHeld,
            VermaxionReservationState = VermaxionReservationState,
            VermaxionReservationSummary = VermaxionReservationSummary,
            VermaxionReservationCreatedAtUtc = VermaxionReservationCreatedAtUtc,
            VermaxionReservationUpdatedAtUtc = VermaxionReservationUpdatedAtUtc,
            ExternalAutomationActivity = ExternalAutomationActivity,
            ExternalAutomationState = ExternalAutomationState,
            ExternalAutomationSummary = ExternalAutomationSummary,
            ResetIssuedUtc = ResetIssuedUtc,
            TakeoverVerifiedUtc = TakeoverVerifiedUtc,
            RelogIssuedUtc = RelogIssuedUtc,
            ReadyUtc = ReadyUtc,
            Summary = Summary,
            BlockedReason = BlockedReason,
            Snapshot = Snapshot?.Clone() ?? new DadParticipantSnapshot(),
        };
}

public sealed class DadWakeTakeoverTargetSnapshot
{
    public bool DadEnabled { get; set; }
    public bool RemoteMutationAllowed { get; set; }
    public bool AccountMatches { get; set; }
    public bool CharacterKnownToAccount { get; set; }
    public bool CorrectCharacter { get; set; }
    public bool PostArReady { get; set; }
    public bool AutoRetainerAvailable { get; set; }
    public bool AutoRetainerBusy { get; set; }
    public bool LifestreamAvailable { get; set; }
    public bool LifestreamBusy { get; set; }
    public string LifestreamStatus { get; set; } = string.Empty;
    public bool SuppressionReadable { get; set; }
    public bool AutoRetainerSuppressed { get; set; }
    public bool DadOwnsSuppression { get; set; }
    public bool DadOwnsCharacterPostprocess { get; set; }
    public bool MultiModeEnabled { get; set; }
    public bool ExternalAutomationHeld { get; set; }
    public bool VermaxionReservationAuthoritative { get; set; }
    public DadVermaxionMutationAuthorization VermaxionMutationAuthorization { get; set; }
    public DadVermaxionCompatibilityEvidence VermaxionCompatibilityEvidence { get; set; }
    public DadVermaxionReservationState VermaxionReservationState { get; set; } = DadVermaxionReservationState.NotLoaded;
    public string VermaxionReservationSummary { get; set; } = string.Empty;
    public DateTime? VermaxionReservationCreatedAtUtc { get; set; }
    public DateTime? VermaxionReservationUpdatedAtUtc { get; set; }
    public string ExternalAutomationActivity { get; set; } = string.Empty;
    public string ExternalAutomationState { get; set; } = string.Empty;
    public string ExternalAutomationSummary { get; set; } = string.Empty;
    public string AutoRetainerStatus { get; set; } = string.Empty;
    public DadParticipantSnapshot Participant { get; set; } = new();
}

public readonly record struct DadSuppressionLeaseSnapshot(
    bool Readable,
    bool Suppressed,
    bool OwnedByDad,
    string Error = "");

public readonly record struct DadWakeTakeoverActionResult(bool Success, string Error)
{
    public static DadWakeTakeoverActionResult Accepted() => new(true, string.Empty);

    public static DadWakeTakeoverActionResult Rejected(string error)
        => new(false, string.IsNullOrWhiteSpace(error) ? "Wake takeover action was rejected." : error.Trim());
}
