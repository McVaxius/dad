using dad.Models;

namespace dad.Services;

public enum DadPartyMembershipDisposition
{
    Ready,
    Wait,
    Reject,
}

public readonly record struct DadPartyMembershipDecision(
    DadPartyMembershipDisposition Disposition,
    string Summary);

internal static class DadPartySnapshotSourceRules
{
    public static IReadOnlyList<DadPartyMemberSnapshot> Read(
        bool crossRealmPartyActive,
        Func<IReadOnlyList<DadPartyMemberSnapshot>> readPartyList,
        Func<IReadOnlyList<DadPartyMemberSnapshot>> readCrossRealmParty)
        => crossRealmPartyActive ? readCrossRealmParty() : readPartyList();
}

public sealed class DadPartyAssemblyService
{
    public List<DadAssemblyInstructionDto> BuildInstructions(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants, out string blocker)
        => BuildInstructionsCore(
            plan,
            participants,
            manifest: null,
            participants.FirstOrDefault(static participant => participant.IsAuthority)?.WorkerSessionId
            ?? new DadWorkerSessionId(string.Empty),
            out blocker);

    internal List<DadAssemblyInstructionDto> BuildInstructions(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants,
        DadRunSlotManifest manifest,
        DadWorkerSessionId authorityWorkerSessionId,
        out string blocker)
        => BuildInstructionsCore(
            plan,
            participants,
            manifest,
            authorityWorkerSessionId,
            out blocker);

    private static List<DadAssemblyInstructionDto> BuildInstructionsCore(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants,
        DadRunSlotManifest? manifest,
        DadWorkerSessionId authorityWorkerSessionId,
        out string blocker)
    {
        blocker = string.Empty;
        var instructions = new List<DadAssemblyInstructionDto>();

        if (participants.Count < plan.RequiredParticipantCount)
        {
            blocker = $"Need {plan.RequiredParticipantCount} participant(s), have {participants.Count}.";
            return instructions;
        }

        if (plan.Orchestration.QueueAuthority != DadQueueAuthority.Leader)
        {
            blocker = $"Party assembly requires leader queue authority; request has {plan.Orchestration.QueueAuthority}.";
            return instructions;
        }

        var ordered = manifest == null
            ? OrderParticipantsForParty(plan, participants)
            : OrderFrozenParticipants(manifest, participants, out blocker);
        if (!string.IsNullOrWhiteSpace(blocker))
            return [];
        if (!string.IsNullOrWhiteSpace(plan.LeaderCharacterKey) &&
            ordered.Count > 0 &&
            !string.Equals(ordered[0].ActiveCharacterKey, plan.LeaderCharacterKey, StringComparison.OrdinalIgnoreCase))
        {
            blocker = $"Configured leader {plan.LeaderCharacterKey} is not online on Slot1.";
            return [];
        }

        var frozenInviter = new DadExpectedPartyInviter();
        var inviteTargets = new List<DadNativePartyInviteTarget>();
        if (manifest != null &&
            !TryBuildFrozenPartyAuthority(
                plan,
                manifest,
                ordered,
                out frozenInviter,
                out inviteTargets,
                out blocker))
        {
            return [];
        }

        for (var index = 0; index < ordered.Count; index++)
        {
            var participant = ordered[index];
            if (!participant.PostArReady)
            {
                blocker = $"{participant.Character.CharacterKey} is not post-AR ready.";
                return [];
            }

            instructions.Add(new DadAssemblyInstructionDto
            {
                RunId = plan.Request.RequestId,
                AuthorityWorkerSessionId = authorityWorkerSessionId,
                ModuleId = plan.CompositeModuleId,
                SlotId = string.IsNullOrWhiteSpace(participant.AssignedSlotId)
                    ? DadPlannerSlotRules.FormatSlotId(index + 1)
                    : participant.AssignedSlotId,
                RequiredCharacterKey = participant.Character.CharacterKey,
                InstructionKind = index == 0 ? DadAssemblyInstructionKind.FormParty : DadAssemblyInstructionKind.JoinParty,
                FrozenInviter = frozenInviter.Clone(),
                InviteTargets = inviteTargets.Select(static target => target.Clone()).ToList(),
                Summary = index == 0
                    ? $"Slot1 leader confirmed on {participant.Character.CharacterKey}; form Dad party."
                    : $"Join Slot1's Dad party on {participant.Character.CharacterKey}.",
            });
        }

        return instructions;
    }

