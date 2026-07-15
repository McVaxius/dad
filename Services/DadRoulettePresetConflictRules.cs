using System.Globalization;
using dad.Models;

namespace dad.Services;

public sealed class DadRoulettePresetConflictIndex
{
    private readonly Dictionary<string, List<Entry>> entries;

    internal DadRoulettePresetConflictIndex(Dictionary<string, List<Entry>> entries)
    {
        this.entries = entries;
    }

    public DadRoulettePresetConflictWarning Find(
        DadPlannerGroup currentGroup,
        DadAccountKey accountKey,
        DadCharacterKey characterKey)
    {
        ArgumentNullException.ThrowIfNull(currentGroup);
        var rouletteId = DadRoulettePresetConflictRules.ResolveCanonicalRouletteId(currentGroup);
        if (rouletteId == 0 || accountKey.IsEmpty || characterKey.IsEmpty)
            return Empty(rouletteId, accountKey, characterKey);

        var key = DadRoulettePresetConflictRules.BuildKey(rouletteId, accountKey, characterKey);
        if (!entries.TryGetValue(key, out var candidates))
            return Empty(rouletteId, accountKey, characterKey);

        var currentGroupId = Normalize(currentGroup.GroupId);
        var names = candidates
            .Where(entry => currentGroupId.Length == 0 ||
                            !string.Equals(entry.GroupId, currentGroupId, StringComparison.OrdinalIgnoreCase))
            .Select(static entry => entry.DisplayName)
            .Where(static name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new DadRoulettePresetConflictWarning
        {
            RouletteId = rouletteId,
            AccountKey = accountKey,
            CharacterKey = characterKey,
            PresetNames = names,
        };
    }

    private static DadRoulettePresetConflictWarning Empty(
        uint rouletteId,
        DadAccountKey accountKey,
        DadCharacterKey characterKey)
        => new()
        {
            RouletteId = rouletteId,
            AccountKey = accountKey,
            CharacterKey = characterKey,
        };

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim();

    internal sealed record Entry(string GroupId, string DisplayName);
}

public static class DadRoulettePresetConflictRules
{
    public static DadRoulettePresetConflictIndex BuildIndex(IEnumerable<DadPlannerGroup>? groups)
    {
        var entries = new Dictionary<string, List<DadRoulettePresetConflictIndex.Entry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups ?? [])
        {
            if (group == null)
                continue;
            var rouletteId = ResolveCanonicalRouletteId(group);
            if (rouletteId == 0)
                continue;

            var displayName = string.IsNullOrWhiteSpace(group.DisplayName)
                ? Normalize(group.GroupId)
                : group.DisplayName.Trim();
            var seenInGroup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in DadPlannerSlotRules.NormalizeGroupSlots(group.Slots))
            {
                if (slot.RequiredAccountKey.IsEmpty || slot.RequiredCharacterKey.IsEmpty)
                    continue;
                var key = BuildKey(rouletteId, slot.RequiredAccountKey, slot.RequiredCharacterKey);
                if (!seenInGroup.Add(key))
                    continue;
                if (!entries.TryGetValue(key, out var list))
                {
                    list = [];
                    entries[key] = list;
                }
                list.Add(new DadRoulettePresetConflictIndex.Entry(Normalize(group.GroupId), displayName));
            }
        }

        return new DadRoulettePresetConflictIndex(entries);
    }

    public static uint ResolveCanonicalRouletteId(DadPlannerGroup? group)
    {
        if (group == null || group.ActivityMode != DadPlannerActivityMode.DailyRoulette)
            return 0;
        return ResolveCanonicalRouletteId(group.RouletteTarget);
    }

    public static uint ResolveCanonicalRouletteId(DadQueueTarget? target)
    {
        if (target == null || target.Kind != DadQueueTargetKind.Roulette)
            return 0;
        if (target.RouletteId is > 0 and <= byte.MaxValue)
            return target.RouletteId;

        var key = Normalize(target.Key);
        if (string.Equals(key, DadRouletteCatalogProjection.MainScenarioLegacyKey, StringComparison.OrdinalIgnoreCase))
            return DadRouletteCatalogProjection.MainScenarioRouletteId;
        if (!key.StartsWith(DadRouletteCatalogProjection.CanonicalKeyPrefix, StringComparison.OrdinalIgnoreCase))
            return 0;

        var suffix = key[DadRouletteCatalogProjection.CanonicalKeyPrefix.Length..];
        return uint.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
               parsed is > 0 and <= byte.MaxValue
            ? parsed
            : 0;
    }

    internal static string BuildKey(
        uint rouletteId,
        DadAccountKey accountKey,
        DadCharacterKey characterKey)
        => $"roulette:{rouletteId.ToString(CultureInfo.InvariantCulture)}|account:{Normalize(accountKey.Value)}|character:{Normalize(characterKey.Value)}";

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim();
}
