using AutoParty.Contracts;
using dad.Models;

namespace dad.Services;

public interface IAutoPartyPolicyFacade
{
    DadAutoPartyPolicyDecision VerifyIdentity(OwnerIdentity owner, IslandIdentity island);
    DadAutoPartyPolicyDecision VerifyReplay(ContractHeader header);
    DadAutoPartyPolicyDecision IntersectGrant(RunProposal proposal, SessionPermission requiredPermissions);
    DadAutoPartyPolicyDecision Reserve(Reservation reservation, DadAutoPartySessionMode mode);
    DadAutoPartyPolicyDecision VerifyPreflight(PreflightResult preflight);
    DadAutoPartyPolicyDecision AcquireLease(SessionLease lease);
    DadAutoPartyPolicyDecision Revoke(Revocation revocation);
    DadAutoPartyPolicyDecision SetOwnerVeto(OwnerId ownerId, bool vetoed, string safeReason);
    DadAutoPartyPolicyDecision AuthorizeExecution(ExecutionOperation operation);
    DadAutoPartyAuthorizationDecision GetProposalAuthorization(Guid proposalId);
    int ActiveSessionCount { get; }
    void StopAll(string safeReason);
}

public sealed class DadAutoPartyPolicyFacade : IAutoPartyPolicyFacade
{
    private readonly object gate = new();
    private readonly DadAutoPartyConfiguration configuration;
    private readonly Func<bool> dadEnabled;
    private readonly Func<bool> localSafetyAllowsExecution;
    private readonly Dictionary<Guid, ProposalState> proposals = [];
    private readonly Dictionary<string, Guid> activeIslandSessions = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> replayedMessageIds = [];
    private readonly HashSet<string> vetoedOwners = new(StringComparer.Ordinal);
    private readonly HashSet<string> revokedTargets = new(StringComparer.Ordinal);
    private long stateGeneration;
    private bool stopped;

    public DadAutoPartyPolicyFacade(
        DadAutoPartyConfiguration configuration,
        Func<bool> dadEnabled,
        Func<bool>? localSafetyAllowsExecution = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.dadEnabled = dadEnabled ?? throw new ArgumentNullException(nameof(dadEnabled));
        this.localSafetyAllowsExecution = localSafetyAllowsExecution ?? (static () => true);
        stateGeneration = Math.Max(1, configuration.StateGeneration);
    }

    public int ActiveSessionCount
    {
        get
        {
            lock (gate)
            {
                ExpireSessions(DateTime.UtcNow);
                return activeIslandSessions.Count;
            }
        }
    }

    public DadAutoPartyPolicyDecision VerifyIdentity(OwnerIdentity owner, IslandIdentity island)
    {
        lock (gate)
        {
            if (!IsLocallyEnabled())
                return Denied("dad-autoparty-disabled");
            if (owner.OwnerId != island.OwnerId || owner.HomeIslandId != island.IslandId)
                return Denied("dad-identity-owner-island-mismatch");
            if (owner.KeyGeneration < 1 || island.KeyGeneration < 1 ||
                owner.KeyGeneration != island.KeyGeneration ||
                !string.Equals(owner.PublicKeyId, island.PublicKeyId, StringComparison.Ordinal))
                return Denied("dad-identity-key-mismatch");

            var pairing = configuration.Pairings.FirstOrDefault(candidate =>
                candidate.RevokedAtUtc == null &&
                string.Equals(candidate.OwnerId, owner.OwnerId.Value, StringComparison.Ordinal) &&
                string.Equals(candidate.IslandId, island.IslandId.Value, StringComparison.Ordinal) &&
                candidate.KeyGeneration == island.KeyGeneration &&
                string.Equals(candidate.PublicKeyFingerprint, owner.PublicKeyId, StringComparison.Ordinal));
            return pairing == null
                ? Denied("dad-identity-not-paired")
                : Allowed("dad-identity-verified");
        }
    }

    public DadAutoPartyPolicyDecision VerifyReplay(ContractHeader header)
    {
        lock (gate)
        {
            if (!IsLocallyEnabled())
                return Denied("dad-autoparty-disabled");
            var now = DateTimeOffset.UtcNow;
            if (header.SchemaVersion != AutoPartyProtocol.CurrentVersion ||
                header.MessageId == Guid.Empty ||
                string.IsNullOrWhiteSpace(header.IdempotencyKey) ||
                header.IdempotencyKey.Length > AutoPartyProtocol.MaximumIdentifierLength ||
                header.Nonce.IsDefaultOrEmpty ||
                header.Nonce.Length != AutoPartyProtocol.ContractNonceBytes ||
                header.IssuedAt > now + TimeSpan.FromMinutes(2) ||
                header.ExpiresAt <= now ||
                header.ExpiresAt <= header.IssuedAt)
                return Denied("dad-contract-header-invalid");
            if (!replayedMessageIds.Add(header.MessageId))
                return Denied("dad-contract-replay-denied");
            return Allowed("dad-contract-fresh");
        }
    }

