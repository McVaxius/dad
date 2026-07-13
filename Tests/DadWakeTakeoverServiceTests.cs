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
    public void CharacterPostprocessLeaseAllowsPreparationWhileBusyIsOnlyDiagnostic()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        service.Handle(Request());
        target.Snapshot.AutoRetainerBusy = true;
        target.Snapshot.DadOwnsCharacterPostprocess = true;

        service.OnCharacterPostprocessReady();
        var prepared = service.Handle(Request());

        Assert.Equal(DadWakeTakeoverPhase.Prepared, prepared.Phase);
        Assert.True(prepared.AutoRetainerBusy);
        Assert.Equal(["Arm", "AcquireSuppression"], target.Actions);
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

        Assert.Contains("Finish:Retry", target.Actions);
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

        Assert.Contains("Finish:Retry", target.Actions);
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
    public void AcceptedButIgnoredRelogRetriesAfterFiveSecondsAndPreservesFirstTimestamp()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        var waiting = StartRelog(service, target, clock);
        var firstIssuedUtc = waiting.RelogIssuedUtc;

        clock.Advance(TimeSpan.FromMilliseconds(4999));
        service.Update();
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));

        clock.Advance(TimeSpan.FromMilliseconds(1));
        service.Update();

        var retried = service.Handle(StatusRequest());
        Assert.Equal(2, target.Actions.Count(static action => action == "RelogCharacter"));
        Assert.Equal(firstIssuedUtc, retried.RelogIssuedUtc);
        Assert.Contains("attempt 2", retried.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RelogDoesNotRetryWhileAutoRetainerOrLifestreamIsBusy(bool autoRetainerBusy, bool lifestreamBusy)
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
    public void RelogDoesNotRetryAfterCharacterDisappearsOrWorldBecomesUnstable(bool characterAvailable, bool worldReadyStable)
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
    public void RelogRetryRequiresMultiModeOffAndOwnedSuppression(
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
    public void RelogRetryStopsWhenTargetCharacterBecomesActive()
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
    public void StopAllPreservesCommittedTakeoverBoundary()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);
        service.Handle(Go(DadWakeCommitKind.Reset, clock.Now.AddSeconds(5)));
        var actionsBeforeStop = target.Actions.ToList();

        var result = service.StopAll("Stop-all test.");

        Assert.Equal(0, result.CancelledCount);
        Assert.Equal(1, result.PreservedCommittedCount);
        Assert.Equal(DadWakeTakeoverPhase.ResetCommitted, service.Handle(StatusRequest()).Phase);
        Assert.Equal(actionsBeforeStop, target.Actions);
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
        Assert.Equal(DadWakeTakeoverStage.WaitingForExternalAutomation, result.Stage);
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

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, result.Phase);
        Assert.DoesNotContain("AcquireSuppression", target.Actions);
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void CompatibilityInvalidatedBeforeResetReturnsToWaitingWithoutCommandsAndRecovers()
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
        Assert.DoesNotContain(target.Actions, IsMutation);

        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Idle);
        service.Update();
        var recovered = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverPhase.Prepared, recovered.Phase);
        Assert.Equal("scheduler-run", recovered.OperationToken);
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
    public void CoordinatorDisconnectReleasesTemporaryLeasesButKeepsReservationAndOrder()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target);
        Prepare(service, target);

        service.OnCoordinatorDisconnected();
        var result = service.Handle(StatusRequest());

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, result.Phase);
        Assert.Contains("Finish:Stop", target.Actions);
        Assert.Contains("ReleaseSuppression", target.Actions);
        Assert.DoesNotContain("ReleaseReservation", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
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
        TestClock clock)
    {
        Prepare(service, target);
        ExecuteGo(service, clock, DadWakeCommitKind.Reset);
        ExecuteGo(service, clock, DadWakeCommitKind.Relog);
        var result = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, result.Phase);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        return result;
    }

    private static bool IsMutation(string action)
        => action is "DisableAutoRetainer" or "ResetAutoRetainer" or "RelogCharacter" ||
           action.StartsWith("SetMultiMode", StringComparison.Ordinal);

    private static void Grant(FakeTarget target)
    {
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
            RefreshAuthority(request.OperationToken);
            return Snapshot;
        }

        public DadVermaxionReservationStatus ReserveVermaxion(DadWakeTakeoverRequestDto request)
        {
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
            return DadWakeTakeoverActionResult.Accepted();
        }

        public bool FinishCharacterPostprocess(bool retryAtNextBoundary)
        {
            Actions.Add(retryAtNextBoundary ? "Finish:Retry" : "Finish:Stop");
            Snapshot.DadOwnsCharacterPostprocess = false;
            return true;
        }

        public bool ReleaseSuppressionIfOwned(bool force = false)
        {
            if (!Snapshot.DadOwnsSuppression)
                return true;
            Actions.Add("ReleaseSuppression");
            Snapshot.DadOwnsSuppression = false;
            Snapshot.AutoRetainerSuppressed = false;
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
            return DadWakeTakeoverActionResult.Accepted();
        }
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
