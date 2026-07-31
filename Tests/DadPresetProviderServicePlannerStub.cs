using dad.Models;

namespace dad.Services;

public sealed class DadPresetProviderService
{
    public DadPlannerDutyOption? SelectHighestEligibleNpcDuty(
        DadAcquiredCharacter? character,
        DadNpcAutoLevelLane lane,
        out string blocker)
    {
        blocker = "Test stub does not resolve NPC duties.";
        return null;
    }

    public DadPlannerDutyOption? GetPlannerDuty(uint contentFinderConditionId)
        => null;
}
