namespace dad.Models;

public enum DadOrchestrationRole
{
    None,
    Leader,
    Participant,
}

public enum DadAuthorityMode
{
    ServerDad,
    LocalOnly,
}

public enum DadWorkerRole
{
    None,
    ServerDad,
    ClientDad,
}

public readonly record struct DadWorkerSessionId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value ?? string.Empty;

    public static implicit operator string(DadWorkerSessionId key) => key.Value ?? string.Empty;

    public static implicit operator DadWorkerSessionId(string value) => new(value ?? string.Empty);
}

public readonly record struct DadAccountKey(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value ?? string.Empty;

    public static implicit operator string(DadAccountKey key) => key.Value ?? string.Empty;

    public static implicit operator DadAccountKey(string value) => new(value ?? string.Empty);
}

public readonly record struct DadCharacterKey(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value ?? string.Empty;

    public static implicit operator string(DadCharacterKey key) => key.Value ?? string.Empty;

    public static implicit operator DadCharacterKey(string value) => new(value ?? string.Empty);
}

public enum DadParticipantState
{
    Unknown,
    Idle,
    Discovered,
    Assigned,
    WaitingForRequiredCharacter,
    WaitingForPostArReady,
    Ready,
    Claimed,
    AssemblyPending,
    AssemblyConfirmed,
    QueuePending,
    Running,
    Completed,
    Failed,
    Cancelled,
    Stale,
}

public enum DadClaimState
{
    None,
    Pending,
    Granted,
    Denied,
    Released,
    Collided,
    Stale,
}

public enum DadParticipantLeaseState
{
    None,
    Pending,
    Granted,
    Denied,
    Released,
    Collided,
    Stale,
}

public enum DadRunCancellationState
{
    None,
    Requested,
    Cancelling,
    Acknowledged,
    Finalized,
}

public enum DadModuleId
{
    None,
    Duty,
    Msq,
    DutySupport,
    Trust,
    PremadeDuty,
    DailyMsq,
    Blunderville,
    Mogtome,
    Commendation,
    Astrope,
    CustomDuty,
    Squadron,
    VariantVvd,
    Mixed,
}

public enum DadTransportMode
{
    LocalOnly,
    ServerHub,
    [Obsolete("Use ServerHub. Numeric value retained for stored configuration compatibility.")]
    LocalhostHybrid = ServerHub,
}

public enum DadRunPhase
{
    Idle,
    Planning,
    DiscoveringParticipants,
    WaitingForReadiness,
    ClaimingSlots,
    AssemblingParty,
    RoutingModules,
    QueuePreparing,
    QueueStarting,
    WaitingForQueuePop,
    InDutyOrTask,
    PostRunStabilizing,
    RequeueOrComplete,
    Finalizing,
}

public enum DadModuleBlockerSeverity
{
    Info,
    Deferred,
    Blocked,
    Failed,
}

public enum DadAssemblyInstructionKind
{
    None,
    ConfirmCharacter,
    TravelToPullPoint,
    FormParty,
    JoinParty,
    ReadyCheck,
}

public sealed class DadRunWaitPolicy
{
    public int ParticipantReadyTimeoutSeconds { get; set; } = 300;
    public int AssemblyTimeoutSeconds { get; set; } = 120;
    public int HeartbeatStaleSeconds { get; set; } = 12;
    public int LeaseDurationSeconds { get; set; } = 20;
    public int CancelAckTimeoutSeconds { get; set; } = 6;

    public TimeSpan GetParticipantReadyTimeout()
        => TimeSpan.FromSeconds(Math.Max(30, ParticipantReadyTimeoutSeconds));

    public TimeSpan GetAssemblyTimeout()
        => TimeSpan.FromSeconds(Math.Max(10, AssemblyTimeoutSeconds));

    public TimeSpan GetHeartbeatStaleThreshold()
        => TimeSpan.FromSeconds(Math.Max(3, HeartbeatStaleSeconds));

    public TimeSpan GetLeaseDuration()
        => TimeSpan.FromSeconds(Math.Max(5, LeaseDurationSeconds));

