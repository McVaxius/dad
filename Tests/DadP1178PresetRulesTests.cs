using System.Text.Json;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadP1178PresetRulesTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    public void AdaptiveDutyUsesPrimaryRowCount(int primaryCount, bool premade)
    {
        var group = Group(primaryCount);
        group.Slots.Add(new DadPlannerGroupSlot
        {
            SlotId = "Slot1",
            IsSubstitute = true,
            RequiredCharacterKey = new DadCharacterKey("sub"),
        });

        var projection = DadAdaptiveDutyProjectionRules.Resolve(DadPlannerActivityMode.LocalDuty, group);
        var request = new DadRunRequest();
        DadAdaptiveDutyProjectionRules.PopulateDutyTask(request, projection, 123, "Duty", true);

        Assert.Equal(primaryCount, projection.ExpectedPartySize);
        Assert.Equal(premade, projection.UsesPremadeExecutor);
        if (premade)
        {
            Assert.Null(request.Dungeon);
            Assert.Equal(primaryCount, request.PremadeDuty!.ExpectedPartySize);
        }
        else
        {
            Assert.NotNull(request.Dungeon);
            Assert.Null(request.PremadeDuty);
        }
    }

    [Fact]
    public void BoundSubstituteCarriesItsOwnAdsAndLevelSettings()
    {
        var group = Group(1);
        group.Slots[0].AdsLootMode = DadAdsLootMode.Need;
        group.Slots[0].LevelSeekTarget = 80;
        group.Slots.Add(new DadPlannerGroupSlot
        {
            SlotId = "Slot1",
            IsSubstitute = true,
            RequiredAccountKey = new DadAccountKey("account"),
            RequiredCharacterKey = new DadCharacterKey("sub"),
            AdsLootMode = DadAdsLootMode.Pass,
            LevelSeekTarget = 99,
        });

        var bound = DadEffectivePlannerGroupProjection.BindResolvedSchedulerSlots(group,
        [
            new DadPresetCharacterSlot
            {
                SlotId = "Slot1",
                RequiredAccountKey = new DadAccountKey("account"),
                RequiredCharacterKey = new DadCharacterKey("sub"),
                CharacterKey = "sub",
                IsSubstitution = true,
                AdsLootMode = DadAdsLootMode.Pass,
                LevelSeekTarget = 99,
            },
        ]);

        var selected = Assert.Single(bound.Slots);
        Assert.False(selected.IsSubstitute);
        Assert.Equal(DadAdsLootMode.Pass, selected.AdsLootMode);
        Assert.Equal(99, selected.LevelSeekTarget);
    }

    [Fact]
    public void LevelSeekWithNoTargetsRunsUnconditionally()
    {
        var result = DadLevelSeekEvaluator.Evaluate(Group(1), Pool(Character("one", 90, 19)));
        Assert.False(result.HasTargetedRows);
        Assert.False(result.ShouldSkip);
    }

    [Fact]
    public void LevelSeekIgnoresBlankTargetRows()
    {
        var group = Group(1);
        group.Slots[0].RequiredAccountKey = new DadAccountKey(string.Empty);
        group.Slots[0].RequiredCharacterKey = new DadCharacterKey(string.Empty);
        group.Slots[0].LevelSeekTarget = 90;
        var result = DadLevelSeekEvaluator.Evaluate(group, new DadCharacterPool());
        Assert.False(result.HasTargetedRows);
        Assert.False(result.ShouldSkip);
    }

    [Theory]
    [InlineData(89, false)]
    [InlineData(90, true)]
    [InlineData(100, true)]
    public void LevelSeekUsesRequiredJobLevel(int knownLevel, bool shouldSkip)
    {
        var group = Group(1);
        group.Slots[0].LevelSeekTarget = 90;
        group.Slots[0].RequiredJobId = 21;
        var character = Character("one", 30, 19);
        character.JobLevels[21] = knownLevel;
        Assert.Equal(shouldSkip, DadLevelSeekEvaluator.Evaluate(group, Pool(character)).ShouldSkip);
    }

    [Fact]
    public void LevelSeekUsesCurrentJobAndUnknownOrMissingRuns()
    {
        var group = Group(2);
        group.Slots.ForEach(slot => slot.LevelSeekTarget = 90);
        var known = Character("one", 90, 19);
        var unknown = Character("two", null, null);
        var result = DadLevelSeekEvaluator.Evaluate(group, Pool(known, unknown));
        Assert.False(result.ShouldSkip);
        Assert.Contains(result.Rows, row => row.State == DadLevelSeekRowState.Unknown);

        Assert.False(DadLevelSeekEvaluator.Evaluate(group, Pool(known)).ShouldSkip);
    }

    [Theory]
    [InlineData(DadAdsLootMode.NoChange, false)]
    [InlineData(DadAdsLootMode.Need, true)]
    [InlineData(DadAdsLootMode.Greed, true)]
    [InlineData(DadAdsLootMode.Pass, true)]
    public void AdsPatchEnablesEveryRegistrableCategoryAndConditionallySetsMode(DadAdsLootMode mode, bool hasMode)
    {
        using var document = JsonDocument.Parse(DadAdsConfigurationPatchRules.BuildPatchJson(mode));
        var root = document.RootElement;
        var expected = new[]
        {
            "lootRegistrableNeedingEnabled", "lootRegistrableMountsEnabled", "lootRegistrableMinionsEnabled",
            "lootRegistrableFashionAccessoriesEnabled", "lootRegistrableFacewearEnabled", "lootRegistrableOrchestrionRollsEnabled",
            "lootRegistrableFadedOrchestrionCopiesEnabled", "lootRegistrableEmotesHairstylesEnabled",
            "lootRegistrableBardingsEnabled", "lootRegistrableTripleTriadCardsEnabled",
        };
        foreach (var name in expected)
            Assert.True(root.GetProperty(name).GetBoolean());
        Assert.Equal(hasMode, root.TryGetProperty("lootMode", out _));
    }

    [Fact]
    public void SchedulerSkippedMapsToSuccessfulCompletedResult()
    {
        var state = new DadSchedulerPresetState { Phase = DadSchedulerPresetPhase.Skipped };
        var result = state.ToRunResult();
        Assert.Equal(DadRunStatus.Completed, result.Status);
    }

    [Fact]
    public void SchedulerCancelledMapsToCancelledResult()
    {
        var state = new DadSchedulerPresetState { Phase = DadSchedulerPresetPhase.Cancelled };
        Assert.Equal(DadRunStatus.Cancelled, state.ToRunResult().Status);
    }

    [Fact]
    public void ScheduleSkipAdvancesAndCountsAllSkippedCompletion()
    {
        var schedule = new DadScheduleDefinition
        {
            ScheduleId = "schedule",
            DisplayName = "Schedule",
            Entries =
            [
                new DadScheduleEntry { EntryId = "one", GroupId = "g1" },
                new DadScheduleEntry { EntryId = "two", GroupId = "g2" },
            ],
        };
        var now = DateTime.UtcNow;
        var state = DadScheduleRules.StartRun(schedule, false, true, "test", now);
        state = DadScheduleRules.AdvanceAfterEntry(state, schedule.Entries, true, "skip one", now.AddSeconds(1), entrySkipped: true);
        Assert.Equal(DadScheduleRunStatus.Running, state.Status);
        Assert.Equal(1, state.SkippedEntryExecutions);
        state = DadScheduleRules.AdvanceAfterEntry(state, schedule.Entries, true, "skip two", now.AddSeconds(2), entrySkipped: true);
        Assert.Equal(DadScheduleRunStatus.Completed, state.Status);
        Assert.Equal(2, state.CompletedEntryExecutions);
        Assert.Equal(2, state.SkippedEntryExecutions);
    }

    private static DadPlannerGroup Group(int primaryCount)
        => new()
        {
            Slots = Enumerable.Range(1, primaryCount).Select(index => new DadPlannerGroupSlot
            {
                SlotId = $"Slot{index}",
                RequiredAccountKey = new DadAccountKey($"account{index}"),
                RequiredCharacterKey = new DadCharacterKey(index == 1 ? "one" : index == 2 ? "two" : $"character{index}"),
            }).ToList(),
        };

    private static DadAcquiredCharacter Character(string key, int? currentLevel, uint? currentJob)
        => new()
        {
            CharacterKey = key,
            AccountId = key == "one" ? "account1" : "account2",
            CurrentJobId = currentJob,
            CurrentLevel = currentLevel,
            JobLevels = currentJob.HasValue && currentLevel.HasValue
                ? new Dictionary<uint, int> { [currentJob.Value] = currentLevel.Value }
                : [],
        };

    private static DadCharacterPool Pool(params DadAcquiredCharacter[] characters)
        => new() { Characters = [..characters] };
}
