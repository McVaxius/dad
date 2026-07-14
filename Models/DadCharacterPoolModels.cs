namespace dad.Models;

public enum DadCharacterSource
{
    LocalRuntime,
    PeerRuntime,
    XadbOnly,
    ManualUnresolved,
}

public enum DadSnapshotFreshness
{
    Live,
    Recent,
    Stale,
    Unknown,
}

public enum DadReadinessState
{
    Unknown,
    Ready,
    Deferred,
    Blocked,
    Unavailable,
    Stale,
}

public enum DadPartyRole
{
    Any,
    Tank,
    Healer,
    Dps,
    Melee,
    PhysicalRanged,
    Caster,
    Limited,
}

public enum DadSlotAssignmentMode
{
    Auto,
    SpecificCharacter,
    SpecificRole,
    UnresolvedManual,
}

public enum DadQueueAuthority
{
    LocalOnly,
    Leader,
    DadDirect,
    LanParty,
    AuraFarmer,
    Mogtome,
    Blunderville,
}

public enum DadPlannerActivityMode
{
    Msq = 0,
    DutySupport = 1,
    Trust = 2,
    PremadeDuty = 3,
    DutyPremade = 4,
    DailyRoulette = 5,
    [Obsolete("Use DailyRoulette. This alias is retained for numeric/config compatibility only.")]
    DailyMsqPremade = DailyRoulette,
    Blunderville = 6,
    Mogtome = 7,
    Commendation = 8,
    Astrope = 9,
    LocalDuty = 10,
    CustomDuty = 11,
    DutySupportLeveling = 12,
    TrustLeveling = 13,
    Squadron = 14,
    VariantVvd = 15,
}

public enum DadPlannerRunFamily
{
    Msq = 0,
    LevelingNpc = 1,
    DutyFinder = 2,
    FarmLoops = 3,
    Event = 4,
    DailyRoulette = 5,
}

public enum DadPlannerStopMode
{
    AfterRuns,
    TargetLevel,
    ItemTarget, // feature batch A: stop when a target item reaches a target count
    RestedXpDepleted,
}

public enum DadPlannerOperatorMode
{
    RemotePartyPlan,
    TestOnThisMachine,
}

public enum DadTransportOwner
{
    DadDirect,
    LanParty,
    AuraFarmer,
    Mogtome,
    Blunderville,
    External,
}

public enum DadLaneMaturity
{
    MissingContract,
    PreviewOnly,
    LocalTestable,
    LiveReady,
    IntegrationDeferred,
}

public enum DadInviteAuthority
{
    NotNeeded,
    PresetLeader,
    ServerDad,
    External,
}

public enum DadAdsLootMode
{
    NoChange = 0,
    Need = 1,
    Greed = 2,
    Pass = 3,
}

public enum DadRosterSourceMode
{
    ConnectedAndXadb,
    ConnectedOnly,
    XadbOnly,
}