    public TimeSpan GetCancelAckTimeout()
        => TimeSpan.FromSeconds(Math.Max(2, CancelAckTimeoutSeconds));
}

public sealed class DadOrchestrationIntent
{
    public DadAuthorityMode AuthorityMode { get; set; } = DadAuthorityMode.ServerDad;
    public bool LocalOnlyOverride { get; set; }
    public DadModuleId ModuleTarget { get; set; } = DadModuleId.None;
    public DadQueueAuthority QueueAuthority { get; set; } = DadQueueAuthority.Leader;
    public DadInviteAuthority InviteAuthority { get; set; } = DadInviteAuthority.PresetLeader;
    public DadTransportMode TransportMode { get; set; } = DadTransportMode.LocalOnly;
    public DadRosterIntent RosterIntent { get; set; } = new();
    public bool RequirePostArReady { get; set; } = true;
    public bool PreferTypedRosterPool { get; set; } = true;
    public DadCharacterKey PreferredLeaderCharacterKey { get; set; } = new(string.Empty);
    public DadCharacterKey PreferredInviterCharacterKey { get; set; } = new(string.Empty);
    public List<DadAccountKey> PreferredAccountKeys { get; set; } = [];
    public List<DadAccountKey> RequiredAccountKeys { get; set; } = [];
    public List<DadRosterCharacterRef> PreferredRosterCharacters { get; set; } = [];
    public List<DadRosterCharacterRef> RequiredRosterCharacters { get; set; } = [];
    public List<DadCharacterKey> PreferredCharacterKeys { get; set; } = [];
    public List<DadCharacterKey> RequiredCharacterKeys { get; set; } = [];
    public DadRunWaitPolicy WaitPolicy { get; set; } = new();
    public string ExecutionConstraintSummary { get; set; } = string.Empty;
}

public sealed class DadRosterIntent
{
    public int ExpectedPartySize { get; set; } = 1;
    public bool RequireRemoteParticipants { get; set; }
    public bool AllowStoredXadbFallback { get; set; } = true;
    public bool RequireExactCharacters { get; set; }
}

