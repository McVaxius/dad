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
    public string ConfirmationIdentity { get; init; } = string.Empty;
    public string ConfirmationText { get; init; } = string.Empty;
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
    bool ShouldAudit);

/// <summary>
/// Pure recruitment-only cleanup coordinator. Each destructive step is gated by
/// a later observation and the final acknowledgement requires both local
/// recruitment status and the opaque PF owner handle to clear.
/// </summary>
internal sealed class DadAlliancePartyFinderCleanupFlow
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IDadAlliancePartyFinderCleanupUi ui;
    private readonly Func<DateTime> utcNow;
    private DadAlliancePfCleanupStage stage = DadAlliancePfCleanupStage.OpenMainWindow;
    private DateTime nextPollUtc;
    private DateTime nextActionUtc;
    private int actionAttempt;
    private string lastError = string.Empty;
    private string confirmationBaseline = string.Empty;
    private string acceptedConfirmation = string.Empty;
    private bool detailsDispatched;
    private bool stopped;

    public DadAlliancePartyFinderCleanupFlow(
        IDadAlliancePartyFinderCleanupUi ui,
        Func<DateTime>? utcNow = null)
    {
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public DadAlliancePfCleanupStage Stage => stage;

    public DadAlliancePfCleanupResult Advance(ulong expectedOwnerHandle)
    {
        var now = EnsureUtc(utcNow());
        if (stopped)
            return Result(DadAlliancePfCreateResultKind.Stopped, "stop", "Party Finder cleanup stopped.", default, false);
        if (stage == DadAlliancePfCleanupStage.Complete)
            return Result(DadAlliancePfCreateResultKind.Succeeded, "success", "Recruitment-only cleanup is acknowledged.", default, false);
        if (stage == DadAlliancePfCleanupStage.Blocked)
            return Result(DadAlliancePfCreateResultKind.Blocked, "block", lastError, default, false);
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
            return Block(snapshot.HardBlocker, snapshot);
        if (!snapshot.AgentAvailable)
            return ScheduleRetry(now, "Party Finder agent is unavailable during cleanup.", snapshot);
        if (expectedOwnerHandle == 0)
            return Block("DAD cannot clean up recruitment without its acknowledged PF owner handle.", snapshot);

        if (!snapshot.ActiveRecruitment && snapshot.OwnerHandle == 0)
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

        if (snapshot.OwnerHandle != 0 && snapshot.OwnerHandle != expectedOwnerHandle)
        {
            return Block(
                "The active PF owner handle no longer matches DAD's acknowledged owner handle.",
                snapshot);
        }

        if (stage != DadAlliancePfCleanupStage.AwaitClosure &&
            (!snapshot.ActiveRecruitment || snapshot.OwnerHandle == 0))
        {
            return Result(
                DadAlliancePfCreateResultKind.Waiting,
                "readiness",
                "Waiting for active owned recruitment before cleanup continues.",
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
                "Waiting for active recruitment status and the PF owner handle to clear.",
                snapshot,
                true),
            _ => Block($"Unsupported Party Finder cleanup stage {stage}.", snapshot),
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

        confirmationBaseline = snapshot.ConfirmationVisible
            ? snapshot.ConfirmationIdentity
            : string.Empty;
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
        if (!snapshot.ConfirmationVisible ||
            string.IsNullOrWhiteSpace(snapshot.ConfirmationIdentity) ||
            string.Equals(
                snapshot.ConfirmationIdentity,
                confirmationBaseline,
                StringComparison.Ordinal))
        {
            return Result(
                DadAlliancePfCreateResultKind.Waiting,
                "readiness",
                "Waiting for a fresh recruitment-only confirmation.",
                snapshot,
                true);
        }
        if (!IsRecruitmentOnlyConfirmation(snapshot.ConfirmationText))
        {
            return Block(
                "A fresh recruitment-only confirmation could not be proven; DAD will not click it.",
                snapshot);
        }

        acceptedConfirmation = snapshot.ConfirmationIdentity;
        return Acknowledge(
            DadAlliancePfCleanupStage.ConfirmEndRecruitment,
            "fresh recruitment-only confirmation",
            snapshot);
    }

    private DadAlliancePfCleanupResult AdvanceConfirm(
        DateTime now,
        DadAlliancePfCleanupSnapshot snapshot)
    {
        if (!snapshot.ConfirmationVisible ||
            !string.Equals(
                snapshot.ConfirmationIdentity,
                acceptedConfirmation,
                StringComparison.Ordinal) ||
            !IsRecruitmentOnlyConfirmation(snapshot.ConfirmationText))
        {
            return Block(
                "The acknowledged recruitment-only confirmation changed before confirmation.",
                snapshot);
        }

        return Send(
            now,
            DadAlliancePfNativeAction.ConfirmEndRecruitment,
            "confirming recruitment-only closure",
            snapshot,
            DadAlliancePfCleanupStage.AwaitClosure);
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
        string error,
        DadAlliancePfCleanupSnapshot snapshot)
    {
        lastError = error;
        stage = DadAlliancePfCleanupStage.Blocked;
        nextActionUtc = DateTime.MinValue;
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

    private static bool IsRecruitmentOnlyConfirmation(string text)
        => text.Contains("recruit", StringComparison.OrdinalIgnoreCase) &&
           !text.Contains("disband", StringComparison.OrdinalIgnoreCase) &&
           !text.Contains("leave the party", StringComparison.OrdinalIgnoreCase) &&
           !text.Contains("leave the alliance", StringComparison.OrdinalIgnoreCase);

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
