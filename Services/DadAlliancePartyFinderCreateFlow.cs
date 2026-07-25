using System.Numerics;

namespace dad.Services;

internal enum DadAlliancePfCreateStage
{
    CloseStaleWindows,
    OpenMainWindow,
    OpenConditions,
    SelectAlliance,
    SelectRaids,
    SelectDuty,
    Configure,
    Submit,
    Complete,
    Stopped,
    Blocked,
}

internal enum DadAlliancePfCreateAction
{
    CloseStaleWindows,
    OpenMainWindow,
    OpenConditions,
    SelectAlliance,
    SelectRaids,
    SelectDuty,
    ConfigureNextSetting,
    Submit,
}

internal enum DadAlliancePfCreateResultKind
{
    Progress,
    Waiting,
    Retry,
    Succeeded,
    Stopped,
    Blocked,
}

internal sealed record DadAlliancePfCreateSnapshot
{
    public bool SafeToMutate { get; init; } = true;
    public string SafetyBlocker { get; init; } = string.Empty;
    public bool AgentAvailable { get; init; } = true;
    public bool MainVisible { get; init; }
    public bool MainReady { get; init; }
    public bool MainRecruitUsable { get; init; }
    public bool ConditionVisible { get; init; }
    public bool ConditionReady { get; init; }
    public bool AllianceSelected { get; init; }
    public uint SelectedCategory { get; init; }
    public ushort TargetDutyId { get; init; }
    public int TargetDutySheetMatches { get; init; }
    public bool DutyListLoaded { get; init; }
    public int TargetDutyDropDownMatches { get; init; }
    public bool TargetDutyEntryEnabled { get; init; }
    public ushort SelectedDutyId { get; init; }
    public bool AllianceASelected { get; init; }
    public bool PrivateRecruitment { get; init; }
    public int Passcode { get; init; }
    public bool CrossWorldRecruitment { get; init; }
    public bool OnePlayerPerJob { get; init; }
    public bool EmptyComment { get; init; }
    public bool UnrestrictedJobs { get; init; }
    public int NumberOfGroups { get; init; }
    public int SlotsPerGroup { get; init; }
    public bool StoredSettingsExact { get; init; }
    public bool StoredSettingsContradictory { get; init; }
    public ulong OwnListingId { get; init; }
    public int ErrorToastSequence { get; init; }
    public string ErrorToast { get; init; } = string.Empty;
    public string HardBlocker { get; init; } = string.Empty;
    public string Readiness { get; init; } = string.Empty;
}

internal readonly record struct DadAlliancePfCreateActionResult(
    bool Sent,
    string Summary,
    string Error = "");

internal interface IDadAlliancePartyFinderCreateUi
{
    DadAlliancePfCreateSnapshot Read(int passcode);
    DadAlliancePfCreateActionResult Perform(DadAlliancePfCreateAction action, int passcode);
}

internal readonly record struct DadAlliancePfCreateResult(
    DadAlliancePfCreateResultKind Kind,
    DadAlliancePfCreateStage Stage,
    string Event,
    string Summary,
    int Attempt,
    DateTime? NextRetryUtc,
    string LastError,
    string Readiness,
    uint Category,
    ushort DutyId,
    ulong ListingId,
    int ElapsedMilliseconds,
    bool ShouldAudit);

/// <summary>
/// Pure acknowledgement-driven PF creation coordinator. Sending a UI action never
/// advances the stage; only a later observed snapshot can acknowledge it.
/// </summary>
internal sealed class DadAlliancePartyFinderCreateFlow
{
    internal const uint RaidsCategoryMask = 0x20;
    internal static readonly byte RaidsCategoryBitIndex =
        checked((byte)BitOperations.TrailingZeroCount(RaidsCategoryMask));
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IDadAlliancePartyFinderCreateUi ui;
    private readonly Func<DateTime> utcNow;
    private DadAlliancePfCreateStage stage = DadAlliancePfCreateStage.CloseStaleWindows;
    private DateTime startedAtUtc;
    private DateTime nextPollUtc;
    private DateTime nextActionUtc;
    private int actionAttempt;
    private int lastErrorToastSequence;
    private string lastError = string.Empty;
    private bool started;
    private bool configurationAcknowledged;
    private bool stopped;

