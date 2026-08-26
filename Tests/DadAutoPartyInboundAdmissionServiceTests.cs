using System.Collections.Immutable;
using AutoParty.Contracts;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAutoPartyInboundAdmissionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadyMixedPlanAdmitsOnlyLocalOwnedParticipant()
    {
        var fixture = new Fixture();

        var result = fixture.Service.Admit(Proposal(), fixture.Publication);

        Assert.True(result.Ready, result.SafeBlocker);
        Assert.Equal("run-inbound-admission", result.RunId);
        Assert.Equal(["Slot2"], result.OwnedSlotIds.ToArray());
        var target = Assert.Single(result.InviteTargets);
        Assert.Equal("Slot2", target.SlotId);
        Assert.Equal(1, fixture.WakeCalls);
        Assert.Equal(1, fixture.ClaimCalls);
        Assert.Equal(1, fixture.TakeoverCalls);
    }

    [Fact]
    public void AmbiguousOrMismatchedPublishedRouteFailsClosed()
    {
        var duplicate = new Fixture();
        duplicate.Publication = duplicate.Publication with
        {
            InboundRoutes = [duplicate.Route, duplicate.Route],
        };
        var duplicateResult = duplicate.Service.Admit(Proposal(), duplicate.Publication);
        Assert.False(duplicateResult.Ready);
        Assert.Equal(DadAutoPartyInboundAdmissionService.FleetRouteMismatch, duplicateResult.SafeBlocker);
        Assert.Empty(duplicateResult.InviteTargets);

        var mismatch = new Fixture();
        mismatch.Route.OwnerSnapshot.WorkerSessionId = new DadWorkerSessionId("different-worker");
        mismatch.Publication = mismatch.Publication with { InboundRoutes = [mismatch.Route] };
        var mismatchResult = mismatch.Service.Admit(Proposal(), mismatch.Publication);
        Assert.False(mismatchResult.Ready);
        Assert.Equal(DadAutoPartyInboundAdmissionService.FleetRouteMismatch, mismatchResult.SafeBlocker);
        Assert.Empty(mismatchResult.InviteTargets);
    }

    [Theory]
    [InlineData("wake")]
    [InlineData("readiness")]
    [InlineData("dependencies")]
    public void WakeReadinessAndDependencyBarriersRemainBlockedButExposeInvitePayload(string barrier)
    {
        var fixture = new Fixture();
        fixture.WakeOverride = (route, request) =>
        {
            var response = fixture.ReadyResponse(route, request);
            switch (barrier)
            {
                case "wake":
                    response.AcceptedAssignment = false;
                    break;
                case "readiness":
                    response.Snapshot.WorldReadyStable = false;
                    break;
                case "dependencies":
                    response.Snapshot.Dependencies = DadDependencySnapshot.CreateChecking();
                    break;
            }
            return response;
        };

        var result = fixture.Service.Admit(Proposal(), fixture.Publication);

        Assert.False(result.Ready);
        Assert.Equal(DadAutoPartyInboundAdmissionDisposition.Pending, result.Disposition);
        Assert.Equal(
            barrier switch
            {
                "wake" => DadAutoPartyInboundAdmissionService.WakeBlocked,
                "dependencies" => DadAutoPartyInboundAdmissionService.DependenciesBlocked,
                _ => DadAutoPartyInboundAdmissionService.ReadinessBlocked,
            },
            result.SafeBlocker);
        var inviteTarget = Assert.Single(result.InviteTargets);
        Assert.Equal("run-inbound-admission", inviteTarget.RunId);
        Assert.Equal("Slot2", inviteTarget.SlotId);
        Assert.Equal(0, fixture.ClaimCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeniedOrContradictoryClaimFailsClosed(bool contradictory)
    {
        var fixture = new Fixture();
        fixture.ClaimOverride = (snapshot, request) =>
        {
            var decision = fixture.GrantedDecision(snapshot, request);
            if (contradictory)
            {
                decision.Lease.SlotId = "DifferentSlot";
            }
            else
            {
                decision.Granted = false;
                decision.ClaimState = DadClaimState.Denied;
                decision.LeaseState = DadParticipantLeaseState.Denied;
            }
            return decision;
        };

        var result = fixture.Service.Admit(Proposal(), fixture.Publication);

        Assert.False(result.Ready);
        Assert.Equal(DadAutoPartyInboundAdmissionService.ClaimBlocked, result.SafeBlocker);
        Assert.Empty(result.InviteTargets);
    }

    [Fact]
    public void ExactLocatorOutputIsMemoryOnlyAndRepeatedAdmissionUsesSameRunSlotIdentity()
    {
        var fixture = new Fixture();

        var first = fixture.Service.Admit(Proposal(), fixture.Publication);
        var second = fixture.Service.Admit(Proposal(), fixture.Publication);

        Assert.True(first.Ready, first.SafeBlocker);
        Assert.True(second.Ready, second.SafeBlocker);
        foreach (var target in new[] { Assert.Single(first.InviteTargets), Assert.Single(second.InviteTargets) })
        {
            Assert.Equal("run-inbound-admission", target.RunId);
            Assert.Equal(DadModuleId.PremadeDuty, target.ModuleId);
            Assert.Equal("Slot2", target.SlotId);
            Assert.Equal("account-local", target.AccountKey.Value);
            Assert.Equal("Local Character@Alpha", target.CharacterKey.Value);
            Assert.Equal(1002UL, target.ContentId);
            Assert.Equal("Local Character", target.CharacterName);
            Assert.Equal((ushort)21, target.WorldId);
            Assert.Equal("worker-local", target.WorkerSessionId.Value);
        }
        Assert.All(fixture.WakeRequests, request =>
        {
            Assert.Equal("run-inbound-admission", request.RunId);
            Assert.Equal("Slot2", request.AssignedSlotId);
            Assert.Equal((uint)19, request.RequiredJobId);
        });
        Assert.All(fixture.ClaimRequests, request =>
        {
            Assert.Equal("run-inbound-admission", request.RunId);
            Assert.Equal("Slot2", request.SlotId);
            Assert.Equal("worker-local", request.Lease.OwningWorkerSessionId.Value);
        });
    }

    [Fact]
    public void ExpiredOrOversizedProposalFailsBeforeGatewayCallbacks()
    {
        var fixture = new Fixture();
        var expired = Proposal() with { Header = Header(expiresAt: Now) };
        var expiredResult = fixture.Service.Admit(expired, fixture.Publication);
        Assert.Equal(DadAutoPartyInboundAdmissionService.ExpiredProposal, expiredResult.SafeBlocker);

        var oversizedParticipants = Enumerable.Range(1, 9)
            .Select(index => new EndpointExecutionParticipant(
                $"Slot{index}",
                new OwnerId(Fixture.LocalOwner),
                new IslandId(Fixture.LocalIsland),
                new OpaqueCharacterId($"opaque-{index}"),
                new JobId("19"),
                index == 1 ? EndpointExecutionRole.QueueLeader : EndpointExecutionRole.Participant,
                IsInviter: index == 1))
            .ToImmutableArray();
        var oversized = Proposal() with
        {
            ExecutionPlan = Proposal().ExecutionPlan! with { Participants = oversizedParticipants },
        };
        var oversizedResult = fixture.Service.Admit(oversized, fixture.Publication);
        Assert.Equal(DadAutoPartyInboundAdmissionService.InvalidProposal, oversizedResult.SafeBlocker);
        Assert.Equal(0, fixture.WakeCalls);
        Assert.Equal(0, fixture.ClaimCalls);
    }

    [Fact]
    public void GuardedTakeoverRemainsPendingWithoutWakeOrClaimUntilReady()
    {
        var fixture = new Fixture
        {
            TakeoverOverride = (_, request) => new DadWakeTakeoverResultDto
            {
                SchedulerRunId = request.SchedulerRunId,
                SlotId = request.SlotId,
                AccountKey = request.AccountKey,
                CharacterKey = request.CharacterKey,
                OperationToken = request.OperationToken,
                Status = DadWakeTakeoverStatus.Pending,
                Phase = DadWakeTakeoverPhase.Prepared,
                AcknowledgementState = DadWakeAcknowledgementState.Accepted,
                Snapshot = Fixture.ReadySnapshot(),
            },
        };

        var result = fixture.Service.Admit(Proposal(), fixture.Publication);

        Assert.Equal(DadAutoPartyInboundAdmissionDisposition.Pending, result.Disposition);
        Assert.Equal(DadAutoPartyInboundAdmissionService.TakeoverPending, result.SafeBlocker);
        Assert.Equal(0, fixture.WakeCalls);
        Assert.Equal(0, fixture.ClaimCalls);
    }

    [Fact]
    public void ProposalExpiryRestoresTheFrozenTakeoverBeforeRemoval()
    {
        var fixture = new Fixture();
        var proposal = Proposal();
        Assert.True(fixture.Service.Admit(proposal, fixture.Publication).Ready);

        var restored = fixture.Service.RestoreProposal(
            proposal.ProposalId,
            "dad-inbound-proposal-expired");

        Assert.True(restored);
        var cancel = Assert.Single(fixture.TakeoverRequests, request =>
            request.MessageKind == DadWakeTakeoverMessageKind.Cancel);
        Assert.Equal($"autoparty-{proposal.ProposalId:N}-Slot2", cancel.OperationToken);
    }

    [Fact]
    public void QueueAndSettleBuildOnlyTheExactLocalWorkerCommand()
    {
        var fixture = new Fixture();
        var proposal = Proposal();
        var plan = Assert.IsType<EndpointExecutionPlan>(proposal.ExecutionPlan);
        var live = fixture.Route.OwnerSnapshot.Clone();
        live.RunId = plan.RunId;
        live.AssignedSlotId = "Slot2";
        live.ClaimState = DadClaimState.Granted;
        live.LeaseState = DadParticipantLeaseState.Granted;
        live.RequestedJobPreparation = new DadRequestedJobPreparationProof
        {
            Key = new DadRequestedJobPreparationKey(
                plan.RunId,
                live.WorkerSessionId,
                "Slot2",
                live.ManagedAccountKey,
                live.ActiveCharacterKey,
                live.Character.ContentId,
                19),
            Status = DadRequestedJobPreparationStatus.AlreadyMatched,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        var target = new DadNativePartyInviteTarget
        {
            RunId = plan.RunId,
            ModuleId = DadModuleId.PremadeDuty,
            SlotId = "Slot2",
            WorkerSessionId = live.WorkerSessionId,
            AccountKey = live.ManagedAccountKey,
            CharacterKey = live.ActiveCharacterKey,
            ContentId = live.Character.ContentId,
            CharacterName = live.Character.CharacterName,
            WorldId = checked((ushort)live.Character.WorldId),
        };
        var context = new DadAutoPartyInboundExecutionContext(
            plan,
            target,
            "island-peer",
            Fixture.LocalOwner,
            DateTimeOffset.UtcNow.AddMinutes(5));
        ExecutionOperation Operation(ExecutionOperationKind kind) => new(
            Header(DateTimeOffset.UtcNow.AddMinutes(5)),
            Guid.NewGuid(),
            proposal.ProposalId,
            new OwnerId(Fixture.LocalOwner),
            kind,
            proposal.ActivityId,
            new OpaqueCharacterId("opaque-local"),
            new JobId("19"),
            null,
            3,
            FormationOnly: false,
            PartyInviteTargets: default,
            ModuleReference: new EndpointExecutionModuleReference(0, nameof(DadModuleId.PremadeDuty)));

        Assert.True(DadAutoPartyInboundExecutionRules.TryBuildWorkerCommand(
            Operation(ExecutionOperationKind.Queue), context, live, out var queue, out var queueParticipant, out var queueBlocker), queueBlocker);
        Assert.True(DadAutoPartyInboundExecutionRules.TryBuildWorkerCommand(
            Operation(ExecutionOperationKind.Settle), context, live, out var settle, out var settleParticipant, out var settleBlocker), settleBlocker);
        Assert.Equal(2, queue.Participants.Count);
        Assert.Single(queue.Participants, static participant => participant.IsLocalClient);
        Assert.Equal(live.WorkerSessionId, queueParticipant.WorkerSessionId);
        Assert.Equal(queue.CommandId, settle.CommandId);
        Assert.Equal(queueParticipant.WorkerSessionId, settleParticipant.WorkerSessionId);
        Assert.DoesNotContain(queue.Participants, participant =>
            participant.IsLocalClient && !string.Equals(participant.AssignedSlotId, "Slot2", StringComparison.OrdinalIgnoreCase));
    }

    private static RunProposal Proposal()
        => new(
            Header(),
            Guid.Parse("38a011ce-578d-4677-8c75-764558c57013"),
            new OwnerId("owner-peer"),
            new ActivityId("dad-duty-1"),
            [
                new ParticipantRequest(
                    new OwnerId("owner-peer"),
                    new IslandId("island-peer"),
                    new OpaqueCharacterId("opaque-peer"),
                    new JobId("24")),
                new ParticipantRequest(
                    new OwnerId(Fixture.LocalOwner),
                    new IslandId(Fixture.LocalIsland),
                    new OpaqueCharacterId("opaque-local"),
                    new JobId("19")),
            ],
            "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            new EndpointExecutionPlan(
                "run-inbound-admission",
                FormationOnly: false,
                RequirePostArReady: true,
                ParticipantReadyTimeoutSeconds: 120,
                AssemblyTimeoutSeconds: 90,
                LeaseDurationSeconds: 300,
                RepairPolicy: new EndpointRepairPolicy(false, 75, "self"),
                Participants:
                [
                    new EndpointExecutionParticipant(
                        "Slot1",
                        new OwnerId("owner-peer"),
                        new IslandId("island-peer"),
                        new OpaqueCharacterId("opaque-peer"),
                        new JobId("24"),
                        EndpointExecutionRole.QueueLeader,
                        IsInviter: true),
                    new EndpointExecutionParticipant(
                        "Slot2",
                        new OwnerId(Fixture.LocalOwner),
                        new IslandId(Fixture.LocalIsland),
                        new OpaqueCharacterId("opaque-local"),
                        new JobId("19"),
                        EndpointExecutionRole.Participant,
                        IsInviter: false),
                ],
                Modules:
                [
                    new EndpointExecutionModule(
                        0,
                        nameof(DadModuleId.PremadeDuty),
                        new ActivityId("dad-duty-1"),
                        "Fixture Duty",
                        "duty-finder-duty",
                        1,
                        0,
                        Unsynced: false,
                        ExpectedPartySize: 2),
                ]));

    private static ContractHeader Header(DateTimeOffset? expiresAt = null)
        => new(
            AutoPartyProtocol.CurrentVersion,
            Guid.Parse("8503afc5-b2d4-4fda-acaa-e4d58d7ca86f"),
            "inbound-admission-proposal",
            new IslandId("island-peer"),
            new IslandId(Fixture.LocalIsland),
            Now.AddMinutes(-1),
            expiresAt ?? Now.AddMinutes(5),
            1,
            1,
            3,
            1,
            ContractHeader.CreateNonce(new byte[AutoPartyProtocol.ContractNonceBytes]),
            ImmutableArray<int>.Empty);

    private sealed class Fixture
    {
        public const string LocalOwner = "owner-local";
        public const string LocalIsland = "island-local";

        public Fixture()
        {
            var owner = ReadySnapshot();
            Route = new DadAutoPartyInboundRoute(
                "opaque-local",
                owner.ManagedAccountKey,
                owner.ActiveCharacterKey,
                owner.Character.ContentId,
                owner.Character.CharacterName,
                owner.Character.WorldId,
                owner.Character.WorldName,
                owner.WorkerSessionId,
                owner.ClientInstanceId,
                owner,
                Now);
            Publication = new DadAutoPartyListingPublication(
                new DadAutoPartySharePolicy(),
                [])
            {
                InboundRoutes = [Route],
            };
            Service = new DadAutoPartyInboundAdmissionService(
                LocalOwner,
                LocalIsland,
                new DadWorkerSessionId("authority-worker"),
                (route, request) =>
                {
                    TakeoverCalls++;
                    TakeoverRequests.Add(request);
                    return TakeoverOverride?.Invoke(route, request) ?? ReadyTakeover(route, request);
                },
                (route, request) =>
                {
                    WakeCalls++;
                    WakeRequests.Add(request.Clone());
                    return WakeOverride?.Invoke(route, request) ?? ReadyResponse(route, request);
                },
                (request, snapshot, duration) =>
                {
                    var lease = new DadParticipantLeaseRecord
                    {
                        RunId = request.RunId,
                        SlotId = request.SlotId,
                        AssignedAccountKey = request.RequiredAccountKey,
                        AssignedCharacterKey = request.RequiredCharacterKey,
                        OwningWorkerSessionId = snapshot.WorkerSessionId,
                        IssuedUtc = Now.UtcDateTime,
                        RenewedUtc = Now.UtcDateTime,
                        ExpiresUtc = Now.UtcDateTime + duration,
                        State = DadParticipantLeaseState.Pending,
                    };
                    return lease;
                },
                (snapshot, request) =>
                {
                    ClaimCalls++;
                    ClaimRequests.Add(CloneClaimRequest(request));
                    return ClaimOverride?.Invoke(snapshot, request) ?? GrantedDecision(snapshot, request);
                },
                () => Now,
                TimeSpan.FromSeconds(15));
        }

        public DadAutoPartyInboundAdmissionService Service { get; }
        public DadAutoPartyListingPublication Publication { get; set; }
        public DadAutoPartyInboundRoute Route { get; }
        public List<DadWakeTakeoverRequestDto> TakeoverRequests { get; } = [];
        public List<DadWakeRequestDto> WakeRequests { get; } = [];
        public List<DadClaimRequestDto> ClaimRequests { get; } = [];
        public int TakeoverCalls { get; private set; }
        public int WakeCalls { get; private set; }
        public int ClaimCalls { get; private set; }
        public Func<DadAutoPartyInboundRoute, DadWakeTakeoverRequestDto, DadWakeTakeoverResultDto?>?
            TakeoverOverride { get; set; }
        public Func<DadParticipantSnapshot, DadWakeRequestDto, DadParticipantReadyDto?>? WakeOverride { get; set; }
        public Func<DadParticipantSnapshot, DadClaimRequestDto, DadClaimDecisionDto?>? ClaimOverride { get; set; }

        public DadParticipantReadyDto ReadyResponse(DadParticipantSnapshot route, DadWakeRequestDto request)
        {
            var snapshot = route.Clone();
            snapshot.RunId = request.RunId;
            snapshot.AssignedSlotId = request.AssignedSlotId;
            snapshot.State = DadParticipantState.Ready;
            snapshot.PostArReady = true;
            return new DadParticipantReadyDto
            {
                RunId = request.RunId,
                WorkerSessionId = snapshot.WorkerSessionId,
                CharacterKey = snapshot.ActiveCharacterKey,
                State = DadParticipantState.Ready,
                PostArReady = true,
                AcceptedAssignment = true,
                Snapshot = snapshot,
            };
        }

        public DadClaimDecisionDto GrantedDecision(DadParticipantSnapshot snapshot, DadClaimRequestDto request)
        {
            var lease = request.Lease.Clone();
            lease.State = DadParticipantLeaseState.Granted;
            return new DadClaimDecisionDto
            {
                RunId = request.RunId,
                WorkerSessionId = snapshot.WorkerSessionId,
                Granted = true,
                ClaimState = DadClaimState.Granted,
                LeaseState = DadParticipantLeaseState.Granted,
                CharacterKey = snapshot.ActiveCharacterKey,
                Lease = lease,
                Snapshot = snapshot.Clone(),
            };
        }

        private static DadWakeTakeoverResultDto ReadyTakeover(
            DadAutoPartyInboundRoute route,
            DadWakeTakeoverRequestDto request)
        {
            var snapshot = route.OwnerSnapshot.Clone();
            snapshot.ActiveCharacterKey = route.CharacterKey;
            snapshot.Character = snapshot.Character.Clone();
            snapshot.Character.CharacterKey = route.CharacterKey.Value;
            snapshot.Character.ContentId = route.ContentId;
            snapshot.Character.CharacterName = route.CharacterName;
            snapshot.Character.WorldId = route.WorldId;
            snapshot.Character.WorldName = route.WorldName;
            snapshot.Character.CurrentJobId = 19;
            return new DadWakeTakeoverResultDto
            {
                SchedulerRunId = request.SchedulerRunId,
                SlotId = request.SlotId,
                AccountKey = request.AccountKey,
                CharacterKey = request.CharacterKey,
                OperationToken = request.OperationToken,
                Status = request.MessageKind == DadWakeTakeoverMessageKind.Cancel
                    ? DadWakeTakeoverStatus.Blocked
                    : DadWakeTakeoverStatus.Ready,
                Stage = request.MessageKind == DadWakeTakeoverMessageKind.Cancel
                    ? DadWakeTakeoverStage.Blocked
                    : DadWakeTakeoverStage.Ready,
                Phase = request.MessageKind == DadWakeTakeoverMessageKind.Cancel
                    ? DadWakeTakeoverPhase.Cancelled
                    : DadWakeTakeoverPhase.Ready,
                AcknowledgementState = DadWakeAcknowledgementState.Executed,
                Snapshot = snapshot,
            };
        }

        internal static DadParticipantSnapshot ReadySnapshot()
            => new()
            {
                ClientInstanceId = "client-local",
                WorkerSessionId = new DadWorkerSessionId("worker-local"),
                IsLocalClient = true,
                State = DadParticipantState.Ready,
                IsAvailable = true,
                IsEligibleForRun = true,
                PostArReady = true,
                WorldReadyStable = true,
                AutoRetainerAvailable = true,
                LastHeartbeatUtc = Now.UtcDateTime,
                ManagedAccountKey = new DadAccountKey("account-local"),
                ActiveCharacterKey = new DadCharacterKey("Local Character@Alpha"),
                Dependencies = ReadyDependencies(),
                Character = new DadAcquiredCharacter
                {
                    CharacterKey = "Local Character@Alpha",
                    ContentId = 1002,
                    CharacterName = "Local Character",
                    WorldId = 21,
                    WorldName = "Alpha",
                    AccountId = "account-local",
                    Source = DadCharacterSource.LocalRuntime,
                    Freshness = DadSnapshotFreshness.Live,
                    CurrentJobId = 19,
                    Readiness = DadReadinessState.Ready,
                },
            };

        private static DadDependencySnapshot ReadyDependencies()
            => DadDependencyRules.Evaluate(
                DadDependencyRules.Requirements.Select(requirement => new DadInstalledPluginMetadata(
                    requirement.AcceptedInternalNames[0],
                    requirement.DisplayName,
                    string.IsNullOrWhiteSpace(requirement.MinimumVersion) ? "1.0.0" : requirement.MinimumVersion,
                    IsLoaded: true,
                    IsOutdated: false)),
                revision: 1,
                checkedAtUtc: Now.UtcDateTime);

        private static DadClaimRequestDto CloneClaimRequest(DadClaimRequestDto request)
            => new()
            {
                RunId = request.RunId,
                AuthorityWorkerSessionId = request.AuthorityWorkerSessionId,
                ModuleId = request.ModuleId,
                SlotId = request.SlotId,
                RequiredAccountKey = request.RequiredAccountKey,
                RequiredCharacterKey = request.RequiredCharacterKey,
                Lease = request.Lease.Clone(),
            };
    }
}
