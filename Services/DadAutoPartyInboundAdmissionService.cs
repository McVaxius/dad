using System.Collections.Immutable;
using System.Globalization;
using AutoParty.Contracts;
using dad.Models;

namespace dad.Services;

internal enum DadAutoPartyInboundAdmissionDisposition
{
    Pending = 0,
    Ready = 1,
    Denied = 2,
}

internal sealed record DadAutoPartyInboundAdmissionResult(
    string RunId,
    DadAutoPartyInboundAdmissionDisposition Disposition,
    string SafeBlocker,
    ImmutableArray<string> OwnedSlotIds,
    ImmutableArray<DadNativePartyInviteTarget> InviteTargets)
{
    public DadAutoPartyInboundAdmissionResult(
        string runId,
        bool ready,
        string safeBlocker,
        ImmutableArray<string> ownedSlotIds,
        ImmutableArray<DadNativePartyInviteTarget> inviteTargets)
        : this(
            runId,
            ready ? DadAutoPartyInboundAdmissionDisposition.Ready : DadAutoPartyInboundAdmissionDisposition.Denied,
            safeBlocker,
            ownedSlotIds,
            inviteTargets)
    {
    }

    public bool Ready => Disposition == DadAutoPartyInboundAdmissionDisposition.Ready;

    public static DadAutoPartyInboundAdmissionResult Blocked(string runId, string safeBlocker)
        => new(runId, DadAutoPartyInboundAdmissionDisposition.Denied, safeBlocker, [], []);

    public static DadAutoPartyInboundAdmissionResult Pending(string runId, string safeBlocker)
        => new(runId, DadAutoPartyInboundAdmissionDisposition.Pending, safeBlocker, [], []);
}

internal sealed class DadAutoPartyInboundAdmissionService
{
    internal const string InvalidProposal = "dad-inbound-admission-invalid-proposal";
    internal const string ExpiredProposal = "dad-inbound-admission-proposal-expired";
    internal const string InvalidOwnedParticipants = "dad-inbound-admission-owned-participants-invalid";
    internal const string InvalidRequestedJob = "dad-inbound-admission-requested-job-invalid";
    internal const string RuntimeRouteMismatch = "dad-inbound-admission-runtime-route-mismatch";
    internal const string FleetRouteMismatch = RuntimeRouteMismatch;
    internal const string WorkerRouteMismatch = "dad-inbound-admission-worker-route-mismatch";
    internal const string WakeBlocked = "dad-inbound-admission-wake-blocked";
    internal const string ReadinessBlocked = "dad-inbound-admission-readiness-blocked";
    internal const string DependenciesBlocked = "dad-inbound-admission-dependencies-blocked";
    internal const string ClaimBlocked = "dad-inbound-admission-claim-blocked";
    internal const string TakeoverPending = "dad-inbound-admission-takeover-pending";
    internal const string RestorationPending = "dad-inbound-admission-restoration-pending";

    private readonly string registeredOwnerId;
    private readonly string registeredIslandId;
    private readonly DadWorkerSessionId authorityWorkerSessionId;
    private readonly Func<DadAutoPartyInboundRoute, DadWakeTakeoverRequestDto, DadWakeTakeoverResultDto?> submitTakeover;
    private readonly Func<DadParticipantSnapshot, DadWakeRequestDto, DadParticipantReadyDto?> submitWake;
    private readonly Func<DadClaimRequestDto, DadParticipantSnapshot, TimeSpan, DadParticipantLeaseRecord?> issueLease;
    private readonly Func<DadParticipantSnapshot, DadClaimRequestDto, DadClaimDecisionDto?> submitClaim;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly TimeSpan dependencyStaleAfter;
    private readonly object gate = new();
    private readonly Dictionary<Guid, TakeoverProposalState> takeoverProposals = [];

