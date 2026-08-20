using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using AutoParty.Contracts;
using AutoParty.Core.Authentication;
using AutoParty.Core.Cryptography;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAutoPartyRelayPumpTests
{
    [Fact]
    public void SemanticKeyResolverRequiresExactVersionsAndZeroesPrivateMaterial()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        using var resolver = new DadAutoPartySemanticKeyResolver(fixture.Configuration, fixture.Identity);

        Assert.True(resolver.TryGetEd25519PrivateKey(
            new IslandId(PumpFixture.LocalIsland), 1, out var signingPrivate));
        Assert.Equal(fixture.LocalSigningPrivate, signingPrivate.ToArray());
        Assert.False(resolver.TryGetEd25519PrivateKey(
            new IslandId(PumpFixture.LocalIsland), 2, out _));
        Assert.True(resolver.TryGetX25519PublicKey(
            new IslandId(PumpFixture.PeerIsland), 3, out var peerAgreement));
        Assert.Equal(fixture.PeerAgreementPublic, peerAgreement.ToArray());
        Assert.False(resolver.TryGetEd25519PublicKey(new IslandId("unknown-island"), 1, out _));

        resolver.Dispose();
        Assert.All(signingPrivate.ToArray(), static value => Assert.Equal(0, value));
    }

    [Fact]
    public void TransientRequesterKeysRequireFingerprintAndZeroOnRemovalOrExpiry()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        using var resolver = new DadAutoPartySemanticKeyResolver(fixture.Configuration, fixture.Identity);
        var signingPrivate = RandomNumberGenerator.GetBytes(32);
        var agreementPrivate = RandomNumberGenerator.GetBytes(32);
        var signingPublic = BouncyCastlePrimitives.DeriveEd25519PublicKey(signingPrivate);
        var agreementPublic = BouncyCastlePrimitives.DeriveX25519PublicKey(agreementPrivate);
        try
        {
            const string owner = "owner-attested";
            const string island = "island-attested";
            const int version = 7;
            var keys = new EndpointPublicKeys(
                version,
                "attested-signing-7",
                ImmutableArray.CreateRange(signingPublic),
                "attested-agreement-7",
                ImmutableArray.CreateRange(agreementPublic));
            var fingerprint = DadAutoPartyIdentityPackageService.BuildFingerprint(
                owner,
                island,
                version,
                signingPublic,
                agreementPublic);
            var now = DateTimeOffset.UtcNow;

            Assert.False(resolver.TryAddTransientPublicKeys(
                new OwnerId(owner),
                new IslandId(island),
                keys,
                new string('F', 64),
                now.AddMinutes(2),
                now));
            Assert.True(resolver.TryAddTransientPublicKeys(
                new OwnerId(owner),
                new IslandId(island),
                keys,
                fingerprint,
                now.AddMinutes(2),
                now));
            Assert.True(resolver.TryGetEd25519PublicKey(new IslandId(island), version, out var observed));
            Assert.Equal(signingPublic, observed.ToArray());

            resolver.RemoveTransientPublicKeys(island);
            Assert.All(observed.ToArray(), static value => Assert.Equal(0, value));
            Assert.False(resolver.TryGetEd25519PublicKey(new IslandId(island), version, out _));

            Assert.True(resolver.TryAddTransientPublicKeys(
                new OwnerId(owner),
                new IslandId(island),
                keys,
                fingerprint,
                now.AddSeconds(1),
                now));
            resolver.ExpireTransientPublicKeys(now.AddSeconds(2));
            Assert.False(resolver.TryGetX25519PublicKey(new IslandId(island), version, out _));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingPrivate);
            CryptographicOperations.ZeroMemory(agreementPrivate);
            CryptographicOperations.ZeroMemory(signingPublic);
            CryptographicOperations.ZeroMemory(agreementPublic);
        }
    }

    [Fact]
    public async Task App1AttemptRemainsStableSubmitsWithoutPeerClipboardPersistenceAndRegeneratesAfterCancellation()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        await using var pump = fixture.CreatePump(utcNow: () => now);

        var generated = await pump.EnsurePairingInviteAsync();
        Assert.True(generated.Allowed, generated.SafeCode);
        var firstToken = fixture.Configuration.PairingInviteToken;
        var localSigned = PairingCopyPasteCodec.DecodeInvite(firstToken);
        var localCanonical = CanonicalCborCodec.EncodeUnsigned(localSigned.Contract);
        Assert.Equal(TimeSpan.FromMinutes(10), localSigned.Contract.InviteExpiresAt - localSigned.Contract.Header.IssuedAt);
        Assert.True(BouncyCastlePrimitives.Ed25519Verify(
            fixture.LocalSigningPublic,
            localCanonical,
            localSigned.AuthenticationTag.AsSpan()));
        Assert.Equal("dad-pairing-invite-current", (await pump.EnsurePairingInviteAsync()).SafeCode);
        Assert.Equal(firstToken, fixture.Configuration.PairingInviteToken);

        var peerSigned = CreatePeerPairingInvite(fixture, now);
        var peerToken = PairingCopyPasteCodec.EncodeInvite(peerSigned);
        Assert.True(pump.TryValidatePeerPairingInvite(peerToken, out var peerInvite, out var peerSafeCode));
        Assert.Equal("dad-pairing-peer-invite-valid", peerSafeCode);
        Assert.NotNull(peerInvite);
        Assert.False(pump.TryValidatePeerPairingInvite(firstToken, out _, out _));
        var policy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.CharacterList,
            CharacterHandles = ["character-local"],
            Enabled = true,
            Revision = 3,
            UpdatedAtUtc = now.UtcDateTime,
        };
        var intentNonce = RandomNumberGenerator.GetBytes(AutoPartyProtocol.ContractNonceBytes);
        var manualIntent = new PairingIntent(
            new ContractHeader(
                AutoPartyProtocol.CurrentVersion,
                Guid.NewGuid(),
                "pairing-intent-validation",
                new IslandId(PumpFixture.LocalIsland),
                new IslandId(DadAutoPartyIdentityPackageService.RegistrationRecipient),
                now,
                now.AddMinutes(10),
                1,
                1,
                1,
                2,
                ContractHeader.CreateNonce(intentNonce),
                []),
            localSigned.Contract.AttemptId,
            localSigned.Contract.RegistrationId,
            ImmutableArray.CreateRange(CanonicalCborCodec.EncodeSigned(localSigned)),
            ImmutableArray.CreateRange(CanonicalCborCodec.EncodeSigned(peerSigned)),
            new CharacterSharePolicy(
                CharacterShareMode.CharacterList,
                [new OpaqueCharacterId("character-local")],
                true,
                3,
                now));
        Assert.NotEmpty(CanonicalCborCodec.EncodeUnsigned(manualIntent));
        CryptographicOperations.ZeroMemory(intentNonce);

        var submitted = await pump.SubmitPairingAsync(peerToken, policy);
        var replay = await pump.SubmitPairingAsync(peerToken, policy);
        Assert.True(submitted.Allowed, submitted.SafeCode);
        Assert.Equal("dad-pairing-intent-queued", submitted.SafeCode);
        Assert.True(replay.Allowed, replay.SafeCode);
        Assert.Equal("dad-pairing-intent-idempotent", replay.SafeCode);
        Assert.True(fixture.Configuration.PairingAttemptSubmitted);
        Assert.DoesNotContain(peerToken, JsonSerializer.Serialize(fixture.Configuration), StringComparison.Ordinal);

        await pump.ProcessOnceAsync();
        var intentEnvelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<PairingIntent>());
        var intent = fixture.Open<PairingIntent>(intentEnvelope);
        Assert.Equal(localSigned.Contract.AttemptId, intent.AttemptId);
        Assert.Equal(peerSigned.Contract.AttemptId, CanonicalCborCodec.DecodeSigned<PairingInvite>(
            intent.PeerInvite.AsMemory()).Contract.AttemptId);

        var regeneration = await pump.EnsurePairingInviteAsync(regenerate: true);
        Assert.True(regeneration.Allowed, regeneration.SafeCode);
        Assert.Equal("dad-pairing-cancellation-queued", regeneration.SafeCode);
        Assert.Equal(firstToken, fixture.Configuration.PairingInviteToken);
        await pump.ProcessOnceAsync();
        var cancellationEnvelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<PairingAttemptCancellation>());
        var cancellation = fixture.Open<PairingAttemptCancellation>(cancellationEnvelope);
        Assert.Equal(localSigned.Contract.AttemptId, cancellation.AttemptId);

        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
            fixture.RelayHeader("pairing-cancellation-receipt", now),
            Guid.NewGuid(),
            cancellation.Header.MessageId,
            true,
            "pairing-attempt-cancelled")));
        await pump.ProcessOnceAsync();
        Assert.Equal("pairing-attempt-cancelled", pump.Snapshot.SafeCode);
        Assert.Equal(string.Empty, fixture.Configuration.PairingAttemptId);
        Assert.Equal(string.Empty, fixture.Configuration.PairingInviteToken);

        Assert.True((await pump.EnsurePairingInviteAsync()).Allowed);
        var secondToken = fixture.Configuration.PairingInviteToken;
        Assert.NotEqual(firstToken, secondToken);
        now = now.AddMinutes(11);
        Assert.True((await pump.EnsurePairingInviteAsync()).Allowed);
        Assert.NotEqual(secondToken, fixture.Configuration.PairingInviteToken);
    }

    [Fact]
    public async Task App1RejectsAnAlreadyActivePeerBeforeSubmission()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump(utcNow: () => now);
        var peerToken = PairingCopyPasteCodec.EncodeInvite(CreatePeerPairingInvite(fixture, now));

        Assert.False(pump.TryValidatePeerPairingInvite(peerToken, out _, out var safeCode));
        Assert.Equal("dad-pairing-peer-already-active", safeCode);
    }

    [Fact]
    public async Task PairingEstablishedInstallsPeerAliasAndPoliciesThenInvalidatesTheAttempt()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        await using var pump = fixture.CreatePump(utcNow: () => now);
        Assert.True((await pump.EnsurePairingInviteAsync()).Allowed);
        var localSigned = PairingCopyPasteCodec.DecodeInvite(fixture.Configuration.PairingInviteToken);
        var peerSigned = CreatePeerPairingInvite(fixture, now);
        var localPolicy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.AllCharactersForPeer,
            Enabled = true,
            Revision = 4,
            UpdatedAtUtc = now.UtcDateTime,
        };
        Assert.True((await pump.SubmitPairingAsync(
            PairingCopyPasteCodec.EncodeInvite(peerSigned),
            localPolicy)).Allowed);
        await pump.ProcessOnceAsync();
        var intent = fixture.Open<PairingIntent>(Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<PairingIntent>()));
        var header = fixture.RelayHeader("pairing-established", now);
        var peerPolicy = new CharacterSharePolicy(
            CharacterShareMode.SpecificCharacter,
            [new OpaqueCharacterId("character-peer")],
            true,
            5,
            header.IssuedAt);
        var established = new PairingEstablished(
            header,
            Guid.NewGuid(),
            localSigned.Contract.AttemptId,
            peerSigned.Contract.AttemptId,
            peerSigned.Contract.OwnerId,
            peerSigned.Contract.IslandId,
            "Peer-endpoint",
            peerSigned.Contract.PublicKeys,
            peerSigned.Contract.EndpointFingerprint,
            new string('a', 64),
            intent.SharePolicy,
            peerPolicy,
            header.IssuedAt);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(established));

        await pump.ProcessOnceAsync();

        Assert.Equal("dad-pairing-active", pump.Snapshot.SafeCode);
        var pairing = Assert.Single(fixture.Configuration.Pairings);
        Assert.True(pairing.IsActive);
        Assert.Equal(PumpFixture.PeerIsland, pairing.IslandId);
        Assert.Equal("Peer-endpoint", pairing.PeerEndpointAlias);
        Assert.Equal(localPolicy.Mode, pairing.LocalSharePolicy.Mode);
        Assert.Equal(DadAutoPartyCharacterShareMode.SpecificCharacter, pairing.PeerSharePolicy.Mode);
        Assert.Equal(["character-peer"], pairing.PeerSharePolicy.CharacterHandles);
        Assert.Equal(string.Empty, fixture.Configuration.PairingAttemptId);
        Assert.Equal(string.Empty, fixture.Configuration.PairingInviteToken);
        Assert.True((await pump.EnsurePairingInviteAsync()).Allowed);
        Assert.NotEqual(localSigned.Contract.AttemptId.ToString("D"), fixture.Configuration.PairingAttemptId);
    }

    [Fact]
    public async Task RegistrationActivatesAndAcknowledgesOnlyAuthenticatedReceipt()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.BootstrapImported);
        await using var pump = fixture.CreatePump();
        var activated = false;
        pump.ConfigureLifecycleHandlers(
            receipt =>
            {
                activated = true;
                fixture.Configuration.RegistrationState = DadAutoPartyRegistrationState.Active;
                return new(true, "dad-registration-active", 2);
            },
            static (_, _, _) => ValueTask.FromResult(new DadAutoPartyPrivacyResult(false, false, "unused")));

        var receipt = new RegistrationReceipt(
            fixture.RelayHeader("registration-receipt"),
            Guid.Parse(fixture.Configuration.RegistrationId),
            new OwnerId(PumpFixture.LocalOwner),
            new IslandId(PumpFixture.LocalIsland),
            true,
            1,
            "registration-active");
        var delivery = fixture.SealRelay(receipt);
        fixture.Transport.Inbound.Enqueue(delivery);

        await pump.ProcessOnceAsync();

        Assert.True(activated);
        Assert.Contains(fixture.Transport.Acknowledged, item => item.EnvelopeId == delivery.EnvelopeId);
        Assert.Equal(DadAutoPartyRegistrationState.Active, fixture.Configuration.RegistrationState);
    }

    [Fact]
    public async Task RegistrationControlPlaneRunsWhileExecutionOptInIsDisabled()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.BootstrapImported);
        fixture.Configuration.Enabled = false;
        await using var pump = fixture.CreatePump();
        pump.ConfigureLifecycleHandlers(
            _ =>
            {
                fixture.Configuration.RegistrationState = DadAutoPartyRegistrationState.Active;
                return new(true, "dad-registration-active", 2);
            },
            static (_, _, _) => ValueTask.FromResult(new DadAutoPartyPrivacyResult(false, false, "unused")));
        var receipt = new RegistrationReceipt(
            fixture.RelayHeader("registration-control-disabled"),
            Guid.Parse(fixture.Configuration.RegistrationId),
            new OwnerId(PumpFixture.LocalOwner),
            new IslandId(PumpFixture.LocalIsland),
            true,
            1,
            "registration-active");
        var delivery = fixture.SealRelay(receipt);
        fixture.Transport.Inbound.Enqueue(delivery);

        await pump.ProcessOnceAsync();

        Assert.Equal(DadAutoPartyRegistrationState.Active, fixture.Configuration.RegistrationState);
        Assert.Contains(fixture.Transport.Acknowledged, item => item.EnvelopeId == delivery.EnvelopeId);
        Assert.Contains(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<RegistrationHello>());
    }

    [Fact]
    public async Task RejectedRegistrationReceiptIsNotTransportAcknowledged()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.BootstrapImported);
        await using var pump = fixture.CreatePump();
        var handlerCalled = false;
        pump.ConfigureLifecycleHandlers(
            _ =>
            {
                handlerCalled = true;
                return new(true, "unexpected", 1);
            },
            static (_, _, _) => ValueTask.FromResult(new DadAutoPartyPrivacyResult(false, false, "unused")));
        var receipt = new RegistrationReceipt(
            fixture.RelayHeader("registration-receipt-denied"),
            Guid.Parse(fixture.Configuration.RegistrationId),
            new OwnerId(PumpFixture.LocalOwner),
            new IslandId(PumpFixture.LocalIsland),
            false,
            1,
            "registration-not-active");
        var delivery = fixture.SealRelay(receipt);
        fixture.Transport.Inbound.Enqueue(delivery);

        await pump.ProcessOnceAsync();

        Assert.False(handlerCalled);
        Assert.DoesNotContain(fixture.Transport.Acknowledged, item => item.EnvelopeId == delivery.EnvelopeId);
    }

    [Fact]
    public async Task DeregistrationRetainsMailboxIntentUntilMatchingAuthenticatedReceipt()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        await using var pump = fixture.CreatePump();
        var completed = false;
        pump.ConfigureLifecycleHandlers(
            static _ => new(false, "unused", 1),
            (receipt, pending, _) =>
            {
                completed = true;
                Assert.Equal(pending.DeregistrationId, receipt.DeregistrationId);
                fixture.Configuration.RegistrationState = DadAutoPartyRegistrationState.Unregistered;
                return ValueTask.FromResult(new DadAutoPartyPrivacyResult(true, false, "dad-deregistered"));
            });

        var queued = pump.BeginDeregistration(false);
        Assert.True(queued.Allowed, queued.SafeCode);
        var pending = Assert.IsType<DadAutoPartyPendingDeregistration>(fixture.PendingStore.LoadDeregistration());
        await pump.ProcessOnceAsync();
        Assert.False(completed);
        Assert.NotNull(fixture.PendingStore.LoadDeregistration());
        Assert.Contains(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DeregistrationRequest>());

        var receipt = new DeregistrationReceipt(
            fixture.RelayHeader("deregistration-receipt"),
            pending.DeregistrationId,
            new IslandId(PumpFixture.LocalIsland),
            true,
            2,
            "deregistered");
        var delivery = fixture.SealRelay(receipt);
        fixture.Transport.Inbound.Enqueue(delivery);
        await pump.ProcessOnceAsync();

        Assert.True(completed);
        Assert.Null(fixture.PendingStore.LoadDeregistration());
        Assert.Contains(fixture.Transport.Acknowledged, item => item.EnvelopeId == delivery.EnvelopeId);
    }

    [Fact]
    public async Task ParticipantCommandLeaseReleasesDeniedSendAndAcknowledgesAcceptedRetry()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        var bridge = fixture.Bridge;
        fixture.Configuration.RemoteBindings.Add(new DadAutoPartyRemoteBinding
        {
            FleetRowId = "row-remote",
            OpaqueCharacterId = "opaque-remote",
            OwnerId = PumpFixture.PeerOwner,
            IslandId = PumpFixture.PeerIsland,
            RequestedJobId = "19",
            OwnsQueueAuthority = true,
            OwnerConsentConfirmed = true,
        });
        var runtime = RemoteRuntime();
        Assert.True(bridge.TryBindRun(runtime.Plan, runtime.Manifest, DateTimeOffset.UtcNow, out var blocker), blocker);
        await using var pump = fixture.CreatePump();
        pump.ConfigureLifecycleHandlers(
            static _ => new(false, "unused", 1),
            static (_, _, _) => ValueTask.FromResult(new DadAutoPartyPrivacyResult(false, false, "unused")));

        fixture.Transport.SendAcceptance.Enqueue(false);
        await pump.ProcessOnceAsync();
        Assert.Equal(1, bridge.PendingCommandCount);

        fixture.Transport.SendAcceptance.Enqueue(true);
        await pump.ProcessOnceAsync();
        Assert.Equal(0, bridge.PendingCommandCount);
        var envelope = fixture.Transport.Sent.Last(item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<RunProposal>());
        var proposal = fixture.Open<RunProposal>(envelope);
        var execution = Assert.IsType<EndpointExecutionPlan>(proposal.ExecutionPlan);
        Assert.Equal(runtime.Plan.Request.RequestId, execution.RunId);
        Assert.Single(execution.Participants);
        Assert.Single(execution.Modules);
        Assert.Equal(proposal.ActivityId, execution.Modules[0].ActivityId);
    }


    [Fact]
    public async Task UnpairedDirectRouteRequiresMatchingUnexpiredCentralAttestation()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        await using var pump = fixture.CreatePump();
        var deniedReceipt = new ExecutionReceipt(
            fixture.PeerHeader("unattested-receipt"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new OwnerId(PumpFixture.PeerOwner),
            ExecutionStage.Complete,
            ExecutionOutcome.Completed,
            1,
            "peer-complete");
        var deniedDelivery = fixture.SealPeer(deniedReceipt);
        fixture.Transport.Inbound.Enqueue(deniedDelivery);

        await pump.ProcessOnceAsync();

        Assert.DoesNotContain(fixture.Transport.Acknowledged, item => item.EnvelopeId == deniedDelivery.EnvelopeId);

        var accessId = Guid.NewGuid();
        const string policyHash = "policy-hash-one";
        var access = new RegisteredRequesterAccessRequest(
            fixture.RelayHeader("access-request"),
            accessId,
            new IslandId(PumpFixture.LocalIsland),
            ImmutableArray.Create(new OpaqueCharacterId("opaque-one")),
            policyHash);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(access));
        await pump.ProcessOnceAsync();

        var attestationHeader = fixture.RelayHeader("requester-attestation");
        var fingerprint = DadAutoPartyIdentityPackageService.BuildFingerprint(
            PumpFixture.PeerOwner,
            PumpFixture.PeerIsland,
            3,
            fixture.PeerSigningPublic,
            fixture.PeerAgreementPublic);
        var attestation = new RegisteredRequesterAttestation(
            attestationHeader,
            accessId,
            new OwnerId(PumpFixture.PeerOwner),
            new IslandId(PumpFixture.PeerIsland),
            new IslandId(PumpFixture.LocalIsland),
            new OwnerId(PumpFixture.LocalOwner),
            "guild-home",
            new EndpointPublicKeys(
                3,
                "peer-signing-3",
                ImmutableArray.CreateRange(fixture.PeerSigningPublic),
                "peer-agreement-3",
                ImmutableArray.CreateRange(fixture.PeerAgreementPublic)),
            fingerprint,
            new EndpointPublicKeys(
                1,
                "local-signing-1",
                ImmutableArray.CreateRange(fixture.LocalSigningPublic),
                "local-agreement-1",
                ImmutableArray.CreateRange(fixture.LocalAgreementPublic)),
            fixture.Configuration.RegistrationFingerprint,
            policyHash,
            access.Header.ExpiresAt - TimeSpan.FromSeconds(1));
        var attestationDelivery = fixture.SealRelay(attestation);
        fixture.Transport.Inbound.Enqueue(attestationDelivery);
        await pump.ProcessOnceAsync();

        var acceptedReceipt = deniedReceipt with
        {
            Header = fixture.PeerHeader("attested-receipt"),
            ReceiptId = Guid.NewGuid(),
        };
        var acceptedDelivery = fixture.SealPeer(acceptedReceipt);
        fixture.Transport.Inbound.Enqueue(acceptedDelivery);
        await pump.ProcessOnceAsync();

        Assert.Contains(fixture.Transport.Acknowledged, item => item.EnvelopeId == attestationDelivery.EnvelopeId);
        Assert.Contains(fixture.Transport.Acknowledged, item => item.EnvelopeId == acceptedDelivery.EnvelopeId);
        var route = Assert.Single(pump.GetTransientRoutes());
        Assert.Equal(PumpFixture.PeerOwner, route.RequesterOwnerId);
        Assert.Equal(PumpFixture.LocalOwner, route.SharingOwnerId);
        Assert.Equal(policyHash, route.PolicyHash);

        Assert.True(pump.Deauthenticate(PumpFixture.PeerIsland, "dad-owner-deauthenticated").Allowed);
        Assert.Empty(pump.GetTransientRoutes());
    }

    [Fact]
    public async Task DirectoryProjectsCommunityMetadataAndRequesterRouteNeedsExactAttestation()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        await using var pump = fixture.CreatePump();
        const string policyHash = "policy-hash-requester";
        Assert.True((await pump.RequestDirectoryAsync(string.Empty, true)).Allowed);
        await pump.ProcessOnceAsync();
        var directoryQueryEnvelope = Assert.Single(
            fixture.Transport.Sent,
            item => item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>());
        var query = fixture.Open<DirectoryQuery>(directoryQueryEnvelope);
        var listingExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var page = new DirectoryPage(
            fixture.RelayHeader("directory-page"),
            query.QueryId,
            1,
            false,
            string.Empty,
            ImmutableArray.Create(new PrivateDirectoryEntry(
                new OwnerId(PumpFixture.PeerOwner),
                new IslandId(PumpFixture.PeerIsland),
                "peer-endpoint",
                "guild-home",
                CharacterShareMode.CharacterList,
                policyHash,
                true,
                ImmutableArray.Create(new PrivateCharacterListing(
                    new OpaqueCharacterId("opaque-one"),
                    "Peer character",
                    ImmutableArray.Create(new JobId("19")),
                    ImmutableArray.Create(new ActivityId("dad-duty-1")),
                    true,
                    1,
                    listingExpiresAt)),
                1,
                listingExpiresAt)),
            1);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(page));

        await pump.ProcessOnceAsync();

        var listing = Assert.Single(fixture.Configuration.Listings);
        Assert.Equal(PumpFixture.PeerOwner, listing.OwnerId);
        Assert.Equal(DadAutoPartyCharacterShareMode.CharacterList, listing.EffectiveShareMode);
        Assert.Equal(policyHash, listing.EffectivePolicyHash);
        Assert.False(listing.HasCurrentTransientRoute);
        Assert.False(pump.IsListingRouteCurrent(listing));

        Assert.True(pump.RequestPromiscuousAccess(
            PumpFixture.PeerIsland,
            [listing.OpaqueCharacterId],
            policyHash).Allowed);
        await pump.ProcessOnceAsync();
        var accessEnvelope = fixture.Transport.Sent.Last(item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<RegisteredRequesterAccessRequest>());
        var access = fixture.Open<RegisteredRequesterAccessRequest>(accessEnvelope);
        var attestationHeader = fixture.RelayHeader("requester-side-attestation");
        var peerFingerprint = DadAutoPartyIdentityPackageService.BuildFingerprint(
            PumpFixture.PeerOwner,
            PumpFixture.PeerIsland,
            3,
            fixture.PeerSigningPublic,
            fixture.PeerAgreementPublic);
        var attestation = new RegisteredRequesterAttestation(
            attestationHeader,
            access.AccessRequestId,
            new OwnerId(PumpFixture.LocalOwner),
            new IslandId(PumpFixture.LocalIsland),
            new IslandId(PumpFixture.PeerIsland),
            new OwnerId(PumpFixture.PeerOwner),
            "guild-home",
            new EndpointPublicKeys(
                1,
                "local-signing-1",
                ImmutableArray.CreateRange(fixture.LocalSigningPublic),
                "local-agreement-1",
                ImmutableArray.CreateRange(fixture.LocalAgreementPublic)),
            fixture.Configuration.RegistrationFingerprint,
            new EndpointPublicKeys(
                3,
                "peer-signing-3",
                ImmutableArray.CreateRange(fixture.PeerSigningPublic),
                "peer-agreement-3",
                ImmutableArray.CreateRange(fixture.PeerAgreementPublic)),
            peerFingerprint,
            policyHash,
            access.Header.ExpiresAt - TimeSpan.FromSeconds(1));
        var mismatch = attestation with
        {
            Header = fixture.RelayHeader("requester-side-attestation-mismatch"),
            RequestedPolicyHash = "wrong-policy-hash",
        };
        var mismatchDelivery = fixture.SealRelay(mismatch);
        fixture.Transport.Inbound.Enqueue(mismatchDelivery);
        await pump.ProcessOnceAsync();
        Assert.DoesNotContain(fixture.Transport.Acknowledged, item => item.EnvelopeId == mismatchDelivery.EnvelopeId);

        var attestationDelivery = fixture.SealRelay(attestation);
        fixture.Transport.Inbound.Enqueue(attestationDelivery);
        await pump.ProcessOnceAsync();

        Assert.Contains(fixture.Transport.Acknowledged, item => item.EnvelopeId == attestationDelivery.EnvelopeId);
        Assert.True(listing.HasCurrentTransientRoute);
        Assert.True(pump.IsListingRouteCurrent(listing));
        var route = Assert.Single(pump.GetTransientRoutes());
        Assert.Equal(PumpFixture.LocalOwner, route.RequesterOwnerId);
        Assert.Equal(PumpFixture.PeerOwner, route.SharingOwnerId);

        Assert.True(pump.Deauthenticate(PumpFixture.PeerIsland, "dad-owner-deauthenticated").Allowed);
        Assert.Empty(pump.GetTransientRoutes());
        Assert.False(listing.HasCurrentTransientRoute);
        Assert.False(pump.IsListingRouteCurrent(listing));
    }

    [Fact]
    public async Task OfflinePairedDirectoryEntryClearsListingsAndPresenceResetsOnRestart()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        Assert.True((await pump.RequestDirectoryAsync(string.Empty, false)).Allowed);
        await pump.ProcessOnceAsync();
        var firstQuery = fixture.Open<DirectoryQuery>(fixture.Transport.Sent.Last(item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>()));
        var listingExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(BuildDirectoryPage(firstQuery.QueryId, true, listingExpiresAt)));

        await pump.ProcessOnceAsync();

        var online = fixture.Service.GetDirectorySnapshot();
        Assert.Contains(PumpFixture.PeerIsland, online.OnlineIslandIds);
        Assert.Single(online.Listings);
        using (var restarted = new DadAutoPartyService(
                   fixture.Configuration,
                   fixture.IdentityStore,
                   static () => true,
                   static () => { }))
        {
            Assert.True(fixture.Configuration.IsRegistrationActive);
            Assert.True(Assert.Single(fixture.Configuration.Pairings).IsActive);
            Assert.DoesNotContain(PumpFixture.PeerIsland, restarted.GetDirectorySnapshot().OnlineIslandIds);
            Assert.Empty(restarted.GetDirectorySnapshot().Listings);
        }

        Assert.True((await pump.RequestDirectoryAsync(string.Empty, false)).Allowed);
        await pump.ProcessOnceAsync();
        var secondQuery = fixture.Open<DirectoryQuery>(fixture.Transport.Sent.Last(item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>()));
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(BuildDirectoryPage(secondQuery.QueryId, false, listingExpiresAt)));

        await pump.ProcessOnceAsync();

        var offline = fixture.Service.GetDirectorySnapshot();
        Assert.DoesNotContain(PumpFixture.PeerIsland, offline.OnlineIslandIds);
        Assert.Empty(offline.Listings);
        Assert.DoesNotContain(fixture.Configuration.Listings, item =>
            item.SharingIslandId == PumpFixture.PeerIsland);
        Assert.True(Assert.Single(fixture.Configuration.Pairings).IsActive);

        DirectoryPage BuildDirectoryPage(Guid queryId, bool online, DateTimeOffset expiresAt) => new(
            fixture.RelayHeader($"directory-peer-{online}"),
            queryId,
            1,
            false,
            string.Empty,
            ImmutableArray.Create(new PrivateDirectoryEntry(
                new OwnerId(PumpFixture.PeerOwner),
                new IslandId(PumpFixture.PeerIsland),
                "peer-endpoint",
                "guild-home",
                CharacterShareMode.AllCharactersForPeer,
                "paired-policy-hash",
                online,
                online
                    ? ImmutableArray.Create(new PrivateCharacterListing(
                        new OpaqueCharacterId("opaque-peer"),
                        "Peer character",
                        ImmutableArray.Create(new JobId("19")),
                        ImmutableArray.Create(new ActivityId("dad-duty-1")),
                        true,
                        1,
                        expiresAt))
                    : ImmutableArray<PrivateCharacterListing>.Empty,
                1,
                expiresAt)),
            1);
    }


    [Fact]
    public async Task PrivateListingsAreSealedForTheCentralRelay()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        fixture.Configuration.DirectoryGeneration = 7;
        fixture.Configuration.StateGeneration = 29;
        await using var pump = fixture.CreatePump();
        await pump.ProcessOnceAsync();
        var policy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.SpecificCharacter,
            CharacterHandles = ["opaque-local"],
            Enabled = true,
            Revision = 2,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        var listing = new DadAutoPartyListing
        {
            ListingId = Guid.NewGuid().ToString("D"),
            OwnerId = PumpFixture.LocalOwner,
            SharingIslandId = PumpFixture.LocalIsland,
            OpaqueCharacterId = "opaque-local",
            DisplayLabel = "Shared character 1234abcd",
            AllowedJobIds = ["19"],
            AllowedActivityIds = [DadAutoPartyFreeformRules.FormationActivityId],
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
        };

        var queued = pump.QueueListingUpdate(policy, [listing]);
        await pump.ProcessOnceAsync();

        Assert.True(queued.Allowed, queued.SafeCode);
        var envelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<PrivateListingUpdate>());
        Assert.Equal(DadAutoPartyIdentityPackageService.RegistrationRecipient, envelope.RecipientIslandId.Value);
        var update = fixture.Open<PrivateListingUpdate>(envelope);
        Assert.Equal(PumpFixture.LocalIsland, update.SharingIslandId.Value);
        Assert.Equal(fixture.Configuration.DirectoryGeneration, update.DirectoryGeneration);
        Assert.NotEqual(fixture.Configuration.StateGeneration, update.DirectoryGeneration);
        Assert.Equal(CharacterShareMode.SpecificCharacter, update.SharePolicy.Mode);
        Assert.Equal("opaque-local", Assert.Single(update.Listings).CharacterHandle.Value);
    }

    [Fact]
    public async Task DisabledEmptyCharacterListPublishesWireValidEmptyPolicyWithoutChangingSavedSelection()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        var savedPolicy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.CharacterList,
            CharacterHandles = [],
            Enabled = false,
            Revision = 4,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        fixture.Configuration.StandingSharePolicy = savedPolicy;
        var listing = new DadAutoPartyListing
        {
            ListingId = Guid.NewGuid().ToString("D"),
            OwnerId = PumpFixture.LocalOwner,
            SharingIslandId = PumpFixture.LocalIsland,
            OpaqueCharacterId = "opaque-local",
            DisplayLabel = "Shared character 1234abcd",
            AllowedJobIds = ["19"],
            AllowedActivityIds = [DadAutoPartyFreeformRules.FormationActivityId],
            Available = true,
            Revision = 1,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
        };
        await using var pump = fixture.CreatePump();

        var queued = pump.QueueListingUpdate(savedPolicy, [listing]);
        await pump.ProcessOnceAsync();

        Assert.True(queued.Allowed, queued.SafeCode);
        Assert.Same(savedPolicy, fixture.Configuration.StandingSharePolicy);
        Assert.Equal(DadAutoPartyCharacterShareMode.CharacterList, savedPolicy.Mode);
        Assert.False(savedPolicy.Enabled);
        Assert.Empty(savedPolicy.CharacterHandles);
        var envelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<PrivateListingUpdate>());
        var update = fixture.Open<PrivateListingUpdate>(envelope);
        Assert.Equal(fixture.Configuration.DirectoryGeneration, update.DirectoryGeneration);
        Assert.Equal(CharacterShareMode.AllCharactersForPeer, update.SharePolicy.Mode);
        Assert.False(update.SharePolicy.Enabled);
        Assert.Empty(update.SharePolicy.CharacterHandles);
        Assert.Equal("opaque-local", Assert.Single(update.Listings).CharacterHandle.Value);
    }

    [Fact]
    public async Task InboundProposalRetainsExactLocalRouteAndEmitsTruthfulBlockedPreflight()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        var listing = new DadAutoPartyListing
        {
            ListingId = Guid.NewGuid().ToString("D"),
            OwnerId = PumpFixture.LocalOwner,
            SharingIslandId = PumpFixture.LocalIsland,
            OpaqueCharacterId = "opaque-local",
            DisplayLabel = "Shared local character",
            AllowedJobIds = ["19"],
            AllowedActivityIds = ["dad-duty-1"],
            Available = true,
            Revision = 1,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
        };
        var policy = Assert.Single(fixture.Configuration.Pairings).LocalSharePolicy.Clone();
        var diagnostics = new List<string>();
        await using var pump = fixture.CreatePump(
            _ => new DadAutoPartyListingPublication(policy, [listing]),
            diagnostic: diagnostics.Add);
        var proposalId = Guid.NewGuid();
        var proposal = new RunProposal(
            fixture.PeerHeader("inbound-run-proposal"),
            proposalId,
            new OwnerId(PumpFixture.PeerOwner),
            new ActivityId("dad-duty-1"),
            [
                new ParticipantRequest(
                    new OwnerId(PumpFixture.PeerOwner),
                    new IslandId(PumpFixture.PeerIsland),
                    new OpaqueCharacterId("opaque-peer"),
                    new JobId("24")),
                new ParticipantRequest(
                    new OwnerId(PumpFixture.LocalOwner),
                    new IslandId(PumpFixture.LocalIsland),
                    new OpaqueCharacterId("opaque-local"),
                    new JobId("19")),
            ],
            "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            new EndpointExecutionPlan(
                "run-inbound",
                FormationOnly: false,
                RequirePostArReady: true,
                ParticipantReadyTimeoutSeconds: 120,
                AssemblyTimeoutSeconds: 90,
                LeaseDurationSeconds: 300,
                RepairPolicy: new EndpointRepairPolicy(false, 75, "self"),
                Participants:
                [
                    new EndpointExecutionParticipant(
                        "slot-1",
                        new OwnerId(PumpFixture.PeerOwner),
                        new IslandId(PumpFixture.PeerIsland),
                        new OpaqueCharacterId("opaque-peer"),
                        new JobId("24"),
                        EndpointExecutionRole.QueueLeader,
                        IsInviter: true),
                    new EndpointExecutionParticipant(
                        "slot-2",
                        new OwnerId(PumpFixture.LocalOwner),
                        new IslandId(PumpFixture.LocalIsland),
                        new OpaqueCharacterId("opaque-local"),
                        new JobId("19"),
                        EndpointExecutionRole.Participant,
                        IsInviter: false),
                ],
                Modules:
                [
                    new EndpointExecutionModule(
                        0,
                        "premade-duty",
                        new ActivityId("dad-duty-1"),
                        "Fixture Duty",
                        "duty-finder-duty",
                        1,
                        0,
                        Unsynced: false,
                        ExpectedPartySize: 2),
                ]));
        var delivery = fixture.SealPeer(proposal);
        fixture.Transport.Inbound.Enqueue(delivery);

        await pump.ProcessOnceAsync();
        pump.UpdateFramework();
        await pump.ProcessOnceAsync();

        Assert.Contains(fixture.Transport.Acknowledged, item => item.EnvelopeId == delivery.EnvelopeId);
        Assert.True(
            fixture.Transport.Sent.Count > 0,
            $"{pump.Snapshot.SafeCode}|{string.Join(',', diagnostics)}");
        var reservationEnvelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<Reservation>());
        var reservation = fixture.Open<Reservation>(reservationEnvelope);
        Assert.Equal(proposalId, reservation.ProposalId);
        Assert.Equal(PumpFixture.LocalOwner, reservation.OwnerId.Value);
        Assert.Equal("opaque-local", reservation.CharacterId.Value);
        Assert.True(reservation.ObservedStateGeneration >= reservation.ExpectedStateGeneration);
        var preflightEnvelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<PreflightResult>());
        var preflight = fixture.Open<PreflightResult>(preflightEnvelope);
        Assert.False(preflight.Ready);
        Assert.Equal("dad-inbound-execution-admission-not-wired", Assert.Single(preflight.SafeBlockers));
        Assert.Equal(reservation.ObservedStateGeneration, preflight.ExpectedStateGeneration);
        Assert.Equal(preflight.ExpectedStateGeneration, preflight.ObservedStateGeneration);
        Assert.DoesNotContain(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<SessionLease>());
    }

    [Fact]
    public async Task ReadyInboundAdmissionRetainsExactRuntimeTargetOnlyInMemory()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        var listing = new DadAutoPartyListing
        {
            ListingId = Guid.NewGuid().ToString("D"),
            OwnerId = PumpFixture.LocalOwner,
            SharingIslandId = PumpFixture.LocalIsland,
            OpaqueCharacterId = "opaque-local",
            DisplayLabel = "Shared local character",
            AllowedJobIds = ["19"],
            AllowedActivityIds = ["dad-duty-1"],
            Available = true,
            Revision = 1,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
        };
        var policy = Assert.Single(fixture.Configuration.Pairings).LocalSharePolicy.Clone();
        var proposalId = Guid.NewGuid();
        var runId = $"run-{Guid.NewGuid():N}";
        var proposal = PeerProposalForLocalParticipant(fixture, proposalId, runId);
        var expectedTarget = NativeInviteTarget(runId, "Slot2", "Private Local", 1001);
        await using var pump = fixture.CreatePump(
            _ => new DadAutoPartyListingPublication(policy, [listing]),
            inboundAdmission: _ => new DadAutoPartyInboundAdmissionResult(
                runId,
                true,
                "dad-inbound-admission-ready",
                ["Slot2"],
                [expectedTarget]));
        var delivery = fixture.SealPeer(proposal);
        fixture.Transport.Inbound.Enqueue(delivery);

        await pump.ProcessOnceAsync();
        pump.UpdateFramework();
        await pump.ProcessOnceAsync();

        Assert.True(pump.TryGetInboundRuntimeTarget(
            proposalId,
            new OpaqueCharacterId("opaque-local"),
            out var slotId,
            out var retainedTarget,
            out var safeCode), safeCode);
        Assert.Equal("Slot2", slotId);
        AssertNativeInviteTarget(expectedTarget, retainedTarget);
        Assert.Contains(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<SessionLease>());
        Assert.Contains(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<ParticipantInviteLocator>());
        Assert.Equal(0, fixture.PendingStore.SaveCount);
    }

    [Fact]
    public async Task AllianceInstructionWaitsForInitializedRelaySecurity()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        var instruction = AllianceInstruction(
            PumpFixture.PeerIsland,
            PumpFixture.PeerOwner,
            "opaque-peer");

        var early = pump.QueueAllianceInstruction(instruction);

        Assert.False(early.Sent);
        Assert.Equal("dad-alliance-central-not-ready", early.SafeCode);
        await pump.ProcessOnceAsync();
        Assert.DoesNotContain(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<AllianceRecruitmentOperation>());

        var ready = pump.QueueAllianceInstruction(instruction);
        Assert.True(ready.Sent, ready.SafeCode);
    }

    [Fact]
    public async Task AllianceInstructionIsSealedAndRoutedToRegisteredIsland()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        await pump.ProcessOnceAsync();
        var instruction = AllianceInstruction(
            PumpFixture.PeerIsland,
            PumpFixture.PeerOwner,
            "opaque-peer");

        var queued = pump.QueueAllianceInstruction(instruction);
        await pump.ProcessOnceAsync();

        Assert.True(queued.Sent, queued.SafeCode);
        var envelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<AllianceRecruitmentOperation>());
        Assert.Equal(queued.MessageId, envelope.EnvelopeId);
        Assert.Equal(PumpFixture.LocalIsland, envelope.SenderIslandId.Value);
        Assert.Equal(PumpFixture.PeerIsland, envelope.RecipientIslandId.Value);
        var operation = fixture.Open<AllianceRecruitmentOperation>(envelope);
        Assert.Equal(AllianceRecruitmentOperationKind.Recruit, operation.Kind);
        Assert.Equal(Guid.Parse(instruction.RecruitmentId), operation.RecruitmentId);
        Assert.Equal(PumpFixture.PeerOwner, operation.TargetOwnerId.Value);
        Assert.Equal("opaque-peer", operation.TargetCharacterId.Value);
    }

    [Fact]
    public async Task AllianceRecruitmentDispatchesAndCreatesCorrelatedReceipt()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        DadAllianceCentralOperationContext? observed = null;
        pump.ConfigureAllianceHandlers(context => observed = context, static _ => { });
        var instruction = AllianceInstruction(
            PumpFixture.LocalIsland,
            PumpFixture.LocalOwner,
            "opaque-local");
        var operationId = Guid.NewGuid();
        var operation = DadAllianceAutoPartyContractMapping.ToRecruitOperation(
            instruction,
            fixture.PeerAllianceHeader("alliance-recruit"),
            operationId);
        var delivery = fixture.SealPeer(operation);
        fixture.Transport.Inbound.Enqueue(delivery);

        await pump.ProcessOnceAsync();

        Assert.NotNull(observed);
        Assert.Equal(operationId, observed!.OperationId);
        Assert.Equal(PumpFixture.PeerIsland, observed.SenderIslandId);
        Assert.Equal(
            "opaque-local",
            Assert.IsType<DadAllianceRecruitmentInstructionDto>(observed.Instruction).TargetOpaqueCharacterId);
        Assert.Null(observed.Cancellation);
        Assert.Contains(fixture.Transport.Acknowledged, item => item.EnvelopeId == delivery.EnvelopeId);

        var result = AllianceResult(instruction, PumpFixture.LocalOwner, "opaque-local");
        var receiptDecision = pump.QueueAllianceReceipt(operationId, result);
        Assert.True(receiptDecision.Allowed, receiptDecision.SafeCode);
        Assert.False(pump.QueueAllianceReceipt(operationId, result).Allowed);
        await pump.ProcessOnceAsync();

        var receiptEnvelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<AllianceRecruitmentReceipt>());
        var receipt = fixture.Open<AllianceRecruitmentReceipt>(receiptEnvelope);
        Assert.Equal(operationId, receipt.OperationId);
        Assert.Equal(PumpFixture.PeerIsland, receipt.Header.RecipientIslandId.Value);
        Assert.Equal(PumpFixture.LocalOwner, receipt.ParticipantOwnerId.Value);
        Assert.Equal("opaque-local", receipt.TargetCharacterId.Value);
    }

    [Fact]
    public async Task AllianceReceiptRequiresExactCorrelationAndRejectsReplay()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        await pump.ProcessOnceAsync();
        var received = new List<DadAllianceCentralReceiptContext>();
        pump.ConfigureAllianceHandlers(static _ => { }, received.Add);
        var instruction = AllianceInstruction(
            PumpFixture.PeerIsland,
            PumpFixture.PeerOwner,
            "opaque-peer");
        var queued = pump.QueueAllianceInstruction(instruction);
        Assert.True(queued.Sent, queued.SafeCode);
        await pump.ProcessOnceAsync();
        var sentOperation = fixture.Open<AllianceRecruitmentOperation>(fixture.Transport.Sent.Single(item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<AllianceRecruitmentOperation>()));

        var mismatchResult = AllianceResult(instruction, PumpFixture.PeerOwner, "opaque-wrong");
        var mismatchReceipt = DadAllianceAutoPartyContractMapping.ToReceipt(
            mismatchResult,
            fixture.PeerAllianceHeader("alliance-receipt-mismatch"),
            sentOperation.OperationId);
        var mismatchDelivery = fixture.SealPeer(mismatchReceipt);
        fixture.Transport.Inbound.Enqueue(mismatchDelivery);
        await pump.ProcessOnceAsync();
        Assert.Empty(received);
        Assert.DoesNotContain(fixture.Transport.Acknowledged, item => item.EnvelopeId == mismatchDelivery.EnvelopeId);

        var result = AllianceResult(instruction, PumpFixture.PeerOwner, "opaque-peer");
        var receipt = DadAllianceAutoPartyContractMapping.ToReceipt(
            result,
            fixture.PeerAllianceHeader("alliance-receipt"),
            sentOperation.OperationId);
        var delivery = fixture.SealPeer(receipt);
        fixture.Transport.Inbound.Enqueue(delivery);
        await pump.ProcessOnceAsync();
        Assert.True(received.Count == 1, pump.Snapshot.SafeCode);
        var applied = Assert.Single(received);
        Assert.Equal(sentOperation.OperationId, applied.OperationId);
        Assert.Null(applied.Cancellation);
        Assert.Contains(fixture.Transport.Acknowledged, item => item.EnvelopeId == delivery.EnvelopeId);

        fixture.Transport.Inbound.Enqueue(delivery);
        await pump.ProcessOnceAsync();
        Assert.Single(received);
        Assert.Single(fixture.Transport.Acknowledged, item => item.EnvelopeId == delivery.EnvelopeId);
    }

    [Fact]
    public async Task AllianceRecruitmentDeniesOpaqueHandleOutsideSharingPolicy()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        var pairing = Assert.Single(fixture.Configuration.Pairings);
        pairing.LocalSharePolicy.Mode = DadAutoPartyCharacterShareMode.SpecificCharacter;
        pairing.LocalSharePolicy.CharacterHandles = ["opaque-allowed"];
        await using var pump = fixture.CreatePump();
        var handlerCalled = false;
        pump.ConfigureAllianceHandlers(_ => handlerCalled = true, static _ => { });
        var instruction = AllianceInstruction(
            PumpFixture.LocalIsland,
            PumpFixture.LocalOwner,
            "opaque-denied");
        var operation = DadAllianceAutoPartyContractMapping.ToRecruitOperation(
            instruction,
            fixture.PeerAllianceHeader("alliance-policy-denied"),
            Guid.NewGuid());
        var delivery = fixture.SealPeer(operation);
        fixture.Transport.Inbound.Enqueue(delivery);

        await pump.ProcessOnceAsync();

        Assert.False(handlerCalled);
        Assert.DoesNotContain(fixture.Transport.Acknowledged, item => item.EnvelopeId == delivery.EnvelopeId);
        Assert.Equal("dad-alliance-central-route-denied", pump.Snapshot.SafeCode);
    }

    [Fact]
    public async Task AllianceCancellationRoundTripsWithOriginalInstructionCorrelation()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        await pump.ProcessOnceAsync();
        DadAllianceCentralReceiptContext? observed = null;
        pump.ConfigureAllianceHandlers(static _ => { }, context => observed = context);
        var instruction = AllianceInstruction(
            PumpFixture.PeerIsland,
            PumpFixture.PeerOwner,
            "opaque-peer");
        var cancellation = new DadAllianceRecruitmentCancellationDto
        {
            RecruitmentId = instruction.RecruitmentId,
            TargetIslandId = instruction.TargetIslandId,
            TargetOwnerId = instruction.TargetOwnerId,
            TargetOpaqueCharacterId = instruction.TargetOpaqueCharacterId,
            StopGeneration = instruction.StopGeneration + 1,
            RequestedAtUtc = DateTime.UtcNow,
            Reason = "dad-owner-stop",
        };

        var queued = pump.QueueAllianceCancellation(cancellation, instruction);
        Assert.True(queued.Sent, queued.SafeCode);
        await pump.ProcessOnceAsync();
        var envelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<AllianceRecruitmentOperation>());
        var operation = fixture.Open<AllianceRecruitmentOperation>(envelope);
        Assert.Equal(AllianceRecruitmentOperationKind.Cancel, operation.Kind);
        Assert.Equal(cancellation.StopGeneration, operation.StopGeneration);
        Assert.Equal("dad-owner-stop", operation.SafeCode);

        var result = AllianceResult(instruction, PumpFixture.PeerOwner, "opaque-peer");
        result.StopGeneration = cancellation.StopGeneration;
        result.State = DadAllianceRecruitmentState.Stopped;
        result.ResultKind = DadAllianceRecruitmentResultKind.Stopped;
        var receipt = DadAllianceAutoPartyContractMapping.ToReceipt(
            result,
            fixture.PeerAllianceHeader("alliance-cancel-receipt"),
            operation.OperationId);
        var delivery = fixture.SealPeer(receipt);
        fixture.Transport.Inbound.Enqueue(delivery);
        await pump.ProcessOnceAsync();

        Assert.True(observed != null, pump.Snapshot.SafeCode);
        Assert.Equal(operation.OperationId, observed!.OperationId);
        Assert.Equal(
            cancellation.StopGeneration,
            Assert.IsType<DadAllianceRecruitmentCancellationDto>(observed.Cancellation).StopGeneration);
        Assert.Equal(instruction.TargetOpaqueCharacterId, observed.Instruction.TargetOpaqueCharacterId);
        Assert.Contains(fixture.Transport.Acknowledged, item => item.EnvelopeId == delivery.EnvelopeId);
    }

    [Fact]
    public async Task DeauthenticationVetoesLocallyBeforeSignedPropagation()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        pump.ConfigureLifecycleHandlers(
            static _ => new(false, "unused", 1),
            static (_, _, _) => ValueTask.FromResult(new DadAutoPartyPrivacyResult(false, false, "unused")));
        Assert.True(fixture.Service.SetPairingAlias(PumpFixture.PeerIsland, "Temporary_DAD").Allowed);
        Assert.True(fixture.Service.SetPairingAlias(PumpFixture.PeerIsland, string.Empty).Allowed);
        Assert.False(fixture.Configuration.PairedDadAliases.ContainsKey(PumpFixture.PeerIsland));
        Assert.True(fixture.Service.SetPairingAlias(PumpFixture.PeerIsland, "Stable_DAD").Allowed);

        var decision = pump.Deauthenticate(PumpFixture.PeerIsland, "dad-owner-deauthenticated");

        Assert.True(decision.Allowed, decision.SafeCode);
        var pairing = Assert.Single(fixture.Configuration.Pairings);
        Assert.NotNull(pairing.RevokedAtUtc);
        Assert.Contains(fixture.Configuration.Deauthentications, item =>
            item.PeerIslandId == PumpFixture.PeerIsland);
        Assert.Equal("Stable_DAD", fixture.Configuration.PairedDadAliases[PumpFixture.PeerIsland]);
        Assert.True(fixture.Bridge.PendingCommandCount > 0);

        await pump.ProcessOnceAsync();

        Assert.Contains(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DeauthenticationNotice>());
        Assert.Contains(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<Revocation>());
    }

    [Fact]
    public async Task ParticipantInviteLocatorIsSealedToProposalRequesterWithoutPendingStorePersistence()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        await pump.ProcessOnceAsync();
        fixture.Transport.Sent.Clear();
        var proposalId = Guid.NewGuid();
        var runId = $"run-{Guid.NewGuid():N}";
        var proposal = PeerProposalForLocalParticipant(fixture, proposalId, runId);
        var target = NativeInviteTarget(runId, "Slot2", "Private Local", 1001);

        var decision = pump.QueueParticipantInviteLocator(
            proposal,
            new OpaqueCharacterId("opaque-local"),
            target,
            observedStateGeneration: 3);
        await pump.ProcessOnceAsync();

        Assert.True(decision.Allowed, decision.SafeCode);
        Assert.Equal(0, fixture.PendingStore.SaveCount);
        var envelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<ParticipantInviteLocator>());
        Assert.Equal(PumpFixture.PeerIsland, envelope.RecipientIslandId.Value);
        var locator = fixture.Open<ParticipantInviteLocator>(envelope);
        Assert.Equal(proposalId, locator.ProposalId);
        Assert.Equal(PumpFixture.LocalOwner, locator.OwnerId.Value);
        Assert.Equal("opaque-local", locator.CharacterId.Value);
        Assert.Equal(PumpFixture.LocalIsland, locator.Locator.IslandId.Value);
        var encoded = locator.Locator.OpaqueLocator.ToArray();
        try
        {
            using var payload = JsonDocument.Parse(encoded);
            Assert.Equal(runId, payload.RootElement.GetProperty("RunId").GetString());
            Assert.Equal("Slot2", payload.RootElement.GetProperty("SlotId").GetString());
            Assert.Equal(nameof(DadModuleId.PremadeDuty), payload.RootElement.GetProperty("ModuleId").GetString());
            Assert.Equal("Private Local", payload.RootElement.GetProperty("CharacterName").GetString());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    [Fact]
    public async Task FormationOnlyParticipantInviteLocatorDoesNotRequireExecutionModules()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        await pump.ProcessOnceAsync();
        fixture.Transport.Sent.Clear();
        var proposal = PeerProposalForLocalParticipant(
            fixture,
            Guid.NewGuid(),
            $"run-{Guid.NewGuid():N}");
        proposal = proposal with
        {
            ExecutionPlan = proposal.ExecutionPlan! with
            {
                FormationOnly = true,
                Modules = ImmutableArray<EndpointExecutionModule>.Empty,
            },
        };

        var decision = pump.QueueParticipantInviteLocator(
            proposal,
            new OpaqueCharacterId("opaque-local"),
            NativeInviteTarget(proposal.ExecutionPlan.RunId, "Slot2", "Private Local", 1001),
            observedStateGeneration: 3);
        await pump.ProcessOnceAsync();

        Assert.True(decision.Allowed, decision.SafeCode);
        Assert.Contains(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<ParticipantInviteLocator>());
    }

    [Fact]
    public async Task ParticipantInviteLocatorDispatchesIntoRequesterBridgeAndRejectsReplay()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        fixture.Configuration.RemoteBindings.Add(new DadAutoPartyRemoteBinding
        {
            FleetRowId = "row-remote",
            OpaqueCharacterId = "opaque-remote",
            OwnerId = PumpFixture.PeerOwner,
            IslandId = PumpFixture.PeerIsland,
            RequestedJobId = "19",
            OwnsQueueAuthority = true,
            OwnerConsentConfirmed = true,
        });
        var runtime = RemoteRuntime();
        var now = DateTimeOffset.UtcNow;
        Assert.True(fixture.Bridge.TryBindRun(runtime.Plan, runtime.Manifest, now, out var blocker), blocker);
        var proposalId = Guid.Parse(runtime.Plan.Orchestration.AutoPartyProposalId);
        await using var pump = fixture.CreatePump();
        var header = fixture.PeerHeader("participant-invite-locator");
        var locator = PeerParticipantInviteLocator(header, proposalId, runtime.Plan.Request.RequestId);
        var delivery = fixture.SealPeer(locator);
        fixture.Transport.Inbound.Enqueue(delivery);

        await pump.ProcessOnceAsync();

        Assert.Contains(fixture.Transport.Acknowledged, item => item.EnvelopeId == delivery.EnvelopeId);
        Assert.True(fixture.Bridge.TryGetInviteTarget(
            proposalId,
            "Slot1",
            DateTimeOffset.UtcNow,
            out var observed,
            out blocker), blocker);
        Assert.Equal("private-worker", observed.WorkerSessionId.Value);
        Assert.Equal("Private Peer", observed.CharacterName);
        Assert.Equal((ulong)2002, observed.ContentId);
        Assert.Equal(0, fixture.PendingStore.SaveCount);

        fixture.Transport.Inbound.Enqueue(delivery);
        await pump.ProcessOnceAsync();

        Assert.Equal(1, fixture.Transport.Acknowledged.Count(item => item.EnvelopeId == delivery.EnvelopeId));
        Assert.Equal("dad-relay-contract-replay", pump.Snapshot.SafeCode);
    }

    [Fact]
    public async Task ParticipantInviteLocatorRejectsMalformedPayloadAndOwnerRouteMismatch()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        var malformedHeader = fixture.PeerHeader("participant-invite-locator-malformed");
        var malformed = new ParticipantInviteLocator(
            malformedHeader,
            Guid.NewGuid(),
            new OwnerId(PumpFixture.PeerOwner),
            new OpaqueCharacterId("opaque-remote"),
            new InviteLocator(
                $"participant-{Guid.NewGuid():N}",
                new OwnerId(PumpFixture.PeerOwner),
                new IslandId(PumpFixture.PeerIsland),
                DateTimeOffset.UtcNow.AddMinutes(2),
                ImmutableArray.Create((byte)0x7B)),
            1);
        var mismatchHeader = fixture.PeerHeader("participant-invite-locator-owner-mismatch");
        var mismatch = PeerParticipantInviteLocator(
            mismatchHeader,
            Guid.NewGuid(),
            $"run-{Guid.NewGuid():N}",
            ownerId: "owner-not-paired");
        var malformedDelivery = fixture.SealPeer(malformed);
        var mismatchDelivery = fixture.SealPeer(mismatch);
        fixture.Transport.Inbound.Enqueue(malformedDelivery);
        fixture.Transport.Inbound.Enqueue(mismatchDelivery);

        await pump.ProcessOnceAsync();

        Assert.DoesNotContain(fixture.Transport.Acknowledged, item =>
            item.EnvelopeId == malformedDelivery.EnvelopeId || item.EnvelopeId == mismatchDelivery.EnvelopeId);
        Assert.Equal("dad-participant-invite-locator-invalid", pump.Snapshot.SafeCode);
        Assert.Equal(0, fixture.PendingStore.SaveCount);
    }

    [Fact]
    public async Task AuthenticatedFormLocatorIsBoundedAndDispatchedOnFrameworkUpdate()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        DadAutoPartyFormExecutionContext? observed = null;
        pump.ConfigureLifecycleHandlers(
            static _ => new(false, "unused", 1),
            static (_, _, _) => ValueTask.FromResult(new DadAutoPartyPrivacyResult(false, false, "unused")));
        pump.ConfigureFormExecutionHandler((context, _) =>
        {
            observed = context;
            return ValueTask.FromResult(new DadAutoPartyExecutionResult(
                context.Operation.OperationId,
                context.Operation.ProposalId,
                context.Operation.Kind,
                ExecutionOutcome.Completed,
                DadRunPhase.GroupReady,
                "dad-form-complete",
                context.Operation.ExpectedStateGeneration));
        });

        var header = fixture.PeerHeader("form-with-inviter");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            RunId = "run-one",
            WorkerSessionId = "peer-worker",
            AccountKey = "peer-account",
            CharacterKey = "peer-character",
            ContentId = 1234UL,
            CharacterName = "Peer Character",
            WorldId = (ushort)21,
        });
        var locatorBytes = ImmutableArray.CreateRange(payload);
        CryptographicOperations.ZeroMemory(payload);
        var operation = new ExecutionOperation(
            header,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new OwnerId(PumpFixture.LocalOwner),
            ExecutionOperationKind.Form,
            new ActivityId("dad-duty-1"),
            new OpaqueCharacterId("opaque-local"),
            new JobId("19"),
            new InviteLocator(
                $"invite-{Guid.NewGuid():N}",
                new OwnerId(PumpFixture.PeerOwner),
                new IslandId(PumpFixture.PeerIsland),
                DateTimeOffset.UtcNow.AddMinutes(2),
                locatorBytes),
            1,
            false,
            ImmutableArray<InviteLocator>.Empty);
        var delivery = fixture.SealPeer(operation);
        fixture.Transport.Inbound.Enqueue(delivery);

        await pump.ProcessOnceAsync();
        pump.UpdateFramework();
        pump.UpdateFramework();
        await pump.ProcessOnceAsync();

        Assert.NotNull(observed);
        var expectedInviter = Assert.IsType<DadExpectedPartyInviter>(observed!.ExpectedInviter);
        Assert.Equal("run-one", expectedInviter.RunId);
        Assert.Equal((ulong)1234, expectedInviter.ContentId);
        Assert.Equal("Peer Character", expectedInviter.CharacterName);
        Assert.Contains(fixture.Transport.Acknowledged, item => item.EnvelopeId == delivery.EnvelopeId);
        var receiptEnvelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<ExecutionOperationReceipt>());
        var receipt = fixture.Open<ExecutionOperationReceipt>(receiptEnvelope);
        Assert.Equal(ExecutionOutcome.Denied, receipt.Outcome);
        Assert.Equal("dad-partylist-proof-required", receipt.SafeCode);
        Assert.True(receipt.ObservedPartyContentIds.IsDefaultOrEmpty);
    }

    [Fact]
    public async Task SlotOneFollowerTargetsSerializeAndDecodeExactly()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        DadAutoPartyFormExecutionContext? observed = null;
        pump.ConfigureLifecycleHandlers(
            static _ => new(false, "unused", 1),
            static (_, _, _) => ValueTask.FromResult(new DadAutoPartyPrivacyResult(false, false, "unused")));
        pump.ConfigureFormExecutionHandler((context, _) =>
        {
            observed = context;
            return ValueTask.FromResult(new DadAutoPartyExecutionResult(
                context.Operation.OperationId,
                context.Operation.ProposalId,
                context.Operation.Kind,
                ExecutionOutcome.Denied,
                DadRunPhase.Idle,
                "dad-test-form-observed",
                context.Operation.ExpectedStateGeneration));
        });
        var first = NativeInviteTarget("run-follower-targets", "Slot2", "Private Two", 2002);
        var second = new DadNativePartyInviteTarget
        {
            RunId = "run-follower-targets",
            ModuleId = DadModuleId.PremadeDuty,
            SlotId = "Slot3",
            WorkerSessionId = new DadWorkerSessionId("private-worker-three"),
            AccountKey = new DadAccountKey("private-account-three"),
            CharacterKey = new DadCharacterKey("private-character-three"),
            ContentId = 3003,
            CharacterName = "Private Three",
            WorldId = 31,
        };
        var command = new DadAutoPartyParticipantCommand(
            Guid.NewGuid(),
            DadAutoPartyParticipantCommandKind.Execution,
            Guid.NewGuid(),
            first.RunId,
            "Slot1",
            PumpFixture.PeerOwner,
            PumpFixture.PeerIsland,
            "opaque-remote",
            24,
            "dad-duty-1",
            ExecutionOperationKind.Form,
            3,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(2),
            PartyInviteTargets: [first, second]);
        var method = typeof(DadAutoPartyRelayPump).GetMethod(
            "BuildExecutionOperation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var outgoing = Assert.IsType<ExecutionOperation>(method.Invoke(pump, [command]));

        Assert.Null(outgoing.InviteLocator);
        Assert.Equal(2, outgoing.PartyInviteTargets.Length);
        var expectedTargets = new[] { first, second };
        for (var index = 0; index < expectedTargets.Length; index++)
        {
            var encoded = outgoing.PartyInviteTargets[index].OpaqueLocator.ToArray();
            try
            {
                using var payload = JsonDocument.Parse(encoded);
                var expected = expectedTargets[index];
                Assert.Equal(expected.RunId, payload.RootElement.GetProperty("RunId").GetString());
                Assert.Equal(expected.WorkerSessionId.Value, payload.RootElement.GetProperty("WorkerSessionId").GetString());
                Assert.Equal(expected.AccountKey.Value, payload.RootElement.GetProperty("AccountKey").GetString());
                Assert.Equal(expected.CharacterKey.Value, payload.RootElement.GetProperty("CharacterKey").GetString());
                Assert.Equal(expected.ContentId, payload.RootElement.GetProperty("ContentId").GetUInt64());
                Assert.Equal(expected.CharacterName, payload.RootElement.GetProperty("CharacterName").GetString());
                Assert.Equal(expected.WorldId, payload.RootElement.GetProperty("WorldId").GetUInt16());
                Assert.Equal(expected.ModuleId.ToString(), payload.RootElement.GetProperty("ModuleId").GetString());
                Assert.Equal(expected.SlotId, payload.RootElement.GetProperty("SlotId").GetString());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }

        var inbound = outgoing with
        {
            Header = fixture.PeerHeader("form-with-follower-targets"),
            OwnerId = new OwnerId(PumpFixture.LocalOwner),
            PartyInviteTargets = outgoing.PartyInviteTargets.Select(locator => locator with
            {
                OwnerId = new OwnerId(PumpFixture.PeerOwner),
                IslandId = new IslandId(PumpFixture.PeerIsland),
            }).ToImmutableArray(),
        };
        var delivery = fixture.SealPeer(inbound);
        fixture.Transport.Inbound.Enqueue(delivery);

        await pump.ProcessOnceAsync();
        pump.UpdateFramework();
        pump.UpdateFramework();
        await pump.ProcessOnceAsync();

        Assert.NotNull(observed);
        Assert.Null(observed!.ExpectedInviter);
        Assert.Equal(2, observed.PartyInviteTargets.Count);
        AssertNativeInviteTarget(first, observed.PartyInviteTargets[0]);
        AssertNativeInviteTarget(second, observed.PartyInviteTargets[1]);
        Assert.Contains(fixture.Transport.Acknowledged, item => item.EnvelopeId == delivery.EnvelopeId);
    }

    [Fact]
    public async Task FormWithInviterAndFollowerTargetsIsDenied()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        var operation = new ExecutionOperation(
            fixture.PeerHeader("form-with-contradictory-locators"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new OwnerId(PumpFixture.LocalOwner),
            ExecutionOperationKind.Form,
            new ActivityId("dad-duty-1"),
            new OpaqueCharacterId("opaque-local"),
            new JobId("19"),
            PeerExpectedInviterLocator("run-contradictory"),
            1,
            false,
            PeerPartyInviteLocators(NativeInviteTarget("run-contradictory", "Slot2", "Private Two", 2002)));
        var delivery = fixture.SealPeer(operation);
        fixture.Transport.Inbound.Enqueue(delivery);

        await pump.ProcessOnceAsync();

        Assert.DoesNotContain(fixture.Transport.Acknowledged, item => item.EnvelopeId == delivery.EnvelopeId);
        Assert.Equal(0, pump.Snapshot.PendingExecutionCount);
        Assert.Equal("dad-relay-form-locator-mode-invalid", pump.Snapshot.SafeCode);
    }

    [Fact]
    public async Task AuthoritativeFormReceiptPropagatesContentIds()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        pump.ConfigureLifecycleHandlers(
            static _ => new(false, "unused", 1),
            static (_, _, _) => ValueTask.FromResult(new DadAutoPartyPrivacyResult(false, false, "unused")));
        pump.ConfigureFormExecutionHandler((context, _) => ValueTask.FromResult(new DadAutoPartyExecutionResult(
            context.Operation.OperationId,
            context.Operation.ProposalId,
            context.Operation.Kind,
            ExecutionOutcome.Completed,
            DadRunPhase.GroupReady,
            "dad-form-complete",
            context.Operation.ExpectedStateGeneration,
            new DadAutoPartyObservedPartyReceipt(
                2,
                [1001UL, 2002UL],
                "partylist-authoritative",
                DateTime.UtcNow))));
        var operation = new ExecutionOperation(
            fixture.PeerHeader("form-with-authoritative-receipt"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new OwnerId(PumpFixture.LocalOwner),
            ExecutionOperationKind.Form,
            new ActivityId("dad-duty-1"),
            new OpaqueCharacterId("opaque-local"),
            new JobId("19"),
            null,
            1,
            false,
            PeerPartyInviteLocators(NativeInviteTarget("run-authoritative", "Slot2", "Private Two", 2002)));
        fixture.Transport.Inbound.Enqueue(fixture.SealPeer(operation));

        await pump.ProcessOnceAsync();
        pump.UpdateFramework();
        pump.UpdateFramework();
        await pump.ProcessOnceAsync();

        var receiptEnvelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<ExecutionOperationReceipt>());
        var receipt = fixture.Open<ExecutionOperationReceipt>(receiptEnvelope);
        Assert.Equal(ExecutionOutcome.Completed, receipt.Outcome);
        Assert.Equal(new ulong[] { 1001, 2002 }, receipt.ObservedPartyContentIds);
    }

    [Fact]
    public async Task ExecutionOperationIsDeniedWhileExecutionOptInIsDisabled()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        fixture.Configuration.Enabled = false;
        await using var pump = fixture.CreatePump();
        var operation = new ExecutionOperation(
            fixture.PeerHeader("disabled-execution"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new OwnerId(PumpFixture.LocalOwner),
            ExecutionOperationKind.Prepare,
            new ActivityId("dad-duty-1"),
            new OpaqueCharacterId("opaque-local"),
            new JobId("19"),
            null,
            1,
            false,
            ImmutableArray<InviteLocator>.Empty);
        var delivery = fixture.SealPeer(operation);
        fixture.Transport.Inbound.Enqueue(delivery);

        await pump.ProcessOnceAsync();

        Assert.DoesNotContain(fixture.Transport.Acknowledged, item => item.EnvelopeId == delivery.EnvelopeId);
        Assert.Equal(0, pump.Snapshot.PendingExecutionCount);
        Assert.Equal("dad-execution-disabled", pump.Snapshot.SafeCode);
    }

    [Fact]
    public async Task ExecutionCommandSerializesExactModuleReference()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        var module = new EndpointExecutionModuleReference(2, nameof(DadModuleId.Duty));
        var command = new DadAutoPartyParticipantCommand(
            Guid.NewGuid(),
            DadAutoPartyParticipantCommandKind.Execution,
            Guid.NewGuid(),
            "run-module-reference",
            "Slot1",
            PumpFixture.PeerOwner,
            PumpFixture.PeerIsland,
            "opaque-remote",
            19,
            "dad-duty-1",
            ExecutionOperationKind.Queue,
            3,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(2),
            ExecutionModuleReference: module);
        var method = typeof(DadAutoPartyRelayPump).GetMethod(
            "BuildExecutionOperation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var operation = Assert.IsType<ExecutionOperation>(method.Invoke(pump, [command]));

        Assert.Equal(module, operation.ModuleReference);
    }

    [Fact]
    public async Task RestoreCommandCarriesOneBoundedEncryptedTeardownLocator()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        var follower = NativeInviteTarget("run-restore-locator", "Slot2", "Private Follower", 2002);
        var command = new DadAutoPartyParticipantCommand(
            Guid.NewGuid(),
            DadAutoPartyParticipantCommandKind.Execution,
            Guid.NewGuid(),
            "run-restore-locator",
            "Slot1",
            PumpFixture.PeerOwner,
            PumpFixture.PeerIsland,
            "opaque-remote",
            19,
            "dad-duty-1",
            ExecutionOperationKind.Restore,
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(2),
            Inviter: new DadExpectedPartyInviter
            {
                RunId = "run-restore-locator",
                WorkerSessionId = new DadWorkerSessionId("worker-slot1"),
                AccountKey = new DadAccountKey("account-slot1"),
                CharacterKey = new DadCharacterKey("Private Leader@Alpha"),
                ContentId = 1001,
                CharacterName = "Private Leader",
                WorldId = 21,
            },
            PartyInviteTargets: [follower]);
        var method = typeof(DadAutoPartyRelayPump).GetMethod(
            "BuildExecutionOperation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var operation = Assert.IsType<ExecutionOperation>(method.Invoke(pump, [command]));

        Assert.Equal(ExecutionOperationKind.Restore, operation.Kind);
        Assert.NotNull(operation.InviteLocator);
        Assert.True(operation.InviteLocator.OpaqueLocator.Length is > 0 and <= AutoPartyProtocol.MaximumTextValueLength);
        Assert.True(operation.PartyInviteTargets.IsDefaultOrEmpty);
        Assert.Null(operation.ModuleReference);
    }

    [Fact]
    public async Task ExecutionReceiptEchoesExactModuleReferenceWithoutPartyProof()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        var module = new EndpointExecutionModuleReference(0, nameof(DadModuleId.PremadeDuty));
        var operation = new ExecutionOperation(
            fixture.PeerHeader("queue-with-module-reference"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new OwnerId(PumpFixture.LocalOwner),
            ExecutionOperationKind.Queue,
            new ActivityId("dad-duty-1"),
            new OpaqueCharacterId("opaque-local"),
            new JobId("19"),
            null,
            1,
            false,
            ImmutableArray<InviteLocator>.Empty,
            module);
        fixture.Transport.Inbound.Enqueue(fixture.SealPeer(operation));

        await pump.ProcessOnceAsync();
        pump.UpdateFramework();
        pump.UpdateFramework();
        await pump.ProcessOnceAsync();

        var envelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<ExecutionOperationReceipt>());
        var receipt = fixture.Open<ExecutionOperationReceipt>(envelope);
        Assert.Equal(module, receipt.ModuleReference);
        Assert.True(receipt.ObservedPartyContentIds.IsDefaultOrEmpty);
    }

    [Fact]
    public async Task AcceptedQueueIsPolledUntilCompletedBeforeReceipt()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        var execution = new AcceptedThenCompletedExecutionFacade();
        fixture.Service.ConfigureExecutionFacade(execution);
        await using var pump = fixture.CreatePump();
        var module = new EndpointExecutionModuleReference(0, nameof(DadModuleId.PremadeDuty));
        var operation = new ExecutionOperation(
            fixture.PeerHeader("queue-accepted-then-complete"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new OwnerId(PumpFixture.LocalOwner),
            ExecutionOperationKind.Queue,
            new ActivityId("dad-duty-1"),
            new OpaqueCharacterId("opaque-local"),
            new JobId("19"),
            null,
            1,
            false,
            ImmutableArray<InviteLocator>.Empty,
            module);
        fixture.Transport.Inbound.Enqueue(fixture.SealPeer(operation));

        await pump.ProcessOnceAsync();
        for (var attempt = 0; attempt < 4; attempt++)
            pump.UpdateFramework();
        await pump.ProcessOnceAsync();

        Assert.Equal(2, execution.QueueCalls);
        var envelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<ExecutionOperationReceipt>());
        var receipt = fixture.Open<ExecutionOperationReceipt>(envelope);
        Assert.Equal(ExecutionOutcome.Completed, receipt.Outcome);
        Assert.Equal("dad-test-queue-complete", receipt.SafeCode);
        Assert.Equal(module, receipt.ModuleReference);
    }

    [Fact]
    public void EndpointOwnsAndStopsAttachedRelayPump()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        var pump = new DadAutoPartyRelayPump(
            fixture.Configuration,
            fixture.IdentityStore,
            fixture.Connector,
            fixture.Service,
            fixture.Bridge,
            fixture.PendingStore);
        using var endpoint = new DadAutoPartyEndpointService(
            fixture.Configuration,
            new UnusedWebhookStore(),
            new UnusedLegacyTokenStore(),
            fixture.Connector,
            static () => { },
            identityStore: fixture.IdentityStore);

        endpoint.AttachRelayPump(pump, fixture.Service);
        Assert.True(endpoint.RelayStatus.Attached);

        endpoint.Dispose();

        Assert.False(pump.Snapshot.Running);
    }

    [Fact]
    public async Task EndpointRejectsRelayAttachmentBeforeValidatedBootstrap()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Unregistered);
        var pump = fixture.CreatePump();
        using var endpoint = new DadAutoPartyEndpointService(
            fixture.Configuration,
            new UnusedWebhookStore(),
            new UnusedLegacyTokenStore(),
            fixture.Connector,
            static () => { },
            identityStore: fixture.IdentityStore);

        var error = Assert.Throws<InvalidOperationException>(() =>
            endpoint.AttachRelayPump(pump, fixture.Service));

        Assert.Equal("dad-relay-bootstrap-not-validated", error.Message);
        Assert.False(endpoint.RelayStatus.Attached);
        await pump.DisposeAsync();
    }

    [Fact]
    public void FilePendingStoreRoundTripsAndClearsOnlyMatchingDeregistration()
    {
        var root = Path.Combine(Path.GetTempPath(), "dad-autoparty-pending-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DadAutoPartyFilePendingOperationStore(root);
            var pending = new DadAutoPartyPendingDeregistration(
                Guid.NewGuid(),
                7,
                "dad-owner-deregistered",
                DateTimeOffset.UtcNow,
                true);

            store.SaveDeregistration(pending);
            Assert.Equal(pending, store.LoadDeregistration());
            store.ClearDeregistration(Guid.NewGuid());
            Assert.Equal(pending, store.LoadDeregistration());
            store.ClearDeregistration(pending.DeregistrationId);
            Assert.Null(store.LoadDeregistration());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static (DadRunPlan Plan, DadRunSlotManifest Manifest) RemoteRuntime()
    {
        var proposalId = Guid.NewGuid();
        var orchestration = new DadOrchestrationIntent
        {
            AutoPartyProposalId = proposalId.ToString("D"),
            QueueAuthority = DadQueueAuthority.Leader,
            InviteAuthority = DadInviteAuthority.PresetLeader,
            RosterIntent = new DadRosterIntent { ExpectedPartySize = 1, RequireRemoteParticipants = true },
        };
        var request = new DadRunRequest
        {
            RequestId = $"run-{Guid.NewGuid():N}",
            Orchestration = orchestration,
        };
        return (
            new DadRunPlan
            {
                Request = request,
                Orchestration = orchestration,
                RequiredParticipantCount = 1,
                RequiresRemoteParticipants = true,
                LeaderCharacterKey = DadRunSlotManifestRules.RegisteredIslandSlotOneAuthority,
                InviterCharacterKey = DadRunSlotManifestRules.RegisteredIslandSlotOneAuthority,
                CompositeModuleId = DadModuleId.PremadeDuty,
                Modules =
                [
                    new DadPlannedModuleExecution
                    {
                        ModuleId = DadModuleId.PremadeDuty,
                        DisplayName = "Synthetic duty",
                        ExpectedPartySize = 1,
                        RequiresPeers = true,
                    },
                ],
            },
            new DadRunSlotManifest
            {
                RequestId = request.RequestId,
                ExpectedPartySize = 1,
                LeaderCharacterKey = DadRunSlotManifestRules.RegisteredIslandSlotOneAuthority,
                InviterCharacterKey = DadRunSlotManifestRules.RegisteredIslandSlotOneAuthority,
                Modules =
                [
                    new DadFrozenModulePayload
                    {
                        ModuleId = DadModuleId.PremadeDuty,
                        DutyName = "Synthetic duty",
                        ExpectedPartySize = 1,
                    },
                ],
                Slots =
                [
                    new DadFrozenRunSlot
                    {
                        SlotId = "Slot1",
                        RouteKind = DadRunSlotRouteKind.RegisteredIsland,
                        OwnerId = PumpFixture.PeerOwner,
                        IslandId = PumpFixture.PeerIsland,
                        OpaqueCharacterId = "opaque-remote",
                        RequiredJobId = 19,
                        IsLeader = true,
                        IsInviter = true,
                    },
                ],
            });
    }

    private static RunProposal PeerProposalForLocalParticipant(
        PumpFixture fixture,
        Guid proposalId,
        string runId)
        => new(
            fixture.PeerHeader("participant-invite-locator-outbound"),
            proposalId,
            new OwnerId(PumpFixture.PeerOwner),
            new ActivityId("dad-duty-1"),
            [
                new ParticipantRequest(
                    new OwnerId(PumpFixture.PeerOwner),
                    new IslandId(PumpFixture.PeerIsland),
                    new OpaqueCharacterId("opaque-peer"),
                    new JobId("24")),
                new ParticipantRequest(
                    new OwnerId(PumpFixture.LocalOwner),
                    new IslandId(PumpFixture.LocalIsland),
                    new OpaqueCharacterId("opaque-local"),
                    new JobId("19")),
            ],
            "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            new EndpointExecutionPlan(
                runId,
                FormationOnly: false,
                RequirePostArReady: true,
                ParticipantReadyTimeoutSeconds: 120,
                AssemblyTimeoutSeconds: 90,
                LeaseDurationSeconds: 300,
                RepairPolicy: new EndpointRepairPolicy(false, 75, "self"),
                Participants:
                [
                    new EndpointExecutionParticipant(
                        "Slot1",
                        new OwnerId(PumpFixture.PeerOwner),
                        new IslandId(PumpFixture.PeerIsland),
                        new OpaqueCharacterId("opaque-peer"),
                        new JobId("24"),
                        EndpointExecutionRole.QueueLeader,
                        IsInviter: true),
                    new EndpointExecutionParticipant(
                        "Slot2",
                        new OwnerId(PumpFixture.LocalOwner),
                        new IslandId(PumpFixture.LocalIsland),
                        new OpaqueCharacterId("opaque-local"),
                        new JobId("19"),
                        EndpointExecutionRole.Participant,
                        IsInviter: false),
                ],
                Modules:
                [
                    new EndpointExecutionModule(
                        0,
                        nameof(DadModuleId.PremadeDuty),
                        new ActivityId("dad-duty-1"),
                        "Fixture Duty",
                        "duty-finder-duty",
                        1,
                        0,
                        Unsynced: false,
                        ExpectedPartySize: 2),
                ]));

    private static DadNativePartyInviteTarget NativeInviteTarget(
        string runId,
        string slotId,
        string characterName,
        ulong contentId)
        => new()
        {
            RunId = runId,
            ModuleId = DadModuleId.PremadeDuty,
            SlotId = slotId,
            WorkerSessionId = new DadWorkerSessionId("private-worker"),
            AccountKey = new DadAccountKey("private-account"),
            CharacterKey = new DadCharacterKey("private-character"),
            ContentId = contentId,
            CharacterName = characterName,
            WorldId = 21,
        };

    private static void AssertNativeInviteTarget(
        DadNativePartyInviteTarget expected,
        DadNativePartyInviteTarget actual)
    {
        Assert.Equal(expected.RunId, actual.RunId);
        Assert.Equal(expected.ModuleId, actual.ModuleId);
        Assert.Equal(expected.SlotId, actual.SlotId);
        Assert.Equal(expected.WorkerSessionId, actual.WorkerSessionId);
        Assert.Equal(expected.AccountKey, actual.AccountKey);
        Assert.Equal(expected.CharacterKey, actual.CharacterKey);
        Assert.Equal(expected.ContentId, actual.ContentId);
        Assert.Equal(expected.CharacterName, actual.CharacterName);
        Assert.Equal(expected.WorldId, actual.WorldId);
    }

    private static InviteLocator PeerExpectedInviterLocator(string runId)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            RunId = runId,
            WorkerSessionId = "peer-worker",
            AccountKey = "peer-account",
            CharacterKey = "peer-character",
            ContentId = 1001UL,
            CharacterName = "Peer Inviter",
            WorldId = (ushort)21,
        });
        try
        {
            return new InviteLocator(
                $"invite-{Guid.NewGuid():N}",
                new OwnerId(PumpFixture.PeerOwner),
                new IslandId(PumpFixture.PeerIsland),
                DateTimeOffset.UtcNow.AddMinutes(2),
                ImmutableArray.CreateRange(payload));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static ImmutableArray<InviteLocator> PeerPartyInviteLocators(
        params DadNativePartyInviteTarget[] targets)
    {
        var builder = ImmutableArray.CreateBuilder<InviteLocator>(targets.Length);
        foreach (var target in targets)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                target.RunId,
                WorkerSessionId = target.WorkerSessionId.Value,
                AccountKey = target.AccountKey.Value,
                CharacterKey = target.CharacterKey.Value,
                target.ContentId,
                target.CharacterName,
                target.WorldId,
                ModuleId = target.ModuleId.ToString(),
                target.SlotId,
            });
            try
            {
                builder.Add(new InviteLocator(
                    $"party-invite-{Guid.NewGuid():N}",
                    new OwnerId(PumpFixture.PeerOwner),
                    new IslandId(PumpFixture.PeerIsland),
                    DateTimeOffset.UtcNow.AddMinutes(2),
                    ImmutableArray.CreateRange(payload)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
        return builder.MoveToImmutable();
    }

    private static ParticipantInviteLocator PeerParticipantInviteLocator(
        ContractHeader header,
        Guid proposalId,
        string runId,
        string ownerId = PumpFixture.PeerOwner)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            RunId = runId,
            WorkerSessionId = "private-worker",
            AccountKey = "private-account",
            CharacterKey = "private-character",
            ContentId = 2002UL,
            CharacterName = "Private Peer",
            WorldId = (ushort)21,
            ModuleId = nameof(DadModuleId.PremadeDuty),
            SlotId = "Slot1",
        });
        try
        {
            return new ParticipantInviteLocator(
                header,
                proposalId,
                new OwnerId(ownerId),
                new OpaqueCharacterId("opaque-remote"),
                new InviteLocator(
                    $"participant-{Guid.NewGuid():N}",
                    new OwnerId(ownerId),
                    new IslandId(PumpFixture.PeerIsland),
                    DateTimeOffset.UtcNow.AddMinutes(2),
                    ImmutableArray.CreateRange(payload)),
                1);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static DadAllianceRecruitmentInstructionDto AllianceInstruction(
        string targetIslandId,
        string targetOwnerId,
        string targetOpaqueCharacterId)
        => new()
        {
            RecruitmentId = Guid.NewGuid().ToString("N"),
            LeaderName = "Leader Example",
            LeaderWorld = "Alpha",
            TargetIslandId = targetIslandId,
            TargetOwnerId = targetOwnerId,
            TargetOpaqueCharacterId = targetOpaqueCharacterId,
            AssignedAlliance = DadAllianceAssignment.G,
            CreateListingAsHost = false,
            Passcode = 1234,
            Attempt = 2,
            State = DadAllianceRecruitmentState.Searching,
            StopGeneration = 5,
            IssuedAtUtc = DateTime.UtcNow,
        };

    private static DadAllianceRecruitmentResultDto AllianceResult(
        DadAllianceRecruitmentInstructionDto instruction,
        string participantOwnerId,
        string targetOpaqueCharacterId)
        => new()
        {
            RecruitmentId = instruction.RecruitmentId,
            ParticipantOwnerId = participantOwnerId,
            TargetOpaqueCharacterId = targetOpaqueCharacterId,
            ExpectedAlliance = instruction.AssignedAlliance,
            ObservedAlliance = instruction.AssignedAlliance,
            Attempt = instruction.Attempt,
            State = DadAllianceRecruitmentState.Complete,
            ResultKind = DadAllianceRecruitmentResultKind.Succeeded,
            Retryable = false,
            StopGeneration = instruction.StopGeneration,
            ObservedAtUtc = DateTime.UtcNow,
            Summary = "dad-alliance-succeeded",
        };

    private static AuthenticatedContract<PairingInvite> CreatePeerPairingInvite(
        PumpFixture fixture,
        DateTimeOffset issuedAt)
    {
        var nonce = RandomNumberGenerator.GetBytes(AutoPartyProtocol.ContractNonceBytes);
        try
        {
            var header = new ContractHeader(
                AutoPartyProtocol.CurrentVersion,
                Guid.NewGuid(),
                $"peer-pairing-invite-{Guid.NewGuid():N}",
                new IslandId(PumpFixture.PeerIsland),
                new IslandId(DadAutoPartyIdentityPackageService.RegistrationRecipient),
                issuedAt,
                issuedAt.AddMinutes(10),
                1,
                1,
                3,
                2,
                ContractHeader.CreateNonce(nonce),
                []);
            var keys = new EndpointPublicKeys(
                3,
                "peer-signing-3",
                ImmutableArray.CreateRange(fixture.PeerSigningPublic),
                "peer-agreement-3",
                ImmutableArray.CreateRange(fixture.PeerAgreementPublic));
            var invite = new PairingInvite(
                header,
                Guid.NewGuid(),
                Guid.NewGuid(),
                new OwnerId(PumpFixture.PeerOwner),
                new IslandId(PumpFixture.PeerIsland),
                "Peer-endpoint",
                keys,
                DadAutoPartyIdentityPackageService.BuildFingerprint(
                    PumpFixture.PeerOwner,
                    PumpFixture.PeerIsland,
                    3,
                    fixture.PeerSigningPublic,
                    fixture.PeerAgreementPublic),
                header.ExpiresAt);
            var canonical = CanonicalCborCodec.EncodeUnsigned(invite);
            try
            {
                return AuthenticatedContract<PairingInvite>.Create(
                    invite,
                    BouncyCastlePrimitives.Ed25519Sign(fixture.PeerSigningPrivate, canonical));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private sealed class PumpFixture : IDisposable
    {
        public const string LocalOwner = "owner-local";
        public const string LocalIsland = "island-local";
        public const string PeerOwner = "owner-peer";
        public const string PeerIsland = "island-22222222222222222222222222222222";
        private readonly byte[] relaySigningPrivate = RandomNumberGenerator.GetBytes(32);
        private readonly byte[] relayAgreementPrivate = RandomNumberGenerator.GetBytes(32);
        private readonly byte[] relaySigningPublic;
        private readonly byte[] relayAgreementPublic;
        private readonly FixtureResolver fixtureResolver;
        public byte[] LocalSigningPrivate { get; } = RandomNumberGenerator.GetBytes(32);
        public byte[] LocalAgreementPrivate { get; } = RandomNumberGenerator.GetBytes(32);
        public byte[] PeerSigningPrivate { get; } = RandomNumberGenerator.GetBytes(32);
        public byte[] PeerAgreementPrivate { get; } = RandomNumberGenerator.GetBytes(32);
        public byte[] LocalSigningPublic { get; }
        public byte[] LocalAgreementPublic { get; }
        public byte[] PeerSigningPublic { get; }
        public byte[] PeerAgreementPublic { get; }
        public DadAutoPartyConfiguration Configuration { get; }
        public DadAutoPartyPrivateIdentityPackage Identity { get; }
        public MemoryIdentityStore IdentityStore { get; }
        public MemoryPendingStore PendingStore { get; } = new();
        public FakeTransport Transport { get; } = new();
        public DadDiscordCourierConnector Connector { get; }
        public DadAutoPartyService Service { get; }
        public DadAutoPartyParticipantBridge Bridge { get; }

        public PumpFixture(DadAutoPartyRegistrationState state, bool includePeer = false)
        {
            LocalSigningPublic = BouncyCastlePrimitives.DeriveEd25519PublicKey(LocalSigningPrivate);
            LocalAgreementPublic = BouncyCastlePrimitives.DeriveX25519PublicKey(LocalAgreementPrivate);
            PeerSigningPublic = BouncyCastlePrimitives.DeriveEd25519PublicKey(PeerSigningPrivate);
            PeerAgreementPublic = BouncyCastlePrimitives.DeriveX25519PublicKey(PeerAgreementPrivate);
            relaySigningPublic = BouncyCastlePrimitives.DeriveEd25519PublicKey(relaySigningPrivate);
            relayAgreementPublic = BouncyCastlePrimitives.DeriveX25519PublicKey(relayAgreementPrivate);
            Configuration = new DadAutoPartyConfiguration
            {
                Enabled = true,
                RegistrationState = state,
                RegistrationId = Guid.NewGuid().ToString("D"),
                RouteId = "route-local",
                CentralBotApplicationId = "123456789012345678",
                HomeGuildScope = "guild-home",
                WebhookCredentialReference = "webhook-mailbox-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                UplinkEpochId = Guid.NewGuid().ToString("D"),
                DownlinkEpochId = Guid.NewGuid().ToString("D"),
                MailboxEpochGeneration = 1,
                RelayKeyGeneration = 2,
                RelaySigningPublicKey = Convert.ToBase64String(relaySigningPublic),
                RelayAgreementPublicKey = Convert.ToBase64String(relayAgreementPublic),
                EndpointIdentityReference = "identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                RegisteredOwnerId = LocalOwner,
                RegisteredIslandId = LocalIsland,
                RegistrationFingerprint = DadAutoPartyIdentityPackageService.BuildFingerprint(
                    LocalOwner,
                    LocalIsland,
                    1,
                    LocalSigningPublic,
                    LocalAgreementPublic),
                EndpointAlias = "local-endpoint",
                SigningPublicKey = Convert.ToBase64String(LocalSigningPublic),
                EncryptionPublicKey = Convert.ToBase64String(LocalAgreementPublic),
                EndpointKeyGeneration = 1,
                StateGeneration = 1,
            };
            if (includePeer)
                Configuration.Pairings.Add(ActivePeerPairing());
            Identity = new(
                LocalOwner,
                LocalIsland,
                1,
                Convert.ToBase64String(LocalSigningPrivate),
                Convert.ToBase64String(LocalAgreementPrivate));
            IdentityStore = new MemoryIdentityStore(JsonSerializer.SerializeToUtf8Bytes(Identity));
            Connector = new DadDiscordCourierConnector(Configuration, static () => true);
            Connector.AttachVerifiedAdapter(Transport);
            Service = new DadAutoPartyService(Configuration, IdentityStore, static () => true, static () => { });
            // DadAutoPartyService owns a separate connector; the pump intentionally uses the fixture connector.
            Bridge = new DadAutoPartyParticipantBridge(Configuration);
            fixtureResolver = new FixtureResolver(this);
        }

        public DadAutoPartyRelayPump CreatePump(
            Func<DateTime, DadAutoPartyListingPublication>? inboundListingPublicationProvider = null,
            IDadAutoPartyInboundProposalStore? inboundProposalStore = null,
            Action<string>? diagnostic = null,
            Func<RunProposal, DadAutoPartyInboundAdmissionResult>? inboundAdmission = null,
            Func<DateTimeOffset>? utcNow = null)
            => new(
                Configuration,
                IdentityStore,
                Connector,
                Service,
                Bridge,
                PendingStore,
                inboundProposalStore: inboundProposalStore,
                inboundListingPublicationProvider: inboundListingPublicationProvider,
                inboundAdmission: inboundAdmission,
                utcNow: utcNow,
                delay: static (_, _) => Task.CompletedTask,
                diagnostic: diagnostic);

        public ContractHeader RelayHeader(string purpose, DateTimeOffset? now = null)
        {
            var nonce = RandomNumberGenerator.GetBytes(AutoPartyProtocol.ContractNonceBytes);
            try
            {
                var observedAt = now ?? DateTimeOffset.UtcNow;
                return new ContractHeader(
                    AutoPartyProtocol.CurrentVersion,
                    Guid.NewGuid(),
                    $"{purpose}-{Guid.NewGuid():N}",
                    new IslandId(DadAutoPartyIdentityPackageService.RegistrationRecipient),
                    new IslandId(LocalIsland),
                    observedAt,
                    observedAt.AddMinutes(5),
                    1,
                    1,
                    2,
                    1,
                    ContractHeader.CreateNonce(nonce),
                    ImmutableArray<int>.Empty);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
            }
        }

        public ContractHeader PeerHeader(string purpose)
        {
            var nonce = RandomNumberGenerator.GetBytes(AutoPartyProtocol.ContractNonceBytes);
            try
            {
                return new ContractHeader(
                    AutoPartyProtocol.CurrentVersion,
                    Guid.NewGuid(),
                    $"{purpose}-{Guid.NewGuid():N}",
                    new IslandId(PeerIsland),
                    new IslandId(LocalIsland),
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddMinutes(5),
                    1,
                    1,
                    3,
                    1,
                    ContractHeader.CreateNonce(nonce),
                    ImmutableArray<int>.Empty);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
            }
        }

        public ContractHeader PeerAllianceHeader(string purpose)
        {
            var header = PeerHeader(purpose);
            return header with { ExpiresAt = header.IssuedAt.AddMinutes(5) };
        }

        public OpaqueEnvelope SealRelay<T>(T contract)
            where T : IAutoPartyContract
        {
            var authenticator = new ProductionContractAuthenticator(fixtureResolver);
            var sealedContract = authenticator.Seal(authenticator.Sign(contract));
            return OpaqueEnvelope.Create(
                AutoPartyProtocol.CurrentVersion,
                contract.Header.MessageId,
                contract.Header.SenderIslandId,
                contract.Header.RecipientIslandId,
                contract.Header.IssuedAt,
                contract.Header.ExpiresAt,
                contract.Header.Generation,
                ProtocolContractRegistry.GetTypeId<T>(),
                SealedContractCodec.Encode(sealedContract));
        }

        public OpaqueEnvelope SealPeer<T>(T contract)
            where T : IAutoPartyContract
        {
            var authenticator = new ProductionContractAuthenticator(fixtureResolver);
            var sealedContract = authenticator.Seal(authenticator.Sign(contract));
            return OpaqueEnvelope.Create(
                AutoPartyProtocol.CurrentVersion,
                contract.Header.MessageId,
                contract.Header.SenderIslandId,
                contract.Header.RecipientIslandId,
                contract.Header.IssuedAt,
                contract.Header.ExpiresAt,
                contract.Header.Generation,
                ProtocolContractRegistry.GetTypeId<T>(),
                SealedContractCodec.Encode(sealedContract));
        }

        public T Open<T>(OpaqueEnvelope envelope)
            where T : IAutoPartyContract
        {
            var authenticator = new ProductionContractAuthenticator(fixtureResolver);
            var opened = authenticator.Open<T>(SealedContractCodec.Decode(envelope.Ciphertext.AsMemory()));
            Assert.True(opened.Succeeded);
            Assert.NotNull(opened.Message);
            return opened.Message.Contract;
        }

        public void Dispose()
        {
            Service.Dispose();
            Connector.DisposeAsync().AsTask().GetAwaiter().GetResult();
            IdentityStore.Dispose();
            foreach (var key in new[]
                     {
                         LocalSigningPrivate, LocalAgreementPrivate, PeerSigningPrivate, PeerAgreementPrivate,
                         LocalSigningPublic, LocalAgreementPublic, PeerSigningPublic, PeerAgreementPublic,
                         relaySigningPrivate, relayAgreementPrivate, relaySigningPublic, relayAgreementPublic,
                     })
                CryptographicOperations.ZeroMemory(key);
        }

        private DadAutoPartyPairing ActivePeerPairing()
        {
            var pairingId = Guid.NewGuid();
            return new()
            {
                PairingId = pairingId.ToString("D"),
                OwnerId = PeerOwner,
                IslandId = PeerIsland,
                HomeGuildScope = "guild-peer",
                PublicKeyFingerprint = new string('B', 64),
                LocalFingerprint = Configuration.RegistrationFingerprint,
                TranscriptHash = new string('C', 64),
                LocalSharePolicy = new DadAutoPartySharePolicy
                {
                    Enabled = true,
                    Mode = DadAutoPartyCharacterShareMode.AllCharactersForPeer,
                },
                PeerSharePolicy = new DadAutoPartySharePolicy
                {
                    Enabled = true,
                    Mode = DadAutoPartyCharacterShareMode.AllCharactersForPeer,
                },
                ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
                KeyGeneration = 3,
                SigningPublicKey = Convert.ToBase64String(PeerSigningPublic),
                AgreementPublicKey = Convert.ToBase64String(PeerAgreementPublic),
                ConfirmedAtUtc = DateTime.UtcNow,
            };
        }

        private sealed class FixtureResolver(PumpFixture fixture) : IContractKeyResolver
        {
            public bool TryGetEd25519PrivateKey(IslandId islandId, long version, out ReadOnlyMemory<byte> key)
                => Select(
                    islandId, version, fixture.relaySigningPrivate, fixture.LocalSigningPrivate,
                    fixture.PeerSigningPrivate, out key);
            public bool TryGetEd25519PublicKey(IslandId islandId, long version, out ReadOnlyMemory<byte> key)
                => Select(
                    islandId, version, fixture.relaySigningPublic, fixture.LocalSigningPublic,
                    fixture.PeerSigningPublic, out key);
            public bool TryGetX25519PrivateKey(IslandId islandId, long version, out ReadOnlyMemory<byte> key)
                => Select(
                    islandId, version, fixture.relayAgreementPrivate, fixture.LocalAgreementPrivate,
                    fixture.PeerAgreementPrivate, out key);
            public bool TryGetX25519PublicKey(IslandId islandId, long version, out ReadOnlyMemory<byte> key)
                => Select(
                    islandId, version, fixture.relayAgreementPublic, fixture.LocalAgreementPublic,
                    fixture.PeerAgreementPublic, out key);

            private static bool Select(
                IslandId islandId,
                long version,
                byte[] relay,
                byte[] local,
                byte[] peer,
                out ReadOnlyMemory<byte> key)
            {
                if (islandId.Value == DadAutoPartyIdentityPackageService.RegistrationRecipient && version == 2)
                {
                    key = relay;
                    return true;
                }
                if (islandId.Value == LocalIsland && version == 1)
                {
                    key = local;
                    return true;
                }
                if (islandId.Value == PeerIsland && version == 3)
                {
                    key = peer;
                    return true;
                }
                key = default;
                return false;
            }
        }
    }

    private sealed class AcceptedThenCompletedExecutionFacade : IAutoPartyExecutionFacade
    {
        public int QueueCalls { get; private set; }

        public ValueTask<DadAutoPartyExecutionResult> PrepareAsync(
            ExecutionOperation operation,
            IntegrationProfile? profile,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Completed(operation, "dad-test-prepare-complete"));

        public ValueTask<DadAutoPartyExecutionResult> ReserveAsync(
            ExecutionOperation operation,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Completed(operation, "dad-test-reserve-complete"));

        public ValueTask<DadAutoPartyExecutionResult> FormAsync(
            ExecutionOperation operation,
            DadAutoPartyObservedPartyReceipt observedParty,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Completed(operation, "dad-test-form-complete"));

        public ValueTask<DadAutoPartyExecutionResult> QueueAsync(
            ExecutionOperation operation,
            CancellationToken cancellationToken = default)
        {
            QueueCalls++;
            return ValueTask.FromResult(new DadAutoPartyExecutionResult(
                operation.OperationId,
                operation.ProposalId,
                operation.Kind,
                QueueCalls == 1 ? ExecutionOutcome.Accepted : ExecutionOutcome.Completed,
                DadRunPhase.QueueStarting,
                QueueCalls == 1 ? "dad-test-queue-pending" : "dad-test-queue-complete",
                operation.ExpectedStateGeneration));
        }

        public ValueTask<DadAutoPartyExecutionResult> CancelAsync(
            ExecutionOperation operation,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Completed(operation, "dad-test-cancel-complete"));

        public ValueTask<DadAutoPartyExecutionResult> SettleAsync(
            ExecutionOperation operation,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Completed(operation, "dad-test-settle-complete"));

        public ValueTask<DadAutoPartyExecutionResult> RestoreAsync(
            ExecutionOperation operation,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Completed(operation, "dad-test-restore-complete"));

        public void StopAll(string safeReason)
        {
        }

        private static DadAutoPartyExecutionResult Completed(ExecutionOperation operation, string safeCode)
            => new(
                operation.OperationId,
                operation.ProposalId,
                operation.Kind,
                ExecutionOutcome.Completed,
                DadRunPhase.Finalizing,
                safeCode,
                operation.ExpectedStateGeneration);
    }

    private sealed class MemoryIdentityStore(byte[] material) : IDadAutoPartyEndpointIdentityStore, IDisposable
    {
        private readonly byte[] material = material;

        public ValueTask<string> StoreAsync(ReadOnlyMemory<byte> identityMaterial, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<byte[]> LoadAsync(string identityReference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(material.ToArray());
        }

        public ValueTask<bool> DeleteAsync(string identityReference, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public void Dispose() => CryptographicOperations.ZeroMemory(material);
    }

    private sealed class MemoryPendingStore : IDadAutoPartyPendingOperationStore
    {
        private DadAutoPartyPendingDeregistration? pending;
        public int SaveCount { get; private set; }
        public DadAutoPartyPendingDeregistration? LoadDeregistration() => pending;
        public void SaveDeregistration(DadAutoPartyPendingDeregistration value)
        {
            SaveCount++;
            pending = value;
        }
        public void ClearDeregistration(Guid deregistrationId)
        {
            if (pending?.DeregistrationId == deregistrationId)
                pending = null;
        }
    }

    private sealed class UnusedWebhookStore : IDadAutoPartyWebhookCredentialStore
    {
        public ValueTask<string> StoreAsync(
            DadAutoPartyWebhookCredential credential,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<DadAutoPartyWebhookCredential> LoadAsync(
            string credentialReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ReplaceAsync(
            string credentialReference,
            DadAutoPartyWebhookCredential credential,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(
            string credentialReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedLegacyTokenStore : IDadAutoPartyDiscordTokenStore
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
            throw new NotSupportedException();
    }

    private sealed class FakeTransport : IAutoPartyTransportAdapter
    {
        public Queue<OpaqueEnvelope> Inbound { get; } = [];
        public Queue<bool> SendAcceptance { get; } = [];
        public Queue<string> SendSafeCodes { get; } = [];
        public List<OpaqueEnvelope> Sent { get; } = [];
        public List<AutoPartyTransportAcknowledgement> Acknowledged { get; } = [];

        public ValueTask<AutoPartyTransportHealth> GetHealthAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AutoPartyTransportHealth(
                AutoPartyTransportHealthState.Ready,
                "ready",
                DateTimeOffset.UtcNow));

        public async IAsyncEnumerable<OpaqueEnvelope> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (Inbound.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Inbound.Dequeue();
                await Task.Yield();
            }
        }

        public ValueTask<AutoPartyTransportSendResult> SendAsync(
            OpaqueEnvelope delivery,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(delivery);
            var accepted = SendAcceptance.Count == 0 || SendAcceptance.Dequeue();
            var safeCode = SendSafeCodes.Count > 0
                ? SendSafeCodes.Dequeue()
                : accepted ? "accepted" : "denied";
            return ValueTask.FromResult(new AutoPartyTransportSendResult(
                accepted,
                safeCode,
                delivery.EnvelopeId));
        }

        public ValueTask AcknowledgeAsync(
            AutoPartyTransportAcknowledgement acknowledgement,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Acknowledged.Add(acknowledgement);
            return ValueTask.CompletedTask;
        }
    }
}
