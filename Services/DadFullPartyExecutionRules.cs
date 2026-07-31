using dad.Models;

namespace dad.Services;

public static class DadFullPartyExecutionRules
{
    public static DadAcquiredCharacter? ResolveActiveCoordinatorCharacter(
        DadParticipantSnapshot? liveCoordinatorTruth)
    {
        return TryResolveActiveCoordinatorCharacter(liveCoordinatorTruth, out var character, out _)
            ? character
            : null;
    }

    public static bool TryResolveActiveCoordinatorCharacter(
        DadParticipantSnapshot? liveCoordinatorTruth,
        out DadAcquiredCharacter character,
        out string blocker)
    {
        character = new DadAcquiredCharacter();
        blocker = string.Empty;
        if (liveCoordinatorTruth == null)
            return FailCoordinatorTruth("Explicit live coordinator truth is unavailable.", out blocker);

        if (!liveCoordinatorTruth.IsLocalClient ||
            liveCoordinatorTruth.WorkerSessionId.IsEmpty ||
            string.IsNullOrWhiteSpace(liveCoordinatorTruth.ClientInstanceId))
        {
            return FailCoordinatorTruth(
                "Explicit coordinator truth is not bound to an exact local worker/client session.",
                out blocker);
        }

        var liveCharacter = liveCoordinatorTruth.Character;
        if (!liveCoordinatorTruth.IsAvailable ||
            liveCharacter.Source != DadCharacterSource.LocalRuntime ||
            liveCoordinatorTruth.ActiveCharacterKey.IsEmpty ||
            string.IsNullOrWhiteSpace(liveCharacter.CharacterKey) ||
            liveCharacter.ContentId == 0 ||
            !string.Equals(
                liveCoordinatorTruth.ActiveCharacterKey.Value,
                liveCharacter.CharacterKey,
                StringComparison.OrdinalIgnoreCase))
        {
            return FailCoordinatorTruth(
                "Explicit live coordinator truth does not contain one exact loaded local character and Content ID.",
                out blocker);
        }

        if (liveCoordinatorTruth.ManagedAccountKey.IsEmpty)
            return FailCoordinatorTruth("Explicit live coordinator truth has no managed account identity.", out blocker);

        var liveAccount = ResolveCharacterAccount(liveCharacter);
        if (!liveAccount.IsEmpty &&
            !DadRosterIdentity.SameAccount(liveAccount, liveCoordinatorTruth.ManagedAccountKey))
        {
            return FailCoordinatorTruth(
                $"Explicit live coordinator character account '{liveAccount}' conflicts with managed account '{liveCoordinatorTruth.ManagedAccountKey}'.",
                out blocker);
        }

        character = liveCharacter.Clone();
        if (liveAccount.IsEmpty)
            character.AccountId = liveCoordinatorTruth.ManagedAccountKey.Value;
        return true;
    }

    private static bool FailCoordinatorTruth(string reason, out string blocker)
    {
        blocker = reason;
        return false;
    }

    public static bool RequiresLocalCoordinatorLeader(DadRunRequest request)
        => RequiresFrozenSlot1Authority(request);

