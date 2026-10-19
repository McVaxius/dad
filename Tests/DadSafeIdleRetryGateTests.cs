using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadSafeIdleRetryGateTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ElapsedTimeWithoutAdvancingLiveObservationNeverProvesIdle()
    {
        var gate = new DadSafeIdleRetryGate();
        var participant = Participant();
        Assert.False(gate.Observe(participant, Now));
        Assert.False(gate.Observe(participant, Now.AddSeconds(5)));
        participant.CurrentLocation!.ObservedAtUtc = Now.AddSeconds(5);
        Assert.True(gate.Observe(participant, Now.AddSeconds(5)));
    }

    [Theory]
    [InlineData(0)] // missing location
    [InlineData(1)] // stale location
    [InlineData(2)] // future location
    [InlineData(3)] // clock rollback
    [InlineData(4)] // observation gap
    [InlineData(5)] // location changed
    [InlineData(6)] // unavailable
    [InlineData(7)] // world unstable
    [InlineData(8)] // cached observation rolled backwards
    public void InterruptedProofMustStartAnotherFullWindow(int kind)
    {
        var gate = new DadSafeIdleRetryGate();
        var participant = Participant();
        Assert.False(gate.Observe(participant, Now));
        var at = Now.AddSeconds(4);
        participant.CurrentLocation!.ObservedAtUtc = at;
        Assert.False(gate.Observe(participant, at));
        switch (kind)
        {
            case 0: participant.CurrentLocation = null; break;
            case 1: participant.CurrentLocation.ObservedAtUtc = Now.AddMinutes(-1); break;
            case 2: participant.CurrentLocation.ObservedAtUtc = Now.AddMinutes(1); break;
            case 3: at = Now.AddSeconds(-1); participant.CurrentLocation.ObservedAtUtc = at; break;
            case 4: at = Now.AddDays(1); participant.CurrentLocation.ObservedAtUtc = at; break;
            case 5: participant.CurrentLocation.WorldId++; break;
            case 6: participant.IsAvailable = false; break;
            case 7: participant.WorldReadyStable = false; break;
            case 8: participant.CurrentLocation.ObservedAtUtc = Now.AddSeconds(3); break;
        }
        Assert.False(gate.Observe(participant, at));
        participant = Participant();
        participant.CurrentLocation!.ObservedAtUtc = at;
        Assert.False(gate.Observe(participant, at));
        participant.CurrentLocation.ObservedAtUtc = at.AddSeconds(4);
        Assert.False(gate.Observe(participant, at.AddSeconds(4)));
        participant.CurrentLocation.ObservedAtUtc = at.AddSeconds(5);
        Assert.True(gate.Observe(participant, at.AddSeconds(5)));
    }

    private static DadParticipantSnapshot Participant() => new()
    {
        IsAvailable = true,
        WorldReadyStable = true,
        ActiveCharacterKey = new DadCharacterKey("Source@World"),
        Character = new DadAcquiredCharacter { CharacterKey = "Source@World", ContentId = 10, WorldId = 1, WorldName = "World" },
        CurrentLocation = new DadWorldLocationObservation
        {
            WorldId = 1, WorldName = "World", DataCenterId = 1, DataCenterName = "DC",
            RegionId = 1, RegionName = "Region", ObservedAtUtc = Now,
        },
    };
}
