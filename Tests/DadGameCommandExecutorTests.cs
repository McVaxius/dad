using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadGameCommandExecutorTests
{
    [Fact]
    public void OpenMainWindowSendsExactlyPfinderThroughInjectedExecutor()
    {
        var executor = new RecordingExecutor();
        var dispatcher = new DadAlliancePartyFinderCommandDispatcher(executor);

        var result = dispatcher.TryExecute("Requested a fresh Party Finder window.");

        Assert.True(result.Sent);
        Assert.Equal(["/pfinder"], executor.Commands);
    }

    [Fact]
    public void SuccessfulNativeSubmissionDoesNotAdvanceWithoutAddonAcknowledgement()
    {
        var executor = new RecordingExecutor();
        var fixture = new FlowFixture(executor);

        Assert.Equal(DadAlliancePfCreateStage.OpenMainWindow, fixture.Tick().Stage);
        var sent = fixture.Tick();
        var unacknowledged = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Progress, sent.Kind);
        Assert.Equal("action", sent.Event);
        Assert.True(sent.ShouldAudit);
        Assert.Equal(DadAlliancePfCreateStage.OpenMainWindow, sent.Stage);
        Assert.Equal(DadAlliancePfCreateStage.OpenMainWindow, unacknowledged.Stage);
        Assert.Equal(["/pfinder"], executor.Commands);

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            MainVisible = true,
            MainReady = true,
            MainRecruitUsable = true,
        };
        var acknowledged = fixture.Tick();

        Assert.Equal("acknowledgement", acknowledged.Event);
        Assert.Equal(DadAlliancePfCreateStage.OpenConditions, acknowledged.Stage);
    }

    [Fact]
    public void UnavailableNativeSinkBlocksAfterOneAuditedDispatch()
    {
        var executor = new RecordingExecutor
        {
            Result = false,
            Error = "The native game UI module is unavailable for /pfinder.",
        };
        var fixture = new FlowFixture(executor);
        Assert.Equal(DadAlliancePfCreateStage.OpenMainWindow, fixture.Tick().Stage);

        var blocked = fixture.Tick();
        var later = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, blocked.Kind);
        Assert.Equal("block", blocked.Event);
        Assert.True(blocked.ShouldAudit);
        Assert.Equal(1, blocked.Attempt);
        Assert.Null(blocked.NextRetryUtc);
        Assert.Contains("unavailable", blocked.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DadAlliancePfCreateStage.Blocked, blocked.Stage);
        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, later.Kind);
        Assert.Equal(["/pfinder"], executor.Commands);
    }

    [Fact]
    public void ThrowingNativeSinkBlocksAfterOneAuditedDispatch()
    {
        var executor = new RecordingExecutor
        {
            Exception = new InvalidOperationException("synthetic native sink failure"),
        };
        var fixture = new FlowFixture(executor);
        Assert.Equal(DadAlliancePfCreateStage.OpenMainWindow, fixture.Tick().Stage);

        var result = fixture.Tick();

        var later = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, result.Kind);
        Assert.Equal("block", result.Event);
        Assert.True(result.ShouldAudit);
        Assert.Contains("synthetic native sink failure", result.LastError, StringComparison.Ordinal);
        Assert.Null(result.NextRetryUtc);
        Assert.Equal(DadAlliancePfCreateStage.Blocked, result.Stage);
        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, later.Kind);
        Assert.Equal(["/pfinder"], executor.Commands);
    }

    private sealed class RecordingExecutor : IDadGameCommandExecutor
    {
        public List<string> Commands { get; } = [];
        public bool Result { get; init; } = true;
        public string Error { get; init; } = string.Empty;
        public Exception? Exception { get; init; }

        public bool TryExecute(string command, out string error)
        {
            Commands.Add(command);
            if (Exception != null)
                throw Exception;
            error = Error;
            return Result;
        }
    }

    private sealed class FlowFixture
    {
        private DateTime now =
            new(2026, 7, 25, 18, 30, 0, DateTimeKind.Utc);
        private readonly DadAlliancePartyFinderCreateFlow flow;

        public FlowFixture(IDadGameCommandExecutor executor)
        {
            Ui = new CommandUi(executor);
            flow = new DadAlliancePartyFinderCreateFlow(Ui, () => now);
        }

        public CommandUi Ui { get; }
        public DateTime LastTickUtc { get; private set; }

        public DadAlliancePfCreateResult Tick()
        {
            LastTickUtc = now;
            var result = flow.Advance(9752);
            now += DadAlliancePartyFinderCreateFlow.PollInterval;
            return result;
        }

    }

    private sealed class CommandUi : IDadAlliancePartyFinderCreateUi
    {
        private readonly DadAlliancePartyFinderCommandDispatcher dispatcher;

        public CommandUi(IDadGameCommandExecutor executor)
        {
            dispatcher = new DadAlliancePartyFinderCommandDispatcher(executor);
        }

        public DadAlliancePfCreateSnapshot Snapshot { get; set; } = new();

        public DadAlliancePfCreateSnapshot Read(int passcode)
            => Snapshot;

        public DadAlliancePfCreateActionResult Perform(
            DadAlliancePfCreateAction action,
            int passcode)
            => action == DadAlliancePfCreateAction.OpenMainWindow
                ? dispatcher.TryExecute("Requested a fresh Party Finder window.")
                : new DadAlliancePfCreateActionResult(true, $"sent {action}");
    }
}
