using System.Text.Json;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadDutyLootSettingsTests
{
    [Fact]
    public void ReadForceFreezeAndWriteBackDoNotConfigureAnotherSession()
    {
        var settings = new DadDutyLootSettings();
        var keys = new[] { "LootTreasure", "LootBossTreasureOnly", "LootMethodEnum" };
        var original = keys.ToDictionary(key => key, key => settings.Get(key)!);
        Assert.All(original.Values, value => Assert.False(string.IsNullOrEmpty(value)));

        settings.Set("LootTreasure", "True");
        settings.Set("LootBossTreasureOnly", "False");
        if (original["LootMethodEnum"] is not ("AutoDuty" or "All"))
            settings.Set("LootMethodEnum", "AutoDuty");
        Assert.True(settings.Configured);
        using var active = JsonDocument.Parse(settings.BuildPolicy("run-a", 901, 41));
        Assert.True(active.RootElement.GetProperty("active").GetBoolean());

        foreach (var key in keys)
            Assert.True(settings.Set(key, original[key]));
        Assert.Empty(settings.Failure);
        Assert.False(settings.Configured);

        // Wiggly's next forced run captures the values again before writing.
        foreach (var key in keys)
            Assert.Equal(original[key], settings.Get(key));
        settings.Set("LootTreasure", "True");
        settings.Set("LootBossTreasureOnly", "False");
        Assert.True(settings.Configured);
    }

    [Fact]
    public void SettingsAreRealAndPolicyFreezesTheAcceptedSessionValues()
    {
        var settings = new DadDutyLootSettings();
        Assert.False(settings.Configured);
        settings.Set("LootTreasure", "False");
        settings.Set("LootBossTreasureOnly", "True");
        settings.Set("LootMethodEnum", "pandora");
        Assert.Equal("False", settings.Get("loottreasure"));
        Assert.Equal("True", settings.Get("LOOTBOSSTREASUREONLY"));
        Assert.Equal("Pandora", settings.Get("LootMethodEnum"));
        var snapshot = settings.BuildPolicy("session-a", 901, 41);
        settings.Set("LootTreasure", "True");
        settings.Set("LootBossTreasureOnly", "False");
        settings.Set("LootMethodEnum", "AutoDuty");
        using var policy = JsonDocument.Parse(snapshot);
        Assert.Equal("session-a", policy.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(901u, policy.RootElement.GetProperty("territoryTypeId").GetUInt32());
        Assert.Equal(41u, policy.RootElement.GetProperty("contentFinderConditionId").GetUInt32());
        Assert.False(policy.RootElement.GetProperty("lootTreasure").GetBoolean());
        Assert.True(policy.RootElement.GetProperty("lootBossTreasureOnly").GetBoolean());
        Assert.Equal("Pandora", policy.RootElement.GetProperty("lootMethodEnum").GetString());
    }

    [Fact]
    public void InvalidValuesPreservePreviousSettingsAndBlockUntilTheirOwnKeyIsCorrected()
    {
        var settings = new DadDutyLootSettings();
        settings.Set("LootTreasure", "invalid");
        settings.Set("LootBossTreasureOnly", "False");
        Assert.Contains("Invalid LootTreasure", settings.Failure);
        Assert.Equal("True", settings.Get("LootTreasure"));
        settings.Set("LootTreasure", "False");
        Assert.Empty(settings.Failure);
        settings.Set("LootMethodEnum", "unsupported");
        Assert.Equal("AutoDuty", settings.Get("LootMethodEnum"));
        Assert.Contains("Invalid LootMethodEnum", settings.Failure);
        Assert.False(settings.Set("unknown", "True"));
        settings.Set("LootMethodEnum", "All");
        Assert.Empty(settings.Failure);
    }
}
