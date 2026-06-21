using dad.Models;

namespace dad.Services;

// Feature batch A (dadfeatures20260620b): best-effort anonymizer for issue/bug-report dumps.
// Replaces character names / account ids / machine + session ids with stable aliases
// (numpty0, acct0, machineA, ...) before the operator attaches the report to a GitHub issue.
// Phase 9 (H10): the anonymization-map building lives here (extracted from Plugin) to start shrinking the god object.
internal static class DadIssueReport
{
    public static List<(string token, string alias)> BuildAnonymizationMap(
        DadCharacterPool pool,
        Configuration configuration,
        DadPresenceService presence)
    {
        var map = new List<(string token, string alias)>();
        var charKeys = new List<string>();

        foreach (var character in pool.Characters)
        {
            if (!string.IsNullOrWhiteSpace(character.CharacterKey))
                charKeys.Add(character.CharacterKey);
        }

        foreach (var run in configuration.RunHistory ?? [])
        {
            foreach (var participant in run.Participants ?? [])
            {
                var key = participant.ActiveCharacterKey.ToString();
                if (!string.IsNullOrWhiteSpace(key))
                    charKeys.Add(key);
            }
        }

        var index = 0;
        foreach (var key in charKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var alias = $"numpty{index++}";
            map.Add((key, alias));
            var name = key.Split('@')[0];
            if (name.Length >= 4 && !string.Equals(name, key, StringComparison.Ordinal))
                map.Add((name, alias));
        }

        var accountIndex = 0;
        foreach (var account in new[] { configuration.ClientAccountId, configuration.LastAccountId }
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            map.Add((account, $"acct{accountIndex++}"));
        }

        var hostIndex = 0;
        foreach (var host in new[] { configuration.TransportBindHost, configuration.AuthorityTargetHost }
                     .Where(static value => !string.IsNullOrWhiteSpace(value)
                                            && !string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            map.Add((host, $"host{hostIndex++}"));
        }

        if (!string.IsNullOrWhiteSpace(Environment.MachineName))
            map.Add((Environment.MachineName, "machineA"));
        if (!string.IsNullOrWhiteSpace(presence.ClientInstanceId))
            map.Add((presence.ClientInstanceId, "clientA"));

        var worker = presence.WorkerSessionId.ToString();
        if (!string.IsNullOrWhiteSpace(worker))
            map.Add((worker, "workerA"));

        return map;
    }

    public static string Anonymize(string text, IReadOnlyList<(string token, string alias)> map)
    {
        if (string.IsNullOrEmpty(text) || map.Count == 0)
            return text;

        // Replace longest tokens first so a name that is a substring of a key doesn't corrupt it.
        foreach (var (token, alias) in map
                     .Where(static entry => !string.IsNullOrWhiteSpace(entry.token))
                     .OrderByDescending(static entry => entry.token.Length))
        {
            text = text.Replace(token, alias, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }
}
