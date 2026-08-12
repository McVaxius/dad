namespace dad.Models;

internal enum DadRunSlotRouteKind
{
    LanWorker = 0,
    RegisteredIsland = 1,
}

// Internal execution contract built when a coordinator accepts a multiplayer run or a
// single-worker run that needs an exact requested-job preparation assignment.
// It is deliberately not part of the IPC schema; the existing ordered
// RequiredRosterCharacters list remains the wire-compatible source of truth.
internal sealed class DadRunSlotManifest
{
    public string RequestId { get; set; } = string.Empty;
    public int ExpectedPartySize { get; set; }
    public string LeaderCharacterKey { get; set; } = string.Empty;
    public string InviterCharacterKey { get; set; } = string.Empty;
    public DadCoordinatorTravelTarget? CoordinatorTravelTarget { get; set; }
    public List<DadFrozenModulePayload> Modules { get; set; } = [];
    public List<DadFrozenRunSlot> Slots { get; set; } = [];

    public DadRunSlotManifest Clone()
        => new()
        {
            RequestId = RequestId,
            ExpectedPartySize = ExpectedPartySize,
            LeaderCharacterKey = LeaderCharacterKey,
            InviterCharacterKey = InviterCharacterKey,
            CoordinatorTravelTarget = CoordinatorTravelTarget?.Clone(),
            Modules = Modules.Select(static module => module.Clone()).ToList(),
            Slots = Slots.Select(static slot => slot.Clone()).ToList(),
        };
}

internal sealed class DadFrozenRunSlot
{
    public string SlotId { get; set; } = string.Empty;
    public DadRunSlotRouteKind RouteKind { get; set; } = DadRunSlotRouteKind.LanWorker;
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong ContentId { get; set; }
    public string OpaqueCharacterId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string IslandId { get; set; } = string.Empty;
    public uint? RequiredJobId { get; set; }
    public DadAdsLootMode? AdsLootMode { get; set; }
    public bool IsLeader { get; set; }
    public bool IsInviter { get; set; }
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);

    public DadFrozenRunSlot Clone()
        => new()
        {
            SlotId = SlotId,
            RouteKind = RouteKind,
            AccountKey = AccountKey,
            CharacterKey = CharacterKey,
            ContentId = ContentId,
            OpaqueCharacterId = OpaqueCharacterId,
            OwnerId = OwnerId,
            IslandId = IslandId,
            RequiredJobId = RequiredJobId,
            AdsLootMode = AdsLootMode,
            IsLeader = IsLeader,
            IsInviter = IsInviter,
            WorkerSessionId = WorkerSessionId,
        };
}

internal sealed class DadFrozenModulePayload
{
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public string DutyName { get; set; } = string.Empty;
    public DadQueueTargetKind TargetKind { get; set; } = DadQueueTargetKind.DutyFinderDuty;
    public uint ContentFinderConditionId { get; set; }
    public uint RouletteId { get; set; }
    public bool Unsynced { get; set; }
    public int ExpectedPartySize { get; set; }

    public DadFrozenModulePayload Clone()
        => new()
        {
            ModuleId = ModuleId,
            DutyName = DutyName,
            TargetKind = TargetKind,
            ContentFinderConditionId = ContentFinderConditionId,
            RouletteId = RouletteId,
            Unsynced = Unsynced,
            ExpectedPartySize = ExpectedPartySize,
        };
}
