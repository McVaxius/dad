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

public sealed record DadLevelSeekDisplayState(bool IsSkipIndicated, string Tooltip)
{
    public static DadLevelSeekDisplayState None { get; } = new(false, string.Empty);
}

public static class DadLevelSeekDisplayRules
{
    public static DadLevelSeekDisplayState Build(DadLevelSeekEvaluation? evaluation)
    {
        if (evaluation == null)
            return DadLevelSeekDisplayState.None;

        var evidence = evaluation.Rows
            .Select(static row => row.Summary)
            .Where(static summary => !string.IsNullOrWhiteSpace(summary));
        var tooltip = string.Join(Environment.NewLine, new[] { evaluation.Summary }.Concat(evidence));
        return new DadLevelSeekDisplayState(evaluation.ShouldSkip, tooltip);
    }
}

public enum DadResolvedLevelTargetState
{
    Unknown = 0,
    BelowTarget = 1,
    Satisfied = 2,
}

public sealed record DadResolvedLevelTargetEvidence(
    DadResolvedLevelTarget Target,
    uint? ObservedJobId,
    int? ObservedLevel,
    DadResolvedLevelTargetState State,
    string Summary);

public sealed class DadResolvedLevelTargetEvaluation
{
    public bool HasTargets { get; init; }
    public bool AllSatisfied { get; init; }
    public IReadOnlyList<DadResolvedLevelTargetEvidence> Evidence { get; init; } = [];
    public string Summary { get; init; } = string.Empty;

    public string DescribeEvidence()
        => string.Join(
            " ",
            new[] { Summary }.Concat(Evidence.Select(static evidence => evidence.Summary)));

    public DadLevelSeekEvaluation ToLevelSeekEvaluation()
        => new()
        {
            HasTargetedRows = HasTargets,
            ShouldSkip = AllSatisfied,
            Rows = Evidence.Select(static evidence => new DadLevelSeekRowEvaluation(
                    string.IsNullOrWhiteSpace(evidence.Target.CharacterLabel)
                        ? evidence.Target.CharacterKey.Value
                        : evidence.Target.CharacterLabel,
                    evidence.Target.TargetLevel,
                    evidence.ObservedJobId ?? evidence.Target.JobId,
                    evidence.ObservedLevel,
                    evidence.State switch
                    {
                        DadResolvedLevelTargetState.Satisfied => DadLevelSeekRowState.Satisfied,
                        DadResolvedLevelTargetState.BelowTarget => DadLevelSeekRowState.BelowTarget,
                        _ => DadLevelSeekRowState.Unknown,
                    },
                    evidence.Summary))
                .ToList(),
            Summary = Summary,
        };
}

public static class DadResolvedLevelTargetRules
{
    public static DadRunStopPolicy ResolvePolicy(
        DadRunStopPolicy? source,
        IReadOnlyList<DadPresetCharacterSlot> selectedSlots,
        IReadOnlyList<DadAcquiredCharacter> availableCharacters)
    {
        var policy = (source ?? new DadRunStopPolicy()).Clone().Normalize();
        policy.ResolvedLevelTargets = [];
        if (policy.Mode != DadPlannerStopMode.TargetLevel)
            return policy;

        var firstSelectedIndex = -1;
        for (var index = 0; index < selectedSlots.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(selectedSlots[index].CharacterKey))
                continue;

            firstSelectedIndex = index;
            break;
        }

        if (firstSelectedIndex < 0)
            return policy;

        var firstSelected = selectedSlots[firstSelectedIndex];

        policy.TargetCharacterKey = new DadCharacterKey(firstSelected.CharacterKey);
        policy.TargetCharacterLabel = ResolveLabel(firstSelected.CharacterKey, availableCharacters);

        if (!selectedSlots.Any(static slot =>
                !string.IsNullOrWhiteSpace(slot.CharacterKey) &&
                slot.LevelSeekTarget.HasValue))
            return policy;

        for (var index = 0; index < selectedSlots.Count; index++)
        {
            var slot = selectedSlots[index];
            if (string.IsNullOrWhiteSpace(slot.CharacterKey))
                continue;

            var targetLevel = index == firstSelectedIndex
                ? slot.LevelSeekTarget ?? policy.TargetLevel
                : slot.LevelSeekTarget;
            if (!targetLevel.HasValue)
                continue;

            policy.ResolvedLevelTargets.Add(new DadResolvedLevelTarget
            {
                CharacterKey = new DadCharacterKey(slot.CharacterKey),
                CharacterLabel = ResolveLabel(slot.CharacterKey, availableCharacters),
                JobId = slot.RequiredJobId,
                TargetLevel = Math.Clamp(targetLevel.Value, 1, 999),
            });
        }

