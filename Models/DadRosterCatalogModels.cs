namespace dad.Models;

public enum DadRosterVisibility
{
    Active,
    Hidden,
    Ignored,
    NeedsUpdate,
}

public enum DadSchedulerJobType
{
    ScheduledPreset,
    RosterUpdate,
    MapCrew,
}

public enum DadMapCrewJobMode
{
    ManualMapReady,
    GatherThenRun,
    PluginHandoff,
}

public sealed class DadRosterVisibilityRecord
{
    public string CharacterKey { get; set; } = string.Empty;
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadRosterVisibility Visibility { get; set; } = DadRosterVisibility.Active;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = string.Empty;

    public DadRosterVisibilityRecord Clone()
        => new()
        {
            CharacterKey = CharacterKey,
            AccountKey = AccountKey,
            Visibility = Visibility,
            UpdatedAtUtc = UpdatedAtUtc,
            Reason = Reason,
        };
}

public sealed class DadRosterCatalogConfiguration
{
    public int Version { get; set; } = 1;
    public bool ShowHiddenInRoster { get; set; }
    public bool ShowAllInPresetSlots { get; set; }
    public int StaleAfterHours { get; set; } = 72;
    public List<DadRosterVisibilityRecord> Visibility { get; set; } = [];
    public List<DadRosterRefreshRecord> RefreshHistory { get; set; } = [];
}

public sealed class DadRosterRefreshRecord
{
    public string CharacterKey { get; set; } = string.Empty;
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RefreshedAtUtc { get; set; }
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;

    public DadRosterRefreshRecord Clone()
        => new()
        {
            CharacterKey = CharacterKey,
            AccountKey = AccountKey,
            RequestedAtUtc = RequestedAtUtc,
            RefreshedAtUtc = RefreshedAtUtc,
            Success = Success,
            Summary = Summary,
        };
}

