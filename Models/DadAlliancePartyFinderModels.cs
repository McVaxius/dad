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
    public string TargetIslandId { get; set; } = string.Empty;
    public string TargetOwnerId { get; set; } = string.Empty;
    public string TargetOpaqueCharacterId { get; set; } = string.Empty;
    public DadCharacterKey TargetCharacterKey { get; set; } = new(string.Empty);
    public string TargetCharacterName { get; set; } = string.Empty;
    public string TargetCharacterWorld { get; set; } = string.Empty;
    public ulong TargetContentId { get; set; }
    public DadAllianceAssignment AssignedAlliance { get; set; }
    public bool CreateListingAsHost { get; set; }
    public int Passcode { get; set; }
    public int Attempt { get; set; }
    public DadAllianceRecruitmentState State { get; set; } = DadAllianceRecruitmentState.Validating;
    public long StopGeneration { get; set; }
    public DateTime IssuedAtUtc { get; set; } = DateTime.UtcNow;

    public string DedupeKey
        => $"{RecruitmentId.Trim()}|{(string.IsNullOrWhiteSpace(TargetOpaqueCharacterId) ? TargetCharacterKey.Value.Trim() : TargetOpaqueCharacterId.Trim())}";

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
            TargetIslandId = TargetIslandId,
            TargetOwnerId = TargetOwnerId,
            TargetOpaqueCharacterId = TargetOpaqueCharacterId,
            TargetCharacterKey = TargetCharacterKey,
            TargetCharacterName = TargetCharacterName,
            TargetCharacterWorld = TargetCharacterWorld,
            TargetContentId = TargetContentId,
            AssignedAlliance = AssignedAlliance,
            CreateListingAsHost = CreateListingAsHost,
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
    public string TargetIslandId { get; set; } = string.Empty;
    public string TargetOwnerId { get; set; } = string.Empty;
    public string TargetOpaqueCharacterId { get; set; } = string.Empty;
    public DadCharacterKey TargetCharacterKey { get; set; } = new(string.Empty);
    public long StopGeneration { get; set; }
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = string.Empty;
}

public sealed class DadAllianceRecruitmentResultDto
{
    public string RecruitmentId { get; set; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public string ParticipantOwnerId { get; set; } = string.Empty;
    public string TargetOpaqueCharacterId { get; set; } = string.Empty;
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
            ParticipantOwnerId = ParticipantOwnerId,
            TargetOpaqueCharacterId = TargetOpaqueCharacterId,
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
/// listing payloads, signing keys, transport secrets, and mailbox credentials.
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
    public int AllianceDCount { get; init; }
    public int AllianceECount { get; init; }
    public int AllianceFCount { get; init; }
    public int AllianceGCount { get; init; }
    public int TotalCount =>
        AllianceACount +
        AllianceBCount +
        AllianceCCount +
        AllianceDCount +
        AllianceECount +
        AllianceFCount +
        AllianceGCount;
    public List<string> Blockers { get; init; } = [];
    public bool IsValid => Blockers.Count == 0;
    public string Summary { get; init; } = string.Empty;
}

public sealed class DadAllianceRecruitmentTarget
{
    public string SlotId { get; set; } = string.Empty;
    public DadAllianceAssignment Assignment { get; set; }
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public string RegisteredIslandId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string OpaqueCharacterId { get; set; } = string.Empty;
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong ContentId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string WorldName { get; set; } = string.Empty;
}

internal static class DadAllianceAutoPartyContractMapping
{
    public static AutoParty.Contracts.AllianceRecruitmentOperation ToRecruitOperation(
        DadAllianceRecruitmentInstructionDto instruction,
        AutoParty.Contracts.ContractHeader header,
        Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        var validShape = instruction.CreateListingAsHost
            ? instruction.AssignedAlliance == DadAllianceAssignment.A &&
              instruction.State == DadAllianceRecruitmentState.CreatingListing
            : instruction.State == DadAllianceRecruitmentState.Searching;
        if (!validShape)
        {
            throw new ArgumentException(
                "Alliance recruitment host/state authority contradicts the protocol contract.",
                nameof(instruction));
        }
        ValidateTargetRoute(
            header,
            instruction.TargetIslandId,
            instruction.TargetOwnerId,
            instruction.TargetOpaqueCharacterId);
        return new AutoParty.Contracts.AllianceRecruitmentOperation(
            header,
            operationId,
            ParseRecruitmentId(instruction.RecruitmentId),
            AutoParty.Contracts.AllianceRecruitmentOperationKind.Recruit,
            new AutoParty.Contracts.OwnerId(instruction.TargetOwnerId),
            new AutoParty.Contracts.OpaqueCharacterId(instruction.TargetOpaqueCharacterId),
            instruction.LeaderName,
            instruction.LeaderWorld,
            (AutoParty.Contracts.AllianceAssignment)(int)instruction.AssignedAlliance,
            instruction.CreateListingAsHost,
            instruction.Passcode,
            instruction.Attempt,
            (AutoParty.Contracts.AllianceRecruitmentState)(int)instruction.State,
            instruction.StopGeneration,
            "dad-alliance-recruit");
    }

