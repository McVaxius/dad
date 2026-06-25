using dad.Models;

namespace dad.Services;

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
        blocker = string.Empty;
        if (plan.RequiredParticipantCount <= 1)
            return true;

        if (partyMembers.Count < plan.RequiredParticipantCount)
        {
            blocker = $"Waiting for party assembly: PartyList has {partyMembers.Count}/{plan.RequiredParticipantCount} member(s).";
            return false;
        }

        var missing = participants
            .Where(participant => !IsParticipantInParty(participant, partyMembers))
            .Select(static participant => participant.ActiveCharacterKey.ToString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count == 0)
            return true;

        blocker = $"Waiting for party member(s): {string.Join(", ", missing)}.";
        return false;
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
        return partyMembers.Any(member =>
            (contentId != 0 && member.ContentId == contentId) ||
            (!member.CharacterKey.IsEmpty &&
             string.Equals(member.CharacterKey.Value, characterKey, StringComparison.OrdinalIgnoreCase)));
    }
}
