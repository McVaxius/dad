using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadParticipantPartyDepartureRulesTests
{
    [Fact]
    public void SoloOrAlreadyWithExpectedInviterCompletesWithoutMutation()
    {
        var now = DateTime.UtcNow;
        var solo = new DadParticipantPartyDepartureController(2, now, false);
        var expectedParty = new DadParticipantPartyDepartureController(2, now, false);

        Assert.Equal(
            DadParticipantPartyDepartureAction.Complete,
            solo.Pulse(Observation(now, members: [1UL])).Action);
        Assert.Equal(
            DadParticipantPartyDepartureAction.Complete,
            expectedParty.Pulse(Observation(now, members: [1UL, 2UL])).Action);
        Assert.Equal(0, solo.CommandAttempts);
        Assert.Equal(0, expectedParty.CommandAttempts);
    }

    [Fact]
    public void SameWorldWrongPartyLeavesApprovesFreshPromptAndRequiresSustainedSolo()
    {
        var now = DateTime.UtcNow;
        var controller = new DadParticipantPartyDepartureController(2, now, false);

        Assert.Equal(DadParticipantPartyDepartureAction.SendLeave, controller.Pulse(Observation(now)).Action);
        Assert.Equal(
            DadParticipantPartyDepartureAction.ApprovePrompt,
            controller.Pulse(Observation(now.AddMilliseconds(100), prompt: true, promptIdentity: "leave-1")).Action);
        Assert.Equal(DadParticipantPartyDepartureAction.None, controller.Pulse(Observation(now.AddSeconds(1), members: [1UL])).Action);
        Assert.Equal(DadParticipantPartyDepartureAction.Complete, controller.Pulse(Observation(now.AddSeconds(2), members: [1UL])).Action);
    }

    [Fact]
    public void PreExistingPromptIsNeverApproved()
    {
        var now = DateTime.UtcNow;
        var controller = new DadParticipantPartyDepartureController(2, now, true);

        Assert.Equal(
            DadParticipantPartyDepartureAction.None,
            controller.Pulse(Observation(now, prompt: true, promptIdentity: "unrelated")).Action);
        Assert.Equal(DadParticipantPartyDepartureAction.SendLeave, controller.Pulse(Observation(now.AddSeconds(1))).Action);
        Assert.Equal(
            DadParticipantPartyDepartureAction.ApprovePrompt,
            controller.Pulse(Observation(now.AddSeconds(2), prompt: true, promptIdentity: "leave-1")).Action);
    }

    [Fact]
    public void CrossWorldWrongPartyUsesPartyMemberListCallbackOncePerAttempt()
    {
        var now = DateTime.UtcNow;
        var controller = new DadParticipantPartyDepartureController(2, now, false);

        Assert.Equal(DadParticipantPartyDepartureAction.SendLeave, controller.Pulse(Observation(now, crossRealm: true)).Action);
        Assert.Equal(
            DadParticipantPartyDepartureAction.InvokePartyMenuLeave,
            controller.Pulse(Observation(now.AddMilliseconds(100), crossRealm: true, partyMenu: true)).Action);
        Assert.Equal(
            DadParticipantPartyDepartureAction.None,
            controller.Pulse(Observation(now.AddMilliseconds(200), crossRealm: true, partyMenu: true)).Action);
        Assert.Equal(DadParticipantPartyDepartureAction.SendLeave, controller.Pulse(Observation(now.AddSeconds(8), crossRealm: true)).Action);
        Assert.Equal(
            DadParticipantPartyDepartureAction.InvokePartyMenuLeave,
            controller.Pulse(Observation(now.AddSeconds(8.1), crossRealm: true, partyMenu: true)).Action);
    }

    [Fact]
    public void UnsafeWorldDutyAndQueueStatesNeverMutate()
    {
        var now = DateTime.UtcNow;
        var controller = new DadParticipantPartyDepartureController(2, now, false);

        Assert.Equal(DadParticipantPartyDepartureAction.None, controller.Pulse(Observation(now, worldStable: false)).Action);
        Assert.Equal(DadParticipantPartyDepartureAction.None, controller.Pulse(Observation(now.AddSeconds(1), inDuty: true)).Action);
        Assert.Equal(DadParticipantPartyDepartureAction.None, controller.Pulse(Observation(now.AddSeconds(2), queued: true)).Action);
        Assert.Equal(0, controller.CommandAttempts);
    }

    [Fact]
    public void GuardedDepartureUsesSevenAttemptsEightSecondsAndSixtySecondTimeout()
    {
        var now = DateTime.UtcNow;
        var controller = new DadParticipantPartyDepartureController(2, now, false);

        Assert.Equal(DadPartyTeardownController.MaximumAttempts, DadParticipantPartyDepartureController.MaximumAttempts);
        Assert.Equal(DadPartyTeardownController.AttemptThrottle, DadParticipantPartyDepartureController.AttemptThrottle);
        Assert.Equal(DadPartyTeardownController.Timeout, DadParticipantPartyDepartureController.Timeout);
        foreach (var seconds in new[] { 0, 8, 16, 24, 32, 40, 48 })
            Assert.Equal(DadParticipantPartyDepartureAction.SendLeave, controller.Pulse(Observation(now.AddSeconds(seconds))).Action);

        Assert.Equal(DadParticipantPartyDepartureAction.None, controller.Pulse(Observation(now.AddSeconds(56))).Action);
        var timeout = controller.Pulse(Observation(now.AddSeconds(60)));
        Assert.Equal(DadParticipantPartyDepartureAction.Fail, timeout.Action);
        Assert.Contains("7 leave command attempt(s)", timeout.Summary, StringComparison.Ordinal);
        Assert.Equal("/partycmd leave", DadParticipantPartyDepartureController.LeaveCommand);
        Assert.Equal(2, DadParticipantPartyDepartureController.PartyMenuLeaveCallbackOperation);
        Assert.Equal(3, DadParticipantPartyDepartureController.PartyMenuLeaveCallbackArgument);
    }

    [Fact]
    public void ExpectedInviterIdentityDriftFailsClosed()
    {
        var now = DateTime.UtcNow;
        var controller = new DadParticipantPartyDepartureController(2, now, false);

        var decision = controller.Pulse(Observation(now, expectedInviter: 3));

        Assert.Equal(DadParticipantPartyDepartureAction.Fail, decision.Action);
        Assert.Contains("changed", decision.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, controller.CommandAttempts);
    }

    private static DadParticipantPartyDepartureObservation Observation(
        DateTime now,
        IReadOnlyCollection<ulong>? members = null,
        ulong expectedInviter = 2,
        bool crossRealm = false,
        bool partyMenu = false,
        bool prompt = false,
        string promptIdentity = "",
        bool worldStable = true,
        bool inDuty = false,
        bool queued = false)
        => new(
            now,
            LocalContentId: 1,
            ExpectedInviterContentId: expectedInviter,
            PartyMemberContentIds: members ?? [1UL, 3UL],
            IsCrossRealmParty: crossRealm,
            IsInDuty: inDuty,
            IsQueued: queued,
            IsWorldStable: worldStable,
            PartyMenuVisible: partyMenu,
            PromptVisible: prompt,
            PromptIdentity: promptIdentity,
            PromptText: prompt ? "Leave the party?" : string.Empty);
}
