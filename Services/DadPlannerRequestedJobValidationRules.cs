using dad.Models;

namespace dad.Services;

public enum DadPlannerRequestedJobValidationFailure
{
    None,
    InvalidCombatJob,
    ExactCharacterUnavailable,
    XadbUnavailable,
    JobUnavailable,
}

public static class DadPlannerRequestedJobValidationRules
{
    public static DadPlannerRequestedJobValidationFailure Validate(
        DadPresetCharacterSlot slot,
        IReadOnlyList<DadAcquiredCharacter> availableCharacters)
    {
        if (!slot.RequiredJobId.HasValue)
            return DadPlannerRequestedJobValidationFailure.None;

        var requiredJobId = slot.RequiredJobId.Value;
        if (!DadRosterCharacterMerge.IsCombatJob(requiredJobId))
            return DadPlannerRequestedJobValidationFailure.InvalidCombatJob;

        var exactCharacters = availableCharacters
            .Where(character => MatchesExactSelectedCharacter(character, slot))
            .ToList();
        if (exactCharacters.Count == 0)
            return DadPlannerRequestedJobValidationFailure.ExactCharacterUnavailable;

        return exactCharacters.Any(character =>
                character.JobLevels != null &&
                character.JobLevels.TryGetValue(requiredJobId, out var level) &&
                level > 0)
            ? DadPlannerRequestedJobValidationFailure.None
            : DadPlannerRequestedJobValidationFailure.JobUnavailable;
    }

    private static bool MatchesExactSelectedCharacter(
        DadAcquiredCharacter character,
        DadPresetCharacterSlot slot)
    {
        if (slot.RequiredAccountKey.IsEmpty || !MatchesAccountKey(character, slot.RequiredAccountKey))
        {
            return false;
        }

        if (slot.ContentId is > 0 && character.ContentId != slot.ContentId.Value)
            return false;

        if (!string.IsNullOrWhiteSpace(slot.CharacterKey) &&
            !string.Equals(character.CharacterKey, slot.CharacterKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return slot.RequiredCharacterKey.IsEmpty ||
               string.Equals(
                   character.CharacterKey,
                   slot.RequiredCharacterKey.Value,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAccountKey(DadAcquiredCharacter character, DadAccountKey accountKey)
    {
        var exactAccountKey = DadRosterIdentity.ResolveAccountKey(character.AccountId, character.AccountAlias);
        return !exactAccountKey.IsEmpty && DadRosterIdentity.SameAccount(exactAccountKey, accountKey);
    }
}
