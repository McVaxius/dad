using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadPresenceService
{
    private static readonly TimeSpan PartyJoinCommandCooldown = TimeSpan.FromSeconds(5);

    private readonly Configuration configuration;
    private readonly ConfigManager configManager;
    private readonly DadVermaxionIpcService vermaxion;
    private readonly DadAutoRetainerIpcService autoRetainer;
    private readonly IPluginLog log;
    private string currentRunId = string.Empty;
    private DadWorkerSessionId currentAuthorityWorkerSessionId = new(string.Empty);
    private DadAuthorityMode currentAuthorityMode = DadAuthorityMode.ServerDad;
    private DadAccountKey requiredAccountKey = new(string.Empty);
    private DadCharacterKey requiredCharacterKey = new(string.Empty);
    private string assignedSlotId = string.Empty;
    private DateTime lastPartyJoinCommandUtc = DateTime.MinValue;
    private string lastPartyJoinRunId = string.Empty;
    private string lastPartyJoinFailure = string.Empty;

    public DadPresenceService(
        Configuration configuration,
        ConfigManager configManager,
        DadVermaxionIpcService vermaxion,
        DadAutoRetainerIpcService autoRetainer,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.configManager = configManager;
        this.vermaxion = vermaxion;
        this.autoRetainer = autoRetainer;
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

    public void Update(DadCharacterPool pool, string endpoint = "")
    {
        // Stored/XADB rows describe the roster, not the character currently loaded in this client.
        // Falling back to one of them would keep a relogging client falsely available under the old identity.
        var localCharacter = pool.Characters.FirstOrDefault(static character => character.Source == DadCharacterSource.LocalRuntime);
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
            LeaseIssuedUtc = CurrentParticipant.LeaseIssuedUtc,
            LeaseRenewedUtc = CurrentParticipant.LeaseRenewedUtc,
            LeaseExpiresUtc = CurrentParticipant.LeaseExpiresUtc,
            Warnings = [..CurrentParticipant.Warnings],
            StatusText = BuildStatusText(localCharacter, postArReady, nextState, vermaxionStatus),
        };
    }

    public void MarkLeader(string runId, DadAuthorityMode authorityMode, string summary)
    {
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

        var acceptsAccount = requiredAccountKey.IsEmpty ||
                             string.Equals(CurrentParticipant.ManagedAccountKey, requiredAccountKey.ToString(), StringComparison.OrdinalIgnoreCase);
        var acceptsCharacter = requiredCharacterKey.IsEmpty ||
                               string.Equals(CurrentParticipant.ActiveCharacterKey, requiredCharacterKey.ToString(), StringComparison.OrdinalIgnoreCase);

        if (!acceptsAccount)
        {
            var mismatch = $"Wrong account active: need {requiredAccountKey}, have {CurrentParticipant.ManagedAccountKey}.";
            AddWarning(mismatch);
            CurrentParticipant.State = DadParticipantState.Assigned;
            CurrentParticipant.StatusText = mismatch;
            return BuildReadyResponse(blockerSummary: mismatch, acceptedAssignment: true);
        }

        if (!acceptsCharacter)
        {
            var mismatch = $"Waiting for required character {requiredCharacterKey}; active {CurrentParticipant.ActiveCharacterKey}.";
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
            var joinResult = TrySendPartyJoinCommand(instruction.RunId);
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

    private DadWorkerRole GetConfiguredWorkerRole()
        => configuration.RunAsServerDad ? DadWorkerRole.ServerDad : DadWorkerRole.ClientDad;

    private static bool EvaluateBasePostArReady(DadAcquiredCharacter? character)
    {
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null || character == null)
            return false;

        if (Plugin.Condition[ConditionFlag.BoundByDuty] ||
            Plugin.Condition[ConditionFlag.InDutyQueue] ||
            Plugin.Condition[ConditionFlag.WaitingForDuty] ||
            Plugin.Condition[ConditionFlag.WaitingForDutyFinder])
        {
            return false;
        }

        return character.Readiness == DadReadinessState.Ready;
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

    private (bool Success, string Summary) TrySendPartyJoinCommand(string runId)
    {
        if (string.Equals(lastPartyJoinRunId, runId, StringComparison.Ordinal) &&
            DateTime.UtcNow - lastPartyJoinCommandUtc < PartyJoinCommandCooldown)
        {
            if (!string.IsNullOrWhiteSpace(lastPartyJoinFailure))
                return (false, lastPartyJoinFailure);

            return (true, "Party join already requested; waiting for PartyList confirmation.");
        }

        const string command = "/pcmd join";
        try
        {
            lastPartyJoinRunId = runId;
            lastPartyJoinCommandUtc = DateTime.UtcNow;
            if (!Plugin.CommandManager.ProcessCommand(command))
            {
                lastPartyJoinFailure = "Command manager rejected /pcmd join.";
                return (false, "Command manager rejected /pcmd join.");
            }

            lastPartyJoinFailure = string.Empty;
            return (true, "Sent /pcmd join; waiting for PartyList confirmation.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Party join command threw.");
            lastPartyJoinFailure = $"Party join command threw: {ex.Message}";
            return (false, lastPartyJoinFailure);
        }
    }

    private void ResetRunContext()
    {
        currentRunId = string.Empty;
        currentAuthorityWorkerSessionId = new DadWorkerSessionId(string.Empty);
        currentAuthorityMode = configuration.LocalOnlyModeEnabled ? DadAuthorityMode.LocalOnly : DadAuthorityMode.ServerDad;
        requiredAccountKey = new DadAccountKey(string.Empty);
        requiredCharacterKey = new DadCharacterKey(string.Empty);
        assignedSlotId = string.Empty;
        lastPartyJoinRunId = string.Empty;
        lastPartyJoinCommandUtc = DateTime.MinValue;
        lastPartyJoinFailure = string.Empty;
    }
}
