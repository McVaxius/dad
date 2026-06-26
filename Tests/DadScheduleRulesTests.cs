using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadScheduleRulesTests
{
    [Fact]
    public void NormalizeScheduleClampsRepeatCountsAndFillsIds()
    {
        var schedule = DadScheduleRules.NormalizeSchedule(new DadScheduleDefinition
        {
            ScheduleId = " schedule ",
            DisplayName = "  Daily chain  ",
            Entries =
            [
                new DadScheduleEntry { EntryId = " entry ", GroupId = " preset-a ", RepeatCount = 0 },
                new DadScheduleEntry { GroupId = "preset-b", RepeatCount = DadScheduleRules.MaxRepeatCount + 20 },
            ],
        });

        Assert.Equal("schedule", schedule.ScheduleId);
        Assert.Equal("Daily chain", schedule.DisplayName);
        Assert.Equal("entry", schedule.Entries[0].EntryId);
        Assert.Equal("preset-a", schedule.Entries[0].GroupId);
        Assert.Equal(DadScheduleRules.MinRepeatCount, schedule.Entries[0].RepeatCount);
        Assert.False(string.IsNullOrWhiteSpace(schedule.Entries[1].EntryId));
        Assert.Equal(DadScheduleRules.MaxRepeatCount, schedule.Entries[1].RepeatCount);
    }

    [Fact]
    public void DailyResetDueUsesFfxivResetBoundary()
    {
        var schedule = new DadScheduleDefinition
        {
            Cadence = DadScheduleCadence.DailyReset,
            Entries = [new DadScheduleEntry { GroupId = "preset" }],
        };
        var beforeReset = new DateTime(2026, 6, 25, 14, 59, 0, DateTimeKind.Utc);
        var atReset = new DateTime(2026, 6, 25, 15, 0, 0, DateTimeKind.Utc);

        schedule.LastDailyResetUtc = DadScheduleRules.GetDailyResetBoundaryUtc(beforeReset);
        Assert.False(DadScheduleRules.IsDailyResetDue(schedule, beforeReset));
        Assert.True(DadScheduleRules.IsDailyResetDue(schedule, atReset));
        Assert.Equal(new DateTime(2026, 6, 25, 15, 0, 0, DateTimeKind.Utc), DadScheduleRules.GetDailyResetBoundaryUtc(atReset));
    }

    [Fact]
    public void AdvanceAfterEntryHonorsRepeatsThenMovesToNextEntry()
    {
        var schedule = new DadScheduleDefinition
        {
            DisplayName = "Chain",
            Entries =
            [
                new DadScheduleEntry { EntryId = "a", GroupId = "preset-a", PresetName = "A", RepeatCount = 2 },
                new DadScheduleEntry { EntryId = "b", GroupId = "preset-b", PresetName = "B", RepeatCount = 1 },
            ],
        }.Normalize();
        var now = new DateTime(2026, 6, 25, 16, 0, 0, DateTimeKind.Utc);
        var state = DadScheduleRules.StartRun(schedule, dryRun: false, manualRun: true, requestedBy: "test", nowUtc: now);

        state = DadScheduleRules.AdvanceAfterEntry(state, schedule.Entries, true, "first done", now.AddMinutes(1));
        Assert.Equal(DadScheduleRunStatus.Running, state.Status);
        Assert.Equal(0, state.CurrentEntryIndex);
        Assert.Equal(2, state.RepeatIteration);
        Assert.Equal(1, state.CompletedEntryExecutions);

        state = DadScheduleRules.AdvanceAfterEntry(state, schedule.Entries, true, "second done", now.AddMinutes(2));
        Assert.Equal(DadScheduleRunStatus.Running, state.Status);
        Assert.Equal(1, state.CurrentEntryIndex);
        Assert.Equal(1, state.RepeatIteration);
        Assert.Equal("b", state.CurrentEntryId);
        Assert.Equal(2, state.CompletedEntryExecutions);

        state = DadScheduleRules.AdvanceAfterEntry(state, schedule.Entries, true, "all done", now.AddMinutes(3));
        Assert.Equal(DadScheduleRunStatus.Completed, state.Status);
        Assert.Equal(DadScheduleRunPhase.Completed, state.Phase);
        Assert.Equal(3, state.CompletedEntryExecutions);
    }

    [Fact]
    public void AdvanceAfterEntryStopsOnFailure()
    {
        var schedule = new DadScheduleDefinition
        {
            DisplayName = "Chain",
            Entries = [new DadScheduleEntry { GroupId = "preset-a", RepeatCount = 2 }],
        }.Normalize();
        var state = DadScheduleRules.StartRun(schedule, dryRun: false, manualRun: true, requestedBy: "test", nowUtc: DateTime.UtcNow);

        var blocked = DadScheduleRules.AdvanceAfterEntry(state, schedule.Entries, false, "preset failed", DateTime.UtcNow);

        Assert.Equal(DadScheduleRunStatus.Blocked, blocked.Status);
        Assert.Equal(DadScheduleRunPhase.Blocked, blocked.Phase);
        Assert.Equal("preset failed", blocked.BlockedReason);
        Assert.Equal(0, blocked.CompletedEntryExecutions);
    }

    [Fact]
    public void ValidateCurrentEntryBlocksMissingPreset()
    {
        var schedule = new DadScheduleDefinition
        {
            DisplayName = "Chain",
            Entries = [new DadScheduleEntry { GroupId = "missing-preset" }],
        }.Normalize();
        var state = DadScheduleRules.StartRun(schedule, dryRun: false, manualRun: true, requestedBy: "test", nowUtc: DateTime.UtcNow);

        var blocker = DadScheduleRules.ValidateCurrentEntry(
            state,
            schedule.Entries,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "other-preset" });

        Assert.Contains("missing preset", blocker);
        Assert.Contains("missing-preset", blocker);
    }

    [Fact]
    public void SchedulerJobAndResultClonePreserveScheduleMetadata()
    {
        var job = new DadScheduledCrewJob
        {
            ScheduleId = "schedule",
            ScheduleRunId = "run",
            ScheduleEntryId = "entry",
            ScheduleEntryIndex = 2,
            ScheduleRepeatIteration = 3,
        };
        var result = new DadScheduledCrewJobResult
        {
            ScheduleId = "schedule",
            ScheduleRunId = "run",
            ScheduleEntryId = "entry",
            ScheduleEntryIndex = 2,
            ScheduleRepeatIteration = 3,
        };

        var jobClone = job.Clone();
        var resultClone = result.Clone();

        Assert.Equal("schedule", jobClone.ScheduleId);
        Assert.Equal("run", jobClone.ScheduleRunId);
        Assert.Equal("entry", jobClone.ScheduleEntryId);
        Assert.Equal(2, jobClone.ScheduleEntryIndex);
        Assert.Equal(3, jobClone.ScheduleRepeatIteration);
        Assert.Equal("schedule", resultClone.ScheduleId);
        Assert.Equal("run", resultClone.ScheduleRunId);
        Assert.Equal("entry", resultClone.ScheduleEntryId);
        Assert.Equal(2, resultClone.ScheduleEntryIndex);
        Assert.Equal(3, resultClone.ScheduleRepeatIteration);
    }
}
