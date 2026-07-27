using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadHomeWorldReturnRulesTests
{
    private static readonly DateTime Now = new(2026, 7, 15, 16, 0, 0, DateTimeKind.Utc);
    private static readonly DadCharacterKey RelogTarget = new("Target@Coeurl");

    [Fact]
    public void HomeWorldWithFreshStableProofAndIdleLifestreamIsReady()
    {
        var decision = new DadHomeWorldReturnGate().Evaluate(
            ParticipantAt(74, "Coeurl", Now),
            lifestreamAvailable: true,
            lifestreamBusy: false,
            RelogTarget,
            Now);

        Assert.Equal(DadHomeWorldReturnAction.Ready, decision.Action);
        Assert.Contains("home world Coeurl", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisitingCharacterReturnsHomeOnceThenWaitsForFreshHomeProof()
    {
        var gate = new DadHomeWorldReturnGate();
        var participant = ParticipantAt(63, "Siren", Now);

        var invoke = gate.Evaluate(participant, true, false, RelogTarget, Now);
        Assert.Equal(DadHomeWorldReturnAction.InvokeLifestream, invoke.Action);
        Assert.Equal("Coeurl", invoke.DestinationWorldName);
        Assert.Equal(1, invoke.AttemptNumber);
        Assert.Equal("Character@Coeurl", gate.FrozenSourceCharacterKey);
        Assert.Equal("Coeurl", gate.FrozenHomeWorldName);
        Assert.Equal("Target@Coeurl", gate.FrozenRelogTargetCharacterKey);
        Assert.Equal(
            "Character@Coeurl is waiting to start Data Center travel back to home world Coeurl before DAD relogs to Target@Coeurl; invoking Lifestream.ChangeWorld('Coeurl') attempt 1/3.",
            invoke.Summary);
        gate.RecordInvocationResult(
            new DadLifestreamChangeWorldResult(DadLifestreamChangeWorldOutcome.Accepted, "accepted"),
            Now);

        participant.CurrentLocation!.ObservedAtUtc = Now.AddSeconds(1);
        var traveling = gate.Evaluate(participant, true, false, RelogTarget, Now.AddSeconds(1));
        Assert.Equal(DadHomeWorldReturnAction.Wait, traveling.Action);
        Assert.Equal(
            "Character@Coeurl is Data Center traveling back to home world Coeurl before DAD relogs to Target@Coeurl; waiting for fresh home-world proof.",
            traveling.Summary);
        Assert.Equal(1, gate.InvocationCount);

        participant.CurrentLocation = Location(74, "Coeurl", Now.AddSeconds(2));
        var ready = gate.Evaluate(participant, true, false, RelogTarget, Now.AddSeconds(2));
        Assert.Equal(DadHomeWorldReturnAction.Ready, ready.Action);
        Assert.Equal(1, gate.InvocationCount);
        Assert.Contains("before DAD relogs to Target@Coeurl", ready.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedTravelStillRequiresIdleLifestreamAtHome()
    {
        var gate = new DadHomeWorldReturnGate();
        var participant = ParticipantAt(63, "Siren", Now);
        Assert.Equal(DadHomeWorldReturnAction.InvokeLifestream, gate.Evaluate(participant, true, false, RelogTarget, Now).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.Accepted, "accepted"), Now);
        participant.CurrentLocation = Location(74, "Coeurl", Now.AddSeconds(1));

        Assert.Equal(DadHomeWorldReturnAction.Wait, gate.Evaluate(
            participant, true, true, RelogTarget, Now.AddSeconds(1)).Action);
        Assert.Equal(DadHomeWorldReturnAction.Ready, gate.Evaluate(
            participant, true, false, RelogTarget, Now.AddSeconds(2)).Action);
    }

    [Fact]
    public void ExplicitFalseAllowsThreeAttemptsAtTenSecondIntervals()
    {
        var gate = new DadHomeWorldReturnGate();
        var participant = ParticipantAt(63, "Siren", Now);

        Assert.Equal(DadHomeWorldReturnAction.InvokeLifestream, gate.Evaluate(participant, true, false, RelogTarget, Now).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.ExplicitFalse, "false"), Now);
        participant.CurrentLocation!.ObservedAtUtc = Now.AddSeconds(9);
        var retryWait = gate.Evaluate(participant, true, false, RelogTarget, Now.AddSeconds(9));
        Assert.Equal(DadHomeWorldReturnAction.Wait, retryWait.Action);
        Assert.Contains("Character@Coeurl", retryWait.Summary, StringComparison.Ordinal);
        Assert.Contains("Target@Coeurl", retryWait.Summary, StringComparison.Ordinal);
        participant.CurrentLocation.ObservedAtUtc = Now.AddSeconds(10);
        Assert.Equal(DadHomeWorldReturnAction.InvokeLifestream, gate.Evaluate(participant, true, false, RelogTarget, Now.AddSeconds(10)).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.ExplicitFalse, "false"), Now.AddSeconds(10));
        participant.CurrentLocation.ObservedAtUtc = Now.AddSeconds(20);
        Assert.Equal(DadHomeWorldReturnAction.InvokeLifestream, gate.Evaluate(participant, true, false, RelogTarget, Now.AddSeconds(20)).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.ExplicitFalse, "false"), Now.AddSeconds(20));

        Assert.Equal(3, gate.InvocationCount);
        Assert.Equal(DadHomeWorldReturnAction.Reject, gate.Evaluate(participant, true, false, RelogTarget, Now.AddSeconds(20)).Action);
    }

    [Fact]
    public void UncertainChangeWorldAcceptanceFailsWithoutRetry()
    {
        var gate = new DadHomeWorldReturnGate();
        var participant = ParticipantAt(63, "Siren", Now);
        Assert.Equal(DadHomeWorldReturnAction.InvokeLifestream, gate.Evaluate(participant, true, false, RelogTarget, Now).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.Uncertain, "IPC exception"), Now);

        var decision = gate.Evaluate(participant, true, false, RelogTarget, Now.AddSeconds(20));
        Assert.Equal(DadHomeWorldReturnAction.Reject, decision.Action);
        Assert.Equal(1, gate.InvocationCount);
        Assert.Contains("no retry", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptedTravelPreservesFrozenIdentityThroughUnavailableObservations()
    {
        var gate = new DadHomeWorldReturnGate();
        var participant = ParticipantAt(63, "Siren", Now);
        Assert.Equal(
            DadHomeWorldReturnAction.InvokeLifestream,
            gate.Evaluate(participant, true, false, RelogTarget, Now).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.Accepted, "accepted"), Now);

        participant.IsAvailable = false;
        participant.WorldReadyStable = false;
        participant.CurrentLocation = null;
        var unavailable = gate.Evaluate(participant, false, true, RelogTarget, Now.AddSeconds(1));

        Assert.Equal(DadHomeWorldReturnAction.Wait, unavailable.Action);
        Assert.Equal(
            "Character@Coeurl is Data Center traveling back to home world Coeurl before DAD relogs to Target@Coeurl; waiting for fresh home-world proof.",
            unavailable.Summary);
        Assert.Equal("Character@Coeurl", gate.FrozenSourceCharacterKey);
        Assert.Equal("Coeurl", gate.FrozenHomeWorldName);
        Assert.Equal("Target@Coeurl", gate.FrozenRelogTargetCharacterKey);
    }

    [Fact]
    public void ExplicitFalseRetryPreservesFrozenIdentityThroughLoadingObservation()
    {
        var gate = new DadHomeWorldReturnGate();
        var participant = ParticipantAt(63, "Siren", Now);
        Assert.Equal(
            DadHomeWorldReturnAction.InvokeLifestream,
            gate.Evaluate(participant, true, false, RelogTarget, Now).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.ExplicitFalse, "false"), Now);

        participant.IsAvailable = false;
        participant.WorldReadyStable = false;
        var loading = gate.Evaluate(participant, false, true, RelogTarget, Now.AddSeconds(5));

        Assert.Equal(DadHomeWorldReturnAction.Wait, loading.Action);
        Assert.Contains("Character@Coeurl", loading.Summary, StringComparison.Ordinal);
        Assert.Contains("home world Coeurl", loading.Summary, StringComparison.Ordinal);
        Assert.Contains("Target@Coeurl", loading.Summary, StringComparison.Ordinal);

        participant.IsAvailable = true;
        participant.WorldReadyStable = true;
        participant.CurrentLocation = Location(63, "Siren", Now.AddSeconds(10));
        Assert.Equal(
            DadHomeWorldReturnAction.InvokeLifestream,
            gate.Evaluate(participant, true, false, RelogTarget, Now.AddSeconds(10)).Action);
    }

    [Fact]
    public void FrozenRelogTargetDriftFailsClosed()
    {
        var gate = new DadHomeWorldReturnGate();
        var participant = ParticipantAt(63, "Siren", Now);
        Assert.Equal(
            DadHomeWorldReturnAction.InvokeLifestream,
            gate.Evaluate(participant, true, false, RelogTarget, Now).Action);

        var decision = gate.Evaluate(
            participant,
            true,
            false,
            new DadCharacterKey("Different@Coeurl"),
            Now.AddSeconds(1));

        Assert.Equal(DadHomeWorldReturnAction.Reject, decision.Action);
        Assert.Contains("frozen relog target changed", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingHomeIdentityRejectsWhileIncompleteCurrentObservationWaits()
    {
        var missingHome = ParticipantAt(63, "Siren", Now);
        missingHome.Character.WorldId = 0;
        var missingCurrent = ParticipantAt(63, "Siren", Now);
        missingCurrent.CurrentLocation!.DataCenterId = 0;

        Assert.Equal(DadHomeWorldReturnAction.Reject, new DadHomeWorldReturnGate().Evaluate(
            missingHome, true, false, RelogTarget, Now).Action);
        Assert.Equal(DadHomeWorldReturnAction.Wait, new DadHomeWorldReturnGate().Evaluate(
            missingCurrent, true, false, RelogTarget, Now).Action);
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
