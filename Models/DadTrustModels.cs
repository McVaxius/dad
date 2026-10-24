namespace dad.Models;

public enum DadTrustNpcId
{
    Unknown = 0,
    Alphinaud = 1,
    Alisaie = 2,
    Thancred = 3,
    Urianger = 4,
    Yshtola = 5,
    Ryne = 6,
    Estinien = 7,
    GrahaTia = 8,
    Zero = 9,
    Krile = 10,
}

// Native DawnMemberEntry role bits; an all-rounder may fill any of these roles.
[Flags]
public enum DadTrustRoles
{
    None = 0,
    Tank = 1,
    Dps = 2,
    Healer = 4,
    All = Tank | Dps | Healer,
}

public sealed record DadTrustNpcObservation
{
    public DadTrustNpcId NpcId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public int? Level { get; set; }
    public int? LevelCap { get; set; }
    public bool? Unlocked { get; set; }
    public DateTime ObservedAtUtc { get; set; }
}

public sealed record DadTrustDutyMember
{
    public DadTrustNpcId NpcId { get; set; }
    public DadTrustRoles AvailableRoles { get; set; }
}

public sealed class DadTrustDutyObservation
{
    public uint ContentFinderConditionId { get; set; }
    public bool? Unlocked { get; set; }
    // null means membership was not read; an observed empty list is distinct.
    public List<DadTrustDutyMember>? Members { get; set; }
    public DateTime ObservedAtUtc { get; set; }

    public DadTrustDutyObservation Clone() => new()
    {
        ContentFinderConditionId = ContentFinderConditionId,
        Unlocked = Unlocked,
        Members = Members?.Where(static member => member != null).Select(static member => member with { }).ToList(),
        ObservedAtUtc = ObservedAtUtc,
    };
}

// Observations belong to the existing roster's character record, not a preset or
// an editable character profile. Sharing/copying those definitions carries no XP.
public sealed class DadTrustProgress
{
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong ContentId { get; set; }
    // Set only after availability for the complete supported roster was read.
    public DateTime? RosterObservedAtUtc { get; set; }
    public List<DadTrustNpcObservation> Npcs { get; set; } = [];
    public List<DadTrustDutyObservation> Duties { get; set; } = [];

    public DadTrustProgress Clone() => new()
    {
        AccountKey = AccountKey,
        CharacterKey = CharacterKey,
        ContentId = ContentId,
        RosterObservedAtUtc = RosterObservedAtUtc,
        Npcs = (Npcs ?? []).Where(static npc => npc != null).Select(static npc => npc with { }).ToList(),
        Duties = (Duties ?? []).Where(static duty => duty != null).Select(static duty => duty.Clone()).ToList(),
    };
}

public sealed class DadTrustLevelingOptions
{
    public int NpcTargetLevel { get; set; } = 100;
    public bool LevelPlayerJobs { get; set; }
    public int PlayerTargetLevel { get; set; } = 100;
    public bool AllAvailableNpcs { get; set; } = true;
    public List<DadTrustNpcId> SelectedNpcs { get; set; } = [];
    public List<uint> AllowedJobs { get; set; } = [];

    public DadTrustLevelingOptions Clone() => new()
    {
        NpcTargetLevel = NpcTargetLevel,
        LevelPlayerJobs = LevelPlayerJobs,
        PlayerTargetLevel = PlayerTargetLevel,
        AllAvailableNpcs = AllAvailableNpcs,
        SelectedNpcs = [.. SelectedNpcs ?? []],
        AllowedJobs = [.. AllowedJobs ?? []],
    };
}

public sealed record DadTrustSelectedNpc(DadTrustNpcId NpcId, DadTrustRoles Role, int Level, bool IsLevelingGoal);

public sealed class DadTrustFixedParty
{
    public uint PlayerJobId { get; set; }
    public List<DadTrustNpcId> Npcs { get; set; } = [];
    public DadTrustFixedParty Clone() => new() { PlayerJobId = PlayerJobId, Npcs = [.. Npcs ?? []] };
}

// A planning result. Actual queue admission must re-read the native party/duty.
public sealed class DadTrustPartySelection
{
    public DadLevelingCompilationStatus Status { get; set; } = DadLevelingCompilationStatus.Blocked;
    public string Summary { get; set; } = string.Empty;
    public uint PlayerJobId { get; set; }
    public int PlayerLevel { get; set; }
    public bool IsPlayerLevelingGoal { get; set; }
    public DadPlannerDutyOption? Duty { get; set; }
    public List<DadTrustSelectedNpc> Npcs { get; set; } = [];
}
