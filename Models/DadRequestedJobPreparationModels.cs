namespace dad.Models;

public enum DadRequestedJobPreparationStatus
{
    NotRequested = 0,
    Pending = 1,
    AwaitingVerification = 2,
    AlreadyMatched = 3,
    Switched = 4,
    SoftFailed = 5,
    Cancelled = 6,
}

public readonly record struct DadRequestedJobPreparationKey(
    string RunId,
    DadWorkerSessionId WorkerSessionId,
    string SlotId,
    DadAccountKey AccountKey,
    DadCharacterKey CharacterKey,
    ulong ContentId,
    uint? RequiredJobId)
{
    public bool IsValid
        => !string.IsNullOrWhiteSpace(RunId) &&
           !WorkerSessionId.IsEmpty &&
           !string.IsNullOrWhiteSpace(SlotId) &&
           !AccountKey.IsEmpty &&
           !CharacterKey.IsEmpty &&
           ContentId != 0 &&
           (!RequiredJobId.HasValue || RequiredJobId.Value != 0);
}

public sealed class DadRequestedJobPreparationProof
{
    public DadRequestedJobPreparationKey Key { get; set; }
    public DadRequestedJobPreparationStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public int? SelectedGearsetId { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? EquipAcceptedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;

    public DadRequestedJobPreparationProof Clone()
        => new()
        {
            Key = Key,
            Status = Status,
            AttemptCount = AttemptCount,
            SelectedGearsetId = SelectedGearsetId,
            StartedAtUtc = StartedAtUtc,
            LastAttemptAtUtc = LastAttemptAtUtc,
            EquipAcceptedAtUtc = EquipAcceptedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            Summary = Summary,
            FailureReason = FailureReason,
        };
}

public static class DadRequestedJobPreparationProofRules
{
    public static bool Matches(
        DadRequestedJobPreparationProof? proof,
        DadRequestedJobPreparationKey expected)
        => proof != null &&
           expected.IsValid &&
           DadRequestedJobPreparationKeyRules.Matches(proof.Key, expected);

    public static bool IsTerminal(DadRequestedJobPreparationStatus status)
        => status is DadRequestedJobPreparationStatus.NotRequested or
            DadRequestedJobPreparationStatus.AlreadyMatched or
            DadRequestedJobPreparationStatus.Switched or
            DadRequestedJobPreparationStatus.SoftFailed or
            DadRequestedJobPreparationStatus.Cancelled;

    public static bool PermitsReadiness(
        DadRequestedJobPreparationProof? proof,
        DadRequestedJobPreparationKey expected)
    {
        if (!Matches(proof, expected))
            return false;

        return proof!.Status switch
        {
            DadRequestedJobPreparationStatus.NotRequested => !expected.RequiredJobId.HasValue,
            DadRequestedJobPreparationStatus.AlreadyMatched or
            DadRequestedJobPreparationStatus.Switched or
            DadRequestedJobPreparationStatus.SoftFailed => expected.RequiredJobId.HasValue,
            _ => false,
        };
    }

    public static bool PermitsReadiness(
        DadRequestedJobPreparationProof? proof,
        DadRequestedJobPreparationKey expected,
        uint currentJobId)
    {
        if (!PermitsReadiness(proof, expected))
            return false;

        return proof!.Status switch
        {
            DadRequestedJobPreparationStatus.AlreadyMatched or
            DadRequestedJobPreparationStatus.Switched => currentJobId == expected.RequiredJobId,
            DadRequestedJobPreparationStatus.SoftFailed => true,
            DadRequestedJobPreparationStatus.NotRequested => !expected.RequiredJobId.HasValue,
            _ => false,
        };
    }
}

public static class DadRequestedJobPreparationKeyRules
{
    public static bool Matches(
        DadRequestedJobPreparationKey left,
        DadRequestedJobPreparationKey right)
        => string.Equals(left.RunId, right.RunId, StringComparison.Ordinal) &&
           string.Equals(left.WorkerSessionId.Value, right.WorkerSessionId.Value, StringComparison.Ordinal) &&
           string.Equals(left.SlotId, right.SlotId, StringComparison.Ordinal) &&
           string.Equals(left.AccountKey.Value, right.AccountKey.Value, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.CharacterKey.Value, right.CharacterKey.Value, StringComparison.OrdinalIgnoreCase) &&
           left.ContentId == right.ContentId &&
           left.RequiredJobId == right.RequiredJobId;
}

public readonly record struct DadClassJobGearsetSnapshot(
    int GearsetId,
    uint ClassJobId,
    bool Exists,
    bool IsValid);

public static class DadClassJobGearsetSelectionRules
{
    public static int? SelectFirstMatching(
        IEnumerable<DadClassJobGearsetSnapshot>? gearsets,
        uint requiredJobId)
    {
        if (gearsets == null || requiredJobId == 0)
            return null;

        return gearsets
            .Where(entry =>
                entry.GearsetId is >= 0 and < 100 &&
                entry.Exists &&
                entry.IsValid &&
                entry.ClassJobId == requiredJobId)
            .Select(static entry => (int?)entry.GearsetId)
            .OrderBy(static gearsetId => gearsetId)
            .FirstOrDefault();
    }
}

public sealed class DadClassJobGearsetCatalogSnapshot
{
    public bool Available { get; set; }
    public List<DadClassJobGearsetSnapshot> Gearsets { get; set; } = [];
    public string FailureReason { get; set; } = string.Empty;

    public static DadClassJobGearsetCatalogSnapshot Success(
        IEnumerable<DadClassJobGearsetSnapshot> gearsets)
        => new()
        {
            Available = true,
            Gearsets = gearsets.ToList(),
        };

    public static DadClassJobGearsetCatalogSnapshot Unavailable(string failureReason)
        => new()
        {
            FailureReason = string.IsNullOrWhiteSpace(failureReason)
                ? "The gearset catalog is unavailable."
                : failureReason.Trim(),
        };
}

public readonly record struct DadClassJobEquipAttemptResult(bool Accepted, string FailureReason)
{
    public static DadClassJobEquipAttemptResult Success() => new(true, string.Empty);

    public static DadClassJobEquipAttemptResult Rejected(string failureReason)
        => new(
            false,
            string.IsNullOrWhiteSpace(failureReason)
                ? "The game rejected the gearset change."
                : failureReason.Trim());
}

public readonly record struct DadRequestedJobPreparationObservation(
    DadRequestedJobPreparationKey Identity,
    uint CurrentJobId,
    bool IsSafeToEquip,
    DadClassJobGearsetCatalogSnapshot? GearsetCatalog,
    string UnsafeReason = "");
