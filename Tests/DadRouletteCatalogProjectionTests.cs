using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRouletteCatalogProjectionTests
{
    [Fact]
    public void BuildOptionsIncludesEligibleLightPartyRoulettesWithCanonicalIdentity()
    {
        var options = DadRouletteCatalogProjection.BuildOptions(
        [
            Row(5, "Expert", sortKey: 10),
            Row(8, "Level Cap Dungeons", sortKey: 20),
            Row(2, "High-level Dungeons", sortKey: 30),
            Row(1, "Leveling", sortKey: 40),
            Row(3, "Main Scenario", sortKey: 50),
        ]);

        Assert.Collection(
            options,
            option => AssertOption(option, 5, "Expert"),
            option => AssertOption(option, 8, "Level Cap Dungeons"),
            option => AssertOption(option, 2, "High-level Dungeons"),
            option => AssertOption(option, 1, "Leveling"),
            option => AssertOption(option, 3, "Main Scenario"));
    }

    [Fact]
    public void BuildOptionsFiltersInvalidRowsAndNonLightPartyRoulettes()
    {
        var options = DadRouletteCatalogProjection.BuildOptions(
        [
            Row(1, "Eligible"),
            Row(0, "Zero row"),
            Row(256, "Above byte limit"),
            Row(2, "   "),
            Row(3, "Hidden", isInDutyFinder: false),
            Row(4, "PvP", isPvP: true),
            Row(5, "Eight player", membersPerParty: 8),
            Row(6, "Alliance", membersPerParty: 8, partyCount: 3),
            Row(7, "Three player", membersPerParty: 3),
            Row(8, "Two light parties", partyCount: 2),
        ]);

        var option = Assert.Single(options);
        Assert.Equal((uint)1, option.RouletteId);
    }

    [Fact]
    public void BuildOptionsIgnoresQueueMaxPlayers()
    {
        var options = DadRouletteCatalogProjection.BuildOptions(
        [
            Row(1, "Queue max zero", queueMaxPlayers: 0),
            Row(2, "Queue max eight", queueMaxPlayers: 8),
            Row(3, "Queue max alliance", queueMaxPlayers: 24),
        ]);

        Assert.Equal(
            new uint[] { 1, 2, 3 },
            options.Select(static option => option.RouletteId).OrderBy(static id => id));
    }

    [Fact]
    public void BuildOptionsSortsBySortKeyThenLocalizedNameThenRowId()
    {
        var options = DadRouletteCatalogProjection.BuildOptions(
        [
            Row(9, "Zulu", sortKey: 2),
            Row(8, "Beta", sortKey: 1),
            Row(7, "Alpha", sortKey: 1),
            Row(6, "Alpha", sortKey: 1),
        ]);

        Assert.Equal(new uint[] { 6, 7, 8, 9 }, options.Select(static option => option.RouletteId));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "ContentRoulette:1")]
    [InlineData(255, "ContentRoulette:255")]
    [InlineData(256, "")]
    public void BuildCanonicalKeyEnforcesDutyFinderByteRange(uint rouletteId, string expected)
        => Assert.Equal(expected, DadRouletteCatalogProjection.BuildCanonicalKey(rouletteId));

    [Fact]
    public void ResolveEligibleOptionRequiresAvailableNonzeroExactId()
    {
        var availableMainScenario = new DadPlannerRouletteOption
        {
            RouletteId = DadRouletteCatalogProjection.MainScenarioRouletteId,
            Key = "ContentRoulette:3",
            DisplayName = "Main Scenario",
        };
        var unavailable = new DadPlannerRouletteOption
        {
            RouletteId = 4,
            Key = "ContentRoulette:4",
            DisplayName = "Unavailable",
            IsAvailable = false,
        };
        var options = new[] { availableMainScenario, unavailable };

        Assert.Same(
            availableMainScenario,
            DadRouletteCatalogProjection.ResolveEligibleOption(
                options,
                DadRouletteCatalogProjection.MainScenarioRouletteId));
        Assert.Null(DadRouletteCatalogProjection.ResolveEligibleOption(options, 0));
        Assert.Null(DadRouletteCatalogProjection.ResolveEligibleOption(options, 4));
        Assert.Null(DadRouletteCatalogProjection.ResolveEligibleOption(options, 255));
    }

    [Fact]
    public void MainScenarioCompatibilityConstantsRemainStable()
    {
        Assert.Equal((uint)3, DadRouletteCatalogProjection.MainScenarioRouletteId);
        Assert.Equal("MainScenario", DadRouletteCatalogProjection.MainScenarioLegacyKey);
    }

    private static DadContentRouletteCatalogRow Row(
        uint rowId,
        string name,
        bool isInDutyFinder = true,
        bool isPvP = false,
        byte membersPerParty = 4,
        byte partyCount = 1,
        byte sortKey = 1,
        byte queueMaxPlayers = 4)
        => new(
            rowId,
            name,
            isInDutyFinder,
            isPvP,
            membersPerParty,
            partyCount,
            sortKey,
            queueMaxPlayers);

    private static void AssertOption(DadPlannerRouletteOption option, uint id, string displayName)
    {
        Assert.Equal(id, option.RouletteId);
        Assert.Equal($"ContentRoulette:{id}", option.Key);
        Assert.Equal(displayName, option.DisplayName);
        Assert.True(option.IsAvailable);
        Assert.Equal(string.Empty, option.UnavailableReason);

        var target = option.ToQueueTarget();
        Assert.Equal(DadQueueTargetKind.Roulette, target.Kind);
        Assert.Equal(id, target.RouletteId);
        Assert.Equal(option.Key, target.Key);
        Assert.Equal(displayName, target.DisplayName);
    }
}
