using dad.Models;

namespace dad.Services;

public sealed class DadClaimService
{
    // Review C4: lease state is read/written from both the framework thread (coordinator) and the
    // transport background thread; every method that touches the dictionaries below holds this lock
    // so check-then-act sequences are atomic and enumeration never races a Remove.
    private readonly object gate = new();
    private readonly Dictionary<string, DadParticipantLeaseRecord> activeLeasesBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DadParticipantLeaseRecord> localAcceptedLeasesByCharacter = new(StringComparer.OrdinalIgnoreCase);

    public DadParticipantLeaseRecord IssueLease(DadClaimRequestDto request, DadParticipantSnapshot participant, TimeSpan leaseDuration)
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            if (activeLeasesBySlot.TryGetValue(request.SlotId, out var existing) &&
                string.Equals(existing.RunId, request.RunId, StringComparison.Ordinal))
            {
                existing.RenewedUtc = now;
                existing.ExpiresUtc = now + leaseDuration;
                existing.State = DadParticipantLeaseState.Pending;
                existing.Summary = $"Renewed lease for {request.RequiredCharacterKey}.";
                return existing.Clone();
            }

            var lease = new DadParticipantLeaseRecord
            {
                RunId = request.RunId,
                SlotId = request.SlotId,
                AssignedAccountKey = request.RequiredAccountKey,
                AssignedCharacterKey = request.RequiredCharacterKey,
                OwningWorkerSessionId = participant.WorkerSessionId,
                IssuedUtc = now,
                RenewedUtc = now,
                ExpiresUtc = now + leaseDuration,
                State = DadParticipantLeaseState.Pending,
                Summary = $"Issued pending lease for {request.RequiredCharacterKey}.",
            };

