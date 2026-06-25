using dad.Models;

namespace dad.Services;

public interface IDadModuleExecutor
{
    string ExecutorId { get; }
    DadModuleId ModuleId { get; }
    DadModuleExecutionStatusDto CanStart(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants);
    DadRunStepResultDto Start(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants);
    DadRunStepResultDto Update();
    DadRunStepResultDto Cancel(string reason);
    DadModuleExecutionStatusDto GetStatus();
}

public abstract class DadDeferredModuleExecutor : IDadModuleExecutor
{
    private readonly DadModuleRegistry moduleRegistry;
    private readonly Func<DadRunPlan, string> queueBlockerFactory;
    private DadModuleExecutionStatusDto status = new();

    protected DadDeferredModuleExecutor(
        DadModuleRegistry moduleRegistry,
        string executorId,
        DadModuleId moduleId,
        string displayName,
        Func<DadRunPlan, string> queueBlockerFactory)
    {
        this.moduleRegistry = moduleRegistry;
        this.queueBlockerFactory = queueBlockerFactory;
        ExecutorId = executorId;
        ModuleId = moduleId;
        DisplayName = displayName;
    }

    public string ExecutorId { get; }
    public DadModuleId ModuleId { get; }
    protected string DisplayName { get; }

    public DadModuleExecutionStatusDto CanStart(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var module = ResolveModule(plan);
        var capability = moduleRegistry.GetCapability(module.ModuleId);
        var blockers = BuildCapabilityBlockers(plan, module, capability, participants);
        var hardBlocked = blockers.Any(static blocker =>
            blocker.Severity is DadModuleBlockerSeverity.Blocked or DadModuleBlockerSeverity.Failed);
        var deferred = blockers.Any(static blocker => blocker.Severity == DadModuleBlockerSeverity.Deferred);

        return new DadModuleExecutionStatusDto
        {
            RunId = plan.Request.RequestId,
            ModuleId = module.ModuleId,
            DisplayName = module.DisplayName,
            Phase = DadRunPhase.QueuePreparing,
            Status = hardBlocked ? DadRunStatus.Failed : DadRunStatus.Running,
            StepName = ExecutorId,
            CanStart = !hardBlocked,
            Deferred = deferred,
            RetryAttempt = 0,
            MaxRetryAttempts = capability.CanRequeue ? 3 : 0,
            UpdatedAtUtc = DateTime.UtcNow,
            Summary = hardBlocked
                ? $"Dad cannot route {module.DisplayName}: {FormatBlockers(blockers)}"
                : deferred
                    ? $"Dad can route {module.DisplayName}, but live queue start remains deferred."
                    : $"Dad can start {module.DisplayName}.",
            BlockedReason = FormatBlockers(blockers),
            Blockers = blockers,
        };
    }

    public DadRunStepResultDto Start(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var nextStatus = CanStart(plan, participants);
        nextStatus.StartedAtUtc = DateTime.UtcNow;
        nextStatus.UpdatedAtUtc = nextStatus.StartedAtUtc.Value;
        nextStatus.CompletedAtUtc = nextStatus.StartedAtUtc;
        nextStatus.IsActive = false;
        nextStatus.Status = nextStatus.Deferred || !nextStatus.CanStart
            ? DadRunStatus.Failed
            : DadRunStatus.Completed;
        nextStatus.Summary = nextStatus.Deferred
            ? $"{nextStatus.DisplayName} live execution is unavailable: {nextStatus.BlockedReason}"
            : nextStatus.CanStart
                ? $"Dad completed {nextStatus.DisplayName} routing."
                : nextStatus.Summary;
        nextStatus.FailureReason = nextStatus.Status == DadRunStatus.Failed
            ? nextStatus.BlockedReason
            : string.Empty;
        status = nextStatus;

        return new DadRunStepResultDto
        {
            RunId = plan.Request.RequestId,
            ModuleId = nextStatus.ModuleId,
            StepName = nextStatus.DisplayName,
            ParticipantState = nextStatus.CanStart ? DadParticipantState.QueuePending : DadParticipantState.Failed,
            Success = nextStatus.Status == DadRunStatus.Completed,
            Deferred = nextStatus.Deferred,
            TimedOut = false,
            Summary = nextStatus.Summary,
            FailureReason = nextStatus.FailureReason,
            BlockedReason = nextStatus.BlockedReason,
            ExecutorStatus = nextStatus.Clone(),
            ModuleBlockers = nextStatus.Blockers.Select(static blocker => blocker.Clone()).ToList(),
            ReportedAtUtc = DateTime.UtcNow,
        };
    }

    public DadRunStepResultDto Update()
        => BuildStatusStep(status);

    public DadRunStepResultDto Cancel(string reason)
    {
        status.Status = DadRunStatus.Cancelled;
        status.Phase = DadRunPhase.Finalizing;
        status.IsActive = false;
        status.CompletedAtUtc = DateTime.UtcNow;
        status.UpdatedAtUtc = status.CompletedAtUtc.Value;
        status.Summary = string.IsNullOrWhiteSpace(reason) ? $"{DisplayName} executor cancelled." : reason;

        return BuildStatusStep(status);
    }

    public DadModuleExecutionStatusDto GetStatus()
        => status.Clone();

    protected virtual DadPlannedModuleExecution ResolveModule(DadRunPlan plan)
        => plan.Modules.FirstOrDefault(module => module.ModuleId == ModuleId)
           ?? plan.Modules.FirstOrDefault()
           ?? new DadPlannedModuleExecution
           {
               ModuleId = ModuleId,
               DisplayName = DisplayName,
               ExpectedPartySize = Math.Max(1, plan.RequiredParticipantCount),
           };

