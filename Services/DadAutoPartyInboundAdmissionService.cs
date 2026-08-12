using System.Collections.Immutable;
using System.Globalization;
using AutoParty.Contracts;
using dad.Models;

namespace dad.Services;

internal sealed record DadAutoPartyInboundAdmissionResult(
    string RunId,
    bool Ready,
    string SafeBlocker,
    ImmutableArray<string> OwnedSlotIds,
    ImmutableArray<DadNativePartyInviteTarget> InviteTargets)
{
    public static DadAutoPartyInboundAdmissionResult Blocked(string runId, string safeBlocker)
        => new(runId, false, safeBlocker, [], []);
}

internal sealed class DadAutoPartyInboundAdmissionService
{
    internal const string InvalidProposal = "dad-inbound-admission-invalid-proposal";
    internal const string ExpiredProposal = "dad-inbound-admission-proposal-expired";
    internal const string InvalidOwnedParticipants = "dad-inbound-admission-owned-participants-invalid";
    internal const string InvalidRequestedJob = "dad-inbound-admission-requested-job-invalid";
    internal const string FleetRouteMismatch = "dad-inbound-admission-fleet-route-mismatch";
    internal const string WorkerRouteMismatch = "dad-inbound-admission-worker-route-mismatch";
    internal const string WakeBlocked = "dad-inbound-admission-wake-blocked";
    internal const string ReadinessBlocked = "dad-inbound-admission-readiness-blocked";
    internal const string DependenciesBlocked = "dad-inbound-admission-dependencies-blocked";
    internal const string ClaimBlocked = "dad-inbound-admission-claim-blocked";

    private readonly string registeredOwnerId;
    private readonly string registeredIslandId;
    private readonly DadWorkerSessionId authorityWorkerSessionId;
    private readonly Func<DadAutoPartyFleetRow, IReadOnlyList<DadParticipantSnapshot>> resolveWorkerRoutes;
    private readonly Func<DadParticipantSnapshot, DadWakeRequestDto, DadParticipantReadyDto?> submitWake;
    private readonly Func<DadClaimRequestDto, DadParticipantSnapshot, TimeSpan, DadParticipantLeaseRecord?> issueLease;
    private readonly Func<DadParticipantSnapshot, DadClaimRequestDto, DadClaimDecisionDto?> submitClaim;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly TimeSpan dependencyStaleAfter;

    public DadAutoPartyInboundAdmissionService(
        string registeredOwnerId,
        string registeredIslandId,
        DadWorkerSessionId authorityWorkerSessionId,
        Func<DadAutoPartyFleetRow, IReadOnlyList<DadParticipantSnapshot>> resolveWorkerRoutes,
        Func<DadParticipantSnapshot, DadWakeRequestDto, DadParticipantReadyDto?> submitWake,
        Func<DadClaimRequestDto, DadParticipantSnapshot, TimeSpan, DadParticipantLeaseRecord?> issueLease,
        Func<DadParticipantSnapshot, DadClaimRequestDto, DadClaimDecisionDto?> submitClaim,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? dependencyStaleAfter = null)
    {
        this.registeredOwnerId = Normalize(registeredOwnerId);
        this.registeredIslandId = Normalize(registeredIslandId);
        this.authorityWorkerSessionId = authorityWorkerSessionId;
        this.resolveWorkerRoutes = resolveWorkerRoutes ?? throw new ArgumentNullException(nameof(resolveWorkerRoutes));
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
        IReadOnlyList<DadAutoPartyFleetRow> fleetRows)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(fleetRows);

        var plan = proposal.ExecutionPlan;
        var runId = plan?.RunId ?? string.Empty;
        var now = utcNow();
        if (plan == null || string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(registeredOwnerId) || string.IsNullOrWhiteSpace(registeredIslandId) ||
            authorityWorkerSessionId.IsEmpty || proposal.Participants.IsDefaultOrEmpty ||
            proposal.Participants.Length > 8 || plan.Participants.IsDefaultOrEmpty || plan.Participants.Length > 8 ||
            plan.LeaseDurationSeconds is < 3 or > 1800)
        {
            return DadAutoPartyInboundAdmissionResult.Blocked(runId, InvalidProposal);
        }
        if (proposal.Header.ExpiresAt <= now)
            return DadAutoPartyInboundAdmissionResult.Blocked(runId, ExpiredProposal);

