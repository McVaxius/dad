using System.Collections.Immutable;
using AutoParty.Contracts;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAutoPartyInboundProposalServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RetainRejectsLocalIslandClaimedByAnotherOwner()
    {
        var configuration = Configuration();
        var service = new DadAutoPartyInboundProposalService(configuration, utcNow: () => Now);
        var proposal = Proposal() with
        {
            Participants =
            [
                new ParticipantRequest(
                    new OwnerId("owner-conflict"),
                    new IslandId("island-local"),
                    new OpaqueCharacterId("opaque-local"),
                    new JobId("19")),
            ],
            ExecutionPlan = Proposal().ExecutionPlan! with
            {
                Participants =
                [
                    new EndpointExecutionParticipant(
                        "slot-1",
                        new OwnerId("owner-conflict"),
                        new IslandId("island-local"),
                        new OpaqueCharacterId("opaque-local"),
                        new JobId("19"),
                        EndpointExecutionRole.QueueLeader,
                        IsInviter: false),
                ],
            },
        };

        Assert.False(service.TryRetain(proposal, out _, out var safeCode));
        Assert.Equal("dad-inbound-proposal-local-route-conflict", safeCode);
    }

    [Fact]
    public void PreparedResponsesRecoverExactlyAndAcceptedReceiptIsMonotonic()
    {
        var configuration = Configuration();
        var store = new DadAutoPartyMemoryInboundProposalStore();
        var proposal = Proposal();
        Assert.NotEmpty(CanonicalCborCodec.EncodeUnsigned(proposal));
        var first = new DadAutoPartyInboundProposalService(configuration, store, () => Now);
        Assert.True(first.TryRetain(proposal, out var retained, out var safeCode), safeCode);
        var reservation = new Reservation(
            ResponseHeader(Guid.Parse("a284a873-0c65-4cbf-a7f5-b35315016f5a")),
            Guid.Parse("51b070f7-2ab8-41f0-b34f-28a3ed00c31c"),
            proposal.ProposalId,
            new OwnerId("owner-local"),
            new OpaqueCharacterId("opaque-local"),
            ExpectedStateGeneration: 4,
            ObservedStateGeneration: 5);
        var preflight = new PreflightResult(
            ResponseHeader(Guid.Parse("9f936bb1-a07d-430b-89c3-f2e4f40fda97")),
            proposal.ProposalId,
            new OwnerId("owner-local"),
            Ready: false,
            ReadinessGeneration: 4,
            ExpectedStateGeneration: 5,
            SafeBlockers: ["dad-inbound-execution-admission-not-wired"],
            ObservedStateGeneration: 5);
        Assert.NotEmpty(CanonicalCborCodec.EncodeUnsigned(reservation));
        Assert.NotEmpty(CanonicalCborCodec.EncodeUnsigned(preflight));
        Assert.True(first.TryPrepareResponses(
            proposal.ProposalId,
            [reservation],
            preflight,
            null,
            5,
            "dad-inbound-execution-admission-not-wired",
            out _));

        var restarted = new DadAutoPartyInboundProposalService(configuration, store, () => Now.AddSeconds(1));
        var recovered = restarted.UnacknowledgedResponses(8);
        Assert.Equal(2, recovered.Count);
        Assert.Equal(
            CanonicalCborCodec.EncodeUnsigned(reservation),
            CanonicalCborCodec.EncodeUnsigned(Assert.IsType<Reservation>(recovered[0])));
        Assert.Equal(
            CanonicalCborCodec.EncodeUnsigned(preflight),
            CanonicalCborCodec.EncodeUnsigned(Assert.IsType<PreflightResult>(recovered[1])));

        Assert.True(restarted.ObserveRelayReceipt(reservation.Header.MessageId, accepted: true));
        Assert.True(restarted.ObserveRelayReceipt(reservation.Header.MessageId, accepted: true));
        var afterReceipt = new DadAutoPartyInboundProposalService(configuration, store, () => Now.AddSeconds(2))
            .UnacknowledgedResponses(8);
        Assert.Single(afterReceipt);
        Assert.IsType<PreflightResult>(afterReceipt[0]);
        Assert.Equal(retained.Proposal.ProposalId, preflight.ProposalId);
    }

    [Fact]
    public void RenewalExtendsTheSameProposalAndReissuesResponsesBeforeRemovalOrExpiry()
    {
        var now = Now;
        var configuration = Configuration();
        var proposal = Proposal() with
        {
            Header = ProposalHeader() with { ExpiresAt = Now.AddMinutes(30) },
        };
        var service = new DadAutoPartyInboundProposalService(
            configuration,
            utcNow: () => now);
        Assert.True(service.TryRetain(proposal, out _, out var retainCode), retainCode);

        var reservation = new Reservation(
            ResponseHeader(Guid.Parse("a284a873-0c65-4cbf-a7f5-b35315016f5a")),
            Guid.Parse("51b070f7-2ab8-41f0-b34f-28a3ed00c31c"),
            proposal.ProposalId,
            new OwnerId("owner-local"),
            new OpaqueCharacterId("opaque-local"),
            4,
            5);
        var preflight = new PreflightResult(
            ResponseHeader(Guid.Parse("9f936bb1-a07d-430b-89c3-f2e4f40fda97")),
            proposal.ProposalId,
            new OwnerId("owner-local"),
            Ready: false,
            ReadinessGeneration: 4,
            ExpectedStateGeneration: 5,
            SafeBlockers: ["dad-inbound-execution-admission-not-wired"],
            ObservedStateGeneration: 5);
        Assert.True(service.TryPrepareResponses(
            proposal.ProposalId,
            [reservation],
            preflight,
            null,
            5,
            "dad-inbound-execution-admission-not-wired",
            out _));

        var previousExpiresAt = proposal.Header.ExpiresAt;
        var renewedExpiresAt = previousExpiresAt.AddMinutes(30);
        var renewal = Renewal(
            proposal,
            previousExpiresAt,
            renewedExpiresAt,
            renewalGeneration: 1,
            issuedAt: now);
        Assert.True(service.TryApplyRenewal(renewal, out var renewed, out var renewalCode), renewalCode);
        Assert.Equal(renewedExpiresAt, renewed.Proposal.Header.ExpiresAt);
        Assert.Equal(1, renewed.RenewalGeneration);
        var reissued = service.UnacknowledgedResponses(8);
        Assert.Equal(2, reissued.Count);
        Assert.All(reissued, response => Assert.Equal(renewedExpiresAt, response.Header.ExpiresAt));
        Assert.DoesNotContain(reissued, response => response.Header.MessageId == reservation.Header.MessageId);

        Assert.False(service.TryApplyRenewal(renewal, out _, out var replayCode));
        Assert.Equal("dad-inbound-proposal-renewal-identity-mismatch", replayCode);

        var removed = new DadAutoPartyInboundProposalService(configuration, utcNow: () => now);
        Assert.True(removed.TryRetain(proposal, out _, out _));
        removed.Remove(proposal.ProposalId);
        Assert.False(removed.TryApplyRenewal(renewal, out _, out var cancelledCode));
        Assert.Equal("dad-inbound-proposal-renewal-no-session", cancelledCode);

        var revoked = new DadAutoPartyInboundProposalService(configuration, utcNow: () => now);
        Assert.True(revoked.TryRetain(proposal, out _, out _));
        revoked.RemoveSender(proposal.Header.SenderIslandId.Value);
        Assert.False(revoked.TryApplyRenewal(renewal, out _, out var revokedCode));
        Assert.Equal("dad-inbound-proposal-renewal-no-session", revokedCode);

        var expired = new DadAutoPartyInboundProposalService(configuration, utcNow: () => now);
        Assert.True(expired.TryRetain(proposal, out _, out _));
        now = proposal.Header.ExpiresAt;
        Assert.False(expired.TryApplyRenewal(renewal, out _, out var expiredCode));
        Assert.Equal("dad-inbound-proposal-renewal-expired", expiredCode);
    }

    private static DadAutoPartyConfiguration Configuration() => new()
    {
        Enabled = true,
        RegistrationState = DadAutoPartyRegistrationState.Active,
        RegistrationId = Guid.Parse("75c75b0b-2411-44a7-8e6a-3c056924e5f5").ToString("D"),
        RouteId = "route-local",
        WebhookCredentialReference = "webhook-mailbox-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        UplinkEpochId = Guid.Parse("4c7594c3-d04e-47d1-8f1e-d286cdf89db7").ToString("D"),
        DownlinkEpochId = Guid.Parse("fab3d39b-f6e7-465a-a996-e5fc4a52de93").ToString("D"),
        MailboxEpochGeneration = 1,
        RelayKeyGeneration = 1,
        RelaySigningPublicKey = Convert.ToBase64String(new byte[32]),
        RelayAgreementPublicKey = Convert.ToBase64String(new byte[32]),
        RegisteredOwnerId = "owner-local",
        RegisteredIslandId = "island-local",
    };

    private static RunProposal Proposal()
    {
        var proposalId = Guid.Parse("ef25ed75-c04f-47b7-8cf8-280a680f647c");
        return new RunProposal(
            ProposalHeader(),
            proposalId,
            new OwnerId("owner-peer"),
            new ActivityId("dad-duty-1"),
            [
                new ParticipantRequest(
                    new OwnerId("owner-peer"),
                    new IslandId("island-peer"),
                    new OpaqueCharacterId("opaque-peer"),
                    new JobId("24")),
                new ParticipantRequest(
                    new OwnerId("owner-local"),
                    new IslandId("island-local"),
                    new OpaqueCharacterId("opaque-local"),
                    new JobId("19")),
            ],
            "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            new EndpointExecutionPlan(
                "run-inbound",
                FormationOnly: true,
                RequirePostArReady: false,
                ParticipantReadyTimeoutSeconds: 120,
                AssemblyTimeoutSeconds: 90,
                LeaseDurationSeconds: 300,
                RepairPolicy: new EndpointRepairPolicy(false, 75, "self"),
                Participants:
                [
                    new EndpointExecutionParticipant(
                        "slot-1",
                        new OwnerId("owner-peer"),
                        new IslandId("island-peer"),
                        new OpaqueCharacterId("opaque-peer"),
                        new JobId("24"),
                        EndpointExecutionRole.QueueLeader,
                        IsInviter: true),
                    new EndpointExecutionParticipant(
                        "slot-2",
                        new OwnerId("owner-local"),
                        new IslandId("island-local"),
                        new OpaqueCharacterId("opaque-local"),
                        new JobId("19"),
                        EndpointExecutionRole.Participant,
                        IsInviter: false),
                ],
                Modules: []));
    }

    private static ContractHeader ProposalHeader() => new(
        AutoPartyProtocol.CurrentVersion,
        Guid.Parse("9d63c1ad-017d-416d-870c-0dbdfbbd6555"),
        "proposal-inbound",
        new IslandId("island-peer"),
        new IslandId("island-local"),
        Now,
        Now.AddMinutes(5),
        1,
        1,
        3,
        1,
        ContractHeader.CreateNonce(new byte[AutoPartyProtocol.ContractNonceBytes]),
        ImmutableArray<int>.Empty);

    private static ContractHeader ResponseHeader(Guid messageId) => new(
        AutoPartyProtocol.CurrentVersion,
        messageId,
        $"response-{messageId:N}",
        new IslandId("island-local"),
        new IslandId("island-peer"),
        Now.AddSeconds(1),
        Now.AddMinutes(5),
        2,
        5,
        1,
        3,
        ContractHeader.CreateNonce(Enumerable.Repeat((byte)1, AutoPartyProtocol.ContractNonceBytes).ToArray()),
        ImmutableArray<int>.Empty);

    private static ProposalRenewal Renewal(
        RunProposal proposal,
        DateTimeOffset previousExpiresAt,
        DateTimeOffset newExpiresAt,
        long renewalGeneration,
        DateTimeOffset issuedAt) =>
        new(
            ProposalHeader() with
            {
                MessageId = Guid.NewGuid(),
                IdempotencyKey = $"renewal-{renewalGeneration}-{Guid.NewGuid():N}",
                IssuedAt = issuedAt,
                ExpiresAt = newExpiresAt,
                Generation = renewalGeneration,
            },
            proposal.ProposalId,
            proposal.RequesterOwnerId,
            new OwnerId("owner-local"),
            new IslandId("island-local"),
            previousExpiresAt,
            newExpiresAt,
            renewalGeneration);
}