    public DadAutoPartyInboundAdmissionService(
        string registeredOwnerId,
        string registeredIslandId,
        DadWorkerSessionId authorityWorkerSessionId,
        Func<DadAutoPartyInboundRoute, DadWakeTakeoverRequestDto, DadWakeTakeoverResultDto?> submitTakeover,
        Func<DadParticipantSnapshot, DadWakeRequestDto, DadParticipantReadyDto?> submitWake,
        Func<DadClaimRequestDto, DadParticipantSnapshot, TimeSpan, DadParticipantLeaseRecord?> issueLease,
        Func<DadParticipantSnapshot, DadClaimRequestDto, DadClaimDecisionDto?> submitClaim,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? dependencyStaleAfter = null)
    {
        this.registeredOwnerId = Normalize(registeredOwnerId);
        this.registeredIslandId = Normalize(registeredIslandId);
        this.authorityWorkerSessionId = authorityWorkerSessionId;
        this.submitTakeover = submitTakeover ?? throw new ArgumentNullException(nameof(submitTakeover));
        this.submitWake = submitWake ?? throw new ArgumentNullException(nameof(submitWake));
        this.issueLease = issueLease ?? throw new ArgumentNullException(nameof(issueLease));
        this.submitClaim = submitClaim ?? throw new ArgumentNullException(nameof(submitClaim));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.dependencyStaleAfter = dependencyStaleAfter is { } staleAfter && staleAfter > TimeSpan.Zero
            ? staleAfter
            : TimeSpan.FromSeconds(15);
    }

    public DadAutoPartyInboundAdmissionResult Admit(
        RunProposal proposal,
        DadAutoPartyListingPublication publication)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(publication);

        var plan = proposal.ExecutionPlan;
        var runId = plan?.RunId ?? string.Empty;
        var now = utcNow();
        if (plan == null || string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(registeredOwnerId) || string.IsNullOrWhiteSpace(registeredIslandId) ||
            authorityWorkerSessionId.IsEmpty || proposal.Participants.IsDefaultOrEmpty ||
            proposal.Participants.Length > 8 || plan.Participants.IsDefaultOrEmpty || plan.Participants.Length > 8 ||
            plan.LeaseDurationSeconds is < 3 or > 1800)
        {
            return DenyOrRestore(proposal.ProposalId, runId, InvalidProposal);
        }
        if (proposal.Header.ExpiresAt <= now)
            return DenyOrRestore(proposal.ProposalId, runId, ExpiredProposal);

        var participants = plan.Participants;
        if (participants.Any(participant =>
                SameOrdinal(participant.OwnerId.Value, registeredOwnerId) !=
                SameOrdinal(participant.OwnerIslandId.Value, registeredIslandId)))
        {
            return DenyOrRestore(proposal.ProposalId, runId, InvalidOwnedParticipants);
        }

