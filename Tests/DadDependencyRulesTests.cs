using System.Collections;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadDependencyRulesTests
{
    private static readonly DateTime Now = new(2026, 7, 15, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CompleteLoadedSetIsReady()
    {
        var snapshot = Evaluate(ReadyMetadata());

        Assert.True(snapshot.IsReady);
        Assert.Equal(DadDependencyState.Ready, snapshot.AggregateState);
        Assert.Equal(6, snapshot.Entries.Count);
        Assert.All(snapshot.Entries, entry => Assert.Equal(DadDependencyState.Ready, entry.State));
    }

    [Theory]
    [InlineData("FrenRider")]
    [InlineData("ADS")]
    [InlineData("vnavmesh")]
    [InlineData("XADatabase")]
    [InlineData("XASlave")]
    [InlineData("BossMod")]
    public void EveryRequirementBlocksWhenMissing(string requirementId)
    {
        var requirement = DadDependencyRules.Requirements.Single(item => item.RequirementId == requirementId);
        var metadata = ReadyMetadata()
            .Where(plugin => !requirement.AcceptedInternalNames.Contains(plugin.InternalName, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var snapshot = Evaluate(metadata);

        Assert.False(snapshot.IsReady);
        Assert.Equal(DadDependencyState.Missing, snapshot.Entries.Single(entry => entry.RequirementId == requirementId).State);
    }

    [Fact]
    public void InstalledButDisabledIsNotLoaded()
    {
        var metadata = ReadyMetadata();
        Replace(metadata, "ADS", plugin => plugin with { IsLoaded = false });

        var entry = Evaluate(metadata).Entries.Single(item => item.RequirementId == "ADS");

        Assert.Equal(DadDependencyState.InstalledNotLoaded, entry.State);
    }

    [Theory]
    [InlineData("0.0.0.38", DadDependencyState.UpdateRequired)]
    [InlineData("0.0.0.39", DadDependencyState.Ready)]
    [InlineData("0.0.0.40", DadDependencyState.Ready)]
    [InlineData("not-a-version", DadDependencyState.Checking)]
    [InlineData("", DadDependencyState.Checking)]
    public void XaDatabaseMinimumAndMalformedVersions(string version, DadDependencyState expected)
    {
        var metadata = ReadyMetadata();
        Replace(metadata, "XADatabase", plugin => plugin with { Version = version });

        var entry = Evaluate(metadata).Entries.Single(item => item.RequirementId == "XADatabase");

        Assert.Equal(expected, entry.State);
        Assert.Equal("0.0.0.39", entry.MinimumVersion);
    }

    [Fact]
    public void AnyLoadedValidDuplicateWins()
    {
        var metadata = ReadyMetadata();
        metadata.Add(new DadInstalledPluginMetadata("xadatabase", "Old XA Database", "0.0.0.1", true, true));

        var entry = Evaluate(metadata).Entries.Single(item => item.RequirementId == "XADatabase");

        Assert.Equal(DadDependencyState.Ready, entry.State);
        Assert.Equal("XADatabase", entry.DetectedInternalName);
    }

    [Fact]
    public void DuplicatePreferenceIsUpdateBeforeInstalledNotLoaded()
    {
        var metadata = ReadyMetadata();
        metadata.RemoveAll(plugin => plugin.InternalName.Equals("XADatabase", StringComparison.OrdinalIgnoreCase));
        metadata.Add(new DadInstalledPluginMetadata("XADatabase", "XA disabled", "0.0.0.50", false, false));
        metadata.Add(new DadInstalledPluginMetadata("xadatabase", "XA old", "0.0.0.10", true, false));

        var entry = Evaluate(metadata).Entries.Single(item => item.RequirementId == "XADatabase");

        Assert.Equal(DadDependencyState.UpdateRequired, entry.State);
        Assert.Equal("0.0.0.10", entry.DetectedVersion);
    }

    [Theory]
    [InlineData("BossModReborn")]
    [InlineData("BossMod")]
    [InlineData("bossmodreborn")]
    public void EitherBossModAlternativeSatisfiesRequirement(string internalName)
    {
        var metadata = ReadyMetadata();
        metadata.RemoveAll(plugin => plugin.InternalName.Equals("BossModReborn", StringComparison.OrdinalIgnoreCase));
        metadata.Add(new DadInstalledPluginMetadata(internalName, "Boss", "1.0.0.0", true, false));

        Assert.Equal(
            DadDependencyState.Ready,
            Evaluate(metadata).Entries.Single(item => item.RequirementId == "BossMod").State);
    }

    [Fact]
    public void MatchingIsCaseInsensitiveForAllRequirements()
    {
        var metadata = ReadyMetadata()
            .Select(plugin => plugin with { InternalName = plugin.InternalName.ToUpperInvariant() })
            .ToList();

        Assert.True(Evaluate(metadata).IsReady);
    }

    [Fact]
    public void EnumerationFailureAndNullMetadataAreChecking()
    {
        var failed = DadDependencyRules.Evaluate(new ThrowingMetadata(), 9, Now);
        var incomplete = DadDependencyRules.Evaluate([null!], 10, Now);
        var unavailable = DadDependencyRules.Evaluate(null, 11, Now);

        Assert.Equal(DadDependencyState.Checking, failed.AggregateState);
        Assert.Equal(DadDependencyState.Checking, incomplete.AggregateState);
        Assert.Equal(DadDependencyState.Checking, unavailable.AggregateState);
    }

    [Fact]
    public void DirtyCheckingRetainsDetailsThenFreshInspectionRecovers()
    {
        var ready = Evaluate(ReadyMetadata(), revision: 5);

        var checking = DadDependencySnapshot.CreateChecking(6, ready, "dirty");
        var recovered = DadDependencyRules.Evaluate(ReadyMetadata(), 7, Now.AddSeconds(2));

        Assert.False(checking.IsReady);
        Assert.All(checking.Entries, entry => Assert.Equal(DadDependencyState.Checking, entry.State));
        Assert.All(checking.Entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.DetectedVersion)));
        Assert.True(recovered.IsReady);
        Assert.Equal(7, recovered.Revision);
    }

    [Fact]
    public void SnapshotCloneAndJsonRoundTripPreserveVersionedTruth()
    {
        var participant = FreshParticipant("worker", ready: true);

        var json = DadIpcJson.Serialize(participant);
        var roundTrip = DadIpcJson.Deserialize<DadParticipantSnapshot>(json);
        var clone = participant.Clone();

        Assert.NotNull(roundTrip);
        Assert.True(roundTrip!.Dependencies.IsReady);
        Assert.Equal(DadDependencySnapshot.CurrentSchemaVersion, roundTrip.Dependencies.SchemaVersion);
        Assert.True(clone.Dependencies.IsReady);
        Assert.NotSame(participant.Dependencies, clone.Dependencies);
    }

    [Theory]
    [InlineData("{\"ClientInstanceId\":\"legacy\"}")]
    [InlineData("{\"ClientInstanceId\":\"legacy\",\"Dependencies\":null}")]
    public void LegacyOrNullParticipantPayloadIsChecking(string json)
    {
        var participant = DadIpcJson.Deserialize<DadParticipantSnapshot>(json);

        Assert.NotNull(participant);
        Assert.Equal(DadDependencyState.Checking, participant!.Dependencies.AggregateState);
        Assert.False(participant.Dependencies.IsReady);
    }

    [Fact]
    public void CrewGateCoversFreshOfflineStaleMissingLegacyAndRecovery()
    {
        var coordinator = FreshParticipant("coordinator", ready: true);
        var offline = FreshParticipant("offline", ready: true);
        offline.IsAvailable = false;
        var stale = FreshParticipant("stale", ready: true);
        stale.LastHeartbeatUtc = Now.AddMinutes(-1);
        var legacy = FreshParticipant("legacy", ready: false);

        Assert.True(DadDependencyGateRules.EvaluateCrew(coordinator, [offline], Now, TimeSpan.FromSeconds(15)).Ready);
        Assert.False(DadDependencyGateRules.EvaluateCrew(coordinator, [stale], Now, TimeSpan.FromSeconds(15)).Ready);
        Assert.False(DadDependencyGateRules.EvaluateCrew(coordinator, [null], Now, TimeSpan.FromSeconds(15)).Ready);
        Assert.False(DadDependencyGateRules.EvaluateCrew(coordinator, [legacy], Now, TimeSpan.FromSeconds(15)).Ready);

        legacy.Dependencies = Evaluate(ReadyMetadata(), revision: 2);
        Assert.True(DadDependencyGateRules.EvaluateCrew(coordinator, [legacy], Now, TimeSpan.FromSeconds(15)).Ready);
    }

    [Fact]
    public void UnselectedClientDoesNotBlockCrew()
    {
        var coordinator = FreshParticipant("coordinator", ready: true);
        var selected = FreshParticipant("selected", ready: true);
        _ = FreshParticipant("unselected", ready: false);

        Assert.True(DadDependencyGateRules.EvaluateCrew(coordinator, [selected], Now, TimeSpan.FromSeconds(15)).Ready);
    }

    [Fact]
    public void StaleProjectionDowngradesDependencyTruthImmediately()
    {
        var participant = FreshParticipant("remote", ready: true);

        var stale = DadHubParticipants.PrepareRemoteWithStaleState(
            participant,
            Now.AddSeconds(-20),
            Now,
            TimeSpan.FromSeconds(15),
            "stale");

        Assert.Equal(DadParticipantState.Stale, stale.State);
        Assert.Equal(DadDependencyState.Checking, stale.Dependencies.AggregateState);
        Assert.False(stale.Dependencies.IsReady);
    }

    [Fact]
    public void PopupLifecycleSuppressesDisabledAndRecoversAutomatically()
    {
        var missing = DadDependencyRules.Evaluate([], 1, Now);
        var checking = DadDependencySnapshot.CreateChecking(2, missing);
        var ready = Evaluate(ReadyMetadata(), revision: 3);

        Assert.False(DadDependencyWindowRules.ShouldBeOpen(false, missing));
        Assert.True(DadDependencyWindowRules.ShouldBeOpen(true, missing));
        Assert.True(DadDependencyWindowRules.ShouldBeOpen(true, checking));
        Assert.True(DadDependencyWindowRules.ResolveCloseAttempt(true, missing));
        Assert.False(DadDependencyWindowRules.ShouldBeOpen(true, ready));
        Assert.True(DadDependencyWindowRules.ShouldBeOpen(true, missing));
        Assert.False(DadDependencyWindowRules.ResolveCloseAttempt(false, missing));
    }

    [Fact]
    public void InstallerRulesExposeSeparateBossModChoicesWithoutRepositories()
    {
        var missing = DadDependencyRules.Evaluate([], 1, Now);
        var boss = missing.Entries.Single(entry => entry.RequirementId == "BossMod");
        var xadb = missing.Entries.Single(entry => entry.RequirementId == "XADatabase");

        var bossOptions = DadDependencyInstallerRules.ResolveOptions(boss);
        var xadbOptions = DadDependencyInstallerRules.ResolveOptions(xadb);

        Assert.Equal(["BossModReborn", "BossMod"], bossOptions.Select(option => option.SearchText));
        Assert.Single(xadbOptions);
        Assert.Equal("XADatabase", xadbOptions[0].SearchText);
        Assert.DoesNotContain(bossOptions.Concat(xadbOptions), option => option.SearchText.Contains("http", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MutationBoundaryBlocksNewWorkButPreservesApprovedRun()
    {
        Assert.False(DadDependencyMutationBoundaryRules.CanCross(false, [true, false, true]));
        Assert.True(DadDependencyMutationBoundaryRules.CanCross(false, [true, true]));
        Assert.True(DadDependencyMutationBoundaryRules.CanCross(true, [false, false]));
    }

    [Fact]
    public void SchedulerStatusClonePreservesDependencyFieldsAndAppendedEnumValue()
    {
        var slot = new DadSchedulerSlotState
        {
            DependenciesReady = false,
            DependencyState = DadDependencyState.UpdateRequired,
            DependencyRevision = 42,
            DependencySummary = "update",
        };

        var clone = slot.Clone();

        Assert.False(clone.DependenciesReady);
        Assert.Equal(DadDependencyState.UpdateRequired, clone.DependencyState);
        Assert.Equal(42, clone.DependencyRevision);
        Assert.Equal("update", clone.DependencySummary);
        Assert.Equal(12, (int)DadSchedulerPresetPhase.Skipped);
        Assert.Equal(13, (int)DadSchedulerPresetPhase.WaitingForDependencies);
    }

    [Fact]
    public void DebugVisibilityUsesStableCrewStepIdsAndPreservesHiddenFields()
    {
        var slot = new DadPlannerGroupSlot { LaunchProfileId = "preserved-profile" };

        Assert.False(DadDebugUiRules.ShowLaunchProfiles(false));
        Assert.True(DadDebugUiRules.ShowLaunchProfiles(true));
        Assert.Equal(10, DadDebugUiRules.PresetCrewColumnCount(false));
        Assert.Equal(11, DadDebugUiRules.PresetCrewColumnCount(true));
        Assert.Equal(
            DadDebugUiRules.CrewReviewStepId,
            DadDebugUiRules.ResolveVisibleCrewStep(DadDebugUiRules.CrewLaunchProfilesStepId, false));
        Assert.Equal(
            DadDebugUiRules.CrewLaunchProfilesStepId,
            DadDebugUiRules.ResolveVisibleCrewStep(DadDebugUiRules.CrewLaunchProfilesStepId, true));
        Assert.False(DadDebugUiRules.CanRunLaunchProfileDiagnostics(false));
        Assert.Equal("preserved-profile", slot.LaunchProfileId);
        Assert.Equal("Wake/relog", DadDebugUiRules.FormatWakePolicy(DadSchedulerWakePolicy.LaunchIfOffline, false));
        Assert.Equal("LaunchIfOffline", DadDebugUiRules.FormatWakePolicy(DadSchedulerWakePolicy.LaunchIfOffline, true));
    }

    [Fact]
    public void RuntimeReadinessSignatureTracksDependencyRevisionAndState()
    {
        var participant = FreshParticipant("worker", ready: true);
        var first = DadRuntimeReadinessSignature.Create(participant);
        participant.Dependencies = DadDependencySnapshot.CreateChecking(99, participant.Dependencies);
        var second = DadRuntimeReadinessSignature.Create(participant);

        Assert.NotEqual(first, second);
        Assert.Equal(99, second.DependencyRevision);
        Assert.Equal(DadDependencyState.Checking, second.DependencyState);
    }

    private static DadParticipantSnapshot FreshParticipant(string worker, bool ready)
        => new()
        {
            ClientInstanceId = worker,
            WorkerSessionId = new DadWorkerSessionId(worker),
            State = DadParticipantState.Ready,
            LastHeartbeatUtc = Now,
            Dependencies = ready ? Evaluate(ReadyMetadata()) : DadDependencySnapshot.CreateChecking(),
        };

    private static DadDependencySnapshot Evaluate(List<DadInstalledPluginMetadata> metadata, long revision = 1)
        => DadDependencyRules.Evaluate(metadata, revision, Now);

    private static List<DadInstalledPluginMetadata> ReadyMetadata()
        =>
        [
            new("FrenRider", "Fren Rider", "1.0.0.0", true, false),
            new("ADS", "AI Duty Solver", "1.0.0.0", true, false),
            new("vnavmesh", "vnavmesh", "1.0.0.0", true, false),
            new("XADatabase", "XA Database", "0.0.0.39", true, false),
            new("XASlave", "XA Slave", "1.0.0.0", true, false),
            new("BossModReborn", "BossModReborn", "1.0.0.0", true, false),
        ];

    private static void Replace(
        List<DadInstalledPluginMetadata> metadata,
        string internalName,
        Func<DadInstalledPluginMetadata, DadInstalledPluginMetadata> update)
    {
        var index = metadata.FindIndex(plugin => plugin.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase));
        metadata[index] = update(metadata[index]);
    }

    private sealed class ThrowingMetadata : IEnumerable<DadInstalledPluginMetadata>
    {
        public IEnumerator<DadInstalledPluginMetadata> GetEnumerator() => throw new InvalidOperationException("inspection failed");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
