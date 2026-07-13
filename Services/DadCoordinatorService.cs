using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadCoordinatorService
{
    private static readonly TimeSpan ParticipantPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WorkerStatusPollInterval = TimeSpan.FromMilliseconds(750);

    private readonly Configuration configuration;
    private readonly ConfigManager configManager;
    private readonly DadCharacterIntelligenceService characterIntelligenceService;
    private readonly DadPresenceService presenceService;
    private readonly DadTransportService transportService;
    private readonly DadClaimService claimService;
    private readonly DadPartyAssemblyService partyAssemblyService;
    private readonly InfoProxyPartyInviteGateway partyInviteGateway;
    private readonly DadQueueExecutionService queueExecutionService;
    private readonly DadWorkerExecutionService workerExecutionService;
    private readonly DadPlannerService plannerService;
    private readonly IPluginLog log;

    private DadRunPlan? activePlan;
    private DadRunSlotManifest? activeSlotManifest;
    private readonly List<DadParticipantSnapshot> activeParticipants = [];
    private readonly List<DadRunStepResultDto> stepResults = [];
    private DadRunStopProgress stopProgress = DadRunStopProgress.FromPolicy(null);
    private DateTime phaseChangedAtUtc = DateTime.MinValue;
    private DateTime nextParticipantPollUtc = DateTime.MinValue;
    private int activeModuleIndex = -1;
    private int activeStepResultIndex = -1;
    private bool loggedSingleWorkerSeed;
    private bool loggedSingleWorkerAssemblyConfirmed;
    private string lastSingleWorkerAssemblyBlocker = string.Empty;
    private readonly Dictionary<string, DadWorkerExecutionStatus> workerStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> slotResolutionTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> assignmentTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> partyTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> workerCommandTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly DadRemoteAssignmentTracker remoteAssignmentTracker = new();
    private DateTime nextWorkerStatusPollUtc = DateTime.MinValue;
    private DadRunPhase? lastLoggedCoordinatorPhase;

    internal DadCoordinatorService(
        Configuration configuration,
        ConfigManager configManager,
        DadCharacterIntelligenceService characterIntelligenceService,
        DadPresenceService presenceService,
        DadTransportService transportService,
        DadClaimService claimService,
        DadPartyAssemblyService partyAssemblyService,
        InfoProxyPartyInviteGateway partyInviteGateway,
        DadQueueExecutionService queueExecutionService,
        DadWorkerExecutionService workerExecutionService,
        DadPlannerService plannerService,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.configManager = configManager;
        this.characterIntelligenceService = characterIntelligenceService;
        this.presenceService = presenceService;
        this.transportService = transportService;
        this.claimService = claimService;
        this.partyAssemblyService = partyAssemblyService;
        this.partyInviteGateway = partyInviteGateway;
        this.queueExecutionService = queueExecutionService;
        this.workerExecutionService = workerExecutionService;
        this.plannerService = plannerService;
        this.log = log;
        RecoverAbandonedRun();
    }

    public DadRunResult CurrentResult { get; private set; } = DadRunResult.Idle();

    public bool IsReady => transportService.IsReady;

    public bool IsBusy => CurrentResult.Status is DadRunStatus.Queued or DadRunStatus.WaitingForParticipants or DadRunStatus.Running;

    public bool IsServerDad => configuration.RunAsServerDad;

    public event Action<DadRunResult>? StatusChanged;

    public void Update()
    {
        claimService.SweepExpiredLeases(DateTime.UtcNow);

        if (!IsBusy || activePlan == null)
            return;

        LogCoordinatorPhaseTransition();

        switch (CurrentResult.Phase)
        {
            case DadRunPhase.DiscoveringParticipants:
            case DadRunPhase.WaitingForReadiness:
                UpdateParticipantDiscovery();
                break;
            case DadRunPhase.ClaimingSlots:
                UpdateClaims();
                break;
            case DadRunPhase.AssemblingParty:
                UpdateAssembly();
                break;
            case DadRunPhase.RoutingModules:
            case DadRunPhase.QueuePreparing:
            case DadRunPhase.QueueStarting:
            case DadRunPhase.WaitingForQueuePop:
            case DadRunPhase.InDutyOrTask:
            case DadRunPhase.PostRunStabilizing:
            case DadRunPhase.RequeueOrComplete:
                UpdateModuleRouting();
                break;
            case DadRunPhase.Finalizing:
                CompleteRun();
                break;
        }
    }

    public DadRunResult GetLocalResult()
        => BuildPublishedResult();

    public DadRunResult GetAuthorityAwareResult()
    {
        var localResult = BuildPublishedResult();
        if (activePlan != null || IsServerDad || !configuration.PluginEnabled || localResult.AuthorityMode == DadAuthorityMode.LocalOnly)
            return localResult;

        var authorityEndpoint = ResolveAuthorityEndpoint();
        if (string.IsNullOrWhiteSpace(authorityEndpoint))
            return localResult;

        var remote = transportService.QueryAuthorityStatus(authorityEndpoint);
        if (remote == null)
        {
            var unavailable = localResult.Clone();
            unavailable.AuthorityEndpoint = authorityEndpoint;
            unavailable.AuthorityWorkerSessionId = transportService.CurrentTransport.AuthorityWorkerSessionId;
            unavailable.Summary = "Dad Coordinator status unavailable.";
            unavailable.BlockedReason = "Dad Coordinator status query failed.";
            return unavailable;
        }

        var authorityResult = remote.Clone();
        if (string.IsNullOrWhiteSpace(authorityResult.AuthorityEndpoint))
            authorityResult.AuthorityEndpoint = authorityEndpoint;
        if (authorityResult.AuthorityWorkerSessionId.IsEmpty)
            authorityResult.AuthorityWorkerSessionId = transportService.CurrentTransport.AuthorityWorkerSessionId;
        if (authorityResult.WorkerRole == DadWorkerRole.None && !authorityResult.AuthorityWorkerSessionId.IsEmpty)
            authorityResult.WorkerRole = DadWorkerRole.ServerDad;
        return authorityResult;
    }

    public DadRunResult StartTasks(DadRunRequest request)
    {
        if (!configuration.PluginEnabled)
            return DadRunResult.Rejected(request, "dad is disabled.");

        var profile = configManager.GetActiveConfig();
        if (!profile.Enabled)
            return DadRunResult.Rejected(request, "dad profile is disabled.");

        if (!profile.AllowIpcStarts)
            return DadRunResult.Rejected(request, "dad profile blocks Dad starts.");

        if (IsBusy)
            return DadRunResult.Rejected(request, "dad already has an active run.");

        ApplyConfigurationDefaults(request);

        if (!IsServerDad && RequiresServerDadAuthority(request))
        {
            var authorityEndpoint = ResolveAuthorityEndpoint(forceRefresh: true);
            if (string.IsNullOrWhiteSpace(authorityEndpoint))
                return DadRunResult.Rejected(request, "No Dad Coordinator hub connection is available.");

            log.Information("[dad] Forwarding run {RequestId} to Dad Coordinator at {Endpoint}: {Payload}",
                request.RequestId,
                authorityEndpoint,
                request.DescribeRequestedWork());
            var forwarded = transportService.SendStartRunCommand(authorityEndpoint, request);
            if (forwarded != null)
            {
                log.Information("[dad] Dad Coordinator responded for forwarded run {RequestId}: {Status}/{Phase}/{Module} {Summary}",
                    forwarded.RequestId,
                    forwarded.Status,
                    forwarded.Phase,
                    forwarded.ModuleId,
                    forwarded.Summary);
                return forwarded;
            }

            log.Warning("[dad] Dad Coordinator did not answer forwarded run {RequestId} for {Payload}", request.RequestId, request.DescribeRequestedWork());
            return DadRunResult.Rejected(request, "Dad Coordinator did not accept forwarded run start.");
        }

        var pool = characterIntelligenceService.RefreshLocalCharacterPool("run-start", logRefresh: false);
        var plan = plannerService.BuildPlan(request, pool, out var rejectionReason);
        if (plan == null)
            return DadRunResult.Rejected(request, rejectionReason);

        DadRunSlotManifest? acceptedManifest = null;
        if (DadRunSlotManifestRules.RequiresFrozenRoster(plan))
        {
            if (!DadRunSlotManifestRules.TryCreate(plan, out var unboundManifest, out rejectionReason))
                return DadRunResult.Rejected(request, rejectionReason);

            var onlineParticipants = BuildOnlineParticipantSet(pool);
            if (!DadRunSlotManifestRules.TryBindWorkerSessions(
                    unboundManifest,
                    onlineParticipants,
                    out acceptedManifest,
                    out rejectionReason))
            {
                return DadRunResult.Rejected(request, rejectionReason);
            }
        }

        activePlan = plan;
        activeSlotManifest = acceptedManifest;
        activeParticipants.Clear();
        stepResults.Clear();
        stopProgress = DadRunStopProgress.FromPolicy(plan.Request.StopPolicy);
        activeModuleIndex = -1;
        activeStepResultIndex = -1;
        loggedSingleWorkerSeed = false;
        loggedSingleWorkerAssemblyConfirmed = false;
        lastSingleWorkerAssemblyBlocker = string.Empty;
        nextParticipantPollUtc = DateTime.MinValue;
        nextWorkerStatusPollUtc = DateTime.MinValue;
        workerStatuses.Clear();
        partyInviteGateway.Reset();
        slotResolutionTransitions.Clear();
        assignmentTransitions.Clear();
        partyTransitions.Clear();
        workerCommandTransitions.Clear();
        remoteAssignmentTracker.BeginAttempt(plan.Request.RequestId);
        lastLoggedCoordinatorPhase = null;
        claimService.ReleaseClaims(plan.Request.RequestId);
        presenceService.MarkLeader(plan.Request.RequestId, plan.Orchestration.AuthorityMode, $"Dad Coordinator planned {plan.Modules.Count} Dad module(s).");
        if (!TryBeginLocalRequestedJobPreparation(plan, acceptedManifest, out var preparationBlocker))
        {
            activePlan = null;
            activeSlotManifest = null;
            remoteAssignmentTracker.Clear();
            presenceService.ResetToIdle();
            return DadRunResult.Rejected(request, preparationBlocker);
        }
        SeedLocalParticipantIfNeeded(plan);
        LogAcceptedSlotManifest(plan, acceptedManifest);

        CurrentResult = DadRunResult.FromPlan(plan, DadRunStatus.Queued, $"Queued Dad orchestration: {plan.Summary}");
        CurrentResult.Role = DadOrchestrationRole.Leader;
        CurrentResult.WorkerRole = IsServerDad ? DadWorkerRole.ServerDad : DadWorkerRole.ClientDad;
        CurrentResult.AuthorityMode = plan.Orchestration.AuthorityMode;
        CurrentResult.CancellationState = DadRunCancellationState.None;
        CurrentResult.LeaderClientInstanceId = presenceService.ClientInstanceId;
        CurrentResult.AuthorityWorkerSessionId = presenceService.WorkerSessionId;
        CurrentResult.AuthorityEndpoint = transportService.CurrentTransport.ListenerEndpoint;
        CurrentResult.LocalClientInstanceId = presenceService.ClientInstanceId;
        CurrentResult.LocalWorkerSessionId = presenceService.WorkerSessionId;
        if (IsStopPolicyLoopEligible(plan))
            CurrentResult.TotalTaskCount = stopProgress.SafetyCap;
        CurrentResult.ActiveTaskStatus = $"Planned {plan.Modules.Count} module(s).";
        CurrentResult.StopProgress = stopProgress.Clone();
        CurrentResult.Participants = activeParticipants.Select(static participant => participant.Clone()).ToList();
        CurrentResult.Leases = [];
        configuration.PersistedActiveRun = CurrentResult.Clone();
        configuration.Save();

        var requiresDiscovery = RequiresParticipantDiscovery(plan, acceptedManifest);
        Transition(requiresDiscovery ? DadRunPhase.DiscoveringParticipants : DadRunPhase.ClaimingSlots,
            requiresDiscovery ? DadRunStatus.WaitingForParticipants : DadRunStatus.Running,
            requiresDiscovery
                ? $"Dad Coordinator waiting for {plan.RequiredParticipantCount} participant(s)."
                : plan.Orchestration.LocalOnlyOverride
                    ? "Local-only Dad orchestration is ready to claim local slot."
                    : "Single-worker Dad orchestration is ready to claim local slot.");

        log.Information("[dad] Planned run {RequestId}: {Summary}", plan.Request.RequestId, plan.Summary);
        return PublishAndClone();
    }

    public DadRunResult CancelActiveRun()
    {
        if (activePlan == null && !IsServerDad)
        {
            var authorityEndpoint = ResolveAuthorityEndpoint(forceRefresh: true);
            if (!string.IsNullOrWhiteSpace(authorityEndpoint))
            {
                log.Information("[dad] Forwarding cancel to Dad Coordinator at {Endpoint}: request {RequestId}",
                    authorityEndpoint,
                    string.IsNullOrWhiteSpace(CurrentResult.RequestId) ? "(none)" : CurrentResult.RequestId);
                var forwarded = transportService.SendCancelCommand(authorityEndpoint, new DadCancelCommandDto
                {
                    RunId = CurrentResult.RequestId,
                    AuthorityWorkerSessionId = transportService.CurrentTransport.AuthorityWorkerSessionId,
                    CancellationState = DadRunCancellationState.Requested,
                    Reason = "Cancelled by Client Dad.",
                });

                if (forwarded != null)
                {
                    log.Information("[dad] Dad Coordinator responded to forwarded cancel {RequestId}: {Status} {Summary}",
                        string.IsNullOrWhiteSpace(forwarded.RequestId) ? "(none)" : forwarded.RequestId,
                        forwarded.Status,
                        forwarded.Summary);
                    return forwarded;
                }

                log.Warning("[dad] Forwarded cancel did not receive a Dad Coordinator response for request {RequestId}",
                    string.IsNullOrWhiteSpace(CurrentResult.RequestId) ? "(none)" : CurrentResult.RequestId);
            }
        }

        if (!IsBusy || activePlan == null)
            return CurrentResult.Clone();

        var command = new DadCancelCommandDto
        {
            RunId = activePlan.Request.RequestId,
            AuthorityWorkerSessionId = presenceService.WorkerSessionId,
            CancellationState = DadRunCancellationState.Cancelling,
            Reason = "Cancelled by operator.",
        };

        CurrentResult.CancellationState = DadRunCancellationState.Cancelling;
        var remoteAcks = transportService.BroadcastCancel(command, activeParticipants);
        var localAck = presenceService.HandleCancelRun(command);
        var executorCancelAck = workerExecutionService.Cancel(new DadWorkerExecutionCancel
        {
            RunId = activePlan.Request.RequestId,
            Reason = "Cancelled by operator.",
        });
        foreach (var participant in activeParticipants.Where(static participant => !participant.IsLocalClient))
        {
            transportService.SendWorkerExecutionCancel(participant, new DadWorkerExecutionCancel
            {
                RunId = activePlan.Request.RequestId,
                Reason = "Cancelled by Dad Coordinator.",
            });
        }
        claimService.ReleaseClaims(activePlan.Request.RequestId);
        if (!string.IsNullOrWhiteSpace(executorCancelAck.Status.StepResult.StepName))
            stepResults.Add(executorCancelAck.Status.StepResult.Clone());

        foreach (var participant in activeParticipants)
        {
            participant.CancellationState = DadRunCancellationState.Acknowledged;
            participant.State = DadParticipantState.Cancelled;
            participant.LeaseState = DadParticipantLeaseState.Released;
            participant.ClaimState = DadClaimState.Released;
        }

        if (localAck.Snapshot != null)
        {
            var local = activeParticipants.FirstOrDefault(static participant => participant.IsLocalClient);
            if (local != null)
                CopyLocalParticipant(local, localAck.Snapshot);
        }

        foreach (var ack in remoteAcks)
        {
            var participant = activeParticipants.FirstOrDefault(candidate =>
                string.Equals(candidate.WorkerSessionId, ack.WorkerSessionId.ToString(), StringComparison.OrdinalIgnoreCase));
            if (participant != null && !TryApplyRemoteParticipantResponse(participant, ack.Snapshot, out var blocker))
                log.Warning("[dad] Ignored remote cancellation snapshot for {WorkerSessionId}: {Blocker}", ack.WorkerSessionId, blocker);
        }

        return FinalizeRun(DadRunStatus.Cancelled, "Dad run cancelled.", "Cancelled by operator.");
    }

    public DadRunResult CancelAllLocal(string reason)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? "Stopped by DAD Stop-all." : reason;
        var runId = activePlan?.Request.RequestId ?? CurrentResult.RequestId;
        workerExecutionService.CancelAll(reason);
        queueExecutionService.CancelAll(reason);
        claimService.ReleaseAllClaims();

        if (activePlan == null || !IsBusy)
        {
            presenceService.ResetToIdle();
            return CurrentResult.Clone();
        }

        var localAck = presenceService.HandleCancelRun(new DadCancelCommandDto
        {
            RunId = runId,
            AuthorityWorkerSessionId = presenceService.WorkerSessionId,
            CancellationState = DadRunCancellationState.Cancelling,
            Reason = reason,
        });
        foreach (var participant in activeParticipants)
        {
            participant.CancellationState = DadRunCancellationState.Acknowledged;
            participant.State = DadParticipantState.Cancelled;
            participant.LeaseState = DadParticipantLeaseState.Released;
            participant.ClaimState = DadClaimState.Released;
        }

        if (localAck.Snapshot != null)
        {
            var local = activeParticipants.FirstOrDefault(static participant => participant.IsLocalClient);
            if (local != null)
                CopyLocalParticipant(local, localAck.Snapshot);
        }

        CurrentResult.CancellationState = DadRunCancellationState.Cancelling;
        return FinalizeRun(DadRunStatus.Cancelled, "Dad run stopped by Stop-all.", reason);
    }

    private void UpdateParticipantDiscovery()
    {
        if (activePlan == null || activeSlotManifest == null)
            return;

        var pool = GetPlanningPool(forcePeerRefresh: DateTime.UtcNow >= nextParticipantPollUtc);
        nextParticipantPollUtc = DateTime.UtcNow + ParticipantPollInterval;

        activeParticipants.Clear();
        var runtimeParticipants = BuildCurrentManifestParticipantSet(pool);
        var resolutionBlockers = new List<string>();
        foreach (var slot in activeSlotManifest.Slots)
        {
            var participant = DadRunSlotManifestRules.ResolveSlot(
                slot,
                runtimeParticipants,
                activePlan.Orchestration.RequirePostArReady,
                out var blocker);
            participant.IsAuthority = participant.IsLocalClient && slot.IsLeader &&
                                      (IsServerDad || activePlan.Orchestration.AuthorityMode == DadAuthorityMode.LocalOnly);
            activeParticipants.Add(participant);
            LogSlotResolutionTransition(activePlan, slot, participant, blocker);
            if (!string.IsNullOrWhiteSpace(blocker))
                resolutionBlockers.Add(blocker);
        }

        var blockers = new List<string>(resolutionBlockers);
        foreach (var participant in activeParticipants.Where(participant =>
                     !participant.IsLocalClient &&
                     runtimeParticipants.Count(runtime => string.Equals(
                         runtime.WorkerSessionId.Value,
                         participant.WorkerSessionId.Value,
                         StringComparison.OrdinalIgnoreCase)) == 1))
        {
            var frozenSlot = activeSlotManifest.Slots.Single(slot =>
                string.Equals(slot.SlotId, participant.AssignedSlotId, StringComparison.OrdinalIgnoreCase));
            if (remoteAssignmentTracker.IsAccepted(activePlan.Request.RequestId, frozenSlot))
            {
                LogAssignmentTransition(activePlan, frozenSlot, "accepted/cached", "Using exact heartbeat truth after sticky assignment acceptance.");
                continue;
            }

            var ready = transportService.SendWakeRequest(participant, new DadWakeRequestDto
            {
                RunId = activePlan.Request.RequestId,
                AuthorityWorkerSessionId = presenceService.WorkerSessionId,
                AuthorityMode = activePlan.Orchestration.AuthorityMode,
                ModuleId = activePlan.CompositeModuleId,
                RequiredAccountKey = frozenSlot.AccountKey,
                RequiredCharacterKey = frozenSlot.CharacterKey,
                RequiredContentId = frozenSlot.ContentId,
                RequiredJobId = frozenSlot.RequiredJobId,
                AssignedSlotId = participant.AssignedSlotId,
                RequirePostArReady = activePlan.Orchestration.RequirePostArReady,
            });

            if (ready == null)
            {
                var pending = remoteAssignmentTracker.MarkPending(activePlan.Request.RequestId, frozenSlot);
                blockers.Add(pending.Summary);
                LogAssignmentTransition(activePlan, frozenSlot, "submitted/pending", pending.Summary);
                continue;
            }

            var assignment = remoteAssignmentTracker.Observe(
                activePlan.Request.RequestId,
                frozenSlot,
                ready,
                DateTime.UtcNow);
            if (assignment.Disposition != DadRemoteAssignmentDisposition.Accepted)
            {
                blockers.Add(assignment.Summary);
                LogAssignmentTransition(activePlan, frozenSlot, "rejected", assignment.Summary);
                continue;
            }

            LogAssignmentTransition(activePlan, frozenSlot, "accepted", assignment.Summary);
        }

        foreach (var slot in activeSlotManifest.Slots.Where(static slot => slot.RequiredJobId.HasValue))
        {
            var participant = activeParticipants.Single(candidate =>
                string.Equals(candidate.AssignedSlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase));
            if (participant.State != DadParticipantState.Discovered)
                continue;

            if (!participant.IsLocalClient &&
                !remoteAssignmentTracker.IsAccepted(activePlan.Request.RequestId, slot))
            {
                continue;
            }

            var preparationBlocker = ResolveRequestedJobPreparationBlocker(
                activePlan.Request.RequestId,
                slot,
                participant);
            if (!string.IsNullOrWhiteSpace(preparationBlocker))
                blockers.Add(preparationBlocker);
        }

        blockers.AddRange(activeParticipants
            .Where(static participant => participant.State is DadParticipantState.WaitingForRequiredCharacter or DadParticipantState.WaitingForPostArReady or DadParticipantState.Stale)
            .Select(static participant => string.IsNullOrWhiteSpace(participant.StatusText) ? participant.State.ToString() : participant.StatusText));

        CurrentResult.Participants = activeParticipants.Select(static participant => participant.Clone()).ToList();
        if (blockers.Count > 0)
        {
            if (HasTimedOut(activePlan.Orchestration.WaitPolicy.GetParticipantReadyTimeout()))
            {
                var neverAcknowledged = activeSlotManifest.Slots
                    .Where(slot =>
                        !string.Equals(
                            slot.WorkerSessionId.Value,
                            presenceService.WorkerSessionId.Value,
                            StringComparison.OrdinalIgnoreCase) &&
                        !remoteAssignmentTracker.IsAccepted(activePlan.Request.RequestId, slot))
                    .Select(slot =>
                        $"{slot.SlotId} was never acknowledged by frozen worker '{slot.WorkerSessionId}' " +
                        $"for account '{slot.AccountKey}', character '{slot.CharacterKey}', Content ID {slot.ContentId}.")
                    .ToList();
                FinalizeRun(
                    DadRunStatus.TimedOut,
                    "Dad run timed out waiting for frozen slot readiness.",
                    string.Join(
                        " | ",
                        (neverAcknowledged.Count > 0 ? neverAcknowledged : blockers)
                        .Distinct(StringComparer.OrdinalIgnoreCase)));
                return;
            }

            CurrentResult.Phase = DadRunPhase.WaitingForReadiness;
            CurrentResult.Status = DadRunStatus.WaitingForParticipants;
            CurrentResult.ActiveTaskStatus = string.Join(" | ", blockers.Distinct(StringComparer.OrdinalIgnoreCase));
            CurrentResult.BlockedReason = CurrentResult.ActiveTaskStatus;
            Publish();
            return;
        }

        Transition(DadRunPhase.ClaimingSlots, DadRunStatus.Running, $"All {activeParticipants.Count} participant(s) are ready; issuing leases.");
    }

    private void UpdateClaims()
    {
        if (activePlan == null || activeSlotManifest == null)
            return;

        var livenessBlockers = activeParticipants
            .Where(static participant => participant.State is
                DadParticipantState.WaitingForRequiredCharacter or
                DadParticipantState.WaitingForPostArReady or
                DadParticipantState.Stale)
            .Select(static participant => string.IsNullOrWhiteSpace(participant.StatusText)
                ? $"{participant.AssignedSlotId} exact frozen assignment is not ready."
                : participant.StatusText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (livenessBlockers.Count > 0)
        {
            CurrentResult.Phase = DadRunPhase.WaitingForReadiness;
            CurrentResult.Status = DadRunStatus.WaitingForParticipants;
            CurrentResult.ActiveTaskStatus = string.Join(" | ", livenessBlockers);
            CurrentResult.BlockedReason = CurrentResult.ActiveTaskStatus;
            CurrentResult.Participants = activeParticipants.Select(static participant => participant.Clone()).ToList();
            Publish();
            return;
        }

        var blockers = new List<string>();
        foreach (var participant in activeParticipants)
        {
            if (participant.State is DadParticipantState.WaitingForRequiredCharacter or DadParticipantState.WaitingForPostArReady or DadParticipantState.Stale)
            {
                blockers.Add(string.IsNullOrWhiteSpace(participant.StatusText) ? $"{participant.ActiveCharacterKey} is not ready." : participant.StatusText);
                continue;
            }

            var frozenSlot = activeSlotManifest.Slots.Single(slot =>
                string.Equals(slot.SlotId, participant.AssignedSlotId, StringComparison.OrdinalIgnoreCase));
            var request = new DadClaimRequestDto
            {
                RunId = activePlan.Request.RequestId,
                AuthorityWorkerSessionId = presenceService.WorkerSessionId,
                ModuleId = activePlan.CompositeModuleId,
                SlotId = participant.AssignedSlotId,
                RequiredAccountKey = frozenSlot.AccountKey,
                RequiredCharacterKey = frozenSlot.CharacterKey,
            };
            request.Lease = claimService.IssueLease(request, participant, activePlan.Orchestration.WaitPolicy.GetLeaseDuration());

            DadClaimDecisionDto? decision;
            if (participant.IsLocalClient)
            {
                decision = claimService.TryClaimLocal(request, presenceService.BuildSnapshotCopy());
                presenceService.ApplyClaimState(activePlan.Request.RequestId, decision.ClaimState, decision.LeaseState, decision.Lease, decision.Reason);
            }
            else
            {
                decision = transportService.RequestClaim(participant, request);
            }

            if (decision == null)
            {
                blockers.Add($"Lease acknowledgement missing for {participant.ActiveCharacterKey}.");
                continue;
            }

            if (participant.IsLocalClient)
            {
                CopyLocalParticipant(participant, decision.Snapshot);
            }
            else if (!TryApplyRemoteParticipantResponse(participant, decision.Snapshot, out var projectionBlocker))
            {
                blockers.Add(projectionBlocker);
                continue;
            }

            claimService.AcknowledgeLease(decision);
            participant.ClaimState = decision.ClaimState;
            participant.LeaseState = decision.LeaseState;
            participant.LeaseIssuedUtc = decision.Lease?.IssuedUtc;
            participant.LeaseRenewedUtc = decision.Lease?.RenewedUtc;
            participant.LeaseExpiresUtc = decision.Lease?.ExpiresUtc;

            if (!decision.Granted)
                blockers.Add(decision.Reason);
        }

        CurrentResult.Participants = activeParticipants.Select(static participant => participant.Clone()).ToList();
        CurrentResult.Leases = claimService.GetLeasesForRun(activePlan.Request.RequestId).ToList();

        if (blockers.Count > 0)
        {
            if (HasTimedOut(activePlan.Orchestration.WaitPolicy.GetParticipantReadyTimeout()))
            {
                FinalizeRun(
                    DadRunStatus.TimedOut,
                    "Dad run timed out waiting for lease grants.",
                    string.Join(" | ", blockers.Distinct(StringComparer.OrdinalIgnoreCase)));
                return;
            }

            CurrentResult.Phase = activeParticipants.Any(static participant => participant.State is
                DadParticipantState.WaitingForRequiredCharacter or
                DadParticipantState.WaitingForPostArReady or
                DadParticipantState.Stale)
                ? DadRunPhase.WaitingForReadiness
                : DadRunPhase.ClaimingSlots;
            CurrentResult.Status = DadRunStatus.WaitingForParticipants;
            CurrentResult.ActiveTaskStatus = string.Join(" | ", blockers.Distinct(StringComparer.OrdinalIgnoreCase));
            CurrentResult.BlockedReason = CurrentResult.ActiveTaskStatus;
            Publish();
            return;
        }

        Transition(DadRunPhase.AssemblingParty, DadRunStatus.Running, "Leases granted; assembling Dad party.");
    }

    private void UpdateAssembly()
    {
        if (activePlan == null)
            return;

        if (TryResolveSingleWorkerAssembly(activePlan))
            return;

        var instructions = partyAssemblyService.BuildInstructions(activePlan, activeParticipants, out var blocker);
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            if (HasTimedOut(activePlan.Orchestration.WaitPolicy.GetAssemblyTimeout()))
            {
                FinalizeRun(DadRunStatus.TimedOut, "Dad run timed out during party assembly.", blocker);
                return;
            }

            CurrentResult.ActiveTaskStatus = blocker;
            CurrentResult.BlockedReason = blocker;
            Publish();
            return;
        }

        var blockers = new List<string>();
        foreach (var instruction in instructions.Where(static instruction => instruction.InstructionKind == DadAssemblyInstructionKind.FormParty))
        {
            var participant = ResolveParticipantForInstruction(instruction);
            if (participant == null)
                continue;

            participant.State = DadParticipantState.AssemblyPending;
            DadRunStepResultDto? result = participant.IsLocalClient
                ? presenceService.HandleAssemblyInstruction(instruction)
                : transportService.SendAssemblyInstruction(participant, instruction);
            if (result == null || !result.Success)
            {
                blockers.Add(result?.FailureReason ?? $"Assembly acknowledgement missing for {participant.ActiveCharacterKey}.");
                continue;
            }

            participant.State = DadParticipantState.AssemblyConfirmed;
            participant.StatusText = result.Summary;
        }

        var partyMembers = BuildLocalPartySnapshot(out var partySnapshotBlocker);
        if (!string.IsNullOrWhiteSpace(partySnapshotBlocker))
            blockers.Add(partySnapshotBlocker);
        else
            IssueLeaderPartyInvites(activePlan, instructions, partyMembers, blockers);

        foreach (var instruction in instructions.Where(static instruction => instruction.InstructionKind == DadAssemblyInstructionKind.JoinParty))
        {
            var participant = ResolveParticipantForInstruction(instruction);
            if (participant == null)
                continue;

            if (!DadPartyAssemblyService.ShouldDispatchJoinInstruction(participant, partyMembers))
            {
                participant.State = DadParticipantState.AssemblyConfirmed;
                participant.StatusText = $"Dad Coordinator PartyList confirms {participant.ActiveCharacterKey}.";
                LogPartyTransition(activePlan, participant, "partylist-confirmed", participant.StatusText);
                continue;
            }

            participant.State = DadParticipantState.AssemblyPending;
            DadRunStepResultDto? result = participant.IsLocalClient
                ? presenceService.HandleAssemblyInstruction(instruction)
                : transportService.SendAssemblyInstruction(participant, instruction);

            if (result == null || (!result.Success && !result.Deferred))
            {
                var pending = result?.FailureReason ?? $"Join instruction acknowledgement pending for {participant.ActiveCharacterKey}.";
                blockers.Add(pending);
                LogPartyTransition(activePlan, participant, "join-pending", pending);
                continue;
            }

            participant.State = result.Success ? DadParticipantState.AssemblyConfirmed : DadParticipantState.AssemblyPending;
            participant.StatusText = result.Summary;
            if (!result.Success && !string.IsNullOrWhiteSpace(result.BlockedReason))
                blockers.Add(result.BlockedReason);
            LogPartyTransition(activePlan, participant, "join-dispatched", result.Summary);
        }

        CurrentResult.Participants = activeParticipants.Select(static participant => participant.Clone()).ToList();
        if (blockers.Count > 0)
        {
            if (HasTimedOut(activePlan.Orchestration.WaitPolicy.GetAssemblyTimeout()))
            {
                FinalizeRun(
                    DadRunStatus.TimedOut,
                    "Dad run timed out during party assembly.",
                    string.Join(" | ", blockers.Distinct(StringComparer.OrdinalIgnoreCase)));
                return;
            }

            CurrentResult.ActiveTaskStatus = string.Join(" | ", blockers.Distinct(StringComparer.OrdinalIgnoreCase));
            CurrentResult.BlockedReason = CurrentResult.ActiveTaskStatus;
            Publish();
            return;
        }

        partyMembers = BuildLocalPartySnapshot(out partySnapshotBlocker);
        var verified = partyAssemblyService.VerifyPartyMembership(activePlan, activeParticipants, partyMembers, out var verificationBlocker);
        if (!string.IsNullOrWhiteSpace(partySnapshotBlocker) || !verified)
        {
            var blockerSummary = string.IsNullOrWhiteSpace(partySnapshotBlocker) ? verificationBlocker : partySnapshotBlocker;
            if (HasTimedOut(activePlan.Orchestration.WaitPolicy.GetAssemblyTimeout()))
            {
                FinalizeRun(DadRunStatus.TimedOut, "Dad run timed out during party assembly.", blockerSummary);
                return;
            }

            CurrentResult.ActiveTaskStatus = blockerSummary;
            CurrentResult.BlockedReason = blockerSummary;
            Publish();
            return;
        }

        foreach (var participant in activeParticipants)
        {
            LogPartyTransition(
                activePlan,
                participant,
                "party-complete",
                $"Dad Coordinator PartyList confirms complete frozen membership {partyMembers.Count}/{activePlan.RequiredParticipantCount}.");
        }

        if (!partyInviteGateway.ConfirmRunPartyMembership(activePlan.Request.RequestId))
            return;

        Transition(DadRunPhase.QueuePreparing, DadRunStatus.Running, "Dad party assembly confirmed; preparing queue executor.");
    }

    private DadParticipantSnapshot? ResolveParticipantForInstruction(DadAssemblyInstructionDto instruction)
        => activeParticipants.FirstOrDefault(candidate =>
            string.Equals(candidate.ActiveCharacterKey.Value, instruction.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<DadPartyMemberSnapshot> BuildLocalPartySnapshot(out string blocker)
    {
        blocker = string.Empty;
        var members = new List<DadPartyMemberSnapshot>();

        try
        {
            foreach (var member in Plugin.PartyList)
            {
                var name = member.Name.ToString();
                members.Add(new DadPartyMemberSnapshot
                {
                    CharacterKey = new DadCharacterKey(string.Empty),
                    ContentId = member.ContentId,
                    CharacterName = name,
                    IsLocalPlayer = member.ContentId != 0 && member.ContentId == Plugin.PlayerState.ContentId,
                });
            }
        }
        catch (Exception ex)
        {
            blocker = $"Unable to read local PartyList for Dad assembly verification: {ex.Message}";
            return [];
        }

        var local = presenceService.BuildSnapshotCopy();
        if (!local.ActiveCharacterKey.IsEmpty &&
            members.All(member => !IsSamePartyMember(member, local)))
        {
            members.Add(new DadPartyMemberSnapshot
            {
                CharacterKey = local.ActiveCharacterKey,
                ContentId = local.Character.ContentId,
                CharacterName = local.Character.CharacterName,
                WorldName = local.Character.WorldName,
                IsLocalPlayer = true,
            });
        }

        return members;
    }

    private static bool IsSamePartyMember(DadPartyMemberSnapshot member, DadParticipantSnapshot participant)
    {
        if (participant.Character.ContentId != 0 && member.ContentId == participant.Character.ContentId)
            return true;

        return !member.CharacterKey.IsEmpty &&
               string.Equals(member.CharacterKey.Value, participant.ActiveCharacterKey.Value, StringComparison.OrdinalIgnoreCase);
    }

    private void IssueLeaderPartyInvites(
        DadRunPlan plan,
        IReadOnlyList<DadAssemblyInstructionDto> instructions,
        IReadOnlyList<DadPartyMemberSnapshot> partyMembers,
        List<string> blockers)
    {
        if (plan.RequiredParticipantCount <= 1)
            return;

        if (plan.Orchestration.QueueAuthority != DadQueueAuthority.Leader)
        {
            blockers.Add($"Party invite authority must be the leader; request has {plan.Orchestration.QueueAuthority}.");
            return;
        }

        if (!TryResolveLocalPartyInviter(plan, out _, out var inviteBlocker))
        {
            blockers.Add(inviteBlocker);
            return;
        }

        foreach (var instruction in instructions.Where(static instruction => instruction.InstructionKind == DadAssemblyInstructionKind.JoinParty))
        {
            var participant = ResolveParticipantForInstruction(instruction);
            if (participant == null || DadPartyAssemblyService.IsParticipantInParty(participant, partyMembers))
                continue;

            var frozenSlot = activeSlotManifest?.Slots.SingleOrDefault(slot =>
                string.Equals(slot.SlotId, instruction.SlotId, StringComparison.OrdinalIgnoreCase));
            if (frozenSlot == null)
            {
                blockers.Add($"Cannot invite {participant.ActiveCharacterKey}: its frozen slot is unavailable.");
                continue;
            }

            if (participant.Character.ContentId != frozenSlot.ContentId ||
                string.IsNullOrEmpty(participant.Character.CharacterName) ||
                participant.Character.WorldId == 0 ||
                participant.Character.WorldId > ushort.MaxValue)
            {
                blockers.Add(
                    $"Cannot invite frozen {frozenSlot.SlotId} {frozenSlot.CharacterKey}: exact name, Content ID, or World ID is unavailable.");
                continue;
            }

            var target = new DadNativePartyInviteTarget
            {
                RunId = plan.Request.RequestId,
                ModuleId = plan.CompositeModuleId,
                SlotId = frozenSlot.SlotId,
                AccountKey = frozenSlot.AccountKey,
                CharacterKey = frozenSlot.CharacterKey,
                ContentId = frozenSlot.ContentId,
                CharacterName = participant.Character.CharacterName,
                WorldId = (ushort)participant.Character.WorldId,
                WorkerSessionId = frozenSlot.WorkerSessionId,
                SameApplicableInstanceExact = false,
            };
            var attempt = partyInviteGateway.TryInvite(
                target,
                partyListContainsContentId: false,
                out var nativeInviteBlocker);
            if (!string.IsNullOrWhiteSpace(nativeInviteBlocker))
            {
                blockers.Add(nativeInviteBlocker);
                continue;
            }

            if (attempt == null)
                continue;

            log.Information(
                "[dad] Native party invite request={RequestId} module={ModuleId} slot={SlotId} account={AccountKey} character={CharacterKey} contentId={ContentId} world={WorldId} worker={WorkerSessionId} inviteType={InviteType} attempt={AttemptNumber} dispatch={DispatchResult} partyList={PartyListResult} partyCount={PartyCount} expectedCount={ExpectedCount}.",
                plan.Request.RequestId,
                plan.CompositeModuleId,
                frozenSlot.SlotId,
                frozenSlot.AccountKey,
                frozenSlot.CharacterKey,
                frozenSlot.ContentId,
                target.WorldId,
                frozenSlot.WorkerSessionId,
                attempt.InviteType,
                attempt.AttemptNumber,
                attempt.DispatchResult,
                attempt.PartyListContainsContentId,
                partyMembers.Count,
                plan.RequiredParticipantCount);

            participant.StatusText = attempt.DispatchResult
                ? $"Native {attempt.InviteType} party invite dispatched for {frozenSlot.CharacterKey}; waiting for exact PartyList Content ID {frozenSlot.ContentId}."
                : $"Native {attempt.InviteType} party invite returned false for {frozenSlot.CharacterKey}; retry {attempt.AttemptNumber + 1} is due after five seconds.";
            LogPartyTransition(plan, participant, "native-invite-attempt", participant.StatusText);
            if (!attempt.DispatchResult)
                blockers.Add(participant.StatusText);
        }
    }

    private bool TryResolveLocalPartyInviter(
        DadRunPlan plan,
        out DadParticipantSnapshot inviter,
        out string blocker)
    {
        inviter = new DadParticipantSnapshot();
        blocker = string.Empty;

        if (plan.Orchestration.InviteAuthority == DadInviteAuthority.External)
        {
            blocker = "External party inviter is not executable by Dad.";
            return false;
        }

        if (plan.Orchestration.InviteAuthority == DadInviteAuthority.NotNeeded)
        {
            blocker = "Party invite authority is Not needed, but this run requires party invites.";
            return false;
        }

        var requiredInviterKey = plan.Orchestration.InviteAuthority == DadInviteAuthority.ServerDad
            ? plan.InviterCharacterKey
            : string.IsNullOrWhiteSpace(plan.InviterCharacterKey)
                ? plan.LeaderCharacterKey
                : plan.InviterCharacterKey;
        if (string.IsNullOrWhiteSpace(requiredInviterKey))
        {
            blocker = "Party inviter is not selected.";
            return false;
        }

        var participant = activeParticipants.FirstOrDefault(candidate =>
            string.Equals(candidate.ActiveCharacterKey.Value, requiredInviterKey, StringComparison.OrdinalIgnoreCase));
        if (participant == null)
        {
            blocker = $"Configured inviter {requiredInviterKey} is offline or not part of this Dad party.";
            return false;
        }

        if (!participant.IsLocalClient)
        {
            blocker = $"Configured inviter {requiredInviterKey} is not loaded on this Dad client; remote party invite execution is not available.";
            return false;
        }

        if (!participant.PostArReady)
        {
            blocker = $"Configured inviter {requiredInviterKey} is not post-AR ready.";
            return false;
        }

        if (plan.Orchestration.InviteAuthority == DadInviteAuthority.ServerDad &&
            !activeParticipants.Any(candidate =>
                candidate.IsLocalClient &&
                string.Equals(candidate.ActiveCharacterKey.Value, requiredInviterKey, StringComparison.OrdinalIgnoreCase)))
        {
            blocker = $"Dad Coordinator inviter {requiredInviterKey} is not the loaded local character.";
            return false;
        }

        inviter = participant;
        return true;
    }

    private void UpdateModuleRouting()
    {
        if (activePlan == null)
            return;

        if (activeModuleIndex >= 0 &&
            activeModuleIndex < activePlan.Modules.Count &&
            workerStatuses.Count > 0)
        {
            UpdateWorkerExecution(activePlan.Modules[activeModuleIndex]);
            return;
        }

        activeModuleIndex++;
        if (activeModuleIndex >= activePlan.Modules.Count)
        {
            Transition(DadRunPhase.Finalizing, DadRunStatus.Running, "Dad module routing complete.");
            return;
        }

        foreach (var participant in activeParticipants)
            participant.State = DadParticipantState.QueuePending;

        var module = activePlan.Modules[activeModuleIndex];
        MarkStopPolicyAttemptStarted(activePlan);
        DispatchWorkerExecution(module);
    }

    private void DispatchWorkerExecution(DadPlannedModuleExecution module)
    {
        if (activePlan == null)
            return;

        workerStatuses.Clear();
        nextWorkerStatusPollUtc = DateTime.MinValue;
        var failures = new List<string>();
        foreach (var participant in activeParticipants)
        {
            var role = IsQueueLeaderParticipant(activePlan, participant)
                ? DadWorkerExecutionRole.QueueLeader
                : DadWorkerExecutionRole.Participant;
            var participantView = BuildWorkerParticipantView(participant, role);
            var command = new DadWorkerExecutionCommand
            {
                RunId = activePlan.Request.RequestId,
                ModuleIndex = activeModuleIndex,
                Role = role,
                Plan = activePlan,
                Participants = participantView,
                TimeoutSeconds = Math.Max(
                    60,
                    activePlan.Orchestration.WaitPolicy.ParticipantReadyTimeoutSeconds +
                    activePlan.Orchestration.WaitPolicy.AssemblyTimeoutSeconds +
                    900),
            };

            participant.RunId = activePlan.Request.RequestId;
            var targetRuntime = participant.Clone();
            targetRuntime.IsLocalClient = true;
            if (!DadWorkerCommandValidationRules.TryValidate(command, targetRuntime, out _, out var validationBlocker))
            {
                failures.Add($"{participant.AssignedSlotId} worker command rejected before dispatch: {validationBlocker}");
                continue;
            }

            DadWorkerExecutionAck? ack = participant.IsLocalClient
                ? workerExecutionService.Accept(command)
                : transportService.SendWorkerExecutionCommand(participant, command);
            if (ack == null || !ack.Accepted)
            {
                var failure = ack?.Summary ?? $"Worker command acknowledgement pending from {participant.ActiveCharacterKey}.";
                failures.Add(failure);
                LogWorkerCommandTransition(activePlan, module, participant, "rejected-or-missing", failure);
                continue;
            }

            workerStatuses[participant.WorkerSessionId.Value] = ack.Status.Clone();
            participant.State = DadParticipantState.QueuePending;
            participant.StatusText = ack.Summary;
            LogWorkerCommandTransition(activePlan, module, participant, "accepted-or-queued", ack.Summary);
        }

        if (failures.Count > 0)
        {
            var summary = string.Join(" | ", failures.Distinct(StringComparer.OrdinalIgnoreCase));
            ApplyModuleRoutingResult(module, BuildWorkerFailureResult(module, summary), replaceExisting: false);
            return;
        }

        ApplyModuleRoutingResult(
            module,
            BuildWorkerProgressResult(module, $"Assigned {workerStatuses.Count} worker(s); waiting for execution status."),
            replaceExisting: false);
    }

    private static bool IsQueueLeaderParticipant(DadRunPlan plan, DadParticipantSnapshot participant)
        => !string.IsNullOrWhiteSpace(plan.LeaderCharacterKey) &&
           string.Equals(participant.ActiveCharacterKey.Value, plan.LeaderCharacterKey, StringComparison.OrdinalIgnoreCase);

    private List<DadParticipantSnapshot> BuildWorkerParticipantView(
        DadParticipantSnapshot targetParticipant,
        DadWorkerExecutionRole targetRole)
        => activeParticipants.Select(candidate =>
        {
            var clone = candidate.Clone();
            clone.RunId = activePlan?.Request.RequestId ?? string.Empty;
            var isTarget = string.Equals(
                candidate.WorkerSessionId.Value,
                targetParticipant.WorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase);
            clone.IsLocalClient = isTarget;
            if (isTarget && targetRole == DadWorkerExecutionRole.QueueLeader)
                clone.IsAuthority = true;
            return clone;
        }).ToList();

    private void UpdateWorkerExecution(DadPlannedModuleExecution module)
    {
        if (activePlan == null || DateTime.UtcNow < nextWorkerStatusPollUtc)
            return;

        nextWorkerStatusPollUtc = DateTime.UtcNow + WorkerStatusPollInterval;
        var failures = new List<string>();
        foreach (var participant in activeParticipants)
        {
            DadWorkerExecutionStatus? workerStatus = participant.IsLocalClient
                ? workerExecutionService.GetStatus()
                : transportService.GetWorkerExecutionStatus(participant);
            if (workerStatus == null ||
                !string.Equals(workerStatus.RunId, activePlan.Request.RequestId, StringComparison.OrdinalIgnoreCase))
            {
                if (DateTime.UtcNow - participant.LastHeartbeatUtc >= activePlan.Orchestration.WaitPolicy.GetHeartbeatStaleThreshold())
                    failures.Add($"Worker {participant.ActiveCharacterKey} heartbeat/status is stale.");
                continue;
            }

            workerStatuses[participant.WorkerSessionId.Value] = workerStatus.Clone();
            participant.State = workerStatus.State switch
            {
                DadWorkerExecutionState.Completed => DadParticipantState.Completed,
                DadWorkerExecutionState.Failed or DadWorkerExecutionState.TimedOut => DadParticipantState.Failed,
                DadWorkerExecutionState.Cancelled => DadParticipantState.Cancelled,
                DadWorkerExecutionState.Running => DadParticipantState.Running,
                _ => DadParticipantState.QueuePending,
            };
            participant.StatusText = workerStatus.Summary;
            if (workerStatus.IsTerminal && !workerStatus.Success)
                failures.Add(string.IsNullOrWhiteSpace(workerStatus.FailureReason) ? workerStatus.Summary : workerStatus.FailureReason);
        }

        if (failures.Count > 0)
        {
            foreach (var participant in activeParticipants.Where(static participant => !participant.IsLocalClient))
            {
                transportService.SendWorkerExecutionCancel(participant, new DadWorkerExecutionCancel
                {
                    RunId = activePlan.Request.RequestId,
                    Reason = "Peer worker failed; releasing run-owned work.",
                });
            }
            workerExecutionService.Cancel(new DadWorkerExecutionCancel
            {
                RunId = activePlan.Request.RequestId,
                Reason = "Peer worker failed; releasing run-owned work.",
            });
            ApplyModuleRoutingResult(
                module,
                BuildWorkerFailureResult(module, string.Join(" | ", failures.Distinct(StringComparer.OrdinalIgnoreCase))),
                replaceExisting: true);
            return;
        }

        var statuses = workerStatuses.Values.ToList();
        var leaderStatus = statuses.FirstOrDefault(static worker => worker.Role == DadWorkerExecutionRole.QueueLeader);
        if (statuses.Count == activeParticipants.Count && statuses.All(static worker => worker.IsTerminal && worker.Success))
        {
            var result = leaderStatus?.StepResult.Clone() ?? BuildWorkerProgressResult(module, $"All {statuses.Count} workers completed.");
            result.Success = true;
            result.Deferred = false;
            result.ParticipantState = DadParticipantState.Completed;
            result.Summary = $"All {statuses.Count} workers completed {module.DisplayName}. {result.Summary}".Trim();
            result.ExecutorStatus.IsActive = false;
            result.ExecutorStatus.Status = DadRunStatus.Completed;
            result.ExecutorStatus.Phase = DadRunPhase.Finalizing;
            result.ExecutorStatus.CompletedAtUtc ??= DateTime.UtcNow;
            result.ExecutorStatus.UpdatedAtUtc = DateTime.UtcNow;
            ApplyModuleRoutingResult(module, result, replaceExisting: true);
            workerStatuses.Clear();
            return;
        }

        var progress = leaderStatus?.StepResult.StepName?.Length > 0
            ? leaderStatus.StepResult.Clone()
            : BuildWorkerProgressResult(
                module,
                $"Workers: {statuses.Count(static worker => worker.State == DadWorkerExecutionState.Running)} running, " +
                $"{statuses.Count(static worker => worker.State == DadWorkerExecutionState.WaitingForQueue)} waiting, " +
                $"{statuses.Count(static worker => worker.IsTerminal)} complete.");
        progress.ExecutorStatus.IsActive = true;
        progress.ExecutorStatus.Status = DadRunStatus.Running;
        ApplyModuleRoutingResult(module, progress, replaceExisting: true);
    }

    private DadRunStepResultDto BuildWorkerProgressResult(DadPlannedModuleExecution module, string summary)
        => new()
        {
            RunId = activePlan?.Request.RequestId ?? string.Empty,
            ModuleId = module.ModuleId,
            StepName = module.DisplayName,
            ParticipantState = DadParticipantState.QueuePending,
            Success = true,
            Summary = summary,
            ExecutorStatus = new DadModuleExecutionStatusDto
            {
                RunId = activePlan?.Request.RequestId ?? string.Empty,
                ModuleId = module.ModuleId,
                DisplayName = module.DisplayName,
                Phase = DadRunPhase.QueueStarting,
                Status = DadRunStatus.Running,
                StepName = "Distributed workers",
                IsActive = true,
                CanStart = true,
                UpdatedAtUtc = DateTime.UtcNow,
                Summary = summary,
            },
        };

    private DadRunStepResultDto BuildWorkerFailureResult(DadPlannedModuleExecution module, string reason)
    {
        var result = BuildWorkerProgressResult(module, reason);
        result.Success = false;
        result.ParticipantState = DadParticipantState.Failed;
        result.FailureReason = reason;
        result.BlockedReason = reason;
        result.ExecutorStatus.IsActive = false;
        result.ExecutorStatus.Status = DadRunStatus.Failed;
        result.ExecutorStatus.Phase = DadRunPhase.Finalizing;
        result.ExecutorStatus.FailureReason = reason;
        result.ExecutorStatus.BlockedReason = reason;
        result.ExecutorStatus.CompletedAtUtc = DateTime.UtcNow;
        return result;
    }

    private void ApplyModuleRoutingResult(DadPlannedModuleExecution module, DadRunStepResultDto result, bool replaceExisting)
    {
        if (activePlan == null)
            return;

        if (replaceExisting && activeStepResultIndex >= 0 && activeStepResultIndex < stepResults.Count)
            stepResults[activeStepResultIndex] = result;
        else if (replaceExisting && stepResults.Count > 0)
            stepResults[^1] = result;
        else
        {
            stepResults.Add(result);
            activeStepResultIndex = stepResults.Count - 1;
        }

        CurrentResult.StepResults = stepResults.Select(static step => step.Clone()).ToList();
        CurrentResult.CurrentExecutorStatus = result.ExecutorStatus.Clone();
        if (result.ExecutorStatus.Phase != DadRunPhase.Idle)
            CurrentResult.Phase = result.ExecutorStatus.Phase;
        CurrentResult.Status = DadRunStatus.Running;
        CurrentResult.ActiveTaskIndex = IsStopPolicyLoopEligible(activePlan)
            ? Math.Max(1, stopProgress.StartedRuns)
            : activeModuleIndex + 1;
        CurrentResult.ActiveTaskName = module.DisplayName;
        CurrentResult.ActiveTaskStatus = result.Summary;
        RefreshStopProgressSummary(activePlan, refreshPool: false);
        CurrentResult.StopProgress = stopProgress.Clone();
        CurrentResult.CompletedTaskCount = stepResults.Count(static step =>
            step.Success &&
            step.ExecutorStatus.Status == DadRunStatus.Completed &&
            !step.ExecutorStatus.IsActive);
        CurrentResult.BlockedReason = string.Join(" | ", stepResults
            .Where(static step => !string.IsNullOrWhiteSpace(step.BlockedReason))
            .Select(static step => step.BlockedReason)
            .Distinct(StringComparer.OrdinalIgnoreCase));

        var participantState = ResolveModuleParticipantState(result);
        foreach (var participant in activeParticipants)
            participant.State = participantState;

        presenceService.SetLeaderState(activePlan.Request.RequestId, participantState, result.Summary);
        CurrentResult.Participants = activeParticipants.Select(static participant => participant.Clone()).ToList();
        CurrentResult.Leases = claimService.GetLeasesForRun(activePlan.Request.RequestId).ToList();

        if (!result.Success)
        {
            FinalizeRun(
                result.TimedOut ? DadRunStatus.TimedOut : DadRunStatus.PartialFailure,
                result.Summary,
                string.IsNullOrWhiteSpace(result.FailureReason) ? "Dad module routing failed." : result.FailureReason);
            return;
        }

        if (!result.ExecutorStatus.IsActive && result.ExecutorStatus.Status == DadRunStatus.Completed)
        {
            if (activeModuleIndex + 1 >= activePlan.Modules.Count)
            {
                if (TryContinueStopPolicyLoop(module, result))
                    return;

                Transition(DadRunPhase.Finalizing, DadRunStatus.Running, "Dad module routing complete.");
                return;
            }

            CurrentResult.Phase = DadRunPhase.RoutingModules;
            Publish();
            return;
        }

        Publish();
    }

    private void MarkStopPolicyAttemptStarted(DadRunPlan plan)
    {
        if (!IsStopPolicyLoopEligible(plan))
            return;

        stopProgress.StartedRuns++;
        RefreshStopProgressSummary(plan, refreshPool: false);
        CurrentResult.StopProgress = stopProgress.Clone();
        CurrentResult.TotalTaskCount = stopProgress.SafetyCap;
    }

    private bool TryContinueStopPolicyLoop(DadPlannedModuleExecution module, DadRunStepResultDto result)
    {
        if (activePlan == null || !IsStopPolicyLoopEligible(activePlan))
            return false;

        RefreshStopProgressSummary(activePlan, refreshPool: true);
        CurrentResult.StopProgress = stopProgress.Clone();
        CurrentResult.CompletedTaskCount = stopProgress.CompletedRuns;
        CurrentResult.TotalTaskCount = stopProgress.SafetyCap;

        if (stopProgress.StopReached)
        {
            Transition(
                DadRunPhase.Finalizing,
                DadRunStatus.Running,
                $"Dad stop policy reached after {stopProgress.CompletedRuns} run(s): {stopProgress.Summary}");
            return true;
        }

        if (stopProgress.SafetyCapReached)
        {
            FinalizeRun(
                DadRunStatus.PartialFailure,
                $"Dad stop policy safety cap reached after {stopProgress.CompletedRuns} run(s).",
                stopProgress.Summary);
            return true;
        }

        // The accepted plan and slot manifest are the execution contract. A repeat
        // refreshes liveness below, but never asks the generic planner to select a
        // second party.
        var nextPlan = activePlan;

        activePlan = nextPlan;
        activeParticipants.Clear();
        activeModuleIndex = -1;
        activeStepResultIndex = -1;
        loggedSingleWorkerSeed = false;
        loggedSingleWorkerAssemblyConfirmed = false;
        lastSingleWorkerAssemblyBlocker = string.Empty;
        nextParticipantPollUtc = DateTime.MinValue;
        nextWorkerStatusPollUtc = DateTime.MinValue;
        workerStatuses.Clear();
        partyInviteGateway.Reset();
        claimService.ReleaseClaims(nextPlan.Request.RequestId);
        presenceService.MarkLeader(nextPlan.Request.RequestId, nextPlan.Orchestration.AuthorityMode, $"Dad Coordinator repeating {module.DisplayName}; {stopProgress.Summary}");
        if (!TryBeginLocalRequestedJobPreparation(nextPlan, activeSlotManifest, out var preparationBlocker))
        {
            FinalizeRun(
                DadRunStatus.PartialFailure,
                "Dad stop policy could not restore requested-job preparation.",
                preparationBlocker);
            return true;
        }
        SeedLocalParticipantIfNeeded(nextPlan);

        CurrentResult.Request = nextPlan.Request;
        CurrentResult.ModuleId = nextPlan.CompositeModuleId;
        CurrentResult.AuthorityMode = nextPlan.Orchestration.AuthorityMode;
        CurrentResult.TransportMode = nextPlan.Orchestration.TransportMode;
        CurrentResult.LocalOnlyEnabled = nextPlan.Orchestration.LocalOnlyOverride;
        CurrentResult.Summary = $"Dad stop policy continuing: {stopProgress.Summary}";
        CurrentResult.ActiveTaskName = string.Empty;
        CurrentResult.ActiveTaskStatus = CurrentResult.Summary;
        CurrentResult.Participants = activeParticipants.Select(static participant => participant.Clone()).ToList();
        CurrentResult.Leases = [];
        CurrentResult.StepResults = stepResults.Select(static step => step.Clone()).ToList();

        var requiresDiscovery = RequiresParticipantDiscovery(nextPlan, activeSlotManifest);
        Transition(requiresDiscovery ? DadRunPhase.DiscoveringParticipants : DadRunPhase.ClaimingSlots,
            requiresDiscovery ? DadRunStatus.WaitingForParticipants : DadRunStatus.Running,
            requiresDiscovery
                ? $"Stop policy continuing; waiting for {nextPlan.RequiredParticipantCount} participant(s). {stopProgress.Summary}"
                : $"Stop policy continuing; local runner ready for next run. {stopProgress.Summary}");
        return true;
    }

    private void RefreshStopProgressSummary(DadRunPlan plan, bool refreshPool)
    {
        var policy = plan.Request.StopPolicy.Normalize();
        stopProgress.StopPolicy = policy.Clone();
        stopProgress.SafetyCap = policy.GetSafetyCap();
        stopProgress.CompletedRuns = CountCompletedStopPolicyRuns(plan);
        if (refreshPool)
        {
            var pool = RefreshStopPolicyPool(plan);
            stopProgress.CurrentLevel = ResolveStopPolicyCurrentLevel(policy, pool);
        }

        stopProgress.RestedExperience = policy.Mode == DadPlannerStopMode.RestedXpDepleted
            ? DadGameStateReader.GetRestedExperience()
            : null;

        stopProgress.StopReached = policy.Mode switch
        {
            DadPlannerStopMode.AfterRuns => stopProgress.CompletedRuns >= Math.Max(1, policy.AfterRuns),
            DadPlannerStopMode.TargetLevel => stopProgress.CurrentLevel.HasValue && stopProgress.CurrentLevel.Value >= policy.TargetLevel,
            // Feature batch A: item-target stop condition (SafetyCap below still bounds the run).
            DadPlannerStopMode.ItemTarget => DadGameStateReader.GetInventoryItemCount(policy.StopItemId) >= Math.Max(1, policy.StopItemTargetCount),
            DadPlannerStopMode.RestedXpDepleted => stopProgress.RestedExperience == 0,
            _ => stopProgress.CompletedRuns >= Math.Max(1, policy.AfterRuns),
        };
        stopProgress.SafetyCapReached = !stopProgress.StopReached &&
                                        stopProgress.CompletedRuns >= stopProgress.SafetyCap;
        stopProgress.Summary = BuildStopProgressSummary(stopProgress);
    }

    private int CountCompletedStopPolicyRuns(DadRunPlan plan)
    {
        var eligibleModules = ResolveStopPolicyEligibleModules(plan);
        return stepResults.Count(step =>
            step.Success &&
            !step.ExecutorStatus.IsActive &&
            step.ExecutorStatus.Status == DadRunStatus.Completed &&
            eligibleModules.Contains(step.ModuleId));
    }

    private DadCharacterPool RefreshStopPolicyPool(DadRunPlan plan)
    {
        var pool = characterIntelligenceService.RefreshLocalCharacterPool("stop-policy", logRefresh: false);
        return plan.RequiresRemoteParticipants
            ? characterIntelligenceService.RequestPeerSnapshots()
            : pool;
    }

    private static int? ResolveStopPolicyCurrentLevel(DadRunStopPolicy policy, DadCharacterPool pool)
    {
        if (policy.Mode != DadPlannerStopMode.TargetLevel || policy.TargetCharacterKey.IsEmpty)
            return null;

        var character = pool.Characters
            .FirstOrDefault(character => string.Equals(
                character.CharacterKey,
                policy.TargetCharacterKey.Value,
                StringComparison.OrdinalIgnoreCase));
        return character == null
            ? null
            : DadRosterCharacterMerge.ResolveCurrentLevel(
                character.JobLevels,
                character.CurrentJobId,
                character.CurrentLevel);
    }

    private static string BuildStopProgressSummary(DadRunStopProgress progress)
    {
        var policy = progress.StopPolicy;
        return policy.Mode switch
        {
            DadPlannerStopMode.TargetLevel => progress.CurrentLevel.HasValue
                ? $"target level {policy.TargetLevel}; current {progress.CurrentLevel.Value}; completed {progress.CompletedRuns}/{progress.SafetyCap} run(s)"
                : $"target level {policy.TargetLevel}; current level unknown; completed {progress.CompletedRuns}/{progress.SafetyCap} run(s)",
            DadPlannerStopMode.ItemTarget => $"item {policy.StopItemId} target {Math.Max(1, policy.StopItemTargetCount)}; completed {progress.CompletedRuns}/{progress.SafetyCap} run(s)",
            DadPlannerStopMode.RestedXpDepleted => progress.RestedExperience.HasValue
                ? $"rested XP {progress.RestedExperience.Value}; completed {progress.CompletedRuns}/{progress.SafetyCap} run(s)"
                : $"rested XP unknown; completed {progress.CompletedRuns}/{progress.SafetyCap} run(s)",
            _ => $"completed {progress.CompletedRuns}/{Math.Max(1, policy.AfterRuns)} run(s)",
        };
    }

    private static bool IsStopPolicyLoopEligible(DadRunPlan plan)
    {
        if (plan.Modules.Count != 1)
            return false;

        return ResolveStopPolicyEligibleModules(plan).Count > 0;
    }

    private static HashSet<DadModuleId> ResolveStopPolicyEligibleModules(DadRunPlan plan)
    {
        var modules = new HashSet<DadModuleId>();
        foreach (var module in plan.Modules)
        {
            if (DadStopPolicyLoopRules.IsEligibleModule(module.ModuleId))
                modules.Add(module.ModuleId);
        }

        if (plan.Request.Dungeon?.QueueViaLanParty == true)
            modules.Add(DadModuleId.Duty);

        return modules;
    }

    private static DadParticipantState ResolveModuleParticipantState(DadRunStepResultDto result)
    {
        if (result.ParticipantState != DadParticipantState.Unknown)
            return result.ParticipantState;

        return result.ExecutorStatus.Phase switch
        {
            DadRunPhase.InDutyOrTask => DadParticipantState.Running,
            DadRunPhase.Finalizing when result.ExecutorStatus.Status == DadRunStatus.Completed => DadParticipantState.Completed,
            DadRunPhase.Finalizing when result.ExecutorStatus.Status == DadRunStatus.Cancelled => DadParticipantState.Cancelled,
            DadRunPhase.Finalizing => DadParticipantState.Failed,
            _ => result.Deferred ? DadParticipantState.QueuePending : DadParticipantState.Running,
        };
    }

    private void CompleteRun()
    {
        var deferredReasons = stepResults
            .Where(static step => step.Deferred && !string.IsNullOrWhiteSpace(step.BlockedReason))
            .Select(static step => step.BlockedReason)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summary = deferredReasons.Count == 0
            ? stopProgress.StopReached
                ? $"Dad run completed; stop policy reached: {stopProgress.Summary}"
                : "Dad run completed."
            : $"Dad orchestration completed; guarded live executor deferred: {string.Join(" | ", deferredReasons)}";

        FinalizeRun(DadRunStatus.Completed, summary, string.Empty);
    }

    private DadCharacterPool GetPlanningPool(bool forcePeerRefresh)
    {
        if (forcePeerRefresh && activePlan != null && activePlan.RequiresRemoteParticipants)
            return characterIntelligenceService.RequestPeerSnapshots();

        return characterIntelligenceService.CurrentPool;
    }

    private IReadOnlyList<DadParticipantSnapshot> BuildOnlineParticipantSet(DadCharacterPool pool)
        => DadCoordinatorRuntimeProjectionRules.BuildOnlineParticipantSet(
            presenceService.BuildSnapshotCopy(),
            pool.PeerTransport.KnownParticipants,
            transportService.IsWorkerOnline);

    private IReadOnlyList<DadParticipantSnapshot> BuildCurrentManifestParticipantSet(DadCharacterPool pool)
    {
        if (activeSlotManifest == null)
            return [];

        var frozenSessions = activeSlotManifest.Slots
            .Select(static slot => slot.WorkerSessionId.Value)
            .Where(static session => !string.IsNullOrWhiteSpace(session))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var local = presenceService.BuildSnapshotCopy();
        return DadCoordinatorRuntimeProjectionRules.BuildFrozenParticipantSet(
            local,
            pool.PeerTransport.KnownParticipants,
            frozenSessions,
            transportService.IsWorkerOnline);
    }

    private void LogAcceptedSlotManifest(DadRunPlan plan, DadRunSlotManifest? manifest)
    {
        if (manifest == null)
            return;

        foreach (var module in manifest.Modules)
        {
            foreach (var slot in manifest.Slots)
            {
                log.Information(
                    "[dad] Frozen run assignment accepted request={RequestId} module={ModuleId} duty={DutyName} cfc={ContentFinderConditionId} unsynced={Unsynced} party={PartySize} slot={SlotId} account={AccountKey} character={CharacterKey} contentId={ContentId} worker={WorkerSessionId} leader={IsLeader} inviter={IsInviter}.",
                    plan.Request.RequestId,
                    module.ModuleId,
                    module.DutyName,
                    module.ContentFinderConditionId,
                    module.Unsynced,
                    module.ExpectedPartySize,
                    slot.SlotId,
                    slot.AccountKey,
                    slot.CharacterKey,
                    slot.ContentId,
                    slot.WorkerSessionId,
                    slot.IsLeader,
                    slot.IsInviter);
            }
        }
    }

    private void LogSlotResolutionTransition(
        DadRunPlan plan,
        DadFrozenRunSlot slot,
        DadParticipantSnapshot participant,
        string blocker)
    {
        var transition = string.Join(
            "|",
            participant.State,
            participant.IsAvailable,
            participant.ManagedAccountKey.Value,
            participant.ActiveCharacterKey.Value,
            participant.Character.ContentId,
            participant.WorkerSessionId.Value,
            blocker);
        if (slotResolutionTransitions.TryGetValue(slot.SlotId, out var previous) &&
            string.Equals(previous, transition, StringComparison.Ordinal))
        {
            return;
        }

        slotResolutionTransitions[slot.SlotId] = transition;
        log.Information(
            "[dad] Frozen slot transition request={RequestId} module={ModuleId} slot={SlotId} account={AccountKey} character={CharacterKey} contentId={ContentId} worker={WorkerSessionId} state={State} blocker={Blocker}.",
            plan.Request.RequestId,
            plan.CompositeModuleId,
            slot.SlotId,
            slot.AccountKey,
            slot.CharacterKey,
            slot.ContentId,
            slot.WorkerSessionId,
            participant.State,
            string.IsNullOrWhiteSpace(blocker) ? "none" : blocker);
    }

    private void LogAssignmentTransition(
        DadRunPlan plan,
        DadFrozenRunSlot slot,
        string state,
        string summary)
    {
        var key = $"{plan.Request.RequestId}|{slot.SlotId}";
        var transition = $"{state}|{summary}";
        if (assignmentTransitions.TryGetValue(key, out var prior) && string.Equals(prior, transition, StringComparison.Ordinal))
            return;

        assignmentTransitions[key] = transition;
        log.Information(
            "[dad] Assignment transition request={RequestId} module={ModuleId} slot={SlotId} account={AccountKey} character={CharacterKey} contentId={ContentId} worker={WorkerSessionId} state={State} summary={Summary}.",
            plan.Request.RequestId,
            plan.CompositeModuleId,
            slot.SlotId,
            slot.AccountKey,
            slot.CharacterKey,
            slot.ContentId,
            slot.WorkerSessionId,
            state,
            summary);
    }

    private void LogPartyTransition(
        DadRunPlan plan,
        DadParticipantSnapshot participant,
        string state,
        string summary)
    {
        var slot = activeSlotManifest?.Slots.FirstOrDefault(candidate =>
            string.Equals(candidate.SlotId, participant.AssignedSlotId, StringComparison.OrdinalIgnoreCase));
        var key = $"{plan.Request.RequestId}|{participant.AssignedSlotId}|{state}";
        if (partyTransitions.TryGetValue(key, out var prior) && string.Equals(prior, summary, StringComparison.Ordinal))
            return;

        partyTransitions[key] = summary;
        log.Information(
            "[dad] Party transition request={RequestId} module={ModuleId} slot={SlotId} account={AccountKey} character={CharacterKey} contentId={ContentId} worker={WorkerSessionId} state={State} summary={Summary}.",
            plan.Request.RequestId,
            plan.CompositeModuleId,
            participant.AssignedSlotId,
            slot?.AccountKey ?? participant.ManagedAccountKey,
            slot?.CharacterKey ?? participant.ActiveCharacterKey,
            slot?.ContentId ?? participant.Character.ContentId,
            slot?.WorkerSessionId ?? participant.WorkerSessionId,
            state,
            summary);
    }

    private void LogWorkerCommandTransition(
        DadRunPlan plan,
        DadPlannedModuleExecution module,
        DadParticipantSnapshot participant,
        string state,
        string summary)
    {
        var slot = activeSlotManifest?.Slots.FirstOrDefault(candidate =>
            string.Equals(candidate.SlotId, participant.AssignedSlotId, StringComparison.OrdinalIgnoreCase));
        var key = $"{plan.Request.RequestId}|{module.ModuleId}|{participant.AssignedSlotId}";
        var transition = $"{state}|{summary}";
        if (workerCommandTransitions.TryGetValue(key, out var prior) && string.Equals(prior, transition, StringComparison.Ordinal))
            return;

        workerCommandTransitions[key] = transition;
        log.Information(
            "[dad] Queue dispatch transition request={RequestId} module={ModuleId} slot={SlotId} account={AccountKey} character={CharacterKey} contentId={ContentId} worker={WorkerSessionId} state={State} summary={Summary}.",
            plan.Request.RequestId,
            module.ModuleId,
            participant.AssignedSlotId,
            slot?.AccountKey ?? participant.ManagedAccountKey,
            slot?.CharacterKey ?? participant.ActiveCharacterKey,
            slot?.ContentId ?? participant.Character.ContentId,
            slot?.WorkerSessionId ?? participant.WorkerSessionId,
            state,
            summary);
    }

    private DadParticipantSnapshot BuildLocalAssignment(string requiredCharacterKey, DadAuthorityMode authorityMode, string slotId)
    {
        var participant = presenceService.BuildSnapshotCopy();
        participant.AssignedSlotId = slotId;
        participant.IsAuthority = IsServerDad || authorityMode == DadAuthorityMode.LocalOnly;
        participant.State = string.Equals(participant.ActiveCharacterKey, requiredCharacterKey, StringComparison.OrdinalIgnoreCase)
            ? participant.State
            : DadParticipantState.WaitingForRequiredCharacter;
        if (!string.Equals(participant.ActiveCharacterKey, requiredCharacterKey, StringComparison.OrdinalIgnoreCase))
            participant.StatusText = $"Waiting for required character {requiredCharacterKey}.";
        return participant;
    }

    private static bool RequiresParticipantDiscovery(DadRunPlan plan, DadRunSlotManifest? manifest)
        => plan.RequiresRemoteParticipants ||
           (manifest?.Slots.Any(static slot => slot.RequiredJobId.HasValue) ?? false);

    private bool TryBeginLocalRequestedJobPreparation(
        DadRunPlan plan,
        DadRunSlotManifest? manifest,
        out string blocker)
    {
        blocker = string.Empty;
        if (manifest == null)
            return true;

        var localRequestedSlots = manifest.Slots
            .Where(slot =>
                slot.RequiredJobId.HasValue &&
                string.Equals(
                    slot.WorkerSessionId.Value,
                    presenceService.WorkerSessionId.Value,
                    StringComparison.Ordinal))
            .ToList();
        if (localRequestedSlots.Count == 0)
            return true;

        if (localRequestedSlots.Count != 1)
        {
            blocker = $"Run {plan.Request.RequestId} maps {localRequestedSlots.Count} requested-job slots to the local worker; expected exactly one.";
            return false;
        }

        return presenceService.BeginRequestedJobPreparation(
            plan.Request.RequestId,
            localRequestedSlots[0],
            out blocker);
    }

    private static string ResolveRequestedJobPreparationBlocker(
        string runId,
        DadFrozenRunSlot slot,
        DadParticipantSnapshot participant)
    {
        if (!slot.RequiredJobId.HasValue)
            return string.Empty;

        var expected = new DadRequestedJobPreparationKey(
            runId,
            slot.WorkerSessionId,
            slot.SlotId,
            slot.AccountKey,
            slot.CharacterKey,
            slot.ContentId,
            slot.RequiredJobId);
        var proof = participant.RequestedJobPreparation;
        if (DadRequestedJobPreparationProofRules.PermitsReadiness(
                proof,
                expected,
                participant.Character.CurrentJobId.GetValueOrDefault()))
        {
            return string.Empty;
        }

        if (!DadRequestedJobPreparationProofRules.Matches(proof, expected))
        {
            return $"{slot.SlotId} is waiting for exact requested-job preparation proof for job {slot.RequiredJobId} " +
                   $"from frozen worker '{slot.WorkerSessionId}'.";
        }

        return $"{slot.SlotId} requested-job preparation is {proof!.Status}: " +
               (string.IsNullOrWhiteSpace(proof.Summary) ? "waiting for a terminal preparation result." : proof.Summary);
    }

    private void SeedLocalParticipantIfNeeded(DadRunPlan plan)
    {
        if (plan.RequiresRemoteParticipants || activeParticipants.Any(static participant => participant.IsLocalClient))
            return;

        var participant = BuildLocalAssignment(plan.LeaderCharacterKey, plan.Orchestration.AuthorityMode, DadPlannerSlotRules.LeaderSlotId);
        activeParticipants.Add(participant);

        if (loggedSingleWorkerSeed)
            return;

        log.Information(
            "[dad] Seeded local participant {CharacterKey} for {RunShape} run {RequestId}.",
            participant.ActiveCharacterKey,
            plan.Orchestration.LocalOnlyOverride ? "local-only" : "single-worker",
            plan.Request.RequestId);
        loggedSingleWorkerSeed = true;
    }

    private bool TryResolveSingleWorkerAssembly(DadRunPlan plan)
    {
        if (plan.RequiredParticipantCount != 1 || activeParticipants.Count != 1 || !activeParticipants[0].IsLocalClient)
            return false;

        var participant = activeParticipants[0];
        var blocker = ResolveSingleWorkerAssemblyBlocker(plan, participant);
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            if (HasTimedOut(plan.Orchestration.WaitPolicy.GetAssemblyTimeout()))
            {
                FinalizeRun(DadRunStatus.TimedOut, "Dad run timed out during party assembly.", blocker);
                return true;
            }

            presenceService.SetLeaderState(plan.Request.RequestId, participant.State, blocker);
            CurrentResult.Participants = activeParticipants.Select(static candidate => candidate.Clone()).ToList();
            CurrentResult.ActiveTaskStatus = blocker;
            CurrentResult.BlockedReason = blocker;
            LogSingleWorkerAssemblyBlocker(blocker);
            Publish();
            return true;
        }

        var summary = plan.Orchestration.LocalOnlyOverride
            ? $"Local-only assembly already satisfied on {participant.ActiveCharacterKey}."
            : $"Single-worker assembly already satisfied on {participant.ActiveCharacterKey}.";

        participant.State = DadParticipantState.AssemblyConfirmed;
        participant.StatusText = summary;
        presenceService.SetLeaderState(plan.Request.RequestId, DadParticipantState.AssemblyConfirmed, summary);
        CurrentResult.Participants = activeParticipants.Select(static candidate => candidate.Clone()).ToList();

        if (!loggedSingleWorkerAssemblyConfirmed)
        {
            log.Information("[dad] {Summary}", summary);
            loggedSingleWorkerAssemblyConfirmed = true;
        }

        Transition(
            DadRunPhase.QueuePreparing,
            DadRunStatus.Running,
            plan.Orchestration.LocalOnlyOverride
                ? "Local-only assembly confirmed; preparing queue executor."
                : "Single-worker assembly confirmed; preparing queue executor.");
        return true;
    }

    private string ResolveSingleWorkerAssemblyBlocker(DadRunPlan plan, DadParticipantSnapshot participant)
    {
        if (!string.IsNullOrWhiteSpace(plan.LeaderCharacterKey) &&
            !string.Equals(participant.ActiveCharacterKey, plan.LeaderCharacterKey, StringComparison.OrdinalIgnoreCase))
        {
            participant.State = DadParticipantState.WaitingForRequiredCharacter;
            participant.StatusText = $"Waiting for required character {plan.LeaderCharacterKey}; active {participant.ActiveCharacterKey}.";
            return participant.StatusText;
        }

        if (participant.ClaimState != DadClaimState.Granted || participant.LeaseState != DadParticipantLeaseState.Granted)
        {
            participant.StatusText = string.IsNullOrWhiteSpace(participant.StatusText)
                ? "Waiting for local lease grant."
                : participant.StatusText;
            return participant.StatusText;
        }

        if (plan.Orchestration.RequirePostArReady && !participant.PostArReady)
        {
            participant.State = DadParticipantState.WaitingForPostArReady;
            participant.StatusText = "Waiting for post-AR readiness.";
            return participant.StatusText;
        }

        lastSingleWorkerAssemblyBlocker = string.Empty;
        return string.Empty;
    }

    private void LogSingleWorkerAssemblyBlocker(string blocker)
    {
        if (string.Equals(lastSingleWorkerAssemblyBlocker, blocker, StringComparison.OrdinalIgnoreCase))
            return;

        log.Debug("[dad] Single-worker assembly deferred: {Blocker}", blocker);
        lastSingleWorkerAssemblyBlocker = blocker;
    }

    private void ApplyConfigurationDefaults(DadRunRequest request)
    {
        request.StopPolicy ??= new DadRunStopPolicy();
        request.StopPolicy.Normalize();
        request.Orchestration ??= new DadOrchestrationIntent();
        request.Orchestration.LocalOnlyOverride |= configuration.LocalOnlyModeEnabled;
        request.Orchestration.WaitPolicy ??= new DadRunWaitPolicy();
        request.Orchestration.WaitPolicy.ParticipantReadyTimeoutSeconds = request.Orchestration.WaitPolicy.ParticipantReadyTimeoutSeconds <= 0
            ? configuration.ParticipantReadyTimeoutSeconds
            : request.Orchestration.WaitPolicy.ParticipantReadyTimeoutSeconds;
        request.Orchestration.WaitPolicy.AssemblyTimeoutSeconds = request.Orchestration.WaitPolicy.AssemblyTimeoutSeconds <= 0
            ? configuration.AssemblyTimeoutSeconds
            : request.Orchestration.WaitPolicy.AssemblyTimeoutSeconds;
        request.Orchestration.WaitPolicy.HeartbeatStaleSeconds = request.Orchestration.WaitPolicy.HeartbeatStaleSeconds <= 0
            ? configuration.HeartbeatStaleSeconds
            : request.Orchestration.WaitPolicy.HeartbeatStaleSeconds;
        request.Orchestration.WaitPolicy.LeaseDurationSeconds = request.Orchestration.WaitPolicy.LeaseDurationSeconds <= 0
            ? configuration.LeaseDurationSeconds
            : request.Orchestration.WaitPolicy.LeaseDurationSeconds;
        request.Orchestration.WaitPolicy.CancelAckTimeoutSeconds = request.Orchestration.WaitPolicy.CancelAckTimeoutSeconds <= 0
            ? configuration.CancelAckTimeoutSeconds
            : request.Orchestration.WaitPolicy.CancelAckTimeoutSeconds;
        if (request.Blunderville != null && string.IsNullOrWhiteSpace(request.Blunderville.EmoteCommand))
            request.Blunderville.EmoteCommand = configManager.GetActiveConfig().BlundervilleEmoteCommand;
        request.ApplyOrchestrationDefaults();
    }

    // Single source of truth (review L5): Plugin.cs previously held an identical private copy.
    internal static bool RequiresServerDadAuthority(DadRunRequest request)
    {
        if (request.Orchestration.LocalOnlyOverride)
            return false;

        if (request.Orchestration.RosterIntent.RequireRemoteParticipants ||
            request.Orchestration.RosterIntent.ExpectedPartySize > 1)
        {
            return true;
        }

        return request.Dungeon?.QueueViaLanParty == true ||
               request.PremadeDuty != null ||
               request.DailyMsq != null ||
               request.Mogtome != null ||
               request.Commendation != null ||
               request.Astrope != null;
    }

    private string ResolveAuthorityEndpoint(bool forceRefresh = false)
    {
        if (forceRefresh)
            characterIntelligenceService.RequestPeerSnapshots();

        // Review H2: use the same preferred endpoint the UI shows (configured target first,
        // discovered peer endpoint as fallback) so start/cancel don't silently fail when peer
        // discovery hasn't populated CurrentTransport.AuthorityEndpoint yet.
        return transportService.GetPreferredAuthorityEndpoint();
    }

    private bool HasTimedOut(TimeSpan timeout)
        => DateTime.UtcNow - phaseChangedAtUtc >= timeout;

    private void Transition(DadRunPhase phase, DadRunStatus status, string summary)
    {
        CurrentResult.Phase = phase;
        CurrentResult.Status = status;
        CurrentResult.Summary = summary;
        CurrentResult.ActiveTaskStatus = summary;
        CurrentResult.BlockedReason = string.Empty;
        CurrentResult.FailureReason = string.Empty;
        CurrentResult.AuthorityWorkerSessionId = presenceService.WorkerSessionId;
        CurrentResult.AuthorityEndpoint = transportService.CurrentTransport.ListenerEndpoint;
        CurrentResult.LocalWorkerSessionId = presenceService.WorkerSessionId;
        CurrentResult.LocalOnlyEnabled = activePlan?.Orchestration.LocalOnlyOverride ?? configuration.LocalOnlyModeEnabled;
        CurrentResult.StopProgress = stopProgress.Clone();
        CurrentResult.Participants = activeParticipants.Select(static participant => participant.Clone()).ToList();
        CurrentResult.Leases = activePlan == null ? [] : claimService.GetLeasesForRun(activePlan.Request.RequestId).ToList();
        phaseChangedAtUtc = DateTime.UtcNow;
        LogCoordinatorPhaseTransition();
        Publish();
    }

    private DadRunResult FinalizeRun(DadRunStatus status, string summary, string failureReason)
    {
        if (activePlan != null && status != DadRunStatus.Cancelled)
        {
            transportService.BroadcastCancel(
                new DadCancelCommandDto
                {
                    RunId = activePlan.Request.RequestId,
                    AuthorityWorkerSessionId = presenceService.WorkerSessionId,
                    CancellationState = DadRunCancellationState.Finalized,
                    Reason = $"Dad run finalized with status {status}.",
                },
                activeParticipants.Where(static participant => !participant.IsLocalClient).ToList());
        }

        if (activePlan != null && status != DadRunStatus.Cancelled)
            claimService.ReleaseClaims(activePlan.Request.RequestId);

        CurrentResult.Status = status;
        CurrentResult.Phase = DadRunPhase.Finalizing;
        CurrentResult.CancellationState = status == DadRunStatus.Cancelled ? DadRunCancellationState.Finalized : CurrentResult.CancellationState;
        CurrentResult.Summary = summary;
        CurrentResult.FailureReason = failureReason;
        CurrentResult.CompletedAtUtc = DateTime.UtcNow;
        CurrentResult.Participants = activeParticipants.Select(static participant => participant.Clone()).ToList();
        CurrentResult.Leases = activePlan == null ? [] : claimService.GetLeasesForRun(activePlan.Request.RequestId).ToList();
        CurrentResult.StepResults = stepResults.Select(static step => step.Clone()).ToList();
        CurrentResult.StopProgress = stopProgress.Clone();
        CurrentResult.ActiveTaskName = string.Empty;
        CurrentResult.ActiveTaskStatus = summary;
        CurrentResult.CompletedTaskCount = stepResults.Count(static step => step.Success);

        configuration.RunHistory ??= [];
        configuration.RunHistory.Insert(0, CurrentResult.Clone());
        if (configuration.RunHistory.Count > 50)
            configuration.RunHistory = configuration.RunHistory.Take(50).ToList();
        configuration.PersistedActiveRun = null;
        configuration.Save();

        // Feature batch A: run operator-chosen completion actions (sound / commands / legacy kill settings).
        if (status == DadRunStatus.Completed)
            DadCompletionActionRunner.Enqueue(configuration, log, activePlan?.Request);

        LogCoordinatorPhaseTransition();

        activePlan = null;
        activeSlotManifest = null;
        activeParticipants.Clear();
        activeModuleIndex = -1;
        activeStepResultIndex = -1;
        workerStatuses.Clear();
        nextWorkerStatusPollUtc = DateTime.MinValue;
        loggedSingleWorkerSeed = false;
        loggedSingleWorkerAssemblyConfirmed = false;
        lastSingleWorkerAssemblyBlocker = string.Empty;
        slotResolutionTransitions.Clear();
        assignmentTransitions.Clear();
        partyTransitions.Clear();
        workerCommandTransitions.Clear();
        remoteAssignmentTracker.Clear();
        partyInviteGateway.Reset();
        presenceService.ResetToIdle();

        log.Information("[dad] Finalized run {RequestId}: {Status} {Summary}", CurrentResult.RequestId, status, summary);
        return PublishAndClone();
    }

    private void LogCoordinatorPhaseTransition()
    {
        if (activePlan == null || lastLoggedCoordinatorPhase == CurrentResult.Phase)
            return;

        lastLoggedCoordinatorPhase = CurrentResult.Phase;
        if (activeParticipants.Count == 0)
        {
            log.Information(
                "[dad] Coordinator phase transition request={RequestId} module={ModuleId} slot=(none) account=(none) character=(none) contentId=0 worker=(none) phase={Phase} summary={Summary}.",
                activePlan.Request.RequestId,
                activePlan.CompositeModuleId,
                CurrentResult.Phase,
                CurrentResult.Summary);
            return;
        }

        foreach (var participant in activeParticipants)
        {
            var slot = activeSlotManifest?.Slots.FirstOrDefault(candidate =>
                string.Equals(candidate.SlotId, participant.AssignedSlotId, StringComparison.OrdinalIgnoreCase));
            log.Information(
                "[dad] Coordinator phase transition request={RequestId} module={ModuleId} slot={SlotId} account={AccountKey} character={CharacterKey} contentId={ContentId} worker={WorkerSessionId} phase={Phase} summary={Summary}.",
                activePlan.Request.RequestId,
                activePlan.CompositeModuleId,
                participant.AssignedSlotId,
                slot?.AccountKey ?? participant.ManagedAccountKey,
                slot?.CharacterKey ?? participant.ActiveCharacterKey,
                slot?.ContentId ?? participant.Character.ContentId,
                slot?.WorkerSessionId ?? participant.WorkerSessionId,
                CurrentResult.Phase,
                CurrentResult.Summary);
        }
    }

    private void RecoverAbandonedRun()
    {
        configuration.RunHistory ??= [];
        var abandoned = configuration.PersistedActiveRun;
        if (abandoned == null || abandoned.IsTerminal)
        {
            configuration.PersistedActiveRun = null;
            return;
        }

        var recovered = abandoned.Clone();
        recovered.Status = DadRunStatus.Failed;
        recovered.Phase = DadRunPhase.Finalizing;
        recovered.Summary = "Run abandoned by plugin reload; explicit restart required.";
        recovered.FailureReason = recovered.Summary;
        recovered.CompletedAtUtc = DateTime.UtcNow;
        recovered.Leases = [];
        recovered.CurrentExecutorStatus = new DadModuleExecutionStatusDto();
        configuration.RunHistory.Insert(0, recovered);
        if (configuration.RunHistory.Count > 50)
            configuration.RunHistory = configuration.RunHistory.Take(50).ToList();
        configuration.PersistedActiveRun = null;
        configuration.Save();
        CurrentResult = recovered;
    }

    private bool TryApplyRemoteParticipantResponse(
        DadParticipantSnapshot target,
        DadParticipantSnapshot source,
        out string blocker)
    {
        blocker = string.Empty;
        if (activePlan == null || activeSlotManifest == null)
        {
            blocker = "Remote participant response arrived without an active frozen run manifest.";
            return false;
        }

        var frozenSlot = activeSlotManifest.Slots.SingleOrDefault(slot =>
            string.Equals(slot.SlotId, target.AssignedSlotId, StringComparison.OrdinalIgnoreCase));
        if (frozenSlot == null)
        {
            blocker = $"Remote participant response has no frozen slot for '{target.AssignedSlotId}'.";
            return false;
        }

        return DadRemoteParticipantMutationRules.TryApplyIdentityValidRuntimeState(
            target,
            source,
            frozenSlot,
            activePlan.Request.RequestId,
            out blocker);
    }

    private static void CopyLocalParticipant(DadParticipantSnapshot target, DadParticipantSnapshot source)
    {
        target.ClientInstanceId = source.ClientInstanceId;
        target.WorkerSessionId = source.WorkerSessionId;
        target.MachineName = source.MachineName;
        target.ProcessId = source.ProcessId;
        target.Endpoint = source.Endpoint;
        target.RunId = source.RunId;
        target.AuthorityMode = source.AuthorityMode;
        target.Role = source.Role;
        target.WorkerRole = source.WorkerRole;
        target.State = source.State;
        target.ClaimState = source.ClaimState;
        target.LeaseState = source.LeaseState;
        target.CancellationState = source.CancellationState;
        target.IsLocalClient = source.IsLocalClient;
        target.IsAuthority = source.IsAuthority;
        target.IsAvailable = source.IsAvailable;
        target.IsEligibleForRun = source.IsEligibleForRun;
        target.PostArReady = source.PostArReady;
        target.LastHeartbeatUtc = source.LastHeartbeatUtc;
        target.ManagedAccountKey = source.ManagedAccountKey;
        target.ManagedAccountAlias = source.ManagedAccountAlias;
        target.ActiveCharacterKey = source.ActiveCharacterKey;
        target.AvailableCharacterKeys = [..source.AvailableCharacterKeys];
        target.Character = source.Character.Clone();
        target.AssignedSlotId = string.IsNullOrWhiteSpace(target.AssignedSlotId) ? source.AssignedSlotId : target.AssignedSlotId;
        target.DesiredCharacterKey = source.DesiredCharacterKey;
        target.RequestedJobPreparation = source.RequestedJobPreparation?.Clone();
        target.LeaseIssuedUtc = source.LeaseIssuedUtc;
        target.LeaseRenewedUtc = source.LeaseRenewedUtc;
        target.LeaseExpiresUtc = source.LeaseExpiresUtc;
        target.Warnings = [..source.Warnings];
        target.StatusText = source.StatusText;
    }

    private void Publish()
    {
        StatusChanged?.Invoke(BuildPublishedResult());
    }

    private DadRunResult PublishAndClone()
    {
        var result = BuildPublishedResult();
        StatusChanged?.Invoke(result.Clone());
        return result;
    }

    private DadRunResult BuildPublishedResult()
    {
        var result = CurrentResult.Clone();
        ApplyLocalPerspective(result);
        return result;
    }

    private void ApplyLocalPerspective(DadRunResult result)
    {
        var localParticipant = presenceService.BuildSnapshotCopy();
        result.LocalClientInstanceId = presenceService.ClientInstanceId;
        result.LocalWorkerSessionId = presenceService.WorkerSessionId;
        result.Role = localParticipant.Role;
        result.WorkerRole = localParticipant.WorkerRole;
        result.LocalOnlyEnabled = result.LocalOnlyEnabled || configuration.LocalOnlyModeEnabled;
        if (configuration.LocalOnlyModeEnabled && result.Status == DadRunStatus.Idle)
        {
            result.AuthorityMode = DadAuthorityMode.LocalOnly;
            result.TransportMode = DadTransportMode.LocalOnly;
        }
    }
}
