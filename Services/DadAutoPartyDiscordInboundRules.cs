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
    internal static bool MatchesPendingIdentity(
        DadAutoPartyPairing pending,
        DadAutoPartyDiscoveredClient peer)
        => pending.ApplicationId == peer.ApplicationId &&
           pending.BotUserId == peer.BotUserId &&
           pending.KeyGeneration == peer.KeyGeneration &&
           pending.Role == peer.Role &&
           string.Equals(pending.IslandId, peer.DadIdentity, StringComparison.Ordinal) &&
           string.Equals(pending.PublicKeyFingerprint, peer.EndpointFingerprint, StringComparison.Ordinal) &&
           string.Equals(pending.SigningPublicKey, peer.SigningPublicKey, StringComparison.Ordinal);
}
