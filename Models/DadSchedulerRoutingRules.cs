namespace dad.Models;

public readonly record struct DadSchedulerClientRoute(
    string SlotId,
    DadParticipantSnapshot Participant);

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
}
