using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadPlannerSlotRulesTests
{
    [Fact]
    public void NormalizeGroupSlotsMigratesLegacyLabelsToStrictSlotIds()
    {
        var slots = DadPlannerSlotRules.NormalizeGroupSlots(
        [
            new DadPlannerGroupSlot { SlotId = "Leader" },
            new DadPlannerGroupSlot { SlotId = "Party 2" },
            new DadPlannerGroupSlot { SlotId = "Slot8" },
            new DadPlannerGroupSlot { SlotId = "Runner" },
            new DadPlannerGroupSlot { SlotId = "DPS 1" },
        ]);

        Assert.Collection(
            slots,
            slot => Assert.Equal("Slot1", slot.SlotId),
            slot => Assert.Equal("Slot2", slot.SlotId),
            slot => Assert.Equal("Slot3", slot.SlotId),
            slot => Assert.Equal("Slot4", slot.SlotId),
            slot => Assert.Equal("Slot5", slot.SlotId));
        Assert.All(slots, static slot => Assert.False(slot.IsSubstitute));
    }

    [Fact]
    public void PrimaryRowsAreSequentialEvenWhenStoredSlotIdsHaveGaps()
    {
        var slots = DadPlannerSlotRules.NormalizeGroupSlots(
        [
            new DadPlannerGroupSlot { SlotId = "Slot1", RequiredCharacterKey = new DadCharacterKey("first") },
            new DadPlannerGroupSlot { SlotId = "Slot8", RequiredCharacterKey = new DadCharacterKey("second") },
            new DadPlannerGroupSlot { SlotId = "Slot56", RequiredCharacterKey = new DadCharacterKey("third") },
        ]);

        Assert.Equal(["Slot1", "Slot2", "Slot3"], slots.Select(static slot => slot.SlotId).ToArray());
        Assert.Equal(["first", "second", "third"], slots.Select(static slot => slot.RequiredCharacterKey.Value).ToArray());
    }

    [Fact]
    public void DuplicateStrictSlotsBecomeExplicitSubstituteRows()
    {
        var slots = DadPlannerSlotRules.NormalizeGroupSlots(
        [
            new DadPlannerGroupSlot { SlotId = "Slot1", RequiredCharacterKey = new DadCharacterKey("primary") },
            new DadPlannerGroupSlot { SlotId = "Slot1", RequiredCharacterKey = new DadCharacterKey("sub-a") },
            new DadPlannerGroupSlot { SlotId = "Party 1", RequiredCharacterKey = new DadCharacterKey("sub-b") },
        ]);

        Assert.Collection(
            slots,
            slot =>
            {
                Assert.Equal("Slot1", slot.SlotId);
                Assert.False(slot.IsSubstitute);
                Assert.Equal("primary", slot.RequiredCharacterKey.Value);
            },
            slot =>
            {
                Assert.Equal("Slot1", slot.SlotId);
                Assert.True(slot.IsSubstitute);
                Assert.Equal("sub-a", slot.RequiredCharacterKey.Value);
            },
            slot =>
            {
                Assert.Equal("Slot1", slot.SlotId);
                Assert.True(slot.IsSubstitute);
                Assert.Equal("sub-b", slot.RequiredCharacterKey.Value);
            });
    }

    [Fact]
    public void AllowSubstitutionIsPreservedButDoesNotCreateSubstituteRows()
    {
        var slot = Assert.Single(DadPlannerSlotRules.NormalizeGroupSlots(
        [
            new DadPlannerGroupSlot { SlotId = "Leader", AllowSubstitution = true },
        ]));

        Assert.Equal("Slot1", slot.SlotId);
        Assert.True(slot.AllowSubstitution);
        Assert.False(slot.IsSubstitute);
    }

    [Fact]
    public void NormalizePreservesIndependentPrimaryAndSubstituteJobSelections()
    {
        var slots = DadPlannerSlotRules.NormalizeGroupSlots(
        [
            new DadPlannerGroupSlot
            {
                SlotId = "Slot1",
                RequiredCharacterKey = new DadCharacterKey("primary"),
                RequiredJobId = 21,
            },
            new DadPlannerGroupSlot
            {
                SlotId = "Slot1",
                IsSubstitute = true,
                RequiredCharacterKey = new DadCharacterKey("substitute"),
                RequiredJobId = 24,
            },
        ]);

        Assert.Collection(
            slots,
            slot => Assert.Equal((uint?)21, slot.RequiredJobId),
            slot => Assert.Equal((uint?)24, slot.RequiredJobId));
    }

    [Fact]
    public void NextPrimarySlotNumberHonorsSlot56HardCap()
    {
        var full = Enumerable.Range(1, DadPlannerSlotRules.MaxSlotNumber)
            .Select(slotNumber => new DadPlannerGroupSlot { SlotId = DadPlannerSlotRules.FormatSlotId(slotNumber) })
            .ToList();
        var overfull = Enumerable.Range(1, DadPlannerSlotRules.MaxSlotNumber + 2)
            .Select(slotNumber => new DadPlannerGroupSlot { SlotId = $"Runner {slotNumber}" })
            .ToList();

        Assert.Equal(0, DadPlannerSlotRules.NextPrimarySlotNumber(full));
        Assert.Equal("Slot56", DadPlannerSlotRules.FormatSlotId(99));
        Assert.Equal(DadPlannerSlotRules.MaxSlotNumber, DadPlannerSlotRules.CountPrimarySlots(overfull));
    }

    [Fact]
    public void CapKeepsSubstitutesOnlyForIncludedPrimarySlots()
    {
        var slots = DadPlannerSlotRules.TakePrimarySlotsWithSubstitutes(
        [
            new DadPlannerGroupSlot { SlotId = "Slot1" },
            new DadPlannerGroupSlot { SlotId = "Slot1", IsSubstitute = true },
            new DadPlannerGroupSlot { SlotId = "Slot2" },
            new DadPlannerGroupSlot { SlotId = "Slot3" },
            new DadPlannerGroupSlot { SlotId = "Slot3", IsSubstitute = true },
            new DadPlannerGroupSlot { SlotId = "Slot4", IsSubstitute = true },
        ], 2);

        Assert.Equal(["Slot1", "Slot1", "Slot2"], slots.Select(static slot => slot.SlotId).ToArray());
        Assert.Equal([false, true, false], slots.Select(static slot => slot.IsSubstitute).ToArray());
    }

    [Fact]
    public void DailyRouletteEffectiveSlotCapIsExactlyFourPrimaries()
    {
        var savedSlots = Enumerable.Range(1, 6)
            .Select(slot => new DadPlannerGroupSlot
            {
                SlotId = $"Slot{slot}",
                RequiredCharacterKey = new DadCharacterKey($"Character {slot}@Alpha"),
            })
            .ToList();
        savedSlots.Add(new DadPlannerGroupSlot
        {
            SlotId = "Slot4",
            IsSubstitute = true,
            RequiredCharacterKey = new DadCharacterKey("Slot Four Substitute@Alpha"),
        });
        savedSlots.Add(new DadPlannerGroupSlot
        {
            SlotId = "Slot5",
            IsSubstitute = true,
            RequiredCharacterKey = new DadCharacterKey("Slot Five Substitute@Alpha"),
        });

        var effective = DadPlannerSlotRules.TakePrimarySlotsWithSubstitutes(
            savedSlots,
            DadDailyRoulettePlannerRules.RequiredPartySize);

        Assert.Equal(4, DadPlannerSlotRules.CountPrimarySlots(effective));
        Assert.Equal(
            ["Slot1", "Slot2", "Slot3", "Slot4", "Slot4"],
            effective.Select(static slot => slot.SlotId).ToArray());
        Assert.DoesNotContain(effective, static slot => slot.SlotId == "Slot5");
        Assert.Equal(
            "Slot Four Substitute@Alpha",
            effective.Single(static slot => slot.IsSubstitute).RequiredCharacterKey.Value);
    }

    [Fact]
    public void SubstituteRowsStayUnderTheirSequentialPrimarySlotInLegacySlotOrder()
    {
        var slots = DadPlannerSlotRules.NormalizeGroupSlots(
        [
            new DadPlannerGroupSlot { SlotId = "Slot4", RequiredCharacterKey = new DadCharacterKey("primary-a") },
            new DadPlannerGroupSlot { SlotId = "Slot2", RequiredCharacterKey = new DadCharacterKey("primary-b") },
            new DadPlannerGroupSlot { SlotId = "Slot4", IsSubstitute = true, RequiredCharacterKey = new DadCharacterKey("sub-a1") },
            new DadPlannerGroupSlot { SlotId = "Slot4", IsSubstitute = true, RequiredCharacterKey = new DadCharacterKey("sub-a2") },
            new DadPlannerGroupSlot { SlotId = "Slot2", IsSubstitute = true, RequiredCharacterKey = new DadCharacterKey("sub-b1") },
        ]);

        Assert.Equal(
            ["Slot1", "Slot1", "Slot2", "Slot2", "Slot2"],
            slots.Select(static slot => slot.SlotId).ToArray());
        Assert.Equal(
            ["primary-b", "sub-b1", "primary-a", "sub-a1", "sub-a2"],
            slots.Select(static slot => slot.RequiredCharacterKey.Value).ToArray());
        Assert.Equal([false, true, false, true, true], slots.Select(static slot => slot.IsSubstitute).ToArray());
    }

    [Fact]
    public void RowsForSlotArePrimaryFirstAndSameSlotOnly()
    {
        var rows = DadPlannerSlotRules.GetRowsForSlot(
        [
            new DadPlannerGroupSlot { SlotId = "Slot1", RequiredCharacterKey = new DadCharacterKey("primary") },
            new DadPlannerGroupSlot { SlotId = "Slot2", RequiredCharacterKey = new DadCharacterKey("other-slot") },
            new DadPlannerGroupSlot { SlotId = "Slot1", IsSubstitute = true, RequiredCharacterKey = new DadCharacterKey("sub") },
        ], "Slot1");

        Assert.Collection(
            rows,
            row =>
            {
                Assert.False(row.IsSubstitute);
                Assert.Equal("primary", row.RequiredCharacterKey.Value);
            },
            row =>
            {
                Assert.True(row.IsSubstitute);
                Assert.Equal("sub", row.RequiredCharacterKey.Value);
            });
    }
}