    private List<DadModuleBlockerDto> BuildCapabilityBlockers(
        DadRunPlan plan,
        DadPlannedModuleExecution module,
        DadModuleCapabilitySnapshot capability,
        IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var blockers = new List<DadModuleBlockerDto>();
        if (participants.Count < module.ExpectedPartySize)
        {
            blockers.Add(new DadModuleBlockerDto
            {
                ModuleId = module.ModuleId,
                Capability = "Participants",
                Severity = DadModuleBlockerSeverity.Failed,
                Summary = $"Need {module.ExpectedPartySize} participant(s), have {participants.Count}.",
            });
        }

        if (!capability.CanPlan)
            blockers.Add(BuildBlocker(module.ModuleId, "CanPlan", "Module cannot plan yet.", DadModuleBlockerSeverity.Blocked));

        if (module.ExpectedPartySize > 1 && !capability.CanAssembleParty)
            blockers.Add(BuildBlocker(module.ModuleId, "CanAssembleParty", "Module cannot assemble party yet.", DadModuleBlockerSeverity.Blocked));

        if (!capability.CanStartQueue)
            blockers.Add(BuildBlocker(module.ModuleId, "CanStartQueue", queueBlockerFactory(plan)));

        if (!capability.CanTrackCompletion)
            blockers.Add(BuildBlocker(module.ModuleId, "CanTrackCompletion", "Completion tracking is not enabled for this module."));

        if (!capability.CanRequeue && AllowsRepeatedWork(plan))
            blockers.Add(BuildBlocker(module.ModuleId, "CanRequeue", "Requeue/retry loop is not enabled for this module."));

        return blockers
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker.Summary))
            .GroupBy(static blocker => $"{blocker.ModuleId}|{blocker.Capability}|{blocker.Summary}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    private static bool AllowsRepeatedWork(DadRunPlan plan)
        => (plan.Request.Dungeon?.Count ?? 0) > 1
           || (plan.Request.Msq?.Attempts ?? 0) > 1
           || (plan.Request.DutySupport?.Attempts ?? 0) > 1
           || (plan.Request.Trust?.Attempts ?? 0) > 1
           || (plan.Request.PremadeDuty?.Attempts ?? 0) > 1
           || (plan.Request.Blunderville?.Attempts ?? 0) > 1
           || (plan.Request.Mogtome?.Attempts ?? 0) > 1
           || (plan.Request.Commendation?.Attempts ?? 0) > 1
           || (plan.Request.Astrope?.Attempts ?? 0) > 1
           || (plan.Request.CustomDuty?.Attempts ?? 0) > 1
           || (plan.Request.Squadron?.Attempts ?? 0) > 1
           || (plan.Request.VariantVvd?.Attempts ?? 0) > 1;

    private static DadModuleBlockerDto BuildBlocker(
        DadModuleId moduleId,
        string capability,
        string summary,
        DadModuleBlockerSeverity severity = DadModuleBlockerSeverity.Deferred)
        => new()
        {
            ModuleId = moduleId,
            Capability = capability,
            Severity = severity,
            Summary = summary,
        };

    private static DadRunStepResultDto BuildStatusStep(DadModuleExecutionStatusDto status)
        => new()
        {
            RunId = status.RunId,
            ModuleId = status.ModuleId,
            StepName = status.DisplayName,
            ParticipantState = status.Status == DadRunStatus.Cancelled ? DadParticipantState.Cancelled : DadParticipantState.QueuePending,
            Success = status.Status is DadRunStatus.Running or DadRunStatus.Completed,
            Deferred = status.Deferred,
            Summary = status.Summary,
            FailureReason = status.FailureReason,
            BlockedReason = status.BlockedReason,
            ExecutorStatus = status.Clone(),
            ModuleBlockers = status.Blockers.Select(static blocker => blocker.Clone()).ToList(),
            ReportedAtUtc = DateTime.UtcNow,
        };

    private static string FormatBlockers(IReadOnlyList<DadModuleBlockerDto> blockers)
        => blockers.Count == 0
            ? string.Empty
            : string.Join(" | ", blockers.Select(static blocker => $"{blocker.Capability}: {blocker.Summary}"));
}

public sealed class DadMsqExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadMsqExecutor", DadModuleId.Msq, "MSQ", queueBlockerFactory);

