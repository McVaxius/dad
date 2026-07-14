using dad.Models;

namespace dad.Services;

public enum DadLevelSeekRowState
{
    Ignored = 0,
    Unknown = 1,
    BelowTarget = 2,
    Satisfied = 3,
}

public sealed record DadLevelSeekRowEvaluation(
    string SlotId,
    int TargetLevel,
    uint? JobId,
    int? KnownLevel,
    DadLevelSeekRowState State,
    string Summary);

public sealed class DadLevelSeekEvaluation
{
    public bool HasTargetedRows { get; init; }
    public bool ShouldSkip { get; init; }
    public IReadOnlyList<DadLevelSeekRowEvaluation> Rows { get; init; } = [];
    public string Summary { get; init; } = string.Empty;
}

/// <summary>
/// Pure evaluation of the exact, substitute-bound preset rows. The caller freezes this
/// result before any scheduler wake, launch, relog, or early requested-job assignment.
/// </summary>
public static class DadLevelSeekEvaluator
{
    public static DadLevelSeekEvaluation Evaluate(
        DadPlannerGroup effectiveGroup,
        DadCharacterPool pool)
    {
        ArgumentNullException.ThrowIfNull(effectiveGroup);
        ArgumentNullException.ThrowIfNull(pool);

        var rows = new List<DadLevelSeekRowEvaluation>();
        foreach (var slot in DadPlannerSlotRules.GetPrimaryRows(
                     DadPlannerSlotRules.NormalizeGroupSlots(effectiveGroup.Slots)))
        {
            if (!slot.LevelSeekTarget.HasValue || slot.LevelSeekTarget.Value <= 0)
                continue;

            // Empty editor rows are placeholders, not level requirements.
            if (slot.RequiredAccountKey.IsEmpty && slot.RequiredCharacterKey.IsEmpty)
                continue;

            var target = slot.LevelSeekTarget.Value;
            var matches = pool.Characters
                .Where(character => MatchesExactRow(character, slot))
                .ToList();
            if (matches.Count != 1)
            {
                rows.Add(new DadLevelSeekRowEvaluation(
                    slot.SlotId,
                    target,
                    slot.RequiredJobId,
                    null,
                    DadLevelSeekRowState.Unknown,
                    matches.Count == 0
                        ? $"{slot.SlotId} level is unknown because its exact character is absent."
                        : $"{slot.SlotId} level is unknown because its exact character is ambiguous."));
                continue;
            }

            var character = matches[0];
            var jobId = slot.RequiredJobId ?? character.CurrentJobId;
            var level = ResolveLevel(character, jobId);
            var state = !jobId.HasValue || !level.HasValue
                ? DadLevelSeekRowState.Unknown
                : level.Value < target
                    ? DadLevelSeekRowState.BelowTarget
                    : DadLevelSeekRowState.Satisfied;
            var summary = state switch
            {
                DadLevelSeekRowState.Satisfied => $"{slot.SlotId} job {jobId} is level {level}, meeting target {target}.",
                DadLevelSeekRowState.BelowTarget => $"{slot.SlotId} job {jobId} is level {level}, below target {target}.",
                _ => $"{slot.SlotId} required level is unknown for target {target}.",
            };
            rows.Add(new DadLevelSeekRowEvaluation(slot.SlotId, target, jobId, level, state, summary));
        }

        if (rows.Count == 0)
        {
            return new DadLevelSeekEvaluation
            {
                HasTargetedRows = false,
                ShouldSkip = false,
                Rows = rows,
                Summary = "No effective preset row has an enabled level target; run unconditionally.",
            };
        }

        var shouldSkip = rows.All(static row => row.State == DadLevelSeekRowState.Satisfied);
        return new DadLevelSeekEvaluation
        {
            HasTargetedRows = true,
            ShouldSkip = shouldSkip,
            Rows = rows,
            Summary = shouldSkip
                ? $"All {rows.Count} targeted preset row(s) already meet their level targets."
                : "At least one targeted preset row is below target or has an unknown required level.",
        };
    }

    private static bool MatchesExactRow(DadAcquiredCharacter character, DadPlannerGroupSlot slot)
    {
        if (!slot.RequiredCharacterKey.IsEmpty &&
            !string.Equals(character.CharacterKey, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (slot.RequiredAccountKey.IsEmpty)
            return !slot.RequiredCharacterKey.IsEmpty;

        return string.Equals(character.AccountId?.Trim(), slot.RequiredAccountKey.Value, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(character.AccountAlias?.Trim(), slot.RequiredAccountKey.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static int? ResolveLevel(DadAcquiredCharacter character, uint? jobId)
    {
        if (!jobId.HasValue || jobId.Value == 0)
            return null;

        if (character.JobLevels.TryGetValue(jobId.Value, out var level) && level > 0)
            return level;

        return character.CurrentJobId == jobId && character.CurrentLevel is > 0
            ? character.CurrentLevel
            : null;
    }
}
