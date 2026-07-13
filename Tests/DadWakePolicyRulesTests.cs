using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadWakePolicyRulesTests
{
    [Fact]
    public void AlreadyOnlineRequiresExactPostArReadyCharacterWithoutTakeover()
    {
        var ready = DadWakePolicyRules.Evaluate(
            DadSchedulerWakePolicy.AlreadyOnlineOnly,
            sameAccountClientConnected: true,
            correctCharacter: true,
            postArReady: true);
        var wrong = DadWakePolicyRules.Evaluate(
            DadSchedulerWakePolicy.AlreadyOnlineOnly,
            sameAccountClientConnected: true,
            correctCharacter: false,
            postArReady: true);
        var notPostArReady = DadWakePolicyRules.Evaluate(
            DadSchedulerWakePolicy.AlreadyOnlineOnly,
            sameAccountClientConnected: true,
            correctCharacter: true,
            postArReady: false);

        Assert.True(ready.Ready);
        Assert.False(ready.ShouldRequestTakeover);
        Assert.False(wrong.CanSchedule);
        Assert.Contains("will not relog", wrong.BlockedReason);
        Assert.False(notPostArReady.ShouldRequestTakeover);
        Assert.Contains("send no AutoRetainer commands", notPostArReady.BlockedReason);
    }

    [Fact]
    public void LaunchIfOfflineWaitsWithoutRequestingProcessLaunch()
    {
        var decision = DadWakePolicyRules.Evaluate(
            DadSchedulerWakePolicy.LaunchIfOffline,
            sameAccountClientConnected: false,
            correctCharacter: false,
            postArReady: false);

        Assert.True(decision.CanSchedule);
        Assert.False(decision.ShouldRequestTakeover);
        Assert.Equal(DadWakeTakeoverStage.WaitingForClient, decision.Stage);
        Assert.Contains("will not launch a process", decision.Summary);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void StaleReadyTakeoverCannotMakeAnInexactOrUnsafeHeartbeatReady(
        bool exactCharacter,
        bool postArReady,
        bool multiModeEnabled)
    {
        var decision = DadWakePolicyRules.Evaluate(
            DadSchedulerWakePolicy.LaunchIfOffline,
            sameAccountClientConnected: true,
            correctCharacter: exactCharacter,
            postArReady: postArReady,
            takeoverStatus: DadWakeTakeoverStatus.Ready,
            multiModeEnabled: multiModeEnabled);

        Assert.False(decision.Ready);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void AlreadyOnlineOnlyRequiresReadableIdleDisabledAutoRetainerWithoutTakeover(
        bool available,
        bool busy,
        bool multiMode)
    {
        var decision = DadWakePolicyRules.Evaluate(
            DadSchedulerWakePolicy.AlreadyOnlineOnly,
            sameAccountClientConnected: true,
            correctCharacter: true,
            postArReady: true,
            autoRetainerAvailable: available,
            autoRetainerBusy: busy,
            multiModeEnabled: multiMode);

        Assert.False(decision.Ready);
        Assert.False(decision.ShouldRequestTakeover);
        Assert.Contains("send no commands", decision.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadCharacterStubAlwaysBlocksAndSendsNothing()
    {
        var decision = DadWakePolicyRules.Evaluate(
            DadSchedulerWakePolicy.LoadCharacterIfOnline,
            sameAccountClientConnected: true,
            correctCharacter: true,
            postArReady: true);

        Assert.False(decision.CanSchedule);
        Assert.False(decision.ShouldRequestTakeover);
        Assert.Equal(DadWakeTakeoverStage.Blocked, decision.Stage);
        Assert.Contains("not implemented", decision.BlockedReason);
    }

    [Fact]
    public void ParticipantTimeoutUsesConfiguredDefaultFloor()
    {
        var started = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(DadWakePolicyRules.IsParticipantReadyTimedOut(started, started.AddSeconds(299), 300));
        Assert.True(DadWakePolicyRules.IsParticipantReadyTimedOut(started, started.AddSeconds(300), 300));
        Assert.False(DadWakePolicyRules.IsParticipantReadyTimedOut(started, started.AddSeconds(29), 1));
        Assert.True(DadWakePolicyRules.IsParticipantReadyTimedOut(started, started.AddSeconds(30), 1));
    }
}
