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
    public async Task PairingProtocolRejectsMissingAndTamperedSignatures()
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

        Assert.Equal("dad-discord-envelope-invalid", missing.SafeCode);
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
    public void PendingPairingIdentityMustMatchEveryPersistedPeerField()
    {
        var peer = new DadAutoPartyDiscoveredClient(
            10,
            20,
            "island-a",
            new string('A', 64),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            3,
            DadAutoPartyRole.Client,
            DateTime.UtcNow,
            DadAutoPartyPairingHealth.Unpaired,
            string.Empty);
        var pending = new DadAutoPartyPairing
        {
            OwnerId = "discord",
            IslandId = peer.DadIdentity,
            PublicKeyFingerprint = peer.EndpointFingerprint,
            SigningPublicKey = peer.SigningPublicKey,
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