public sealed class DadDutySupportExecutor(
    DadNpcDutyQueueService queueService,
    DadDutySupportAdsService adsService,
    DadCombatRotationService combatRotationService) : IDadModuleExecutor
{
    private static readonly TimeSpan LeaveRetryCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PostDutyStabilizeDuration = TimeSpan.FromSeconds(10);

    private DadModuleExecutionStatusDto status = new();
    private DadNpcDutyResolvedContent? resolvedContent;
    private DateTime runStartedAtUtc = DateTime.MinValue;
    private DateTime dutyCompletedAtUtc = DateTime.MinValue;
    private DateTime nextLeaveAttemptUtc = DateTime.MinValue;
    private DateTime postDutyStabilizeUntilUtc = DateTime.MinValue;
    private bool adsOutsideArmed;
    private bool adsStopProtectedByDutyEntry;
    private bool enteredDuty;
    private bool dutyCompleted;
    private bool leaveRequested;
    private bool leaveConfirmationObserved;
    private bool adsStopSentBeforeEntry;
    private bool entryAutomationAttempted;
    private DadCombatRotationMode rotationMode = DadCombatRotationMode.UseFrenRider;
    private string entryAutomationSummary = string.Empty;
    private int leaveAttemptCount;

    public string ExecutorId => "DadDutySupportExecutor";
    public DadModuleId ModuleId => DadModuleId.DutySupport;

    public DadModuleExecutionStatusDto CanStart(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var module = ResolveModule(plan);
        var mode = combatRotationService.CombatRotationMode;
        var blockers = BuildBlockers(plan, module, participants, mode, out _);
        var blockedReason = FormatBlockers(blockers);
        var hardBlocked = blockers.Any(static blocker =>
            blocker.Severity is DadModuleBlockerSeverity.Blocked or DadModuleBlockerSeverity.Failed);

        return new DadModuleExecutionStatusDto
        {
            RunId = plan.Request.RequestId,
            ModuleId = DadModuleId.DutySupport,
            DisplayName = module.DisplayName,
            Phase = DadRunPhase.QueuePreparing,
            Status = hardBlocked ? DadRunStatus.Failed : DadRunStatus.Running,
            StepName = ExecutorId,
            CanStart = !hardBlocked,
            Deferred = false,
            RetryAttempt = 0,
            MaxRetryAttempts = 0,
            UpdatedAtUtc = DateTime.UtcNow,
            Summary = hardBlocked
                ? $"Dad cannot start Duty Support: {blockedReason}"
                : BuildCanStartSummary(plan.Request.DutySupport?.DutyName, mode),
            FailureReason = hardBlocked ? blockedReason : string.Empty,
            BlockedReason = blockedReason,
            Blockers = blockers,
        };
    }

    public DadRunStepResultDto Start(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var module = ResolveModule(plan);
        rotationMode = combatRotationService.CombatRotationMode;
        var blockers = BuildBlockers(plan, module, participants, rotationMode, out resolvedContent);
        var blockedReason = FormatBlockers(blockers);
        var hardBlocked = blockers.Any(static blocker =>
            blocker.Severity is DadModuleBlockerSeverity.Blocked or DadModuleBlockerSeverity.Failed);
        var now = DateTime.UtcNow;
        ResetRuntimeState(now);

        status = new DadModuleExecutionStatusDto
        {
            RunId = plan.Request.RequestId,
            ModuleId = DadModuleId.DutySupport,
            DisplayName = module.DisplayName,
            Phase = DadRunPhase.QueuePreparing,
            Status = hardBlocked ? DadRunStatus.Failed : DadRunStatus.Running,
            StepName = ExecutorId,
            IsActive = !hardBlocked,
            CanStart = !hardBlocked,
            Deferred = false,
            RetryAttempt = 0,
            MaxRetryAttempts = 0,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            CompletedAtUtc = hardBlocked ? now : null,
            Summary = hardBlocked
                ? $"Dad cannot start Duty Support: {blockedReason}"
                : BuildStartSummary(resolvedContent?.DutyName),
            FailureReason = hardBlocked ? blockedReason : string.Empty,
            BlockedReason = blockedReason,
            Blockers = blockers,
        };

        if (hardBlocked)
            return BuildStatusStep(status);

        if (UsesAdsDutyFlow() && !adsService.TryArmOutside(out var adsFailure))
        {
            Fail(adsFailure);
            return BuildStatusStep(status, DadParticipantState.Failed);
        }

        if (UsesAdsDutyFlow())
        {
            adsOutsideArmed = true;
            status.Summary = $"Force Commands mode: ADS outside armed; starting native Duty Support queue for {resolvedContent?.DutyName}.";
        }

        return Update();
    }

    public DadRunStepResultDto Update()
    {
        if (status.Status is DadRunStatus.Cancelled or DadRunStatus.Completed or DadRunStatus.Failed)
            return BuildStatusStep(status);

        if (resolvedContent == null)
        {
            Fail("Duty Support content was not resolved.");
            return BuildStatusStep(status);
        }

        var now = DateTime.UtcNow;
        if (postDutyStabilizeUntilUtc != DateTime.MinValue)
            return UpdatePostDutyStabilizing(now);

        if (enteredDuty && HasExitedRequestedDuty())
        {
            if (!dutyCompleted)
            {
                Fail($"Duty Support duty {resolvedContent.DutyName} exited before DutyCompleted; treating as abandoned.");
                return BuildStatusStep(status, DadParticipantState.Failed);
            }

            return BeginOrUpdatePostDutyStabilizing(now);
        }

        if (enteredDuty && !TryApplyEntryAutomation())
            return BuildStatusStep(status, DadParticipantState.Failed);

        if (enteredDuty && !dutyCompleted && queueService.HasDutyCompleted(resolvedContent, runStartedAtUtc))
        {
            dutyCompleted = true;
            dutyCompletedAtUtc = now;
        }

        if (dutyCompleted)
            return UsesAdsDutyFlow()
                ? UpdateDutyCompletionLeave(now)
                : UpdateDutyCompletionWaitForExit();

        var pulse = queueService.Pulse(status.RunId, resolvedContent);
        if (UsesAdsDutyFlow() && ProtectsAdsOwnershipAfterQueue(pulse.Kind))
            adsStopProtectedByDutyEntry = true;
        var enteredDutyThisPulse = pulse.Kind == DadNpcDutyQueuePulseKind.EnteredDuty;
        if (enteredDutyThisPulse)
            enteredDuty = true;
        if (pulse.Status == DadRunStatus.Failed && !enteredDuty)
            StopAdsBeforeDutyIfNeeded();

        ApplyPulse(pulse);
        if (pulse.Status == DadRunStatus.Failed)
            return BuildStatusStep(status, pulse.ParticipantState);

        if (enteredDutyThisPulse && !TryApplyEntryAutomation())
        {
            return BuildStatusStep(status, DadParticipantState.Failed);
        }

        if (enteredDuty)
        {
            SetActiveStatus(
                DadRunPhase.InDutyOrTask,
                BuildInDutySummary());
            return BuildStatusStep(status, DadParticipantState.Running);
        }

        status.Summary = BuildPreDutySummary(status.Summary);

        return BuildStatusStep(status, pulse.ParticipantState);
    }

    public DadRunStepResultDto Cancel(string reason)
    {
        if (UsesAdsDutyFlow())
            adsService.TryStop(out _);
        var pulse = queueService.Cancel(status.RunId, DadNpcDutyQueueMode.DutySupport, reason);
        ApplyPulse(pulse);
        status.Summary = pulse.Summary;
        status.FailureReason = pulse.FailureReason;
        status.CompletedAtUtc = DateTime.UtcNow;
        ClearRuntimeState();
        return BuildStatusStep(status, DadParticipantState.Cancelled);
    }

    public DadModuleExecutionStatusDto GetStatus()
        => status.Clone();

    private void ApplyPulse(DadNpcDutyQueuePulse pulse)
    {
        status.Phase = pulse.Phase;
        status.Status = pulse.Status;
        status.IsActive = pulse.IsActive;
        status.CanStart = pulse.Status != DadRunStatus.Failed;
        status.Deferred = false;
        status.UpdatedAtUtc = DateTime.UtcNow;
        status.CompletedAtUtc = pulse.IsActive ? null : status.UpdatedAtUtc;
        status.Summary = pulse.Summary;
        status.FailureReason = pulse.FailureReason;
        status.BlockedReason = pulse.BlockedReason;
        status.Blockers = pulse.Blockers.Select(static blocker => blocker.Clone()).ToList();
    }

    private void Fail(string reason)
    {
        if (UsesAdsDutyFlow())
            StopAdsBeforeDutyIfNeeded();
        status.Phase = DadRunPhase.Finalizing;
        status.Status = DadRunStatus.Failed;
        status.IsActive = false;
        status.CanStart = false;
        status.UpdatedAtUtc = DateTime.UtcNow;
        status.CompletedAtUtc = status.UpdatedAtUtc;
        status.Summary = reason;
        status.FailureReason = reason;
        status.BlockedReason = reason;
        status.Blockers =
        [
            BuildBlocker("RuntimeReadiness", reason, DadModuleBlockerSeverity.Failed),
        ];
    }

    private DadRunStepResultDto UpdateDutyCompletionLeave(DateTime now)
    {
        if (HasExitedRequestedDuty())
            return BeginOrUpdatePostDutyStabilizing(now);

        if (adsService.IsLeaveBlocked(out var leaveBlocker))
        {
            SetActiveStatus(
                DadRunPhase.InDutyOrTask,
                BuildAdsLeaveBlockedSummary(leaveBlocker),
                $"Leave blocked: {leaveBlocker}",
                [BuildBlocker("LeaveSafety", $"Leave blocked: {leaveBlocker}", DadModuleBlockerSeverity.Deferred)]);
            return BuildStatusStep(status, DadParticipantState.Running);
        }

        if (leaveRequested &&
            !leaveConfirmationObserved &&
            adsService.TryObserveLeaveEvidence(out var evidence))
        {
            leaveConfirmationObserved = true;
            status.Summary = BuildAdsLeaveWaitingSummary(now, $" Observed {evidence}.");
        }

        if (!leaveRequested || now >= nextLeaveAttemptUtc)
            return RequestAdsLeave(now);

        SetActiveStatus(
            DadRunPhase.InDutyOrTask,
            BuildAdsLeaveWaitingSummary(now));
        return BuildStatusStep(status, DadParticipantState.Running);
    }

    private DadRunStepResultDto UpdateDutyCompletionWaitForExit()
    {
        if (HasExitedRequestedDuty())
            return BeginOrUpdatePostDutyStabilizing(DateTime.UtcNow);

        SetActiveStatus(
            DadRunPhase.InDutyOrTask,
            BuildDutyCompleteWaitingForExitSummary());
        return BuildStatusStep(status, DadParticipantState.Running);
    }

    private DadRunStepResultDto RequestAdsLeave(DateTime now)
    {
        if (!adsService.TryLeave(out var leaveFailure))
        {
            Fail(leaveFailure);
            return BuildStatusStep(status, DadParticipantState.Failed);
        }

        leaveRequested = true;
        nextLeaveAttemptUtc = now + LeaveRetryCooldown;
        leaveConfirmationObserved = false;
        leaveAttemptCount++;

        SetActiveStatus(
            DadRunPhase.InDutyOrTask,
            BuildAdsLeaveWaitingSummary(now));
        return BuildStatusStep(status, DadParticipantState.Running);
    }

    private DadRunStepResultDto BeginOrUpdatePostDutyStabilizing(DateTime now)
    {
        if (postDutyStabilizeUntilUtc == DateTime.MinValue)
            postDutyStabilizeUntilUtc = now + PostDutyStabilizeDuration;

        return UpdatePostDutyStabilizing(now);
    }

    private DadRunStepResultDto UpdatePostDutyStabilizing(DateTime now)
    {
        if (postDutyStabilizeUntilUtc == DateTime.MinValue)
            postDutyStabilizeUntilUtc = now + PostDutyStabilizeDuration;

        if (now < postDutyStabilizeUntilUtc)
        {
            var remaining = Math.Max(0, (postDutyStabilizeUntilUtc - now).TotalSeconds);
            SetActiveStatus(
                DadRunPhase.PostRunStabilizing,
                $"Post-duty stabilizing ({remaining:F0}s).");
            return BuildStatusStep(status, DadParticipantState.Completed);
        }

        status.Phase = DadRunPhase.Finalizing;
        status.Status = DadRunStatus.Completed;
        status.IsActive = false;
        status.CanStart = true;
        status.UpdatedAtUtc = now;
        status.CompletedAtUtc = now;
        status.Summary = BuildCompletedSummary();
        status.FailureReason = string.Empty;
        status.BlockedReason = string.Empty;
        status.Blockers = [];
        ClearRuntimeState();
        return BuildStatusStep(status, DadParticipantState.Completed);
    }

    private bool HasExitedRequestedDuty()
        => resolvedContent != null &&
           enteredDuty &&
           !queueService.IsInRequestedDuty(resolvedContent) &&
           !queueService.IsQueued() &&
           !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty];

    private void SetActiveStatus(
        DadRunPhase phase,
        string summary,
        string blockedReason = "",
        List<DadModuleBlockerDto>? blockers = null)
    {
        status.Phase = phase;
        status.Status = DadRunStatus.Running;
        status.IsActive = true;
        status.CanStart = true;
        status.Deferred = false;
        status.UpdatedAtUtc = DateTime.UtcNow;
        status.CompletedAtUtc = null;
        status.Summary = summary;
        status.FailureReason = string.Empty;
        status.BlockedReason = blockedReason;
        status.Blockers = blockers ?? [];
    }

    private void StopAdsBeforeDutyIfNeeded()
    {
        if (!UsesAdsDutyFlow() || !adsOutsideArmed || enteredDuty || adsStopProtectedByDutyEntry || adsStopSentBeforeEntry)
            return;

        adsService.TryStop(out _);
        adsStopSentBeforeEntry = true;
    }

    private bool TryApplyEntryAutomation()
    {
        if (entryAutomationAttempted)
            return true;

        if (rotationMode == DadCombatRotationMode.UseFrenRider)
        {
            var entryEnableStatus = combatRotationService.TryEnableFrenRiderAfterDutyEntry(
                status.RunId,
                DadModuleId.DutySupport,
                DateTime.UtcNow,
                out entryAutomationSummary);
            if (entryEnableStatus != DadFrenRiderEntryEnableStatus.PendingRetry)
                entryAutomationAttempted = true;
            if (entryEnableStatus != DadFrenRiderEntryEnableStatus.Failed)
                return true;

            Fail(entryAutomationSummary);
            return false;
        }

        entryAutomationAttempted = true;
        var succeeded = combatRotationService.TryApplyDutySupportEntryMode(
            rotationMode,
            status.RunId,
            out entryAutomationSummary,
            out var shouldFailRun);
        if (succeeded || !shouldFailRun)
            return true;

        Fail(entryAutomationSummary);
        return false;
    }

    private static bool ProtectsAdsOwnershipAfterQueue(DadNpcDutyQueuePulseKind pulseKind)
        => pulseKind is DadNpcDutyQueuePulseKind.AcceptedQueueConfirm
            or DadNpcDutyQueuePulseKind.WaitingForQueue
            or DadNpcDutyQueuePulseKind.DutyEntryTransition
            or DadNpcDutyQueuePulseKind.EnteredDuty;

    private void ResetRuntimeState(DateTime now)
    {
        runStartedAtUtc = now;
        dutyCompletedAtUtc = DateTime.MinValue;
        nextLeaveAttemptUtc = DateTime.MinValue;
        postDutyStabilizeUntilUtc = DateTime.MinValue;
        adsOutsideArmed = false;
        adsStopProtectedByDutyEntry = false;
        enteredDuty = false;
        dutyCompleted = false;
        leaveRequested = false;
        leaveConfirmationObserved = false;
        adsStopSentBeforeEntry = false;
        entryAutomationAttempted = false;
        entryAutomationSummary = string.Empty;
        leaveAttemptCount = 0;
    }

    private void ClearRuntimeState()
    {
        runStartedAtUtc = DateTime.MinValue;
        dutyCompletedAtUtc = DateTime.MinValue;
        nextLeaveAttemptUtc = DateTime.MinValue;
        postDutyStabilizeUntilUtc = DateTime.MinValue;
        adsOutsideArmed = false;
        adsStopProtectedByDutyEntry = false;
        enteredDuty = false;
        dutyCompleted = false;
        leaveRequested = false;
        leaveConfirmationObserved = false;
        adsStopSentBeforeEntry = false;
        entryAutomationAttempted = false;
        entryAutomationSummary = string.Empty;
        leaveAttemptCount = 0;
    }

    private static DadPlannedModuleExecution ResolveModule(DadRunPlan plan)
        => plan.Modules.FirstOrDefault(module => module.ModuleId == DadModuleId.DutySupport)
           ?? new DadPlannedModuleExecution
           {
               ModuleId = DadModuleId.DutySupport,
               DisplayName = "Duty Support",
               ExpectedPartySize = 1,
           };

    private bool UsesAdsDutyFlow()
        => rotationMode == DadCombatRotationMode.ForceCommands;

    private static string BuildCanStartSummary(string? dutyName, DadCombatRotationMode mode)
        => mode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Dad can start native Duty Support queue for {dutyName}; Dad will enable FrenRider after duty entry, then observe while FrenRider owns duty behavior and exit.",
            DadCombatRotationMode.ForceCommands => $"Dad can start native Duty Support queue for {dutyName}; ADS and fixed rotation commands will be used.",
            DadCombatRotationMode.DoNothing => $"Dad can start native Duty Support queue for {dutyName}; no external automation commands will be sent.",
            _ => $"Dad can start native Duty Support queue for {dutyName}.",
        };

    private string BuildStartSummary(string? dutyName)
        => rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Use FrenRider mode: queueing native Duty Support for {dutyName}; Dad will enable FrenRider after duty entry.",
            DadCombatRotationMode.ForceCommands => $"Force Commands mode: starting native Duty Support queue for {dutyName}.",
            DadCombatRotationMode.DoNothing => $"Do Nothing mode: starting native Duty Support queue for {dutyName}.",
            _ => $"Starting native Duty Support queue for {dutyName}.",
        };

    private string BuildPreDutySummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            summary = $"Waiting to start native Duty Support queue for {resolvedContent?.DutyName ?? "requested duty"}.";

        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => summary.StartsWith("Use FrenRider mode:", StringComparison.OrdinalIgnoreCase)
                ? summary
                : $"Use FrenRider mode: queueing; {summary}",
            DadCombatRotationMode.ForceCommands when adsOutsideArmed => summary.StartsWith("Force Commands mode:", StringComparison.OrdinalIgnoreCase)
                ? summary
                : $"Force Commands mode: ADS outside armed; {summary}",
            DadCombatRotationMode.ForceCommands => summary.StartsWith("Force Commands mode:", StringComparison.OrdinalIgnoreCase)
                ? summary
                : $"Force Commands mode: {summary}",
            DadCombatRotationMode.DoNothing => summary.StartsWith("Do Nothing mode:", StringComparison.OrdinalIgnoreCase)
                ? summary
                : $"Do Nothing mode: {summary}",
            _ => summary,
        };
    }

    private string BuildInDutySummary()
    {
        var dutyName = resolvedContent?.DutyName ?? "requested duty";
        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => string.IsNullOrWhiteSpace(entryAutomationSummary)
                ? $"Use FrenRider mode: in duty for {dutyName}; Dad is observing while FrenRider owns combat, movement, and exit."
                : $"{entryAutomationSummary} In duty for {dutyName}; Dad is observing while FrenRider owns combat, movement, and exit.",
            DadCombatRotationMode.ForceCommands => string.IsNullOrWhiteSpace(entryAutomationSummary)
                ? $"Force Commands mode: ADS running duty; waiting for DutyCompleted before leave for {dutyName}."
                : $"Force Commands mode: ADS running duty; waiting for DutyCompleted before leave for {dutyName}. {entryAutomationSummary}",
            DadCombatRotationMode.DoNothing => $"Do Nothing mode: Dad queued {dutyName} and is observing completion/exit; user owns combat and leave.",
            _ => $"Dad is observing {dutyName}.",
        };
    }

    private string BuildDutyCompleteWaitingForExitSummary()
    {
        var dutyName = resolvedContent?.DutyName ?? "requested duty";
        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Duty Support duty {dutyName} completed; waiting for FrenRider or user to leave. Disable commands are reserved for successful final dad.Duty.Run IPC cleanup.",
            DadCombatRotationMode.DoNothing => $"Duty Support duty {dutyName} completed; waiting for user-owned duty exit.",
            _ => $"Duty Support duty {dutyName} completed; waiting for duty exit.",
        };
    }

    private string BuildCompletedSummary()
    {
        var dutyName = resolvedContent?.DutyName ?? "requested duty";
        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Duty Support duty {dutyName} completed and stabilized; normal Dad run done without disable commands. Successful final dad.Duty.Run IPC cleanup is separate.",
            DadCombatRotationMode.ForceCommands => $"Duty Support duty {dutyName} completed; Dad ADS run done.",
            DadCombatRotationMode.DoNothing => $"Duty Support duty {dutyName} completed; Dad queue-only run done.",
            _ => $"Duty Support duty {dutyName} completed; Dad run done.",
        };
    }

    private string BuildAdsLeaveBlockedSummary(string leaveBlocker)
        => $"ADS running duty; duty complete, leave blocked ({leaveBlocker}). {BuildAdsLeaveAttemptLabel()} pending.";

    private string BuildAdsLeaveWaitingSummary(DateTime now, string extraDetail = "")
    {
        var remaining = nextLeaveAttemptUtc == DateTime.MinValue
            ? 0
            : Math.Max(0, (nextLeaveAttemptUtc - now).TotalSeconds);
        var retryWindow = nextLeaveAttemptUtc == DateTime.MinValue
            ? "retry window unavailable"
            : $"{remaining:F0}s to retry";
        var evidenceText = leaveConfirmationObserved ? " Leave evidence observed." : string.Empty;
        return $"ADS leave requested ({BuildAdsLeaveAttemptLabel()}); waiting for duty exit ({retryWindow}).{evidenceText}{extraDetail}";
    }

    private string BuildAdsLeaveAttemptLabel()
        => leaveAttemptCount <= 1
            ? "attempt 1"
            : $"retry {leaveAttemptCount}";

    private List<DadModuleBlockerDto> BuildBlockers(
        DadRunPlan plan,
        DadPlannedModuleExecution module,
        IReadOnlyList<DadParticipantSnapshot> participants,
        DadCombatRotationMode mode,
        out DadNpcDutyResolvedContent? content)
    {
        var blockers = new List<DadModuleBlockerDto>();
        content = queueService.Resolve(plan.Request.DutySupport, out var resolveBlocker);
        if (!string.IsNullOrWhiteSpace(resolveBlocker))
            blockers.Add(BuildBlocker("DutySelector", resolveBlocker, DadModuleBlockerSeverity.Blocked));

        if (participants.Count < Math.Max(1, module.ExpectedPartySize))
            blockers.Add(BuildBlocker("Participants", $"Need {Math.Max(1, module.ExpectedPartySize)} participant(s), have {participants.Count}.", DadModuleBlockerSeverity.Failed));

        if (module.ExpectedPartySize != 1)
            blockers.Add(BuildBlocker("Participants", "Native Duty Support executor only supports one local participant.", DadModuleBlockerSeverity.Blocked));

        if (mode == DadCombatRotationMode.UseFrenRider && !combatRotationService.IsFrenRiderLoaded())
            blockers.Add(BuildBlocker("FrenRider", combatRotationService.MissingFrenRiderBlocker, DadModuleBlockerSeverity.Blocked));

        if (mode == DadCombatRotationMode.ForceCommands && !adsService.IsAdsLoaded())
            blockers.Add(BuildBlocker("ADS", adsService.MissingAdsBlocker, DadModuleBlockerSeverity.Blocked));

        return blockers
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker.Summary))
            .GroupBy(static blocker => $"{blocker.Capability}|{blocker.Summary}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    private static DadModuleBlockerDto BuildBlocker(
        string capability,
        string summary,
        DadModuleBlockerSeverity severity)
        => new()
        {
            ModuleId = DadModuleId.DutySupport,
            Capability = capability,
            Severity = severity,
            Summary = summary,
        };

    private static DadRunStepResultDto BuildStatusStep(DadModuleExecutionStatusDto status, DadParticipantState? participantState = null)
        => new()
        {
            RunId = status.RunId,
            ModuleId = DadModuleId.DutySupport,
            StepName = "Duty Support",
            ParticipantState = participantState
                               ?? (status.Status switch
                               {
                                   DadRunStatus.Cancelled => DadParticipantState.Cancelled,
                                   DadRunStatus.Failed => DadParticipantState.Failed,
                                   DadRunStatus.Completed => DadParticipantState.Completed,
                                   _ => DadParticipantState.QueuePending,
                               }),
            Success = status.Status is DadRunStatus.Running or DadRunStatus.Completed,
            Deferred = false,
            Summary = status.Summary,
            FailureReason = status.FailureReason,
            BlockedReason = status.BlockedReason,
            ExecutorStatus = status.Clone(),
            ModuleBlockers = status.Blockers.Select(static blocker => blocker.Clone()).ToList(),
            ReportedAtUtc = DateTime.UtcNow,
        };

    private static string FormatBlockers(IReadOnlyList<DadModuleBlockerDto> blockers)
        => blockers.Count == 0
            ? string.Empty
            : string.Join(" | ", blockers.Select(static blocker => $"{blocker.Capability}: {blocker.Summary}"));
}

