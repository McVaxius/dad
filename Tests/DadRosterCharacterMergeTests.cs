using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRosterCharacterMergeTests
{
    private const uint FisherJobId = 18;
    private const uint WhiteMageJobId = 24;
    private const uint DarkKnightJobId = 32;

    [Fact]
    public void NormalizeXadbSnapshotUsesCurrentJobsLevel()
    {
        var character = CreateXadbCharacter(currentLevel: 18);

        DadRosterCharacterMerge.NormalizeXadbSnapshot(character);

        Assert.Equal(40, character.CurrentLevel);
    }

    [Fact]
    public void NormalizeXadbSnapshotInfersSoleCombatJob()
    {
        var character = new DadRosterCharacter
        {
            CurrentJobId = null,
            CurrentLevel = null,
            JobLevels = new Dictionary<uint, int>
            {
                [FisherJobId] = 100,
                [WhiteMageJobId] = 40,
            },
            XadbReady = true,
        };

        DadRosterCharacterMerge.NormalizeXadbSnapshot(character);

        Assert.Equal(WhiteMageJobId, character.CurrentJobId);
        Assert.Equal(40, character.CurrentLevel);
    }

    [Fact]
    public void NormalizeXadbSnapshotLeavesAmbiguousCombatJobUnset()
    {
        var character = new DadRosterCharacter
        {
            CurrentJobId = null,
            CurrentLevel = null,
            JobLevels = new Dictionary<uint, int>
            {
                [FisherJobId] = 100,
                [WhiteMageJobId] = 40,
                [DarkKnightJobId] = 90,
            },
            XadbReady = true,
        };

        DadRosterCharacterMerge.NormalizeXadbSnapshot(character);

        Assert.Null(character.CurrentJobId);
        Assert.Null(character.CurrentLevel);
    }

    [Fact]
    public void AuthoritativeXadbSnapshotReplacesCachedJobAndSnapshotFields()
    {
        var cachedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var freshAt = new DateTime(2026, 6, 19, 0, 0, 0, DateTimeKind.Utc);
        var cached = new DadRosterCharacter
        {
            AccountKey = new DadAccountKey("account-1"),
            LastSnapshotUtc = cachedAt,
            JobLevels = new Dictionary<uint, int>
            {
                [WhiteMageJobId] = 18,
                [19] = 90,
            },
            CurrentJobId = WhiteMageJobId,
            CurrentJobAbbrev = "WHM",
            CurrentLevel = 18,
            SnapshotQuality = "cached",
            SnapshotVersion = 1,
            XadbReady = true,
        };
        var fresh = CreateXadbCharacter(currentLevel: 18);
        fresh.LastSnapshotUtc = freshAt;
        fresh.SnapshotQuality = "complete";
        fresh.SnapshotVersion = 7;

        DadRosterCharacterMerge.ApplyAuthoritativeXadbSnapshot(cached, fresh);

        Assert.Equal("account-1", cached.AccountKey.ToString());
        Assert.Equal(freshAt, cached.LastSnapshotUtc);
        Assert.Equal(40, cached.CurrentLevel);
        Assert.Equal("WHM", cached.CurrentJobAbbrev);
        Assert.Equal(7, cached.SnapshotVersion);
        Assert.Equal("complete", cached.SnapshotQuality);
        Assert.Equal(fresh.JobLevels.Count, cached.JobLevels.Count);
        Assert.All(fresh.JobLevels, pair => Assert.Equal(pair.Value, cached.JobLevels[pair.Key]));
        Assert.False(cached.JobLevels.ContainsKey(19));
    }

    [Fact]
    public void RepeatXadbRefreshKeepsNormalizedLevel()
    {
        var cached = CreateXadbCharacter(currentLevel: 18);
        DadRosterCharacterMerge.ApplyAuthoritativeXadbSnapshot(cached, CreateXadbCharacter(currentLevel: 18));

        DadRosterCharacterMerge.ApplyAuthoritativeXadbSnapshot(cached, CreateXadbCharacter(currentLevel: 18));

        Assert.Equal(40, cached.CurrentLevel);
        Assert.Equal(40, cached.JobLevels[WhiteMageJobId]);
    }

    [Theory]
    [InlineData(DadCharacterSource.LocalRuntime)]
    [InlineData(DadCharacterSource.PeerRuntime)]
    public void RuntimeOverlayUpdatesCurrentFieldsWithoutOverwritingCompleteXadbJobs(DadCharacterSource source)
    {
        var xadb = new DadRosterCharacter
        {
            Source = DadCharacterSource.XadbOnly,
            CurrentJobId = null,
            CurrentJobAbbrev = string.Empty,
            CurrentLevel = null,
            JobLevels = new Dictionary<uint, int>
            {
                [FisherJobId] = 100,
                [WhiteMageJobId] = 40,
            },
            XadbReady = true,
        };
        var xadbSnapshotUtc = new DateTime(2026, 6, 19, 0, 0, 0, DateTimeKind.Utc);
        xadb.LastSnapshotUtc = xadbSnapshotUtc;
        xadb.SnapshotQuality = "complete";
        xadb.SnapshotVersion = 7;
        DadRosterCharacterMerge.NormalizeXadbSnapshot(xadb);
        var runtime = new DadRosterCharacter
        {
            Source = source,
            LastSnapshotUtc = xadbSnapshotUtc.AddHours(1),
            CurrentJobId = WhiteMageJobId,
            CurrentJobAbbrev = "WHM",
            CurrentLevel = 18,
            JobLevels = new Dictionary<uint, int>
            {
                [WhiteMageJobId] = 18,
            },
            SnapshotQuality = "runtime",
            SnapshotVersion = 99,
        };

        DadRosterCharacterMerge.MergeNonAuthoritativeSnapshot(xadb, runtime);

        Assert.Equal(WhiteMageJobId, xadb.CurrentJobId);
        Assert.Equal("WHM", xadb.CurrentJobAbbrev);
        Assert.Equal(40, xadb.CurrentLevel);
        Assert.Equal(40, xadb.JobLevels[WhiteMageJobId]);
        Assert.Equal(100, xadb.JobLevels[FisherJobId]);
        Assert.Equal(2, xadb.JobLevels.Count);
        Assert.Equal(xadbSnapshotUtc, xadb.LastSnapshotUtc);
        Assert.Equal("complete", xadb.SnapshotQuality);
        Assert.Equal(7, xadb.SnapshotVersion);
    }

    [Fact]
    public void RuntimeOverlayKeepsRuntimeLevelWhenCurrentJobIsMissingFromXadbJobs()
    {
        const uint pictomancerJobId = 42;
        var xadb = new DadRosterCharacter
        {
            Source = DadCharacterSource.XadbOnly,
            CurrentJobId = WhiteMageJobId,
            CurrentJobAbbrev = "WHM",
            CurrentLevel = 40,
            JobLevels = new Dictionary<uint, int>
            {
                [FisherJobId] = 100,
                [WhiteMageJobId] = 40,
            },
            XadbReady = true,
        };
        var runtime = new DadRosterCharacter
        {
            Source = DadCharacterSource.LocalRuntime,
            CurrentJobId = pictomancerJobId,
            CurrentJobAbbrev = "PCT",
            CurrentLevel = 18,
            JobLevels = new Dictionary<uint, int>
            {
                [pictomancerJobId] = 18,
            },
        };

        DadRosterCharacterMerge.MergeNonAuthoritativeSnapshot(xadb, runtime);

        Assert.Equal(pictomancerJobId, xadb.CurrentJobId);
        Assert.Equal("PCT", xadb.CurrentJobAbbrev);
        Assert.Equal(18, xadb.CurrentLevel);
        Assert.False(xadb.JobLevels.ContainsKey(pictomancerJobId));
        Assert.Equal(40, xadb.JobLevels[WhiteMageJobId]);
        Assert.Equal(100, xadb.JobLevels[FisherJobId]);
    }

    [Fact]
    public void LocalMergeCurrentLevelUsesXadbJobDictionary()
    {
        var character = new DadAcquiredCharacter
        {
            CurrentJobId = WhiteMageJobId,
            CurrentLevel = 18,
            JobLevels = new Dictionary<uint, int>
            {
                [WhiteMageJobId] = 18,
            },
        };
        var xadbJobLevels = new Dictionary<uint, int>
        {
            [WhiteMageJobId] = 40,
        };

        foreach (var pair in xadbJobLevels)
            character.JobLevels[pair.Key] = pair.Value;
        character.CurrentLevel = DadRosterCharacterMerge.ResolveCurrentLevel(
            character.JobLevels,
            character.CurrentJobId,
            character.CurrentLevel);

        Assert.Equal(40, character.CurrentLevel);
    }

    private static DadRosterCharacter CreateXadbCharacter(int currentLevel)
        => new()
        {
            Source = DadCharacterSource.XadbOnly,
            CurrentJobId = WhiteMageJobId,
            CurrentJobAbbrev = "WHM",
            CurrentLevel = currentLevel,
            JobLevels = new Dictionary<uint, int>
            {
                [WhiteMageJobId] = 40,
                [33] = 90,
            },
            XadbReady = true,
        };
}
