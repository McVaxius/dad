using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAlliancePartyFinderCreatePreflightTests
{
    [Fact]
    public void CoordinatorWithAllPrerequisitesIsReady()
    {
        var decision = DadAlliancePartyFinderCreatePreflight.Evaluate(ReadyInput());

        Assert.True(decision.Ready);
        Assert.Empty(decision.Blocker);
    }

    [Fact]
    public void WorkerCannotImpersonateCoordinatorAndExactBlockerRemainsVisible()
    {
        const string blocker = "The alliance PF creator must be the active Dad Coordinator.";
        var decision = DadAlliancePartyFinderCreatePreflight.Evaluate(
            ReadyInput() with
            {
                OperationalBlocker = blocker,
            });

        Assert.False(decision.Ready);
        Assert.Equal(blocker, decision.Blocker);
    }

    [Fact]
    public void OtherOperationalPrerequisiteControlsReadiness()
    {
        const string blocker = "Wait for the DAD Coordinator hub to become ready.";
        var decision = DadAlliancePartyFinderCreatePreflight.Evaluate(
            ReadyInput() with
            {
                OperationalBlocker = blocker,
            });

        Assert.False(decision.Ready);
        Assert.Equal(blocker, decision.Blocker);
    }

    [Fact]
    public void ActiveRecruitmentBlocksBeforeAnotherCreate()
    {
        var decision = DadAlliancePartyFinderCreatePreflight.Evaluate(
            ReadyInput() with
            {
                RecruitmentActive = true,
                OperationalBlocker = "A DAD run is already active.",
            });

        Assert.False(decision.Ready);
        Assert.Equal(
            DadAlliancePartyFinderCreatePreflight.ActiveRecruitmentBlocker,
            decision.Blocker);
    }

    [Fact]
    public void TargetAndHostPrerequisitesRemainExact()
    {
        const string targetBlocker =
            "slot-a exact character is not online, world-ready, and visible through the authenticated DAD hub.";
        var target = DadAlliancePartyFinderCreatePreflight.Evaluate(
            ReadyInput() with
            {
                TargetsResolved = false,
                TargetBlocker = targetBlocker,
            });
        var host = DadAlliancePartyFinderCreatePreflight.Evaluate(
            ReadyInput() with
            {
                HostIsAllianceA = false,
            });

        Assert.False(target.Ready);
        Assert.Equal(targetBlocker, target.Blocker);
        Assert.False(host.Ready);
        Assert.Equal(DadAlliancePartyFinderCreatePreflight.HostBlocker, host.Blocker);
    }

    [Fact]
    public void RejectedCreateStatusIsRetainedInsteadOfReplacedByPreview()
    {
        var live = new DadAlliancePartyFinderStatus
        {
            State = DadAllianceRecruitmentState.Blocked,
            CreateRejected = true,
            CreatePreflightBlocker = "The alliance PF creator must be the active Dad Coordinator.",
            Summary = "The alliance PF creator must be the active Dad Coordinator.",
        };
        var preview = new DadAlliancePartyFinderStatus
        {
            State = DadAllianceRecruitmentState.Idle,
            CreatePreflightReady = true,
            Summary = "Ready.",
        };

        var display = DadAlliancePartyFinderCreatePreflight.SelectLocalDisplay(live, preview);
        var clone = display.Clone();

        Assert.Same(live, display);
        Assert.True(clone.CreateRejected);
        Assert.Equal(live.CreatePreflightBlocker, clone.CreatePreflightBlocker);
        Assert.Equal(live.Summary, clone.Summary);
    }

    private static DadAlliancePfCreatePreflightInput ReadyInput()
        => new()
        {
            HasConcretePreset = true,
            Validation = new DadAlliancePresetValidation
            {
                AllianceACount = 1,
                AllianceBCount = 1,
                AllianceCCount = 1,
                Summary = "Ready: A 1, B 1, C 1, total 3.",
            },
            TargetsResolved = true,
            HostIsAllianceA = true,
        };
}
