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
    private readonly DadBackgroundTaskObserver backgroundTasks;
    private readonly CancellationTokenSource refreshCancellation = new();
    private readonly object refreshGate = new();
    private readonly object cacheGate = new();
    private Task? refreshTask;
    private bool disposed;
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
        backgroundTasks = new DadBackgroundTaskObserver(log, "profile directory");
        configuration.ProfileCatalogCache ??= [];
        MarkCachedOwnersOffline();
        RebuildCurrentCatalogs();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        refreshCancellation.Cancel();
        refreshCancellation.Dispose();
        backgroundTasks.Dispose();
    }

    public void Update()
    {
        if (DateTime.UtcNow < nextRefreshUtc)
        {
            RebuildCurrentCatalogs();
            return;
        }

        nextRefreshUtc = DateTime.UtcNow + RefreshInterval;
        QueueRefreshRemoteCatalogs();
        RebuildCurrentCatalogs();
    }

    public DadProfileCatalog BuildLocalCatalog()
        => configManager.BuildLocalProfileCatalog(
            presenceService.ClientInstanceId,
            presenceService.WorkerSessionId,
            string.Empty,
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

        DadProfileCatalog? owner;
        lock (cacheGate)
        {
            owner = (configuration.ProfileCatalogCache ?? []).FirstOrDefault(catalog =>
                catalog.Accounts.Any(account => DadRosterIdentity.SameAccount(account.AccountKey, request.AccountKey)))?.Clone();
        }
        if (owner == null)
        {
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                Summary = $"No Client Dad owns account {request.AccountKey}.",
            };
        }

        if (!owner.OwnerOnline ||
            owner.ReadOnly ||
            owner.OwnerWorkerSessionId.IsEmpty ||
            !transportService.IsWorkerOnline(owner.OwnerWorkerSessionId))
        {
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                Summary = $"Owning Client Dad for {request.AccountKey} is offline; cached profile is read-only.",
            };
        }

        var ownerKey = GetOwnerKey(owner);
        var ack = transportService.SendProfileUpdate(
            owner.OwnerWorkerSessionId,
            request,
            completed => ApplyRemoteProfileAck(ownerKey, completed));
        if (ack == null)
        {
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                Summary = "Could not queue profile update through Server Dad hub.",
            };
        }

        if (ack.Account != null)
            ApplyRemoteProfileAck(ownerKey, ack);

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
                catalog.OwnerOnline,
                catalog.ReadOnly,
                CharacterCount = account.Characters.Count,
            }))
            .ToList());

    private void QueueRefreshRemoteCatalogs()
    {
        if (disposed)
            return;

        lock (refreshGate)
        {
            if (refreshTask is { IsCompleted: false })
                return;

            refreshTask = RefreshRemoteCatalogsAsync(refreshCancellation.Token);
            backgroundTasks.Track(refreshTask, "remote profile catalog refresh");
        }
    }

    private async Task RefreshRemoteCatalogsAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        RefreshRemoteCatalogs();
        RebuildCurrentCatalogs();
    }

    private void RefreshRemoteCatalogs()
    {
        if (!configuration.PluginEnabled || configuration.LocalOnlyModeEnabled)
        {
            MarkCachedOwnersOffline();
            return;
        }

        try
        {
            var responses = transportService.RequestProfileCatalogs(Guid.NewGuid().ToString("N"));
            lock (cacheGate)
            {
                var seenOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var response in responses.Where(static response => response.Success))
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

                    if (!transportService.IsWorkerOnline(cached.OwnerWorkerSessionId) ||
                        DateTime.UtcNow - cached.GeneratedAtUtc >= OfflineAfter)
                    {
                        cached.OwnerOnline = false;
                        cached.ReadOnly = true;
                    }
                }

                SaveCacheIfChanged();
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[dad][Profiles] Profile catalog refresh failed.");
            MarkCachedOwnersOffline();
        }
    }

    private void MarkCachedOwnersOffline()
    {
        lock (cacheGate)
        {
            foreach (var catalog in configuration.ProfileCatalogCache ?? [])
            {
                catalog.OwnerOnline = false;
                catalog.ReadOnly = true;
            }

            SaveCacheIfChanged();
        }
    }

    private void SaveCacheIfChanged()
    {
        lock (cacheGate)
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
    }

    private static string GetOwnerKey(DadProfileCatalog catalog)
        => !catalog.OwnerWorkerSessionId.IsEmpty
            ? catalog.OwnerWorkerSessionId.Value
            : catalog.OwnerClientInstanceId;

    private void ApplyRemoteProfileAck(string ownerKey, DadProfileUpdateAck ack)
    {
        if (ack.Account == null)
            return;

        lock (cacheGate)
        {
            var owner = (configuration.ProfileCatalogCache ?? []).FirstOrDefault(catalog =>
                string.Equals(GetOwnerKey(catalog), ownerKey, StringComparison.OrdinalIgnoreCase));
            if (owner == null)
                return;

            owner.Accounts.RemoveAll(account => DadRosterIdentity.SameAccount(account.AccountKey, ack.Account.AccountKey));
            owner.Accounts.Add(ack.Account.Clone());
            owner.GeneratedAtUtc = DateTime.UtcNow;
            owner.OwnerOnline = transportService.IsWorkerOnline(owner.OwnerWorkerSessionId);
            owner.ReadOnly = !owner.OwnerOnline;
            SaveCacheIfChanged();
        }

        RebuildCurrentCatalogs();
    }

    private void RebuildCurrentCatalogs()
    {
        var catalogs = new List<DadProfileCatalog> { BuildLocalCatalog() };
        lock (cacheGate)
        {
            catalogs.AddRange((configuration.ProfileCatalogCache ?? [])
                .Where(catalog => !string.Equals(
                    catalog.OwnerWorkerSessionId.Value,
                    presenceService.WorkerSessionId.Value,
                    StringComparison.OrdinalIgnoreCase))
                .Select(static catalog => catalog.Clone()));
        }

        currentCatalogs = catalogs
            .OrderByDescending(static catalog => catalog.OwnerOnline)
            .ThenBy(static catalog => catalog.OwnerClientInstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
