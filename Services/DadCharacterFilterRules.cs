using dad.Models;

namespace dad.Services;

public static class DadCharacterFilterRules
{
    public static DadCharacterFilterResult Apply(
        IEnumerable<DadAcquiredCharacter>? source,
        DadCharacterFilterSessionState? state)
    {
        var characters = (source ?? [])
            .Where(static character => character != null)
            .ToList();
        state ??= new DadCharacterFilterSessionState();

        var selectedDataCenter = Normalize(state.DataCenterName);
        var selectedWorld = Normalize(state.WorldName);
        var search = Normalize(state.CharacterSearch);
        var dataCenters = characters
            .Select(static character => Normalize(character.DataCenterName))
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var worlds = characters
            .Where(character => selectedDataCenter.Length == 0 ||
                                Same(character.DataCenterName, selectedDataCenter))
            .Select(static character => Normalize(character.WorldName))
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var filtered = characters
            .Where(character => selectedDataCenter.Length == 0 ||
                                Same(character.DataCenterName, selectedDataCenter))
            .Where(character => selectedWorld.Length == 0 ||
                                Same(character.WorldName, selectedWorld))
            .Where(character => search.Length == 0 ||
                                Contains(character.CharacterName, search) ||
                                Contains(character.CharacterKey, search))
            .ToList();

        return new DadCharacterFilterResult
        {
            TotalCount = characters.Count,
            Characters = filtered,
            DataCenters = dataCenters,
            Worlds = worlds,
        };
    }

    public static bool WorldBelongsToDataCenter(
        IEnumerable<DadAcquiredCharacter>? source,
        string worldName,
        string dataCenterName)
    {
        var world = Normalize(worldName);
        var dataCenter = Normalize(dataCenterName);
        if (world.Length == 0 || dataCenter.Length == 0)
            return false;

        return (source ?? []).Any(character =>
            character != null &&
            Same(character.WorldName, world) &&
            Same(character.DataCenterName, dataCenter));
    }

    private static bool Contains(string? value, string search)
        => Normalize(value).Contains(search, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim();
}

