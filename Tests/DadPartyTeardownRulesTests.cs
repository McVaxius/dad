using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadPartyTeardownRulesTests
{
    [Fact]
    public void CrossWorldLocalOnlyFrameCannotCompleteTeardown()
    {
        var now = DateTime.UtcNow;
        var controller = new DadPartyTeardownController([1UL, 2UL], 1, now, false, string.Empty);

        var breakup = controller.Pulse(Observation(now, members: [1UL], crossRealm: true));
        var leave = controller.Pulse(Observation(now.AddMilliseconds(100), members: [1UL], crossRealm: true, partyMenu: true));
        var beforePrompt = controller.Pulse(Observation(now.AddSeconds(1), members: [1UL], crossRealm: true));
        var approve = controller.Pulse(Observation(
            now.AddSeconds(2),
            members: [1UL, 2UL],
            crossRealm: true,
            prompt: true,
            identity: "fresh",
            text: "Disband the party?",
            worldStable: false));
        var crossRealmLocalOnly = controller.Pulse(Observation(now.AddSeconds(3), members: [1UL], crossRealm: true));
        var firstAbsentFrame = controller.Pulse(Observation(now.AddSeconds(4), members: [1UL]));
        var complete = controller.Pulse(Observation(now.AddSeconds(5), members: [1UL]));

        Assert.Equal(DadPartyTeardownAction.SendBreakup, breakup.Action);
        Assert.Equal(DadPartyTeardownAction.InvokePartyMenuLeave, leave.Action);
        Assert.Equal(DadPartyTeardownAction.None, beforePrompt.Action);
        Assert.Equal(DadPartyTeardownAction.ApprovePrompt, approve.Action);
        Assert.Equal(DadPartyTeardownAction.None, crossRealmLocalOnly.Action);
        Assert.Equal(DadPartyTeardownAction.None, firstAbsentFrame.Action);
        Assert.Equal(DadPartyTeardownAction.Complete, complete.Action);
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
                text: "Disband the party?",
                worldStable: false)).Action);
        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now.AddSeconds(2))).Action);
        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now.AddSeconds(3), members: [1UL])).Action);
        Assert.Equal(
            DadPartyTeardownAction.Complete,
            controller.Pulse(Observation(now.AddSeconds(4), members: [1UL])).Action);
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
    public void OutOfDutyLeaderSendsBreakupWithoutASeparateWorldStabilityGate()
    {
        var now = DateTime.UtcNow;
        var controller = new DadPartyTeardownController([1UL, 2UL], 1, now, false, string.Empty);

        Assert.Equal(
            DadPartyTeardownAction.SendBreakup,
            controller.Pulse(Observation(now, worldStable: false)).Action);
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

    [Fact]
    public void BreakupUsesTheExactFullChatCommand()
    {
        Assert.Equal("/partycmd breakup", DadPartyTeardownController.BreakupCommand);
        Assert.Equal(2, DadPartyTeardownController.PartyMenuLeaveCallbackOperation);
        Assert.Equal(3, DadPartyTeardownController.PartyMenuLeaveCallbackArgument);
    }

    [Fact]
    public void CrossWorldPartyMenuCallbackFiresOncePerCommandAttempt()
    {
        var now = DateTime.UtcNow;
        var controller = new DadPartyTeardownController([1UL, 2UL], 1, now, false, string.Empty);

        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now, crossRealm: true)).Action);
        Assert.Equal(DadPartyTeardownAction.InvokePartyMenuLeave, controller.Pulse(Observation(now.AddMilliseconds(100), crossRealm: true, partyMenu: true)).Action);
        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now.AddMilliseconds(200), crossRealm: true, partyMenu: true)).Action);
        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now.AddSeconds(10), crossRealm: true, partyMenu: true)).Action);
        Assert.Equal(DadPartyTeardownAction.InvokePartyMenuLeave, controller.Pulse(Observation(now.AddSeconds(10.1), crossRealm: true, partyMenu: true)).Action);
    }

    private static DadPartyTeardownObservation Observation(
        DateTime now,
        IReadOnlyCollection<ulong>? members = null,
        bool crossRealm = false,
        bool partyMenu = false,
        bool prompt = false,
        string identity = "",
        string text = "",
        bool worldStable = true)
        => new(
            now,
            LocalContentId: 1,
            PartyLeaderContentId: 1,
            PartyMemberContentIds: members ?? [1UL, 2UL],
            IsCrossRealmParty: crossRealm,
            IsInDuty: false,
            IsQueued: false,
            IsWorldStable: worldStable,
            PartyMenuVisible: partyMenu,
            PromptVisible: prompt,
            PromptIdentity: identity,
            PromptText: text,
            InviterName: "Leader");
}
