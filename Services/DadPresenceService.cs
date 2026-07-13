using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadPresenceService
{
    private readonly Configuration configuration;
    private readonly ConfigManager configManager;
    private readonly DadVermaxionIpcService vermaxion;
    private readonly DadAutoRetainerIpcService autoRetainer;
    private readonly InfoProxyPartyInviteGateway partyInviteGateway;
    private readonly DadRequestedJobPreparationGate requestedJobPreparationGate;
    private readonly IDadClassJobGearsetGateway classJobGearsetGateway;
    private readonly IPluginLog log;
    private Func<DadWorkerSessionId, DadParticipantSnapshot?> participantResolver = static _ => null;
    private string currentRunId = string.Empty;
    private DadWorkerSessionId currentAuthorityWorkerSessionId = new(string.Empty);
    private DadAuthorityMode currentAuthorityMode = DadAuthorityMode.ServerDad;
    private DadAccountKey requiredAccountKey = new(string.Empty);
    private DadCharacterKey requiredCharacterKey = new(string.Empty);
    private string assignedSlotId = string.Empty;
    private DadRequestedJobPreparationKey? requestedJobPreparationKey;
    private string lastRequestedJobPreparationTransition = string.Empty;
    private readonly DadImmutableCommandRegistry assignmentCommandRegistry = new();
    private readonly DadRunCancellationLedger cancelledAssignmentRuns = new(StringComparer.Ordinal);

    internal DadPresenceService(
        Configuration configuration,
        ConfigManager configManager,
        DadVermaxionIpcService vermaxion,
        DadAutoRetainerIpcService autoRetainer,
        InfoProxyPartyInviteGateway partyInviteGateway,
        DadRequestedJobPreparationGate requestedJobPreparationGate,
        IDadClassJobGearsetGateway classJobGearsetGateway,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.configManager = configManager;
        this.vermaxion = vermaxion;
        this.autoRetainer = autoRetainer;
        this.partyInviteGateway = partyInviteGateway;
        this.requestedJobPreparationGate = requestedJobPreparationGate;
        this.classJobGearsetGateway = classJobGearsetGateway;
        this.log = log;
        ClientInstanceId = $"dad-{Environment.ProcessId:X}-{Guid.NewGuid():N}"[..16];
        WorkerSessionId = ClientInstanceId;
        CurrentParticipant = new DadParticipantSnapshot
        {
            ClientInstanceId = ClientInstanceId,
            WorkerSessionId = WorkerSessionId,
            MachineName = Environment.MachineName,
            ProcessId = Environment.ProcessId,
            WorkerRole = GetConfiguredWorkerRole(),
            State = DadParticipantState.Idle,
            ManagedAccountKey = DadSchedulerRoutingRules.ResolveStableClientAccount(configuration.ClientAccountId),
            StatusText = "Waiting for first local snapshot.",
        };
    }

    public string ClientInstanceId { get; }

    public DadWorkerSessionId WorkerSessionId { get; }

    public DadParticipantSnapshot CurrentParticipant { get; private set; }

    public void ConfigureParticipantResolver(Func<DadWorkerSessionId, DadParticipantSnapshot?> resolver)
        => participantResolver = resolver ?? (static _ => null);

    public void Update(DadCharacterPool pool, string endpoint = "")
    {
        // Stored/XADB rows describe the roster, not the character currently loaded in this client.
        // Falling back to one of them would keep a relogging client falsely available under the old identity.
        var localCharacter = RefreshLocalRuntimeJobTruth(
            pool.Characters.FirstOrDefault(static character => character.Source == DadCharacterSource.LocalRuntime));
        var availableCharacterKeys = BuildAvailableCharacterKeys(localCharacter);
        var managedAccountKey = DadSchedulerRoutingRules.ResolveStableClientAccount(configuration.ClientAccountId);
        var managedAccountAlias = configManager.GetCurrentAccountAlias();
        var vermaxionStatus = vermaxion.Inspect();
        var autoRetainerStatus = autoRetainer.Inspect();
        var worldReadyStable = EvaluateBasePostArReady(localCharacter);
        var postArReady = DadExternalAutomationRules.ApplyPostArReadiness(
                              worldReadyStable,
                              vermaxionStatus) &&
                          !autoRetainerStatus.IsSuppressed;
        var workerRole = GetConfiguredWorkerRole();
        var nextState = ResolveParticipantState(localCharacter, postArReady);

        CurrentParticipant = new DadParticipantSnapshot
        {
            ClientInstanceId = ClientInstanceId,
            WorkerSessionId = WorkerSessionId,
            MachineName = Environment.MachineName,
            ProcessId = Environment.ProcessId,
            Endpoint = endpoint,
            RunId = currentRunId,
            AuthorityMode = currentAuthorityMode,
            Role = CurrentParticipant.Role,
            WorkerRole = workerRole,
            State = nextState,
            ClaimState = CurrentParticipant.ClaimState,
            LeaseState = CurrentParticipant.LeaseState,
            CancellationState = CurrentParticipant.CancellationState,
            IsLocalClient = true,
            IsAuthority = configuration.RunAsServerDad,
            IsAvailable = localCharacter != null,
            IsEligibleForRun = CurrentParticipant.State is not DadParticipantState.Stale,
            PostArReady = postArReady,
            WorldReadyStable = worldReadyStable,
            AutoRetainerAvailable = autoRetainerStatus.Available,
            AutoRetainerBusy = autoRetainerStatus.IsBusy,
            AutoRetainerMultiModeEnabled = autoRetainerStatus.MultiModeEnabled,
            ExternalAutomationHeld = vermaxionStatus.IsHeld,
            ExternalAutomationActivity = vermaxionStatus.Activity,
            ExternalAutomationState = vermaxionStatus.State,
            ExternalAutomationSummary = vermaxionStatus.Summary,
            LastHeartbeatUtc = DateTime.UtcNow,
            ManagedAccountKey = managedAccountKey,
            ManagedAccountAlias = managedAccountAlias,
            ActiveCharacterKey = new DadCharacterKey(localCharacter?.CharacterKey ?? string.Empty),
            AvailableCharacterKeys = availableCharacterKeys,
            Character = localCharacter?.Clone() ?? new DadAcquiredCharacter
            {
                Source = DadCharacterSource.LocalRuntime,
                Freshness = DadSnapshotFreshness.Unknown,
                Readiness = DadReadinessState.Unknown,
                CharacterKey = string.Empty,
                Blockers = ["No local character snapshot."],
            },
            AssignedSlotId = assignedSlotId,
            DesiredCharacterKey = requiredCharacterKey.ToString(),
            RequestedJobPreparation = CurrentParticipant.RequestedJobPreparation?.Clone(),
            LeaseIssuedUtc = CurrentParticipant.LeaseIssuedUtc,
            LeaseRenewedUtc = CurrentParticipant.LeaseRenewedUtc,
            LeaseExpiresUtc = CurrentParticipant.LeaseExpiresUtc,
            Warnings = [..CurrentParticipant.Warnings],
            StatusText = BuildStatusText(localCharacter, postArReady, nextState, vermaxionStatus),
        };

        AdvanceRequestedJobPreparation(localCharacter);
    }

    public void MarkLeader(string runId, DadAuthorityMode authorityMode, string summary)
    {
        if (!string.Equals(currentRunId, runId, StringComparison.Ordinal))
            ResetRequestedJobPreparation();

        currentRunId = runId;
        currentAuthorityWorkerSessionId = WorkerSessionId;
        currentAuthorityMode = authorityMode;
        requiredAccountKey = CurrentParticipant.ManagedAccountKey;
        requiredCharacterKey = CurrentParticipant.ActiveCharacterKey;
        assignedSlotId = DadPlannerSlotRules.LeaderSlotId;
        CurrentParticipant.Role = DadOrchestrationRole.Leader;
        CurrentParticipant.WorkerRole = GetConfiguredWorkerRole();
        CurrentParticipant.State = DadParticipantState.Ready;
        CurrentParticipant.ClaimState = DadClaimState.None;
        CurrentParticipant.LeaseState = DadParticipantLeaseState.None;
        CurrentParticipant.CancellationState = DadRunCancellationState.None;
        CurrentParticipant.IsAuthority = true;
        CurrentParticipant.StatusText = summary;
    }

    internal bool BeginRequestedJobPreparation(
        string runId,
        DadFrozenRunSlot slot,
        out string blocker)
    {
        blocker = string.Empty;
        if (!slot.RequiredJobId.HasValue)
        {
            ResetRequestedJobPreparation();
            return true;
        }

        var key = new DadRequestedJobPreparationKey(
            runId,
            slot.WorkerSessionId,
            slot.SlotId,
            slot.AccountKey,
            slot.CharacterKey,
            slot.ContentId,
            slot.RequiredJobId);
        if (!key.IsValid ||
            !string.Equals(key.WorkerSessionId.Value, WorkerSessionId.Value, StringComparison.Ordinal))
        {
            blocker = $"Requested-job preparation has an invalid or non-local frozen assignment for {slot.SlotId}.";
            return false;
        }

        requiredAccountKey = slot.AccountKey;
        requiredCharacterKey = slot.CharacterKey;
        assignedSlotId = slot.SlotId;
        SetRequestedJobPreparation(key);
        return true;
    }

    public void SetLeaderState(string runId, DadParticipantState state, string summary)
    {
        if (!string.Equals(currentRunId, runId, StringComparison.Ordinal))
            currentRunId = runId;

        CurrentParticipant.Role = DadOrchestrationRole.Leader;
        CurrentParticipant.WorkerRole = GetConfiguredWorkerRole();
        CurrentParticipant.State = state;
        CurrentParticipant.StatusText = summary;
    }

    public DadParticipantReadyDto HandleWakeRequest(DadWakeRequestDto request)
    {
        var commandId = $"{request.RunId}:{request.AssignedSlotId}:{WorkerSessionId.Value}:job-assignment";
        var payload = DadIpcJson.Serialize(request);
        var registration = assignmentCommandRegistry.Register(
            commandId,
            payload,
            payload,
            $"{request.AuthorityWorkerSessionId.Value}/{request.AuthorityMode}->{WorkerSessionId.Value}");
        if (registration.Disposition == DadImmutableCommandDisposition.Collision)
        {
            log.Error(
                "[dad] Immutable requested-job assignment collision command={CommandId} originalProducerRoute={OriginalProducerRoute} incomingProducerRoute={IncomingProducerRoute} originalPayload={OriginalPayload} incomingPayload={IncomingPayload}.",
                registration.CommandId,
                registration.OriginalProducerRoute,
                registration.IncomingProducerRoute,
                registration.OriginalPayload,
                registration.IncomingPayload);
            return BuildReadyResponse(
                blockerSummary: $"Immutable requested-job assignment collision for {commandId}.",
                acceptedAssignment: false);
        }

        if (!cancelledAssignmentRuns.CanAccept(request.RunId))
        {
            return BuildReadyResponse(
                blockerSummary: $"Requested-job assignment belongs to cancelled run {request.RunId}.",
                acceptedAssignment: false);
        }

        if (!string.Equals(currentRunId, request.RunId, StringComparison.Ordinal))
            ResetRequestedJobPreparation();

        partyInviteGateway.BeginParticipantRun(request.RunId);
        currentRunId = request.RunId;
        currentAuthorityWorkerSessionId = request.AuthorityWorkerSessionId;
        currentAuthorityMode = request.AuthorityMode;
        requiredAccountKey = request.RequiredAccountKey;
        requiredCharacterKey = request.RequiredCharacterKey;
        assignedSlotId = request.AssignedSlotId;
        CurrentParticipant.Role = DadOrchestrationRole.Participant;
        CurrentParticipant.WorkerRole = GetConfiguredWorkerRole();
        CurrentParticipant.State = DadParticipantState.Assigned;
        CurrentParticipant.ClaimState = DadClaimState.None;
        CurrentParticipant.LeaseState = DadParticipantLeaseState.None;
        CurrentParticipant.CancellationState = DadRunCancellationState.None;

        if (request.RequiredJobId.HasValue)
        {
            var key = new DadRequestedJobPreparationKey(
                request.RunId,
                WorkerSessionId,
                request.AssignedSlotId,
                request.RequiredAccountKey,
                request.RequiredCharacterKey,
                request.RequiredContentId,
                request.RequiredJobId);
            if (!key.IsValid)
            {
                const string invalid = "Requested-job assignment is missing its exact run/session/slot/account/character/Content ID/job identity.";
                AddWarning(invalid);
                CurrentParticipant.StatusText = invalid;
                return BuildReadyResponse(blockerSummary: invalid, acceptedAssignment: false);
            }

            SetRequestedJobPreparation(key);
        }
        else
        {
            ResetRequestedJobPreparation();
        }

        var acceptsAccount = requiredAccountKey.IsEmpty ||
                             string.Equals(CurrentParticipant.ManagedAccountKey, requiredAccountKey.ToString(), StringComparison.OrdinalIgnoreCase);
        var acceptsCharacter = requiredCharacterKey.IsEmpty ||
                               string.Equals(CurrentParticipant.ActiveCharacterKey, requiredCharacterKey.ToString(), StringComparison.OrdinalIgnoreCase);
        var acceptsContentId = request.RequiredContentId == 0 ||
                               CurrentParticipant.Character.ContentId == request.RequiredContentId;

        if (!acceptsAccount)
        {
            var mismatch = $"Wrong account active: need {requiredAccountKey}, have {CurrentParticipant.ManagedAccountKey}.";
            AddWarning(mismatch);
            CurrentParticipant.State = DadParticipantState.Assigned;
            CurrentParticipant.StatusText = mismatch;
            return BuildReadyResponse(blockerSummary: mismatch, acceptedAssignment: true);
        }

        if (!acceptsCharacter || !acceptsContentId)
        {
            var mismatch = $"Waiting for required character {requiredCharacterKey} Content ID {request.RequiredContentId}; " +
                           $"active {CurrentParticipant.ActiveCharacterKey} Content ID {CurrentParticipant.Character.ContentId}.";
            AddWarning(mismatch);
            CurrentParticipant.State = DadParticipantState.WaitingForRequiredCharacter;
            CurrentParticipant.StatusText = mismatch;
            return BuildReadyResponse(blockerSummary: mismatch, acceptedAssignment: true);
        }

        if (request.RequirePostArReady && !CurrentParticipant.PostArReady)
        {
            CurrentParticipant.State = DadParticipantState.WaitingForPostArReady;
            CurrentParticipant.StatusText = "Waiting for post-AR readiness.";
            return BuildReadyResponse(blockerSummary: CurrentParticipant.StatusText, acceptedAssignment: true);
        }

        CurrentParticipant.State = DadParticipantState.Ready;
        CurrentParticipant.StatusText = "Worker ready for Dad Coordinator lease.";
        return BuildReadyResponse(blockerSummary: string.Empty, acceptedAssignment: true);
    }

    public DadRunStepResultDto HandleAssemblyInstruction(DadAssemblyInstructionDto instruction)
    {
        if (!string.Equals(currentRunId, instruction.RunId, StringComparison.Ordinal))
        {
            return new DadRunStepResultDto
            {
                RunId = instruction.RunId,
                ModuleId = instruction.ModuleId,
                StepName = "Assembly",
                ParticipantState = DadParticipantState.Failed,
                Summary = "Worker run mismatch during assembly.",
                FailureReason = "Assembly instruction targeted a different run.",
            };
        }

        if (!string.IsNullOrWhiteSpace(instruction.RequiredCharacterKey) &&
            !string.Equals(CurrentParticipant.ActiveCharacterKey, instruction.RequiredCharacterKey.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            var reason = $"Waiting for required character {instruction.RequiredCharacterKey}; active {CurrentParticipant.ActiveCharacterKey}.";
            AddWarning(reason);
            CurrentParticipant.State = DadParticipantState.WaitingForRequiredCharacter;
            CurrentParticipant.StatusText = reason;
            return new DadRunStepResultDto
            {
                RunId = instruction.RunId,
                ModuleId = instruction.ModuleId,
                StepName = "Assembly",
                ParticipantState = CurrentParticipant.State,
                Summary = reason,
                FailureReason = reason,
            };
        }

        if (!CurrentParticipant.PostArReady)
        {
            CurrentParticipant.State = DadParticipantState.WaitingForPostArReady;
            CurrentParticipant.StatusText = "Waiting for post-AR readiness.";
            return new DadRunStepResultDto
            {
                RunId = instruction.RunId,
                ModuleId = instruction.ModuleId,
                StepName = "Assembly",
                ParticipantState = CurrentParticipant.State,
                Summary = CurrentParticipant.StatusText,
                FailureReason = CurrentParticipant.StatusText,
            };
        }

        var summary = instruction.Summary;
        if (instruction.InstructionKind == DadAssemblyInstructionKind.JoinParty)
        {
            var joinResult = TryArmNativePartyInvitationAcceptance(instruction.RunId);
            if (!joinResult.Success)
            {
                CurrentParticipant.State = DadParticipantState.AssemblyPending;
                CurrentParticipant.StatusText = joinResult.Summary;
                return new DadRunStepResultDto
                {
                    RunId = instruction.RunId,
                    ModuleId = instruction.ModuleId,
                    StepName = "Assembly",
                    ParticipantState = CurrentParticipant.State,
                    Deferred = true,
                    Summary = joinResult.Summary,
                    FailureReason = joinResult.Summary,
                    BlockedReason = joinResult.Summary,
                };
            }

            summary = joinResult.Summary;
        }

        CurrentParticipant.State = DadParticipantState.AssemblyConfirmed;
        CurrentParticipant.StatusText = summary;
        return new DadRunStepResultDto
        {
            RunId = instruction.RunId,
            ModuleId = instruction.ModuleId,
            StepName = "Assembly",
            ParticipantState = CurrentParticipant.State,
            Success = true,
            Summary = summary,
        };
    }

    public DadCancelAckDto HandleCancelRun(DadCancelCommandDto command)
    {
        cancelledAssignmentRuns.Record(command.RunId);
        if (!string.Equals(currentRunId, command.RunId, StringComparison.Ordinal))
        {
            return new DadCancelAckDto
            {
                RunId = command.RunId,
                WorkerSessionId = WorkerSessionId,
                CancellationState = DadRunCancellationState.Acknowledged,
                Acknowledged = true,
                Summary = "Cancel ignored; worker was on a different run.",
                Snapshot = BuildSnapshotCopy(),
            };
        }

        CurrentParticipant.CancellationState = DadRunCancellationState.Acknowledged;
        CurrentParticipant.State = DadParticipantState.Cancelled;
        CurrentParticipant.StatusText = string.IsNullOrWhiteSpace(command.Reason) ? "Cancelled by Dad Coordinator." : command.Reason;
        CurrentParticipant.ClaimState = DadClaimState.Released;
        CurrentParticipant.LeaseState = DadParticipantLeaseState.Released;
        CurrentParticipant.LeaseExpiresUtc = DateTime.UtcNow;
        var snapshot = BuildSnapshotCopy();
        ResetRunContext();

        return new DadCancelAckDto
        {
            RunId = command.RunId,
            WorkerSessionId = WorkerSessionId,
            CancellationState = DadRunCancellationState.Acknowledged,
            Acknowledged = true,
            Summary = "Worker acknowledged cancellation.",
            Snapshot = snapshot,
        };
    }

    public void ApplyClaimState(string runId, DadClaimState claimState, DadParticipantLeaseState leaseState, DadParticipantLeaseRecord? lease, string summary)
    {
        if (!string.IsNullOrWhiteSpace(runId))
            currentRunId = runId;

        CurrentParticipant.ClaimState = claimState;
        CurrentParticipant.LeaseState = leaseState;
        CurrentParticipant.AssignedSlotId = lease?.SlotId ?? assignedSlotId;
        CurrentParticipant.LeaseIssuedUtc = lease?.IssuedUtc;
        CurrentParticipant.LeaseRenewedUtc = lease?.RenewedUtc;
        CurrentParticipant.LeaseExpiresUtc = lease?.ExpiresUtc;
        CurrentParticipant.State = leaseState switch
        {
            DadParticipantLeaseState.Granted => DadParticipantState.Claimed,
            DadParticipantLeaseState.Pending => DadParticipantState.Assigned,
            DadParticipantLeaseState.Released => DadParticipantState.Ready,
            DadParticipantLeaseState.Stale => DadParticipantState.Stale,
            DadParticipantLeaseState.Denied or DadParticipantLeaseState.Collided => DadParticipantState.Assigned,
            _ => CurrentParticipant.State,
        };
        CurrentParticipant.StatusText = summary;
    }

    public void ResetToIdle()
    {
        ResetRunContext();
        CurrentParticipant.Role = DadOrchestrationRole.None;
        CurrentParticipant.WorkerRole = GetConfiguredWorkerRole();
        CurrentParticipant.State = DadParticipantState.Idle;
        CurrentParticipant.ClaimState = DadClaimState.None;
        CurrentParticipant.LeaseState = DadParticipantLeaseState.None;
        CurrentParticipant.CancellationState = DadRunCancellationState.None;
        CurrentParticipant.IsAuthority = configuration.RunAsServerDad;
        CurrentParticipant.StatusText = "Idle";
        CurrentParticipant.Warnings.Clear();
    }

    public DadParticipantSnapshot BuildSnapshotCopy() => CurrentParticipant.Clone();

    /// <summary>
    /// Builds a fail-closed, non-publishing safety projection directly from Dalamud's current
    /// client, object-table, player-state, and condition truth. Takeover command boundaries use
    /// this instead of the previous framework-tick participant snapshot.
    /// </summary>
    public DadParticipantSnapshot BuildLiveSafetySnapshot()
    {
        var snapshot = CurrentParticipant.Clone();
        var managedAccountKey = DadSchedulerRoutingRules.ResolveStableClientAccount(configuration.ClientAccountId);
        snapshot.ClientInstanceId = ClientInstanceId;
        snapshot.WorkerSessionId = WorkerSessionId;
        snapshot.IsLocalClient = true;
        snapshot.IsAuthority = configuration.RunAsServerDad;
        snapshot.ManagedAccountKey = managedAccountKey;
        snapshot.ManagedAccountAlias = configManager.GetCurrentAccountAlias();
        try
        {
            var isLoggedIn = Plugin.ClientState.IsLoggedIn;
            var player = Plugin.ObjectTable.LocalPlayer;
            if (!isLoggedIn || player == null)
                return MarkLiveSnapshotUnavailable(snapshot, "The local character is offline or between sessions.");

            var now = DateTime.UtcNow;
            var characterName = player.Name.ToString().Trim();
            var worldName = player.HomeWorld.Value.Name.ToString().Trim();
            var characterKey = BuildCharacterKey(characterName, worldName);
            var contentId = Plugin.PlayerState.ContentId;
            var currentJobId = player.ClassJob.IsValid ? (uint?)player.ClassJob.RowId : null;
            var readiness = contentId == 0 || string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(worldName)
                ? DadReadinessState.Blocked
                : DadReadinessState.Ready;
            var liveCharacter = new DadAcquiredCharacter
            {
                CharacterKey = characterKey,
                ContentId = contentId,
                CharacterName = characterName,
                WorldId = (uint)player.HomeWorld.RowId,
                WorldName = worldName,
                AccountId = managedAccountKey.Value,
                AccountAlias = configManager.GetCurrentAccountAlias(),
                Source = DadCharacterSource.LocalRuntime,
                Freshness = DadSnapshotFreshness.Live,
                LastSeenUtc = now,
                CurrentJobId = currentJobId,
                CurrentJobAbbrev = player.ClassJob.IsValid
                    ? player.ClassJob.Value.Abbreviation.ToString()
                    : string.Empty,
                CurrentLevel = player.Level,
                TerritoryId = Plugin.ClientState.TerritoryType,
                Readiness = readiness,
            };
            if (currentJobId.HasValue)
                liveCharacter.JobLevels[currentJobId.Value] = player.Level;
            if (readiness != DadReadinessState.Ready)
                liveCharacter.Blockers.Add("Exact live character identity is incomplete.");

            var unsafeConditionActive = TryGetUnsafeWorldCondition(out _);
            var worldReadyStable = DadParticipantWorldSafetyRules.IsWorldReadyStable(
                isLoggedIn,
                hasLocalPlayer: true,
                characterKey,
                contentId,
                readiness,
                unsafeConditionActive);

            snapshot.IsAvailable = readiness == DadReadinessState.Ready;
            snapshot.ActiveCharacterKey = new DadCharacterKey(characterKey);
            snapshot.Character = liveCharacter;
            snapshot.WorldReadyStable = worldReadyStable;
            snapshot.PostArReady &= worldReadyStable;
            snapshot.LastHeartbeatUtc = now;
            if (!worldReadyStable)
                snapshot.StatusText = "Live world safety is not stable for takeover mutation.";
            return snapshot;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Failed to build live takeover safety snapshot.");
            return MarkLiveSnapshotUnavailable(snapshot, "Live takeover safety could not be read.");
        }
    }

    public DadParticipantStatusSnapshot BuildStatusSnapshot(
        IEnumerable<DadParticipantSnapshot> peers,
        DadTransportMode transportMode,
        bool localOnlyEnabled,
        DadWorkerSessionId authorityWorkerSessionId,
        string authorityEndpoint,
        string summary)
    {
        var participants = new List<DadParticipantSnapshot> { BuildSnapshotCopy() };
        participants.AddRange(peers
            .Where(peer => !string.Equals(peer.WorkerSessionId, WorkerSessionId.ToString(), StringComparison.Ordinal))
            .Select(static peer => peer.Clone()));

        return new DadParticipantStatusSnapshot
        {
            GeneratedAtUtc = DateTime.UtcNow,
            LocalClientInstanceId = ClientInstanceId,
            LocalWorkerSessionId = WorkerSessionId,
            LocalWorkerRole = GetConfiguredWorkerRole(),
            TransportMode = transportMode,
            LocalOnlyModeEnabled = localOnlyEnabled,
            AuthorityWorkerSessionId = authorityWorkerSessionId,
            AuthorityEndpoint = authorityEndpoint,
            Summary = summary,
            Participants = participants,
        };
    }

    private DadParticipantReadyDto BuildReadyResponse(string blockerSummary, bool acceptedAssignment)
        => new()
        {
            RunId = currentRunId,
            WorkerSessionId = WorkerSessionId,
            CharacterKey = CurrentParticipant.ActiveCharacterKey,
            State = CurrentParticipant.State,
            PostArReady = CurrentParticipant.PostArReady,
            AcceptedAssignment = acceptedAssignment,
            BlockerSummary = blockerSummary,
            StatusText = CurrentParticipant.StatusText,
            Snapshot = BuildSnapshotCopy(),
        };

    private DadParticipantState ResolveParticipantState(DadAcquiredCharacter? character, bool postArReady)
    {
        if (CurrentParticipant.CancellationState is DadRunCancellationState.Cancelling or DadRunCancellationState.Acknowledged)
            return DadParticipantState.Cancelled;

        if (string.IsNullOrWhiteSpace(currentRunId))
            return character?.Readiness == DadReadinessState.Ready ? DadParticipantState.Idle : DadParticipantState.Unknown;

        if (!requiredCharacterKey.IsEmpty &&
            !string.Equals(character?.CharacterKey, requiredCharacterKey.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return DadParticipantState.WaitingForRequiredCharacter;
        }

        if (!requiredAccountKey.IsEmpty &&
            !string.Equals(configManager.GetCurrentAccountKey(), requiredAccountKey.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return DadParticipantState.Assigned;
        }

        if (CurrentParticipant.LeaseState == DadParticipantLeaseState.Granted)
            return DadParticipantState.Claimed;

        if (!postArReady)
            return DadParticipantState.WaitingForPostArReady;

        return CurrentParticipant.State switch
        {
            DadParticipantState.AssemblyPending => DadParticipantState.AssemblyPending,
            DadParticipantState.AssemblyConfirmed => DadParticipantState.AssemblyConfirmed,
            DadParticipantState.QueuePending => DadParticipantState.QueuePending,
            DadParticipantState.Running => DadParticipantState.Running,
            _ => DadParticipantState.Ready,
        };
    }

    private List<DadCharacterKey> BuildAvailableCharacterKeys(DadAcquiredCharacter? localCharacter)
    {
        var keys = configManager.GetKnownCharacterKeysForCurrentAccount()
            .Select(static key => new DadCharacterKey(key))
            .ToList();

        if (localCharacter != null &&
            keys.All(key => !string.Equals(key, localCharacter.CharacterKey, StringComparison.OrdinalIgnoreCase)))
        {
            keys.Add(localCharacter.CharacterKey);
        }

        if (keys.Count == 0 && localCharacter != null)
            keys.Add(localCharacter.CharacterKey);

        return keys
            .DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DadAcquiredCharacter? RefreshLocalRuntimeJobTruth(DadAcquiredCharacter? localCharacter)
    {
        if (localCharacter == null)
            return null;

        var refreshed = localCharacter.Clone();
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player?.ClassJob.IsValid != true)
                return refreshed;

            var currentJobId = (uint)player.ClassJob.RowId;
            refreshed.CurrentJobId = currentJobId;
            refreshed.CurrentJobAbbrev = player.ClassJob.Value.Abbreviation.ToString();
            refreshed.CurrentLevel = player.Level;
            if (currentJobId != 0)
                refreshed.JobLevels[currentJobId] = player.Level;
        }
        catch
        {
            // Keep the last character-pool snapshot if live object truth is between lifecycles.
        }

        return refreshed;
    }

    private DadWorkerRole GetConfiguredWorkerRole()
        => configuration.RunAsServerDad ? DadWorkerRole.ServerDad : DadWorkerRole.ClientDad;

    private static bool EvaluateBasePostArReady(DadAcquiredCharacter? character)
    {
        var isLoggedIn = Plugin.ClientState.IsLoggedIn;
        var hasLocalPlayer = Plugin.ObjectTable.LocalPlayer != null;
        if (!isLoggedIn || !hasLocalPlayer || character == null)
            return false;

        return DadParticipantWorldSafetyRules.IsWorldReadyStable(
            isLoggedIn,
            hasLocalPlayer,
            character.CharacterKey,
            character.ContentId,
            character.Readiness,
            TryGetUnsafeWorldCondition(out _));
    }

    private static DadParticipantSnapshot MarkLiveSnapshotUnavailable(
        DadParticipantSnapshot snapshot,
        string status)
    {
        snapshot.IsAvailable = false;
        snapshot.ActiveCharacterKey = new DadCharacterKey(string.Empty);
        snapshot.Character = new DadAcquiredCharacter
        {
            Source = DadCharacterSource.LocalRuntime,
            Freshness = DadSnapshotFreshness.Unknown,
            Readiness = DadReadinessState.Blocked,
            Blockers = [status],
        };
        snapshot.WorldReadyStable = false;
        snapshot.PostArReady = false;
        snapshot.LastHeartbeatUtc = DateTime.UtcNow;
        snapshot.StatusText = status;
        return snapshot;
    }

    private static string BuildCharacterKey(string? name, string? worldName)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        var cleanWorld = string.IsNullOrWhiteSpace(worldName) ? string.Empty : worldName.Trim();
        return string.IsNullOrWhiteSpace(cleanName) || string.IsNullOrWhiteSpace(cleanWorld)
            ? string.Empty
            : $"{cleanName}@{cleanWorld}";
    }

    private static bool TryGetUnsafeWorldCondition(out string label)
    {
        (ConditionFlag Flag, string Label)[] unsafeConditions =
        [
            (ConditionFlag.BoundByDuty, "bound by duty"),
            (ConditionFlag.BoundByDuty56, "bound by duty"),
            (ConditionFlag.InDutyQueue, "in the Duty Finder queue"),
            (ConditionFlag.WaitingForDuty, "waiting for duty"),
            (ConditionFlag.WaitingForDutyFinder, "waiting for Duty Finder"),
            (ConditionFlag.BetweenAreas, "between areas"),
            (ConditionFlag.BetweenAreas51, "between areas"),
            (ConditionFlag.InCombat, "in combat"),
            (ConditionFlag.Crafting, "crafting"),
            (ConditionFlag.Gathering, "gathering"),
            (ConditionFlag.Casting, "casting"),
            (ConditionFlag.Occupied, "occupied"),
            (ConditionFlag.Occupied30, "occupied"),
            (ConditionFlag.OccupiedInEvent, "occupied in an event"),
            (ConditionFlag.OccupiedInQuestEvent, "occupied in a quest event"),
            (ConditionFlag.Occupied33, "occupied"),
            (ConditionFlag.OccupiedInCutSceneEvent, "occupied in a cutscene"),
            (ConditionFlag.WatchingCutscene, "watching a cutscene"),
            (ConditionFlag.TradeOpen, "in a trade"),
            (ConditionFlag.Occupied38, "occupied"),
            (ConditionFlag.Occupied39, "occupied"),
        ];
        foreach (var condition in unsafeConditions)
        {
            if (!Plugin.Condition[condition.Flag])
                continue;
            label = condition.Label;
            return true;
        }

        label = string.Empty;
        return false;
    }

    private string BuildStatusText(
        DadAcquiredCharacter? character,
        bool postArReady,
        DadParticipantState state,
        DadVermaxionReadinessStatus vermaxionStatus)
    {
        if (character == null)
            return "Dad client connected; character offline or relogging.";

        if (!requiredAccountKey.IsEmpty &&
            !string.Equals(requiredAccountKey, configManager.GetCurrentAccountKey(), StringComparison.OrdinalIgnoreCase))
        {
            return $"Wrong account active: need {requiredAccountKey}.";
        }

        if (!requiredCharacterKey.IsEmpty &&
            !string.Equals(requiredCharacterKey, character.CharacterKey, StringComparison.OrdinalIgnoreCase))
        {
            return $"Waiting for required character {requiredCharacterKey}.";
        }

        if (vermaxionStatus.IsHeld)
        {
            var detail = string.Join(
                "/",
                new[] { vermaxionStatus.Activity, vermaxionStatus.State }
                    .Where(static value => !string.IsNullOrWhiteSpace(value)));
            return string.IsNullOrWhiteSpace(detail)
                ? "Waiting for VERMAXION status."
                : $"Waiting for VERMAXION — {detail}.";
        }

        if (!postArReady && !string.IsNullOrWhiteSpace(currentRunId))
            return "Waiting for post-AR readiness.";

        return state switch
        {
            DadParticipantState.Assigned => $"Assigned slot {assignedSlotId}.",
            DadParticipantState.Claimed => "Dad Coordinator lease granted.",
            DadParticipantState.AssemblyPending => "Assembly pending.",
            DadParticipantState.AssemblyConfirmed => "Assembly acknowledged.",
            DadParticipantState.QueuePending => "Queue pending.",
            DadParticipantState.Running => "Running Dad module work.",
            DadParticipantState.Cancelled => "Cancelled.",
            _ when string.IsNullOrWhiteSpace(currentRunId) => $"Idle on {character.CharacterKey}.",
            _ => CurrentParticipant.StatusText,
        };
    }

    private void AddWarning(string warning)
    {
        if (CurrentParticipant.Warnings.Any(existing => string.Equals(existing, warning, StringComparison.OrdinalIgnoreCase)))
            return;

        CurrentParticipant.Warnings.Add(warning);
        log.Warning("[dad] Presence warning for {WorkerSessionId}: {Warning}", WorkerSessionId, warning);
    }

    private (bool Success, string Summary) TryArmNativePartyInvitationAcceptance(string runId)
    {
        var inviter = participantResolver(currentAuthorityWorkerSessionId);
        if (inviter == null)
        {
            return (false,
                $"Waiting for frozen inviter worker '{currentAuthorityWorkerSessionId}' before accepting a native party invitation.");
        }

        if (inviter.Character.ContentId == 0 ||
            string.IsNullOrEmpty(inviter.Character.CharacterName) ||
            inviter.Character.WorldId == 0 ||
            inviter.Character.WorldId > ushort.MaxValue)
        {
            return (false,
                $"Frozen inviter '{inviter.ActiveCharacterKey}' is missing its exact name, Content ID, or World ID.");
        }

        var expected = new DadExpectedPartyInviter
        {
            RunId = runId,
            WorkerSessionId = currentAuthorityWorkerSessionId,
            AccountKey = inviter.ManagedAccountKey,
            CharacterKey = inviter.ActiveCharacterKey,
            ContentId = inviter.Character.ContentId,
            CharacterName = inviter.Character.CharacterName,
            WorldId = (ushort)inviter.Character.WorldId,
        };
        if (!partyInviteGateway.TryArmAcceptance(expected, out var blocker))
            return (false, blocker);

        return (true,
            $"Native party invitation acceptance armed for exact inviter {expected.CharacterKey}; waiting for fresh invitation and PartyList confirmation.");
    }

    private void SetRequestedJobPreparation(DadRequestedJobPreparationKey key)
    {
        if (requestedJobPreparationKey.HasValue &&
            DadRequestedJobPreparationKeyRules.Matches(requestedJobPreparationKey.Value, key))
        {
            return;
        }

        requestedJobPreparationGate.Reset();
        requestedJobPreparationKey = key;
        CurrentParticipant.RequestedJobPreparation = null;
        lastRequestedJobPreparationTransition = string.Empty;
    }

    private void AdvanceRequestedJobPreparation(DadAcquiredCharacter? localCharacter)
    {
        if (!requestedJobPreparationKey.HasValue)
        {
            CurrentParticipant.RequestedJobPreparation = null;
            return;
        }

        var expected = requestedJobPreparationKey.Value;
        if (localCharacter == null)
        {
            // Logout/loading is an ordinary wait. Retain the exact assignment,
            // proof, and actual-call allowance so a reconnect cannot gain extra
            // equip attempts or lose a previously terminal best-effort result.
            CurrentParticipant.RequestedJobPreparation = requestedJobPreparationGate.TryGet(expected, out var retained)
                ? retained
                : null;
            return;
        }

        var observedIdentity = new DadRequestedJobPreparationKey(
            currentRunId,
            WorkerSessionId,
            assignedSlotId,
            CurrentParticipant.ManagedAccountKey,
            CurrentParticipant.ActiveCharacterKey,
            CurrentParticipant.Character.ContentId,
            expected.RequiredJobId);

        // A worker may accept a wake while AutoRetainer is still loading the requested character.
        // Wrong/loading characters are ordinary waits and consume no attempts.
        // A retained proof is never projected as readiness for the wrong slot
        // identity by the frozen manifest boundary.
        if (!DadRequestedJobPreparationKeyRules.Matches(expected, observedIdentity))
        {
            CurrentParticipant.RequestedJobPreparation = requestedJobPreparationGate.TryGet(expected, out var retained)
                ? retained
                : null;
            return;
        }

        if (!CurrentParticipant.PostArReady &&
            !requestedJobPreparationGate.TryGet(expected, out _))
        {
            CurrentParticipant.RequestedJobPreparation = null;
            return;
        }

        var (safeToEquip, unsafeReason) = CurrentParticipant.PostArReady
            ? EvaluateRequestedJobEquipSafety(localCharacter)
            : (false, "The exact character is waiting for post-AR readiness.");
        var nowUtc = DateTime.UtcNow;
        var observation = new DadRequestedJobPreparationObservation(
            observedIdentity,
            CurrentParticipant.Character.CurrentJobId.GetValueOrDefault(),
            safeToEquip,
            GearsetCatalog: null,
            unsafeReason);
        if (requestedJobPreparationGate.NeedsGearsetCatalog(expected, observation, nowUtc))
            observation = observation with { GearsetCatalog = classJobGearsetGateway.ReadCatalog() };

        var proof = requestedJobPreparationGate.Advance(
            expected,
            observation,
            nowUtc,
            expected.RequiredJobId.HasValue
                ? gearsetId => classJobGearsetGateway.TryEquip(gearsetId, expected.RequiredJobId.Value)
                : null);
        CurrentParticipant.RequestedJobPreparation = proof;

        var transition = string.Join(
            "|",
            proof.Status,
            proof.AttemptCount,
            proof.SelectedGearsetId,
            proof.FailureReason);
        if (!string.Equals(transition, lastRequestedJobPreparationTransition, StringComparison.Ordinal))
        {
            lastRequestedJobPreparationTransition = transition;
            var permitsReadiness = DadRequestedJobPreparationProofRules.PermitsReadiness(
                proof,
                expected,
                CurrentParticipant.Character.CurrentJobId.GetValueOrDefault());
            log.Information(
                "[dad] Requested-job preparation outcome request={RequestId} slot={SlotId} account={AccountKey} character={CharacterKey} contentId={ContentId} worker={WorkerSessionId} requestedJob={RequestedJobId} currentJob={CurrentJobId} status={Status} terminal={Terminal} permitsParty={PermitsParty} gearset={GearsetId} attempt={AttemptCount}/{MaxAttemptCount} summary={Summary}.",
                expected.RunId,
                expected.SlotId,
                expected.AccountKey,
                expected.CharacterKey,
                expected.ContentId,
                expected.WorkerSessionId,
                expected.RequiredJobId?.ToString() ?? "(current)",
                CurrentParticipant.Character.CurrentJobId.GetValueOrDefault(),
                proof.Status,
                DadRequestedJobPreparationProofRules.IsTerminal(proof.Status),
                permitsReadiness,
                proof.SelectedGearsetId?.ToString() ?? "(none)",
                proof.AttemptCount,
                DadRequestedJobPreparationGate.MaxAttemptCount,
                proof.Summary);
            if (proof.Status == DadRequestedJobPreparationStatus.SoftFailed)
            {
                var warning = $"{expected.SlotId} could not switch to requested job {expected.RequiredJobId}; continuing on current job {CurrentParticipant.Character.CurrentJobId.GetValueOrDefault()}: {proof.FailureReason}";
                AddWarning(warning);
            }
            else if (proof.Status == DadRequestedJobPreparationStatus.Cancelled)
            {
                log.Warning(
                    "[dad] Requested-job preparation cancelled for {RunId}/{SlotId}/{CharacterKey}: {Reason}",
                    expected.RunId,
                    expected.SlotId,
                    expected.CharacterKey,
                    proof.FailureReason);
            }
        }

        if (proof.Status is DadRequestedJobPreparationStatus.Pending or DadRequestedJobPreparationStatus.AwaitingVerification)
            CurrentParticipant.StatusText = proof.Summary;
    }

    private static (bool Safe, string Reason) EvaluateRequestedJobEquipSafety(DadAcquiredCharacter? localCharacter)
    {
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null || localCharacter == null)
            return (false, "The local character is not fully logged in.");

        if (TryGetUnsafeWorldCondition(out var label))
            return (false, $"The client is {label}.");

        return (true, string.Empty);
    }

    private void ResetRequestedJobPreparation()
    {
        requestedJobPreparationGate.Reset();
        requestedJobPreparationKey = null;
        CurrentParticipant.RequestedJobPreparation = null;
        lastRequestedJobPreparationTransition = string.Empty;
    }

    private void ResetRunContext()
    {
        partyInviteGateway.Reset();
        ResetRequestedJobPreparation();
        currentRunId = string.Empty;
        currentAuthorityWorkerSessionId = new DadWorkerSessionId(string.Empty);
        currentAuthorityMode = configuration.LocalOnlyModeEnabled ? DadAuthorityMode.LocalOnly : DadAuthorityMode.ServerDad;
        requiredAccountKey = new DadAccountKey(string.Empty);
        requiredCharacterKey = new DadCharacterKey(string.Empty);
        assignedSlotId = string.Empty;
    }
}
