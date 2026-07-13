namespace dad.Models;

public sealed class DadPlannerValidationDetails
{
    public bool CanStart { get; init; }
    public bool CanSchedule { get; init; }
    public string ReadinessSummary { get; init; } = string.Empty;
    public List<string> StaticBlockers { get; init; } = [];
    public List<string> ReadinessBlockers { get; init; } = [];
    public List<string> ScheduleBlockers { get; init; } = [];
}

public enum DadStrictPlannerRevalidationDisposition
{
    ReadyToStart = 0,
    WaitForRuntimeReadiness = 1,
    TerminalRejection = 2,
}

public sealed class DadStrictPlannerRevalidationDecision
{
    public DadStrictPlannerRevalidationDisposition Disposition { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class DadPlannerModuleRuntimeDecision
{
    public bool CanStart { get; init; }
    public bool CanSchedule { get; init; }
    public bool IsTransientRuntimeReadiness { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class DadStrictPlannerRevalidationTracker
{
    private int startClaimed;
    private int waitingDiagnosticRecorded;
    private int terminalDiagnosticRecorded;

    public bool TryClaimStart()
        => Interlocked.CompareExchange(ref startClaimed, 1, 0) == 0;

    public bool TryRecordDiagnostic(DadStrictPlannerRevalidationDisposition disposition)
        => disposition switch
        {
            DadStrictPlannerRevalidationDisposition.WaitForRuntimeReadiness =>
                Interlocked.CompareExchange(ref waitingDiagnosticRecorded, 1, 0) == 0,
            DadStrictPlannerRevalidationDisposition.TerminalRejection =>
                Interlocked.CompareExchange(ref terminalDiagnosticRecorded, 1, 0) == 0,
            _ => false,
        };
}

public static class DadPlannerValidationRules
{
    private static readonly string[] RuntimeReadinessCapabilities =
    [
        "RuntimeReadiness",
        "PlannerRuntimeReadiness",
    ];

    public static DadPlannerModuleRuntimeDecision EvaluateModuleRuntimeStatus(
        bool currentCanSchedule,
        DadModuleExecutionStatusDto status)
    {
        var reason = string.IsNullOrWhiteSpace(status.BlockedReason)
            ? string.IsNullOrWhiteSpace(status.FailureReason)
                ? status.Summary
                : status.FailureReason
            : status.BlockedReason;
        var transient = IsTransientRuntimeReadinessFailure(status);
        return new DadPlannerModuleRuntimeDecision
        {
            CanStart = status.CanStart,
            CanSchedule = status.CanStart || transient ? currentCanSchedule : false,
            IsTransientRuntimeReadiness = transient,
            Reason = reason,
        };
    }

    public static bool IsTransientRuntimeReadinessFailure(DadModuleExecutionStatusDto? status)
    {
        if (status == null || status.CanStart || status.Blockers.Count == 0)
            return false;

        return status.Blockers.All(static blocker =>
            string.Equals(blocker.Capability, "RuntimeReadiness", StringComparison.OrdinalIgnoreCase) &&
            blocker.Severity is DadModuleBlockerSeverity.Blocked or DadModuleBlockerSeverity.Failed);
    }

    public static DadPlannerValidationDetails Evaluate(
        IEnumerable<string>? staticBlockers,
        IEnumerable<string>? readinessBlockers,
        IEnumerable<string>? scheduleBlockers)
    {
        var staticList = Normalize(staticBlockers);
        var readinessList = Normalize(readinessBlockers);
        var scheduleList = Normalize(scheduleBlockers);
        var canStart = staticList.Count == 0 && readinessList.Count == 0;
        var canSchedule = staticList.Count == 0 && scheduleList.Count == 0;
        var summary = canStart
            ? "Ready for direct start."
            : canSchedule
                ? $"Scheduler can resolve live readiness: {string.Join(" | ", readinessList)}"
                : string.Join(" | ", staticList.Concat(scheduleList).Distinct(StringComparer.OrdinalIgnoreCase));

        return new DadPlannerValidationDetails
        {
            CanStart = canStart,
            CanSchedule = canSchedule,
            ReadinessSummary = summary,
            StaticBlockers = staticList,
            ReadinessBlockers = readinessList,
            ScheduleBlockers = scheduleList,
        };
    }

    public static bool CanStartStrictScheduledRun(
        bool allSlotsReady,
        DadPlannerRunRequestPreview? strictPreview,
        out string reason)
    {
        var decision = EvaluateStrictScheduledRun(allSlotsReady, strictPreview);
        reason = decision.Reason;
        return decision.Disposition == DadStrictPlannerRevalidationDisposition.ReadyToStart;
    }

    public static DadStrictPlannerRevalidationDecision EvaluateStrictScheduledRun(
        bool allSlotsReady,
        DadPlannerRunRequestPreview? strictPreview)
    {
        if (!allSlotsReady)
        {
            return new DadStrictPlannerRevalidationDecision
            {
                Disposition = DadStrictPlannerRevalidationDisposition.WaitForRuntimeReadiness,
                Reason = "Scheduler slots are not all ready.",
            };
        }

        if (strictPreview?.Request == null)
        {
            return new DadStrictPlannerRevalidationDecision
            {
                Disposition = DadStrictPlannerRevalidationDisposition.TerminalRejection,
                Reason = "Strict planner revalidation did not produce a request.",
            };
        }

        if (strictPreview.CanStart)
        {
            return new DadStrictPlannerRevalidationDecision
            {
                Disposition = DadStrictPlannerRevalidationDisposition.ReadyToStart,
            };
        }

        var reason = BuildBlockedReason(strictPreview);
        if (string.IsNullOrWhiteSpace(reason))
            reason = "Strict planner revalidation is not startable.";

        var transient = strictPreview.CanSchedule &&
                        strictPreview.StaticBlockers.Count == 0 &&
                        strictPreview.ScheduleBlockers.Count == 0;
        return new DadStrictPlannerRevalidationDecision
        {
            Disposition = transient
                ? DadStrictPlannerRevalidationDisposition.WaitForRuntimeReadiness
                : DadStrictPlannerRevalidationDisposition.TerminalRejection,
            Reason = reason,
        };
    }

    public static bool IsStrictRuntimeOnlyFailure(
        bool strictValidationRequested,
        bool strictPlanBuilt,
        bool relaxedPlanBuilt)
        => strictValidationRequested && !strictPlanBuilt && relaxedPlanBuilt;

    public static string BuildBlockedReason(DadPlannerRunRequestPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var staticBlockers = Normalize(preview.StaticBlockers);
        var scheduleBlockers = Normalize(preview.ScheduleBlockers);
        var readinessBlockers = Normalize(preview.ReadinessBlockers.Concat(
            preview.ModuleBlockers
                .Where(IsRuntimeReadinessBlocker)
                .Select(static blocker => blocker.Summary)));
        var moduleBlockers = Normalize(preview.ModuleBlockers
            .Where(IsTerminalModuleBlocker)
            .Select(static blocker => blocker.Summary));

        if (preview.CanSchedule && staticBlockers.Count == 0 && scheduleBlockers.Count == 0 && moduleBlockers.Count == 0)
        {
            var readiness = readinessBlockers.Count > 0
                ? string.Join(" | ", readinessBlockers)
                : FirstNonEmpty(preview.BlockedReason, preview.ReadinessSummary, preview.StatusSummary);
            return string.IsNullOrWhiteSpace(readiness) ? string.Empty : $"Readiness: {readiness}";
        }

        var categorized = new List<string>();
        AddCategory(categorized, "Static", staticBlockers);
        AddCategory(categorized, "Schedule", scheduleBlockers);
        AddCategory(categorized, "Readiness", readinessBlockers);
        AddCategory(categorized, "Module", moduleBlockers);
        if (categorized.Count > 0)
            return string.Join(" || ", categorized);

        return FirstNonEmpty(preview.BlockedReason, preview.StatusSummary);
    }

    public static IReadOnlyList<string> GetTerminalModuleBlockers(DadPlannerRunRequestPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return Normalize(preview.ModuleBlockers
            .Where(IsTerminalModuleBlocker)
            .Select(static blocker => blocker.Summary));
    }

    public static string BuildSchedulerTerminalReason(
        DadPlannerRunRequestPreview preview,
        IEnumerable<string>? slotBlockers)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var reasons = new List<string>();
        var hasPlannerDetail = !preview.CanStart ||
                               !preview.CanSchedule ||
                               preview.StaticBlockers.Count > 0 ||
                               preview.ScheduleBlockers.Count > 0 ||
                               preview.ReadinessBlockers.Count > 0 ||
                               preview.ModuleBlockers.Any(static blocker =>
                                   blocker.Severity is DadModuleBlockerSeverity.Blocked or DadModuleBlockerSeverity.Failed);
        if (hasPlannerDetail)
        {
            var plannerReason = BuildBlockedReason(preview);
            if (!string.IsNullOrWhiteSpace(plannerReason))
                reasons.Add(plannerReason);
        }

        var slots = Normalize(slotBlockers);
        if (slots.Count > 0)
            reasons.Add($"Slot: {string.Join(" | ", slots)}");
        return string.Join(" || ", reasons.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static DateTime ResolveStrictReadinessBudgetStartUtc(
        IReadOnlyCollection<DadSchedulerSlotState> slots,
        DateTime fallbackUtc)
    {
        var latestReadyUtc = slots
            .Where(static slot => slot.Ready && slot.ReadyUtc.HasValue)
            .Select(static slot => slot.ReadyUtc!.Value)
            .DefaultIfEmpty(fallbackUtc)
            .Max();
        return EnsureUtc(latestReadyUtc);
    }

    public static void StampReadyTransitions(
        IReadOnlyCollection<DadSchedulerSlotState> currentSlots,
        IReadOnlyCollection<DadSchedulerSlotState> previousSlots,
        DateTime nowUtc)
    {
        nowUtc = EnsureUtc(nowUtc);
        foreach (var current in currentSlots)
        {
            var previous = previousSlots.FirstOrDefault(slot =>
                string.Equals(slot.SlotId, current.SlotId, StringComparison.OrdinalIgnoreCase));
            if (!current.Ready)
            {
                current.ReadyUtc = null;
                continue;
            }

            if (previous is { Ready: true, ReadyUtc: not null })
                current.ReadyUtc = previous.ReadyUtc;
            else
                current.ReadyUtc = nowUtc;
        }
    }

    private static List<string> Normalize(IEnumerable<string>? values)
        => (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsRuntimeReadinessBlocker(DadModuleBlockerDto blocker)
        => RuntimeReadinessCapabilities.Contains(blocker.Capability, StringComparer.OrdinalIgnoreCase) &&
           blocker.Severity is DadModuleBlockerSeverity.Blocked or DadModuleBlockerSeverity.Failed;

    private static bool IsTerminalModuleBlocker(DadModuleBlockerDto blocker)
        => (blocker.Severity is DadModuleBlockerSeverity.Blocked or DadModuleBlockerSeverity.Failed) &&
           !IsRuntimeReadinessBlocker(blocker) &&
           !string.Equals(blocker.Capability, "Planner", StringComparison.OrdinalIgnoreCase);

    private static void AddCategory(List<string> target, string name, IReadOnlyCollection<string> values)
    {
        if (values.Count > 0)
            target.Add($"{name}: {string.Join(" | ", values)}");
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
