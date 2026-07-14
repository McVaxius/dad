using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadWakeTakeoverServiceTests
{
    [Fact]
    public void TransientIdleWithoutOwnedLeaseSendsNoCommands()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.Snapshot.AutoRetainerBusy = false;
        var service = new DadWakeTakeoverService(target);

        var result = service.Handle(Request());

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, result.Phase);
        Assert.Equal(["Arm"], target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void AutoRetainerBusyWaitsBeforeReservationAndResumesWithoutMutation()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.Snapshot.AutoRetainerBusy = true;
        var service = new DadWakeTakeoverService(target);

        var waiting = service.Handle(Request());
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, waiting.Phase);
        Assert.Equal(DadWakeTakeoverStage.WaitingForAutoRetainer, waiting.Stage);
        Assert.Equal(0, target.ReserveCount);
        Assert.Empty(target.Actions);

        target.Snapshot.AutoRetainerBusy = false;
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, service.Handle(StatusRequest()).Phase);
        Assert.Equal(1, target.ReserveCount);
        Assert.Equal(["Arm"], target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Theory]
    [InlineData(false, true, DadWakeTakeoverStage.WaitingForClient)]
    [InlineData(true, false, DadWakeTakeoverStage.WaitingForPostArReady)]
    public void UnsafeWorldWaitsBeforeAnyTakeoverMutation(
        bool participantAvailable,
        bool worldReadyStable,
        DadWakeTakeoverStage expectedStage)
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.Snapshot.Participant.IsAvailable = participantAvailable;
        target.Snapshot.Participant.WorldReadyStable = worldReadyStable;
        var service = new DadWakeTakeoverService(target);

        var waiting = service.Handle(Request());
        service.Update();
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, waiting.Phase);
        Assert.Equal(expectedStage, waiting.Stage);
        Assert.Contains("no timeout", waiting.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, target.ReserveCount);
        Assert.Empty(target.Actions);
    }

    [Fact]
    public void SummoningBellUnsafeStateBlocksReservationSuppressionResetAndRelogUntilItClears()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        // DadPresenceService maps ConditionFlag.OccupiedSummoningBell to this shared
        // WorldReadyStable safety gate.
        target.Snapshot.Participant.WorldReadyStable = false;
        var service = new DadWakeTakeoverService(target, clock.UtcNow);

        service.Handle(Request());
        service.Update();
        service.Update();

        Assert.Equal(0, target.ReserveCount);
        Assert.DoesNotContain("AcquireSuppression", target.Actions);
        Assert.DoesNotContain("DisableAutoRetainer", target.Actions);
        Assert.DoesNotContain("ResetAutoRetainer", target.Actions);
        Assert.DoesNotContain("RelogCharacter", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);

        target.Snapshot.Participant.WorldReadyStable = true;
        Prepare(service, target);
        Assert.Equal(1, target.ReserveCount);
        Assert.Contains("AcquireSuppression", target.Actions);

        ExecuteGo(service, clock, DadWakeCommitKind.Reset);
        ExecuteGo(service, clock, DadWakeCommitKind.Relog);

        Assert.Contains("SetMultiMode:False", target.Actions);
        Assert.Contains("DisableAutoRetainer", target.Actions);
        Assert.Contains("ResetAutoRetainer", target.Actions);
        Assert.Contains("RelogCharacter", target.Actions);
    }

    [Fact]
    public void ExistingVermaxionHoldWaitsBeforeReservationOrCallbackMutation()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.Reservation.State = DadVermaxionReservationState.Pending;
        target.Reservation.OperationToken = "another-operation";
        target.Reservation.Summary = "Another automation owner is active.";
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Busy);
        var service = new DadWakeTakeoverService(target);

        var waiting = service.Handle(Request());
        service.Update();

        Assert.Equal(DadWakeTakeoverStage.WaitingForExternalAutomation, waiting.Stage);
        Assert.Contains("no timeout", waiting.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, target.ReserveCount);
        Assert.Empty(target.Actions);
    }

    [Fact]
    public void ForcedPreReservationRefreshSeesNewVermaxionHoldWithoutMutation()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.OnCapture = force =>
        {
            if (force)
                target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Busy);
        };
        var service = new DadWakeTakeoverService(target);

        var waiting = service.Handle(Request());

        Assert.Contains(true, target.CaptureForceFlags);
        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, waiting.Phase);
        Assert.Equal(0, target.ReserveCount);
        Assert.Empty(target.Actions);
    }

    [Fact]
    public void ForcedCallbackRefreshSeesNewVermaxionHoldBeforeSuppression()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        service.Handle(Request());
        target.Snapshot.DadOwnsCharacterPostprocess = true;
        target.OnCapture = force =>
        {
            if (force)
                target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Busy);
        };

        service.OnCharacterPostprocessReady();

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, service.GetActiveStatus()!.Phase);
        Assert.DoesNotContain("AcquireSuppression", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void SafetyLossImmediatelyAfterSuppressionAcquisitionRollsBackWithoutCommands()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        service.Handle(Request());
        target.Snapshot.DadOwnsCharacterPostprocess = true;
        target.OnSuppressionAcquired = () => target.Snapshot.Participant.WorldReadyStable = false;

        service.OnCharacterPostprocessReady();
        var waiting = service.GetActiveStatus();

        Assert.NotNull(waiting);
        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, waiting.Phase);
        Assert.Contains("Finish:Stop", target.Actions);
        Assert.Contains("ReleaseSuppression", target.Actions);
        Assert.Contains("ReleaseReservation", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void SafetyLossAfterCallbackArmReleasesReservationAndDisarmsWithoutCommands()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, service.Handle(Request()).Phase);
        Assert.Contains("Arm", target.Actions);

        target.Snapshot.Participant.WorldReadyStable = false;
        service.Update();

        var waiting = service.GetActiveStatus();
        Assert.NotNull(waiting);
        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, waiting.Phase);
        Assert.Contains("Finish:Stop", target.Actions);
        Assert.Contains("ReleaseReservation", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void PostprocessOwnedSafetyLossRollsBackOnWorkerUpdate()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        service.Handle(Request());
        target.Snapshot.DadOwnsCharacterPostprocess = true;
        service.OnCharacterPostprocessReady();
        Assert.Equal(DadWakeTakeoverPhase.PostprocessOwned, service.GetActiveStatus()!.Phase);

        target.Snapshot.Participant.WorldReadyStable = false;
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, service.GetActiveStatus()!.Phase);
        Assert.Contains("Finish:Stop", target.Actions);
        Assert.Contains("ReleaseSuppression", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void VermaxionPendingAtCallbackYieldsWithoutMutationAndRetriesFutureBoundary()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        service.Handle(Request());
        target.Snapshot.DadOwnsCharacterPostprocess = true;
        target.Reservation.State = DadVermaxionReservationState.Pending;
        target.Reservation.OperationToken = "scheduler-run";
        target.Reservation.VermaxionActivity = "CharacterPostprocessPending";

        service.OnCharacterPostprocessReady();

        Assert.Contains("Finish:Stop", target.Actions);
        Assert.Contains("ReleaseReservation", target.Actions);
        Assert.DoesNotContain("AcquireSuppression", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, service.Handle(StatusRequest()).Phase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void UnreadableOrExternallyOwnedSuppressionYieldsCallback(bool readable, bool suppressed)
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        service.Handle(Request());
        target.Snapshot.DadOwnsCharacterPostprocess = true;
        target.Snapshot.SuppressionReadable = readable;
        target.Snapshot.AutoRetainerSuppressed = suppressed;

        service.OnCharacterPostprocessReady();

        Assert.Contains("Finish:Stop", target.Actions);
        Assert.Contains("ReleaseReservation", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void ResetAndRelogExecuteOnlyAtSharedGoTimestamps()
    {
        var clock = new TestClock();
        var firstTarget = FakeTarget.Valid(wrongCharacter: true);
        var secondTarget = FakeTarget.Valid(wrongCharacter: true);
        var first = new DadWakeTakeoverService(firstTarget, clock.UtcNow);
        var second = new DadWakeTakeoverService(secondTarget, clock.UtcNow);
        Prepare(first, firstTarget);
        Prepare(second, secondTarget);
        var resetAt = clock.Now.AddSeconds(5);

        Assert.Equal(DadWakeTakeoverPhase.ResetCommitted, first.Handle(Go(DadWakeCommitKind.Reset, resetAt)).Phase);
        Assert.Equal(DadWakeTakeoverPhase.ResetCommitted, second.Handle(Go(DadWakeCommitKind.Reset, resetAt)).Phase);
        Assert.DoesNotContain(firstTarget.Actions, IsMutation);
        Assert.DoesNotContain(secondTarget.Actions, IsMutation);

        clock.Advance(TimeSpan.FromSeconds(5));
        first.Update();
        second.Update();
        Assert.Equal(["SetMultiMode:False", "DisableAutoRetainer", "ResetAutoRetainer"], firstTarget.Actions.Where(IsMutation));
        Assert.Equal(["SetMultiMode:False", "DisableAutoRetainer", "ResetAutoRetainer"], secondTarget.Actions.Where(IsMutation));

        var relogAt = clock.Now.AddSeconds(5);
        first.Handle(Go(DadWakeCommitKind.Relog, relogAt));
        second.Handle(Go(DadWakeCommitKind.Relog, relogAt));
        clock.Advance(TimeSpan.FromSeconds(5));
        first.Update();
        second.Update();
        Assert.Equal(relogAt, first.Handle(StatusRequest()).ExecutionTimeUtc);
        Assert.Equal(relogAt, second.Handle(StatusRequest()).ExecutionTimeUtc);
        Assert.Equal(1, firstTarget.Actions.Count(static action => action == "RelogCharacter"));
        Assert.Equal(1, secondTarget.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Fact]
    public void AcceptedRelogIsNeverDuplicatedInFlightAndAProvenNoEffectStartsANewEpoch()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        var waiting = StartRelog(service, target, clock);
        Assert.NotNull(waiting.RelogIssuedUtc);

        for (var update = 0; update < 3; update++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            service.Update();
        }

        var nextEpoch = service.Handle(StatusRequest());
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, nextEpoch.Phase);
        Assert.Contains("epoch 2", nextEpoch.Summary, StringComparison.OrdinalIgnoreCase);

        clock.Advance(TimeSpan.FromSeconds(5));
        StartRelog(service, target, clock, expectedRelogCount: 2);
        Assert.Equal(2, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RelogIsNotRepeatedWhileAutoRetainerOrLifestreamIsBusy(bool autoRetainerBusy, bool lifestreamBusy)
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        target.Snapshot.AutoRetainerBusy = autoRetainerBusy;
        target.Snapshot.LifestreamBusy = lifestreamBusy;

        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();

        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, service.Handle(StatusRequest()).Phase);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void RelogIsNotRepeatedAfterCharacterDisappearsOrWorldBecomesUnstable(bool characterAvailable, bool worldReadyStable)
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        target.Snapshot.Participant.IsAvailable = characterAvailable;
        target.Snapshot.Participant.WorldReadyStable = worldReadyStable;

        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();

        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public void RelogIsNotRepeatedWhenMultiModeOrSuppressionStateDrifts(
        bool multiModeEnabled,
        bool ownsSuppression,
        bool autoRetainerSuppressed)
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        target.Snapshot.MultiModeEnabled = multiModeEnabled;
        target.Snapshot.DadOwnsSuppression = ownsSuppression;
        target.Snapshot.AutoRetainerSuppressed = autoRetainerSuppressed;

        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();

        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Fact]
    public void TargetCharacterBecomingActiveCompletesWithoutRepeatingRelog()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        target.Snapshot.CorrectCharacter = true;
        target.Snapshot.Participant.ActiveCharacterKey = new DadCharacterKey("Target Character@World");

        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();

        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        Assert.Equal(DadWakeTakeoverPhase.Ready, service.Handle(StatusRequest()).Phase);
    }

    [Fact]
    public void UnsafeCommittedRelogWaitsThenIssuesExactlyOnceWhenSafe()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);
        ExecuteGo(service, clock, DadWakeCommitKind.Reset);
        var relogAt = clock.Now.AddSeconds(5);
        service.Handle(Go(DadWakeCommitKind.Relog, relogAt));
        target.Snapshot.Participant.WorldReadyStable = false;

        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();
        Assert.Equal(DadWakeTakeoverPhase.RelogCommitted, service.GetActiveStatus()!.Phase);
        Assert.DoesNotContain("RelogCharacter", target.Actions);

        target.Snapshot.Participant.WorldReadyStable = true;
        service.Update();
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, service.GetActiveStatus()!.Phase);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Fact]
    public void WorldReadyDestinationDoesNotDependOnSuppressionSensitivePostArReady()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        target.Snapshot.CorrectCharacter = true;
        target.Snapshot.PostArReady = false;
        target.Snapshot.Participant.PostArReady = false;
        target.Snapshot.Participant.WorldReadyStable = true;
        target.Snapshot.Participant.ActiveCharacterKey = new DadCharacterKey("Target Character@World");

        service.Update();

        var ready = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverPhase.Ready, ready.Phase);
        Assert.False(ready.PostArReady);
        Assert.True(ready.Snapshot.WorldReadyStable);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Fact]
    public void DestinationWaitsForCallbackCleanupThatFailedAtResetBoundary()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);
        target.FinishResults.Enqueue(false);
        target.FinishResults.Enqueue(false);

        ExecuteGo(service, clock, DadWakeCommitKind.Reset);
        Assert.Equal(DadWakeTakeoverPhase.ResetCommitted, service.GetActiveStatus()!.Phase);
        Assert.Equal(1, target.Actions.Count(static action => action == "Finish:Stop"));

        var prematureRelog = service.Handle(Go(DadWakeCommitKind.Relog, clock.Now.AddSeconds(5)));
        Assert.Equal(DadWakeTakeoverPhase.ResetCommitted, prematureRelog.Phase);
        Assert.Equal(DadWakeAcknowledgementState.Rejected, prematureRelog.AcknowledgementState);
        Assert.DoesNotContain("RelogCharacter", target.Actions);
        Assert.Equal(2, target.Actions.Count(static action => action == "Finish:Stop"));

        service.Update();
        Assert.Equal(DadWakeTakeoverPhase.ResetVerified, service.GetActiveStatus()!.Phase);
        Assert.Equal(3, target.Actions.Count(static action => action == "Finish:Stop"));

        ExecuteGo(service, clock, DadWakeCommitKind.Relog);
        target.Snapshot.CorrectCharacter = true;
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.Ready, service.Handle(StatusRequest()).Phase);
        Assert.Equal(3, target.Actions.Count(static action => action == "Finish:Stop"));
    }

    [Fact]
    public void DestinationRevalidatesSafetyAfterLeaseRelease()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        target.Snapshot.CorrectCharacter = true;
        target.Snapshot.Participant.ActiveCharacterKey = new DadCharacterKey("Target Character@World");
        target.OnSuppressionReleased = () => target.Snapshot.Participant.WorldReadyStable = false;

        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, service.GetActiveStatus()!.Phase);
        target.OnSuppressionReleased = null;
        target.Snapshot.Participant.WorldReadyStable = true;
        service.Update();
        Assert.Equal(DadWakeTakeoverPhase.Ready, service.Handle(StatusRequest()).Phase);
    }

    [Fact]
    public void OnePreparedClientCannotCommitBeforeCrewBarrier()
    {
        Assert.False(DadWakeCrewBarrierPolicy.CanCommitReset([
            DadWakeTakeoverPhase.Prepared,
            DadWakeTakeoverPhase.AwaitingArHook,
        ]));
        Assert.True(DadWakeCrewBarrierPolicy.CanCommitReset([
            DadWakeTakeoverPhase.Prepared,
            DadWakeTakeoverPhase.Prepared,
        ]));
        Assert.False(DadWakeCrewBarrierPolicy.CanCommitRelog([
            DadWakeTakeoverPhase.ResetVerified,
            DadWakeTakeoverPhase.ResetCommitted,
        ]));
        Assert.True(DadWakeCrewBarrierPolicy.CanCommitRelog([
            DadWakeTakeoverPhase.ResetVerified,
            DadWakeTakeoverPhase.ResetVerified,
        ]));
    }

    [Fact]
    public void CorrectCharacterResetsButNeverRelogs()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: false);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);
        ExecuteGo(service, clock, DadWakeCommitKind.Reset);
        ExecuteGo(service, clock, DadWakeCommitKind.Relog);

        Assert.DoesNotContain("RelogCharacter", target.Actions);
        Assert.Equal(DadWakeTakeoverPhase.Ready, service.Handle(StatusRequest()).Phase);
        Assert.Contains("ReleaseSuppression", target.Actions);
        Assert.Contains("ReleaseReservation", target.Actions);
    }

    [Fact]
    public void DuplicateGoMessagesAreIdempotent()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);
        var resetAt = clock.Now.AddSeconds(5);
        service.Handle(Go(DadWakeCommitKind.Reset, resetAt));
        service.Handle(Go(DadWakeCommitKind.Reset, resetAt));
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();
        service.Handle(Go(DadWakeCommitKind.Reset, resetAt));

        Assert.Equal(1, target.Actions.Count(static action => action == "DisableAutoRetainer"));
        Assert.Equal(1, target.Actions.Count(static action => action == "ResetAutoRetainer"));
    }

    [Fact]
    public void SafetyLossBeforeResetGoRejectsCommitAndReleasesTemporaryState()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);
        target.Snapshot.Participant.WorldReadyStable = false;

        var rejected = service.Handle(Go(DadWakeCommitKind.Reset, clock.Now));

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, rejected.Phase);
        Assert.Equal(DadWakeAcknowledgementState.Rejected, rejected.AcknowledgementState);
        Assert.Contains("Finish:Stop", target.Actions);
        Assert.Contains("ReleaseSuppression", target.Actions);
        Assert.Contains("ReleaseReservation", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void SafetyLossAfterResetGoPreservesCommitUntilSafeAndExecutesOnce()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);
        var executeAt = clock.Now.AddSeconds(5);
        service.Handle(Go(DadWakeCommitKind.Reset, executeAt));
        target.Snapshot.Participant.WorldReadyStable = false;

        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();

        var delayed = service.GetActiveStatus();
        Assert.NotNull(delayed);
        Assert.Equal(DadWakeTakeoverPhase.ResetCommitted, delayed.Phase);
        Assert.Equal(executeAt, delayed.ExecutionTimeUtc);
        Assert.DoesNotContain(target.Actions, IsMutation);

        target.Snapshot.Participant.WorldReadyStable = true;
        service.Update();
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.ResetVerified, service.GetActiveStatus()!.Phase);
        Assert.Equal(1, target.Actions.Count(static action => action == "SetMultiMode:False"));
        Assert.Equal(1, target.Actions.Count(static action => action == "DisableAutoRetainer"));
        Assert.Equal(1, target.Actions.Count(static action => action == "ResetAutoRetainer"));
    }

    [Fact]
    public void SafetyLossBetweenResetSubstepsResumesWithoutRepeatingEarlierCommands()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);
        target.OnCommandExecuted = command =>
        {
            if (command == DadWakeTakeoverCommand.DisableAutoRetainer)
                target.Snapshot.Participant.WorldReadyStable = false;
        };

        service.Handle(Go(DadWakeCommitKind.Reset, clock.Now));

        Assert.Equal(DadWakeTakeoverPhase.ResetCommitted, service.GetActiveStatus()!.Phase);
        Assert.Equal(1, target.Actions.Count(static action => action == "SetMultiMode:False"));
        Assert.Equal(1, target.Actions.Count(static action => action == "DisableAutoRetainer"));
        Assert.DoesNotContain("ResetAutoRetainer", target.Actions);

        target.OnCommandExecuted = null;
        target.Snapshot.Participant.WorldReadyStable = true;
        service.Update();
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.ResetVerified, service.GetActiveStatus()!.Phase);
        Assert.Equal(1, target.Actions.Count(static action => action == "SetMultiMode:False"));
        Assert.Equal(1, target.Actions.Count(static action => action == "DisableAutoRetainer"));
        Assert.Equal(1, target.Actions.Count(static action => action == "ResetAutoRetainer"));
    }

    [Fact]
    public void CancelMessageWinsOverDueCommittedResetAndPreventsLaterCommands()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);
        service.Handle(Go(DadWakeCommitKind.Reset, clock.Now.AddSeconds(5)));
        clock.Advance(TimeSpan.FromSeconds(5));
        var cancel = Request();
        cancel.MessageKind = DadWakeTakeoverMessageKind.Cancel;

        var cancelled = service.Handle(cancel);
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.Cancelled, cancelled.Phase);
        Assert.DoesNotContain(target.Actions, IsMutation);
        Assert.Contains("ReleaseSuppression", target.Actions);
        Assert.Contains("ReleaseReservation", target.Actions);
    }

    [Fact]
    public void PreCommitCancellationReleasesOnlyDadOwnedLeasesWithoutCommands()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        Prepare(service, target);
        var cancel = Request();
        cancel.MessageKind = DadWakeTakeoverMessageKind.Cancel;

        var result = service.Handle(cancel);

        Assert.Equal(DadWakeTakeoverPhase.Cancelled, result.Phase);
        Assert.Contains("Finish:Stop", target.Actions);
        Assert.Contains("ReleaseSuppression", target.Actions);
        Assert.Contains("ReleaseReservation", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void CancellationDisarmsUncommittedCallbackBeforeOwnershipArrives()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        service.Handle(Request());
        var cancel = Request();
        cancel.MessageKind = DadWakeTakeoverMessageKind.Cancel;

        var result = service.Handle(cancel);
        service.OnCharacterPostprocessReady();

        Assert.Equal(DadWakeTakeoverPhase.Cancelled, result.Phase);
        Assert.Equal(2, target.Actions.Count(static action => action == "Finish:Stop"));
        Assert.Equal(1, target.Actions.Count(static action => action == "ReleaseReservation"));
        Assert.DoesNotContain("AcquireSuppression", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void CancellationRemainsPendingUntilEveryOwnedLeaseIsReleased()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        Prepare(service, target);
        target.SuppressionReleaseResults.Enqueue(false);
        target.ReservationReleaseResults.Enqueue(false);
        var cancel = Request();
        cancel.MessageKind = DadWakeTakeoverMessageKind.Cancel;

        var pending = service.Handle(cancel);

        Assert.Equal(DadWakeTakeoverPhase.Cancelled, pending.Phase);
        Assert.Equal(DadWakeAcknowledgementState.Pending, pending.AcknowledgementState);
        Assert.NotNull(service.GetActiveStatus());
        Assert.Equal(1, target.Actions.Count(static action => action == "Finish:Stop"));
        Assert.Equal(1, target.Actions.Count(static action => action == "ReleaseSuppression"));
        Assert.Equal(1, target.Actions.Count(static action => action == "ReleaseReservation"));

        var completed = service.Handle(cancel);

        Assert.Equal(DadWakeTakeoverPhase.Cancelled, completed.Phase);
        Assert.Equal(DadWakeAcknowledgementState.Executed, completed.AcknowledgementState);
        Assert.Null(service.GetActiveStatus());
        Assert.Equal(1, target.Actions.Count(static action => action == "Finish:Stop"));
        Assert.Equal(2, target.Actions.Count(static action => action == "ReleaseSuppression"));
        Assert.Equal(2, target.Actions.Count(static action => action == "ReleaseReservation"));
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void PendingCancellationSurvivesPruningBlocksConflictsAndQuiescesDueCommands()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);
        service.Handle(Go(DadWakeCommitKind.Reset, clock.Now.AddSeconds(5)));
        target.ReservationReleaseResults.Enqueue(false);
        var cancel = Request();
        cancel.MessageKind = DadWakeTakeoverMessageKind.Cancel;
        Assert.Equal(DadWakeAcknowledgementState.Pending, service.Handle(cancel).AcknowledgementState);
        clock.Advance(TimeSpan.FromHours(2));
        var conflicting = Request();
        conflicting.SchedulerRunId = "other-run";
        conflicting.OperationToken = "other-run";

        var blocked = service.Handle(conflicting);

        Assert.Equal(DadWakeTakeoverPhase.Blocked, blocked.Phase);
        Assert.Contains("conflicting", blocked.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(target.Actions, IsMutation);

        service.Update();
        var retry = service.Handle(conflicting);

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, retry.Phase);
        Assert.DoesNotContain("conflicting", retry.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void StopAllReportsCleanupPendingUntilCancellationCleanupCompletes()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        Prepare(service, target);
        target.ReservationReleaseResults.Enqueue(false);

        var stop = service.StopAll("Stop-all cleanup test.");

        Assert.Equal(1, stop.CancelledCount);
        Assert.True(stop.CleanupPending);
        Assert.Equal(DadWakeAcknowledgementState.Pending, service.GetActiveStatus()!.AcknowledgementState);

        service.Update();
        var completed = service.Handle(StatusRequest());

        Assert.Equal(DadWakeTakeoverPhase.Cancelled, completed.Phase);
        Assert.Equal(DadWakeAcknowledgementState.Executed, completed.AcknowledgementState);
        Assert.Null(service.GetActiveStatus());
        Assert.Equal(1, target.Actions.Count(static action => action == "Finish:Stop"));
        Assert.Equal(1, target.Actions.Count(static action => action == "ReleaseSuppression"));
        Assert.Equal(2, target.Actions.Count(static action => action == "ReleaseReservation"));
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void DisposeRetriesCleanupPendingCancellation()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        Prepare(service, target);
        target.FinishResults.Enqueue(false);
        target.SuppressionReleaseResults.Enqueue(false);
        target.ReservationReleaseResults.Enqueue(false);
        var cancel = Request();
        cancel.MessageKind = DadWakeTakeoverMessageKind.Cancel;
        Assert.Equal(DadWakeAcknowledgementState.Pending, service.Handle(cancel).AcknowledgementState);

        service.Dispose();

        Assert.Equal(2, target.Actions.Count(static action => action == "Finish:Stop"));
        Assert.Equal(2, target.Actions.Count(static action => action == "ReleaseSuppression"));
        Assert.Equal(2, target.Actions.Count(static action => action == "ReleaseReservation"));
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void StopAllCancelsPreCommitTakeoverAndReleasesOwnedLeases()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        Prepare(service, target);

        var result = service.StopAll("Stop-all test.");

        Assert.Equal(1, result.CancelledCount);
        Assert.Equal(0, result.PreservedCommittedCount);
        Assert.Contains("Finish:Stop", target.Actions);
        Assert.Contains("ReleaseSuppression", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void StopAllCancelsCommittedTakeoverAndPreventsFutureCommands()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);
        service.Handle(Go(DadWakeCommitKind.Reset, clock.Now.AddSeconds(5)));
        var result = service.StopAll("Stop-all test.");

        Assert.Equal(1, result.CancelledCount);
        Assert.Equal(0, result.PreservedCommittedCount);
        Assert.Equal(DadWakeTakeoverPhase.Cancelled, service.Handle(StatusRequest()).Phase);
        Assert.Contains("ReleaseSuppression", target.Actions);
        Assert.Contains("ReleaseReservation", target.Actions);

        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void TransportLossBeforeCommitDoesNotExpireLogicalOrder()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow, TimeSpan.FromSeconds(10));
        Prepare(service, target);

        clock.Advance(TimeSpan.FromSeconds(10));
        service.Update();
        var result = service.Handle(StatusRequest());

        Assert.Equal(DadWakeTakeoverPhase.Prepared, result.Phase);
        Assert.DoesNotContain("Finish:Stop", target.Actions);
        Assert.DoesNotContain("ReleaseSuppression", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void VermaxionGrantPreparesImmediatelyWithoutCharacterBoundary()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.Snapshot.MultiModeEnabled = false;
        target.Reservation.State = DadVermaxionReservationState.Pending;
        var service = new DadWakeTakeoverService(target);

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, service.Handle(Request()).Phase);
        Assert.DoesNotContain("Arm", target.Actions);

        target.Reservation.State = DadVermaxionReservationState.Granted;
        target.Reservation.UpdatedAtUtc = DateTime.UtcNow;
        target.Snapshot.VermaxionReservationState = DadVermaxionReservationState.Granted;
        service.OnVermaxionReservationGranted(target.Reservation);

        var result = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverPhase.Prepared, result.Phase);
        Assert.Contains("AcquireSuppression", target.Actions);
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void CachedVermaxionGrantAdvancesAfterAutoRetainerBecomesIdle()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.Snapshot.MultiModeEnabled = false;
        target.Snapshot.AutoRetainerBusy = true;
        target.Reservation.State = DadVermaxionReservationState.Pending;
        var service = new DadWakeTakeoverService(target);
        service.Handle(Request());

        target.Reservation.State = DadVermaxionReservationState.Granted;
        target.Snapshot.VermaxionReservationState = DadVermaxionReservationState.Granted;
        service.OnVermaxionReservationGranted(target.Reservation);
        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, service.Handle(StatusRequest()).Phase);
        Assert.DoesNotContain("AcquireSuppression", target.Actions);

        target.Snapshot.AutoRetainerBusy = false;
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.Prepared, service.Handle(StatusRequest()).Phase);
        Assert.Equal(1, target.Actions.Count(static action => action == "AcquireSuppression"));
    }

    [Fact]
    public void UnavailableV2WithCompleteIdleProofPreparesWithoutCharacterCallback()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        ConfigureUnavailableCompatibility(target);
        var service = new DadWakeTakeoverService(target);

        var result = service.Handle(Request());

        Assert.Equal(DadWakeTakeoverPhase.Prepared, result.Phase);
        Assert.Equal(DadWakeTakeoverStage.Prepared, result.Stage);
        Assert.Contains("Compatibility handoff: VERMAXION idle / AR idle", result.Summary);
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.Contains("AcquireSuppression", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
        Assert.Equal(DadWakeTakeoverStatus.Pending, result.Status);
        Assert.True(DadWakeCrewBarrierPolicy.CanCommitReset([result.Phase]));
        Assert.NotEqual(result.CharacterKey, result.Snapshot.ActiveCharacterKey);
    }

    [Fact]
    public void CompatibilityProofAcceptsAlreadyDadOwnedSuppression()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        ConfigureUnavailableCompatibility(target);
        target.Snapshot.DadOwnsSuppression = true;
        target.Snapshot.AutoRetainerSuppressed = true;
        var service = new DadWakeTakeoverService(target);

        var result = service.Handle(Request());

        Assert.Equal(DadWakeTakeoverPhase.Prepared, result.Phase);
        Assert.True(target.Snapshot.DadOwnsSuppression);
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Theory]
    [InlineData(CompatibilityFailure.LegacyBusy)]
    [InlineData(CompatibilityFailure.LegacyUnreadable)]
    [InlineData(CompatibilityFailure.LegacyNotLoaded)]
    [InlineData(CompatibilityFailure.AutoRetainerUnavailable)]
    [InlineData(CompatibilityFailure.AutoRetainerBusy)]
    [InlineData(CompatibilityFailure.MultiModeEnabled)]
    [InlineData(CompatibilityFailure.SuppressionUnreadable)]
    [InlineData(CompatibilityFailure.ExternalSuppression)]
    public void UnavailableV2FailsClosedWhenCompatibilityProofIsIncomplete(CompatibilityFailure failure)
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        ConfigureUnavailableCompatibility(target);
        ApplyCompatibilityFailure(target, failure);
        var service = new DadWakeTakeoverService(target);

        var result = service.Handle(Request());

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, result.Phase);
        var expectedStage = failure is CompatibilityFailure.AutoRetainerUnavailable or CompatibilityFailure.AutoRetainerBusy
            ? DadWakeTakeoverStage.WaitingForAutoRetainer
            : DadWakeTakeoverStage.WaitingForExternalAutomation;
        Assert.Equal(expectedStage, result.Stage);
        Assert.DoesNotContain("AcquireSuppression", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
        Assert.DoesNotContain("Arm", target.Actions);
    }

    [Theory]
    [InlineData(DadVermaxionReservationState.Pending)]
    [InlineData(DadVermaxionReservationState.Granting)]
    public void PendingAndGrantingNeverUseCompatibilityFallback(DadVermaxionReservationState state)
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        ConfigureUnavailableCompatibility(target);
        target.Reservation.State = state;
        var service = new DadWakeTakeoverService(target);

        var result = service.Handle(Request());
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, result.Phase);
        Assert.Equal(1, target.ReserveCount);
        Assert.DoesNotContain("ReleaseReservation", target.Actions);
        Assert.DoesNotContain("AcquireSuppression", target.Actions);
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void CompatibilityLossAfterGoReleasesOwnedStateAndStartsAnotherSafeEpoch()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        ConfigureUnavailableCompatibility(target);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Assert.Equal(DadWakeTakeoverPhase.Prepared, service.Handle(Request()).Phase);
        service.Handle(Go(DadWakeCommitKind.Reset, clock.Now.AddSeconds(5)));

        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Busy);
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();

        var waiting = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, waiting.Phase);
        Assert.Contains("ReleaseSuppression", target.Actions);
        Assert.Contains("ReleaseReservation", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);

        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Idle);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(DadWakeTakeoverPhase.Prepared, service.Handle(Request()).Phase);
        ExecuteGo(service, clock, DadWakeCommitKind.Reset);
        var recovered = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverPhase.ResetVerified, recovered.Phase);
        Assert.Equal("scheduler-run", recovered.OperationToken);
        Assert.Equal(1, target.Actions.Count(static action => action == "DisableAutoRetainer"));
        Assert.Equal(1, target.Actions.Count(static action => action == "ResetAutoRetainer"));
    }

    [Fact]
    public void FreshV2GrantPromotesPreparedCompatibilityHandoff()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        ConfigureUnavailableCompatibility(target);
        var service = new DadWakeTakeoverService(target);
        Assert.Equal(DadWakeTakeoverPhase.Prepared, service.Handle(Request()).Phase);

        Grant(target);
        service.OnVermaxionReservationGranted(target.Reservation);

        var promoted = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverPhase.Prepared, promoted.Phase);
        Assert.Equal(DadVermaxionReservationState.Granted, promoted.VermaxionReservationState);
        Assert.Contains("VERMAXION handoff granted", promoted.Summary);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void RepeatedUnavailableIdleCyclesRecoverWithoutRestartingOperation()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        ConfigureUnavailableCompatibility(target);
        var service = new DadWakeTakeoverService(target);
        Assert.Equal(DadWakeTakeoverPhase.Prepared, service.Handle(Request()).Phase);

        for (var cycle = 0; cycle < 2; cycle++)
        {
            target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Busy);
            service.Update();
            Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, service.Handle(StatusRequest()).Phase);

            target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Idle);
            service.Update();
            Assert.Equal(DadWakeTakeoverPhase.Prepared, service.Handle(StatusRequest()).Phase);
        }

        Assert.Equal("scheduler-run", service.Handle(StatusRequest()).OperationToken);
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void CoordinatorDisconnectReleasesAllTemporaryLeasesButKeepsLogicalOrder()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        Prepare(service, target);

        service.OnCoordinatorDisconnected();
        var result = service.Handle(StatusRequest());

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, result.Phase);
        Assert.Contains("Finish:Stop", target.Actions);
        Assert.Contains("ReleaseSuppression", target.Actions);
        Assert.Contains("ReleaseReservation", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void CoordinatorDisconnectDoesNotStopAlreadyCommittedCommands()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);

        service.Handle(Go(DadWakeCommitKind.Reset, clock.Now.AddSeconds(5)));
        service.OnCoordinatorDisconnected();
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.ResetVerified, service.GetActiveStatus()!.Phase);
        Assert.Equal(1, target.Actions.Count(static action => action == "ResetAutoRetainer"));

        service.Handle(StatusRequest());
        service.Handle(Go(DadWakeCommitKind.Relog, clock.Now.AddSeconds(5)));
        service.OnCoordinatorDisconnected();
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, service.GetActiveStatus()!.Phase);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Fact]
    public void ConflictingTokenIsRejectedWithoutDisturbingOwnedOperation()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        service.Handle(Request());
        var stale = Request();
        stale.SchedulerRunId = "other-run";
        stale.OperationToken = "other-run";

        var result = service.Handle(stale);

        Assert.Equal(DadWakeTakeoverStatus.Blocked, result.Status);
        Assert.Contains("conflicting", result.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    private static void Prepare(DadWakeTakeoverService service, FakeTarget target)
    {
        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, service.Handle(Request()).Phase);
        target.Snapshot.DadOwnsCharacterPostprocess = true;
        service.OnCharacterPostprocessReady();
        Assert.Equal(DadWakeTakeoverPhase.Prepared, service.Handle(Request()).Phase);
    }

    private static void ExecuteGo(DadWakeTakeoverService service, TestClock clock, DadWakeCommitKind kind)
    {
        var at = clock.Now.AddSeconds(5);
        service.Handle(Go(kind, at));
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();
    }

    private static DadWakeTakeoverResultDto StartRelog(
        DadWakeTakeoverService service,
        FakeTarget target,
        TestClock clock,
        int expectedRelogCount = 1)
    {
        Prepare(service, target);
        ExecuteGo(service, clock, DadWakeCommitKind.Reset);
        ExecuteGo(service, clock, DadWakeCommitKind.Relog);
        var result = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, result.Phase);
        Assert.Equal(expectedRelogCount, target.Actions.Count(static action => action == "RelogCharacter"));
        return result;
    }

    private static bool IsMutation(string action)
        => action is "DisableAutoRetainer" or "ResetAutoRetainer" or "RelogCharacter" ||
           action.StartsWith("SetMultiMode", StringComparison.Ordinal);

    private static void Grant(FakeTarget target)
    {
        target.ReservationStateOnReserve = DadVermaxionReservationState.Granted;
        target.Reservation.State = DadVermaxionReservationState.Granted;
        target.Reservation.Summary = "Fresh VERMAXION grant.";
        target.Reservation.VermaxionActivity = "DadHandoff";
        target.Reservation.VermaxionState = "Granted";
        target.Snapshot.VermaxionReservationState = DadVermaxionReservationState.Granted;
        target.Snapshot.ExternalAutomationHeld = false;
        target.Snapshot.ExternalAutomationActivity = "DadHandoff";
        target.Snapshot.ExternalAutomationState = "Granted";
        target.Snapshot.VermaxionReservationSummary = target.Reservation.Summary;
    }

    private static void ConfigureUnavailableCompatibility(FakeTarget target)
    {
        target.Snapshot.MultiModeEnabled = false;
        target.Snapshot.AutoRetainerAvailable = true;
        target.Snapshot.AutoRetainerBusy = false;
        target.Snapshot.SuppressionReadable = true;
        target.Snapshot.AutoRetainerSuppressed = false;
        target.Snapshot.DadOwnsSuppression = false;
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Idle);
        target.ReservationStateOnReserve = DadVermaxionReservationState.Unavailable;
        target.Reservation.State = DadVermaxionReservationState.Unavailable;
        target.Reservation.CompatibilityFallbackEligible = true;
        target.Reservation.Summary = "VERMAXION reloaded/unavailable; renewing handoff.";
        target.Reservation.VermaxionActivity = "ReservationRenewal";
        target.Reservation.VermaxionState = "Unavailable";
    }

    private static void ApplyCompatibilityFailure(FakeTarget target, CompatibilityFailure failure)
    {
        switch (failure)
        {
            case CompatibilityFailure.LegacyBusy:
                target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Busy);
                break;
            case CompatibilityFailure.LegacyUnreadable:
                target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Unavailable);
                break;
            case CompatibilityFailure.LegacyNotLoaded:
                target.LegacyStatus = Legacy(DadVermaxionReadinessKind.NotLoaded);
                break;
            case CompatibilityFailure.AutoRetainerUnavailable:
                target.Snapshot.AutoRetainerAvailable = false;
                break;
            case CompatibilityFailure.AutoRetainerBusy:
                target.Snapshot.AutoRetainerBusy = true;
                break;
            case CompatibilityFailure.MultiModeEnabled:
                target.Snapshot.MultiModeEnabled = true;
                break;
            case CompatibilityFailure.SuppressionUnreadable:
                target.Snapshot.SuppressionReadable = false;
                break;
            case CompatibilityFailure.ExternalSuppression:
                target.Snapshot.AutoRetainerSuppressed = true;
                target.Snapshot.DadOwnsSuppression = false;
                break;
        }
    }

    private static DadVermaxionReadinessStatus Legacy(DadVermaxionReadinessKind kind)
        => new()
        {
            Kind = kind,
            Activity = kind == DadVermaxionReadinessKind.Idle ? "Idle" : "Automation",
            State = kind.ToString(),
            Summary = $"VERMAXION v1 {kind}.",
        };

    private static DadWakeTakeoverRequestDto Request()
        => new()
        {
            SchedulerRunId = "scheduler-run",
            OperationToken = "scheduler-run",
            SlotId = "Slot1",
            AccountKey = new DadAccountKey("account-a"),
            CharacterKey = new DadCharacterKey("Target Character@World"),
            RequestedAtUtc = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc),
        };

    private static DadWakeTakeoverRequestDto StatusRequest()
    {
        var request = Request();
        request.MessageKind = DadWakeTakeoverMessageKind.Status;
        return request;
    }

    private static DadWakeTakeoverRequestDto Go(DadWakeCommitKind kind, DateTime at)
    {
        var request = Request();
        request.MessageKind = DadWakeTakeoverMessageKind.Go;
        request.CommitKind = kind;
        request.ExecutionTimeUtc = at;
        return request;
    }

    private sealed class FakeTarget : IDadWakeTakeoverTarget
    {
        public DadWakeTakeoverTargetSnapshot Snapshot { get; } = new();
        public DadVermaxionReservationStatus Reservation { get; } = new()
        {
            State = DadVermaxionReservationState.NotLoaded,
            Summary = "VERMAXION not loaded.",
        };
        public DadVermaxionReadinessStatus LegacyStatus { get; set; } = Legacy(DadVermaxionReadinessKind.Idle);
        public List<string> Actions { get; } = [];
        public int ReserveCount { get; private set; }
        public DadVermaxionReservationState ReservationStateOnReserve { get; set; } = DadVermaxionReservationState.NotLoaded;
        public Action? OnSuppressionAcquired { get; set; }
        public Action<DadWakeTakeoverCommand>? OnCommandExecuted { get; set; }
        public Queue<bool> FinishResults { get; } = new();
        public Queue<bool> SuppressionReleaseResults { get; } = new();
        public Queue<bool> ReservationReleaseResults { get; } = new();
        public List<bool> CaptureForceFlags { get; } = [];
        public Action<bool>? OnCapture { get; set; }
        public Action? OnSuppressionReleased { get; set; }

        public static FakeTarget Valid(bool wrongCharacter)
        {
            var target = new FakeTarget();
            target.Snapshot.DadEnabled = true;
            target.Snapshot.RemoteMutationAllowed = true;
            target.Snapshot.AccountMatches = true;
            target.Snapshot.CharacterKnownToAccount = true;
            target.Snapshot.CorrectCharacter = !wrongCharacter;
            target.Snapshot.PostArReady = true;
            target.Snapshot.AutoRetainerAvailable = true;
            target.Snapshot.AutoRetainerBusy = false;
            target.Snapshot.LifestreamAvailable = true;
            target.Snapshot.LifestreamBusy = false;
            target.Snapshot.MultiModeEnabled = true;
            target.Snapshot.SuppressionReadable = true;
            target.Snapshot.Participant = new DadParticipantSnapshot
            {
                IsAvailable = true,
                PostArReady = true,
                WorldReadyStable = true,
                ActiveCharacterKey = new DadCharacterKey(
                    wrongCharacter ? "Other Character@World" : "Target Character@World"),
            };
            return target;
        }

        public DadWakeTakeoverTargetSnapshot Capture(
            DadWakeTakeoverRequestDto request,
            bool forceExternalRefresh = false)
        {
            CaptureForceFlags.Add(forceExternalRefresh);
            OnCapture?.Invoke(forceExternalRefresh);
            RefreshAuthority(request.OperationToken);
            return Snapshot;
        }

        public DadVermaxionReservationStatus ReserveVermaxion(DadWakeTakeoverRequestDto request)
        {
            ReserveCount++;
            if (Reservation.State == DadVermaxionReservationState.Released)
                Reservation.State = ReservationStateOnReserve;
            if (Reservation.State != DadVermaxionReservationState.NotLoaded)
                Reservation.OperationToken = request.OperationToken;
            RefreshAuthority(request.OperationToken);
            return Reservation;
        }

        private void RefreshAuthority(string operationToken)
        {
            var evidence = DadVermaxionCompatibilityEvidence.Evaluate(
                LegacyStatus,
                Snapshot.AutoRetainerAvailable,
                Snapshot.AutoRetainerBusy,
                Snapshot.MultiModeEnabled,
                Snapshot.SuppressionReadable,
                Snapshot.AutoRetainerSuppressed,
                Snapshot.DadOwnsSuppression);
            var authority = DadVermaxionAuthorityRules.Resolve(
                operationToken,
                Reservation,
                LegacyStatus,
                evidence);
            Snapshot.VermaxionReservationState = Reservation.State;
            Snapshot.VermaxionReservationSummary = Reservation.Summary;
            Snapshot.VermaxionReservationUpdatedAtUtc = Reservation.UpdatedAtUtc;
            Snapshot.VermaxionReservationAuthoritative = authority.Authoritative;
            Snapshot.VermaxionMutationAuthorization = authority.MutationAuthorization;
            Snapshot.VermaxionCompatibilityEvidence = authority.CompatibilityEvidence;
            Snapshot.ExternalAutomationHeld = authority.Held;
            Snapshot.ExternalAutomationActivity = authority.Activity;
            Snapshot.ExternalAutomationState = authority.State;
            Snapshot.ExternalAutomationSummary = authority.Summary;
        }

        public bool ReleaseVermaxion(string operationToken)
        {
            if (string.IsNullOrWhiteSpace(operationToken))
                return true;
            Actions.Add("ReleaseReservation");
            if (!NextResult(ReservationReleaseResults))
                return false;
            Reservation.State = DadVermaxionReservationState.Released;
            return true;
        }

        public DadWakeTakeoverActionResult ArmCharacterPostprocess(string operationToken)
        {
            Actions.Add("Arm");
            return DadWakeTakeoverActionResult.Accepted();
        }

        public DadWakeTakeoverActionResult AcquireSuppression()
        {
            Actions.Add("AcquireSuppression");
            Snapshot.DadOwnsSuppression = true;
            Snapshot.AutoRetainerSuppressed = true;
            OnSuppressionAcquired?.Invoke();
            return DadWakeTakeoverActionResult.Accepted();
        }

        public bool FinishCharacterPostprocess(bool retryAtNextBoundary)
        {
            Actions.Add(retryAtNextBoundary ? "Finish:Retry" : "Finish:Stop");
            if (!NextResult(FinishResults))
                return false;
            Snapshot.DadOwnsCharacterPostprocess = false;
            return true;
        }

        public bool ReleaseSuppressionIfOwned(bool force = false)
        {
            if (!Snapshot.DadOwnsSuppression)
                return true;
            Actions.Add("ReleaseSuppression");
            if (!NextResult(SuppressionReleaseResults))
                return false;
            Snapshot.DadOwnsSuppression = false;
            Snapshot.AutoRetainerSuppressed = false;
            OnSuppressionReleased?.Invoke();
            return true;
        }

        public DadWakeTakeoverActionResult SetMultiModeEnabled(bool enabled)
        {
            Actions.Add($"SetMultiMode:{enabled}");
            Snapshot.MultiModeEnabled = enabled;
            return DadWakeTakeoverActionResult.Accepted();
        }

        public DadWakeTakeoverActionResult ExecuteCommand(DadWakeTakeoverCommand command, DadWakeTakeoverRequestDto request)
        {
            Actions.Add(command.ToString());
            OnCommandExecuted?.Invoke(command);
            return DadWakeTakeoverActionResult.Accepted();
        }

        private static bool NextResult(Queue<bool> results)
            => results.Count == 0 || results.Dequeue();
    }

    private sealed class TestClock
    {
        public DateTime Now { get; private set; } = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow() => Now;
        public void Advance(TimeSpan elapsed) => Now = Now.Add(elapsed);
    }

    public enum CompatibilityFailure
    {
        LegacyBusy,
        LegacyUnreadable,
        LegacyNotLoaded,
        AutoRetainerUnavailable,
        AutoRetainerBusy,
        MultiModeEnabled,
        SuppressionUnreadable,
        ExternalSuppression,
    }
}
