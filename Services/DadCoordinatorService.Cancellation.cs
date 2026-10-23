using dad.Models;

namespace dad.Services;

public sealed partial class DadCoordinatorService
{
    private ManagedCancellation? managedCancellation;

    private sealed class ManagedCancellation(string runId, string reason)
    {
        public string RunId { get; } = runId;
        public string Reason { get; } = reason;
        public bool LocalWorkerAcknowledged { get; set; }
        public bool LocalRunAcknowledged { get; set; }
        public bool PartySettled { get; set; }
        public bool Failed { get; set; }
        public DateTime NextLocalAttemptUtc { get; set; }
    }

    private bool TryBeginManagedCancellation(string reason, out DadRunResult result)
    {
        result = CurrentResult.Clone();
        if (managedCancellation != null)
            return true;
        if (!RequiresManagedCancellation())
            return false;

        managedCancellation = new(activePlan!.Request.RequestId, reason);
        successfulPartyBoundarySettled = false;
        pendingDutyExitFailure = null;
        crewFormationTeardownRequested = false;
        CurrentResult.Status = DadRunStatus.Running;
        CurrentResult.CancellationState = DadRunCancellationState.Cancelling;
        CurrentResult.Phase = activePartyTeardownInstructions.Count > 0
            ? DadRunPhase.TearingDownParty : DadRunPhase.Finalizing;
        CurrentResult.Summary = "Cancelling: stopping run-owned work before exact party teardown.";
        CurrentResult.ActiveTaskStatus = CurrentResult.Summary;
        CurrentResult.BlockedReason = CurrentResult.Summary;
        QueueCoordinatorCancellations(new DadCancelCommandDto
        {
            RunId = managedCancellation.RunId,
            AuthorityWorkerSessionId = presenceService.WorkerSessionId,
            CancellationState = DadRunCancellationState.Cancelling,
            Reason = reason,
        }, reason, preserveRunContext: true);
        UpdateManagedCancellation();
        result = CurrentResult.Clone();
        return true;
    }

    private bool RequiresManagedCancellation()
        => activePlan != null && activeSlotManifest is { Slots.Count: > 0 } &&
           activePlan.RequiredParticipantCount > 1 &&
           activePlan.CompositeModuleId != DadModuleId.GatherParty &&
           activeSlotManifest.Slots.All(static slot => slot.RouteKind == DadRunSlotRouteKind.LanWorker) &&
           CurrentResult.Phase is DadRunPhase.AssemblingParty or DadRunPhase.GroupReady
               or DadRunPhase.RoutingModules or DadRunPhase.QueuePreparing or DadRunPhase.QueueStarting
               or DadRunPhase.WaitingForQueuePop or DadRunPhase.InDutyOrTask or DadRunPhase.PostRunStabilizing
               or DadRunPhase.RequeueOrComplete or DadRunPhase.TearingDownParty or DadRunPhase.Finalizing;

    private void UpdateManagedCancellation()
    {
        if (managedCancellation is not { } pending || activePlan == null || pending.Failed)
            return;

        if (!pending.LocalWorkerAcknowledged && DadClock.UtcNow >= pending.NextLocalAttemptUtc)
        {
            pending.NextLocalAttemptUtc = DadClock.UtcNow + CancellationRetryInterval;
            var acknowledgement = workerExecutionService.Cancel(new DadWorkerExecutionCancel
            {
                RunId = pending.RunId, Reason = pending.Reason,
            });
            ConsumeShoppingResults(acknowledgement.Status.ShoppingResults);
            pending.LocalWorkerAcknowledged = DadSchedulerRoutingRules.IsWorkerCancellationAcknowledged(
                pending.RunId, presenceService.WorkerSessionId, acknowledgement);
        }
        if (!pending.LocalWorkerAcknowledged || pendingCoordinatorCancellations.Values.Any(item =>
                string.Equals(item.RunCommand.RunId, pending.RunId, StringComparison.Ordinal) &&
                !item.WorkerAcknowledged))
        {
            CurrentResult.ActiveTaskStatus = "Cancelling: waiting for exact worker stop acknowledgements; run assignments remain owned.";
            CurrentResult.BlockedReason = CurrentResult.ActiveTaskStatus;
            Publish();
            return;
        }

        if (!pending.PartySettled)
        {
            AdvanceManagedCancellationTeardown();
            return;
        }

        if (!pending.LocalRunAcknowledged)
        {
            var acknowledgement = presenceService.HandleCancelRun(new DadCancelCommandDto
            {
                RunId = pending.RunId, AuthorityWorkerSessionId = presenceService.WorkerSessionId,
                CancellationState = DadRunCancellationState.Cancelling, Reason = pending.Reason,
            });
            pending.LocalRunAcknowledged = DadSchedulerRoutingRules.IsRunCancellationAcknowledged(
                pending.RunId, presenceService.WorkerSessionId, acknowledgement);
        }
        if (!pending.LocalRunAcknowledged || pendingCoordinatorCancellations.Values.Any(item =>
                string.Equals(item.RunCommand.RunId, pending.RunId, StringComparison.Ordinal)))
        {
            CurrentResult.ActiveTaskStatus = "Cancelling: exact party teardown completed; waiting for run-context release acknowledgements.";
            CurrentResult.BlockedReason = CurrentResult.ActiveTaskStatus;
            Publish();
            return;
        }

        claimService.ReleaseClaims(pending.RunId);
        managedCancellation = null;
        CurrentResult.BlockedReason = string.Empty;
        FinalizeRun(DadRunStatus.Cancelled, "Dad run cancelled; exact party teardown and client cleanup acknowledged.",
            pending.Reason, autoPartyCleanupComplete: true, localWorkerCleanupComplete: true);
    }

    private void AdvanceManagedCancellationTeardown()
    {
        if (managedCancellation is not { } pending || pending.Failed || pending.PartySettled)
            return;
        if (CurrentResult.Phase == DadRunPhase.TearingDownParty)
            UpdatePartyTeardown();
        else
            BeginPartyTeardown("Operator cancellation: settling the exact managed party.");
        pending.PartySettled = !pending.Failed && successfulPartyBoundarySettled &&
                               CurrentResult.Phase == DadRunPhase.Finalizing;
    }

    private void BlockManagedCancellation(string reason)
    {
        managedCancellation!.Failed = true;
        CurrentResult.Status = DadRunStatus.WaitingForParticipants;
        CurrentResult.CancellationState = DadRunCancellationState.Cancelling;
        CurrentResult.ActiveTaskStatus = "Cancellation cleanup blocked; original run remains owned: " + reason;
        CurrentResult.BlockedReason = CurrentResult.ActiveTaskStatus;
        CurrentResult.Summary = CurrentResult.ActiveTaskStatus;
        Publish();
    }
}
