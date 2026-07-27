using System.Numerics;

namespace dad.Services;

internal enum DadAlliancePfCreateStage
{
    CloseStaleWindows,
    OpenMainWindow,
    OpenConditions,
    SelectAlliance,
    ReloadCloseConditions,
    ReloadMainWindow,
    ReloadOpenConditions,
    SelectRaids,
    SelectDuty,
    ApplyPreset,
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
    ReloadCloseConditions,
    ReloadMainWindow,
    ReloadOpenConditions,
    SelectRaids,
    SelectDuty,
    ApplyPreset,
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
    public bool PresetLoaderAvailable { get; init; } = true;
    public string PresetLoaderBlocker { get; init; } = string.Empty;
    public byte GroupTypeTab { get; init; }
    public bool AllianceSelected { get; init; }
    public uint SelectedCategory { get; init; }
    public ushort TargetDutyId { get; init; }
    public int TargetDutySheetMatches { get; init; }
    public bool DutyListLoaded { get; init; }
    public int TargetDutyDropDownMatches { get; init; }
    public bool TargetDutyEntryEnabled { get; init; }
    public int TargetDutyDropDownIndex { get; init; } = -1;
    public int SelectedDutyDropDownIndex { get; init; } = -1;
    public ushort SelectedDutyId { get; init; }
    public bool AllianceASelected { get; init; }
    public bool PrivateRecruitment { get; init; }
    public bool StoredPrivateRecruitment { get; init; }
    public int Passcode { get; init; }
    public int StoredPasscode { get; init; }
    public bool CrossWorldRecruitment { get; init; }
    public bool StoredCrossWorldRecruitment { get; init; }
    public bool OnePlayerPerJob { get; init; }
    public bool StoredOnePlayerPerJob { get; init; }
    public bool EmptyComment { get; init; }
    public bool StoredEmptyComment { get; init; }
    public bool UnrestrictedJobs { get; init; }
    public bool StoredOpenSlotsUnrestricted { get; init; }
    public bool StoredStaleMembersCleared { get; init; }
    public int NumberOfGroups { get; init; }
    public int SlotsPerGroup { get; init; }
    public bool StoredSettingsExactBeforeSubmit { get; init; }
    public bool StoredSettingsExact { get; init; }
    public bool StoredSettingsContradictory { get; init; }
    public ulong OwnerHandle { get; init; }
    public bool ActiveRecruitment { get; init; }
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
    bool ActiveRecruitment,
    bool EditorVisible,
    bool SubmitDispatched,
    string ConfigurationTarget,
    string ObservedSettings,
    bool ShouldAudit);

