namespace dad.Models;

// Pure, time-driven gate. The caller owns live-state reads and the one unsafe gearset mutation gateway.
public sealed class DadRequestedJobPreparationGate
{
    public const int MaxAttemptCount = 6;
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(5);

    private readonly Dictionary<DadRequestedJobPreparationKey, DadRequestedJobPreparationProof> proofs =
        new(DadRequestedJobPreparationKeyComparer.Instance);

    public bool NeedsGearsetCatalog(
        DadRequestedJobPreparationKey expected,
        DadRequestedJobPreparationObservation observation,
        DateTime nowUtc)
    {
        if (!expected.IsValid ||
            !expected.RequiredJobId.HasValue ||
            !DadRequestedJobPreparationKeyRules.Matches(expected, observation.Identity) ||
            observation.CurrentJobId == expected.RequiredJobId.Value ||
            !observation.IsSafeToEquip)
        {
            return false;
        }

        if (!proofs.TryGetValue(expected, out var proof))
            return true;

        return proof.Status == DadRequestedJobPreparationStatus.Pending &&
               (!proof.LastAttemptAtUtc.HasValue || nowUtc - proof.LastAttemptAtUtc.Value >= RetryInterval);
    }

    public DadRequestedJobPreparationProof Advance(
        DadRequestedJobPreparationKey expected,
        DadRequestedJobPreparationObservation observation,
        DateTime nowUtc,
        Func<int, DadClassJobEquipAttemptResult>? tryEquip)
    {
        if (!expected.IsValid)
            return CancelInvalid(expected, nowUtc);

        var proof = GetOrCreate(expected, nowUtc);

        if (!DadRequestedJobPreparationKeyRules.Matches(expected, observation.Identity))
            return CancelInternal(proof, nowUtc, "The live preparation identity no longer matches the requested assignment.");

        if (!expected.RequiredJobId.HasValue)
            return proof.Clone();

        var requiredJobId = expected.RequiredJobId.Value;

        if (proof.Status == DadRequestedJobPreparationStatus.Cancelled)
            return proof.Clone();

        if (proof.Status == DadRequestedJobPreparationStatus.SoftFailed)
            return proof.Clone();

        if (proof.Status is DadRequestedJobPreparationStatus.AlreadyMatched or DadRequestedJobPreparationStatus.Switched)
        {
            if (observation.CurrentJobId != requiredJobId)
                return CancelInternal(proof, nowUtc, "The active class/job changed after preparation completed.");

            return proof.Clone();
        }

        if (observation.CurrentJobId == requiredJobId)
        {
            proof.Status = proof.Status == DadRequestedJobPreparationStatus.AwaitingVerification
                ? DadRequestedJobPreparationStatus.Switched
                : DadRequestedJobPreparationStatus.AlreadyMatched;
            proof.UpdatedAtUtc = nowUtc;
            proof.FailureReason = string.Empty;
            proof.Summary = proof.Status == DadRequestedJobPreparationStatus.Switched
                ? $"Switched to requested class/job {requiredJobId}."
                : $"Already on requested class/job {requiredJobId}.";
            return proof.Clone();
        }

        if (proof.Status == DadRequestedJobPreparationStatus.AwaitingVerification)
        {
            if (proof.EquipAcceptedAtUtc.HasValue &&
                nowUtc - proof.EquipAcceptedAtUtc.Value < VerificationTimeout)
            {
                return proof.Clone();
            }

            return SoftFail(
                proof,
                nowUtc,
                $"The game accepted gearset {proof.SelectedGearsetId?.ToString() ?? "?"}, but class/job {requiredJobId} was not observed within five seconds.");
        }

        if (proof.LastAttemptAtUtc.HasValue && nowUtc - proof.LastAttemptAtUtc.Value < RetryInterval)
            return proof.Clone();

        if (!observation.IsSafeToEquip)
        {
            return RecordTransientFailure(
                proof,
                nowUtc,
                string.IsNullOrWhiteSpace(observation.UnsafeReason)
                    ? "The client is not currently safe for a class/job change."
                    : observation.UnsafeReason.Trim());
        }

        var catalog = observation.GearsetCatalog;
        if (catalog?.Available != true)
        {
            return RecordTransientFailure(
                proof,
                nowUtc,
                catalog?.FailureReason ?? "The gearset catalog is unavailable.");
        }

        var gearsetId = DadClassJobGearsetSelectionRules.SelectFirstMatching(catalog.Gearsets, requiredJobId);
        if (!gearsetId.HasValue)
        {
            return SoftFail(
                proof,
                nowUtc,
                $"No valid saved gearset exists for requested class/job {requiredJobId}.");
        }

        proof.SelectedGearsetId = gearsetId;

        if (tryEquip == null)
            return RecordTransientFailure(proof, nowUtc, "The gearset mutation gateway is unavailable.");

        DadClassJobEquipAttemptResult attempt;
        try
        {
            attempt = tryEquip(gearsetId.Value);
        }
        catch (Exception ex)
        {
            return RecordTransientFailure(
                proof,
                nowUtc,
                $"The gearset mutation gateway threw {ex.GetType().Name}: {ex.Message}");
        }

        proof.AttemptCount++;
        proof.LastAttemptAtUtc = nowUtc;
        proof.UpdatedAtUtc = nowUtc;

        if (!attempt.Accepted)
            return CompleteTransientFailure(proof, nowUtc, attempt.FailureReason);

        proof.Status = DadRequestedJobPreparationStatus.AwaitingVerification;
        proof.EquipAcceptedAtUtc = nowUtc;
        proof.FailureReason = string.Empty;
        proof.Summary = $"Gearset {gearsetId.Value} was accepted; waiting to observe class/job {requiredJobId}.";
        return proof.Clone();
    }

