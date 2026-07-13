using dad.Models;

namespace dad.Services;

internal static class DadCoordinatorMutationBoundaryRules
{
    public static bool TryResolveStrictParticipants(
        DadRunPlan plan,
        DadRunSlotManifest manifest,
        IReadOnlyList<DadParticipantSnapshot> currentParticipants,
        DadAccountKey coordinatorAccountKey,
        DadAcquiredCharacter? activeCoordinatorCharacter,
        out List<DadParticipantSnapshot> resolvedParticipants,
        out string blocker)
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

        var resolutionBlockers = new List<string>();
        foreach (var slot in manifest.Slots)
        {
            var participant = DadRunSlotManifestRules.ResolveSlot(
                slot,
                currentParticipants,
                plan.Orchestration.RequirePostArReady,
                out var slotBlocker);
            participant.IsAuthority = participant.IsLocalClient &&
                                      slot.IsLeader &&
                                      plan.Orchestration.AuthorityMode is DadAuthorityMode.ServerDad or DadAuthorityMode.LocalOnly;
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

        if (!DadFullPartyExecutionRules.RequiresLocalCoordinatorLeader(plan.Request))
            return true;

        var leaderSlots = manifest.Slots.Where(static slot => slot.IsLeader).ToList();
        if (leaderSlots.Count != 1 ||
            !Same(leaderSlots[0].SlotId, DadPlannerSlotRules.LeaderSlotId))
        {
            return Fail("Strict coordinator mutation validation requires exactly one frozen Slot1 leader.", out blocker);
        }

        var leaderSlot = leaderSlots[0];
        var localParticipants = resolvedParticipants.Where(static participant => participant.IsLocalClient).ToList();
        if (localParticipants.Count != 1)
        {
            return Fail(
                $"Strict coordinator mutation validation requires exactly one local participant; found {localParticipants.Count}.",
                out blocker);
        }

        var localLeader = localParticipants[0];
        if (!Same(localLeader.AssignedSlotId, leaderSlot.SlotId) ||
            !Same(localLeader.WorkerSessionId.Value, leaderSlot.WorkerSessionId.Value) ||
            !localLeader.IsAuthority)
        {
            return Fail(
                $"Strict coordinator mutation validation requires frozen {leaderSlot.SlotId} '{leaderSlot.CharacterKey}' on its exact local authority worker session '{leaderSlot.WorkerSessionId}'.",
                out blocker);
        }

        var activeAccount = ResolveAccount(activeCoordinatorCharacter);
        var rawIdentityIsExact = activeCoordinatorCharacter?.Source == DadCharacterSource.LocalRuntime &&
                                 Same(activeCoordinatorCharacter.CharacterKey, leaderSlot.CharacterKey.Value) &&
                                 activeCoordinatorCharacter.ContentId == leaderSlot.ContentId &&
                                 !activeAccount.IsEmpty &&
                                 DadRosterIdentity.SameAccount(activeAccount, leaderSlot.AccountKey);
        var plannedLeader = new DadAcquiredCharacter
        {
            AccountId = leaderSlot.AccountKey.Value,
            CharacterKey = leaderSlot.CharacterKey.Value,
            ContentId = leaderSlot.ContentId,
            Source = rawIdentityIsExact ? DadCharacterSource.LocalRuntime : DadCharacterSource.XadbOnly,
        };

        return DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(
            plan.Request,
            plannedLeader,
            coordinatorAccountKey,
            activeCoordinatorCharacter,
            requireExactLocalIdentity: true,
            allowWakeableCoordinatorLeader: false,
            out blocker);
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
