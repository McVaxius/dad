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

public enum DadScheduleFailureKind
{
    None = 0,
    PreStartRejected = 1,
    EntryTerminalFailure = 2,
    CoordinatorReloadAbandonment = 3,
    MissingOrUnknownLeaderState = 4,
    SchedulerStateDisappeared = 5,
    Cancellation = 6,
    ScheduleRevisionChanged = 7,
    EntryIdentityChanged = 8,
    Unknown = 9,
}

public enum DadScheduleAttachmentDisposition
{
    Added = 0,
    AlreadyPresent = 1,
    ScheduleMissing = 2,
    MutationLocked = 3,
    InvalidPlan = 4,
}

public sealed class DadScheduleAttachmentResult
{
    public DadScheduleAttachmentDisposition Disposition { get; set; }
    public string ScheduleId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public bool Added => Disposition == DadScheduleAttachmentDisposition.Added;
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
    public DadScheduleFailureKind FailureKind { get; set; }
    public long ScheduleRevisionAtStart { get; set; }
    public string RetriedFromRunId { get; set; } = string.Empty;

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
            FailureKind = FailureKind,
            ScheduleRevisionAtStart = ScheduleRevisionAtStart,
            RetriedFromRunId = RetriedFromRunId,
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
            FailureKind = FailureKind,
            ScheduleRevisionAtStart = ScheduleRevisionAtStart,
            RetriedFromRunId = RetriedFromRunId,
            CurrentEntryId = CurrentEntryId,
            CurrentGroupId = CurrentGroupId,
            CurrentPresetName = CurrentPresetName,
            CurrentEntryIndex = CurrentEntryIndex,
            RepeatIteration = RepeatIteration,
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
    public DadScheduleFailureKind FailureKind { get; set; }
    public long ScheduleRevisionAtStart { get; set; }
    public string RetriedFromRunId { get; set; } = string.Empty;
    public string CurrentEntryId { get; set; } = string.Empty;
    public string CurrentGroupId { get; set; } = string.Empty;
    public string CurrentPresetName { get; set; } = string.Empty;
    public int CurrentEntryIndex { get; set; }
    public int RepeatIteration { get; set; } = 1;

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
            FailureKind = FailureKind,
            ScheduleRevisionAtStart = ScheduleRevisionAtStart,
            RetriedFromRunId = RetriedFromRunId,
            CurrentEntryId = CurrentEntryId,
            CurrentGroupId = CurrentGroupId,
            CurrentPresetName = CurrentPresetName,
            CurrentEntryIndex = CurrentEntryIndex,
            RepeatIteration = RepeatIteration,
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

public sealed class DadScheduleRetryRequest
{
    public string FailedRunId { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
}

public sealed class DadScheduleRetryResult
{
    public string FailedRunId { get; set; } = string.Empty;
    public bool Eligible { get; set; }
    public bool Retried { get; set; }
    public DadScheduleFailureKind FailureKind { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DadScheduleRunState ActiveRun { get; set; } = new();
}

public static class DadScheduleRules
{
    public const int MinRepeatCount = 1;
    public const int MaxRepeatCount = 99;
    public const int DailyResetHourUtc = 15;
    public const string DuplicatePlanAttachmentMessage = "Maybe you should have hit Duplicate first before making this version. This one has the same ID as an existing Plan in the schedule already.";

    public static DadScheduleAttachmentResult AttachSavedPlan(
        DadScheduleDefinition schedule,
        DadPlannerGroup group,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(group);
        schedule.Entries ??= [];
        var groupId = group.GroupId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return new DadScheduleAttachmentResult
            {
                Disposition = DadScheduleAttachmentDisposition.InvalidPlan,
                ScheduleId = schedule.ScheduleId,
                Summary = "The saved Plan has no stable ID and cannot be attached.",
            };
        }

        if (schedule.Entries.Any(entry =>
                string.Equals(entry.GroupId?.Trim(), groupId, StringComparison.OrdinalIgnoreCase)))
        {
            return new DadScheduleAttachmentResult
            {
                Disposition = DadScheduleAttachmentDisposition.AlreadyPresent,
                ScheduleId = schedule.ScheduleId,
                GroupId = groupId,
                Summary = DuplicatePlanAttachmentMessage,
            };
        }

