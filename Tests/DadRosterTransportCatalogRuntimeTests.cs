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
    public void LocalTransportPoolReplacesStaleLocalRowWithCurrentPresenceFallback()
    {
        var fallback = Snapshot("dad-w", "worker-w", "acct-w", "Current Character@Alpha", 101);

        var transportPool = DadRosterTransportCatalogRuntime.BuildLocalTransportPool(
            new DadCharacterPool
            {
                Characters = [Character("Stale Character@Alpha", "acct-w", DadCharacterSource.LocalRuntime, contentId: 202)],
            },
            fallback,
            new DadPeerTransportSnapshot { LocalClientInstanceId = "dad-w" });

        var character = Assert.Single(transportPool.Characters);
        Assert.Equal("Current Character@Alpha", character.CharacterKey);
        Assert.Equal(101UL, character.ContentId);
        Assert.Equal("acct-w", character.AccountId);
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
    public void ServerDadTransportSnapshotBuildsFallbackRowsForConnectedClientsMissingFromCatalog()
    {
        var server = Snapshot("client-w", "worker-w", "acct-w", "W Character@Alpha", 101);
        server.IsLocalClient = true;
        server.State = DadParticipantState.Ready;
        var clientX = Snapshot("client-x", "worker-x", "acct-x", "X Character@Alpha", 202);
        clientX.State = DadParticipantState.Ready;
        var clientY = Snapshot("client-y", "worker-y", "acct-y", "Y Character@Alpha", 303);
        clientY.State = DadParticipantState.Ready;
        var transport = Transport("client-w", "worker-w", server, clientX, clientY);
        var serverCatalog = PeerCatalog(
            "client-w",
            "worker-w",
            RosterCharacter("acct-w", "W Character@Alpha", 101, "client-w", "worker-w"));

        var rows = DadRosterTransportCatalogRuntime.BuildParticipantRuntimeFallbackRows(
            transport,
            [serverCatalog]);

        Assert.Equal(["acct-x", "acct-y"], rows.Select(static row => row.AccountKey.Value).Order().ToArray());
        Assert.All(rows, static row => Assert.Equal(DadCharacterSource.PeerRuntime, row.Source));
        Assert.Contains(rows, static row => row.SourceClientInstanceId == "client-x" &&
                                            row.SourceWorkerSessionId.Value == "worker-x");
        Assert.Contains(rows, static row => row.SourceClientInstanceId == "client-y" &&
                                            row.SourceWorkerSessionId.Value == "worker-y");
    }

    [Fact]
    public void ClientDadTransportSnapshotBuildsFallbackRowForMissingServerDad()
    {
        var server = Snapshot("client-w", "worker-w", "acct-w", "W Character@Alpha", 101);
        server.State = DadParticipantState.Ready;
        var transport = Transport("client-x", "worker-x", server);

        var row = Assert.Single(DadRosterTransportCatalogRuntime.BuildParticipantRuntimeFallbackRows(
            transport,
            []));

        Assert.Equal("acct-w", row.AccountKey.Value);
        Assert.Equal("W Character@Alpha", row.CharacterKey.Value);
        Assert.Equal(DadCharacterSource.PeerRuntime, row.Source);
        Assert.Equal("client-w", row.SourceClientInstanceId);
        Assert.Equal("worker-w", row.SourceWorkerSessionId.Value);
    }

    [Fact]
    public void ClientDadTransportRuntimeKeepsLocalSelfCoordinatorAndSiblingCatalogVisible()
    {
        var local = Snapshot("client-x", "worker-x", "acct-x", "X Character@Alpha", 202);
        local.IsLocalClient = true;
        local.State = DadParticipantState.Ready;
        var server = Snapshot("client-w", "worker-w", "acct-w", "W Character@Alpha", 101);
        server.IsAuthority = true;
        server.State = DadParticipantState.Ready;
        var siblingCatalog = PeerCatalog(
            "client-y",
            "worker-y",
            RosterCharacter("acct-y", "Y Character@Alpha", 303, "client-y", "worker-y"));
        var transport = Transport("client-x", "worker-x", local, server);
        transport.AuthorityEndpoint = "192.168.1.10:4647";

        var fallbackRows = DadRosterTransportCatalogRuntime.BuildParticipantRuntimeFallbackRows(
            transport,
            [siblingCatalog]);

        Assert.Equal(["acct-w", "acct-x"], fallbackRows.Select(static row => row.AccountKey.Value).Order().ToArray());
        Assert.Contains(fallbackRows, static row => row.Source == DadCharacterSource.LocalRuntime &&
                                                    row.SourceWorkerSessionId.Value == "worker-x");
        Assert.Contains(fallbackRows, static row => row.Source == DadCharacterSource.PeerRuntime &&
                                                    row.SourceWorkerSessionId.Value == "worker-w");
        Assert.True(DadRosterTransportCatalogRuntime.IsRosterOwnerReachable(
            new DadWorkerSessionId("worker-y"),
            "client-y",
            transport,
            [siblingCatalog]));
    }

    [Fact]
    public void ReturnedUsableCatalogRowSuppressesHeartbeatFallbackRow()
    {
        var client = Snapshot("client-x", "worker-x", "acct-x", "X Character@Alpha", 202);
        client.State = DadParticipantState.Ready;
        var transport = Transport("client-w", "worker-w", client);
        var catalogResponse = PeerCatalog(
            "client-x",
            "worker-x",
            RosterCharacter("acct-x", "X Character@Alpha", 202, "client-x", "worker-x"));

        var rows = DadRosterTransportCatalogRuntime.BuildParticipantRuntimeFallbackRows(
            transport,
            [catalogResponse]);

        Assert.Empty(rows);
    }

    [Fact]
    public void StaleParticipantsDoNotBuildHeartbeatFallbackRows()
    {
        var client = Snapshot("client-x", "worker-x", "acct-x", "X Character@Alpha", 202);
        client.State = DadParticipantState.Stale;
        var transport = Transport("client-w", "worker-w", client);

        var rows = DadRosterTransportCatalogRuntime.BuildParticipantRuntimeFallbackRows(
            transport,
            []);

        Assert.Empty(rows);
    }

    [Fact]
    public void LocalServerDadWorkerIsReachableForRosterOwnership()
    {
        var transport = new DadPeerTransportSnapshot
        {
            LocalClientInstanceId = "client-w",
            LocalWorkerSessionId = new DadWorkerSessionId("worker-w"),
            AuthorityWorkerSessionId = new DadWorkerSessionId("worker-w"),
            AuthorityRole = DadWorkerRole.ServerDad,
        };

        Assert.True(DadRosterTransportCatalogRuntime.IsRosterOwnerReachable(
            new DadWorkerSessionId("worker-w"),
            "client-w",
            transport,
            []));
    }

    [Fact]
    public void ConnectedClientParticipantsAreReachableForRosterOwnershipOnServerDad()
    {
        var client = Snapshot("client-x", "worker-x", "acct-x", "X Character@Alpha", 202);
        client.State = DadParticipantState.Ready;
        var transport = new DadPeerTransportSnapshot
        {
            LocalClientInstanceId = "client-w",
            LocalWorkerSessionId = new DadWorkerSessionId("worker-w"),
            AuthorityWorkerSessionId = new DadWorkerSessionId("worker-w"),
            AuthorityRole = DadWorkerRole.ServerDad,
            KnownParticipants = [client],
        };

        Assert.True(DadRosterTransportCatalogRuntime.IsRosterOwnerReachable(
            new DadWorkerSessionId("worker-x"),
            "client-x",
            transport,
            []));
    }

    [Fact]
    public void SiblingClientFromRecentAggregateResponseIsReachableForRosterOwnershipOnClientDad()
    {
        var now = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);
        var server = Snapshot("client-w", "worker-w", "acct-w", "W Character@Alpha", 101);
        server.State = DadParticipantState.Ready;
        var siblingResponse = PeerCatalog(
            "client-y",
            "worker-y",
            RosterCharacter("acct-y", "Y Character@Alpha", 303, "client-y", "worker-y"));
        siblingResponse.RespondedAtUtc = now.AddMinutes(-2);
        var transport = new DadPeerTransportSnapshot
        {
            LocalClientInstanceId = "client-x",
            LocalWorkerSessionId = new DadWorkerSessionId("worker-x"),
            AuthorityWorkerSessionId = new DadWorkerSessionId("worker-w"),
            AuthorityRole = DadWorkerRole.ServerDad,
            KnownParticipants = [server],
        };

        Assert.True(DadRosterTransportCatalogRuntime.IsRosterOwnerReachable(
            new DadWorkerSessionId("worker-y"),
            "client-y",
            transport,
            [siblingResponse],
            now));
    }

    [Fact]
    public void StaleAggregateResponseDoesNotKeepRosterOwnerReachable()
    {
        var now = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);
        var staleResponse = PeerCatalog(
            "client-y",
            "worker-y",
            RosterCharacter("acct-y", "Y Character@Alpha", 303, "client-y", "worker-y"));
        staleResponse.RespondedAtUtc = now.AddMinutes(-16);
        var transport = new DadPeerTransportSnapshot
        {
            LocalClientInstanceId = "client-x",
            LocalWorkerSessionId = new DadWorkerSessionId("worker-x"),
            KnownParticipants =
            [
                new DadParticipantSnapshot
                {
                    ClientInstanceId = "client-w",
                    WorkerSessionId = new DadWorkerSessionId("worker-w"),
                    State = DadParticipantState.Ready,
                },
            ],
        };

        Assert.False(DadRosterTransportCatalogRuntime.IsRosterOwnerReachable(
            new DadWorkerSessionId("worker-y"),
            "client-y",
            transport,
            [staleResponse],
            now));
    }

    [Fact]
    public void AggregateStyleCatalogMatchesDistinctAccountScopedRows()
    {
        var now = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);
        var serverRuntime = PeerSnapshot("client-w", "worker-w", "acct-w", "Shared Character@Alpha", 303);
        var clientRuntime = PeerSnapshot("client-x", "worker-x", "acct-x", "Shared Character@Alpha", 303);
        var serverRow = RosterCharacter("acct-w", "Shared Character@Alpha", 303, "client-w", "worker-w");
        var clientRow = RosterCharacter("acct-x", "Shared Character@Alpha", 303, "client-x", "worker-x");
        var aggregateResponse = PeerCatalog("client-server", "worker-server", serverRow, clientRow);
        aggregateResponse.RespondedAtUtc = now;
        var transport = new DadPeerTransportSnapshot
        {
            LocalClientInstanceId = "client-y",
            LocalWorkerSessionId = new DadWorkerSessionId("worker-y"),
        };

        Assert.False(DadRosterTransportCatalogRuntime.ShouldUsePeerRuntimeFallback(
            serverRuntime,
            [aggregateResponse]));
        Assert.False(DadRosterTransportCatalogRuntime.ShouldUsePeerRuntimeFallback(
            clientRuntime,
            [aggregateResponse]));
        Assert.True(DadRosterTransportCatalogRuntime.IsRosterOwnerReachable(
            new DadWorkerSessionId("worker-w"),
            "client-w",
            transport,
            [aggregateResponse],
            now));
        Assert.True(DadRosterTransportCatalogRuntime.IsRosterOwnerReachable(
            new DadWorkerSessionId("worker-x"),
            "client-x",
            transport,
            [aggregateResponse],
            now));
        Assert.NotEqual(DadRosterIdentity.BuildKey(serverRow), DadRosterIdentity.BuildKey(clientRow));
    }

    [Fact]
    public void RequesterCatalogResponseFilterDoesNotMatchCoordinatorOrSiblingRows()
    {
        var requester = PeerCatalog(
            "client-x",
            "worker-x",
            RosterCharacter("acct-x", "X Character@Alpha", 202, "client-x", "worker-x"));
        var coordinator = PeerCatalog(
            "client-w",
            "worker-w",
            RosterCharacter("acct-w", "W Character@Alpha", 101, "client-w", "worker-w"));
        var sibling = PeerCatalog(
            "client-y",
            "worker-y",
            RosterCharacter("acct-y", "Y Character@Alpha", 303, "client-y", "worker-y"));

        Assert.True(DadRosterTransportCatalogRuntime.IsRequesterCatalogResponse(
            requester,
            new DadWorkerSessionId("worker-x"),
            "client-x"));
        Assert.False(DadRosterTransportCatalogRuntime.IsRequesterCatalogResponse(
            coordinator,
            new DadWorkerSessionId("worker-x"),
            "client-x"));
        Assert.False(DadRosterTransportCatalogRuntime.IsRequesterCatalogResponse(
            sibling,
            new DadWorkerSessionId("worker-x"),
            "client-x"));
    }

    [Fact]
    public void RequesterCatalogRowFilterRemovesOnlyRequesterRowsFromAggregateStyleCoordinatorResponse()
    {
        var aggregateStyleCoordinator = PeerCatalog(
            "client-w",
            "worker-w",
            RosterCharacter("acct-w", "W Character@Alpha", 101, "client-w", "worker-w"),
            RosterCharacter("acct-x", "X Character@Alpha", 202, "client-x", "worker-x"),
            RosterCharacter("acct-y", "Y Character@Alpha", 303, "client-y", "worker-y"));
        aggregateStyleCoordinator.Catalog.Accounts =
        [
            Account("acct-w", "client-w", "worker-w"),
            Account("acct-x", "client-x", "worker-x"),
            Account("acct-y", "client-y", "worker-y"),
        ];

        var filtered = DadRosterTransportCatalogRuntime.WithoutRequesterCatalogRows(
            aggregateStyleCoordinator,
            new DadWorkerSessionId("worker-x"),
            "client-x");

        Assert.Equal("worker-w", filtered.WorkerSessionId.Value);
        Assert.Equal(["acct-w", "acct-y"], filtered.Catalog.Characters.Select(static row => row.AccountKey.Value).Order().ToArray());
        Assert.Equal(["acct-w", "acct-y"], filtered.Catalog.Accounts.Select(static account => account.AccountKey.Value).Order().ToArray());
    }

    [Fact]
    public void RosterOwnerReachabilityDoesNotRequireDiscoveryDirectory()
    {
        var transport = new DadPeerTransportSnapshot
        {
            LocalClientInstanceId = "client-x",
            LocalWorkerSessionId = new DadWorkerSessionId("worker-x"),
            KnownParticipants =
            [
                Snapshot("client-w", "worker-w", "acct-w", "W Character@Alpha", 101),
            ],
        };

        Assert.True(DadRosterTransportCatalogRuntime.IsRosterOwnerReachable(
            new DadWorkerSessionId("worker-w"),
            "client-w",
            transport,
            []));
    }

    [Fact]
    public void DistinctAccountScopedServerAndClientFallbackRowsRemainDistinct()
    {
        var server = Snapshot("client-w", "worker-w", "acct-w", "Shared Character@Alpha", 303);
        server.IsLocalClient = true;
        server.State = DadParticipantState.Ready;
        var client = Snapshot("client-x", "worker-x", "acct-x", "Shared Character@Alpha", 303);
        client.State = DadParticipantState.Ready;
        var transport = Transport("client-w", "worker-w", server, client);

        var rows = DadRosterTransportCatalogRuntime.BuildParticipantRuntimeFallbackRows(
            transport,
            []);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(DadRosterIdentity.BuildKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(rows, static row => row.AccountKey.Value == "acct-w" &&
                                            row.Source == DadCharacterSource.LocalRuntime);
        Assert.Contains(rows, static row => row.AccountKey.Value == "acct-x" &&
                                            row.Source == DadCharacterSource.PeerRuntime);
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

    private static DadPeerTransportSnapshot Transport(
        string localClientId,
        string localWorkerId,
        params DadParticipantSnapshot[] participants)
        => new()
        {
            LocalClientInstanceId = localClientId,
            LocalWorkerSessionId = new DadWorkerSessionId(localWorkerId),
            KnownParticipants = participants.Select(static participant => participant.Clone()).ToList(),
            LastResponses = participants.Select(RuntimeResponse).ToList(),
        };

    private static DadPeerSnapshotResponse RuntimeResponse(DadParticipantSnapshot participant)
        => new()
        {
            ClientInstanceId = participant.ClientInstanceId,
            RespondedAtUtc = participant.LastHeartbeatUtc,
            ProcessId = participant.ProcessId,
            Character = participant.Character.Clone(),
            Participant = participant.Clone(),
            XadbReady = participant.Character.XadbReady,
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

    private static DadRosterAccountOption Account(
        string accountId,
        string clientId,
        string workerId)
        => new()
        {
            AccountKey = new DadAccountKey(accountId),
            AccountAlias = $"Dad {accountId}",
            DisplayName = $"Dad {accountId}",
            SourceClientInstanceId = clientId,
            SourceWorkerSessionId = new DadWorkerSessionId(workerId),
            OwnerOnline = true,
            AssignedCharacterCount = 1,
        };
}
