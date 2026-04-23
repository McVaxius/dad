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
        nextStatus.CompletedAtUtc = nextStatus.CanStart ? nextStatus.StartedAtUtc : null;
        nextStatus.IsActive = false;
        nextStatus.Status = nextStatus.CanStart ? DadRunStatus.Completed : DadRunStatus.Failed;
        nextStatus.Summary = nextStatus.CanStart
            ? $"Dad routed {nextStatus.DisplayName} with {participants.Count}/{ResolveModule(plan).ExpectedPartySize} ready participant(s)."
            : nextStatus.Summary;
        nextStatus.FailureReason = nextStatus.CanStart ? string.Empty : nextStatus.BlockedReason;
        status = nextStatus;

        return new DadRunStepResultDto
        {
            RunId = plan.Request.RequestId,
            ModuleId = nextStatus.ModuleId,
            StepName = nextStatus.DisplayName,
            ParticipantState = nextStatus.CanStart ? DadParticipantState.QueuePending : DadParticipantState.Failed,
            Success = nextStatus.CanStart,
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
           || (plan.Request.CustomDuty?.Attempts ?? 0) > 1;

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

public sealed class DadLocalDutyExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadLocalDutyExecutor", DadModuleId.Duty, "Local Duty", queueBlockerFactory);

public sealed class DadPremadeDutyExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadPremadeDutyExecutor", DadModuleId.PremadeDuty, "Premade Duty", queueBlockerFactory);

public sealed class DadMsqExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadMsqExecutor", DadModuleId.Msq, "MSQ", queueBlockerFactory);

public sealed class DadDutySupportExecutor(
    DadDutySupportQueueService queueService,
    DadDutySupportAdsService adsService,
    DadCombatRotationService combatRotationService) : IDadModuleExecutor
{
    private static readonly TimeSpan LeaveRetryCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PostDutyStabilizeDuration = TimeSpan.FromSeconds(10);

    private DadModuleExecutionStatusDto status = new();
    private DadDutySupportResolvedContent? resolvedContent;
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
        var enteredDutyThisPulse = pulse.Kind == DadDutySupportQueuePulseKind.EnteredDuty;
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

        if (adsOutsideArmed && !string.IsNullOrWhiteSpace(status.Summary))
            status.Summary = $"Force Commands mode: ADS outside armed; {status.Summary}";

        return BuildStatusStep(status, pulse.ParticipantState);
    }

    public DadRunStepResultDto Cancel(string reason)
    {
        if (UsesAdsDutyFlow())
            adsService.TryStop(out _);
        var pulse = queueService.Cancel(status.RunId, reason);
        ApplyPulse(pulse);
        status.Summary = pulse.Summary;
        status.FailureReason = pulse.FailureReason;
        status.CompletedAtUtc = DateTime.UtcNow;
        ClearRuntimeState();
        return BuildStatusStep(status, DadParticipantState.Cancelled);
    }

    public DadModuleExecutionStatusDto GetStatus()
        => status.Clone();

    private void ApplyPulse(DadDutySupportQueuePulse pulse)
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
                $"ADS running duty; duty complete, leave blocked ({leaveBlocker}).",
                $"Leave blocked: {leaveBlocker}",
                [BuildBlocker("LeaveSafety", $"Leave blocked: {leaveBlocker}", DadModuleBlockerSeverity.Deferred)]);
            return BuildStatusStep(status, DadParticipantState.Running);
        }

        if (leaveRequested &&
            !leaveConfirmationObserved &&
            adsService.TryObserveLeaveEvidence(out var evidence))
        {
            leaveConfirmationObserved = true;
            status.Summary = $"ADS leave requested; observed {evidence}, waiting for duty exit.";
        }

        if (!leaveRequested || now >= nextLeaveAttemptUtc)
            return RequestAdsLeave(now);

        var remaining = Math.Max(0, (nextLeaveAttemptUtc - now).TotalSeconds);
        var evidenceText = leaveConfirmationObserved ? " Leave evidence observed." : string.Empty;
        SetActiveStatus(
            DadRunPhase.InDutyOrTask,
            $"ADS leave requested; waiting for duty exit ({remaining:F0}s to retry).{evidenceText}");
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

        var completedDelta = dutyCompletedAtUtc == DateTime.MinValue
            ? "n/a"
            : $"+{Math.Max(0, (now - dutyCompletedAtUtc).TotalSeconds):F1}s";
        var attemptText = leaveAttemptCount <= 1 ? string.Empty : $" (retry {leaveAttemptCount})";
        SetActiveStatus(
            DadRunPhase.InDutyOrTask,
            $"ADS leave requested{attemptText}; waiting for duty exit ({completedDelta} from DutyCompleted).");
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

        entryAutomationAttempted = true;
        var succeeded = combatRotationService.TryApplyDutySupportEntryMode(
            rotationMode,
            out entryAutomationSummary,
            out var shouldFailRun);
        if (succeeded || !shouldFailRun)
            return true;

        Fail(entryAutomationSummary);
        return false;
    }

    private static bool ProtectsAdsOwnershipAfterQueue(DadDutySupportQueuePulseKind pulseKind)
        => pulseKind is DadDutySupportQueuePulseKind.AcceptedQueueConfirm
            or DadDutySupportQueuePulseKind.WaitingForQueue
            or DadDutySupportQueuePulseKind.DutyEntryTransition
            or DadDutySupportQueuePulseKind.EnteredDuty;

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
            DadCombatRotationMode.UseFrenRider => $"Dad can start native Duty Support queue for {dutyName}; FrenRider will be requested before queue.",
            DadCombatRotationMode.ForceCommands => $"Dad can start native Duty Support queue for {dutyName}; ADS and fixed rotation commands will be used.",
            DadCombatRotationMode.DoNothing => $"Dad can start native Duty Support queue for {dutyName}; no external automation commands will be sent.",
            _ => $"Dad can start native Duty Support queue for {dutyName}.",
        };

    private string BuildStartSummary(string? dutyName)
        => rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Use FrenRider mode: starting native Duty Support queue for {dutyName}.",
            DadCombatRotationMode.ForceCommands => $"Force Commands mode: starting native Duty Support queue for {dutyName}.",
            DadCombatRotationMode.DoNothing => $"Do Nothing mode: starting native Duty Support queue for {dutyName}.",
            _ => $"Starting native Duty Support queue for {dutyName}.",
        };

    private string BuildInDutySummary()
    {
        var dutyName = resolvedContent?.DutyName ?? "requested duty";
        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => string.IsNullOrWhiteSpace(entryAutomationSummary)
                ? $"Use FrenRider mode: Dad is observing {dutyName}; FrenRider owns in-duty behavior and exit."
                : $"{entryAutomationSummary} Dad is observing {dutyName}; FrenRider owns in-duty behavior and exit.",
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
            DadCombatRotationMode.UseFrenRider => $"Duty Support duty {dutyName} completed; waiting for FrenRider or user to leave. Dad will not send a FrenRider disable command.",
            DadCombatRotationMode.DoNothing => $"Duty Support duty {dutyName} completed; waiting for user-owned duty exit.",
            _ => $"Duty Support duty {dutyName} completed; waiting for duty exit.",
        };
    }

    private string BuildCompletedSummary()
    {
        var dutyName = resolvedContent?.DutyName ?? "requested duty";
        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Duty Support duty {dutyName} completed; Dad FrenRider-mode run done without sending a FrenRider disable command.",
            DadCombatRotationMode.ForceCommands => $"Duty Support duty {dutyName} completed; Dad ADS run done.",
            DadCombatRotationMode.DoNothing => $"Duty Support duty {dutyName} completed; Dad queue-only run done.",
            _ => $"Duty Support duty {dutyName} completed; Dad run done.",
        };
    }

    private List<DadModuleBlockerDto> BuildBlockers(
        DadRunPlan plan,
        DadPlannedModuleExecution module,
        IReadOnlyList<DadParticipantSnapshot> participants,
        DadCombatRotationMode mode,
        out DadDutySupportResolvedContent? content)
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
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadTrustExecutor", DadModuleId.Trust, "Trust", queueBlockerFactory);

public sealed class DadDailyMsqExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadDailyMsqExecutor", DadModuleId.DailyMsq, "Daily MSQ", queueBlockerFactory);

public sealed class DadBlundervilleExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadBlundervilleExecutor", DadModuleId.Blunderville, "Blunderville", queueBlockerFactory);

public sealed class DadMogtomeExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadMogtomeExecutor", DadModuleId.Mogtome, "MOGTOME", queueBlockerFactory);

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
