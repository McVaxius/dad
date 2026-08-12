using System.Security.Cryptography;
using AutoParty.Contracts;
using AutoParty.Core.Authentication;
using AutoParty.Core.Cryptography;
using dad.Models;

namespace dad.Services;

/// <summary>
/// Resolves only the exact endpoint, relay, and currently approved peer key versions used by the
/// semantic AutoParty channel. Private key material never leaves this resolver.
/// </summary>
internal sealed class DadAutoPartySemanticKeyResolver : IContractKeyResolver, IDisposable
{
    private const string RelayIslandId = DadAutoPartyIdentityPackageService.RegistrationRecipient;
    private readonly object gate = new();
    private readonly DadAutoPartyConfiguration configuration;
    private readonly string localIslandId;
    private readonly long localKeyVersion;
    private readonly byte[] localSigningPrivateKey;
    private readonly byte[] localAgreementPrivateKey;
    private readonly byte[] localSigningPublicKey;
    private readonly byte[] localAgreementPublicKey;
    private Dictionary<KeyReference, PublicKeyPair> peerKeys = [];
    private readonly Dictionary<KeyReference, TransientPublicKeyPair> transientPeerKeys = [];
    private PublicKeyPair? relayKeys;
    private bool disposed;

    public DadAutoPartySemanticKeyResolver(
        DadAutoPartyConfiguration configuration,
        DadAutoPartyPrivateIdentityPackage identity)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        ArgumentNullException.ThrowIfNull(identity);

        localIslandId = DadAutoPartyConfiguration.NormalizeIdentifier(identity.IslandId);
        localKeyVersion = identity.KeyGeneration;
        if (string.IsNullOrWhiteSpace(localIslandId) ||
            localKeyVersion < 1 ||
            !string.Equals(localIslandId, configuration.RegisteredIslandId, StringComparison.Ordinal) ||
            !string.Equals(identity.OwnerId, configuration.RegisteredOwnerId, StringComparison.Ordinal) ||
            localKeyVersion != configuration.EndpointKeyGeneration)
            throw new InvalidOperationException("dad-semantic-identity-mismatch");

