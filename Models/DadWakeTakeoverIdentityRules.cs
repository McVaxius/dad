namespace dad.Models;

public readonly record struct DadWakeTakeoverIdentityDecision(
    bool AccountMatches,
    bool CharacterKnownToAccount,
    bool TransientAccountMatches);

public static class DadWakeTakeoverIdentityRules
{
    public static DadWakeTakeoverIdentityDecision Evaluate(
        string configuredClientAccountId,
        DadAccountKey requestedAccountKey,
        DadCharacterKey requestedCharacterKey,
        AccountConfig? requestedAccount,
        params DadAccountKey[] transientAccountEvidence)
    {
        var configuredId = configuredClientAccountId?.Trim() ?? string.Empty;
        var requestedId = requestedAccountKey.Value.Trim();
        var persistedAccountId = requestedAccount?.AccountId?.Trim() ?? string.Empty;

        var accountMatches = !string.IsNullOrWhiteSpace(configuredId) &&
                             string.Equals(configuredId, requestedId, StringComparison.OrdinalIgnoreCase) &&
                             !string.IsNullOrWhiteSpace(persistedAccountId) &&
                             string.Equals(configuredId, persistedAccountId, StringComparison.OrdinalIgnoreCase);
        var characterKnown = accountMatches &&
                             !requestedCharacterKey.IsEmpty &&
                             requestedAccount!.Characters.Keys.Any(characterKey =>
                                 string.Equals(
                                     characterKey?.Trim(),
                                     requestedCharacterKey.Value.Trim(),
                                     StringComparison.OrdinalIgnoreCase));
        var transientMatches = transientAccountEvidence.Any(accountKey =>
            !accountKey.IsEmpty &&
            string.Equals(accountKey.Value.Trim(), requestedId, StringComparison.OrdinalIgnoreCase));

        return new DadWakeTakeoverIdentityDecision(accountMatches, characterKnown, transientMatches);
    }
}
