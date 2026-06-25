using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRosterTransportCatalogRuntimeTests
{
    [Fact]
    public void LocalTransportPoolUsesCurrentLocalRuntimeRowsOnly()
    {
        var pool = new DadCharacterPool
        {
            Characters =
            [
                Character("W Character@Alpha", "acct-w", DadCharacterSource.LocalRuntime, contentId: 101),
                Character("X Character@Alpha", "acct-x", DadCharacterSource.PeerRuntime, contentId: 202),
            ],
        };

        var transportPool = DadRosterTransportCatalogRuntime.BuildLocalTransportPool(
            pool,
            fallbackSnapshot: Snapshot("fallback-client", "fallback-worker", "acct-fallback", "Fallback@Alpha", 303),
            currentTransport: new DadPeerTransportSnapshot { LocalClientInstanceId = "dad-w" });

        var character = Assert.Single(transportPool.Characters);
        Assert.Equal("W Character@Alpha", character.CharacterKey);
        Assert.Equal("acct-w", character.AccountId);
        Assert.Equal(DadCharacterSource.LocalRuntime, character.Source);
    }

    [Fact]
    public void LocalTransportPoolUsesPresenceFallbackWhenCurrentPoolHasNoLocalRow()
    {
        var fallback = Snapshot("dad-w", "worker-w", "acct-w", "W Character@Alpha", 101);
        fallback.Character.CharacterKey = string.Empty;

        var transportPool = DadRosterTransportCatalogRuntime.BuildLocalTransportPool(
            new DadCharacterPool
            {
                Characters = [Character("X Character@Alpha", "acct-x", DadCharacterSource.PeerRuntime, contentId: 202)],
            },
            fallback,
            new DadPeerTransportSnapshot { LocalClientInstanceId = "dad-w" });

        var character = Assert.Single(transportPool.Characters);
        Assert.Equal("W Character@Alpha", character.CharacterKey);
        Assert.Equal("acct-w", character.AccountId);
        Assert.Equal("Dad acct-w", character.AccountAlias);
        Assert.Equal(DadCharacterSource.LocalRuntime, character.Source);
        Assert.Equal(DadSnapshotFreshness.Live, character.Freshness);
    }

    [Fact]
    public void EmptyPeerCatalogResponseStillAllowsRuntimeFallback()
    {
        var runtime = PeerSnapshot("client-x", "worker-x", "acct-x", "X Character@Alpha", 202);
        var emptyCatalogResponse = PeerCatalog("client-x", "worker-x");

        Assert.True(DadRosterTransportCatalogRuntime.ShouldUsePeerRuntimeFallback(
            runtime,
            [emptyCatalogResponse]));
    }

    [Fact]
    public void PeerCatalogWithMatchingCharacterSuppressesRuntimeFallback()
    {
        var runtime = PeerSnapshot("client-x", "worker-x", "acct-x", "X Character@Alpha", 202);
        var catalogResponse = PeerCatalog(
            "client-x",
            "worker-x",
            RosterCharacter("acct-x", "X Character@Alpha", 202, "client-x", "worker-x"));

        Assert.False(DadRosterTransportCatalogRuntime.ShouldUsePeerRuntimeFallback(
            runtime,
            [catalogResponse]));
    }

    [Fact]
    public void AggregateStyleCatalogMatchesDistinctAccountScopedRows()
    {
        var serverRuntime = PeerSnapshot("client-w", "worker-w", "acct-w", "Shared Character@Alpha", 303);
        var clientRuntime = PeerSnapshot("client-x", "worker-x", "acct-x", "Shared Character@Alpha", 303);
        var serverRow = RosterCharacter("acct-w", "Shared Character@Alpha", 303, "client-w", "worker-w");
        var clientRow = RosterCharacter("acct-x", "Shared Character@Alpha", 303, "client-x", "worker-x");
        var aggregateResponse = PeerCatalog("client-server", "worker-server", serverRow, clientRow);

        Assert.False(DadRosterTransportCatalogRuntime.ShouldUsePeerRuntimeFallback(
            serverRuntime,
            [aggregateResponse]));
        Assert.False(DadRosterTransportCatalogRuntime.ShouldUsePeerRuntimeFallback(
            clientRuntime,
            [aggregateResponse]));
        Assert.NotEqual(DadRosterIdentity.BuildKey(serverRow), DadRosterIdentity.BuildKey(clientRow));
    }

    private static DadAcquiredCharacter Character(
        string characterKey,
        string accountId,
        DadCharacterSource source,
        ulong contentId)
    {
        var parts = characterKey.Split('@', 2, StringSplitOptions.TrimEntries);
        return new DadAcquiredCharacter
        {
            CharacterKey = characterKey,
            ContentId = contentId,
            CharacterName = parts[0],
            WorldName = parts.Length == 2 ? parts[1] : string.Empty,
            AccountId = accountId,
            AccountAlias = $"Dad {accountId}",
            Source = source,
            Freshness = DadSnapshotFreshness.Live,
            Readiness = DadReadinessState.Ready,
        };
    }

    private static DadParticipantSnapshot Snapshot(
        string clientId,
        string workerId,
        string accountId,
        string characterKey,
        ulong contentId)
        => new()
        {
            ClientInstanceId = clientId,
            WorkerSessionId = new DadWorkerSessionId(workerId),
            ManagedAccountKey = new DadAccountKey(accountId),
            ManagedAccountAlias = $"Dad {accountId}",
            ActiveCharacterKey = new DadCharacterKey(characterKey),
            IsAvailable = true,
            LastHeartbeatUtc = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc),
            Character = Character(characterKey, accountId, DadCharacterSource.LocalRuntime, contentId),
        };

    private static DadPeerSnapshotResponse PeerSnapshot(
        string clientId,
        string workerId,
        string accountId,
        string characterKey,
        ulong contentId)
    {
        var participant = Snapshot(clientId, workerId, accountId, characterKey, contentId);
        participant.Character.Source = DadCharacterSource.PeerRuntime;
        return new DadPeerSnapshotResponse
        {
            ClientInstanceId = clientId,
            Character = participant.Character.Clone(),
            Participant = participant,
        };
    }

    private static DadPeerRosterCatalogResponse PeerCatalog(
        string clientId,
        string workerId,
        params DadRosterCharacter[] characters)
        => new()
        {
            ClientInstanceId = clientId,
            WorkerSessionId = new DadWorkerSessionId(workerId),
            Catalog = new DadAccountRosterCatalog
            {
                SourceClientInstanceId = clientId,
                SourceWorkerSessionId = new DadWorkerSessionId(workerId),
                Characters = characters.Select(static character => character.Clone()).ToList(),
            },
        };

    private static DadRosterCharacter RosterCharacter(
        string accountId,
        string characterKey,
        ulong contentId,
        string clientId,
        string workerId)
    {
        var parts = characterKey.Split('@', 2, StringSplitOptions.TrimEntries);
        return new DadRosterCharacter
        {
            AccountKey = new DadAccountKey(accountId),
            AccountAlias = $"Dad {accountId}",
            CharacterKey = new DadCharacterKey(characterKey),
            ContentId = contentId,
            CharacterName = parts[0],
            WorldName = parts.Length == 2 ? parts[1] : string.Empty,
            Source = DadCharacterSource.PeerRuntime,
            SourceClientInstanceId = clientId,
            SourceWorkerSessionId = new DadWorkerSessionId(workerId),
            LastRuntimeSeenUtc = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc),
        };
    }
}
