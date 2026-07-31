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
}
