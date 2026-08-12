using System.Collections.Immutable;
using System.Text.Json;
using AutoParty.Contracts;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAutoPartyIntegrationRulesTests
{
    private const string Owner = "owner-a";
    private const string SenderIsland = "island-owner-a";
    private const string LocalIsland = "island-local";
    private const string Character = "opaque-character-a";
    private const string Job = "job-a";
    private const string Activity = "activity-a";

    [Fact]
    public async Task DisabledCourierNeverTouchesAttachedAdapter()
    {
        var configuration = new DadAutoPartyConfiguration();
        var inner = new FakeTransportAdapter();
        await using var connector = new DadDiscordCourierConnector(configuration, static () => true);
        connector.AttachVerifiedAdapter(inner);
        var delivery = Envelope();

        var health = await connector.GetHealthAsync();
        var send = await connector.SendAsync(delivery);
        var received = 0;
        await foreach (var _ in connector.ReceiveAsync())
            received++;

        Assert.Equal(AutoPartyTransportHealthState.Disabled, health.State);
        Assert.False(send.Accepted);
        Assert.Equal(0, received);
        Assert.Equal(0, inner.HealthCalls);
        Assert.Equal(0, inner.SendCalls);
        Assert.Equal(0, inner.ReceiveCalls);
    }

    [Fact]
    public async Task EnabledCourierWithoutVerifiedAdapterIsNotReady()
    {
        var configuration = new DadAutoPartyConfiguration { Enabled = true };
        await using var connector = new DadDiscordCourierConnector(configuration, static () => true);

        var health = await connector.GetHealthAsync();
        var send = await connector.SendAsync(Envelope());

        Assert.Equal(AutoPartyTransportHealthState.NotReady, health.State);
        Assert.Equal("dad-courier-not-attached", health.SafeCode);
        Assert.False(send.Accepted);
    }

    [Fact]
    public async Task CourierRejectsSemanticEnvelopeOverProtocolLimit()
    {
        var configuration = new DadAutoPartyConfiguration { Enabled = true };
        var inner = new FakeTransportAdapter();
        await using var connector = new DadDiscordCourierConnector(configuration, static () => true);
        connector.AttachVerifiedAdapter(inner);
        var oversized = Envelope(new byte[AutoPartyProtocol.MaximumSemanticEnvelopeBytes + 1]);

        var result = await connector.SendAsync(oversized);

        Assert.False(result.Accepted);
        Assert.Equal("dad-courier-envelope-invalid", result.SafeCode);
        Assert.Equal(0, inner.SendCalls);
    }

    [Fact]
    public void ExistingLanRequestDoesNotRequireAutoPartyAuthorization()
    {
        var request = new DadRunRequest
        {
            Dungeon = new DadDungeonTask { QueueViaLanParty = true },
        };
        request.ApplyOrchestrationDefaults();

        var decision = DadAutoPartySchedulerAuthorizationRules.Evaluate(
            request,
            _ => throw new InvalidOperationException("Resolver must not run."));

        Assert.Equal(DadAutoPartyAuthorizationState.NotRequired, decision.State);
    }

    [Fact]
    public void SchedulerWaitsForExplicitProposalAndDeniesMalformedProposal()
    {
        var proposalId = Guid.NewGuid();
        var waitingRequest = new DadRunRequest
        {
            Orchestration = new DadOrchestrationIntent { AutoPartyProposalId = proposalId.ToString("D") },
        };
        var malformedRequest = new DadRunRequest
        {
            Orchestration = new DadOrchestrationIntent { AutoPartyProposalId = "not-a-proposal" },
        };

        var waiting = DadAutoPartySchedulerAuthorizationRules.Evaluate(
            waitingRequest,
            id => new(DadAutoPartyAuthorizationState.Waiting, "pending", id));
        var malformed = DadAutoPartySchedulerAuthorizationRules.Evaluate(
            malformedRequest,
            _ => throw new InvalidOperationException("Resolver must not run."));

        Assert.Equal(DadAutoPartyAuthorizationState.Waiting, waiting.State);
        Assert.Equal(proposalId, waiting.ProposalId);
        Assert.Equal(DadAutoPartyAuthorizationState.Denied, malformed.State);
    }

    [Fact]
    public void NewPhasesAppendWithoutRenumberingExistingPhases()
    {
        Assert.Equal(0, (int)DadRunPhase.Idle);
        Assert.Equal(14, (int)DadRunPhase.TearingDownParty);
        Assert.Equal(15, (int)DadRunPhase.GroupReady);
        Assert.Equal(15, (int)DadSchedulerPresetPhase.LevelingBetweenChildren);
        Assert.Equal(16, (int)DadSchedulerPresetPhase.WaitingForAutoPartyAuthorization);
        Assert.True(new DadSchedulerPresetState
        {
            Phase = DadSchedulerPresetPhase.WaitingForAutoPartyAuthorization,
        }.IsActive);
    }

    [Fact]
    public void ProposalBoundGrantIsConsumedExactlyOnceAndPersisted()
    {
        var proposalId = Guid.NewGuid();
        var saves = 0;
        var configuration = ActiveConfiguration();
        configuration.Pairings.Add(Pairing());
        var grant = Grant(proposalId);
        configuration.Grants.Add(grant);
        var policy = new DadAutoPartyPolicyFacade(
            configuration,
            static () => true,
            saveConfiguration: () => saves++);
        var proposal = Proposal(proposalId);

        var accepted = policy.IntersectGrant(proposal, SessionPermission.Reserve);
        var replayed = policy.IntersectGrant(proposal, SessionPermission.Reserve);

        Assert.True(accepted.Allowed, accepted.SafeCode);
        Assert.Equal("dad-grant-intersection-empty", replayed.SafeCode);
        Assert.NotNull(grant.ConsumedAtUtc);
        Assert.Equal(1, grant.MaximumUses);
        Assert.Equal(proposalId.ToString("D"), grant.ProposalId);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void PolicyReplayCacheNeverExceedsItsBound()
    {
        var configuration = new DadAutoPartyConfiguration { Enabled = true };
        var policy = new DadAutoPartyPolicyFacade(configuration, static () => true);
        ContractHeader last = default!;
        for (var index = 0; index <= 4096; index++)
        {
            last = Header();
            var accepted = policy.VerifyReplay(last);
            Assert.True(accepted.Allowed, accepted.SafeCode);
        }

        var replayed = policy.VerifyReplay(last);

        Assert.Equal(4096, policy.ReplayEntryCount);
        Assert.Equal("dad-contract-replay-denied", replayed.SafeCode);
    }

    [Fact]
    public void ProposalIngressUsesTheCompleteProtocolHeaderValidator()
    {
        var proposalId = Guid.NewGuid();
        var configuration = ActiveConfiguration();
        configuration.Pairings.Add(Pairing());
        configuration.Grants.Add(Grant(proposalId));
        using var service = Service(configuration, new FakeIdentityStore());
        var proposal = Proposal(proposalId);
        var header = proposal.Header;
        var malformed = new[]
        {
            header with { SchemaVersion = AutoPartyProtocol.CurrentVersion + 1 },
            header with { MessageId = Guid.Empty },
            header with { IdempotencyKey = "idempotency key with spaces" },
            header with { SenderIslandId = new IslandId("sender with spaces") },
            header with { RecipientIslandId = new IslandId(string.Empty) },
            header with { Sequence = 0 },
            header with { Generation = 0 },
            header with { SenderKeyVersion = 0 },
            header with { RecipientKeyVersion = 0 },
            header with { IssuedAt = header.IssuedAt.ToOffset(TimeSpan.FromHours(1)) },
            header with { ExpiresAt = header.ExpiresAt.ToOffset(TimeSpan.FromHours(1)) },
            header with { ExpiresAt = header.IssuedAt },
            header with { ExpiresAt = header.IssuedAt + TimeSpan.FromHours(25) },
            header with { Nonce = ImmutableArray<byte>.Empty },
            header with { CriticalFields = ImmutableArray.Create(999) },
        };

        foreach (var candidate in malformed)
        {
            var denied = service.AcceptProposal(proposal with { Header = candidate }, SessionPermission.All);
            Assert.False(denied.Allowed);
            Assert.Equal("dad-contract-header-invalid", denied.SafeCode);
        }

        Assert.Equal(0, service.Policy.ReplayEntryCount);
        Assert.Null(configuration.Grants.Single().ConsumedAtUtc);
    }

    [Fact]
    public void NullProposalIsDeniedWithoutReplayMutation()
    {
        var configuration = new DadAutoPartyConfiguration { Enabled = true };
        var policy = new DadAutoPartyPolicyFacade(configuration, static () => true);

        var denied = policy.AcceptProposal(null!, SessionPermission.All);

        Assert.False(denied.Allowed);
        Assert.Equal("dad-proposal-invalid", denied.SafeCode);
        Assert.Equal(0, policy.ReplayEntryCount);
    }

    [Fact]
    public void RejectedProposalDoesNotPoisonTheValidRetransmitReplaySlot()
    {
        var proposalId = Guid.NewGuid();
        var configuration = ActiveConfiguration();
        configuration.Pairings.Add(Pairing());
        configuration.Grants.Add(Grant(proposalId));
        using var service = Service(configuration, new FakeIdentityStore());
        var valid = Proposal(proposalId);
        var unauthorized = valid with
        {
            Participants = ImmutableArray.Create(new ParticipantRequest(
                new OwnerId(Owner),
                new IslandId(SenderIsland),
                new OpaqueCharacterId(Character),
                new JobId("job-not-granted"))),
        };

        var rejected = service.AcceptProposal(unauthorized, SessionPermission.All);
        var retransmit = service.AcceptProposal(valid, SessionPermission.All);
        var duplicate = service.AcceptProposal(valid, SessionPermission.All);

        Assert.False(rejected.Allowed);
        Assert.Equal("dad-grant-intersection-empty", rejected.SafeCode);
        Assert.True(retransmit.Allowed, retransmit.SafeCode);
        Assert.False(duplicate.Allowed);
        Assert.Equal("dad-contract-replay-denied", duplicate.SafeCode);
        Assert.Equal(1, service.Policy.ReplayEntryCount);
    }

    [Fact]
    public async Task ReplayAndStrictRequestedJobFailClosed()
    {
        var fixture = AuthorizedFixture();
        using var service = fixture.Service;

        var duplicate = service.AcceptProposal(fixture.Proposal, SessionPermission.All);
        var wrongJob = Operation(
            fixture.Proposal.ProposalId,
            fixture.Generation,
            ExecutionOperationKind.Prepare,
            requestedJob: "job-not-granted");
        var execution = await service.Execution.PrepareAsync(wrongJob, null);

        Assert.False(duplicate.Allowed);
        Assert.Equal("dad-contract-replay-denied", duplicate.SafeCode);
        Assert.Equal(ExecutionOutcome.Denied, execution.Outcome);
        Assert.Equal("dad-execution-strict-job-grant-denied", execution.SafeCode);
    }

    [Fact]
    public void OneActiveSessionPerIslandIsEnforced()
    {
        var fixture = AuthorizedFixture();
        using var service = fixture.Service;
        var secondProposal = Proposal(Guid.NewGuid());
        fixture.Configuration.Grants.Add(Grant(secondProposal.ProposalId));
        var accepted = service.AcceptProposal(secondProposal, SessionPermission.All);
        var secondReservation = new Reservation(
            Header(),
            Guid.NewGuid(),
            secondProposal.ProposalId,
            new OwnerId(Owner),
            new OpaqueCharacterId(Character),
            accepted.StateGeneration);

        var denied = service.Reserve(secondReservation, DadAutoPartySessionMode.MultiOwner);

        Assert.True(accepted.Allowed);
        Assert.False(denied.Allowed);
        Assert.Equal("dad-island-session-already-active", denied.SafeCode);
    }

    [Fact]
    public async Task FormationOnlyStopsAtGroupReadyAndRestoresProfile()
    {
        var fixture = AuthorizedFixture();
        using var service = fixture.Service;
        var proposalId = fixture.Proposal.ProposalId;
        var prepare = Operation(proposalId, fixture.Generation, ExecutionOperationKind.Prepare, formationOnly: true);
        var profile = new IntegrationProfile(
            Header(),
            Guid.NewGuid(),
            proposalId,
            new OwnerId(Owner),
            EnableLevelSync: true,
            EnableUnrestrictedParty: false,
            EnableMinimumItemLevel: false,
            EnableSilenceEcho: false,
            ImmutableArray<string>.Empty,
            "profile-hash",
            fixture.Generation);
        var reserve = Operation(proposalId, fixture.Generation, ExecutionOperationKind.Reserve, formationOnly: true);
        var form = Operation(
            proposalId,
            fixture.Generation,
            ExecutionOperationKind.Form,
            formationOnly: true,
            locator: Locator());
        var observed = DadAutoPartyFakeExecutionFacade.CreateObservedPartyReceipt(
            proposalId,
            [101, 102, 103, 104],
            fixture.Generation);

        var prepared = await service.Execution.PrepareAsync(prepare, profile);
        var reserved = await service.Execution.ReserveAsync(reserve);
        var formed = await service.Execution.FormAsync(form, observed);
        var queued = await service.Execution.QueueAsync(
            Operation(proposalId, fixture.Generation, ExecutionOperationKind.Queue, formationOnly: true));
        var settled = await service.Execution.SettleAsync(
            Operation(proposalId, fixture.Generation, ExecutionOperationKind.Settle, formationOnly: true));
        var restored = await service.Execution.RestoreAsync(
            Operation(proposalId, fixture.Generation, ExecutionOperationKind.Restore, formationOnly: true));

        Assert.Equal(ExecutionOutcome.Completed, prepared.Outcome);
        Assert.Equal(ExecutionOutcome.Completed, reserved.Outcome);
        Assert.Equal(DadRunPhase.GroupReady, formed.Phase);
        Assert.Equal("dad-group-ready", formed.SafeCode);
        Assert.NotNull(formed.PartyReceipt);
        Assert.Equal([101UL, 102UL, 103UL, 104UL], formed.PartyReceipt.ContentIds);
        Assert.Equal(ExecutionOutcome.Denied, queued.Outcome);
        Assert.Equal("dad-formation-only-queue-denied", queued.SafeCode);
        Assert.Equal(ExecutionOutcome.Denied, settled.Outcome);
        Assert.Equal("dad-formation-only-settle-denied", settled.SafeCode);
        Assert.True(restored.ProfileRestored);
    }

    [Fact]
    public async Task CancelledFakeSessionRejectsLaterWorkButAllowsCleanupRestore()
    {
        var fixture = AuthorizedFixture();
        using var service = fixture.Service;
        var cancelled = await service.Execution.CancelAsync(Operation(
            fixture.Proposal.ProposalId,
            fixture.Generation,
            ExecutionOperationKind.Cancel));
        var laterPrepare = await service.Execution.PrepareAsync(Operation(
            fixture.Proposal.ProposalId,
            fixture.Generation,
            ExecutionOperationKind.Prepare), null);
        var restore = await service.Execution.RestoreAsync(Operation(
            fixture.Proposal.ProposalId,
            fixture.Generation,
            ExecutionOperationKind.Restore));

        Assert.Equal(ExecutionOutcome.Completed, cancelled.Outcome);
        Assert.Equal(ExecutionOutcome.Denied, laterPrepare.Outcome);
        Assert.Equal("dad-session-cancelled", laterPrepare.SafeCode);
        Assert.Equal(ExecutionOutcome.Completed, restore.Outcome);
    }

    [Fact]
    public async Task ExpiredInviteLocatorIsDeniedWithoutPartyMutation()
    {
        var fixture = AuthorizedFixture();
        using var service = fixture.Service;
        var proposalId = fixture.Proposal.ProposalId;
        await service.Execution.PrepareAsync(
            Operation(proposalId, fixture.Generation, ExecutionOperationKind.Prepare),
            null);
        await service.Execution.ReserveAsync(
            Operation(proposalId, fixture.Generation, ExecutionOperationKind.Reserve));
        var expired = Locator(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));

        var result = await service.Execution.FormAsync(
            Operation(proposalId, fixture.Generation, ExecutionOperationKind.Form, locator: expired),
            DadAutoPartyFakeExecutionFacade.CreateObservedPartyReceipt(
                proposalId,
                [101, 102, 103, 104],
                fixture.Generation));

        Assert.Equal(ExecutionOutcome.Denied, result.Outcome);
        Assert.Equal("dad-invite-locator-invalid", result.SafeCode);
    }

    [Fact]
    public async Task FormRequiresExactOrderedUniqueNonzeroPartyListProof()
    {
        var fixture = AuthorizedFixture();
        using var service = fixture.Service;
        var proposalId = fixture.Proposal.ProposalId;
        await service.Execution.PrepareAsync(
            Operation(proposalId, fixture.Generation, ExecutionOperationKind.Prepare),
            null);
        await service.Execution.ReserveAsync(
            Operation(proposalId, fixture.Generation, ExecutionOperationKind.Reserve));
        var valid = DadAutoPartyFakeExecutionFacade.CreateObservedPartyReceipt(
            proposalId,
            [101, 102, 103, 104],
            fixture.Generation);

        var countMismatch = await service.Execution.FormAsync(
            Operation(proposalId, fixture.Generation, ExecutionOperationKind.Form, locator: Locator()),
            valid with { MemberCount = 3 });
        var reordered = await service.Execution.FormAsync(
            Operation(proposalId, fixture.Generation, ExecutionOperationKind.Form, locator: Locator()),
            valid with { ContentIds = [102, 101, 103, 104] });

        Assert.Equal("dad-observed-party-receipt-invalid", countMismatch.SafeCode);
        Assert.Equal("dad-observed-party-receipt-invalid", reordered.SafeCode);
        Assert.Throws<ArgumentException>(() => DadAutoPartyFakeExecutionFacade.CreateObservedPartyReceipt(
            proposalId,
            [101, 101],
            fixture.Generation));
        Assert.Throws<ArgumentException>(() => DadAutoPartyFakeExecutionFacade.CreateObservedPartyReceipt(
            proposalId,
            [0],
            fixture.Generation));
    }

    [Fact]
    public async Task RevocationAndOwnerStopOverrideExecution()
    {
        var fixture = AuthorizedFixture();
        using var service = fixture.Service;
        var revocation = new Revocation(
            Header(),
            Guid.NewGuid(),
            new OwnerId(Owner),
            RevocationTargetKind.Session,
            fixture.Proposal.ProposalId.ToString("D"),
            1,
            "owner-stop");

        var revoked = service.Revoke(revocation);
        var scheduler = service.EvaluateSchedulerAuthorization(new DadRunRequest
        {
            Orchestration = new DadOrchestrationIntent
            {
                AutoPartyProposalId = fixture.Proposal.ProposalId.ToString("D"),
            },
        });
        var execution = await service.Execution.PrepareAsync(
            Operation(fixture.Proposal.ProposalId, fixture.Generation, ExecutionOperationKind.Prepare),
            null);
        service.StopAll("owner-stop");
        var afterStop = await service.Execution.PrepareAsync(
            Operation(fixture.Proposal.ProposalId, fixture.Generation, ExecutionOperationKind.Prepare),
            null);

        Assert.True(revoked.Allowed);
        Assert.Equal(DadAutoPartyAuthorizationState.Denied, scheduler.State);
        Assert.Equal(ExecutionOutcome.Denied, execution.Outcome);
        Assert.Equal(ExecutionOutcome.Denied, afterStop.Outcome);
    }

    [Fact]
    public async Task LeaseExpiryAndLocalSafetyOverrideRemoteExecution()
    {
        var expiring = AuthorizedFixture(leaseDuration: TimeSpan.FromMilliseconds(500));
        using var expiringService = expiring.Service;
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        var expired = await expiringService.Execution.PrepareAsync(
            Operation(expiring.Proposal.ProposalId, expiring.Generation, ExecutionOperationKind.Prepare),
            null);

        var unsafeFixture = AuthorizedFixture(localSafetyAllowsExecution: static () => false);
        using var unsafeService = unsafeFixture.Service;
        var unsafeResult = await unsafeService.Execution.PrepareAsync(
            Operation(unsafeFixture.Proposal.ProposalId, unsafeFixture.Generation, ExecutionOperationKind.Prepare),
            null);

        Assert.Equal(ExecutionOutcome.Denied, expired.Outcome);
        Assert.Equal("dad-session-lease-expired", expired.SafeCode);
        Assert.Equal(ExecutionOutcome.Denied, unsafeResult.Outcome);
        Assert.Equal("dad-local-safety-veto", unsafeResult.SafeCode);
    }

    [Fact]
    public async Task StatusSeparatesTransportPolicyAndExecution()
    {
        var configuration = new DadAutoPartyConfiguration { Enabled = true };
        using var service = Service(configuration, new FakeIdentityStore());

        var status = await service.GetStatusAsync();

        Assert.Equal(DadAutoPartyComponentState.NotReady, status.Transport.State);
        Assert.Equal(DadAutoPartyComponentState.NotReady, status.Policy.State);
        Assert.Equal(DadAutoPartyComponentState.NotReady, status.Execution.State);
    }

    [Fact]
    public void PlannerAutoPartyFieldsCloneButRemainOutsideIpcJson()
    {
        var proposalId = Guid.NewGuid().ToString("D");
        var group = new DadPlannerGroup
        {
            AutoPartyProposalId = proposalId,
            AutoPartyFormationOnly = true,
        };

        var clone = DadSchedulerGroupCloneRules.CloneWithSlots(group, []);
        var json = JsonSerializer.Serialize(group);

        Assert.Equal(proposalId, clone.AutoPartyProposalId);
        Assert.True(clone.AutoPartyFormationOnly);
        Assert.DoesNotContain("AutoPartyProposalId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoPartyFormationOnly", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrivacyPurgeRemovesLocalAuthorityAndEndpointIdentity()
    {
        var configuration = ActiveConfiguration();
        configuration.Listings.Add(new DadAutoPartyListing
        {
            ListingId = Guid.NewGuid().ToString("D"),
            OpaqueCharacterId = Character,
            AllowedJobIds = [Job],
            AllowedActivityIds = [Activity],
            ExpiresAtUtc = DateTime.UtcNow + TimeSpan.FromDays(1),
        });
        var store = new FakeIdentityStore();
        using var service = Service(configuration, store);

        var result = await service.PurgeAsync(deleteEndpointIdentity: true);

        Assert.True(result.Purged);
        Assert.True(result.IdentityDeleted);
        Assert.False(configuration.Enabled);
        Assert.Equal(DadAutoPartyRegistrationState.Unregistered, configuration.RegistrationState);
        Assert.Equal(string.Empty, configuration.WebhookCredentialReference);
        Assert.Equal(string.Empty, configuration.EndpointIdentityReference);
        Assert.Empty(configuration.Pairings);
        Assert.Empty(configuration.Grants);
        Assert.Empty(configuration.Listings);
        Assert.Equal(1, store.DeleteCalls);
    }

    private static AuthorizedContext AuthorizedFixture(
        Func<bool>? localSafetyAllowsExecution = null,
        TimeSpan? leaseDuration = null)
    {
        var proposalId = Guid.NewGuid();
        var configuration = ActiveConfiguration();
        configuration.Pairings.Add(Pairing());
        configuration.Grants.Add(Grant(proposalId));
        var service = Service(configuration, new FakeIdentityStore(), localSafetyAllowsExecution);
        var proposal = Proposal(proposalId);
        var accepted = service.AcceptProposal(proposal, SessionPermission.All);
        Assert.True(accepted.Allowed);
        var reserved = service.Reserve(
            new Reservation(
                Header(),
                Guid.NewGuid(),
                proposal.ProposalId,
                new OwnerId(Owner),
                new OpaqueCharacterId(Character),
                accepted.StateGeneration),
            DadAutoPartySessionMode.MultiOwner);
        Assert.True(reserved.Allowed);
        var preflight = service.VerifyPreflight(new PreflightResult(
            Header(),
            proposal.ProposalId,
            new OwnerId(Owner),
            Ready: true,
            ReadinessGeneration: 1,
            reserved.StateGeneration,
            ImmutableArray<string>.Empty));
        Assert.True(preflight.Allowed);
        var lease = service.AcquireLease(new SessionLease(
            Header(),
            Guid.NewGuid(),
            proposal.ProposalId,
            new OwnerId(Owner),
            DateTimeOffset.UtcNow + (leaseDuration ?? TimeSpan.FromMinutes(10)),
            SessionPermission.All,
            preflight.StateGeneration));
        Assert.True(lease.Allowed);
        return new(service, configuration, proposal, lease.StateGeneration);
    }

    private static DadAutoPartyPairing Pairing()
        => new()
        {
            PairingId = Guid.NewGuid().ToString("D"),
            OwnerId = Owner,
            IslandId = SenderIsland,
            HomeGuildScope = "guild-peer",
            PublicKeyFingerprint = new string('A', 64),
            LocalFingerprint = new string('B', 64),
            TranscriptHash = new string('C', 64),
            ConfirmationCodeHash = new string('D', 64),
            LocalApproved = true,
            PeerApproved = true,
            LocalSharePolicy = SharePolicy(),
            PeerSharePolicy = SharePolicy(),
            ExpiresAtUtc = DateTime.UtcNow + TimeSpan.FromMinutes(10),
            KeyGeneration = 1,
            SigningPublicKey = Convert.ToBase64String(new byte[AutoPartyProtocol.Ed25519PublicKeyBytes]),
            AgreementPublicKey = Convert.ToBase64String(new byte[AutoPartyProtocol.X25519KeyBytes]),
            ConfirmedAtUtc = DateTime.UtcNow,
        };

    private static DadAutoPartySharePolicy SharePolicy() => new()
    {
        Mode = DadAutoPartyCharacterShareMode.SpecificCharacter,
        CharacterHandles = [Character],
        Enabled = true,
        Revision = 1,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static DadAutoPartyConfiguration ActiveConfiguration() => new()
    {
        Enabled = true,
        RegistrationState = DadAutoPartyRegistrationState.Active,
        RegistrationId = Guid.NewGuid().ToString("D"),
        RouteId = "route-local",
        CentralBotApplicationId = "123456789",
        HomeGuildScope = "guild-home",
        WebhookCredentialReference = "webhook-mailbox-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        UplinkEpochId = "11111111-1111-4111-8111-111111111111",
        DownlinkEpochId = "22222222-2222-4222-8222-222222222222",
        MailboxEpochGeneration = 1,
        RelayKeyGeneration = 1,
        RelaySigningPublicKey = Convert.ToBase64String(new byte[32]),
        RelayAgreementPublicKey = Convert.ToBase64String(new byte[32]),
        EndpointIdentityReference = "identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        RegisteredOwnerId = Owner,
        RegisteredIslandId = LocalIsland,
        RegistrationFingerprint = new string('E', 64),
        EndpointAlias = "local",
        SigningPublicKey = Convert.ToBase64String(new byte[32]),
        EncryptionPublicKey = Convert.ToBase64String(new byte[32]),
    };

    private static DadAutoPartyGrant Grant(Guid proposalId)
        => new()
        {
            GrantId = Guid.NewGuid().ToString("D"),
            ProposalId = proposalId.ToString("D"),
            OwnerId = Owner,
            IslandId = SenderIsland,
            OpaqueCharacterId = Character,
            RequestedJobId = Job,
            ActivityId = Activity,
            Permissions = SessionPermission.All,
            IssuedAtUtc = DateTime.UtcNow - TimeSpan.FromMinutes(1),
            ExpiresAtUtc = DateTime.UtcNow + TimeSpan.FromMinutes(20),
            MaximumUses = 1,
        };
    private static DadAutoPartyService Service(
        DadAutoPartyConfiguration configuration,
        IDadAutoPartyEndpointIdentityStore identityStore,
        Func<bool>? localSafetyAllowsExecution = null)
        => new(
            configuration,
            identityStore,
            static () => true,
            static () => { },
            localSafetyAllowsExecution ?? (static () => true));

    private static RunProposal Proposal(Guid proposalId)
        => new(
            Header(),
            proposalId,
            new OwnerId(Owner),
            new ActivityId(Activity),
            ImmutableArray.Create(new ParticipantRequest(
                new OwnerId(Owner),
                new IslandId(SenderIsland),
                new OpaqueCharacterId(Character),
                new JobId(Job))),
            "effective-content-hash");

    private static ExecutionOperation Operation(
        Guid proposalId,
        long generation,
        ExecutionOperationKind kind,
        string requestedJob = Job,
        bool formationOnly = false,
        InviteLocator? locator = null)
        => new(
            Header(),
            Guid.NewGuid(),
            proposalId,
            new OwnerId(Owner),
            kind,
            new ActivityId(Activity),
            new OpaqueCharacterId(Character),
            new JobId(requestedJob),
            locator,
            generation,
            formationOnly);

    private static InviteLocator Locator(DateTimeOffset? validUntil = null)
        => new(
            Guid.NewGuid().ToString("N"),
            new OwnerId(Owner),
            new IslandId(LocalIsland),
            validUntil ?? DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2),
            ImmutableArray.Create<byte>(1, 2, 3, 4));

    private static ContractHeader Header()
        => new(
            AutoPartyProtocol.CurrentVersion,
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            new IslandId(SenderIsland),
            new IslandId(LocalIsland),
            DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1),
            DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10),
            1,
            1,
            1,
            1,
            ContractHeader.CreateNonce(Enumerable.Repeat((byte)0x5A, AutoPartyProtocol.ContractNonceBytes).ToArray()),
            ImmutableArray<int>.Empty);

    private static OpaqueEnvelope Envelope(byte[]? ciphertext = null)
        => OpaqueEnvelope.Create(
            AutoPartyProtocol.CurrentVersion,
            Guid.NewGuid(),
            new IslandId(SenderIsland),
            new IslandId(LocalIsland),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1),
            1,
            "test",
            ciphertext ?? [1, 2, 3]);

    private sealed record AuthorizedContext(
        DadAutoPartyService Service,
        DadAutoPartyConfiguration Configuration,
        RunProposal Proposal,
        long Generation);
    private sealed class FakeIdentityStore : IDadAutoPartyEndpointIdentityStore
    {
        public int StoreCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public ValueTask<string> StoreAsync(
            ReadOnlyMemory<byte> identityMaterial,
            CancellationToken cancellationToken = default)
        {
            StoreCalls++;
            return ValueTask.FromResult($"identity-{Guid.NewGuid():N}");
        }

        public ValueTask<byte[]> LoadAsync(
            string identityReference,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new byte[] { 1, 2, 3, 4 });

        public ValueTask<bool> DeleteAsync(
            string identityReference,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class FakeTransportAdapter : IAutoPartyTransportAdapter
    {
        public int HealthCalls { get; private set; }
        public int SendCalls { get; private set; }
        public int ReceiveCalls { get; private set; }

        public ValueTask<AutoPartyTransportHealth> GetHealthAsync(CancellationToken cancellationToken = default)
        {
            HealthCalls++;
            return ValueTask.FromResult(new AutoPartyTransportHealth(
                AutoPartyTransportHealthState.Ready,
                "ready",
                DateTimeOffset.UtcNow));
        }

        public async IAsyncEnumerable<OpaqueEnvelope> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceiveCalls++;
            await Task.Yield();
            yield return Envelope();
        }

        public ValueTask<AutoPartyTransportSendResult> SendAsync(
            OpaqueEnvelope delivery,
            CancellationToken cancellationToken = default)
        {
            SendCalls++;
            return ValueTask.FromResult(new AutoPartyTransportSendResult(true, "accepted", delivery.EnvelopeId));
        }

        public ValueTask AcknowledgeAsync(
            AutoPartyTransportAcknowledgement acknowledgement,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