    public DadRequestedJobPreparationProof Cancel(
        DadRequestedJobPreparationKey expected,
        DateTime nowUtc,
        string reason)
    {
        var proof = GetOrCreate(expected, nowUtc);
        return CancelInternal(proof, nowUtc, reason);
    }

    public bool TryGet(
        DadRequestedJobPreparationKey expected,
        out DadRequestedJobPreparationProof proof)
    {
        if (proofs.TryGetValue(expected, out var stored))
        {
            proof = stored.Clone();
            return true;
        }

        proof = new DadRequestedJobPreparationProof();
        return false;
    }

    public void Reset() => proofs.Clear();

    private DadRequestedJobPreparationProof GetOrCreate(
        DadRequestedJobPreparationKey expected,
        DateTime nowUtc)
    {
        if (proofs.TryGetValue(expected, out var existing))
            return existing;

        var status = expected.RequiredJobId.HasValue
            ? DadRequestedJobPreparationStatus.Pending
            : DadRequestedJobPreparationStatus.NotRequested;
        var proof = new DadRequestedJobPreparationProof
        {
            Key = expected,
            Status = status,
            StartedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            Summary = status == DadRequestedJobPreparationStatus.NotRequested
                ? "No class/job change was requested; keep the current class/job."
                : $"Waiting to prepare requested class/job {expected.RequiredJobId}.",
        };
        proofs[expected] = proof;
        return proof;
    }

    private DadRequestedJobPreparationProof CancelInvalid(
        DadRequestedJobPreparationKey expected,
        DateTime nowUtc)
    {
        var proof = GetOrCreate(expected, nowUtc);
        return CancelInternal(proof, nowUtc, "The requested class/job preparation identity is incomplete or invalid.");
    }

    private static DadRequestedJobPreparationProof CancelInternal(
        DadRequestedJobPreparationProof proof,
        DateTime nowUtc,
        string reason)
    {
        proof.Status = DadRequestedJobPreparationStatus.Cancelled;
        proof.UpdatedAtUtc = nowUtc;
        proof.FailureReason = NormalizeReason(reason, "Class/job preparation was cancelled.");
        proof.Summary = proof.FailureReason;
        return proof.Clone();
    }

    private static DadRequestedJobPreparationProof RecordTransientFailure(
        DadRequestedJobPreparationProof proof,
        DateTime nowUtc,
        string reason)
    {
        proof.AttemptCount++;
        proof.LastAttemptAtUtc = nowUtc;
        proof.UpdatedAtUtc = nowUtc;
        return CompleteTransientFailure(proof, nowUtc, reason);
    }

    private static DadRequestedJobPreparationProof CompleteTransientFailure(
        DadRequestedJobPreparationProof proof,
        DateTime nowUtc,
        string reason)
    {
        var normalizedReason = NormalizeReason(reason, "The class/job change attempt was rejected.");
        if (proof.AttemptCount >= MaxAttemptCount)
        {
            return SoftFail(
                proof,
                nowUtc,
                $"Class/job preparation soft-failed after {proof.AttemptCount} attempts: {normalizedReason}");
        }

        proof.Status = DadRequestedJobPreparationStatus.Pending;
        proof.FailureReason = normalizedReason;
        proof.Summary = $"Class/job preparation attempt {proof.AttemptCount}/{MaxAttemptCount} is pending retry: {normalizedReason}";
        return proof.Clone();
    }

    private static DadRequestedJobPreparationProof SoftFail(
        DadRequestedJobPreparationProof proof,
        DateTime nowUtc,
        string reason)
    {
        proof.Status = DadRequestedJobPreparationStatus.SoftFailed;
        proof.UpdatedAtUtc = nowUtc;
        proof.FailureReason = NormalizeReason(reason, "Class/job preparation soft-failed.");
        proof.Summary = proof.FailureReason;
        return proof.Clone();
    }

    private static string NormalizeReason(string? reason, string fallback)
        => string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();

    private sealed class DadRequestedJobPreparationKeyComparer : IEqualityComparer<DadRequestedJobPreparationKey>
    {
        public static DadRequestedJobPreparationKeyComparer Instance { get; } = new();

        public bool Equals(DadRequestedJobPreparationKey x, DadRequestedJobPreparationKey y)
            => DadRequestedJobPreparationKeyRules.Matches(x, y);

        public int GetHashCode(DadRequestedJobPreparationKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.RunId, StringComparer.Ordinal);
            hash.Add(obj.WorkerSessionId.Value, StringComparer.Ordinal);
            hash.Add(obj.SlotId, StringComparer.Ordinal);
            hash.Add(obj.AccountKey.Value, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.CharacterKey.Value, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.ContentId);
            hash.Add(obj.RequiredJobId);
            return hash.ToHashCode();
        }
    }
}
