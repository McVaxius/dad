using dad.Models;

namespace dad.Services;

internal static class DadRosterKnownCharacterLedger
{
    public static bool Normalize(List<DadRosterKnownCharacterRecord> records)
    {
        records ??= [];
        var normalized = new List<DadRosterKnownCharacterRecord>(records.Count);
        foreach (var source in records)
        {
            if (source == null)
                continue;

            var candidate = CloneForLedger(source);
            LearnCurrentJob(candidate.JobLevels, candidate.CurrentJobId, candidate.CurrentLevel);

            var existingIndex = HasDurableIdentity(candidate)
                ? normalized.FindIndex(existing =>
                    HasDurableIdentity(existing) &&
                    DadRosterIdentity.SameAccount(existing.AccountKey, candidate.AccountKey) &&
                    DadRosterIdentity.SameCharacter(
                        new DadCharacterKey(existing.CharacterKey),
                        existing.ContentId,
                        new DadCharacterKey(candidate.CharacterKey),
                        candidate.ContentId))
                : -1;
            if (existingIndex < 0)
            {
                normalized.Add(candidate);
                continue;
            }

            normalized[existingIndex] = MergeStoredRecords(normalized[existingIndex], candidate);
        }

        var changed = records.Count != normalized.Count ||
                      records.Where(static record => record != null)
                          .Zip(normalized)
                          .Any(pair => !PayloadEquals(pair.First, pair.Second));
        if (!changed)
            return false;

        records.Clear();
        records.AddRange(normalized);
        return true;
    }

    public static DadRosterKnownCharacterRecord MergeStoredRecords(
        DadRosterKnownCharacterRecord left,
        DadRosterKnownCharacterRecord right)
    {
        var leftCopy = CloneForLedger(left);
        var rightCopy = CloneForLedger(right);
        var newest = ObservationUtc(rightCopy) >= ObservationUtc(leftCopy)
            ? rightCopy
            : leftCopy;
        var older = ReferenceEquals(newest, rightCopy) ? leftCopy : rightCopy;
        var merged = CloneForLedger(newest);

        UnionStoredJobs(merged.JobLevels, older.JobLevels);
        LearnCurrentJob(merged.JobLevels, leftCopy.CurrentJobId, leftCopy.CurrentLevel);
        LearnCurrentJob(merged.JobLevels, rightCopy.CurrentJobId, rightCopy.CurrentLevel);

        if (merged.AccountKey.IsEmpty)
            merged.AccountKey = older.AccountKey;
        if (string.IsNullOrWhiteSpace(merged.AccountAlias))
            merged.AccountAlias = older.AccountAlias;
        if (string.IsNullOrWhiteSpace(merged.CharacterKey))
            merged.CharacterKey = older.CharacterKey;
        if (merged.ContentId == 0)
            merged.ContentId = older.ContentId;
        if (string.IsNullOrWhiteSpace(merged.CharacterName))
            merged.CharacterName = older.CharacterName;
        merged.WorldId ??= older.WorldId;
        if (string.IsNullOrWhiteSpace(merged.WorldName))
            merged.WorldName = older.WorldName;
        merged.DataCenterId ??= older.DataCenterId;
        if (string.IsNullOrWhiteSpace(merged.DataCenterName))
            merged.DataCenterName = older.DataCenterName;
        merged.LastSnapshotUtc = MaxDate(leftCopy.LastSnapshotUtc, rightCopy.LastSnapshotUtc);
        merged.LastRuntimeSeenUtc = MaxDate(leftCopy.LastRuntimeSeenUtc, rightCopy.LastRuntimeSeenUtc);
        if (string.IsNullOrWhiteSpace(merged.CurrentJobAbbrev))
            merged.CurrentJobAbbrev = older.CurrentJobAbbrev;
        if (string.IsNullOrWhiteSpace(merged.SnapshotQuality))
            merged.SnapshotQuality = older.SnapshotQuality;
        merged.SnapshotVersion ??= older.SnapshotVersion;
        merged.MapEligible ??= older.MapEligible;
        if (string.IsNullOrWhiteSpace(merged.MapEligibilitySummary))
            merged.MapEligibilitySummary = older.MapEligibilitySummary;
        merged.UpdatedAtUtc = leftCopy.UpdatedAtUtc >= rightCopy.UpdatedAtUtc
            ? leftCopy.UpdatedAtUtc
            : rightCopy.UpdatedAtUtc;
        return merged;
    }

