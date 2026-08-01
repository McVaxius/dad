namespace dad.Models;

public static class DadAutoPartyFleetLimits
{
    public const int MaxFleetRows = 160;
    public const int MaxCrewSets = 40;
    public const int MaxCrewMembers = 8;
    public const int MaxBlueprints = 40;
    public const int MaxGeneratedParties = 40;
    public const int MaxTextLength = 80;
    public const int MaxIdentifierLength = 64;
    public const int MaxTsvBytes = 256 * 1024;
}

public sealed class DadAutoPartyFleetConfiguration
{
    public bool Enabled { get; set; }
    public long Revision { get; set; } = 1;
    public List<DadAutoPartyFleetRow> Rows { get; set; } = [];
    public List<DadAutoPartyCrewSet> CrewSets { get; set; } = [];
    public List<DadAutoPartyFleetBlueprint> Blueprints { get; set; } = [];
    public List<string> ManagedPlannerGroupIds { get; set; } = [];
    public List<string> ManagedScheduleIds { get; set; } = [];
    public DadAutoPartyFleetUndoSnapshot? UndoSnapshot { get; set; }

    public DadAutoPartyFleetConfiguration Normalize()
    {
        Revision = Math.Max(1, Revision);
        Rows ??= [];
        CrewSets ??= [];
        Blueprints ??= [];
        ManagedPlannerGroupIds ??= [];
        ManagedScheduleIds ??= [];
        Rows = Rows.Where(static row => row != null)
            .Take(DadAutoPartyFleetLimits.MaxFleetRows)
            .Select(static row => row!.Normalize())
            .ToList();
        CrewSets = CrewSets.Where(static crew => crew != null)
            .Take(DadAutoPartyFleetLimits.MaxCrewSets)
            .Select(static crew => crew!.Normalize())
            .ToList();
        Blueprints = Blueprints.Where(static blueprint => blueprint != null)
            .Take(DadAutoPartyFleetLimits.MaxBlueprints)
            .Select(static blueprint => blueprint!.Normalize())
            .ToList();
        ManagedPlannerGroupIds = NormalizeIdentifiers(ManagedPlannerGroupIds, DadAutoPartyFleetLimits.MaxGeneratedParties);
        ManagedScheduleIds = NormalizeIdentifiers(ManagedScheduleIds, DadAutoPartyFleetLimits.MaxBlueprints);
        UndoSnapshot?.Normalize();
        return this;
    }

    public DadAutoPartyFleetConfiguration Clone()
        => new()
        {
            Enabled = Enabled,
            Revision = Revision,
            Rows = Rows?.Select(static row => row.Clone()).ToList() ?? [],
            CrewSets = CrewSets?.Select(static crew => crew.Clone()).ToList() ?? [],
            Blueprints = Blueprints?.Select(static blueprint => blueprint.Clone()).ToList() ?? [],
            ManagedPlannerGroupIds = ManagedPlannerGroupIds?.ToList() ?? [],
            ManagedScheduleIds = ManagedScheduleIds?.ToList() ?? [],
            UndoSnapshot = UndoSnapshot?.Clone(),
        };

    internal static string NormalizeIdentifier(string? value)
        => (value ?? string.Empty).Trim()[..Math.Min((value ?? string.Empty).Trim().Length, DadAutoPartyFleetLimits.MaxIdentifierLength)];

    internal static string NormalizeText(string? value)
        => (value ?? string.Empty).Trim()[..Math.Min((value ?? string.Empty).Trim().Length, DadAutoPartyFleetLimits.MaxTextLength)];

    internal static List<string> NormalizeIdentifiers(IEnumerable<string>? values, int maximum)
        => (values ?? [])
            .Select(NormalizeIdentifier)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maximum)
            .ToList();
}

public sealed class DadAutoPartyFleetRow
{
    public string RowId { get; set; } = string.Empty;
    public string OpaqueCharacterId { get; set; } = string.Empty;
    public string AccountKey { get; set; } = string.Empty;
    public string CharacterKey { get; set; } = string.Empty;
    public DadAllianceAssignment AllianceAssignment { get; set; } = DadAllianceAssignment.None;
    public DadPartyRole Role { get; set; } = DadPartyRole.Any;
    public uint JobId { get; set; }
    public bool IsRemote { get; set; }
    public bool Enabled { get; set; } = true;

    public DadAutoPartyFleetRow Normalize()
    {
        RowId = DadAutoPartyFleetConfiguration.NormalizeIdentifier(RowId);
        OpaqueCharacterId = DadAutoPartyFleetConfiguration.NormalizeIdentifier(OpaqueCharacterId);
        AccountKey = DadAutoPartyFleetConfiguration.NormalizeIdentifier(AccountKey);
        CharacterKey = DadAutoPartyFleetConfiguration.NormalizeIdentifier(CharacterKey);
        if (!Enum.IsDefined(AllianceAssignment))
            AllianceAssignment = DadAllianceAssignment.None;
        if (!Enum.IsDefined(Role))
            Role = DadPartyRole.Any;
        return this;
    }

    public DadAutoPartyFleetRow Clone()
        => new()
        {
            RowId = RowId,
            OpaqueCharacterId = OpaqueCharacterId,
            AccountKey = AccountKey,
            CharacterKey = CharacterKey,
            AllianceAssignment = AllianceAssignment,
            Role = Role,
            JobId = JobId,
            IsRemote = IsRemote,
            Enabled = Enabled,
        };
}

public sealed class DadAutoPartyCrewSet
{
    public string CrewSetId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> FleetRowIds { get; set; } = [];

