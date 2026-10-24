using System.Text.Json;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadTrustProgressTests
{
    // All account, character and content identities in this fixture are fabricated.
    private static readonly DadAccountKey Account = new("fixture-account");
    private static readonly DadCharacterKey Character = new("Fixture One@Example");
    private static readonly DateTime ReadAt = new(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LegacyRecordsHaveUnknownProgressAndBothSaveFormatsPreserveObservedUnknowns()
    {
        Assert.Null(JsonSerializer.Deserialize<DadRosterKnownCharacterRecord>("{}")!.TrustProgress);
        Assert.Null(Newtonsoft.Json.JsonConvert.DeserializeObject<DadRosterKnownCharacterRecord>("{}")!.TrustProgress);
        var record = Record();
        record.TrustProgress!.Npcs.Add(new() { NpcId = DadTrustNpcId.Ryne, Level = 80,
            LevelCap = 80, Unlocked = false, ObservedAtUtc = ReadAt });
        record.TrustProgress.Duties.Add(new() { ContentFinderConditionId = 2, Unlocked = null, Members = null, ObservedAtUtc = ReadAt });
        record.TrustProgress.Duties.Add(new() { ContentFinderConditionId = 3, Unlocked = false, Members = [], ObservedAtUtc = ReadAt });

        var stj = JsonSerializer.Deserialize<DadRosterKnownCharacterRecord>(JsonSerializer.Serialize(record))!;
        var jsonNet = Newtonsoft.Json.JsonConvert.DeserializeObject<DadRosterKnownCharacterRecord>(
            Newtonsoft.Json.JsonConvert.SerializeObject(record))!;

        foreach (var restored in new[] { stj, jsonNet })
        {
            Assert.True(DadTrustProgressRules.Same(record.TrustProgress, restored.TrustProgress));
            Assert.False(restored.TrustProgress!.Npcs[1].Unlocked);
            Assert.Null(restored.TrustProgress.Duties[1].Members);
            Assert.Empty(restored.TrustProgress.Duties[2].Members!);
        }
    }

    [Fact]
    public void EveryRosterCloneOwnsItsNpcAndDungeonLists()
    {
        var record = Record();
        var clone = record.Clone();
        clone.TrustProgress!.Npcs[0].Level = 100;
        clone.TrustProgress.Duties[0].Members![0].AvailableRoles = DadTrustRoles.All;
        Assert.Equal(87, record.TrustProgress!.Npcs[0].Level);
        Assert.Equal(DadTrustRoles.Healer, record.TrustProgress.Duties[0].Members![0].AvailableRoles);

        var roster = new DadRosterCharacter { TrustProgress = record.TrustProgress };
        roster.Clone().TrustProgress!.Npcs.Clear();
        var acquired = new DadAcquiredCharacter { TrustProgress = record.TrustProgress };
        acquired.Clone().TrustProgress!.Duties.Clear();
        Assert.Single(record.TrustProgress.Npcs);
        Assert.Single(record.TrustProgress.Duties);
    }

    [Fact]
    public void ProgressCannotCrossAccountsOrContentIdsOrUseUnresolvedOwners()
    {
        var progress = Record().TrustProgress;
        Assert.Null(DadTrustProgressRules.ForOwner(progress, new("other-account"), Character, 1001));
        Assert.Null(DadTrustProgressRules.ForOwner(progress, Account, Character, 1002));
        Assert.Null(DadTrustProgressRules.ForOwner(progress, Account, Character, 0));
        Assert.Null(DadTrustProgressRules.ForOwner(progress, new(""), Character, 1001));
        Assert.Null(DadTrustProgressRules.ForOwner(progress, Account, new(""), 1001));

        // A verified same CID survives a character rename inside the same account.
        var renamed = new DadCharacterKey("Renamed Fixture@Example");
        Assert.Equal(renamed, DadTrustProgressRules.ForOwner(progress, Account, renamed, 1001)!.CharacterKey);
    }

    [Fact]
    public void ObservationReadTimesOverrideTheContainingRecordsSnapshotTimes()
    {
        var first = Record();
        first.TrustProgress!.Npcs[0].ObservedAtUtc = ReadAt.AddMinutes(5);
        first.TrustProgress.Npcs[0].Level = 88;
        first.UpdatedAtUtc = ReadAt;
        var laterXadb = Record();
        laterXadb.UpdatedAtUtc = ReadAt.AddHours(1);
        laterXadb.TrustProgress!.Npcs[0].Level = 87;
        laterXadb.TrustProgress.Duties[0].ObservedAtUtc = ReadAt.AddMinutes(10);
        laterXadb.TrustProgress.Duties[0].Unlocked = false;

        var merged = DadRosterKnownCharacterLedger.MergeStoredRecords(first, laterXadb);

        Assert.Equal(88, merged.TrustProgress!.Npcs[0].Level);
        Assert.False(merged.TrustProgress.Duties[0].Unlocked);
        Assert.Equal(ReadAt.AddMinutes(5), merged.TrustProgress.Npcs[0].ObservedAtUtc);
        Assert.Equal(87, laterXadb.TrustProgress.Npcs[0].Level);
    }

    [Fact]
    public void MissingXadbAndHeartbeatProgressCannotEraseKnownProgress()
    {
        var record = Record();
        var missing = Record();
        missing.TrustProgress = null;
        missing.UpdatedAtUtc = ReadAt.AddHours(1);
        Assert.True(DadTrustProgressRules.Same(record.TrustProgress,
            DadRosterKnownCharacterLedger.MergeStoredRecords(record, missing).TrustProgress));

        var target = new DadRosterCharacter { AccountKey = Account, CharacterKey = Character, ContentId = 1001,
            TrustProgress = record.TrustProgress, JobLevels = new() { [19] = 87 } };
        var incoming = new DadRosterCharacter { AccountKey = Account, CharacterKey = Character, ContentId = 1001,
            Source = DadCharacterSource.PeerRuntime, JobLevels = new() { [19] = 88 } };
        DadRosterCharacterMerge.ApplyAuthoritativeXadbSnapshot(target, incoming);
        DadRosterCharacterMerge.MergeNonAuthoritativeSnapshot(target, incoming);
        Assert.True(DadTrustProgressRules.Same(record.TrustProgress, target.TrustProgress));
        incoming.TrustProgress = Record().TrustProgress;
        incoming.TrustProgress!.AccountKey = new("different-account");
        DadRosterCharacterMerge.MergeNonAuthoritativeSnapshot(target, incoming);
        Assert.Equal(Account, target.TrustProgress!.AccountKey);
    }

    [Fact]
    public void InvalidLevelsAndMembershipRemainUnknownAndCannotGrantEligibility()
    {
        var progress = Record().TrustProgress!;
        progress.Npcs[0].Level = 0;
        progress.Npcs[0].LevelCap = -1;
        progress.Npcs[0].Unlocked = null;
        progress.Duties[0].Members!.Add(progress.Duties[0].Members![0] with { });

        var normalized = DadTrustProgressRules.ForOwner(progress, Account, Character, 1001)!;

        Assert.Null(normalized.Npcs[0].Level);
        Assert.Null(normalized.Npcs[0].LevelCap);
        Assert.Null(normalized.Npcs[0].Unlocked);
        Assert.Null(normalized.Duties[0].Members);
        progress.Npcs[0].Level = 100;
        progress.Npcs[0].LevelCap = 80;
        Assert.Null(DadTrustProgressRules.ForOwner(progress, Account, Character, 1001)!.Npcs[0].LevelCap);
    }

    [Fact]
    public void ConflictingSameReadNpcValuesAreNotResolvedByListOrder()
    {
        var progress = Record().TrustProgress!;
        progress.RosterObservedAtUtc = ReadAt;
        progress.Npcs.Add(progress.Npcs[0] with { Level = 100 });
        var normalized = DadTrustProgressRules.ForOwner(progress, Account, Character, 1001)!;
        Assert.Empty(normalized.Npcs);
        Assert.Null(normalized.RosterObservedAtUtc);
        Assert.Single(normalized.Duties);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-30, true)]
    [InlineData(-61, false)]
    [InlineData(1, false)]
    public void FreshnessUsesEachObservationAndDoesNotAcceptFutureTimes(int seconds, bool fresh)
        => Assert.Equal(fresh, DadTrustProgressRules.IsFresh(ReadAt.AddSeconds(seconds), ReadAt, TimeSpan.FromSeconds(60)));

    [Fact]
    public void NewTrustReadIsDurableAndNormalizationIsIdempotent()
    {
        var record = Record();
        var changed = record.Clone();
        changed.TrustProgress!.Npcs[0].ObservedAtUtc = ReadAt.AddSeconds(1);
        Assert.False(DadRosterKnownCharacterLedger.DurableKnowledgeEquals(record, changed));
        var records = new List<DadRosterKnownCharacterRecord> { changed };
        DadRosterKnownCharacterLedger.Normalize(records);
        Assert.False(DadRosterKnownCharacterLedger.Normalize(records));
        records[0].TrustProgress!.AccountKey = new("wrong-owner");
        Assert.True(DadRosterKnownCharacterLedger.Normalize(records));
        Assert.Null(records[0].TrustProgress);
    }

    private static DadRosterKnownCharacterRecord Record() => new()
    {
        AccountKey = Account, CharacterKey = Character.Value, ContentId = 1001, UpdatedAtUtc = ReadAt,
        TrustProgress = new()
        {
            AccountKey = Account, CharacterKey = Character, ContentId = 1001,
            Npcs = [new() { NpcId = DadTrustNpcId.Alphinaud, DisplayName = "Alphinaud", Level = 87,
                LevelCap = 100, Unlocked = true, ObservedAtUtc = ReadAt }],
            Duties = [new() { ContentFinderConditionId = 1, Unlocked = true, ObservedAtUtc = ReadAt,
                Members = [new() { NpcId = DadTrustNpcId.Alphinaud, AvailableRoles = DadTrustRoles.Healer }] }],
        },
    };
}
