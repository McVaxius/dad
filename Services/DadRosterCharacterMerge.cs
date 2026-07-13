using dad.Models;

namespace dad.Services;

internal static class DadRosterCharacterMerge
{
    public static void NormalizeXadbSnapshot(DadRosterCharacter character)
        => NormalizeSnapshotJobLedger(character);

    public static void NormalizeSnapshotJobLedger(DadRosterCharacter character)
    {
        // Normalization is allowed to learn, never forget. Preserve every already-stored entry verbatim,
        // including unknown/future IDs and non-positive legacy values; positive validation is a caller concern.
        var normalizedJobLevels = character.JobLevels == null
            ? []
            : new Dictionary<uint, int>(character.JobLevels);
        if (character.CurrentJobId.HasValue && character.CurrentLevel.HasValue)
            LearnJobLevel(normalizedJobLevels, character.CurrentJobId.Value, character.CurrentLevel.Value);
        character.JobLevels = normalizedJobLevels;
        character.CurrentJobId = ResolveCurrentJobId(
            character.JobLevels,
            character.CurrentJobId);
        character.CurrentLevel = ResolveCurrentLevel(
            character.JobLevels,
            character.CurrentJobId,
            character.CurrentLevel);
    }

    public static void MergeJobLedger(
        Dictionary<uint, int> target,
        IReadOnlyDictionary<uint, int>? incoming,
        uint? currentJobId = null,
        int? currentLevel = null)
    {
        if (incoming != null)
        {
            foreach (var pair in incoming)
                LearnJobLevel(target, pair.Key, pair.Value);
        }

        if (currentJobId.HasValue && currentLevel.HasValue)
            LearnJobLevel(target, currentJobId.Value, currentLevel.Value);
    }

    public static void RecordReportedJobLevel(Dictionary<uint, int> target, uint jobId, int level)
    {
        // Raw owner catalogs can contain future IDs and legacy/non-positive values. Preserve the
        // maximum value reported for every ID before any combat-job presentation filtering occurs.
        if (!target.TryGetValue(jobId, out var knownLevel) || level > knownLevel)
            target[jobId] = level;
    }

    public static uint? ResolveCurrentJobId(
        IReadOnlyDictionary<uint, int> jobLevels,
        uint? currentJobId)
    {
        if (currentJobId is > 0)
            return currentJobId;

        uint? soleCombatJobId = null;
        foreach (var pair in jobLevels)
        {
            if (pair.Value <= 0 || !IsCombatJob(pair.Key))
                continue;

            if (soleCombatJobId.HasValue)
                return null;

            soleCombatJobId = pair.Key;
        }

        return soleCombatJobId;
    }

    public static int? ResolveCurrentLevel(
        IReadOnlyDictionary<uint, int> jobLevels,
        uint? currentJobId,
        int? currentLevel)
    {
        var resolvedCurrentJobId = ResolveCurrentJobId(jobLevels, currentJobId);
        return resolvedCurrentJobId.HasValue &&
               jobLevels.TryGetValue(resolvedCurrentJobId.Value, out var jobLevel) &&
               jobLevel > 0
            ? jobLevel
            : currentLevel;
    }

    public static bool IsCombatJob(uint classJobId)
        => classJobId is >= 1 and <= 7 or >= 19 and <= 42;

    public static void ApplyAuthoritativeXadbSnapshot(
        DadRosterCharacter target,
        DadRosterCharacter incoming)
    {
        NormalizeXadbSnapshot(incoming);
        target.JobLevels ??= [];
        MergeJobLedger(
            target.JobLevels,
            incoming.JobLevels,
            incoming.CurrentJobId,
            incoming.CurrentLevel);
        target.LastSnapshotUtc = incoming.LastSnapshotUtc;
        target.CurrentJobId = incoming.CurrentJobId;
        target.CurrentJobAbbrev = incoming.CurrentJobAbbrev;
        target.CurrentLevel = ResolveCurrentLevel(
            target.JobLevels,
            target.CurrentJobId,
            incoming.CurrentLevel);
        target.SnapshotQuality = incoming.SnapshotQuality;
        target.SnapshotVersion = incoming.SnapshotVersion;
        target.XadbReady = incoming.XadbReady;
    }

