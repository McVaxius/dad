using dad.Models;

namespace dad.Services;

public interface IDadWakeTakeoverTarget
{
    DadWakeTakeoverTargetSnapshot Capture(DadWakeTakeoverRequestDto request, bool forceExternalRefresh = false);
    DadVermaxionReservationStatus ReserveVermaxion(DadWakeTakeoverRequestDto request);
    bool ReleaseVermaxion(string operationToken);
    DadWakeTakeoverActionResult ArmCharacterPostprocess(string operationToken);
    DadWakeTakeoverActionResult AcquireSuppression();
    bool FinishCharacterPostprocess(bool retryAtNextBoundary);
    bool ReleaseSuppressionIfOwned(bool force = false);
    DadWakeTakeoverActionResult SetMultiModeEnabled(bool enabled);
    DadLifestreamChangeWorldResult ChangeWorld(string worldName);
    DadWakeTakeoverActionResult ExecuteCommand(DadWakeTakeoverCommand command, DadWakeTakeoverRequestDto request);
}

public sealed class DadWakeTakeoverService : IDisposable
{
    private static readonly TimeSpan OperationRetention = TimeSpan.FromHours(1);
    private readonly IDadWakeTakeoverTarget target;
    private readonly Func<DateTime> utcNow;
    private readonly Action<string>? diagnostic;
    private readonly object gate = new();
    private readonly Dictionary<string, OperationState> operations = new(StringComparer.OrdinalIgnoreCase);
    private string activeOperationKey = string.Empty;
    private bool disposed;

    public DadWakeTakeoverService(
        IDadWakeTakeoverTarget target,
        Func<DateTime>? utcNow = null,
        TimeSpan? preCommitBudget = null,
        Action<string>? diagnostic = null)
    {
        this.target = target;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.diagnostic = diagnostic;
        _ = preCommitBudget; // Retained for constructor compatibility; readiness waits are intentionally unbounded.
    }

    public DadWakeTakeoverResultDto Handle(DadWakeTakeoverRequestDto request)
    {
        request ??= new DadWakeTakeoverRequestDto();
        lock (gate)
        {
            if (disposed)
                return Blocked(request, null, "DAD wake takeover service is disposed.");
            NormalizeRequest(request);
            var validation = ValidateRequest(request);
            if (!string.IsNullOrWhiteSpace(validation))
                return Blocked(request, null, validation);

            PruneOperations();
            var key = DadWakePolicyRules.BuildOperationKey(request);
            if (!operations.TryGetValue(key, out var operation))
            {
                if (!string.IsNullOrWhiteSpace(activeOperationKey) &&
                    operations.TryGetValue(activeOperationKey, out var active) &&
                    (!IsTerminal(active.Phase) || active.CleanupPending))
                {
                    return Blocked(request, null, $"Stale or conflicting takeover token; active operation is {active.Request.OperationToken}.");
                }

                operation = new OperationState(CloneRequest(request), utcNow());
                operations[key] = operation;
                activeOperationKey = key;
            }

            if (!SameOperation(operation.Request, request))
                return Blocked(request, null, "Takeover token was reused with different target data.");

            if (request.MessageKind == DadWakeTakeoverMessageKind.Cancel)
                return Cancel(operation, "Takeover cancelled by coordinator.");

            operation.CoordinatorAvailable = true;
            AdvanceDueOperation(operation);
            return request.MessageKind switch
            {
                DadWakeTakeoverMessageKind.Go => Commit(operation, request),
                DadWakeTakeoverMessageKind.Prepare => Prepare(operation),
                _ => BuildResult(operation),
            };
        }
    }

    public void OnCharacterPostprocessReady()
    {
        lock (gate)
        {
            if (disposed || string.IsNullOrWhiteSpace(activeOperationKey) ||
                !operations.TryGetValue(activeOperationKey, out var operation) ||
                operation.Phase != DadWakeTakeoverPhase.AwaitingArHook)
            {
                target.FinishCharacterPostprocess(retryAtNextBoundary: false);
                return;
            }

            operation.CharacterPostprocessArmed = true;
            var snapshot = Capture(operation, forceExternalRefresh: true);
            var blocker = ValidateCoreTarget(snapshot, operation.Request);
            if (!string.IsNullOrWhiteSpace(blocker))
            {
                Block(operation, blocker, cleanup: true);
                return;
            }

            if (!operation.CoordinatorAvailable || !IsWorldMutationSafe(snapshot))
            {
                ReturnToReadinessWait(operation, snapshot, BuildReadinessWaitSummary(snapshot), releaseReservation: true);
                return;
            }

            // VERMAXION marks itself busy at request time. This check also covers unreadable v1 status.
            if (snapshot.ExternalAutomationHeld || !snapshot.SuppressionReadable ||
                snapshot.AutoRetainerSuppressed && !snapshot.DadOwnsSuppression ||
                !snapshot.DadOwnsCharacterPostprocess)
            {
                ReturnToReadinessWait(operation, snapshot, BuildLeaseYieldSummary(snapshot), releaseReservation: true);
                return;
            }

            // VERMAXION and participant state can change during the callback. Re-read at the exact
            // suppression boundary so no cached-idle window can authorize an unsafe mutation.
            snapshot = Capture(operation, forceExternalRefresh: true);
            blocker = ValidateCoreTarget(snapshot, operation.Request);
            if (!string.IsNullOrWhiteSpace(blocker))
            {
                Block(operation, blocker, cleanup: true);
                return;
            }
            if (!operation.CoordinatorAvailable || !IsWorldMutationSafe(snapshot) ||
                !snapshot.SuppressionReadable ||
                snapshot.AutoRetainerSuppressed && !snapshot.DadOwnsSuppression ||
                !snapshot.DadOwnsCharacterPostprocess)
            {
                ReturnToReadinessWait(operation, snapshot, BuildReadinessWaitSummary(snapshot), releaseReservation: true);
                return;
            }

            // Treat the write as conservatively owned until cleanup proves otherwise. The IPC
            // service only releases when its own local ownership marker is set, so this is safe
            // even if acquisition was rejected before writing.
            operation.SuppressionAcquired = true;
            var acquire = Invoke(target.AcquireSuppression, "acquire DAD AutoRetainer suppression");
            operation.SuppressionAcquired |= acquire.Success;
            snapshot = Capture(operation, forceExternalRefresh: true);
            operation.SuppressionAcquired |= snapshot.DadOwnsSuppression;
            if (!operation.CoordinatorAvailable || !IsWorldMutationSafe(snapshot))
            {
                ReturnToReadinessWait(operation, snapshot, BuildReadinessWaitSummary(snapshot), releaseReservation: true);
                return;
            }
            if (!acquire.Success || !snapshot.DadOwnsSuppression || !snapshot.AutoRetainerSuppressed)
            {
                var summary = acquire.Success
                    ? "DAD suppression ownership could not be verified; retrying at a future character boundary."
                    : $"{acquire.Error} Retrying at a future character boundary.";
                ReturnToReadinessWait(operation, snapshot, summary, releaseReservation: true);
                return;
            }

            operation.Phase = DadWakeTakeoverPhase.PostprocessOwned;
            operation.Summary = "AR handoff acquired — validating local preparation with no timeout; cancel to stop.";
            operation.UpdatedAtUtc = utcNow();
        }
    }

    public void OnVermaxionReservationGranted(DadVermaxionReservationStatus grant)
    {
        lock (gate)
        {
            if (disposed || !grant.IsGranted)
                return;
            var operation = operations.Values.FirstOrDefault(candidate =>
                !IsTerminal(candidate.Phase) &&
                string.Equals(candidate.Request.OperationToken, grant.OperationToken, StringComparison.OrdinalIgnoreCase));
            if (operation == null || !operation.CoordinatorAvailable || operation.Phase > DadWakeTakeoverPhase.Prepared)
                return;

            var snapshot = Capture(operation, forceExternalRefresh: true);
            if (operation.Phase == DadWakeTakeoverPhase.Prepared &&
                (!operation.CoordinatorAvailable || !IsWorldReadyStable(snapshot) ||
                 !snapshot.AutoRetainerAvailable || snapshot.AutoRetainerBusy || snapshot.ExternalAutomationHeld))
            {
                ReturnToReadinessWait(operation, snapshot, BuildReadinessWaitSummary(snapshot), releaseReservation: true);
                return;
            }
            if (operation.Phase == DadWakeTakeoverPhase.Prepared &&
                snapshot.VermaxionMutationAuthorization == DadVermaxionMutationAuthorization.Granted &&
                IsVerifiedReservationPreparation(snapshot))
            {
                operation.VermaxionMutationAuthorization = DadVermaxionMutationAuthorization.Granted;
                operation.Summary = BuildPreparedSummary(DadVermaxionMutationAuthorization.Granted);
                operation.UpdatedAtUtc = utcNow();
                return;
            }

            Prepare(operation);
        }
    }

