namespace dad.Models;

public static class DadShareConstants
{
    public const string Format = "dad-share";
    public const int MinimumSupportedSchema = 1;
    public const int Schema = 3;
    public const string PlanKind = "plan";
    public const string ScheduleKind = "schedule";
    public const int MaxEncodedCharacters = 1_048_576;
    public const int MaxDecodedBytes = 786_432;
    public const int MaxBundledPlans = 256;
    public const int MaxScheduleEntries = 512;
    public const int MaxSlotsPerPlan = 256;
}

public enum DadShareApplyMode
{
    ReplaceMatching = 0,
    SkipExisting = 1,
}

/// <summary>
/// An imported account/character marker that intentionally has no ConfigManager
/// account or roster record. It lives only on a saved planner row until that row
/// is mapped to local identities in the normal crew editor.
/// </summary>
public sealed class DadSharedIdentityPlaceholder
{
    public string IdentityToken { get; set; } = string.Empty;
    public string AccountToken { get; set; } = string.Empty;
    public string CharacterLabel { get; set; } = string.Empty;
    public bool RequiresCharacter { get; set; }

    public DadSharedIdentityPlaceholder Clone()
        => new()
        {
            IdentityToken = IdentityToken,
            AccountToken = AccountToken,
            CharacterLabel = CharacterLabel,
            RequiresCharacter = RequiresCharacter,
        };
}

