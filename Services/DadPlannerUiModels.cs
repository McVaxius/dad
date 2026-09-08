using dad.Models;
using dad.Services;
using System.Collections.Immutable;

namespace dad;

internal sealed record DadPlannerUiSnapshot
{
    public long Generation { get; init; }
    public DateTime RebuiltAtUtc { get; init; } = DateTime.UtcNow;
    public string RebuildReason { get; init; } = string.Empty;
    public DadCharacterPool CuratedPool { get; init; } = new();
    public DadActivityPreset PlannerPreview { get; init; } = new();
    public DadPlannerRunRequestPreview RequestPreview { get; init; } = new();
    public DadSchedulerPreview SchedulerPreview { get; init; } = new();
    public IReadOnlyList<DadRosterAccountOption> AccountOptions { get; init; } = [];
    public IReadOnlyList<DadLaunchProfile> LaunchProfiles { get; init; } = [];
    public IReadOnlyList<DadPlannerGroup> PlannerGroups { get; init; } = [];
    public DadPlannerGroup? SelectedGroup { get; init; }
    public IReadOnlyList<DadPlannerLanePreviewSnapshot> LanePreviews { get; init; } = [];
    public DadPlannerDutyOption? SelectedDuty { get; init; }
    public IReadOnlyList<DadPlannerRouletteOption> RouletteOptions { get; init; } = [];
    public DadPlannerRouletteOption? SelectedRoulette { get; init; }
    public DadRoulettePresetConflictIndex RouletteConflictIndex { get; init; } =
        DadRoulettePresetConflictRules.BuildIndex([]);
    public IReadOnlyDictionary<string, IReadOnlyList<DadAcquiredCharacter>> CharactersByAccountKey { get; init; } =
        new Dictionary<string, IReadOnlyList<DadAcquiredCharacter>>(StringComparer.OrdinalIgnoreCase);

    public DadPlannerLanePreviewSnapshot? GetLanePreview(DadPlannerActivityMode activityMode)
        => LanePreviews.FirstOrDefault(preview => preview.Lane.ActivityMode == activityMode);

    public IReadOnlyList<DadAcquiredCharacter> GetCharactersForAccount(DadAccountKey accountKey)
        => !accountKey.IsEmpty && CharactersByAccountKey.TryGetValue(accountKey.Value, out var characters)
            ? characters
            : [];
}

internal sealed record DadPlannerLanePreviewSnapshot(
    DadPlannerLaneDefinition Lane,
    bool IsSelected,
    DadPlannerRunRequestPreview RequestPreview);

internal sealed class DadPlannerUiCacheStats
{
    public long Generation { get; init; }
    public long HitCount { get; init; }
    public long MissCount { get; init; }
    public long SchedulerHitCount { get; init; }
    public long SchedulerMissCount { get; init; }
    public double LastRebuildMilliseconds { get; init; }
    public double MaxRebuildMilliseconds { get; init; }
    public DateTime LastRebuiltAtUtc { get; init; }
    public string LastRebuildReason { get; init; } = string.Empty;
}

internal readonly record struct DadPlannerUiCacheKey(
    long Generation,
    bool DebugUiEnabled,
    bool PluginEnabled,
    bool LocalOnlyModeEnabled,
    int CombatRotationMode,
    long RosterRevision,
    long CatalogRevision,
    long LaunchProfilesRevision,
    long TransportRevision);

internal readonly record struct DadPlannerSchedulerCacheKey(
    DadPlannerUiCacheKey HeavyweightKey,
    long SchedulerRevision,
    long RunRevision,
    long DependencyRevision);

internal sealed record DadPlannerRosterSemantic(
    bool XadbReady,
    int? XadbSnapshotVersion,
    DadOrderedSemantic<DadPlannerCharacterSemantic> Characters);

internal sealed record DadPlannerCharacterSemantic(
    string CharacterKey,
    ulong ContentId,
    string CharacterName,
    uint WorldId,
    string WorldName,
    uint? DataCenterId,
    string DataCenterName,
    string AccountId,
    string AccountAlias,
    DadCharacterSource Source,
    DadSnapshotFreshness Freshness,
    uint? CurrentJobId,
    string CurrentJobAbbrev,
    int? CurrentLevel,
    DadOrderedSemantic<KeyValuePair<uint, int>> JobLevels,
    uint? TerritoryId,
    string TerritoryName,
    int? PartyRosterCount,
    int? VisiblePartyCount,
    DadReadinessState Readiness,
    DadOrderedSemantic<string> Blockers,
    string SnapshotQuality,
    int? SnapshotVersion,
    bool XadbReady,
    DadRosterVisibility RosterVisibility,
    bool NeedsRosterUpdate,
    bool? MapEligible);

internal sealed record DadPlannerDependencySemantic(
    DadOrderedSemantic<DadPlannerDependencyEntrySemantic> Participants);

internal sealed record DadPlannerDependencyEntrySemantic(
    string WorkerSessionId,
    int SchemaVersion,
    long Revision,
    DadDependencyState AggregateState,
    bool IsReady);

internal sealed record DadPlannerRunSemantic(
    DadPlannerRunValueSemantic Local,
    DadPlannerRunValueSemantic Authority,
    DadPlannerRunValueSemantic Visible,
    bool IsRemoteAuthorityView,
    DadAuthorityViewKind AuthorityViewKind,
    bool HasRemoteAuthority);

internal sealed record DadPlannerRunValueSemantic(
    string RequestId,
    DadRunStatus Status,
    DadRunPhase Phase,
    DadModuleId ModuleId,
    DadRunCancellationState CancellationState,
    int CompletedTaskCount,
    int ActiveTaskIndex,
    int TotalTaskCount,
    string ActiveTaskName,
    string ActiveTaskStatus,
    string BlockedReason,
    string FailureReason,
    DadModuleId ExecutorModuleId,
    DadRunStatus ExecutorStatus,
    DadRunPhase ExecutorPhase,
    string ExecutorBlockedReason,
    int ParticipantCount,
    int StepResultCount);

internal sealed record DadPlannerValidationFeedback(
    long Generation,
    string GroupId,
    string Summary,
    string PlannerStatus,
    string SchedulerStatus,
    DateTime CheckedAtUtc);
