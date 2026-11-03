using System.Text.Json;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAdsDutyMaturityTests
{
    [Theory]
    [InlineData(0, "NotCleared", false)]
    [InlineData(1, "OnePlayerUnsyncCleared", true)]
    [InlineData(2, "OnePlayerDutySupport", true)]
    [InlineData(3, "FourPlayerSyncCleared", true)]
    public void OnlyNonzeroKnownMaturityIsEligible(int level, string status, bool eligible)
    {
        var result = DadAdsDutyMaturity.Parse(Payload(level, status), 901, 41);
        Assert.True(result.Success);
        Assert.Equal(eligible, result.Eligible);
        Assert.Equal(level, result.Level);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    public void UnreadableResponseDoesNotGrantEligibility(string payload)
        => Assert.False(DadAdsDutyMaturity.Parse(payload, 901, 41).Eligible);

    [Fact]
    public void UnsupportedSchemaIdentityAndContradictoryMaturityAreRejected()
    {
        var valid = Payload(1, "OnePlayerUnsyncCleared");
        foreach (var payload in new[]
        {
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
            valid.Replace("\"territoryTypeId\":901", "\"territoryTypeId\":902"),
            valid.Replace("\"contentFinderConditionId\":41", "\"contentFinderConditionId\":42"),
            valid.Replace("\"success\":true", "\"success\":false"),
            valid.Replace("\"eligibleForQuest\":true", "\"eligibleForQuest\":false"),
            Payload(4, "FutureClearance"), Payload(1, "NotCleared"),
        }) Assert.False(DadAdsDutyMaturity.Parse(payload, 901, 41).Eligible);
    }

    private static string Payload(int level, string status) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, success = true, territoryTypeId = 901, contentFinderConditionId = 41,
        clearanceLevel = level, clearanceStatus = status, eligibleForQuest = level > 0,
    });
}
