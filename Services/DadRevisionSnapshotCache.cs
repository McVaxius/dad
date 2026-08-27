using System.Collections.Immutable;

namespace dad.Services;

internal sealed class DadProjectionCache<TKey, TSnapshot>
    where TKey : notnull
    where TSnapshot : class
{
    private bool hasSnapshot;
    private TKey? semanticKey;
    private TSnapshot? snapshot;
    private DateTime? validUntilUtc;

    public DateTime? ValidUntilUtc => validUntilUtc;

    public TSnapshot GetOrCreate(
        TKey currentSemanticKey,
        Func<TSnapshot> factory,
        DateTime utcNow,
        DateTime? currentValidUntilUtc = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (hasSnapshot && snapshot != null &&
            EqualityComparer<TKey>.Default.Equals(semanticKey!, currentSemanticKey) &&
            (!validUntilUtc.HasValue || utcNow < validUntilUtc.Value))
            return snapshot;

        snapshot = factory();
        semanticKey = currentSemanticKey;
        validUntilUtc = currentValidUntilUtc;
        hasSnapshot = true;
        return snapshot;
    }

    public bool TryGet(TKey currentSemanticKey, DateTime utcNow, out TSnapshot current)
    {
        if (hasSnapshot && snapshot != null &&
            EqualityComparer<TKey>.Default.Equals(semanticKey!, currentSemanticKey) &&
            (!validUntilUtc.HasValue || utcNow < validUntilUtc.Value))
        {
            current = snapshot;
            return true;
        }

        current = null!;
        return false;
    }

    public void Invalidate()
    {
        hasSnapshot = false;
        semanticKey = default;
        snapshot = null;
        validUntilUtc = null;
    }
}

internal sealed class DadSemanticRevisionTracker<TSemantic>
    where TSemantic : notnull
{
    private readonly IEqualityComparer<TSemantic> comparer;
    private bool hasSemanticValue;
    private TSemantic? semanticValue;
    private long revision;

    public DadSemanticRevisionTracker(IEqualityComparer<TSemantic>? comparer = null)
        => this.comparer = comparer ?? EqualityComparer<TSemantic>.Default;

    public long Revision => revision;

    public long Observe(TSemantic current)
    {
        if (hasSemanticValue && comparer.Equals(semanticValue!, current))
            return revision;

        semanticValue = current;
        hasSemanticValue = true;
        return ++revision;
    }
}

internal sealed class DadOrderedSemantic<T> : IEquatable<DadOrderedSemantic<T>>
{
    private readonly ImmutableArray<T> values;

    public DadOrderedSemantic(IEnumerable<T> source)
        => values = source.ToImmutableArray();

    public IReadOnlyList<T> Values => values;

    public bool Equals(DadOrderedSemantic<T>? other)
        => other != null && values.AsSpan().SequenceEqual(other.values.AsSpan());

    public override bool Equals(object? obj) => obj is DadOrderedSemantic<T> other && Equals(other);

    public override int GetHashCode() => 0;
}

internal readonly record struct DadRosterUiProjectionKey(long CatalogRevision, long TransportRevision);

internal sealed record DadRosterUiSnapshot(
    long CatalogRevision,
    long TransportRevision,
    dad.Models.DadAccountRosterCatalog Catalog);
