namespace dad.Models;

public enum DadSafetyProofDisposition
{
    Ready,
    Wait,
    Reject,
}

public readonly record struct DadStableContradictionDecision(
    DadSafetyProofDisposition Disposition,
    string Evidence,
    DateTime? FirstObservedAtUtc,
    string Summary);

/// <summary>
/// Requires the same contradiction in two consecutive world-stable observations
/// separated by a real refresh interval. Transient/unstable truth can never
/// confirm a rejection.
/// </summary>
public sealed class DadStableContradictionTracker
{
    private string pendingEvidence = string.Empty;
    private DateTime? firstObservedAtUtc;
    private DateTime? lastSourceObservedAtUtc;

    public DadStableContradictionDecision Observe(
        string? contradictionEvidence,
        bool worldStable,
        DateTime nowUtc,
        TimeSpan minimumRefreshInterval,
        DateTime? sourceObservedAtUtc = null)
    {
        nowUtc = EnsureUtc(nowUtc);
        var sourceObservation = EnsureUtc(sourceObservedAtUtc ?? nowUtc);
        minimumRefreshInterval = minimumRefreshInterval < TimeSpan.Zero
            ? TimeSpan.Zero
            : minimumRefreshInterval;
        var evidence = contradictionEvidence?.Trim() ?? string.Empty;

        if (!worldStable || string.IsNullOrWhiteSpace(evidence))
        {
            Reset();
            return new DadStableContradictionDecision(
                DadSafetyProofDisposition.Ready,
                string.Empty,
                null,
                worldStable
                    ? "No stable identity contradiction is present."
                    : "Runtime identity is not world-stable; contradiction proof was cleared.");
        }

        if (!string.Equals(pendingEvidence, evidence, StringComparison.Ordinal))
        {
            pendingEvidence = evidence;
            firstObservedAtUtc = nowUtc;
            lastSourceObservedAtUtc = sourceObservation;
            return Pending(evidence, nowUtc);
        }

        if (!firstObservedAtUtc.HasValue ||
            nowUtc - firstObservedAtUtc.Value < minimumRefreshInterval ||
            !lastSourceObservedAtUtc.HasValue ||
            sourceObservation <= lastSourceObservedAtUtc.Value)
        {
            return Pending(evidence, firstObservedAtUtc ?? nowUtc);
        }

        lastSourceObservedAtUtc = sourceObservation;

        return new DadStableContradictionDecision(
            DadSafetyProofDisposition.Reject,
            evidence,
            firstObservedAtUtc,
            $"Confirmed the same world-stable contradiction in two fresh observations: {evidence}");
    }

    public void Reset()
    {
        pendingEvidence = string.Empty;
        firstObservedAtUtc = null;
        lastSourceObservedAtUtc = null;
    }

    private static DadStableContradictionDecision Pending(string evidence, DateTime firstObservedAtUtc)
        => new(
            DadSafetyProofDisposition.Wait,
            evidence,
            firstObservedAtUtc,
            $"Waiting for a second fresh world-stable observation of: {evidence}");

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

public enum DadImmutableCommandDisposition
{
    Accepted,
    Duplicate,
    Collision,
}

public sealed class DadImmutableCommandRegistration
{
    public DadImmutableCommandDisposition Disposition { get; init; }
    public string CommandId { get; init; } = string.Empty;
    public string OriginalFingerprint { get; init; } = string.Empty;
    public string IncomingFingerprint { get; init; } = string.Empty;
    public string OriginalPayload { get; init; } = string.Empty;
    public string IncomingPayload { get; init; } = string.Empty;
    public string OriginalProducerRoute { get; init; } = string.Empty;
    public string IncomingProducerRoute { get; init; } = string.Empty;
}

/// <summary>
/// Process-lifetime command ledger. A duplicate is idempotent; reusing the same
/// command ID with another frozen payload is an immediate protocol collision.
/// </summary>
public sealed class DadImmutableCommandRegistry
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public DadImmutableCommandRegistration Register(
        string commandId,
        string fingerprint,
        string payload,
        string producerRoute)
    {
        commandId = commandId?.Trim() ?? string.Empty;
        fingerprint ??= string.Empty;
        payload ??= string.Empty;
        producerRoute ??= string.Empty;

        if (!entries.TryGetValue(commandId, out var original))
        {
            entries[commandId] = new Entry(fingerprint, payload, producerRoute);
            return Build(DadImmutableCommandDisposition.Accepted, commandId, entries[commandId], fingerprint, payload, producerRoute);
        }

        return Build(
            string.Equals(original.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? DadImmutableCommandDisposition.Duplicate
                : DadImmutableCommandDisposition.Collision,
            commandId,
            original,
            fingerprint,
            payload,
            producerRoute);
    }

    public void Reset() => entries.Clear();

    private static DadImmutableCommandRegistration Build(
        DadImmutableCommandDisposition disposition,
        string commandId,
        Entry original,
        string incomingFingerprint,
        string incomingPayload,
        string incomingProducerRoute)
        => new()
        {
            Disposition = disposition,
            CommandId = commandId,
            OriginalFingerprint = original.Fingerprint,
            IncomingFingerprint = incomingFingerprint,
            OriginalPayload = original.Payload,
            IncomingPayload = incomingPayload,
            OriginalProducerRoute = original.ProducerRoute,
            IncomingProducerRoute = incomingProducerRoute,
        };

    private sealed record Entry(string Fingerprint, string Payload, string ProducerRoute);
}
