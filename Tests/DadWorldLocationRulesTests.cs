using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadWorldLocationRulesTests
{
    [Theory]
    [InlineData(7u, "Light", 3u, "Europe")]
    [InlineData(3u, "Mana", 1u, "Japan")]
    [InlineData(2u, "Gaia", 1u, "Japan")]
    [InlineData(5u, "Primal", 2u, "North America")]
    [InlineData(11u, "Dynamis", 2u, "North America")]
    [InlineData(9u, "Materia", 4u, "Oceania")]
    [InlineData(13u, "NA Cloud DC (Beta)", 7u, "Cloud")]
    public void ResolvesLogicalMembershipWithoutReplacingItsRegion(
        uint id, string name, uint regionId, string regionName)
    {
        uint requestedId = 0;
        Assert.True(DadWorldLocationRules.TryResolveDataCenter(id, regionId, requested =>
        {
            requestedId = requested;
            return new DadDataCenterLocation(id, $" {name} ", regionId, $" {regionName} ");
        }, out var resolved));

        Assert.Equal(id, requestedId);
        Assert.Equal(new DadDataCenterLocation(id, name, regionId, regionName), resolved);
    }

    [Theory]
    [InlineData(0u, 3u)]
    [InlineData(7u, 0u)]
    public void MissingLogicalOrRegionalIdentityNeverUsesAParentDcFallback(uint dc, uint region)
        => Assert.False(DadWorldLocationRules.TryResolveDataCenter(dc, region,
            _ => throw new InvalidOperationException("No lookup should be attempted."), out _));

    [Fact]
    public void MissingLogicalRowCannotBeConfirmedFromItsParentRegion()
        => Assert.False(DadWorldLocationRules.TryResolveDataCenter(7, 3, _ => null, out _));

    [Theory]
    [InlineData(6u, "Chaos", 3u, "Europe")]
    [InlineData(7u, "Light", 2u, "North America")]
    [InlineData(7u, "", 3u, "Europe")]
    [InlineData(7u, "Light", 3u, " ")]
    public void RejectsMismatchedOrIncompleteDcMetadata(
        uint id, string name, uint region, string regionName)
        => Assert.False(DadWorldLocationRules.TryResolveDataCenter(7, 3,
            _ => new DadDataCenterLocation(id, name, region, regionName), out _));
}
