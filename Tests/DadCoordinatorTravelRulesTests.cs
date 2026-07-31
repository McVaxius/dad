using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadCoordinatorTravelRulesTests
{
    private static readonly DateTime Now = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SameDataCenterIsNoOpEvenWhenMutationGatesAreUnavailable()
    {
        var context = Context(currentDcId: 20, targetDcId: 20, targetRegionId: 2, homeRegionId: 2);
        context.Safety = new DadClientTravelSafetyEvidence();

        var decision = new DadClientTravelGate().Evaluate(context, Now);

        Assert.Equal(DadClientTravelAction.Ready, decision.Action);
    }

    [Fact]
    public void SameHomeRegionAllowsGuardedTravel()
    {
        var decision = new DadClientTravelGate().Evaluate(
            Context(currentDcId: 10, targetDcId: 20, targetRegionId: 2, homeRegionId: 2),
            Now);

        Assert.Equal(DadClientTravelAction.InvokeLifestream, decision.Action);
        Assert.Equal("TargetWorld", decision.DestinationWorldName);
    }

    [Fact]
    public void AuthenticatedCoordinatorMaySendARemoteSlotOneAssemblyTarget()
    {
        var context = Context(currentDcId: 10, targetDcId: 20, targetRegionId: 2, homeRegionId: 2);
        context.Assignment.AuthorityWorkerSessionId = new DadWorkerSessionId("coordinator-worker");
        context.Assignment.CoordinatorTravelTarget!.CoordinatorWorkerSessionId =
            new DadWorkerSessionId("remote-slot1-worker");

        var decision = new DadClientTravelGate().Evaluate(context, Now);

        Assert.Equal(DadClientTravelAction.InvokeLifestream, decision.Action);
        Assert.Equal("remote-slot1-worker",
            context.Assignment.CoordinatorTravelTarget.CoordinatorWorkerSessionId.Value);
    }

    [Fact]
    public void NonOceHomeMayVisitOceBelowExactAccountCap()
    {
        var context = Context(currentDcId: 10, targetDcId: 40, targetRegionId: 4, homeRegionId: 2);
        context.OceCapacityProof = CompleteProof("account-a", oceHomeCount: 39);

        var decision = new DadClientTravelGate().Evaluate(context, Now);

        Assert.Equal(DadClientTravelAction.InvokeLifestream, decision.Action);
    }

    [Fact]
    public void NonOceHomeCannotVisitOceAtFortyCharacterCap()
    {
        var context = Context(currentDcId: 10, targetDcId: 40, targetRegionId: 4, homeRegionId: 2);
        context.OceCapacityProof = CompleteProof("account-a", oceHomeCount: 40);

        var decision = new DadClientTravelGate().Evaluate(context, Now);

        Assert.Equal(DadClientTravelAction.Reject, decision.Action);
        Assert.Contains("cap 40", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OceHomeCharacterMayReturnToOceWithoutVisitorProofAtCap()
    {
        var context = Context(currentDcId: 20, targetDcId: 40, targetRegionId: 4, homeRegionId: 4);

        var decision = new DadClientTravelGate().Evaluate(context, Now);

        Assert.Equal(DadClientTravelAction.InvokeLifestream, decision.Action);
    }

    [Fact]
    public void OtherAccountOceRowsDoNotConsumeExactAccountCapacity()
    {
        var context = Context(currentDcId: 10, targetDcId: 40, targetRegionId: 4, homeRegionId: 2);
        var proof = CompleteProof("account-a", oceHomeCount: 0);
        for (var index = 0; index < 40; index++)
        {
            proof.Characters.Add(ProofRow("account-b", $"Other {index}@Oce", (ulong)(9000 + index), 4));
        }
        context.OceCapacityProof = proof;

        var decision = new DadClientTravelGate().Evaluate(context, Now);

        Assert.Equal(DadClientTravelAction.InvokeLifestream, decision.Action);
    }

    [Fact]
    public void IncompleteOrWrongAccountOceProofFailsClosed()
    {
        var incomplete = Context(currentDcId: 10, targetDcId: 40, targetRegionId: 4, homeRegionId: 2);
        incomplete.OceCapacityProof = CompleteProof("account-a", oceHomeCount: 1);
        incomplete.OceCapacityProof.IsComplete = false;
        var wrongAccount = Context(currentDcId: 10, targetDcId: 40, targetRegionId: 4, homeRegionId: 2);
        wrongAccount.OceCapacityProof = CompleteProof("account-b", oceHomeCount: 1);
        var stale = Context(currentDcId: 10, targetDcId: 40, targetRegionId: 4, homeRegionId: 2);
        stale.OceCapacityProof = CompleteProof("account-a", oceHomeCount: 1);
        stale.OceCapacityProof.ObservedAtUtc = Now.AddSeconds(-31);

        Assert.Equal(DadClientTravelAction.Reject, new DadClientTravelGate().Evaluate(incomplete, Now).Action);
        Assert.Equal(DadClientTravelAction.Reject, new DadClientTravelGate().Evaluate(wrongAccount, Now).Action);
        Assert.Equal(DadClientTravelAction.Reject, new DadClientTravelGate().Evaluate(stale, Now).Action);
    }

    [Theory]
    [InlineData("world")]
    [InlineData("post-ar")]
    [InlineData("vermaxion")]
    [InlineData("autoretainer")]
    [InlineData("lifestream")]
    public void EveryMutationSafetyGateMustPass(string failedGate)
    {
        var context = Context(currentDcId: 10, targetDcId: 20, targetRegionId: 2, homeRegionId: 2);
        switch (failedGate)
        {
            case "world":
                context.Participant.WorldReadyStable = false;
                break;
            case "post-ar":
                context.Participant.PostArReady = false;
                break;
            case "vermaxion":
                context.Safety.VermaxionSafe = false;
                break;
            case "autoretainer":
                context.Safety.AutoRetainerBusy = true;
                break;
            case "lifestream":
                context.Safety.LifestreamBusy = true;
                break;
        }

        var decision = new DadClientTravelGate().Evaluate(context, Now);

        Assert.Equal(DadClientTravelAction.Wait, decision.Action);
    }

    [Fact]
    public void ExplicitFalseRetriesAreBoundedToThreeAtTenSecondIntervals()
    {
        var gate = new DadClientTravelGate();
        var context = Context(currentDcId: 10, targetDcId: 20, targetRegionId: 2, homeRegionId: 2);

        Assert.Equal(DadClientTravelAction.InvokeLifestream, gate.Evaluate(context, Now).Action);
        gate.RecordInvocationResult(new DadLifestreamChangeWorldResult(DadLifestreamChangeWorldOutcome.ExplicitFalse, "false"), Now);
        Assert.Equal(DadClientTravelAction.Wait, gate.Evaluate(context, Now.AddSeconds(9)).Action);
        Assert.Equal(DadClientTravelAction.InvokeLifestream, gate.Evaluate(context, Now.AddSeconds(10)).Action);
        gate.RecordInvocationResult(new DadLifestreamChangeWorldResult(DadLifestreamChangeWorldOutcome.ExplicitFalse, "false"), Now.AddSeconds(10));
        context.Participant.CurrentLocation!.ObservedAtUtc = Now.AddSeconds(20);
        Assert.Equal(DadClientTravelAction.InvokeLifestream, gate.Evaluate(context, Now.AddSeconds(20)).Action);
        gate.RecordInvocationResult(new DadLifestreamChangeWorldResult(DadLifestreamChangeWorldOutcome.ExplicitFalse, "false"), Now.AddSeconds(20));

        Assert.Equal(3, gate.InvocationCount);
        Assert.Equal(DadClientTravelAction.Reject, gate.Evaluate(context, Now.AddMinutes(1)).Action);
    }

    [Fact]
    public void AcceptedInvocationIsNeverRepeatedAndWaitsForTargetDcProof()
    {
        var gate = new DadClientTravelGate();
        var context = Context(currentDcId: 10, targetDcId: 20, targetRegionId: 2, homeRegionId: 2);
        Assert.Equal(DadClientTravelAction.InvokeLifestream, gate.Evaluate(context, Now).Action);
        gate.RecordInvocationResult(new DadLifestreamChangeWorldResult(DadLifestreamChangeWorldOutcome.Accepted, "accepted"), Now);

        Assert.Equal(DadClientTravelAction.Wait, gate.Evaluate(context, Now.AddSeconds(1)).Action);
        context.Participant.CurrentLocation = Location(20, "TargetDC", 4, "ArrivedWorld", 2, "North America", Now.AddSeconds(2));
        Assert.Equal(DadClientTravelAction.Ready, gate.Evaluate(context, Now.AddSeconds(2)).Action);
        Assert.Equal(1, gate.InvocationCount);
    }

    [Fact]
    public void UncertainIpcResultAndImmutableTargetCollisionNeverRetry()
    {
        var uncertainGate = new DadClientTravelGate();
        var context = Context(currentDcId: 10, targetDcId: 20, targetRegionId: 2, homeRegionId: 2);
        Assert.Equal(DadClientTravelAction.InvokeLifestream, uncertainGate.Evaluate(context, Now).Action);
        uncertainGate.RecordInvocationResult(new DadLifestreamChangeWorldResult(DadLifestreamChangeWorldOutcome.Uncertain, "IPC exception"), Now);
        Assert.Equal(DadClientTravelAction.Reject, uncertainGate.Evaluate(context, Now.AddMinutes(1)).Action);
        Assert.Equal(1, uncertainGate.InvocationCount);

        var collisionGate = new DadClientTravelGate();
        Assert.Equal(DadClientTravelAction.InvokeLifestream, collisionGate.Evaluate(context, Now).Action);
        var changed = Context(currentDcId: 10, targetDcId: 20, targetRegionId: 2, homeRegionId: 2);
        changed.Assignment.CoordinatorTravelTarget!.WorldName = "ChangedWorld";
        Assert.Equal(DadClientTravelAction.Reject, collisionGate.Evaluate(changed, Now.AddSeconds(1)).Action);
    }

    [Fact]
    public void CoordinatorFreezesCurrentWorldAndRequiresExactClientDcProof()
    {
        var coordinator = Participant("coordinator", "account-coordinator", "Lead@Home", 111, "slot1",
            Location(20, "TargetDC", 3, "TargetWorld", 2, "North America", Now));
        Assert.True(DadCoordinatorTravelRules.TryFreezeTarget("run", coordinator, Now, out var target, out var blocker), blocker);
        var client = Participant("client", "account-a", "Client@Home", 222, "slot2",
            Location(20, "TargetDC", 7, "VisitorWorld", 2, "North America", Now));

        var ready = DadCoordinatorTravelRules.ValidateParticipants(target, [coordinator, client], Now);
        Assert.True(ready.Ready, ready.Summary);

        client.CurrentLocation = Location(10, "OtherDC", 8, "OtherWorld", 2, "North America", Now);
        var wrongDc = DadCoordinatorTravelRules.ValidateParticipants(target, [coordinator, client], Now);
        Assert.False(wrongDc.Ready);
        Assert.False(wrongDc.ImmutableTargetChanged);
    }

    [Fact]
    public void MissingStaleAndChangedCoordinatorLocationProofFailClosed()
    {
        var coordinator = Participant("coordinator", "account-coordinator", "Lead@Home", 111, "slot1",
            Location(20, "TargetDC", 3, "TargetWorld", 2, "North America", Now));
        Assert.True(DadCoordinatorTravelRules.TryFreezeTarget("run", coordinator, Now, out var target, out _));
        var client = Participant("client", "account-a", "Client@Home", 222, "slot2", null);
        Assert.False(DadCoordinatorTravelRules.ValidateParticipants(target, [coordinator, client], Now).Ready);

        client.CurrentLocation = Location(20, "TargetDC", 7, "VisitorWorld", 2, "North America", Now.AddSeconds(-16));
        Assert.False(DadCoordinatorTravelRules.ValidateParticipants(target, [coordinator, client], Now).Ready);

        client.CurrentLocation = Location(20, "TargetDC", 7, "VisitorWorld", 2, "North America", Now);
        coordinator.CurrentLocation = Location(20, "TargetDC", 9, "ChangedWorld", 2, "North America", Now);
        var changed = DadCoordinatorTravelRules.ValidateParticipants(target, [coordinator, client], Now);
        Assert.False(changed.Ready);
        Assert.True(changed.ImmutableTargetChanged);
    }

    private static DadClientTravelContext Context(uint currentDcId, uint targetDcId, uint targetRegionId, uint homeRegionId)
    {
        var participant = Participant(
            "client",
            "account-a",
            "Client@Home",
            222,
            "slot2",
            Location(currentDcId, currentDcId == targetDcId ? "TargetDC" : "CurrentDC", 5, "CurrentWorld", 2, "North America", Now));
        return new DadClientTravelContext
        {
            Assignment = new DadWakeRequestDto
            {
                RunId = "run",
                AuthorityWorkerSessionId = "coordinator",
                RequiredAccountKey = "account-a",
                RequiredCharacterKey = "Client@Home",
                RequiredContentId = 222,
                AssignedSlotId = "slot2",
                CoordinatorTravelTarget = new DadCoordinatorTravelTarget
                {
                    RunId = "run",
                    CoordinatorWorkerSessionId = "coordinator",
                    CoordinatorAccountKey = "account-coordinator",
                    CoordinatorCharacterKey = "Lead@Home",
                    CoordinatorContentId = 111,
                    WorldId = 4,
                    WorldName = "TargetWorld",
                    DataCenterId = targetDcId,
                    DataCenterName = "TargetDC",
                    RegionId = targetRegionId,
                    RegionName = targetRegionId == 4 ? "Oceania" : "North America",
                    CapturedAtUtc = Now,
                },
            },
            Participant = participant,
            HomeRegionId = homeRegionId,
            HomeRegionName = homeRegionId == 4 ? "Oceania" : "North America",
            Safety = new DadClientTravelSafetyEvidence
            {
                VermaxionSafe = true,
                AutoRetainerAvailable = true,
                LifestreamAvailable = true,
            },
        };
    }

    private static DadParticipantSnapshot Participant(
        string worker,
        string account,
        string character,
        ulong contentId,
        string slot,
        DadWorldLocationObservation? location)
        => new()
        {
            RunId = "run",
            WorkerSessionId = worker,
            ManagedAccountKey = account,
            ActiveCharacterKey = character,
            AssignedSlotId = slot,
            Character = new DadAcquiredCharacter
            {
                CharacterKey = character,
                ContentId = contentId,
            },
            CurrentLocation = location,
            IsAvailable = true,
            WorldReadyStable = true,
            PostArReady = true,
        };

    private static DadWorldLocationObservation Location(
        uint dcId,
        string dcName,
        uint worldId,
        string worldName,
        uint regionId,
        string regionName,
        DateTime observedAtUtc)
        => new()
        {
            DataCenterId = dcId,
            DataCenterName = dcName,
            WorldId = worldId,
            WorldName = worldName,
            RegionId = regionId,
            RegionName = regionName,
            ObservedAtUtc = observedAtUtc,
        };

    private static DadOceTravelCapacityProof CompleteProof(string account, int oceHomeCount)
    {
        var rows = Enumerable.Range(0, oceHomeCount)
            .Select(index => ProofRow(account, $"Oce {index}@Oce", (ulong)(1000 + index), 4))
            .ToList();
        rows.Add(ProofRow(account, "Client@Home", 222, 2));
        return new DadOceTravelCapacityProof
        {
            AccountKey = account,
            IsFullRosterAvailable = true,
            IsComplete = true,
            XadbContractVersion = 6,
            AdvertisedCharacterCount = rows.Count,
            AttributedCharacterCount = rows.Count,
            ObservedAtUtc = Now,
            Characters = rows,
        };
    }

    private static DadOceRosterCharacterProof ProofRow(string account, string character, ulong contentId, uint regionId)
        => new()
        {
            AccountKey = account,
            CharacterKey = character,
            ContentId = contentId,
            HomeWorldId = 1,
            HomeWorldName = "Home",
            HomeRegionId = regionId,
            HomeRegionName = regionId == 4 ? "Oceania" : "North America",
        };
}