public sealed class DadTrustExecutor(
    DadNpcDutyQueueService queueService,
    DadCombatRotationService combatRotationService) : IDadModuleExecutor
{
    private static readonly TimeSpan PostDutyStabilizeDuration = TimeSpan.FromSeconds(10);

    private DadModuleExecutionStatusDto status = new();
    private DadNpcDutyResolvedContent? resolvedContent;
    private DateTime runStartedAtUtc = DateTime.MinValue;
    private DateTime postDutyStabilizeUntilUtc = DateTime.MinValue;
    private bool enteredDuty;
    private bool dutyCompleted;
    private DadCombatRotationMode rotationMode = DadCombatRotationMode.UseFrenRider;
    private string entryAutomationSummary = string.Empty;

    public string ExecutorId => "DadTrustExecutor";
    public DadModuleId ModuleId => DadModuleId.Trust;

    public DadModuleExecutionStatusDto CanStart(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var module = ResolveModule(plan);
        var mode = combatRotationService.CombatRotationMode;
        var blockers = BuildBlockers(plan, module, participants, mode, out _);
        var blockedReason = FormatBlockers(blockers);
        var hardBlocked = blockers.Any(static blocker =>
            blocker.Severity is DadModuleBlockerSeverity.Blocked or DadModuleBlockerSeverity.Failed);

        return new DadModuleExecutionStatusDto
        {
            RunId = plan.Request.RequestId,
            ModuleId = DadModuleId.Trust,
            DisplayName = module.DisplayName,
            Phase = DadRunPhase.QueuePreparing,
            Status = hardBlocked ? DadRunStatus.Failed : DadRunStatus.Running,
            StepName = ExecutorId,
            CanStart = !hardBlocked,
            Deferred = false,
            RetryAttempt = 0,
            MaxRetryAttempts = 0,
            UpdatedAtUtc = DateTime.UtcNow,
            Summary = hardBlocked
                ? $"Dad cannot start Trust: {blockedReason}"
                : BuildCanStartSummary(plan.Request.Trust?.DutyName, mode),
            FailureReason = hardBlocked ? blockedReason : string.Empty,
            BlockedReason = blockedReason,
            Blockers = blockers,
        };
    }

    public DadRunStepResultDto Start(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var module = ResolveModule(plan);
        rotationMode = combatRotationService.CombatRotationMode;
        var blockers = BuildBlockers(plan, module, participants, rotationMode, out resolvedContent);
        var blockedReason = FormatBlockers(blockers);
        var hardBlocked = blockers.Any(static blocker =>
            blocker.Severity is DadModuleBlockerSeverity.Blocked or DadModuleBlockerSeverity.Failed);
        var now = DateTime.UtcNow;
        ResetRuntimeState(now);

        status = new DadModuleExecutionStatusDto
        {
            RunId = plan.Request.RequestId,
            ModuleId = DadModuleId.Trust,
            DisplayName = module.DisplayName,
            Phase = DadRunPhase.QueuePreparing,
            Status = hardBlocked ? DadRunStatus.Failed : DadRunStatus.Running,
            StepName = ExecutorId,
            IsActive = !hardBlocked,
            CanStart = !hardBlocked,
            Deferred = false,
            RetryAttempt = 0,
            MaxRetryAttempts = 0,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            CompletedAtUtc = hardBlocked ? now : null,
            Summary = hardBlocked
                ? $"Dad cannot start Trust: {blockedReason}"
                : BuildStartSummary(resolvedContent?.DutyName),
            FailureReason = hardBlocked ? blockedReason : string.Empty,
            BlockedReason = blockedReason,
            Blockers = blockers,
        };

        return hardBlocked ? BuildStatusStep(status) : Update();
    }

    public DadRunStepResultDto Update()
    {
        if (status.Status is DadRunStatus.Cancelled or DadRunStatus.Completed or DadRunStatus.Failed)
            return BuildStatusStep(status);

        if (resolvedContent == null)
        {
            Fail("Trust content was not resolved.");
            return BuildStatusStep(status);
        }

        var now = DateTime.UtcNow;
        if (postDutyStabilizeUntilUtc != DateTime.MinValue)
            return UpdatePostDutyStabilizing(now);

        if (enteredDuty && HasExitedRequestedDuty())
        {
            if (!dutyCompleted)
            {
                Fail($"Trust duty {resolvedContent.DutyName} exited before DutyCompleted; treating as abandoned.");
                return BuildStatusStep(status, DadParticipantState.Failed);
            }

            return BeginOrUpdatePostDutyStabilizing(now);
        }

        if (enteredDuty && !TryApplyEntryAutomation())
            return BuildStatusStep(status, DadParticipantState.Failed);

        if (enteredDuty && !dutyCompleted && queueService.HasDutyCompleted(resolvedContent, runStartedAtUtc))
            dutyCompleted = true;

        if (dutyCompleted)
            return UpdateDutyCompletionWaitForExit();

        var pulse = queueService.Pulse(status.RunId, resolvedContent);
        var enteredDutyThisPulse = pulse.Kind == DadNpcDutyQueuePulseKind.EnteredDuty;
        if (enteredDutyThisPulse)
            enteredDuty = true;

        ApplyPulse(pulse);
        if (pulse.Status == DadRunStatus.Failed)
            return BuildStatusStep(status, pulse.ParticipantState);

        if (enteredDutyThisPulse && !TryApplyEntryAutomation())
            return BuildStatusStep(status, DadParticipantState.Failed);

        if (enteredDuty)
        {
            SetActiveStatus(
                DadRunPhase.InDutyOrTask,
                BuildInDutySummary());
            return BuildStatusStep(status, DadParticipantState.Running);
        }

        status.Summary = BuildPreDutySummary(status.Summary);

        return BuildStatusStep(status, pulse.ParticipantState);
    }

    public DadRunStepResultDto Cancel(string reason)
    {
        var pulse = queueService.Cancel(status.RunId, DadNpcDutyQueueMode.Trust, reason);
        ApplyPulse(pulse);
        status.Summary = string.IsNullOrWhiteSpace(reason)
            ? "Trust executor cancelled. Dad does not send external stop commands; clear any remaining game-side queue or duty state manually if needed."
            : reason;
        status.FailureReason = pulse.FailureReason;
        status.CompletedAtUtc = DateTime.UtcNow;
        ClearRuntimeState();
        return BuildStatusStep(status, DadParticipantState.Cancelled);
    }

    public DadModuleExecutionStatusDto GetStatus()
        => status.Clone();

    private void ApplyPulse(DadNpcDutyQueuePulse pulse)
    {
        status.Phase = pulse.Phase;
        status.Status = pulse.Status;
        status.IsActive = pulse.IsActive;
        status.CanStart = pulse.Status != DadRunStatus.Failed;
        status.Deferred = false;
        status.UpdatedAtUtc = DateTime.UtcNow;
        status.CompletedAtUtc = pulse.IsActive ? null : status.UpdatedAtUtc;
        status.Summary = pulse.Summary;
        status.FailureReason = pulse.FailureReason;
        status.BlockedReason = pulse.BlockedReason;
        status.Blockers = pulse.Blockers.Select(static blocker => blocker.Clone()).ToList();
    }

    private void Fail(string reason)
    {
        status.Phase = DadRunPhase.Finalizing;
        status.Status = DadRunStatus.Failed;
        status.IsActive = false;
        status.CanStart = false;
        status.UpdatedAtUtc = DateTime.UtcNow;
        status.CompletedAtUtc = status.UpdatedAtUtc;
        status.Summary = reason;
        status.FailureReason = reason;
        status.BlockedReason = reason;
        status.Blockers =
        [
            BuildBlocker("RuntimeReadiness", reason, DadModuleBlockerSeverity.Failed),
        ];
    }

    private DadRunStepResultDto UpdateDutyCompletionWaitForExit()
    {
        if (HasExitedRequestedDuty())
            return BeginOrUpdatePostDutyStabilizing(DateTime.UtcNow);

        SetActiveStatus(
            DadRunPhase.InDutyOrTask,
            BuildDutyCompleteWaitingForExitSummary());
        return BuildStatusStep(status, DadParticipantState.Running);
    }

    private DadRunStepResultDto BeginOrUpdatePostDutyStabilizing(DateTime now)
    {
        if (postDutyStabilizeUntilUtc == DateTime.MinValue)
            postDutyStabilizeUntilUtc = now + PostDutyStabilizeDuration;

        return UpdatePostDutyStabilizing(now);
    }

    private DadRunStepResultDto UpdatePostDutyStabilizing(DateTime now)
    {
        if (postDutyStabilizeUntilUtc == DateTime.MinValue)
            postDutyStabilizeUntilUtc = now + PostDutyStabilizeDuration;

        if (now < postDutyStabilizeUntilUtc)
        {
            var remaining = Math.Max(0, (postDutyStabilizeUntilUtc - now).TotalSeconds);
            SetActiveStatus(
                DadRunPhase.PostRunStabilizing,
                $"Post-duty stabilizing ({remaining:F0}s).");
            return BuildStatusStep(status, DadParticipantState.Completed);
        }

        status.Phase = DadRunPhase.Finalizing;
        status.Status = DadRunStatus.Completed;
        status.IsActive = false;
        status.CanStart = true;
        status.UpdatedAtUtc = now;
        status.CompletedAtUtc = now;
        status.Summary = BuildCompletedSummary();
        status.FailureReason = string.Empty;
        status.BlockedReason = string.Empty;
        status.Blockers = [];
        ClearRuntimeState();
        return BuildStatusStep(status, DadParticipantState.Completed);
    }

    private bool HasExitedRequestedDuty()
        => resolvedContent != null &&
           enteredDuty &&
           !queueService.IsInRequestedDuty(resolvedContent) &&
           !queueService.IsQueued() &&
           !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty];

    private void SetActiveStatus(
        DadRunPhase phase,
        string summary,
        string blockedReason = "",
        List<DadModuleBlockerDto>? blockers = null)
    {
        status.Phase = phase;
        status.Status = DadRunStatus.Running;
        status.IsActive = true;
        status.CanStart = true;
        status.Deferred = false;
        status.UpdatedAtUtc = DateTime.UtcNow;
        status.CompletedAtUtc = null;
        status.Summary = summary;
        status.FailureReason = string.Empty;
        status.BlockedReason = blockedReason;
        status.Blockers = blockers ?? [];
    }

    private bool TryApplyEntryAutomation()
    {
        if (rotationMode == DadCombatRotationMode.UseFrenRider)
        {
            var entryEnableStatus = combatRotationService.TryEnableFrenRiderAfterDutyEntry(
                status.RunId,
                DadModuleId.Trust,
                DateTime.UtcNow,
                out entryAutomationSummary);
            if (entryEnableStatus != DadFrenRiderEntryEnableStatus.Failed)
                return true;

            Fail(entryAutomationSummary);
            return false;
        }

        entryAutomationSummary = rotationMode switch
        {
            DadCombatRotationMode.DoNothing => "Do Nothing mode selected; Dad sent no FrenRider, ADS, or rotation command after Trust entry.",
            DadCombatRotationMode.ForceCommands => "Force Commands mode is not guarded for Trust; Dad sent no entry command.",
            _ => $"Unknown combat rotation mode {rotationMode}; Dad sent no Trust entry command.",
        };
        return true;
    }

    private void ResetRuntimeState(DateTime now)
    {
        runStartedAtUtc = now;
        postDutyStabilizeUntilUtc = DateTime.MinValue;
        enteredDuty = false;
        dutyCompleted = false;
        entryAutomationSummary = string.Empty;
    }

    private void ClearRuntimeState()
    {
        runStartedAtUtc = DateTime.MinValue;
        postDutyStabilizeUntilUtc = DateTime.MinValue;
        enteredDuty = false;
        dutyCompleted = false;
        entryAutomationSummary = string.Empty;
    }

    private static DadPlannedModuleExecution ResolveModule(DadRunPlan plan)
        => plan.Modules.FirstOrDefault(module => module.ModuleId == DadModuleId.Trust)
           ?? new DadPlannedModuleExecution
           {
               ModuleId = DadModuleId.Trust,
               DisplayName = "Trust",
               ExpectedPartySize = 1,
           };

    private static string BuildCanStartSummary(string? dutyName, DadCombatRotationMode mode)
        => mode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Dad can start native Trust queue for {dutyName}; Dad will enable FrenRider after duty entry, then observe while FrenRider owns duty behavior and exit.",
            DadCombatRotationMode.DoNothing => $"Dad can start native Trust queue for {dutyName}; no external automation commands will be sent.",
            _ => $"Dad can start native Trust queue for {dutyName}.",
        };

    private string BuildStartSummary(string? dutyName)
        => rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Use FrenRider mode: queueing native Trust for {dutyName}; Dad will enable FrenRider after duty entry.",
            DadCombatRotationMode.DoNothing => $"Do Nothing mode: starting native Trust queue for {dutyName}.",
            _ => $"Starting native Trust queue for {dutyName}.",
        };

    private string BuildPreDutySummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            summary = $"Waiting to start native Trust queue for {resolvedContent?.DutyName ?? "requested duty"}.";

        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => summary.StartsWith("Use FrenRider mode:", StringComparison.OrdinalIgnoreCase)
                ? summary
                : $"Use FrenRider mode: queueing; {summary}",
            DadCombatRotationMode.DoNothing => summary.StartsWith("Do Nothing mode:", StringComparison.OrdinalIgnoreCase)
                ? summary
                : $"Do Nothing mode: {summary}",
            _ => summary,
        };
    }

    private string BuildInDutySummary()
    {
        var dutyName = resolvedContent?.DutyName ?? "requested duty";
        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => string.IsNullOrWhiteSpace(entryAutomationSummary)
                ? $"Use FrenRider mode: in Trust duty {dutyName}; Dad is observing while FrenRider owns combat, movement, and exit."
                : $"{entryAutomationSummary} In Trust duty {dutyName}; Dad is observing while FrenRider owns combat, movement, and exit.",
            DadCombatRotationMode.DoNothing => $"Do Nothing mode: Dad queued Trust duty {dutyName} and is observing completion/exit; user owns combat and leave.",
            _ => $"Dad is observing Trust duty {dutyName}.",
        };
    }

    private string BuildDutyCompleteWaitingForExitSummary()
    {
        var dutyName = resolvedContent?.DutyName ?? "requested duty";
        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Trust duty {dutyName} completed; waiting for FrenRider or user to leave. Disable commands are reserved for successful final dad.Duty.Run IPC cleanup.",
            DadCombatRotationMode.DoNothing => $"Trust duty {dutyName} completed; waiting for user-owned duty exit.",
            _ => $"Trust duty {dutyName} completed; waiting for duty exit.",
        };
    }

    private string BuildCompletedSummary()
    {
        var dutyName = resolvedContent?.DutyName ?? "requested duty";
        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Trust duty {dutyName} completed and stabilized; normal Dad run done without disable commands. Successful final dad.Duty.Run IPC cleanup is separate.",
            DadCombatRotationMode.DoNothing => $"Trust duty {dutyName} completed; Dad queue-only run done.",
            _ => $"Trust duty {dutyName} completed; Dad run done.",
        };
    }

    private List<DadModuleBlockerDto> BuildBlockers(
        DadRunPlan plan,
        DadPlannedModuleExecution module,
        IReadOnlyList<DadParticipantSnapshot> participants,
        DadCombatRotationMode mode,
        out DadNpcDutyResolvedContent? content)
    {
        var blockers = new List<DadModuleBlockerDto>();
        content = queueService.Resolve(plan.Request.Trust, out var resolveBlocker);
        if (!string.IsNullOrWhiteSpace(resolveBlocker))
            blockers.Add(BuildBlocker("DutySelector", resolveBlocker, DadModuleBlockerSeverity.Blocked));

        if (participants.Count < Math.Max(1, module.ExpectedPartySize))
            blockers.Add(BuildBlocker("Participants", $"Need {Math.Max(1, module.ExpectedPartySize)} participant(s), have {participants.Count}.", DadModuleBlockerSeverity.Failed));

        if (module.ExpectedPartySize != 1)
            blockers.Add(BuildBlocker("Participants", "Native Trust executor only supports one local participant.", DadModuleBlockerSeverity.Blocked));

        if (!queueService.CanSelectTrustPartyForLocalPlayer(out var trustPartyBlocker))
            blockers.Add(BuildBlocker("TrustParty", trustPartyBlocker, DadModuleBlockerSeverity.Blocked));

        if (mode == DadCombatRotationMode.UseFrenRider && !combatRotationService.IsFrenRiderLoaded())
            blockers.Add(BuildBlocker("FrenRider", combatRotationService.MissingFrenRiderBlocker, DadModuleBlockerSeverity.Blocked));

        if (mode == DadCombatRotationMode.ForceCommands)
            blockers.Add(BuildBlocker("CombatRotation", "Force Commands mode is only guarded for Duty Support; select Use FrenRider or Do Nothing before starting Trust.", DadModuleBlockerSeverity.Blocked));

        return blockers
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker.Summary))
            .GroupBy(static blocker => $"{blocker.Capability}|{blocker.Summary}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    private static DadModuleBlockerDto BuildBlocker(
        string capability,
        string summary,
        DadModuleBlockerSeverity severity)
        => new()
        {
            ModuleId = DadModuleId.Trust,
            Capability = capability,
            Severity = severity,
            Summary = summary,
        };

    private static DadRunStepResultDto BuildStatusStep(DadModuleExecutionStatusDto status, DadParticipantState? participantState = null)
        => new()
        {
            RunId = status.RunId,
            ModuleId = DadModuleId.Trust,
            StepName = "Trust",
            ParticipantState = participantState
                               ?? (status.Status switch
                               {
                                   DadRunStatus.Cancelled => DadParticipantState.Cancelled,
                                   DadRunStatus.Failed => DadParticipantState.Failed,
                                   DadRunStatus.Completed => DadParticipantState.Completed,
                                   _ => DadParticipantState.QueuePending,
                               }),
            Success = status.Status is DadRunStatus.Running or DadRunStatus.Completed,
            Deferred = false,
            Summary = status.Summary,
            FailureReason = status.FailureReason,
            BlockedReason = status.BlockedReason,
            ExecutorStatus = status.Clone(),
            ModuleBlockers = status.Blockers.Select(static blocker => blocker.Clone()).ToList(),
            ReportedAtUtc = DateTime.UtcNow,
        };

    private static string FormatBlockers(IReadOnlyList<DadModuleBlockerDto> blockers)
        => blockers.Count == 0
            ? string.Empty
            : string.Join(" | ", blockers.Select(static blocker => $"{blocker.Capability}: {blocker.Summary}"));
}

