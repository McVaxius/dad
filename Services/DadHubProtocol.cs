using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using dad.Models;

namespace dad.Services;

internal enum DadHubFrameKind
{
    Hello,
    HelloAck,
    Heartbeat,
    Request,
    Response,
    Error,
}

internal sealed class DadHubFrame
{
    public int ProtocolVersion { get; set; } = DadHubProtocol.CurrentVersion;
    public DadHubFrameKind Kind { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DadWorkerSessionId SourceWorkerSessionId { get; set; } = new(string.Empty);
    public DadWorkerSessionId TargetWorkerSessionId { get; set; } = new(string.Empty);
    public string PayloadJson { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
}

internal sealed class DadHubHello
{
    public string ClientInstanceId { get; set; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public string BuildVersion { get; set; } = string.Empty;
    public DadParticipantSnapshot Participant { get; set; } = new();
}

internal sealed class DadHubHeartbeat
{
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
    public DadParticipantSnapshot Participant { get; set; } = new();
}

internal sealed class DadHubProtocolException : IOException
{
    public DadHubProtocolException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

internal static class DadHubProtocol
{
    public const int CurrentVersion = 1;
    public const int MaxFrameBytes = 256 * 1024;
    private const int HeaderBytes = sizeof(int);

    public static bool RequiresSharedSecret(IPAddress address)
        => !IPAddress.IsLoopback(address);

    public static void RequireSharedSecretForAddress(IPAddress address, string sharedSecret)
    {
        if (!RequiresSharedSecret(address) || !string.IsNullOrWhiteSpace(sharedSecret))
            return;

        throw new DadHubProtocolException(
            "authentication-required",
            "Dad hub shared secret is required for non-loopback connections.");
    }

    public static DadHubFrame CreateFrame(
        DadHubFrameKind kind,
        DadWorkerSessionId sourceWorkerSessionId,
        DadWorkerSessionId targetWorkerSessionId,
        string messageType,
        string correlationId,
        string payloadJson,
        string sharedSecret)
    {
        var frame = new DadHubFrame
        {
            Kind = kind,
            SourceWorkerSessionId = sourceWorkerSessionId,
            TargetWorkerSessionId = targetWorkerSessionId,
            MessageType = messageType ?? string.Empty,
            CorrelationId = correlationId ?? string.Empty,
            PayloadJson = payloadJson ?? string.Empty,
        };
        frame.Auth = ComputeAuth(frame, sharedSecret);
        return frame;
    }

    public static DadHubFrame CreateError(
        DadWorkerSessionId sourceWorkerSessionId,
        DadWorkerSessionId targetWorkerSessionId,
        string correlationId,
        string code,
        string message,
        string sharedSecret)
    {
        var frame = CreateFrame(
            DadHubFrameKind.Error,
            sourceWorkerSessionId,
            targetWorkerSessionId,
            "error",
            correlationId,
            string.Empty,
            sharedSecret);
        frame.ErrorCode = code;
        frame.ErrorMessage = message;
        frame.Auth = ComputeAuth(frame, sharedSecret);
        return frame;
    }

    public static string ComputeAuth(DadHubFrame frame, string sharedSecret)
    {
        if (string.IsNullOrEmpty(sharedSecret))
            return string.Empty;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sharedSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(BuildAuthPayload(frame)));
        return Convert.ToBase64String(hash);
    }

    public static bool VerifyAuth(DadHubFrame frame, string sharedSecret)
    {
        if (string.IsNullOrEmpty(sharedSecret))
            return string.IsNullOrEmpty(frame.Auth);

        var expected = ComputeAuth(frame, sharedSecret);
        var actualBytes = Encoding.UTF8.GetBytes(frame.Auth ?? string.Empty);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    public static async Task WriteFrameAsync(Stream stream, DadHubFrame frame, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(DadIpcJson.Serialize(frame));
        if (payload.Length > MaxFrameBytes)
        {
            throw new DadHubProtocolException(
                "frame-too-large",
                $"Dad hub frame is {payload.Length} bytes; maximum is {MaxFrameBytes}.");
        }

        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<DadHubFrame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderBytes];
        var headerRead = await ReadExactlyOrEofAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (!headerRead)
            return null;

        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(header);
        if (payloadLength <= 0 || payloadLength > MaxFrameBytes)
        {
            throw new DadHubProtocolException(
                "frame-too-large",
                $"Dad hub frame length {payloadLength} is invalid; maximum is {MaxFrameBytes}.");
        }

        var payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        var frame = DadIpcJson.Deserialize<DadHubFrame>(Encoding.UTF8.GetString(payload));
        return frame ?? throw new DadHubProtocolException("invalid-frame", "Dad hub frame JSON is invalid.");
    }

    public static void ValidateFrame(DadHubFrame frame, string sharedSecret)
    {
        if (frame.ProtocolVersion != CurrentVersion)
        {
            throw new DadHubProtocolException(
                "protocol-mismatch",
                $"Dad hub protocol {frame.ProtocolVersion} is incompatible; expected {CurrentVersion}.");
        }

        if (!VerifyAuth(frame, sharedSecret))
            throw new DadHubProtocolException("authentication-failed", "Dad hub shared secret is missing or incorrect.");
    }

    private static string BuildAuthPayload(DadHubFrame frame)
        => string.Join(
            "\n",
            frame.ProtocolVersion,
            frame.Kind,
            frame.MessageType ?? string.Empty,
            frame.CorrelationId ?? string.Empty,
            frame.SourceWorkerSessionId.Value ?? string.Empty,
            frame.TargetWorkerSessionId.Value ?? string.Empty,
            frame.PayloadJson ?? string.Empty,
            frame.ErrorCode ?? string.Empty,
            frame.ErrorMessage ?? string.Empty);

    private static async Task<bool> ReadExactlyOrEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (totalRead == 0)
                    return false;

                throw new EndOfStreamException("Dad hub frame header ended early.");
            }

            totalRead += read;
        }

        return true;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Dad hub frame payload ended early.");

            totalRead += read;
        }
    }
}

