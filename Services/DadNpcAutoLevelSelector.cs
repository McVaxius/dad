using dad.Models;

namespace dad.Services;

public enum DadNpcAutoLevelLane
{
    DutySupport,
    Trust,
}

public static class DadNpcAutoLevelSelector
{
    public static DadPlannerDutyOption? SelectHighestEligibleDuty(
        IEnumerable<DadPlannerDutyOption> duties,
        DadAcquiredCharacter? character,
        DadNpcAutoLevelLane lane,
        out string blocker)
    {
        blocker = string.Empty;
        if (character == null)
        {
            blocker = $"{FormatLane(lane)} auto-leveling requires a local character snapshot.";
            return null;
        }

        var currentLevel = DadRosterCharacterMerge.ResolveCurrentLevel(
            character.JobLevels,
            character.CurrentJobId,
            character.CurrentLevel);
        if (!currentLevel.HasValue)
        {
            blocker = $"{FormatLane(lane)} auto-leveling requires current combat job level data.";
            return null;
        }

        var eligible = duties
            .Where(duty => SupportsLane(duty, lane))
            .Where(duty => duty.JobLevelRequired <= Math.Max(1, currentLevel.Value))
            .Where(duty => string.IsNullOrWhiteSpace(DadNpcDutyEligibility.GetBlocker(
                character,
                duty.DutyDisplayName,
                duty.ContentFinderConditionId,
                duty.JobLevelRequired)))
            .OrderByDescending(static duty => duty.JobLevelRequired)
            .ThenByDescending(static duty => duty.JobLevelSync)
            .ThenBy(static duty => duty.ContentFinderConditionId)
            .FirstOrDefault();

        if (eligible == null)
        {
            blocker = $"{FormatLane(lane)} auto-leveling found no eligible duty for {FormatCharacter(character)} at level {currentLevel.Value}.";
            return null;
        }

        return eligible;
    }

    private static bool SupportsLane(DadPlannerDutyOption duty, DadNpcAutoLevelLane lane)
        => lane == DadNpcAutoLevelLane.Trust
            ? duty.SupportsTrust
            : duty.SupportsDutySupport;

    private static string FormatLane(DadNpcAutoLevelLane lane)
        => lane == DadNpcAutoLevelLane.Trust ? "Trust" : "Duty Support";

    private static string FormatCharacter(DadAcquiredCharacter character)
    {
        if (!string.IsNullOrWhiteSpace(character.CharacterName) && !string.IsNullOrWhiteSpace(character.WorldName))
            return $"{character.CharacterName}@{character.WorldName}";

        return string.IsNullOrWhiteSpace(character.CharacterKey) ? "selected character" : character.CharacterKey;
    }
}