    public static bool HasDurableIdentity(DadRosterKnownCharacterRecord record)
        => record != null &&
           !record.AccountKey.IsEmpty &&
           (record.ContentId != 0 || !string.IsNullOrWhiteSpace(record.CharacterKey));

    public static bool PayloadEquals(
        DadRosterKnownCharacterRecord left,
        DadRosterKnownCharacterRecord right)
        => DadRosterIdentity.SameAccount(left.AccountKey, right.AccountKey)
           && string.Equals(left.AccountAlias, right.AccountAlias, StringComparison.Ordinal)
           && string.Equals(left.CharacterKey, right.CharacterKey, StringComparison.Ordinal)
           && left.ContentId == right.ContentId
           && string.Equals(left.CharacterName, right.CharacterName, StringComparison.Ordinal)
           && left.WorldId == right.WorldId
           && string.Equals(left.WorldName, right.WorldName, StringComparison.Ordinal)
           && left.DataCenterId == right.DataCenterId
           && string.Equals(left.DataCenterName, right.DataCenterName, StringComparison.Ordinal)
           && left.LastSnapshotUtc == right.LastSnapshotUtc
           && left.LastRuntimeSeenUtc == right.LastRuntimeSeenUtc
           && left.CurrentJobId == right.CurrentJobId
           && string.Equals(left.CurrentJobAbbrev, right.CurrentJobAbbrev, StringComparison.Ordinal)
           && left.CurrentLevel == right.CurrentLevel
           && string.Equals(left.SnapshotQuality, right.SnapshotQuality, StringComparison.Ordinal)
           && left.SnapshotVersion == right.SnapshotVersion
           && left.XadbReady == right.XadbReady
           && left.MapEligible == right.MapEligible
           && string.Equals(left.MapEligibilitySummary, right.MapEligibilitySummary, StringComparison.Ordinal)
           && left.UpdatedAtUtc == right.UpdatedAtUtc
           && (left.JobLevels == null) == (right.JobLevels == null)
           && DictionariesEqual(left.JobLevels, right.JobLevels);

    public static bool DurableKnowledgeEquals(
        DadRosterKnownCharacterRecord left,
        DadRosterKnownCharacterRecord right)
        => DadRosterIdentity.SameAccount(left.AccountKey, right.AccountKey)
           && string.Equals(left.AccountAlias, right.AccountAlias, StringComparison.Ordinal)
           && string.Equals(left.CharacterKey, right.CharacterKey, StringComparison.Ordinal)
           && left.ContentId == right.ContentId
           && string.Equals(left.CharacterName, right.CharacterName, StringComparison.Ordinal)
           && left.WorldId == right.WorldId
           && string.Equals(left.WorldName, right.WorldName, StringComparison.Ordinal)
           && left.DataCenterId == right.DataCenterId
           && string.Equals(left.DataCenterName, right.DataCenterName, StringComparison.Ordinal)
           && left.LastSnapshotUtc == right.LastSnapshotUtc
           && left.CurrentJobId == right.CurrentJobId
           && string.Equals(left.CurrentJobAbbrev, right.CurrentJobAbbrev, StringComparison.Ordinal)
           && left.CurrentLevel == right.CurrentLevel
           && string.Equals(left.SnapshotQuality, right.SnapshotQuality, StringComparison.Ordinal)
           && left.SnapshotVersion == right.SnapshotVersion
           && left.XadbReady == right.XadbReady
           && left.MapEligible == right.MapEligible
           && string.Equals(left.MapEligibilitySummary, right.MapEligibilitySummary, StringComparison.Ordinal)
           && DictionariesEqual(left.JobLevels, right.JobLevels);

    public static List<DadRosterKnownCharacterRecord> CloneLedger(
        IReadOnlyList<DadRosterKnownCharacterRecord> records)
        => records.Select(CloneForLedger).ToList();