public sealed class DadAcquiredCharacter
{
    public string CharacterKey { get; set; } = string.Empty;
    public ulong ContentId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public uint WorldId { get; set; }
    public string WorldName { get; set; } = string.Empty;
    public uint? DataCenterId { get; set; }
    public string DataCenterName { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string AccountAlias { get; set; } = string.Empty;
    public DadCharacterSource Source { get; set; } = DadCharacterSource.LocalRuntime;
    public DadSnapshotFreshness Freshness { get; set; } = DadSnapshotFreshness.Unknown;
    public DateTime? LastSeenUtc { get; set; }
    public DateTime? XadbSnapshotUtc { get; set; }
    public uint? CurrentJobId { get; set; }
    public string CurrentJobAbbrev { get; set; } = string.Empty;
    public int? CurrentLevel { get; set; }
    public Dictionary<uint, int> JobLevels { get; set; } = [];
    public uint? TerritoryId { get; set; }
    public string TerritoryName { get; set; } = string.Empty;
    public int? PartyRosterCount { get; set; }
    public int? VisiblePartyCount { get; set; }
    public DadReadinessState Readiness { get; set; } = DadReadinessState.Unknown;
    public List<string> Blockers { get; set; } = [];
    public string SnapshotQuality { get; set; } = string.Empty;
    public int? SnapshotVersion { get; set; }
    public bool XadbReady { get; set; }
    public DadRosterVisibility RosterVisibility { get; set; } = DadRosterVisibility.Active;
    public bool NeedsRosterUpdate { get; set; }
    public bool? MapEligible { get; set; }
    public string MapEligibilitySummary { get; set; } = string.Empty;

    public bool IsLiveConnected =>
        Source is DadCharacterSource.LocalRuntime or DadCharacterSource.PeerRuntime
        && Freshness is DadSnapshotFreshness.Live or DadSnapshotFreshness.Recent
        && Readiness == DadReadinessState.Ready;

    public DadAcquiredCharacter Clone() => new()
    {
        CharacterKey = CharacterKey,
        ContentId = ContentId,
        CharacterName = CharacterName,
        WorldId = WorldId,
        WorldName = WorldName,
        DataCenterId = DataCenterId,
        DataCenterName = DataCenterName,
        AccountId = AccountId,
        AccountAlias = AccountAlias,
        Source = Source,
        Freshness = Freshness,
        LastSeenUtc = LastSeenUtc,
        XadbSnapshotUtc = XadbSnapshotUtc,
        CurrentJobId = CurrentJobId,
        CurrentJobAbbrev = CurrentJobAbbrev,
        CurrentLevel = CurrentLevel,
        JobLevels = new Dictionary<uint, int>(JobLevels),
        TerritoryId = TerritoryId,
        TerritoryName = TerritoryName,
        PartyRosterCount = PartyRosterCount,
        VisiblePartyCount = VisiblePartyCount,
        Readiness = Readiness,
        Blockers = [..Blockers],
        SnapshotQuality = SnapshotQuality,
        SnapshotVersion = SnapshotVersion,
        XadbReady = XadbReady,
        RosterVisibility = RosterVisibility,
        NeedsRosterUpdate = NeedsRosterUpdate,
        MapEligible = MapEligible,
        MapEligibilitySummary = MapEligibilitySummary,
    };
}

public sealed class DadPeerSnapshotRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IncludeXadbSummary { get; set; } = true;
    public bool IncludePartySnapshot { get; set; } = true;
    public bool IncludeJobLevels { get; set; } = true;
}

