using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadLocalDutySupportRulesTests
{
    [Fact]
    public void ExactOwnAccountSoloPresetAndPureScheduleAreAllowed()
    {
        var first = Preset("first");
        var second = Preset("second");
        var schedule = Schedule(first, second);
        Assert.True(DadLocalDutySupportRules.CanRunPreset(first, "own-account"));
        Assert.True(DadLocalDutySupportRules.CanRunSchedule(schedule, [first, second], "own-account"));
        second.ShoppingAssociations.Add(new() { CustomCommand = "/fixture" });
        Assert.False(DadLocalDutySupportRules.CanRunPreset(second, "own-account"));
        Assert.False(DadLocalDutySupportRules.CanRunSchedule(schedule, [first, second], "own-account"));
    }

    [Theory]
    [InlineData(DadPlannerActivityMode.DailyRoulette)]
    [InlineData(DadPlannerActivityMode.Trust)]
    [InlineData(DadPlannerActivityMode.PremadeDuty)]
    [InlineData(DadPlannerActivityMode.Msq)]
    [InlineData(DadPlannerActivityMode.LocalDuty)]
    public void OtherLanesAndMixedSchedulesRemainCoordinatorOnly(DadPlannerActivityMode activity)
    {
        var first = Preset("first");
        var second = Preset("second");
        second.ActivityMode = activity;
        Assert.False(DadLocalDutySupportRules.CanRunPreset(second, "own-account"));
        Assert.False(DadLocalDutySupportRules.CanRunSchedule(Schedule(first, second), [first, second], "own-account"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ForeignUnresolvedMultiSlotTemplateAndFormationPresetsAreRejected(int invalid)
    {
        var group = Preset("first");
        switch (invalid)
        {
            case 0: group.Slots[0].RequiredAccountKey = new("foreign-account"); break;
            case 1: group.Slots[0].RequiredAccountKey = new(string.Empty); break;
            case 2: group.Slots[0].RequiredCharacterKey = new(string.Empty); break;
            case 3: group.Slots.Add(new() { SlotId = "Slot2", RequiredAccountKey = new("own-account"), RequiredCharacterKey = new("Other@World") }); break;
            case 4: group.IsTemplate = true; break;
            case 5: group.AutoPartyFormationOnly = true; break;
            case 6: group.DutyExpectedPartySize = 4; break;
            case 7: group.RunFamily = DadPlannerRunFamily.Msq; break;
            case 8: group.TransportOwner = (DadTransportOwner)999; break;
        }
        Assert.False(DadLocalDutySupportRules.CanRunPreset(group, "own-account"));
        Assert.False(DadLocalDutySupportRules.CanRunSchedule(Schedule(group), [group], "own-account"));
    }

    [Fact]
    public void SubstitutesMayNotCrossAccountBoundary()
    {
        var group = Preset("first");
        group.Slots.Add(new() { SlotId = "Slot1", IsSubstitute = true, RequiredAccountKey = new("own-account"), RequiredCharacterKey = new("Substitute@World") });
        Assert.True(DadLocalDutySupportRules.CanRunPreset(group, "own-account"));
        group.Slots[1].RequiredAccountKey = new("foreign-account");
        Assert.False(DadLocalDutySupportRules.CanRunPreset(group, "own-account"));
    }

    [Fact]
    public void EmptyMissingAndAmbiguousScheduleEntriesFailClosed()
    {
        var group = Preset("first");
        Assert.False(DadLocalDutySupportRules.CanRunSchedule(new(), [group], "own-account"));
        Assert.False(DadLocalDutySupportRules.CanRunSchedule(Schedule(group), [], "own-account"));
        Assert.False(DadLocalDutySupportRules.CanRunSchedule(Schedule(group), [group, group], "own-account"));
        Assert.False(DadLocalDutySupportRules.CanRunPreset(group, string.Empty));
    }

    [Fact]
    public void ShoppingAndSharedRuntimeIdentitiesDoNotInheritClientPermission()
    {
        var group = Preset("first");
        var schedule = Schedule(group);
        schedule.ShoppingAssociation = new();
        Assert.False(DadLocalDutySupportRules.CanRunSchedule(schedule, [group], "own-account"));
        group.ShoppingAssociation = new();
        Assert.False(DadLocalDutySupportRules.CanRunPreset(group, "own-account"));
        group.ShoppingAssociation = null;
        group.Slots[0].SharedIdentity = new();
        Assert.False(DadLocalDutySupportRules.CanRunPreset(group, "own-account"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void SoloRoleExceptionPreservesEveryAdmissionConflict(int conflict)
    {
        var blocker = DadSchedulerRoutingRules.GetAdmissionBlocker(
            isCoordinator: false,
            schedulerActive: conflict == 0,
            crewFormationActive: conflict == 1,
            standaloneDisbandActive: conflict == 2,
            visibleRunActive: conflict == 3,
            schedulerCleanupPending: conflict == 4,
            coordinatorCleanupPending: conflict == 5,
            localDutySupport: true);
        Assert.Equal(conflict == 6, string.IsNullOrWhiteSpace(blocker));
    }

    [Fact]
    public void ClientWithoutScopedExceptionRemainsRejected()
        => Assert.Contains("Coordinator", DadSchedulerRoutingRules.GetAdmissionBlocker(
            false, false, false, false, false, false, false));

    [Theory]
    [InlineData(DadSchedulerJobType.MapCrew)]
    [InlineData(DadSchedulerJobType.RosterUpdate)]
    public void OtherQueuedWorkDoesNotInheritSoloSchedulingPermission(DadSchedulerJobType jobType)
        => Assert.False(DadLocalDutySupportRules.CanRunQueuedJob(new() { JobType = jobType }, Preset("first"), "own-account"));

    private static DadPlannerGroup Preset(string id) => new()
    {
        GroupId = id,
        ActivityMode = DadPlannerActivityMode.DutySupport,
        DutyExpectedPartySize = 1,
        Slots = [new() { SlotId = "Slot1", RequiredAccountKey = new("own-account"), RequiredCharacterKey = new("Character@World") }],
    };

    private static DadScheduleDefinition Schedule(params DadPlannerGroup[] groups)
        => new() { Entries = groups.Select(group => new DadScheduleEntry { GroupId = group.GroupId }).ToList() };
}
