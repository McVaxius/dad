using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadRemoteAssignmentTrackerTests
{
    [Fact]
    public void InitialNullTransportResultIsPendingNotRejected()
    {
        var tracker = new DadRemoteAssignmentTracker();
        var state = tracker.MarkPending("run", Slot());

        Assert.Equal(DadRemoteAssignmentDisposition.Pending, state.Disposition);
        Assert.Contains("pending", state.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(tracker.IsAccepted("run", Slot()));
    }

    [Fact]
    public void ExactAcceptedAcknowledgementSticksWhileReadinessBlockerRemains()
    {
        var tracker = new DadRemoteAssignmentTracker();
        var response = Response();
        response.PostArReady = false;
        response.State = DadParticipantState.WaitingForPostArReady;
        response.BlockerSummary = "Waiting for post-AR readiness.";
        response.Snapshot.PostArReady = false;
        response.Snapshot.State = DadParticipantState.WaitingForPostArReady;

        var accepted = tracker.Observe("run", Slot(), response, DateTime.UtcNow);
        var afterAnotherPendingPoll = tracker.MarkPending("run", Slot());

        Assert.Equal(DadRemoteAssignmentDisposition.Accepted, accepted.Disposition);
        Assert.Equal(DadRemoteAssignmentDisposition.Accepted, afterAnotherPendingPoll.Disposition);
        Assert.True(tracker.IsAccepted("run", Slot()));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("worker")]
    [InlineData("slot")]
    [InlineData("account")]
    [InlineData("character")]
    [InlineData("content")]
    public void InvalidAcknowledgementIdentityIsRejected(string mismatch)
    {
        var tracker = new DadRemoteAssignmentTracker();
        var response = Response();
        switch (mismatch)
        {
            case "run":
                response.RunId = "stale-run";
                break;
            case "worker":
                response.WorkerSessionId = new DadWorkerSessionId("stale-worker");
                break;
            case "slot":
                response.Snapshot.AssignedSlotId = "Slot1";
                break;
            case "account":
                response.Snapshot.ManagedAccountKey = new DadAccountKey("wrong-account");
                break;
            case "character":
                response.CharacterKey = new DadCharacterKey("Wrong Character@Excalibur");
                break;
            case "content":
                response.Snapshot.Character.ContentId = 999;
                break;
        }

        var state = tracker.Observe("run", Slot(), response, DateTime.UtcNow);

        Assert.Equal(DadRemoteAssignmentDisposition.Rejected, state.Disposition);
        Assert.Contains("rejected", state.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewAttemptAndExplicitClearDiscardStickyAcceptance()
    {
        var tracker = new DadRemoteAssignmentTracker();
        tracker.Observe("run", Slot(), Response(), DateTime.UtcNow);

        tracker.BeginAttempt("new-run");
        Assert.False(tracker.IsAccepted("run", Slot()));

        tracker.Observe("new-run", Slot(), Response("new-run"), DateTime.UtcNow);
        tracker.Clear();
        Assert.False(tracker.IsAccepted("new-run", Slot()));
    }

    private static DadFrozenRunSlot Slot()
        => new()
        {
            SlotId = "Slot2",
            AccountKey = new DadAccountKey("account-x"),
            CharacterKey = new DadCharacterKey("Hard'carry Gray'parse@Excalibur"),
            ContentId = 200,
            WorkerSessionId = new DadWorkerSessionId("worker-x"),
        };

    private static DadParticipantReadyDto Response(string runId = "run")
        => new()
        {
            RunId = runId,
            WorkerSessionId = new DadWorkerSessionId("worker-x"),
            CharacterKey = new DadCharacterKey("Hard'carry Gray'parse@Excalibur"),
            AcceptedAssignment = true,
            PostArReady = true,
            State = DadParticipantState.Ready,
            Snapshot = new DadParticipantSnapshot
            {
                RunId = runId,
                AssignedSlotId = "Slot2",
                WorkerSessionId = new DadWorkerSessionId("worker-x"),
                ManagedAccountKey = new DadAccountKey("account-x"),
                ActiveCharacterKey = new DadCharacterKey("Hard'carry Gray'parse@Excalibur"),
                Character = new DadAcquiredCharacter
                {
                    CharacterKey = "Hard'carry Gray'parse@Excalibur",
                    ContentId = 200,
                },
                IsAvailable = true,
                IsEligibleForRun = true,
                PostArReady = true,
                State = DadParticipantState.Ready,
            },
        };
}
