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
            var lease = request.Lease?.Clone() ?? new DadParticipantLeaseRecord
            {
                RunId = request.RunId,
                SlotId = request.SlotId,
                AssignedAccountKey = request.RequiredAccountKey,
                AssignedCharacterKey = request.RequiredCharacterKey,
                OwningWorkerSessionId = participant.WorkerSessionId,
                IssuedUtc = DateTime.UtcNow,
                RenewedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddSeconds(20),
                State = DadParticipantLeaseState.Pending,
            };

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
                !string.Equals(existing.RunId, request.RunId, StringComparison.Ordinal))
            {
                lease.State = DadParticipantLeaseState.Collided;
                lease.Summary = $"Character {characterKey} already leased by {existing.RunId}.";
                return BuildDecision(request, participant, granted: false, DadClaimState.Collided, DadParticipantLeaseState.Collided, lease.Summary, lease);
            }

            lease.State = DadParticipantLeaseState.Granted;
            lease.RenewedUtc = DateTime.UtcNow;
            localAcceptedLeasesByCharacter[characterKey] = lease.Clone();
            return BuildDecision(request, participant, granted: true, DadClaimState.Granted, DadParticipantLeaseState.Granted, $"Granted lease for {characterKey}.", lease);
        }
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
}
