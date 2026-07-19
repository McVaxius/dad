using System.Text.Json;
using Xunit;

namespace dad.Tests;

public sealed class DadAutoPartyConfigurationMigrationTests
{
    [Fact]
    public void NewConfigurationUsesSchemaFiveWithAutoPartyDisabled()
    {
        var configuration = new Configuration();

        Assert.Equal(5, configuration.Version);
        Assert.False(configuration.AutoParty.Enabled);
        Assert.False(configuration.AutoParty.PairingEnabled);
        Assert.False(configuration.AutoParty.ExecutionEnabled);
        Assert.Equal(string.Empty, configuration.AutoParty.EndpointIdentityReference);
        Assert.Equal(string.Empty, configuration.AutoParty.RegisteredOwnerId);
        Assert.Equal(string.Empty, configuration.AutoParty.RegisteredIslandId);
        Assert.Equal(@"Z:\autopartypilot", configuration.AutoParty.PilotExchangeRoot);
        Assert.Equal(@"Z:\autopartypilot\pilot-input", configuration.AutoParty.GetPilotInputRoot());
        Assert.Equal(@"Z:\autopartypilot\pilot-receipts", configuration.AutoParty.GetPilotReceiptRoot());
        Assert.Equal(@"Z:\autopartypilot\pilot-input\pilot-fixture.json", configuration.AutoParty.GetPilotFixturePath());
        Assert.Equal(@"Z:\autopartypilot\pilot-courier", configuration.AutoParty.GetPilotCourierRoot());
        Assert.Equal(@"Z:\autopartypilot\plugin", configuration.AutoParty.GetPilotPluginRoot());
        Assert.Empty(configuration.AutoParty.Pairings);
        Assert.Empty(configuration.AutoParty.Grants);
        Assert.Empty(configuration.AutoParty.Listings);
    }

    [Fact]
    public void SchemaFourMigrationDiscardsPrematureAutoPartyAuthority()
    {
        const string json = """
            {
              "Version": 4,
              "PluginEnabled": true,
              "AutoParty": {
                "Enabled": true,
                "PairingEnabled": true,
                "ExecutionEnabled": true,
                "EndpointIdentityReference": "identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "RegisteredOwnerId": "owner-before-schema-five",
                "RegisteredIslandId": "island-before-schema-five",
                "Pairings": [
                  {
                    "OwnerId": "owner-before-schema-five",
                    "IslandId": "island-before-schema-five",
                    "PublicKeyFingerprint": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                    "KeyGeneration": 1,
                    "ConfirmedAtUtc": "2026-07-18T00:00:00Z"
                  }
                ]
              }
            }
            """;
        var configuration = JsonSerializer.Deserialize<Configuration>(json)!;

        Assert.True(configuration.MigrateTransportSettings());
        Assert.Equal(5, configuration.Version);
        Assert.True(configuration.PluginEnabled);
        Assert.False(configuration.AutoParty.Enabled);
        Assert.False(configuration.AutoParty.PairingEnabled);
        Assert.False(configuration.AutoParty.ExecutionEnabled);
        Assert.Equal(string.Empty, configuration.AutoParty.EndpointIdentityReference);
        Assert.Equal(string.Empty, configuration.AutoParty.RegisteredOwnerId);
        Assert.Equal(string.Empty, configuration.AutoParty.RegisteredIslandId);
        Assert.Empty(configuration.AutoParty.Pairings);
        Assert.False(configuration.MigrateTransportSettings());
    }

    [Fact]
    public void SchemaFivePreservesExplicitDisabledRegistrationMetadata()
    {
        const string json = """
            {
              "Version": 5,
              "AutoParty": {
                "Enabled": false,
                "PairingEnabled": false,
                "ExecutionEnabled": false,
                "CourierRootPath": "D:\\AutoParty-LiveGate\\pilot-courier",
                "EndpointIdentityReference": "identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "RegisteredOwnerId": "owner-public",
                "RegisteredIslandId": "island-public",
                "RegistrationFingerprint": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
              }
            }
            """;
        var configuration = JsonSerializer.Deserialize<Configuration>(json)!;

        Assert.True(configuration.MigrateTransportSettings());
        Assert.False(configuration.AutoParty.Enabled);
        Assert.False(configuration.AutoParty.PairingEnabled);
        Assert.False(configuration.AutoParty.ExecutionEnabled);
        Assert.Equal("identity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", configuration.AutoParty.EndpointIdentityReference);
        Assert.Equal("owner-public", configuration.AutoParty.RegisteredOwnerId);
        Assert.Equal("island-public", configuration.AutoParty.RegisteredIslandId);
        Assert.Equal(@"Z:\autopartypilot", configuration.AutoParty.PilotExchangeRoot);
        Assert.Equal(@"Z:\autopartypilot\pilot-courier", configuration.AutoParty.CourierRootPath);
        Assert.False(configuration.MigrateTransportSettings());
    }

    [Fact]
    public void SchemaFivePreservesNormalizedCustomPilotExchangeRoot()
    {
        const string json = """
            {
              "Version": 5,
              "AutoParty": {
                "PilotExchangeRoot": "C:\\shared\\pilot-root\\",
                "CourierRootPath": "C:\\stale-courier"
              }
            }
            """;
        var configuration = JsonSerializer.Deserialize<Configuration>(json)!;

        Assert.True(configuration.MigrateTransportSettings());
        Assert.Equal(@"C:\shared\pilot-root", configuration.AutoParty.PilotExchangeRoot);
        Assert.Equal(@"C:\shared\pilot-root\pilot-courier", configuration.AutoParty.CourierRootPath);
        Assert.False(configuration.MigrateTransportSettings());
    }
}