            activeLeasesBySlot[request.SlotId] = lease.Clone();
            return lease;
        }
    }

    public DadClaimDecisionDto TryClaimLocal(DadClaimRequestDto request, DadParticipantSnapshot participant)
    {
        lock (gate)
        {
            if (!TryValidateLease(request, participant, DateTime.UtcNow, out var validationReason))
            {
                var rejectedLease = request.Lease?.Clone() ?? new DadParticipantLeaseRecord();
                rejectedLease.State = DadParticipantLeaseState.Denied;
                rejectedLease.Summary = validationReason;
                return BuildDecision(
                    request,
                    participant,
                    granted: false,
                    DadClaimState.Denied,
                    DadParticipantLeaseState.Denied,
                    validationReason,
                    rejectedLease);
            }

            var lease = request.Lease.Clone();

            var characterKey = participant.ActiveCharacterKey.ToString();
            if (!string.IsNullOrWhiteSpace(request.RequiredAccountKey) &&
                !string.Equals(participant.ManagedAccountKey, request.RequiredAccountKey.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                lease.State = DadParticipantLeaseState.Denied;
                lease.Summary = $"Wrong account active ({participant.ManagedAccountKey}).";
                return BuildDecision(request, participant, granted: false, DadClaimState.Denied, DadParticipantLeaseState.Denied, lease.Summary, lease);
            }

            if (!string.IsNullOrWhiteSpace(request.RequiredCharacterKey) &&
                !string.Equals(characterKey, request.RequiredCharacterKey.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                lease.State = DadParticipantLeaseState.Denied;
                lease.Summary = $"Wrong character active ({characterKey}).";
                return BuildDecision(request, participant, granted: false, DadClaimState.Denied, DadParticipantLeaseState.Denied, lease.Summary, lease);
            }

            if (string.IsNullOrWhiteSpace(characterKey))
            {
                lease.State = DadParticipantLeaseState.Denied;
                lease.Summary = "Worker has no claimable character key.";
                return BuildDecision(request, participant, granted: false, DadClaimState.Denied, DadParticipantLeaseState.Denied, lease.Summary, lease);
            }

            if (localAcceptedLeasesByCharacter.TryGetValue(characterKey, out var existing) &&
                (!string.Equals(existing.RunId, request.RunId, StringComparison.Ordinal) ||
                 !string.Equals(existing.SlotId, request.SlotId, StringComparison.OrdinalIgnoreCase)))
            {
                lease.State = DadParticipantLeaseState.Collided;
                lease.Summary = $"Character {characterKey} already leased by run {existing.RunId}, slot {existing.SlotId}.";
                return BuildDecision(request, participant, granted: false, DadClaimState.Collided, DadParticipantLeaseState.Collided, lease.Summary, lease);
            }

            lease.State = DadParticipantLeaseState.Granted;
            lease.RenewedUtc = DateTime.UtcNow;
            localAcceptedLeasesByCharacter[characterKey] = lease.Clone();
            return BuildDecision(request, participant, granted: true, DadClaimState.Granted, DadParticipantLeaseState.Granted, $"Granted lease for {characterKey}.", lease);
        }
    }

    internal static bool TryValidateLease(
        DadClaimRequestDto request,
        DadParticipantSnapshot participant,
        DateTime nowUtc,
        out string reason)
    {
        reason = string.Empty;
        if (request == null || request.Lease == null)
            return Fail("Claim request is missing its required lease.", out reason);
        if (string.IsNullOrWhiteSpace(request.RunId) || string.IsNullOrWhiteSpace(request.SlotId) ||
            request.AuthorityWorkerSessionId.IsEmpty || request.RequiredAccountKey.IsEmpty ||
            request.RequiredCharacterKey.IsEmpty)
        {
            return Fail("Claim request is missing run, slot, authority, account, or character identity.", out reason);
        }

        var lease = request.Lease;
        if (!Same(lease.RunId, request.RunId) || !Same(lease.SlotId, request.SlotId) ||
            !DadRosterIdentity.SameAccount(lease.AssignedAccountKey, request.RequiredAccountKey) ||
            !Same(lease.AssignedCharacterKey.Value, request.RequiredCharacterKey.Value))
        {
            return Fail("Claim lease identity does not match the requested run, slot, account, and character.", out reason);
        }

        if (participant.WorkerSessionId.IsEmpty || lease.OwningWorkerSessionId.IsEmpty ||
            !Same(lease.OwningWorkerSessionId.Value, participant.WorkerSessionId.Value))
        {
            return Fail("Claim lease is not owned by the authenticated target worker.", out reason);
        }

        nowUtc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
        if (lease.IssuedUtc == default || lease.RenewedUtc == default || lease.ExpiresUtc == default ||
            lease.RenewedUtc < lease.IssuedUtc || lease.ExpiresUtc <= lease.RenewedUtc ||
            lease.ExpiresUtc <= nowUtc)
        {
            return Fail("Claim lease timing is invalid or expired.", out reason);
        }

        return true;
    }

    public void AcknowledgeLease(DadClaimDecisionDto decision)
    {
        if (decision.Lease == null || string.IsNullOrWhiteSpace(decision.Lease.SlotId))
            return;

        lock (gate)
        {
            if (decision.LeaseState == DadParticipantLeaseState.Released)
            {
                activeLeasesBySlot.Remove(decision.Lease.SlotId);
                return;
            }

            activeLeasesBySlot[decision.Lease.SlotId] = decision.Lease.Clone();
        }
    }

    public IReadOnlyList<DadParticipantLeaseRecord> GetLeasesForRun(string runId)
    {
        lock (gate)
        {
            return activeLeasesBySlot.Values
                .Where(lease => string.Equals(lease.RunId, runId, StringComparison.Ordinal))
                .OrderBy(static lease => lease.SlotId, StringComparer.OrdinalIgnoreCase)
                .Select(static lease => lease.Clone())
                .ToList();
        }
    }

    public bool HasClaimsForRun(string runId)
    {
        lock (gate)
        {
            return activeLeasesBySlot.Values.Any(lease => string.Equals(lease.RunId, runId, StringComparison.Ordinal)) ||
                   localAcceptedLeasesByCharacter.Values.Any(lease => string.Equals(lease.RunId, runId, StringComparison.Ordinal));
        }
    }

    public IReadOnlyList<DadParticipantLeaseRecord> SweepExpiredLeases(DateTime utcNow)
    {
        lock (gate)
        {
            var expired = activeLeasesBySlot.Values
                .Where(lease => utcNow >= lease.ExpiresUtc)
                .Select(static lease => lease.Clone())
                .ToList();

            foreach (var lease in expired)
            {
                lease.State = DadParticipantLeaseState.Stale;
                activeLeasesBySlot.Remove(lease.SlotId);
            }

            var localExpiredKeys = localAcceptedLeasesByCharacter
                .Where(pair => utcNow >= pair.Value.ExpiresUtc)
                .Select(static pair => pair.Key)
                .ToList();

            foreach (var key in localExpiredKeys)
                localAcceptedLeasesByCharacter.Remove(key);

            return expired;
        }
    }

    public void ReleaseClaims(string runId)
    {
        lock (gate)
        {
            var slotIds = activeLeasesBySlot
                .Where(pair => string.Equals(pair.Value.RunId, runId, StringComparison.Ordinal))
                .Select(static pair => pair.Key)
                .ToList();

            foreach (var slotId in slotIds)
                activeLeasesBySlot.Remove(slotId);

            var characterKeys = localAcceptedLeasesByCharacter
                .Where(pair => string.Equals(pair.Value.RunId, runId, StringComparison.Ordinal))
                .Select(static pair => pair.Key)
                .ToList();

            foreach (var key in characterKeys)
                localAcceptedLeasesByCharacter.Remove(key);
        }
    }

    public int ReleaseAllClaims()
    {
        lock (gate)
        {
            var released = activeLeasesBySlot.Count + localAcceptedLeasesByCharacter.Count;
            activeLeasesBySlot.Clear();
            localAcceptedLeasesByCharacter.Clear();
            return released;
        }
    }

    private static DadClaimDecisionDto BuildDecision(
        DadClaimRequestDto request,
        DadParticipantSnapshot participant,
        bool granted,
        DadClaimState claimState,
        DadParticipantLeaseState leaseState,
        string reason,
        DadParticipantLeaseRecord lease)
    {
        var snapshot = participant.Clone();
        snapshot.ClaimState = claimState;
        snapshot.LeaseState = leaseState;
        snapshot.LeaseIssuedUtc = lease.IssuedUtc;
        snapshot.LeaseRenewedUtc = lease.RenewedUtc;
        snapshot.LeaseExpiresUtc = lease.ExpiresUtc;

        return new DadClaimDecisionDto
        {
            RunId = request.RunId,
            WorkerSessionId = snapshot.WorkerSessionId,
            Granted = granted,
            ClaimState = claimState,
            LeaseState = leaseState,
            CharacterKey = snapshot.ActiveCharacterKey,
            Reason = reason,
            Lease = lease,
            Snapshot = snapshot,
        };
    }

    private static bool Same(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Fail(string message, out string reason)
    {
        reason = message;
        return false;
    }
}
