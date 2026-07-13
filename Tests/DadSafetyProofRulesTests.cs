using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadSafetyProofRulesTests
{
    private static readonly DateTime Start = new(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ContradictionRequiresTwoMatchingFreshStableProofs()
    {
        var tracker = new DadStableContradictionTracker();

        var first = tracker.Observe("content 2 != 1", true, Start, TimeSpan.FromSeconds(2));
        var tooSoon = tracker.Observe("content 2 != 1", true, Start.AddSeconds(1), TimeSpan.FromSeconds(2));
        var confirmed = tracker.Observe("content 2 != 1", true, Start.AddSeconds(2), TimeSpan.FromSeconds(2));

        Assert.Equal(DadSafetyProofDisposition.Wait, first.Disposition);
        Assert.Equal(DadSafetyProofDisposition.Wait, tooSoon.Disposition);
        Assert.Equal(DadSafetyProofDisposition.Reject, confirmed.Disposition);
    }

    [Fact]
    public void ReusingOneStableSnapshotCannotConfirmContradiction()
    {
        var tracker = new DadStableContradictionTracker();
        var snapshotAt = Start;

        var first = tracker.Observe(
            "content 2 != 1",
            true,
            Start,
            TimeSpan.FromSeconds(2),
            snapshotAt);
        var repeated = tracker.Observe(
            "content 2 != 1",
            true,
            Start.AddSeconds(3),
            TimeSpan.FromSeconds(2),
            snapshotAt);
        var refreshed = tracker.Observe(
            "content 2 != 1",
            true,
            Start.AddSeconds(4),
            TimeSpan.FromSeconds(2),
            snapshotAt.AddSeconds(4));

        Assert.Equal(DadSafetyProofDisposition.Wait, first.Disposition);
        Assert.Equal(DadSafetyProofDisposition.Wait, repeated.Disposition);
        Assert.Equal(DadSafetyProofDisposition.Reject, refreshed.Disposition);
    }

    [Fact]
    public void UnstableEmptyOrChangedEvidenceCannotConfirmContradiction()
    {
        var tracker = new DadStableContradictionTracker();

        Assert.Equal(DadSafetyProofDisposition.Wait, tracker.Observe("wrong account", true, Start, TimeSpan.FromSeconds(2)).Disposition);
        Assert.Equal(DadSafetyProofDisposition.Ready, tracker.Observe("wrong account", false, Start.AddSeconds(2), TimeSpan.FromSeconds(2)).Disposition);
        Assert.Equal(DadSafetyProofDisposition.Wait, tracker.Observe("wrong account", true, Start.AddSeconds(4), TimeSpan.FromSeconds(2)).Disposition);
        Assert.Equal(DadSafetyProofDisposition.Wait, tracker.Observe("wrong content", true, Start.AddSeconds(6), TimeSpan.FromSeconds(2)).Disposition);
        Assert.Equal(DadSafetyProofDisposition.Ready, tracker.Observe(null, true, Start.AddSeconds(8), TimeSpan.FromSeconds(2)).Disposition);
    }

    [Fact]
    public void ImmutableCommandDuplicateIsIdempotentButChangedPayloadCollidesImmediately()
    {
        var registry = new DadImmutableCommandRegistry();

        var accepted = registry.Register("command-1", "fingerprint-a", "payload-a", "producer-a/route-a");
        var duplicate = registry.Register("command-1", "fingerprint-a", "payload-a", "producer-a/route-b");
        var collision = registry.Register("command-1", "fingerprint-b", "payload-b", "producer-b/route-c");

        Assert.Equal(DadImmutableCommandDisposition.Accepted, accepted.Disposition);
        Assert.Equal(DadImmutableCommandDisposition.Duplicate, duplicate.Disposition);
        Assert.Equal(DadImmutableCommandDisposition.Collision, collision.Disposition);
        Assert.Equal("payload-a", collision.OriginalPayload);
        Assert.Equal("payload-b", collision.IncomingPayload);
        Assert.Equal("producer-a/route-a", collision.OriginalProducerRoute);
        Assert.Equal("producer-b/route-c", collision.IncomingProducerRoute);
    }
}
