namespace dad.Models;

public enum DadLevelingJobOrder
{
    LowestFirst = 0,
    HighestBelowGoal = 1,
}

public sealed class DadLevelingDutyThreshold
{
    public int MinimumLevel { get; set; } = 1;
    public uint ContentFinderConditionId { get; set; }
    public string DutyDisplayName { get; set; } = string.Empty;

    public DadLevelingDutyThreshold Clone()
        => new()
        {
            MinimumLevel = MinimumLevel,
            ContentFinderConditionId = ContentFinderConditionId,
            DutyDisplayName = DutyDisplayName,
        };
}

public sealed class DadLevelingModeOptions
{
    public bool Enabled { get; set; }
    public int GoalLevel { get; set; } = DadRunStopPolicy.DefaultTargetLevel;
    public DadLevelingJobOrder JobOrder { get; set; } = DadLevelingJobOrder.LowestFirst;
    public List<DadLevelingDutyThreshold> DutyThresholds { get; set; } = [];

    public DadLevelingModeOptions Normalize()
    {
        GoalLevel = Math.Clamp(GoalLevel <= 0 ? DadRunStopPolicy.DefaultTargetLevel : GoalLevel, 1, 999);
        if (!Enum.IsDefined(JobOrder))
            JobOrder = DadLevelingJobOrder.LowestFirst;
        DutyThresholds ??= [];
        foreach (var threshold in DutyThresholds)
        {
            if (threshold != null)
                threshold.DutyDisplayName = threshold.DutyDisplayName?.Trim() ?? string.Empty;
        }
        return this;
    }

    public DadLevelingModeOptions Clone()
        => new()
        {
            Enabled = Enabled,
            GoalLevel = GoalLevel,
            JobOrder = JobOrder,
            DutyThresholds = (DutyThresholds ?? [])
                .Where(static threshold => threshold != null)
                .Select(static threshold => threshold.Clone())
                .ToList(),
        };
}

public sealed class DadLevelingJobDescriptor
{
    public uint JobId { get; set; }
    public string Abbreviation { get; set; } = string.Empty;
    public DadPartyRole Role { get; set; } = DadPartyRole.Any;
    public bool IsFullCombatJob { get; set; }
    public bool IsLimitedJob { get; set; }
}

public enum DadLevelingCompilationStatus
{
    Blocked = 0,
    Ready = 1,
    Complete = 2,
}

public sealed class DadLevelingSlotSelection
{
    public string SlotId { get; set; } = string.Empty;
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong ContentId { get; set; }
    public DadPartyRole Role { get; set; } = DadPartyRole.Any;
    public uint JobId { get; set; }
    public string JobAbbreviation { get; set; } = string.Empty;
    public int JobLevel { get; set; }
    public bool SlotComplete { get; set; }
    public bool IsFiller { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class DadLevelingCompilation
{
    public DadLevelingCompilationStatus Status { get; set; } = DadLevelingCompilationStatus.Blocked;
    public string Summary { get; set; } = string.Empty;
    public List<string> Blockers { get; set; } = [];
    public int Iteration { get; set; }
    public int PartyMinimumLevel { get; set; }
    public DadPlannerDutyOption? SelectedDuty { get; set; }
    public List<DadLevelingSlotSelection> Slots { get; set; } = [];
    public string ChildJobId { get; set; } = string.Empty;
    public string ChildRequestId { get; set; } = string.Empty;
    public DadPlannerGroup? ChildGroup { get; set; }

    public bool CanStartChild => Status == DadLevelingCompilationStatus.Ready &&
                                 ChildGroup != null &&
                                 !string.IsNullOrWhiteSpace(ChildJobId) &&
                                 !string.IsNullOrWhiteSpace(ChildRequestId);
}

public sealed class DadLevelingChildBuild
{
    public DadLevelingCompilation Compilation { get; set; } = new();
    public DadPlannerRunRequestPreview? PlannerPreview { get; set; }
}

public enum DadLevelingChildDisposition
{
    Waiting = 0,
    RefreshAndContinue = 1,
    CompleteDryRun = 2,
    Fail = 3,
    Cancel = 4,
}
