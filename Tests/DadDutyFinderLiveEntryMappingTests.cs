using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadDutyFinderLiveEntryMappingTests
{
    [Fact]
    public void RouletteRowIdThreeMapsToItsLiveOrdinalInsteadOfUsingTheRowIdAsAnIndex()
    {
        var target = Target(DadDutyFinderLiveContentType.Roulette, 3);
        var snapshot = Snapshot(
            Entries(
                (DadDutyFinderLiveContentType.Roulette, 8u, "Expert"),
                (DadDutyFinderLiveContentType.Roulette, 1u, "Leveling"),
                (DadDutyFinderLiveContentType.Roulette, 12u, "Trials"),
                (DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario")),
            [
                Header("Daily Roulettes"),
                Leaf("Expert"),
                Leaf("Leveling"),
                Leaf("Trials"),
                Leaf("Main Scenario"),
            ]);

        var mapping = Stabilize(snapshot, target);

        Assert.True(mapping.IsReady, mapping.Reason);
        Assert.Equal((uint)3, mapping.Entry!.Content.RowId);
        Assert.Equal(4, mapping.Entry.ObservedListPosition);
        Assert.Equal(4, mapping.Entry.UiRow.CallbackOrdinal);
        Assert.NotEqual((int)target.RowId, mapping.Entry.UiRow.CallbackOrdinal);
        Assert.True(DadDutyFinderMappedMutationRules.ShouldSelect(mapping, null));
        Assert.True(DadDutyFinderMappedMutationRules.CanJoin(
            mapping,
            mapping.Entry.SelectionToken,
            DadDutyFinderLiveContentType.Roulette,
            3,
            target));
    }

    [Fact]
    public void HeadersAreExcludedButDisabledPrecedingLeavesKeepTheirCallbackOrdinals()
    {
        var target = Target(DadDutyFinderLiveContentType.Regular, 30);
        var snapshot = Snapshot(
            Entries(
                (DadDutyFinderLiveContentType.Regular, 20u, "Available Second"),
                (DadDutyFinderLiveContentType.Regular, 30u, "Exact Target")),
            [
                Header("Expansion A"),
                Leaf("Locked First", enabled: false),
                Header("Expansion B"),
                Leaf("Available Second"),
                Leaf("Exact Target"),
            ]);

        var rows = snapshot.BuildUiRows();
        var mapping = Stabilize(snapshot, target);

        Assert.Equal([1, 2, 3], rows.Select(static row => row.CallbackOrdinal));
        Assert.False(rows[0].Enabled);
        Assert.True(mapping.IsReady, mapping.Reason);
        Assert.Equal(4, mapping.Entry!.UiRow.TreeIndex);
        Assert.Equal(3, mapping.Entry.UiRow.CallbackOrdinal);
    }

    [Fact]
    public void ExplicitAgentEntryCanStillProveThatTheExactTargetIsDisabled()
    {
        var target = Target(DadDutyFinderLiveContentType.Regular, 30);
        var snapshot = Snapshot(
            Entries(
                (DadDutyFinderLiveContentType.Regular, 20u, "Available First"),
                (DadDutyFinderLiveContentType.Regular, 30u, "Exact Target")),
            [Leaf("Available First"), Leaf("Exact Target", enabled: false)]);

        var mapping = Stabilize(snapshot, target);

        Assert.Equal(DadDutyFinderMappingStatus.Disabled, mapping.Status);
        Assert.NotNull(mapping.Entry);
        Assert.Equal(2, mapping.Entry!.UiRow.CallbackOrdinal);
        Assert.False(DadDutyFinderMappedMutationRules.ShouldSelect(mapping, null));
    }

    [Fact]
    public void ReorderedOrFilteredListsCannotProduceAnIndexBasedSelection()
    {
        var target = Target(DadDutyFinderLiveContentType.Roulette, 3);
        var reordered = Snapshot(
            Entries(
                (DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario"),
                (DadDutyFinderLiveContentType.Roulette, 1u, "Leveling")),
            [
                Leaf("Leveling"),
                Leaf("Main Scenario"),
            ]);
        var filtered = Snapshot(
            Entries(
                (DadDutyFinderLiveContentType.Roulette, 1u, "Leveling"),
                (DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario")),
            [Leaf("Main Scenario")]);

        AssertNoSelectionOrJoin(Stabilize(reordered, target), target, DadDutyFinderMappingStatus.Mismatch);
        AssertNoSelectionOrJoin(Stabilize(filtered, target), target, DadDutyFinderMappingStatus.Mismatch);
    }

    [Fact]
    public void AbsentDisabledDuplicateUnstableAndMismatchedTargetsCauseNoSelectionOrJoin()
    {
        var target = Target(DadDutyFinderLiveContentType.Roulette, 3);
        var absent = Snapshot(
            Entries((DadDutyFinderLiveContentType.Roulette, 1u, "Leveling")),
            [Leaf("Leveling")]);
        var disabled = Snapshot(
            Entries((DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario")),
            [Leaf("Main Scenario", enabled: false)]);
        var duplicate = Snapshot(
            Entries(
                (DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario"),
                (DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario Duplicate")),
            [Leaf("Main Scenario"), Leaf("Main Scenario Duplicate")]);
        var unstable = Snapshot(
            Entries((DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario")),
            [Leaf("Main Scenario")],
            listChanged: true);
        var mismatched = Snapshot(
            Entries((DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario")),
            [Leaf("Leveling")]);

        AssertNoSelectionOrJoin(Stabilize(absent, target), target, DadDutyFinderMappingStatus.Absent);
        AssertNoSelectionOrJoin(Stabilize(disabled, target), target, DadDutyFinderMappingStatus.Disabled);
        AssertNoSelectionOrJoin(Stabilize(duplicate, target), target, DadDutyFinderMappingStatus.Ambiguous);

        var unstableResult = new DadDutyFinderStableMappingGate().Observe(unstable, target);
        AssertNoSelectionOrJoin(unstableResult, target, DadDutyFinderMappingStatus.Unstable);
        AssertNoSelectionOrJoin(Stabilize(mismatched, target), target, DadDutyFinderMappingStatus.Mismatch);
    }

    [Fact]
    public void ListChangedDiscardsAReadyMappingEvenWhenTheSameListReturns()
    {
        var target = Target(DadDutyFinderLiveContentType.Roulette, 3);
        var stable = Snapshot(
            Entries((DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario")),
            [Leaf("Main Scenario")]);
        var changing = Snapshot(
            stable.ContentEntries,
            stable.TreeItems,
            listChanged: true);
        var gate = new DadDutyFinderStableMappingGate();

        Assert.Equal(DadDutyFinderMappingStatus.AwaitingStableSnapshot, gate.Observe(stable, target).Status);
        var firstReady = gate.Observe(stable, target);
        Assert.True(firstReady.IsReady, firstReady.Reason);
        var oldToken = firstReady.Entry!.SelectionToken;

        var invalidated = gate.Observe(changing, target);
        Assert.Equal(DadDutyFinderMappingStatus.Unstable, invalidated.Status);
        Assert.False(DadDutyFinderMappedMutationRules.CanJoin(
            invalidated,
            oldToken,
            DadDutyFinderLiveContentType.Roulette,
            3,
            target));

        Assert.Equal(DadDutyFinderMappingStatus.AwaitingStableSnapshot, gate.Observe(stable, target).Status);
        var secondReady = gate.Observe(stable, target);
        Assert.True(secondReady.IsReady, secondReady.Reason);
        Assert.NotEqual(oldToken, secondReady.Entry!.SelectionToken);
        Assert.True(DadDutyFinderMappedMutationRules.ShouldSelect(secondReady, oldToken));
    }

    [Fact]
    public void SameFrozenTargetUsesEachCharactersOwnLiveOrdinalAndNeverThePreviousCharactersToken()
    {
        var target = Target(DadDutyFinderLiveContentType.Roulette, 3);
        var firstCharacter = Snapshot(
            Entries(
                (DadDutyFinderLiveContentType.Roulette, 1u, "Leveling"),
                (DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario")),
            [Leaf("Leveling"), Leaf("Main Scenario")],
            characterContentId: 1001);
        var secondCharacter = Snapshot(
            Entries(
                (DadDutyFinderLiveContentType.Roulette, 8u, "Expert"),
                (DadDutyFinderLiveContentType.Roulette, 1u, "Leveling"),
                (DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario")),
            [
                Header("Daily Roulettes"),
                Leaf("Expert"),
                Leaf("Unavailable Roulette", enabled: false),
                Leaf("Leveling"),
                Leaf("Main Scenario"),
            ],
            characterContentId: 2002);
        var gate = new DadDutyFinderStableMappingGate();

        Assert.Equal(DadDutyFinderMappingStatus.AwaitingStableSnapshot, gate.Observe(firstCharacter, target).Status);
        var firstReady = gate.Observe(firstCharacter, target);
        Assert.True(firstReady.IsReady, firstReady.Reason);
        Assert.Equal(2, firstReady.Entry!.UiRow.CallbackOrdinal);
        var firstToken = firstReady.Entry.SelectionToken;

        var firstSecondCharacterScan = gate.Observe(secondCharacter, target);
        Assert.Equal(DadDutyFinderMappingStatus.AwaitingStableSnapshot, firstSecondCharacterScan.Status);
        Assert.False(DadDutyFinderMappedMutationRules.ShouldSelect(firstSecondCharacterScan, firstToken));
        Assert.False(DadDutyFinderMappedMutationRules.CanJoin(
            firstSecondCharacterScan,
            firstToken,
            DadDutyFinderLiveContentType.Roulette,
            3,
            target));

        var secondReady = gate.Observe(secondCharacter, target);
        Assert.True(secondReady.IsReady, secondReady.Reason);
        Assert.Equal((ulong)2002, secondReady.Entry!.SelectionToken.CharacterContentId);
        Assert.Equal(4, secondReady.Entry.UiRow.CallbackOrdinal);
        Assert.NotEqual(firstToken, secondReady.Entry.SelectionToken);
        Assert.True(DadDutyFinderMappedMutationRules.ShouldSelect(secondReady, firstToken));
        Assert.False(DadDutyFinderMappedMutationRules.CanJoin(
            secondReady,
            firstToken,
            DadDutyFinderLiveContentType.Roulette,
            3,
            target));
    }

    [Fact]
    public void ReorderingAValidLiveListInvalidatesTheOldCallbackUntilTwoFreshFullScans()
    {
        var target = Target(DadDutyFinderLiveContentType.Regular, 30);
        var original = Snapshot(
            Entries(
                (DadDutyFinderLiveContentType.Regular, 20u, "First Duty"),
                (DadDutyFinderLiveContentType.Regular, 30u, "Exact Target")),
            [Leaf("First Duty"), Leaf("Exact Target")]);
        var reordered = Snapshot(
            Entries(
                (DadDutyFinderLiveContentType.Regular, 30u, "Exact Target"),
                (DadDutyFinderLiveContentType.Regular, 20u, "First Duty")),
            [Leaf("Exact Target"), Leaf("First Duty")]);
        var gate = new DadDutyFinderStableMappingGate();

        _ = gate.Observe(original, target);
        var originalReady = gate.Observe(original, target);
        Assert.True(originalReady.IsReady, originalReady.Reason);
        Assert.Equal(2, originalReady.Entry!.UiRow.CallbackOrdinal);
        var oldToken = originalReady.Entry.SelectionToken;

        var changedOnce = gate.Observe(reordered, target);
        AssertNoSelectionOrJoin(changedOnce, target, DadDutyFinderMappingStatus.AwaitingStableSnapshot);
        Assert.False(DadDutyFinderMappedMutationRules.CanJoin(
            changedOnce,
            oldToken,
            DadDutyFinderLiveContentType.Regular,
            30,
            target));

        var reorderedReady = gate.Observe(reordered, target);
        Assert.True(reorderedReady.IsReady, reorderedReady.Reason);
        Assert.Equal(1, reorderedReady.Entry!.UiRow.CallbackOrdinal);
        Assert.NotEqual(oldToken, reorderedReady.Entry.SelectionToken);
    }

    [Fact]
    public void EnabledStateChangeInvalidatesAReadyTargetAndProducesNoSelectionOrJoin()
    {
        var target = Target(DadDutyFinderLiveContentType.Regular, 30);
        var enabled = Snapshot(
            Entries((DadDutyFinderLiveContentType.Regular, 30u, "Exact Target")),
            [Leaf("Exact Target")]);
        var disabled = Snapshot(
            enabled.ContentEntries,
            [Leaf("Exact Target", enabled: false)]);
        var gate = new DadDutyFinderStableMappingGate();

        _ = gate.Observe(enabled, target);
        var enabledReady = gate.Observe(enabled, target);
        Assert.True(enabledReady.IsReady, enabledReady.Reason);
        var oldToken = enabledReady.Entry!.SelectionToken;

        var changedOnce = gate.Observe(disabled, target);
        AssertNoSelectionOrJoin(changedOnce, target, DadDutyFinderMappingStatus.AwaitingStableSnapshot);
        Assert.False(DadDutyFinderMappedMutationRules.CanJoin(
            changedOnce,
            oldToken,
            DadDutyFinderLiveContentType.Regular,
            30,
            target));

        var disabledStable = gate.Observe(disabled, target);
        AssertNoSelectionOrJoin(disabledStable, target, DadDutyFinderMappingStatus.Disabled);
    }

    [Fact]
    public void DuplicateLocalizedNamesWithDifferentIdsAreAmbiguousAndCauseZeroMutations()
    {
        var target = Target(DadDutyFinderLiveContentType.Roulette, 3);
        var duplicateNames = Snapshot(
            Entries(
                (DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario"),
                (DadDutyFinderLiveContentType.Roulette, 4u, "  main\tScenario ")),
            [Leaf("Main Scenario"), Leaf("Main Scenario")]);

        AssertNoSelectionOrJoin(
            Stabilize(duplicateNames, target),
            target,
            DadDutyFinderMappingStatus.Ambiguous);
    }

    [Fact]
    public void RendererNodeTextIsDiagnosticOnlyAndCannotAuthorizeAMismatchedItemLabel()
    {
        var target = Target(DadDutyFinderLiveContentType.Roulette, 3);
        var diagnosticMismatch = Snapshot(
            Entries((DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario")),
            [Leaf("Main Scenario") with { RendererNodeText = "Leveling" }]);
        var falseRendererConfirmation = Snapshot(
            Entries((DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario")),
            [Leaf("Leveling") with { RendererNodeText = "Main Scenario" }]);

        var ready = Stabilize(diagnosticMismatch, target);
        Assert.True(ready.IsReady, ready.Reason);
        AssertNoSelectionOrJoin(
            Stabilize(falseRendererConfirmation, target),
            target,
            DadDutyFinderMappingStatus.Mismatch);
    }

    [Fact]
    public void MissingCharacterIdentityCannotCreateAStableMapping()
    {
        var target = Target(DadDutyFinderLiveContentType.Roulette, 3);
        var noCharacter = Snapshot(
            Entries((DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario")),
            [Leaf("Main Scenario")],
            characterContentId: 0);

        AssertNoSelectionOrJoin(
            new DadDutyFinderStableMappingGate().Observe(noCharacter, target),
            target,
            DadDutyFinderMappingStatus.Unstable);
    }

    [Theory]
    [InlineData("addon")]
    [InlineData("agent")]
    [InlineData("character")]
    [InlineData("tab")]
    [InlineData("count")]
    [InlineData("label")]
    [InlineData("storage")]
    [InlineData("tree-storage")]
    public void CharacterAddonAgentTabCountLabelOrStorageChangesRequireTwoFreshSnapshots(string change)
    {
        var target = Target(DadDutyFinderLiveContentType.Regular, 777);
        var original = Snapshot(
            Entries((DadDutyFinderLiveContentType.Regular, 777u, "The Exact Duty")),
            [Leaf("The Exact Duty")]);
        var gate = new DadDutyFinderStableMappingGate();
        _ = gate.Observe(original, target);
        var ready = gate.Observe(original, target);
        Assert.True(ready.IsReady, ready.Reason);

        var changed = Snapshot(
            change == "label"
                ? Entries((DadDutyFinderLiveContentType.Regular, 777u, "The Renamed Duty"))
                : original.ContentEntries,
            change == "label"
                ? [Leaf("The Renamed Duty")]
                : original.TreeItems,
            characterContentId: change == "character" ? 909u : original.CharacterContentId,
            addonIdentity: change == "addon" ? 101u : original.AddonIdentity,
            agentIdentity: change == "agent" ? 202u : original.AgentIdentity,
            selectedTab: change == "tab" ? (byte)9 : original.SelectedTab,
            declaredEntryCount: change == "count" ? 2u : null,
            contentStorageIdentity: change == "storage" ? 303u : original.ContentListStorageIdentity,
            dutyListStorageIdentity: change == "tree-storage" ? 404u : original.DutyListStorageIdentity);

        var firstChanged = gate.Observe(changed, target);
        Assert.Equal(DadDutyFinderMappingStatus.AwaitingStableSnapshot, firstChanged.Status);
        Assert.False(DadDutyFinderMappedMutationRules.ShouldSelect(firstChanged, ready.Entry!.SelectionToken));
        Assert.False(DadDutyFinderMappedMutationRules.CanJoin(
            firstChanged,
            ready.Entry.SelectionToken,
            DadDutyFinderLiveContentType.Regular,
            777,
            target));
    }

    [Fact]
    public void RegularDutyJoinRequiresEnabledLiveCfcMappingAndExactAgentTypeAndId()
    {
        var target = Target(DadDutyFinderLiveContentType.Regular, 777);
        var snapshot = Snapshot(
            Entries(
                (DadDutyFinderLiveContentType.Regular, 111u, "Another Duty"),
                (DadDutyFinderLiveContentType.Regular, 777u, "The Exact Duty")),
            [Leaf("Another Duty"), Leaf("The Exact Duty")]);
        var mapping = Stabilize(snapshot, target);
        var token = mapping.Entry!.SelectionToken;

        Assert.True(mapping.IsReady, mapping.Reason);
        Assert.False(DadDutyFinderMappedMutationRules.CanJoin(
            mapping, token, DadDutyFinderLiveContentType.Roulette, 777, target));
        Assert.False(DadDutyFinderMappedMutationRules.CanJoin(
            mapping, token, DadDutyFinderLiveContentType.Regular, 778, target));
        Assert.False(DadDutyFinderMappedMutationRules.CanJoin(
            mapping, null, DadDutyFinderLiveContentType.Regular, 777, target));
        Assert.True(DadDutyFinderMappedMutationRules.CanJoin(
            mapping, token, DadDutyFinderLiveContentType.Regular, 777, target));
        Assert.False(DadDutyFinderMappedMutationRules.ShouldSelect(mapping, token));
    }

    [Fact]
    public void WrongAgentContentTypeCannotBeCorrelatedByMatchingTextAlone()
    {
        var target = Target(DadDutyFinderLiveContentType.Roulette, 3);
        var snapshot = Snapshot(
            Entries((DadDutyFinderLiveContentType.Regular, 3u, "Main Scenario")),
            [Leaf("Main Scenario")]);

        AssertNoSelectionOrJoin(Stabilize(snapshot, target), target, DadDutyFinderMappingStatus.Mismatch);
    }

    [Fact]
    public void NormalizedLocalizedNamesConfirmTheAuthoritativeAgentOrder()
    {
        var target = Target(DadDutyFinderLiveContentType.Roulette, 3);
        var snapshot = Snapshot(
            Entries((DadDutyFinderLiveContentType.Roulette, 3u, "  Main\tScenario  ")),
            [Leaf("main scenario")]);

        var mapping = Stabilize(snapshot, target);

        Assert.True(mapping.IsReady, mapping.Reason);
        Assert.Equal("MAIN SCENARIO", DadDutyFinderLiveEntryMapping.NormalizeLocalizedName("  Main\tScenario  "));
    }

    [Fact]
    public void DiagnosticFreezesOnlyLiveDutyRouletteContentRows()
    {
        var snapshot = Snapshot(
            Entries(
                (DadDutyFinderLiveContentType.Regular, 777u, "Unrelated Duty"),
                (DadDutyFinderLiveContentType.Roulette, 1u, "Leveling"),
                (DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario")),
            [Leaf("Unrelated Duty"), Leaf("Leveling"), Leaf("Main Scenario")]);

        Assert.True(DadRouletteRewardDiagnosticLiveRowRules.TryBuildRows(
            snapshot,
            out var rows,
            out var fingerprint,
            out var reason), reason);
        Assert.Equal([1u, 3u], rows.Select(static row => row.RouletteId));
        Assert.Equal(["Leveling", "Main Scenario"], rows.Select(static row => row.LocalizedName));
        Assert.NotEmpty(fingerprint);
    }

    [Fact]
    public void DiagnosticFreezeRequiresTwoStableSnapshotsAndRejectsIdentityOrRowDrift()
    {
        var snapshot = Snapshot(
            Entries(
                (DadDutyFinderLiveContentType.Roulette, 1u, "Leveling"),
                (DadDutyFinderLiveContentType.Roulette, 3u, "Main Scenario")),
            [Leaf("Leveling"), Leaf("Main Scenario")]);
        var gate = new DadRouletteRewardDiagnosticFreezeGate();

        Assert.Equal(
            DadRouletteRewardDiagnosticFreezeStatus.Waiting,
            gate.Observe(snapshot, out _, out _));
        Assert.Equal(
            DadRouletteRewardDiagnosticFreezeStatus.Ready,
            gate.Observe(snapshot, out var frozen, out var readyReason));
        Assert.NotNull(frozen);
        Assert.Empty(readyReason);

        var identityDrift = Snapshot(
            snapshot.ContentEntries,
            snapshot.TreeItems,
            characterContentId: 909);
        Assert.False(DadRouletteRewardDiagnosticLiveRowRules.MatchesFrozen(
            identityDrift,
            frozen!,
            out var identityReason));
        Assert.Contains("drifted", identityReason, StringComparison.OrdinalIgnoreCase);

        var rowDrift = Snapshot(
            Entries(
                (DadDutyFinderLiveContentType.Roulette, 1u, "Leveling"),
                (DadDutyFinderLiveContentType.Roulette, 8u, "High-level Dungeons")),
            [Leaf("Leveling"), Leaf("High-level Dungeons")]);
        Assert.False(DadRouletteRewardDiagnosticLiveRowRules.MatchesFrozen(
            rowDrift,
            frozen!,
            out var rowReason));
        Assert.Contains("row set drifted", rowReason, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            DadRouletteRewardDiagnosticFreezeStatus.Waiting,
            gate.Observe(
                Snapshot(snapshot.ContentEntries, snapshot.TreeItems, listChanged: true),
                out _,
                out var listReason));
        Assert.Contains("generation changed", listReason, StringComparison.OrdinalIgnoreCase);
    }

    private static DadDutyFinderMappingResult Stabilize(
        DadDutyFinderListSnapshot snapshot,
        DadDutyFinderLiveTarget target)
    {
        var gate = new DadDutyFinderStableMappingGate();
        var first = gate.Observe(snapshot, target);
        Assert.Equal(DadDutyFinderMappingStatus.AwaitingStableSnapshot, first.Status);
        return gate.Observe(snapshot, target);
    }

    private static void AssertNoSelectionOrJoin(
        DadDutyFinderMappingResult mapping,
        DadDutyFinderLiveTarget target,
        DadDutyFinderMappingStatus expectedStatus)
    {
        Assert.Equal(expectedStatus, mapping.Status);
        Assert.False(DadDutyFinderMappedMutationRules.ShouldSelect(mapping, null));
        Assert.False(DadDutyFinderMappedMutationRules.CanJoin(
            mapping,
            mapping.Entry?.SelectionToken,
            target.ContentType,
            target.RowId,
            target));
    }

    private static DadDutyFinderLiveTarget Target(DadDutyFinderLiveContentType type, uint rowId)
        => new(type, rowId);

    private static IReadOnlyList<DadDutyFinderContentEntry> Entries(
        params (DadDutyFinderLiveContentType Type, uint RowId, string Name)[] entries)
        => entries
            .Select((entry, index) => new DadDutyFinderContentEntry(index, entry.Type, entry.RowId, entry.Name))
            .ToList();

    private static DadDutyFinderTreeItem Header(string label)
        => new(0, IsLeaf: false, Enabled: true, ItemLabel: label);

    private static DadDutyFinderTreeItem Leaf(string label, bool enabled = true)
        => new(0, IsLeaf: true, Enabled: enabled, ItemLabel: label);

    private static DadDutyFinderListSnapshot Snapshot(
        IReadOnlyList<DadDutyFinderContentEntry> entries,
        IReadOnlyList<DadDutyFinderTreeItem> treeItems,
        bool listChanged = false,
        ulong characterContentId = 66,
        nuint addonIdentity = 11,
        nuint agentIdentity = 22,
        byte selectedTab = 1,
        uint? declaredEntryCount = null,
        nuint contentStorageIdentity = 44,
        nuint dutyListStorageIdentity = 55)
    {
        var indexedTreeItems = treeItems
            .Select((item, index) => item with { TreeIndex = index })
            .ToList();
        return new DadDutyFinderListSnapshot
        {
            CharacterContentId = characterContentId,
            AddonIdentity = addonIdentity,
            AgentIdentity = agentIdentity,
            DutyListIdentity = 33,
            ContentListStorageIdentity = contentStorageIdentity,
            DutyListStorageIdentity = dutyListStorageIdentity,
            SelectedTab = selectedTab,
            SelectedRadioButton = 0,
            DeclaredEntryCount = declaredEntryCount ?? (uint)indexedTreeItems.Count,
            TreeItemCount = indexedTreeItems.Count,
            ListChanged = listChanged,
            ContentEntries = entries,
            TreeItems = indexedTreeItems,
        };
    }
}
