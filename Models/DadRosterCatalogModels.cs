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
    LevelingOperation,
    LevelingChild,
}

public enum DadMapCrewJobMode
{
    ManualMapReady,
    GatherThenRun,
    PluginHandoff,
}

public sealed class DadAccountDataClearResult
{
    public int AccountConfigsCleared { get; set; }
    public int AccountConfigFilesDeleted { get; set; }
    public int AccountConfigDeleteFailures { get; set; }
    public int RosterKnownCharactersCleared { get; set; }
    public int RosterVisibilityCleared { get; set; }
    public int RosterRefreshHistoryCleared { get; set; }
    public int PlannerAccountRefsCleared { get; set; }
    public int PlannerGroupSlotRefsCleared { get; set; }
    public int LaunchProfileRefsCleared { get; set; }
    public int SchedulerJobsCleared { get; set; }
    public bool LastAccountIdCleared { get; set; }

    public void Merge(DadAccountDataClearResult other)
    {
        AccountConfigsCleared += other.AccountConfigsCleared;
        AccountConfigFilesDeleted += other.AccountConfigFilesDeleted;
        AccountConfigDeleteFailures += other.AccountConfigDeleteFailures;
        RosterKnownCharactersCleared += other.RosterKnownCharactersCleared;
        RosterVisibilityCleared += other.RosterVisibilityCleared;
        RosterRefreshHistoryCleared += other.RosterRefreshHistoryCleared;
        PlannerAccountRefsCleared += other.PlannerAccountRefsCleared;
        PlannerGroupSlotRefsCleared += other.PlannerGroupSlotRefsCleared;
        LaunchProfileRefsCleared += other.LaunchProfileRefsCleared;
        SchedulerJobsCleared += other.SchedulerJobsCleared;
        LastAccountIdCleared |= other.LastAccountIdCleared;
    }

    public string ToStatusMessage()
    {
        var lastAccount = LastAccountIdCleared ? "last account cleared" : "last account already empty";
        var failures = AccountConfigDeleteFailures == 0
            ? string.Empty
            : $", {AccountConfigDeleteFailures} account config delete failure(s)";
        return $"Cleared Dad account data: {AccountConfigFilesDeleted} account config file(s) deleted{failures}, {AccountConfigsCleared} in-memory account(s), roster known {RosterKnownCharactersCleared}, visibility {RosterVisibilityCleared}, refresh {RosterRefreshHistoryCleared}, planner refs {PlannerAccountRefsCleared}, group slot refs {PlannerGroupSlotRefsCleared}, launch refs {LaunchProfileRefsCleared}, scheduler jobs {SchedulerJobsCleared}; {lastAccount}. XADB snapshots untouched.";
    }
}

