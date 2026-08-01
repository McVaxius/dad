using Xunit;

namespace dad.Tests;

public sealed class DadReliabilityIntegrationSourceContractTests
{
    [Fact]
    public void SchedulerReevaluatesFrozenLevelSeekAfterReadinessAndJobAcknowledgementBeforeDispatch()
    {
        var source = ReadRepositorySource("Services", "DadSchedulerService.cs");
        var initialDailyPreflight = source.IndexOf(
            "if (frozenLevelSeekGroup == null &&",
            StringComparison.Ordinal);
        var initialLevelSeekSkip = source.IndexOf(
            "if (levelSeek.ShouldSkip)",
            StringComparison.Ordinal);
        var acknowledgement = source.IndexOf(
            "if (!assignmentsAcknowledged)",
            StringComparison.Ordinal);
        var postWakeCheck = source.IndexOf(
            "if (TrySkipSatisfiedPostWakeLevelTargets())",
            acknowledgement,
            StringComparison.Ordinal);
        var deferredDailyPreflight = source.IndexOf(
            "TryBeginDailyRewardPreflight(frozenLevelSeekGroup)",
            postWakeCheck,
            StringComparison.Ordinal);
        var plannerDispatch = source.IndexOf(
            "var result = startPlannerRequest",
            postWakeCheck,
            StringComparison.Ordinal);

        Assert.True(initialDailyPreflight >= 0);
        Assert.True(initialLevelSeekSkip >= 0);
        Assert.True(initialDailyPreflight > initialLevelSeekSkip);
        Assert.True(acknowledgement >= 0);
        Assert.True(postWakeCheck > acknowledgement);
        Assert.True(deferredDailyPreflight > postWakeCheck);
        Assert.True(plannerDispatch > postWakeCheck);
        Assert.True(plannerDispatch > deferredDailyPreflight);
        Assert.Contains("if (dailyRewardPreflightAttempted)", source, StringComparison.Ordinal);
        Assert.Contains("dailyRewardPreflightAttempted = true;", source, StringComparison.Ordinal);
        Assert.Contains(
            "characterIntelligenceService.RequestPeerSnapshots()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "DadLevelSeekEvaluationRules.Evaluate(",
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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dad.csproj")))
            directory = directory.Parent;
        var repositoryRoot = directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate the Dad repository root from the test output directory.");
        return File.ReadAllText(Path.Combine([repositoryRoot, .. pathParts]));
    }
}
