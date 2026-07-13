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
    Notification,
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

    // B5: replay-resistance fields bound by the HMAC (see DadHubProtocol.BuildAuthPayload).
    public string Nonce { get; set; } = string.Empty;
    public long SentAtUnixMs { get; set; }
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

// B2: compact per-character roster projection that rides along the hub publish so clients can render
// peers (and the coordinator) without issuing a manual catalog pull. Fields are kept intentionally small
// to stay well under DadHubProtocol.MaxFrameBytes (256 KiB) even with a large roster.
internal sealed class DadHubRosterCatalogRow
{
    public DadWorkerSessionId OwnerWorkerSessionId { get; set; } = new(string.Empty);
    public string OwnerClientInstanceId { get; set; } = string.Empty;
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public string AccountAlias { get; set; } = string.Empty;
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong ContentId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string WorldName { get; set; } = string.Empty;
    public Dictionary<uint, int> JobLevels { get; set; } = [];
    public uint? CurrentJobId { get; set; }
    public string CurrentJobAbbrev { get; set; } = string.Empty;
    public int? CurrentLevel { get; set; }
    public DadCharacterSource Source { get; set; } = DadCharacterSource.PeerRuntime;

    public DadHubRosterCatalogRow Clone()
        => new()
        {
            OwnerWorkerSessionId = OwnerWorkerSessionId,
            OwnerClientInstanceId = OwnerClientInstanceId,
            AccountKey = AccountKey,
            AccountAlias = AccountAlias,
            CharacterKey = CharacterKey,
            ContentId = ContentId,
            CharacterName = CharacterName,
            WorldName = WorldName,
            JobLevels = new Dictionary<uint, int>(JobLevels),
            CurrentJobId = CurrentJobId,
            CurrentJobAbbrev = CurrentJobAbbrev,
            CurrentLevel = CurrentLevel,
            Source = Source,
        };
}

internal sealed class DadHubRosterPublish
{
    public long Generation { get; set; }
    public string AuthorityEpochId { get; set; } = string.Empty;
    public DateTime PublishedAtUtc { get; set; } = DateTime.UtcNow;
    public string AuthorityEndpoint { get; set; } = string.Empty;
    public DadWorkerSessionId AuthorityWorkerSessionId { get; set; } = new(string.Empty);
    public DadParticipantSnapshot CoordinatorParticipant { get; set; } = new();
    public List<DadParticipantSnapshot> ClientParticipants { get; set; } = [];
    public List<DadParticipantSnapshot> DisconnectedParticipants { get; set; } = [];
    public List<DadParticipantSnapshot> Participants { get; set; } = [];

    // B2: compact roster projection (account/character/content-id/job→level) for passive client rendering.
    public List<DadHubRosterCatalogRow> CatalogRows { get; set; } = [];

    public DadHubRosterPublish Clone()
        => new()
        {
            Generation = Generation,
            AuthorityEpochId = AuthorityEpochId,
            PublishedAtUtc = PublishedAtUtc,
            AuthorityEndpoint = AuthorityEndpoint,
            AuthorityWorkerSessionId = AuthorityWorkerSessionId,
            CoordinatorParticipant = CoordinatorParticipant.Clone(),
            ClientParticipants = ClientParticipants.Select(static participant => participant.Clone()).ToList(),
            DisconnectedParticipants = DisconnectedParticipants.Select(static participant => participant.Clone()).ToList(),
            Participants = Participants.Select(static participant => participant.Clone()).ToList(),
            CatalogRows = CatalogRows.Select(static row => row.Clone()).ToList(),
        };
}

