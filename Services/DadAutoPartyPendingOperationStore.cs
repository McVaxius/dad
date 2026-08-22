using System.Text.Json;

namespace dad.Services;

internal sealed record DadAutoPartyPendingDeregistration(
    Guid DeregistrationId,
    long RevocationGeneration,
    string SafeReason,
    DateTimeOffset RequestedAt,
    bool DeleteEndpointIdentity,
    long StateGeneration)
{
    public bool IsValid =>
        DeregistrationId != Guid.Empty &&
        RevocationGeneration >= 1 &&
        StateGeneration >= 1 &&
        RequestedAt.Offset == TimeSpan.Zero &&
        RequestedAt <= DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2) &&
        !string.IsNullOrWhiteSpace(SafeReason) &&
        SafeReason.Length <= 128 &&
        SafeReason.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':' or '.');
}

internal interface IDadAutoPartyPendingOperationStore
{
    DadAutoPartyPendingDeregistration? LoadDeregistration();
    void SaveDeregistration(DadAutoPartyPendingDeregistration pending);
    void ClearDeregistration(Guid deregistrationId);
    void ClearAll();
}

internal sealed class DadAutoPartyFilePendingOperationStore : IDadAutoPartyPendingOperationStore
{
    private const int MaximumStateBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string statePath;
    private readonly DadAtomicFileStore atomicFileStore;

    public DadAutoPartyFilePendingOperationStore(
        string rootPath,
        DadAtomicFileStore? atomicFileStore = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("An AutoParty pending-operation root is required.", nameof(rootPath));
        var root = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(root);
        statePath = Path.GetFullPath(Path.Combine(root, "pending-deregistration.json"));
        var expectedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!statePath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pending-operation path escaped its configured root.");
        this.atomicFileStore = atomicFileStore ?? new DadAtomicFileStore();
    }

    public DadAutoPartyPendingDeregistration? LoadDeregistration()
    {
        try
        {
            var file = new FileInfo(statePath);
            if (!file.Exists)
                return null;
            if (file.Length is <= 0 or > MaximumStateBytes)
                return null;
            var pending = JsonSerializer.Deserialize<DadAutoPartyPendingDeregistration>(
                File.ReadAllText(statePath),
                JsonOptions);
            return pending is { IsValid: true } ? pending : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void SaveDeregistration(DadAutoPartyPendingDeregistration pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (!pending.IsValid)
            throw new ArgumentException("The pending deregistration is invalid.", nameof(pending));
        var json = JsonSerializer.Serialize(pending, JsonOptions);
        if (json.Length is <= 0 or > MaximumStateBytes)
            throw new InvalidOperationException("The pending deregistration exceeded its storage bound.");
        atomicFileStore.Write(statePath, json);
    }

    public void ClearDeregistration(Guid deregistrationId)
    {
        if (deregistrationId == Guid.Empty)
            return;
        var current = LoadDeregistration();
        if (current?.DeregistrationId == deregistrationId && File.Exists(statePath))
            File.Delete(statePath);
    }

    public void ClearAll()
    {
        try
        {
            File.Delete(statePath);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
