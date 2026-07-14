using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadSchedulePartyBoundaryTests
{
    [Fact]
    public void StandalonePresetRequiresPartyTeardown()
    {
        var boundary = DadScheduleRules.ResolveRepeatBoundary(
            string.Empty,
            string.Empty,
            repeatIteration: 0,
            entries: null);

        Assert.False(boundary.IsScheduleRun);
        Assert.False(boundary.PreservePartyAfterCompletion);
        Assert.True(boundary.RequiresPartyTeardown);
    }

    [Fact]
    public void SchedulePreservesPartyOnlyBeforeAnotherRepeatInTheSamePresetRow()
    {
        var entries = new List<DadScheduleEntry>
        {
            new() { EntryId = "row-a", GroupId = "preset-a", RepeatCount = 2 },
            new() { EntryId = "row-b", GroupId = "preset-b", RepeatCount = 1 },
        };

        var firstRepeat = DadScheduleRules.ResolveRepeatBoundary("run", "row-a", 1, entries);
        var finalRepeat = DadScheduleRules.ResolveRepeatBoundary("run", "row-a", 2, entries);
        var nextRow = DadScheduleRules.ResolveRepeatBoundary("run", "row-b", 1, entries);

        Assert.True(firstRepeat.PreservePartyAfterCompletion);
        Assert.False(firstRepeat.RequiresPartyTeardown);
        Assert.False(finalRepeat.PreservePartyAfterCompletion);
        Assert.True(finalRepeat.RequiresPartyTeardown);
        Assert.False(nextRow.PreservePartyAfterCompletion);
        Assert.True(nextRow.RequiresPartyTeardown);
    }

    [Fact]
    public void MissingScheduleRowMetadataFailsSafeToPartyTeardown()
    {
        var boundary = DadScheduleRules.ResolveRepeatBoundary(
            "run",
            "missing-row",
            repeatIteration: 1,
            [new DadScheduleEntry { EntryId = "other-row", RepeatCount = 2 }]);

        Assert.True(boundary.RequiresPartyTeardown);
    }
}
