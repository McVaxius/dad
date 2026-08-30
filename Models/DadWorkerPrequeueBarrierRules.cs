namespace dad.Models;

internal static class DadWorkerPrequeueBarrierRules
{
    public static bool IsRequired(
        DadRunPlan plan,
        DadPlannedModuleExecution module,
        IReadOnlyCollection<DadParticipantSnapshot> participants)
        => participants.Count > 1 &&
           (DadParticipantQueueFollowThroughRules.IsObserveAcceptOnlyLane(plan, module) ||
            plan.Request.ShoppingAssociations?.Any() == true &&
            (module.ModuleId == DadModuleId.Mogtome ||
             plan.Request.ShoppingAssociations.Any(association => participants.Any(participant =>
                 DadShoppingAssociationRules.MatchesLocalShopper(association, participant) &&
                 !IsLeader(plan, participant)))));

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

        if (module.ModuleId == DadModuleId.Mogtome && plan.Request.ShoppingAssociations?.Any() == true)
        {
            if (!TryResolveShoppingShopper(plan, participants, out var shopper, out blocker))
                return false;
            if (!acknowledgedStatuses.TryGetValue(shopper.WorkerSessionId.Value, out var shopperStatus))
            {
                targets = [shopper];
                return true;
            }
            if (!IsMogtomeShoppingGateReady(plan, shopperStatus))
                return true;

            targets = participants
                .Where(participant => !acknowledgedStatuses.ContainsKey(participant.WorkerSessionId.Value))
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
                IsNonLeaderReady(plan, module, status)))
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
        DadPlannedModuleExecution module,
        IReadOnlyList<DadParticipantSnapshot> participants,
        IReadOnlyDictionary<string, DadWorkerExecutionStatus> statuses)
    {
        var nonLeaders = participants.Where(participant => !IsLeader(plan, participant)).ToList();
        return nonLeaders.Count > 0 && nonLeaders.All(participant =>
            statuses.TryGetValue(participant.WorkerSessionId.Value, out var status) &&
            IsNonLeaderReady(plan, module, status));
    }

    public static bool IsNonLeaderReady(
        DadRunPlan plan,
        DadPlannedModuleExecution module,
        DadWorkerExecutionStatus status)
    {
        if (status.State == DadWorkerExecutionState.WaitingForQueue && !status.IsTerminal)
            return true;

        return module.ModuleId == DadModuleId.LootGoblin &&
               status.Role == DadWorkerExecutionRole.Participant &&
               status.State == DadWorkerExecutionState.Completed &&
               status.IsTerminal &&
               status.Success &&
               status.ModuleId == DadModuleId.LootGoblin &&
               string.Equals(status.RunId, plan.Request.RequestId, StringComparison.Ordinal) &&
               status.StepResult.Success &&
               status.StepResult.ParticipantState == DadParticipantState.Completed &&
               string.Equals(status.StepResult.RunId, plan.Request.RequestId, StringComparison.Ordinal) &&
               status.StepResult.ModuleId == DadModuleId.LootGoblin &&
               string.Equals(status.StepResult.StepName, "LootGoblin passive party holder", StringComparison.Ordinal) &&
               status.StepResult.ExecutorStatus.Status == DadRunStatus.Completed &&
               !status.StepResult.ExecutorStatus.IsActive &&
               string.Equals(status.StepResult.ExecutorStatus.StepName, "PassivePartyHolder", StringComparison.Ordinal);
    }

    public static bool TryResolveShoppingShopper(
        DadRunPlan plan,
        IReadOnlyCollection<DadParticipantSnapshot> participants,
        out DadParticipantSnapshot shopper,
        out string blocker)
    {
        var associations = plan.Request.ShoppingAssociations ?? [];
        var matches = participants.Where(participant => associations.Any(association =>
                DadShoppingAssociationRules.MatchesLocalShopper(association, participant)))
            .ToList();
        if (matches.Count == 1)
        {
            shopper = matches[0];
            blocker = string.Empty;
            return true;
        }

        shopper = new DadParticipantSnapshot();
        blocker = $"Shopping prequeue gate requires one exact LAN shopper; found {matches.Count}.";
        return false;
    }

    public static bool IsMogtomeShoppingGateReady(
        DadRunPlan plan,
        DadWorkerExecutionStatus status)
    {
        if (!string.Equals(status.RunId, plan.Request.RequestId, StringComparison.Ordinal) ||
            status.ModuleId != DadModuleId.Mogtome ||
            !string.Equals(status.StepResult.RunId, plan.Request.RequestId, StringComparison.Ordinal) ||
            status.StepResult.ModuleId != DadModuleId.Mogtome ||
            !status.StepResult.Success)
        {
            return false;
        }

        if (status.IsTerminal)
        {
            return status.State == DadWorkerExecutionState.Completed &&
                   status.Success &&
                   status.StepResult.ParticipantState == DadParticipantState.Completed &&
                   status.StepResult.ExecutorStatus.Status == DadRunStatus.Completed &&
                   !status.StepResult.ExecutorStatus.IsActive;
        }

        return status.State is DadWorkerExecutionState.WaitingForQueue or DadWorkerExecutionState.Running &&
               status.StepResult.ExecutorStatus.Status == DadRunStatus.Running &&
               status.StepResult.ExecutorStatus.IsActive;
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