public sealed class DadDailyMsqExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadDailyMsqExecutor", DadModuleId.DailyMsq, "Daily MSQ", queueBlockerFactory);

public sealed class DadBlundervilleExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadBlundervilleExecutor", DadModuleId.Blunderville, "Blunderville", queueBlockerFactory);

public sealed class DadMogtomeExecutor : IDadModuleExecutor
{
    private readonly DadMogtomeIpcService ipc;
    private DadModuleExecutionStatusDto status = new();
    private DadWorkerExecutionRole workerRole = DadWorkerExecutionRole.QueueLeader;

    public DadMogtomeExecutor(DadMogtomeIpcService ipc)
    {
        this.ipc = ipc;
    }

    public string ExecutorId => "DadMogtomeExecutor";
    public DadModuleId ModuleId => DadModuleId.Mogtome;

    public void SetWorkerRole(DadWorkerExecutionRole role)
        => workerRole = role;

    public DadModuleExecutionStatusDto CanStart(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var ready = ipc.IsReady();
        var summary = ready
            ? $"MOGTOME helper ready for {workerRole} handoff."
            : "MOGTOME helper IPC is unavailable.";
        return new DadModuleExecutionStatusDto
        {
            RunId = plan.Request.RequestId,
            ModuleId = DadModuleId.Mogtome,
            DisplayName = "MOGTOME",
            Phase = DadRunPhase.QueuePreparing,
            Status = ready ? DadRunStatus.Running : DadRunStatus.Failed,
            StepName = ExecutorId,
            CanStart = ready,
            Deferred = false,
            MaxRetryAttempts = 3,
            UpdatedAtUtc = DateTime.UtcNow,
            Summary = summary,
            FailureReason = ready ? string.Empty : summary,
            BlockedReason = ready ? string.Empty : summary,
        };
    }

