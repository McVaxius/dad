using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadEffectivePlannerGroupProjectionTests
{
    [Fact]
    public void FourSavedMsqRowsProjectToOneLogicalSlotWithoutMutatingSource()
    {
        var group = BuildGroup(5);
        group.LevelingMode = new DadLevelingModeOptions
        {
            Enabled = true,
            GoalLevel = 91,
            DutyThresholds =
            [
                new DadLevelingDutyThreshold { MinimumLevel = 1, ContentFinderConditionId = 777 },
            ],
        };
        group.Slots.Insert(1, Slot(1, "Msq Backup", substitute: true));
        group.Slots.Insert(4, Slot(3, "Discarded Backup", substitute: true));
        var originalRows = group.Slots
            .Select(static slot => (slot.SlotId, slot.IsSubstitute, slot.RequiredCharacterKey.Value))
            .ToList();

        var projected = DadEffectivePlannerGroupProjection.Project(group, DadPlannerActivityMode.Msq, 4);

        Assert.Single(DadPlannerSlotRules.GetPrimaryRows(projected.Slots));
        Assert.Equal(2, projected.Slots.Count);
        Assert.All(projected.Slots, static slot => Assert.Equal("Slot1", slot.SlotId));
        Assert.Contains(projected.Slots, static slot => slot.IsSubstitute && slot.RequiredCharacterKey.Value == "Msq Backup@World");
        Assert.Equal(originalRows, group.Slots.Select(static slot => (slot.SlotId, slot.IsSubstitute, slot.RequiredCharacterKey.Value)).ToList());
        Assert.NotSame(group.Slots[0], projected.Slots[0]);
        Assert.True(projected.LevelingMode.Enabled);
        Assert.Equal(91, projected.LevelingMode.GoalLevel);
        Assert.NotSame(group.LevelingMode, projected.LevelingMode);
        Assert.NotSame(group.LevelingMode.DutyThresholds[0], projected.LevelingMode.DutyThresholds[0]);
    }

    [Fact]
    public void DailyRouletteProjectsExactlyFourLogicalSlotsAndTheirSubstitutes()
    {
        var group = BuildGroup(5);
        group.Slots.Insert(2, Slot(2, "Slot2 Backup", substitute: true));
        group.Slots.Add(Slot(5, "Slot5 Backup", substitute: true));

        var projected = DadEffectivePlannerGroupProjection.Project(group, DadPlannerActivityMode.DailyRoulette, 8);

        Assert.Equal(4, DadPlannerSlotRules.CountPrimarySlots(projected.Slots));
        Assert.Equal(5, projected.Slots.Count);
        Assert.Contains(projected.Slots, static slot => slot.IsSubstitute && slot.SlotId == "Slot2");
        Assert.DoesNotContain(projected.Slots, static slot => slot.SlotId == "Slot5");
        Assert.Equal(7, group.Slots.Count);
    }

    [Fact]
    public void SchedulerBindingEmitsOneRowPerProjectedPrimaryEvenWhenResolutionIsMissing()
    {
        var group = BuildGroup(4);
        group.Slots.Insert(2, Slot(2, "Slot2 Backup", substitute: true));
        group.Slots[1].SkipIfDailyRouletteRewardReceived = false;
        group.Slots[2].SkipIfDailyRouletteRewardReceived = true;
        var projected = DadEffectivePlannerGroupProjection.Project(group, DadPlannerActivityMode.DailyRoulette, 4);

        var bound = DadEffectivePlannerGroupProjection.BindResolvedSchedulerSlots(
            projected,
            [
                new DadPresetCharacterSlot
                {
                    SlotId = "Slot2",
                    CharacterKey = "Slot2 Backup@World",
                    RequiredCharacterKey = new DadCharacterKey("Slot2 Backup@World"),
                    RequiredAccountKey = new DadAccountKey("account-2b"),
                    RequiredJobId = 32,
                    IsSubstitution = true,
                },
            ]);

        Assert.Equal(4, bound.Slots.Count);
        Assert.All(bound.Slots, static slot => Assert.False(slot.IsSubstitute));
        Assert.Equal("Character 1@World", bound.Slots[0].RequiredCharacterKey.Value);
        Assert.Equal("Slot2 Backup@World", bound.Slots[1].RequiredCharacterKey.Value);
        Assert.Equal((uint)32, bound.Slots[1].RequiredJobId);
        Assert.True(bound.Slots[1].SkipIfDailyRouletteRewardReceived);
        Assert.Equal("Character 3@World", bound.Slots[2].RequiredCharacterKey.Value);
        Assert.Equal("Character 4@World", bound.Slots[3].RequiredCharacterKey.Value);
    }

    [Fact]
    public void MsqFourPlayerTestPreservesRequestedJobsThroughProjectionAndAssignment()
    {
        uint[] requestedJobs = [40, 32, 24, 38];
        var group = BuildGroup(4);
        group.DisplayName = "msq 4 player test";
        group.ActivityMode = DadPlannerActivityMode.DailyRoulette;
        group.RouletteTarget = new DadQueueTarget
        {
            Kind = DadQueueTargetKind.Roulette,
            Key = "3",
            DisplayName = "Main Scenario",
        };
        for (var index = 0; index < group.Slots.Count; index++)
            group.Slots[index].RequiredJobId = requestedJobs[index];

        var projected = DadEffectivePlannerGroupProjection.Project(
            group,
            DadPlannerActivityMode.DailyRoulette,
            requestedPartySize: 4);
        var assignments = projected.Slots.Select((slot, index) => new DadPresetCharacterSlot
        {
            SlotId = slot.SlotId,
            CharacterKey = slot.RequiredCharacterKey.Value,
            RequiredCharacterKey = slot.RequiredCharacterKey,
            RequiredAccountKey = slot.RequiredAccountKey,
            RequiredJobId = requestedJobs[index],
            ContentId = (ulong)(1001 + index),
        }).ToList();
        var bound = DadEffectivePlannerGroupProjection.BindResolvedSchedulerSlots(projected, assignments);

        Assert.Equal("msq 4 player test", bound.DisplayName);
        Assert.Equal(requestedJobs, bound.Slots.Select(static slot => slot.RequiredJobId!.Value).ToArray());
        Assert.Equal(requestedJobs, group.Slots.Select(static slot => slot.RequiredJobId!.Value).ToArray());
        Assert.Equal("3", bound.RouletteTarget.Key);
    }

    private static DadPlannerGroup BuildGroup(int slotCount)
        => new()
        {
            GroupId = "group",
            DisplayName = "Projection test",
            Slots = Enumerable.Range(1, slotCount)
                .Select(index => Slot(index, $"Character {index}"))
                .ToList(),
        };

    private static DadPlannerGroupSlot Slot(int number, string character, bool substitute = false)
        => new()
        {
            SlotId = $"Slot{number}",
            IsSubstitute = substitute,
            RequiredAccountKey = new DadAccountKey($"account-{number}{(substitute ? "b" : string.Empty)}"),
            RequiredCharacterKey = new DadCharacterKey($"{character}@World"),
            WakePolicy = DadSchedulerWakePolicy.LaunchIfOffline,
        };
}
