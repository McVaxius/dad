using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAutoPartyListingPublicationRulesTests
{
    [Fact]
    public void BuildsPrivacySafeLocalListingsForFormationAndSavedPlans()
    {
        var now = new DateTime(2026, 8, 11, 21, 0, 0, DateTimeKind.Utc);
        var autoParty = new DadAutoPartyConfiguration
        {
            RegisteredOwnerId = "owner-local",
            RegisteredIslandId = "island-local",
            StateGeneration = 7,
        };
        var fleet = new DadAutoPartyFleetConfiguration
        {
            Revision = 9,
            Rows =
            [
                new DadAutoPartyFleetRow
                {
                    RowId = "row-local",
                    OpaqueCharacterId = "opaque-local",
                    AccountKey = "private-account",
                    CharacterKey = "private-character-key",
                    JobId = 19,
                    Enabled = true,
                },
                new DadAutoPartyFleetRow
                {
                    RowId = "row-remote",
                    OpaqueCharacterId = "opaque-remote",
                    JobId = 24,
                    IsRemote = true,
                    Enabled = true,
                },
            ],
        };
        var plans = new[]
        {
            new DadPlannerGroup
            {
                ActivityMode = DadPlannerActivityMode.PremadeDuty,
                DutyContentFinderConditionId = 123,
                DutyExpectedPartySize = 4,
            },
        };

        var publication = DadAutoPartyListingPublicationRules.Build(autoParty, fleet, plans, now);

        Assert.False(publication.StandingPolicy.Enabled);
        var listing = Assert.Single(publication.Listings);
        Assert.Equal("owner-local", listing.OwnerId);
        Assert.Equal("island-local", listing.SharingIslandId);
        Assert.Equal("opaque-local", listing.OpaqueCharacterId);
        Assert.DoesNotContain("opaque-local", listing.DisplayLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-account", listing.DisplayLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-character-key", listing.DisplayLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["19"], listing.AllowedJobIds);
        Assert.Contains(DadAutoPartyFreeformRules.FormationActivityId, listing.AllowedActivityIds);
        Assert.Contains("dad-premadeduty-123", listing.AllowedActivityIds);
        Assert.Equal(9, listing.Revision);
        Assert.Equal(now.AddMinutes(15), listing.ExpiresAtUtc);
    }

    [Fact]
    public void PromiscuousStandingPolicyPublishesWithoutAPairing()
    {
        var now = new DateTime(2026, 8, 11, 21, 0, 0, DateTimeKind.Utc);
        var policy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild,
            Enabled = true,
            Revision = 4,
            UpdatedAtUtc = now.AddMinutes(-1),
        };
        var configuration = new DadAutoPartyConfiguration
        {
            StateGeneration = 5,
            StandingSharePolicy = policy,
        };

        var publication = DadAutoPartyListingPublicationRules.Build(
            configuration,
            new DadAutoPartyFleetConfiguration(),
            [],
            now);

        Assert.Empty(configuration.Pairings);
        Assert.True(publication.StandingPolicy.Enabled);
        Assert.Equal(DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild, publication.StandingPolicy.Mode);
    }

    [Fact]
    public void PairPolicyCannotEnableOrOverwriteStandingPublicationPolicy()
    {
        var now = new DateTime(2026, 8, 11, 21, 0, 0, DateTimeKind.Utc);
        var pairPolicy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild,
            Enabled = true,
            Revision = 99,
            UpdatedAtUtc = now,
        };
        var configuration = new DadAutoPartyConfiguration
        {
            StateGeneration = 5,
            StandingSharePolicy = new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild,
                Enabled = false,
                Revision = 3,
                UpdatedAtUtc = now,
            },
            Pairings = [ValidPairing(pairPolicy)],
        };

        var publication = DadAutoPartyListingPublicationRules.Build(
            configuration,
            new DadAutoPartyFleetConfiguration(),
            [],
            now);

        Assert.False(publication.StandingPolicy.Enabled);
        Assert.True(configuration.Pairings[0].LocalSharePolicy.Enabled);
        Assert.Equal(99, configuration.Pairings[0].LocalSharePolicy.Revision);
    }

    [Fact]
    public void InvalidStandingPolicyFailsClosedAndNormalizationDoesNotTouchPairPolicies()
    {
        var pairPolicy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.AllCharactersForPeer,
            Enabled = true,
            Revision = 8,
        };
        var configuration = new DadAutoPartyConfiguration
        {
            StandingSharePolicy = new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.AllCharactersForPeer,
                Enabled = true,
            },
            Pairings = [ValidPairing(pairPolicy)],
        };

        var publication = DadAutoPartyListingPublicationRules.Build(
            configuration,
            new DadAutoPartyFleetConfiguration(),
            [],
            DateTime.UtcNow);

        Assert.False(publication.StandingPolicy.Enabled);
        Assert.Equal(DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild, publication.StandingPolicy.Mode);

        configuration.Normalize();

        Assert.False(configuration.StandingSharePolicy.Enabled);
        Assert.Equal(DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild, configuration.StandingSharePolicy.Mode);
        Assert.True(configuration.StandingSharePolicy.IsValid);
        Assert.Same(pairPolicy, configuration.Pairings[0].LocalSharePolicy);
        Assert.True(pairPolicy.Enabled);
        Assert.Equal(DadAutoPartyCharacterShareMode.AllCharactersForPeer, pairPolicy.Mode);
    }

    [Fact]
    public void StandingPolicyControlIsRenderedBeforePendingPairingBranch()
    {
        var source = ReadRepositorySource("Windows", "DadAutoPartyWindow.cs");
        var control = source.IndexOf(
            "Share all local characters with attested same-guild requesters",
            StringComparison.Ordinal);
        var pendingBranch = source.IndexOf(
            "var pending = configuration.PendingPairings",
            StringComparison.Ordinal);

        Assert.True(control >= 0);
        Assert.True(pendingBranch > control);
        Assert.Contains("SetStandingSharePolicy", source[control..pendingBranch], StringComparison.Ordinal);
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dad.csproj")))
            directory = directory.Parent;
        var repositoryRoot = directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate the Dad repository root from the test output directory.");
        return File.ReadAllText(Path.Combine([repositoryRoot, .. pathParts]));
    }

    private static DadAutoPartyPairing ValidPairing(DadAutoPartySharePolicy localPolicy) => new()
    {
        PairingId = Guid.NewGuid().ToString("D"),
        OwnerId = "owner-peer",
        IslandId = "island-peer",
        PublicKeyFingerprint = new string('A', 64),
        LocalFingerprint = new string('B', 64),
        TranscriptHash = new string('C', 64),
        ConfirmationCodeHash = new string('D', 64),
        SigningPublicKey = Convert.ToBase64String(new byte[32]),
        AgreementPublicKey = Convert.ToBase64String(new byte[32]),
        KeyGeneration = 1,
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        LocalSharePolicy = localPolicy,
    };

}
