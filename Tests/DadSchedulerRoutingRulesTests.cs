using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadSchedulerRoutingRulesTests
{
    [Fact]
    public void TakeoverCancellationCompletionRequiresExecutedCancelledOrReady()
    {
        Assert.False(DadSchedulerRoutingRules.IsTakeoverCancellationComplete(null));
        Assert.False(DadSchedulerRoutingRules.IsTakeoverCancellationComplete(new DadWakeTakeoverResultDto
        {
            Phase = DadWakeTakeoverPhase.Cancelled,
            AcknowledgementState = DadWakeAcknowledgementState.Pending,
        }));
        Assert.False(DadSchedulerRoutingRules.IsTakeoverCancellationComplete(new DadWakeTakeoverResultDto
        {
            Phase = DadWakeTakeoverPhase.Blocked,
            AcknowledgementState = DadWakeAcknowledgementState.Rejected,
        }));
        Assert.True(DadSchedulerRoutingRules.IsTakeoverCancellationComplete(new DadWakeTakeoverResultDto
        {
            Phase = DadWakeTakeoverPhase.Cancelled,
            AcknowledgementState = DadWakeAcknowledgementState.Executed,
        }));
        Assert.True(DadSchedulerRoutingRules.IsTakeoverCancellationComplete(new DadWakeTakeoverResultDto
        {
            Phase = DadWakeTakeoverPhase.Ready,
        }));
    }

    [Fact]
    public void NeverDispatchedOfflineSlotNeedsNoCancellationBarrier()
    {
        var offline = new DadSchedulerSlotState
        {
            WakePolicy = DadSchedulerWakePolicy.LaunchIfOffline,
            TakeoverPhase = DadWakeTakeoverPhase.AwaitingArHook,
        };
        Assert.False(DadSchedulerRoutingRules.RequiresTakeoverCancellation(offline));

        offline.OperationToken = "scheduler-run";
        Assert.True(DadSchedulerRoutingRules.RequiresTakeoverCancellation(offline));

        offline.TakeoverPhase = DadWakeTakeoverPhase.Cancelled;
        Assert.True(DadSchedulerRoutingRules.RequiresTakeoverCancellation(offline));

        offline.AcknowledgementState = DadWakeAcknowledgementState.Executed;
        Assert.False(DadSchedulerRoutingRules.RequiresTakeoverCancellation(offline));

        offline.TakeoverPhase = DadWakeTakeoverPhase.Blocked;
        offline.AcknowledgementState = DadWakeAcknowledgementState.Rejected;
        Assert.True(DadSchedulerRoutingRules.RequiresTakeoverCancellation(offline));
    }

    private const string AccountW = "dad-client-01c4df9f09b5488abc6980d9c09f103e";
    private const string AccountX = "dad-client-42a9d8e48b3a411689c692ada8e3676f";

    [Fact]
    public void ConfiguredAccountIdentityExistsBeforeLoginAndDoesNotFollowCharacterLifecycle()
    {
        var beforeLogin = DadSchedulerRoutingRules.ResolveStableClientAccount($"  {AccountX}  ");
        var duringLogin = DadSchedulerRoutingRules.ResolveStableClientAccount(AccountX);
        var afterLogout = DadSchedulerRoutingRules.ResolveStableClientAccount(AccountX);
        var duringRelog = DadSchedulerRoutingRules.ResolveStableClientAccount(AccountX);

        Assert.Equal(AccountX, beforeLogin.Value);
        Assert.Equal(beforeLogin, duringLogin);
        Assert.Equal(beforeLogin, afterLogout);
        Assert.Equal(beforeLogin, duringRelog);
    }

    [Fact]
    public void ConnectedCharacterSelectClientIsRoutableWithoutAvailabilityOrCharacterSnapshot()
    {
        var x = Participant("worker-x", AccountX, isAvailable: false, activeCharacter: string.Empty);

        var resolved = DadSchedulerRoutingRules.ResolveExactConnectedClient(
            new DadAccountKey(AccountX),
            [x],
            worker => worker.Value == "worker-x");

        Assert.Same(x, resolved);
        Assert.False(resolved!.IsAvailable);
        Assert.True(resolved.ActiveCharacterKey.IsEmpty);
    }

    [Fact]
    public void StaleAndPhysicallyDisconnectedClientsAreRejected()
    {
        var stale = Participant("worker-stale", AccountX, isAvailable: true, activeCharacter: "Hard'carry Gray'parse@Excalibur");
        stale.State = DadParticipantState.Stale;
        var disconnected = Participant("worker-disconnected", AccountX, isAvailable: true, activeCharacter: "Hard'carry Gray'parse@Excalibur");

        Assert.Null(DadSchedulerRoutingRules.ResolveExactConnectedClient(
            new DadAccountKey(AccountX),
            [stale],
            static _ => true));
        Assert.Null(DadSchedulerRoutingRules.ResolveExactConnectedClient(
            new DadAccountKey(AccountX),
            [disconnected],
            static _ => false));
    }

    [Fact]
    public void ExactStableAccountRoutesSlot2ToXWithoutAliasesOrActiveCharacterEvidence()
    {
        var aliasProjection = Participant("worker-alias", "cached-account", isAvailable: true, activeCharacter: "Hard'carry Gray'parse@Excalibur");
        aliasProjection.ManagedAccountAlias = AccountX;
        aliasProjection.Character.AccountId = AccountX;
        var x = Participant("worker-x", AccountX, isAvailable: false, string.Empty);

        var resolved = DadSchedulerRoutingRules.ResolveExactConnectedClient(
            new DadAccountKey(AccountX),
            [aliasProjection, x],
            static _ => true);

        Assert.NotNull(resolved);
        Assert.Equal("worker-x", resolved!.WorkerSessionId.Value);
    }

    [Fact]
    public void WCanScheduleResetAndRelogWhileXIsMissing()
    {
        var slots = Slots();
        var now = new DateTime(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);
        slots[0].ClientConnected = true;
        slots[0].BasePostArReady = true;
        slots[0].AutoRetainerAvailable = true;
        slots[0].TakeoverPhase = DadWakeTakeoverPhase.Prepared;
        slots[1].ClientConnected = false;

        var wReset = DadSchedulerRoutingRules.ResolveNextTakeoverAction(slots[0], now);
        var xMissing = DadSchedulerRoutingRules.ResolveNextTakeoverAction(slots[1], now);

        Assert.True(wReset.CanDispatch);
        Assert.Equal(DadWakeCommitKind.Reset, wReset.CommitKind);
        Assert.Equal(now.AddSeconds(5), wReset.ExecutionTimeUtc);
        Assert.False(xMissing.CanDispatch);

        slots[0].ResetExecutionUtc = wReset.ExecutionTimeUtc;
        slots[0].TakeoverPhase = DadWakeTakeoverPhase.ResetVerified;
        var wRelog = DadSchedulerRoutingRules.ResolveNextTakeoverAction(slots[0], now.AddSeconds(10));

        Assert.True(wRelog.CanDispatch);
        Assert.Equal(DadWakeCommitKind.Relog, wRelog.CommitKind);
        Assert.Equal(now.AddSeconds(15), wRelog.ExecutionTimeUtc);
    }

    [Fact]
    public void XCanCatchUpWithItsOwnExecutionBoundariesAfterConnecting()
    {
        var slots = Slots();
        var now = new DateTime(2026, 7, 12, 12, 0, 30, DateTimeKind.Utc);
        slots[0].ClientConnected = true;
        slots[0].TakeoverPhase = DadWakeTakeoverPhase.WaitingForCharacter;
        slots[0].ResetExecutionUtc = now.AddSeconds(-20);
        slots[0].RelogExecutionUtc = now.AddSeconds(-10);

        slots[1].ClientConnected = true;
        slots[1].BasePostArReady = true;
        slots[1].AutoRetainerAvailable = true;
        slots[1].TakeoverPhase = DadWakeTakeoverPhase.AwaitingArHook;
        var xPrepare = DadSchedulerRoutingRules.ResolveNextTakeoverAction(slots[1], now);
        Assert.Equal(DadWakeTakeoverMessageKind.Prepare, xPrepare.MessageKind);
        Assert.Equal(DadWakeCommitKind.None, xPrepare.CommitKind);

        slots[1].TakeoverPhase = DadWakeTakeoverPhase.Prepared;
        var xReset = DadSchedulerRoutingRules.ResolveNextTakeoverAction(slots[1], now);
        slots[1].ResetExecutionUtc = xReset.ExecutionTimeUtc;

        Assert.Equal(now.AddSeconds(5), slots[1].ResetExecutionUtc);
        Assert.NotEqual(slots[0].ResetExecutionUtc, slots[1].ResetExecutionUtc);
        Assert.NotEqual(slots[0].RelogExecutionUtc, slots[1].ResetExecutionUtc);
    }

    [Fact]
    public void FrozenWorkerSessionCannotBeSubstitutedByAnotherSessionOnTheSameAccount()
    {
        var frozen = Participant("worker-x", AccountX, false, string.Empty);
        var substitute = Participant("worker-x-new", AccountX, true, "Hard'carry Gray'parse@Excalibur");

        Assert.Null(DadSchedulerRoutingRules.ResolveFrozenConnectedClient(
            new DadAccountKey(AccountX),
            new DadWorkerSessionId("worker-x"),
            [substitute],
            static _ => true));

        var reconnected = DadSchedulerRoutingRules.ResolveFrozenConnectedClient(
            new DadAccountKey(AccountX),
            new DadWorkerSessionId("worker-x"),
            [frozen, substitute],
            worker => worker.Value == "worker-x");

        Assert.Same(frozen, reconnected);
        Assert.False(reconnected!.IsAvailable);
    }

    [Fact]
    public void DuplicateLiveRoutesForOneStableAccountRemainUnresolved()
    {
        var first = Participant("worker-x-a", AccountX, true, "Hard'carry Gray'parse@Excalibur");
        var second = Participant("worker-x-b", AccountX, true, "Other Character@Excalibur");

        var resolved = DadSchedulerRoutingRules.ResolveExactConnectedClient(
            new DadAccountKey(AccountX),
            [second, first],
            static _ => true);

        Assert.Null(resolved);
    }

    [Fact]
    public void DisconnectedSessionSafelyRebindsOnlyOneExactStableAccountReplacement()
    {
        var replacement = Participant(
            "worker-x-new",
            AccountX,
            isAvailable: false,
            activeCharacter: string.Empty);

        var rebound = DadSchedulerRoutingRules.ResolveCurrentOrSoleReconnectedClient(
            new DadAccountKey(AccountX),
            new DadWorkerSessionId("worker-x-old"),
            [replacement],
            static _ => true);

        Assert.Same(replacement, rebound);

        var duplicate = Participant("worker-x-other", AccountX, true, "Other@Excalibur");
        Assert.Null(DadSchedulerRoutingRules.ResolveCurrentOrSoleReconnectedClient(
            new DadAccountKey(AccountX),
            new DadWorkerSessionId("worker-x-old"),
            [replacement, duplicate],
            static _ => true));

        var contradictoryOldSession = Participant("worker-x-old", "different-account", true, "Wrong@Excalibur");
        Assert.Null(DadSchedulerRoutingRules.ResolveCurrentOrSoleReconnectedClient(
            new DadAccountKey(AccountX),
            new DadWorkerSessionId("worker-x-old"),
            [replacement, contradictoryOldSession],
            static _ => true));
    }

    [Fact]
    public void FrozenWorkerSessionSurvivesDisconnectedRebuildAndRejectsSubstituteUntilReconnect()
    {
        var frozenId = new DadWorkerSessionId("worker-x");
        var substitute = Participant("worker-x-new", AccountX, true, "Hard'carry Gray'parse@Excalibur");

        var afterDisconnectedRebuild = DadSchedulerRoutingRules.PreserveFrozenWorkerSession(
            frozenId,
            new DadWorkerSessionId(string.Empty));
        Assert.Equal(frozenId, afterDisconnectedRebuild);
        Assert.Null(DadSchedulerRoutingRules.ResolveFrozenConnectedClient(
            new DadAccountKey(AccountX),
            afterDisconnectedRebuild,
            [substitute],
            static _ => true));

        var reconnected = Participant("worker-x", AccountX, false, string.Empty);
        Assert.Same(reconnected, DadSchedulerRoutingRules.ResolveFrozenConnectedClient(
            new DadAccountKey(AccountX),
            afterDisconnectedRebuild,
            [substitute, reconnected],
            worker => worker.Value == "worker-x"));
    }

    [Fact]
    public void CancellationRoutesToExactFrozenWorkerDespiteAccountEvidenceDrift()
    {
        var drifted = Participant(
            "worker-x",
            "temporarily-unresolved-account",
            isAvailable: false,
            activeCharacter: string.Empty);
        var substitute = Participant(
            "replacement-worker",
            AccountX,
            isAvailable: true,
            activeCharacter: "Hard'carry Gray'parse@Excalibur");

        Assert.Null(DadSchedulerRoutingRules.ResolveFrozenConnectedClient(
            new DadAccountKey(AccountX),
            new DadWorkerSessionId("worker-x"),
            [drifted, substitute],
            static _ => true));

        var cleanupRoute = DadSchedulerRoutingRules.ResolveFrozenCancellationClient(
            new DadWorkerSessionId("worker-x"),
            [substitute, drifted],
            worker => worker.Value == "worker-x");

        Assert.Same(drifted, cleanupRoute);
        Assert.Equal("worker-x", cleanupRoute!.WorkerSessionId.Value);
    }

    [Fact]
    public void CancellationNeverRetargetsAndWaitsForFrozenWorkerToBeLive()
    {
        var stale = Participant("worker-x", AccountX, true, "Hard'carry Gray'parse@Excalibur");
        stale.State = DadParticipantState.Stale;
        var substitute = Participant("replacement-worker", AccountX, true, "Hard'carry Gray'parse@Excalibur");

        Assert.Null(DadSchedulerRoutingRules.ResolveFrozenCancellationClient(
            new DadWorkerSessionId("worker-x"),
            [substitute],
            static _ => true));
        Assert.Null(DadSchedulerRoutingRules.ResolveFrozenCancellationClient(
            new DadWorkerSessionId("worker-x"),
            [stale, substitute],
            static _ => true));

        var online = Participant("worker-x", "drifted-account", false, string.Empty);
        Assert.Null(DadSchedulerRoutingRules.ResolveFrozenCancellationClient(
            new DadWorkerSessionId("worker-x"),
            [online, substitute],
            static _ => false));
    }

    [Fact]
    public void UnsafeLatestHeartbeatPollsStatusWithoutSendingGo()
    {
        var slot = SafeTakeoverSlot(DadWakeTakeoverPhase.Prepared);
        slot.BasePostArReady = false;

        var reset = DadSchedulerRoutingRules.ResolveNextTakeoverAction(slot, DateTime.UtcNow);
        Assert.True(reset.CanDispatch);
        Assert.Equal(DadWakeTakeoverMessageKind.Status, reset.MessageKind);
        Assert.Equal(DadWakeCommitKind.None, reset.CommitKind);

        slot = SafeTakeoverSlot(DadWakeTakeoverPhase.ResetVerified);
        slot.AutoRetainerBusy = true;
        var relog = DadSchedulerRoutingRules.ResolveNextTakeoverAction(slot, DateTime.UtcNow);
        Assert.True(relog.CanDispatch);
        Assert.Equal(DadWakeTakeoverMessageKind.Status, relog.MessageKind);
        Assert.Equal(DadWakeCommitKind.None, relog.CommitKind);
    }

    [Fact]
    public void ReadyAcknowledgementUsesExactWorldTruthNotSuppressionSensitivePostAr()
    {
        var slot = new DadSchedulerSlotState
        {
            RequiredAccountKey = new DadAccountKey(AccountX),
            RequiredCharacterKey = new DadCharacterKey("Hard'carry Gray'parse@Excalibur"),
            MatchedWorkerSessionId = new DadWorkerSessionId("worker-x"),
        };
        var snapshot = Participant(
            "worker-x",
            AccountX,
            isAvailable: true,
            activeCharacter: "Hard'carry Gray'parse@Excalibur");
        snapshot.WorldReadyStable = true;
        snapshot.PostArReady = false;
        var result = new DadWakeTakeoverResultDto
        {
            Status = DadWakeTakeoverStatus.Ready,
            Phase = DadWakeTakeoverPhase.Ready,
            PostArReady = false,
            AutoRetainerAvailable = true,
            Snapshot = snapshot,
        };

        Assert.True(DadSchedulerRoutingRules.CanAcceptReadyAcknowledgement(slot, result));

        result.Snapshot.WorkerSessionId = new DadWorkerSessionId("replacement-worker");
        Assert.False(DadSchedulerRoutingRules.CanAcceptReadyAcknowledgement(slot, result));
    }

    [Fact]
    public void ConnectedCorrectCharacterCanAdvanceToReadyInsteadOfWaitingForClient()
    {
        var decision = DadWakePolicyRules.Evaluate(
            DadSchedulerWakePolicy.LaunchIfOffline,
            sameAccountClientConnected: true,
            correctCharacter: true,
            postArReady: true,
            takeoverStatus: DadWakeTakeoverStatus.Ready);

        Assert.True(decision.Ready);
        Assert.Equal(DadWakeTakeoverStage.Ready, decision.Stage);
        Assert.DoesNotContain("Waiting", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SchedulerSlotClonePreservesSeparateClientAndCharacterStates()
    {
        var slot = new DadSchedulerSlotState
        {
            ClientConnected = true,
            IsOnline = false,
            MatchedWorkerSessionId = new DadWorkerSessionId("worker-x"),
            ResetExecutionUtc = new DateTime(2026, 7, 12, 12, 0, 5, DateTimeKind.Utc),
            RelogExecutionUtc = new DateTime(2026, 7, 12, 12, 0, 15, DateTimeKind.Utc),
        };

        var clone = slot.Clone();

        Assert.True(clone.ClientConnected);
        Assert.False(clone.IsOnline);
        Assert.Equal("worker-x", clone.MatchedWorkerSessionId.Value);
        Assert.Equal(slot.ResetExecutionUtc, clone.ResetExecutionUtc);
        Assert.Equal(slot.RelogExecutionUtc, clone.RelogExecutionUtc);
    }

    private static List<DadSchedulerSlotState> Slots()
        =>
        [
            new DadSchedulerSlotState
            {
                SlotId = "Slot1",
                RequiredAccountKey = new DadAccountKey(AccountW),
                RequiredCharacterKey = new DadCharacterKey("Venat Azem@Excalibur"),
            },
            new DadSchedulerSlotState
            {
                SlotId = "Slot2",
                RequiredAccountKey = new DadAccountKey(AccountX),
                RequiredCharacterKey = new DadCharacterKey("Hard'carry Gray'parse@Excalibur"),
            },
        ];

    private static DadSchedulerSlotState SafeTakeoverSlot(DadWakeTakeoverPhase phase)
        => new()
        {
            ClientConnected = true,
            BasePostArReady = true,
            AutoRetainerAvailable = true,
            TakeoverPhase = phase,
        };

    private static DadParticipantSnapshot Participant(
        string worker,
        string stableAccount,
        bool isAvailable,
        string activeCharacter)
        => new()
        {
            WorkerSessionId = new DadWorkerSessionId(worker),
            ManagedAccountKey = new DadAccountKey(stableAccount),
            IsAvailable = isAvailable,
            IsEligibleForRun = true,
            State = DadParticipantState.Idle,
            ActiveCharacterKey = new DadCharacterKey(activeCharacter),
            Character = new DadAcquiredCharacter
            {
                AccountId = stableAccount,
                CharacterKey = activeCharacter,
            },
        };
}