public sealed class DadRosterVisibilityRecord
{
    public string CharacterKey { get; set; } = string.Empty;
    public ulong ContentId { get; set; }
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadRosterVisibility Visibility { get; set; } = DadRosterVisibility.Active;
    public bool NeedsRosterUpdate { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = string.Empty;

    public DadRosterVisibilityRecord Clone()
        => new()
        {
            CharacterKey = CharacterKey,
            ContentId = ContentId,
            AccountKey = AccountKey,
            Visibility = Visibility,
            NeedsRosterUpdate = NeedsRosterUpdate,
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
    public List<DadRosterKnownCharacterRecord> KnownCharacters { get; set; } = [];
    public List<DadRosterVisibilityRecord> Visibility { get; set; } = [];
    public List<DadRosterRefreshRecord> RefreshHistory { get; set; } = [];
}

public sealed class DadRosterKnownCharacterRecord
{
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public string AccountAlias { get; set; } = string.Empty;
    public string CharacterKey { get; set; } = string.Empty;
    public ulong ContentId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public uint? WorldId { get; set; }
    public string WorldName { get; set; } = string.Empty;
    public uint? DataCenterId { get; set; }
    public string DataCenterName { get; set; } = string.Empty;
    public DateTime? LastSnapshotUtc { get; set; }
    public DateTime? LastRuntimeSeenUtc { get; set; }
    public Dictionary<uint, int> JobLevels { get; set; } = [];
    public uint? CurrentJobId { get; set; }
    public string CurrentJobAbbrev { get; set; } = string.Empty;
    public int? CurrentLevel { get; set; }
    public string SnapshotQuality { get; set; } = string.Empty;
    public int? SnapshotVersion { get; set; }
    public bool XadbReady { get; set; }
    public bool? MapEligible { get; set; }
    public string MapEligibilitySummary { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DadRosterKnownCharacterRecord Clone()
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
            JobLevels = new Dictionary<uint, int>(JobLevels),
            CurrentJobId = CurrentJobId,
            CurrentJobAbbrev = CurrentJobAbbrev,
            CurrentLevel = CurrentLevel,
            SnapshotQuality = SnapshotQuality,
            SnapshotVersion = SnapshotVersion,
            XadbReady = XadbReady,
            MapEligible = MapEligible,
            MapEligibilitySummary = MapEligibilitySummary,
            UpdatedAtUtc = UpdatedAtUtc,
        };
}

public sealed class DadRosterAccountOption
{
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public string AccountAlias { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string SourceClientInstanceId { get; set; } = string.Empty;
    public DadWorkerSessionId SourceWorkerSessionId { get; set; } = new(string.Empty);
    public bool IsLocal { get; set; }
    public bool OwnerOnline { get; set; }
    public int AssignedCharacterCount { get; set; }

    public DadRosterAccountOption Clone()
        => new()
        {
            AccountKey = AccountKey,
            AccountAlias = AccountAlias,
            DisplayName = DisplayName,
            SourceClientInstanceId = SourceClientInstanceId,
            SourceWorkerSessionId = SourceWorkerSessionId,
            IsLocal = IsLocal,
            OwnerOnline = OwnerOnline,
            AssignedCharacterCount = AssignedCharacterCount,
        };
}

public sealed class DadRosterRefreshRecord
{
    public string CharacterKey { get; set; } = string.Empty;
    public ulong ContentId { get; set; }
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RefreshedAtUtc { get; set; }
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;

    public DadRosterRefreshRecord Clone()
        => new()
        {
            CharacterKey = CharacterKey,
            ContentId = ContentId,
            AccountKey = AccountKey,
            RequestedAtUtc = RequestedAtUtc,
            RefreshedAtUtc = RefreshedAtUtc,
            Success = Success,
            Summary = Summary,
        };
}

public sealed class DadRosterCharacterRef
{
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong ContentId { get; set; }
    public uint? RequiredJobId { get; set; }
    public DadAdsLootMode? AdsLootMode { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string SharedIdentityToken { get; set; } = string.Empty;

    public bool IsEmpty =>
        AccountKey.IsEmpty &&
        CharacterKey.IsEmpty &&
        ContentId == 0 &&
        string.IsNullOrWhiteSpace(SharedIdentityToken);

    public DadRosterCharacterRef Clone()
        => new()
        {
            AccountKey = AccountKey,
            CharacterKey = CharacterKey,
            ContentId = ContentId,
            RequiredJobId = RequiredJobId,
            AdsLootMode = AdsLootMode,
            SharedIdentityToken = SharedIdentityToken,
        };
}

public static class DadRosterIdentity
{
    public static DadRosterCharacterRef From(DadRosterCharacter character)
        => new()
        {
            AccountKey = character.AccountKey,
            CharacterKey = character.CharacterKey,
            ContentId = character.ContentId,
        };

    public static DadRosterCharacterRef From(DadAcquiredCharacter character)
        => new()
        {
            AccountKey = ResolveAccountKey(character.AccountId, character.AccountAlias),
            CharacterKey = new DadCharacterKey(character.CharacterKey),
            ContentId = character.ContentId,
        };

    public static DadAccountKey ResolveAccountKey(string accountId, string accountAlias)
        => !string.IsNullOrWhiteSpace(accountId)
            ? new DadAccountKey(accountId.Trim())
            : !string.IsNullOrWhiteSpace(accountAlias)
                ? new DadAccountKey(accountAlias.Trim())
                : new DadAccountKey(string.Empty);

    public static string BuildKey(DadRosterCharacterRef reference)
    {
        var sharedIdentityToken = Normalize(reference.SharedIdentityToken);
        return sharedIdentityToken.Length > 0
            ? $"shared:{sharedIdentityToken}"
            : BuildKey(reference.AccountKey, reference.CharacterKey, reference.ContentId);
    }

    public static string BuildKey(DadRosterCharacter character)
        => BuildKey(character.AccountKey, character.CharacterKey, character.ContentId);

    public static string BuildKey(DadAcquiredCharacter character)
        => BuildKey(From(character));

    public static string BuildKey(DadAccountKey accountKey, DadCharacterKey characterKey, ulong contentId)
    {
        var accountPart = Normalize(accountKey.Value);
        if (contentId != 0)
            return $"acct:{accountPart}|cid:{contentId}";

        return $"acct:{accountPart}|key:{Normalize(characterKey.Value)}";
    }

    public static bool Matches(DadRosterCharacter character, DadRosterCharacterRef reference)
    {
        if (reference.IsEmpty)
            return false;

        if (!SameAccount(character.AccountKey, reference.AccountKey))
            return false;

        return SameCharacter(character.CharacterKey, character.ContentId, reference.CharacterKey, reference.ContentId);
    }

    public static bool SameRow(DadRosterCharacter left, DadRosterCharacter right)
        => SameAccount(left.AccountKey, right.AccountKey)
           && SameCharacter(left.CharacterKey, left.ContentId, right.CharacterKey, right.ContentId);

    public static bool SameAccount(DadAccountKey left, DadAccountKey right)
        => string.Equals(Normalize(left.Value), Normalize(right.Value), StringComparison.OrdinalIgnoreCase);

    public static bool SameCharacter(DadCharacterKey leftKey, ulong leftContentId, DadCharacterKey rightKey, ulong rightContentId)
    {
        if (leftContentId != 0 && rightContentId != 0)
            return leftContentId == rightContentId;

        return !leftKey.IsEmpty
               && !rightKey.IsEmpty
               && string.Equals(leftKey.Value, rightKey.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim();
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
    public bool NeedsRosterUpdate { get; set; }
    public bool? MapEligible { get; set; }
    public string MapEligibilitySummary { get; set; } = string.Empty;
    public DadRosterVisibility Visibility { get; set; } = DadRosterVisibility.Active;
    public DadCharacterSource Source { get; set; } = DadCharacterSource.XadbOnly;
    public string SourceClientInstanceId { get; set; } = string.Empty;
    public DadWorkerSessionId SourceWorkerSessionId { get; set; } = new(string.Empty);
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
            NeedsRosterUpdate = NeedsRosterUpdate,
            MapEligible = MapEligible,
            MapEligibilitySummary = MapEligibilitySummary,
            Visibility = Visibility,
            Source = Source,
            SourceClientInstanceId = SourceClientInstanceId,
            SourceWorkerSessionId = SourceWorkerSessionId,
            Blockers = [..Blockers],
            Warnings = [..Warnings],
        };
}

public sealed class DadRosterSourceDiagnostics
{
    public string LocalAccountKey { get; set; } = string.Empty;
    public int XadbPayloadRows { get; set; }
    public int XadbSnapshotRows { get; set; }
    public int XadbLegacyRows { get; set; }
    public int XadbMergedRows { get; set; }
    public Dictionary<string, int> XadbDataCenterCounts { get; set; } = [];
    public Dictionary<string, int> XadbWorldCounts { get; set; } = [];
    public int LocalXadbAttributedRows { get; set; }
    public int KnownRosterRows { get; set; }
    public int LocalRuntimeRows { get; set; }
    public int FinalLocalRows { get; set; }
    public int PeerCatalogCount { get; set; }
    public int PeerFullRosterCount { get; set; }
    public int PeerFullRosterRows { get; set; }
    public List<string> Warnings { get; set; } = [];

    public DadRosterSourceDiagnostics Clone()
        => new()
        {
            LocalAccountKey = LocalAccountKey,
            XadbPayloadRows = XadbPayloadRows,
            XadbSnapshotRows = XadbSnapshotRows,
            XadbLegacyRows = XadbLegacyRows,
            XadbMergedRows = XadbMergedRows,
            XadbDataCenterCounts = new Dictionary<string, int>(XadbDataCenterCounts),
            XadbWorldCounts = new Dictionary<string, int>(XadbWorldCounts),
            LocalXadbAttributedRows = LocalXadbAttributedRows,
            KnownRosterRows = KnownRosterRows,
            LocalRuntimeRows = LocalRuntimeRows,
            FinalLocalRows = FinalLocalRows,
            PeerCatalogCount = PeerCatalogCount,
            PeerFullRosterCount = PeerFullRosterCount,
            PeerFullRosterRows = PeerFullRosterRows,
            Warnings = [..Warnings],
        };
}

public sealed class DadAccountRosterCatalog
{
    public int Version { get; set; } = 1;
    public int? XadbContractVersion { get; set; }
    public int XadbPayloadRowCount { get; set; }
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string SourceClientInstanceId { get; set; } = string.Empty;
    public DadWorkerSessionId SourceWorkerSessionId { get; set; } = new(string.Empty);
    public bool IsFullRosterAvailable { get; set; }
    public bool IsLiveConnectedCatalog { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<DadRosterAccountOption> Accounts { get; set; } = [];
    public List<DadRosterCharacter> Characters { get; set; } = [];
    public List<DadRosterVisibilityRecord> Visibility { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public DadRosterSourceDiagnostics SourceDiagnostics { get; set; } = new();

    public DadAccountRosterCatalog Clone()
        => new()
        {
            Version = Version,
            XadbContractVersion = XadbContractVersion,
            XadbPayloadRowCount = XadbPayloadRowCount,
            GeneratedAtUtc = GeneratedAtUtc,
            SourceClientInstanceId = SourceClientInstanceId,
            SourceWorkerSessionId = SourceWorkerSessionId,
            IsFullRosterAvailable = IsFullRosterAvailable,
            IsLiveConnectedCatalog = IsLiveConnectedCatalog,
            Summary = Summary,
            Accounts = Accounts.Select(static account => account.Clone()).ToList(),
            Characters = Characters.Select(static character => character.Clone()).ToList(),
            Visibility = Visibility.Select(static record => record.Clone()).ToList(),
            Warnings = [..Warnings],
            SourceDiagnostics = SourceDiagnostics.Clone(),
        };
}

public sealed class DadRosterRefreshPlan
{
    public string PlanId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public bool ForcePeerRefresh { get; set; }
    public bool LiveConnectedOnly { get; set; }
    public bool IncludeHidden { get; set; }
    public bool IncludeIgnored { get; set; }
    public int StaleAfterHours { get; set; } = 72;
    public List<DadRosterCharacterRef> CharacterRefs { get; set; } = [];
    public List<DadAccountKey> AccountKeys { get; set; } = [];
    public List<DadCharacterKey> CharacterKeys { get; set; } = [];
    public bool DryRun { get; set; }
    public bool LogDiagnostics { get; set; }
    public string DiagnosticsReason { get; set; } = string.Empty;

    public static DadRosterRefreshPlan ConnectedDads(string diagnosticsReason = "", bool logDiagnostics = true)
        => new()
        {
            ForcePeerRefresh = false,
            IncludeHidden = true,
            IncludeIgnored = true,
            LiveConnectedOnly = true,
            LogDiagnostics = logDiagnostics,
            DiagnosticsReason = diagnosticsReason,
        };
}

public sealed class DadRosterVisibilityChangeRequest
{
    public List<DadRosterCharacterRef> CharacterRefs { get; set; } = [];
    public List<DadCharacterKey> CharacterKeys { get; set; } = [];
    public List<DadAccountKey> AccountKeys { get; set; } = [];
    public DadRosterVisibility Visibility { get; set; } = DadRosterVisibility.Active;
    public string Reason { get; set; } = string.Empty;
}

public sealed class DadRosterAssignmentChangeRequest
{
    public DadRosterCharacterRef CharacterRef { get; set; } = new();
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public string AccountAlias { get; set; } = string.Empty;
    public bool ClearAssignment { get; set; }
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

public sealed class DadAggregateRosterCatalogRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public DadWorkerSessionId RequestingWorkerSessionId { get; set; } = new(string.Empty);
    public bool IncludeRequester { get; set; }
    public DadRosterRefreshPlan Plan { get; set; } = new();
}

public sealed class DadAggregateRosterCatalogResponse
{
    public string RequestId { get; set; } = string.Empty;
    public DateTime RespondedAtUtc { get; set; } = DateTime.UtcNow;
    public int ExpectedCatalogCount { get; set; }
    public int RespondedCatalogCount { get; set; }
    public int PendingCatalogCount { get; set; }
    public int TimedOutCatalogCount { get; set; }
    public bool Complete { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<DadPeerRosterCatalogResponse> Responses { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class DadRosterRefreshCommandDto
{
    public string CommandId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong ContentId { get; set; }
    public bool SaveAfterRefresh { get; set; } = true;
    public bool DryRun { get; set; }
}

public sealed class DadRosterRefreshResultDto
{
    public string CommandId { get; set; } = string.Empty;
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong ContentId { get; set; }
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
    public List<DadRosterCharacterRef> TargetCharacters { get; set; } = [];
    public List<DadAccountKey> TargetAccountKeys { get; set; } = [];
    public List<DadCharacterKey> TargetCharacterKeys { get; set; } = [];
    public string StatusSummary { get; set; } = string.Empty;
    public string BlockedReason { get; set; } = string.Empty;
    public string ScheduleId { get; set; } = string.Empty;
    public string ScheduleRunId { get; set; } = string.Empty;
    public string ScheduleEntryId { get; set; } = string.Empty;
    public int ScheduleEntryIndex { get; set; } = -1;
    public int ScheduleRepeatIteration { get; set; }
    public DadScheduleCadence ScheduleCadence { get; set; } = DadScheduleCadence.Manual;
    public string ParentOperationJobId { get; set; } = string.Empty;
    public int LevelingIteration { get; set; }

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
            TargetCharacters = TargetCharacters?.Select(static target => target.Clone()).ToList() ?? [],
            TargetAccountKeys = TargetAccountKeys == null ? [] : [..TargetAccountKeys],
            TargetCharacterKeys = TargetCharacterKeys == null ? [] : [..TargetCharacterKeys],
            StatusSummary = StatusSummary,
            BlockedReason = BlockedReason,
            ScheduleId = ScheduleId,
            ScheduleRunId = ScheduleRunId,
            ScheduleEntryId = ScheduleEntryId,
            ScheduleEntryIndex = ScheduleEntryIndex,
            ScheduleRepeatIteration = ScheduleRepeatIteration,
            ScheduleCadence = ScheduleCadence,
            ParentOperationJobId = ParentOperationJobId,
            LevelingIteration = LevelingIteration,
        };
}

public sealed class DadScheduledCrewJobResult
{
    public string JobId { get; set; } = string.Empty;
    public DadSchedulerJobType JobType { get; set; } = DadSchedulerJobType.ScheduledPreset;
    public string GroupId { get; set; } = string.Empty;
    public string PresetName { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CompletedAtUtc { get; set; } = DateTime.UtcNow;
    public DadSchedulerPresetPhase FinalPhase { get; set; } = DadSchedulerPresetPhase.Idle;
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string BlockedReason { get; set; } = string.Empty;
    public string ScheduleId { get; set; } = string.Empty;
    public string ScheduleRunId { get; set; } = string.Empty;
    public string ScheduleEntryId { get; set; } = string.Empty;
    public int ScheduleEntryIndex { get; set; } = -1;
    public int ScheduleRepeatIteration { get; set; }
    public DadScheduleCadence ScheduleCadence { get; set; } = DadScheduleCadence.Manual;
    public DadSchedulerSkipKind SkipKind { get; set; }
    public string ParentOperationJobId { get; set; } = string.Empty;
    public int LevelingIteration { get; set; }

    public DadScheduledCrewJobResult Clone()
        => new()
        {
            JobId = JobId,
            JobType = JobType,
            GroupId = GroupId,
            PresetName = PresetName,
            RequestedBy = RequestedBy,
            StartedAtUtc = StartedAtUtc,
            CompletedAtUtc = CompletedAtUtc,
            FinalPhase = FinalPhase,
            Success = Success,
            Summary = Summary,
            BlockedReason = BlockedReason,
            ScheduleId = ScheduleId,
            ScheduleRunId = ScheduleRunId,
            ScheduleEntryId = ScheduleEntryId,
            ScheduleEntryIndex = ScheduleEntryIndex,
            ScheduleRepeatIteration = ScheduleRepeatIteration,
            ScheduleCadence = ScheduleCadence,
            SkipKind = SkipKind,
            ParentOperationJobId = ParentOperationJobId,
            LevelingIteration = LevelingIteration,
        };
}

public sealed class DadSchedulerQueueSnapshot
{
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string Summary { get; set; } = "Scheduler queue idle.";
    public string ActiveQueueOwner { get; set; } = string.Empty;
    public DadScheduledCrewJob? ActiveJob { get; set; }
    public List<DadScheduledCrewJob> PendingJobs { get; set; } = [];
    public List<DadScheduledCrewJobResult> RecentResults { get; set; } = [];
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
    public List<DadRosterCharacterRef> TargetCharacters { get; set; } = [];
    public List<DadCharacterKey> TargetCharacterKeys { get; set; } = [];
}

public sealed class DadCancelScheduledJobRequest
{
    public string JobId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