        var owned = participants.Where(participant =>
                SameOrdinal(participant.OwnerId.Value, registeredOwnerId) &&
                SameOrdinal(participant.OwnerIslandId.Value, registeredIslandId))
            .ToArray();
        if (owned.Length is < 1 or > 8 ||
            owned.Select(static participant => participant.SlotId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != owned.Length ||
            owned.Select(static participant => participant.CharacterId.Value)
                .Distinct(StringComparer.Ordinal).Count() != owned.Length)
        {
            return DenyOrRestore(proposal.ProposalId, runId, InvalidOwnedParticipants);
        }

        if (!TryResolveModuleId(plan, out var moduleId))
            return DenyOrRestore(proposal.ProposalId, runId, InvalidProposal);

        TakeoverProposalState takeover;
        lock (gate)
        {
            if (!takeoverProposals.TryGetValue(proposal.ProposalId, out takeover!))
            {
                var routes = new List<TakeoverSlotState>(owned.Length);
                foreach (var participant in owned)
                {
                    if (!uint.TryParse(
                            participant.RequestedJob.Value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var requestedJobId) || requestedJobId == 0)
                        return DadAutoPartyInboundAdmissionResult.Blocked(runId, InvalidRequestedJob);

                    var matches = publication.InboundRoutes.Where(route =>
                            SameOrdinal(route.OpaqueCharacterId, participant.CharacterId.Value) &&
                            route.OwnerSnapshot != null &&
                            !route.WorkerSessionId.IsEmpty)
                        .Take(2)
                        .ToArray();
                    if (matches.Length != 1 || !TryValidatePublishedRoute(matches[0], now, out var route))
                        return DadAutoPartyInboundAdmissionResult.Blocked(runId, RuntimeRouteMismatch);
                    routes.Add(new TakeoverSlotState(
                        participant,
                        requestedJobId,
                        route,
                        new DadWakeTakeoverRequestDto
                        {
                            SchedulerRunId = runId,
                            SlotId = participant.SlotId,
                            AccountKey = route.AccountKey,
                            CharacterKey = route.CharacterKey,
                            RequestedAtUtc = now.UtcDateTime,
                            OperationToken = $"autoparty-{proposal.ProposalId:N}-{participant.SlotId}",
                        }));
                }
                if (routes.Select(static route => route.Route.WorkerSessionId.Value)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() != routes.Count)
                    return DadAutoPartyInboundAdmissionResult.Blocked(runId, WorkerRouteMismatch);
                takeover = new TakeoverProposalState(
                    proposal.ProposalId,
                    runId,
                    routes);
                takeoverProposals[proposal.ProposalId] = takeover;
            }
        }

        if (!string.IsNullOrWhiteSpace(takeover.TerminalBlocker))
            return RestoreOrDeny(takeover, takeover.TerminalBlocker);
        if (!MatchesFrozenProposal(takeover, owned))
            return MarkTerminalAndRestore(takeover, InvalidOwnedParticipants);

        var takeoverResults = new List<(TakeoverSlotState Slot, DadWakeTakeoverResultDto Result)>(takeover.Slots.Count);
        foreach (var slot in takeover.Slots)
        {
            var result = SubmitTakeover(slot, DadWakeTakeoverMessageKind.Prepare);
            if (result == null)
                return DadAutoPartyInboundAdmissionResult.Pending(runId, TakeoverPending);
            if (result.Status == DadWakeTakeoverStatus.Blocked)
                return MarkTerminalAndRestore(takeover, WakeBlocked);
            takeoverResults.Add((slot, result));
        }

        if (takeoverResults.Any(static item => item.Result.Phase < DadWakeTakeoverPhase.Prepared))
            return DadAutoPartyInboundAdmissionResult.Pending(runId, TakeoverPending);

        if (takeoverResults.Any(static item => item.Result.Phase == DadWakeTakeoverPhase.Prepared))
        {
            takeover.ResetExecutionUtc ??= now.UtcDateTime.AddSeconds(1);
            foreach (var item in takeoverResults.Where(static item => item.Result.Phase == DadWakeTakeoverPhase.Prepared))
            {
                var result = SubmitTakeover(
                    item.Slot,
                    DadWakeTakeoverMessageKind.Go,
                    DadWakeCommitKind.Reset,
                    takeover.ResetExecutionUtc);
                if (result?.Status == DadWakeTakeoverStatus.Blocked)
                    return MarkTerminalAndRestore(takeover, WakeBlocked);
            }
            return DadAutoPartyInboundAdmissionResult.Pending(runId, TakeoverPending);
        }

        if (takeoverResults.Any(static item => item.Result.Phase < DadWakeTakeoverPhase.ResetVerified))
            return DadAutoPartyInboundAdmissionResult.Pending(runId, TakeoverPending);

        if (takeoverResults.Any(static item => item.Result.Phase == DadWakeTakeoverPhase.ResetVerified))
        {
            takeover.RelogExecutionUtc ??= now.UtcDateTime.AddSeconds(1);
            foreach (var item in takeoverResults.Where(static item => item.Result.Phase == DadWakeTakeoverPhase.ResetVerified))
            {
                var result = SubmitTakeover(
                    item.Slot,
                    DadWakeTakeoverMessageKind.Go,
                    DadWakeCommitKind.Relog,
                    takeover.RelogExecutionUtc);
                if (result?.Status == DadWakeTakeoverStatus.Blocked)
                    return MarkTerminalAndRestore(takeover, WakeBlocked);
            }
            return DadAutoPartyInboundAdmissionResult.Pending(runId, TakeoverPending);
        }

        if (takeoverResults.Any(static item => item.Result.Phase != DadWakeTakeoverPhase.Ready))
            return DadAutoPartyInboundAdmissionResult.Pending(runId, TakeoverPending);

        var targets = ImmutableArray.CreateBuilder<DadNativePartyInviteTarget>(owned.Length);
        var slotIds = ImmutableArray.CreateBuilder<string>(owned.Length);
        foreach (var slot in takeover.Slots)
        {
            var participant = slot.Participant;
            var requestedJobId = slot.RequestedJobId;
            var route = takeoverResults.Single(item => string.Equals(
                    item.Slot.Participant.SlotId,
                    participant.SlotId,
                    StringComparison.OrdinalIgnoreCase))
                .Result.Snapshot;

            var wakeRequest = new DadWakeRequestDto
            {
                RunId = runId,
                AuthorityWorkerSessionId = authorityWorkerSessionId,
                AuthorityMode = DadAuthorityMode.ServerDad,
                ModuleId = moduleId,
                RequiredAccountKey = slot.Route.AccountKey,
                RequiredCharacterKey = slot.Route.CharacterKey,
                RequiredContentId = slot.Route.ContentId,
                RequiredJobId = requestedJobId,
                AssignedSlotId = participant.SlotId,
                RequirePostArReady = plan.RequirePostArReady,
            };

            DadParticipantReadyDto? wake;
            try
            {
                wake = submitWake(route, wakeRequest);
            }
            catch
            {
                wake = null;
            }
            if (wake == null || !wake.AcceptedAssignment)
                return DadAutoPartyInboundAdmissionResult.Pending(runId, WakeBlocked);
            if (!TryValidateReady(
                    wake,
                    wakeRequest,
                    route.WorkerSessionId,
                    requestedJobId,
                    plan.RequirePostArReady,
                    now.UtcDateTime,
                    out var readySnapshot,
                    out var dependencyBlocked))
            {
                return DadAutoPartyInboundAdmissionResult.Pending(
                    runId,
                    dependencyBlocked ? DependenciesBlocked : ReadinessBlocked);
            }

            var claimRequest = new DadClaimRequestDto
            {
                RunId = runId,
                AuthorityWorkerSessionId = authorityWorkerSessionId,
                ModuleId = moduleId,
                SlotId = participant.SlotId,
                RequiredAccountKey = wakeRequest.RequiredAccountKey,
                RequiredCharacterKey = wakeRequest.RequiredCharacterKey,
            };
            DadParticipantLeaseRecord? lease;
            try
            {
                lease = issueLease(
                    claimRequest,
                    readySnapshot,
                    TimeSpan.FromSeconds(plan.LeaseDurationSeconds));
            }
            catch
            {
                lease = null;
            }
            if (lease == null)
                return MarkTerminalAndRestore(takeover, ClaimBlocked);
            claimRequest.Lease = lease;

            DadClaimDecisionDto? claim;
            try
            {
                claim = submitClaim(readySnapshot, claimRequest);
            }
            catch
            {
                claim = null;
            }
            if (!TryValidateClaim(claim, claimRequest, readySnapshot, now.UtcDateTime))
                return MarkTerminalAndRestore(takeover, ClaimBlocked);

            slotIds.Add(participant.SlotId);
            targets.Add(new DadNativePartyInviteTarget
            {
                RunId = runId,
                ModuleId = moduleId,
                SlotId = participant.SlotId,
                AccountKey = wakeRequest.RequiredAccountKey,
                CharacterKey = wakeRequest.RequiredCharacterKey,
                ContentId = readySnapshot.Character.ContentId,
                CharacterName = readySnapshot.Character.CharacterName,
                WorldId = checked((ushort)readySnapshot.Character.WorldId),
                WorkerSessionId = readySnapshot.WorkerSessionId,
            });
        }

        return new DadAutoPartyInboundAdmissionResult(
            runId,
            DadAutoPartyInboundAdmissionDisposition.Ready,
            string.Empty,
            slotIds.MoveToImmutable(),
            targets.MoveToImmutable());
    }

