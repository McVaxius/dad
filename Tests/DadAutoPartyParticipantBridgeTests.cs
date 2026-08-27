using System.Collections.Immutable;
using System.Security.Cryptography;
using AutoParty.Contracts;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAutoPartyParticipantBridgeTests
{
    private const string LocalIsland = "island-local";
    private const string RemoteIsland = "island-remote";
    private const string RemoteOwner = "owner-remote";
    private const string RemoteCharacter = "opaque-character-1";
    private const uint RequestedJob = 19;

    [Fact]
    public void ProposalDispatchIsPerIslandAndRequiresExplicitAcknowledgement()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = ActiveConfiguration();
        configuration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: true));
        configuration.RemoteBindings.Add(Binding("opaque-character-2", ownsQueueAuthority: false));
        var bridge = new DadAutoPartyParticipantBridge(configuration);
        var (plan, manifest, proposalId) = Runtime(
            new RemoteSlot("Slot1", RemoteCharacter, IsLeader: true),
            new RemoteSlot("Slot2", "opaque-character-2", IsLeader: false));

        Assert.True(bridge.TryBindRun(plan, manifest, now, out var blocker), blocker);
        Assert.Equal(1, bridge.PendingCommandCount);

        var first = bridge.LeasePendingCommands(8, TimeSpan.FromSeconds(10), now);
        var proposal = Assert.Single(first.Commands);
        Assert.Equal(DadAutoPartyParticipantCommandKind.Proposal, proposal.CommandKind);
        Assert.Equal(RemoteIsland, proposal.IslandId);
        Assert.Equal(2, proposal.Participants?.Count);
        Assert.Equal(proposalId, proposal.ProposalId);
        Assert.Equal(1, bridge.PendingCommandCount);

        Assert.Equal(1, bridge.ReleasePendingCommands(first.DispatchLeaseId, now));
        var retry = bridge.LeasePendingCommands(8, TimeSpan.FromSeconds(10), now);
        Assert.Equal(proposal.CommandId, Assert.Single(retry.Commands).CommandId);
        Assert.Equal(1, bridge.AcknowledgePendingCommands(
            retry.DispatchLeaseId,
            [proposal.CommandId],
            now));
        Assert.Equal(0, bridge.PendingCommandCount);
        Assert.Empty(bridge.LeasePendingCommands(8, TimeSpan.FromSeconds(10), now).Commands);
    }

    [Fact]
    public void FrenRiderProfileIsFrozenBeforeOperationsAndDispatchAdvancesWithoutApplicationReceipt()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = ActiveConfiguration();
        configuration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: true));
        var firstFrame = FrenRiderProfileCodec.Encode("{\"frenName\":\"Frozen\",\"enabled\":false}");
        var replacementFrame = FrenRiderProfileCodec.Encode("{\"frenName\":\"Changed\",\"enabled\":true}");
        var currentFrame = firstFrame;
        var profileCalls = 0;
        var bridge = new DadAutoPartyParticipantBridge(
            configuration,
            useFrenRiderProvider: static () => true,
            remoteProfileProvider: _ =>
            {
                profileCalls++;
                return new DadAutoPartyRemoteProfileResult(true, currentFrame, "ok");
            });
        var (plan, manifest, proposalId) = Runtime(
            new RemoteSlot("Slot1", RemoteCharacter, IsLeader: true));

        Assert.True(bridge.TryBindRun(plan, manifest, now, out var blocker), blocker);
        currentFrame = replacementFrame;

        var initial = bridge.LeasePendingCommands(8, TimeSpan.FromSeconds(10), now);
        Assert.Collection(
            initial.Commands,
            proposal =>
            {
                Assert.Equal(DadAutoPartyParticipantCommandKind.Proposal, proposal.CommandKind);
                Assert.True(Assert.IsType<EndpointExecutionPlan>(proposal.ExecutionPlan).UseFrenRider);
            },
            profile =>
            {
                Assert.Equal(DadAutoPartyParticipantCommandKind.IntegrationProfile, profile.CommandKind);
                Assert.Equal(firstFrame, profile.FrenRiderProfile);
            });
        Assert.Equal(1, profileCalls);
        Assert.Equal(2, bridge.AcknowledgePendingCommands(
            initial.DispatchLeaseId,
            initial.Commands.Select(static command => command.CommandId).ToList(),
            now));

        Assert.True(bridge.ObserveReservation(new Reservation(
            Header(RemoteIsland, LocalIsland, now),
            Guid.NewGuid(),
            proposalId,
            new OwnerId(RemoteOwner),
            new OpaqueCharacterId(RemoteCharacter),
            ExpectedStateGeneration: 1,
            ObservedStateGeneration: 1), now, out blocker), blocker);
        Assert.True(bridge.ObservePreflight(new PreflightResult(
            Header(RemoteIsland, LocalIsland, now),
            proposalId,
            new OwnerId(RemoteOwner),
            Ready: true,
            ReadinessGeneration: 1,
            ExpectedStateGeneration: 1,
            SafeBlockers: ImmutableArray<string>.Empty,
            ObservedStateGeneration: 1), now, out blocker), blocker);
        Assert.True(bridge.ObserveLease(new SessionLease(
            Header(RemoteIsland, LocalIsland, now),
            Guid.NewGuid(),
            proposalId,
            new OwnerId(RemoteOwner),
            now.AddMinutes(10),
            SessionPermission.All,
            ExpectedStateGeneration: 1,
            ObservedStateGeneration: 1), now, out blocker), blocker);
        Assert.True(bridge.ObserveInviteTarget(
            Header(RemoteIsland, LocalIsland, now),
            proposalId,
            new OwnerId(RemoteOwner),
            new OpaqueCharacterId(RemoteCharacter),
            new DadWorkerSessionId("private-worker"),
            new DadAccountKey("private-account"),
            new DadCharacterKey("private-character"),
            1001,
            "Private Character",
            21,
            now.AddMinutes(2),
            now,
            out blocker), blocker);

        Assert.True(bridge.RequestOperation(
            proposalId,
            "Slot1",
            ExecutionOperationKind.Form,
            null,
            inviter: null,
            partyInviteTargets: [],
            now,
            out blocker), blocker);
        var form = LeaseAndAcknowledgeSingle(bridge, now);
        Assert.Equal(ExecutionOperationKind.Form, form.OperationKind);
        Assert.True(bridge.IsOperationComplete(
            proposalId,
            "Slot1",
            ExecutionOperationKind.Form,
            now));

        Assert.True(bridge.RequestOperation(
            proposalId,
            "Slot1",
            ExecutionOperationKind.Queue,
            moduleIndex: 0,
            inviter: null,
            now,
            out blocker), blocker);
        var queue = LeaseAndAcknowledgeSingle(bridge, now);
        Assert.Equal(ExecutionOperationKind.Queue, queue.OperationKind);
        Assert.True(bridge.IsOperationComplete(
            proposalId,
            "Slot1",
            ExecutionOperationKind.Queue,
            now));
        Assert.Equal(1, profileCalls);
    }

    [Fact]
    public void FrenRiderBindFailureIsAtomicBeforeAnyCommandIsSent()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = ActiveConfiguration();
        configuration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: true));
        var bridge = new DadAutoPartyParticipantBridge(
            configuration,
            useFrenRiderProvider: static () => true,
            remoteProfileProvider: static _ =>
                DadAutoPartyRemoteProfileResult.Unavailable("profile-missing"));
        var (plan, manifest, _) = Runtime(new RemoteSlot("Slot1", RemoteCharacter, IsLeader: true));

        Assert.False(bridge.TryBindRun(plan, manifest, now, out var blocker));
        Assert.Contains("profile-missing", blocker, StringComparison.Ordinal);
        Assert.Equal(0, bridge.PendingCommandCount);
        Assert.Empty(bridge.LeasePendingCommands(8, TimeSpan.FromSeconds(10), now).Commands);
    }

    [Fact]
    public void LocalAnyJobFreezesLiveCombatJobAndProposalCarriesFullExecutionPlan()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = ActiveConfiguration();
        configuration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: false));
        var localCrew = new List<DadAutoPartyCrewCandidate>
        {
            new(
                new DadAutoPartyCrewIdentity
                {
                    RosterIdentityKey = "crew-local",
                    OpaqueCharacterId = "opaque-local-character",
                },
                new DadAcquiredCharacter
                {
                    AccountId = "account-local",
                    CharacterKey = "character-local",
                },
                [RequestedJob],
                Available: true),
        };
        var bridge = new DadAutoPartyParticipantBridge(
            configuration,
            currentLocalCrewProvider: () => localCrew);
        var proposalId = Guid.NewGuid();
        var orchestration = new DadOrchestrationIntent
        {
            AutoPartyProposalId = proposalId.ToString("D"),
            QueueAuthority = DadQueueAuthority.Leader,
            InviteAuthority = DadInviteAuthority.PresetLeader,
            RequirePostArReady = false,
            WaitPolicy = new DadRunWaitPolicy
            {
                ParticipantReadyTimeoutSeconds = 321,
                AssemblyTimeoutSeconds = 123,
                LeaseDurationSeconds = 45,
            },
            RosterIntent = new DadRosterIntent
            {
                ExpectedPartySize = 2,
                RequireRemoteParticipants = true,
            },
        };
        var request = new DadRunRequest
        {
            RequestId = $"run-{Guid.NewGuid():N}",
            Orchestration = orchestration,
            PreDutyRepairPolicy = new DadPreDutyRepairPolicy
            {
                Enabled = true,
                ThresholdPercent = 42,
                Mode = DadPreDutyRepairMode.NearbyNpcNoTeleportOrInn,
            },
        };
        var plan = new DadRunPlan
        {
            Request = request,
            Orchestration = orchestration,
            RequiredParticipantCount = 2,
            RequiresRemoteParticipants = true,
            LeaderCharacterKey = "character-local",
            InviterCharacterKey = "character-local",
            CompositeModuleId = DadModuleId.PremadeDuty,
            Modules =
            [
                new DadPlannedModuleExecution
                {
                    ModuleId = DadModuleId.PremadeDuty,
                    DisplayName = "Synthetic duty",
                    ExpectedPartySize = 2,
                    RequiresPeers = true,
                },
            ],
        };
        var unboundManifest = new DadRunSlotManifest
        {
            RequestId = request.RequestId,
            ExpectedPartySize = 2,
            LeaderCharacterKey = plan.LeaderCharacterKey,
            InviterCharacterKey = plan.InviterCharacterKey,
            Modules =
            [
                new DadFrozenModulePayload
                {
                    ModuleId = DadModuleId.PremadeDuty,
                    DutyName = "Synthetic duty",
                    ContentFinderConditionId = 777,
                    Unsynced = true,
                    ExpectedPartySize = 2,
                },
            ],
            Slots =
            [
                new DadFrozenRunSlot
                {
                    SlotId = "Slot1",
                    RouteKind = DadRunSlotRouteKind.LanWorker,
                    AccountKey = new DadAccountKey("account-local"),
                    CharacterKey = new DadCharacterKey("character-local"),
                    ContentId = 123456789,
                    RequiredJobId = null,
                    AdsLootMode = DadAdsLootMode.Need,
                    IsLeader = true,
                    IsInviter = true,
                },
                new DadFrozenRunSlot
                {
                    SlotId = "Slot2",
                    RouteKind = DadRunSlotRouteKind.RegisteredIsland,
                    OwnerId = RemoteOwner,
                    IslandId = RemoteIsland,
                    OpaqueCharacterId = RemoteCharacter,
                    RequiredJobId = RequestedJob,
                    AdsLootMode = DadAdsLootMode.Pass,
                },
            ],
        };

        Assert.True(
            DadRunSlotManifestRules.TryBindWorkerSessions(
                unboundManifest,
                [new DadParticipantSnapshot
                {
                    WorkerSessionId = new DadWorkerSessionId("worker-local"),
                    ManagedAccountKey = new DadAccountKey("account-local"),
                    ActiveCharacterKey = new DadCharacterKey("character-local"),
                    Character = new DadAcquiredCharacter
                    {
                        AccountId = "account-local",
                        CharacterKey = "character-local",
                        ContentId = 123456789,
                        CurrentJobId = RequestedJob,
                        Source = DadCharacterSource.LocalRuntime,
                        Freshness = DadSnapshotFreshness.Live,
                        Readiness = DadReadinessState.Ready,
                    },
                    WorldReadyStable = true,
                }],
                out var manifest,
                out var bindBlocker),
            bindBlocker);
        Assert.Equal((uint?)RequestedJob, manifest.Slots[0].RequiredJobId);

        Assert.True(bridge.TryBindRun(plan, manifest, now, out var blocker), blocker);

        var command = Assert.Single(bridge.LeasePendingCommands(8, TimeSpan.FromSeconds(10), now).Commands);
        var requests = Assert.IsAssignableFrom<IReadOnlyList<DadAutoPartyParticipantRequest>>(command.Participants);
        Assert.Equal(2, requests.Count);
        Assert.Equal(configuration.RegisteredOwnerId, requests[0].OwnerId);
        Assert.Equal(configuration.RegisteredIslandId, requests[0].IslandId);
        Assert.Equal("opaque-local-character", requests[0].OpaqueCharacterId);
        Assert.Equal(RemoteCharacter, requests[1].OpaqueCharacterId);

        var execution = Assert.IsType<EndpointExecutionPlan>(command.ExecutionPlan);
        Assert.Equal(request.RequestId, execution.RunId);
        Assert.False(execution.FormationOnly);
        Assert.False(execution.RequirePostArReady);
        Assert.Equal(321, execution.ParticipantReadyTimeoutSeconds);
        Assert.Equal(123, execution.AssemblyTimeoutSeconds);
        Assert.Equal(45, execution.LeaseDurationSeconds);
        Assert.Equal(new EndpointRepairPolicy(true, 42, "npc-no-teleport-no-inn"), execution.RepairPolicy);
        Assert.Equal(2, execution.Participants.Length);
        Assert.Equal(EndpointExecutionRole.QueueLeader, execution.Participants[0].Role);
        Assert.True(execution.Participants[0].IsInviter);
        Assert.Equal("need", execution.Participants[0].AdsLootMode);
        Assert.Equal(EndpointExecutionRole.Participant, execution.Participants[1].Role);
        Assert.False(execution.Participants[1].IsInviter);
        Assert.Equal("pass", execution.Participants[1].AdsLootMode);
        var module = Assert.Single(execution.Modules);
        Assert.Equal(0, module.ModuleIndex);
        Assert.Equal(nameof(DadModuleId.PremadeDuty), module.ModuleId);
        Assert.Equal("dad-premadeduty-777", module.ActivityId.Value);
        Assert.Equal(777u, module.ContentFinderConditionId);
        Assert.True(module.Unsynced);
        Assert.Equal(2, module.ExpectedPartySize);
    }

    [Fact]
    public void FormationOnlyProposalUsesStableDadFormationActivity()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = ActiveConfiguration();
        configuration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: true));
        var bridge = new DadAutoPartyParticipantBridge(configuration);
        var (plan, manifest, _) = Runtime(new RemoteSlot("Slot1", RemoteCharacter, IsLeader: true));
        plan.Orchestration.AutoPartyFormationOnly = true;

        Assert.True(bridge.TryBindRun(plan, manifest, now, out var blocker), blocker);

        var proposal = Assert.Single(bridge.LeasePendingCommands(8, TimeSpan.FromSeconds(10), now).Commands);
        Assert.Equal(DadAutoPartyFreeformRules.FormationActivityId, proposal.ActivityId);
    }

    [Fact]
    public void RejectedAdmissionCompletionRemovesUndeliveredProposalCommands()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = ActiveConfiguration();
        configuration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: true));
        var bridge = new DadAutoPartyParticipantBridge(configuration);
        var (plan, manifest, proposalId) = Runtime(new RemoteSlot("Slot1", RemoteCharacter, IsLeader: true));
        Assert.True(bridge.TryBindRun(plan, manifest, now, out var blocker), blocker);
        Assert.Equal(1, bridge.PendingCommandCount);

        bridge.CompleteProposal(proposalId, now);

        Assert.Equal(0, bridge.PendingCommandCount);
        Assert.Equal(
            DadAutoPartyParticipantStage.Restored,
            bridge.GetSnapshot(proposalId, "Slot1", now)!.Stage);
    }

    [Fact]
    public void DispatchedProposalBindingRemainsCommandReadyWithoutReplyOrLiveDirectoryRefresh()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = ActiveConfiguration();
        configuration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: true));
        var bridge = new DadAutoPartyParticipantBridge(configuration);
        var directoryAvailable = true;
        bridge.ConfigureDirectoryAuthorityGate((_, _) => directoryAvailable
            ? null
            : "Current directory refresh is unavailable.");
        var (plan, manifest, proposalId) = Runtime(new RemoteSlot("Slot1", RemoteCharacter, IsLeader: true));
        Assert.True(bridge.TryBindRun(plan, manifest, now, out var blocker), blocker);

        Assert.True(bridge.ObserveInviteTarget(
            Header(RemoteIsland, LocalIsland, now),
            proposalId,
            new OwnerId(RemoteOwner),
            new OpaqueCharacterId(RemoteCharacter),
            new DadWorkerSessionId("private-worker"),
            new DadAccountKey("private-account"),
            new DadCharacterKey("private-character"),
            1001,
            "Private Character",
            21,
            now.AddMinutes(2),
            now,
            out _));
        Assert.True(bridge.RequestOperation(
            proposalId,
            "Slot1",
            ExecutionOperationKind.Form,
            null,
            inviter: null,
            partyInviteTargets: [],
            now,
            out _));

        var batch = bridge.LeasePendingCommands(8, TimeSpan.FromSeconds(10), now);
        Assert.Equal(2, batch.Commands.Count);
        Assert.Equal(
            2,
            bridge.AcknowledgePendingCommands(
                batch.DispatchLeaseId,
                batch.Commands.Select(static command => command.CommandId).ToList(),
                now));
        Assert.Equal(
            DadAutoPartyParticipantStage.Formed,
            bridge.GetSnapshot(proposalId, "Slot1", now)!.Stage);

        directoryAvailable = false;
        configuration.RemoteBindings.Clear();
        var participant = bridge.ResolveParticipant(proposalId, manifest.Slots[0], now, out blocker);

        Assert.Empty(blocker);
        Assert.Equal(DadParticipantState.Discovered, participant.State);
        Assert.False(participant.IsAvailable);
        Assert.False(participant.IsEligibleForRun);
        Assert.False(participant.PostArReady);
        Assert.False(participant.WorldReadyStable);
        Assert.False(participant.Dependencies.IsReady);
        Assert.Equal(DadClaimState.None, participant.ClaimState);
        Assert.Equal(DadParticipantLeaseState.None, participant.LeaseState);
        Assert.Equal("dad-remote-form-dispatched", participant.StatusText);
    }

    [Fact]
    public void RevokedExpiredMismatchedDeauthenticatedAndUnregisteredRoutesRemainUnavailable()
    {
        var now = DateTimeOffset.UtcNow;

        var unregisteredConfiguration = ActiveConfiguration();
        unregisteredConfiguration.RegistrationState = DadAutoPartyRegistrationState.Unregistered;
        unregisteredConfiguration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: true));
        var unregistered = new DadAutoPartyParticipantBridge(unregisteredConfiguration);
        var (plan, manifest, _) = Runtime(new RemoteSlot("Slot1", RemoteCharacter, IsLeader: true));
        Assert.False(unregistered.TryBindRun(plan, manifest, now, out _));

        var deauthenticatedConfiguration = ActiveConfiguration();
        deauthenticatedConfiguration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: true));
        deauthenticatedConfiguration.Deauthentications.Add(new DadAutoPartyDeauthentication
        {
            PeerIslandId = RemoteIsland,
        });
        var deauthenticated = new DadAutoPartyParticipantBridge(deauthenticatedConfiguration);
        (plan, manifest, _) = Runtime(new RemoteSlot("Slot1", RemoteCharacter, IsLeader: true));
        Assert.False(deauthenticated.TryBindRun(plan, manifest, now, out _));

        var configuration = ActiveConfiguration();
        configuration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: true));
        var bridge = new DadAutoPartyParticipantBridge(configuration);
        var runtime = Runtime(new RemoteSlot("Slot1", RemoteCharacter, IsLeader: true));
        Assert.True(bridge.TryBindRun(runtime.Plan, runtime.Manifest, now, out var blocker), blocker);

        var mismatched = runtime.Manifest.Slots[0].Clone();
        mismatched.OpaqueCharacterId = "different-character";
        Assert.Equal(
            DadParticipantState.Stale,
            bridge.ResolveParticipant(runtime.ProposalId, mismatched, now, out _).State);

        Assert.Equal(
            DadParticipantState.Stale,
            bridge.ResolveParticipant(
                runtime.ProposalId,
                runtime.Manifest.Slots[0],
                now.AddMinutes(31),
                out _).State);

        var revokedConfiguration = ActiveConfiguration();
        revokedConfiguration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: true));
        var revoked = new DadAutoPartyParticipantBridge(revokedConfiguration);
        runtime = Runtime(new RemoteSlot("Slot1", RemoteCharacter, IsLeader: true));
        Assert.True(revoked.TryBindRun(runtime.Plan, runtime.Manifest, now, out blocker), blocker);
        revoked.DeauthenticateIsland(RemoteIsland, 1, "dad-test-revoked", now);
        Assert.Equal(
            DadParticipantState.Stale,
            revoked.ResolveParticipant(runtime.ProposalId, runtime.Manifest.Slots[0], now, out _).State);
    }

    [Fact]
    public void ReservationThroughRestoreRejectsReplayAndAcceptsLateValidatedReceipts()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = ActiveConfiguration();
        configuration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: true));
        var bridge = new DadAutoPartyParticipantBridge(configuration);
        var (plan, manifest, proposalId) = Runtime(new RemoteSlot("Slot1", RemoteCharacter, IsLeader: true));
        Assert.True(bridge.TryBindRun(plan, manifest, now, out var blocker), blocker);
        AcknowledgeAll(bridge, now);

        var reservationHeader = Header(RemoteIsland, LocalIsland, now);
        var reservation = new Reservation(
            reservationHeader,
            Guid.NewGuid(),
            proposalId,
            new OwnerId(RemoteOwner),
            new OpaqueCharacterId(RemoteCharacter),
            ExpectedStateGeneration: 1,
            ObservedStateGeneration: 1);
        Assert.True(bridge.ObserveReservation(reservation, now, out _));
        Assert.False(bridge.ObserveReservation(reservation, now, out var replayCode));
        Assert.Equal("dad-remote-contract-replay", replayCode);

        Assert.True(bridge.ObservePreflight(new PreflightResult(
            Header(RemoteIsland, LocalIsland, now),
            proposalId,
            new OwnerId(RemoteOwner),
            true,
            1,
            1,
            ImmutableArray<string>.Empty,
            ObservedStateGeneration: 1), now, out _));
        Assert.True(bridge.ObserveLease(new SessionLease(
            Header(RemoteIsland, LocalIsland, now),
            Guid.NewGuid(),
            proposalId,
            new OwnerId(RemoteOwner),
            now.AddMinutes(10),
            SessionPermission.All,
            ExpectedStateGeneration: 1,
            ObservedStateGeneration: 1), now, out _));

        var target = new DadNativePartyInviteTarget
        {
            RunId = plan.Request.RequestId,
            ModuleId = DadModuleId.PremadeDuty,
            SlotId = "Slot1",
            AccountKey = new DadAccountKey("private-account"),
            CharacterKey = new DadCharacterKey("private-character"),
            ContentId = 1001,
            CharacterName = "Private Character",
            WorldId = 21,
            WorkerSessionId = new DadWorkerSessionId("private-worker"),
        };
        Assert.True(bridge.ObserveInviteTarget(
            Header(RemoteIsland, LocalIsland, now),
            proposalId,
            new OwnerId(RemoteOwner),
            new OpaqueCharacterId(RemoteCharacter),
            target,
            now.AddMinutes(2),
            now,
            out _));

        Assert.True(bridge.RequestOperation(
            proposalId,
            "Slot1",
            ExecutionOperationKind.Form,
            null,
            inviter: null,
            partyInviteTargets: [],
            now,
            out _));
        var form = LeaseAndAcknowledgeSingle(bridge, now);
        Assert.True(bridge.ObserveOperationReceipt(Receipt(
            form,
            ExecutionOutcome.Accepted,
            2,
            now), now, out _));
        Assert.Equal(
            DadAutoPartyParticipantStage.Formed,
            bridge.GetSnapshot(proposalId, "Slot1", now)!.Stage);
        Assert.True(bridge.ObserveOperationReceipt(Receipt(
            form,
            ExecutionOutcome.Completed,
            3,
            now), now, out _));
        Assert.Equal(
            DadAutoPartyParticipantStage.Formed,
            bridge.GetSnapshot(proposalId, "Slot1", now)!.Stage);

        foreach (var (kind, moduleIndex, expected) in new[]
                 {
                     (ExecutionOperationKind.Queue, (int?)0, DadAutoPartyParticipantStage.Queued),
                     (ExecutionOperationKind.Settle, (int?)0, DadAutoPartyParticipantStage.Settled),
                     (ExecutionOperationKind.Cancel, (int?)null, DadAutoPartyParticipantStage.Cancelled),
                     (ExecutionOperationKind.Restore, (int?)null, DadAutoPartyParticipantStage.Restored),
                 })
        {
            Assert.True(bridge.RequestOperation(proposalId, "Slot1", kind, moduleIndex, null, now, out _));
            var command = LeaseAndAcknowledgeSingle(bridge, now);
            Assert.True(bridge.ObserveOperationReceipt(Receipt(
                command,
                ExecutionOutcome.Completed,
                command.ExpectedStateGeneration + 1,
                now), now, out _));
            Assert.Equal(expected, bridge.GetSnapshot(proposalId, "Slot1", now)!.Stage);
        }

        Assert.False(bridge.TryGetInviteTarget(
            proposalId,
            "Slot1",
            now.AddMinutes(3),
            out _,
            out _));
    }

    [Fact]
    public void BlockedPreflightRemainsPendingUntilBothGenerationsAdvanceThenAllowsLease()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = ActiveConfiguration();
        configuration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: true));
        var bridge = new DadAutoPartyParticipantBridge(configuration);
        var (plan, manifest, proposalId) = Runtime(new RemoteSlot("Slot1", RemoteCharacter, IsLeader: true));
        Assert.True(bridge.TryBindRun(plan, manifest, now, out var blocker), blocker);
        AcknowledgeAll(bridge, now);
        Assert.True(bridge.ObserveReservation(new Reservation(
            Header(RemoteIsland, LocalIsland, now),
            Guid.NewGuid(),
            proposalId,
            new OwnerId(RemoteOwner),
            new OpaqueCharacterId(RemoteCharacter),
            ExpectedStateGeneration: 1,
            ObservedStateGeneration: 2), now, out _));

        Assert.True(bridge.ObservePreflight(new PreflightResult(
            Header(RemoteIsland, LocalIsland, now),
            proposalId,
            new OwnerId(RemoteOwner),
            Ready: false,
            ReadinessGeneration: 3,
            ExpectedStateGeneration: 2,
            SafeBlockers: ["dad-inbound-execution-admission-not-wired"],
            ObservedStateGeneration: 2), now, out var safeCode));

        var snapshot = bridge.GetSnapshot(proposalId, "Slot1", now)!;
        Assert.Equal("dad-inbound-execution-admission-not-wired", safeCode);
        Assert.Equal(DadAutoPartyParticipantStage.PreflightPending, snapshot.Stage);
        Assert.Equal(2, snapshot.StateGeneration);
        Assert.False(snapshot.IsTerminal);
        Assert.False(snapshot.LeaseActive(now));

        Assert.False(bridge.ObservePreflight(new PreflightResult(
            Header(RemoteIsland, LocalIsland, now),
            proposalId,
            new OwnerId(RemoteOwner),
            Ready: true,
            ReadinessGeneration: 3,
            ExpectedStateGeneration: 2,
            SafeBlockers: ImmutableArray<string>.Empty,
            ObservedStateGeneration: 3), now, out var sameReadinessCode));
        Assert.Equal("dad-remote-preflight-generation-replay", sameReadinessCode);

        Assert.False(bridge.ObservePreflight(new PreflightResult(
            Header(RemoteIsland, LocalIsland, now),
            proposalId,
            new OwnerId(RemoteOwner),
            Ready: true,
            ReadinessGeneration: 2,
            ExpectedStateGeneration: 2,
            SafeBlockers: ImmutableArray<string>.Empty,
            ObservedStateGeneration: 3), now, out var lowerReadinessCode));
        Assert.Equal("dad-remote-preflight-generation-replay", lowerReadinessCode);

        Assert.False(bridge.ObservePreflight(new PreflightResult(
            Header(RemoteIsland, LocalIsland, now),
            proposalId,
            new OwnerId(RemoteOwner),
            Ready: true,
            ReadinessGeneration: 4,
            ExpectedStateGeneration: 2,
            SafeBlockers: ImmutableArray<string>.Empty,
            ObservedStateGeneration: 2), now, out var sameStateCode));
        Assert.Equal("dad-remote-preflight-generation-replay", sameStateCode);

        Assert.True(bridge.ObservePreflight(new PreflightResult(
            Header(RemoteIsland, LocalIsland, now),
            proposalId,
            new OwnerId(RemoteOwner),
            Ready: true,
            ReadinessGeneration: 4,
            ExpectedStateGeneration: 2,
            SafeBlockers: ImmutableArray<string>.Empty,
            ObservedStateGeneration: 3), now, out _));
        Assert.Equal(
            DadAutoPartyParticipantStage.LeasePending,
            bridge.GetSnapshot(proposalId, "Slot1", now)!.Stage);
        Assert.True(bridge.ObserveLease(new SessionLease(
            Header(RemoteIsland, LocalIsland, now),
            Guid.NewGuid(),
            proposalId,
            new OwnerId(RemoteOwner),
            now.AddMinutes(10),
            SessionPermission.All,
            ExpectedStateGeneration: 3,
            ObservedStateGeneration: 3), now, out _));
        Assert.True(bridge.GetSnapshot(proposalId, "Slot1", now)!.LeaseActive(now));
    }

    [Fact]
    public void OperationsRequireExactModuleReferenceAndEnforceNullRules()
    {
        var now = DateTimeOffset.UtcNow;
        var (bridge, proposalId, form) = FormedBridge(now, DadModuleId.PremadeDuty);
        Assert.Null(form.ExecutionModuleReference);
        Assert.Equal(
            new ulong[] { 1001 },
            bridge.GetSnapshot(proposalId, "Slot1", now)!.ObservedPartyContentIds);

        Assert.False(bridge.RequestOperation(
            proposalId, "Slot1", ExecutionOperationKind.Form, 0, null, now, out var formCode));
        Assert.Equal("dad-remote-operation-module-reference-forbidden", formCode);
        Assert.False(bridge.RequestOperation(
            proposalId, "Slot1", ExecutionOperationKind.Restore, 0, null, now, out var restoreCode));
        Assert.Equal("dad-remote-operation-module-reference-forbidden", restoreCode);
        Assert.False(bridge.RequestOperation(
            proposalId, "Slot1", ExecutionOperationKind.Cancel, 0, null, now, out var cancelCode));
        Assert.Equal("dad-remote-operation-module-reference-forbidden", cancelCode);
        Assert.False(bridge.RequestOperation(
            proposalId, "Slot1", ExecutionOperationKind.Queue, null, null, now, out var queueCode));
        Assert.Equal("dad-remote-operation-module-reference-required", queueCode);

        Assert.True(bridge.RequestOperation(
            proposalId, "Slot1", ExecutionOperationKind.Queue, 0, null, now, out _));
        var queue = LeaseAndAcknowledgeSingle(bridge, now);
        Assert.Equal(
            new EndpointExecutionModuleReference(0, nameof(DadModuleId.PremadeDuty)),
            queue.ExecutionModuleReference);
        Assert.False(bridge.ObserveOperationReceipt(
            Receipt(queue, ExecutionOutcome.Completed, 3, now) with
            {
                ObservedPartyContentIds = [1001],
            },
            now,
            out var unexpectedProofCode));
        Assert.Equal("dad-remote-operation-party-proof-invalid", unexpectedProofCode);
        Assert.False(bridge.ObserveOperationReceipt(
            Receipt(queue, ExecutionOutcome.Completed, 3, now) with
            {
                ModuleReference = new EndpointExecutionModuleReference(0, nameof(DadModuleId.Duty)),
            },
            now,
            out var mismatchCode));
        Assert.Equal("dad-remote-operation-receipt-mismatch", mismatchCode);
        Assert.True(bridge.ObserveOperationReceipt(
            Receipt(queue, ExecutionOutcome.Completed, 3, now), now, out _));

        Assert.False(bridge.RequestOperation(
            proposalId, "Slot1", ExecutionOperationKind.Settle, null, null, now, out var settleCode));
        Assert.Equal("dad-remote-operation-module-reference-required", settleCode);
        Assert.True(bridge.RequestOperation(
            proposalId, "Slot1", ExecutionOperationKind.Settle, 0, null, now, out _));
        var settle = LeaseAndAcknowledgeSingle(bridge, now);
        Assert.Equal(queue.ExecutionModuleReference, settle.ExecutionModuleReference);
        Assert.False(bridge.ObserveOperationReceipt(
            Receipt(settle, ExecutionOutcome.Completed, 4, now) with { ModuleReference = null },
            now,
            out mismatchCode));
        Assert.Equal("dad-remote-operation-receipt-mismatch", mismatchCode);
        Assert.True(bridge.ObserveOperationReceipt(
            Receipt(settle, ExecutionOutcome.Completed, 4, now), now, out _));

        Assert.True(bridge.RequestOperation(
            proposalId, "Slot1", ExecutionOperationKind.Restore, null, null, now, out _));
        var restore = LeaseAndAcknowledgeSingle(bridge, now);
        Assert.Null(restore.ExecutionModuleReference);
        Assert.False(bridge.ObserveOperationReceipt(
            Receipt(restore, ExecutionOutcome.Completed, 5, now) with
            {
                ObservedPartyContentIds = [0],
            },
            now,
            out var invalidProofCode));
        Assert.Equal("dad-remote-operation-party-proof-invalid", invalidProofCode);
        Assert.True(bridge.ObserveOperationReceipt(
            Receipt(restore, ExecutionOutcome.Completed, 5, now), now, out _));
        Assert.Equal(
            new ulong[] { 1001 },
            bridge.GetSnapshot(proposalId, "Slot1", now)!.ObservedPartyContentIds);
    }

    [Fact]
    public void QueueAndSettleAdvanceThroughFrozenModulesInOrder()
    {
        var now = DateTimeOffset.UtcNow;
        var (bridge, proposalId, _) = FormedBridge(now, DadModuleId.PremadeDuty, DadModuleId.Duty);

        Assert.False(bridge.RequestOperation(
            proposalId, "Slot1", ExecutionOperationKind.Queue, 1, null, now, out var earlyQueueCode));
        Assert.Equal("dad-remote-operation-module-order-invalid", earlyQueueCode);
        CompleteModuleOperation(bridge, proposalId, ExecutionOperationKind.Queue, 0, 3, now);
        Assert.False(bridge.RequestOperation(
            proposalId, "Slot1", ExecutionOperationKind.Settle, 1, null, now, out var earlySettleCode));
        Assert.Equal("dad-remote-operation-module-order-invalid", earlySettleCode);
        CompleteModuleOperation(bridge, proposalId, ExecutionOperationKind.Settle, 0, 4, now);
        Assert.Equal(
            DadAutoPartyParticipantStage.Formed,
            bridge.GetSnapshot(proposalId, "Slot1", now)!.Stage);

        Assert.False(bridge.RequestOperation(
            proposalId, "Slot1", ExecutionOperationKind.Queue, 0, null, now, out var replayModuleCode));
        Assert.Equal("dad-remote-operation-module-order-invalid", replayModuleCode);
        var secondQueue = CompleteModuleOperation(
            bridge, proposalId, ExecutionOperationKind.Queue, 1, 5, now);
        Assert.Equal(
            new EndpointExecutionModuleReference(1, nameof(DadModuleId.Duty)),
            secondQueue.ExecutionModuleReference);
        CompleteModuleOperation(bridge, proposalId, ExecutionOperationKind.Settle, 1, 6, now);
        Assert.Equal(
            DadAutoPartyParticipantStage.Settled,
            bridge.GetSnapshot(proposalId, "Slot1", now)!.Stage);
    }

    [Fact]
    public void SettledCommandRouteCanStartTheNextRepeatWithoutReadinessCeremony()
    {
        var now = DateTimeOffset.UtcNow;
        var (bridge, proposalId, _) = FormedBridge(now, DadModuleId.PremadeDuty);
        CompleteModuleOperation(bridge, proposalId, ExecutionOperationKind.Queue, 0, 3, now);
        CompleteModuleOperation(bridge, proposalId, ExecutionOperationKind.Settle, 0, 4, now);
        Assert.Equal(
            DadAutoPartyParticipantStage.Settled,
            bridge.GetSnapshot(proposalId, "Slot1", now)!.Stage);

        Assert.True(bridge.RequestOperation(
            proposalId,
            "Slot1",
            ExecutionOperationKind.Form,
            null,
            inviter: null,
            partyInviteTargets: [],
            now,
            out var formCode), formCode);
        LeaseAndAcknowledgeSingle(bridge, now);
        var repeated = bridge.GetSnapshot(proposalId, "Slot1", now)!;
        Assert.Equal(DadAutoPartyParticipantStage.Formed, repeated.Stage);
        Assert.Equal(0, repeated.NextModuleIndex);

        Assert.True(bridge.RequestOperation(
            proposalId,
            "Slot1",
            ExecutionOperationKind.Queue,
            0,
            inviter: null,
            now,
            out var queueCode), queueCode);
    }

    [Fact]
    public void StopAndDeauthenticationRetainRetryableLifecycleCommands()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = ActiveConfiguration();
        configuration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: true));
        var bridge = new DadAutoPartyParticipantBridge(configuration);
        var (plan, manifest, _) = Runtime(new RemoteSlot("Slot1", RemoteCharacter, IsLeader: true));
        Assert.True(bridge.TryBindRun(plan, manifest, now, out var blocker), blocker);
        AcknowledgeAll(bridge, now);

        bridge.StopAll("dad-owner-stop", now);
        var stopped = bridge.LeasePendingCommands(8, TimeSpan.FromSeconds(10), now);
        Assert.Contains(stopped.Commands, static command => command.OperationKind == ExecutionOperationKind.Cancel);
        Assert.Contains(stopped.Commands, static command => command.OperationKind == ExecutionOperationKind.Restore);
        var commandIds = stopped.Commands.Select(static command => command.CommandId).Order().ToList();
        Assert.Equal(stopped.Commands.Count, bridge.ReleasePendingCommands(stopped.DispatchLeaseId, now));
        var retry = bridge.LeasePendingCommands(8, TimeSpan.FromSeconds(10), now);
        Assert.Equal(commandIds, retry.Commands.Select(static command => command.CommandId).Order().ToList());
        bridge.ReleasePendingCommands(retry.DispatchLeaseId, now);

        bridge.DeauthenticateIsland(RemoteIsland, 7, "dad-owner-deauthenticated", now);
        var deauthenticated = bridge.LeasePendingCommands(16, TimeSpan.FromSeconds(10), now);
        Assert.Contains(deauthenticated.Commands, static command =>
            command.CommandKind == DadAutoPartyParticipantCommandKind.Revocation &&
            command.RevocationGeneration == 7);
        Assert.Contains(deauthenticated.Commands, static command => command.OperationKind == ExecutionOperationKind.Cancel);
        Assert.Contains(deauthenticated.Commands, static command => command.OperationKind == ExecutionOperationKind.Restore);
    }

    [Fact]
    public void ParsedInviteLocatorBuildsExactRuntimeOnlyTargetAndRejectsInvalidBounds()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = ActiveConfiguration();
        configuration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: true));
        var bridge = new DadAutoPartyParticipantBridge(configuration);
        var (plan, manifest, proposalId) = Runtime(new RemoteSlot("Slot1", RemoteCharacter, IsLeader: true));
        Assert.True(bridge.TryBindRun(plan, manifest, now, out var blocker), blocker);
        AcknowledgeAll(bridge, now);

        Assert.False(bridge.ObserveInviteTarget(
            Header(RemoteIsland, LocalIsland, now),
            proposalId,
            new OwnerId(RemoteOwner),
            new OpaqueCharacterId(RemoteCharacter),
            new DadWorkerSessionId(new string('w', AutoPartyProtocol.MaximumIdentifierLength + 1)),
            new DadAccountKey("private-account"),
            new DadCharacterKey("private-character"),
            1001,
            "Private Character",
            21,
            now.AddMinutes(2),
            now,
            out var invalidCode));
        Assert.Equal("dad-remote-invite-target-invalid", invalidCode);

        Assert.True(bridge.ObserveInviteTarget(
            Header(RemoteIsland, LocalIsland, now),
            proposalId,
            new OwnerId(RemoteOwner),
            new OpaqueCharacterId(RemoteCharacter),
            new DadWorkerSessionId("private-worker"),
            new DadAccountKey("private-account"),
            new DadCharacterKey("private-character"),
            1001,
            "Private Character",
            21,
            now.AddMinutes(2),
            now,
            out var readyCode));
        Assert.Equal("dad-remote-invite-target-ready", readyCode);

        Assert.True(bridge.TryGetInviteTarget(proposalId, "Slot1", now, out var target, out blocker), blocker);
        Assert.Equal(plan.Request.RequestId, target.RunId);
        Assert.Equal(plan.CompositeModuleId, target.ModuleId);
        Assert.Equal("Slot1", target.SlotId);
        Assert.Equal(new DadWorkerSessionId("private-worker"), target.WorkerSessionId);
        Assert.Equal(new DadAccountKey("private-account"), target.AccountKey);
        Assert.Equal(new DadCharacterKey("private-character"), target.CharacterKey);
        Assert.Equal(1001UL, target.ContentId);
        Assert.Equal("Private Character", target.CharacterName);
        Assert.Equal((ushort)21, target.WorldId);
    }

    private static void AcknowledgeAll(DadAutoPartyParticipantBridge bridge, DateTimeOffset now)
    {
        var batch = bridge.LeasePendingCommands(32, TimeSpan.FromSeconds(10), now);
        if (batch.Commands.Count == 0)
            return;
        Assert.Equal(
            batch.Commands.Count,
            bridge.AcknowledgePendingCommands(
                batch.DispatchLeaseId,
                batch.Commands.Select(static command => command.CommandId).ToList(),
                now));
    }

    private static DadAutoPartyParticipantCommand CompleteModuleOperation(
        DadAutoPartyParticipantBridge bridge,
        Guid proposalId,
        ExecutionOperationKind kind,
        int moduleIndex,
        long generation,
        DateTimeOffset now)
    {
        Assert.True(bridge.RequestOperation(proposalId, "Slot1", kind, moduleIndex, null, now, out _));
        var command = LeaseAndAcknowledgeSingle(bridge, now);
        Assert.True(bridge.ObserveOperationReceipt(
            Receipt(command, ExecutionOutcome.Completed, generation, now), now, out _));
        return command;
    }

    private static (DadAutoPartyParticipantBridge Bridge, Guid ProposalId, DadAutoPartyParticipantCommand Form)
        FormedBridge(DateTimeOffset now, params DadModuleId[] moduleIds)
    {
        var configuration = ActiveConfiguration();
        configuration.RemoteBindings.Add(Binding(RemoteCharacter, ownsQueueAuthority: true));
        var bridge = new DadAutoPartyParticipantBridge(configuration);
        var (plan, manifest, proposalId) = Runtime(
            new RemoteSlot("Slot1", RemoteCharacter, IsLeader: true));
        plan.CompositeModuleId = moduleIds[0];
        plan.Modules = moduleIds.Select(moduleId => new DadPlannedModuleExecution
        {
            ModuleId = moduleId,
            DisplayName = moduleId.ToString(),
            ExpectedPartySize = 1,
            RequiresPeers = true,
        }).ToList();
        manifest.Modules = moduleIds.Select(moduleId => new DadFrozenModulePayload
        {
            ModuleId = moduleId,
            DutyName = moduleId.ToString(),
            ExpectedPartySize = 1,
        }).ToList();
        Assert.True(bridge.TryBindRun(plan, manifest, now, out var blocker), blocker);
        AcknowledgeAll(bridge, now);
        Assert.True(bridge.ObserveReservation(new Reservation(
            Header(RemoteIsland, LocalIsland, now),
            Guid.NewGuid(),
            proposalId,
            new OwnerId(RemoteOwner),
            new OpaqueCharacterId(RemoteCharacter),
            ExpectedStateGeneration: 1,
            ObservedStateGeneration: 1), now, out _));
        Assert.True(bridge.ObservePreflight(new PreflightResult(
            Header(RemoteIsland, LocalIsland, now),
            proposalId,
            new OwnerId(RemoteOwner),
            Ready: true,
            ReadinessGeneration: 1,
            ExpectedStateGeneration: 1,
            SafeBlockers: ImmutableArray<string>.Empty,
            ObservedStateGeneration: 1), now, out _));
        Assert.True(bridge.ObserveLease(new SessionLease(
            Header(RemoteIsland, LocalIsland, now),
            Guid.NewGuid(),
            proposalId,
            new OwnerId(RemoteOwner),
            now.AddMinutes(10),
            SessionPermission.All,
            ExpectedStateGeneration: 1,
            ObservedStateGeneration: 1), now, out _));
        Assert.True(bridge.ObserveInviteTarget(
            Header(RemoteIsland, LocalIsland, now),
            proposalId,
            new OwnerId(RemoteOwner),
            new OpaqueCharacterId(RemoteCharacter),
            new DadWorkerSessionId("private-worker"),
            new DadAccountKey("private-account"),
            new DadCharacterKey("private-character"),
            1001,
            "Private Character",
            21,
            now.AddMinutes(2),
            now,
            out _));
        Assert.True(bridge.RequestOperation(
            proposalId,
            "Slot1",
            ExecutionOperationKind.Form,
            null,
            inviter: null,
            partyInviteTargets: [],
            now,
            out _));
        var form = LeaseAndAcknowledgeSingle(bridge, now);
        Assert.True(bridge.ObserveOperationReceipt(
            Receipt(form, ExecutionOutcome.Completed, 2, now), now, out _));
        return (bridge, proposalId, form);
    }

    private static DadAutoPartyParticipantCommand LeaseAndAcknowledgeSingle(
        DadAutoPartyParticipantBridge bridge,
        DateTimeOffset now)
    {
        var batch = bridge.LeasePendingCommands(8, TimeSpan.FromSeconds(10), now);
        var command = Assert.Single(batch.Commands);
        Assert.Equal(1, bridge.AcknowledgePendingCommands(batch.DispatchLeaseId, [command.CommandId], now));
        return command;
    }

    private static ExecutionOperationReceipt Receipt(
        DadAutoPartyParticipantCommand command,
        ExecutionOutcome outcome,
        long generation,
        DateTimeOffset now)
        => new(
            Header(RemoteIsland, LocalIsland, now),
            command.CommandId,
            command.ProposalId,
            new OwnerId(command.OwnerId),
            command.OperationKind!.Value,
            outcome,
            generation,
            "dad-test-operation",
            ObservedPartyContentIds: outcome == ExecutionOutcome.Completed &&
                command.OperationKind == ExecutionOperationKind.Form
                    ? [1001]
                    : ImmutableArray<ulong>.Empty,
            ModuleReference: command.ExecutionModuleReference);

    private static (DadRunPlan Plan, DadRunSlotManifest Manifest, Guid ProposalId) Runtime(
        params RemoteSlot[] remoteSlots)
    {
        var proposalId = Guid.NewGuid();
        var orchestration = new DadOrchestrationIntent
        {
            AutoPartyProposalId = proposalId.ToString("D"),
            AutoPartyFormationOnly = false,
            QueueAuthority = DadQueueAuthority.Leader,
            InviteAuthority = DadInviteAuthority.PresetLeader,
            RosterIntent = new DadRosterIntent
            {
                ExpectedPartySize = remoteSlots.Length,
                RequireRemoteParticipants = true,
            },
        };
        var request = new DadRunRequest
        {
            RequestId = $"run-{Guid.NewGuid():N}",
            Orchestration = orchestration,
        };
        var plan = new DadRunPlan
        {
            Request = request,
            Orchestration = orchestration,
            RequiredParticipantCount = remoteSlots.Length,
            RequiresRemoteParticipants = true,
            LeaderCharacterKey = DadRunSlotManifestRules.RegisteredIslandSlotOneAuthority,
            InviterCharacterKey = DadRunSlotManifestRules.RegisteredIslandSlotOneAuthority,
            CompositeModuleId = DadModuleId.PremadeDuty,
            Modules =
            [
                new DadPlannedModuleExecution
                {
                    ModuleId = DadModuleId.PremadeDuty,
                    DisplayName = "Synthetic duty",
                    ExpectedPartySize = remoteSlots.Length,
                    RequiresPeers = true,
                },
            ],
        };
        var manifest = new DadRunSlotManifest
        {
            RequestId = request.RequestId,
            ExpectedPartySize = remoteSlots.Length,
            LeaderCharacterKey = plan.LeaderCharacterKey,
            InviterCharacterKey = plan.InviterCharacterKey,
            Modules =
            [
                new DadFrozenModulePayload
                {
                    ModuleId = DadModuleId.PremadeDuty,
                    DutyName = "Synthetic duty",
                    ExpectedPartySize = remoteSlots.Length,
                },
            ],
            Slots = remoteSlots.Select(slot => new DadFrozenRunSlot
            {
                SlotId = slot.SlotId,
                RouteKind = DadRunSlotRouteKind.RegisteredIsland,
                OwnerId = RemoteOwner,
                IslandId = RemoteIsland,
                OpaqueCharacterId = slot.CharacterId,
                RequiredJobId = RequestedJob,
                IsLeader = slot.IsLeader,
                IsInviter = slot.IsLeader,
            }).ToList(),
        };
        return (plan, manifest, proposalId);
    }

    private static DadAutoPartyRemoteBinding Binding(string characterId, bool ownsQueueAuthority)
        => new()
        {
            FleetRowId = $"row-{characterId}",
            OpaqueCharacterId = characterId,
            OwnerId = RemoteOwner,
            IslandId = RemoteIsland,
            RequestedJobId = RequestedJob.ToString(),
            OwnsQueueAuthority = ownsQueueAuthority,
            OwnerConsentConfirmed = true,
        };

    private static DadAutoPartyConfiguration ActiveConfiguration()
        => new()
        {
            Enabled = true,
            RegistrationState = DadAutoPartyRegistrationState.Active,
            RegistrationId = Guid.NewGuid().ToString("D"),
            RouteId = "route-local",
            CentralBotApplicationId = "123456789",
            HomeGuildScope = "guild-home",
            WebhookCredentialReference = "webhook-mailbox-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            UplinkEpochId = Guid.NewGuid().ToString("D"),
            DownlinkEpochId = Guid.NewGuid().ToString("D"),
            MailboxEpochGeneration = 1,
            RelayKeyGeneration = 1,
            RelaySigningPublicKey = Convert.ToBase64String(new byte[32]),
            RelayAgreementPublicKey = Convert.ToBase64String(new byte[32]),
            EndpointIdentityReference = "identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            RegisteredOwnerId = "owner-local",
            RegisteredIslandId = LocalIsland,
            RegistrationFingerprint = new string('A', 64),
            EndpointAlias = "local",
            SigningPublicKey = Convert.ToBase64String(new byte[32]),
            EncryptionPublicKey = Convert.ToBase64String(new byte[32]),
        };

    private static ContractHeader Header(
        string senderIsland,
        string recipientIsland,
        DateTimeOffset now)
    {
        var nonce = RandomNumberGenerator.GetBytes(AutoPartyProtocol.ContractNonceBytes);
        try
        {
            return new ContractHeader(
                AutoPartyProtocol.CurrentVersion,
                Guid.NewGuid(),
                $"test-{Guid.NewGuid():N}",
                new IslandId(senderIsland),
                new IslandId(recipientIsland),
                now,
                now.AddMinutes(5),
                1,
                1,
                1,
                1,
                ContractHeader.CreateNonce(nonce),
                ImmutableArray<int>.Empty);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private sealed record RemoteSlot(string SlotId, string CharacterId, bool IsLeader);
}
