using dad.Models;

namespace dad.Services;

internal static class DadNpcDutyEligibility
{
    public static string? GetBlocker(
        DadAcquiredCharacter character,
        string dutyName,
        uint contentFinderConditionId,
        int jobLevelRequired)
    {
        var characterKey = string.IsNullOrWhiteSpace(character.CharacterKey)
            ? "(unknown local character)"
            : character.CharacterKey;
        var dutyLabel = string.IsNullOrWhiteSpace(dutyName)
            ? $"Duty #{contentFinderConditionId}"
            : $"{dutyName} #{contentFinderConditionId}";
        var currentJobId = DadRosterCharacterMerge.ResolveCurrentJobId(
            character.JobLevels,
            character.CurrentJobId);
        var currentLevel = DadRosterCharacterMerge.ResolveCurrentLevel(
            character.JobLevels,
            currentJobId,
            character.CurrentLevel);

        if (!currentJobId.HasValue)
            return $"Runner '{characterKey}' has no current job data; {dutyLabel} requires a combat job.";

        if (!DadRosterCharacterMerge.IsCombatJob(currentJobId.Value))
        {
            return $"Runner '{characterKey}' is on non-combat job {FormatJob(character, currentJobId)}; {dutyLabel} requires a combat job.";
        }

        if (jobLevelRequired <= 0)
            return null;

        if (!currentLevel.HasValue)
            return $"Runner '{characterKey}' has no current level data; {dutyLabel} requires level {jobLevelRequired}.";

        if (currentLevel.Value < jobLevelRequired)
        {
            return $"Runner '{characterKey}' is level {currentLevel.Value} on {FormatJob(character, currentJobId)}; {dutyLabel} requires level {jobLevelRequired}.";
        }

        return null;
    }

    private static string FormatJob(DadAcquiredCharacter character, uint? currentJobId)
    {
        var abbreviation = character.CurrentJobAbbrev?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(abbreviation)
            ? $"#{currentJobId}"
            : $"{abbreviation} (#{currentJobId})";
    }
}
