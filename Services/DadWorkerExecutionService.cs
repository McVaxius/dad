using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadWorkerExecutionService
{
    private readonly DadQueueExecutionService queueExecutionService;
    private readonly DadPresenceService presenceService;
    private readonly DadCombatRotationService combatRotationService;
    private readonly DadDutySupportAdsService adsService;
    private readonly DadShoppingRuntimeService shoppingService;
    private readonly DadPreDutyRepairRuntimeService preDutyRepairService;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly DadWorkerRunCommandQueue pendingCommands = new();
    private readonly object stateLock = new();
    private readonly DadParticipantFrenRiderHandoffGate participantFrenRiderHandoffGate = new();
    private readonly DadImmutableCommandRegistry immutableCommandRegistry = new();
    private readonly DadStableContradictionTracker mutationContradictionTracker = new();
    private readonly Dictionary<string, DadWorkerExecutionStatus> commandStatuses = new(StringComparer.Ordinal);
    private readonly HashSet<string> dependencyApprovedRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly DadRunCancellationLedger cancelledRuns = new(StringComparer.OrdinalIgnoreCase);

    private DadWorkerExecutionCommand? activeCommand;
    private DadWorkerExecutionStatus status = new();
    private DateTime startedAtUtc = DateTime.MinValue;
    private bool enteredDuty;
    private DadLocalDutyResolvedContent? participantQueueContent;
    private string lastParticipantQueueTransition = string.Empty;
    private DadCombatRotationMode participantCombatRotationMode = DadCombatRotationMode.UseFrenRider;
    private bool prequeuePrepared;
    private bool repairPreparationStarted;
    private bool passiveLootGoblinPending;
    private bool cancellationPending;
    private DadWorkerExecutionState pendingCancellationState;
    private string pendingCancellationSummary = string.Empty;
    private string pendingCancellationFailureReason = string.Empty;

    public DadWorkerExecutionService(
        DadQueueExecutionService queueExecutionService,
        DadPresenceService presenceService,
        DadCombatRotationService combatRotationService,
        DadDutySupportAdsService adsService,
        DadShoppingRuntimeService shoppingService,
        DadPreDutyRepairRuntimeService preDutyRepairService,
        ICondition condition,
        IPluginLog log)
    {
        this.queueExecutionService = queueExecutionService;
        this.presenceService = presenceService;
        this.combatRotationService = combatRotationService;
        this.adsService = adsService;
        this.shoppingService = shoppingService;
        this.preDutyRepairService = preDutyRepairService;
        this.condition = condition;
        this.log = log;
        status.WorkerSessionId = presenceService.WorkerSessionId;
    }

    public DadWorkerExecutionAck Accept(DadWorkerExecutionCommand command)
    {
        lock (stateLock)
        {
            if (command == null)
                return BuildRejectedAck("Worker assignment payload is missing.");

            var hasRecordedStatus = commandStatuses.TryGetValue(
                command.CommandId,
                out var recordedStatus);
            string validationBlocker;
            var commandValid = hasRecordedStatus
                ? DadWorkerRecordedCommandValidationRules.TryValidate(
                    command,
                    out validationBlocker)
                : DadWorkerCommandValidationRules.TryValidate(
                    command,
                    presenceService.BuildLiveSafetySnapshot(),
                    out _,
                    out validationBlocker);
            if (!commandValid)
            {
                return BuildAck(false, command, $"Worker assignment rejected: {validationBlocker}");
            }

            if (!hasRecordedStatus &&
                !DadDependencyMutationBoundaryRules.CanCross(
                    dependencyApprovedRuns.Contains(command.RunId),
                    [presenceService.BuildSnapshotCopy().Dependencies.IsReady]))
                return BuildAck(false, command, DadDependencyRules.DependencyBlocker);

            var payload = DadIpcJson.Serialize(command);
            var registration = immutableCommandRegistry.Register(
                command.CommandId,
                payload,
                payload,
                $"{command.Plan.Request.RequestedBy}:{command.RunId}/{command.ModuleIndex}->{presenceService.WorkerSessionId.Value}");
            if (registration.Disposition == DadImmutableCommandDisposition.Collision)
            {
                log.Error(
                    "[dad] Immutable worker command collision command={CommandId} originalProducerRoute={OriginalProducerRoute} incomingProducerRoute={IncomingProducerRoute} originalPayload={OriginalPayload} incomingPayload={IncomingPayload}.",
                    registration.CommandId,
                    registration.OriginalProducerRoute,
                    registration.IncomingProducerRoute,
                    registration.OriginalPayload,
                    registration.IncomingPayload);
                return BuildAck(false, command, $"Immutable worker command collision for {command.CommandId}.");
            }

            if (!cancelledRuns.CanAccept(command.RunId))
                return BuildAck(false, command, $"Cancelled run {command.RunId} cannot accept later worker mutation.");

            if (registration.Disposition == DadImmutableCommandDisposition.Duplicate &&
                recordedStatus != null)
            {
                return BuildAck(
                    recordedStatus.State != DadWorkerExecutionState.Cancelled,
                    command,
                    $"Worker assignment already recorded as {recordedStatus.State}.",
                    recordedStatus);
            }

            if (activeCommand != null && status.IsTerminal)
                activeCommand = null;

            pendingCommands.ReleaseOwnershipIfIdle(activeCommand != null);

            if (activeCommand != null &&
                string.Equals(activeCommand.CommandId, command.CommandId, StringComparison.OrdinalIgnoreCase))
            {
                return BuildAck(true, command, "Worker assignment already accepted.");
            }

            var frozenCommand = DadIpcJson.Deserialize<DadWorkerExecutionCommand>(payload) ?? command;
            var admission = pendingCommands.Enqueue(frozenCommand, out var ownershipBlocker);
            if (admission == DadWorkerRunQueueAdmission.Rejected)
                return BuildAck(false, command, ownershipBlocker);
            if (admission == DadWorkerRunQueueAdmission.Duplicate)
                return BuildAck(true, command, "Worker assignment is already pending execution.");

            dependencyApprovedRuns.Add(command.RunId);
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
            commandStatuses[command.CommandId] = status.Clone();
            return BuildAck(true, command, status.Summary);
        }
    }

    public DadWorkerExecutionAck Cancel(DadWorkerExecutionCancel cancel)
    {
        lock (stateLock)
        {
            cancelledRuns.Record(cancel.RunId);
            // Active command matches → cancel the running execution.
            if (activeCommand != null &&
                string.Equals(activeCommand.RunId, cancel.RunId, StringComparison.OrdinalIgnoreCase))
            {
                if (cancellationPending)
                {
                    return new DadWorkerExecutionAck
                    {
                        CommandId = activeCommand.CommandId,
                        RunId = activeCommand.RunId,
                        WorkerSessionId = presenceService.WorkerSessionId,
                        Accepted = false,
                        Summary = status.Summary,
                        Status = status.Clone(),
                    };
                }

                shoppingService.CancelActive(cancel.Reason);
                var cancelledCommandId = activeCommand.CommandId;
                var cancelledRunId = activeCommand.RunId;
                DadRunStepResultDto? executorCancellation = null;
                if (activeCommand.Role == DadWorkerExecutionRole.QueueLeader ||
                    status.ModuleId == DadModuleId.Mogtome)
                    executorCancellation = queueExecutionService.CancelActiveExecutor(cancel.Reason);
                else if (participantQueueContent != null)
                {
                    queueExecutionService.ResetParticipantQueueObserver(activeCommand.RunId);
                    participantQueueContent = null;
                    lastParticipantQueueTransition = string.Empty;
                }

                var lootGoblinCancellationAcknowledged = status.ModuleId != DadModuleId.LootGoblin ||
                    activeCommand.Role != DadWorkerExecutionRole.QueueLeader ||
                    IsAcknowledgedLootGoblinCancellation(executorCancellation, cancelledRunId);
                var finalState = lootGoblinCancellationAcknowledged
                    ? DadWorkerExecutionState.Cancelled
                    : DadWorkerExecutionState.Failed;
                var finalSummary = cancel.Reason;
                if (!lootGoblinCancellationAcknowledged)
                {
                    finalSummary = executorCancellation == null || string.IsNullOrWhiteSpace(executorCancellation.FailureReason)
                        ? "LootGoblin did not acknowledge exact terminal cancellation."
                        : executorCancellation.FailureReason;
                    if (executorCancellation != null)
                        status.StepResult = executorCancellation.Clone();
                }

                if (shoppingService.IsCancellationPending)
                {
                    HoldForShoppingCancellation(finalState, finalSummary, finalSummary);
                    DrainPendingForRun(cancel.RunId);
                    return new DadWorkerExecutionAck
                    {
                        CommandId = cancelledCommandId,
                        RunId = cancelledRunId,
                        WorkerSessionId = presenceService.WorkerSessionId,
                        Accepted = false,
                        Summary = status.Summary,
                        Status = status.Clone(),
                    };
                }

                Finish(finalState, false, finalSummary, finalSummary);
                DrainPendingForRun(cancel.RunId);
                pendingCommands.ReleaseOwnershipIfIdle(activeCommand != null);
                return new DadWorkerExecutionAck
                {
                    CommandId = cancelledCommandId,
                    RunId = cancelledRunId,
                    WorkerSessionId = presenceService.WorkerSessionId,
                    Accepted = lootGoblinCancellationAcknowledged,
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
                    IsTerminal = true,
                    UpdatedAtUtc = DateTime.UtcNow,
                    Summary = "Pending worker assignment cancelled before start.",
                };
                pendingCommands.ReleaseOwnershipIfIdle(activeCommand != null);
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
                Accepted = true,
                Summary = "Worker recorded the cancellation tombstone; no matching run-owned execution remains.",
                Status = status.Clone(),
            };
        }
    }

    public DadWorkerExecutionAck CancelAll(string reason)
    {
        lock (stateLock)
        {
            if (cancellationPending && activeCommand != null)
            {
                pendingCommands.DrainAll();
                cancelledRuns.Record(activeCommand.RunId);
                return new DadWorkerExecutionAck
                {
                    CommandId = activeCommand.CommandId,
                    RunId = activeCommand.RunId,
                    WorkerSessionId = presenceService.WorkerSessionId,
                    Accepted = false,
                    Summary = status.Summary,
                    Status = status.Clone(),
                };
            }

            var hadWork = activeCommand != null || !pendingCommands.IsEmpty || !status.IsTerminal && status.State != DadWorkerExecutionState.Idle;
            pendingCommands.DrainAll();

            var runId = activeCommand?.RunId ?? status.RunId;
            var commandId = activeCommand?.CommandId ?? status.CommandId;
            var activeLootGoblinLeader = activeCommand?.Role == DadWorkerExecutionRole.QueueLeader &&
                                         status.ModuleId == DadModuleId.LootGoblin;
            shoppingService.CancelActive(reason);
            var shoppingResults = shoppingService.Results.Select(static result => result.Clone()).ToList();
            if (activeCommand?.Role == DadWorkerExecutionRole.Participant && participantQueueContent != null)
                queueExecutionService.ResetParticipantQueueObserver(activeCommand.RunId);
            var executorCancellation = queueExecutionService.CancelAll(reason);
            var lootGoblinCancellationAcknowledged = !activeLootGoblinLeader ||
                IsAcknowledgedLootGoblinCancellation(executorCancellation, runId);
            if (shoppingService.IsCancellationPending && activeCommand != null)
            {
                if (activeLootGoblinLeader && !lootGoblinCancellationAcknowledged)
                    status.StepResult = executorCancellation.Clone();
                participantQueueContent = null;
                lastParticipantQueueTransition = string.Empty;
                cancelledRuns.Record(runId);
                var pendingSummary = lootGoblinCancellationAcknowledged
                    ? reason
                    : string.IsNullOrWhiteSpace(executorCancellation.FailureReason)
                        ? "LootGoblin did not acknowledge exact terminal cancellation."
                        : executorCancellation.FailureReason;
                HoldForShoppingCancellation(
                    lootGoblinCancellationAcknowledged
                        ? DadWorkerExecutionState.Cancelled
                        : DadWorkerExecutionState.Failed,
                    pendingSummary,
                    pendingSummary);
                return new DadWorkerExecutionAck
                {
                    CommandId = commandId,
                    RunId = runId,
                    WorkerSessionId = presenceService.WorkerSessionId,
                    Accepted = false,
                    Summary = status.Summary,
                    Status = status.Clone(),
                };
            }

            activeCommand = null;
            if (!string.IsNullOrWhiteSpace(runId))
                cancelledRuns.Record(runId);
            participantQueueContent = null;
            lastParticipantQueueTransition = string.Empty;
            participantFrenRiderHandoffGate.Reset();
            status = new DadWorkerExecutionStatus
            {
                CommandId = commandId,
                RunId = runId,
                WorkerSessionId = presenceService.WorkerSessionId,
                State = hadWork
                    ? lootGoblinCancellationAcknowledged
                        ? DadWorkerExecutionState.Cancelled
                        : DadWorkerExecutionState.Failed
                    : DadWorkerExecutionState.Idle,
                IsTerminal = hadWork,
                UpdatedAtUtc = DateTime.UtcNow,
                Summary = hadWork
                    ? lootGoblinCancellationAcknowledged
                        ? reason
                        : string.IsNullOrWhiteSpace(executorCancellation.FailureReason)
                            ? "LootGoblin did not acknowledge exact terminal cancellation."
                            : executorCancellation.FailureReason
                    : "No local DAD worker execution was active.",
                FailureReason = hadWork && !lootGoblinCancellationAcknowledged
                    ? string.IsNullOrWhiteSpace(executorCancellation.FailureReason)
                        ? "LootGoblin did not acknowledge exact terminal cancellation."
                        : executorCancellation.FailureReason
                    : hadWork ? reason : string.Empty,
                StepResult = activeLootGoblinLeader ? executorCancellation.Clone() : new DadRunStepResultDto(),
                ShoppingResults = shoppingResults,
            };
            shoppingService.Reset();
            ClearPendingCancellation();
            return new DadWorkerExecutionAck
            {
                CommandId = commandId,
                RunId = runId,
                WorkerSessionId = presenceService.WorkerSessionId,
                Accepted = lootGoblinCancellationAcknowledged,
                Summary = status.Summary,
                Status = status.Clone(),
            };
        }
    }

    // Review H3: remove all pending (accepted-but-not-started) commands for a run id.
    // Caller must hold stateLock. ConcurrentQueue has no arbitrary removal, so re-enqueue the keepers.
    private bool DrainPendingForRun(string runId)
    {
        var removed = pendingCommands.DrainRun(runId);
        foreach (var pending in removed)
        {
            commandStatuses[pending.CommandId] = new DadWorkerExecutionStatus
            {
                CommandId = pending.CommandId,
                RunId = pending.RunId,
                WorkerSessionId = presenceService.WorkerSessionId,
                Role = pending.Role,
                State = DadWorkerExecutionState.Cancelled,
                IsTerminal = true,
                UpdatedAtUtc = DateTime.UtcNow,
                Summary = "Pending worker assignment cancelled before start.",
            };
        }

        return removed.Count > 0;
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
            {
                Start(command);
            }

            if (activeCommand == null || status.IsTerminal)
                return;

            if (cancellationPending)
            {
                UpdatePendingShoppingCancellation();
                return;
            }

            if (DadWorkerTimeoutRules.HasTimedOut(
                    activeCommand.TimeoutSeconds,
                    status.ModuleId,
                    status.EnteredDuty,
                    DateTime.UtcNow - startedAtUtc))
            {
                shoppingService.CancelActive("Worker execution timeout.");
                if (activeCommand.Role == DadWorkerExecutionRole.QueueLeader ||
                    status.ModuleId == DadModuleId.Mogtome)
                    queueExecutionService.CancelActiveExecutor("Worker execution timeout.");
                if (shoppingService.IsCancellationPending)
                {
                    HoldForShoppingCancellation(
                        DadWorkerExecutionState.TimedOut,
                        "Worker execution timed out.",
                        "Worker execution timeout.");
                    return;
                }
                Finish(DadWorkerExecutionState.TimedOut, false, "Worker execution timed out.", "Worker execution timeout.");
                return;
            }

            if (!prequeuePrepared)
            {
                UpdatePrequeuePreparation();
                return;
            }

            if (activeCommand.Role == DadWorkerExecutionRole.QueueLeader ||
                status.ModuleId == DadModuleId.Mogtome)
                UpdateQueueLeader();
            else
                UpdateParticipant();
        }
    }

    private void HoldForShoppingCancellation(
        DadWorkerExecutionState finalState,
        string finalSummary,
        string finalFailureReason)
    {
        cancellationPending = true;
        pendingCancellationState = finalState;
        pendingCancellationSummary = finalSummary;
        pendingCancellationFailureReason = finalFailureReason;
        status.IsTerminal = false;
        status.Success = false;
        status.Summary = "Waiting for exact ADS shopping cancellation to reach correlated terminal status.";
        status.FailureReason = string.Empty;
        status.ShoppingResults = shoppingService.Results.Select(static result => result.Clone()).ToList();
        status.UpdatedAtUtc = DateTime.UtcNow;
        if (activeCommand != null)
            commandStatuses[activeCommand.CommandId] = status.Clone();
    }

    private void UpdatePendingShoppingCancellation()
    {
        var decision = shoppingService.Update(DateTime.UtcNow);
        status.ShoppingResults = shoppingService.Results.Select(static result => result.Clone()).ToList();
        status.UpdatedAtUtc = DateTime.UtcNow;
        if (shoppingService.IsCancellationPending)
        {
            status.Summary = decision.Summary;
            if (activeCommand != null)
                commandStatuses[activeCommand.CommandId] = status.Clone();
            return;
        }

        var completedCommand = activeCommand;
        var finalState = pendingCancellationState;
        var finalSummary = pendingCancellationSummary;
        var finalFailureReason = pendingCancellationFailureReason;
        Finish(finalState, false, finalSummary, finalFailureReason);
        if (completedCommand != null)
            DrainPendingForRun(completedCommand.RunId);
        pendingCommands.ReleaseOwnershipIfIdle(activeCommand != null);
    }

    private void ClearPendingCancellation()
    {
        cancellationPending = false;
        pendingCancellationState = DadWorkerExecutionState.Idle;
        pendingCancellationSummary = string.Empty;
        pendingCancellationFailureReason = string.Empty;
    }

    private void Start(DadWorkerExecutionCommand command)
    {
        ClearPendingCancellation();
        activeCommand = command;
        startedAtUtc = DateTime.UtcNow;
        enteredDuty = condition[ConditionFlag.BoundByDuty];
        participantQueueContent = null;
        lastParticipantQueueTransition = string.Empty;
        participantCombatRotationMode = combatRotationService.CombatRotationMode;
        participantFrenRiderHandoffGate.Reset();
        preDutyRepairService.Reset();
        shoppingService.Reset();
        prequeuePrepared = false;
        repairPreparationStarted = false;
        passiveLootGoblinPending = false;
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

        var liveRuntime = presenceService.BuildLiveSafetySnapshot();
        if (!DadWorkerCommandValidationRules.TryValidate(
                command,
                liveRuntime,
                out var localAssignment,
                out var validationBlocker))
        {
            status.State = DadWorkerExecutionState.Accepted;
            status.Summary = $"Worker assignment is waiting for fresh safe runtime truth before execution: {validationBlocker}";
            status.UpdatedAtUtc = DateTime.UtcNow;
            commandStatuses[command.CommandId] = status.Clone();
            shoppingService.Reset();
            activeCommand = null;
            pendingCommands.Enqueue(command, out _);
            return;
        }

        shoppingService.Begin(command.Plan.Request, command.ModuleIndex, localAssignment, DateTime.UtcNow);
        passiveLootGoblinPending = module.ModuleId == DadModuleId.LootGoblin &&
                                   command.Role == DadWorkerExecutionRole.Participant;
        if (passiveLootGoblinPending && !shoppingService.IsRequired)
        {
            CompletePassiveLootGoblinParticipant(command, module, localAssignment);
            return;
        }

        preDutyRepairService.Begin(command.Plan.Request, module.ModuleId, DateTime.UtcNow);
        repairPreparationStarted = true;
        UpdatePrequeuePreparation();
    }

    private void UpdatePrequeuePreparation()
    {
        if (activeCommand == null || prequeuePrepared || status.IsTerminal)
            return;

        var module = ResolveModule(activeCommand);
        if (module == null)
        {
            Finish(DadWorkerExecutionState.Failed, false, "Worker assignment has no module.", "Missing module.");
            return;
        }

        var liveRuntime = presenceService.BuildLiveSafetySnapshot();
        if (!DadWorkerCommandValidationRules.TryValidate(
                activeCommand,
                liveRuntime,
                out var localAssignment,
                out var validationBlocker))
        {
            status.State = DadWorkerExecutionState.Preparing;
            status.Summary = $"Worker preparation is waiting for fresh exact prequeue safety proof: {validationBlocker}";
            status.UpdatedAtUtc = DateTime.UtcNow;
            commandStatuses[activeCommand.CommandId] = status.Clone();
            return;
        }

        if (!liveRuntime.WorldReadyStable)
        {
            status.State = DadWorkerExecutionState.Preparing;
            status.Summary = "Worker preparation is waiting for normal world-stable prequeue safety proof before durability or ADS inspection.";
            status.UpdatedAtUtc = DateTime.UtcNow;
            commandStatuses[activeCommand.CommandId] = status.Clone();
            return;
        }

        if (!repairPreparationStarted)
        {
            preDutyRepairService.Begin(activeCommand.Plan.Request, module.ModuleId, DateTime.UtcNow);
            repairPreparationStarted = true;
        }

        var repairDecision = preDutyRepairService.Update(DateTime.UtcNow);
        if (repairDecision.Action == DadPreDutyRepairAction.Reject)
        {
            var assignment = localAssignment.WorkerSessionId.IsEmpty ? liveRuntime : localAssignment;
            var attributed = DadWorkerPrequeueBarrierRules.AttributeFailure(assignment, repairDecision.Summary);
            Finish(DadWorkerExecutionState.Failed, false, attributed, attributed);
            return;
        }

        if (repairDecision.Action != DadPreDutyRepairAction.Ready)
        {
            status.State = preDutyRepairService.IsRequired
                ? DadWorkerExecutionState.Repairing
                : DadWorkerExecutionState.Preparing;
            status.Summary = repairDecision.Summary;
            status.UpdatedAtUtc = DateTime.UtcNow;
            commandStatuses[activeCommand.CommandId] = status.Clone();
            return;
        }

        if (shoppingService.IsRequired)
        {
            var shoppingDecision = shoppingService.Update(DateTime.UtcNow);
            status.ShoppingResults = shoppingService.Results.Select(static result => result.Clone()).ToList();
            if (shoppingDecision.Action == DadShoppingRuntimeAction.Reject)
            {
                var assignment = localAssignment.WorkerSessionId.IsEmpty ? liveRuntime : localAssignment;
                var attributed = DadWorkerPrequeueBarrierRules.AttributeFailure(assignment, shoppingDecision.Summary);
                Finish(DadWorkerExecutionState.Failed, false, attributed, attributed);
                return;
            }

            if (shoppingDecision.Action != DadShoppingRuntimeAction.Ready)
            {
                status.State = DadWorkerExecutionState.Shopping;
                status.Summary = shoppingDecision.Summary;
                status.UpdatedAtUtc = DateTime.UtcNow;
                commandStatuses[activeCommand.CommandId] = status.Clone();
                return;
            }

            if (passiveLootGoblinPending)
            {
                CompletePassiveLootGoblinParticipant(activeCommand, module, localAssignment);
                return;
            }
        }

        if (module.ModuleId == DadModuleId.LootGoblin)
        {
            prequeuePrepared = true;
            BeginQueueWork(activeCommand, module);
            return;
        }

        var adsAssignment = localAssignment.WorkerSessionId.IsEmpty
            ? liveRuntime
            : localAssignment;
        if (!TryResolveLocalAdsLootMode(activeCommand, adsAssignment, liveRuntime, out var adsLootMode, out var adsIdentityBlocker))
        {
            var attributedBlocker = DadWorkerPrequeueBarrierRules.AttributeFailure(
                adsAssignment,
                adsIdentityBlocker);
            Finish(DadWorkerExecutionState.Failed, false, attributedBlocker, attributedBlocker);
            return;
        }
        if (!adsService.TryPatchConfiguration(adsLootMode, out var adsBlocker))
        {
            var attributedBlocker = DadWorkerPrequeueBarrierRules.AttributeFailure(
                adsAssignment,
                $"required ADS configuration failed before queue mutation: {adsBlocker}");
            Finish(
                DadWorkerExecutionState.Failed,
                false,
                attributedBlocker,
                attributedBlocker);
            return;
        }

        log.Information(
            "[dad][ADS] Required configuration patch accepted slot={SlotId} character={CharacterKey} worker={WorkerSessionId} lootMode={LootMode} after repairProof={RepairProof}.",
            adsAssignment.AssignedSlotId,
            adsAssignment.ActiveCharacterKey.Value,
            adsAssignment.WorkerSessionId.Value,
            adsLootMode?.ToString() ?? DadAdsLootMode.NoChange.ToString(),
            repairDecision.Summary);

        prequeuePrepared = true;
        BeginQueueWork(activeCommand, module);
    }

    private void BeginQueueWork(DadWorkerExecutionCommand command, DadPlannedModuleExecution module)
    {
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

        var localRuntime = presenceService.BuildLiveSafetySnapshot();
        var queueOrDutyCommitted = enteredDuty ||
                                   condition[ConditionFlag.InDutyQueue] ||
                                   condition[ConditionFlag.WaitingForDuty] ||
                                   condition[ConditionFlag.WaitingForDutyFinder] ||
                                   condition[ConditionFlag.BoundByDuty] ||
                                   condition[ConditionFlag.BoundByDuty56];
        var valid = status.ModuleId != DadModuleId.LootGoblin && queueOrDutyCommitted
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
        {
            mutationContradictionTracker.Reset();
            return true;
        }

        var expected = activeCommand.Participants.SingleOrDefault(static participant => participant.IsLocalClient);
        var evidence = string.Empty;
        if (expected != null && localRuntime.WorldReadyStable)
        {
            if (string.Equals(localRuntime.WorkerSessionId.Value, expected.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) &&
                !localRuntime.ManagedAccountKey.IsEmpty &&
                !DadRosterIdentity.SameAccount(localRuntime.ManagedAccountKey, expected.ManagedAccountKey))
            {
                evidence = $"Worker {expected.WorkerSessionId} reports stable account {localRuntime.ManagedAccountKey}; expected {expected.ManagedAccountKey}.";
            }
            else if (string.Equals(localRuntime.ActiveCharacterKey.Value, expected.ActiveCharacterKey.Value, StringComparison.OrdinalIgnoreCase) &&
                     localRuntime.Character.ContentId != 0 &&
                     localRuntime.Character.ContentId != expected.Character.ContentId)
            {
                evidence = $"Character {expected.ActiveCharacterKey} reports stable Content ID {localRuntime.Character.ContentId}; expected {expected.Character.ContentId}.";
            }
        }

        var contradiction = mutationContradictionTracker.Observe(
            evidence,
            localRuntime.WorldReadyStable,
            DateTime.UtcNow,
            TimeSpan.FromSeconds(2),
            localRuntime.LastHeartbeatUtc);
        if (contradiction.Disposition != DadSafetyProofDisposition.Reject)
        {
            status.Summary = contradiction.Disposition == DadSafetyProofDisposition.Wait
                ? contradiction.Summary
                : $"Worker mutation is waiting for fresh safe frozen identity proof: {blocker}";
            status.UpdatedAtUtc = DateTime.UtcNow;
            return false;
        }

        if (activeCommand.Role == DadWorkerExecutionRole.QueueLeader || status.ModuleId == DadModuleId.Mogtome)
            queueExecutionService.CancelActiveExecutor("Frozen worker mutation authority changed.");

        Finish(
            DadWorkerExecutionState.Failed,
            false,
            $"Worker mutation stopped after two fresh stable contradictions: {contradiction.Evidence}",
            contradiction.Evidence);
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

    private void CompletePassiveLootGoblinParticipant(
        DadWorkerExecutionCommand command,
        DadPlannedModuleExecution module,
        DadParticipantSnapshot localAssignment)
    {
        var summary = $"{localAssignment.AssignedSlotId} passed exact worker validation and is holding the LootGoblin party passively; frozen Slot1 owns map gather, open, and run IPC.";
        var now = DateTime.UtcNow;
        status.StepResult = new DadRunStepResultDto
        {
            RunId = command.RunId,
            ModuleId = module.ModuleId,
            StepName = "LootGoblin passive party holder",
            ParticipantState = DadParticipantState.Completed,
            Success = true,
            Summary = summary,
            ExecutorStatus = new DadModuleExecutionStatusDto
            {
                RunId = command.RunId,
                ModuleId = module.ModuleId,
                DisplayName = module.DisplayName,
                Phase = DadRunPhase.InDutyOrTask,
                Status = DadRunStatus.Completed,
                StepName = "PassivePartyHolder",
                CanStart = true,
                IsActive = false,
                StartedAtUtc = now,
                UpdatedAtUtc = now,
                CompletedAtUtc = now,
                Summary = summary,
            },
            ReportedAtUtc = now,
        };
        Finish(DadWorkerExecutionState.Completed, true, summary, string.Empty);
    }

    private static bool IsAcknowledgedLootGoblinCancellation(
        DadRunStepResultDto? result,
        string expectedRunId)
        => result != null &&
           string.Equals(result.RunId, expectedRunId, StringComparison.Ordinal) &&
           result.ModuleId == DadModuleId.LootGoblin &&
           result.ParticipantState == DadParticipantState.Cancelled &&
           result.ExecutorStatus.Status == DadRunStatus.Cancelled &&
           !result.ExecutorStatus.IsActive;

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

    private static bool TryResolveLocalAdsLootMode(
        DadWorkerExecutionCommand command,
        DadParticipantSnapshot localAssignment,
        DadParticipantSnapshot liveRuntime,
        out DadAdsLootMode? mode,
        out string blocker)
    {
        if (!liveRuntime.IsAvailable || !liveRuntime.IsEligibleForRun || liveRuntime.State == DadParticipantState.Stale)
        {
            mode = null;
            blocker = "ADS patch identity proof requires a live, available, run-eligible worker snapshot.";
            return false;
        }

        var contentId = localAssignment.Character.ContentId != 0
            ? localAssignment.Character.ContentId
            : liveRuntime.Character.ContentId;
        var characterKey = !localAssignment.ActiveCharacterKey.IsEmpty
            ? localAssignment.ActiveCharacterKey
            : liveRuntime.ActiveCharacterKey;
        var accountKey = !localAssignment.ManagedAccountKey.IsEmpty
            ? localAssignment.ManagedAccountKey
            : liveRuntime.ManagedAccountKey;

        if (contentId == 0 || characterKey.IsEmpty)
        {
            mode = null;
            blocker = "ADS patch identity proof requires a non-zero live Content ID and exact character key.";
            return false;
        }

        var roster = command.Plan.Orchestration.RequiredRosterCharacters ?? [];
        var matches = roster
            .Where(reference =>
                reference.ContentId == contentId &&
                !reference.CharacterKey.IsEmpty &&
                string.Equals(reference.CharacterKey.Value, characterKey.Value, StringComparison.OrdinalIgnoreCase) &&
                (reference.AccountKey.IsEmpty ||
                 (!accountKey.IsEmpty && DadRosterIdentity.SameAccount(reference.AccountKey, accountKey))))
            .ToList();

        if (roster.Count > 0 && matches.Count != 1)
        {
            mode = null;
            blocker = $"ADS patch identity proof expected one frozen roster row for the live worker, found {matches.Count}.";
            return false;
        }

        mode = matches.Count == 1 ? matches[0].AdsLootMode : DadAdsLootMode.NoChange;
        blocker = string.Empty;
        return true;
    }

    private void Finish(DadWorkerExecutionState state, bool success, string summary, string failureReason)
    {
        if (activeCommand?.Role == DadWorkerExecutionRole.Participant && participantQueueContent != null)
            queueExecutionService.ResetParticipantQueueObserver(activeCommand.RunId);
        var completedCommand = activeCommand;
        participantQueueContent = null;
        lastParticipantQueueTransition = string.Empty;
        participantFrenRiderHandoffGate.Reset();
        preDutyRepairService.Reset();
        status.ShoppingResults = shoppingService.Results.Select(static result => result.Clone()).ToList();
        shoppingService.Reset();
        ClearPendingCancellation();
        prequeuePrepared = false;
        repairPreparationStarted = false;
        passiveLootGoblinPending = false;
        status.State = state;
        status.IsTerminal = true;
        status.Success = success;
        status.Summary = summary;
        status.FailureReason = failureReason;
        status.UpdatedAtUtc = DateTime.UtcNow;
        if (completedCommand != null)
            commandStatuses[completedCommand.CommandId] = status.Clone();
        activeCommand = null;
        pendingCommands.ReleaseOwnershipIfIdle(activeCommand != null);
    }

    private DadWorkerExecutionAck BuildAck(
        bool accepted,
        DadWorkerExecutionCommand command,
        string summary,
        DadWorkerExecutionStatus? statusSnapshot = null)
        => new()
        {
            CommandId = command.CommandId,
            RunId = command.RunId,
            WorkerSessionId = presenceService.WorkerSessionId,
            Accepted = accepted,
            Summary = summary,
            Status = (statusSnapshot ?? status).Clone(),
        };

    private DadWorkerExecutionAck BuildRejectedAck(string summary)
        => new()
        {
            WorkerSessionId = presenceService.WorkerSessionId,
            Accepted = false,
            Summary = summary,
            Status = status.Clone(),
        };

    private static DadPlannedModuleExecution? ResolveModule(DadWorkerExecutionCommand command)
        => command.ModuleIndex >= 0 && command.ModuleIndex < command.Plan.Modules.Count
            ? command.Plan.Modules[command.ModuleIndex]
            : null;
}
