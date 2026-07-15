using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRoulettePresetConflictRulesTests
{
    [Fact]
    public void CanonicalRouletteConflictFindsExactAccountAndCharacterAcrossSavedPresets()
    {
        var current = Group("current", "Current", 1, "account-a", "Alpha@Siren");
        var index = DadRoulettePresetConflictRules.BuildIndex(
        [
            current,
            Group("one", "Leveling Batch 01", 1, "account-a", "Alpha@Siren"),
            Group("two", "Leveling Batch 02", 1, "account-a", "Alpha@Siren"),
            Group("main", "Main Scenario Batch", 3, "account-a", "Alpha@Siren"),
        ]);

        var warning = index.Find(current, new DadAccountKey("account-a"), new DadCharacterKey("Alpha@Siren"));

        Assert.True(warning.HasConflict);
        Assert.Equal((uint)1, warning.RouletteId);
        Assert.Equal(["Leveling Batch 01", "Leveling Batch 02"], warning.PresetNames);
        Assert.Equal(
            "This Character is already in a similar preset: Leveling Batch 01, Leveling Batch 02.",
            warning.Message);
        Assert.False(warning.IsBlocking);
    }

    [Fact]
    public void ExactAccountIsolationPreventsAnotherAccountsCharacterFromWarning()
    {
        var current = Group("current", "Current", 1, "account-a", "Shared Name@Siren");
        var index = DadRoulettePresetConflictRules.BuildIndex(
        [
            Group("other", "Other Account", 1, "account-b", "Shared Name@Siren"),
        ]);

        Assert.False(index.Find(
            current,
            new DadAccountKey("account-a"),
            new DadCharacterKey("Shared Name@Siren")).HasConflict);
    }

    [Fact]
    public void LegacyMainScenarioAndCanonicalIdThreeAreEquivalent()
    {
        var legacy = Group("legacy", "Legacy MSQ", 0, "account-a", "Alpha@Siren");
        legacy.RouletteTarget.Key = DadRouletteCatalogProjection.MainScenarioLegacyKey;
        var canonical = Group("canonical", "Canonical MSQ", 3, "account-a", "Alpha@Siren");
        var index = DadRoulettePresetConflictRules.BuildIndex([legacy, canonical]);

        var warning = index.Find(
            canonical,
            new DadAccountKey("account-a"),
            new DadCharacterKey("Alpha@Siren"));

        Assert.Equal((uint)3, DadRoulettePresetConflictRules.ResolveCanonicalRouletteId(legacy));
        Assert.Equal(["Legacy MSQ"], warning.PresetNames);
    }

    [Fact]
    public void CanonicalKeyCanRecoverMissingStoredRouletteId()
    {
        var group = Group("key-only", "Key only", 0, "account-a", "Alpha@Siren");
        group.RouletteTarget.Key = "contentroulette:1";

        Assert.Equal((uint)1, DadRoulettePresetConflictRules.ResolveCanonicalRouletteId(group));
    }

    [Fact]
    public void NonRoulettePlansAndDifferentRoulettesDoNotConflictOrBlock()
    {
        var leveling = Group("leveling", "Leveling", 1, "account-a", "Alpha@Siren");
        var mainScenario = Group("main", "MSQ", 3, "account-a", "Alpha@Siren");
        var duty = Group("duty", "Duty", 1, "account-a", "Alpha@Siren");
        duty.ActivityMode = DadPlannerActivityMode.PremadeDuty;
        var index = DadRoulettePresetConflictRules.BuildIndex([leveling, mainScenario, duty]);

        var warning = index.Find(
            leveling,
            new DadAccountKey("account-a"),
            new DadCharacterKey("Alpha@Siren"));

        Assert.False(warning.HasConflict);
        Assert.False(warning.IsBlocking);
        Assert.Equal(string.Empty, warning.Message);
    }

    [Fact]
    public void ConflictPresentationMarksEveryChoiceAndSelectedPreviewBoldOrange()
    {
        var presentation = DadCharacterConflictPresentationRules.Build(
        [
            new("alpha@siren", "Alpha", true),
            new("beta@siren", "Beta", false),
            new("gamma@siren", "Gamma", true),
        ], "GAMMA@SIREN");

        Assert.True(presentation.Choices.Single(choice => choice.DisplayName == "Alpha").UseBoldOrange);
        Assert.False(presentation.Choices.Single(choice => choice.DisplayName == "Beta").UseBoldOrange);
        Assert.True(presentation.Choices.Single(choice => choice.DisplayName == "Gamma").UseBoldOrange);
        Assert.True(presentation.SelectedUseBoldOrange);
    }

    [Fact]
    public void ConflictSummaryContainsUniqueNamesOnlyAndDefaultsEmpty()
    {
        var empty = DadCharacterConflictPresentationRules.Build([], null);
        var presentation = DadCharacterConflictPresentationRules.Build(
        [
            new("alpha@siren", "Alpha", false),
            new("ALPHA@SIREN", "Alpha", true),
            new("gamma@siren", "gamma", true),
            new("delta@siren", "Gamma", true),
        ], "beta@siren");

        Assert.Empty(empty.SummaryNames);
        Assert.Equal(string.Empty, empty.Summary);
        Assert.False(presentation.SelectedUseBoldOrange);
        Assert.Equal(["Alpha", "gamma"], presentation.SummaryNames);
        Assert.Equal("Characters in multiple presets: Alpha, gamma", presentation.Summary);
    }

    private static DadPlannerGroup Group(
        string id,
        string name,
        uint rouletteId,
        string account,
        string character)
        => new()
        {
            GroupId = id,
            DisplayName = name,
            ActivityMode = DadPlannerActivityMode.DailyRoulette,
            RunFamily = DadPlannerRunFamily.DailyRoulette,
            RouletteTarget = new DadQueueTarget
            {
                Kind = DadQueueTargetKind.Roulette,
                RouletteId = rouletteId,
                Key = rouletteId == 0 ? string.Empty : DadRouletteCatalogProjection.BuildCanonicalKey(rouletteId),
            },
            Slots =
            [
                new DadPlannerGroupSlot
                {
                    SlotId = "Slot1",
                    RequiredAccountKey = new DadAccountKey(account),
                    RequiredCharacterKey = new DadCharacterKey(character),
                },
            ],
        };
}
