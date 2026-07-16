namespace dad.Models;

public static class DadWorkerStatusPollingRules
{
    public static DadWorkerExecutionAck BuildQueuedAcknowledgement(
        DadWorkerExecutionCommand command,
        DadWorkerSessionId workerSessionId,
        DateTime updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(command);

        var status = new DadWorkerExecutionStatus
        {
            CommandId = command.CommandId,
            RunId = command.RunId,
            WorkerSessionId = workerSessionId,
            Role = command.Role,
            ModuleId = ResolveModuleId(command),
            State = DadWorkerExecutionState.Accepted,
            UpdatedAtUtc = EnsureUtc(updatedAtUtc),
            Summary = "Awaiting Client Dad acknowledgement.",
        };

        return new DadWorkerExecutionAck
        {
            CommandId = command.CommandId,
            RunId = command.RunId,
            WorkerSessionId = workerSessionId,
            Accepted = true,
            Summary = "Worker command queued through Dad Coordinator hub.",
            Status = status,
        };
    }

    public static DadWorkerExecutionStatus? SelectRemoteStatus(
        DadWorkerExecutionStatus? liveStatus,
        DadWorkerExecutionStatus? cachedStatus,
        bool exactRequestPending,
        bool authenticatedRouteRoutable)
    {
        if (liveStatus != null)
            return liveStatus;

        return exactRequestPending && authenticatedRouteRoutable
            ? cachedStatus?.Clone()
            : null;
    }

    private static DadModuleId ResolveModuleId(DadWorkerExecutionCommand command)
        => command.Plan?.Modules != null &&
           command.ModuleIndex >= 0 &&
           command.ModuleIndex < command.Plan.Modules.Count
            ? command.Plan.Modules[command.ModuleIndex].ModuleId
            : DadModuleId.None;

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
}
