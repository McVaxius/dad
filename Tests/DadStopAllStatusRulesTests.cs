using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadStopAllStatusRulesTests
{
    [Fact]
    public void PendingWorkerKeepsAggregateOpen()
    {
        var status = Status(DadStopAllWorkerState.Acknowledged, DadStopAllWorkerState.Expected);

        DadStopAllStatusRules.FinalizeFromWorkers(status, DateTime.UtcNow);

        Assert.False(status.IsFinal);
        Assert.Null(status.CompletedAtUtc);
        Assert.Contains("acknowledged 1", status.Summary);
        Assert.Contains("pending 1", status.Summary);
    }

    [Fact]
    public void RejectionDisconnectAndTimeoutAreFinalPartialOutcomes()
    {
        var completed = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        var status = Status(
            DadStopAllWorkerState.Acknowledged,
            DadStopAllWorkerState.Rejected,
            DadStopAllWorkerState.Disconnected,
            DadStopAllWorkerState.TimedOut);

        DadStopAllStatusRules.FinalizeFromWorkers(status, completed);

        Assert.True(status.IsFinal);
        Assert.True(status.Partial);
        Assert.Equal(completed, status.CompletedAtUtc);
        Assert.Contains("rejected 1", status.Summary);
        Assert.Contains("disconnected 1", status.Summary);
        Assert.Contains("timed out 1", status.Summary);
    }

    [Fact]
    public void AcknowledgedCommittedTakeoverMakesAggregatePartial()
    {
        var status = Status(DadStopAllWorkerState.Acknowledged);
        status.Workers[0].Partial = true;
        status.Workers[0].PreservedCommittedTakeovers = 1;

        DadStopAllStatusRules.FinalizeFromWorkers(status, DateTime.UtcNow);

        Assert.True(status.IsFinal);
        Assert.True(status.Partial);
    }

    private static DadStopAllStatus Status(params DadStopAllWorkerState[] states)
        => new()
        {
            OperationId = "operation",
            LocalResult = new DadStopAllWorkerResult { LocalCleanupCompleted = true },
            Workers = states.Select((state, index) => new DadStopAllWorkerResult
            {
                WorkerSessionId = new DadWorkerSessionId($"worker-{index}"),
                State = state,
            }).ToList(),
        };
}
