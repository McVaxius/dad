using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadCoordinatorMutationBoundaryRulesTests
{
    private const string RunId = "strict-boundary-run";
    private const string CoordinatorAccount = "account-w";
    private const string LeaderCharacter = "W Leader@World";
    private const ulong LeaderContentId = 1001;

    [Fact]
    public void ExactFrozenRemoteSlotOneIsAcceptedWithoutMutatingInputs()
    {
        var (plan, manifest, runtime, liveCoordinator) = BuildBoundary();
        var originalStates = runtime.Select(static participant => participant.State).ToList();

        var accepted = DadCoordinatorMutationBoundaryRules.TryResolveStrictParticipants(
            plan,
            manifest,
            runtime,
            new DadAccountKey(CoordinatorAccount),
            liveCoordinator,
            out var resolved,
            out var blocker);

        Assert.True(accepted, blocker);
        Assert.Empty(blocker);
        Assert.Equal(4, resolved.Count);
        Assert.False(resolved[0].IsLocalClient);
        Assert.False(resolved[0].IsAuthority);
        Assert.Equal(originalStates, runtime.Select(static participant => participant.State));
    }

    [Fact]
    public void FrozenWorkerSessionDriftFailsBeforeMutation()
    {
        var (plan, manifest, runtime, liveCoordinator) = BuildBoundary();
        runtime[0].WorkerSessionId = new DadWorkerSessionId("replacement-session");

        var accepted = DadCoordinatorMutationBoundaryRules.TryResolveStrictParticipants(
            plan,
            manifest,
            runtime,
            new DadAccountKey(CoordinatorAccount),
            liveCoordinator,
            out _,
            out var blocker);

        Assert.False(accepted);
        Assert.Contains("frozen worker session", blocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoteSlotOneDoesNotRequireALocalRosterParticipant()
    {
        var (plan, manifest, runtime, liveCoordinator) = BuildBoundary();
        var accepted = DadCoordinatorMutationBoundaryRules.TryResolveStrictParticipants(
            plan,
            manifest,
            runtime,
            new DadAccountKey(CoordinatorAccount),
            liveCoordinator,
            out _,
            out var blocker);

        Assert.True(accepted, blocker);
        Assert.Empty(blocker);
    }

    [Fact]
    public void UnrelatedCoordinatorAccountDoesNotChangeFrozenSlotOneAuthority()
    {
        var (plan, manifest, runtime, _) = BuildBoundary();
        var wrongAccountRuntime = LiveCoordinator(
            Character(
                "Other Account Character@World",
                "different-account",
                9009,
                DadCharacterSource.LocalRuntime),
            "different-account");

        var accepted = DadCoordinatorMutationBoundaryRules.TryResolveStrictParticipants(
            plan,
            manifest,
            runtime,
            new DadAccountKey(CoordinatorAccount),
            wrongAccountRuntime,
            out _,
            out var blocker);

        Assert.True(accepted, blocker);
        Assert.Empty(blocker);
    }

    [Fact]
    public void WorldSafetyLossFailsTheFreshMutationBoundary()
    {
        var (plan, manifest, runtime, liveCoordinator) = BuildBoundary();
        runtime[0].WorldReadyStable = false;

        var accepted = DadCoordinatorMutationBoundaryRules.TryResolveStrictParticipants(
            plan,
            manifest,
            runtime,
            new DadAccountKey(CoordinatorAccount),
            liveCoordinator,
            out _,
            out var blocker);

        Assert.False(accepted);
        Assert.Contains("world-ready-stable", blocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingCoordinatorRosterTruthDoesNotReplaceExactSlotOneTruth()
    {
        var (plan, manifest, runtime, _) = BuildBoundary();

        var accepted = DadCoordinatorMutationBoundaryRules.TryResolveStrictParticipants(
            plan,
            manifest,
            runtime,
            new DadAccountKey(CoordinatorAccount),
            null,
            out _,
            out var blocker);

        Assert.True(accepted, blocker);
        Assert.Empty(blocker);
    }

    [Fact]
    public void SlotOneContentIdOrSessionDriftFailsBeforeMutation()
    {
        var (plan, manifest, runtime, liveCoordinator) = BuildBoundary();
        runtime[0].Character.ContentId = 9999;

        Assert.False(DadCoordinatorMutationBoundaryRules.TryResolveStrictParticipants(
            plan,
            manifest,
            runtime,
            new DadAccountKey(CoordinatorAccount),
            liveCoordinator,
            out _,
            out var contentBlocker));
        Assert.Contains("Content ID", contentBlocker, StringComparison.OrdinalIgnoreCase);

        (_, _, runtime, liveCoordinator) = BuildBoundary();
        runtime[0].WorkerSessionId = new DadWorkerSessionId("replacement-session");
        Assert.False(DadCoordinatorMutationBoundaryRules.TryResolveStrictParticipants(
            plan,
            manifest,
            runtime,
            new DadAccountKey(CoordinatorAccount),
            liveCoordinator,
            out _,
            out var sessionBlocker));
        Assert.Contains("frozen worker session", sessionBlocker, StringComparison.OrdinalIgnoreCase);
    }

    private static (DadRunPlan Plan, DadRunSlotManifest Manifest, List<DadParticipantSnapshot> Runtime, DadParticipantSnapshot LiveCoordinator) BuildBoundary()
    {
        var references = Enumerable.Range(1, 4)
            .Select(index => new DadRosterCharacterRef
            {
                AccountKey = new DadAccountKey(index == 1 ? CoordinatorAccount : $"account-{index}"),
                CharacterKey = new DadCharacterKey(index == 1 ? LeaderCharacter : $"Character-{index}@World"),
                ContentId = (ulong)(1000 + index),
            })
            .ToList();
        var orchestration = new DadOrchestrationIntent
        {
            AuthorityMode = DadAuthorityMode.ServerDad,
            QueueAuthority = DadQueueAuthority.Leader,
            InviteAuthority = DadInviteAuthority.ServerDad,
            PreferredLeaderCharacterKey = new DadCharacterKey(LeaderCharacter),
            PreferredInviterCharacterKey = new DadCharacterKey(LeaderCharacter),
            RequirePostArReady = true,
            RosterIntent = new DadRosterIntent
            {
                ExpectedPartySize = 4,
                RequireRemoteParticipants = true,
                RequireExactCharacters = true,
            },
            RequiredRosterCharacters = references,
        };
        var request = new DadRunRequest
        {
            RequestId = RunId,
            DailyMsq = new DadDailyMsqTask(),
            Orchestration = orchestration,
        };
        var plan = new DadRunPlan
        {
            Request = request,
            Orchestration = orchestration,
            CompositeModuleId = DadModuleId.DailyMsq,
            RequiredParticipantCount = 4,
            RequiresRemoteParticipants = true,
            LeaderCharacterKey = LeaderCharacter,
            InviterCharacterKey = LeaderCharacter,
            Modules =
            [
                new DadPlannedModuleExecution
                {
                    ModuleId = DadModuleId.DailyMsq,
                    DisplayName = "Daily Roulette",
                    ExpectedPartySize = 4,
                    RequiresPeers = true,
                },
            ],
        };
        var manifest = new DadRunSlotManifest
        {
            RequestId = RunId,
            ExpectedPartySize = 4,
            LeaderCharacterKey = LeaderCharacter,
            InviterCharacterKey = LeaderCharacter,
            Slots = references.Select((reference, index) => new DadFrozenRunSlot
            {
                SlotId = DadPlannerSlotRules.FormatSlotId(index + 1),
                AccountKey = reference.AccountKey,
                CharacterKey = reference.CharacterKey,
                ContentId = reference.ContentId,
                IsLeader = index == 0,
                IsInviter = index == 0,
                WorkerSessionId = new DadWorkerSessionId($"worker-{index + 1}"),
            }).ToList(),
        };
        var runtime = manifest.Slots.Select((slot, index) => new DadParticipantSnapshot
        {
            ClientInstanceId = $"client-{index + 1}",
            WorkerSessionId = slot.WorkerSessionId,
            ManagedAccountKey = slot.AccountKey,
            ActiveCharacterKey = slot.CharacterKey,
            Character = Character(
                slot.CharacterKey.Value,
                slot.AccountKey.Value,
                slot.ContentId,
                DadCharacterSource.PeerRuntime),
            IsLocalClient = false,
            IsAvailable = true,
            IsEligibleForRun = true,
            PostArReady = true,
            WorldReadyStable = true,
            State = DadParticipantState.AssemblyConfirmed,
        }).ToList();
        var liveCoordinator = LiveCoordinator(
            Character(
                "Unrelated Coordinator@Other",
                "unrelated-account",
                9009,
                DadCharacterSource.LocalRuntime),
            "unrelated-account");
        return (plan, manifest, runtime, liveCoordinator);
    }

    private static DadParticipantSnapshot LiveCoordinator(
        DadAcquiredCharacter character,
        string accountId)
        => new()
        {
            ClientInstanceId = "client-w",
            WorkerSessionId = new DadWorkerSessionId("coordinator-worker"),
            ManagedAccountKey = new DadAccountKey(accountId),
            ActiveCharacterKey = new DadCharacterKey(character.CharacterKey),
            Character = character,
            IsLocalClient = true,
            IsAvailable = true,
            IsEligibleForRun = true,
            PostArReady = true,
            WorldReadyStable = true,
        };

    private static DadAcquiredCharacter Character(
        string characterKey,
        string accountId,
        ulong contentId,
        DadCharacterSource source)
        => new()
        {
            CharacterKey = characterKey,
            AccountId = accountId,
            ContentId = contentId,
            Source = source,
            Freshness = DadSnapshotFreshness.Live,
            Readiness = DadReadinessState.Ready,
        };
}
