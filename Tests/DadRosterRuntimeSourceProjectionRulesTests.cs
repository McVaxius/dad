using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRosterRuntimeSourceProjectionRulesTests
{
    private static readonly DadWorkerSessionId WorkerW = new("worker-w");

    [Fact]
    public void ProducerLocalRowsAreRebasedToTheConsumingClientRegardlessOfMergeOrder()
    {
        var venat = Row(
            "account-w",
            "Venat@Excalibur",
            1001,
            DadCharacterSource.PeerRuntime,
            "client-w",
            "worker-w");
        var xCharacter = Row(
            "account-x",
            "X Character@Excalibur",
            4242,
            DadCharacterSource.LocalRuntime,
            "client-x",
            "worker-x");
        var connected = new[]
        {
            Participant("account-w", "Venat@Excalibur", 1001, "client-w", "worker-w", isLocal: true),
            Participant("account-x", "X Character@Excalibur", 4242, "client-x", "worker-x", isLocal: true),
        };

        foreach (var input in new[] { new[] { venat, xCharacter }, new[] { xCharacter, venat } })
        {
            var projected = input
                .Select(character => DadRosterRuntimeSourceProjectionRules.ProjectForConsumer(
                    character,
                    WorkerW,
                    "client-w",
                    connected).Character)
                .ToList();

            var local = Assert.Single(projected, static character =>
                character.Source == DadCharacterSource.LocalRuntime);
            Assert.Equal("Venat@Excalibur", local.CharacterKey.Value);
            Assert.Equal("account-w", local.AccountKey.Value);

            var peer = Assert.Single(projected, static character =>
                character.Source == DadCharacterSource.PeerRuntime);
            Assert.Equal("X Character@Excalibur", peer.CharacterKey.Value);
            Assert.Equal("account-x", peer.AccountKey.Value);
        }

        Assert.Equal(DadCharacterSource.PeerRuntime, venat.Source);
        Assert.Equal(DadCharacterSource.LocalRuntime, xCharacter.Source);
    }

    [Fact]
    public void OwnerlessRuntimeClaimBecomesStoredOrUnresolvedWithoutChangingLedgerData()
    {
        var stored = Row(
            "account-y",
            "Hildabrand@Excalibur",
            303,
            DadCharacterSource.LocalRuntime,
            string.Empty,
            string.Empty);
        stored.XadbReady = true;
        stored.LastSnapshotUtc = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
        stored.JobLevels = new Dictionary<uint, int> { [38] = 90, [19] = 100 };

        var projection = DadRosterRuntimeSourceProjectionRules.ProjectForConsumer(
            stored,
            WorkerW,
            "client-w",
            []);

        Assert.Equal(DadRosterRuntimeSourceOwnership.UnresolvedRuntimeClaim, projection.Ownership);
        Assert.Equal(DadCharacterSource.XadbOnly, projection.Character.Source);
        Assert.False(projection.Character.IsCurrent);
        Assert.Equal(90, projection.Character.JobLevels[38]);
        Assert.Equal(DadCharacterSource.LocalRuntime, stored.Source);
        Assert.True(stored.IsCurrent);
        Assert.NotSame(stored.JobLevels, projection.Character.JobLevels);

        stored.XadbReady = false;
        stored.LastSnapshotUtc = null;
        var unresolved = DadRosterRuntimeSourceProjectionRules.ProjectForConsumer(
            stored,
            WorkerW,
            "client-w",
            []);
        Assert.Equal(DadCharacterSource.ManualUnresolved, unresolved.Character.Source);
    }

    [Fact]
    public void MissingExplicitLocalOwnerCannotPromoteAnyConnectedPeer()
    {
        var xCharacter = Row(
            "account-x",
            "X Character@Excalibur",
            4242,
            DadCharacterSource.LocalRuntime,
            "client-x",
            "worker-x");
        var peer = Participant(
            "account-x",
            "X Character@Excalibur",
            4242,
            "client-x",
            "worker-x",
            isLocal: true);

        var projection = DadRosterRuntimeSourceProjectionRules.ProjectForConsumer(
            xCharacter,
            new DadWorkerSessionId(string.Empty),
            string.Empty,
            [peer]);

        Assert.NotEqual(DadCharacterSource.LocalRuntime, projection.Character.Source);
        Assert.Equal(DadCharacterSource.PeerRuntime, projection.Character.Source);
    }

    [Fact]
    public void PartialLocalOwnerClaimCannotBecomeLocalRuntime()
    {
        var partial = Row(
            "account-w",
            "Venat@Excalibur",
            1001,
            DadCharacterSource.LocalRuntime,
            string.Empty,
            "worker-w");

        var projection = DadRosterRuntimeSourceProjectionRules.ProjectForConsumer(
            partial,
            WorkerW,
            "client-w",
            []);

        Assert.NotEqual(DadCharacterSource.LocalRuntime, projection.Character.Source);
        Assert.Equal(DadRosterRuntimeSourceOwnership.UnresolvedRuntimeClaim, projection.Ownership);
    }

    private static DadRosterCharacter Row(
        string account,
        string character,
        ulong contentId,
        DadCharacterSource source,
        string client,
        string worker)
        => new()
        {
            AccountKey = new DadAccountKey(account),
            CharacterKey = new DadCharacterKey(character),
            ContentId = contentId,
            Source = source,
            SourceClientInstanceId = client,
            SourceWorkerSessionId = new DadWorkerSessionId(worker),
            IsCurrent = true,
        };

    private static DadParticipantSnapshot Participant(
        string account,
        string character,
        ulong contentId,
        string client,
        string worker,
        bool isLocal)
        => new()
        {
            ClientInstanceId = client,
            WorkerSessionId = new DadWorkerSessionId(worker),
            ManagedAccountKey = new DadAccountKey(account),
            ActiveCharacterKey = new DadCharacterKey(character),
            IsLocalClient = isLocal,
            IsAvailable = true,
            State = DadParticipantState.Ready,
            Character = new DadAcquiredCharacter
            {
                AccountId = account,
                CharacterKey = character,
                ContentId = contentId,
                Source = DadCharacterSource.LocalRuntime,
            },
        };
}
