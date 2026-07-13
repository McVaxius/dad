namespace dad.Models;

public static class DadCoordinatorRuntimeProjectionRules
{
    public static IReadOnlyList<DadParticipantSnapshot> BuildOnlineParticipantSet(
        DadParticipantSnapshot localPresence,
        IEnumerable<DadParticipantSnapshot> peerProjections,
        Func<DadWorkerSessionId, bool> isWorkerOnline)
    {
        var participants = new List<DadParticipantSnapshot> { localPresence.Clone() };
        participants.AddRange(FilterRemoteProjections(localPresence, peerProjections, isWorkerOnline));
        return participants;
    }

    public static IReadOnlyList<DadParticipantSnapshot> BuildFrozenParticipantSet(
        DadParticipantSnapshot localPresence,
        IEnumerable<DadParticipantSnapshot> peerProjections,
        IReadOnlySet<string> frozenWorkerSessionIds,
        Func<DadWorkerSessionId, bool> isWorkerOnline)
    {
        var participants = new List<DadParticipantSnapshot>();
        if (frozenWorkerSessionIds.Contains(localPresence.WorkerSessionId.Value))
            participants.Add(localPresence.Clone());

        participants.AddRange(FilterRemoteProjections(localPresence, peerProjections, isWorkerOnline)
            .Where(participant => frozenWorkerSessionIds.Contains(participant.WorkerSessionId.Value)));
        return participants;
    }

    private static IEnumerable<DadParticipantSnapshot> FilterRemoteProjections(
        DadParticipantSnapshot localPresence,
        IEnumerable<DadParticipantSnapshot> peerProjections,
        Func<DadWorkerSessionId, bool> isWorkerOnline)
        => peerProjections
            .Where(participant =>
                !participant.WorkerSessionId.IsEmpty &&
                !string.Equals(
                    participant.WorkerSessionId.Value,
                    localPresence.WorkerSessionId.Value,
                    StringComparison.OrdinalIgnoreCase) &&
                isWorkerOnline(participant.WorkerSessionId))
            .Select(static participant =>
            {
                var remote = participant.Clone();
                remote.IsLocalClient = false;
                remote.IsAuthority = false;
                return remote;
            });
}
