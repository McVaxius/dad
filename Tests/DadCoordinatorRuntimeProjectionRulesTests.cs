using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadCoordinatorRuntimeProjectionRulesTests
{
    [Fact]
    public void LocalPresenceSuppressesItsMirroredHubRow()
    {
        var local = Participant("worker-w", "account-w", isLocal: true);
        var mirroredW = Participant("worker-w", "account-w", isLocal: false);
        var remoteX = Participant("worker-x", "account-x", isLocal: false);

        var projected = DadCoordinatorRuntimeProjectionRules.BuildOnlineParticipantSet(
            local,
            [mirroredW, remoteX],
            static _ => true);

        Assert.Equal(2, projected.Count);
        Assert.Single(projected, static participant => participant.WorkerSessionId.Value == "worker-w");
        Assert.Single(projected, static participant => participant.WorkerSessionId.Value == "worker-x");
        Assert.True(projected.Single(static participant => participant.WorkerSessionId.Value == "worker-w").IsLocalClient);
    }

    [Fact]
    public void GenuineRemoteDuplicateSessionsRemainVisibleAndFailBinding()
    {
        var local = Participant("worker-w", "account-w", isLocal: true);
        var x1 = Participant("worker-x1", "account-x", isLocal: false);
        var x2 = Participant("worker-x2", "account-x", isLocal: false);
        var projected = DadCoordinatorRuntimeProjectionRules.BuildOnlineParticipantSet(
            local,
            [x1, x2],
            static _ => true);
        var manifest = new DadRunSlotManifest
        {
            Slots =
            [
                new DadFrozenRunSlot
                {
                    SlotId = "Slot1",
                    AccountKey = new DadAccountKey("account-w"),
                    RequiredJobId = 19,
                },
                new DadFrozenRunSlot
                {
                    SlotId = "Slot2",
                    AccountKey = new DadAccountKey("account-x"),
                    RequiredJobId = 19,
                },
            ],
        };

        Assert.False(DadRunSlotManifestRules.TryBindWorkerSessions(manifest, projected, out _, out var blocker));
        Assert.Contains("2 online Dad worker sessions", blocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FrozenDiscoveryAlsoSuppressesTheMirroredLocalRow()
    {
        var local = Participant("worker-w", "account-w", isLocal: true);
        var projected = DadCoordinatorRuntimeProjectionRules.BuildFrozenParticipantSet(
            local,
            [Participant("worker-w", "account-w", isLocal: false), Participant("worker-x", "account-x", isLocal: false)],
            new HashSet<string>(["worker-w", "worker-x"], StringComparer.OrdinalIgnoreCase),
            static _ => true);

        Assert.Equal(["worker-w", "worker-x"], projected.Select(static participant => participant.WorkerSessionId.Value).ToArray());
    }

    [Fact]
    public void MixedLanAndDadIslandDiscoveryResolvesTheExactLocalCharacterWithoutWaitingForItself()
    {
        var local = Participant("worker-w", "account-w", isLocal: true);
        local.ActiveCharacterKey = new DadCharacterKey("Local Character@World");
        local.Character = new DadAcquiredCharacter
        {
            AccountId = "account-w",
            CharacterKey = "Local Character@World",
            ContentId = 1001,
            CurrentJobId = 19,
            Freshness = DadSnapshotFreshness.Live,
            Readiness = DadReadinessState.Ready,
        };
        local.WorldReadyStable = true;
        local.RegisteredIslandId = "island-local";
        var localSlot = new DadFrozenRunSlot
        {
            SlotId = "Slot1",
            RouteKind = DadRunSlotRouteKind.LanWorker,
            AccountKey = new DadAccountKey("account-w"),
            CharacterKey = new DadCharacterKey("Local Character@World"),
            ContentId = 1001,
            WorkerSessionId = new DadWorkerSessionId("worker-w"),
            RequiredJobId = 19,
            IsLeader = true,
            IsInviter = true,
        };

        var projected = DadCoordinatorRuntimeProjectionRules.BuildFrozenParticipantSet(
            local,
            [local.Clone()],
            new HashSet<string>(["worker-w"], StringComparer.OrdinalIgnoreCase),
            static _ => true);
        var resolved = DadRunSlotManifestRules.ResolveSlot(
            localSlot,
            projected,
            requirePostArReady: false,
            out var blocker);
        var dadIsland = new DadParticipantSnapshot
        {
            RegisteredIslandId = "island-remote",
            AssignedSlotId = "Slot2",
            State = DadParticipantState.Discovered,
        };
        var manifest = new DadRunSlotManifest
        {
            Slots =
            [
                localSlot,
                new DadFrozenRunSlot
                {
                    SlotId = "Slot2",
                    RouteKind = DadRunSlotRouteKind.RegisteredIsland,
                    IslandId = "island-remote",
                },
            ],
        };

        Assert.Empty(blocker);
        Assert.Equal(DadParticipantState.Discovered, resolved.State);
        Assert.True(resolved.IsLocalClient);
        Assert.Equal("island-local", resolved.RegisteredIslandId);
        Assert.Same(resolved, Assert.Single(DadCoordinatorTravelRules.SelectLanParticipants(manifest, [resolved, dadIsland])));
    }

    [Fact]
    public void RemoteSelfLocalProjectionIsNormalizedAndRemainsParticipantWork()
    {
        var local = Participant("worker-w", "account-w", isLocal: true);
        var remoteFromItsOwnPerspective = Participant("worker-x", "account-x", isLocal: true);
        remoteFromItsOwnPerspective.IsAuthority = true;

        var projected = DadCoordinatorRuntimeProjectionRules.BuildOnlineParticipantSet(
            local,
            [remoteFromItsOwnPerspective],
            static _ => true);
        var remote = projected.Single(static participant => participant.WorkerSessionId.Value == "worker-x");

        Assert.False(remote.IsLocalClient);
        Assert.False(remote.IsAuthority);
        Assert.NotEqual(local.WorkerSessionId, remote.WorkerSessionId);
    }

    private static DadParticipantSnapshot Participant(string worker, string account, bool isLocal)
        => new()
        {
            WorkerSessionId = new DadWorkerSessionId(worker),
            ManagedAccountKey = new DadAccountKey(account),
            IsLocalClient = isLocal,
            IsAvailable = true,
            IsEligibleForRun = true,
            State = DadParticipantState.Ready,
        };
}