    public void Update()
    {
        lock (gate)
        {
            foreach (var operation in operations.Values)
            {
                if (operation.CleanupPending)
                {
                    operation.CleanupPending = !TryCleanupOwnedLeases(
                        operation,
                        retryAtNextBoundary: false,
                        releaseReservation: operation.CleanupReleaseReservation);
                    if (!operation.CleanupPending && operation.Phase == DadWakeTakeoverPhase.Cancelled)
                        FinalizeCancellation(operation);
                    else if (!operation.CleanupPending && operation.Phase == DadWakeTakeoverPhase.Blocked)
                        ClearActiveOperationIfMatches(operation);
                }
                AdvanceDueOperation(operation);
            }
        }
    }

    public void CancelOperation(string operationToken, string reason)
    {
        lock (gate)
        {
            var operation = operations.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.Request.OperationToken, operationToken, StringComparison.OrdinalIgnoreCase));
            if (operation != null && operation.Phase != DadWakeTakeoverPhase.Ready)
                Cancel(operation, reason);
        }
    }

    public DadWakeTakeoverResultDto? GetActiveStatus()
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(activeOperationKey) ||
                !operations.TryGetValue(activeOperationKey, out var operation))
                return null;
            return BuildResult(operation).Clone();
        }
    }

    public void OnCoordinatorDisconnected()
    {
        lock (gate)
        {
            if (disposed || string.IsNullOrWhiteSpace(activeOperationKey) ||
                !operations.TryGetValue(activeOperationKey, out var operation) || IsTerminal(operation.Phase))
                return;

            operation.CoordinatorAvailable = false;

            if (operation.Phase >= DadWakeTakeoverPhase.ResetCommitted)
            {
                operation.Summary = "Coordinator disconnected after GO; the committed takeover command is preserved and continues once its local safety gates are clear.";
                operation.UpdatedAtUtc = utcNow();
                return;
            }

            if (operation.Phase == DadWakeTakeoverPhase.AwaitingArHook &&
                !operation.LastSnapshot.DadOwnsCharacterPostprocess &&
                !operation.LastSnapshot.DadOwnsSuppression &&
                !operation.CharacterPostprocessArmed &&
                !operation.ReservationRequested)
                return;

            operation.CleanupReleaseReservation = true;
            operation.CleanupPending = !TryCleanupOwnedLeases(
                operation,
                retryAtNextBoundary: false,
                releaseReservation: true);
            operation.Phase = DadWakeTakeoverPhase.AwaitingArHook;
            operation.VermaxionMutationAuthorization = DadVermaxionMutationAuthorization.None;
            operation.LastSnapshot.DadOwnsCharacterPostprocess = false;
            operation.LastSnapshot.DadOwnsSuppression = false;
            operation.LastSnapshot.AutoRetainerSuppressed = false;
            operation.Summary = "Coordinator disconnected; temporary DAD leases and the VERMAXION reservation were released while the logical order remains active.";
            operation.UpdatedAtUtc = utcNow();
        }
    }

    public DadWakeTakeoverStopAllResult StopAll(string reason)
    {
        lock (gate)
        {
            var result = new DadWakeTakeoverStopAllResult();
            foreach (var operation in operations.Values.Where(static operation =>
                         !IsTerminal(operation.Phase) || operation.CleanupPending))
            {
                Cancel(operation, string.IsNullOrWhiteSpace(reason) ? "Stopped by DAD Stop-all." : reason);
                result.CancelledCount++;
            }

            result.CleanupPending = operations.Values.Any(static operation => operation.CleanupPending);
            result.Summary = $"Cancelled {result.CancelledCount} takeover(s).";
            return result;
        }
    }

    private DadWakeTakeoverResultDto Prepare(OperationState operation)
    {
        if (IsTerminal(operation.Phase) || operation.Phase >= DadWakeTakeoverPhase.Prepared)
            return BuildResult(operation);
        operation.PreparationStarted = true;
        if (operation.NextEpochEligibleUtc.HasValue && utcNow() < operation.NextEpochEligibleUtc.Value)
            return BuildResult(operation, summary: $"Takeover epoch {operation.Epoch} is waiting for the five-second retry cadence.");
        if (operation.CleanupPending)
            return BuildResult(operation, summary: "Waiting for temporary DAD takeover state to finish releasing before preparation resumes.");

        var snapshot = Capture(operation, forceExternalRefresh: true);
        var blocker = ValidateCoreTarget(snapshot, operation.Request);
        if (!string.IsNullOrWhiteSpace(blocker))
            return Block(operation, blocker, cleanup: true);
        if (!operation.CoordinatorAvailable || !IsWorldAndLocalServicesSafe(snapshot))
        {
            return ReturnToReadinessWait(
                operation,
                snapshot,
                BuildReadinessWaitSummary(snapshot),
                releaseReservation: operation.ReservationRequested);
        }
        if (IsExternalAutomationBlocking(snapshot))
        {
            // An exact reservation owned by this operation may remain Pending/Granting while
            // VERMAXION drains to the handoff boundary. Any other hold is not a reason to keep a
            // prior DAD reservation or callback armed through an unsafe readiness wait.
            return ReturnToReadinessWait(
                operation,
                snapshot,
                BuildReadinessWaitSummary(snapshot),
                releaseReservation: operation.ReservationRequested && !IsOwnedReservationWait(snapshot));
        }

        DadVermaxionReservationStatus? reservation = null;
        if (!operation.ReservationRequested)
        {
            operation.ReservationRequested = true;
            reservation = target.ReserveVermaxion(operation.Request);
        }
        snapshot = Capture(operation, forceExternalRefresh: true);
        if (reservation?.IsRejected == true)
            return BeginNextEpoch(operation, reservation.Summary);
        if (!operation.CoordinatorAvailable || !IsWorldAndLocalServicesSafe(snapshot))
            return ReturnToReadinessWait(operation, snapshot, BuildReadinessWaitSummary(snapshot), releaseReservation: true);
        if (snapshot.VermaxionMutationAuthorization != DadVermaxionMutationAuthorization.None)
            return PrepareFromVermaxionAuthorization(operation, snapshot);

        if (snapshot.VermaxionReservationAuthoritative)
        {
            operation.Summary = BuildReservationHoldSummary(
                snapshot.VermaxionReservationState,
                snapshot.ExternalAutomationActivity,
                snapshot.ExternalAutomationState,
                snapshot.VermaxionReservationSummary);
            operation.UpdatedAtUtc = utcNow();
            return BuildResult(operation, snapshot);
        }

        if (snapshot.ExternalAutomationHeld)
        {
            return ReturnToReadinessWait(
                operation,
                snapshot,
                $"{BuildExternalHoldSummary(snapshot)} No timeout; cancel to stop.",
                releaseReservation: true);
        }

        if (operation.Phase == DadWakeTakeoverPhase.AwaitingArHook)
        {
            snapshot = Capture(operation, forceExternalRefresh: true);
            blocker = ValidateCoreTarget(snapshot, operation.Request);
            if (!string.IsNullOrWhiteSpace(blocker))
                return Block(operation, blocker, cleanup: true);
            if (!operation.CoordinatorAvailable || !IsPreReservationReady(snapshot))
                return ReturnToReadinessWait(operation, snapshot, BuildReadinessWaitSummary(snapshot), releaseReservation: true);

            if (operation.CharacterPostprocessArmed)
                return BuildResult(operation, snapshot, "Waiting for AutoRetainer character postprocess; no timeout; cancel to stop.");
            var armed = Invoke(
                () => target.ArmCharacterPostprocess(operation.Request.OperationToken),
                "arm the AutoRetainer character postprocess request");
            if (!armed.Success)
                return BeginNextEpoch(operation, armed.Error);
            operation.CharacterPostprocessArmed = true;
            operation.Summary = "Waiting for AutoRetainer character postprocess; no timeout; cancel to stop.";
            operation.UpdatedAtUtc = utcNow();
            return BuildResult(operation, snapshot);
        }

        snapshot = Capture(operation, forceExternalRefresh: true);
        if (!operation.CoordinatorAvailable || !IsWorldAndLocalServicesSafe(snapshot) ||
            snapshot.ExternalAutomationHeld || !snapshot.SuppressionReadable ||
            !snapshot.DadOwnsCharacterPostprocess || !snapshot.DadOwnsSuppression ||
            !snapshot.AutoRetainerSuppressed)
        {
            var summary = IsWorldReadyStable(snapshot) && snapshot.AutoRetainerAvailable && !snapshot.AutoRetainerBusy
                ? BuildLeaseYieldSummary(snapshot)
                : BuildReadinessWaitSummary(snapshot);
            return ReturnToReadinessWait(operation, snapshot, summary, releaseReservation: true);
        }

        operation.Phase = DadWakeTakeoverPhase.Prepared;
        operation.Summary = "AR handoff acquired — waiting for crew with no timeout; cancel to stop.";
        operation.Acknowledgement = DadWakeAcknowledgementState.Accepted;
        operation.UpdatedAtUtc = utcNow();
        return BuildResult(operation, snapshot);
    }

    private DadWakeTakeoverResultDto PrepareFromVermaxionAuthorization(
        OperationState operation,
        DadWakeTakeoverTargetSnapshot snapshot)
    {
        var authorization = snapshot.VermaxionMutationAuthorization;
        if (authorization == DadVermaxionMutationAuthorization.None)
            return BuildResult(operation, snapshot);

        if (!operation.CoordinatorAvailable || !IsWorldMutationSafe(snapshot))
        {
            return ReturnToReadinessWait(operation, snapshot, BuildReadinessWaitSummary(snapshot), releaseReservation: true);
        }
        if (snapshot.MultiModeEnabled)
        {
            operation.Summary = authorization == DadVermaxionMutationAuthorization.Granted
                ? "VERMAXION grant received; waiting for verified AutoRetainer off/idle state."
                : BuildReservationHoldSummary(
                    snapshot.VermaxionReservationState,
                    snapshot.ExternalAutomationActivity,
                    snapshot.ExternalAutomationState,
                    snapshot.VermaxionReservationSummary);
            return BuildResult(operation, snapshot);
        }
        if (!snapshot.SuppressionReadable || snapshot.AutoRetainerSuppressed && !snapshot.DadOwnsSuppression)
        {
            operation.Summary = BuildLeaseYieldSummary(snapshot);
            return BuildResult(operation, snapshot);
        }

        // Re-read the grant/compatibility evidence at the exact suppression boundary. A hold that
        // starts inside the status cache window must win before DAD mutates AutoRetainer state.
        snapshot = Capture(operation, forceExternalRefresh: true);
        var blocker = ValidateCoreTarget(snapshot, operation.Request);
        if (!string.IsNullOrWhiteSpace(blocker))
            return Block(operation, blocker, cleanup: true);
        if (!operation.CoordinatorAvailable || !IsWorldMutationSafe(snapshot) ||
            snapshot.MultiModeEnabled || !snapshot.SuppressionReadable ||
            snapshot.AutoRetainerSuppressed && !snapshot.DadOwnsSuppression ||
            snapshot.VermaxionMutationAuthorization == DadVermaxionMutationAuthorization.None)
        {
            return ReturnToReadinessWait(operation, snapshot, BuildReadinessWaitSummary(snapshot), releaseReservation: true);
        }

        operation.SuppressionAcquired = true;
        var acquire = Invoke(
            target.AcquireSuppression,
            authorization == DadVermaxionMutationAuthorization.Granted
                ? "acquire DAD AutoRetainer suppression after VERMAXION grant"
                : "acquire DAD AutoRetainer suppression from verified-idle compatibility evidence");
        operation.SuppressionAcquired |= acquire.Success;
        snapshot = Capture(operation, forceExternalRefresh: true);
        operation.SuppressionAcquired |= snapshot.DadOwnsSuppression;
        if (!operation.CoordinatorAvailable || !IsWorldMutationSafe(snapshot))
        {
            return ReturnToReadinessWait(operation, snapshot, BuildReadinessWaitSummary(snapshot), releaseReservation: true);
        }
        if (!acquire.Success || !snapshot.DadOwnsSuppression || !snapshot.AutoRetainerSuppressed ||
            snapshot.AutoRetainerBusy || snapshot.MultiModeEnabled ||
            snapshot.VermaxionMutationAuthorization == DadVermaxionMutationAuthorization.None)
        {
            var summary = acquire.Success
                ? authorization == DadVermaxionMutationAuthorization.Granted
                    ? "VERMAXION granted, but DAD could not verify suppression with AutoRetainer off/idle."
                    : BuildReservationHoldSummary(
                        snapshot.VermaxionReservationState,
                        snapshot.ExternalAutomationActivity,
                        snapshot.ExternalAutomationState,
                        snapshot.VermaxionReservationSummary)
                : acquire.Error;
            return ReturnToReadinessWait(operation, snapshot, summary, releaseReservation: true);
        }

        operation.VermaxionMutationAuthorization = snapshot.VermaxionMutationAuthorization;
        operation.Phase = DadWakeTakeoverPhase.Prepared;
        operation.Summary = BuildPreparedSummary(operation.VermaxionMutationAuthorization);
        operation.Acknowledgement = DadWakeAcknowledgementState.Accepted;
        operation.UpdatedAtUtc = utcNow();
        return BuildResult(operation, snapshot);
    }

    private DadWakeTakeoverResultDto Commit(OperationState operation, DadWakeTakeoverRequestDto request)
    {
        if (IsTerminal(operation.Phase))
            return BuildResult(operation);
        if (request.CommitKind == DadWakeCommitKind.None || !request.ExecutionTimeUtc.HasValue)
            return Block(operation, "GO requires a commit kind and execution time.", cleanup: true);

        var execution = EnsureUtc(request.ExecutionTimeUtc.Value);
        if (request.CommitKind == DadWakeCommitKind.Reset)
        {
            if (operation.Phase > DadWakeTakeoverPhase.Prepared)
                return BuildResult(operation);
            if (operation.Phase != DadWakeTakeoverPhase.Prepared)
                return BuildResult(operation, summary: "Reset GO rejected until this client is Prepared.", acknowledgement: DadWakeAcknowledgementState.Rejected);
            var snapshot = Capture(operation, forceExternalRefresh: true);
            var blocker = ValidateCoreTarget(snapshot, operation.Request);
            if (!string.IsNullOrWhiteSpace(blocker))
                return Block(operation, blocker, cleanup: true);
            if (!operation.CoordinatorAvailable || !CanAcceptResetGo(operation, snapshot))
            {
                ReturnToReadinessWait(
                    operation,
                    snapshot,
                    $"Reset GO not accepted: {BuildReadinessWaitSummary(snapshot)}",
                    releaseReservation: true);
                return BuildResult(operation, acknowledgement: DadWakeAcknowledgementState.Rejected);
            }
            operation.Phase = DadWakeTakeoverPhase.ResetCommitted;
            operation.CommitKind = DadWakeCommitKind.Reset;
            operation.ExecutionTimeUtc = execution;
            operation.Acknowledgement = DadWakeAcknowledgementState.Accepted;
            operation.Summary = $"Coordinated reset scheduled for {execution:O}";
        }
        else
        {
            if (operation.Phase > DadWakeTakeoverPhase.ResetVerified)
                return BuildResult(operation);
            if (operation.Phase != DadWakeTakeoverPhase.ResetVerified)
                return BuildResult(operation, summary: "Relog GO rejected until reset is verified.", acknowledgement: DadWakeAcknowledgementState.Rejected);
            operation.Phase = DadWakeTakeoverPhase.RelogCommitted;
            operation.CommitKind = DadWakeCommitKind.Relog;
            operation.ExecutionTimeUtc = execution;
            operation.Acknowledgement = DadWakeAcknowledgementState.Accepted;
            operation.Summary = $"Coordinated relog scheduled for {execution:O}";
        }

        operation.UpdatedAtUtc = utcNow();
        AdvanceDueOperation(operation);
        return BuildResult(operation);
    }

    private void AdvanceDueOperation(OperationState operation)
    {
        if (IsTerminal(operation.Phase) || operation.CleanupPending)
            return;
        if (operation.Phase == DadWakeTakeoverPhase.AwaitingArHook)
        {
            if (operation.PreparationStarted)
                Prepare(operation);
        }

        if (operation.Phase == DadWakeTakeoverPhase.PostprocessOwned)
        {
            var snapshot = Capture(operation, forceExternalRefresh: true);
            var blocker = ValidateCoreTarget(snapshot, operation.Request);
            if (!string.IsNullOrWhiteSpace(blocker))
            {
                Block(operation, blocker, cleanup: true);
            }
            else if (!IsPreparedStateValid(operation, snapshot))
            {
                ReturnToReadinessWait(
                    operation,
                    snapshot,
                    BuildReadinessWaitSummary(snapshot),
                    releaseReservation: true);
            }
        }

        if (operation.Phase == DadWakeTakeoverPhase.Prepared)
        {
            var snapshot = Capture(operation, forceExternalRefresh: true);
            var blocker = ValidateCoreTarget(snapshot, operation.Request);
            if (!string.IsNullOrWhiteSpace(blocker))
            {
                Block(operation, blocker, cleanup: true);
            }
            else if (!IsPreparedStateValid(operation, snapshot))
            {
                ReturnToReservationWait(operation, snapshot);
            }
            else if (operation.VermaxionMutationAuthorization != DadVermaxionMutationAuthorization.None &&
                     operation.VermaxionMutationAuthorization != snapshot.VermaxionMutationAuthorization)
            {
                operation.VermaxionMutationAuthorization = snapshot.VermaxionMutationAuthorization;
                operation.Summary = BuildPreparedSummary(operation.VermaxionMutationAuthorization);
                operation.UpdatedAtUtc = utcNow();
            }
        }

        if (operation.Phase is DadWakeTakeoverPhase.ResetCommitted or DadWakeTakeoverPhase.RelogCommitted &&
            operation.ExecutionTimeUtc.HasValue && utcNow() >= operation.ExecutionTimeUtc.Value)
        {
            if (operation.Phase == DadWakeTakeoverPhase.ResetCommitted)
                ExecuteReset(operation);
            else
                ExecuteRelog(operation);
        }

        if (operation.Phase == DadWakeTakeoverPhase.WaitingForCharacter)
            VerifyDestination(operation);
    }

    private void ExecuteReset(OperationState operation)
    {
        if (operation.ResetCommandAttempted)
        {
            TryFinalizeExecutedReset(operation);
            return;
        }

        if (!CanContinueCommittedReset(operation, forceExternalRefresh: true, out _))
            return;

        if (!operation.MultiModeDisableAttempted)
        {
            operation.MultiModeDisableAttempted = true;
            var disableMulti = Invoke(() => target.SetMultiModeEnabled(false), "disable AutoRetainer Multi Mode");
            if (!disableMulti.Success)
            {
                BeginNextEpoch(operation, disableMulti.Error);
                return;
            }
        }

        if (!CanContinueCommittedReset(operation, forceExternalRefresh: true, out _))
            return;
        if (!operation.DisableAutoRetainerAttempted)
        {
            operation.DisableAutoRetainerAttempted = true;
            var disableAr = Invoke(
                () => target.ExecuteCommand(DadWakeTakeoverCommand.DisableAutoRetainer, operation.Request),
                "send /ays d");
            if (!disableAr.Success)
            {
                BeginNextEpoch(operation, disableAr.Error);
                return;
            }
        }

        if (!CanContinueCommittedReset(operation, forceExternalRefresh: true, out _))
            return;
        if (!operation.ResetCommandAttempted)
        {
            operation.ResetCommandAttempted = true;
            var reset = Invoke(
                () => target.ExecuteCommand(DadWakeTakeoverCommand.ResetAutoRetainer, operation.Request),
                "send /ays reset");
            if (!reset.Success)
            {
                BeginNextEpoch(operation, reset.Error);
                return;
            }
        }

        operation.ResetIssuedUtc ??= utcNow();
        TryFinalizeExecutedReset(operation);
    }

    private bool TryFinalizeExecutedReset(OperationState operation)
    {
        // /ays reset already committed and executed once. Callback release is cleanup, so keep
        // retrying it even if the world becomes unsafe after the command; no new mutation command
        // is allowed and relog cannot be committed until this transition reaches ResetVerified.
        if (operation.CharacterPostprocessArmed)
        {
            if (!target.FinishCharacterPostprocess(retryAtNextBoundary: false))
            {
                operation.Summary = "Coordinated reset executed once; waiting for the DAD AutoRetainer callback lease to finish before relog can be committed.";
                operation.UpdatedAtUtc = utcNow();
                return false;
            }

            operation.CharacterPostprocessArmed = false;
            operation.LastSnapshot.DadOwnsCharacterPostprocess = false;
        }

        operation.Phase = DadWakeTakeoverPhase.ResetVerified;
        operation.Acknowledgement = DadWakeAcknowledgementState.Executed;
        operation.Summary = "Coordinated reset executed and verified; waiting for crew reset barrier.";
        operation.UpdatedAtUtc = utcNow();
        return true;
    }

    private void ExecuteRelog(OperationState operation)
    {
        var snapshot = Capture(operation, forceExternalRefresh: true);
        var blocker = ValidateCoreTarget(snapshot, operation.Request);
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            Block(operation, blocker, cleanup: true);
            return;
        }
        if (!CanExecuteRelogNow(operation, snapshot))
        {
            if (snapshot.AccountMatches && snapshot.CharacterKnownToAccount &&
                IsWorldReadyStable(snapshot) &&
                (!snapshot.DadOwnsSuppression || !snapshot.AutoRetainerSuppressed || snapshot.ExternalAutomationHeld))
            {
                BeginNextEpoch(operation, "Committed relog lost its operation-owned safety lease; returning to the readiness handshake.");
                return;
            }
            operation.Summary = $"Committed relog is waiting without timeout: {BuildReadinessWaitSummary(snapshot)}";
            operation.UpdatedAtUtc = utcNow();
            return;
        }

        if (!snapshot.CorrectCharacter && !operation.RelogCommandAttempted)
        {
            if (!PrepareHomeWorldBeforeRelog(operation, snapshot))
                return;

            // Nothing may intervene between this forced safety read and the relog command.
            snapshot = Capture(operation, forceExternalRefresh: true);
            blocker = ValidateCoreTarget(snapshot, operation.Request);
            if (!string.IsNullOrWhiteSpace(blocker))
            {
                Block(operation, blocker, cleanup: true);
                return;
            }
            if (!CanExecuteRelogNow(operation, snapshot))
            {
                operation.Summary = $"Committed relog is waiting without timeout: {BuildReadinessWaitSummary(snapshot)}";
                operation.UpdatedAtUtc = utcNow();
                return;
            }
            if (!PrepareHomeWorldBeforeRelog(operation, snapshot))
                return;

            operation.RelogCommandAttempted = true;
            var relog = Invoke(
                () => target.ExecuteCommand(DadWakeTakeoverCommand.RelogCharacter, operation.Request),
                $"send /ays relog {operation.Request.CharacterKey}");
            if (!relog.Success)
            {
                BeginNextEpoch(operation, relog.Error);
                return;
            }
            var issuedAt = utcNow();
            operation.RelogIssuedUtc = issuedAt;
            operation.RelogAcceptedAtUtc = issuedAt;
            operation.RelogSourceCharacterKey = snapshot.Participant.ActiveCharacterKey;
            operation.RelogTransitionObserved = false;
            operation.StableWrongCharacterSinceUtc = snapshot.Participant.WorldReadyStable ? issuedAt : null;
        }

        operation.Phase = DadWakeTakeoverPhase.WaitingForCharacter;
        operation.Acknowledgement = DadWakeAcknowledgementState.Executed;
        operation.Summary = snapshot.CorrectCharacter
            ? "Correct character required no relog; verifying the pre-AR destination gate."
            : $"Relog issued for {operation.Request.CharacterKey}; retaining DAD suppression through login.";
        operation.UpdatedAtUtc = utcNow();
        VerifyDestination(operation);
    }

    private bool PrepareHomeWorldBeforeRelog(
        OperationState operation,
        DadWakeTakeoverTargetSnapshot snapshot)
    {
        var decision = operation.HomeWorldReturnGate.Evaluate(
            snapshot.Participant,
            snapshot.LifestreamAvailable,
            snapshot.LifestreamBusy,
            utcNow());
        switch (decision.Action)
        {
            case DadHomeWorldReturnAction.Ready:
                operation.Summary = decision.Summary;
                operation.UpdatedAtUtc = utcNow();
                return true;
            case DadHomeWorldReturnAction.InvokeLifestream:
                operation.HomeWorldReturnStarted = true;
                var result = target.ChangeWorld(decision.DestinationWorldName);
                operation.HomeWorldReturnGate.RecordInvocationResult(result, utcNow());
                if (result.Outcome == DadLifestreamChangeWorldOutcome.Uncertain)
                {
                    Block(
                        operation,
                        $"Return-home travel failed closed before relog: {result.Summary}",
                        cleanup: true);
                    return false;
                }
                operation.Summary = result.Outcome == DadLifestreamChangeWorldOutcome.Accepted
                    ? $"{result.Summary} Waiting for fresh world-stable home proof before relog."
                    : result.Summary;
                operation.UpdatedAtUtc = utcNow();
                return false;
            case DadHomeWorldReturnAction.Wait:
                operation.HomeWorldReturnStarted = true;
                operation.Summary = decision.Summary;
                operation.UpdatedAtUtc = utcNow();
                return false;
            default:
                Block(operation, decision.Summary, cleanup: true);
                return false;
        }
    }

    private void VerifyDestination(OperationState operation)
    {
        var snapshot = Capture(operation, forceExternalRefresh: true);
        var blocker = ValidateCoreTarget(snapshot, operation.Request);
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            Block(operation, blocker, cleanup: true);
            return;
        }
        if (!snapshot.CorrectCharacter)
        {
            var now = utcNow();
            var activeCharacter = snapshot.Participant.ActiveCharacterKey;
            if (!snapshot.Participant.IsAvailable || !snapshot.Participant.WorldReadyStable)
            {
                operation.RelogTransitionObserved = true;
                operation.StableWrongCharacterSinceUtc = null;
                operation.Summary = BuildRelogWaitSummary(operation, snapshot);
                return;
            }

            if (!operation.RelogSourceCharacterKey.IsEmpty &&
                !string.Equals(
                    activeCharacter.Value,
                    operation.RelogSourceCharacterKey.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                operation.RelogTransitionObserved = true;
            }

            operation.StableWrongCharacterSinceUtc ??= now;
            var provenNoEffect = !operation.RelogTransitionObserved &&
                                 operation.RelogAcceptedAtUtc.HasValue &&
                                 now - operation.RelogAcceptedAtUtc.Value >= TimeSpan.FromSeconds(15);
            if (operation.RelogTransitionObserved || provenNoEffect)
            {
                BeginNextEpoch(
                    operation,
                    operation.RelogTransitionObserved
                        ? $"Relog settled world-stable on {activeCharacter}; retrying target {operation.Request.CharacterKey}."
                        : $"Relog had no transition effect for 15 seconds on {activeCharacter}; retrying target {operation.Request.CharacterKey}.");
                return;
            }

            operation.Summary = BuildRelogWaitSummary(operation, snapshot);
            return;
        }

        if (!IsDestinationReady(snapshot))
        {
            operation.Summary = $"Waiting for {operation.Request.CharacterKey} world-ready stability with AutoRetainer off.";
            return;
        }

        if (operation.CharacterPostprocessArmed)
        {
            if (!target.FinishCharacterPostprocess(retryAtNextBoundary: false))
            {
                operation.Summary = "Destination verified; waiting for the DAD AutoRetainer callback lease to release.";
                return;
            }

            operation.CharacterPostprocessArmed = false;
            operation.LastSnapshot.DadOwnsCharacterPostprocess = false;
        }

        if (operation.ReservationRequested)
        {
            if (!target.ReleaseVermaxion(operation.Request.OperationToken))
            {
                operation.Summary = "Destination verified; waiting for DAD VERMAXION reservation release verification.";
                return;
            }
            operation.ReservationRequested = false;
        }

        if (operation.SuppressionAcquired || snapshot.DadOwnsSuppression)
        {
            if (!target.ReleaseSuppressionIfOwned())
            {
                operation.Summary = "Destination verified; waiting for DAD suppression release verification.";
                return;
            }
            operation.SuppressionAcquired = false;
        }

        snapshot = Capture(operation, forceExternalRefresh: true);
        blocker = ValidateCoreTarget(snapshot, operation.Request);
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            Block(operation, blocker, cleanup: true);
            return;
        }
        if (!IsDestinationReady(snapshot))
        {
            operation.Summary = snapshot.CorrectCharacter
                ? "Destination changed while DAD leases were releasing; waiting for world/AR/Lifestream safety to stabilize again."
                : BuildRelogWaitSummary(operation, snapshot);
            return;
        }

        operation.Phase = DadWakeTakeoverPhase.Ready;
        operation.ReadyUtc = utcNow();
        operation.Acknowledgement = DadWakeAcknowledgementState.Executed;
        operation.Summary = $"{operation.Request.CharacterKey} is world-ready after takeover.";
        operation.UpdatedAtUtc = operation.ReadyUtc.Value;
        if (string.Equals(activeOperationKey, DadWakePolicyRules.BuildOperationKey(operation.Request), StringComparison.OrdinalIgnoreCase))
            activeOperationKey = string.Empty;
    }

    private static string BuildRelogWaitSummary(OperationState operation, DadWakeTakeoverTargetSnapshot snapshot)
    {
        if (!snapshot.Participant.IsAvailable)
            return "Relog command started a login transition; waiting for a local character without timeout.";
        if (!snapshot.Participant.WorldReadyStable)
            return "Relog command started a world/login transition; waiting for stability without timeout.";
        if (snapshot.AutoRetainerBusy)
            return "Relog command is in progress; AutoRetainer is busy.";
        if (!snapshot.LifestreamAvailable || snapshot.LifestreamBusy)
            return "Relog command is in progress; Lifestream is busy or unavailable.";
        if (snapshot.MultiModeEnabled || !snapshot.DadOwnsSuppression || !snapshot.AutoRetainerSuppressed)
            return "Waiting for the committed relog destination with Multi Mode off and DAD suppression ownership verified.";
        return $"Relog was issued once; waiting without timeout for {operation.Request.CharacterKey}. Cancel to stop.";
    }

    private DadWakeTakeoverResultDto Cancel(OperationState operation, string reason)
    {
        if (operation.Phase == DadWakeTakeoverPhase.Ready ||
            (operation.Phase == DadWakeTakeoverPhase.Cancelled &&
             !operation.CleanupPending &&
             operation.Acknowledgement == DadWakeAcknowledgementState.Executed))
        {
            return BuildResult(operation);
        }

        if (operation.Phase != DadWakeTakeoverPhase.Cancelled || string.IsNullOrWhiteSpace(operation.BlockedReason))
            operation.BlockedReason = string.IsNullOrWhiteSpace(reason) ? "Takeover cancelled." : reason;

        operation.CoordinatorAvailable = false;
        operation.CleanupReleaseReservation = true;
        operation.Phase = DadWakeTakeoverPhase.Cancelled;
        operation.Status = DadWakeTakeoverStatus.Blocked;
        operation.Acknowledgement = DadWakeAcknowledgementState.Pending;
        operation.CleanupPending = !TryCleanupOwnedLeases(operation, retryAtNextBoundary: false);
        if (operation.CleanupPending)
        {
            operation.Summary = $"{operation.BlockedReason} Waiting for DAD-owned takeover state to release before acknowledging cancellation.";
            operation.UpdatedAtUtc = utcNow();
        }
        else
        {
            FinalizeCancellation(operation);
        }
        return BuildResult(operation);
    }

    private void FinalizeCancellation(OperationState operation)
    {
        operation.CleanupPending = false;
        operation.Phase = DadWakeTakeoverPhase.Cancelled;
        operation.Status = DadWakeTakeoverStatus.Blocked;
        operation.Acknowledgement = DadWakeAcknowledgementState.Executed;
        operation.Summary = string.IsNullOrWhiteSpace(operation.BlockedReason)
            ? "Takeover cancelled."
            : operation.BlockedReason;
        operation.UpdatedAtUtc = utcNow();
        ClearActiveOperationIfMatches(operation);
    }

    private DadWakeTakeoverResultDto Block(OperationState operation, string reason, bool cleanup)
    {
        if (cleanup)
        {
            operation.CleanupReleaseReservation = true;
            operation.CleanupPending = !TryCleanupOwnedLeases(operation, retryAtNextBoundary: false);
        }
        operation.Phase = DadWakeTakeoverPhase.Blocked;
        operation.Status = DadWakeTakeoverStatus.Blocked;
        operation.Acknowledgement = DadWakeAcknowledgementState.Rejected;
        operation.BlockedReason = string.IsNullOrWhiteSpace(reason) ? "Wake takeover blocked." : reason;
        operation.Summary = operation.BlockedReason;
        operation.UpdatedAtUtc = utcNow();
        if (!operation.CleanupPending)
            ClearActiveOperationIfMatches(operation);
        return BuildResult(operation);
    }

    private void ClearActiveOperationIfMatches(OperationState operation)
    {
        if (string.Equals(
                activeOperationKey,
                DadWakePolicyRules.BuildOperationKey(operation.Request),
                StringComparison.OrdinalIgnoreCase))
        {
            activeOperationKey = string.Empty;
        }
    }

    private bool TryCleanupOwnedLeases(OperationState operation, bool retryAtNextBoundary, bool releaseReservation = true)
    {
        var postprocessFinished = true;
        if (operation.CharacterPostprocessArmed || operation.LastSnapshot.DadOwnsCharacterPostprocess)
        {
            postprocessFinished = target.FinishCharacterPostprocess(retryAtNextBoundary);
            if (postprocessFinished)
            {
                operation.CharacterPostprocessArmed = false;
                operation.LastSnapshot.DadOwnsCharacterPostprocess = false;
            }
        }

        var suppressionReleased = true;
        if (operation.SuppressionAcquired || operation.LastSnapshot.DadOwnsSuppression)
        {
            suppressionReleased = target.ReleaseSuppressionIfOwned();
            if (suppressionReleased)
            {
                operation.SuppressionAcquired = false;
                operation.LastSnapshot.DadOwnsSuppression = false;
                operation.LastSnapshot.AutoRetainerSuppressed = false;
            }
        }

        var reservationReleased = true;
        if (releaseReservation && operation.ReservationRequested)
        {
            reservationReleased = target.ReleaseVermaxion(operation.Request.OperationToken);
            if (reservationReleased)
                operation.ReservationRequested = false;
        }
        return postprocessFinished && suppressionReleased && reservationReleased;
    }

    private DadWakeTakeoverTargetSnapshot Capture(OperationState operation, bool forceExternalRefresh = false)
    {
        try
        {
            operation.LastSnapshot = target.Capture(operation.Request, forceExternalRefresh) ?? new DadWakeTakeoverTargetSnapshot();
        }
        catch (Exception ex)
        {
            operation.LastSnapshot = new DadWakeTakeoverTargetSnapshot
            {
                AutoRetainerStatus = $"Wake takeover target snapshot failed: {ex.Message}",
            };
        }
        return operation.LastSnapshot;
    }

    private void ReturnToReservationWait(
        OperationState operation,
        DadWakeTakeoverTargetSnapshot snapshot)
    {
        var summary = !operation.CoordinatorAvailable || !IsWorldReadyStable(snapshot) ||
                      !snapshot.AutoRetainerAvailable || snapshot.AutoRetainerBusy || snapshot.ExternalAutomationHeld
            ? BuildReadinessWaitSummary(snapshot)
            : operation.VermaxionMutationAuthorization == DadVermaxionMutationAuthorization.None
                ? BuildLeaseYieldSummary(snapshot)
                : snapshot.VermaxionMutationAuthorization == DadVermaxionMutationAuthorization.Granted
                    ? "VERMAXION grant remains current; waiting for AutoRetainer idle, Multi Mode off, and DAD suppression reacquisition."
                    : snapshot.VermaxionMutationAuthorization == DadVermaxionMutationAuthorization.CompatibilityIdle
                        ? "Compatibility handoff evidence remains idle; waiting to reacquire and verify DAD suppression."
                        : BuildReservationHoldSummary(
                            snapshot.VermaxionReservationState,
                            snapshot.ExternalAutomationActivity,
                            snapshot.ExternalAutomationState,
                            snapshot.VermaxionReservationSummary);
        ReturnToReadinessWait(operation, snapshot, summary, releaseReservation: true);
    }

    private DadWakeTakeoverResultDto ReturnToReadinessWait(
        OperationState operation,
        DadWakeTakeoverTargetSnapshot snapshot,
        string summary,
        bool releaseReservation)
    {
        operation.CleanupReleaseReservation = releaseReservation;
        operation.CleanupPending = !TryCleanupOwnedLeases(
            operation,
            retryAtNextBoundary: false,
            releaseReservation: releaseReservation);
        operation.VermaxionMutationAuthorization = DadVermaxionMutationAuthorization.None;
        operation.Phase = DadWakeTakeoverPhase.AwaitingArHook;
        operation.CommitKind = DadWakeCommitKind.None;
        operation.ExecutionTimeUtc = null;
        operation.Acknowledgement = DadWakeAcknowledgementState.Pending;
        operation.Summary = string.IsNullOrWhiteSpace(summary)
            ? "Waiting for safe takeover readiness; no timeout; cancel to stop."
            : summary;
        operation.UpdatedAtUtc = utcNow();
        return BuildResult(operation, snapshot);
    }

    private DadWakeTakeoverResultDto BeginNextEpoch(OperationState operation, string reason)
    {
        operation.CleanupReleaseReservation = true;
        operation.CleanupPending = !TryCleanupOwnedLeases(
            operation,
            retryAtNextBoundary: false,
            releaseReservation: true);
        operation.Epoch++;
        operation.EpochStartedAtUtc = utcNow();
        operation.NextEpochEligibleUtc = operation.EpochStartedAtUtc + TimeSpan.FromSeconds(5);
        operation.Phase = DadWakeTakeoverPhase.AwaitingArHook;
        operation.Status = DadWakeTakeoverStatus.Pending;
        operation.CommitKind = DadWakeCommitKind.None;
        operation.ExecutionTimeUtc = null;
        operation.Acknowledgement = DadWakeAcknowledgementState.Pending;
        operation.BlockedReason = string.Empty;
        operation.VermaxionMutationAuthorization = DadVermaxionMutationAuthorization.None;
        operation.MultiModeDisableAttempted = false;
        operation.DisableAutoRetainerAttempted = false;
        operation.ResetCommandAttempted = false;
        operation.RelogCommandAttempted = false;
        operation.ResetIssuedUtc = null;
        operation.RelogIssuedUtc = null;
        operation.RelogAcceptedAtUtc = null;
        operation.RelogSourceCharacterKey = new DadCharacterKey(string.Empty);
        operation.RelogTransitionObserved = false;
        operation.StableWrongCharacterSinceUtc = null;
        operation.HomeWorldReturnGate = new DadHomeWorldReturnGate();
        operation.HomeWorldReturnStarted = false;
        operation.Summary = $"Takeover epoch {operation.Epoch} queued after retryable outcome: {reason}";
        operation.UpdatedAtUtc = operation.EpochStartedAtUtc;
        diagnostic?.Invoke(
            $"request={operation.Request.SchedulerRunId} slot={operation.Request.SlotId} account={operation.Request.AccountKey} character={operation.Request.CharacterKey} epoch={operation.Epoch} reason={reason}");
        return BuildResult(operation);
    }

    private bool CanContinueCommittedReset(
        OperationState operation,
        bool forceExternalRefresh,
        out DadWakeTakeoverTargetSnapshot snapshot)
    {
        snapshot = Capture(operation, forceExternalRefresh);
        var blocker = ValidateCoreTarget(snapshot, operation.Request);
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            Block(operation, blocker, cleanup: true);
            return false;
        }

        var leaseReady = snapshot.DadOwnsSuppression && snapshot.AutoRetainerSuppressed &&
                         (operation.VermaxionMutationAuthorization != DadVermaxionMutationAuthorization.None ||
                          snapshot.DadOwnsCharacterPostprocess);
        var authorizationReady = operation.VermaxionMutationAuthorization == DadVermaxionMutationAuthorization.None ||
                                 snapshot.VermaxionMutationAuthorization != DadVermaxionMutationAuthorization.None;
        if (!IsWorldAndLocalServicesSafe(snapshot) ||
            snapshot.ExternalAutomationHeld || !leaseReady || !authorizationReady)
        {
            if (snapshot.AccountMatches && snapshot.CharacterKnownToAccount &&
                IsWorldReadyStable(snapshot) &&
                (!leaseReady || !authorizationReady || snapshot.ExternalAutomationHeld))
            {
                BeginNextEpoch(operation, "Committed reset lost its operation-owned safety lease; returning to the readiness handshake.");
                return false;
            }
            operation.Summary = $"Committed reset is waiting without timeout: {BuildReadinessWaitSummary(snapshot)}";
            operation.UpdatedAtUtc = utcNow();
            return false;
        }

        if (operation.VermaxionMutationAuthorization != DadVermaxionMutationAuthorization.None)
            operation.VermaxionMutationAuthorization = snapshot.VermaxionMutationAuthorization;
        return true;
    }

    private static bool CanAcceptResetGo(OperationState operation, DadWakeTakeoverTargetSnapshot snapshot)
    {
        if (!operation.CoordinatorAvailable || !IsWorldMutationSafe(snapshot) ||
            !snapshot.DadOwnsSuppression || !snapshot.AutoRetainerSuppressed)
        {
            return false;
        }

        return operation.VermaxionMutationAuthorization != DadVermaxionMutationAuthorization.None
            ? IsVerifiedReservationPreparation(snapshot)
            : snapshot.DadOwnsCharacterPostprocess;
    }

    private static bool CanExecuteRelogNow(OperationState operation, DadWakeTakeoverTargetSnapshot snapshot)
        => IsWorldMutationSafe(snapshot) &&
           !snapshot.MultiModeEnabled &&
           snapshot.DadOwnsSuppression &&
           snapshot.AutoRetainerSuppressed;

    private static bool IsPreReservationReady(DadWakeTakeoverTargetSnapshot snapshot)
        => IsWorldAndLocalServicesSafe(snapshot) &&
           !IsExternalAutomationBlocking(snapshot);

    private static bool IsWorldAndLocalServicesSafe(DadWakeTakeoverTargetSnapshot snapshot)
        => snapshot.AccountMatches &&
           snapshot.CharacterKnownToAccount &&
           IsWorldReadyStable(snapshot) &&
           snapshot.AutoRetainerAvailable &&
           !snapshot.AutoRetainerBusy &&
           snapshot.LifestreamAvailable &&
           !snapshot.LifestreamBusy;

    private static bool IsWorldMutationSafe(DadWakeTakeoverTargetSnapshot snapshot)
        => IsWorldAndLocalServicesSafe(snapshot) && !snapshot.ExternalAutomationHeld;

    private static bool IsDestinationReady(DadWakeTakeoverTargetSnapshot snapshot)
        => snapshot.CorrectCharacter &&
           IsWorldMutationSafe(snapshot) &&
           !snapshot.MultiModeEnabled;

    private static bool IsExternalAutomationBlocking(DadWakeTakeoverTargetSnapshot snapshot)
        => snapshot.ExternalAutomationHeld &&
           !(snapshot.VermaxionReservationAuthoritative &&
             snapshot.VermaxionReservationState == DadVermaxionReservationState.Released);

    private static bool IsOwnedReservationWait(DadWakeTakeoverTargetSnapshot snapshot)
        => snapshot.VermaxionReservationAuthoritative &&
           snapshot.VermaxionReservationState is DadVermaxionReservationState.Pending
               or DadVermaxionReservationState.Granting;

    private static bool IsWorldReadyStable(DadWakeTakeoverTargetSnapshot snapshot)
        => snapshot.Participant.IsAvailable && snapshot.Participant.WorldReadyStable;

    private static bool IsPreparedStateValid(
        OperationState operation,
        DadWakeTakeoverTargetSnapshot snapshot)
    {
        if (!operation.CoordinatorAvailable || !IsWorldMutationSafe(snapshot) ||
            !snapshot.SuppressionReadable || !snapshot.DadOwnsSuppression || !snapshot.AutoRetainerSuppressed)
        {
            return false;
        }

        return operation.VermaxionMutationAuthorization != DadVermaxionMutationAuthorization.None
            ? IsVerifiedReservationPreparation(snapshot)
            : snapshot.DadOwnsCharacterPostprocess;
    }

    private static string BuildReadinessWaitSummary(DadWakeTakeoverTargetSnapshot snapshot)
    {
        if (!snapshot.AccountMatches)
            return "Waiting for the exact configured stable-account client; no mutation and no timeout.";
        if (!snapshot.CharacterKnownToAccount)
            return "Waiting for authoritative character/account catalog truth; no mutation and no timeout.";
        if (!snapshot.Participant.IsAvailable)
            return "Waiting for a connected local character; no timeout; cancel to stop.";
        if (!snapshot.Participant.WorldReadyStable)
            return "Waiting for world-ready stability (duty, queue, loading, or transition still active); no timeout; cancel to stop.";
        if (!snapshot.AutoRetainerAvailable)
            return string.IsNullOrWhiteSpace(snapshot.AutoRetainerStatus)
                ? "Waiting for readable AutoRetainer handoff state; no timeout; cancel to stop."
                : $"{snapshot.AutoRetainerStatus} No timeout; cancel to stop.";
        if (snapshot.AutoRetainerBusy)
            return "Waiting for AutoRetainer work to finish; no timeout; cancel to stop.";
        if (snapshot.ExternalAutomationHeld)
            return $"{BuildExternalHoldSummary(snapshot)} No timeout; cancel to stop.";
        if (!snapshot.LifestreamAvailable || snapshot.LifestreamBusy)
            return "Waiting for Lifestream to become available and idle; no timeout; cancel to stop.";
        if (!snapshot.DadOwnsSuppression || !snapshot.AutoRetainerSuppressed)
            return "Waiting for the already-owned DAD suppression lease; no timeout; cancel to stop.";
        return "Waiting for safe takeover readiness; no timeout; cancel to stop.";
    }

    private static bool IsVerifiedReservationPreparation(DadWakeTakeoverTargetSnapshot snapshot)
        => snapshot.VermaxionMutationAuthorization != DadVermaxionMutationAuthorization.None &&
           snapshot.AutoRetainerAvailable &&
           !snapshot.AutoRetainerBusy &&
           !snapshot.MultiModeEnabled &&
           snapshot.SuppressionReadable &&
           snapshot.DadOwnsSuppression &&
           snapshot.AutoRetainerSuppressed;

    private static string BuildPreparedSummary(DadVermaxionMutationAuthorization authorization)
        => authorization == DadVermaxionMutationAuthorization.CompatibilityIdle
            ? "Compatibility handoff: VERMAXION idle / AR idle; DAD suppression acquired; waiting for crew with no timeout; cancel to stop."
            : "VERMAXION handoff granted; AR off/idle and DAD suppression acquired; waiting for crew with no timeout; cancel to stop.";

    private static string ValidateCoreTarget(DadWakeTakeoverTargetSnapshot snapshot, DadWakeTakeoverRequestDto request)
    {
        if (!snapshot.DadEnabled)
            return "DAD is disabled on the target client.";
        if (!snapshot.RemoteMutationAllowed)
            return "Remote mutation is disabled on the target client.";
        return string.Empty;
    }

    private static string BuildExternalHoldSummary(DadWakeTakeoverTargetSnapshot snapshot)
    {
        var detail = string.Join("/", new[] { snapshot.ExternalAutomationActivity, snapshot.ExternalAutomationState }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(detail)
            ? "Waiting for VERMAXION status."
            : $"Waiting for VERMAXION — {detail}. {snapshot.ExternalAutomationSummary}".Trim();
    }

    private static string BuildReservationHoldSummary(
        DadVermaxionReservationState reservationState,
        string activity,
        string state,
        string summary)
    {
        var detail = string.Join(
            "/",
            new[] { activity, state }.Where(static value => !string.IsNullOrWhiteSpace(value)));
        var activityText = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";
        return $"Waiting for VERMAXION reservation — {reservationState}{activityText}. {summary} No timeout; cancel to stop.".Trim();
    }

    private static string BuildLeaseYieldSummary(DadWakeTakeoverTargetSnapshot snapshot)
    {
        if (snapshot.ExternalAutomationHeld)
            return BuildExternalHoldSummary(snapshot) + " DAD yielded this character callback.";
        if (!snapshot.SuppressionReadable)
            return "AutoRetainer suppression is unreadable; DAD yielded this character callback.";
        if (snapshot.AutoRetainerSuppressed && !snapshot.DadOwnsSuppression)
            return "AutoRetainer suppression is externally owned; DAD yielded this character callback.";
        return "DAD could not verify its character-postprocess lease; retrying at a future character boundary.";
    }

    private static DadWakeTakeoverActionResult Invoke(Func<DadWakeTakeoverActionResult> action, string description)
    {
        try
        {
            var result = action();
            return result.Success ? result : DadWakeTakeoverActionResult.Rejected(
                string.IsNullOrWhiteSpace(result.Error) ? $"Failed to {description}." : result.Error);
        }
        catch (Exception ex)
        {
            return DadWakeTakeoverActionResult.Rejected($"Failed to {description}: {ex.Message}");
        }
    }

    private DadWakeTakeoverResultDto BuildResult(
        OperationState operation,
        DadWakeTakeoverTargetSnapshot? snapshot = null,
        string? summary = null,
        DadWakeAcknowledgementState? acknowledgement = null)
    {
        snapshot ??= operation.LastSnapshot;
        var phase = operation.Phase;
        var status = phase == DadWakeTakeoverPhase.Ready
            ? DadWakeTakeoverStatus.Ready
            : phase is DadWakeTakeoverPhase.Blocked or DadWakeTakeoverPhase.Cancelled
                ? DadWakeTakeoverStatus.Blocked
                : phase >= DadWakeTakeoverPhase.RelogCommitted
                    ? DadWakeTakeoverStatus.RelogIssued
                    : DadWakeTakeoverStatus.Pending;
        return new DadWakeTakeoverResultDto
        {
            SchedulerRunId = operation.Request.SchedulerRunId,
            SlotId = operation.Request.SlotId,
            AccountKey = operation.Request.AccountKey,
            CharacterKey = operation.Request.CharacterKey,
            OperationToken = operation.Request.OperationToken,
            Phase = phase,
            Stage = ResolveStage(operation, snapshot),
            Status = status,
            CommitKind = operation.CommitKind,
            ExecutionTimeUtc = operation.ExecutionTimeUtc,
            AcknowledgementState = acknowledgement ?? operation.Acknowledgement,
            // Preserve the existing wire meaning. Takeover safety intentionally uses the
            // independent Participant.WorldReadyStable value carried in Snapshot instead.
            PostArReady = snapshot.PostArReady,
            AutoRetainerAvailable = snapshot.AutoRetainerAvailable,
            AutoRetainerBusy = snapshot.AutoRetainerBusy,
            MultiModeEnabled = snapshot.MultiModeEnabled,
            RelogIssued = operation.RelogIssuedUtc.HasValue,
            ExternalAutomationHeld = snapshot.ExternalAutomationHeld,
            VermaxionReservationState = snapshot.VermaxionReservationState,
            VermaxionReservationSummary = snapshot.VermaxionReservationSummary,
            VermaxionReservationCreatedAtUtc = snapshot.VermaxionReservationCreatedAtUtc,
            VermaxionReservationUpdatedAtUtc = snapshot.VermaxionReservationUpdatedAtUtc,
            ExternalAutomationActivity = snapshot.ExternalAutomationActivity,
            ExternalAutomationState = snapshot.ExternalAutomationState,
            ExternalAutomationSummary = snapshot.ExternalAutomationSummary,
            ResetIssuedUtc = operation.ResetIssuedUtc,
            TakeoverVerifiedUtc = operation.ResetIssuedUtc,
            RelogIssuedUtc = operation.RelogIssuedUtc,
            ReadyUtc = operation.ReadyUtc,
            Summary = summary ?? operation.Summary,
            BlockedReason = operation.BlockedReason,
            Snapshot = snapshot.Participant?.Clone() ?? new DadParticipantSnapshot(),
        };
    }

    private static DadWakeTakeoverResultDto Blocked(DadWakeTakeoverRequestDto request, OperationState? operation, string reason)
        => operation == null
            ? new DadWakeTakeoverResultDto
            {
                SchedulerRunId = request.SchedulerRunId,
                SlotId = request.SlotId,
                AccountKey = request.AccountKey,
                CharacterKey = request.CharacterKey,
                OperationToken = request.OperationToken,
                Status = DadWakeTakeoverStatus.Blocked,
                Stage = DadWakeTakeoverStage.Blocked,
                Phase = DadWakeTakeoverPhase.Blocked,
                AcknowledgementState = DadWakeAcknowledgementState.Rejected,
                Summary = reason,
                BlockedReason = reason,
            }
            : throw new InvalidOperationException("Use Block for tracked operations.");

    private static DadWakeTakeoverStage ToStage(DadWakeTakeoverPhase phase)
        => phase switch
        {
            DadWakeTakeoverPhase.AwaitingArHook => DadWakeTakeoverStage.AwaitingArHook,
            DadWakeTakeoverPhase.PostprocessOwned => DadWakeTakeoverStage.PostprocessOwned,
            DadWakeTakeoverPhase.Prepared => DadWakeTakeoverStage.Prepared,
            DadWakeTakeoverPhase.ResetCommitted => DadWakeTakeoverStage.ResetCommitted,
            DadWakeTakeoverPhase.ResetVerified => DadWakeTakeoverStage.ResetVerified,
            DadWakeTakeoverPhase.RelogCommitted => DadWakeTakeoverStage.RelogCommitted,
            DadWakeTakeoverPhase.WaitingForCharacter => DadWakeTakeoverStage.WaitingForCharacter,
            DadWakeTakeoverPhase.Ready => DadWakeTakeoverStage.Ready,
            _ => DadWakeTakeoverStage.Blocked,
        };

    private static DadWakeTakeoverStage ResolveStage(
        OperationState operation,
        DadWakeTakeoverTargetSnapshot snapshot)
    {
        var phase = operation.Phase;
        if (phase == DadWakeTakeoverPhase.RelogCommitted && operation.HomeWorldReturnStarted)
        {
            return operation.HomeWorldReturnGate.AcceptedInvocation
                ? DadWakeTakeoverStage.WaitingForHomeWorld
                : DadWakeTakeoverStage.ReturningHome;
        }
        if (phase != DadWakeTakeoverPhase.AwaitingArHook)
            return ToStage(phase);
        if (!snapshot.Participant.IsAvailable)
            return DadWakeTakeoverStage.WaitingForClient;
        if (!snapshot.Participant.WorldReadyStable)
            return DadWakeTakeoverStage.WaitingForPostArReady;
        if (!snapshot.AutoRetainerAvailable || snapshot.AutoRetainerBusy)
            return DadWakeTakeoverStage.WaitingForAutoRetainer;
        if (snapshot.ExternalAutomationHeld)
            return DadWakeTakeoverStage.WaitingForExternalAutomation;
        return DadWakeTakeoverStage.AwaitingArHook;
    }

    private static void NormalizeRequest(DadWakeTakeoverRequestDto request)
    {
        request.OperationToken = string.IsNullOrWhiteSpace(request.OperationToken)
            ? request.SchedulerRunId?.Trim() ?? string.Empty
            : request.OperationToken.Trim();
    }

    private static string ValidateRequest(DadWakeTakeoverRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.SchedulerRunId))
            return "Wake takeover requires a scheduler run id.";
        if (string.IsNullOrWhiteSpace(request.OperationToken))
            return "Wake takeover requires an operation token.";
        if (string.IsNullOrWhiteSpace(request.SlotId))
            return "Wake takeover requires a slot id.";
        if (request.AccountKey.IsEmpty)
            return "Wake takeover requires an account key.";
        if (!DadWakePolicyRules.IsValidCharacterKey(request.CharacterKey))
            return "Wake takeover requires a known Name@World character key.";
        return string.Empty;
    }

    private static bool SameOperation(DadWakeTakeoverRequestDto left, DadWakeTakeoverRequestDto right)
        => string.Equals(left.OperationToken, right.OperationToken, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.SlotId, right.SlotId, StringComparison.OrdinalIgnoreCase) &&
           left.AccountKey.Equals(right.AccountKey) && left.CharacterKey.Equals(right.CharacterKey);

    private static DadWakeTakeoverRequestDto CloneRequest(DadWakeTakeoverRequestDto request)
        => new()
        {
            SchedulerRunId = request.SchedulerRunId,
            SlotId = request.SlotId,
            AccountKey = request.AccountKey,
            CharacterKey = request.CharacterKey,
            RequestedAtUtc = request.RequestedAtUtc,
            OperationToken = request.OperationToken,
        };

    private void PruneOperations()
    {
        var cutoff = utcNow() - OperationRetention;
        foreach (var key in operations.Where(pair =>
                         IsTerminal(pair.Value.Phase) &&
                         !pair.Value.CleanupPending &&
                         pair.Value.UpdatedAtUtc < cutoff)
                     .Select(static pair => pair.Key).ToList())
            operations.Remove(key);
    }

    private static bool IsTerminal(DadWakeTakeoverPhase phase)
        => phase is DadWakeTakeoverPhase.Ready or DadWakeTakeoverPhase.Blocked or DadWakeTakeoverPhase.Cancelled;

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            foreach (var operation in operations.Values.Where(static operation =>
                         !IsTerminal(operation.Phase) || operation.CleanupPending))
            {
                operation.CleanupPending = !TryCleanupOwnedLeases(operation, retryAtNextBoundary: false);
                if (!operation.CleanupPending && operation.Phase == DadWakeTakeoverPhase.Cancelled)
                    FinalizeCancellation(operation);
            }
            activeOperationKey = string.Empty;
        }
    }

    private sealed class OperationState
    {
        public OperationState(DadWakeTakeoverRequestDto request, DateTime createdAtUtc)
        {
            Request = request;
            CreatedAtUtc = createdAtUtc;
            UpdatedAtUtc = createdAtUtc;
            EpochStartedAtUtc = createdAtUtc;
        }

        public DadWakeTakeoverRequestDto Request { get; }
        public DateTime CreatedAtUtc { get; }
        public DateTime UpdatedAtUtc { get; set; }
        public bool PreparationStarted { get; set; }
        public DadWakeTakeoverPhase Phase { get; set; } = DadWakeTakeoverPhase.AwaitingArHook;
        public DadWakeTakeoverStatus Status { get; set; } = DadWakeTakeoverStatus.Pending;
        public DadWakeCommitKind CommitKind { get; set; }
        public DateTime? ExecutionTimeUtc { get; set; }
        public DadWakeAcknowledgementState Acknowledgement { get; set; } = DadWakeAcknowledgementState.Pending;
        public DateTime? ResetIssuedUtc { get; set; }
        public DateTime? RelogIssuedUtc { get; set; }
        public DateTime? ReadyUtc { get; set; }
        public string Summary { get; set; } = "Waiting for AutoRetainer character postprocess";
        public string BlockedReason { get; set; } = string.Empty;
        public DadWakeTakeoverTargetSnapshot LastSnapshot { get; set; } = new();
        public bool CleanupPending { get; set; }
        public bool CleanupReleaseReservation { get; set; } = true;
        public DadVermaxionMutationAuthorization VermaxionMutationAuthorization { get; set; }
        public bool CoordinatorAvailable { get; set; } = true;
        public bool ReservationRequested { get; set; }
        public bool CharacterPostprocessArmed { get; set; }
        public bool SuppressionAcquired { get; set; }
        public bool MultiModeDisableAttempted { get; set; }
        public bool DisableAutoRetainerAttempted { get; set; }
        public bool ResetCommandAttempted { get; set; }
        public bool RelogCommandAttempted { get; set; }
        public int Epoch { get; set; } = 1;
        public DateTime EpochStartedAtUtc { get; set; }
        public DateTime? NextEpochEligibleUtc { get; set; }
        public DateTime? RelogAcceptedAtUtc { get; set; }
        public DadCharacterKey RelogSourceCharacterKey { get; set; } = new(string.Empty);
        public bool RelogTransitionObserved { get; set; }
        public DateTime? StableWrongCharacterSinceUtc { get; set; }
        public DadHomeWorldReturnGate HomeWorldReturnGate { get; set; } = new();
        public bool HomeWorldReturnStarted { get; set; }
    }
}
