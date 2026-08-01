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
    public const string EnrollmentReceiptSchema = "dad.autoparty.enrollment-receipt/v1";
    public const string PilotStatusReceiptSchema = "dad.autoparty.pilot-status/v1";
    private const int MaximumPackageBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly DadAutoPartyConfiguration configuration;
    private readonly IDadAutoPartyEndpointIdentityStore identityStore;
    private readonly Action saveConfiguration;

    public DadAutoPartyIdentityPackageService(
        DadAutoPartyConfiguration configuration,
        IDadAutoPartyEndpointIdentityStore identityStore,
        Action saveConfiguration)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.identityStore = identityStore ?? throw new ArgumentNullException(nameof(identityStore));
        this.saveConfiguration = saveConfiguration ?? throw new ArgumentNullException(nameof(saveConfiguration));
    }

    public async ValueTask<DadAutoPartyIdentityOperationResult> GenerateAsync(
        string? requestedAlias = null,
        CancellationToken cancellationToken = default)
    {
        var signingPrivate = RandomNumberGenerator.GetBytes(AutoPartyProtocol.X25519KeyBytes);
        var encryptionPrivate = RandomNumberGenerator.GetBytes(AutoPartyProtocol.X25519KeyBytes);
        byte[]? signingPublic = null;
        byte[]? encryptionPublic = null;
        byte[]? privatePackage = null;
        try
        {
            signingPublic = BouncyCastlePrimitives.DeriveEd25519PublicKey(signingPrivate);
            encryptionPublic = BouncyCastlePrimitives.DeriveX25519PublicKey(encryptionPrivate);
            var ownerId = $"owner-{Guid.NewGuid():N}";
            var islandId = $"island-{Guid.NewGuid():N}";
            var fingerprint = BuildFingerprint(ownerId, islandId, 1, signingPublic, encryptionPublic);
            var alias = DadAutoPartyConfiguration.NormalizeAlias(requestedAlias);
            if (string.IsNullOrWhiteSpace(alias))
                alias = $"Island-{fingerprint[..8]}";
            privatePackage = JsonSerializer.SerializeToUtf8Bytes(
                new DadAutoPartyPrivateIdentityPackage(
                    ownerId,
                    islandId,
                    1,
                    Convert.ToBase64String(signingPrivate),
                    Convert.ToBase64String(encryptionPrivate)),
                JsonOptions);
            var oldReference = configuration.EndpointIdentityReference;
            var newReference = await identityStore.StoreAsync(privatePackage, cancellationToken).ConfigureAwait(false);
            configuration.EndpointIdentityReference = newReference;
            configuration.RegisteredOwnerId = ownerId;
            configuration.RegisteredIslandId = islandId;
            configuration.RegistrationFingerprint = fingerprint;
            configuration.EndpointAlias = alias;
            configuration.SigningPublicKey = Convert.ToBase64String(signingPublic);
            configuration.EncryptionPublicKey = Convert.ToBase64String(encryptionPublic);
            configuration.EnrollmentReceiptId = string.Empty;
            configuration.PilotArtifactSha256 = string.Empty;
            configuration.OwnerAcceptanceConfirmed = false;
            configuration.PairingEnabled = false;
            configuration.ExecutionEnabled = false;
            configuration.Pairings.Clear();
            configuration.Grants.Clear();
            configuration.StateGeneration++;
            saveConfiguration();
            if (!string.IsNullOrWhiteSpace(oldReference))
                await identityStore.DeleteAsync(oldReference, cancellationToken).ConfigureAwait(false);
            return new(true, "dad-endpoint-identity-generated");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingPrivate);
            CryptographicOperations.ZeroMemory(encryptionPrivate);
            if (signingPublic != null)
                CryptographicOperations.ZeroMemory(signingPublic);
            if (encryptionPublic != null)
                CryptographicOperations.ZeroMemory(encryptionPublic);
            if (privatePackage != null)
                CryptographicOperations.ZeroMemory(privatePackage);
        }
    }

    public async ValueTask<DadAutoPartyIdentityOperationResult> ExportPublicAsync(
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasCompletePublicIdentity())
            return new(false, "dad-public-identity-not-ready");
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathFullyQualified(outputDirectory))
            return new(false, "dad-public-identity-output-invalid");

        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{configuration.EndpointAlias}.apidentity");
        var package = new DadAutoPartyPublicIdentity(
            PublicIdentitySchema,
            configuration.EndpointAlias,
            configuration.RegisteredOwnerId,
            configuration.RegisteredIslandId,
            1,
            configuration.SigningPublicKey,
            configuration.EncryptionPublicKey,
            configuration.RegistrationFingerprint,
            DateTime.UtcNow);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
        if (bytes.Length > MaximumPackageBytes)
            return new(false, "dad-public-identity-package-too-large");
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
            return new(true, "dad-public-identity-exported", path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public async ValueTask<DadAutoPartyIdentityOperationResult> ImportEnrollmentReceiptAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasCompletePublicIdentity() || string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".apregistration", StringComparison.OrdinalIgnoreCase))
            return new(false, "dad-enrollment-receipt-path-invalid");
        var file = new FileInfo(path);
        if (file.Length is <= 0 or > MaximumPackageBytes)
            return new(false, "dad-enrollment-receipt-size-invalid");

        DadAutoPartyEnrollmentReceipt? receipt;
        try
        {
            await using var stream = File.OpenRead(path);
            receipt = await JsonSerializer.DeserializeAsync<DadAutoPartyEnrollmentReceipt>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return new(false, "dad-enrollment-receipt-invalid");
        }

        if (receipt == null ||
            !string.Equals(receipt.Schema, EnrollmentReceiptSchema, StringComparison.Ordinal) ||
            !Guid.TryParse(receipt.ReceiptId, out var receiptId) ||
            !string.Equals(receipt.OwnerId, configuration.RegisteredOwnerId, StringComparison.Ordinal) ||
            !string.Equals(receipt.IslandId, configuration.RegisteredIslandId, StringComparison.Ordinal) ||
            receipt.KeyGeneration != 1 ||
            !string.Equals(
                DadAutoPartyConfiguration.NormalizeFingerprint(receipt.IdentityFingerprint),
                configuration.RegistrationFingerprint,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(DadAutoPartyConfiguration.NormalizeSha256(receipt.PilotArtifactSha256)) ||
            !receipt.OwnerAcceptanceConfirmed ||
            receipt.AcceptedAtUtc.Kind != DateTimeKind.Utc ||
            receipt.AcceptedAtUtc > DateTime.UtcNow + TimeSpan.FromMinutes(2) ||
            receipt.AcceptedAtUtc < DateTime.UtcNow - TimeSpan.FromDays(30) ||
            receipt.Peers == null || receipt.Peers.Count is < 1 or > 16 ||
            receipt.Peers.Any(peer =>
                string.IsNullOrWhiteSpace(DadAutoPartyConfiguration.NormalizeIdentifier(peer.OwnerId)) ||
                string.IsNullOrWhiteSpace(DadAutoPartyConfiguration.NormalizeIdentifier(peer.IslandId)) ||
                string.IsNullOrWhiteSpace(DadAutoPartyConfiguration.NormalizeFingerprint(peer.IdentityFingerprint)) ||
                peer.KeyGeneration < 1 ||
                string.Equals(peer.IslandId, configuration.RegisteredIslandId, StringComparison.Ordinal)))
            return new(false, "dad-enrollment-receipt-mismatch");

        configuration.EnrollmentReceiptId = receiptId.ToString("D");
        configuration.PilotArtifactSha256 = DadAutoPartyConfiguration.NormalizeSha256(receipt.PilotArtifactSha256);
        configuration.OwnerAcceptanceConfirmed = true;
        configuration.PilotCourierProbeVerified = false;
        configuration.PendingPairings = receipt.Peers
            .Select(peer => new DadAutoPartyPairing
            {
                OwnerId = peer.OwnerId,
                IslandId = peer.IslandId,
                PublicKeyFingerprint = peer.IdentityFingerprint,
                KeyGeneration = peer.KeyGeneration,
                ConfirmedAtUtc = receipt.AcceptedAtUtc,
            }.Normalize())
            .ToList();
        configuration.StateGeneration++;
        saveConfiguration();
        return new(true, "dad-enrollment-receipt-imported", path);
    }

    public async ValueTask<DadAutoPartyIdentityOperationResult> ExportPilotStatusAsync(
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasCompletePublicIdentity() || !configuration.OwnerAcceptanceConfirmed ||
            string.IsNullOrWhiteSpace(configuration.PilotArtifactSha256))
            return new(false, "dad-pilot-status-not-ready");
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathFullyQualified(outputDirectory))
            return new(false, "dad-pilot-status-output-invalid");
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, configuration.EndpointAlias + ".apreceipt");
        var receipt = new DadAutoPartyPilotStatusReceipt(
            PilotStatusReceiptSchema,
            configuration.EndpointAlias,
            configuration.RegistrationFingerprint,
            configuration.PilotArtifactSha256,
            configuration.Enabled,
            configuration.PairingEnabled,
            configuration.ExecutionEnabled,
            configuration.OwnerAcceptanceConfirmed,
            !string.IsNullOrWhiteSpace(configuration.PilotPlannerGroupId) &&
            !string.IsNullOrWhiteSpace(configuration.PilotQueueAuthorityFingerprint),
            configuration.PilotCourierProbeVerified,
            configuration.Pairings.Count(pairing => pairing.RevokedAtUtc == null),
            DateTime.UtcNow);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
            return new(true, "dad-pilot-status-exported", path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public DadAutoPartyIdentityOperationResult Rotate()
    {
        configuration.PairingEnabled = false;
        configuration.ExecutionEnabled = false;
        configuration.OwnerAcceptanceConfirmed = false;
        configuration.EnrollmentReceiptId = string.Empty;
        configuration.Pairings.Clear();
        configuration.PendingPairings.Clear();
        configuration.Grants.Clear();
        configuration.PilotCourierProbeVerified = false;
        configuration.StateGeneration++;
        saveConfiguration();
        return new(true, "dad-identity-rotation-requires-generate");
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

}