    public DadRunStepResultDto Start(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants)
    {
        status = CanStart(plan, participants);
        status.StartedAtUtc = DateTime.UtcNow;
        if (!status.CanStart)
            return BuildStep();

        ApplyHelperStatus(ipc.Start(plan, workerRole));
        return BuildStep();
    }

    public DadRunStepResultDto Update()
    {
        if (status.IsActive)
            ApplyHelperStatus(ipc.GetStatus());
        return BuildStep();
    }

    public DadRunStepResultDto Cancel(string reason)
    {
        ApplyHelperStatus(ipc.Stop(status.RunId, reason));
        status.Status = DadRunStatus.Cancelled;
        status.Phase = DadRunPhase.Finalizing;
        status.IsActive = false;
        status.CompletedAtUtc = DateTime.UtcNow;
        status.Summary = string.IsNullOrWhiteSpace(reason) ? "MOGTOME helper cancelled." : reason;
        return BuildStep();
    }

    public DadModuleExecutionStatusDto GetStatus()
        => status.Clone();

    private void ApplyHelperStatus(DadMogtomeRunStatus helper)
    {
        status.UpdatedAtUtc = DateTime.UtcNow;
        status.Summary = helper.Summary;
        status.FailureReason = helper.FailureReason;
        status.BlockedReason = helper.Accepted || helper.Success ? string.Empty : helper.FailureReason;
        status.RetryAttempt = helper.CompletedAttempts;
        status.MaxRetryAttempts = Math.Max(helper.AttemptLimit, status.MaxRetryAttempts);
        status.IsActive = helper.IsRunning && !helper.IsTerminal;
        status.Phase = helper.IsTerminal
            ? DadRunPhase.Finalizing
            : string.Equals(helper.EngineState, "InDuty", StringComparison.OrdinalIgnoreCase)
                ? DadRunPhase.InDutyOrTask
                : DadRunPhase.WaitingForQueuePop;
        status.Status = helper.IsTerminal
            ? helper.Success ? DadRunStatus.Completed : DadRunStatus.Failed
            : helper.Accepted ? DadRunStatus.Running : DadRunStatus.Failed;
        if (helper.IsTerminal)
            status.CompletedAtUtc = DateTime.UtcNow;
    }