    public DadAutoPartyPolicyDecision IntersectGrant(
        RunProposal proposal,
        SessionPermission requiredPermissions)
    {
        lock (gate)
        {
            if (!IsLocallyEnabled())
                return Denied("dad-autoparty-disabled");
            if (proposal.ProposalId == Guid.Empty || proposal.Participants.IsDefaultOrEmpty)
                return Denied("dad-proposal-invalid");
            if (vetoedOwners.Contains(proposal.RequesterOwnerId.Value))
                return Denied("dad-owner-veto");
            if (revokedTargets.Contains(proposal.ProposalId.ToString("D")))
                return Denied("dad-proposal-revoked");
            if (!IsPaired(proposal.RequesterOwnerId.Value, proposal.Header.SenderIslandId.Value))
                return Denied("dad-proposal-sender-not-paired");

            var now = DateTime.UtcNow;
            foreach (var participant in proposal.Participants)
            {
                if (vetoedOwners.Contains(participant.OwnerId.Value))
                    return Denied("dad-participant-owner-veto");
                var grant = configuration.Grants.FirstOrDefault(candidate =>
                    candidate.IsValid &&
                    candidate.IssuedAtUtc <= now &&
                    candidate.ExpiresAtUtc > now &&
                    string.Equals(candidate.OwnerId, participant.OwnerId.Value, StringComparison.Ordinal) &&
                    string.Equals(candidate.IslandId, participant.OwnerIslandId.Value, StringComparison.Ordinal) &&
                    string.Equals(candidate.OpaqueCharacterId, participant.CharacterId.Value, StringComparison.Ordinal) &&
                    string.Equals(candidate.RequestedJobId, participant.RequestedJob.Value, StringComparison.Ordinal) &&
                    string.Equals(candidate.ActivityId, proposal.ActivityId.Value, StringComparison.Ordinal) &&
                    (candidate.Permissions & requiredPermissions) == requiredPermissions &&
                    !revokedTargets.Contains(candidate.GrantId));
                if (grant == null)
                    return Denied("dad-grant-intersection-empty");
            }

            var islandId = proposal.Header.RecipientIslandId.Value;
            if (string.IsNullOrWhiteSpace(islandId))
                return Denied("dad-proposal-recipient-island-missing");
            proposals[proposal.ProposalId] = new ProposalState(
                proposal.ProposalId,
                islandId,
                proposal.RequesterOwnerId.Value,
                proposal.ActivityId.Value,
                requiredPermissions,
                DadAutoPartySessionMode.Local,
                DateTime.MinValue,
                false,
                false,
                NextGeneration());
            return Allowed("dad-grant-intersection-accepted");
        }
    }

    public DadAutoPartyPolicyDecision Reserve(Reservation reservation, DadAutoPartySessionMode mode)
    {
        lock (gate)
        {
            if (!TryGetProposal(reservation.ProposalId, reservation.OwnerId.Value, out var state, out var denial))
                return denial;
            ExpireSessions(DateTime.UtcNow);
            if (activeIslandSessions.TryGetValue(state.IslandId, out var activeProposal) &&
                activeProposal != reservation.ProposalId)
                return Denied("dad-island-session-already-active");
            if (reservation.ExpectedStateGeneration != state.StateGeneration)
                return Denied("dad-reservation-generation-mismatch");

            state = state with
            {
                Mode = mode,
                Reserved = true,
                StateGeneration = NextGeneration(),
            };
            proposals[reservation.ProposalId] = state;
            activeIslandSessions[state.IslandId] = reservation.ProposalId;
            return Allowed("dad-reservation-accepted");
        }
    }

    public DadAutoPartyPolicyDecision VerifyPreflight(PreflightResult preflight)
    {
        lock (gate)
        {
            if (!TryGetProposal(preflight.ProposalId, preflight.OwnerId.Value, out var state, out var denial))
                return denial;
            if (!state.Reserved)
                return Denied("dad-preflight-without-reservation");
            if (!preflight.Ready || !preflight.SafeBlockers.IsDefaultOrEmpty)
                return Denied("dad-preflight-not-ready");
            if (preflight.ExpectedStateGeneration != state.StateGeneration)
                return Denied("dad-preflight-generation-mismatch");
            state = state with { PreflightReady = true, StateGeneration = NextGeneration() };
            proposals[preflight.ProposalId] = state;
            return Allowed("dad-preflight-ready");
        }
    }