    public static bool DurableLedgerEquals(
        IReadOnlyList<DadRosterKnownCharacterRecord> left,
        IReadOnlyList<DadRosterKnownCharacterRecord> right)
        => left.Count == right.Count &&
           left.Zip(right).All(pair => DurableKnowledgeEquals(pair.First, pair.Second));

    public static void RestoreTransientStateWhenDurablyEqual(
        IReadOnlyList<DadRosterKnownCharacterRecord> baseline,
        List<DadRosterKnownCharacterRecord> current)
    {
        if (!DurableLedgerEquals(baseline, current))
            return;

        for (var index = 0; index < current.Count; index++)
        {
            current[index].UpdatedAtUtc = baseline[index].UpdatedAtUtc;
            current[index].LastRuntimeSeenUtc = MaxDate(
                baseline[index].LastRuntimeSeenUtc,
                current[index].LastRuntimeSeenUtc);
        }
    }

    private static DadRosterKnownCharacterRecord CloneForLedger(DadRosterKnownCharacterRecord source)
        => new()
        {
            AccountKey = source.AccountKey,
            AccountAlias = source.AccountAlias ?? string.Empty,
            CharacterKey = source.CharacterKey ?? string.Empty,
            ContentId = source.ContentId,
            CharacterName = source.CharacterName ?? string.Empty,
            WorldId = source.WorldId,
            WorldName = source.WorldName ?? string.Empty,
            DataCenterId = source.DataCenterId,
            DataCenterName = source.DataCenterName ?? string.Empty,
            LastSnapshotUtc = source.LastSnapshotUtc,
            LastRuntimeSeenUtc = source.LastRuntimeSeenUtc,
            JobLevels = source.JobLevels == null
                ? []
                : new Dictionary<uint, int>(source.JobLevels),
            CurrentJobId = source.CurrentJobId,
            CurrentJobAbbrev = source.CurrentJobAbbrev ?? string.Empty,
            CurrentLevel = source.CurrentLevel,
            SnapshotQuality = source.SnapshotQuality ?? string.Empty,
            SnapshotVersion = source.SnapshotVersion,
            XadbReady = source.XadbReady,
            MapEligible = source.MapEligible,
            MapEligibilitySummary = source.MapEligibilitySummary ?? string.Empty,
            UpdatedAtUtc = source.UpdatedAtUtc,
        };

    private static void UnionStoredJobs(
        Dictionary<uint, int> target,
        IReadOnlyDictionary<uint, int>? incoming)
    {
        if (incoming == null)
            return;

        foreach (var pair in incoming)
        {
            if (!target.TryGetValue(pair.Key, out var knownLevel) || pair.Value > knownLevel)
                target[pair.Key] = pair.Value;
        }
    }

    private static void LearnCurrentJob(
        Dictionary<uint, int> target,
        uint? currentJobId,
        int? currentLevel)
    {
        if (currentJobId is not > 0 || currentLevel is not > 0)
            return;

        if (!target.TryGetValue(currentJobId.Value, out var knownLevel) || currentLevel.Value > knownLevel)
            target[currentJobId.Value] = currentLevel.Value;
    }

    private static DateTime ObservationUtc(DadRosterKnownCharacterRecord record)
    {
        var observed = record.UpdatedAtUtc;
        if (record.LastSnapshotUtc is { } snapshotUtc && snapshotUtc > observed)
            observed = snapshotUtc;
        if (record.LastRuntimeSeenUtc is { } runtimeUtc && runtimeUtc > observed)
            observed = runtimeUtc;
        return observed;
    }

    private static DateTime? MaxDate(DateTime? left, DateTime? right)
    {
        if (!left.HasValue)
            return right;
        if (!right.HasValue)
            return left;
        return left.Value >= right.Value ? left : right;
    }

    private static bool DictionariesEqual(
        IReadOnlyDictionary<uint, int>? left,
        IReadOnlyDictionary<uint, int>? right)
    {
        left ??= new Dictionary<uint, int>();
        right ??= new Dictionary<uint, int>();
        return left.Count == right.Count &&
               left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }
}