internal sealed class DadHubSessionRegistry<T> where T : class
{
    private readonly ConcurrentDictionary<string, T> sessions = new(StringComparer.OrdinalIgnoreCase);

    public int Count => sessions.Count;

    public IReadOnlyList<T> Snapshot()
        => sessions.Values.ToList();

    public T? Register(DadWorkerSessionId workerSessionId, T session)
    {
        T? replaced = null;
        sessions.AddOrUpdate(
            workerSessionId.Value,
            _ => session,
            (_, existing) =>
            {
                replaced = existing;
                return session;
            });
        return replaced;
    }

    public bool TryGet(DadWorkerSessionId workerSessionId, out T? session)
        => sessions.TryGetValue(workerSessionId.Value, out session);

    public bool RemoveIfCurrent(DadWorkerSessionId workerSessionId, T session)
    {
        if (!sessions.TryGetValue(workerSessionId.Value, out var current) ||
            !ReferenceEquals(current, session))
        {
            return false;
        }

        return sessions.TryRemove(new KeyValuePair<string, T>(workerSessionId.Value, session));
    }

    public void Clear() => sessions.Clear();
}

internal static class DadHubParticipants
{
    public static DadParticipantSnapshot PrepareRemote(
        DadParticipantSnapshot participant,
        DateTime heartbeatUtc)
    {
        var clone = participant.Clone();
        clone.Endpoint = string.Empty;
        clone.IsLocalClient = false;
        clone.LastHeartbeatUtc = heartbeatUtc;
        return clone;
    }

    public static DadParticipantSnapshot PrepareRemoteWithStaleState(
        DadParticipantSnapshot participant,
        DateTime heartbeatUtc,
        DateTime nowUtc,
        TimeSpan staleAfter,
        string staleReason)
        => PrepareRemoteWithStaleState(
            participant,
            heartbeatUtc,
            heartbeatUtc,
            nowUtc,
            staleAfter,
            staleReason);

    public static DadParticipantSnapshot PrepareRemoteWithStaleState(
        DadParticipantSnapshot participant,
        DateTime heartbeatUtc,
        DateTime staleSinceUtc,
        DateTime nowUtc,
        TimeSpan staleAfter,
        string staleReason)
    {
        var clone = PrepareRemote(participant, heartbeatUtc);
        if (IsStale(nowUtc, staleSinceUtc, staleAfter))
            MarkStale(clone, staleReason);
        return clone;
    }

    public static bool IsStale(DateTime nowUtc, DateTime heartbeatUtc, TimeSpan staleAfter)
        => nowUtc - heartbeatUtc >= staleAfter;

    private static void MarkStale(DadParticipantSnapshot participant, string reason)
    {
        participant.State = DadParticipantState.Stale;
        participant.ClaimState = DadClaimState.Stale;
        participant.LeaseState = DadParticipantLeaseState.Stale;
        participant.IsAvailable = false;
        participant.IsEligibleForRun = false;
        participant.StatusText = reason;
        participant.Character.Readiness = DadReadinessState.Stale;
        if (participant.Warnings.All(warning => !string.Equals(warning, reason, StringComparison.OrdinalIgnoreCase)))
            participant.Warnings.Add(reason);
    }
}
