using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadWorkerStatusPollingRulesTests
{
    private static readonly DateTime Now = new(2026, 7, 16, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ExactQueuedAcceptedStatusWaitsWithCompleteProvenance()
    {
        var participant = Participant();
        var command = Command(participant, moduleIndex: 0);

        var acknowledgement = DadWorkerStatusPollingRules.BuildQueuedAcknowledgement(
            command,
            participant.WorkerSessionId,
            Now);

        Assert.True(acknowledgement.Accepted);
        Assert.Equal(command.CommandId, acknowledgement.CommandId);
        Assert.Equal(command.RunId, acknowledgement.RunId);
        Assert.Equal(participant.WorkerSessionId, acknowledgement.WorkerSessionId);
        Assert.Equal(command.CommandId, acknowledgement.Status.CommandId);
        Assert.Equal(command.RunId, acknowledgement.Status.RunId);
        Assert.Equal(participant.WorkerSessionId, acknowledgement.Status.WorkerSessionId);
        Assert.Equal(command.Role, acknowledgement.Status.Role);
        Assert.Equal(DadModuleId.PremadeDuty, acknowledgement.Status.ModuleId);
        Assert.Equal(DadWorkerExecutionState.Accepted, acknowledgement.Status.State);
        Assert.False(acknowledgement.Status.IsTerminal);
        Assert.True(DadDroppedPeerContinuationRules.MatchesExactCommand(
            participant,
            command,
            acknowledgement.Status));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void InvalidModuleIndexStaysNoneAndFailsClosed(int moduleIndex)
    {
        var participant = Participant();
        var command = Command(participant, moduleIndex);

        var acknowledgement = DadWorkerStatusPollingRules.BuildQueuedAcknowledgement(
            command,
            participant.WorkerSessionId,
            Now);

        Assert.Equal(DadModuleId.None, acknowledgement.Status.ModuleId);
        Assert.False(DadDroppedPeerContinuationRules.MatchesExactCommand(
            participant,
            command,
            acknowledgement.Status));
    }

    [Fact]
    public void LiveStatusAlwaysWinsOverPendingCache()
    {
        var live = Status("live", DadWorkerExecutionState.WaitingForQueue);
        var cached = Status("cached", DadWorkerExecutionState.Accepted);

        var selected = DadWorkerStatusPollingRules.SelectRemoteStatus(
            live,
            cached,
            exactRequestPending: true,
            authenticatedRouteRoutable: true);

        Assert.Same(live, selected);
    }

    [Fact]
    public void PendingRoutableRequestReturnsIsolatedCacheClone()
    {
        var cached = Status("cached", DadWorkerExecutionState.WaitingForQueue);
        cached.StepResult.Summary = "original step";

        var selected = DadWorkerStatusPollingRules.SelectRemoteStatus(
            liveStatus: null,
            cached,
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
    public void DisconnectedOrNonPendingRequestReturnsNull(bool pending, bool routable)
    {
        var selected = DadWorkerStatusPollingRules.SelectRemoteStatus(
            liveStatus: null,
            Status("cached", DadWorkerExecutionState.Accepted),
            exactRequestPending: pending,
            authenticatedRouteRoutable: routable);

        Assert.Null(selected);
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

    private static DadWorkerExecutionCommand Command(DadParticipantSnapshot participant, int moduleIndex)
        => new()
        {
            CommandId = "command-x",
            RunId = "run-x",
            ModuleIndex = moduleIndex,
            Role = DadWorkerExecutionRole.Participant,
            Plan = new DadRunPlan
            {
                Request = new DadRunRequest { RequestId = "run-x" },
                Modules = [new DadPlannedModuleExecution { ModuleId = DadModuleId.PremadeDuty }],
            },
            Participants = [participant.Clone()],
        };

    private static DadWorkerExecutionStatus Status(string summary, DadWorkerExecutionState state)
        => new()
        {
            CommandId = "command-x",
            RunId = "run-x",
            WorkerSessionId = new DadWorkerSessionId("worker-x"),
            Role = DadWorkerExecutionRole.Participant,
            ModuleId = DadModuleId.PremadeDuty,
            State = state,
            Summary = summary,
        };
}
