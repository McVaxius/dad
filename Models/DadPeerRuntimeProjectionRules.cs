namespace dad.Models;

public sealed class DadPeerRuntimeProjection
{
    public DadReadinessState Readiness { get; init; }
    public List<string> Blockers { get; init; } = [];
}

public static class DadPeerRuntimeProjectionRules
{
    public static DadPeerRuntimeProjection Evaluate(
        DadParticipantSnapshot participant,
        DadAcquiredCharacter character)
    {
        var blockers = character.Blockers
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker))
            .Select(static blocker => blocker.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        DadReadinessState readiness;
        if (participant.State == DadParticipantState.Stale || character.Freshness == DadSnapshotFreshness.Stale)
        {
            readiness = DadReadinessState.Stale;
            Add(blockers, "Peer runtime heartbeat or character snapshot is stale.");
        }
        else if (!participant.IsAvailable)
        {
            readiness = DadReadinessState.Unavailable;
            Add(blockers, "Peer runtime character is unavailable or relogging.");
        }
        else if (!participant.IsEligibleForRun)
        {
            readiness = DadReadinessState.Unavailable;
            Add(blockers, "Peer runtime is not eligible for Dad work.");
        }
        else if (participant.AuthorityMode == DadAuthorityMode.LocalOnly)
        {
            readiness = DadReadinessState.Unavailable;
            Add(blockers, "Peer runtime is local-only/isolated from Dad Coordinator work.");
        }
        else if (blockers.Count > 0 || character.Readiness == DadReadinessState.Blocked)
        {
            readiness = DadReadinessState.Blocked;
            if (blockers.Count == 0)
                Add(blockers, "Peer runtime reports the character as blocked.");
        }
        else
        {
            readiness = character.Readiness switch
            {
                DadReadinessState.Ready => DadReadinessState.Ready,
                DadReadinessState.Deferred => DadReadinessState.Deferred,
                DadReadinessState.Unavailable => DadReadinessState.Unavailable,
                DadReadinessState.Stale => DadReadinessState.Stale,
                _ => DadReadinessState.Unknown,
            };

            if (readiness != DadReadinessState.Ready)
                Add(blockers, $"Peer runtime readiness is {readiness}.");
        }

        return new DadPeerRuntimeProjection
        {
            Readiness = readiness,
            Blockers = blockers,
        };
    }

    private static void Add(List<string> blockers, string blocker)
    {
        if (blockers.All(existing => !string.Equals(existing, blocker, StringComparison.OrdinalIgnoreCase)))
            blockers.Add(blocker);
    }
}
