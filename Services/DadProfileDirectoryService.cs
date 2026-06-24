using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadProfileDirectoryService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(15);

    private readonly Configuration configuration;
    private readonly ConfigManager configManager;
    private readonly DadPresenceService presenceService;
    private readonly DadTransportService transportService;
    private readonly IPluginLog log;
    private readonly object refreshGate = new();
    private readonly CancellationTokenSource refreshCancellation = new();
    private Task? refreshTask;
    private DateTime nextRefreshUtc = DateTime.MinValue;
    private string cacheSignature = string.Empty;
    private IReadOnlyList<DadProfileCatalog> currentCatalogs = [];

    public DadProfileDirectoryService(
        Configuration configuration,
        ConfigManager configManager,
        DadPresenceService presenceService,
        DadTransportService transportService,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.configManager = configManager;
        this.presenceService = presenceService;
        this.transportService = transportService;
        this.log = log;
        configuration.ProfileCatalogCache ??= [];
        MarkCachedOwnersOffline();
        RebuildCurrentCatalogs();
    }

    public void Update()
    {
        if (DateTime.UtcNow < nextRefreshUtc)
            return;

        nextRefreshUtc = DateTime.UtcNow + RefreshInterval;
        RebuildCurrentCatalogs();
        ScheduleRemoteCatalogRefresh();
    }

    public DadProfileCatalog BuildLocalCatalog()
        => configManager.BuildLocalProfileCatalog(
            presenceService.ClientInstanceId,
            presenceService.WorkerSessionId,
            transportService.CurrentTransport.ListenerEndpoint,
            configuration.PluginEnabled && !configuration.LocalOnlyModeEnabled);

    public IReadOnlyList<DadProfileCatalog> GetCatalogs()
        => currentCatalogs;

    public DadProfileUpdateAck UpdateProfile(DadProfileUpdateRequest request)
    {
        var local = BuildLocalCatalog();
        if (local.Accounts.Any(account => DadRosterIdentity.SameAccount(account.AccountKey, request.AccountKey)))
        {
            var localAck = configManager.ApplyProfileUpdate(request);
            RebuildCurrentCatalogs();
            return localAck;
        }

        var owner = (configuration.ProfileCatalogCache ?? []).FirstOrDefault(catalog =>
            catalog.Accounts.Any(account => DadRosterIdentity.SameAccount(account.AccountKey, request.AccountKey)));
        if (owner == null)
        {
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                Summary = $"No Client Dad owns account {request.AccountKey}.",
            };
        }

        if (!owner.OwnerOnline || owner.ReadOnly || string.IsNullOrWhiteSpace(owner.OwnerEndpoint))
        {
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                Summary = $"Owning Client Dad for {request.AccountKey} is offline; cached profile is read-only.",
            };
        }

        var ack = transportService.SendProfileUpdate(owner.OwnerEndpoint, request);
        if (ack == null)
        {
            owner.OwnerOnline = false;
            owner.ReadOnly = true;
            SaveCacheIfChanged();
            RebuildCurrentCatalogs();
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                Summary = "Owning Client Dad did not acknowledge profile update.",
            };
        }

        if (ack.Account != null)
        {
            owner.Accounts.RemoveAll(account => DadRosterIdentity.SameAccount(account.AccountKey, ack.Account.AccountKey));
            owner.Accounts.Add(ack.Account.Clone());
            owner.GeneratedAtUtc = DateTime.UtcNow;
            SaveCacheIfChanged();
            RebuildCurrentCatalogs();
        }

        return ack;
    }

    public string GetAccountDirectoryJson()
        => DadIpcJson.Serialize(GetCatalogs()
            .SelectMany(catalog => catalog.Accounts.Select(account => new
            {
                account.AccountKey,
                account.AccountAlias,
                account.Revision,
                account.PrimaryLaunchProfileId,
                catalog.OwnerClientInstanceId,
                catalog.OwnerWorkerSessionId,
                catalog.OwnerEndpoint,
                catalog.OwnerOnline,
                catalog.ReadOnly,
                CharacterCount = account.Characters.Count,
            }))
            .ToList());

    public void Dispose()
    {
        try { refreshCancellation.Cancel(); } catch { /* ignore */ }
        if (refreshTask is not { IsCompleted: false })
        {
            try { refreshCancellation.Dispose(); } catch { /* ignore */ }
        }
    }

    private void ScheduleRemoteCatalogRefresh()
    {
        lock (refreshGate)
        {
            if (refreshTask is { IsCompleted: false })
                return;

            var request = new DadProfileCatalogRefreshRequest(
                Guid.NewGuid().ToString("N"),
                configuration.PluginEnabled,
                configuration.LocalOnlyModeEnabled,
                transportService.GetKnownParticipantsSnapshot());
            refreshTask = Task.Run(
                () => RefreshRemoteCatalogsAsync(request, refreshCancellation.Token),
                refreshCancellation.Token);
        }
    }

    private async Task RefreshRemoteCatalogsAsync(
        DadProfileCatalogRefreshRequest request,
        CancellationToken cancellationToken)
    {
        DadProfileCatalogRefreshResult result;
        if (!request.PluginEnabled || request.LocalOnlyModeEnabled)
        {
            result = DadProfileCatalogRefreshResult.MarkOffline(request.RequestId);
        }
        else
        {
            try
            {
                var responses = await transportService.RequestProfileCatalogsAsync(
                    request.RequestId,
                    request.Participants,
                    cancellationToken).ConfigureAwait(false);
                result = DadProfileCatalogRefreshResult.FromResponses(request.RequestId, responses);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.Debug(ex, "[dad][Profiles] Profile catalog refresh failed.");
                result = DadProfileCatalogRefreshResult.MarkOffline(request.RequestId);
            }
        }

        if (cancellationToken.IsCancellationRequested)
            return;

        try
        {
            await Plugin.Framework.RunOnFrameworkThread(() => ApplyRemoteCatalogRefresh(result))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void ApplyRemoteCatalogRefresh(DadProfileCatalogRefreshResult result)
    {
        transportService.RecordProfileCatalogRefreshResult(result.Responses.Count);
        if (result.MarkOwnersOffline)
        {
            MarkCachedOwnersOffline();
            RebuildCurrentCatalogs();
            return;
        }

        try
        {
            var seenOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var response in result.Responses.Where(static response => response.Success))
            {
                var catalog = response.Catalog.Clone();
                catalog.GeneratedAtUtc = DateTime.UtcNow;
                catalog.OwnerOnline = true;
                catalog.ReadOnly = false;
                var ownerKey = GetOwnerKey(catalog);
                seenOwners.Add(ownerKey);
                configuration.ProfileCatalogCache.RemoveAll(existing =>
                    string.Equals(GetOwnerKey(existing), ownerKey, StringComparison.OrdinalIgnoreCase));
                configuration.ProfileCatalogCache.Add(catalog);
            }

            foreach (var cached in configuration.ProfileCatalogCache)
            {
                if (seenOwners.Contains(GetOwnerKey(cached)))
                    continue;

                if (DateTime.UtcNow - cached.GeneratedAtUtc >= OfflineAfter)
                {
                    cached.OwnerOnline = false;
                    cached.ReadOnly = true;
                }
            }

            SaveCacheIfChanged();
            RebuildCurrentCatalogs();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[dad][Profiles] Failed to apply profile catalog refresh.");
            MarkCachedOwnersOffline();
            RebuildCurrentCatalogs();
        }
    }

    private void MarkCachedOwnersOffline()
    {
        foreach (var catalog in configuration.ProfileCatalogCache ?? [])
        {
            catalog.OwnerOnline = false;
            catalog.ReadOnly = true;
        }

        SaveCacheIfChanged();
    }

    private void SaveCacheIfChanged()
    {
        configuration.ProfileCatalogCache ??= [];
        var signature = string.Join(
            "\n",
            configuration.ProfileCatalogCache
                .OrderBy(GetOwnerKey, StringComparer.OrdinalIgnoreCase)
                .Select(catalog => $"{GetOwnerKey(catalog)}|{catalog.OwnerOnline}|{catalog.ReadOnly}|{string.Join(",", catalog.Accounts.OrderBy(static account => account.AccountKey.Value, StringComparer.OrdinalIgnoreCase).Select(static account => $"{account.AccountKey.Value}:{account.Revision}:{account.Characters.Count}:{string.Join(".", account.Characters.Select(static character => character.Revision))}"))}"));
        if (string.Equals(signature, cacheSignature, StringComparison.Ordinal))
            return;

        cacheSignature = signature;
        configuration.Save();
    }

    private static string GetOwnerKey(DadProfileCatalog catalog)
        => !catalog.OwnerWorkerSessionId.IsEmpty
            ? catalog.OwnerWorkerSessionId.Value
            : catalog.OwnerClientInstanceId;

    private void RebuildCurrentCatalogs()
    {
        var catalogs = new List<DadProfileCatalog> { BuildLocalCatalog() };
        catalogs.AddRange((configuration.ProfileCatalogCache ?? [])
            .Where(catalog => !string.Equals(
                catalog.OwnerWorkerSessionId.Value,
                presenceService.WorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase))
            .Select(static catalog => catalog.Clone()));
        currentCatalogs = catalogs
            .OrderByDescending(static catalog => catalog.OwnerOnline)
            .ThenBy(static catalog => catalog.OwnerClientInstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record DadProfileCatalogRefreshRequest(
        string RequestId,
        bool PluginEnabled,
        bool LocalOnlyModeEnabled,
        IReadOnlyList<DadParticipantSnapshot> Participants);

    private sealed record DadProfileCatalogRefreshResult(
        string RequestId,
        IReadOnlyList<DadProfileCatalogResponse> Responses,
        bool MarkOwnersOffline)
    {
        public static DadProfileCatalogRefreshResult FromResponses(
            string requestId,
            IReadOnlyList<DadProfileCatalogResponse> responses)
            => new(requestId, responses.Select(static response => new DadProfileCatalogResponse
            {
                RequestId = response.RequestId,
                Success = response.Success,
                Summary = response.Summary,
                Catalog = response.Catalog.Clone(),
            }).ToList(), MarkOwnersOffline: false);

        public static DadProfileCatalogRefreshResult MarkOffline(string requestId)
            => new(requestId, [], MarkOwnersOffline: true);
    }
}
