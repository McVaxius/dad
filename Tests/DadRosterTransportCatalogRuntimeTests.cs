using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRosterTransportCatalogRuntimeTests
{
    [Fact]
    public void ProducerSideIsLocalFlagCannotMakePeerLocalOnCoordinator()
    {
        var venat = Snapshot("client-w", "worker-w", "acct-w", "Venat@Alpha", 101);
        venat.IsLocalClient = true;
        venat.State = DadParticipantState.Ready;
        var xCharacter = Snapshot("client-x", "worker-x", "acct-x", "X Character@Alpha", 202);
        xCharacter.IsLocalClient = true;
        xCharacter.State = DadParticipantState.Ready;

        var catalog = DadRosterTransportCatalogRuntime.BuildLiveConnectedCatalog(
            Transport("client-w", "worker-w", xCharacter, venat));

        var local = Assert.Single(catalog.Characters, static row =>
            row.Source == DadCharacterSource.LocalRuntime);
        Assert.Equal("Venat@Alpha", local.CharacterKey.Value);
        var peer = Assert.Single(catalog.Characters, static row =>
            row.Source == DadCharacterSource.PeerRuntime);
        Assert.Equal("X Character@Alpha", peer.CharacterKey.Value);
    }

    [Fact]
    public void PositiveStatusTextAndHistoricalWarningsDoNotBlockHealthyPeer()
    {
        var participant = Snapshot("client-x", "worker-x", "acct-x", "X Character@Alpha", 202);
        participant.State = DadParticipantState.Ready;
        participant.IsEligibleForRun = true;
        participant.AuthorityMode = DadAuthorityMode.ServerDad;
        participant.StatusText = "Idle on X Character@Alpha.";
        participant.Warnings = ["Waiting for required character Old Character@Alpha."];

        var row = Assert.Single(DadRosterTransportCatalogRuntime.BuildLiveConnectedCatalog(
            Transport("client-w", "worker-w", participant)).Characters);

        Assert.Empty(row.Blockers);
        Assert.Contains(participant.Warnings[0], row.Warnings);
    }

    [Fact]
    public void StructuredStaleUnavailableLocalOnlyAndBlockedPeersFailClosed()
    {
        var stale = Snapshot("client-stale", "worker-stale", "acct-stale", "Stale@Alpha", 101);
        stale.State = DadParticipantState.Stale;
        var unavailable = Snapshot("client-off", "worker-off", "acct-off", "Off@Alpha", 102);
        unavailable.State = DadParticipantState.Ready;
        unavailable.IsAvailable = false;
        var localOnly = Snapshot("client-local", "worker-local", "acct-local", "Local@Alpha", 103);
        localOnly.State = DadParticipantState.Ready;
        localOnly.AuthorityMode = DadAuthorityMode.LocalOnly;
        var blocked = Snapshot("client-blocked", "worker-blocked", "acct-blocked", "Blocked@Alpha", 104);
        blocked.State = DadParticipantState.Ready;
        blocked.Character.Readiness = DadReadinessState.Blocked;
        blocked.Character.Blockers = ["Content ID unavailable."];

        var runtime = DadRosterTransportCatalogRuntime.BuildLiveConnectedCatalog(
            Transport("client-w", "worker-w", stale, unavailable, localOnly, blocked));

        Assert.DoesNotContain(runtime.Characters, static row => row.AccountKey.Value == "acct-stale");
        Assert.Contains(runtime.Characters, static row => row.AccountKey.Value == "acct-off" &&
                                                          row.Blockers.Any(blocker => blocker.Contains("unavailable", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(runtime.Characters, static row => row.AccountKey.Value == "acct-local" &&
                                                          row.Blockers.Any(blocker => blocker.Contains("local-only", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(runtime.Characters, static row => row.AccountKey.Value == "acct-blocked" &&
                                                          row.Blockers.Contains("Content ID unavailable."));

        var cached = new DadAccountRosterCatalog
        {
            Characters =
            [
                RosterCharacter("acct-stale", "Stale@Alpha", 101, "client-stale", "worker-stale"),
            ],
        };
        DadRosterTransportCatalogRuntime.ApplyCurrentRuntimeCoverage(cached, runtime);
        Assert.Contains("No current live Dad heartbeat for roster character.", cached.Characters[0].Blockers);
    }

    [Fact]
    public void SupersedingRuntimeReplacesOfflineBlockersThenReappliesOperatorPolicy()
    {
        var cached = RosterCharacter("acct-x", "X Character@Alpha", 202, "client-x", "worker-x");
        cached.Source = DadCharacterSource.XadbOnly;
        cached.IsCurrent = false;
        cached.Blockers = ["No live peer connection.", "Owning Client Dad is offline."];
        var live = RosterCharacter("acct-x", "X Character@Alpha", 202, "client-x", "worker-x");
        live.Source = DadCharacterSource.PeerRuntime;
        live.IsCurrent = true;
        live.Blockers = [];

        Assert.True(DadRosterTransportCatalogRuntime.ReplaceSourceBlockersFromSupersedingRuntime(cached, live));
        DadRosterTransportCatalogRuntime.ApplyOperatorPlanningPolicy(
            cached,
            DadRosterVisibility.Ignored,
            needsRosterUpdate: true);

        Assert.DoesNotContain(cached.Blockers, blocker => blocker.Contains("offline", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Ignored by operator.", cached.Blockers);
        Assert.Contains("Needs roster refresh before normal planning.", cached.Blockers);
    }

    [Fact]
    public void ConnectedDadsRefreshPlanUsesLiveParticipantProjection()
    {
        var plan = DadRosterRefreshPlan.ConnectedDads("manual connected roster refresh");

        Assert.False(plan.ForcePeerRefresh);
        Assert.True(plan.LiveConnectedOnly);
        Assert.True(plan.IncludeHidden);
        Assert.True(plan.IncludeIgnored);
        Assert.True(plan.LogDiagnostics);
        Assert.Equal("manual connected roster refresh", plan.DiagnosticsReason);
    }

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
    public void PeerRuntimeAccountMismatchCannotBuildDurableFallbackRow()
    {
        var peer = Snapshot("client-x", "worker-x", "account-managed", "X Character@Alpha", 202);
        peer.State = DadParticipantState.Ready;
        peer.Character.AccountId = "account-stale";
        var transport = Transport("client-w", "worker-w", peer);

        var rows = DadRosterTransportCatalogRuntime.BuildParticipantRuntimeFallbackRows(
            transport,
            []);

        Assert.Empty(rows);
        Assert.False(DadRosterTransportCatalogRuntime.HasExactManagedAccountBinding(
            peer,
            peer.Character));
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

    [Fact]
    public void LiveConnectedCatalogFromServerSnapshotKeepsCoordinatorAndFiveF2PClients()
    {
        var participants = new List<DadParticipantSnapshot>
        {
            Snapshot("client-w", "worker-w", "acct-w", "W Character@Alpha", 101),
            Snapshot("client-x", "worker-x", "acct-x", "X Character@Alpha", 202),
        };
        participants[0].IsLocalClient = true;
        participants[0].IsAuthority = true;
        participants[0].WorkerRole = DadWorkerRole.ServerDad;
        participants[1].WorkerRole = DadWorkerRole.ClientDad;
        for (var index = 1; index <= 5; index++)
        {
            var participant = Snapshot(
                $"client-f2p-{index}",
                $"worker-f2p-{index}",
                $"acct-f2p-{index}",
                $"F2P {index}@Alpha",
                (ulong)(300 + index));
            participant.WorkerRole = DadWorkerRole.ClientDad;
            participants.Add(participant);
        }

        foreach (var participant in participants)
            participant.State = DadParticipantState.Ready;

        var catalog = DadRosterTransportCatalogRuntime.BuildLiveConnectedCatalog(
            Transport("client-w", "worker-w", participants.ToArray()));

        Assert.True(catalog.IsLiveConnectedCatalog);
        Assert.Equal(7, catalog.Characters.Count);
        Assert.Equal(7, catalog.Accounts.Count);
        Assert.Contains(catalog.Characters, static row => row.AccountKey.Value == "acct-w" &&
                                                          row.SourceWorkerSessionId.Value == "worker-w" &&
                                                          row.Source == DadCharacterSource.LocalRuntime);
        Assert.Equal(
            ["acct-f2p-1", "acct-f2p-2", "acct-f2p-3", "acct-f2p-4", "acct-f2p-5", "acct-w", "acct-x"],
            catalog.Characters.Select(static row => row.AccountKey.Value).Order().ToArray());
    }

    [Fact]
    public void LiveConnectedCatalogFromClientSnapshotKeepsSelfCoordinatorAndSiblings()
    {
        var local = Snapshot("client-x", "worker-x", "acct-x", "X Character@Alpha", 202);
        local.IsLocalClient = true;
        local.WorkerRole = DadWorkerRole.ClientDad;
        var coordinator = Snapshot("client-w", "worker-w", "acct-w", "W Character@Alpha", 101);
        coordinator.IsAuthority = true;
        coordinator.WorkerRole = DadWorkerRole.ServerDad;
        var siblingY = Snapshot("client-y", "worker-y", "acct-y", "Y Character@Alpha", 303);
        siblingY.WorkerRole = DadWorkerRole.ClientDad;
        var siblingT = Snapshot("client-t", "worker-t", "acct-t", "T Character@Alpha", 404);
        siblingT.WorkerRole = DadWorkerRole.ClientDad;
        foreach (var participant in new[] { local, coordinator, siblingY, siblingT })
            participant.State = DadParticipantState.Ready;

        var catalog = DadRosterTransportCatalogRuntime.BuildLiveConnectedCatalog(
            Transport("client-x", "worker-x", local, coordinator, siblingY, siblingT));

        Assert.Equal(["acct-t", "acct-w", "acct-x", "acct-y"], catalog.Characters.Select(static row => row.AccountKey.Value).Order().ToArray());
        Assert.Contains(catalog.Accounts, static account => account.AccountKey.Value == "acct-x" && account.IsLocal);
        Assert.Contains(catalog.Characters, static row => row.AccountKey.Value == "acct-w" &&
                                                          row.SourceWorkerSessionId.Value == "worker-w" &&
                                                          row.Source == DadCharacterSource.PeerRuntime);
        Assert.Contains(catalog.Characters, static row => row.AccountKey.Value == "acct-y" &&
                                                          row.SourceWorkerSessionId.Value == "worker-y");
        Assert.Contains(catalog.Characters, static row => row.AccountKey.Value == "acct-t" &&
                                                          row.SourceWorkerSessionId.Value == "worker-t");
    }

    [Fact]
    public void LiveConnectedCatalogSuppressesMirroredLocalWorker()
    {
        var local = Snapshot("client-x", "worker-x", "acct-x", "X Character@Alpha", 202);
        local.IsLocalClient = true;
        local.State = DadParticipantState.Ready;
        var mirror = local.Clone();
        mirror.IsLocalClient = false;
        mirror.StatusText = "mirrored local projection";

        var catalog = DadRosterTransportCatalogRuntime.BuildLiveConnectedCatalog(
            Transport("client-x", "worker-x", mirror, local));

        var row = Assert.Single(catalog.Characters);
        Assert.Equal("worker-x", row.SourceWorkerSessionId.Value);
        Assert.Equal(DadCharacterSource.LocalRuntime, row.Source);
    }

    [Fact]
    public void LiveConnectedCatalogPreservesDistinctRemoteWorkerSessions()
    {
        var local = Snapshot("client-local", "worker-local", "acct-local", "Local@Alpha", 101);
        local.IsLocalClient = true;
        local.State = DadParticipantState.Ready;
        var remoteOne = Snapshot("shared-client", "worker-one", "acct-one", "One@Alpha", 201);
        var remoteTwo = Snapshot("shared-client", "worker-two", "acct-two", "Two@Alpha", 202);
        remoteOne.State = DadParticipantState.Ready;
        remoteTwo.State = DadParticipantState.Ready;

        var catalog = DadRosterTransportCatalogRuntime.BuildLiveConnectedCatalog(
            Transport("client-local", "worker-local", local, remoteOne, remoteTwo));

        Assert.Equal(3, catalog.Characters.Count);
        Assert.Contains(catalog.Characters, static row => row.SourceWorkerSessionId.Value == "worker-one");
        Assert.Contains(catalog.Characters, static row => row.SourceWorkerSessionId.Value == "worker-two");
    }

    [Fact]
    public void LiveConnectedCatalogRequiresRealManagedAccountKey()
    {
        var aliasOnly = Snapshot("client-x", "worker-x", "acct-x", "X Character@Alpha", 202);
        aliasOnly.ManagedAccountKey = new DadAccountKey(string.Empty);
        aliasOnly.ManagedAccountAlias = "Alias Only";
        aliasOnly.Character.AccountId = string.Empty;
        aliasOnly.Character.AccountAlias = "Alias Only";
        aliasOnly.State = DadParticipantState.Ready;

        var catalog = DadRosterTransportCatalogRuntime.BuildLiveConnectedCatalog(
            Transport("client-w", "worker-w", aliasOnly));

        Assert.Empty(catalog.Characters);
        Assert.Empty(catalog.Accounts);
    }

    [Fact]
    public void LiveConnectedCatalogAccountOptionsComeOnlyFromUsableLiveRows()
    {
        var valid = Snapshot("client-x", "worker-x", "acct-x", "X Character@Alpha", 202);
        valid.State = DadParticipantState.Ready;
        var noCurrentCharacter = Snapshot("client-config", "worker-config", "acct-config", "Unknown", 0);
        noCurrentCharacter.State = DadParticipantState.Ready;
        noCurrentCharacter.ActiveCharacterKey = new DadCharacterKey("Unknown");
        noCurrentCharacter.Character.CharacterKey = "Unknown";
        noCurrentCharacter.Character.ContentId = 0;
        noCurrentCharacter.Character.CharacterName = string.Empty;
        noCurrentCharacter.Character.WorldName = string.Empty;

        var catalog = DadRosterTransportCatalogRuntime.BuildLiveConnectedCatalog(
            Transport("client-w", "worker-w", valid, noCurrentCharacter));

        var account = Assert.Single(catalog.Accounts);
        Assert.Equal("acct-x", account.AccountKey.Value);
        Assert.Equal(Assert.Single(catalog.Characters).AccountKey.Value, account.AccountKey.Value);
        Assert.DoesNotContain(catalog.Accounts, static option => option.AccountKey.Value == "acct-config");
    }

    [Fact]
    public void LiveConnectedCatalogExcludesStaleParticipants()
    {
        var live = Snapshot("client-x", "worker-x", "acct-x", "X Character@Alpha", 202);
        live.State = DadParticipantState.Ready;
        var stale = Snapshot("client-y", "worker-y", "acct-y", "Y Character@Alpha", 303);
        stale.State = DadParticipantState.Stale;

        var catalog = DadRosterTransportCatalogRuntime.BuildLiveConnectedCatalog(
            Transport("client-w", "worker-w", live, stale));

        Assert.Equal(["acct-x"], catalog.Characters.Select(static row => row.AccountKey.Value).ToArray());
        Assert.Equal(["acct-x"], catalog.Accounts.Select(static account => account.AccountKey.Value).ToArray());
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
            IsEligibleForRun = true,
            AuthorityMode = DadAuthorityMode.ServerDad,
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
