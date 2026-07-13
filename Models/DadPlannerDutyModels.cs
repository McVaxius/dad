namespace dad.Models;

public sealed class DadPlannerDutyOption
{
    public uint ContentFinderConditionId { get; set; }
    public uint TerritoryType { get; set; }
    public string DutyDisplayName { get; set; } = string.Empty;
    public string ShortCode { get; set; } = string.Empty;
    public int QueueSize { get; set; } = 1;
    public int JobLevelRequired { get; set; }
    public int JobLevelSync { get; set; }
    public int ItemLevelRequired { get; set; }
    public int ItemLevelSync { get; set; }
    public bool FixedItemLevelSync { get; set; }
    public bool AllowUndersized { get; set; }
    public bool SupportsDutySupport { get; set; }
    public bool SupportsTrust { get; set; }
    public bool IsHighEndDuty { get; set; }
    public string SearchText { get; set; } = string.Empty;
    public string SelectionLabel { get; set; } = string.Empty;
    public string MetadataSummary { get; set; } = string.Empty;
}

public sealed class DadPlannerRequestContractPreview
{
    public string RequestId { get; set; } = string.Empty;
    public string Lane { get; set; } = string.Empty;
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public object? TaskConfig { get; set; }
    public DadRunStopPolicy StopPolicy { get; set; } = new();
    public DadCompletionActions? CompletionActions { get; set; }
    public List<DadCharacterKey> RequiredCharacterKeys { get; set; } = [];
    public List<DadAccountKey> RequiredAccountKeys { get; set; } = [];
    public int PartySize { get; set; }
    public DadAuthorityMode AuthorityMode { get; set; } = DadAuthorityMode.ServerDad;
    public DadQueueAuthority QueueAuthority { get; set; } = DadQueueAuthority.LocalOnly;
    public string Startability { get; set; } = string.Empty;
    public bool CanStart { get; set; }
    public bool CanSchedule { get; set; }
    public string ReadinessSummary { get; set; } = string.Empty;
    public List<string> StaticBlockers { get; set; } = [];
    public List<string> ReadinessBlockers { get; set; } = [];
    public List<string> ScheduleBlockers { get; set; } = [];
    public List<string> Blockers { get; set; } = [];
}