internal readonly record struct DadHubRosterPublishCursor(
    DadWorkerSessionId AuthorityWorkerSessionId,
    string AuthorityEpochId,
    long Generation)
{
    public static DadHubRosterPublishCursor Empty { get; } = new(new DadWorkerSessionId(string.Empty), string.Empty, 0);

    public static DadHubRosterPublishCursor FromPublish(DadHubRosterPublish publish)
        => new(
            publish.AuthorityWorkerSessionId,
            NormalizeEpoch(publish.AuthorityEpochId),
            publish.Generation);

    public static bool ShouldApply(DadHubRosterPublish publish, DadHubRosterPublishCursor lastApplied)
    {
        if (publish.Generation <= 0)
            return true;

        if (!SameAuthorityWorker(publish.AuthorityWorkerSessionId, lastApplied.AuthorityWorkerSessionId) ||
            !SameEpoch(publish.AuthorityEpochId, lastApplied.AuthorityEpochId))
        {
            return true;
        }

        return publish.Generation > lastApplied.Generation;
    }

    private static bool SameAuthorityWorker(DadWorkerSessionId left, DadWorkerSessionId right)
        => string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

    private static bool SameEpoch(string? left, string? right)
        => string.Equals(NormalizeEpoch(left), NormalizeEpoch(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeEpoch(string? epoch)
        => epoch?.Trim() ?? string.Empty;
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
    // B5: bumped 1 -> 2 for the replay-resistant envelope (signed nonce + timestamp). Mixed-version peers
    // are rejected cleanly by the ValidateFrame version check instead of failing the HMAC ambiguously.
    public const int CurrentVersion = 2;
    public const int MaxFrameBytes = 256 * 1024;
    private const int HeaderBytes = sizeof(int);

    // B5: replay window + bounded seen-nonce cache for authenticated (shared-secret) frames.
    private static readonly TimeSpan ReplayWindow = TimeSpan.FromSeconds(30);
    private static readonly DadHubReplayGuard ReplayGuard = new(ReplayWindow);

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
            // B5: fresh per-frame nonce + send timestamp, both signed by ComputeAuth below.
            Nonce = Guid.NewGuid().ToString("N"),
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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
        var payload = SerializeFrame(frame);
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

    public static int GetSerializedFrameByteCount(DadHubFrame frame)
        => SerializeFrame(frame).Length;

    private static byte[] SerializeFrame(DadHubFrame frame)
        => Encoding.UTF8.GetBytes(DadIpcJson.Serialize(frame));

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
            throw new DadHubProtocolException("authentication-failed", "Shared secret mismatch");

        // B5: replay/forgery resistance only applies on authenticated (shared-secret) links. Loopback /
        // no-secret frames keep today's behavior (empty secret = no auth, no replay window).
        if (string.IsNullOrEmpty(sharedSecret))
            return;

        if (string.IsNullOrEmpty(frame.Nonce))
            throw new DadHubProtocolException("replay-detected", "Dad hub frame is missing its replay nonce.");

        var nowUtc = DateTimeOffset.UtcNow;
        var sentAt = DateTimeOffset.FromUnixTimeMilliseconds(frame.SentAtUnixMs);
        if (Math.Abs((nowUtc - sentAt).TotalMilliseconds) > ReplayWindow.TotalMilliseconds)
        {
            throw new DadHubProtocolException(
                "stale-frame",
                $"Dad hub frame timestamp is outside the {ReplayWindow.TotalSeconds:0}s replay window.");
        }

        if (!ReplayGuard.TryAccept(frame.SourceWorkerSessionId.Value ?? string.Empty, frame.Nonce, sentAt, nowUtc))
        {
            throw new DadHubProtocolException(
                "replay-detected",
                "Dad hub frame nonce was already seen; rejecting replayed envelope.");
        }
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
            frame.ErrorMessage ?? string.Empty,
            frame.Nonce ?? string.Empty,
            frame.SentAtUnixMs);

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

    // B5: bounded, thread-safe TTL set of accepted (sourceWorker, nonce) pairs. Entries expire after the
    // replay window so memory stays bounded; a duplicate nonce inside the window is rejected as a replay.
    private sealed class DadHubReplayGuard(TimeSpan window)
    {
        private const int MaxEntries = 16384;
        private readonly object gate = new();
        private readonly Dictionary<string, DateTime> seenExpiryUtc = new(StringComparer.Ordinal);

        public bool TryAccept(string sourceWorker, string nonce, DateTimeOffset sentAt, DateTimeOffset nowUtc)
        {
            if (string.IsNullOrEmpty(nonce))
                return false;

            var key = $"{sourceWorker}\u0000{nonce}";
            var expiryUtc = (sentAt + window).UtcDateTime;
            lock (gate)
            {
                PruneExpired(nowUtc.UtcDateTime);
                if (seenExpiryUtc.ContainsKey(key))
                    return false;

                if (seenExpiryUtc.Count >= MaxEntries)
                    EvictOldest(MaxEntries / 4);

                seenExpiryUtc[key] = expiryUtc;
                return true;
            }
        }

        private void PruneExpired(DateTime nowUtc)
        {
            if (seenExpiryUtc.Count == 0)
                return;

            foreach (var key in seenExpiryUtc
                         .Where(entry => entry.Value <= nowUtc)
                         .Select(entry => entry.Key)
                         .ToList())
            {
                seenExpiryUtc.Remove(key);
            }
        }

        private void EvictOldest(int count)
        {
            foreach (var key in seenExpiryUtc
                         .OrderBy(entry => entry.Value)
                         .Take(Math.Max(1, count))
                         .Select(entry => entry.Key)
                         .ToList())
            {
                seenExpiryUtc.Remove(key);
            }
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

    public static void MarkDisconnected(DadParticipantSnapshot participant, string reason)
        => MarkStale(participant, reason);

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

internal static class DadHubRosterPublishRuntime
{
    public static bool IsFresh(DadHubRosterPublish publish, DateTime nowUtc, TimeSpan staleAfter)
        => publish.PublishedAtUtc != default && nowUtc - publish.PublishedAtUtc < staleAfter;

    public static int CountPublishedParticipants(DadHubRosterPublish? publish)
        => publish == null ? 0 : EnumeratePublishedParticipants(publish).Count();

    public static List<DadParticipantSnapshot> BuildParticipantView(
        DadHubRosterPublish publish,
        DadParticipantSnapshot? localParticipant,
        DadWorkerSessionId localWorkerSessionId,
        string localClientInstanceId)
    {
        var participants = EnumeratePublishedParticipants(publish)
            .Select(static participant => participant.Clone())
            .ToList();
        var localMatched = false;

        for (var index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            var isLocal = MatchesLocal(participant, localWorkerSessionId, localClientInstanceId);
            if (isLocal)
            {
                localMatched = true;
                if (localParticipant != null)
                {
                    var replacement = localParticipant.Clone();
                    replacement.IsLocalClient = true;
                    replacement.Endpoint = string.Empty;
                    replacement.IsAuthority = string.Equals(
                        replacement.WorkerSessionId.Value,
                        publish.AuthorityWorkerSessionId.Value,
                        StringComparison.OrdinalIgnoreCase);
                    participants[index] = replacement;
                    continue;
                }
            }

            participant.IsLocalClient = false;
            if (string.Equals(
                    participant.WorkerSessionId.Value,
                    publish.AuthorityWorkerSessionId.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                participant.IsAuthority = true;
                participant.WorkerRole = DadWorkerRole.ServerDad;
                participant.Endpoint = publish.AuthorityEndpoint;
            }
            else
            {
                participant.IsAuthority = false;
                participant.Endpoint = string.Empty;
            }
        }

        if (!localMatched && localParticipant != null)
        {
            var local = localParticipant.Clone();
            local.IsLocalClient = true;
            local.Endpoint = string.Empty;
            participants.Add(local);
        }

        return participants
            .DistinctBy(BuildParticipantKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static participant => participant.ManagedAccountAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.ActiveCharacterKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<DadPeerSnapshotResponse> BuildSnapshotResponses(IEnumerable<DadParticipantSnapshot> participants)
        => participants
            .Select(static participant => new DadPeerSnapshotResponse
            {
                RespondedAtUtc = participant.LastHeartbeatUtc,
                ClientInstanceId = participant.ClientInstanceId,
                ProcessId = participant.ProcessId,
                Character = participant.Character.Clone(),
                Participant = participant.Clone(),
                XadbReady = participant.Character.XadbReady,
                Warnings = [..participant.Warnings],
            })
            .ToList();

    private static IEnumerable<DadParticipantSnapshot> EnumeratePublishedParticipants(DadHubRosterPublish publish)
    {
        if (publish.Participants.Count > 0)
        {
            foreach (var participant in publish.Participants)
                yield return participant;
            yield break;
        }

        if (!publish.CoordinatorParticipant.WorkerSessionId.IsEmpty ||
            !string.IsNullOrWhiteSpace(publish.CoordinatorParticipant.ClientInstanceId))
        {
            yield return publish.CoordinatorParticipant;
        }

        foreach (var participant in publish.ClientParticipants)
            yield return participant;
        foreach (var participant in publish.DisconnectedParticipants)
            yield return participant;
    }

    private static bool MatchesLocal(
        DadParticipantSnapshot participant,
        DadWorkerSessionId localWorkerSessionId,
        string localClientInstanceId)
        => (!participant.WorkerSessionId.IsEmpty &&
            !localWorkerSessionId.IsEmpty &&
            string.Equals(
                participant.WorkerSessionId.Value,
                localWorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase)) ||
           (!string.IsNullOrWhiteSpace(participant.ClientInstanceId) &&
            !string.IsNullOrWhiteSpace(localClientInstanceId) &&
            string.Equals(
                participant.ClientInstanceId.Trim(),
                localClientInstanceId.Trim(),
                StringComparison.OrdinalIgnoreCase));

    private static string BuildParticipantKey(DadParticipantSnapshot participant)
    {
        if (!participant.WorkerSessionId.IsEmpty)
            return $"worker:{participant.WorkerSessionId.Value}";
        if (!string.IsNullOrWhiteSpace(participant.ClientInstanceId))
            return $"client:{participant.ClientInstanceId.Trim()}";
        return $"character:{participant.ActiveCharacterKey.Value}";
    }
}
