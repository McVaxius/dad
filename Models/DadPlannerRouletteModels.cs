namespace dad.Models;

public sealed class DadPlannerRouletteOption
{
    public uint RouletteId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int SortKey { get; set; }
    public bool IsAvailable { get; set; } = true;
    public string UnavailableReason { get; set; } = string.Empty;

    public DadPlannerRouletteOption Clone()
        => new()
        {
            RouletteId = RouletteId,
            Key = Key,
            DisplayName = DisplayName,
            SortKey = SortKey,
            IsAvailable = IsAvailable,
            UnavailableReason = UnavailableReason,
        };

    public DadQueueTarget ToQueueTarget()
        => new()
        {
            Kind = DadQueueTargetKind.Roulette,
            RouletteId = RouletteId,
            Key = Key,
            DisplayName = DisplayName,
        };
}

public readonly record struct DadContentRouletteCatalogRow(
    uint RowId,
    string Name,
    bool IsInDutyFinder,
    bool IsPvP,
    byte MembersPerParty,
    byte PartyCount,
    byte SortKey,
    byte QueueMaxPlayers = 0);