        var now = EnsureUtc(nowUtc);
        schedule.Entries.Add(new DadScheduleEntry
        {
            GroupId = groupId,
            PresetName = group.DisplayName?.Trim() ?? string.Empty,
            RepeatCount = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        schedule.Revision++;
        schedule.UpdatedAtUtc = now;
        return new DadScheduleAttachmentResult
        {
            Disposition = DadScheduleAttachmentDisposition.Added,
            ScheduleId = schedule.ScheduleId,
            GroupId = groupId,
            Summary = $"Added Plan '{group.DisplayName}' to Schedule '{schedule.DisplayName}' with repeat count 1.",
        };
    }

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
                ScheduleRevisionAtStart = schedule.Revision,
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
            DailyResetUtc = schedule.Cadence == DadScheduleCadence.DailyReset && !dryRun
                ? GetDailyResetBoundaryUtc(nowUtc)
                : null,
            StartedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            CurrentEntryIndex = 0,
            RepeatIteration = 1,
            TotalEntryExecutions = schedule.Entries.Sum(static entry => entry.Normalize().RepeatCount),
            CompletedEntryExecutions = 0,
            SkippedEntryExecutions = 0,
            ScheduleRevisionAtStart = schedule.Revision,
        };
        ApplyCurrentEntry(state, schedule.Entries[0]);
        state.Summary = BuildEntrySummary(state);
        return state;
    }

