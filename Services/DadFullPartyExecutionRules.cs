using dad.Models;

namespace dad.Services;

public static class DadFullPartyExecutionRules
{
    public static DadAcquiredCharacter? ResolveActiveCoordinatorCharacter(
        DadAcquiredCharacter? unfilteredLocalRuntimeCharacter,
        IEnumerable<DadAcquiredCharacter> projectedCharacters)
    {
        ArgumentNullException.ThrowIfNull(projectedCharacters);

        if (unfilteredLocalRuntimeCharacter?.Source == DadCharacterSource.LocalRuntime)
            return unfilteredLocalRuntimeCharacter;

        return projectedCharacters.FirstOrDefault(static character =>
            character.Source == DadCharacterSource.LocalRuntime);
    }

    public static bool RequiresLocalCoordinatorLeader(DadRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.DailyMsq != null ||
               request.PremadeDuty != null ||
               request.Dungeon?.QueueViaLanParty == true;
    }

    public static bool TryValidatePlannedCoordinatorLeader(
        DadRunRequest request,
        DadAcquiredCharacter leader,
        out string blocker)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(leader);
        blocker = string.Empty;
        if (!RequiresLocalCoordinatorLeader(request))
            return true;

        if (request.Orchestration.AuthorityMode != DadAuthorityMode.ServerDad)
        {
            blocker = "Full-party queue leader requires ServerDad coordinator authority.";
            return false;
        }

        if (leader.Source != DadCharacterSource.LocalRuntime)
        {
            blocker = $"Full-party queue leader '{leader.CharacterKey}' must be the character loaded on this Dad Coordinator client (Slot1).";
            return false;
        }

