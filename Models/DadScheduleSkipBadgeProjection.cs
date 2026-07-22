namespace dad.Models;

internal sealed record DadScheduleSkipBadge(
    string EntryId,
    int Count,
    string Label,
    string Tooltip);

internal sealed class DadScheduleSkipBadgeProjectionResult
{
    public string SelectedRunId { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, DadScheduleSkipBadge> Badges { get; init; } =
        new Dictionary<string, DadScheduleSkipBadge>(StringComparer.OrdinalIgnoreCase);
    public int TotalSkipCount { get; init; }
    public int RetainedRowDetailCount { get; init; }
    public string HistoryNotice { get; init; } = string.Empty;
}

internal static class DadScheduleSkipBadgeProjection
{
    public static DadScheduleSkipBadgeProjectionResult Build(
        DadScheduleDefinition? schedule,
        DadScheduleRunState? activeRun,
        IEnumerable<DadScheduleRunResult>? scheduleHistory,
        IEnumerable<DadScheduledCrewJobResult>? schedulerHistory)
    {
        var scheduleId = schedule?.ScheduleId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(scheduleId))
            return new DadScheduleSkipBadgeProjectionResult();

        var runResults = (scheduleHistory ?? [])
            .Where(result =>
                result != null &&
                !result.DryRun &&
                !string.IsNullOrWhiteSpace(result.RunId) &&
                string.Equals(result.ScheduleId?.Trim(), scheduleId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var resultsByRunId = runResults
            .GroupBy(static result => result.RunId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderByDescending(static result => result.CompletedAtUtc)
                    .ThenByDescending(static result => result.StartedAtUtc)
                    .First(),
                StringComparer.OrdinalIgnoreCase);

        var selectedRun = SelectRun(scheduleId, activeRun, runResults);
        if (selectedRun == null)
            return new DadScheduleSkipBadgeProjectionResult();

        var includedRunIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalSkipCount = 0;
        var current = selectedRun;
        while (current != null && includedRunIds.Add(current.RunId))
        {
            totalSkipCount += Math.Max(0, current.SkippedEntryExecutions);
            if (string.IsNullOrWhiteSpace(current.RetriedFromRunId) ||
                !resultsByRunId.TryGetValue(current.RetriedFromRunId, out var ancestor))
            {
                break;
            }

            current = ToRunEvidence(ancestor);
        }

        var retainedDetails = (schedulerHistory ?? [])
            .Where(result =>
                result != null &&
                result.FinalPhase == DadSchedulerPresetPhase.Skipped &&
                string.Equals(result.ScheduleId?.Trim(), scheduleId, StringComparison.OrdinalIgnoreCase) &&
                includedRunIds.Contains(result.ScheduleRunId?.Trim() ?? string.Empty) &&
                !string.IsNullOrWhiteSpace(result.ScheduleEntryId))
            .Select(result => new
            {
                Result = result,
                DetailKey = BuildDetailKey(result),
            })
            .Where(static detail => !string.IsNullOrWhiteSpace(detail.DetailKey))
            .GroupBy(static detail => detail.DetailKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static detail => detail.Result.CompletedAtUtc)
                .ThenByDescending(static detail => detail.Result.StartedAtUtc)
                .First()
                .Result)
            .ToList();

        var entryIds = (schedule?.Entries ?? [])
            .Where(static entry => entry != null && !string.IsNullOrWhiteSpace(entry.EntryId))
            .Select(static entry => entry.EntryId.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var badges = retainedDetails
            .Where(result => entryIds.Contains(result.ScheduleEntryId.Trim()))
            .GroupBy(result => result.ScheduleEntryId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group =>
                {
                    var ordered = group
                        .OrderBy(static result => result.ScheduleRepeatIteration)
                        .ThenBy(static result => result.CompletedAtUtc)
                        .ToList();
                    var summaries = ordered
                        .Select(static result => FirstNonEmpty(result.Summary, result.BlockedReason))
                        .Where(static summary => !string.IsNullOrWhiteSpace(summary))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    var count = ordered.Count;
                    return new DadScheduleSkipBadge(
                        group.Key,
                        count,
                        count == 1 ? "SKIPPED" : $"SKIPPED ×{count}",
                        string.Join(Environment.NewLine, summaries));
                },
                StringComparer.OrdinalIgnoreCase);
        var historyNotice = totalSkipCount > retainedDetails.Count
            ? $"{totalSkipCount} skips total; {retainedDetails.Count} row details retained"
            : string.Empty;

        return new DadScheduleSkipBadgeProjectionResult
        {
            SelectedRunId = selectedRun.RunId,
            Badges = badges,
            TotalSkipCount = totalSkipCount,
            RetainedRowDetailCount = retainedDetails.Count,
            HistoryNotice = historyNotice,
        };
    }

    private static RunEvidence? SelectRun(
        string scheduleId,
        DadScheduleRunState? activeRun,
        IReadOnlyList<DadScheduleRunResult> runResults)
    {
        if (activeRun is
            {
                IsActive: true,
                DryRun: false,
            } &&
            !string.IsNullOrWhiteSpace(activeRun.RunId) &&
            string.Equals(activeRun.ScheduleId?.Trim(), scheduleId, StringComparison.OrdinalIgnoreCase))
        {
            return new RunEvidence(
                activeRun.RunId.Trim(),
                activeRun.RetriedFromRunId?.Trim() ?? string.Empty,
                activeRun.SkippedEntryExecutions);
        }

        var latest = runResults
            .OrderByDescending(static result => result.CompletedAtUtc)
            .ThenByDescending(static result => result.StartedAtUtc)
            .FirstOrDefault();
        return latest == null ? null : ToRunEvidence(latest);
    }

    private static RunEvidence ToRunEvidence(DadScheduleRunResult result)
        => new(
            result.RunId.Trim(),
            result.RetriedFromRunId?.Trim() ?? string.Empty,
            result.SkippedEntryExecutions);

    private static string BuildDetailKey(DadScheduledCrewJobResult result)
    {
        var runId = result.ScheduleRunId?.Trim() ?? string.Empty;
        var entryId = result.ScheduleEntryId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(entryId))
            return string.Empty;

        if (result.ScheduleRepeatIteration > 0)
            return $"{runId}\u001f{entryId}\u001f{result.ScheduleRepeatIteration}";

        var jobId = result.JobId?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(jobId)
            ? string.Empty
            : $"{runId}\u001f{entryId}\u001fjob:{jobId}";
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private sealed record RunEvidence(
        string RunId,
        string RetriedFromRunId,
        int SkippedEntryExecutions);
}