    public DadAutoPartyPolicyDecision AcquireLease(SessionLease lease)
    {
        lock (gate)
        {
            if (!TryGetProposal(lease.ProposalId, lease.OwnerId.Value, out var state, out var denial))
                return denial;
            if (!state.Reserved || !state.PreflightReady)
                return Denied("dad-lease-prerequisites-missing");
            if (lease.ExpectedStateGeneration != state.StateGeneration)
                return Denied("dad-lease-generation-mismatch");
            var now = DateTimeOffset.UtcNow;
            if (lease.LeaseExpiresAt <= now || lease.LeaseExpiresAt > now + TimeSpan.FromMinutes(30))
                return Denied("dad-lease-expiry-invalid");
            if ((lease.Permissions & state.Permissions) != lease.Permissions)
                return Denied("dad-lease-permission-escalation");
            state = state with
            {
                LeaseExpiresAtUtc = lease.LeaseExpiresAt.UtcDateTime,
                StateGeneration = NextGeneration(),
            };
            proposals[lease.ProposalId] = state;
            return Allowed("dad-lease-active");
        }
    }

    public DadAutoPartyPolicyDecision Revoke(Revocation revocation)
    {
        lock (gate)
        {
            if (revocation.RevocationId == Guid.Empty || string.IsNullOrWhiteSpace(revocation.TargetId))
                return Denied("dad-revocation-invalid");
            revokedTargets.Add(revocation.TargetId);
            if (revocation.TargetKind == RevocationTargetKind.Session &&
                Guid.TryParse(revocation.TargetId, out var proposalId) &&
                proposals.TryGetValue(proposalId, out var state))
            {
                proposals[proposalId] = state with { Revoked = true, StateGeneration = NextGeneration() };
                activeIslandSessions.Remove(state.IslandId);
            }
            if (revocation.TargetKind == RevocationTargetKind.Identity)
                vetoedOwners.Add(revocation.TargetId);
            return Allowed("dad-revocation-applied");
        }
    }

