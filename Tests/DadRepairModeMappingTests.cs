using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRepairModeMappingTests
{
    [Theory]
    [InlineData("self", 0, 0)]
    [InlineData("npc-no-inn", 1, 0)]
    [InlineData("npc-no-teleport-no-inn", 2, 0)]
    [InlineData("npc", 3, 0)]
    [InlineData("npc-yes-inn", 4, 0)]
    [InlineData("npc-yes-inn-uldah", 4, 1)]
    [InlineData("npc-yes-inn-gridania", 4, 2)]
    [InlineData("npc-yes-inn-limsa", 4, 3)]
    [InlineData("npc-yes-inn-ishgard", 4, 4)]
    [InlineData("npc-yes-inn-crystarium", 4, 5)]
    [InlineData("npc-yes-inn-sharlayan", 4, 6)]
    [InlineData("npc-yes-inn-tuliyollal", 4, 7)]
    public void CanonicalAdsModesRoundTripThroughNumericPolicyAndBothSerializers(string mode, int value, int destination)
    {
        Assert.True(DadPreDutyRepairPolicy.TryFromAdsMode(mode, out var policy));
        policy.Enabled = true;
        policy.ThresholdPercent = 42;
        Assert.Equal(value, (int)policy.Mode);
        Assert.Equal(destination, (int)policy.InnDestination);
        Assert.Equal(mode, policy.AdsMode);
        Assert.Equal(value == 4, policy.RequiresInnCompletion);
        var clone = policy.Clone().Normalize();
        var wire = DadIpcJson.Deserialize<DadPreDutyRepairPolicy>(DadIpcJson.Serialize(policy));
        var saved = Newtonsoft.Json.JsonConvert.DeserializeObject<DadPreDutyRepairPolicy>(
            Newtonsoft.Json.JsonConvert.SerializeObject(policy));
        foreach (var copy in new[] { clone, wire, saved })
        {
            Assert.NotNull(copy);
            Assert.NotSame(policy, copy);
            Assert.Equal(mode, copy.AdsMode);
            Assert.Equal(policy.InnDestination, copy.InnDestination);
            Assert.True(copy.Enabled);
            Assert.Equal(42, copy.ThresholdPercent);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("npc-yes-inn-unknown")]
    [InlineData("npc-yes-inn-")]
    [InlineData("npc-yes-inn-limsa /logout")]
    public void UnknownModesAndFreeTextDestinationsAreRejected(string? mode)
        => Assert.False(DadPreDutyRepairPolicy.TryFromAdsMode(mode, out _));

    [Fact]
    public void LegacyJsonRetainsModeIdsAndDefaultsTheNewDestination()
    {
        for (var mode = 0; mode < 3; mode++)
        {
            var policy = DadIpcJson.Deserialize<DadPreDutyRepairPolicy>($"{{\"enabled\":true,\"mode\":{mode}}}")!;
            Assert.Equal(mode, (int)policy.Mode);
            Assert.Equal(DadRepairInnDestination.Automatic, policy.InnDestination);
        }
        var invalid = new DadPreDutyRepairPolicy
        {
            Mode = DadPreDutyRepairMode.NpcThenInn,
            InnDestination = (DadRepairInnDestination)99,
        }.Normalize();
        Assert.False(invalid.Enabled);
        Assert.Equal("npc-yes-inn", invalid.AdsMode);
    }

    [Theory]
    [InlineData(DadPreDutyRepairMode.Self, "self")]
    [InlineData(DadPreDutyRepairMode.NpcExcludingInns, "npc-no-inn")]
    [InlineData(DadPreDutyRepairMode.NearbyNpcNoTeleportOrInn, "npc-no-teleport-no-inn")]
    [InlineData(DadPreDutyRepairMode.NpcWithInnFallback, "npc")]
    public void InnDestinationDoesNotChangeOtherRoutes(DadPreDutyRepairMode mode, string expected)
        => Assert.Equal(expected, new DadPreDutyRepairPolicy
        {
            Mode = mode, InnDestination = DadRepairInnDestination.Tuliyollal,
        }.AdsMode);

    [Fact]
    public void InnRouteWithHealthyGearAndNoActiveRepairDoesNotStartAnUnnecessaryTrip()
    {
        var gate = new DadPreDutyRepairGate();
        Assert.Equal(DadPreDutyRepairAction.Ready, gate.Evaluate(InnPolicy(), DadModuleId.PremadeDuty,
            new(), DadEquippedDurabilityObservation.ReadableAt(100), DadAdsRepairObservation.Absent(), Now).Action);
        Assert.Equal(0, gate.InvocationCount);
    }

    [Fact]
    public void ExistingInnRepairIsAdoptedEvenAfterDurabilityHasImprovedAndStillHasADeadline()
    {
        var gate = new DadPreDutyRepairGate();
        var running = DadAdsRepairObservation.Running(true, "", "npc-yes-inn-limsa");
        Assert.Equal(DadPreDutyRepairAction.Wait, gate.Evaluate(InnPolicy(), DadModuleId.DailyMsq,
            new(), DadEquippedDurabilityObservation.ReadableAt(100), running, Now).Action);
        Assert.Equal(0, gate.InvocationCount);
        Assert.Equal(DadPreDutyRepairAction.Reject, gate.Evaluate(InnPolicy(), DadModuleId.DailyMsq,
            new(), DadEquippedDurabilityObservation.ReadableAt(100), running, Now.AddSeconds(180)).Action);
        // A new run/gate never inherits the previous return-to-inn wait.
        Assert.Equal(DadPreDutyRepairAction.Ready, new DadPreDutyRepairGate().Evaluate(InnPolicy(), DadModuleId.DailyMsq,
            new(), DadEquippedDurabilityObservation.ReadableAt(100), DadAdsRepairObservation.Idle(), Now.AddSeconds(181)).Action);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InnCompletionNeverUsesUnreadableOrMissingAdsAsIdleProof(bool missing)
    {
        var gate = new DadPreDutyRepairGate();
        var running = DadAdsRepairObservation.Running(true, "NPC repair", "npc-yes-inn");
        gate.Evaluate(InnPolicy(), DadModuleId.DailyMsq, new(), DadEquippedDurabilityObservation.ReadableAt(100), running, Now);
        var observation = missing ? DadAdsRepairObservation.Absent() : DadAdsRepairObservation.Unreadable("temporary");
        var first = gate.Evaluate(InnPolicy(), DadModuleId.DailyMsq, new(),
            DadEquippedDurabilityObservation.ReadableAt(100), observation, Now.AddSeconds(1));
        Assert.Equal(missing ? DadPreDutyRepairAction.Reject : DadPreDutyRepairAction.Wait, first.Action);
        Assert.Equal(DadPreDutyRepairAction.Reject, gate.Evaluate(InnPolicy(), DadModuleId.DailyMsq,
            new(), DadEquippedDurabilityObservation.ReadableAt(100), observation, Now.AddSeconds(6)).Action);
    }

    [Fact]
    public void LegacyRouteStillUsesDurabilityProofWithoutWaitingForAnInn()
        => Assert.Equal(DadPreDutyRepairAction.Ready, new DadPreDutyRepairGate().Evaluate(
            new() { Enabled = true, Mode = DadPreDutyRepairMode.NpcExcludingInns }, DadModuleId.DailyMsq, new(),
            DadEquippedDurabilityObservation.ReadableAt(100),
            DadAdsRepairObservation.Running(true, "NPC repair", "npc-no-inn"), Now).Action);

    private static readonly DateTime Now = new(2026, 10, 29, 7, 0, 0, DateTimeKind.Utc);
    private static DadPreDutyRepairPolicy InnPolicy() => new()
    {
        Enabled = true, Mode = DadPreDutyRepairMode.NpcThenInn,
        InnDestination = DadRepairInnDestination.Limsa,
    };
}
