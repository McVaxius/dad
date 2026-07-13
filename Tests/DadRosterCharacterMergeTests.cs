using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRosterCharacterMergeTests
{
    private const uint FisherJobId = 18;
    private const uint PaladinJobId = 19;
    private const uint WhiteMageJobId = 24;
    private const uint DarkKnightJobId = 32;
    private const uint PictomancerJobId = 42;

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
    public void MergeJobLedgerUnionsValidJobsAtTheirMaximumAndLearnsCurrentJob()
    {
        var ledger = new Dictionary<uint, int>
        {
            [WhiteMageJobId] = 90,
        };
        var incoming = new Dictionary<uint, int>
        {
            [0] = 100,
            [WhiteMageJobId] = 80,
            [DarkKnightJobId] = 0,
            [PaladinJobId] = 50,
        };

        DadRosterCharacterMerge.MergeJobLedger(
            ledger,
            incoming,
            PictomancerJobId,
            25);

        Assert.Equal(3, ledger.Count);
        Assert.Equal(90, ledger[WhiteMageJobId]);
        Assert.Equal(50, ledger[PaladinJobId]);
        Assert.Equal(25, ledger[PictomancerJobId]);
        Assert.False(ledger.ContainsKey(0));
        Assert.False(ledger.ContainsKey(DarkKnightJobId));
    }

    [Fact]
    public void AuthoritativeXadbSnapshotUnionsPartialJobsAndReplacesObservationFields()
    {
        var cachedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var freshAt = new DateTime(2026, 6, 19, 0, 0, 0, DateTimeKind.Utc);
        var cached = new DadRosterCharacter
        {
            AccountKey = new DadAccountKey("account-1"),
            LastSnapshotUtc = cachedAt,
            JobLevels = new Dictionary<uint, int>
            {
                [WhiteMageJobId] = 80,
                [PaladinJobId] = 90,
            },
            CurrentJobId = WhiteMageJobId,
            CurrentJobAbbrev = "WHM",
            CurrentLevel = 80,
            SnapshotQuality = "cached",
            SnapshotVersion = 1,
            XadbReady = true,
        };
        var fresh = new DadRosterCharacter
        {
            Source = DadCharacterSource.XadbOnly,
            CurrentJobId = DarkKnightJobId,
            CurrentJobAbbrev = "DRK",
            CurrentLevel = 45,
            JobLevels = new Dictionary<uint, int>
            {
                [WhiteMageJobId] = 40,
            },
            XadbReady = false,
        };
        fresh.LastSnapshotUtc = freshAt;
        fresh.SnapshotQuality = "partial";
        fresh.SnapshotVersion = 7;

        DadRosterCharacterMerge.ApplyAuthoritativeXadbSnapshot(cached, fresh);

        Assert.Equal("account-1", cached.AccountKey.ToString());
        Assert.Equal(freshAt, cached.LastSnapshotUtc);
        Assert.Equal(DarkKnightJobId, cached.CurrentJobId);
        Assert.Equal(45, cached.CurrentLevel);
        Assert.Equal("DRK", cached.CurrentJobAbbrev);
        Assert.Equal(7, cached.SnapshotVersion);
        Assert.Equal("partial", cached.SnapshotQuality);
        Assert.False(cached.XadbReady);
        Assert.Equal(3, cached.JobLevels.Count);
        Assert.Equal(80, cached.JobLevels[WhiteMageJobId]);
        Assert.Equal(90, cached.JobLevels[PaladinJobId]);
        Assert.Equal(45, cached.JobLevels[DarkKnightJobId]);
    }

    [Fact]
    public void AuthoritativeEmptyJobRefreshRetainsEveryLearnedJob()
    {
        var cached = CreateXadbCharacter(currentLevel: 40);
        cached.JobLevels[PaladinJobId] = 90;
        var freshAt = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
        var empty = new DadRosterCharacter
        {
            Source = DadCharacterSource.XadbOnly,
            LastSnapshotUtc = freshAt,
            JobLevels = [],
            CurrentJobId = null,
            CurrentJobAbbrev = string.Empty,
            CurrentLevel = null,
            SnapshotQuality = "empty",
            SnapshotVersion = 8,
            XadbReady = true,
        };

        DadRosterCharacterMerge.ApplyAuthoritativeXadbSnapshot(cached, empty);

        Assert.Equal(3, cached.JobLevels.Count);
        Assert.Equal(40, cached.JobLevels[WhiteMageJobId]);
        Assert.Equal(90, cached.JobLevels[PaladinJobId]);
        Assert.Equal(90, cached.JobLevels[33]);
        Assert.Null(cached.CurrentJobId);
        Assert.Null(cached.CurrentLevel);
        Assert.Equal(string.Empty, cached.CurrentJobAbbrev);
        Assert.Equal(freshAt, cached.LastSnapshotUtc);
        Assert.Equal("empty", cached.SnapshotQuality);
        Assert.Equal(8, cached.SnapshotVersion);
    }

    [Fact]
    public void RepeatXadbRefreshKeepsNormalizedLevel()
    {
        var cached = CreateXadbCharacter(currentLevel: 18);
        var refresh = CreateXadbCharacter(currentLevel: 18);
        DadRosterCharacterMerge.ApplyAuthoritativeXadbSnapshot(cached, refresh.Clone());
        var firstLedger = new Dictionary<uint, int>(cached.JobLevels);

        DadRosterCharacterMerge.ApplyAuthoritativeXadbSnapshot(cached, refresh.Clone());

        Assert.Equal(40, cached.CurrentLevel);
        Assert.Equal(40, cached.JobLevels[WhiteMageJobId]);
        Assert.Equal(firstLedger.Count, cached.JobLevels.Count);
        Assert.All(firstLedger, pair => Assert.Equal(pair.Value, cached.JobLevels[pair.Key]));
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
    public void RuntimeOverlayLearnsCurrentJobWhenMissingFromXadbJobs()
    {
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
            CurrentJobId = PictomancerJobId,
            CurrentJobAbbrev = "PCT",
            CurrentLevel = 18,
            JobLevels = [],
        };

        DadRosterCharacterMerge.MergeNonAuthoritativeSnapshot(xadb, runtime);

        Assert.Equal(PictomancerJobId, xadb.CurrentJobId);
        Assert.Equal("PCT", xadb.CurrentJobAbbrev);
        Assert.Equal(18, xadb.CurrentLevel);
        Assert.Equal(18, xadb.JobLevels[PictomancerJobId]);
        Assert.Equal(40, xadb.JobLevels[WhiteMageJobId]);
        Assert.Equal(100, xadb.JobLevels[FisherJobId]);
    }

    [Fact]
    public void RuntimeOverlayCannotLowerKnownCurrentJobLevel()
    {
        var cached = CreateXadbCharacter(currentLevel: 40);
        var runtime = new DadRosterCharacter
        {
            Source = DadCharacterSource.PeerRuntime,
            CurrentJobId = WhiteMageJobId,
            CurrentJobAbbrev = "WHM",
            CurrentLevel = 20,
            JobLevels = new Dictionary<uint, int>
            {
                [WhiteMageJobId] = 20,
            },
        };

        DadRosterCharacterMerge.MergeNonAuthoritativeSnapshot(cached, runtime);

        Assert.Equal(40, cached.JobLevels[WhiteMageJobId]);
        Assert.Equal(40, cached.CurrentLevel);
    }

    [Fact]
    public void LocalRuntimeAndXadbMergeKeepsTheMaximumKnownLevel()
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

        DadRosterCharacterMerge.MergeJobLedger(
            character.JobLevels,
            xadbJobLevels,
            character.CurrentJobId,
            character.CurrentLevel);
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
