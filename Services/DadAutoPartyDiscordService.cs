using Discord;
using Discord.WebSocket;
using dad.Models;

namespace dad.Services;

public sealed class DadAutoPartyDiscordService : IDisposable
{
    private static readonly TimeSpan PresenceInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PresenceStaleAfter = TimeSpan.FromMinutes(3);
    private readonly DadAutoPartyConfiguration configuration;
    private readonly IDadAutoPartyDiscordTokenStore tokenStore;
    private readonly DadAutoPartyPairingProtocol protocol;
    private readonly DadAutoPartySigningService signing;
    private readonly Func<bool> isCoordinator;
    private readonly Action saveConfiguration;
    private readonly Action<string> diagnostic;
    private readonly DadDiscordReconnectBackoff reconnectBackoff = new();
    private readonly object discoveredGate = new();
    private readonly Dictionary<ulong, DadAutoPartyDiscoveredClient> discovered = [];
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private DiscordSocketClient? client;
    private Task? lifecycleTask;
    private DateTime nextPresenceUtc = DateTime.MinValue;
    private DateTime nextReconnectUtc = DateTime.MinValue;
    private bool restartRequested;
    private bool disposed;
    private DadAutoPartyDiscordHealth health = new(
        DadAutoPartyDiscordConnectionState.Disabled,
        "dad-discord-disabled",
        DateTime.UtcNow,
        null,
        0,
        0,
        false);

    public DadAutoPartyDiscordService(
        DadAutoPartyConfiguration configuration,
        IDadAutoPartyDiscordTokenStore tokenStore,
        DadAutoPartyPairingProtocol protocol,
        DadAutoPartySigningService signing,
        Func<bool> isCoordinator,
        Action saveConfiguration,
        Action<string>? diagnostic = null)
    {
        this.configuration = configuration;
        this.tokenStore = tokenStore;
        this.protocol = protocol;
        this.signing = signing;
        this.isCoordinator = isCoordinator;
        this.saveConfiguration = saveConfiguration;
        this.diagnostic = diagnostic ?? (_ => { });
    }

    public DadAutoPartyDiscordHealth Health => health;
    public event Action<ulong>? PairingRevoked;
    public event Action<ulong>? PairingRestored;

    public IReadOnlyList<DadAutoPartyDiscoveredClient> GetDiscoveredClients()
    {
        lock (discoveredGate)
        {
            var now = DateTime.UtcNow;
            return discovered.Values
                .Select(item => item with
                {
                    PairingHealth = now - item.LastSeenUtc > PresenceStaleAfter
                        ? DadAutoPartyPairingHealth.Stale
                        : item.PairingHealth,
                })
                .OrderBy(static item => item.Role)
                .ThenBy(static item => item.ApplicationId)
                .ToList();
        }
    }

    public DadAutoPartyLanPresence GetLanPresence()
    {
        if (!configuration.DiscordEnabled)
            return new DadAutoPartyLanPresence();
        var pairingHealth = health.State switch
        {
            DadAutoPartyDiscordConnectionState.Ready when configuration.Pairings.Any(IsActiveVerifiedPairing)
                => DadAutoPartyPairingHealth.Healthy,
            DadAutoPartyDiscordConnectionState.Ready => DadAutoPartyPairingHealth.Unpaired,
            DadAutoPartyDiscordConnectionState.Stale => DadAutoPartyPairingHealth.Stale,
            DadAutoPartyDiscordConnectionState.Blocked => DadAutoPartyPairingHealth.Blocked,
            _ => DadAutoPartyPairingHealth.Disabled,
        };
        return new(configuration.DiscordApplicationId, configuration.RegistrationFingerprint, pairingHealth);
    }

    public IReadOnlyList<ulong> GetHealthyPairedApplicationIds()
    {
        var now = DateTime.UtcNow;
        lock (discoveredGate)
        {
            var peers = configuration.Pairings.Where(IsActiveVerifiedPairing)
                .Where(pairing => discovered.TryGetValue(pairing.ApplicationId, out var item) &&
                                  now - item.LastSeenUtc <= PresenceStaleAfter &&
                                  item.PairingHealth == DadAutoPartyPairingHealth.Healthy)
                .Select(static pairing => pairing.ApplicationId);
            return peers.Append(configuration.DiscordApplicationId).Where(static id => id != 0)
                .Distinct().Order().ToList();
        }
    }

