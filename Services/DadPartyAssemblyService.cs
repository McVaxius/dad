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

        var ordered = participants
            .OrderByDescending(static participant => participant.IsLocalClient)
            .ThenBy(static participant => participant.Character.CharacterKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
                SlotId = index == 0 ? "Leader" : $"Party {index + 1}",
                RequiredCharacterKey = participant.Character.CharacterKey,
                InstructionKind = index == 0 ? DadAssemblyInstructionKind.FormParty : DadAssemblyInstructionKind.JoinParty,
                Summary = index == 0
                    ? $"Leader confirmed on {participant.Character.CharacterKey}; form Dad party."
                    : $"Join Dad party on {participant.Character.CharacterKey}.",
            });
        }

        return instructions;
    }
}
