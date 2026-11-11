using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadCoordinatorTravelRulesTests
{
    private static readonly DateTime Now = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(DadModuleId.Duty)]
    [InlineData(DadModuleId.Msq)]
    [InlineData(DadModuleId.DutySupport)]
    [InlineData(DadModuleId.Trust)]
    [InlineData(DadModuleId.PremadeDuty)]
    [InlineData(DadModuleId.DailyMsq)]
    [InlineData(DadModuleId.Commendation)]
    [InlineData(DadModuleId.Astrope)]
    [InlineData(DadModuleId.CustomDuty)]
    [InlineData(DadModuleId.Squadron)]
    [InlineData(DadModuleId.VariantVvd)]
    public void ConcreteDutyModulesSelectRegionalPartyReadiness(DadModuleId module)
        => Assert.Equal(DadPartyLocationRequirement.Region,
            DadCoordinatorTravelRules.ResolveLocationRequirement(LocationPlan(module)));

    [Theory]
    [InlineData(DadModuleId.LootGoblin, DadPartyLocationRequirement.World)]
    [InlineData(DadModuleId.GatherParty, DadPartyLocationRequirement.DataCenter)]
    [InlineData(DadModuleId.Mogtome, DadPartyLocationRequirement.DataCenter)]
    [InlineData(DadModuleId.Blunderville, DadPartyLocationRequirement.DataCenter)]
    [InlineData(DadModuleId.None, DadPartyLocationRequirement.DataCenter)]
    [InlineData(DadModuleId.Mixed, DadPartyLocationRequirement.DataCenter)]
    [InlineData((DadModuleId)999, DadPartyLocationRequirement.DataCenter)]
    public void PhysicalAndUnknownModulesRetainTheirRequiredTravelProof(
        DadModuleId module, DadPartyLocationRequirement expected)
        => Assert.Equal(expected, DadCoordinatorTravelRules.ResolveLocationRequirement(LocationPlan(module)));

    [Fact]
    public void ConcreteMixedDutyPlansStayRegionalWhilePhysicalModulesTakePrecedence()
    {
        var duties = LocationPlan(DadModuleId.Duty, DadModuleId.DailyMsq);
        Assert.Equal(DadModuleId.Mixed, duties.CompositeModuleId);
        Assert.Equal(DadPartyLocationRequirement.Region, DadCoordinatorTravelRules.ResolveLocationRequirement(duties));
        Assert.Equal(DadPartyLocationRequirement.World, DadCoordinatorTravelRules.ResolveLocationRequirement(
            LocationPlan(DadModuleId.Duty, DadModuleId.LootGoblin)));
        Assert.Equal(DadPartyLocationRequirement.World, DadCoordinatorTravelRules.ResolveLocationRequirement(
            LocationPlan(DadModuleId.LootGoblin, DadModuleId.Duty)));
        Assert.Equal(DadPartyLocationRequirement.DataCenter, DadCoordinatorTravelRules.ResolveLocationRequirement(
            LocationPlan(DadModuleId.Duty, DadModuleId.GatherParty)));
    }

    [Fact]
    public void FormationOnlyAndEmptyPlansRetainTheExistingDataCenterRequirement()
    {
        var formation = LocationPlan(DadModuleId.Duty);
        formation.Orchestration.AutoPartyFormationOnly = true;
        Assert.Equal(DadPartyLocationRequirement.DataCenter, DadCoordinatorTravelRules.ResolveLocationRequirement(formation));
        var orchestrationClone = formation.Orchestration.Clone();
        Assert.True(orchestrationClone.AutoPartyFormationOnly);
        formation.Orchestration = orchestrationClone;
        Assert.Equal(DadPartyLocationRequirement.DataCenter, DadCoordinatorTravelRules.ResolveLocationRequirement(formation));
        var requestClone = DadAutoPartyRuntimeRequestRules.ClonePreservingRuntimeMetadata(formation.Request);
        Assert.True(requestClone.Orchestration.AutoPartyFormationOnly);
        formation.Orchestration = requestClone.Orchestration;
        Assert.Equal(DadPartyLocationRequirement.DataCenter, DadCoordinatorTravelRules.ResolveLocationRequirement(formation));
        Assert.Equal(DadPartyLocationRequirement.DataCenter, DadCoordinatorTravelRules.ResolveLocationRequirement(LocationPlan()));
    }

    [Theory]
    [InlineData(DadPartyLocationRequirement.DataCenter)]
    [InlineData(DadPartyLocationRequirement.Region)]
    [InlineData(DadPartyLocationRequirement.World)]
    public void FrozenLocationRequirementSurvivesCloneAndWakeSerialization(DadPartyLocationRequirement requirement)
    {
        var coordinator = Participant("coordinator", "account-coordinator", "Lead@Home", 111, "slot1",
            Location(20, "TargetDC", 3, "TargetWorld", 2, "North America", Now));
        Assert.True(DadCoordinatorTravelRules.TryFreezeTarget("run", coordinator, Now,
            out var target, out var blocker, requirement), blocker);
        Assert.Equal(requirement, target.LocationRequirement);
        var clone = target.Clone();
        Assert.NotSame(target, clone);
        Assert.Equal(requirement, clone.LocationRequirement);
        var wake = new DadWakeRequestDto { RunId = "run", CoordinatorTravelTarget = clone };
        var restored = DadIpcJson.Deserialize<DadWakeRequestDto>(DadIpcJson.Serialize(wake));
        Assert.NotNull(restored);
        Assert.Equal(requirement, restored!.CoordinatorTravelTarget!.LocationRequirement);
        Assert.Equal(111UL, restored.CoordinatorTravelTarget.CoordinatorContentId);
        Assert.Equal("coordinator", restored.CoordinatorTravelTarget.CoordinatorWorkerSessionId.Value);
        Assert.Equal(3u, restored.CoordinatorTravelTarget.WorldId);
        Assert.Equal(Now, restored.CoordinatorTravelTarget.CapturedAtUtc);
        Assert.Equal(DadPartyLocationRequirement.DataCenter, new DadCoordinatorTravelTarget().LocationRequirement);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void UnknownLocationRequirementsCannotBecomeTravelOrInvitationAuthority(int value)
    {
        var context = Context(10, 20, 2, 2);
        context.Assignment.CoordinatorTravelTarget!.LocationRequirement = (DadPartyLocationRequirement)value;
        Assert.False(context.Assignment.CoordinatorTravelTarget.IsComplete);
        Assert.Equal(DadClientTravelAction.Reject, new DadClientTravelGate().Evaluate(context, Now).Action);
        Assert.False(DadIpcJson.TryDeserialize<DadWakeRequestDto>(DadIpcJson.Serialize(context.Assignment),
            out var rejectedWireAssignment, out var wireBlocker));
        Assert.Null(rejectedWireAssignment);
        Assert.Contains("location requirement", wireBlocker, StringComparison.OrdinalIgnoreCase);
        var coordinator = Participant("coordinator", "account-coordinator", "Lead@Home", 111, "slot1",
            Location(20, "TargetDC", 3, "TargetWorld", 2, "North America", Now));
        Assert.False(DadCoordinatorTravelRules.TryFreezeTarget("run", coordinator, Now,
            out _, out var blocker, (DadPartyLocationRequirement)value));
        Assert.NotEmpty(blocker);
    }

    [Fact]
    public void FreshSameRegionDutyReadinessAcrossDataCentersDoesNotRequestPhysicalTravel()
    {
        var context = Context(10, 20, 2, 2);
        var target = context.Assignment.CoordinatorTravelTarget!;
        target.LocationRequirement = DadPartyLocationRequirement.Region;
        context.Safety = new DadClientTravelSafetyEvidence();
        var gate = new DadClientTravelGate();
        Assert.True(DadCoordinatorTravelRules.MatchesTarget(context.Participant.CurrentLocation, target));
        var decision = gate.Evaluate(context, Now);
        Assert.Equal(DadClientTravelAction.Ready, decision.Action);
        Assert.Equal(0, gate.InvocationCount);
        Assert.True(string.IsNullOrEmpty(decision.DestinationWorldName));
        Assert.Equal(10u, context.Participant.CurrentLocation!.DataCenterId);
        Assert.Equal(20u, target.DataCenterId);
        Assert.Equal(4u, target.WorldId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("incomplete")]
    public void RegionalDutyReadinessStillRequiresFreshCompleteLocation(string problem)
    {
        var context = Context(10, 20, 2, 2);
        context.Assignment.CoordinatorTravelTarget!.LocationRequirement = DadPartyLocationRequirement.Region;
        switch (problem)
        {
            case "missing": context.Participant.CurrentLocation = null; break;
            case "stale": context.Participant.CurrentLocation!.ObservedAtUtc = Now.AddSeconds(-16); break;
            case "future": context.Participant.CurrentLocation!.ObservedAtUtc = Now.AddSeconds(3); break;
            case "incomplete": context.Participant.CurrentLocation!.WorldId = 0; break;
        }
        var gate = new DadClientTravelGate();
        Assert.Equal(DadClientTravelAction.Wait, gate.Evaluate(context, Now).Action);
        Assert.Equal(0, gate.InvocationCount);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("character")]
    [InlineData("content")]
    [InlineData("run")]
    [InlineData("slot")]
    [InlineData("worker")]
    public void RegionalDutyReadinessCannotReuseForeignAssignmentIdentity(string identity)
    {
        var context = Context(10, 20, 2, 2);
        context.Assignment.CoordinatorTravelTarget!.LocationRequirement = DadPartyLocationRequirement.Region;
        var gate = new DadClientTravelGate();
        Assert.Equal(DadClientTravelAction.Ready, gate.Evaluate(context, Now).Action);
        switch (identity)
        {
            case "account": context.Participant.ManagedAccountKey = new("foreign-account"); break;
            case "character": context.Participant.ActiveCharacterKey = new("Foreign@Home"); break;
            case "content": context.Participant.Character.ContentId++; break;
            case "run": context.Participant.RunId = "foreign-run"; break;
            case "slot": context.Participant.AssignedSlotId = "slot3"; break;
            case "worker": context.Participant.WorkerSessionId = new("foreign-worker"); break;
        }
        Assert.NotEqual(DadClientTravelAction.Ready, gate.Evaluate(context, Now).Action);
        Assert.Equal(0, gate.InvocationCount);
    }

    [Fact]
    public void ChangingTheFrozenLocationRequirementPermanentlyRejectsThatTravelAttempt()
    {
        var context = Context(10, 20, 2, 2);
        var target = context.Assignment.CoordinatorTravelTarget!;
        target.LocationRequirement = DadPartyLocationRequirement.Region;
        var gate = new DadClientTravelGate();
        Assert.Equal(DadClientTravelAction.Ready, gate.Evaluate(context, Now).Action);
        target.LocationRequirement = DadPartyLocationRequirement.DataCenter;
        Assert.Equal(DadClientTravelAction.Reject, gate.Evaluate(context, Now).Action);
        target.LocationRequirement = DadPartyLocationRequirement.Region;
        Assert.Equal(DadClientTravelAction.Reject, gate.Evaluate(context, Now).Action);
        Assert.Equal(0, gate.InvocationCount);
    }

    [Fact]
    public void AnotherRegionDoesNotSatisfyRegionalDutyReadiness()
    {
        var context = Context(10, 20, 2, 2);
        context.Assignment.CoordinatorTravelTarget!.LocationRequirement = DadPartyLocationRequirement.Region;
        context.Participant.CurrentLocation!.RegionId = 3;
        context.Participant.CurrentLocation.RegionName = "Europe";
        Assert.False(DadCoordinatorTravelRules.MatchesTarget(
            context.Participant.CurrentLocation, context.Assignment.CoordinatorTravelTarget));
        var decision = new DadClientTravelGate().Evaluate(context, Now);
        Assert.Equal(DadClientTravelAction.InvokeLifestream, decision.Action);
        Assert.Equal("TargetWorld", decision.DestinationWorldName);
    }

    [Fact]
    public void PhysicalWorldRequirementTravelsEvenWithinTheSameDataCenter()
    {
        var context = Context(20, 20, 2, 2);
        context.Assignment.CoordinatorTravelTarget!.LocationRequirement = DadPartyLocationRequirement.World;
        Assert.False(DadCoordinatorTravelRules.MatchesTarget(
            context.Participant.CurrentLocation, context.Assignment.CoordinatorTravelTarget));
        var decision = new DadClientTravelGate().Evaluate(context, Now);
        Assert.Equal(DadClientTravelAction.InvokeLifestream, decision.Action);
        Assert.Equal("TargetWorld", decision.DestinationWorldName);
    }

    [Theory]
    [InlineData(39, DadClientTravelAction.InvokeLifestream)]
    [InlineData(40, DadClientTravelAction.Reject)]
    public void RegionalDutyPolicyPreservesPhysicalOceTravelAndExactAccountCapacity(
        int oceCharacters, DadClientTravelAction expected)
    {
        var context = Context(10, 40, 4, 2);
        context.Assignment.CoordinatorTravelTarget!.LocationRequirement = DadPartyLocationRequirement.Region;
        context.OceCapacityProof = CompleteProof("account-a", oceCharacters);
        var decision = new DadClientTravelGate().Evaluate(context, Now);
        Assert.Equal(expected, decision.Action);
        if (expected == DadClientTravelAction.InvokeLifestream)
            Assert.Equal("TargetWorld", decision.DestinationWorldName);
        else
            Assert.Contains("cap 40", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MixedManifestRegionalReadinessUsesOnlyItsExactLanLocationProof()
    {
        var coordinator = Participant("coordinator", "account-coordinator", "Lead@Home", 111, "slot1",
            Location(20, "TargetDC", 3, "TargetWorld", 2, "North America", Now));
        var requirement = DadCoordinatorTravelRules.ResolveLocationRequirement(LocationPlan(DadModuleId.Duty));
        Assert.True(DadCoordinatorTravelRules.TryFreezeTarget("run", coordinator, Now,
            out var target, out var blocker, requirement), blocker);
        var client = Participant("client", "account-a", "Client@Home", 222, "slot2",
            Location(10, "OtherDC", 7, "OtherWorld", 2, "North America", Now));
        var island = Participant("island-worker", "", "", 0, "slot3", null);
        island.RegisteredIslandId = "registered-island";
        var manifest = new DadRunSlotManifest
        {
            Slots =
            [
                new() { SlotId = "slot1", WorkerSessionId = coordinator.WorkerSessionId, RouteKind = DadRunSlotRouteKind.LanWorker },
                new() { SlotId = "slot2", WorkerSessionId = client.WorkerSessionId, RouteKind = DadRunSlotRouteKind.LanWorker },
                new() { SlotId = "slot3", IslandId = "registered-island", RouteKind = DadRunSlotRouteKind.RegisteredIsland },
            ],
        };
        var participants = new[] { coordinator, client, island };
        var lan = DadCoordinatorTravelRules.SelectLanParticipants(manifest, participants);
        Assert.Equal(new[] { coordinator, client }, lan);
        Assert.Null(island.CurrentLocation);
        Assert.True(DadCoordinatorTravelRules.ValidateParticipants(target, lan, Now).Ready);
        client.CurrentLocation!.ObservedAtUtc = Now.AddSeconds(-16);
        Assert.False(DadCoordinatorTravelRules.ValidateParticipants(target, lan, Now).Ready);
        client.CurrentLocation.ObservedAtUtc = Now;
        client.CurrentLocation.RegionId = 3;
        client.CurrentLocation.RegionName = "Europe";
        Assert.False(DadCoordinatorTravelRules.ValidateParticipants(target, lan, Now).Ready);
        client.CurrentLocation.RegionId = 2;
        client.CurrentLocation.RegionName = "North America";
        client.WorkerSessionId = new("replacement-client");
        Assert.Single(DadCoordinatorTravelRules.SelectLanParticipants(manifest, participants));
    }

    [Theory]
    [InlineData("world")]
    [InlineData("account")]
    [InlineData("character")]
    [InlineData("content")]
    [InlineData("missing-route")]
    [InlineData("duplicate-route")]
    public void RegionalDutyProofRetainsExactFrozenCoordinatorAuthority(string conflict)
    {
        var coordinator = Participant("coordinator", "account-coordinator", "Lead@Home", 111, "slot1",
            Location(20, "TargetDC", 3, "TargetWorld", 2, "North America", Now));
        Assert.True(DadCoordinatorTravelRules.TryFreezeTarget("run", coordinator, Now,
            out var target, out var blocker, DadPartyLocationRequirement.Region), blocker);
        var client = Participant("client", "account-a", "Client@Home", 222, "slot2",
            Location(10, "OtherDC", 7, "OtherWorld", 2, "North America", Now));
        var participants = new List<DadParticipantSnapshot> { coordinator, client };
        Assert.True(DadCoordinatorTravelRules.ValidateParticipants(target, participants, Now).Ready);
        switch (conflict)
        {
            case "world": coordinator.CurrentLocation!.WorldId++; break;
            case "account": coordinator.ManagedAccountKey = new("foreign-account"); break;
            case "character": coordinator.ActiveCharacterKey = new("Foreign@Home"); break;
            case "content": coordinator.Character.ContentId++; break;
            case "missing-route": participants.Remove(coordinator); break;
            case "duplicate-route": participants.Add(coordinator.Clone()); break;
        }
        var rejected = DadCoordinatorTravelRules.ValidateParticipants(target, participants, Now);
        Assert.False(rejected.Ready);
        Assert.NotEmpty(rejected.Summary);
    }

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
    public void ExplicitFalseRetriesContinueWithoutAttemptLimitAtFiveSecondIntervals()
    {
        var gate = new DadClientTravelGate();
        var context = Context(currentDcId: 10, targetDcId: 20, targetRegionId: 2, homeRegionId: 2);

        for (var attempt = 1; attempt <= 200; attempt++)
        {
            var second = (attempt - 1) * 5;
            var retry = Observe(gate, context, second);
            Assert.Equal(DadClientTravelAction.InvokeLifestream, retry.Action);
            Assert.Equal(attempt, retry.AttemptNumber);
            gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.ExplicitFalse, "false"), Now.AddSeconds(second));
            Assert.Equal(DadClientTravelAction.Wait, Observe(gate, context, second + 4).Action);
        }
        Assert.Equal(200, gate.InvocationCount);
        Assert.Equal(DadClientTravelAction.InvokeLifestream, Observe(gate, context, 1000).Action);
    }

    [Fact]
    public void AcceptedInvocationDoesNotRepeatBeforeRecoveryProofAndWaitsForTargetDcProof()
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
    public void AcceptedButAbortedTravelRetriesAfterFiveSecondsOfFreshSafeIdleProof()
    {
        var gate = new DadClientTravelGate();
        var context = Context(currentDcId: 10, targetDcId: 20, targetRegionId: 2, homeRegionId: 2);
        Assert.Equal(DadClientTravelAction.InvokeLifestream, gate.Evaluate(context, Now).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.Accepted, "accepted"), Now);
        for (var second = 1; second <= 5; second++)
        {
            context.Participant.CurrentLocation!.ObservedAtUtc = Now.AddSeconds(second);
            Assert.Equal(DadClientTravelAction.Wait, gate.Evaluate(context, Now.AddSeconds(second)).Action);
        }
        context.Participant.CurrentLocation!.ObservedAtUtc = Now.AddSeconds(6);
        var retry = gate.Evaluate(context, Now.AddSeconds(6));
        Assert.Equal(DadClientTravelAction.InvokeLifestream, retry.Action);
        Assert.Equal(2, retry.AttemptNumber);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("unavailable")]
    [InlineData("world")]
    [InlineData("offline")]
    [InlineData("post-ar")]
    [InlineData("vermaxion")]
    [InlineData("external-owner")]
    [InlineData("ar-busy")]
    [InlineData("ar-multimode")]
    [InlineData("ar-unavailable")]
    [InlineData("stale")]
    [InlineData("identity")]
    [InlineData("location-missing")]
    public void InterruptedAcceptedTravelRecoveryRequiresANewFullSafeIdleWindow(string interruption)
    {
        var (gate, context) = AcceptedTravel();
        Assert.Equal(DadClientTravelAction.Wait, Observe(gate, context, 1).Action);
        Assert.Equal(DadClientTravelAction.Wait, Observe(gate, context, 3).Action);
        context.Participant.CurrentLocation!.ObservedAtUtc = Now.AddSeconds(4);
        switch (interruption)
        {
            case "busy": context.Safety.LifestreamBusy = true; break;
            case "unavailable": context.Safety.LifestreamAvailable = false; break;
            case "world": context.Participant.WorldReadyStable = false; break;
            case "offline": context.Participant.IsAvailable = false; break;
            case "post-ar": context.Participant.PostArReady = false; break;
            case "vermaxion": context.Safety.VermaxionSafe = false; break;
            case "external-owner": context.Participant.ExternalAutomationHeld = true; break;
            case "ar-busy": context.Safety.AutoRetainerBusy = true; break;
            case "ar-multimode": context.Safety.AutoRetainerMultiModeEnabled = true; break;
            case "ar-unavailable": context.Safety.AutoRetainerAvailable = false; break;
            case "stale": context.Participant.CurrentLocation.ObservedAtUtc = Now.AddMinutes(-1); break;
            case "identity": context.Participant.Character.ContentId++; break;
            case "location-missing": context.Participant.CurrentLocation = null; break;
        }
        Assert.Equal(DadClientTravelAction.Wait, gate.Evaluate(context, Now.AddSeconds(4)).Action);
        var restored = Context(10, 20, 2, 2);
        foreach (var second in new[] { 5, 6, 7, 8, 9 })
            Assert.Equal(DadClientTravelAction.Wait, Observe(gate, restored, second).Action);
        Assert.Equal(DadClientTravelAction.InvokeLifestream, Observe(gate, restored, 10).Action);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObservationGapsAndWorldChangesCannotCountAsContinuousIdle(bool worldChange)
    {
        var (gate, context) = AcceptedTravel();
        Observe(gate, context, 1);
        if (worldChange)
            context.Participant.CurrentLocation!.WorldId++;
        var restarted = worldChange ? 3 : 31;
        foreach (var second in Enumerable.Range(restarted, 5))
            Assert.Equal(DadClientTravelAction.Wait, Observe(gate, context, second).Action);
        Assert.Equal(DadClientTravelAction.InvokeLifestream, Observe(gate, context, restarted + 5).Action);
    }

    [Fact]
    public void AcceptedFailuresRetryBeyondThreeAttemptsAndTenMinutesWithoutPendingDuplicates()
    {
        var (gate, context) = AcceptedTravel();
        for (var attempt = 2; attempt <= 200; attempt++)
        {
            var start = (attempt - 2) * 6 + 1;
            foreach (var second in Enumerable.Range(start, 5))
                Assert.Equal(DadClientTravelAction.Wait, Observe(gate, context, second).Action);
            var retry = Observe(gate, context, start + 5);
            Assert.Equal(DadClientTravelAction.InvokeLifestream, retry.Action);
            Assert.Equal(attempt, retry.AttemptNumber);
            Assert.Equal(DadClientTravelAction.Wait, Observe(gate, context, start + 5).Action);
            gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.Accepted, "accepted"), Now.AddSeconds(start + 5));
        }
        Assert.Equal(200, gate.InvocationCount);
    }

    [Fact]
    public void BusyTravelWaitsWithoutTimeoutAndRetriesOnlyAfterItBecomesSafelyIdle()
    {
        var (gate, context) = AcceptedTravel();
        context.Safety.LifestreamBusy = true;
        Assert.Equal(DadClientTravelAction.Wait, Observe(gate, context, 300).Action);
        Assert.Equal(DadClientTravelAction.Wait, Observe(gate, context, 600).Action);
        Assert.Equal(DadClientTravelAction.Wait, Observe(gate, context, 86400).Action);
        Assert.Equal(1, gate.InvocationCount);
        context.Safety.LifestreamBusy = false;
        foreach (var second in Enumerable.Range(86401, 5))
            Assert.Equal(DadClientTravelAction.Wait, Observe(gate, context, second).Action);
        Assert.Equal(DadClientTravelAction.InvokeLifestream, Observe(gate, context, 86406).Action);
        gate.Reset();
        Assert.Equal(DadClientTravelAction.InvokeLifestream, Observe(gate, context, 86407).Action);
        Assert.Equal(0, gate.InvocationCount);
    }

    [Fact]
    public void ConfirmedArrivalEvenAfterLongTravelStopsFurtherRetries()
    {
        var (gate, context) = AcceptedTravel();
        context.Participant.CurrentLocation = Location(20, "TargetDC", 4, "TargetWorld", 2, "North America", Now.AddSeconds(600));
        Assert.Equal(DadClientTravelAction.Ready, gate.Evaluate(context, Now.AddSeconds(600)).Action);
        Assert.Equal(DadClientTravelAction.Ready, Observe(gate, context, 900).Action);
        Assert.Equal(1, gate.InvocationCount);
    }

    [Theory]
    [InlineData("logged-out")]
    [InlineData("loading")]
    [InlineData("casting")]
    [InlineData("post-ar")]
    public void MissingLifestreamBusyFlagNeverAllowsReplayDuringLogoutLoginOrLoading(string condition)
    {
        var (gate, context) = AcceptedTravel();
        switch (condition)
        {
            case "logged-out": context.Participant.IsAvailable = false; break;
            case "loading": context.Participant.CurrentLocation = null; break;
            case "casting": context.Participant.WorldReadyStable = false; break;
            case "post-ar": context.Participant.PostArReady = false; break;
        }
        foreach (var second in new[] { 300, 3600, 86400 })
        {
            if (context.Participant.CurrentLocation != null)
                context.Participant.CurrentLocation.ObservedAtUtc = Now.AddSeconds(second);
            Assert.Equal(DadClientTravelAction.Wait, gate.Evaluate(context, Now.AddSeconds(second)).Action);
        }
        Assert.Equal(1, gate.InvocationCount);
        var restored = Context(10, 20, 2, 2);
        foreach (var second in Enumerable.Range(86401, 5))
            Assert.Equal(DadClientTravelAction.Wait, Observe(gate, restored, second).Action);
        Assert.Equal(DadClientTravelAction.InvokeLifestream, Observe(gate, restored, 86406).Action);
    }

    [Fact]
    public void ChangedWorkerOrDestinationPolicyNeverAuthorizesAnAcceptedRecovery()
    {
        var (workerGate, workerContext) = AcceptedTravel();
        workerContext.Participant.WorkerSessionId = "different-worker";
        Assert.Equal(DadClientTravelAction.Reject, Observe(workerGate, workerContext, 1).Action);
        Assert.Equal(1, workerGate.InvocationCount);
        var (policyGate, policyContext) = AcceptedTravel();
        policyContext.HomeRegionId = 3;
        Assert.Equal(DadClientTravelAction.Reject, Observe(policyGate, policyContext, 1).Action);
        Assert.Equal(1, policyGate.InvocationCount);
    }

    [Fact]
    public void MixedExplicitFalseAndAcceptedRetriesKeepAttemptNumbersAndUncertaintyStillNeverRetries()
    {
        var gate = new DadClientTravelGate();
        var context = Context(10, 20, 2, 2);
        Assert.Equal(DadClientTravelAction.InvokeLifestream, Observe(gate, context, 0).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.ExplicitFalse, "false"), Now);
        Assert.Equal(2, Observe(gate, context, 5).AttemptNumber);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.Accepted, "accepted"), Now.AddSeconds(5));
        foreach (var second in Enumerable.Range(6, 5))
            Assert.Equal(DadClientTravelAction.Wait, Observe(gate, context, second).Action);
        Assert.Equal(3, Observe(gate, context, 11).AttemptNumber);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.Uncertain, "IPC exception"), Now.AddSeconds(11));
        Assert.Equal(DadClientTravelAction.Reject, Observe(gate, context, 80).Action);
        Assert.Equal(3, gate.InvocationCount);
    }

    [Fact]
    public void OceVisitorCapacityMustBeAuthorizedAgainBeforeAcceptedRecovery()
    {
        var gate = new DadClientTravelGate();
        var context = Context(10, 40, 4, 2);
        context.OceCapacityProof = CompleteProof("account-a", 39);
        Assert.Equal(DadClientTravelAction.InvokeLifestream, gate.Evaluate(context, Now).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.Accepted, "accepted"), Now);
        context.OceCapacityProof = CompleteProof("account-a", 40);
        context.OceCapacityProof.ObservedAtUtc = Now.AddSeconds(1);
        Assert.Equal(DadClientTravelAction.Reject, Observe(gate, context, 1).Action);
        Assert.Equal(1, gate.InvocationCount);
    }

    private static (DadClientTravelGate Gate, DadClientTravelContext Context) AcceptedTravel()
    {
        var gate = new DadClientTravelGate();
        var context = Context(10, 20, 2, 2);
        Assert.Equal(DadClientTravelAction.InvokeLifestream, gate.Evaluate(context, Now).Action);
        gate.RecordInvocationResult(new(DadLifestreamChangeWorldOutcome.Accepted, "accepted"), Now);
        return (gate, context);
    }

    private static DadClientTravelDecision Observe(DadClientTravelGate gate, DadClientTravelContext context, int second)
    {
        context.Participant.CurrentLocation!.ObservedAtUtc = Now.AddSeconds(second);
        return gate.Evaluate(context, Now.AddSeconds(second));
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

    [Fact]
    public void MixedPartyTravelIgnoresRegisteredIslandWithoutLocationButStillRequiresLanProof()
    {
        var coordinator = Participant("coordinator", "account-coordinator", "Lead@Home", 111, "slot1",
            Location(20, "TargetDC", 3, "TargetWorld", 2, "North America", Now));
        Assert.True(DadCoordinatorTravelRules.TryFreezeTarget("run", coordinator, Now, out var target, out var blocker), blocker);
        var client = Participant("client", "account-a", "Client@Home", 222, "slot2",
            Location(20, "TargetDC", 7, "VisitorWorld", 2, "North America", Now));
        var registeredIsland = Participant(
            "autoparty-proposal-slot3",
            string.Empty,
            "remote-slot3",
            0,
            "slot3",
            null);
        registeredIsland.RegisteredIslandId = "island-remote";
        registeredIsland.IsAvailable = false;
        registeredIsland.PostArReady = false;
        registeredIsland.WorldReadyStable = false;
        var manifest = new DadRunSlotManifest
        {
            Slots =
            [
                new DadFrozenRunSlot
                {
                    SlotId = "slot1",
                    RouteKind = DadRunSlotRouteKind.LanWorker,
                    WorkerSessionId = coordinator.WorkerSessionId,
                },
                new DadFrozenRunSlot
                {
                    SlotId = "slot2",
                    RouteKind = DadRunSlotRouteKind.LanWorker,
                    WorkerSessionId = client.WorkerSessionId,
                },
                new DadFrozenRunSlot
                {
                    SlotId = "slot3",
                    RouteKind = DadRunSlotRouteKind.RegisteredIsland,
                    IslandId = "island-remote",
                },
            ],
        };

        var participants = new[] { coordinator, client, registeredIsland };
        var ready = DadCoordinatorTravelRules.ValidateParticipants(
            target,
            DadCoordinatorTravelRules.SelectLanParticipants(manifest, participants),
            Now);

        Assert.True(ready.Ready, ready.Summary);
        Assert.Contains("2 participant(s)", ready.Summary, StringComparison.Ordinal);

        client.CurrentLocation = null;
        var blocked = DadCoordinatorTravelRules.ValidateParticipants(
            target,
            DadCoordinatorTravelRules.SelectLanParticipants(manifest, participants),
            Now);

        Assert.False(blocked.Ready);
        Assert.Contains("slot2", blocked.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private static DadRunPlan LocationPlan(params DadModuleId[] modules)
    {
        var intent = new DadOrchestrationIntent();
        return new DadRunPlan
        {
            Request = new DadRunRequest { Orchestration = intent },
            Orchestration = intent,
            CompositeModuleId = modules.Length == 1 ? modules[0] : DadModuleId.Mixed,
            Modules = modules.Select(module => new DadPlannedModuleExecution { ModuleId = module }).ToList(),
        };
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