    private DadRunStepResultDto BuildStep()
        => new()
        {
            RunId = status.RunId,
            ModuleId = DadModuleId.Mogtome,
            StepName = "MOGTOME",
            ParticipantState = status.Status switch
            {
                DadRunStatus.Completed => DadParticipantState.Completed,
                DadRunStatus.Cancelled => DadParticipantState.Cancelled,
                DadRunStatus.Failed => DadParticipantState.Failed,
                _ => status.Phase == DadRunPhase.InDutyOrTask
                    ? DadParticipantState.Running
                    : DadParticipantState.QueuePending,
            },
            Success = status.Status is DadRunStatus.Running or DadRunStatus.Completed,
            Deferred = false,
            Summary = status.Summary,
            FailureReason = status.FailureReason,
            BlockedReason = status.BlockedReason,
            ExecutorStatus = status.Clone(),
            ReportedAtUtc = DateTime.UtcNow,
        };
}

public sealed class DadCommendationExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadCommendationExecutor", DadModuleId.Commendation, "Commendation", queueBlockerFactory);

public sealed class DadAstropeExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadAstropeExecutor", DadModuleId.Astrope, "Astrope", queueBlockerFactory);

public sealed class DadCustomDutyExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadCustomDutyExecutor", DadModuleId.CustomDuty, "Custom Duty", queueBlockerFactory);

public sealed class DadSquadronExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadSquadronExecutor", DadModuleId.Squadron, "Squadron", queueBlockerFactory);

public sealed class DadVariantVvdExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadVariantVvdExecutor", DadModuleId.VariantVvd, "Variant / VVD", queueBlockerFactory);
