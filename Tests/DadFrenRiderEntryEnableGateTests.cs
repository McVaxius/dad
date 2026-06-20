using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadFrenRiderEntryEnableGateTests
{
    private const string Command = "/fr on";
    private static readonly DateTime Start = new(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SuccessfulEntryEnableSendsOncePerRun()
    {
        var gate = new DadFrenRiderEntryEnableGate();
        var sendCount = 0;

        var first = gate.Apply(
            "run-1|DutySupport",
            "a Duty Support operation",
            Command,
            Start,
            () =>
            {
                sendCount++;
                return DadFrenRiderCommandResult.Success();
            },
            out var firstSummary);
        var second = gate.Apply(
            "run-1|DutySupport",
            "a Duty Support operation",
            Command,
            Start.AddSeconds(1),
            () =>
            {
                sendCount++;
                return DadFrenRiderCommandResult.Failure("should not run");
            },
            out var secondSummary);

        Assert.Equal(DadFrenRiderEntryEnableStatus.Sent, first);
        Assert.Equal(DadFrenRiderEntryEnableStatus.AlreadySent, second);
        Assert.Equal(1, sendCount);
        Assert.Contains("sent /fr on after duty entry", firstSummary);
        Assert.Equal(firstSummary, secondSummary);
    }

    [Fact]
    public void FailedAttemptWaitsForOneSecondBeforeRetry()
    {
        var gate = new DadFrenRiderEntryEnableGate();
        var sendCount = 0;

        var first = gate.Apply(
            "run-1|DutySupport",
            "a Duty Support operation",
            Command,
            Start,
            () =>
            {
                sendCount++;
                return DadFrenRiderCommandResult.Failure("Command manager rejected /fr on");
            },
            out _);
        var early = gate.Apply(
            "run-1|DutySupport",
            "a Duty Support operation",
            Command,
            Start.AddMilliseconds(500),
            () =>
            {
                sendCount++;
                return DadFrenRiderCommandResult.Success();
            },
            out _);
        var retry = gate.Apply(
            "run-1|DutySupport",
            "a Duty Support operation",
            Command,
            Start.AddSeconds(1),
            () =>
            {
                sendCount++;
                return DadFrenRiderCommandResult.Success();
            },
            out var retrySummary);

        Assert.Equal(DadFrenRiderEntryEnableStatus.PendingRetry, first);
        Assert.Equal(DadFrenRiderEntryEnableStatus.PendingRetry, early);
        Assert.Equal(DadFrenRiderEntryEnableStatus.Sent, retry);
        Assert.Equal(2, sendCount);
        Assert.Contains("sent /fr on after duty entry", retrySummary);
    }

    [Fact]
    public void FailedRetryWindowReportsCommandFailure()
    {
        var gate = new DadFrenRiderEntryEnableGate();
        var sendCount = 0;
        DadFrenRiderEntryEnableStatus status = default;
        var summary = string.Empty;

        for (var second = 0; second <= 5; second++)
        {
            status = gate.Apply(
                "run-1|DutySupport",
                "a Duty Support operation",
                Command,
                Start.AddSeconds(second),
                () =>
                {
                    sendCount++;
                    return DadFrenRiderCommandResult.Failure("Command manager rejected /fr on");
                },
                out summary);
        }

        Assert.Equal(DadFrenRiderEntryEnableStatus.Failed, status);
        Assert.Equal(6, sendCount);
        Assert.Contains("failed to send /fr on after duty entry", summary);
        Assert.Contains("Command manager rejected /fr on", summary);
    }

    [Fact]
    public void NewRunCanSendAfterPreviousRunSucceeded()
    {
        var gate = new DadFrenRiderEntryEnableGate();
        var sendCount = 0;

        gate.Apply(
            "run-1|DutySupport",
            "a Duty Support operation",
            Command,
            Start,
            () =>
            {
                sendCount++;
                return DadFrenRiderCommandResult.Success();
            },
            out _);
        var secondRun = gate.Apply(
            "run-2|DutySupport",
            "a Duty Support operation",
            Command,
            Start.AddSeconds(10),
            () =>
            {
                sendCount++;
                return DadFrenRiderCommandResult.Success();
            },
            out _);

        Assert.Equal(DadFrenRiderEntryEnableStatus.Sent, secondRun);
        Assert.Equal(2, sendCount);
    }

    [Fact]
    public void InterleavedRunDoesNotClearSentRunState()
    {
        var gate = new DadFrenRiderEntryEnableGate();
        var sendCount = 0;

        gate.Apply(
            "run-1|DutySupport",
            "a Duty Support operation",
            Command,
            Start,
            () =>
            {
                sendCount++;
                return DadFrenRiderCommandResult.Success();
            },
            out _);
        gate.Apply(
            "run-2|DutySupport",
            "a Duty Support operation",
            Command,
            Start.AddSeconds(1),
            () =>
            {
                sendCount++;
                return DadFrenRiderCommandResult.Success();
            },
            out _);
        var firstRunAgain = gate.Apply(
            "run-1|DutySupport",
            "a Duty Support operation",
            Command,
            Start.AddSeconds(2),
            () =>
            {
                sendCount++;
                return DadFrenRiderCommandResult.Failure("should not run");
            },
            out _);

        Assert.Equal(DadFrenRiderEntryEnableStatus.AlreadySent, firstRunAgain);
        Assert.Equal(2, sendCount);
    }
}
