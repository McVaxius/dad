namespace dad.Models;

public readonly record struct DadSchedulerClientRoute(
    string SlotId,
    DadParticipantSnapshot Participant);

public readonly record struct DadWakeSlotPipelineDecision(
    bool CanDispatch,
    DadWakeTakeoverMessageKind MessageKind,
    DadWakeCommitKind CommitKind,
    DateTime? ExecutionTimeUtc,
    string Summary);

public static class DadSchedulerRoutingRules
{
    public static DadAccountKey ResolveStableClientAccount(string configuredClientAccountId)
        => new((configuredClientAccountId ?? string.Empty).Trim());

    public static DadParticipantSnapshot? ResolveExactConnectedClient(
        DadAccountKey requiredAccountKey,
        IEnumerable<DadParticipantSnapshot> participants,
        Func<DadWorkerSessionId, bool> isWorkerOnline)
    {
        if (requiredAccountKey.IsEmpty)
            return null;

        return participants.FirstOrDefault(participant =>
            !participant.WorkerSessionId.IsEmpty &&
            participant.State != DadParticipantState.Stale &&
            string.Equals(
                participant.ManagedAccountKey.Value,
                requiredAccountKey.Value,
                StringComparison.OrdinalIgnoreCase) &&
            isWorkerOnline(participant.WorkerSessionId));
    }

    public static DadParticipantSnapshot? ResolveFrozenConnectedClient(
        DadAccountKey requiredAccountKey,
        DadWorkerSessionId frozenWorkerSessionId,
        IEnumerable<DadParticipantSnapshot> participants,
        Func<DadWorkerSessionId, bool> isWorkerOnline)
    {
        if (requiredAccountKey.IsEmpty || frozenWorkerSessionId.IsEmpty)
            return null;

        return participants.FirstOrDefault(participant =>
            participant.State != DadParticipantState.Stale &&
            string.Equals(
                participant.WorkerSessionId.Value,
                frozenWorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                participant.ManagedAccountKey.Value,
                requiredAccountKey.Value,
                StringComparison.OrdinalIgnoreCase) &&
            isWorkerOnline(participant.WorkerSessionId));
    }

    public static DadWakeSlotPipelineDecision ResolveNextTakeoverAction(
        DadSchedulerSlotState slot,
        DateTime nowUtc)
    {
        if (!slot.ClientConnected)
        {
            return new DadWakeSlotPipelineDecision(
                false,
                DadWakeTakeoverMessageKind.Status,
                DadWakeCommitKind.None,
                null,
                "Waiting for the frozen worker session.");
        }

        if (slot.TakeoverPhase == DadWakeTakeoverPhase.Prepared)
        {
            var execution = slot.ResetExecutionUtc ?? EnsureUtc(nowUtc).AddSeconds(5);
            return new DadWakeSlotPipelineDecision(
                true,
                DadWakeTakeoverMessageKind.Go,
                DadWakeCommitKind.Reset,
                execution,
                "This slot is prepared and can schedule its reset independently.");
        }

        if (slot.TakeoverPhase == DadWakeTakeoverPhase.ResetVerified)
        {
            var execution = slot.RelogExecutionUtc ?? EnsureUtc(nowUtc).AddSeconds(5);
            return new DadWakeSlotPipelineDecision(
                true,
                DadWakeTakeoverMessageKind.Go,
                DadWakeCommitKind.Relog,
                execution,
                "This slot verified reset and can schedule its relog independently.");
        }

        if (slot.TakeoverPhase is DadWakeTakeoverPhase.Ready or DadWakeTakeoverPhase.Blocked or DadWakeTakeoverPhase.Cancelled)
        {
            return new DadWakeSlotPipelineDecision(
                false,
                DadWakeTakeoverMessageKind.Status,
                DadWakeCommitKind.None,
                null,
                "This slot is terminal.");
        }

        return new DadWakeSlotPipelineDecision(
            true,
            slot.TakeoverPhase < DadWakeTakeoverPhase.Prepared
                ? DadWakeTakeoverMessageKind.Prepare
                : DadWakeTakeoverMessageKind.Status,
            DadWakeCommitKind.None,
            null,
            "Prepare or poll this slot without waiting for another slot.");
    }

    public static bool TryResolveAllTakeoverClients(
        IReadOnlyList<DadSchedulerSlotState> slots,
        IReadOnlyList<DadParticipantSnapshot> participants,
        Func<DadWorkerSessionId, bool> isWorkerOnline,
        out IReadOnlyList<DadSchedulerClientRoute> routes)
    {
        var resolved = new List<DadSchedulerClientRoute>(slots.Count);
        foreach (var slot in slots)
        {
            var participant = ResolveExactConnectedClient(
                slot.RequiredAccountKey,
                participants,
                isWorkerOnline);
            if (participant == null)
            {
                routes = [];
                return false;
            }

            resolved.Add(new DadSchedulerClientRoute(slot.SlotId, participant));
        }

        routes = resolved;
        return true;
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