    internal DadAssemblyInstructionDto? BuildDisbandInstruction(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants,
        DadRunSlotManifest manifest,
        DadWorkerSessionId authorityWorkerSessionId,
        out string blocker)
    {
        var form = BuildInstructions(
                plan,
                participants,
                manifest,
                authorityWorkerSessionId,
                out blocker)
            .SingleOrDefault(static instruction =>
                instruction.InstructionKind == DadAssemblyInstructionKind.FormParty);
        if (form == null)
            return null;

        form.InstructionKind = DadAssemblyInstructionKind.DisbandParty;
        form.Summary = "Slot1 is performing guarded Dad party teardown.";
        return form;
    }

    public bool VerifyPartyMembership(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants,
        IReadOnlyList<DadPartyMemberSnapshot> partyMembers,
        out string blocker)
    {
        var decision = EvaluatePartyMembership(plan, participants, partyMembers);
        blocker = decision.Disposition == DadPartyMembershipDisposition.Ready
            ? string.Empty
            : decision.Summary;
        return decision.Disposition == DadPartyMembershipDisposition.Ready;
    }

    public DadPartyMembershipDecision EvaluatePartyMembership(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants,
        IReadOnlyList<DadPartyMemberSnapshot> partyMembers)
    {
        if (plan.RequiredParticipantCount <= 1)
            return new DadPartyMembershipDecision(DadPartyMembershipDisposition.Ready, "Single-participant run needs no PartyList proof.");

        var expectedContentIds = participants
            .Select(static participant => participant.Character.ContentId)
            .Where(static contentId => contentId != 0)
            .ToHashSet();
        if (expectedContentIds.Count != plan.RequiredParticipantCount)
        {
            return new DadPartyMembershipDecision(
                DadPartyMembershipDisposition.Reject,
                $"Frozen party manifest has {expectedContentIds.Count}/{plan.RequiredParticipantCount} exact nonzero Content IDs.");
        }

        if (partyMembers.Count > plan.RequiredParticipantCount)
        {
            return new DadPartyMembershipDecision(
                DadPartyMembershipDisposition.Reject,
                $"PartyList contains {partyMembers.Count} members, exceeding frozen party size {plan.RequiredParticipantCount}.");
        }

        var unexpected = partyMembers
            .Where(static member => member.ContentId != 0)
            .Where(member => !expectedContentIds.Contains(member.ContentId))
            .Select(static member => $"{member.CharacterKey}#{member.ContentId}")
            .ToList();
        if (unexpected.Count > 0)
        {
            return new DadPartyMembershipDecision(
                DadPartyMembershipDisposition.Reject,
                $"PartyList contains unexpected frozen-member contradiction(s): {string.Join(", ", unexpected)}.");
        }

        var observedContentIds = partyMembers
            .Select(static member => member.ContentId)
            .Where(static contentId => contentId != 0)
            .ToHashSet();
        var missingIds = expectedContentIds.Except(observedContentIds).ToList();
        if (partyMembers.Count < plan.RequiredParticipantCount || missingIds.Count > 0)
        {
            return new DadPartyMembershipDecision(
                DadPartyMembershipDisposition.Wait,
                $"Waiting for exact PartyList Content IDs: {observedContentIds.Count}/{plan.RequiredParticipantCount} proven; missing {string.Join(",", missingIds)}.");
        }

        return new DadPartyMembershipDecision(
            DadPartyMembershipDisposition.Ready,
            $"PartyList proves exact frozen membership {observedContentIds.Count}/{plan.RequiredParticipantCount}.");
    }

