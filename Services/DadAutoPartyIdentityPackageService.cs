using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoParty.Contracts;
using AutoParty.Core.Cryptography;
using dad.Models;

namespace dad.Services;

public sealed class DadAutoPartyIdentityPackageService
{
    public const string PublicIdentitySchema = "dad.autoparty.public-identity/v1";
    public const string RegistrationRecipient = "central-autoparty";
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(10);
    private readonly DadAutoPartyConfiguration configuration;
    private readonly IDadAutoPartyEndpointIdentityStore identityStore;
    private readonly DadAutoPartySigningService signing;
    private readonly Action saveConfiguration;

    public DadAutoPartyIdentityPackageService(
        DadAutoPartyConfiguration configuration,
        IDadAutoPartyEndpointIdentityStore identityStore,
        Action saveConfiguration)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.identityStore = identityStore ?? throw new ArgumentNullException(nameof(identityStore));
        signing = new DadAutoPartySigningService(configuration, identityStore);
        this.saveConfiguration = saveConfiguration ?? throw new ArgumentNullException(nameof(saveConfiguration));
    }

    public async ValueTask<DadAutoPartyIdentityOperationResult> GenerateAsync(
        string? requestedAlias = null,
        CancellationToken cancellationToken = default) =>
        await GenerateChallengeAsync(requestedAlias, cancellationToken).ConfigureAwait(false);

    public async ValueTask<DadAutoPartyIdentityOperationResult> GenerateChallengeAsync(
        string? requestedAlias = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var alias = DadAutoPartyConfiguration.NormalizeAlias(requestedAlias);
        if (string.IsNullOrWhiteSpace(alias))
            return Failure("dad-registration-alias-invalid");

        if (!HasCompletePublicIdentity())
        {
            var generated = await GenerateIdentityAsync(alias, cancellationToken).ConfigureAwait(false);
            if (!generated.Succeeded)
                return generated;
        }
        else if (!string.Equals(configuration.EndpointAlias, alias, StringComparison.Ordinal))
        {
            return Failure("dad-registration-alias-identity-mismatch");
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var registrationId = Guid.NewGuid();
            var messageId = Guid.NewGuid();
            var nonce = RandomNumberGenerator.GetBytes(AutoPartyProtocol.ContractNonceBytes);
            byte[]? signingPublic = null;
            byte[]? encryptionPublic = null;
            byte[]? canonical = null;
            byte[]? signature = null;
            try
            {
                signingPublic = Convert.FromBase64String(configuration.SigningPublicKey);
                encryptionPublic = Convert.FromBase64String(configuration.EncryptionPublicKey);
                var header = new ContractHeader(
                    AutoPartyProtocol.CurrentVersion,
                    messageId,
                    $"registration:{registrationId:N}",
                    new IslandId(configuration.RegisteredIslandId),
                    new IslandId(RegistrationRecipient),
                    now,
                    now + ChallengeLifetime,
                    Math.Max(1, configuration.StateGeneration + 1),
                    Math.Max(1, configuration.EndpointKeyGeneration),
                    Math.Max(1, configuration.EndpointKeyGeneration),
                    1,
                    ContractHeader.CreateNonce(nonce),
                    ImmutableArray<int>.Empty);
                var challenge = new RegistrationChallenge(
                    header,
                    registrationId,
                    new OwnerId(configuration.RegisteredOwnerId),
                    new IslandId(configuration.RegisteredIslandId),
                    configuration.EndpointAlias,
                    new EndpointPublicKeys(
                        configuration.EndpointKeyGeneration,
                        $"ed25519:{configuration.RegistrationFingerprint[..16].ToLowerInvariant()}",
                        ImmutableArray.CreateRange(signingPublic),
                        $"x25519:{configuration.RegistrationFingerprint[..16].ToLowerInvariant()}",
                        ImmutableArray.CreateRange(encryptionPublic)),
                    configuration.RegistrationFingerprint,
                    now + ChallengeLifetime);
                canonical = RegistrationCborCodec.EncodeUnsignedChallenge(challenge);
                signature = await signing.SignAsync(canonical, cancellationToken).ConfigureAwait(false);
                var encoded = RegistrationCopyPasteCodec.EncodeChallenge(
                    AuthenticatedContract<RegistrationChallenge>.Create(challenge, signature));

                configuration.RegistrationId = registrationId.ToString("D");
                configuration.RegistrationState = DadAutoPartyRegistrationState.Unregistered;
                configuration.StateGeneration++;
                saveConfiguration();
                return new(true, "dad-registration-challenge-created", encoded);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
                if (signingPublic != null) CryptographicOperations.ZeroMemory(signingPublic);
                if (encryptionPublic != null) CryptographicOperations.ZeroMemory(encryptionPublic);
                if (canonical != null) CryptographicOperations.ZeroMemory(canonical);
                if (signature != null) CryptographicOperations.ZeroMemory(signature);
            }
        }
        catch (Exception exception) when (
            exception is ProtocolException or FormatException or CryptographicException or InvalidOperationException)
        {
            return Failure("dad-registration-challenge-failed");
        }
    }

    public async ValueTask<DadAutoPartyIdentityOperationResult> RotateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(configuration.EndpointIdentityReference))
            await identityStore.DeleteAsync(configuration.EndpointIdentityReference, cancellationToken).ConfigureAwait(false);
        configuration.EndpointIdentityReference = string.Empty;
        configuration.RegisteredOwnerId = string.Empty;
        configuration.RegisteredIslandId = string.Empty;
        configuration.RegistrationFingerprint = string.Empty;
        configuration.EndpointAlias = string.Empty;
        configuration.SigningPublicKey = string.Empty;
        configuration.EncryptionPublicKey = string.Empty;
        configuration.EndpointKeyGeneration = Math.Max(1, configuration.EndpointKeyGeneration + 1);
        configuration.RegistrationState = DadAutoPartyRegistrationState.Unregistered;
        configuration.RegistrationId = string.Empty;
        configuration.RouteId = string.Empty;
        configuration.WebhookCredentialReference = string.Empty;
        configuration.UplinkEpochId = string.Empty;
        configuration.DownlinkEpochId = string.Empty;
        configuration.MailboxEpochGeneration = 0;
        configuration.RelayKeyGeneration = 1;
        configuration.RelaySigningPublicKey = string.Empty;
        configuration.RelayAgreementPublicKey = string.Empty;
        configuration.Pairings.Clear();
        configuration.PendingPairings.Clear();
        configuration.Grants.Clear();
        configuration.Listings.Clear();
        configuration.RemoteBindings.Clear();
        configuration.Deauthentications.Clear();
        configuration.StateGeneration++;
        saveConfiguration();
        return new(true, "dad-identity-rotation-requires-generate");
    }

    private async ValueTask<DadAutoPartyIdentityOperationResult> GenerateIdentityAsync(
        string alias,
        CancellationToken cancellationToken)
    {
        var signingPrivate = RandomNumberGenerator.GetBytes(AutoPartyProtocol.Ed25519SignatureBytes / 2);
        var encryptionPrivate = RandomNumberGenerator.GetBytes(AutoPartyProtocol.X25519KeyBytes);
        byte[]? signingPublic = null;
        byte[]? encryptionPublic = null;
        byte[]? package = null;
        try
        {
            signingPublic = BouncyCastlePrimitives.DeriveEd25519PublicKey(signingPrivate);
            encryptionPublic = BouncyCastlePrimitives.DeriveX25519PublicKey(encryptionPrivate);
            var ownerId = $"owner-{Guid.NewGuid():N}";
            var islandId = $"island-{Guid.NewGuid():N}";
            var keyGeneration = Math.Max(1, configuration.EndpointKeyGeneration);
            var fingerprint = BuildFingerprint(
                ownerId,
                islandId,
                keyGeneration,
                signingPublic,
                encryptionPublic);
            package = JsonSerializer.SerializeToUtf8Bytes(new DadAutoPartyPrivateIdentityPackage(
                ownerId,
                islandId,
                keyGeneration,
                Convert.ToBase64String(signingPrivate),
                Convert.ToBase64String(encryptionPrivate)));
            var identityReference = await identityStore.StoreAsync(package, cancellationToken).ConfigureAwait(false);
            configuration.EndpointIdentityReference = identityReference;
            configuration.RegisteredOwnerId = ownerId;
            configuration.RegisteredIslandId = islandId;
            configuration.RegistrationFingerprint = fingerprint;
            configuration.EndpointAlias = alias;
            configuration.SigningPublicKey = Convert.ToBase64String(signingPublic);
            configuration.EncryptionPublicKey = Convert.ToBase64String(encryptionPublic);
            configuration.EndpointKeyGeneration = keyGeneration;
            configuration.StateGeneration++;
            saveConfiguration();
            return new(true, "dad-endpoint-identity-generated");
        }
        catch (Exception exception) when (
            exception is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failure("dad-endpoint-identity-generation-failed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingPrivate);
            CryptographicOperations.ZeroMemory(encryptionPrivate);
            if (signingPublic != null) CryptographicOperations.ZeroMemory(signingPublic);
            if (encryptionPublic != null) CryptographicOperations.ZeroMemory(encryptionPublic);
            if (package != null) CryptographicOperations.ZeroMemory(package);
        }
    }

    private bool HasCompletePublicIdentity() =>
        !string.IsNullOrWhiteSpace(configuration.EndpointIdentityReference) &&
        !string.IsNullOrWhiteSpace(configuration.RegisteredOwnerId) &&
        !string.IsNullOrWhiteSpace(configuration.RegisteredIslandId) &&
        !string.IsNullOrWhiteSpace(configuration.RegistrationFingerprint) &&
        !string.IsNullOrWhiteSpace(configuration.EndpointAlias) &&
        !string.IsNullOrWhiteSpace(configuration.SigningPublicKey) &&
        !string.IsNullOrWhiteSpace(configuration.EncryptionPublicKey);

    internal static string BuildFingerprint(
        string ownerId,
        string islandId,
        long keyGeneration,
        byte[] signingPublic,
        byte[] encryptionPublic)
    {
        var canonical = Encoding.UTF8.GetBytes(string.Join('\n',
            PublicIdentitySchema,
            ownerId,
            islandId,
            keyGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBase64String(signingPublic),
            Convert.ToBase64String(encryptionPublic)));
        try
        {
            return Convert.ToHexString(SHA256.HashData(canonical));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static DadAutoPartyIdentityOperationResult Failure(string safeCode) => new(false, safeCode);
}
