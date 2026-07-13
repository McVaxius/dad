using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRosterKnownCharacterLedgerTests
{
    [Fact]
    public void StartupNormalizationPreservesEveryStoredJobAndLearnsCurrentJob()
    {
        var records = new List<DadRosterKnownCharacterRecord>
        {
            Record(
                "account-a",
                "Alpha@World",
                100,
                new Dictionary<uint, int>
                {
                    [0] = 0,
                    [8] = 100,
                    [18] = 90,
                    [36] = 80,
                    [999] = 77,
                },
                currentJobId: 32,
                currentLevel: 95),
        };

        var changed = DadRosterKnownCharacterLedger.Normalize(records);

        Assert.True(changed);
        Assert.Equal(6, records[0].JobLevels.Count);
        Assert.Equal(0, records[0].JobLevels[0]);
        Assert.Equal(100, records[0].JobLevels[8]);
        Assert.Equal(90, records[0].JobLevels[18]);
        Assert.Equal(80, records[0].JobLevels[36]);
        Assert.Equal(77, records[0].JobLevels[999]);
        Assert.Equal(95, records[0].JobLevels[32]);
    }

    [Fact]
    public void DuplicateExactIdentityUnionsMaximumLevelsAndKeepsNewestMetadata()
    {
        var older = Record(
            "account-a",
            "Alpha@World",
            100,
            new Dictionary<uint, int> { [19] = 90, [32] = 95 });
        older.CharacterName = "Old Alpha";
        older.UpdatedAtUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = Record(
            "account-a",
            "Alpha@World",
            100,
            new Dictionary<uint, int> { [19] = 80, [24] = 100 });
        newer.CharacterName = "New Alpha";
        newer.UpdatedAtUtc = new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc);
        var records = new List<DadRosterKnownCharacterRecord> { older, newer };

        Assert.True(DadRosterKnownCharacterLedger.Normalize(records));

        var merged = Assert.Single(records);
        Assert.Equal("New Alpha", merged.CharacterName);
        Assert.Equal(3, merged.JobLevels.Count);
        Assert.Equal(90, merged.JobLevels[19]);
        Assert.Equal(95, merged.JobLevels[32]);
        Assert.Equal(100, merged.JobLevels[24]);
    }

    [Fact]
    public void NormalizedLedgerIsIdempotent()
    {
        var records = new List<DadRosterKnownCharacterRecord>
        {
            Record("account-a", "Alpha@World", 100, new Dictionary<uint, int> { [32] = 95 }),
            Record("account-a", "Alpha@World", 100, new Dictionary<uint, int> { [24] = 100 }),
        };

        Assert.True(DadRosterKnownCharacterLedger.Normalize(records));
        var first = records[0].Clone();

        Assert.False(DadRosterKnownCharacterLedger.Normalize(records));
        Assert.True(DadRosterKnownCharacterLedger.PayloadEquals(first, records[0]));
    }

    [Fact]
    public void HeartbeatOnlyObservationDoesNotCountAsDurableKnowledgeChange()
    {
        var stored = Record(
            "account-a",
            "Alpha@World",
            100,
            new Dictionary<uint, int> { [32] = 95 });
        stored.LastRuntimeSeenUtc = new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc);
        var heartbeat = stored.Clone();
        heartbeat.LastRuntimeSeenUtc = new DateTime(2026, 7, 13, 0, 1, 0, DateTimeKind.Utc);
        heartbeat.UpdatedAtUtc = new DateTime(2026, 7, 13, 0, 1, 0, DateTimeKind.Utc);

        Assert.True(DadRosterKnownCharacterLedger.DurableKnowledgeEquals(stored, heartbeat));

        heartbeat.JobLevels[32] = 96;
        Assert.False(DadRosterKnownCharacterLedger.DurableKnowledgeEquals(stored, heartbeat));
    }

    [Fact]
    public void DurablyIdempotentBatchRestoresTimestampButKeepsNewestHeartbeat()
    {
        var baselineRecord = Record(
            "account-a",
            "Alpha@World",
            100,
            new Dictionary<uint, int> { [32] = 95 });
        baselineRecord.UpdatedAtUtc = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
        baselineRecord.LastRuntimeSeenUtc = baselineRecord.UpdatedAtUtc;
        var baseline = new List<DadRosterKnownCharacterRecord> { baselineRecord };
        var currentRecord = baselineRecord.Clone();
        currentRecord.UpdatedAtUtc = baselineRecord.UpdatedAtUtc.AddMinutes(5);
        currentRecord.LastRuntimeSeenUtc = baselineRecord.LastRuntimeSeenUtc.Value.AddMinutes(5);
        var expectedHeartbeatUtc = currentRecord.LastRuntimeSeenUtc;
        var current = new List<DadRosterKnownCharacterRecord> { currentRecord };

        Assert.True(DadRosterKnownCharacterLedger.DurableLedgerEquals(baseline, current));

        DadRosterKnownCharacterLedger.RestoreTransientStateWhenDurablyEqual(baseline, current);

        Assert.Equal(baselineRecord.UpdatedAtUtc, current[0].UpdatedAtUtc);
        Assert.Equal(expectedHeartbeatUtc, current[0].LastRuntimeSeenUtc);

        current[0].JobLevels[32] = 96;
        Assert.False(DadRosterKnownCharacterLedger.DurableLedgerEquals(baseline, current));
    }

    [Fact]
    public void AccountAndCharacterBoundariesNeverContributeJobs()
    {
        var records = new List<DadRosterKnownCharacterRecord>
        {
            Record("account-a", "Alpha@World", 100, new Dictionary<uint, int> { [32] = 95 }),
            Record("account-b", "Alpha@World", 100, new Dictionary<uint, int> { [24] = 100 }),
            Record("account-a", "Beta@World", 200, new Dictionary<uint, int> { [19] = 90 }),
            Record("account-a", "Alpha@World", 300, new Dictionary<uint, int> { [21] = 80 }),
        };

        Assert.False(DadRosterKnownCharacterLedger.Normalize(records));

        Assert.Equal(4, records.Count);
        Assert.Single(records[0].JobLevels);
        Assert.Single(records[1].JobLevels);
        Assert.Single(records[2].JobLevels);
        Assert.Single(records[3].JobLevels);
    }

    [Fact]
    public void MissingDurableIdentityRowsArePreservedButNeverMerged()
    {
        var records = new List<DadRosterKnownCharacterRecord>
        {
            Record(string.Empty, "Alpha@World", 100, new Dictionary<uint, int> { [32] = 95 }),
            Record(string.Empty, "Alpha@World", 100, new Dictionary<uint, int> { [24] = 100 }),
            Record("account-a", string.Empty, 0, new Dictionary<uint, int> { [19] = 90 }),
            Record("account-a", string.Empty, 0, new Dictionary<uint, int> { [21] = 80 }),
        };

        Assert.False(DadRosterKnownCharacterLedger.Normalize(records));

        Assert.Equal(4, records.Count);
        Assert.Equal(95, records[0].JobLevels[32]);
        Assert.Equal(100, records[1].JobLevels[24]);
        Assert.Equal(90, records[2].JobLevels[19]);
        Assert.Equal(80, records[3].JobLevels[21]);
    }

    [Fact]
    public void SparseLowerObservationCannotLowerOrRemoveStoredJobs()
    {
        var full = Record(
            "account-a",
            "Alpha@World",
            100,
            new Dictionary<uint, int> { [8] = 100, [18] = 90, [32] = 95, [999] = 77 });
        full.UpdatedAtUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var sparse = Record(
            "account-a",
            "Alpha@World",
            100,
            new Dictionary<uint, int> { [32] = 80 });
        sparse.UpdatedAtUtc = new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc);

        var merged = DadRosterKnownCharacterLedger.MergeStoredRecords(full, sparse);

        Assert.Equal(4, merged.JobLevels.Count);
        Assert.Equal(100, merged.JobLevels[8]);
        Assert.Equal(90, merged.JobLevels[18]);
        Assert.Equal(95, merged.JobLevels[32]);
        Assert.Equal(77, merged.JobLevels[999]);
    }

    [Fact]
    public void JsonRoundTripPreservesCompleteLedgerIncludingUnknownAndNonCombatJobs()
    {
        var configuration = new DadRosterCatalogConfiguration
        {
            KnownCharacters =
            [
                Record(
                    "account-a",
                    "Alpha@World",
                    100,
                    new Dictionary<uint, int>
                    {
                        [8] = 100,
                        [18] = 90,
                        [36] = 80,
                        [999] = 77,
                    }),
            ],
        };

        var json = DadIpcJson.Serialize(configuration);
        var restored = Assert.IsType<DadRosterCatalogConfiguration>(
            DadIpcJson.Deserialize<DadRosterCatalogConfiguration>(json));

        var character = Assert.Single(restored.KnownCharacters);
        Assert.Equal(configuration.KnownCharacters[0].JobLevels, character.JobLevels);
        Assert.False(DadRosterKnownCharacterLedger.Normalize(restored.KnownCharacters));
    }

    [Fact]
    public void RetargetedAccountMergeUnionsBeforeSourceRowsDisappear()
    {
        var target = Record("target", "Alpha@World", 100, new Dictionary<uint, int> { [32] = 95 });
        var source = Record("source", "Alpha@World", 100, new Dictionary<uint, int> { [24] = 100 });
        source.AccountKey = new DadAccountKey("target");
        var records = new List<DadRosterKnownCharacterRecord> { target, source };

        Assert.True(DadRosterKnownCharacterLedger.Normalize(records));

        var merged = Assert.Single(records);
        Assert.Equal(95, merged.JobLevels[32]);
        Assert.Equal(100, merged.JobLevels[24]);
    }

    private static DadRosterKnownCharacterRecord Record(
        string account,
        string characterKey,
        ulong contentId,
        Dictionary<uint, int> jobs,
        uint? currentJobId = null,
        int? currentLevel = null)
        => new()
        {
            AccountKey = new DadAccountKey(account),
            CharacterKey = characterKey,
            ContentId = contentId,
            JobLevels = jobs,
            CurrentJobId = currentJobId,
            CurrentLevel = currentLevel,
            UpdatedAtUtc = new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc),
        };
}
