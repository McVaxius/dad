using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using dad.Models;

namespace dad.Services;

public sealed class DadCoordinatorService
{
    private static readonly TimeSpan ParticipantPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WorkerStatusPollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan CancellationRetryInterval = TimeSpan.FromSeconds(2);

    private readonly Configuration configuration;
    private readonly ConfigManager configManager;
    private readonly DadCharacterIntelligenceService characterIntelligenceService;
    private readonly DadPresenceService presenceService;
    private readonly DadTransportService transportService;
    private readonly DadClaimService claimService;
    private readonly DadPartyAssemblyService partyAssemblyService;
    private readonly InfoProxyPartyInviteGateway partyInviteGateway;
    private readonly DadPartyTeardownService partyTeardownService;
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
    private readonly Dictionary<string, DadWorkerExecutionCommand> workerCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> missingWorkerSinceUtc = new(StringComparer.OrdinalIgnoreCase);
    private List<DadParticipantSnapshot>? finalizationCancellationScopeOverride;
    private readonly Dictionary<string, string> slotResolutionTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> assignmentTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> partyTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> workerCommandTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> coordinatorProvenanceTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, DateTime> firstPartyInviteAttemptUtcByContentId = [];
    private readonly DadRemoteAssignmentTracker remoteAssignmentTracker = new();
    private DateTime nextWorkerStatusPollUtc = DateTime.MinValue;
    private DadRunPhase? lastLoggedCoordinatorPhase;
    private string firstPartyInviteBoundaryRunId = string.Empty;
    private string inviteRetryContinuationRunId = string.Empty;
    private bool persistentStartup;
    private DadScheduleRepeatBoundary activeScheduleRepeatBoundary = DadScheduleRepeatBoundary.Standalone;
    private readonly DadStableContradictionTracker coordinatorContradictionTracker = new();
    private readonly Dictionary<string, PendingCoordinatorCancellation> pendingCoordinatorCancellations = new(StringComparer.OrdinalIgnoreCase);

    private sealed class PendingCoordinatorCancellation
    {
        public DadParticipantSnapshot Target { get; init; } = new();
        public DadCancelCommandDto RunCommand { get; init; } = new();
        public DadWorkerExecutionCancel WorkerCommand { get; init; } = new();
        public bool RunAcknowledged { get; set; }
        public bool WorkerAcknowledged { get; set; }
        public DateTime NextAttemptUtc { get; set; } = DateTime.MinValue;
        public string LastDiagnosticState { get; set; } = string.Empty;
    }

