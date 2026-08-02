using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadScheduleSkipBadgeProjectionTests
{
    [Fact]
    public void ActiveRunWinsOverLatestTerminalRun()
    {
        var schedule = Schedule("schedule-a", "entry-active", "entry-old");
        var projection = DadScheduleSkipBadgeProjection.Build(
            schedule,
            ActiveRun("schedule-a", "run-active", skipped: 1),
            [Run("schedule-a", "run-old", skipped: 1, completedAt: Utc(12))],
            [
                Skip("schedule-a", "run-old", "entry-old", 1, "old result", Utc(12)),
                Skip("schedule-a", "run-active", "entry-active", 1, "active result", Utc(13)),
            ]);

        Assert.Equal("run-active", projection.SelectedRunId);
        var badge = Assert.Single(projection.Badges);
        Assert.Equal("entry-active", badge.Key);
        Assert.Equal("SKIPPED", badge.Value.Label);
        Assert.Equal("active result", badge.Value.Tooltip);
    }

    [Fact]
    public void LatestNonDryRunReplacesOlderBadgesAndExcludesDryRuns()
    {
        var schedule = Schedule("schedule-a", "entry-old", "entry-latest", "entry-dry");
        var projection = DadScheduleSkipBadgeProjection.Build(
            schedule,
            ActiveRun("schedule-a", "run-dry-active", skipped: 1, dryRun: true),
            [
                Run("schedule-a", "run-dry", skipped: 1, completedAt: Utc(15), dryRun: true),
                Run("schedule-a", "run-latest", skipped: 1, completedAt: Utc(14)),
                Run("schedule-a", "run-old", skipped: 1, completedAt: Utc(13)),
            ],
            [
                Skip("schedule-a", "run-dry-active", "entry-dry", 1, "active dry", Utc(16)),
                Skip("schedule-a", "run-dry", "entry-dry", 1, "terminal dry", Utc(15)),
                Skip("schedule-a", "run-latest", "entry-latest", 1, "latest live", Utc(14)),
                Skip("schedule-a", "run-old", "entry-old", 1, "older live", Utc(13)),
            ]);

        Assert.Equal("run-latest", projection.SelectedRunId);
        var badge = Assert.Single(projection.Badges);
        Assert.Equal("entry-latest", badge.Key);
        Assert.Equal("latest live", badge.Value.Tooltip);
    }

    [Fact]
    public void ExactScheduleRunEntryAndSkippedPhaseAreRequired()
    {
        var schedule = Schedule("schedule-a", "entry-a");
        var projection = DadScheduleSkipBadgeProjection.Build(
            schedule,
            null,
            [Run("schedule-a", "run-a", skipped: 1, completedAt: Utc(12))],
            [
                Skip("schedule-b", "run-a", "entry-a", 1, "wrong schedule", Utc(12)),
                Skip("schedule-a", "run-b", "entry-a", 1, "wrong run", Utc(12)),
                Skip("schedule-a", "run-a", "entry-b", 1, "wrong entry", Utc(12)),
                Result("schedule-a", "run-a", "entry-a", 1, DadSchedulerPresetPhase.Completed, "not skipped", Utc(12)),
                Skip("schedule-a", "run-a", "entry-a", 1, "exact", Utc(12)),
            ]);

        var badge = Assert.Single(projection.Badges).Value;
        Assert.Equal(1, badge.Count);
        Assert.Equal("exact", badge.Tooltip);
    }

    [Fact]
    public void AvailableRetryLineageContributesExactAncestorSkips()
    {
        var schedule = Schedule("schedule-a", "entry-before-retry", "entry-after-retry");
        var projection = DadScheduleSkipBadgeProjection.Build(
            schedule,
            null,
            [
                Run("schedule-a", "run-retry", skipped: 2, completedAt: Utc(14), retriedFrom: "run-original"),
                Run("schedule-a", "run-original", skipped: 1, completedAt: Utc(13)),
            ],
            [
                Skip("schedule-a", "run-original", "entry-before-retry", 1, "before retry", Utc(13)),
                Skip("schedule-a", "run-retry", "entry-after-retry", 1, "after retry", Utc(14)),
            ]);

        Assert.Equal(2, projection.TotalSkipCount);
        Assert.Equal(2, projection.RetainedRowDetailCount);
        Assert.Equal("before retry", projection.Badges["entry-before-retry"].Tooltip);
        Assert.Equal("after retry", projection.Badges["entry-after-retry"].Tooltip);
    }

    [Fact]
    public void RepeatIterationsAggregateIntoOneBadge()
    {
        var schedule = Schedule("schedule-a", "entry-a");
        var projection = DadScheduleSkipBadgeProjection.Build(
            schedule,
            null,
            [Run("schedule-a", "run-a", skipped: 3, completedAt: Utc(15))],
            [
                Skip("schedule-a", "run-a", "entry-a", 1, "repeat one", Utc(13)),
                Skip("schedule-a", "run-a", "entry-a", 2, "repeat two", Utc(14)),
                Skip("schedule-a", "run-a", "entry-a", 3, "repeat three", Utc(15)),
            ]);

        var badge = Assert.Single(projection.Badges).Value;
        Assert.Equal(3, badge.Count);
        Assert.Equal("SKIPPED ×3", badge.Label);
        Assert.Equal(
            string.Join(Environment.NewLine, "repeat one", "repeat two", "repeat three"),
            badge.Tooltip);
        Assert.Equal(string.Empty, projection.HistoryNotice);
    }

    [Fact]
    public void DuplicateRepeatIterationsAreSuppressedUsingLatestResultSummary()
    {
        var schedule = Schedule("schedule-a", "entry-a");
        var projection = DadScheduleSkipBadgeProjection.Build(
            schedule,
            null,
            [Run("schedule-a", "run-a", skipped: 1, completedAt: Utc(15))],
            [
                Skip("schedule-a", "run-a", "entry-a", 1, "older duplicate", Utc(14), jobId: "job-old"),
                Skip("schedule-a", "run-a", "entry-a", 1, "newer duplicate", Utc(15), jobId: "job-new"),
            ]);

        var badge = Assert.Single(projection.Badges).Value;
        Assert.Equal(1, badge.Count);
        Assert.Equal("SKIPPED", badge.Label);
        Assert.Equal("newer duplicate", badge.Tooltip);
        Assert.Equal(1, projection.RetainedRowDetailCount);
    }

    [Fact]
    public void AggregateCounterDisclosesPrunedRowDetailsWithoutGuessingBadges()
    {
        var schedule = Schedule("schedule-a", "entry-a", "entry-b", "entry-c", "entry-d");
        var projection = DadScheduleSkipBadgeProjection.Build(
            schedule,
            null,
            [Run("schedule-a", "run-a", skipped: 4, completedAt: Utc(15))],
            [
                Skip("schedule-a", "run-a", "entry-a", 1, "retained a", Utc(14)),
                Skip("schedule-a", "run-a", "entry-b", 1, "retained b", Utc(15)),
            ]);

        Assert.Equal(4, projection.TotalSkipCount);
        Assert.Equal(2, projection.RetainedRowDetailCount);
        Assert.Equal(2, projection.Badges.Count);
        Assert.Equal("4 skips total; 2 row details retained", projection.HistoryNotice);
        Assert.False(projection.Badges.ContainsKey("entry-c"));
        Assert.False(projection.Badges.ContainsKey("entry-d"));
    }

    private static DadScheduleDefinition Schedule(string scheduleId, params string[] entryIds)
        => new()
        {
            ScheduleId = scheduleId,
            Entries = entryIds.Select((entryId, index) => new DadScheduleEntry
            {
                EntryId = entryId,
                GroupId = $"group-{index}",
                PresetName = $"Preset {index}",
            }).ToList(),
        };

    private static DadScheduleRunState ActiveRun(
        string scheduleId,
        string runId,
        int skipped,
        bool dryRun = false)
        => new()
        {
            ScheduleId = scheduleId,
            RunId = runId,
            Status = DadScheduleRunStatus.Running,
            Phase = DadScheduleRunPhase.WaitingForScheduler,
            DryRun = dryRun,
            SkippedEntryExecutions = skipped,
        };

    private static DadScheduleRunResult Run(
        string scheduleId,
        string runId,
        int skipped,
        DateTime completedAt,
        bool dryRun = false,
        string retriedFrom = "")
        => new()
        {
            ScheduleId = scheduleId,
            RunId = runId,
            Status = DadScheduleRunStatus.Completed,
            Success = true,
            DryRun = dryRun,
            StartedAtUtc = completedAt.AddMinutes(-1),
            CompletedAtUtc = completedAt,
            SkippedEntryExecutions = skipped,
            RetriedFromRunId = retriedFrom,
        };

    private static DadScheduledCrewJobResult Skip(
        string scheduleId,
        string runId,
        string entryId,
        int repeat,
        string summary,
        DateTime completedAt,
        string? jobId = null)
        => Result(
            scheduleId,
            runId,
            entryId,
            repeat,
            DadSchedulerPresetPhase.Skipped,
            summary,
            completedAt,
            jobId);

    private static DadScheduledCrewJobResult Result(
        string scheduleId,
        string runId,
        string entryId,
        int repeat,
        DadSchedulerPresetPhase phase,
        string summary,
        DateTime completedAt,
        string? jobId = null)
        => new()
        {
            JobId = jobId ?? Guid.NewGuid().ToString("N"),
            ScheduleId = scheduleId,
            ScheduleRunId = runId,
            ScheduleEntryId = entryId,
            ScheduleRepeatIteration = repeat,
            FinalPhase = phase,
            Success = phase == DadSchedulerPresetPhase.Skipped,
            Summary = summary,
            StartedAtUtc = completedAt.AddMinutes(-1),
            CompletedAtUtc = completedAt,
        };

    private static DateTime Utc(int hour)
        => new(2026, 7, 21, hour, 0, 0, DateTimeKind.Utc);
}
