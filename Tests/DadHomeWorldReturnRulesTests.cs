using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadHomeWorldReturnRulesTests
{
    private static readonly DateTime Now = new(2026, 7, 15, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HomeWorldWithFreshStableProofAndIdleLifestreamIsReady()
    {
        var decision = new DadHomeWorldReturnGate().Evaluate(
            ParticipantAt(74, "Coeurl", Now),
            lifestreamAvailable: true,
            lifestreamBusy: false,
            Now);

        Assert.Equal(DadHomeWorldReturnAction.Ready, decision.Action);
        Assert.Contains("home world Coeurl", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisitingCharacterReturnsHomeOnceThenWaitsForFreshHomeProof()
    {
        var gate = new DadHomeWorldReturnGate();
        var participant = ParticipantAt(63, "Siren", Now);

        var invoke = gate.Evaluate(participant, true, false, Now);
        Assert.Equal(DadHomeWorldReturnAction.InvokeLifestream, invoke.Action);
        Assert.Equal("Coeurl", invoke.DestinationWorldName);
        Assert.Equal(1, invoke.AttemptNumber);
        gate.RecordInvocationResult(
            new DadLifestreamChangeWorldResult(DadLifestreamChangeWorldOutcome.Accepted, "accepted"),
            Now);

        participant.CurrentLocation!.ObservedAtUtc = Now.AddSeconds(1);
        Assert.Equal(DadHomeWorldReturnAction.Wait, gate.Evaluate(
            participant, true, false, Now.AddSeconds(1)).Action);
        Assert.Equal(1, gate.InvocationCount);

        participant.CurrentLocation = Location(74, "Coeurl", Now.AddSeconds(2));
        var ready = gate.Evaluate(participant, true, false, Now.AddSeconds(2));
        Assert.Equal(DadHomeWorldReturnAction.Ready, ready.Action);
        Assert.Equal(1, gate.InvocationCount);
    }

    [Fact]
    public void AcceptedTravelStillRequiresIdleLifestreamAtHome()
    {
        var gate = new DadHomeWorldReturnGate();
        var participant = ParticipantAt(63, "Siren", Now);
        Assert.Equal(DadHomeWorldReturnAction.InvokeLifestream, gate.Evaluate(participant, true, false, Now).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.Accepted, "accepted"), Now);
        participant.CurrentLocation = Location(74, "Coeurl", Now.AddSeconds(1));

        Assert.Equal(DadHomeWorldReturnAction.Wait, gate.Evaluate(
            participant, true, true, Now.AddSeconds(1)).Action);
        Assert.Equal(DadHomeWorldReturnAction.Ready, gate.Evaluate(
            participant, true, false, Now.AddSeconds(2)).Action);
    }

    [Fact]
    public void ExplicitFalseAllowsThreeAttemptsAtTenSecondIntervals()
    {
        var gate = new DadHomeWorldReturnGate();
        var participant = ParticipantAt(63, "Siren", Now);

        Assert.Equal(DadHomeWorldReturnAction.InvokeLifestream, gate.Evaluate(participant, true, false, Now).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.ExplicitFalse, "false"), Now);
        participant.CurrentLocation!.ObservedAtUtc = Now.AddSeconds(9);
        Assert.Equal(DadHomeWorldReturnAction.Wait, gate.Evaluate(participant, true, false, Now.AddSeconds(9)).Action);
        participant.CurrentLocation.ObservedAtUtc = Now.AddSeconds(10);
        Assert.Equal(DadHomeWorldReturnAction.InvokeLifestream, gate.Evaluate(participant, true, false, Now.AddSeconds(10)).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.ExplicitFalse, "false"), Now.AddSeconds(10));
        participant.CurrentLocation.ObservedAtUtc = Now.AddSeconds(20);
        Assert.Equal(DadHomeWorldReturnAction.InvokeLifestream, gate.Evaluate(participant, true, false, Now.AddSeconds(20)).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.ExplicitFalse, "false"), Now.AddSeconds(20));

        Assert.Equal(3, gate.InvocationCount);
        Assert.Equal(DadHomeWorldReturnAction.Reject, gate.Evaluate(participant, true, false, Now.AddSeconds(20)).Action);
    }

    [Fact]
    public void UncertainChangeWorldAcceptanceFailsWithoutRetry()
    {
        var gate = new DadHomeWorldReturnGate();
        var participant = ParticipantAt(63, "Siren", Now);
        Assert.Equal(DadHomeWorldReturnAction.InvokeLifestream, gate.Evaluate(participant, true, false, Now).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.Uncertain, "IPC exception"), Now);

        var decision = gate.Evaluate(participant, true, false, Now.AddSeconds(20));
        Assert.Equal(DadHomeWorldReturnAction.Reject, decision.Action);
        Assert.Equal(1, gate.InvocationCount);
        Assert.Contains("no retry", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingHomeOrCurrentIdentityFailsClosed()
    {
        var missingHome = ParticipantAt(63, "Siren", Now);
        missingHome.Character.WorldId = 0;
        var missingCurrent = ParticipantAt(63, "Siren", Now);
        missingCurrent.CurrentLocation!.DataCenterId = 0;

        Assert.Equal(DadHomeWorldReturnAction.Reject, new DadHomeWorldReturnGate().Evaluate(
            missingHome, true, false, Now).Action);
        Assert.Equal(DadHomeWorldReturnAction.Reject, new DadHomeWorldReturnGate().Evaluate(
            missingCurrent, true, false, Now).Action);
    }

    [Fact]
    public void ReturnHomeStagesAppendWithoutRenumberingEarlierTakeoverValues()
    {
        Assert.Equal(17, (int)DadWakeTakeoverStage.RelogCommitted);
        Assert.Equal(18, (int)DadWakeTakeoverStage.ReturningHome);
        Assert.Equal(19, (int)DadWakeTakeoverStage.WaitingForHomeWorld);
    }

    private static DadParticipantSnapshot ParticipantAt(uint currentWorldId, string currentWorldName, DateTime observedAtUtc)
        => new()
        {
            WorkerSessionId = new DadWorkerSessionId("worker"),
            ManagedAccountKey = new DadAccountKey("account"),
            ActiveCharacterKey = new DadCharacterKey("Character@Coeurl"),
            IsAvailable = true,
            WorldReadyStable = true,
            Character = new DadAcquiredCharacter
            {
                AccountId = "account",
                CharacterKey = "Character@Coeurl",
                ContentId = 1234,
                WorldId = 74,
                WorldName = "Coeurl",
            },
            CurrentLocation = Location(currentWorldId, currentWorldName, observedAtUtc),
        };

    private static DadWorldLocationObservation Location(uint worldId, string worldName, DateTime observedAtUtc)
        => new()
        {
            WorldId = worldId,
            WorldName = worldName,
            DataCenterId = worldId == 74 ? 1u : 2u,
            DataCenterName = worldId == 74 ? "Crystal" : "Aether",
            RegionId = 2,
            RegionName = "North America",
            ObservedAtUtc = observedAtUtc,
        };
}
