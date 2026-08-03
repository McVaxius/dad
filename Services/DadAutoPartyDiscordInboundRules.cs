using System.Security.Cryptography;
using dad.Models;

namespace dad.Services;

internal sealed record DadAutoPartyDiscordInboundMessage(
    ulong ChannelId,
    ulong GuildId,
    ulong AuthorId,
    bool AuthorIsBot,
    string Content);

internal sealed class DadAutoPartyDiscordInboundQueue
{
    internal const int DefaultCapacity = 256;
    private readonly object gate = new();
    private readonly Queue<DadAutoPartyDiscordInboundMessage> messages = [];
    private readonly int capacity;

    internal DadAutoPartyDiscordInboundQueue(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    internal int Count
    {
        get
        {
            lock (gate) return messages.Count;
        }
    }

    internal bool TryEnqueue(DadAutoPartyDiscordInboundMessage message)
    {
        lock (gate)
        {
            if (messages.Count >= capacity)
                return false;
            messages.Enqueue(message);
            return true;
        }
    }

    internal bool TryDequeue(out DadAutoPartyDiscordInboundMessage? message)
    {
        lock (gate)
        {
            if (messages.Count == 0)
            {
                message = null;
                return false;
            }

            message = messages.Dequeue();
            return true;
        }
    }

    internal int DrainAtMost(int maximumMessages, Action<DadAutoPartyDiscordInboundMessage> process)
    {
        if (maximumMessages <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumMessages));
        ArgumentNullException.ThrowIfNull(process);

        var drained = 0;
        while (drained < maximumMessages && TryDequeue(out var message))
        {
            process(message!);
            drained++;
        }
        return drained;
    }

    internal void Clear()
    {
        lock (gate) messages.Clear();
    }
}

internal readonly record struct DadAutoPartyDiscordLifecycleDecision(
    bool ObserveCompletedTask,
    bool ScheduleBlockedStop);

internal static class DadAutoPartyDiscordLifecycleRules
{
    internal static DadAutoPartyDiscordLifecycleDecision EvaluateBlocked(
        bool clientExists,
        bool lifecycleTaskExists,
        bool lifecycleTaskCompleted)
    {
        var observeCompletedTask = lifecycleTaskExists && lifecycleTaskCompleted;
        var lifecycleTaskActive = lifecycleTaskExists && !lifecycleTaskCompleted;
        return new(observeCompletedTask, clientExists && !lifecycleTaskActive);
    }

    internal static bool CanSetHealth(
        bool blockedUntilExplicitReconnect,
        DadAutoPartyDiscordConnectionState state)
        => !blockedUntilExplicitReconnect || state == DadAutoPartyDiscordConnectionState.Blocked;
}

internal static class DadAutoPartyDiscordPairingRules
{
    internal const int MaximumOutboundChallenges = 16;
    internal static readonly TimeSpan PairingChallengeLifetime = TimeSpan.FromMinutes(5);