    public DadAlliancePartyFinderCreateFlow(
        IDadAlliancePartyFinderCreateUi ui,
        Func<DateTime>? utcNow = null)
    {
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public DadAlliancePfCreateStage Stage => stage;
    public int Attempt => actionAttempt;
    public DateTime? NextRetryUtc => nextActionUtc == DateTime.MinValue ? null : nextActionUtc;
    public string LastError => lastError;

    public DadAlliancePfCreateResult Advance(int passcode)
    {
        var now = EnsureUtc(utcNow());
        if (!started)
        {
            started = true;
            startedAtUtc = now;
        }

        if (stopped)
            return Result(DadAlliancePfCreateResultKind.Stopped, "stop", "Party Finder creation stopped.", now, shouldAudit: false);
        if (stage == DadAlliancePfCreateStage.Complete)
            return Result(DadAlliancePfCreateResultKind.Succeeded, "success", "Party Finder listing is acknowledged.", now, shouldAudit: false);
        if (stage == DadAlliancePfCreateStage.Blocked)
            return Result(DadAlliancePfCreateResultKind.Blocked, "block", lastError, now, shouldAudit: false);
        if (now < nextPollUtc)
            return Result(DadAlliancePfCreateResultKind.Waiting, "poll-wait", "Waiting for the next Party Finder readiness poll.", now, shouldAudit: false);

        nextPollUtc = now + PollInterval;
        DadAlliancePfCreateSnapshot snapshot;
        try
        {
            snapshot = ui.Read(passcode);
        }
        catch (Exception exception)
        {
            if (now < nextActionUtc)
                return Result(DadAlliancePfCreateResultKind.Waiting, "retry-wait", "Waiting to retry the Party Finder readiness check.", now, shouldAudit: false);
            return ScheduleRetry(
                now,
                "exception",
                $"Party Finder readiness check failed: {exception.Message}",
                string.Empty,
                incrementAttempt: true);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.HardBlocker))
            return Block(now, snapshot.HardBlocker, snapshot);
        if (snapshot.OwnListingId != 0)
        {
            if (!configurationAcknowledged || !snapshot.StoredSettingsExact || snapshot.StoredSettingsContradictory)
                return Block(now, "The active Party Finder listing contradicts the exact acknowledged DAD Labyrinth settings.", snapshot);

            stage = DadAlliancePfCreateStage.Complete;
            nextActionUtc = DateTime.MinValue;
            return Result(
                DadAlliancePfCreateResultKind.Succeeded,
                "success",
                $"Private cross-world Labyrinth alliance recruitment is open as listing {snapshot.OwnListingId}.",
                now,
                snapshot,
                shouldAudit: true);
        }
        if (snapshot.StoredSettingsContradictory)
            return Block(now, "Stored Party Finder settings contradict the DAD Labyrinth recruitment.", snapshot);

        if (snapshot.ErrorToastSequence != 0 &&
            snapshot.ErrorToastSequence != lastErrorToastSequence)
        {
            lastErrorToastSequence = snapshot.ErrorToastSequence;
            var error = string.IsNullOrWhiteSpace(snapshot.ErrorToast)
                ? "Party Finder reported an error."
                : snapshot.ErrorToast.Trim();
            return ScheduleRetry(now, "error-toast", error, snapshot.Readiness, snapshot);
        }

        if (!snapshot.SafeToMutate)
        {
            var summary = string.IsNullOrWhiteSpace(snapshot.SafetyBlocker)
                ? "Waiting for safe Party Finder mutation conditions."
                : snapshot.SafetyBlocker;
            return Result(DadAlliancePfCreateResultKind.Waiting, "readiness", summary, now, snapshot, shouldAudit: true);
        }
        if (!snapshot.AgentAvailable)
        {
            if (now < nextActionUtc)
                return Result(DadAlliancePfCreateResultKind.Waiting, "retry-wait", "Waiting to retry the Party Finder agent.", now, snapshot, shouldAudit: false);
            return ScheduleRetry(
                now,
                "readiness",
                "Party Finder agent is unavailable.",
                snapshot.Readiness,
                snapshot,
                incrementAttempt: true);
        }
        if (stage is DadAlliancePfCreateStage.Configure or DadAlliancePfCreateStage.Submit)
        {
            if (snapshot.SelectedCategory != RaidsCategoryMask)
                return Block(now, "Party Finder no longer retains the exact Raids category.", snapshot);
            if (snapshot.TargetDutyId == 0 || snapshot.SelectedDutyId != snapshot.TargetDutyId)
                return Block(now, "Party Finder no longer retains the exact Labyrinth duty ID.", snapshot);
        }
        if (stage == DadAlliancePfCreateStage.Submit && !snapshot.StoredSettingsExact)
            return Block(now, "Party Finder stored settings changed after exact configuration acknowledgement.", snapshot);

        var acknowledgement = TryAcknowledge(snapshot, passcode, now);
        if (acknowledgement is { } acknowledged)
            return acknowledged;

        var readinessWait = GetReadinessWait(snapshot);
        if (!string.IsNullOrWhiteSpace(readinessWait))
        {
            return Result(
                DadAlliancePfCreateResultKind.Waiting,
                "readiness",
                readinessWait,
                now,
                snapshot,
                shouldAudit: true);
        }

        if (now < nextActionUtc)
            return Result(
                DadAlliancePfCreateResultKind.Waiting,
                "retry-wait",
                $"Waiting to retry {DescribeStage(stage)}.",
                now,
                snapshot,
                shouldAudit: false);

        return SendAction(snapshot, passcode, now);
    }

