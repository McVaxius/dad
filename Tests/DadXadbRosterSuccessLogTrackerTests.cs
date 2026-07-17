using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadXadbRosterSuccessLogTrackerTests
{
    private static readonly DadXadbRosterSuccessSignature Initial = new(159, 1, 6, true);

    [Fact]
    public void FirstUnchangedChangedFailedAndRecoveredTransitionsAreCoalesced()
    {
        var tracker = new DadXadbRosterSuccessLogTracker();

        var first = tracker.RecordSuccess(Initial);
        var unchanged = tracker.RecordSuccess(Initial);
        var changed = tracker.RecordSuccess(Initial with { RowCount = 160 });
        var failed = tracker.RecordFailure();
        var recovered = tracker.RecordSuccess(Initial with { RowCount = 160 });

        Assert.Equal(DadXadbRosterLogTransition.FirstSuccess, first);
        Assert.Equal(DadXadbRosterLogTransition.UnchangedSuccess, unchanged);
        Assert.Equal(DadXadbRosterLogTransition.ChangedSuccess, changed);
        Assert.Equal(DadXadbRosterLogTransition.Failure, failed);
        Assert.Equal(DadXadbRosterLogTransition.RecoveredSuccess, recovered);
        Assert.True(DadXadbRosterSuccessLogTracker.ShouldWriteInformation(first));
        Assert.False(DadXadbRosterSuccessLogTracker.ShouldWriteInformation(unchanged));
        Assert.True(DadXadbRosterSuccessLogTracker.ShouldWriteInformation(changed));
        Assert.False(DadXadbRosterSuccessLogTracker.ShouldWriteInformation(failed));
        Assert.True(DadXadbRosterSuccessLogTracker.ShouldWriteInformation(recovered));
    }

    [Theory]
    [InlineData(160, 1, 6, true)]
    [InlineData(159, 2, 6, true)]
    [InlineData(159, 1, 7, true)]
    [InlineData(159, 1, 6, false)]
    public void EachSignatureFieldTriggersChangedSuccess(
        int rowCount,
        int rosterVersion,
        int contractVersion,
        bool fullRoster)
    {
        var tracker = new DadXadbRosterSuccessLogTracker();
        tracker.RecordSuccess(Initial);

        var transition = tracker.RecordSuccess(new DadXadbRosterSuccessSignature(
            rowCount,
            rosterVersion,
            contractVersion,
            fullRoster));

        Assert.Equal(DadXadbRosterLogTransition.ChangedSuccess, transition);
    }
}
