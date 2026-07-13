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
    DadWakeTakeoverActionResult ExecuteCommand(DadWakeTakeoverCommand command, DadWakeTakeoverRequestDto request);
}

public sealed class DadWakeTakeoverService : IDisposable
{
    private static readonly TimeSpan OperationRetention = TimeSpan.FromHours(1);
    private static readonly TimeSpan RelogRetryInterval = TimeSpan.FromSeconds(5);
    private readonly IDadWakeTakeoverTarget target;
    private readonly Func<DateTime> utcNow;
    private readonly TimeSpan preCommitBudget;
    private readonly object gate = new();
    private readonly Dictionary<string, OperationState> operations = new(StringComparer.OrdinalIgnoreCase);
    private string activeOperationKey = string.Empty;
    private bool disposed;

    public DadWakeTakeoverService(
        IDadWakeTakeoverTarget target,
        Func<DateTime>? utcNow = null,
        TimeSpan? preCommitBudget = null)
    {
        this.target = target;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.preCommitBudget = preCommitBudget ?? TimeSpan.FromMinutes(20);
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
                    !IsTerminal(active.Phase))
                {
                    return Blocked(request, null, $"Stale or conflicting takeover token; active operation is {active.Request.OperationToken}.");
                }

                operation = new OperationState(CloneRequest(request), utcNow());
                operations[key] = operation;
                activeOperationKey = key;
            }

            if (!SameOperation(operation.Request, request))
                return Blocked(request, null, "Takeover token was reused with different target data.");

            AdvanceDueOperation(operation);
            return request.MessageKind switch
            {
                DadWakeTakeoverMessageKind.Cancel => Cancel(operation, "Takeover cancelled by coordinator."),
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

            var snapshot = Capture(operation);
            var blocker = ValidateTarget(snapshot, operation.Request);
            if (!string.IsNullOrWhiteSpace(blocker))
            {
                Block(operation, blocker, cleanup: true);
                return;
            }

            // VERMAXION marks itself busy at request time. This check also covers unreadable v1 status.
            if (snapshot.ExternalAutomationHeld || !snapshot.SuppressionReadable ||
                snapshot.AutoRetainerSuppressed && !snapshot.DadOwnsSuppression ||
                !snapshot.DadOwnsCharacterPostprocess)
            {
                operation.Summary = BuildLeaseYieldSummary(snapshot);
                operation.CleanupRetryAtNextBoundary = true;
                operation.CleanupReleaseReservation = false;
                operation.CleanupPending = !TryCleanupOwnedLeases(operation.Request.OperationToken, retryAtNextBoundary: true, releaseReservation: false);
                operation.UpdatedAtUtc = utcNow();
                return;
            }

            var acquire = Invoke(target.AcquireSuppression, "acquire DAD AutoRetainer suppression");
            snapshot = Capture(operation);
            if (!acquire.Success || !snapshot.DadOwnsSuppression || !snapshot.AutoRetainerSuppressed)
            {
                operation.Summary = acquire.Success
                    ? "DAD suppression ownership could not be verified; retrying at a future character boundary."
                    : $"{acquire.Error} Retrying at a future character boundary.";
                operation.CleanupRetryAtNextBoundary = true;
                operation.CleanupReleaseReservation = false;
                operation.CleanupPending = !TryCleanupOwnedLeases(operation.Request.OperationToken, retryAtNextBoundary: true, releaseReservation: false);
                operation.UpdatedAtUtc = utcNow();
                return;
            }

            operation.Phase = DadWakeTakeoverPhase.PostprocessOwned;
            operation.Summary = "AR handoff acquired — validating local preparation.";
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
            if (operation == null || operation.Phase > DadWakeTakeoverPhase.Prepared)
                return;

            var snapshot = Capture(operation, forceExternalRefresh: true);
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
                    operation.CleanupPending = !TryCleanupOwnedLeases(
                        operation.Request.OperationToken,
                        operation.CleanupRetryAtNextBoundary,
                        operation.CleanupReleaseReservation);
                if (operation.Phase == DadWakeTakeoverPhase.AwaitingArHook && operation.LastSnapshot.DadOwnsSuppression)
                    target.ReleaseSuppressionIfOwned();
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
            if (operation != null && operation.Phase < DadWakeTakeoverPhase.ResetCommitted)
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
                !operations.TryGetValue(activeOperationKey, out var operation) ||
                operation.Phase >= DadWakeTakeoverPhase.ResetCommitted || IsTerminal(operation.Phase))
                return;

            if (operation.Phase == DadWakeTakeoverPhase.AwaitingArHook &&
                !operation.LastSnapshot.DadOwnsCharacterPostprocess &&
                !operation.LastSnapshot.DadOwnsSuppression)
                return;

            operation.CleanupRetryAtNextBoundary = false;
            operation.CleanupReleaseReservation = false;
            operation.CleanupPending = !TryCleanupOwnedLeases(
                operation.Request.OperationToken,
                retryAtNextBoundary: false,
                releaseReservation: false);
            operation.Phase = DadWakeTakeoverPhase.AwaitingArHook;
            operation.VermaxionMutationAuthorization = DadVermaxionMutationAuthorization.None;
            operation.LastSnapshot.DadOwnsCharacterPostprocess = false;
            operation.LastSnapshot.DadOwnsSuppression = false;
            operation.LastSnapshot.AutoRetainerSuppressed = false;
            operation.Summary = "Coordinator disconnected; temporary DAD leases released while the logical order and VERMAXION reservation remain active.";
            operation.UpdatedAtUtc = utcNow();
        }
    }

    public DadWakeTakeoverStopAllResult StopAll(string reason)
    {
        lock (gate)
        {
            var result = new DadWakeTakeoverStopAllResult();
            foreach (var operation in operations.Values.Where(static operation => !IsTerminal(operation.Phase)))
            {
                if (operation.Phase >= DadWakeTakeoverPhase.ResetCommitted)
                {
                    result.PreservedCommittedCount++;
                    continue;
                }

                Cancel(operation, string.IsNullOrWhiteSpace(reason) ? "Stopped by DAD Stop-all." : reason);
                result.CancelledCount++;
            }

            result.CleanupPending = operations.Values.Any(static operation => operation.CleanupPending);
            result.Summary = result.PreservedCommittedCount > 0
                ? $"Cancelled {result.CancelledCount} pre-commit takeover(s); preserved {result.PreservedCommittedCount} committed takeover(s)."
                : $"Cancelled {result.CancelledCount} pre-commit takeover(s).";
            return result;
        }
    }

    private DadWakeTakeoverResultDto Prepare(OperationState operation)
    {
        if (IsTerminal(operation.Phase) || operation.Phase >= DadWakeTakeoverPhase.Prepared)
            return BuildResult(operation);

        var snapshot = Capture(operation);
        var blocker = ValidateCoreTarget(snapshot, operation.Request);
        if (!string.IsNullOrWhiteSpace(blocker))
            return Block(operation, blocker, cleanup: true);
        var reservation = target.ReserveVermaxion(operation.Request);
        snapshot = Capture(operation);
        if (reservation.IsRejected)
            return Block(operation, reservation.Summary, cleanup: true);
        if (!snapshot.AutoRetainerAvailable)
        {
            operation.Summary = string.IsNullOrWhiteSpace(snapshot.AutoRetainerStatus)
                ? "Waiting for readable AutoRetainer handoff state."
                : snapshot.AutoRetainerStatus;
            operation.UpdatedAtUtc = utcNow();
            return BuildResult(operation, snapshot);
        }
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
            operation.Summary = BuildExternalHoldSummary(snapshot);
            return BuildResult(operation, snapshot);
        }

        operation.ArBudgetStartedUtc ??= utcNow();

        if (operation.Phase == DadWakeTakeoverPhase.AwaitingArHook)
        {
            var armed = Invoke(
                () => target.ArmCharacterPostprocess(operation.Request.OperationToken),
                "arm the AutoRetainer character postprocess request");
            if (!armed.Success)
                return Block(operation, armed.Error, cleanup: true);
            operation.Summary = "Waiting for AutoRetainer character postprocess";
            operation.UpdatedAtUtc = utcNow();
            return BuildResult(operation, snapshot);
        }

        snapshot = Capture(operation);
        if (snapshot.ExternalAutomationHeld || !snapshot.SuppressionReadable ||
            !snapshot.DadOwnsCharacterPostprocess || !snapshot.DadOwnsSuppression ||
            !snapshot.AutoRetainerSuppressed)
        {
            operation.CleanupRetryAtNextBoundary = true;
            operation.CleanupReleaseReservation = false;
            operation.CleanupPending = !TryCleanupOwnedLeases(operation.Request.OperationToken, retryAtNextBoundary: true, releaseReservation: false);
            operation.Phase = DadWakeTakeoverPhase.AwaitingArHook;
            operation.Summary = BuildLeaseYieldSummary(snapshot);
            operation.UpdatedAtUtc = utcNow();
            return BuildResult(operation, snapshot);
        }

        operation.Phase = DadWakeTakeoverPhase.Prepared;
        operation.Summary = "AR handoff acquired — waiting for crew";
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

        if (!snapshot.AutoRetainerAvailable || snapshot.AutoRetainerBusy || snapshot.MultiModeEnabled)
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

        var acquire = Invoke(
            target.AcquireSuppression,
            authorization == DadVermaxionMutationAuthorization.Granted
                ? "acquire DAD AutoRetainer suppression after VERMAXION grant"
                : "acquire DAD AutoRetainer suppression from verified-idle compatibility evidence");
        snapshot = Capture(operation);
        if (!acquire.Success || !snapshot.DadOwnsSuppression || !snapshot.AutoRetainerSuppressed ||
            snapshot.AutoRetainerBusy || snapshot.MultiModeEnabled ||
            snapshot.VermaxionMutationAuthorization == DadVermaxionMutationAuthorization.None)
        {
            if (snapshot.DadOwnsSuppression)
                target.ReleaseSuppressionIfOwned();
            snapshot = Capture(operation);
            operation.Summary = acquire.Success
                ? authorization == DadVermaxionMutationAuthorization.Granted
                    ? "VERMAXION granted, but DAD could not verify suppression with AutoRetainer off/idle."
                    : BuildReservationHoldSummary(
                        snapshot.VermaxionReservationState,
                        snapshot.ExternalAutomationActivity,
                        snapshot.ExternalAutomationState,
                        snapshot.VermaxionReservationSummary)
                : acquire.Error;
            return BuildResult(operation, snapshot);
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
        if (request.CommitKind == DadWakeCommitKind.None || !request.ExecutionTimeUtc.HasValue)
            return Block(operation, "GO requires a commit kind and execution time.", cleanup: true);

        var execution = EnsureUtc(request.ExecutionTimeUtc.Value);
        if (request.CommitKind == DadWakeCommitKind.Reset)
        {
            if (operation.Phase > DadWakeTakeoverPhase.Prepared)
                return BuildResult(operation);
            if (operation.Phase != DadWakeTakeoverPhase.Prepared)
                return BuildResult(operation, summary: "Reset GO rejected until this client is Prepared.", acknowledgement: DadWakeAcknowledgementState.Rejected);
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
        if (operation.Phase == DadWakeTakeoverPhase.AwaitingArHook)
        {
            var snapshot = Capture(operation);
            if (snapshot.VermaxionMutationAuthorization != DadVermaxionMutationAuthorization.None)
                PrepareFromVermaxionAuthorization(operation, snapshot);
            else if (snapshot.VermaxionReservationAuthoritative)
            {
                operation.Summary = BuildReservationHoldSummary(
                    snapshot.VermaxionReservationState,
                    snapshot.ExternalAutomationActivity,
                    snapshot.ExternalAutomationState,
                    snapshot.VermaxionReservationSummary);
                operation.UpdatedAtUtc = utcNow();
            }
        }

        if (operation.Phase == DadWakeTakeoverPhase.Prepared &&
            operation.VermaxionMutationAuthorization != DadVermaxionMutationAuthorization.None)
        {
            var snapshot = Capture(operation);
            if (!IsVerifiedReservationPreparation(snapshot))
            {
                ReturnToReservationWait(operation, snapshot);
            }
            else if (operation.VermaxionMutationAuthorization != snapshot.VermaxionMutationAuthorization)
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
        var snapshot = Capture(operation, forceExternalRefresh: true);
        var blocker = operation.VermaxionMutationAuthorization == DadVermaxionMutationAuthorization.None
            ? ValidateTarget(snapshot, operation.Request)
            : ValidateCoreTarget(snapshot, operation.Request);
        if (operation.VermaxionMutationAuthorization != DadVermaxionMutationAuthorization.None &&
            !string.IsNullOrWhiteSpace(blocker))
        {
            Block(operation, blocker, cleanup: true);
            return;
        }
        if (operation.VermaxionMutationAuthorization != DadVermaxionMutationAuthorization.None)
        {
            if (!snapshot.AutoRetainerAvailable ||
                !IsVerifiedReservationPreparation(snapshot))
            {
                ReturnToReservationWait(operation, snapshot);
                return;
            }

            operation.VermaxionMutationAuthorization = snapshot.VermaxionMutationAuthorization;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(blocker) && snapshot.ExternalAutomationHeld)
                blocker = "VERMAXION became busy before the coordinated reset boundary.";
            if (string.IsNullOrWhiteSpace(blocker) &&
                (!snapshot.DadOwnsCharacterPostprocess ||
                 !snapshot.DadOwnsSuppression || !snapshot.AutoRetainerSuppressed))
            {
                blocker = "DAD no longer owns its character-postprocess suppression mutation lease.";
            }
        }
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            Block(operation, blocker, cleanup: true);
            return;
        }

        var disableMulti = Invoke(() => target.SetMultiModeEnabled(false), "disable AutoRetainer Multi Mode");
        var disableAr = disableMulti.Success
            ? Invoke(() => target.ExecuteCommand(DadWakeTakeoverCommand.DisableAutoRetainer, operation.Request), "send /ays d")
            : disableMulti;
        var reset = disableAr.Success
            ? Invoke(() => target.ExecuteCommand(DadWakeTakeoverCommand.ResetAutoRetainer, operation.Request), "send /ays reset")
            : disableAr;
        if (!reset.Success)
        {
            Block(operation, reset.Error, cleanup: true);
            return;
        }

        operation.ResetIssuedUtc = utcNow();
        operation.Phase = DadWakeTakeoverPhase.ResetVerified;
        operation.Acknowledgement = DadWakeAcknowledgementState.Executed;
        operation.Summary = "Coordinated reset executed and verified; waiting for crew reset barrier.";
        operation.UpdatedAtUtc = operation.ResetIssuedUtc.Value;
        // /ays reset aborts AR's queued natural relog. The derived suppression lease remains ours.
        target.FinishCharacterPostprocess(retryAtNextBoundary: false);
    }

    private void ExecuteRelog(OperationState operation)
    {
        var snapshot = Capture(operation);
        var blocker = ValidateTarget(snapshot, operation.Request);
        if (string.IsNullOrWhiteSpace(blocker) && (!snapshot.DadOwnsSuppression || !snapshot.AutoRetainerSuppressed))
            blocker = "DAD no longer owns its derived pre-AutoRetainer suppression lease.";
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            Block(operation, blocker, cleanup: true);
            return;
        }

        if (!snapshot.CorrectCharacter)
        {
            var relog = Invoke(
                () => target.ExecuteCommand(DadWakeTakeoverCommand.RelogCharacter, operation.Request),
                $"send /ays relog {operation.Request.CharacterKey}");
            if (!relog.Success)
            {
                Block(operation, relog.Error, cleanup: true);
                return;
            }
            var issuedAt = utcNow();
            operation.RelogIssuedUtc ??= issuedAt;
            operation.LastRelogAttemptUtc = issuedAt;
            operation.RelogAttemptCount++;
        }

        operation.Phase = DadWakeTakeoverPhase.WaitingForCharacter;
        operation.Acknowledgement = DadWakeAcknowledgementState.Executed;
        operation.Summary = snapshot.CorrectCharacter
            ? "Correct character required no relog; verifying the pre-AR destination gate."
            : $"Relog issued for {operation.Request.CharacterKey}; retaining DAD suppression through login.";
        operation.UpdatedAtUtc = utcNow();
        VerifyDestination(operation);
    }

    private void VerifyDestination(OperationState operation)
    {
        var snapshot = Capture(operation);
        if (!snapshot.CorrectCharacter)
        {
            if (!TryRetryRelog(operation, snapshot))
                operation.Summary = BuildRelogWaitSummary(operation, snapshot);
            return;
        }

        if (!snapshot.PostArReady || snapshot.MultiModeEnabled ||
            !snapshot.DadOwnsSuppression || !snapshot.AutoRetainerSuppressed)
        {
            operation.Summary = $"Waiting for {operation.Request.CharacterKey} world-ready stability with AutoRetainer off.";
            return;
        }

        if (!target.ReleaseSuppressionIfOwned())
        {
            operation.Summary = "Destination verified; waiting for DAD suppression release verification.";
            return;
        }

        operation.Phase = DadWakeTakeoverPhase.Ready;
        operation.ReadyUtc = utcNow();
        operation.Acknowledgement = DadWakeAcknowledgementState.Executed;
        operation.Summary = $"{operation.Request.CharacterKey} is ready after the post-AR gate.";
        operation.UpdatedAtUtc = operation.ReadyUtc.Value;
        target.ReleaseVermaxion(operation.Request.OperationToken);
        if (string.Equals(activeOperationKey, DadWakePolicyRules.BuildOperationKey(operation.Request), StringComparison.OrdinalIgnoreCase))
            activeOperationKey = string.Empty;
    }

    private bool TryRetryRelog(OperationState operation, DadWakeTakeoverTargetSnapshot snapshot)
    {
        if (!operation.LastRelogAttemptUtc.HasValue ||
            utcNow() - operation.LastRelogAttemptUtc.Value < RelogRetryInterval ||
            !snapshot.Participant.IsAvailable || !snapshot.Participant.WorldReadyStable ||
            snapshot.AutoRetainerBusy || !snapshot.LifestreamAvailable || snapshot.LifestreamBusy ||
            snapshot.MultiModeEnabled || !snapshot.DadOwnsSuppression || !snapshot.AutoRetainerSuppressed)
        {
            return false;
        }

        var attemptedAt = utcNow();
        operation.LastRelogAttemptUtc = attemptedAt;
        operation.RelogAttemptCount++;
        var relog = Invoke(
            () => target.ExecuteCommand(DadWakeTakeoverCommand.RelogCharacter, operation.Request),
            $"retry /ays relog {operation.Request.CharacterKey}");
        operation.Summary = relog.Success
            ? $"Relog attempt {operation.RelogAttemptCount} issued for {operation.Request.CharacterKey}; retaining DAD suppression through login."
            : $"Relog attempt {operation.RelogAttemptCount} was rejected: {relog.Error} Retrying after the next safe interval.";
        operation.UpdatedAtUtc = attemptedAt;
        return true;
    }

    private static string BuildRelogWaitSummary(OperationState operation, DadWakeTakeoverTargetSnapshot snapshot)
    {
        if (!snapshot.Participant.IsAvailable)
            return $"Relog attempt {operation.RelogAttemptCount} started a login transition; waiting for a local character.";
        if (!snapshot.Participant.WorldReadyStable)
            return $"Relog attempt {operation.RelogAttemptCount} started a world/login transition; waiting for stability.";
        if (snapshot.AutoRetainerBusy)
            return $"Relog attempt {operation.RelogAttemptCount} is in progress; AutoRetainer is busy.";
        if (!snapshot.LifestreamAvailable || snapshot.LifestreamBusy)
            return $"Relog attempt {operation.RelogAttemptCount} is in progress; Lifestream is busy or unavailable.";
        if (snapshot.MultiModeEnabled || !snapshot.DadOwnsSuppression || !snapshot.AutoRetainerSuppressed)
            return $"Waiting to retry relog with Multi Mode off and DAD suppression ownership verified.";
        return $"Waiting after relog attempt {operation.RelogAttemptCount} for {operation.Request.CharacterKey}.";
    }

    private DadWakeTakeoverResultDto Cancel(OperationState operation, string reason)
    {
        if (operation.Phase >= DadWakeTakeoverPhase.ResetCommitted && !IsTerminal(operation.Phase))
            return BuildResult(operation, summary: "Committed takeover continues locally; cancellation cannot invalidate its GO boundary.");
        operation.CleanupRetryAtNextBoundary = false;
        operation.CleanupReleaseReservation = true;
        operation.CleanupPending = !TryCleanupOwnedLeases(operation.Request.OperationToken, retryAtNextBoundary: false);
        operation.Phase = DadWakeTakeoverPhase.Cancelled;
        operation.Status = DadWakeTakeoverStatus.Blocked;
        operation.Acknowledgement = DadWakeAcknowledgementState.Executed;
        operation.BlockedReason = string.IsNullOrWhiteSpace(reason) ? "Takeover cancelled." : reason;
        operation.Summary = operation.BlockedReason;
        operation.UpdatedAtUtc = utcNow();
        activeOperationKey = string.Empty;
        return BuildResult(operation);
    }

    private DadWakeTakeoverResultDto Block(OperationState operation, string reason, bool cleanup)
    {
        if (cleanup)
        {
            operation.CleanupRetryAtNextBoundary = false;
            operation.CleanupReleaseReservation = true;
            operation.CleanupPending = !TryCleanupOwnedLeases(operation.Request.OperationToken, retryAtNextBoundary: false);
        }
        operation.Phase = DadWakeTakeoverPhase.Blocked;
        operation.Status = DadWakeTakeoverStatus.Blocked;
        operation.Acknowledgement = DadWakeAcknowledgementState.Rejected;
        operation.BlockedReason = string.IsNullOrWhiteSpace(reason) ? "Wake takeover blocked." : reason;
        operation.Summary = operation.BlockedReason;
        operation.UpdatedAtUtc = utcNow();
        activeOperationKey = string.Empty;
        return BuildResult(operation);
    }

    private bool TryCleanupOwnedLeases(string operationToken, bool retryAtNextBoundary, bool releaseReservation = true)
    {
        var postprocessFinished = target.FinishCharacterPostprocess(retryAtNextBoundary);
        var suppressionReleased = target.ReleaseSuppressionIfOwned();
        var reservationReleased = !releaseReservation || target.ReleaseVermaxion(operationToken);
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
        target.ReleaseSuppressionIfOwned();
        snapshot = Capture(operation, forceExternalRefresh: true);
        operation.VermaxionMutationAuthorization = DadVermaxionMutationAuthorization.None;
        operation.Phase = DadWakeTakeoverPhase.AwaitingArHook;
        operation.CommitKind = DadWakeCommitKind.None;
        operation.ExecutionTimeUtc = null;
        operation.Acknowledgement = DadWakeAcknowledgementState.Pending;
        operation.Summary = snapshot.VermaxionMutationAuthorization == DadVermaxionMutationAuthorization.Granted
            ? "VERMAXION grant remains current; waiting for AutoRetainer idle, Multi Mode off, and DAD suppression reacquisition."
            : snapshot.VermaxionMutationAuthorization == DadVermaxionMutationAuthorization.CompatibilityIdle
                ? "Compatibility handoff evidence remains idle; waiting to reacquire and verify DAD suppression."
                : BuildReservationHoldSummary(
                    snapshot.VermaxionReservationState,
                    snapshot.ExternalAutomationActivity,
                    snapshot.ExternalAutomationState,
                    snapshot.VermaxionReservationSummary);
        operation.UpdatedAtUtc = utcNow();
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
            ? "Compatibility handoff: VERMAXION idle / AR idle — DAD suppression acquired; waiting for crew"
            : "VERMAXION handoff granted — AR off/idle and DAD suppression acquired; waiting for crew";

    private static string ValidateTarget(DadWakeTakeoverTargetSnapshot snapshot, DadWakeTakeoverRequestDto request)
    {
        var blocker = ValidateCoreTarget(snapshot, request);
        if (!string.IsNullOrWhiteSpace(blocker))
            return blocker;
        if (!snapshot.AutoRetainerAvailable)
            return string.IsNullOrWhiteSpace(snapshot.AutoRetainerStatus)
                ? "AutoRetainer handoff IPC is unavailable."
                : snapshot.AutoRetainerStatus;
        return string.Empty;
    }

    private static string ValidateCoreTarget(DadWakeTakeoverTargetSnapshot snapshot, DadWakeTakeoverRequestDto request)
    {
        if (!snapshot.DadEnabled)
            return "DAD is disabled on the target client.";
        if (!snapshot.RemoteMutationAllowed)
            return "Remote mutation is disabled on the target client.";
        if (!snapshot.AccountMatches)
            return $"Target DAD client does not own requested account {request.AccountKey}.";
        if (!snapshot.CharacterKnownToAccount)
            return $"Character {request.CharacterKey} is not known to account {request.AccountKey}.";
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
        return $"Waiting for VERMAXION reservation — {reservationState}{activityText}. {summary}".Trim();
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
            Stage = snapshot.ExternalAutomationHeld && phase == DadWakeTakeoverPhase.AwaitingArHook
                ? DadWakeTakeoverStage.WaitingForExternalAutomation
                : ToStage(phase),
            Status = status,
            CommitKind = operation.CommitKind,
            ExecutionTimeUtc = operation.ExecutionTimeUtc,
            AcknowledgementState = acknowledgement ?? operation.Acknowledgement,
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
        foreach (var key in operations.Where(pair => IsTerminal(pair.Value.Phase) && pair.Value.UpdatedAtUtc < cutoff)
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
            var operationToken = operations.Values.FirstOrDefault(static operation => !IsTerminal(operation.Phase))?.Request.OperationToken
                                 ?? string.Empty;
            TryCleanupOwnedLeases(operationToken, retryAtNextBoundary: false);
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
        }

        public DadWakeTakeoverRequestDto Request { get; }
        public DateTime CreatedAtUtc { get; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime? ArBudgetStartedUtc { get; set; }
        public DadWakeTakeoverPhase Phase { get; set; } = DadWakeTakeoverPhase.AwaitingArHook;
        public DadWakeTakeoverStatus Status { get; set; } = DadWakeTakeoverStatus.Pending;
        public DadWakeCommitKind CommitKind { get; set; }
        public DateTime? ExecutionTimeUtc { get; set; }
        public DadWakeAcknowledgementState Acknowledgement { get; set; } = DadWakeAcknowledgementState.Pending;
        public DateTime? ResetIssuedUtc { get; set; }
        public DateTime? RelogIssuedUtc { get; set; }
        public DateTime? LastRelogAttemptUtc { get; set; }
        public int RelogAttemptCount { get; set; }
        public DateTime? ReadyUtc { get; set; }
        public string Summary { get; set; } = "Waiting for AutoRetainer character postprocess";
        public string BlockedReason { get; set; } = string.Empty;
        public DadWakeTakeoverTargetSnapshot LastSnapshot { get; set; } = new();
        public bool CleanupPending { get; set; }
        public bool CleanupRetryAtNextBoundary { get; set; }
        public bool CleanupReleaseReservation { get; set; } = true;
        public DadVermaxionMutationAuthorization VermaxionMutationAuthorization { get; set; }
    }
}