    public DadAutoPartyPolicyDecision SetOwnerVeto(OwnerId ownerId, bool vetoed, string safeReason)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(ownerId.Value))
                return Denied("dad-owner-veto-invalid");
            if (vetoed)
            {
                vetoedOwners.Add(ownerId.Value);
                foreach (var pair in proposals.Where(pair =>
                             string.Equals(pair.Value.OwnerId, ownerId.Value, StringComparison.Ordinal)).ToList())
                {
                    proposals[pair.Key] = pair.Value with { Revoked = true, StateGeneration = NextGeneration() };
                    activeIslandSessions.Remove(pair.Value.IslandId);
                }
            }
            else
            {
                vetoedOwners.Remove(ownerId.Value);
            }
            return Allowed(vetoed ? "dad-owner-veto-applied" : "dad-owner-veto-cleared");
        }
    }

    public DadAutoPartyPolicyDecision AuthorizeExecution(ExecutionOperation operation)
    {
        lock (gate)
        {
            if (!IsLocallyEnabled() || !configuration.ExecutionEnabled)
                return Denied("dad-autoparty-execution-disabled");
            if (stopped)
                return Denied("dad-owner-stop-active");
            if (!localSafetyAllowsExecution())
                return Denied("dad-local-safety-veto");
            if (!TryGetProposal(operation.ProposalId, operation.OwnerId.Value, out var state, out var denial))
                return denial;
            if (state.LeaseExpiresAtUtc <= DateTime.UtcNow)
            {
                activeIslandSessions.Remove(state.IslandId);
                return Denied("dad-session-lease-expired");
            }
            if (operation.ExpectedStateGeneration != state.StateGeneration)
                return Denied("dad-execution-generation-mismatch");

            var required = PermissionFor(operation.Kind);
            if ((state.Permissions & required) != required)
                return Denied("dad-execution-permission-denied");
            if (!string.Equals(operation.ActivityId.Value, state.ActivityId, StringComparison.Ordinal))
                return Denied("dad-execution-activity-mismatch");

            var participantGrant = configuration.Grants.Any(grant =>
                grant.IsValid &&
                grant.ExpiresAtUtc > DateTime.UtcNow &&
                !revokedTargets.Contains(grant.GrantId) &&
                string.Equals(grant.OwnerId, operation.OwnerId.Value, StringComparison.Ordinal) &&
                string.Equals(grant.OpaqueCharacterId, operation.CharacterId.Value, StringComparison.Ordinal) &&
                string.Equals(grant.RequestedJobId, operation.RequestedJob.Value, StringComparison.Ordinal) &&
                string.Equals(grant.ActivityId, operation.ActivityId.Value, StringComparison.Ordinal) &&
                (grant.Permissions & required) == required);
            return participantGrant
                ? Allowed("dad-execution-authorized")
                : Denied("dad-execution-strict-job-grant-denied");
        }
    }

    public DadAutoPartyAuthorizationDecision GetProposalAuthorization(Guid proposalId)
    {
        lock (gate)
        {
            if (!IsLocallyEnabled())
                return new(DadAutoPartyAuthorizationState.Waiting, "dad-autoparty-disabled", proposalId);
            ExpireSessions(DateTime.UtcNow);
            if (!proposals.TryGetValue(proposalId, out var state))
                return new(DadAutoPartyAuthorizationState.Waiting, "dad-proposal-authorization-pending", proposalId);
            if (state.Revoked || revokedTargets.Contains(proposalId.ToString("D")))
                return new(DadAutoPartyAuthorizationState.Denied, "dad-proposal-revoked", proposalId);
            if (vetoedOwners.Contains(state.OwnerId))
                return new(DadAutoPartyAuthorizationState.Denied, "dad-owner-veto", proposalId);
            if (state.LeaseExpiresAtUtc <= DateTime.UtcNow)
                return new(DadAutoPartyAuthorizationState.Waiting, "dad-session-lease-pending", proposalId);
            return new(DadAutoPartyAuthorizationState.Authorized, "dad-proposal-authorized", proposalId);
        }
    }

    public void StopAll(string safeReason)
    {
        lock (gate)
        {
            stopped = true;
            foreach (var pair in proposals.ToList())
                proposals[pair.Key] = pair.Value with { Revoked = true, StateGeneration = NextGeneration() };
            activeIslandSessions.Clear();
        }
    }

    public void ClearStopAfterExplicitEnable()
    {
        lock (gate)
            stopped = false;
    }

    private bool TryGetProposal(
        Guid proposalId,
        string ownerId,
        out ProposalState state,
        out DadAutoPartyPolicyDecision denial)
    {
        if (!IsLocallyEnabled())
        {
            state = default!;
            denial = Denied("dad-autoparty-disabled");
            return false;
        }
        if (stopped)
        {
            state = default!;
            denial = Denied("dad-owner-stop-active");
            return false;
        }
        if (!proposals.TryGetValue(proposalId, out state!) || state.Revoked)
        {
            denial = Denied("dad-proposal-not-authorized");
            return false;
        }
        if (!string.Equals(state.OwnerId, ownerId, StringComparison.Ordinal))
        {
            denial = Denied("dad-proposal-owner-mismatch");
            return false;
        }
        if (vetoedOwners.Contains(ownerId))
        {
            denial = Denied("dad-owner-veto");
            return false;
        }
        denial = Allowed("dad-proposal-found");
        return true;
    }

    private bool IsLocallyEnabled()
        => dadEnabled() && configuration.Enabled;

    private bool IsPaired(string ownerId, string islandId)
        => configuration.Pairings.Any(pairing =>
            pairing.RevokedAtUtc == null &&
            string.Equals(pairing.OwnerId, ownerId, StringComparison.Ordinal) &&
            string.Equals(pairing.IslandId, islandId, StringComparison.Ordinal));

    private void ExpireSessions(DateTime now)
    {
        foreach (var state in proposals.Values.Where(state =>
                     !state.Revoked &&
                     state.LeaseExpiresAtUtc != DateTime.MinValue &&
                     state.LeaseExpiresAtUtc <= now).ToList())
            activeIslandSessions.Remove(state.IslandId);
    }

    private long NextGeneration()
    {
        stateGeneration = Math.Max(stateGeneration + 1, 1);
        configuration.StateGeneration = stateGeneration;
        return stateGeneration;
    }

    private DadAutoPartyPolicyDecision Allowed(string safeCode)
        => new(true, safeCode, stateGeneration);

    private DadAutoPartyPolicyDecision Denied(string safeCode)
        => new(false, safeCode, stateGeneration);

    private static SessionPermission PermissionFor(ExecutionOperationKind kind)
        => kind switch
        {
            ExecutionOperationKind.Reserve => SessionPermission.Reserve,
            ExecutionOperationKind.Prepare => SessionPermission.Preflight,
            ExecutionOperationKind.Form => SessionPermission.FormParty,
            ExecutionOperationKind.Queue => SessionPermission.Queue,
            ExecutionOperationKind.Cancel => SessionPermission.Cancel,
            ExecutionOperationKind.Settle => SessionPermission.Complete,
            ExecutionOperationKind.Restore => SessionPermission.Complete,
            _ => SessionPermission.Execute,
        };

    private sealed record ProposalState(
        Guid ProposalId,
        string IslandId,
        string OwnerId,
        string ActivityId,
        SessionPermission Permissions,
        DadAutoPartySessionMode Mode,
        DateTime LeaseExpiresAtUtc,
        bool Reserved,
        bool PreflightReady,
        long StateGeneration,
        bool Revoked = false);
}
