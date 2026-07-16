namespace dad.Services;

internal sealed class DadRevisionSnapshotCache<TSnapshot>
    where TSnapshot : class
{
    private long catalogRevision = long.MinValue;
    private long transportRevision = long.MinValue;
    private TSnapshot? snapshot;

    public TSnapshot GetOrCreate(long currentCatalogRevision, long currentTransportRevision, Func<TSnapshot> factory)
    {
        if (snapshot != null &&
            catalogRevision == currentCatalogRevision &&
            transportRevision == currentTransportRevision)
        {
            return snapshot;
        }

        snapshot = factory();
        catalogRevision = currentCatalogRevision;
        transportRevision = currentTransportRevision;
        return snapshot;
    }

    public void Invalidate()
    {
        snapshot = null;
        catalogRevision = long.MinValue;
        transportRevision = long.MinValue;
    }
}

internal sealed class DadRosterUiSnapshot
{
    public required long CatalogRevision { get; init; }
    public required long TransportRevision { get; init; }
    public required dad.Models.DadAccountRosterCatalog Catalog { get; init; }
}