    public static AutoParty.Contracts.AllianceRecruitmentOperation ToCancelOperation(
        DadAllianceRecruitmentCancellationDto cancellation,
        AutoParty.Contracts.ContractHeader header,
        Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        ValidateTargetRoute(
            header,
            cancellation.TargetIslandId,
            cancellation.TargetOwnerId,
            cancellation.TargetOpaqueCharacterId);
        var safeCode = DadAutoPartyConfiguration.NormalizeSafeCode(cancellation.Reason);
        return new AutoParty.Contracts.AllianceRecruitmentOperation(
            header,
            operationId,
            ParseRecruitmentId(cancellation.RecruitmentId),
            AutoParty.Contracts.AllianceRecruitmentOperationKind.Cancel,
            new AutoParty.Contracts.OwnerId(cancellation.TargetOwnerId),
            new AutoParty.Contracts.OpaqueCharacterId(cancellation.TargetOpaqueCharacterId),
            string.Empty,
            string.Empty,
            AutoParty.Contracts.AllianceAssignment.None,
            false,
            0,
            0,
            AutoParty.Contracts.AllianceRecruitmentState.Stopped,
            cancellation.StopGeneration,
            safeCode.Length == 0 ? "dad-alliance-cancel" : safeCode);
    }

    public static DadAllianceRecruitmentInstructionDto FromRecruitOperation(
        AutoParty.Contracts.AllianceRecruitmentOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Kind != AutoParty.Contracts.AllianceRecruitmentOperationKind.Recruit)
            throw new ArgumentException("Expected an Alliance recruitment operation.", nameof(operation));
        return new DadAllianceRecruitmentInstructionDto
        {
            RecruitmentId = operation.RecruitmentId.ToString("N"),
            TargetIslandId = operation.Header.RecipientIslandId.Value,
            TargetOwnerId = operation.TargetOwnerId.Value,
            TargetOpaqueCharacterId = operation.TargetCharacterId.Value,
            LeaderName = operation.LeaderName,
            LeaderWorld = operation.LeaderWorld,
            AssignedAlliance = (DadAllianceAssignment)(int)operation.AssignedAlliance,
            CreateListingAsHost = operation.CreateListingAsHost,
            Passcode = operation.Passcode,
            Attempt = operation.Attempt,
            State = (DadAllianceRecruitmentState)(int)operation.RequestedState,
            StopGeneration = operation.StopGeneration,
            IssuedAtUtc = operation.Header.IssuedAt.UtcDateTime,
        };
    }

