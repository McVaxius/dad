using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadDroppedPeerContinuationRulesTests
{
    private static readonly DateTime Now = new(2026, 7, 15, 16, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(300);

    [Fact]
    public void EnteredNonLeaderWaitsBeyondTimeoutWhileExactLeaderRuns()
    {
        var participant = Participant("worker-x", "account-x", "X@World", 2002, "Slot2");
        var leader = Participant("worker-w", "account-w", "W@World", 1001, "Slot1", authority: true);
        var command = Command(participant, DadWorkerExecutionRole.Participant);
        var leaderCommand = Command(leader, DadWorkerExecutionRole.QueueLeader);

        var decision = DadDroppedPeerContinuationRules.EvaluateMissingPeer(
            participant,
            command,
            Status(command, participant, enteredDuty: true, terminal: false, success: false),
            leaderCommand,
            Status(leaderCommand, leader, enteredDuty: true, terminal: false, success: false),
            Now.AddSeconds(-600),
            Now,
            Timeout);

        Assert.Equal(DadDroppedPeerContinuationAction.Wait, decision.Action);
        Assert.Equal(DadScheduleFailureKind.None, decision.FailureKind);
    }

    [Fact]
    public void ExactLeaderTerminalSuccessSatisfiesOnlyEnteredNonLeader()
    {
        var participant = Participant("worker-x", "account-x", "X@World", 2002, "Slot2");
        var leader = Participant("worker-w", "account-w", "W@World", 1001, "Slot1", authority: true);
        var command = Command(participant, DadWorkerExecutionRole.Participant);
        var leaderCommand = Command(leader, DadWorkerExecutionRole.QueueLeader);

        var decision = DadDroppedPeerContinuationRules.EvaluateMissingPeer(
            participant,
            command,
            Status(command, participant, enteredDuty: true, terminal: false, success: false),
            leaderCommand,
            Status(leaderCommand, leader, enteredDuty: true, terminal: true, success: true),
            Now.AddSeconds(-600),
            Now,
            Timeout);

        Assert.Equal(DadDroppedPeerContinuationAction.SatisfyParticipant, decision.Action);
        Assert.Contains("without replaying", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingPeerWithoutEntryProofWaitsThenFailsAtExistingTimeout()
    {
        var participant = Participant("worker-x", "account-x", "X@World", 2002, "Slot2");
        var command = Command(participant, DadWorkerExecutionRole.Participant);
        var status = Status(command, participant, enteredDuty: false, terminal: false, success: false);

        Assert.Equal(DadDroppedPeerContinuationAction.Wait,
            DadDroppedPeerContinuationRules.EvaluateMissingPeer(
                participant, command, status, null, null, Now.AddSeconds(-299), Now, Timeout).Action);
        var failed = DadDroppedPeerContinuationRules.EvaluateMissingPeer(
            participant, command, status, null, null, Now.AddSeconds(-300), Now, Timeout);
        Assert.Equal(DadDroppedPeerContinuationAction.Fail, failed.Action);
        Assert.Equal(DadScheduleFailureKind.EntryTerminalFailure, failed.FailureKind);
    }

    [Theory]
    [InlineData(DadWorkerExecutionRole.QueueLeader, false)]
    [InlineData(DadWorkerExecutionRole.Participant, true)]
    public void QueueLeaderAndCoordinatorAreNeverSynthesized(
        DadWorkerExecutionRole role,
        bool authority)
    {
        var participant = Participant("worker-x", "account-x", "X@World", 2002, "Slot2", authority);
        var command = Command(participant, role);
        var status = Status(command, participant, enteredDuty: true, terminal: false, success: false);

        var decision = DadDroppedPeerContinuationRules.EvaluateMissingPeer(
            participant, command, status, null, null, Now.AddSeconds(-300), Now, Timeout);

        Assert.Equal(DadDroppedPeerContinuationAction.Fail, decision.Action);
        Assert.Equal(DadScheduleFailureKind.MissingOrUnknownLeaderState, decision.FailureKind);
    }

    [Fact]
    public void ContradictoryOrFailedCachedPeerCanNeverBeSynthesized()
    {
        var participant = Participant("worker-x", "account-x", "X@World", 2002, "Slot2");
        var command = Command(participant, DadWorkerExecutionRole.Participant);
        var contradictory = Status(command, participant, enteredDuty: true, terminal: false, success: false);
        contradictory.CommandId = "other-command";
        var failed = Status(command, participant, enteredDuty: true, terminal: true, success: false);

        Assert.Equal(DadDroppedPeerContinuationAction.Fail,
            DadDroppedPeerContinuationRules.EvaluateMissingPeer(
                participant, command, contradictory, null, null, Now, Now, Timeout).Action);
        Assert.Equal(DadDroppedPeerContinuationAction.Fail,
            DadDroppedPeerContinuationRules.EvaluateMissingPeer(
                participant, command, failed, null, null, Now, Now, Timeout).Action);
    }

    [Fact]
    public void WrongLeaderIdentityCannotAuthorizeContinuation()
    {
        var participant = Participant("worker-x", "account-x", "X@World", 2002, "Slot2");
        var leader = Participant("worker-w", "account-w", "W@World", 1001, "Slot1", authority: true);
        var command = Command(participant, DadWorkerExecutionRole.Participant);
        var leaderCommand = Command(leader, DadWorkerExecutionRole.QueueLeader);
        var wrongLeaderStatus = Status(leaderCommand, leader, enteredDuty: true, terminal: true, success: true);
        wrongLeaderStatus.WorkerSessionId = new DadWorkerSessionId("replacement-leader");

        var decision = DadDroppedPeerContinuationRules.EvaluateMissingPeer(
            participant,
            command,
            Status(command, participant, enteredDuty: true, terminal: false, success: false),
            leaderCommand,
            wrongLeaderStatus,
            Now.AddSeconds(-300),
            Now,
            Timeout);

        Assert.Equal(DadDroppedPeerContinuationAction.Fail, decision.Action);
        Assert.Equal(DadScheduleFailureKind.MissingOrUnknownLeaderState, decision.FailureKind);
    }

    [Fact]
    public void LeaderFromAnotherRunCannotAuthorizeContinuation()
    {
        var participant = Participant("worker-x", "account-x", "X@World", 2002, "Slot2");
        var leader = Participant("worker-w", "account-w", "W@World", 1001, "Slot1", authority: true);
        var command = Command(participant, DadWorkerExecutionRole.Participant);
        var leaderCommand = Command(leader, DadWorkerExecutionRole.QueueLeader);
        leaderCommand.RunId = "other-run";
        leaderCommand.Plan.Request.RequestId = "other-run";

        var decision = DadDroppedPeerContinuationRules.EvaluateMissingPeer(
            participant,
            command,
            Status(command, participant, enteredDuty: true, terminal: false, success: false),
            leaderCommand,
            Status(leaderCommand, leader, enteredDuty: true, terminal: true, success: true),
            Now.AddSeconds(-300),
            Now,
            Timeout);

        Assert.Equal(DadDroppedPeerContinuationAction.Fail, decision.Action);
        Assert.Equal(DadScheduleFailureKind.MissingOrUnknownLeaderState, decision.FailureKind);
    }

    private static DadParticipantSnapshot Participant(
        string worker,
        string account,
        string character,
        ulong contentId,
        string slot,
        bool authority = false)
        => new()
        {
            WorkerSessionId = new DadWorkerSessionId(worker),
            ManagedAccountKey = new DadAccountKey(account),
            ActiveCharacterKey = new DadCharacterKey(character),
            Character = new DadAcquiredCharacter
            {
                AccountId = account,
                CharacterKey = character,
                ContentId = contentId,
            },
            AssignedSlotId = slot,
            IsLocalClient = true,
            IsAuthority = authority,
        };

    private static DadWorkerExecutionCommand Command(
        DadParticipantSnapshot participant,
        DadWorkerExecutionRole role)
        => new()
        {
            CommandId = $"command-{participant.WorkerSessionId.Value}",
            RunId = "run",
            ModuleIndex = 0,
            Role = role,
            Plan = new DadRunPlan
            {
                Request = new DadRunRequest { RequestId = "run" },
                Modules = [new DadPlannedModuleExecution { ModuleId = DadModuleId.PremadeDuty }],
            },
            Participants = [participant.Clone()],
        };

    private static DadWorkerExecutionStatus Status(
        DadWorkerExecutionCommand command,
        DadParticipantSnapshot participant,
        bool enteredDuty,
        bool terminal,
        bool success)
        => new()
        {
            CommandId = command.CommandId,
            RunId = command.RunId,
            WorkerSessionId = participant.WorkerSessionId,
            Role = command.Role,
            ModuleId = DadModuleId.PremadeDuty,
            State = terminal
                ? success ? DadWorkerExecutionState.Completed : DadWorkerExecutionState.Failed
                : DadWorkerExecutionState.Running,
            EnteredDuty = enteredDuty,
            IsTerminal = terminal,
            Success = success,
        };
}
