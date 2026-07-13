using System.Collections.Concurrent;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadWorkerExecutionService
{
    private readonly DadQueueExecutionService queueExecutionService;
    private readonly DadPresenceService presenceService;
    private readonly DadCombatRotationService combatRotationService;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly ConcurrentQueue<DadWorkerExecutionCommand> pendingCommands = new();
    private readonly object stateLock = new();
    private readonly DadParticipantFrenRiderHandoffGate participantFrenRiderHandoffGate = new();

    private DadWorkerExecutionCommand? activeCommand;
    private DadWorkerExecutionStatus status = new();
    private DateTime startedAtUtc = DateTime.MinValue;
    private bool enteredDuty;
    private DadLocalDutyResolvedContent? participantQueueContent;
    private string lastParticipantQueueTransition = string.Empty;
    private DadCombatRotationMode participantCombatRotationMode = DadCombatRotationMode.UseFrenRider;

    public DadWorkerExecutionService(
        DadQueueExecutionService queueExecutionService,
        DadPresenceService presenceService,
        DadCombatRotationService combatRotationService,
        ICondition condition,
        IPluginLog log)
    {
        this.queueExecutionService = queueExecutionService;
        this.presenceService = presenceService;
        this.combatRotationService = combatRotationService;
        this.condition = condition;
        this.log = log;
        status.WorkerSessionId = presenceService.WorkerSessionId;
    }

    public DadWorkerExecutionAck Accept(DadWorkerExecutionCommand command)
    {
        lock (stateLock)
        {
            if (!DadWorkerCommandValidationRules.TryValidate(
                    command,
                    presenceService.BuildSnapshotCopy(),
                    out _,
                    out var validationBlocker))
            {
                return BuildAck(false, command, $"Worker assignment rejected: {validationBlocker}");
            }

            if (activeCommand != null && status.IsTerminal)
                activeCommand = null;

            if (activeCommand != null && !status.IsTerminal &&
                !string.Equals(activeCommand.RunId, command.RunId, StringComparison.OrdinalIgnoreCase))
            {
                return BuildAck(false, command, $"Worker already owns run {activeCommand.RunId}.");
            }

            if (activeCommand != null &&
                string.Equals(activeCommand.CommandId, command.CommandId, StringComparison.OrdinalIgnoreCase))
            {
                return BuildAck(true, command, "Worker assignment already accepted.");
            }

            pendingCommands.Enqueue(command);
            status = new DadWorkerExecutionStatus
            {
                CommandId = command.CommandId,
                RunId = command.RunId,
                WorkerSessionId = presenceService.WorkerSessionId,
                Role = command.Role,
                State = DadWorkerExecutionState.Accepted,
                ModuleId = ResolveModule(command)?.ModuleId ?? DadModuleId.None,
                UpdatedAtUtc = DateTime.UtcNow,
                Summary = $"Accepted {command.Role} assignment.",
            };
            return BuildAck(true, command, status.Summary);
        }
    }

    public DadWorkerExecutionAck Cancel(DadWorkerExecutionCancel cancel)
    {
        lock (stateLock)
        {
            // Active command matches → cancel the running execution.
            if (activeCommand != null &&
                string.Equals(activeCommand.RunId, cancel.RunId, StringComparison.OrdinalIgnoreCase))
            {
                if (activeCommand.Role == DadWorkerExecutionRole.QueueLeader ||
                    status.ModuleId == DadModuleId.Mogtome)
                    queueExecutionService.CancelActiveExecutor(cancel.Reason);
                else if (participantQueueContent != null)
                    queueExecutionService.ResetParticipantQueueObserver(activeCommand.RunId);

                Finish(DadWorkerExecutionState.Cancelled, false, cancel.Reason, cancel.Reason);
                return new DadWorkerExecutionAck
                {
                    CommandId = activeCommand.CommandId,
                    RunId = activeCommand.RunId,
                    WorkerSessionId = presenceService.WorkerSessionId,
                    Accepted = true,
                    Summary = status.Summary,
                    Status = status.Clone(),
                };
            }

            // Review H3: a cancel can arrive after ACK but before Update() dequeues the command.
            // Drain any matching pending command so it never starts on the next frame.
            if (DrainPendingForRun(cancel.RunId))
            {
                participantFrenRiderHandoffGate.Reset();
                status = new DadWorkerExecutionStatus
                {
                    RunId = cancel.RunId,
                    WorkerSessionId = presenceService.WorkerSessionId,
                    State = DadWorkerExecutionState.Cancelled,
                    UpdatedAtUtc = DateTime.UtcNow,
                    Summary = "Pending worker assignment cancelled before start.",
                };
                return new DadWorkerExecutionAck
                {
                    RunId = cancel.RunId,
                    WorkerSessionId = presenceService.WorkerSessionId,
                    Accepted = true,
                    Summary = status.Summary,
                    Status = status.Clone(),
                };
            }

            return new DadWorkerExecutionAck
            {
                RunId = cancel.RunId,
                WorkerSessionId = presenceService.WorkerSessionId,
                Accepted = false,
                Summary = "Worker has no matching run-owned execution.",
                Status = status.Clone(),
            };
        }
    }

    public DadWorkerExecutionAck CancelAll(string reason)
    {
        lock (stateLock)
        {
            var hadWork = activeCommand != null || !pendingCommands.IsEmpty || !status.IsTerminal && status.State != DadWorkerExecutionState.Idle;
            while (pendingCommands.TryDequeue(out _))
            {
            }

            var runId = activeCommand?.RunId ?? status.RunId;
            var commandId = activeCommand?.CommandId ?? status.CommandId;
            if (activeCommand?.Role == DadWorkerExecutionRole.Participant && participantQueueContent != null)
                queueExecutionService.ResetParticipantQueueObserver(activeCommand.RunId);
            queueExecutionService.CancelAll(reason);
            activeCommand = null;
            participantQueueContent = null;
            lastParticipantQueueTransition = string.Empty;
            participantFrenRiderHandoffGate.Reset();
            status = new DadWorkerExecutionStatus
            {
                CommandId = commandId,
                RunId = runId,
                WorkerSessionId = presenceService.WorkerSessionId,
                State = hadWork ? DadWorkerExecutionState.Cancelled : DadWorkerExecutionState.Idle,
                IsTerminal = hadWork,
                UpdatedAtUtc = DateTime.UtcNow,
                Summary = hadWork ? reason : "No local DAD worker execution was active.",
                FailureReason = hadWork ? reason : string.Empty,
            };
            return new DadWorkerExecutionAck
            {
                CommandId = commandId,
                RunId = runId,
                WorkerSessionId = presenceService.WorkerSessionId,
                Accepted = true,
                Summary = status.Summary,
                Status = status.Clone(),
            };
        }
    }

    // Review H3: remove all pending (accepted-but-not-started) commands for a run id.
    // Caller must hold stateLock. ConcurrentQueue has no arbitrary removal, so re-enqueue the keepers.
    private bool DrainPendingForRun(string runId)
    {
        var removed = false;
        var kept = new List<DadWorkerExecutionCommand>();
        while (pendingCommands.TryDequeue(out var pending))
        {
            if (string.Equals(pending.RunId, runId, StringComparison.OrdinalIgnoreCase))
                removed = true;
            else
                kept.Add(pending);
        }

        foreach (var keep in kept)
            pendingCommands.Enqueue(keep);

        return removed;
    }

    public DadWorkerExecutionStatus GetStatus()
    {
        lock (stateLock)
            return status.Clone();
    }

    public void Update()
    {
        lock (stateLock)
        {
            if (activeCommand == null && pendingCommands.TryDequeue(out var command))
                Start(command);

            if (activeCommand == null || status.IsTerminal)
                return;

            var timeout = TimeSpan.FromSeconds(Math.Clamp(activeCommand.TimeoutSeconds, 30, 7200));
            if (DateTime.UtcNow - startedAtUtc >= timeout)
            {
                if (activeCommand.Role == DadWorkerExecutionRole.QueueLeader ||
                    status.ModuleId == DadModuleId.Mogtome)
                    queueExecutionService.CancelActiveExecutor("Worker execution timeout.");
                Finish(DadWorkerExecutionState.TimedOut, false, "Worker execution timed out.", "Worker execution timeout.");
                return;
            }

            if (activeCommand.Role == DadWorkerExecutionRole.QueueLeader ||
                status.ModuleId == DadModuleId.Mogtome)
                UpdateQueueLeader();
            else
                UpdateParticipant();
        }
    }

    private void Start(DadWorkerExecutionCommand command)
    {
        activeCommand = command;
        startedAtUtc = DateTime.UtcNow;
        enteredDuty = condition[ConditionFlag.BoundByDuty];
        participantQueueContent = null;
        lastParticipantQueueTransition = string.Empty;
        participantCombatRotationMode = combatRotationService.CombatRotationMode;
        participantFrenRiderHandoffGate.Reset();
        var module = ResolveModule(command);
        if (module == null)
        {
            Finish(DadWorkerExecutionState.Failed, false, "Worker assignment has no module.", "Missing module.");
            return;
        }

        status = new DadWorkerExecutionStatus
        {
            CommandId = command.CommandId,
            RunId = command.RunId,
            WorkerSessionId = presenceService.WorkerSessionId,
            Role = command.Role,
            State = DadWorkerExecutionState.Starting,
            ModuleId = module.ModuleId,
            EnteredDuty = enteredDuty,
            UpdatedAtUtc = DateTime.UtcNow,
            Summary = $"Starting {command.Role} work for {module.DisplayName}.",
        };

        if (!DadWorkerCommandValidationRules.TryValidate(
                command,
                presenceService.BuildSnapshotCopy(),
                out _,
                out var validationBlocker))
        {
            Finish(
                DadWorkerExecutionState.Failed,
                false,
                $"Worker assignment became invalid before execution: {validationBlocker}",
                validationBlocker);
            return;
        }

        if (command.Role == DadWorkerExecutionRole.Participant)
        {
            if (module.ModuleId == DadModuleId.Mogtome)
            {
                queueExecutionService.SetWorkerRole(command.Role);
                ApplyLeaderResult(queueExecutionService.ExecuteModule(command.Plan, module, command.Participants));
                return;
            }

            if (DadParticipantQueueFollowThroughRules.IsObserveAcceptOnlyLane(command.Plan, module))
            {
                if (!queueExecutionService.TryResolveParticipantQueueContent(
                        command.Plan,
                        module,
                        out participantQueueContent,
                        out var participantQueueBlocker))
                {
                    Finish(
                        DadWorkerExecutionState.Failed,
                        false,
                        $"Participant queue follow-through could not resolve {module.DisplayName}: {participantQueueBlocker}",
                        participantQueueBlocker);
                    return;
                }

                // A participant may already be bound by an unrelated duty when the command arrives.
                // Running is granted only after the observe-only pulse proves the requested duty.
                enteredDuty = false;
                status.EnteredDuty = false;
            }

            status.State = enteredDuty ? DadWorkerExecutionState.Running : DadWorkerExecutionState.WaitingForQueue;
            status.Summary = enteredDuty
                ? $"Participant entered {module.DisplayName}."
                : $"Participant waiting for queue leader to start {module.DisplayName}.";
            return;
        }

        queueExecutionService.SetWorkerRole(command.Role);
        var result = queueExecutionService.ExecuteModule(command.Plan, module, command.Participants);
        ApplyLeaderResult(result);
    }

    private void UpdateQueueLeader()
    {
        var executor = queueExecutionService.GetActiveExecutorStatus();
        if (!executor.IsActive)
            return;

        if (!TryValidateActiveMutationBoundary())
            return;

        ApplyLeaderResult(queueExecutionService.UpdateActiveExecutor());
    }

    private void ApplyLeaderResult(DadRunStepResultDto result)
    {
        status.StepResult = result.Clone();
        status.UpdatedAtUtc = DateTime.UtcNow;
        status.Summary = result.Summary;
        status.FailureReason = result.FailureReason;
        status.EnteredDuty |= result.ExecutorStatus.Phase == DadRunPhase.InDutyOrTask;

        if (!result.Success)
        {
            Finish(
                result.TimedOut ? DadWorkerExecutionState.TimedOut : DadWorkerExecutionState.Failed,
                false,
                result.Summary,
                string.IsNullOrWhiteSpace(result.FailureReason) ? result.BlockedReason : result.FailureReason);
            return;
        }

        if (!result.ExecutorStatus.IsActive && result.ExecutorStatus.Status == DadRunStatus.Completed)
        {
            Finish(DadWorkerExecutionState.Completed, true, result.Summary, string.Empty);
            return;
        }

        status.State = result.ExecutorStatus.Phase == DadRunPhase.InDutyOrTask
            ? DadWorkerExecutionState.Running
            : DadWorkerExecutionState.WaitingForQueue;
    }

    private void UpdateParticipant()
    {
        if (activeCommand != null && participantQueueContent != null)
        {
            if (!TryValidateActiveMutationBoundary())
                return;

            if (enteredDuty && !condition[ConditionFlag.BoundByDuty])
            {
                CompleteParticipant();
                return;
            }

            var pulse = queueExecutionService.ObserveParticipantQueue(activeCommand.RunId, participantQueueContent);
            LogParticipantQueueTransition(pulse);
            if (!pulse.Success)
            {
                Finish(
                    pulse.Status == DadRunStatus.TimedOut
                        ? DadWorkerExecutionState.TimedOut
                        : DadWorkerExecutionState.Failed,
                    false,
                    pulse.Summary,
                    string.IsNullOrWhiteSpace(pulse.FailureReason) ? pulse.BlockedReason : pulse.FailureReason);
                return;
            }

            var exactRequestedDutyEntered =
                pulse.Kind == DadLocalDutyQueuePulseKind.EnteredDuty &&
                pulse.Phase == DadRunPhase.InDutyOrTask;
            if (exactRequestedDutyEntered)
            {
                enteredDuty = true;
                status.EnteredDuty = true;
                status.State = DadWorkerExecutionState.Running;
            }
            else
            {
                status.State = DadWorkerExecutionState.WaitingForQueue;
            }

            var handoffStatus = participantFrenRiderHandoffGate.Apply(
                activeCommand,
                participantCombatRotationMode == DadCombatRotationMode.UseFrenRider,
                exactRequestedDutyEntered,
                DateTime.UtcNow,
                combatRotationService.TryConfigureAndEnableParticipant,
                out var handoffSummary);
            if (handoffStatus == DadParticipantFrenRiderHandoffStatus.Failed)
            {
                Finish(DadWorkerExecutionState.Failed, false, handoffSummary, handoffSummary);
                return;
            }

            status.Summary = handoffStatus is
                DadParticipantFrenRiderHandoffStatus.Configured or
                DadParticipantFrenRiderHandoffStatus.AlreadyConfigured or
                DadParticipantFrenRiderHandoffStatus.PendingRetry
                ? handoffSummary
                : pulse.Summary;
            status.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        var inDuty = condition[ConditionFlag.BoundByDuty];
        if (inDuty)
        {
            enteredDuty = true;
            status.EnteredDuty = true;
            status.State = DadWorkerExecutionState.Running;
            status.Summary = $"Participant running {status.ModuleId}.";
            status.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        if (enteredDuty)
        {
            CompleteParticipant();
            return;
        }

        status.State = DadWorkerExecutionState.WaitingForQueue;
        status.UpdatedAtUtc = DateTime.UtcNow;
    }

    private bool TryValidateActiveMutationBoundary()
    {
        if (activeCommand == null)
            return false;

        var localRuntime = presenceService.BuildSnapshotCopy();
        var queueOrDutyCommitted = enteredDuty ||
                                   condition[ConditionFlag.InDutyQueue] ||
                                   condition[ConditionFlag.WaitingForDuty] ||
                                   condition[ConditionFlag.WaitingForDutyFinder] ||
                                   condition[ConditionFlag.BoundByDuty] ||
                                   condition[ConditionFlag.BoundByDuty56];
        var valid = queueOrDutyCommitted
            ? DadWorkerCommandValidationRules.TryValidateMutationIdentity(
                activeCommand,
                localRuntime,
                out _,
                out var blocker)
            : DadWorkerCommandValidationRules.TryValidate(
                activeCommand,
                localRuntime,
                out _,
                out blocker);
        if (valid)
            return true;

        if (activeCommand.Role == DadWorkerExecutionRole.QueueLeader || status.ModuleId == DadModuleId.Mogtome)
            queueExecutionService.CancelActiveExecutor("Frozen worker mutation authority changed.");

        Finish(
            DadWorkerExecutionState.Failed,
            false,
            $"Worker mutation stopped because its frozen assignment changed: {blocker}",
            blocker);
        return false;
    }

    private void CompleteParticipant()
    {
        var step = new DadRunStepResultDto
        {
            RunId = status.RunId,
            ModuleId = status.ModuleId,
            StepName = status.ModuleId.ToString(),
            ParticipantState = DadParticipantState.Completed,
            Success = true,
            Summary = $"Participant completed {status.ModuleId} and exited duty.",
            ExecutorStatus = new DadModuleExecutionStatusDto
            {
                RunId = status.RunId,
                ModuleId = status.ModuleId,
                DisplayName = status.ModuleId.ToString(),
                Phase = DadRunPhase.Finalizing,
                Status = DadRunStatus.Completed,
                CanStart = true,
                CompletedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Summary = $"Participant completed {status.ModuleId} and exited duty.",
            },
        };
        status.StepResult = step;
        Finish(DadWorkerExecutionState.Completed, true, step.Summary, string.Empty);
    }

    private void LogParticipantQueueTransition(DadLocalDutyQueuePulse pulse)
    {
        if (activeCommand == null)
            return;

        var local = activeCommand.Participants.SingleOrDefault(static participant => participant.IsLocalClient);
        if (local == null)
            return;

        var transition = $"{pulse.Kind}|{pulse.Phase}|{pulse.Summary}";
        if (string.Equals(lastParticipantQueueTransition, transition, StringComparison.Ordinal))
            return;

        lastParticipantQueueTransition = transition;
        log.Information(
            "[dad] Participant queue transition request={RequestId} module={ModuleId} slot={SlotId} account={AccountKey} character={CharacterKey} contentId={ContentId} worker={WorkerSessionId} pulse={PulseKind} phase={Phase} summary={Summary}.",
            activeCommand.RunId,
            status.ModuleId,
            local.AssignedSlotId,
            local.ManagedAccountKey,
            local.ActiveCharacterKey,
            local.Character.ContentId,
            local.WorkerSessionId,
            pulse.Kind,
            pulse.Phase,
            pulse.Summary);
    }

    private void Finish(DadWorkerExecutionState state, bool success, string summary, string failureReason)
    {
        if (activeCommand?.Role == DadWorkerExecutionRole.Participant && participantQueueContent != null)
            queueExecutionService.ResetParticipantQueueObserver(activeCommand.RunId);
        participantQueueContent = null;
        lastParticipantQueueTransition = string.Empty;
        participantFrenRiderHandoffGate.Reset();
        status.State = state;
        status.IsTerminal = true;
        status.Success = success;
        status.Summary = summary;
        status.FailureReason = failureReason;
        status.UpdatedAtUtc = DateTime.UtcNow;
    }

    private DadWorkerExecutionAck BuildAck(bool accepted, DadWorkerExecutionCommand command, string summary)
        => new()
        {
            CommandId = command.CommandId,
            RunId = command.RunId,
            WorkerSessionId = presenceService.WorkerSessionId,
            Accepted = accepted,
            Summary = summary,
            Status = status.Clone(),
        };

    private static DadPlannedModuleExecution? ResolveModule(DadWorkerExecutionCommand command)
        => command.ModuleIndex >= 0 && command.ModuleIndex < command.Plan.Modules.Count
            ? command.Plan.Modules[command.ModuleIndex]
            : null;
}
