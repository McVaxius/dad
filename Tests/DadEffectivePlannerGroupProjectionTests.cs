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
        Assert.Equal("Character 3@World", bound.Slots[2].RequiredCharacterKey.Value);
        Assert.Equal("Character 4@World", bound.Slots[3].RequiredCharacterKey.Value);
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
