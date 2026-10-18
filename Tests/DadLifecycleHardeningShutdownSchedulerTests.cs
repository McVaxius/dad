using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadLifecycleHardeningShutdownSchedulerTests
{
    [Theory]
    [InlineData(DadRunPhase.InDutyOrTask)]
    [InlineData(DadRunPhase.PostRunStabilizing)]
    [InlineData(DadRunPhase.RoutingModules)]
    public void ChildFinalizingNeverAdvancesCoordinatorPastPartyTeardown(DadRunPhase current)
        => Assert.Equal(DadRunPhase.RoutingModules,
            DadLifecycleCleanupRules.ResolveCoordinatorModulePhase(current, DadRunPhase.Finalizing));

    [Fact]
    public void IdleChildKeepsOwnedCoordinatorPhaseAndOrdinaryProgressRemainsVisible()
    {
        Assert.Equal(DadRunPhase.PostRunStabilizing,
            DadLifecycleCleanupRules.ResolveCoordinatorModulePhase(DadRunPhase.PostRunStabilizing, DadRunPhase.Idle));
        Assert.Equal(DadRunPhase.InDutyOrTask,
            DadLifecycleCleanupRules.ResolveCoordinatorModulePhase(DadRunPhase.RoutingModules, DadRunPhase.InDutyOrTask));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void FourClientExitOrderingNeverLetsLeaderFinalizingSkipThePartyBoundary(int leaderExitIndex)
    {
        var now = new DateTime(2026, 10, 18, 20, 0, 0, DateTimeKind.Utc);
        for (var preset = 0; preset < 3; preset++)
        {
            var exitOrder = new List<int> { 1, 2, 3 };
            exitOrder.Insert(leaderExitIndex, 0);
            var completed = new HashSet<int>();
            var phase = DadRunPhase.InDutyOrTask;
            foreach (var worker in exitOrder)
            {
                completed.Add(worker);
                if (!completed.Contains(0))
                    continue;
                phase = DadLifecycleCleanupRules.ResolveCoordinatorModulePhase(phase, DadRunPhase.Finalizing);
                Assert.Equal(DadRunPhase.RoutingModules, phase);
                Assert.NotEqual(DadRunPhase.Finalizing, phase);
            }
            Assert.Equal(4, completed.Count);

            // Only the aggregate worker barrier opens the independent party boundary.
            phase = DadRunPhase.TearingDownParty;
            var started = now.AddMinutes(preset);
            var teardown = new DadPartyTeardownController([1UL, 2UL, 3UL, 4UL], 1, started, false, string.Empty);
            var party = new DadPartyTeardownObservation(started, 1, 1, [1UL, 2UL, 3UL, 4UL],
                true, false, false, true, false, false, string.Empty, string.Empty, string.Empty);
            Assert.Equal(DadPartyTeardownAction.SendBreakup, teardown.Pulse(party).Action);
            Assert.Equal(DadPartyTeardownAction.ApprovePrompt, teardown.Pulse(party with
            {
                NowUtc = started.AddMilliseconds(100), PromptVisible = true,
                PromptIdentity = $"preset-{preset}-breakup", PromptText = "Disband party?",
            }).Action);
            var solo = party with
            {
                NowUtc = started.AddSeconds(1), PartyMemberContentIds = [1UL], IsCrossRealmParty = false,
            };
            Assert.Equal(DadPartyTeardownAction.None, teardown.Pulse(solo).Action);
            Assert.Equal(DadRunPhase.TearingDownParty, phase);
            Assert.Equal(DadPartyTeardownAction.Complete,
                teardown.Pulse(solo with { NowUtc = started.AddSeconds(2) }).Action);
        }
    }

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