    public DadAutoPartyCrewSet Normalize()
    {
        CrewSetId = DadAutoPartyFleetConfiguration.NormalizeIdentifier(CrewSetId);
        DisplayName = DadAutoPartyFleetConfiguration.NormalizeText(DisplayName);
        FleetRowIds = DadAutoPartyFleetConfiguration.NormalizeIdentifiers(FleetRowIds, DadAutoPartyFleetLimits.MaxCrewMembers);
        return this;
    }

    public DadAutoPartyCrewSet Clone()
        => new()
        {
            CrewSetId = CrewSetId,
            DisplayName = DisplayName,
            FleetRowIds = FleetRowIds?.ToList() ?? [],
        };
}

public sealed class DadAutoPartyFleetBlueprint
{
    public string BlueprintId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> CrewSetIds { get; set; } = [];
    public DadPlannerRunFamily RunFamily { get; set; } = DadPlannerRunFamily.DutyFinder;
    public DadPlannerActivityMode ActivityMode { get; set; } = DadPlannerActivityMode.PremadeDuty;
    public uint DutyContentFinderConditionId { get; set; }
    public string DutyDisplayName { get; set; } = string.Empty;
    public bool DutyUnsynced { get; set; }
    public bool CreateSchedule { get; set; } = true;
    public DadScheduleCadence ScheduleCadence { get; set; } = DadScheduleCadence.Manual;
    public int RepeatCount { get; set; } = 1;

    public DadAutoPartyFleetBlueprint Normalize()
    {
        BlueprintId = DadAutoPartyFleetConfiguration.NormalizeIdentifier(BlueprintId);
        DisplayName = DadAutoPartyFleetConfiguration.NormalizeText(DisplayName);
        DutyDisplayName = DadAutoPartyFleetConfiguration.NormalizeText(DutyDisplayName);
        CrewSetIds = DadAutoPartyFleetConfiguration.NormalizeIdentifiers(CrewSetIds, DadAutoPartyFleetLimits.MaxCrewSets);
        if (!Enum.IsDefined(RunFamily))
            RunFamily = DadPlannerRunFamily.DutyFinder;
        if (!Enum.IsDefined(ActivityMode))
            ActivityMode = DadPlannerActivityMode.PremadeDuty;
        if (!Enum.IsDefined(ScheduleCadence))
            ScheduleCadence = DadScheduleCadence.Manual;
        RepeatCount = Math.Clamp(RepeatCount, DadScheduleRules.MinRepeatCount, DadScheduleRules.MaxRepeatCount);
        return this;
    }

    public DadAutoPartyFleetBlueprint Clone()
        => new()
        {
            BlueprintId = BlueprintId,
            DisplayName = DisplayName,
            CrewSetIds = CrewSetIds?.ToList() ?? [],
            RunFamily = RunFamily,
            ActivityMode = ActivityMode,
            DutyContentFinderConditionId = DutyContentFinderConditionId,
            DutyDisplayName = DutyDisplayName,
            DutyUnsynced = DutyUnsynced,
            CreateSchedule = CreateSchedule,
            ScheduleCadence = ScheduleCadence,
            RepeatCount = RepeatCount,
        };
}

public sealed class DadAutoPartyFleetUndoSnapshot
{
    public string UndoToken { get; set; } = string.Empty;
    public long AppliedRevision { get; set; }
    public string AppliedStateFingerprint { get; set; } = string.Empty;
    public DateTime CapturedAtUtc { get; set; }
    public List<DadPlannerGroup> PlannerGroups { get; set; } = [];
    public List<DadScheduleDefinition> Schedules { get; set; } = [];

    public DadAutoPartyFleetUndoSnapshot Normalize()
    {
        UndoToken = DadAutoPartyFleetConfiguration.NormalizeIdentifier(UndoToken);
        AppliedRevision = Math.Max(1, AppliedRevision);
        AppliedStateFingerprint = DadAutoPartyConfiguration.NormalizeSha256(AppliedStateFingerprint);
        PlannerGroups ??= [];
        Schedules ??= [];
        return this;
    }

    public DadAutoPartyFleetUndoSnapshot Clone()
        => new()
        {
            UndoToken = UndoToken,
            AppliedRevision = AppliedRevision,
            AppliedStateFingerprint = AppliedStateFingerprint,
            CapturedAtUtc = CapturedAtUtc,
            PlannerGroups = PlannerGroups?.ToList() ?? [],
            Schedules = Schedules?.Select(static schedule => schedule.Clone()).ToList() ?? [],
        };
}

public sealed record DadAutoPartyFleetIssue(string SafeCode, string Message);

public sealed record DadAutoPartyFleetPreview(
    long SourceRevision,
    string Fingerprint,
    IReadOnlyList<DadPlannerGroup> PlannerGroups,
    IReadOnlyList<DadScheduleDefinition> Schedules,
    IReadOnlyList<DadAutoPartyFleetIssue> Issues)
{
    public bool CanApply => Issues.Count == 0;
    public string Summary => CanApply
        ? $"Ready: {PlannerGroups.Count} Plan(s), {Schedules.Count} Schedule(s)."
        : $"Blocked by {Issues.Count} Matrix issue(s).";
}

public sealed record DadAutoPartyFleetMutationResult(
    bool Succeeded,
    string SafeCode,
    string Summary,
    string UndoToken = "",
    string Fingerprint = "",
    int PlannerGroupCount = 0,
    int ScheduleCount = 0);

public sealed record DadAutoPartyFleetImportDraft(
    IReadOnlyList<DadAutoPartyFleetRow> Rows,
    IReadOnlyList<DadAutoPartyCrewSet> CrewSets);

public sealed record DadAutoPartyFleetImportResult(
    bool Succeeded,
    string SafeCode,
    string Summary,
    DadAutoPartyFleetImportDraft? Draft = null);
