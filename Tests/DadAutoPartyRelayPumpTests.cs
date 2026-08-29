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
    public async Task RestartDropsPendingDeregistrationFromPreviousStateGeneration()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        fixture.PendingStore.SaveDeregistration(new DadAutoPartyPendingDeregistration(
            Guid.NewGuid(),
            2,
            "dad-owner-deregistered",
            DateTimeOffset.UtcNow,
            false,
            1));
        fixture.Configuration.StateGeneration = 2;
        await using var pump = fixture.CreatePump();

        await pump.ProcessOnceAsync();

        Assert.Null(fixture.PendingStore.LoadDeregistration());
        Assert.DoesNotContain(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DeregistrationRequest>());
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
        await using var pump = fixture.CreatePump();
        pump.ConfigureLifecycleHandlers(
            static _ => new(false, "unused", 1),
            static (_, _, _) => ValueTask.FromResult(new DadAutoPartyPrivacyResult(false, false, "unused")));

        await pump.ProcessOnceAsync();
        var runtime = RemoteRuntime();
        Assert.True(bridge.TryBindRun(runtime.Plan, runtime.Manifest, DateTimeOffset.UtcNow, out var blocker), blocker);

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
    public async Task IslandProfileFormAndQueueAdvanceOnCourierAcceptanceWithoutRelayReceiptWait()
    {
        var frame = FrenRiderProfileCodec.Encode("{\"frenName\":\"Remote\",\"enabled\":true}");
        using var fixture = new PumpFixture(
            DadAutoPartyRegistrationState.Active,
            includePeer: true,
            useFrenRider: true,
            remoteProfileProvider: _ => new DadAutoPartyRemoteProfileResult(true, frame, "ok"));
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
        await using var pump = fixture.CreatePump();

        await pump.ProcessOnceAsync();
        var directoryEnvelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>());
        var directory = fixture.Open<DirectoryQuery>(directoryEnvelope);
        Assert.Equal(1, pump.Snapshot.AwaitingRelayReceiptCount);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
            fixture.RelayHeader("directory-receipt"),
            Guid.NewGuid(),
            directory.Header.MessageId,
            true,
            "directory-complete")));
        await pump.ProcessOnceAsync();
        Assert.Equal(0, pump.Snapshot.AwaitingRelayReceiptCount);
        fixture.Transport.Sent.Clear();

        var runtime = RemoteRuntime();
        var proposalId = Guid.Parse(runtime.Plan.Orchestration.AutoPartyProposalId);
        var now = DateTimeOffset.UtcNow;
        Assert.True(fixture.Bridge.TryBindRun(runtime.Plan, runtime.Manifest, now, out var blocker), blocker);

        await pump.ProcessOnceAsync();

        Assert.Collection(
            fixture.Transport.Sent,
            sent => Assert.Equal(ProtocolContractRegistry.GetTypeId<RunProposal>(), sent.PayloadType),
            sent => Assert.Equal(ProtocolContractRegistry.GetTypeId<IntegrationProfile>(), sent.PayloadType));
        Assert.Equal(0, pump.Snapshot.AwaitingRelayReceiptCount);
        Assert.Equal(0, fixture.Bridge.PendingCommandCount);

        Assert.True(fixture.Bridge.ObserveReservation(new Reservation(
            fixture.PeerHeader("reservation"),
            Guid.NewGuid(),
            proposalId,
            new OwnerId(PumpFixture.PeerOwner),
            new OpaqueCharacterId("opaque-remote"),
            1,
            1), now, out blocker), blocker);
        Assert.True(fixture.Bridge.ObservePreflight(new PreflightResult(
            fixture.PeerHeader("preflight"),
            proposalId,
            new OwnerId(PumpFixture.PeerOwner),
            true,
            1,
            1,
            ImmutableArray<string>.Empty,
            1), now, out blocker), blocker);
        Assert.True(fixture.Bridge.ObserveLease(new SessionLease(
            fixture.PeerHeader("lease"),
            Guid.NewGuid(),
            proposalId,
            new OwnerId(PumpFixture.PeerOwner),
            now.AddMinutes(10),
            SessionPermission.All,
            1,
            1), now, out blocker), blocker);
        Assert.True(fixture.Bridge.ObserveInviteTarget(
            fixture.PeerHeader("invite-target"),
            proposalId,
            new OwnerId(PumpFixture.PeerOwner),
            new OpaqueCharacterId("opaque-remote"),
            new DadWorkerSessionId("private-worker"),
            new DadAccountKey("private-account"),
            new DadCharacterKey("private-character"),
            2002,
            "Private Peer",
            21,
            now.AddMinutes(2),
            now,
            out blocker), blocker);

        Assert.True(fixture.Bridge.RequestOperation(
            proposalId,
            "Slot1",
            ExecutionOperationKind.Form,
            null,
            null,
            [],
            now,
            out blocker), blocker);
        await pump.ProcessOnceAsync();
        Assert.Equal(0, pump.Snapshot.AwaitingRelayReceiptCount);
        Assert.True(fixture.Bridge.IsOperationComplete(
            proposalId,
            "Slot1",
            ExecutionOperationKind.Form,
            now));

        Assert.True(fixture.Bridge.RequestOperation(
            proposalId,
            "Slot1",
            ExecutionOperationKind.Queue,
            0,
            null,
            now,
            out blocker), blocker);
        await pump.ProcessOnceAsync();

        var participantTypes = fixture.Transport.Sent
            .Select(static sent => sent.PayloadType)
            .ToList();
        Assert.Equal(
            [
                ProtocolContractRegistry.GetTypeId<RunProposal>(),
                ProtocolContractRegistry.GetTypeId<IntegrationProfile>(),
                ProtocolContractRegistry.GetTypeId<ExecutionOperation>(),
                ProtocolContractRegistry.GetTypeId<ExecutionOperation>(),
            ],
            participantTypes);
        Assert.Equal(0, pump.Snapshot.AwaitingRelayReceiptCount);
        Assert.DoesNotContain(ProtocolContractRegistry.GetTypeId<IntegrationProfileReceipt>(), participantTypes);
        Assert.DoesNotContain(ProtocolContractRegistry.GetTypeId<ExecutionOperationReceipt>(), participantTypes);
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
            item => item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>() &&
                    fixture.Open<DirectoryQuery>(item).IncludePromiscuous);
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
                Guid.Parse("2a7a07ad-a834-4ac7-bad2-f68d5010309d"),
                1,
                0,
                false,
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
    public async Task TwoEndpointsPublishRouteReceiveAndProjectCompleteDirectories()
    {
        var now = DateTimeOffset.UtcNow;
        await using var fixture = new TwoEndpointConvergenceFixture(now);
        var firstA = fixture.BuildPublication(
            fixture.EndpointA,
            "opaque-endpoint-a-v1",
            "Endpoint A Character",
            "Endpoint A World");
        var firstB = fixture.BuildPublication(
            fixture.EndpointB,
            "opaque-endpoint-b-v1",
            "Endpoint B Character",
            "Endpoint B World");

        Assert.Single(firstA.Listings);
        Assert.Single(firstB.Listings);
        var publishA = fixture.EndpointA.Pump.QueueListingUpdateAndWaitAsync(
            firstA.StandingPolicy,
            firstA.Listings,
            firstA.PairedLabels,
            CancellationToken.None).AsTask();
        var publishB = fixture.EndpointB.Pump.QueueListingUpdateAndWaitAsync(
            firstB.StandingPolicy,
            firstB.Listings,
            firstB.PairedLabels,
            CancellationToken.None).AsTask();

        await fixture.DrainAsync();

        Assert.True(
            publishA.IsCompleted,
            $"Endpoint A publication remained pending: pump={fixture.EndpointA.Pump.Snapshot.SafeCode}, " +
            $"sent=[{string.Join(',', fixture.EndpointA.Transport.Sent.Select(static item => item.PayloadType))}], " +
            $"acknowledged={fixture.EndpointA.Transport.Acknowledged.Count}, " +
            $"observed=[{string.Join(',', fixture.EndpointA.ObservedSafeCodes.Distinct(StringComparer.Ordinal))}], " +
            $"delivered={fixture.Delivered.Count}.");
        Assert.True(
            publishB.IsCompleted,
            $"Endpoint B publication remained pending: pump={fixture.EndpointB.Pump.Snapshot.SafeCode}, " +
            $"sent=[{string.Join(',', fixture.EndpointB.Transport.Sent.Select(static item => item.PayloadType))}], " +
            $"acknowledged={fixture.EndpointB.Transport.Acknowledged.Count}.");
        Assert.True((await publishA).Allowed);
        Assert.True((await publishB).Allowed);
        var activeA = fixture.GetActiveSnapshot(fixture.EndpointA);
        var activeB = fixture.GetActiveSnapshot(fixture.EndpointB);
        Assert.Equal("opaque-endpoint-a-v1", Assert.Single(activeA.Listings).CharacterHandle.Value);
        Assert.Equal("opaque-endpoint-b-v1", Assert.Single(activeB.Listings).CharacterHandle.Value);

        Assert.True((await fixture.EndpointA.Pump.RequestDirectoryAsync(string.Empty, false)).Allowed);
        Assert.True((await fixture.EndpointB.Pump.RequestDirectoryAsync(string.Empty, false)).Allowed);
        await fixture.DrainAsync();

        var projectedAtA = Assert.Single(fixture.EndpointA.Service.GetDirectorySnapshot().Listings);
        var projectedAtB = Assert.Single(fixture.EndpointB.Service.GetDirectorySnapshot().Listings);
        Assert.Equal("opaque-endpoint-b-v1", projectedAtA.OpaqueCharacterId);
        Assert.Equal("Endpoint B Character@Endpoint B World", projectedAtA.DisplayLabel);
        Assert.Equal("opaque-endpoint-a-v1", projectedAtB.OpaqueCharacterId);
        Assert.Equal("Endpoint A Character@Endpoint A World", projectedAtB.DisplayLabel);
        Assert.NotEqual(Assert.Single(firstB.Listings).DisplayLabel, projectedAtA.DisplayLabel);
        Assert.NotEqual(Assert.Single(firstA.Listings).DisplayLabel, projectedAtB.DisplayLabel);
        Assert.Contains(
            fixture.EndpointB.IslandId,
            fixture.EndpointA.Service.GetDirectorySnapshot().OnlineIslandIds);
        Assert.Contains(
            fixture.EndpointA.IslandId,
            fixture.EndpointB.Service.GetDirectorySnapshot().OnlineIslandIds);

        fixture.Advance(TimeSpan.FromSeconds(1));
        fixture.EndpointB.Configuration.StateGeneration++;
        var changedB = fixture.BuildPublication(
            fixture.EndpointB,
            "opaque-endpoint-b-v2",
            "Endpoint B Replacement",
            "Endpoint B World");
        var changedPublish = fixture.EndpointB.Pump.QueueListingUpdateAndWaitAsync(
            changedB.StandingPolicy,
            changedB.Listings,
            changedB.PairedLabels,
            CancellationToken.None).AsTask();
        await fixture.DrainAsync();

        Assert.True(
            changedPublish.IsCompleted,
            $"Changed endpoint B publication remained pending: pump={fixture.EndpointB.Pump.Snapshot.SafeCode}.");
        Assert.True((await changedPublish).Allowed);
        var latestB = fixture.GetActiveSnapshot(fixture.EndpointB);
        Assert.True(latestB.SnapshotRevision > activeB.SnapshotRevision);
        Assert.Equal("opaque-endpoint-b-v2", Assert.Single(latestB.Listings).CharacterHandle.Value);
        Assert.True((await fixture.EndpointA.Pump.RequestDirectoryAsync(string.Empty, false)).Allowed);
        await fixture.DrainAsync();

        var replacedAtA = Assert.Single(fixture.EndpointA.Service.GetDirectorySnapshot().Listings);
        Assert.Equal("opaque-endpoint-b-v2", replacedAtA.OpaqueCharacterId);
        Assert.Equal("Endpoint B Replacement@Endpoint B World", replacedAtA.DisplayLabel);
        Assert.DoesNotContain(
            fixture.EndpointA.Configuration.Listings,
            listing => listing.OpaqueCharacterId == "opaque-endpoint-b-v1");
        Assert.Equal(
            "opaque-endpoint-a-v1",
            Assert.Single(fixture.EndpointB.Service.GetDirectorySnapshot().Listings).OpaqueCharacterId);

        Assert.Empty(fixture.EndpointA.Configuration.RemoteBindings);
        var runtimeBindings = new DadAutoPartyRuntimeBindingStore();
        Assert.Empty(runtimeBindings.Snapshot(fixture.EndpointA.Configuration.RemoteBindings));
        Assert.True(DadAutoPartyFreeformRules.TryBuild(
            [
                new DadAutoPartyFreeformParticipant
                {
                    SelectionKey = "local-endpoint-a",
                    DisplayLabel = "Local endpoint A",
                    Kind = DadAutoPartyFreeformParticipantKind.Local,
                    AccountKey = new DadAccountKey("account-endpoint-a"),
                    CharacterKey = new DadCharacterKey("Local Character@Local World"),
                    ContentId = 1,
                    RequestedJobId = 19,
                },
                new DadAutoPartyFreeformParticipant
                {
                    SelectionKey = replacedAtA.OpaqueCharacterId,
                    DisplayLabel = replacedAtA.DisplayLabel,
                    Kind = DadAutoPartyFreeformParticipantKind.RegisteredIsland,
                    OwnerId = replacedAtA.OwnerId,
                    IslandId = replacedAtA.SharingIslandId,
                    OpaqueCharacterId = replacedAtA.OpaqueCharacterId,
                    RequestedJobId = 19,
                },
            ],
            out var formation,
            out var blocker), blocker);
        Assert.Empty(runtimeBindings.Snapshot(fixture.EndpointA.Configuration.RemoteBindings));
        Assert.True(runtimeBindings.TryStage(formation, out blocker), blocker);
        var binding = Assert.Single(runtimeBindings.Snapshot(fixture.EndpointA.Configuration.RemoteBindings));
        Assert.Equal("opaque-endpoint-b-v2", binding.OpaqueCharacterId);

        Assert.NotEmpty(fixture.Delivered);
        Assert.All(fixture.Delivered, delivered =>
            Assert.Contains(
                delivered.Recipient.Transport.Acknowledged,
                acknowledgement => acknowledgement.EnvelopeId == delivered.Envelope.EnvelopeId));
    }

    [Fact]
    public async Task ActiveRegistrationAutomaticallyRefreshesPrivateDirectoryEveryFiveMinutesAndCoalesces()
    {
        var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        await using var pump = fixture.CreatePump(utcNow: () => now);

        await pump.ProcessOnceAsync();
        Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>());
        Assert.Equal(
            "dad-directory-query-coalesced",
            (await pump.RequestDirectoryAsync(string.Empty, false)).SafeCode);

        await pump.ProcessOnceAsync();
        Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>());

        now = now.AddMinutes(5);
        await pump.ProcessOnceAsync();
        Assert.Equal(2, fixture.Transport.Sent.Count(item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>()));
    }

    [Fact]
    public async Task DirectoryQueryReassemblesOneIslandAcrossPagesBeforeReplacingItsRoster()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        var expiresAt = now.AddHours(2);
        fixture.Configuration.Listings.Add(new DadAutoPartyListing
        {
            ListingId = Guid.NewGuid().ToString("D"),
            OwnerId = "owner-community",
            SharingIslandId = "island-community",
            SharingEndpointAlias = "community-endpoint",
            EffectiveShareMode = DadAutoPartyCharacterShareMode.CharacterList,
            EffectivePolicyHash = "community-policy-hash",
            OpaqueCharacterId = "opaque-old",
            DisplayLabel = "Old listing",
            AllowedJobIds = ["19"],
            AllowedActivityIds = [DadAutoPartyFreeformRules.FormationActivityId],
            Available = true,
            Revision = 1,
            ExpiresAtUtc = expiresAt.UtcDateTime,
        }.Normalize());
        await using var pump = fixture.CreatePump(utcNow: () => now);
        Assert.True((await pump.RequestDirectoryAsync(string.Empty, false)).Allowed);
        await pump.ProcessOnceAsync();
        var query = fixture.Open<DirectoryQuery>(fixture.Transport.Sent.Last(item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>()));
        Assert.Equal(TimeSpan.FromMinutes(5), query.Header.ExpiresAt - query.Header.IssuedAt);
        var snapshotId = Guid.Parse("a67e67f9-d1d2-4c5e-a582-c2f12b29b2a1");

        now = now.AddMinutes(2);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new DirectoryPage(
            fixture.RelayHeader("directory-community-1", now),
            query.QueryId,
            1,
            true,
            "position-2-0-1",
            [BuildEntry(0, true, "opaque-new-1")],
            1)));
        await pump.ProcessOnceAsync();

        Assert.Contains(fixture.Configuration.Listings, item => item.OpaqueCharacterId == "opaque-old");
        Assert.DoesNotContain(fixture.Configuration.Listings, item => item.OpaqueCharacterId == "opaque-new-1");
        var continuation = fixture.Transport.Sent
            .Where(item => item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>())
            .Select(fixture.Open<DirectoryQuery>)
            .Single(item => item.ContinuationToken == "position-2-0-1");
        Assert.Equal(query.Header.ExpiresAt, continuation.Header.ExpiresAt);
        Assert.Contains(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>() &&
            fixture.Open<DirectoryQuery>(item).ContinuationToken == "position-2-0-1");

        now = now.AddMinutes(2);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new DirectoryPage(
            fixture.RelayHeader("directory-community-2", now),
            query.QueryId,
            2,
            false,
            string.Empty,
            [BuildEntry(1, false, "opaque-new-2")],
            1)));
        await pump.ProcessOnceAsync();

        var visible = fixture.Configuration.Listings
            .Where(item => item.SharingIslandId == "island-community")
            .OrderBy(static item => item.OpaqueCharacterId, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["opaque-new-1", "opaque-new-2"], visible.Select(static item => item.OpaqueCharacterId));

        PrivateDirectoryEntry BuildEntry(int offset, bool hasMore, string handle) => new(
            new OwnerId("owner-community"),
            new IslandId("island-community"),
            "community-endpoint",
            "guild-home",
            CharacterShareMode.CharacterList,
            "community-policy-hash",
            true,
            [new PrivateCharacterListing(
                new OpaqueCharacterId(handle),
                $"Shared character {handle}",
                [new JobId("19")],
                [new ActivityId(DadAutoPartyFreeformRules.FormationActivityId)],
                true,
                1,
                expiresAt)],
            snapshotId,
            7,
            offset,
            hasMore,
            1,
            expiresAt);
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
                online ? Guid.Parse("4a227498-fd25-4e64-8e6d-609844c45a07") : Guid.Empty,
                online ? 1 : 0,
                0,
                false,
                1,
                expiresAt)),
            1);
    }

    [Fact]
    public async Task EstablishedCommandAuthorityDoesNotExpireWithPairingDeliveryTimestamp()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump();
        var slot = new DadFrozenRunSlot
        {
            SlotId = "Slot2",
            RouteKind = DadRunSlotRouteKind.RegisteredIsland,
            OwnerId = PumpFixture.PeerOwner,
            IslandId = PumpFixture.PeerIsland,
            OpaqueCharacterId = "opaque-peer",
            RequiredJobId = 19,
        };

        Assert.Null(pump.GetRemoteAuthorityBlocker([slot], DateTimeOffset.UtcNow));

        Assert.Single(fixture.Configuration.Pairings).ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        Assert.Null(pump.GetRemoteAuthorityBlocker([slot], DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task PairedDirectoryRequestsPrivateLabelsInSixteenHandleBatchesAndKeepsCommunityOpaque()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump(utcNow: () => now);
        await pump.ProcessOnceAsync();
        var query = fixture.Open<DirectoryQuery>(fixture.Transport.Sent.Single(item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>()));
        var expiresAt = now.AddHours(2);
        var pairedListings = Enumerable.Range(1, 17)
            .Select(index => new PrivateCharacterListing(
                new OpaqueCharacterId($"opaque-peer-{index}"),
                $"Shared character {index:0000}",
                [new JobId("19")],
                [new ActivityId(DadAutoPartyFreeformRules.FormationActivityId)],
                true,
                1,
                expiresAt))
            .ToImmutableArray();
        var pairing = Assert.Single(fixture.Configuration.Pairings);
        var page = new DirectoryPage(
            fixture.RelayHeader("directory-private-labels", now),
            query.QueryId,
            1,
            false,
            string.Empty,
            [
                new PrivateDirectoryEntry(
                    new OwnerId(PumpFixture.PeerOwner),
                    new IslandId(PumpFixture.PeerIsland),
                    "peer-endpoint",
                    "guild-peer",
                    CharacterShareMode.AllCharactersForPeer,
                    "paired-policy-hash",
                    true,
                    pairedListings,
                    Guid.Parse("0b9b33c1-c11f-4874-a287-e3539125442c"),
                    1,
                    0,
                    false,
                    1,
                    expiresAt),
                new PrivateDirectoryEntry(
                    new OwnerId("owner-community"),
                    new IslandId("island-community"),
                    "community-endpoint",
                    "guild-home",
                    CharacterShareMode.CharacterList,
                    "community-policy-hash",
                    true,
                    [new PrivateCharacterListing(
                        new OpaqueCharacterId("opaque-community"),
                        "Shared character community",
                        [new JobId("19")],
                        [new ActivityId(DadAutoPartyFreeformRules.FormationActivityId)],
                        true,
                        1,
                        expiresAt)],
                    Guid.Parse("13b90c1f-86f8-4c65-95fe-d79128ac3f0b"),
                    1,
                    0,
                    false,
                    1,
                    expiresAt),
            ],
            1);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(page));

        await pump.ProcessOnceAsync();

        var requests = fixture.Transport.Sent
            .Where(item => item.PayloadType == ProtocolContractRegistry.GetTypeId<PairedListingLabelRequest>())
            .Select(fixture.Open<PairedListingLabelRequest>)
            .ToList();
        Assert.Equal([16, 1], requests.Select(static request => request.RequestedCharacters.Length));
        Assert.All(requests, request =>
        {
            Assert.Equal(PumpFixture.PeerIsland, request.Header.RecipientIslandId.Value);
            Assert.Equal(Guid.Parse(pairing.PairingId), request.PairingId);
            Assert.Equal(pairing.TranscriptHash, request.PairingTranscriptHash);
            Assert.Equal(TimeSpan.FromMinutes(5), request.Header.ExpiresAt - request.Header.IssuedAt);
        });

        var unresolved = fixture.Service.GetDirectorySnapshot();
        Assert.Same(unresolved, fixture.Service.GetDirectorySnapshot());
        Assert.Equal(
            "Shared character 0001 (real name incoming)",
            unresolved.Listings.Single(item => item.OpaqueCharacterId == "opaque-peer-1").DisplayLabel);
        Assert.Equal(
            "Shared character community",
            unresolved.Listings.Single(item => item.OpaqueCharacterId == "opaque-community").DisplayLabel);
        Assert.Equal(
            "Shared character 0001",
            fixture.Configuration.Listings.Single(item => item.OpaqueCharacterId == "opaque-peer-1").DisplayLabel);

        var first = requests[0];
        now = now.AddMinutes(2);
        var response = new PairedListingLabelResponse(
            fixture.PeerHeader("paired-label-response", now),
            first.RequestId,
            first.PairingId,
            first.PairingTranscriptHash,
            [new PairedListingLabel(first.RequestedCharacters[0], "Private Character@Private World")]);
        fixture.Transport.Inbound.Enqueue(fixture.SealPeer(response));
        await pump.ProcessOnceAsync();

        var directory = fixture.Service.GetDirectorySnapshot();
        Assert.NotSame(unresolved, directory);
        Assert.Same(directory, fixture.Service.GetDirectorySnapshot());
        Assert.Equal(
            response.Header.ExpiresAt.UtcDateTime,
            fixture.Service.GetWindowProjection(string.Empty, includePromiscuous: true).ValidUntilUtc);
        Assert.Equal(
            "Private Character@Private World",
            directory.Listings.Single(item =>
                item.OpaqueCharacterId == first.RequestedCharacters[0].Value).DisplayLabel);
        Assert.Equal(
            "Shared character community",
            directory.Listings.Single(item => item.OpaqueCharacterId == "opaque-community").DisplayLabel);
        Assert.Equal(
            "Private Character@Private World",
            fixture.Configuration.Listings.Single(item =>
                item.OpaqueCharacterId == first.RequestedCharacters[0].Value).DisplayLabel);

        var refreshedListings = fixture.Configuration.Listings
            .Where(item => item.SharingIslandId == PumpFixture.PeerIsland)
            .Select(item =>
            {
                var refreshed = item.Clone();
                refreshed.OpaqueDisplayLabel = string.Empty;
                refreshed.DisplayLabel = pairedListings.Single(source =>
                    source.CharacterHandle.Value == item.OpaqueCharacterId).DisplayLabel;
                return refreshed;
            })
            .ToList();
        var refreshDecision = fixture.Service.ApplyRemoteListings(
            PumpFixture.PeerIsland,
            "guild-peer",
            new DadAutoPartySharePolicy
            {
                Enabled = true,
                Mode = DadAutoPartyCharacterShareMode.AllCharactersForPeer,
                Revision = 2,
                UpdatedAtUtc = now.UtcDateTime,
            },
            refreshedListings,
            registeredRequesterAttested: false);
        Assert.True(refreshDecision.Allowed, refreshDecision.SafeCode);
        Assert.Equal(
            "Private Character@Private World",
            fixture.Service.GetDirectorySnapshot().Listings.Single(item =>
                item.OpaqueCharacterId == first.RequestedCharacters[0].Value).DisplayLabel);

        var serializedConfiguration = JsonSerializer.Serialize(fixture.Configuration);
        var reloadedConfiguration = JsonSerializer.Deserialize<DadAutoPartyConfiguration>(serializedConfiguration)!;
        using (var reloadedService = new DadAutoPartyService(
                   reloadedConfiguration,
                   fixture.IdentityStore,
                   static () => true,
                   static () => { }))
        {
            reloadedService.ApplyDirectoryPresence(PumpFixture.PeerIsland, online: true);
            Assert.Equal(
                "Private Character@Private World",
                reloadedService.GetDirectorySnapshot().Listings.Single(item =>
                    item.OpaqueCharacterId == first.RequestedCharacters[0].Value).DisplayLabel);
        }

        Assert.False(fixture.Service.ApplyPairedListingLabels(
            PumpFixture.PeerIsland,
            first.PairingId,
            first.PairingTranscriptHash,
            first.RequestedCharacters.Select(static handle => handle.Value).ToArray(),
            [new PairedListingLabel(first.RequestedCharacters[0], "Expired Character@Private World")],
            now.AddSeconds(-1),
            now));
        var expiredLabelsDirectory = fixture.Service.GetDirectorySnapshot();
        Assert.Equal(
            "Private Character@Private World",
            expiredLabelsDirectory.Listings.Single(item =>
                item.OpaqueCharacterId == first.RequestedCharacters[0].Value).DisplayLabel);

        now = now.AddMinutes(3).AddSeconds(1);
        var expiredRequest = requests[1];
        var expiredResponse = new PairedListingLabelResponse(
            fixture.PeerHeader("paired-label-response-expired", now),
            expiredRequest.RequestId,
            expiredRequest.PairingId,
            expiredRequest.PairingTranscriptHash,
            [new PairedListingLabel(expiredRequest.RequestedCharacters[0], "Too Late@Private World")]);
        var expiredDelivery = fixture.SealPeer(expiredResponse);
        fixture.Transport.Inbound.Enqueue(expiredDelivery);
        await pump.ProcessOnceAsync();
        Assert.DoesNotContain(
            fixture.Transport.Acknowledged,
            item => item.EnvelopeId == expiredDelivery.EnvelopeId);
        Assert.Equal(
            "Private Character@Private World",
            fixture.Service.GetDirectorySnapshot().Listings.Single(item =>
                item.OpaqueCharacterId == first.RequestedCharacters[0].Value).DisplayLabel);

        fixture.Configuration.Listings.Single(item =>
            item.OpaqueCharacterId == first.RequestedCharacters[0].Value).ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        fixture.Service.Update(dadPluginEnabled: true);
        Assert.DoesNotContain(
            fixture.Service.GetDirectorySnapshot().Listings,
            item => item.OpaqueCharacterId == first.RequestedCharacters[0].Value);
    }

    [Fact]
    public async Task PairedLabelResponderReturnsOnlyCurrentlyPublishedAndPairAuthorizedHandles()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        var pairing = Assert.Single(fixture.Configuration.Pairings);
        pairing.LocalSharePolicy = new DadAutoPartySharePolicy
        {
            Enabled = true,
            Mode = DadAutoPartyCharacterShareMode.SpecificCharacter,
            CharacterHandles = ["opaque-allowed"],
        }.Normalize();
        var expiresAt = DateTime.UtcNow.AddMinutes(10);
        var listings = new[] { "opaque-allowed", "opaque-denied" }
            .Select(handle => new DadAutoPartyListing
            {
                ListingId = Guid.NewGuid().ToString("D"),
                OwnerId = PumpFixture.LocalOwner,
                SharingIslandId = PumpFixture.LocalIsland,
                OpaqueCharacterId = handle,
                DisplayLabel = $"Shared character {handle}",
                AllowedJobIds = ["19"],
                AllowedActivityIds = [DadAutoPartyFreeformRules.FormationActivityId],
                Available = true,
                Revision = 1,
                ExpiresAtUtc = expiresAt,
            })
            .ToList();
        await using var pump = fixture.CreatePump();
        Assert.True(pump.QueueListingUpdate(
            pairing.LocalSharePolicy,
            listings,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["opaque-allowed"] = "Allowed Character@Allowed World",
                ["opaque-denied"] = "Denied Character@Denied World",
            }).Allowed);
        var request = new PairedListingLabelRequest(
            fixture.PeerHeader("paired-label-request"),
            Guid.NewGuid(),
            Guid.Parse(pairing.PairingId),
            pairing.TranscriptHash,
            [new OpaqueCharacterId("opaque-allowed"), new OpaqueCharacterId("opaque-denied")]);
        fixture.Transport.Inbound.Enqueue(fixture.SealPeer(request));

        await pump.ProcessOnceAsync();

        var envelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<PairedListingLabelResponse>());
        var response = fixture.Open<PairedListingLabelResponse>(envelope);
        var label = Assert.Single(response.Labels);
        Assert.Equal("opaque-allowed", label.CharacterHandle.Value);
        Assert.Equal("Allowed Character@Allowed World", label.DisplayLabel);
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
    public async Task NinetyTwoCharacterRosterQueuesCompleteSizeValidListingSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        await using var pump = fixture.CreatePump(utcNow: () => now);
        var expiresAt = now.UtcDateTime.AddHours(2);
        var policy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.AllCharactersForPeer,
            Enabled = true,
            Revision = 3,
            UpdatedAtUtc = now.UtcDateTime,
        };
        var listings = Enumerable.Range(1, 92).Select(index => new DadAutoPartyListing
        {
            ListingId = Guid.NewGuid().ToString("D"),
            OwnerId = PumpFixture.LocalOwner,
            SharingIslandId = PumpFixture.LocalIsland,
            OpaqueCharacterId = $"opaque-local-{index:000}",
            DisplayLabel = $"Shared character {index:000}",
            AllowedJobIds = ["19"],
            AllowedActivityIds = [DadAutoPartyFreeformRules.FormationActivityId],
            Available = true,
            Revision = 3,
            ExpiresAtUtc = expiresAt,
        }).ToList();

        var queued = pump.QueueListingUpdate(policy, listings);
        await pump.ProcessOnceAsync();

        Assert.True(queued.Allowed, queued.SafeCode);
        var envelopes = fixture.Transport.Sent
            .Where(item => item.PayloadType == ProtocolContractRegistry.GetTypeId<PrivateListingUpdate>())
            .ToList();
        Assert.True(
            envelopes.Count > 1,
            $"Expected a multi-chunk snapshot; queued={queued.SafeCode}, sent {envelopes.Count}, " +
            $"types=[{string.Join(',', fixture.Transport.Sent.Select(static item => item.PayloadType))}], " +
            $"pump={pump.Snapshot.SafeCode}.");
        Assert.All(envelopes, envelope =>
            Assert.InRange(envelope.PayloadLength, 1, AutoPartyProtocol.MaximumSemanticEnvelopeBytes));
        var updates = envelopes.Select(fixture.Open<PrivateListingUpdate>)
            .OrderBy(static update => update.ChunkIndex)
            .ToList();
        Assert.Single(updates.Select(static update => update.SnapshotId).Distinct());
        Assert.Single(updates.Select(static update => update.SnapshotRevision).Distinct());
        Assert.All(updates, update => Assert.Equal(
            TimeSpan.FromMinutes(5),
            update.Header.ExpiresAt - update.Header.IssuedAt));
        Assert.All(updates, update => Assert.Equal(updates.Count, update.ChunkCount));
        Assert.Equal(Enumerable.Range(1, updates.Count), updates.Select(static update => update.ChunkIndex));
        Assert.Equal(
            listings.Select(static listing => listing.OpaqueCharacterId).Order(StringComparer.Ordinal),
            updates.SelectMany(static update => update.Listings)
                .Select(static listing => listing.CharacterHandle.Value)
                .Order(StringComparer.Ordinal));

        now = now.AddMinutes(1);
        var delayed = pump.QueueListingUpdate(policy, listings);
        Assert.True(delayed.Allowed, delayed.SafeCode);
        Assert.Equal("dad-listing-update-coalesced", delayed.SafeCode);

        foreach (var update in updates)
        {
            fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
                fixture.RelayHeader($"listing-update-receipt-{update.ChunkIndex}", now),
                Guid.NewGuid(),
                update.Header.MessageId,
                true,
                "listing-update-applied")));
        }
        await pump.ProcessOnceAsync();

        var completed = pump.QueueListingUpdate(policy, listings);
        Assert.True(completed.Allowed, completed.SafeCode);
        Assert.Equal("dad-listing-update-coalesced", completed.SafeCode);

        now = now.AddMinutes(5).AddSeconds(1);
        var expired = pump.QueueListingUpdate(policy, listings);
        Assert.True(expired.Allowed, expired.SafeCode);
        Assert.Equal("dad-listing-update-queued", expired.SafeCode);
    }

    [Fact]
    public async Task LatestDesiredListingPublishesAfterPendingSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        await using var pump = fixture.CreatePump(utcNow: () => now);
        var initialPolicy = CreateListingPolicy(now, revision: 1);

        var initial = pump.QueueListingUpdate(initialPolicy, []);
        await pump.ProcessOnceAsync();
        var first = Assert.Single(SentListingUpdates(fixture));

        now = now.AddSeconds(10);
        var latestPolicy = CreateListingPolicy(now, revision: 2);
        var latestListing = CreateListing(now, "opaque-latest", revision: 2);
        var coalesced = pump.QueueListingUpdate(latestPolicy, [latestListing]);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
            fixture.RelayHeader("listing-latest-accepted", now),
            Guid.NewGuid(),
            first.Header.MessageId,
            true,
            "listing-update-applied")));

        await pump.ProcessOnceAsync();

        Assert.True(initial.Allowed, initial.SafeCode);
        Assert.Equal("dad-listing-update-queued", initial.SafeCode);
        Assert.True(coalesced.Allowed, coalesced.SafeCode);
        Assert.Equal("dad-listing-update-coalesced", coalesced.SafeCode);
        var second = Assert.Single(
            SentListingUpdates(fixture),
            update => update.SnapshotId != first.SnapshotId);
        Assert.Equal("opaque-latest", Assert.Single(second.Listings).CharacterHandle.Value);
        Assert.Equal(2, second.SharePolicy.Revision);
        Assert.Equal(now, second.Header.IssuedAt);
        Assert.Equal(now.AddMinutes(5), second.Header.ExpiresAt);
    }

    [Fact]
    public async Task LatestDesiredListingUsesNewestOfMultipleCoalescedUpdates()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        await using var pump = fixture.CreatePump(utcNow: () => now);
        Assert.True(pump.QueueListingUpdate(CreateListingPolicy(now, revision: 1), []).Allowed);
        await pump.ProcessOnceAsync();
        var first = Assert.Single(SentListingUpdates(fixture));

        var middlePolicy = CreateListingPolicy(now, revision: 2);
        var middle = pump.QueueListingUpdate(
            middlePolicy,
            [CreateListing(now, "opaque-middle", revision: 2)]);
        var latestPolicy = CreateListingPolicy(now, revision: 3);
        var latestListing = CreateListing(now, "opaque-newest", revision: 3);
        var latestLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["opaque-newest"] = "Newest Character@Synthetic World",
        };
        var latest = pump.QueueListingUpdate(latestPolicy, [latestListing], latestLabels);
        latestPolicy.Revision = 99;
        latestListing.OpaqueCharacterId = "opaque-mutated-after-queue";
        latestListing.AllowedJobIds.Clear();
        latestLabels["opaque-newest"] = "Mutated Character@Synthetic World";
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
            fixture.RelayHeader("listing-newest-accepted", now),
            Guid.NewGuid(),
            first.Header.MessageId,
            true,
            "listing-update-applied")));

        await pump.ProcessOnceAsync();

        Assert.True(middle.Allowed, middle.SafeCode);
        Assert.Equal("dad-listing-update-coalesced", middle.SafeCode);
        Assert.True(latest.Allowed, latest.SafeCode);
        Assert.Equal("dad-listing-update-coalesced", latest.SafeCode);
        var second = Assert.Single(
            SentListingUpdates(fixture),
            update => update.SnapshotId != first.SnapshotId);
        Assert.Equal(3, second.SharePolicy.Revision);
        var published = Assert.Single(second.Listings);
        Assert.Equal("opaque-newest", published.CharacterHandle.Value);
        Assert.Equal(3, published.Revision);
        Assert.DoesNotContain(
            SentListingUpdates(fixture).SelectMany(static update => update.Listings),
            listing => listing.CharacterHandle.Value == "opaque-middle");
    }

    [Fact]
    public async Task LatestDesiredListingPublishesAfterRejectedSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        await using var pump = fixture.CreatePump(utcNow: () => now);
        Assert.True(pump.QueueListingUpdate(CreateListingPolicy(now, revision: 1), []).Allowed);
        await pump.ProcessOnceAsync();
        var first = Assert.Single(SentListingUpdates(fixture));
        Assert.True(pump.QueueListingUpdate(
            CreateListingPolicy(now, revision: 2),
            [CreateListing(now, "opaque-after-rejection", revision: 2)]).Allowed);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
            fixture.RelayHeader("listing-latest-rejected", now),
            Guid.NewGuid(),
            first.Header.MessageId,
            false,
            "listing-update-rejected")));

        await pump.ProcessOnceAsync();

        var second = Assert.Single(
            SentListingUpdates(fixture),
            update => update.SnapshotId != first.SnapshotId);
        Assert.Equal("opaque-after-rejection", Assert.Single(second.Listings).CharacterHandle.Value);
    }

    [Fact]
    public async Task LatestDesiredListingPublishesAfterExpiredSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        await using var pump = fixture.CreatePump(utcNow: () => now);
        Assert.True(pump.QueueListingUpdate(CreateListingPolicy(now, revision: 1), []).Allowed);
        await pump.ProcessOnceAsync();
        var first = Assert.Single(SentListingUpdates(fixture));
        Assert.True(pump.QueueListingUpdate(
            CreateListingPolicy(now, revision: 2),
            [CreateListing(now, "opaque-after-expiry", revision: 2)]).Allowed);
        now = first.Header.ExpiresAt.AddSeconds(1);

        await pump.ProcessOnceAsync();

        var second = Assert.Single(
            SentListingUpdates(fixture),
            update => update.SnapshotId != first.SnapshotId);
        Assert.Equal("opaque-after-expiry", Assert.Single(second.Listings).CharacterHandle.Value);
        Assert.Equal(now, second.Header.IssuedAt);
    }

    [Fact]
    public async Task DisposalClearsDeferredDesiredListing()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        await using var pump = fixture.CreatePump(utcNow: () => now);
        Assert.True(pump.QueueListingUpdate(CreateListingPolicy(now, revision: 1), []).Allowed);
        await pump.ProcessOnceAsync();
        Assert.True(pump.QueueListingUpdate(
            CreateListingPolicy(now, revision: 2),
            [CreateListing(now, "opaque-disposed", revision: 2)]).Allowed);
        var deferredField = typeof(DadAutoPartyRelayPump).GetField(
            "deferredListingPublication",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(deferredField);
        Assert.NotNull(deferredField.GetValue(pump));
        await pump.DisposeAsync();
        Assert.Null(deferredField.GetValue(pump));
    }

    [Fact]
    public async Task RosterAboveProtocolMaximumFailsClosedWithoutTruncation()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        await using var pump = fixture.CreatePump();
        var expiresAt = DateTime.UtcNow.AddMinutes(15);
        var policy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.AllCharactersForPeer,
            Enabled = true,
            Revision = 3,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        var listings = Enumerable.Range(1, AutoPartyProtocol.MaximumCollectionItems + 1)
            .Select(index => new DadAutoPartyListing
            {
                ListingId = Guid.NewGuid().ToString("D"),
                OwnerId = PumpFixture.LocalOwner,
                SharingIslandId = PumpFixture.LocalIsland,
                OpaqueCharacterId = $"opaque-local-{index:000}",
                DisplayLabel = $"Shared character {index:000}",
                AllowedJobIds = ["19"],
                AllowedActivityIds = [DadAutoPartyFreeformRules.FormationActivityId],
                Available = true,
                Revision = 3,
                ExpiresAtUtc = expiresAt,
            })
            .ToList();

        var rejected = pump.QueueListingUpdate(policy, listings);
        await pump.ProcessOnceAsync();

        Assert.False(rejected.Allowed);
        Assert.Equal("dad-listing-update-invalid", rejected.SafeCode);
        Assert.DoesNotContain(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<PrivateListingUpdate>());
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
        var now = proposal.Header.IssuedAt;
        var expiredTargets = new List<DadAutoPartyExpiredRuntimeTarget>();
        var expectedTarget = NativeInviteTarget(runId, "Slot2", "Private Local", 1001);
        await using var pump = fixture.CreatePump(
            _ => new DadAutoPartyListingPublication(policy, [listing]),
            inboundAdmission: _ => new DadAutoPartyInboundAdmissionResult(
                runId,
                true,
                "dad-inbound-admission-ready",
                ["Slot2"],
                [expectedTarget]),
            utcNow: () => now,
            expiredRuntimeTargetHandler: expiredTargets.Add);
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
        Assert.DoesNotContain(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<Reservation>() ||
            item.PayloadType == ProtocolContractRegistry.GetTypeId<PreflightResult>() ||
            item.PayloadType == ProtocolContractRegistry.GetTypeId<SessionLease>());
        Assert.Contains(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<ParticipantInviteLocator>());
        Assert.Equal(0, fixture.PendingStore.SaveCount);

        now = proposal.Header.ExpiresAt.AddSeconds(1);
        await pump.ProcessOnceAsync();
        pump.UpdateFramework();

        var expired = Assert.Single(expiredTargets);
        Assert.Equal(proposalId, expired.ProposalId);
        Assert.Equal(PumpFixture.PeerIsland, expired.SenderIslandId);
        Assert.Equal(PumpFixture.LocalOwner, expired.OwnerId);
        Assert.Equal("opaque-local", expired.OpaqueCharacterId);
        Assert.False(pump.TryGetInboundRuntimeTarget(
            proposalId,
            new OpaqueCharacterId("opaque-local"),
            out _,
            out _,
            out _));
    }

    [Fact]
    public async Task AuthenticatedIntegrationProfileIsStoredAndAcknowledgedWithoutApplicationReceipt()
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
        proposal = proposal with
        {
            ExecutionPlan = proposal.ExecutionPlan! with { UseFrenRider = true },
        };
        await using var pump = fixture.CreatePump(
            _ => new DadAutoPartyListingPublication(policy, [listing]),
            inboundAdmission: _ => new DadAutoPartyInboundAdmissionResult(
                runId,
                true,
                "dad-inbound-admission-ready",
                ["Slot2"],
                [NativeInviteTarget(runId, "Slot2", "Private Local", 1001)]));

        await pump.ProcessOnceAsync();
        var directoryEnvelope = Assert.Single(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>());
        var directory = fixture.Open<DirectoryQuery>(directoryEnvelope);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
            fixture.RelayHeader("directory-receipt"),
            Guid.NewGuid(),
            directory.Header.MessageId,
            true,
            "directory-complete")));
        await pump.ProcessOnceAsync();
        fixture.Transport.Sent.Clear();

        var proposalDelivery = fixture.SealPeer(proposal);
        fixture.Transport.Inbound.Enqueue(proposalDelivery);
        await pump.ProcessOnceAsync();
        pump.UpdateFramework();
        await pump.ProcessOnceAsync();

        var frame = FrenRiderProfileCodec.Encode("{\"frenName\":\"Inbound\",\"enabled\":true}");
        var profileHeader = fixture.PeerHeader("integration-profile") with
        {
            ExpiresAt = proposal.Header.ExpiresAt,
        };
        var profile = new IntegrationProfile(
            profileHeader,
            Guid.NewGuid(),
            proposalId,
            new OwnerId(PumpFixture.LocalOwner),
            EnableLevelSync: false,
            EnableUnrestrictedParty: false,
            EnableMinimumItemLevel: false,
            EnableSilenceEcho: false,
            ["FrenRider"],
            "frenrider-profile",
            ExpectedStateGeneration: 1,
            new OpaqueCharacterId("opaque-local"),
            frame);
        var profileDelivery = fixture.SealPeer(profile);
        fixture.Transport.Inbound.Enqueue(profileDelivery);

        await pump.ProcessOnceAsync();

        Assert.Contains(fixture.Transport.Acknowledged, item =>
            item.EnvelopeId == proposalDelivery.EnvelopeId);
        Assert.Contains(fixture.Transport.Acknowledged, item =>
            item.EnvelopeId == profileDelivery.EnvelopeId);
        Assert.Equal(0, pump.Snapshot.AwaitingRelayReceiptCount);
        Assert.DoesNotContain(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<IntegrationProfileReceipt>() ||
            item.PayloadType == ProtocolContractRegistry.GetTypeId<ExecutionOperationReceipt>());
    }

    [Fact]
    public async Task PendingInboundAdmissionEmitsNoReadinessCeremonyAndReevaluatesToInviteLocator()
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
        var ready = false;
        await using var pump = fixture.CreatePump(
            _ => new DadAutoPartyListingPublication(policy, [listing]),
            inboundAdmission: _ => ready
                ? new DadAutoPartyInboundAdmissionResult(
                    runId,
                    true,
                    string.Empty,
                    ["Slot2"],
                    [NativeInviteTarget(runId, "Slot2", "Private Local", 1001)])
                : DadAutoPartyInboundAdmissionResult.Pending(
                    runId,
                    DadAutoPartyInboundAdmissionService.TakeoverPending));
        fixture.Transport.Inbound.Enqueue(fixture.SealPeer(proposal));

        await pump.ProcessOnceAsync();
        pump.UpdateFramework();
        await pump.ProcessOnceAsync();

        Assert.DoesNotContain(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<Reservation>() ||
            item.PayloadType == ProtocolContractRegistry.GetTypeId<PreflightResult>() ||
            item.PayloadType == ProtocolContractRegistry.GetTypeId<SessionLease>());

        ready = true;
        pump.UpdateFramework();
        await pump.ProcessOnceAsync();

        Assert.DoesNotContain(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<Reservation>() ||
            item.PayloadType == ProtocolContractRegistry.GetTypeId<PreflightResult>() ||
            item.PayloadType == ProtocolContractRegistry.GetTypeId<SessionLease>());
        Assert.Contains(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<ParticipantInviteLocator>());
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
        Assert.Equal(0, fixture.Bridge.PendingCommandCount);

        await pump.ProcessOnceAsync();

        Assert.Contains(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DeauthenticationNotice>());
        Assert.DoesNotContain(fixture.Transport.Sent, item =>
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
        Assert.DoesNotContain(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<ExecutionOperationReceipt>());
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
    public async Task AuthoritativeFormResultRemainsLocalWithoutExecutionReceipt()
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

        Assert.DoesNotContain(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<ExecutionOperationReceipt>());
        Assert.Equal(0, pump.Snapshot.PendingExecutionCount);
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
    public async Task ExactQueueModuleReferenceExecutesWithoutOutboundReceipt()
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

        Assert.DoesNotContain(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<ExecutionOperationReceipt>());
        Assert.Equal(0, pump.Snapshot.PendingExecutionCount);
    }

    [Fact]
    public async Task AcceptedQueueIsPolledUntilCompletedWithoutOutboundReceipt()
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
        Assert.DoesNotContain(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<ExecutionOperationReceipt>());
        Assert.Equal(0, pump.Snapshot.PendingExecutionCount);
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
    public async Task EndpointRefreshesFullXadbRosterBeforeFirstAndCadencedListingPublication()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        var pump = fixture.CreatePump();
        var rosterReady = false;
        var refreshCalls = 0;
        var publicationCalls = 0;
        var diagnostics = new List<string>();
        var policy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.SpecificCharacter,
            CharacterHandles = ["opaque-local"],
            Enabled = true,
        }.Normalize();
        var listing = new DadAutoPartyListing
        {
            ListingId = Guid.NewGuid().ToString("D"),
            OwnerId = PumpFixture.LocalOwner,
            SharingIslandId = PumpFixture.LocalIsland,
            OpaqueCharacterId = "opaque-local",
            DisplayLabel = "Shared character local",
            AllowedJobIds = ["19"],
            AllowedActivityIds = [DadAutoPartyFreeformRules.FormationActivityId],
            Available = true,
            Revision = 1,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
        };
        using var endpoint = new DadAutoPartyEndpointService(
            fixture.Configuration,
            new UnusedWebhookStore(),
            new UnusedLegacyTokenStore(),
            fixture.Connector,
            static () => { },
            diagnostic: diagnostics.Add,
            identityStore: fixture.IdentityStore,
            listingPublicationProvider: _ =>
            {
                publicationCalls++;
                return new DadAutoPartyListingPublication(policy, [listing]);
            },
            prepareListingPublication: () =>
            {
                refreshCalls++;
                return rosterReady;
            });
        typeof(DadAutoPartyEndpointService).GetField(
            "relayPump",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(endpoint, pump);
        var publish = typeof(DadAutoPartyEndpointService).GetMethod(
            "PublishListingsIfDue",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var first = DateTime.UtcNow;

        publish.Invoke(endpoint, [first]);
        Assert.Equal(1, refreshCalls);
        Assert.Equal(0, publicationCalls);
        Assert.Contains("dad-listing-publication-roster-unavailable", diagnostics);
        var unavailable = endpoint.ListingPublicationSnapshot;
        Assert.True(unavailable.Attempted);
        Assert.False(unavailable.Allowed);
        Assert.Equal(0, unavailable.PublishedOrQueuedListingCount);
        Assert.Equal(first, unavailable.LastAttemptAtUtc);
        Assert.Equal(first.AddSeconds(30), unavailable.NextAttemptAtUtc);
        Assert.Equal(
            "Local sharing blocked: XA Database contract-v6 full roster is unavailable",
            unavailable.OperatorStatus);

        rosterReady = true;
        publish.Invoke(endpoint, [first.AddSeconds(31)]);
        Assert.Equal(2, refreshCalls);
        Assert.Equal(1, publicationCalls);
        var queued = endpoint.ListingPublicationSnapshot;
        Assert.True(queued.Allowed);
        Assert.Equal(1, queued.PublishedOrQueuedListingCount);
        Assert.Equal(first.AddSeconds(31), queued.LastAttemptAtUtc);
        Assert.Equal(first.AddSeconds(31).AddMinutes(5), queued.NextAttemptAtUtc);

        publish.Invoke(endpoint, [first.AddMinutes(5).AddSeconds(32)]);
        Assert.Equal(3, refreshCalls);
        Assert.Equal(2, publicationCalls);

        endpoint.Dispose();
        await pump.DisposeAsync();
    }

    [Fact]
    public async Task ImmediateListingPublicationReportsCurrentRosterFailureAndCount()
    {
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active);
        var pump = fixture.CreatePump();
        var rosterReady = false;
        var publicationCalls = 0;
        var policy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.SpecificCharacter,
            CharacterHandles = ["opaque-local"],
            Enabled = true,
        }.Normalize();
        var listing = new DadAutoPartyListing
        {
            ListingId = Guid.NewGuid().ToString("D"),
            OwnerId = PumpFixture.LocalOwner,
            SharingIslandId = PumpFixture.LocalIsland,
            OpaqueCharacterId = "opaque-local",
            DisplayLabel = "Shared character local",
            AllowedJobIds = ["19"],
            AllowedActivityIds = [DadAutoPartyFreeformRules.FormationActivityId],
            Available = true,
            Revision = 1,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
        };
        using var endpoint = new DadAutoPartyEndpointService(
            fixture.Configuration,
            new UnusedWebhookStore(),
            new UnusedLegacyTokenStore(),
            fixture.Connector,
            static () => { },
            identityStore: fixture.IdentityStore,
            listingPublicationProvider: _ =>
            {
                publicationCalls++;
                return new DadAutoPartyListingPublication(policy, [listing]);
            },
            prepareListingPublication: () => rosterReady);
        typeof(DadAutoPartyEndpointService).GetField(
            "relayPump",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(endpoint, pump);

        var unavailable = endpoint.PublishListingsImmediately();
        Assert.False(unavailable.Allowed);
        Assert.Equal(0, unavailable.PublishedListingCount);
        Assert.Equal(0, publicationCalls);
        var unavailableSnapshot = endpoint.ListingPublicationSnapshot;
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            unavailableSnapshot.NextAttemptAtUtc - unavailableSnapshot.LastAttemptAtUtc);

        rosterReady = true;
        var published = endpoint.PublishListingsImmediately();
        Assert.True(published.Allowed, published.SafeCode);
        Assert.Equal(1, published.PublishedListingCount);
        Assert.Equal(1, publicationCalls);
        var publishedSnapshot = endpoint.ListingPublicationSnapshot;
        Assert.True(publishedSnapshot.Allowed);
        Assert.Equal(1, publishedSnapshot.PublishedOrQueuedListingCount);
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            publishedSnapshot.NextAttemptAtUtc - publishedSnapshot.LastAttemptAtUtc);

        endpoint.Dispose();
        await pump.DisposeAsync();
    }

    [Fact]
    public async Task PairedDirectoryRefreshWaitsForEveryPublicationReceiptAndCountsOnlyActionableCharacters()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump(utcNow: () => now);
        typeof(DadAutoPartyRelayPump).GetField(
                "lastPrivateDirectoryRequestAt",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(pump, now);
        var policy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.AllCharactersForPeer,
            Enabled = true,
            Revision = 3,
            UpdatedAtUtc = now.UtcDateTime,
        }.Normalize();
        var sourceListings = Enumerable.Range(1, 92).Select(index => new DadAutoPartyListing
        {
            ListingId = Guid.NewGuid().ToString("D"),
            OwnerId = PumpFixture.LocalOwner,
            SharingIslandId = PumpFixture.LocalIsland,
            OpaqueCharacterId = $"opaque-local-{index:000}",
            DisplayLabel = $"Shared local character {index:000}",
            AllowedJobIds = ["19"],
            AllowedActivityIds = [DadAutoPartyFreeformRules.FormationActivityId],
            Available = true,
            Revision = 1,
            ExpiresAtUtc = now.AddHours(1).UtcDateTime,
        }.Normalize()).ToList();
        var prepareCalls = 0;
        var publicationCalls = 0;
        using var endpoint = new DadAutoPartyEndpointService(
            fixture.Configuration,
            new UnusedWebhookStore(),
            new UnusedLegacyTokenStore(),
            fixture.Connector,
            static () => { },
            identityStore: fixture.IdentityStore,
            listingPublicationProvider: _ =>
            {
                publicationCalls++;
                Assert.Equal(1, prepareCalls);
                return new DadAutoPartyListingPublication(policy, sourceListings);
            },
            prepareListingPublication: () =>
            {
                prepareCalls++;
                return true;
            });
        typeof(DadAutoPartyEndpointService).GetField(
                "relayPump",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(endpoint, pump);

        var refresh = endpoint.RefreshPairedDirectoryAsync().AsTask();
        for (var cycle = 0; cycle < 16; cycle++)
            await pump.ProcessOnceAsync();
        var updates = fixture.Transport.Sent
            .Where(item => item.PayloadType == ProtocolContractRegistry.GetTypeId<PrivateListingUpdate>())
            .Select(fixture.Open<PrivateListingUpdate>)
            .OrderBy(static update => update.ChunkIndex)
            .ToList();

        var refreshSafeCode = refresh.IsCompleted ? (await refresh).SafeCode : "pending";
        Assert.True(
            updates.Count > 1,
            $"Expected a multi-chunk refresh publication; sent {updates.Count}, pump={pump.Snapshot.SafeCode}, " +
            $"completed={refresh.IsCompleted}:{refreshSafeCode}.");
        Assert.False(refresh.IsCompleted);
        Assert.DoesNotContain(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>());
        foreach (var update in updates.Take(updates.Count - 1))
        {
            fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
                fixture.RelayHeader($"listing-receipt-{update.ChunkIndex}", now),
                Guid.NewGuid(),
                update.Header.MessageId,
                true,
                "listing-update-applied")));
        }
        while (fixture.Transport.Inbound.Count > 0)
            await pump.ProcessOnceAsync();

        Assert.False(refresh.IsCompleted);
        Assert.DoesNotContain(fixture.Transport.Sent, item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>());

        var finalUpdate = updates[^1];
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
            fixture.RelayHeader("listing-receipt-final", now),
            Guid.NewGuid(),
            finalUpdate.Header.MessageId,
            true,
            "listing-update-applied")));
        DirectoryQuery? query = null;
        for (var cycle = 0; cycle < 100 && query == null; cycle++)
        {
            await pump.ProcessOnceAsync();
            query = fixture.Transport.Sent
                .Where(item => item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>())
                .Select(fixture.Open<DirectoryQuery>)
                .LastOrDefault();
            if (query == null)
                await Task.Delay(1);
        }

        Assert.NotNull(query);
        Assert.False(refresh.IsCompleted);
        var listingExpiresAt = now.AddHours(1);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new DirectoryPage(
            fixture.RelayHeader("paired-directory-refresh", now),
            query!.QueryId,
            1,
            false,
            string.Empty,
            [
                new PrivateDirectoryEntry(
                    new OwnerId(PumpFixture.PeerOwner),
                    new IslandId(PumpFixture.PeerIsland),
                    "peer-endpoint",
                    "guild-home",
                    CharacterShareMode.AllCharactersForPeer,
                    "paired-policy-hash",
                    true,
                    [
                        new PrivateCharacterListing(
                            new OpaqueCharacterId("opaque-actionable-one"),
                            "Actionable one",
                            [new JobId("19")],
                            [new ActivityId("dad-duty-1")],
                            true,
                            1,
                            listingExpiresAt),
                        new PrivateCharacterListing(
                            new OpaqueCharacterId("opaque-actionable-two"),
                            "Actionable two",
                            [new JobId("24")],
                            [new ActivityId("dad-duty-1")],
                            true,
                            1,
                            listingExpiresAt),
                        new PrivateCharacterListing(
                            new OpaqueCharacterId("opaque-noncombat"),
                            "Noncombat",
                            [new JobId("999")],
                            [new ActivityId("dad-duty-1")],
                            true,
                            1,
                            listingExpiresAt),
                        new PrivateCharacterListing(
                            new OpaqueCharacterId("opaque-unavailable"),
                            "Unavailable",
                            [new JobId("19")],
                            [new ActivityId("dad-duty-1")],
                            false,
                            1,
                            listingExpiresAt),
                    ],
                    Guid.NewGuid(),
                    1,
                    0,
                    false,
                    1,
                    listingExpiresAt),
            ],
            1)));
        await pump.ProcessOnceAsync();
        var result = await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Allowed, result.SafeCode);
        Assert.Equal("dad-directory-page-applied", result.SafeCode);
        Assert.Equal(sourceListings.Count, result.PublishedListingCount);
        Assert.Equal(2, result.ReceivedListingCount);
        Assert.Equal(1, prepareCalls);
        Assert.Equal(1, publicationCalls);
        var publicationSnapshot = endpoint.ListingPublicationSnapshot;
        Assert.True(publicationSnapshot.Allowed);
        Assert.Equal(sourceListings.Count, publicationSnapshot.PublishedOrQueuedListingCount);
        Assert.NotNull(publicationSnapshot.LastAttemptAtUtc);
        Assert.NotNull(publicationSnapshot.NextAttemptAtUtc);
    }

    [Fact]
    public async Task PairedDirectoryRefreshAcceptsAnEmptyPublicationAndCompletesCancellationTruthfully()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump(utcNow: () => now);
        typeof(DadAutoPartyRelayPump).GetField(
                "lastPrivateDirectoryRequestAt",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(pump, now);
        var policy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.CharacterList,
            Enabled = false,
            CharacterHandles = [],
            Revision = 1,
            UpdatedAtUtc = now.UtcDateTime,
        }.Normalize();
        using var endpoint = new DadAutoPartyEndpointService(
            fixture.Configuration,
            new UnusedWebhookStore(),
            new UnusedLegacyTokenStore(),
            fixture.Connector,
            static () => { },
            identityStore: fixture.IdentityStore,
            listingPublicationProvider: _ => new DadAutoPartyListingPublication(policy, []),
            prepareListingPublication: static () => true);
        typeof(DadAutoPartyEndpointService).GetField(
                "relayPump",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(endpoint, pump);

        var emptyRefresh = endpoint.RefreshPairedDirectoryAsync().AsTask();
        await pump.ProcessOnceAsync();
        var emptyUpdates = fixture.Transport.Sent.Where(item =>
                item.PayloadType == ProtocolContractRegistry.GetTypeId<PrivateListingUpdate>())
            .ToList();
        var emptyRefreshSafeCode = emptyRefresh.IsCompleted ? (await emptyRefresh).SafeCode : "pending";
        Assert.True(
            emptyUpdates.Count == 1,
            $"Expected one empty publication; sent {emptyUpdates.Count}, pump={pump.Snapshot.SafeCode}, " +
            $"completed={emptyRefresh.IsCompleted}:{emptyRefreshSafeCode}.");
        var emptyUpdate = fixture.Open<PrivateListingUpdate>(emptyUpdates[0]);
        Assert.Empty(emptyUpdate.Listings);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
            fixture.RelayHeader("empty-listing-receipt", now),
            Guid.NewGuid(),
            emptyUpdate.Header.MessageId,
            true,
            "listing-update-applied")));
        DirectoryQuery? query = null;
        for (var cycle = 0; cycle < 100 && query == null; cycle++)
        {
            await pump.ProcessOnceAsync();
            query = fixture.Transport.Sent
                .Where(item => item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>())
                .Select(fixture.Open<DirectoryQuery>)
                .LastOrDefault();
            if (query == null)
                await Task.Delay(1);
        }
        Assert.NotNull(query);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new DirectoryPage(
            fixture.RelayHeader("empty-directory-page", now),
            query!.QueryId,
            1,
            false,
            string.Empty,
            [],
            1)));
        await pump.ProcessOnceAsync();
        var emptyResult = await emptyRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(emptyResult.Allowed, emptyResult.SafeCode);
        Assert.Equal(0, emptyResult.PublishedListingCount);
        Assert.Equal(0, emptyResult.ReceivedListingCount);
        Assert.Equal(
            "No peer listings received. Inspect the peer DAD's local-sharing status.",
            emptyResult.OperatorStatus);
        var emptyPublication = endpoint.ListingPublicationSnapshot;
        Assert.True(emptyPublication.Allowed);
        Assert.Equal(0, emptyPublication.PublishedOrQueuedListingCount);

        using var cancellation = new CancellationTokenSource();
        var cancelledRefresh = endpoint.RefreshPairedDirectoryAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        var cancelled = await cancelledRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(cancelled.Allowed);
        Assert.Equal("dad-paired-directory-refresh-cancelled", cancelled.SafeCode);
        Assert.Equal(0, cancelled.PublishedListingCount);
        Assert.Equal(0, cancelled.ReceivedListingCount);

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        var preCancelled = await endpoint.RefreshPairedDirectoryAsync(alreadyCancelled.Token);
        Assert.False(preCancelled.Allowed);
        Assert.Equal("dad-paired-directory-refresh-cancelled", preCancelled.SafeCode);
        Assert.Equal(0, preCancelled.PublishedListingCount);
        Assert.Equal(0, preCancelled.ReceivedListingCount);
    }

    [Fact]
    public async Task PairedDirectoryRefreshRejectsNegativePublicationReceiptBeforeDirectoryQuery()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump(utcNow: () => now);
        using var endpoint = CreatePairedDirectoryRefreshEndpoint(fixture, pump, now);
        var sentBefore = fixture.Transport.Sent.Count;

        var refresh = endpoint.RefreshPairedDirectoryAsync().AsTask();
        var update = await WaitForSentContractAsync<PrivateListingUpdate>(fixture, pump, sentBefore);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
            fixture.RelayHeader("listing-refresh-rejected", now),
            Guid.NewGuid(),
            update.Header.MessageId,
            false,
            "listing-update-rejected")));
        await pump.ProcessOnceAsync();
        var result = await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Allowed);
        Assert.Equal("listing-update-rejected", result.SafeCode);
        Assert.Equal(0, result.PublishedListingCount);
        Assert.Equal(0, result.ReceivedListingCount);
        var publication = endpoint.ListingPublicationSnapshot;
        Assert.False(publication.Allowed);
        Assert.Equal("listing-update-rejected", publication.SafeCode);
        Assert.Equal(0, publication.PublishedOrQueuedListingCount);
        Assert.NotNull(publication.NextAttemptAtUtc);
        Assert.DoesNotContain(fixture.Transport.Sent.Skip(sentBefore), item =>
            item.PayloadType == ProtocolContractRegistry.GetTypeId<DirectoryQuery>());
    }

    [Fact]
    public async Task PairedDirectoryRefreshRejectsMalformedFinalPageAfterAcceptedPublication()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
        await using var pump = fixture.CreatePump(utcNow: () => now);
        using var endpoint = CreatePairedDirectoryRefreshEndpoint(fixture, pump, now);
        var sentBefore = fixture.Transport.Sent.Count;

        var refresh = endpoint.RefreshPairedDirectoryAsync().AsTask();
        var update = await WaitForSentContractAsync<PrivateListingUpdate>(fixture, pump, sentBefore);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
            fixture.RelayHeader("listing-refresh-accepted", now),
            Guid.NewGuid(),
            update.Header.MessageId,
            true,
            "listing-update-applied")));
        var queryStart = fixture.Transport.Sent.Count;
        var query = await WaitForSentContractAsync<DirectoryQuery>(fixture, pump, queryStart);
        fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new DirectoryPage(
            fixture.RelayHeader("directory-refresh-mixed", now),
            query.QueryId,
            2,
            false,
            string.Empty,
            [],
            1)));
        await pump.ProcessOnceAsync();
        var result = await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Allowed);
        Assert.Equal("dad-directory-page-mixed", result.SafeCode);
        Assert.Equal(1, result.PublishedListingCount);
        Assert.Equal(0, result.ReceivedListingCount);
    }

    [Fact]
    public async Task PairedDirectoryRefreshCompletesListingAndQueryExpiryTruthfully()
    {
        {
            var now = DateTimeOffset.UtcNow;
            using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
            await using var pump = fixture.CreatePump(utcNow: () => now);
            using var endpoint = CreatePairedDirectoryRefreshEndpoint(fixture, pump, now);
            var sentBefore = fixture.Transport.Sent.Count;

            var refresh = endpoint.RefreshPairedDirectoryAsync().AsTask();
            var update = await WaitForSentContractAsync<PrivateListingUpdate>(fixture, pump, sentBefore);
            now = update.Header.ExpiresAt.AddSeconds(1);
            await pump.ProcessOnceAsync();
            var result = await refresh.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(result.Allowed);
            Assert.Equal("dad-listing-publication-expired", result.SafeCode);
            Assert.Equal(0, result.PublishedListingCount);
            Assert.Equal(0, result.ReceivedListingCount);
        }

        {
            var now = DateTimeOffset.UtcNow;
            using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
            await using var pump = fixture.CreatePump(utcNow: () => now);
            using var endpoint = CreatePairedDirectoryRefreshEndpoint(fixture, pump, now);
            var sentBefore = fixture.Transport.Sent.Count;

            var refresh = endpoint.RefreshPairedDirectoryAsync().AsTask();
            var update = await WaitForSentContractAsync<PrivateListingUpdate>(fixture, pump, sentBefore);
            fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
                fixture.RelayHeader("listing-before-query-expiry", now),
                Guid.NewGuid(),
                update.Header.MessageId,
                true,
                "listing-update-applied")));
            var queryStart = fixture.Transport.Sent.Count;
            var query = await WaitForSentContractAsync<DirectoryQuery>(fixture, pump, queryStart);
            now = query.Header.ExpiresAt.AddSeconds(1);
            await pump.ProcessOnceAsync();
            var result = await refresh.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(result.Allowed);
            Assert.Equal("dad-directory-query-expired", result.SafeCode);
            Assert.Equal(1, result.PublishedListingCount);
            Assert.Equal(0, result.ReceivedListingCount);
        }
    }

    [Fact]
    public async Task PairedDirectoryRefreshCompletesSecurityResetDeregistrationAndDisposal()
    {
        {
            var now = DateTimeOffset.UtcNow;
            using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
            await using var pump = fixture.CreatePump(utcNow: () => now);
            using var endpoint = CreatePairedDirectoryRefreshEndpoint(fixture, pump, now);
            var sentBefore = fixture.Transport.Sent.Count;

            var refresh = endpoint.RefreshPairedDirectoryAsync().AsTask();
            var update = await WaitForSentContractAsync<PrivateListingUpdate>(fixture, pump, sentBefore);
            fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
                fixture.RelayHeader("listing-before-security-reset", now),
                Guid.NewGuid(),
                update.Header.MessageId,
                true,
                "listing-update-applied")));
            var queryStart = fixture.Transport.Sent.Count;
            _ = await WaitForSentContractAsync<DirectoryQuery>(fixture, pump, queryStart);
            fixture.Configuration.EndpointIdentityReference =
                "identity-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            await pump.ProcessOnceAsync();
            var result = await refresh.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(result.Allowed);
            Assert.Equal("dad-directory-query-reset", result.SafeCode);
            Assert.Equal(1, result.PublishedListingCount);
            Assert.Equal(0, result.ReceivedListingCount);
        }

        {
            var now = DateTimeOffset.UtcNow;
            using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
            await using var pump = fixture.CreatePump(utcNow: () => now);
            pump.ConfigureLifecycleHandlers(
                static _ => new(false, "unused", 1),
                (receipt, pending, _) =>
                {
                    Assert.Equal(pending.DeregistrationId, receipt.DeregistrationId);
                    fixture.Configuration.RegistrationState = DadAutoPartyRegistrationState.Unregistered;
                    return ValueTask.FromResult(new DadAutoPartyPrivacyResult(
                        true,
                        false,
                        "dad-deregistered"));
                });
            using var endpoint = CreatePairedDirectoryRefreshEndpoint(fixture, pump, now);
            var sentBefore = fixture.Transport.Sent.Count;

            var refresh = endpoint.RefreshPairedDirectoryAsync().AsTask();
            var update = await WaitForSentContractAsync<PrivateListingUpdate>(fixture, pump, sentBefore);
            fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
                fixture.RelayHeader("listing-before-deregistration", now),
                Guid.NewGuid(),
                update.Header.MessageId,
                true,
                "listing-update-applied")));
            var queryStart = fixture.Transport.Sent.Count;
            _ = await WaitForSentContractAsync<DirectoryQuery>(fixture, pump, queryStart);
            var queued = pump.BeginDeregistration(false);
            Assert.True(queued.Allowed, queued.SafeCode);
            var pending = Assert.IsType<DadAutoPartyPendingDeregistration>(
                fixture.PendingStore.LoadDeregistration());
            fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new DeregistrationReceipt(
                fixture.RelayHeader("refresh-deregistration", now),
                pending.DeregistrationId,
                new IslandId(PumpFixture.LocalIsland),
                true,
                2,
                "dad-deregistered")));
            await pump.ProcessOnceAsync();
            var result = await refresh.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(result.Allowed);
            Assert.Equal("dad-directory-query-deregistered", result.SafeCode);
            Assert.Equal(1, result.PublishedListingCount);
            Assert.Equal(0, result.ReceivedListingCount);
        }

        {
            var now = DateTimeOffset.UtcNow;
            using var fixture = new PumpFixture(DadAutoPartyRegistrationState.Active, includePeer: true);
            await using var pump = fixture.CreatePump(utcNow: () => now);
            using var endpoint = CreatePairedDirectoryRefreshEndpoint(fixture, pump, now);
            var sentBefore = fixture.Transport.Sent.Count;

            var refresh = endpoint.RefreshPairedDirectoryAsync().AsTask();
            var update = await WaitForSentContractAsync<PrivateListingUpdate>(fixture, pump, sentBefore);
            fixture.Transport.Inbound.Enqueue(fixture.SealRelay(new RelayReceipt(
                fixture.RelayHeader("listing-before-disposal", now),
                Guid.NewGuid(),
                update.Header.MessageId,
                true,
                "listing-update-applied")));
            var queryStart = fixture.Transport.Sent.Count;
            _ = await WaitForSentContractAsync<DirectoryQuery>(fixture, pump, queryStart);
            await pump.DisposeAsync();
            var result = await refresh.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(result.Allowed);
            Assert.Equal("dad-directory-query-disposed", result.SafeCode);
            Assert.Equal(1, result.PublishedListingCount);
            Assert.Equal(0, result.ReceivedListingCount);
        }
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
                true,
                3);

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

    private static DadAutoPartyEndpointService CreatePairedDirectoryRefreshEndpoint(
        PumpFixture fixture,
        DadAutoPartyRelayPump pump,
        DateTimeOffset now)
    {
        typeof(DadAutoPartyRelayPump).GetField(
                "lastPrivateDirectoryRequestAt",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(pump, now);
        var endpoint = new DadAutoPartyEndpointService(
            fixture.Configuration,
            new UnusedWebhookStore(),
            new UnusedLegacyTokenStore(),
            fixture.Connector,
            static () => { },
            identityStore: fixture.IdentityStore,
            listingPublicationProvider: observedAt =>
            {
                var policy = new DadAutoPartySharePolicy
                {
                    Mode = DadAutoPartyCharacterShareMode.AllCharactersForPeer,
                    Enabled = true,
                    Revision = 1,
                    UpdatedAtUtc = observedAt,
                }.Normalize();
                var listing = new DadAutoPartyListing
                {
                    ListingId = Guid.NewGuid().ToString("D"),
                    OwnerId = PumpFixture.LocalOwner,
                    SharingIslandId = PumpFixture.LocalIsland,
                    OpaqueCharacterId = "opaque-refresh-local",
                    DisplayLabel = "Shared refresh character",
                    AllowedJobIds = ["19"],
                    AllowedActivityIds = [DadAutoPartyFreeformRules.FormationActivityId],
                    Available = true,
                    Revision = 1,
                    ExpiresAtUtc = observedAt.AddHours(1),
                }.Normalize();
                return new DadAutoPartyListingPublication(policy, [listing]);
            },
            prepareListingPublication: static () => true);
        typeof(DadAutoPartyEndpointService).GetField(
                "relayPump",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(endpoint, pump);
        return endpoint;
    }

    private static async Task<T> WaitForSentContractAsync<T>(
        PumpFixture fixture,
        DadAutoPartyRelayPump pump,
        int startIndex)
        where T : class, IAutoPartyContract
    {
        for (var cycle = 0; cycle < 100; cycle++)
        {
            await pump.ProcessOnceAsync();
            var envelope = fixture.Transport.Sent
                .Skip(startIndex)
                .FirstOrDefault(item => item.PayloadType == ProtocolContractRegistry.GetTypeId<T>());
            if (envelope != null)
                return fixture.Open<T>(envelope);
            await Task.Delay(1);
        }

        throw new Xunit.Sdk.XunitException($"No {typeof(T).Name} was sent.");
    }

    private static DadAutoPartySharePolicy CreateListingPolicy(DateTimeOffset now, long revision)
        => new()
        {
            Mode = DadAutoPartyCharacterShareMode.AllCharactersForPeer,
            Enabled = true,
            Revision = revision,
            UpdatedAtUtc = now.UtcDateTime,
        };

    private static DadAutoPartyListing CreateListing(DateTimeOffset now, string handle, long revision)
        => new()
        {
            ListingId = Guid.NewGuid().ToString("D"),
            OwnerId = PumpFixture.LocalOwner,
            SharingIslandId = PumpFixture.LocalIsland,
            OpaqueCharacterId = handle,
            DisplayLabel = $"Shared character {handle}",
            AllowedJobIds = ["19"],
            AllowedActivityIds = [DadAutoPartyFreeformRules.FormationActivityId],
            Available = true,
            Revision = revision,
            ExpiresAtUtc = now.UtcDateTime.AddHours(2),
        };

    private static IReadOnlyList<PrivateListingUpdate> SentListingUpdates(PumpFixture fixture)
        => fixture.Transport.Sent
            .Where(item => item.PayloadType == ProtocolContractRegistry.GetTypeId<PrivateListingUpdate>())
            .Select(fixture.Open<PrivateListingUpdate>)
            .ToList();

    private sealed class TwoEndpointConvergenceFixture : IAsyncDisposable
    {
        private const string EndpointAOwner = "owner-endpoint-a";
        private const string EndpointAIsland = "island-endpoint-a";
        private const string EndpointBOwner = "owner-endpoint-b";
        private const string EndpointBIsland = "island-endpoint-b";
        private const long EndpointAKeyVersion = 1;
        private const long RelayKeyVersion = 2;
        private const long EndpointBKeyVersion = 3;
        private readonly ConvergenceKeyMaterial keys = new();
        private readonly ProductionContractAuthenticator authenticator;
        private readonly Dictionary<string, StagedSnapshot> staged = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ActiveSnapshot> active = new(StringComparer.Ordinal);
        private int endpointASentOffset;
        private int endpointBSentOffset;
        private long relaySequence;
        private DateTimeOffset now;

        public TwoEndpointConvergenceFixture(DateTimeOffset observedAt)
        {
            now = observedAt;
            var pairingId = Guid.Parse("896a958b-220a-4d19-92bb-a9f4bf3eaa3c");
            const string transcriptHash =
                "7a5f6f40467e48cca05fe261564987e2d3d49339955890ab3d92e26ed768b720";
            EndpointA = new ConvergenceEndpoint(
                EndpointAOwner,
                EndpointAIsland,
                "endpoint-a",
                "guild-endpoint-a",
                EndpointAKeyVersion,
                keys.EndpointASigningPrivate,
                keys.EndpointAAgreementPrivate,
                EndpointBOwner,
                EndpointBIsland,
                "guild-endpoint-b",
                EndpointBKeyVersion,
                keys.EndpointBSigningPublic,
                keys.EndpointBAgreementPublic,
                keys.RelaySigningPublic,
                keys.RelayAgreementPublic,
                pairingId,
                transcriptHash,
                () => now);
            EndpointB = new ConvergenceEndpoint(
                EndpointBOwner,
                EndpointBIsland,
                "endpoint-b",
                "guild-endpoint-b",
                EndpointBKeyVersion,
                keys.EndpointBSigningPrivate,
                keys.EndpointBAgreementPrivate,
                EndpointAOwner,
                EndpointAIsland,
                "guild-endpoint-a",
                EndpointAKeyVersion,
                keys.EndpointASigningPublic,
                keys.EndpointAAgreementPublic,
                keys.RelaySigningPublic,
                keys.RelayAgreementPublic,
                pairingId,
                transcriptHash,
                () => now);
            authenticator = new ProductionContractAuthenticator(new ConvergenceResolver(keys));
        }

        public ConvergenceEndpoint EndpointA { get; }

        public ConvergenceEndpoint EndpointB { get; }

        public List<DeliveredEnvelope> Delivered { get; } = [];

        public void Advance(TimeSpan elapsed) => now += elapsed;

        public DadAutoPartyListingPublication BuildPublication(
            ConvergenceEndpoint endpoint,
            string opaqueCharacterId,
            string characterName,
            string worldName)
        {
            var participant = new DadParticipantSnapshot
            {
                ClientInstanceId = $"client-{endpoint.EndpointAlias}",
                WorkerSessionId = new DadWorkerSessionId($"worker-{endpoint.EndpointAlias}"),
                IsLocalClient = true,
                ManagedAccountKey = new DadAccountKey($"account-{endpoint.EndpointAlias}"),
            };
            var route = new DadAutoPartyInboundRoute(
                opaqueCharacterId,
                participant.ManagedAccountKey,
                new DadCharacterKey($"{characterName}@{worldName}"),
                1,
                characterName,
                1,
                worldName,
                participant.WorkerSessionId,
                participant.ClientInstanceId,
                participant,
                now);
            var candidate = new DadAutoPartyCrewCandidate(
                new DadAutoPartyCrewIdentity
                {
                    RosterIdentityKey = $"roster-{opaqueCharacterId}",
                    OpaqueCharacterId = opaqueCharacterId,
                },
                new DadAcquiredCharacter(),
                [19],
                Available: true,
                InboundRoute: route);
            return DadAutoPartyListingPublicationRules.Build(
                endpoint.Configuration,
                [candidate],
                [],
                now.UtcDateTime);
        }

        public ActiveSnapshot GetActiveSnapshot(ConvergenceEndpoint endpoint) =>
            active.TryGetValue(endpoint.IslandId, out var snapshot)
                ? snapshot
                : throw new InvalidOperationException("convergence-snapshot-not-active");

        public async Task DrainAsync()
        {
            var quietCycles = 0;
            for (var cycle = 0; cycle < 128; cycle++)
            {
                var before = ActivityCount();
                await EndpointA.Pump.ProcessOnceAsync();
                EndpointA.ObservedSafeCodes.Add(EndpointA.Pump.Snapshot.SafeCode);
                await EndpointB.Pump.ProcessOnceAsync();
                EndpointB.ObservedSafeCodes.Add(EndpointB.Pump.Snapshot.SafeCode);
                DispatchNewOutbound(EndpointA, ref endpointASentOffset);
                DispatchNewOutbound(EndpointB, ref endpointBSentOffset);
                var after = ActivityCount();
                if (before == after &&
                    EndpointA.Transport.Inbound.Count == 0 &&
                    EndpointB.Transport.Inbound.Count == 0 &&
                    endpointASentOffset == EndpointA.Transport.Sent.Count &&
                    endpointBSentOffset == EndpointB.Transport.Sent.Count)
                {
                    quietCycles++;
                    if (quietCycles >= 4)
                        return;
                }
                else
                {
                    quietCycles = 0;
                }
            }

            throw new TimeoutException("two-endpoint-convergence-did-not-quiesce");
        }

        public async ValueTask DisposeAsync()
        {
            await EndpointA.DisposeAsync();
            await EndpointB.DisposeAsync();
            keys.Dispose();
        }

        private int ActivityCount() =>
            EndpointA.Transport.Sent.Count +
            EndpointB.Transport.Sent.Count +
            EndpointA.Transport.Acknowledged.Count +
            EndpointB.Transport.Acknowledged.Count +
            EndpointA.Transport.Inbound.Count +
            EndpointB.Transport.Inbound.Count +
            Delivered.Count +
            active.Count;

        private void DispatchNewOutbound(ConvergenceEndpoint source, ref int sentOffset)
        {
            while (sentOffset < source.Transport.Sent.Count)
            {
                var envelope = source.Transport.Sent[sentOffset++];
                if (string.Equals(
                        envelope.PayloadType,
                        ProtocolContractRegistry.GetTypeId<PrivateListingUpdate>(),
                        StringComparison.Ordinal))
                {
                    StageListing(source, Open<PrivateListingUpdate>(envelope));
                    continue;
                }

                if (string.Equals(
                        envelope.PayloadType,
                        ProtocolContractRegistry.GetTypeId<DirectoryQuery>(),
                        StringComparison.Ordinal))
                {
                    RouteDirectory(source, Open<DirectoryQuery>(envelope));
                    continue;
                }

                if (string.Equals(
                        envelope.PayloadType,
                        ProtocolContractRegistry.GetTypeId<PairedListingLabelRequest>(),
                        StringComparison.Ordinal) ||
                    string.Equals(
                        envelope.PayloadType,
                        ProtocolContractRegistry.GetTypeId<PairedListingLabelResponse>(),
                        StringComparison.Ordinal))
                {
                    Deliver(
                        FindEndpoint(envelope.RecipientIslandId.Value),
                        envelope);
                    continue;
                }

                throw new InvalidOperationException($"unexpected-convergence-contract:{envelope.PayloadType}");
            }
        }

        private void StageListing(ConvergenceEndpoint source, PrivateListingUpdate update)
        {
            if (!string.Equals(update.SharingIslandId.Value, source.IslandId, StringComparison.Ordinal))
                throw new InvalidDataException("convergence-listing-island-mismatch");
            var stageKey = $"{source.IslandId}\n{update.SnapshotId:D}";
            if (!staged.TryGetValue(stageKey, out var pending))
            {
                pending = new StagedSnapshot(update.ChunkCount);
                staged.Add(stageKey, pending);
            }
            if (pending.ChunkCount != update.ChunkCount ||
                update.ChunkIndex is < 1 || update.ChunkIndex > update.ChunkCount)
            {
                throw new InvalidDataException("convergence-listing-chunk-invalid");
            }
            pending.Chunks[update.ChunkIndex] = update;
            if (pending.Chunks.Count == pending.ChunkCount)
            {
                var chunks = pending.Chunks.OrderBy(static item => item.Key)
                    .Select(static item => item.Value)
                    .ToArray();
                var snapshot = new ActiveSnapshot(
                    update.SnapshotId,
                    update.SnapshotRevision,
                    update.DirectoryGeneration,
                    chunks.SelectMany(static chunk => chunk.Listings).ToImmutableArray());
                if (!active.TryGetValue(source.IslandId, out var current) ||
                    snapshot.SnapshotRevision >= current.SnapshotRevision)
                {
                    active[source.IslandId] = snapshot;
                }
                staged.Remove(stageKey);
            }

            var receipt = new RelayReceipt(
                CreateRelayHeader(
                    source,
                    $"listing-receipt-{update.Header.MessageId:N}",
                    Min(update.Header.ExpiresAt, now.AddMinutes(5))),
                Guid.NewGuid(),
                update.Header.MessageId,
                true,
                "listing-update-applied");
            Deliver(source, Seal(receipt));
        }

        private void RouteDirectory(ConvergenceEndpoint requester, DirectoryQuery query)
        {
            var sharing = ReferenceEquals(requester, EndpointA) ? EndpointB : EndpointA;
            var entries = active.TryGetValue(sharing.IslandId, out var snapshot)
                ? ImmutableArray.Create(new PrivateDirectoryEntry(
                    new OwnerId(sharing.OwnerId),
                    new IslandId(sharing.IslandId),
                    sharing.EndpointAlias,
                    sharing.HomeGuildScope,
                    CharacterShareMode.AllCharactersForPeer,
                    $"paired-policy-{snapshot.SnapshotRevision}",
                    true,
                    snapshot.Listings,
                    snapshot.SnapshotId,
                    snapshot.SnapshotRevision,
                    0,
                    false,
                    snapshot.DirectoryGeneration,
                    snapshot.Listings.Min(static listing => listing.ExpiresAt)))
                : ImmutableArray<PrivateDirectoryEntry>.Empty;
            var directoryGeneration = entries.IsEmpty ? 1 : entries[0].DirectoryGeneration;
            var page = new DirectoryPage(
                CreateRelayHeader(
                    requester,
                    $"directory-page-{query.QueryId:N}",
                    Min(query.Header.ExpiresAt, now.AddMinutes(5))),
                query.QueryId,
                1,
                false,
                string.Empty,
                entries,
                directoryGeneration);
            Deliver(requester, Seal(page));
        }

        private ConvergenceEndpoint FindEndpoint(string islandId)
        {
            if (string.Equals(islandId, EndpointA.IslandId, StringComparison.Ordinal))
                return EndpointA;
            if (string.Equals(islandId, EndpointB.IslandId, StringComparison.Ordinal))
                return EndpointB;
            throw new InvalidOperationException("convergence-recipient-unknown");
        }

        private T Open<T>(OpaqueEnvelope envelope)
            where T : IAutoPartyContract
        {
            var opened = authenticator.Open<T>(
                SealedContractCodec.Decode(envelope.Ciphertext.AsMemory()));
            return opened is { Succeeded: true, Message: not null }
                ? opened.Message.Contract
                : throw new InvalidDataException("convergence-contract-open-failed");
        }

        private OpaqueEnvelope Seal<T>(T contract)
            where T : IAutoPartyContract
        {
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

        private ContractHeader CreateRelayHeader(
            ConvergenceEndpoint recipient,
            string purpose,
            DateTimeOffset expiresAt)
        {
            var messageId = Guid.NewGuid();
            return new ContractHeader(
                AutoPartyProtocol.CurrentVersion,
                messageId,
                $"{purpose}-{messageId:N}",
                new IslandId(DadAutoPartyIdentityPackageService.RegistrationRecipient),
                new IslandId(recipient.IslandId),
                now,
                expiresAt,
                Interlocked.Increment(ref relaySequence),
                Math.Max(1, recipient.Configuration.StateGeneration),
                RelayKeyVersion,
                recipient.KeyVersion,
                ContractHeader.CreateNonce(
                    messageId.ToByteArray().AsSpan(0, AutoPartyProtocol.ContractNonceBytes)),
                []);
        }

        private void Deliver(ConvergenceEndpoint recipient, OpaqueEnvelope envelope)
        {
            recipient.Transport.Inbound.Enqueue(envelope);
            Delivered.Add(new DeliveredEnvelope(recipient, envelope));
        }

        private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) =>
            first <= second ? first : second;

        public sealed record ActiveSnapshot(
            Guid SnapshotId,
            long SnapshotRevision,
            long DirectoryGeneration,
            ImmutableArray<PrivateCharacterListing> Listings);

        public sealed record DeliveredEnvelope(
            ConvergenceEndpoint Recipient,
            OpaqueEnvelope Envelope);

        private sealed class StagedSnapshot(int chunkCount)
        {
            public int ChunkCount { get; } = chunkCount;

            public Dictionary<int, PrivateListingUpdate> Chunks { get; } = [];
        }

        public sealed class ConvergenceEndpoint : IAsyncDisposable
        {
            private readonly MemoryIdentityStore identityStore;
            private readonly DadDiscordCourierConnector connector;

            public ConvergenceEndpoint(
                string ownerId,
                string islandId,
                string endpointAlias,
                string homeGuildScope,
                long keyVersion,
                byte[] signingPrivate,
                byte[] agreementPrivate,
                string peerOwnerId,
                string peerIslandId,
                string peerHomeGuildScope,
                long peerKeyVersion,
                byte[] peerSigningPublic,
                byte[] peerAgreementPublic,
                byte[] relaySigningPublic,
                byte[] relayAgreementPublic,
                Guid pairingId,
                string transcriptHash,
                Func<DateTimeOffset> utcNow)
            {
                OwnerId = ownerId;
                IslandId = islandId;
                EndpointAlias = endpointAlias;
                HomeGuildScope = homeGuildScope;
                KeyVersion = keyVersion;
                var signingPublic = BouncyCastlePrimitives.DeriveEd25519PublicKey(signingPrivate);
                var agreementPublic = BouncyCastlePrimitives.DeriveX25519PublicKey(agreementPrivate);
                var localFingerprint = DadAutoPartyIdentityPackageService.BuildFingerprint(
                    ownerId,
                    islandId,
                    keyVersion,
                    signingPublic,
                    agreementPublic);
                var peerFingerprint = DadAutoPartyIdentityPackageService.BuildFingerprint(
                    peerOwnerId,
                    peerIslandId,
                    peerKeyVersion,
                    peerSigningPublic,
                    peerAgreementPublic);
                Configuration = new DadAutoPartyConfiguration
                {
                    Enabled = true,
                    RegistrationState = DadAutoPartyRegistrationState.Active,
                    RegistrationId = Guid.NewGuid().ToString("D"),
                    RouteId = $"route-{endpointAlias}",
                    CentralBotApplicationId = "123456789012345678",
                    HomeGuildScope = homeGuildScope,
                    WebhookCredentialReference = $"webhook-{endpointAlias}-aaaaaaaaaaaaaaaaaaaaaaaa",
                    UplinkEpochId = Guid.NewGuid().ToString("D"),
                    DownlinkEpochId = Guid.NewGuid().ToString("D"),
                    MailboxEpochGeneration = 1,
                    DirectoryGeneration = 1,
                    RelayKeyGeneration = RelayKeyVersion,
                    RelaySigningPublicKey = Convert.ToBase64String(relaySigningPublic),
                    RelayAgreementPublicKey = Convert.ToBase64String(relayAgreementPublic),
                    EndpointIdentityReference = $"identity-{endpointAlias}-aaaaaaaaaaaaaaaaaaaaaaaa",
                    RegisteredOwnerId = ownerId,
                    RegisteredIslandId = islandId,
                    RegistrationFingerprint = localFingerprint,
                    EndpointAlias = endpointAlias,
                    SigningPublicKey = Convert.ToBase64String(signingPublic),
                    EncryptionPublicKey = Convert.ToBase64String(agreementPublic),
                    EndpointKeyGeneration = keyVersion,
                    StateGeneration = 1,
                };
                Configuration.Pairings.Add(new DadAutoPartyPairing
                {
                    PairingId = pairingId.ToString("D"),
                    OwnerId = peerOwnerId,
                    IslandId = peerIslandId,
                    HomeGuildScope = peerHomeGuildScope,
                    PublicKeyFingerprint = peerFingerprint,
                    LocalFingerprint = localFingerprint,
                    TranscriptHash = transcriptHash,
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
                    ExpiresAtUtc = utcNow().AddDays(1).UtcDateTime,
                    KeyGeneration = peerKeyVersion,
                    SigningPublicKey = Convert.ToBase64String(peerSigningPublic),
                    AgreementPublicKey = Convert.ToBase64String(peerAgreementPublic),
                    ConfirmedAtUtc = utcNow().UtcDateTime,
                });
                var identity = new DadAutoPartyPrivateIdentityPackage(
                    ownerId,
                    islandId,
                    keyVersion,
                    Convert.ToBase64String(signingPrivate),
                    Convert.ToBase64String(agreementPrivate));
                identityStore = new MemoryIdentityStore(JsonSerializer.SerializeToUtf8Bytes(identity));
                Transport = new FakeTransport();
                connector = new DadDiscordCourierConnector(Configuration, static () => true);
                connector.AttachVerifiedAdapter(Transport);
                Service = new DadAutoPartyService(
                    Configuration,
                    identityStore,
                    static () => true,
                    static () => { });
                var bridge = new DadAutoPartyParticipantBridge(
                    Configuration,
                    useFrenRiderProvider: static () => false);
                Pump = new DadAutoPartyRelayPump(
                    Configuration,
                    identityStore,
                    connector,
                    Service,
                    bridge,
                    new MemoryPendingStore(),
                    utcNow: utcNow,
                    delay: static (_, _) => Task.CompletedTask);
                typeof(DadAutoPartyRelayPump).GetField(
                        "lastPrivateDirectoryRequestAt",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(Pump, utcNow());
                CryptographicOperations.ZeroMemory(signingPublic);
                CryptographicOperations.ZeroMemory(agreementPublic);
            }

            public string OwnerId { get; }

            public string IslandId { get; }

            public string EndpointAlias { get; }

            public string HomeGuildScope { get; }

            public long KeyVersion { get; }

            public DadAutoPartyConfiguration Configuration { get; }

            public FakeTransport Transport { get; }

            public DadAutoPartyService Service { get; }

            public DadAutoPartyRelayPump Pump { get; }

            public List<string> ObservedSafeCodes { get; } = [];

            public async ValueTask DisposeAsync()
            {
                await Pump.DisposeAsync();
                await connector.DisposeAsync();
                Service.Dispose();
                identityStore.Dispose();
            }
        }

        private sealed class ConvergenceKeyMaterial : IDisposable
        {
            public byte[] RelaySigningPrivate { get; } = RandomNumberGenerator.GetBytes(32);
            public byte[] RelayAgreementPrivate { get; } = RandomNumberGenerator.GetBytes(32);
            public byte[] EndpointASigningPrivate { get; } = RandomNumberGenerator.GetBytes(32);
            public byte[] EndpointAAgreementPrivate { get; } = RandomNumberGenerator.GetBytes(32);
            public byte[] EndpointBSigningPrivate { get; } = RandomNumberGenerator.GetBytes(32);
            public byte[] EndpointBAgreementPrivate { get; } = RandomNumberGenerator.GetBytes(32);
            public byte[] RelaySigningPublic { get; }
            public byte[] RelayAgreementPublic { get; }
            public byte[] EndpointASigningPublic { get; }
            public byte[] EndpointAAgreementPublic { get; }
            public byte[] EndpointBSigningPublic { get; }
            public byte[] EndpointBAgreementPublic { get; }

            public ConvergenceKeyMaterial()
            {
                RelaySigningPublic = BouncyCastlePrimitives.DeriveEd25519PublicKey(RelaySigningPrivate);
                RelayAgreementPublic = BouncyCastlePrimitives.DeriveX25519PublicKey(RelayAgreementPrivate);
                EndpointASigningPublic = BouncyCastlePrimitives.DeriveEd25519PublicKey(EndpointASigningPrivate);
                EndpointAAgreementPublic = BouncyCastlePrimitives.DeriveX25519PublicKey(EndpointAAgreementPrivate);
                EndpointBSigningPublic = BouncyCastlePrimitives.DeriveEd25519PublicKey(EndpointBSigningPrivate);
                EndpointBAgreementPublic = BouncyCastlePrimitives.DeriveX25519PublicKey(EndpointBAgreementPrivate);
            }

            public void Dispose()
            {
                foreach (var key in new[]
                         {
                             RelaySigningPrivate,
                             RelayAgreementPrivate,
                             EndpointASigningPrivate,
                             EndpointAAgreementPrivate,
                             EndpointBSigningPrivate,
                             EndpointBAgreementPrivate,
                             RelaySigningPublic,
                             RelayAgreementPublic,
                             EndpointASigningPublic,
                             EndpointAAgreementPublic,
                             EndpointBSigningPublic,
                             EndpointBAgreementPublic,
                         })
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
        }

        private sealed class ConvergenceResolver(ConvergenceKeyMaterial keys) : IContractKeyResolver
        {
            public bool TryGetEd25519PrivateKey(
                IslandId islandId,
                long version,
                out ReadOnlyMemory<byte> key) =>
                Select(
                    islandId,
                    version,
                    keys.RelaySigningPrivate,
                    keys.EndpointASigningPrivate,
                    keys.EndpointBSigningPrivate,
                    out key);

            public bool TryGetEd25519PublicKey(
                IslandId islandId,
                long version,
                out ReadOnlyMemory<byte> key) =>
                Select(
                    islandId,
                    version,
                    keys.RelaySigningPublic,
                    keys.EndpointASigningPublic,
                    keys.EndpointBSigningPublic,
                    out key);

            public bool TryGetX25519PrivateKey(
                IslandId islandId,
                long version,
                out ReadOnlyMemory<byte> key) =>
                Select(
                    islandId,
                    version,
                    keys.RelayAgreementPrivate,
                    keys.EndpointAAgreementPrivate,
                    keys.EndpointBAgreementPrivate,
                    out key);

            public bool TryGetX25519PublicKey(
                IslandId islandId,
                long version,
                out ReadOnlyMemory<byte> key) =>
                Select(
                    islandId,
                    version,
                    keys.RelayAgreementPublic,
                    keys.EndpointAAgreementPublic,
                    keys.EndpointBAgreementPublic,
                    out key);

            private static bool Select(
                IslandId islandId,
                long version,
                byte[] relay,
                byte[] endpointA,
                byte[] endpointB,
                out ReadOnlyMemory<byte> key)
            {
                if (islandId.Value == DadAutoPartyIdentityPackageService.RegistrationRecipient &&
                    version == RelayKeyVersion)
                {
                    key = relay;
                    return true;
                }
                if (islandId.Value == EndpointAIsland && version == EndpointAKeyVersion)
                {
                    key = endpointA;
                    return true;
                }
                if (islandId.Value == EndpointBIsland && version == EndpointBKeyVersion)
                {
                    key = endpointB;
                    return true;
                }
                key = default;
                return false;
            }
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

        public PumpFixture(
            DadAutoPartyRegistrationState state,
            bool includePeer = false,
            bool useFrenRider = false,
            Func<DadAutoPartyRemoteProfileRequest, DadAutoPartyRemoteProfileResult>? remoteProfileProvider = null)
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
            Bridge = new DadAutoPartyParticipantBridge(
                Configuration,
                useFrenRiderProvider: () => useFrenRider,
                remoteProfileProvider: remoteProfileProvider);
            fixtureResolver = new FixtureResolver(this);
        }

        public DadAutoPartyRelayPump CreatePump(
            Func<DateTime, DadAutoPartyListingPublication>? inboundListingPublicationProvider = null,
            IDadAutoPartyInboundProposalStore? inboundProposalStore = null,
            Action<string>? diagnostic = null,
            Func<RunProposal, DadAutoPartyInboundAdmissionResult>? inboundAdmission = null,
            Func<DateTimeOffset>? utcNow = null,
            Action<DadAutoPartyExpiredRuntimeTarget>? expiredRuntimeTargetHandler = null)
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
                diagnostic: diagnostic,
                expiredRuntimeTargetHandler: expiredRuntimeTargetHandler);

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

        public ContractHeader PeerHeader(string purpose, DateTimeOffset? now = null)
        {
            var nonce = RandomNumberGenerator.GetBytes(AutoPartyProtocol.ContractNonceBytes);
            try
            {
                var observedAt = now ?? DateTimeOffset.UtcNow;
                return new ContractHeader(
                    AutoPartyProtocol.CurrentVersion,
                    Guid.NewGuid(),
                    $"{purpose}-{Guid.NewGuid():N}",
                    new IslandId(PeerIsland),
                    new IslandId(LocalIsland),
                    observedAt,
                    observedAt.AddMinutes(5),
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
        public void ClearAll() => pending = null;
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
