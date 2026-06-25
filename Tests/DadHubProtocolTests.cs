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
            registry.Register(hello.SourceWorkerSessionId, new TestSession());

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
    }

    [Fact]
    public void ProtocolMismatchFailsClearly()
    {
        var frame = DadHubProtocol.CreateFrame(
            DadHubFrameKind.Hello,
            new DadWorkerSessionId("client-x"),
            new DadWorkerSessionId(string.Empty),
            "hello",
            "corr",
            "{}",
            string.Empty);
        frame.ProtocolVersion++;

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
    public void LegacyTransportModeAliasRetainsNumericValue()
    {
#pragma warning disable CS0618
        Assert.Equal((int)DadTransportMode.ServerHub, (int)DadTransportMode.LocalhostHybrid);
#pragma warning restore CS0618
    }

    private static async Task<DadHubFrame> RoundTripAsync(DadHubFrame frame)
    {
        await using var stream = new MemoryStream();
        await DadHubProtocol.WriteFrameAsync(stream, frame, CancellationToken.None);
        stream.Position = 0;
        return Assert.IsType<DadHubFrame>(
            await DadHubProtocol.ReadFrameAsync(stream, CancellationToken.None));
    }

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
