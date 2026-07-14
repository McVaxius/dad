namespace dad.Models;

internal static class DadWorkerPrequeueBarrierRules
{
    public static bool IsRequired(
        DadRunPlan plan,
        DadPlannedModuleExecution module,
        IReadOnlyCollection<DadParticipantSnapshot> participants)
        => participants.Count > 1 &&
           DadParticipantQueueFollowThroughRules.IsObserveAcceptOnlyLane(plan, module);

    public static bool TryResolveDispatchTargets(
        DadRunPlan plan,
        DadPlannedModuleExecution module,
        IReadOnlyList<DadParticipantSnapshot> participants,
        IReadOnlyDictionary<string, DadWorkerExecutionStatus> acknowledgedStatuses,
        out List<DadParticipantSnapshot> targets,
        out string blocker)
    {
        targets = [];
        blocker = string.Empty;

        if (!IsRequired(plan, module, participants))
        {
            targets = participants
                .Where(participant =>
                    !acknowledgedStatuses.ContainsKey(participant.WorkerSessionId.Value))
                .ToList();
            return true;
        }

        var leaders = participants
            .Where(participant => IsLeader(plan, participant))
            .ToList();
        if (leaders.Count != 1)
        {
            blocker = $"ADS prequeue barrier requires exactly one queue leader; found {leaders.Count}.";
            return false;
        }

        var leader = leaders[0];
        var nonLeaders = participants
            .Where(participant => !SameWorker(participant, leader))
            .ToList();
        targets = nonLeaders
            .Where(participant =>
                !acknowledgedStatuses.ContainsKey(participant.WorkerSessionId.Value))
            .ToList();
        if (targets.Count > 0)
            return true;

        if (!nonLeaders.All(participant =>
                acknowledgedStatuses.TryGetValue(participant.WorkerSessionId.Value, out var status) &&
                status.State == DadWorkerExecutionState.WaitingForQueue &&
                !status.IsTerminal))
        {
            targets = [];
            return true;
        }

        if (!acknowledgedStatuses.ContainsKey(leader.WorkerSessionId.Value))
            targets = [leader];

        return true;
    }

    public static bool AreAllNonLeadersWaiting(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants,
        IReadOnlyDictionary<string, DadWorkerExecutionStatus> statuses)
    {
        var nonLeaders = participants.Where(participant => !IsLeader(plan, participant)).ToList();
        return nonLeaders.Count > 0 && nonLeaders.All(participant =>
            statuses.TryGetValue(participant.WorkerSessionId.Value, out var status) &&
            status.State == DadWorkerExecutionState.WaitingForQueue &&
            !status.IsTerminal);
    }

    public static List<DadParticipantSnapshot> ResolveCancellationScope(
        IReadOnlyList<DadParticipantSnapshot> participants,
        IReadOnlyCollection<string> acknowledgedWorkerSessionIds)
        => participants
            .Where(participant => acknowledgedWorkerSessionIds.Contains(
                participant.WorkerSessionId.Value,
                StringComparer.OrdinalIgnoreCase))
            .Select(static participant => participant.Clone())
            .ToList();

    public static string AttributeFailure(DadParticipantSnapshot participant, string reason)
    {
        var prefix = $"slot '{participant.AssignedSlotId}', character '{participant.ActiveCharacterKey.Value}', " +
                     $"worker '{participant.WorkerSessionId.Value}'";
        return reason.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? reason
            : $"{prefix}: {reason}";
    }

    public static bool IsLeader(DadRunPlan plan, DadParticipantSnapshot participant)
        => !string.IsNullOrWhiteSpace(plan.LeaderCharacterKey) &&
           string.Equals(
               participant.ActiveCharacterKey.Value,
               plan.LeaderCharacterKey,
               StringComparison.OrdinalIgnoreCase);

    private static bool SameWorker(DadParticipantSnapshot left, DadParticipantSnapshot right)
        => string.Equals(
            left.WorkerSessionId.Value,
            right.WorkerSessionId.Value,
            StringComparison.OrdinalIgnoreCase);
}