    internal static string ComputeSigningKeyFingerprint(string signingPublicKey)
    {
        var normalized = DadAutoPartyConfiguration.NormalizePublicKey(signingPublicKey);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        byte[]? publicKey = null;
        try
        {
            publicKey = Convert.FromBase64String(normalized);
            return Convert.ToHexString(SHA256.HashData(publicKey));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
        finally
        {
            if (publicKey != null)
                CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    internal static bool OperatorConfirmedFingerprint(
        DadAutoPartyDiscoveredClient peer,
        string? confirmedSigningKeyFingerprint)
        => !string.IsNullOrWhiteSpace(peer.SigningKeyFingerprint) &&
           string.Equals(
               peer.SigningKeyFingerprint,
               DadAutoPartyConfiguration.NormalizeFingerprint(confirmedSigningKeyFingerprint),
               StringComparison.Ordinal);

    internal static DadAutoPartyOutboundPairingChallenge CreateOutboundChallenge(
        DadAutoPartyDiscoveredClient peer,
        string confirmedSigningKeyFingerprint,
        DateTime nowUtc)
    {
        if (!OperatorConfirmedFingerprint(peer, confirmedSigningKeyFingerprint))
            throw new InvalidOperationException("The operator-confirmed Discord signing-key fingerprint does not match the discovered peer.");

        nowUtc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
        return new DadAutoPartyOutboundPairingChallenge
        {
            RequestNonce = Guid.NewGuid().ToString("N"),
            ApplicationId = peer.ApplicationId,
            BotUserId = peer.BotUserId,
            IslandId = peer.DadIdentity,
            EndpointFingerprint = peer.EndpointFingerprint,
            SigningPublicKey = peer.SigningPublicKey,
            SigningKeyFingerprint = peer.SigningKeyFingerprint,
            KeyGeneration = peer.KeyGeneration,
            Role = peer.Role,
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc + PairingChallengeLifetime,
            OperatorConfirmedAtUtc = nowUtc,
        };
    }

    internal static bool MatchesActiveChallenge(
        DadAutoPartyOutboundPairingChallenge challenge,
        DadAutoPartyDiscoveredClient peer,
        string pairingRequestNonce,
        DateTime nowUtc)
        => challenge.IsValid &&
           challenge.RevokedAtUtc == null &&
           challenge.UsedAtUtc == null &&
           nowUtc < challenge.ExpiresAtUtc &&
           string.Equals(challenge.RequestNonce, pairingRequestNonce, StringComparison.Ordinal) &&
           challenge.ApplicationId == peer.ApplicationId &&
           challenge.BotUserId == peer.BotUserId &&
           challenge.KeyGeneration == peer.KeyGeneration &&
           challenge.Role == peer.Role &&
           string.Equals(challenge.IslandId, peer.DadIdentity, StringComparison.Ordinal) &&
           string.Equals(challenge.EndpointFingerprint, peer.EndpointFingerprint, StringComparison.Ordinal) &&
           string.Equals(challenge.SigningPublicKey, peer.SigningPublicKey, StringComparison.Ordinal) &&
           string.Equals(challenge.SigningKeyFingerprint, peer.SigningKeyFingerprint, StringComparison.Ordinal);

    internal static int PruneOutboundChallenges(
        List<DadAutoPartyOutboundPairingChallenge> challenges,
        DateTime nowUtc)
    {
        var removed = challenges.RemoveAll(challenge =>
            challenge == null || !challenge.IsValid || challenge.RevokedAtUtc.HasValue ||
            challenge.UsedAtUtc.HasValue || nowUtc >= challenge.ExpiresAtUtc);
        if (challenges.Count > MaximumOutboundChallenges)
        {
            var keep = challenges
                .OrderByDescending(static challenge => challenge.CreatedAtUtc)
                .Take(MaximumOutboundChallenges)
                .Select(static challenge => challenge.RequestNonce)
                .ToHashSet(StringComparer.Ordinal);
            removed += challenges.RemoveAll(challenge => !keep.Contains(challenge.RequestNonce));
        }
        return removed;
    }

    internal static bool MatchesPendingIdentity(
        DadAutoPartyPairing pending,
        DadAutoPartyDiscoveredClient peer)
        => pending.ApplicationId == peer.ApplicationId &&
           pending.BotUserId == peer.BotUserId &&
           pending.KeyGeneration == peer.KeyGeneration &&
           pending.Role == peer.Role &&
           string.Equals(pending.IslandId, peer.DadIdentity, StringComparison.Ordinal) &&
           string.Equals(pending.PublicKeyFingerprint, peer.EndpointFingerprint, StringComparison.Ordinal) &&
           string.Equals(pending.SigningPublicKey, peer.SigningPublicKey, StringComparison.Ordinal) &&
           string.Equals(pending.SigningKeyFingerprint, peer.SigningKeyFingerprint, StringComparison.Ordinal);
}

internal sealed class DadRateLimitedDiagnosticGate
{
    private readonly object gate = new();
    private readonly Dictionary<string, DateTime> nextAllowedUtc = new(StringComparer.Ordinal);

    internal bool ShouldEmit(string safeCode, DateTime nowUtc, TimeSpan interval)
    {
        lock (gate)
        {
            if (nextAllowedUtc.TryGetValue(safeCode, out var next) && nowUtc < next)
                return false;
            nextAllowedUtc[safeCode] = nowUtc + interval;
            return true;
        }
    }
}
