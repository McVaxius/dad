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
    public async Task PilotFixtureCreatesOnlyArtifactBoundFormationPlanAfterLocalPairing()
    {
        var localFingerprint = new string('A', 64);
        var peerFingerprint = new string('B', 64);
        var artifactHash = new string('c', 64);
        var configuration = new Configuration
        {
            AutoParty = new DadAutoPartyConfiguration
            {
                RegisteredOwnerId = "owner-local",
                RegisteredIslandId = "island-local",
                RegistrationFingerprint = localFingerprint,
                PilotArtifactSha256 = artifactHash,
                OwnerAcceptanceConfirmed = true,
                Pairings =
                [
                    new DadAutoPartyPairing
                    {
                        OwnerId = "owner-peer",
                        IslandId = "island-peer",
                        PublicKeyFingerprint = peerFingerprint,
                        KeyGeneration = 1,
                        ConfirmedAtUtc = DateTime.UtcNow,
                    },
                ],
            },
        };
        var saved = 0;
        var service = new DadAutoPartyPilotFixtureService(configuration, () => saved++);
        var fixture = new DadAutoPartyPilotFixture(
            DadAutoPartyPilotFixtureService.FixtureSchema,
            true,
            55,
            localFingerprint,
            artifactHash,
            [
                new(localFingerprint, "19", true, true),
                new(peerFingerprint, "24", true, false),
            ]);
        var path = Path.Combine(Path.GetTempPath(), $"dad-pilot-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(fixture, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var result = await service.ImportAsync(path);

            Assert.True(result.Succeeded, result.SafeCode);
            Assert.Equal("dad-pilot-formation-fixture-imported", result.SafeCode);
            Assert.Equal(1, saved);
            var group = Assert.Single(configuration.PlannerGroups);
            Assert.True(group.AutoPartyFormationOnly);
            Assert.Equal(DadPlannerActivityMode.DutyPremade, group.ActivityMode);
            Assert.Equal((uint)55, group.DutyContentFinderConditionId);
            Assert.Equal([19u, 24u], group.Slots.Select(slot => slot.RequiredJobId!.Value));
            Assert.Equal(localFingerprint, configuration.AutoParty.PilotQueueAuthorityFingerprint);
            Assert.Equal(2, configuration.AutoParty.RemoteBindings.Count);
            Assert.Single(configuration.AutoParty.RemoteBindings, binding => binding.OwnsQueueAuthority);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PilotFixtureRejectsQueueAuthorityThatDoesNotOwnTheFixture()
    {
        var localFingerprint = new string('A', 64);
        var peerFingerprint = new string('B', 64);
        var configuration = new Configuration
        {
            AutoParty = new DadAutoPartyConfiguration
            {
                RegisteredOwnerId = "owner-local",
                RegisteredIslandId = "island-local",
                RegistrationFingerprint = localFingerprint,
                PilotArtifactSha256 = new string('c', 64),
                OwnerAcceptanceConfirmed = true,
                Pairings =
                [
                    new DadAutoPartyPairing
                    {
                        OwnerId = "owner-peer",
                        IslandId = "island-peer",
                        PublicKeyFingerprint = peerFingerprint,
                        KeyGeneration = 1,
                        ConfirmedAtUtc = DateTime.UtcNow,
                    },
                ],
            },
        };
        var service = new DadAutoPartyPilotFixtureService(configuration, () => throw new InvalidOperationException());
        var fixture = new DadAutoPartyPilotFixture(
            DadAutoPartyPilotFixtureService.FixtureSchema,
            true,
            55,
            peerFingerprint,
            configuration.AutoParty.PilotArtifactSha256,
            [
                new(localFingerprint, "19", true, true),
                new(peerFingerprint, "24", true, false),
            ]);
        var path = Path.Combine(Path.GetTempPath(), $"dad-pilot-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(fixture, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var result = await service.ImportAsync(path);

            Assert.False(result.Succeeded);
            Assert.Equal("dad-pilot-fixture-mismatch", result.SafeCode);
            Assert.Empty(configuration.PlannerGroups);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RegistrationAndPairingStayReviewGated()
    {
        var configuration = new DadAutoPartyConfiguration { Enabled = true };
        var identityStore = new FakeIdentityStore();
        using var service = Service(configuration, identityStore);
        var registration = new DadAutoPartyRegistrationImport(
            Owner,
            LocalIsland,
            new string('A', 64),
            1,
            [1, 2, 3, 4]);

        var result = await service.ImportRegistrationAsync(registration, confirmReplacement: false);
        var challenge = service.BeginPairing(
            new OwnerIdentity(new OwnerId(Owner), new IslandId(SenderIsland), new string('A', 64), 1),
            new IslandIdentity(new IslandId(SenderIsland), new OwnerId(Owner), new string('A', 64), 1));

        Assert.False(result.Allowed);
        Assert.Equal("dad-registration-disabled-pending-review", result.SafeCode);
        Assert.Null(challenge);
        Assert.Equal(0, identityStore.StoreCalls);
        Assert.Equal(string.Empty, configuration.EndpointIdentityReference);
    }

    [Fact]
    public async Task ExplicitRegistrationUsesOpaqueIdentityReferenceAndRequiresReplacementConfirmation()
    {
        var configuration = new DadAutoPartyConfiguration
        {
            Enabled = true,
            PairingEnabled = true,
            OwnerAcceptanceConfirmed = true,
            EnrollmentReceiptId = Guid.NewGuid().ToString("D"),
            PilotArtifactSha256 = new string('a', 64),
        };
        var identityStore = new FakeIdentityStore();
        using var service = Service(configuration, identityStore);
        var registration = new DadAutoPartyRegistrationImport(
            Owner,
            LocalIsland,
            new string('A', 64),
            1,
            [1, 2, 3, 4]);

        var first = await service.ImportRegistrationAsync(registration, confirmReplacement: false);
        var deniedReplacement = await service.ImportRegistrationAsync(registration, confirmReplacement: false);
        var replaced = await service.ImportRegistrationAsync(registration, confirmReplacement: true);

        Assert.True(first.Allowed);
        Assert.False(deniedReplacement.Allowed);
        Assert.Equal("dad-registration-replacement-confirmation-required", deniedReplacement.SafeCode);
        Assert.True(replaced.Allowed);
        Assert.StartsWith("identity-", configuration.EndpointIdentityReference, StringComparison.Ordinal);
        Assert.Equal(2, identityStore.StoreCalls);
        Assert.Equal(1, identityStore.DeleteCalls);
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
            memberCount: 4,
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
        Assert.Equal(ExecutionOutcome.Denied, queued.Outcome);
        Assert.Equal("dad-formation-only-queue-denied", queued.SafeCode);
        Assert.Equal(ExecutionOutcome.Denied, settled.Outcome);
        Assert.Equal("dad-formation-only-settle-denied", settled.SafeCode);
        Assert.True(restored.ProfileRestored);
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
            DadAutoPartyFakeExecutionFacade.CreateObservedPartyReceipt(proposalId, 4, fixture.Generation));

        Assert.Equal(ExecutionOutcome.Denied, result.Outcome);
        Assert.Equal("dad-invite-locator-invalid", result.SafeCode);
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
        Assert.Equal(DadAutoPartyComponentState.Disabled, status.Execution.State);
    }

    [Fact]
    public void PairingAndExecutionRequireReceiptPairingAndLocalEnablement()
    {
        var configuration = new DadAutoPartyConfiguration { Enabled = true };
        using var service = Service(configuration, new FakeIdentityStore());

        var pairingDenied = service.SetPairingEnabled(true);
        configuration.OwnerAcceptanceConfirmed = true;
        configuration.EnrollmentReceiptId = Guid.NewGuid().ToString("D");
        configuration.PilotArtifactSha256 = new string('b', 64);
        var pairingEnabled = service.SetPairingEnabled(true);
        var executionDenied = service.SetExecutionEnabled(true);

        Assert.False(pairingDenied.Allowed);
        Assert.Equal("dad-pairing-registration-pending", pairingDenied.SafeCode);
        Assert.True(pairingEnabled.Allowed);
        Assert.False(executionDenied.Allowed);
        Assert.Equal("dad-execution-prerequisites-pending", executionDenied.SafeCode);
    }

    [Fact]
    public void PilotExchangeRootApplyRequiresAllThreeGatesDisabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "dad-autoparty-root-gate", Guid.NewGuid().ToString("N"));
        var configuration = new DadAutoPartyConfiguration { Enabled = true };
        using var service = Service(configuration, new FakeIdentityStore());

        var enabled = service.ApplyPilotExchangeRoot(root);
        configuration.Enabled = false;
        configuration.PairingEnabled = true;
        var pairing = service.ApplyPilotExchangeRoot(root);
        configuration.PairingEnabled = false;
        configuration.ExecutionEnabled = true;
        var execution = service.ApplyPilotExchangeRoot(root);

        Assert.All([enabled, pairing, execution], decision =>
        {
            Assert.False(decision.Allowed);
            Assert.Equal("dad-pilot-exchange-root-gates-enabled", decision.SafeCode);
        });
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void PilotExchangeRootApplyCreatesManagedFoldersAndUpdatesDerivedPathsImmediately()
    {
        var root = Path.Combine(Path.GetTempPath(), "dad-autoparty-root-apply", Guid.NewGuid().ToString("N"));
        var configuration = new DadAutoPartyConfiguration();
        var saved = 0;
        using var service = new DadAutoPartyService(
            configuration,
            new FakeIdentityStore(),
            static () => true,
            () => saved++,
            static () => true);
        try
        {
            var result = service.ApplyPilotExchangeRoot(root + Path.DirectorySeparatorChar);

            Assert.True(result.Allowed, result.SafeCode);
            Assert.Equal("dad-pilot-exchange-root-applied", result.SafeCode);
            Assert.Equal(Path.GetFullPath(root), configuration.PilotExchangeRoot);
            Assert.Equal(Path.Combine(root, "pilot-courier"), configuration.CourierRootPath);
            Assert.Equal(Path.Combine(root, "pilot-input"), configuration.GetPilotInputRoot());
            Assert.Equal(Path.Combine(root, "pilot-receipts"), configuration.GetPilotReceiptRoot());
            Assert.Equal(Path.Combine(root, "pilot-input", "pilot-fixture.json"), configuration.GetPilotFixturePath());
            Assert.Equal(1, saved);
            Assert.All(new[] { "pilot-input", "pilot-receipts", "pilot-courier", "plugin" },
                folder => Assert.True(Directory.Exists(Path.Combine(root, folder))));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PilotExchangeRootApplyFailsWithSafeCodeWhenTargetIsUnavailable()
    {
        var file = Path.Combine(Path.GetTempPath(), $"dad-autoparty-root-file-{Guid.NewGuid():N}");
        File.WriteAllText(file, "not-a-directory");
        try
        {
            var configuration = new DadAutoPartyConfiguration();
            using var service = Service(configuration, new FakeIdentityStore());

            var result = service.ApplyPilotExchangeRoot(Path.Combine(file, "share"));

            Assert.False(result.Allowed);
            Assert.Equal("dad-pilot-exchange-root-unavailable", result.SafeCode);
            Assert.DoesNotContain(file, result.SafeCode, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(DadAutoPartyConfiguration.DefaultPilotExchangeRoot, configuration.PilotExchangeRoot);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("relative\\pilot")]
    [InlineData("C:\\")]
    public void PilotExchangeRootApplyRejectsRelativeAndDriveRootPaths(string root)
    {
        var configuration = new DadAutoPartyConfiguration();
        using var service = Service(configuration, new FakeIdentityStore());

        var result = service.ApplyPilotExchangeRoot(root);

        Assert.False(result.Allowed);
        Assert.Equal("dad-pilot-exchange-root-invalid", result.SafeCode);
    }

    [Fact]
    public async Task PublicIdentityExportContainsNoPrivateKeyAndReceiptIsArtifactBound()
    {
        var root = Path.Combine(Path.GetTempPath(), "dad-autoparty-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new DadAutoPartyConfiguration();
            var packages = new DadAutoPartyIdentityPackageService(
                configuration,
                new FakeIdentityStore(),
                static () => { });
            var generated = await packages.GenerateAsync("Pilot_A");
            var exported = await packages.ExportPublicAsync(root);
            var identityJson = await File.ReadAllTextAsync(exported.OutputPath);
            var receiptPath = Path.Combine(root, "Pilot_A.apregistration");
            var receipt = new DadAutoPartyEnrollmentReceipt(
                DadAutoPartyIdentityPackageService.EnrollmentReceiptSchema,
                Guid.NewGuid().ToString("D"),
                configuration.RegisteredOwnerId,
                configuration.RegisteredIslandId,
                1,
                configuration.RegistrationFingerprint,
                new string('c', 64),
                true,
                DateTime.UtcNow,
                [new DadAutoPartyEnrollmentPeer(
                    "owner-peer",
                    "island-peer",
                    new string('D', 64),
                    1)]);
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt));

            var imported = await packages.ImportEnrollmentReceiptAsync(receiptPath);

            Assert.True(generated.Succeeded);
            Assert.True(exported.Succeeded);
            Assert.DoesNotContain("PrivateKey", identityJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SigningPrivate", identityJson, StringComparison.OrdinalIgnoreCase);
            Assert.True(imported.Succeeded);
            Assert.True(configuration.OwnerAcceptanceConfirmed);
            Assert.Equal(new string('c', 64), configuration.PilotArtifactSha256);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FileCourierIsOutboundOnlyBoundedAndIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "dad-autoparty-courier", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "pilot-courier"));
        try
        {
            var configuration = new DadAutoPartyConfiguration
            {
                Enabled = true,
                RegisteredIslandId = LocalIsland,
                OwnerAcceptanceConfirmed = true,
                EnrollmentReceiptId = Guid.NewGuid().ToString("D"),
                PilotExchangeRoot = root,
            };
            using var courier = new DadAutoPartyFileCourierAdapter(configuration);
            var envelope = OpaqueEnvelope.Create(
                AutoPartyProtocol.CurrentVersion,
                Guid.NewGuid(),
                new IslandId(LocalIsland),
                new IslandId(SenderIsland),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1),
                1,
                "test",
                [1, 2, 3]);

            var health = await courier.GetHealthAsync();
            var first = await courier.SendAsync(envelope);
            var duplicate = await courier.SendAsync(envelope);

            Assert.Equal(AutoPartyTransportHealthState.Ready, health.State);
            Assert.True(first.Accepted);
            Assert.True(duplicate.Accepted);
            Assert.Equal("dad-file-courier-already-enqueued", duplicate.SafeCode);
            Assert.Single(Directory.GetFiles(root, "*.apout", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExistingFileCourierUsesAppliedRootWithoutPluginReload()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), "dad-autoparty-dynamic-a", Guid.NewGuid().ToString("N"));
        var secondRoot = Path.Combine(Path.GetTempPath(), "dad-autoparty-dynamic-b", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(firstRoot, "pilot-courier"));
        try
        {
            var configuration = new DadAutoPartyConfiguration
            {
                PilotExchangeRoot = firstRoot,
                RegisteredIslandId = LocalIsland,
                OwnerAcceptanceConfirmed = true,
                EnrollmentReceiptId = Guid.NewGuid().ToString("D"),
            };
            using var courier = new DadAutoPartyFileCourierAdapter(configuration);
            using var service = Service(configuration, new FakeIdentityStore());

            var applied = service.ApplyPilotExchangeRoot(secondRoot);
            configuration.Enabled = true;
            var health = await courier.GetHealthAsync();
            var delivery = OpaqueEnvelope.Create(
                AutoPartyProtocol.CurrentVersion,
                Guid.NewGuid(),
                new IslandId(LocalIsland),
                new IslandId(SenderIsland),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(1),
                1,
                "test",
                [1, 2, 3]);
            var sent = await courier.SendAsync(delivery);

            Assert.True(applied.Allowed, applied.SafeCode);
            Assert.Equal(AutoPartyTransportHealthState.Ready, health.State);
            Assert.True(sent.Accepted, sent.SafeCode);
            Assert.Empty(Directory.GetFiles(firstRoot, "*.apout", SearchOption.AllDirectories));
            Assert.Single(Directory.GetFiles(secondRoot, "*.apout", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(firstRoot))
                Directory.Delete(firstRoot, true);
            if (Directory.Exists(secondRoot))
                Directory.Delete(secondRoot, true);
        }
    }

    [Fact]
    public async Task PilotCourierProbeIsAcknowledgedOnlyFromAnActivelyPairedIsland()
    {
        var root = Path.Combine(Path.GetTempPath(), "dad-autoparty-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "pilot-courier"));
        try
        {
            var sender = ProbeConfiguration(SenderIsland, LocalIsland, root);
            var receiver = ProbeConfiguration(LocalIsland, SenderIsland, root);
            using var senderService = Service(sender, new FakeIdentityStore());
            using var receiverService = Service(receiver, new FakeIdentityStore());
            senderService.AttachVerifiedCourier(new DadAutoPartyFileCourierAdapter(sender));
            receiverService.AttachVerifiedCourier(new DadAutoPartyFileCourierAdapter(receiver));

            var sent = await senderService.SendPilotCourierProbeAsync();
            Assert.False(receiverService.SetExecutionEnabled(true).Allowed);
            var outbox = Assert.Single(Directory.GetFiles(root, "*.apout", SearchOption.AllDirectories));
            var envelopeId = Path.GetFileNameWithoutExtension(outbox);
            var inbox = Path.Combine(root, "pilot-courier", "inbox", IslandFolder(LocalIsland));
            Directory.CreateDirectory(inbox);
            File.Copy(outbox, Path.Combine(inbox, envelopeId + ".apin"));

            for (var attempt = 0; attempt < 40 && !receiver.PilotCourierProbeVerified; attempt++)
            {
                receiverService.Update(dadPluginEnabled: true);
                await Task.Delay(25);
            }

            Assert.True(sent.Succeeded, sent.SafeCode);
            Assert.True(receiver.PilotCourierProbeVerified);
            Assert.True(receiverService.SetExecutionEnabled(true).Allowed);
            Assert.Single(Directory.GetFiles(root, "*.apack", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(root, "*.apin", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
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
        var configuration = new DadAutoPartyConfiguration
        {
            Enabled = true,
            PairingEnabled = true,
            ExecutionEnabled = true,
            EndpointIdentityReference = "identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            RegisteredOwnerId = Owner,
            RegisteredIslandId = LocalIsland,
        };
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
        Assert.False(configuration.PairingEnabled);
        Assert.False(configuration.ExecutionEnabled);
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
        var configuration = new DadAutoPartyConfiguration
        {
            Enabled = true,
            ExecutionEnabled = true,
            RegisteredOwnerId = Owner,
            RegisteredIslandId = LocalIsland,
        };
        configuration.Pairings.Add(new DadAutoPartyPairing
        {
            OwnerId = Owner,
            IslandId = SenderIsland,
            PublicKeyFingerprint = new string('A', 64),
            KeyGeneration = 1,
            ConfirmedAtUtc = DateTime.UtcNow,
        });
        configuration.Grants.Add(new DadAutoPartyGrant
        {
            GrantId = Guid.NewGuid().ToString("D"),
            OwnerId = Owner,
            IslandId = SenderIsland,
            OpaqueCharacterId = Character,
            RequestedJobId = Job,
            ActivityId = Activity,
            Permissions = SessionPermission.All,
            IssuedAtUtc = DateTime.UtcNow - TimeSpan.FromMinutes(1),
            ExpiresAtUtc = DateTime.UtcNow + TimeSpan.FromMinutes(20),
        });
        var service = Service(configuration, new FakeIdentityStore(), localSafetyAllowsExecution);
        var proposal = Proposal(Guid.NewGuid());
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
        return new(service, proposal, lease.StateGeneration);
    }

    private static DadAutoPartyConfiguration ProbeConfiguration(
        string localIsland,
        string peerIsland,
        string root)
    {
        var configuration = new DadAutoPartyConfiguration
        {
            Enabled = true,
            PairingEnabled = true,
            RegisteredOwnerId = "owner-" + localIsland,
            RegisteredIslandId = localIsland,
            OwnerAcceptanceConfirmed = true,
            EnrollmentReceiptId = Guid.NewGuid().ToString("D"),
            PilotExchangeRoot = root,
            PilotPlannerGroupId = "pilot-group",
            PilotQueueAuthorityFingerprint = new string('C', 64),
        };
        configuration.Pairings.Add(new DadAutoPartyPairing
        {
            OwnerId = "owner-" + peerIsland,
            IslandId = peerIsland,
            PublicKeyFingerprint = new string(localIsland == LocalIsland ? 'A' : 'B', 64),
            KeyGeneration = 1,
            ConfirmedAtUtc = DateTime.UtcNow,
        });
        return configuration;
    }

    private static string IslandFolder(string islandId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(islandId)))[..32]
            .ToLowerInvariant();

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
