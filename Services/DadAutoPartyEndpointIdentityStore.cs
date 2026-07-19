using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace dad.Services;

public interface IDadAutoPartyEndpointIdentityStore
{
    ValueTask<string> StoreAsync(
        ReadOnlyMemory<byte> identityMaterial,
        CancellationToken cancellationToken = default);

    ValueTask<byte[]> LoadAsync(
        string identityReference,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(
        string identityReference,
        CancellationToken cancellationToken = default);
}

[SupportedOSPlatform("windows")]
public sealed partial class DadAutoPartyDpapiEndpointIdentityStore : IDadAutoPartyEndpointIdentityStore
{
    private const int MaximumIdentityBytes = 64 * 1024;
    private const int MaximumProtectedIdentityBytes = MaximumIdentityBytes + 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Dad.AutoParty.EndpointIdentity.v1");
    private readonly string rootPath;

    public DadAutoPartyDpapiEndpointIdentityStore(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("An endpoint identity root is required.", nameof(rootPath));
        this.rootPath = Path.GetFullPath(rootPath);
    }

    public async ValueTask<string> StoreAsync(
        ReadOnlyMemory<byte> identityMaterial,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (identityMaterial.Length is <= 0 or > MaximumIdentityBytes)
            throw new ArgumentOutOfRangeException(nameof(identityMaterial));

        var identityReference = $"identity-{Guid.NewGuid():N}";
        var path = ResolvePath(identityReference);
        var temporaryPath = path + ".tmp";
        var plain = identityMaterial.ToArray();
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(rootPath);
            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, true);
            return identityReference;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (protectedBytes != null)
                CryptographicOperations.ZeroMemory(protectedBytes);
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async ValueTask<byte[]> LoadAsync(
        string identityReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(identityReference);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaximumProtectedIdentityBytes)
            throw new InvalidOperationException("The endpoint identity reference is unavailable or invalid.");

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            if (plain.Length is <= 0 or > MaximumIdentityBytes)
            {
                CryptographicOperations.ZeroMemory(plain);
                throw new InvalidOperationException("The endpoint identity material is invalid.");
            }
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public ValueTask<bool> DeleteAsync(
        string identityReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(identityReference);
        if (!File.Exists(path))
            return ValueTask.FromResult(false);
        File.Delete(path);
        return ValueTask.FromResult(true);
    }

    private string ResolvePath(string identityReference)
    {
        var normalized = (identityReference ?? string.Empty).Trim();
        if (!IdentityReferencePattern().IsMatch(normalized))
            throw new ArgumentException("The endpoint identity reference is invalid.", nameof(identityReference));
        var path = Path.GetFullPath(Path.Combine(rootPath, normalized + ".dpapi"));
        var expectedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The endpoint identity path escaped its configured root.");
        return path;
    }

    [GeneratedRegex("^identity-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityReferencePattern();
}