    public DadAlliancePfCreateResult Stop()
    {
        var now = EnsureUtc(utcNow());
        if (!started)
        {
            started = true;
            startedAtUtc = now;
        }
        if (stopped)
            return Result(DadAlliancePfCreateResultKind.Stopped, "stop", "Party Finder creation already stopped.", now, shouldAudit: false);

        stopped = true;
        stage = DadAlliancePfCreateStage.Stopped;
        nextActionUtc = DateTime.MinValue;
        return Result(DadAlliancePfCreateResultKind.Stopped, "stop", "Party Finder creation stopped.", now, shouldAudit: true);
    }

    private DadAlliancePfCreateResult? TryAcknowledge(
        DadAlliancePfCreateSnapshot snapshot,
        int passcode,
        DateTime now)
    {
        var next = stage switch
        {
            DadAlliancePfCreateStage.CloseStaleWindows when !snapshot.MainVisible && !snapshot.ConditionVisible
                => DadAlliancePfCreateStage.OpenMainWindow,
            DadAlliancePfCreateStage.OpenMainWindow when snapshot.MainReady && snapshot.MainRecruitUsable
                => DadAlliancePfCreateStage.OpenConditions,
            DadAlliancePfCreateStage.OpenConditions when snapshot.ConditionReady
                => DadAlliancePfCreateStage.SelectAlliance,
            DadAlliancePfCreateStage.SelectAlliance when snapshot.AllianceSelected
                => DadAlliancePfCreateStage.SelectRaids,
            DadAlliancePfCreateStage.SelectRaids when snapshot.SelectedCategory == RaidsCategoryMask
                => DadAlliancePfCreateStage.SelectDuty,
            DadAlliancePfCreateStage.SelectDuty when
                snapshot.TargetDutySheetMatches == 1 &&
                snapshot.TargetDutyDropDownMatches == 1 &&
                snapshot.TargetDutyEntryEnabled &&
                snapshot.TargetDutyId != 0 &&
                snapshot.SelectedDutyId == snapshot.TargetDutyId
                => DadAlliancePfCreateStage.Configure,
            DadAlliancePfCreateStage.Configure when HasExactConfiguredSettings(snapshot, passcode)
                => DadAlliancePfCreateStage.Submit,
            _ => stage,
        };

        if (next == stage)
            return null;

        var prior = stage;
        stage = next;
        actionAttempt = 0;
        nextActionUtc = DateTime.MinValue;
        lastError = string.Empty;
        if (next == DadAlliancePfCreateStage.Submit)
            configurationAcknowledged = true;
        return Result(
            DadAlliancePfCreateResultKind.Progress,
            "acknowledgement",
            $"Acknowledged {DescribeStage(prior)}; next stage is {DescribeStage(next)}.",
            now,
            snapshot,
            shouldAudit: true);
    }