        return policy.Normalize();
    }

    public static DadResolvedLevelTargetEvaluation Evaluate(
        DadRunStopPolicy? source,
        DadCharacterPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var policy = (source ?? new DadRunStopPolicy()).Clone().Normalize();
        if (policy.Mode != DadPlannerStopMode.TargetLevel || policy.ResolvedLevelTargets.Count == 0)
        {
            return new DadResolvedLevelTargetEvaluation
            {
                HasTargets = false,
                AllSatisfied = false,
                Summary = "No resolved row level targets are active.",
            };
        }

        var evidence = policy.ResolvedLevelTargets
            .Select(target => EvaluateTarget(target, pool))
            .ToList();
        var satisfied = evidence.Count(static item =>
            item.State == DadResolvedLevelTargetState.Satisfied);
        var below = evidence.Count(static item =>
            item.State == DadResolvedLevelTargetState.BelowTarget);
        var unknown = evidence.Count - satisfied - below;
        var allSatisfied = evidence.Count > 0 && satisfied == evidence.Count;
        return new DadResolvedLevelTargetEvaluation
        {
            HasTargets = true,
            AllSatisfied = allSatisfied,
            Evidence = evidence,
            Summary = allSatisfied
                ? $"All {evidence.Count} resolved level target(s) are proven satisfied."
                : $"Resolved level targets: {satisfied}/{evidence.Count} satisfied, {below} below target, {unknown} unknown.",
        };
    }

    private static DadResolvedLevelTargetEvidence EvaluateTarget(
        DadResolvedLevelTarget target,
        DadCharacterPool pool)
    {
        var matches = pool.Characters
            .Where(character => string.Equals(
                character.CharacterKey,
                target.CharacterKey.Value,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        var label = string.IsNullOrWhiteSpace(target.CharacterLabel)
            ? target.CharacterKey.Value
            : target.CharacterLabel;
        if (target.CharacterKey.IsEmpty || matches.Count != 1)
        {
            return new DadResolvedLevelTargetEvidence(
                target.Clone(),
                target.JobId,
                null,
                DadResolvedLevelTargetState.Unknown,
                matches.Count > 1
                    ? $"{label}: exact character evidence is ambiguous for target {target.TargetLevel}."
                    : $"{label}: exact character evidence is absent for target {target.TargetLevel}.");
        }

        var character = matches[0];
        uint? observedJobId;
        int? observedLevel;
        string evidenceKind;
        if (target.JobId.HasValue)
        {
            observedJobId = target.JobId;
            observedLevel = character.JobLevels != null &&
                            character.JobLevels.TryGetValue(target.JobId.Value, out var ledgerLevel) &&
                            ledgerLevel > 1
                ? ledgerLevel
                : null;
            evidenceKind = $"job {target.JobId.Value} ledger";
        }
        else
        {
            observedJobId = character.IsLiveConnected && character.CurrentJobId is > 0
                ? character.CurrentJobId
                : null;
            observedLevel = observedJobId.HasValue && character.CurrentLevel is > 1
                ? character.CurrentLevel
                : null;
            evidenceKind = observedJobId.HasValue
                ? $"live Any job {observedJobId.Value}"
                : "live Any job";
        }

        var state = !observedLevel.HasValue
            ? DadResolvedLevelTargetState.Unknown
            : observedLevel.Value >= target.TargetLevel
                ? DadResolvedLevelTargetState.Satisfied
                : DadResolvedLevelTargetState.BelowTarget;
        var knownLevel = observedLevel.GetValueOrDefault();
        var summary = state switch
        {
            DadResolvedLevelTargetState.Satisfied =>
                $"{label}: {evidenceKind} level {knownLevel} meets target {target.TargetLevel}.",
            DadResolvedLevelTargetState.BelowTarget =>
                $"{label}: {evidenceKind} level {knownLevel} is below target {target.TargetLevel}.",
            _ =>
                $"{label}: {evidenceKind} level is unknown for target {target.TargetLevel}.",
        };
        return new DadResolvedLevelTargetEvidence(
            target.Clone(),
            observedJobId,
            observedLevel,
            state,
            summary);
    }

    private static string ResolveLabel(
        string characterKey,
        IReadOnlyList<DadAcquiredCharacter> availableCharacters)
    {
        var character = availableCharacters.FirstOrDefault(candidate =>
            string.Equals(candidate.CharacterKey, characterKey, StringComparison.OrdinalIgnoreCase));
        if (character == null ||
            string.IsNullOrWhiteSpace(character.CharacterName) ||
            string.IsNullOrWhiteSpace(character.WorldName))
        {
            return characterKey;
        }

        return $"{character.CharacterName}@{character.WorldName}";
    }
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
