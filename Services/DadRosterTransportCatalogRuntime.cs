using dad.Models;

namespace dad.Services;

public static class DadRosterTransportCatalogRuntime
{
    private static readonly TimeSpan RecentAggregateRosterResponseTtl = TimeSpan.FromMinutes(15);

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

    public static IReadOnlyList<DadRosterCharacter> BuildParticipantRuntimeFallbackRows(
        DadPeerTransportSnapshot currentTransport,
        IReadOnlyList<DadPeerRosterCatalogResponse> catalogResponses)
    {
        var rows = new List<DadRosterCharacter>();
        foreach (var response in EnumerateTransportRuntimeResponses(currentTransport))
        {
            if (!IsConnectedFallbackParticipant(response.Participant))
                continue;
            if (!ShouldUsePeerRuntimeFallback(response, catalogResponses))
                continue;
            if (!TryBuildParticipantFallbackRosterCharacter(response.Participant, currentTransport, out var row))
                continue;

            rows.Add(row);
        }

        return rows;
    }

    public static bool IsRosterOwnerReachable(
        DadWorkerSessionId ownerWorkerSessionId,
        string? ownerClientInstanceId,
        DadPeerTransportSnapshot currentTransport,
        IReadOnlyList<DadPeerRosterCatalogResponse> aggregateResponses,
        DateTime? nowUtc = null)
    {
        ownerClientInstanceId = ownerClientInstanceId?.Trim() ?? string.Empty;
        if (ownerWorkerSessionId.IsEmpty && string.IsNullOrWhiteSpace(ownerClientInstanceId))
            return false;

        if (MatchesRosterOwner(
                currentTransport.LocalWorkerSessionId,
                currentTransport.LocalClientInstanceId,
                ownerWorkerSessionId,
                ownerClientInstanceId))
        {
            return true;
        }

        if (currentTransport.KnownParticipants.Any(participant =>
                participant.State != DadParticipantState.Stale &&
                MatchesRosterOwner(
                    participant.WorkerSessionId,
                    participant.ClientInstanceId,
                    ownerWorkerSessionId,
                    ownerClientInstanceId)))
        {
            return true;
        }

        var now = nowUtc ?? DateTime.UtcNow;
        return aggregateResponses.Any(response =>
            IsRecentAggregateResponse(response, now) &&
            AggregateResponseMatchesRosterOwner(response, ownerWorkerSessionId, ownerClientInstanceId));
    }

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

    private static bool AggregateResponseMatchesRosterOwner(
        DadPeerRosterCatalogResponse response,
        DadWorkerSessionId ownerWorkerSessionId,
        string ownerClientInstanceId)
    {
        if (MatchesRosterOwner(response.WorkerSessionId, response.ClientInstanceId, ownerWorkerSessionId, ownerClientInstanceId) ||
            MatchesRosterOwner(
                response.Catalog.SourceWorkerSessionId,
                response.Catalog.SourceClientInstanceId,
                ownerWorkerSessionId,
                ownerClientInstanceId))
        {
            return true;
        }

        return response.Catalog.Accounts.Any(account =>
                   MatchesRosterOwner(
                       account.SourceWorkerSessionId,
                       account.SourceClientInstanceId,
                       ownerWorkerSessionId,
                       ownerClientInstanceId)) ||
               response.Catalog.Characters.Any(character =>
                   MatchesRosterOwner(
                       character.SourceWorkerSessionId,
                       character.SourceClientInstanceId,
                       ownerWorkerSessionId,
                       ownerClientInstanceId));
    }

    private static bool IsRecentAggregateResponse(DadPeerRosterCatalogResponse response, DateTime nowUtc)
        => nowUtc - response.RespondedAtUtc <= RecentAggregateRosterResponseTtl;

    private static bool MatchesRosterOwner(
        DadWorkerSessionId candidateWorkerSessionId,
        string? candidateClientInstanceId,
        DadWorkerSessionId ownerWorkerSessionId,
        string ownerClientInstanceId)
        => MatchesWorker(candidateWorkerSessionId, ownerWorkerSessionId) ||
           MatchesClient(candidateClientInstanceId, ownerClientInstanceId);

    private static bool MatchesWorker(DadWorkerSessionId candidateWorkerSessionId, DadWorkerSessionId ownerWorkerSessionId)
        => !candidateWorkerSessionId.IsEmpty &&
           !ownerWorkerSessionId.IsEmpty &&
           string.Equals(
               candidateWorkerSessionId.Value,
               ownerWorkerSessionId.Value,
               StringComparison.OrdinalIgnoreCase);

    private static bool MatchesClient(string? candidateClientInstanceId, string ownerClientInstanceId)
        => !string.IsNullOrWhiteSpace(candidateClientInstanceId) &&
           !string.IsNullOrWhiteSpace(ownerClientInstanceId) &&
           string.Equals(
               candidateClientInstanceId.Trim(),
               ownerClientInstanceId,
               StringComparison.OrdinalIgnoreCase);

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

        var participantAccountKey = ResolveParticipantAccountKey(participant);
        if (!participantAccountKey.IsEmpty &&
            (row.AccountKey.IsEmpty || !DadRosterIdentity.SameAccount(row.AccountKey, participantAccountKey)))
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

