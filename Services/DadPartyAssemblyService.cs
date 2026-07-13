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

public sealed class DadPartyAssemblyService
{
    public List<DadAssemblyInstructionDto> BuildInstructions(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants, out string blocker)
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

        var ordered = OrderParticipantsForParty(plan, participants);
        if (!string.IsNullOrWhiteSpace(plan.LeaderCharacterKey) &&
            ordered.Count > 0 &&
            !string.Equals(ordered[0].ActiveCharacterKey, plan.LeaderCharacterKey, StringComparison.OrdinalIgnoreCase))
        {
            blocker = $"Configured leader {plan.LeaderCharacterKey} is not online on Slot1.";
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
                AuthorityWorkerSessionId = participant.IsAuthority ? participant.WorkerSessionId : new DadWorkerSessionId(string.Empty),
                ModuleId = plan.CompositeModuleId,
                SlotId = string.IsNullOrWhiteSpace(participant.AssignedSlotId)
                    ? DadPlannerSlotRules.FormatSlotId(index + 1)
                    : participant.AssignedSlotId,
                RequiredCharacterKey = participant.Character.CharacterKey,
                InstructionKind = index == 0 ? DadAssemblyInstructionKind.FormParty : DadAssemblyInstructionKind.JoinParty,
                Summary = index == 0
                    ? $"Leader confirmed on {participant.Character.CharacterKey}; form Dad party."
                    : $"Join Dad party on {participant.Character.CharacterKey}.",
            });
        }

        return instructions;
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
