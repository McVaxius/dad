using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadScheduleCursorFormatterTests
{
    [Fact]
    public void ExactEntryIdentityOverridesStaleIndexAndResolvesTotals()
    {
        var schedule = Schedule();
        var run = Run();
        run.CurrentEntryId = "entry-b";
        run.CurrentEntryIndex = 0;
        run.RepeatIteration = 2;

        var text = DadScheduleCursorFormatter.Format(run, [schedule]);

        Assert.Equal("Nightly — Plan B | entry 2/2 | repeat 2/3", text);
    }

    [Fact]
    public void EntryIndexFallsBackWhenStableEntryIdDoesNotResolve()
    {
        var schedule = Schedule();
        var run = Run();
        run.CurrentEntryId = "missing-entry";
        run.CurrentEntryIndex = 0;

        var text = DadScheduleCursorFormatter.Format(run, [schedule]);

        Assert.Equal("Nightly — Plan A | entry 1/2 | repeat 1/2", text);
    }

    [Fact]
    public void ActiveScheduleIdSelectsTheCorrectDefinition()
    {
        var other = Schedule();
        other.ScheduleId = "other";
        other.DisplayName = "Other";
        other.Entries = [new DadScheduleEntry { EntryId = "entry-b", PresetName = "Wrong", RepeatCount = 9 }];
        var run = Run();
        run.CurrentEntryId = "entry-b";
        run.CurrentEntryIndex = 0;
        run.RepeatIteration = 2;

        var text = DadScheduleCursorFormatter.Format(run, [other, Schedule()]);

        Assert.Equal("Nightly — Plan B | entry 2/2 | repeat 2/3", text);
    }

    [Fact]
    public void UnknownScheduleOmitsUnavailableDenominators()
    {
        var run = Run();
        run.ScheduleId = "missing";
        run.CurrentEntryIndex = 2;
        run.RepeatIteration = 2;

        var text = DadScheduleCursorFormatter.Format(run, [Schedule()]);

        Assert.Equal("Nightly — Plan A | entry 3 | repeat 2", text);
    }

    [Fact]
    public void UnresolvedEntryDoesNotInventDefinitionTotals()
    {
        var run = Run();
        run.CurrentEntryId = "missing-entry";
        run.CurrentEntryIndex = 9;

        var text = DadScheduleCursorFormatter.Format(run, [Schedule()]);

        Assert.Equal("Nightly — Plan A | entry 10 | repeat 1", text);
    }

    private static DadScheduleDefinition Schedule()
        => new()
        {
            ScheduleId = "schedule",
            DisplayName = "Nightly",
            Entries =
            [
                new DadScheduleEntry
                {
                    EntryId = "entry-a",
                    GroupId = "group-a",
                    PresetName = "Plan A",
                    RepeatCount = 2,
                },
                new DadScheduleEntry
                {
                    EntryId = "entry-b",
                    GroupId = "group-b",
                    PresetName = "Plan B",
                    RepeatCount = 3,
                },
            ],
        };

    private static DadScheduleRunState Run()
        => new()
        {
            ScheduleId = "schedule",
            ScheduleName = "Nightly",
            CurrentEntryId = "entry-a",
            CurrentGroupId = "group-a",
            CurrentPresetName = "Plan A",
            CurrentEntryIndex = 0,
            RepeatIteration = 1,
        };
}
