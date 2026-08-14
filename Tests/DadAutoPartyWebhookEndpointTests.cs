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

        var bootstrapToken = crypto.CreateBootstrapCopyPaste(registrationId);
        var imported = await endpoint.ImportBootstrapCopyPasteAsync(
            RegistrationCopyPasteCodec.FormatBootstrapResponse(bootstrapToken));

        Assert.True(imported.Allowed, imported.SafeCode);
        Assert.Equal(DadAutoPartyRegistrationState.BootstrapImported, configuration.RegistrationState);
        Assert.True(configuration.HasImportedBootstrap);
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

        var activated = endpoint.MarkRegistrationActive(
            registrationId,
            crypto.UplinkEpoch.EpochId,
            crypto.UplinkEpoch.EpochGeneration);

        Assert.True(activated.Allowed, activated.SafeCode);
        Assert.True(configuration.IsRegistrationActive);

        var replayed = await endpoint.ImportBootstrapCopyPasteAsync(bootstrapToken);
        Assert.False(replayed.Allowed);
        Assert.Equal("dad-bootstrap-replayed", replayed.SafeCode);
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
    }

    [Fact]
    public void AutoPartyWindowShowsDirectBilateralPairingAndUnmaskedBootstrapInput()
    {
        var source = ReadRepositorySource("Windows", "DadAutoPartyWindow.cs");
        var bootstrapStart = source.IndexOf("\"Encrypted bootstrap DM\"", StringComparison.Ordinal);
        var bootstrapEnd = source.IndexOf("\"Import bootstrap\"", bootstrapStart, StringComparison.Ordinal);

        Assert.Contains("Enable bot DMs before registering", source, StringComparison.Ordinal);
        Assert.Contains("transport-channel traffic is private machine traffic", source, StringComparison.Ordinal);
        Assert.Contains("relay acknowledgement is pending", source, StringComparison.Ordinal);
        Assert.Contains("ImGui.BeginDisabled(!registrationReady)", source, StringComparison.Ordinal);
        Assert.Contains("This DAD island ID", source, StringComparison.Ordinal);
        Assert.Contains("Copy island ID", source, StringComparison.Ordinal);
        Assert.Contains("Peer island ID", source, StringComparison.Ordinal);
        Assert.Contains("Initiate bilateral pairing by island ID", source, StringComparison.Ordinal);
        Assert.Contains("Pairing is bilateral", source, StringComparison.Ordinal);
        Assert.Contains("One character\\0Selected characters\\0All characters for this peer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Opaque handles (comma-separated)", source, StringComparison.Ordinal);
        Assert.True(bootstrapStart >= 0);
        Assert.True(bootstrapEnd > bootstrapStart);
        Assert.DoesNotContain("Password", source[bootstrapStart..bootstrapEnd], StringComparison.Ordinal);
        Assert.Contains("endpoint.SafeCode", source, StringComparison.Ordinal);
        Assert.Contains("Last mailbox exchange", source, StringComparison.Ordinal);
        Assert.Contains("Mailbox queues:", source, StringComparison.Ordinal);
        Assert.Contains("Status: {status}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TransportChannelIds", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewChannel", source, StringComparison.Ordinal);
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
    public async Task AdapterQueuesOffCallerRetriesPublishAndPollsExactKnownMessage()
    {
        using var crypto = new CryptoFixture();
        var outbound = Envelope("island-local", "central-autoparty", [1, 2, 3, 4]);
        var (downlink, downlinkPage) = crypto.CreateDownlink([5, 6, 7, 8]);
        var handler = new ScriptedWebhookHandler(downlinkPage);
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
        for (var attempt = 0; attempt < 100 && handler.PatchAttempts < 2; attempt++)
            await Task.Delay(10);
        Assert.True(handler.PatchAttempts >= 2);
        Assert.True(handler.GetAttempts >= 1);
        Assert.DoesNotContain(callerThread, handler.RequestThreadIds.Take(1));

        await adapter.AcknowledgeAsync(new AutoPartyTransportAcknowledgement(
            downlink.EnvelopeId,
            "dad-downlink-consumed"));
        var priorPatchAttempts = handler.PatchAttempts;
        for (var attempt = 0; attempt < 100 && handler.PatchAttempts == priorPatchAttempts; attempt++)
            await Task.Delay(10);
        Assert.True(handler.PatchAttempts > priorPatchAttempts);
        Assert.Contains(handler.PatchedContents, content =>
            CourierTextCodec.GetKind(content) == CourierTextKind.Page);
        Assert.Contains(handler.PatchedContents, content =>
            CourierTextCodec.GetKind(content) == CourierTextKind.Acknowledgement);
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

    private static OpaqueEnvelope Envelope(string sender, string recipient, byte[] payload) =>
        new(
            AutoPartyProtocol.CurrentVersion,
            Guid.NewGuid(),
            new IslandId(sender),
            new IslandId(recipient),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            1,
            "test-envelope",
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

    private sealed class ScriptedWebhookHandler(string downlinkContent) : HttpMessageHandler
    {
        private int postAttempts;
        private int getAttempts;
        private int patchAttempts;
        private readonly object patchedContentsGate = new();
        private readonly List<string> patchedContents = [];
        public int PostAttempts => Volatile.Read(ref postAttempts);
        public int GetAttempts => Volatile.Read(ref getAttempts);
        public int PatchAttempts => Volatile.Read(ref patchAttempts);
        public List<int> RequestThreadIds { get; } = [];
        public IReadOnlyList<string> PatchedContents
        {
            get
            {
                lock (patchedContentsGate)
                    return patchedContents.ToArray();
            }
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
                return Json(HttpStatusCode.OK, new { id = messageReference, content = downlinkContent });
            }
            if (request.Method == HttpMethod.Patch)
            {
                var attempt = Interlocked.Increment(ref patchAttempts);
                var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(payload);
                var content = document.RootElement.GetProperty("content").GetString()!;
                lock (patchedContentsGate)
                    patchedContents.Add(content);
                if (attempt == 1)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                var messageReference = request.RequestUri!.Segments[^1];
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

    private sealed class CryptoFixture : IDisposable
    {
        private readonly FixtureKeyResolver keyResolver;
        public OwnerId OwnerId { get; } = new("owner-local");
        public IslandId IslandId { get; } = new("island-local");
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

        public CryptoFixture()
        {
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

        public string CreateBootstrapCopyPaste(
            Guid registrationId,
            IslandId? recipient = null,
            DateTimeOffset? issuedAt = null,
            DateTimeOffset? expiresAt = null)
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
                "route-one",
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

        public ValueTask<string> StoreAsync(
            DadAutoPartyWebhookCredential credential,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoredCredential = credential;
            return ValueTask.FromResult("webhook-mailbox-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        }

        public ValueTask<DadAutoPartyWebhookCredential> LoadAsync(
            string credentialReference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StoredCredential is { } credential
                ? ValueTask.FromResult(credential)
                : throw new InvalidOperationException("missing-test-credential");
        }

        public ValueTask<bool> DeleteAsync(
            string credentialReference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existed = StoredCredential != null;
            StoredCredential = null;
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
