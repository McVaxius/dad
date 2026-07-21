using System.Security.Cryptography;
using System.Text.Json;
using AutoParty.Core.Cryptography;
using dad.Models;

namespace dad.Services;

public sealed class DadAutoPartySigningService
{
    private readonly DadAutoPartyConfiguration configuration;
    private readonly IDadAutoPartyEndpointIdentityStore identityStore;

    public DadAutoPartySigningService(
        DadAutoPartyConfiguration configuration,
        IDadAutoPartyEndpointIdentityStore identityStore)
    {
        this.configuration = configuration;
        this.identityStore = identityStore;
    }

    public async ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configuration.EndpointIdentityReference))
            throw new InvalidOperationException("The DAD endpoint identity is not configured.");
        var packageBytes = await identityStore.LoadAsync(
            configuration.EndpointIdentityReference,
            cancellationToken).ConfigureAwait(false);
        byte[]? privateKey = null;
        try
        {
            var package = JsonSerializer.Deserialize<PrivateIdentityPackage>(packageBytes)
                ?? throw new InvalidOperationException("The DAD endpoint identity is invalid.");
            if (!string.Equals(package.OwnerId, configuration.RegisteredOwnerId, StringComparison.Ordinal) ||
                !string.Equals(package.IslandId, configuration.RegisteredIslandId, StringComparison.Ordinal) ||
                package.KeyGeneration < 1)
                throw new InvalidOperationException("The DAD endpoint identity binding changed.");
            privateKey = Convert.FromBase64String(package.SigningPrivateKey);
            return BouncyCastlePrimitives.Ed25519Sign(privateKey, payload.Span);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The DAD endpoint identity is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(packageBytes);
            if (privateKey != null)
                CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
        => BouncyCastlePrimitives.Ed25519Verify(publicKey, payload, signature);

    private sealed record PrivateIdentityPackage(
        string OwnerId,
        string IslandId,
        long KeyGeneration,
        string SigningPrivateKey,
        string EncryptionPrivateKey);
}