        return true;
    }

    public static bool TryValidatePlannedCoordinatorLeader(
        DadRunRequest request,
        DadAcquiredCharacter leader,
        DadAccountKey coordinatorAccountKey,
        DadAcquiredCharacter? activeCoordinatorCharacter,
        bool requireExactLocalIdentity,
        bool allowWakeableCoordinatorLeader,
        out string blocker)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(leader);
        blocker = string.Empty;
        if (!RequiresLocalCoordinatorLeader(request))
            return true;

        if (request.Orchestration.AuthorityMode != DadAuthorityMode.ServerDad)
        {
            blocker = "Full-party queue leader requires ServerDad coordinator authority.";
            return false;
        }

        if (request.Orchestration.QueueAuthority != DadQueueAuthority.Leader)
        {
            blocker = $"Full-party queue leader requires leader queue authority; request has {request.Orchestration.QueueAuthority}.";
            return false;
        }

        var requestedLeaderKey = request.Orchestration.PreferredLeaderCharacterKey.IsEmpty
            ? new DadCharacterKey(leader.CharacterKey)
            : request.Orchestration.PreferredLeaderCharacterKey;
        if (requestedLeaderKey.IsEmpty ||
            !string.Equals(requestedLeaderKey.Value, leader.CharacterKey, StringComparison.OrdinalIgnoreCase))
        {
            blocker = $"Full-party Slot1 identity must be exact; requested '{requestedLeaderKey}', resolved '{leader.CharacterKey}'.";
            return false;
        }

        var requestedLeaderAccount = ResolveLeaderAccount(request, leader, requestedLeaderKey);
        if (coordinatorAccountKey.IsEmpty)
            coordinatorAccountKey = ResolveCharacterAccount(activeCoordinatorCharacter);
        if (coordinatorAccountKey.IsEmpty || requestedLeaderAccount.IsEmpty)
        {
            blocker = $"Full-party Slot1 '{requestedLeaderKey}' must have an exact coordinator account identity.";
            return false;
        }

        if (!DadRosterIdentity.SameAccount(requestedLeaderAccount, coordinatorAccountKey))
        {
            blocker = $"Full-party Slot1 '{requestedLeaderKey}' belongs to account '{requestedLeaderAccount}', not coordinator account '{coordinatorAccountKey}'.";
            return false;
        }

        var activeAccount = ResolveCharacterAccount(activeCoordinatorCharacter);
        if (!activeAccount.IsEmpty && !DadRosterIdentity.SameAccount(activeAccount, coordinatorAccountKey))
        {
            blocker = $"Dad Coordinator currently observes different account '{activeAccount}', not required coordinator account '{coordinatorAccountKey}'.";
            return false;
        }

        var exactLocalIdentity = leader.Source == DadCharacterSource.LocalRuntime &&
                                 activeCoordinatorCharacter != null &&
                                 string.Equals(activeCoordinatorCharacter.CharacterKey, requestedLeaderKey.Value, StringComparison.OrdinalIgnoreCase);
        if (!requireExactLocalIdentity && (exactLocalIdentity || allowWakeableCoordinatorLeader))
            return true;

        if (!exactLocalIdentity)
        {
            var activeCharacter = activeCoordinatorCharacter == null || string.IsNullOrWhiteSpace(activeCoordinatorCharacter.CharacterKey)
                ? "(none)"
                : activeCoordinatorCharacter.CharacterKey;
            blocker = $"Full-party queue leader '{requestedLeaderKey}' must be the exact character loaded on this Dad Coordinator client (Slot1); active '{activeCharacter}'.";
            return false;
        }

        return true;
    }

    public static IReadOnlyList<DadModuleBlockerDto> Evaluate(
        DadRunPlan plan,
        DadModuleId moduleId,
        IReadOnlyList<DadParticipantSnapshot> participants,
        int expectedPartySize,
        string laneDisplayName)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(participants);

        var displayName = string.IsNullOrWhiteSpace(laneDisplayName)
            ? moduleId.ToString()
            : laneDisplayName.Trim();
        var blockers = new List<DadModuleBlockerDto>();

        if (participants.Count != expectedPartySize)
        {
            blockers.Add(BuildBlocker(
                moduleId,
                "Participants",
                $"{displayName} requires exactly {expectedPartySize} Dad-verified participant(s), have {participants.Count}.",
                DadModuleBlockerSeverity.Failed));
        }

        var unverifiedParticipants = participants
            .Where(static participant => !IsDadVerifiedParticipant(participant))
            .Select(static participant => participant.ActiveCharacterKey.IsEmpty
                ? participant.AssignedSlotId
                : participant.ActiveCharacterKey.ToString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unverifiedParticipants.Count > 0)
        {
            blockers.Add(BuildBlocker(
                moduleId,
                "Participants",
                $"Full Dad participant readiness is not verified for: {string.Join(", ", unverifiedParticipants)}.",
                DadModuleBlockerSeverity.Blocked));
        }

        var duplicateCharacters = participants
            .Select(static participant => participant.ActiveCharacterKey.ToString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToList();
        if (duplicateCharacters.Count > 0)
        {
            blockers.Add(BuildBlocker(
                moduleId,
                "Participants",
                $"{displayName} participant list has duplicate character(s): {string.Join(", ", duplicateCharacters)}.",
                DadModuleBlockerSeverity.Blocked));
        }

        var localParticipants = participants.Where(static participant => participant.IsLocalClient).ToList();
        if (localParticipants.Count != 1)
        {
            blockers.Add(BuildBlocker(
                moduleId,
                "LeaderAuthority",
                $"{displayName} requires exactly one Dad Coordinator local client as the queue leader; found {localParticipants.Count}.",
                DadModuleBlockerSeverity.Blocked));
        }
        else
        {
            var localLeader = localParticipants[0];
            if (!localLeader.IsAuthority)
            {
                blockers.Add(BuildBlocker(
                    moduleId,
                    "LeaderAuthority",
                    $"Local client is not marked as Dad Coordinator authority for this {displayName} queue.",
                    DadModuleBlockerSeverity.Blocked));
            }

            if (localLeader.Character?.Source != DadCharacterSource.LocalRuntime)
            {
                blockers.Add(BuildBlocker(
                    moduleId,
                    "LeaderAuthority",
                    $"{displayName} queue leader must be the character loaded on this Dad Coordinator client.",
                    DadModuleBlockerSeverity.Blocked));
            }

            if (!string.Equals(localLeader.AssignedSlotId, DadPlannerSlotRules.LeaderSlotId, StringComparison.OrdinalIgnoreCase))
            {
                blockers.Add(BuildBlocker(
                    moduleId,
                    "LeaderAuthority",
                    $"Local client is assigned to '{localLeader.AssignedSlotId}', not Slot1.",
                    DadModuleBlockerSeverity.Blocked));
            }

            if (!string.IsNullOrWhiteSpace(plan.LeaderCharacterKey) &&
                !string.Equals(localLeader.ActiveCharacterKey.ToString(), plan.LeaderCharacterKey, StringComparison.OrdinalIgnoreCase))
            {
                blockers.Add(BuildBlocker(
                    moduleId,
                    "LeaderAuthority",
                    $"Local leader mismatch: need {plan.LeaderCharacterKey}, active {localLeader.ActiveCharacterKey}.",
                    DadModuleBlockerSeverity.Blocked));
            }
        }

        if (plan.Orchestration.AuthorityMode != DadAuthorityMode.ServerDad)
        {
            blockers.Add(BuildBlocker(
                moduleId,
                "LeaderAuthority",
                $"{displayName} requires Dad Coordinator authority, not local-only authority.",
                DadModuleBlockerSeverity.Blocked));
        }

        if (plan.Orchestration.QueueAuthority is not (DadQueueAuthority.Leader or DadQueueAuthority.LanParty))
        {
            blockers.Add(BuildBlocker(
                moduleId,
                "QueueAuthority",
                $"{displayName} requires leader/LAN-party queue authority; current authority is {plan.Orchestration.QueueAuthority}.",
                DadModuleBlockerSeverity.Blocked));
        }

        return blockers;
    }

    public static bool IsDadVerifiedParticipant(DadParticipantSnapshot participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (participant.ActiveCharacterKey.IsEmpty ||
            !participant.IsAvailable ||
            !participant.IsEligibleForRun ||
            !participant.PostArReady ||
            participant.ClaimState != DadClaimState.Granted ||
            participant.LeaseState != DadParticipantLeaseState.Granted)
        {
            return false;
        }

        return participant.State is not (DadParticipantState.WaitingForRequiredCharacter
            or DadParticipantState.WaitingForPostArReady
            or DadParticipantState.Failed
            or DadParticipantState.Cancelled
            or DadParticipantState.Stale);
    }

    private static DadModuleBlockerDto BuildBlocker(
        DadModuleId moduleId,
        string capability,
        string summary,
        DadModuleBlockerSeverity severity)
        => new()
        {
            ModuleId = moduleId,
            Capability = capability,
            Severity = severity,
            Summary = summary,
        };

    private static DadAccountKey ResolveLeaderAccount(
        DadRunRequest request,
        DadAcquiredCharacter leader,
        DadCharacterKey requestedLeaderKey)
    {
        var direct = ResolveCharacterAccount(leader);
        if (!direct.IsEmpty)
            return direct;

        return request.Orchestration.RequiredRosterCharacters
                   .FirstOrDefault(reference =>
                       !reference.AccountKey.IsEmpty &&
                       !reference.CharacterKey.IsEmpty &&
                       string.Equals(reference.CharacterKey.Value, requestedLeaderKey.Value, StringComparison.OrdinalIgnoreCase))
                   ?.AccountKey
               ?? new DadAccountKey(string.Empty);
    }

    private static DadAccountKey ResolveCharacterAccount(DadAcquiredCharacter? character)
        => character == null
            ? new DadAccountKey(string.Empty)
            : DadRosterIdentity.ResolveAccountKey(character.AccountId, character.AccountAlias);
}
