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
                new DadFrozenRunSlot { SlotId = "Slot1", AccountKey = new DadAccountKey("account-w") },
                new DadFrozenRunSlot { SlotId = "Slot2", AccountKey = new DadAccountKey("account-x") },
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
