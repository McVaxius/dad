using System.Security.Cryptography;
using System.Text.Json;
using AutoParty.Core.Cryptography;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAllianceDiscordProtocolTests
{
    [Fact]
    public async Task SignedCoordinatorEnvelopeVerifiesOnceForExactApplicationAndCharacter()
    {
        using var fixture = await Fixture.CreateAsync();
        var accepted = fixture.Protocol.Validate(fixture.Envelope, fixture.Context);
        var replay = fixture.Protocol.Validate(fixture.Envelope, fixture.Context);
        var serialized = DadAllianceDiscordProtocol.Serialize(fixture.Envelope);

        Assert.True(accepted.Allowed, accepted.SafeCode);
        Assert.Equal("dad-alliance-discord-envelope-verified", accepted.SafeCode);
        Assert.False(replay.Allowed);
        Assert.Equal("dad-alliance-discord-envelope-replay", replay.SafeCode);
        Assert.Equal(DadAllianceDiscordProtocol.Schema, fixture.Envelope.Schema);
        Assert.Equal(fixture.Instruction.TargetApplicationId, fixture.Envelope.TargetApplicationId);
        Assert.Equal(fixture.Instruction.TargetCharacterKey, fixture.Envelope.TargetCharacterKey);
        Assert.Equal(fixture.Instruction.AssignedAlliance, fixture.Envelope.AssignedAlliance);
        Assert.Equal(fixture.Instruction.Passcode, fixture.Envelope.Passcode);
        Assert.True(serialized.Length <= DadAllianceDiscordProtocol.MaximumEnvelopeCharacters);
        Assert.DoesNotContain("privateKey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sharedSecret", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("botToken", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("application")]
    [InlineData("character")]
    [InlineData("role")]
    public async Task ExactTargetAndCoordinatorRoleContradictionsAreRejected(string contradiction)
    {
        using var fixture = await Fixture.CreateAsync();
        var context = fixture.Context;
        switch (contradiction)
        {
            case "application":
                context = context with { LocalApplicationId = context.LocalApplicationId + 1 };
                break;
            case "character":
                context = context with { LocalCharacterKey = new DadCharacterKey("Other Example@Gamma") };
                break;
            case "role":
                fixture.Envelope.Role = DadAutoPartyRole.Client;
                break;
        }

        var result = fixture.Protocol.Validate(fixture.Envelope, context);

        Assert.False(result.Allowed);
        Assert.Equal("dad-alliance-discord-envelope-invalid", result.SafeCode);
    }

    [Fact]
    public async Task StaleAndRevokedPairingEnvelopesAreRejected()
    {
        using var staleFixture = await Fixture.CreateAsync();
        var stale = staleFixture.Protocol.Validate(
            staleFixture.Envelope,
            staleFixture.Context with
            {
                UtcNow = staleFixture.Context.UtcNow + DadAllianceDiscordProtocol.MaximumAge + TimeSpan.FromSeconds(1),
            });

        using var revokedFixture = await Fixture.CreateAsync();
        revokedFixture.Pairing.RevokedAtUtc = revokedFixture.Context.UtcNow;
        var revoked = revokedFixture.Protocol.Validate(revokedFixture.Envelope, revokedFixture.Context);

        Assert.False(stale.Allowed);
        Assert.Equal("dad-alliance-discord-envelope-stale", stale.SafeCode);
        Assert.False(revoked.Allowed);
        Assert.Equal("dad-alliance-discord-pairing-revoked", revoked.SafeCode);
    }

    [Fact]
    public async Task SignatureAndPairedIdentityChangesAreRejected()
    {
        using var signatureFixture = await Fixture.CreateAsync();
        signatureFixture.Envelope.LeaderWorld = "ChangedWorld";
        var signature = signatureFixture.Protocol.Validate(signatureFixture.Envelope, signatureFixture.Context);

        using var pairingFixture = await Fixture.CreateAsync();
        pairingFixture.Pairing.KeyGeneration++;
        var pairing = pairingFixture.Protocol.Validate(pairingFixture.Envelope, pairingFixture.Context);

        Assert.False(signature.Allowed);
        Assert.Equal("dad-alliance-discord-signature-invalid", signature.SafeCode);
        Assert.False(pairing.Allowed);
        Assert.Equal("dad-alliance-discord-paired-identity-changed", pairing.SafeCode);
    }

    [Fact]
    public async Task MessageSizeBoundIsEnforcedOnCreateAndDeserialize()
    {
        using var fixture = await Fixture.CreateAsync(createEnvelope: false);
        fixture.Instruction.LeaderName = new string('X', DadAllianceDiscordProtocol.MaximumEnvelopeCharacters);

        var oversized = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Protocol.CreateAsync(
                fixture.Instruction,
                fixture.Configuration,
                fixture.Signing));
        Assert.Contains("message bound", oversized.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(DadAllianceDiscordProtocol.Deserialize(
            new string('X', DadAllianceDiscordProtocol.MaximumEnvelopeCharacters + 1)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly byte[] privateKey;
        private readonly byte[] publicKey;
        private readonly byte[] package;

        private Fixture(
            DadAllianceDiscordProtocol protocol,
            DadAllianceRecruitmentInstructionDto instruction,
            DadAutoPartyConfiguration configuration,
            DadAutoPartySigningService signing,
            DadAutoPartyPairing pairing,
            DadAllianceDiscordValidationContext context,
            DadAllianceDiscordEnvelope envelope,
            byte[] privateKey,
            byte[] publicKey,
            byte[] package)
        {
            Protocol = protocol;
            Instruction = instruction;
            Configuration = configuration;
            Signing = signing;
            Pairing = pairing;
            Context = context;
            Envelope = envelope;
            this.privateKey = privateKey;
            this.publicKey = publicKey;
            this.package = package;
        }

        public DadAllianceDiscordProtocol Protocol { get; }
        public DadAllianceRecruitmentInstructionDto Instruction { get; }
        public DadAutoPartyConfiguration Configuration { get; }
        public DadAutoPartySigningService Signing { get; }
        public DadAutoPartyPairing Pairing { get; }
        public DadAllianceDiscordValidationContext Context { get; }
        public DadAllianceDiscordEnvelope Envelope { get; }

        public static async Task<Fixture> CreateAsync(bool createEnvelope = true)
        {
            var privateKey = RandomNumberGenerator.GetBytes(32);
            var publicKey = BouncyCastlePrimitives.DeriveEd25519PublicKey(privateKey);
            var package = JsonSerializer.SerializeToUtf8Bytes(new
            {
                OwnerId = "owner-fixture",
                IslandId = "coordinator-fixture",
                KeyGeneration = 7,
                SigningPrivateKey = Convert.ToBase64String(privateKey),
                EncryptionPrivateKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            });
            var fingerprint = new string('A', 64);
            var configuration = new DadAutoPartyConfiguration
            {
                EndpointIdentityReference = "identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                RegisteredOwnerId = "owner-fixture",
                RegisteredIslandId = "coordinator-fixture",
                RegistrationFingerprint = fingerprint,
                SigningPublicKey = Convert.ToBase64String(publicKey),
                DiscordApplicationId = 100,
                DiscordBotUserId = 200,
                DiscordBinding = new DadAutoPartyDiscordBinding
                {
                    ApplicationId = 100,
                    BotUserId = 200,
                    DadIdentity = "coordinator-fixture",
                    EndpointFingerprint = fingerprint,
                    KeyGeneration = 7,
                },
            };
            var signing = new DadAutoPartySigningService(configuration, new MemoryIdentityStore(package));
            var instruction = new DadAllianceRecruitmentInstructionDto
            {
                RecruitmentId = Guid.NewGuid().ToString("N"),
                CoordinatorWorkerSessionId = new DadWorkerSessionId("coordinator-worker"),
                CoordinatorIdentity = "coordinator-fixture",
                LeaderName = "Host Example",
                LeaderWorld = "Alpha",
                TargetWorkerSessionId = new DadWorkerSessionId("target-worker"),
                TargetApplicationId = 300,
                TargetCharacterKey = new DadCharacterKey("Target Example@Beta"),
                TargetCharacterName = "Target Example",
                TargetCharacterWorld = "Beta",
                TargetContentId = 400,
                AssignedAlliance = DadAllianceAssignment.C,
                Passcode = 4321,
                Attempt = 5,
                State = DadAllianceRecruitmentState.Searching,
                StopGeneration = 2,
            };
            var protocol = new DadAllianceDiscordProtocol();
            var envelope = createEnvelope
                ? await protocol.CreateAsync(instruction, configuration, signing)
                : new DadAllianceDiscordEnvelope();
            var pairing = new DadAutoPartyPairing
            {
                OwnerId = "owner-fixture",
                IslandId = "coordinator-fixture",
                PublicKeyFingerprint = fingerprint,
                KeyGeneration = 7,
                ApplicationId = 100,
                BotUserId = 200,
                SigningPublicKey = Convert.ToBase64String(publicKey),
                Role = DadAutoPartyRole.Coordinator,
                ConfirmedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            };
            var context = new DadAllianceDiscordValidationContext(
                MessageAuthorId: 200,
                LocalApplicationId: 300,
                LocalCharacterKey: instruction.TargetCharacterKey,
                CoordinatorPairing: pairing,
                UtcNow: DateTime.UtcNow);
            return new(
                protocol,
                instruction,
                configuration,
                signing,
                pairing,
                context,
                envelope,
                privateKey,
                publicKey,
                package);
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(publicKey);
            CryptographicOperations.ZeroMemory(package);
        }
    }

    private sealed class MemoryIdentityStore(byte[] package) : IDadAutoPartyEndpointIdentityStore
    {
        public ValueTask<string> StoreAsync(
            ReadOnlyMemory<byte> identityMaterial,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult("identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        public ValueTask<byte[]> LoadAsync(
            string identityReference,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(package.ToArray());

        public ValueTask<bool> DeleteAsync(
            string identityReference,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
    }
}