    internal DadCoordinatorService(
        Configuration configuration,
        ConfigManager configManager,
        DadCharacterIntelligenceService characterIntelligenceService,
        DadPresenceService presenceService,
        DadTransportService transportService,
        DadClaimService claimService,
        DadPartyAssemblyService partyAssemblyService,
        InfoProxyPartyInviteGateway partyInviteGateway,
        DadPartyTeardownService partyTeardownService,
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
        this.partyTeardownService = partyTeardownService;
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
        UpdatePendingCoordinatorCancellations();

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
            case DadRunPhase.GroupReady:
                // Formation-only AutoParty runs deliberately hold until a local veto,
                // owner Stop, disable, expiry, or revocation cancels the run.
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
            case DadRunPhase.TearingDownParty:
                UpdatePartyTeardown();
                break;
            case DadRunPhase.Finalizing:
                CompleteRun();
                break;
        }
    }

    public DadRunResult GetLocalResult()
        => BuildPublishedResult();

    public bool IsActiveAutoPartyProposal(Guid proposalId)
        => proposalId != Guid.Empty &&
           activePlan != null &&
           Guid.TryParse(activePlan.Request.Orchestration.AutoPartyProposalId, out var activeProposalId) &&
           activeProposalId == proposalId;

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

    public DadRunResult StartTasks(DadRunRequest request, bool persistentStartup = false)
        => StartTasksCore(request, persistentStartup, DadScheduleRepeatBoundary.Standalone);

    internal DadRunResult StartScheduledTasks(
        DadRunRequest request,
        DadScheduleRepeatBoundary repeatBoundary)
        => StartTasksCore(request, persistentStartup: true, repeatBoundary);

    private DadRunResult StartTasksCore(
        DadRunRequest request,
        bool persistentStartup,
        DadScheduleRepeatBoundary repeatBoundary)
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

        if (!presenceService.BuildSnapshotCopy().Dependencies.IsReady)
            return DadRunResult.Rejected(request, DadDependencyRules.DependencyBlocker);

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
        var liveCoordinatorTruth = presenceService.BuildLiveSafetySnapshot();
        LogCoordinatorProvenance("run-start", request.RequestId, liveCoordinatorTruth);
        var plan = plannerService.BuildPlan(
            request,
            pool,
            out var rejectionReason,
            liveLocalRuntimeTruth: liveCoordinatorTruth);
        if (plan == null)
            return DadRunResult.Rejected(request, rejectionReason);

        DadRunSlotManifest? acceptedManifest = null;
        if (DadRunSlotManifestRules.RequiresFrozenRoster(plan))
        {
            if (!DadRunSlotManifestRules.TryCreate(plan, out var unboundManifest, out rejectionReason))
                return DadRunResult.Rejected(request, rejectionReason);

            var onlineParticipants = BuildOnlineParticipantSet(pool, liveCoordinatorTruth);
            if (!DadRunSlotManifestRules.TryBindWorkerSessions(
                    unboundManifest,
                    onlineParticipants,
                    out acceptedManifest,
                    out rejectionReason))
            {
                return DadRunResult.Rejected(request, rejectionReason);
            }

            if (plan.RequiredParticipantCount > 1 || plan.RequiresRemoteParticipants)
            {
                if (!DadCoordinatorTravelRules.TryFreezeTarget(
                        plan.Request.RequestId,
                        liveCoordinatorTruth,
                        DateTime.UtcNow,
                        out var travelTarget,
                        out rejectionReason))
                {
                    return DadRunResult.Rejected(request, rejectionReason);
                }

                var coordinatorSlots = acceptedManifest.Slots
                    .Where(slot => string.Equals(
                        slot.WorkerSessionId.Value,
                        travelTarget.CoordinatorWorkerSessionId.Value,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (coordinatorSlots.Count != 1 ||
                    !DadRosterIdentity.SameAccount(coordinatorSlots[0].AccountKey, travelTarget.CoordinatorAccountKey) ||
                    !string.Equals(
                        coordinatorSlots[0].CharacterKey.Value,
                        travelTarget.CoordinatorCharacterKey.Value,
                        StringComparison.OrdinalIgnoreCase) ||
                    coordinatorSlots[0].ContentId != travelTarget.CoordinatorContentId)
                {
                    return DadRunResult.Rejected(
                        request,
                        "Frozen Coordinator travel target does not match exactly one bound roster slot identity.");
                }

                acceptedManifest.CoordinatorTravelTarget = travelTarget;
            }
        }

        var selectedDependencyParticipants = new List<DadParticipantSnapshot?>();
        if (acceptedManifest != null)
        {
            var currentParticipants = BuildOnlineParticipantSet(pool, liveCoordinatorTruth);
            foreach (var workerSessionId in acceptedManifest.Slots
                         .Select(static slot => slot.WorkerSessionId)
                         .Where(workerSessionId => !workerSessionId.IsEmpty &&
                             !string.Equals(
                                 workerSessionId.Value,
                                 liveCoordinatorTruth.WorkerSessionId.Value,
                                 StringComparison.OrdinalIgnoreCase))
                         .DistinctBy(static workerSessionId => workerSessionId.Value, StringComparer.OrdinalIgnoreCase))
            {
                selectedDependencyParticipants.Add(currentParticipants.FirstOrDefault(participant =>
                    string.Equals(
                        participant.WorkerSessionId.Value,
                        workerSessionId.Value,
                        StringComparison.OrdinalIgnoreCase)));
            }
        }

        var dependencyGate = DadDependencyGateRules.EvaluateCrew(
            liveCoordinatorTruth,
            selectedDependencyParticipants,
            DateTime.UtcNow,
            TimeSpan.FromSeconds(Math.Max(3, configuration.HeartbeatStaleSeconds)));
        if (!dependencyGate.Ready)
        {
            log.Information("[dad][Dependencies] Rejected new run {RequestId}: {Summary}", request.RequestId, dependencyGate.Summary);
            return DadRunResult.Rejected(request, DadDependencyRules.DependencyBlocker);
        }

        activePlan = plan;
        this.persistentStartup = persistentStartup;
        activeScheduleRepeatBoundary = repeatBoundary;
        coordinatorContradictionTracker.Reset();
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
        workerCommands.Clear();
        missingWorkerSinceUtc.Clear();
        finalizationCancellationScopeOverride = null;
        partyInviteGateway.Reset();
        partyTeardownService.Reset();
        slotResolutionTransitions.Clear();
        assignmentTransitions.Clear();
        partyTransitions.Clear();
        workerCommandTransitions.Clear();
        coordinatorProvenanceTransitions.Clear();
        firstPartyInviteAttemptUtcByContentId.Clear();
        firstPartyInviteBoundaryRunId = string.Empty;
        inviteRetryContinuationRunId = string.Empty;
        remoteAssignmentTracker.BeginAttempt(plan.Request.RequestId);
        lastLoggedCoordinatorPhase = null;
        claimService.ReleaseClaims(plan.Request.RequestId);
        presenceService.MarkLeader(plan.Request.RequestId, plan.Orchestration.AuthorityMode, $"Dad Coordinator planned {plan.Modules.Count} Dad module(s).");
        if (!TryBeginLocalRequestedJobPreparation(plan, acceptedManifest, out var preparationBlocker))
        {
            activePlan = null;
            activeSlotManifest = null;
            activeScheduleRepeatBoundary = DadScheduleRepeatBoundary.Standalone;
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
        QueueCoordinatorCancellations(command, "Cancelled by Dad Coordinator.");
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

        workerExecutionService.Cancel(new DadWorkerExecutionCancel
        {
            RunId = runId,
            Reason = reason,
        });

        QueueCoordinatorCancellations(
            new DadCancelCommandDto
            {
                RunId = runId,
                AuthorityWorkerSessionId = presenceService.WorkerSessionId,
                CancellationState = DadRunCancellationState.Cancelling,
                Reason = reason,
            },
            reason);

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
        var liveCoordinatorTruth = presenceService.BuildLiveSafetySnapshot();
        var runtimeParticipants = BuildCurrentManifestParticipantSet(pool, liveCoordinatorTruth);
        var contradictionProof = BuildManifestContradictionEvidence(
            activeSlotManifest,
            runtimeParticipants,
            liveCoordinatorTruth);
        var contradiction = coordinatorContradictionTracker.Observe(
            contradictionProof.Evidence,
            contradictionProof.WorldStable,
            DateTime.UtcNow,
            ParticipantPollInterval,
            contradictionProof.ObservedAtUtc);
        if (contradiction.Disposition == DadSafetyProofDisposition.Reject)
        {
            log.Error(
                "[dad] Confirmed participant-discovery contradiction request={RequestId} evidence={Evidence} firstObserved={FirstObservedAtUtc}.",
                activePlan.Request.RequestId,
                contradiction.Evidence,
                contradiction.FirstObservedAtUtc?.ToString("O") ?? "(unknown)");
            FinalizeRun(
                DadRunStatus.Failed,
                "Dad rejected participant discovery after two fresh world-stable identity contradictions.",
                contradiction.Evidence);
            return;
        }

        if (contradiction.Disposition == DadSafetyProofDisposition.Wait)
        {
            CurrentResult.Phase = DadRunPhase.WaitingForReadiness;
            CurrentResult.Status = DadRunStatus.WaitingForParticipants;
            CurrentResult.ActiveTaskStatus = contradiction.Summary;
            CurrentResult.BlockedReason = contradiction.Summary;
            CurrentResult.Participants = [];
            Publish();
            return;
        }

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
                CoordinatorTravelTarget = activeSlotManifest.CoordinatorTravelTarget?.Clone(),
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

        if (activeSlotManifest.CoordinatorTravelTarget != null)
        {
            var travelProof = DadCoordinatorTravelRules.ValidateParticipants(
                activeSlotManifest.CoordinatorTravelTarget,
                activeParticipants,
                DateTime.UtcNow);
            if (travelProof.ImmutableTargetChanged)
            {
                FinalizeRun(
                    DadRunStatus.Failed,
                    "Dad rejected participant discovery because the immutable Coordinator travel target changed.",
                    travelProof.Summary);
                return;
            }

            if (!travelProof.Ready)
                blockers.Add(travelProof.Summary);
        }

        blockers.AddRange(activeParticipants
            .Where(static participant => participant.State is DadParticipantState.WaitingForRequiredCharacter or DadParticipantState.WaitingForPostArReady or DadParticipantState.Stale)
            .Select(static participant => string.IsNullOrWhiteSpace(participant.StatusText) ? participant.State.ToString() : participant.StatusText));

        CurrentResult.Participants = activeParticipants.Select(static participant => participant.Clone()).ToList();
        if (blockers.Count > 0)
        {
            if (!persistentStartup && HasTimedOut(activePlan.Orchestration.WaitPolicy.GetParticipantReadyTimeout()))
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

        if (!TryRefreshStrictMutationBoundary("claim issuance"))
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
                decision = claimService.TryClaimLocal(request, presenceService.BuildLiveSafetySnapshot());
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
            if (!persistentStartup && HasTimedOut(activePlan.Orchestration.WaitPolicy.GetParticipantReadyTimeout()))
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

        if (!TryRefreshStrictMutationBoundary("party assembly and invite dispatch"))
            return;

        if (TryResolveSingleWorkerAssembly(activePlan))
            return;

        var instructions = partyAssemblyService.BuildInstructions(activePlan, activeParticipants, out var blocker);
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            if (!persistentStartup && HasTimedOut(activePlan.Orchestration.WaitPolicy.GetAssemblyTimeout()))
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
            {
                blockers.Add(result.BlockedReason);
                if (DadPartyInvitationRetryRules.IsPersistentWarning(result.BlockedReason) &&
                    !participant.Warnings.Any(existing => string.Equals(existing, result.BlockedReason, StringComparison.Ordinal)))
                {
                    participant.Warnings.Add(result.BlockedReason);
                }
            }
            LogPartyTransition(activePlan, participant, "join-dispatched", result.Summary);
        }

        CurrentResult.Participants = activeParticipants.Select(static participant => participant.Clone()).ToList();
        if (blockers.Count > 0)
        {
            if (blockers.Any(DadPartyInvitationRetryRules.IsContinuingRetry))
            {
                inviteRetryContinuationRunId = activePlan.Request.RequestId;
                PromoteInviteAttemptWarnings(activePlan);
            }
            PromotePersistentPartyInviteWarnings();

            if (DadPartyInvitationRetryRules.ShouldApplyAssemblyTimeout(
                    persistentStartup,
                    HasTimedOut(activePlan.Orchestration.WaitPolicy.GetAssemblyTimeout()),
                    blockers))
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
        var membership = partyAssemblyService.EvaluatePartyMembership(activePlan, activeParticipants, partyMembers);
        if (membership.Disposition == DadPartyMembershipDisposition.Reject)
        {
            FinalizeRun(
                DadRunStatus.Failed,
                "Dad rejected party assembly because PartyList contradicted the frozen manifest.",
                membership.Summary);
            return;
        }

        if (!string.IsNullOrWhiteSpace(partySnapshotBlocker) || membership.Disposition != DadPartyMembershipDisposition.Ready)
        {
            var blockerSummary = string.IsNullOrWhiteSpace(partySnapshotBlocker) ? membership.Summary : partySnapshotBlocker;
            var inviteRetryContinues = string.Equals(
                inviteRetryContinuationRunId,
                activePlan.Request.RequestId,
                StringComparison.Ordinal);
            if (inviteRetryContinues)
                PromoteInviteAttemptWarnings(activePlan);
            PromotePersistentPartyInviteWarnings();
            if (!inviteRetryContinues &&
                !persistentStartup &&
                HasTimedOut(activePlan.Orchestration.WaitPolicy.GetAssemblyTimeout()))
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

        inviteRetryContinuationRunId = string.Empty;
        TransitionAfterAssembly(activePlan, "Dad party assembly confirmed; preparing queue executor.");
    }

    private void PromotePersistentPartyInviteWarnings()
    {
        foreach (var warning in activeParticipants
                     .SelectMany(static participant => participant.Warnings)
                     .Where(DadPartyInvitationRetryRules.IsPersistentWarning))
        {
            if (CurrentResult.Warnings.Any(DadPartyInvitationRetryRules.IsPersistentWarning))
                continue;

            CurrentResult.Warnings.Add(warning);
            log.Warning("[dad] {Warning}", warning);
        }
    }

    private DadParticipantSnapshot? ResolveParticipantForInstruction(DadAssemblyInstructionDto instruction)
        => activeParticipants.FirstOrDefault(candidate =>
            string.Equals(candidate.ActiveCharacterKey.Value, instruction.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<DadPartyMemberSnapshot> BuildLocalPartySnapshot(out string blocker)
    {
        blocker = string.Empty;
        var members = new List<DadPartyMemberSnapshot>();
        var sourceName = "party state";

        try
        {
            var crossRealmPartyActive = InfoProxyCrossRealm.IsCrossRealmParty();
            sourceName = crossRealmPartyActive ? "InfoProxyCrossRealm" : "PartyList";
            members.AddRange(DadPartySnapshotSourceRules.Read(
                crossRealmPartyActive,
                ReadPartyListSnapshot,
                ReadCrossRealmPartySnapshot));
        }
        catch (Exception ex)
        {
            blocker = $"Unable to read local {sourceName} for Dad assembly verification: {ex.Message}";
            return [];
        }

        var local = presenceService.BuildLiveSafetySnapshot();
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

    private static IReadOnlyList<DadPartyMemberSnapshot> ReadPartyListSnapshot()
    {
        var members = new List<DadPartyMemberSnapshot>();
        foreach (var member in Plugin.PartyList)
        {
            members.Add(new DadPartyMemberSnapshot
            {
                CharacterKey = new DadCharacterKey(string.Empty),
                ContentId = member.ContentId,
                CharacterName = member.Name.ToString(),
                IsLocalPlayer = member.ContentId != 0 && member.ContentId == Plugin.PlayerState.ContentId,
            });
        }

        return members;
    }

    private static unsafe IReadOnlyList<DadPartyMemberSnapshot> ReadCrossRealmPartySnapshot()
    {
        var members = new List<DadPartyMemberSnapshot>();
        var memberCount = InfoProxyCrossRealm.GetPartyMemberCount();
        for (uint memberIndex = 0; memberIndex < memberCount; memberIndex++)
        {
            var member = InfoProxyCrossRealm.GetGroupMember(memberIndex);
            if (member == null)
                continue;

            members.Add(new DadPartyMemberSnapshot
            {
                CharacterKey = new DadCharacterKey(string.Empty),
                ContentId = member->ContentId,
                CharacterName = member->NameString,
                IsLocalPlayer = member->ContentId != 0 && member->ContentId == Plugin.PlayerState.ContentId,
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

        LogFirstPartyInviteBoundary(plan, instructions, partyMembers);

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

            firstPartyInviteAttemptUtcByContentId.TryAdd(frozenSlot.ContentId, attempt.AttemptedAtUtc);

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

    private void PromoteInviteAttemptWarnings(DadRunPlan plan)
    {
        if (CurrentResult.Warnings.Any(DadPartyInvitationRetryRules.IsPersistentWarning))
            return;

        var participant = activeParticipants.FirstOrDefault(candidate =>
            candidate.Character.ContentId != 0 &&
            firstPartyInviteAttemptUtcByContentId.TryGetValue(candidate.Character.ContentId, out var firstAttemptUtc) &&
            DateTime.UtcNow - firstAttemptUtc >= plan.Orchestration.WaitPolicy.GetAssemblyTimeout());
        if (participant == null)
        {
            return;
        }

        var warning = DadPartyInvitationRetryRules.BuildWarning(
            participant.ActiveCharacterKey,
            plan.InviterCharacterKey);
        if (!participant.Warnings.Any(existing => string.Equals(existing, warning, StringComparison.Ordinal)))
            participant.Warnings.Add(warning);

        CurrentResult.Warnings.Add(warning);
        log.Warning("[dad] {Warning}", warning);
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
            workerCommands.Count > 0)
        {
            if (workerStatuses.Count < workerCommands.Count)
                DispatchWorkerExecution(activePlan.Modules[activeModuleIndex]);
            else
                UpdateWorkerExecution(activePlan.Modules[activeModuleIndex]);
            return;
        }

        if (activeModuleIndex + 1 < activePlan.Modules.Count &&
            !TryRefreshStrictMutationBoundary("queue worker dispatch"))
        {
            return;
        }

        activeModuleIndex++;
        if (activeModuleIndex >= activePlan.Modules.Count)
        {
            FinishSuccessfulWork("Dad module routing complete.");
            return;
        }

        foreach (var participant in activeParticipants)
            participant.State = DadParticipantState.QueuePending;

        var module = activePlan.Modules[activeModuleIndex];
        workerCommands.Clear();
        workerStatuses.Clear();
        missingWorkerSinceUtc.Clear();
        finalizationCancellationScopeOverride = null;
        activeStepResultIndex = -1;
        nextWorkerStatusPollUtc = DateTime.MinValue;
        MarkStopPolicyAttemptStarted(activePlan);
        DispatchWorkerExecution(module);
    }

    private void DispatchWorkerExecution(DadPlannedModuleExecution module)
    {
        if (activePlan == null)
            return;

        var barrierRequired = DadWorkerPrequeueBarrierRules.IsRequired(
            activePlan,
            module,
            activeParticipants);
        var failures = new List<string>();
        var pending = new List<string>();
        if (!DadWorkerPrequeueBarrierRules.TryResolveDispatchTargets(
                activePlan,
                module,
                activeParticipants,
                workerStatuses,
                out var dispatchTargets,
                out var barrierBlocker))
        {
            ScopeFinalizationToAcknowledgedWorkers(barrierRequired);
            ApplyModuleRoutingResult(
                module,
                BuildWorkerFailureResult(module, barrierBlocker),
                replaceExisting: activeStepResultIndex >= 0);
            return;
        }

        foreach (var participant in dispatchTargets)
        {
            var role = IsQueueLeaderParticipant(activePlan, participant)
                ? DadWorkerExecutionRole.QueueLeader
                : DadWorkerExecutionRole.Participant;
            var participantView = BuildWorkerParticipantView(participant, role);
            if (!workerCommands.TryGetValue(participant.WorkerSessionId.Value, out var command))
            {
                command = new DadWorkerExecutionCommand
                {
                    SchemaVersion = DadWorkerCommandSchemaRules.ResolveEmissionSchema(
                        activePlan.Request.PreDutyRepairPolicy),
                    CommandId = $"{activePlan.Request.RequestId}:{activeModuleIndex}:{participant.AssignedSlotId}:worker-execution",
                    RunId = activePlan.Request.RequestId,
                    ModuleIndex = activeModuleIndex,
                    Role = role,
                    Plan = DadIpcJson.Deserialize<DadRunPlan>(DadIpcJson.Serialize(activePlan)) ?? activePlan,
                    Participants = participantView.Select(static candidate => candidate.Clone()).ToList(),
                    TimeoutSeconds = persistentStartup
                        ? 0
                        : Math.Max(
                            60,
                            activePlan.Orchestration.WaitPolicy.ParticipantReadyTimeoutSeconds +
                            activePlan.Orchestration.WaitPolicy.AssemblyTimeoutSeconds +
                            900),
                };
                workerCommands[participant.WorkerSessionId.Value] = command;
            }

            if (workerStatuses.ContainsKey(participant.WorkerSessionId.Value))
                continue;

            participant.RunId = activePlan.Request.RequestId;
            var targetRuntime = participant.Clone();
            targetRuntime.IsLocalClient = true;
            if (!DadWorkerCommandValidationRules.TryValidate(command, targetRuntime, out _, out var validationBlocker))
            {
                failures.Add(DadWorkerPrequeueBarrierRules.AttributeFailure(
                    participant,
                    $"worker command rejected before dispatch: {validationBlocker}"));
                continue;
            }

            DadWorkerExecutionAck? ack = participant.IsLocalClient
                ? workerExecutionService.Accept(command)
                : transportService.SendWorkerExecutionCommand(participant, command);
            if (ack == null)
            {
                var workerKey = participant.WorkerSessionId.Value;
                missingWorkerSinceUtc.TryAdd(workerKey, DateTime.UtcNow);
                var decision = DadDroppedPeerContinuationRules.EvaluateMissingPeer(
                    participant,
                    command,
                    cachedStatus: null,
                    leaderCommand: null,
                    leaderStatus: null,
                    missingSinceUtc: missingWorkerSinceUtc[workerKey],
                    nowUtc: DateTime.UtcNow,
                    participantReadyTimeout: activePlan.Orchestration.WaitPolicy.GetParticipantReadyTimeout());
                var summary = DadWorkerPrequeueBarrierRules.AttributeFailure(
                    participant,
                    $"Worker command acknowledgement pending. {decision.Summary}");
                if (decision.Action == DadDroppedPeerContinuationAction.Fail)
                {
                    CurrentResult.ScheduleFailureKind = decision.FailureKind;
                    failures.Add(summary);
                    LogWorkerCommandTransition(activePlan, module, participant, "acknowledgement-timeout", summary);
                }
                else
                {
                    pending.Add(summary);
                    LogWorkerCommandTransition(activePlan, module, participant, "acknowledgement-pending", summary);
                }
                continue;
            }

            missingWorkerSinceUtc.Remove(participant.WorkerSessionId.Value);
            if (!ack.Accepted)
            {
                var failure = ack.Summary;
                var attributedFailure = DadWorkerPrequeueBarrierRules.AttributeFailure(participant, failure);
                if (!persistentStartup ||
                    role == DadWorkerExecutionRole.QueueLeader && barrierRequired ||
                    failure.Contains("immutable", StringComparison.OrdinalIgnoreCase) ||
                    failure.Contains("collision", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(attributedFailure);
                }
                else
                    pending.Add(attributedFailure);
                LogWorkerCommandTransition(activePlan, module, participant, "rejected-or-missing", attributedFailure);
                continue;
            }

            if (!DadWorkerStatusPollingRules.MatchesExactAcknowledgement(participant, command, ack))
            {
                CurrentResult.ScheduleFailureKind = command.Role == DadWorkerExecutionRole.QueueLeader || participant.IsAuthority
                    ? DadScheduleFailureKind.MissingOrUnknownLeaderState
                    : DadScheduleFailureKind.EntryTerminalFailure;
                var failure = DadWorkerPrequeueBarrierRules.AttributeFailure(
                    participant,
                    "Worker returned contradictory run/module/command/role/identity acknowledgement.");
                failures.Add(failure);
                LogWorkerCommandTransition(activePlan, module, participant, "contradictory-acknowledgement", failure);
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
            ScopeFinalizationToAcknowledgedWorkers(barrierRequired);
            ApplyModuleRoutingResult(module, BuildWorkerFailureResult(module, summary), replaceExisting: false);
            return;
        }

        if (pending.Count > 0 || workerStatuses.Count < workerCommands.Count)
        {
            var summary = string.Join(" | ", pending.Distinct(StringComparer.OrdinalIgnoreCase));
            ApplyModuleRoutingResult(
                module,
                BuildWorkerProgressResult(
                    module,
                    $"Worker command acknowledgements {workerStatuses.Count}/{workerCommands.Count}; retrying without timeout. {summary}".Trim()),
                replaceExisting: activeStepResultIndex >= 0);
            return;
        }

        ApplyModuleRoutingResult(
            module,
            BuildWorkerProgressResult(
                module,
                barrierRequired && !workerStatuses.Values.Any(static status => status.Role == DadWorkerExecutionRole.QueueLeader)
                    ? $"ADS prequeue barrier dispatched {workerStatuses.Count}/{Math.Max(1, activeParticipants.Count - 1)} non-leader worker(s); waiting for every worker to reach WaitingForQueue."
                    : $"Assigned {workerStatuses.Count} worker(s); waiting for execution status."),
            replaceExisting: activeStepResultIndex >= 0);
    }

    private static bool IsQueueLeaderParticipant(DadRunPlan plan, DadParticipantSnapshot participant)
        => DadWorkerPrequeueBarrierRules.IsLeader(plan, participant);

    private void ScopeFinalizationToAcknowledgedWorkers(bool barrierRequired)
    {
        if (!barrierRequired)
            return;

        finalizationCancellationScopeOverride = DadWorkerPrequeueBarrierRules.ResolveCancellationScope(
            activeParticipants,
            workerStatuses.Keys.ToList());
    }

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
        var barrierRequired = DadWorkerPrequeueBarrierRules.IsRequired(
            activePlan,
            module,
            activeParticipants);
        var failures = new List<string>();
        var dispatchedParticipants = activeParticipants
            .Where(participant => workerCommands.ContainsKey(participant.WorkerSessionId.Value))
            .ToList();
        var queueLeaders = dispatchedParticipants.Where(participant =>
            IsQueueLeaderParticipant(activePlan, participant)).ToList();
        var queueLeader = queueLeaders.Count == 1 ? queueLeaders[0] : null;
        foreach (var participant in dispatchedParticipants)
        {
            var workerKey = participant.WorkerSessionId.Value;
            if (!workerCommands.TryGetValue(workerKey, out var exactCommand))
            {
                CurrentResult.ScheduleFailureKind = DadScheduleFailureKind.EntryTerminalFailure;
                failures.Add(DadWorkerPrequeueBarrierRules.AttributeFailure(
                    participant,
                    "Worker status has no exact cached command provenance."));
                continue;
            }

            workerStatuses.TryGetValue(workerKey, out var freshestCachedStatus);
            DadWorkerExecutionStatus? workerStatus = participant.IsLocalClient
                ? workerExecutionService.GetStatus()
                : transportService.GetWorkerExecutionStatus(participant, exactCommand, freshestCachedStatus);
            if (workerStatus == null)
            {
                missingWorkerSinceUtc.TryAdd(workerKey, DateTime.UtcNow);
                var cachedStatus = freshestCachedStatus;
                if (cachedStatus is { IsTerminal: true, Success: true } &&
                    workerCommands.TryGetValue(workerKey, out var completedCommand) &&
                    DadDroppedPeerContinuationRules.MatchesExactCommand(participant, completedCommand, cachedStatus))
                {
                    participant.State = DadParticipantState.Completed;
                    participant.StatusText = cachedStatus.Summary;
                    continue;
                }

                DadWorkerExecutionCommand? leaderCommand = null;
                DadWorkerExecutionStatus? cachedLeaderStatus = null;
                if (queueLeader != null)
                {
                    workerCommands.TryGetValue(queueLeader.WorkerSessionId.Value, out leaderCommand);
                    workerStatuses.TryGetValue(queueLeader.WorkerSessionId.Value, out cachedLeaderStatus);
                }
                var decision = DadDroppedPeerContinuationRules.EvaluateMissingPeer(
                    participant,
                    exactCommand,
                    cachedStatus,
                    leaderCommand,
                    cachedLeaderStatus,
                    missingWorkerSinceUtc[workerKey],
                    DateTime.UtcNow,
                    activePlan.Orchestration.WaitPolicy.GetParticipantReadyTimeout());
                if (decision.Action == DadDroppedPeerContinuationAction.SatisfyParticipant && cachedStatus != null)
                {
                    var satisfied = cachedStatus.Clone();
                    satisfied.State = DadWorkerExecutionState.Completed;
                    satisfied.IsTerminal = true;
                    satisfied.Success = true;
                    satisfied.Summary = decision.Summary;
                    satisfied.FailureReason = string.Empty;
                    satisfied.UpdatedAtUtc = DateTime.UtcNow;
                    workerStatuses[workerKey] = satisfied;
                    participant.State = DadParticipantState.Completed;
                    participant.StatusText = decision.Summary;
                    LogWorkerCommandTransition(
                        activePlan,
                        module,
                        participant,
                        "dropped-peer-internally-satisfied",
                        decision.Summary);
                }
                else if (decision.Action == DadDroppedPeerContinuationAction.Fail)
                {
                    CurrentResult.ScheduleFailureKind = decision.FailureKind;
                    failures.Add(DadWorkerPrequeueBarrierRules.AttributeFailure(participant, decision.Summary));
                }
                else
                {
                    LogWorkerCommandTransition(
                        activePlan,
                        module,
                        participant,
                        "status-pending",
                        decision.Summary);
                }
                continue;
            }

            if (!DadDroppedPeerContinuationRules.MatchesExactCommand(participant, exactCommand, workerStatus))
            {
                CurrentResult.ScheduleFailureKind = exactCommand.Role == DadWorkerExecutionRole.QueueLeader || participant.IsAuthority
                    ? DadScheduleFailureKind.MissingOrUnknownLeaderState
                    : DadScheduleFailureKind.EntryTerminalFailure;
                failures.Add(DadWorkerPrequeueBarrierRules.AttributeFailure(
                    participant,
                    "Worker returned contradictory run/module/command/role/identity status."));
                continue;
            }

            missingWorkerSinceUtc.Remove(workerKey);

            workerStatuses[workerKey] = workerStatus.Clone();
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
            {
                failures.Add(DadWorkerPrequeueBarrierRules.AttributeFailure(
                    participant,
                    string.IsNullOrWhiteSpace(workerStatus.FailureReason)
                        ? workerStatus.Summary
                        : workerStatus.FailureReason));
            }
        }

        if (failures.Count > 0)
        {
            if (barrierRequired)
            {
                ScopeFinalizationToAcknowledgedWorkers(true);
            }
            else
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
            }
            ApplyModuleRoutingResult(
                module,
                BuildWorkerFailureResult(module, string.Join(" | ", failures.Distinct(StringComparer.OrdinalIgnoreCase))),
                replaceExisting: true);
            return;
        }

        if (barrierRequired &&
            !workerStatuses.Values.Any(static worker => worker.Role == DadWorkerExecutionRole.QueueLeader))
        {
            if (DadWorkerPrequeueBarrierRules.AreAllNonLeadersWaiting(
                    activePlan,
                    activeParticipants,
                    workerStatuses))
            {
                DispatchWorkerExecution(module);
                return;
            }

            ApplyModuleRoutingResult(
                module,
                BuildWorkerProgressResult(
                    module,
                    $"ADS prequeue barrier is waiting: {workerStatuses.Count(static pair => pair.Value.State == DadWorkerExecutionState.WaitingForQueue)}/{Math.Max(1, activeParticipants.Count - 1)} non-leader worker(s) reached WaitingForQueue; Accepted alone is not ready."),
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
            workerCommands.Clear();
            missingWorkerSinceUtc.Clear();
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

                FinishSuccessfulWork("Dad module routing complete.");
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
            FinishSuccessfulWork($"Dad stop policy reached after {stopProgress.CompletedRuns} run(s): {stopProgress.Summary}");
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
        workerCommands.Clear();
        missingWorkerSinceUtc.Clear();
        finalizationCancellationScopeOverride = null;
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

    private void FinishSuccessfulWork(string summary)
    {
        if (!activeScheduleRepeatBoundary.PreservePartyAfterCompletion)
        {
            BeginPartyTeardown(summary);
            return;
        }

        var preservationSummary =
            $"{summary} Preserving the party between repeats " +
            $"{activeScheduleRepeatBoundary.RepeatIteration}/{activeScheduleRepeatBoundary.RepeatCount} " +
            "of the same schedule preset row.";
        log.Information("[dad] {Summary}", preservationSummary);
        Transition(DadRunPhase.Finalizing, DadRunStatus.Running, preservationSummary);
    }

    private void BeginPartyTeardown(string summary)
    {
        if (activePlan == null)
            return;

        var frozenRoster = activeSlotManifest?.Slots
            .Select(static slot => (ContentId: slot.ContentId, CharacterKey: slot.CharacterKey.Value, IsLeader: slot.IsLeader))
            .ToList()
            ?? activePlan.Orchestration.RequiredRosterCharacters
                .Select(reference => (
                    ContentId: reference.ContentId,
                    CharacterKey: reference.CharacterKey.Value,
                    IsLeader: string.Equals(reference.CharacterKey.Value, activePlan.LeaderCharacterKey, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        var expectedMembers = frozenRoster.Select(static row => row.ContentId).Where(static id => id != 0).ToList();
        var leader = frozenRoster.FirstOrDefault(static row => row.IsLeader);
        if (leader.ContentId == 0 && frozenRoster.Count > 0)
            leader = frozenRoster[0];
        var leaderName = activeParticipants
            .FirstOrDefault(participant => participant.Character.ContentId == leader.ContentId)
            ?.Character.CharacterName ?? leader.CharacterKey;

        partyTeardownService.Begin(expectedMembers, leader.ContentId, leaderName);
        Transition(DadRunPhase.TearingDownParty, DadRunStatus.Running, $"{summary} Preparing guarded party teardown.");
    }

    private void UpdatePartyTeardown()
    {
        var decision = partyTeardownService.Update();
        CurrentResult.ActiveTaskStatus = decision.Summary;
        CurrentResult.Summary = decision.Summary;

        switch (decision.Action)
        {
            case DadPartyTeardownAction.Complete:
                Transition(DadRunPhase.Finalizing, DadRunStatus.Running, decision.Summary);
                break;
            case DadPartyTeardownAction.Fail:
                FinalizeRun(DadRunStatus.PartialFailure, "Dad run completed its duty work, but guarded party teardown failed.", decision.Summary);
                break;
            default:
                Publish();
                break;
        }
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

    private bool TryRefreshStrictMutationBoundary(string boundary)
    {
        if (activePlan == null || activeSlotManifest == null)
        {
            return true;
        }

        if (!DadFullPartyExecutionRules.RequiresLocalCoordinatorLeader(activePlan.Request) &&
            activeSlotManifest.CoordinatorTravelTarget == null)
        {
            return true;
        }

        var pool = GetPlanningPool(forcePeerRefresh: false);
        var liveCoordinatorTruth = presenceService.BuildLiveSafetySnapshot();
        LogCoordinatorProvenance(boundary, activePlan.Request.RequestId, liveCoordinatorTruth);
        var runtimeParticipants = BuildCurrentManifestParticipantSet(pool, liveCoordinatorTruth);
        var coordinatorAccountKey = DadSchedulerRoutingRules.ResolveStableClientAccount(configuration.ClientAccountId);
        if (!DadCoordinatorMutationBoundaryRules.TryResolveStrictParticipants(
                activePlan,
                activeSlotManifest,
                runtimeParticipants,
                coordinatorAccountKey,
                liveCoordinatorTruth,
                out var refreshedParticipants,
                out var blocker))
        {
            if (HasImmutableBoundaryMismatch(activePlan, activeSlotManifest))
            {
                FinalizeRun(
                    DadRunStatus.Failed,
                    $"Dad rejected {boundary} because the immutable plan/manifest binding changed.",
                    blocker);
                return false;
            }

            var contradictionProof = BuildManifestContradictionEvidence(
                activeSlotManifest,
                runtimeParticipants,
                liveCoordinatorTruth);
            var contradiction = coordinatorContradictionTracker.Observe(
                contradictionProof.Evidence,
                contradictionProof.WorldStable,
                DateTime.UtcNow,
                ParticipantPollInterval,
                contradictionProof.ObservedAtUtc);
            if (contradiction.Disposition == DadSafetyProofDisposition.Reject)
            {
                log.Error(
                    "[dad] Confirmed coordinator contradiction request={RequestId} boundary={Boundary} evidence={Evidence} firstObserved={FirstObservedAtUtc} live={LiveSnapshot}.",
                    activePlan.Request.RequestId,
                    boundary,
                    contradiction.Evidence,
                    contradiction.FirstObservedAtUtc?.ToString("O") ?? "(unknown)",
                    DadIpcJson.Serialize(liveCoordinatorTruth));
                FinalizeRun(
                    DadRunStatus.Failed,
                    $"Dad rejected {boundary} after two fresh world-stable identity contradictions.",
                    contradiction.Evidence);
                return false;
            }

            CurrentResult.Status = DadRunStatus.WaitingForParticipants;
            CurrentResult.ActiveTaskStatus = contradiction.Disposition == DadSafetyProofDisposition.Wait
                ? contradiction.Summary
                : $"Waiting at strict {boundary} boundary: {blocker}";
            CurrentResult.BlockedReason = CurrentResult.ActiveTaskStatus;
            Publish();
            return false;
        }

        if (activeSlotManifest.CoordinatorTravelTarget != null)
        {
            var travelProof = DadCoordinatorTravelRules.ValidateParticipants(
                activeSlotManifest.CoordinatorTravelTarget,
                refreshedParticipants,
                DateTime.UtcNow);
            if (!travelProof.Ready)
            {
                if (travelProof.ImmutableTargetChanged)
                {
                    FinalizeRun(
                        DadRunStatus.Failed,
                        $"Dad rejected {boundary} because the immutable Coordinator travel target changed.",
                        travelProof.Summary);
                    return false;
                }

                CurrentResult.Status = DadRunStatus.WaitingForParticipants;
                CurrentResult.ActiveTaskStatus = $"Waiting at strict {boundary} boundary: {travelProof.Summary}";
                CurrentResult.BlockedReason = CurrentResult.ActiveTaskStatus;
                CurrentResult.Participants = refreshedParticipants.Select(static participant => participant.Clone()).ToList();
                Publish();
                return false;
            }
        }

        coordinatorContradictionTracker.Reset();
        activeParticipants.Clear();
        activeParticipants.AddRange(refreshedParticipants);
        CurrentResult.Participants = activeParticipants.Select(static participant => participant.Clone()).ToList();
        return true;
    }

    private static bool HasImmutableBoundaryMismatch(DadRunPlan plan, DadRunSlotManifest manifest)
        => plan.Request == null ||
           plan.Orchestration == null ||
           !string.Equals(plan.Request.RequestId, manifest.RequestId, StringComparison.Ordinal) ||
           manifest.ExpectedPartySize != plan.RequiredParticipantCount ||
           manifest.Slots.Count != plan.RequiredParticipantCount ||
           !string.Equals(plan.LeaderCharacterKey, manifest.LeaderCharacterKey, StringComparison.OrdinalIgnoreCase);

    private static (string Evidence, bool WorldStable, DateTime ObservedAtUtc) BuildManifestContradictionEvidence(
        DadRunSlotManifest manifest,
        IReadOnlyList<DadParticipantSnapshot> runtimeParticipants,
        DadParticipantSnapshot liveCoordinatorTruth)
    {
        foreach (var slot in manifest.Slots.OrderBy(static slot => DadPlannerSlotRules.GetSlotSortKey(slot.SlotId)))
        {
            var runtime = runtimeParticipants.FirstOrDefault(participant =>
                string.Equals(
                    participant.WorkerSessionId.Value,
                    slot.WorkerSessionId.Value,
                    StringComparison.OrdinalIgnoreCase));
            if (runtime == null &&
                string.Equals(
                    liveCoordinatorTruth.WorkerSessionId.Value,
                    slot.WorkerSessionId.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                runtime = liveCoordinatorTruth;
            }

            if (runtime is not { WorldReadyStable: true })
                continue;

            if (!runtime.ManagedAccountKey.IsEmpty &&
                !DadRosterIdentity.SameAccount(runtime.ManagedAccountKey, slot.AccountKey))
            {
                return (
                    $"Frozen worker {slot.WorkerSessionId} reports stable account {runtime.ManagedAccountKey}; expected {slot.AccountKey} for {slot.SlotId}.",
                    true,
                    runtime.LastHeartbeatUtc);
            }

            if (string.Equals(
                    runtime.ActiveCharacterKey.Value,
                    slot.CharacterKey.Value,
                    StringComparison.OrdinalIgnoreCase) &&
                runtime.Character.ContentId != 0 &&
                runtime.Character.ContentId != slot.ContentId)
            {
                return (
                    $"Character {slot.CharacterKey} reports stable Content ID {runtime.Character.ContentId}; expected frozen {slot.ContentId} for {slot.SlotId}.",
                    true,
                    runtime.LastHeartbeatUtc);
            }
        }

        return (string.Empty, liveCoordinatorTruth.WorldReadyStable, liveCoordinatorTruth.LastHeartbeatUtc);
    }

    private IReadOnlyList<DadParticipantSnapshot> BuildOnlineParticipantSet(
        DadCharacterPool pool,
        DadParticipantSnapshot? liveLocalTruth = null)
        => DadCoordinatorRuntimeProjectionRules.BuildOnlineParticipantSet(
            liveLocalTruth ?? presenceService.BuildLiveSafetySnapshot(),
            pool.PeerTransport.KnownParticipants,
            transportService.IsWorkerOnline);

    private IReadOnlyList<DadParticipantSnapshot> BuildCurrentManifestParticipantSet(
        DadCharacterPool pool,
        DadParticipantSnapshot? liveLocalTruth = null)
    {
        if (activeSlotManifest == null)
            return [];

        var frozenSessions = activeSlotManifest.Slots
            .Select(static slot => slot.WorkerSessionId.Value)
            .Where(static session => !string.IsNullOrWhiteSpace(session))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return DadCoordinatorRuntimeProjectionRules.BuildFrozenParticipantSet(
            liveLocalTruth ?? presenceService.BuildLiveSafetySnapshot(),
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
                    "[dad] Frozen run assignment accepted request={RequestId} module={ModuleId} duty={DutyName} cfc={ContentFinderConditionId} unsynced={Unsynced} party={PartySize} slot={SlotId} account={AccountKey} character={CharacterKey} contentId={ContentId} requestedJob={RequestedJobId} worker={WorkerSessionId} leader={IsLeader} inviter={IsInviter}.",
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
                    slot.RequiredJobId?.ToString() ?? "(current)",
                    slot.WorkerSessionId,
                    slot.IsLeader,
                    slot.IsInviter);
            }
        }
    }

    private void LogCoordinatorProvenance(
        string boundary,
        string requestId,
        DadParticipantSnapshot liveTruth)
    {
        var resolved = DadFullPartyExecutionRules.TryResolveActiveCoordinatorCharacter(
            liveTruth,
            out var character,
            out var blocker);
        var transition = string.Join(
            "|",
            liveTruth.WorkerSessionId.Value,
            liveTruth.ClientInstanceId,
            liveTruth.ManagedAccountKey.Value,
            liveTruth.ActiveCharacterKey.Value,
            liveTruth.Character.ContentId,
            liveTruth.Character.Source,
            liveTruth.IsAvailable,
            liveTruth.WorldReadyStable,
            resolved,
            blocker);
        if (coordinatorProvenanceTransitions.TryGetValue(boundary, out var previous) &&
            string.Equals(previous, transition, StringComparison.Ordinal))
        {
            return;
        }

        coordinatorProvenanceTransitions[boundary] = transition;
        log.Information(
            "[dad] Coordinator provenance boundary={Boundary} request={RequestId} localWorker={LocalWorkerSessionId} localClient={LocalClientInstanceId} managedAccount={ManagedAccountKey} character={CharacterKey} contentId={ContentId} source={Source} available={Available} worldReadyStable={WorldReadyStable} resolved={Resolved} resolvedCharacter={ResolvedCharacterKey} blocker={Blocker}.",
            boundary,
            requestId,
            liveTruth.WorkerSessionId,
            liveTruth.ClientInstanceId,
            liveTruth.ManagedAccountKey,
            liveTruth.ActiveCharacterKey.IsEmpty ? "(none)" : liveTruth.ActiveCharacterKey.Value,
            liveTruth.Character.ContentId,
            liveTruth.Character.Source,
            liveTruth.IsAvailable,
            liveTruth.WorldReadyStable,
            resolved,
            resolved ? character.CharacterKey : "(none)",
            resolved ? "(none)" : blocker);
    }

    private void LogFirstPartyInviteBoundary(
        DadRunPlan plan,
        IReadOnlyList<DadAssemblyInstructionDto> instructions,
        IReadOnlyList<DadPartyMemberSnapshot> partyMembers)
    {
        if (string.Equals(firstPartyInviteBoundaryRunId, plan.Request.RequestId, StringComparison.Ordinal))
            return;

        var missingSlots = instructions
            .Where(static instruction => instruction.InstructionKind == DadAssemblyInstructionKind.JoinParty)
            .Select(instruction => new
            {
                Instruction = instruction,
                Participant = ResolveParticipantForInstruction(instruction),
            })
            .Where(candidate => candidate.Participant != null &&
                                !DadPartyAssemblyService.IsParticipantInParty(candidate.Participant, partyMembers))
            .Select(static candidate => candidate.Instruction.SlotId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingSlots.Count == 0)
            return;

        firstPartyInviteBoundaryRunId = plan.Request.RequestId;
        var preparationOutcomes = (activeSlotManifest?.Slots ?? [])
            .OrderBy(static slot => slot.SlotId, StringComparer.OrdinalIgnoreCase)
            .Select(slot =>
            {
                var participant = activeParticipants.FirstOrDefault(candidate =>
                    string.Equals(candidate.AssignedSlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase));
                var outcome = slot.RequiredJobId.HasValue
                    ? participant?.RequestedJobPreparation?.Status.ToString() ?? "Missing"
                    : "NotRequested";
                return $"{slot.SlotId}:job={slot.RequiredJobId?.ToString() ?? "current"},outcome={outcome}";
            })
            .ToList();
        log.Information(
            "[dad] First party-invite boundary request={RequestId} module={ModuleId} leader={LeaderCharacterKey} inviter={InviterCharacterKey} expectedParty={ExpectedPartySize} partyListCount={PartyListCount} missingSlots={MissingSlots} requestedJobOutcomes={RequestedJobOutcomes}.",
            plan.Request.RequestId,
            plan.CompositeModuleId,
            plan.LeaderCharacterKey,
            plan.InviterCharacterKey,
            plan.RequiredParticipantCount,
            partyMembers.Count,
            string.Join(",", missingSlots),
            preparationOutcomes.Count == 0 ? "(none)" : string.Join(";", preparationOutcomes));
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
            if (!persistentStartup && HasTimedOut(plan.Orchestration.WaitPolicy.GetAssemblyTimeout()))
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

        TransitionAfterAssembly(
            plan,
            plan.Orchestration.LocalOnlyOverride
                ? "Local-only assembly confirmed; preparing queue executor."
                : "Single-worker assembly confirmed; preparing queue executor.");
        return true;
    }

    private void TransitionAfterAssembly(DadRunPlan plan, string queueSummary)
    {
        if (plan.Orchestration.AutoPartyFormationOnly)
        {
            Transition(
                DadRunPhase.GroupReady,
                DadRunStatus.Running,
                "AutoParty formation-only group is ready; queue execution remains locally disabled.");
            return;
        }

        Transition(DadRunPhase.QueuePreparing, DadRunStatus.Running, queueSummary);
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
        configuration.PreDutyRepairPolicy ??= new DadPreDutyRepairPolicy();
        request.PreDutyRepairPolicy = configuration.PreDutyRepairPolicy.Clone().Normalize();
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
            var exactCancellationScope = finalizationCancellationScopeOverride;
            var cancellationTargets = exactCancellationScope ?? activeParticipants;
            var remoteCancellationTargets = cancellationTargets
                .Where(static participant => !participant.IsLocalClient)
                .ToList();
            var finalizationCommand = new DadCancelCommandDto
            {
                RunId = activePlan.Request.RequestId,
                AuthorityWorkerSessionId = presenceService.WorkerSessionId,
                CancellationState = DadRunCancellationState.Finalized,
                Reason = $"Dad run finalized with status {status}.",
            };
            QueueCoordinatorCancellations(
                finalizationCommand,
                finalizationCommand.Reason,
                exactCancellationScope);
            transportService.BroadcastCancel(
                finalizationCommand,
                remoteCancellationTargets);
            presenceService.HandleCancelRun(finalizationCommand);
            if (exactCancellationScope == null ||
                exactCancellationScope.Any(static participant => participant.IsLocalClient))
            {
                workerExecutionService.Cancel(new DadWorkerExecutionCancel
                {
                    RunId = activePlan.Request.RequestId,
                    Reason = finalizationCommand.Reason,
                });
            }
        }

        if (activePlan != null && status != DadRunStatus.Cancelled)
            claimService.ReleaseClaims(activePlan.Request.RequestId);

        CurrentResult.Status = status;
        if (status == DadRunStatus.Cancelled)
            CurrentResult.ScheduleFailureKind = DadScheduleFailureKind.Cancellation;
        else if (status is DadRunStatus.Failed or DadRunStatus.PartialFailure or DadRunStatus.TimedOut &&
                 CurrentResult.ScheduleFailureKind == DadScheduleFailureKind.None)
            CurrentResult.ScheduleFailureKind = DadScheduleFailureKind.EntryTerminalFailure;
        else if (status == DadRunStatus.Completed)
            CurrentResult.ScheduleFailureKind = DadScheduleFailureKind.None;
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
        DadRunHistoryPersistenceRules.InsertSnapshot(configuration.RunHistory, CurrentResult);
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
        workerCommands.Clear();
        missingWorkerSinceUtc.Clear();
        finalizationCancellationScopeOverride = null;
        nextWorkerStatusPollUtc = DateTime.MinValue;
        loggedSingleWorkerSeed = false;
        loggedSingleWorkerAssemblyConfirmed = false;
        lastSingleWorkerAssemblyBlocker = string.Empty;
        slotResolutionTransitions.Clear();
        assignmentTransitions.Clear();
        partyTransitions.Clear();
        workerCommandTransitions.Clear();
        coordinatorProvenanceTransitions.Clear();
        firstPartyInviteAttemptUtcByContentId.Clear();
        firstPartyInviteBoundaryRunId = string.Empty;
        inviteRetryContinuationRunId = string.Empty;
        persistentStartup = false;
        activeScheduleRepeatBoundary = DadScheduleRepeatBoundary.Standalone;
        coordinatorContradictionTracker.Reset();
        remoteAssignmentTracker.Clear();
        partyInviteGateway.Reset();
        partyTeardownService.Reset();
        presenceService.ResetToIdle();

        log.Information("[dad] Finalized run {RequestId}: {Status} {Summary}", CurrentResult.RequestId, status, summary);
        return PublishAndClone();
    }

    private void QueueCoordinatorCancellations(
        DadCancelCommandDto command,
        string workerReason,
        IReadOnlyCollection<DadParticipantSnapshot>? exactTargets = null)
    {
        var targets = (exactTargets ?? activeParticipants)
            .Where(static participant => !participant.IsLocalClient && !participant.WorkerSessionId.IsEmpty)
            .Select(static participant => participant.Clone())
            .ToList();
        if (exactTargets == null && activeSlotManifest != null)
        {
            foreach (var slot in activeSlotManifest.Slots.Where(slot =>
                         !slot.WorkerSessionId.IsEmpty &&
                         !string.Equals(
                             slot.WorkerSessionId.Value,
                             presenceService.WorkerSessionId.Value,
                             StringComparison.OrdinalIgnoreCase) &&
                         targets.All(target => !string.Equals(
                             target.WorkerSessionId.Value,
                             slot.WorkerSessionId.Value,
                             StringComparison.OrdinalIgnoreCase))))
            {
                targets.Add(new DadParticipantSnapshot
                {
                    WorkerSessionId = slot.WorkerSessionId,
                    ManagedAccountKey = slot.AccountKey,
                    ActiveCharacterKey = slot.CharacterKey,
                    AssignedSlotId = slot.SlotId,
                    Character = new DadAcquiredCharacter
                    {
                        AccountId = slot.AccountKey.Value,
                        CharacterKey = slot.CharacterKey.Value,
                        ContentId = slot.ContentId,
                    },
                });
            }
        }

        foreach (var participant in targets)
        {
            var key = $"{command.RunId}|{participant.WorkerSessionId.Value}";
            if (pendingCoordinatorCancellations.ContainsKey(key))
                continue;

            pendingCoordinatorCancellations[key] = new PendingCoordinatorCancellation
            {
                Target = participant.Clone(),
                RunCommand = new DadCancelCommandDto
                {
                    RunId = command.RunId,
                    AuthorityWorkerSessionId = command.AuthorityWorkerSessionId,
                    CancellationState = command.CancellationState,
                    Reason = command.Reason,
                },
                WorkerCommand = new DadWorkerExecutionCancel
                {
                    RunId = command.RunId,
                    Reason = string.IsNullOrWhiteSpace(workerReason) ? command.Reason : workerReason,
                },
            };
        }

        UpdatePendingCoordinatorCancellations();
    }

    private void UpdatePendingCoordinatorCancellations()
    {
        if (pendingCoordinatorCancellations.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var pair in pendingCoordinatorCancellations.ToList())
        {
            var pending = pair.Value;
            if (now < pending.NextAttemptUtc)
                continue;

            pending.NextAttemptUtc = now + CancellationRetryInterval;
            if (!transportService.IsWorkerOnline(pending.Target.WorkerSessionId))
            {
                LogCoordinatorCancellationTransition(pair.Key, pending, "waiting-route");
                continue;
            }

            if (!pending.RunAcknowledged)
            {
                var ack = transportService.SendCancelRun(pending.Target, pending.RunCommand);
                pending.RunAcknowledged = ack is { Acknowledged: true } &&
                                          string.Equals(ack.RunId, pending.RunCommand.RunId, StringComparison.Ordinal) &&
                                          string.Equals(
                                              ack.WorkerSessionId.Value,
                                              pending.Target.WorkerSessionId.Value,
                                              StringComparison.OrdinalIgnoreCase);
            }

            if (!pending.WorkerAcknowledged)
            {
                var ack = transportService.SendWorkerExecutionCancel(pending.Target, pending.WorkerCommand);
                pending.WorkerAcknowledged = ack is { Accepted: true } &&
                                             string.Equals(ack.RunId, pending.WorkerCommand.RunId, StringComparison.Ordinal) &&
                                             string.Equals(
                                                 ack.WorkerSessionId.Value,
                                                 pending.Target.WorkerSessionId.Value,
                                                 StringComparison.OrdinalIgnoreCase);
            }

            if (!pending.RunAcknowledged || !pending.WorkerAcknowledged)
            {
                LogCoordinatorCancellationTransition(
                    pair.Key,
                    pending,
                    $"waiting-ack:run={pending.RunAcknowledged}:worker={pending.WorkerAcknowledged}");
                continue;
            }

            LogCoordinatorCancellationTransition(pair.Key, pending, "acknowledged");
            pendingCoordinatorCancellations.Remove(pair.Key);
        }
    }

    private void LogCoordinatorCancellationTransition(
        string key,
        PendingCoordinatorCancellation pending,
        string state)
    {
        if (string.Equals(pending.LastDiagnosticState, state, StringComparison.Ordinal))
            return;

        pending.LastDiagnosticState = state;
        log.Information(
            "[dad] Coordinator cancellation transition command={CommandId} request={RequestId} worker={WorkerSessionId} state={State} runAck={RunAcknowledged} workerAck={WorkerAcknowledged} reason={Reason}.",
            key,
            pending.RunCommand.RunId,
            pending.Target.WorkerSessionId,
            state,
            pending.RunAcknowledged,
            pending.WorkerAcknowledged,
            pending.RunCommand.Reason);
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
        recovered.ScheduleFailureKind = DadScheduleFailureKind.CoordinatorReloadAbandonment;
        recovered.CompletedAtUtc = DateTime.UtcNow;
        recovered.Leases = [];
        recovered.CurrentExecutorStatus = new DadModuleExecutionStatusDto();
        DadRunHistoryPersistenceRules.InsertSnapshot(configuration.RunHistory, recovered);
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
        target.Dependencies = source.Dependencies.Clone();
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