    public bool RestoreProposal(Guid proposalId, string reason)
    {
        TakeoverProposalState? state;
        lock (gate)
            takeoverProposals.TryGetValue(proposalId, out state);
        if (state == null)
            return true;
        state.TerminalBlocker = string.IsNullOrWhiteSpace(reason) ? RestorationPending : reason.Trim();
        var restored = TryRestore(state);
        if (restored)
        {
            lock (gate)
                takeoverProposals.Remove(proposalId);
        }
        return restored;
    }

    private DadAutoPartyInboundAdmissionResult DenyOrRestore(Guid proposalId, string runId, string blocker)
    {
        TakeoverProposalState? state;
        lock (gate)
        {
            takeoverProposals.TryGetValue(proposalId, out state);
            if (state != null)
                state.TerminalBlocker = blocker;
        }
        return state == null
            ? DadAutoPartyInboundAdmissionResult.Blocked(runId, blocker)
            : RestoreOrDeny(state, blocker);
    }

    private DadAutoPartyInboundAdmissionResult MarkTerminalAndRestore(
        TakeoverProposalState state,
        string blocker)
    {
        state.TerminalBlocker = blocker;
        return RestoreOrDeny(state, blocker);
    }

    private DadAutoPartyInboundAdmissionResult RestoreOrDeny(
        TakeoverProposalState state,
        string blocker)
    {
        if (!TryRestore(state))
            return DadAutoPartyInboundAdmissionResult.Pending(state.RunId, RestorationPending);
        lock (gate)
            takeoverProposals.Remove(state.ProposalId);
        return DadAutoPartyInboundAdmissionResult.Blocked(state.RunId, blocker);
    }

