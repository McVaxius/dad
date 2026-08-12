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

        var result = fixture.Service.Admit(Proposal(), fixture.Rows);

        Assert.True(result.Ready, result.SafeBlocker);
        Assert.Equal("run-inbound-admission", result.RunId);
        Assert.Equal(["Slot2"], result.OwnedSlotIds);
        var target = Assert.Single(result.InviteTargets);
        Assert.Equal("Slot2", target.SlotId);
        Assert.Equal(1, fixture.WakeCalls);
        Assert.Equal(1, fixture.ClaimCalls);
        Assert.Equal("opaque-local", fixture.ResolvedRows.Single().OpaqueCharacterId);
    }

    [Fact]
    public void DuplicateFleetRowAndMismatchedWorkerRouteFailClosed()
    {
        var duplicate = new Fixture();
        duplicate.Rows.Add(duplicate.Rows[0].Clone());
        var duplicateResult = duplicate.Service.Admit(Proposal(), duplicate.Rows);
        Assert.False(duplicateResult.Ready);
        Assert.Equal(DadAutoPartyInboundAdmissionService.FleetRouteMismatch, duplicateResult.SafeBlocker);
        Assert.Empty(duplicateResult.InviteTargets);

        var mismatch = new Fixture();
        mismatch.Routes[0].ActiveCharacterKey = new DadCharacterKey("Different Character@World");
        var mismatchResult = mismatch.Service.Admit(Proposal(), mismatch.Rows);
        Assert.False(mismatchResult.Ready);
        Assert.Equal(DadAutoPartyInboundAdmissionService.WorkerRouteMismatch, mismatchResult.SafeBlocker);
        Assert.Empty(mismatchResult.InviteTargets);
    }

    [Theory]
    [InlineData("wake")]
    [InlineData("readiness")]
    [InlineData("dependencies")]
    public void WakeReadinessAndDependencyBarriersRemainBlocked(string barrier)
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

        var result = fixture.Service.Admit(Proposal(), fixture.Rows);

        Assert.False(result.Ready);
        Assert.Equal(
            barrier switch
            {
                "wake" => DadAutoPartyInboundAdmissionService.WakeBlocked,
                "dependencies" => DadAutoPartyInboundAdmissionService.DependenciesBlocked,
                _ => DadAutoPartyInboundAdmissionService.ReadinessBlocked,
            },
            result.SafeBlocker);
        Assert.Empty(result.InviteTargets);
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

        var result = fixture.Service.Admit(Proposal(), fixture.Rows);

        Assert.False(result.Ready);
        Assert.Equal(DadAutoPartyInboundAdmissionService.ClaimBlocked, result.SafeBlocker);
        Assert.Empty(result.InviteTargets);
    }

    [Fact]
    public void ExactLocatorOutputIsMemoryOnlyAndRepeatedAdmissionUsesSameRunSlotIdentity()
    {
        var fixture = new Fixture();

        var first = fixture.Service.Admit(Proposal(), fixture.Rows);
        var second = fixture.Service.Admit(Proposal(), fixture.Rows);

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
        var expiredResult = fixture.Service.Admit(expired, fixture.Rows);
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
        var oversizedResult = fixture.Service.Admit(oversized, fixture.Rows);
        Assert.Equal(DadAutoPartyInboundAdmissionService.InvalidProposal, oversizedResult.SafeBlocker);
        Assert.Equal(0, fixture.WakeCalls);
        Assert.Equal(0, fixture.ClaimCalls);
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
            Rows =
            [
                new DadAutoPartyFleetRow
                {
                    RowId = "row-local",
                    OpaqueCharacterId = "opaque-local",
                    AccountKey = "account-local",
                    CharacterKey = "Local Character@Alpha",
                    JobId = 19,
                    Enabled = true,
                    IsRemote = false,
                },
            ];
            Routes = [ReadySnapshot()];
            Service = new DadAutoPartyInboundAdmissionService(
                LocalOwner,
                LocalIsland,
                new DadWorkerSessionId("authority-worker"),
                row =>
                {
                    ResolvedRows.Add(row);
                    return Routes;
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
        public List<DadAutoPartyFleetRow> Rows { get; }
        public List<DadParticipantSnapshot> Routes { get; }
        public List<DadAutoPartyFleetRow> ResolvedRows { get; } = [];
        public List<DadWakeRequestDto> WakeRequests { get; } = [];
        public List<DadClaimRequestDto> ClaimRequests { get; } = [];
        public int WakeCalls { get; private set; }
        public int ClaimCalls { get; private set; }
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

        private static DadParticipantSnapshot ReadySnapshot()
            => new()
            {
                WorkerSessionId = new DadWorkerSessionId("worker-local"),
                State = DadParticipantState.Ready,
                IsAvailable = true,
                IsEligibleForRun = true,
                PostArReady = true,
                WorldReadyStable = true,
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
