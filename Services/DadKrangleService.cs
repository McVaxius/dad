using dad.Models;

namespace dad.Services;

public sealed class DadKrangleService
{
    private static readonly string[] ExerciseWords =
    [
        "Pushup", "Squat", "Lunge", "Plank", "Burpee", "Crunch", "Deadlift",
        "Curl", "Press", "Pullup", "Shrug", "Thrust", "Bridge", "Flutter",
        "Situp", "Sprawl", "Kata", "Kihon", "Kumite", "Ukemi", "Breakfall",
        "Sweep", "Roundhouse", "Jab", "Hook", "Cross", "Uppercut", "Parry",
        "Block", "Guard", "Stance", "Strike", "Punch", "Kick", "Elbow",
        "Knee", "Clinch", "Throw", "Grapple", "Armbar", "Choke", "Dodge",
        "Weave", "Slip", "Roll", "Feint", "Riposte", "Sprint", "Bench",
        "Clean", "Snatch", "Jerk", "Row", "Dip", "Step", "Jump", "Dash",
        "March", "Drill", "Crawl", "Climb", "Planche", "Muscle", "Lever",
        "Pistol", "Dragon", "Crane", "Tiger", "Mantis", "Viper", "Eagle",
    ];

    private readonly Configuration configuration;
    private readonly Dictionary<string, string> cache = new(StringComparer.Ordinal);
    private DateTime? lastChangedUtc;
    private string lastStatus = "Krangle names are off.";

    public DadKrangleService(Configuration configuration)
    {
        this.configuration = configuration;
    }

    public bool Enabled => configuration.KrangleOperatorNamesEnabled;

    public string LastStatus => lastStatus;

    public string Toggle(DadCharacterPool pool)
    {
        configuration.KrangleOperatorNamesEnabled = !configuration.KrangleOperatorNamesEnabled;
        configuration.Save();
        lastChangedUtc = DateTime.UtcNow;
        lastStatus = BuildStatus(pool);
        return lastStatus;
    }

    public string BuildStatus(DadCharacterPool pool)
    {
        var state = Enabled ? "on" : "off";
        var suffix = lastChangedUtc.HasValue
            ? $" | changed {lastChangedUtc.Value.ToLocalTime():HH:mm:ss}"
            : string.Empty;
        return $"Krangle names {state}. {pool.Characters.Count} acquired character row(s) covered.{suffix}";
    }

    public string FormatCharacterKey(string? value)
        => Enabled ? KrangleName(value) : value ?? string.Empty;

    public string FormatAccountLabel(string? alias, string? accountKey)
    {
        var cleanAlias = string.IsNullOrWhiteSpace(alias) ? "(unknown)" : alias.Trim();
        var cleanKey = string.IsNullOrWhiteSpace(accountKey) ? string.Empty : accountKey.Trim();
        if (!Enabled)
            return string.IsNullOrWhiteSpace(cleanKey) ? cleanAlias : $"{cleanAlias} ({cleanKey})";

        var krangledAlias = KrangleName(cleanAlias);
        if (string.IsNullOrWhiteSpace(cleanKey))
            return krangledAlias;

        return $"{krangledAlias} ({BuildToken("acct", cleanKey)})";
    }

    public string FormatCharacterKeys(IEnumerable<DadCharacterKey> keys)
    {
        var values = keys
            .Select(static key => key.ToString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(FormatCharacterKey)
            .ToList();
        return values.Count == 0 ? "(none)" : string.Join(", ", values);
    }

    public string FormatOperatorText(string? value, DadCharacterPool pool)
    {
        if (string.IsNullOrWhiteSpace(value) || !Enabled)
            return value ?? string.Empty;

        var output = value;
        foreach (var character in pool.Characters)
        {
            ReplaceIfPresent(ref output, character.CharacterKey, FormatCharacterKey(character.CharacterKey));
            ReplaceIfPresent(ref output, character.CharacterName, KrangleName(character.CharacterName));
            ReplaceIfPresent(ref output, character.AccountAlias, KrangleName(character.AccountAlias));
            ReplaceTokenIfPresent(ref output, character.AccountId, BuildToken("acct", character.AccountId));
        }

        return output;
    }

    public string KrangleName(string? originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName))
            return originalName ?? string.Empty;

        if (cache.TryGetValue(originalName, out var cached))
            return cached;

        var atIndex = originalName.IndexOf('@', StringComparison.Ordinal);
        var characterPart = atIndex >= 0 ? originalName[..atIndex] : originalName;
        var serverPart = atIndex >= 0 ? originalName[(atIndex + 1)..] : string.Empty;
        var hash = GetStableHash(characterPart);
        var rng = new Random(hash);
        var sourceParts = characterPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var first = TrimNamePart(ExerciseWords[rng.Next(ExerciseWords.Length)]);
        var last = sourceParts.Length > 1 ? TrimNamePart(ExerciseWords[rng.Next(ExerciseWords.Length)]) : string.Empty;
        if (last.Length > 0 && first.Length + 1 + last.Length > 22)
            last = last[..Math.Max(1, 22 - first.Length - 1)];

        var result = last.Length > 0 ? $"{first} {last}" : first;
        if (!string.IsNullOrWhiteSpace(serverPart))
            result = $"{result}@{KrangleServer(serverPart)}";

        cache[originalName] = result;
        return result;
    }

    private string KrangleServer(string serverName)
    {
        var key = $"srv:{serverName}";
        if (cache.TryGetValue(key, out var cached))
            return cached;

        var rng = new Random(GetStableHash(serverName));
        var result = TrimNamePart(ExerciseWords[rng.Next(ExerciseWords.Length)], 25);
        cache[key] = result;
        return result;
    }

    private string BuildToken(string prefix, string value)
    {
        var key = $"{prefix}:{value}";
        if (cache.TryGetValue(key, out var cached))
            return cached;

        var hash = GetStableHash(key);
        var rng = new Random(hash);
        var result = $"{prefix}-{ExerciseWords[rng.Next(ExerciseWords.Length)]}-{Math.Abs(hash % 10000):D4}";
        cache[key] = result;
        return result;
    }

    private static void ReplaceIfPresent(ref string output, string? original, string replacement)
    {
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(replacement))
            return;

        output = output.Replace(original, replacement, StringComparison.Ordinal);
    }

    private static void ReplaceTokenIfPresent(ref string output, string? original, string replacement)
    {
        if (string.IsNullOrWhiteSpace(original) || original.Trim().Length < 4 || string.IsNullOrWhiteSpace(replacement))
            return;

        output = output.Replace(original.Trim(), replacement, StringComparison.Ordinal);
    }

    private static string TrimNamePart(string value, int maxLength = 14)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static int GetStableHash(string input)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in input)
                hash = (hash * 31) + character;

            return hash;
        }
    }
}