public sealed class DadParticipantSnapshot
{
    public string ClientInstanceId { get; set; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public string MachineName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public DadAuthorityMode AuthorityMode { get; set; } = DadAuthorityMode.ServerDad;
    public DadOrchestrationRole Role { get; set; } = DadOrchestrationRole.None;
    public DadWorkerRole WorkerRole { get; set; } = DadWorkerRole.None;
    public DadParticipantState State { get; set; } = DadParticipantState.Unknown;
    public DadClaimState ClaimState { get; set; } = DadClaimState.None;
    public DadParticipantLeaseState LeaseState { get; set; } = DadParticipantLeaseState.None;
    public DadRunCancellationState CancellationState { get; set; } = DadRunCancellationState.None;
    public bool IsLocalClient { get; set; }
    public bool IsAuthority { get; set; }
    public bool IsAvailable { get; set; }
    public bool IsEligibleForRun { get; set; } = true;
    public bool PostArReady { get; set; }
    public bool WorldReadyStable { get; set; }
    public bool AutoRetainerAvailable { get; set; }
    public bool AutoRetainerBusy { get; set; }
    public bool AutoRetainerMultiModeEnabled { get; set; }
    public bool ExternalAutomationHeld { get; set; }
    public string ExternalAutomationActivity { get; set; } = string.Empty;
    public string ExternalAutomationState { get; set; } = string.Empty;
    public string ExternalAutomationSummary { get; set; } = string.Empty;
    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;
    public DadAccountKey ManagedAccountKey { get; set; } = new(string.Empty);
    public string ManagedAccountAlias { get; set; } = string.Empty;
    public DadCharacterKey ActiveCharacterKey { get; set; } = new(string.Empty);
    public List<DadCharacterKey> AvailableCharacterKeys { get; set; } = [];
    public DadAcquiredCharacter Character { get; set; } = new();
    public string AssignedSlotId { get; set; } = string.Empty;
    public string DesiredCharacterKey { get; set; } = string.Empty;
    public DateTime? LeaseIssuedUtc { get; set; }
    public DateTime? LeaseRenewedUtc { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public List<string> Warnings { get; set; } = [];
    public string StatusText { get; set; } = string.Empty;

    public DadParticipantSnapshot Clone() => new()
    {
        ClientInstanceId = ClientInstanceId,
        WorkerSessionId = WorkerSessionId,
        MachineName = MachineName,
        ProcessId = ProcessId,
        Endpoint = Endpoint,
        RunId = RunId,
        AuthorityMode = AuthorityMode,
        Role = Role,
        WorkerRole = WorkerRole,
        State = State,
        ClaimState = ClaimState,
        LeaseState = LeaseState,
        CancellationState = CancellationState,
        IsLocalClient = IsLocalClient,
        IsAuthority = IsAuthority,
        IsAvailable = IsAvailable,
        IsEligibleForRun = IsEligibleForRun,
        PostArReady = PostArReady,
        WorldReadyStable = WorldReadyStable,
        AutoRetainerAvailable = AutoRetainerAvailable,
        AutoRetainerBusy = AutoRetainerBusy,
        AutoRetainerMultiModeEnabled = AutoRetainerMultiModeEnabled,
        ExternalAutomationHeld = ExternalAutomationHeld,
        ExternalAutomationActivity = ExternalAutomationActivity,
        ExternalAutomationState = ExternalAutomationState,
        ExternalAutomationSummary = ExternalAutomationSummary,
        LastHeartbeatUtc = LastHeartbeatUtc,
        ManagedAccountKey = ManagedAccountKey,
        ManagedAccountAlias = ManagedAccountAlias,
        ActiveCharacterKey = ActiveCharacterKey,
        AvailableCharacterKeys = [..AvailableCharacterKeys],
        Character = Character.Clone(),
        AssignedSlotId = AssignedSlotId,
        DesiredCharacterKey = DesiredCharacterKey,
        LeaseIssuedUtc = LeaseIssuedUtc,
        LeaseRenewedUtc = LeaseRenewedUtc,
        LeaseExpiresUtc = LeaseExpiresUtc,
        Warnings = [..Warnings],
        StatusText = StatusText,
    };
}

public sealed class DadHeartbeatDto
{
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
    public DadParticipantSnapshot Participant { get; set; } = new();
}

public sealed class DadWakeRequestDto
{
    public string RunId { get; set; } = string.Empty;
    public DadWorkerSessionId AuthorityWorkerSessionId { get; set; } = new(string.Empty);
    public DadAuthorityMode AuthorityMode { get; set; } = DadAuthorityMode.ServerDad;
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public DadAccountKey RequiredAccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey RequiredCharacterKey { get; set; } = new(string.Empty);
    public string AssignedSlotId { get; set; } = string.Empty;
    public bool RequirePostArReady { get; set; } = true;
}

public sealed class DadParticipantReadyDto
{
    public string RunId { get; set; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public DadParticipantState State { get; set; } = DadParticipantState.Unknown;
    public bool PostArReady { get; set; }
    public bool AcceptedAssignment { get; set; }
    public string BlockerSummary { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public DadParticipantSnapshot Snapshot { get; set; } = new();
}

public sealed class DadParticipantLeaseRecord
{
    public string RunId { get; set; } = string.Empty;
    public string SlotId { get; set; } = string.Empty;
    public DadAccountKey AssignedAccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey AssignedCharacterKey { get; set; } = new(string.Empty);
    public DadWorkerSessionId OwningWorkerSessionId { get; set; } = new(string.Empty);
    public DateTime IssuedUtc { get; set; } = DateTime.UtcNow;
    public DateTime RenewedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; } = DateTime.UtcNow;
    public DadParticipantLeaseState State { get; set; } = DadParticipantLeaseState.None;
    public string Summary { get; set; } = string.Empty;

    public DadParticipantLeaseRecord Clone() => new()
    {
        RunId = RunId,
        SlotId = SlotId,
        AssignedAccountKey = AssignedAccountKey,
        AssignedCharacterKey = AssignedCharacterKey,
        OwningWorkerSessionId = OwningWorkerSessionId,
        IssuedUtc = IssuedUtc,
        RenewedUtc = RenewedUtc,
        ExpiresUtc = ExpiresUtc,
        State = State,
        Summary = Summary,
    };
}

public sealed class DadClaimRequestDto
{
    public string RunId { get; set; } = string.Empty;
    public DadWorkerSessionId AuthorityWorkerSessionId { get; set; } = new(string.Empty);
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public string SlotId { get; set; } = string.Empty;
    public DadAccountKey RequiredAccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey RequiredCharacterKey { get; set; } = new(string.Empty);
    public DadParticipantLeaseRecord Lease { get; set; } = new();
}

public sealed class DadClaimDecisionDto
{
    public string RunId { get; set; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public bool Granted { get; set; }
    public DadClaimState ClaimState { get; set; } = DadClaimState.None;
    public DadParticipantLeaseState LeaseState { get; set; } = DadParticipantLeaseState.None;
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public string Reason { get; set; } = string.Empty;
    public DadParticipantLeaseRecord Lease { get; set; } = new();
    public DadParticipantSnapshot Snapshot { get; set; } = new();
}

public sealed class DadAssemblyInstructionDto
{
    public string RunId { get; set; } = string.Empty;
    public DadWorkerSessionId AuthorityWorkerSessionId { get; set; } = new(string.Empty);
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public string SlotId { get; set; } = string.Empty;
    public DadCharacterKey RequiredCharacterKey { get; set; } = new(string.Empty);
    public DadAssemblyInstructionKind InstructionKind { get; set; } = DadAssemblyInstructionKind.None;
    public string Summary { get; set; } = string.Empty;
}

public sealed class DadPartyMemberSnapshot
{
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong ContentId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string WorldName { get; set; } = string.Empty;
    public bool IsLocalPlayer { get; set; }
}

public sealed class DadCancelCommandDto
{
    public string RunId { get; set; } = string.Empty;
    public DadWorkerSessionId AuthorityWorkerSessionId { get; set; } = new(string.Empty);
    public DadRunCancellationState CancellationState { get; set; } = DadRunCancellationState.Requested;
    public string Reason { get; set; } = string.Empty;
}

public sealed class DadCancelAckDto
{
    public string RunId { get; set; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public DadRunCancellationState CancellationState { get; set; } = DadRunCancellationState.Acknowledged;
    public bool Acknowledged { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DadParticipantSnapshot Snapshot { get; set; } = new();
}

public sealed class DadRunStepResultDto
{
    public string RunId { get; set; } = string.Empty;
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public string StepName { get; set; } = string.Empty;
    public DadParticipantState ParticipantState { get; set; } = DadParticipantState.Unknown;
    public bool Success { get; set; }
    public bool Deferred { get; set; }
    public bool TimedOut { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public string BlockedReason { get; set; } = string.Empty;
    public DadModuleExecutionStatusDto ExecutorStatus { get; set; } = new();
    public List<DadModuleBlockerDto> ModuleBlockers { get; set; } = [];
    public DateTime ReportedAtUtc { get; set; } = DateTime.UtcNow;

    public DadRunStepResultDto Clone() => new()
    {
        RunId = RunId,
        ModuleId = ModuleId,
        StepName = StepName,
        ParticipantState = ParticipantState,
        Success = Success,
        Deferred = Deferred,
        TimedOut = TimedOut,
        Summary = Summary,
        FailureReason = FailureReason,
        BlockedReason = BlockedReason,
        ExecutorStatus = ExecutorStatus.Clone(),
        ModuleBlockers = ModuleBlockers.Select(static blocker => blocker.Clone()).ToList(),
        ReportedAtUtc = ReportedAtUtc,
    };
}

public sealed class DadRunFinalResultDto
{
    public string RunId { get; set; } = string.Empty;
    public DadRunStatus Status { get; set; } = DadRunStatus.Idle;
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public string Summary { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public DateTime CompletedAtUtc { get; set; } = DateTime.UtcNow;
    public List<DadRunStepResultDto> Steps { get; set; } = [];
}

public sealed class DadModuleCapabilitySnapshot
{
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public string DisplayName { get; set; } = string.Empty;
    public string OwnerLabel { get; set; } = string.Empty;
    public int RequiredPartySize { get; set; }
    public bool RequiresPeers { get; set; }
    public bool SupportsLocalOnly { get; set; }
    public bool SupportsPremade { get; set; }
    public bool CanPlan { get; set; }
    public bool CanAssembleParty { get; set; }
    public bool CanStartQueue { get; set; }
    public bool CanTrackCompletion { get; set; }
    public bool CanRequeue { get; set; }
    public bool CanExecuteLiveQueue { get; set; }
    public string CurrentStatus { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public List<DadModuleBlockerDto> Blockers { get; set; } = [];
}

public sealed class DadModuleBlockerDto
{
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public string Capability { get; set; } = string.Empty;
    public DadModuleBlockerSeverity Severity { get; set; } = DadModuleBlockerSeverity.Deferred;
    public string Summary { get; set; } = string.Empty;

    public DadModuleBlockerDto Clone() => new()
    {
        ModuleId = ModuleId,
        Capability = Capability,
        Severity = Severity,
        Summary = Summary,
    };
}

public sealed class DadModuleExecutionStatusDto
{
    public string RunId { get; set; } = string.Empty;
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public string DisplayName { get; set; } = string.Empty;
    public DadRunPhase Phase { get; set; } = DadRunPhase.Idle;
    public DadRunStatus Status { get; set; } = DadRunStatus.Idle;
    public string StepName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool CanStart { get; set; }
    public bool Deferred { get; set; }
    public int RetryAttempt { get; set; }
    public int MaxRetryAttempts { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public string BlockedReason { get; set; } = string.Empty;
    public List<DadModuleBlockerDto> Blockers { get; set; } = [];

    public DadModuleExecutionStatusDto Clone() => new()
    {
        RunId = RunId,
        ModuleId = ModuleId,
        DisplayName = DisplayName,
        Phase = Phase,
        Status = Status,
        StepName = StepName,
        IsActive = IsActive,
        CanStart = CanStart,
        Deferred = Deferred,
        RetryAttempt = RetryAttempt,
        MaxRetryAttempts = MaxRetryAttempts,
        StartedAtUtc = StartedAtUtc,
        UpdatedAtUtc = UpdatedAtUtc,
        CompletedAtUtc = CompletedAtUtc,
        Summary = Summary,
        FailureReason = FailureReason,
        BlockedReason = BlockedReason,
        Blockers = Blockers.Select(static blocker => blocker.Clone()).ToList(),
    };
}

public sealed class DadModuleCapabilityQueryResult
{
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public List<DadModuleCapabilitySnapshot> Modules { get; set; } = [];
}

public sealed class DadParticipantStatusSnapshot
{
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string LocalClientInstanceId { get; set; } = string.Empty;
    public DadWorkerSessionId LocalWorkerSessionId { get; set; } = new(string.Empty);
    public DadWorkerRole LocalWorkerRole { get; set; } = DadWorkerRole.None;
    public DadTransportMode TransportMode { get; set; } = DadTransportMode.LocalOnly;
    public bool LocalOnlyModeEnabled { get; set; }
    public DadWorkerSessionId AuthorityWorkerSessionId { get; set; } = new(string.Empty);
    public string AuthorityEndpoint { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<DadParticipantSnapshot> Participants { get; set; } = [];
}

public sealed class DadPlannedModuleExecution
{
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public string DisplayName { get; set; } = string.Empty;
    public string OwnerLabel { get; set; } = string.Empty;
    public int ExpectedPartySize { get; set; } = 1;
    public bool RequiresPeers { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class DadRunPlan
{
    public DadRunRequest Request { get; set; } = new();
    public DadModuleId CompositeModuleId { get; set; } = DadModuleId.None;
    public DadOrchestrationIntent Orchestration { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public int RequiredParticipantCount { get; set; } = 1;
    public bool RequiresRemoteParticipants { get; set; }
    public string LeaderCharacterKey { get; set; } = string.Empty;
    public string InviterCharacterKey { get; set; } = string.Empty;
    public List<DadPlannedModuleExecution> Modules { get; set; } = [];
    public List<string> PlannerWarnings { get; set; } = [];
}
