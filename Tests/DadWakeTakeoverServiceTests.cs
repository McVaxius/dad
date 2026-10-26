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
    public void NotLoadedVermaxionAcquiresIdleSuppressionWithoutCharacterPostprocess()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.NotLoaded);
        target.Reservation.State = DadVermaxionReservationState.NotLoaded;
        var service = new DadWakeTakeoverService(target);

        var waiting = service.Handle(Request());

        Assert.Equal(DadVermaxionReservationState.NotLoaded, waiting.VermaxionReservationState);
        Assert.Equal(DadWakeTakeoverPhase.Prepared, waiting.Phase);
        Assert.Equal(["AcquireSuppression"], target.Actions);
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain("ReleaseReservation", target.Actions);
        Assert.Contains("no character postprocess callback required", waiting.Summary);
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
    [InlineData(DadVermaxionReadinessKind.Idle)]
    [InlineData(DadVermaxionReadinessKind.NotLoaded)]
    public void TitleIdleWithoutLoggedInPriorCharacterUsesExactOwnedLoginPath(DadVermaxionReadinessKind vermaxionKind)
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        ConfigureTitleIdle(target);
        target.LegacyStatus = Legacy(vermaxionKind);
        target.Snapshot.VermaxionStatusKind = vermaxionKind;
        var service = new DadWakeTakeoverService(target);

        var preparing = service.Handle(Request());
        service.Update();
        var waiting = service.Handle(StatusRequest());

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, preparing.Phase);
        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, waiting.Phase);
        Assert.Equal(DadWakeAcknowledgementState.Executed, waiting.AcknowledgementState);
        Assert.Equal(0, target.ReserveCount);
        Assert.Equal(
            ["SetMultiMode:False", "ConnectAndLogin:Target Character@World"],
            target.Actions);
    }

    [Fact]
    public void ExplicitFalseTitleLoginWaitsFiveSecondsAndRecapturesBeforeRetry()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        ConfigureTitleIdle(target);
        target.UtcNow = clock.UtcNow;
        target.TitleLoginOutcomes.Enqueue(DadLifestreamLoginOutcome.ExplicitFalse);
        target.TitleLoginOutcomes.Enqueue(DadLifestreamLoginOutcome.Accepted);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);

        service.Handle(Request());
        service.Update();

        Assert.Single(target.TitleLoginCaptureCounts);
        clock.Advance(TimeSpan.FromSeconds(4));
        service.Update();
        Assert.Single(target.TitleLoginCaptureCounts);

        var capturesBeforeRetry = target.CaptureForceFlags.Count;
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Update();

        Assert.Equal(2, target.TitleLoginCaptureCounts.Count);
        Assert.True(target.TitleLoginCaptureCounts[1] > capturesBeforeRetry);
        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, service.GetActiveStatus()!.Phase);
    }

    [Fact]
    public void UncertainTitleLoginNeverReplays()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        ConfigureTitleIdle(target);
        target.TitleLoginOutcomes.Enqueue(DadLifestreamLoginOutcome.Uncertain);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);

        service.Handle(Request());
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(10));
        service.Update();
        service.Update();

        Assert.Single(target.TitleLoginCaptureCounts);
    }

    [Fact]
    public void AcceptedTitleLoginReturningToSafeIdleTitleRetriesAfterFiveSeconds()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        ConfigureTitleIdle(target);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        service.Handle(Request());
        service.Update();
        Assert.Single(target.TitleLoginCaptureCounts);
        target.Snapshot.LifestreamBusy = true;
        clock.Advance(TimeSpan.FromMinutes(1));
        service.Update();
        target.Snapshot.LifestreamBusy = false;
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(4));
        service.Update();
        Assert.Single(target.TitleLoginCaptureCounts);
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Update();
        Assert.Equal(2, target.TitleLoginCaptureCounts.Count);
        Assert.Single(target.Actions, action => action == "SetMultiMode:False");
    }

    [Fact]
    public void AcceptedTitleLoginRetriesWithoutAttemptCapAndStopsOnArrival()
    {
        var (clock, target, service) = StartAcceptedTitleLogin();
        for (var attempt = 2; attempt <= 201; attempt++)
        {
            service.Update();
            clock.Advance(TimeSpan.FromSeconds(5));
            service.Update();
            Assert.Equal(attempt, target.TitleLoginCaptureCounts.Count);
        }
        Assert.Single(target.Actions, action => action == "SetMultiMode:False");
        Assert.Equal(0, target.ReserveCount);
        Assert.DoesNotContain("ResetAutoRetainer", target.Actions);
        Assert.All(target.Actions.Where(action => action.StartsWith("ConnectAndLogin:")),
            action => Assert.Equal("ConnectAndLogin:Target Character@World", action));
        ActivateTargetCharacter(target);
        service.Update();
        clock.Advance(TimeSpan.FromDays(1));
        service.Update();
        Assert.Equal(201, target.TitleLoginCaptureCounts.Count);
        Assert.Equal(DadWakeTakeoverPhase.Ready, service.GetActiveStatus()!.Phase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    public void AcceptedTitleLoginUnsafeObservationResetsEntireIdleWindow(int gate)
    {
        var (clock, target, service) = StartAcceptedTitleLogin();
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(4));
        service.Update();
        MakeTitleLoginUnsafe(target, gate);
        service.Update();
        clock.Advance(TimeSpan.FromDays(1));
        service.Update();
        Assert.Single(target.TitleLoginCaptureCounts);

        ConfigureTitleIdle(target);
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Idle);
        target.Snapshot.MultiModeEnabled = false;
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(4));
        service.Update();
        Assert.Single(target.TitleLoginCaptureCounts);
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Update();
        Assert.Equal(2, target.TitleLoginCaptureCounts.Count);
        Assert.Single(target.Actions, action => action == "SetMultiMode:False");
        Assert.DoesNotContain("DismissTitleMovie", target.Actions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AcceptedTitleLoginFinalForcedReadCannotReplayDuringNewLoginTransition(int gate)
    {
        var (clock, target, service) = StartAcceptedTitleLogin();
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(5));
        var captures = 0;
        target.OnCapture = force =>
        {
            Assert.True(force);
            if (++captures == 2)
                MakeTitleLoginUnsafe(target, gate);
        };
        service.Update();
        Assert.Equal(2, captures);
        Assert.Single(target.TitleLoginCaptureCounts);
        target.OnCapture = null;
        ConfigureTitleIdle(target);
        target.Snapshot.MultiModeEnabled = false;
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();
        Assert.Equal(2, target.TitleLoginCaptureCounts.Count);
    }

    [Fact]
    public void AcceptedTitleLoginFinalForcedReadArrivalWinsOverRetry()
    {
        var (clock, target, service) = StartAcceptedTitleLogin();
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(5));
        var captures = 0;
        target.OnCapture = _ =>
        {
            if (++captures == 2)
                ActivateTargetCharacter(target);
        };
        service.Update();
        target.OnCapture = null;
        service.Update();
        Assert.Single(target.TitleLoginCaptureCounts);
        Assert.Equal(DadWakeTakeoverPhase.Ready, service.GetActiveStatus()!.Phase);
    }

    [Fact]
    public void AcceptedTitleLoginSettlingOnWrongLiveCharacterFailsClosed()
    {
        var (clock, target, service) = StartAcceptedTitleLogin();
        target.Snapshot.Participant.IsAvailable = true;
        target.Snapshot.Participant.WorldReadyStable = true;
        target.Snapshot.Participant.ActiveCharacterKey = new DadCharacterKey("Other Character@World");
        service.Update();
        Assert.Equal(DadWakeTakeoverStatus.Blocked, service.GetActiveStatus()!.Status);
        clock.Advance(TimeSpan.FromMinutes(1));
        service.Update();
        Assert.Single(target.TitleLoginCaptureCounts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedTitleLoginRetryKeepsFrozenWorkerAccountRoute(bool changeAccount)
    {
        var (clock, target, service) = StartAcceptedTitleLogin();
        if (changeAccount)
            target.Snapshot.Participant.ManagedAccountKey = new DadAccountKey("different-account");
        else
            target.Snapshot.Participant.WorkerSessionId = new DadWorkerSessionId("different-worker");
        service.Update();
        Assert.Equal(DadWakeTakeoverStatus.Blocked, service.GetActiveStatus()!.Status);
        clock.Advance(TimeSpan.FromMinutes(1));
        service.Update();
        Assert.Single(target.TitleLoginCaptureCounts);
    }

    [Fact]
    public void AcceptedThenExplicitFalseTitleLoginReturnsToFiveSecondRetryCadence()
    {
        var (clock, target, service) = StartAcceptedTitleLogin();
        target.TitleLoginOutcomes.Enqueue(DadLifestreamLoginOutcome.ExplicitFalse);
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();
        Assert.Equal(2, target.TitleLoginCaptureCounts.Count);
        clock.Advance(TimeSpan.FromSeconds(4));
        service.Update();
        Assert.Equal(2, target.TitleLoginCaptureCounts.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Update();
        Assert.Equal(3, target.TitleLoginCaptureCounts.Count);
        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, service.GetActiveStatus()!.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UncertainOrThrowingAcceptedTitleLoginRetryFailsClosed(bool throws)
    {
        var (clock, target, service) = StartAcceptedTitleLogin();
        target.ThrowOnTitleLogin = throws;
        target.TitleLoginOutcomes.Enqueue(DadLifestreamLoginOutcome.Uncertain);
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();
        Assert.Equal(2, target.TitleLoginCaptureCounts.Count);
        Assert.Equal(DadWakeTakeoverStatus.Blocked, service.GetActiveStatus()!.Status);
        clock.Advance(TimeSpan.FromMinutes(1));
        service.Update();
        Assert.Equal(2, target.TitleLoginCaptureCounts.Count);
    }

    [Fact]
    public void AcceptedTitleLoginCoordinatorLossRequiresNewIdleWindowAfterReconnect()
    {
        var (clock, target, service) = StartAcceptedTitleLogin();
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(4));
        service.OnCoordinatorDisconnected();
        clock.Advance(TimeSpan.FromMinutes(1));
        service.Update();
        Assert.Single(target.TitleLoginCaptureCounts);
        service.Handle(StatusRequest());
        clock.Advance(TimeSpan.FromSeconds(4));
        service.Update();
        Assert.Single(target.TitleLoginCaptureCounts);
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Update();
        Assert.Equal(2, target.TitleLoginCaptureCounts.Count);
    }

    [Fact]
    public void AcceptedTitleLoginCancellationStopsPendingRetries()
    {
        var (clock, target, service) = StartAcceptedTitleLogin();
        service.Update();
        var cancel = Request();
        cancel.MessageKind = DadWakeTakeoverMessageKind.Cancel;
        service.Handle(cancel);
        clock.Advance(TimeSpan.FromMinutes(1));
        service.Update();
        Assert.Single(target.TitleLoginCaptureCounts);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(16)]
    public void AcceptedTitleLoginClockRollbackOrObservationGapRestartsIdleProof(int seconds)
    {
        var (clock, target, service) = StartAcceptedTitleLogin();
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(4));
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(seconds));
        service.Update();
        Assert.Single(target.TitleLoginCaptureCounts);
        clock.Advance(TimeSpan.FromSeconds(4));
        service.Update();
        Assert.Single(target.TitleLoginCaptureCounts);
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Update();
        Assert.Equal(2, target.TitleLoginCaptureCounts.Count);
    }

    private static (TestClock Clock, FakeTarget Target, DadWakeTakeoverService Service) StartAcceptedTitleLogin()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        ConfigureTitleIdle(target);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        service.Handle(Request());
        service.Update();
        Assert.Single(target.TitleLoginCaptureCounts);
        return (clock, target, service);
    }

    private static void MakeTitleLoginUnsafe(FakeTarget target, int gate)
    {
        switch (gate)
        {
            case 0: target.Snapshot.LifestreamBusy = true; break;
            case 1: target.Snapshot.TitleSurface = DadTitleSurface.CharacterSelect; break;
            case 2: target.Snapshot.TitleSurface = DadTitleSurface.ConnectingToDataCenter; break;
            case 3: target.Snapshot.TitleSurface = DadTitleSurface.TitleMovie; break;
            case 4: target.Snapshot.TitleSurface = DadTitleSurface.Ambiguous; break;
            case 5: target.Snapshot.TitleSurface = DadTitleSurface.None; break;
            case 6: target.Snapshot.TitleSurfaceEvidenceFresh = false; break;
            case 7: target.Snapshot.TitleMenuReady = false; break;
            case 8: target.Snapshot.TitleClientLoggedOut = false; break;
            case 9: target.Snapshot.TitleNoActiveConditionFlags = false; break;
            case 10: target.Snapshot.TitleNavigationOverlayVisible = true; break;
            case 11: target.Snapshot.TitleConnectionOverlayVisible = true; break;
            case 12: target.Snapshot.TitleErrorOverlayVisible = true; break;
            case 13: target.Snapshot.AutoRetainerAvailable = false; break;
            case 14: target.Snapshot.AutoRetainerBusy = true; break;
            case 15: target.Snapshot.SuppressionReadable = false; break;
            case 16: target.Snapshot.AutoRetainerSuppressed = true; break;
            case 17: target.Snapshot.VermaxionStatusEvidenceFresh = false; break;
            case 18: target.Snapshot.LifestreamAvailable = false; break;
            case 19: target.Snapshot.LifestreamCanAutoLogin = false; break;
            case 20: target.Snapshot.MultiModeEnabled = true; break;
            case 21: target.Snapshot.DadOwnsCharacterPostprocess = true; break;
            case 22: target.Snapshot.DadOwnsSuppression = true; break;
            case 23:
                target.Snapshot.VermaxionStatusKind = DadVermaxionReadinessKind.Busy;
                target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Busy);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(gate));
        }
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
        Assert.DoesNotContain("DisableAutoRetainerMultiMode", target.Actions);
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
        target.Reservation.OperationToken = target.ReserveTokens.Single();
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
    public void VisitingCharacterReturnsHomeBeforeRelogWhileSafetyLeaseRemainsOwned()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.UtcNow = clock.UtcNow;
        target.Snapshot.Participant.CurrentLocation = new DadWorldLocationObservation
        {
            WorldId = 2,
            WorldName = "Visitor World",
            DataCenterId = 2,
            DataCenterName = "Visitor DC",
            RegionId = 1,
            RegionName = "Region",
            ObservedAtUtc = clock.Now,
        };
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);
        ExecuteGo(service, clock, DadWakeCommitKind.Reset);

        var relogAt = clock.Now.AddSeconds(5);
        service.Handle(Go(DadWakeCommitKind.Relog, relogAt));
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();

        Assert.Contains("ChangeWorld:World", target.Actions);
        Assert.DoesNotContain("RelogCharacter", target.Actions);
        Assert.True(target.Snapshot.DadOwnsSuppression);
        Assert.Equal(DadWakeTakeoverStage.WaitingForHomeWorld, service.GetActiveStatus()!.Stage);
        Assert.Contains("Return-home attempt 1", service.GetActiveStatus()!.Summary, StringComparison.Ordinal);
        Assert.Contains("fresh live observation", service.GetActiveStatus()!.Summary, StringComparison.Ordinal);

        target.Snapshot.Participant.CurrentLocation = new DadWorldLocationObservation
        {
            WorldId = 1,
            WorldName = "World",
            DataCenterId = 1,
            DataCenterName = "Data Center",
            RegionId = 1,
            RegionName = "Region",
            ObservedAtUtc = clock.Now,
        };
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Update();

        var changeWorldIndex = target.Actions.IndexOf("ChangeWorld:World");
        var relogIndex = target.Actions.IndexOf("RelogCharacter");
        Assert.True(changeWorldIndex >= 0 && relogIndex > changeWorldIndex);
        Assert.True(target.Snapshot.DadOwnsSuppression);
        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, service.GetActiveStatus()!.Phase);
    }

    [Fact]
    public void AcceptedRelogDoesNotVerifyDestinationInTheCommandCycle()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);
        ExecuteGo(service, clock, DadWakeCommitKind.Reset);
        var relogAt = clock.Now.AddSeconds(5);
        service.Handle(Go(DadWakeCommitKind.Relog, relogAt));
        clock.Advance(TimeSpan.FromSeconds(5));
        var capturesAfterCommand = 0;
        target.OnCapture = _ =>
        {
            if (target.Actions.Contains("RelogCharacter"))
                capturesAfterCommand++;
        };

        service.Update();

        target.OnCapture = null;
        Assert.Equal(0, capturesAfterCommand);
        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, service.GetActiveStatus()!.Phase);
        Assert.Equal("Other Character@World", target.Snapshot.Participant.ActiveCharacterKey.Value);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Fact]
    public void AcceptedButAbortedRelogRetriesAfterFiveFreshSafeIdleSeconds()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        target.Snapshot.LifestreamBusy = true;
        clock.Advance(TimeSpan.FromMinutes(1));
        service.Update();
        target.Snapshot.LifestreamBusy = false;
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(4));
        service.Update();
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Update();
        Assert.Equal(2, target.Actions.Count(static action => action == "RelogCharacter"));
        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, service.Handle(StatusRequest()).Phase);
        Assert.DoesNotContain("ReleaseSuppression", target.Actions);
    }

    [Fact]
    public void RepeatedAcceptedRelogAbortsHaveNoAttemptLimitAndKeepExactOwnership()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        var resetCount = target.Actions.Count(static action => action == "ResetAutoRetainer");
        for (var attempt = 1; attempt <= 200; attempt++)
        {
            service.Update();
            clock.Advance(TimeSpan.FromSeconds(5));
            service.Update();
            Assert.Equal(attempt + 1, target.Actions.Count(static action => action == "RelogCharacter"));
            Assert.True(target.Snapshot.DadOwnsSuppression);
            Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, service.GetActiveStatus()!.Phase);
        }
        Assert.Equal(resetCount, target.Actions.Count(static action => action == "ResetAutoRetainer"));
        Assert.DoesNotContain("ReleaseSuppression", target.Actions);
        Assert.All(target.Actions.Where(static action => action.StartsWith("RelogTarget:", StringComparison.Ordinal)),
            static action => Assert.Equal("RelogTarget:Target Character@World", action));
        ActivateTargetCharacter(target);
        service.Update();
        Assert.Equal(DadWakeTakeoverPhase.Ready, service.GetActiveStatus()!.Phase);
        Assert.Contains("ReleaseSuppression", target.Actions);
    }

    [Theory]
    [InlineData(0)] // AutoRetainer busy
    [InlineData(1)] // Lifestream busy
    [InlineData(2)] // AutoRetainer unavailable
    [InlineData(3)] // Lifestream unavailable
    [InlineData(4)] // casting/loading/world unsafe
    [InlineData(5)] // suppression unreadable
    [InlineData(6)] // suppression no longer owned
    [InlineData(7)] // suppression no longer active
    [InlineData(8)] // Multi Mode re-enabled
    [InlineData(9)] // external automation
    [InlineData(10)] // account mismatch
    [InlineData(11)] // target no longer catalogued
    [InlineData(12)] // disconnected route
    public void UnsafeObservationRestartsRelogRetryWindowEvenAfterOneDay(int unsafeKind)
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(4));
        SetUnsafeRelogObservation(target.Snapshot, unsafeKind, true);
        service.Update();
        clock.Advance(TimeSpan.FromDays(1));
        service.Update();
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        SetUnsafeRelogObservation(target.Snapshot, unsafeKind, false);
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(4));
        service.Update();
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Update();
        Assert.Equal(2, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Theory]
    [InlineData(0)] // Content ID
    [InlineData(1)] // worker session
    [InlineData(2)] // managed account
    [InlineData(3)] // recorded home world
    public void ChangedSourceIdentityBlocksRelogRetry(int identityKind)
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(5));
        switch (identityKind)
        {
            case 0: target.Snapshot.Participant.Character.ContentId++; break;
            case 1: target.Snapshot.Participant.WorkerSessionId = new DadWorkerSessionId("replacement"); break;
            case 2: target.Snapshot.Participant.ManagedAccountKey = new DadAccountKey("replacement"); break;
            case 3: target.Snapshot.Participant.Character.WorldId++; break;
        }
        service.Update();
        var blocked = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverStatus.Blocked, blocked.Status);
        Assert.Contains("source identity changed", blocked.BlockedReason, StringComparison.Ordinal);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Theory]
    [InlineData(0)] // busy on final capture
    [InlineData(1)] // target arrived on final capture
    [InlineData(2)] // different source Content ID on final capture
    [InlineData(3)] // location changed on final capture
    public void RelogRetryRechecksLiveTruthImmediatelyBeforeDispatch(int changeKind)
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(5));
        var captures = 0;
        target.OnCapture = _ =>
        {
            if (++captures != 3)
                return;
            switch (changeKind)
            {
                case 0: target.Snapshot.LifestreamBusy = true; break;
                case 1: ActivateTargetCharacter(target); break;
                case 2: target.Snapshot.Participant.Character.ContentId++; break;
                case 3: target.Snapshot.Participant.CurrentLocation!.WorldId++; break;
            }
        };
        service.Update();
        Assert.True(captures >= 3);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        if (changeKind == 1)
            Assert.Equal(DadWakeTakeoverPhase.Ready, service.GetActiveStatus()!.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedOrUncertainRetryDispatchNeverReplays(bool throws)
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        service.Update();
        target.RelogDispatchFailure = throws ? 2 : 1;
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();
        var blocked = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverStatus.Blocked, blocked.Status);
        Assert.Contains("no automatic replay", blocked.BlockedReason, StringComparison.Ordinal);
        clock.Advance(TimeSpan.FromDays(1));
        service.Update();
        Assert.Equal(2, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Fact]
    public void CancelledRelogCannotRetryAfterIdleWindow()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        service.Update();
        var cancel = StatusRequest();
        cancel.MessageKind = DadWakeTakeoverMessageKind.Cancel;
        Assert.Equal(DadWakeTakeoverPhase.Cancelled, service.Handle(cancel).Phase);
        clock.Advance(TimeSpan.FromDays(1));
        service.Update();
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Fact]
    public void CoordinatorDisconnectPausesRetriesAndReconnectRequiresNewIdleProof()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        service.Update();
        service.OnCoordinatorDisconnected();
        clock.Advance(TimeSpan.FromDays(1));
        service.Update();
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        service.Handle(StatusRequest()); // same exact authorized operation reconnects
        clock.Advance(TimeSpan.FromSeconds(4));
        service.Update();
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Update();
        Assert.Equal(2, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void ReturnHomeRetryAlsoRestartsProofAfterUnsafeAutomationOrOwnership(int unsafeKind)
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.UtcNow = clock.UtcNow;
        target.Snapshot.Participant.CurrentLocation!.WorldId = 2;
        target.Snapshot.Participant.CurrentLocation.WorldName = "Visitor World";
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);
        ExecuteGo(service, clock, DadWakeCommitKind.Reset);
        ExecuteGo(service, clock, DadWakeCommitKind.Relog);
        Assert.Single(target.Actions, static action => action == "ChangeWorld:World");
        service.Update();
        clock.Advance(TimeSpan.FromSeconds(4));
        SetUnsafeRelogObservation(target.Snapshot, unsafeKind, true);
        service.Update();
        clock.Advance(TimeSpan.FromDays(1));
        service.Update();
        SetUnsafeRelogObservation(target.Snapshot, unsafeKind, false);
        service.Update();
        Assert.Single(target.Actions, static action => action == "ChangeWorld:World");
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();
        Assert.Equal(2, target.Actions.Count(static action => action == "ChangeWorld:World"));
        Assert.DoesNotContain("RelogCharacter", target.Actions);
    }

    private static void SetUnsafeRelogObservation(DadWakeTakeoverTargetSnapshot snapshot, int kind, bool unsafeValue)
    {
        switch (kind)
        {
            case 0: snapshot.AutoRetainerBusy = unsafeValue; break;
            case 1: snapshot.LifestreamBusy = unsafeValue; break;
            case 2: snapshot.AutoRetainerAvailable = !unsafeValue; break;
            case 3: snapshot.LifestreamAvailable = !unsafeValue; break;
            case 4: snapshot.Participant.WorldReadyStable = !unsafeValue; break;
            case 5: snapshot.SuppressionReadable = !unsafeValue; break;
            case 6: snapshot.DadOwnsSuppression = !unsafeValue; break;
            case 7: snapshot.AutoRetainerSuppressed = !unsafeValue; break;
            case 8: snapshot.MultiModeEnabled = unsafeValue; break;
            case 9: snapshot.ExternalAutomationHeld = unsafeValue; break;
            case 10: snapshot.AccountMatches = !unsafeValue; break;
            case 11: snapshot.CharacterKnownToAccount = !unsafeValue; break;
            case 12: snapshot.ClientRouteConnected = !unsafeValue; break;
        }
    }

    [Fact]
    public void LongObservationGapDoesNotAuthorizeImmediateRelogReplay()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        var waiting = StartRelog(service, target, clock);
        Assert.NotNull(waiting.RelogIssuedUtc);

        clock.Advance(TimeSpan.FromHours(6));
        service.Update();

        var waitingForTarget = service.Handle(StatusRequest());
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, waitingForTarget.Phase);
        Assert.Equal(DadWakeTakeoverStatus.RelogIssued, waitingForTarget.Status);
        Assert.Contains("source character", waitingForTarget.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Other Character@World", target.Snapshot.Participant.ActiveCharacterKey.Value);

        for (var update = 0; update < 5; update++)
            service.Update();

        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, service.Handle(StatusRequest()).Phase);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Fact]
    public void SourceWorldInstabilityAloneDoesNotProveIdentityTransition()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);

        target.Snapshot.Participant.WorldReadyStable = false;
        service.Update();
        target.Snapshot.Participant.WorldReadyStable = true;
        service.Update();

        var waiting = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, waiting.Phase);
        Assert.Equal(DadWakeTakeoverStatus.RelogIssued, waiting.Status);
        Assert.Contains("source character", waiting.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Fact]
    public void SourceDisappearsThenExactTargetCompletesWithoutRelogReplay()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);

        target.Snapshot.Participant.IsAvailable = false;
        target.Snapshot.Participant.WorldReadyStable = false;
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, service.Handle(StatusRequest()).Phase);
        ActivateTargetCharacter(target);
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.Ready, service.Handle(StatusRequest()).Phase);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Fact]
    public void ExactSourceReturningAfterAbortedIdentityTransitionCanRetryWhenSafe()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);

        target.Snapshot.Participant.IsAvailable = false;
        target.Snapshot.Participant.WorldReadyStable = false;
        service.Update();
        target.Snapshot.Participant.IsAvailable = true;
        target.Snapshot.Participant.WorldReadyStable = true;
        service.Update();

        var waiting = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverStatus.RelogIssued, waiting.Status);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();
        Assert.Equal(2, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Fact]
    public void DifferentStableWrongCharacterFailsAfterTransitionProofWithoutRelogReplay()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);

        target.Snapshot.Participant.ActiveCharacterKey = new DadCharacterKey("Unexpected Character@World");
        target.Snapshot.Participant.Character.CharacterKey = "Unexpected Character@World";
        target.Snapshot.Participant.Character.ContentId = 30;
        target.Snapshot.Participant.IsAvailable = true;
        target.Snapshot.Participant.WorldReadyStable = true;
        service.Update();

        var failed = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverStatus.Blocked, failed.Status);
        Assert.Contains("instead of frozen target", failed.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("after identity-transition proof", failed.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        Assert.DoesNotContain("RelogTarget:Other Character@World", target.Actions);
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
        ActivateTargetCharacter(target);

        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();

        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        Assert.Equal(DadWakeTakeoverPhase.Ready, service.Handle(StatusRequest()).Phase);
    }

    [Fact]
    public void WaitingForCharacterUsesForcedLiveCaptureInsteadOfCachedParticipantIdentity()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        target.CaptureForceFlags.Clear();
        target.OnCapture = forceExternalRefresh =>
        {
            if (forceExternalRefresh)
                ActivateTargetCharacter(target);
        };

        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.Ready, service.Handle(StatusRequest()).Phase);
        Assert.NotEmpty(target.CaptureForceFlags);
        Assert.All(target.CaptureForceFlags, Assert.True);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Fact]
    public void CompletedTakeoverRestoresMultiModeOnCurrentTargetWithoutCharacterRestoration()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        ActivateTargetCharacter(target);
        service.Update();
        Assert.Equal(DadWakeTakeoverPhase.Ready, service.Handle(StatusRequest()).Phase);

        var cancel = Request();
        cancel.MessageKind = DadWakeTakeoverMessageKind.Cancel;
        var restored = service.Handle(cancel);
        Assert.True(DadSchedulerRoutingRules.IsTakeoverCancellationComplete(restored));
        Assert.Equal("Target Character@World", target.Snapshot.Participant.ActiveCharacterKey.Value);
        Assert.True(target.Snapshot.MultiModeEnabled);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        Assert.DoesNotContain("RelogTarget:Other Character@World", target.Actions);

        service.Update();
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FourClientRunCleanupAllowsNextPresetOnlyAfterOldOwnershipIsReleased(bool originalMultiMode)
    {
        for (var slot = 1; slot <= 4; slot++)
        {
            var clock = new TestClock();
            var target = FakeTarget.Valid(wrongCharacter: true);
            target.Snapshot.MultiModeEnabled = originalMultiMode;
            using var service = new DadWakeTakeoverService(target, clock.UtcNow);
            StartRelog(service, target, clock);
            ActivateTargetCharacter(target);
            service.Update();
            Assert.Equal(DadWakeTakeoverPhase.Ready, service.Handle(StatusRequest()).Phase);

            var next = Request();
            next.SchedulerRunId = next.OperationToken = $"next-preset-slot-{slot}";
            var rejected = service.Handle(next);
            Assert.Contains("conflicting", rejected.BlockedReason, StringComparison.OrdinalIgnoreCase);

            var cancel = Request();
            cancel.MessageKind = DadWakeTakeoverMessageKind.Cancel;
            Assert.True(DadSchedulerRoutingRules.IsTakeoverCancellationComplete(service.Handle(cancel)));
            Assert.Equal(originalMultiMode, target.Snapshot.MultiModeEnabled);
            Assert.Equal("Target Character@World", target.Snapshot.Participant.ActiveCharacterKey.Value);

            var accepted = service.Handle(next);
            Assert.DoesNotContain("conflicting", accepted.BlockedReason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(next.OperationToken, accepted.OperationToken);
            Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        }
    }

    [Fact]
    public void CancellingRejectedTokenAcknowledgesNoOwnershipWithoutTouchingActiveTakeover()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.Snapshot.Participant.WorkerSessionId = new DadWorkerSessionId("worker-a");
        using var service = new DadWakeTakeoverService(target);
        service.Handle(Request());
        var rejected = Request();
        rejected.SchedulerRunId = rejected.OperationToken = "rejected-next-run";
        Assert.Equal(DadWakeAcknowledgementState.Rejected, service.Handle(rejected).AcknowledgementState);
        var actions = target.Actions.ToArray();

        rejected.MessageKind = DadWakeTakeoverMessageKind.Cancel;
        for (var retry = 0; retry < 2; retry++)
        {
            var result = service.Handle(rejected);
            Assert.True(DadSchedulerRoutingRules.IsTakeoverCancellationComplete(
                rejected, target.Snapshot.Participant.WorkerSessionId, result));
            Assert.Equal(rejected.OperationToken, result.OperationToken);
            Assert.Equal("scheduler-run", service.GetActiveStatus()!.OperationToken);
            Assert.Equal(actions, target.Actions);
        }

        rejected.MessageKind = DadWakeTakeoverMessageKind.Prepare;
        Assert.Equal(DadWakeTakeoverPhase.Cancelled, service.Handle(rejected).Phase);
        rejected.MessageKind = DadWakeTakeoverMessageKind.Go;
        rejected.CommitKind = DadWakeCommitKind.Reset;
        rejected.ExecutionTimeUtc = DateTime.UtcNow.AddSeconds(-1);
        Assert.Equal(DadWakeTakeoverPhase.Cancelled, service.Handle(rejected).Phase);
        Assert.Equal("scheduler-run", service.GetActiveStatus()!.OperationToken);
        Assert.Equal(actions, target.Actions);
    }

    [Fact]
    public void DisconnectAfterReadyCleansUpInPlaceWithoutCharacterRestoration()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        ActivateTargetCharacter(target);
        service.Update();

        service.OnCoordinatorDisconnected();

        Assert.Equal("Target Character@World", target.Snapshot.Participant.ActiveCharacterKey.Value);
        Assert.True(target.Snapshot.MultiModeEnabled);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        Assert.DoesNotContain("RelogTarget:Other Character@World", target.Actions);
    }

    [Fact]
    public void StopAllAfterReadyCleansUpInPlaceWithoutCharacterRestoration()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        ActivateTargetCharacter(target);
        service.Update();

        service.StopAll("Stop-all in-place cleanup test.");

        Assert.Equal("Target Character@World", target.Snapshot.Participant.ActiveCharacterKey.Value);
        Assert.True(target.Snapshot.MultiModeEnabled);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        Assert.DoesNotContain("RelogTarget:Other Character@World", target.Actions);
    }

    [Fact]
    public void DisposalAfterReadyCleansUpInPlaceWithoutCharacterRestoration()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        ActivateTargetCharacter(target);
        service.Update();

        service.Dispose();

        Assert.Equal("Target Character@World", target.Snapshot.Participant.ActiveCharacterKey.Value);
        Assert.True(target.Snapshot.MultiModeEnabled);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
        Assert.DoesNotContain("RelogTarget:Other Character@World", target.Actions);
    }

    [Fact]
    public void CancellationAfterResetRestoresMultiModeOnCurrentCharacterWithoutRelog()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Prepare(service, target);
        ExecuteGo(service, clock, DadWakeCommitKind.Reset);
        var cancel = Request();
        cancel.MessageKind = DadWakeTakeoverMessageKind.Cancel;

        var restored = service.Handle(cancel);

        Assert.True(DadSchedulerRoutingRules.IsTakeoverCancellationComplete(restored));
        Assert.Equal("Other Character@World", target.Snapshot.Participant.ActiveCharacterKey.Value);
        Assert.True(target.Snapshot.MultiModeEnabled);
        Assert.DoesNotContain(target.Actions, static action =>
            action.StartsWith("RelogTarget:", StringComparison.Ordinal));
        Assert.Equal(1, target.Actions.Count(static action => action == "SetMultiMode:True"));
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
        ActivateTargetCharacter(target);
        target.Snapshot.PostArReady = false;
        target.Snapshot.Participant.PostArReady = false;

        service.Update();

        var ready = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverPhase.Ready, ready.Phase);
        Assert.False(ready.PostArReady);
        Assert.True(ready.Snapshot.WorldReadyStable);
        Assert.Equal(1, target.Actions.Count(static action => action == "RelogCharacter"));
    }

    [Fact]
    public void CompletedWakeRefreshesTransientExternalHoldWithoutRepeatingTakeover()
    {
        var (service, target, clock) = CompletedWakeWithFreshSnapshots(holdAtCompletion: true);
        var held = service.Handle(StatusRequest());
        Assert.True(held.ExternalAutomationHeld);
        Assert.False(DadSchedulerRoutingRules.CanAcceptReadyAcknowledgement(ReadySlot(), held));
        var actions = target.Actions.ToArray();
        var captures = target.CaptureForceFlags.Count;

        ReplaceLiveReadySnapshot(target);
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Idle);
        clock.Advance(TimeSpan.FromSeconds(1));
        var slot = ReadySlot();
        slot.ClientConnected = true;
        slot.TakeoverPhase = held.Phase;
        slot.Ready = false;
        var next = DadSchedulerRoutingRules.ResolveNextTakeoverAction(slot, clock.Now);
        Assert.True(next.CanDispatch);
        Assert.Equal(DadWakeTakeoverMessageKind.Status, next.MessageKind);
        Assert.Equal(DadWakeCommitKind.None, next.CommitKind);
        var poll = Request();
        poll.MessageKind = next.MessageKind;
        var ready = service.Handle(poll);

        Assert.True(target.CaptureForceFlags.Count > captures);
        Assert.True(target.CaptureForceFlags[^1]);
        Assert.True(DadSchedulerRoutingRules.CanAcceptReadyAcknowledgement(ReadySlot(), ready));
        Assert.True(ready.PostArReady);
        Assert.False(ready.ExternalAutomationHeld);
        Assert.True(held.ExternalAutomationHeld); // Prior wire response remains a historical value.
        Assert.Equal(held.ReadyUtc, ready.ReadyUtc);
        Assert.Equal(held.ResetIssuedUtc, ready.ResetIssuedUtc);
        Assert.Equal(held.RelogIssuedUtc, ready.RelogIssuedUtc);
        Assert.Equal(actions, target.Actions);
    }

    [Fact]
    public void CompletedWakeActiveStatusAlsoRefreshesWithoutReplayingMutations()
    {
        var (service, target, _) = CompletedWakeWithFreshSnapshots(holdAtCompletion: true);
        Assert.True(service.GetActiveStatus()!.ExternalAutomationHeld);
        ReplaceLiveReadySnapshot(target);
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Idle);
        var actions = target.Actions.ToArray();
        Assert.True(DadSchedulerRoutingRules.CanAcceptReadyAcknowledgement(ReadySlot(), service.GetActiveStatus()!));
        Assert.Equal(actions, target.Actions);
    }

    [Theory]
    [InlineData("external-hold")]
    [InlineData("ar-busy")]
    [InlineData("ar-missing")]
    [InlineData("multi-mode")]
    [InlineData("offline")]
    [InlineData("world-unstable")]
    [InlineData("wrong-character")]
    [InlineData("wrong-account")]
    [InlineData("wrong-worker")]
    [InlineData("route-lost")]
    [InlineData("account-mismatch")]
    [InlineData("character-unknown")]
    [InlineData("dad-disabled")]
    [InlineData("mutation-disabled")]
    [InlineData("capture-failed")]
    public void CompletedWakeCannotReuseOldSafeProofAfterLiveSafetyChanges(string change)
    {
        var (service, target, _) = CompletedWakeWithFreshSnapshots();
        Assert.True(DadSchedulerRoutingRules.CanAcceptReadyAcknowledgement(ReadySlot(), service.Handle(StatusRequest())));
        var actions = target.Actions.ToArray();
        ReplaceLiveReadySnapshot(target);
        switch (change)
        {
            case "external-hold": target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Busy); break;
            case "ar-busy": target.Snapshot.AutoRetainerBusy = true; break;
            case "ar-missing": target.Snapshot.AutoRetainerAvailable = false; break;
            case "multi-mode": target.Snapshot.MultiModeEnabled = true; break;
            case "offline": target.Snapshot.Participant.IsAvailable = false; break;
            case "world-unstable": target.Snapshot.Participant.WorldReadyStable = false; break;
            case "wrong-character": target.Snapshot.Participant.ActiveCharacterKey = new("Other Character@World"); break;
            case "wrong-account": target.Snapshot.Participant.ManagedAccountKey = new("other-account"); break;
            case "wrong-worker": target.Snapshot.Participant.WorkerSessionId = new("other-worker"); break;
            case "route-lost": target.Snapshot.ClientRouteConnected = false; break;
            case "account-mismatch": target.Snapshot.AccountMatches = false; break;
            case "character-unknown": target.Snapshot.CharacterKnownToAccount = false; break;
            case "dad-disabled": target.Snapshot.DadEnabled = false; break;
            case "mutation-disabled": target.Snapshot.RemoteMutationAllowed = false; break;
            case "capture-failed": target.OnCapture = _ => throw new InvalidOperationException("Read failed."); break;
        }

        var unsafeResult = service.Handle(StatusRequest());

        Assert.False(DadSchedulerRoutingRules.CanAcceptReadyAcknowledgement(ReadySlot(), unsafeResult));
        Assert.Equal(DadWakeTakeoverPhase.Ready, unsafeResult.Phase); // No replay of a completed operation.
        Assert.Equal(actions, target.Actions);
        target.OnCapture = null;
        ReplaceLiveReadySnapshot(target);
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Idle);
        Assert.True(DadSchedulerRoutingRules.CanAcceptReadyAcknowledgement(ReadySlot(), service.Handle(StatusRequest())));
        Assert.Equal(actions, target.Actions);
    }

    [Fact]
    public void CompletedWakeLatePrepareAndGoOnlyRefreshAndDoNotRearm()
    {
        var (service, target, clock) = CompletedWakeWithFreshSnapshots();
        var actions = target.Actions.ToArray();
        var captures = target.CaptureForceFlags.Count;
        foreach (var request in new[] { Request(), Go(DadWakeCommitKind.Reset, clock.Now), Go(DadWakeCommitKind.Relog, clock.Now) })
            Assert.Equal(DadWakeTakeoverPhase.Ready, service.Handle(request).Phase);
        Assert.True(target.CaptureForceFlags.Count >= captures + 3);
        Assert.Equal(actions, target.Actions);
    }

    [Fact]
    public void CancelledCompletedWakeCannotRefreshOrRearmOnLateMessages()
    {
        var (service, target, clock) = CompletedWakeWithFreshSnapshots();
        var cancel = Request();
        cancel.MessageKind = DadWakeTakeoverMessageKind.Cancel;
        Assert.Equal(DadWakeTakeoverPhase.Cancelled, service.Handle(cancel).Phase);
        var captures = target.CaptureForceFlags.Count;
        var actions = target.Actions.ToArray();
        target.OnCapture = _ => throw new InvalidOperationException("Cancelled token must not inspect again.");
        foreach (var request in new[] { StatusRequest(), Request(), Go(DadWakeCommitKind.Relog, clock.Now) })
            Assert.Equal(DadWakeTakeoverPhase.Cancelled, service.Handle(request).Phase);
        Assert.Equal(captures, target.CaptureForceFlags.Count);
        Assert.Equal(actions, target.Actions);
    }

    [Fact]
    public void OldCompletedWakeReadAndCancelCannotReleaseNewerTakeoverOwnership()
    {
        // A mutating wake retains its restoration lease until Cancel; use an
        // already-online completion here so a second token can legitimately own it.
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: false);
        target.Snapshot.MultiModeEnabled = false;
        target.Snapshot.Participant.ManagedAccountKey = new("account-a");
        target.Snapshot.Participant.WorkerSessionId = new("worker-a");
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.NotLoaded);
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Assert.Equal(DadWakeTakeoverPhase.Prepared, service.Handle(Request()).Phase);
        ExecuteGo(service, clock, DadWakeCommitKind.Reset);
        ExecuteGo(service, clock, DadWakeCommitKind.Relog);
        Assert.Equal(DadWakeTakeoverPhase.Ready, service.Handle(StatusRequest()).Phase);
        Assert.DoesNotContain(target.Actions, IsMutation);
        ReplaceLiveReadySnapshot(target);
        target.Snapshot.CorrectCharacter = false;
        target.Snapshot.Participant.ActiveCharacterKey = new("Other Character@World");
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.NotLoaded);
        var newer = Request();
        newer.OperationToken = "new-operation";
        newer.SchedulerRunId = "new-run";
        Assert.Equal(DadWakeTakeoverPhase.Prepared, service.Handle(newer).Phase);
        Assert.True(target.Snapshot.DadOwnsSuppression);
        var actions = target.Actions.ToArray();
        var reservations = target.ReserveCount;
        var releases = target.ReleaseTokens.ToArray();

        var old = service.Handle(StatusRequest());

        Assert.False(DadSchedulerRoutingRules.CanAcceptReadyAcknowledgement(ReadySlot(), old));
        Assert.True(target.Snapshot.DadOwnsSuppression);
        Assert.Equal(actions, target.Actions);
        Assert.Equal(reservations, target.ReserveCount);
        Assert.Equal(releases, target.ReleaseTokens);
        Assert.Equal("new-operation", service.GetActiveStatus()!.OperationToken);

        var cancelOld = Request();
        cancelOld.MessageKind = DadWakeTakeoverMessageKind.Cancel;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var cancelled = service.Handle(cancelOld);
            Assert.Equal(DadWakeTakeoverPhase.Cancelled, cancelled.Phase);
            Assert.Equal(DadWakeAcknowledgementState.Executed, cancelled.AcknowledgementState);
            Assert.True(target.Snapshot.DadOwnsSuppression);
            Assert.True(target.Snapshot.AutoRetainerSuppressed);
            Assert.Equal(actions, target.Actions);
            Assert.Equal(reservations, target.ReserveCount);
            Assert.Equal(releases, target.ReleaseTokens);
            Assert.Equal("new-operation", service.GetActiveStatus()!.OperationToken);
        }

        newer.MessageKind = DadWakeTakeoverMessageKind.Cancel;
        Assert.Equal(DadWakeTakeoverPhase.Cancelled, service.Handle(newer).Phase);
        Assert.False(target.Snapshot.DadOwnsSuppression);
        Assert.False(target.Snapshot.AutoRetainerSuppressed);
        Assert.Equal(actions.Count(action => action == "ReleaseSuppression") + 1,
            target.Actions.Count(action => action == "ReleaseSuppression"));
        Assert.Null(service.GetActiveStatus());
    }

    private static (DadWakeTakeoverService Service, FakeTarget Target, TestClock Clock) CompletedWakeWithFreshSnapshots(
        bool holdAtCompletion = false)
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.Snapshot.MultiModeEnabled = false;
        target.Snapshot.Participant.ManagedAccountKey = new("account-a");
        target.Snapshot.Participant.WorkerSessionId = new("worker-a");
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        StartRelog(service, target, clock);
        ActivateTargetCharacter(target);
        if (holdAtCompletion)
            target.OnSuppressionReleased = () => target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Busy);
        service.Update();
        Assert.Equal(DadWakeTakeoverPhase.Ready, service.Handle(StatusRequest()).Phase);
        return (service, target, clock);
    }

    private static void ReplaceLiveReadySnapshot(FakeTarget target)
    {
        // The real target allocates a new snapshot on each Capture. Replacing this fake's
        // backing snapshot reproduces the stale-cache bug instead of mutating its cache in place.
        target.Snapshot = FakeTarget.Valid(wrongCharacter: false).Snapshot;
        target.Snapshot.MultiModeEnabled = false;
        target.Snapshot.Participant.ManagedAccountKey = new("account-a");
        target.Snapshot.Participant.WorkerSessionId = new("worker-a");
    }

    private static DadSchedulerSlotState ReadySlot() => new()
    {
        RequiredAccountKey = new("account-a"),
        RequiredCharacterKey = new("Target Character@World"),
        MatchedWorkerSessionId = new("worker-a"),
    };

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
        ActivateTargetCharacter(target);
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
        ActivateTargetCharacter(target);
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
    public void FreshAttemptTokenBypassesPreV0301SameTokenReleasedTrapWithoutChangingLogicalIdentity()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.Snapshot.MultiModeEnabled = false;
        target.Reservation.State = DadVermaxionReservationState.Released;
        target.Reservation.OperationToken = "scheduler-run";
        target.ReservationStateOnReserve = DadVermaxionReservationState.Granted;
        target.EmulatePreV0301SameTokenReleasedTrap = true;
        target.ReleasedReservationTokens.Add("scheduler-run");
        var attemptTokens = new Queue<string>(["attempt-a"]);
        var service = new DadWakeTakeoverService(target, attemptTokens.Dequeue);

        var result = service.Handle(Request());

        Assert.Equal(DadWakeTakeoverPhase.Prepared, result.Phase);
        Assert.Equal("scheduler-run", result.OperationToken);
        Assert.Equal(["attempt-a"], target.ReserveTokens);
        Assert.DoesNotContain("scheduler-run", target.ReserveTokens);
        Assert.Contains("attempt-a", target.CaptureOperationTokens);
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void FailedCleanupRetainsOldAttemptTokenBeforeNextEpochAllocatesFreshToken()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.Snapshot.MultiModeEnabled = false;
        target.ReservationStatesOnReserve.Enqueue(DadVermaxionReservationState.Rejected);
        target.ReservationStatesOnReserve.Enqueue(DadVermaxionReservationState.Granted);
        target.ReservationReleaseResults.Enqueue(false);
        target.ReservationReleaseResults.Enqueue(true);
        var attemptTokens = new Queue<string>(["attempt-a", "attempt-b"]);
        var service = new DadWakeTakeoverService(target, attemptTokens.Dequeue, clock.UtcNow);

        var retry = service.Handle(Request());

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, retry.Phase);
        Assert.Equal(["attempt-a"], target.ReserveTokens);
        Assert.Equal(["attempt-a"], target.ReleaseTokens);
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);

        service.Update();

        Assert.Equal(["attempt-a"], target.ReserveTokens);
        Assert.Equal(["attempt-a", "attempt-a"], target.ReleaseTokens);

        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();

        var prepared = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverPhase.Prepared, prepared.Phase);
        Assert.Equal("scheduler-run", prepared.OperationToken);
        Assert.Equal(["attempt-a", "attempt-b"], target.ReserveTokens);
        Assert.Equal(2, target.ReserveTokens.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void StaleGrantIsIgnoredUntilCurrentAttemptGrantPreparesWithoutMutation()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.Snapshot.MultiModeEnabled = false;
        target.Reservation.State = DadVermaxionReservationState.Pending;
        var attemptTokens = new Queue<string>(["attempt-a"]);
        var service = new DadWakeTakeoverService(target, attemptTokens.Dequeue);
        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, service.Handle(Request()).Phase);

        var staleGrant = target.Reservation.Clone();
        staleGrant.OperationToken = "stale-attempt";
        staleGrant.State = DadVermaxionReservationState.Granted;
        service.OnVermaxionReservationGranted(staleGrant);

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, service.Handle(StatusRequest()).Phase);
        Assert.DoesNotContain("AcquireSuppression", target.Actions);
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);

        Grant(target);
        service.OnVermaxionReservationGranted(target.Reservation);

        var prepared = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverPhase.Prepared, prepared.Phase);
        Assert.Equal("scheduler-run", prepared.OperationToken);
        Assert.Equal(["attempt-a"], target.ReserveTokens);
        Assert.Equal(1, target.Actions.Count(static action => action == "AcquireSuppression"));
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void TransientUnsafeStateRetriesReleasedReserveWithoutArFallbackAndResetsOnce()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.UtcNow = clock.UtcNow;
        target.Snapshot.MultiModeEnabled = false;
        Grant(target);
        var attemptTokens = new Queue<string>(["attempt-a", "attempt-b", "attempt-c"]);
        var service = new DadWakeTakeoverService(target, attemptTokens.Dequeue, clock.UtcNow);

        Assert.Equal(DadWakeTakeoverPhase.Prepared, service.Handle(Request()).Phase);
        Assert.Equal(1, target.ReserveCount);
        Assert.Equal(1, target.Actions.Count(static action => action == "AcquireSuppression"));
        service.Update();
        Assert.Equal(["attempt-a"], target.ReserveTokens);
        Assert.All(
            target.CaptureOperationTokens.Where(static token => !string.IsNullOrWhiteSpace(token)),
            static token => Assert.Equal("attempt-a", token));

        target.Snapshot.Participant.WorldReadyStable = false;
        service.Update();

        var unsafeWait = service.GetActiveStatus();
        Assert.NotNull(unsafeWait);
        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, unsafeWait.Phase);
        Assert.Contains("ReleaseSuppression", target.Actions);
        Assert.Contains("ReleaseReservation", target.Actions);
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);

        target.Reservation.Version = DadVermaxionHandoffContract.Version;
        target.Reservation.WireFormat = DadVermaxionReservationWireFormat.CanonicalString;
        target.Reservation.Summary = "Prior reservation is terminally released.";
        target.ReservationStateOnReserve = DadVermaxionReservationState.Released;
        target.Snapshot.Participant.WorldReadyStable = true;
        service.Update();

        var retry = service.GetActiveStatus();
        Assert.NotNull(retry);
        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, retry.Phase);
        Assert.Contains("fresh reservation", retry.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("five-second cadence", retry.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, target.ReserveCount);
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);

        target.ReservationStateOnReserve = DadVermaxionReservationState.Granted;
        target.Reservation.Summary = "Fresh VERMAXION grant.";
        target.Reservation.VermaxionActivity = "DadHandoff";
        target.Reservation.VermaxionState = "Granted";
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Update();

        Assert.Equal(DadWakeTakeoverPhase.Prepared, service.GetActiveStatus()!.Phase);
        Assert.Equal(3, target.ReserveCount);
        Assert.Equal(["attempt-a", "attempt-b", "attempt-c"], target.ReserveTokens);
        Assert.Equal(3, target.ReserveTokens.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(2, target.Actions.Count(static action => action == "AcquireSuppression"));
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);

        ExecuteGo(service, clock, DadWakeCommitKind.Reset);

        Assert.Equal(1, target.Actions.Count(static action => action == "DisableAutoRetainer"));
        Assert.Equal(1, target.Actions.Count(static action => action == "ResetAutoRetainer"));
        Assert.DoesNotContain("RelogCharacter", target.Actions);
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
        target.UtcNow = clock.UtcNow;
        Prepare(service, target);
        ExecuteGo(service, clock, DadWakeCommitKind.Reset);
        ExecuteGo(service, clock, DadWakeCommitKind.Relog);
        var result = service.Handle(StatusRequest());
        Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, result.Phase);
        Assert.Equal(expectedRelogCount, target.Actions.Count(static action => action == "RelogCharacter"));
        return result;
    }

    private static bool IsMutation(string action)
        => action is "DisableAutoRetainer" or "ResetAutoRetainer" or "RelogCharacter" or
               "DisableAutoRetainerMultiMode" or "DismissTitleMovie" ||
           action.StartsWith("ConnectAndLogin:", StringComparison.Ordinal) ||
           action.StartsWith("SetMultiMode", StringComparison.Ordinal);

    private static void ConfigureTitleIdle(FakeTarget target)
    {
        target.Snapshot.ClientRouteConnected = true;
        target.Snapshot.CorrectCharacter = false;
        target.Snapshot.PostArReady = false;
        target.Snapshot.Participant.IsAvailable = false;
        target.Snapshot.Participant.WorldReadyStable = false;
        target.Snapshot.Participant.ActiveCharacterKey = new DadCharacterKey(string.Empty);
        target.Snapshot.TitleSurface = DadTitleSurface.TitleMenu;
        target.Snapshot.TitleSurfaceEvidenceFresh = true;
        target.Snapshot.TitleClientLoggedOut = true;
        target.Snapshot.TitleNoActiveConditionFlags = true;
        target.Snapshot.TitleMovieExactReady = false;
        target.Snapshot.TitleMenuEvidenceFresh = true;
        target.Snapshot.TitleMenuReady = true;
        target.Snapshot.TitleNavigationOverlayVisible = false;
        target.Snapshot.TitleConnectionOverlayVisible = false;
        target.Snapshot.TitleErrorOverlayVisible = false;
        target.Snapshot.AutoRetainerAvailable = true;
        target.Snapshot.AutoRetainerBusy = false;
        target.Snapshot.MultiModeEnabled = true;
        target.Snapshot.SuppressionReadable = true;
        target.Snapshot.AutoRetainerSuppressed = false;
        target.Snapshot.DadOwnsSuppression = false;
        target.Snapshot.DadOwnsCharacterPostprocess = false;
        target.Snapshot.ExternalAutomationHeld = false;
        target.Snapshot.VermaxionStatusEvidenceFresh = true;
        target.Snapshot.VermaxionStatusKind = DadVermaxionReadinessKind.Idle;
        target.Snapshot.LifestreamAvailable = true;
        target.Snapshot.LifestreamBusy = false;
        target.Snapshot.LifestreamCanAutoLogin = true;
    }

    private static void ActivateTargetCharacter(FakeTarget target)
    {
        target.Snapshot.CorrectCharacter = true;
        target.Snapshot.PostArReady = true;
        target.Snapshot.Participant.IsAvailable = true;
        target.Snapshot.Participant.WorldReadyStable = true;
        target.Snapshot.Participant.ActiveCharacterKey = new DadCharacterKey("Target Character@World");
        target.Snapshot.Participant.Character.CharacterKey = "Target Character@World";
        target.Snapshot.Participant.Character.ContentId = 20;
        target.Snapshot.LifestreamAvailable = true;
        target.Snapshot.LifestreamBusy = false;
        target.Snapshot.TitleSurface = DadTitleSurface.None;
        target.Snapshot.TitleSurfaceEvidenceFresh = true;
        target.Snapshot.TitleMenuReady = false;
    }

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

    [Fact]
    public void AlreadyOnlineIdleAccountWaitsForCrewGoWithoutResetOrRelog()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: false);
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.NotLoaded);
        target.Snapshot.MultiModeEnabled = false;
        var service = new DadWakeTakeoverService(target, clock.UtcNow);

        Assert.Equal(DadWakeTakeoverPhase.Prepared, service.Handle(Request()).Phase);
        clock.Advance(TimeSpan.FromHours(1));
        service.Update();
        Assert.Equal(DadWakeTakeoverPhase.Prepared, service.Handle(StatusRequest()).Phase);
        Assert.DoesNotContain(target.Actions, IsMutation);

        ExecuteGo(service, clock, DadWakeCommitKind.Reset);
        Assert.Equal(DadWakeTakeoverPhase.ResetVerified, service.Handle(StatusRequest()).Phase);
        Assert.DoesNotContain(target.Actions, IsMutation);
        ExecuteGo(service, clock, DadWakeCommitKind.Relog);

        Assert.Equal(DadWakeTakeoverPhase.Ready, service.Handle(StatusRequest()).Phase);
        Assert.DoesNotContain(target.Actions, IsMutation);
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.False(target.Snapshot.DadOwnsSuppression);
    }

    [Fact]
    public void FourAccountIdleCrewUsesSchedulerPipelinesAndRelogsOnlyDifferentCharacters()
    {
        var clock = new TestClock();
        var targets = Enumerable.Range(0, 4).Select(index =>
        {
            var target = FakeTarget.Valid(wrongCharacter: index >= 2);
            target.LegacyStatus = Legacy(DadVermaxionReadinessKind.NotLoaded);
            target.Snapshot.MultiModeEnabled = false;
            target.UtcNow = clock.UtcNow;
            return target;
        }).ToArray();
        var services = targets.Select(target => new DadWakeTakeoverService(target, clock.UtcNow)).ToArray();
        var requests = Enumerable.Range(0, 4).Select(index =>
        {
            var request = Request();
            request.SlotId = $"Slot{index + 1}";
            request.AccountKey = new DadAccountKey($"account-{index}");
            return request;
        }).ToArray();

        for (var index = 0; index < 4; index++)
            Assert.Equal(DadWakeTakeoverPhase.Prepared, services[index].Handle(requests[index]).Phase);
        var slots = requests.Select(request => new DadSchedulerSlotState
        {
            SlotId = request.SlotId,
            RequiredAccountKey = request.AccountKey,
            RequiredCharacterKey = request.CharacterKey,
            ClientConnected = true,
            BasePostArReady = true,
            AutoRetainerAvailable = true,
            TakeoverPhase = DadWakeTakeoverPhase.Prepared,
        }).ToArray();
        Assert.All(targets, target => Assert.DoesNotContain(target.Actions, IsMutation));

        var resetAt = clock.Now.AddSeconds(5);
        for (var index = 0; index < 4; index++)
        {
            requests[index].MessageKind = DadWakeTakeoverMessageKind.Go;
            var decision = DadSchedulerRoutingRules.ResolveNextTakeoverAction(slots[index], clock.Now);
            Assert.Equal(DadWakeCommitKind.Reset, decision.CommitKind);
            requests[index].CommitKind = decision.CommitKind;
            requests[index].ExecutionTimeUtc = decision.ExecutionTimeUtc;
            Assert.Equal(resetAt, decision.ExecutionTimeUtc);
            services[index].Handle(requests[index]);
        }
        clock.Advance(TimeSpan.FromSeconds(4));
        foreach (var service in services) service.Update();
        Assert.All(targets, target => Assert.DoesNotContain(target.Actions, IsMutation));
        clock.Advance(TimeSpan.FromSeconds(1));
        foreach (var service in services) service.Update();

        var relogAt = clock.Now.AddSeconds(5);
        for (var index = 0; index < 4; index++)
        {
            slots[index].TakeoverPhase = services[index].GetActiveStatus()!.Phase;
            var decision = DadSchedulerRoutingRules.ResolveNextTakeoverAction(slots[index], clock.Now);
            Assert.Equal(DadWakeCommitKind.Relog, decision.CommitKind);
            requests[index].CommitKind = decision.CommitKind;
            requests[index].ExecutionTimeUtc = decision.ExecutionTimeUtc;
            Assert.Equal(relogAt, decision.ExecutionTimeUtc);
            services[index].Handle(requests[index]);
        }
        Assert.All(targets, target => Assert.DoesNotContain("RelogCharacter", target.Actions));
        clock.Advance(TimeSpan.FromSeconds(5));
        foreach (var service in services) service.Update();

        for (var index = 0; index < 4; index++)
        {
            Assert.DoesNotContain("Arm", targets[index].Actions);
            if (index < 2)
            {
                requests[index].MessageKind = DadWakeTakeoverMessageKind.Status;
                Assert.Equal(DadWakeTakeoverPhase.Ready, services[index].Handle(requests[index]).Phase);
                Assert.DoesNotContain(targets[index].Actions, IsMutation);
            }
            else
            {
                Assert.Equal(DadWakeTakeoverPhase.WaitingForCharacter, services[index].GetActiveStatus()!.Phase);
                Assert.Single(targets[index].Actions, action => action == "RelogCharacter");
                Assert.True(targets[index].Snapshot.DadOwnsSuppression);
            }
        }
    }

    [Fact]
    public void IdleSuppressionAcquisitionRecapturesBusyStateAndYields()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.NotLoaded);
        target.OnSuppressionAcquired = () => target.Snapshot.AutoRetainerBusy = true;
        var service = new DadWakeTakeoverService(target);

        var result = service.Handle(Request());

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, result.Phase);
        Assert.Contains("ReleaseSuppression", target.Actions);
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IdleHandoffStopsWhenAccountOrRouteDoesNotMatch(bool accountMismatch)
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.NotLoaded);
        if (accountMismatch)
            target.Snapshot.AccountMatches = false;
        else
            target.Snapshot.ClientRouteConnected = false;
        var service = new DadWakeTakeoverService(target);

        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, service.Handle(Request()).Phase);
        Assert.Empty(target.Actions);
    }

    [Fact]
    public void IdleHandoffCancellationReleasesOnlyDadSuppression()
    {
        var target = FakeTarget.Valid(wrongCharacter: false);
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.NotLoaded);
        var service = new DadWakeTakeoverService(target);
        Assert.Equal(DadWakeTakeoverPhase.Prepared, service.Handle(Request()).Phase);
        var cancel = Request();
        cancel.MessageKind = DadWakeTakeoverMessageKind.Cancel;

        Assert.Equal(DadWakeTakeoverPhase.Cancelled, service.Handle(cancel).Phase);
        Assert.False(target.Snapshot.DadOwnsSuppression);
        Assert.DoesNotContain("Finish:Stop", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void ManuallyLoadedTargetResumesIdleTakeoverWithoutRelog()
    {
        var clock = new TestClock();
        var target = FakeTarget.Valid(wrongCharacter: true);
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.NotLoaded);
        target.Snapshot.Participant.WorldReadyStable = false;
        target.Snapshot.MultiModeEnabled = false;
        var service = new DadWakeTakeoverService(target, clock.UtcNow);
        Assert.Equal(DadWakeTakeoverPhase.AwaitingArHook, service.Handle(Request()).Phase);

        target.Snapshot.Participant.WorldReadyStable = true;
        target.Snapshot.Participant.ActiveCharacterKey = Request().CharacterKey;
        target.Snapshot.Participant.Character.CharacterKey = Request().CharacterKey.Value;
        target.Snapshot.Participant.Character.ContentId = 20;
        target.Snapshot.CorrectCharacter = true;
        service.Update();
        Assert.Equal(DadWakeTakeoverPhase.Prepared, service.Handle(StatusRequest()).Phase);
        ExecuteGo(service, clock, DadWakeCommitKind.Reset);
        ExecuteGo(service, clock, DadWakeCommitKind.Relog);

        Assert.Equal(DadWakeTakeoverPhase.Ready, service.Handle(StatusRequest()).Phase);
        Assert.DoesNotContain("Arm", target.Actions);
        Assert.DoesNotContain(target.Actions, IsMutation);
    }

    [Fact]
    public void NoVermaxionTitleLoginRecapturesAutomationStateAfterDisablingMultiMode()
    {
        var target = FakeTarget.Valid(wrongCharacter: true);
        ConfigureTitleIdle(target);
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.NotLoaded);
        target.Snapshot.VermaxionStatusKind = DadVermaxionReadinessKind.NotLoaded;
        var service = new DadWakeTakeoverService(target);
        service.Handle(Request());
        Assert.Equal(["SetMultiMode:False"], target.Actions);
        target.LegacyStatus = Legacy(DadVermaxionReadinessKind.Busy);
        target.Snapshot.VermaxionStatusKind = DadVermaxionReadinessKind.Busy;

        service.Update();

        Assert.Empty(target.TitleLoginCaptureCounts);
        Assert.Equal(["SetMultiMode:False"], target.Actions);
    }

    private sealed class FakeTarget : IDadWakeTakeoverTarget, IDadTitleMovieDismissalTarget
    {
        public DadWakeTakeoverTargetSnapshot Snapshot { get; set; } = new();
        public DadVermaxionReservationStatus Reservation { get; } = new()
        {
            State = DadVermaxionReservationState.NotLoaded,
            Summary = "VERMAXION not loaded.",
        };
        public DadVermaxionReadinessStatus LegacyStatus { get; set; } = Legacy(DadVermaxionReadinessKind.Idle);
        public List<string> Actions { get; } = [];
        public List<string> CaptureOperationTokens { get; } = [];
        public List<string> ReserveTokens { get; } = [];
        public List<string> ReleaseTokens { get; } = [];
        public List<string> ArmOperationTokens { get; } = [];
        public HashSet<string> ReleasedReservationTokens { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int ReserveCount { get; private set; }
        public DadVermaxionReservationState ReservationStateOnReserve { get; set; } = DadVermaxionReservationState.NotLoaded;
        public Queue<DadVermaxionReservationState> ReservationStatesOnReserve { get; } = new();
        public bool EmulatePreV0301SameTokenReleasedTrap { get; set; }
        public Action? OnSuppressionAcquired { get; set; }
        public Action<DadWakeTakeoverCommand>? OnCommandExecuted { get; set; }
        public Queue<bool> FinishResults { get; } = new();
        public Queue<bool> SuppressionReleaseResults { get; } = new();
        public Queue<bool> ReservationReleaseResults { get; } = new();
        public Queue<DadLifestreamLoginOutcome> TitleLoginOutcomes { get; } = new();
        public List<int> TitleLoginCaptureCounts { get; } = [];
        public List<bool> CaptureForceFlags { get; } = [];
        public Action<bool>? OnCapture { get; set; }
        public Action? OnSuppressionReleased { get; set; }
        public Func<DateTime>? UtcNow { get; set; }
        public bool DismissTitleMovieAccepted { get; set; } = true;
        public bool ThrowOnTitleLogin { get; set; }
        public int RelogDispatchFailure { get; set; }
        public bool ResolveCorrectCharacterFromRequest { get; set; }

        public static FakeTarget Valid(bool wrongCharacter)
        {
            var target = new FakeTarget();
            target.Snapshot.DadEnabled = true;
            target.Snapshot.RemoteMutationAllowed = true;
            target.Snapshot.ClientRouteConnected = true;
            target.Snapshot.AccountMatches = true;
            target.Snapshot.CharacterKnownToAccount = true;
            target.Snapshot.CorrectCharacter = !wrongCharacter;
            target.Snapshot.PostArReady = true;
            target.Snapshot.AutoRetainerAvailable = true;
            target.Snapshot.AutoRetainerBusy = false;
            target.Snapshot.LifestreamAvailable = true;
            target.Snapshot.LifestreamBusy = false;
            target.Snapshot.LifestreamCanAutoLogin = true;
            target.Snapshot.MultiModeEnabled = true;
            target.Snapshot.SuppressionReadable = true;
            target.Snapshot.Participant = new DadParticipantSnapshot
            {
                IsAvailable = true,
                PostArReady = true,
                WorldReadyStable = true,
                ActiveCharacterKey = new DadCharacterKey(
                    wrongCharacter ? "Other Character@World" : "Target Character@World"),
                Character = new DadAcquiredCharacter
                {
                    CharacterKey = wrongCharacter ? "Other Character@World" : "Target Character@World",
                    ContentId = wrongCharacter ? 10UL : 20UL,
                    WorldId = 1,
                    WorldName = "World",
                },
                CurrentLocation = new DadWorldLocationObservation
                {
                    WorldId = 1,
                    WorldName = "World",
                    DataCenterId = 1,
                    DataCenterName = "Data Center",
                    RegionId = 1,
                    RegionName = "Region",
                    ObservedAtUtc = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc),
                },
            };
            return target;
        }

        public DadWakeTakeoverTargetSnapshot Capture(
            DadWakeTakeoverRequestDto request,
            bool forceExternalRefresh = false)
        {
            CaptureForceFlags.Add(forceExternalRefresh);
            CaptureOperationTokens.Add(request.OperationToken);
            OnCapture?.Invoke(forceExternalRefresh);
            if (UtcNow != null && Snapshot.Participant.CurrentLocation != null)
                Snapshot.Participant.CurrentLocation.ObservedAtUtc = UtcNow();
            if (ResolveCorrectCharacterFromRequest)
                Snapshot.CorrectCharacter = string.Equals(
                    Snapshot.Participant.ActiveCharacterKey.Value,
                    request.CharacterKey.Value,
                    StringComparison.OrdinalIgnoreCase);
            RefreshAuthority(request.OperationToken);
            return Snapshot;
        }

        public DadVermaxionReservationStatus ReserveVermaxion(DadWakeTakeoverRequestDto request)
        {
            ReserveCount++;
            ReserveTokens.Add(request.OperationToken);
            if (ReservationStatesOnReserve.Count > 0)
                Reservation.State = ReservationStatesOnReserve.Dequeue();
            else if (EmulatePreV0301SameTokenReleasedTrap)
            {
                Reservation.State = ReleasedReservationTokens.Contains(request.OperationToken)
                    ? DadVermaxionReservationState.Released
                    : ReservationStateOnReserve;
            }
            else if (Reservation.State == DadVermaxionReservationState.Released)
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
            ReleaseTokens.Add(operationToken);
            if (!NextResult(ReservationReleaseResults))
                return false;
            ReleasedReservationTokens.Add(operationToken);
            Reservation.State = DadVermaxionReservationState.Released;
            return true;
        }

        public DadWakeTakeoverActionResult ArmCharacterPostprocess(string operationToken)
        {
            Actions.Add("Arm");
            ArmOperationTokens.Add(operationToken);
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

        public DadLifestreamChangeWorldResult ChangeWorld(string worldName)
        {
            Actions.Add($"ChangeWorld:{worldName}");
            return new DadLifestreamChangeWorldResult(
                DadLifestreamChangeWorldOutcome.Accepted,
                $"Accepted {worldName}.");
        }

        public DadLifestreamLoginResult ConnectAndLogin(DadWakeTakeoverRequestDto request)
        {
            Actions.Add($"ConnectAndLogin:{request.CharacterKey.Value}");
            TitleLoginCaptureCounts.Add(CaptureForceFlags.Count);
            if (ThrowOnTitleLogin)
                throw new InvalidOperationException("Simulated title-login exception.");
            var outcome = TitleLoginOutcomes.Count == 0
                ? DadLifestreamLoginOutcome.Accepted
                : TitleLoginOutcomes.Dequeue();
            return new DadLifestreamLoginResult(outcome, $"Title login {outcome}.");
        }

        public DadWakeTakeoverActionResult DismissExactTitleMovie()
        {
            Actions.Add("DismissTitleMovie");
            return DismissTitleMovieAccepted
                ? DadWakeTakeoverActionResult.Accepted()
                : DadWakeTakeoverActionResult.Rejected("Movie dismissal was rejected.");
        }

        public DadWakeTakeoverActionResult ExecuteCommand(DadWakeTakeoverCommand command, DadWakeTakeoverRequestDto request)
        {
            Actions.Add(command.ToString());
            if (command == DadWakeTakeoverCommand.RelogCharacter)
            {
                Actions.Add($"RelogTarget:{request.CharacterKey.Value}");
                if (RelogDispatchFailure == 2)
                    throw new InvalidOperationException("Simulated uncertain relog dispatch.");
                if (RelogDispatchFailure == 1)
                    return DadWakeTakeoverActionResult.Rejected("Simulated rejected relog dispatch.");
            }
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
