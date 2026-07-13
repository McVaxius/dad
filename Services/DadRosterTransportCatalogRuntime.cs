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

        if (TryBuildFallbackLocalCharacter(fallbackSnapshot, out var fallbackCharacter))
            UpsertPresenceFallbackLocalCharacter(localRows, fallbackCharacter);

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

    public static DadAccountRosterCatalog BuildLiveConnectedCatalog(DadPeerTransportSnapshot currentTransport)
    {
        var catalog = new DadAccountRosterCatalog
        {
            GeneratedAtUtc = DateTime.UtcNow,
            SourceClientInstanceId = currentTransport.LocalClientInstanceId,
            SourceWorkerSessionId = currentTransport.LocalWorkerSessionId,
            IsFullRosterAvailable = false,
            IsLiveConnectedCatalog = true,
            SourceDiagnostics = new DadRosterSourceDiagnostics
            {
                LocalAccountKey = string.Empty,
            },
        };

        foreach (var participant in currentTransport.KnownParticipants)
        {
            if (!IsLiveConnectedCatalogParticipant(participant))
                continue;
            if (!TryBuildParticipantFallbackRosterCharacter(
                    participant,
                    currentTransport,
                    out var row))
            {
                continue;
            }

            UpsertLiveConnectedRosterCharacter(catalog.Characters, row);
        }

        catalog.Characters = catalog.Characters
            .OrderBy(static character => character.AccountAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static character => character.AccountKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static character => character.CharacterKey.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        catalog.Accounts = BuildLiveConnectedAccountOptions(catalog.Characters, currentTransport).ToList();
        catalog.SourceDiagnostics.LocalRuntimeRows = catalog.Characters.Count(static character => character.Source == DadCharacterSource.LocalRuntime);
        catalog.SourceDiagnostics.FinalLocalRows = catalog.SourceDiagnostics.LocalRuntimeRows;
        catalog.Summary = BuildLiveConnectedCatalogSummary(catalog);
        return catalog;
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

    public static bool IsRequesterCatalogResponse(
        DadPeerRosterCatalogResponse response,
        DadWorkerSessionId requesterWorkerSessionId,
        string requesterClientInstanceId)
    {
        requesterClientInstanceId = requesterClientInstanceId?.Trim() ?? string.Empty;
        if (requesterWorkerSessionId.IsEmpty && string.IsNullOrWhiteSpace(requesterClientInstanceId))
            return false;

        return MatchesRosterOwner(
                   response.WorkerSessionId,
                   response.ClientInstanceId,
                   requesterWorkerSessionId,
                   requesterClientInstanceId) ||
               MatchesRosterOwner(
                   response.Catalog.SourceWorkerSessionId,
                   response.Catalog.SourceClientInstanceId,
                   requesterWorkerSessionId,
                   requesterClientInstanceId);
    }

    public static DadPeerRosterCatalogResponse WithoutRequesterCatalogRows(
        DadPeerRosterCatalogResponse response,
        DadWorkerSessionId requesterWorkerSessionId,
        string requesterClientInstanceId)
    {
        requesterClientInstanceId = requesterClientInstanceId?.Trim() ?? string.Empty;
        if (requesterWorkerSessionId.IsEmpty && string.IsNullOrWhiteSpace(requesterClientInstanceId))
            return response;

        var catalog = response.Catalog.Clone();
        var originalCharacterCount = catalog.Characters.Count;
        var originalAccountCount = catalog.Accounts.Count;
        catalog.Characters = catalog.Characters
            .Where(character => !MatchesRosterOwner(
                character.SourceWorkerSessionId,
                character.SourceClientInstanceId,
                requesterWorkerSessionId,
                requesterClientInstanceId))
            .ToList();
        catalog.Accounts = catalog.Accounts
            .Where(account => !MatchesRosterOwner(
                account.SourceWorkerSessionId,
                account.SourceClientInstanceId,
                requesterWorkerSessionId,
                requesterClientInstanceId))
            .ToList();

        if (catalog.Characters.Count == originalCharacterCount &&
            catalog.Accounts.Count == originalAccountCount)
        {
            return response;
        }

        return new DadPeerRosterCatalogResponse
        {
            RequestId = response.RequestId,
            RespondedAtUtc = response.RespondedAtUtc,
            ClientInstanceId = response.ClientInstanceId,
            WorkerSessionId = response.WorkerSessionId,
            Catalog = catalog,
            Warnings = [..response.Warnings],
        };
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

    private static void UpsertPresenceFallbackLocalCharacter(
        List<DadAcquiredCharacter> localRows,
        DadAcquiredCharacter fallbackCharacter)
    {
        var existingIndex = localRows.FindIndex(row => SameRuntimeCharacter(row, fallbackCharacter));
        if (existingIndex < 0)
        {
            if (localRows.Count > 0 && localRows.All(row => !SameRuntimeAccount(row, fallbackCharacter)))
                return;

            localRows.Clear();
            localRows.Add(fallbackCharacter);
            return;
        }

        localRows[existingIndex] = MergeLocalRuntimeCharacter(localRows[existingIndex], fallbackCharacter);
    }

    private static DadAcquiredCharacter MergeLocalRuntimeCharacter(
        DadAcquiredCharacter current,
        DadAcquiredCharacter fallback)
    {
        var merged = current.Clone();
        if (string.IsNullOrWhiteSpace(merged.AccountId))
            merged.AccountId = fallback.AccountId;
        if (string.IsNullOrWhiteSpace(merged.AccountAlias))
            merged.AccountAlias = fallback.AccountAlias;
        if (merged.ContentId == 0)
            merged.ContentId = fallback.ContentId;
        if (string.IsNullOrWhiteSpace(merged.CharacterName))
            merged.CharacterName = fallback.CharacterName;
        if (string.IsNullOrWhiteSpace(merged.WorldName))
            merged.WorldName = fallback.WorldName;
        if (merged.LastSeenUtc == null || fallback.LastSeenUtc > merged.LastSeenUtc)
            merged.LastSeenUtc = fallback.LastSeenUtc;
        if (merged.Freshness == DadSnapshotFreshness.Unknown)
            merged.Freshness = fallback.Freshness;
        if (merged.Readiness == DadReadinessState.Unknown)
            merged.Readiness = fallback.Readiness;
        return merged;
    }

    private static bool SameRuntimeCharacter(DadAcquiredCharacter left, DadAcquiredCharacter right)
        => DadRosterIdentity.SameCharacter(
            new DadCharacterKey(left.CharacterKey),
            left.ContentId,
            new DadCharacterKey(right.CharacterKey),
            right.ContentId);

    private static bool SameRuntimeAccount(DadAcquiredCharacter left, DadAcquiredCharacter right)
    {
        var leftAccount = DadRosterIdentity.ResolveAccountKey(left.AccountId, left.AccountAlias);
        var rightAccount = DadRosterIdentity.ResolveAccountKey(right.AccountId, right.AccountAlias);
        return !leftAccount.IsEmpty &&
               !rightAccount.IsEmpty &&
               DadRosterIdentity.SameAccount(leftAccount, rightAccount);
    }

    private static bool IsConnectedFallbackParticipant(DadParticipantSnapshot participant)
        => participant.State != DadParticipantState.Stale &&
           (!participant.WorkerSessionId.IsEmpty || !string.IsNullOrWhiteSpace(participant.ClientInstanceId));

    private static bool IsLiveConnectedCatalogParticipant(DadParticipantSnapshot participant)
        => IsConnectedFallbackParticipant(participant) && !participant.ManagedAccountKey.IsEmpty;

    private static bool TryBuildParticipantFallbackRosterCharacter(
        DadParticipantSnapshot participant,
        DadPeerTransportSnapshot currentTransport,
        out DadRosterCharacter row)
    {
        row = new DadRosterCharacter();
        var character = participant.Character.Clone();
        if (!HasExactManagedAccountBinding(participant, character))
            return false;
        var characterKey = ResolveParticipantCharacterKey(character, participant);
        if (string.IsNullOrWhiteSpace(characterKey) && character.ContentId == 0)
            return false;

        var accountKey = participant.ManagedAccountKey;
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

        var projection = DadPeerRuntimeProjectionRules.Evaluate(participant, character);
        row.Blockers = projection.Blockers;
        return true;
    }

    public static bool HasExactManagedAccountBinding(
        DadParticipantSnapshot participant,
        DadAcquiredCharacter character)
    {
        if (participant.ManagedAccountKey.IsEmpty)
            return false;

        // Account aliases are presentation only. A runtime row with no embedded account ID can be
        // attributed by its authenticated participant envelope, but an explicit conflicting ID is rejected.
        return string.IsNullOrWhiteSpace(character.AccountId) ||
               DadRosterIdentity.SameAccount(
                   new DadAccountKey(character.AccountId),
                   participant.ManagedAccountKey);
    }

    public static bool ReplaceSourceBlockersFromSupersedingRuntime(
        DadRosterCharacter target,
        DadRosterCharacter incoming)
    {
        if (!incoming.IsCurrent ||
            incoming.Source is not (DadCharacterSource.LocalRuntime or DadCharacterSource.PeerRuntime))
        {
            return false;
        }

        target.Blockers = incoming.Blockers
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker))
            .Select(static blocker => blocker.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        target.IsCurrent = true;
        target.IsStale = false;
        return true;
    }

    public static void ApplyOperatorPlanningPolicy(
        DadRosterCharacter character,
        DadRosterVisibility visibility,
        bool needsRosterUpdate)
    {
        character.Blockers.RemoveAll(static blocker =>
            string.Equals(blocker, "Hidden from normal roster planning.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(blocker, "Ignored by operator.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(blocker, "Needs roster refresh before normal planning.", StringComparison.OrdinalIgnoreCase));
        character.Visibility = visibility;
        character.NeedsRosterUpdate = needsRosterUpdate;
        if (visibility == DadRosterVisibility.Hidden)
            AddBlocker(character.Blockers, "Hidden from normal roster planning.");
        else if (visibility == DadRosterVisibility.Ignored)
            AddBlocker(character.Blockers, "Ignored by operator.");
        if (needsRosterUpdate)
            AddBlocker(character.Blockers, "Needs roster refresh before normal planning.");
    }

    public static void ApplyCurrentRuntimeCoverage(
        DadAccountRosterCatalog mergedCatalog,
        DadAccountRosterCatalog currentRuntimeCatalog)
    {
        foreach (var character in mergedCatalog.Characters.Where(static row =>
                     row.Source is DadCharacterSource.LocalRuntime or DadCharacterSource.PeerRuntime))
        {
            if (currentRuntimeCatalog.Characters.Any(runtime => DadRosterIdentity.SameRow(runtime, character)))
                continue;

            character.IsCurrent = false;
            character.IsStale = true;
            AddBlocker(character.Blockers, "No current live Dad heartbeat for roster character.");
        }
    }

    private static void AddBlocker(List<string> blockers, string blocker)
    {
        if (blockers.All(existing => !string.Equals(existing, blocker, StringComparison.OrdinalIgnoreCase)))
            blockers.Add(blocker);
    }

    private static void UpsertLiveConnectedRosterCharacter(
        List<DadRosterCharacter> rows,
        DadRosterCharacter candidate)
    {
        var incoming = candidate.Clone();
        var existingIndex = rows.FindIndex(row => DadRosterIdentity.SameRow(row, incoming));
        if (existingIndex < 0)
        {
            rows.Add(incoming);
            return;
        }

        var existing = rows[existingIndex];
        if (incoming.Source == DadCharacterSource.LocalRuntime || existing.Source != DadCharacterSource.LocalRuntime)
            rows[existingIndex] = incoming;
    }

    private static IReadOnlyList<DadRosterAccountOption> BuildLiveConnectedAccountOptions(
        IReadOnlyList<DadRosterCharacter> characters,
        DadPeerTransportSnapshot currentTransport)
        => characters
            .Where(static character => !character.AccountKey.IsEmpty)
            .GroupBy(static character => character.AccountKey.Value.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rows = group.ToList();
                var source = rows.FirstOrDefault(row => IsLocalRosterOwner(row, currentTransport)) ?? rows[0];
                return new DadRosterAccountOption
                {
                    AccountKey = source.AccountKey,
                    AccountAlias = source.AccountAlias,
                    DisplayName = BuildAccountDisplayName(source.AccountKey.Value, source.AccountAlias),
                    SourceClientInstanceId = source.SourceClientInstanceId,
                    SourceWorkerSessionId = source.SourceWorkerSessionId,
                    IsLocal = rows.Any(row => IsLocalRosterOwner(row, currentTransport)),
                    OwnerOnline = true,
                    AssignedCharacterCount = rows
                        .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
                        .Count(),
                };
            })
            .OrderByDescending(static account => account.IsLocal)
            .ThenBy(static account => account.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static account => account.AccountKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static account => account.SourceClientInstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsLocalRosterOwner(
        DadRosterCharacter row,
        DadPeerTransportSnapshot currentTransport)
        => MatchesRosterOwner(
            row.SourceWorkerSessionId,
            row.SourceClientInstanceId,
            currentTransport.LocalWorkerSessionId,
            currentTransport.LocalClientInstanceId?.Trim() ?? string.Empty);

    private static string BuildAccountDisplayName(string accountKey, string accountAlias)
        => string.IsNullOrWhiteSpace(accountAlias)
            ? accountKey
            : accountAlias.Trim();

    private static string BuildLiveConnectedCatalogSummary(DadAccountRosterCatalog catalog)
        => $"{catalog.Characters.Count} live connected Dad roster row(s).";

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
        => MatchesRosterOwner(
            currentTransport.LocalWorkerSessionId,
            currentTransport.LocalClientInstanceId,
            participant.WorkerSessionId,
            participant.ClientInstanceId?.Trim() ?? string.Empty);

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
