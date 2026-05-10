using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadCoordinatorService
{
    private static readonly TimeSpan ParticipantPollInterval = TimeSpan.FromSeconds(2);

    private readonly Configuration configuration;
    private readonly ConfigManager configManager;
    private readonly DadCharacterIntelligenceService characterIntelligenceService;
    private readonly DadPresenceService presenceService;
    private readonly DadTransportService transportService;
    private readonly DadClaimService claimService;
    private readonly DadPartyAssemblyService partyAssemblyService;
    private readonly DadQueueExecutionService queueExecutionService;
    private readonly DadPlannerService plannerService;
    private readonly IPluginLog log;

    private DadRunPlan? activePlan;
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
    private string lastParticipantDiscoveryFilterSummary = string.Empty;

    public DadCoordinatorService(
        Configuration configuration,
        ConfigManager configManager,
        DadCharacterIntelligenceService characterIntelligenceService,
        DadPresenceService presenceService,
        DadTransportService transportService,
        DadClaimService claimService,
        DadPartyAssemblyService partyAssemblyService,
        DadQueueExecutionService queueExecutionService,
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
        this.queueExecutionService = queueExecutionService;
        this.plannerService = plannerService;
        this.log = log;
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
            unavailable.Summary = "Server Dad status unavailable.";
            unavailable.BlockedReason = "Server Dad status query failed.";
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
                return DadRunResult.Rejected(request, "No Server Dad authority discovered on localhost.");

            log.Information("[dad] Forwarding run {RequestId} to Server Dad at {Endpoint}: {Payload}",
                request.RequestId,
                authorityEndpoint,
                request.DescribeRequestedWork());
            var forwarded = transportService.SendStartRunCommand(authorityEndpoint, request);
            if (forwarded != null)
            {
                log.Information("[dad] Server Dad responded for forwarded run {RequestId}: {Status}/{Phase}/{Module} {Summary}",
                    forwarded.RequestId,
                    forwarded.Status,
                    forwarded.Phase,
                    forwarded.ModuleId,
                    forwarded.Summary);
                return forwarded;
            }

            log.Warning("[dad] Server Dad did not answer forwarded run {RequestId} for {Payload}", request.RequestId, request.DescribeRequestedWork());
            return DadRunResult.Rejected(request, "Server Dad did not accept forwarded run start.");
        }

        var pool = characterIntelligenceService.RefreshLocalCharacterPool("run-start", logRefresh: false);
        var plan = plannerService.BuildPlan(request, pool, out var rejectionReason);
        if (plan == null)
            return DadRunResult.Rejected(request, rejectionReason);

        activePlan = plan;
        activeParticipants.Clear();
        stepResults.Clear();
        stopProgress = DadRunStopProgress.FromPolicy(plan.Request.StopPolicy);
        activeModuleIndex = -1;
        activeStepResultIndex = -1;
        loggedSingleWorkerSeed = false;
        loggedSingleWorkerAssemblyConfirmed = false;
        lastSingleWorkerAssemblyBlocker = string.Empty;
        lastParticipantDiscoveryFilterSummary = string.Empty;
        nextParticipantPollUtc = DateTime.MinValue;
        claimService.ReleaseClaims(plan.Request.RequestId);
        presenceService.MarkLeader(plan.Request.RequestId, plan.Orchestration.AuthorityMode, $"Server Dad planned {plan.Modules.Count} Dad module(s).");
        SeedLocalParticipantIfNeeded(plan);

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

        Transition(plan.RequiresRemoteParticipants ? DadRunPhase.DiscoveringParticipants : DadRunPhase.ClaimingSlots,
            plan.RequiresRemoteParticipants ? DadRunStatus.WaitingForParticipants : DadRunStatus.Running,
            plan.RequiresRemoteParticipants
                ? $"Server Dad waiting for {plan.RequiredParticipantCount} participant(s)."
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
                log.Information("[dad] Forwarding cancel to Server Dad at {Endpoint}: request {RequestId}",
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
                    log.Information("[dad] Server Dad responded to forwarded cancel {RequestId}: {Status} {Summary}",
                        string.IsNullOrWhiteSpace(forwarded.RequestId) ? "(none)" : forwarded.RequestId,
                        forwarded.Status,
                        forwarded.Summary);
                    return forwarded;
                }

                log.Warning("[dad] Forwarded cancel did not receive a Server Dad response for request {RequestId}",
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
        var executorCancel = queueExecutionService.CancelActiveExecutor("Cancelled by operator.");
        claimService.ReleaseClaims(activePlan.Request.RequestId);
        if (!string.IsNullOrWhiteSpace(executorCancel.StepName))
            stepResults.Add(executorCancel);

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
                CopyParticipant(local, localAck.Snapshot);
        }

        foreach (var ack in remoteAcks)
        {
            var participant = activeParticipants.FirstOrDefault(candidate =>
                string.Equals(candidate.WorkerSessionId, ack.WorkerSessionId.ToString(), StringComparison.OrdinalIgnoreCase));
            if (participant != null)
                CopyParticipant(participant, ack.Snapshot);
        }

        return FinalizeRun(DadRunStatus.Cancelled, "Dad run cancelled.", "Cancelled by operator.");
    }

    private void UpdateParticipantDiscovery()
    {
        if (activePlan == null)
            return;

        var pool = GetPlanningPool(forcePeerRefresh: DateTime.UtcNow >= nextParticipantPollUtc);
        var plannedCharacters = plannerService.ResolveParticipants(activePlan, pool, out var plannerBlocker);
        nextParticipantPollUtc = DateTime.UtcNow + ParticipantPollInterval;

        activeParticipants.Clear();
        activeParticipants.Add(BuildLocalAssignment(activePlan.LeaderCharacterKey, activePlan.Orchestration.AuthorityMode, slotId: "Leader"));

        foreach (var character in plannedCharacters.Where(static character => character.Source != DadCharacterSource.LocalRuntime))
        {
            var participant = ResolvePeerParticipant(character, pool.PeerTransport.KnownParticipants);
            if (participant != null)
            {
                participant.AssignedSlotId = string.IsNullOrWhiteSpace(participant.AssignedSlotId)
                    ? $"Party {activeParticipants.Count + 1}"
                    : participant.AssignedSlotId;
                activeParticipants.Add(participant);
            }
        }

        CurrentResult.Participants = activeParticipants.Select(static participant => participant.Clone()).ToList();

        if (!string.IsNullOrWhiteSpace(plannerBlocker) || activeParticipants.Count < activePlan.RequiredParticipantCount)
        {
            var skipSummary = BuildRemoteParticipantSkipSummary(pool.PeerTransport.KnownParticipants);
            LogParticipantDiscoveryFilter(skipSummary);

            if (HasTimedOut(activePlan.Orchestration.WaitPolicy.GetParticipantReadyTimeout()))
            {
                FinalizeRun(
                    DadRunStatus.TimedOut,
                    "Dad run timed out waiting for participant discovery.",
                    string.IsNullOrWhiteSpace(plannerBlocker)
                        ? $"Needed {activePlan.RequiredParticipantCount} participant(s), found {activeParticipants.Count}."
                        : plannerBlocker);
                return;
            }

            var waitSummary = string.IsNullOrWhiteSpace(plannerBlocker)
                ? $"Waiting for {activePlan.RequiredParticipantCount} participant(s); found {activeParticipants.Count}."
                : plannerBlocker;
            CurrentResult.ActiveTaskStatus = string.IsNullOrWhiteSpace(skipSummary)
                ? waitSummary
                : $"{waitSummary} {skipSummary}";
            CurrentResult.BlockedReason = CurrentResult.ActiveTaskStatus;
            Publish();
            return;
        }

        var blockers = new List<string>();
        foreach (var participant in activeParticipants.Where(static participant => !participant.IsLocalClient))
        {
            var ready = transportService.SendWakeRequest(participant, new DadWakeRequestDto
            {
                RunId = activePlan.Request.RequestId,
                AuthorityWorkerSessionId = presenceService.WorkerSessionId,
                AuthorityMode = activePlan.Orchestration.AuthorityMode,
                ModuleId = activePlan.CompositeModuleId,
                RequiredAccountKey = participant.ManagedAccountKey,
                RequiredCharacterKey = participant.ActiveCharacterKey,
                AssignedSlotId = participant.AssignedSlotId,
                RequirePostArReady = activePlan.Orchestration.RequirePostArReady,
            });

            if (ready == null)
            {
                blockers.Add($"No assignment acknowledgement from {participant.ActiveCharacterKey}.");
                continue;
            }

            CopyParticipant(participant, ready.Snapshot);
            participant.AssignedSlotId = string.IsNullOrWhiteSpace(participant.AssignedSlotId) ? ready.Snapshot.AssignedSlotId : participant.AssignedSlotId;
            if (!ready.AcceptedAssignment || !string.IsNullOrWhiteSpace(ready.BlockerSummary))
                blockers.Add(string.IsNullOrWhiteSpace(ready.BlockerSummary) ? ready.StatusText : ready.BlockerSummary);
        }

        blockers.AddRange(activeParticipants
            .Where(static participant => participant.State is DadParticipantState.WaitingForRequiredCharacter or DadParticipantState.WaitingForPostArReady or DadParticipantState.Stale)
            .Select(static participant => string.IsNullOrWhiteSpace(participant.StatusText) ? participant.State.ToString() : participant.StatusText));

        if (blockers.Count > 0)
        {
            if (HasTimedOut(activePlan.Orchestration.WaitPolicy.GetParticipantReadyTimeout()))
            {
                FinalizeRun(
                    DadRunStatus.TimedOut,
                    "Dad run timed out waiting for worker readiness.",
                    string.Join(" | ", blockers.Distinct(StringComparer.OrdinalIgnoreCase)));
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
        if (activePlan == null)
            return;

        var blockers = new List<string>();
        foreach (var participant in activeParticipants)
        {
            if (participant.State is DadParticipantState.WaitingForRequiredCharacter or DadParticipantState.WaitingForPostArReady or DadParticipantState.Stale)
            {
                blockers.Add(string.IsNullOrWhiteSpace(participant.StatusText) ? $"{participant.ActiveCharacterKey} is not ready." : participant.StatusText);
                continue;
            }

            var request = new DadClaimRequestDto
            {
                RunId = activePlan.Request.RequestId,
                AuthorityWorkerSessionId = presenceService.WorkerSessionId,
                ModuleId = activePlan.CompositeModuleId,
                SlotId = participant.AssignedSlotId,
                RequiredAccountKey = participant.ManagedAccountKey,
                RequiredCharacterKey = participant.ActiveCharacterKey,
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

            claimService.AcknowledgeLease(decision);
            CopyParticipant(participant, decision.Snapshot);
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
        foreach (var instruction in instructions)
        {
            var participant = activeParticipants.FirstOrDefault(candidate =>
                string.Equals(candidate.ActiveCharacterKey, instruction.RequiredCharacterKey.ToString(), StringComparison.OrdinalIgnoreCase));
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

        Transition(DadRunPhase.QueuePreparing, DadRunStatus.Running, "Dad party assembly confirmed; preparing queue executor.");
    }

    private void UpdateModuleRouting()
    {
        if (activePlan == null)
            return;

        if (activeModuleIndex >= 0 && activeModuleIndex < activePlan.Modules.Count)
        {
            var activeStatus = queueExecutionService.GetActiveExecutorStatus();
            if (activeStatus.IsActive)
            {
                var activeModule = activePlan.Modules[activeModuleIndex];
                var updateResult = queueExecutionService.UpdateActiveExecutor();
                ApplyModuleRoutingResult(activeModule, updateResult, replaceExisting: true);
                return;
            }
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
        var result = queueExecutionService.ExecuteModule(activePlan, module, activeParticipants);
        ApplyModuleRoutingResult(module, result, replaceExisting: false);
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

        var pool = RefreshStopPolicyPool(activePlan);
        var nextPlan = plannerService.BuildPlan(activePlan.Request, pool, out var rejectionReason);
        if (nextPlan == null)
        {
            FinalizeRun(
                DadRunStatus.PartialFailure,
                "Dad stop-policy repeat blocked before next run.",
                rejectionReason);
            return true;
        }

        activePlan = nextPlan;
        activeParticipants.Clear();
        activeModuleIndex = -1;
        activeStepResultIndex = -1;
        loggedSingleWorkerSeed = false;
        loggedSingleWorkerAssemblyConfirmed = false;
        lastSingleWorkerAssemblyBlocker = string.Empty;
        lastParticipantDiscoveryFilterSummary = string.Empty;
        nextParticipantPollUtc = DateTime.MinValue;
        claimService.ReleaseClaims(nextPlan.Request.RequestId);
        presenceService.MarkLeader(nextPlan.Request.RequestId, nextPlan.Orchestration.AuthorityMode, $"Server Dad repeating {module.DisplayName}; {stopProgress.Summary}");
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

        Transition(nextPlan.RequiresRemoteParticipants ? DadRunPhase.DiscoveringParticipants : DadRunPhase.ClaimingSlots,
            nextPlan.RequiresRemoteParticipants ? DadRunStatus.WaitingForParticipants : DadRunStatus.Running,
            nextPlan.RequiresRemoteParticipants
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

        stopProgress.StopReached = policy.Mode == DadPlannerStopMode.AfterRuns
            ? stopProgress.CompletedRuns >= Math.Max(1, policy.AfterRuns)
            : stopProgress.CurrentLevel.HasValue && stopProgress.CurrentLevel.Value >= policy.TargetLevel;
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

        return pool.Characters
            .FirstOrDefault(character => string.Equals(
                character.CharacterKey,
                policy.TargetCharacterKey.Value,
                StringComparison.OrdinalIgnoreCase))
            ?.CurrentLevel;
    }

    private static string BuildStopProgressSummary(DadRunStopProgress progress)
    {
        var policy = progress.StopPolicy;
        return policy.Mode == DadPlannerStopMode.TargetLevel
            ? progress.CurrentLevel.HasValue
                ? $"target level {policy.TargetLevel}; current {progress.CurrentLevel.Value}; completed {progress.CompletedRuns}/{progress.SafetyCap} run(s)"
                : $"target level {policy.TargetLevel}; current level unknown; completed {progress.CompletedRuns}/{progress.SafetyCap} run(s)"
            : $"completed {progress.CompletedRuns}/{Math.Max(1, policy.AfterRuns)} run(s)";
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
            if (module.ModuleId is DadModuleId.Duty or DadModuleId.DutySupport or DadModuleId.Trust or DadModuleId.PremadeDuty)
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

    private void SeedLocalParticipantIfNeeded(DadRunPlan plan)
    {
        if (plan.RequiresRemoteParticipants || activeParticipants.Any(static participant => participant.IsLocalClient))
            return;

        var participant = BuildLocalAssignment(plan.LeaderCharacterKey, plan.Orchestration.AuthorityMode, "Leader");
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

    private DadParticipantSnapshot? ResolvePeerParticipant(DadAcquiredCharacter character, IReadOnlyList<DadParticipantSnapshot> knownParticipants)
    {
        var participant = knownParticipants.FirstOrDefault(peer =>
            IsRemoteParticipantEligibleForWork(peer) &&
            (string.Equals(peer.ActiveCharacterKey, character.CharacterKey, StringComparison.OrdinalIgnoreCase) ||
             peer.AvailableCharacterKeys.Any(key => string.Equals(key, character.CharacterKey, StringComparison.OrdinalIgnoreCase)) ||
             (!string.IsNullOrWhiteSpace(character.AccountId) &&
              string.Equals(peer.ManagedAccountKey, character.AccountId, StringComparison.OrdinalIgnoreCase)) ||
             (!string.IsNullOrWhiteSpace(character.AccountAlias) &&
              string.Equals(peer.ManagedAccountAlias, character.AccountAlias, StringComparison.OrdinalIgnoreCase))));

        if (participant == null)
            return null;

        var clone = participant.Clone();
        clone.AssignedSlotId = string.IsNullOrWhiteSpace(clone.AssignedSlotId)
            ? $"Party {activeParticipants.Count + 1}"
            : clone.AssignedSlotId;

        if (!string.Equals(clone.ActiveCharacterKey, character.CharacterKey, StringComparison.OrdinalIgnoreCase))
        {
            clone.State = DadParticipantState.WaitingForRequiredCharacter;
            clone.StatusText = $"Waiting for required character {character.CharacterKey}; active {clone.ActiveCharacterKey}.";
        }
        else if (!clone.PostArReady)
        {
            clone.State = DadParticipantState.WaitingForPostArReady;
            clone.StatusText = "Waiting for post-AR readiness.";
        }
        else
        {
            clone.State = DadParticipantState.Discovered;
            clone.StatusText = "Discovered by Server Dad.";
        }

        clone.IsEligibleForRun = IsRemoteParticipantEligibleForWork(clone);
        return clone;
    }

    private static bool IsRemoteParticipantEligibleForWork(DadParticipantSnapshot peer)
        => peer.IsAvailable
           && peer.IsEligibleForRun
           && !string.IsNullOrWhiteSpace(peer.Endpoint)
           && peer.AuthorityMode != DadAuthorityMode.LocalOnly
           && peer.State != DadParticipantState.Stale
           && peer.ClaimState != DadClaimState.Stale
           && peer.LeaseState != DadParticipantLeaseState.Stale
           && !HasLocalIsolationReason(peer);

    private string BuildRemoteParticipantSkipSummary(IReadOnlyList<DadParticipantSnapshot> knownParticipants)
    {
        if (activePlan?.RequiresRemoteParticipants != true)
            return string.Empty;

        if (knownParticipants.Count == 0)
            return "No remote Dad participants discovered for this run.";

        var skipped = knownParticipants
            .Select(GetRemoteParticipantSkipReason)
            .Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .GroupBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .Select(static group => $"{group.Count()} {group.Key}")
            .ToList();

        return skipped.Count == 0
            ? string.Empty
            : $"Remote participant filter skipped {string.Join(", ", skipped)}.";
    }

    private static string GetRemoteParticipantSkipReason(DadParticipantSnapshot peer)
    {
        if (HasLocalIsolationReason(peer))
            return "disabled/local-only peer(s)";

        if (peer.AuthorityMode == DadAuthorityMode.LocalOnly)
            return "local-only peer(s)";

        if (peer.State == DadParticipantState.Stale ||
            peer.ClaimState == DadClaimState.Stale ||
            peer.LeaseState == DadParticipantLeaseState.Stale)
        {
            return "stale peer(s)";
        }

        if (!peer.IsAvailable)
            return "unavailable peer(s)";

        if (!peer.IsEligibleForRun)
            return "ineligible peer(s)";

        if (string.IsNullOrWhiteSpace(peer.Endpoint))
            return "peer(s) missing endpoint";

        return string.Empty;
    }

    private void LogParticipantDiscoveryFilter(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary) ||
            string.Equals(lastParticipantDiscoveryFilterSummary, summary, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        log.Information("[dad] {Summary}", summary);
        lastParticipantDiscoveryFilterSummary = summary;
    }

    private static bool HasLocalIsolationReason(DadParticipantSnapshot peer)
        => IsLocalIsolationReason(peer.StatusText)
           || peer.Warnings.Any(IsLocalIsolationReason)
           || peer.Character.Blockers.Any(IsLocalIsolationReason);

    private static bool IsLocalIsolationReason(string value)
        => value.Contains("dad is disabled", StringComparison.OrdinalIgnoreCase)
           || value.Contains("dad is in local-only mode", StringComparison.OrdinalIgnoreCase);

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
        request.ApplyOrchestrationDefaults();
    }

    private static bool RequiresServerDadAuthority(DadRunRequest request)
    {
        if (request.Orchestration.LocalOnlyOverride)
            return false;

        if (request.Orchestration.RosterIntent.RequireRemoteParticipants ||
            request.Orchestration.RosterIntent.ExpectedPartySize > 1)
        {
            return true;
        }

        return request.Dungeon?.QueueViaLanParty == true ||
               request.Msq != null ||
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

        return string.IsNullOrWhiteSpace(transportService.CurrentTransport.AuthorityEndpoint)
            ? string.Empty
            : transportService.CurrentTransport.AuthorityEndpoint;
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
        Publish();
    }

    private DadRunResult FinalizeRun(DadRunStatus status, string summary, string failureReason)
    {
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

        activePlan = null;
        activeParticipants.Clear();
        activeModuleIndex = -1;
        activeStepResultIndex = -1;
        loggedSingleWorkerSeed = false;
        loggedSingleWorkerAssemblyConfirmed = false;
        lastSingleWorkerAssemblyBlocker = string.Empty;
        presenceService.ResetToIdle();

        log.Information("[dad] Finalized run {RequestId}: {Status} {Summary}", CurrentResult.RequestId, status, summary);
        return PublishAndClone();
    }

    private void CopyParticipant(DadParticipantSnapshot target, DadParticipantSnapshot source)
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
