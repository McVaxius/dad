namespace dad.Models;

// Internal execution contract built when a coordinator accepts a multiplayer run.
// It is deliberately not part of the IPC schema; the existing ordered
// RequiredRosterCharacters list remains the wire-compatible source of truth.
internal sealed class DadRunSlotManifest
{
    public string RequestId { get; set; } = string.Empty;
    public int ExpectedPartySize { get; set; }
    public string LeaderCharacterKey { get; set; } = string.Empty;
    public string InviterCharacterKey { get; set; } = string.Empty;
    public List<DadFrozenModulePayload> Modules { get; set; } = [];
    public List<DadFrozenRunSlot> Slots { get; set; } = [];

    public DadRunSlotManifest Clone()
        => new()
        {
            RequestId = RequestId,
            ExpectedPartySize = ExpectedPartySize,
            LeaderCharacterKey = LeaderCharacterKey,
            InviterCharacterKey = InviterCharacterKey,
            Modules = Modules.Select(static module => module.Clone()).ToList(),
            Slots = Slots.Select(static slot => slot.Clone()).ToList(),
        };
}

internal sealed class DadFrozenRunSlot
{
    public string SlotId { get; set; } = string.Empty;
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong ContentId { get; set; }
    public bool IsLeader { get; set; }
    public bool IsInviter { get; set; }
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);

    public DadFrozenRunSlot Clone()
        => new()
        {
            SlotId = SlotId,
            AccountKey = AccountKey,
            CharacterKey = CharacterKey,
            ContentId = ContentId,
            IsLeader = IsLeader,
            IsInviter = IsInviter,
            WorkerSessionId = WorkerSessionId,
        };
}

internal sealed class DadFrozenModulePayload
{
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public string DutyName { get; set; } = string.Empty;
    public uint ContentFinderConditionId { get; set; }
    public uint RouletteId { get; set; }
    public bool Unsynced { get; set; }
    public int ExpectedPartySize { get; set; }

    public DadFrozenModulePayload Clone()
        => new()
        {
            ModuleId = ModuleId,
            DutyName = DutyName,
            ContentFinderConditionId = ContentFinderConditionId,
            RouletteId = RouletteId,
            Unsynced = Unsynced,
            ExpectedPartySize = ExpectedPartySize,
        };
}
