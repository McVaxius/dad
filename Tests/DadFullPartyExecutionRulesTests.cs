using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadFullPartyExecutionRulesTests
{
    [Fact]
    public void ExactVerifiedFourDadPartyWithLocalSlotOneLeaderIsAllowed()
    {
        var (plan, participants) = ValidParty();

        var blockers = DadFullPartyExecutionRules.Evaluate(
            plan,
            DadModuleId.DailyMsq,
            participants,
            4,
            "Daily Roulette");

        Assert.Empty(blockers);
    }

    [Fact]
    public void ExactPartySizeAndVerificationAreRequired()
    {
        var (plan, participants) = ValidParty();
        participants.RemoveAt(3);

        var shortParty = Evaluate(plan, participants);
        Assert.Contains(shortParty, blocker => blocker.Capability == "Participants" && blocker.Severity == DadModuleBlockerSeverity.Failed);

        (plan, participants) = ValidParty();
        participants[2].PostArReady = false;
        var unverified = Evaluate(plan, participants);
        Assert.Contains(unverified, blocker => blocker.Capability == "Participants" && blocker.Summary.Contains("not verified", StringComparison.OrdinalIgnoreCase));

        (plan, participants) = ValidParty();
        participants[3].ActiveCharacterKey = participants[2].ActiveCharacterKey;
        var duplicate = Evaluate(plan, participants);
        Assert.Contains(duplicate, blocker => blocker.Capability == "Participants" && blocker.Summary.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExactSlotOneIsQueueAuthorityWhetherLocalOrRemote()
    {
        var (plan, participants) = ValidParty();
        participants[0].AssignedSlotId = "Slot2";
        Assert.Contains(Evaluate(plan, participants), blocker => blocker.Capability == "LeaderAuthority" && blocker.Summary.Contains("Slot1", StringComparison.Ordinal));

        (plan, participants) = ValidParty();
        participants[0].IsAuthority = false;
        participants[0].IsLocalClient = false;
        participants[0].Character.Source = DadCharacterSource.PeerRuntime;
        participants[1].IsLocalClient = true;
        participants[1].IsAuthority = true;
        Assert.Empty(Evaluate(plan, participants));

        (plan, participants) = ValidParty();
        participants[0].ActiveCharacterKey = new DadCharacterKey("Drifted@Alpha");
        Assert.Contains(Evaluate(plan, participants), blocker => blocker.Capability == "LeaderAuthority" && blocker.Summary.Contains("mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ServerDadAndLeaderOrLanPartyQueueAuthorityAreRequired()
    {
        var (plan, participants) = ValidParty();
        plan.Orchestration.AuthorityMode = DadAuthorityMode.LocalOnly;
        Assert.Contains(Evaluate(plan, participants), blocker => blocker.Capability == "LeaderAuthority");

        (plan, participants) = ValidParty();
        plan.Orchestration.QueueAuthority = DadQueueAuthority.LocalOnly;
        Assert.Contains(Evaluate(plan, participants), blocker => blocker.Capability == "QueueAuthority");

        (plan, participants) = ValidParty();
        plan.Orchestration.QueueAuthority = DadQueueAuthority.LanParty;
        Assert.Empty(Evaluate(plan, participants));
    }

    [Fact]
    public void DailyRoulettePlannedLeaderMustBeExactFrozenSlotOne()
    {
        var request = new DadRunRequest
        {
            DailyMsq = new DadDailyMsqTask(),
            Orchestration = new DadOrchestrationIntent
            {
                AuthorityMode = DadAuthorityMode.ServerDad,
                QueueAuthority = DadQueueAuthority.Leader,
                RosterIntent = new DadRosterIntent { ExpectedPartySize = 4 },
                RequiredRosterCharacters =
                [
                    new DadRosterCharacterRef
                    {
                        AccountKey = new DadAccountKey("slot1-account"),
                        CharacterKey = new DadCharacterKey("Remote Leader@Alpha"),
                        ContentId = 1001,
                    },
                ],
            },
        };
        var leader = Character("Remote Leader@Alpha", "slot1-account", DadCharacterSource.PeerRuntime, 1001);

        Assert.True(DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(request, leader, out var remoteBlocker));
        Assert.Empty(remoteBlocker);

        request.Orchestration.AuthorityMode = DadAuthorityMode.LocalOnly;
        Assert.False(DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(request, leader, out var authorityBlocker));
        Assert.Contains("ServerDad", authorityBlocker, StringComparison.Ordinal);

        request.Orchestration.AuthorityMode = DadAuthorityMode.ServerDad;
        leader.ContentId = 9999;
        Assert.False(DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(request, leader, out var driftBlocker));
        Assert.Contains("exact", driftBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoordinatorIdentityIsUnrelatedWhileSlotOneAccountCharacterAndCidStayExact()
    {
        var request = new DadRunRequest
        {
            DailyMsq = new DadDailyMsqTask(),
            Orchestration = new DadOrchestrationIntent
            {
                AuthorityMode = DadAuthorityMode.ServerDad,
                QueueAuthority = DadQueueAuthority.Leader,
                PreferredLeaderCharacterKey = new DadCharacterKey("Requested Leader@World"),
                RequiredRosterCharacters =
                [
                    new DadRosterCharacterRef
                    {
                        AccountKey = new DadAccountKey("coordinator-account"),
                        CharacterKey = new DadCharacterKey("Requested Leader@World"),
                        ContentId = 1001,
                    },
                ],
                RosterIntent = new DadRosterIntent { ExpectedPartySize = 4 },
            },
        };
        var requestedLeader = Character("Requested Leader@World", "coordinator-account", DadCharacterSource.PeerRuntime, 1001);
        var unrelatedCoordinator = Character("Unrelated Coordinator@World", "different-account", DadCharacterSource.LocalRuntime, 9009);

        Assert.True(DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(
            request,
            requestedLeader,
            new DadAccountKey("different-account"),
            unrelatedCoordinator,
            requireExactLocalIdentity: true,
            allowWakeableCoordinatorLeader: false,
            out var allowedBlocker));
        Assert.Empty(allowedBlocker);

        var wrongAccount = Character("Requested Leader@World", "different-account", DadCharacterSource.PeerRuntime, 1001);
        Assert.False(DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(
            request,
            wrongAccount,
            new DadAccountKey("different-account"),
            unrelatedCoordinator,
            requireExactLocalIdentity: true,
            allowWakeableCoordinatorLeader: false,
            out var accountBlocker));
        Assert.Contains("exact", accountBlocker, StringComparison.OrdinalIgnoreCase);

        var wrongCid = Character("Requested Leader@World", "coordinator-account", DadCharacterSource.PeerRuntime, 9999);
        Assert.False(DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(
            request,
            wrongCid,
            new DadAccountKey("different-account"),
            unrelatedCoordinator,
            requireExactLocalIdentity: true,
            allowWakeableCoordinatorLeader: false,
            out var cidBlocker));
        Assert.Contains("Content ID", cidBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitLiveCoordinatorTruthNeverFallsBackToProjectedLocalRuntimeRows()
    {
        var peerAdvertisingLocal = LiveTruth(
            Character("X Character@World", "different-account", DadCharacterSource.LocalRuntime, 4242),
            "different-account",
            "worker-x",
            "client-x",
            isLocalClient: false);

        Assert.Null(DadFullPartyExecutionRules.ResolveActiveCoordinatorCharacter(null));
        Assert.Null(DadFullPartyExecutionRules.ResolveActiveCoordinatorCharacter(peerAdvertisingLocal));

        var venat = Character("Venat@World", "coordinator-account", DadCharacterSource.LocalRuntime, 1001);
        var explicitLocal = LiveTruth(
            venat,
            "coordinator-account",
            "worker-w",
            "client-w",
            isLocalClient: true);
        var resolved = DadFullPartyExecutionRules.ResolveActiveCoordinatorCharacter(explicitLocal);

        Assert.NotNull(resolved);
        Assert.NotSame(venat, resolved);
        Assert.Equal("Venat@World", resolved!.CharacterKey);
        Assert.Equal((ulong)1001, resolved.ContentId);
    }

    [Fact]
    public void QueuePreflightRunsLocallyOnlyWhenCoordinatorIsExactSlotOne()
    {
        var (plan, _) = ValidParty();
        plan.Orchestration.RequiredRosterCharacters =
        [
            new DadRosterCharacterRef
            {
                AccountKey = new DadAccountKey("slot1-account"),
                CharacterKey = new DadCharacterKey("Character-1"),
                ContentId = 1001,
            },
        ];
        var unrelated = LiveTruth(
            Character("Coordinator@Other", "coordinator-account", DadCharacterSource.LocalRuntime, 9009),
            "coordinator-account",
            "coordinator-worker",
            "coordinator-client",
            isLocalClient: true);
        var localSlotOne = LiveTruth(
            Character("Character-1", "slot1-account", DadCharacterSource.LocalRuntime, 1001),
            "slot1-account",
            "slot1-worker",
            "slot1-client",
            isLocalClient: true);

        Assert.False(DadFullPartyExecutionRules.IsQueueAuthorityLocal(plan, unrelated));
        Assert.True(DadFullPartyExecutionRules.IsQueueAuthorityLocal(plan, localSlotOne));
    }

    private static IReadOnlyList<DadModuleBlockerDto> Evaluate(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants)
        => DadFullPartyExecutionRules.Evaluate(
            plan,
            DadModuleId.DailyMsq,
            participants,
            4,
            "Daily Roulette");

    private static DadAcquiredCharacter Character(
        string characterKey,
        string accountId,
        DadCharacterSource source,
        ulong contentId = 1)
        => new()
        {
            CharacterKey = characterKey,
            AccountId = accountId,
            Source = source,
            ContentId = contentId,
            Freshness = DadSnapshotFreshness.Live,
            Readiness = DadReadinessState.Ready,
        };

    private static DadParticipantSnapshot LiveTruth(
        DadAcquiredCharacter character,
        string accountId,
        string workerSessionId,
        string clientInstanceId,
        bool isLocalClient)
        => new()
        {
            ClientInstanceId = clientInstanceId,
            WorkerSessionId = new DadWorkerSessionId(workerSessionId),
            ManagedAccountKey = new DadAccountKey(accountId),
            ActiveCharacterKey = new DadCharacterKey(character.CharacterKey),
            Character = character,
            IsLocalClient = isLocalClient,
            IsAvailable = true,
            WorldReadyStable = true,
        };

    private static (DadRunPlan Plan, List<DadParticipantSnapshot> Participants) ValidParty()
    {
        var plan = new DadRunPlan
        {
            RequiredParticipantCount = 4,
            LeaderCharacterKey = "Character-1",
            Orchestration = new DadOrchestrationIntent
            {
                AuthorityMode = DadAuthorityMode.ServerDad,
                QueueAuthority = DadQueueAuthority.Leader,
            },
        };
        var participants = Enumerable.Range(1, 4)
            .Select(index => new DadParticipantSnapshot
            {
                ActiveCharacterKey = new DadCharacterKey($"Character-{index}"),
                AssignedSlotId = DadPlannerSlotRules.FormatSlotId(index),
                IsLocalClient = index == 1,
                IsAuthority = index == 1,
                IsAvailable = true,
                IsEligibleForRun = true,
                PostArReady = true,
                State = DadParticipantState.Ready,
                ClaimState = DadClaimState.Granted,
                LeaseState = DadParticipantLeaseState.Granted,
            })
            .ToList();
        return (plan, participants);
    }
}
