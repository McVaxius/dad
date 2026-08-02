namespace dad.Services;

internal enum DadAlliancePfCleanupStage
{
    OpenMainWindow,
    OpenDetails,
    RequestEndRecruitment,
    AwaitConfirmation,
    ConfirmEndRecruitment,
    AwaitClosure,
    Complete,
    Stopped,
    Blocked,
}

internal sealed record DadAlliancePfCleanupSnapshot
{
    public bool AgentAvailable { get; init; } = true;
    public bool ActiveRecruitment { get; init; }
    public ulong OwnerHandle { get; init; }
    public bool MainVisible { get; init; }
    public bool MainReady { get; init; }
    public bool DetailsControlUsable { get; init; }
    public bool DetailVisible { get; init; }
    public bool DetailReady { get; init; }
    public bool ConfirmationVisible { get; init; }
    public bool ConfirmationReady { get; init; }
    public string ConfirmationIdentity { get; init; } = string.Empty;
    public string ConfirmationText { get; init; } = string.Empty;
    public bool OtherReadyPromptVisible { get; init; }
    public string HardBlocker { get; init; } = string.Empty;
    public string Readiness { get; init; } = string.Empty;
}

internal interface IDadAlliancePartyFinderCleanupUi
{
    DadAlliancePfCleanupSnapshot ReadCleanup();
    DadAlliancePfCreateActionResult PerformCleanup(DadAlliancePfNativeAction action);
}

internal readonly record struct DadAlliancePfCleanupResult(
    DadAlliancePfCreateResultKind Kind,
    DadAlliancePfCleanupStage Stage,
    string Event,
    string Summary,
    int Attempt,
    DateTime? NextRetryUtc,
    string LastError,
    string Readiness,
    bool ActiveRecruitment,
    ulong OwnerHandle,
    bool ShouldAudit,
    bool PromptOverrideUsed = false);

