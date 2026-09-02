using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadWorkerTimeoutRulesTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void NonPositiveTimeoutIsUnbounded(int timeoutSeconds)
    {
        Assert.Null(DadWorkerTimeoutRules.ResolveTimeout(timeoutSeconds));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(29, 30)]
    [InlineData(30, 30)]
    [InlineData(300, 300)]
    [InlineData(1800, 1800)]
    [InlineData(7200, 7200)]
    [InlineData(7201, 7200)]
    public void PositiveTimeoutIsClampedToFiniteWorkerBounds(int timeoutSeconds, int expectedSeconds)
    {
        var resolved = DadWorkerTimeoutRules.ResolveTimeout(timeoutSeconds);

        Assert.True(resolved.HasValue);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), resolved.Value);
    }

    [Fact]
    public void DutyWorkerStillTimesOutBeforeEntry()
    {
        Assert.True(DadWorkerTimeoutRules.HasTimedOut(
            1320,
            DadModuleId.Duty,
            enteredDuty: false,
            TimeSpan.FromMinutes(22)));
    }

    [Theory]
    [InlineData(DadModuleId.Duty)]
    [InlineData(DadModuleId.Msq)]
    [InlineData(DadModuleId.DutySupport)]
    [InlineData(DadModuleId.Trust)]
    [InlineData(DadModuleId.PremadeDuty)]
    [InlineData(DadModuleId.DailyMsq)]
    [InlineData(DadModuleId.Mogtome)]
    [InlineData(DadModuleId.Commendation)]
    [InlineData(DadModuleId.CustomDuty)]
    public void ConfirmedDutyWorkerRemainsActiveBeyondTwentyTwoMinutes(DadModuleId moduleId)
    {
        Assert.False(DadWorkerTimeoutRules.HasTimedOut(
            1320,
            moduleId,
            enteredDuty: true,
            TimeSpan.FromHours(2)));
    }

    [Fact]
    public void LatchedDutyEntryRemainsProtectedThroughExitTransition()
    {
        Assert.False(DadWorkerTimeoutRules.HasTimedOut(
            1320,
            DadModuleId.Duty,
            enteredDuty: true,
            TimeSpan.FromHours(2)));
    }

    [Theory]
    [InlineData(DadModuleId.LootGoblin)]
    [InlineData(DadModuleId.Blunderville)]
    [InlineData(DadModuleId.Astrope)]
    public void NonDutyTaskModulesRetainWorkerTimeout(DadModuleId moduleId)
    {
        Assert.True(DadWorkerTimeoutRules.HasTimedOut(
            1320,
            moduleId,
            enteredDuty: true,
            TimeSpan.FromMinutes(22)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void NonPositivePersistentStartupTimeoutRemainsUnbounded(int timeoutSeconds)
    {
        Assert.False(DadWorkerTimeoutRules.HasTimedOut(
            timeoutSeconds,
            DadModuleId.Duty,
            enteredDuty: false,
            TimeSpan.FromDays(1)));
    }
}
