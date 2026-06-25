using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadPostRunUtilityPredicatesTests
{
    [Fact]
    public void GearCofferPredicateRequiresCofferAndGearSignal()
    {
        Assert.True(DadPostRunUtilityPredicates.IsGearCoffer("Augmented Gear Coffer"));
        Assert.True(DadPostRunUtilityPredicates.IsGearCoffer("Antique Coffer", "Contains equipment for your current job."));
        Assert.False(DadPostRunUtilityPredicates.IsGearCoffer("Wooden Coffer"));
        Assert.False(DadPostRunUtilityPredicates.IsGearCoffer("Potion"));
    }

    [Fact]
    public void TripleTriadPredicateMatchesCardsByNameOrDescription()
    {
        Assert.True(DadPostRunUtilityPredicates.IsTripleTriadCard("Ifrit Card"));
        Assert.True(DadPostRunUtilityPredicates.IsTripleTriadCard("Damaged Card", "A Triple Triad card."));
        Assert.False(DadPostRunUtilityPredicates.IsTripleTriadCard("Marked Token"));
        Assert.False(DadPostRunUtilityPredicates.IsTripleTriadCard(""));
    }
}
