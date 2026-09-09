using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadCharacterXadbMergeRulesTests
{
    [Fact]
    public void ExactPartialSnapshotPreservesMetadataAndKeepsCharacterAndPeerReady()
    {
        var snapshotUtc = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);
        var current = new DadAcquiredCharacter
        {
            CharacterKey = "Current Character@World",
            ContentId = 200,
            Freshness = DadSnapshotFreshness.Live,
            Readiness = DadReadinessState.Ready,
        };
        var exact = new DadXadbStatus
        {
            IsReady = true,
            Availability = "Ready",
            ContentId = 200,
            CharacterName = "Current Character",
            WorldName = "World",
            SnapshotUtc = snapshotUtc,
            SnapshotVersion = 9,
            SnapshotQuality = "Partial",
            JobLevels = new Dictionary<uint, int> { [35] = 100 },
        };

        DadCharacterXadbMergeRules.Merge(current, exact);

        Assert.True(current.XadbReady);
        Assert.Equal(snapshotUtc, current.XadbSnapshotUtc);
        Assert.Equal(9, current.SnapshotVersion);
        Assert.Equal("Partial", current.SnapshotQuality);
        Assert.Equal(100, current.JobLevels[35]);
        Assert.Equal(DadReadinessState.Ready, current.Readiness);
        Assert.Empty(current.Blockers);

        var peerCharacter = current.Clone();
        peerCharacter.Source = DadCharacterSource.PeerRuntime;
        var projection = DadPeerRuntimeProjectionRules.Evaluate(new DadParticipantSnapshot
        {
            State = DadParticipantState.Ready,
            IsAvailable = true,
            IsEligibleForRun = true,
            AuthorityMode = DadAuthorityMode.ServerDad,
        }, peerCharacter);

        Assert.Equal(DadReadinessState.Ready, projection.Readiness);
        Assert.Empty(projection.Blockers);
    }

    [Fact]
    public void PriorCharacterXadbSnapshotCannotStampCurrentCharacterMetadataOrJobs()
    {
        var snapshotUtc = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
        var current = new DadAcquiredCharacter
        {
            CharacterKey = "Current Character@World",
            ContentId = 200,
            JobLevels = new Dictionary<uint, int> { [35] = 100 },
        };
        var prior = new DadXadbStatus
        {
            IsReady = true,
            Availability = "Ready",
            ContentId = 100,
            CharacterName = "Prior Character",
            WorldName = "World",
            SnapshotUtc = snapshotUtc,
            SnapshotVersion = 9,
            SnapshotQuality = "full",
            JobLevels = new Dictionary<uint, int> { [32] = 95 },
        };

        DadCharacterXadbMergeRules.Merge(current, prior);

        Assert.False(current.XadbReady);
        Assert.Null(current.XadbSnapshotUtc);
        Assert.Null(current.SnapshotVersion);
        Assert.Equal(string.Empty, current.SnapshotQuality);
        Assert.Equal(new Dictionary<uint, int> { [35] = 100 }, current.JobLevels);
        Assert.Contains(current.Blockers, blocker => blocker.Contains("identity does not match", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExactCharacterSnapshotUnionsJobsWithoutLoweringRuntimeKnowledge()
    {
        var current = new DadAcquiredCharacter
        {
            CharacterKey = "Current Character@World",
            ContentId = 200,
            CurrentJobId = 35,
            CurrentLevel = 100,
            JobLevels = new Dictionary<uint, int> { [32] = 95, [35] = 100 },
        };
        var exact = new DadXadbStatus
        {
            IsReady = true,
            Availability = "Ready",
            ContentId = 200,
            CharacterName = "Current Character",
            WorldName = "World",
            JobLevels = new Dictionary<uint, int> { [32] = 80, [24] = 90 },
        };

        DadCharacterXadbMergeRules.Merge(current, exact);

        Assert.True(current.XadbReady);
        Assert.Equal(95, current.JobLevels[32]);
        Assert.Equal(100, current.JobLevels[35]);
        Assert.Equal(90, current.JobLevels[24]);
    }
}