public sealed class DadRosterCharacter
{
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public string AccountAlias { get; set; } = string.Empty;
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong ContentId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public uint? WorldId { get; set; }
    public string WorldName { get; set; } = string.Empty;
    public uint? DataCenterId { get; set; }
    public string DataCenterName { get; set; } = string.Empty;
    public DateTime? LastSnapshotUtc { get; set; }
    public DateTime? LastRuntimeSeenUtc { get; set; }
    public DateTime? LastRosterRefreshUtc { get; set; }
    public Dictionary<uint, int> JobLevels { get; set; } = [];
    public uint? CurrentJobId { get; set; }
    public string CurrentJobAbbrev { get; set; } = string.Empty;
    public int? CurrentLevel { get; set; }
    public string SnapshotQuality { get; set; } = string.Empty;
    public int? SnapshotVersion { get; set; }
    public bool XadbReady { get; set; }
    public bool IsCurrent { get; set; }
    public bool IsStale { get; set; }
    public bool? MapEligible { get; set; }
    public string MapEligibilitySummary { get; set; } = string.Empty;
    public DadRosterVisibility Visibility { get; set; } = DadRosterVisibility.Active;
    public DadCharacterSource Source { get; set; } = DadCharacterSource.XadbOnly;
    public List<string> Blockers { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    public DadRosterCharacter Clone()
        => new()
        {
            AccountKey = AccountKey,
            AccountAlias = AccountAlias,
            CharacterKey = CharacterKey,
            ContentId = ContentId,
            CharacterName = CharacterName,
            WorldId = WorldId,
            WorldName = WorldName,
            DataCenterId = DataCenterId,
            DataCenterName = DataCenterName,
            LastSnapshotUtc = LastSnapshotUtc,
            LastRuntimeSeenUtc = LastRuntimeSeenUtc,
            LastRosterRefreshUtc = LastRosterRefreshUtc,
            JobLevels = new Dictionary<uint, int>(JobLevels),
            CurrentJobId = CurrentJobId,
            CurrentJobAbbrev = CurrentJobAbbrev,
            CurrentLevel = CurrentLevel,
            SnapshotQuality = SnapshotQuality,
            SnapshotVersion = SnapshotVersion,
            XadbReady = XadbReady,
            IsCurrent = IsCurrent,
            IsStale = IsStale,
            MapEligible = MapEligible,
            MapEligibilitySummary = MapEligibilitySummary,
            Visibility = Visibility,
            Source = Source,
            Blockers = [..Blockers],
            Warnings = [..Warnings],
        };
}

public sealed class DadAccountRosterCatalog
{
    public int Version { get; set; } = 1;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string SourceClientInstanceId { get; set; } = string.Empty;
    public DadWorkerSessionId SourceWorkerSessionId { get; set; } = new(string.Empty);
    public bool IsFullRosterAvailable { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<DadRosterCharacter> Characters { get; set; } = [];
    public List<DadRosterVisibilityRecord> Visibility { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    public DadAccountRosterCatalog Clone()
        => new()
        {
            Version = Version,
            GeneratedAtUtc = GeneratedAtUtc,
            SourceClientInstanceId = SourceClientInstanceId,
            SourceWorkerSessionId = SourceWorkerSessionId,
            IsFullRosterAvailable = IsFullRosterAvailable,
            Summary = Summary,
            Characters = Characters.Select(static character => character.Clone()).ToList(),
            Visibility = Visibility.Select(static record => record.Clone()).ToList(),
            Warnings = [..Warnings],
        };
}

public sealed class DadRosterRefreshPlan
{
    public string PlanId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public bool ForcePeerRefresh { get; set; }
    public bool IncludeHidden { get; set; }
    public bool IncludeIgnored { get; set; }
    public int StaleAfterHours { get; set; } = 72;
    public List<DadAccountKey> AccountKeys { get; set; } = [];
    public List<DadCharacterKey> CharacterKeys { get; set; } = [];
    public bool DryRun { get; set; }
}

public sealed class DadRosterVisibilityChangeRequest
{
    public List<DadCharacterKey> CharacterKeys { get; set; } = [];
    public List<DadAccountKey> AccountKeys { get; set; } = [];
    public DadRosterVisibility Visibility { get; set; } = DadRosterVisibility.Active;
    public string Reason { get; set; } = string.Empty;
}

public sealed class DadPeerRosterCatalogResponse
{
    public string RequestId { get; set; } = string.Empty;
    public DateTime RespondedAtUtc { get; set; } = DateTime.UtcNow;
    public string ClientInstanceId { get; set; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public DadAccountRosterCatalog Catalog { get; set; } = new();
    public List<string> Warnings { get; set; } = [];
}

public sealed class DadRosterRefreshCommandDto
{
    public string CommandId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public bool SaveAfterRefresh { get; set; } = true;
    public bool DryRun { get; set; }
}

public sealed class DadRosterRefreshResultDto
{
    public string CommandId { get; set; } = string.Empty;
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public bool Accepted { get; set; }
    public bool Success { get; set; }
    public bool DryRun { get; set; }
    public DateTime? RefreshedAtUtc { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DadXadbStatus XadbStatus { get; set; } = new();
    public DadParticipantSnapshot Snapshot { get; set; } = new();
}

public sealed class DadScheduledCrewJob
{
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");
    public DadSchedulerJobType JobType { get; set; } = DadSchedulerJobType.ScheduledPreset;
    public string GroupId { get; set; } = string.Empty;
    public string PresetName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool DryRun { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? NextEligibleTimeUtc { get; set; }
    public TimeSpan Cadence { get; set; } = TimeSpan.Zero;
    public string RequestedBy { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string MapRunTemplate { get; set; } = string.Empty;
    public DadMapCrewJobMode MapMode { get; set; } = DadMapCrewJobMode.ManualMapReady;
    public List<DadAccountKey> TargetAccountKeys { get; set; } = [];
    public List<DadCharacterKey> TargetCharacterKeys { get; set; } = [];
    public string StatusSummary { get; set; } = string.Empty;
    public string BlockedReason { get; set; } = string.Empty;

    public DadScheduledCrewJob Clone()
        => new()
        {
            JobId = JobId,
            JobType = JobType,
            GroupId = GroupId,
            PresetName = PresetName,
            Enabled = Enabled,
            DryRun = DryRun,
            CreatedAtUtc = CreatedAtUtc,
            NextEligibleTimeUtc = NextEligibleTimeUtc,
            Cadence = Cadence,
            RequestedBy = RequestedBy,
            Priority = Priority,
            MapRunTemplate = MapRunTemplate,
            MapMode = MapMode,
            TargetAccountKeys = [..TargetAccountKeys],
            TargetCharacterKeys = [..TargetCharacterKeys],
            StatusSummary = StatusSummary,
            BlockedReason = BlockedReason,
        };
}

public sealed class DadSchedulerQueueSnapshot
{
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string Summary { get; set; } = "Scheduler queue idle.";
    public string ActiveQueueOwner { get; set; } = string.Empty;
    public DadScheduledCrewJob? ActiveJob { get; set; }
    public List<DadScheduledCrewJob> PendingJobs { get; set; } = [];
    public DadSchedulerPresetState ActiveState { get; set; } = new();
}

public sealed class DadScheduledPresetRequest
{
    public string GroupId { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;
    public int CadenceHours { get; set; }
    public DateTime? NextEligibleTimeUtc { get; set; }
    public DadSchedulerJobType JobType { get; set; } = DadSchedulerJobType.ScheduledPreset;
    public DadMapCrewJobMode MapMode { get; set; } = DadMapCrewJobMode.ManualMapReady;
    public string MapRunTemplate { get; set; } = string.Empty;
    public List<DadCharacterKey> TargetCharacterKeys { get; set; } = [];
}

public sealed class DadCancelScheduledJobRequest
{
    public string JobId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
