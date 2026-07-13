using dad.Models;

namespace dad.Services;

internal static class DadCharacterXadbMergeRules
{
    public static void Merge(DadAcquiredCharacter character, DadXadbStatus xadbStatus)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(xadbStatus);

        if (!xadbStatus.IsReady)
        {
            character.XadbReady = false;
            AddBlocker(character, "XADB unavailable.");
            return;
        }

        if (!MatchesExactCharacter(character, xadbStatus))
        {
            character.XadbReady = false;
            AddBlocker(character, "XADB summary identity does not match the current character.");
            return;
        }

        character.XadbReady = true;
        character.XadbSnapshotUtc = xadbStatus.SnapshotUtc;
        character.SnapshotVersion = xadbStatus.SnapshotVersion;
        character.SnapshotQuality = xadbStatus.SnapshotQuality;

        if (character.ContentId == 0 && xadbStatus.ContentId != 0)
            character.ContentId = xadbStatus.ContentId;
        if (character.WorldId == 0 && xadbStatus.WorldId.HasValue)
            character.WorldId = xadbStatus.WorldId.Value;
        if (string.IsNullOrWhiteSpace(character.WorldName))
            character.WorldName = xadbStatus.WorldName;
        if (character.DataCenterId == null && xadbStatus.DataCenterId.HasValue)
            character.DataCenterId = xadbStatus.DataCenterId;
        if (string.IsNullOrWhiteSpace(character.DataCenterName))
            character.DataCenterName = xadbStatus.DataCenterName;
        if (character.CurrentJobId == null && xadbStatus.CurrentJobId.HasValue)
            character.CurrentJobId = xadbStatus.CurrentJobId.Value;
        if (string.IsNullOrWhiteSpace(character.CurrentJobAbbrev))
            character.CurrentJobAbbrev = xadbStatus.CurrentJobAbbrev;
        if (character.CurrentLevel == null && xadbStatus.CurrentLevel.HasValue)
            character.CurrentLevel = xadbStatus.CurrentLevel.Value;

        DadRosterCharacterMerge.MergeJobLedger(
            character.JobLevels,
            xadbStatus.JobLevels,
            character.CurrentJobId,
            character.CurrentLevel);
        character.CurrentJobId = DadRosterCharacterMerge.ResolveCurrentJobId(
            character.JobLevels,
            character.CurrentJobId);
        character.CurrentLevel = DadRosterCharacterMerge.ResolveCurrentLevel(
            character.JobLevels,
            character.CurrentJobId,
            character.CurrentLevel);

        if (character.JobLevels.Count == 0)
            AddBlocker(character, "Missing XADB job levels.");
        if (!string.IsNullOrWhiteSpace(xadbStatus.SnapshotQuality) &&
            xadbStatus.SnapshotQuality.Contains("partial", StringComparison.OrdinalIgnoreCase))
        {
            AddBlocker(character, $"XADB snapshot quality {xadbStatus.SnapshotQuality}.");
        }
    }

    public static bool MatchesExactCharacter(DadAcquiredCharacter character, DadXadbStatus xadbStatus)
    {
        if (character.ContentId != 0 && xadbStatus.ContentId != 0)
            return character.ContentId == xadbStatus.ContentId;

        if (string.IsNullOrWhiteSpace(character.CharacterKey) ||
            string.IsNullOrWhiteSpace(xadbStatus.CharacterName) ||
            string.IsNullOrWhiteSpace(xadbStatus.WorldName))
        {
            return false;
        }

        return string.Equals(
            character.CharacterKey,
            BuildCharacterKey(xadbStatus.CharacterName, xadbStatus.WorldName),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCharacterKey(string? name, string? worldName)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? "Unknown" : name.Trim();
        var cleanWorld = string.IsNullOrWhiteSpace(worldName) ? "Unknown" : worldName.Trim();
        return $"{cleanName}@{cleanWorld}";
    }

    private static void AddBlocker(DadAcquiredCharacter character, string blocker)
    {
        if (!character.Blockers.Any(existing =>
                string.Equals(existing, blocker, StringComparison.OrdinalIgnoreCase)))
        {
            character.Blockers.Add(blocker);
        }
    }
}
