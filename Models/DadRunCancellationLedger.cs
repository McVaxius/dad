namespace dad.Models;

/// <summary>
/// Process-lifetime tombstones for cancelled run identities. A cancellation is
/// intentionally not cleared when active state is reset: a delayed assignment
/// for the same run must remain unable to mutate later.
/// </summary>
public sealed class DadRunCancellationLedger
{
    private readonly HashSet<string> cancelledRunIds;

    public DadRunCancellationLedger(StringComparer? comparer = null)
        => cancelledRunIds = new HashSet<string>(comparer ?? StringComparer.Ordinal);

    public bool Record(string? runId)
    {
        var normalized = runId?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(normalized) && cancelledRunIds.Add(normalized);
    }

    public bool IsCancelled(string? runId)
    {
        var normalized = runId?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(normalized) && cancelledRunIds.Contains(normalized);
    }

    public bool CanAccept(string? runId) => !IsCancelled(runId);
}
