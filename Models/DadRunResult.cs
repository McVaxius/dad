using dad.Services;

namespace dad.Models;

public sealed class DadRunResult
{
    public string RequestId { get; set; } = string.Empty;
    public DadRunStatus Status { get; set; } = DadRunStatus.Idle;
    public DadRunPhase Phase { get; set; } = DadRunPhase.Idle;
    public DadOrchestrationRole Role { get; set; } = DadOrchestrationRole.None;
    public DadWorkerRole WorkerRole { get; set; } = DadWorkerRole.None;
    public DadAuthorityMode AuthorityMode { get; set; } = DadAuthorityMode.ServerDad;
    public DadRunCancellationState CancellationState { get; set; } = DadRunCancellationState.None;
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public DadTransportMode TransportMode { get; set; } = DadTransportMode.LocalOnly;
    public bool LocalOnlyEnabled { get; set; }
    public string LeaderClientInstanceId { get; set; } = string.Empty;
    public DadWorkerSessionId AuthorityWorkerSessionId { get; set; } = new(string.Empty);
    public string AuthorityEndpoint { get; set; } = string.Empty;
    public string LocalClientInstanceId { get; set; } = string.Empty;
    public DadWorkerSessionId LocalWorkerSessionId { get; set; } = new(string.Empty);
    public string RequestedBy { get; set; } = string.Empty;
    public int RequestedTaskCount { get; set; }
    public int CompletedTaskCount { get; set; }
    public int ActiveTaskIndex { get; set; }
    public int TotalTaskCount { get; set; }
    public string ActiveTaskName { get; set; } = string.Empty;
    public string ActiveTaskStatus { get; set; } = string.Empty;
    public string BlockedReason { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public string Summary { get; set; } = "Idle";
    public DadScheduleFailureKind ScheduleFailureKind { get; set; }
    public DadRunRequest? Request { get; set; }
    public DadRunStopProgress StopProgress { get; set; } = new();
    public DadModuleExecutionStatusDto CurrentExecutorStatus { get; set; } = new();
    public List<DadParticipantSnapshot> Participants { get; set; } = [];
    public List<DadParticipantLeaseRecord> Leases { get; set; } = [];
    public List<DadRunStepResultDto> StepResults { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public DateTime? CompletedAtUtc { get; set; }

    public bool IsTerminal =>
        Status is DadRunStatus.Rejected or DadRunStatus.Completed or DadRunStatus.Failed or DadRunStatus.Cancelled or DadRunStatus.PartialFailure or DadRunStatus.TimedOut;

    public static DadRunResult Idle() => new()
    {
        Status = DadRunStatus.Idle,
        Phase = DadRunPhase.Idle,
        Summary = "Idle",
    };

    public static DadRunResult Rejected(DadRunRequest? request, string reason) => new()
    {
        RequestId = request?.RequestId ?? Guid.NewGuid().ToString("N"),
        Status = DadRunStatus.Rejected,
        Phase = DadRunPhase.Planning,
        ModuleId = request?.Orchestration?.ModuleTarget ?? DadModuleId.None,
        AuthorityMode = request?.Orchestration?.AuthorityMode ?? DadAuthorityMode.ServerDad,
        LocalOnlyEnabled = request?.Orchestration?.LocalOnlyOverride ?? false,
        TransportMode = request?.Orchestration?.TransportMode ?? DadTransportMode.LocalOnly,
        RequestedBy = request?.RequestedBy ?? string.Empty,
        RequestedTaskCount = request?.GetConfiguredTaskCount() ?? 0,
        TotalTaskCount = request?.GetConfiguredTaskCount() ?? 0,
        StopProgress = DadRunStopProgress.FromPolicy(request?.StopPolicy),
        FailureReason = reason,
        Summary = reason,
        Request = request,
        CompletedAtUtc = DateTime.UtcNow,
    };

    public static DadRunResult FromRequest(DadRunRequest request, DadRunStatus status, string summary) => new()
    {
        RequestId = request.RequestId,
        Status = status,
        Phase = DadRunPhase.Planning,
        ModuleId = request.ApplyOrchestrationDefaults().ModuleTarget,
        AuthorityMode = request.Orchestration.AuthorityMode,
        LocalOnlyEnabled = request.Orchestration.LocalOnlyOverride,
        TransportMode = request.Orchestration.TransportMode,
        RequestedBy = request.RequestedBy,
        RequestedTaskCount = request.GetConfiguredTaskCount(),
        TotalTaskCount = request.GetConfiguredTaskCount(),
        StopProgress = DadRunStopProgress.FromPolicy(request.StopPolicy),
        Summary = summary,
        Request = request,
    };

    public static DadRunResult FromPlan(DadRunPlan plan, DadRunStatus status, string summary) => new()
    {
        RequestId = plan.Request.RequestId,
        Status = status,
        Phase = DadRunPhase.Planning,
        Role = DadOrchestrationRole.Leader,
        WorkerRole = DadWorkerRole.ServerDad,
        AuthorityMode = plan.Orchestration.AuthorityMode,
        LocalOnlyEnabled = plan.Orchestration.LocalOnlyOverride,
        ModuleId = plan.CompositeModuleId,
        TransportMode = plan.Orchestration.TransportMode,
        RequestedBy = plan.Request.RequestedBy,
        RequestedTaskCount = plan.Modules.Count,
        TotalTaskCount = plan.Modules.Count,
        StopProgress = DadRunStopProgress.FromPolicy(plan.Request.StopPolicy),
        Summary = summary,
        Request = plan.Request,
        Warnings = [..plan.PlannerWarnings],
    };

    public DadRunResult Clone() => new()
    {
        RequestId = RequestId,
        Status = Status,
        Phase = Phase,
        Role = Role,
        WorkerRole = WorkerRole,
        AuthorityMode = AuthorityMode,
        CancellationState = CancellationState,
        ModuleId = ModuleId,
        TransportMode = TransportMode,
        LocalOnlyEnabled = LocalOnlyEnabled,
        LeaderClientInstanceId = LeaderClientInstanceId,
        AuthorityWorkerSessionId = AuthorityWorkerSessionId,
        AuthorityEndpoint = AuthorityEndpoint,
        LocalClientInstanceId = LocalClientInstanceId,
        LocalWorkerSessionId = LocalWorkerSessionId,
        RequestedBy = RequestedBy,
        RequestedTaskCount = RequestedTaskCount,
        CompletedTaskCount = CompletedTaskCount,
        ActiveTaskIndex = ActiveTaskIndex,
        TotalTaskCount = TotalTaskCount,
        ActiveTaskName = ActiveTaskName,
        ActiveTaskStatus = ActiveTaskStatus,
        BlockedReason = BlockedReason,
        FailureReason = FailureReason,
        Summary = Summary,
        ScheduleFailureKind = ScheduleFailureKind,
        Request = DadIpcJson.DeepClone(Request),
        StopProgress = StopProgress.Clone(),
        CurrentExecutorStatus = CurrentExecutorStatus.Clone(),
        Participants = Participants.Select(static participant => participant.Clone()).ToList(),
        Leases = Leases.Select(static lease => lease.Clone()).ToList(),
        StepResults = StepResults.Select(static step => step.Clone()).ToList(),
        Warnings = [..Warnings],
        CompletedAtUtc = CompletedAtUtc,
    };
}

public sealed class DadRunStopProgress
{
    public DadRunStopPolicy StopPolicy { get; set; } = new();
    public int StartedRuns { get; set; }
    public int CompletedRuns { get; set; }
    public int SafetyCap { get; set; } = 1;
    public int? CurrentLevel { get; set; }
    public string ResolvedLevelTargetEvidence { get; set; } = string.Empty;
    public uint? RestedExperience { get; set; }
    public bool StopReached { get; set; }
    public bool SafetyCapReached { get; set; }
    public string Summary { get; set; } = "Stop policy: after 1 run.";

    public static DadRunStopProgress FromPolicy(DadRunStopPolicy? policy)
    {
        var normalized = (policy ?? new DadRunStopPolicy()).Clone().Normalize();
        return new DadRunStopProgress
        {
            StopPolicy = normalized,
            SafetyCap = normalized.GetSafetyCap(),
            Summary = $"Stop policy: {normalized.Describe()}.",
        };
    }

    public DadRunStopProgress Clone()
        => new()
        {
            StopPolicy = StopPolicy.Clone(),
            StartedRuns = StartedRuns,
            CompletedRuns = CompletedRuns,
            SafetyCap = SafetyCap,
            CurrentLevel = CurrentLevel,
            ResolvedLevelTargetEvidence = ResolvedLevelTargetEvidence,
            RestedExperience = RestedExperience,
            StopReached = StopReached,
            SafetyCapReached = SafetyCapReached,
            Summary = Summary,
        };
}
