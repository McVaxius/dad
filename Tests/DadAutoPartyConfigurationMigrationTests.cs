using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoParty.Contracts;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAutoPartyConfigurationMigrationTests
{
    [Fact]
    public void NewConfigurationUsesSchemaTenWithInertAutoPartyDefaults()
    {
        var configuration = new Configuration();

        Assert.Equal(10, configuration.Version);
        AssertInert(configuration.AutoParty);
        Assert.Null(typeof(DadAutoPartyConfiguration).GetProperty("PairingEnabled"));
        Assert.Null(typeof(DadAutoPartyConfiguration).GetProperty("ExecutionEnabled"));
        Assert.Null(typeof(DadAutoPartyConfiguration).GetProperty("DiscordGuildId"));
        Assert.Null(typeof(DadAutoPartyConfiguration).GetProperty("PilotExchangeRoot"));
        Assert.Null(typeof(DadAutoPartyConfiguration).GetProperty("MeasuredPilot"));
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

        Assert.Equal(10, configuration.Version);
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

            Assert.Equal(10, configuration.Version);
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

            Assert.Equal(10, missing.Version);
            Assert.Equal(10, malformed.Version);
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
                    "AutoParty schema-10 protected-state reset could not complete.",
                    exception.Message);
                Assert.Equal(9, configuration.Version);
                Assert.Same(priorAutoParty, configuration.AutoParty);
            }

            Assert.True(DadAutoPartyConfigurationMigration.Migrate(
                configuration,
                identityStore,
                webhookStore));
            Assert.Equal(10, configuration.Version);
            Assert.False(File.Exists(webhookPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SchemaTenMigrationIsIdempotent()
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
            DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild,
            autoParty.StandingSharePolicy.Mode);
        Assert.True(autoParty.StandingSharePolicy.IsValid);
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
        public ValueTask<string> StoreAsync(
            DadAutoPartyWebhookCredential credential,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult("webhook-mailbox-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        public ValueTask<DadAutoPartyWebhookCredential> LoadAsync(
            string credentialReference,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DadAutoPartyWebhookCredential(
                "123456789",
                new string('a', 64),
                "987654321"));

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
