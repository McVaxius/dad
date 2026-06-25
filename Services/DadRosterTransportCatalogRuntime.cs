using dad.Models;

namespace dad.Services;

public static class DadRosterTransportCatalogRuntime
{
    public static DadCharacterPool BuildLocalTransportPool(
        DadCharacterPool? currentPool,
        DadParticipantSnapshot? fallbackSnapshot,
        DadPeerTransportSnapshot currentTransport)
    {
        currentPool ??= new DadCharacterPool();
        var localRows = currentPool.Characters
            .Where(static character => character.Source == DadCharacterSource.LocalRuntime)
            .Select(static character => character.Clone())
            .Where(HasUsableCharacterIdentity)
            .ToList();

        if (localRows.Count == 0 && TryBuildFallbackLocalCharacter(fallbackSnapshot, out var fallbackCharacter))
            localRows.Add(fallbackCharacter);

        return new DadCharacterPool
        {
            LastUpdatedUtc = currentPool.LastUpdatedUtc,
            XadbStatus = currentPool.XadbStatus,
            PeerTransport = currentTransport,
            LastSummary = currentPool.LastSummary,
            Characters = localRows,
        };
    }

    public static bool ShouldUsePeerRuntimeFallback(
        DadPeerSnapshotResponse runtimeResponse,
        IReadOnlyList<DadPeerRosterCatalogResponse> peerCatalogResponses)
        => !PeerCatalogContainsUsableRuntimeRow(runtimeResponse, peerCatalogResponses);

    public static bool PeerCatalogContainsUsableRuntimeRow(
        DadPeerSnapshotResponse runtimeResponse,
        IReadOnlyList<DadPeerRosterCatalogResponse> peerCatalogResponses)
    {
        foreach (var response in peerCatalogResponses)
        {
            if (response.Catalog.Characters.Any(character =>
                    CatalogRowMatchesRuntime(character, response, runtimeResponse)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CatalogRowMatchesRuntime(
        DadRosterCharacter row,
        DadPeerRosterCatalogResponse catalogResponse,
        DadPeerSnapshotResponse runtimeResponse)
    {
        if (!HasUsableCharacterIdentity(row))
            return false;

        var participant = runtimeResponse.Participant;
        if (!MatchesRuntimeOwner(row, catalogResponse, runtimeResponse, participant))
            return false;

        if (!participant.ManagedAccountKey.IsEmpty &&
            !row.AccountKey.IsEmpty &&
            !DadRosterIdentity.SameAccount(row.AccountKey, participant.ManagedAccountKey))
        {
            return false;
        }

        return MatchesRuntimeCharacter(row, runtimeResponse.Character) ||
               MatchesRuntimeCharacter(row, participant.Character) ||
               DadRosterIdentity.SameCharacter(
                   row.CharacterKey,
                   row.ContentId,
                   participant.ActiveCharacterKey,
                   participant.Character.ContentId);
    }

    private static bool MatchesRuntimeOwner(
        DadRosterCharacter row,
        DadPeerRosterCatalogResponse catalogResponse,
        DadPeerSnapshotResponse runtimeResponse,
        DadParticipantSnapshot participant)
    {
        var runtimeWorkerId = participant.WorkerSessionId;
        var runtimeClientId = FirstNonEmpty(runtimeResponse.ClientInstanceId, participant.ClientInstanceId);

        if (!row.SourceWorkerSessionId.IsEmpty &&
            !runtimeWorkerId.IsEmpty &&
            string.Equals(row.SourceWorkerSessionId.Value, runtimeWorkerId.Value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(row.SourceClientInstanceId) &&
            !string.IsNullOrWhiteSpace(runtimeClientId) &&
            string.Equals(row.SourceClientInstanceId, runtimeClientId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var catalogWorkerId = catalogResponse.Catalog.SourceWorkerSessionId.IsEmpty
            ? catalogResponse.WorkerSessionId
            : catalogResponse.Catalog.SourceWorkerSessionId;
        if (!catalogWorkerId.IsEmpty &&
            !runtimeWorkerId.IsEmpty &&
            string.Equals(catalogWorkerId.Value, runtimeWorkerId.Value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var catalogClientId = FirstNonEmpty(catalogResponse.Catalog.SourceClientInstanceId, catalogResponse.ClientInstanceId);
        return !string.IsNullOrWhiteSpace(catalogClientId) &&
               !string.IsNullOrWhiteSpace(runtimeClientId) &&
               string.Equals(catalogClientId, runtimeClientId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesRuntimeCharacter(DadRosterCharacter row, DadAcquiredCharacter runtimeCharacter)
        => DadRosterIdentity.SameCharacter(
               row.CharacterKey,
               row.ContentId,
               new DadCharacterKey(runtimeCharacter.CharacterKey),
               runtimeCharacter.ContentId);

    private static bool TryBuildFallbackLocalCharacter(
        DadParticipantSnapshot? fallbackSnapshot,
        out DadAcquiredCharacter fallbackCharacter)
    {
        fallbackCharacter = new DadAcquiredCharacter();
        if (fallbackSnapshot == null)
            return false;

        var candidate = fallbackSnapshot.Character.Clone();
        if (!IsUsableCharacterKey(candidate.CharacterKey) && IsUsableCharacterKey(fallbackSnapshot.ActiveCharacterKey.Value))
            candidate.CharacterKey = fallbackSnapshot.ActiveCharacterKey.Value;
        if (!HasUsableCharacterIdentity(candidate))
            return false;

        candidate.Source = DadCharacterSource.LocalRuntime;
        candidate.LastSeenUtc ??= fallbackSnapshot.LastHeartbeatUtc == default
            ? DateTime.UtcNow
            : fallbackSnapshot.LastHeartbeatUtc;
        if (candidate.Freshness == DadSnapshotFreshness.Unknown && fallbackSnapshot.IsAvailable)
            candidate.Freshness = DadSnapshotFreshness.Live;
        if (string.IsNullOrWhiteSpace(candidate.AccountId) && !fallbackSnapshot.ManagedAccountKey.IsEmpty)
            candidate.AccountId = fallbackSnapshot.ManagedAccountKey.Value;
        if (string.IsNullOrWhiteSpace(candidate.AccountAlias))
            candidate.AccountAlias = fallbackSnapshot.ManagedAccountAlias;

        var parsed = ParseCharacterKey(candidate.CharacterKey);
        if (string.IsNullOrWhiteSpace(candidate.CharacterName))
            candidate.CharacterName = parsed.CharacterName;
        if (string.IsNullOrWhiteSpace(candidate.WorldName))
            candidate.WorldName = parsed.WorldName;

        fallbackCharacter = candidate;
        return true;
    }

    private static bool HasUsableCharacterIdentity(DadAcquiredCharacter character)
        => character.ContentId != 0 || IsUsableCharacterKey(character.CharacterKey);

    private static bool HasUsableCharacterIdentity(DadRosterCharacter character)
        => character.ContentId != 0 || IsUsableCharacterKey(character.CharacterKey.Value);

    private static bool IsUsableCharacterKey(string? characterKey)
        => !string.IsNullOrWhiteSpace(characterKey) &&
           !string.Equals(characterKey.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static (string CharacterName, string WorldName) ParseCharacterKey(string characterKey)
    {
        var parts = (characterKey ?? string.Empty).Split('@', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (parts[0], parts[1])
            : (characterKey ?? string.Empty, string.Empty);
    }
}
