namespace dad.Models;

internal enum DadCrewFormationMode
{
    Unavailable = 0,
    RegularParty = 1,
    AlliancePartyFinder = 2,
}

internal enum DadCrewFormationPhase
{
    Idle = 0,
    Preparing = 1,
    StartingRegularParty = 2,
    RegularGroupReady = 3,
    CreatingAllianceListing = 4,
    GrabbingAlliance = 5,
    AllianceCleanup = 6,
    Disbanding = 7,
    Completed = 8,
    Blocked = 9,
    Cancelled = 10,
}

internal sealed class DadCrewFormationStatus
{
    public string RunId { get; set; } = string.Empty;
    public string SchedulerRunId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string SourceGroupId { get; set; } = string.Empty;
    public string SourcePresetName { get; set; } = string.Empty;
    public string EffectiveGroupId { get; set; } = string.Empty;
    public string EffectivePresetName { get; set; } = string.Empty;
    public string RecruitmentId { get; set; } = string.Empty;
    public DadCrewFormationMode Mode { get; set; }
    public DadCrewFormationPhase Phase { get; set; }
    public bool GrabRequested { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string Summary { get; set; } = "Crew Tools idle.";
    public string BlockedReason { get; set; } = string.Empty;

    public bool IsActive => Phase is DadCrewFormationPhase.Preparing
        or DadCrewFormationPhase.StartingRegularParty
        or DadCrewFormationPhase.RegularGroupReady
        or DadCrewFormationPhase.CreatingAllianceListing
        or DadCrewFormationPhase.GrabbingAlliance
        or DadCrewFormationPhase.AllianceCleanup
        or DadCrewFormationPhase.Disbanding;

    public DadCrewFormationStatus Clone()
        => new()
        {
            RunId = RunId,
            SchedulerRunId = SchedulerRunId,
            RequestId = RequestId,
            SourceGroupId = SourceGroupId,
            SourcePresetName = SourcePresetName,
            EffectiveGroupId = EffectiveGroupId,
            EffectivePresetName = EffectivePresetName,
            RecruitmentId = RecruitmentId,
            Mode = Mode,
            Phase = Phase,
            GrabRequested = GrabRequested,
            StartedAtUtc = StartedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            CompletedAtUtc = CompletedAtUtc,
            Summary = Summary,
            BlockedReason = BlockedReason,
        };
}

internal sealed class DadPartyDisbandPreflight
{
    public bool CanDisband { get; set; }
    public ulong LocalContentId { get; set; }
    public ulong LeaderContentId { get; set; }
    public bool IsCrossRealmParty { get; set; }
    public bool IsInDuty { get; set; }
    public bool IsQueued { get; set; }
    public bool IsWorldStable { get; set; }
    public List<ulong> MemberContentIds { get; set; } = [];
    public string LeaderName { get; set; } = string.Empty;
    public string BlockedReason { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;

    public DadPartyDisbandPreflight Clone()
        => new()
        {
            CanDisband = CanDisband,
            LocalContentId = LocalContentId,
            LeaderContentId = LeaderContentId,
            IsCrossRealmParty = IsCrossRealmParty,
            IsInDuty = IsInDuty,
            IsQueued = IsQueued,
            IsWorldStable = IsWorldStable,
            MemberContentIds = [..MemberContentIds],
            LeaderName = LeaderName,
            BlockedReason = BlockedReason,
            Summary = Summary,
        };
}

internal sealed class DadCrewToolsSnapshot
{
    public string SelectedPresetName { get; set; } = "(select a saved preset)";
    public string ResolvedPresetName { get; set; } = "(unresolved)";
    public DadCrewFormationMode ResolvedMode { get; set; }
    public DadCrewFormationStatus Formation { get; set; } = new();
    public DadPartyDisbandPreflight DisbandPreflight { get; set; } = new();
    public bool StandaloneDisbandActive { get; set; }
    public bool CanCreateGroup { get; set; }
    public bool CanDisband { get; set; }
    public string LiveState { get; set; } = "Idle";
    public string FirstBlocker { get; set; } = string.Empty;
    public string DisbandSummary { get; set; } = string.Empty;
}

internal enum DadAlliancePartyFinderActionSource
{
    Debug = 0,
    CrewFormation = 1,
}

internal readonly record struct DadAlliancePartyFinderActionContext(
    DadAlliancePartyFinderActionSource Source,
    string CrewFormationRunId)
{
    public static DadAlliancePartyFinderActionContext Debug
        => new(DadAlliancePartyFinderActionSource.Debug, string.Empty);

    public static DadAlliancePartyFinderActionContext CrewFormation(string runId)
        => new(DadAlliancePartyFinderActionSource.CrewFormation, runId?.Trim() ?? string.Empty);
}
