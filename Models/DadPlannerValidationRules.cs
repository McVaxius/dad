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

        var reason = string.IsNullOrWhiteSpace(strictPreview.BlockedReason)
            ? strictPreview.StatusSummary
            : strictPreview.BlockedReason;
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

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
