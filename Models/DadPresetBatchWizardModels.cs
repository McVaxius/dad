namespace dad.Models;

public static class DadPresetBatchLimits
{
    public const int MaxAccountLanes = 8;
    public const int MaxPools = 16;
    public const int MaxTemplates = 8;
    public const int MaxCharactersPerRotatingLane = 512;
    public const int MaxPlannerGroups = 512;
    public const int MaxScheduleEntries = 512;
    public const int MaxTextLength = 128;
}

public sealed class DadPresetBatchDraft
{
    public List<DadPresetBatchRotatingLane> RotatingLanes { get; set; } = [];
    public List<DadPresetBatchAnchorLane> AnchorLanes { get; set; } = [];
    public List<DadPresetBatchPool> Pools { get; set; } = [];
    public List<DadPresetBatchTemplate> Templates { get; set; } = [];
    public bool CreateCombinedSchedule { get; set; }
    public string CombinedScheduleName { get; set; } = "Combined Daily Batch";
    public DadScheduleCadence CombinedScheduleCadence { get; set; } = DadScheduleCadence.Manual;

    public DadPresetBatchDraft Clone()
        => new()
        {
            RotatingLanes = RotatingLanes.Select(static lane => lane.Clone()).ToList(),
            AnchorLanes = AnchorLanes.Select(static lane => lane.Clone()).ToList(),
            Pools = Pools.Select(static pool => pool.Clone()).ToList(),
            Templates = Templates.Select(static template => template.Clone()).ToList(),
            CreateCombinedSchedule = CreateCombinedSchedule,
            CombinedScheduleName = CombinedScheduleName,
            CombinedScheduleCadence = CombinedScheduleCadence,
        };
}

public sealed class DadPresetBatchRotatingLane
{
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public List<DadRosterCharacterRef> Characters { get; set; } = [];

    public DadPresetBatchRotatingLane Clone()
        => new()
        {
            AccountKey = AccountKey,
            Characters = Characters.Select(static character => character.Clone()).ToList(),
        };
}

public sealed class DadPresetBatchAnchorLane
{
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public List<DadPresetBatchAnchorAssignment> Assignments { get; set; } = [];

    public DadPresetBatchAnchorLane Clone()
        => new()
        {
            AccountKey = AccountKey,
            Assignments = Assignments.Select(static assignment => assignment.Clone()).ToList(),
        };
}

public sealed class DadPresetBatchAnchorAssignment
{
    public string PoolId { get; set; } = string.Empty;
    public DadRosterCharacterRef Character { get; set; } = new();

    public DadPresetBatchAnchorAssignment Clone()
        => new()
        {
            PoolId = PoolId,
            Character = Character.Clone(),
        };
}

public sealed class DadPresetBatchPool
{
    public string PoolId { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "Data-center pool";
    public List<uint> DataCenterIds { get; set; } = [];
    public int CrewCount { get; set; } = 1;

    public DadPresetBatchPool Clone()
        => new()
        {
            PoolId = PoolId,
            DisplayName = DisplayName,
            DataCenterIds = [.. DataCenterIds],
            CrewCount = CrewCount,
        };
}

public sealed class DadPresetBatchTemplate
{
    public string PlannerGroupId { get; set; } = string.Empty;
    public string ActivityLabel { get; set; } = "Activity";
    public string PlanNameFormat { get; set; } = "{Activity} {Pool} {Index:00}";
    public string ScheduleName { get; set; } = string.Empty;
    public DadScheduleCadence ScheduleCadence { get; set; } = DadScheduleCadence.Manual;
    public int RepeatCount { get; set; } = 1;
    public bool SetDailyRewardChecksForAllPrimary { get; set; }

    public DadPresetBatchTemplate Clone()
        => new()
        {
            PlannerGroupId = PlannerGroupId,
            ActivityLabel = ActivityLabel,
            PlanNameFormat = PlanNameFormat,
            ScheduleName = ScheduleName,
            ScheduleCadence = ScheduleCadence,
            RepeatCount = RepeatCount,
            SetDailyRewardChecksForAllPrimary = SetDailyRewardChecksForAllPrimary,
        };
}

public sealed record DadPresetBatchCrew(
    string PoolId,
    string PoolName,
    int CrewIndex,
    IReadOnlyList<DadRosterCharacterRef> Characters);

public sealed record DadPresetBatchUnusedCount(
    string PoolId,
    DadAccountKey AccountKey,
    int SelectedCount,
    int UsedCount)
{
    public int UnusedCount => Math.Max(0, SelectedCount - UsedCount);
}

public enum DadPresetBatchIssueSeverity
{
    Warning = 0,
    Blocking = 1,
}

public sealed record DadPresetBatchIssue(
    string SafeCode,
    string Message,
    DadPresetBatchIssueSeverity Severity = DadPresetBatchIssueSeverity.Blocking)
{
    public bool IsBlocking => Severity == DadPresetBatchIssueSeverity.Blocking;
}

public sealed record DadPresetBatchPreview(
    string Fingerprint,
    string SourceConfigurationFingerprint,
    IReadOnlyList<DadPresetBatchCrew> Crews,
    IReadOnlyList<DadPresetBatchUnusedCount> UnusedCounts,
    IReadOnlyList<DadPlannerGroup> PlannerGroups,
    IReadOnlyList<DadScheduleDefinition> Schedules,
    IReadOnlyList<DadPresetBatchIssue> Issues)
{
    public bool CanApply => Issues.All(static issue => !issue.IsBlocking);
    public int WarningCount => Issues.Count(static issue => !issue.IsBlocking);
    public int BlockingCount => Issues.Count(static issue => issue.IsBlocking);
    public string Summary => CanApply
        ? $"Ready: {Crews.Count} crew(s), {PlannerGroups.Count} Plan(s), {Schedules.Count} Schedule(s), {WarningCount} warning(s)."
        : $"Blocked by {BlockingCount} issue(s); {WarningCount} warning(s).";
}

public sealed record DadPresetBatchMutationResult(
    bool Succeeded,
    string SafeCode,
    string Summary,
    string UndoToken = "",
    int PlannerGroupCount = 0,
    int ScheduleCount = 0);
