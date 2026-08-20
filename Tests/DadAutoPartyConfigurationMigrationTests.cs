using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoParty.Contracts;
using AutoParty.Core.Cryptography;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAutoPartyConfigurationMigrationTests
{
    [Fact]
    public void MissingDirectoryGenerationDefaultsNormalizesAndClonesAsOne()
    {
        var autoParty = JsonSerializer.Deserialize<DadAutoPartyConfiguration>("{}")!;

        Assert.Equal(1, autoParty.DirectoryGeneration);
        autoParty.DirectoryGeneration = 0;
        autoParty.Normalize();

        Assert.Equal(1, autoParty.DirectoryGeneration);
        Assert.Equal(1, autoParty.Clone().DirectoryGeneration);
    }

    [Fact]
    public void NewConfigurationUsesSchemaTwelveWithInertAutoPartyDefaults()
    {
        var configuration = new Configuration();

        Assert.Equal(12, configuration.Version);
        AssertInert(configuration.AutoParty);
        Assert.Null(typeof(DadAutoPartyConfiguration).GetProperty("PairingEnabled"));
        Assert.Null(typeof(DadAutoPartyConfiguration).GetProperty("ExecutionEnabled"));
        Assert.Null(typeof(DadAutoPartyConfiguration).GetProperty("DiscordGuildId"));
        Assert.Null(typeof(DadAutoPartyConfiguration).GetProperty("PilotExchangeRoot"));
        Assert.Null(typeof(DadAutoPartyConfiguration).GetProperty("MeasuredPilot"));
    }

    [Fact]
    public async Task SchemaTenToTwelvePreservesRegistrationTrustRoutesPoliciesAndOrdinaryConfiguration()
    {
        var fixture = await CreateRegistrationFixtureAsync();
        using var identityStore = fixture.IdentityStore;
        var configuration = fixture.Configuration;
        var autoParty = configuration.AutoParty;
        var now = DateTime.UtcNow;
        configuration.Version = 10;
        configuration.PluginEnabled = true;
        configuration.ServerListenPort = 5544;
        configuration.PlannerGroups = [new DadPlannerGroup { DisplayName = "Preserved planner" }];
        configuration.Schedules = [new DadScheduleDefinition { DisplayName = "Preserved schedule" }];
        autoParty.StandingShareScope = DadAutoPartyCrewShareScope.AllCharacters;
        autoParty.CrewIdentities =
        [
            new DadAutoPartyCrewIdentity
            {
                RosterIdentityKey = "roster-preserved",
                OpaqueCharacterId = "character-preserved",
            },
        ];
        autoParty.Grants =
        [
            new DadAutoPartyGrant
            {
                GrantId = Guid.NewGuid().ToString("D"),
                ProposalId = Guid.NewGuid().ToString("D"),
                OwnerId = "owner-preserved",
                IslandId = "island-preserved",
                OpaqueCharacterId = "character-preserved",
                RequestedJobId = "job-paladin",
                ActivityId = "activity-preserved",
                Permissions = SessionPermission.Reserve,
                IssuedAtUtc = now,
                ExpiresAtUtc = now.AddMinutes(5),
                MaximumUses = 1,
            },
        ];
        autoParty.Listings =
        [
            new DadAutoPartyListing
            {
                ListingId = Guid.NewGuid().ToString("D"),
                OwnerId = "owner-preserved",
                SharingIslandId = "island-preserved",
                SharingEndpointAlias = "Preserved endpoint",
                EffectiveShareMode = DadAutoPartyCharacterShareMode.CharacterList,
                EffectivePolicyHash = "policy-preserved",
                OpaqueCharacterId = "character-preserved",
                DisplayLabel = "Preserved character",
                AllowedJobIds = ["job-paladin"],
                AllowedActivityIds = ["activity-preserved"],
                ExpiresAtUtc = now.AddMinutes(5),
            },
        ];
        autoParty.RemoteBindings =
        [
            new DadAutoPartyRemoteBinding
            {
                FleetRowId = "fleet-row-preserved",
                OpaqueCharacterId = "character-preserved",
                OwnerId = "owner-preserved",
                IslandId = "island-preserved",
                RequestedJobId = "job-paladin",
                OwnsQueueAuthority = true,
                OwnerConsentConfirmed = true,
            },
        ];
        var revoked = Pairing("peer-revoked", now, active: false);
        revoked.RevokedAtUtc = now;
        autoParty.Pairings.Add(revoked);
        autoParty.PairingInviteToken = "APP1.obsolete-schema-ten-state";
        autoParty.PairingAttemptId = Guid.NewGuid().ToString("D");
        autoParty.PairingAttemptExpiresAtUtc = now.AddMinutes(10);
        autoParty.PairingAttemptSubmitted = true;
        autoParty.PairingPeerAttemptId = Guid.NewGuid().ToString("D");
        autoParty.PairingPeerIslandId = "island-obsolete-peer";
        var preserved = autoParty.Clone();

        Assert.True(DadAutoPartyConfigurationMigration.Migrate(
            configuration,
            identityStore,
            fixture.WebhookStore));

        Assert.Equal(12, configuration.Version);
        Assert.True(configuration.PluginEnabled);
        Assert.Equal(5544, configuration.ServerListenPort);
        Assert.Equal("Preserved planner", Assert.Single(configuration.PlannerGroups).DisplayName);
        Assert.Equal("Preserved schedule", Assert.Single(configuration.Schedules).DisplayName);
        Assert.Equal(DadAutoPartyRegistrationState.Active, autoParty.RegistrationState);
        Assert.Equal(fixture.RegistrationId.ToString("D"), autoParty.RegistrationId);
        Assert.Equal(DadAutoPartyRegistrationRecoveryState.Active, autoParty.RegistrationRecoveryState);
        Assert.Equal(preserved.RouteId, autoParty.RouteId);
        Assert.Equal(preserved.EndpointIdentityReference, autoParty.EndpointIdentityReference);
        Assert.Equal(preserved.WebhookCredentialReference, autoParty.WebhookCredentialReference);
        Assert.Equal(preserved.UplinkEpochId, autoParty.UplinkEpochId);
        Assert.Equal(preserved.DownlinkEpochId, autoParty.DownlinkEpochId);
        Assert.Equal(preserved.MailboxEpochGeneration, autoParty.MailboxEpochGeneration);
        Assert.Equal(preserved.RelayKeyGeneration, autoParty.RelayKeyGeneration);
        Assert.Equal(preserved.RelaySigningPublicKey, autoParty.RelaySigningPublicKey);
        Assert.Equal(preserved.RelayAgreementPublicKey, autoParty.RelayAgreementPublicKey);
        Assert.Equal(preserved.SigningPublicKey, autoParty.SigningPublicKey);
        Assert.Equal(preserved.EncryptionPublicKey, autoParty.EncryptionPublicKey);
        Assert.Equal(preserved.EndpointKeyGeneration, autoParty.EndpointKeyGeneration);
        Assert.Equal(2, autoParty.Pairings.Count);
        Assert.Contains(autoParty.Pairings, pairing => pairing.IsActive);
        Assert.Contains(autoParty.Pairings, pairing => pairing.RevokedAtUtc != null);
        Assert.Equal(
            preserved.Pairings.Select(static pairing => pairing.TranscriptHash).Order(),
            autoParty.Pairings.Select(static pairing => pairing.TranscriptHash).Order());
        var serializedPairings = JsonSerializer.Serialize(autoParty.Pairings);
        Assert.DoesNotContain("ConfirmationCode", serializedPairings, StringComparison.Ordinal);
        Assert.DoesNotContain("ApprovalRelay", serializedPairings, StringComparison.Ordinal);
        Assert.Empty(autoParty.PendingPairings);
        Assert.Single(autoParty.CrewIdentities);
        Assert.Single(autoParty.Grants);
        Assert.Single(autoParty.Listings);
        Assert.Single(autoParty.RemoteBindings);
        Assert.True(autoParty.StandingSharePolicy.Enabled);
        Assert.Equal(DadAutoPartyCrewShareScope.AllCharacters, autoParty.StandingShareScope);
        Assert.Equal(string.Empty, autoParty.PairingInviteToken);
        Assert.Equal(string.Empty, autoParty.PairingAttemptId);
        Assert.False(autoParty.PairingAttemptSubmitted);
        Assert.Equal(string.Empty, autoParty.PairingPeerIslandId);
    }

    [Fact]
    public void SchemaElevenMigratesLegacyPairingAliasesIntoNormalizedStableMap()
    {
        var now = DateTime.UtcNow;
        var legacyPairing = Pairing(" island-legacy ", now, active: true);
        legacyPairing.LocalAlias = " Legacy_DAD ";
        var mappedPairing = Pairing("island-mapped", now, active: true);
        mappedPairing.LocalAlias = "Legacy_Loses";
        var configuration = new Configuration
        {
            Version = 11,
            AutoParty = new DadAutoPartyConfiguration
            {
                PairedDadAliases = new Dictionary<string, string>
                {
                    [" island-mapped "] = " Map_Wins ",
                },
                Pairings = [legacyPairing, mappedPairing],
            },
        };

        Assert.True(DadAutoPartyConfigurationMigration.Migrate(
            configuration,
            new RecordingIdentityStore(),
            new RecordingWebhookStore()));

        Assert.Equal(12, configuration.Version);
        Assert.Equal("Legacy_DAD", configuration.AutoParty.PairedDadAliases["island-legacy"]);
        Assert.Equal("Map_Wins", configuration.AutoParty.PairedDadAliases["island-mapped"]);
        Assert.Null(legacyPairing.LocalAlias);
        Assert.Null(mappedPairing.LocalAlias);
        Assert.DoesNotContain(
            "LocalAlias",
            JsonSerializer.Serialize(configuration.AutoParty.Pairings),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LocalAlias",
            Newtonsoft.Json.JsonConvert.SerializeObject(configuration.AutoParty.Pairings),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaNineResetClearsAutoPartyAndPreservesUnrelatedConfigurationAndFleet()
    {
        var identityStore = new RecordingIdentityStore();
        var webhookStore = new RecordingWebhookStore();
        var configuration = PopulatedSchemaNine();
        var plans = configuration.PlannerGroups;
        var schedules = configuration.Schedules;
        var fleet = configuration.AutoPartyFleet;

        Assert.True(DadAutoPartyConfigurationMigration.Migrate(
            configuration,
            identityStore,
            webhookStore));

        Assert.Equal(12, configuration.Version);
        Assert.True(configuration.PluginEnabled);
        Assert.Equal(5544, configuration.ServerListenPort);
        Assert.Equal("account-current", configuration.ClientAccountId);
        Assert.Equal("account-last", configuration.LastAccountId);
        Assert.Same(plans, configuration.PlannerGroups);
        Assert.Same(schedules, configuration.Schedules);
        Assert.Same(fleet, configuration.AutoPartyFleet);
        Assert.True(configuration.AutoPartyFleet.Enabled);
        Assert.Equal(7, configuration.AutoPartyFleet.Revision);
        Assert.Equal("fleet-row-one", Assert.Single(configuration.AutoPartyFleet.Rows).RowId);
        AssertInert(configuration.AutoParty);
        Assert.Equal(
            ["identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"],
            identityStore.DeletedReferences);
        Assert.Equal(
            ["webhook-mailbox-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"],
            webhookStore.DeletedReferences);
    }

    [Fact]
    public async Task SchemaNineResetDeletesExactReferencedDpapiFiles()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "dad-autoparty-schema-ten", Guid.NewGuid().ToString("N"));
        try
        {
            var identityStore = new DadAutoPartyDpapiEndpointIdentityStore(Path.Combine(root, "identity"));
            var webhookStore = new DadAutoPartyDpapiWebhookCredentialStore(Path.Combine(root, "mailbox"));
            var identityReference = await identityStore.StoreAsync(new byte[] { 1, 2, 3, 4 });
            var webhookReference = await webhookStore.StoreAsync(new DadAutoPartyWebhookCredential(
                "123456789",
                new string('a', 64),
                "987654321"));
            var configuration = PopulatedSchemaNine(identityReference, webhookReference);

            Assert.True(DadAutoPartyConfigurationMigration.Migrate(
                configuration,
                identityStore,
                webhookStore));

            Assert.Equal(12, configuration.Version);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "identity"), "*.dpapi"));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "mailbox"), "*.dpapi"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MissingAndMalformedCredentialReferencesAreAcceptedAsDiscarded()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "dad-autoparty-schema-ten", Guid.NewGuid().ToString("N"));
        try
        {
            var identityStore = new DadAutoPartyDpapiEndpointIdentityStore(Path.Combine(root, "identity"));
            var webhookStore = new DadAutoPartyDpapiWebhookCredentialStore(Path.Combine(root, "mailbox"));
            var missing = PopulatedSchemaNine(
                "identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "webhook-mailbox-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var malformed = PopulatedSchemaNine("invalid identity reference", "invalid mailbox reference");

            Assert.True(DadAutoPartyConfigurationMigration.Migrate(missing, identityStore, webhookStore));
            Assert.True(DadAutoPartyConfigurationMigration.Migrate(malformed, identityStore, webhookStore));

            Assert.Equal(12, missing.Version);
            Assert.Equal(12, malformed.Version);
            AssertInert(missing.AutoParty);
            AssertInert(malformed.AutoParty);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LockedCredentialFilePreventsVersionAdvanceAndRetryCompletesReset()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "dad-autoparty-schema-ten", Guid.NewGuid().ToString("N"));
        var identityStore = new DadAutoPartyDpapiEndpointIdentityStore(Path.Combine(root, "identity"));
        var webhookRoot = Path.Combine(root, "mailbox");
        var webhookStore = new DadAutoPartyDpapiWebhookCredentialStore(webhookRoot);
        const string webhookReference = "webhook-mailbox-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var webhookPath = Path.Combine(webhookRoot, webhookReference + ".dpapi");
        var configuration = PopulatedSchemaNine(string.Empty, webhookReference);
        var priorAutoParty = configuration.AutoParty;
        try
        {
            Directory.CreateDirectory(webhookRoot);
            await File.WriteAllBytesAsync(webhookPath, new byte[] { 1, 2, 3, 4 });
            using (var locked = new FileStream(
                       webhookPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.None))
            {
                var exception = Assert.Throws<InvalidOperationException>(() =>
                    DadAutoPartyConfigurationMigration.Migrate(configuration, identityStore, webhookStore));
                Assert.Equal(
                    "AutoParty schema-12 protected-state reset could not complete.",
                    exception.Message);
                Assert.Equal(9, configuration.Version);
                Assert.Same(priorAutoParty, configuration.AutoParty);
            }

            Assert.True(DadAutoPartyConfigurationMigration.Migrate(
                configuration,
                identityStore,
                webhookStore));
            Assert.Equal(12, configuration.Version);
            Assert.False(File.Exists(webhookPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SchemaTwelveMigrationIsIdempotent()
    {
        var identityStore = new RecordingIdentityStore();
        var webhookStore = new RecordingWebhookStore();
        var configuration = PopulatedSchemaNine();

        Assert.True(DadAutoPartyConfigurationMigration.Migrate(
            configuration,
            identityStore,
            webhookStore));
        Assert.False(DadAutoPartyConfigurationMigration.Migrate(
            configuration,
            identityStore,
            webhookStore));

        Assert.Single(identityStore.DeletedReferences);
        Assert.Single(webhookStore.DeletedReferences);
        AssertInert(configuration.AutoParty);
    }

    [Fact]
    public void LegacyFullRosterStandingPolicyBecomesDisabledCharacterListWithoutProtectedStateReset()
    {
        var configuration = new Configuration
        {
            Version = DadAutoPartyConfigurationMigration.CurrentVersion,
            AutoParty = new DadAutoPartyConfiguration
            {
                RegistrationState = DadAutoPartyRegistrationState.Active,
                RegistrationId = "11111111-1111-4111-8111-111111111111",
                RouteId = "route-current",
                WebhookCredentialReference = "webhook-mailbox-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                UplinkEpochId = "22222222-2222-4222-8222-222222222222",
                DownlinkEpochId = "33333333-3333-4333-8333-333333333333",
                MailboxEpochGeneration = 4,
                RelaySigningPublicKey = Convert.ToBase64String(new byte[32]),
                RelayAgreementPublicKey = Convert.ToBase64String(new byte[32]),
                EndpointIdentityReference = "endpoint-identity-current",
                RegisteredOwnerId = "owner-current",
                RegisteredIslandId = "island-current",
                StandingSharePolicy = new DadAutoPartySharePolicy
                {
                    Mode = DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild,
                    Enabled = true,
                    Revision = 7,
                },
            },
        };

        configuration.MigrateTransportSettings();

        Assert.Equal(DadAutoPartyRegistrationState.Active, configuration.AutoParty.RegistrationState);
        Assert.Equal("11111111-1111-4111-8111-111111111111", configuration.AutoParty.RegistrationId);
        Assert.Equal("endpoint-identity-current", configuration.AutoParty.EndpointIdentityReference);
        Assert.Equal("island-current", configuration.AutoParty.RegisteredIslandId);
        Assert.Equal(DadAutoPartyCharacterShareMode.CharacterList, configuration.AutoParty.StandingSharePolicy.Mode);
        Assert.False(configuration.AutoParty.StandingSharePolicy.Enabled);
        Assert.Empty(configuration.AutoParty.StandingSharePolicy.CharacterHandles);
        Assert.Equal(7, configuration.AutoParty.StandingSharePolicy.Revision);
    }

    [Fact]
    public async Task CurrentIdentityUsesPascalCaseAndCreatesVerifiableChallenge()
    {
        var configuration = new DadAutoPartyConfiguration();
        using var identityStore = new MemoryIdentityStore();
        var saves = 0;
        var service = new DadAutoPartyIdentityPackageService(
            configuration,
            identityStore,
            () => saves++);

        var generated = await service.GenerateChallengeAsync("schema-ten-endpoint");

        Assert.True(generated.Succeeded, generated.SafeCode);
        Assert.True(saves >= 2);
        var identityJson = identityStore.Snapshot();
        try
        {
            using var document = JsonDocument.Parse(identityJson);
            Assert.True(document.RootElement.TryGetProperty("OwnerId", out _));
            Assert.True(document.RootElement.TryGetProperty("IslandId", out _));
            Assert.True(document.RootElement.TryGetProperty("KeyGeneration", out _));
            Assert.True(document.RootElement.TryGetProperty("SigningPrivateKey", out _));
            Assert.True(document.RootElement.TryGetProperty("EncryptionPrivateKey", out _));
            Assert.False(document.RootElement.TryGetProperty("ownerId", out _));
            Assert.False(document.RootElement.TryGetProperty("islandId", out _));
            Assert.False(document.RootElement.TryGetProperty("keyGeneration", out _));
            Assert.False(document.RootElement.TryGetProperty("signingPrivateKey", out _));
            Assert.False(document.RootElement.TryGetProperty("encryptionPrivateKey", out _));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identityJson);
        }

        var decoded = RegistrationCopyPasteCodec.DecodeChallenge(generated.OutputPath);
        var canonical = RegistrationCborCodec.EncodeUnsignedChallenge(decoded.Contract);
        var publicKey = Convert.FromBase64String(configuration.SigningPublicKey);
        try
        {
            Assert.Equal(configuration.RegisteredOwnerId, decoded.Contract.OwnerId.Value);
            Assert.Equal(configuration.RegisteredIslandId, decoded.Contract.IslandId.Value);
            Assert.True(DadAutoPartySigningService.Verify(
                publicKey,
                canonical,
                decoded.Signature.AsSpan()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    [Fact]
    public async Task ChallengeGenerationReusesIdsAllowsReadOnlyActiveRecoveryAndLocksPendingOrLostIdentity()
    {
        var configuration = new DadAutoPartyConfiguration();
        using var identityStore = new MemoryIdentityStore();
        var saves = 0;
        var service = new DadAutoPartyIdentityPackageService(configuration, identityStore, () => saves++);

        var first = await service.GenerateChallengeAsync("recovery-endpoint");
        var firstId = RegistrationCopyPasteCodec.DecodeChallenge(first.OutputPath).Contract.RegistrationId;
        var repeated = await service.GenerateChallengeAsync("recovery-endpoint");
        Assert.Equal(
            firstId,
            RegistrationCopyPasteCodec.DecodeChallenge(repeated.OutputPath).Contract.RegistrationId);

        configuration.RouteId = $"route-{firstId:N}";
        configuration.RegistrationId = Guid.NewGuid().ToString("D");
        var recovered = await service.GenerateChallengeAsync("recovery-endpoint");
        Assert.Equal(
            firstId,
            RegistrationCopyPasteCodec.DecodeChallenge(recovered.OutputPath).Contract.RegistrationId);

        configuration.RegistrationState = DadAutoPartyRegistrationState.Active;
        configuration.EndpointAlias = string.Empty;
        configuration.Pairings.Add(Pairing("peer-active", DateTime.UtcNow, active: true));
        configuration.StandingSharePolicy.Enabled = true;
        var activeSnapshot = JsonSerializer.Serialize(configuration);
        var activeRecoveryState = configuration.RegistrationRecoveryState;
        var savesBeforeActive = saves;
        var active = await service.GenerateChallengeAsync("recovery-endpoint");
        Assert.True(active.Succeeded, active.SafeCode);
        var activeChallenge = RegistrationCopyPasteCodec.DecodeChallenge(active.OutputPath).Contract;
        Assert.Equal(firstId, activeChallenge.RegistrationId);
        Assert.Equal("recovery-endpoint", activeChallenge.EndpointAlias);
        Assert.Equal(activeSnapshot, JsonSerializer.Serialize(configuration));
        Assert.Equal(activeRecoveryState, configuration.RegistrationRecoveryState);
        Assert.Equal(savesBeforeActive, saves);

        configuration.RegistrationState = DadAutoPartyRegistrationState.BootstrapImported;
        configuration.BootstrapExpiresAtUtc = DateTime.UtcNow.AddMinutes(5);
        var pendingSnapshot = JsonSerializer.Serialize(configuration);
        var pending = await service.GenerateChallengeAsync("recovery-endpoint");
        Assert.False(pending.Succeeded);
        Assert.Equal("dad-registration-activation-pending", pending.SafeCode);
        Assert.Equal(pendingSnapshot, JsonSerializer.Serialize(configuration));
        Assert.Equal(savesBeforeActive, saves);

        configuration.BootstrapExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        var expiredSnapshot = JsonSerializer.Serialize(configuration);
        var expired = await service.GenerateChallengeAsync("recovery-endpoint");
        Assert.True(expired.Succeeded, expired.SafeCode);
        Assert.Equal(
            firstId,
            RegistrationCopyPasteCodec.DecodeChallenge(expired.OutputPath).Contract.RegistrationId);
        Assert.Equal(expiredSnapshot, JsonSerializer.Serialize(configuration));
        Assert.Equal(savesBeforeActive, saves);

        _ = await identityStore.DeleteAsync(configuration.EndpointIdentityReference);
        configuration.RegistrationState = DadAutoPartyRegistrationState.Unregistered;
        configuration.RegistrationRecoveryState = DadAutoPartyRegistrationRecoveryState.IdentityLost;
        var lostSnapshot = JsonSerializer.Serialize(configuration);
        var lost = await service.GenerateChallengeAsync("recovery-endpoint");
        Assert.False(lost.Succeeded);
        Assert.Equal("dad-registration-identity-lost", lost.SafeCode);
        Assert.Equal(lostSnapshot, JsonSerializer.Serialize(configuration));
        Assert.Equal(savesBeforeActive, saves);
    }

    [Fact]
    public async Task StartupReloadRestoresValidatedRegistrationAndPreservesTrustState()
    {
        var fixture = await CreateRegistrationFixtureAsync();
        using var identityStore = fixture.IdentityStore;
        var serialized = JsonSerializer.Serialize(fixture.Configuration);
        var reloaded = JsonSerializer.Deserialize<Configuration>(serialized)!;

        _ = DadAutoPartyConfigurationMigration.Migrate(
            reloaded,
            identityStore,
            fixture.WebhookStore);

        Assert.Equal(DadAutoPartyRegistrationState.Active, reloaded.AutoParty.RegistrationState);
        Assert.Equal(fixture.RegistrationId.ToString("D"), reloaded.AutoParty.RegistrationId);
        Assert.Equal($"route-{fixture.RegistrationId:N}", reloaded.AutoParty.RouteId);
        Assert.Equal(DadAutoPartyRegistrationRecoveryState.Active, reloaded.AutoParty.RegistrationRecoveryState);
        Assert.Single(reloaded.AutoParty.Pairings);
        Assert.Empty(reloaded.AutoParty.PendingPairings);
        Assert.True(reloaded.AutoParty.StandingSharePolicy.Enabled);
        Assert.Equal(["character-one"], reloaded.AutoParty.StandingSharePolicy.CharacterHandles);
    }

    [Fact]
    public async Task StartupAdvancesConfigurationWhenProtectedMailboxHasANewerCompleteEpochPair()
    {
        var fixture = await CreateRegistrationFixtureAsync();
        using var identityStore = fixture.IdentityStore;
        var current = await fixture.WebhookStore.LoadAsync(
            fixture.Configuration.AutoParty.WebhookCredentialReference);
        var protectedNewer = current with
        {
            UplinkEpoch = current.UplinkEpoch! with
            {
                EpochId = Guid.NewGuid(),
                EpochGeneration = current.UplinkEpoch.EpochGeneration + 1,
            },
            DownlinkEpoch = current.DownlinkEpoch! with
            {
                EpochId = Guid.NewGuid(),
                EpochGeneration = current.DownlinkEpoch.EpochGeneration + 1,
            },
        };

        Assert.True(DadAutoPartyConfigurationMigration.Migrate(
            fixture.Configuration,
            identityStore,
            new MemoryWebhookStore(protectedNewer)));

        Assert.Equal(DadAutoPartyRegistrationState.Active, fixture.Configuration.AutoParty.RegistrationState);
        Assert.Equal(
            protectedNewer.UplinkEpoch!.EpochId.ToString("D"),
            fixture.Configuration.AutoParty.UplinkEpochId);
        Assert.Equal(
            protectedNewer.DownlinkEpoch!.EpochId.ToString("D"),
            fixture.Configuration.AutoParty.DownlinkEpochId);
        Assert.Equal(
            protectedNewer.UplinkEpoch.EpochGeneration,
            fixture.Configuration.AutoParty.MailboxEpochGeneration);
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("same-generation-id-mismatch")]
    [InlineData("island-mismatch")]
    [InlineData("relay-key-mismatch")]
    public async Task StartupRejectsProtectedMailboxBindingOrEpochConflicts(string conflict)
    {
        var fixture = await CreateRegistrationFixtureAsync();
        using var identityStore = fixture.IdentityStore;
        var current = await fixture.WebhookStore.LoadAsync(
            fixture.Configuration.AutoParty.WebhookCredentialReference);
        var protectedCredential = conflict switch
        {
            "rollback" => current with
            {
                UplinkEpoch = current.UplinkEpoch! with
                {
                    EpochGeneration = current.UplinkEpoch.EpochGeneration - 1,
                },
                DownlinkEpoch = current.DownlinkEpoch! with
                {
                    EpochGeneration = current.DownlinkEpoch.EpochGeneration - 1,
                },
            },
            "same-generation-id-mismatch" => current with
            {
                UplinkEpoch = current.UplinkEpoch! with { EpochId = Guid.NewGuid() },
            },
            "island-mismatch" => current with
            {
                UplinkEpoch = current.UplinkEpoch! with { IslandId = new IslandId("island-other") },
                DownlinkEpoch = current.DownlinkEpoch! with { IslandId = new IslandId("island-other") },
            },
            "relay-key-mismatch" => current with
            {
                RelayPublicKeys = new EndpointPublicKeys(
                    current.RelayPublicKeys!.KeyVersion,
                    current.RelayPublicKeys.SigningKeyId,
                    ImmutableArray.CreateRange(Enumerable.Repeat(
                        (byte)0x71,
                        AutoPartyProtocol.Ed25519PublicKeyBytes)),
                    current.RelayPublicKeys.AgreementKeyId,
                    current.RelayPublicKeys.X25519PublicKey),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(conflict)),
        };

        Assert.True(DadAutoPartyConfigurationMigration.Migrate(
            fixture.Configuration,
            identityStore,
            new MemoryWebhookStore(protectedCredential)));

        Assert.Equal(
            DadAutoPartyRegistrationState.Unregistered,
            fixture.Configuration.AutoParty.RegistrationState);
        Assert.Equal(
            DadAutoPartyRegistrationRecoveryState.RecoveryAvailable,
            fixture.Configuration.AutoParty.RegistrationRecoveryState);
    }

    [Fact]
    public async Task StartupKeepsValidatedBootstrapImportPendingUntilAuthenticatedReceipt()
    {
        var fixture = await CreateRegistrationFixtureAsync();
        using var identityStore = fixture.IdentityStore;
        var expiresAt = DateTime.UtcNow.AddMinutes(5);
        fixture.Configuration.AutoParty.RegistrationState = DadAutoPartyRegistrationState.BootstrapImported;
        fixture.Configuration.AutoParty.BootstrapExpiresAtUtc = expiresAt;

        _ = DadAutoPartyConfigurationMigration.Migrate(
            fixture.Configuration,
            identityStore,
            fixture.WebhookStore);

        Assert.Equal(
            DadAutoPartyRegistrationState.BootstrapImported,
            fixture.Configuration.AutoParty.RegistrationState);
        Assert.Equal(expiresAt, fixture.Configuration.AutoParty.BootstrapExpiresAtUtc);
        Assert.Single(fixture.Configuration.AutoParty.Pairings);
        Assert.Empty(fixture.Configuration.AutoParty.PendingPairings);
    }

    [Fact]
    public async Task StartupMakesExpiredBootstrapImportRecoverableWithoutDiscardingTrust()
    {
        var fixture = await CreateRegistrationFixtureAsync();
        using var identityStore = fixture.IdentityStore;
        fixture.Configuration.AutoParty.RegistrationState = DadAutoPartyRegistrationState.BootstrapImported;
        fixture.Configuration.AutoParty.BootstrapExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);

        Assert.True(DadAutoPartyConfigurationMigration.Migrate(
            fixture.Configuration,
            identityStore,
            fixture.WebhookStore));

        Assert.Equal(DadAutoPartyRegistrationState.Unregistered, fixture.Configuration.AutoParty.RegistrationState);
        Assert.Equal(
            DadAutoPartyRegistrationRecoveryState.RecoveryAvailable,
            fixture.Configuration.AutoParty.RegistrationRecoveryState);
        Assert.Equal(default, fixture.Configuration.AutoParty.BootstrapExpiresAtUtc);
        Assert.Single(fixture.Configuration.AutoParty.Pairings);
        Assert.Empty(fixture.Configuration.AutoParty.PendingPairings);
        Assert.True(fixture.Configuration.AutoParty.StandingSharePolicy.Enabled);
    }

    [Fact]
    public async Task StartupRepairsOverwrittenRegistrationIdFromValidatedRoute()
    {
        var fixture = await CreateRegistrationFixtureAsync();
        using var identityStore = fixture.IdentityStore;
        fixture.Configuration.AutoParty.RegistrationId = Guid.NewGuid().ToString("D");
        fixture.Configuration.AutoParty.RegistrationState = DadAutoPartyRegistrationState.Unregistered;

        Assert.True(DadAutoPartyConfigurationMigration.Migrate(
            fixture.Configuration,
            identityStore,
            fixture.WebhookStore));

        Assert.Equal(DadAutoPartyRegistrationState.Active, fixture.Configuration.AutoParty.RegistrationState);
        Assert.Equal(fixture.RegistrationId.ToString("D"), fixture.Configuration.AutoParty.RegistrationId);
        Assert.Equal(7, fixture.Configuration.AutoParty.DirectoryGeneration);
        Assert.Single(fixture.Configuration.AutoParty.Pairings);
        Assert.Empty(fixture.Configuration.AutoParty.PendingPairings);
    }

    [Fact]
    public async Task StartupLeavesMissingProtectedIdentityOrMailboxUnregistered()
    {
        var missingIdentity = await CreateRegistrationFixtureAsync();
        using (missingIdentity.IdentityStore)
        {
            _ = await missingIdentity.IdentityStore.DeleteAsync(
                missingIdentity.Configuration.AutoParty.EndpointIdentityReference);
            _ = DadAutoPartyConfigurationMigration.Migrate(
                missingIdentity.Configuration,
                missingIdentity.IdentityStore,
                missingIdentity.WebhookStore);
            Assert.Equal(DadAutoPartyRegistrationState.Unregistered, missingIdentity.Configuration.AutoParty.RegistrationState);
            Assert.Equal(
                DadAutoPartyRegistrationRecoveryState.IdentityLost,
                missingIdentity.Configuration.AutoParty.RegistrationRecoveryState);
        }

        var missingMailbox = await CreateRegistrationFixtureAsync();
        using (missingMailbox.IdentityStore)
        {
            _ = DadAutoPartyConfigurationMigration.Migrate(
                missingMailbox.Configuration,
                missingMailbox.IdentityStore,
                new MemoryWebhookStore());
        }
        Assert.Equal(DadAutoPartyRegistrationState.Unregistered, missingMailbox.Configuration.AutoParty.RegistrationState);
        Assert.Equal(
            DadAutoPartyRegistrationRecoveryState.RecoveryAvailable,
            missingMailbox.Configuration.AutoParty.RegistrationRecoveryState);
        Assert.Single(missingMailbox.Configuration.AutoParty.Pairings);
        Assert.Empty(missingMailbox.Configuration.AutoParty.PendingPairings);
    }

    [Fact]
    public async Task LegacyCamelCaseIdentityMaterialIsRejected()
    {
        var privateKey = RandomNumberGenerator.GetBytes(32);
        var camelCaseIdentity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ownerId = "owner-camel-case",
            islandId = "island-camel-case",
            keyGeneration = 1,
            signingPrivateKey = Convert.ToBase64String(privateKey),
            encryptionPrivateKey = Convert.ToBase64String(privateKey),
        });
        try
        {
            using var identityStore = new MemoryIdentityStore(camelCaseIdentity);
            var configuration = new DadAutoPartyConfiguration
            {
                EndpointIdentityReference = "identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                RegisteredOwnerId = "owner-camel-case",
                RegisteredIslandId = "island-camel-case",
            };
            var signing = new DadAutoPartySigningService(configuration, identityStore);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                signing.SignAsync(Encoding.UTF8.GetBytes("payload")).AsTask());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(camelCaseIdentity);
        }
    }

    [Fact]
    public async Task MigratedLegacyBotFieldsDisappearAfterSuccessfulCleanupSave()
    {
        var configuration = new Configuration
        {
            AutoParty = new DadAutoPartyConfiguration
            {
                LegacyDiscordTokenReference = "discord-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                LegacyDiscordTokenCleanupPending = true,
                LegacyDiscordTokenCleanupWarning = "dad-autoparty-legacy-token-cleanup-pending",
            },
        };
        var saves = 0;
        var connector = new DadDiscordCourierConnector(configuration.AutoParty, static () => true);
        using var service = new DadAutoPartyEndpointService(
            configuration.AutoParty,
            new MemoryWebhookStore(),
            new MemoryLegacyTokenStore(),
            connector,
            () => saves++);

        for (var attempt = 0;
             attempt < 500 && configuration.AutoParty.LegacyDiscordTokenCleanupPending;
             attempt++)
        {
            service.Update(dadEnabled: true);
            await Task.Delay(1);
        }

        Assert.False(configuration.AutoParty.LegacyDiscordTokenCleanupPending);
        Assert.Null(configuration.AutoParty.LegacyDiscordTokenReference);
        Assert.Equal(string.Empty, configuration.AutoParty.LegacyDiscordTokenCleanupWarning);
        Assert.True(saves >= 1);
        var saved = JsonSerializer.Serialize(configuration);
        Assert.DoesNotContain("DiscordTokenReference", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedLegacySecretCleanupLeavesVisibleRetryableWarning()
    {
        var configuration = new Configuration
        {
            AutoParty = new DadAutoPartyConfiguration
            {
                LegacyDiscordTokenReference = "discord-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                LegacyDiscordTokenCleanupPending = true,
                LegacyDiscordTokenCleanupWarning = "dad-autoparty-legacy-token-cleanup-pending",
            },
        };
        var connector = new DadDiscordCourierConnector(configuration.AutoParty, static () => true);
        using var service = new DadAutoPartyEndpointService(
            configuration.AutoParty,
            new MemoryWebhookStore(),
            new ThrowingLegacyTokenStore(),
            connector,
            static () => { });

        for (var attempt = 0;
             attempt < 500 &&
             configuration.AutoParty.LegacyDiscordTokenCleanupWarning !=
             "dad-autoparty-legacy-token-cleanup-retry";
             attempt++)
        {
            service.Update(dadEnabled: true);
            await Task.Delay(1);
        }

        Assert.True(configuration.AutoParty.LegacyDiscordTokenCleanupPending);
        Assert.Equal("discord-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            configuration.AutoParty.LegacyDiscordTokenReference);
        Assert.Equal("dad-autoparty-legacy-token-cleanup-retry",
            configuration.AutoParty.LegacyDiscordTokenCleanupWarning);
    }

    private static Configuration PopulatedSchemaNine(
        string identityReference = "identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string webhookReference = "webhook-mailbox-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
        => new()
        {
            Version = 9,
            PluginEnabled = true,
            ServerListenPort = 5544,
            ClientAccountId = "account-current",
            LastAccountId = "account-last",
            PlannerGroups = [new DadPlannerGroup()],
            Schedules = [new DadScheduleDefinition()],
            AutoPartyFleet = new DadAutoPartyFleetConfiguration
            {
                Enabled = true,
                Revision = 7,
                Rows =
                [
                    new DadAutoPartyFleetRow
                    {
                        RowId = "fleet-row-one",
                        AccountKey = "account-current",
                        CharacterKey = "character-current",
                        JobId = 19,
                    },
                ],
            },
            AutoParty = new DadAutoPartyConfiguration
            {
                Enabled = true,
                RegistrationState = DadAutoPartyRegistrationState.Active,
                RegistrationId = "11111111-1111-4111-8111-111111111111",
                RouteId = "route-old",
                CentralBotApplicationId = "123456789",
                HomeGuildScope = "guild-old",
                WebhookCredentialReference = webhookReference,
                UplinkEpochId = "22222222-2222-4222-8222-222222222222",
                DownlinkEpochId = "33333333-3333-4333-8333-333333333333",
                MailboxEpochGeneration = 4,
                RelayKeyGeneration = 5,
                RelaySigningPublicKey = Convert.ToBase64String(new byte[32]),
                RelayAgreementPublicKey = Convert.ToBase64String(new byte[32]),
                BootstrapExpiresAtUtc = DateTime.UtcNow.AddHours(1),
                LegacyDiscordTokenReference = "discord-token-cccccccccccccccccccccccccccccccc",
                LegacyDiscordTokenCleanupPending = true,
                LegacyDiscordTokenCleanupWarning = "dad-autoparty-legacy-token-cleanup-pending",
                EndpointIdentityReference = identityReference,
                RegisteredOwnerId = "owner-old",
                RegisteredIslandId = "island-old",
                RegistrationFingerprint = new string('A', 64),
                EndpointAlias = "endpoint-old",
                SigningPublicKey = Convert.ToBase64String(new byte[32]),
                EncryptionPublicKey = Convert.ToBase64String(new byte[32]),
                EndpointKeyGeneration = 6,
                RevocationGeneration = 7,
                StateGeneration = 8,
                StandingSharePolicy = new DadAutoPartySharePolicy
                {
                    Enabled = true,
                    Mode = DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild,
                },
                Pairings = [new DadAutoPartyPairing()],
                PendingPairings = [new DadAutoPartyPairing()],
                Grants = [new DadAutoPartyGrant()],
                Listings = [new DadAutoPartyListing()],
                RemoteBindings = [new DadAutoPartyRemoteBinding()],
                Deauthentications = [new DadAutoPartyDeauthentication()],
            },
        };

    private static async Task<RegistrationFixture> CreateRegistrationFixtureAsync()
    {
        var identityStore = new MemoryIdentityStore();
        var autoParty = new DadAutoPartyConfiguration { Enabled = true };
        var identityService = new DadAutoPartyIdentityPackageService(autoParty, identityStore, static () => { });
        var challenge = await identityService.GenerateChallengeAsync("reload-endpoint");
        var registrationId = RegistrationCopyPasteCodec.DecodeChallenge(challenge.OutputPath).Contract.RegistrationId;
        var now = new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.Zero);
        var island = new IslandId(autoParty.RegisteredIslandId);
        var relaySigning = Enumerable.Repeat((byte)0x31, AutoPartyProtocol.Ed25519PublicKeyBytes).ToArray();
        var relayAgreement = Enumerable.Repeat((byte)0x41, AutoPartyProtocol.X25519KeyBytes).ToArray();
        var relayKeys = new EndpointPublicKeys(
            1,
            "relay-signing",
            ImmutableArray.CreateRange(relaySigning),
            "relay-agreement",
            ImmutableArray.CreateRange(relayAgreement));
        var uplink = new CourierEpochDescriptor(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            island,
            CourierDirection.Uplink,
            now,
            now.AddMinutes(30),
            now.AddMinutes(35),
            1,
            [new CourierPageReference(1, "500000000000000001")],
            4);
        var downlink = new CourierEpochDescriptor(
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            island,
            CourierDirection.Downlink,
            now,
            now.AddMinutes(30),
            now.AddMinutes(35),
            1,
            [new CourierPageReference(1, "500000000000000002")],
            4);
        var credential = new DadAutoPartyWebhookCredential(
            "300000000000000001",
            new string('a', 64),
            "400000000000000001")
        {
            UplinkEpoch = uplink,
            DownlinkEpoch = downlink,
            RelayPublicKeys = relayKeys,
        };

        autoParty.RegistrationState = DadAutoPartyRegistrationState.Active;
        autoParty.RegistrationId = registrationId.ToString("D");
        autoParty.RouteId = $"route-{registrationId:N}";
        autoParty.CentralBotApplicationId = "600000000000000001";
        autoParty.HomeGuildScope = "200000000000000001";
        autoParty.WebhookCredentialReference = "webhook-mailbox-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        autoParty.UplinkEpochId = uplink.EpochId.ToString("D");
        autoParty.DownlinkEpochId = downlink.EpochId.ToString("D");
        autoParty.MailboxEpochGeneration = uplink.EpochGeneration;
        autoParty.DirectoryGeneration = 7;
        autoParty.RelayKeyGeneration = relayKeys.KeyVersion;
        autoParty.RelaySigningPublicKey = Convert.ToBase64String(relaySigning);
        autoParty.RelayAgreementPublicKey = Convert.ToBase64String(relayAgreement);
        autoParty.StandingSharePolicy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.CharacterList,
            CharacterHandles = ["character-one"],
            Enabled = true,
            Revision = 3,
            UpdatedAtUtc = now.UtcDateTime,
        };
        autoParty.Pairings = [Pairing("peer-active", now.UtcDateTime, active: true)];
        autoParty.PendingPairings = [Pairing("peer-pending", now.UtcDateTime, active: false)];

        return new(
            new Configuration
            {
                Version = DadAutoPartyConfigurationMigration.CurrentVersion,
                AutoParty = autoParty,
            },
            identityStore,
            new MemoryWebhookStore(credential),
            registrationId);
    }

    private static DadAutoPartyPairing Pairing(string islandId, DateTime now, bool active) => new()
    {
        PairingId = Guid.NewGuid().ToString("D"),
        OwnerId = "owner-peer",
        IslandId = islandId,
        HomeGuildScope = "200000000000000001",
        PublicKeyFingerprint = new string('1', 64),
        LocalFingerprint = new string('2', 64),
        TranscriptHash = new string('3', 64),
        LocalSharePolicy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.CharacterList,
            CharacterHandles = ["character-one"],
            Enabled = true,
            UpdatedAtUtc = now,
        },
        PeerSharePolicy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.CharacterList,
            CharacterHandles = ["character-two"],
            Enabled = true,
            UpdatedAtUtc = now,
        },
        ExpiresAtUtc = now.AddHours(1),
        KeyGeneration = 1,
        SigningPublicKey = Convert.ToBase64String(new byte[32]),
        AgreementPublicKey = Convert.ToBase64String(new byte[32]),
        ConfirmedAtUtc = active ? now : default,
    };

    private sealed record RegistrationFixture(
        Configuration Configuration,
        MemoryIdentityStore IdentityStore,
        MemoryWebhookStore WebhookStore,
        Guid RegistrationId);

    private static void AssertInert(DadAutoPartyConfiguration autoParty)
    {
        Assert.False(autoParty.Enabled);
        Assert.Equal(DadAutoPartyRegistrationState.Unregistered, autoParty.RegistrationState);
        Assert.Equal(string.Empty, autoParty.RegistrationId);
        Assert.Equal(string.Empty, autoParty.RouteId);
        Assert.Equal(string.Empty, autoParty.CentralBotApplicationId);
        Assert.Equal(string.Empty, autoParty.HomeGuildScope);
        Assert.Equal(string.Empty, autoParty.WebhookCredentialReference);
        Assert.Equal(string.Empty, autoParty.UplinkEpochId);
        Assert.Equal(string.Empty, autoParty.DownlinkEpochId);
        Assert.Equal(0, autoParty.MailboxEpochGeneration);
        Assert.Equal(1, autoParty.DirectoryGeneration);
        Assert.Equal(1, autoParty.RelayKeyGeneration);
        Assert.Equal(string.Empty, autoParty.RelaySigningPublicKey);
        Assert.Equal(string.Empty, autoParty.RelayAgreementPublicKey);
        Assert.Equal(default, autoParty.BootstrapExpiresAtUtc);
        Assert.Null(autoParty.LegacyDiscordTokenReference);
        Assert.False(autoParty.LegacyDiscordTokenCleanupPending);
        Assert.Equal(string.Empty, autoParty.LegacyDiscordTokenCleanupWarning);
        Assert.Equal(string.Empty, autoParty.EndpointIdentityReference);
        Assert.Equal(string.Empty, autoParty.RegisteredOwnerId);
        Assert.Equal(string.Empty, autoParty.RegisteredIslandId);
        Assert.Equal(string.Empty, autoParty.RegistrationFingerprint);
        Assert.Equal(string.Empty, autoParty.EndpointAlias);
        Assert.Equal(string.Empty, autoParty.SigningPublicKey);
        Assert.Equal(string.Empty, autoParty.EncryptionPublicKey);
        Assert.Equal(1, autoParty.EndpointKeyGeneration);
        Assert.Equal(1, autoParty.RevocationGeneration);
        Assert.Equal(1, autoParty.StateGeneration);
        Assert.False(autoParty.StandingSharePolicy.Enabled);
        Assert.Equal(
            DadAutoPartyCharacterShareMode.CharacterList,
            autoParty.StandingSharePolicy.Mode);
        Assert.True(autoParty.StandingSharePolicy.IsValid);
        Assert.Empty(autoParty.PairedDadAliases);
        Assert.Empty(autoParty.Pairings);
        Assert.Empty(autoParty.PendingPairings);
        Assert.Empty(autoParty.Grants);
        Assert.Empty(autoParty.Listings);
        Assert.Empty(autoParty.RemoteBindings);
        Assert.Empty(autoParty.Deauthentications);
    }

    private sealed class RecordingIdentityStore : IDadAutoPartyEndpointIdentityStore
    {
        public List<string> DeletedReferences { get; } = [];

        public ValueTask<string> StoreAsync(
            ReadOnlyMemory<byte> identityMaterial,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<byte[]> LoadAsync(
            string identityReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(
            string identityReference,
            CancellationToken cancellationToken = default)
        {
            DeletedReferences.Add(identityReference);
            return ValueTask.FromResult(false);
        }
    }

    private sealed class RecordingWebhookStore : IDadAutoPartyWebhookCredentialStore
    {
        public List<string> DeletedReferences { get; } = [];

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
            CancellationToken cancellationToken = default)
        {
            DeletedReferences.Add(credentialReference);
            return ValueTask.FromResult(false);
        }
    }

    private sealed class MemoryIdentityStore : IDadAutoPartyEndpointIdentityStore, IDisposable
    {
        private byte[]? identityMaterial;

        public MemoryIdentityStore()
        {
        }

        public MemoryIdentityStore(ReadOnlySpan<byte> identityMaterial)
        {
            this.identityMaterial = identityMaterial.ToArray();
        }

        public byte[] Snapshot() => identityMaterial?.ToArray() ?? [];

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

    private sealed class MemoryWebhookStore : IDadAutoPartyWebhookCredentialStore
    {
        private readonly DadAutoPartyWebhookCredential? credential;

        public MemoryWebhookStore(DadAutoPartyWebhookCredential? credential = null)
        {
            this.credential = credential;
        }

        public ValueTask<string> StoreAsync(
            DadAutoPartyWebhookCredential credential,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult("webhook-mailbox-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        public ValueTask<DadAutoPartyWebhookCredential> LoadAsync(
            string credentialReference,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(credential ?? new DadAutoPartyWebhookCredential(
                "123456789",
                new string('a', 64),
                "987654321"));

        public ValueTask ReplaceAsync(
            string credentialReference,
            DadAutoPartyWebhookCredential credential,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(
            string credentialReference,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
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

    private sealed class ThrowingLegacyTokenStore : IDadAutoPartyDiscordTokenStore
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
            throw new IOException("simulated");
    }
}
