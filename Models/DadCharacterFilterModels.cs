namespace dad.Models;

/// <summary>
/// UI-only character filtering that intentionally lives for one plugin session.
/// It is never attached to Dalamud configuration or a saved Plan.
/// </summary>
public sealed class DadCharacterFilterSessionState
{
    public string CharacterSearch { get; set; } = string.Empty;
    public string DataCenterName { get; set; } = string.Empty;
    public string WorldName { get; set; } = string.Empty;

    public bool HasFilters =>
        !string.IsNullOrWhiteSpace(CharacterSearch) ||
        !string.IsNullOrWhiteSpace(DataCenterName) ||
        !string.IsNullOrWhiteSpace(WorldName);

    public void Clear()
    {
        CharacterSearch = string.Empty;
        DataCenterName = string.Empty;
        WorldName = string.Empty;
    }
}

public sealed class DadCharacterFilterResult
{
    public int TotalCount { get; init; }
    public int ResultCount => Characters.Count;
    public IReadOnlyList<DadAcquiredCharacter> Characters { get; init; } = [];
    public IReadOnlyList<string> DataCenters { get; init; } = [];
    public IReadOnlyList<string> Worlds { get; init; } = [];
}

