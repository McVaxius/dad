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
        Assert.Equal(DadSchedulerWakePolicy.AlreadyOnlineOnly, merged[0].WakePolicy);
        Assert.Equal("saved-profile", merged[0].LaunchProfileId);
        Assert.Equal("/saved", merged[0].CharacterLoadInstruction.CommandTemplate);
        Assert.Equal("New Character@World", merged[1].RequiredCharacterKey.Value);
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
                },
            ],
        };

        for (var cycle = 0; cycle < 3; cycle++)
        {
            group.Slots = DadPlannerSlotRules.NormalizeGroupSlots(group.Slots);
            group = DadIpcJson.Deserialize<DadPlannerGroup>(DadIpcJson.Serialize(group))!;
        }

        Assert.Equal(DadSchedulerWakePolicy.AlreadyOnlineOnly, Assert.Single(group.Slots).WakePolicy);
    }
}
