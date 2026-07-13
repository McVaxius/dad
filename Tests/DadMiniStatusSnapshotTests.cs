using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadMiniStatusSnapshotTests
{
    [Fact]
    public void BuilderProjectsCachedRunSchedulerQueueSlotsAndStaleRoute()
    {
        var run = new DadRunResult
        {
            RequestId = "run-1",
            Status = DadRunStatus.Running,
            Phase = DadRunPhase.InDutyOrTask,
            ModuleId = DadModuleId.Duty,
            ActiveTaskName = "Duty",
            ActiveTaskIndex = 1,
            TotalTaskCount = 3,
            Summary = "Running cached duty.",
        };
        var slot = new DadSchedulerSlotState
        {
            SlotId = "Slot2",
            RequiredCharacterKey = new DadCharacterKey("Target@World"),
            MatchedWorkerSessionId = new DadWorkerSessionId("worker-x"),
            ClientConnected = true,
            CorrectCharacter = false,
            TakeoverPhase = DadWakeTakeoverPhase.Prepared,
            TimeoutStage = DadWakeTimeoutStage.Participant,
            Summary = "Waiting for target character.",
        };
        var queue = new DadSchedulerQueueSnapshot
        {
            ActiveState = new DadSchedulerPresetState
            {
                JobId = "active-job",
                Phase = DadSchedulerPresetPhase.WaitingForHeartbeat,
                Slots = [slot],
            },
            PendingJobs =
            [
                new DadScheduledCrewJob { JobId = "first", Priority = 10, RequestedBy = "owner-a" },
                new DadScheduledCrewJob { JobId = "second", Priority = 5, RequestedBy = "owner-b" },
            ],
        };
        var transport = new DadPeerTransportSnapshot
        {
            ConnectedPeerCount = 1,
            ConnectionStatus = "Connected",
            LastTransportTimeoutSummary = "Cached route timed out.",
            KnownParticipants =
            [
                new DadParticipantSnapshot
                {
                    WorkerSessionId = new DadWorkerSessionId("worker-x"),
                    State = DadParticipantState.Ready,
                },
            ],
        };
        var authority = new DadAuthorityViewState
        {
            Kind = DadAuthorityViewKind.RemoteStale,
            HasRemoteAuthority = true,
            IsFresh = false,
            StateText = "Remote stale",
        };

        var snapshot = DadMiniStatusSnapshotBuilder.Build(
            false,
            authority,
            transport,
            run,
            queue,
            new DadScheduleSnapshot(),
            new DadWorkerExecutionStatus { State = DadWorkerExecutionState.Running },
            new DadParticipantSnapshot(),
            null);

        Assert.Equal("Client", snapshot.RoleText);
        Assert.Equal(DadAuthorityViewKind.RemoteStale, snapshot.Authority.Kind);
        Assert.Equal("Cached route timed out.", snapshot.TransportError);
        Assert.Equal("run-1", snapshot.VisibleRun.RequestId);
        Assert.Equal("active-job", snapshot.SchedulerQueue.ActiveState.JobId);
        Assert.Equal("Slot2", snapshot.SchedulerQueue.ActiveState.Slots.Single().SlotId);
        Assert.Equal(["first", "second"], snapshot.SchedulerQueue.PendingJobs.Select(static job => job.JobId));
        Assert.Single(snapshot.ConnectedParticipants);
    }

    [Fact]
    public void BuilderSelectsMostRecentTerminalFailureAndStopMatrix()
    {
        var failure = DadRunResult.Rejected(null, "Latest failure");
        var stop = new DadStopAllStatus
        {
            OperationId = "stop-1",
            Workers =
            [
                new DadStopAllWorkerResult
                {
                    WorkerSessionId = new DadWorkerSessionId("worker-x"),
                    State = DadStopAllWorkerState.TimedOut,
                },
            ],
        };

        var snapshot = DadMiniStatusSnapshotBuilder.Build(
            true,
            new DadAuthorityViewState(),
            new DadPeerTransportSnapshot(),
            DadRunResult.Idle(),
            new DadSchedulerQueueSnapshot(),
            new DadScheduleSnapshot(),
            new DadWorkerExecutionStatus(),
            new DadParticipantSnapshot(),
            stop,
            [failure]);

        Assert.Equal("Latest failure", snapshot.RecentFailure);
        Assert.Equal("stop-1", snapshot.LastStopAll?.OperationId);
        Assert.Equal(DadStopAllWorkerState.TimedOut, snapshot.LastStopAll?.Workers.Single().State);
    }
}
