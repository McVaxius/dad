namespace dad.Models;

public enum DadDroppedPeerContinuationAction
{
    Wait = 0,
    SatisfyParticipant = 1,
    Fail = 2,
}

public readonly record struct DadDroppedPeerContinuationDecision(
    DadDroppedPeerContinuationAction Action,
    DadScheduleFailureKind FailureKind,
    string Summary);

public static class DadDroppedPeerContinuationRules
{
    public static DadDroppedPeerContinuationDecision EvaluateMissingPeer(
        DadParticipantSnapshot participant,
        DadWorkerExecutionCommand command,
        DadWorkerExecutionStatus? cachedStatus,
        DadWorkerExecutionCommand? leaderCommand,
        DadWorkerExecutionStatus? leaderStatus,
        DateTime missingSinceUtc,
        DateTime nowUtc,
        TimeSpan participantReadyTimeout)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(command);
        var elapsed = EnsureUtc(nowUtc) - EnsureUtc(missingSinceUtc);
        var timedOut = elapsed >= participantReadyTimeout;
        var isProtectedRole = command.Role == DadWorkerExecutionRole.QueueLeader || participant.IsAuthority;

        if (cachedStatus != null && !MatchesExactCommand(participant, command, cachedStatus))
        {
            return Fail(
                isProtectedRole ? DadScheduleFailureKind.MissingOrUnknownLeaderState : DadScheduleFailureKind.EntryTerminalFailure,
                "Cached worker status contradicts the exact run/module/command/role/identity assignment.");
        }
        if (cachedStatus is { IsTerminal: true, Success: false })
        {
            return Fail(
                isProtectedRole ? DadScheduleFailureKind.MissingOrUnknownLeaderState : DadScheduleFailureKind.EntryTerminalFailure,
                "A failed or cancelled peer cannot be internally satisfied after disconnect.");
        }

        if (!isProtectedRole && cachedStatus is { EnteredDuty: true })
        {
            if (leaderCommand != null && leaderStatus != null &&
                Same(leaderCommand.RunId, command.RunId) &&
                ResolveModule(leaderCommand) == ResolveModule(command) &&
                MatchesExactCommandForRole(leaderCommand, leaderStatus, DadWorkerExecutionRole.QueueLeader))
            {
                if (leaderStatus.IsTerminal)
                {
                    return leaderStatus.Success
                        ? new DadDroppedPeerContinuationDecision(
                            DadDroppedPeerContinuationAction.SatisfyParticipant,
                            DadScheduleFailureKind.None,
                            "The exact queue leader completed successfully after this non-leader proved duty entry; satisfy only the dropped participant without replaying the duty.")
                        : Fail(
                            DadScheduleFailureKind.MissingOrUnknownLeaderState,
                            "The exact queue leader ended unsuccessfully; dropped-peer continuation is forbidden.");
                }

                return new DadDroppedPeerContinuationDecision(
                    DadDroppedPeerContinuationAction.Wait,
                    DadScheduleFailureKind.None,
                    "Dropped non-leader proved exact duty entry; continue waiting while the exact queue leader runs.");
            }

            if (!timedOut)
            {
                return new DadDroppedPeerContinuationDecision(
                    DadDroppedPeerContinuationAction.Wait,
                    DadScheduleFailureKind.None,
                    "Dropped non-leader proved duty entry; waiting for exact queue-leader state.");
            }

            return Fail(
                DadScheduleFailureKind.MissingOrUnknownLeaderState,
                "Exact queue-leader state remained missing or unknown after participant-ready timeout.");
        }

        if (!timedOut)
        {
            return new DadDroppedPeerContinuationDecision(
                DadDroppedPeerContinuationAction.Wait,
                DadScheduleFailureKind.None,
                "Missing peer has not proved duty entry; waiting up to participant-ready timeout.");
        }

        return Fail(
            isProtectedRole ? DadScheduleFailureKind.MissingOrUnknownLeaderState : DadScheduleFailureKind.EntryTerminalFailure,
            isProtectedRole
                ? "Queue leader or coordinator state remained missing or unknown after participant-ready timeout."
                : "Participant disappeared before proving duty entry and did not return within participant-ready timeout.");
    }

    public static bool MatchesExactCommand(
        DadParticipantSnapshot participant,
        DadWorkerExecutionCommand command,
        DadWorkerExecutionStatus status)
    {
        if (!MatchesExactCommandForRole(command, status, command.Role) ||
            !Same(status.WorkerSessionId.Value, participant.WorkerSessionId.Value))
        {
            return false;
        }

        var rows = command.Participants.Where(static row => row.IsLocalClient).ToList();
        if (rows.Count != 1)
            return false;
        var row = rows[0];
        return Same(row.WorkerSessionId.Value, participant.WorkerSessionId.Value) &&
               DadRosterIdentity.SameAccount(row.ManagedAccountKey, participant.ManagedAccountKey) &&
               Same(row.ActiveCharacterKey.Value, participant.ActiveCharacterKey.Value) &&
               row.Character.ContentId == participant.Character.ContentId &&
               Same(row.AssignedSlotId, participant.AssignedSlotId);
    }

    private static bool MatchesExactCommandForRole(
        DadWorkerExecutionCommand command,
        DadWorkerExecutionStatus status,
        DadWorkerExecutionRole role)
    {
        var module = ResolveModule(command);
        var localRows = command.Participants.Where(static row => row.IsLocalClient).ToList();
        return localRows.Count == 1 &&
               Same(localRows[0].WorkerSessionId.Value, status.WorkerSessionId.Value) &&
               !string.IsNullOrWhiteSpace(command.CommandId) &&
               Same(status.CommandId, command.CommandId) &&
               Same(status.RunId, command.RunId) &&
               status.Role == role &&
               command.Role == role &&
               module != DadModuleId.None &&
               status.ModuleId == module;
    }

    private static DadModuleId ResolveModule(DadWorkerExecutionCommand command)
        => command.ModuleIndex >= 0 && command.ModuleIndex < command.Plan.Modules.Count
            ? command.Plan.Modules[command.ModuleIndex].ModuleId
            : DadModuleId.None;

    private static DadDroppedPeerContinuationDecision Fail(
        DadScheduleFailureKind failureKind,
        string summary)
        => new(DadDroppedPeerContinuationAction.Fail, failureKind, summary);

    private static bool Same(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
