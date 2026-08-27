using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadTestingCreateGroupRegressionTests
{
    [Fact]
    public void ExactLocalIdentityResolvesAndRegisteredEndpointRemainsLanTravelProof()
    {
        var fixture = new TestingCreateGroupFixture();
        var projected = fixture.Project(fixture.LiveLocal);
        var resolved = DadRunSlotManifestRules.ResolveSlot(
            fixture.LocalSlot,
            projected,
            requirePostArReady: true,
            out var blocker);

        Assert.Empty(blocker);
        Assert.Equal(DadParticipantState.Discovered, resolved.State);
        Assert.Equal(TestingCreateGroupFixture.LocalWorker, resolved.WorkerSessionId.Value);
        Assert.Equal(TestingCreateGroupFixture.LocalAccount, resolved.ManagedAccountKey.Value);
        Assert.Equal(TestingCreateGroupFixture.LocalCharacter, resolved.ActiveCharacterKey.Value);
        Assert.Equal(TestingCreateGroupFixture.LocalContentId, resolved.Character.ContentId);
        Assert.Equal(TestingCreateGroupFixture.LocalIsland, resolved.RegisteredIslandId);
        Assert.Equal(TestingCreateGroupFixture.LocalIsland, fixture.Configuration.AutoParty.RegisteredIslandId);

        Assert.True(DadCoordinatorTravelRules.TryFreezeTarget(
            fixture.Plan.Request.RequestId,
            resolved,
            TestingCreateGroupFixture.Now.UtcDateTime,
            out var travelTarget,
            out var travelBlocker), travelBlocker);
        var lanProofRows = DadCoordinatorTravelRules.SelectLanParticipants(
            fixture.Manifest,
            [resolved, fixture.RemoteParticipant]);
        Assert.Same(resolved, Assert.Single(lanProofRows));
        Assert.True(DadCoordinatorTravelRules.ValidateParticipants(
            travelTarget,
            lanProofRows,
            TestingCreateGroupFixture.Now.UtcDateTime).Ready);
    }

    [Fact]
    public void DifferentLiveCharacterCannotFreezeTravelOrBuildFormationInstructions()
    {
        var fixture = new TestingCreateGroupFixture();
        var wrongLive = fixture.LiveLocal.Clone();
        wrongLive.ActiveCharacterKey = new DadCharacterKey("Different Character@Alpha");
        wrongLive.Character.CharacterKey = "Different Character@Alpha";
        wrongLive.Character.ContentId = 9999;
        var resolved = DadRunSlotManifestRules.ResolveSlot(
            fixture.LocalSlot,
            fixture.Project(wrongLive),
            requirePostArReady: true,
            out var blocker);

        Assert.Equal(DadParticipantState.WaitingForRequiredCharacter, resolved.State);
        Assert.Contains(TestingCreateGroupFixture.LocalCharacter, blocker, StringComparison.Ordinal);
        var canFreezeTravel = resolved.State == DadParticipantState.Discovered &&
                              DadCoordinatorTravelRules.TryFreezeTarget(
                                  fixture.Plan.Request.RequestId,
                                  resolved,
                                  TestingCreateGroupFixture.Now.UtcDateTime,
                                  out _,
                                  out _);
        Assert.False(canFreezeTravel);

        var instructions = new DadPartyAssemblyService().BuildInstructions(
            fixture.Plan,
            [resolved, fixture.RemoteParticipant],
            fixture.Manifest,
            fixture.LiveLocal.WorkerSessionId,
            fixture.RuntimeInviteTargets,
            out var assemblyBlocker);
        Assert.Empty(instructions);
        Assert.Contains("exact frozen", assemblyBlocker, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TestingCreateGroupFixture.LocalIsland, resolved.RegisteredIslandId);
        Assert.Equal(TestingCreateGroupFixture.LocalIsland, fixture.Configuration.AutoParty.RegisteredIslandId);
    }

    [Fact]
    public void FormationRoutesLocalSlotOneAndAuthenticatedDadIslandSlotTwo()
    {
        var fixture = new TestingCreateGroupFixture();
        var resolved = DadRunSlotManifestRules.ResolveSlot(
            fixture.LocalSlot,
            fixture.Project(fixture.LiveLocal),
            requirePostArReady: true,
            out var resolutionBlocker);
        Assert.Empty(resolutionBlocker);

        var service = new DadPartyAssemblyService();
        var instructions = service.BuildInstructions(
            fixture.Plan,
            [resolved, fixture.RemoteParticipant],
            fixture.Manifest,
            fixture.LiveLocal.WorkerSessionId,
            fixture.RuntimeInviteTargets,
            out var blocker);

        Assert.Empty(blocker);
        Assert.Collection(
            instructions,
            form =>
            {
                Assert.Equal("Slot1", form.SlotId);
                Assert.Equal(DadAssemblyInstructionKind.FormParty, form.InstructionKind);
                Assert.Equal(
                    DadRunSlotRouteKind.LanWorker,
                    DadRunSlotManifestRules.FindInstructionSlot(fixture.Manifest, form)!.RouteKind);
            },
            join =>
            {
                Assert.Equal("Slot2", join.SlotId);
                Assert.Equal(DadAssemblyInstructionKind.JoinParty, join.InstructionKind);
                Assert.Equal(
                    DadRunSlotRouteKind.RegisteredIsland,
                    DadRunSlotManifestRules.FindInstructionSlot(fixture.Manifest, join)!.RouteKind);
            });

        var notPostArReady = resolved.Clone();
        notPostArReady.PostArReady = false;
        Assert.Empty(service.BuildInstructions(
            fixture.Plan,
            [notPostArReady, fixture.RemoteParticipant],
            fixture.Manifest,
            fixture.LiveLocal.WorkerSessionId,
            fixture.RuntimeInviteTargets,
            out var readinessBlocker));
        Assert.Contains("post-AR ready", readinessBlocker, StringComparison.OrdinalIgnoreCase);

        var bridge = fixture.CreateBridge();
        Assert.True(bridge.TryBindRun(
            fixture.Plan,
            fixture.Manifest,
            TestingCreateGroupFixture.Now,
            out var bridgeBlocker), bridgeBlocker);
        var proposal = Assert.Single(bridge.LeasePendingCommands(
            8,
            TimeSpan.FromSeconds(10),
            TestingCreateGroupFixture.Now).Commands);
        Assert.Equal(TestingCreateGroupFixture.RemoteIsland, proposal.IslandId);
        Assert.Contains(proposal.Participants!, static participant => participant.SlotId == "Slot2");
        Assert.Equal(TestingCreateGroupFixture.LocalIsland, fixture.Configuration.AutoParty.RegisteredIslandId);
    }

    [Fact]
    public void FormationInviteCadenceStartsImmediatelyAndStopsAtFiveMinutes()
    {
        var fixture = new TestingCreateGroupFixture();
        var tracker = new DadNativePartyInviteAttemptTracker();
        var dispatcher = new FakeDispatcher();
        var target = fixture.RuntimeInviteTargets["Slot2"];

        for (var attempt = 0; attempt <= 9; attempt++)
        {
            var dispatched = tracker.TryDispatchAuthenticatedIsland(
                target,
                partyListContainsContentId: false,
                TestingCreateGroupFixture.Now.UtcDateTime.AddSeconds(attempt * 30),
                dispatcher,
                out var blocker);
            Assert.Empty(blocker);
            Assert.Equal(2, dispatched.Count);
            Assert.All(dispatched, item => Assert.Equal(attempt + 1, item.AttemptNumber));
            Assert.All(dispatched, item => Assert.Equal(
                TestingCreateGroupFixture.Now.UtcDateTime.AddSeconds((attempt + 1) * 30),
                item.NextAttemptAtUtc));
        }

        Assert.Empty(tracker.TryDispatchAuthenticatedIsland(
            target,
            partyListContainsContentId: false,
            TestingCreateGroupFixture.Now.UtcDateTime.AddMinutes(5),
            dispatcher,
            out var terminalBlocker));
        Assert.Contains("five minutes", terminalBlocker, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(20, dispatcher.CallCount);
    }

    private sealed class TestingCreateGroupFixture
    {
        public static readonly DateTimeOffset Now = new(2026, 8, 26, 20, 0, 0, TimeSpan.Zero);
        public const string LocalWorker = "worker-w";
        public const string LocalAccount = "account-w";
        public const string LocalCharacter = "Worker W@Alpha";
        public const ulong LocalContentId = 1001;
        public const string LocalIsland = "island-local-registration";
        public const string RemoteOwner = "owner-dad-island";
        public const string RemoteIsland = "island-dad-remote";
        public const string RemoteOpaqueCharacter = "opaque-dad-character";

        public TestingCreateGroupFixture()
        {
            Configuration = new Configuration
            {
                ClientAccountId = LocalAccount,
                AutoParty = ActiveAutoPartyConfiguration(),
            };
            RemoteBinding = new DadAutoPartyRemoteBinding
            {
                FleetRowId = "testing-create-group-slot2",
                OpaqueCharacterId = RemoteOpaqueCharacter,
                OwnerId = RemoteOwner,
                IslandId = RemoteIsland,
                RequestedJobId = "19",
                OwnsQueueAuthority = false,
                OwnerConsentConfirmed = true,
            };
            Configuration.AutoParty.RemoteBindings.Add(RemoteBinding);

            LiveCharacter = Character(
                LocalAccount,
                LocalCharacter,
                LocalContentId,
                DadCharacterSource.LocalRuntime);
            LiveLocal = new DadParticipantSnapshot
            {
                WorkerSessionId = new DadWorkerSessionId(LocalWorker),
                ManagedAccountKey = new DadAccountKey(LocalAccount),
                ActiveCharacterKey = new DadCharacterKey(LocalCharacter),
                Character = LiveCharacter.Clone(),
                CurrentLocation = new DadWorldLocationObservation
                {
                    WorldId = 21,
                    WorldName = "Alpha",
                    DataCenterId = 20,
                    DataCenterName = "Testing DC",
                    RegionId = 2,
                    RegionName = "North America",
                    ObservedAtUtc = Now.UtcDateTime,
                },
                RegisteredIslandId = LocalIsland,
                IsLocalClient = true,
                IsAvailable = true,
                IsEligibleForRun = true,
                PostArReady = true,
                WorldReadyStable = true,
                State = DadParticipantState.Ready,
            };

            var request = Request();
            var classification = DadCrewToolsRules.Classify(
                DadPlannerActivityMode.DutyPremade,
                allianceACount: 0,
                allianceBCount: 0,
                allianceCCount: 0,
                expectedPartySize: 2);
            Assert.True(classification.CanCreate);
            Assert.Equal(DadCrewFormationMode.RegularParty, classification.Mode);
            Assert.True(DadCrewFormationPlannerRules.TryBuildPlan(
                request,
                new DadCharacterPool { Characters = [LiveCharacter.Clone()] },
                Configuration,
                [RemoteBinding],
                LiveCharacter,
                requireLiveReadiness: true,
                allowWakeableCoordinatorLeader: false,
                out var plan,
                out var planBlocker), planBlocker);
            Plan = plan;
            Assert.True(Plan.Orchestration.AutoPartyFormationOnly);
            Assert.Equal(DadModuleId.None, Plan.CompositeModuleId);

            Assert.True(DadRunSlotManifestRules.TryCreate(
                Plan,
                [RemoteBinding],
                out var unboundManifest,
                out var manifestBlocker), manifestBlocker);
            Assert.True(DadRunSlotManifestRules.TryBindWorkerSessions(
                unboundManifest,
                [LiveLocal],
                out var manifest,
                out var bindBlocker), bindBlocker);
            Manifest = manifest;
            LocalSlot = Manifest.Slots.Single(static slot => slot.SlotId == "Slot1");

            RemoteParticipant = new DadParticipantSnapshot
            {
                WorkerSessionId = new DadWorkerSessionId("autoparty-testing-slot2"),
                RegisteredIslandId = RemoteIsland,
                RunId = Plan.Request.RequestId,
                ActiveCharacterKey = new DadCharacterKey("remote-Slot2"),
                Character = new DadAcquiredCharacter { CharacterKey = "remote-Slot2" },
                AssignedSlotId = "Slot2",
                State = DadParticipantState.Discovered,
            };
            RuntimeInviteTargets = new Dictionary<string, DadNativePartyInviteTarget>(StringComparer.OrdinalIgnoreCase)
            {
                ["Slot2"] = new DadNativePartyInviteTarget
                {
                    RunId = Plan.Request.RequestId,
                    ModuleId = DadModuleId.None,
                    SlotId = "Slot2",
                    AccountKey = new DadAccountKey("dad-endpoint-account"),
                    CharacterKey = new DadCharacterKey("Dad Island Member@Beta"),
                    ContentId = 2002,
                    CharacterName = "Dad Island Member",
                    WorldId = 22,
                    WorkerSessionId = new DadWorkerSessionId("dad-endpoint-worker"),
                },
            };
        }

        public Configuration Configuration { get; }
        public DadAutoPartyRemoteBinding RemoteBinding { get; }
        public DadAcquiredCharacter LiveCharacter { get; }
        public DadParticipantSnapshot LiveLocal { get; }
        public DadRunPlan Plan { get; }
        public DadRunSlotManifest Manifest { get; }
        public DadFrozenRunSlot LocalSlot { get; }
        public DadParticipantSnapshot RemoteParticipant { get; }
        public IReadOnlyDictionary<string, DadNativePartyInviteTarget> RuntimeInviteTargets { get; }

        public IReadOnlyList<DadParticipantSnapshot> Project(DadParticipantSnapshot live)
            => DadCoordinatorRuntimeProjectionRules.BuildFrozenParticipantSet(
                live,
                [live.Clone()],
                new HashSet<string>([LocalWorker], StringComparer.OrdinalIgnoreCase),
                static _ => true);

        public DadAutoPartyParticipantBridge CreateBridge()
        {
            var localCrew = new List<DadAutoPartyCrewCandidate>
            {
                new(
                    new DadAutoPartyCrewIdentity
                    {
                        RosterIdentityKey = "testing-create-group-local",
                        OpaqueCharacterId = "opaque-local-character",
                    },
                    LiveCharacter.Clone(),
                    [19],
                    Available: true),
            };
            return new DadAutoPartyParticipantBridge(
                Configuration.AutoParty,
                currentLocalCrewProvider: () => localCrew);
        }

        private static DadRunRequest Request()
        {
            var roster = new List<DadRosterCharacterRef>
            {
                new()
                {
                    AccountKey = new DadAccountKey(LocalAccount),
                    CharacterKey = new DadCharacterKey(LocalCharacter),
                    ContentId = LocalContentId,
                    RequiredJobId = 19,
                },
                new()
                {
                    SharedIdentityToken = RemoteOpaqueCharacter,
                    RequiredJobId = 19,
                },
            };
            return new DadRunRequest
            {
                RequestId = "testing-create-group",
                RequestedBy = "crew-tools-create-group",
                Orchestration = new DadOrchestrationIntent
                {
                    AutoPartyProposalId = Guid.NewGuid().ToString("D"),
                    AutoPartyFormationOnly = true,
                    AuthorityMode = DadAuthorityMode.ServerDad,
                    TransportMode = DadTransportMode.ServerHub,
                    QueueAuthority = DadQueueAuthority.Leader,
                    InviteAuthority = DadInviteAuthority.PresetLeader,
                    PreferredLeaderCharacterKey = new DadCharacterKey(LocalCharacter),
                    PreferredInviterCharacterKey = new DadCharacterKey(LocalCharacter),
                    RequirePostArReady = true,
                    WaitPolicy = new DadRunWaitPolicy
                    {
                        ParticipantReadyTimeoutSeconds = 300,
                        AssemblyTimeoutSeconds = 300,
                        LeaseDurationSeconds = 60,
                    },
                    RequiredRosterCharacters = roster,
                    RequiredAccountKeys = [new DadAccountKey(LocalAccount)],
                    RequiredCharacterKeys = [new DadCharacterKey(LocalCharacter)],
                    RosterIntent = new DadRosterIntent
                    {
                        ExpectedPartySize = 2,
                        RequireRemoteParticipants = true,
                        RequireExactCharacters = true,
                        AllowStoredXadbFallback = false,
                    },
                },
            };
        }

        private static DadAcquiredCharacter Character(
            string account,
            string character,
            ulong contentId,
            DadCharacterSource source)
            => new()
            {
                AccountId = account,
                CharacterKey = character,
                CharacterName = "Worker W",
                WorldId = 21,
                ContentId = contentId,
                CurrentJobId = 19,
                Source = source,
                Freshness = DadSnapshotFreshness.Live,
                Readiness = DadReadinessState.Ready,
                JobLevels = new Dictionary<uint, int> { [19] = 100 },
            };

        private static DadAutoPartyConfiguration ActiveAutoPartyConfiguration()
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
    }

    private sealed class FakeDispatcher : IDadNativePartyInviteDispatcher
    {
        public int CallCount { get; private set; }

        public bool InviteSameWorld(ulong contentId, string exactCharacterName, ushort worldId)
        {
            CallCount++;
            return true;
        }

        public bool InviteCrossWorld(ulong contentId, ushort worldId)
        {
            CallCount++;
            return true;
        }

        public bool InviteInInstance(ulong contentId)
        {
            CallCount++;
            return true;
        }
    }
}