    public string GetZeroPermissionInviteLink()
        => configuration.DiscordApplicationId == 0
            ? string.Empty
            : $"https://discord.com/oauth2/authorize?client_id={configuration.DiscordApplicationId}&permissions=0&scope=bot";

    public IReadOnlyList<string> GetBlockers()
    {
        var blockers = new List<string>();
        if (string.IsNullOrWhiteSpace(configuration.EndpointIdentityReference)) blockers.Add("Generate a DAD endpoint identity.");
        if (string.IsNullOrWhiteSpace(configuration.DiscordTokenReference)) blockers.Add("Save this bot's token.");
        if (configuration.DiscordGuildId == 0) blockers.Add("Enter the private Discord server Guild ID.");
        if (configuration.DiscordChannelId == 0) blockers.Add("Enter the private #dad-pairing Channel ID.");
        if (!health.PermissionsValid) blockers.Add("Grant View Channel, Send Messages, and Read Message History in #dad-pairing.");
        if (health.State == DadAutoPartyDiscordConnectionState.Blocked) blockers.Add(health.SafeCode);
        return blockers;
    }

    public async ValueTask<DadAutoPartyPolicyDecision> SaveAndConnectAsync(
        ReadOnlyMemory<char> token,
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (guildId == 0 || channelId == 0 || token.IsEmpty)
            return Decision(false, "dad-discord-settings-invalid");
        if (string.IsNullOrWhiteSpace(configuration.EndpointIdentityReference))
            return Decision(false, "dad-discord-endpoint-identity-required");
        var oldReference = configuration.DiscordTokenReference;
        var newReference = await tokenStore.StoreAsync(token, cancellationToken).ConfigureAwait(false);
        configuration.DiscordTokenReference = newReference;
        configuration.DiscordGuildId = guildId;
        configuration.DiscordChannelId = channelId;
        configuration.DiscordEnabled = true;
        configuration.StateGeneration++;
        saveConfiguration();
        if (!string.IsNullOrWhiteSpace(oldReference) && !string.Equals(oldReference, newReference, StringComparison.Ordinal))
            await tokenStore.DeleteAsync(oldReference, cancellationToken).ConfigureAwait(false);
        await RestartNowAsync(cancellationToken).ConfigureAwait(false);
        return Decision(true, "dad-discord-connect-started");
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        configuration.DiscordEnabled = false;
        configuration.StateGeneration++;
        saveConfiguration();
        await StopClientAsync(cancellationToken).ConfigureAwait(false);
        SetHealth(DadAutoPartyDiscordConnectionState.Disabled, "dad-discord-disconnected", false, null);
    }

