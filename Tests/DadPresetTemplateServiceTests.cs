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

    [Fact]
    public void CreateTemplateClearsCharacterSpecificJobSelection()
    {
        var source = new DadPlannerGroup
        {
            DisplayName = "Saved roster",
            Slots =
            [
                new DadPlannerGroupSlot
                {
                    SlotId = "Slot1",
                    RequiredAccountKey = new DadAccountKey("account-a"),
                    RequiredCharacterKey = new DadCharacterKey("Character@Alpha"),
                    RequiredJobId = 21,
                },
            ],
        };

        var template = DadPresetTemplateService.CreateTemplateFrom(source, "Reusable", DateTime.UtcNow);
        var slot = Assert.Single(template.Slots);

        Assert.True(slot.RequiredAccountKey.IsEmpty);
        Assert.True(slot.RequiredCharacterKey.IsEmpty);
        Assert.Null(slot.RequiredJobId);
    }

    [Fact]
    public void TemplateCreationAndInstantiationDeepCloneDailyRouletteTarget()
    {
        var sourceTarget = new DadQueueTarget
        {
            SchemaVersion = 3,
            Kind = DadQueueTargetKind.Roulette,
            RouletteId = 5,
            Key = "ContentRoulette:5",
            DisplayName = "Expert",
        };
        var source = new DadPlannerGroup
        {
            DisplayName = "Expert roulette",
            RunFamily = DadPlannerRunFamily.DailyRoulette,
            ActivityMode = DadPlannerActivityMode.DailyRoulette,
            RouletteTarget = sourceTarget,
            Slots = Enumerable.Range(1, DadDailyRoulettePlannerRules.RequiredPartySize)
                .Select(slot => new DadPlannerGroupSlot
                {
                    SlotId = $"Slot{slot}",
                    RequiredRole = slot == 1 ? DadPartyRole.Tank : DadPartyRole.Any,
                })
                .ToList(),
        };

        var template = DadPresetTemplateService.CreateTemplateFrom(source, "Expert template", DateTime.UtcNow);
        var instance = DadPresetTemplateService.Instantiate(template, new DadCharacterPool(), DateTime.UtcNow);

        Assert.Equal(DadPlannerRunFamily.DailyRoulette, template.RunFamily);
        Assert.Equal(DadPlannerActivityMode.DailyRoulette, template.ActivityMode);
        Assert.NotSame(sourceTarget, template.RouletteTarget);
        Assert.NotSame(template.RouletteTarget, instance.RouletteTarget);
        Assert.Equal((uint)5, template.RouletteTarget.RouletteId);
        Assert.Equal("ContentRoulette:5", template.RouletteTarget.Key);
        Assert.Equal("Expert", template.RouletteTarget.DisplayName);
        Assert.Equal(3, template.RouletteTarget.SchemaVersion);
        Assert.Equal((uint)5, instance.RouletteTarget.RouletteId);
        Assert.Equal("ContentRoulette:5", instance.RouletteTarget.Key);
        Assert.Equal("Expert", instance.RouletteTarget.DisplayName);
        Assert.Equal(DadDailyRoulettePlannerRules.RequiredPartySize, instance.Slots.Count);

        sourceTarget.RouletteId = 8;
        template.RouletteTarget.RouletteId = 3;
        Assert.Equal((uint)5, instance.RouletteTarget.RouletteId);
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
