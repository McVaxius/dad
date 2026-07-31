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

        Assert.Equal(TimeSpan.FromSeconds(8), DadPartyTeardownController.AttemptThrottle);
        Assert.Equal(7, DadPartyTeardownController.MaximumAttempts);
        foreach (var attemptSecond in new[] { 0, 8, 16, 24, 32, 40, 48 })
        {
            Assert.Equal(
                DadPartyTeardownAction.SendBreakup,
                controller.Pulse(Observation(now.AddSeconds(attemptSecond))).Action);
        }

        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now.AddSeconds(56))).Action);
        var timeout = controller.Pulse(Observation(now.AddSeconds(60)));
        Assert.Equal(DadPartyTeardownAction.Fail, timeout.Action);
        Assert.Contains("7 breakup command attempt(s)", timeout.Summary, StringComparison.Ordinal);
        Assert.Equal(DadPartyTeardownController.MaximumAttempts, controller.CommandAttempts);
    }

    [Fact]
    public void ApprovedButIneffectiveAttemptCanRetryWithFreshCallbackAndPrompt()
    {
        var now = DateTime.UtcNow;
        var controller = new DadPartyTeardownController([1UL, 2UL], 1, now, false, string.Empty);

        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now, crossRealm: true)).Action);
        Assert.Equal(
            DadPartyTeardownAction.InvokePartyMenuLeave,
            controller.Pulse(Observation(now.AddMilliseconds(100), crossRealm: true, partyMenu: true)).Action);
        Assert.Equal(
            DadPartyTeardownAction.ApprovePrompt,
            controller.Pulse(Observation(now.AddSeconds(1), crossRealm: true, prompt: true, identity: "attempt-1")).Action);
        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now.AddSeconds(2), crossRealm: true)).Action);
        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now.AddSeconds(7.9), crossRealm: true)).Action);

        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now.AddSeconds(8), crossRealm: true)).Action);
        Assert.Equal(
            DadPartyTeardownAction.InvokePartyMenuLeave,
            controller.Pulse(Observation(now.AddSeconds(8.1), crossRealm: true, partyMenu: true)).Action);
        Assert.Equal(
            DadPartyTeardownAction.None,
            controller.Pulse(Observation(now.AddSeconds(8.2), crossRealm: true, partyMenu: true)).Action);
        Assert.Equal(
            DadPartyTeardownAction.ApprovePrompt,
            controller.Pulse(Observation(now.AddSeconds(9), crossRealm: true, prompt: true, identity: "attempt-2")).Action);

        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now.AddSeconds(10), members: [1UL])).Action);
        Assert.Equal(DadPartyTeardownAction.Complete, controller.Pulse(Observation(now.AddSeconds(11), members: [1UL])).Action);
        Assert.Equal(2, controller.CommandAttempts);
    }

    [Fact]
    public void UnsafeOrUnresolvedStatePreventsTheNextMutationAfterApproval()
    {
        var now = DateTime.UtcNow;

        var lingeringPrompt = ApprovedAttemptOne(now);
        Assert.Equal(
            DadPartyTeardownAction.None,
            lingeringPrompt.Pulse(Observation(now.AddSeconds(8), prompt: true, identity: "attempt-1")).Action);
        Assert.Equal(1, lingeringPrompt.CommandAttempts);
        Assert.Equal(DadPartyTeardownAction.SendBreakup, lingeringPrompt.Pulse(Observation(now.AddSeconds(9))).Action);

        var dutyOrQueue = ApprovedAttemptOne(now);
        Assert.Equal(DadPartyTeardownAction.None, dutyOrQueue.Pulse(Observation(now.AddSeconds(8), inDuty: true)).Action);
        Assert.Equal(DadPartyTeardownAction.None, dutyOrQueue.Pulse(Observation(now.AddSeconds(9), queued: true)).Action);
        Assert.Equal(1, dutyOrQueue.CommandAttempts);
        Assert.Equal(DadPartyTeardownAction.SendBreakup, dutyOrQueue.Pulse(Observation(now.AddSeconds(10))).Action);

        var unexpectedMember = ApprovedAttemptOne(now);
        Assert.Equal(
            DadPartyTeardownAction.Fail,
            unexpectedMember.Pulse(Observation(now.AddSeconds(8), members: [1UL, 3UL], crossRealm: true, partyMenu: true)).Action);
        Assert.Equal(1, unexpectedMember.CommandAttempts);

        var lostLeadership = ApprovedAttemptOne(now);
        Assert.Equal(
            DadPartyTeardownAction.Fail,
            lostLeadership.Pulse(Observation(now.AddSeconds(8), leader: 2, crossRealm: true, partyMenu: true)).Action);
        Assert.Equal(1, lostLeadership.CommandAttempts);
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
        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now.AddSeconds(8), crossRealm: true, partyMenu: true)).Action);
        Assert.Equal(DadPartyTeardownAction.InvokePartyMenuLeave, controller.Pulse(Observation(now.AddSeconds(8.1), crossRealm: true, partyMenu: true)).Action);
    }

    [Fact]
    public void FollowerAlreadyAuthoritativelySoloCompletesWithoutMutation()
    {
        var now = DateTime.UtcNow;
        var controller = FollowerController(now);

        var complete = controller.Pulse(Observation(
            now,
            local: 2,
            leader: 0,
            members: [2UL]));

        Assert.Equal(DadPartyTeardownAction.Complete, complete.Action);
        Assert.Equal(0, controller.CommandAttempts);
    }

    [Fact]
    public void FollowerUsesGuardedLeaveAndFreshRelevantConfirmation()
    {
        var now = DateTime.UtcNow;
        var controller = FollowerController(now);

        var leave = controller.Pulse(Observation(now, local: 2));
        var approve = controller.Pulse(Observation(
            now.AddSeconds(1),
            local: 2,
            prompt: true,
            identity: "fresh-leave",
            text: "Leave the party?"));

        Assert.Equal(DadPartyTeardownAction.SendBreakup, leave.Action);
        Assert.Contains("leave command", leave.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DadPartyTeardownAction.ApprovePrompt, approve.Action);
        Assert.Equal("/partycmd leave", DadPartyTeardownController.LeaveCommand);
    }

    [Fact]
    public void ApprovedFollowerPromptMayRemainVisibleDuringSustainedSoloProof()
    {
        var now = DateTime.UtcNow;
        var controller = FollowerController(now);

        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now, local: 2)).Action);
        Assert.Equal(
            DadPartyTeardownAction.ApprovePrompt,
            controller.Pulse(Observation(
                now.AddSeconds(1),
                local: 2,
                prompt: true,
                identity: "fresh-leave",
                text: "Leave the party?")).Action);
        Assert.Equal(
            DadPartyTeardownAction.None,
            controller.Pulse(Observation(
                now.AddSeconds(2),
                local: 2,
                leader: 0,
                members: [2UL],
                prompt: true,
                identity: "fresh-leave",
                text: "Leave the party?")).Action);
        Assert.Equal(
            DadPartyTeardownAction.Complete,
            controller.Pulse(Observation(
                now.AddSeconds(3),
                local: 2,
                leader: 0,
                members: [2UL],
                prompt: true,
                identity: "fresh-leave",
                text: "Leave the party?")).Action);
    }

    [Fact]
    public void FollowerNeverApprovesAnUnrelatedFreshPrompt()
    {
        var now = DateTime.UtcNow;
        var controller = FollowerController(now);

        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now, local: 2)).Action);
        var unrelated = controller.Pulse(Observation(
            now.AddSeconds(1),
            local: 2,
            prompt: true,
            identity: "unrelated",
            text: "Commence the duty?"));

        Assert.Equal(DadPartyTeardownAction.None, unrelated.Action);
        Assert.Contains("will not be approved", unrelated.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FollowerRejectsLocalOrFrozenLeaderIdentityDrift()
    {
        var now = DateTime.UtcNow;
        var localDrift = FollowerController(now).Pulse(Observation(
            now,
            local: 3,
            members: [1UL, 2UL]));
        var leaderDrift = FollowerController(now).Pulse(Observation(
            now,
            local: 2,
            leader: 3,
            members: [1UL, 2UL]));

        Assert.Equal(DadPartyTeardownAction.Fail, localDrift.Action);
        Assert.Contains("local character", localDrift.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DadPartyTeardownAction.Fail, leaderDrift.Action);
        Assert.Contains("Slot1 leader", leaderDrift.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FollowerLeaveRetainsSevenAttemptsEightSecondThrottleAndSixtySecondTimeout()
    {
        var now = DateTime.UtcNow;
        var controller = FollowerController(now);

        Assert.Equal(TimeSpan.FromSeconds(8), DadPartyTeardownController.AttemptThrottle);
        Assert.Equal(TimeSpan.FromSeconds(60), DadPartyTeardownController.Timeout);
        Assert.Equal(7, DadPartyTeardownController.MaximumAttempts);
        foreach (var attemptSecond in new[] { 0, 8, 16, 24, 32, 40, 48 })
        {
            Assert.Equal(
                DadPartyTeardownAction.SendBreakup,
                controller.Pulse(Observation(now.AddSeconds(attemptSecond), local: 2)).Action);
        }

        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now.AddSeconds(56), local: 2)).Action);
        var timeout = controller.Pulse(Observation(now.AddSeconds(60), local: 2));
        Assert.Equal(DadPartyTeardownAction.Fail, timeout.Action);
        Assert.Contains("7 leave command attempt(s)", timeout.Summary, StringComparison.Ordinal);
    }

    private static DadPartyTeardownController ApprovedAttemptOne(DateTime now)
    {
        var controller = new DadPartyTeardownController([1UL, 2UL], 1, now, false, string.Empty);
        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now)).Action);
        Assert.Equal(
            DadPartyTeardownAction.ApprovePrompt,
            controller.Pulse(Observation(now.AddSeconds(1), prompt: true, identity: "attempt-1")).Action);
        return controller;
    }

    private static DadPartyTeardownController FollowerController(DateTime now)
        => new(
            [1UL, 2UL],
            expectedLeaderContentId: 1,
            expectedLocalContentId: 2,
            mutationMode: DadPartyTeardownMutationMode.LeaveAsFollower,
            startedAtUtc: now,
            promptVisible: false,
            promptIdentity: string.Empty);

    private static DadPartyTeardownObservation Observation(
        DateTime now,
        IReadOnlyCollection<ulong>? members = null,
        ulong local = 1,
        ulong leader = 1,
        bool crossRealm = false,
        bool partyMenu = false,
        bool prompt = false,
        string identity = "",
        string text = "",
        bool worldStable = true,
        bool inDuty = false,
        bool queued = false)
        => new(
            now,
            LocalContentId: local,
            PartyLeaderContentId: leader,
            PartyMemberContentIds: members ?? [1UL, 2UL],
            IsCrossRealmParty: crossRealm,
            IsInDuty: inDuty,
            IsQueued: queued,
            IsWorldStable: worldStable,
            PartyMenuVisible: partyMenu,
            PromptVisible: prompt,
            PromptIdentity: identity,
            PromptText: text,
            InviterName: "Leader");
}