public sealed class DadShareKnownIdentity
{
    public string AccountKey { get; set; } = string.Empty;
    public string AccountAlias { get; set; } = string.Empty;
    public string CharacterKey { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
}

public sealed class DadShareEnvelopeDto
{
    public string Format { get; set; } = DadShareConstants.Format;
    public int Schema { get; set; } = DadShareConstants.Schema;
    public string Kind { get; set; } = string.Empty;
    public DadSharePlanDto? Plan { get; set; }
    public DadShareScheduleDto? Schedule { get; set; }
    public List<DadSharePlanDto> Plans { get; set; } = [];
}

public sealed class DadSharePlanDto
{
    public string GroupId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DadPlannerRunFamily RunFamily { get; set; } = DadPlannerRunFamily.LevelingNpc;
    public DadPlannerActivityMode ActivityMode { get; set; } = DadPlannerActivityMode.DutySupport;
    public DadPlannerOperatorMode OperatorMode { get; set; } = DadPlannerOperatorMode.RemotePartyPlan;
    public bool ConnectedOnly { get; set; } = true;
    public bool SameDatacenterOnly { get; set; } = true;
    public bool AllowStaleForPlanning { get; set; }
    public DadTransportOwner TransportOwner { get; set; } = DadTransportOwner.DadDirect;
    public DadQueueAuthority QueueAuthority { get; set; } = DadQueueAuthority.LocalOnly;
    public DadInviteAuthority InviteAuthority { get; set; } = DadInviteAuthority.PresetLeader;
    public uint DutyContentFinderConditionId { get; set; }
    public string DutyDisplayName { get; set; } = string.Empty;
    public bool DutyUnsynced { get; set; }
    public int DutyExpectedPartySize { get; set; } = 1;
    public DadShareQueueTargetDto RouletteTarget { get; set; } = new();
    public string MogtomePreset { get; set; } = string.Empty;
    public string MogtomeDutyPolicy { get; set; } = string.Empty;
    public bool RefreshTrustNpcLevels { get; set; } = true;
    public DadShareStopPolicyDto StopPolicy { get; set; } = new();
    public DadShareLevelingModeDto LevelingMode { get; set; } = new();
    public DadShareCompletionActionsDto? CompletionActions { get; set; }
    public List<DadSharePlanSlotDto> Slots { get; set; } = [];
    public bool IsTemplate { get; set; }
    public string MapRunTemplate { get; set; } = string.Empty;
    public DadMapCrewJobMode MapMode { get; set; } = DadMapCrewJobMode.ManualMapReady;
}

public sealed class DadShareLevelingModeDto
{
    public bool Enabled { get; set; }
    public int GoalLevel { get; set; } = DadRunStopPolicy.DefaultTargetLevel;
    public DadLevelingJobOrder JobOrder { get; set; } = DadLevelingJobOrder.LowestFirst;
    public List<DadShareLevelingDutyThresholdDto> DutyThresholds { get; set; } = [];
}

public sealed class DadShareLevelingDutyThresholdDto
{
    public int MinimumLevel { get; set; }
    public uint ContentFinderConditionId { get; set; }
    public string DutyDisplayName { get; set; } = string.Empty;
}

public sealed class DadSharePlanSlotDto
{
    public string SlotId { get; set; } = string.Empty;
    public bool IsSubstitute { get; set; }
    public DadAllianceAssignment AllianceAssignment { get; set; } = DadAllianceAssignment.None;
    public DadPartyRole RequiredRole { get; set; } = DadPartyRole.Any;
    public string AccountToken { get; set; } = string.Empty;
    public string CharacterToken { get; set; } = string.Empty;
    public string CharacterLabel { get; set; } = string.Empty;
    public uint? RequiredJobId { get; set; }
    public DadAdsLootMode AdsLootMode { get; set; } = DadAdsLootMode.NoChange;
    public int? LevelSeekTarget { get; set; }
    public bool SkipIfDailyRouletteRewardReceived { get; set; }
    public DadSchedulerWakePolicy WakePolicy { get; set; } = DadSchedulerWakePolicy.LaunchIfOffline;
    public bool AllowSubstitution { get; set; }
}

public sealed class DadShareQueueTargetDto
{
    public DadQueueTargetKind Kind { get; set; } = DadQueueTargetKind.Roulette;
    public uint ContentFinderConditionId { get; set; }
    public uint RouletteId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class DadShareStopPolicyDto
{
    public DadPlannerStopMode Mode { get; set; } = DadPlannerStopMode.AfterRuns;
    public int AfterRuns { get; set; } = 1;
    public int TargetLevel { get; set; } = DadRunStopPolicy.DefaultTargetLevel;
    public string TargetCharacterToken { get; set; } = string.Empty;
    public string TargetCharacterLabel { get; set; } = string.Empty;
    public int SafetyCap { get; set; } = DadRunStopPolicy.DefaultSafetyCap;
    public uint StopItemId { get; set; }
    public int StopItemTargetCount { get; set; } = 1;
}

public sealed class DadShareCompletionActionsDto
{
    public bool PlaySound { get; set; }
    public int SoundEffectId { get; set; } = 1;
    public bool RunCommands { get; set; }
    public List<string> Commands { get; set; } = [];
    public DadCompletionKillMode KillMode { get; set; } = DadCompletionKillMode.None;
    public DadSharePostRunUtilitiesDto Utilities { get; set; } = new();
}

public sealed class DadSharePostRunUtilitiesDto
{
    public bool OpenGearCoffers { get; set; }
    public bool RegisterTripleTriadCards { get; set; }
    public bool SellTripleTriadCards { get; set; }
    public bool GrandCompanyHandInViaAutoRetainer { get; set; }
    public string GrandCompanyHandInCommand { get; set; } = "/ays gc";
}

public sealed class DadShareScheduleDto
{
    public string ScheduleId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DadScheduleCadence Cadence { get; set; } = DadScheduleCadence.Manual;
    public List<DadShareScheduleEntryDto> Entries { get; set; } = [];
}

public sealed class DadShareScheduleEntryDto
{
    public string EntryId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string PresetName { get; set; } = string.Empty;
    public int RepeatCount { get; set; } = 1;
}

public sealed class DadShareImportPreview
{
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public int BundledPlanCount { get; set; }
    public List<string> ReplacementIds { get; set; } = [];
}

public sealed class DadShareApplyResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string ResultId { get; set; } = string.Empty;
    public int AddedPlanCount { get; set; }
    public int ReplacedPlanCount { get; set; }
    public int SkippedPlanCount { get; set; }
    public bool ScheduleAdded { get; set; }
    public bool ScheduleReplaced { get; set; }
    public bool ScheduleSkipped { get; set; }
    public List<DadPlannerGroup> PlannerGroups { get; set; } = [];
    public List<DadScheduleDefinition> Schedules { get; set; } = [];
}

public sealed class DadShareRenameResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string NewId { get; set; } = string.Empty;
    public int UpdatedReferenceCount { get; set; }
}
