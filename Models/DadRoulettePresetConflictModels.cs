namespace dad.Models;

public sealed class DadRoulettePresetConflictWarning
{
    public uint RouletteId { get; init; }
    public DadAccountKey AccountKey { get; init; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; init; } = new(string.Empty);
    public IReadOnlyList<string> PresetNames { get; init; } = [];
    public bool HasConflict => PresetNames.Count > 0;
    public bool IsBlocking => false;
    public string Message => HasConflict
        ? $"This Character is already in a similar preset: {string.Join(", ", PresetNames)}."
        : string.Empty;
}

public sealed record DadCharacterConflictChoice(
    string CharacterKey,
    string DisplayName,
    bool IsConflict)
{
    public bool UseBoldOrange => IsConflict;
}

public sealed class DadCharacterConflictPresentation
{
    public IReadOnlyList<DadCharacterConflictChoice> Choices { get; init; } = [];
    public bool SelectedUseBoldOrange { get; init; }
    public IReadOnlyList<string> SummaryNames { get; init; } = [];
    public string Summary => SummaryNames.Count == 0
        ? string.Empty
        : $"Characters in multiple presets: {string.Join(", ", SummaryNames)}";
}

public static class DadCharacterConflictPresentationRules
{
    public static DadCharacterConflictPresentation Build(
        IEnumerable<DadCharacterConflictChoice>? choices,
        string? selectedCharacterKey)
    {
        var normalized = (choices ?? [])
            .Where(static choice => choice != null)
            .GroupBy(static choice => choice.CharacterKey?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(static group => new DadCharacterConflictChoice(
                group.Key,
                group.Select(static choice => choice.DisplayName?.Trim() ?? string.Empty)
                    .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name)) ?? group.Key,
                group.Any(static choice => choice.IsConflict)))
            .ToList();
        var selected = selectedCharacterKey?.Trim() ?? string.Empty;
        return new DadCharacterConflictPresentation
        {
            Choices = normalized,
            SelectedUseBoldOrange = normalized.Any(choice =>
                choice.IsConflict &&
                string.Equals(choice.CharacterKey, selected, StringComparison.OrdinalIgnoreCase)),
            SummaryNames = normalized
                .Where(static choice => choice.IsConflict)
                .Select(static choice => choice.DisplayName?.Trim() ?? string.Empty)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }
}
