using dad.Models;

namespace dad.Services;

public sealed class DadPremadeDutyExecutor(
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
    private string entryAutomationSummary = string.Empty;

    public string ExecutorId => "DadPremadeDutyExecutor";
    public DadModuleId ModuleId => DadModuleId.PremadeDuty;

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
            ModuleId = module.ModuleId,
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
                ? $"Dad cannot start Premade Duty: {blockedReason}"
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
            ModuleId = module.ModuleId,
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
                ? $"Dad cannot start Premade Duty: {blockedReason}"
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
            Fail("Premade Duty content was not resolved.");
            return BuildStatusStep(status);
        }

        var now = DateTime.UtcNow;
        if (postDutyStabilizeUntilUtc != DateTime.MinValue)
            return UpdatePostDutyStabilizing(now);

        if (enteredDuty && HasExitedRequestedDuty())
        {
            if (!dutyCompleted)
            {
                Fail($"Premade Duty {resolvedContent.DutyName} exited before DutyCompleted; treating as abandoned.");
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
        var enteredDutyThisPulse = pulse.Kind == DadLocalDutyQueuePulseKind.EnteredDuty;
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
        var pulse = queueService.Cancel(status.RunId, reason);
        ApplyPulse(pulse);
        status.Summary = string.IsNullOrWhiteSpace(reason)
            ? "Premade Duty executor cancelled. Dad does not leave duties or send external stop commands; clear any remaining game-side queue or duty state manually if needed."
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
            BuildBlocker(status.ModuleId == DadModuleId.None ? DadModuleId.PremadeDuty : status.ModuleId, "RuntimeReadiness", reason, DadModuleBlockerSeverity.Failed),
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
                $"Premade Duty post-duty stabilizing ({remaining:F0}s).");
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
        if (rotationMode != DadCombatRotationMode.UseFrenRider)
            return true;

        var entryEnableStatus = combatRotationService.TryEnableFrenRiderAfterDutyEntry(
            status.RunId,
            DadModuleId.PremadeDuty,
            DateTime.UtcNow,
            out entryAutomationSummary);
        if (entryEnableStatus != DadFrenRiderEntryEnableStatus.Failed)
            return true;

        Fail(entryAutomationSummary);
        return false;
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
    {
        var module = plan.Modules.FirstOrDefault(module => module.ModuleId == DadModuleId.PremadeDuty);
        if (module != null)
            return module;

        if (plan.Request.Dungeon?.QueueViaLanParty == true)
        {
            module = plan.Modules.FirstOrDefault(module => module.ModuleId == DadModuleId.Duty);
            if (module != null)
                return module;
        }

        return new DadPlannedModuleExecution
        {
            ModuleId = DadModuleId.PremadeDuty,
            DisplayName = "Premade Duty",
            ExpectedPartySize = Math.Max(2, plan.RequiredParticipantCount),
            RequiresPeers = true,
        };
    }

    private string BuildCanStartSummary(DadLocalDutyResolvedContent? content, DadCombatRotationMode mode)
    {
        var dutyName = content?.DutyName ?? "selected duty";
        var syncMode = content?.Unsynced == true ? "unsynced" : "synced";
        var expectedPartySize = content?.ExpectedPartySize ?? 0;
        var baseSummary = $"Dad can start {syncMode} regular Duty Finder queue for Premade Duty {dutyName} with {expectedPartySize} Dad-verified participant(s); in-game party roster validation remains manual follow-up.";
        return mode switch
        {
            DadCombatRotationMode.UseFrenRider => $"{baseSummary} Dad will enable FrenRider after duty entry, then observe while FrenRider or the user owns duty behavior and exit.",
            DadCombatRotationMode.DoNothing => $"{baseSummary} No external automation commands will be sent.",
            _ => baseSummary,
        };
    }

    private string BuildStartSummary(DadLocalDutyResolvedContent? content)
    {
        var dutyName = content?.DutyName ?? "selected duty";
        var syncMode = content?.Unsynced == true ? "unsynced" : "synced";
        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Use FrenRider mode: queueing {syncMode} Premade Duty {dutyName}; Dad will enable FrenRider after duty entry.",
            DadCombatRotationMode.DoNothing => $"Do Nothing mode: queueing {syncMode} Premade Duty {dutyName}.",
            _ => $"Queueing {syncMode} Premade Duty {dutyName}.",
        };
    }

    private string BuildPreDutySummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            summary = $"Waiting to start regular Duty Finder queue for Premade Duty {resolvedContent?.DutyName ?? "requested duty"}.";

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
            DadCombatRotationMode.UseFrenRider => string.IsNullOrWhiteSpace(entryAutomationSummary)
                ? $"Use FrenRider mode: in {syncMode} Premade Duty {dutyName}; Dad is observing completion and exit while FrenRider or the user owns in-duty behavior."
                : $"{entryAutomationSummary} In {syncMode} Premade Duty {dutyName}; Dad is observing completion and exit while FrenRider or the user owns in-duty behavior.",
            DadCombatRotationMode.DoNothing => $"Do Nothing mode: Dad queued {syncMode} Premade Duty {dutyName} and is observing completion/exit; user owns combat and leave.",
            _ => $"Dad is observing {syncMode} Premade Duty {dutyName}.",
        };
    }

    private string BuildDutyCompleteWaitingForExitSummary()
    {
        var dutyName = resolvedContent?.DutyName ?? "requested duty";
        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Premade Duty {dutyName} completed; waiting for FrenRider or user to leave. Disable commands are reserved for successful final dad.Duty.Run IPC cleanup.",
            DadCombatRotationMode.DoNothing => $"Premade Duty {dutyName} completed; waiting for user-owned duty exit.",
            _ => $"Premade Duty {dutyName} completed; waiting for duty exit.",
        };
    }

    private string BuildCompletedSummary()
    {
        var dutyName = resolvedContent?.DutyName ?? "requested duty";
        return rotationMode switch
        {
            DadCombatRotationMode.UseFrenRider => $"Premade Duty {dutyName} completed and stabilized; normal Dad run done without disable commands. Successful final dad.Duty.Run IPC cleanup is separate.",
            DadCombatRotationMode.DoNothing => $"Premade Duty {dutyName} completed; Dad queue-only run done.",
            _ => $"Premade Duty {dutyName} completed; Dad run done.",
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
        content = ResolveContent(plan, out var resolveBlocker);
        if (!string.IsNullOrWhiteSpace(resolveBlocker))
            blockers.Add(BuildBlocker(module.ModuleId, "DutySelector", resolveBlocker, DadModuleBlockerSeverity.Blocked));

        var expectedPartySize = Math.Max(2, content?.ExpectedPartySize ?? Math.Max(module.ExpectedPartySize, plan.RequiredParticipantCount));
        if (plan.Request.PremadeDuty?.Attempts > 1 || plan.Request.Dungeon?.Count > 1)
            blockers.Add(BuildBlocker(module.ModuleId, "Requeue", "Premade Duty live executor currently supports one run; requeue/retry loop remains deferred.", DadModuleBlockerSeverity.Blocked));

        if (participants.Count != expectedPartySize)
            blockers.Add(BuildBlocker(module.ModuleId, "Participants", $"Premade Duty requires exactly {expectedPartySize} Dad-verified participant(s), have {participants.Count}.", DadModuleBlockerSeverity.Failed));

        var unverifiedParticipants = participants
            .Where(static participant => !IsDadVerifiedParticipant(participant))
            .Select(static participant => string.IsNullOrWhiteSpace(participant.ActiveCharacterKey) ? participant.AssignedSlotId : participant.ActiveCharacterKey.ToString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unverifiedParticipants.Count > 0)
            blockers.Add(BuildBlocker(module.ModuleId, "Participants", $"Full Dad participant readiness is not verified for: {string.Join(", ", unverifiedParticipants)}.", DadModuleBlockerSeverity.Blocked));

        var duplicateCharacters = participants
            .Select(static participant => participant.ActiveCharacterKey.ToString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToList();
        if (duplicateCharacters.Count > 0)
            blockers.Add(BuildBlocker(module.ModuleId, "Participants", $"Premade Duty participant list has duplicate character(s): {string.Join(", ", duplicateCharacters)}.", DadModuleBlockerSeverity.Blocked));

        var localLeader = participants.FirstOrDefault(static participant => participant.IsLocalClient);
        if (localLeader == null)
        {
            blockers.Add(BuildBlocker(module.ModuleId, "LeaderAuthority", "Premade Duty requires the Dad Coordinator local client to be present as the queue leader.", DadModuleBlockerSeverity.Blocked));
        }
        else
        {
            if (!localLeader.IsAuthority)
                blockers.Add(BuildBlocker(module.ModuleId, "LeaderAuthority", "Local client is not marked as Dad Coordinator authority for this premade queue.", DadModuleBlockerSeverity.Blocked));

            if (!string.Equals(localLeader.AssignedSlotId, DadPlannerSlotRules.LeaderSlotId, StringComparison.OrdinalIgnoreCase))
                blockers.Add(BuildBlocker(module.ModuleId, "LeaderAuthority", $"Local client is assigned to '{localLeader.AssignedSlotId}', not Slot1.", DadModuleBlockerSeverity.Blocked));

            if (!string.IsNullOrWhiteSpace(plan.LeaderCharacterKey) &&
                !string.Equals(localLeader.ActiveCharacterKey.ToString(), plan.LeaderCharacterKey, StringComparison.OrdinalIgnoreCase))
            {
                blockers.Add(BuildBlocker(module.ModuleId, "LeaderAuthority", $"Local leader mismatch: need {plan.LeaderCharacterKey}, active {localLeader.ActiveCharacterKey}.", DadModuleBlockerSeverity.Blocked));
            }
        }

        if (plan.Orchestration.AuthorityMode != DadAuthorityMode.ServerDad)
            blockers.Add(BuildBlocker(module.ModuleId, "LeaderAuthority", "Premade Duty requires Dad Coordinator authority, not local-only authority.", DadModuleBlockerSeverity.Blocked));

        if (plan.Orchestration.QueueAuthority is not (DadQueueAuthority.Leader or DadQueueAuthority.LanParty))
            blockers.Add(BuildBlocker(module.ModuleId, "QueueAuthority", $"Premade Duty requires leader/Dad premade queue authority; current authority is {plan.Orchestration.QueueAuthority}.", DadModuleBlockerSeverity.Blocked));

        if (!queueService.CanStart(content, out var runtimeBlocker))
            blockers.Add(BuildBlocker(module.ModuleId, "RuntimeReadiness", runtimeBlocker, DadModuleBlockerSeverity.Blocked));

        if (mode == DadCombatRotationMode.UseFrenRider && !combatRotationService.IsFrenRiderLoaded())
            blockers.Add(BuildBlocker(module.ModuleId, "FrenRider", combatRotationService.MissingFrenRiderBlocker, DadModuleBlockerSeverity.Blocked));

        if (mode == DadCombatRotationMode.ForceCommands)
            blockers.Add(BuildBlocker(module.ModuleId, "CombatRotation", "Force Commands mode is only guarded for Duty Support; select Use FrenRider or Do Nothing before starting Premade Duty.", DadModuleBlockerSeverity.Blocked));

        return blockers
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker.Summary))
            .GroupBy(static blocker => $"{blocker.Capability}|{blocker.Summary}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    private DadLocalDutyResolvedContent? ResolveContent(DadRunPlan plan, out string blocker)
    {
        if (plan.Request.PremadeDuty != null)
            return queueService.Resolve(plan.Request.PremadeDuty, out blocker);

        if (plan.Request.Dungeon?.QueueViaLanParty == true)
            return queueService.ResolvePremade(plan.Request.Dungeon, out blocker);

        blocker = "Premade Duty request is missing a premade duty task.";
        return null;
    }

    private static bool IsDadVerifiedParticipant(DadParticipantSnapshot participant)
    {
        if (!participant.IsAvailable ||
            !participant.IsEligibleForRun ||
            !participant.PostArReady ||
            participant.ClaimState != DadClaimState.Granted ||
            participant.LeaseState != DadParticipantLeaseState.Granted)
        {
            return false;
        }

        return participant.State is not (DadParticipantState.WaitingForRequiredCharacter
            or DadParticipantState.WaitingForPostArReady
            or DadParticipantState.Failed
            or DadParticipantState.Cancelled
            or DadParticipantState.Stale);
    }

    private static DadModuleBlockerDto BuildBlocker(
        DadModuleId moduleId,
        string capability,
        string summary,
        DadModuleBlockerSeverity severity)
        => new()
        {
            ModuleId = moduleId,
            Capability = capability,
            Severity = severity,
            Summary = summary,
        };

    private static DadRunStepResultDto BuildStatusStep(DadModuleExecutionStatusDto status, DadParticipantState? participantState = null)
        => new()
        {
            RunId = status.RunId,
            ModuleId = status.ModuleId,
            StepName = "Premade Duty",
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
