using dad.Models;

namespace dad.Services;

/// <summary>
/// Session-only remote profile catalogs. Entries are keyed by worker session and are never serialized.
/// </summary>
internal sealed class DadOnlineProfileCatalogCache
{
    private sealed class Entry
    {
        public required DadProfileCatalog Catalog { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public DateTime? OfflineSinceUtc { get; set; }
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan refreshInterval;
    private readonly TimeSpan offlineAfter;
    private DateTime nextRefreshUtc = DateTime.MinValue;
    private long revision;

    public DadOnlineProfileCatalogCache(TimeSpan refreshInterval, TimeSpan offlineAfter)
    {
        this.refreshInterval = refreshInterval;
        this.offlineAfter = offlineAfter;
    }

    public long Revision
    {
        get
        {
            lock (gate)
                return revision;
        }
    }

    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    public bool TryBeginRefresh(DateTime nowUtc)
    {
        lock (gate)
        {
            if (nowUtc < nextRefreshUtc)
                return false;

            nextRefreshUtc = nowUtc + refreshInterval;
            return true;
        }
    }

    public bool Upsert(DadProfileCatalog catalog, DateTime nowUtc)
    {
        var workerId = catalog.OwnerWorkerSessionId.Value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(workerId))
            return false;

        var replacement = catalog.Clone();
        replacement.GeneratedAtUtc = nowUtc;
        replacement.OwnerOnline = true;
        replacement.ReadOnly = false;

        lock (gate)
        {
            var changed = !entries.TryGetValue(workerId, out var existing) ||
                          !SameCatalog(existing.Catalog, replacement) ||
                          existing.OfflineSinceUtc.HasValue;
            entries[workerId] = new Entry
            {
                Catalog = replacement,
                LastSeenUtc = nowUtc,
            };
            if (changed)
                revision++;
            return changed;
        }
    }

    public bool ObserveTransport(DateTime nowUtc, Func<DadWorkerSessionId, bool> isOnline)
    {
        var changed = false;
        lock (gate)
        {
            foreach (var pair in entries.ToList())
            {
                var entry = pair.Value;
                if (isOnline(entry.Catalog.OwnerWorkerSessionId))
                {
                    if (entry.OfflineSinceUtc.HasValue)
                    {
                        entry.OfflineSinceUtc = null;
                        changed = true;
                    }
                    continue;
                }

                if (!entry.OfflineSinceUtc.HasValue)
                {
                    entry.OfflineSinceUtc = nowUtc;
                    changed = true;
                    continue;
                }

                if (nowUtc - entry.OfflineSinceUtc.Value < offlineAfter)
                    continue;

                entries.Remove(pair.Key);
                changed = true;
            }

            if (changed)
                revision++;
        }

        return changed;
    }

    public IReadOnlyList<DadProfileCatalog> BuildOnlineProjection(Func<DadWorkerSessionId, bool> isOnline)
    {
        lock (gate)
        {
            return entries.Values
                .Where(entry => !entry.OfflineSinceUtc.HasValue && isOnline(entry.Catalog.OwnerWorkerSessionId))
                .Select(entry => entry.Catalog.Clone())
                .OrderBy(static catalog => catalog.OwnerClientInstanceId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public DadProfileCatalog? FindOwner(DadAccountKey accountKey)
    {
        lock (gate)
        {
            return entries.Values
                .Where(static entry => !entry.OfflineSinceUtc.HasValue)
                .Select(static entry => entry.Catalog)
                .FirstOrDefault(catalog => catalog.Accounts.Any(account => DadRosterIdentity.SameAccount(account.AccountKey, accountKey)))
                ?.Clone();
        }
    }

    public bool ApplyAccount(string workerId, DadAccountProfileRecord account, DateTime nowUtc)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(workerId, out var entry))
                return false;

            entry.Catalog.Accounts.RemoveAll(existing => DadRosterIdentity.SameAccount(existing.AccountKey, account.AccountKey));
            entry.Catalog.Accounts.Add(account.Clone());
            entry.Catalog.GeneratedAtUtc = nowUtc;
            entry.LastSeenUtc = nowUtc;
            entry.OfflineSinceUtc = null;
            revision++;
            return true;
        }
    }

    public bool RemoveAccount(DadAccountKey accountKey)
    {
        var changed = false;
        lock (gate)
        {
            foreach (var entry in entries.Values)
                changed |= entry.Catalog.Accounts.RemoveAll(account => DadRosterIdentity.SameAccount(account.AccountKey, accountKey)) > 0;
            if (changed)
                revision++;
        }

        return changed;
    }

    public bool Clear()
    {
        lock (gate)
        {
            if (entries.Count == 0)
                return false;
            entries.Clear();
            revision++;
            return true;
        }
    }

    private static bool SameCatalog(DadProfileCatalog left, DadProfileCatalog right)
    {
        if (!string.Equals(left.OwnerClientInstanceId, right.OwnerClientInstanceId, StringComparison.OrdinalIgnoreCase) ||
            left.Accounts.Count != right.Accounts.Count)
        {
            return false;
        }

        var leftAccounts = left.Accounts.OrderBy(static account => account.AccountKey.Value, StringComparer.OrdinalIgnoreCase).ToList();
        var rightAccounts = right.Accounts.OrderBy(static account => account.AccountKey.Value, StringComparer.OrdinalIgnoreCase).ToList();
        for (var index = 0; index < leftAccounts.Count; index++)
        {
            var leftAccount = leftAccounts[index];
            var rightAccount = rightAccounts[index];
            if (!DadRosterIdentity.SameAccount(leftAccount.AccountKey, rightAccount.AccountKey) ||
                leftAccount.Revision != rightAccount.Revision ||
                leftAccount.Characters.Count != rightAccount.Characters.Count ||
                !string.Equals(leftAccount.AccountAlias, rightAccount.AccountAlias, StringComparison.Ordinal) ||
                !string.Equals(leftAccount.PrimaryLaunchProfileId, rightAccount.PrimaryLaunchProfileId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
