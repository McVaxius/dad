using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadP1178SafetyRulesTests
{
    [Fact]
    public void RegularJoinAllowsOnlyFingerprintAndGenerationDrift()
    {
        var target = new DadDutyFinderLiveTarget(DadDutyFinderLiveContentType.Regular, 55);
        var selected = new DadDutyFinderSelectionToken(target, 100, "old-fingerprint:G1", 7, 3);
        var mapping = Ready(target, 100, "new-fingerprint:G2", 7, 3);
        Assert.True(DadDutyFinderMappedMutationRules.CanJoinRegularAfterSelection(
            mapping, selected, DadDutyFinderLiveContentType.Regular, 55, 55, target));
        Assert.False(DadDutyFinderMappedMutationRules.CanJoin(mapping, selected, DadDutyFinderLiveContentType.Regular, 55, target));

        var wrongTarget = new DadDutyFinderLiveTarget(DadDutyFinderLiveContentType.Regular, 56);
        Assert.False(DadDutyFinderMappedMutationRules.CanJoinRegularAfterSelection(Ready(wrongTarget, 100, "new:G2", 7, 3), selected, DadDutyFinderLiveContentType.Regular, 55, 55, target));
        Assert.False(DadDutyFinderMappedMutationRules.CanJoinRegularAfterSelection(Ready(target, 101, "new", 7, 3), selected, DadDutyFinderLiveContentType.Regular, 55, 55, target));
        Assert.False(DadDutyFinderMappedMutationRules.CanJoinRegularAfterSelection(Ready(target, 100, "new", 8, 3), selected, DadDutyFinderLiveContentType.Regular, 55, 55, target));
        Assert.False(DadDutyFinderMappedMutationRules.CanJoinRegularAfterSelection(Ready(target, 100, "new", 7, 4), selected, DadDutyFinderLiveContentType.Regular, 55, 55, target));
        Assert.False(DadDutyFinderMappedMutationRules.CanJoinRegularAfterSelection(mapping, selected, DadDutyFinderLiveContentType.Roulette, 55, 55, target));
        Assert.False(DadDutyFinderMappedMutationRules.CanJoinRegularAfterSelection(mapping, selected, DadDutyFinderLiveContentType.Regular, 56, 55, target));
        Assert.False(DadDutyFinderMappedMutationRules.CanJoinRegularAfterSelection(mapping, selected, DadDutyFinderLiveContentType.Regular, 55, 99, target));
    }

    [Fact]
    public void RegressionAnchorSequenceClearsOnceSelectsExactRowOnceThenJoinsOnce()
    {
        var target = new DadDutyFinderLiveTarget(DadDutyFinderLiveContentType.Regular, 55);
        var beforeSelection = Snapshot(selectedRadioButton: 0);
        var afterSelection = Snapshot(selectedRadioButton: 1);
        var gate = new DadDutyFinderStableMappingGate();
        var callbacks = new List<string> { "Clear:12:1" };

        Assert.Equal(DadDutyFinderMappingStatus.AwaitingStableSnapshot, gate.Observe(beforeSelection, target).Status);
        var readyToSelect = gate.Observe(beforeSelection, target);
        Assert.True(DadDutyFinderMappedMutationRules.ShouldSelect(readyToSelect, null));
        var selected = readyToSelect.Entry!.SelectionToken;
        callbacks.Add($"Select:3:{readyToSelect.Entry.UiRow.CallbackOrdinal}");

        var changedFingerprint = gate.Observe(afterSelection, target);
        Assert.True(DadDutyFinderMappedMutationRules.ShouldAwaitRegularPostSelectionMapping(changedFingerprint, selected));
        Assert.Equal(DadDutyFinderMappingStatus.AwaitingStableSnapshot, changedFingerprint.Status);

        var readyToJoin = gate.Observe(afterSelection, target);
        Assert.True(DadDutyFinderMappedMutationRules.CanJoinRegularAfterSelection(
            readyToJoin,
            selected,
            DadDutyFinderLiveContentType.Regular,
            selectedAgentId: 55,
            interfaceSelectedId: 55,
            target));
        callbacks.Add("Join:12:0");

        Assert.Equal(["Clear:12:1", "Select:3:2", "Join:12:0"], callbacks);
        Assert.Single(callbacks, callback => callback.StartsWith("Clear", StringComparison.Ordinal));
        Assert.Single(callbacks, callback => callback.StartsWith("Select", StringComparison.Ordinal));
        Assert.Single(callbacks, callback => callback.StartsWith("Join", StringComparison.Ordinal));
        Assert.DoesNotContain(callbacks, callback => callback.Contains("Hydrate", StringComparison.OrdinalIgnoreCase) || callback.Contains("Reset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TeardownRequiresExactLeaderRosterAndFreshPostCommandPrompt()
    {
        var now = DateTime.UtcNow;
        var controller = new DadPartyTeardownController([1UL, 2UL], 1, now, promptVisible: true, promptIdentity: "old");
        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now, prompt: true, identity: "old")).Action);
        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now.AddSeconds(1))).Action);
        Assert.Equal(DadPartyTeardownAction.ApprovePrompt, controller.Pulse(Observation(now.AddSeconds(2), prompt: true, identity: "new", text: "Disband the party?")).Action);
        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now.AddSeconds(3), prompt: true, identity: "new", text: "Disband the party?")).Action);
        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now.AddSeconds(4), members: [1UL])).Action);
        Assert.Equal(DadPartyTeardownAction.Complete, controller.Pulse(Observation(now.AddSeconds(5), members: [1UL])).Action);
    }

    [Fact]
    public void TeardownFailsOnUnexpectedMemberLostLeadershipAndTimeout()
    {
        var now = DateTime.UtcNow;
        var unexpected = new DadPartyTeardownController([1UL, 2UL], 1, now, false, string.Empty);
        Assert.Equal(DadPartyTeardownAction.Fail, unexpected.Pulse(Observation(now, members: [1UL, 3UL])).Action);

        var lostLeader = new DadPartyTeardownController([1UL, 2UL], 1, now, false, string.Empty);
        Assert.Equal(DadPartyTeardownAction.Fail, lostLeader.Pulse(Observation(now, leader: 2)).Action);

        var timeout = new DadPartyTeardownController([1UL, 2UL], 1, now, false, string.Empty);
        Assert.Equal(DadPartyTeardownAction.Fail, timeout.Pulse(Observation(now.AddSeconds(60))).Action);
    }

    [Fact]
    public void TeardownSendsAtMostThreeThrottledCommands()
    {
        var now = DateTime.UtcNow;
        var controller = new DadPartyTeardownController([1UL, 2UL], 1, now, false, string.Empty);
        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now)).Action);
        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now.AddSeconds(5))).Action);
        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now.AddSeconds(10))).Action);
        Assert.Equal(DadPartyTeardownAction.SendBreakup, controller.Pulse(Observation(now.AddSeconds(20))).Action);
        Assert.Equal(DadPartyTeardownAction.None, controller.Pulse(Observation(now.AddSeconds(30))).Action);
        Assert.Equal(3, controller.CommandAttempts);
    }

    private static DadDutyFinderMappingResult Ready(DadDutyFinderLiveTarget target, ulong character, string fingerprint, int tree, int ordinal)
    {
        var token = new DadDutyFinderSelectionToken(target, character, fingerprint, tree, ordinal);
        return new DadDutyFinderMappingResult(
            DadDutyFinderMappingStatus.Ready,
            string.Empty,
            new DadDutyFinderResolvedEntry(
                new DadDutyFinderContentEntry(0, target.ContentType, target.RowId, "Duty"),
                new DadDutyFinderUiRow(tree, ordinal, true, "Duty", string.Empty),
                token));
    }

    private static DadDutyFinderListSnapshot Snapshot(uint selectedRadioButton)
        => new()
        {
            CharacterContentId = 100,
            AddonIdentity = 11,
            AgentIdentity = 22,
            DutyListIdentity = 33,
            ContentListStorageIdentity = 44,
            DutyListStorageIdentity = 55,
            SelectedTab = 1,
            SelectedRadioButton = selectedRadioButton,
            DeclaredEntryCount = 2,
            TreeItemCount = 2,
            ContentEntries =
            [
                new DadDutyFinderContentEntry(0, DadDutyFinderLiveContentType.Regular, 44, "First Duty"),
                new DadDutyFinderContentEntry(1, DadDutyFinderLiveContentType.Regular, 55, "Exact Duty"),
            ],
            TreeItems =
            [
                new DadDutyFinderTreeItem(0, true, true, "First Duty"),
                new DadDutyFinderTreeItem(1, true, true, "Exact Duty"),
            ],
        };

    private static DadPartyTeardownObservation Observation(
        DateTime now,
        IReadOnlyCollection<ulong>? members = null,
        ulong leader = 1,
        bool prompt = false,
        string identity = "",
        string text = "")
        => new(now, 1, leader, members ?? [1UL, 2UL], false, false, false, true, false, prompt, identity, text, "Leader");
}
