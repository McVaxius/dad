using dad.Models;

namespace dad.Services;

internal static class DadCoordinatorMutationBoundaryRules
{
    public static bool TryResolveStrictParticipants(
        DadRunPlan plan,
        DadRunSlotManifest manifest,
        IReadOnlyList<DadParticipantSnapshot> currentParticipants,
        DadAccountKey coordinatorAccountKey,
        DadParticipantSnapshot? liveCoordinatorTruth,
        out List<DadParticipantSnapshot> resolvedParticipants,
        out string blocker,
        Func<Guid, DadFrozenRunSlot, DadParticipantSnapshot?>? registeredIslandResolver = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(currentParticipants);

        resolvedParticipants = [];
        blocker = string.Empty;

        if (plan.Request == null || plan.Orchestration == null)
            return Fail("Strict coordinator mutation validation is missing its plan request or orchestration intent.", out blocker);

        if (!Same(plan.Request.RequestId, manifest.RequestId) ||
            manifest.ExpectedPartySize != plan.RequiredParticipantCount ||
            !Same(plan.LeaderCharacterKey, manifest.LeaderCharacterKey) ||
            manifest.Slots.Count != plan.RequiredParticipantCount)
        {
            return Fail("The frozen participant manifest no longer matches the accepted coordinator plan.", out blocker);
        }

        var hasRegisteredIslandSlots = manifest.Slots.Any(static slot =>
            slot.RouteKind == DadRunSlotRouteKind.RegisteredIsland);
        var proposalId = Guid.Empty;
        if (hasRegisteredIslandSlots &&
            (!Guid.TryParse(plan.Orchestration.AutoPartyProposalId, out proposalId) ||
             proposalId == Guid.Empty))
        {
            return Fail(
                "Strict coordinator mutation validation is missing its runtime AutoParty proposal binding.",
                out blocker);
        }

        var resolutionBlockers = new List<string>();
        foreach (var slot in manifest.Slots)
        {
            DadParticipantSnapshot participant;
            string slotBlocker;
            if (slot.RouteKind == DadRunSlotRouteKind.RegisteredIsland)
            {
                participant = registeredIslandResolver?.Invoke(proposalId, slot) ?? new DadParticipantSnapshot
                {
                    AssignedSlotId = slot.SlotId,
                    RegisteredIslandId = slot.IslandId,
                    State = DadParticipantState.Stale,
                    StatusText = $"{slot.SlotId} is waiting for its exact AutoParty runtime lease.",
                };
                slotBlocker = ValidateRegisteredIslandParticipant(
                    plan,
                    proposalId,
                    slot,
                    participant,
                    DateTime.UtcNow);
            }
            else
            {
                participant = DadRunSlotManifestRules.ResolveSlot(
                    slot,
                    currentParticipants,
                    plan.Orchestration.RequirePostArReady,
                    out slotBlocker);
            }
            resolvedParticipants.Add(participant);

            if (!string.IsNullOrWhiteSpace(slotBlocker))
                resolutionBlockers.Add(slotBlocker);
            else if (!participant.WorldReadyStable)
                resolutionBlockers.Add($"{slot.SlotId} exact character '{slot.CharacterKey}' is not world-ready-stable at the coordinator mutation boundary.");
        }

        if (resolutionBlockers.Count > 0)
        {
            blocker = string.Join(" | ", resolutionBlockers.Distinct(StringComparer.OrdinalIgnoreCase));
            return false;
        }

        if (!DadFullPartyExecutionRules.RequiresFrozenSlot1Authority(plan.Request))
            return true;

        var leaderSlots = manifest.Slots.Where(static slot => slot.IsLeader).ToList();
        var inviterSlots = manifest.Slots.Where(static slot => slot.IsInviter).ToList();
        if (leaderSlots.Count != 1 ||
            inviterSlots.Count != 1 ||
            !Same(leaderSlots[0].SlotId, DadPlannerSlotRules.LeaderSlotId) ||
            !Same(inviterSlots[0].SlotId, DadPlannerSlotRules.LeaderSlotId) ||
            !Same(leaderSlots[0].CharacterKey.Value, inviterSlots[0].CharacterKey.Value))
        {
            return Fail("Strict coordinator mutation validation requires exactly one frozen Slot1 leader and inviter.", out blocker);
        }

        var leaderSlot = leaderSlots[0];
        var slotOneParticipants = resolvedParticipants.Where(participant =>
            Same(participant.AssignedSlotId, leaderSlot.SlotId) &&
            (leaderSlot.RouteKind == DadRunSlotRouteKind.RegisteredIsland ||
             Same(participant.WorkerSessionId.Value, leaderSlot.WorkerSessionId.Value))).ToList();
        if (slotOneParticipants.Count != 1)
        {
            return Fail(
                $"Strict coordinator mutation validation requires exactly one exact frozen Slot1 worker; found {slotOneParticipants.Count}.",
                out blocker);
        }

        var slotOne = slotOneParticipants[0];
        if (leaderSlot.RouteKind == DadRunSlotRouteKind.RegisteredIsland)
        {
            blocker = ValidateRegisteredIslandParticipant(
                plan,
                proposalId,
                leaderSlot,
                slotOne,
                DateTime.UtcNow);
            return string.IsNullOrWhiteSpace(blocker);
        }

        if (!DadRosterIdentity.SameAccount(slotOne.ManagedAccountKey, leaderSlot.AccountKey) ||
            !Same(slotOne.ActiveCharacterKey.Value, leaderSlot.CharacterKey.Value) ||
            slotOne.Character.ContentId != leaderSlot.ContentId ||
            !slotOne.WorldReadyStable)
        {
            return Fail(
                $"Strict coordinator mutation validation requires frozen {leaderSlot.SlotId} '{leaderSlot.CharacterKey}' Content ID {leaderSlot.ContentId} on exact worker '{leaderSlot.WorkerSessionId}'.",
                out blocker);
        }

        var plannedLeader = new DadAcquiredCharacter
        {
            AccountId = leaderSlot.AccountKey.Value,
            CharacterKey = leaderSlot.CharacterKey.Value,
            ContentId = leaderSlot.ContentId,
            Source = slotOne.Character.Source,
            Freshness = slotOne.Character.Freshness,
            Readiness = slotOne.Character.Readiness,
        };

        return DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(
            plan.Request,
            plannedLeader,
            new DadAccountKey(string.Empty),
            null,
            requireExactLocalIdentity: true,
            allowWakeableCoordinatorLeader: false,
            out blocker);
    }

