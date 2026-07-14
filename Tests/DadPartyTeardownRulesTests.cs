using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadPartyTeardownRulesTests
{
    [Fact]
    public void TransientEmptyPartyListCannotReportSuccessfulTeardown()
    {
        var now = DateTime.UtcNow;
        var controller = new DadPartyTeardownController([1UL, 2UL], 1, now, false, string.Empty);

        var transientEmpty = controller.Pulse(Observation(now, members: []));
        var restoredParty = controller.Pulse(Observation(now.AddSeconds(1), members: [1UL, 2UL]));

        Assert.Equal(DadPartyTeardownAction.None, transientEmpty.Action);
        Assert.Contains("temporarily reported solo", transientEmpty.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DadPartyTeardownAction.SendBreakup, restoredParty.Action);
        Assert.Equal(1, controller.CommandAttempts);
    }

    [Fact]
    public void LeaderBreakupPromptWithoutInviterNameIsApprovedThenWaitsForPartyListSolo()
    {
        var now = DateTime.UtcNow;
        var controller = new DadPartyTeardownController([1UL, 2UL], 1, now, false, string.Empty);

        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now)).Action);
        Assert.Equal(
            DadPartyTeardownAction.ApprovePrompt,
            controller.Pulse(Observation(
                now.AddSeconds(1),
                prompt: true,
                identity: "fresh",
                text: "Disband the party?")).Action);
        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now.AddSeconds(2))).Action);
        Assert.Equal(
            DadPartyTeardownAction.Complete,
            controller.Pulse(Observation(now.AddSeconds(3), members: [1UL])).Action);
    }

    [Fact]
    public void PreExistingPromptMustDisappearBeforePostCommandPromptCanBeApproved()
    {
        var now = DateTime.UtcNow;
        var controller = new DadPartyTeardownController([1UL, 2UL], 1, now, true, "pre-existing");

        Assert.Equal(
            DadPartyTeardownAction.None,
            controller.Pulse(Observation(now, prompt: true, identity: "pre-existing", text: "Unrelated confirmation")).Action);
        Assert.Equal(
            DadPartyTeardownAction.SendBreakup,
            controller.Pulse(Observation(now.AddSeconds(1))).Action);
        Assert.Equal(
            DadPartyTeardownAction.ApprovePrompt,
            controller.Pulse(Observation(now.AddSeconds(2), prompt: true, identity: "breakup", text: "Disband the party?")).Action);
    }

    [Fact]
    public void BreakupRetriesRemainThrottledAndCappedUntilTimeout()
    {
        var now = DateTime.UtcNow;
        var controller = new DadPartyTeardownController([1UL, 2UL], 1, now, false, string.Empty);

        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now)).Action);
        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now.AddSeconds(9))).Action);
        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now.AddSeconds(10))).Action);
        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now.AddSeconds(20))).Action);
        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now.AddSeconds(30))).Action);
        Assert.Equal(DadPartyTeardownAction.Fail, controller.Pulse(Observation(now.AddSeconds(60))).Action);
        Assert.Equal(DadPartyTeardownController.MaximumAttempts, controller.CommandAttempts);
    }

    [Fact]
    public void SoloPresetRosterCanCompleteWithoutAFalseMultiMemberGate()
    {
        var now = DateTime.UtcNow;
        var controller = new DadPartyTeardownController([1UL], 1, now, false, string.Empty);

        Assert.Equal(
            DadPartyTeardownAction.Complete,
            controller.Pulse(Observation(now, members: [])).Action);
    }

    private static DadPartyTeardownObservation Observation(
        DateTime now,
        IReadOnlyCollection<ulong>? members = null,
        bool prompt = false,
        string identity = "",
        string text = "")
        => new(
            now,
            LocalContentId: 1,
            PartyLeaderContentId: 1,
            PartyMemberContentIds: members ?? [1UL, 2UL],
            IsInDuty: false,
            IsQueued: false,
            IsWorldStable: true,
            PromptVisible: prompt,
            PromptIdentity: identity,
            PromptText: text,
            InviterName: "Leader");
}