/// <summary>
/// Pure recruitment-only cleanup coordinator. Each destructive step is gated by
/// retained DAD ownership and a later observation. Closure is acknowledged when
/// authoritative condition 66 reports that recruitment is no longer active.
/// </summary>
internal sealed class DadAlliancePartyFinderCleanupFlow
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IDadAlliancePartyFinderCleanupUi ui;
    private readonly Func<DateTime> utcNow;
    private readonly bool allowFreshUnprovenPromptApproval;
    private DadAlliancePfCleanupStage stage = DadAlliancePfCleanupStage.OpenMainWindow;
    private DateTime nextPollUtc;
    private DateTime nextActionUtc;
    private int actionAttempt;
    private string lastError = string.Empty;
    private string acceptedConfirmation = string.Empty;
    private DadPromptObservation confirmationBaselineObservation;
    private int confirmationCommandAttempt;
    private int approvedConfirmationAttempt;
    private int blockedRetryAttempt;
    private bool pendingPromptOverride;
    private bool detailsDispatched;
    private bool stopped;

    public DadAlliancePartyFinderCleanupFlow(
        IDadAlliancePartyFinderCleanupUi ui,
        Func<DateTime>? utcNow = null,
        bool allowFreshUnprovenPromptApproval = false)
    {
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.allowFreshUnprovenPromptApproval = allowFreshUnprovenPromptApproval;
    }

    public DadAlliancePfCleanupStage Stage => stage;

    public DadAlliancePfCleanupResult Advance(bool dadOwnsRecruitment)
    {
        var now = EnsureUtc(utcNow());
        if (stopped)
            return Result(DadAlliancePfCreateResultKind.Stopped, "stop", "Party Finder cleanup stopped.", default, false);
        if (stage == DadAlliancePfCleanupStage.Complete)
            return Result(DadAlliancePfCreateResultKind.Succeeded, "success", "Recruitment-only cleanup is acknowledged.", default, false);
        if (stage == DadAlliancePfCleanupStage.Blocked)
        {
            if (now < nextActionUtc)
            {
                return Result(
                    DadAlliancePfCreateResultKind.Blocked,
                    "block-wait",
                    lastError,
                    default,
                    false);
            }

            stage = DadAlliancePfCleanupStage.OpenMainWindow;
            nextActionUtc = DateTime.MinValue;
        }
        if (now < nextPollUtc)
            return Result(DadAlliancePfCreateResultKind.Waiting, "poll-wait", "Waiting for the next cleanup readiness poll.", default, false);

        nextPollUtc = now + PollInterval;
        DadAlliancePfCleanupSnapshot snapshot;
        try
        {
            snapshot = ui.ReadCleanup();
        }
        catch (Exception exception)
        {
            return ScheduleRetry(
                now,
                $"Party Finder cleanup readiness check failed: {exception.Message}",
                default);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.HardBlocker))
            return Block(now, snapshot.HardBlocker, snapshot, retryable: true);
        if (!snapshot.AgentAvailable)
            return ScheduleRetry(now, "Party Finder agent is unavailable during cleanup.", snapshot);
        if (!dadOwnsRecruitment)
            return Block(now, "DAD cannot clean up recruitment without retained DAD ownership.", snapshot);

        if (!snapshot.ActiveRecruitment)
        {
            stage = DadAlliancePfCleanupStage.Complete;
            nextActionUtc = DateTime.MinValue;
            return Result(
                DadAlliancePfCreateResultKind.Succeeded,
                "success",
                "DAD-owned recruitment ended; the formed alliance was preserved.",
                snapshot,
                true);
        }

        return stage switch
        {
            DadAlliancePfCleanupStage.OpenMainWindow => AdvanceOpenMain(now, snapshot),
            DadAlliancePfCleanupStage.OpenDetails => AdvanceOpenDetails(now, snapshot),
            DadAlliancePfCleanupStage.RequestEndRecruitment => AdvanceEndRequest(now, snapshot),
            DadAlliancePfCleanupStage.AwaitConfirmation => AdvanceConfirmation(snapshot),
            DadAlliancePfCleanupStage.ConfirmEndRecruitment => AdvanceConfirm(now, snapshot),
            DadAlliancePfCleanupStage.AwaitClosure => Result(
                DadAlliancePfCreateResultKind.Waiting,
                "readiness",
                "Waiting for condition 66 UsingPartyFinder to clear.",
                snapshot,
                true),
            _ => Block(now, $"Unsupported Party Finder cleanup stage {stage}.", snapshot),
        };
    }

    public DadAlliancePfCleanupResult Stop()
    {
        stopped = true;
        stage = DadAlliancePfCleanupStage.Stopped;
        nextActionUtc = DateTime.MinValue;
        return Result(
            DadAlliancePfCreateResultKind.Stopped,
            "stop",
            "Party Finder cleanup stopped.",
            default,
            true);
    }

    private DadAlliancePfCleanupResult AdvanceOpenMain(
        DateTime now,
        DadAlliancePfCleanupSnapshot snapshot)
    {
        if (snapshot.MainReady && snapshot.DetailsControlUsable)
            return Acknowledge(DadAlliancePfCleanupStage.OpenDetails, "owned Party Finder controls", snapshot);
        return Send(
            now,
            DadAlliancePfNativeAction.ShowOwnedRecruitment,
            "opening the owned Party Finder window",
            snapshot);
    }

    private DadAlliancePfCleanupResult AdvanceOpenDetails(
        DateTime now,
        DadAlliancePfCleanupSnapshot snapshot)
    {
        if (snapshot.DetailReady && detailsDispatched)
        {
            detailsDispatched = false;
            return Acknowledge(DadAlliancePfCleanupStage.RequestEndRecruitment, "owned recruitment details", snapshot);
        }
        if (detailsDispatched)
        {
            if (now < nextActionUtc)
            {
                return Result(
                    DadAlliancePfCreateResultKind.Waiting,
                    "readiness",
                    "Waiting for the owned recruitment detail window to become ready.",
                    snapshot,
                    true);
            }

            detailsDispatched = false;
            stage = DadAlliancePfCleanupStage.OpenMainWindow;
            return Result(
                DadAlliancePfCreateResultKind.Waiting,
                "readiness",
                "Owned recruitment details did not acknowledge; reopening the owned Party Finder window.",
                snapshot,
                true);
        }
        if (!snapshot.MainReady || !snapshot.DetailsControlUsable)
        {
            stage = DadAlliancePfCleanupStage.OpenMainWindow;
            return Result(
                DadAlliancePfCreateResultKind.Waiting,
                "readiness",
                "Waiting for the owned Party Finder controls to become ready again.",
                snapshot,
                true);
        }
        return Send(
            now,
            DadAlliancePfNativeAction.OpenOwnedDetails,
            "opening owned recruitment details",
            snapshot);
    }

    private DadAlliancePfCleanupResult AdvanceEndRequest(
        DateTime now,
        DadAlliancePfCleanupSnapshot snapshot)
    {
        if (!snapshot.DetailReady)
        {
            stage = DadAlliancePfCleanupStage.OpenDetails;
            return Result(
                DadAlliancePfCreateResultKind.Waiting,
                "readiness",
                "Waiting for owned recruitment details to become ready again.",
                snapshot,
                true);
        }

        confirmationBaselineObservation = BuildPromptObservation(snapshot);
        confirmationCommandAttempt++;
        pendingPromptOverride = false;
        return Send(
            now,
            DadAlliancePfNativeAction.EndRecruitment,
            "requesting recruitment-only closure",
            snapshot,
            DadAlliancePfCleanupStage.AwaitConfirmation);
    }

    private DadAlliancePfCleanupResult AdvanceConfirmation(
        DadAlliancePfCleanupSnapshot snapshot)
    {
        var decision = EvaluatePrompt(snapshot);
        if (!decision.CanApprove)
        {
            return Result(
                DadAlliancePfCreateResultKind.Waiting,
                "readiness",
                $"Waiting for a fresh recruitment-only confirmation: {decision.Summary}",
                snapshot,
                true);
        }

        acceptedConfirmation = snapshot.ConfirmationIdentity;
        pendingPromptOverride = decision.UsedOverride;
        return Acknowledge(
            DadAlliancePfCleanupStage.ConfirmEndRecruitment,
            decision.UsedOverride
                ? "WARNING: one fresh sole recruitment prompt through the operator override"
                : "fresh exact recruitment-only confirmation",
            snapshot);
    }

    private DadAlliancePfCleanupResult AdvanceConfirm(
        DateTime now,
        DadAlliancePfCleanupSnapshot snapshot)
    {
        var decision = EvaluatePrompt(snapshot);
        if (!decision.CanApprove ||
            decision.UsedOverride != pendingPromptOverride ||
            !string.Equals(
                snapshot.ConfirmationIdentity,
                acceptedConfirmation,
                StringComparison.Ordinal))
        {
            return Block(
                now,
                "The acknowledged recruitment-only confirmation changed before confirmation.",
                snapshot,
                retryable: true);
        }

        var result = Send(
            now,
            DadAlliancePfNativeAction.ConfirmEndRecruitment,
            "confirming recruitment-only closure",
            snapshot,
            DadAlliancePfCleanupStage.AwaitClosure);
        if (result.Kind != DadAlliancePfCreateResultKind.Progress)
            return result;

        approvedConfirmationAttempt = confirmationCommandAttempt;
        return result with { PromptOverrideUsed = decision.UsedOverride };
    }

    private DadAlliancePfCleanupResult Send(
        DateTime now,
        DadAlliancePfNativeAction action,
        string description,
        DadAlliancePfCleanupSnapshot snapshot,
        DadAlliancePfCleanupStage? stageAfterSend = null)
    {
        if (now < nextActionUtc)
        {
            return Result(
                DadAlliancePfCreateResultKind.Waiting,
                "retry-wait",
                $"Waiting to retry {description}.",
                snapshot,
                false);
        }

        actionAttempt++;
        DadAlliancePfCreateActionResult actionResult;
        try
        {
            actionResult = ui.PerformCleanup(action);
        }
        catch (Exception exception)
        {
            return ScheduleRetry(now, $"{description} failed: {exception.Message}", snapshot);
        }

        nextActionUtc = now + DadAlliancePartyFinderRules.GetRetryDelay(actionAttempt - 1);
        if (!actionResult.Sent)
        {
            lastError = string.IsNullOrWhiteSpace(actionResult.Error)
                ? actionResult.Summary
                : actionResult.Error;
            return Result(
                DadAlliancePfCreateResultKind.Retry,
                "retry",
                actionResult.Summary,
                snapshot,
                true);
        }

        lastError = string.Empty;
        if (action == DadAlliancePfNativeAction.OpenOwnedDetails)
            detailsDispatched = true;
        if (stageAfterSend.HasValue)
        {
            stage = stageAfterSend.Value;
            actionAttempt = 0;
            nextActionUtc = DateTime.MinValue;
        }
        return Result(
            DadAlliancePfCreateResultKind.Progress,
            "action",
            actionResult.Summary,
            snapshot,
            true);
    }

    private DadAlliancePfCleanupResult Acknowledge(
        DadAlliancePfCleanupStage next,
        string acknowledgement,
        DadAlliancePfCleanupSnapshot snapshot)
    {
        stage = next;
        blockedRetryAttempt = 0;
        actionAttempt = 0;
        nextActionUtc = DateTime.MinValue;
        lastError = string.Empty;
        return Result(
            DadAlliancePfCreateResultKind.Progress,
            "acknowledgement",
            $"Acknowledged {acknowledgement}.",
            snapshot,
            true);
    }

    private DadAlliancePfCleanupResult ScheduleRetry(
        DateTime now,
        string error,
        DadAlliancePfCleanupSnapshot? snapshot)
    {
        if (actionAttempt == 0)
            actionAttempt = 1;
        lastError = error;
        nextActionUtc = now + DadAlliancePartyFinderRules.GetRetryDelay(actionAttempt - 1);
        return Result(
            DadAlliancePfCreateResultKind.Retry,
            "exception",
            error,
            snapshot,
            true);
    }

    private DadAlliancePfCleanupResult Block(
        DateTime now,
        string error,
        DadAlliancePfCleanupSnapshot snapshot,
        bool retryable = false)
    {
        lastError = error;
        stage = DadAlliancePfCleanupStage.Blocked;
        nextActionUtc = retryable
            ? now + DadAlliancePartyFinderRules.GetRetryDelay(blockedRetryAttempt++)
            : DateTime.MaxValue;
        return Result(
            DadAlliancePfCreateResultKind.Blocked,
            "block",
            error,
            snapshot,
            true);
    }

    private DadAlliancePfCleanupResult Result(
        DadAlliancePfCreateResultKind kind,
        string eventName,
        string summary,
        DadAlliancePfCleanupSnapshot? snapshot,
        bool shouldAudit)
        => new(
            kind,
            stage,
            eventName,
            summary,
            actionAttempt,
            nextActionUtc == DateTime.MinValue ? null : nextActionUtc,
            lastError,
            snapshot?.Readiness ?? string.Empty,
            snapshot?.ActiveRecruitment ?? false,
            snapshot?.OwnerHandle ?? 0,
            shouldAudit);

    private DadPromptApprovalDecision EvaluatePrompt(
        DadAlliancePfCleanupSnapshot snapshot)
    {
        var operationKey = "alliance-recruitment-cleanup";
        return DadPromptOwnershipRules.Evaluate(new DadPromptApprovalRequest(
            DadPromptOperationKind.AllianceRecruitmentCleanup,
            operationKey,
            operationKey,
            confirmationCommandAttempt,
            confirmationCommandAttempt,
            approvedConfirmationAttempt,
            confirmationBaselineObservation,
            BuildPromptObservation(snapshot),
            string.Empty,
            allowFreshUnprovenPromptApproval));
    }

    private static DadPromptObservation BuildPromptObservation(
        DadAlliancePfCleanupSnapshot snapshot)
        => new(
            snapshot.ConfirmationVisible,
            snapshot.ConfirmationReady,
            snapshot.ConfirmationIdentity,
            snapshot.ConfirmationText,
            snapshot.ConfirmationVisible && !snapshot.OtherReadyPromptVisible);

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