    private DadAlliancePfCreateResult SendAction(
        DadAlliancePfCreateSnapshot snapshot,
        int passcode,
        DateTime now)
    {
        if (stage == DadAlliancePfCreateStage.SelectDuty)
        {
            if (snapshot.TargetDutySheetMatches != 1)
                return Block(now, $"Expected one ContentFinderCondition match for The Labyrinth of the Ancients; found {snapshot.TargetDutySheetMatches}.", snapshot);
            if (snapshot.TargetDutyDropDownMatches != 1)
                return Block(now, $"Expected one enabled Labyrinth duty dropdown entry; found {snapshot.TargetDutyDropDownMatches}.", snapshot);
            if (!snapshot.TargetDutyEntryEnabled)
                return Block(now, "The exact Labyrinth duty dropdown entry is disabled.", snapshot);
        }

        var action = stage switch
        {
            DadAlliancePfCreateStage.CloseStaleWindows => DadAlliancePfCreateAction.CloseStaleWindows,
            DadAlliancePfCreateStage.OpenMainWindow => DadAlliancePfCreateAction.OpenMainWindow,
            DadAlliancePfCreateStage.OpenConditions => DadAlliancePfCreateAction.OpenConditions,
            DadAlliancePfCreateStage.SelectAlliance => DadAlliancePfCreateAction.SelectAlliance,
            DadAlliancePfCreateStage.SelectRaids => DadAlliancePfCreateAction.SelectRaids,
            DadAlliancePfCreateStage.SelectDuty => DadAlliancePfCreateAction.SelectDuty,
            DadAlliancePfCreateStage.Configure => DadAlliancePfCreateAction.ConfigureNextSetting,
            DadAlliancePfCreateStage.Submit => DadAlliancePfCreateAction.Submit,
            _ => throw new InvalidOperationException($"Stage {stage} cannot send a Party Finder action."),
        };

        actionAttempt++;
        DadAlliancePfCreateActionResult actionResult;
        try
        {
            actionResult = ui.Perform(action, passcode);
        }
        catch (Exception exception)
        {
            return ScheduleRetry(now, "exception", $"{DescribeStage(stage)} failed: {exception.Message}", snapshot.Readiness, snapshot);
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
                now,
                snapshot,
                shouldAudit: true);
        }

