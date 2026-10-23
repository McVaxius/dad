using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadWorkerStatusPollingRulesTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("legacy", false)]
    [InlineData("accepted", false)]
    [InlineData("command", false)]
    [InlineData("run", false)]
    [InlineData("worker", false)]
    [InlineData("role", false)]
    [InlineData("module", false)]
    [InlineData("queued", false)]
    [InlineData("terminal", false)]
    [InlineData("entered", false)]
    [InlineData("old", false)]
    [InlineData("future", false)]
    public void OnlyExactFreshUnacceptedIdleProofCanDeferAdmission(string fault, bool expected)
    {
        var participant = Participant();
        var command = Command(participant);
        var now = DateTime.UtcNow;
        var reply = Acknowledgement(participant, command);
        reply.Accepted = false;
        reply.RuntimeReadinessDeferred = true;
        reply.Status.State = DadWorkerExecutionState.Idle;
        reply.Status.UpdatedAtUtc = now;
        switch (fault)
        {
            case "legacy": reply.RuntimeReadinessDeferred = false; break;
            case "accepted": reply.Accepted = true; break;
            case "command": reply.CommandId = "other"; break;
            case "run": reply.RunId = "other"; break;
            case "worker": reply.WorkerSessionId = new("other"); break;
            case "role": reply.Status.Role = DadWorkerExecutionRole.QueueLeader; break;
            case "module": reply.Status.ModuleId = DadModuleId.PremadeDuty; break;
            case "queued": reply.Status.State = DadWorkerExecutionState.Accepted; break;
            case "terminal": reply.Status.IsTerminal = true; break;
            case "entered": reply.Status.EnteredDuty = true; break;
            case "old": reply.Status.UpdatedAtUtc = now.AddSeconds(-11); break;
            case "future": reply.Status.UpdatedAtUtc = now.AddTicks(1); break;
        }
        Assert.Equal(expected, DadWorkerStatusPollingRules.IsExactReadinessDeferral(participant, command, reply, now));
    }

    [Theory]
    [InlineData(0, "command", false)]
    [InlineData(299, "command", false)]
    [InlineData(300, "command", true)]
    [InlineData(301, "command", true)]
    [InlineData(-1, "command", true)]
    [InlineData(0, "changed", true)]
    public void ReadinessWaitKeepsOriginalDeadlineAndFailsClosedOnClockOrCommandDrift(int seconds, string command, bool expired)
    {
        var start = DateTime.UtcNow;
        Assert.Equal(expired, DadWorkerStatusPollingRules.ReadinessWaitExpired("command", command,
            start, start.AddSeconds(seconds), TimeSpan.FromSeconds(300)));
    }

    [Fact]
    public void MissingAcknowledgementRemainsPendingWithoutSubstitute()
    {
        var participant = Participant();
        var command = Command(participant);

        Assert.Null(DadWorkerStatusPollingRules.SelectCommandAcknowledgement(null, command));
    }

    [Fact]
    public void ExactCurrentCommandAcknowledgementAuthorizesStatusPolling()
    {
        var participant = Participant();
        var command = Command(participant);
        var acknowledgement = Acknowledgement(participant, command);

        var selected = DadWorkerStatusPollingRules.SelectCommandAcknowledgement(
            acknowledgement,
            command);

        Assert.Same(acknowledgement, selected);
        Assert.True(DadWorkerStatusPollingRules.MatchesExactAcknowledgement(
            participant,
            command,
            acknowledgement));
    }

    [Fact]
    public void AcknowledgementFromEarlierModuleOfSameRunIsDiscarded()
    {
        var participant = Participant();
        var current = Command(participant, moduleIndex: 1, commandId: "command-b");
        var earlier = Command(participant, moduleIndex: 0, commandId: "command-a");

        Assert.Equal(current.RunId, earlier.RunId);
        Assert.Null(DadWorkerStatusPollingRules.SelectCommandAcknowledgement(
            Acknowledgement(participant, earlier),
            current));
    }

    [Fact]
    public void CurrentCommandAcknowledgementContradictionFailsExactMatch()
    {
        var participant = Participant();
        var command = Command(participant);
        var acknowledgement = Acknowledgement(participant, command);
        acknowledgement.Status.Role = DadWorkerExecutionRole.QueueLeader;

        var selected = DadWorkerStatusPollingRules.SelectCommandAcknowledgement(
            acknowledgement,
            command);

        Assert.Same(acknowledgement, selected);
        Assert.False(DadWorkerStatusPollingRules.MatchesExactAcknowledgement(
            participant,
            command,
            acknowledgement));
    }

    [Fact]
    public void StalePriorRunLiveStatusIsDiscardedWithoutCacheFallback()
    {
        var participant = Participant();
        var command = Command(participant, runId: "run-current", commandId: "command-current");
        var cached = Status(participant, command, DadWorkerExecutionState.Accepted);
        var priorCommand = Command(participant, runId: "run-prior", commandId: "command-prior");
        var stale = Status(participant, priorCommand, DadWorkerExecutionState.Completed);

        var selected = DadWorkerStatusPollingRules.SelectRemoteStatus(
            stale,
            cached,
            command,
            exactRequestPending: false,
            authenticatedRouteRoutable: true);

        Assert.Null(selected);
    }

    [Fact]
    public void EarlierCommandOfSameRunLiveStatusIsDiscarded()
    {
        var participant = Participant();
        var current = Command(participant, moduleIndex: 1, commandId: "command-b");
        var earlier = Command(participant, moduleIndex: 0, commandId: "command-a");

        Assert.Null(DadWorkerStatusPollingRules.SelectRemoteStatus(
            Status(participant, earlier, DadWorkerExecutionState.Completed),
            Status(participant, current, DadWorkerExecutionState.Accepted),
            current,
            exactRequestPending: false,
            authenticatedRouteRoutable: true));
    }

    [Fact]
    public void ExactLiveStatusProgressionReplacesPendingCache()
    {
        var participant = Participant();
        var command = Command(participant);
        var live = Status(participant, command, DadWorkerExecutionState.WaitingForQueue, "live");
        var cached = Status(participant, command, DadWorkerExecutionState.Accepted, "cached");

        var selected = DadWorkerStatusPollingRules.SelectRemoteStatus(
            live,
            cached,
            command,
            exactRequestPending: true,
            authenticatedRouteRoutable: true);

        Assert.Same(live, selected);
    }

    [Fact]
    public void PendingRoutableRequestReturnsIsolatedExactCacheClone()
    {
        var participant = Participant();
        var command = Command(participant);
        var cached = Status(participant, command, DadWorkerExecutionState.WaitingForQueue, "cached");
        cached.StepResult.Summary = "original step";

        var selected = DadWorkerStatusPollingRules.SelectRemoteStatus(
            liveStatus: null,
            cachedStatus: cached,
            command: command,
            exactRequestPending: true,
            authenticatedRouteRoutable: true);

        Assert.NotNull(selected);
        Assert.NotSame(cached, selected);
        Assert.NotSame(cached.StepResult, selected.StepResult);
        selected.Summary = "changed";
        selected.StepResult.Summary = "changed step";
        Assert.Equal("cached", cached.Summary);
        Assert.Equal("original step", cached.StepResult.Summary);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void MissingOrDisconnectedStatusRequestReturnsNull(bool pending, bool routable)
    {
        var participant = Participant();
        var command = Command(participant);

        var selected = DadWorkerStatusPollingRules.SelectRemoteStatus(
            liveStatus: null,
            cachedStatus: Status(participant, command, DadWorkerExecutionState.Accepted),
            command: command,
            exactRequestPending: pending,
            authenticatedRouteRoutable: routable);

        Assert.Null(selected);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("module")]
    [InlineData("worker")]
    [InlineData("identity")]
    public void CurrentCommandContradictionsReachStrictValidator(string contradiction)
    {
        var participant = Participant();
        var command = Command(participant);
        var live = Status(participant, command, DadWorkerExecutionState.WaitingForQueue);
        switch (contradiction)
        {
            case "role":
                live.Role = DadWorkerExecutionRole.QueueLeader;
                break;
            case "module":
                live.ModuleId = DadModuleId.PremadeDuty;
                break;
            case "worker":
                live.WorkerSessionId = new DadWorkerSessionId("worker-other");
                break;
            case "identity":
                participant.ActiveCharacterKey = new DadCharacterKey("Different@World");
                break;
        }

        var selected = DadWorkerStatusPollingRules.SelectRemoteStatus(
            liveStatus: live,
            cachedStatus: null,
            command: command,
            exactRequestPending: false,
            authenticatedRouteRoutable: true);

        Assert.Same(live, selected);
        Assert.False(DadDroppedPeerContinuationRules.MatchesExactCommand(
            participant,
            command,
            selected!));
    }

    private static DadParticipantSnapshot Participant()
        => new()
        {
            WorkerSessionId = new DadWorkerSessionId("worker-x"),
            ManagedAccountKey = new DadAccountKey("account-x"),
            ActiveCharacterKey = new DadCharacterKey("X@World"),
            Character = new DadAcquiredCharacter
            {
                AccountId = "account-x",
                CharacterKey = "X@World",
                ContentId = 2002,
            },
            AssignedSlotId = "Slot2",
            IsLocalClient = true,
        };

    private static DadWorkerExecutionCommand Command(
        DadParticipantSnapshot participant,
        int moduleIndex = 0,
        string commandId = "command-x",
        string runId = "run-x")
        => new()
        {
            CommandId = commandId,
            RunId = runId,
            ModuleIndex = moduleIndex,
            Role = DadWorkerExecutionRole.Participant,
            Plan = new DadRunPlan
            {
                Request = new DadRunRequest { RequestId = runId },
                Modules =
                [
                    new DadPlannedModuleExecution { ModuleId = DadModuleId.DailyMsq },
                    new DadPlannedModuleExecution { ModuleId = DadModuleId.PremadeDuty },
                ],
            },
            Participants = [participant.Clone()],
        };

    private static DadWorkerExecutionAck Acknowledgement(
        DadParticipantSnapshot participant,
        DadWorkerExecutionCommand command)
        => new()
        {
            CommandId = command.CommandId,
            RunId = command.RunId,
            WorkerSessionId = participant.WorkerSessionId,
            Accepted = true,
            Summary = "accepted",
            Status = Status(participant, command, DadWorkerExecutionState.Accepted),
        };

    private static DadWorkerExecutionStatus Status(
        DadParticipantSnapshot participant,
        DadWorkerExecutionCommand command,
        DadWorkerExecutionState state,
        string summary = "status")
        => new()
        {
            CommandId = command.CommandId,
            RunId = command.RunId,
            WorkerSessionId = participant.WorkerSessionId,
            Role = command.Role,
            ModuleId = command.Plan.Modules[command.ModuleIndex].ModuleId,
            State = state,
            Summary = summary,
        };
}