        localSigningPrivateKey = DecodeKey(identity.SigningPrivateKey, AutoPartyProtocol.Ed25519SignatureBytes / 2);
        localAgreementPrivateKey = DecodeKey(identity.EncryptionPrivateKey, AutoPartyProtocol.X25519KeyBytes);
        localSigningPublicKey = DecodeKey(configuration.SigningPublicKey, AutoPartyProtocol.Ed25519PublicKeyBytes);
        localAgreementPublicKey = DecodeKey(configuration.EncryptionPublicKey, AutoPartyProtocol.X25519KeyBytes);
        var derivedSigning = BouncyCastlePrimitives.DeriveEd25519PublicKey(localSigningPrivateKey);
        var derivedAgreement = BouncyCastlePrimitives.DeriveX25519PublicKey(localAgreementPrivateKey);
        var keysMatch = false;
        try
        {
            keysMatch = CryptographicOperations.FixedTimeEquals(derivedSigning, localSigningPublicKey) &&
                CryptographicOperations.FixedTimeEquals(derivedAgreement, localAgreementPublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedSigning);
            CryptographicOperations.ZeroMemory(derivedAgreement);
        }
        if (!keysMatch)
        {
            CryptographicOperations.ZeroMemory(localSigningPrivateKey);
            CryptographicOperations.ZeroMemory(localAgreementPrivateKey);
            CryptographicOperations.ZeroMemory(localSigningPublicKey);
            CryptographicOperations.ZeroMemory(localAgreementPublicKey);
            throw new InvalidOperationException("dad-semantic-key-binding-mismatch");
        }
        RefreshPublicKeys();
    }

    public void RefreshPublicKeys()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var refreshed = new Dictionary<KeyReference, PublicKeyPair>();
            foreach (var pairing in configuration.Pairings
                         .Where(static pairing => pairing.IsActive)
                         .Concat(configuration.PendingPairings.Where(static pairing => pairing.IsValid)))
            {
                var reference = new KeyReference(pairing.IslandId, pairing.KeyGeneration);
                if (refreshed.ContainsKey(reference) || !TryDecodePair(pairing, out var keys))
                    continue;
                refreshed.Add(reference, keys);
            }

            var relay = TryDecodePair(
                configuration.RelaySigningPublicKey,
                configuration.RelayAgreementPublicKey,
                out var decodedRelay)
                ? decodedRelay
                : null;
            ZeroPeerKeys(peerKeys.Values);
            if (relayKeys is { } oldRelay)
                oldRelay.Dispose();
            peerKeys = refreshed;
            relayKeys = relay;
        }
    }

    public bool TryAddTransientPublicKeys(
        OwnerId ownerId,
        IslandId islandId,
        EndpointPublicKeys publicKeys,
        string expectedFingerprint,
        DateTimeOffset validUntil,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        var owner = DadAutoPartyConfiguration.NormalizeIdentifier(ownerId.Value);
        var island = DadAutoPartyConfiguration.NormalizeIdentifier(islandId.Value);
        var fingerprint = DadAutoPartyConfiguration.NormalizeFingerprint(expectedFingerprint);
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(island) ||
            string.Equals(island, localIslandId, StringComparison.Ordinal) ||
            string.Equals(island, RelayIslandId, StringComparison.Ordinal) ||
            publicKeys.KeyVersion < 1 || validUntil <= now ||
            publicKeys.Ed25519PublicKey.IsDefault ||
            publicKeys.Ed25519PublicKey.Length != AutoPartyProtocol.Ed25519PublicKeyBytes ||
            publicKeys.X25519PublicKey.IsDefault ||
            publicKeys.X25519PublicKey.Length != AutoPartyProtocol.X25519KeyBytes)
            return false;

        var expected = DadAutoPartyIdentityPackageService.BuildFingerprint(
            owner,
            island,
            publicKeys.KeyVersion,
            publicKeys.Ed25519PublicKey.ToArray(),
            publicKeys.X25519PublicKey.ToArray());
        if (!FixedEquals(fingerprint, expected) ||
            !TryDecodePair(publicKeys, out var decoded))
            return false;

        lock (gate)
        {
            if (disposed)
            {
                decoded.Dispose();
                return false;
            }
            ExpireTransientPublicKeysCore(now);
            var reference = new KeyReference(island, publicKeys.KeyVersion);
            if (transientPeerKeys.Remove(reference, out var replaced))
                replaced.Dispose();
            transientPeerKeys[reference] = new(decoded, validUntil);
            return true;
        }
    }

    public void RemoveTransientPublicKeys(string islandId)
    {
        var island = DadAutoPartyConfiguration.NormalizeIdentifier(islandId);
        if (string.IsNullOrWhiteSpace(island))
            return;
        lock (gate)
        {
            if (disposed)
                return;
            foreach (var reference in transientPeerKeys.Keys
                         .Where(reference => string.Equals(reference.IslandId, island, StringComparison.Ordinal))
                         .ToList())
            {
                transientPeerKeys[reference].Dispose();
                transientPeerKeys.Remove(reference);
            }
        }
    }

    public void ExpireTransientPublicKeys(DateTimeOffset now)
    {
        lock (gate)
        {
            if (!disposed)
                ExpireTransientPublicKeysCore(now);
        }
    }

    public bool TryGetEd25519PrivateKey(
        IslandId islandId,
        long keyVersion,
        out ReadOnlyMemory<byte> privateKey)
    {
        lock (gate)
        {
            if (!disposed && IsLocal(islandId, keyVersion))
            {
                privateKey = localSigningPrivateKey;
                return true;
            }
            privateKey = default;
            return false;
        }
    }

    public bool TryGetEd25519PublicKey(
        IslandId islandId,
        long keyVersion,
        out ReadOnlyMemory<byte> publicKey)
    {
        lock (gate)
        {
            if (disposed)
            {
                publicKey = default;
                return false;
            }
            if (IsLocal(islandId, keyVersion))
            {
                publicKey = localSigningPublicKey;
                return true;
            }
            if (IsRelay(islandId, keyVersion) && relayKeys is { } relay)
            {
                publicKey = relay.Signing;
                return true;
            }
            if (peerKeys.TryGetValue(new KeyReference(islandId.Value, keyVersion), out var peer))
            {
                publicKey = peer.Signing;
                return true;
            }
            if (transientPeerKeys.TryGetValue(
                    new KeyReference(islandId.Value, keyVersion),
                    out var transient))
            {
                publicKey = transient.Keys.Signing;
                return true;
            }
            publicKey = default;
            return false;
        }
    }

    public bool TryGetX25519PrivateKey(
        IslandId islandId,
        long keyVersion,
        out ReadOnlyMemory<byte> privateKey)
    {
        lock (gate)
        {
            if (!disposed && IsLocal(islandId, keyVersion))
            {
                privateKey = localAgreementPrivateKey;
                return true;
            }
            privateKey = default;
            return false;
        }
    }

    public bool TryGetX25519PublicKey(
        IslandId islandId,
        long keyVersion,
        out ReadOnlyMemory<byte> publicKey)
    {
        lock (gate)
        {
            if (disposed)
            {
                publicKey = default;
                return false;
            }
            if (IsLocal(islandId, keyVersion))
            {
                publicKey = localAgreementPublicKey;
                return true;
            }
            if (IsRelay(islandId, keyVersion) && relayKeys is { } relay)
            {
                publicKey = relay.Agreement;
                return true;
            }
            if (peerKeys.TryGetValue(new KeyReference(islandId.Value, keyVersion), out var peer))
            {
                publicKey = peer.Agreement;
                return true;
            }
            if (transientPeerKeys.TryGetValue(
                    new KeyReference(islandId.Value, keyVersion),
                    out var transient))
            {
                publicKey = transient.Keys.Agreement;
                return true;
            }
            publicKey = default;
            return false;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            CryptographicOperations.ZeroMemory(localSigningPrivateKey);
            CryptographicOperations.ZeroMemory(localAgreementPrivateKey);
            CryptographicOperations.ZeroMemory(localSigningPublicKey);
            CryptographicOperations.ZeroMemory(localAgreementPublicKey);
            ZeroPeerKeys(peerKeys.Values);
            peerKeys.Clear();
            foreach (var transient in transientPeerKeys.Values)
                transient.Dispose();
            transientPeerKeys.Clear();
            relayKeys?.Dispose();
            relayKeys = null;
        }
    }

    private bool IsLocal(IslandId islandId, long keyVersion) =>
        string.Equals(islandId.Value, localIslandId, StringComparison.Ordinal) &&
        keyVersion == localKeyVersion;

    private bool IsRelay(IslandId islandId, long keyVersion) =>
        string.Equals(islandId.Value, RelayIslandId, StringComparison.Ordinal) &&
        keyVersion == configuration.RelayKeyGeneration;

    private static bool TryDecodePair(DadAutoPartyPairing pairing, out PublicKeyPair keys) =>
        TryDecodePair(pairing.SigningPublicKey, pairing.AgreementPublicKey, out keys);

    private static bool TryDecodePair(EndpointPublicKeys publicKeys, out PublicKeyPair keys)
    {
        keys = null!;
        if (publicKeys.Ed25519PublicKey.IsDefault ||
            publicKeys.Ed25519PublicKey.Length != AutoPartyProtocol.Ed25519PublicKeyBytes ||
            publicKeys.X25519PublicKey.IsDefault ||
            publicKeys.X25519PublicKey.Length != AutoPartyProtocol.X25519KeyBytes)
            return false;
        keys = new(
            publicKeys.Ed25519PublicKey.ToArray(),
            publicKeys.X25519PublicKey.ToArray());
        return true;
    }

    private static bool TryDecodePair(string signing, string agreement, out PublicKeyPair keys)
    {
        keys = null!;
        byte[]? signingBytes = null;
        byte[]? agreementBytes = null;
        try
        {
            signingBytes = Convert.FromBase64String(signing);
            agreementBytes = Convert.FromBase64String(agreement);
            if (signingBytes.Length != AutoPartyProtocol.Ed25519PublicKeyBytes ||
                agreementBytes.Length != AutoPartyProtocol.X25519KeyBytes)
                return false;
            keys = new PublicKeyPair(signingBytes, agreementBytes);
            signingBytes = null;
            agreementBytes = null;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (signingBytes != null)
                CryptographicOperations.ZeroMemory(signingBytes);
            if (agreementBytes != null)
                CryptographicOperations.ZeroMemory(agreementBytes);
        }
    }

    private static byte[] DecodeKey(string encoded, int expectedLength)
    {
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("dad-semantic-key-invalid", exception);
        }
        if (decoded.Length == expectedLength)
            return decoded;
        CryptographicOperations.ZeroMemory(decoded);
        throw new InvalidOperationException("dad-semantic-key-invalid");
    }

    private static void ZeroPeerKeys(IEnumerable<PublicKeyPair> keys)
    {
        foreach (var key in keys)
            key.Dispose();
    }

    private void ExpireTransientPublicKeysCore(DateTimeOffset now)
    {
        foreach (var reference in transientPeerKeys
                     .Where(pair => pair.Value.ValidUntil <= now)
                     .Select(static pair => pair.Key)
                     .ToList())
        {
            transientPeerKeys[reference].Dispose();
            transientPeerKeys.Remove(reference);
        }
    }

    private static bool FixedEquals(string left, string right)
    {
        if (left.Length == 0 || left.Length != right.Length)
            return false;
        var leftBytes = System.Text.Encoding.ASCII.GetBytes(left);
        var rightBytes = System.Text.Encoding.ASCII.GetBytes(right);
        try
        {
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private readonly record struct KeyReference(string IslandId, long KeyVersion);

    private sealed class TransientPublicKeyPair(PublicKeyPair keys, DateTimeOffset validUntil) : IDisposable
    {
        public PublicKeyPair Keys { get; } = keys;
        public DateTimeOffset ValidUntil { get; } = validUntil;
        public void Dispose() => Keys.Dispose();
    }

    private sealed class PublicKeyPair(byte[] signing, byte[] agreement) : IDisposable
    {
        public byte[] Signing { get; } = signing;
        public byte[] Agreement { get; } = agreement;

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Signing);
            CryptographicOperations.ZeroMemory(Agreement);
        }
    }
}
