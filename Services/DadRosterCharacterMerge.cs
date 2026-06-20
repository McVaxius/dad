using dad.Models;

namespace dad.Services;

internal static class DadRosterCharacterMerge
{
    public static void NormalizeXadbSnapshot(DadRosterCharacter character)
    {
        character.JobLevels ??= [];
        character.CurrentJobId = ResolveCurrentJobId(
            character.JobLevels,
            character.CurrentJobId);
        character.CurrentLevel = ResolveCurrentLevel(
            character.JobLevels,
            character.CurrentJobId,
            character.CurrentLevel);
    }

    public static uint? ResolveCurrentJobId(
        IReadOnlyDictionary<uint, int> jobLevels,
        uint? currentJobId)
    {
        if (currentJobId.HasValue)
            return currentJobId;

        uint? soleCombatJobId = null;
        foreach (var pair in jobLevels)
        {
            if (!IsCombatJob(pair.Key))
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
        return resolvedCurrentJobId.HasValue && jobLevels.TryGetValue(resolvedCurrentJobId.Value, out var jobLevel)
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
        target.LastSnapshotUtc = incoming.LastSnapshotUtc;
        target.JobLevels = new Dictionary<uint, int>(incoming.JobLevels);
        target.CurrentJobId = incoming.CurrentJobId;
        target.CurrentJobAbbrev = incoming.CurrentJobAbbrev;
        target.CurrentLevel = incoming.CurrentLevel;
        target.SnapshotQuality = incoming.SnapshotQuality;
        target.SnapshotVersion = incoming.SnapshotVersion;
        target.XadbReady = incoming.XadbReady;
    }

    public static void MergeNonAuthoritativeSnapshot(
        DadRosterCharacter target,
        DadRosterCharacter incoming)
    {
        var preserveExistingXadbJobs =
            IsRuntimeSource(incoming.Source) &&
            HasCompleteXadbJobData(target);
        target.XadbReady |= incoming.XadbReady;
        target.IsCurrent |= incoming.IsCurrent;
        target.LastRuntimeSeenUtc = MaxDate(target.LastRuntimeSeenUtc, incoming.LastRuntimeSeenUtc);
        if (preserveExistingXadbJobs)
        {
            ApplyRuntimeCurrentFields(target, incoming);
            return;
        }

        target.LastSnapshotUtc = MaxDate(target.LastSnapshotUtc, incoming.LastSnapshotUtc);
        foreach (var pair in incoming.JobLevels)
            target.JobLevels[pair.Key] = pair.Value;
        target.CurrentJobId = ResolveCurrentJobId(target.JobLevels, target.CurrentJobId ?? incoming.CurrentJobId);
        if (string.IsNullOrWhiteSpace(target.CurrentJobAbbrev))
            target.CurrentJobAbbrev = incoming.CurrentJobAbbrev;
        target.CurrentLevel = ResolveCurrentLevel(
            target.JobLevels,
            target.CurrentJobId,
            target.CurrentLevel ?? incoming.CurrentLevel);
        if (string.IsNullOrWhiteSpace(target.SnapshotQuality))
            target.SnapshotQuality = incoming.SnapshotQuality;
        target.SnapshotVersion ??= incoming.SnapshotVersion;
    }

    private static void ApplyRuntimeCurrentFields(DadRosterCharacter target, DadRosterCharacter incoming)
    {
        if (incoming.CurrentJobId.HasValue)
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

    private static bool HasCompleteXadbJobData(DadRosterCharacter character)
        => character.XadbReady && character.JobLevels.Count > 0;

    private static bool IsRuntimeSource(DadCharacterSource source)
        => source is DadCharacterSource.LocalRuntime or DadCharacterSource.PeerRuntime;

    private static DateTime? MaxDate(DateTime? left, DateTime? right)
    {
        if (!left.HasValue)
            return right;
        if (!right.HasValue)
            return left;
        return left.Value >= right.Value ? left : right;
    }
}
