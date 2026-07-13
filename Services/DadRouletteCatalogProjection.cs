using System.Globalization;
using dad.Models;

namespace dad.Services;

public static class DadRouletteCatalogProjection
{
    public const uint MainScenarioRouletteId = 3;
    public const string MainScenarioLegacyKey = "MainScenario";
    public const string CanonicalKeyPrefix = "ContentRoulette:";

    public static IReadOnlyList<DadPlannerRouletteOption> BuildOptions(
        IEnumerable<DadContentRouletteCatalogRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return rows
            .Where(static row => row.RowId is > 0 and <= byte.MaxValue)
            .Where(static row => !string.IsNullOrWhiteSpace(row.Name))
            .Where(static row => row.IsInDutyFinder && !row.IsPvP)
            .Where(static row => row.MembersPerParty == 4 && row.PartyCount == 1)
            .Select(static row => new DadPlannerRouletteOption
            {
                RouletteId = row.RowId,
                Key = BuildCanonicalKey(row.RowId),
                DisplayName = row.Name.Trim(),
                SortKey = row.SortKey,
                IsAvailable = true,
            })
            .OrderBy(static option => option.SortKey)
            .ThenBy(static option => option.DisplayName, StringComparer.CurrentCulture)
            .ThenBy(static option => option.RouletteId)
            .ToList();
    }

    public static string BuildCanonicalKey(uint rouletteId)
    {
        if (rouletteId is 0 or > byte.MaxValue)
            return string.Empty;

        return $"{CanonicalKeyPrefix}{rouletteId.ToString(CultureInfo.InvariantCulture)}";
    }

    public static DadPlannerRouletteOption? ResolveEligibleOption(
        IEnumerable<DadPlannerRouletteOption> options,
        uint rouletteId)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (rouletteId is 0 or > byte.MaxValue)
            return null;

        return options.FirstOrDefault(option =>
            option.IsAvailable &&
            option.RouletteId == rouletteId);
    }
}
