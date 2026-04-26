using dad.Models;

namespace dad.Services;

public sealed class DadLocalDutyExecutor(
    DadLocalDutyQueueService queueService,
    DadCombatRotationService combatRotationService) : IDadModuleExecutor
{
    private static readonly TimeSpan PostDutyStabilizeDuration = TimeSpan.FromSeconds(10);

    private DadModuleExecutionStatusDto status = new();
    private DadLocalDutyResolvedContent? resolvedContent;
    private DateTime runStartedAtUtc = DateTime.MinValue;
    private DateTime postDutyStabilizeUntilUtc = DateTime.MinValue;
    private bool enteredDuty;
    private bool dutyCompleted;
    private DadCombatRotationMode rotationMode = DadCombatRotationMode.UseFrenRider;

    public string ExecutorId => "DadLocalDutyExecutor";
    public DadModuleId ModuleId => DadModuleId.Duty;

    public DadModuleExecutionStatusDto CanStart(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var module = ResolveModule(plan);
        var mode = combatRotationService.CombatRotationMode;
        var blockers = BuildBlockers(plan, module, participants, mode, out var content);
        var blockedReason = FormatBlockers(blockers);
        var hardBlocked = blockers.Any(static blocker =>
            blocker.Severity is DadModuleBlockerSeverity.Blocked or DadModuleBlockerSeverity.Failed);

        return new DadModuleExecutionStatusDto
        {
            RunId = plan.Request.RequestId,
            ModuleId = DadModuleId.Duty,
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
                ? $"Dad cannot start Local Duty: {blockedReason}"
                : BuildCanStartSummary(content, mode),
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
            ModuleId = DadModuleId.Duty,
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
                ? $"Dad cannot start Local Duty: {blockedReason}"
                : BuildStartSummary(resolvedContent),
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
            Fail("Local Duty content was not resolved.");
            return BuildStatusStep(status);
        }

        var now = DateTime.UtcNow;
        if (postDutyStabilizeUntilUtc != DateTime.MinValue)
            return UpdatePostDutyStabilizing(now);

        if (enteredDuty && HasExitedRequestedDuty())
        {
            if (!dutyCompleted)
            {
                Fail($"Local Duty {resolvedContent.DutyName} exited before DutyCompleted; treating as abandoned.");
                return BuildStatusStep(status, DadParticipantState.Failed);
            }

            return BeginOrUpdatePostDutyStabilizing(now);
        }

        if (enteredDuty && !dutyCompleted && queueService.HasDutyCompleted(resolvedContent, runStartedAtUtc))
            dutyCompleted = true;

        if (dutyCompleted)
            return UpdateDutyCompletionWaitForExit();

        var pulse = queueService.Pulse(status.RunId, resolvedContent);
        var enteredDutyThisPulse = pulse.Kind == DadLocalDutyQueuePulseKind.EnteredDuty;
        if (enteredDutyThisPulse)
            enteredDuty = true;

        ApplyPulse(pulse);
        if (pulse.Status == DadRunStatus.Failed)
            return BuildStatusStep(status, pulse.ParticipantState);

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
        var pulse = queueService.Cancel(status.RunId, reason);
        ApplyPulse(pulse);
        status.Summary = string.IsNullOrWhiteSpace(reason)
            ? "Local Duty executor cancelled. Dad does not leave duties or send external stop commands; clear any remaining game-side queue or duty state manually if needed."
            : reason;
        status.FailureReason = pulse.FailureReason;
        status.CompletedAtUtc = DateTime.UtcNow;
        ClearRuntimeState();
        return BuildStatusStep(status, DadParticipantState.Cancelled);
    }

    public DadModuleExecutionStatusDto GetStatus()
        => status.Clone();

    private void ApplyPulse(DadLocalDutyQueuePulse pulse)
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
                $"Local Duty post-duty stabilizing ({remaining:F0}s).");
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

    private void ResetRuntimeState(DateTime now)
    {
        runStartedAtUtc = now;
        postDutyStabilizeUntilUtc = DateTime.MinValue;
        enteredDuty = false;
        dutyCompleted = false;
    }

    private void ClearRuntimeState()
    {
        runStartedAtUtc = DateTime.MinValue;
        postDutyStabilizeUntilUtc = DateTime.MinValue;
        enteredDuty = false;
        dutyCompleted = false;
    }

    private static DadPlannedModuleExecution ResolveModule(DadRunPlan plan)
        => plan.Modules.FirstOrDefault(module => module.ModuleId == DadModuleId.Duty)
           ?? new DadPlannedModuleExecution
           {
               ModuleId = DadModuleId.Duty,
               DisplayName = "Local Duty",
               ExpectedPartySize = 1,
           };

    private static string BuildCanStartSummary(DadLocalDutyResolvedContent? content, DadCombatRotationMode mode)
    {
        var dutyName = content?.DutyName ?? "selected duty";
        var syncMode = content?.Unsynced == true ? "unsynced" : "synced";
        return mode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Dad can start {syncMode} regular Duty Finder queue for {dutyName}; Dad will send /fr on before queue, then observe while FrenRider or the user owns duty behavior and exit.",
            DadCombatRotationMode.DoNothing => $"Dad can start {syncMode} regular Duty Finder queue for {dutyName}; no external automation commands will be sent.",
            _ => $"Dad can start {syncMode} regular Duty Finder queue for {dutyName}.",
        };
    }

    private string BuildStartSummary(DadLocalDutyResolvedContent? content)
    {
        var dutyName = content?.DutyName ?? "selected duty";
        var syncMode = content?.Unsynced == true ? "unsynced" : "synced";
        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Use FrenRider mode: /fr on requested before queue; queueing {syncMode} Local Duty {dutyName}.",
            DadCombatRotationMode.DoNothing => $"Do Nothing mode: queueing {syncMode} Local Duty {dutyName}.",
            _ => $"Queueing {syncMode} Local Duty {dutyName}.",
        };
    }

    private string BuildPreDutySummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            summary = $"Waiting to start regular Duty Finder queue for {resolvedContent?.DutyName ?? "requested duty"}.";

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
        var syncMode = resolvedContent?.Unsynced == true ? "unsynced" : "synced";
        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Use FrenRider mode: in {syncMode} Local Duty {dutyName}; Dad is observing completion and exit while FrenRider or the user owns in-duty behavior.",
            DadCombatRotationMode.DoNothing => $"Do Nothing mode: Dad queued {syncMode} Local Duty {dutyName} and is observing completion/exit; user owns combat and leave.",
            _ => $"Dad is observing {syncMode} Local Duty {dutyName}.",
        };
    }

    private string BuildDutyCompleteWaitingForExitSummary()
    {
        var dutyName = resolvedContent?.DutyName ?? "requested duty";
        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Local Duty {dutyName} completed; waiting for FrenRider or user to leave. Dad will keep observing and will not send a FrenRider disable command.",
            DadCombatRotationMode.DoNothing => $"Local Duty {dutyName} completed; waiting for user-owned duty exit.",
            _ => $"Local Duty {dutyName} completed; waiting for duty exit.",
        };
    }

    private string BuildCompletedSummary()
    {
        var dutyName = resolvedContent?.DutyName ?? "requested duty";
        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Local Duty {dutyName} completed and stabilized; Dad FrenRider-mode run done without sending a FrenRider disable command.",
            DadCombatRotationMode.DoNothing => $"Local Duty {dutyName} completed; Dad queue-only run done.",
            _ => $"Local Duty {dutyName} completed; Dad run done.",
        };
    }

    private List<DadModuleBlockerDto> BuildBlockers(
        DadRunPlan plan,
        DadPlannedModuleExecution module,
        IReadOnlyList<DadParticipantSnapshot> participants,
        DadCombatRotationMode mode,
        out DadLocalDutyResolvedContent? content)
    {
        var blockers = new List<DadModuleBlockerDto>();
        content = queueService.Resolve(plan.Request.Dungeon, out var resolveBlocker);
        if (!string.IsNullOrWhiteSpace(resolveBlocker))
            blockers.Add(BuildBlocker("DutySelector", resolveBlocker, DadModuleBlockerSeverity.Blocked));

        if (plan.Request.Dungeon?.Count > 1)
            blockers.Add(BuildBlocker("Requeue", "Local Duty live executor currently supports one run; requeue/retry loop remains deferred.", DadModuleBlockerSeverity.Blocked));

        if (participants.Count != 1)
            blockers.Add(BuildBlocker("Participants", $"Local Duty executor requires exactly one local participant; have {participants.Count}.", DadModuleBlockerSeverity.Blocked));

        if (participants.All(static participant => !participant.IsLocalClient))
            blockers.Add(BuildBlocker("Participants", "Local Duty executor requires the one participant to be the local client.", DadModuleBlockerSeverity.Blocked));

        if (module.ExpectedPartySize != 1)
            blockers.Add(BuildBlocker("Participants", "Local Duty executor only supports one local runner.", DadModuleBlockerSeverity.Blocked));

        if (!queueService.CanStart(content, out var runtimeBlocker))
            blockers.Add(BuildBlocker("RuntimeReadiness", runtimeBlocker, DadModuleBlockerSeverity.Blocked));

        if (mode == DadCombatRotationMode.UseFrenRider && !combatRotationService.IsFrenRiderLoaded())
            blockers.Add(BuildBlocker("FrenRider", combatRotationService.MissingFrenRiderBlocker, DadModuleBlockerSeverity.Blocked));

        if (mode == DadCombatRotationMode.ForceCommands)
            blockers.Add(BuildBlocker("CombatRotation", "Force Commands mode is only guarded for Duty Support; select Use FrenRider or Do Nothing before starting Local Duty.", DadModuleBlockerSeverity.Blocked));

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
            ModuleId = DadModuleId.Duty,
            Capability = capability,
            Severity = severity,
            Summary = summary,
        };

    private static DadRunStepResultDto BuildStatusStep(DadModuleExecutionStatusDto status, DadParticipantState? participantState = null)
        => new()
        {
            RunId = status.RunId,
            ModuleId = DadModuleId.Duty,
            StepName = "Local Duty",
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
