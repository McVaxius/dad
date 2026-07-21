using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadMeasuredPilotTests
{
    [Fact]
    public void EvaluationRequiresEveryMeasuredCoverageDimension()
    {
        var campaign = PassingCampaign();

        var evaluation = DadMeasuredPilotService.Evaluate(campaign);

        Assert.True(evaluation.Passed);
        Assert.Equal(10, evaluation.QualifyingSuccesses);
        Assert.Equal(3, evaluation.PlanSuccesses);
        Assert.Equal(3, evaluation.ScheduleSuccesses);
        Assert.Equal(2, evaluation.RequestedJobSuccesses);
        Assert.Equal(1, evaluation.RequestedJobSwitches);
        Assert.Empty(evaluation.Missing);
    }

    [Fact]
    public void FailuresRemainInCampaignButDoNotCountTowardTen()
    {
        var campaign = PassingCampaign();
        var failed = QualifyingRun("failed", DadMeasuredPilotOrigin.Plans);
        failed.Successful = false;
        failed.FailureCode = "ordinary-failure";
        campaign.Runs.Add(failed);

        var evaluation = DadMeasuredPilotService.Evaluate(campaign);

        Assert.Equal(11, campaign.Runs.Count);
        Assert.Equal(10, evaluation.QualifyingSuccesses);

    }

    [Fact]
    public void SafetyViolationHardFailsOtherwisePassingCampaign()
    {
        var campaign = PassingCampaign();
        campaign.SafetyViolations.Add("queue-before-ready");

        var evaluation = DadMeasuredPilotService.Evaluate(campaign);

        Assert.Equal(DadMeasuredPilotState.HardFailed, evaluation.State);
        Assert.False(evaluation.Passed);
    }

    [Fact]
    public void IncompleteEvaluationReportsExactMissingCountsAndCanBeResumed()
    {
        var campaign = new DadMeasuredPilotCampaign
        {
            State = DadMeasuredPilotState.Active,
            StoppedAtUtc = DateTime.UtcNow,
            Runs = [QualifyingRun("one", DadMeasuredPilotOrigin.Plans)],
        };

        var evaluation = DadMeasuredPilotService.Evaluate(campaign);

        Assert.Equal(DadMeasuredPilotState.EvaluationIncomplete, evaluation.State);
        Assert.Contains("successful multi-client executions: 1/10", evaluation.Missing);
        Assert.Contains("direct Plans executions: 1/3", evaluation.Missing);
        Assert.Contains("Schedule executions: 0/3", evaluation.Missing);
    }

    private static DadMeasuredPilotCampaign PassingCampaign()
    {
        var runs = Enumerable.Range(0, 10).Select(index =>
        {
            var origin = index < 3 ? DadMeasuredPilotOrigin.Plans :
                index < 6 ? DadMeasuredPilotOrigin.Schedules : DadMeasuredPilotOrigin.Unknown;
            var run = QualifyingRun($"run-{index}", origin);
            if (index < 2)
            {
                run.RequestedJobRun = true;
                run.RequestedJobMatched = true;
                run.RequestedJobSwitched = index == 0;
            }
            return run;
        }).ToList();
        return new DadMeasuredPilotCampaign
        {
            State = DadMeasuredPilotState.Active,
            StoppedAtUtc = DateTime.UtcNow,
            Runs = runs,
            StopAllVerified = true,
            RecoveryRunVerified = true,
            DiscordReconnectCycleVerified = true,
            RevokeExclusionVerified = true,
            RePairVerified = true,
        };
    }

    private static DadMeasuredPilotRunEvidence QualifyingRun(string id, DadMeasuredPilotOrigin origin) => new()
    {
        RunId = id,
        Origin = origin,
        Terminal = true,
        Successful = true,
        ParticipantCount = 2,
        HealthyApplicationIds = [10, 20],
        FormationVerified = true,
        ReadinessBeforeQueueVerified = true,
        LeaseCleanupVerified = true,
        ClaimCleanupVerified = true,
        SchedulerCleanupVerified = true,
        ProfileRestoration = "not-applicable",
    };
}
