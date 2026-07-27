namespace dad.Models;

public enum DadAllianceRecruitmentState
{
    Idle,
    Validating,
    CreatingListing,
    ListingOpen,
    Searching,
    Joining,
    WaitingUnsafe,
    RetryWaiting,
    Verifying,
    CorrectingWrongAlliance,
    Complete,
    Stopped,
    Blocked,
}

public enum DadAllianceRecruitmentResultKind
{
    Pending,
    Waiting,
    Retry,
    Succeeded,
    Stopped,
    Blocked,
}

public sealed class DadAllianceRecruitmentInstructionDto
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string RecruitmentId { get; set; } = string.Empty;
    public DadWorkerSessionId CoordinatorWorkerSessionId { get; set; } = new(string.Empty);
    public string CoordinatorIdentity { get; set; } = string.Empty;
    public string LeaderName { get; set; } = string.Empty;
    public string LeaderWorld { get; set; } = string.Empty;
    public DadWorkerSessionId TargetWorkerSessionId { get; set; } = new(string.Empty);
    public ulong TargetApplicationId { get; set; }
    public DadCharacterKey TargetCharacterKey { get; set; } = new(string.Empty);
    public string TargetCharacterName { get; set; } = string.Empty;
    public string TargetCharacterWorld { get; set; } = string.Empty;
    public ulong TargetContentId { get; set; }
    public DadAllianceAssignment AssignedAlliance { get; set; }
    public int Passcode { get; set; }
    public int Attempt { get; set; }
    public DadAllianceRecruitmentState State { get; set; } = DadAllianceRecruitmentState.Validating;
    public long StopGeneration { get; set; }
    public DateTime IssuedAtUtc { get; set; } = DateTime.UtcNow;

    public string DedupeKey
        => $"{RecruitmentId.Trim()}|{TargetCharacterKey.Value.Trim()}";

    public DadAllianceRecruitmentInstructionDto Clone()
        => new()
        {
            SchemaVersion = SchemaVersion,
            RecruitmentId = RecruitmentId,
            CoordinatorWorkerSessionId = CoordinatorWorkerSessionId,
            CoordinatorIdentity = CoordinatorIdentity,
            LeaderName = LeaderName,
            LeaderWorld = LeaderWorld,
            TargetWorkerSessionId = TargetWorkerSessionId,
            TargetApplicationId = TargetApplicationId,
            TargetCharacterKey = TargetCharacterKey,
            TargetCharacterName = TargetCharacterName,
            TargetCharacterWorld = TargetCharacterWorld,
            TargetContentId = TargetContentId,
            AssignedAlliance = AssignedAlliance,
            Passcode = Passcode,
            Attempt = Attempt,
            State = State,
            StopGeneration = StopGeneration,
            IssuedAtUtc = IssuedAtUtc,
        };
}

public sealed class DadAllianceRecruitmentCancellationDto
{
    public string RecruitmentId { get; set; } = string.Empty;
    public DadWorkerSessionId CoordinatorWorkerSessionId { get; set; } = new(string.Empty);
    public DadWorkerSessionId TargetWorkerSessionId { get; set; } = new(string.Empty);
    public DadCharacterKey TargetCharacterKey { get; set; } = new(string.Empty);
    public long StopGeneration { get; set; }
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = string.Empty;
}

