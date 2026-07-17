namespace dad.Models;

public static class DadWorkerStatusPollingRules
{
    internal static DadWorkerExecutionAck? SelectCommandAcknowledgement(
        DadWorkerExecutionAck? acknowledgement,
        DadWorkerExecutionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (acknowledgement == null ||
            !Same(acknowledgement.CommandId, command.CommandId) ||
            !Same(acknowledgement.RunId, command.RunId))
        {
            return null;
        }

        // A rejected acknowledgement is still an exact command response and must
        // retain its fail-closed behavior. Accepted replies must also carry the
        // current command/run in their status; an older response is discarded so
        // the immutable command can be polled again.
        return !acknowledgement.Accepted || HasExactRunAndCommand(acknowledgement.Status, command)
            ? acknowledgement
            : null;
    }

    internal static bool MatchesExactAcknowledgement(
        DadParticipantSnapshot participant,
        DadWorkerExecutionCommand command,
        DadWorkerExecutionAck acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        return acknowledgement.Accepted &&
               Same(acknowledgement.CommandId, command.CommandId) &&
               Same(acknowledgement.RunId, command.RunId) &&
               Same(acknowledgement.WorkerSessionId.Value, participant.WorkerSessionId.Value) &&
               DadDroppedPeerContinuationRules.MatchesExactCommand(
                   participant,
                   command,
                   acknowledgement.Status);
    }

    public static DadWorkerExecutionStatus? SelectRemoteStatus(
        DadWorkerExecutionStatus? liveStatus,
        DadWorkerExecutionStatus? cachedStatus,
        DadWorkerExecutionCommand command,
        bool exactRequestPending,
        bool authenticatedRouteRoutable)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (liveStatus != null)
            return HasExactRunAndCommand(liveStatus, command) ? liveStatus : null;

        return exactRequestPending &&
               authenticatedRouteRoutable &&
               cachedStatus != null &&
               HasExactRunAndCommand(cachedStatus, command)
            ? cachedStatus?.Clone()
            : null;
    }

    private static bool HasExactRunAndCommand(
        DadWorkerExecutionStatus status,
        DadWorkerExecutionCommand command)
        => Same(status.CommandId, command.CommandId) && Same(status.RunId, command.RunId);

    private static bool Same(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