        lastError = string.Empty;
        return Result(
            DadAlliancePfCreateResultKind.Progress,
            "action",
            actionResult.Summary,
            now,
            snapshot,
            shouldAudit: true);
    }

    private string GetReadinessWait(DadAlliancePfCreateSnapshot snapshot)
        => stage switch
        {
            DadAlliancePfCreateStage.OpenMainWindow when snapshot.MainVisible &&
                                                         (!snapshot.MainReady || !snapshot.MainRecruitUsable)
                => "Waiting for the visible Party Finder window and Recruit Members control to become fully usable.",
            DadAlliancePfCreateStage.OpenConditions when snapshot.ConditionVisible && !snapshot.ConditionReady
                => "Waiting for the visible Party Finder conditions window to become fully ready.",
            DadAlliancePfCreateStage.SelectAlliance or
                DadAlliancePfCreateStage.SelectRaids or
                DadAlliancePfCreateStage.SelectDuty or
                DadAlliancePfCreateStage.Configure when !snapshot.ConditionReady &&
                                                        snapshot.OwnListingId == 0
                => "Waiting for Party Finder conditions to become ready.",
            DadAlliancePfCreateStage.SelectDuty when
                snapshot.TargetDutySheetMatches == 1 &&
                !snapshot.DutyListLoaded
                => "Waiting for the Raids duty dropdown to finish loading.",
            _ => string.Empty,
        };

    private DadAlliancePfCreateResult ScheduleRetry(
        DateTime now,
        string eventName,
        string error,
        string readiness,
        DadAlliancePfCreateSnapshot? snapshot = null,
        bool incrementAttempt = false)
    {
        if (incrementAttempt)
            actionAttempt++;
        else if (actionAttempt == 0)
            actionAttempt = 1;
        lastError = error;
        nextActionUtc = now + DadAlliancePartyFinderRules.GetRetryDelay(actionAttempt - 1);
        return Result(
            DadAlliancePfCreateResultKind.Retry,
            eventName,
            error,
            now,
            snapshot,
            readiness,
            shouldAudit: true);
    }

    private DadAlliancePfCreateResult Block(
        DateTime now,
        string error,
        DadAlliancePfCreateSnapshot snapshot)
    {
        lastError = error;
        stage = DadAlliancePfCreateStage.Blocked;
        nextActionUtc = DateTime.MinValue;
        return Result(DadAlliancePfCreateResultKind.Blocked, "block", error, now, snapshot, shouldAudit: true);
    }

    private DadAlliancePfCreateResult Result(
        DadAlliancePfCreateResultKind kind,
        string eventName,
        string summary,
        DateTime now,
        DadAlliancePfCreateSnapshot? snapshot = null,
        string readiness = "",
        bool shouldAudit = false)
        => new(
            kind,
            stage,
            eventName,
            summary,
            actionAttempt,
            nextActionUtc == DateTime.MinValue ? null : nextActionUtc,
            lastError,
            string.IsNullOrWhiteSpace(readiness) ? snapshot?.Readiness ?? string.Empty : readiness,
            snapshot?.SelectedCategory ?? 0,
            snapshot?.SelectedDutyId ?? 0,
            snapshot?.OwnListingId ?? 0,
            checked((int)Math.Clamp((now - startedAtUtc).TotalMilliseconds, 0, int.MaxValue)),
            shouldAudit);

    private static string DescribeStage(DadAlliancePfCreateStage value)
        => value switch
        {
            DadAlliancePfCreateStage.CloseStaleWindows => "closing stale Party Finder windows",
            DadAlliancePfCreateStage.OpenMainWindow => "opening Party Finder",
            DadAlliancePfCreateStage.OpenConditions => "opening recruitment conditions",
            DadAlliancePfCreateStage.SelectAlliance => "selecting Alliance recruitment",
            DadAlliancePfCreateStage.SelectRaids => "selecting the Raids category",
            DadAlliancePfCreateStage.SelectDuty => "selecting The Labyrinth of the Ancients",
            DadAlliancePfCreateStage.Configure => "configuring exact private alliance settings",
            DadAlliancePfCreateStage.Submit => "submitting and acknowledging the listing",
            DadAlliancePfCreateStage.Complete => "complete",
            DadAlliancePfCreateStage.Stopped => "stopped",
            DadAlliancePfCreateStage.Blocked => "blocked",
            _ => value.ToString(),
        };

    private static bool HasExactConfiguredSettings(
        DadAlliancePfCreateSnapshot snapshot,
        int passcode)
        => snapshot.AllianceSelected &&
           snapshot.AllianceASelected &&
           snapshot.PrivateRecruitment &&
           snapshot.Passcode == passcode &&
           snapshot.CrossWorldRecruitment &&
           !snapshot.OnePlayerPerJob &&
           snapshot.EmptyComment &&
           snapshot.UnrestrictedJobs &&
           snapshot.NumberOfGroups == 3 &&
           snapshot.SlotsPerGroup == 8 &&
           snapshot.StoredSettingsExact;

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
