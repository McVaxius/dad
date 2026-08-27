using System.Collections.Immutable;
using AutoParty.Contracts;
using dad.Models;

namespace dad.Services;

internal enum DadAutoPartyParticipantStage
{
    ProposalPending = 0,
    PreflightPending = 1,
    LeasePending = 2,
    Ready = 3,
    FormPending = 4,
    Formed = 5,
    QueuePending = 6,
    Queued = 7,
    SettlementPending = 8,
    Settled = 9,
    RestorePending = 10,
    Restored = 11,
    CancelPending = 12,
    Cancelled = 13,
    Revoked = 14,
    Expired = 15,
    Failed = 16,
}

internal enum DadAutoPartyParticipantCommandKind
{
    Proposal = 0,
    Execution = 1,
    Revocation = 2,
    ProposalRenewal = 3,
    IntegrationProfile = 4,
}

internal sealed record DadAutoPartyRemoteProfileRequest(
    string OwnerId,
    string IslandId,
    string OpaqueCharacterId,
    Guid ProposalId,
    string DisplayLabel);

internal sealed record DadAutoPartyRemoteProfileResult(
    bool Success,
    ImmutableArray<byte> FramedProfile,
    string SafeCode)
{
    public static DadAutoPartyRemoteProfileResult Unavailable(string safeCode)
        => new(false, [], safeCode);
}

internal sealed record DadAutoPartyParticipantRequest(
    string SlotId,
    string OwnerId,
    string IslandId,
    string OpaqueCharacterId,
    uint RequestedJobId,
    bool IsLeader,
    bool IsInviter);

internal sealed record DadAutoPartyParticipantSnapshot(
    Guid ProposalId,
    string RunId,
    string SlotId,
    string OwnerId,
    string IslandId,
    string OpaqueCharacterId,
    uint RequestedJobId,
    DadAutoPartyParticipantStage Stage,
    long StateGeneration,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset ProposalExpiresAt,
    DateTimeOffset ObservedAt,
    string SafeCode,
    IReadOnlyList<ulong> ObservedPartyContentIds,
    EndpointExecutionModuleReference? ActiveModuleReference = null,
    int NextModuleIndex = 0)
{
    public bool ReservationAccepted => Stage >= DadAutoPartyParticipantStage.PreflightPending &&
                                       Stage < DadAutoPartyParticipantStage.Revoked;

    public bool PreflightReady => Stage >= DadAutoPartyParticipantStage.LeasePending &&
                                  Stage < DadAutoPartyParticipantStage.Revoked;

    public bool LeaseActive(DateTimeOffset now) =>
        Stage >= DadAutoPartyParticipantStage.Ready &&
        Stage < DadAutoPartyParticipantStage.Revoked &&
        LeaseExpiresAt > now;

    public bool CommandRouteActive(DateTimeOffset now) =>
        Stage < DadAutoPartyParticipantStage.Revoked &&
        ProposalExpiresAt > now;

    public bool IsTerminal => Stage is DadAutoPartyParticipantStage.Settled or
        DadAutoPartyParticipantStage.Restored or
        DadAutoPartyParticipantStage.Cancelled or
        DadAutoPartyParticipantStage.Revoked or
        DadAutoPartyParticipantStage.Expired or
        DadAutoPartyParticipantStage.Failed;
}

internal sealed record DadAutoPartyParticipantCommand(
    Guid CommandId,
    DadAutoPartyParticipantCommandKind CommandKind,
    Guid ProposalId,
    string RunId,
    string SlotId,
    string OwnerId,
    string IslandId,
    string OpaqueCharacterId,
    uint RequestedJobId,
    string ActivityId,
    ExecutionOperationKind? OperationKind,
    long ExpectedStateGeneration,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DadExpectedPartyInviter? Inviter = null,
    IReadOnlyList<DadNativePartyInviteTarget>? PartyInviteTargets = null,
    IReadOnlyList<DadAutoPartyParticipantRequest>? Participants = null,
    EndpointExecutionPlan? ExecutionPlan = null,
    EndpointExecutionModuleReference? ExecutionModuleReference = null,
    long RevocationGeneration = 0,
    string SafeCode = "",
    bool FormationOnly = false,
    DateTimeOffset? PreviousProposalExpiresAt = null,
    long RenewalGeneration = 0,
    ImmutableArray<byte> FrenRiderProfile = default);

internal sealed record DadAutoPartyParticipantCommandBatch(
    Guid DispatchLeaseId,
    DateTimeOffset LeaseExpiresAt,
    IReadOnlyList<DadAutoPartyParticipantCommand> Commands)
{
    public static DadAutoPartyParticipantCommandBatch Empty { get; } =
        new(Guid.Empty, default, []);
}

/// <summary>
/// Runtime-only registered-island participant state. The bridge deliberately performs no network
/// work: a background semantic relay pump consumes its bounded commands and feeds authenticated
/// protocol evidence back through the Observe methods. Framework updates only read immutable
/// snapshots and therefore remain fail-closed while that evidence is absent.
/// </summary>
internal sealed class DadAutoPartyParticipantBridge
{
    private const int MaximumReplayEntries = 4096;
    private const int MaximumPendingCommands = 256;
    private const int MaximumRetainedSessions = 64;
    private static readonly TimeSpan ProposalLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan OperationLifetime = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private readonly DadAutoPartyConfiguration? configuration;
    private readonly Func<IReadOnlyList<DadAutoPartyRemoteBinding>> currentRemoteBindingsProvider;
    private readonly Func<IReadOnlyList<DadAutoPartyCrewCandidate>> currentLocalCrewProvider;
    private readonly Func<bool> useFrenRiderProvider;
    private readonly Func<DadAutoPartyRemoteProfileRequest, DadAutoPartyRemoteProfileResult>
        remoteProfileProvider;
    private readonly Dictionary<Guid, ProposalRuntime> proposals = [];
    private readonly Dictionary<Guid, PendingOperation> operations = [];
    private readonly Dictionary<Guid, DateTimeOffset> replayedMessages = [];
    private readonly Dictionary<string, long> revokedIslands = new(StringComparer.Ordinal);
    private readonly LinkedList<Guid> pendingCommandOrder = [];
    private readonly Dictionary<Guid, PendingCommand> pendingCommands = [];
    private Func<IReadOnlyList<DadFrozenRunSlot>, DateTimeOffset, string?>? directoryAuthorityGate;

    public DadAutoPartyParticipantBridge(
        DadAutoPartyConfiguration? configuration,
        Func<IReadOnlyList<DadAutoPartyRemoteBinding>>? currentRemoteBindingsProvider = null,
        Func<IReadOnlyList<DadAutoPartyCrewCandidate>>? currentLocalCrewProvider = null,
        Func<bool>? useFrenRiderProvider = null,
        Func<DadAutoPartyRemoteProfileRequest, DadAutoPartyRemoteProfileResult>? remoteProfileProvider = null)
    {
        this.configuration = configuration;
        this.currentRemoteBindingsProvider = currentRemoteBindingsProvider ??
            (() => configuration?.RemoteBindings ?? []);
        this.currentLocalCrewProvider = currentLocalCrewProvider ?? (() => []);
        this.useFrenRiderProvider = useFrenRiderProvider ?? (() => false);
        this.remoteProfileProvider = remoteProfileProvider ??
            (_ => DadAutoPartyRemoteProfileResult.Unavailable("dad-frenrider-profile-provider-unavailable"));
    }

    public void ConfigureDirectoryAuthorityGate(
        Func<IReadOnlyList<DadFrozenRunSlot>, DateTimeOffset, string?> gate)
        => directoryAuthorityGate = gate ?? throw new ArgumentNullException(nameof(gate));

    public bool TryBindRun(
        DadRunPlan plan,
        DadRunSlotManifest manifest,
        DateTimeOffset now,
        out string blocker)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(manifest);
        blocker = string.Empty;
        var remoteSlots = manifest.Slots
            .Where(static slot => slot.RouteKind == DadRunSlotRouteKind.RegisteredIsland)
            .ToList();
        if (remoteSlots.Count == 0)
            return true;

        if (configuration is not { IsRegistrationActive: true })
            return Fail("AutoParty registration is not active for registered-island participants.", out blocker);

        var authorityBlocker = directoryAuthorityGate?.Invoke(remoteSlots, now);
        if (!string.IsNullOrWhiteSpace(authorityBlocker))
            return Fail(authorityBlocker, out blocker);
        if (!Guid.TryParse(plan.Request.Orchestration.AutoPartyProposalId, out var proposalId) ||
            proposalId == Guid.Empty)
        {
            return Fail("Registered-island run admission is missing its runtime proposal id.", out blocker);
        }

