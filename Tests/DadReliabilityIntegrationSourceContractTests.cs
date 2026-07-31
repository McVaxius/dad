using Xunit;

namespace dad.Tests;

public sealed class DadReliabilityIntegrationSourceContractTests
{
    [Fact]
    public void SchedulerReevaluatesFrozenTargetsAfterReadinessAndJobAcknowledgementBeforeDispatch()
    {
        var source = ReadRepositorySource("Services", "DadSchedulerService.cs");
        var acknowledgement = source.IndexOf(
            "if (!assignmentsAcknowledged)",
            StringComparison.Ordinal);
        var postWakeCheck = source.IndexOf(
            "if (TrySkipSatisfiedPostWakeLevelTargets())",
            acknowledgement,
            StringComparison.Ordinal);
        var plannerDispatch = source.IndexOf(
            "var result = startPlannerRequest",
            postWakeCheck,
            StringComparison.Ordinal);

        Assert.True(acknowledgement >= 0);
        Assert.True(postWakeCheck > acknowledgement);
        Assert.True(plannerDispatch > postWakeCheck);
        Assert.Contains(
            "characterIntelligenceService.RequestPeerSnapshots()",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CoordinatorRefreshesAggregateEvidenceBeforePostRunStopDecision()
    {
        var source = ReadRepositorySource("Services", "DadCoordinatorService.cs");
        var loop = source.IndexOf(
            "private bool TryContinueStopPolicyLoop",
            StringComparison.Ordinal);
        var refresh = source.IndexOf(
            "RefreshStopProgressSummary(activePlan, refreshPool: true);",
            loop,
            StringComparison.Ordinal);
        var stopDecision = source.IndexOf(
            "if (stopProgress.StopReached)",
            refresh,
            StringComparison.Ordinal);
        var aggregateEvaluation = source.IndexOf(
            "DadResolvedLevelTargetRules.Evaluate(policy, pool)",
            StringComparison.Ordinal);

        Assert.True(loop >= 0);
        Assert.True(refresh > loop);
        Assert.True(stopDecision > refresh);
        Assert.True(aggregateEvaluation >= 0);
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine([repositoryRoot, .. pathParts]));
    }
}
