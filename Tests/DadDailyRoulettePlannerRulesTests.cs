using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadDailyRoulettePlannerRulesTests
{
    [Fact]
    public void EnumNumericValuesRemainWireAndConfigurationCompatible()
    {
        Assert.Equal(0, (int)DadPlannerActivityMode.Msq);
        Assert.Equal(1, (int)DadPlannerActivityMode.DutySupport);
        Assert.Equal(2, (int)DadPlannerActivityMode.Trust);
        Assert.Equal(3, (int)DadPlannerActivityMode.PremadeDuty);
        Assert.Equal(4, (int)DadPlannerActivityMode.DutyPremade);
        Assert.Equal(5, (int)DadPlannerActivityMode.DailyRoulette);
#pragma warning disable CS0618
        Assert.Equal(5, (int)DadPlannerActivityMode.DailyMsqPremade);
#pragma warning restore CS0618
        Assert.Equal(6, (int)DadPlannerActivityMode.Blunderville);
        Assert.Equal(7, (int)DadPlannerActivityMode.Mogtome);
        Assert.Equal(8, (int)DadPlannerActivityMode.Commendation);
        Assert.Equal(9, (int)DadPlannerActivityMode.Astrope);
        Assert.Equal(10, (int)DadPlannerActivityMode.LocalDuty);
        Assert.Equal(11, (int)DadPlannerActivityMode.CustomDuty);
        Assert.Equal(12, (int)DadPlannerActivityMode.DutySupportLeveling);
        Assert.Equal(13, (int)DadPlannerActivityMode.TrustLeveling);
        Assert.Equal(14, (int)DadPlannerActivityMode.Squadron);
        Assert.Equal(15, (int)DadPlannerActivityMode.VariantVvd);

        Assert.Equal(0, (int)DadPlannerRunFamily.Msq);
        Assert.Equal(1, (int)DadPlannerRunFamily.LevelingNpc);
        Assert.Equal(2, (int)DadPlannerRunFamily.DutyFinder);
        Assert.Equal(3, (int)DadPlannerRunFamily.FarmLoops);
        Assert.Equal(4, (int)DadPlannerRunFamily.Event);
        Assert.Equal(5, (int)DadPlannerRunFamily.DailyRoulette);
        Assert.Equal(6, (int)DadModuleId.DailyMsq);
    }

    [Fact]
    public void AvailableSelectionCanonicalizesIdentityWithoutMutatingSource()
    {
        var source = Target(
            rouletteId: 1,
            key: "saved-key",
            displayName: "Saved Leveling Name",
            schemaVersion: 4);
        var options = new[] { Option(1, "Leveling", sortKey: 30) };

        var resolution = DadDailyRoulettePlannerRules.ResolveTarget(source, options);

        Assert.True(resolution.IsAvailable);
        Assert.False(resolution.ResolvedLegacyMainScenario);
        Assert.Equal(string.Empty, resolution.Blocker);
        Assert.Equal((uint)1, resolution.Target.RouletteId);
        Assert.Equal("ContentRoulette:1", resolution.Target.Key);
        Assert.Equal("Leveling", resolution.Target.DisplayName);
        Assert.Equal(4, resolution.Target.SchemaVersion);
        Assert.NotSame(source, resolution.Target);
        Assert.NotSame(options[0], resolution.Option);

        Assert.Equal("saved-key", source.Key);
        Assert.Equal("Saved Leveling Name", source.DisplayName);
    }

    [Fact]
    public void LegacyMainScenarioSelectionResolvesIdZeroToCanonicalRowThree()
    {
        var legacy = Target(
            rouletteId: 0,
            key: "  MainScenario  ",
            displayName: "Main Scenario Roulette");

        var resolution = DadDailyRoulettePlannerRules.ResolveTarget(
            legacy,
            [Option(DadRouletteCatalogProjection.MainScenarioRouletteId, "Main Scenario")]);

        Assert.True(resolution.IsAvailable);
        Assert.True(resolution.ResolvedLegacyMainScenario);
        Assert.Equal((uint)3, resolution.Target.RouletteId);
        Assert.Equal("ContentRoulette:3", resolution.Target.Key);
        Assert.Equal("Main Scenario", resolution.Target.DisplayName);
        Assert.Equal((uint)0, legacy.RouletteId);
        Assert.Equal("  MainScenario  ", legacy.Key);

        var task = DadDailyRoulettePlannerRules.BuildWireCompatibleTask(resolution.Target);
        Assert.Equal("Daily Roulette", task.LanPartyPreset);
        Assert.Equal(DadQueueTargetKind.Roulette, task.QueueTarget.Kind);
        Assert.Equal((uint)3, task.QueueTarget.RouletteId);
        Assert.Equal("ContentRoulette:3", task.QueueTarget.Key);
        Assert.Equal("Main Scenario", task.QueueTarget.DisplayName);
        Assert.NotSame(resolution.Target, task.QueueTarget);
    }

    [Fact]
    public void StaleNonzeroSelectionIsPreservedAndBlockedInsteadOfRetargeted()
    {
        var stale = Target(
            rouletteId: 77,
            key: "ContentRoulette:77",
            displayName: "Saved Future Roulette",
            schemaVersion: 3);

        var resolution = DadDailyRoulettePlannerRules.ResolveTarget(
            stale,
            [Option(3, "Main Scenario")]);

        Assert.False(resolution.IsAvailable);
        Assert.False(resolution.ResolvedLegacyMainScenario);
        Assert.Contains("unavailable", resolution.Blocker, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((uint)77, resolution.Target.RouletteId);
        Assert.Equal("ContentRoulette:77", resolution.Target.Key);
        Assert.Equal("Saved Future Roulette", resolution.Target.DisplayName);
        Assert.Equal(3, resolution.Target.SchemaVersion);
        Assert.NotSame(stale, resolution.Target);

        var unavailable = Assert.IsType<DadPlannerRouletteOption>(resolution.Option);
        Assert.False(unavailable.IsAvailable);
        Assert.Equal((uint)77, unavailable.RouletteId);
        Assert.Equal("ContentRoulette:77", unavailable.Key);
        Assert.Equal("Saved Future Roulette", unavailable.DisplayName);
        Assert.Equal(resolution.Blocker, unavailable.UnavailableReason);
    }

    [Fact]
    public void StaleSelectionWithLegacyKeyButNonzeroIdNeverSilentlyBecomesMainScenario()
    {
        var stale = Target(
            rouletteId: 99,
            key: DadRouletteCatalogProjection.MainScenarioLegacyKey,
            displayName: "Old Saved Target");

        var resolution = DadDailyRoulettePlannerRules.ResolveTarget(
            stale,
            [Option(3, "Main Scenario")]);

        Assert.False(resolution.IsAvailable);
        Assert.False(resolution.ResolvedLegacyMainScenario);
        Assert.Equal((uint)99, resolution.Target.RouletteId);
        Assert.Equal(DadRouletteCatalogProjection.MainScenarioLegacyKey, resolution.Target.Key);
        Assert.Equal("Old Saved Target", resolution.Target.DisplayName);
    }

    [Fact]
    public void WrongTargetKindIsPreservedAndBlockedBeforeIdResolution()
    {
        var wrongKind = Target(3, "ContentRoulette:3", "Main Scenario");
        wrongKind.Kind = DadQueueTargetKind.DutyFinderDuty;
        wrongKind.ContentFinderConditionId = 4;

        var resolution = DadDailyRoulettePlannerRules.ResolveTarget(
            wrongKind,
            [Option(3, "Main Scenario")]);

        Assert.False(resolution.IsAvailable);
        Assert.Null(resolution.Option);
        Assert.Contains("target kind", resolution.Blocker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(DadQueueTargetKind.DutyFinderDuty), resolution.Blocker, StringComparison.Ordinal);
        Assert.Equal(DadQueueTargetKind.DutyFinderDuty, resolution.Target.Kind);
        Assert.Equal((uint)4, resolution.Target.ContentFinderConditionId);
        Assert.Equal((uint)3, resolution.Target.RouletteId);
    }

    [Theory]
    [InlineData(0u, "")]
    [InlineData(256u, "ContentRoulette:256")]
    public void MissingOrOutOfRangeSelectionIsBlocked(uint rouletteId, string key)
    {
        var resolution = DadDailyRoulettePlannerRules.ResolveTarget(
            Target(rouletteId, key, string.Empty),
            []);

        Assert.False(resolution.IsAvailable);
        Assert.NotEmpty(resolution.Blocker);
        if (rouletteId == 0)
        {
            Assert.Null(resolution.Option);
            Assert.Contains("requires a roulette selection", resolution.Blocker, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.False(Assert.IsType<DadPlannerRouletteOption>(resolution.Option).IsAvailable);
            Assert.Contains("byte range", resolution.Blocker, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(rouletteId, resolution.Target.RouletteId);
        }
    }

    [Fact]
    public void WireCompatibleTaskDeepClonesTarget()
    {
        var target = Target(8, "ContentRoulette:8", "Level Cap Dungeons", schemaVersion: 7);

        var task = DadDailyRoulettePlannerRules.BuildWireCompatibleTask(target);
        target.RouletteId = 5;
        target.Key = "mutated";
        target.DisplayName = "mutated";

        Assert.Equal("Daily Roulette", task.LanPartyPreset);
        Assert.Equal(7, task.QueueTarget.SchemaVersion);
        Assert.Equal((uint)8, task.QueueTarget.RouletteId);
        Assert.Equal("ContentRoulette:8", task.QueueTarget.Key);
        Assert.Equal("Level Cap Dungeons", task.QueueTarget.DisplayName);
    }

    [Fact]
    public void ExistingDailyMsqRequestCarrierRoundTripsExactRouletteIdentity()
    {
        var request = new DadRunRequest
        {
            RequestId = "daily-roulette-round-trip",
            DailyMsq = DadDailyRoulettePlannerRules.BuildWireCompatibleTask(
                Target(5, "ContentRoulette:5", "Expert", schemaVersion: 6)),
        };

        var json = DadIpcJson.Serialize(request);
        var restored = DadIpcJson.Deserialize<DadRunRequest>(json);

        Assert.NotNull(restored);
        Assert.Equal("daily-roulette-round-trip", restored.RequestId);
        Assert.NotNull(restored.DailyMsq);
        Assert.Equal("Daily Roulette", restored.DailyMsq.LanPartyPreset);
        Assert.Equal(6, restored.DailyMsq.QueueTarget.SchemaVersion);
        Assert.Equal(DadQueueTargetKind.Roulette, restored.DailyMsq.QueueTarget.Kind);
        Assert.Equal((uint)5, restored.DailyMsq.QueueTarget.RouletteId);
        Assert.Equal("ContentRoulette:5", restored.DailyMsq.QueueTarget.Key);
        Assert.Equal("Expert", restored.DailyMsq.QueueTarget.DisplayName);
        Assert.Equal(1, restored.GetConfiguredTaskCount());
        Assert.Null(restored.Msq);
    }

    private static DadQueueTarget Target(
        uint rouletteId,
        string key,
        string displayName,
        int schemaVersion = 1)
        => new()
        {
            SchemaVersion = schemaVersion,
            Kind = DadQueueTargetKind.Roulette,
            RouletteId = rouletteId,
            Key = key,
            DisplayName = displayName,
        };

    private static DadPlannerRouletteOption Option(uint rouletteId, string displayName, int sortKey = 1)
        => new()
        {
            RouletteId = rouletteId,
            Key = DadRouletteCatalogProjection.BuildCanonicalKey(rouletteId),
            DisplayName = displayName,
            SortKey = sortKey,
            IsAvailable = true,
        };
}
