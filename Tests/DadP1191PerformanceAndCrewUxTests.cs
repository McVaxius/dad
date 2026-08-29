using System.Text.Json;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadP1191PerformanceAndCrewUxTests
{
    [Fact]
    public void VersionThreeConfigurationMigratesOnceAndDropsHistoricalProfileCatalogJson()
    {
        const string json = """
            {
              "Version": 3,
              "ClientAccountId": "dad-client-stable",
              "ProfileCatalogCache": [
                {
                  "OwnerClientInstanceId": "historical-process",
                  "OwnerWorkerSessionId": { "Value": "historical-worker" },
                  "Accounts": []
                }
              ]
            }
            """;
        var configuration = JsonSerializer.Deserialize<Configuration>(json);
        Assert.NotNull(configuration);
        var now = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
        var saveCount = 0;
        var persistence = new DadConfigurationPersistenceCoordinator(
            () => saveCount++,
            () => now);
        configuration!.AttachPersistenceCoordinator(persistence);

        var identityStore = new MissingAutoPartyIdentityStore();
        var webhookStore = new MissingAutoPartyWebhookStore();
        if (DadAutoPartyConfigurationMigration.Migrate(configuration, identityStore, webhookStore))
            configuration.Save();

        Assert.Equal(12, configuration.Version);
        Assert.False(configuration.AutoParty.Enabled);
        Assert.Equal(DadAutoPartyRegistrationState.Unregistered, configuration.AutoParty.RegistrationState);
        Assert.Equal(string.Empty, configuration.AutoParty.EndpointIdentityReference);
        Assert.Equal(string.Empty, configuration.AutoParty.RegisteredOwnerId);
        Assert.Equal(string.Empty, configuration.AutoParty.RegisteredIslandId);
        Assert.Empty(configuration.AutoParty.Pairings);
        Assert.Empty(configuration.AutoParty.PendingPairings);
        Assert.Empty(configuration.AutoParty.Grants);
        Assert.Empty(configuration.AutoParty.Listings);
        Assert.Empty(configuration.AutoParty.Deauthentications);
        Assert.Equal(0, saveCount);
        now = now.AddMilliseconds(250);
        Assert.True(persistence.Update());
        Assert.Equal(1, saveCount);
        Assert.DoesNotContain("ProfileCatalogCache", JsonSerializer.Serialize(configuration), StringComparison.Ordinal);
        Assert.False(DadAutoPartyConfigurationMigration.Migrate(configuration, identityStore, webhookStore));
        Assert.False(persistence.Update());
        Assert.Equal(1, saveCount);
    }

    [Fact]
    public void RemoteProfileCatalogCacheIsRefreshGatedOnlineOnlyAndPrunesStaleSessions()
    {
        var start = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
        var cache = new DadOnlineProfileCatalogCache(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15));
        Assert.True(cache.TryBeginRefresh(start));
        Assert.False(cache.TryBeginRefresh(start.AddSeconds(59)));
        Assert.True(cache.TryBeginRefresh(start.AddSeconds(60)));

        cache.Upsert(Profile("worker-a", "account-a"), start);
        cache.Upsert(Profile("worker-b", "account-b"), start);
        var online = new HashSet<string>(["worker-a", "worker-b"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, cache.BuildOnlineProjection(worker => online.Contains(worker.Value)).Count);

        online.Remove("worker-b");
        Assert.True(cache.ObserveTransport(start.AddSeconds(1), worker => online.Contains(worker.Value)));
        Assert.Single(cache.BuildOnlineProjection(worker => online.Contains(worker.Value)));
        Assert.Equal(2, cache.Count);
        Assert.True(cache.ObserveTransport(start.AddSeconds(17), worker => online.Contains(worker.Value)));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void RemoteProfileAccountEvictionUpdatesTheInMemoryProjectionImmediately()
    {
        var now = DateTime.UtcNow;
        var cache = new DadOnlineProfileCatalogCache(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15));
        cache.Upsert(Profile("worker-a", "account-a"), now);

        Assert.True(cache.RemoveAccount(new DadAccountKey("ACCOUNT-A")));
        var catalog = Assert.Single(cache.BuildOnlineProjection(static _ => true));
        Assert.Empty(catalog.Accounts);
        Assert.False(cache.RemoveAccount(new DadAccountKey("account-a")));
    }

    [Fact]
    public void DeferredRosterSaveHonorsQuietMaximumAndForceFlushBoundaries()
    {
        var start = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
        var quiet = new DadDeferredSaveGate(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
        quiet.MarkDirty(start);
        Assert.False(quiet.TryConsumeDue(start.AddSeconds(9)));
        Assert.True(quiet.TryConsumeDue(start.AddSeconds(10)));
        Assert.False(quiet.IsPending);

        var continuous = new DadDeferredSaveGate(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
        continuous.MarkDirty(start);
        foreach (var seconds in new[] { 9, 18, 27, 36, 45, 54 })
            continuous.MarkDirty(start.AddSeconds(seconds));
        Assert.False(continuous.TryConsumeDue(start.AddSeconds(59)));
        Assert.True(continuous.TryConsumeDue(start.AddSeconds(60)));

        var forced = new DadDeferredSaveGate(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
        forced.MarkDirty(start);
        Assert.True(forced.TryConsumeDue(start.AddSeconds(1), force: true));
    }

    [Fact]
    public void TypedProjectionCacheReusesSemanticKeysAndHonorsExpiryAndInvalidation()
    {
        var cache = new DadProjectionCache<(long Catalog, long Transport), object>();
        var now = new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
        var builds = 0;
        object Build()
        {
            builds++;
            return new object();
        }

        var first = cache.GetOrCreate((1, 1), Build, now, now.AddMinutes(1));
        Assert.Same(first, cache.GetOrCreate((1, 1), Build, now.AddSeconds(59), now.AddMinutes(2)));
        Assert.NotSame(first, cache.GetOrCreate((2, 1), Build, now, now.AddMinutes(1)));
        Assert.Equal(2, builds);
        cache.GetOrCreate((2, 1), Build, now.AddMinutes(1));
        Assert.Equal(3, builds);
        cache.Invalidate();
        cache.GetOrCreate((2, 1), Build, now.AddMinutes(1));
        Assert.Equal(4, builds);
    }

    [Fact]
    public void SemanticRevisionTrackerAdvancesOnlyForDirectSemanticChanges()
    {
        var tracker = new DadSemanticRevisionTracker<DadOrderedSemantic<string>>();

        Assert.Equal(1, tracker.Observe(new DadOrderedSemantic<string>(["alpha", "beta"])));
        Assert.Equal(1, tracker.Observe(new DadOrderedSemantic<string>(["alpha", "beta"])));
        Assert.Equal(2, tracker.Observe(new DadOrderedSemantic<string>(["alpha", "gamma"])));
    }

    [Fact]
    public void PlannerCachesUseSemanticRevisionsExpiryAndImmutableComposition()
    {
        var pluginSource = ReadRepositorySource("Plugin.cs");
        var characterIntelligenceSource = ReadRepositorySource(
            "Services",
            "DadCharacterIntelligenceService.cs");
        var schedulerSource = ReadRepositorySource("Services", "DadSchedulerService.cs");
        var plannerStart = pluginSource.IndexOf(
            "internal DadPlannerUiSnapshot GetPlannerUiSnapshot",
            StringComparison.Ordinal);
        var plannerEnd = pluginSource.IndexOf(
            "private IReadOnlyList<DadPlannerLanePreviewSnapshot>",
            plannerStart,
            StringComparison.Ordinal);
        var planner = pluginSource[plannerStart..plannerEnd];
        var schedulerStart = schedulerSource.IndexOf(
            "internal DadSchedulerUiRevision GetPlannerUiRevision",
            StringComparison.Ordinal);
        var schedulerEnd = schedulerSource.IndexOf(
            "public DadSchedulerQueueSnapshot GetQueueSnapshot",
            schedulerStart,
            StringComparison.Ordinal);
        var scheduler = schedulerSource[schedulerStart..schedulerEnd];

        Assert.Contains("DadProjectionCache<DadPlannerUiCacheKey", pluginSource, StringComparison.Ordinal);
        Assert.Contains("CharacterIntelligenceService.PlannerSemanticRevision", planner, StringComparison.Ordinal);
        Assert.Contains("plannerSemanticRevisionTracker.Observe", characterIntelligenceSource, StringComparison.Ordinal);
        Assert.Contains("GetPlannerSemanticRevision", planner, StringComparison.Ordinal);
        Assert.Contains("schedulerRevision.ValidUntilUtc", planner, StringComparison.Ordinal);
        Assert.Contains("with { SchedulerPreview = schedulerPreview }", planner, StringComparison.Ordinal);
        Assert.DoesNotContain("SchedulerPreview = cached", planner, StringComparison.Ordinal);
        Assert.DoesNotContain("LastUpdatedUtc.Ticks", planner, StringComparison.Ordinal);
        Assert.DoesNotContain("SnapshotUtc?.Ticks", planner, StringComparison.Ordinal);
        Assert.DoesNotContain("new HashCode", scheduler, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdatedAtUtc", scheduler, StringComparison.Ordinal);
        Assert.DoesNotContain("NextTakeoverStatusCheckUtc", scheduler[..scheduler.IndexOf("validUntilUtc", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void AutoPartyProjectionAndSetupWizardReuseTheirPerFrameWork()
    {
        var autoPartySource = ReadRepositorySource("Services", "DadAutoPartyService.cs");
        var cacheHit = autoPartySource.IndexOf(
            "if (windowProjectionCache.TryGet(key, now, out var cached))",
            StringComparison.Ordinal);
        var listingClone = autoPartySource.IndexOf(
            ".Select(static item => item.Clone())",
            cacheHit,
            StringComparison.Ordinal);
        Assert.True(cacheHit >= 0 && listingClone > cacheHit);

        var wizardSource = ReadRepositorySource("Windows", "SetupWizardWindow.cs");
        Assert.Contains("DrawStep(steps[stepIndex], steps);", wizardSource, StringComparison.Ordinal);
        var drawStepStart = wizardSource.IndexOf(
            "private void DrawStep(GuideStep step, IReadOnlyList<GuideStep> steps)",
            StringComparison.Ordinal);
        var drawStepEnd = wizardSource.IndexOf(
            "private void DrawStepContent()",
            drawStepStart,
            StringComparison.Ordinal);
        Assert.DoesNotContain("BuildSteps()", wizardSource[drawStepStart..drawStepEnd], StringComparison.Ordinal);
    }

    [Fact]
    public void CrewAccountPresentationDefaultsToAliasAndDetailsAddsStableId()
    {
        var option = new DadRosterAccountOption
        {
            AccountKey = new DadAccountKey("dad-client-stable"),
            AccountAlias = "Raid Lead",
            OwnerOnline = false,
        };
        Assert.Equal("Raid Lead [offline]", DadCrewAccountPresentationRules.Format(option, showDetails: false));
        Assert.Equal("Raid Lead [dad-client-stable] [offline]", DadCrewAccountPresentationRules.Format(option, showDetails: true));

        option.AccountAlias = string.Empty;
        option.DisplayName = "Derived display text";
        option.OwnerOnline = true;
        Assert.Equal("dad-client-stable", DadCrewAccountPresentationRules.Format(option, showDetails: false));
    }

    [Fact]
    public void ShowAccountNavigationSelectsAccountAndClearsSecondaryFilters()
    {
        var state = DadRosterBrowseFilterState.ShowAccount(new DadAccountKey("account-a"));
        Assert.Equal("account-a", state.Account);
        Assert.Equal(string.Empty, state.Search);
        Assert.Equal(string.Empty, state.Assigned);
        Assert.Equal(string.Empty, state.Visibility);
        Assert.Equal(string.Empty, state.WorldDc);
        Assert.Equal(string.Empty, state.Source);
        Assert.Equal(string.Empty, state.Client);
        Assert.False(state.StaleOnly);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Account")]
    [InlineData("DAD client")]
    [InlineData("dad-client-stable")]
    public void NamingRejectsBlankGenericAndStableIdAliases(string alias)
        => Assert.False(DadClientNamingRules.TryValidate(alias, "dad-client-stable", out _, out _));

    [Fact]
    public void NamingReadinessRequiresTheExactStableAccountAndMeaningfulAlias()
    {
        var account = new AccountConfig { AccountId = "dad-client-stable", AccountAlias = "Basement Crew" };
        Assert.True(DadClientNamingRules.IsReady(account, "dad-client-stable"));
        Assert.False(DadClientNamingRules.IsReady(account, "another-id"));
        account.AccountAlias = "Account";
        Assert.False(DadClientNamingRules.IsReady(account, "dad-client-stable"));
    }

    [Fact]
    public void ConnectionGuideSurfaceDoesNotRequireRosterOrPlannerSnapshots()
    {
        var coordinator = DadGuideSurfaceRules.RequiredFor("Coordinator");
        var client = DadGuideSurfaceRules.RequiredFor("Client");
        Assert.Equal(DadGuideSurface.Transport, coordinator);
        Assert.Equal(DadGuideSurface.Transport, client);
        Assert.False(coordinator.HasFlag(DadGuideSurface.Roster));
        Assert.False(coordinator.HasFlag(DadGuideSurface.Planner));
        Assert.Equal(DadGuideSurface.Roster, DadGuideSurfaceRules.RequiredFor("Crew"));
    }

    private static DadProfileCatalog Profile(string workerId, string accountId)
        => new()
        {
            OwnerClientInstanceId = $"client-{workerId}",
            OwnerWorkerSessionId = new DadWorkerSessionId(workerId),
            Accounts =
            [
                new DadAccountProfileRecord
                {
                    AccountKey = new DadAccountKey(accountId),
                    AccountAlias = accountId,
                    Revision = 1,
                },
            ],
        };

    private static string ReadRepositorySource(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dad.csproj")))
            directory = directory.Parent;
        var repositoryRoot = directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate the DAD repository root from the test output directory.");
        return File.ReadAllText(Path.Combine([repositoryRoot, .. pathParts]));
    }
}
