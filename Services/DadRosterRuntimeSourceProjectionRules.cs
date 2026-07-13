using dad.Models;

namespace dad.Services;

public enum DadRosterRuntimeSourceOwnership
{
    NonRuntime = 0,
    LocalWorker = 1,
    ConnectedPeer = 2,
    UnresolvedRuntimeClaim = 3,
}

public readonly record struct DadRosterRuntimeSourceProjection(
    DadRosterCharacter Character,
    DadRosterRuntimeSourceOwnership Ownership,
    string Reason);

/// <summary>
/// Reinterprets producer-relative runtime labels from the point of view of the client consuming
/// a merged roster. The returned row is always a clone so presentation projection cannot mutate
/// a received catalog or the durable known-character/job ledger learned from that catalog.
/// </summary>
public static class DadRosterRuntimeSourceProjectionRules
{
    public static DadRosterRuntimeSourceProjection ProjectForConsumer(
        DadRosterCharacter character,
        DadWorkerSessionId localWorkerSessionId,
        string? localClientInstanceId,
        IEnumerable<DadParticipantSnapshot>? connectedParticipants)
    {
        ArgumentNullException.ThrowIfNull(character);

        var projected = character.Clone();
        if (!IsRuntime(projected.Source))
        {
            return new DadRosterRuntimeSourceProjection(
                projected,
                DadRosterRuntimeSourceOwnership.NonRuntime,
                "The row does not claim live runtime provenance.");
        }

        var localClientId = Normalize(localClientInstanceId);
        if (MatchesExactLocalOwner(
                projected.SourceWorkerSessionId,
                projected.SourceClientInstanceId,
                localWorkerSessionId,
                localClientId))
        {
            projected.Source = DadCharacterSource.LocalRuntime;
            projected.IsCurrent = true;
            return new DadRosterRuntimeSourceProjection(
                projected,
                DadRosterRuntimeSourceOwnership.LocalWorker,
                "The runtime owner is this exact consuming worker/client.");
        }

        var connectedPeer = (connectedParticipants ?? [])
            .Where(static participant => participant.State != DadParticipantState.Stale)
            .Where(participant => !MatchesExactOwner(
                participant.WorkerSessionId,
                participant.ClientInstanceId,
                localWorkerSessionId,
                localClientId))
            .FirstOrDefault(participant => MatchesExactOwner(
                projected.SourceWorkerSessionId,
                projected.SourceClientInstanceId,
                participant.WorkerSessionId,
                Normalize(participant.ClientInstanceId)));
        if (connectedPeer != null)
        {
            projected.Source = DadCharacterSource.PeerRuntime;
            projected.IsCurrent = true;
            return new DadRosterRuntimeSourceProjection(
                projected,
                DadRosterRuntimeSourceOwnership.ConnectedPeer,
                $"The runtime owner is connected peer '{connectedPeer.WorkerSessionId}'.");
        }

        projected.Source = HasStoredSnapshotProof(projected)
            ? DadCharacterSource.XadbOnly
            : DadCharacterSource.ManualUnresolved;
        projected.IsCurrent = false;
        return new DadRosterRuntimeSourceProjection(
            projected,
            DadRosterRuntimeSourceOwnership.UnresolvedRuntimeClaim,
            HasOwnerClaim(projected)
                ? "The claimed runtime owner is not a connected local or peer worker."
                : "The runtime claim has no worker/client owner provenance.");
    }

    public static bool MatchesExactOwner(
        DadWorkerSessionId claimedWorkerSessionId,
        string? claimedClientInstanceId,
        DadWorkerSessionId candidateWorkerSessionId,
        string? candidateClientInstanceId)
    {
        var claimedClientId = Normalize(claimedClientInstanceId);
        var candidateClientId = Normalize(candidateClientInstanceId);
        var hasWorkerClaim = !claimedWorkerSessionId.IsEmpty;
        var hasClientClaim = !string.IsNullOrWhiteSpace(claimedClientId);
        if (!hasWorkerClaim && !hasClientClaim)
            return false;

        if (hasWorkerClaim &&
            (candidateWorkerSessionId.IsEmpty ||
             !string.Equals(
                 claimedWorkerSessionId.Value,
                 candidateWorkerSessionId.Value,
                 StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !hasClientClaim ||
               !string.IsNullOrWhiteSpace(candidateClientId) &&
               string.Equals(claimedClientId, candidateClientId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesExactLocalOwner(
        DadWorkerSessionId claimedWorkerSessionId,
        string? claimedClientInstanceId,
        DadWorkerSessionId localWorkerSessionId,
        string? localClientInstanceId)
    {
        var claimedClientId = Normalize(claimedClientInstanceId);
        var localClientId = Normalize(localClientInstanceId);
        return !claimedWorkerSessionId.IsEmpty &&
               !localWorkerSessionId.IsEmpty &&
               !string.IsNullOrWhiteSpace(claimedClientId) &&
               !string.IsNullOrWhiteSpace(localClientId) &&
               string.Equals(
                   claimedWorkerSessionId.Value,
                   localWorkerSessionId.Value,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(claimedClientId, localClientId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRuntime(DadCharacterSource source)
        => source is DadCharacterSource.LocalRuntime or DadCharacterSource.PeerRuntime;

    private static bool HasStoredSnapshotProof(DadRosterCharacter character)
        => character.XadbReady || character.LastSnapshotUtc.HasValue;

    private static bool HasOwnerClaim(DadRosterCharacter character)
        => !character.SourceWorkerSessionId.IsEmpty ||
           !string.IsNullOrWhiteSpace(character.SourceClientInstanceId);

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim();
}
