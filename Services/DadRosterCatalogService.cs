using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadRosterCatalogService
{
    private const string XadbRosterIpcMissingWarning = "XADB roster IPC missing; XADatabase loaded 20-channel/old provider.";

    private readonly Configuration configuration;
    private readonly ConfigManager configManager;
    private readonly DadXadbClient xadbClient;
    private readonly DadTransportService transportService;
    private readonly DadPresenceService presenceService;
    private readonly IPluginLog log;
    private DadAccountRosterCatalog currentCatalog = new() { Summary = "Roster catalog not refreshed yet." };
    private IReadOnlyList<DadPeerRosterCatalogResponse> lastPeerResponses = [];

    public DadRosterCatalogService(
        Configuration configuration,
        ConfigManager configManager,
        DadXadbClient xadbClient,
        DadTransportService transportService,
        DadPresenceService presenceService,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.configManager = configManager;
        this.xadbClient = xadbClient;
        this.transportService = transportService;
        this.presenceService = presenceService;
        this.log = log;
        configuration.RosterCatalog ??= new DadRosterCatalogConfiguration();
        configuration.RosterCatalog.KnownCharacters ??= [];
        configuration.RosterCatalog.Visibility ??= [];
        configuration.RosterCatalog.RefreshHistory ??= [];
    }

    public DadAccountRosterCatalog CurrentCatalog => currentCatalog.Clone();

    public IReadOnlyList<DadRosterAccountOption> GetAccountDirectory()
    {
        var accounts = currentCatalog.Accounts.Count > 0
            ? currentCatalog.Accounts
            : BuildLocalAccountDirectory([]);

        return accounts
            .Select(static account => account.Clone())
            .OrderByDescending(static account => account.IsLocal)
            .ThenBy(static account => account.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static account => account.AccountKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static account => account.SourceClientInstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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

        var xadbCatalog = xadbClient.GetAccountCharacterList();
        var accountSaveNeeded = SeedKnownCharactersFromAccountConfigs();
        var catalog = BuildKnownRosterCatalog();
        catalog.GeneratedAtUtc = xadbCatalog.GeneratedAtUtc;
        catalog.SourceClientInstanceId = presenceService.ClientInstanceId;
        catalog.SourceWorkerSessionId = presenceService.WorkerSessionId;
        catalog.IsFullRosterAvailable = xadbCatalog.IsFullRosterAvailable;
        foreach (var warning in xadbCatalog.Warnings)
            AddWarning(catalog.Warnings, warning);

        if (!xadbCatalog.IsFullRosterAvailable)
            AddWarning(catalog.Warnings, XadbRosterIpcMissingWarning);

        var localRuntimeRows = pool.Characters
            .Where(static character => character.Source == DadCharacterSource.LocalRuntime)
            .Select(FromAcquiredCharacter)
            .ToList();
        var currentAccount = ResolveCurrentAccount(localRuntimeRows);
        foreach (var character in AttributeXadbCharacters(xadbCatalog.Characters, currentAccount, catalog.Warnings))
        {
            UpsertRosterCharacter(catalog.Characters, character);
            accountSaveNeeded |= UpsertKnownCharacter(character);
        }

        foreach (var character in pool.Characters)
        {
            var rosterCharacter = FromAcquiredCharacter(character);
            UpsertRosterCharacter(catalog.Characters, rosterCharacter);
            if (character.Source == DadCharacterSource.LocalRuntime)
                accountSaveNeeded |= UpsertKnownCharacter(rosterCharacter);
        }

        if (accountSaveNeeded)
            configuration.Save();

        StampCatalogSource(catalog, catalog.SourceClientInstanceId, catalog.SourceWorkerSessionId);
        ApplyVisibility(catalog, plan);
        catalog.Accounts = BuildLocalAccountDirectory(catalog.Characters).ToList();
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
            IncludeHidden = true,
            IncludeIgnored = true,
            StaleAfterHours = configuration.RosterCatalog.StaleAfterHours,
        };
        var catalog = RefreshCatalog(pool, plan);

        var filteredCharacters = catalog.Characters
            .Where(static character => !character.AccountKey.IsEmpty)
            .Where(static character => character.Visibility == DadRosterVisibility.Active)
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

    public DadAccountRosterCatalog ChangeAssignment(DadRosterAssignmentChangeRequest request, DadCharacterPool pool)
    {
        configuration.RosterCatalog ??= new DadRosterCatalogConfiguration();
        configuration.RosterCatalog.KnownCharacters ??= [];
        var catalog = RefreshCatalog(pool, new DadRosterRefreshPlan
        {
            IncludeHidden = true,
            IncludeIgnored = true,
            StaleAfterHours = configuration.RosterCatalog.StaleAfterHours,
        });
        var character = catalog.Characters.FirstOrDefault(candidate =>
            DadRosterIdentity.Matches(candidate, request.CharacterRef));
        if (character == null)
        {
            AddWarning(catalog.Warnings, "Roster assignment target no longer exists in current catalog.");
            return catalog;
        }

        if (IsRemoteSource(character))
        {
            AddWarning(catalog.Warnings, "Assign roster rows on the Dad client that owns the source XADB snapshots.");
            return catalog;
        }

        if (request.ClearAssignment)
        {
            var removedKnown = RemoveKnownCharacter(character);
            var removedAccountConfig = configManager.RemoveCharacterFromAccount(character.AccountKey, character.CharacterKey);
            if (removedKnown || removedAccountConfig)
                configuration.Save();

            return RefreshCatalog(pool, new DadRosterRefreshPlan
            {
                IncludeHidden = true,
                IncludeIgnored = true,
                StaleAfterHours = configuration.RosterCatalog.StaleAfterHours,
            });
        }

        if (request.AccountKey.IsEmpty)
        {
            AddWarning(catalog.Warnings, "Roster assignment needs a Dad account.");
            return catalog;
        }

        var account = configManager.GetAccount(request.AccountKey);
        if (account == null)
        {
            AddWarning(catalog.Warnings, "Can only assign roster rows to accounts known by this Dad config.");
            return catalog;
        }

        var assigned = character.Clone();
        assigned.AccountKey = new DadAccountKey(account.AccountId);
        assigned.AccountAlias = string.IsNullOrWhiteSpace(request.AccountAlias)
            ? account.AccountAlias
            : request.AccountAlias.Trim();
        assigned.Blockers.RemoveAll(static blocker =>
            blocker.Contains("Account attribution missing", StringComparison.OrdinalIgnoreCase));
        assigned.Warnings.RemoveAll(static warning =>
            warning.Contains("no Dad account attribution", StringComparison.OrdinalIgnoreCase));

        var changed = UpsertKnownCharacter(assigned);
        configManager.EnsureCharacterForAccount(
            assigned.AccountKey,
            assigned.CharacterKey.Value,
            assigned.CharacterName,
            assigned.WorldName);
        if (changed)
            configuration.Save();

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
            {
                var candidate = character.Clone();
                if (string.IsNullOrWhiteSpace(candidate.SourceClientInstanceId))
                    candidate.SourceClientInstanceId = catalog.SourceClientInstanceId;
                if (candidate.SourceWorkerSessionId.IsEmpty)
                    candidate.SourceWorkerSessionId = catalog.SourceWorkerSessionId;
                UpsertRosterCharacter(merged.Characters, candidate);
            }
        }

        merged.Accounts = BuildMergedAccountDirectory(catalogs, merged.Characters).ToList();
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
            if (character.AccountKey.IsEmpty)
                AddBlocker(character, "Account attribution missing; excluded from preset and scheduler planning.");

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
        if (!string.IsNullOrWhiteSpace(incoming.SourceClientInstanceId))
            merged.SourceClientInstanceId = incoming.SourceClientInstanceId;
        if (!incoming.SourceWorkerSessionId.IsEmpty)
            merged.SourceWorkerSessionId = incoming.SourceWorkerSessionId;
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

    private DadAccountRosterCatalog BuildKnownRosterCatalog()
    {
        configuration.RosterCatalog.KnownCharacters ??= [];
        return new DadAccountRosterCatalog
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Characters = configuration.RosterCatalog.KnownCharacters
                .Where(static record => !record.AccountKey.IsEmpty)
                .Select(ToRosterCharacter)
                .ToList(),
        };
    }

    private bool SeedKnownCharactersFromAccountConfigs()
    {
        var changed = false;
        foreach (var account in configManager.GetAllAccounts())
        {
            var accountKey = new DadAccountKey(account.AccountId);
            if (accountKey.IsEmpty)
                continue;

            foreach (var characterKey in account.Characters.Keys)
            {
                var parsed = ParseCharacterKey(characterKey);
                changed |= UpsertKnownCharacter(new DadRosterCharacter
                {
                    AccountKey = accountKey,
                    AccountAlias = account.AccountAlias,
                    CharacterKey = new DadCharacterKey(characterKey),
                    CharacterName = parsed.CharacterName,
                    WorldName = parsed.WorldName,
                    Source = DadCharacterSource.ManualUnresolved,
                });
            }
        }

        return changed;
    }

    private bool RemoveKnownCharacter(DadRosterCharacter character)
    {
        var before = configuration.RosterCatalog.KnownCharacters.Count;
        configuration.RosterCatalog.KnownCharacters = configuration.RosterCatalog.KnownCharacters
            .Where(record =>
                !DadRosterIdentity.SameAccount(record.AccountKey, character.AccountKey) ||
                !DadRosterIdentity.SameCharacter(
                    new DadCharacterKey(record.CharacterKey),
                    record.ContentId,
                    character.CharacterKey,
                    character.ContentId))
            .ToList();
        return before != configuration.RosterCatalog.KnownCharacters.Count;
    }

    private bool IsRemoteSource(DadRosterCharacter character)
        => !string.IsNullOrWhiteSpace(character.SourceClientInstanceId) &&
           !string.Equals(character.SourceClientInstanceId, presenceService.ClientInstanceId, StringComparison.OrdinalIgnoreCase);

    private static void StampCatalogSource(
        DadAccountRosterCatalog catalog,
        string sourceClientInstanceId,
        DadWorkerSessionId sourceWorkerSessionId)
    {
        foreach (var character in catalog.Characters)
        {
            if (string.IsNullOrWhiteSpace(character.SourceClientInstanceId))
                character.SourceClientInstanceId = sourceClientInstanceId;
            if (character.SourceWorkerSessionId.IsEmpty)
                character.SourceWorkerSessionId = sourceWorkerSessionId;
        }
    }

    private IReadOnlyList<DadRosterAccountOption> BuildLocalAccountDirectory(IReadOnlyList<DadRosterCharacter> characters)
    {
        var options = new List<DadRosterAccountOption>();
        foreach (var account in configManager.GetAllAccounts())
        {
            var accountKey = new DadAccountKey(account.AccountId);
            if (accountKey.IsEmpty)
                continue;

            UpsertAccountOption(options, new DadRosterAccountOption
            {
                AccountKey = accountKey,
                AccountAlias = account.AccountAlias,
                DisplayName = BuildAccountDisplayName(account.AccountId, account.AccountAlias),
                SourceClientInstanceId = presenceService.ClientInstanceId,
                SourceWorkerSessionId = presenceService.WorkerSessionId,
                IsLocal = true,
                AssignedCharacterCount = account.Characters.Count,
            });
        }

        AddCatalogCharacterAccounts(options, characters, presenceService.ClientInstanceId, presenceService.WorkerSessionId);
        RefreshAccountCharacterCounts(options, characters);
        return SortAccountOptions(options);
    }

    private IReadOnlyList<DadRosterAccountOption> BuildMergedAccountDirectory(
        IReadOnlyList<DadAccountRosterCatalog> catalogs,
        IReadOnlyList<DadRosterCharacter> characters)
    {
        var options = new List<DadRosterAccountOption>();
        foreach (var catalog in catalogs)
        {
            var sourceClientInstanceId = catalog.SourceClientInstanceId;
            var sourceWorkerSessionId = catalog.SourceWorkerSessionId;
            foreach (var account in catalog.Accounts)
            {
                var candidate = account.Clone();
                if (string.IsNullOrWhiteSpace(candidate.SourceClientInstanceId))
                    candidate.SourceClientInstanceId = sourceClientInstanceId;
                if (candidate.SourceWorkerSessionId.IsEmpty)
                    candidate.SourceWorkerSessionId = sourceWorkerSessionId;
                candidate.IsLocal = string.Equals(
                    candidate.SourceClientInstanceId,
                    presenceService.ClientInstanceId,
                    StringComparison.OrdinalIgnoreCase);
                candidate.DisplayName = BuildAccountDisplayName(candidate.AccountKey.Value, candidate.AccountAlias);
                UpsertAccountOption(options, candidate);
            }

            if (catalog.Accounts.Count == 0)
                AddCatalogCharacterAccounts(options, catalog.Characters, sourceClientInstanceId, sourceWorkerSessionId);
        }

        RefreshAccountCharacterCounts(options, characters);
        return SortAccountOptions(options);
    }

    private void AddCatalogCharacterAccounts(
        List<DadRosterAccountOption> options,
        IReadOnlyList<DadRosterCharacter> characters,
        string sourceClientInstanceId,
        DadWorkerSessionId sourceWorkerSessionId)
    {
        foreach (var group in characters
                     .Where(static character => !character.AccountKey.IsEmpty)
                     .GroupBy(character => new
                     {
                         SourceClientInstanceId = string.IsNullOrWhiteSpace(character.SourceClientInstanceId)
                             ? sourceClientInstanceId
                             : character.SourceClientInstanceId,
                         AccountKey = character.AccountKey.Value,
                     }))
        {
            var alias = group
                .Select(static character => character.AccountAlias)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
            var workerSessionId = group
                .Select(static character => character.SourceWorkerSessionId)
                .FirstOrDefault(static value => !value.IsEmpty);
            if (workerSessionId.IsEmpty)
                workerSessionId = sourceWorkerSessionId;

            UpsertAccountOption(options, new DadRosterAccountOption
            {
                AccountKey = new DadAccountKey(group.Key.AccountKey),
                AccountAlias = alias,
                DisplayName = BuildAccountDisplayName(group.Key.AccountKey, alias),
                SourceClientInstanceId = group.Key.SourceClientInstanceId,
                SourceWorkerSessionId = workerSessionId,
                IsLocal = string.Equals(group.Key.SourceClientInstanceId, presenceService.ClientInstanceId, StringComparison.OrdinalIgnoreCase),
                AssignedCharacterCount = group
                    .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
                    .Count(),
            });
        }
    }

    private static void RefreshAccountCharacterCounts(
        List<DadRosterAccountOption> options,
        IReadOnlyList<DadRosterCharacter> characters)
    {
        foreach (var option in options)
        {
            var count = characters
                .Where(character => !character.AccountKey.IsEmpty &&
                                    DadRosterIdentity.SameAccount(character.AccountKey, option.AccountKey) &&
                                    string.Equals(
                                        character.SourceClientInstanceId,
                                        option.SourceClientInstanceId,
                                        StringComparison.OrdinalIgnoreCase))
                .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
                .Count();
            option.AssignedCharacterCount = Math.Max(option.AssignedCharacterCount, count);
        }
    }

    private static void UpsertAccountOption(List<DadRosterAccountOption> options, DadRosterAccountOption candidate)
    {
        if (candidate.AccountKey.IsEmpty)
            return;

        candidate.AccountAlias = candidate.AccountAlias?.Trim() ?? string.Empty;
        candidate.DisplayName = BuildAccountDisplayName(candidate.AccountKey.Value, candidate.AccountAlias);
        candidate.SourceClientInstanceId = candidate.SourceClientInstanceId?.Trim() ?? string.Empty;
        var existing = options.FirstOrDefault(option =>
            string.Equals(option.SourceClientInstanceId, candidate.SourceClientInstanceId, StringComparison.OrdinalIgnoreCase) &&
            DadRosterIdentity.SameAccount(option.AccountKey, candidate.AccountKey));
        if (existing == null)
        {
            options.Add(candidate.Clone());
            return;
        }

        if (string.IsNullOrWhiteSpace(existing.AccountAlias) ||
            string.Equals(existing.AccountAlias, existing.AccountKey.Value, StringComparison.OrdinalIgnoreCase))
        {
            existing.AccountAlias = candidate.AccountAlias;
            existing.DisplayName = candidate.DisplayName;
        }

        if (existing.SourceWorkerSessionId.IsEmpty)
            existing.SourceWorkerSessionId = candidate.SourceWorkerSessionId;
        existing.IsLocal |= candidate.IsLocal;
        existing.AssignedCharacterCount = Math.Max(existing.AssignedCharacterCount, candidate.AssignedCharacterCount);
    }

    private static IReadOnlyList<DadRosterAccountOption> SortAccountOptions(List<DadRosterAccountOption> options)
        => options
            .OrderByDescending(static option => option.IsLocal)
            .ThenBy(static option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static option => option.AccountKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static option => option.SourceClientInstanceId, StringComparer.OrdinalIgnoreCase)
            .Select(static option => option.Clone())
            .ToList();

    private static string BuildAccountDisplayName(string accountKey, string accountAlias)
        => string.IsNullOrWhiteSpace(accountAlias)
            ? accountKey
            : accountAlias.Trim();

    private static (string CharacterName, string WorldName) ParseCharacterKey(string characterKey)
    {
        var parts = (characterKey ?? string.Empty).Split('@', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (parts[0], parts[1])
            : (characterKey ?? string.Empty, string.Empty);
    }

    private List<DadRosterCharacter> AttributeXadbCharacters(
        IReadOnlyList<DadRosterCharacter> xadbCharacters,
        CurrentRosterAccount currentAccount,
        List<string> warnings)
    {
        var attributed = new List<DadRosterCharacter>();
        var ambiguousCount = 0;
        foreach (var character in xadbCharacters)
        {
            if (!character.AccountKey.IsEmpty)
            {
                attributed.Add(character.Clone());
                continue;
            }

            var knownMatches = FindKnownCharacterMatches(character);
            if (knownMatches.Count > 0)
            {
                foreach (var known in knownMatches)
                    attributed.Add(WithAccount(character, known.AccountKey, known.AccountAlias));
                continue;
            }

            if (MatchesCurrentAccount(character, currentAccount))
            {
                attributed.Add(WithAccount(character, currentAccount.AccountKey, currentAccount.AccountAlias));
                continue;
            }

            ambiguousCount++;
            var ambiguous = character.Clone();
            AddBlocker(ambiguous, "Account attribution missing; log into this character/account in Dad before scheduling.");
            if (ambiguous.Warnings.All(static warning => !string.Equals(warning, "XADB row has no Dad account attribution.", StringComparison.OrdinalIgnoreCase)))
                ambiguous.Warnings.Add("XADB row has no Dad account attribution.");
            attributed.Add(ambiguous);
        }

        if (ambiguousCount > 0)
            AddWarning(warnings, $"Skipped scheduling for {ambiguousCount} XADB roster row(s) with no Dad account attribution.");

        return attributed;
    }

    private List<DadRosterKnownCharacterRecord> FindKnownCharacterMatches(DadRosterCharacter character)
        => configuration.RosterCatalog.KnownCharacters
            .Where(static record => !record.AccountKey.IsEmpty)
            .Where(record => DadRosterIdentity.SameCharacter(
                new DadCharacterKey(record.CharacterKey),
                record.ContentId,
                character.CharacterKey,
                character.ContentId))
            .GroupBy(static record => record.AccountKey.Value, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(record => record.LastSnapshotUtc ?? record.LastRuntimeSeenUtc ?? record.UpdatedAtUtc)
                .First())
            .ToList();

    private static CurrentRosterAccount ResolveCurrentAccount(IReadOnlyList<DadRosterCharacter> localRuntimeRows)
    {
        var source = localRuntimeRows.FirstOrDefault(static character => !character.AccountKey.IsEmpty);
        if (source == null)
            return CurrentRosterAccount.Empty;

        var characterKeys = localRuntimeRows
            .Where(static character => !character.CharacterKey.IsEmpty)
            .Select(static character => character.CharacterKey.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contentIds = localRuntimeRows
            .Where(static character => character.ContentId != 0)
            .Select(static character => character.ContentId)
            .ToHashSet();

        return new CurrentRosterAccount(source.AccountKey, source.AccountAlias, characterKeys, contentIds);
    }

    private static bool MatchesCurrentAccount(DadRosterCharacter character, CurrentRosterAccount account)
    {
        if (account.AccountKey.IsEmpty)
            return false;

        if (character.ContentId != 0 && account.ContentIds.Contains(character.ContentId))
            return true;

        return !character.CharacterKey.IsEmpty && account.CharacterKeys.Contains(character.CharacterKey.Value);
    }

    private static DadRosterCharacter WithAccount(
        DadRosterCharacter source,
        DadAccountKey accountKey,
        string accountAlias)
    {
        var character = source.Clone();
        character.AccountKey = accountKey;
        if (!string.IsNullOrWhiteSpace(accountAlias))
            character.AccountAlias = accountAlias.Trim();
        return character;
    }

    private bool UpsertKnownCharacter(DadRosterCharacter character)
    {
        configuration.RosterCatalog.KnownCharacters ??= [];
        if (character.AccountKey.IsEmpty || character.CharacterKey.IsEmpty && character.ContentId == 0)
            return false;

        var incoming = ToKnownRecord(character);
        var existingIndex = configuration.RosterCatalog.KnownCharacters.FindIndex(record =>
            DadRosterIdentity.SameAccount(record.AccountKey, incoming.AccountKey) &&
            DadRosterIdentity.SameCharacter(
                new DadCharacterKey(record.CharacterKey),
                record.ContentId,
                new DadCharacterKey(incoming.CharacterKey),
                incoming.ContentId));

        if (existingIndex < 0)
        {
            configuration.RosterCatalog.KnownCharacters.Add(incoming);
            return true;
        }

        var existing = configuration.RosterCatalog.KnownCharacters[existingIndex];
        var mergeList = new List<DadRosterCharacter> { ToRosterCharacter(existing) };
        UpsertRosterCharacter(mergeList, character);
        var merged = ToKnownRecord(mergeList[0]);
        if (KnownRecordPayloadEquals(existing, merged))
            return false;

        configuration.RosterCatalog.KnownCharacters[existingIndex] = merged;
        return true;
    }

    private static DadRosterKnownCharacterRecord ToKnownRecord(DadRosterCharacter character)
        => new()
        {
            AccountKey = character.AccountKey,
            AccountAlias = character.AccountAlias,
            CharacterKey = character.CharacterKey.Value,
            ContentId = character.ContentId,
            CharacterName = character.CharacterName,
            WorldId = character.WorldId,
            WorldName = character.WorldName,
            DataCenterId = character.DataCenterId,
            DataCenterName = character.DataCenterName,
            LastSnapshotUtc = character.LastSnapshotUtc,
            LastRuntimeSeenUtc = character.LastRuntimeSeenUtc,
            JobLevels = new Dictionary<uint, int>(character.JobLevels),
            CurrentJobId = character.CurrentJobId,
            CurrentJobAbbrev = character.CurrentJobAbbrev,
            CurrentLevel = character.CurrentLevel,
            SnapshotQuality = character.SnapshotQuality,
            SnapshotVersion = character.SnapshotVersion,
            XadbReady = character.XadbReady,
            MapEligible = character.MapEligible,
            MapEligibilitySummary = character.MapEligibilitySummary,
            UpdatedAtUtc = DateTime.UtcNow,
        };

    private static DadRosterCharacter ToRosterCharacter(DadRosterKnownCharacterRecord record)
        => new()
        {
            AccountKey = record.AccountKey,
            AccountAlias = record.AccountAlias,
            CharacterKey = new DadCharacterKey(record.CharacterKey),
            ContentId = record.ContentId,
            CharacterName = record.CharacterName,
            WorldId = record.WorldId,
            WorldName = record.WorldName,
            DataCenterId = record.DataCenterId,
            DataCenterName = record.DataCenterName,
            LastSnapshotUtc = record.LastSnapshotUtc,
            LastRuntimeSeenUtc = record.LastRuntimeSeenUtc,
            JobLevels = new Dictionary<uint, int>(record.JobLevels),
            CurrentJobId = record.CurrentJobId,
            CurrentJobAbbrev = record.CurrentJobAbbrev,
            CurrentLevel = record.CurrentLevel,
            SnapshotQuality = record.SnapshotQuality,
            SnapshotVersion = record.SnapshotVersion,
            XadbReady = record.XadbReady,
            MapEligible = record.MapEligible,
            MapEligibilitySummary = record.MapEligibilitySummary,
            Source = record.XadbReady || record.LastSnapshotUtc.HasValue
                ? DadCharacterSource.XadbOnly
                : DadCharacterSource.ManualUnresolved,
        };

    private static bool KnownRecordPayloadEquals(
        DadRosterKnownCharacterRecord left,
        DadRosterKnownCharacterRecord right)
        => DadRosterIdentity.SameAccount(left.AccountKey, right.AccountKey)
           && string.Equals(left.AccountAlias, right.AccountAlias, StringComparison.Ordinal)
           && string.Equals(left.CharacterKey, right.CharacterKey, StringComparison.Ordinal)
           && left.ContentId == right.ContentId
           && string.Equals(left.CharacterName, right.CharacterName, StringComparison.Ordinal)
           && left.WorldId == right.WorldId
           && string.Equals(left.WorldName, right.WorldName, StringComparison.Ordinal)
           && left.DataCenterId == right.DataCenterId
           && string.Equals(left.DataCenterName, right.DataCenterName, StringComparison.Ordinal)
           && left.LastSnapshotUtc == right.LastSnapshotUtc
           && left.LastRuntimeSeenUtc == right.LastRuntimeSeenUtc
           && left.CurrentJobId == right.CurrentJobId
           && string.Equals(left.CurrentJobAbbrev, right.CurrentJobAbbrev, StringComparison.Ordinal)
           && left.CurrentLevel == right.CurrentLevel
           && string.Equals(left.SnapshotQuality, right.SnapshotQuality, StringComparison.Ordinal)
           && left.SnapshotVersion == right.SnapshotVersion
           && left.XadbReady == right.XadbReady
           && left.MapEligible == right.MapEligible
           && string.Equals(left.MapEligibilitySummary, right.MapEligibilitySummary, StringComparison.Ordinal)
           && DictionariesEqual(left.JobLevels, right.JobLevels);

    private static bool DictionariesEqual(Dictionary<uint, int> left, Dictionary<uint, int> right)
        => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private sealed record CurrentRosterAccount(
        DadAccountKey AccountKey,
        string AccountAlias,
        HashSet<string> CharacterKeys,
        HashSet<ulong> ContentIds)
    {
        public static CurrentRosterAccount Empty { get; } = new(
            new DadAccountKey(string.Empty),
            string.Empty,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            []);
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

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (string.IsNullOrWhiteSpace(warning) ||
            warnings.Any(existing => string.Equals(existing, warning, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        warnings.Add(warning);
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
