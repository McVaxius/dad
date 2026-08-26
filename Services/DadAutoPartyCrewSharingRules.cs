using dad.Models;

namespace dad.Services;

internal sealed record DadAutoPartyCrewCandidate(
    DadAutoPartyCrewIdentity Identity,
    DadAcquiredCharacter Character,
    IReadOnlyList<uint> PermittedCombatJobIds,
    bool Available,
    DadAutoPartyInboundRoute? InboundRoute = null)
{
    public bool IsCurrentCharacter => Character.Source == DadCharacterSource.LocalRuntime;
}

internal sealed record DadAutoPartyInboundRoute(
    string OpaqueCharacterId,
    DadAccountKey AccountKey,
    DadCharacterKey CharacterKey,
    ulong ContentId,
    string CharacterName,
    uint WorldId,
    string WorldName,
    DadWorkerSessionId WorkerSessionId,
    string ClientInstanceId,
    DadParticipantSnapshot OwnerSnapshot,
    DateTimeOffset ObservedAt);

internal sealed record DadAutoPartyCrewReconciliation(
    bool Changed,
    IReadOnlyList<DadAutoPartyCrewCandidate> Candidates);

/// <summary>
/// Reconciles DAD's already-curated Crew with the opaque identities AutoParty may publish.
/// Fleet Matrix is consulted only to preserve an existing opaque identifier during migration.
/// </summary>
internal static class DadAutoPartyCrewSharingRules
{
    private const int MaximumCrewIdentities = 256;

    public static DadAutoPartyCrewReconciliation Reconcile(
        DadAutoPartyConfiguration configuration,
        DadAutoPartyFleetConfiguration fleet,
        IEnumerable<DadAcquiredCharacter>? curatedCrew,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(fleet);
        utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

        var crew = (curatedCrew ?? [])
            .Where(static character => character != null)
            .Select(static character => character.Clone())
            .Select(character => new { Character = character, Key = BuildRosterIdentityKey(character) })
            .Where(static item => item.Key.Length > 0)
            .GroupBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumCrewIdentities)
            .ToList();

        var existing = (configuration.CrewIdentities ?? [])
            .Where(static identity => identity != null)
            .Select(static identity => identity!.Clone().Normalize())
            .Where(static identity => identity.IsValid)
            .GroupBy(static identity => identity.RosterIdentityKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var localFleetRows = (fleet.Rows ?? [])
            .Where(static row => row is { IsRemote: false } &&
                                 !string.IsNullOrWhiteSpace(row.OpaqueCharacterId))
            .Select(static row => row.Clone().Normalize())
            .ToList();
        var identities = new List<DadAutoPartyCrewIdentity>(crew.Count);
        foreach (var item in crew)
        {
            if (!existing.TryGetValue(item.Key, out var identity))
            {
                identity = new DadAutoPartyCrewIdentity
                {
                    RosterIdentityKey = item.Key,
                    OpaqueCharacterId = FindFleetOpaqueIdentity(localFleetRows, item.Character) ??
                                        "opaque-" + Guid.NewGuid().ToString("N"),
                };
            }
            identities.Add(identity.Clone().Normalize());
        }

        var changed = !SameIdentities(configuration.CrewIdentities, identities);
        if (changed)
            configuration.CrewIdentities = identities;

        var handles = identities
            .Select(static identity => identity.OpaqueCharacterId)
            .ToHashSet(StringComparer.Ordinal);
        changed |= PruneLocalPairPolicies(configuration.Pairings, handles, utcNow);
        changed |= PruneLocalPairPolicies(configuration.PendingPairings, handles, utcNow);
        changed |= ReconcileStandingPolicy(
            configuration,
            handles,
            crew.Select(static item => item.Character).ToList(),
            utcNow);

        var identitiesByKey = identities.ToDictionary(
            static identity => identity.RosterIdentityKey,
            StringComparer.OrdinalIgnoreCase);
            var candidates = crew
            .Select(item => new DadAutoPartyCrewCandidate(
                identitiesByKey[item.Key].Clone(),
                item.Character,
                ResolvePermittedCombatJobs(item.Character),
                true))
            .ToList();
        return new(changed, candidates);
    }

