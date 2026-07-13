using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadVermaxionReadinessTests
{
    private static readonly DateTime Now = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UnloadedVermaxionPasses()
    {
        var result = DadVermaxionStatusParser.Parse(false, null, Now);

        Assert.Equal(DadVermaxionReadinessKind.NotLoaded, result.Kind);
        Assert.False(result.IsHeld);
    }

    [Fact]
    public void LoadedIdlePasses()
    {
        var result = DadVermaxionStatusParser.Parse(
            true,
            "{\"version\":1,\"isBusy\":false,\"activity\":\"Idle\",\"state\":\"Idle\",\"summary\":\"idle\",\"generatedAtUtc\":\"2026-07-10T11:59:59Z\"}",
            Now);

        Assert.Equal(DadVermaxionReadinessKind.Idle, result.Kind);
        Assert.False(result.IsHeld);
    }

    [Fact]
    public void LoadedBusyFailsClosedAndClearsPostArReadiness()
    {
        var result = DadVermaxionStatusParser.Parse(
            true,
            "{\"version\":1,\"isBusy\":true,\"activity\":\"Fishing\",\"state\":\"Fishing\",\"summary\":\"voyage\"}",
            Now);

        Assert.Equal(DadVermaxionReadinessKind.Busy, result.Kind);
        Assert.True(result.IsHeld);
        var postArReady = DadExternalAutomationRules.ApplyPostArReadiness(true, result);
        Assert.False(postArReady);
        var alreadyOnline = DadWakePolicyRules.Evaluate(
            DadSchedulerWakePolicy.AlreadyOnlineOnly,
            sameAccountClientConnected: true,
            correctCharacter: true,
            postArReady);
        Assert.False(alreadyOnline.Ready);
        Assert.False(alreadyOnline.ShouldRequestTakeover);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":2,\"isBusy\":false}")]
    public void MissingMalformedAndUnsupportedStatusFailClosed(string? json)
    {
        var result = DadVermaxionStatusParser.Parse(true, json, Now);

        Assert.Equal(DadVermaxionReadinessKind.Unavailable, result.Kind);
        Assert.True(result.IsHeld);
        Assert.Contains("VERMAXION status", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IndependentStageClocksPauseAndResumeWithoutResetting()
    {
        var slot = new DadSchedulerSlotState
        {
            TakeoverStage = DadWakeTakeoverStage.WaitingForClient,
        };

        DadWakeStageTimeoutPolicy.Observe(slot, Now);
        slot.TakeoverStage = DadWakeTakeoverStage.WaitingForExternalAutomation;
        DadWakeStageTimeoutPolicy.Observe(slot, Now.AddSeconds(30));
        slot.TakeoverStage = DadWakeTakeoverStage.WaitingForAutoRetainer;
        DadWakeStageTimeoutPolicy.Observe(slot, Now.AddMinutes(60));
        slot.TakeoverStage = DadWakeTakeoverStage.WaitingForClient;
        DadWakeStageTimeoutPolicy.Observe(slot, Now.AddMinutes(70));

        Assert.Equal(30, slot.ParticipantWaitElapsedSeconds, precision: 3);
        Assert.Equal(3570, slot.VermaxionHoldElapsedSeconds, precision: 3);
        Assert.Equal(600, slot.AutoRetainerWaitElapsedSeconds, precision: 3);
        Assert.Equal(Now, slot.ParticipantWaitStartedUtc);
        Assert.Equal(Now.AddSeconds(30), slot.VermaxionHoldStartedUtc);
        Assert.Equal(Now.AddMinutes(60), slot.AutoRetainerWaitStartedUtc);

        var remaining = DadWakeStageTimeoutPolicy.GetRemaining(slot, Now.AddMinutes(70), 5400, 1200, 300);
        Assert.Equal(270, remaining.TotalSeconds, precision: 3);
    }

    [Theory]
    [InlineData(DadWakeTakeoverStage.WaitingForExternalAutomation, 5400)]
    [InlineData(DadWakeTakeoverStage.WaitingForAutoRetainer, 1200)]
    [InlineData(DadWakeTakeoverStage.WaitingForClient, 300)]
    public void EachStageUsesItsOwnDefaultBudget(DadWakeTakeoverStage stage, int expectedSeconds)
    {
        var slot = new DadSchedulerSlotState { TakeoverStage = stage };
        DadWakeStageTimeoutPolicy.Observe(slot, Now);

        Assert.Equal(
            expectedSeconds,
            DadWakeStageTimeoutPolicy.GetRemaining(slot, Now, 5400, 1200, 300).TotalSeconds);
        Assert.True(DadWakeStageTimeoutPolicy.IsTimedOut(slot, Now.AddSeconds(expectedSeconds), 5400, 1200, 300));
    }
}
