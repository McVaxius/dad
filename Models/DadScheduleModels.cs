namespace dad.Models;

public enum DadScheduleCadence
{
    Manual = 0,
    DailyReset = 1,
}

public enum DadScheduleRunStatus
{
    Idle = 0,
    Running = 1,
    Completed = 2,
    Blocked = 3,
    Cancelled = 4,
}

public enum DadScheduleRunPhase
{
    Idle = 0,
    StartingEntry = 1,
    WaitingForScheduler = 2,
    WaitingForDadRun = 3,
    Completed = 4,
    Blocked = 5,
    Cancelled = 6,
}

internal readonly record struct DadScheduleRepeatBoundary(
    bool IsScheduleRun,
    int RepeatIteration,
    int RepeatCount)
{
    public static DadScheduleRepeatBoundary Standalone => new(false, 0, 0);

    public bool PreservePartyAfterCompletion =>
        IsScheduleRun &&
        RepeatIteration > 0 &&
        RepeatIteration < RepeatCount;

    public bool RequiresPartyTeardown => !PreservePartyAfterCompletion;
}

public sealed class DadScheduleEntry
{
    public string EntryId { get; set; } = Guid.NewGuid().ToString("N");
    public string GroupId { get; set; } = string.Empty;
    public string PresetName { get; set; } = string.Empty;
    public int RepeatCount { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DadScheduleEntry Normalize()
    {
        EntryId = string.IsNullOrWhiteSpace(EntryId) ? Guid.NewGuid().ToString("N") : EntryId.Trim();
        GroupId = GroupId?.Trim() ?? string.Empty;
        PresetName = PresetName?.Trim() ?? string.Empty;
        RepeatCount = Math.Clamp(
            RepeatCount <= 0 ? DadScheduleRules.MinRepeatCount : RepeatCount,
            DadScheduleRules.MinRepeatCount,
            DadScheduleRules.MaxRepeatCount);
        return this;
    }

    public DadScheduleEntry Clone()
        => new()
        {
            EntryId = EntryId,
            GroupId = GroupId,
            PresetName = PresetName,
            RepeatCount = RepeatCount,
            CreatedAtUtc = CreatedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
        };
}

public sealed class DadScheduleDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; } = 1;
    public string ScheduleId { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "Dad Schedule";
    public DadScheduleCadence Cadence { get; set; } = DadScheduleCadence.Manual;
    public List<DadScheduleEntry> Entries { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastDailyResetUtc { get; set; }
    public DateTime? LastRunStartedAtUtc { get; set; }
    public DateTime? LastRunCompletedAtUtc { get; set; }
    public DadScheduleRunStatus LastRunStatus { get; set; } = DadScheduleRunStatus.Idle;
    public string LastSummary { get; set; } = string.Empty;

    public DadScheduleDefinition Normalize()
    {
        SchemaVersion = Math.Max(1, SchemaVersion);
        Revision = Math.Max(1, Revision);
        ScheduleId = string.IsNullOrWhiteSpace(ScheduleId) ? Guid.NewGuid().ToString("N") : ScheduleId.Trim();
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? "Dad Schedule" : DisplayName.Trim();
        Entries ??= [];
        Entries = Entries
            .Where(static entry => entry != null)
            .Select(static entry => entry.Normalize())
            .ToList();
        LastSummary = LastSummary?.Trim() ?? string.Empty;
        return this;
    }

    public DadScheduleDefinition Clone()
        => new()
        {
            SchemaVersion = SchemaVersion,
            Revision = Revision,
            ScheduleId = ScheduleId,
            DisplayName = DisplayName,
            Cadence = Cadence,
            Entries = Entries?.Select(static entry => entry.Clone()).ToList() ?? [],
            CreatedAtUtc = CreatedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            LastDailyResetUtc = LastDailyResetUtc,
            LastRunStartedAtUtc = LastRunStartedAtUtc,
            LastRunCompletedAtUtc = LastRunCompletedAtUtc,
            LastRunStatus = LastRunStatus,
            LastSummary = LastSummary,
        };
}

public sealed class DadScheduleRunState
{
    public string RunId { get; set; } = string.Empty;
    public string ScheduleId { get; set; } = string.Empty;
    public string ScheduleName { get; set; } = string.Empty;
    public DadScheduleRunStatus Status { get; set; } = DadScheduleRunStatus.Idle;
    public DadScheduleRunPhase Phase { get; set; } = DadScheduleRunPhase.Idle;
    public string RequestedBy { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public bool ManualRun { get; set; }
    public DateTime? DailyResetUtc { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public string CurrentEntryId { get; set; } = string.Empty;
    public string CurrentGroupId { get; set; } = string.Empty;
    public string CurrentPresetName { get; set; } = string.Empty;
    public int CurrentEntryIndex { get; set; }
    public int RepeatIteration { get; set; } = 1;
    public int TotalEntryExecutions { get; set; }
    public int CompletedEntryExecutions { get; set; }
    public int SkippedEntryExecutions { get; set; }
    public string ActiveSchedulerJobId { get; set; } = string.Empty;
    public string ActivePlannerRequestId { get; set; } = string.Empty;
    public string Summary { get; set; } = "Schedule idle.";
    public string BlockedReason { get; set; } = string.Empty;

    public bool IsActive =>
        Status == DadScheduleRunStatus.Running &&
        Phase is DadScheduleRunPhase.StartingEntry
            or DadScheduleRunPhase.WaitingForScheduler
            or DadScheduleRunPhase.WaitingForDadRun;

    public DadScheduleRunState Clone()
        => new()
        {
            RunId = RunId,
            ScheduleId = ScheduleId,
            ScheduleName = ScheduleName,
            Status = Status,
            Phase = Phase,
            RequestedBy = RequestedBy,
            DryRun = DryRun,
            ManualRun = ManualRun,
            DailyResetUtc = DailyResetUtc,
            StartedAtUtc = StartedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            CompletedAtUtc = CompletedAtUtc,
            CurrentEntryId = CurrentEntryId,
            CurrentGroupId = CurrentGroupId,
            CurrentPresetName = CurrentPresetName,
            CurrentEntryIndex = CurrentEntryIndex,
            RepeatIteration = RepeatIteration,
            TotalEntryExecutions = TotalEntryExecutions,
            CompletedEntryExecutions = CompletedEntryExecutions,
            SkippedEntryExecutions = SkippedEntryExecutions,
            ActiveSchedulerJobId = ActiveSchedulerJobId,
            ActivePlannerRequestId = ActivePlannerRequestId,
            Summary = Summary,
            BlockedReason = BlockedReason,
        };

    public DadScheduleRunResult ToResult(bool success)
        => new()
        {
            RunId = RunId,
            ScheduleId = ScheduleId,
            ScheduleName = ScheduleName,
            Status = Status,
            Success = success,
            DryRun = DryRun,
            ManualRun = ManualRun,
            DailyResetUtc = DailyResetUtc,
            StartedAtUtc = StartedAtUtc,
            CompletedAtUtc = CompletedAtUtc ?? DateTime.UtcNow,
            TotalEntryExecutions = TotalEntryExecutions,
            CompletedEntryExecutions = CompletedEntryExecutions,
            SkippedEntryExecutions = SkippedEntryExecutions,
            Summary = Summary,
            BlockedReason = BlockedReason,
        };
}

public sealed class DadScheduleRunResult
{
    public string RunId { get; set; } = string.Empty;
    public string ScheduleId { get; set; } = string.Empty;
    public string ScheduleName { get; set; } = string.Empty;
    public DadScheduleRunStatus Status { get; set; } = DadScheduleRunStatus.Idle;
    public bool Success { get; set; }
    public bool DryRun { get; set; }
    public bool ManualRun { get; set; }
    public DateTime? DailyResetUtc { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CompletedAtUtc { get; set; } = DateTime.UtcNow;
    public int TotalEntryExecutions { get; set; }
    public int CompletedEntryExecutions { get; set; }
    public int SkippedEntryExecutions { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string BlockedReason { get; set; } = string.Empty;

    public DadScheduleRunResult Clone()
        => new()
        {
            RunId = RunId,
            ScheduleId = ScheduleId,
            ScheduleName = ScheduleName,
            Status = Status,
            Success = Success,
            DryRun = DryRun,
            ManualRun = ManualRun,
            DailyResetUtc = DailyResetUtc,
            StartedAtUtc = StartedAtUtc,
            CompletedAtUtc = CompletedAtUtc,
            TotalEntryExecutions = TotalEntryExecutions,
            CompletedEntryExecutions = CompletedEntryExecutions,
            SkippedEntryExecutions = SkippedEntryExecutions,
            Summary = Summary,
            BlockedReason = BlockedReason,
        };
}

public sealed class DadScheduleSnapshot
{
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string Summary { get; set; } = "No schedules configured.";
    public List<DadScheduleDefinition> Schedules { get; set; } = [];
    public DadScheduleRunState ActiveRun { get; set; } = new();
    public List<DadScheduleRunResult> RecentResults { get; set; } = [];
}

public sealed class DadScheduleStartRequest
{
    public string ScheduleId { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
}

public sealed class DadScheduleCancelRequest
{
    public string RunId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class DadScheduleCancelResult
{
    public string RunId { get; set; } = string.Empty;
    public bool Cancelled { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DadScheduleRunState ActiveRun { get; set; } = new();
}

public static class DadScheduleRules
{
    public const int MinRepeatCount = 1;
    public const int MaxRepeatCount = 99;
    public const int DailyResetHourUtc = 15;

    public static List<DadScheduleDefinition> NormalizeSchedules(IEnumerable<DadScheduleDefinition>? schedules)
        => (schedules ?? [])
            .Where(static schedule => schedule != null)
            .Select(static schedule => schedule.Normalize())
            .ToList();

    public static DadScheduleDefinition NormalizeSchedule(DadScheduleDefinition? schedule)
        => (schedule ?? new DadScheduleDefinition()).Normalize();

    public static DateTime GetDailyResetBoundaryUtc(DateTime nowUtc)
    {
        nowUtc = EnsureUtc(nowUtc);
        var todayReset = new DateTime(
            nowUtc.Year,
            nowUtc.Month,
            nowUtc.Day,
            DailyResetHourUtc,
            0,
            0,
            DateTimeKind.Utc);
        return nowUtc >= todayReset ? todayReset : todayReset.AddDays(-1);
    }

    public static DateTime GetNextDailyResetUtc(DateTime nowUtc)
    {
        var boundary = GetDailyResetBoundaryUtc(nowUtc);
        return EnsureUtc(nowUtc) < boundary ? boundary : boundary.AddDays(1);
    }

    public static bool IsDailyResetDue(DadScheduleDefinition schedule, DateTime nowUtc)
    {
        schedule.Normalize();
        if (schedule.Cadence != DadScheduleCadence.DailyReset || schedule.Entries.Count == 0)
            return false;

        var currentBoundary = GetDailyResetBoundaryUtc(nowUtc);
        return !schedule.LastDailyResetUtc.HasValue ||
               EnsureUtc(schedule.LastDailyResetUtc.Value) < currentBoundary;
    }

    public static DadScheduleRunState StartRun(
        DadScheduleDefinition schedule,
        bool dryRun,
        bool manualRun,
        string requestedBy,
        DateTime nowUtc)
    {
        schedule.Normalize();
        nowUtc = EnsureUtc(nowUtc);
        if (schedule.Entries.Count == 0)
        {
            return BlockRun(new DadScheduleRunState
            {
                RunId = Guid.NewGuid().ToString("N"),
                ScheduleId = schedule.ScheduleId,
                ScheduleName = schedule.DisplayName,
                RequestedBy = NormalizeRequester(requestedBy),
                DryRun = dryRun,
                ManualRun = manualRun,
                StartedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
                TotalEntryExecutions = 0,
            }, $"Schedule '{schedule.DisplayName}' has no entries.", nowUtc);
        }

        var state = new DadScheduleRunState
        {
            RunId = Guid.NewGuid().ToString("N"),
            ScheduleId = schedule.ScheduleId,
            ScheduleName = schedule.DisplayName,
            Status = DadScheduleRunStatus.Running,
            Phase = DadScheduleRunPhase.StartingEntry,
            RequestedBy = NormalizeRequester(requestedBy),
            DryRun = dryRun,
            ManualRun = manualRun,
            DailyResetUtc = manualRun ? null : GetDailyResetBoundaryUtc(nowUtc),
            StartedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            CurrentEntryIndex = 0,
            RepeatIteration = 1,
            TotalEntryExecutions = schedule.Entries.Sum(static entry => entry.Normalize().RepeatCount),
            CompletedEntryExecutions = 0,
            SkippedEntryExecutions = 0,
        };
        ApplyCurrentEntry(state, schedule.Entries[0]);
        state.Summary = BuildEntrySummary(state);
        return state;
    }

    public static string ValidateCurrentEntry(
        DadScheduleRunState state,
        IReadOnlyList<DadScheduleEntry> entries,
        IReadOnlySet<string> existingGroupIds)
    {
        var entry = GetCurrentEntry(state, entries);
        if (entry == null)
            return $"Schedule '{state.ScheduleName}' has no entry at index {state.CurrentEntryIndex + 1}.";

        if (string.IsNullOrWhiteSpace(entry.GroupId))
            return $"Schedule entry {state.CurrentEntryIndex + 1} has no saved preset.";

        return existingGroupIds.Contains(entry.GroupId)
            ? string.Empty
            : $"Schedule entry {state.CurrentEntryIndex + 1} references missing preset '{entry.GroupId}'.";
    }

    public static DadScheduleEntry? GetCurrentEntry(DadScheduleRunState state, IReadOnlyList<DadScheduleEntry> entries)
    {
        if (state.CurrentEntryIndex < 0 || state.CurrentEntryIndex >= entries.Count)
            return null;

        return entries[state.CurrentEntryIndex].Normalize();
    }

    internal static DadScheduleRepeatBoundary ResolveRepeatBoundary(
        string scheduleRunId,
        string scheduleEntryId,
        int repeatIteration,
        IEnumerable<DadScheduleEntry>? entries)
    {
        if (string.IsNullOrWhiteSpace(scheduleRunId) || string.IsNullOrWhiteSpace(scheduleEntryId))
            return DadScheduleRepeatBoundary.Standalone;

        var entry = (entries ?? [])
            .FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.EntryId, scheduleEntryId, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
            return DadScheduleRepeatBoundary.Standalone;

        entry.Normalize();
        return new DadScheduleRepeatBoundary(
            IsScheduleRun: true,
            RepeatIteration: Math.Clamp(repeatIteration, MinRepeatCount, entry.RepeatCount),
            RepeatCount: entry.RepeatCount);
    }

    public static DadScheduleRunState AdvanceAfterEntry(
        DadScheduleRunState source,
        IReadOnlyList<DadScheduleEntry> entries,
        bool entrySucceeded,
        string terminalSummary,
        DateTime nowUtc,
        bool entrySkipped = false)
    {
        var state = source.Clone();
        nowUtc = EnsureUtc(nowUtc);
        if (!entrySucceeded)
            return BlockRun(state, terminalSummary, nowUtc);

        var normalizedEntries = entries.Select(static entry => entry.Normalize()).ToList();
        if (normalizedEntries.Count == 0)
            return CompleteRun(state, terminalSummary, nowUtc);

        var currentIndex = Math.Clamp(state.CurrentEntryIndex, 0, normalizedEntries.Count - 1);
        var currentEntry = normalizedEntries[currentIndex];
        state.CompletedEntryExecutions = Math.Clamp(
            state.CompletedEntryExecutions + 1,
            0,
            Math.Max(state.TotalEntryExecutions, 1));
        if (entrySkipped)
        {
            state.SkippedEntryExecutions = Math.Clamp(
                state.SkippedEntryExecutions + 1,
                0,
                state.CompletedEntryExecutions);
        }

        if (state.RepeatIteration < currentEntry.RepeatCount)
        {
            state.RepeatIteration++;
        }
        else
        {
            currentIndex++;
            state.CurrentEntryIndex = currentIndex;
            state.RepeatIteration = 1;
        }

        state.ActiveSchedulerJobId = string.Empty;
        state.ActivePlannerRequestId = string.Empty;
        state.UpdatedAtUtc = nowUtc;

        if (currentIndex >= normalizedEntries.Count)
        {
            return CompleteRun(
                state,
                string.IsNullOrWhiteSpace(terminalSummary)
                    ? $"Schedule '{state.ScheduleName}' completed {state.CompletedEntryExecutions}/{state.TotalEntryExecutions} entry run(s)."
                    : terminalSummary,
                nowUtc);
        }

        ApplyCurrentEntry(state, normalizedEntries[currentIndex]);
        state.Status = DadScheduleRunStatus.Running;
        state.Phase = DadScheduleRunPhase.StartingEntry;
        state.CompletedAtUtc = null;
        state.BlockedReason = string.Empty;
        state.Summary = BuildEntrySummary(state);
        return state;
    }

    public static DadScheduleRunState CompleteRun(DadScheduleRunState source, string summary, DateTime nowUtc)
    {
        var state = source.Clone();
        nowUtc = EnsureUtc(nowUtc);
        state.Status = DadScheduleRunStatus.Completed;
        state.Phase = DadScheduleRunPhase.Completed;
        state.CompletedAtUtc = nowUtc;
        state.UpdatedAtUtc = nowUtc;
        state.ActiveSchedulerJobId = string.Empty;
        state.ActivePlannerRequestId = string.Empty;
        state.BlockedReason = string.Empty;
        state.Summary = string.IsNullOrWhiteSpace(summary) ? $"Schedule '{state.ScheduleName}' completed." : summary.Trim();
        return state;
    }

    public static DadScheduleRunState BlockRun(DadScheduleRunState source, string reason, DateTime nowUtc)
    {
        var state = source.Clone();
        nowUtc = EnsureUtc(nowUtc);
        state.Status = DadScheduleRunStatus.Blocked;
        state.Phase = DadScheduleRunPhase.Blocked;
        state.CompletedAtUtc = nowUtc;
        state.UpdatedAtUtc = nowUtc;
        state.BlockedReason = string.IsNullOrWhiteSpace(reason) ? "Schedule blocked." : reason.Trim();
        state.Summary = state.BlockedReason;
        return state;
    }

    public static DadScheduleRunState CancelRun(DadScheduleRunState source, string reason, DateTime nowUtc)
    {
        var state = source.Clone();
        nowUtc = EnsureUtc(nowUtc);
        state.Status = DadScheduleRunStatus.Cancelled;
        state.Phase = DadScheduleRunPhase.Cancelled;
        state.CompletedAtUtc = nowUtc;
        state.UpdatedAtUtc = nowUtc;
        state.BlockedReason = string.Empty;
        state.Summary = string.IsNullOrWhiteSpace(reason) ? "Schedule cancelled." : reason.Trim();
        return state;
    }

    public static string BuildEntrySummary(DadScheduleRunState state)
        => $"Schedule '{state.ScheduleName}' entry {state.CurrentEntryIndex + 1}, repeat {state.RepeatIteration}: {state.CurrentPresetName}.";

    private static void ApplyCurrentEntry(DadScheduleRunState state, DadScheduleEntry entry)
    {
        entry.Normalize();
        state.CurrentEntryId = entry.EntryId;
        state.CurrentGroupId = entry.GroupId;
        state.CurrentPresetName = string.IsNullOrWhiteSpace(entry.PresetName) ? entry.GroupId : entry.PresetName;
    }

    private static string NormalizeRequester(string requestedBy)
        => string.IsNullOrWhiteSpace(requestedBy) ? "schedule" : requestedBy.Trim();

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