    public static bool TryBuildPrivatePolicy(
        DadAutoPartyCrewShareScope scope,
        IEnumerable<DadAutoPartyCrewCandidate>? crew,
        IEnumerable<string>? selectedHandles,
        DateTime utcNow,
        out DadAutoPartySharePolicy policy)
    {
        var candidates = (crew ?? []).Where(static candidate => candidate != null).ToList();
        var availableHandles = candidates
            .Select(static candidate => candidate.Identity.OpaqueCharacterId)
            .ToHashSet(StringComparer.Ordinal);
        var selected = (selectedHandles ?? [])
            .Where(handle => availableHandles.Contains(handle))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var current = candidates
            .FirstOrDefault(static candidate => candidate.IsCurrentCharacter)?.Identity.OpaqueCharacterId;
        policy = scope switch
        {
            DadAutoPartyCrewShareScope.AllCharacters => new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.AllCharactersForPeer,
                Enabled = true,
                UpdatedAtUtc = utcNow,
            },
            DadAutoPartyCrewShareScope.CurrentCharacter when !string.IsNullOrWhiteSpace(current) => new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.SpecificCharacter,
                CharacterHandles = [current],
                Enabled = true,
                UpdatedAtUtc = utcNow,
            },
            DadAutoPartyCrewShareScope.SpecificCharacters when selected.Count > 0 => new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.CharacterList,
                CharacterHandles = selected,
                Enabled = true,
                UpdatedAtUtc = utcNow,
            },
            _ => new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.CharacterList,
                Enabled = false,
                UpdatedAtUtc = utcNow,
            },
        };
        policy.Normalize();
        return policy.IsValid && policy.Enabled;
    }

    public static DadAutoPartySharePolicy BuildCommunityPolicy(
        DadAutoPartyCrewShareScope scope,
        IEnumerable<DadAutoPartyCrewCandidate>? crew,
        IEnumerable<string>? selectedHandles,
        DateTime utcNow)
    {
        var candidates = (crew ?? []).Where(static candidate => candidate != null).ToList();
        var availableHandles = candidates
            .Select(static candidate => candidate.Identity.OpaqueCharacterId)
            .ToHashSet(StringComparer.Ordinal);
        var current = candidates
            .FirstOrDefault(static candidate => candidate.IsCurrentCharacter)?.Identity.OpaqueCharacterId;
        var handles = scope switch
        {
            DadAutoPartyCrewShareScope.AllCharacters => availableHandles.Order(StringComparer.Ordinal).ToList(),
            DadAutoPartyCrewShareScope.CurrentCharacter when !string.IsNullOrWhiteSpace(current) => [current],
            _ => (selectedHandles ?? [])
                .Where(handle => availableHandles.Contains(handle))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList(),
        };
        return new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.CharacterList,
            CharacterHandles = handles,
            Enabled = handles.Count > 0,
            UpdatedAtUtc = utcNow,
        }.Normalize();
    }

    public static string BuildRosterIdentityKey(DadAcquiredCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return DadAutoPartyConfiguration.NormalizeIdentifier(DadRosterIdentity.BuildKey(character));
    }

    public static IReadOnlyList<uint> ResolvePermittedCombatJobs(DadAcquiredCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        var jobs = (character.JobLevels ?? [])
            .Where(static entry => entry.Value > 0 && DadRosterCharacterMerge.IsCombatJob(entry.Key))
            .Select(static entry => entry.Key)
            .ToHashSet();
        if (character.CurrentJobId is { } currentJob && DadRosterCharacterMerge.IsCombatJob(currentJob))
            jobs.Add(currentJob);
        return jobs.Order().ToArray();
    }

    public static IReadOnlyList<DadAutoPartyCrewCandidate> AttachInboundRoutes(
        IEnumerable<DadAutoPartyCrewCandidate>? crew,
        IEnumerable<DadRosterCharacter>? roster,
        DadParticipantSnapshot localParticipant,
        IEnumerable<DadParticipantSnapshot>? reachableParticipants,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(localParticipant);
        var rosterRows = (roster ?? []).Where(static row => row != null).ToList();
        var owners = new[] { localParticipant }
            .Concat(reachableParticipants ?? [])
            .Where(static participant => participant != null && !participant.WorkerSessionId.IsEmpty)
            .DistinctBy(static participant => participant.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (crew ?? []).Where(static candidate => candidate != null).Select(candidate =>
        {
            var accountKey = DadRosterIdentity.ResolveAccountKey(
                candidate.Character.AccountId,
                candidate.Character.AccountAlias);
            var characterKey = new DadCharacterKey(candidate.Character.CharacterKey?.Trim() ?? string.Empty);
            var matchingRows = rosterRows.Where(row =>
                    DadRosterIdentity.SameAccount(row.AccountKey, accountKey) &&
                    DadRosterIdentity.SameCharacter(
                        row.CharacterKey,
                        row.ContentId,
                        characterKey,
                        candidate.Character.ContentId))
                .Take(2)
                .ToArray();
            if (accountKey.IsEmpty || characterKey.IsEmpty || matchingRows.Length != 1)
                return candidate with { Available = false, InboundRoute = null };

            var row = matchingRows[0];
            if (row.Visibility != DadRosterVisibility.Active || row.IsStale || row.NeedsRosterUpdate ||
                row.Source == DadCharacterSource.PeerRuntime ||
                (!row.XadbReady && row.Source != DadCharacterSource.LocalRuntime) ||
                row.ContentId == 0 || row.WorldId is null or 0 or > ushort.MaxValue ||
                string.IsNullOrWhiteSpace(row.CharacterName) || string.IsNullOrWhiteSpace(row.WorldName))
            {
                return candidate with { Available = false, InboundRoute = null };
            }

            var routes = owners.Where(owner =>
                {
                    var activeCharacterMatches = DadRosterIdentity.SameCharacter(
                        owner.ActiveCharacterKey,
                        owner.Character?.ContentId ?? 0,
                        characterKey,
                        row.ContentId);
                    return (owner.AutoRetainerAvailable ||
                            candidate.IsCurrentCharacter && owner.IsLocalClient && activeCharacterMatches) &&
                           DadRosterIdentity.SameAccount(owner.ManagedAccountKey, accountKey) &&
                           (owner.IsLocalClient || utcNow.UtcDateTime - owner.LastHeartbeatUtc <= TimeSpan.FromSeconds(15)) &&
                           (activeCharacterMatches ||
                            owner.AvailableCharacterKeys.Any(available => DadRosterIdentity.SameCharacter(
                                available,
                                0,
                                characterKey,
                                row.ContentId))) &&
                           (row.SourceWorkerSessionId.IsEmpty || string.Equals(
                               row.SourceWorkerSessionId.Value,
                               owner.WorkerSessionId.Value,
                               StringComparison.OrdinalIgnoreCase)) &&
                           (string.IsNullOrWhiteSpace(row.SourceClientInstanceId) || string.Equals(
                               row.SourceClientInstanceId,
                               owner.ClientInstanceId,
                               StringComparison.OrdinalIgnoreCase)) &&
                           (!row.SourceWorkerSessionId.IsEmpty ||
                            !string.IsNullOrWhiteSpace(row.SourceClientInstanceId) || owner.IsLocalClient);
                })
                .Take(2)
                .ToArray();
            if (routes.Length != 1)
                return candidate with { Available = false, InboundRoute = null };

            var owner = routes[0].Clone();
            var route = new DadAutoPartyInboundRoute(
                candidate.Identity.OpaqueCharacterId,
                accountKey,
                characterKey,
                row.ContentId,
                row.CharacterName,
                row.WorldId.Value,
                row.WorldName,
                owner.WorkerSessionId,
                owner.ClientInstanceId,
                owner,
                utcNow);
            return candidate with { Available = true, InboundRoute = route };
        }).ToList();
    }

    private static string? FindFleetOpaqueIdentity(
        IReadOnlyList<DadAutoPartyFleetRow> rows,
        DadAcquiredCharacter character)
    {
        var accountKey = DadRosterIdentity.ResolveAccountKey(character.AccountId, character.AccountAlias).Value;
        var matching = rows.Where(row =>
            string.Equals(row.CharacterKey, character.CharacterKey?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(row.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 0)
        {
            matching = rows.Where(row => string.Equals(
                row.CharacterKey,
                character.CharacterKey?.Trim(),
                StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return matching.Count == 1
            ? DadAutoPartyConfiguration.NormalizeIdentifier(matching[0].OpaqueCharacterId)
            : null;
    }

    private static bool PruneLocalPairPolicies(
        IEnumerable<DadAutoPartyPairing>? pairings,
        ISet<string> validHandles,
        DateTime utcNow)
    {
        var changed = false;
        foreach (var pairing in pairings ?? [])
        {
            if (pairing == null)
                continue;
            var policy = pairing.LocalSharePolicy;
            if (policy == null || policy.Mode is not (DadAutoPartyCharacterShareMode.SpecificCharacter or DadAutoPartyCharacterShareMode.CharacterList))
                continue;
            var retained = (policy.CharacterHandles ?? [])
                .Where(validHandles.Contains)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            if ((policy.CharacterHandles ?? []).SequenceEqual(retained, StringComparer.Ordinal) &&
                (!policy.Enabled || retained.Count > 0))
            {
                continue;
            }
            policy.CharacterHandles = retained;
            policy.Enabled = retained.Count > 0;
            policy.Revision = Math.Max(1, policy.Revision + 1);
            policy.UpdatedAtUtc = utcNow;
            changed = true;
        }
        return changed;
    }

    private static bool ReconcileStandingPolicy(
        DadAutoPartyConfiguration configuration,
        ISet<string> validHandles,
        IReadOnlyList<DadAcquiredCharacter> crew,
        DateTime utcNow)
    {
        var policy = configuration.StandingSharePolicy ??= new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.CharacterList,
            Enabled = false,
        };
        var currentCharacter = crew.FirstOrDefault(static character =>
            character.Source == DadCharacterSource.LocalRuntime);
        var currentRosterKey = currentCharacter == null
            ? string.Empty
            : BuildRosterIdentityKey(currentCharacter);
        var identitiesByKey = (configuration.CrewIdentities ?? [])
            .Where(static identity => identity is { IsValid: true })
            .ToDictionary(static identity => identity.RosterIdentityKey, static identity => identity.OpaqueCharacterId,
                StringComparer.OrdinalIgnoreCase);
        var desired = configuration.StandingShareScope switch
        {
            DadAutoPartyCrewShareScope.AllCharacters => validHandles.Order(StringComparer.Ordinal).ToList(),
            DadAutoPartyCrewShareScope.CurrentCharacter when !string.IsNullOrWhiteSpace(currentRosterKey) &&
                identitiesByKey.TryGetValue(currentRosterKey, out var handle) => [handle],
            _ => (policy.CharacterHandles ?? [])
                .Where(validHandles.Contains)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList(),
        };
        var enabled = desired.Count > 0;
        if (policy.Mode == DadAutoPartyCharacterShareMode.CharacterList &&
            policy.Enabled == enabled &&
            (policy.CharacterHandles ?? []).SequenceEqual(desired, StringComparer.Ordinal))
        {
            return false;
        }
        policy.Mode = DadAutoPartyCharacterShareMode.CharacterList;
        policy.CharacterHandles = desired;
        policy.Enabled = enabled;
        policy.Revision = Math.Max(1, policy.Revision + 1);
        policy.UpdatedAtUtc = utcNow;
        return true;
    }

    private static bool SameIdentities(
        IEnumerable<DadAutoPartyCrewIdentity>? left,
        IReadOnlyList<DadAutoPartyCrewIdentity> right)
    {
        var normalizedLeft = (left ?? [])
            .Where(static identity => identity != null)
            .Select(static identity => identity!.Clone().Normalize())
            .Where(static identity => identity.IsValid)
            .OrderBy(static identity => identity.RosterIdentityKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedLeft.Count != right.Count)
            return false;
        return normalizedLeft.Zip(right).All(pair =>
            string.Equals(pair.First.RosterIdentityKey, pair.Second.RosterIdentityKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pair.First.OpaqueCharacterId, pair.Second.OpaqueCharacterId, StringComparison.Ordinal));
    }
}