    private static string ValidateRegisteredIslandParticipant(
        DadRunPlan plan,
        Guid proposalId,
        DadFrozenRunSlot slot,
        DadParticipantSnapshot participant,
        DateTime nowUtc)
    {
        var expectedWorker = $"autoparty-{proposalId:N}-{slot.SlotId.ToLowerInvariant()}";
        if (slot.RouteKind != DadRunSlotRouteKind.RegisteredIsland ||
            !Same(participant.AssignedSlotId, slot.SlotId) ||
            !string.Equals(participant.RegisteredIslandId, slot.IslandId, StringComparison.Ordinal) ||
            !Same(participant.WorkerSessionId.Value, expectedWorker) ||
            !string.Equals(participant.RunId, plan.Request.RequestId, StringComparison.Ordinal) ||
            participant.State == DadParticipantState.Stale ||
            !participant.IsAvailable ||
            !participant.IsEligibleForRun ||
            !participant.PostArReady ||
            !participant.WorldReadyStable ||
            !participant.Dependencies.IsReady ||
            participant.ClaimState != DadClaimState.Granted ||
            participant.LeaseState != DadParticipantLeaseState.Granted ||
            participant.LeaseExpiresUtc is not { } leaseExpiresUtc ||
            leaseExpiresUtc <= nowUtc)
        {
            return $"{slot.SlotId} is waiting for its exact active registered-island proposal lease.";
        }

        return string.Empty;
    }

    private static DadAccountKey ResolveAccount(DadAcquiredCharacter? character)
        => character == null
            ? new DadAccountKey(string.Empty)
            : DadRosterIdentity.ResolveAccountKey(character.AccountId, character.AccountAlias);

    private static bool Same(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim();

    private static bool Fail(string reason, out string blocker)
    {
        blocker = reason;
        return false;
    }
}
