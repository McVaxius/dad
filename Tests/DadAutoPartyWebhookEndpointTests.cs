using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoParty.Contracts;
using AutoParty.Core.Authentication;
using AutoParty.Core.Cryptography;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAutoPartyWebhookEndpointTests
{
    [Fact]
    public async Task CurrentUserDpapiMailboxStoreRoundTripsRedactsAndDeletes()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var root = Path.Combine(Path.GetTempPath(), "dad-webhook-mailbox", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DadAutoPartyDpapiWebhookCredentialStore(root);
            using var crypto = new CryptoFixture();
            var credential = crypto.Credential();
            var reference = await store.StoreAsync(credential);
            var loaded = await store.LoadAsync(reference);

            Assert.StartsWith("webhook-mailbox-", reference, StringComparison.Ordinal);
            Assert.Equal(credential.WebhookId, loaded.WebhookId);
            Assert.Equal(credential.ChannelId, loaded.ChannelId);
            Assert.Equal(credential.UplinkEpoch!.EpochId, loaded.UplinkEpoch!.EpochId);
            Assert.Equal(credential.DownlinkEpoch!.EpochId, loaded.DownlinkEpoch!.EpochId);
            Assert.True(credential.RelayPublicKeys!.Ed25519PublicKey.AsSpan().SequenceEqual(
                loaded.RelayPublicKeys!.Ed25519PublicKey.AsSpan()));
            Assert.Equal("DadAutoPartyWebhookCredential([redacted])", loaded.ToString());
            Assert.DoesNotContain(credential.WebhookToken, await File.ReadAllTextAsync(
                Assert.Single(Directory.GetFiles(root, "*.dpapi"))), StringComparison.Ordinal);
            Assert.True(await store.DeleteAsync(reference));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task EncryptedBootstrapImportPinsMailboxEpochsRelayKeysAndHelloActivation()
    {
        using var crypto = new CryptoFixture();
        using var identityStore = new MemoryIdentityStore();
        var registrationId = Guid.NewGuid();
        var identityMaterial = JsonSerializer.SerializeToUtf8Bytes(new DadAutoPartyPrivateIdentityPackage(
            crypto.OwnerId.Value,
            crypto.IslandId.Value,
            crypto.EndpointKeyVersion,
            Convert.ToBase64String(crypto.EndpointSigningPrivateKey),
            Convert.ToBase64String(crypto.EndpointAgreementPrivateKey)));
        var identityReference = await identityStore.StoreAsync(identityMaterial);
        CryptographicOperations.ZeroMemory(identityMaterial);

        var configuration = new DadAutoPartyConfiguration
        {
            Enabled = true,
            RegistrationState = DadAutoPartyRegistrationState.Active,
            RegistrationId = registrationId.ToString("D"),
            RouteId = $"route-{registrationId:N}",
            CentralBotApplicationId = "123456789012345678",
            HomeGuildScope = "223456789012345678",
            WebhookCredentialReference = "webhook-mailbox-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            UplinkEpochId = "11111111-1111-4111-8111-111111111111",
            DownlinkEpochId = "22222222-2222-4222-8222-222222222222",
            MailboxEpochGeneration = 1,
            RelayKeyGeneration = 1,
            RelaySigningPublicKey = Convert.ToBase64String(Enumerable.Repeat((byte)0x11, 32).ToArray()),
            RelayAgreementPublicKey = Convert.ToBase64String(Enumerable.Repeat((byte)0x22, 32).ToArray()),
            EndpointIdentityReference = identityReference,
            RegisteredOwnerId = crypto.OwnerId.Value,
            RegisteredIslandId = crypto.IslandId.Value,
            RegistrationFingerprint = DadAutoPartyIdentityPackageService.BuildFingerprint(
                crypto.OwnerId.Value,
                crypto.IslandId.Value,
                crypto.EndpointKeyVersion,
                crypto.EndpointSigningPublicKey,
                crypto.EndpointAgreementPublicKey),
            EndpointAlias = "local",
            SigningPublicKey = Convert.ToBase64String(crypto.EndpointSigningPublicKey),
            EncryptionPublicKey = Convert.ToBase64String(crypto.EndpointAgreementPublicKey),
            EndpointKeyGeneration = crypto.EndpointKeyVersion,
        }.Normalize();
        configuration.Pairings.Add(new DadAutoPartyPairing { PairingId = Guid.NewGuid().ToString("D") });
        configuration.PendingPairings.Add(new DadAutoPartyPairing { PairingId = Guid.NewGuid().ToString("D") });
        configuration.StandingSharePolicy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.CharacterList,
            CharacterHandles = ["character-one"],
            Enabled = true,
        };
        var webhookStore = new MemoryWebhookStore();
        await using var connector = new DadDiscordCourierConnector(configuration, static () => true);
        using var endpoint = new DadAutoPartyEndpointService(
            configuration,
            webhookStore,
            new MemoryLegacyTokenStore(),
            connector,
            static () => { },
            identityStore: identityStore);

        var bootstrapToken = crypto.CreateBootstrapCopyPaste(
            registrationId,
            routeId: configuration.RouteId);
        var imported = await endpoint.ImportBootstrapCopyPasteAsync(
            RegistrationCopyPasteCodec.FormatBootstrapResponse(bootstrapToken));

        Assert.True(imported.Allowed, imported.SafeCode);
        Assert.Equal(DadAutoPartyRegistrationState.Active, configuration.RegistrationState);
        Assert.Equal(DadAutoPartyRegistrationRecoveryState.Active, configuration.RegistrationRecoveryState);
        Assert.True(configuration.HasImportedBootstrap);
        Assert.True(configuration.IsRegistrationActive);
        Assert.Single(configuration.Pairings);
        Assert.Single(configuration.PendingPairings);
        Assert.True(configuration.StandingSharePolicy.Enabled);
        Assert.Equal(crypto.UplinkEpoch.EpochId.ToString("D"), configuration.UplinkEpochId);
        Assert.Equal(crypto.DownlinkEpoch.EpochId.ToString("D"), configuration.DownlinkEpochId);
        Assert.Equal(crypto.RelayKeyVersion, configuration.RelayKeyGeneration);
        Assert.Equal(Convert.ToBase64String(crypto.RelaySigningPublicKey),
            configuration.RelaySigningPublicKey);
        Assert.Equal(Convert.ToBase64String(crypto.RelayAgreementPublicKey),
            configuration.RelayAgreementPublicKey);
        Assert.NotNull(webhookStore.StoredCredential);
        Assert.Equal(crypto.UplinkEpoch.EpochId, webhookStore.StoredCredential!.UplinkEpoch!.EpochId);
        Assert.Equal(crypto.DownlinkEpoch.EpochId, webhookStore.StoredCredential.DownlinkEpoch!.EpochId);

        using (var service = new DadAutoPartyService(
                   configuration,
                   identityStore,
                   static () => true,
                   static () => { }))
        await using (var pump = new DadAutoPartyRelayPump(
                         configuration,
                         identityStore,
                         connector,
                         service,
                         new DadAutoPartyParticipantBridge(configuration),
                         new MemoryPendingOperationStore()))
        {
            var pairing = await pump.InitiatePairingAsync("island-22222222222222222222222222222222");
            Assert.True(pairing.Allowed, pairing.SafeCode);
            Assert.Equal("dad-pairing-notice-queued", pairing.SafeCode);
        }

        var refreshed = await endpoint.ImportBootstrapCopyPasteAsync(bootstrapToken);
        Assert.True(refreshed.Allowed, refreshed.SafeCode);
        Assert.True(configuration.IsRegistrationActive);

        configuration.RegistrationState = DadAutoPartyRegistrationState.BootstrapImported;
        configuration.RegistrationRecoveryState = DadAutoPartyRegistrationRecoveryState.RecoveryAvailable;
        configuration.BootstrapExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        var recoveredInProcess = await endpoint.ImportBootstrapCopyPasteAsync(
            crypto.CreateBootstrapCopyPaste(registrationId, routeId: configuration.RouteId));
        Assert.True(recoveredInProcess.Allowed, recoveredInProcess.SafeCode);
        Assert.Equal(DadAutoPartyRegistrationState.BootstrapImported, configuration.RegistrationState);
        Assert.False(configuration.IsRegistrationActive);
        Assert.Single(configuration.Pairings);
        Assert.Single(configuration.PendingPairings);
        Assert.True(configuration.StandingSharePolicy.Enabled);

        var activated = endpoint.MarkRegistrationActive(
            registrationId,
            crypto.UplinkEpoch.EpochId,
            crypto.UplinkEpoch.EpochGeneration,
            directoryGeneration: 7);

        Assert.True(activated.Allowed, activated.SafeCode);
        Assert.True(configuration.IsRegistrationActive);
        Assert.Equal(7, configuration.DirectoryGeneration);

        var activeRecovery = await endpoint.ImportBootstrapCopyPasteAsync(
            crypto.CreateBootstrapCopyPaste(registrationId, routeId: configuration.RouteId));
        Assert.True(activeRecovery.Allowed, activeRecovery.SafeCode);
        Assert.Equal(7, configuration.DirectoryGeneration);

        using var resetService = new DadAutoPartyService(
            configuration,
            identityStore,
            static () => true,
            static () => { },
            credentialStore: webhookStore);
        var reset = await resetService.PurgeAsync(deleteEndpointIdentity: false);
        Assert.True(reset.Purged, reset.SafeCode);
        Assert.Equal(1, configuration.DirectoryGeneration);
    }

    [Fact]
    public async Task ActiveBootstrapRecoveryRejectsRouteAndProtectedIdentityConflictsBeforeStorageOrMutation()
    {
        using var crypto = new CryptoFixture();
        using var identityStore = new MemoryIdentityStore();
        var registrationId = Guid.NewGuid();
        var identityMaterial = JsonSerializer.SerializeToUtf8Bytes(new DadAutoPartyPrivateIdentityPackage(
            crypto.OwnerId.Value,
            crypto.IslandId.Value,
            crypto.EndpointKeyVersion,
            Convert.ToBase64String(crypto.EndpointSigningPrivateKey),
            Convert.ToBase64String(crypto.EndpointAgreementPrivateKey)));
        var identityReference = await identityStore.StoreAsync(identityMaterial);
        CryptographicOperations.ZeroMemory(identityMaterial);
        var configuration = new DadAutoPartyConfiguration
        {
            RegistrationState = DadAutoPartyRegistrationState.Active,
            RegistrationId = registrationId.ToString("D"),
            RouteId = $"route-{registrationId:N}",
            CentralBotApplicationId = "123456789012345678",
            HomeGuildScope = "223456789012345678",
            WebhookCredentialReference = "webhook-mailbox-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            UplinkEpochId = Guid.NewGuid().ToString("D"),
            DownlinkEpochId = Guid.NewGuid().ToString("D"),
            MailboxEpochGeneration = 1,
            RelayKeyGeneration = 1,
            RelaySigningPublicKey = Convert.ToBase64String(new byte[32]),
            RelayAgreementPublicKey = Convert.ToBase64String(new byte[32]),
            EndpointIdentityReference = identityReference,
            RegisteredOwnerId = crypto.OwnerId.Value,
            RegisteredIslandId = crypto.IslandId.Value,
            RegistrationFingerprint = DadAutoPartyIdentityPackageService.BuildFingerprint(
                crypto.OwnerId.Value,
                crypto.IslandId.Value,
                crypto.EndpointKeyVersion,
                crypto.EndpointSigningPublicKey,
                crypto.EndpointAgreementPublicKey),
            EndpointAlias = "local",
            SigningPublicKey = Convert.ToBase64String(crypto.EndpointSigningPublicKey),
            EncryptionPublicKey = Convert.ToBase64String(crypto.EndpointAgreementPublicKey),
            EndpointKeyGeneration = crypto.EndpointKeyVersion,
        }.Normalize();
        var webhookStore = new MemoryWebhookStore();
        await using var connector = new DadDiscordCourierConnector(configuration, static () => true);
        using var endpoint = new DadAutoPartyEndpointService(
            configuration,
            webhookStore,
            new MemoryLegacyTokenStore(),
            connector,
            static () => { },
            identityStore: identityStore);

        var routeConflictSnapshot = JsonSerializer.Serialize(configuration);
        var routeConflict = await endpoint.ImportBootstrapCopyPasteAsync(
            crypto.CreateBootstrapCopyPaste(registrationId, routeId: "route-conflict"));
        Assert.False(routeConflict.Allowed);
        Assert.Equal("dad-bootstrap-recovery-mismatch", routeConflict.SafeCode);
        Assert.Equal(routeConflictSnapshot, JsonSerializer.Serialize(configuration));
        Assert.Null(webhookStore.StoredCredential);

        configuration.SigningPublicKey = Convert.ToBase64String(new byte[32]);
        var identityConflictSnapshot = JsonSerializer.Serialize(configuration);
        var identityConflict = await endpoint.ImportBootstrapCopyPasteAsync(
            crypto.CreateBootstrapCopyPaste(registrationId, routeId: configuration.RouteId));
        Assert.False(identityConflict.Allowed);
        Assert.Equal("dad-bootstrap-recovery-mismatch", identityConflict.SafeCode);
        Assert.Equal(identityConflictSnapshot, JsonSerializer.Serialize(configuration));
        Assert.Null(webhookStore.StoredCredential);
    }

    [Fact]
    public async Task ActiveRecoveryRebindsRunningMailboxPreservesTrustAndSendsQueuedPairingOnlyThroughReplacement()
    {
        const string ownerId = "owner-11111111111111111111111111111111";
        const string islandId = "island-11111111111111111111111111111111";
        const string peerIslandId = "island-22222222222222222222222222222222";
        const string queuedPeerIslandId = "island-33333333333333333333333333333333";
        const string oldReference = "webhook-mailbox-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string newReference = "webhook-mailbox-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string oldWebhookId = "333333333333333333";
        const string newWebhookId = "323456789012345678";
        using var crypto = new CryptoFixture(ownerId, islandId);
        using var identityStore = new MemoryIdentityStore();
        var identityMaterial = JsonSerializer.SerializeToUtf8Bytes(new DadAutoPartyPrivateIdentityPackage(
            ownerId,
            islandId,
            crypto.EndpointKeyVersion,
            Convert.ToBase64String(crypto.EndpointSigningPrivateKey),
            Convert.ToBase64String(crypto.EndpointAgreementPrivateKey)));
        var identityReference = await identityStore.StoreAsync(identityMaterial);
        CryptographicOperations.ZeroMemory(identityMaterial);
        var registrationId = Guid.Parse("44444444-4444-4444-8444-444444444444");
        var routeId = $"route-{registrationId:N}";
        var now = DateTimeOffset.UtcNow;
        var oldUplink = new CourierEpochDescriptor(
            Guid.Parse("55555555-5555-4555-8555-555555555555"),
            crypto.IslandId,
            CourierDirection.Uplink,
            now.AddMinutes(-1),
            now.AddMinutes(30),
            now.AddMinutes(35),
            1,
            [new CourierPageReference(1, "10001")],
            1);
        var oldDownlink = new CourierEpochDescriptor(
            Guid.Parse("66666666-6666-4666-8666-666666666666"),
            crypto.IslandId,
            CourierDirection.Downlink,
            now.AddMinutes(-1),
            now.AddMinutes(30),
            now.AddMinutes(35),
            1,
            [new CourierPageReference(1, "10002")],
            1);
        var oldCredential = new DadAutoPartyWebhookCredential(
            oldWebhookId,
            new string('b', 64),
            "433333333333333333")
        {
            UplinkEpoch = oldUplink,
            DownlinkEpoch = oldDownlink,
            RelayPublicKeys = crypto.RelayPublicKeys,
        };
        var credentialStore = new RotatingMemoryWebhookStore(
            oldReference,
            oldCredential,
            newReference);
        var savedStandingPolicy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.CharacterList,
            CharacterHandles = [],
            Enabled = false,
            Revision = 3,
            UpdatedAtUtc = now.UtcDateTime,
        };
        var activePairing = new DadAutoPartyPairing
        {
            PairingId = Guid.Parse("77777777-7777-4777-8777-777777777777").ToString("D"),
            OwnerId = "owner-22222222222222222222222222222222",
            IslandId = peerIslandId,
            HomeGuildScope = "223456789012345678",
            PublicKeyFingerprint = new string('1', 64),
            LocalFingerprint = new string('2', 64),
            TranscriptHash = new string('3', 64),
            ConfirmationCodeHash = new string('4', 64),
            LocalApproved = true,
            PeerApproved = true,
            LocalApprovalRelayMessageId = Guid.Parse("88888888-8888-4888-8888-888888888888").ToString("D"),
            LocalApprovalRelayAcceptedAtUtc = now.UtcDateTime,
            LocalSharePolicy = new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.CharacterList,
                CharacterHandles = ["opaque-local"],
                Enabled = true,
                UpdatedAtUtc = now.UtcDateTime,
            },
            PeerSharePolicy = new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.CharacterList,
                CharacterHandles = ["opaque-peer"],
                Enabled = true,
                UpdatedAtUtc = now.UtcDateTime,
            },
            ExpiresAtUtc = now.AddHours(1).UtcDateTime,
            KeyGeneration = 1,
            SigningPublicKey = Convert.ToBase64String(Enumerable.Repeat((byte)0x51, 32).ToArray()),
            AgreementPublicKey = Convert.ToBase64String(Enumerable.Repeat((byte)0x61, 32).ToArray()),
            ConfirmedAtUtc = now.UtcDateTime,
        };
        var configuration = new DadAutoPartyConfiguration
        {
            Enabled = true,
            RegistrationState = DadAutoPartyRegistrationState.Active,
            RegistrationId = registrationId.ToString("D"),
            RouteId = routeId,
            CentralBotApplicationId = "123456789012345678",
            HomeGuildScope = "223456789012345678",
            WebhookCredentialReference = oldReference,
            UplinkEpochId = oldUplink.EpochId.ToString("D"),
            DownlinkEpochId = oldDownlink.EpochId.ToString("D"),
            MailboxEpochGeneration = oldUplink.EpochGeneration,
            RelayKeyGeneration = crypto.RelayKeyVersion,
            RelaySigningPublicKey = Convert.ToBase64String(crypto.RelaySigningPublicKey),
            RelayAgreementPublicKey = Convert.ToBase64String(crypto.RelayAgreementPublicKey),
            EndpointIdentityReference = identityReference,
            RegisteredOwnerId = ownerId,
            RegisteredIslandId = islandId,
            RegistrationFingerprint = DadAutoPartyIdentityPackageService.BuildFingerprint(
                ownerId,
                islandId,
                crypto.EndpointKeyVersion,
                crypto.EndpointSigningPublicKey,
                crypto.EndpointAgreementPublicKey),
            EndpointAlias = "recovery-endpoint",
            SigningPublicKey = Convert.ToBase64String(crypto.EndpointSigningPublicKey),
            EncryptionPublicKey = Convert.ToBase64String(crypto.EndpointAgreementPublicKey),
            EndpointKeyGeneration = crypto.EndpointKeyVersion,
            StandingSharePolicy = savedStandingPolicy,
            Pairings = [activePairing],
        }.Normalize();
        configuration.RegistrationRecoveryState = DadAutoPartyRegistrationRecoveryState.Active;
        using var handler = new RecoveryWebhookHandler(newWebhookId);
        using var service = new DadAutoPartyService(
            configuration,
            identityStore,
            static () => true,
            static () => { },
            credentialStore: credentialStore);
        var diagnostics = new List<string>();
        var diagnosticGate = new object();
        void RecordDiagnostic(string line)
        {
            lock (diagnosticGate)
                diagnostics.Add(line);
        }

        string[] ReadDiagnostics()
        {
            lock (diagnosticGate)
                return diagnostics.ToArray();
        }

        using var endpoint = new DadAutoPartyEndpointService(
            configuration,
            credentialStore,
            new MemoryLegacyTokenStore(),
            service.Connector,
            static () => { },
            diagnostic: RecordDiagnostic,
            httpClientFactory: () => new HttpClient(handler, disposeHandler: false),
            identityStore: identityStore);
        var pumpSleeping = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pump = new DadAutoPartyRelayPump(
            configuration,
            identityStore,
            service.Connector,
            service,
            new DadAutoPartyParticipantBridge(configuration),
            new MemoryPendingOperationStore(),
            delay: (_, cancellationToken) =>
            {
                pumpSleeping.TrySetResult(true);
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        for (var attempt = 0; attempt < 200 &&
             endpoint.Snapshot.State != DadAutoPartyEndpointConnectionState.Ready; attempt++)
        {
            endpoint.Update(dadEnabled: true);
            await Task.Delay(5);
        }
        Assert.Equal(DadAutoPartyEndpointConnectionState.Ready, endpoint.Snapshot.State);
        Assert.Contains(ReadDiagnostics(), line => line.Contains(
            "stage=adapter-start-requested safeCode=dad-webhook-adapter-start-requested",
            StringComparison.Ordinal));
        Assert.Contains(ReadDiagnostics(), line => line.Contains(
            "stage=adapter-ready safeCode=dad-webhook-adapter-ready",
            StringComparison.Ordinal));
        endpoint.AttachRelayPump(pump, service);
        await pumpSleeping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = await endpoint.InitiatePairingAsync(queuedPeerIslandId);
        Assert.True(queued.Allowed, queued.SafeCode);
        Assert.Equal(1, pump.Snapshot.PendingOutboundCount);

        var imported = await endpoint.ImportBootstrapCopyPasteAsync(
            crypto.CreateBootstrapCopyPaste(registrationId, routeId: routeId));

        Assert.True(imported.Allowed, imported.SafeCode);
        Assert.Equal(DadAutoPartyRegistrationState.Active, configuration.RegistrationState);
        Assert.Equal(DadAutoPartyRegistrationRecoveryState.Active, configuration.RegistrationRecoveryState);
        Assert.True(Assert.Single(configuration.Pairings).IsActive);
        Assert.Same(savedStandingPolicy, configuration.StandingSharePolicy);
        Assert.Equal(DadAutoPartyCharacterShareMode.CharacterList, savedStandingPolicy.Mode);
        Assert.False(savedStandingPolicy.Enabled);
        Assert.Empty(savedStandingPolicy.CharacterHandles);
        Assert.Equal(DadAutoPartyEndpointConnectionState.Connecting, endpoint.Snapshot.State);
        Assert.Equal("dad-webhook-refreshing", endpoint.Snapshot.SafeCode);
        Assert.Same(DadAutoPartyAdapterTransferSnapshot.Idle, endpoint.TransferSnapshot);
        Assert.Contains(ReadDiagnostics(), line => line.Contains(
            "stage=adapter-refresh-requested safeCode=dad-webhook-refreshing",
            StringComparison.Ordinal));
        var refreshingRegistration = DadAutoPartyProgressProjection.Registration(
            configuration,
            endpoint.Snapshot,
            DateTime.UtcNow);
        Assert.True(refreshingRegistration.RegistrationActive);
        Assert.False(refreshingRegistration.MailboxReady);
        Assert.Equal(DadAutoPartyProgressState.NotRequired, refreshingRegistration.ActivationReceipt);
        var refreshingPairing = DadAutoPartyProgressProjection.Pairing(
            configuration,
            endpoint.Snapshot,
            endpoint.LastPairingChallenge,
            endpoint.LastPairingAttemptResult,
            queuedPeerIslandId,
            null,
            DateTime.UtcNow);
        Assert.True(refreshingPairing.RegistrationActive);
        Assert.False(refreshingPairing.MailboxReady);
        Assert.Equal("dad-pairing-mailbox-refreshing", refreshingPairing.SafeCode);
        var gated = await endpoint.InitiatePairingAsync(peerIslandId);
        Assert.False(gated.Allowed);
        Assert.Equal("dad-pairing-mailbox-refreshing", gated.SafeCode);

        await pump.ProcessOnceAsync();
        Assert.Equal(1, pump.Snapshot.PendingOutboundCount);
        Assert.DoesNotContain(handler.Pages, page =>
            page.WebhookId == oldWebhookId &&
            page.PayloadTypes.Contains(ProtocolContractRegistry.GetTypeId<PairingNotice>()));

        for (var attempt = 0; attempt < 400 &&
             endpoint.Snapshot.State != DadAutoPartyEndpointConnectionState.Ready; attempt++)
        {
            endpoint.Update(dadEnabled: true);
            await Task.Delay(5);
        }
        Assert.Equal(DadAutoPartyEndpointConnectionState.Ready, endpoint.Snapshot.State);
        Assert.Equal(2, ReadDiagnostics().Count(line => line.Contains(
            "stage=adapter-start-requested safeCode=dad-webhook-adapter-start-requested",
            StringComparison.Ordinal)));
        Assert.Equal(2, ReadDiagnostics().Count(line => line.Contains(
            "stage=adapter-ready safeCode=dad-webhook-adapter-ready",
            StringComparison.Ordinal)));
        Assert.Contains(ReadDiagnostics(), line => line.Contains(
            "stage=adapter-stop-requested safeCode=dad-webhook-adapter-stop-requested",
            StringComparison.Ordinal));
        var recoveredReady = DadAutoPartyProgressProjection.Registration(
            configuration,
            endpoint.Snapshot,
            DateTime.UtcNow);
        Assert.True(recoveredReady.RegistrationActive);
        Assert.True(recoveredReady.MailboxReady);
        await handler.NewMailboxReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await pump.ProcessOnceAsync();
        Assert.Equal(0, pump.Snapshot.PendingOutboundCount);
        handler.ReleaseNewMailbox();
        for (var attempt = 0; attempt < 400 &&
             handler.Pages.Count(page => page.PayloadTypes.Contains(
                 ProtocolContractRegistry.GetTypeId<PairingNotice>())) < 1; attempt++)
            await Task.Delay(5);

        var pairingPage = Assert.Single(handler.Pages, page =>
            page.PayloadTypes.Contains(ProtocolContractRegistry.GetTypeId<PairingNotice>()));
        Assert.Equal(newWebhookId, pairingPage.WebhookId);
        Assert.DoesNotContain(handler.Pages, page =>
            page.PayloadTypes.Contains(ProtocolContractRegistry.GetTypeId<RegistrationHello>()));
        Assert.Equal(1, pump.Snapshot.AwaitingRelayReceiptCount);
    }

    [Fact]
    public async Task BootstrapCopyPasteRejectsTamperedTruncatedExpiredWrongRecipientLegacyAndAmbiguousInput()
    {
        using var crypto = new CryptoFixture();
        using var identityStore = new MemoryIdentityStore();
        var registrationId = Guid.NewGuid();
        var identityMaterial = JsonSerializer.SerializeToUtf8Bytes(new DadAutoPartyPrivateIdentityPackage(
            crypto.OwnerId.Value,
            crypto.IslandId.Value,
            crypto.EndpointKeyVersion,
            Convert.ToBase64String(crypto.EndpointSigningPrivateKey),
            Convert.ToBase64String(crypto.EndpointAgreementPrivateKey)));
        var identityReference = await identityStore.StoreAsync(identityMaterial);
        CryptographicOperations.ZeroMemory(identityMaterial);
        var configuration = new DadAutoPartyConfiguration
        {
            RegistrationState = DadAutoPartyRegistrationState.Unregistered,
            RegistrationId = registrationId.ToString("D"),
            EndpointIdentityReference = identityReference,
            RegisteredOwnerId = crypto.OwnerId.Value,
            RegisteredIslandId = crypto.IslandId.Value,
            RegistrationFingerprint = DadAutoPartyIdentityPackageService.BuildFingerprint(
                crypto.OwnerId.Value,
                crypto.IslandId.Value,
                crypto.EndpointKeyVersion,
                crypto.EndpointSigningPublicKey,
                crypto.EndpointAgreementPublicKey),
            EndpointAlias = "local",
            SigningPublicKey = Convert.ToBase64String(crypto.EndpointSigningPublicKey),
            EncryptionPublicKey = Convert.ToBase64String(crypto.EndpointAgreementPublicKey),
            EndpointKeyGeneration = crypto.EndpointKeyVersion,
        }.Normalize();
        var webhookStore = new MemoryWebhookStore();
        await using var connector = new DadDiscordCourierConnector(configuration, static () => true);
        using var endpoint = new DadAutoPartyEndpointService(
            configuration,
            webhookStore,
            new MemoryLegacyTokenStore(),
            connector,
            static () => { },
            identityStore: identityStore);
        var token = crypto.CreateBootstrapCopyPaste(registrationId);
        var envelope = RegistrationCopyPasteCodec.DecodeBootstrap(token);
        var legacy = Convert.ToBase64String(SealedContractCodec.Encode(envelope))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var wrongRecipient = crypto.CreateBootstrapCopyPaste(
            registrationId,
            recipient: new IslandId("island-wrong-recipient"));
        var expiredIssuedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var expired = crypto.CreateBootstrapCopyPaste(
            registrationId,
            issuedAt: expiredIssuedAt,
            expiresAt: expiredIssuedAt.AddMinutes(5));
        var tampered = token[..^1] + (token[^1] == 'A' ? "B" : "A");
        var inputs = new[]
        {
            tampered,
            token[..^1],
            expired,
            wrongRecipient,
            crypto.CreateBootstrapCopyPaste(Guid.NewGuid()),
            legacy,
            "APB1.not-base64!",
            RegistrationCopyPasteCodec.FormatBootstrapResponse(token) + "\n" + token,
            token + "\n" + token,
        };

        foreach (var input in inputs)
        {
            var rejected = await endpoint.ImportBootstrapCopyPasteAsync(input);
            Assert.False(rejected.Allowed);
            Assert.Contains(
                rejected.SafeCode,
                new[] { "dad-bootstrap-open-rejected", "dad-bootstrap-invalid" });
            Assert.Equal(DadAutoPartyRegistrationState.Unregistered, configuration.RegistrationState);
            Assert.Null(webhookStore.StoredCredential);
        }

        var accepted = await endpoint.ImportBootstrapCopyPasteAsync(token);
        Assert.True(accepted.Allowed, accepted.SafeCode);
        Assert.Equal(DadAutoPartyRegistrationState.BootstrapImported, configuration.RegistrationState);
        Assert.False(configuration.IsRegistrationActive);
    }

    [Fact]
    public void AutoPartyWindowShowsProgressCardsDirectBilateralPairingAndUnmaskedBootstrapInput()
    {
        var source = ReadRepositorySource("Windows", "DadAutoPartyWindow.cs");
        var bootstrapStart = source.IndexOf("\"Encrypted bootstrap DM\"", StringComparison.Ordinal);
        var bootstrapEnd = source.IndexOf("\"Import bootstrap\"", bootstrapStart, StringComparison.Ordinal);

        Assert.Contains("Enable bot DMs before registering", source, StringComparison.Ordinal);
        Assert.Contains("transport-channel traffic is private machine traffic", source, StringComparison.Ordinal);
        Assert.Contains("Registration & mailbox", source, StringComparison.Ordinal);
        Assert.Contains("Mailbox activity", source, StringComparison.Ordinal);
        Assert.Contains("Island pairing", source, StringComparison.Ordinal);
        Assert.Contains("Accepted fragments:", source, StringComparison.Ordinal);
        Assert.Contains("awaiting semantic receipt", source, StringComparison.Ordinal);
        Assert.Contains("Raw safe code:", source, StringComparison.Ordinal);
        Assert.Contains("Activation receipt not required - Active recovery", source, StringComparison.Ordinal);
        Assert.Contains("Local approval accepted by central", source, StringComparison.Ordinal);
        Assert.Contains("Recover registration", source, StringComparison.Ordinal);
        Assert.Contains(
            "configuration.RegistrationState is DadAutoPartyRegistrationState.Active or",
            source,
            StringComparison.Ordinal);
        Assert.Contains("var registrationLocked = identityLost ||", source, StringComparison.Ordinal);
        Assert.Contains("activationPending ||", source, StringComparison.Ordinal);
        Assert.Contains("Forget old identity and register as new", source, StringComparison.Ordinal);
        Assert.Contains("/autoparty deregister island:", source, StringComparison.Ordinal);
        Assert.Contains("I confirm the owner deregistration completed", source, StringComparison.Ordinal);
        Assert.Contains("ImGui.BeginDisabled(!registrationReady)", source, StringComparison.Ordinal);
        Assert.Contains("This DAD island ID", source, StringComparison.Ordinal);
        Assert.Contains("Copy island ID", source, StringComparison.Ordinal);
        Assert.Contains("Peer island ID", source, StringComparison.Ordinal);
        Assert.Contains("Initiate bilateral pairing by island ID", source, StringComparison.Ordinal);
        Assert.Contains("Pairing is bilateral", source, StringComparison.Ordinal);
        Assert.Contains("Only one DAD initiates a pairing", source, StringComparison.Ordinal);
        Assert.Contains("The peer waits for the pending notice", source, StringComparison.Ordinal);
        Assert.Contains("pairingInitiationPending", source, StringComparison.Ordinal);
        Assert.Contains("if (pending.LocalApproved)", source, StringComparison.Ordinal);
        Assert.Contains("Local approval is saved; waiting for central to accept it.", source, StringComparison.Ordinal);
        var locallyApprovedBranch = source.IndexOf("if (pending.LocalApproved)", StringComparison.Ordinal);
        var confirmedFingerprintInput = source.IndexOf("ImGui.InputText(\"Confirmed peer fingerprint\"", StringComparison.Ordinal);
        var pairingCodeInput = source.IndexOf("ImGui.InputText(\"Pairing code\"", StringComparison.Ordinal);
        var approvalButton = source.IndexOf("Approve pairing locally", StringComparison.Ordinal);
        Assert.True(locallyApprovedBranch >= 0 && locallyApprovedBranch < confirmedFingerprintInput);
        Assert.True(pairingCodeInput > confirmedFingerprintInput);
        Assert.DoesNotContain("ImGuiInputTextFlags.Password", source[pairingCodeInput..approvalButton], StringComparison.Ordinal);
        Assert.Contains("This character\\0Specific characters\\0All characters", source, StringComparison.Ordinal);
        Assert.Contains("DadAutoPartyCrewShareScope", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Opaque handles (comma-separated)", source, StringComparison.Ordinal);
        Assert.True(bootstrapStart >= 0);
        Assert.True(bootstrapEnd > bootstrapStart);
        Assert.DoesNotContain("Password", source[bootstrapStart..bootstrapEnd], StringComparison.Ordinal);
        Assert.Contains("Last mailbox exchange", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Registration {configuration.RegistrationState}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Mailbox queues:", source, StringComparison.Ordinal);
        Assert.Contains("active, online", source, StringComparison.Ordinal);
        Assert.Contains("active, offline", source, StringComparison.Ordinal);
        Assert.Contains("directory.OnlineIslandIds.Contains", source, StringComparison.Ordinal);
        Assert.Contains("Status: {status}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TransportChannelIds", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewChannel", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistrationProgressProjectsLifecycleRecoveryAndIdentityLossWithoutActivityRegression()
    {
        var now = DateTime.UtcNow;
        var notRegistered = EndpointSnapshot(
            DadAutoPartyEndpointConnectionState.NotRegistered,
            "dad-webhook-not-registered");
        var ready = EndpointSnapshot(DadAutoPartyEndpointConnectionState.Ready, "dad-webhook-ready");

        var fresh = DadAutoPartyProgressProjection.Registration(
            new DadAutoPartyConfiguration { Enabled = true },
            notRegistered,
            now);
        Assert.False(fresh.EndpointIdentityReady);
        Assert.False(fresh.ChallengeGenerated);
        Assert.False(fresh.BootstrapImportedAndProtected);
        Assert.False(fresh.RegistrationActive);
        Assert.False(fresh.MailboxReady);

        var activatingConfiguration = ActiveConfiguration();
        activatingConfiguration.RegistrationState = DadAutoPartyRegistrationState.BootstrapImported;
        activatingConfiguration.BootstrapExpiresAtUtc = now.AddMinutes(5);
        var activating = DadAutoPartyProgressProjection.Registration(
            activatingConfiguration,
            ready,
            now);
        Assert.True(activating.EndpointIdentityReady);
        Assert.True(activating.ChallengeGenerated);
        Assert.True(activating.BootstrapImportedAndProtected);
        Assert.Equal(DadAutoPartyProgressState.Pending, activating.ActivationReceipt);
        Assert.False(activating.RegistrationActive);
        Assert.True(activating.MailboxReady);

        activatingConfiguration.BootstrapExpiresAtUtc = now.AddSeconds(-1);
        var expired = DadAutoPartyProgressProjection.Registration(
            activatingConfiguration,
            notRegistered,
            now);
        Assert.True(expired.BootstrapImportedAndProtected);
        Assert.Contains("Recover this registration", expired.NextAction, StringComparison.Ordinal);

        var recoveryAvailableConfiguration = ActiveConfiguration();
        recoveryAvailableConfiguration.RegistrationState = DadAutoPartyRegistrationState.Unregistered;
        recoveryAvailableConfiguration.RegistrationRecoveryState =
            DadAutoPartyRegistrationRecoveryState.RecoveryAvailable;
        var recoveryAvailable = DadAutoPartyProgressProjection.Registration(
            recoveryAvailableConfiguration,
            notRegistered,
            now);
        Assert.True(recoveryAvailable.EndpointIdentityReady);
        Assert.True(recoveryAvailable.ChallengeGenerated);
        Assert.False(recoveryAvailable.BootstrapImportedAndProtected);
        Assert.Contains("Recover this registration", recoveryAvailable.NextAction, StringComparison.Ordinal);

        var activeConfiguration = ActiveConfiguration();
        activeConfiguration.RegistrationRecoveryState = DadAutoPartyRegistrationRecoveryState.Active;
        var refreshing = DadAutoPartyProgressProjection.Registration(
            activeConfiguration,
            EndpointSnapshot(DadAutoPartyEndpointConnectionState.Connecting, "dad-webhook-refreshing"),
            now);
        Assert.True(refreshing.RegistrationActive);
        Assert.False(refreshing.MailboxReady);
        Assert.Equal(DadAutoPartyProgressState.NotRequired, refreshing.ActivationReceipt);
        Assert.Contains("replacement mailbox", refreshing.NextAction, StringComparison.Ordinal);

        var activeReady = DadAutoPartyProgressProjection.Registration(activeConfiguration, ready, now);
        Assert.True(activeReady.EndpointIdentityReady);
        Assert.True(activeReady.ChallengeGenerated);
        Assert.True(activeReady.BootstrapImportedAndProtected);
        Assert.Equal(DadAutoPartyProgressState.Complete, activeReady.ActivationReceipt);
        Assert.True(activeReady.RegistrationActive);
        Assert.True(activeReady.MailboxReady);

        var afterListing = DadAutoPartyProgressProjection.Registration(
            activeConfiguration,
            ready with { SafeCode = "dad-webhook-uplink-fragment-published" },
            now);
        Assert.Equal(activeReady.EndpointIdentityReady, afterListing.EndpointIdentityReady);
        Assert.Equal(activeReady.ChallengeGenerated, afterListing.ChallengeGenerated);
        Assert.Equal(activeReady.BootstrapImportedAndProtected, afterListing.BootstrapImportedAndProtected);
        Assert.Equal(activeReady.ActivationReceipt, afterListing.ActivationReceipt);
        Assert.Equal(activeReady.RegistrationActive, afterListing.RegistrationActive);
        Assert.Equal(activeReady.MailboxReady, afterListing.MailboxReady);
        Assert.Equal(activeReady.NextAction, afterListing.NextAction);

        var identityLostConfiguration = ActiveConfiguration();
        identityLostConfiguration.RegistrationState = DadAutoPartyRegistrationState.Unregistered;
        identityLostConfiguration.RegistrationRecoveryState = DadAutoPartyRegistrationRecoveryState.IdentityLost;
        var identityLost = DadAutoPartyProgressProjection.Registration(
            identityLostConfiguration,
            notRegistered,
            now);
        Assert.False(identityLost.EndpointIdentityReady);
        Assert.False(identityLost.RegistrationActive);
        Assert.Equal(DadAutoPartyProgressState.Blocked, identityLost.ActivationReceipt);
        Assert.Contains("Owner deregistration", identityLost.NextAction, StringComparison.Ordinal);
    }

    [Fact]
    public void PairingProgressProjectsInitiatorPeerApprovalOrdersExpiryRejectionAndActivation()
    {
        const string localIsland = "island-11111111111111111111111111111111";
        const string peerIsland = "island-22222222222222222222222222222222";
        var now = DateTime.UtcNow;
        var ready = EndpointSnapshot(DadAutoPartyEndpointConnectionState.Ready, "dad-webhook-ready");
        var initiatorConfiguration = ActiveConfiguration();
        initiatorConfiguration.RegisteredIslandId = localIsland;
        var pairingId = Guid.NewGuid();
        var challenge = new DadAutoPartyPairingChallenge(
            pairingId,
            initiatorConfiguration.RegisteredOwnerId,
            localIsland,
            initiatorConfiguration.RegistrationFingerprint,
            1,
            "123456",
            now.AddMinutes(10));
        var initiator = DadAutoPartyProgressProjection.Pairing(
            initiatorConfiguration,
            ready,
            challenge,
            null,
            peerIsland,
            "dad-pairing-notice-queued",
            now);
        Assert.Equal(DadAutoPartyProgressState.Complete, initiator.PeerIdValidated);
        Assert.Equal(DadAutoPartyProgressState.Complete, initiator.NoticeQueued);
        Assert.False(initiator.NoticeAcceptedAndPendingPairingReceived);
        Assert.False(initiator.PairingActive);
        Assert.Equal(pairingId.ToString("D"), initiator.PairingId);

        var queuedBehindListing = DadAutoPartyProgressProjection.Pairing(
            initiatorConfiguration,
            ready,
            challenge,
            null,
            peerIsland,
            "dad-pairing-notice-queued",
            now,
            peerIsland,
            new DadAutoPartyAdapterTransferSnapshot(
                ProtocolContractRegistry.GetTypeId<PrivateListingUpdate>(),
                3,
                4,
                4,
                true));
        Assert.Contains("queued behind listing update", queuedBehindListing.NextAction, StringComparison.Ordinal);
        Assert.Contains("accepted fragments 3 / 4", queuedBehindListing.NextAction, StringComparison.Ordinal);
        Assert.Contains("current fragment 4 / 4", queuedBehindListing.NextAction, StringComparison.Ordinal);

        var peerConfiguration = ActiveConfiguration();
        peerConfiguration.RegisteredIslandId = localIsland;
        var pairing = ProgressPairing(pairingId, peerIsland, now.AddMinutes(10));
        peerConfiguration.PendingPairings.Add(pairing);
        var peerPending = DadAutoPartyProgressProjection.Pairing(
            peerConfiguration,
            ready,
            null,
            null,
            null,
            null,
            now);
        Assert.Equal(DadAutoPartyProgressState.NotRequired, peerPending.NoticeQueued);
        Assert.True(peerPending.NoticeAcceptedAndPendingPairingReceived);
        Assert.False(peerPending.LocalApprovalSaved);
        Assert.Equal(pairingId.ToString("D"), peerPending.PairingId);

        var losingPairingId = Guid.NewGuid();
        var losingChallenge = challenge with { ChallengeId = losingPairingId };
        var simultaneous = DadAutoPartyProgressProjection.Pairing(
            peerConfiguration,
            ready,
            losingChallenge,
            new DadAutoPartyPairingAttemptResult(
                losingPairingId,
                peerIsland,
                "pairing-route-already-exists",
                new DateTimeOffset(now, TimeSpan.Zero)),
            peerIsland,
            "pairing-route-already-exists",
            now,
            peerIsland);
        Assert.Equal(pairingId.ToString("D"), simultaneous.PairingId);
        Assert.Equal(DadAutoPartyProgressState.NotRequired, simultaneous.NoticeQueued);
        Assert.True(simultaneous.NoticeAcceptedAndPendingPairingReceived);
        Assert.False(simultaneous.ExpiredOrRejected);
        Assert.Equal("dad-pairing-notice-pending", simultaneous.SafeCode);

        pairing.PeerApproved = true;
        var peerFirst = DadAutoPartyProgressProjection.Pairing(
            peerConfiguration, ready, null, null, null, null, now);
        Assert.True(peerFirst.PeerApprovalReceived);
        Assert.False(peerFirst.PairingActive);

        pairing.LocalApproved = true;
        pairing.LocalApprovalRelayMessageId = Guid.NewGuid().ToString("D");
        pairing.LocalSharePolicy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.CharacterList,
            Enabled = false,
            CharacterHandles = [],
            Revision = 2,
            UpdatedAtUtc = now,
        };
        var locallyApproved = DadAutoPartyProgressProjection.Pairing(
            peerConfiguration, ready, null, null, null, null, now);
        Assert.True(locallyApproved.LocalApprovalSaved);
        Assert.False(locallyApproved.LocalApprovalAcceptedByCentral);
        Assert.False(locallyApproved.PairingActive);

        pairing.LocalApprovalRelayAcceptedAtUtc = now;
        pairing.ConfirmedAtUtc = now;
        var allFieldsStillPending = DadAutoPartyProgressProjection.Pairing(
            peerConfiguration, ready, null, null, null, null, now);
        Assert.True(allFieldsStillPending.LocalApprovalAcceptedByCentral);
        Assert.True(allFieldsStillPending.PeerApprovalReceived);
        Assert.False(allFieldsStillPending.PairingActive);

        peerConfiguration.PendingPairings.Clear();
        pairing.ExpiresAtUtc = now.AddMinutes(-1);
        peerConfiguration.Pairings.Add(pairing);
        var active = DadAutoPartyProgressProjection.Pairing(
            peerConfiguration, ready, null, null, null, null, now);
        Assert.True(active.PairingActive);
        Assert.False(active.ExpiredOrRejected);
        Assert.Equal("dad-pairing-active", active.SafeCode);

        var rejectedConfiguration = ActiveConfiguration();
        rejectedConfiguration.RegisteredIslandId = localIsland;
        var rejectedPairingId = Guid.NewGuid();
        var rejected = DadAutoPartyProgressProjection.Pairing(
            rejectedConfiguration,
            ready,
            null,
            new DadAutoPartyPairingAttemptResult(
                rejectedPairingId,
                peerIsland,
                "central-pairing-peer-offline",
                new DateTimeOffset(now, TimeSpan.Zero)),
            peerIsland,
            null,
            now);
        Assert.True(rejected.ExpiredOrRejected);
        Assert.Equal("central-pairing-peer-offline", rejected.SafeCode);
        Assert.Equal(rejectedPairingId.ToString("D"), rejected.PairingId);

        var expiredConfiguration = ActiveConfiguration();
        expiredConfiguration.RegisteredIslandId = localIsland;
        expiredConfiguration.PendingPairings.Add(
            ProgressPairing(Guid.NewGuid(), peerIsland, now.AddSeconds(-1)));
        var expired = DadAutoPartyProgressProjection.Pairing(
            expiredConfiguration, ready, null, null, null, null, now);
        Assert.True(expired.ExpiredOrRejected);
        Assert.False(expired.PairingActive);
        Assert.Equal("dad-pairing-expired", expired.SafeCode);

        var freshPairingId = Guid.NewGuid();
        var freshChallenge = challenge with { ChallengeId = freshPairingId };
        var freshOverExpired = DadAutoPartyProgressProjection.Pairing(
            expiredConfiguration,
            ready,
            freshChallenge,
            null,
            peerIsland,
            "dad-pairing-notice-queued",
            now,
            peerIsland);
        Assert.Equal(freshPairingId.ToString("D"), freshOverExpired.PairingId);
        Assert.False(freshOverExpired.NoticeAcceptedAndPendingPairingReceived);
        Assert.False(freshOverExpired.ExpiredOrRejected);
        Assert.Equal("dad-pairing-notice-queued", freshOverExpired.SafeCode);
    }

    [Fact]
    public void MailboxActivityUsesFriendlyPayloadNames()
    {
        Assert.Equal("Registration hello", DadAutoPartyProgressProjection.FriendlyPayloadName(
            ProtocolContractRegistry.GetTypeId<RegistrationHello>()));
        Assert.Equal("Listing update", DadAutoPartyProgressProjection.FriendlyPayloadName(
            ProtocolContractRegistry.GetTypeId<PrivateListingUpdate>()));
        Assert.Equal("Pairing notice", DadAutoPartyProgressProjection.FriendlyPayloadName(
            ProtocolContractRegistry.GetTypeId<PairingNotice>()));
        Assert.Equal("Pairing approval", DadAutoPartyProgressProjection.FriendlyPayloadName(
            ProtocolContractRegistry.GetTypeId<PairingApproval>()));
        Assert.Equal("Mailbox message", DadAutoPartyProgressProjection.FriendlyPayloadName("unknown-payload"));
    }

    [Fact]
    public async Task BootstrapImportRejectsMailboxThatDoesNotMatchPinnedEpochs()
    {
        using var crypto = new CryptoFixture();
        var registrationId = Guid.NewGuid();
        var configuration = new DadAutoPartyConfiguration
        {
            RegistrationId = registrationId.ToString("D"),
            RegisteredOwnerId = crypto.OwnerId.Value,
            RegisteredIslandId = crypto.IslandId.Value,
            RegistrationFingerprint = DadAutoPartyIdentityPackageService.BuildFingerprint(
                crypto.OwnerId.Value,
                crypto.IslandId.Value,
                crypto.EndpointKeyVersion,
                crypto.EndpointSigningPublicKey,
                crypto.EndpointAgreementPublicKey),
        };
        var store = new MemoryWebhookStore();
        await using var connector = new DadDiscordCourierConnector(configuration, static () => true);
        using var endpoint = new DadAutoPartyEndpointService(
            configuration,
            store,
            new MemoryLegacyTokenStore(),
            connector,
            static () => { });
        var credential = crypto.Credential() with
        {
            UplinkEpoch = crypto.UplinkEpoch with { EpochId = Guid.NewGuid() },
        };

        var result = await endpoint.ImportBootstrapAsync(new(
            registrationId,
            crypto.OwnerId.Value,
            crypto.IslandId.Value,
            configuration.RegistrationFingerprint,
            "123456789012345678",
            "guild-home",
            "route-one",
            credential,
            crypto.UplinkEpoch,
            crypto.DownlinkEpoch,
            new EndpointPublicKeys(
                crypto.RelayKeyVersion,
                "relay-signing",
                ImmutableArray.CreateRange(crypto.RelaySigningPublicKey),
                "relay-agreement",
                ImmutableArray.CreateRange(crypto.RelayAgreementPublicKey)),
            DateTime.UtcNow.AddHours(1)));

        Assert.False(result.Allowed);
        Assert.Equal("dad-bootstrap-invalid", result.SafeCode);
        Assert.Null(store.StoredCredential);
    }

    [Fact]
    public async Task RelayWrappersFailClosedUntilPumpIsAttached()
    {
        var configuration = ActiveConfiguration();
        using var identityStore = new MemoryIdentityStore();
        await using var connector = new DadDiscordCourierConnector(configuration, static () => true);
        using var endpoint = new DadAutoPartyEndpointService(
            configuration,
            new MemoryWebhookStore(),
            new MemoryLegacyTokenStore(),
            connector,
            static () => { },
            identityStore: identityStore);

        Assert.False(endpoint.RelayStatus.Attached);
        Assert.Null(endpoint.LastPairingChallenge);
        Assert.False((await endpoint.RequestDirectoryAsync(string.Empty, true)).Allowed);
        Assert.False((await endpoint.InitiatePairingAsync("island-peer")).Allowed);
        Assert.False((await endpoint.DeauthenticateAsync("island-peer", "dad-owner-deauthenticated")).Allowed);
        Assert.False((await endpoint.DeregisterAsync()).Allowed);
        Assert.True(configuration.IsRegistrationActive);
    }

    [Fact]
    public void SharedCourierFragmentsRoundTripBoundedOpaquePayloadAndRejectTampering()
    {
        var envelope = Envelope("sender", "recipient", RandomNumberGenerator.GetBytes(4096));
        var fragments = CourierFragmentCodec.Fragment(
            envelope.EnvelopeId,
            envelope.PayloadType,
            envelope.Ciphertext.AsSpan(),
            envelope.ExpiresAt);
        var observed = CourierFragmentCodec.Reassemble(fragments);
        var tampered = fragments.ToArray();
        tampered[0] = tampered[0] with { Payload = ImmutableArray.Create<byte>(0xFF) };

        Assert.True(envelope.Ciphertext.AsSpan().SequenceEqual(observed.AsSpan()));
        Assert.Equal(
            (envelope.PayloadLength + AutoPartyProtocol.MaximumCourierFragmentBytes - 1) /
            AutoPartyProtocol.MaximumCourierFragmentBytes,
            fragments.Length);
        Assert.All(fragments, fragment => Assert.InRange(
            fragment.Payload.Length,
            1,
            AutoPartyProtocol.MaximumCourierFragmentBytes));
        Assert.Throws<ProtocolException>(() => CourierFragmentCodec.Reassemble(tampered));
    }

    [Fact]
    public async Task AdapterPollsDistinctSlotsBeforePublishingAndAcknowledgesInMatchingDirection()
    {
        using var crypto = new CryptoFixture();
        var outbound = Envelope(
            "island-local",
            "central-autoparty",
            Enumerable.Range(0, AutoPartyProtocol.MaximumCourierFragmentBytes * 3 + 32)
                .Select(static value => (byte)value)
                .ToArray());
        var (downlink, downlinkPage) = crypto.CreateDownlink([5, 6, 7, 8]);
        var handler = new ScriptedWebhookHandler(downlinkPage, failFirstPatch: true);
        using var client = new HttpClient(handler);
        await using var adapter = new DadAutoPartyWebhookTransportAdapter(
            crypto.Credential(),
            "route-one",
            1,
            crypto.EndpointSigningPrivateKey,
            client,
            ownsHttpClient: false,
            static (_, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(10));

        var callerThread = Environment.CurrentManagedThreadId;
        var sent = await adapter.SendAsync(outbound);
        Assert.True(sent.Accepted, sent.SafeCode);

        OpaqueEnvelope? received = null;
        for (var attempt = 0; attempt < 300 && received == null; attempt++)
        {
            await Task.Delay(10);
            await foreach (var candidate in adapter.ReceiveAsync())
                received = candidate;
        }

        Assert.NotNull(received);
        Assert.Equal(downlink.EnvelopeId, received!.EnvelopeId);
        Assert.Equal(downlink.PayloadType, received.PayloadType);
        Assert.True(downlink.Ciphertext.AsSpan().SequenceEqual(received.Ciphertext.AsSpan()));
        Assert.Equal(0, handler.PostAttempts);
        for (var attempt = 0; attempt < 100 && handler.UplinkPages.Count < 1; attempt++)
            await Task.Delay(10);
        Assert.True(handler.PatchAttempts >= 2);
        Assert.Contains("10001", handler.GetMessageReferences);
        Assert.Contains("10002", handler.GetMessageReferences);
        Assert.DoesNotContain(callerThread, handler.RequestThreadIds.Take(1));

        var firstPage = CourierTextCodec.DecodePage(handler.UplinkPages.First()).Contract;
        Assert.True(firstPage.Fragments.Length == 1);
        Assert.Equal(4, firstPage.Fragments[0].FragmentCount);
        for (var attempt = 0; attempt < 100 && !adapter.TransferSnapshot.AwaitingCentralAcknowledgement; attempt++)
            await Task.Delay(10);
        Assert.Equal(new DadAutoPartyAdapterTransferSnapshot(
            "test-envelope",
            0,
            1,
            4,
            true), adapter.TransferSnapshot);
        var getsBeforeWrongGeneration = handler.GetAttempts;
        handler.SetContent(
            "10001",
            crypto.CreateUplinkAcknowledgement(
                firstPage,
                acknowledgedPageGeneration: firstPage.PageGeneration + 1));
        for (var attempt = 0; attempt < 100 && handler.GetAttempts < getsBeforeWrongGeneration + 2; attempt++)
            await Task.Delay(10);
        Assert.All(handler.UplinkPages, content =>
            Assert.Equal(1, CourierTextCodec.DecodePage(content).Contract.Fragments[0].FragmentNumber));
        Assert.Equal(0, adapter.TransferSnapshot.AcceptedFragmentCount);
        Assert.Equal(1, adapter.TransferSnapshot.CurrentFragmentNumber);

        for (var fragmentNumber = 1; fragmentNumber <= 4; fragmentNumber++)
        {
            CourierPage? publishedPage = null;
            for (var attempt = 0; attempt < 100 && publishedPage == null; attempt++)
            {
                publishedPage = handler.UplinkPages
                    .Select(content => CourierTextCodec.DecodePage(content).Contract)
                    .LastOrDefault(page => page.Fragments[0].FragmentNumber == fragmentNumber);
                if (publishedPage == null)
                    await Task.Delay(10);
            }
            Assert.NotNull(publishedPage);
            Assert.Equal(fragmentNumber - 1, adapter.TransferSnapshot.AcceptedFragmentCount);
            Assert.Equal(fragmentNumber, adapter.TransferSnapshot.CurrentFragmentNumber);
            Assert.Equal(4, adapter.TransferSnapshot.TotalFragmentCount);
            Assert.True(adapter.TransferSnapshot.AwaitingCentralAcknowledgement);

            handler.SetContent("10001", crypto.CreateUplinkAcknowledgement(publishedPage!));
            if (fragmentNumber < 4)
            {
                for (var attempt = 0; attempt < 100 &&
                     (adapter.TransferSnapshot.AcceptedFragmentCount != fragmentNumber ||
                      adapter.TransferSnapshot.CurrentFragmentNumber != fragmentNumber + 1 ||
                      !adapter.TransferSnapshot.AwaitingCentralAcknowledgement); attempt++)
                    await Task.Delay(10);
                Assert.Equal(fragmentNumber, adapter.TransferSnapshot.AcceptedFragmentCount);
                Assert.Equal(fragmentNumber + 1, adapter.TransferSnapshot.CurrentFragmentNumber);
            }
            else
            {
                for (var attempt = 0; attempt < 100 && !adapter.TransferSnapshot.IsIdle; attempt++)
                    await Task.Delay(10);
                Assert.Same(DadAutoPartyAdapterTransferSnapshot.Idle, adapter.TransferSnapshot);
            }
        }

        await adapter.AcknowledgeAsync(new AutoPartyTransportAcknowledgement(
            downlink.EnvelopeId,
            "dad-downlink-consumed"));
        for (var attempt = 0; attempt < 100 && !handler.DownlinkAcknowledgements.Any(content =>
                 CourierTextCodec.DecodeAcknowledgement(content).Contract.AcceptedMessageIds.Contains(downlink.EnvelopeId)); attempt++)
            await Task.Delay(10);
        var downlinkAcknowledgement = handler.DownlinkAcknowledgements
            .Select(content => CourierTextCodec.DecodeAcknowledgement(content).Contract)
            .Single(item => item.AcceptedMessageIds.Contains(downlink.EnvelopeId));
        Assert.Equal(CourierDirection.Downlink, downlinkAcknowledgement.Direction);
        Assert.Contains(downlink.EnvelopeId, downlinkAcknowledgement.AcceptedMessageIds);
        Assert.DoesNotContain(handler.PatchedMessages, item =>
            item.MessageReference == "10001" &&
            CourierTextCodec.GetKind(item.Content) == CourierTextKind.Acknowledgement &&
            CourierTextCodec.DecodeAcknowledgement(item.Content).Contract.Direction == CourierDirection.Downlink);
    }

    [Fact]
    public async Task EndpointLogsAdapterStartFailureOncePerUnchangedFailure()
    {
        var configuration = ActiveConfiguration();
        var now = DateTimeOffset.UtcNow;
        var relayKeys = new EndpointPublicKeys(
            configuration.RelayKeyGeneration,
            "relay-signing",
            ImmutableArray.CreateRange(Convert.FromBase64String(configuration.RelaySigningPublicKey)),
            "relay-agreement",
            ImmutableArray.CreateRange(Convert.FromBase64String(configuration.RelayAgreementPublicKey)));
        var credential = new DadAutoPartyWebhookCredential(
            "123456789012345678",
            new string('a', 64),
            "223456789012345678")
        {
            UplinkEpoch = new(
                Guid.Parse(configuration.UplinkEpochId),
                new IslandId(configuration.RegisteredIslandId),
                CourierDirection.Uplink,
                now.AddMinutes(-1),
                now.AddMinutes(30),
                now.AddMinutes(35),
                1,
                [new CourierPageReference(1, "10001")],
                configuration.MailboxEpochGeneration),
            DownlinkEpoch = new(
                Guid.Parse(configuration.DownlinkEpochId),
                new IslandId(configuration.RegisteredIslandId),
                CourierDirection.Downlink,
                now.AddMinutes(-1),
                now.AddMinutes(30),
                now.AddMinutes(35),
                1,
                [new CourierPageReference(1, "10002")],
                configuration.MailboxEpochGeneration),
            RelayPublicKeys = relayKeys,
        };
        var store = new MemoryWebhookStore();
        configuration.WebhookCredentialReference = await store.StoreAsync(credential);
        var diagnostics = new List<string>();
        var diagnosticGate = new object();
        void RecordDiagnostic(string line)
        {
            lock (diagnosticGate)
                diagnostics.Add(line);
        }

        string[] ReadDiagnostics()
        {
            lock (diagnosticGate)
                return diagnostics.ToArray();
        }

        await using var connector = new DadDiscordCourierConnector(configuration, static () => true);
        using var endpoint = new DadAutoPartyEndpointService(
            configuration,
            store,
            new MemoryLegacyTokenStore(),
            connector,
            static () => { },
            RecordDiagnostic);

        for (var attempt = 0; attempt < 100 && !ReadDiagnostics().Any(line => line.Contains(
                     "stage=adapter-start-failed safeCode=dad-webhook-identity-store-unavailable",
                     StringComparison.Ordinal)); attempt++)
        {
            endpoint.Update(dadEnabled: true);
            await Task.Delay(5);
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            endpoint.Update(dadEnabled: true);
            await Task.Delay(5);
        }

        var observed = ReadDiagnostics();
        Assert.Equal(1, observed.Count(line => line.Contains(
            "stage=adapter-start-requested safeCode=dad-webhook-adapter-start-requested",
            StringComparison.Ordinal)));
        Assert.Equal(1, observed.Count(line => line.Contains(
            "stage=adapter-start-failed safeCode=dad-webhook-identity-store-unavailable",
            StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AdapterLogsPairingTransferProgressOnlyWhenTheStageChanges()
    {
        using var crypto = new CryptoFixture();
        var credential = crypto.Credential();
        var outbound = Envelope(
            "island-local",
            "central-autoparty",
            Enumerable.Range(0, AutoPartyProtocol.MaximumCourierFragmentBytes * 3 + 32)
                .Select(static value => (byte)value)
                .ToArray(),
            ProtocolContractRegistry.GetTypeId<PairingNotice>());
        var handler = new ScriptedWebhookHandler(CourierTextCodec.EmptySlotContent);
        using var client = new HttpClient(handler);
        await using var adapter = new DadAutoPartyWebhookTransportAdapter(
            credential,
            "route-pairing-diagnostics",
            1,
            crypto.EndpointSigningPrivateKey,
            client,
            ownsHttpClient: false,
            static (_, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(10));
        var diagnostics = new List<string>();
        void RecordDiagnostic(string line)
        {
            lock (diagnostics)
                diagnostics.Add(line);
        }

        string[] ReadDiagnostics()
        {
            lock (diagnostics)
                return diagnostics.ToArray();
        }

        adapter.ConfigureDiagnostic(RecordDiagnostic);
        var sent = await adapter.SendAsync(outbound);
        Assert.True(sent.Accepted, sent.SafeCode);

        for (var fragmentNumber = 1; fragmentNumber <= 4; fragmentNumber++)
        {
            CourierPage? publishedPage = null;
            for (var attempt = 0; attempt < 200 && publishedPage == null; attempt++)
            {
                publishedPage = handler.UplinkPages
                    .Select(content => CourierTextCodec.DecodePage(content).Contract)
                    .LastOrDefault(page => page.Fragments[0].FragmentNumber == fragmentNumber);
                if (publishedPage == null)
                    await Task.Delay(10);
            }

            Assert.NotNull(publishedPage);
            handler.SetContent("10001", crypto.CreateUplinkAcknowledgement(publishedPage!));
            for (var attempt = 0; attempt < 200 && !ReadDiagnostics().Any(line =>
                         line.Contains(
                             $"stage=fragment-acknowledged:{fragmentNumber}/4",
                             StringComparison.Ordinal)); attempt++)
                await Task.Delay(10);
        }

        for (var attempt = 0; attempt < 200 && !adapter.TransferSnapshot.IsIdle; attempt++)
            await Task.Delay(10);

        var observed = ReadDiagnostics();
        Assert.Equal(1, observed.Count(line => line.Contains(
            "stage=transfer-started", StringComparison.Ordinal)));
        for (var fragmentNumber = 1; fragmentNumber <= 4; fragmentNumber++)
        {
            Assert.Equal(1, observed.Count(line => line.Contains(
                $"stage=fragment-published:{fragmentNumber}/4",
                StringComparison.Ordinal)));
            Assert.Equal(1, observed.Count(line => line.Contains(
                $"stage=fragment-acknowledged:{fragmentNumber}/4",
                StringComparison.Ordinal)));
        }
        Assert.Equal(1, observed.Count(line => line.Contains(
            "stage=courier-accepted safeCode=relay-uplink-fragment-accepted",
            StringComparison.Ordinal)));
        var joined = string.Join('\n', observed);
        Assert.DoesNotContain(outbound.EnvelopeId.ToString("D"), joined, StringComparison.Ordinal);
        Assert.DoesNotContain(credential.WebhookToken, joined, StringComparison.Ordinal);
        Assert.DoesNotContain(outbound.SenderIslandId.Value, joined, StringComparison.Ordinal);
        Assert.DoesNotContain(outbound.RecipientIslandId.Value, joined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandledMailboxFailuresPreserveReadyPairingAndExactRawActivityCodes()
    {
        using var crypto = new CryptoFixture();
        var handler = new ScriptedWebhookHandler(
            CourierTextCodec.EmptySlotContent,
            failAllPatches: true);
        using var client = new HttpClient(handler);
        await using var adapter = new DadAutoPartyWebhookTransportAdapter(
            crypto.Credential(),
            "route-transient-failures",
            1,
            crypto.EndpointSigningPrivateKey,
            client,
            ownsHttpClient: false,
            static (_, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(10));

        for (var attempt = 0; attempt < 200 &&
             !string.Equals(adapter.Snapshot.SafeCode, "dad-webhook-presence-failed", StringComparison.Ordinal); attempt++)
            await Task.Delay(5);

        Assert.Equal(DadAutoPartyEndpointConnectionState.Ready, adapter.Snapshot.State);
        Assert.Equal("dad-webhook-presence-failed", adapter.Snapshot.SafeCode);

        var configuration = ActiveConfiguration();
        var pairing = DadAutoPartyProgressProjection.Pairing(
            configuration,
            adapter.Snapshot,
            null,
            null,
            "island-22222222222222222222222222222222",
            null,
            DateTime.UtcNow);
        Assert.True(pairing.MailboxReady);
        Assert.NotEqual("dad-pairing-mailbox-not-ready", pairing.SafeCode);

        var outbound = Envelope("island-local", "central-autoparty", [1, 2, 3, 4]);
        Assert.True((await adapter.SendAsync(outbound)).Accepted);
        for (var attempt = 0; attempt < 200 &&
             !string.Equals(adapter.Snapshot.SafeCode, "dad-webhook-publish-failed", StringComparison.Ordinal); attempt++)
            await Task.Delay(5);

        Assert.Equal(DadAutoPartyEndpointConnectionState.Ready, adapter.Snapshot.State);
        var activity = DadAutoPartyProgressProjection.MailboxActivity(
            adapter.Snapshot,
            new DadAutoPartyRelayStatus(true, true, "dad-relay-pump-running", DateTimeOffset.UtcNow, null, 0, 0, 0),
            adapter.TransferSnapshot);
        Assert.Equal("dad-webhook-publish-failed", activity.RawSafeCode);
    }

    [Fact]
    public async Task FetchAcknowledgementQueueAndFatalFailuresRespectMailboxLifecycleState()
    {
        using var crypto = new CryptoFixture();

        var fetchHandler = new BlockingPatchWebhookHandler(failFetches: true);
        using (var fetchClient = new HttpClient(fetchHandler))
        await using (var fetchAdapter = new DadAutoPartyWebhookTransportAdapter(
                         crypto.Credential(),
                         "route-fetch-failure",
                         1,
                         crypto.EndpointSigningPrivateKey,
                         fetchClient,
                         ownsHttpClient: false,
                         static (_, _) => Task.CompletedTask,
                         TimeSpan.FromMilliseconds(10)))
        {
            await fetchHandler.PatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(DadAutoPartyEndpointConnectionState.Ready, fetchAdapter.Snapshot.State);
            Assert.Equal("dad-webhook-fetch-failed", fetchAdapter.Snapshot.SafeCode);
        }

        var (_, downlinkPage) = crypto.CreateDownlink([5, 6, 7, 8]);
        var acknowledgementHandler = new ScriptedWebhookHandler(
            downlinkPage,
            failAllPatches: true);
        using (var acknowledgementClient = new HttpClient(acknowledgementHandler))
        await using (var acknowledgementAdapter = new DadAutoPartyWebhookTransportAdapter(
                         crypto.Credential(),
                         "route-ack-failure",
                         1,
                         crypto.EndpointSigningPrivateKey,
                         acknowledgementClient,
                         ownsHttpClient: false,
                         static (_, _) => Task.CompletedTask,
                         TimeSpan.FromMilliseconds(10)))
        {
            for (var attempt = 0; attempt < 200 &&
                 !string.Equals(acknowledgementAdapter.Snapshot.SafeCode, "dad-webhook-ack-failed", StringComparison.Ordinal); attempt++)
                await Task.Delay(5);
            Assert.Equal(DadAutoPartyEndpointConnectionState.Ready, acknowledgementAdapter.Snapshot.State);
            Assert.Equal("dad-webhook-ack-failed", acknowledgementAdapter.Snapshot.SafeCode);
        }

        var queueHandler = new BlockingPatchWebhookHandler(failFetches: false);
        using (var queueClient = new HttpClient(queueHandler))
        await using (var queueAdapter = new DadAutoPartyWebhookTransportAdapter(
                         crypto.Credential(),
                         "route-queue-failure",
                         1,
                         crypto.EndpointSigningPrivateKey,
                         queueClient,
                         ownsHttpClient: false,
                         static (_, _) => Task.CompletedTask,
                         TimeSpan.FromMilliseconds(10)))
        {
            await queueHandler.PatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            string? rejectedSafeCode = null;
            for (var attempt = 0; attempt < 128 && rejectedSafeCode == null; attempt++)
            {
                var result = await queueAdapter.SendAsync(
                    Envelope("island-local", "central-autoparty", [(byte)attempt]));
                if (!result.Accepted)
                    rejectedSafeCode = result.SafeCode;
            }
            Assert.Equal("dad-webhook-outbound-full", rejectedSafeCode);
            Assert.Equal(DadAutoPartyEndpointConnectionState.Ready, queueAdapter.Snapshot.State);
            Assert.Equal("dad-webhook-outbound-full", queueAdapter.Snapshot.SafeCode);
        }

        using var fatalClient = new HttpClient(new ThrowingWebhookHandler());
        await using var fatalAdapter = new DadAutoPartyWebhookTransportAdapter(
            crypto.Credential(),
            "route-fatal-failure",
            1,
            crypto.EndpointSigningPrivateKey,
            fatalClient,
            ownsHttpClient: false,
            static (_, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(10));
        for (var attempt = 0; attempt < 200 &&
             fatalAdapter.Snapshot.State != DadAutoPartyEndpointConnectionState.Degraded; attempt++)
            await Task.Delay(5);

        Assert.Equal(DadAutoPartyEndpointConnectionState.Degraded, fatalAdapter.Snapshot.State);
        Assert.Equal("dad-webhook-pump-failed", fatalAdapter.Snapshot.SafeCode);
        var fatalPairing = DadAutoPartyProgressProjection.Pairing(
            ActiveConfiguration(),
            fatalAdapter.Snapshot,
            null,
            null,
            "island-22222222222222222222222222222222",
            null,
            DateTime.UtcNow);
        Assert.False(fatalPairing.MailboxReady);
        Assert.Equal("dad-pairing-mailbox-not-ready", fatalPairing.SafeCode);
    }

    [Fact]
    public async Task AdapterPublishesImmediateAndIdlePresenceOnTenSecondCadence()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), DadAutoPartyWebhookTransportAdapter.DefaultPollInterval);
        using var crypto = new CryptoFixture();
        var handler = new ScriptedWebhookHandler(CourierTextCodec.EmptySlotContent);
        using var client = new HttpClient(handler);
        await using var adapter = new DadAutoPartyWebhookTransportAdapter(
            crypto.Credential(),
            "route-one",
            1,
            crypto.EndpointSigningPrivateKey,
            client,
            ownsHttpClient: false,
            static (_, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(10));
        var diagnostics = new List<string>();
        var diagnosticGate = new object();
        void RecordDiagnostic(string line)
        {
            lock (diagnosticGate)
                diagnostics.Add(line);
        }

        string[] ReadDiagnostics()
        {
            lock (diagnosticGate)
                return diagnostics.ToArray();
        }

        adapter.ConfigureDiagnostic(RecordDiagnostic);

        for (var attempt = 0; attempt < 100 && handler.Presences.Count < 2; attempt++)
            await Task.Delay(10);

        var presences = handler.Presences
            .Take(2)
            .Select(crypto.OpenPresence)
            .ToList();
        Assert.Equal(2, presences.Count);
        Assert.All(presences, presence =>
        {
            Assert.Equal(crypto.UplinkEpoch.EpochId, presence.EpochId);
            Assert.Equal(CourierDirection.Uplink, presence.Direction);
            Assert.Equal(crypto.UplinkEpoch.EpochGeneration, presence.EpochGeneration);
        });
        Assert.True(presences[1].Header.Generation > presences[0].Header.Generation);

        handler.FailNextPatches(3);
        for (var attempt = 0; attempt < 200 && !ReadDiagnostics().Any(line => line.Contains(
                     "stage=presence-recovered safeCode=dad-webhook-presence-published",
                     StringComparison.Ordinal)); attempt++)
            await Task.Delay(10);

        var observed = ReadDiagnostics();
        Assert.Equal(1, observed.Count(line => line.Contains(
            "stage=presence-initial-published safeCode=dad-webhook-presence-published",
            StringComparison.Ordinal)));
        Assert.Equal(1, observed.Count(line => line.Contains(
            "stage=presence-publish-failed safeCode=dad-webhook-presence-failed",
            StringComparison.Ordinal)));
        Assert.Equal(1, observed.Count(line => line.Contains(
            "stage=presence-recovered safeCode=dad-webhook-presence-published",
            StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AdapterAcknowledgesCurrentEpochAnnouncementAfterRestart()
    {
        using var crypto = new CryptoFixture();
        var handler = new ScriptedWebhookHandler(
            CourierTextCodec.EmptySlotContent,
            uplinkContent: crypto.CreateEpochAnnouncement(crypto.UplinkEpoch));
        using var client = new HttpClient(handler);
        await using var adapter = new DadAutoPartyWebhookTransportAdapter(
            crypto.Credential(),
            "route-one",
            1,
            crypto.EndpointSigningPrivateKey,
            client,
            ownsHttpClient: false,
            static (_, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(10));

        for (var attempt = 0; attempt < 100 && handler.UplinkAcknowledgements.Count < 1; attempt++)
            await Task.Delay(10);

        var acknowledgement = CourierTextCodec.DecodeAcknowledgement(
            Assert.Single(handler.UplinkAcknowledgements)).Contract;
        Assert.Equal(crypto.UplinkEpoch.EpochId, acknowledgement.EpochId);
        Assert.Equal(CourierDirection.Uplink, acknowledgement.Direction);
        Assert.Equal(crypto.UplinkEpoch.EpochGeneration, acknowledgement.PageGeneration);
    }

    [Theory]
    [InlineData(DadAutoPartyCharacterShareMode.SpecificCharacter, true, false, true)]
    [InlineData(DadAutoPartyCharacterShareMode.SpecificCharacter, false, true, false)]
    [InlineData(DadAutoPartyCharacterShareMode.CharacterList, true, false, true)]
    [InlineData(DadAutoPartyCharacterShareMode.CharacterList, false, true, true)]
    [InlineData(DadAutoPartyCharacterShareMode.CharacterList, false, false, false)]
    [InlineData(DadAutoPartyCharacterShareMode.AllCharactersForPeer, true, false, true)]
    [InlineData(DadAutoPartyCharacterShareMode.AllCharactersForPeer, false, true, false)]
    [InlineData(DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild, true, true, true)]
    [InlineData(DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild, false, true, false)]
    [InlineData(DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild, false, false, false)]
    public void EveryShareModeHonorsPairingOrSameGuildBoundary(
        DadAutoPartyCharacterShareMode mode,
        bool paired,
        bool sameGuild,
        bool expected)
    {
        var policy = new DadAutoPartySharePolicy
        {
            Mode = mode,
            Enabled = true,
            CharacterHandles = ["opaque-one", "opaque-two"],
        }.Normalize();
        if (mode == DadAutoPartyCharacterShareMode.SpecificCharacter)
            policy.CharacterHandles = ["opaque-one"];

        Assert.Equal(expected, DadAutoPartyShareRules.Allows(
            policy,
            "opaque-one",
            paired,
            sameGuild));
    }

    [Theory]
    [InlineData(DadAutoPartyCharacterShareMode.AllCharactersForPeer)]
    [InlineData(DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild)]
    public void BroadShareModesNormalizeWithoutCharacterHandleResidue(
        DadAutoPartyCharacterShareMode mode)
    {
        var policy = new DadAutoPartySharePolicy
        {
            Enabled = true,
            Mode = mode,
            CharacterHandles = ["opaque-one", "opaque-two"],
        };

        policy.Normalize();

        Assert.Empty(policy.CharacterHandles);
        Assert.True(policy.IsValid);
    }

    [Fact]
    public void CrossGuildPairingActivatesOnlyAfterBothExactApprovalsAndDeauthenticationCannotReplay()
    {
        var configuration = ActiveConfiguration();
        using var identityStore = new MemoryIdentityStore();
        using var service = new DadAutoPartyService(
            configuration,
            identityStore,
            static () => true,
            static () => { });
        const string code = "193847";
        var codeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        var pairingId = Guid.NewGuid();
        var peerPublicKeys = PairingPublicKeys();
        var peerFingerprint = DadAutoPartyIdentityPackageService.BuildFingerprint(
            "owner-peer",
            "island-peer",
            peerPublicKeys.KeyVersion,
            peerPublicKeys.Ed25519PublicKey.ToArray(),
            peerPublicKeys.X25519PublicKey.ToArray());
        var transcript = new string('C', 64);

        var notice = service.ReceivePairingNotice(
            pairingId,
            "owner-peer",
            "island-peer",
            "different-guild",
            peerPublicKeys,
            peerFingerprint,
            transcript,
            codeHash,
            DateTime.UtcNow.AddMinutes(5));
        Assert.True(notice.Allowed, notice.SafeCode);
        var remote = service.ConfirmPeerApproval(
            pairingId,
            transcript,
            codeHash,
            peerFingerprint,
            configuration.RegistrationFingerprint,
            Policy(DadAutoPartyCharacterShareMode.AllCharactersForPeer));
        var remoteRetry = service.ConfirmPeerApproval(
            pairingId,
            transcript,
            codeHash,
            peerFingerprint,
            configuration.RegistrationFingerprint,
            Policy(DadAutoPartyCharacterShareMode.AllCharactersForPeer));
        var changedTranscriptRetry = service.ConfirmPeerApproval(
            pairingId,
            new string('D', 64),
            codeHash,
            peerFingerprint,
            configuration.RegistrationFingerprint,
            Policy(DadAutoPartyCharacterShareMode.AllCharactersForPeer));

        Assert.True(remote.Allowed, remote.SafeCode);
        Assert.True(remoteRetry.Allowed, remoteRetry.SafeCode);
        Assert.False(changedTranscriptRetry.Allowed);
        Assert.Empty(configuration.Pairings);

        var local = service.ApprovePairing(
            pairingId,
            peerFingerprint,
            code,
            Policy(DadAutoPartyCharacterShareMode.CharacterList));
        var localRetry = service.ApprovePairing(
            pairingId,
            peerFingerprint,
            code,
            Policy(DadAutoPartyCharacterShareMode.CharacterList));
        var changedPolicyRetry = service.ApprovePairing(
            pairingId,
            peerFingerprint,
            code,
            Policy(DadAutoPartyCharacterShareMode.AllCharactersForPeer));
        var changedFingerprintRetry = service.ApprovePairing(
            pairingId,
            new string('F', 64),
            code,
            Policy(DadAutoPartyCharacterShareMode.CharacterList));
        var changedCodeRetry = service.ApprovePairing(
            pairingId,
            peerFingerprint,
            "000000",
            Policy(DadAutoPartyCharacterShareMode.CharacterList));

        Assert.True(local.Allowed, local.SafeCode);
        Assert.True(localRetry.Allowed, localRetry.SafeCode);
        Assert.False(changedPolicyRetry.Allowed);
        Assert.False(changedFingerprintRetry.Allowed);
        Assert.False(changedCodeRetry.Allowed);
        Assert.Empty(configuration.Pairings);
        var awaitingRelay = Assert.Single(configuration.PendingPairings);
        Assert.False(awaitingRelay.IsActive);
        Assert.True(Guid.TryParse(awaitingRelay.LocalApprovalRelayMessageId, out var approvalMessageId));
        Assert.True(service.TryApplyPairingApprovalRelayReceipt(
            approvalMessageId,
            accepted: true,
            out var relayed));
        Assert.True(relayed.Allowed, relayed.SafeCode);
        var active = Assert.Single(configuration.Pairings);
        Assert.True(active.IsActive);
        Assert.Equal("different-guild", active.HomeGuildScope);
        Assert.Equal(peerPublicKeys.KeyVersion, active.KeyGeneration);
        Assert.Equal(
            Convert.ToBase64String(peerPublicKeys.Ed25519PublicKey.AsSpan()),
            active.SigningPublicKey);
        Assert.Equal(
            Convert.ToBase64String(peerPublicKeys.X25519PublicKey.AsSpan()),
            active.AgreementPublicKey);

        var normalizedClone = active.Clone();
        normalizedClone.SigningPublicKey = $" {normalizedClone.SigningPublicKey} ";
        normalizedClone.AgreementPublicKey = $"\t{normalizedClone.AgreementPublicKey}\r\n";
        normalizedClone.Normalize();
        Assert.True(normalizedClone.IsActive);
        Assert.Equal(active.SigningPublicKey, normalizedClone.SigningPublicKey);
        Assert.Equal(active.AgreementPublicKey, normalizedClone.AgreementPublicKey);

        var persisted = JsonSerializer.Deserialize<DadAutoPartyConfiguration>(
            JsonSerializer.Serialize(configuration))!.Normalize();
        var restored = Assert.Single(persisted.Pairings);
        Assert.Equal(active.SigningPublicKey, restored.SigningPublicKey);
        Assert.Equal(active.AgreementPublicKey, restored.AgreementPublicKey);
        Assert.True(restored.IsActive);

        var withoutSigningKey = active.Clone();
        withoutSigningKey.SigningPublicKey = string.Empty;
        Assert.False(withoutSigningKey.IsActive);
        var withoutAgreementKey = active.Clone();
        withoutAgreementKey.AgreementPublicKey = string.Empty;
        Assert.False(withoutAgreementKey.IsActive);

        configuration.Listings.Add(Listing("island-peer"));
        configuration.RemoteBindings.Add(new DadAutoPartyRemoteBinding
        {
            FleetRowId = "row-one",
            OpaqueCharacterId = "opaque-one",
            OwnerId = "owner-peer",
            IslandId = "island-peer",
            RequestedJobId = "19",
            OwnerConsentConfirmed = true,
        });
        var deauthenticated = service.Deauthenticate("island-peer", "dad-owner-deauthenticated");
        var replay = service.ReceivePairingNotice(
            Guid.NewGuid(),
            "owner-peer",
            "island-peer",
            "different-guild",
            peerPublicKeys,
            peerFingerprint,
            transcript,
            codeHash,
            DateTime.UtcNow.AddMinutes(5));

        Assert.True(deauthenticated.Allowed);
        Assert.NotNull(active.RevokedAtUtc);
        Assert.Empty(configuration.Listings);
        Assert.Empty(configuration.RemoteBindings);
        Assert.False(replay.Allowed);
    }

    [Fact]
    public void ListingModelCannotPersistInviteOrCharacterIdentityLocators()
    {
        var names = typeof(DadAutoPartyListing).GetProperties().Select(static property => property.Name).ToList();

        Assert.DoesNotContain(names, name => name.Contains("ContentId", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("InviteLocator", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("CharacterName", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(nameof(DadAutoPartyListing.OpaqueCharacterId), names);
        Assert.Contains(nameof(DadAutoPartyListing.DisplayLabel), names);
    }

    [Fact]
    public async Task StandingPolicyEditPreservesPairPolicyAndPromptsListingRefresh()
    {
        var pairPolicy = Policy(DadAutoPartyCharacterShareMode.SpecificCharacter);
        pairPolicy.Revision = 12;
        var configuration = new DadAutoPartyConfiguration
        {
            Pairings = [new DadAutoPartyPairing { LocalSharePolicy = pairPolicy }],
        };
        var saves = 0;
        await using var connector = new DadDiscordCourierConnector(configuration, static () => true);
        using var endpoint = new DadAutoPartyEndpointService(
            configuration,
            new MemoryWebhookStore(),
            new MemoryLegacyTokenStore(),
            connector,
            () => saves++);
        var nextPublish = typeof(DadAutoPartyEndpointService).GetField(
            "nextListingPublishUtc",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        nextPublish.SetValue(endpoint, DateTime.UtcNow.AddMinutes(5));

        var decision = endpoint.SetStandingSharePolicy(new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.CharacterList,
            CharacterHandles = ["opaque-community"],
            Enabled = true,
            Revision = 2,
        });

        Assert.True(decision.Allowed);
        Assert.True(configuration.StandingSharePolicy.Enabled);
        Assert.Same(pairPolicy, configuration.Pairings[0].LocalSharePolicy);
        Assert.Equal(DadAutoPartyCharacterShareMode.SpecificCharacter, pairPolicy.Mode);
        Assert.Equal(12, pairPolicy.Revision);
        Assert.Equal(DateTime.MinValue, nextPublish.GetValue(endpoint));
        Assert.Equal(1, saves);
    }

    [Fact]
    public void PerDadDiscordPairingModelsAreAbsentButAllianceAndLanModelsRemain()
    {
        var pairingProperties = typeof(DadAutoPartyPairing).GetProperties()
            .Select(static property => property.Name)
            .ToList();
        Assert.DoesNotContain("ApplicationId", pairingProperties);
        Assert.DoesNotContain("BotUserId", pairingProperties);

        var assembly = typeof(DadAutoPartyPairing).Assembly;
        foreach (var legacyType in new[]
                 {
                     "DadAutoPartyOutboundPairingChallenge",
                     "DadAutoPartyDiscordBinding",
                     "DadAutoPartyDiscordHealth",
                     "DadAutoPartyDiscoveredClient",
                     "DadAutoPartyPairingMessageKind",
                     "DadAutoPartyPairingEnvelope",
                     "DadAutoPartyDiscordConnectionState",
                 })
        {
            Assert.Null(assembly.GetType($"dad.Models.{legacyType}", throwOnError: false));
        }

        Assert.NotNull(assembly.GetType("dad.Models.DadAutoPartyRole", throwOnError: false));
        Assert.NotNull(assembly.GetType("dad.Models.DadAutoPartyPairingHealth", throwOnError: false));
        Assert.NotNull(assembly.GetType("dad.Models.DadAutoPartyLanPresence", throwOnError: false));
    }

    private static DadAutoPartyConfiguration ActiveConfiguration() => new DadAutoPartyConfiguration
    {
        Enabled = true,
        RegistrationState = DadAutoPartyRegistrationState.Active,
        RegistrationId = Guid.NewGuid().ToString("D"),
        RouteId = "route-one",
        CentralBotApplicationId = "123456789",
        HomeGuildScope = "home-guild",
        WebhookCredentialReference = "webhook-mailbox-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        UplinkEpochId = "11111111-1111-4111-8111-111111111111",
        DownlinkEpochId = "22222222-2222-4222-8222-222222222222",
        MailboxEpochGeneration = 1,
        RelayKeyGeneration = 1,
        RelaySigningPublicKey = Convert.ToBase64String(new byte[32]),
        RelayAgreementPublicKey = Convert.ToBase64String(new byte[32]),
        EndpointIdentityReference = "identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        RegisteredOwnerId = "owner-local",
        RegisteredIslandId = "island-local",
        RegistrationFingerprint = new string('A', 64),
        EndpointAlias = "local",
        SigningPublicKey = Convert.ToBase64String(new byte[32]),
        EncryptionPublicKey = Convert.ToBase64String(new byte[32]),
    }.Normalize();

    private static DadAutoPartyEndpointSnapshot EndpointSnapshot(
        DadAutoPartyEndpointConnectionState state,
        string safeCode) =>
        new(state, safeCode, DateTime.UtcNow, null, 0, 0, 0, 1);

    private static DadAutoPartyPairing ProgressPairing(
        Guid pairingId,
        string peerIslandId,
        DateTime expiresAtUtc) => new()
    {
        PairingId = pairingId.ToString("D"),
        OwnerId = "owner-peer",
        IslandId = peerIslandId,
        HomeGuildScope = "home-guild",
        PublicKeyFingerprint = new string('1', 64),
        LocalFingerprint = new string('2', 64),
        TranscriptHash = new string('3', 64),
        ConfirmationCodeHash = new string('4', 64),
        ExpiresAtUtc = expiresAtUtc,
        KeyGeneration = 1,
        SigningPublicKey = Convert.ToBase64String(Enumerable.Repeat((byte)0x51, 32).ToArray()),
        AgreementPublicKey = Convert.ToBase64String(Enumerable.Repeat((byte)0x61, 32).ToArray()),
    };

    private static DadAutoPartySharePolicy Policy(DadAutoPartyCharacterShareMode mode) => new()
    {
        Mode = mode,
        Enabled = true,
        CharacterHandles = ["opaque-one", "opaque-two"],
        Revision = 1,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static EndpointPublicKeys PairingPublicKeys() => new(
        3,
        "ed25519:peer-pairing",
        ImmutableArray.CreateRange(Enumerable.Repeat(
            (byte)0x31,
            AutoPartyProtocol.Ed25519PublicKeyBytes)),
        "x25519:peer-pairing",
        ImmutableArray.CreateRange(Enumerable.Repeat(
            (byte)0x42,
            AutoPartyProtocol.X25519KeyBytes)));

    private static DadAutoPartyListing Listing(string islandId) => new()
    {
        ListingId = Guid.NewGuid().ToString("D"),
        OwnerId = "owner-peer",
        SharingIslandId = islandId,
        EffectiveShareMode = DadAutoPartyCharacterShareMode.AllCharactersForPeer,
        EffectivePolicyHash = "paired-policy-hash",
        OpaqueCharacterId = "opaque-one",
        DisplayLabel = "Shared character",
        AllowedJobIds = ["19"],
        AllowedActivityIds = ["duty-one"],
        Available = true,
        Revision = 1,
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
    };

    private static OpaqueEnvelope Envelope(
        string sender,
        string recipient,
        byte[] payload,
        string payloadType = "test-envelope") =>
        new(
            AutoPartyProtocol.CurrentVersion,
            Guid.NewGuid(),
            new IslandId(sender),
            new IslandId(recipient),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            1,
            payloadType,
            ImmutableArray.CreateRange(payload));

    private static string ReadRepositorySource(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dad.csproj")))
            directory = directory.Parent;
        var repositoryRoot = directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate the DAD repository root from the test output directory.");
        return File.ReadAllText(Path.Combine([repositoryRoot, .. pathParts]));
    }

    private sealed record RecoveryWebhookPage(
        string WebhookId,
        IReadOnlyList<string> PayloadTypes);

    private sealed class RecoveryWebhookHandler(string blockedWebhookId) : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly Dictionary<string, string> contents = new(StringComparer.Ordinal);
        private readonly List<RecoveryWebhookPage> pages = [];
        private readonly TaskCompletionSource<bool> releaseNewMailbox =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int blockNextNewMailboxRead = 1;

        public TaskCompletionSource<bool> NewMailboxReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<RecoveryWebhookPage> Pages
        {
            get
            {
                lock (gate)
                    return pages.ToArray();
            }
        }

        public void ReleaseNewMailbox() => releaseNewMailbox.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            var webhookId = path[3];
            var messageReference = path[^1];
            var key = $"{webhookId}/{messageReference}";
            if (request.Method == HttpMethod.Get)
            {
                if (string.Equals(webhookId, blockedWebhookId, StringComparison.Ordinal) &&
                    Interlocked.Exchange(ref blockNextNewMailboxRead, 0) == 1)
                {
                    NewMailboxReadStarted.TrySetResult(true);
                    await releaseNewMailbox.Task.WaitAsync(cancellationToken);
                }
                string content;
                lock (gate)
                    content = contents.GetValueOrDefault(key, CourierTextCodec.EmptySlotContent);
                return Json(HttpStatusCode.OK, new { id = messageReference, content });
            }
            if (request.Method == HttpMethod.Patch)
            {
                var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(payload);
                var content = document.RootElement.GetProperty("content").GetString()!;
                lock (gate)
                {
                    contents[key] = content;
                    if (CourierTextCodec.GetKind(content) == CourierTextKind.Page)
                    {
                        var page = CourierTextCodec.DecodePage(content).Contract;
                        pages.Add(new RecoveryWebhookPage(
                            webhookId,
                            page.Fragments.Select(static fragment => fragment.PayloadType).ToArray()));
                    }
                }
                return Json(HttpStatusCode.OK, new { id = messageReference, content });
            }
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object payload) => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };
    }

    private sealed class RotatingMemoryWebhookStore : IDadAutoPartyWebhookCredentialStore
    {
        private readonly object gate = new();
        private readonly Dictionary<string, DadAutoPartyWebhookCredential> credentials = new(StringComparer.Ordinal);
        private readonly string importedReference;

        public RotatingMemoryWebhookStore(
            string existingReference,
            DadAutoPartyWebhookCredential existingCredential,
            string importedReference)
        {
            credentials[existingReference] = existingCredential;
            this.importedReference = importedReference;
        }

        public ValueTask<string> StoreAsync(
            DadAutoPartyWebhookCredential credential,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
                credentials[importedReference] = credential;
            return ValueTask.FromResult(importedReference);
        }

        public ValueTask<DadAutoPartyWebhookCredential> LoadAsync(
            string credentialReference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
                return ValueTask.FromResult(credentials.TryGetValue(credentialReference, out var credential)
                    ? credential
                    : throw new InvalidOperationException("missing-test-credential"));
        }

        public ValueTask<bool> DeleteAsync(
            string credentialReference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
                return ValueTask.FromResult(credentials.Remove(credentialReference));
        }
    }

    private sealed class ScriptedWebhookHandler(
        string downlinkContent,
        bool failFirstPatch = false,
        string? uplinkContent = null,
        bool failAllPatches = false) : HttpMessageHandler
    {
        private int postAttempts;
        private int getAttempts;
        private int patchAttempts;
        private int transientPatchFailures;
        private readonly object gate = new();
        private readonly Dictionary<string, string> messageContents = new(StringComparer.Ordinal)
        {
            ["10001"] = uplinkContent ?? CourierTextCodec.EmptySlotContent,
            ["10002"] = downlinkContent,
        };
        private readonly List<(string MessageReference, string Content)> patchedMessages = [];
        private readonly List<string> getMessageReferences = [];
        public int PostAttempts => Volatile.Read(ref postAttempts);
        public int GetAttempts => Volatile.Read(ref getAttempts);
        public int PatchAttempts => Volatile.Read(ref patchAttempts);
        public List<int> RequestThreadIds { get; } = [];
        public IReadOnlyList<(string MessageReference, string Content)> PatchedMessages
        {
            get
            {
                lock (gate)
                    return patchedMessages.ToArray();
            }
        }
        public IReadOnlyList<string> GetMessageReferences
        {
            get
            {
                lock (gate)
                    return getMessageReferences.ToArray();
            }
        }
        public IReadOnlyList<string> UplinkPages => PatchedMessages
            .Where(static item => item.MessageReference == "10001" &&
                                  CourierTextCodec.GetKind(item.Content) == CourierTextKind.Page)
            .Select(static item => item.Content)
            .ToArray();
        public IReadOnlyList<string> DownlinkAcknowledgements => PatchedMessages
            .Where(static item => item.MessageReference == "10002" &&
                                  CourierTextCodec.GetKind(item.Content) == CourierTextKind.Acknowledgement)
            .Select(static item => item.Content)
            .ToArray();
        public IReadOnlyList<string> UplinkAcknowledgements => PatchedMessages
            .Where(static item => item.MessageReference == "10001" &&
                                  CourierTextCodec.GetKind(item.Content) == CourierTextKind.Acknowledgement)
            .Select(static item => item.Content)
            .ToArray();
        public IReadOnlyList<string> Presences => PatchedMessages
            .Where(static item => item.MessageReference == "10001" &&
                                  CourierTextCodec.GetKind(item.Content) == CourierTextKind.Presence)
            .Select(static item => item.Content)
            .ToArray();

        public void SetContent(string messageReference, string content)
        {
            lock (gate)
                messageContents[messageReference] = content;
        }

        public void FailNextPatches(int count)
        {
            if (count < 1)
                throw new ArgumentOutOfRangeException(nameof(count));
            Interlocked.Exchange(ref transientPatchFailures, count);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (RequestThreadIds)
                RequestThreadIds.Add(Environment.CurrentManagedThreadId);
            if (request.Method == HttpMethod.Post)
            {
                var attempt = Interlocked.Increment(ref postAttempts);
                if (attempt == 1)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(payload);
                return Json(HttpStatusCode.OK, new
                {
                    id = "10001",
                    content = document.RootElement.GetProperty("content").GetString(),
                });
            }
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref getAttempts);
                var messageReference = request.RequestUri!.Segments[^1];
                string content;
                lock (gate)
                {
                    getMessageReferences.Add(messageReference);
                    content = messageContents[messageReference];
                }
                return Json(HttpStatusCode.OK, new { id = messageReference, content });
            }
            if (request.Method == HttpMethod.Patch)
            {
                var attempt = Interlocked.Increment(ref patchAttempts);
                var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(payload);
                var content = document.RootElement.GetProperty("content").GetString()!;
                var messageReference = request.RequestUri!.Segments[^1];
                if (failAllPatches || failFirstPatch && attempt == 1 || TryConsumeTransientPatchFailure())
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                lock (gate)
                {
                    patchedMessages.Add((messageReference, content));
                    messageContents[messageReference] = content;
                }
                return Json(HttpStatusCode.OK, new { id = messageReference, content });
            }
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        private bool TryConsumeTransientPatchFailure()
        {
            while (Volatile.Read(ref transientPatchFailures) is var remaining && remaining > 0)
            {
                if (Interlocked.CompareExchange(
                        ref transientPatchFailures,
                        remaining - 1,
                        remaining) == remaining)
                    return true;
            }

            return false;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object payload) => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };
    }

    private sealed class BlockingPatchWebhookHandler(bool failFetches) : HttpMessageHandler
    {
        public TaskCompletionSource<bool> PatchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                if (failFetches)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var messageReference = request.RequestUri!.Segments[^1];
                return Json(HttpStatusCode.OK, new
                {
                    id = messageReference,
                    content = CourierTextCodec.EmptySlotContent,
                });
            }
            if (request.Method == HttpMethod.Patch)
            {
                PatchStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object payload) => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };
    }

    private sealed class ThrowingWebhookHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("synthetic-pump-failure"));
    }

    private sealed class CryptoFixture : IDisposable
    {
        private readonly FixtureKeyResolver keyResolver;
        public OwnerId OwnerId { get; }
        public IslandId IslandId { get; }
        public int EndpointKeyVersion { get; } = 1;
        public int RelayKeyVersion { get; } = 1;
        public byte[] EndpointSigningPrivateKey { get; } = RandomNumberGenerator.GetBytes(32);
        public byte[] EndpointAgreementPrivateKey { get; } = RandomNumberGenerator.GetBytes(32);
        public byte[] RelaySigningPrivateKey { get; } = RandomNumberGenerator.GetBytes(32);
        public byte[] RelayAgreementPrivateKey { get; } = RandomNumberGenerator.GetBytes(32);
        public byte[] EndpointSigningPublicKey { get; }
        public byte[] EndpointAgreementPublicKey { get; }
        public byte[] RelaySigningPublicKey { get; }
        public byte[] RelayAgreementPublicKey { get; }
        public CourierEpochDescriptor UplinkEpoch { get; }
        public CourierEpochDescriptor DownlinkEpoch { get; }
        public EndpointPublicKeys RelayPublicKeys { get; }

        public CryptoFixture(
            string ownerId = "owner-local",
            string islandId = "island-local")
        {
            OwnerId = new OwnerId(ownerId);
            IslandId = new IslandId(islandId);
            EndpointSigningPublicKey = BouncyCastlePrimitives.DeriveEd25519PublicKey(
                EndpointSigningPrivateKey);
            EndpointAgreementPublicKey = BouncyCastlePrimitives.DeriveX25519PublicKey(
                EndpointAgreementPrivateKey);
            RelaySigningPublicKey = BouncyCastlePrimitives.DeriveEd25519PublicKey(
                RelaySigningPrivateKey);
            RelayAgreementPublicKey = BouncyCastlePrimitives.DeriveX25519PublicKey(
                RelayAgreementPrivateKey);
            RelayPublicKeys = new EndpointPublicKeys(
                RelayKeyVersion,
                "relay-signing-one",
                ImmutableArray.CreateRange(RelaySigningPublicKey),
                "relay-agreement-one",
                ImmutableArray.CreateRange(RelayAgreementPublicKey));
            var startsAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            UplinkEpoch = CreateEpoch(
                Guid.NewGuid(),
                CourierDirection.Uplink,
                "10001",
                startsAt);
            DownlinkEpoch = CreateEpoch(
                Guid.NewGuid(),
                CourierDirection.Downlink,
                "10002",
                startsAt);
            keyResolver = new FixtureKeyResolver(this);
        }

        public DadAutoPartyWebhookCredential Credential() =>
            new("323456789012345678", new string('a', 64), "423456789012345678")
            {
                UplinkEpoch = UplinkEpoch,
                DownlinkEpoch = DownlinkEpoch,
                RelayPublicKeys = RelayPublicKeys,
            };

        public (OpaqueEnvelope Envelope, string Page) CreateDownlink(byte[] payload)
        {
            var envelope = DadAutoPartyWebhookEndpointTests.Envelope(
                DadAutoPartyIdentityPackageService.RegistrationRecipient,
                IslandId.Value,
                payload);
            var fragment = Assert.Single(CourierFragmentCodec.Fragment(
                envelope.EnvelopeId,
                envelope.PayloadType,
                envelope.Ciphertext.AsSpan(),
                envelope.ExpiresAt));
            var header = CreateHeader(
                $"downlink-{envelope.EnvelopeId:N}",
                DownlinkEpoch.EpochGeneration,
                envelope.ExpiresAt);
            var page = new CourierPage(
                header,
                DownlinkEpoch.EpochId,
                CourierDirection.Downlink,
                1,
                DownlinkEpoch.PageCount,
                1,
                ImmutableArray.Create(fragment));
            var authenticator = new ProductionContractAuthenticator(keyResolver);
            return (envelope, CourierTextCodec.EncodePage(authenticator.Sign(page)));
        }

        public string CreateUplinkAcknowledgement(
            CourierPage page,
            long? acknowledgedPageGeneration = null)
        {
            var fragment = Assert.Single(page.Fragments);
            var pageGeneration = acknowledgedPageGeneration ?? page.PageGeneration;
            var header = CreateHeader(
                $"uplink-ack-{page.Header.MessageId:N}",
                pageGeneration,
                DateTimeOffset.UtcNow.AddMinutes(2));
            var acknowledgement = new CourierAcknowledgement(
                header,
                UplinkEpoch.EpochId,
                CourierDirection.Uplink,
                page.PageNumber,
                pageGeneration,
                ImmutableArray.Create(new CourierFragmentReceipt(
                    fragment.DeliveryId,
                    fragment.FragmentNumber)),
                ImmutableArray<Guid>.Empty,
                "relay-uplink-fragment-accepted");
            var authenticator = new ProductionContractAuthenticator(keyResolver);
            return CourierTextCodec.EncodeAcknowledgement(authenticator.Sign(acknowledgement));
        }

        public string CreateEpochAnnouncement(CourierEpochDescriptor epoch)
        {
            var header = CreateHeader(
                $"epoch-{epoch.EpochId:N}-{epoch.EpochGeneration}",
                epoch.EpochGeneration,
                DateTimeOffset.UtcNow.AddMinutes(2));
            var announcement = new CourierEpoch(
                header,
                epoch.EpochId,
                epoch.IslandId,
                epoch.Direction,
                epoch.StartsAt,
                epoch.RotatesAt,
                epoch.OverlapEndsAt,
                epoch.PageCount,
                epoch.PageReferences,
                epoch.EpochGeneration);
            var authenticator = new ProductionContractAuthenticator(keyResolver);
            return CourierTextCodec.EncodeEpoch(authenticator.Sign(announcement));
        }

        public CourierPresence OpenPresence(string content)
        {
            var presence = CourierTextCodec.DecodePresence(content);
            var authenticator = new ProductionContractAuthenticator(keyResolver);
            Assert.True(authenticator.Verify(presence).Succeeded);
            return presence.Contract;
        }

        public string CreateBootstrapCopyPaste(
            Guid registrationId,
            IslandId? recipient = null,
            DateTimeOffset? issuedAt = null,
            DateTimeOffset? expiresAt = null,
            string routeId = "route-one")
        {
            var observedAt = issuedAt ?? DateTimeOffset.UtcNow;
            var bootstrapExpiresAt = expiresAt ?? observedAt.AddMinutes(5);
            var header = CreateHeader(
                $"bootstrap-{registrationId:N}",
                1,
                bootstrapExpiresAt,
                recipient,
                observedAt);
            var bootstrap = new RegistrationBootstrap(
                header,
                registrationId,
                OwnerId,
                IslandId,
                "123456789012345678",
                "223456789012345678",
                routeId,
                new WebhookMailboxCredential(
                    "323456789012345678",
                    new string('a', 64),
                    "423456789012345678"),
                UplinkEpoch,
                DownlinkEpoch,
                RelayPublicKeys,
                bootstrapExpiresAt);
            return RegistrationCopyPasteCodec.EncodeBootstrap(
                InitialRegistrationBootstrapSealer.Seal(
                    bootstrap,
                    RelaySigningPrivateKey,
                    EndpointAgreementPublicKey));
        }

        private CourierEpochDescriptor CreateEpoch(
            Guid epochId,
            CourierDirection direction,
            string messageReference,
            DateTimeOffset startsAt) =>
            new(
                epochId,
                IslandId,
                direction,
                startsAt,
                startsAt.AddMinutes(5),
                startsAt.AddMinutes(6),
                1,
                ImmutableArray.Create(new CourierPageReference(1, messageReference)),
                1);

        private ContractHeader CreateHeader(
            string idempotencyKey,
            long generation,
            DateTimeOffset expiresAt,
            IslandId? recipient = null,
            DateTimeOffset? issuedAt = null)
        {
            var nonce = RandomNumberGenerator.GetBytes(AutoPartyProtocol.ContractNonceBytes);
            try
            {
                return new ContractHeader(
                    AutoPartyProtocol.CurrentVersion,
                    Guid.NewGuid(),
                    idempotencyKey,
                    new IslandId(DadAutoPartyIdentityPackageService.RegistrationRecipient),
                    recipient ?? IslandId,
                    issuedAt ?? DateTimeOffset.UtcNow,
                    expiresAt,
                    Math.Max(1, generation),
                    Math.Max(1, generation),
                    RelayKeyVersion,
                    EndpointKeyVersion,
                    ContractHeader.CreateNonce(nonce),
                    ImmutableArray<int>.Empty);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
            }
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(EndpointSigningPrivateKey);
            CryptographicOperations.ZeroMemory(EndpointAgreementPrivateKey);
            CryptographicOperations.ZeroMemory(RelaySigningPrivateKey);
            CryptographicOperations.ZeroMemory(RelayAgreementPrivateKey);
            CryptographicOperations.ZeroMemory(EndpointSigningPublicKey);
            CryptographicOperations.ZeroMemory(EndpointAgreementPublicKey);
            CryptographicOperations.ZeroMemory(RelaySigningPublicKey);
            CryptographicOperations.ZeroMemory(RelayAgreementPublicKey);
        }

        private sealed class FixtureKeyResolver(CryptoFixture fixture) : IContractKeyResolver
        {
            public bool TryGetEd25519PrivateKey(
                IslandId islandId,
                long keyVersion,
                out ReadOnlyMemory<byte> privateKey) =>
                TrySelect(
                    islandId,
                    keyVersion,
                    fixture.RelaySigningPrivateKey,
                    fixture.EndpointSigningPrivateKey,
                    out privateKey);

            public bool TryGetEd25519PublicKey(
                IslandId islandId,
                long keyVersion,
                out ReadOnlyMemory<byte> publicKey) =>
                TrySelect(
                    islandId,
                    keyVersion,
                    fixture.RelaySigningPublicKey,
                    fixture.EndpointSigningPublicKey,
                    out publicKey);

            public bool TryGetX25519PrivateKey(
                IslandId islandId,
                long keyVersion,
                out ReadOnlyMemory<byte> privateKey) =>
                TrySelect(
                    islandId,
                    keyVersion,
                    fixture.RelayAgreementPrivateKey,
                    fixture.EndpointAgreementPrivateKey,
                    out privateKey);

            public bool TryGetX25519PublicKey(
                IslandId islandId,
                long keyVersion,
                out ReadOnlyMemory<byte> publicKey) =>
                TrySelect(
                    islandId,
                    keyVersion,
                    fixture.RelayAgreementPublicKey,
                    fixture.EndpointAgreementPublicKey,
                    out publicKey);

            private bool TrySelect(
                IslandId islandId,
                long keyVersion,
                byte[] relayKey,
                byte[] endpointKey,
                out ReadOnlyMemory<byte> key)
            {
                if (islandId.Value == DadAutoPartyIdentityPackageService.RegistrationRecipient &&
                    keyVersion == fixture.RelayKeyVersion)
                {
                    key = relayKey;
                    return true;
                }
                if (islandId == fixture.IslandId && keyVersion == fixture.EndpointKeyVersion)
                {
                    key = endpointKey;
                    return true;
                }
                key = default;
                return false;
            }
        }
    }

    private sealed class MemoryWebhookStore : IDadAutoPartyWebhookCredentialStore
    {
        public DadAutoPartyWebhookCredential? StoredCredential { get; private set; }
        public string StoredReference { get; private set; } = string.Empty;

        public ValueTask<string> StoreAsync(
            DadAutoPartyWebhookCredential credential,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoredCredential = credential;
            StoredReference = "webhook-mailbox-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            return ValueTask.FromResult(StoredReference);
        }

        public ValueTask<DadAutoPartyWebhookCredential> LoadAsync(
            string credentialReference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StoredCredential is { } credential &&
                   string.Equals(credentialReference, StoredReference, StringComparison.Ordinal)
                ? ValueTask.FromResult(credential)
                : throw new InvalidOperationException("missing-test-credential");
        }

        public ValueTask<bool> DeleteAsync(
            string credentialReference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existed = StoredCredential != null &&
                string.Equals(credentialReference, StoredReference, StringComparison.Ordinal);
            if (existed)
            {
                StoredCredential = null;
                StoredReference = string.Empty;
            }
            return ValueTask.FromResult(existed);
        }
    }

    private sealed class MemoryLegacyTokenStore : IDadAutoPartyDiscordTokenStore
    {
        public ValueTask<string> StoreAsync(
            ReadOnlyMemory<char> token,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<char[]> LoadAsync(
            string tokenReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(
            string tokenReference,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }

    private sealed class MemoryPendingOperationStore : IDadAutoPartyPendingOperationStore
    {
        public DadAutoPartyPendingDeregistration? LoadDeregistration() => null;

        public void SaveDeregistration(DadAutoPartyPendingDeregistration pending)
        {
        }

        public void ClearDeregistration(Guid deregistrationId)
        {
        }
    }

    private sealed class MemoryIdentityStore : IDadAutoPartyEndpointIdentityStore, IDisposable
    {
        private byte[]? identityMaterial;

        public ValueTask<string> StoreAsync(
            ReadOnlyMemory<byte> identityMaterial,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (this.identityMaterial != null)
                CryptographicOperations.ZeroMemory(this.identityMaterial);
            this.identityMaterial = identityMaterial.ToArray();
            return ValueTask.FromResult("identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        }

        public ValueTask<byte[]> LoadAsync(
            string identityReference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return identityMaterial is { } stored
                ? ValueTask.FromResult(stored.ToArray())
                : throw new InvalidOperationException("missing-test-identity");
        }

        public ValueTask<bool> DeleteAsync(
            string identityReference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (identityMaterial == null)
                return ValueTask.FromResult(false);
            CryptographicOperations.ZeroMemory(identityMaterial);
            identityMaterial = null;
            return ValueTask.FromResult(true);
        }

        public void Dispose()
        {
            if (identityMaterial != null)
                CryptographicOperations.ZeroMemory(identityMaterial);
        }
    }
}
