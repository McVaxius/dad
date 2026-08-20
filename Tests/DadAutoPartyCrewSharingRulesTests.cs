using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAutoPartyCrewSharingRulesTests
{
    [Fact]
    public void ReconciliationPreservesFleetOpaqueIdentityAndPrunesDepartedCrewReferences()
    {
        var configuration = new DadAutoPartyConfiguration
        {
            StandingShareScope = DadAutoPartyCrewShareScope.AllCharacters,
            StandingSharePolicy = new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.CharacterList,
                CharacterHandles = ["obsolete"],
                Enabled = true,
            },
        };
        var pairing = new DadAutoPartyPairing
        {
            LocalSharePolicy = new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.CharacterList,
                CharacterHandles = ["opaque-from-fleet", "obsolete"],
                Enabled = true,
            },
            PeerSharePolicy = new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.CharacterList,
                CharacterHandles = ["peer-opaque"],
                Enabled = true,
            },
        };
        configuration.Pairings.Add(pairing);
        var fleet = new DadAutoPartyFleetConfiguration
        {
            Rows =
            [
                new DadAutoPartyFleetRow
                {
                    RowId = "legacy-row",
                    OpaqueCharacterId = "opaque-from-fleet",
                    AccountKey = "account-a",
                    CharacterKey = "Alice@World",
                },
            ],
        };
        var crew = new[]
        {
            Crew("account-a", "Alice@World", DadCharacterSource.LocalRuntime, 19),
        };

        var first = DadAutoPartyCrewSharingRules.Reconcile(configuration, fleet, crew, DateTime.UtcNow);

        var identity = Assert.Single(first.Candidates).Identity;
        Assert.Equal("opaque-from-fleet", identity.OpaqueCharacterId);
        Assert.Equal(["opaque-from-fleet"], pairing.LocalSharePolicy.CharacterHandles);
        Assert.Equal(["peer-opaque"], pairing.PeerSharePolicy.CharacterHandles);
        Assert.Equal(["opaque-from-fleet"], configuration.StandingSharePolicy.CharacterHandles);

        var second = DadAutoPartyCrewSharingRules.Reconcile(configuration, fleet, [], DateTime.UtcNow);

        Assert.Empty(second.Candidates);
        Assert.Empty(configuration.CrewIdentities);
        Assert.Empty(pairing.LocalSharePolicy.CharacterHandles);
        Assert.False(pairing.LocalSharePolicy.Enabled);
        Assert.Empty(configuration.StandingSharePolicy.CharacterHandles);
        Assert.False(configuration.StandingSharePolicy.Enabled);
    }

    [Fact]
    public void PrivateAndCommunityScopesResolveAgainstCuratedCrew()
    {
        var configuration = new DadAutoPartyConfiguration();
        var reconciliation = DadAutoPartyCrewSharingRules.Reconcile(
            configuration,
            new DadAutoPartyFleetConfiguration(),
            [
                Crew("account-a", "Alice@World", DadCharacterSource.LocalRuntime, 19),
                Crew("account-b", "Bob@World", DadCharacterSource.XadbOnly, 24),
            ],
            DateTime.UtcNow);
        var alice = reconciliation.Candidates.Single(candidate => candidate.IsCurrentCharacter).Identity.OpaqueCharacterId;
        var bob = reconciliation.Candidates.Single(candidate => !candidate.IsCurrentCharacter).Identity.OpaqueCharacterId;

        Assert.True(DadAutoPartyCrewSharingRules.TryBuildPrivatePolicy(
            DadAutoPartyCrewShareScope.AllCharacters,
            reconciliation.Candidates,
            [],
            DateTime.UtcNow,
            out var all));
        Assert.Equal(DadAutoPartyCharacterShareMode.AllCharactersForPeer, all.Mode);

        Assert.True(DadAutoPartyCrewSharingRules.TryBuildPrivatePolicy(
            DadAutoPartyCrewShareScope.CurrentCharacter,
            reconciliation.Candidates,
            [],
            DateTime.UtcNow,
            out var current));
        Assert.Equal(DadAutoPartyCharacterShareMode.SpecificCharacter, current.Mode);
        Assert.Equal([alice], current.CharacterHandles);

        Assert.True(DadAutoPartyCrewSharingRules.TryBuildPrivatePolicy(
            DadAutoPartyCrewShareScope.SpecificCharacters,
            reconciliation.Candidates,
            [bob],
            DateTime.UtcNow,
            out var specific));
        Assert.Equal(DadAutoPartyCharacterShareMode.CharacterList, specific.Mode);
        Assert.Equal([bob], specific.CharacterHandles);

        var community = DadAutoPartyCrewSharingRules.BuildCommunityPolicy(
            DadAutoPartyCrewShareScope.AllCharacters,
            reconciliation.Candidates,
            [],
            DateTime.UtcNow);
        Assert.Equal(DadAutoPartyCharacterShareMode.CharacterList, community.Mode);
        Assert.Equal(new[] { alice, bob }.Order(StringComparer.Ordinal), community.CharacterHandles);
    }

    [Fact]
    public void OfflineXadbCrewPublishesOnlyThroughOneFreshOwnershipProvenDadRoute()
    {
        var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var offline = Crew("account-a", "Alice@World", DadCharacterSource.XadbOnly, 19);
        offline.ContentId = 100;
        offline.XadbReady = true;
        var reconciliation = DadAutoPartyCrewSharingRules.Reconcile(
            new DadAutoPartyConfiguration(),
            new DadAutoPartyFleetConfiguration(),
            [offline],
            now.UtcDateTime);
        var row = new DadRosterCharacter
        {
            AccountKey = new DadAccountKey("account-a"),
            CharacterKey = new DadCharacterKey("Alice@World"),
            ContentId = 100,
            CharacterName = "Alice",
            WorldId = 1,
            WorldName = "World",
            XadbReady = true,
            Visibility = DadRosterVisibility.Active,
        };
        var owner = Owner("worker-a", "account-a", "Alice@World", now.UtcDateTime, isLocal: true);

        var unique = DadAutoPartyCrewSharingRules.AttachInboundRoutes(
            reconciliation.Candidates,
            [row],
            owner,
            [],
            now);

        Assert.True(Assert.Single(unique).Available);
        Assert.NotNull(unique[0].InboundRoute);

        var ambiguous = DadAutoPartyCrewSharingRules.AttachInboundRoutes(
            reconciliation.Candidates,
            [row],
            owner,
            [Owner("worker-b", "account-a", "Alice@World", now.UtcDateTime, isLocal: true)],
            now);
        Assert.False(Assert.Single(ambiguous).Available);
        Assert.Null(ambiguous[0].InboundRoute);
    }

    [Fact]
    public void ClearingOneCrewSlotBindingLeavesOtherBindingsIntact()
    {
        var configuration = new DadAutoPartyConfiguration();
        var pairing = ActivePairing();
        var firstSlot = new DadPlannerGroupSlot { SlotId = "Slot1" };
        var secondSlot = new DadPlannerGroupSlot { SlotId = "Slot2" };

        Assert.True(DadAutoPartyCrewSlotBindingRules.TryBind(
            configuration,
            firstSlot,
            pairing,
            Listing("opaque-a", 19),
            19,
            out var firstBlocker), firstBlocker);
        Assert.True(DadAutoPartyCrewSlotBindingRules.TryBind(
            configuration,
            secondSlot,
            pairing,
            Listing("opaque-b", 24),
            24,
            out var secondBlocker), secondBlocker);
        var secondBindingId = secondSlot.SharedIdentity!.BindingId;

        Assert.False(DadAutoPartyCrewSlotBindingRules.TryBind(
            configuration,
            new DadPlannerGroupSlot { SlotId = "Slot3" },
            pairing,
            Listing("opaque-b", 24),
            24,
            out var duplicateBlocker));
        Assert.Contains("already bound", duplicateBlocker, StringComparison.Ordinal);

        Assert.True(DadAutoPartyCrewSlotBindingRules.Clear(configuration, firstSlot));

        var remaining = Assert.Single(configuration.RemoteBindings);
        Assert.Equal(secondBindingId, remaining.FleetRowId);
        Assert.Equal("opaque-b", remaining.OpaqueCharacterId);
    }

    [Fact]
    public void SavedCrewBindingPersistsOpaquePlaceholderWhileRuntimePresentationUsesPrivateLabel()
    {
        var configuration = new DadAutoPartyConfiguration();
        var pairing = ActivePairing();
        var slot = new DadPlannerGroupSlot { SlotId = "Slot1" };
        var listing = Listing("opaque-private", 19);
        listing.OpaqueDisplayLabel = listing.DisplayLabel;
        listing.DisplayLabel = "Private Character@Private World";

        Assert.True(DadAutoPartyCrewSlotBindingRules.TryBind(
            configuration,
            slot,
            pairing,
            listing,
            19,
            out var blocker), blocker);

        Assert.Equal("Private Character@Private World", listing.DisplayLabel);
        Assert.Equal("Shared character", slot.SharedIdentity!.CharacterLabel);
    }

    private static DadAcquiredCharacter Crew(
        string accountId,
        string characterKey,
        DadCharacterSource source,
        uint currentJob)
        => new()
        {
            AccountId = accountId,
            CharacterKey = characterKey,
            CharacterName = characterKey.Split('@')[0],
            WorldName = "World",
            Source = source,
            CurrentJobId = currentJob,
            JobLevels = new Dictionary<uint, int> { [currentJob] = 100 },
            Readiness = DadReadinessState.Ready,
            Freshness = DadSnapshotFreshness.Live,
        };

    private static DadParticipantSnapshot Owner(
        string worker,
        string account,
        string character,
        DateTime heartbeat,
        bool isLocal = false)
        => new()
        {
            ClientInstanceId = worker + "-client",
            WorkerSessionId = new DadWorkerSessionId(worker),
            IsLocalClient = isLocal,
            IsAvailable = true,
            WorldReadyStable = true,
            AutoRetainerAvailable = true,
            ManagedAccountKey = new DadAccountKey(account),
            ActiveCharacterKey = new DadCharacterKey(character),
            AvailableCharacterKeys = [new DadCharacterKey(character)],
            LastHeartbeatUtc = heartbeat,
            Character = new DadAcquiredCharacter
            {
                CharacterKey = character,
                ContentId = 100,
            },
        };

    private static DadAutoPartyPairing ActivePairing() => new()
    {
        PairingId = Guid.NewGuid().ToString("D"),
        OwnerId = "owner-peer",
        IslandId = "island-peer",
        PublicKeyFingerprint = new string('A', 64),
        LocalFingerprint = new string('B', 64),
        TranscriptHash = new string('C', 64),
        ConfirmedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        SigningPublicKey = Convert.ToBase64String(new byte[32]),
        AgreementPublicKey = Convert.ToBase64String(new byte[32]),
    };

    private static DadAutoPartyListing Listing(string opaqueCharacterId, uint jobId)
        => new()
        {
            ListingId = Guid.NewGuid().ToString("D"),
            OwnerId = "owner-peer",
            SharingIslandId = "island-peer",
            OpaqueCharacterId = opaqueCharacterId,
            DisplayLabel = "Shared character",
            AllowedJobIds = [jobId.ToString()],
            AllowedActivityIds = [DadAutoPartyFreeformRules.FormationActivityId],
            Available = true,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
        };
}
