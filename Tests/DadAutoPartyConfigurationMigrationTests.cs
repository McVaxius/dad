using System.Text.Json;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAutoPartyConfigurationMigrationTests
{
    [Fact]
    public void NewConfigurationUsesSchemaNineWithoutPerDadBotOrPromotionGates()
    {
        var configuration = new Configuration();

        Assert.Equal(9, configuration.Version);
        Assert.False(configuration.AutoParty.Enabled);
        Assert.Equal(DadAutoPartyRegistrationState.Unregistered, configuration.AutoParty.RegistrationState);
        Assert.Null(configuration.AutoParty.LegacyDiscordTokenReference);
        Assert.False(configuration.AutoParty.LegacyDiscordTokenCleanupPending);
        Assert.Empty(configuration.AutoParty.Pairings);
        Assert.Empty(configuration.AutoParty.Listings);
        Assert.False(configuration.AutoParty.StandingSharePolicy.Enabled);
        Assert.Equal(
            DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild,
            configuration.AutoParty.StandingSharePolicy.Mode);
        Assert.True(configuration.AutoParty.StandingSharePolicy.IsValid);
        Assert.Equal(1, configuration.AutoParty.EndpointKeyGeneration);
        Assert.Null(typeof(DadAutoPartyConfiguration).GetProperty("PairingEnabled"));
        Assert.Null(typeof(DadAutoPartyConfiguration).GetProperty("ExecutionEnabled"));
        Assert.Null(typeof(DadAutoPartyConfiguration).GetProperty("DiscordGuildId"));
        Assert.Null(typeof(DadAutoPartyConfiguration).GetProperty("PilotExchangeRoot"));
        Assert.Null(typeof(DadAutoPartyConfiguration).GetProperty("MeasuredPilot"));
    }

    [Fact]
    public void SchemaEightPreservesEndpointKeysAndUnrelatedStateButClearsP1218Trust()
    {
        const string json = """
            {
              "Version": 8,
              "PluginEnabled": true,
              "ServerListenPort": 5544,
              "Schedules": [{ "ScheduleId": "schedule-one", "DisplayName": "Keep me" }],
              "AutoPartyFleet": { "Rows": [] },
              "AutoParty": {
                "Enabled": true,
                "DiscordTokenReference": "discord-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "DiscordApplicationId": 123,
                "DiscordBotUserId": 456,
                "EndpointIdentityReference": "identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "RegisteredOwnerId": "owner-public",
                "RegisteredIslandId": "island-public",
                "RegistrationFingerprint": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                "EndpointAlias": "dad-one",
                "SigningPublicKey": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                "EncryptionPublicKey": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                "EndpointKeyGeneration": 4,
                "PilotArtifactSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "Pairings": [{
                  "OwnerId": "owner-peer",
                  "IslandId": "island-peer",
                  "PublicKeyFingerprint": "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
                  "ConfirmedAtUtc": "2026-08-01T00:00:00Z"
                }],
                "Listings": [{
                  "ListingId": "d75fdbd4-32d8-4f15-802f-1e746ec2c19c",
                  "OpaqueCharacterId": "opaque-old",
                  "AllowedJobIds": ["19"],
                  "AllowedActivityIds": ["duty-old"],
                  "ExpiresAtUtc": "2026-08-10T00:00:00Z"
                }],
                "RemoteBindings": [{
                  "FleetRowId": "row-old",
                  "OpaqueCharacterId": "opaque-old",
                  "OwnerId": "owner-peer",
                  "IslandId": "island-peer",
                  "RequestedJobId": "19",
                  "OwnerConsentConfirmed": true
                }]
              }
            }
            """;
        var configuration = JsonSerializer.Deserialize<Configuration>(json)!;

        Assert.True(configuration.MigrateTransportSettings());

        Assert.Equal(9, configuration.Version);
        Assert.True(configuration.PluginEnabled);
        Assert.Equal(5544, configuration.ServerListenPort);
        Assert.Single(configuration.Schedules);
        Assert.Equal("identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            configuration.AutoParty.EndpointIdentityReference);
        Assert.Equal("owner-public", configuration.AutoParty.RegisteredOwnerId);
        Assert.Equal("island-public", configuration.AutoParty.RegisteredIslandId);
        Assert.Equal(4, configuration.AutoParty.EndpointKeyGeneration);
        Assert.False(configuration.AutoParty.Enabled);
        Assert.Equal(DadAutoPartyRegistrationState.Unregistered, configuration.AutoParty.RegistrationState);
        Assert.Empty(configuration.AutoParty.Pairings);
        Assert.Empty(configuration.AutoParty.Listings);
        Assert.Empty(configuration.AutoParty.RemoteBindings);
        Assert.False(configuration.AutoParty.StandingSharePolicy.Enabled);
        Assert.Equal(
            DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild,
            configuration.AutoParty.StandingSharePolicy.Mode);
        Assert.True(configuration.AutoParty.StandingSharePolicy.IsValid);
        Assert.True(configuration.AutoParty.LegacyDiscordTokenCleanupPending);
        Assert.Equal("discord-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            configuration.AutoParty.LegacyDiscordTokenReference);
        Assert.Equal("dad-autoparty-legacy-token-cleanup-pending",
            configuration.AutoParty.LegacyDiscordTokenCleanupWarning);
        Assert.False(configuration.MigrateTransportSettings());
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