    public async ValueTask ForgetTokenAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var tokenReference = configuration.DiscordTokenReference;
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(tokenReference))
            await tokenStore.DeleteAsync(tokenReference, cancellationToken).ConfigureAwait(false);
        configuration.DiscordTokenReference = string.Empty;
        configuration.DiscordApplicationId = 0;
        configuration.DiscordBotUserId = 0;
        configuration.DiscordPresenceMessageId = 0;
        configuration.DiscordBinding = new DadAutoPartyDiscordBinding();
        configuration.StateGeneration++;
        saveConfiguration();
    }

    public void Update(bool dadEnabled)
    {
        ThrowIfDisposed();
        if (!dadEnabled || !configuration.DiscordEnabled)
            return;
        if (lifecycleTask is { IsCompleted: true })
        {
            _ = lifecycleTask.Exception;
            lifecycleTask = null;
        }
        var now = DateTime.UtcNow;
        if (health.LastPresenceAtUtc.HasValue && now - health.LastPresenceAtUtc.Value > PresenceStaleAfter &&
            health.State == DadAutoPartyDiscordConnectionState.Ready)
            SetHealth(DadAutoPartyDiscordConnectionState.Stale, "dad-discord-presence-stale", health.PermissionsValid, health.LastPresenceAtUtc);
        if ((client == null || restartRequested) && lifecycleTask == null && now >= nextReconnectUtc)
        {
            restartRequested = false;
            lifecycleTask = RestartNowAsync(CancellationToken.None);
        }
        else if (client?.ConnectionState == ConnectionState.Connected && lifecycleTask == null && now >= nextPresenceUtc)
        {
            nextPresenceUtc = now + PresenceInterval;
            lifecycleTask = PublishPresenceAsync(CancellationToken.None);
        }
    }

    public async ValueTask<DadAutoPartyPolicyDecision> PairAsync(ulong applicationId, CancellationToken cancellationToken = default)
    {
        var peer = FindDiscovered(applicationId);
        if (peer == null) return Decision(false, "dad-discord-peer-not-discovered");
        if (peer.Role == (isCoordinator() ? DadAutoPartyRole.Coordinator : DadAutoPartyRole.Client))
            return Decision(false, "dad-discord-coordinator-star-required");
        return await SendEnvelopeAsync(DadAutoPartyPairingMessageKind.PairRequest, peer, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DadAutoPartyPolicyDecision> AcceptAsync(ulong applicationId, CancellationToken cancellationToken = default)
    {
        var pending = configuration.PendingPairings.FirstOrDefault(pairing => pairing.ApplicationId == applicationId);
        var peer = FindDiscovered(applicationId);
        if (pending == null || peer == null) return Decision(false, "dad-discord-pair-request-not-pending");
        SavePairing(peer);
        configuration.PendingPairings.RemoveAll(pairing => pairing.ApplicationId == applicationId);
        configuration.StateGeneration++;
        saveConfiguration();
        PairingRestored?.Invoke(applicationId);
        return await SendEnvelopeAsync(DadAutoPartyPairingMessageKind.PairAccept, peer, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DadAutoPartyPolicyDecision> RejectAsync(ulong applicationId, CancellationToken cancellationToken = default)
    {
        var peer = FindDiscovered(applicationId);
        configuration.PendingPairings.RemoveAll(pairing => pairing.ApplicationId == applicationId);
        configuration.StateGeneration++;
        saveConfiguration();
        return peer == null
            ? Decision(true, "dad-discord-pair-request-rejected")
            : await SendEnvelopeAsync(DadAutoPartyPairingMessageKind.PairReject, peer, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DadAutoPartyPolicyDecision> RevokeAsync(ulong applicationId, CancellationToken cancellationToken = default)
    {
        var pairing = configuration.Pairings.FirstOrDefault(candidate => candidate.ApplicationId == applicationId);
        if (pairing == null) return Decision(true, "dad-discord-pairing-already-absent");
        pairing.RevokedAtUtc = DateTime.UtcNow;
        configuration.StateGeneration++;
        saveConfiguration();
        PairingRevoked?.Invoke(applicationId);
        var peer = FindDiscovered(applicationId);
        return peer == null
            ? Decision(true, "dad-discord-pairing-revoked")
            : await SendEnvelopeAsync(DadAutoPartyPairingMessageKind.Revoke, peer, cancellationToken).ConfigureAwait(false);
    }

    private async Task RestartNowAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopClientCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!configuration.DiscordEnabled || string.IsNullOrWhiteSpace(configuration.DiscordTokenReference))
                return;
            SetHealth(DadAutoPartyDiscordConnectionState.Connecting, "dad-discord-connecting", false, health.LastPresenceAtUtc);
            var socket = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
                AlwaysDownloadUsers = false,
                MessageCacheSize = 100,
                LogGatewayIntentWarnings = false,
            });
            socket.Ready += OnReadyAsync;
            socket.MessageReceived += OnMessageReceivedAsync;
            socket.MessageUpdated += OnMessageUpdatedAsync;
            socket.Disconnected += OnDisconnectedAsync;
            client = socket;
            var tokenCharacters = await tokenStore.LoadAsync(configuration.DiscordTokenReference, cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.LoginAsync(TokenType.Bot, new string(tokenCharacters)).ConfigureAwait(false);
                await socket.StartAsync().ConfigureAwait(false);
            }
            finally
            {
                Array.Clear(tokenCharacters);
            }
        }
        catch (Exception)
        {
            diagnostic("dad-discord-connect-failed");
            SetHealth(DadAutoPartyDiscordConnectionState.Disconnected, "dad-discord-connect-failed", false, health.LastPresenceAtUtc);
            restartRequested = true;
            nextReconnectUtc = DateTime.UtcNow + reconnectBackoff.NextDelay();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task OnReadyAsync()
    {
        var socket = client;
        if (socket == null) return;
        try
        {
            var application = await socket.GetApplicationInfoAsync().ConfigureAwait(false);
            var applicationId = application.Id;
            var botUserId = socket.CurrentUser.Id;
            var guild = socket.GetGuild(configuration.DiscordGuildId);
            var channel = guild?.GetTextChannel(configuration.DiscordChannelId);
            if (guild == null || channel == null || channel.Guild.Id != configuration.DiscordGuildId)
            {
                Block("dad-discord-channel-not-found");
                return;
            }
            var permissions = guild.CurrentUser.GetPermissions(channel);
            if (!permissions.ViewChannel || !permissions.SendMessages || !permissions.ReadMessageHistory)
            {
                Block("dad-discord-channel-permissions-missing");
                return;
            }
            var binding = configuration.DiscordBinding;
            if (binding.IsComplete && (binding.ApplicationId != applicationId || binding.BotUserId != botUserId ||
                !string.Equals(binding.DadIdentity, configuration.RegisteredIslandId, StringComparison.Ordinal) ||
                !string.Equals(binding.EndpointFingerprint, configuration.RegistrationFingerprint, StringComparison.Ordinal)))
            {
                Block("dad-discord-authenticated-binding-changed");
                return;
            }
            configuration.DiscordApplicationId = applicationId;
            configuration.DiscordBotUserId = botUserId;
            configuration.DiscordBinding = new DadAutoPartyDiscordBinding
            {
                ApplicationId = applicationId,
                BotUserId = botUserId,
                DadIdentity = configuration.RegisteredIslandId,
                EndpointFingerprint = configuration.RegistrationFingerprint,
                KeyGeneration = Math.Max(1, binding.KeyGeneration),
            };
            configuration.StateGeneration++;
            saveConfiguration();
            reconnectBackoff.Reset();
            nextPresenceUtc = DateTime.MinValue;
            SetHealth(DadAutoPartyDiscordConnectionState.Ready, "dad-discord-ready", true, DateTime.UtcNow);
            await PublishPresenceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Block("dad-discord-ready-validation-failed");
        }
    }

    private Task OnMessageUpdatedAsync(Cacheable<IMessage, ulong> _, SocketMessage message, ISocketMessageChannel __)
        => OnMessageReceivedAsync(message);

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message.Channel.Id != configuration.DiscordChannelId || message.Author.Id == 0 || !message.Author.IsBot ||
            message.Channel is not SocketGuildChannel guildChannel || guildChannel.Guild.Id != configuration.DiscordGuildId)
            return;
        var envelope = DadAutoPartyPairingProtocol.Deserialize(message.Content);
        var decision = protocol.Validate(
            envelope,
            message.Author.Id,
            DateTime.UtcNow,
            isCoordinator() ? DadAutoPartyRole.Coordinator : DadAutoPartyRole.Client);
        if (!decision.Allowed || envelope == null)
            return;
        if (envelope.ApplicationId == configuration.DiscordApplicationId)
            return;
        var known = configuration.Pairings.FirstOrDefault(pairing => pairing.ApplicationId == envelope.ApplicationId);
        var drifted = known != null && (known.BotUserId != envelope.BotUserId || known.Role != envelope.Role ||
            !string.Equals(known.IslandId, envelope.DadIdentity, StringComparison.Ordinal) ||
            !string.Equals(known.PublicKeyFingerprint, envelope.EndpointFingerprint, StringComparison.Ordinal) ||
            !string.Equals(known.SigningPublicKey, envelope.SigningPublicKey, StringComparison.Ordinal));
        var pairingHealth = drifted ? DadAutoPartyPairingHealth.Blocked :
            known?.RevokedAtUtc != null ? DadAutoPartyPairingHealth.Revoked :
            known != null ? DadAutoPartyPairingHealth.Healthy : DadAutoPartyPairingHealth.Unpaired;
        var discoveredPeer = new DadAutoPartyDiscoveredClient(
            envelope.ApplicationId, envelope.BotUserId, envelope.DadIdentity, envelope.EndpointFingerprint,
            envelope.SigningPublicKey, envelope.KeyGeneration, envelope.Role, DateTime.UtcNow,
            pairingHealth, drifted ? "dad-discord-paired-identity-changed" : string.Empty);
        lock (discoveredGate) discovered[envelope.ApplicationId] = discoveredPeer;
        if (drifted || (envelope.TargetApplicationId != 0 && envelope.TargetApplicationId != configuration.DiscordApplicationId) ||
            (!string.IsNullOrWhiteSpace(envelope.TargetDadIdentity) &&
             !string.Equals(envelope.TargetDadIdentity, configuration.RegisteredIslandId, StringComparison.Ordinal)))
            return;
        switch (envelope.Kind)
        {
            case DadAutoPartyPairingMessageKind.PairRequest:
                SavePending(discoveredPeer);
                break;
            case DadAutoPartyPairingMessageKind.PairAccept:
                SavePairing(discoveredPeer);
                configuration.PendingPairings.RemoveAll(pairing => pairing.ApplicationId == envelope.ApplicationId);
                configuration.StateGeneration++;
                saveConfiguration();
                PairingRestored?.Invoke(envelope.ApplicationId);
                break;
            case DadAutoPartyPairingMessageKind.PairReject:
                configuration.PendingPairings.RemoveAll(pairing => pairing.ApplicationId == envelope.ApplicationId);
                configuration.StateGeneration++;
                saveConfiguration();
                break;
            case DadAutoPartyPairingMessageKind.Revoke:
                if (known != null)
                {
                    known.RevokedAtUtc = DateTime.UtcNow;
                    configuration.StateGeneration++;
                    saveConfiguration();
                    PairingRevoked?.Invoke(envelope.ApplicationId);
                }
                break;
        }
        await Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(Exception _)
    {
        if (disposed || !configuration.DiscordEnabled)
            return Task.CompletedTask;
        SetHealth(DadAutoPartyDiscordConnectionState.Disconnected, "dad-discord-disconnected-retrying", false, health.LastPresenceAtUtc);
        restartRequested = true;
        nextReconnectUtc = DateTime.UtcNow + reconnectBackoff.NextDelay();
        return Task.CompletedTask;
    }

    private async Task PublishPresenceAsync(CancellationToken cancellationToken)
    {
        var socket = client;
        var channel = socket?.GetGuild(configuration.DiscordGuildId)?.GetTextChannel(configuration.DiscordChannelId);
        if (socket == null || channel == null || socket.ConnectionState != ConnectionState.Connected)
            return;
        var envelope = await protocol.CreateAsync(
            DadAutoPartyPairingMessageKind.Presence,
            isCoordinator() ? DadAutoPartyRole.Coordinator : DadAutoPartyRole.Client,
            configuration,
            signing,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var content = DadAutoPartyPairingProtocol.Serialize(envelope);
        IUserMessage? presence = null;
        if (configuration.DiscordPresenceMessageId != 0)
            presence = await channel.GetMessageAsync(configuration.DiscordPresenceMessageId).ConfigureAwait(false) as IUserMessage;
        if (presence != null && presence.Author.Id == configuration.DiscordBotUserId)
            await presence.ModifyAsync(properties => properties.Content = content).ConfigureAwait(false);
        else
        {
            presence = await channel.SendMessageAsync(content).ConfigureAwait(false);
            configuration.DiscordPresenceMessageId = presence.Id;
            configuration.StateGeneration++;
            saveConfiguration();
        }
        SetHealth(DadAutoPartyDiscordConnectionState.Ready, "dad-discord-ready", true, DateTime.UtcNow);
    }

    private async ValueTask<DadAutoPartyPolicyDecision> SendEnvelopeAsync(
        DadAutoPartyPairingMessageKind kind,
        DadAutoPartyDiscoveredClient peer,
        CancellationToken cancellationToken)
    {
        var socket = client;
        var channel = socket?.GetGuild(configuration.DiscordGuildId)?.GetTextChannel(configuration.DiscordChannelId);
        if (socket == null || channel == null || health.State != DadAutoPartyDiscordConnectionState.Ready)
            return Decision(false, "dad-discord-not-ready");
        var envelope = await protocol.CreateAsync(
            kind,
            isCoordinator() ? DadAutoPartyRole.Coordinator : DadAutoPartyRole.Client,
            configuration,
            signing,
            peer.ApplicationId,
            peer.DadIdentity,
            cancellationToken).ConfigureAwait(false);
        await channel.SendMessageAsync(DadAutoPartyPairingProtocol.Serialize(envelope)).ConfigureAwait(false);
        return Decision(true, $"dad-discord-{kind.ToString().ToLowerInvariant()}-sent");
    }

    private void SavePending(DadAutoPartyDiscoveredClient peer)
    {
        configuration.PendingPairings.RemoveAll(pairing => pairing.ApplicationId == peer.ApplicationId);
        configuration.PendingPairings.Add(ToPairing(peer, DateTime.UtcNow));
        configuration.StateGeneration++;
        saveConfiguration();
    }

    private void SavePairing(DadAutoPartyDiscoveredClient peer)
    {
        configuration.Pairings.RemoveAll(pairing => pairing.ApplicationId == peer.ApplicationId ||
            string.Equals(pairing.IslandId, peer.DadIdentity, StringComparison.Ordinal));
        configuration.Pairings.Add(ToPairing(peer, DateTime.UtcNow));
        configuration.StateGeneration++;
        saveConfiguration();
        lock (discoveredGate) discovered[peer.ApplicationId] = peer with { PairingHealth = DadAutoPartyPairingHealth.Healthy };
    }

    private static DadAutoPartyPairing ToPairing(DadAutoPartyDiscoveredClient peer, DateTime confirmedAtUtc) => new()
    {
        OwnerId = "discord",
        IslandId = peer.DadIdentity,
        PublicKeyFingerprint = peer.EndpointFingerprint,
        SigningPublicKey = peer.SigningPublicKey,
        KeyGeneration = peer.KeyGeneration,
        ApplicationId = peer.ApplicationId,
        BotUserId = peer.BotUserId,
        Role = peer.Role,
        ConfirmedAtUtc = confirmedAtUtc,
    };

    private DadAutoPartyDiscoveredClient? FindDiscovered(ulong applicationId)
    {
        lock (discoveredGate) return discovered.GetValueOrDefault(applicationId);
    }

    private static bool IsActiveVerifiedPairing(DadAutoPartyPairing pairing)
        => pairing.RevokedAtUtc == null && pairing.ApplicationId != 0 &&
           !string.IsNullOrWhiteSpace(pairing.SigningPublicKey);

    private void Block(string safeCode)
    {
        diagnostic(safeCode);
        SetHealth(DadAutoPartyDiscordConnectionState.Blocked, safeCode, false, health.LastPresenceAtUtc);
    }

    private void SetHealth(
        DadAutoPartyDiscordConnectionState state,
        string safeCode,
        bool permissionsValid,
        DateTime? lastPresenceUtc)
        => health = new(state, safeCode, DateTime.UtcNow, lastPresenceUtc,
            configuration.DiscordApplicationId, configuration.DiscordBotUserId, permissionsValid);

    private async Task StopClientAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopClientCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { lifecycleGate.Release(); }
    }

    private async Task StopClientCoreAsync(CancellationToken cancellationToken)
    {
        var socket = client;
        client = null;
        if (socket == null) return;
        socket.Ready -= OnReadyAsync;
        socket.MessageReceived -= OnMessageReceivedAsync;
        socket.MessageUpdated -= OnMessageUpdatedAsync;
        socket.Disconnected -= OnDisconnectedAsync;
        try
        {
            if (socket.ConnectionState != ConnectionState.Disconnected)
                await socket.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            await socket.LogoutAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            diagnostic("dad-discord-stop-incomplete");
        }
        socket.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { StopClientAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception) { diagnostic("dad-discord-dispose-incomplete"); }
        lifecycleGate.Dispose();
    }

    private DadAutoPartyPolicyDecision Decision(bool allowed, string safeCode)
        => new(allowed, safeCode, Math.Max(1, configuration.StateGeneration));
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