        var participants = plan.Participants;
        if (participants.Any(participant =>
                SameOrdinal(participant.OwnerId.Value, registeredOwnerId) !=
                SameOrdinal(participant.OwnerIslandId.Value, registeredIslandId)))
        {
            return DadAutoPartyInboundAdmissionResult.Blocked(runId, InvalidOwnedParticipants);
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
            return DadAutoPartyInboundAdmissionResult.Blocked(runId, InvalidOwnedParticipants);
        }

        if (!TryResolveModuleId(plan, out var moduleId))
            return DadAutoPartyInboundAdmissionResult.Blocked(runId, InvalidProposal);

        var targets = ImmutableArray.CreateBuilder<DadNativePartyInviteTarget>(owned.Length);
        var slotIds = ImmutableArray.CreateBuilder<string>(owned.Length);
        foreach (var participant in owned)
        {
            if (!uint.TryParse(
                    participant.RequestedJob.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var requestedJobId) || requestedJobId == 0)
            {
                return DadAutoPartyInboundAdmissionResult.Blocked(runId, InvalidRequestedJob);
            }

            var matchingRows = fleetRows.Where(row =>
                    row != null && row.Enabled && !row.IsRemote &&
                    SameOrdinal(row.OpaqueCharacterId, participant.CharacterId.Value) &&
                    row.JobId == requestedJobId)
                .ToArray();
            if (matchingRows.Length != 1 ||
                string.IsNullOrWhiteSpace(matchingRows[0].AccountKey) ||
                string.IsNullOrWhiteSpace(matchingRows[0].CharacterKey))
            {
                return DadAutoPartyInboundAdmissionResult.Blocked(runId, FleetRouteMismatch);
            }

            var row = matchingRows[0];
            IReadOnlyList<DadParticipantSnapshot> routes;
            try
            {
                routes = resolveWorkerRoutes(row) ?? [];
            }
            catch
            {
                return DadAutoPartyInboundAdmissionResult.Blocked(runId, WorkerRouteMismatch);
            }
            if (routes.Count != 1 || !TryValidateRoute(routes[0], row, out var route))
                return DadAutoPartyInboundAdmissionResult.Blocked(runId, WorkerRouteMismatch);

            var wakeRequest = new DadWakeRequestDto
            {
                RunId = runId,
                AuthorityWorkerSessionId = authorityWorkerSessionId,
                AuthorityMode = DadAuthorityMode.ServerDad,
                ModuleId = moduleId,
                RequiredAccountKey = new DadAccountKey(row.AccountKey),
                RequiredCharacterKey = new DadCharacterKey(row.CharacterKey),
                RequiredContentId = route.Character.ContentId,
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
                return DadAutoPartyInboundAdmissionResult.Blocked(runId, WakeBlocked);
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
                return DadAutoPartyInboundAdmissionResult.Blocked(
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
                return DadAutoPartyInboundAdmissionResult.Blocked(runId, ClaimBlocked);
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
                return DadAutoPartyInboundAdmissionResult.Blocked(runId, ClaimBlocked);

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
            true,
            string.Empty,
            slotIds.MoveToImmutable(),
            targets.MoveToImmutable());
    }

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

    private static bool TryValidateRoute(
        DadParticipantSnapshot? candidate,
        DadAutoPartyFleetRow row,
        out DadParticipantSnapshot route)
    {
        route = candidate ?? new DadParticipantSnapshot();
        return candidate != null && candidate.Character != null && !candidate.WorkerSessionId.IsEmpty &&
               SameIgnoreCase(candidate.ManagedAccountKey.Value, row.AccountKey) &&
               SameIgnoreCase(candidate.ActiveCharacterKey.Value, row.CharacterKey) &&
               SameIgnoreCase(candidate.Character.CharacterKey, row.CharacterKey) &&
               candidate.Character.ContentId != 0 &&
               !string.IsNullOrWhiteSpace(candidate.Character.CharacterName) &&
               !string.IsNullOrWhiteSpace(candidate.Character.WorldName) &&
               SameIgnoreCase(
                   $"{candidate.Character.CharacterName}@{candidate.Character.WorldName}",
                   row.CharacterKey) &&
               candidate.Character.WorldId is > 0 and <= ushort.MaxValue;
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
}
