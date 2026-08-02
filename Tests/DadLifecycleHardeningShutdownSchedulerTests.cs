using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadLifecycleHardeningShutdownSchedulerTests
{
    [Fact]
    public void FirstCleanupRunsEveryLocalOwnerIncludingTakeoverRelease()
    {
        var decision = DadLifecycleCleanupRules.Decide(
            hasRecordedResult: false,
            cleanupPending: false);

        Assert.True(decision.RunFullCleanup);
        Assert.True(decision.RetryTakeoverCleanup);
        Assert.False(decision.ReturnRecordedResult);
    }

    [Fact]
    public void PendingCleanupRetriesOnlyTakeoverRelease()
    {
        var decision = DadLifecycleCleanupRules.Decide(
            hasRecordedResult: true,
            cleanupPending: true);

        Assert.False(decision.RunFullCleanup);
        Assert.True(decision.RetryTakeoverCleanup);
        Assert.False(decision.ReturnRecordedResult);
    }

    [Fact]
    public void CompletedCleanupIsIdempotent()
    {
        var decision = DadLifecycleCleanupRules.Decide(
            hasRecordedResult: true,
            cleanupPending: false);

        Assert.False(decision.RunFullCleanup);
        Assert.False(decision.RetryTakeoverCleanup);
        Assert.True(decision.ReturnRecordedResult);
    }

    [Theory]
    [InlineData(1, false, 0, true)]
    [InlineData(1, true, 0, false)]
    [InlineData(1, false, 1, false)]
    [InlineData(2, false, 0, false)]
    public void OnlyRosterlessSingleWorkerRunsSkipPartyTeardown(
        int requiredParticipantCount,
        bool hasSlotManifest,
        int requiredRosterCharacterCount,
        bool expected)
        => Assert.Equal(
            expected,
            DadLifecycleCleanupRules.ShouldFinalizeRosterlessSingleWorker(
                requiredParticipantCount,
                hasSlotManifest,
                requiredRosterCharacterCount));

    [Fact]
    public void CoordinatorFanoutRebindsRequesterToAuthenticatedFrameSource()
    {
        var requestedAt = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        var original = new DadStopAllRequest
        {
            OperationId = "stop-1",
            RequestedByWorkerSessionId = new DadWorkerSessionId("operator-worker"),
            RequestedAtUtc = requestedAt,
            Reason = "operator stop",
        };

        var fanout = DadLifecycleCleanupRules.RebindStopAllFanoutRequester(
            original,
            new DadWorkerSessionId("coordinator-worker"));

        Assert.Equal("operator-worker", original.RequestedByWorkerSessionId.Value);
        Assert.Equal("coordinator-worker", fanout.RequestedByWorkerSessionId.Value);
        Assert.Equal(original.OperationId, fanout.OperationId);
        Assert.Equal(requestedAt, fanout.RequestedAtUtc);
        Assert.Equal(original.Reason, fanout.Reason);
    }
}
