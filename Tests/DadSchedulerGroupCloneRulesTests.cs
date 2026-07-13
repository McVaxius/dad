using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadSchedulerGroupCloneRulesTests
{
    [Fact]
    public void NormalizedLaunchProfileSnapshotDoesNotMutateSavedProfiles()
    {
        var source = new DadLaunchProfile
        {
            ProfileId = "  profile-id  ",
            DisplayName = "  Profile Name  ",
            BatchPath = "  C:\\Dad\\launch.bat  ",
            TimeoutSeconds = 1,
        };

        var snapshot = DadSchedulerGroupCloneRules.CloneNormalizedLaunchProfiles([source]);

        var normalized = Assert.Single(snapshot);
        Assert.Equal("profile-id", normalized.ProfileId);
        Assert.Equal("Profile Name", normalized.DisplayName);
        Assert.Equal("C:\\Dad\\launch.bat", normalized.BatchPath);
        Assert.Equal(30, normalized.TimeoutSeconds);
        Assert.Equal("  profile-id  ", source.ProfileId);
        Assert.Equal("  Profile Name  ", source.DisplayName);
        Assert.Equal("  C:\\Dad\\launch.bat  ", source.BatchPath);
        Assert.Equal(1, source.TimeoutSeconds);
    }

    [Fact]
    public void SchedulerClonePreservesExactRouletteTargetAndOwnsDeepCopies()
    {
        var target = new DadQueueTarget
        {
            SchemaVersion = 7,
            Kind = DadQueueTargetKind.Roulette,
            RouletteId = 1,
            Key = "ContentRoulette:1",
            DisplayName = "Leveling",
        };
        var source = new DadPlannerGroup
        {
            GroupId = "daily-leveling",
            RunFamily = DadPlannerRunFamily.DailyRoulette,
            ActivityMode = DadPlannerActivityMode.DailyRoulette,
            TransportOwner = DadTransportOwner.LanParty,
            QueueAuthority = DadQueueAuthority.Leader,
            DutyUnsynced = false,
            DutyExpectedPartySize = 4,
            RouletteTarget = target,
            StopPolicy = new DadRunStopPolicy { AfterRuns = 2 },
            Slots =
            [
                new DadPlannerGroupSlot
                {
                    SlotId = "Slot1",
                    RequiredCharacterKey = new DadCharacterKey("Leader@Alpha"),
                    CharacterLoadInstruction = new DadCharacterLoadInstruction { CommandTemplate = "/load leader" },
                },
            ],
        };

        var clone = DadSchedulerGroupCloneRules.CloneWithSlots(source, source.Slots);

        Assert.Equal(DadPlannerRunFamily.DailyRoulette, clone.RunFamily);
        Assert.Equal(DadPlannerActivityMode.DailyRoulette, clone.ActivityMode);
        Assert.Equal(DadTransportOwner.LanParty, clone.TransportOwner);
        Assert.Equal(DadQueueAuthority.Leader, clone.QueueAuthority);
        Assert.False(clone.DutyUnsynced);
        Assert.Equal(4, clone.DutyExpectedPartySize);
        Assert.NotSame(target, clone.RouletteTarget);
        Assert.Equal(7, clone.RouletteTarget.SchemaVersion);
        Assert.Equal(DadQueueTargetKind.Roulette, clone.RouletteTarget.Kind);
        Assert.Equal((uint)1, clone.RouletteTarget.RouletteId);
        Assert.Equal("ContentRoulette:1", clone.RouletteTarget.Key);
        Assert.Equal("Leveling", clone.RouletteTarget.DisplayName);
        Assert.NotSame(source.StopPolicy, clone.StopPolicy);
        Assert.NotSame(source.Slots[0], clone.Slots[0]);
        Assert.NotSame(source.Slots[0].CharacterLoadInstruction, clone.Slots[0].CharacterLoadInstruction);

        target.RouletteId = 3;
        source.Slots[0].CharacterLoadInstruction.CommandTemplate = "/mutated";
        Assert.Equal((uint)1, clone.RouletteTarget.RouletteId);
        Assert.Equal("/load leader", clone.Slots[0].CharacterLoadInstruction.CommandTemplate);
    }
}
