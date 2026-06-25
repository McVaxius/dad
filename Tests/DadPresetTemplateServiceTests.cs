using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadPresetTemplateServiceTests
{
    [Fact]
    public void InstantiateBindsAccountsAndAvoidsDuplicateCharactersAndAccounts()
    {
        var template = new DadPlannerGroup
        {
            DisplayName = "Template",
            IsTemplate = true,
            Slots =
            [
                new DadPlannerGroupSlot { SlotId = "Leader", RequiredRole = DadPartyRole.Tank },
                new DadPlannerGroupSlot { SlotId = "Party 2", RequiredRole = DadPartyRole.Dps },
                new DadPlannerGroupSlot { SlotId = "Party 3", RequiredRole = DadPartyRole.Dps },
            ],
        };
        var pool = new DadCharacterPool
        {
            Characters =
            [
                Character("Leader Tank@Alpha", "acct-tank", "WAR", DadCharacterSource.PeerRuntime),
                Character("Dps One@Alpha", "acct-dps-a", "RPR", DadCharacterSource.PeerRuntime),
                Character("Dps Alt@Alpha", "acct-dps-a", "MCH", DadCharacterSource.PeerRuntime),
                Character("Dps Two@Alpha", "acct-dps-b", "BLM", DadCharacterSource.PeerRuntime),
            ],
        };

        var instance = DadPresetTemplateService.Instantiate(template, pool, new DateTime(2026, 6, 24, 0, 0, 0, DateTimeKind.Utc));
        var assigned = instance.Slots.Where(static slot => !slot.RequiredCharacterKey.IsEmpty).ToList();

        Assert.False(instance.IsTemplate);
        Assert.Equal("Leader Tank@Alpha", instance.Slots[0].RequiredCharacterKey.Value);
        Assert.Equal("acct-tank", instance.Slots[0].RequiredAccountKey.Value);
        Assert.Equal(3, assigned.Count);
        Assert.Equal(assigned.Count, assigned.Select(static slot => slot.RequiredCharacterKey.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(assigned.Count, assigned.Select(static slot => slot.RequiredAccountKey.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void InstantiatePrefersLiveReadyRosterEntries()
    {
        var template = new DadPlannerGroup
        {
            Slots = [new DadPlannerGroupSlot { SlotId = "Leader", RequiredRole = DadPartyRole.Tank }],
        };
        var pool = new DadCharacterPool
        {
            Characters =
            [
                Character("Stale Tank@Alpha", "acct-stale", "WAR", DadCharacterSource.XadbOnly, DadSnapshotFreshness.Stale),
                Character("Live Tank@Alpha", "acct-live", "WAR", DadCharacterSource.PeerRuntime),
            ],
        };

        var instance = DadPresetTemplateService.Instantiate(template, pool, DateTime.UtcNow);

        Assert.Equal("Live Tank@Alpha", instance.Slots[0].RequiredCharacterKey.Value);
        Assert.Equal("acct-live", instance.Slots[0].RequiredAccountKey.Value);
    }

    private static DadAcquiredCharacter Character(
        string key,
        string accountId,
        string job,
        DadCharacterSource source,
        DadSnapshotFreshness freshness = DadSnapshotFreshness.Live)
        => new()
        {
            CharacterKey = key,
            CharacterName = key.Split('@')[0],
            WorldName = key.Contains('@', StringComparison.Ordinal) ? key.Split('@')[1] : "Alpha",
            AccountId = accountId,
            AccountAlias = accountId,
            CurrentJobAbbrev = job,
            CurrentLevel = 90,
            Source = source,
            Freshness = freshness,
            Readiness = DadReadinessState.Ready,
        };
}
