using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace dad.Services;

public interface IDadAutoPartyDiscordTokenStore
{
    ValueTask<string> StoreAsync(ReadOnlyMemory<char> token, CancellationToken cancellationToken = default);
    ValueTask<char[]> LoadAsync(string tokenReference, CancellationToken cancellationToken = default);
    ValueTask<bool> DeleteAsync(string tokenReference, CancellationToken cancellationToken = default);
}

[SupportedOSPlatform("windows")]
public sealed partial class DadAutoPartyDpapiDiscordTokenStore : IDadAutoPartyDiscordTokenStore
{
    private const int MaximumTokenBytes = 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Dad.AutoParty.DiscordToken.v1");
    private readonly string rootPath;

    public DadAutoPartyDpapiDiscordTokenStore(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("A Discord token root is required.", nameof(rootPath));
        this.rootPath = Path.GetFullPath(rootPath);
    }

    public async ValueTask<string> StoreAsync(ReadOnlyMemory<char> token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var characters = token.ToArray();
        byte[]? plain = null;
        byte[]? protectedBytes = null;
        try
        {
            plain = Encoding.UTF8.GetBytes(characters);
            if (plain.Length is <= 0 or > MaximumTokenBytes)
                throw new ArgumentOutOfRangeException(nameof(token));
            protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var tokenReference = $"discord-token-{Guid.NewGuid():N}";
            var path = ResolvePath(tokenReference);
            var temporary = path + ".tmp";
            Directory.CreateDirectory(rootPath);
            try
            {
                await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            return tokenReference;
        }
        finally
        {
            Array.Clear(characters);
            if (plain != null)
                CryptographicOperations.ZeroMemory(plain);
            if (protectedBytes != null)
                CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public async ValueTask<char[]> LoadAsync(string tokenReference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(tokenReference);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaximumTokenBytes + 2048)
            throw new InvalidOperationException("The Discord token reference is unavailable or invalid.");
        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            if (plain.Length is <= 0 or > MaximumTokenBytes)
                throw new InvalidOperationException("The Discord token material is invalid.");
            return Encoding.UTF8.GetChars(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plain != null)
                CryptographicOperations.ZeroMemory(plain);
        }
    }

    public ValueTask<bool> DeleteAsync(string tokenReference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(tokenReference);
        if (!File.Exists(path))
            return ValueTask.FromResult(false);
        File.Delete(path);
        return ValueTask.FromResult(true);
    }

    private string ResolvePath(string tokenReference)
    {
        var normalized = (tokenReference ?? string.Empty).Trim().ToLowerInvariant();
        if (!TokenReferencePattern().IsMatch(normalized))
            throw new ArgumentException("The Discord token reference is invalid.", nameof(tokenReference));
        var path = Path.GetFullPath(Path.Combine(rootPath, normalized + ".dpapi"));
        var expectedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Discord token path escaped its configured root.");
        return path;
    }

    [GeneratedRegex("^discord-token-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenReferencePattern();
}
