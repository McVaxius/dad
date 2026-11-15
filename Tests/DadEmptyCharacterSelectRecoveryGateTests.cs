using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadEmptyCharacterSelectRecoveryGateTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RequiresThirtyContinuouslyObservedSafeSeconds()
    {
        var gate = new DadEmptyCharacterSelectRecoveryGate();
        for (var second = 0; second < 30; second += 5)
            Assert.False(gate.Observe(true, Now.AddSeconds(second)));
        Assert.True(gate.Observe(true, Now.AddSeconds(30)));
        gate.RecordAttempt();
        Assert.Equal(1, gate.AttemptCount);
        Assert.False(gate.Observe(true, Now.AddSeconds(30)));
    }

    [Theory]
    [InlineData("unsafe")]
    [InlineData("gap")]
    [InlineData("clock")]
    public void UnsafeLoadingQueueStaleOrReversedObservationDiscardsTheWholeWindow(string change)
    {
        var gate = new DadEmptyCharacterSelectRecoveryGate();
        Assert.False(gate.Observe(true, Now));
        Assert.False(gate.Observe(true, Now.AddSeconds(10)));
        Assert.False(gate.Observe(true, Now.AddSeconds(20)));
        if (change == "unsafe")
        {
            Assert.False(gate.Observe(false, Now.AddSeconds(25)));
            Assert.False(gate.Observe(true, Now.AddSeconds(30)));
        }
        else if (change == "gap")
            Assert.False(gate.Observe(true, Now.AddSeconds(36)));
        else
            Assert.False(gate.Observe(true, Now.AddSeconds(19)));
    }

    [Fact]
    public void FiveAttemptLimitSurvivesProofResets()
    {
        var gate = new DadEmptyCharacterSelectRecoveryGate();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var start = Now.AddMinutes(attempt);
            for (var second = 0; second < 30; second += 5)
                Assert.False(gate.Observe(true, start.AddSeconds(second)));
            Assert.True(gate.Observe(true, start.AddSeconds(30)));
            gate.RecordAttempt();
            gate.ResetProof();
        }
        for (var second = 0; second <= 90; second += 5)
            Assert.False(gate.Observe(true, Now.AddMinutes(6).AddSeconds(second)));
        Assert.Equal(5, gate.AttemptCount);
    }
}
