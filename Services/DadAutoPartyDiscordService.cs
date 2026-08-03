using Discord;
using Discord.WebSocket;
using dad.Models;

namespace dad.Services;

public sealed class DadAutoPartyDiscordService : IDisposable
{
    private static readonly TimeSpan PresenceInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PresenceStaleAfter = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DiagnosticInterval = TimeSpan.FromMinutes(1);
    private const int MaximumInboundMessagesPerUpdate = 8;
    private readonly DadAutoPartyConfiguration configuration;
    private readonly IDadAutoPartyDiscordTokenStore tokenStore;
    private readonly DadAutoPartyPairingProtocol protocol;
    private readonly DadAllianceDiscordProtocol allianceProtocol;
    private readonly DadAutoPartySigningService signing;
    private readonly Func<bool> isCoordinator;
    private readonly Func<DadCharacterKey> localCharacterKey;
    private readonly Action saveConfiguration;
    private readonly Action<string> diagnostic;
    private readonly DadDiscordReconnectBackoff reconnectBackoff = new();
    private readonly object discoveredGate = new();
    private readonly Dictionary<ulong, DadAutoPartyDiscoveredClient> discovered = [];
    private readonly DadAutoPartyDiscordInboundQueue inboundMessages = new();
    private readonly DadRateLimitedDiagnosticGate diagnosticGate = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private DiscordSocketClient? client;
    private Task? lifecycleTask;
    private DateTime nextPresenceUtc = DateTime.MinValue;
    private DateTime nextReconnectUtc = DateTime.MinValue;
    private bool restartRequested;
    private volatile bool blockedUntilExplicitReconnect;
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
        DadAllianceDiscordProtocol allianceProtocol,
        DadAutoPartySigningService signing,
        Func<bool> isCoordinator,
        Func<DadCharacterKey> localCharacterKey,
        Action saveConfiguration,
        Action<string>? diagnostic = null)
    {
        this.configuration = configuration;
        this.tokenStore = tokenStore;
        this.protocol = protocol;
        this.allianceProtocol = allianceProtocol;
        this.signing = signing;
        this.isCoordinator = isCoordinator;
        this.localCharacterKey = localCharacterKey;
        this.saveConfiguration = saveConfiguration;
        this.diagnostic = diagnostic ?? (_ => { });
    }

    public DadAutoPartyDiscordHealth Health => health;
    public event Action<ulong>? PairingRevoked;
    public event Action<ulong>? PairingRestored;
    public event Action<DadAllianceRecruitmentInstructionDto>? AllianceRecruitmentReceived;

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
        blockedUntilExplicitReconnect = false;
        inboundMessages.Clear();
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
        var observedLifecycleTask = lifecycleTask;
        var blockedLifecycleDecision = DadAutoPartyDiscordLifecycleRules.EvaluateBlocked(
            client != null,
            observedLifecycleTask != null,
            observedLifecycleTask?.IsCompleted == true);
        if (blockedLifecycleDecision.ObserveCompletedTask)
        {
            _ = observedLifecycleTask!.Exception;
            lifecycleTask = null;
        }
        if (blockedUntilExplicitReconnect)
        {
            inboundMessages.Clear();
            if (blockedLifecycleDecision.ScheduleBlockedStop)
                lifecycleTask = StopClientAsync(CancellationToken.None);
            return;
        }
        if (!dadEnabled || !configuration.DiscordEnabled)
        {
            inboundMessages.Clear();
            return;
        }
        DrainInboundMessages();
        var now = DateTime.UtcNow;
        if (DadAutoPartyDiscordPairingRules.PruneOutboundChallenges(
                configuration.OutboundPairingChallenges,
                now) > 0)
        {
            configuration.StateGeneration++;
            saveConfiguration();
        }
        if (health.LastPresenceAtUtc.HasValue && now - health.LastPresenceAtUtc.Value > PresenceStaleAfter &&
            health.State == DadAutoPartyDiscordConnectionState.Ready)
            SetHealth(DadAutoPartyDiscordConnectionState.Stale, "dad-discord-presence-stale", health.PermissionsValid, health.LastPresenceAtUtc);
        if (!blockedUntilExplicitReconnect &&
            (client == null || restartRequested) && lifecycleTask == null && now >= nextReconnectUtc)
        {
            restartRequested = false;
            lifecycleTask = RestartNowAsync(CancellationToken.None);
        }
        else if (!blockedUntilExplicitReconnect &&
                 client?.ConnectionState == ConnectionState.Connected && lifecycleTask == null && now >= nextPresenceUtc)
        {
            nextPresenceUtc = now + PresenceInterval;
            lifecycleTask = PublishPresenceAsync(CancellationToken.None);
        }
    }

    public async ValueTask<DadAutoPartyPolicyDecision> PairAsync(
        ulong applicationId,
        string confirmedSigningKeyFingerprint,
        CancellationToken cancellationToken = default)
    {
        var peer = FindDiscovered(applicationId);
        if (peer == null) return Decision(false, "dad-discord-peer-not-discovered");
        var existing = configuration.Pairings.FirstOrDefault(pairing => pairing.ApplicationId == applicationId);
        if (existing != null && IsActiveVerifiedPairing(existing))
            return Decision(true, "dad-discord-peer-already-paired");
        if (peer.Role == (isCoordinator() ? DadAutoPartyRole.Coordinator : DadAutoPartyRole.Client))
            return Decision(false, "dad-discord-coordinator-star-required");
        if (!DadAutoPartyDiscordPairingRules.OperatorConfirmedFingerprint(peer, confirmedSigningKeyFingerprint))
            return Decision(false, "dad-discord-signing-fingerprint-confirmation-required");

        var now = DateTime.UtcNow;
        var challenge = DadAutoPartyDiscordPairingRules.CreateOutboundChallenge(
            peer,
            confirmedSigningKeyFingerprint,
            now);
        configuration.OutboundPairingChallenges.RemoveAll(item => item.ApplicationId == applicationId);
        configuration.OutboundPairingChallenges.Add(challenge);
        DadAutoPartyDiscordPairingRules.PruneOutboundChallenges(configuration.OutboundPairingChallenges, now);
        configuration.StateGeneration++;
        saveConfiguration();
        return await SendEnvelopeAsync(
            DadAutoPartyPairingMessageKind.PairRequest,
            peer,
            challenge.RequestNonce,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DadAutoPartyPolicyDecision> AcceptAsync(
        ulong applicationId,
        string confirmedSigningKeyFingerprint,
        CancellationToken cancellationToken = default)
    {
        var pending = configuration.PendingPairings.FirstOrDefault(pairing => pairing.ApplicationId == applicationId);
        var peer = FindDiscovered(applicationId);
        if (pending == null || peer == null) return Decision(false, "dad-discord-pair-request-not-pending");
        if (pending.PairingRequestExpiresAtUtc is not { } expiresAtUtc || DateTime.UtcNow >= expiresAtUtc)
        {
            configuration.PendingPairings.RemoveAll(pairing => pairing.ApplicationId == applicationId);
            configuration.StateGeneration++;
            saveConfiguration();
            return Decision(false, "dad-discord-pair-request-expired");
        }
        if (!DadAutoPartyDiscordPairingRules.MatchesPendingIdentity(pending, peer))
            return Decision(false, "dad-discord-pair-request-identity-changed");
        if (!DadAutoPartyDiscordPairingRules.OperatorConfirmedFingerprint(peer, confirmedSigningKeyFingerprint))
            return Decision(false, "dad-discord-signing-fingerprint-confirmation-required");
        pending.OperatorFingerprintConfirmedAtUtc = DateTime.UtcNow;
        configuration.PendingPairings.RemoveAll(pairing => pairing.ApplicationId == applicationId);
        if (!SavePairing(pending, peer))
            return Decision(false, "dad-discord-pairing-confirmation-invalid");
        PairingRestored?.Invoke(applicationId);
        return await SendEnvelopeAsync(
            DadAutoPartyPairingMessageKind.PairAccept,
            peer,
            pending.PairingRequestNonce,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DadAutoPartyPolicyDecision> RejectAsync(ulong applicationId, CancellationToken cancellationToken = default)
    {
        var peer = FindDiscovered(applicationId);
        var requestNonce = configuration.PendingPairings
            .FirstOrDefault(pairing => pairing.ApplicationId == applicationId)?.PairingRequestNonce ?? string.Empty;
        configuration.PendingPairings.RemoveAll(pairing => pairing.ApplicationId == applicationId);
        configuration.StateGeneration++;
        saveConfiguration();
        return peer == null || !Guid.TryParseExact(requestNonce, "N", out _)
            ? Decision(true, "dad-discord-pair-request-rejected")
            : await SendEnvelopeAsync(
                DadAutoPartyPairingMessageKind.PairReject,
                peer,
                requestNonce,
                cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DadAutoPartyPolicyDecision> RevokeAsync(ulong applicationId, CancellationToken cancellationToken = default)
    {
        var pairing = configuration.Pairings.FirstOrDefault(candidate => candidate.ApplicationId == applicationId);
        if (pairing == null) return Decision(true, "dad-discord-pairing-already-absent");
        pairing.RevokedAtUtc = DateTime.UtcNow;
        configuration.OutboundPairingChallenges.RemoveAll(item => item.ApplicationId == applicationId);
        configuration.PendingPairings.RemoveAll(item => item.ApplicationId == applicationId);
        configuration.StateGeneration++;
        saveConfiguration();
        PairingRevoked?.Invoke(applicationId);
        var peer = FindDiscovered(applicationId);
        return peer == null
            ? Decision(true, "dad-discord-pairing-revoked")
            : await SendEnvelopeAsync(
                DadAutoPartyPairingMessageKind.Revoke,
                peer,
                string.Empty,
                cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<(bool Sent, ulong MessageId, string SafeCode)> SendAllianceInstructionAsync(
        DadAllianceRecruitmentInstructionDto instruction,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!isCoordinator())
            return (false, 0, "dad-alliance-discord-coordinator-required");
        if (health.State != DadAutoPartyDiscordConnectionState.Ready || !health.PermissionsValid)
            return (false, 0, "dad-alliance-discord-not-ready");
        var pairing = configuration.Pairings.FirstOrDefault(candidate =>
            candidate.ApplicationId == instruction.TargetApplicationId);
        if (pairing == null || !IsActiveVerifiedPairing(pairing) || pairing.Role != DadAutoPartyRole.Client)
            return (false, 0, "dad-alliance-discord-target-not-paired");

        var socket = client;
        var channel = socket?.GetGuild(configuration.DiscordGuildId)?.GetTextChannel(configuration.DiscordChannelId);
        if (socket == null || channel == null || socket.ConnectionState != ConnectionState.Connected)
            return (false, 0, "dad-alliance-discord-not-ready");

        try
        {
            var envelope = await allianceProtocol.CreateAsync(
                instruction,
                configuration,
                signing,
                cancellationToken).ConfigureAwait(false);
            var message = await channel.SendMessageAsync(
                    DadAllianceDiscordProtocol.Serialize(envelope))
                .ConfigureAwait(false);
            return (true, message.Id, "dad-alliance-discord-instruction-sent");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, 0, "dad-alliance-discord-cancelled");
        }
        catch (Exception)
        {
            diagnostic("dad-alliance-discord-send-failed");
            return (false, 0, "dad-alliance-discord-send-failed");
        }
    }

    public async Task DeleteAllianceMessagesBestEffortAsync(
        IEnumerable<ulong> messageIds,
        CancellationToken cancellationToken = default)
    {
        var socket = client;
        var channel = socket?.GetGuild(configuration.DiscordGuildId)?.GetTextChannel(configuration.DiscordChannelId);
        if (channel == null)
            return;

        foreach (var messageId in messageIds.Where(static id => id != 0).Distinct())
        {
            try
            {
                var message = await channel.GetMessageAsync(messageId).ConfigureAwait(false);
                if (message?.Author.Id == configuration.DiscordBotUserId)
                    await message.DeleteAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                diagnostic("dad-alliance-discord-delete-failed");
            }
        }
    }

    private async Task RestartNowAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopClientCoreAsync(cancellationToken).ConfigureAwait(false);
            if (blockedUntilExplicitReconnect || !configuration.DiscordEnabled ||
                string.IsNullOrWhiteSpace(configuration.DiscordTokenReference))
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
        if (blockedUntilExplicitReconnect) return;
        var socket = client;
        if (socket == null) return;
        try
        {
            var application = await socket.GetApplicationInfoAsync().ConfigureAwait(false);
            if (blockedUntilExplicitReconnect) return;
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
                !string.Equals(binding.EndpointFingerprint, configuration.RegistrationFingerprint, StringComparison.Ordinal) ||
                binding.KeyGeneration != configuration.EndpointKeyGeneration))
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
                KeyGeneration = Math.Max(1, configuration.EndpointKeyGeneration),
            };
            configuration.StateGeneration++;
            saveConfiguration();
            reconnectBackoff.Reset();
            nextPresenceUtc = DateTime.MinValue;
            SetHealth(DadAutoPartyDiscordConnectionState.Ready, "dad-discord-ready", true, DateTime.UtcNow);
            if (!blockedUntilExplicitReconnect)
                await PublishPresenceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Block("dad-discord-ready-validation-failed");
        }
    }

    private Task OnMessageUpdatedAsync(Cacheable<IMessage, ulong> _, SocketMessage message, ISocketMessageChannel __)
        => OnMessageReceivedAsync(message);

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (blockedUntilExplicitReconnect)
            return Task.CompletedTask;
        var guildId = message.Channel is SocketGuildChannel guildChannel ? guildChannel.Guild.Id : 0;
        var accepted = inboundMessages.TryEnqueue(new DadAutoPartyDiscordInboundMessage(
            message.Channel.Id,
            guildId,
            message.Author.Id,
            message.Author.IsBot,
            message.Content ?? string.Empty));
        if (!accepted && diagnosticGate.ShouldEmit(
                "dad-discord-inbound-queue-full",
                DateTime.UtcNow,
                DiagnosticInterval))
            diagnostic("dad-discord-inbound-queue-full");
        return Task.CompletedTask;
    }

    private void DrainInboundMessages()
    {
        inboundMessages.DrainAtMost(MaximumInboundMessagesPerUpdate, message =>
        {
            if (blockedUntilExplicitReconnect)
                return;
            try
            {
                ProcessInboundMessage(message);
            }
            catch (Exception)
            {
                EmitDiagnostic("dad-discord-inbound-processing-failed");
            }
        });
    }

    private void ProcessInboundMessage(DadAutoPartyDiscordInboundMessage message)
    {
        if (message.ChannelId != configuration.DiscordChannelId || message.AuthorId == 0 || !message.AuthorIsBot ||
            message.GuildId != configuration.DiscordGuildId)
            return;

        var allianceEnvelope = DadAllianceDiscordProtocol.Deserialize(message.Content);
        if (allianceEnvelope != null &&
            string.Equals(allianceEnvelope.Schema, DadAllianceDiscordProtocol.Schema, StringComparison.Ordinal))
        {
            var coordinatorPairing = configuration.Pairings.FirstOrDefault(pairing =>
                pairing.ApplicationId == allianceEnvelope.ApplicationId);
            var allianceDecision = allianceProtocol.Validate(
                allianceEnvelope,
                new DadAllianceDiscordValidationContext(
                    message.AuthorId,
                    configuration.DiscordApplicationId,
                    localCharacterKey(),
                    coordinatorPairing,
                    DateTime.UtcNow));
            if (allianceDecision.Allowed)
            {
                AllianceRecruitmentReceived?.Invoke(new DadAllianceRecruitmentInstructionDto
                {
                    RecruitmentId = allianceEnvelope.RecruitmentId,
                    CoordinatorWorkerSessionId = allianceEnvelope.CoordinatorWorkerSessionId,
                    CoordinatorIdentity = allianceEnvelope.CoordinatorIdentity,
                    LeaderName = allianceEnvelope.LeaderName,
                    LeaderWorld = allianceEnvelope.LeaderWorld,
                    TargetWorkerSessionId = allianceEnvelope.TargetWorkerSessionId,
                    TargetApplicationId = allianceEnvelope.TargetApplicationId,
                    TargetCharacterKey = allianceEnvelope.TargetCharacterKey,
                    TargetCharacterName = allianceEnvelope.TargetCharacterName,
                    TargetCharacterWorld = allianceEnvelope.TargetCharacterWorld,
                    TargetContentId = allianceEnvelope.TargetContentId,
                    AssignedAlliance = allianceEnvelope.AssignedAlliance,
                    Passcode = allianceEnvelope.Passcode,
                    Attempt = allianceEnvelope.Attempt,
                    State = allianceEnvelope.State,
                    StopGeneration = allianceEnvelope.StopGeneration,
                    IssuedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                        allianceEnvelope.TimestampUnixMs).UtcDateTime,
                });
            }
            else if (allianceDecision.SafeCode.Contains("signature", StringComparison.Ordinal))
            {
                EmitDiagnostic(allianceDecision.SafeCode);
            }
            return;
        }

        var envelope = DadAutoPartyPairingProtocol.Deserialize(message.Content);
        var decision = protocol.Validate(
            envelope,
            message.AuthorId,
            DateTime.UtcNow,
            isCoordinator() ? DadAutoPartyRole.Coordinator : DadAutoPartyRole.Client);
        if (!decision.Allowed || envelope == null)
        {
            if (decision.SafeCode.Contains("signature", StringComparison.Ordinal))
                EmitDiagnostic(decision.SafeCode);
            return;
        }
        if (envelope.ApplicationId == configuration.DiscordApplicationId)
            return;
        if ((envelope.TargetApplicationId != 0 && envelope.TargetApplicationId != configuration.DiscordApplicationId) ||
            (!string.IsNullOrWhiteSpace(envelope.TargetDadIdentity) &&
             !string.Equals(envelope.TargetDadIdentity, configuration.RegisteredIslandId, StringComparison.Ordinal)))
            return;

        var signingKeyFingerprint = DadAutoPartyDiscordPairingRules.ComputeSigningKeyFingerprint(
            envelope.SigningPublicKey);
        var known = configuration.Pairings.FirstOrDefault(pairing => pairing.ApplicationId == envelope.ApplicationId);
        var identityChanged = known != null && (known.BotUserId != envelope.BotUserId || known.Role != envelope.Role ||
            known.KeyGeneration != envelope.KeyGeneration ||
            !string.Equals(known.IslandId, envelope.DadIdentity, StringComparison.Ordinal) ||
            !string.Equals(known.PublicKeyFingerprint, envelope.EndpointFingerprint, StringComparison.Ordinal) ||
            !string.Equals(known.SigningPublicKey, envelope.SigningPublicKey, StringComparison.Ordinal) ||
            !string.Equals(known.SigningKeyFingerprint, signingKeyFingerprint, StringComparison.Ordinal));
        var drifted = identityChanged && known?.RevokedAtUtc == null;
        var pairingHealth = drifted ? DadAutoPartyPairingHealth.Blocked :
            known?.RevokedAtUtc != null ? DadAutoPartyPairingHealth.Revoked :
            known != null ? DadAutoPartyPairingHealth.Healthy : DadAutoPartyPairingHealth.Unpaired;
        var discoveredPeer = new DadAutoPartyDiscoveredClient(
            envelope.ApplicationId, envelope.BotUserId, envelope.DadIdentity, envelope.EndpointFingerprint,
            envelope.SigningPublicKey, signingKeyFingerprint, envelope.KeyGeneration, envelope.Role, DateTime.UtcNow,
            pairingHealth, drifted ? "dad-discord-paired-identity-changed" : string.Empty);
        lock (discoveredGate) discovered[envelope.ApplicationId] = discoveredPeer;
        if (drifted)
            return;
        switch (envelope.Kind)
        {
            case DadAutoPartyPairingMessageKind.PairRequest:
                SavePending(discoveredPeer, envelope.PairingRequestNonce);
                break;
            case DadAutoPartyPairingMessageKind.PairAccept:
                var challenge = FindOutgoingChallenge(
                    envelope.ApplicationId,
                    envelope.PairingRequestNonce,
                    discoveredPeer,
                    DateTime.UtcNow);
                if (challenge == null)
                {
                    EmitDiagnostic("dad-discord-pairaccept-no-matching-durable-request");
                    break;
                }
                if (!SavePairing(ToPairing(challenge), discoveredPeer, challenge.RequestNonce))
                {
                    EmitDiagnostic("dad-discord-pairing-confirmation-invalid");
                    break;
                }
                PairingRestored?.Invoke(envelope.ApplicationId);
                break;
            case DadAutoPartyPairingMessageKind.PairReject:
                var rejectedChallenge = FindOutgoingChallenge(
                    envelope.ApplicationId,
                    envelope.PairingRequestNonce,
                    discoveredPeer,
                    DateTime.UtcNow);
                if (rejectedChallenge != null)
                    RevokeOutgoingChallenge(rejectedChallenge.RequestNonce);
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
    }

    private Task OnDisconnectedAsync(Exception _)
    {
        if (disposed || !configuration.DiscordEnabled)
            return Task.CompletedTask;
        if (blockedUntilExplicitReconnect)
            return Task.CompletedTask;
        SetHealth(DadAutoPartyDiscordConnectionState.Disconnected, "dad-discord-disconnected-retrying", false, health.LastPresenceAtUtc);
        restartRequested = true;
        nextReconnectUtc = DateTime.UtcNow + reconnectBackoff.NextDelay();
        return Task.CompletedTask;
    }

    private async Task PublishPresenceAsync(CancellationToken cancellationToken)
    {
        if (blockedUntilExplicitReconnect) return;
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
        if (!blockedUntilExplicitReconnect)
            SetHealth(DadAutoPartyDiscordConnectionState.Ready, "dad-discord-ready", true, DateTime.UtcNow);
    }

    private async ValueTask<DadAutoPartyPolicyDecision> SendEnvelopeAsync(
        DadAutoPartyPairingMessageKind kind,
        DadAutoPartyDiscoveredClient peer,
        string pairingRequestNonce,
        CancellationToken cancellationToken)
    {
        var socket = client;
        var channel = socket?.GetGuild(configuration.DiscordGuildId)?.GetTextChannel(configuration.DiscordChannelId);
        if (blockedUntilExplicitReconnect || socket == null || channel == null ||
            health.State != DadAutoPartyDiscordConnectionState.Ready)
            return Decision(false, "dad-discord-not-ready");
        var envelope = await protocol.CreateAsync(
            kind,
            isCoordinator() ? DadAutoPartyRole.Coordinator : DadAutoPartyRole.Client,
            configuration,
            signing,
            peer.ApplicationId,
            peer.DadIdentity,
            pairingRequestNonce,
            cancellationToken).ConfigureAwait(false);
        await channel.SendMessageAsync(DadAutoPartyPairingProtocol.Serialize(envelope)).ConfigureAwait(false);
        return Decision(true, $"dad-discord-{kind.ToString().ToLowerInvariant()}-sent");
    }

    private void SavePending(DadAutoPartyDiscoveredClient peer, string pairingRequestNonce)
    {
        configuration.PendingPairings.RemoveAll(pairing => pairing.ApplicationId == peer.ApplicationId);
        var now = DateTime.UtcNow;
        var pending = ToPairing(peer, now);
        pending.PairingRequestNonce = pairingRequestNonce;
        pending.PairingRequestExpiresAtUtc = now + DadAutoPartyDiscordPairingRules.PairingChallengeLifetime;
        configuration.PendingPairings.Add(pending);
        configuration.StateGeneration++;
        saveConfiguration();
    }

    private bool SavePairing(
        DadAutoPartyPairing pending,
        DadAutoPartyDiscoveredClient peer,
        string consumeChallengeNonce = "")
    {
        if (!pending.OperatorFingerprintConfirmedAtUtc.HasValue ||
            !DadAutoPartyDiscordPairingRules.MatchesPendingIdentity(pending, peer) ||
            !DadAutoPartyDiscordPairingRules.OperatorConfirmedFingerprint(peer, pending.SigningKeyFingerprint))
            return false;
        var accepted = pending.Clone();
        accepted.ConfirmedAtUtc = DateTime.UtcNow;
        accepted.RevokedAtUtc = null;
        accepted.SigningKeyFingerprint = peer.SigningKeyFingerprint;
        accepted.PairingRequestNonce = string.Empty;
        accepted.PairingRequestExpiresAtUtc = null;
        configuration.Pairings.RemoveAll(pairing => pairing.ApplicationId == accepted.ApplicationId ||
            string.Equals(pairing.IslandId, accepted.IslandId, StringComparison.Ordinal));
        configuration.Pairings.Add(accepted);
        if (!string.IsNullOrWhiteSpace(consumeChallengeNonce))
        {
            var consumed = configuration.OutboundPairingChallenges.FirstOrDefault(challenge =>
                string.Equals(challenge.RequestNonce, consumeChallengeNonce, StringComparison.Ordinal));
            if (consumed != null)
                consumed.UsedAtUtc = DateTime.UtcNow;
            configuration.OutboundPairingChallenges.RemoveAll(challenge =>
                string.Equals(challenge.RequestNonce, consumeChallengeNonce, StringComparison.Ordinal));
        }
        configuration.StateGeneration++;
        saveConfiguration();
        lock (discoveredGate)
            discovered[peer.ApplicationId] = peer with { PairingHealth = DadAutoPartyPairingHealth.Healthy };
        return true;
    }

    private static DadAutoPartyPairing ToPairing(DadAutoPartyDiscoveredClient peer, DateTime confirmedAtUtc) => new()
    {
        OwnerId = "discord",
        IslandId = peer.DadIdentity,
        PublicKeyFingerprint = peer.EndpointFingerprint,
        SigningPublicKey = peer.SigningPublicKey,
        SigningKeyFingerprint = peer.SigningKeyFingerprint,
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

    private DadAutoPartyOutboundPairingChallenge? FindOutgoingChallenge(
        ulong applicationId,
        string requestNonce,
        DadAutoPartyDiscoveredClient peer,
        DateTime nowUtc)
    {
        var challenge = configuration.OutboundPairingChallenges.FirstOrDefault(candidate =>
            candidate.ApplicationId == applicationId &&
            string.Equals(candidate.RequestNonce, requestNonce, StringComparison.Ordinal));
        return challenge != null && DadAutoPartyDiscordPairingRules.MatchesActiveChallenge(
            challenge,
            peer,
            requestNonce,
            nowUtc)
                ? challenge.Clone()
                : null;
    }

    private void RevokeOutgoingChallenge(string requestNonce)
    {
        var challenge = configuration.OutboundPairingChallenges.FirstOrDefault(candidate =>
            string.Equals(candidate.RequestNonce, requestNonce, StringComparison.Ordinal));
        if (challenge == null)
            return;
        challenge.RevokedAtUtc = DateTime.UtcNow;
        configuration.StateGeneration++;
        saveConfiguration();
    }

    private static DadAutoPartyPairing ToPairing(DadAutoPartyOutboundPairingChallenge challenge) => new()
    {
        OwnerId = "discord",
        IslandId = challenge.IslandId,
        PublicKeyFingerprint = challenge.EndpointFingerprint,
        SigningPublicKey = challenge.SigningPublicKey,
        SigningKeyFingerprint = challenge.SigningKeyFingerprint,
        PairingRequestNonce = challenge.RequestNonce,
        PairingRequestExpiresAtUtc = challenge.ExpiresAtUtc,
        OperatorFingerprintConfirmedAtUtc = challenge.OperatorConfirmedAtUtc,
        KeyGeneration = challenge.KeyGeneration,
        ApplicationId = challenge.ApplicationId,
        BotUserId = challenge.BotUserId,
        Role = challenge.Role,
        ConfirmedAtUtc = challenge.OperatorConfirmedAtUtc,
    };

    private static bool IsActiveVerifiedPairing(DadAutoPartyPairing pairing)
        => pairing.RevokedAtUtc == null && pairing.ApplicationId != 0 &&
           pairing.OperatorFingerprintConfirmedAtUtc.HasValue &&
           Enum.IsDefined(typeof(DadAutoPartyRole), pairing.Role) &&
           !string.IsNullOrWhiteSpace(pairing.SigningPublicKey) &&
           !string.IsNullOrWhiteSpace(pairing.SigningKeyFingerprint) &&
           pairing.KeyGeneration >= 1 &&
           string.Equals(
               pairing.SigningKeyFingerprint,
               DadAutoPartyDiscordPairingRules.ComputeSigningKeyFingerprint(pairing.SigningPublicKey),
               StringComparison.Ordinal);

    private void EmitDiagnostic(string safeCode)
    {
        if (diagnosticGate.ShouldEmit(safeCode, DateTime.UtcNow, DiagnosticInterval))
            diagnostic(safeCode);
    }

    private void Block(string safeCode)
    {
        blockedUntilExplicitReconnect = true;
        restartRequested = false;
        nextReconnectUtc = DateTime.MaxValue;
        inboundMessages.Clear();
        diagnostic(safeCode);
        SetHealth(DadAutoPartyDiscordConnectionState.Blocked, safeCode, false, health.LastPresenceAtUtc);
    }

    private void SetHealth(
        DadAutoPartyDiscordConnectionState state,
        string safeCode,
        bool permissionsValid,
        DateTime? lastPresenceUtc)
    {
        if (!DadAutoPartyDiscordLifecycleRules.CanSetHealth(blockedUntilExplicitReconnect, state))
            return;
        health = new(state, safeCode, DateTime.UtcNow, lastPresenceUtc,
            configuration.DiscordApplicationId, configuration.DiscordBotUserId, permissionsValid);
    }

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