    private static IEnumerable<DadPeerSnapshotResponse> EnumerateTransportRuntimeResponses(
        DadPeerTransportSnapshot currentTransport)
    {
        if (currentTransport.LastResponses.Count > 0)
        {
            foreach (var response in currentTransport.LastResponses)
                yield return response;
            yield break;
        }

        foreach (var participant in currentTransport.KnownParticipants)
            yield return BuildRuntimeResponse(participant);
    }

    private static DadPeerSnapshotResponse BuildRuntimeResponse(DadParticipantSnapshot participant)
        => new()
        {
            RespondedAtUtc = participant.LastHeartbeatUtc == default
                ? DateTime.UtcNow
                : participant.LastHeartbeatUtc,
            ClientInstanceId = participant.ClientInstanceId,
            ProcessId = participant.ProcessId,
            Character = participant.Character.Clone(),
            Participant = participant.Clone(),
            XadbReady = participant.Character.XadbReady,
            Warnings = [..participant.Warnings],
        };

    private static bool IsConnectedFallbackParticipant(DadParticipantSnapshot participant)
        => participant.State != DadParticipantState.Stale &&
           (!participant.WorkerSessionId.IsEmpty || !string.IsNullOrWhiteSpace(participant.ClientInstanceId));

    private static bool TryBuildParticipantFallbackRosterCharacter(
        DadParticipantSnapshot participant,
        DadPeerTransportSnapshot currentTransport,
        out DadRosterCharacter row)
    {
        row = new DadRosterCharacter();
        var character = participant.Character.Clone();
        var characterKey = ResolveParticipantCharacterKey(character, participant);
        if (string.IsNullOrWhiteSpace(characterKey) && character.ContentId == 0)
            return false;

        var accountKey = ResolveParticipantAccountKey(participant);
        if (accountKey.IsEmpty)
            accountKey = DadRosterIdentity.ResolveAccountKey(character.AccountId, character.AccountAlias);
        if (accountKey.IsEmpty)
            return false;

        var parsed = ParseCharacterKey(characterKey);
        var lastRuntimeSeenUtc = character.LastSeenUtc ??
                                 (participant.LastHeartbeatUtc == default
                                     ? DateTime.UtcNow
                                     : participant.LastHeartbeatUtc);

        row = new DadRosterCharacter
        {
            AccountKey = accountKey,
            AccountAlias = FirstNonEmpty(participant.ManagedAccountAlias, character.AccountAlias),
            CharacterKey = new DadCharacterKey(characterKey),
            ContentId = character.ContentId,
            CharacterName = FirstNonEmpty(character.CharacterName, parsed.CharacterName),
            WorldId = character.WorldId == 0 ? null : character.WorldId,
            WorldName = FirstNonEmpty(character.WorldName, parsed.WorldName),
            DataCenterId = character.DataCenterId,
            DataCenterName = character.DataCenterName,
            LastSnapshotUtc = character.XadbSnapshotUtc,
            LastRuntimeSeenUtc = lastRuntimeSeenUtc,
            JobLevels = new Dictionary<uint, int>(character.JobLevels),
            CurrentJobId = character.CurrentJobId,
            CurrentJobAbbrev = character.CurrentJobAbbrev,
            CurrentLevel = character.CurrentLevel,
            SnapshotQuality = character.SnapshotQuality,
            SnapshotVersion = character.SnapshotVersion,
            XadbReady = character.XadbReady,
            IsCurrent = true,
            Source = IsLocalTransportParticipant(participant, currentTransport)
                ? DadCharacterSource.LocalRuntime
                : DadCharacterSource.PeerRuntime,
            SourceClientInstanceId = participant.ClientInstanceId,
            SourceWorkerSessionId = participant.WorkerSessionId,
            MapEligible = character.MapEligible,
            MapEligibilitySummary = character.MapEligibilitySummary,
            Blockers = [..character.Blockers],
            Warnings = [..participant.Warnings],
        };
        return true;
    }

    private static DadAccountKey ResolveParticipantAccountKey(DadParticipantSnapshot participant)
        => !participant.ManagedAccountKey.IsEmpty
            ? participant.ManagedAccountKey
            : DadRosterIdentity.ResolveAccountKey(string.Empty, participant.ManagedAccountAlias);

    private static string ResolveParticipantCharacterKey(
        DadAcquiredCharacter character,
        DadParticipantSnapshot participant)
    {
        if (IsUsableCharacterKey(character.CharacterKey))
            return character.CharacterKey.Trim();
        if (IsUsableCharacterKey(participant.ActiveCharacterKey.Value))
            return participant.ActiveCharacterKey.Value.Trim();

        return string.Empty;
    }

    private static bool IsLocalTransportParticipant(
        DadParticipantSnapshot participant,
        DadPeerTransportSnapshot currentTransport)
    {
        if (participant.IsLocalClient)
            return true;

        return MatchesRosterOwner(
            currentTransport.LocalWorkerSessionId,
            currentTransport.LocalClientInstanceId,
            participant.WorkerSessionId,
            participant.ClientInstanceId?.Trim() ?? string.Empty);
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
