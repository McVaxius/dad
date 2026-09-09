extern alias DadRuntime;
using Xunit;
using DadRuntime::dad.Models;
using DadRuntime::dad.Services;

namespace dad.Tests;

public sealed class DadDutySupportLevelingPresetsTests
{
    private static readonly DadPlannerDutyOption[] Catalog =
    [
        new() { TerritoryType = 1036, ContentFinderConditionId = 4, DutyDisplayName = "First", JobLevelRequired = 15, SupportsDutySupport = true },
        new() { TerritoryType = 1037, ContentFinderConditionId = 6, DutyDisplayName = "Second", JobLevelRequired = 16, ItemLevelRequired = 10, SupportsDutySupport = true },
        new() { TerritoryType = 1048, ContentFinderConditionId = 99, DutyDisplayName = "Manual alternative", JobLevelRequired = 50, SupportsDutySupport = true },
    ];

    [Theory]
    [InlineData(1, 15, 9, 4)]
    [InlineData(26, 16, 10, 6)]
    [InlineData(19, 16, 9, 4)]
    [InlineData(19, 50, 100, 6)]
    public void SelectsCurrentCombatClassOrJobByActualLevelAndEquipment(uint job, int level, int itemLevel, uint cfc)
    {
        var result = DadDutySupportLevelingPresets.Select(new(job, level, itemLevel), Lookup, _ => true, out var blocker);
        Assert.Equal(cfc, result?.ContentFinderConditionId);
        Assert.Empty(blocker);
    }

    [Fact]
    public void FiltersUnlocksAndSupportAndUsesCfcForUnlockEvidence()
    {
        var queried = new List<uint>();
        var result = DadDutySupportLevelingPresets.Select(new(19, 30, 100), Lookup, id =>
        { queried.Add(id); return id == 4; }, out var blocker);
        Assert.Equal(4u, result?.ContentFinderConditionId);
        Assert.Equal(new uint[] { 4, 6 }, queried);
        Assert.Empty(blocker);
        Assert.Null(DadDutySupportLevelingPresets.Resolve(new(1036, "Unsupported"), _ =>
            [new() { TerritoryType = 1036, ContentFinderConditionId = 123 }], out blocker));
        Assert.Contains("no Duty Support", blocker);
        Assert.Null(DadDutySupportLevelingPresets.Resolve(new(1036, "Ambiguous"), _ => [Catalog[0], Catalog[0]], out blocker));
        Assert.Contains("multiple", blocker);
    }

    [Theory]
    [InlineData(0, 15, 10, "unavailable")]
    [InlineData(19, 0, 10, "unavailable")]
    [InlineData(19, 15, 0, "unavailable")]
    [InlineData(19, 14, 10, "level 15")]
    [InlineData(8, 20, 10, "combat")]
    [InlineData(36, 20, 10, "Blue Mage")]
    [InlineData(43, 20, 10, "Beastmaster")]
    public void RejectsMissingEvidenceAndIneligibleJobs(uint job, int level, int itemLevel, string expected)
    {
        Assert.Null(DadDutySupportLevelingPresets.Select(new(job, level, itemLevel), Lookup, _ => true, out var blocker));
        Assert.Contains(expected, blocker);
    }

    [Theory]
    [InlineData(false, "locked")]
    [InlineData(null, "unlock evidence")]
    public void ReportsUnavailableDuties(bool? unlocked, string expected)
    {
        Assert.Null(DadDutySupportLevelingPresets.Select(new(19, 20, 100), Lookup, _ => unlocked, out var blocker));
        Assert.Contains(expected, blocker);
    }

    [Fact]
    public void ManualCutsceneEntryCreatesOrdinaryEditableOneRunForCurrentCharacter()
    {
        var entry = Assert.Single(DadDutySupportLevelingPresets.Entries, entry => entry.ManualOnly);
        var duty = DadDutySupportLevelingPresets.Resolve(entry, Lookup, out var blocker);
        Assert.NotNull(duty);
        Assert.Empty(blocker);
        var group = DadDutySupportLevelingPresets.CreateManual(duty!, new() { AccountId = "synthetic", CharacterKey = "Synthetic Runner@Synthetic", CurrentJobId = 19 });
        Assert.Equal(99u, group.DutyContentFinderConditionId);
        Assert.Equal(DadPlannerActivityMode.DutySupport, group.ActivityMode);
        var provider = new DadPresetProviderService(new DadModuleRegistry(), () => [], dutyCatalogProvider: () => Catalog);
        Assert.Equal(DadPlannerOperatorMode.RemotePartyPlan, provider.BuildOptionsForGroup(group, null).OperatorMode);
        Assert.Equal(1, group.StopPolicy.AfterRuns);
        Assert.False(group.LevelingMode.Enabled);
        Assert.False(group.IsTemplate);
        var slot = Assert.Single(group.Slots);
        Assert.Equal("synthetic", slot.RequiredAccountKey.Value);
        Assert.Null(slot.RequiredJobId); // Current-job execution without a job-switch request.
        Assert.Equal(DadSchedulerWakePolicy.AlreadyOnlineOnly, slot.WakePolicy);
        group.StopPolicy.AfterRuns = 3;
        Assert.Equal(3, group.StopPolicy.AfterRuns);
    }

    private static IReadOnlyList<DadPlannerDutyOption> Lookup(uint territory)
        => Catalog.Where(duty => duty.TerritoryType == territory).ToArray();
}
