namespace dad.Models;

public static class DadScheduleCursorFormatter
{
    public static string Format(
        DadScheduleRunState activeRun,
        IEnumerable<DadScheduleDefinition>? schedules)
    {
        ArgumentNullException.ThrowIfNull(activeRun);

        var schedule = (schedules ?? []).FirstOrDefault(candidate =>
            string.Equals(
                candidate.ScheduleId?.Trim(),
                activeRun.ScheduleId?.Trim(),
                StringComparison.OrdinalIgnoreCase));
        var entries = schedule?.Entries ?? [];
        var exactEntryIndex = string.IsNullOrWhiteSpace(activeRun.CurrentEntryId)
            ? -1
            : entries.FindIndex(entry =>
                string.Equals(
                    entry.EntryId?.Trim(),
                    activeRun.CurrentEntryId.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        var resolvedEntryIndex = exactEntryIndex >= 0
            ? exactEntryIndex
            : activeRun.CurrentEntryIndex >= 0 && activeRun.CurrentEntryIndex < entries.Count
                ? activeRun.CurrentEntryIndex
                : -1;
        var resolvedEntry = resolvedEntryIndex >= 0 ? entries[resolvedEntryIndex] : null;

        var scheduleName = FirstText(
            activeRun.ScheduleName,
            schedule?.DisplayName,
            activeRun.ScheduleId,
            "Schedule");
        var presetName = FirstText(
            resolvedEntry?.PresetName,
            activeRun.CurrentPresetName,
            resolvedEntry?.GroupId,
            activeRun.CurrentGroupId,
            "Preset");

        var entryPosition = exactEntryIndex >= 0
            ? exactEntryIndex + 1
            : activeRun.CurrentEntryIndex >= 0
                ? activeRun.CurrentEntryIndex + 1
                : 0;
        var entryTotal = schedule != null && entries.Count > 0 && resolvedEntryIndex >= 0
            ? entries.Count
            : 0;
        var repeatIteration = activeRun.RepeatIteration > 0 ? activeRun.RepeatIteration : 0;
        var repeatTotal = resolvedEntry is { RepeatCount: > 0 } ? resolvedEntry.RepeatCount : 0;

        return $"{scheduleName} — {presetName} | entry {FormatCursor(entryPosition, entryTotal)} | repeat {FormatCursor(repeatIteration, repeatTotal)}";
    }

    private static string FormatCursor(int position, int total)
    {
        var current = position > 0 ? position.ToString() : "?";
        return total > 0 ? $"{current}/{total}" : current;
    }

    private static string FirstText(params string?[] candidates)
        => candidates.FirstOrDefault(static candidate => !string.IsNullOrWhiteSpace(candidate))?.Trim()
           ?? string.Empty;
}