    private static List<DadParticipantSnapshot> OrderParticipantsForParty(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var leaderKey = plan.LeaderCharacterKey?.Trim() ?? string.Empty;
        return participants
            .OrderByDescending(participant => !string.IsNullOrWhiteSpace(leaderKey) &&
                                               string.Equals(participant.ActiveCharacterKey, leaderKey, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static participant => participant.IsAuthority)
            .ThenByDescending(static participant => participant.IsLocalClient)
            .ThenBy(static participant => DadPlannerSlotRules.GetSlotSortKey(participant.AssignedSlotId))
            .ThenBy(static participant => participant.AssignedSlotId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.Character.CharacterKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<DadParticipantSnapshot> OrderFrozenParticipants(
        DadRunSlotManifest manifest,
        IReadOnlyList<DadParticipantSnapshot> participants,
        out string blocker)
    {
        blocker = string.Empty;
        var ordered = new List<DadParticipantSnapshot>(manifest.Slots.Count);
        foreach (var slot in manifest.Slots.OrderBy(static slot => DadPlannerSlotRules.GetSlotSortKey(slot.SlotId)))
        {
            var matches = participants.Where(participant =>
                    string.Equals(participant.AssignedSlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        participant.WorkerSessionId.Value,
                        slot.WorkerSessionId.Value,
                        StringComparison.OrdinalIgnoreCase) &&
                    DadRosterIdentity.SameAccount(participant.ManagedAccountKey, slot.AccountKey) &&
                    DadRosterIdentity.SameCharacter(
                        participant.ActiveCharacterKey,
                        participant.Character.ContentId,
                        slot.CharacterKey,
                        slot.ContentId))
                .ToList();
            if (matches.Count != 1)
            {
                blocker =
                    $"{slot.SlotId} must resolve to one exact frozen worker/account/character/Content-ID row; found {matches.Count}.";
                return [];
            }

            ordered.Add(matches[0]);
        }

        return ordered;
    }

    private static bool TryBuildFrozenPartyAuthority(
        DadRunPlan plan,
        DadRunSlotManifest manifest,
        IReadOnlyList<DadParticipantSnapshot> ordered,
        out DadExpectedPartyInviter inviter,
        out List<DadNativePartyInviteTarget> inviteTargets,
        out string blocker)
    {
        inviter = new DadExpectedPartyInviter();
        inviteTargets = [];
        blocker = string.Empty;
        var slotOne = manifest.Slots.SingleOrDefault(static slot =>
            slot.IsLeader &&
            slot.IsInviter &&
            string.Equals(slot.SlotId, DadPlannerSlotRules.LeaderSlotId, StringComparison.OrdinalIgnoreCase));
        if (slotOne == null ||
            manifest.Slots.Count(static slot => slot.IsLeader) != 1 ||
            manifest.Slots.Count(static slot => slot.IsInviter) != 1 ||
            !string.Equals(manifest.LeaderCharacterKey, manifest.InviterCharacterKey, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.LeaderCharacterKey, slotOne.CharacterKey.Value, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.LeaderCharacterKey, slotOne.CharacterKey.Value, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.InviterCharacterKey, slotOne.CharacterKey.Value, StringComparison.OrdinalIgnoreCase))
        {
            blocker = "Party assembly requires one exact frozen Slot1 leader and inviter.";
            return false;
        }

        var leader = ordered.SingleOrDefault(participant =>
            string.Equals(participant.AssignedSlotId, slotOne.SlotId, StringComparison.OrdinalIgnoreCase));
        if (leader == null ||
            string.IsNullOrWhiteSpace(leader.Character.CharacterName) ||
            leader.Character.WorldId == 0 ||
            leader.Character.WorldId > ushort.MaxValue)
        {
            blocker = "Frozen Slot1 inviter is missing its exact name or World ID.";
            return false;
        }

        inviter = new DadExpectedPartyInviter
        {
            RunId = plan.Request.RequestId,
            WorkerSessionId = slotOne.WorkerSessionId,
            AccountKey = slotOne.AccountKey,
            CharacterKey = slotOne.CharacterKey,
            ContentId = slotOne.ContentId,
            CharacterName = leader.Character.CharacterName,
            WorldId = (ushort)leader.Character.WorldId,
        };

        foreach (var slot in manifest.Slots.Where(static slot => !slot.IsLeader))
        {
            var participant = ordered.Single(candidate =>
                string.Equals(candidate.AssignedSlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(participant.Character.CharacterName) ||
                participant.Character.WorldId == 0 ||
                participant.Character.WorldId > ushort.MaxValue)
            {
                blocker = $"{slot.SlotId} invite target is missing its exact name or World ID.";
                return false;
            }

            inviteTargets.Add(new DadNativePartyInviteTarget
            {
                RunId = plan.Request.RequestId,
                ModuleId = plan.CompositeModuleId,
                SlotId = slot.SlotId,
                AccountKey = slot.AccountKey,
                CharacterKey = slot.CharacterKey,
                ContentId = slot.ContentId,
                CharacterName = participant.Character.CharacterName,
                WorldId = (ushort)participant.Character.WorldId,
                WorkerSessionId = slot.WorkerSessionId,
            });
        }

        return true;
    }

    public static bool IsParticipantInParty(
        DadParticipantSnapshot participant,
        IReadOnlyList<DadPartyMemberSnapshot> partyMembers)
    {
        var characterKey = participant.ActiveCharacterKey.Value;
        var contentId = participant.Character.ContentId;
        if (contentId != 0)
            return partyMembers.Any(member => member.ContentId == contentId);

        return partyMembers.Any(member =>
            !member.CharacterKey.IsEmpty &&
            string.Equals(member.CharacterKey.Value, characterKey, StringComparison.OrdinalIgnoreCase));
    }

    public static bool ShouldDispatchJoinInstruction(
        DadParticipantSnapshot participant,
        IReadOnlyList<DadPartyMemberSnapshot> leaderPartyMembers)
        => !IsParticipantInParty(participant, leaderPartyMembers);
}