    public static DadAllianceRecruitmentCancellationDto FromCancelOperation(
        AutoParty.Contracts.AllianceRecruitmentOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Kind != AutoParty.Contracts.AllianceRecruitmentOperationKind.Cancel)
            throw new ArgumentException("Expected an Alliance cancellation operation.", nameof(operation));
        return new DadAllianceRecruitmentCancellationDto
        {
            RecruitmentId = operation.RecruitmentId.ToString("N"),
            TargetIslandId = operation.Header.RecipientIslandId.Value,
            TargetOwnerId = operation.TargetOwnerId.Value,
            TargetOpaqueCharacterId = operation.TargetCharacterId.Value,
            StopGeneration = operation.StopGeneration,
            RequestedAtUtc = operation.Header.IssuedAt.UtcDateTime,
            Reason = operation.SafeCode,
        };
    }

    public static AutoParty.Contracts.AllianceRecruitmentReceipt ToReceipt(
        DadAllianceRecruitmentResultDto result,
        AutoParty.Contracts.ContractHeader header,
        Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(result.ParticipantOwnerId) ||
            string.IsNullOrWhiteSpace(result.TargetOpaqueCharacterId))
        {
            throw new ArgumentException("Alliance central receipt identity is incomplete.", nameof(result));
        }
        return new AutoParty.Contracts.AllianceRecruitmentReceipt(
            header,
            operationId,
            ParseRecruitmentId(result.RecruitmentId),
            new AutoParty.Contracts.OwnerId(result.ParticipantOwnerId),
            new AutoParty.Contracts.OpaqueCharacterId(result.TargetOpaqueCharacterId),
            (AutoParty.Contracts.AllianceAssignment)(int)result.ExpectedAlliance,
            (AutoParty.Contracts.AllianceAssignment)(int)result.ObservedAlliance,
            result.Attempt,
            (AutoParty.Contracts.AllianceRecruitmentState)(int)result.State,
            (AutoParty.Contracts.AllianceRecruitmentResultKind)(int)result.ResultKind,
            result.Retryable,
            result.StopGeneration,
            $"dad-alliance-{result.ResultKind.ToString().ToLowerInvariant()}");
    }

    public static DadAllianceRecruitmentResultDto FromReceipt(
        AutoParty.Contracts.AllianceRecruitmentReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new DadAllianceRecruitmentResultDto
        {
            RecruitmentId = receipt.RecruitmentId.ToString("N"),
            ParticipantOwnerId = receipt.ParticipantOwnerId.Value,
            TargetOpaqueCharacterId = receipt.TargetCharacterId.Value,
            ExpectedAlliance = (DadAllianceAssignment)(int)receipt.ExpectedAlliance,
            ObservedAlliance = (DadAllianceAssignment)(int)receipt.ObservedAlliance,
            Attempt = receipt.Attempt,
            State = (DadAllianceRecruitmentState)(int)receipt.State,
            ResultKind = (DadAllianceRecruitmentResultKind)(int)receipt.ResultKind,
            Retryable = receipt.Retryable,
            StopGeneration = receipt.StopGeneration,
            ObservedAtUtc = receipt.Header.IssuedAt.UtcDateTime,
            Summary = receipt.SafeCode,
        };
    }

    private static Guid ParseRecruitmentId(string value)
        => Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new ArgumentException("Alliance recruitment id must be a non-empty GUID.", nameof(value));

    private static void ValidateTargetRoute(
        AutoParty.Contracts.ContractHeader header,
        string islandId,
        string ownerId,
        string opaqueCharacterId)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (!string.Equals(header.RecipientIslandId.Value, islandId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(ownerId) ||
            string.IsNullOrWhiteSpace(opaqueCharacterId))
        {
            throw new ArgumentException("Alliance central target route is incomplete or contradicts the contract header.");
        }
    }
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
                AllianceDCount = Validation.AllianceDCount,
                AllianceECount = Validation.AllianceECount,
                AllianceFCount = Validation.AllianceFCount,
                AllianceGCount = Validation.AllianceGCount,
                Blockers = Validation.Blockers.ToList(),
                Summary = Validation.Summary,
            },
            Results = Results.Select(static result => result.Clone()).ToList(),
        };
}
