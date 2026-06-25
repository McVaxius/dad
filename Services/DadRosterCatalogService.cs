using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadRosterCatalogService
{
    private readonly Configuration configuration;
    private readonly ConfigManager configManager;
    private readonly DadXadbClient xadbClient;
    private readonly DadTransportService transportService;
    private readonly DadPresenceService presenceService;
    private readonly IPluginLog log;
    private DadAccountRosterCatalog currentCatalog = new() { Summary = "Roster catalog not refreshed yet." };
    private IReadOnlyList<DadPeerRosterCatalogResponse> lastPeerResponses = [];
    private long catalogVersion;

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
        NormalizeRosterVisibilityRecords(saveIfChanged: true);
    }

    public DadAccountRosterCatalog CurrentCatalog
    {
        get
        {
            var catalog = ApplyOwnerConnectivity(currentCatalog.Clone());
            catalog.Accounts = BuildCurrentAccountDirectory(catalog).ToList();
            return catalog;
        }
    }

    public long CatalogVersion => catalogVersion;

    public IReadOnlyList<DadRosterAccountOption> GetAccountDirectory()
    {
        var connectedCatalog = ApplyOwnerConnectivity(currentCatalog.Clone());
        return BuildCurrentAccountDirectory(connectedCatalog);
    }

    private IReadOnlyList<DadRosterAccountOption> BuildCurrentAccountDirectory(DadAccountRosterCatalog connectedCatalog)
    {
        var accounts = connectedCatalog.Accounts.Select(static account => account.Clone()).ToList();
        foreach (var account in BuildLocalAccountDirectory(
                     connectedCatalog.Characters,
                     transportService.CurrentTransport.KnownParticipants))
        {
            UpsertAccountOption(accounts, account);
        }

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
            IncludeHidden = true,
            IncludeIgnored = true,
            StaleAfterHours = configuration.RosterCatalog.StaleAfterHours,
        };

        var effectivePool = plan.ForcePeerRefresh
            ? WithCurrentTransport(pool, transportService.RequestSnapshots(new DadPeerSnapshotRequest()))
            : pool;
        var localCatalog = BuildLocalCatalog(effectivePool, plan);
        var allCatalogs = new List<DadAccountRosterCatalog> { localCatalog };

        if (plan.ForcePeerRefresh)
        {
            var aggregate = transportService.RequestAggregateRosterCatalogs(plan);
            lastPeerResponses = aggregate.Responses
                .Where(response => !string.Equals(
                    response.WorkerSessionId.Value,
                    presenceService.WorkerSessionId.Value,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (!aggregate.Complete)
                AddWarning(localCatalog.Warnings, aggregate.Summary);
            foreach (var warning in aggregate.Warnings)
                AddWarning(localCatalog.Warnings, warning);
            foreach (var response in lastPeerResponses)
            {
                foreach (var warning in response.Warnings)
                    AddWarning(response.Catalog.Warnings, warning);
            }

            allCatalogs.AddRange(lastPeerResponses.Select(static response => response.Catalog));
            var fallbackSuppressionResponses = new List<DadPeerRosterCatalogResponse>
            {
                new()
                {
                    RequestId = plan.PlanId,
                    RespondedAtUtc = localCatalog.GeneratedAtUtc,
                    ClientInstanceId = localCatalog.SourceClientInstanceId,
                    WorkerSessionId = localCatalog.SourceWorkerSessionId,
                    Catalog = localCatalog,
                },
            };
            fallbackSuppressionResponses.AddRange(lastPeerResponses);
            var peerRuntimeFallback = BuildPeerRuntimeFallbackCatalog(
                transportService.CurrentTransport,
                fallbackSuppressionResponses);
            if (peerRuntimeFallback.Characters.Count > 0)
                allCatalogs.Add(peerRuntimeFallback);
        }

        currentCatalog = ApplyOwnerConnectivity(MergeCatalogs(allCatalogs, plan));
        catalogVersion++;
        if (plan.LogDiagnostics)
            LogRosterDiagnostics(currentCatalog, plan);

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
        var localAccountKey = GetLocalClientAccountKey();
        var localAccountAlias = GetLocalClientAccountAlias();
        var localDadAccounts = BuildLocalDadAccounts();
        var accountSaveNeeded = SeedKnownCharactersFromAccountConfigs();
        accountSaveNeeded |= PruneKnownOwnershipFromRuntime(pool.Characters, localDadAccounts);
        var catalog = BuildKnownRosterCatalog();
        catalog.GeneratedAtUtc = xadbCatalog.GeneratedAtUtc;
        catalog.Version = xadbCatalog.Version;
        catalog.XadbContractVersion = xadbCatalog.XadbContractVersion;
        catalog.XadbPayloadRowCount = xadbCatalog.XadbPayloadRowCount;
        catalog.SourceDiagnostics = xadbCatalog.SourceDiagnostics.Clone();
        catalog.SourceDiagnostics.LocalAccountKey = localAccountKey.Value;
        catalog.SourceDiagnostics.XadbPayloadRows = xadbCatalog.XadbPayloadRowCount;
        catalog.SourceDiagnostics.KnownRosterRows = catalog.Characters.Count;
        catalog.SourceClientInstanceId = presenceService.ClientInstanceId;
        catalog.SourceWorkerSessionId = presenceService.WorkerSessionId;
        catalog.IsFullRosterAvailable = xadbCatalog.IsFullRosterAvailable;
        foreach (var warning in xadbCatalog.Warnings)
            AddWarning(catalog.Warnings, warning);

        if (!xadbCatalog.IsFullRosterAvailable)
            AddWarning(catalog.Warnings, DadXadbClient.RosterIpcMissingWarning);

        var localXadbCharacters = AttributeLocalXadbCharacters(xadbCatalog.Characters, localAccountKey, localAccountAlias, catalog.Warnings);
        catalog.SourceDiagnostics.LocalXadbAttributedRows = localXadbCharacters.Count(static character => !character.AccountKey.IsEmpty);
        if (catalog.SourceDiagnostics.XadbMergedRows > catalog.SourceDiagnostics.LocalXadbAttributedRows)
        {
            AddWarning(
                catalog.Warnings,
                $"XADB advertised {catalog.SourceDiagnostics.XadbMergedRows} merged roster row(s), but Dad attributed {catalog.SourceDiagnostics.LocalXadbAttributedRows} local XADB row(s).");
        }

        foreach (var character in localXadbCharacters)
        {
            UpsertRosterCharacter(catalog.Characters, character, xadbAuthoritative: true);
            accountSaveNeeded |= UpsertKnownCharacter(character, xadbAuthoritative: true);
        }

        var localRuntimeRows = pool.Characters
            .Where(static character => character.Source == DadCharacterSource.LocalRuntime)
            .ToList();
        catalog.SourceDiagnostics.LocalRuntimeRows = localRuntimeRows.Count;
        foreach (var character in localRuntimeRows)
        {
            var rosterCharacter = FromAcquiredCharacter(character);
            if (rosterCharacter.AccountKey.IsEmpty)
                StampCharacterAccount(rosterCharacter, localAccountKey, localAccountAlias);
            UpsertRosterCharacter(catalog.Characters, rosterCharacter);
            accountSaveNeeded |= UpsertKnownCharacter(rosterCharacter);
        }

        if (accountSaveNeeded)
            configuration.Save();

        StampCatalogSource(catalog, catalog.SourceClientInstanceId, catalog.SourceWorkerSessionId);
        ApplyVisibility(catalog, plan);
        catalog.SourceDiagnostics.FinalLocalRows = catalog.Characters.Count;
        foreach (var warning in catalog.Warnings)
            AddWarning(catalog.SourceDiagnostics.Warnings, warning);
        catalog.Accounts = BuildLocalAccountDirectory(catalog.Characters, pool.PeerTransport.KnownParticipants).ToList();
        catalog.Summary = BuildCatalogSummary(catalog);
        return catalog;
    }

    public DadAccountRosterCatalog BuildLocalXadbCatalog(DadRosterRefreshPlan? plan = null)
        => BuildLocalCatalog(new DadCharacterPool
        {
            PeerTransport = transportService.CurrentTransport,
        }, plan);

    public DadAccountRosterCatalog BuildLocalTransportCatalog(
        DadCharacterPool currentPool,
        DadParticipantSnapshot fallbackSnapshot,
        DadRosterRefreshPlan? plan = null)
        => BuildLocalCatalog(
            DadRosterTransportCatalogRuntime.BuildLocalTransportPool(
                currentPool,
                fallbackSnapshot,
                transportService.CurrentTransport),
            plan);

    public DadCharacterPool BuildCuratedPool(
        DadCharacterPool pool,
        bool includeHidden = false,
        bool includeIgnored = false,
        bool includeNeedsUpdate = false)
        => BuildPlannerRosterSnapshot(pool, includeHidden, includeIgnored, includeNeedsUpdate).CuratedPool;

    public DadPlannerRosterSnapshot BuildPlannerRosterSnapshot(
        DadCharacterPool pool,
        bool includeHidden = false,
        bool includeIgnored = false,
        bool includeNeedsUpdate = false)
    {
        var catalog = BuildPlannerPreviewCatalog(pool);

        var filteredCharacters = catalog.Characters
            .Where(static character => !character.AccountKey.IsEmpty)
            .Where(character => ShouldIncludeForPlanner(character, includeHidden, includeIgnored, includeNeedsUpdate))
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
        return new DadPlannerRosterSnapshot
        {
            CuratedPool = curated,
            AccountOptions = catalog.Accounts
                .Select(static account => account.Clone())
                .ToList(),
        };
    }

    public DadAccountRosterCatalog BuildPlannerPreviewCatalog(DadCharacterPool pool)
    {
        var plan = new DadRosterRefreshPlan
        {
            IncludeHidden = true,
            IncludeIgnored = true,
            StaleAfterHours = configuration.RosterCatalog.StaleAfterHours,
        };

        var catalogs = new List<DadAccountRosterCatalog>();
        var cachedCatalog = ApplyOwnerConnectivity(currentCatalog.Clone());
        if (cachedCatalog.Characters.Count > 0 || cachedCatalog.Accounts.Count > 0)
            catalogs.Add(cachedCatalog);
        else
            catalogs.Add(BuildKnownRosterCatalog());

        var runtimeCatalog = BuildRuntimeOverlayCatalog(pool);
        if (runtimeCatalog.Characters.Count > 0 || runtimeCatalog.Accounts.Count > 0)
            catalogs.Add(runtimeCatalog);

        return MergeCatalogs(catalogs, plan);
    }

    public IReadOnlyList<DadRosterAccountOption> BuildPlannerAccountOptions(DadCharacterPool pool)
        => BuildPlannerRosterSnapshot(pool).AccountOptions;

    public DadRosterVisibility ResolveVisibility(DadCharacterKey characterKey, DadAccountKey accountKey)
    {
        var record = FindVisibilityRecord(characterKey, accountKey);
        return NormalizeVisibility(record?.Visibility ?? DadRosterVisibility.Active);
    }

    public bool ResolveNeedsRosterUpdate(DadCharacterKey characterKey, DadAccountKey accountKey)
    {
        var record = FindVisibilityRecord(characterKey, accountKey);
        return record is { NeedsRosterUpdate: true } || record?.Visibility == DadRosterVisibility.NeedsUpdate;
    }

    public bool IsVisibleForNormalPlanning(DadCharacterKey characterKey, DadAccountKey accountKey)
        => ResolveVisibility(characterKey, accountKey) == DadRosterVisibility.Active &&
           !ResolveNeedsRosterUpdate(characterKey, accountKey);

    public DadAccountRosterCatalog SetVisibility(DadRosterVisibilityChangeRequest request, DadCharacterPool pool)
    {
        configuration.RosterCatalog ??= new DadRosterCatalogConfiguration();
        request.CharacterRefs ??= [];
        request.CharacterKeys ??= [];
        request.AccountKeys ??= [];
        var changedKeys = ResolveVisibilityTargets(request, pool);
        var marksRosterUpdate = request.Visibility == DadRosterVisibility.NeedsUpdate;
        var now = DateTime.UtcNow;
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
            if (marksRosterUpdate)
            {
                record.Visibility = NormalizeVisibility(record.Visibility);
                record.NeedsRosterUpdate = true;
            }
            else
            {
                record.Visibility = NormalizeVisibility(request.Visibility);
            }

            record.UpdatedAtUtc = now;
            record.Reason = request.Reason?.Trim() ?? string.Empty;
        }

        configuration.Save();
        if (marksRosterUpdate)
            log.Information("[dad][Roster] Marked {Count} roster row(s) as needing update.", changedKeys.Count);
        else
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

        var localAccountKey = GetLocalClientAccountKey();
        if (localAccountKey.IsEmpty || !DadRosterIdentity.SameAccount(request.AccountKey, localAccountKey))
        {
            AddWarning(catalog.Warnings, "Local roster rows can only be assigned to this Dad client account.");
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

    public bool PurgeAccount(DadAccountKey accountKey)
    {
        configuration.RosterCatalog ??= new DadRosterCatalogConfiguration();
        configuration.RosterCatalog.KnownCharacters ??= [];
        configuration.RosterCatalog.Visibility ??= [];
        configuration.RosterCatalog.RefreshHistory ??= [];

        if (accountKey.IsEmpty)
            return false;

        var knownBefore = configuration.RosterCatalog.KnownCharacters.Count;
        var visibilityBefore = configuration.RosterCatalog.Visibility.Count;
        var refreshBefore = configuration.RosterCatalog.RefreshHistory.Count;

        configuration.RosterCatalog.KnownCharacters = configuration.RosterCatalog.KnownCharacters
            .Where(record => !DadRosterIdentity.SameAccount(record.AccountKey, accountKey))
            .ToList();
        configuration.RosterCatalog.Visibility = configuration.RosterCatalog.Visibility
            .Where(record => !DadRosterIdentity.SameAccount(record.AccountKey, accountKey))
            .ToList();
        configuration.RosterCatalog.RefreshHistory = configuration.RosterCatalog.RefreshHistory
            .Where(record => !DadRosterIdentity.SameAccount(record.AccountKey, accountKey))
            .ToList();

        var knownRemoved = knownBefore - configuration.RosterCatalog.KnownCharacters.Count;
        var visibilityRemoved = visibilityBefore - configuration.RosterCatalog.Visibility.Count;
        var refreshRemoved = refreshBefore - configuration.RosterCatalog.RefreshHistory.Count;
        if (knownRemoved == 0 && visibilityRemoved == 0 && refreshRemoved == 0)
            return false;

        currentCatalog.Accounts = currentCatalog.Accounts
            .Where(account => !DadRosterIdentity.SameAccount(account.AccountKey, accountKey))
            .ToList();
        currentCatalog.Visibility = currentCatalog.Visibility
            .Where(record => !DadRosterIdentity.SameAccount(record.AccountKey, accountKey))
            .ToList();

        configuration.Save();
        log.Information(
            "[dad][Roster] Purged Dad metadata for account {AccountKey}: {KnownCount} known, {VisibilityCount} visibility, {RefreshCount} refresh record(s).",
            accountKey.Value,
            knownRemoved,
            visibilityRemoved,
            refreshRemoved);
        return true;
    }

    public DadAccountDataClearResult ClearAccountData()
    {
        configuration.RosterCatalog ??= new DadRosterCatalogConfiguration();
        configuration.RosterCatalog.KnownCharacters ??= [];
        configuration.RosterCatalog.Visibility ??= [];
        configuration.RosterCatalog.RefreshHistory ??= [];

        var result = new DadAccountDataClearResult
        {
            RosterKnownCharactersCleared = configuration.RosterCatalog.KnownCharacters.Count,
            RosterVisibilityCleared = configuration.RosterCatalog.Visibility.Count,
            RosterRefreshHistoryCleared = configuration.RosterCatalog.RefreshHistory.Count,
        };

        configuration.RosterCatalog.KnownCharacters.Clear();
        configuration.RosterCatalog.Visibility.Clear();
        configuration.RosterCatalog.RefreshHistory.Clear();
        currentCatalog = new DadAccountRosterCatalog
        {
            Summary = "Dad account data cleared; roster catalog not refreshed yet.",
        };
        lastPeerResponses = [];
        log.Information(
            "[dad][Roster] Cleared Dad roster account data: {KnownCount} known, {VisibilityCount} visibility, {RefreshCount} refresh record(s).",
            result.RosterKnownCharactersCleared,
            result.RosterVisibilityCleared,
            result.RosterRefreshHistoryCleared);
        return result;
    }

    public bool HasLocalRosterCopy(DadRosterCharacter character)
    {
        configuration.RosterCatalog ??= new DadRosterCatalogConfiguration();
        configuration.RosterCatalog.KnownCharacters ??= [];
        configuration.RosterCatalog.Visibility ??= [];
        configuration.RosterCatalog.RefreshHistory ??= [];

        if (character.AccountKey.IsEmpty ||
            DadRosterIdentity.SameAccount(character.AccountKey, GetLocalClientAccountKey()))
        {
            return false;
        }

        return configuration.RosterCatalog.KnownCharacters.Any(record =>
                   DadRosterIdentity.SameAccount(record.AccountKey, character.AccountKey) &&
                   DadRosterIdentity.SameCharacter(
                       new DadCharacterKey(record.CharacterKey),
                       record.ContentId,
                       character.CharacterKey,
                       character.ContentId)) ||
               configuration.RosterCatalog.Visibility.Any(record =>
                   RecordMatches(record, character.CharacterKey, character.AccountKey, character.ContentId)) ||
               configuration.RosterCatalog.RefreshHistory.Any(record =>
                   RecordMatches(record, character.CharacterKey, character.AccountKey, character.ContentId)) ||
               configManager.HasCharacterInAccount(character.AccountKey, character.CharacterKey);
    }

    public bool ForgetLocalRosterCopy(DadRosterCharacter character)
    {
        configuration.RosterCatalog ??= new DadRosterCatalogConfiguration();
        configuration.RosterCatalog.KnownCharacters ??= [];
        configuration.RosterCatalog.Visibility ??= [];
        configuration.RosterCatalog.RefreshHistory ??= [];

        if (character.AccountKey.IsEmpty)
            return false;

        var removedKnown = RemoveKnownCharacter(character);
        var removedVisibility = RemoveVisibilityRecords(character);
        var removedRefresh = RemoveRefreshRecords(character);
        var removedAccountConfig = configManager.RemoveCharacterFromAccount(character.AccountKey, character.CharacterKey);
        if (!removedKnown && !removedVisibility && !removedRefresh && !removedAccountConfig)
            return false;

        if (removedKnown || removedVisibility || removedRefresh)
            configuration.Save();

        currentCatalog.Characters = currentCatalog.Characters
            .Where(existing => !DadRosterIdentity.SameRow(existing, character))
            .ToList();
        currentCatalog.Visibility = currentCatalog.Visibility
            .Where(record => !RecordMatches(record, character.CharacterKey, character.AccountKey, character.ContentId))
            .ToList();

        log.Information(
            "[dad][Roster] Forgot local Dad roster copy for account {AccountKey}, character {CharacterKey}, cid {ContentId}: known {Known}, visibility {Visibility}, refresh {Refresh}, config {Config}.",
            character.AccountKey.Value,
            character.CharacterKey.Value,
            character.ContentId,
            removedKnown,
            removedVisibility,
            removedRefresh,
            removedAccountConfig);
        return true;
    }

    public bool MergeAccount(DadAccountKey sourceAccountKey, DadAccountKey targetAccountKey, string targetAccountAlias)
    {
        configuration.RosterCatalog ??= new DadRosterCatalogConfiguration();
        configuration.RosterCatalog.KnownCharacters ??= [];
        configuration.RosterCatalog.Visibility ??= [];
        configuration.RosterCatalog.RefreshHistory ??= [];

        if (sourceAccountKey.IsEmpty || targetAccountKey.IsEmpty ||
            DadRosterIdentity.SameAccount(sourceAccountKey, targetAccountKey))
        {
            return false;
        }

        var changed = MoveKnownCharactersToAccount(sourceAccountKey, targetAccountKey, targetAccountAlias);
        changed |= MoveVisibilityToAccount(sourceAccountKey, targetAccountKey);
        changed |= MoveRefreshHistoryToAccount(sourceAccountKey, targetAccountKey);
        if (!changed)
            return false;

        configuration.Save();
        log.Information(
            "[dad][Roster] Merged Dad metadata from account {SourceAccountKey} into {TargetAccountKey}.",
            sourceAccountKey.Value,
            targetAccountKey.Value);
        return true;
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
            if (record != null && (record.NeedsRosterUpdate || record.Visibility == DadRosterVisibility.NeedsUpdate))
            {
                record.Visibility = NormalizeVisibility(record.Visibility);
                record.NeedsRosterUpdate = false;
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
        var localCatalog = catalogs.FirstOrDefault();
        var peerCatalogs = catalogs.Skip(1).ToList();
        var merged = new DadAccountRosterCatalog
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Version = localCatalog?.Version ?? 1,
            XadbContractVersion = localCatalog?.XadbContractVersion,
            XadbPayloadRowCount = localCatalog?.XadbPayloadRowCount ?? 0,
            IsFullRosterAvailable = localCatalog?.IsFullRosterAvailable ?? false,
            Visibility = configuration.RosterCatalog.Visibility.Select(static record => record.Clone()).ToList(),
            SourceDiagnostics = localCatalog?.SourceDiagnostics.Clone() ?? new DadRosterSourceDiagnostics(),
        };
        merged.SourceDiagnostics.PeerCatalogCount = peerCatalogs.Count;
        merged.SourceDiagnostics.PeerFullRosterCount = peerCatalogs.Count(static catalog => catalog.IsFullRosterAvailable);
        merged.SourceDiagnostics.PeerFullRosterRows = peerCatalogs
            .Where(static catalog => catalog.IsFullRosterAvailable)
            .Sum(static catalog => catalog.Characters.Count);

        foreach (var catalog in catalogs)
        {
            foreach (var warning in catalog.SourceDiagnostics.Warnings)
                AddWarning(merged.SourceDiagnostics.Warnings, warning);

            foreach (var warning in catalog.Warnings)
            {
                if (!string.IsNullOrWhiteSpace(warning) &&
                    merged.Warnings.All(existing => !string.Equals(existing, warning, StringComparison.OrdinalIgnoreCase)))
                {
                    merged.Warnings.Add(warning);
                }

                AddWarning(merged.SourceDiagnostics.Warnings, warning);
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

        PruneCatalogRowsSupersededByRuntime(merged.Characters);
        merged.Accounts = BuildMergedAccountDirectory(catalogs, merged.Characters).ToList();
        ApplyVisibility(merged, plan);
        merged.Characters = merged.Characters
            .Where(character => ShouldIncludeInCatalog(character, plan))
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

            var record = FindVisibilityRecord(character.CharacterKey, character.AccountKey, character.ContentId);
            var visibility = NormalizeVisibility(record?.Visibility ?? DadRosterVisibility.Active);
            var needsRosterUpdate = record is { NeedsRosterUpdate: true } || record?.Visibility == DadRosterVisibility.NeedsUpdate;
            character.Visibility = visibility;
            character.NeedsRosterUpdate = needsRosterUpdate;
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
            if (needsRosterUpdate)
                AddBlocker(character, "Needs roster refresh before normal planning.");
        }

        catalog.Visibility = configuration.RosterCatalog.Visibility.Select(static record => record.Clone()).ToList();
    }

    private static void UpsertRosterCharacter(
        List<DadRosterCharacter> characters,
        DadRosterCharacter candidate,
        bool xadbAuthoritative = false)
    {
        var existing = characters.FindIndex(existingCharacter => DadRosterIdentity.SameRow(existingCharacter, candidate));
        var incoming = candidate.Clone();
        if (xadbAuthoritative)
            DadRosterCharacterMerge.NormalizeXadbSnapshot(incoming);

        if (existing < 0)
        {
            characters.Add(incoming);
            return;
        }

        var merged = characters[existing];
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
        merged.LastRuntimeSeenUtc = MaxDate(merged.LastRuntimeSeenUtc, incoming.LastRuntimeSeenUtc);
        if (xadbAuthoritative)
            DadRosterCharacterMerge.ApplyAuthoritativeXadbSnapshot(merged, incoming);
        else
            DadRosterCharacterMerge.MergeNonAuthoritativeSnapshot(merged, incoming);
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
                .Select(ToRosterCharacter)
                .ToList(),
        };
    }

    private bool SeedKnownCharactersFromAccountConfigs()
    {
        var changed = false;
        var localAccountKey = GetLocalClientAccountKey();
        foreach (var account in configManager.GetAllAccounts())
        {
            var accountKey = new DadAccountKey(account.AccountId);
            if (accountKey.IsEmpty || !DadRosterIdentity.SameAccount(accountKey, localAccountKey))
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

    private bool RemoveVisibilityRecords(DadRosterCharacter character)
    {
        var before = configuration.RosterCatalog.Visibility.Count;
        configuration.RosterCatalog.Visibility = configuration.RosterCatalog.Visibility
            .Where(record => !RecordMatches(record, character.CharacterKey, character.AccountKey, character.ContentId))
            .ToList();
        return before != configuration.RosterCatalog.Visibility.Count;
    }

    private bool RemoveRefreshRecords(DadRosterCharacter character)
    {
        var before = configuration.RosterCatalog.RefreshHistory.Count;
        configuration.RosterCatalog.RefreshHistory = configuration.RosterCatalog.RefreshHistory
            .Where(record => !RecordMatches(record, character.CharacterKey, character.AccountKey, character.ContentId))
            .ToList();
        return before != configuration.RosterCatalog.RefreshHistory.Count;
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

    private Dictionary<string, string> BuildKnownDadAccounts(IReadOnlyList<DadParticipantSnapshot> peerParticipants)
    {
        var accounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AddKnownDadAccount(accounts, GetLocalClientAccountKey(), GetLocalClientAccountAlias());
        foreach (var participant in peerParticipants)
            AddKnownDadAccount(accounts, participant.ManagedAccountKey, participant.ManagedAccountAlias);

        return accounts;
    }

    private Dictionary<string, string> BuildLocalDadAccounts()
    {
        var accounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddKnownDadAccount(accounts, GetLocalClientAccountKey(), GetLocalClientAccountAlias());
        return accounts;
    }

    private static void AddKnownDadAccount(Dictionary<string, string> accounts, DadAccountKey accountKey, string accountAlias)
    {
        if (accountKey.IsEmpty)
            return;

        var key = accountKey.Value.Trim();
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!accounts.TryGetValue(key, out var existingAlias) || string.IsNullOrWhiteSpace(existingAlias))
            accounts[key] = accountAlias?.Trim() ?? string.Empty;
    }

    private static bool IsKnownDadAccount(
        DadAccountKey accountKey,
        IReadOnlyDictionary<string, string> knownDadAccounts)
        => !accountKey.IsEmpty && knownDadAccounts.ContainsKey(accountKey.Value.Trim());

    private bool PruneKnownOwnershipFromRuntime(
        IReadOnlyList<DadAcquiredCharacter> characters,
        IReadOnlyDictionary<string, string> knownDadAccounts)
    {
        var runtimeCharacters = characters
            .Where(static character => character.Source == DadCharacterSource.LocalRuntime)
            .Where(character => IsKnownDadAccount(new DadAccountKey(character.AccountId), knownDadAccounts))
            .Where(static character => !string.IsNullOrWhiteSpace(character.CharacterKey) || character.ContentId != 0)
            .Select(FromAcquiredCharacter)
            .ToList();
        if (runtimeCharacters.Count == 0 || configuration.RosterCatalog.KnownCharacters.Count == 0)
            return false;

        var before = configuration.RosterCatalog.KnownCharacters.Count;
        configuration.RosterCatalog.KnownCharacters = configuration.RosterCatalog.KnownCharacters
            .Where(record => !runtimeCharacters.Any(runtime =>
                !DadRosterIdentity.SameAccount(record.AccountKey, runtime.AccountKey) &&
                DadRosterIdentity.SameCharacter(
                    new DadCharacterKey(record.CharacterKey),
                    record.ContentId,
                    runtime.CharacterKey,
                    runtime.ContentId)))
            .ToList();
        return before != configuration.RosterCatalog.KnownCharacters.Count;
    }

    private static void PruneCatalogRowsSupersededByRuntime(List<DadRosterCharacter> characters)
    {
        var runtimeCharacters = characters
            .Where(static character => IsRuntimeSource(character.Source))
            .Where(static character => !character.AccountKey.IsEmpty)
            .Where(static character => !character.CharacterKey.IsEmpty || character.ContentId != 0)
            .ToList();
        if (runtimeCharacters.Count == 0)
            return;

        characters.RemoveAll(character =>
            !IsRuntimeSource(character.Source) &&
            !character.AccountKey.IsEmpty &&
            runtimeCharacters.Any(runtime =>
                SameSourceClient(runtime, character) &&
                !DadRosterIdentity.SameAccount(runtime.AccountKey, character.AccountKey) &&
                DadRosterIdentity.SameCharacter(
                    runtime.CharacterKey,
                    runtime.ContentId,
                    character.CharacterKey,
                    character.ContentId)));
    }

    private static bool SameSourceClient(DadRosterCharacter left, DadRosterCharacter right)
        => string.Equals(
            left.SourceClientInstanceId?.Trim() ?? string.Empty,
            right.SourceClientInstanceId?.Trim() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<DadRosterAccountOption> BuildLocalAccountDirectory(
        IReadOnlyList<DadRosterCharacter> characters,
        IReadOnlyList<DadParticipantSnapshot> peerParticipants)
    {
        var options = new List<DadRosterAccountOption>();
        AddConfiguredAccountOptions(options);
        AddCharacterAccountOptions(options, characters);
        AddLocalClientAccountOption(options);
        AddParticipantAccountOption(options, presenceService.CurrentParticipant, isLocal: true);
        foreach (var participant in peerParticipants)
            AddParticipantAccountOption(options, participant, isLocal: false);

        RefreshAccountCharacterCounts(options, characters);
        return SortAccountOptions(options);
    }

    private void AddConfiguredAccountOptions(List<DadRosterAccountOption> options)
    {
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
                OwnerOnline = true,
                AssignedCharacterCount = account.Characters.Count,
            });
        }
    }

    private static void AddCharacterAccountOptions(
        List<DadRosterAccountOption> options,
        IReadOnlyList<DadRosterCharacter> characters)
    {
        foreach (var group in characters
                     .Where(static character => !character.AccountKey.IsEmpty)
                     .GroupBy(static character => character.AccountKey.Value.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            UpsertAccountOption(options, new DadRosterAccountOption
            {
                AccountKey = first.AccountKey,
                AccountAlias = first.AccountAlias,
                DisplayName = BuildAccountDisplayName(first.AccountKey.Value, first.AccountAlias),
                SourceClientInstanceId = first.SourceClientInstanceId,
                SourceWorkerSessionId = first.SourceWorkerSessionId,
                IsLocal = false,
                AssignedCharacterCount = group
                    .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
                    .Count(),
            });
        }
    }

    private void AddParticipantAccountOption(
        List<DadRosterAccountOption> options,
        DadParticipantSnapshot participant,
        bool isLocal)
    {
        if (participant.ManagedAccountKey.IsEmpty)
            return;

        var assignedCharacterCount = participant.AvailableCharacterKeys
            .Where(static key => !key.IsEmpty)
            .DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase)
            .Count();
        if (assignedCharacterCount == 0 && !participant.ActiveCharacterKey.IsEmpty)
            assignedCharacterCount = 1;

        UpsertAccountOption(options, new DadRosterAccountOption
        {
            AccountKey = participant.ManagedAccountKey,
            AccountAlias = participant.ManagedAccountAlias,
            DisplayName = BuildAccountDisplayName(participant.ManagedAccountKey.Value, participant.ManagedAccountAlias),
            SourceClientInstanceId = participant.ClientInstanceId,
            SourceWorkerSessionId = participant.WorkerSessionId,
            IsLocal = isLocal,
            OwnerOnline = isLocal || transportService.IsWorkerOnline(participant.WorkerSessionId),
            AssignedCharacterCount = assignedCharacterCount,
        });
    }

    private static DadCharacterPool WithCurrentTransport(
        DadCharacterPool pool,
        DadPeerTransportSnapshot currentTransport)
        => new()
        {
            LastUpdatedUtc = pool.LastUpdatedUtc,
            XadbStatus = pool.XadbStatus,
            PeerTransport = currentTransport,
            LastSummary = pool.LastSummary,
            Characters = pool.Characters.Select(static character => character.Clone()).ToList(),
        };

    private DadAccountRosterCatalog BuildPeerRuntimeFallbackCatalog(
        DadPeerTransportSnapshot currentTransport,
        IReadOnlyList<DadPeerRosterCatalogResponse> catalogResponses)
    {
        var catalog = new DadAccountRosterCatalog
        {
            GeneratedAtUtc = DateTime.UtcNow,
            SourceClientInstanceId = string.Empty,
            SourceWorkerSessionId = new DadWorkerSessionId(string.Empty),
            IsFullRosterAvailable = false,
            SourceDiagnostics = new DadRosterSourceDiagnostics
            {
                LocalAccountKey = GetLocalClientAccountKey().Value,
            },
        };

        var fallbackRows = DadRosterTransportCatalogRuntime.BuildParticipantRuntimeFallbackRows(
            currentTransport,
            catalogResponses);
        foreach (var rosterCharacter in fallbackRows)
            UpsertRosterCharacter(catalog.Characters, rosterCharacter);

        if (catalog.Characters.Count == 0)
            return catalog;

        AddCharacterAccountOptions(catalog.Accounts, catalog.Characters);
        RefreshAccountCharacterCounts(catalog.Accounts, catalog.Characters);
        catalog.Accounts = SortAccountOptions(catalog.Accounts).ToList();
        var warning = "Peer roster catalog unavailable or incomplete; using current hub participant fallback.";
        AddWarning(catalog.Warnings, warning);
        AddWarning(catalog.SourceDiagnostics.Warnings, warning);
        catalog.Summary = BuildCatalogSummary(catalog);
        return catalog;
    }

    private DadAccountRosterCatalog BuildRuntimeOverlayCatalog(DadCharacterPool pool)
    {
        var catalog = new DadAccountRosterCatalog
        {
            GeneratedAtUtc = DateTime.UtcNow,
            SourceClientInstanceId = presenceService.ClientInstanceId,
            SourceWorkerSessionId = presenceService.WorkerSessionId,
            IsFullRosterAvailable = false,
            SourceDiagnostics = new DadRosterSourceDiagnostics
            {
                LocalAccountKey = GetLocalClientAccountKey().Value,
            },
        };

        var localAccountKey = GetLocalClientAccountKey();
        var localAccountAlias = GetLocalClientAccountAlias();
        foreach (var acquired in pool.Characters)
        {
            var rosterCharacter = FromAcquiredCharacter(acquired);
            if (rosterCharacter.CharacterKey.IsEmpty && rosterCharacter.ContentId == 0)
                continue;

            if (acquired.Source == DadCharacterSource.LocalRuntime)
            {
                catalog.SourceDiagnostics.LocalRuntimeRows++;
                rosterCharacter.SourceClientInstanceId = presenceService.ClientInstanceId;
                rosterCharacter.SourceWorkerSessionId = presenceService.WorkerSessionId;
                if (rosterCharacter.AccountKey.IsEmpty)
                    StampCharacterAccount(rosterCharacter, localAccountKey, localAccountAlias);
            }
            else if (acquired.Source == DadCharacterSource.PeerRuntime)
            {
                StampPeerRuntimeSource(rosterCharacter, acquired, pool.PeerTransport.LastResponses);
            }

            UpsertRosterCharacter(catalog.Characters, rosterCharacter);
        }

        catalog.SourceDiagnostics.FinalLocalRows = catalog.Characters.Count;
        catalog.Accounts = BuildLocalAccountDirectory(catalog.Characters, pool.PeerTransport.KnownParticipants).ToList();
        catalog.Summary = BuildCatalogSummary(catalog);
        return catalog;
    }

    private static void StampPeerRuntimeSource(
        DadRosterCharacter rosterCharacter,
        DadAcquiredCharacter acquired,
        IReadOnlyList<DadPeerSnapshotResponse> peerResponses)
    {
        var response = peerResponses.FirstOrDefault(candidate =>
            DadRosterIdentity.SameCharacter(
                new DadCharacterKey(acquired.CharacterKey),
                acquired.ContentId,
                new DadCharacterKey(candidate.Character.CharacterKey),
                candidate.Character.ContentId) ||
            DadRosterIdentity.SameCharacter(
                new DadCharacterKey(acquired.CharacterKey),
                acquired.ContentId,
                candidate.Participant.ActiveCharacterKey,
                candidate.Participant.Character.ContentId));
        if (response == null)
            return;

        var participant = response.Participant;
        rosterCharacter.SourceClientInstanceId = string.IsNullOrWhiteSpace(response.ClientInstanceId)
            ? participant.ClientInstanceId
            : response.ClientInstanceId;
        rosterCharacter.SourceWorkerSessionId = participant.WorkerSessionId;
        if (rosterCharacter.AccountKey.IsEmpty && !participant.ManagedAccountKey.IsEmpty)
            StampCharacterAccount(rosterCharacter, participant.ManagedAccountKey, participant.ManagedAccountAlias);
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
        }

        AddCharacterAccountOptions(options, characters);
        RefreshAccountCharacterCounts(options, characters);
        return SortAccountOptions(options);
    }

    private static void RefreshAccountCharacterCounts(
        List<DadRosterAccountOption> options,
        IReadOnlyList<DadRosterCharacter> characters)
    {
        foreach (var option in options)
        {
            var count = characters
                .Where(character => !character.AccountKey.IsEmpty &&
                                    DadRosterIdentity.SameAccount(character.AccountKey, option.AccountKey))
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

        if (string.IsNullOrWhiteSpace(existing.SourceClientInstanceId) || candidate.IsLocal && !existing.IsLocal)
            existing.SourceClientInstanceId = candidate.SourceClientInstanceId;
        if (existing.SourceWorkerSessionId.IsEmpty || candidate.IsLocal && !existing.IsLocal)
            existing.SourceWorkerSessionId = candidate.SourceWorkerSessionId;
        existing.IsLocal |= candidate.IsLocal;
        existing.OwnerOnline |= candidate.OwnerOnline || candidate.IsLocal;
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

    private DadAccountRosterCatalog ApplyOwnerConnectivity(DadAccountRosterCatalog catalog)
    {
        foreach (var account in catalog.Accounts)
        {
            account.OwnerOnline = account.IsLocal ||
                                  DadRosterTransportCatalogRuntime.IsRosterOwnerReachable(
                                      account.SourceWorkerSessionId,
                                      account.SourceClientInstanceId,
                                      transportService.CurrentTransport,
                                      lastPeerResponses);
        }

        foreach (var character in catalog.Characters)
        {
            var hasOwnerIdentity = !character.SourceWorkerSessionId.IsEmpty ||
                                   !string.IsNullOrWhiteSpace(character.SourceClientInstanceId);
            if (!hasOwnerIdentity ||
                DadRosterTransportCatalogRuntime.IsRosterOwnerReachable(
                    character.SourceWorkerSessionId,
                    character.SourceClientInstanceId,
                    transportService.CurrentTransport,
                    lastPeerResponses))
            {
                continue;
            }

            character.IsCurrent = false;
            character.IsStale = true;
            const string warning = "Owning Client Dad is offline.";
            if (character.Warnings.All(existing =>
                    !string.Equals(existing, warning, StringComparison.OrdinalIgnoreCase)))
            {
                character.Warnings.Add(warning);
            }
        }

        return catalog;
    }

    private static string BuildAccountDisplayName(string accountKey, string accountAlias)
        => string.IsNullOrWhiteSpace(accountAlias)
            ? accountKey
            : accountAlias.Trim();

    private DadAccountKey GetLocalClientAccountKey()
        => new(configuration.ClientAccountId?.Trim() ?? string.Empty);

    private string GetLocalClientAccountAlias()
    {
        var localAccountKey = GetLocalClientAccountKey();
        var account = configManager.GetAccount(localAccountKey);
        if (!string.IsNullOrWhiteSpace(account?.AccountAlias))
            return account.AccountAlias.Trim();

        if (!presenceService.CurrentParticipant.ManagedAccountKey.IsEmpty &&
            DadRosterIdentity.SameAccount(presenceService.CurrentParticipant.ManagedAccountKey, localAccountKey) &&
            !string.IsNullOrWhiteSpace(presenceService.CurrentParticipant.ManagedAccountAlias))
        {
            return presenceService.CurrentParticipant.ManagedAccountAlias.Trim();
        }

        return "Dad client";
    }

    private void AddLocalClientAccountOption(List<DadRosterAccountOption> options)
    {
        var accountKey = GetLocalClientAccountKey();
        if (accountKey.IsEmpty)
            return;

        var account = configManager.GetAccount(accountKey);
        var accountAlias = !string.IsNullOrWhiteSpace(account?.AccountAlias)
            ? account.AccountAlias.Trim()
            : GetLocalClientAccountAlias();

        UpsertAccountOption(options, new DadRosterAccountOption
        {
            AccountKey = accountKey,
            AccountAlias = accountAlias,
            DisplayName = BuildAccountDisplayName(accountKey.Value, accountAlias),
            SourceClientInstanceId = presenceService.ClientInstanceId,
            SourceWorkerSessionId = presenceService.WorkerSessionId,
            IsLocal = true,
            OwnerOnline = true,
            AssignedCharacterCount = account?.Characters.Count ?? 0,
        });
    }

    private static (string CharacterName, string WorldName) ParseCharacterKey(string characterKey)
    {
        var parts = (characterKey ?? string.Empty).Split('@', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (parts[0], parts[1])
            : (characterKey ?? string.Empty, string.Empty);
    }

    private List<DadRosterCharacter> AttributeLocalXadbCharacters(
        IReadOnlyList<DadRosterCharacter> xadbCharacters,
        DadAccountKey localAccountKey,
        string localAccountAlias,
        List<string> warnings)
    {
        var attributed = new List<DadRosterCharacter>();
        if (localAccountKey.IsEmpty)
        {
            AddWarning(warnings, "Dad client account id missing; local XADB roster rows cannot be attributed.");
            return xadbCharacters.Select(static character => character.Clone()).ToList();
        }

        foreach (var character in xadbCharacters)
        {
            var candidate = character.Clone();
            StampCharacterAccount(candidate, localAccountKey, localAccountAlias);
            attributed.Add(candidate);
        }

        return attributed;
    }

    private static void StampCharacterAccount(
        DadRosterCharacter character,
        DadAccountKey accountKey,
        string accountAlias)
    {
        character.AccountKey = accountKey;
        if (!string.IsNullOrWhiteSpace(accountAlias))
            character.AccountAlias = accountAlias.Trim();
        character.Blockers.RemoveAll(static blocker =>
            blocker.Contains("Account attribution missing", StringComparison.OrdinalIgnoreCase));
        character.Warnings.RemoveAll(static warning =>
            warning.Contains("Dad account attribution", StringComparison.OrdinalIgnoreCase) ||
            warning.Contains("XADB account attribution", StringComparison.OrdinalIgnoreCase));
    }

    private bool UpsertKnownCharacter(
        DadRosterCharacter character,
        bool xadbAuthoritative = false)
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
        UpsertRosterCharacter(mergeList, character, xadbAuthoritative);
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
            NeedsRosterUpdate = character.NeedsRosterUpdate,
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

        if (character.NeedsRosterUpdate)
            return DadReadinessState.Unavailable;

        if (character.Source is DadCharacterSource.LocalRuntime or DadCharacterSource.PeerRuntime)
            return character.Blockers.Count == 0 ? DadReadinessState.Ready : DadReadinessState.Blocked;

        return DadReadinessState.Unavailable;
    }

    private static DadAccountKey ResolveAccountKey(DadAcquiredCharacter character)
        => DadRosterIdentity.ResolveAccountKey(character.AccountId, character.AccountAlias);

    private static bool IsRuntimeSource(DadCharacterSource source)
        => source is DadCharacterSource.LocalRuntime or DadCharacterSource.PeerRuntime;

    private static bool ShouldIncludeForPlanner(
        DadRosterCharacter character,
        bool includeHidden,
        bool includeIgnored,
        bool includeNeedsUpdate)
    {
        var visibility = NormalizeVisibility(character.Visibility);
        var visibilityIncluded = visibility == DadRosterVisibility.Active
                                 || includeHidden && visibility == DadRosterVisibility.Hidden
                                 || includeIgnored && visibility == DadRosterVisibility.Ignored;
        return visibilityIncluded && (!character.NeedsRosterUpdate || includeNeedsUpdate);
    }

    private static bool ShouldIncludeInCatalog(DadRosterCharacter character, DadRosterRefreshPlan plan)
    {
        var visibility = NormalizeVisibility(character.Visibility);
        return visibility == DadRosterVisibility.Active
               || plan.IncludeHidden && visibility == DadRosterVisibility.Hidden
               || plan.IncludeIgnored && visibility == DadRosterVisibility.Ignored;
    }

    private bool NormalizeRosterVisibilityRecords(bool saveIfChanged)
    {
        var changed = false;
        foreach (var record in configuration.RosterCatalog.Visibility)
        {
            if (record.Visibility != DadRosterVisibility.NeedsUpdate)
                continue;

            record.Visibility = DadRosterVisibility.Active;
            record.NeedsRosterUpdate = true;
            changed = true;
        }

        if (changed && saveIfChanged)
            configuration.Save();

        return changed;
    }

    private static DadRosterVisibility NormalizeVisibility(DadRosterVisibility visibility)
        => visibility == DadRosterVisibility.NeedsUpdate
            ? DadRosterVisibility.Active
            : visibility;

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

    private bool MoveKnownCharactersToAccount(
        DadAccountKey sourceAccountKey,
        DadAccountKey targetAccountKey,
        string targetAccountAlias)
    {
        var changed = false;
        var sourceRecords = configuration.RosterCatalog.KnownCharacters
            .Where(record => DadRosterIdentity.SameAccount(record.AccountKey, sourceAccountKey))
            .Select(static record => record.Clone())
            .ToList();
        if (sourceRecords.Count == 0)
            return false;

        configuration.RosterCatalog.KnownCharacters = configuration.RosterCatalog.KnownCharacters
            .Where(record => !DadRosterIdentity.SameAccount(record.AccountKey, sourceAccountKey))
            .ToList();
        changed = true;

        foreach (var record in sourceRecords)
        {
            record.AccountKey = targetAccountKey;
            record.AccountAlias = string.IsNullOrWhiteSpace(targetAccountAlias)
                ? record.AccountAlias
                : targetAccountAlias.Trim();
            record.UpdatedAtUtc = DateTime.UtcNow;

            var existingIndex = configuration.RosterCatalog.KnownCharacters.FindIndex(existing =>
                DadRosterIdentity.SameAccount(existing.AccountKey, targetAccountKey) &&
                DadRosterIdentity.SameCharacter(
                    new DadCharacterKey(existing.CharacterKey),
                    existing.ContentId,
                    new DadCharacterKey(record.CharacterKey),
                    record.ContentId));
            if (existingIndex >= 0)
                continue;

            configuration.RosterCatalog.KnownCharacters.Add(record);
        }

        return changed;
    }

    private bool MoveVisibilityToAccount(DadAccountKey sourceAccountKey, DadAccountKey targetAccountKey)
    {
        var sourceRecords = configuration.RosterCatalog.Visibility
            .Where(record => DadRosterIdentity.SameAccount(record.AccountKey, sourceAccountKey))
            .Select(static record => record.Clone())
            .ToList();
        if (sourceRecords.Count == 0)
            return false;

        configuration.RosterCatalog.Visibility = configuration.RosterCatalog.Visibility
            .Where(record => !DadRosterIdentity.SameAccount(record.AccountKey, sourceAccountKey))
            .ToList();

        foreach (var record in sourceRecords)
        {
            record.AccountKey = targetAccountKey;
            record.UpdatedAtUtc = DateTime.UtcNow;
            var exists = configuration.RosterCatalog.Visibility.Any(existing =>
                DadRosterIdentity.SameAccount(existing.AccountKey, targetAccountKey) &&
                SameVisibilityTarget(existing, record));
            if (!exists)
                configuration.RosterCatalog.Visibility.Add(record);
        }

        return true;
    }

    private bool MoveRefreshHistoryToAccount(DadAccountKey sourceAccountKey, DadAccountKey targetAccountKey)
    {
        var sourceRecords = configuration.RosterCatalog.RefreshHistory
            .Where(record => DadRosterIdentity.SameAccount(record.AccountKey, sourceAccountKey))
            .Select(static record => record.Clone())
            .ToList();
        if (sourceRecords.Count == 0)
            return false;

        configuration.RosterCatalog.RefreshHistory = configuration.RosterCatalog.RefreshHistory
            .Where(record => !DadRosterIdentity.SameAccount(record.AccountKey, sourceAccountKey))
            .ToList();

        foreach (var record in sourceRecords)
        {
            record.AccountKey = targetAccountKey;
            var exists = configuration.RosterCatalog.RefreshHistory.Any(existing =>
                DadRosterIdentity.SameAccount(existing.AccountKey, targetAccountKey) &&
                SameRefreshTarget(existing, record));
            if (!exists)
                configuration.RosterCatalog.RefreshHistory.Add(record);
        }

        TrimRefreshHistory();
        return true;
    }

    private static bool SameVisibilityTarget(DadRosterVisibilityRecord left, DadRosterVisibilityRecord right)
    {
        if (left.ContentId != 0 && right.ContentId != 0)
            return left.ContentId == right.ContentId;

        return !string.IsNullOrWhiteSpace(left.CharacterKey) &&
               !string.IsNullOrWhiteSpace(right.CharacterKey) &&
               string.Equals(left.CharacterKey, right.CharacterKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameRefreshTarget(DadRosterRefreshRecord left, DadRosterRefreshRecord right)
    {
        if (left.ContentId != 0 && right.ContentId != 0)
            return left.ContentId == right.ContentId;

        return !string.IsNullOrWhiteSpace(left.CharacterKey) &&
               !string.IsNullOrWhiteSpace(right.CharacterKey) &&
               string.Equals(left.CharacterKey, right.CharacterKey, StringComparison.OrdinalIgnoreCase);
    }

    private void LogRosterDiagnostics(DadAccountRosterCatalog catalog, DadRosterRefreshPlan plan)
    {
        var diagnostics = catalog.SourceDiagnostics;
        var reason = string.IsNullOrWhiteSpace(plan.DiagnosticsReason)
            ? "manual"
            : plan.DiagnosticsReason.Trim();
        log.Information(
            "[dad][Roster] {Reason}: account {LocalAccountKey}, XADB snapshots {SnapshotRows}, legacy {LegacyRows}, merged {MergedRows}, payload {XadbPayloadRows}, DCs {DataCenterCounts}, attributed local {AttributedRows}, known {KnownRows}, local runtime {RuntimeRows}, final local {FinalLocalRows}, peer catalogs {PeerCatalogs}, peer full-roster {PeerFullRosterCount}/{PeerFullRosterRows} row(s), IPC roster v{RosterVersion}, contract v{ContractVersion}, full roster {FullRoster}.",
            reason,
            diagnostics.LocalAccountKey,
            diagnostics.XadbSnapshotRows,
            diagnostics.XadbLegacyRows,
            diagnostics.XadbMergedRows,
            diagnostics.XadbPayloadRows,
            FormatCountBreakdown(diagnostics.XadbDataCenterCounts),
            diagnostics.LocalXadbAttributedRows,
            diagnostics.KnownRosterRows,
            diagnostics.LocalRuntimeRows,
            diagnostics.FinalLocalRows,
            diagnostics.PeerCatalogCount,
            diagnostics.PeerFullRosterCount,
            diagnostics.PeerFullRosterRows,
            catalog.Version,
            catalog.XadbContractVersion?.ToString() ?? "?",
            catalog.IsFullRosterAvailable);

        if (!catalog.IsFullRosterAvailable)
            log.Warning("[dad][Roster] {Warning}", DadXadbClient.RosterIpcMissingWarning);
    }

    private static string FormatCountBreakdown(IReadOnlyDictionary<string, int> counts, int limit = 8)
    {
        if (counts.Count == 0)
            return "-";

        var ordered = counts
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var visible = ordered.Take(Math.Max(1, limit))
            .Select(static pair => $"{pair.Key}:{pair.Value}");
        var suffix = ordered.Count > limit ? $", +{ordered.Count - limit}" : string.Empty;
        return string.Join(", ", visible) + suffix;
    }

    private static string BuildCatalogSummary(DadAccountRosterCatalog catalog)
    {
        var active = catalog.Characters.Count(static character => character.Visibility == DadRosterVisibility.Active);
        var hidden = catalog.Characters.Count(static character => character.Visibility == DadRosterVisibility.Hidden);
        var ignored = catalog.Characters.Count(static character => character.Visibility == DadRosterVisibility.Ignored);
        var needsUpdate = catalog.Characters.Count(static character => character.NeedsRosterUpdate);
        var stale = catalog.Characters.Count(static character => character.IsStale);
        return $"{active} active, {hidden} hidden, {ignored} ignored, {needsUpdate} need update, {stale} stale.";
    }
}

public sealed class DadPlannerRosterSnapshot
{
    public DadCharacterPool CuratedPool { get; init; } = new();
    public IReadOnlyList<DadRosterAccountOption> AccountOptions { get; init; } = [];
}
