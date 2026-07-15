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