public sealed class DadPeerSnapshotResponse
{
    public string RequestId { get; set; } = string.Empty;
    public DateTime RespondedAtUtc { get; set; } = DateTime.UtcNow;
    public string ClientInstanceId { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public DadAcquiredCharacter Character { get; set; } = new();
    public DadParticipantSnapshot Participant { get; set; } = new();
    public bool XadbReady { get; set; }
    public int? XadbContractVersion { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public sealed class DadXadbStatus
{
    public bool IsReady { get; set; }
    public string Availability { get; set; } = "Unavailable";
    public DateTime? LastRefreshUtc { get; set; }
    public DateTime? LastSaveUtc { get; set; }
    public DateTime? SnapshotUtc { get; set; }
    public int? SnapshotVersion { get; set; }
    public string SnapshotQuality { get; set; } = string.Empty;
    public ulong ContentId { get; set; }
    public uint? WorldId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string WorldName { get; set; } = string.Empty;
    public uint? DataCenterId { get; set; }
    public string DataCenterName { get; set; } = string.Empty;
    public uint? CurrentJobId { get; set; }
    public string CurrentJobAbbrev { get; set; } = string.Empty;
    public int? CurrentLevel { get; set; }
    public Dictionary<uint, int> JobLevels { get; set; } = [];
    public string RawSummaryJson { get; set; } = string.Empty;
    public string LastStatus { get; set; } = "XADB not queried.";
    public List<string> Warnings { get; set; } = [];
}

public sealed class DadPeerTransportSnapshot
{
    public string Availability { get; set; } = "Unavailable";
    public int ProtocolVersion { get; set; }
    public int ConnectedPeerCount { get; set; }
    public int ReconnectAttempt { get; set; }
    public bool AuthorityRoutable { get; set; }
    public DateTime? LastInboundFrameUtc { get; set; }
    public DateTime? LastConnectedUtc { get; set; }
    public DateTime? LastDisconnectedUtc { get; set; }
    public DateTime? NextReconnectUtc { get; set; }
    public string LastDisconnectReason { get; set; } = string.Empty;
    public DateTime? LastRequestUtc { get; set; }
    public DadTransportMode TransportMode { get; set; } = DadTransportMode.LocalOnly;
    public string ConnectionStatus { get; set; } = "Disconnected";
    [Obsolete("Filesystem discovery was removed. This value remains empty.")]
    public string DiscoveryDirectory { get; set; } = string.Empty;
    public string ListenerEndpoint { get; set; } = string.Empty;
    public string ConfiguredEndpoint { get; set; } = string.Empty;
    public string AdvertisedEndpoint { get; set; } = string.Empty;
    public bool SharedSecretRequired { get; set; }
    public bool SharedSecretConfigured { get; set; }
    public string LastAuthOrProtocolError { get; set; } = string.Empty;
    public string HubRosterPublishEpochId { get; set; } = string.Empty;
    public long HubRosterPublishGeneration { get; set; }
    public int PublishedParticipantCount { get; set; }
    public int KnownParticipantCount { get; set; }
    public int PendingTransportEventCount { get; set; }
    public int PendingOutboundOperationCount { get; set; }
    public string LastRosterPublishReason { get; set; } = string.Empty;
    public DateTime? LastRosterPublishUtc { get; set; }
    public long CoalescedRosterPublishCount { get; set; }
    public string LastTransportTimeoutSummary { get; set; } = string.Empty;
    public string LocalClientInstanceId { get; set; } = string.Empty;
    public DadWorkerSessionId LocalWorkerSessionId { get; set; } = new(string.Empty);
    public DadWorkerSessionId AuthorityWorkerSessionId { get; set; } = new(string.Empty);
    public string AuthorityEndpoint { get; set; } = string.Empty;
    public DadWorkerRole AuthorityRole { get; set; } = DadWorkerRole.None;
    public string AuthorityStatus { get; set; } = "Authority not discovered.";
    public string LastRequestStatus { get; set; } = "Peer transport unavailable.";
    public List<DadParticipantSnapshot> KnownParticipants { get; set; } = [];
    public List<DadPeerSnapshotResponse> LastResponses { get; set; } = [];

    // B1: monotonic counter bumped whenever a fresh peer roster catalog (pull response or pushed projection)
    // lands. Plain fields so Interlocked.Increment(ref ...) is legal; the UI polls these to re-render itself.
    public long RosterCatalogCacheRevision;

    // B6: count of roster completion callbacks dropped because the framework queue was full (surfaced in
    // diagnostics; a drop also marks the publish dirty so the result is re-issued instead of silently lost).
    public long RosterCatalogDroppedCount;
}

public sealed class DadCharacterPool
{
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<DadAcquiredCharacter> Characters { get; set; } = [];
    public DadXadbStatus XadbStatus { get; set; } = new();
    public DadPeerTransportSnapshot PeerTransport { get; set; } = new();
    public string LastSummary { get; set; } = "Waiting for first Dad character snapshot.";
}

public sealed class DadPlannerFilterStats
{
    public int TotalCandidates { get; set; }
    public int CandidatesAfterFilters { get; set; }
    public int ExcludedByConnectedFilter { get; set; }
    public int ExcludedByStaleFilter { get; set; }
    public int ExcludedByDatacenterFilter { get; set; }
    public int ExcludedByAccountFilter { get; set; }
    public int ExcludedByLocalOnlyIsolation { get; set; }
    public int ExcludedByPeerEligibility { get; set; }
}

public sealed class DadPlannerAccountOption
{
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public string DisplayName { get; set; } = string.Empty;
    public int CharacterCount { get; set; }
}

public sealed class DadPresetPlannerOptions
{
    public string PresetName { get; set; } = "Duty Support Group";
    public string SelectedPlannerGroupId { get; set; } = string.Empty;
    public DadPlannerRunFamily RunFamily { get; set; } = DadPlannerRunFamily.LevelingNpc;
    public DadPlannerActivityMode ActivityMode { get; set; } = DadPlannerActivityMode.DutySupport;
    public string ActivityName { get; set; } = "Duty Support";
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
    public DadQueueTarget RouletteTarget { get; set; } = new()
    {
        Kind = DadQueueTargetKind.Roulette,
        Key = "MainScenario",
        DisplayName = "Main Scenario Roulette",
    };
    public string MogtomePreset { get; set; } = "Daily MSQ";
    public string MogtomeDutyPolicy { get; set; } = DadMogtomeDutyPolicies.PresetHandoff;
    public bool RefreshTrustNpcLevels { get; set; } = true;
    public DadRunStopPolicy StopPolicy { get; set; } = new();
    public DadCompletionActions? CompletionActions { get; set; }
    public List<DadAccountKey> IncludedAccountKeys { get; set; } = [];
}

public sealed class DadPlannerGroup
{
    public string GroupId { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "Dad Group";
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
    public DadQueueTarget RouletteTarget { get; set; } = new()
    {
        Kind = DadQueueTargetKind.Roulette,
        Key = "MainScenario",
        DisplayName = "Main Scenario Roulette",
    };
    public string MogtomePreset { get; set; } = "Daily MSQ";
    public string MogtomeDutyPolicy { get; set; } = DadMogtomeDutyPolicies.PresetHandoff;
    public bool RefreshTrustNpcLevels { get; set; } = true;
    public DadRunStopPolicy StopPolicy { get; set; } = new();
    // Dalamud configuration uses Newtonsoft.Json, while DAD IPC uses System.Text.Json.
    // Keep the unresolved share marker in the main config without changing IPC JSON.
    [System.Text.Json.Serialization.JsonIgnore]
    public string SharedStopTargetIdentityToken { get; set; } = string.Empty;
    public DadCompletionActions? CompletionActions { get; set; }
    public List<DadPlannerGroupSlot> Slots { get; set; } = [];
    // Feature batch B (dadfeatures20260620b line 56): a template is a reusable group whose slots are NOT
    // bound to specific characters; it is instantiated against the live roster by role on demand.
    public bool IsTemplate { get; set; }
    public bool ScheduleEnabled { get; set; }
    public int ScheduleCadenceHours { get; set; }
    public DateTime? NextEligibleTimeUtc { get; set; }
    public string ScheduleRequester { get; set; } = string.Empty;
    public int SchedulePriority { get; set; }
    public string MapRunTemplate { get; set; } = string.Empty;
    public DadMapCrewJobMode MapMode { get; set; } = DadMapCrewJobMode.ManualMapReady;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class DadPlannerGroupSlot
{
    public string SlotId { get; set; } = string.Empty;
    public bool IsSubstitute { get; set; }
    public DadPartyRole RequiredRole { get; set; } = DadPartyRole.Any;
    public DadAccountKey RequiredAccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey RequiredCharacterKey { get; set; } = new(string.Empty);
    public uint? RequiredJobId { get; set; }
    public DadAdsLootMode AdsLootMode { get; set; } = DadAdsLootMode.NoChange;
    public int? LevelSeekTarget { get; set; }
    public DadSchedulerWakePolicy WakePolicy { get; set; } = DadSchedulerWakePolicy.LaunchIfOffline;
    public string LaunchProfileId { get; set; } = string.Empty;
    public DadCharacterLoadInstruction CharacterLoadInstruction { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public DadSharedIdentityPlaceholder? SharedIdentity { get; set; }
    // Legacy config field only. Runtime fallback is driven by explicit IsSubstitute rows.
    public bool AllowSubstitution { get; set; } = true;
}

public sealed class DadPlannerGroupSummary
{
    public string GroupId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DadPlannerActivityMode ActivityMode { get; set; } = DadPlannerActivityMode.Msq;
    public string Lane { get; set; } = string.Empty;
    public int SlotCount { get; set; }
    public int RequiredAccountCount { get; set; }
    public int RequiredCharacterCount { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class DadPlannerGroupStartRequest
{
    public string GroupId { get; set; } = string.Empty;
    public string Lane { get; set; } = string.Empty;
    public uint? DutyContentFinderConditionId { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public bool DryRun { get; set; }
}

public sealed class DadPlannerLaneDefinition
{
    public DadPlannerActivityMode ActivityMode { get; set; } = DadPlannerActivityMode.Msq;
    public DadPlannerRunFamily RunFamily { get; set; } = DadPlannerRunFamily.Msq;
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public string DisplayName { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DadLaneMaturity Maturity { get; set; } = DadLaneMaturity.PreviewOnly;
    public string MaturityLabel { get; set; } = string.Empty;
    public string AccentColorHex { get; set; } = "#9CA3AF";
    public DadAuthorityMode DefaultAuthorityMode { get; set; } = DadAuthorityMode.ServerDad;
    public DadTransportOwner DefaultTransportOwner { get; set; } = DadTransportOwner.DadDirect;
    public DadQueueAuthority DefaultQueueAuthority { get; set; } = DadQueueAuthority.Leader;
    public int ExpectedPartySize { get; set; } = 1;
    public bool RequiresRemoteParty { get; set; }
    public bool RequiresDutySelector { get; set; }
    public bool RequiresRouletteSelector { get; set; }
    public bool UsesExternalHelper { get; set; }
    public string NextAction { get; set; } = string.Empty;
}

public sealed class DadActivityPreset
{
    public string PresetId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string SelectedPlannerGroupId { get; set; } = string.Empty;
    public string SelectedPlannerGroupName { get; set; } = string.Empty;
    public bool UsingPlannerGroup { get; set; }
    public DadPlannerRunFamily RunFamily { get; set; } = DadPlannerRunFamily.Msq;
    public string RunFamilyId { get; set; } = string.Empty;
    public DadPlannerActivityMode ActivityMode { get; set; } = DadPlannerActivityMode.Msq;
    public string ActivityModeId { get; set; } = string.Empty;
    public DadRunStopPolicy StopPolicy { get; set; } = new();
    public DadPlannerOperatorMode OperatorMode { get; set; } = DadPlannerOperatorMode.RemotePartyPlan;
    public string OperatorModeLabel { get; set; } = string.Empty;
    public DadTransportOwner TransportOwner { get; set; } = DadTransportOwner.DadDirect;
    public DadInviteAuthority InviteAuthority { get; set; } = DadInviteAuthority.NotNeeded;
    public DadRosterSourceMode RosterSource { get; set; } = DadRosterSourceMode.ConnectedAndXadb;
    public DadPlannerLaneDefinition LaneDefinition { get; set; } = new();
    public List<DadAcquiredCharacter> AvailableCharacters { get; set; } = [];
    public List<DadPresetCharacterSlot> SelectedCharacters { get; set; } = [];
    public DadQueueAuthority QueueAuthority { get; set; } = DadQueueAuthority.LocalOnly;
    public DadReadinessState ValidationState { get; set; } = DadReadinessState.Unknown;
    public string ValidationSummary { get; set; } = string.Empty;
    public string LeaderCharacterKey { get; set; } = string.Empty;
    public string LeaderStatusText { get; set; } = string.Empty;
    public string PreviewScope { get; set; } = string.Empty;
    public bool PreviewOnly { get; set; }
    public string AccountFilterSummary { get; set; } = "Any account";
    public string PlannerSummary { get; set; } = string.Empty;
    public string FilterSummary { get; set; } = string.Empty;
    public DadPlannerFilterStats FilterStats { get; set; } = new();
    public List<string> Notes { get; set; } = [];
    public List<string> Blockers { get; set; } = [];
    public bool CanSchedule { get; set; }
    public string ReadinessSummary { get; set; } = string.Empty;
    public List<string> StaticBlockers { get; set; } = [];
    public List<string> ReadinessBlockers { get; set; } = [];
    public List<string> ScheduleBlockers { get; set; } = [];
}

public sealed class DadPlannerRunRequestPreview
{
    public DadActivityPreset PlannerPreview { get; set; } = new();
    public DadRunRequest? Request { get; set; }
    public DadPlannerRequestContractPreview ContractPreview { get; set; } = new();
    public DadRunStopPolicy StopPolicy { get; set; } = new();
    public bool CanStart { get; set; }
    public bool CanSchedule { get; set; }
    public string ReadinessSummary { get; set; } = string.Empty;
    public List<string> StaticBlockers { get; set; } = [];
    public List<string> ReadinessBlockers { get; set; } = [];
    public List<string> ScheduleBlockers { get; set; } = [];
    public string StatusSummary { get; set; } = string.Empty;
    public string BlockedReason { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string ContractPreviewJson { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public DadQueueAuthority QueueAuthority { get; set; } = DadQueueAuthority.LocalOnly;
    public int ExpectedPartySize { get; set; }
    public List<DadCharacterKey> RequiredCharacterKeys { get; set; } = [];
    public List<DadAccountKey> RequiredAccountKeys { get; set; } = [];
    public List<DadModuleBlockerDto> ModuleBlockers { get; set; } = [];
}

public sealed class DadPresetCharacterSlot
{
    public string SlotId { get; set; } = string.Empty;
    public DadPartyRole RequiredRole { get; set; } = DadPartyRole.Any;
    public DadAccountKey RequiredAccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey RequiredCharacterKey { get; set; } = new(string.Empty);
    public uint? RequiredJobId { get; set; }
    public DadAdsLootMode AdsLootMode { get; set; } = DadAdsLootMode.NoChange;
    public int? LevelSeekTarget { get; set; }
    public DadSlotAssignmentMode AssignmentMode { get; set; } = DadSlotAssignmentMode.Auto;
    public ulong? ContentId { get; set; }
    public string CharacterKey { get; set; } = string.Empty;
    public bool AllowSubstitution { get; set; }
    public bool IsSubstitution { get; set; }
    public DadCharacterSource? SelectedSource { get; set; }
    public DadSnapshotFreshness SelectedFreshness { get; set; } = DadSnapshotFreshness.Unknown;
    public DadReadinessState SelectedReadiness { get; set; } = DadReadinessState.Unknown;
    public string AssignmentSummary { get; set; } = string.Empty;
    public string BlockerSummary { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
}