        lock (gate)
        {
            Sweep(now);
            if (proposals.TryGetValue(proposalId, out var existing))
            {
                if (string.Equals(existing.RunId, plan.Request.RequestId, StringComparison.Ordinal) &&
                    existing.ModuleId == plan.CompositeModuleId &&
                    SameSlots(existing.Slots.Values, remoteSlots))
                    return true;
                return Fail("AutoParty proposal id is already bound to a different runtime run.", out blocker);
            }

            var bindings = new List<(DadFrozenRunSlot Slot, DadAutoPartyRemoteBinding Binding)>();
            foreach (var slot in remoteSlots)
            {
                if (string.IsNullOrWhiteSpace(slot.OwnerId) || string.IsNullOrWhiteSpace(slot.IslandId) ||
                    string.IsNullOrWhiteSpace(slot.OpaqueCharacterId) || !slot.RequiredJobId.HasValue ||
                    slot.IsLeader != slot.IsInviter)
                {
                    return Fail($"{slot.SlotId} has an incomplete registered-island runtime route.", out blocker);
                }

                if (IsIslandRevoked(slot.IslandId) || configuration.Deauthentications.Any(item =>
                        string.Equals(item.PeerIslandId, slot.IslandId, StringComparison.Ordinal)))
                {
                    return Fail($"{slot.SlotId} registered-island route is locally deauthenticated.", out blocker);
                }

                var matches = currentRemoteBindingsProvider()
                    .Where(binding => binding.IsValid &&
                        string.Equals(binding.OwnerId, slot.OwnerId, StringComparison.Ordinal) &&
                        string.Equals(binding.IslandId, slot.IslandId, StringComparison.Ordinal) &&
                        string.Equals(binding.OpaqueCharacterId, slot.OpaqueCharacterId, StringComparison.Ordinal) &&
                        string.Equals(binding.RequestedJobId, slot.RequiredJobId.Value.ToString(), StringComparison.Ordinal))
                    .ToList();
                if (matches.Count != 1)
                    return Fail($"{slot.SlotId} no longer has one exact current registered-island binding.", out blocker);
                if (matches[0].OwnsQueueAuthority != slot.IsLeader)
                    return Fail($"{slot.SlotId} registered-island queue authority no longer matches the frozen Slot1 route.", out blocker);
                bindings.Add((slot, matches[0]));
            }

            var useFrenRider = useFrenRiderProvider();
            var activityId = BuildActivityId(manifest, plan.Orchestration.AutoPartyFormationOnly);
            if (!TryBuildExecutionPlan(
                    plan,
                    manifest,
                    activityId,
                    useFrenRider,
                    out var participants,
                    out var executionPlan,
                    out blocker))
            {
                return false;
            }

            var frozenProfiles = new Dictionary<string, ImmutableArray<byte>>(StringComparer.OrdinalIgnoreCase);
            if (useFrenRider)
            {
                foreach (var (slot, _) in bindings.OrderBy(static pair =>
                             DadPlannerSlotRules.GetSlotSortKey(pair.Slot.SlotId)))
                {
                    DadAutoPartyRemoteProfileResult resolved;
                    try
                    {
                        resolved = remoteProfileProvider(new DadAutoPartyRemoteProfileRequest(
                            slot.OwnerId,
                            slot.IslandId,
                            slot.OpaqueCharacterId,
                            proposalId,
                            string.IsNullOrWhiteSpace(slot.CharacterKey.Value)
                                ? slot.SlotId
                                : slot.CharacterKey.Value));
                    }
                    catch
                    {
                        return Fail($"{slot.SlotId} FrenRider remote profile could not be resolved.", out blocker);
                    }

                    if (!resolved.Success || resolved.FramedProfile.IsDefaultOrEmpty)
                    {
                        var detail = string.IsNullOrWhiteSpace(resolved.SafeCode)
                            ? "profile unavailable"
                            : resolved.SafeCode;
                        return Fail($"{slot.SlotId} FrenRider remote profile is unavailable ({detail}).", out blocker);
                    }
                    try
                    {
                        _ = FrenRiderProfileCodec.Decode(resolved.FramedProfile);
                    }
                    catch (ProtocolException)
                    {
                        return Fail($"{slot.SlotId} FrenRider remote profile is invalid or oversized.", out blocker);
                    }
                    if (!frozenProfiles.TryAdd(slot.SlotId, ImmutableArray.CreateRange(resolved.FramedProfile)))
                        return Fail($"{slot.SlotId} FrenRider remote profile route is duplicated.", out blocker);
                }
            }

            var proposalCommandCount = bindings
                .Select(static pair => pair.Slot.IslandId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            var profileCommandCount = frozenProfiles.Count;
            if (pendingCommands.Count + proposalCommandCount + profileCommandCount > MaximumPendingCommands)
                return Fail("AutoParty relay command capacity is unavailable for atomic run admission.", out blocker);

            var runtime = new ProposalRuntime(
                proposalId,
                plan.Request.RequestId,
                plan.CompositeModuleId,
                activityId,
                plan.Orchestration.AutoPartyFormationOnly,
                executionPlan,
                now,
                now + ProposalLifetime,
                bindings.ToDictionary(
                    static pair => pair.Slot.SlotId,
                    pair => new SlotRuntime(pair.Slot.Clone(), now),
                    StringComparer.OrdinalIgnoreCase));
            proposals[proposalId] = runtime;
            foreach (var island in runtime.Slots.Values
                         .GroupBy(static slot => slot.Slot.IslandId, StringComparer.Ordinal))
            {
                var slots = island
                    .OrderBy(static slot => DadPlannerSlotRules.GetSlotSortKey(slot.Slot.SlotId))
                    .ToList();
                var first = slots[0].Slot;
                if (!Enqueue(new DadAutoPartyParticipantCommand(
                    Guid.NewGuid(),
                    DadAutoPartyParticipantCommandKind.Proposal,
                    proposalId,
                    runtime.RunId,
                    first.SlotId,
                    first.OwnerId,
                    first.IslandId,
                    first.OpaqueCharacterId,
                    first.RequiredJobId!.Value,
                    activityId,
                    null,
                    1,
                    now,
                    runtime.ExpiresAt,
                    Participants: participants,
                    ExecutionPlan: executionPlan)))
                {
                    proposals.Remove(proposalId);
                    RemoveCommandsForProposal(proposalId);
                    return Fail("AutoParty relay command capacity changed during atomic run admission.", out blocker);
                }
            }
            foreach (var slot in runtime.Slots.Values
                         .OrderBy(static slot => DadPlannerSlotRules.GetSlotSortKey(slot.Slot.SlotId)))
            {
                if (!frozenProfiles.TryGetValue(slot.Slot.SlotId, out var framedProfile))
                    continue;
                if (!Enqueue(new DadAutoPartyParticipantCommand(
                        Guid.NewGuid(),
                        DadAutoPartyParticipantCommandKind.IntegrationProfile,
                        proposalId,
                        runtime.RunId,
                        slot.Slot.SlotId,
                        slot.Slot.OwnerId,
                        slot.Slot.IslandId,
                        slot.Slot.OpaqueCharacterId,
                        slot.Slot.RequiredJobId!.Value,
                        activityId,
                        null,
                        1,
                        now,
                        runtime.ExpiresAt,
                        FrenRiderProfile: framedProfile)))
                {
                    proposals.Remove(proposalId);
                    RemoveCommandsForProposal(proposalId);
                    return Fail("AutoParty relay command capacity changed during atomic run admission.", out blocker);
                }
            }
            TrimSessions();
            return true;
        }
    }

    public DadAutoPartyParticipantSnapshot? GetSnapshot(Guid proposalId, string slotId, DateTimeOffset now)
    {
        lock (gate)
        {
            Sweep(now);
            return TryGetSlot(proposalId, slotId, out _, out var slot)
                ? ToSnapshot(proposalId, proposals[proposalId], slot)
                : null;
        }
    }

    public bool IsOperationComplete(
        Guid proposalId,
        string slotId,
        ExecutionOperationKind kind,
        DateTimeOffset now)
    {
        lock (gate)
        {
            Sweep(now);
            var matching = operations.Values.Where(operation =>
                operation.ProposalId == proposalId &&
                string.Equals(operation.SlotId, slotId, StringComparison.OrdinalIgnoreCase) &&
                operation.Kind == kind).ToList();
            return matching.Count > 0 && matching.All(static operation => operation.Completed);
        }
    }

    public DadParticipantSnapshot ResolveParticipant(
        Guid proposalId,
        DadFrozenRunSlot slot,
        DateTimeOffset now,
        out string blocker)
    {
        lock (gate)
        {
            Sweep(now);
            if (!TryGetSlot(proposalId, slot.SlotId, out var proposal, out var runtime) ||
                !SameSlot(runtime.Slot, slot))
            {
                blocker = $"{slot.SlotId} is waiting for its exact AutoParty runtime proposal binding.";
                return BuildParticipant(proposalId, slot, null, routeActive: false, blocker);
            }

            var snapshot = ToSnapshot(proposalId, proposal, runtime);
            if (!snapshot.CommandRouteActive(now))
            {
                blocker = $"{slot.SlotId} AutoParty command route is unavailable ({snapshot.SafeCode}).";
                return BuildParticipant(proposalId, slot, snapshot, routeActive: false, blocker);
            }

            blocker = string.Empty;
            var status = snapshot.Stage < DadAutoPartyParticipantStage.Ready
                ? "Authenticated registered-island command route is bound; participant readiness is not requested."
                : snapshot.SafeCode;
            return BuildParticipant(proposalId, slot, snapshot, routeActive: true, status);
        }
    }

    public IReadOnlyList<DadParticipantLeaseRecord> GetLeaseSnapshots(Guid proposalId, DateTimeOffset now)
    {
        lock (gate)
        {
            Sweep(now);
            if (!proposals.TryGetValue(proposalId, out var proposal))
                return [];
            return proposal.Slots.Values
                .Where(slot => slot.LeaseExpiresAt > now &&
                               slot.Stage >= DadAutoPartyParticipantStage.Ready &&
                               slot.Stage < DadAutoPartyParticipantStage.Revoked)
                .Select(slot => new DadParticipantLeaseRecord
                {
                    RunId = proposal.RunId,
                    SlotId = slot.Slot.SlotId,
                    OwningWorkerSessionId = RuntimeWorkerId(proposalId, slot.Slot.SlotId),
                    IssuedUtc = slot.LeaseIssuedAt?.UtcDateTime ?? proposal.CreatedAt.UtcDateTime,
                    RenewedUtc = slot.ObservedAt.UtcDateTime,
                    ExpiresUtc = slot.LeaseExpiresAt!.Value.UtcDateTime,
                    State = DadParticipantLeaseState.Granted,
                    Summary = "Authenticated AutoParty lease is active.",
                })
                .ToList();
        }
    }

    public bool TryGetInviteTarget(
        Guid proposalId,
        string slotId,
        DateTimeOffset now,
        out DadNativePartyInviteTarget target,
        out string blocker)
    {
        lock (gate)
        {
            Sweep(now);
            target = new DadNativePartyInviteTarget();
            if (!TryGetSlot(proposalId, slotId, out _, out var slot) ||
                slot.InviteTarget == null || slot.InviteTargetExpiresAt <= now)
            {
                blocker = $"{slotId} Form command payload is waiting for a fresh endpoint-encrypted invite target.";
                return false;
            }
            target = slot.InviteTarget.Clone();
            blocker = string.Empty;
            return true;
        }
    }

