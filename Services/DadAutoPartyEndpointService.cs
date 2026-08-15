using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using AutoParty.Contracts;
using AutoParty.Core.Authentication;
using AutoParty.Core.Cryptography;
using dad.Models;

namespace dad.Services;

public sealed record DadAutoPartyRelayStatus(
    bool Attached,
    bool Running,
    string SafeCode,
    DateTimeOffset ObservedAt,
    DateTimeOffset? LastAuthenticatedInboundAt,
    int PendingOutboundCount,
    int AwaitingRelayReceiptCount,
    int PendingExecutionCount);

public sealed class DadAutoPartyEndpointService : IDisposable
{
    private static readonly TimeSpan LegacyCleanupRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ListingPublishInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ListingPublishRetryDelay = TimeSpan.FromSeconds(30);
    private readonly DadAutoPartyConfiguration configuration;
    private readonly IDadAutoPartyWebhookCredentialStore credentialStore;
    private readonly IDadAutoPartyDiscordTokenStore legacyTokenStore;
    private readonly IDadAutoPartyEndpointIdentityStore? identityStore;
    private readonly DadDiscordCourierConnector connector;
    private readonly Action saveConfiguration;
    private readonly Action<string> diagnostic;
    private readonly Func<HttpClient> httpClientFactory;
    private readonly Func<DateTime, DadAutoPartyListingPublication>? listingPublicationProvider;
    private readonly CancellationTokenSource shutdown = new();
    private Task<AdapterStartResult>? adapterStartTask;
    private Task<LegacyCleanupResult>? legacyCleanupTask;
    private Task? adapterStopTask;
    private DadAutoPartyWebhookTransportAdapter? adapter;
    private long desiredAdapterGeneration;
    private long attachedAdapterGeneration = -1;
    private long readyAdapterGeneration;
    private DadAutoPartyRelayPump? relayPump;
    private DadAutoPartyService? autoPartyService;
    private DateTime nextLegacyCleanupAttemptUtc = DateTime.MinValue;
    private DateTime nextListingPublishUtc = DateTime.MinValue;
    private bool disposed;

    public DadAutoPartyEndpointService(
        DadAutoPartyConfiguration configuration,
        IDadAutoPartyWebhookCredentialStore credentialStore,
        IDadAutoPartyDiscordTokenStore legacyTokenStore,
        DadDiscordCourierConnector connector,
        Action saveConfiguration,
        Action<string>? diagnostic = null,
        Func<HttpClient>? httpClientFactory = null,
        IDadAutoPartyEndpointIdentityStore? identityStore = null,
        Func<DateTime, DadAutoPartyListingPublication>? listingPublicationProvider = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        this.legacyTokenStore = legacyTokenStore ?? throw new ArgumentNullException(nameof(legacyTokenStore));
        this.identityStore = identityStore;
        this.connector = connector ?? throw new ArgumentNullException(nameof(connector));
        this.saveConfiguration = saveConfiguration ?? throw new ArgumentNullException(nameof(saveConfiguration));
        this.diagnostic = diagnostic ?? (_ => { });
        this.httpClientFactory = httpClientFactory ?? (() => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        this.listingPublicationProvider = listingPublicationProvider;
    }

    public DadAutoPartyEndpointSnapshot Snapshot { get; private set; } =
        DadAutoPartyEndpointSnapshot.Disabled("dad-webhook-not-registered");

    internal DadAutoPartyAdapterTransferSnapshot TransferSnapshot
    {
        get
        {
            var currentAdapter = adapter;
            return currentAdapter != null &&
                   attachedAdapterGeneration == Volatile.Read(ref desiredAdapterGeneration)
                ? currentAdapter.TransferSnapshot
                : DadAutoPartyAdapterTransferSnapshot.Idle;
        }
    }

    public DadAutoPartyRelayStatus RelayStatus
    {
        get
        {
            var snapshot = relayPump?.Snapshot;
            return snapshot == null
                ? new(
                    false,
                    false,
                    "dad-relay-pump-not-attached",
                    DateTimeOffset.UtcNow,
                    null,
                    0,
                    0,
                    0)
                : new(
                    true,
                    snapshot.Running,
                    snapshot.SafeCode,
                    snapshot.ObservedAt,
                    snapshot.LastAuthenticatedInboundAt,
                    snapshot.PendingOutboundCount,
                    snapshot.AwaitingRelayReceiptCount,
                    snapshot.PendingExecutionCount);
        }
    }

    public DadAutoPartyPairingChallenge? LastPairingChallenge => relayPump?.LastPairingChallenge;

    internal string LastPairingChallengePeerIslandId => relayPump?.LastPairingChallengePeerIslandId ?? string.Empty;

    internal DadAutoPartyPairingAttemptResult? LastPairingAttemptResult => relayPump?.LastPairingAttemptResult;

    public DadAutoPartyPolicyDecision SetStandingSharePolicy(DadAutoPartySharePolicy sharePolicy)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var normalized = sharePolicy?.Clone().Normalize();
        if (normalized is not
            {
                IsValid: true,
                Mode: DadAutoPartyCharacterShareMode.CharacterList,
            })
            return Decision(false, "dad-standing-share-policy-invalid");
        configuration.StandingSharePolicy = normalized;
        configuration.StateGeneration++;
        saveConfiguration();
        nextListingPublishUtc = DateTime.MinValue;
        return Decision(true, "dad-standing-share-policy-updated");
    }

    internal event Action<DadAllianceCentralOperationContext>? AllianceRecruitmentReceived;
    internal event Action<DadAllianceCentralReceiptContext>? AllianceRecruitmentReceiptReceived;

    internal void AttachRelayPump(DadAutoPartyRelayPump pump, DadAutoPartyService service)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(pump);
        ArgumentNullException.ThrowIfNull(service);
        if (!HasValidatedBootstrap(DateTime.UtcNow))
            throw new InvalidOperationException("dad-relay-bootstrap-not-validated");
        if (relayPump != null && !ReferenceEquals(relayPump, pump))
            throw new InvalidOperationException("dad-relay-pump-already-attached");

