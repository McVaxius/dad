using System.Collections.Concurrent;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadWorkerExecutionService
{
    private readonly DadQueueExecutionService queueExecutionService;
    private readonly DadPresenceService presenceService;
    private readonly ICondition condition;
    private readonly ConcurrentQueue<DadWorkerExecutionCommand> pendingCommands = new();
    private readonly object stateLock = new();

    private DadWorkerExecutionCommand? activeCommand;
    private DadWorkerExecutionStatus status = new();
    private DateTime startedAtUtc = DateTime.MinValue;
    private bool enteredDuty;

    public DadWorkerExecutionService(
        DadQueueExecutionService queueExecutionService,
        DadPresenceService presenceService,
        ICondition condition)
    {
        this.queueExecutionService = queueExecutionService;
        this.presenceService = presenceService;
        this.condition = condition;
        status.WorkerSessionId = presenceService.WorkerSessionId;
    }

    public DadWorkerExecutionAck Accept(DadWorkerExecutionCommand command)
    {
        lock (stateLock)
        {
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
            if (activeCommand == null ||
                !string.Equals(activeCommand.RunId, cancel.RunId, StringComparison.OrdinalIgnoreCase))
            {
                return new DadWorkerExecutionAck
                {
                    RunId = cancel.RunId,
                    WorkerSessionId = presenceService.WorkerSessionId,
                    Accepted = false,
                    Summary = "Worker has no matching run-owned execution.",
                    Status = status.Clone(),
                };
            }

            if (activeCommand.Role == DadWorkerExecutionRole.QueueLeader ||
                status.ModuleId == DadModuleId.Mogtome)
                queueExecutionService.CancelActiveExecutor(cancel.Reason);

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

        if (command.Role == DadWorkerExecutionRole.Participant)
        {
            if (module.ModuleId == DadModuleId.Mogtome)
            {
                queueExecutionService.SetWorkerRole(command.Role);
                ApplyLeaderResult(queueExecutionService.ExecuteModule(command.Plan, module, command.Participants));
                return;
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
            return;
        }

        status.State = DadWorkerExecutionState.WaitingForQueue;
        status.UpdatedAtUtc = DateTime.UtcNow;
    }

    private void Finish(DadWorkerExecutionState state, bool success, string summary, string failureReason)
    {
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