    public static void MergeNonAuthoritativeSnapshot(
        DadRosterCharacter target,
        DadRosterCharacter incoming)
    {
        target.JobLevels ??= [];
        incoming.JobLevels ??= [];
        var runtimeSource = IsRuntimeSource(incoming.Source);
        var applyRuntimeCurrentFields = runtimeSource &&
                                        ShouldReplaceObservation(
                                            MaxDate(incoming.LastRuntimeSeenUtc, incoming.LastSnapshotUtc),
                                            target.LastRuntimeSeenUtc,
                                            target.LastSnapshotUtc);
        var preserveExistingXadbObservations =
            runtimeSource &&
            HasCompleteXadbJobData(target);
        target.XadbReady |= incoming.XadbReady;
        target.IsCurrent |= incoming.IsCurrent;
        target.LastRuntimeSeenUtc = MaxDate(target.LastRuntimeSeenUtc, incoming.LastRuntimeSeenUtc);
        MergeJobLedger(
            target.JobLevels,
            incoming.JobLevels,
            incoming.CurrentJobId,
            incoming.CurrentLevel);

        if (applyRuntimeCurrentFields)
            ApplyRuntimeCurrentFields(target, incoming);

        if (preserveExistingXadbObservations)
        {
            return;
        }

        target.LastSnapshotUtc = MaxDate(target.LastSnapshotUtc, incoming.LastSnapshotUtc);
        if (!runtimeSource)
        {
            target.CurrentJobId = ResolveCurrentJobId(target.JobLevels, target.CurrentJobId ?? incoming.CurrentJobId);
            if (string.IsNullOrWhiteSpace(target.CurrentJobAbbrev))
                target.CurrentJobAbbrev = incoming.CurrentJobAbbrev;
            target.CurrentLevel = ResolveCurrentLevel(
                target.JobLevels,
                target.CurrentJobId,
                target.CurrentLevel ?? incoming.CurrentLevel);
        }

        if (string.IsNullOrWhiteSpace(target.SnapshotQuality))
            target.SnapshotQuality = incoming.SnapshotQuality;
        target.SnapshotVersion ??= incoming.SnapshotVersion;
    }

    private static void ApplyRuntimeCurrentFields(DadRosterCharacter target, DadRosterCharacter incoming)
    {
        if (incoming.CurrentJobId is > 0)
            target.CurrentJobId = incoming.CurrentJobId;
        else
            target.CurrentJobId = ResolveCurrentJobId(target.JobLevels, target.CurrentJobId);

        if (!string.IsNullOrWhiteSpace(incoming.CurrentJobAbbrev))
            target.CurrentJobAbbrev = incoming.CurrentJobAbbrev;

        var fallbackCurrentLevel = incoming.CurrentJobId.HasValue || incoming.CurrentLevel.HasValue
            ? incoming.CurrentLevel
            : target.CurrentLevel;
        target.CurrentLevel = ResolveCurrentLevel(
            target.JobLevels,
            target.CurrentJobId,
            fallbackCurrentLevel);
    }

    private static void LearnJobLevel(Dictionary<uint, int> ledger, uint jobId, int level)
    {
        if (jobId == 0 || level <= 0)
            return;

        if (!ledger.TryGetValue(jobId, out var knownLevel) || level > knownLevel)
            ledger[jobId] = level;
    }

    private static bool HasCompleteXadbJobData(DadRosterCharacter character)
        => character.XadbReady && character.JobLevels.Count > 0;

    private static bool IsRuntimeSource(DadCharacterSource source)
        => source is DadCharacterSource.LocalRuntime or DadCharacterSource.PeerRuntime;

    public static bool ShouldReplaceMutableObservation(
        DadRosterCharacter target,
        DadRosterCharacter incoming,
        bool xadbAuthoritative)
    {
        var incomingUtc = xadbAuthoritative
            ? incoming.LastSnapshotUtc
            : IsRuntimeSource(incoming.Source)
                ? MaxDate(incoming.LastRuntimeSeenUtc, incoming.LastSnapshotUtc)
                : MaxDate(incoming.LastSnapshotUtc, incoming.LastRuntimeSeenUtc);
        return ShouldReplaceObservation(
            incomingUtc,
            target.LastRuntimeSeenUtc,
            target.LastSnapshotUtc);
    }

    private static bool ShouldReplaceObservation(
        DateTime? incomingUtc,
        DateTime? currentRuntimeUtc,
        DateTime? currentSnapshotUtc)
    {
        var currentUtc = MaxDate(currentRuntimeUtc, currentSnapshotUtc);
        if (!currentUtc.HasValue)
            return true;
        return incomingUtc.HasValue && incomingUtc.Value >= currentUtc.Value;
    }

    private static DateTime? MaxDate(DateTime? left, DateTime? right)
    {
        if (!left.HasValue)
            return right;
        if (!right.HasValue)
            return left;
        return left.Value >= right.Value ? left : right;
    }
}
