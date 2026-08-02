using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadProfileDirectoryService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(15);

    private readonly Configuration configuration;
    private readonly ConfigManager configManager;
    private readonly DadPresenceService presenceService;
    private readonly DadTransportService transportService;
    private readonly IPluginLog log;
    private readonly DadOnlineProfileCatalogCache remoteCatalogs = new(RefreshInterval, OfflineAfter);
    private bool disposed;
    private long projectedLocalRevision = -1;
    private long projectedRemoteRevision = -1;
    private long projectedTransportRevision = -1;
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
        RebuildCurrentCatalogs(force: true);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
    }

    public void Update()
    {
        var nowUtc = DateTime.UtcNow;
        if (!configuration.PluginEnabled || configuration.LocalOnlyModeEnabled)
        {
            remoteCatalogs.Clear();
        }
        else
        {
            remoteCatalogs.ObserveTransport(nowUtc, transportService.IsWorkerOnline);
            if (remoteCatalogs.TryBeginRefresh(nowUtc))
                RefreshRemoteCatalogs();
        }

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

    public void PurgeAccount(DadAccountKey accountKey)
    {
        if (remoteCatalogs.RemoveAccount(accountKey))
            RebuildCurrentCatalogs(force: true);
    }

    public DadProfileUpdateAck UpdateProfile(DadProfileUpdateRequest request)
    {
        var local = BuildLocalCatalog();
        if (local.Accounts.Any(account => DadRosterIdentity.SameAccount(account.AccountKey, request.AccountKey)))
        {
            var localAck = configManager.ApplyProfileUpdate(request);
            RebuildCurrentCatalogs(force: true);
            return localAck;
        }

        var owner = remoteCatalogs.FindOwner(request.AccountKey);
        if (owner == null)
        {
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                Summary = $"No online Client Dad owns account {request.AccountKey}.",
            };
        }

        if (owner.OwnerWorkerSessionId.IsEmpty || !transportService.IsWorkerOnline(owner.OwnerWorkerSessionId))
        {
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                Summary = $"Owning Client Dad for {request.AccountKey} is offline; profile is read-only.",
            };
        }

        var ownerKey = owner.OwnerWorkerSessionId.Value;
        var ack = transportService.SendProfileUpdate(
            owner.OwnerWorkerSessionId,
            request,
            completed => ApplyRemoteProfileAck(ownerKey, completed));
        if (ack == null)
        {
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                Summary = "Could not queue profile update through Dad Coordinator hub.",
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

    private void RefreshRemoteCatalogs()
    {
        if (!configuration.PluginEnabled || configuration.LocalOnlyModeEnabled)
            return;

        try
        {
            var nowUtc = DateTime.UtcNow;
            var responses = transportService.RequestProfileCatalogs(Guid.NewGuid().ToString("N"));
            foreach (var response in responses.Where(static response => response.Success))
            {
                var catalog = response.Catalog;
                if (catalog.OwnerWorkerSessionId.IsEmpty ||
                    string.Equals(catalog.OwnerWorkerSessionId.Value, presenceService.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) ||
                    !transportService.IsWorkerOnline(catalog.OwnerWorkerSessionId))
                {
                    continue;
                }

                remoteCatalogs.Upsert(catalog, nowUtc);
            }

            remoteCatalogs.ObserveTransport(nowUtc, transportService.IsWorkerOnline);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[dad][Profiles] Profile catalog refresh failed.");
        }
    }

    private void ApplyRemoteProfileAck(string ownerWorkerId, DadProfileUpdateAck ack)
    {
        if (ack.Account == null)
            return;

        if (remoteCatalogs.ApplyAccount(ownerWorkerId, ack.Account, DateTime.UtcNow))
            RebuildCurrentCatalogs(force: true);
    }

    private void RebuildCurrentCatalogs(bool force = false)
    {
        var localRevision = configManager.ProfileCatalogRevision;
        var remoteRevision = remoteCatalogs.Revision;
        var transportRevision = transportService.TransportRevision;
        if (!force &&
            localRevision == projectedLocalRevision &&
            remoteRevision == projectedRemoteRevision &&
            transportRevision == projectedTransportRevision)
        {
            return;
        }

        var catalogs = new List<DadProfileCatalog> { BuildLocalCatalog() };
        catalogs.AddRange(remoteCatalogs.BuildOnlineProjection(transportService.IsWorkerOnline));
        currentCatalogs = catalogs
            .OrderByDescending(static catalog => catalog.OwnerOnline)
            .ThenBy(static catalog => catalog.OwnerClientInstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        projectedLocalRevision = localRevision;
        projectedRemoteRevision = remoteRevision;
        projectedTransportRevision = transportRevision;
    }
}
