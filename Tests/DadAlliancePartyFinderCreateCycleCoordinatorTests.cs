using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAlliancePartyFinderCreateCycleCoordinatorTests
{
    [Fact]
    public void OneClickCanRecoverOnceThenReachListingOpenAndEnableGrab()
    {
        var coordinator = new DadAlliancePartyFinderCreateCycleCoordinator();

        var restart = coordinator.Observe(
            DadAlliancePfCreateCycleOutcome.Blocked,
            activeRecruitment: false);
        var complete = coordinator.Observe(
            DadAlliancePfCreateCycleOutcome.Succeeded,
            activeRecruitment: true);
        var status = new DadAlliancePartyFinderStatus
        {
            State = DadAllianceRecruitmentState.ListingOpen,
            OwnsRecruitment = true,
        };

        Assert.Equal(
            DadAlliancePfCreateCycleDecision.RestartOnce,
            restart);
        Assert.Equal(2, coordinator.Cycle);
        Assert.True(coordinator.RecoveryUsed);
        Assert.Equal(DadAlliancePfCreateCycleDecision.Complete, complete);
        Assert.True(DadAlliancePartyFinderRules.CanGrabDads(status));
    }

    [Fact]
    public void FirstCycleSuccessNeverStartsRecovery()
    {
        var coordinator = new DadAlliancePartyFinderCreateCycleCoordinator();

        var decision = coordinator.Observe(
            DadAlliancePfCreateCycleOutcome.Succeeded,
            activeRecruitment: true);

        Assert.Equal(DadAlliancePfCreateCycleDecision.Complete, decision);
        Assert.Equal(1, coordinator.Cycle);
        Assert.False(coordinator.RecoveryUsed);
    }

    [Fact]
    public void StopCancelsAutomaticRecovery()
    {
        var coordinator = new DadAlliancePartyFinderCreateCycleCoordinator();
        coordinator.Stop();

        var decision = coordinator.Observe(
            DadAlliancePfCreateCycleOutcome.Blocked,
            activeRecruitment: false);

        Assert.Equal(
            DadAlliancePfCreateCycleDecision.RemainBlocked,
            decision);
        Assert.Equal(1, coordinator.Cycle);
    }

    [Fact]
    public void PreExistingCondition66NeverStartsRecovery()
    {
        var coordinator = new DadAlliancePartyFinderCreateCycleCoordinator();

        var decision = coordinator.Observe(
            DadAlliancePfCreateCycleOutcome.Blocked,
            activeRecruitment: true);

        Assert.Equal(
            DadAlliancePfCreateCycleDecision.RemainBlocked,
            decision);
        Assert.Equal(1, coordinator.Cycle);
    }

    [Fact]
    public void SecondCycleFailureNeverStartsThirdCycle()
    {
        var coordinator = new DadAlliancePartyFinderCreateCycleCoordinator();

        Assert.Equal(
            DadAlliancePfCreateCycleDecision.RestartOnce,
            coordinator.Observe(
                DadAlliancePfCreateCycleOutcome.Blocked,
                activeRecruitment: false));
        var secondFailure = coordinator.Observe(
            DadAlliancePfCreateCycleOutcome.Blocked,
            activeRecruitment: false);

        Assert.Equal(
            DadAlliancePfCreateCycleDecision.RemainBlocked,
            secondFailure);
        Assert.Equal(2, coordinator.Cycle);
    }

    [Fact]
    public void ManualSecondCreateGeneratesAnotherFreshPasscode()
    {
        var values = new Queue<int>([1234, 5678]);
        var coordinator =
            new DadAlliancePartyFinderCreateCycleCoordinator(
                values.Dequeue);

        var second = coordinator.GenerateFreshPasscode(1234);

        Assert.Equal(5678, second);
        Assert.Empty(values);
    }

    [Fact]
    public void FreshPasscodeFallsBackIfRandomSourceRepeats()
    {
        var coordinator =
            new DadAlliancePartyFinderCreateCycleCoordinator(() => 9999);

        var fresh = coordinator.GenerateFreshPasscode(9999);

        Assert.Equal(1000, fresh);
    }
}
