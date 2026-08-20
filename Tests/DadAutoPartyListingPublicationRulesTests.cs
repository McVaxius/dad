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
            Pairings = [ValidPairing(new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.AllCharactersForPeer,
                Enabled = true,
            })],
        };
        var crew = new[]
        {
            new DadAutoPartyCrewCandidate(
                new DadAutoPartyCrewIdentity
                {
                    RosterIdentityKey = "crew-local",
                    OpaqueCharacterId = "opaque-local",
                },
                new DadAcquiredCharacter
                {
                    AccountId = "private-account",
                    CharacterKey = "private-character-key",
                },
                [19],
                Available: true,
                InboundRoute: Route("opaque-local")),
            Candidate("opaque-unavailable", available: false),
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

        var publication = DadAutoPartyListingPublicationRules.Build(autoParty, crew, plans, now);

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
        Assert.Equal(7, listing.Revision);
        Assert.Equal(now.AddMinutes(15), listing.ExpiresAtUtc);
        Assert.DoesNotContain(publication.Listings, item => item.OpaqueCharacterId == "opaque-unavailable");
    }

    [Fact]
    public void CommunityStandingPolicyIntersectsAvailablePublishedListingsWithoutMutatingSelection()
    {
        var now = new DateTime(2026, 8, 11, 21, 0, 0, DateTimeKind.Utc);
        var policy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.CharacterList,
            CharacterHandles = ["opaque-community", "opaque-unavailable", "opaque-missing"],
            Enabled = true,
            Revision = 4,
            UpdatedAtUtc = now.AddMinutes(-1),
        };
        var configuration = new DadAutoPartyConfiguration
        {
            StateGeneration = 5,
            StandingSharePolicy = policy,
            Pairings = [ValidPairing(new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.CharacterList,
                CharacterHandles = ["opaque-private"],
                Enabled = true,
            })],
        };

        var publication = DadAutoPartyListingPublicationRules.Build(
            configuration,
            [
                Candidate("opaque-community", available: true),
                Candidate("opaque-private", available: true),
                Candidate("opaque-unavailable", available: false),
            ],
            [],
            now);

        Assert.True(publication.StandingPolicy.Enabled);
        Assert.Equal(DadAutoPartyCharacterShareMode.CharacterList, publication.StandingPolicy.Mode);
        Assert.Equal(["opaque-community"], publication.StandingPolicy.CharacterHandles);
        Assert.Equal(
            ["opaque-community", "opaque-private"],
            publication.Listings.Select(static listing => listing.OpaqueCharacterId));
        Assert.Same(policy, configuration.StandingSharePolicy);
        Assert.Equal(
            ["opaque-community", "opaque-unavailable", "opaque-missing"],
            policy.CharacterHandles);
    }

    [Fact]
    public void DisabledCommunityPolicyStillPublishesPrivateListingsWithoutTouchingPairPolicy()
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
                Mode = DadAutoPartyCharacterShareMode.CharacterList,
                CharacterHandles = ["opaque-private"],
                Enabled = false,
                Revision = 3,
                UpdatedAtUtc = now,
            },
            Pairings = [ValidPairing(pairPolicy)],
        };

        var publication = DadAutoPartyListingPublicationRules.Build(
            configuration,
            [Candidate("opaque-private", available: true)],
            [],
            now);

        Assert.False(publication.StandingPolicy.Enabled);
        Assert.Empty(publication.StandingPolicy.CharacterHandles);
        Assert.Equal("opaque-private", Assert.Single(publication.Listings).OpaqueCharacterId);
        Assert.Equal(["opaque-private"], configuration.StandingSharePolicy.CharacterHandles);
        Assert.True(configuration.Pairings[0].LocalSharePolicy.Enabled);
        Assert.Equal(99, configuration.Pairings[0].LocalSharePolicy.Revision);
    }

    [Fact]
    public void CommunityPolicyDisablesWireCloneWhenNoSelectedListingIsAvailable()
    {
        var now = new DateTime(2026, 8, 11, 21, 0, 0, DateTimeKind.Utc);
        var policy = new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.CharacterList,
            CharacterHandles = ["opaque-unavailable"],
            Enabled = true,
            Revision = 6,
            UpdatedAtUtc = now,
        };
        var configuration = new DadAutoPartyConfiguration { StandingSharePolicy = policy };

        var publication = DadAutoPartyListingPublicationRules.Build(
            configuration,
            [Candidate("opaque-unavailable", available: false)],
            [],
            now);

        Assert.False(publication.StandingPolicy.Enabled);
        Assert.Empty(publication.StandingPolicy.CharacterHandles);
        Assert.Empty(publication.Listings);
        Assert.True(policy.Enabled);
        Assert.Equal(["opaque-unavailable"], policy.CharacterHandles);
    }

    [Fact]
    public void PublicationRetainsTheExistingTwoHundredFiftySixCharacterLimit()
    {
        var configuration = new DadAutoPartyConfiguration
        {
            Pairings = [ValidPairing(new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.AllCharactersForPeer,
                Enabled = true,
            })],
        };

        var publication = DadAutoPartyListingPublicationRules.Build(
            configuration,
            Enumerable.Range(0, 300).Select(index => Candidate($"opaque-{index:D3}", available: true)),
            [],
            DateTime.UtcNow);

        Assert.Equal(256, publication.Listings.Count);
        Assert.Equal(256, publication.InboundRoutes.Count);
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
            [],
            [],
            DateTime.UtcNow);

        Assert.False(publication.StandingPolicy.Enabled);
        Assert.Equal(DadAutoPartyCharacterShareMode.CharacterList, publication.StandingPolicy.Mode);

        configuration.Normalize();

        Assert.False(configuration.StandingSharePolicy.Enabled);
        Assert.Equal(DadAutoPartyCharacterShareMode.CharacterList, configuration.StandingSharePolicy.Mode);
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
            "Save Community Available characters",
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
        SigningPublicKey = Convert.ToBase64String(new byte[32]),
        AgreementPublicKey = Convert.ToBase64String(new byte[32]),
        KeyGeneration = 1,
        ConfirmedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        LocalSharePolicy = localPolicy,
    };

    private static DadAutoPartyCrewCandidate Candidate(string opaqueCharacterId, bool available)
        => new(
            new DadAutoPartyCrewIdentity
            {
                RosterIdentityKey = $"roster-{opaqueCharacterId}",
                OpaqueCharacterId = opaqueCharacterId,
            },
            new DadAcquiredCharacter(),
            [19],
            available,
            available ? Route(opaqueCharacterId) : null);

    private static DadAutoPartyInboundRoute Route(string opaqueCharacterId)
    {
        var owner = new DadParticipantSnapshot
        {
            ClientInstanceId = "client-local",
            WorkerSessionId = new DadWorkerSessionId("worker-local"),
            IsLocalClient = true,
            ManagedAccountKey = new DadAccountKey("account-local"),
        };
        return new DadAutoPartyInboundRoute(
            opaqueCharacterId,
            owner.ManagedAccountKey,
            new DadCharacterKey("Character@World"),
            1,
            "Character",
            1,
            "World",
            owner.WorkerSessionId,
            owner.ClientInstanceId,
            owner,
            DateTimeOffset.UtcNow);
    }

}