    private bool TryRestore(TakeoverProposalState state)
    {
        var complete = true;
        foreach (var slot in state.Slots)
        {
            var result = SubmitTakeover(slot, DadWakeTakeoverMessageKind.Cancel);
            complete &= DadSchedulerRoutingRules.IsTakeoverCancellationComplete(result);
        }
        return complete;
    }

    private DadWakeTakeoverResultDto? SubmitTakeover(
        TakeoverSlotState slot,
        DadWakeTakeoverMessageKind messageKind,
        DadWakeCommitKind commitKind = DadWakeCommitKind.None,
        DateTime? executionTimeUtc = null)
    {
        var request = CloneTakeoverRequest(slot.Request);
        request.MessageKind = messageKind;
        request.CommitKind = commitKind;
        request.ExecutionTimeUtc = executionTimeUtc;
        try
        {
            return submitTakeover(slot.Route, request);
        }
        catch
        {
            return null;
        }
    }

    private static bool MatchesFrozenProposal(
        TakeoverProposalState state,
        IReadOnlyCollection<EndpointExecutionParticipant> participants)
        => state.Slots.Count == participants.Count && state.Slots.All(slot => participants.Any(participant =>
            string.Equals(participant.SlotId, slot.Participant.SlotId, StringComparison.OrdinalIgnoreCase) &&
            SameOrdinal(participant.CharacterId.Value, slot.Participant.CharacterId.Value) &&
            SameOrdinal(participant.RequestedJob.Value, slot.Participant.RequestedJob.Value)));

    private static bool TryValidatePublishedRoute(
        DadAutoPartyInboundRoute candidate,
        DateTimeOffset now,
        out DadAutoPartyInboundRoute route)
    {
        route = candidate;
        var owner = candidate.OwnerSnapshot;
        return !candidate.AccountKey.IsEmpty && !candidate.CharacterKey.IsEmpty &&
               candidate.ContentId != 0 && candidate.WorldId is > 0 and <= ushort.MaxValue &&
               !candidate.WorkerSessionId.IsEmpty && owner != null &&
               owner.AutoRetainerAvailable &&
               SameIgnoreCase(owner.WorkerSessionId.Value, candidate.WorkerSessionId.Value) &&
               SameIgnoreCase(owner.ManagedAccountKey.Value, candidate.AccountKey.Value) &&
               (owner.IsLocalClient || now.UtcDateTime - owner.LastHeartbeatUtc <= TimeSpan.FromSeconds(15)) &&
               (owner.AvailableCharacterKeys.Any(available =>
                    DadRosterIdentity.SameCharacter(available, 0, candidate.CharacterKey, candidate.ContentId)) ||
                DadRosterIdentity.SameCharacter(
                    owner.ActiveCharacterKey,
                    owner.Character?.ContentId ?? 0,
                    candidate.CharacterKey,
                    candidate.ContentId));
    }

    private static DadWakeTakeoverRequestDto CloneTakeoverRequest(DadWakeTakeoverRequestDto request)
        => new()
        {
            SchedulerRunId = request.SchedulerRunId,
            SlotId = request.SlotId,
            AccountKey = request.AccountKey,
            CharacterKey = request.CharacterKey,
            RequestedAtUtc = request.RequestedAtUtc,
            OperationToken = request.OperationToken,
            MessageKind = request.MessageKind,
            CommitKind = request.CommitKind,
            ExecutionTimeUtc = request.ExecutionTimeUtc,
        };