/// <summary>
/// Pure acknowledgement-driven PF creation coordinator. Sending a UI action never
/// advances the stage; only a later observed snapshot can acknowledge it.
/// </summary>
internal sealed class DadAlliancePartyFinderCreateFlow
{
    internal const uint RaidsCategoryMask =
        DadAlliancePartyFinderPresetDefinition.RaidsCategoryMask;
    internal const ushort LabyrinthDutyId =
        DadAlliancePartyFinderPresetDefinition.LabyrinthDutyId;
    internal static readonly byte RaidsCategoryBitIndex =
        checked((byte)BitOperations.TrailingZeroCount(RaidsCategoryMask));
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan ObservationTimeout =
        TimeSpan.FromSeconds(5);

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
    private bool actionDispatched;
    private bool presetAcknowledged;
    private bool submitDispatched;
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
    public bool SubmitDispatched => submitDispatched;

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
            return Result(DadAlliancePfCreateResultKind.Succeeded, "success", "Party Finder recruitment is acknowledged.", now, shouldAudit: false);
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
            return Block(
                now,
                $"Party Finder readiness check failed; DAD will not redispatch " +
                $"{DescribeStage(stage)}: {exception.Message}",
                null);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.HardBlocker))
            return Block(now, snapshot.HardBlocker, snapshot);
        if (!snapshot.PresetLoaderAvailable)
        {
            return Block(
                now,
                string.IsNullOrWhiteSpace(snapshot.PresetLoaderBlocker)
                    ? "The DAD-owned Party Finder preset loader is unavailable."
                    : snapshot.PresetLoaderBlocker,
                snapshot);
        }
        if (!submitDispatched && snapshot.ActiveRecruitment)
        {
            return Block(
                now,
                "A Party Finder recruitment is already active; DAD will not replace it.",
                snapshot);
        }

        var publishedTransition =
            submitDispatched &&
            !snapshot.ConditionVisible &&
            snapshot.ActiveRecruitment &&
            snapshot.OwnerHandle != 0;
        if (publishedTransition)
        {
            if (!presetAcknowledged ||
                !snapshot.StoredSettingsExact ||
                snapshot.StoredSettingsContradictory)
            {
                return Block(
                    now,
                    "The published Party Finder recruitment contradicts the exact acknowledged DAD Labyrinth settings.",
                    snapshot);
            }

            stage = DadAlliancePfCreateStage.Complete;
            actionDispatched = false;
            nextActionUtc = DateTime.MinValue;
            return Result(
                DadAlliancePfCreateResultKind.Succeeded,
                "success",
                $"Private cross-world Labyrinth alliance recruitment is active with PF owner handle {snapshot.OwnerHandle}.",
                now,
                snapshot,
                shouldAudit: true);
        }

        if (snapshot.ErrorToastSequence != 0 &&
            snapshot.ErrorToastSequence != lastErrorToastSequence)
        {
            lastErrorToastSequence = snapshot.ErrorToastSequence;
            var error = string.IsNullOrWhiteSpace(snapshot.ErrorToast)
                ? "Party Finder reported an error."
                : snapshot.ErrorToast.Trim();
            return Block(
                now,
                $"{error} DAD will not redispatch {DescribeStage(stage)}.",
                snapshot);
        }

        if (!snapshot.AgentAvailable)
        {
            return Block(
                now,
                "Party Finder agent is unavailable; DAD will not dispatch or redispatch this Create request.",
                snapshot);
        }
        if ((stage is DadAlliancePfCreateStage.ReloadCloseConditions or
                DadAlliancePfCreateStage.ReloadMainWindow or
                DadAlliancePfCreateStage.ReloadOpenConditions) &&
            snapshot.GroupTypeTab !=
                DadAlliancePartyFinderPresetDefinition.AllianceGroupTypeTab)
        {
            return Block(
                now,
                "The stored Alliance Party Finder tab changed during the one allowed editor reload.",
                snapshot);
        }
        if (stage == DadAlliancePfCreateStage.Submit &&
            !submitDispatched &&
            !HasExactPresetAcknowledgement(snapshot, passcode))
        {
            return Block(
                now,
                "Party Finder visible or stored settings changed after the exact DAD-owned preset acknowledgement.",
                snapshot);
        }

        var acknowledgement = TryAcknowledge(snapshot, passcode, now);
        if (acknowledgement is { } acknowledged)
            return acknowledged;

        if (actionDispatched)
        {
            if (now >= nextActionUtc)
            {
                return Block(
                    now,
                    BuildObservationTimeoutError(snapshot),
                    snapshot);
            }

            return Result(
                DadAlliancePfCreateResultKind.Waiting,
                "observation",
                $"Waiting up to five seconds for a later acknowledgement of {DescribeStage(stage)}; DAD will not redispatch it.",
                now,
                snapshot,
                shouldAudit: false);
        }

        if (!snapshot.SafeToMutate)
        {
            var summary = string.IsNullOrWhiteSpace(snapshot.SafetyBlocker)
                ? "Waiting for safe Party Finder mutation conditions."
                : snapshot.SafetyBlocker;
            return Result(
                DadAlliancePfCreateResultKind.Waiting,
                "readiness",
                summary,
                now,
                snapshot,
                shouldAudit: true);
        }

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
        actionDispatched = false;
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
            DadAlliancePfCreateStage.OpenMainWindow when
                actionDispatched &&
                snapshot.MainReady &&
                snapshot.MainRecruitUsable
                => DadAlliancePfCreateStage.OpenConditions,
            DadAlliancePfCreateStage.OpenConditions when
                actionDispatched &&
                snapshot.ConditionReady
                => DadAlliancePfCreateStage.SelectAlliance,
            DadAlliancePfCreateStage.SelectAlliance when
                actionDispatched &&
                snapshot.GroupTypeTab ==
                    DadAlliancePartyFinderPresetDefinition.AllianceGroupTypeTab &&
                snapshot.AllianceSelected
                => DadAlliancePfCreateStage.SelectRaids,
            DadAlliancePfCreateStage.SelectAlliance when
                actionDispatched &&
                snapshot.GroupTypeTab ==
                    DadAlliancePartyFinderPresetDefinition.AllianceGroupTypeTab
                => DadAlliancePfCreateStage.ReloadCloseConditions,
            DadAlliancePfCreateStage.ReloadCloseConditions when
                actionDispatched &&
                !snapshot.ConditionVisible
                => DadAlliancePfCreateStage.ReloadMainWindow,
            DadAlliancePfCreateStage.ReloadMainWindow when
                snapshot.MainReady &&
                snapshot.MainRecruitUsable
                => DadAlliancePfCreateStage.ReloadOpenConditions,
            DadAlliancePfCreateStage.ReloadOpenConditions when
                actionDispatched &&
                snapshot.ConditionReady &&
                snapshot.GroupTypeTab ==
                    DadAlliancePartyFinderPresetDefinition.AllianceGroupTypeTab &&
                snapshot.AllianceSelected
                => DadAlliancePfCreateStage.SelectRaids,
            DadAlliancePfCreateStage.SelectRaids when
                actionDispatched &&
                snapshot.SelectedCategory == RaidsCategoryMask
                => DadAlliancePfCreateStage.SelectDuty,
            DadAlliancePfCreateStage.SelectDuty when
                actionDispatched &&
                HasPreparedGameOwnedSelector(snapshot)
                => DadAlliancePfCreateStage.ApplyPreset,
            DadAlliancePfCreateStage.ApplyPreset when
                actionDispatched &&
                HasExactPresetAcknowledgement(snapshot, passcode)
                => DadAlliancePfCreateStage.Submit,
            _ => stage,
        };

        if (next == stage)
            return null;

        var prior = stage;
        stage = next;
        actionAttempt = 0;
        actionDispatched = false;
        nextActionUtc = DateTime.MinValue;
        lastError = string.Empty;
        if (next == DadAlliancePfCreateStage.Submit)
            presetAcknowledged = true;
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
        if (stage is DadAlliancePfCreateStage.SelectDuty or
            DadAlliancePfCreateStage.ApplyPreset)
        {
            if (snapshot.TargetDutySheetMatches != 1)
                return Block(now, $"Expected one ContentFinderCondition match for The Labyrinth of the Ancients; found {snapshot.TargetDutySheetMatches}.", snapshot);
            if (snapshot.TargetDutyId != LabyrinthDutyId)
                return Block(now, $"The Labyrinth of the Ancients resolved to duty ID {snapshot.TargetDutyId} instead of {LabyrinthDutyId}.", snapshot);
        }
        if (stage == DadAlliancePfCreateStage.SelectDuty &&
            (snapshot.TargetDutyDropDownMatches != 1 ||
             !snapshot.TargetDutyEntryEnabled ||
             snapshot.TargetDutyDropDownIndex < 0))
        {
            return Block(
                now,
                "The exact enabled Labyrinth duty row is unavailable; DAD will not dispatch duty selection.",
                snapshot);
        }
        if (stage == DadAlliancePfCreateStage.ApplyPreset &&
            !HasPreparedGameOwnedSelector(snapshot))
        {
            return Block(
                now,
                "The game-owned API-15 Alliance/Raids/Labyrinth selector changed before capture.",
                snapshot);
        }

        var action = stage switch
        {
            DadAlliancePfCreateStage.CloseStaleWindows => DadAlliancePfCreateAction.CloseStaleWindows,
            DadAlliancePfCreateStage.OpenMainWindow => DadAlliancePfCreateAction.OpenMainWindow,
            DadAlliancePfCreateStage.OpenConditions => DadAlliancePfCreateAction.OpenConditions,
            DadAlliancePfCreateStage.SelectAlliance => DadAlliancePfCreateAction.SelectAlliance,
            DadAlliancePfCreateStage.ReloadCloseConditions =>
                DadAlliancePfCreateAction.ReloadCloseConditions,
            DadAlliancePfCreateStage.ReloadMainWindow =>
                DadAlliancePfCreateAction.ReloadMainWindow,
            DadAlliancePfCreateStage.ReloadOpenConditions =>
                DadAlliancePfCreateAction.ReloadOpenConditions,
            DadAlliancePfCreateStage.SelectRaids => DadAlliancePfCreateAction.SelectRaids,
            DadAlliancePfCreateStage.SelectDuty => DadAlliancePfCreateAction.SelectDuty,
            DadAlliancePfCreateStage.ApplyPreset => DadAlliancePfCreateAction.ApplyPreset,
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
            return Block(
                now,
                $"{DescribeStage(stage)} threw before acknowledgement; DAD will not redispatch it: {exception.Message}",
                snapshot);
        }

        if (!actionResult.Sent)
        {
            lastError = string.IsNullOrWhiteSpace(actionResult.Error)
                ? actionResult.Summary
                : actionResult.Error;
            return Block(
                now,
                $"{actionResult.Summary} DAD will not redispatch this Create request. {lastError}".Trim(),
                snapshot);
        }

        actionDispatched = true;
        nextActionUtc = now + ObservationTimeout;
        lastError = string.Empty;
        if (stage == DadAlliancePfCreateStage.Submit)
            submitDispatched = true;
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
            DadAlliancePfCreateStage.CloseStaleWindows when
                snapshot.ConditionVisible &&
                !snapshot.ConditionReady
                => "Waiting for stale Party Finder conditions to become closable before the one allowed close action.",
            DadAlliancePfCreateStage.OpenMainWindow when snapshot.MainVisible &&
                                                         (!snapshot.MainReady || !snapshot.MainRecruitUsable)
                => "Waiting for the visible Party Finder window and Recruit Members control to become fully usable.",
            DadAlliancePfCreateStage.OpenConditions when
                !snapshot.MainReady ||
                !snapshot.MainRecruitUsable
                => "Waiting for the typed Recruit Members control before its one allowed opening action.",
            DadAlliancePfCreateStage.SelectAlliance or
                DadAlliancePfCreateStage.SelectRaids or
                DadAlliancePfCreateStage.SelectDuty or
                DadAlliancePfCreateStage.ApplyPreset
                when !snapshot.ConditionReady
                => "Waiting for Party Finder conditions to become ready.",
            DadAlliancePfCreateStage.ReloadCloseConditions when
                !snapshot.ConditionReady
                => "Waiting for the Alliance conditions editor to become ready for its one allowed typed Cancel action.",
            DadAlliancePfCreateStage.ReloadMainWindow when
                snapshot.MainVisible &&
                (!snapshot.MainReady || !snapshot.MainRecruitUsable)
                => "Waiting for the retained Party Finder window to become fully usable before reopening conditions.",
            DadAlliancePfCreateStage.ReloadOpenConditions when
                !snapshot.MainReady ||
                !snapshot.MainRecruitUsable
                => "Waiting for the retained or reopened Party Finder window before the one allowed conditions reopen.",
            DadAlliancePfCreateStage.SelectDuty when
                !snapshot.DutyListLoaded ||
                snapshot.TargetDutyDropDownMatches != 1 ||
                !snapshot.TargetDutyEntryEnabled ||
                snapshot.TargetDutyDropDownIndex < 0
                => "Waiting for one exact enabled Labyrinth duty row before its one allowed selection event.",
            _ => string.Empty,
        };

    private DadAlliancePfCreateResult Block(
        DateTime now,
        string error,
        DadAlliancePfCreateSnapshot? snapshot)
    {
        lastError = error;
        stage = DadAlliancePfCreateStage.Blocked;
        actionDispatched = false;
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
            BuildDiagnosticReadiness(snapshot, readiness, submitDispatched),
            snapshot?.SelectedCategory ?? 0,
            snapshot?.SelectedDutyId ?? 0,
            snapshot?.OwnerHandle ?? 0,
            checked((int)Math.Clamp((now - startedAtUtc).TotalMilliseconds, 0, int.MaxValue)),
            snapshot?.ActiveRecruitment ?? false,
            snapshot?.ConditionVisible ?? false,
            submitDispatched,
            string.Empty,
            BuildObservedSettings(snapshot),
            shouldAudit);

    private static string DescribeStage(DadAlliancePfCreateStage value)
        => value switch
        {
            DadAlliancePfCreateStage.CloseStaleWindows => "closing stale Party Finder windows",
            DadAlliancePfCreateStage.OpenMainWindow => "opening Party Finder",
            DadAlliancePfCreateStage.OpenConditions => "opening recruitment conditions",
            DadAlliancePfCreateStage.SelectAlliance =>
                "preparing the game-owned Alliance selector",
            DadAlliancePfCreateStage.ReloadCloseConditions =>
                "closing the stale Alliance conditions editor",
            DadAlliancePfCreateStage.ReloadMainWindow =>
                "ensuring Party Finder is open for the Alliance editor reload",
            DadAlliancePfCreateStage.ReloadOpenConditions =>
                "reopening the Alliance conditions editor",
            DadAlliancePfCreateStage.SelectRaids =>
                "preparing the game-owned Raids selector",
            DadAlliancePfCreateStage.SelectDuty =>
                "preparing the game-owned Labyrinth selector",
            DadAlliancePfCreateStage.ApplyPreset => "loading the exact DAD-owned Alliance preset",
            DadAlliancePfCreateStage.Submit => "submitting and acknowledging recruitment",
            DadAlliancePfCreateStage.Complete => "complete",
            DadAlliancePfCreateStage.Stopped => "stopped",
            DadAlliancePfCreateStage.Blocked => "blocked",
            _ => value.ToString(),
        };

    private static bool HasExactPresetAcknowledgement(
        DadAlliancePfCreateSnapshot snapshot,
        int passcode)
        => snapshot.PresetLoaderAvailable &&
           snapshot.GroupTypeTab ==
               DadAlliancePartyFinderPresetDefinition.AllianceGroupTypeTab &&
           snapshot.AllianceSelected &&
           snapshot.AllianceASelected &&
           snapshot.SelectedCategory == RaidsCategoryMask &&
           snapshot.TargetDutySheetMatches == 1 &&
           snapshot.TargetDutyId == LabyrinthDutyId &&
           IsTargetDutyVisiblySelected(snapshot) &&
           snapshot.SelectedDutyId == LabyrinthDutyId &&
           snapshot.PrivateRecruitment &&
           snapshot.StoredPrivateRecruitment &&
           snapshot.Passcode == passcode &&
           snapshot.StoredPasscode == passcode &&
           snapshot.CrossWorldRecruitment &&
           snapshot.StoredCrossWorldRecruitment &&
           !snapshot.OnePlayerPerJob &&
           !snapshot.StoredOnePlayerPerJob &&
           snapshot.EmptyComment &&
           snapshot.StoredEmptyComment &&
           snapshot.StoredOpenSlotsUnrestricted &&
           snapshot.StoredStaleMembersCleared &&
           snapshot.NumberOfGroups == 3 &&
           snapshot.SlotsPerGroup == 8 &&
           snapshot.StoredSettingsExactBeforeSubmit;

    private static bool IsTargetDutyVisiblySelected(
        DadAlliancePfCreateSnapshot snapshot)
        => snapshot.TargetDutyDropDownMatches == 1 &&
           snapshot.TargetDutyEntryEnabled &&
           snapshot.TargetDutyDropDownIndex >= 0 &&
           snapshot.SelectedDutyDropDownIndex == snapshot.TargetDutyDropDownIndex;

    private static bool HasPreparedGameOwnedSelector(
        DadAlliancePfCreateSnapshot snapshot)
        => snapshot.GroupTypeTab ==
               DadAlliancePartyFinderPresetDefinition.AllianceGroupTypeTab &&
           snapshot.AllianceSelected &&
           snapshot.SelectedCategory == RaidsCategoryMask &&
           snapshot.TargetDutySheetMatches == 1 &&
           snapshot.TargetDutyId == LabyrinthDutyId &&
           IsTargetDutyVisiblySelected(snapshot) &&
           snapshot.SelectedDutyId == LabyrinthDutyId;

    private string BuildObservationTimeoutError(
        DadAlliancePfCreateSnapshot snapshot)
        => $"Party Finder did not acknowledge {DescribeStage(stage)} within " +
           $"{ObservationTimeout.TotalSeconds:0} seconds after its single dispatch. " +
           $"DAD will not open another editor, resend a callback/event, rewrite the preset, " +
           $"refresh, or Submit on this Create request; press Create again explicitly. " +
           $"Observed category 0x{snapshot.SelectedCategory:X}, target index " +
           $"{snapshot.TargetDutyDropDownIndex}, visible index " +
           $"{snapshot.SelectedDutyDropDownIndex}, stored duty " +
           $"{snapshot.SelectedDutyId}, groups {snapshot.NumberOfGroups}.";

    private static string BuildDiagnosticReadiness(
        DadAlliancePfCreateSnapshot? snapshot,
        string readiness,
        bool submitWasDispatched)
    {
        if (snapshot == null)
            return readiness;

        var prefix = string.IsNullOrWhiteSpace(readiness)
            ? snapshot.Readiness
            : readiness;
        if (!string.IsNullOrWhiteSpace(prefix))
            prefix += "; ";
        return prefix +
               $"active-recruitment={snapshot.ActiveRecruitment}; " +
               $"editor-visible={snapshot.ConditionVisible}; " +
               $"submit-dispatched={submitWasDispatched}; " +
               $"duty-target-index={snapshot.TargetDutyDropDownIndex}; " +
               $"duty-visible-index={snapshot.SelectedDutyDropDownIndex}; " +
               $"duty-stored-id={snapshot.SelectedDutyId}; " +
               $"owner-handle={snapshot.OwnerHandle}; " +
               BuildObservedSettings(snapshot);
    }

    private static string BuildObservedSettings(
        DadAlliancePfCreateSnapshot? snapshot)
    {
        if (snapshot == null)
            return string.Empty;

        return
            $"preset-loader={snapshot.PresetLoaderAvailable}; group-type-tab={snapshot.GroupTypeTab}; " +
            $"alliance-tab={snapshot.AllianceSelected}; alliance-a={snapshot.AllianceASelected}; " +
            $"private-visible={snapshot.PrivateRecruitment}; private-stored={snapshot.StoredPrivateRecruitment}; " +
            $"passcode-visible={snapshot.Passcode}; passcode-stored={snapshot.StoredPasscode}; " +
            $"cross-world-visible={snapshot.CrossWorldRecruitment}; cross-world-stored={snapshot.StoredCrossWorldRecruitment}; " +
            $"one-player-per-job-visible={snapshot.OnePlayerPerJob}; one-player-per-job-stored={snapshot.StoredOnePlayerPerJob}; " +
            $"empty-comment-visible={snapshot.EmptyComment}; empty-comment-stored={snapshot.StoredEmptyComment}; " +
            $"unrestricted-visible={snapshot.UnrestrictedJobs}; unrestricted-open-slot-flags={snapshot.StoredOpenSlotsUnrestricted}; " +
            $"stale-members-cleared={snapshot.StoredStaleMembersCleared}; " +
            $"groups={snapshot.NumberOfGroups}; slots-per-group={snapshot.SlotsPerGroup}";
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
