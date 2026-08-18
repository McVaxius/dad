using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using dad.Models;

namespace dad.Services;

public interface IDadAutoPartyWebhookCredentialStore
{
    ValueTask<string> StoreAsync(
        DadAutoPartyWebhookCredential credential,
        CancellationToken cancellationToken = default);

    ValueTask<DadAutoPartyWebhookCredential> LoadAsync(
        string credentialReference,
        CancellationToken cancellationToken = default);

    ValueTask ReplaceAsync(
        string credentialReference,
        DadAutoPartyWebhookCredential credential,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(
        string credentialReference,
        CancellationToken cancellationToken = default);
}

[SupportedOSPlatform("windows")]
public sealed partial class DadAutoPartyDpapiWebhookCredentialStore : IDadAutoPartyWebhookCredentialStore
{
    private const int MaximumPlainBytes = 4096;
    private const int MaximumProtectedBytes = MaximumPlainBytes + 2048;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Dad.AutoParty.WebhookMailbox.v2");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string rootPath;

    public DadAutoPartyDpapiWebhookCredentialStore(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("A webhook mailbox root is required.", nameof(rootPath));
        this.rootPath = Path.GetFullPath(rootPath);
    }

    public async ValueTask<string> StoreAsync(
        DadAutoPartyWebhookCredential credential,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (credential is not { IsValid: true })
            throw new ArgumentException("The webhook mailbox credential is invalid.", nameof(credential));

        var plain = JsonSerializer.SerializeToUtf8Bytes(credential, JsonOptions);
        byte[]? protectedBytes = null;
        try
        {
            if (plain.Length is <= 0 or > MaximumPlainBytes)
                throw new ArgumentOutOfRangeException(nameof(credential));
            protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var credentialReference = $"webhook-mailbox-{Guid.NewGuid():N}";
            var path = ResolvePath(credentialReference);
            var temporaryPath = path + ".tmp";
            Directory.CreateDirectory(rootPath);
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            return credentialReference;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (protectedBytes != null)
                CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public async ValueTask<DadAutoPartyWebhookCredential> LoadAsync(
        string credentialReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(credentialReference);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaximumProtectedBytes)
            throw new InvalidOperationException("The webhook mailbox credential is unavailable or invalid.");

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            if (plain.Length is <= 0 or > MaximumPlainBytes)
                throw new InvalidOperationException("The webhook mailbox credential is invalid.");
            var credential = JsonSerializer.Deserialize<DadAutoPartyWebhookCredential>(plain, JsonOptions);
            return credential is { IsValid: true }
                ? credential
                : throw new InvalidOperationException("The webhook mailbox credential is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The webhook mailbox credential is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plain != null)
                CryptographicOperations.ZeroMemory(plain);
        }
    }

    public async ValueTask ReplaceAsync(
        string credentialReference,
        DadAutoPartyWebhookCredential credential,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (credential is not { IsValid: true })
            throw new ArgumentException("The webhook mailbox credential is invalid.", nameof(credential));

        var plain = JsonSerializer.SerializeToUtf8Bytes(credential, JsonOptions);
        byte[]? protectedBytes = null;
        try
        {
            if (plain.Length is <= 0 or > MaximumPlainBytes)
                throw new ArgumentOutOfRangeException(nameof(credential));
            protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var path = ResolvePath(credentialReference);
            var temporaryPath = path + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken).ConfigureAwait(false);
                File.Replace(temporaryPath, path, null);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (protectedBytes != null)
                CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public ValueTask<bool> DeleteAsync(
        string credentialReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(credentialReference);
        var existed = File.Exists(path);
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            return ValueTask.FromResult(false);
        }
        return ValueTask.FromResult(existed);
    }

    private string ResolvePath(string credentialReference)
    {
        var normalized = (credentialReference ?? string.Empty).Trim().ToLowerInvariant();
        if (!CredentialReferencePattern().IsMatch(normalized))
            throw new ArgumentException("The webhook mailbox reference is invalid.", nameof(credentialReference));
        var path = Path.GetFullPath(Path.Combine(rootPath, normalized + ".dpapi"));
        var expectedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The webhook mailbox path escaped its configured root.");
        return path;
    }

    [GeneratedRegex("^webhook-mailbox-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialReferencePattern();
}
