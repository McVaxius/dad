using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoParty.Contracts;
using dad.Models;

namespace dad.Services;

public sealed class DadAutoPartyFileCourierAdapter : IAutoPartyTransportAdapter, IDisposable
{
    private const int MaximumSpoolFiles = 1024;
    private const int MaximumFileBytes = AutoPartyProtocol.PreallocationDefensiveCeilingBytes;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DadAutoPartyConfiguration configuration;
    private readonly string rootPath;
    private readonly Dictionary<Guid, string> receivedFiles = [];
    private bool disposed;

    public DadAutoPartyFileCourierAdapter(DadAutoPartyConfiguration configuration)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        if (string.IsNullOrWhiteSpace(configuration.CourierRootPath) ||
            !Path.IsPathFullyQualified(configuration.CourierRootPath))
            throw new ArgumentException("The AutoParty courier root must be absolute.", nameof(configuration));
        rootPath = Path.GetFullPath(configuration.CourierRootPath);
    }

    public ValueTask<AutoPartyTransportHealth> GetHealthAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed || !configuration.Enabled)
            return ValueTask.FromResult(Health(AutoPartyTransportHealthState.Disabled, "dad-file-courier-disabled"));
        if (string.IsNullOrWhiteSpace(configuration.RegisteredIslandId) ||
            !configuration.OwnerAcceptanceConfirmed ||
            string.IsNullOrWhiteSpace(configuration.EnrollmentReceiptId))
            return ValueTask.FromResult(Health(AutoPartyTransportHealthState.NotReady, "dad-file-courier-registration-pending"));
        if (!Directory.Exists(rootPath))
            return ValueTask.FromResult(Health(AutoPartyTransportHealthState.NotReady, "dad-file-courier-root-missing"));
        try
        {
            var probe = Path.Combine(rootPath, $".probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return ValueTask.FromResult(Health(AutoPartyTransportHealthState.Ready, "dad-file-courier-ready"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(Health(AutoPartyTransportHealthState.Degraded, "dad-file-courier-root-unwritable"));
        }
    }

    public async IAsyncEnumerable<OpaqueEnvelope> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (disposed || !configuration.Enabled || string.IsNullOrWhiteSpace(configuration.RegisteredIslandId))
            yield break;
        var inbox = GetIslandFolder("inbox", configuration.RegisteredIslandId);
        if (!Directory.Exists(inbox))
            yield break;

        var paths = Directory.EnumerateFiles(inbox, "*.apin", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .ToList();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpaqueEnvelope? envelope;
            try
            {
                envelope = await ReadEnvelopeAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidDataException)
            {
                MoveToQuarantine(path);
                continue;
            }

            if (!IsBounded(envelope) ||
                !string.Equals(envelope.RecipientIslandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal) ||
                envelope.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                MoveToQuarantine(path);
                continue;
            }

            receivedFiles[envelope.EnvelopeId] = path;
            yield return envelope;
        }
    }

    public async ValueTask<AutoPartyTransportSendResult> SendAsync(
        OpaqueEnvelope delivery,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed || !configuration.Enabled)
            return Denied(delivery.EnvelopeId, "dad-file-courier-disabled");
        if (!IsBounded(delivery) || delivery.ExpiresAt <= DateTimeOffset.UtcNow ||
            !string.Equals(delivery.SenderIslandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal))
            return Denied(delivery.EnvelopeId, "dad-file-courier-envelope-invalid");
        var outbox = GetIslandFolder("outbox", delivery.SenderIslandId.Value);
        Directory.CreateDirectory(outbox);
        if (Directory.EnumerateFiles(outbox, "*.apout", SearchOption.TopDirectoryOnly).Take(MaximumSpoolFiles + 1).Count() >= MaximumSpoolFiles)
            return Denied(delivery.EnvelopeId, "dad-file-courier-spool-full");
        var path = Path.Combine(outbox, $"{delivery.EnvelopeId:N}.apout");
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ToPayload(delivery), JsonOptions);
        if (bytes.Length > MaximumFileBytes)
            return Denied(delivery.EnvelopeId, "dad-file-courier-envelope-too-large");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, false);
            return new(true, "dad-file-courier-enqueued", delivery.EnvelopeId);
        }
        catch (IOException) when (File.Exists(path))
        {
            return new(true, "dad-file-courier-already-enqueued", delivery.EnvelopeId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public async ValueTask AcknowledgeAsync(
        AutoPartyTransportAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed || acknowledgement.EnvelopeId == Guid.Empty ||
            !IsSafeCode(acknowledgement.SafeCode) ||
            !receivedFiles.Remove(acknowledgement.EnvelopeId, out var inboxPath))
            return;
        if (File.Exists(inboxPath))
            File.Delete(inboxPath);
        var acknowledgementFolder = GetIslandFolder("acknowledgements", configuration.RegisteredIslandId);
        Directory.CreateDirectory(acknowledgementFolder);
        var path = Path.Combine(acknowledgementFolder, $"{acknowledgement.EnvelopeId:N}.apack");
        var payload = Encoding.UTF8.GetBytes(acknowledgement.SafeCode + "\n");
        try
        {
            await File.WriteAllBytesAsync(path, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public void Dispose()
    {
        receivedFiles.Clear();
        disposed = true;
    }

    private async Task<OpaqueEnvelope> ReadEnvelopeAsync(string path, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("dad-file-courier-file-size-invalid");
        await using var stream = File.OpenRead(path);
        var payload = await JsonSerializer.DeserializeAsync<EnvelopePayload>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("dad-file-courier-payload-empty");
        return OpaqueEnvelope.Create(
            payload.EnvelopeVersion,
            payload.EnvelopeId,
            new IslandId(payload.SenderIslandId ?? string.Empty),
            new IslandId(payload.RecipientIslandId ?? string.Empty),
            payload.IssuedAt,
            payload.ExpiresAt,
            payload.Generation,
            payload.PayloadType ?? string.Empty,
            Convert.FromBase64String(payload.Ciphertext ?? string.Empty));
    }

    private void MoveToQuarantine(string path)
    {
        try
        {
            var quarantine = Path.Combine(rootPath, "quarantine");
            Directory.CreateDirectory(quarantine);
            File.Move(path, Path.Combine(quarantine, Path.GetFileName(path) + $".{Guid.NewGuid():N}.bad"), false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable hostile file remains inert and will be retried on the next bounded scan.
        }
    }

    private string GetIslandFolder(string kind, string islandId)
    {
        var islandKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(islandId)))[..32].ToLowerInvariant();
        var path = Path.GetFullPath(Path.Combine(rootPath, kind, islandKey));
        var expected = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("dad-file-courier-path-escaped");
        return path;
    }

    private static EnvelopePayload ToPayload(OpaqueEnvelope delivery) => new(
        delivery.EnvelopeVersion,
        delivery.EnvelopeId,
        delivery.SenderIslandId.Value,
        delivery.RecipientIslandId.Value,
        delivery.IssuedAt,
        delivery.ExpiresAt,
        delivery.Generation,
        delivery.PayloadType,
        Convert.ToBase64String(delivery.Ciphertext.AsSpan()));

    private static bool IsBounded(OpaqueEnvelope delivery) =>
        delivery.EnvelopeVersion == AutoPartyProtocol.CurrentVersion &&
        delivery.EnvelopeId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(delivery.SenderIslandId.Value) &&
        delivery.SenderIslandId.Value.Length <= AutoPartyProtocol.MaximumIdentifierLength &&
        !string.IsNullOrWhiteSpace(delivery.RecipientIslandId.Value) &&
        delivery.RecipientIslandId.Value.Length <= AutoPartyProtocol.MaximumIdentifierLength &&
        delivery.IssuedAt < delivery.ExpiresAt &&
        delivery.Generation > 0 &&
        delivery.PayloadLength is > 0 and <= AutoPartyProtocol.MaximumSemanticEnvelopeBytes &&
        !string.IsNullOrWhiteSpace(delivery.PayloadType) &&
        delivery.PayloadType.Length <= AutoPartyProtocol.MaximumIdentifierLength;

    private static bool IsSafeCode(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(character => character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character is '-' or '.');

    private static AutoPartyTransportHealth Health(AutoPartyTransportHealthState state, string code) =>
        new(state, code, DateTimeOffset.UtcNow);

    private static AutoPartyTransportSendResult Denied(Guid id, string code) => new(false, code, id);

    private sealed record EnvelopePayload(
        int EnvelopeVersion,
        Guid EnvelopeId,
        string? SenderIslandId,
        string? RecipientIslandId,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        long Generation,
        string? PayloadType,
        string? Ciphertext);
}