        relayPump = pump;
        autoPartyService = service;
        pump.ConfigureLifecycleHandlers(
            HandleRegistrationReceipt,
            (_, pending, cancellationToken) => service.PurgeAsync(
                pending.DeleteEndpointIdentity,
                cancellationToken));
        pump.ConfigureAllianceHandlers(
            operation => AllianceRecruitmentReceived?.Invoke(operation),
            receipt => AllianceRecruitmentReceiptReceived?.Invoke(receipt));
        pump.Start();
    }

    public ValueTask<DadAutoPartyPolicyDecision> RequestDirectoryAsync(
        string searchText,
        bool includePromiscuous,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return relayPump?.RequestDirectoryAsync(searchText, includePromiscuous, cancellationToken) ??
            ValueTask.FromResult(Decision(false, "dad-relay-pump-not-attached"));
    }

    public ValueTask<DadAutoPartyPolicyDecision> InitiatePairingAsync(
        string peerIslandId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration.IsRegistrationActive &&
            Volatile.Read(ref desiredAdapterGeneration) != Volatile.Read(ref readyAdapterGeneration))
            return ValueTask.FromResult(Decision(false, "dad-pairing-mailbox-refreshing"));
        if (configuration.IsRegistrationActive && Snapshot.State != DadAutoPartyEndpointConnectionState.Ready)
            return ValueTask.FromResult(Decision(false, "dad-pairing-mailbox-not-ready"));
        return relayPump?.InitiatePairingAsync(peerIslandId, cancellationToken) ??
            ValueTask.FromResult(Decision(false, "dad-relay-pump-not-attached"));
    }

    public ValueTask<DadAutoPartyPolicyDecision> ApprovePairingAsync(
        Guid pairingId,
        string displayedPeerFingerprint,
        string confirmationCode,
        DadAutoPartySharePolicy localSharePolicy,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ApprovePairing(
            pairingId,
            displayedPeerFingerprint,
            confirmationCode,
            localSharePolicy));
    }

    internal DadAutoPartyPolicyDecision ApprovePairing(
        Guid pairingId,
        string displayedPeerFingerprint,
        string confirmationCode,
        DadAutoPartySharePolicy localSharePolicy)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (relayPump == null || autoPartyService == null)
            return Decision(false, "dad-relay-pump-not-attached");

        var local = autoPartyService.ApprovePairing(
            pairingId,
            displayedPeerFingerprint,
            confirmationCode,
            localSharePolicy);
        if (!local.Allowed)
            return local;
        var pairingIdText = pairingId.ToString("D");
        var pairing = configuration.Pairings
            .Concat(configuration.PendingPairings)
            .SingleOrDefault(item => string.Equals(item.PairingId, pairingIdText, StringComparison.Ordinal));
        if (pairing == null)
            return Decision(false, "dad-pairing-approval-state-missing");
        if (pairing.LocalApprovalRelayAcceptedAtUtc != null)
            return local;
        var queued = relayPump.QueuePairingApproval(pairing, pairing.LocalSharePolicy, accepted: true);
        if (queued.Allowed)
            nextListingPublishUtc = DateTime.MinValue;
        return queued;
    }

    public ValueTask<DadAutoPartyPolicyDecision> DeauthenticateAsync(
        string peerIslandId,
        string safeReason,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Deauthenticate(peerIslandId, safeReason));
    }

    internal DadAutoPartyPolicyDecision Deauthenticate(string peerIslandId, string safeReason)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var result = relayPump?.Deauthenticate(peerIslandId, safeReason) ??
                     Decision(false, "dad-relay-pump-not-attached");
        if (result.Allowed)
            nextListingPublishUtc = DateTime.MinValue;
        return result;
    }

    public ValueTask<DadAutoPartyPolicyDecision> DeregisterAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(BeginDeregistration());
    }

    internal DadAutoPartyPolicyDecision BeginDeregistration()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return relayPump?.BeginDeregistration(deleteEndpointIdentity: false) ??
               Decision(false, "dad-relay-pump-not-attached");
    }

    public DadAutoPartyLanPresence GetLanPresence()
    {
        var health = Snapshot.State switch
        {
            DadAutoPartyEndpointConnectionState.Ready when configuration.Pairings.Any(static item => item.IsActive) =>
                DadAutoPartyPairingHealth.Healthy,
            DadAutoPartyEndpointConnectionState.Ready => DadAutoPartyPairingHealth.Unpaired,
            DadAutoPartyEndpointConnectionState.Degraded => DadAutoPartyPairingHealth.Stale,
            DadAutoPartyEndpointConnectionState.Quarantined => DadAutoPartyPairingHealth.Blocked,
            _ => DadAutoPartyPairingHealth.Disabled,
        };
        return new(configuration.RegisteredIslandId, configuration.RegistrationFingerprint, health);
    }

    internal ValueTask<DadAllianceCentralSendResult> SendAllianceInstructionAsync(
        DadAllianceRecruitmentInstructionDto instruction,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(relayPump?.QueueAllianceInstruction(instruction) ??
            new DadAllianceCentralSendResult(false, Guid.Empty, "dad-relay-pump-not-attached"));
    }

    internal ValueTask<DadAllianceCentralSendResult> SendAllianceCancellationAsync(
        DadAllianceRecruitmentCancellationDto cancellation,
        DadAllianceRecruitmentInstructionDto instruction,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(relayPump?.QueueAllianceCancellation(cancellation, instruction) ??
            new DadAllianceCentralSendResult(false, Guid.Empty, "dad-relay-pump-not-attached"));
    }

    internal DadAutoPartyPolicyDecision QueueAllianceReceipt(
        Guid operationId,
        DadAllianceRecruitmentResultDto result)
        => relayPump?.QueueAllianceReceipt(operationId, result) ??
           Decision(false, "dad-relay-pump-not-attached");

    public Task ForgetAllianceDeliveriesBestEffortAsync(
        IEnumerable<Guid> messageIds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        relayPump?.ForgetAllianceDeliveries(messageIds);
        return Task.CompletedTask;
    }

    public async ValueTask<DadAutoPartyPolicyDecision> ImportBootstrapAsync(
        DadAutoPartyBootstrapImport bootstrap,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTime.UtcNow;
        if (bootstrap == null)
            return Decision(false, "dad-bootstrap-invalid");
        var expiredBootstrap = configuration.RegistrationState == DadAutoPartyRegistrationState.BootstrapImported &&
            configuration.BootstrapExpiresAtUtc <= now;
        if (configuration.RegistrationState is not (
                DadAutoPartyRegistrationState.Unregistered or DadAutoPartyRegistrationState.Active) &&
            !expiredBootstrap)
            return Decision(false, "dad-bootstrap-replayed");

        var recoveryImport = configuration.RegistrationState == DadAutoPartyRegistrationState.Active ||
            expiredBootstrap ||
            configuration.RegistrationRecoveryState == DadAutoPartyRegistrationRecoveryState.RecoveryAvailable ||
            !string.IsNullOrWhiteSpace(configuration.RouteId);
        var expectedRegistrationId = Guid.TryParse(configuration.RegistrationId, out var configuredRegistrationId)
            ? configuredRegistrationId
            : Guid.Empty;
        Guid routedRegistrationId = default;
        var sameIdentityRecovery = recoveryImport &&
            identityStore != null &&
            DadAutoPartyRegistrationRecovery.TryGetRegistrationIdFromRoute(
                configuration.RouteId,
                out routedRegistrationId) &&
            routedRegistrationId == bootstrap.RegistrationId &&
            string.Equals(configuration.RouteId, bootstrap.RouteId, StringComparison.Ordinal) &&
            string.Equals(configuration.RegisteredOwnerId, bootstrap.OwnerId, StringComparison.Ordinal) &&
            string.Equals(configuration.RegisteredIslandId, bootstrap.IslandId, StringComparison.Ordinal) &&
            DadAutoPartyRegistrationRecovery.HasValidProtectedIdentity(configuration, identityStore);
        if (recoveryImport && !sameIdentityRecovery)
            return Decision(false, "dad-bootstrap-recovery-mismatch");
        if (sameIdentityRecovery)
            expectedRegistrationId = routedRegistrationId;
        var preserveActiveRegistration = sameIdentityRecovery &&
            configuration.RegistrationState == DadAutoPartyRegistrationState.Active;

        if (bootstrap.RegistrationId == Guid.Empty ||
            bootstrap.RegistrationId != expectedRegistrationId ||
            !string.Equals(
                DadAutoPartyConfiguration.NormalizeIdentifier(bootstrap.OwnerId),
                configuration.RegisteredOwnerId,
                StringComparison.Ordinal) ||
            !string.Equals(
                DadAutoPartyConfiguration.NormalizeIdentifier(bootstrap.IslandId),
                configuration.RegisteredIslandId,
                StringComparison.Ordinal) ||
            !string.Equals(
                DadAutoPartyConfiguration.NormalizeFingerprint(bootstrap.EndpointFingerprint),
                configuration.RegistrationFingerprint,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(DadAutoPartyConfiguration.NormalizeSnowflake(bootstrap.CentralBotApplicationId)) ||
            string.IsNullOrWhiteSpace(DadAutoPartyConfiguration.NormalizeIdentifier(bootstrap.HomeGuildScope)) ||
            string.IsNullOrWhiteSpace(DadAutoPartyConfiguration.NormalizeIdentifier(bootstrap.RouteId)) ||
            bootstrap.Mailbox is not { HasProvisionedMailbox: true } ||
            bootstrap.UplinkEpoch is null || bootstrap.DownlinkEpoch is null ||
            bootstrap.RelayPublicKeys is null ||
            bootstrap.UplinkEpoch.IslandId.Value != bootstrap.IslandId ||
            bootstrap.DownlinkEpoch.IslandId.Value != bootstrap.IslandId ||
            bootstrap.UplinkEpoch.Direction != CourierDirection.Uplink ||
            bootstrap.DownlinkEpoch.Direction != CourierDirection.Downlink ||
            bootstrap.UplinkEpoch.EpochId == bootstrap.DownlinkEpoch.EpochId ||
            bootstrap.UplinkEpoch.EpochGeneration < 1 ||
            bootstrap.DownlinkEpoch.EpochGeneration < 1 ||
            bootstrap.RelayPublicKeys.KeyVersion < 1 ||
            bootstrap.RelayPublicKeys.Ed25519PublicKey.Length != AutoPartyProtocol.Ed25519PublicKeyBytes ||
            bootstrap.RelayPublicKeys.X25519PublicKey.Length != AutoPartyProtocol.X25519KeyBytes ||
            bootstrap.BootstrapExpiresAtUtc <= now ||
            bootstrap.BootstrapExpiresAtUtc > now + TimeSpan.FromHours(24) ||
            !MailboxMatchesBootstrap(bootstrap.Mailbox, bootstrap))
            return Decision(false, "dad-bootstrap-invalid");

        var oldReference = configuration.WebhookCredentialReference;
        string newReference;
        try
        {
            newReference = await credentialStore.StoreAsync(bootstrap.Mailbox, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return Decision(false, "dad-bootstrap-credential-store-failed");
        }

        configuration.RegistrationId = bootstrap.RegistrationId.ToString("D");
        configuration.RouteId = DadAutoPartyConfiguration.NormalizeIdentifier(bootstrap.RouteId);
        configuration.CentralBotApplicationId = DadAutoPartyConfiguration.NormalizeSnowflake(
            bootstrap.CentralBotApplicationId);
        configuration.HomeGuildScope = DadAutoPartyConfiguration.NormalizeIdentifier(bootstrap.HomeGuildScope);
        configuration.WebhookCredentialReference = newReference;
        configuration.UplinkEpochId = bootstrap.UplinkEpoch.EpochId.ToString("D");
        configuration.DownlinkEpochId = bootstrap.DownlinkEpoch.EpochId.ToString("D");
        configuration.MailboxEpochGeneration = bootstrap.UplinkEpoch.EpochGeneration;
        configuration.RelayKeyGeneration = bootstrap.RelayPublicKeys.KeyVersion;
        configuration.RelaySigningPublicKey = Convert.ToBase64String(
            bootstrap.RelayPublicKeys.Ed25519PublicKey.AsSpan());
        configuration.RelayAgreementPublicKey = Convert.ToBase64String(
            bootstrap.RelayPublicKeys.X25519PublicKey.AsSpan());
        configuration.BootstrapExpiresAtUtc = preserveActiveRegistration
            ? default
            : bootstrap.BootstrapExpiresAtUtc;
        configuration.RegistrationState = preserveActiveRegistration
            ? DadAutoPartyRegistrationState.Active
            : DadAutoPartyRegistrationState.BootstrapImported;
        if (preserveActiveRegistration)
            configuration.RegistrationRecoveryState = DadAutoPartyRegistrationRecoveryState.Active;
        if (!sameIdentityRecovery)
        {
            configuration.Pairings.Clear();
            configuration.PendingPairings.Clear();
            configuration.Grants.Clear();
            configuration.Listings.Clear();
            configuration.RemoteBindings.Clear();
            configuration.Deauthentications.Clear();
        }
        configuration.StateGeneration++;
        saveConfiguration();
        RequestAdapterRefresh();

        if (!string.IsNullOrWhiteSpace(oldReference) &&
            !string.Equals(oldReference, newReference, StringComparison.Ordinal))
        {
            try
            {
                await credentialStore.DeleteAsync(oldReference, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostic("dad-bootstrap-old-credential-cleanup-failed");
            }
        }
        return Decision(true, "dad-bootstrap-imported");
    }

    public async ValueTask<DadAutoPartyPolicyDecision> ImportBootstrapCopyPasteAsync(
        string encryptedBootstrap,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (identityStore == null ||
            string.IsNullOrWhiteSpace(configuration.EndpointIdentityReference) ||
            string.IsNullOrWhiteSpace(configuration.RegisteredOwnerId) ||
            string.IsNullOrWhiteSpace(configuration.RegisteredIslandId))
            return Decision(false, "dad-bootstrap-identity-not-ready");
        var expiredBootstrap = configuration.RegistrationState == DadAutoPartyRegistrationState.BootstrapImported &&
            configuration.BootstrapExpiresAtUtc <= DateTime.UtcNow;
        if (configuration.RegistrationState is not (
                DadAutoPartyRegistrationState.Unregistered or DadAutoPartyRegistrationState.Active) &&
            !expiredBootstrap)
            return Decision(false, "dad-bootstrap-replayed");

        var recoveryImport = configuration.RegistrationState == DadAutoPartyRegistrationState.Active ||
            expiredBootstrap ||
            configuration.RegistrationRecoveryState == DadAutoPartyRegistrationRecoveryState.RecoveryAvailable ||
            !string.IsNullOrWhiteSpace(configuration.RouteId);
        var hasRegistrationId = recoveryImport
            ? DadAutoPartyRegistrationRecovery.TryGetRegistrationIdFromRoute(
                configuration.RouteId,
                out var registrationId)
            : Guid.TryParse(configuration.RegistrationId, out registrationId);
        if (!hasRegistrationId)
            return Decision(false, "dad-bootstrap-identity-not-ready");

        byte[]? identityMaterial = null;
        byte[]? signingPrivateKey = null;
        byte[]? encryptionPrivateKey = null;
        try
        {
            var sealedBootstrap = RegistrationCopyPasteCodec.DecodeBootstrap(encryptedBootstrap);
            identityMaterial = await identityStore.LoadAsync(
                configuration.EndpointIdentityReference,
                cancellationToken).ConfigureAwait(false);
            var identity = JsonSerializer.Deserialize<DadAutoPartyPrivateIdentityPackage>(identityMaterial)
                ?? throw new InvalidOperationException("dad-bootstrap-identity-invalid");
            if (!string.Equals(identity.OwnerId, configuration.RegisteredOwnerId, StringComparison.Ordinal) ||
                !string.Equals(identity.IslandId, configuration.RegisteredIslandId, StringComparison.Ordinal) ||
                identity.KeyGeneration != configuration.EndpointKeyGeneration)
                return Decision(false, "dad-bootstrap-identity-mismatch");

            signingPrivateKey = Convert.FromBase64String(identity.SigningPrivateKey);
            encryptionPrivateKey = Convert.FromBase64String(identity.EncryptionPrivateKey);
            if (signingPrivateKey.Length != AutoPartyProtocol.Ed25519SignatureBytes / 2 ||
                encryptionPrivateKey.Length != AutoPartyProtocol.X25519KeyBytes)
                return Decision(false, "dad-bootstrap-identity-invalid");

            var opened = InitialRegistrationBootstrapOpener.Open(
                sealedBootstrap,
                registrationId,
                new OwnerId(configuration.RegisteredOwnerId),
                new IslandId(configuration.RegisteredIslandId),
                configuration.EndpointKeyGeneration,
                encryptionPrivateKey);
            if (!opened.Succeeded || opened.Message?.Contract is not { } bootstrap ||
                !string.Equals(
                    bootstrap.Header.SenderIslandId.Value,
                    DadAutoPartyIdentityPackageService.RegistrationRecipient,
                    StringComparison.Ordinal))
                return Decision(false, "dad-bootstrap-open-rejected");

            var credential = new DadAutoPartyWebhookCredential(
                bootstrap.Mailbox.WebhookId,
                bootstrap.Mailbox.WebhookToken,
                bootstrap.Mailbox.ChannelId)
            {
                UplinkEpoch = bootstrap.InitialUplinkEpoch,
                DownlinkEpoch = bootstrap.InitialDownlinkEpoch,
                RelayPublicKeys = bootstrap.RelayPublicKeys,
            };
            return await ImportBootstrapAsync(
                new DadAutoPartyBootstrapImport(
                    bootstrap.RegistrationId,
                    bootstrap.OwnerId.Value,
                    bootstrap.IslandId.Value,
                    configuration.RegistrationFingerprint,
                    bootstrap.BotApplicationId,
                    bootstrap.HomeGuildId,
                    bootstrap.RouteId,
                    credential,
                    bootstrap.InitialUplinkEpoch,
                    bootstrap.InitialDownlinkEpoch,
                    bootstrap.RelayPublicKeys,
                    bootstrap.BootstrapExpiresAt.UtcDateTime),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ProtocolException or JsonException or FormatException or CryptographicException or
                InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Decision(false, "dad-bootstrap-open-rejected");
        }
        finally
        {
            if (identityMaterial != null) CryptographicOperations.ZeroMemory(identityMaterial);
            if (signingPrivateKey != null) CryptographicOperations.ZeroMemory(signingPrivateKey);
            if (encryptionPrivateKey != null) CryptographicOperations.ZeroMemory(encryptionPrivateKey);
        }
    }

    public DadAutoPartyPolicyDecision MarkRegistrationActive(
        Guid registrationId,
        Guid uplinkEpochId,
        long epochGeneration,
        long directoryGeneration = 1)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (configuration.RegistrationState != DadAutoPartyRegistrationState.BootstrapImported ||
            registrationId == Guid.Empty || uplinkEpochId == Guid.Empty || epochGeneration < 1 ||
            directoryGeneration < 1 ||
            !string.Equals(configuration.RegistrationId, registrationId.ToString("D"), StringComparison.Ordinal) ||
            !string.Equals(configuration.UplinkEpochId, uplinkEpochId.ToString("D"), StringComparison.Ordinal) ||
            configuration.MailboxEpochGeneration != epochGeneration)
            return Decision(false, "dad-registration-hello-mismatch");
        configuration.RegistrationState = DadAutoPartyRegistrationState.Active;
        configuration.RegistrationRecoveryState = DadAutoPartyRegistrationRecoveryState.Active;
        configuration.DirectoryGeneration = directoryGeneration;
        configuration.BootstrapExpiresAtUtc = default;
        configuration.StateGeneration++;
        saveConfiguration();
        nextListingPublishUtc = DateTime.MinValue;
        return Decision(true, "dad-registration-active");
    }

    public void Update(bool dadEnabled)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        relayPump?.UpdateFramework();
        ObserveLegacyCleanup();
        ObserveAdapterStop();
        ObserveAdapterStart();

        if (configuration.LegacyDiscordTokenCleanupPending && legacyCleanupTask == null &&
            DateTime.UtcNow >= nextLegacyCleanupAttemptUtc)
        {
            var reference = configuration.LegacyDiscordTokenReference;
            legacyCleanupTask = Task.Run(
                () => DeleteLegacyTokenAsync(reference, shutdown.Token),
                shutdown.Token);
        }

        var now = DateTime.UtcNow;
        var shouldRun = dadEnabled && HasValidatedBootstrap(now);
        if (!shouldRun)
        {
            nextListingPublishUtc = DateTime.MinValue;
            Snapshot = configuration.Enabled
                ? DadAutoPartyEndpointSnapshot.Disabled("dad-webhook-not-registered")
                : DadAutoPartyEndpointSnapshot.Disabled();
            StopAdapter();
            return;
        }

        PublishListingsIfDue(now);

        var desiredGeneration = Volatile.Read(ref desiredAdapterGeneration);
        if (adapter != null && attachedAdapterGeneration != desiredGeneration)
            StopAdapter();

        if (adapter == null && adapterStartTask == null && adapterStopTask == null)
        {
            var credentialReference = configuration.WebhookCredentialReference;
            var routeId = configuration.RouteId;
            var identityReference = configuration.EndpointIdentityReference;
            var endpointKeyGeneration = configuration.EndpointKeyGeneration;
            var expected = configuration.Clone();
            var startGeneration = desiredGeneration;
            adapterStartTask = Task.Run(async () =>
            {
                byte[]? identityMaterial = null;
                byte[]? signingPrivateKey = null;
                try
                {
                    var credential = await credentialStore.LoadAsync(
                        credentialReference,
                        shutdown.Token).ConfigureAwait(false);
                    if (!CredentialMatchesConfiguration(credential, expected))
                        return new AdapterStartResult(null, "dad-webhook-bootstrap-binding-mismatch", startGeneration);
                    if (identityStore == null)
                        return new AdapterStartResult(null, "dad-webhook-identity-store-unavailable", startGeneration);
                    identityMaterial = await identityStore.LoadAsync(
                        identityReference,
                        shutdown.Token).ConfigureAwait(false);
                    var identity = JsonSerializer.Deserialize<DadAutoPartyPrivateIdentityPackage>(identityMaterial)
                        ?? throw new InvalidOperationException("dad-webhook-identity-invalid");
                    if (!string.Equals(identity.OwnerId, expected.RegisteredOwnerId, StringComparison.Ordinal) ||
                        !string.Equals(identity.IslandId, expected.RegisteredIslandId, StringComparison.Ordinal) ||
                        identity.KeyGeneration != endpointKeyGeneration)
                        return new AdapterStartResult(null, "dad-webhook-identity-mismatch", startGeneration);
                    signingPrivateKey = Convert.FromBase64String(identity.SigningPrivateKey);
                    if (signingPrivateKey.Length != AutoPartyProtocol.Ed25519SignatureBytes / 2)
                        return new AdapterStartResult(null, "dad-webhook-identity-invalid", startGeneration);
                    return new AdapterStartResult(
                        new DadAutoPartyWebhookTransportAdapter(
                            credential,
                            routeId,
                            endpointKeyGeneration,
                            signingPrivateKey,
                            httpClientFactory(),
                            ownsHttpClient: true),
                        "dad-webhook-adapter-created",
                        startGeneration);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    return new AdapterStartResult(null, "dad-webhook-adapter-start-cancelled", startGeneration);
                }
                catch
                {
                    return new AdapterStartResult(null, "dad-webhook-credential-load-failed", startGeneration);
                }
                finally
                {
                    if (identityMaterial != null) CryptographicOperations.ZeroMemory(identityMaterial);
                    if (signingPrivateKey != null) CryptographicOperations.ZeroMemory(signingPrivateKey);
                }
            }, shutdown.Token);
        }

        if (adapter != null)
        {
            var adapterSnapshot = adapter.Snapshot;
            if (attachedAdapterGeneration == desiredGeneration &&
                adapterSnapshot.State is DadAutoPartyEndpointConnectionState.Degraded or
                    DadAutoPartyEndpointConnectionState.Quarantined or
                    DadAutoPartyEndpointConnectionState.Disabled)
            {
                Snapshot = adapterSnapshot;
            }
            else if (attachedAdapterGeneration == desiredGeneration &&
                adapterSnapshot.State == DadAutoPartyEndpointConnectionState.Ready)
            {
                Volatile.Write(ref readyAdapterGeneration, desiredGeneration);
                Snapshot = adapterSnapshot;
            }
            else if (desiredGeneration != Volatile.Read(ref readyAdapterGeneration))
            {
                Snapshot = RefreshingSnapshot();
            }
            else
            {
                Snapshot = adapterSnapshot;
            }
        }
        else if (desiredGeneration != Volatile.Read(ref readyAdapterGeneration))
        {
            Snapshot = RefreshingSnapshot();
        }
    }

    private void PublishListingsIfDue(DateTime utcNow)
    {
        if (!configuration.IsRegistrationActive || relayPump == null || listingPublicationProvider == null ||
            utcNow < nextListingPublishUtc)
            return;
        try
        {
            var publication = listingPublicationProvider(utcNow);
            var result = relayPump.QueueListingUpdate(publication.StandingPolicy, publication.Listings);
            nextListingPublishUtc = utcNow + (result.Allowed ? ListingPublishInterval : ListingPublishRetryDelay);
            if (!result.Allowed)
                diagnostic(result.SafeCode);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        {
            nextListingPublishUtc = utcNow + ListingPublishRetryDelay;
            diagnostic("dad-listing-publication-invalid");
        }
    }

    private void ObserveAdapterStart()
    {
        if (adapterStartTask is not { IsCompleted: true } completed)
            return;
        adapterStartTask = null;
        if (disposed)
        {
            if (completed.IsCompletedSuccessfully && completed.Result.Adapter != null)
                ObserveBackgroundDisposal(completed.Result.Adapter.DisposeAsync().AsTask());
            else
                _ = completed.Exception;
            return;
        }
        if (!completed.IsCompletedSuccessfully || completed.Result.Adapter == null)
        {
            _ = completed.Exception;
            Snapshot = new(
                DadAutoPartyEndpointConnectionState.Degraded,
                completed.IsCompletedSuccessfully
                    ? completed.Result.SafeCode
                    : "dad-webhook-adapter-start-failed",
                DateTime.UtcNow,
                null,
                0,
                0,
                0,
                configuration.MailboxEpochGeneration);
            return;
        }
        if (completed.Result.Generation != Volatile.Read(ref desiredAdapterGeneration))
        {
            adapterStopTask = completed.Result.Adapter.DisposeAsync().AsTask();
            Snapshot = RefreshingSnapshot();
            return;
        }
        adapter = completed.Result.Adapter;
        attachedAdapterGeneration = completed.Result.Generation;
        connector.AttachVerifiedAdapter(adapter);
        Snapshot = completed.Result.Generation == Volatile.Read(ref readyAdapterGeneration)
            ? adapter.Snapshot
            : RefreshingSnapshot();
    }

    private void StopAdapter()
    {
        if (adapter == null || adapterStopTask != null)
            return;
        connector.DetachAdapter();
        var stopping = adapter;
        adapter = null;
        attachedAdapterGeneration = -1;
        adapterStopTask = stopping.DisposeAsync().AsTask();
    }

    private void RequestAdapterRefresh()
    {
        _ = Interlocked.Increment(ref desiredAdapterGeneration);
        connector.DetachAdapter();
        Snapshot = RefreshingSnapshot();
    }

    private DadAutoPartyEndpointSnapshot RefreshingSnapshot()
    {
        var prior = Snapshot;
        return new(
            DadAutoPartyEndpointConnectionState.Connecting,
            "dad-webhook-refreshing",
            DateTime.UtcNow,
            prior.LastSuccessfulExchangeAtUtc,
            prior.PendingOutboundCount,
            prior.PendingAcknowledgementCount,
            prior.BufferedInboundCount,
            configuration.MailboxEpochGeneration);
    }

    private void ObserveAdapterStop()
    {
        if (adapterStopTask is not { IsCompleted: true } completed)
            return;
        if (completed.IsFaulted)
        {
            _ = completed.Exception;
            diagnostic("dad-webhook-adapter-stop-failed");
        }
        adapterStopTask = null;
    }

    private async Task<LegacyCleanupResult> DeleteLegacyTokenAsync(
        string? reference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return new(true, "dad-autoparty-legacy-token-absent");
        try
        {
            await legacyTokenStore.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
            return new(true, "dad-autoparty-legacy-token-retired");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, "dad-autoparty-legacy-token-cleanup-retry");
        }
    }

    private void ObserveLegacyCleanup()
    {
        if (legacyCleanupTask is not { IsCompleted: true } completed)
            return;
        legacyCleanupTask = null;
        if (!completed.IsCompletedSuccessfully || !completed.Result.Succeeded)
        {
            _ = completed.Exception;
            configuration.LegacyDiscordTokenCleanupPending = true;
            configuration.LegacyDiscordTokenCleanupWarning = completed.IsCompletedSuccessfully
                ? completed.Result.SafeCode
                : "dad-autoparty-legacy-token-cleanup-retry";
            nextLegacyCleanupAttemptUtc = DateTime.UtcNow + LegacyCleanupRetryDelay;
            saveConfiguration();
            return;
        }
        configuration.LegacyDiscordTokenReference = null;
        configuration.LegacyDiscordTokenCleanupPending = false;
        configuration.LegacyDiscordTokenCleanupWarning = string.Empty;
        configuration.StateGeneration++;
        saveConfiguration();
    }

    private DadAutoPartyPolicyDecision HandleRegistrationReceipt(RegistrationReceipt receipt)
    {
        if (!Guid.TryParse(configuration.UplinkEpochId, out var uplinkEpochId))
            return Decision(false, "dad-registration-hello-mismatch");
        return MarkRegistrationActive(
            receipt.RegistrationId,
            uplinkEpochId,
            configuration.MailboxEpochGeneration,
            receipt.DirectoryGeneration);
    }

    private DadAutoPartyPolicyDecision Decision(bool allowed, string safeCode) =>
        new(allowed, safeCode, Math.Max(1, configuration.StateGeneration));

    private bool HasValidatedBootstrap(DateTime nowUtc) =>
        configuration.HasImportedBootstrap &&
        (configuration.RegistrationState == DadAutoPartyRegistrationState.Active ||
         configuration.BootstrapExpiresAtUtc > nowUtc);

    private static bool MailboxMatchesBootstrap(
        DadAutoPartyWebhookCredential mailbox,
        DadAutoPartyBootstrapImport bootstrap) =>
        EpochsMatch(mailbox.UplinkEpoch, bootstrap.UplinkEpoch) &&
        EpochsMatch(mailbox.DownlinkEpoch, bootstrap.DownlinkEpoch) &&
        PublicKeysMatch(mailbox.RelayPublicKeys, bootstrap.RelayPublicKeys);

    internal static bool CredentialMatchesConfiguration(
        DadAutoPartyWebhookCredential credential,
        DadAutoPartyConfiguration expected) =>
        credential.HasProvisionedMailbox &&
        string.Equals(
            credential.UplinkEpoch!.EpochId.ToString("D"),
            expected.UplinkEpochId,
            StringComparison.Ordinal) &&
        string.Equals(
            credential.DownlinkEpoch!.EpochId.ToString("D"),
            expected.DownlinkEpochId,
            StringComparison.Ordinal) &&
        string.Equals(
            credential.UplinkEpoch.IslandId.Value,
            expected.RegisteredIslandId,
            StringComparison.Ordinal) &&
        string.Equals(
            credential.DownlinkEpoch.IslandId.Value,
            expected.RegisteredIslandId,
            StringComparison.Ordinal) &&
        credential.UplinkEpoch.EpochGeneration == expected.MailboxEpochGeneration &&
        credential.DownlinkEpoch.EpochGeneration == expected.MailboxEpochGeneration &&
        credential.RelayPublicKeys!.KeyVersion == expected.RelayKeyGeneration &&
        EncodedKeyMatches(credential.RelayPublicKeys.Ed25519PublicKey, expected.RelaySigningPublicKey) &&
        EncodedKeyMatches(credential.RelayPublicKeys.X25519PublicKey, expected.RelayAgreementPublicKey);

    private static bool EpochsMatch(CourierEpochDescriptor? left, CourierEpochDescriptor right) =>
        left != null &&
        left.EpochId == right.EpochId &&
        left.IslandId == right.IslandId &&
        left.Direction == right.Direction &&
        left.EpochGeneration == right.EpochGeneration &&
        left.PageCount == right.PageCount &&
        left.StartsAt == right.StartsAt &&
        left.RotatesAt == right.RotatesAt &&
        left.OverlapEndsAt == right.OverlapEndsAt &&
        left.PageReferences.SequenceEqual(right.PageReferences);

    private static bool PublicKeysMatch(EndpointPublicKeys? left, EndpointPublicKeys right) =>
        left != null &&
        left.KeyVersion == right.KeyVersion &&
        left.Ed25519PublicKey.AsSpan().SequenceEqual(right.Ed25519PublicKey.AsSpan()) &&
        left.X25519PublicKey.AsSpan().SequenceEqual(right.X25519PublicKey.AsSpan());

    internal static bool EncodedKeyMatches(ImmutableArray<byte> expected, string encoded)
    {
        byte[]? observed = null;
        try
        {
            observed = Convert.FromBase64String(encoded);
            return observed.Length == expected.Length &&
                   CryptographicOperations.FixedTimeEquals(expected.AsSpan(), observed);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (observed != null)
                CryptographicOperations.ZeroMemory(observed);
        }
    }

    private void ObserveBackgroundDisposal(Task cleanup)
    {
        _ = cleanup.ContinueWith(
            task =>
            {
                _ = task.Exception;
                diagnostic("dad-autoparty-background-dispose-failed");
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        shutdown.Cancel();
        var stoppingPump = relayPump;
        relayPump = null;
        autoPartyService = null;
        if (stoppingPump != null)
            ObserveBackgroundDisposal(stoppingPump.DisposeAsync().AsTask());
        connector.DetachAdapter();
        var stopping = adapter;
        adapter = null;
        if (stopping != null)
            ObserveBackgroundDisposal(stopping.DisposeAsync().AsTask());
        if (adapterStopTask != null)
            ObserveBackgroundDisposal(adapterStopTask);
        if (adapterStartTask != null)
        {
            ObserveBackgroundDisposal(adapterStartTask.ContinueWith(
                async task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion && task.Result.Adapter != null)
                        await task.Result.Adapter.DisposeAsync().ConfigureAwait(false);
                    _ = task.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap());
        }
        var lifecycleTasks = new Task?[] { adapterStartTask, adapterStopTask, legacyCleanupTask }
            .Where(static task => task != null)
            .Cast<Task>()
            .ToArray();
        if (lifecycleTasks.Length == 0)
        {
            shutdown.Dispose();
        }
        else
        {
            _ = Task.WhenAll(lifecycleTasks).ContinueWith(
                task =>
                {
                    _ = task.Exception;
                    shutdown.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private sealed record AdapterStartResult(
        DadAutoPartyWebhookTransportAdapter? Adapter,
        string SafeCode,
        long Generation);

    private sealed record LegacyCleanupResult(bool Succeeded, string SafeCode);
}

internal static class DadAutoPartyRegistrationRecovery
{
    internal static bool ReconcileAtStartup(
        DadAutoPartyConfiguration configuration,
        IDadAutoPartyEndpointIdentityStore identityStore,
        IDadAutoPartyWebhookCredentialStore credentialStore)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(identityStore);
        ArgumentNullException.ThrowIfNull(credentialStore);

        var priorState = configuration.RegistrationState;
        var priorRegistrationId = configuration.RegistrationId;
        var priorBootstrapExpiry = configuration.BootstrapExpiresAtUtc;

        if (!HasConfiguredIdentity(configuration))
        {
            configuration.RegistrationState = DadAutoPartyRegistrationState.Unregistered;
            configuration.RegistrationRecoveryState = DadAutoPartyRegistrationRecoveryState.NewRegistration;
            return PersistedStateChanged(configuration, priorState, priorRegistrationId, priorBootstrapExpiry);
        }

        if (!HasValidProtectedIdentity(configuration, identityStore))
        {
            configuration.RegistrationState = DadAutoPartyRegistrationState.Unregistered;
            configuration.RegistrationRecoveryState = DadAutoPartyRegistrationRecoveryState.IdentityLost;
            return PersistedStateChanged(configuration, priorState, priorRegistrationId, priorBootstrapExpiry);
        }

        if (!configuration.HasDurableRegistrationMaterial ||
            !TryGetRegistrationIdFromRoute(configuration.RouteId, out var routedRegistrationId) ||
            !HasMatchingProtectedMailbox(configuration, credentialStore))
        {
            configuration.RegistrationState = DadAutoPartyRegistrationState.Unregistered;
            configuration.RegistrationRecoveryState =
                !string.IsNullOrWhiteSpace(configuration.RouteId) ||
                !string.IsNullOrWhiteSpace(configuration.WebhookCredentialReference)
                    ? DadAutoPartyRegistrationRecoveryState.RecoveryAvailable
                    : DadAutoPartyRegistrationRecoveryState.NewRegistration;
            return PersistedStateChanged(configuration, priorState, priorRegistrationId, priorBootstrapExpiry);
        }

        configuration.RegistrationId = routedRegistrationId.ToString("D");
        if (priorState == DadAutoPartyRegistrationState.BootstrapImported &&
            priorBootstrapExpiry > DateTime.UtcNow)
        {
            configuration.RegistrationState = DadAutoPartyRegistrationState.BootstrapImported;
            configuration.RegistrationRecoveryState = DadAutoPartyRegistrationRecoveryState.Active;
        }
        else if (priorState == DadAutoPartyRegistrationState.BootstrapImported)
        {
            configuration.RegistrationState = DadAutoPartyRegistrationState.Unregistered;
            configuration.RegistrationRecoveryState = DadAutoPartyRegistrationRecoveryState.RecoveryAvailable;
            configuration.BootstrapExpiresAtUtc = default;
        }
        else
        {
            configuration.RegistrationState = DadAutoPartyRegistrationState.Active;
            configuration.RegistrationRecoveryState = DadAutoPartyRegistrationRecoveryState.Active;
            configuration.BootstrapExpiresAtUtc = default;
        }
        return PersistedStateChanged(configuration, priorState, priorRegistrationId, priorBootstrapExpiry);
    }

    internal static bool TryGetRegistrationIdFromRoute(string? routeId, out Guid registrationId)
    {
        registrationId = Guid.Empty;
        var normalized = (routeId ?? string.Empty).Trim();
        return normalized.Length == 38 &&
               normalized.StartsWith("route-", StringComparison.Ordinal) &&
               Guid.TryParseExact(normalized.AsSpan(6), "N", out registrationId) &&
               string.Equals(normalized, $"route-{registrationId:N}", StringComparison.Ordinal);
    }

    private static bool HasConfiguredIdentity(DadAutoPartyConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.EndpointIdentityReference) ||
        !string.IsNullOrWhiteSpace(configuration.RegisteredOwnerId) ||
        !string.IsNullOrWhiteSpace(configuration.RegisteredIslandId) ||
        !string.IsNullOrWhiteSpace(configuration.RegistrationFingerprint) ||
        !string.IsNullOrWhiteSpace(configuration.SigningPublicKey) ||
        !string.IsNullOrWhiteSpace(configuration.EncryptionPublicKey);

    internal static bool HasValidProtectedIdentity(
        DadAutoPartyConfiguration configuration,
        IDadAutoPartyEndpointIdentityStore identityStore)
    {
        if (string.IsNullOrWhiteSpace(configuration.EndpointIdentityReference) ||
            string.IsNullOrWhiteSpace(configuration.RegisteredOwnerId) ||
            string.IsNullOrWhiteSpace(configuration.RegisteredIslandId) ||
            string.IsNullOrWhiteSpace(configuration.RegistrationFingerprint))
            return false;

        byte[]? material = null;
        byte[]? signingPrivate = null;
        byte[]? encryptionPrivate = null;
        byte[]? signingPublic = null;
        byte[]? encryptionPublic = null;
        try
        {
            material = identityStore.LoadAsync(
                configuration.EndpointIdentityReference,
                CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var identity = JsonSerializer.Deserialize<DadAutoPartyPrivateIdentityPackage>(material);
            if (identity == null ||
                !string.Equals(identity.OwnerId, configuration.RegisteredOwnerId, StringComparison.Ordinal) ||
                !string.Equals(identity.IslandId, configuration.RegisteredIslandId, StringComparison.Ordinal) ||
                identity.KeyGeneration != configuration.EndpointKeyGeneration)
                return false;

            signingPrivate = Convert.FromBase64String(identity.SigningPrivateKey);
            encryptionPrivate = Convert.FromBase64String(identity.EncryptionPrivateKey);
            if (signingPrivate.Length != AutoPartyProtocol.Ed25519SignatureBytes / 2 ||
                encryptionPrivate.Length != AutoPartyProtocol.X25519KeyBytes)
                return false;

            signingPublic = BouncyCastlePrimitives.DeriveEd25519PublicKey(signingPrivate);
            encryptionPublic = BouncyCastlePrimitives.DeriveX25519PublicKey(encryptionPrivate);
            return DadAutoPartyEndpointService.EncodedKeyMatches(
                       ImmutableArray.CreateRange(signingPublic),
                       configuration.SigningPublicKey) &&
                   DadAutoPartyEndpointService.EncodedKeyMatches(
                       ImmutableArray.CreateRange(encryptionPublic),
                       configuration.EncryptionPublicKey) &&
                   string.Equals(
                       DadAutoPartyIdentityPackageService.BuildFingerprint(
                           identity.OwnerId,
                           identity.IslandId,
                           identity.KeyGeneration,
                           signingPublic,
                           encryptionPublic),
                       configuration.RegistrationFingerprint,
                       StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or CryptographicException or InvalidOperationException or
                IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (material != null) CryptographicOperations.ZeroMemory(material);
            if (signingPrivate != null) CryptographicOperations.ZeroMemory(signingPrivate);
            if (encryptionPrivate != null) CryptographicOperations.ZeroMemory(encryptionPrivate);
            if (signingPublic != null) CryptographicOperations.ZeroMemory(signingPublic);
            if (encryptionPublic != null) CryptographicOperations.ZeroMemory(encryptionPublic);
        }
    }

    private static bool HasMatchingProtectedMailbox(
        DadAutoPartyConfiguration configuration,
        IDadAutoPartyWebhookCredentialStore credentialStore)
    {
        try
        {
            var credential = credentialStore.LoadAsync(
                configuration.WebhookCredentialReference,
                CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return DadAutoPartyEndpointService.CredentialMatchesConfiguration(credential, configuration);
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or CryptographicException or InvalidOperationException or
                IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool PersistedStateChanged(
        DadAutoPartyConfiguration configuration,
        DadAutoPartyRegistrationState priorState,
        string priorRegistrationId,
        DateTime priorBootstrapExpiry) =>
        configuration.RegistrationState != priorState ||
        !string.Equals(configuration.RegistrationId, priorRegistrationId, StringComparison.Ordinal) ||
        configuration.BootstrapExpiresAtUtc != priorBootstrapExpiry;
}
