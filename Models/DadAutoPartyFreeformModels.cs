namespace dad.Models;

internal enum DadAutoPartyFreeformParticipantKind
{
    Local = 0,
    RegisteredIsland = 1,
}

internal sealed class DadAutoPartyFreeformParticipant
{
    public string SelectionKey { get; init; } = string.Empty;
    public string DisplayLabel { get; init; } = string.Empty;
    public DadAutoPartyFreeformParticipantKind Kind { get; init; }
    public DadAccountKey AccountKey { get; init; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; init; } = new(string.Empty);
    public ulong ContentId { get; init; }
    public string OwnerId { get; init; } = string.Empty;
    public string IslandId { get; init; } = string.Empty;
    public string OpaqueCharacterId { get; init; } = string.Empty;
    public uint RequestedJobId { get; init; }
}

internal sealed record DadAutoPartyFreeformFormation(
    DadPlannerGroup Group,
    IReadOnlyList<DadAutoPartyRemoteBinding> RemoteBindings);
