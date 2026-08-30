using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadShoppingRuntimeService
{
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StatusUnreadableGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PostCommandTimeout = TimeSpan.FromMinutes(20);
    private const int MaximumOperationIdLength = 128;
    private const string AutoRetainerDeliveryCommand = "/ays deliver";

    private enum RuntimePhase
    {
        Idle,
        Starting,
        Polling,
        Cancelling,
        DispatchingPostCommand,
        WaitingForRelease,
        Complete,
        Failed,
    }

    private readonly DadDutySupportAdsService adsService;
    private readonly DadPresenceService presenceService;
    private readonly IPluginLog log;
    private readonly List<DadShoppingRunResult> results = [];
    private readonly Queue<string> postCommands = new();
    private List<DadShoppingRunAssociation> associations = [];
    private RuntimePhase phase;
    private int associationIndex;
    private string runId = string.Empty;
    private string attemptToken = string.Empty;
    private int moduleIndex;
    private string operationId = string.Empty;
    private DateTime operationStartedAtUtc = DateTime.MinValue;
    private DateTime nextStatusPollUtc = DateTime.MinValue;
    private DateTime statusUnreadableSinceUtc = DateTime.MinValue;
    private DateTime postCommandDispatchedAtUtc = DateTime.MinValue;
    private DateTime postCommandActivityObservedAtUtc = DateTime.MinValue;
    private bool observedPostCommandUnsafe;
    private bool postCommandRequiresBusyProof;
    private bool cancellationAttempted;
    private string cancellationFailureCode = string.Empty;
    private string cancellationSummary = string.Empty;
    private string startupBlocker = string.Empty;
    private DadShoppingRunResult? currentResult;
    private DadAdsShoppingStartOutcome currentStartOutcome;
    private List<string> lastCompletedRowIds = [];
    private List<DadAdsShopListRowStatus> lastRows = [];

    public DadShoppingRuntimeService(
        DadDutySupportAdsService adsService,
        DadPresenceService presenceService,
        IPluginLog log)
    {
        this.adsService = adsService;
        this.presenceService = presenceService;
        this.log = log;
    }

    public bool IsRequired => associations.Count > 0;

    public bool IsCancellationPending => phase == RuntimePhase.Cancelling;

    public IReadOnlyList<DadShoppingRunResult> Results
        => results.Select(static result => result.Clone()).ToList();

    public void Begin(
        DadRunRequest request,
        int activeModuleIndex,
        DadParticipantSnapshot localAssignment,
        DateTime nowUtc)
    {
        Reset();
        runId = request.RequestId?.Trim() ?? string.Empty;
        attemptToken = Guid.NewGuid().ToString("N");
        moduleIndex = Math.Max(0, activeModuleIndex);
        associations = DadShoppingAssociationRules.NormalizeRunAssociations(request.ShoppingAssociations)
            .Where(association => DadShoppingAssociationRules.MatchesLocalShopper(association, localAssignment))
            .Select(static association => association.Clone())
            .ToList();
        phase = associations.Count == 0 ? RuntimePhase.Complete : RuntimePhase.Starting;

        foreach (var association in associations)
        {
            if (string.IsNullOrWhiteSpace(association.CustomCommand))
                continue;
            if (DadCompletionCommandRules.TryNormalizeCustomCommand(
                    association.CustomCommand,
                    out var normalized,
                    out var failure))
            {
                association.CustomCommand = normalized;
                continue;
            }

            startupBlocker = failure;
            phase = RuntimePhase.Failed;
            results.Add(BuildFailure(
                association,
                BuildOperationId(association),
                "dad-shopping-custom-command-invalid",
                failure));
            break;
        }

        if (associations.Count > 0)
        {
            log.Information(
                "[dad][shopping] Frozen {Count} local association(s) for run={RunId} module={ModuleIndex} shopperSlot={SlotId} character={CharacterKey}.",
                associations.Count,
                runId,
                moduleIndex,
                localAssignment.AssignedSlotId,
                localAssignment.ActiveCharacterKey.Value);
        }
    }

    public DadShoppingRuntimeDecision Update(DateTime nowUtc)
    {
        if (phase == RuntimePhase.Complete || associations.Count == 0)
            return Ready("No due local shopping association is blocking prequeue preparation.");
        if (phase == RuntimePhase.Failed)
            return Reject(string.IsNullOrWhiteSpace(startupBlocker)
                ? currentResult?.FailureMessage ?? "ADS shopping failed."
                : startupBlocker);

        var now = EnsureUtc(nowUtc);
        return phase switch
        {
            RuntimePhase.Starting => StartCurrent(now),
            RuntimePhase.Polling => PollCurrent(now),
            RuntimePhase.Cancelling => PollCancellation(now),
            RuntimePhase.DispatchingPostCommand => DispatchPostCommand(),
            RuntimePhase.WaitingForRelease => WaitForRelease(now),
            _ => Wait("Preparing ADS shopping."),
        };
    }

    public void CancelActive(string reason)
    {
        var summary = string.IsNullOrWhiteSpace(reason) ? "ADS shopping was cancelled." : reason.Trim();
        BeginCancellation(DateTime.UtcNow, "dad-shopping-cancelled", summary);
    }

    private void BeginCancellation(DateTime now, string failureCode, string summary)
    {
        if (phase != RuntimePhase.Polling || string.IsNullOrWhiteSpace(operationId) || cancellationAttempted)
            return;
        cancellationAttempted = true;
        var cancelled = adsService.CancelShopListPreset(operationId, out var failure);
        if (cancelled)
        {
            log.Information(
                "[dad][shopping] Exact cancellation acknowledged operation={OperationId}: {Reason}",
                operationId,
                summary);
        }
        else
        {
            log.Warning(
                "[dad][shopping] Exact cancellation was not acknowledged operation={OperationId}: {Failure}; polling the exact retained operation before publishing terminal state.",
                operationId,
                failure);
        }

        cancellationFailureCode = cancelled ? failureCode : "dad-shopping-cancel-failed";
        cancellationSummary = cancelled
            ? summary
            : $"ADS shopping cancellation was not acknowledged: {failure}";
        nextStatusPollUtc = EnsureUtc(now);
        phase = RuntimePhase.Cancelling;
    }

    public void Reset()
    {
        associations = [];
        results.Clear();
        postCommands.Clear();
        phase = RuntimePhase.Idle;
        associationIndex = 0;
        runId = string.Empty;
        attemptToken = string.Empty;
        moduleIndex = 0;
        operationId = string.Empty;
        operationStartedAtUtc = DateTime.MinValue;
        nextStatusPollUtc = DateTime.MinValue;
        statusUnreadableSinceUtc = DateTime.MinValue;
        postCommandDispatchedAtUtc = DateTime.MinValue;
        postCommandActivityObservedAtUtc = DateTime.MinValue;
        observedPostCommandUnsafe = false;
        postCommandRequiresBusyProof = false;
        cancellationAttempted = false;
        cancellationFailureCode = string.Empty;
        cancellationSummary = string.Empty;
        startupBlocker = string.Empty;
        currentResult = null;
        currentStartOutcome = DadAdsShoppingStartOutcome.Rejected;
        lastCompletedRowIds = [];
        lastRows = [];
    }

    private DadShoppingRuntimeDecision StartCurrent(DateTime now)
    {
        if (associationIndex >= associations.Count)
        {
            phase = RuntimePhase.Complete;
            return Ready("ADS shopping evaluation completed for this duty boundary.");
        }

        var association = associations[associationIndex];
        operationId = BuildOperationId(association);
        if (operationId.Length > MaximumOperationIdLength)
        {
            return FailCurrent(
                association,
                "dad-shopping-operation-id-too-long",
                $"Exact ADS shopping operation ID exceeds {MaximumOperationIdLength} characters.",
                completedRowIds: [],
                rows: []);
        }
        operationStartedAtUtc = now;
        nextStatusPollUtc = now;
        statusUnreadableSinceUtc = DateTime.MinValue;
        cancellationAttempted = false;
        cancellationFailureCode = string.Empty;
        cancellationSummary = string.Empty;
        lastCompletedRowIds = [];
        lastRows = [];
        var start = adsService.StartShopListPreset(new DadAdsShopListPresetRequest
        {
            Version = 1,
            OperationId = operationId,
            PresetId = association.PresetId,
            CompletedRowIds = [..association.CompletedNonRepeatableRowIds],
        });
        log.Information(
            "[dad][shopping] Start boundary run={RunId} module={ModuleIndex} owner={OwnerKind}:{OwnerId} preset={PresetId} operation={OperationId} outcome={Outcome}.",
            runId,
            moduleIndex,
            association.OwnerKind,
            association.OwnerId,
            association.PresetId,
            operationId,
            start.Outcome);
        currentStartOutcome = start.Outcome;
        MergeEvidence(start.Response?.CompletedNonRepeatableRowIds, null);

        switch (start.Outcome)
        {
            case DadAdsShoppingStartOutcome.Accepted:
            case DadAdsShoppingStartOutcome.Uncertain:
            case DadAdsShoppingStartOutcome.Fulfilled:
            case DadAdsShoppingStartOutcome.NotTriggered:
                phase = RuntimePhase.Polling;
                return Wait(start.Summary);
            default:
                return FailCurrent(
                    association,
                    "dad-shopping-start-rejected",
                    start.Summary,
                    completedRowIds: lastCompletedRowIds,
                    rows: lastRows);
        }
    }

    private DadShoppingRuntimeDecision PollCurrent(DateTime now)
    {
        var association = associations[associationIndex];
        if (now - operationStartedAtUtc >= OperationTimeout)
        {
            const string timeoutSummary = "ADS shopping did not reach correlated terminal status within 15 minutes.";
            BeginCancellation(now, "dad-shopping-operation-timeout", timeoutSummary);
            return IsCancellationPending ? Wait(timeoutSummary) : Reject(currentResult?.Summary ?? timeoutSummary);
        }
        if (now < nextStatusPollUtc)
            return Wait("Waiting for correlated ADS shopping status.");

        nextStatusPollUtc = now + StatusPollInterval;
        var statusResult = adsService.GetShopListPresetStatus(operationId, association.PresetId);
        if (!statusResult.Readable || statusResult.Response == null)
        {
            if (statusUnreadableSinceUtc == DateTime.MinValue)
                statusUnreadableSinceUtc = now;
            if (now - statusUnreadableSinceUtc < StatusUnreadableGrace)
                return Wait(statusResult.Summary);

            BeginCancellation(now, "dad-shopping-status-unreadable", statusResult.Summary);
            return IsCancellationPending ? Wait(statusResult.Summary) : Reject(currentResult?.Summary ?? statusResult.Summary);
        }

        statusUnreadableSinceUtc = DateTime.MinValue;
        var status = statusResult.Response;
        MergeEvidence(status.CompletedNonRepeatableRowIds, status.Rows);
        if (!status.Done)
            return Wait(string.IsNullOrWhiteSpace(status.StatusMessage)
                ? "ADS shopping is running."
                : status.StatusMessage);

        List<string> completed = [..lastCompletedRowIds];
        var rows = lastRows.Select(static row => row.Clone()).ToList();
        if (status.Succeeded != true)
        {
            var failure = string.IsNullOrWhiteSpace(status.FailureMessage)
                ? string.IsNullOrWhiteSpace(status.StatusMessage)
                    ? "ADS shopping reached terminal failure."
                    : status.StatusMessage
                : status.FailureMessage;
            return FailCurrent(
                association,
                string.IsNullOrWhiteSpace(status.FailureCode) ? "dad-shopping-ads-failed" : status.FailureCode,
                failure,
                completed,
                rows);
        }

        var disposition = (status.Disposition ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(disposition))
        {
            disposition = currentStartOutcome switch
            {
                DadAdsShoppingStartOutcome.NotTriggered => "not-triggered",
                DadAdsShoppingStartOutcome.Fulfilled => "fulfilled",
                DadAdsShoppingStartOutcome.Accepted => "succeeded",
                _ => string.Empty,
            };
        }
        if (string.IsNullOrWhiteSpace(disposition))
        {
            return FailCurrent(
                association,
                "dad-shopping-status-disposition-missing",
                "ADS terminal shopping status omitted the disposition needed to decide post-actions safely.",
                completed,
                rows);
        }

        var nonRepeatableRows = rows.Where(static row => !row.Repeatable).ToList();
        var fulfilled = nonRepeatableRows.Count > 0 && nonRepeatableRows.All(row =>
            completed.Contains(row.RowId, StringComparer.Ordinal));
        currentResult = new DadShoppingRunResult
        {
            RunId = runId,
            ModuleIndex = moduleIndex,
            OperationId = operationId,
            Association = association.Clone(),
            Succeeded = true,
            NonRepeatableRowsFulfilled = fulfilled,
            Disposition = disposition,
            CompletedNonRepeatableRowIds = completed,
            Rows = rows,
            Summary = string.IsNullOrWhiteSpace(status.StatusMessage)
                ? "ADS shopping completed successfully."
                : status.StatusMessage,
        };
        results.Add(currentResult);
        if (string.Equals(disposition, "not-triggered", StringComparison.Ordinal))
            return AdvanceAssociation();
        var verifiedPurchase = rows.Any(static row =>
            row.PurchasedQuantity > 0 &&
            string.Equals(row.Outcome, "purchased", StringComparison.Ordinal));
        var newlyCompletedNonRepeatableRow = completed.Any(rowId =>
            !association.CompletedNonRepeatableRowIds.Contains(rowId, StringComparer.Ordinal));
        if (!verifiedPurchase && !newlyCompletedNonRepeatableRow)
            return AdvanceAssociation();
        BuildPostCommands(association);
        if (postCommands.Count == 0)
            return AdvanceAssociation();

        phase = RuntimePhase.DispatchingPostCommand;
        return Wait("ADS shopping succeeded; preparing the configured post-fulfillment action.");
    }

    private DadShoppingRuntimeDecision PollCancellation(DateTime now)
    {
        if (now < nextStatusPollUtc)
            return Wait("Waiting for correlated terminal ADS cancellation status.");

        nextStatusPollUtc = now + StatusPollInterval;
        var association = associations[associationIndex];
        var statusResult = adsService.GetShopListPresetStatus(operationId, association.PresetId);
        if (statusResult.Readable && statusResult.Response != null)
        {
            MergeEvidence(
                statusResult.Response.CompletedNonRepeatableRowIds,
                statusResult.Response.Rows);
            if (statusResult.Response.Done)
            {
                CompleteCancellationFailure(cancellationFailureCode, cancellationSummary);
                return Reject(cancellationSummary);
            }
        }

        var readableStatus = statusResult.Response?.StatusMessage;
        return Wait(statusResult.Readable
            ? string.IsNullOrWhiteSpace(readableStatus)
                ? "ADS shopping cancellation is still settling."
                : readableStatus
            : statusResult.Summary);
    }

    private void CompleteCancellationFailure(string failureCode, string summary)
    {
        var association = associations[associationIndex];
        currentResult = BuildFailure(association, operationId, failureCode, summary);
        currentResult.CompletedNonRepeatableRowIds = [..lastCompletedRowIds];
        currentResult.Rows = lastRows.Select(static row => row.Clone()).ToList();
        results.Add(currentResult);
        phase = RuntimePhase.Failed;
        startupBlocker = summary;
    }

    private DadShoppingRuntimeDecision DispatchPostCommand()
    {
        if (postCommands.Count == 0)
            return AdvanceAssociation();

        var command = postCommands.Dequeue();
        try
        {
            if (!Plugin.CommandManager.ProcessCommand(command))
            {
                return FailPostAction(
                    "dad-shopping-post-command-unregistered",
                    "Post-fulfillment command was not accepted by Dalamud's registered-plugin command manager.");
            }
        }
        catch (Exception ex)
        {
            return FailPostAction(
                "dad-shopping-post-command-failed",
                $"Post-fulfillment command failed: {ex.Message}");
        }

        postCommandDispatchedAtUtc = DateTime.UtcNow;
        postCommandActivityObservedAtUtc = DateTime.MinValue;
        observedPostCommandUnsafe = false;
        postCommandRequiresBusyProof = string.Equals(command, AutoRetainerDeliveryCommand, StringComparison.OrdinalIgnoreCase);
        phase = RuntimePhase.WaitingForRelease;
        log.Information(
            "[dad][shopping] Submitted one registered post-fulfillment command operation={OperationId} commandRoot={CommandRoot}.",
            operationId,
            command.Split(' ', 2)[0]);
        return Wait("Post-fulfillment command submitted once; waiting for the exact shopper to be released and world-safe.");
    }

    private DadShoppingRuntimeDecision WaitForRelease(DateTime now)
    {
        if (now - postCommandDispatchedAtUtc >= PostCommandTimeout)
        {
            return FailPostAction(
                "dad-shopping-post-command-release-timeout",
                "The exact shopper did not return to released, world-safe state within 20 minutes.");
        }

        var live = presenceService.BuildLiveSafetySnapshot();
        var safe = live.IsAvailable &&
                   live.IsEligibleForRun &&
                   live.WorldReadyStable &&
                   live.PostArReady &&
                   !live.AutoRetainerBusy &&
                   !live.ExternalAutomationHeld;
        var matchingExternalReceipt = live.ExternalAutomationHeld &&
                                       (live.ExternalAutomationActivity.Contains("retainer", StringComparison.OrdinalIgnoreCase) ||
                                        live.ExternalAutomationActivity.Contains("deliver", StringComparison.OrdinalIgnoreCase));
        var freshAfterDispatch = live.LastHeartbeatUtc > postCommandDispatchedAtUtc;
        if (postCommandRequiresBusyProof &&
            freshAfterDispatch &&
            (live.AutoRetainerBusy || matchingExternalReceipt))
        {
            observedPostCommandUnsafe = true;
            if (live.LastHeartbeatUtc > postCommandActivityObservedAtUtc)
                postCommandActivityObservedAtUtc = live.LastHeartbeatUtc;
        }
        var releaseObserved = safe &&
                              freshAfterDispatch &&
                              (!postCommandRequiresBusyProof ||
                               observedPostCommandUnsafe &&
                               live.LastHeartbeatUtc > postCommandActivityObservedAtUtc);
        if (!releaseObserved)
        {
            return Wait(postCommandRequiresBusyProof
                ? "Waiting for fresh AutoRetainer activity evidence, then a later released world-safe shopper heartbeat."
                : "Waiting for a fresh post-command released world-safe shopper heartbeat.");
        }

        phase = postCommands.Count > 0
            ? RuntimePhase.DispatchingPostCommand
            : RuntimePhase.Starting;
        return postCommands.Count > 0
            ? Wait("The shopper is released and safe; preparing the next configured post-fulfillment command.")
            : AdvanceAssociation();
    }

    private void BuildPostCommands(DadShoppingRunAssociation association)
    {
        postCommands.Clear();
        if (association.RunAutoRetainerDelivery)
            postCommands.Enqueue(AutoRetainerDeliveryCommand);
        if (!string.IsNullOrWhiteSpace(association.CustomCommand))
            postCommands.Enqueue(association.CustomCommand);
    }

    private DadShoppingRuntimeDecision AdvanceAssociation()
    {
        associationIndex++;
        operationId = string.Empty;
        currentResult = null;
        postCommands.Clear();
        postCommandDispatchedAtUtc = DateTime.MinValue;
        postCommandActivityObservedAtUtc = DateTime.MinValue;
        observedPostCommandUnsafe = false;
        postCommandRequiresBusyProof = false;
        lastCompletedRowIds = [];
        lastRows = [];
        if (associationIndex >= associations.Count)
        {
            phase = RuntimePhase.Complete;
            return Ready("ADS shopping evaluation completed for this duty boundary.");
        }
        phase = RuntimePhase.Starting;
        return Wait("Preparing the next frozen shopping association.");
    }

    private DadShoppingRuntimeDecision FailCurrent(
        DadShoppingRunAssociation association,
        string failureCode,
        string summary,
        IReadOnlyCollection<string> completedRowIds,
        IReadOnlyCollection<DadAdsShopListRowStatus> rows)
    {
        currentResult = BuildFailure(association, operationId, failureCode, summary);
        currentResult.CompletedNonRepeatableRowIds = [..completedRowIds];
        currentResult.Rows = rows.Select(static row => row.Clone()).ToList();
        results.Add(currentResult);
        phase = RuntimePhase.Failed;
        startupBlocker = summary;
        return Reject(summary);
    }

    private DadShoppingRuntimeDecision FailPostAction(string failureCode, string summary)
    {
        if (currentResult == null)
        {
            return FailCurrent(
                associations[associationIndex],
                failureCode,
                summary,
                completedRowIds: [],
                rows: []);
        }

        currentResult.Succeeded = false;
        currentResult.FailureCode = failureCode;
        currentResult.FailureMessage = summary;
        currentResult.Summary = summary;
        phase = RuntimePhase.Failed;
        startupBlocker = summary;
        return Reject(summary);
    }

    private DadShoppingRunResult BuildFailure(
        DadShoppingRunAssociation association,
        string exactOperationId,
        string failureCode,
        string summary)
        => new()
        {
            RunId = runId,
            ModuleIndex = moduleIndex,
            OperationId = exactOperationId,
            Association = association.Clone(),
            Succeeded = false,
            Disposition = "failed",
            FailureCode = failureCode,
            Summary = summary,
            FailureMessage = summary,
        };

    private string BuildOperationId(DadShoppingRunAssociation association)
        => $"{runId}:m{moduleIndex}:a{associationIndex}:{association.AssociationId}:{attemptToken}";

    private void MergeEvidence(
        IEnumerable<string>? completedRowIds,
        IEnumerable<DadAdsShopListRowStatus>? rows)
    {
        lastCompletedRowIds = lastCompletedRowIds
            .Concat(completedRowIds ?? [])
            .Select(DadShoppingAssociationRules.NormalizeAdsGuid)
            .Where(static rowId => !string.IsNullOrWhiteSpace(rowId))
            .Distinct(StringComparer.Ordinal)
            .Take(DadShoppingAssociation.MaxCompletedRowIds)
            .ToList();
        if (rows == null)
            return;
        lastRows = rows
            .Where(static row => row != null)
            .Select(static row =>
            {
                var clone = row.Clone();
                clone.RowId = DadShoppingAssociationRules.NormalizeAdsGuid(clone.RowId);
                return clone;
            })
            .Where(static row => !string.IsNullOrWhiteSpace(row.RowId))
            .GroupBy(static row => row.RowId, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .ToList();
    }

    private static DadShoppingRuntimeDecision Ready(string summary)
        => new(DadShoppingRuntimeAction.Ready, summary);

    private static DadShoppingRuntimeDecision Wait(string summary)
        => new(DadShoppingRuntimeAction.Wait, summary);

    private static DadShoppingRuntimeDecision Reject(string summary)
        => new(DadShoppingRuntimeAction.Reject, summary);

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