    public static bool UpdateOwnedDailyResetBoundary(
        DadScheduleDefinition schedule,
        DadScheduleRunState state,
        DateTime nowUtc)
    {
        if (schedule.Cadence != DadScheduleCadence.DailyReset ||
            state.DryRun ||
            !state.IsActive ||
            !state.DailyResetUtc.HasValue)
        {
            return false;
        }

        var changed = false;
        var ownedBoundary = EnsureUtc(state.DailyResetUtc.Value);
        var currentBoundary = GetDailyResetBoundaryUtc(nowUtc);
        if (currentBoundary > ownedBoundary)
        {
            ownedBoundary = currentBoundary;
            state.DailyResetUtc = ownedBoundary;
            changed = true;
        }

        if (!schedule.LastDailyResetUtc.HasValue ||
            EnsureUtc(schedule.LastDailyResetUtc.Value) < ownedBoundary)
        {
            schedule.LastDailyResetUtc = ownedBoundary;
            changed = true;
        }

        return changed;
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
        if (repeatIteration < MinRepeatCount || repeatIteration > entry.RepeatCount)
            return DadScheduleRepeatBoundary.Standalone;

        return new DadScheduleRepeatBoundary(
            IsScheduleRun: true,
            RepeatIteration: repeatIteration,
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
        state.FailureKind = DadScheduleFailureKind.None;
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
        state.FailureKind = DadScheduleFailureKind.None;
        state.Summary = string.IsNullOrWhiteSpace(summary) ? $"Schedule '{state.ScheduleName}' completed." : summary.Trim();
        return state;
    }

    public static DadScheduleRunState BlockRun(
        DadScheduleRunState source,
        string reason,
        DateTime nowUtc,
        DadScheduleFailureKind failureKind = DadScheduleFailureKind.Unknown)
    {
        var state = source.Clone();
        nowUtc = EnsureUtc(nowUtc);
        state.Status = DadScheduleRunStatus.Blocked;
        state.Phase = DadScheduleRunPhase.Blocked;
        state.CompletedAtUtc = nowUtc;
        state.UpdatedAtUtc = nowUtc;
        state.BlockedReason = string.IsNullOrWhiteSpace(reason) ? "Schedule blocked." : reason.Trim();
        state.FailureKind = failureKind;
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
        state.FailureKind = DadScheduleFailureKind.Cancellation;
        state.Summary = string.IsNullOrWhiteSpace(reason) ? "Schedule cancelled." : reason.Trim();
        return state;
    }

    public static string BuildEntrySummary(DadScheduleRunState state)
        => $"Schedule '{state.ScheduleName}' entry {state.CurrentEntryIndex + 1}, repeat {state.RepeatIteration}: {state.CurrentPresetName}.";

    public static bool IsRetryableFailure(DadScheduleFailureKind failureKind)
        => failureKind is DadScheduleFailureKind.PreStartRejected
            or DadScheduleFailureKind.EntryTerminalFailure
            or DadScheduleFailureKind.CoordinatorReloadAbandonment;

    internal static bool TryValidateRetryAvailability(
        bool scheduleRunActive,
        bool schedulerActive,
        bool dadWorkActive,
        int queuedJobCount,
        bool pendingCleanup,
        out string blocker)
    {
        if (scheduleRunActive ||
            schedulerActive ||
            dadWorkActive ||
            queuedJobCount > 0 ||
            pendingCleanup)
        {
            blocker = "Resume is unavailable while DAD, the scheduler, queued jobs, or cancellation cleanup still owns active work.";
            return false;
        }

        blocker = string.Empty;
        return true;
    }

    public static bool TryCreateRetryState(
        DadScheduleRunResult failed,
        DadScheduleDefinition schedule,
        string requestedBy,
        DateTime nowUtc,
        out DadScheduleRunState state,
        out string blocker)
    {
        state = new DadScheduleRunState();
        blocker = string.Empty;
        ArgumentNullException.ThrowIfNull(failed);
        ArgumentNullException.ThrowIfNull(schedule);
        schedule.Normalize();
        if (failed.Status != DadScheduleRunStatus.Blocked || !IsRetryableFailure(failed.FailureKind))
        {
            blocker = $"Schedule failure kind {failed.FailureKind} cannot be resumed.";
            return false;
        }
        if (failed.ScheduleRevisionAtStart <= 0 || schedule.Revision != failed.ScheduleRevisionAtStart)
        {
            blocker = "Schedule revision changed after the failed entry; resume requires the exact original revision.";
            return false;
        }
        if (failed.CurrentEntryIndex < 0 || failed.CurrentEntryIndex >= schedule.Entries.Count)
        {
            blocker = "The failed schedule cursor no longer resolves to an entry.";
            return false;
        }

        var entry = schedule.Entries[failed.CurrentEntryIndex].Normalize();
        if (!string.Equals(entry.EntryId, failed.CurrentEntryId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entry.GroupId, failed.CurrentGroupId, StringComparison.OrdinalIgnoreCase))
        {
            blocker = "The failed schedule entry identity changed; resume is not permitted.";
            return false;
        }
        if (failed.RepeatIteration < MinRepeatCount || failed.RepeatIteration > entry.RepeatCount)
        {
            blocker = "The failed schedule repeat cursor no longer resolves to the exact entry repeat.";
            return false;
        }

        var now = EnsureUtc(nowUtc);
        state = new DadScheduleRunState
        {
            RunId = Guid.NewGuid().ToString("N"),
            ScheduleId = schedule.ScheduleId,
            ScheduleName = schedule.DisplayName,
            Status = DadScheduleRunStatus.Running,
            Phase = DadScheduleRunPhase.StartingEntry,
            RequestedBy = NormalizeRequester(requestedBy),
            DryRun = failed.DryRun,
            ManualRun = failed.ManualRun,
            DailyResetUtc = failed.DailyResetUtc,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            CurrentEntryId = entry.EntryId,
            CurrentGroupId = entry.GroupId,
            CurrentPresetName = string.IsNullOrWhiteSpace(entry.PresetName) ? entry.GroupId : entry.PresetName,
            CurrentEntryIndex = failed.CurrentEntryIndex,
            RepeatIteration = failed.RepeatIteration,
            TotalEntryExecutions = failed.TotalEntryExecutions,
            CompletedEntryExecutions = failed.CompletedEntryExecutions,
            SkippedEntryExecutions = failed.SkippedEntryExecutions,
            ActiveSchedulerJobId = string.Empty,
            ActivePlannerRequestId = string.Empty,
            FailureKind = DadScheduleFailureKind.None,
            ScheduleRevisionAtStart = failed.ScheduleRevisionAtStart,
            RetriedFromRunId = failed.RunId,
        };
        state.Summary = $"Resuming failed schedule entry {state.CurrentEntryIndex + 1}, repeat {state.RepeatIteration}: {state.CurrentPresetName}.";
        return true;
    }

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
