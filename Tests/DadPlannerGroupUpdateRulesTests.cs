using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadPlannerGroupUpdateRulesTests
{
    [Fact]
    public void WakePolicyNumericValuesRemainStable()
    {
        Assert.Equal(0, (int)DadSchedulerWakePolicy.AlreadyOnlineOnly);
        Assert.Equal(1, (int)DadSchedulerWakePolicy.LaunchIfOffline);
        Assert.Equal(2, (int)DadSchedulerWakePolicy.LoadCharacterIfOnline);
    }

    [Fact]
    public void NewSlotsDefaultToLaunchIfOffline()
    {
        var slot = new DadPlannerGroupSlot();

        Assert.Equal(DadSchedulerWakePolicy.LaunchIfOffline, slot.WakePolicy);
    }

    [Fact]
    public void PlannerFieldUpdateNeverRebuildsExistingSlots()
    {
        var originalInstruction = new DadCharacterLoadInstruction
        {
            Enabled = true,
            CommandTemplate = "/legacy {CharacterKey}",
            TimeoutSeconds = 444,
            DryRun = false,
        };
        var target = new DadPlannerGroup
        {
            GroupId = "saved",
            DisplayName = "Before",
            LevelingMode = new DadLevelingModeOptions
            {
                Enabled = true,
                GoalLevel = 90,
                DutyThresholds =
                [
                    new DadLevelingDutyThreshold { MinimumLevel = 1, ContentFinderConditionId = 4 },
                ],
            },
            Slots =
            [
                new DadPlannerGroupSlot
                {
                    SlotId = "Slot1",
                    RequiredAccountKey = new DadAccountKey("account-a"),
                    RequiredCharacterKey = new DadCharacterKey("Existing Character@World"),
                    WakePolicy = DadSchedulerWakePolicy.AlreadyOnlineOnly,
                    LaunchProfileId = "legacy-profile",
                    CharacterLoadInstruction = originalInstruction,
                },
            ],
        };
        var source = new DadPlannerGroup
        {
            DisplayName = "After",
            ActivityMode = DadPlannerActivityMode.Trust,
            Slots =
            [
                new DadPlannerGroupSlot
                {
                    SlotId = "Slot1",
                    RequiredCharacterKey = new DadCharacterKey("Runtime Preview@World"),
                },
            ],
        };

        DadPlannerGroupUpdateRules.ApplyPlannerFields(
            target,
            source,
            new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc));

        var slot = Assert.Single(target.Slots);
        Assert.Equal("After", target.DisplayName);
        Assert.Equal(DadPlannerActivityMode.Trust, target.ActivityMode);
        Assert.Equal("Existing Character@World", slot.RequiredCharacterKey.Value);
        Assert.Equal(DadSchedulerWakePolicy.AlreadyOnlineOnly, slot.WakePolicy);
        Assert.Equal("legacy-profile", slot.LaunchProfileId);
        Assert.Equal("/legacy {CharacterKey}", slot.CharacterLoadInstruction.CommandTemplate);
        Assert.Same(originalInstruction, slot.CharacterLoadInstruction);
        Assert.True(target.LevelingMode.Enabled);
        Assert.Equal(90, target.LevelingMode.GoalLevel);
        Assert.Equal((uint)4, Assert.Single(target.LevelingMode.DutyThresholds).ContentFinderConditionId);
    }

    [Fact]
    public void PlannerFieldUpdateDeepClonesDailyRouletteTarget()
    {
        var target = new DadPlannerGroup
        {
            RouletteTarget = new DadQueueTarget
            {
                Kind = DadQueueTargetKind.Roulette,
                RouletteId = 3,
                Key = "ContentRoulette:3",
                DisplayName = "Main Scenario",
            },
        };
        var sourceTarget = new DadQueueTarget
        {
            SchemaVersion = 4,
            Kind = DadQueueTargetKind.Roulette,
            RouletteId = 8,
            Key = "ContentRoulette:8",
            DisplayName = "Level Cap Dungeons",
        };
        var source = new DadPlannerGroup
        {
            RunFamily = DadPlannerRunFamily.DailyRoulette,
            ActivityMode = DadPlannerActivityMode.DailyRoulette,
            RouletteTarget = sourceTarget,
        };

        DadPlannerGroupUpdateRules.ApplyPlannerFields(target, source, DateTime.UtcNow);

        Assert.Equal(DadPlannerRunFamily.DailyRoulette, target.RunFamily);
        Assert.Equal(DadPlannerActivityMode.DailyRoulette, target.ActivityMode);
        Assert.NotSame(sourceTarget, target.RouletteTarget);
        Assert.Equal(4, target.RouletteTarget.SchemaVersion);
        Assert.Equal(DadQueueTargetKind.Roulette, target.RouletteTarget.Kind);
        Assert.Equal((uint)8, target.RouletteTarget.RouletteId);
        Assert.Equal("ContentRoulette:8", target.RouletteTarget.Key);
        Assert.Equal("Level Cap Dungeons", target.RouletteTarget.DisplayName);

        var restored = DadIpcJson.Deserialize<DadPlannerGroup>(DadIpcJson.Serialize(target));
        Assert.NotNull(restored);
        Assert.NotSame(target.RouletteTarget, restored.RouletteTarget);
        Assert.Equal((uint)8, restored.RouletteTarget.RouletteId);
        Assert.Equal("ContentRoulette:8", restored.RouletteTarget.Key);
        Assert.Equal("Level Cap Dungeons", restored.RouletteTarget.DisplayName);

        sourceTarget.RouletteId = 5;
        sourceTarget.Key = "ContentRoulette:5";
        sourceTarget.DisplayName = "Expert";
        Assert.Equal((uint)8, target.RouletteTarget.RouletteId);
        Assert.Equal("ContentRoulette:8", target.RouletteTarget.Key);
        Assert.Equal("Level Cap Dungeons", target.RouletteTarget.DisplayName);
    }

    [Fact]
    public void ExplicitRefreshPreservesMatchingOperationalSettingsAndDefaultsNewRows()
    {
        var existing = new[]
        {
            new DadPlannerGroupSlot
            {
                SlotId = "Slot1",
                RequiredRole = DadPartyRole.Tank,
                RequiredAccountKey = new DadAccountKey("account-a"),
                RequiredCharacterKey = new DadCharacterKey("Saved Character@World"),
                RequiredJobId = 21,
                SkipIfDailyRouletteRewardReceived = true,
                WakePolicy = DadSchedulerWakePolicy.AlreadyOnlineOnly,
                LaunchProfileId = "saved-profile",
                CharacterLoadInstruction = new DadCharacterLoadInstruction
                {
                    Enabled = true,
                    CommandTemplate = "/saved",
                    TimeoutSeconds = 321,
                    DryRun = false,
                },
            },
        };
        var refreshed = new[]
        {
            new DadPlannerGroupSlot
            {
                SlotId = "Slot1",
                RequiredRole = DadPartyRole.Healer,
                RequiredAccountKey = new DadAccountKey("runtime-a"),
                RequiredCharacterKey = new DadCharacterKey("Runtime Character@World"),
            },
            new DadPlannerGroupSlot
            {
                SlotId = "Slot2",
                RequiredRole = DadPartyRole.Dps,
                RequiredAccountKey = new DadAccountKey("runtime-b"),
                RequiredCharacterKey = new DadCharacterKey("New Character@World"),
                WakePolicy = DadSchedulerWakePolicy.AlreadyOnlineOnly,
            },
        };

        var merged = DadPlannerGroupUpdateRules.RefreshSlotsPreservingOperationalSettings(existing, refreshed);

        Assert.Equal(2, merged.Count);
        Assert.Equal(DadPartyRole.Healer, merged[0].RequiredRole);
        Assert.Equal("account-a", merged[0].RequiredAccountKey.Value);
        Assert.Equal("Saved Character@World", merged[0].RequiredCharacterKey.Value);
        Assert.Equal((uint?)21, merged[0].RequiredJobId);
        Assert.True(merged[0].SkipIfDailyRouletteRewardReceived);
        Assert.Equal(DadSchedulerWakePolicy.AlreadyOnlineOnly, merged[0].WakePolicy);
        Assert.Equal("saved-profile", merged[0].LaunchProfileId);
        Assert.Equal("/saved", merged[0].CharacterLoadInstruction.CommandTemplate);
        Assert.Equal("New Character@World", merged[1].RequiredCharacterKey.Value);
        Assert.Null(merged[1].RequiredJobId);
        Assert.False(merged[1].SkipIfDailyRouletteRewardReceived);
        Assert.Equal(DadSchedulerWakePolicy.LaunchIfOffline, merged[1].WakePolicy);
    }

    [Fact]
    public void NormalizeAndJsonReloadPreserveIntentionalAlreadyOnlinePolicy()
    {
        var group = new DadPlannerGroup
        {
            Slots =
            [
                new DadPlannerGroupSlot
                {
                    SlotId = "Slot1",
                    WakePolicy = DadSchedulerWakePolicy.AlreadyOnlineOnly,
                    RequiredCharacterKey = new DadCharacterKey("Persisted Character@World"),
                    RequiredJobId = 37,
                },
            ],
        };

        for (var cycle = 0; cycle < 3; cycle++)
        {
            group.Slots = DadPlannerSlotRules.NormalizeGroupSlots(group.Slots);
            group = DadIpcJson.Deserialize<DadPlannerGroup>(DadIpcJson.Serialize(group))!;
        }

        var slot = Assert.Single(group.Slots);
        Assert.Equal(DadSchedulerWakePolicy.AlreadyOnlineOnly, slot.WakePolicy);
        Assert.Equal((uint?)37, slot.RequiredJobId);
    }

    [Fact]
    public void LegacyJsonWithoutRequiredJobDefaultsToAny()
    {
        const string json = "{\"slots\":[{\"slotId\":\"Slot1\"}]}";

        var group = DadIpcJson.Deserialize<DadPlannerGroup>(json)!;

        Assert.Null(Assert.Single(group.Slots).RequiredJobId);
    }
}