public sealed class DadAllianceRecruitmentResultDto
{
    public string RecruitmentId { get; set; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public DadCharacterKey TargetCharacterKey { get; set; } = new(string.Empty);
    public string TargetCharacterName { get; set; } = string.Empty;
    public string TargetCharacterWorld { get; set; } = string.Empty;
    public ulong TargetContentId { get; set; }
    public DadAllianceAssignment ExpectedAlliance { get; set; }
    public DadAllianceAssignment ObservedAlliance { get; set; }
    public int Attempt { get; set; }
    public DadAllianceRecruitmentState State { get; set; }
    public DadAllianceRecruitmentResultKind ResultKind { get; set; }
    public bool Retryable { get; set; }
    public long StopGeneration { get; set; }
    public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;
    public string Summary { get; set; } = string.Empty;

    public bool IsTerminal
        => ResultKind is DadAllianceRecruitmentResultKind.Succeeded
            or DadAllianceRecruitmentResultKind.Stopped
            or DadAllianceRecruitmentResultKind.Blocked;

    public DadAllianceRecruitmentResultDto Clone()
        => new()
        {
            RecruitmentId = RecruitmentId,
            WorkerSessionId = WorkerSessionId,
            TargetCharacterKey = TargetCharacterKey,
            TargetCharacterName = TargetCharacterName,
            TargetCharacterWorld = TargetCharacterWorld,
            TargetContentId = TargetContentId,
            ExpectedAlliance = ExpectedAlliance,
            ObservedAlliance = ObservedAlliance,
            Attempt = Attempt,
            State = State,
            ResultKind = ResultKind,
            Retryable = Retryable,
            StopGeneration = StopGeneration,
            ObservedAtUtc = ObservedAtUtc,
            Summary = Summary,
        };
}

/// <summary>
/// Hub-safe status projection. It deliberately excludes the PF passcode, native
/// listing payloads, signing keys, transport secrets, and Discord tokens.
/// </summary>
public sealed class DadAlliancePfUiSnapshotDto
{
    public string RecruitmentId { get; set; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public DadCharacterKey TargetCharacterKey { get; set; } = new(string.Empty);
    public DadAllianceAssignment AssignedAlliance { get; set; }
    public DadAllianceAssignment ObservedAlliance { get; set; }
    public int Attempt { get; set; }
    public DadAllianceRecruitmentState State { get; set; }
    public long StopGeneration { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string SafeStatusCode { get; set; } = string.Empty;
}

public sealed class DadAlliancePresetValidation
{
    public int AllianceACount { get; init; }
    public int AllianceBCount { get; init; }
    public int AllianceCCount { get; init; }
    public int TotalCount => AllianceACount + AllianceBCount + AllianceCCount;
    public List<string> Blockers { get; init; } = [];
    public bool IsValid => Blockers.Count == 0;
    public string Summary { get; init; } = string.Empty;
}

public sealed class DadAllianceRecruitmentTarget
{
    public string SlotId { get; set; } = string.Empty;
    public DadAllianceAssignment Assignment { get; set; }
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public ulong DiscordApplicationId { get; set; }
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong ContentId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string WorldName { get; set; } = string.Empty;
}

public sealed class DadAlliancePartyFinderStatus
{
    public string RecruitmentId { get; set; } = string.Empty;
    public DadAllianceRecruitmentState State { get; set; }
    public string PresetGroupId { get; set; } = string.Empty;
    public string PresetName { get; set; } = string.Empty;
    public string LeaderName { get; set; } = string.Empty;
    public string LeaderWorld { get; set; } = string.Empty;
    public int Passcode { get; set; }
    public ulong ListingId { get; set; }
    public bool OwnsRecruitment { get; set; }
    public string CreateStage { get; set; } = string.Empty;
    public int CreateAttempt { get; set; }
    public DateTime? CreateNextRetryUtc { get; set; }
    public string CreateLastError { get; set; } = string.Empty;
    public int CreateElapsedMilliseconds { get; set; }
    internal bool CreatePreflightReady { get; set; }
    internal string CreatePreflightBlocker { get; set; } = string.Empty;
    internal bool CreateRejected { get; set; }
    internal bool CreateActiveRecruitment { get; set; }
    internal bool CreateEditorVisible { get; set; }
    internal bool CreateSubmitDispatched { get; set; }
    internal string CreateConfigurationTarget { get; set; } = string.Empty;
    internal string CreateObservedSettings { get; set; } = string.Empty;
    public long StopGeneration { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Summary { get; set; } = "No DAD alliance recruitment is active.";
    public DadAlliancePresetValidation Validation { get; set; } = new();
    public List<DadAllianceRecruitmentResultDto> Results { get; set; } = [];

    public DadAlliancePartyFinderStatus Clone()
        => new()
        {
            RecruitmentId = RecruitmentId,
            State = State,
            PresetGroupId = PresetGroupId,
            PresetName = PresetName,
            LeaderName = LeaderName,
            LeaderWorld = LeaderWorld,
            Passcode = Passcode,
            ListingId = ListingId,
            OwnsRecruitment = OwnsRecruitment,
            CreateStage = CreateStage,
            CreateAttempt = CreateAttempt,
            CreateNextRetryUtc = CreateNextRetryUtc,
            CreateLastError = CreateLastError,
            CreateElapsedMilliseconds = CreateElapsedMilliseconds,
            CreatePreflightReady = CreatePreflightReady,
            CreatePreflightBlocker = CreatePreflightBlocker,
            CreateRejected = CreateRejected,
            CreateActiveRecruitment = CreateActiveRecruitment,
            CreateEditorVisible = CreateEditorVisible,
            CreateSubmitDispatched = CreateSubmitDispatched,
            CreateConfigurationTarget = CreateConfigurationTarget,
            CreateObservedSettings = CreateObservedSettings,
            StopGeneration = StopGeneration,
            StartedAtUtc = StartedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            Summary = Summary,
            Validation = new DadAlliancePresetValidation
            {
                AllianceACount = Validation.AllianceACount,
                AllianceBCount = Validation.AllianceBCount,
                AllianceCCount = Validation.AllianceCCount,
                Blockers = Validation.Blockers.ToList(),
                Summary = Validation.Summary,
            },
            Results = Results.Select(static result => result.Clone()).ToList(),
        };
}

public sealed class DadAllianceDiscordEnvelope
{
    public string Schema { get; set; } = "dad.alliance-pf/v1";
    public long TimestampUnixMs { get; set; }
    public string Nonce { get; set; } = string.Empty;
    public long KeyGeneration { get; set; } = 1;
    public ulong ApplicationId { get; set; }
    public ulong BotUserId { get; set; }
    public DadAutoPartyRole Role { get; set; }
    public string CoordinatorIdentity { get; set; } = string.Empty;
    public DadWorkerSessionId CoordinatorWorkerSessionId { get; set; } = new(string.Empty);
    public string EndpointFingerprint { get; set; } = string.Empty;
    public ulong TargetApplicationId { get; set; }
    public DadWorkerSessionId TargetWorkerSessionId { get; set; } = new(string.Empty);
    public string RecruitmentId { get; set; } = string.Empty;
    public DadCharacterKey TargetCharacterKey { get; set; } = new(string.Empty);
    public string TargetCharacterName { get; set; } = string.Empty;
    public string TargetCharacterWorld { get; set; } = string.Empty;
    public ulong TargetContentId { get; set; }
    public string LeaderName { get; set; } = string.Empty;
    public string LeaderWorld { get; set; } = string.Empty;
    public int Passcode { get; set; }
    public DadAllianceAssignment AssignedAlliance { get; set; }
    public int Attempt { get; set; }
    public DadAllianceRecruitmentState State { get; set; }
    public long StopGeneration { get; set; }
    public string Signature { get; set; } = string.Empty;
}

public sealed record DadAllianceDiscordValidationContext(
    ulong MessageAuthorId,
    ulong LocalApplicationId,
    DadCharacterKey LocalCharacterKey,
    DadAutoPartyPairing? CoordinatorPairing,
    DateTime UtcNow);
