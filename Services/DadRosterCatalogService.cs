using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadRosterCatalogService
{
    private readonly Configuration configuration;
    private readonly DadXadbClient xadbClient;
    private readonly DadTransportService transportService;
    private readonly IPluginLog log;
    private DadAccountRosterCatalog currentCatalog = new() { Summary = "Roster catalog not refreshed yet." };
    private IReadOnlyList<DadPeerRosterCatalogResponse> lastPeerResponses = [];

    public DadRosterCatalogService(
        Configuration configuration,
        DadXadbClient xadbClient,
        DadTransportService transportService,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.xadbClient = xadbClient;
        this.transportService = transportService;
        this.log = log;
        configuration.RosterCatalog ??= new DadRosterCatalogConfiguration();
        configuration.RosterCatalog.Visibility ??= [];
        configuration.RosterCatalog.RefreshHistory ??= [];
    }

    public DadAccountRosterCatalog CurrentCatalog => currentCatalog.Clone();

    public DadAccountRosterCatalog RefreshCatalog(DadCharacterPool pool, DadRosterRefreshPlan? plan = null)
    {
        plan ??= new DadRosterRefreshPlan
        {
            IncludeHidden = configuration.RosterCatalog.ShowHiddenInRoster,
            StaleAfterHours = configuration.RosterCatalog.StaleAfterHours,
        };

        var localCatalog = BuildLocalCatalog(pool, plan);
        var allCatalogs = new List<DadAccountRosterCatalog> { localCatalog };

        if (plan.ForcePeerRefresh)
            lastPeerResponses = transportService.RequestRosterCatalogs(plan);

        allCatalogs.AddRange(lastPeerResponses.Select(static response => response.Catalog));
        currentCatalog = MergeCatalogs(allCatalogs, plan);
        return CurrentCatalog;
    }

    public DadAccountRosterCatalog BuildLocalCatalog(DadCharacterPool pool, DadRosterRefreshPlan? plan = null)
    {
        plan ??= new DadRosterRefreshPlan
        {
            IncludeHidden = true,
            IncludeIgnored = true,
            StaleAfterHours = configuration.RosterCatalog.StaleAfterHours,
        };

        var catalog = xadbClient.GetAccountCharacterList();
        catalog.SourceClientInstanceId = string.Empty;
        catalog.SourceWorkerSessionId = new DadWorkerSessionId(string.Empty);
        StampCatalogAccount(catalog, pool);

        if (!catalog.IsFullRosterAvailable)
            catalog.Warnings.Add("Full XADB roster IPC missing or old; using runtime rows plus any best-effort XADB rows.");

        foreach (var character in pool.Characters)
            UpsertRosterCharacter(catalog.Characters, FromAcquiredCharacter(character));

        ApplyVisibility(catalog, plan);
        catalog.Summary = BuildCatalogSummary(catalog);
        return catalog;
    }

    public DadCharacterPool BuildCuratedPool(
        DadCharacterPool pool,
        bool includeHidden = false,
        bool includeIgnored = false,
        bool includeNeedsUpdate = false)
    {
        var plan = new DadRosterRefreshPlan
        {
            IncludeHidden = includeHidden,
            IncludeIgnored = includeIgnored,
            StaleAfterHours = configuration.RosterCatalog.StaleAfterHours,
        };
        var catalog = RefreshCatalog(pool, plan);

        var filteredCharacters = catalog.Characters
            .Where(character => ShouldIncludeForPlanner(character.Visibility, includeHidden, includeIgnored, includeNeedsUpdate))
            .Select(ToAcquiredCharacter)
            .ToList();

        var curated = new DadCharacterPool
        {
            LastUpdatedUtc = pool.LastUpdatedUtc,
            XadbStatus = pool.XadbStatus,
            PeerTransport = pool.PeerTransport,
            LastSummary = pool.LastSummary,
            Characters = filteredCharacters
                .OrderByDescending(static character => character.Source == DadCharacterSource.LocalRuntime)
                .ThenBy(static character => character.Source)
                .ThenBy(static character => character.CharacterKey, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        curated.LastSummary = $"{curated.Characters.Count} active roster row(s) | {catalog.Summary}";
        return curated;
    }

    public DadRosterVisibility ResolveVisibility(DadCharacterKey characterKey, DadAccountKey accountKey)
    {
        var record = FindVisibilityRecord(characterKey, accountKey);
        return record?.Visibility ?? DadRosterVisibility.Active;
    }

    public bool IsVisibleForNormalPlanning(DadCharacterKey characterKey, DadAccountKey accountKey)
        => ResolveVisibility(characterKey, accountKey) == DadRosterVisibility.Active;

    public DadAccountRosterCatalog SetVisibility(DadRosterVisibilityChangeRequest request, DadCharacterPool pool)
    {
        configuration.RosterCatalog ??= new DadRosterCatalogConfiguration();
        request.CharacterRefs ??= [];
        request.CharacterKeys ??= [];
        request.AccountKeys ??= [];
        var changedKeys = ResolveVisibilityTargets(request, pool);
        foreach (var target in changedKeys)
        {
            var record = FindVisibilityRecord(target.CharacterKey, target.AccountKey, target.ContentId);
            if (record == null)
            {
                record = new DadRosterVisibilityRecord
                {
                    CharacterKey = target.CharacterKey.Value,
                    ContentId = target.ContentId,
                    AccountKey = target.AccountKey,
                };
                configuration.RosterCatalog.Visibility.Add(record);
            }

            record.CharacterKey = target.CharacterKey.Value;
            record.ContentId = target.ContentId;
            record.AccountKey = target.AccountKey;
            record.Visibility = request.Visibility;
            record.UpdatedAtUtc = DateTime.UtcNow;
            record.Reason = request.Reason?.Trim() ?? string.Empty;
        }

        configuration.Save();
        log.Information("[dad][Roster] Set {Count} roster row(s) to {Visibility}.", changedKeys.Count, request.Visibility);
        return RefreshCatalog(pool, new DadRosterRefreshPlan
        {
            IncludeHidden = true,
            IncludeIgnored = true,
            StaleAfterHours = configuration.RosterCatalog.StaleAfterHours,
        });
    }

    public DadRosterRefreshResultDto RefreshLocalRosterCharacter(
        DadRosterRefreshCommandDto command,
        DadParticipantSnapshot snapshot)
    {
        var result = xadbClient.RefreshAndSaveForRosterUpdate(command, snapshot);
        RecordRefreshResult(result);
        return result;
    }

    public void RecordRefreshResult(DadRosterRefreshResultDto result)
    {
        configuration.RosterCatalog ??= new DadRosterCatalogConfiguration();
        configuration.RosterCatalog.RefreshHistory.Add(new DadRosterRefreshRecord
        {
            CharacterKey = result.CharacterKey.Value,
            ContentId = result.ContentId,
            AccountKey = result.AccountKey,
            RequestedAtUtc = DateTime.UtcNow,
            RefreshedAtUtc = result.RefreshedAtUtc,
            Success = result.Success,
            Summary = result.Summary,
        });

        if (result.Success)
        {
            var record = FindVisibilityRecord(result.CharacterKey, result.AccountKey, result.ContentId);
            if (record != null && record.Visibility == DadRosterVisibility.NeedsUpdate)
            {
                record.Visibility = DadRosterVisibility.Active;
                record.UpdatedAtUtc = DateTime.UtcNow;
                record.Reason = "Roster refresh completed.";
            }
        }

        TrimRefreshHistory();
        configuration.Save();
    }

    public DadRosterCharacter? FindCharacter(DadCharacterKey characterKey)
    {
        var catalog = CurrentCatalog;
        return catalog.Characters.FirstOrDefault(character =>
            string.Equals(character.CharacterKey.Value, characterKey.Value, StringComparison.OrdinalIgnoreCase));
    }

    public DadRosterCharacter? FindCharacter(DadRosterCharacterRef reference)
    {
        var catalog = CurrentCatalog;
        return catalog.Characters.FirstOrDefault(character => DadRosterIdentity.Matches(character, reference));
    }

    private DadAccountRosterCatalog MergeCatalogs(IReadOnlyList<DadAccountRosterCatalog> catalogs, DadRosterRefreshPlan plan)
    {
        var merged = new DadAccountRosterCatalog
        {
            GeneratedAtUtc = DateTime.UtcNow,
            IsFullRosterAvailable = catalogs.Any(static catalog => catalog.IsFullRosterAvailable),
            Visibility = configuration.RosterCatalog.Visibility.Select(static record => record.Clone()).ToList(),
        };

        foreach (var catalog in catalogs)
        {
            foreach (var warning in catalog.Warnings)
            {
                if (!string.IsNullOrWhiteSpace(warning) &&
                    merged.Warnings.All(existing => !string.Equals(existing, warning, StringComparison.OrdinalIgnoreCase)))
                {
                    merged.Warnings.Add(warning);
                }
            }

            foreach (var character in catalog.Characters)
                UpsertRosterCharacter(merged.Characters, character);
        }

        ApplyVisibility(merged, plan);
        merged.Characters = merged.Characters
            .Where(character => ShouldIncludeInCatalog(character.Visibility, plan))
            .OrderBy(static character => character.AccountAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static character => character.AccountKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static character => character.CharacterKey.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        merged.Summary = BuildCatalogSummary(merged);
        return merged;
    }

    private void ApplyVisibility(DadAccountRosterCatalog catalog, DadRosterRefreshPlan plan)
    {
        var staleAfter = TimeSpan.FromHours(Math.Clamp(plan.StaleAfterHours <= 0 ? 72 : plan.StaleAfterHours, 1, 24 * 90));
        foreach (var character in catalog.Characters)
        {
            var visibility = ResolveVisibility(character.CharacterKey, character.AccountKey);
            character.Visibility = visibility;
            character.IsStale = character.LastSnapshotUtc.HasValue && DateTime.UtcNow - character.LastSnapshotUtc.Value > staleAfter;

            var lastRefresh = configuration.RosterCatalog.RefreshHistory
                .Where(record => RecordMatches(record, character.CharacterKey, character.AccountKey, character.ContentId))
                .OrderByDescending(static record => record.RefreshedAtUtc ?? record.RequestedAtUtc)
                .FirstOrDefault();
            character.LastRosterRefreshUtc = lastRefresh?.RefreshedAtUtc;

            if (visibility == DadRosterVisibility.Hidden)
                AddBlocker(character, "Hidden from normal roster planning.");
            else if (visibility == DadRosterVisibility.Ignored)
                AddBlocker(character, "Ignored by operator.");
            else if (visibility == DadRosterVisibility.NeedsUpdate)
                AddBlocker(character, "Queued for login-refresh update.");
        }

        catalog.Visibility = configuration.RosterCatalog.Visibility.Select(static record => record.Clone()).ToList();
    }

    private static void UpsertRosterCharacter(List<DadRosterCharacter> characters, DadRosterCharacter candidate)
    {
        var existing = characters.FindIndex(existingCharacter => DadRosterIdentity.SameRow(existingCharacter, candidate));

        if (existing < 0)
        {
            characters.Add(candidate.Clone());
            return;
        }

        var merged = characters[existing];
        var incoming = candidate.Clone();
        if (incoming.Source < merged.Source || merged.Source == DadCharacterSource.XadbOnly)
            merged.Source = incoming.Source;
        if (!incoming.AccountKey.IsEmpty)
            merged.AccountKey = incoming.AccountKey;
        if (!string.IsNullOrWhiteSpace(incoming.AccountAlias))
            merged.AccountAlias = incoming.AccountAlias;
        if (!incoming.CharacterKey.IsEmpty)
            merged.CharacterKey = incoming.CharacterKey;
        if (incoming.ContentId != 0)
            merged.ContentId = incoming.ContentId;
        if (!string.IsNullOrWhiteSpace(incoming.CharacterName))
            merged.CharacterName = incoming.CharacterName;
        if (incoming.WorldId.HasValue)
            merged.WorldId = incoming.WorldId;
        if (!string.IsNullOrWhiteSpace(incoming.WorldName))
            merged.WorldName = incoming.WorldName;
        if (incoming.DataCenterId.HasValue)
            merged.DataCenterId = incoming.DataCenterId;
        if (!string.IsNullOrWhiteSpace(incoming.DataCenterName))
            merged.DataCenterName = incoming.DataCenterName;
        merged.LastSnapshotUtc = MaxDate(merged.LastSnapshotUtc, incoming.LastSnapshotUtc);
        merged.LastRuntimeSeenUtc = MaxDate(merged.LastRuntimeSeenUtc, incoming.LastRuntimeSeenUtc);
        foreach (var pair in incoming.JobLevels)
            merged.JobLevels[pair.Key] = pair.Value;
        merged.CurrentJobId ??= incoming.CurrentJobId;
        if (string.IsNullOrWhiteSpace(merged.CurrentJobAbbrev))
            merged.CurrentJobAbbrev = incoming.CurrentJobAbbrev;
        merged.CurrentLevel ??= incoming.CurrentLevel;
        if (string.IsNullOrWhiteSpace(merged.SnapshotQuality))
            merged.SnapshotQuality = incoming.SnapshotQuality;
        merged.SnapshotVersion ??= incoming.SnapshotVersion;
        merged.XadbReady = merged.XadbReady || incoming.XadbReady;
        merged.IsCurrent = merged.IsCurrent || incoming.IsCurrent;
        merged.MapEligible ??= incoming.MapEligible;
        if (string.IsNullOrWhiteSpace(merged.MapEligibilitySummary))
            merged.MapEligibilitySummary = incoming.MapEligibilitySummary;
        foreach (var blocker in incoming.Blockers)
            AddBlocker(merged, blocker);
        foreach (var warning in incoming.Warnings)
        {
            if (merged.Warnings.All(existingWarning => !string.Equals(existingWarning, warning, StringComparison.OrdinalIgnoreCase)))
                merged.Warnings.Add(warning);
        }
    }

    private static DateTime? MaxDate(DateTime? left, DateTime? right)
    {
        if (!left.HasValue)
            return right;
        if (!right.HasValue)
            return left;
        return left.Value >= right.Value ? left : right;
    }

    private DadRosterVisibilityRecord? FindVisibilityRecord(
        DadCharacterKey characterKey,
        DadAccountKey accountKey,
        ulong contentId = 0)
        => configuration.RosterCatalog.Visibility.FirstOrDefault(record =>
            RecordMatches(record, characterKey, accountKey, contentId));

    private static bool RecordMatches(
        DadRosterVisibilityRecord record,
        DadCharacterKey characterKey,
        DadAccountKey accountKey,
        ulong contentId)
    {
        if (!DadRosterIdentity.SameAccount(record.AccountKey, accountKey))
            return false;

        if (record.ContentId != 0 && contentId != 0)
            return record.ContentId == contentId;

        return !string.IsNullOrWhiteSpace(record.CharacterKey) &&
               string.Equals(record.CharacterKey, characterKey.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RecordMatches(
        DadRosterRefreshRecord record,
        DadCharacterKey characterKey,
        DadAccountKey accountKey,
        ulong contentId)
    {
        if (!DadRosterIdentity.SameAccount(record.AccountKey, accountKey))
            return false;

        if (record.ContentId != 0 && contentId != 0)
            return record.ContentId == contentId;

        return !string.IsNullOrWhiteSpace(record.CharacterKey) &&
               string.Equals(record.CharacterKey, characterKey.Value, StringComparison.OrdinalIgnoreCase);
    }

    private List<DadRosterCharacterRef> ResolveVisibilityTargets(
        DadRosterVisibilityChangeRequest request,
        DadCharacterPool pool)
    {
        var catalog = CurrentCatalog.Characters.Count == 0 ? RefreshCatalog(pool) : CurrentCatalog;
        var explicitRefs = request.CharacterRefs
            .Where(static reference => reference is { IsEmpty: false })
            .Select(static reference => reference.Clone())
            .ToList();
        if (explicitRefs.Count > 0)
        {
            return catalog.Characters
                .Where(character => explicitRefs.Any(reference => DadRosterIdentity.Matches(character, reference)))
                .Select(DadRosterIdentity.From)
                .Concat(explicitRefs)
                .Where(static target => !target.CharacterKey.IsEmpty || target.ContentId != 0)
                .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var targets = catalog.Characters
            .Where(character => request.CharacterKeys.Count == 0 ||
                                request.CharacterKeys.Any(key => string.Equals(key.Value, character.CharacterKey.Value, StringComparison.OrdinalIgnoreCase)))
            .Where(character => request.AccountKeys.Count == 0 ||
                                request.AccountKeys.Any(key => DadRosterIdentity.SameAccount(key, character.AccountKey)))
            .Select(DadRosterIdentity.From)
            .Where(static target => !target.CharacterKey.IsEmpty || target.ContentId != 0)
            .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var key in request.CharacterKeys.Where(static key => !key.IsEmpty))
        {
            if (targets.All(target => !string.Equals(target.CharacterKey.Value, key.Value, StringComparison.OrdinalIgnoreCase)))
            {
                targets.Add(new DadRosterCharacterRef
                {
                    CharacterKey = key,
                    AccountKey = request.AccountKeys.Count == 1 ? request.AccountKeys[0] : new DadAccountKey(string.Empty),
                });
            }
        }

        return targets;
    }

    private static void StampCatalogAccount(DadAccountRosterCatalog catalog, DadCharacterPool pool)
    {
        var accountSource = pool.Characters
            .OrderByDescending(static character => character.Source == DadCharacterSource.LocalRuntime)
            .FirstOrDefault(character =>
                !string.IsNullOrWhiteSpace(character.AccountId) ||
                !string.IsNullOrWhiteSpace(character.AccountAlias));
        if (accountSource == null)
            return;

        var accountKey = ResolveAccountKey(accountSource);
        if (accountKey.IsEmpty)
            return;

        var accountAlias = accountSource.AccountAlias?.Trim() ?? string.Empty;
        foreach (var character in catalog.Characters)
        {
            if (character.AccountKey.IsEmpty)
                character.AccountKey = accountKey;
            if (string.IsNullOrWhiteSpace(character.AccountAlias))
                character.AccountAlias = accountAlias;
        }
    }

    private static DadRosterCharacter FromAcquiredCharacter(DadAcquiredCharacter character)
        => new()
        {
            AccountKey = ResolveAccountKey(character),
            AccountAlias = character.AccountAlias,
            CharacterKey = new DadCharacterKey(character.CharacterKey),
            ContentId = character.ContentId,
            CharacterName = character.CharacterName,
            WorldId = character.WorldId == 0 ? null : character.WorldId,
            WorldName = character.WorldName,
            DataCenterId = character.DataCenterId,
            DataCenterName = character.DataCenterName,
            LastSnapshotUtc = character.XadbSnapshotUtc,
            LastRuntimeSeenUtc = character.LastSeenUtc,
            JobLevels = new Dictionary<uint, int>(character.JobLevels),
            CurrentJobId = character.CurrentJobId,
            CurrentJobAbbrev = character.CurrentJobAbbrev,
            CurrentLevel = character.CurrentLevel,
            SnapshotQuality = character.SnapshotQuality,
            SnapshotVersion = character.SnapshotVersion,
            XadbReady = character.XadbReady,
            IsCurrent = character.Source is DadCharacterSource.LocalRuntime or DadCharacterSource.PeerRuntime,
            Source = character.Source,
            MapEligible = character.MapEligible,
            MapEligibilitySummary = character.MapEligibilitySummary,
            Blockers = [..character.Blockers],
        };

    private static DadAcquiredCharacter ToAcquiredCharacter(DadRosterCharacter character)
        => new()
        {
            CharacterKey = character.CharacterKey.Value,
            ContentId = character.ContentId,
            CharacterName = character.CharacterName,
            WorldId = character.WorldId ?? 0,
            WorldName = character.WorldName,
            DataCenterId = character.DataCenterId,
            DataCenterName = character.DataCenterName,
            AccountId = character.AccountKey.Value,
            AccountAlias = character.AccountAlias,
            Source = character.Source,
            Freshness = ResolveFreshness(character),
            LastSeenUtc = character.LastRuntimeSeenUtc ?? character.LastSnapshotUtc,
            XadbSnapshotUtc = character.LastSnapshotUtc,
            CurrentJobId = character.CurrentJobId,
            CurrentJobAbbrev = character.CurrentJobAbbrev,
            CurrentLevel = character.CurrentLevel,
            JobLevels = new Dictionary<uint, int>(character.JobLevels),
            TerritoryName = character.Source == DadCharacterSource.XadbOnly ? "stored only" : string.Empty,
            Readiness = ResolveReadiness(character),
            Blockers = [..character.Blockers],
            SnapshotQuality = character.SnapshotQuality,
            SnapshotVersion = character.SnapshotVersion,
            XadbReady = character.XadbReady,
            RosterVisibility = character.Visibility,
            MapEligible = character.MapEligible,
            MapEligibilitySummary = character.MapEligibilitySummary,
        };

    private static DadSnapshotFreshness ResolveFreshness(DadRosterCharacter character)
    {
        var snapshotUtc = character.LastRuntimeSeenUtc ?? character.LastSnapshotUtc;
        if (!snapshotUtc.HasValue)
            return DadSnapshotFreshness.Unknown;

        var age = DateTime.UtcNow - snapshotUtc.Value;
        if (age <= TimeSpan.FromMinutes(1))
            return DadSnapshotFreshness.Live;
        if (age <= TimeSpan.FromMinutes(15))
            return DadSnapshotFreshness.Recent;
        return DadSnapshotFreshness.Stale;
    }

    private static DadReadinessState ResolveReadiness(DadRosterCharacter character)
    {
        if (character.Visibility != DadRosterVisibility.Active)
            return DadReadinessState.Unavailable;

        if (character.Source is DadCharacterSource.LocalRuntime or DadCharacterSource.PeerRuntime)
            return character.Blockers.Count == 0 ? DadReadinessState.Ready : DadReadinessState.Blocked;

        return DadReadinessState.Unavailable;
    }

    private static DadAccountKey ResolveAccountKey(DadAcquiredCharacter character)
        => DadRosterIdentity.ResolveAccountKey(character.AccountId, character.AccountAlias);

    private static bool ShouldIncludeForPlanner(
        DadRosterVisibility visibility,
        bool includeHidden,
        bool includeIgnored,
        bool includeNeedsUpdate)
        => visibility == DadRosterVisibility.Active
           || includeHidden && visibility == DadRosterVisibility.Hidden
           || includeIgnored && visibility == DadRosterVisibility.Ignored
           || includeNeedsUpdate && visibility == DadRosterVisibility.NeedsUpdate;

    private static bool ShouldIncludeInCatalog(DadRosterVisibility visibility, DadRosterRefreshPlan plan)
        => visibility == DadRosterVisibility.Active
           || visibility == DadRosterVisibility.NeedsUpdate
           || plan.IncludeHidden && visibility == DadRosterVisibility.Hidden
           || plan.IncludeIgnored && visibility == DadRosterVisibility.Ignored;

    private static void AddBlocker(DadRosterCharacter character, string blocker)
    {
        if (string.IsNullOrWhiteSpace(blocker) ||
            character.Blockers.Any(existing => string.Equals(existing, blocker, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        character.Blockers.Add(blocker);
    }

    private void TrimRefreshHistory()
    {
        configuration.RosterCatalog.RefreshHistory = configuration.RosterCatalog.RefreshHistory
            .OrderByDescending(static record => record.RefreshedAtUtc ?? record.RequestedAtUtc)
            .Take(256)
            .ToList();
    }

    private static string BuildCatalogSummary(DadAccountRosterCatalog catalog)
    {
        var active = catalog.Characters.Count(static character => character.Visibility == DadRosterVisibility.Active);
        var hidden = catalog.Characters.Count(static character => character.Visibility == DadRosterVisibility.Hidden);
        var ignored = catalog.Characters.Count(static character => character.Visibility == DadRosterVisibility.Ignored);
        var needsUpdate = catalog.Characters.Count(static character => character.Visibility == DadRosterVisibility.NeedsUpdate);
        var stale = catalog.Characters.Count(static character => character.IsStale);
        return $"{active} active, {hidden} hidden, {ignored} ignored, {needsUpdate} need update, {stale} stale.";
    }
}