    public bool ObserveReservation(Reservation reservation, DateTimeOffset now, out string safeCode)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (gate)
        {
            Sweep(now);
            if (!TryAcceptHeader(reservation.Header, reservation.ProposalId, now, out safeCode) ||
                reservation.ReservationId == Guid.Empty || reservation.ExpectedStateGeneration < 1 ||
                reservation.ObservedStateGeneration < reservation.ExpectedStateGeneration)
                return false;
            if (!TryFindSlot(
                    reservation.ProposalId,
                    reservation.Header.SenderIslandId.Value,
                    reservation.OwnerId.Value,
                    reservation.CharacterId.Value,
                    out var slot))
            {
                safeCode = "dad-remote-reservation-route-mismatch";
                return false;
            }
            if ((slot.Stage is not DadAutoPartyParticipantStage.ProposalPending and
                 not DadAutoPartyParticipantStage.PreflightPending) &&
                (slot.ReservationId != reservation.ReservationId || slot.ReservationId == Guid.Empty))
            {
                safeCode = "dad-remote-reservation-order-invalid";
                return false;
            }
            slot.ReservationId = reservation.ReservationId;
            if (slot.Stage < DadAutoPartyParticipantStage.PreflightPending)
            {
                slot.StateGeneration = reservation.ObservedStateGeneration;
                slot.Stage = DadAutoPartyParticipantStage.PreflightPending;
            }
            slot.ObservedAt = now;
            slot.SafeCode = "dad-remote-reservation-accepted";
            CommitReplay(reservation.Header);
            safeCode = slot.SafeCode;
            return true;
        }
    }

    public bool ObservePreflight(PreflightResult preflight, DateTimeOffset now, out string safeCode)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        lock (gate)
        {
            Sweep(now);
            if (!TryAcceptHeader(preflight.Header, preflight.ProposalId, now, out safeCode) ||
                preflight.ReadinessGeneration < 1 || preflight.ExpectedStateGeneration < 1 ||
                preflight.ObservedStateGeneration < preflight.ExpectedStateGeneration ||
                preflight.SafeBlockers.IsDefault ||
                (preflight.Ready ? !preflight.SafeBlockers.IsEmpty : preflight.SafeBlockers.IsEmpty))
                return false;
            var slots = FindOwnerSlots(
                preflight.ProposalId,
                preflight.Header.SenderIslandId.Value,
                preflight.OwnerId.Value);
            if (slots.Count == 0 || slots.Any(static slot =>
                    (slot.Stage != DadAutoPartyParticipantStage.PreflightPending &&
                     slot.Stage < DadAutoPartyParticipantStage.LeasePending) ||
                     slot.ReservationId == Guid.Empty))
            {
                safeCode = "dad-remote-preflight-order-invalid";
                return false;
            }
            if (slots.Any(slot =>
                    slot.Stage == DadAutoPartyParticipantStage.PreflightPending
                        ? preflight.ExpectedStateGeneration != slot.StateGeneration ||
                          preflight.ObservedStateGeneration < slot.StateGeneration ||
                          preflight.ReadinessGeneration <= slot.ReadinessGeneration ||
                          (slot.ReadinessGeneration > 0 &&
                           preflight.ObservedStateGeneration <= slot.StateGeneration)
                        : !preflight.Ready ||
                          preflight.ReadinessGeneration != slot.ReadinessGeneration))
            {
                safeCode = "dad-remote-preflight-generation-replay";
                return false;
            }
            if (!preflight.Ready)
            {
                var blocker = preflight.SafeBlockers[0];
                foreach (var slot in slots)
                {
                    slot.StateGeneration = preflight.ObservedStateGeneration;
                    slot.ReadinessGeneration = preflight.ReadinessGeneration;
                    slot.Stage = DadAutoPartyParticipantStage.PreflightPending;
                    slot.ObservedAt = now;
                    slot.SafeCode = blocker;
                }
                CommitReplay(preflight.Header);
                safeCode = blocker;
                return true;
            }
            foreach (var slot in slots.Where(static slot =>
                         slot.Stage == DadAutoPartyParticipantStage.PreflightPending))
            {
                slot.StateGeneration = preflight.ObservedStateGeneration;
                slot.ReadinessGeneration = preflight.ReadinessGeneration;
                slot.Stage = DadAutoPartyParticipantStage.LeasePending;
                slot.ObservedAt = now;
                slot.SafeCode = "dad-remote-preflight-ready";
            }
            CommitReplay(preflight.Header);
            safeCode = "dad-remote-preflight-ready";
            return true;
        }
    }

    public bool ObserveLease(SessionLease lease, DateTimeOffset now, out string safeCode)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (gate)
        {
            Sweep(now);
            if (!TryAcceptHeader(lease.Header, lease.ProposalId, now, out safeCode) ||
                lease.LeaseId == Guid.Empty || lease.ExpectedStateGeneration < 1 ||
                lease.ObservedStateGeneration < lease.ExpectedStateGeneration ||
                lease.LeaseExpiresAt <= now || lease.LeaseExpiresAt > now + TimeSpan.FromMinutes(30))
                return false;
            var proposal = proposals[lease.ProposalId];
            var requiredPermissions = SessionPermission.Reserve | SessionPermission.Preflight |
                                      SessionPermission.FormParty | SessionPermission.Cancel |
                                      SessionPermission.Complete;
            if (!proposal.FormationOnly)
                requiredPermissions |= SessionPermission.Queue | SessionPermission.Execute;
            if ((lease.Permissions & requiredPermissions) != requiredPermissions)
            {
                safeCode = "dad-remote-lease-permission-missing";
                return false;
            }
            var slots = FindOwnerSlots(
                lease.ProposalId,
                lease.Header.SenderIslandId.Value,
                lease.OwnerId.Value);
            if (slots.Count == 0 || slots.Any(slot =>
                    slot.Stage != DadAutoPartyParticipantStage.LeasePending &&
                    !(slot.Stage == DadAutoPartyParticipantStage.Ready &&
                      slot.LeaseId != Guid.Empty &&
                      slot.LeaseId == lease.LeaseId)))
            {
                safeCode = "dad-remote-lease-order-invalid";
                return false;
            }
            if (slots.Any(slot => lease.ObservedStateGeneration < slot.StateGeneration))
            {
                safeCode = "dad-remote-lease-generation-replay";
                return false;
            }
            foreach (var slot in slots)
            {
                slot.LeaseId = lease.LeaseId;
                slot.LeaseIssuedAt ??= now;
                slot.LeaseExpiresAt = lease.LeaseExpiresAt;
                slot.StateGeneration = lease.ObservedStateGeneration;
                slot.Stage = DadAutoPartyParticipantStage.Ready;
                slot.ObservedAt = now;
                slot.SafeCode = "dad-remote-lease-active";
            }
            CommitReplay(lease.Header);
            safeCode = "dad-remote-lease-active";
            return true;
        }
    }

    public bool ObserveInviteTarget(
        ContractHeader header,
        Guid proposalId,
        OwnerId ownerId,
        OpaqueCharacterId characterId,
        DadWorkerSessionId workerSessionId,
        DadAccountKey accountKey,
        DadCharacterKey characterKey,
        ulong contentId,
        string exactCharacterName,
        ushort worldId,
        DateTimeOffset validUntil,
        DateTimeOffset now,
        out string safeCode)
    {
        ArgumentNullException.ThrowIfNull(header);
        lock (gate)
        {
            Sweep(now);
            if (!TryAcceptHeader(header, proposalId, now, out safeCode) ||
                workerSessionId.IsEmpty || accountKey.IsEmpty || characterKey.IsEmpty ||
                !IsBoundedLocatorValue(workerSessionId.Value, AutoPartyProtocol.MaximumIdentifierLength) ||
                !IsBoundedLocatorValue(accountKey.Value, AutoPartyProtocol.MaximumIdentifierLength) ||
                !IsBoundedLocatorValue(characterKey.Value, AutoPartyProtocol.MaximumIdentifierLength) ||
                !IsBoundedLocatorValue(exactCharacterName, AutoPartyProtocol.MaximumDisplayLabelLength) ||
                contentId == 0 || worldId == 0 || validUntil.Offset != TimeSpan.Zero ||
                validUntil <= now || validUntil > now + OperationLifetime || validUntil > header.ExpiresAt ||
                !TryFindSlot(
                    proposalId,
                    header.SenderIslandId.Value,
                    ownerId.Value,
                    characterId.Value,
                    out var slot))
            {
                safeCode = "dad-remote-invite-target-invalid";
                return false;
            }

            var proposal = proposals[proposalId];
            slot.InviteTarget = new DadNativePartyInviteTarget
            {
                RunId = proposal.RunId,
                ModuleId = proposal.ModuleId,
                SlotId = slot.Slot.SlotId,
                WorkerSessionId = workerSessionId,
                AccountKey = accountKey,
                CharacterKey = characterKey,
                ContentId = contentId,
                CharacterName = exactCharacterName,
                WorldId = worldId,
            };
            slot.InviteTargetExpiresAt = validUntil;
            slot.ObservedAt = now;
            CommitReplay(header);
            safeCode = "dad-remote-invite-target-ready";
            return true;
        }
    }

    public bool ObserveInviteTarget(
        ContractHeader header,
        Guid proposalId,
        OwnerId ownerId,
        OpaqueCharacterId characterId,
        DadNativePartyInviteTarget target,
        DateTimeOffset validUntil,
        DateTimeOffset now,
        out string safeCode)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(target);
        lock (gate)
        {
            Sweep(now);
            if (!TryAcceptHeader(header, proposalId, now, out safeCode) ||
                target.ContentId == 0 || target.WorldId == 0 || string.IsNullOrWhiteSpace(target.CharacterName) ||
                validUntil <= now || validUntil > now + OperationLifetime || validUntil > header.ExpiresAt ||
                !TryFindSlot(
                    proposalId,
                    header.SenderIslandId.Value,
                    ownerId.Value,
                    characterId.Value,
                    out var slot) ||
                !string.Equals(target.SlotId, slot.Slot.SlotId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(target.RunId, proposals[proposalId].RunId, StringComparison.Ordinal))
            {
                safeCode = "dad-remote-invite-target-invalid";
                return false;
            }
            var proposal = proposals[proposalId];
            if (target.ModuleId != proposal.ModuleId ||
                proposal.FormationOnly != (target.ModuleId == DadModuleId.None))
            {
                safeCode = "dad-remote-invite-target-invalid";
                return false;
            }
            slot.InviteTarget = target.Clone();
            slot.InviteTargetExpiresAt = validUntil;
            slot.ObservedAt = now;
            CommitReplay(header);
            safeCode = "dad-remote-invite-target-ready";
            return true;
        }
    }

    public bool RequestOperation(
        Guid proposalId,
        string slotId,
        ExecutionOperationKind kind,
        int? moduleIndex,
        DadExpectedPartyInviter? inviter,
        DateTimeOffset now,
        out string safeCode)
        => RequestOperation(
            proposalId,
            slotId,
            kind,
            moduleIndex,
            inviter,
            partyInviteTargets: null,
            now,
            out safeCode);

    public bool RequestOperation(
        Guid proposalId,
        string slotId,
        ExecutionOperationKind kind,
        int? moduleIndex,
        DadExpectedPartyInviter? inviter,
        IReadOnlyList<DadNativePartyInviteTarget>? partyInviteTargets,
        DateTimeOffset now,
        out string safeCode)
    {
        lock (gate)
        {
            Sweep(now);
            if (!TryGetSlot(proposalId, slotId, out var proposal, out var slot) ||
                !HasActiveCommandBinding(proposal, slot, now))
            {
                safeCode = "dad-remote-operation-route-unavailable";
                return false;
            }
            var allowed = kind switch
            {
                ExecutionOperationKind.Form =>
                    slot.Stage is DadAutoPartyParticipantStage.ProposalPending or
                        DadAutoPartyParticipantStage.PreflightPending or
                        DadAutoPartyParticipantStage.LeasePending or
                        DadAutoPartyParticipantStage.Ready or
                        DadAutoPartyParticipantStage.FormPending or DadAutoPartyParticipantStage.Formed or
                        DadAutoPartyParticipantStage.Settled,
                ExecutionOperationKind.Queue => !proposal.FormationOnly &&
                    slot.Stage is DadAutoPartyParticipantStage.Formed or DadAutoPartyParticipantStage.QueuePending or
                        DadAutoPartyParticipantStage.Queued,
                ExecutionOperationKind.Settle => !proposal.FormationOnly &&
                    slot.Stage is DadAutoPartyParticipantStage.Queued or
                        DadAutoPartyParticipantStage.SettlementPending or DadAutoPartyParticipantStage.Settled,
                ExecutionOperationKind.Restore =>
                    slot.Stage is DadAutoPartyParticipantStage.Ready or DadAutoPartyParticipantStage.FormPending or
                        DadAutoPartyParticipantStage.Formed or DadAutoPartyParticipantStage.QueuePending or
                        DadAutoPartyParticipantStage.Queued or DadAutoPartyParticipantStage.SettlementPending or
                        DadAutoPartyParticipantStage.Settled or DadAutoPartyParticipantStage.CancelPending or
                        DadAutoPartyParticipantStage.Cancelled or DadAutoPartyParticipantStage.RestorePending or
                        DadAutoPartyParticipantStage.Restored,
                ExecutionOperationKind.Cancel => slot.Stage is not DadAutoPartyParticipantStage.Restored and
                    not DadAutoPartyParticipantStage.Revoked and not DadAutoPartyParticipantStage.Expired and
                    not DadAutoPartyParticipantStage.Failed,
                _ => false,
            };
            if (!allowed)
            {
                safeCode = "dad-remote-operation-order-invalid";
                return false;
            }
            EndpointExecutionModuleReference? moduleReference = null;
            if (kind is ExecutionOperationKind.Queue or ExecutionOperationKind.Settle)
            {
                if (!moduleIndex.HasValue || moduleIndex.Value < 0 ||
                    moduleIndex.Value >= proposal.ExecutionPlan.Modules.Length)
                {
                    safeCode = "dad-remote-operation-module-reference-required";
                    return false;
                }
                var module = proposal.ExecutionPlan.Modules[moduleIndex.Value];
                moduleReference = new EndpointExecutionModuleReference(module.ModuleIndex, module.ModuleId);
                if (moduleIndex.Value != slot.NextModuleIndex ||
                    kind == ExecutionOperationKind.Settle &&
                    !SameModuleReference(slot.ActiveModuleReference, moduleReference))
                {
                    safeCode = "dad-remote-operation-module-order-invalid";
                    return false;
                }
            }
            else if (moduleIndex.HasValue)
            {
                safeCode = "dad-remote-operation-module-reference-forbidden";
                return false;
            }

            if (IsCompletedOperation(slot, kind, moduleReference))
            {
                safeCode = "dad-remote-operation-already-complete";
                return true;
            }

            safeCode = string.Empty;
            var frozenPartyInviteTargets = (partyInviteTargets ?? [])
                .Select(static target => target?.Clone())
                .ToList();
            if (frozenPartyInviteTargets.Any(static target => target == null) ||
                (kind switch
                {
                    ExecutionOperationKind.Form =>
                        !ValidateFormLocators(proposal, slot, inviter, frozenPartyInviteTargets!, out safeCode),
                    ExecutionOperationKind.Restore when inviter != null || frozenPartyInviteTargets.Count > 0 =>
                        !ValidateRestoreLocators(proposal, slot, inviter, frozenPartyInviteTargets!, out safeCode),
                    ExecutionOperationKind.Restore => false,
                    _ => inviter != null || frozenPartyInviteTargets.Count > 0,
                }))
            {
                if (string.IsNullOrWhiteSpace(safeCode))
                    safeCode = "dad-remote-operation-locator-unexpected";
                return false;
            }
            if (operations.Values.Any(operation => operation.ProposalId == proposalId &&
                    string.Equals(operation.SlotId, slotId, StringComparison.OrdinalIgnoreCase) &&
                    operation.Kind == kind && !operation.Completed))
            {
                safeCode = "dad-remote-operation-pending";
                return true;
            }
            if (kind == ExecutionOperationKind.Form &&
                (slot.InviteTarget == null || slot.InviteTargetExpiresAt <= now))
            {
                safeCode = "dad-remote-invite-target-pending";
                return false;
            }

            var operationId = Guid.NewGuid();
            var restartsExecutionPlan = kind == ExecutionOperationKind.Form &&
                                        slot.Stage == DadAutoPartyParticipantStage.Settled;
            var command = new DadAutoPartyParticipantCommand(
                operationId,
                DadAutoPartyParticipantCommandKind.Execution,
                proposalId,
                proposal.RunId,
                slot.Slot.SlotId,
                slot.Slot.OwnerId,
                slot.Slot.IslandId,
                slot.Slot.OpaqueCharacterId,
                slot.Slot.RequiredJobId!.Value,
                proposal.ActivityId,
                kind,
                slot.StateGeneration,
                now,
                now + OperationLifetime,
                inviter?.Clone(),
                frozenPartyInviteTargets!,
                ExecutionModuleReference: moduleReference,
                FormationOnly: proposal.FormationOnly);
            if (!Enqueue(command))
            {
                slot.Stage = DadAutoPartyParticipantStage.Failed;
                slot.ObservedAt = now;
                slot.SafeCode = "dad-remote-command-capacity-exhausted";
                safeCode = slot.SafeCode;
                return false;
            }
            operations[operationId] = new PendingOperation(
                operationId,
                proposalId,
                slotId,
                kind,
                moduleReference,
                now,
                now + OperationLifetime);
            if (restartsExecutionPlan)
            {
                slot.NextModuleIndex = 0;
                slot.ActiveModuleReference = null;
            }
            slot.Stage = PendingStage(kind);
            slot.ObservedAt = now;
            slot.SafeCode = "dad-remote-operation-pending";
            safeCode = "dad-remote-operation-enqueued";
            return true;
        }
    }

    public bool ObserveOperationReceipt(
        ExecutionOperationReceipt receipt,
        DateTimeOffset now,
        out string safeCode)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        lock (gate)
        {
            Sweep(now);
            if (!TryAcceptHeader(receipt.Header, receipt.ProposalId, now, out safeCode) ||
                !operations.TryGetValue(receipt.OperationId, out var operation) ||
                operation.ProposalId != receipt.ProposalId || operation.Kind != receipt.Kind ||
                !SameModuleReference(operation.ModuleReference, receipt.ModuleReference) ||
                !TryGetSlot(receipt.ProposalId, operation.SlotId, out var proposal, out var slot) ||
                !string.Equals(receipt.OwnerId.Value, slot.Slot.OwnerId, StringComparison.Ordinal) ||
                !string.Equals(receipt.Header.SenderIslandId.Value, slot.Slot.IslandId, StringComparison.Ordinal))
            {
                safeCode = "dad-remote-operation-receipt-mismatch";
                return false;
            }
            var requiresPartyProof = receipt.Kind == ExecutionOperationKind.Form &&
                                     receipt.Outcome == ExecutionOutcome.Completed;
            var expectedPartyContentIds = proposal.Slots.Values
                .Select(static candidate => candidate.InviteTarget?.ContentId ?? 0)
                .ToHashSet();
            if (requiresPartyProof
                    ? receipt.ObservedPartyContentIds.IsDefault ||
                      receipt.ObservedPartyContentIds.Length is < 1 or > 8 ||
                      receipt.ObservedPartyContentIds.Any(static contentId => contentId == 0) ||
                      receipt.ObservedPartyContentIds.Distinct().Count() != receipt.ObservedPartyContentIds.Length ||
                      expectedPartyContentIds.Count != proposal.Slots.Count ||
                      expectedPartyContentIds.Contains(0) ||
                      !expectedPartyContentIds.SetEquals(receipt.ObservedPartyContentIds)
                    : !receipt.ObservedPartyContentIds.IsDefaultOrEmpty)
            {
                safeCode = "dad-remote-operation-party-proof-invalid";
                return false;
            }
            if (operation.Completed)
            {
                if (receipt.Outcome == ExecutionOutcome.Denied ||
                    receipt.ObservedStateGeneration < slot.StateGeneration)
                {
                    CommitReplay(receipt.Header);
                    safeCode = "dad-remote-operation-denied-after-dispatch";
                    return false;
                }

                slot.StateGeneration = receipt.ObservedStateGeneration;
                if (requiresPartyProof)
                    slot.ObservedPartyContentIds = receipt.ObservedPartyContentIds;
                slot.ObservedAt = now;
                CommitReplay(receipt.Header);
                safeCode = receipt.SafeCode;
                return true;
            }
            if (receipt.Outcome == ExecutionOutcome.Denied ||
                receipt.ObservedStateGeneration < slot.StateGeneration)
            {
                slot.Stage = DadAutoPartyParticipantStage.Failed;
                slot.SafeCode = "dad-remote-operation-denied";
                slot.ObservedAt = now;
                operation.Completed = true;
                CommitReplay(receipt.Header);
                safeCode = slot.SafeCode;
                return false;
            }
            slot.StateGeneration = receipt.ObservedStateGeneration;
            if (requiresPartyProof)
                slot.ObservedPartyContentIds = receipt.ObservedPartyContentIds;
            slot.SafeCode = receipt.SafeCode;
            slot.ObservedAt = now;
            if (receipt.Outcome == ExecutionOutcome.Accepted)
            {
                CommitReplay(receipt.Header);
                safeCode = "dad-remote-operation-dispatch-accepted";
                return true;
            }

            operation.Completed = true;
            if (requiresPartyProof && slot.Slot.IsInviter)
            {
                foreach (var candidate in proposal.Slots.Values.Where(static candidate => !candidate.IsTerminal))
                {
                    candidate.Stage = DadAutoPartyParticipantStage.Formed;
                    candidate.ObservedPartyContentIds = receipt.ObservedPartyContentIds;
                    candidate.ObservedAt = now;
                    candidate.SafeCode = "dad-remote-slot1-party-proof-accepted";
                }
                foreach (var pending in operations.Values.Where(candidate =>
                             candidate.ProposalId == operation.ProposalId &&
                             candidate.Kind == ExecutionOperationKind.Form))
                    pending.Completed = true;
            }
            var operationSetComplete = operations.Values
                .Where(candidate => candidate.ProposalId == operation.ProposalId &&
                                    string.Equals(candidate.SlotId, operation.SlotId, StringComparison.OrdinalIgnoreCase) &&
                                    candidate.Kind == operation.Kind)
                .All(static candidate => candidate.Completed);
            if (operationSetComplete &&
                !(slot.Stage == DadAutoPartyParticipantStage.Restored &&
                  receipt.Kind == ExecutionOperationKind.Cancel))
            {
                if (receipt.Kind == ExecutionOperationKind.Queue)
                {
                    slot.ActiveModuleReference = operation.ModuleReference;
                    slot.Stage = DadAutoPartyParticipantStage.Queued;
                }
                else if (receipt.Kind == ExecutionOperationKind.Settle)
                {
                    slot.ActiveModuleReference = null;
                    slot.NextModuleIndex++;
                    slot.Stage = slot.NextModuleIndex < proposals[receipt.ProposalId].ExecutionPlan.Modules.Length
                        ? DadAutoPartyParticipantStage.Formed
                        : DadAutoPartyParticipantStage.Settled;
                }
                else
                {
                    slot.Stage = CompletedStage(receipt.Kind);
                }
            }
            CommitReplay(receipt.Header);
            safeCode = "dad-remote-operation-receipt-accepted";
            return true;
        }
    }

    public DadAutoPartyParticipantCommandBatch LeasePendingCommands(
        int maximumCommands,
        TimeSpan dispatchLeaseDuration,
        DateTimeOffset now)
    {
        if (maximumCommands is < 1 or > MaximumPendingCommands)
            throw new ArgumentOutOfRangeException(nameof(maximumCommands));
        if (dispatchLeaseDuration < TimeSpan.FromSeconds(1) || dispatchLeaseDuration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(dispatchLeaseDuration));

        lock (gate)
        {
            Sweep(now);
            StageDueProposalRenewals(now);
            ReleaseExpiredCommandLeases(now);
            var dispatchLeaseId = Guid.NewGuid();
            var leaseExpiresAt = now + dispatchLeaseDuration;
            var commands = new List<DadAutoPartyParticipantCommand>(Math.Min(maximumCommands, pendingCommands.Count));
            for (var node = pendingCommandOrder.First;
                 node != null && commands.Count < maximumCommands;
                 node = node.Next)
            {
                if (!pendingCommands.TryGetValue(node.Value, out var pending) || pending.DispatchLeaseId != Guid.Empty)
                    continue;
                pending.DispatchLeaseId = dispatchLeaseId;
                pending.DispatchLeaseExpiresAt = leaseExpiresAt;
                commands.Add(CloneCommand(pending.Command));
            }

            return commands.Count == 0
                ? DadAutoPartyParticipantCommandBatch.Empty
                : new DadAutoPartyParticipantCommandBatch(dispatchLeaseId, leaseExpiresAt, commands);
        }
    }

    public int AcknowledgePendingCommands(
        Guid dispatchLeaseId,
        IReadOnlyCollection<Guid> commandIds,
        DateTimeOffset now)
    {
        if (dispatchLeaseId == Guid.Empty || commandIds == null || commandIds.Count == 0)
            return 0;
        lock (gate)
        {
            Sweep(now);
            var acknowledged = 0;
            foreach (var commandId in commandIds.Distinct())
            {
                if (!pendingCommands.TryGetValue(commandId, out var pending) ||
                    pending.DispatchLeaseId != dispatchLeaseId || pending.DispatchLeaseExpiresAt <= now)
                    continue;
                var command = pending.Command;
                ApplySuccessfulDispatch(command, now);
                RemoveCommand(commandId);
                if (command.CommandKind == DadAutoPartyParticipantCommandKind.ProposalRenewal &&
                    proposals.TryGetValue(command.ProposalId, out var proposal))
                {
                    if (command.PreviousProposalExpiresAt is { } previousExpiresAt &&
                        proposal.ExpiresAt == previousExpiresAt &&
                        proposal.RenewalGeneration + 1 == command.RenewalGeneration &&
                        proposal.PendingRenewalExpiresAt == command.ExpiresAt &&
                        proposal.PendingRenewalGeneration == command.RenewalGeneration)
                    {
                        proposal.ExpiresAt = command.ExpiresAt;
                        proposal.RenewalGeneration = command.RenewalGeneration;
                    }
                    if (!pendingCommands.Values.Any(pendingRenewal =>
                            pendingRenewal.Command.CommandKind == DadAutoPartyParticipantCommandKind.ProposalRenewal &&
                            pendingRenewal.Command.ProposalId == command.ProposalId &&
                            pendingRenewal.Command.RenewalGeneration == command.RenewalGeneration))
                    {
                        proposal.PendingRenewalExpiresAt = default;
                        proposal.PendingRenewalGeneration = 0;
                    }
                }
                acknowledged++;
            }
            return acknowledged;
        }
    }

    public int ReleasePendingCommands(Guid dispatchLeaseId, DateTimeOffset now)
    {
        if (dispatchLeaseId == Guid.Empty)
            return 0;
        lock (gate)
        {
            Sweep(now);
            var released = 0;
            foreach (var pending in pendingCommands.Values.Where(item => item.DispatchLeaseId == dispatchLeaseId))
            {
                pending.DispatchLeaseId = Guid.Empty;
                pending.DispatchLeaseExpiresAt = default;
                released++;
            }
            return released;
        }
    }

    internal int PendingCommandCount
    {
        get
        {
            lock (gate)
                return pendingCommands.Count;
        }
    }

    public void DeauthenticateIsland(
        string islandId,
        long revocationGeneration,
        string safeReason,
        DateTimeOffset now,
        bool sendIdentityRevocation = true)
    {
        var normalizedIsland = DadAutoPartyConfiguration.NormalizeIdentifier(islandId);
        if (string.IsNullOrWhiteSpace(normalizedIsland) || revocationGeneration < 1)
            return;
        lock (gate)
        {
            if (revokedIslands.TryGetValue(normalizedIsland, out var current) && current >= revocationGeneration)
                return;
            RemoveSupersededCommandsForIsland(normalizedIsland);
            var reason = DadAutoPartyConfiguration.NormalizeSafeCode(safeReason) is { Length: > 0 } code
                ? code
                : "dad-remote-route-revoked";
            foreach (var proposal in proposals.Values)
            {
                foreach (var slot in proposal.Slots.Values.Where(slot =>
                             string.Equals(slot.Slot.IslandId, normalizedIsland, StringComparison.Ordinal) &&
                             !slot.IsTerminal))
                {
                    StageLifecycleOperation(proposal, slot, ExecutionOperationKind.Cancel, now, reason);
                    StageLifecycleOperation(proposal, slot, ExecutionOperationKind.Restore, now, reason);
                }
            }
            var revocationStaged = !sendIdentityRevocation || Enqueue(new DadAutoPartyParticipantCommand(
                Guid.NewGuid(),
                DadAutoPartyParticipantCommandKind.Revocation,
                Guid.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                normalizedIsland,
                string.Empty,
                1,
                "dad-revocation",
                null,
                Math.Max(1, revocationGeneration),
                now,
                now + OperationLifetime,
                RevocationGeneration: Math.Max(1, revocationGeneration),
                SafeCode: reason));
            revokedIslands[normalizedIsland] = revocationGeneration;
            foreach (var proposal in proposals.Values)
            {
                foreach (var slot in proposal.Slots.Values.Where(slot =>
                             string.Equals(slot.Slot.IslandId, normalizedIsland, StringComparison.Ordinal)))
                {
                    slot.Stage = DadAutoPartyParticipantStage.Revoked;
                    slot.SafeCode = revocationStaged ? reason : "dad-remote-revocation-capacity-exhausted";
                    slot.ObservedAt = now;
                    slot.InviteTarget = null;
                    slot.InviteTargetExpiresAt = default;
                }
            }
        }
    }

    public void ReactivateIsland(string islandId)
    {
        var normalizedIsland = DadAutoPartyConfiguration.NormalizeIdentifier(islandId);
        if (string.IsNullOrWhiteSpace(normalizedIsland))
            return;
        lock (gate)
            revokedIslands.Remove(normalizedIsland);
    }

    public void StopAll(string safeReason, DateTimeOffset now)
    {
        lock (gate)
        {
            var reason = DadAutoPartyConfiguration.NormalizeSafeCode(safeReason) is { Length: > 0 } code
                ? code
                : "dad-remote-session-stopped";
            RemoveSupersededCommandsForStop();
            foreach (var proposal in proposals.Values)
            {
                foreach (var slot in proposal.Slots.Values.Where(static slot => !slot.IsTerminal))
                {
                    var cancelStaged = StageLifecycleOperation(
                        proposal,
                        slot,
                        ExecutionOperationKind.Cancel,
                        now,
                        reason);
                    var restoreStaged = StageLifecycleOperation(
                        proposal,
                        slot,
                        ExecutionOperationKind.Restore,
                        now,
                        reason);
                    if (!cancelStaged || !restoreStaged)
                    {
                        slot.Stage = DadAutoPartyParticipantStage.Failed;
                        slot.SafeCode = "dad-remote-stop-capacity-exhausted";
                    }
                    slot.ObservedAt = now;
                    slot.InviteTarget = null;
                    slot.InviteTargetExpiresAt = default;
                }
            }
        }
    }

    public void CompleteProposal(Guid proposalId, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!proposals.TryGetValue(proposalId, out var proposal))
                return;
            RemoveCommandsForProposal(proposalId);
            foreach (var slot in proposal.Slots.Values)
            {
                if (!slot.IsTerminal)
                {
                    slot.Stage = DadAutoPartyParticipantStage.Restored;
                    slot.SafeCode = "dad-remote-session-complete";
                    slot.ObservedAt = now;
                }
                slot.InviteTarget = null;
                slot.InviteTargetExpiresAt = default;
            }
            foreach (var operationId in operations
                         .Where(pair => pair.Value.ProposalId == proposalId)
                         .Select(static pair => pair.Key)
                         .ToList())
                operations.Remove(operationId);
            TrimSessions();
        }
    }

    private bool TryAcceptHeader(
        ContractHeader header,
        Guid proposalId,
        DateTimeOffset now,
        out string safeCode)
    {
        try
        {
            ProtocolValidator.ValidateHeader(header);
        }
        catch (Exception exception) when (exception is ProtocolException or ArgumentException)
        {
            safeCode = "dad-remote-contract-header-invalid";
            return false;
        }
        if (!proposals.TryGetValue(proposalId, out var proposal) || proposal.ExpiresAt <= now ||
            header.IssuedAt > now + TimeSpan.FromMinutes(2) || header.ExpiresAt <= now ||
            configuration == null ||
            !string.Equals(header.RecipientIslandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal) ||
            IsIslandRevoked(header.SenderIslandId.Value) || replayedMessages.ContainsKey(header.MessageId))
        {
            safeCode = replayedMessages.ContainsKey(header.MessageId)
                ? "dad-remote-contract-replay"
                : "dad-remote-contract-route-invalid";
            return false;
        }
        safeCode = "dad-remote-contract-fresh";
        return true;
    }

    private bool TryFindSlot(
        Guid proposalId,
        string islandId,
        string ownerId,
        string characterId,
        out SlotRuntime slot)
    {
        slot = null!;
        if (!proposals.TryGetValue(proposalId, out var proposal))
            return false;
        var matches = proposal.Slots.Values.Where(candidate =>
                string.Equals(candidate.Slot.IslandId, islandId, StringComparison.Ordinal) &&
                string.Equals(candidate.Slot.OwnerId, ownerId, StringComparison.Ordinal) &&
                string.Equals(candidate.Slot.OpaqueCharacterId, characterId, StringComparison.Ordinal))
            .ToList();
        if (matches.Count != 1)
            return false;
        slot = matches[0];
        return true;
    }

    private List<SlotRuntime> FindOwnerSlots(Guid proposalId, string islandId, string ownerId)
        => proposals.TryGetValue(proposalId, out var proposal)
            ? proposal.Slots.Values.Where(slot =>
                    string.Equals(slot.Slot.IslandId, islandId, StringComparison.Ordinal) &&
                    string.Equals(slot.Slot.OwnerId, ownerId, StringComparison.Ordinal))
                .ToList()
            : [];

    private bool TryGetSlot(
        Guid proposalId,
        string slotId,
        out ProposalRuntime proposal,
        out SlotRuntime slot)
    {
        slot = null!;
        if (!proposals.TryGetValue(proposalId, out proposal!) ||
            !proposal.Slots.TryGetValue(slotId, out slot!))
            return false;
        return true;
    }

    private void CommitReplay(ContractHeader header)
    {
        while (replayedMessages.Count >= MaximumReplayEntries)
            replayedMessages.Remove(replayedMessages.MinBy(static pair => pair.Value).Key);
        replayedMessages[header.MessageId] = header.ExpiresAt;
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var commandId in pendingCommands
                     .Where(pair => pair.Value.Command.ExpiresAt <= now)
                     .Select(static pair => pair.Key)
                     .ToList())
            RemoveCommand(commandId);
        foreach (var messageId in replayedMessages
                     .Where(pair => pair.Value <= now)
                     .Select(static pair => pair.Key)
                     .ToList())
            replayedMessages.Remove(messageId);
        foreach (var operationId in operations
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(static pair => pair.Key)
                     .ToList())
        {
            if (operations.TryGetValue(operationId, out var operation) &&
                TryGetSlot(operation.ProposalId, operation.SlotId, out _, out var slot) &&
                !operation.Completed)
            {
                slot.Stage = DadAutoPartyParticipantStage.Expired;
                slot.SafeCode = "dad-remote-operation-expired";
                slot.ObservedAt = now;
            }
            operations.Remove(operationId);
        }
        foreach (var proposal in proposals.Values)
        {
            var pendingRenewal = pendingCommands.Values.Any(pending =>
                pending.Command.CommandKind == DadAutoPartyParticipantCommandKind.ProposalRenewal &&
                pending.Command.ProposalId == proposal.ProposalId &&
                pending.Command.RenewalGeneration == proposal.PendingRenewalGeneration);
            if (!pendingRenewal)
            {
                proposal.PendingRenewalExpiresAt = default;
                proposal.PendingRenewalGeneration = 0;
            }
            foreach (var slot in proposal.Slots.Values)
            {
                if (slot.InviteTargetExpiresAt <= now)
                {
                    slot.InviteTarget = null;
                    slot.InviteTargetExpiresAt = default;
                }
                if (!slot.IsTerminal &&
                    ((proposal.ExpiresAt <= now &&
                      (!pendingRenewal || proposal.PendingRenewalExpiresAt <= now)) ||
                     slot.LeaseExpiresAt is { } lease && lease <= now))
                {
                    slot.Stage = DadAutoPartyParticipantStage.Expired;
                    slot.SafeCode = "dad-remote-session-expired";
                    slot.ObservedAt = now;
                }
            }
        }
    }

    private bool Enqueue(DadAutoPartyParticipantCommand command)
    {
        if (pendingCommands.Count >= MaximumPendingCommands || pendingCommands.ContainsKey(command.CommandId))
            return false;
        pendingCommands.Add(command.CommandId, new PendingCommand(command));
        pendingCommandOrder.AddLast(command.CommandId);
        return true;
    }

    private void ApplySuccessfulDispatch(DadAutoPartyParticipantCommand command, DateTimeOffset now)
    {
        if (!proposals.TryGetValue(command.ProposalId, out var proposal))
            return;

        if (command.CommandKind == DadAutoPartyParticipantCommandKind.Proposal)
        {
            foreach (var slot in proposal.Slots.Values.Where(slot =>
                         string.Equals(slot.Slot.IslandId, command.IslandId, StringComparison.Ordinal) &&
                         !slot.IsTerminal))
            {
                slot.ObservedAt = now;
                slot.SafeCode = "dad-remote-command-route-active";
            }
            return;
        }

        if (command.CommandKind == DadAutoPartyParticipantCommandKind.IntegrationProfile)
        {
            if (TryGetSlot(command.ProposalId, command.SlotId, out _, out var profiled) && !profiled.IsTerminal)
            {
                profiled.ObservedAt = now;
                profiled.SafeCode = "dad-remote-integration-profile-dispatched";
            }
            return;
        }

        if (command.CommandKind != DadAutoPartyParticipantCommandKind.Execution ||
            command.OperationKind is not { } kind ||
            !operations.TryGetValue(command.CommandId, out var operation) || operation.Completed ||
            !TryGetSlot(command.ProposalId, command.SlotId, out _, out var runtime))
            return;

        operation.Completed = true;
        if (kind == ExecutionOperationKind.Queue)
        {
            runtime.ActiveModuleReference = operation.ModuleReference;
            runtime.Stage = DadAutoPartyParticipantStage.Queued;
        }
        else if (kind == ExecutionOperationKind.Settle)
        {
            runtime.ActiveModuleReference = null;
            runtime.NextModuleIndex++;
            runtime.Stage = runtime.NextModuleIndex < proposal.ExecutionPlan.Modules.Length
                ? DadAutoPartyParticipantStage.Formed
                : DadAutoPartyParticipantStage.Settled;
        }
        else if (!(runtime.Stage == DadAutoPartyParticipantStage.Restored &&
                   kind == ExecutionOperationKind.Cancel))
        {
            runtime.Stage = CompletedStage(kind);
        }
        runtime.ObservedAt = now;
        runtime.SafeCode = $"dad-remote-{kind.ToString().ToLowerInvariant()}-dispatched";
    }

    private void StageDueProposalRenewals(DateTimeOffset now)
    {
        foreach (var proposal in proposals.Values
                     .Where(proposal => proposal.ExpiresAt > now &&
                                        proposal.ExpiresAt - now <= TimeSpan.FromMinutes(5)))
        {
            if (pendingCommands.Values.Any(pending =>
                    pending.Command.CommandKind == DadAutoPartyParticipantCommandKind.ProposalRenewal &&
                    pending.Command.ProposalId == proposal.ProposalId))
                continue;

            var groups = proposal.Slots.Values
                .GroupBy(static slot => slot.Slot.IslandId, StringComparer.Ordinal)
                .ToArray();
            if (groups.Length == 0 || pendingCommands.Count + groups.Length > MaximumPendingCommands)
                continue;

            var previousExpiresAt = proposal.ExpiresAt;
            var nextExpiresAt = previousExpiresAt + ProposalLifetime;
            var renewalGeneration = proposal.RenewalGeneration + 1;
            var commands = groups.Select(group =>
            {
                var first = group
                    .OrderBy(static slot => DadPlannerSlotRules.GetSlotSortKey(slot.Slot.SlotId))
                    .First()
                    .Slot;
                return new DadAutoPartyParticipantCommand(
                    Guid.NewGuid(),
                    DadAutoPartyParticipantCommandKind.ProposalRenewal,
                    proposal.ProposalId,
                    proposal.RunId,
                    first.SlotId,
                    first.OwnerId,
                    first.IslandId,
                    first.OpaqueCharacterId,
                    first.RequiredJobId!.Value,
                    proposal.ActivityId,
                    null,
                    1,
                    now,
                    nextExpiresAt,
                    RenewalGeneration: renewalGeneration,
                    PreviousProposalExpiresAt: previousExpiresAt,
                    FormationOnly: proposal.FormationOnly);
            }).ToArray();
            if (commands.Any(command => !Enqueue(command)))
            {
                foreach (var command in commands)
                    RemoveCommand(command.CommandId);
                continue;
            }

            proposal.PendingRenewalExpiresAt = nextExpiresAt;
            proposal.PendingRenewalGeneration = renewalGeneration;
        }
    }

    private bool StageLifecycleOperation(
        ProposalRuntime proposal,
        SlotRuntime slot,
        ExecutionOperationKind kind,
        DateTimeOffset now,
        string safeReason)
    {
        if (operations.Values.Any(operation => operation.ProposalId == proposal.ProposalId &&
                string.Equals(operation.SlotId, slot.Slot.SlotId, StringComparison.OrdinalIgnoreCase) &&
                operation.Kind == kind && !operation.Completed))
            return true;

        var operationId = Guid.NewGuid();
        var expiresAt = now + OperationLifetime;
        if (!Enqueue(new DadAutoPartyParticipantCommand(
                operationId,
                DadAutoPartyParticipantCommandKind.Execution,
                proposal.ProposalId,
                proposal.RunId,
                slot.Slot.SlotId,
                slot.Slot.OwnerId,
                slot.Slot.IslandId,
                slot.Slot.OpaqueCharacterId,
                slot.Slot.RequiredJobId!.Value,
                proposal.ActivityId,
                kind,
                slot.StateGeneration,
                now,
                expiresAt,
                SafeCode: safeReason,
                FormationOnly: proposal.FormationOnly)))
            return false;

        operations[operationId] = new PendingOperation(
            operationId,
            proposal.ProposalId,
            slot.Slot.SlotId,
            kind,
            null,
            now,
            expiresAt);
        slot.Stage = PendingStage(kind);
        slot.SafeCode = safeReason;
        slot.ObservedAt = now;
        return true;
    }

    private void RemoveSupersededCommandsForIsland(string islandId)
        => RemoveSupersededCommands(command =>
            string.Equals(command.IslandId, islandId, StringComparison.Ordinal));

    private void RemoveSupersededCommandsForStop()
        => RemoveSupersededCommands(static _ => true);

    private void RemoveSupersededCommands(Func<DadAutoPartyParticipantCommand, bool> inScope)
    {
        foreach (var commandId in pendingCommands
                     .Where(pair => pair.Value.DispatchLeaseId == Guid.Empty &&
                                    inScope(pair.Value.Command) &&
                                    pair.Value.Command.CommandKind != DadAutoPartyParticipantCommandKind.Revocation &&
                                    pair.Value.Command.OperationKind is not ExecutionOperationKind.Cancel and
                                        not ExecutionOperationKind.Restore)
                     .Select(static pair => pair.Key)
                     .ToList())
            RemoveCommand(commandId);
    }

    private void RemoveCommandsForProposal(Guid proposalId)
    {
        foreach (var commandId in pendingCommands
                     .Where(pair => pair.Value.Command.ProposalId == proposalId)
                     .Select(static pair => pair.Key)
                     .ToList())
            RemoveCommand(commandId);
    }

    private void RemoveCommand(Guid commandId)
    {
        if (!pendingCommands.Remove(commandId))
            return;
        pendingCommandOrder.Remove(commandId);
    }

    private void ReleaseExpiredCommandLeases(DateTimeOffset now)
    {
        foreach (var pending in pendingCommands.Values.Where(item =>
                     item.DispatchLeaseId != Guid.Empty && item.DispatchLeaseExpiresAt <= now))
        {
            pending.DispatchLeaseId = Guid.Empty;
            pending.DispatchLeaseExpiresAt = default;
        }
    }

    private bool IsIslandRevoked(string islandId)
        => revokedIslands.ContainsKey(islandId);

    private bool HasActiveCommandBinding(
        ProposalRuntime proposal,
        SlotRuntime runtime,
        DateTimeOffset now)
    {
        var slot = runtime.Slot;
        if (proposal.ExpiresAt <= now || runtime.Stage >= DadAutoPartyParticipantStage.Revoked ||
            configuration is not { IsRegistrationActive: true } || IsIslandRevoked(slot.IslandId) ||
            configuration.Deauthentications.Any(item =>
                string.Equals(item.PeerIslandId, slot.IslandId, StringComparison.Ordinal)))
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(directoryAuthorityGate?.Invoke([slot], now)))
            return false;

        var matches = currentRemoteBindingsProvider()
            .Where(binding => binding.IsValid &&
                string.Equals(binding.OwnerId, slot.OwnerId, StringComparison.Ordinal) &&
                string.Equals(binding.IslandId, slot.IslandId, StringComparison.Ordinal) &&
                string.Equals(binding.OpaqueCharacterId, slot.OpaqueCharacterId, StringComparison.Ordinal) &&
                string.Equals(binding.RequestedJobId, slot.RequiredJobId?.ToString(), StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length == 1 && matches[0].OwnsQueueAuthority == slot.IsLeader;
    }

    private void TrimSessions()
    {
        while (proposals.Count > MaximumRetainedSessions)
        {
            var oldest = proposals.Values
                .Where(static proposal => proposal.Slots.Values.All(static slot => slot.IsTerminal))
                .MinBy(static proposal => proposal.CreatedAt);
            if (oldest == null)
                break;
            proposals.Remove(oldest.ProposalId);
        }
    }

    private static DadAutoPartyParticipantSnapshot ToSnapshot(
        Guid proposalId,
        ProposalRuntime proposal,
        SlotRuntime slot)
        => new(
            proposalId,
            proposal.RunId,
            slot.Slot.SlotId,
            slot.Slot.OwnerId,
            slot.Slot.IslandId,
            slot.Slot.OpaqueCharacterId,
            slot.Slot.RequiredJobId!.Value,
            slot.Stage,
            slot.StateGeneration,
            slot.LeaseExpiresAt,
            proposal.ExpiresAt,
            slot.ObservedAt,
            slot.SafeCode,
            slot.ObservedPartyContentIds,
            slot.ActiveModuleReference == null
                ? null
                : new EndpointExecutionModuleReference(
                    slot.ActiveModuleReference.ModuleIndex,
                    slot.ActiveModuleReference.ModuleId),
            slot.NextModuleIndex);

    private static DadParticipantSnapshot BuildParticipant(
        Guid proposalId,
        DadFrozenRunSlot slot,
        DadAutoPartyParticipantSnapshot? snapshot,
        bool routeActive,
        string status)
    {
        return new DadParticipantSnapshot
        {
            WorkerSessionId = RuntimeWorkerId(proposalId, slot.SlotId),
            RegisteredIslandId = slot.IslandId,
            RunId = snapshot?.RunId ?? string.Empty,
            State = routeActive ? DadParticipantState.Discovered : DadParticipantState.Stale,
            ClaimState = DadClaimState.None,
            LeaseState = DadParticipantLeaseState.None,
            IsAvailable = false,
            IsEligibleForRun = false,
            PostArReady = false,
            WorldReadyStable = false,
            Dependencies = DadDependencySnapshot.CreateChecking(
                summary: "Registered-island dependency readiness is not requested."),
            LastHeartbeatUtc = default,
            ActiveCharacterKey = new DadCharacterKey($"remote-{slot.SlotId}"),
            AvailableCharacterKeys = [new DadCharacterKey($"remote-{slot.SlotId}")],
            Character = new DadAcquiredCharacter
            {
                CharacterKey = $"remote-{slot.SlotId}",
                Source = DadCharacterSource.PeerRuntime,
                Freshness = DadSnapshotFreshness.Unknown,
                LastSeenUtc = null,
                CurrentJobId = null,
                Readiness = DadReadinessState.Deferred,
                SnapshotQuality = "AutoParty authenticated command identity",
            },
            AssignedSlotId = slot.SlotId,
            DesiredCharacterKey = $"remote-{slot.SlotId}",
            LeaseIssuedUtc = null,
            LeaseRenewedUtc = null,
            LeaseExpiresUtc = null,
            StatusText = status,
        };
    }

    private static DadWorkerSessionId RuntimeWorkerId(Guid proposalId, string slotId)
        => new($"autoparty-{proposalId:N}-{slotId.ToLowerInvariant()}");

    private static DadAutoPartyParticipantStage PendingStage(ExecutionOperationKind kind)
        => kind switch
        {
            ExecutionOperationKind.Form => DadAutoPartyParticipantStage.FormPending,
            ExecutionOperationKind.Queue => DadAutoPartyParticipantStage.QueuePending,
            ExecutionOperationKind.Settle => DadAutoPartyParticipantStage.SettlementPending,
            ExecutionOperationKind.Restore => DadAutoPartyParticipantStage.RestorePending,
            ExecutionOperationKind.Cancel => DadAutoPartyParticipantStage.CancelPending,
            _ => DadAutoPartyParticipantStage.Failed,
        };

    private static DadAutoPartyParticipantStage CompletedStage(ExecutionOperationKind kind)
        => kind switch
        {
            ExecutionOperationKind.Form => DadAutoPartyParticipantStage.Formed,
            ExecutionOperationKind.Queue => DadAutoPartyParticipantStage.Queued,
            ExecutionOperationKind.Settle => DadAutoPartyParticipantStage.Settled,
            ExecutionOperationKind.Restore => DadAutoPartyParticipantStage.Restored,
            ExecutionOperationKind.Cancel => DadAutoPartyParticipantStage.Cancelled,
            _ => DadAutoPartyParticipantStage.Failed,
        };

    private static string BuildActivityId(DadRunSlotManifest manifest, bool formationOnly)
    {
        if (formationOnly)
            return DadAutoPartyFreeformRules.FormationActivityId;
        var module = manifest.Modules.FirstOrDefault();
        return module == null ? "dad-formation" : BuildActivityId(module);
    }

    private static string BuildActivityId(DadFrozenModulePayload module)
    {
        if (module.ModuleId == DadModuleId.None)
            return "dad-formation";
        var identity = module.ContentFinderConditionId != 0
            ? module.ContentFinderConditionId
            : module.RouletteId != 0
                ? module.RouletteId
                : (uint)Math.Max(1, module.ExpectedPartySize);
        return $"dad-{module.ModuleId.ToString().ToLowerInvariant()}-{identity}";
    }

    private bool TryBuildExecutionPlan(
        DadRunPlan plan,
        DadRunSlotManifest manifest,
        string activityId,
        bool useFrenRider,
        out IReadOnlyList<DadAutoPartyParticipantRequest> participantRequests,
        out EndpointExecutionPlan executionPlan,
        out string blocker)
    {
        participantRequests = [];
        executionPlan = null!;
        blocker = string.Empty;

        if (configuration == null ||
            string.IsNullOrWhiteSpace(configuration.RegisteredOwnerId) ||
            string.IsNullOrWhiteSpace(configuration.RegisteredIslandId))
        {
            return Fail("AutoParty execution projection is missing the local registered route.", out blocker);
        }
        if (plan.Request == null || plan.Orchestration == null || plan.Modules == null)
            return Fail("AutoParty execution projection is missing its typed runtime plan.", out blocker);

        var slots = manifest.Slots
            .OrderBy(static slot => DadPlannerSlotRules.GetSlotSortKey(slot.SlotId))
            .ToList();
        if (slots.Count is < 1 or > 8 ||
            slots.Count != manifest.ExpectedPartySize ||
            slots.Count != plan.RequiredParticipantCount ||
            slots.Count(static slot => slot.IsLeader) != 1 ||
            slots[0].IsLeader == false ||
            (slots.Count > 1 && (slots.Count(static slot => slot.IsInviter) != 1 || !slots[0].IsInviter)))
        {
            return Fail("AutoParty execution projection requires one complete frozen roster with Slot1 authority.", out blocker);
        }

        var localCrew = currentLocalCrewProvider()
            .Where(static candidate => candidate != null)
            .ToList();
        var requests = new List<DadAutoPartyParticipantRequest>(slots.Count);
        var projected = new List<EndpointExecutionParticipant>(slots.Count);
        var uniqueRoutes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            if (!slot.RequiredJobId.HasValue ||
                !DadRosterCharacterMerge.IsCombatJob(slot.RequiredJobId.Value))
            {
                return Fail($"{slot.SlotId} is missing one exact combat job for AutoParty execution.", out blocker);
            }

            string ownerId;
            string islandId;
            string opaqueCharacterId;
            if (slot.RouteKind == DadRunSlotRouteKind.RegisteredIsland)
            {
                ownerId = slot.OwnerId;
                islandId = slot.IslandId;
                opaqueCharacterId = slot.OpaqueCharacterId;
            }
            else if (slot.RouteKind == DadRunSlotRouteKind.LanWorker)
            {
                var matches = localCrew.Where(candidate =>
                        string.Equals(
                            DadRosterIdentity.ResolveAccountKey(
                                candidate.Character.AccountId,
                                candidate.Character.AccountAlias).Value,
                            slot.AccountKey.Value,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            candidate.Character.CharacterKey,
                            slot.CharacterKey.Value,
                            StringComparison.OrdinalIgnoreCase) &&
                        candidate.PermittedCombatJobIds.Contains(slot.RequiredJobId.Value) &&
                        !string.IsNullOrWhiteSpace(candidate.Identity.OpaqueCharacterId))
                    .ToList();
                if (matches.Count != 1)
                {
                    return Fail(
                        $"{slot.SlotId} does not map to one exact published local AutoParty character route.",
                        out blocker);
                }

                ownerId = configuration.RegisteredOwnerId;
                islandId = configuration.RegisteredIslandId;
                opaqueCharacterId = matches[0].Identity.OpaqueCharacterId;
            }
            else
            {
                return Fail($"{slot.SlotId} has an unsupported AutoParty execution route.", out blocker);
            }

            if (string.IsNullOrWhiteSpace(ownerId) ||
                string.IsNullOrWhiteSpace(islandId) ||
                string.IsNullOrWhiteSpace(opaqueCharacterId) ||
                !uniqueRoutes.Add($"{ownerId}\n{islandId}\n{opaqueCharacterId}"))
            {
                return Fail($"{slot.SlotId} has an invalid or duplicate AutoParty execution route.", out blocker);
            }

            requests.Add(new DadAutoPartyParticipantRequest(
                slot.SlotId,
                ownerId,
                islandId,
                opaqueCharacterId,
                slot.RequiredJobId.Value,
                slot.IsLeader,
                slot.IsInviter));
            projected.Add(new EndpointExecutionParticipant(
                slot.SlotId,
                new OwnerId(ownerId),
                new IslandId(islandId),
                new OpaqueCharacterId(opaqueCharacterId),
                new JobId(slot.RequiredJobId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                slot.IsLeader ? EndpointExecutionRole.QueueLeader : EndpointExecutionRole.Participant,
                slot.IsInviter,
                MapAdsLootMode(slot.AdsLootMode)));
        }

        var waitPolicy = plan.Orchestration.WaitPolicy ?? plan.Request.Orchestration?.WaitPolicy;
        if (waitPolicy == null)
            return Fail("AutoParty execution projection is missing its wait policy.", out blocker);
        var participantReadyTimeout = checked((int)waitPolicy.GetParticipantReadyTimeout().TotalSeconds);
        var assemblyTimeout = checked((int)waitPolicy.GetAssemblyTimeout().TotalSeconds);
        var leaseDuration = checked((int)waitPolicy.GetLeaseDuration().TotalSeconds);
        if (participantReadyTimeout > 3600 || assemblyTimeout > 3600 || leaseDuration > 1800)
            return Fail("AutoParty execution wait policy exceeds the bounded protocol limits.", out blocker);

        var modules = ImmutableArray<EndpointExecutionModule>.Empty;
        if (!plan.Orchestration.AutoPartyFormationOnly)
        {
            if (manifest.Modules.Count is < 1 or > 64 || manifest.Modules.Count != plan.Modules.Count)
                return Fail("AutoParty execution projection requires the complete frozen module sequence.", out blocker);
            var projectedModules = ImmutableArray.CreateBuilder<EndpointExecutionModule>(manifest.Modules.Count);
            for (var index = 0; index < manifest.Modules.Count; index++)
            {
                var module = manifest.Modules[index];
                var planned = plan.Modules[index];
                if (module.ModuleId == DadModuleId.None ||
                    module.ModuleId != planned.ModuleId ||
                    module.ExpectedPartySize != slots.Count)
                {
                    return Fail(
                        $"AutoParty module {index + 1} contradicts the frozen full-roster execution plan.",
                        out blocker);
                }

                var displayName = string.IsNullOrWhiteSpace(module.DutyName)
                    ? (planned.DisplayName ?? string.Empty).Trim()
                    : module.DutyName.Trim();
                if (displayName.Length == 0)
                    displayName = module.ModuleId.ToString();
                projectedModules.Add(new EndpointExecutionModule(
                    index,
                    module.ModuleId.ToString(),
                    new ActivityId(BuildActivityId(module)),
                    displayName,
                    module.TargetKind.ToString(),
                    module.ContentFinderConditionId,
                    module.RouletteId,
                    module.Unsynced,
                    module.ExpectedPartySize));
            }
            modules = projectedModules.MoveToImmutable();
            if (!string.Equals(modules[0].ActivityId.Value, activityId, StringComparison.Ordinal))
                return Fail("AutoParty proposal activity contradicts its first frozen module.", out blocker);
        }

        var repairPolicy = (plan.Request.PreDutyRepairPolicy ?? new DadPreDutyRepairPolicy())
            .Clone()
            .Normalize();
        participantRequests = requests;
        executionPlan = new EndpointExecutionPlan(
            plan.Request.RequestId,
            plan.Orchestration.AutoPartyFormationOnly,
            plan.Orchestration.RequirePostArReady,
            participantReadyTimeout,
            assemblyTimeout,
            leaseDuration,
            new EndpointRepairPolicy(
                repairPolicy.Enabled,
                repairPolicy.ThresholdPercent,
                repairPolicy.AdsMode),
            projected.ToImmutableArray(),
            modules,
            useFrenRider);
        return true;
    }

    private static string MapAdsLootMode(DadAdsLootMode? mode)
        => mode switch
        {
            DadAdsLootMode.NoChange => "no-change",
            DadAdsLootMode.Need => "need",
            DadAdsLootMode.Greed => "greed",
            DadAdsLootMode.Pass => "pass",
            _ => string.Empty,
        };

    private static bool SameSlots(IEnumerable<SlotRuntime> existing, IReadOnlyList<DadFrozenRunSlot> incoming)
        => existing.Count() == incoming.Count && incoming.All(slot =>
            existing.Any(candidate => SameSlot(candidate.Slot, slot)));

    private static bool SameSlot(DadFrozenRunSlot left, DadFrozenRunSlot right)
        => left.RouteKind == right.RouteKind &&
           string.Equals(left.SlotId, right.SlotId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.OwnerId, right.OwnerId, StringComparison.Ordinal) &&
           string.Equals(left.IslandId, right.IslandId, StringComparison.Ordinal) &&
           string.Equals(left.OpaqueCharacterId, right.OpaqueCharacterId, StringComparison.Ordinal) &&
           left.RequiredJobId == right.RequiredJobId;

    private static bool Fail(string reason, out string blocker)
    {
        blocker = reason;
        return false;
    }

    private static bool IsBoundedLocatorValue(string? value, int maximumLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && value == value.Trim() &&
           value.All(static character => !char.IsControl(character));

    private static bool SameModuleReference(
        EndpointExecutionModuleReference? left,
        EndpointExecutionModuleReference? right)
        => left == null
            ? right == null
            : right != null && left.ModuleIndex == right.ModuleIndex &&
              string.Equals(left.ModuleId, right.ModuleId, StringComparison.Ordinal);

    private static bool IsCompletedOperation(
        SlotRuntime slot,
        ExecutionOperationKind kind,
        EndpointExecutionModuleReference? moduleReference)
        => kind switch
        {
            ExecutionOperationKind.Form => slot.Stage == DadAutoPartyParticipantStage.Formed,
            ExecutionOperationKind.Queue => slot.Stage == DadAutoPartyParticipantStage.Queued &&
                                            SameModuleReference(slot.ActiveModuleReference, moduleReference),
            ExecutionOperationKind.Restore => slot.Stage == DadAutoPartyParticipantStage.Restored,
            ExecutionOperationKind.Cancel => slot.Stage == DadAutoPartyParticipantStage.Cancelled,
            _ => false,
        };

    private static bool ValidateFormLocators(
        ProposalRuntime proposal,
        SlotRuntime slot,
        DadExpectedPartyInviter? inviter,
        IReadOnlyList<DadNativePartyInviteTarget> partyInviteTargets,
        out string safeCode)
    {
        safeCode = string.Empty;
        if (!slot.Slot.IsInviter)
        {
            if (inviter == null ||
                DadPartyInvitationAcceptanceTracker.Validate(inviter).Length > 0 ||
                !string.Equals(inviter.RunId, proposal.RunId, StringComparison.Ordinal) ||
                partyInviteTargets.Count != 0)
            {
                safeCode = "dad-remote-inviter-locator-invalid";
                return false;
            }
            return true;
        }

        if (inviter != null)
        {
            safeCode = "dad-remote-slot1-inviter-locator-unexpected";
            return false;
        }

        var expectedFollowers = proposal.ExecutionPlan.Participants
            .Where(participant => !participant.IsInviter)
            .ToArray();
        if (partyInviteTargets.Count != expectedFollowers.Length ||
            partyInviteTargets.Count > 7 ||
            partyInviteTargets.Select(static target => target.ContentId).Distinct().Count() != partyInviteTargets.Count ||
            partyInviteTargets.Select(static target => target.SlotId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != partyInviteTargets.Count)
        {
            safeCode = "dad-remote-slot1-party-targets-invalid";
            return false;
        }

        for (var index = 0; index < expectedFollowers.Length; index++)
        {
            var expected = expectedFollowers[index];
            var target = partyInviteTargets[index];
            if (target.ContentId == 0 || target.WorldId == 0 || target.WorkerSessionId.IsEmpty ||
                target.AccountKey.IsEmpty || target.CharacterKey.IsEmpty ||
                string.IsNullOrWhiteSpace(target.CharacterName) ||
                !string.Equals(target.RunId, proposal.RunId, StringComparison.Ordinal) ||
                !string.Equals(target.SlotId, expected.SlotId, StringComparison.OrdinalIgnoreCase))
            {
                safeCode = "dad-remote-slot1-party-targets-invalid";
                return false;
            }
        }
        return true;
    }

    private static bool ValidateRestoreLocators(
        ProposalRuntime proposal,
        SlotRuntime slot,
        DadExpectedPartyInviter? inviter,
        IReadOnlyList<DadNativePartyInviteTarget> partyInviteTargets,
        out string safeCode)
    {
        safeCode = string.Empty;
        var expectedInviter = proposal.ExecutionPlan.Participants.SingleOrDefault(static participant => participant.IsInviter);
        var expectedFollowers = proposal.ExecutionPlan.Participants.Where(static participant => !participant.IsInviter).ToArray();
        if (inviter == null || expectedInviter == null ||
            DadPartyInvitationAcceptanceTracker.Validate(inviter).Length > 0 ||
            !string.Equals(inviter.RunId, proposal.RunId, StringComparison.Ordinal) ||
            partyInviteTargets.Count != expectedFollowers.Length || partyInviteTargets.Count is < 1 or > 7 ||
            partyInviteTargets.Select(static target => target.ContentId).Distinct().Count() != partyInviteTargets.Count ||
            partyInviteTargets.Select(static target => target.SlotId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != partyInviteTargets.Count)
        {
            safeCode = "dad-remote-restore-locator-invalid";
            return false;
        }

        foreach (var expected in expectedFollowers)
        {
            var matches = partyInviteTargets.Where(target =>
                string.Equals(target.RunId, proposal.RunId, StringComparison.Ordinal) &&
                string.Equals(target.SlotId, expected.SlotId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1 || matches[0].ContentId == 0 || matches[0].WorldId == 0 ||
                matches[0].WorkerSessionId.IsEmpty || matches[0].AccountKey.IsEmpty || matches[0].CharacterKey.IsEmpty ||
                string.IsNullOrWhiteSpace(matches[0].CharacterName))
            {
                safeCode = "dad-remote-restore-locator-invalid";
                return false;
            }
        }

        var localIsInviter = slot.Slot.IsInviter;
        var localTargetCount = localIsInviter
            ? string.Equals(slot.Slot.SlotId, expectedInviter.SlotId, StringComparison.OrdinalIgnoreCase) ? 1 : 0
            : partyInviteTargets.Count(target =>
                string.Equals(target.SlotId, slot.Slot.SlotId, StringComparison.OrdinalIgnoreCase));
        if (localTargetCount != 1)
        {
            safeCode = "dad-remote-restore-local-route-invalid";
            return false;
        }
        return true;
    }

    private sealed class ProposalRuntime
    {
        public ProposalRuntime(
            Guid proposalId,
            string runId,
            DadModuleId moduleId,
            string activityId,
            bool formationOnly,
            EndpointExecutionPlan executionPlan,
            DateTimeOffset createdAt,
            DateTimeOffset expiresAt,
            Dictionary<string, SlotRuntime> slots)
        {
            ProposalId = proposalId;
            RunId = runId;
            ModuleId = moduleId;
            ActivityId = activityId;
            FormationOnly = formationOnly;
            ExecutionPlan = executionPlan;
            CreatedAt = createdAt;
            ExpiresAt = expiresAt;
            Slots = slots;
        }

        public Guid ProposalId { get; }
        public string RunId { get; }
        public DadModuleId ModuleId { get; }
        public string ActivityId { get; }
        public bool FormationOnly { get; }
        public EndpointExecutionPlan ExecutionPlan { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset ExpiresAt { get; set; }
        public long RenewalGeneration { get; set; }
        public DateTimeOffset PendingRenewalExpiresAt { get; set; }
        public long PendingRenewalGeneration { get; set; }
        public Dictionary<string, SlotRuntime> Slots { get; }
    }

    private sealed class SlotRuntime
    {
        public SlotRuntime(DadFrozenRunSlot slot, DateTimeOffset observedAt)
        {
            Slot = slot;
            ObservedAt = observedAt;
        }

        public DadFrozenRunSlot Slot { get; }
        public DadAutoPartyParticipantStage Stage { get; set; } = DadAutoPartyParticipantStage.ProposalPending;
        public Guid ReservationId { get; set; }
        public Guid LeaseId { get; set; }
        public long StateGeneration { get; set; } = 1;
        public long ReadinessGeneration { get; set; }
        public int NextModuleIndex { get; set; }
        public EndpointExecutionModuleReference? ActiveModuleReference { get; set; }
        public ImmutableArray<ulong> ObservedPartyContentIds { get; set; } = ImmutableArray<ulong>.Empty;
        public DateTimeOffset? LeaseIssuedAt { get; set; }
        public DateTimeOffset? LeaseExpiresAt { get; set; }
        public DateTimeOffset ObservedAt { get; set; }
        public string SafeCode { get; set; } = "dad-remote-proposal-pending";
        public DadNativePartyInviteTarget? InviteTarget { get; set; }
        public DateTimeOffset InviteTargetExpiresAt { get; set; }
        public bool IsTerminal => Stage is DadAutoPartyParticipantStage.Settled or
            DadAutoPartyParticipantStage.Restored or DadAutoPartyParticipantStage.Cancelled or
            DadAutoPartyParticipantStage.Revoked or DadAutoPartyParticipantStage.Expired or
            DadAutoPartyParticipantStage.Failed;
    }

    private sealed class PendingOperation
    {
        public PendingOperation(
            Guid operationId,
            Guid proposalId,
            string slotId,
            ExecutionOperationKind kind,
            EndpointExecutionModuleReference? moduleReference,
            DateTimeOffset issuedAt,
            DateTimeOffset expiresAt)
        {
            OperationId = operationId;
            ProposalId = proposalId;
            SlotId = slotId;
            Kind = kind;
            ModuleReference = moduleReference;
            IssuedAt = issuedAt;
            ExpiresAt = expiresAt;
        }

        public Guid OperationId { get; }
        public Guid ProposalId { get; }
        public string SlotId { get; }
        public ExecutionOperationKind Kind { get; }
        public EndpointExecutionModuleReference? ModuleReference { get; }
        public DateTimeOffset IssuedAt { get; }
        public DateTimeOffset ExpiresAt { get; }
        public bool Completed { get; set; }
    }

    private sealed class PendingCommand
    {
        public PendingCommand(DadAutoPartyParticipantCommand command)
            => Command = command;

        public DadAutoPartyParticipantCommand Command { get; }
        public Guid DispatchLeaseId { get; set; }
        public DateTimeOffset DispatchLeaseExpiresAt { get; set; }
    }

    private static DadAutoPartyParticipantCommand CloneCommand(DadAutoPartyParticipantCommand command)
        => command with
        {
            Inviter = command.Inviter?.Clone(),
            PartyInviteTargets = command.PartyInviteTargets?
                .Select(static target => target.Clone())
                .ToList(),
            Participants = command.Participants?.Select(static participant => participant with { }).ToList(),
            FrenRiderProfile = command.FrenRiderProfile.IsDefault
                ? default
                : ImmutableArray.CreateRange(command.FrenRiderProfile),
        };
}
