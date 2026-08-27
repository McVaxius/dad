using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadHubProtocolTests
{
    [Fact]
    public async Task RegistrationAndHeartbeatKeepOneWorkerSession()
    {
        var registry = new DadHubSessionRegistry<TestSession>();
        var worker = new DadWorkerSessionId("worker-x");
        var session = new TestSession { HeartbeatUtc = DateTime.UtcNow.AddSeconds(-2) };

        Assert.Null(registry.Register(worker, session));
        session.HeartbeatUtc = DateTime.UtcNow;

        Assert.Equal(1, registry.Count);
        Assert.Same(session, Assert.Single(registry.Snapshot()));
    }

    [Fact]
    public async Task PersistentWireCarriesHelloHeartbeatAndCorrelatedRosterResponse()
    {
        const string secret = "wire-secret";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var registry = new DadHubSessionRegistry<TestSession>();

        var serverTask = Task.Run(async () =>
        {
            using var serverClient = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = serverClient.GetStream();

            var hello = Assert.IsType<DadHubFrame>(
                await DadHubProtocol.ReadFrameAsync(stream, timeout.Token));
            DadHubProtocol.ValidateFrame(hello, secret);
            Assert.Equal(DadHubFrameKind.Hello, hello.Kind);
            var handshake = new DadHubHandshakeState(DadHubHandshakeRole.Server);
            Assert.Equal(0, registry.Count);
            Assert.False(DadHubTransportRouting.IsRoutable(socketOpen: true, handshake));

            await DadHubProtocol.WriteFrameAsync(
                stream,
                DadHubProtocol.CreateFrame(
                    DadHubFrameKind.HelloAck,
                    new DadWorkerSessionId("server-w"),
                    hello.SourceWorkerSessionId,
                    "hello",
                    hello.CorrelationId,
                    DadIpcJson.Serialize(new DadHubHello
                    {
                        WorkerSessionId = new DadWorkerSessionId("server-w"),
                    }),
                    secret),
                timeout.Token);
            handshake.MarkReadyAfterHelloAck();
            registry.Register(hello.SourceWorkerSessionId, new TestSession());

            var heartbeat = Assert.IsType<DadHubFrame>(
                await DadHubProtocol.ReadFrameAsync(stream, timeout.Token));
            DadHubProtocol.ValidateFrame(heartbeat, secret);
            Assert.Equal(DadHubFrameKind.Heartbeat, heartbeat.Kind);

            var request = Assert.IsType<DadHubFrame>(
                await DadHubProtocol.ReadFrameAsync(stream, timeout.Token));
            DadHubProtocol.ValidateFrame(request, secret);
            Assert.Equal("roster-catalog-request", request.MessageType);

            await DadHubProtocol.WriteFrameAsync(
                stream,
                DadHubProtocol.CreateFrame(
                    DadHubFrameKind.Response,
                    new DadWorkerSessionId("server-w"),
                    request.SourceWorkerSessionId,
                    request.MessageType,
                    request.CorrelationId,
                    DadIpcJson.Serialize(new DadPeerRosterCatalogResponse
                    {
                        WorkerSessionId = request.SourceWorkerSessionId,
                        Catalog = new DadAccountRosterCatalog
                        {
                            IsFullRosterAvailable = true,
                            Characters = [new DadRosterCharacter { CharacterName = "X Character" }],
                        },
                    }),
                    secret),
                timeout.Token);
        }, timeout.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await using var clientStream = client.GetStream();
        var clientWorker = new DadWorkerSessionId("client-x");
        await DadHubProtocol.WriteFrameAsync(
            clientStream,
            DadHubProtocol.CreateFrame(
                DadHubFrameKind.Hello,
                clientWorker,
                new DadWorkerSessionId(string.Empty),
                "hello",
                "hello-correlation",
                DadIpcJson.Serialize(new DadHubHello { WorkerSessionId = clientWorker }),
                secret),
            timeout.Token);

        var helloAck = Assert.IsType<DadHubFrame>(
            await DadHubProtocol.ReadFrameAsync(clientStream, timeout.Token));
        DadHubProtocol.ValidateFrame(helloAck, secret);
        Assert.Equal(DadHubFrameKind.HelloAck, helloAck.Kind);
        Assert.Equal("hello-correlation", helloAck.CorrelationId);
        Assert.Equal("server-w", helloAck.SourceWorkerSessionId.Value);
        Assert.Equal("client-x", helloAck.TargetWorkerSessionId.Value);

        await DadHubProtocol.WriteFrameAsync(
            clientStream,
            DadHubProtocol.CreateFrame(
                DadHubFrameKind.Heartbeat,
                clientWorker,
                new DadWorkerSessionId("server-w"),
                "heartbeat",
                string.Empty,
                DadIpcJson.Serialize(new DadHubHeartbeat
                {
                    Participant = new DadParticipantSnapshot { WorkerSessionId = clientWorker },
                }),
                secret),
            timeout.Token);

        await DadHubProtocol.WriteFrameAsync(
            clientStream,
            DadHubProtocol.CreateFrame(
                DadHubFrameKind.Request,
                clientWorker,
                new DadWorkerSessionId("server-w"),
                "roster-catalog-request",
                "roster-correlation",
                DadIpcJson.Serialize(new DadRosterRefreshPlan()),
                secret),
            timeout.Token);

        var response = Assert.IsType<DadHubFrame>(
            await DadHubProtocol.ReadFrameAsync(clientStream, timeout.Token));
        DadHubProtocol.ValidateFrame(response, secret);
        var roster = Assert.IsType<DadPeerRosterCatalogResponse>(
            DadIpcJson.Deserialize<DadPeerRosterCatalogResponse>(response.PayloadJson));

        Assert.Equal("roster-correlation", response.CorrelationId);
        Assert.True(roster.Catalog.IsFullRosterAvailable);
        Assert.Equal("X Character", Assert.Single(roster.Catalog.Characters).CharacterName);
        Assert.Equal(1, registry.Count);

        await serverTask;
        listener.Stop();
    }

    [Fact]
    public void DuplicateSessionReplacesOldSessionWithoutDuplicate()
    {
        var registry = new DadHubSessionRegistry<TestSession>();
        var worker = new DadWorkerSessionId("worker-x");
        var oldSession = new TestSession();
        var newSession = new TestSession();

        Assert.Null(registry.Register(worker, oldSession));
        Assert.Same(oldSession, registry.Register(worker, newSession));

        Assert.Equal(1, registry.Count);
        Assert.Same(newSession, Assert.Single(registry.Snapshot()));
        Assert.False(registry.RemoveIfCurrent(worker, oldSession));
        Assert.True(registry.RemoveIfCurrent(worker, newSession));
    }

    [Fact]
    public async Task CorrelatedFrameRoundTripsWorkerSessionRoute()
    {
        var frame = DadHubProtocol.CreateFrame(
            DadHubFrameKind.Request,
            new DadWorkerSessionId("server-w"),
            new DadWorkerSessionId("client-x"),
            "roster-catalog-request",
            "corr-123",
            DadIpcJson.Serialize(new DadRosterRefreshPlan { ForcePeerRefresh = true }),
            "shared-secret");

        var roundTrip = await RoundTripAsync(frame);

        Assert.Equal(DadHubProtocol.CurrentVersion, roundTrip.ProtocolVersion);
        Assert.Equal("server-w", roundTrip.SourceWorkerSessionId.Value);
        Assert.Equal("client-x", roundTrip.TargetWorkerSessionId.Value);
        Assert.Equal("corr-123", roundTrip.CorrelationId);
        Assert.Equal("roster-catalog-request", roundTrip.MessageType);
        DadHubProtocol.ValidateFrame(roundTrip, "shared-secret");
    }

    [Fact]
    public async Task NotificationFrameCarriesHubRosterPublish()
    {
        var publish = new DadHubRosterPublish
        {
            Generation = 7,
            AuthorityEpochId = "epoch-w-1",
            PublishedAtUtc = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc),
            AuthorityEndpoint = "127.0.0.1:4647",
            AuthorityWorkerSessionId = new DadWorkerSessionId("worker-w"),
            CoordinatorParticipant = Participant("client-w", "worker-w", "acct-w", "W Character@Alpha", isAuthority: true),
            ClientParticipants =
            [
                Participant("client-x", "worker-x", "acct-x", "X Character@Alpha"),
            ],
        };
        publish.Participants = [publish.CoordinatorParticipant, ..publish.ClientParticipants];

        var frame = DadHubProtocol.CreateFrame(
            DadHubFrameKind.Notification,
            new DadWorkerSessionId("worker-w"),
            new DadWorkerSessionId("worker-x"),
            "hub-roster-publish",
            string.Empty,
            DadIpcJson.Serialize(publish),
            "shared-secret");

        var roundTrip = await RoundTripAsync(frame);
        var payload = Assert.IsType<DadHubRosterPublish>(
            DadIpcJson.Deserialize<DadHubRosterPublish>(roundTrip.PayloadJson));

        Assert.Equal(DadHubFrameKind.Notification, roundTrip.Kind);
        Assert.Equal(7, payload.Generation);
        Assert.Equal("epoch-w-1", payload.AuthorityEpochId);
        Assert.Equal("worker-w", payload.AuthorityWorkerSessionId.Value);
        Assert.Equal(["worker-w", "worker-x"], payload.Participants.Select(static participant => participant.WorkerSessionId.Value).ToArray());
        DadHubProtocol.ValidateFrame(roundTrip, "shared-secret");
    }

    [Fact]
    public void PublishedRosterConvergesAcrossClients()
    {
        var publish = new DadHubRosterPublish
        {
            Generation = 8,
            AuthorityEpochId = "epoch-w-1",
            PublishedAtUtc = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc),
            AuthorityEndpoint = "127.0.0.1:4647",
            AuthorityWorkerSessionId = new DadWorkerSessionId("worker-w"),
            CoordinatorParticipant = Participant("client-w", "worker-w", "acct-w", "W Character@Alpha", isAuthority: true),
            ClientParticipants =
            [
                Participant("client-x", "worker-x", "acct-x", "X Character@Alpha"),
                Participant("client-y", "worker-y", "acct-y", "Y Character@Alpha"),
                Participant("client-z", "worker-z", "acct-z", "Z Character@Alpha"),
            ],
        };
        publish.Participants = [publish.CoordinatorParticipant, ..publish.ClientParticipants];

        var xView = DadHubRosterPublishRuntime.BuildParticipantView(
            publish,
            Participant("client-x", "worker-x", "acct-x", "X Character@Alpha"),
            new DadWorkerSessionId("worker-x"),
            "client-x");
        var yView = DadHubRosterPublishRuntime.BuildParticipantView(
            publish,
            Participant("client-y", "worker-y", "acct-y", "Y Character@Alpha"),
            new DadWorkerSessionId("worker-y"),
            "client-y");

        Assert.Equal(
            xView.Select(static participant => participant.WorkerSessionId.Value).Order().ToArray(),
            yView.Select(static participant => participant.WorkerSessionId.Value).Order().ToArray());
        Assert.Equal(4, DadHubRosterPublishRuntime.CountPublishedParticipants(publish));
        Assert.Equal(["acct-w", "acct-x", "acct-y", "acct-z"], xView.Select(static participant => participant.ManagedAccountKey.Value).Order().ToArray());
        Assert.Contains(xView, static participant => participant.WorkerSessionId.Value == "worker-w" && participant.IsAuthority);
        Assert.Contains(xView, static participant => participant.WorkerSessionId.Value == "worker-x" && participant.IsLocalClient);
        Assert.Contains(yView, static participant => participant.WorkerSessionId.Value == "worker-y" && participant.IsLocalClient);
    }

    [Fact]
    public void StaleHubRosterPublishIsDetectable()
    {
        var now = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);
        var publish = new DadHubRosterPublish
        {
            Generation = 9,
            PublishedAtUtc = now.AddSeconds(-20),
        };

        Assert.False(DadHubRosterPublishRuntime.IsFresh(publish, now, TimeSpan.FromSeconds(12)));
        Assert.True(DadHubRosterPublishRuntime.IsFresh(publish, now, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void SameAuthorityEpochStalePublishGenerationIsIgnored()
    {
        var cursor = DadHubRosterPublishCursor.FromPublish(Publish("worker-w", "epoch-w-1", 12));

        Assert.False(DadHubRosterPublishCursor.ShouldApply(Publish("worker-w", "epoch-w-1", 11), cursor));
        Assert.False(DadHubRosterPublishCursor.ShouldApply(Publish("worker-w", "epoch-w-1", 12), cursor));
        Assert.True(DadHubRosterPublishCursor.ShouldApply(Publish("worker-w", "epoch-w-1", 13), cursor));
    }

    [Fact]
    public void NewAuthorityEpochAcceptsLowerPublishGeneration()
    {
        var cursor = DadHubRosterPublishCursor.FromPublish(Publish("worker-w", "epoch-w-1", 12));

        Assert.True(DadHubRosterPublishCursor.ShouldApply(Publish("worker-w", "epoch-w-2", 1), cursor));
    }

    [Fact]
    public void NewAuthorityWorkerAcceptsLowerPublishGeneration()
    {
        var cursor = DadHubRosterPublishCursor.FromPublish(Publish("worker-w", "epoch-w-1", 12));

        Assert.True(DadHubRosterPublishCursor.ShouldApply(Publish("worker-w-reloaded", "epoch-w-1", 1), cursor));
    }

    [Theory]
    [InlineData("profile-catalog-request")]
    [InlineData("profile-update-command")]
    [InlineData("wake-request")]
    [InlineData("claim-request")]
    [InlineData("assembly-instruction")]
    [InlineData("worker-execution-command")]
    [InlineData("worker-execution-status")]
    [InlineData("worker-execution-cancel")]
    [InlineData("cancel-run")]
    [InlineData("stop-all")]
    public async Task RoutedRequestTypesPreserveCorrelation(string messageType)
    {
        var frame = DadHubProtocol.CreateFrame(
            DadHubFrameKind.Request,
            new DadWorkerSessionId("server-w"),
            new DadWorkerSessionId("client-x"),
            messageType,
            Guid.NewGuid().ToString("N"),
            "{}",
            string.Empty);

        var roundTrip = await RoundTripAsync(frame);

        Assert.Equal(messageType, roundTrip.MessageType);
        Assert.Equal(frame.CorrelationId, roundTrip.CorrelationId);
        Assert.Equal("client-x", roundTrip.TargetWorkerSessionId.Value);
    }

    [Fact]
    public void WrongSecretFailsClearly()
    {
        var frame = DadHubProtocol.CreateFrame(
            DadHubFrameKind.Hello,
            new DadWorkerSessionId("client-x"),
            new DadWorkerSessionId(string.Empty),
            "hello",
            "corr",
            "{}",
            "correct-secret");

        var error = Assert.Throws<DadHubProtocolException>(
            () => DadHubProtocol.ValidateFrame(frame, "wrong-secret"));

        Assert.Equal("authentication-failed", error.Code);
        Assert.Equal("Shared secret mismatch", error.Message);
    }

    [Fact]
    public void MatchingSecretValidates()
    {
        var frame = DadHubProtocol.CreateFrame(
            DadHubFrameKind.Hello,
            new DadWorkerSessionId("client-x"),
            new DadWorkerSessionId(string.Empty),
            "hello",
            "corr",
            "{}",
            "correct-secret");

        DadHubProtocol.ValidateFrame(frame, "correct-secret");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void ProtocolMismatchFailsClearly(int versionOffset)
    {
        var frame = DadHubProtocol.CreateFrame(
            DadHubFrameKind.Hello,
            new DadWorkerSessionId("client-x"),
            new DadWorkerSessionId(string.Empty),
            "hello",
            "corr",
            "{}",
            string.Empty);
        frame.ProtocolVersion = DadHubProtocol.CurrentVersion + versionOffset;

        var error = Assert.Throws<DadHubProtocolException>(
            () => DadHubProtocol.ValidateFrame(frame, string.Empty));

        Assert.Equal("protocol-mismatch", error.Code);
        Assert.Contains(DadHubProtocol.CurrentVersion.ToString(), error.Message);
    }

    [Fact]
    public void LanAddressRequiresSecretButLoopbackDoesNot()
    {
        Assert.False(DadHubProtocol.RequiresSharedSecret(IPAddress.Loopback));
        Assert.False(DadHubProtocol.RequiresSharedSecret(IPAddress.IPv6Loopback));
        Assert.True(DadHubProtocol.RequiresSharedSecret(IPAddress.Parse("192.168.1.25")));
    }

    [Fact]
    public void MissingLanSecretFailsClearly()
    {
        var error = Assert.Throws<DadHubProtocolException>(
            () => DadHubProtocol.RequireSharedSecretForAddress(IPAddress.Parse("192.168.1.25"), string.Empty));

        Assert.Equal("authentication-required", error.Code);
        Assert.Contains("shared secret", error.Message, StringComparison.OrdinalIgnoreCase);
        DadHubProtocol.RequireSharedSecretForAddress(IPAddress.Loopback, string.Empty);
        DadHubProtocol.RequireSharedSecretForAddress(IPAddress.Parse("192.168.1.25"), "lan-secret");
    }

    [Fact]
    public async Task OversizedOutboundFrameFailsClearly()
    {
        var frame = DadHubProtocol.CreateFrame(
            DadHubFrameKind.Request,
            new DadWorkerSessionId("server-w"),
            new DadWorkerSessionId("client-x"),
            "snapshot-request",
            "corr",
            new string('x', DadHubProtocol.MaxFrameBytes),
            string.Empty);

        var error = await Assert.ThrowsAsync<DadHubProtocolException>(
            () => DadHubProtocol.WriteFrameAsync(new MemoryStream(), frame, CancellationToken.None));

        Assert.Equal("frame-too-large", error.Code);
    }

    [Fact]
    public void SerializedFrameByteCountIncludesEscapedInnerPayload()
    {
        var payloadJson = DadIpcJson.Serialize(new { rows = Enumerable.Repeat("quoted \"value\"", 100).ToArray() });
        var frame = DadHubProtocol.CreateFrame(
            DadHubFrameKind.Notification,
            new DadWorkerSessionId("server-w"),
            new DadWorkerSessionId("client-x"),
            "hub-roster-publish",
            string.Empty,
            payloadJson,
            "shared-secret");

        Assert.True(DadHubProtocol.GetSerializedFrameByteCount(frame) > System.Text.Encoding.UTF8.GetByteCount(payloadJson));
    }

    [Fact]
    public async Task OversizedInboundFrameFailsBeforePayloadAllocation()
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, DadHubProtocol.MaxFrameBytes + 1);
        await using var stream = new MemoryStream(header);

        var error = await Assert.ThrowsAsync<DadHubProtocolException>(
            () => DadHubProtocol.ReadFrameAsync(stream, CancellationToken.None));

        Assert.Equal("frame-too-large", error.Code);
    }

    [Fact]
    public async Task ReadTimeoutCancelsClearly()
    {
        await using var stream = new BlockingReadStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DadHubProtocol.ReadFrameAsync(stream, cancellation.Token));
    }

    [Fact]
    public void StaleDisconnectedParticipantIsMarkedOffline()
    {
        var now = new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc);
        var lastHeartbeat = now.AddSeconds(-2);
        var disconnectedAt = now.AddSeconds(-8);
        var participant = new DadParticipantSnapshot
        {
            WorkerSessionId = new DadWorkerSessionId("worker-x"),
            IsLocalClient = true,
            IsAvailable = true,
            IsEligibleForRun = true,
            State = DadParticipantState.Idle,
            ClaimState = DadClaimState.Granted,
            LeaseState = DadParticipantLeaseState.Granted,
            Character = new DadAcquiredCharacter
            {
                Readiness = DadReadinessState.Ready,
            },
        };

        var stale = DadHubParticipants.PrepareRemoteWithStaleState(
            participant,
            lastHeartbeat,
            disconnectedAt,
            now,
            TimeSpan.FromSeconds(5),
            "Client Dad disconnected.");

        Assert.False(stale.IsLocalClient);
        Assert.Equal(lastHeartbeat, stale.LastHeartbeatUtc);
        Assert.Equal(DadParticipantState.Stale, stale.State);
        Assert.Equal(DadClaimState.Stale, stale.ClaimState);
        Assert.Equal(DadParticipantLeaseState.Stale, stale.LeaseState);
        Assert.False(stale.IsAvailable);
        Assert.False(stale.IsEligibleForRun);
        Assert.Equal(DadReadinessState.Stale, stale.Character.Readiness);
        Assert.Contains("Client Dad disconnected.", stale.Warnings);
    }

    [Fact]
    public void PhysicalDisconnectIsImmediatelyIneligibleForRoutingProjection()
    {
        var participant = new DadParticipantSnapshot
        {
            WorkerSessionId = new DadWorkerSessionId("worker-x"),
            IsAvailable = true,
            IsEligibleForRun = true,
            State = DadParticipantState.Idle,
        };

        DadHubParticipants.MarkDisconnected(participant, "Client Dad disconnected.");

        Assert.Equal(DadParticipantState.Stale, participant.State);
        Assert.False(participant.IsAvailable);
        Assert.False(participant.IsEligibleForRun);
        Assert.Contains("Client Dad disconnected.", participant.Warnings);
    }

    [Fact]
    public void LegacyTransportModeAliasRetainsNumericValue()
    {
#pragma warning disable CS0618
        Assert.Equal((int)DadTransportMode.ServerHub, (int)DadTransportMode.LocalhostHybrid);
#pragma warning restore CS0618
    }

    // B5: replay-resistant envelope — a freshly signed frame (valid secret + fresh nonce/timestamp) is accepted.
    [Fact]
    public void ReplayResistantEnvelopeAcceptsFreshSignedFrame()
    {
        var frame = DadHubProtocol.CreateFrame(
            DadHubFrameKind.Request,
            new DadWorkerSessionId("server-w"),
            new DadWorkerSessionId("client-x"),
            "roster-catalog-request",
            "corr-b5-accept",
            "{}",
            "b5-secret");

        Assert.False(string.IsNullOrEmpty(frame.Nonce));
        Assert.True(frame.SentAtUnixMs > 0);
        DadHubProtocol.ValidateFrame(frame, "b5-secret"); // fresh frame validates without throwing
    }

    // B5: the same nonce presented twice is rejected as a replay.
    [Fact]
    public void ReplayedNonceIsRejected()
    {
        var frame = DadHubProtocol.CreateFrame(
            DadHubFrameKind.Request,
            new DadWorkerSessionId("server-w"),
            new DadWorkerSessionId("client-x"),
            "roster-catalog-request",
            "corr-b5-replay",
            "{}",
            "b5-secret");

        DadHubProtocol.ValidateFrame(frame, "b5-secret"); // first delivery accepted

        var error = Assert.Throws<DadHubProtocolException>(
            () => DadHubProtocol.ValidateFrame(frame, "b5-secret"));
        Assert.Equal("replay-detected", error.Code);
    }

    // B5: mutating the signed payload after CreateFrame breaks the HMAC.
    [Fact]
    public void TamperedPayloadFailsAuthentication()
    {
        var frame = DadHubProtocol.CreateFrame(
            DadHubFrameKind.Request,
            new DadWorkerSessionId("server-w"),
            new DadWorkerSessionId("client-x"),
            "roster-catalog-request",
            "corr-b5-tamper",
            "{\"value\":1}",
            "b5-secret");

        frame.PayloadJson = "{\"value\":2}"; // tamper after signing

        var error = Assert.Throws<DadHubProtocolException>(
            () => DadHubProtocol.ValidateFrame(frame, "b5-secret"));
        Assert.Equal("authentication-failed", error.Code);
    }

    // B5: a correctly-signed frame whose timestamp is outside the replay window is rejected as stale.
    [Fact]
    public void StaleTimestampIsRejectedEvenWhenCorrectlySigned()
    {
        var frame = new DadHubFrame
        {
            Kind = DadHubFrameKind.Request,
            SourceWorkerSessionId = new DadWorkerSessionId("server-w"),
            TargetWorkerSessionId = new DadWorkerSessionId("client-x"),
            MessageType = "roster-catalog-request",
            CorrelationId = "corr-b5-stale",
            PayloadJson = "{}",
            Nonce = Guid.NewGuid().ToString("N"),
            SentAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),
        };
        frame.Auth = DadHubProtocol.ComputeAuth(frame, "b5-secret"); // valid HMAC over the stale timestamp

        var error = Assert.Throws<DadHubProtocolException>(
            () => DadHubProtocol.ValidateFrame(frame, "b5-secret"));
        Assert.Equal("stale-frame", error.Code);
    }

    // B5: loopback / no-secret behavior is preserved — empty secret = no auth, so replay/stale checks are skipped.
    [Fact]
    public void NoSecretFramesSkipReplayAndStaleChecks()
    {
        var frame = new DadHubFrame
        {
            Kind = DadHubFrameKind.Request,
            SourceWorkerSessionId = new DadWorkerSessionId("server-w"),
            TargetWorkerSessionId = new DadWorkerSessionId("client-x"),
            MessageType = "roster-catalog-request",
            CorrelationId = "corr-b5-loopback",
            PayloadJson = "{}",
            Nonce = string.Empty,
            SentAtUnixMs = 0, // ancient timestamp ignored on the no-secret path
        };

        DadHubProtocol.ValidateFrame(frame, string.Empty);
        DadHubProtocol.ValidateFrame(frame, string.Empty); // replay also accepted without a shared secret
    }

    [Fact]
    public void WakeTakeoverCommitDtoRoundTripsBarrierMetadata()
    {
        var execution = new DateTime(2026, 7, 10, 12, 0, 5, DateTimeKind.Utc);
        var request = new DadWakeTakeoverRequestDto
        {
            SchedulerRunId = "run",
            OperationToken = "shared-token",
            SlotId = "W",
            AccountKey = new DadAccountKey("account"),
            CharacterKey = new DadCharacterKey("Target Character@World"),
            MessageKind = DadWakeTakeoverMessageKind.Go,
            CommitKind = DadWakeCommitKind.Reset,
            ExecutionTimeUtc = execution,
        };

        var roundTrip = DadIpcJson.Deserialize<DadWakeTakeoverRequestDto>(DadIpcJson.Serialize(request));

        Assert.NotNull(roundTrip);
        Assert.Equal("shared-token", roundTrip.OperationToken);
        Assert.Equal(DadWakeTakeoverMessageKind.Go, roundTrip.MessageKind);
        Assert.Equal(DadWakeCommitKind.Reset, roundTrip.CommitKind);
        Assert.Equal(execution, roundTrip.ExecutionTimeUtc);
    }

    private static async Task<DadHubFrame> RoundTripAsync(DadHubFrame frame)
    {
        await using var stream = new MemoryStream();
        await DadHubProtocol.WriteFrameAsync(stream, frame, CancellationToken.None);
        stream.Position = 0;
        return Assert.IsType<DadHubFrame>(
            await DadHubProtocol.ReadFrameAsync(stream, CancellationToken.None));
    }

    private static DadParticipantSnapshot Participant(
        string clientId,
        string workerId,
        string accountId,
        string characterKey,
        bool isAuthority = false)
    {
        var parts = characterKey.Split('@', 2, StringSplitOptions.TrimEntries);
        return new DadParticipantSnapshot
        {
            ClientInstanceId = clientId,
            WorkerSessionId = new DadWorkerSessionId(workerId),
            ManagedAccountKey = new DadAccountKey(accountId),
            ManagedAccountAlias = $"Dad {accountId}",
            ActiveCharacterKey = new DadCharacterKey(characterKey),
            IsAvailable = true,
            IsEligibleForRun = true,
            IsAuthority = isAuthority,
            WorkerRole = isAuthority ? DadWorkerRole.ServerDad : DadWorkerRole.ClientDad,
            State = DadParticipantState.Ready,
            LastHeartbeatUtc = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc),
            Character = new DadAcquiredCharacter
            {
                AccountId = accountId,
                AccountAlias = $"Dad {accountId}",
                CharacterKey = characterKey,
                CharacterName = parts[0],
                WorldName = parts.Length == 2 ? parts[1] : string.Empty,
                Source = DadCharacterSource.LocalRuntime,
                Freshness = DadSnapshotFreshness.Live,
                Readiness = DadReadinessState.Ready,
            },
        };
    }

    private static DadHubRosterPublish Publish(string workerId, string epochId, long generation)
        => new()
        {
            Generation = generation,
            AuthorityEpochId = epochId,
            AuthorityWorkerSessionId = new DadWorkerSessionId(workerId),
            PublishedAtUtc = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc),
        };

    private sealed class TestSession
    {
        public DateTime HeartbeatUtc { get; set; }
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