    public static bool RequiresFrozenSlot1Authority(DadRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Orchestration.AutoPartyFormationOnly ||
               request.Orchestration.RosterIntent.ExpectedPartySize > 1 ||
               request.DailyMsq != null ||
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

        if (request.Orchestration.QueueAuthority != DadQueueAuthority.Leader)
        {
            blocker = $"Full-party Slot1 requires leader queue authority; request has {request.Orchestration.QueueAuthority}.";
            return false;
        }

        if (!TryResolveFirstPrimaryRoster(request, out var slotOne, out blocker))
            return false;
        if (!MatchesFrozenCharacter(slotOne, leader))
        {
            blocker =
                $"Full-party Slot1 must be exact: planned '{slotOne.CharacterKey}' Content ID {slotOne.ContentId}, " +
                $"resolved '{leader.CharacterKey}' Content ID {leader.ContentId}.";
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
        if (!RequiresFrozenSlot1Authority(request))
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

        if (!TryResolveFirstPrimaryRoster(request, out var slotOne, out blocker))
            return false;

        var requestedLeaderKey = request.Orchestration.PreferredLeaderCharacterKey.IsEmpty
            ? slotOne.CharacterKey
            : request.Orchestration.PreferredLeaderCharacterKey;
        if (requestedLeaderKey.IsEmpty ||
            !DadRosterIdentity.SameCharacter(
                requestedLeaderKey,
                slotOne.ContentId,
                slotOne.CharacterKey,
                slotOne.ContentId) ||
            !MatchesFrozenCharacter(slotOne, leader))
        {
            blocker =
                $"Full-party Slot1 identity must be exact; frozen '{slotOne.CharacterKey}' Content ID {slotOne.ContentId}, " +
                $"requested '{requestedLeaderKey}', resolved '{leader.CharacterKey}' Content ID {leader.ContentId}.";
            return false;
        }

        var requestedLeaderAccount = ResolveCharacterAccount(leader);
        if (requestedLeaderAccount.IsEmpty)
            requestedLeaderAccount = slotOne.AccountKey;
        if (requestedLeaderAccount.IsEmpty ||
            !DadRosterIdentity.SameAccount(requestedLeaderAccount, slotOne.AccountKey))
        {
            blocker =
                $"Full-party Slot1 '{requestedLeaderKey}' must belong to exact frozen account '{slotOne.AccountKey}', " +
                $"not '{requestedLeaderAccount}'.";
            return false;
        }

        if (requireExactLocalIdentity &&
            (leader.Source is not (DadCharacterSource.LocalRuntime or DadCharacterSource.PeerRuntime) ||
             leader.Freshness is not (DadSnapshotFreshness.Live or DadSnapshotFreshness.Recent) ||
             leader.Readiness != DadReadinessState.Ready))
        {
            blocker = $"Full-party Slot1 '{requestedLeaderKey}' is not live and ready on its exact Dad worker.";
            return false;
        }

        return true;
    }

    public static bool IsQueueAuthorityLocal(
        DadRunPlan plan,
        DadParticipantSnapshot? liveCoordinatorTruth)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.RequiredParticipantCount <= 1)
            return true;
        if (liveCoordinatorTruth == null ||
            liveCoordinatorTruth.WorkerSessionId.IsEmpty ||
            liveCoordinatorTruth.ActiveCharacterKey.IsEmpty ||
            liveCoordinatorTruth.Character.ContentId == 0)
        {
            return false;
        }

        var slotOne = plan.Orchestration.RequiredRosterCharacters?.FirstOrDefault();
        return slotOne != null &&
               DadRosterIdentity.SameAccount(slotOne.AccountKey, liveCoordinatorTruth.ManagedAccountKey) &&
               DadRosterIdentity.SameCharacter(
                   slotOne.CharacterKey,
                   slotOne.ContentId,
                   liveCoordinatorTruth.ActiveCharacterKey,
                   liveCoordinatorTruth.Character.ContentId);
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

        var slotOneParticipants = participants
            .Where(static participant => string.Equals(
                participant.AssignedSlotId,
                DadPlannerSlotRules.LeaderSlotId,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (slotOneParticipants.Count != 1)
        {
            blockers.Add(BuildBlocker(
                moduleId,
                "LeaderAuthority",
                $"{displayName} requires exactly one frozen Slot1 queue authority; found {slotOneParticipants.Count}.",
                DadModuleBlockerSeverity.Blocked));
        }
        else
        {
            var slotOneLeader = slotOneParticipants[0];

            if (!string.IsNullOrWhiteSpace(plan.LeaderCharacterKey) &&
                !string.Equals(slotOneLeader.ActiveCharacterKey.ToString(), plan.LeaderCharacterKey, StringComparison.OrdinalIgnoreCase))
            {
                blockers.Add(BuildBlocker(
                    moduleId,
                    "LeaderAuthority",
                    $"Slot1 leader mismatch: need {plan.LeaderCharacterKey}, active {slotOneLeader.ActiveCharacterKey}.",
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

    private static bool TryResolveFirstPrimaryRoster(
        DadRunRequest request,
        out DadRosterCharacterRef slotOne,
        out string blocker)
    {
        slotOne = request.Orchestration.RequiredRosterCharacters?.FirstOrDefault() ?? new DadRosterCharacterRef();
        if (slotOne.AccountKey.IsEmpty || slotOne.CharacterKey.IsEmpty || slotOne.ContentId == 0)
        {
            blocker = "Full-party Slot1 requires an exact frozen account, character, and non-zero Content ID.";
            return false;
        }

        blocker = string.Empty;
        return true;
    }

    private static bool MatchesFrozenCharacter(
        DadRosterCharacterRef slot,
        DadAcquiredCharacter character)
    {
        var account = ResolveCharacterAccount(character);
        return !account.IsEmpty &&
               DadRosterIdentity.SameAccount(slot.AccountKey, account) &&
               DadRosterIdentity.SameCharacter(
                   slot.CharacterKey,
                   slot.ContentId,
                   new DadCharacterKey(character.CharacterKey),
                   character.ContentId);
    }

    private static DadAccountKey ResolveCharacterAccount(DadAcquiredCharacter? character)
        => character == null
            ? new DadAccountKey(string.Empty)
            : DadRosterIdentity.ResolveAccountKey(character.AccountId, character.AccountAlias);
}