    private bool TryValidateReady(
        DadParticipantReadyDto ready,
        DadWakeRequestDto request,
        DadWorkerSessionId expectedWorker,
        uint requestedJobId,
        bool requirePostArReady,
        DateTime nowUtc,
        out DadParticipantSnapshot snapshot,
        out bool dependencyBlocked)
    {
        snapshot = ready.Snapshot;
        dependencyBlocked = false;
        if (snapshot == null || snapshot.Character == null ||
            ready.State != DadParticipantState.Ready || snapshot.State != DadParticipantState.Ready ||
            !SameIgnoreCase(ready.RunId, request.RunId) || !SameIgnoreCase(snapshot.RunId, request.RunId) ||
            !SameIgnoreCase(ready.WorkerSessionId.Value, expectedWorker.Value) ||
            !SameIgnoreCase(snapshot.WorkerSessionId.Value, expectedWorker.Value) ||
            !SameIgnoreCase(ready.CharacterKey.Value, request.RequiredCharacterKey.Value) ||
            !SameIgnoreCase(snapshot.ManagedAccountKey.Value, request.RequiredAccountKey.Value) ||
            !SameIgnoreCase(snapshot.ActiveCharacterKey.Value, request.RequiredCharacterKey.Value) ||
            !SameIgnoreCase(snapshot.Character.CharacterKey, request.RequiredCharacterKey.Value) ||
            snapshot.Character.ContentId != request.RequiredContentId ||
            snapshot.Character.CurrentJobId != requestedJobId ||
            string.IsNullOrWhiteSpace(snapshot.Character.CharacterName) ||
            snapshot.Character.WorldId is 0 or > ushort.MaxValue ||
            !snapshot.Character.IsLiveConnected || !snapshot.WorldReadyStable ||
            !SameIgnoreCase(snapshot.AssignedSlotId, request.AssignedSlotId) ||
            requirePostArReady && (!ready.PostArReady || !snapshot.PostArReady))
        {
            return false;
        }

        var dependency = DadDependencyGateRules.EvaluateParticipant(
            snapshot,
            nowUtc,
            dependencyStaleAfter,
            "AutoParty participant");
        dependencyBlocked = !dependency.Ready;
        return dependency.Ready;
    }

    private static bool TryValidateClaim(
        DadClaimDecisionDto? claim,
        DadClaimRequestDto request,
        DadParticipantSnapshot ready,
        DateTime nowUtc)
    {
        if (claim == null || !claim.Granted || claim.ClaimState != DadClaimState.Granted ||
            claim.LeaseState != DadParticipantLeaseState.Granted ||
            !SameIgnoreCase(claim.RunId, request.RunId) ||
            !SameIgnoreCase(claim.WorkerSessionId.Value, ready.WorkerSessionId.Value) ||
            !SameIgnoreCase(claim.CharacterKey.Value, request.RequiredCharacterKey.Value))
        {
            return false;
        }

        var lease = claim.Lease;
        return lease != null && lease.State == DadParticipantLeaseState.Granted &&
               SameIgnoreCase(lease.RunId, request.RunId) &&
               SameIgnoreCase(lease.SlotId, request.SlotId) &&
               SameIgnoreCase(lease.AssignedAccountKey.Value, request.RequiredAccountKey.Value) &&
               SameIgnoreCase(lease.AssignedCharacterKey.Value, request.RequiredCharacterKey.Value) &&
               SameIgnoreCase(lease.OwningWorkerSessionId.Value, ready.WorkerSessionId.Value) &&
               lease.ExpiresUtc > nowUtc;
    }

    private static bool TryResolveModuleId(EndpointExecutionPlan plan, out DadModuleId moduleId)
    {
        if (plan.FormationOnly)
        {
            moduleId = DadModuleId.None;
            return plan.Modules.IsDefaultOrEmpty;
        }
        if (plan.Modules.IsDefaultOrEmpty)
        {
            moduleId = DadModuleId.None;
            return false;
        }
        if (plan.Modules.Length > 1)
        {
            moduleId = DadModuleId.Mixed;
            return true;
        }
        return Enum.TryParse(plan.Modules[0].ModuleId, ignoreCase: false, out moduleId) &&
               moduleId != DadModuleId.None;
    }

    private static bool SameOrdinal(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static bool SameIgnoreCase(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();

    private sealed class TakeoverProposalState(
        Guid proposalId,
        string runId,
        IReadOnlyList<TakeoverSlotState> slots)
    {
        public Guid ProposalId { get; } = proposalId;
        public string RunId { get; } = runId;
        public IReadOnlyList<TakeoverSlotState> Slots { get; } = slots;
        public DateTime? ResetExecutionUtc { get; set; }
        public DateTime? RelogExecutionUtc { get; set; }
        public string TerminalBlocker { get; set; } = string.Empty;
    }

    private sealed record TakeoverSlotState(
        EndpointExecutionParticipant Participant,
        uint RequestedJobId,
        DadAutoPartyInboundRoute Route,
        DadWakeTakeoverRequestDto Request);
}
