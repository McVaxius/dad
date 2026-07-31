using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadCrewFormationPlannerRulesTests
{
    private const string LeaderAccount = "leader-account";
    private const string LeaderCharacter = "Leader Example@Alpha";
    private const ulong LeaderContentId = 1001;
    private const string PeerAccount = "peer-account";
    private const string PeerCharacter = "Peer Example@Alpha";
    private const ulong PeerContentId = 2002;

    [Fact]
    public void FormationPlan_IgnoresQueuePayloadAndSatisfiedStopPolicy()
    {
        var request = BuildRequest();
        request.StopPolicy = new DadRunStopPolicy
        {
            Mode = DadPlannerStopMode.TargetLevel,
            TargetCharacterKey = new DadCharacterKey(LeaderCharacter),
            TargetLevel = 1,
        };

        var built = TryBuild(
            request,
            BuildPool(leaderReady: true, peerReady: true),
            requireLiveReadiness: true,
            out var plan,
            out var blocker);

        Assert.True(built, blocker);
        Assert.Equal(DadModuleId.None, plan.CompositeModuleId);
        Assert.Equal(2, plan.RequiredParticipantCount);
        Assert.True(plan.RequiresRemoteParticipants);
        Assert.Single(plan.Modules);
        Assert.Equal(2, plan.Modules[0].ExpectedPartySize);
        Assert.Null(request.DailyMsq);
        Assert.Null(request.PremadeDuty);
    }

    [Fact]
    public void RealPlannerBuildPlan_UsesFormationBranchWithoutQueueTaskValidation()
    {
        var request = BuildRequest();
        var planner = new DadPlannerService(
            new DadPresetProviderService(),
            new DadModuleRegistry(),
            new Configuration { ClientAccountId = LeaderAccount });

        var plan = planner.BuildPlan(
            request,
            BuildPool(leaderReady: true, peerReady: true),
            out var blocker,
            requireLiveReadiness: true,
            liveLocalRuntimeTruth: BuildLiveCoordinatorTruth());

        Assert.NotNull(plan);
        Assert.Equal(string.Empty, blocker);
        Assert.Equal(DadModuleId.None, plan.CompositeModuleId);
        Assert.Equal(2, plan.RequiredParticipantCount);
    }

    [Fact]
    public void FormationPlan_TemporaryReadinessPassesRelaxedAdmissionButFailsStrictAdmission()
    {
        var request = BuildRequest();
        var pool = BuildPool(leaderReady: true, peerReady: false);

        Assert.True(TryBuild(
            request,
            pool,
            requireLiveReadiness: false,
            out _,
            out var relaxedBlocker),
            relaxedBlocker);
        Assert.False(TryBuild(
            request,
            pool,
            requireLiveReadiness: true,
            out _,
            out var strictBlocker));
        Assert.Contains("not live", strictBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormationPlan_RejectsDuplicateExactIdentities()
    {
        var request = BuildRequest();
        request.Orchestration.RequiredRosterCharacters[1].AccountKey = new DadAccountKey(LeaderAccount);

        Assert.False(TryBuild(
            request,
            BuildPool(leaderReady: true, peerReady: true),
            requireLiveReadiness: false,
            out _,
            out var blocker));
        Assert.Contains("multiple", blocker, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(99u)]
    [InlineData(32u)]
    public void FormationPlan_ValidatesRequestedCombatJobAndLearnedLedger(uint requestedJobId)
    {
        var request = BuildRequest();
        request.Orchestration.RequiredRosterCharacters[1].RequiredJobId = requestedJobId;
        var pool = BuildPool(leaderReady: true, peerReady: true);
        if (requestedJobId == 32)
            pool.Characters[1].JobLevels[32] = 0;

        Assert.False(TryBuild(
            request,
            pool,
            requireLiveReadiness: false,
            out _,
            out var blocker));
        Assert.Contains("class/job", blocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormationPlan_AdmitsUnrelatedCoordinatorAndRemoteSlotOne()
    {
        var request = BuildRequest();
        var configuration = new Configuration { ClientAccountId = "different-account" };
        var pool = BuildPool(leaderReady: true, peerReady: true);
        pool.Characters[0].Source = DadCharacterSource.PeerRuntime;
        var unrelatedCoordinator = BuildCharacter(
            "different-account",
            "Coordinator Example@Beta",
            9009,
            local: true,
            ready: true);

        Assert.True(DadCrewFormationPlannerRules.TryBuildPlan(
            request,
            pool,
            configuration,
            unrelatedCoordinator,
            requireLiveReadiness: true,
            allowWakeableCoordinatorLeader: false,
            out var plan,
            out var blocker));
        Assert.Empty(blocker);
        Assert.Equal(LeaderCharacter, plan.LeaderCharacterKey);
        Assert.Equal(LeaderCharacter, plan.InviterCharacterKey);
    }

    private static bool TryBuild(
        DadRunRequest request,
        DadCharacterPool pool,
        bool requireLiveReadiness,
        out DadRunPlan plan,
        out string blocker)
        => DadCrewFormationPlannerRules.TryBuildPlan(
            request,
            pool,
            new Configuration { ClientAccountId = LeaderAccount },
            BuildCharacter(LeaderAccount, LeaderCharacter, LeaderContentId, local: true, ready: true),
            requireLiveReadiness,
            allowWakeableCoordinatorLeader: !requireLiveReadiness,
            out plan,
            out blocker);

    private static DadRunRequest BuildRequest()
    {
        var roster = new List<DadRosterCharacterRef>
        {
            Reference(LeaderAccount, LeaderCharacter, LeaderContentId),
            Reference(PeerAccount, PeerCharacter, PeerContentId),
        };
        return new DadRunRequest
        {
            RequestId = "formation-request",
            RequestedBy = "crew-tools",
            Orchestration = new DadOrchestrationIntent
            {
                AutoPartyFormationOnly = true,
                AuthorityMode = DadAuthorityMode.ServerDad,
                TransportMode = DadTransportMode.ServerHub,
                QueueAuthority = DadQueueAuthority.Leader,
                InviteAuthority = DadInviteAuthority.PresetLeader,
                PreferredLeaderCharacterKey = new DadCharacterKey(LeaderCharacter),
                PreferredInviterCharacterKey = new DadCharacterKey(LeaderCharacter),
                RequiredRosterCharacters = roster,
                RequiredAccountKeys = roster.Select(static row => row.AccountKey).ToList(),
                RequiredCharacterKeys = roster.Select(static row => row.CharacterKey).ToList(),
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

    private static DadCharacterPool BuildPool(bool leaderReady, bool peerReady)
        => new()
        {
            Characters =
            [
                BuildCharacter(LeaderAccount, LeaderCharacter, LeaderContentId, local: true, ready: leaderReady),
                BuildCharacter(PeerAccount, PeerCharacter, PeerContentId, local: false, ready: peerReady),
            ],
        };

    private static DadParticipantSnapshot BuildLiveCoordinatorTruth()
        => new()
        {
            WorkerSessionId = new DadWorkerSessionId("local-worker"),
            ClientInstanceId = "local-client",
            ManagedAccountKey = new DadAccountKey(LeaderAccount),
            ActiveCharacterKey = new DadCharacterKey(LeaderCharacter),
            Character = BuildCharacter(
                LeaderAccount,
                LeaderCharacter,
                LeaderContentId,
                local: true,
                ready: true),
            IsLocalClient = true,
            IsAvailable = true,
        };

    private static DadAcquiredCharacter BuildCharacter(
        string account,
        string character,
        ulong contentId,
        bool local,
        bool ready)
        => new()
        {
            AccountId = account,
            CharacterKey = character,
            ContentId = contentId,
            Source = local ? DadCharacterSource.LocalRuntime : DadCharacterSource.PeerRuntime,
            Freshness = ready ? DadSnapshotFreshness.Live : DadSnapshotFreshness.Stale,
            Readiness = ready ? DadReadinessState.Ready : DadReadinessState.Blocked,
            JobLevels = new Dictionary<uint, int> { [19] = 90, [32] = 90 },
        };

    private static DadRosterCharacterRef Reference(string account, string character, ulong contentId)
        => new()
        {
            AccountKey = new DadAccountKey(account),
            CharacterKey = new DadCharacterKey(character),
            ContentId = contentId,
        };
}
