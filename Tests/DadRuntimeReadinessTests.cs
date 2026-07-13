using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadRuntimeReadinessTests
{
    [Fact]
    public void RuntimeTransitionsCreateExactlyOneEdgeEach()
    {
        AssertParticipantEdge(static participant => participant.IsAvailable = false);
        AssertParticipantEdge(static participant => participant.State = DadParticipantState.WaitingForPostArReady);
        AssertParticipantEdge(static participant => participant.ActiveCharacterKey = new DadCharacterKey("Other@World"));
        AssertParticipantEdge(static participant => participant.PostArReady = false);
        AssertParticipantEdge(static participant => participant.WorldReadyStable = false);
        AssertParticipantEdge(static participant => participant.AutoRetainerBusy = true);
        AssertParticipantEdge(static participant => participant.AutoRetainerMultiModeEnabled = true);
        AssertParticipantEdge(static participant => participant.ExternalAutomationHeld = true);
        AssertParticipantEdge(static participant => participant.ExternalAutomationActivity = "OceanFishing");
        AssertParticipantEdge(static participant => participant.ExternalAutomationState = "Running");

        AssertLocalExtensionEdge(autoRetainerSuppressed: true);
        AssertLocalExtensionEdge(dadOwnsSuppression: true);
        AssertLocalExtensionEdge(dadOwnsCharacterPostprocess: true);

        var participant = Participant();
        var takeover = Takeover();
        var tracker = Seed(DadRuntimeReadinessSignature.Create(participant, takeover: takeover));
        takeover.Phase = DadWakeTakeoverPhase.PostprocessOwned;
        var changed = DadRuntimeReadinessSignature.Create(participant, takeover: takeover);
        Assert.True(tracker.Observe(changed, out var revision));
        Assert.Equal(1, revision);
        Assert.False(tracker.Observe(changed, out var duplicateRevision));
        Assert.Equal(revision, duplicateRevision);
    }

    [Fact]
    public void DiagnosticTextWarningsAndTimestampsDoNotCreateEdges()
    {
        var participant = Participant();
        var takeover = Takeover();
        var original = DadRuntimeReadinessSignature.Create(
            participant,
            suppressionReadable: true,
            takeover: takeover);

        participant.LastHeartbeatUtc = participant.LastHeartbeatUtc.AddHours(1);
        participant.StatusText = "new display status";
        participant.Warnings.Add("historical warning");
        participant.ExternalAutomationSummary = "new VERMAXION display summary";
        takeover.ReadyUtc = DateTime.UtcNow;
        takeover.VermaxionReservationUpdatedAtUtc = DateTime.UtcNow;
        takeover.Summary = "new takeover display summary";
        takeover.BlockedReason = "historical diagnostic";

        var diagnosticOnly = DadRuntimeReadinessSignature.Create(
            participant,
            suppressionReadable: true,
            takeover: takeover);

        Assert.Equal(original, diagnosticOnly);
        var tracker = Seed(original);
        Assert.False(tracker.Observe(diagnosticOnly, out var revision));
        Assert.Equal(0, revision);
    }

    [Fact]
    public void SchedulerWakeMakesOnlyMatchingTakeoverStatusImmediatelyDue()
    {
        var future = DateTime.UtcNow.AddSeconds(5);
        var slots = new List<DadSchedulerSlotState>
        {
            new()
            {
                MatchedWorkerSessionId = new DadWorkerSessionId("worker-a"),
                NextTakeoverStatusCheckUtc = future,
            },
            new()
            {
                MatchedWorkerSessionId = new DadWorkerSessionId("worker-b"),
                NextTakeoverStatusCheckUtc = future,
            },
        };

        var changed = DadSchedulerRuntimeWakeRules.MakeMatchingTakeoverChecksDue(
            slots,
            new DadWorkerSessionId("WORKER-A"));

        Assert.Equal(1, changed);
        Assert.Equal(DateTime.MinValue, slots[0].NextTakeoverStatusCheckUtc);
        Assert.Equal(future, slots[1].NextTakeoverStatusCheckUtc);
    }

    [Fact]
    public void RuntimeOnlyScheduledJobStartsResolvingEvenWhenSlotsAreAlreadyReady()
    {
        Assert.Equal(
            DadSchedulerPresetPhase.Resolving,
            DadSchedulerRuntimeWakeRules.ResolveInitialPhase(
                plannerCanStart: false,
                plannerCanSchedule: true,
                slotsReadyToStart: true));
        Assert.Equal(
            DadSchedulerPresetPhase.ReadyToStart,
            DadSchedulerRuntimeWakeRules.ResolveInitialPhase(
                plannerCanStart: true,
                plannerCanSchedule: true,
                slotsReadyToStart: true));
    }

    private static void AssertParticipantEdge(Action<DadParticipantSnapshot> mutate)
    {
        var participant = Participant();
        var tracker = Seed(DadRuntimeReadinessSignature.Create(participant));
        mutate(participant);
        var changed = DadRuntimeReadinessSignature.Create(participant);
        Assert.True(tracker.Observe(changed, out var revision));
        Assert.Equal(1, revision);
        Assert.False(tracker.Observe(changed, out var duplicateRevision));
        Assert.Equal(revision, duplicateRevision);
    }

    private static void AssertLocalExtensionEdge(
        bool autoRetainerSuppressed = false,
        bool dadOwnsSuppression = false,
        bool dadOwnsCharacterPostprocess = false)
    {
        var participant = Participant();
        var tracker = Seed(DadRuntimeReadinessSignature.Create(participant));
        var changed = DadRuntimeReadinessSignature.Create(
            participant,
            suppressionReadable: true,
            autoRetainerSuppressed,
            dadOwnsSuppression,
            dadOwnsCharacterPostprocess);
        Assert.True(tracker.Observe(changed, out var revision));
        Assert.Equal(1, revision);
        Assert.False(tracker.Observe(changed, out _));
    }

    private static DadRuntimeReadinessTracker Seed(DadRuntimeReadinessSignature signature)
    {
        var tracker = new DadRuntimeReadinessTracker();
        Assert.False(tracker.Observe(signature, out var revision));
        Assert.Equal(0, revision);
        return tracker;
    }

    private static DadParticipantSnapshot Participant()
        => new()
        {
            ManagedAccountKey = new DadAccountKey("account-a"),
            ActiveCharacterKey = new DadCharacterKey("Character@World"),
            IsAvailable = true,
            IsEligibleForRun = true,
            PostArReady = true,
            WorldReadyStable = true,
            AutoRetainerAvailable = true,
            ExternalAutomationActivity = "Idle",
            ExternalAutomationState = "Idle",
        };

    private static DadWakeTakeoverResultDto Takeover()
        => new()
        {
            OperationToken = "operation-a",
            Status = DadWakeTakeoverStatus.Pending,
            Stage = DadWakeTakeoverStage.AwaitingArHook,
            Phase = DadWakeTakeoverPhase.AwaitingArHook,
            VermaxionReservationState = DadVermaxionReservationState.Pending,
        };
}
