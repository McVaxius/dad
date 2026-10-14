using dad.Models;

namespace dad.Services;

internal static class DadNpcDutyEligibility
{
    public static string? GetBlocker(
        DadAcquiredCharacter character,
        string dutyName,
        uint contentFinderConditionId,
        int jobLevelRequired,
        uint? requiredJobId = null)
    {
        var characterKey = string.IsNullOrWhiteSpace(character.CharacterKey)
            ? "(unknown local character)"
            : character.CharacterKey;
        var dutyLabel = string.IsNullOrWhiteSpace(dutyName)
            ? $"Duty #{contentFinderConditionId}"
            : $"{dutyName} #{contentFinderConditionId}";
        var useRequiredJob = requiredJobId is > 0;
        var currentJobId = useRequiredJob
            ? requiredJobId
            : DadRosterCharacterMerge.ResolveCurrentJobId(
                character.JobLevels,
                character.CurrentJobId);
        var currentLevel = useRequiredJob
            ? ResolveRequiredJobLevel(character, requiredJobId!.Value)
            : DadRosterCharacterMerge.ResolveCurrentLevel(
                character.JobLevels,
                currentJobId,
                character.CurrentLevel);

        if (!currentJobId.HasValue)
            return $"Runner '{characterKey}' has no current job data; {dutyLabel} requires a combat job.";

        if (!DadRosterCharacterMerge.IsCombatJob(currentJobId.Value))
        {
            return $"Runner '{characterKey}' is on non-combat job {FormatJob(character, currentJobId, useRequiredJob)}; {dutyLabel} requires a combat job.";
        }

        if (DadRosterCharacterMerge.IsLimitedJob(currentJobId.Value))
            return $"Runner '{characterKey}' is on limited job {FormatJob(character, currentJobId, useRequiredJob)}; Duty Support and Trust do not support limited jobs.";

        if (jobLevelRequired <= 0)
            return null;

        if (!currentLevel.HasValue)
        {
            return useRequiredJob
                ? $"Runner '{characterKey}' has no level data for required job #{currentJobId}; {dutyLabel} requires level {jobLevelRequired}."
                : $"Runner '{characterKey}' has no current level data; {dutyLabel} requires level {jobLevelRequired}.";
        }

        if (currentLevel.Value < jobLevelRequired)
        {
            return $"Runner '{characterKey}' is level {currentLevel.Value} on {FormatJob(character, currentJobId, useRequiredJob)}; {dutyLabel} requires level {jobLevelRequired}.";
        }

        return null;
    }

    private static int? ResolveRequiredJobLevel(DadAcquiredCharacter character, uint requiredJobId)
    {
        if (character.JobLevels.TryGetValue(requiredJobId, out var knownLevel) && knownLevel > 0)
            return knownLevel;

        return character.CurrentJobId == requiredJobId && character.CurrentLevel is > 0
            ? character.CurrentLevel
            : null;
    }

    private static string FormatJob(DadAcquiredCharacter character, uint? currentJobId, bool requiredJob = false)
    {
        if (requiredJob)
            return $"required job #{currentJobId}";

        var abbreviation = character.CurrentJobAbbrev?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(abbreviation)
            ? $"#{currentJobId}"
            : $"{abbreviation} (#{currentJobId})";
    }
}
