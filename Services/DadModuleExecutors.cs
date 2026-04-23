using dad.Models;

namespace dad.Services;

public interface IDadModuleExecutor
{
    string ExecutorId { get; }
    DadModuleId ModuleId { get; }
    DadModuleExecutionStatusDto CanStart(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants);
    DadRunStepResultDto Start(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants);
    DadRunStepResultDto Update();
    DadRunStepResultDto Cancel(string reason);
    DadModuleExecutionStatusDto GetStatus();
}

public abstract class DadDeferredModuleExecutor : IDadModuleExecutor
{
    private readonly DadModuleRegistry moduleRegistry;
    private readonly Func<DadRunPlan, string> queueBlockerFactory;
    private DadModuleExecutionStatusDto status = new();

    protected DadDeferredModuleExecutor(
        DadModuleRegistry moduleRegistry,
        string executorId,
        DadModuleId moduleId,
        string displayName,
        Func<DadRunPlan, string> queueBlockerFactory)
    {
        this.moduleRegistry = moduleRegistry;
        this.queueBlockerFactory = queueBlockerFactory;
        ExecutorId = executorId;
        ModuleId = moduleId;
        DisplayName = displayName;
    }

    public string ExecutorId { get; }
    public DadModuleId ModuleId { get; }
    protected string DisplayName { get; }

    public DadModuleExecutionStatusDto CanStart(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var module = ResolveModule(plan);
        var capability = moduleRegistry.GetCapability(module.ModuleId);
        var blockers = BuildCapabilityBlockers(plan, module, capability, participants);
        var hardBlocked = blockers.Any(static blocker =>
            blocker.Severity is DadModuleBlockerSeverity.Blocked or DadModuleBlockerSeverity.Failed);
        var deferred = blockers.Any(static blocker => blocker.Severity == DadModuleBlockerSeverity.Deferred);

        return new DadModuleExecutionStatusDto
        {
            RunId = plan.Request.RequestId,
            ModuleId = module.ModuleId,
            DisplayName = module.DisplayName,
            Phase = DadRunPhase.QueuePreparing,
            Status = hardBlocked ? DadRunStatus.Failed : DadRunStatus.Running,
            StepName = ExecutorId,
            CanStart = !hardBlocked,
            Deferred = deferred,
            RetryAttempt = 0,
            MaxRetryAttempts = capability.CanRequeue ? 3 : 0,
            UpdatedAtUtc = DateTime.UtcNow,
            Summary = hardBlocked
                ? $"Dad cannot route {module.DisplayName}: {FormatBlockers(blockers)}"
                : deferred
                    ? $"Dad can route {module.DisplayName}, but live queue start remains deferred."
                    : $"Dad can start {module.DisplayName}.",
            BlockedReason = FormatBlockers(blockers),
            Blockers = blockers,
        };
    }

    public DadRunStepResultDto Start(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var nextStatus = CanStart(plan, participants);
        nextStatus.StartedAtUtc = DateTime.UtcNow;
        nextStatus.UpdatedAtUtc = nextStatus.StartedAtUtc.Value;
        nextStatus.CompletedAtUtc = nextStatus.CanStart ? nextStatus.StartedAtUtc : null;
        nextStatus.IsActive = false;
        nextStatus.Status = nextStatus.CanStart ? DadRunStatus.Completed : DadRunStatus.Failed;
        nextStatus.Summary = nextStatus.CanStart
            ? $"Dad routed {nextStatus.DisplayName} with {participants.Count}/{ResolveModule(plan).ExpectedPartySize} ready participant(s)."
            : nextStatus.Summary;
        nextStatus.FailureReason = nextStatus.CanStart ? string.Empty : nextStatus.BlockedReason;
        status = nextStatus;

        return new DadRunStepResultDto
        {
            RunId = plan.Request.RequestId,
            ModuleId = nextStatus.ModuleId,
            StepName = nextStatus.DisplayName,
            ParticipantState = nextStatus.CanStart ? DadParticipantState.QueuePending : DadParticipantState.Failed,
            Success = nextStatus.CanStart,
            Deferred = nextStatus.Deferred,
            TimedOut = false,
            Summary = nextStatus.Summary,
            FailureReason = nextStatus.FailureReason,
            BlockedReason = nextStatus.BlockedReason,
            ExecutorStatus = nextStatus.Clone(),
            ModuleBlockers = nextStatus.Blockers.Select(static blocker => blocker.Clone()).ToList(),
            ReportedAtUtc = DateTime.UtcNow,
        };
    }

    public DadRunStepResultDto Update()
        => BuildStatusStep(status);

    public DadRunStepResultDto Cancel(string reason)
    {
        status.Status = DadRunStatus.Cancelled;
        status.Phase = DadRunPhase.Finalizing;
        status.IsActive = false;
        status.CompletedAtUtc = DateTime.UtcNow;
        status.UpdatedAtUtc = status.CompletedAtUtc.Value;
        status.Summary = string.IsNullOrWhiteSpace(reason) ? $"{DisplayName} executor cancelled." : reason;

        return BuildStatusStep(status);
    }

    public DadModuleExecutionStatusDto GetStatus()
        => status.Clone();

