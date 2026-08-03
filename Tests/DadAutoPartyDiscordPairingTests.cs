using System.Security.Cryptography;
using System.Text.Json;
using AutoParty.Core.Cryptography;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAutoPartyDiscordPairingTests
{
    [Fact]
    public void ReconnectBackoffIsBoundedAndResettable()
    {
        var backoff = new DadDiscordReconnectBackoff();

        Assert.Equal(TimeSpan.FromSeconds(2), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(5), backoff.NextDelay());
        _ = backoff.NextDelay();
        _ = backoff.NextDelay();
        Assert.Equal(TimeSpan.FromSeconds(60), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(60), backoff.NextDelay());
        backoff.Reset();
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.NextDelay());
    }

    [Fact]
    public async Task SignedPairingEnvelopeVerifiesOnceAndContainsOnlyPublicMetadata()
    {
        var privateKey = RandomNumberGenerator.GetBytes(32);
        var publicKey = BouncyCastlePrimitives.DeriveEd25519PublicKey(privateKey);
        var package = JsonSerializer.SerializeToUtf8Bytes(new
        {
            OwnerId = "owner-test",
            IslandId = "island-test",
            KeyGeneration = 1,
            SigningPrivateKey = Convert.ToBase64String(privateKey),
            EncryptionPrivateKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        });
        var store = new MemoryIdentityStore(package);
        var configuration = new DadAutoPartyConfiguration
        {
            EndpointIdentityReference = "identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            RegisteredOwnerId = "owner-test",
            RegisteredIslandId = "island-test",
            RegistrationFingerprint = new string('A', 64),
            SigningPublicKey = Convert.ToBase64String(publicKey),
            DiscordApplicationId = 123,
            DiscordBotUserId = 456,
            DiscordBinding = new DadAutoPartyDiscordBinding { KeyGeneration = 1 },
        };
        var protocol = new DadAutoPartyPairingProtocol();
        var envelope = await protocol.CreateAsync(
            DadAutoPartyPairingMessageKind.Presence,
            DadAutoPartyRole.Client,
            configuration,
            new DadAutoPartySigningService(configuration, store));
        var serialized = DadAutoPartyPairingProtocol.Serialize(envelope);

        var accepted = protocol.Validate(envelope, 456, DateTime.UtcNow, DadAutoPartyRole.Coordinator);
        var replayed = protocol.Validate(envelope, 456, DateTime.UtcNow, DadAutoPartyRole.Coordinator);

        Assert.True(accepted.Allowed, accepted.SafeCode);
        Assert.False(replayed.Allowed);
        Assert.Equal("dad-discord-envelope-replay", replayed.SafeCode);
        Assert.DoesNotContain("token", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("character", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plan", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("schedule", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.True(serialized.Length <= DadAutoPartyPairingProtocol.MaximumEnvelopeCharacters);
        CryptographicOperations.ZeroMemory(privateKey);
        CryptographicOperations.ZeroMemory(publicKey);
        CryptographicOperations.ZeroMemory(package);
    }

    [Fact]
    public async Task PairingProtocolRejectsWrongAuthorAndStaleEnvelope()
    {
        var fixture = await SignedEnvelopeFixture.CreateAsync();
        var wrongAuthor = fixture.Protocol.Validate(fixture.Envelope, 999, DateTime.UtcNow, DadAutoPartyRole.Coordinator);
        var stale = fixture.Protocol.Validate(
            fixture.Envelope,
            fixture.Envelope.BotUserId,
            DateTime.UtcNow + TimeSpan.FromMinutes(4),
            DadAutoPartyRole.Coordinator);

        Assert.Equal("dad-discord-envelope-invalid", wrongAuthor.SafeCode);
        Assert.Equal("dad-discord-envelope-stale", stale.SafeCode);
    }

    [Fact]
    public async Task PairingProtocolRejectsUndefinedMessageKindAndRole()
    {
        var undefinedKind = await SignedEnvelopeFixture.CreateAsync();
        undefinedKind.Envelope.Kind = (DadAutoPartyPairingMessageKind)99;
        var kindDecision = undefinedKind.Protocol.Validate(
            undefinedKind.Envelope,
            undefinedKind.Envelope.BotUserId,
            DateTime.UtcNow,
            DadAutoPartyRole.Coordinator);

        var undefinedRole = await SignedEnvelopeFixture.CreateAsync();
        undefinedRole.Envelope.Role = (DadAutoPartyRole)99;
        var roleDecision = undefinedRole.Protocol.Validate(
            undefinedRole.Envelope,
            undefinedRole.Envelope.BotUserId,
            DateTime.UtcNow,
            DadAutoPartyRole.Coordinator);

        Assert.Equal("dad-discord-envelope-invalid", kindDecision.SafeCode);
        Assert.Equal("dad-discord-envelope-invalid", roleDecision.SafeCode);
    }

    [Fact]
    public async Task PairingProtocolRejectsMissingMalformedAndTamperedSignaturesWithTypedReasons()
    {
        var missingFixture = await SignedEnvelopeFixture.CreateAsync();
        missingFixture.Envelope.Signature = null!;
        var missing = missingFixture.Protocol.Validate(
            missingFixture.Envelope,
            missingFixture.Envelope.BotUserId,
            DateTime.UtcNow,
            DadAutoPartyRole.Coordinator);
        var tamperedFixture = await SignedEnvelopeFixture.CreateAsync();
        tamperedFixture.Envelope.Signature = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var tampered = tamperedFixture.Protocol.Validate(
            tamperedFixture.Envelope,
            tamperedFixture.Envelope.BotUserId,
            DateTime.UtcNow,
            DadAutoPartyRole.Coordinator);
        var malformedFixture = await SignedEnvelopeFixture.CreateAsync();
        malformedFixture.Envelope.Signature = "not-base64";
        var malformed = malformedFixture.Protocol.Validate(
            malformedFixture.Envelope,
            malformedFixture.Envelope.BotUserId,
            DateTime.UtcNow,
            DadAutoPartyRole.Coordinator);

        Assert.Equal("dad-discord-envelope-signature-missing", missing.SafeCode);
        Assert.Equal("dad-discord-envelope-signature-malformed", malformed.SafeCode);
        Assert.Equal("dad-discord-envelope-signature-invalid", tampered.SafeCode);
    }

    [Fact]
    public void InboundQueueIsBoundedAndPreservesAcceptedOrder()
    {
        var queue = new DadAutoPartyDiscordInboundQueue(capacity: 2);
        var first = Message("first");
        var second = Message("second");

        Assert.True(queue.TryEnqueue(first));
        Assert.True(queue.TryEnqueue(second));
        Assert.False(queue.TryEnqueue(Message("overflow")));
        Assert.Equal(2, queue.Count);
        Assert.True(queue.TryDequeue(out var observedFirst));
        Assert.True(queue.TryDequeue(out var observedSecond));
        Assert.Equal("first", observedFirst!.Content);
        Assert.Equal("second", observedSecond!.Content);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void InboundQueueDrainsAtMostEightFromFullCapacity()
    {
        var queue = new DadAutoPartyDiscordInboundQueue();
        for (var index = 0; index < DadAutoPartyDiscordInboundQueue.DefaultCapacity; index++)
            Assert.True(queue.TryEnqueue(Message(index.ToString())));
        Assert.False(queue.TryEnqueue(Message("overflow")));

        var observed = new List<string>();
        var drained = queue.DrainAtMost(8, message => observed.Add(message.Content));

        Assert.Equal(8, drained);
        Assert.Equal(248, queue.Count);
        Assert.Equal(Enumerable.Range(0, 8).Select(static value => value.ToString()), observed);
    }

    [Fact]
    public void RepeatedDiagnosticsAreRateLimitedBySafeCode()
    {
        var gate = new DadRateLimitedDiagnosticGate();
        var now = DateTime.UtcNow;

        Assert.True(gate.ShouldEmit("queue-full", now, TimeSpan.FromMinutes(1)));
        Assert.False(gate.ShouldEmit("queue-full", now.AddSeconds(59), TimeSpan.FromMinutes(1)));
        Assert.True(gate.ShouldEmit("invalid-signature", now.AddSeconds(1), TimeSpan.FromMinutes(1)));
        Assert.True(gate.ShouldEmit("queue-full", now.AddMinutes(1), TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, true, true, true, true)]
    public void BlockedLifecycleDecisionObservesCompletionAndSchedulesOnlyAnIdleClientStop(
        bool clientExists,
        bool lifecycleTaskExists,
        bool lifecycleTaskCompleted,
        bool expectedObserve,
        bool expectedStop)
    {
        var decision = DadAutoPartyDiscordLifecycleRules.EvaluateBlocked(
            clientExists,
            lifecycleTaskExists,
            lifecycleTaskCompleted);

        Assert.Equal(expectedObserve, decision.ObserveCompletedTask);
        Assert.Equal(expectedStop, decision.ScheduleBlockedStop);
    }

    [Fact]
    public void BlockedHealthAcceptsOnlyBlockedStateUntilExplicitReconnect()
    {
        Assert.True(DadAutoPartyDiscordLifecycleRules.CanSetHealth(
            false,
            DadAutoPartyDiscordConnectionState.Connecting));
        Assert.True(DadAutoPartyDiscordLifecycleRules.CanSetHealth(
            true,
            DadAutoPartyDiscordConnectionState.Blocked));
        Assert.False(DadAutoPartyDiscordLifecycleRules.CanSetHealth(
            true,
            DadAutoPartyDiscordConnectionState.Disconnected));
        Assert.False(DadAutoPartyDiscordLifecycleRules.CanSetHealth(
            true,
            DadAutoPartyDiscordConnectionState.Ready));
    }

    [Fact]
    public void PendingPairingIdentityMustMatchEveryPersistedPeerField()
    {
        var peer = new DadAutoPartyDiscoveredClient(
            10,
            20,
            "island-a",
            new string('A', 64),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            string.Empty,
            3,
            DadAutoPartyRole.Client,
            DateTime.UtcNow,
            DadAutoPartyPairingHealth.Unpaired,
            string.Empty);
        peer = peer with
        {
            SigningKeyFingerprint = DadAutoPartyDiscordPairingRules.ComputeSigningKeyFingerprint(
                peer.SigningPublicKey),
        };
        var pending = new DadAutoPartyPairing
        {
            OwnerId = "discord",
            IslandId = peer.DadIdentity,
            PublicKeyFingerprint = peer.EndpointFingerprint,
            SigningPublicKey = peer.SigningPublicKey,
            SigningKeyFingerprint = peer.SigningKeyFingerprint,
            KeyGeneration = peer.KeyGeneration,
            ApplicationId = peer.ApplicationId,
            BotUserId = peer.BotUserId,
            Role = peer.Role,
            ConfirmedAtUtc = DateTime.UtcNow,
        };

        Assert.True(DadAutoPartyDiscordPairingRules.MatchesPendingIdentity(pending, peer));
        Assert.False(DadAutoPartyDiscordPairingRules.MatchesPendingIdentity(
            pending,
            peer with { EndpointFingerprint = new string('B', 64) }));
        Assert.False(DadAutoPartyDiscordPairingRules.MatchesPendingIdentity(
            pending,
            peer with { SigningPublicKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
        Assert.False(DadAutoPartyDiscordPairingRules.MatchesPendingIdentity(
            pending,
            peer with { BotUserId = 21 }));
    }

    [Fact]
    public void OutboundChallengeSurvivesRestartAndBindsEveryConfirmedIdentityField()
    {
        var now = DateTime.UtcNow;
        var peer = Peer();
        var challenge = DadAutoPartyDiscordPairingRules.CreateOutboundChallenge(
            peer,
            peer.SigningKeyFingerprint,
            now);
        var configuration = new DadAutoPartyConfiguration
        {
            OutboundPairingChallenges = [challenge],
        };

        var json = JsonSerializer.Serialize(configuration);
        var restarted = JsonSerializer.Deserialize<DadAutoPartyConfiguration>(json)!.Normalize();
        var persisted = Assert.Single(restarted.OutboundPairingChallenges);

        Assert.True(DadAutoPartyDiscordPairingRules.MatchesActiveChallenge(
            persisted,
            peer,
            challenge.RequestNonce,
            now.AddSeconds(1)));
        Assert.False(DadAutoPartyDiscordPairingRules.MatchesActiveChallenge(
            persisted,
            peer with { KeyGeneration = peer.KeyGeneration + 1 },
            challenge.RequestNonce,
            now.AddSeconds(1)));
        Assert.False(DadAutoPartyDiscordPairingRules.MatchesActiveChallenge(
            persisted,
            peer with { BotUserId = peer.BotUserId + 1 },
            challenge.RequestNonce,
            now.AddSeconds(1)));
        Assert.False(DadAutoPartyDiscordPairingRules.MatchesActiveChallenge(
            persisted,
            peer with { DadIdentity = "other-island" },
            challenge.RequestNonce,
            now.AddSeconds(1)));
    }

    [Fact]
    public void OutboundChallengeExpiresAndCanAuthorizeAtMostOneAcceptance()
    {
        var now = DateTime.UtcNow;
        var peer = Peer();
        var challenge = DadAutoPartyDiscordPairingRules.CreateOutboundChallenge(
            peer,
            peer.SigningKeyFingerprint,
            now);
        var challenges = new List<DadAutoPartyOutboundPairingChallenge> { challenge };

        Assert.True(DadAutoPartyDiscordPairingRules.MatchesActiveChallenge(
            challenge,
            peer,
            challenge.RequestNonce,
            now.AddSeconds(1)));
        Assert.Equal(1, challenges.RemoveAll(item => item.RequestNonce == challenge.RequestNonce));
        Assert.Empty(challenges);
        Assert.False(DadAutoPartyDiscordPairingRules.MatchesActiveChallenge(
            challenge,
            peer,
            challenge.RequestNonce,
            challenge.ExpiresAtUtc));
    }

    [Fact]
    public void OutboundChallengeRequiresExactOperatorConfirmedSigningFingerprint()
    {
        var peer = Peer();

        Assert.False(DadAutoPartyDiscordPairingRules.OperatorConfirmedFingerprint(peer, new string('0', 64)));
        Assert.Throws<InvalidOperationException>(() =>
            DadAutoPartyDiscordPairingRules.CreateOutboundChallenge(
                peer,
                new string('0', 64),
                DateTime.UtcNow));
        Assert.True(DadAutoPartyDiscordPairingRules.OperatorConfirmedFingerprint(
            peer,
            peer.SigningKeyFingerprint));
    }

    [Fact]
    public async Task CurrentUserDpapiTokenStoreRoundTripsAndDeletesToken()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "dad-discord-token", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DadAutoPartyDpapiDiscordTokenStore(root);
            var reference = await store.StoreAsync("private-token-value".AsMemory());
            var loaded = await store.LoadAsync(reference);

            Assert.StartsWith("discord-token-", reference, StringComparison.Ordinal);
            Assert.Equal("private-token-value", new string(loaded));
            Assert.DoesNotContain("private-token-value", await File.ReadAllTextAsync(
                Assert.Single(Directory.GetFiles(root, "*.dpapi"))), StringComparison.Ordinal);
            Assert.True(await store.DeleteAsync(reference));
            Array.Clear(loaded);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class MemoryIdentityStore(byte[] package) : IDadAutoPartyEndpointIdentityStore
    {
        public ValueTask<string> StoreAsync(ReadOnlyMemory<byte> identityMaterial, CancellationToken cancellationToken = default)
            => ValueTask.FromResult("identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        public ValueTask<byte[]> LoadAsync(string identityReference, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(package.ToArray());
        public ValueTask<bool> DeleteAsync(string identityReference, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
    }

    private static DadAutoPartyDiscordInboundMessage Message(string content)
        => new(1, 2, 3, true, content);

    private static DadAutoPartyDiscoveredClient Peer()
    {
        var signingPublicKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return new DadAutoPartyDiscoveredClient(
            10,
            20,
            "island-a",
            new string('A', 64),
            signingPublicKey,
            DadAutoPartyDiscordPairingRules.ComputeSigningKeyFingerprint(signingPublicKey),
            3,
            DadAutoPartyRole.Client,
            DateTime.UtcNow,
            DadAutoPartyPairingHealth.Unpaired,
            string.Empty);
    }

    private sealed record SignedEnvelopeFixture(
        DadAutoPartyPairingProtocol Protocol,
        DadAutoPartyPairingEnvelope Envelope)
    {
        public static async Task<SignedEnvelopeFixture> CreateAsync()
        {
            var privateKey = RandomNumberGenerator.GetBytes(32);
            var publicKey = BouncyCastlePrimitives.DeriveEd25519PublicKey(privateKey);
            var package = JsonSerializer.SerializeToUtf8Bytes(new
            {
                OwnerId = "owner-test",
                IslandId = "island-test",
                KeyGeneration = 1,
                SigningPrivateKey = Convert.ToBase64String(privateKey),
                EncryptionPrivateKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            });
            var configuration = new DadAutoPartyConfiguration
            {
                EndpointIdentityReference = "identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                RegisteredOwnerId = "owner-test",
                RegisteredIslandId = "island-test",
                RegistrationFingerprint = new string('A', 64),
                SigningPublicKey = Convert.ToBase64String(publicKey),
                DiscordApplicationId = 123,
                DiscordBotUserId = 456,
                DiscordBinding = new DadAutoPartyDiscordBinding { KeyGeneration = 1 },
            };
            var protocol = new DadAutoPartyPairingProtocol();
            var envelope = await protocol.CreateAsync(
                DadAutoPartyPairingMessageKind.Presence,
                DadAutoPartyRole.Client,
                configuration,
                new DadAutoPartySigningService(configuration, new MemoryIdentityStore(package)));
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(publicKey);
            CryptographicOperations.ZeroMemory(package);
            return new(protocol, envelope);
        }
    }
}
