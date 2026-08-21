namespace dad.Models;

public static class DadEarlyRequestedJobAssignmentRules
{
    public static IReadOnlyList<DadWakeRequestDto> Build(
        DadRunRequest request,
        IReadOnlyList<DadSchedulerSlotState> slots,
        DadWorkerSessionId authorityWorkerSessionId,
        Func<DadSchedulerSlotState, ulong>? contentIdFallback = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(slots);
        var orchestration = request.Orchestration ?? new DadOrchestrationIntent();
        var requiredRosterCharacters = orchestration.RequiredRosterCharacters ?? [];

        return slots
            .Where(static slot => !slot.IsRegisteredIsland && slot.RequiredJobId.HasValue)
            .Select(slot =>
            {
                var reference = requiredRosterCharacters.FirstOrDefault(candidate =>
                    DadRosterIdentity.SameAccount(candidate.AccountKey, slot.RequiredAccountKey) &&
                    string.Equals(candidate.CharacterKey.Value, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase));
                return new DadWakeRequestDto
                {
                    RunId = request.RequestId,
                    AuthorityWorkerSessionId = authorityWorkerSessionId,
                    AuthorityMode = orchestration.AuthorityMode,
                    ModuleId = orchestration.ModuleTarget,
                    RequiredAccountKey = slot.RequiredAccountKey,
                    RequiredCharacterKey = slot.RequiredCharacterKey,
                    RequiredContentId = reference?.ContentId ?? contentIdFallback?.Invoke(slot) ?? 0,
                    RequiredJobId = reference?.RequiredJobId ?? slot.RequiredJobId,
                    AssignedSlotId = slot.SlotId,
                    RequirePostArReady = orchestration.RequirePostArReady,
                };
            })
            .ToList();
    }
}