    protected virtual DadPlannedModuleExecution ResolveModule(DadRunPlan plan)
        => plan.Modules.FirstOrDefault(module => module.ModuleId == ModuleId)
           ?? plan.Modules.FirstOrDefault()
           ?? new DadPlannedModuleExecution
           {
               ModuleId = ModuleId,
               DisplayName = DisplayName,
               ExpectedPartySize = Math.Max(1, plan.RequiredParticipantCount),
           };

    private List<DadModuleBlockerDto> BuildCapabilityBlockers(
        DadRunPlan plan,
        DadPlannedModuleExecution module,
        DadModuleCapabilitySnapshot capability,
        IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var blockers = new List<DadModuleBlockerDto>();
        if (participants.Count < module.ExpectedPartySize)
        {
            blockers.Add(new DadModuleBlockerDto
            {
                ModuleId = module.ModuleId,
                Capability = "Participants",
                Severity = DadModuleBlockerSeverity.Failed,
                Summary = $"Need {module.ExpectedPartySize} participant(s), have {participants.Count}.",
            });
        }

        if (!capability.CanPlan)
            blockers.Add(BuildBlocker(module.ModuleId, "CanPlan", "Module cannot plan yet.", DadModuleBlockerSeverity.Blocked));

        if (module.ExpectedPartySize > 1 && !capability.CanAssembleParty)
            blockers.Add(BuildBlocker(module.ModuleId, "CanAssembleParty", "Module cannot assemble party yet.", DadModuleBlockerSeverity.Blocked));

        if (!capability.CanStartQueue)
            blockers.Add(BuildBlocker(module.ModuleId, "CanStartQueue", queueBlockerFactory(plan)));

        if (!capability.CanTrackCompletion)
            blockers.Add(BuildBlocker(module.ModuleId, "CanTrackCompletion", "Completion tracking is not enabled for this module."));

        if (!capability.CanRequeue && AllowsRepeatedWork(plan))
            blockers.Add(BuildBlocker(module.ModuleId, "CanRequeue", "Requeue/retry loop is not enabled for this module."));

        return blockers
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker.Summary))
            .GroupBy(static blocker => $"{blocker.ModuleId}|{blocker.Capability}|{blocker.Summary}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    private static bool AllowsRepeatedWork(DadRunPlan plan)
        => (plan.Request.Dungeon?.Count ?? 0) > 1
           || (plan.Request.Msq?.Attempts ?? 0) > 1
           || (plan.Request.DutySupport?.Attempts ?? 0) > 1
           || (plan.Request.Trust?.Attempts ?? 0) > 1
           || (plan.Request.PremadeDuty?.Attempts ?? 0) > 1
           || (plan.Request.Blunderville?.Attempts ?? 0) > 1
           || (plan.Request.Mogtome?.Attempts ?? 0) > 1
           || (plan.Request.Commendation?.Attempts ?? 0) > 1
           || (plan.Request.Astrope?.Attempts ?? 0) > 1
           || (plan.Request.CustomDuty?.Attempts ?? 0) > 1;

    private static DadModuleBlockerDto BuildBlocker(
        DadModuleId moduleId,
        string capability,
        string summary,
        DadModuleBlockerSeverity severity = DadModuleBlockerSeverity.Deferred)
        => new()
        {
            ModuleId = moduleId,
            Capability = capability,
            Severity = severity,
            Summary = summary,
        };

    private static DadRunStepResultDto BuildStatusStep(DadModuleExecutionStatusDto status)
        => new()
        {
            RunId = status.RunId,
            ModuleId = status.ModuleId,
            StepName = status.DisplayName,
            ParticipantState = status.Status == DadRunStatus.Cancelled ? DadParticipantState.Cancelled : DadParticipantState.QueuePending,
            Success = status.Status is DadRunStatus.Running or DadRunStatus.Completed,
            Deferred = status.Deferred,
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

public sealed class DadLocalDutyExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadLocalDutyExecutor", DadModuleId.Duty, "Local Duty", queueBlockerFactory);

public sealed class DadPremadeDutyExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadPremadeDutyExecutor", DadModuleId.PremadeDuty, "Premade Duty", queueBlockerFactory);

public sealed class DadMsqExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadMsqExecutor", DadModuleId.Msq, "MSQ", queueBlockerFactory);

public sealed class DadDutySupportExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadDutySupportExecutor", DadModuleId.DutySupport, "Duty Support", queueBlockerFactory);

public sealed class DadTrustExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadTrustExecutor", DadModuleId.Trust, "Trust", queueBlockerFactory);

public sealed class DadDailyMsqExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadDailyMsqExecutor", DadModuleId.DailyMsq, "Daily MSQ", queueBlockerFactory);

public sealed class DadBlundervilleExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadBlundervilleExecutor", DadModuleId.Blunderville, "Blunderville", queueBlockerFactory);

public sealed class DadMogtomeExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadMogtomeExecutor", DadModuleId.Mogtome, "MOGTOME", queueBlockerFactory);

public sealed class DadCommendationExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadCommendationExecutor", DadModuleId.Commendation, "Commendation", queueBlockerFactory);

public sealed class DadAstropeExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadAstropeExecutor", DadModuleId.Astrope, "Astrope", queueBlockerFactory);

public sealed class DadCustomDutyExecutor(
    DadModuleRegistry moduleRegistry,
    Func<DadRunPlan, string> queueBlockerFactory)
    : DadDeferredModuleExecutor(moduleRegistry, "DadCustomDutyExecutor", DadModuleId.CustomDuty, "Custom Duty", queueBlockerFactory);
