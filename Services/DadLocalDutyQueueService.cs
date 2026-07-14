using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using dad.Models;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace dad.Services;

public enum DadLocalDutyQueuePulseKind
{
    Waiting,
    SetUnrestrictedParty,
    OpenedDutyFinder,
    ClearedDutySelection,
    SelectedDuty,
    CheckedDuty,
    RegisteredForDuty,
    AcceptedQueueConfirm,
    WaitingForQueue,
    DutyEntryTransition,
    EnteredDuty,
    Failed,
    Cancelled,
}

public sealed class DadLocalDutyResolvedContent
{
    public DadModuleId ModuleId { get; set; } = DadModuleId.Duty;
    public string LaneDisplayName { get; set; } = "Local Duty";
    public DadQueueTargetKind TargetKind { get; set; } = DadQueueTargetKind.DutyFinderDuty;
    public uint ContentFinderConditionId { get; set; }
    public uint RouletteId { get; set; }
    public uint TerritoryType { get; set; }
    public string DutyName { get; set; } = string.Empty;
    public string SheetDutyName { get; set; } = string.Empty;
    public bool Unsynced { get; set; }
    public bool AllowUndersized { get; set; }
    public bool IsHighEndDuty { get; set; }
    public int QueueSize { get; set; } = 1;
    public int ExpectedPartySize { get; set; } = 1;
}

public sealed class DadLocalDutyQueuePulse
{
    public DadLocalDutyQueuePulseKind Kind { get; set; } = DadLocalDutyQueuePulseKind.Waiting;
    public DadRunPhase Phase { get; set; } = DadRunPhase.QueuePreparing;
    public DadRunStatus Status { get; set; } = DadRunStatus.Running;
    public DadParticipantState ParticipantState { get; set; } = DadParticipantState.QueuePending;
    public bool Success { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public string Summary { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public string BlockedReason { get; set; } = string.Empty;
    public List<DadModuleBlockerDto> Blockers { get; set; } = [];
}

public sealed unsafe class DadLocalDutyQueueService : IDisposable
{
    private static readonly TimeSpan OpenThrottle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SelectThrottle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RegisterThrottle = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConfirmThrottle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RestoreRetryThrottle = TimeSpan.FromSeconds(1);

    private readonly IPluginLog log;
    private readonly Func<DadParticipantSnapshot>? liveSafetySnapshotBuilder;
    private DateTime nextOpenAttemptUtc = DateTime.MinValue;
    private DateTime nextSelectAttemptUtc = DateTime.MinValue;
    private DateTime nextRegisterAttemptUtc = DateTime.MinValue;
    private DateTime nextConfirmAttemptUtc = DateTime.MinValue;
    private DateTime lastDutyCompletedUtc = DateTime.MinValue;
    private uint lastDutyCompletedTerritoryId;
    private string activeRunId = string.Empty;
    private bool dutyStateSubscribed;
    private bool frameworkUpdateSubscribed;
    private bool unrestrictedRestorePending;
    private DateTime nextUnrestrictedRestoreAttemptUtc = DateTime.MinValue;
    private bool dutyEntryEvidenceObserved;
    private bool dutyEntryTransitionLogged;
    private bool transientMissingPlayerLogged;

    // Review M7: remember and restore the Duty Finder's unrestricted/unsynced flag so an unsynced
    // Dad run doesn't leak that setting into the player's later manual or synced queues.
    private readonly DadUnrestrictedPartyOverrideLease unrestrictedPartyLease = new();
    private readonly DadRouletteQueueAttemptGate rouletteAttemptGate = new();
    private readonly DadRouletteTerritoryEvidenceGate rouletteTerritoryGate = new();
    private readonly DadRouletteTerritoryEvidenceGate participantRouletteTerritoryGate = new();
    private readonly DadDutyFinderStableMappingGate liveEntryMappingGate = new();
    private bool dutySelectionCleared;
    private bool dutyListHydrated;
    private ulong hydratedDutyFinderCharacterContentId;
    private DadDutyFinderSelectionToken? lastSelectionToken;
    private string lastMappingTransition = string.Empty;
    private string participantObserverRunId = string.Empty;
    private bool participantDutyEntryEvidenceObserved;

    public DadLocalDutyQueueService(
        IPluginLog log,
        Func<DadParticipantSnapshot>? liveSafetySnapshotBuilder = null)
    {
        this.log = log;
        this.liveSafetySnapshotBuilder = liveSafetySnapshotBuilder;
        TrySubscribeDutyState();
        TrySubscribeFrameworkUpdate();
    }

    public void Dispose()
    {
        RestoreUnrestrictedParty();
        if (frameworkUpdateSubscribed)
        {
            try
            {
                Plugin.Framework.Update -= OnFrameworkUpdate;
            }
            catch
            {
                // Best-effort plugin shutdown only.
            }

            frameworkUpdateSubscribed = false;
        }

        if (!dutyStateSubscribed)
            return;

        try
        {
            Plugin.DutyState.DutyCompleted -= OnDutyCompleted;
        }
        catch
        {
            // Best-effort plugin shutdown only.
        }
    }

    public DadLocalDutyResolvedContent? Resolve(DadDungeonTask? task, out string blocker)
    {
        if (task == null)
        {
            blocker = "No Local Duty task exists in this request.";
            return null;
        }

        if (task.QueueViaLanParty)
        {
            blocker = "Local Duty executor does not handle premade/LAN party queue requests.";
            return null;
        }

        return ResolveRegularDutySelection(
            task.ContentFinderConditionId,
            task.SelectedDungeon,
            task.Unsynced,
            expectedPartySize: 1,
            moduleId: DadModuleId.Duty,
            laneDisplayName: "Local Duty",
            enforcePremadePartySize: false,
            out blocker);
    }

    public DadLocalDutyResolvedContent? Resolve(DadPremadeDutyTask? task, out string blocker)
    {
        if (task == null)
        {
            blocker = "No Premade Duty task exists in this request.";
            return null;
        }

        return ResolveRegularDutySelection(
            task.ContentFinderConditionId,
            task.DutyName,
            task.Unsynced,
            task.ExpectedPartySize,
            DadModuleId.PremadeDuty,
            "Premade Duty",
            enforcePremadePartySize: true,
            out blocker);
    }

    public DadLocalDutyResolvedContent? Resolve(DadDailyMsqTask? task, out string blocker)
    {
        if (task == null)
        {
            blocker = "No Daily Roulette task exists in this request.";
            return null;
        }

        var options = new DadRouletteCatalogService(Plugin.DataManager).GetOptions();
        var resolution = DadDailyRoulettePlannerRules.ResolveTarget(task.QueueTarget, options);
        if (!resolution.IsAvailable || resolution.Option == null)
        {
            blocker = string.IsNullOrWhiteSpace(resolution.Blocker)
                ? "Daily Roulette target is unavailable."
                : resolution.Blocker;
            return null;
        }

        task.QueueTarget = resolution.Target.Clone();
        blocker = string.Empty;
        return new DadLocalDutyResolvedContent
        {
            ModuleId = DadModuleId.DailyMsq,
            LaneDisplayName = "Daily Roulette",
            TargetKind = DadQueueTargetKind.Roulette,
            RouletteId = resolution.Option.RouletteId,
            DutyName = resolution.Option.DisplayName,
            SheetDutyName = resolution.Option.DisplayName,
            Unsynced = false,
            QueueSize = DadDailyRoulettePlannerRules.RequiredPartySize,
            ExpectedPartySize = DadDailyRoulettePlannerRules.RequiredPartySize,
        };
    }

    public DadLocalDutyResolvedContent? ResolvePremade(DadDungeonTask? task, out string blocker)
    {
        if (task == null)
        {
            blocker = "No premade dungeon task exists in this request.";
            return null;
        }

        if (!task.QueueViaLanParty)
        {
            blocker = "Premade Duty executor only handles premade/LAN party queue requests.";
            return null;
        }

        return ResolveRegularDutySelection(
            task.ContentFinderConditionId,
            task.SelectedDungeon,
            task.Unsynced,
            expectedPartySize: 4,
            moduleId: DadModuleId.PremadeDuty,
            laneDisplayName: "Premade Duty",
            enforcePremadePartySize: true,
            out blocker);
    }

    public bool CanStart(DadLocalDutyResolvedContent? content, out string blocker)
    {
        blocker = string.Empty;
        if (content == null)
        {
            blocker = "Regular Duty Finder queue requires a resolved content selection.";
            return false;
        }

        if (content.TargetKind == DadQueueTargetKind.Roulette &&
            (content.RouletteId is 0 or > byte.MaxValue ||
             content.Unsynced ||
             content.ExpectedPartySize != DadDailyRoulettePlannerRules.RequiredPartySize))
        {
            blocker = "Daily Roulette requires an exact roulette id in 1..255, synced queueing, and exactly four participants.";
            return false;
        }

        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
        {
            blocker = $"{content.LaneDisplayName} queue requires a logged-in local player.";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.BoundByDuty])
        {
            blocker = $"Already bound by a duty; {content.LaneDisplayName} cannot start another queue.";
            return false;
        }

        if (IsQueued())
        {
            blocker = $"Already in a Duty Finder queue; cancel or finish that queue before starting {content.LaneDisplayName}.";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            blocker = $"{content.LaneDisplayName} cannot start while the client is between areas.";
            return false;
        }

        try
        {
            if (ContentsFinder.Instance() == null)
            {
                blocker = "ContentsFinder runtime state is unavailable.";
                return false;
            }

            if (AgentContentsFinder.Instance() == null)
            {
                blocker = "AgentContentsFinder is unavailable.";
                return false;
            }

            var confirmAddon = RaptureAtkUnitManager.Instance()->GetAddonByName("ContentsFinderConfirm");
            if (confirmAddon != null && confirmAddon->IsVisible)
            {
                blocker = $"A Duty Finder commence popup is already active; resolve it before starting {content.LaneDisplayName}.";
                return false;
            }

            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("ContentsFinder");
            var dutyFinderAlreadyOpen = addon != null && addon->IsVisible;
            var hud = AgentHUD.Instance();
            if (!dutyFinderAlreadyOpen && (hud == null || !hud->IsMainCommandEnabled(33)))
            {
                blocker = "Duty Finder main command is unavailable in the current client state.";
                return false;
            }
        }
        catch (Exception ex)
        {
            blocker = $"Duty Finder runtime readiness check failed: {ex.Message}";
            return false;
        }

        return true;
    }

    public DadLocalDutyQueuePulse Pulse(string runId, DadLocalDutyResolvedContent content)
    {
        // Review M16: mutual exclusion on the shared queue — refuse a different run while one owns it, so the
        // internal orchestrator and the external dad.Duty.* IPC path can't drive the same queue at once.
        if (!string.IsNullOrEmpty(activeRunId) && !string.Equals(activeRunId, runId, StringComparison.OrdinalIgnoreCase))
        {
            return Failed(
                content,
                $"Local Duty queue is owned by another run ({activeRunId}).",
                cleanup: false);
        }

        ResetForNewRun(runId);

        var commonPulse = BuildCommonQueuePulse(content);
        if (commonPulse != null)
            return commonPulse;

        return content.TargetKind == DadQueueTargetKind.Roulette
            ? PulseRoulette(content)
            : PulseRegularDuty(content);
    }

    public DadLocalDutyQueuePulse ObserveParticipant(string runId, DadLocalDutyResolvedContent content)
    {
        if (!string.Equals(participantObserverRunId, runId, StringComparison.OrdinalIgnoreCase))
        {
            participantObserverRunId = runId;
            participantDutyEntryEvidenceObserved = false;
            participantRouletteTerritoryGate.Reset();
            nextConfirmAttemptUtc = DateTime.MinValue;
        }

        // This is intentionally separate from BuildCommonQueuePulse/ResetForNewRun. Participant
        // follow-through may observe truth and accept commence, but cannot restore/alter sync state,
        // open Duty Finder, select a duty, or register a queue.
        var isLoggedIn = Plugin.ClientState.IsLoggedIn;
        var hasLocalPlayer = Plugin.ObjectTable.LocalPlayer != null;
        var territoryType = Plugin.ClientState.TerritoryType;
        var isQueued = IsQueued();
        var isBoundByDuty = Plugin.Condition[ConditionFlag.BoundByDuty];
        var isBetweenAreas = Plugin.Condition[ConditionFlag.BetweenAreas];
        var isBetweenAreas51 = Plugin.Condition[ConditionFlag.BetweenAreas51];
        var isRoulette = content.TargetKind == DadQueueTargetKind.Roulette;
        var isRequestedTerritory = !isRoulette && territoryType == content.TerritoryType;
        var qualifyingRouletteTransition = isRoulette && (isBetweenAreas || isBetweenAreas51);

        if (isRoulette && isQueued)
        {
            participantDutyEntryEvidenceObserved = true;
            participantRouletteTerritoryGate.ObserveEntryEvidence();
        }

        if (qualifyingRouletteTransition)
        {
            participantDutyEntryEvidenceObserved = true;
            participantRouletteTerritoryGate.ObserveEntryEvidence();
            return Active(content, DadLocalDutyQueuePulseKind.DutyEntryTransition, DadRunPhase.WaitingForQueuePop, DadParticipantState.QueuePending, $"Participant observed duty-entry transition for {content.DutyName}; waiting for stable bound-duty territory truth.");
        }

        if (isRoulette && isBoundByDuty)
        {
            if (!isLoggedIn || !hasLocalPlayer)
                return Active(content, DadLocalDutyQueuePulseKind.DutyEntryTransition, DadRunPhase.WaitingForQueuePop, DadParticipantState.QueuePending, $"Participant entry transition for {content.DutyName}; waiting for local player truth.");

            if (!participantRouletteTerritoryGate.TryCapture(true, territoryType))
            {
                return Failed(
                    content,
                    participantRouletteTerritoryGate.EntryEvidenceObserved
                        ? $"Participant entered roulette territory {territoryType}, but the captured roulette territory is {participantRouletteTerritoryGate.CapturedTerritoryId}."
                        : $"Participant became bound by territory {territoryType} before this Daily Roulette observed queue/commence/transition evidence.",
                    cleanup: false);
            }

            content.TerritoryType = participantRouletteTerritoryGate.CapturedTerritoryId;
            participantDutyEntryEvidenceObserved = true;
            return Active(content, DadLocalDutyQueuePulseKind.EnteredDuty, DadRunPhase.InDutyOrTask, DadParticipantState.Running, $"Participant entered Daily Roulette {content.DutyName} territory {content.TerritoryType}.");
        }

        if (isBoundByDuty && isRequestedTerritory)
        {
            participantDutyEntryEvidenceObserved = true;
            return Active(content, DadLocalDutyQueuePulseKind.EnteredDuty, DadRunPhase.InDutyOrTask, DadParticipantState.Running, $"Participant entered {content.LaneDisplayName} {content.DutyName}.");
        }

        if (!isRoulette && IsDutyEntryTransition(
                isBetweenAreas,
                isBetweenAreas51,
                isRequestedTerritory,
                isQueued || participantDutyEntryEvidenceObserved))
        {
            participantDutyEntryEvidenceObserved = true;
            return Active(content, DadLocalDutyQueuePulseKind.DutyEntryTransition, DadRunPhase.WaitingForQueuePop, DadParticipantState.QueuePending, $"Participant observed duty-entry transition for {content.DutyName}.");
        }

        if (isBoundByDuty)
            return Failed(content, $"Participant is bound by another duty in territory {territoryType}; expected {content.DutyName}.", cleanup: false);

        if (TryAcceptContentsFinderConfirm(content))
        {
            participantDutyEntryEvidenceObserved = true;
            if (isRoulette)
                participantRouletteTerritoryGate.ObserveEntryEvidence();
            return Active(content, DadLocalDutyQueuePulseKind.AcceptedQueueConfirm, DadRunPhase.QueueStarting, DadParticipantState.QueuePending, $"Participant accepted Duty Finder commence popup for {content.DutyName}.");
        }

        if (isQueued)
        {
            participantDutyEntryEvidenceObserved = true;
            return Active(content, DadLocalDutyQueuePulseKind.WaitingForQueue, DadRunPhase.WaitingForQueuePop, DadParticipantState.QueuePending, $"Participant observes active queue for {content.DutyName}; waiting for commence or duty entry.");
        }

        if ((!isLoggedIn || !hasLocalPlayer) && participantDutyEntryEvidenceObserved)
            return Active(content, DadLocalDutyQueuePulseKind.DutyEntryTransition, DadRunPhase.WaitingForQueuePop, DadParticipantState.QueuePending, $"Participant entry transition for {content.DutyName}; waiting for local player truth.");

        if (!isLoggedIn || !hasLocalPlayer)
            return Failed(content, $"Participant queue observer requires a logged-in local player for {content.DutyName}.", cleanup: false);

        return Active(
            content,
            DadLocalDutyQueuePulseKind.Waiting,
            DadRunPhase.WaitingForQueuePop,
            DadParticipantState.QueuePending,
            $"Participant observe-only wait for {content.DutyName}; queue leader owns Duty Finder registration.");
    }

    public void ResetParticipantObserver(string runId)
    {
        if (!string.Equals(participantObserverRunId, runId, StringComparison.OrdinalIgnoreCase))
            return;

        participantObserverRunId = string.Empty;
        participantDutyEntryEvidenceObserved = false;
        participantRouletteTerritoryGate.Reset();
        nextConfirmAttemptUtc = DateTime.MinValue;
    }

    public DadLocalDutyQueuePulse Cancel(string runId, string reason)
    {
        if (string.Equals(activeRunId, runId, StringComparison.OrdinalIgnoreCase))
            ClearRunState();

        return new DadLocalDutyQueuePulse
        {
            Kind = DadLocalDutyQueuePulseKind.Cancelled,
            Phase = DadRunPhase.Finalizing,
            Status = DadRunStatus.Cancelled,
            ParticipantState = DadParticipantState.Cancelled,
            Success = false,
            IsActive = false,
            Summary = string.IsNullOrWhiteSpace(reason) ? "Local Duty queue executor cancelled." : reason,
            FailureReason = string.IsNullOrWhiteSpace(reason) ? "Cancelled." : reason,
        };
    }

    public void ResetRun(string runId)
    {
        if (!string.IsNullOrWhiteSpace(activeRunId) &&
            !string.Equals(activeRunId, runId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ClearRunState();
    }

    public bool IsInRequestedDuty(DadLocalDutyResolvedContent content)
        => content.TargetKind == DadQueueTargetKind.Roulette
            ? rouletteTerritoryGate.IsInCapturedDuty(
                Plugin.Condition[ConditionFlag.BoundByDuty],
                Plugin.ClientState.TerritoryType)
            : Plugin.Condition[ConditionFlag.BoundByDuty] &&
              Plugin.ClientState.TerritoryType == content.TerritoryType;

    public bool HasDutyCompleted(DadLocalDutyResolvedContent content, DateTime runStartedAtUtc)
        => content.TargetKind == DadQueueTargetKind.Roulette
            ? rouletteTerritoryGate.MatchesCompletion(
                lastDutyCompletedTerritoryId,
                lastDutyCompletedUtc,
                runStartedAtUtc)
            : lastDutyCompletedUtc >= runStartedAtUtc &&
              lastDutyCompletedTerritoryId == content.TerritoryType;

    public bool IsQueued()
        => Plugin.Condition[ConditionFlag.InDutyQueue] ||
           Plugin.Condition[ConditionFlag.WaitingForDuty] ||
           Plugin.Condition[ConditionFlag.WaitingForDutyFinder] ||
           IsContentsFinderQueueStateActive();

    private DadLocalDutyQueuePulse? BuildCommonQueuePulse(DadLocalDutyResolvedContent content)
    {
        var isLoggedIn = Plugin.ClientState.IsLoggedIn;
        var hasLocalPlayer = Plugin.ObjectTable.LocalPlayer != null;
        var territoryType = Plugin.ClientState.TerritoryType;
        var isQueued = IsQueued();
        var isBoundByDuty = Plugin.Condition[ConditionFlag.BoundByDuty];
        var isBetweenAreas = Plugin.Condition[ConditionFlag.BetweenAreas];
        var isBetweenAreas51 = Plugin.Condition[ConditionFlag.BetweenAreas51];
        var isRoulette = content.TargetKind == DadQueueTargetKind.Roulette;
        var isRequestedTerritory = !isRoulette && territoryType == content.TerritoryType;

        if (isRoulette && isQueued)
        {
            dutyEntryEvidenceObserved = true;
            rouletteTerritoryGate.ObserveEntryEvidence();
        }

        if (TryAcceptContentsFinderConfirm(content))
        {
            dutyEntryEvidenceObserved = true;
            if (isRoulette)
                rouletteTerritoryGate.ObserveEntryEvidence();
            return Active(content, DadLocalDutyQueuePulseKind.AcceptedQueueConfirm, DadRunPhase.QueueStarting, DadParticipantState.QueuePending, $"Accepted Duty Finder commence popup for {content.DutyName}.");
        }

        var qualifyingRouletteTransition = isRoulette && (isBetweenAreas || isBetweenAreas51);
        if (qualifyingRouletteTransition)
        {
            dutyEntryEvidenceObserved = true;
            rouletteTerritoryGate.ObserveEntryEvidence();
            return DutyEntryTransition(content, isLoggedIn, hasLocalPlayer, territoryType, isQueued, isBoundByDuty, isBetweenAreas, isBetweenAreas51);
        }

        if (isRoulette && isBoundByDuty)
        {
            if (!isLoggedIn || !hasLocalPlayer)
                return DutyEntryTransition(content, isLoggedIn, hasLocalPlayer, territoryType, isQueued, true, isBetweenAreas, isBetweenAreas51);

            if (!rouletteTerritoryGate.TryCapture(true, territoryType))
            {
                return Failed(
                    content,
                    rouletteTerritoryGate.EntryEvidenceObserved
                        ? $"Daily Roulette entered territory {territoryType}, but captured territory is {rouletteTerritoryGate.CapturedTerritoryId}."
                        : $"Became bound by territory {territoryType} before this Daily Roulette observed queue/commence/transition evidence.");
            }

            content.TerritoryType = rouletteTerritoryGate.CapturedTerritoryId;
            dutyEntryEvidenceObserved = true;
            RestoreUnrestrictedParty();
            return Active(content, DadLocalDutyQueuePulseKind.EnteredDuty, DadRunPhase.InDutyOrTask, DadParticipantState.Running, $"Entered Daily Roulette {content.DutyName} territory {content.TerritoryType}.");
        }

        if (isBoundByDuty && isRequestedTerritory)
        {
            dutyEntryEvidenceObserved = true;
            // Review M7: queue purpose fulfilled (in the duty) — restore the Duty Finder unsync flag.
            RestoreUnrestrictedParty();
            return Active(content, DadLocalDutyQueuePulseKind.EnteredDuty, DadRunPhase.InDutyOrTask, DadParticipantState.Running, $"Entered {content.LaneDisplayName} {content.DutyName}.");
        }

        if (!isRoulette && IsDutyEntryTransition(isBetweenAreas, isBetweenAreas51, isRequestedTerritory, isQueued || dutyEntryEvidenceObserved))
            return DutyEntryTransition(content, isLoggedIn, hasLocalPlayer, territoryType, isQueued, isBoundByDuty, isBetweenAreas, isBetweenAreas51);

        if (isBoundByDuty)
        {
            if (!isLoggedIn || !hasLocalPlayer)
                return DutyEntryTransition(content, isLoggedIn, hasLocalPlayer, territoryType, isQueued, true, isBetweenAreas, isBetweenAreas51);

            return Failed(content, $"Already bound by another duty in territory {territoryType}; cannot start {content.DutyName}.");
        }

        if (isQueued && (!isLoggedIn || !hasLocalPlayer))
            return DutyEntryTransition(content, isLoggedIn, hasLocalPlayer, territoryType, true, isBoundByDuty, isBetweenAreas, isBetweenAreas51);

        if (isQueued)
        {
            dutyEntryEvidenceObserved = true;
            return Active(content, DadLocalDutyQueuePulseKind.WaitingForQueue, DadRunPhase.WaitingForQueuePop, DadParticipantState.QueuePending, $"Duty Finder queue active for {content.DutyName}; waiting for commence or duty entry.");
        }

        if ((!isLoggedIn || !hasLocalPlayer) && dutyEntryEvidenceObserved)
            return DutyEntryTransition(content, isLoggedIn, hasLocalPlayer, territoryType, false, isBoundByDuty, isBetweenAreas, isBetweenAreas51);

        if (!isLoggedIn || !hasLocalPlayer)
            return Failed(content, $"{content.LaneDisplayName} queue requires a logged-in local player.");

        return null;
    }

    private DadLocalDutyQueuePulse PulseRegularDuty(DadLocalDutyResolvedContent content)
    {
        try
        {
            var contentsFinder = ContentsFinder.Instance();
            if (contentsFinder == null)
                return Failed(content, "ContentsFinder runtime state is unavailable.");

            if (!unrestrictedPartyLease.Ensure(
                    content.Unsynced,
                    () => contentsFinder->IsUnrestrictedParty,
                    value => contentsFinder->IsUnrestrictedParty = value,
                    out var unrestrictedChanged,
                    out var unrestrictedFailure))
            {
                return Failed(content, $"Could not set Duty Finder unrestricted-party mode: {unrestrictedFailure}");
            }

            unrestrictedRestorePending = false;
            nextUnrestrictedRestoreAttemptUtc = DateTime.MinValue;

            if (unrestrictedChanged)
            {
                dutySelectionCleared = false;
                dutyListHydrated = false;
                hydratedDutyFinderCharacterContentId = 0;
                ResetLiveEntryMapping();
                var syncMode = content.Unsynced ? "enabled unrestricted/unsynced" : "disabled unrestricted/unsynced";
                return Active(content, DadLocalDutyQueuePulseKind.SetUnrestrictedParty, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Set Duty Finder to {syncMode} for {content.DutyName}.");
            }

            var agent = AgentContentsFinder.Instance();
            if (agent == null)
                return Failed(content, "AgentContentsFinder is unavailable.");

            var addonBase = RaptureAtkUnitManager.Instance()->GetAddonByName("ContentsFinder");
            if (addonBase == null || !addonBase->IsVisible)
            {
                var hud = AgentHUD.Instance();
                if (hud == null || !hud->IsMainCommandEnabled(33))
                    return Active(content, DadLocalDutyQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, "Waiting for Duty Finder main command to become available.", "Duty Finder main command is unavailable.");

                if (DateTime.UtcNow < nextOpenAttemptUtc)
                    return Active(content, DadLocalDutyQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Waiting for Duty Finder window for {content.DutyName}.");

                log.Debug("[dad] Opening regular Duty Finder for {DutyName} ({ContentFinderConditionId}).", content.DutyName, content.ContentFinderConditionId);
                agent->OpenRegularDuty(content.ContentFinderConditionId);
                nextOpenAttemptUtc = DateTime.UtcNow + OpenThrottle;
                dutyListHydrated = true;
                dutySelectionCleared = false;
                ResetLiveEntryMapping();
                hydratedDutyFinderCharacterContentId = Plugin.PlayerState.ContentId;
                return Active(content, DadLocalDutyQueuePulseKind.OpenedDutyFinder, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Opening regular Duty Finder for {content.LaneDisplayName} {content.DutyName}.");
            }

            if (dutyListHydrated &&
                (Plugin.PlayerState.ContentId == 0 ||
                 hydratedDutyFinderCharacterContentId != Plugin.PlayerState.ContentId))
            {
                return RestartRegularSelectionAttempt(
                    content,
                    "The logged-in character changed after Duty Finder hydration; restarting with that character's live list.");
            }

            if (!dutyListHydrated)
            {
                if (DateTime.UtcNow < nextOpenAttemptUtc)
                    return Active(content, DadLocalDutyQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Waiting to hydrate the live Duty Finder list for {content.DutyName}.");

                log.Debug("[dad] Hydrating regular Duty Finder list for {DutyName} ({ContentFinderConditionId}).", content.DutyName, content.ContentFinderConditionId);
                agent->OpenRegularDuty(content.ContentFinderConditionId);
                dutyListHydrated = true;
                nextOpenAttemptUtc = DateTime.UtcNow + OpenThrottle;
                ResetLiveEntryMapping();
                hydratedDutyFinderCharacterContentId = Plugin.PlayerState.ContentId;
                return Active(content, DadLocalDutyQueuePulseKind.OpenedDutyFinder, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Hydrating the live regular Duty Finder list for {content.DutyName}.");
            }

            if (!dutySelectionCleared)
            {
                log.Information("[dad] Clearing regular Duty Finder selection before selecting {DutyName} ({ContentFinderConditionId}).", content.DutyName, content.ContentFinderConditionId);
                FireAddonIntCallback(addonBase, 12, 1);
                dutySelectionCleared = true;
                ResetLiveEntryMapping();
                nextSelectAttemptUtc = DateTime.UtcNow + SelectThrottle;
                return Active(content, DadLocalDutyQueuePulseKind.ClearedDutySelection, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Cleared stale Duty Finder selection before choosing {content.DutyName} for {content.LaneDisplayName}.");
            }

            if (DateTime.UtcNow < nextSelectAttemptUtc)
                return Active(content, DadLocalDutyQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Waiting for Duty Finder list state to settle for {content.DutyName}.");

            var target = new DadDutyFinderLiveTarget(
                DadDutyFinderLiveContentType.Regular,
                content.ContentFinderConditionId);
            var mapping = ObserveLiveEntryMapping(agent, addonBase, content, target);
            if (!mapping.IsReady)
            {
                if (lastSelectionToken != null)
                {
                    if (DadDutyFinderMappedMutationRules.ShouldAwaitRegularPostSelectionMapping(
                            mapping,
                            lastSelectionToken))
                    {
                        return MappingWait(
                            content,
                            $"Waiting for a fresh stable regular-duty scan after exact selection ({mapping.Reason}).");
                    }

                    return RestartRegularSelectionAttempt(
                        content,
                        $"The live Duty Finder list changed before exact regular-duty proof ({mapping.Reason}); restarting with a fresh tab hydration.");
                }

                return MappingWait(content, mapping.Reason);
            }

            if (lastSelectionToken == null)
            {
                if (!DadDutyFinderMappedMutationRules.ShouldSelect(mapping, null))
                    return MappingWait(content, "The stable live Duty Finder mapping did not authorize an exact row callback.");

                var resolved = mapping.Entry!;
                if (resolved.SelectionToken.CharacterContentId != hydratedDutyFinderCharacterContentId ||
                    Plugin.PlayerState.ContentId != hydratedDutyFinderCharacterContentId)
                {
                    return RestartRegularSelectionAttempt(
                        content,
                        "The logged-in character changed after the stable regular-duty scan; restarting with a fresh hydration.");
                }

                FireAddonIntCallback(addonBase, 3, resolved.UiRow.CallbackOrdinal);
                lastSelectionToken = resolved.SelectionToken;
                nextSelectAttemptUtc = DateTime.UtcNow + SelectThrottle;
                log.Information(
                    "[dad] Selecting mapped regular Duty Finder target {ContentFinderConditionId} for character {CharacterContentId} at live position {ObservedPosition}, tree index {TreeIndex}, callback ordinal {CallbackOrdinal}.",
                    content.ContentFinderConditionId,
                    resolved.SelectionToken.CharacterContentId,
                    resolved.ObservedListPosition,
                    resolved.UiRow.TreeIndex,
                    resolved.UiRow.CallbackOrdinal);
                return Active(content, DadLocalDutyQueuePulseKind.CheckedDuty, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Selected exact live Duty Finder entry for {content.DutyName}; waiting for exact agent proof.");
            }

            var selectedType = ConvertContentType(agent->SelectedDuty.ContentType);
            var selectedId = agent->SelectedDuty.Id;
            var interfaceSelectedId = agent->InterfaceSub.SelectedDutyId >= 0
                ? (uint)agent->InterfaceSub.SelectedDutyId
                : 0;
            if (!DadDutyFinderMappedMutationRules.CanJoinRegularAfterSelection(
                    mapping,
                    lastSelectionToken,
                    selectedType,
                    selectedId,
                    interfaceSelectedId,
                    target))
            {
                return RestartRegularSelectionAttempt(
                    content,
                    $"Exact regular-duty proof failed for {target.ContentType}:{target.RowId}; selected={selectedType}:{selectedId}, interfaceId={agent->InterfaceSub.SelectedDutyId}. Restarting with a fresh tab hydration.");
            }

            if (DateTime.UtcNow < nextRegisterAttemptUtc)
                return Active(content, DadLocalDutyQueuePulseKind.Waiting, DadRunPhase.QueueStarting, DadParticipantState.QueuePending, $"Waiting before retrying regular Duty Finder join for {content.DutyName}.");

            log.Information("[dad] Joining regular Duty Finder duty {DutyName} ({ContentFinderConditionId}) unsynced={Unsynced}.", content.DutyName, content.ContentFinderConditionId, content.Unsynced);
            FireAddonIntCallback(addonBase, 12, 0);
            nextRegisterAttemptUtc = DateTime.UtcNow + RegisterThrottle;
            return Active(content, DadLocalDutyQueuePulseKind.RegisteredForDuty, DadRunPhase.QueueStarting, DadParticipantState.QueuePending, $"Joined regular Duty Finder duty {content.DutyName} for {content.LaneDisplayName}; waiting for queue state, commence popup, or duty entry.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[dad] Regular Duty Finder queue pulse failed for {LaneDisplayName} {DutyName} ({ContentFinderConditionId}).", content.LaneDisplayName, content.DutyName, content.ContentFinderConditionId);
            return Failed(content, $"{content.LaneDisplayName} queue pulse failed: {ex.Message}");
        }
    }

    private DadLocalDutyQueuePulse PulseRoulette(DadLocalDutyResolvedContent content)
    {
        try
        {
            var contentsFinder = ContentsFinder.Instance();
            if (contentsFinder == null)
                return RetryableQueueWait(content, "ContentsFinder runtime state is unavailable; retrying.");

            if (!TryGetMutationSafety(out var safetyWait))
                return RetryableQueueWait(content, safetyWait);

            if (!unrestrictedPartyLease.Ensure(
                    requiredValue: false,
                    () => contentsFinder->IsUnrestrictedParty,
                    value => contentsFinder->IsUnrestrictedParty = value,
                    out var unrestrictedChanged,
                    out var unrestrictedFailure))
            {
                return RetryableQueueWait(content, $"Could not force unrestricted party off for Daily Roulette: {unrestrictedFailure}");
            }

            unrestrictedRestorePending = false;
            nextUnrestrictedRestoreAttemptUtc = DateTime.MinValue;

            if (unrestrictedChanged)
            {
                return Active(
                    content,
                    DadLocalDutyQueuePulseKind.SetUnrestrictedParty,
                    DadRunPhase.QueuePreparing,
                    DadParticipantState.QueuePending,
                    $"Disabled unrestricted party for Daily Roulette {content.DutyName}; the previous value will be restored.");
            }

            var agent = AgentContentsFinder.Instance();
            if (agent == null)
                return RetryableQueueWait(content, "AgentContentsFinder is unavailable; retrying.");

            var now = DateTime.UtcNow;
            if (rouletteAttemptGate.IsRegistrationGraceActive(now))
            {
                return Active(
                    content,
                    DadLocalDutyQueuePulseKind.Waiting,
                    DadRunPhase.QueueStarting,
                    DadParticipantState.QueuePending,
                    "Waiting for Duty Finder registration evidence before another Join attempt.");
            }

            var addonBase = RaptureAtkUnitManager.Instance()->GetAddonByName("ContentsFinder");
            if (addonBase == null || !addonBase->IsVisible)
            {
                var hud = AgentHUD.Instance();
                if (hud == null || !hud->IsMainCommandEnabled(33))
                {
                    return Active(
                        content,
                        DadLocalDutyQueuePulseKind.Waiting,
                        DadRunPhase.QueuePreparing,
                        DadParticipantState.QueuePending,
                        "Waiting for Duty Finder main command to become available.",
                        "Duty Finder main command is unavailable.");
                }

                if (DateTime.UtcNow < nextOpenAttemptUtc)
                    return Active(content, DadLocalDutyQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Waiting for Duty Finder window for {content.DutyName}.");

                if (!TryGetMutationSafety(out safetyWait))
                    return RetryableQueueWait(content, safetyWait);
                agent->Show();
                nextOpenAttemptUtc = DateTime.UtcNow + OpenThrottle;
                ResetLiveEntryMapping();
                hydratedDutyFinderCharacterContentId = 0;
                return Active(content, DadLocalDutyQueuePulseKind.OpenedDutyFinder, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Opening Duty Finder before selecting Daily Roulette {content.DutyName}.");
            }

            var target = new DadDutyFinderLiveTarget(
                DadDutyFinderLiveContentType.Roulette,
                content.RouletteId);
            var mapping = ObserveLiveEntryMapping(agent, addonBase, content, target);
            var selectedType = ConvertContentType(agent->SelectedDuty.ContentType);
            var selectedId = agent->SelectedDuty.Id;
            var exactAgentSelection = DadRouletteSelectionProof.IsExact(
                agent->HasRouletteSelected,
                agent->SelectedDuty.ContentType == ContentsType.Roulette,
                selectedId,
                content.RouletteId);
            var exactMappedSelection = exactAgentSelection &&
                                       DadDutyFinderMappedMutationRules.CanJoin(
                                           mapping,
                                           lastSelectionToken,
                                           selectedType,
                                           selectedId,
                                           target);

            var decision = rouletteAttemptGate.Decide(
                now,
                exactMappedSelection,
                dutyEntryEvidenceObserved || rouletteTerritoryGate.EntryEvidenceObserved,
                mapping.IsReady);

            switch (decision.Mutation)
            {
                case DadRouletteQueueMutation.ClearSelection:
                    if (!TryGetMutationSafety(out safetyWait))
                        return RetryableQueueWait(content, safetyWait);
                    log.Information("[dad] Clearing stale Duty Finder selection before Daily Roulette {RouletteName} ({RouletteId}).", content.DutyName, content.RouletteId);
                    FireAddonIntCallback(addonBase, 12, 1);
                    ResetLiveEntryMapping();
                    hydratedDutyFinderCharacterContentId = 0;
                    return Active(content, DadLocalDutyQueuePulseKind.ClearedDutySelection, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Cleared stale Duty Finder selection before Daily Roulette {content.DutyName}.");

                case DadRouletteQueueMutation.OpenRoulette:
                    if (!TryGetMutationSafety(out safetyWait))
                        return RetryableQueueWait(content, safetyWait);
                    log.Information("[dad] Hydrating Daily Roulette list for {RouletteName} ({RouletteId}).", content.DutyName, content.RouletteId);
                    agent->OpenRouletteDuty(checked((byte)content.RouletteId));
                    ResetLiveEntryMapping();
                    hydratedDutyFinderCharacterContentId = Plugin.PlayerState.ContentId;
                    return Active(content, DadLocalDutyQueuePulseKind.OpenedDutyFinder, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Hydrating the live Daily Roulette list for {content.DutyName}; no row has been selected yet.");

                case DadRouletteQueueMutation.SelectMappedEntry:
                    if (!DadDutyFinderMappedMutationRules.ShouldSelect(mapping, lastSelectionToken) ||
                        mapping.Entry!.SelectionToken.CharacterContentId != hydratedDutyFinderCharacterContentId ||
                        Plugin.PlayerState.ContentId != hydratedDutyFinderCharacterContentId)
                    {
                        rouletteAttemptGate.RetryFullCycle();
                        return RetryableQueueWait(content, $"Mapped Daily Roulette selection token was stale or belonged to a different hydration character before callback; restarting roulette #{content.RouletteId}.");
                    }

                    if (!TryGetMutationSafety(out safetyWait))
                        return RetryableQueueWait(content, safetyWait);
                    var resolved = mapping.Entry!;
                    FireAddonIntCallback(addonBase, 3, resolved.UiRow.CallbackOrdinal);
                    lastSelectionToken = resolved.SelectionToken;
                    log.Information(
                        "[dad] Selecting mapped Daily Roulette target {RouletteId} for character {CharacterContentId} at live position {ObservedPosition}, tree index {TreeIndex}, callback ordinal {CallbackOrdinal}; unbounded attempt {Attempt}.",
                        content.RouletteId,
                        resolved.SelectionToken.CharacterContentId,
                        resolved.ObservedListPosition,
                        resolved.UiRow.TreeIndex,
                        resolved.UiRow.CallbackOrdinal,
                        rouletteAttemptGate.SelectionAttempts);
                    return Active(content, DadLocalDutyQueuePulseKind.SelectedDuty, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Selected exact live Daily Roulette entry for {content.DutyName}; waiting six seconds before exact agent proof.");

                case DadRouletteQueueMutation.Join:
                    if (!DadRouletteSelectionProof.IsExact(
                            agent->HasRouletteSelected,
                            agent->SelectedDuty.ContentType == ContentsType.Roulette,
                            agent->SelectedDuty.Id,
                            content.RouletteId) ||
                        !DadDutyFinderMappedMutationRules.CanJoin(
                            mapping,
                            lastSelectionToken,
                            ConvertContentType(agent->SelectedDuty.ContentType),
                            agent->SelectedDuty.Id,
                            target))
                    {
                        rouletteAttemptGate.RetryFullCycle();
                        return RetryableQueueWait(content, $"Daily Roulette mapping or exact agent selection changed before Join; restarting roulette #{content.RouletteId}.");
                    }

                    if (!TryGetMutationSafety(out safetyWait))
                        return RetryableQueueWait(content, safetyWait);
                    log.Information("[dad] Joining Daily Roulette {RouletteName} ({RouletteId}); unbounded attempt {Attempt}.", content.DutyName, content.RouletteId, rouletteAttemptGate.JoinAttempts);
                    FireAddonIntCallback(addonBase, 12, 0);
                    return Active(content, DadLocalDutyQueuePulseKind.RegisteredForDuty, DadRunPhase.QueueStarting, DadParticipantState.QueuePending, $"Registered Daily Roulette {content.DutyName}; waiting up to eight seconds for queue, commence, or transition evidence.");

                case DadRouletteQueueMutation.Fail:
                    rouletteAttemptGate.RetryFullCycle();
                    return RetryableQueueWait(content, decision.Reason);

                default:
                    var phase = rouletteAttemptGate.JoinAttempts > 0
                        ? DadRunPhase.QueueStarting
                        : DadRunPhase.QueuePreparing;
                    var waitReason = mapping.IsReady ||
                                     decision.Reason.Contains("registration evidence", StringComparison.OrdinalIgnoreCase) ||
                                     decision.Reason.Contains("six seconds", StringComparison.OrdinalIgnoreCase)
                        ? decision.Reason
                        : mapping.Reason;
                    return Active(content, DadLocalDutyQueuePulseKind.Waiting, phase, DadParticipantState.QueuePending, waitReason, waitReason);
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[dad] Daily Roulette queue pulse failed for {RouletteName} ({RouletteId}).", content.DutyName, content.RouletteId);
            rouletteAttemptGate.RetryFullCycle();
            return RetryableQueueWait(content, $"Daily Roulette runtime exception is retryable: {ex.Message}");
        }
    }

    private DadLocalDutyQueuePulse RetryableQueueWait(DadLocalDutyResolvedContent content, string reason)
        => Active(
            content,
            DadLocalDutyQueuePulseKind.Waiting,
            rouletteAttemptGate.JoinAttempts > 0 ? DadRunPhase.QueueStarting : DadRunPhase.QueuePreparing,
            DadParticipantState.QueuePending,
            reason,
            reason);

    private bool TryGetMutationSafety(out string reason)
    {
        reason = string.Empty;
        if (liveSafetySnapshotBuilder == null)
            return true;

        DadParticipantSnapshot snapshot;
        try
        {
            snapshot = liveSafetySnapshotBuilder();
        }
        catch (Exception ex)
        {
            reason = $"Waiting for a fresh local queue safety snapshot: {ex.Message}";
            return false;
        }

        if (!snapshot.IsAvailable ||
            !snapshot.WorldReadyStable ||
            !snapshot.PostArReady ||
            !snapshot.AutoRetainerAvailable ||
            snapshot.AutoRetainerBusy ||
            snapshot.AutoRetainerMultiModeEnabled ||
            snapshot.ExternalAutomationHeld)
        {
            reason = $"Waiting for strict local queue safety: available={snapshot.IsAvailable}, worldStable={snapshot.WorldReadyStable}, postArReady={snapshot.PostArReady}, autoRetainerAvailable={snapshot.AutoRetainerAvailable}, autoRetainerBusy={snapshot.AutoRetainerBusy}, multiMode={snapshot.AutoRetainerMultiModeEnabled}, externalHeld={snapshot.ExternalAutomationHeld}.";
            return false;
        }

        return true;
    }

    private void ResetForNewRun(string runId)
    {
        if (string.Equals(activeRunId, runId, StringComparison.OrdinalIgnoreCase))
            return;

        // Review M7: restore any dangling unsync override from the previous run before starting fresh.
        RestoreUnrestrictedParty();
        activeRunId = runId;
        nextOpenAttemptUtc = DateTime.MinValue;
        nextSelectAttemptUtc = DateTime.MinValue;
        nextRegisterAttemptUtc = DateTime.MinValue;
        nextConfirmAttemptUtc = DateTime.MinValue;
        dutyEntryEvidenceObserved = false;
        dutyEntryTransitionLogged = false;
        transientMissingPlayerLogged = false;
        dutySelectionCleared = false;
        dutyListHydrated = false;
        hydratedDutyFinderCharacterContentId = 0;
        ResetLiveEntryMapping();
        rouletteAttemptGate.Reset();
        rouletteTerritoryGate.Reset();
    }

    private static DadLocalDutyResolvedContent? ResolveRegularDutySelection(
        uint contentFinderConditionId,
        string dutyName,
        bool unsynced,
        int expectedPartySize,
        DadModuleId moduleId,
        string laneDisplayName,
        bool enforcePremadePartySize,
        out string blocker)
    {
        blocker = string.Empty;
        if (string.IsNullOrWhiteSpace(dutyName))
        {
            blocker = $"{laneDisplayName} task is missing duty display name.";
            return null;
        }

        var trimmedDutyName = dutyName.Trim();
        var contentFinderSheet = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();
        if (contentFinderConditionId == 0)
        {
            var matches = contentFinderSheet
                .Where(condition => string.Equals(condition.Name.ToString().Trim(), trimmedDutyName, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            if (matches.Count == 0 && trimmedDutyName.Contains("Armour", StringComparison.OrdinalIgnoreCase))
            {
                var alternateName = trimmedDutyName.Replace("Armour", "Armor", StringComparison.OrdinalIgnoreCase);
                matches = contentFinderSheet
                    .Where(condition => string.Equals(condition.Name.ToString().Trim(), alternateName, StringComparison.OrdinalIgnoreCase))
                    .Take(2)
                    .ToList();
            }
            if (matches.Count != 1)
            {
                blocker = matches.Count == 0
                    ? $"{laneDisplayName} could not resolve Duty Finder duty '{trimmedDutyName}'."
                    : $"{laneDisplayName} duty name '{trimmedDutyName}' is ambiguous; provide ContentFinderCondition id.";
                return null;
            }

            contentFinderConditionId = matches[0].RowId;
        }

        if (!contentFinderSheet.TryGetRow(contentFinderConditionId, out var condition))
        {
            blocker = $"ContentFinderCondition #{contentFinderConditionId} was not found.";
            return null;
        }

        if (!condition.IsInDutyFinder)
        {
            blocker = $"{trimmedDutyName} #{contentFinderConditionId} is not available in the regular Duty Finder.";
            return null;
        }

        if (condition.PvP)
        {
            blocker = $"{trimmedDutyName} #{contentFinderConditionId} is PvP content; {laneDisplayName} does not queue PvP.";
            return null;
        }

        if (condition.TerritoryType.ValueNullable == null)
        {
            blocker = $"ContentFinderCondition #{contentFinderConditionId} has no territory.";
            return null;
        }

        if (unsynced && !condition.AllowUndersized)
        {
            blocker = $"{trimmedDutyName} #{contentFinderConditionId} is not marked for undersized/unrestricted party.";
            return null;
        }

        var queueSize = condition.QueueMaxPlayers > 0
            ? condition.QueueMaxPlayers
            : condition.ContentMemberType.ValueNullable?.MembersPerParty ?? (byte)1;
        var queueSizeInt = Math.Max(1, (int)queueSize);
        var resolvedExpectedPartySize = expectedPartySize > 0 ? expectedPartySize : queueSizeInt;

        if (enforcePremadePartySize)
        {
            if (resolvedExpectedPartySize < 2)
            {
                blocker = $"{laneDisplayName} requires at least two Dad-verified participants; use Local Duty / Unsync for one-character queues.";
                return null;
            }

            if (resolvedExpectedPartySize > queueSizeInt)
            {
                blocker = $"{laneDisplayName} expected party size {resolvedExpectedPartySize} exceeds {trimmedDutyName}'s Duty Finder queue size {queueSizeInt}.";
                return null;
            }

            if (!unsynced && resolvedExpectedPartySize != queueSizeInt)
            {
                blocker = $"Synced {laneDisplayName} requires a full Duty Finder party of {queueSizeInt}; request has {resolvedExpectedPartySize}.";
                return null;
            }
        }

        var sheetDutyName = condition.Name.ToString().Trim();
        return new DadLocalDutyResolvedContent
        {
            ModuleId = moduleId,
            LaneDisplayName = laneDisplayName,
            TargetKind = DadQueueTargetKind.DutyFinderDuty,
            ContentFinderConditionId = condition.RowId,
            TerritoryType = condition.TerritoryType.Value.RowId,
            DutyName = trimmedDutyName,
            SheetDutyName = string.IsNullOrWhiteSpace(sheetDutyName) ? trimmedDutyName : sheetDutyName,
            Unsynced = unsynced,
            AllowUndersized = condition.AllowUndersized,
            IsHighEndDuty = condition.HighEndDuty,
            QueueSize = queueSizeInt,
            ExpectedPartySize = Math.Max(1, resolvedExpectedPartySize),
        };
    }

    private void ClearRunState()
    {
        RestoreUnrestrictedParty();
        activeRunId = string.Empty;
        nextOpenAttemptUtc = DateTime.MinValue;
        nextSelectAttemptUtc = DateTime.MinValue;
        nextRegisterAttemptUtc = DateTime.MinValue;
        nextConfirmAttemptUtc = DateTime.MinValue;
        dutyEntryEvidenceObserved = false;
        dutyEntryTransitionLogged = false;
        transientMissingPlayerLogged = false;
        dutySelectionCleared = false;
        dutyListHydrated = false;
        hydratedDutyFinderCharacterContentId = 0;
        ResetLiveEntryMapping();
        rouletteAttemptGate.Reset();
        rouletteTerritoryGate.Reset();
    }

    // Review M7: restore the Duty Finder unrestricted/unsynced flag to its pre-run value.
    private void RestoreUnrestrictedParty()
    {
        if (!unrestrictedPartyLease.IsActive)
        {
            unrestrictedRestorePending = false;
            nextUnrestrictedRestoreAttemptUtc = DateTime.MinValue;
            return;
        }

        unrestrictedRestorePending = true;
        nextUnrestrictedRestoreAttemptUtc = DateTime.UtcNow + RestoreRetryThrottle;

        ContentsFinder* contentsFinder;
        try
        {
            contentsFinder = ContentsFinder.Instance();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Cannot restore Duty Finder unrestricted-party setting yet; ContentsFinder lookup failed.");
            return;
        }

        if (contentsFinder == null)
        {
            log.Warning("[dad] Cannot restore Duty Finder unrestricted-party setting yet; ContentsFinder is unavailable.");
            return;
        }

        if (!unrestrictedPartyLease.Restore(
                () => contentsFinder->IsUnrestrictedParty,
                value => contentsFinder->IsUnrestrictedParty = value,
                out var failure))
        {
            log.Error("[dad] Failed to restore Duty Finder unrestricted-party setting: {Failure}", failure);
            return;
        }

        unrestrictedRestorePending = false;
        nextUnrestrictedRestoreAttemptUtc = DateTime.MinValue;
    }

    private void TrySubscribeFrameworkUpdate()
    {
        try
        {
            Plugin.Framework.Update += OnFrameworkUpdate;
            frameworkUpdateSubscribed = true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Local Duty queue service could not subscribe to framework updates; pending Duty Finder setting restoration will retry on queue/reset pulses.");
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!unrestrictedRestorePending || DateTime.UtcNow < nextUnrestrictedRestoreAttemptUtc)
            return;

        RestoreUnrestrictedParty();
    }

    private void TrySubscribeDutyState()
    {
        try
        {
            Plugin.DutyState.DutyCompleted += OnDutyCompleted;
            dutyStateSubscribed = true;
            log.Debug("[dad] Local Duty queue service subscribed to DutyCompleted.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Local Duty queue service could not subscribe to DutyCompleted; exit fallback remains available.");
        }
    }

    private void OnDutyCompleted(Dalamud.Game.DutyState.IDutyStateEventArgs args)
        => OnDutyCompleted(args.TerritoryType.RowId);

    private void OnDutyCompleted(uint territoryId)
    {
        lastDutyCompletedTerritoryId = territoryId;
        lastDutyCompletedUtc = DateTime.UtcNow;
        log.Information("[dad] Local Duty DutyCompleted observed for territory {TerritoryId}.", territoryId);
    }

    private bool TryAcceptContentsFinderConfirm(DadLocalDutyResolvedContent content)
    {
        if (DateTime.UtcNow < nextConfirmAttemptUtc)
            return false;

        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("ContentsFinderConfirm");
            if (addon == null || !addon->IsVisible)
                return false;

            if (!TryGetMutationSafety(out var safetyWait))
            {
                log.Information(
                    "[dad] ContentsFinderConfirm acceptance is waiting for fresh strict local safety for {DutyName}: {Reason}",
                    content.DutyName,
                    safetyWait);
                nextConfirmAttemptUtc = DateTime.UtcNow + ConfirmThrottle;
                return false;
            }

            FireAddonIntCallback(addon, 8);
            log.Information("[dad] Accepting regular Duty Finder commence popup for {DutyName}.", content.DutyName);
            nextConfirmAttemptUtc = DateTime.UtcNow + ConfirmThrottle;
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[dad] Failed to accept ContentsFinderConfirm for {DutyName}.", content.DutyName);
            nextConfirmAttemptUtc = DateTime.UtcNow + ConfirmThrottle;
            return false;
        }
    }

    private DadLocalDutyQueuePulse DutyEntryTransition(
        DadLocalDutyResolvedContent content,
        bool isLoggedIn,
        bool hasLocalPlayer,
        uint territoryType,
        bool isQueued,
        bool isBoundByDuty,
        bool isBetweenAreas,
        bool isBetweenAreas51)
    {
        dutyEntryEvidenceObserved = true;

        if (!dutyEntryTransitionLogged)
        {
            dutyEntryTransitionLogged = true;
            log.Information(
                "[dad] Local Duty entry transition observed for {DutyName}; territory={TerritoryType}, requestedTerritory={RequestedTerritory}, queued={Queued}, boundByDuty={BoundByDuty}, betweenAreas={BetweenAreas}, betweenAreas51={BetweenAreas51}, loggedIn={LoggedIn}, localPlayer={LocalPlayer}.",
                content.DutyName,
                territoryType,
                content.TerritoryType,
                isQueued,
                isBoundByDuty,
                isBetweenAreas,
                isBetweenAreas51,
                isLoggedIn,
                hasLocalPlayer);
        }

        if ((!isLoggedIn || !hasLocalPlayer) && !transientMissingPlayerLogged)
        {
            transientMissingPlayerLogged = true;
            log.Information(
                "[dad] Suppressing transient missing local player during Local Duty entry for {DutyName}; loggedIn={LoggedIn}, localPlayer={LocalPlayer}, territory={TerritoryType}, requestedTerritory={RequestedTerritory}, queued={Queued}, boundByDuty={BoundByDuty}, betweenAreas={BetweenAreas}, betweenAreas51={BetweenAreas51}.",
                content.DutyName,
                isLoggedIn,
                hasLocalPlayer,
                territoryType,
                content.TerritoryType,
                isQueued,
                isBoundByDuty,
                isBetweenAreas,
                isBetweenAreas51);
        }

        return Active(
            content,
            DadLocalDutyQueuePulseKind.DutyEntryTransition,
            DadRunPhase.WaitingForQueuePop,
            DadParticipantState.QueuePending,
            $"Duty entry transition for {content.DutyName}; waiting for local player/duty truth to settle.");
    }

    private DadDutyFinderMappingResult ObserveLiveEntryMapping(
        AgentContentsFinder* agent,
        AtkUnitBase* addonBase,
        DadLocalDutyResolvedContent content,
        DadDutyFinderLiveTarget target)
    {
        DadDutyFinderMappingResult mapping;
        if (!DadDutyFinderLiveEntryScanner.TryCapture(agent, addonBase, out var snapshot, out var scanFailure))
        {
            liveEntryMappingGate.Reset();
            mapping = new DadDutyFinderMappingResult(
                DadDutyFinderMappingStatus.Unstable,
                scanFailure);
        }
        else
        {
            mapping = liveEntryMappingGate.Observe(snapshot, target);
        }

        LogLiveEntryMappingTransition(content, target, mapping, agent);
        return mapping;
    }

    private void LogLiveEntryMappingTransition(
        DadLocalDutyResolvedContent content,
        DadDutyFinderLiveTarget target,
        DadDutyFinderMappingResult mapping,
        AgentContentsFinder* agent)
    {
        var selectedType = agent == null
            ? DadDutyFinderLiveContentType.None
            : ConvertContentType(agent->SelectedDuty.ContentType);
        var selectedId = agent == null ? 0u : agent->SelectedDuty.Id;
        var entry = mapping.Entry;
        var observedPosition = entry?.ObservedListPosition ?? 0;
        var treeIndex = entry?.UiRow.TreeIndex ?? -1;
        var callbackOrdinal = entry?.UiRow.CallbackOrdinal ?? 0;
        var enabled = entry?.UiRow.Enabled ?? false;
        var fingerprint = entry?.SelectionToken.ListFingerprint ?? string.Empty;
        var characterContentId = entry?.SelectionToken.CharacterContentId ?? Plugin.PlayerState.ContentId;
        var reason = string.IsNullOrWhiteSpace(mapping.Reason) ? "(none)" : mapping.Reason;
        var transition = string.Join(
            "|",
            target.ContentType,
            target.RowId,
            characterContentId,
            mapping.Status,
            observedPosition,
            treeIndex,
            callbackOrdinal,
            enabled,
            selectedType,
            selectedId,
            fingerprint,
            reason);
        if (string.Equals(lastMappingTransition, transition, StringComparison.Ordinal))
            return;

        lastMappingTransition = transition;
        log.Information(
            "[dad][DutyFinderMap] characterContentId={CharacterContentId} target={TargetType}:{TargetId} duty={DutyName} status={Status} observedPosition={ObservedPosition} treeIndex={TreeIndex} callbackOrdinal={CallbackOrdinal} enabled={Enabled} selectedAgent={SelectedType}:{SelectedId} mismatch={MismatchReason} listFingerprint={ListFingerprint} itemLabel={ItemLabel} rendererNodeTextDiagnostic={RendererNodeTextDiagnostic}.",
            characterContentId,
            target.ContentType,
            target.RowId,
            content.DutyName,
            mapping.Status,
            observedPosition,
            treeIndex,
            callbackOrdinal,
            enabled,
            selectedType,
            selectedId,
            reason,
            fingerprint,
            entry?.UiRow.ItemLabel ?? string.Empty,
            entry?.UiRow.RendererNodeText ?? string.Empty);
    }

    private void ResetLiveEntryMapping()
    {
        liveEntryMappingGate.Reset();
        lastSelectionToken = null;
        lastMappingTransition = string.Empty;
    }

    private static DadDutyFinderLiveContentType ConvertContentType(ContentsType contentType)
        => contentType switch
        {
            ContentsType.Roulette => DadDutyFinderLiveContentType.Roulette,
            ContentsType.Regular => DadDutyFinderLiveContentType.Regular,
            _ => DadDutyFinderLiveContentType.None,
        };

    private static DadLocalDutyQueuePulse MappingWait(
        DadLocalDutyResolvedContent content,
        string reason,
        DadRunPhase phase = DadRunPhase.QueuePreparing)
    {
        reason = string.IsNullOrWhiteSpace(reason)
            ? "Waiting for an exact stable live Duty Finder entry mapping."
            : reason;
        return Active(
            content,
            DadLocalDutyQueuePulseKind.Waiting,
            phase,
            DadParticipantState.QueuePending,
            reason,
            reason);
    }

    private DadLocalDutyQueuePulse RestartRegularSelectionAttempt(
        DadLocalDutyResolvedContent content,
        string reason)
    {
        dutyListHydrated = false;
        dutySelectionCleared = false;
        hydratedDutyFinderCharacterContentId = 0;
        ResetLiveEntryMapping();
        return MappingWait(content, reason);
    }

    private static bool IsContentsFinderQueueStateActive()
    {
        try
        {
            var contentsFinder = ContentsFinder.Instance();
            if (contentsFinder == null)
                return false;

            return contentsFinder->QueueInfo.QueueState is
                ContentsFinderQueueState.Pending or
                ContentsFinderQueueState.Queued or
                ContentsFinderQueueState.Ready or
                ContentsFinderQueueState.Accepted;
        }
        catch
        {
            return false;
        }
    }

    private static void FireAddonIntCallback(AtkUnitBase* addon, int value)
    {
        var atkValues = stackalloc AtkValue[1];
        atkValues[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
        atkValues[0].Int = value;
        addon->FireCallback(1, atkValues, true);
    }

    private static void FireAddonIntCallback(AtkUnitBase* addon, int first, int second)
    {
        var atkValues = stackalloc AtkValue[2];
        atkValues[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
        atkValues[0].Int = first;
        atkValues[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
        atkValues[1].Int = second;
        addon->FireCallback(2, atkValues, true);
    }

    // Review M8: being in the requested territory ALONE is not an entry transition — otherwise, if the player
    // is already standing in that territory without a duty, Dad waits forever for "duty truth to settle".
    // Treat it as a transition only with zone-load (betweenAreas) or actual queue/entry evidence.
    private static bool IsDutyEntryTransition(bool isBetweenAreas, bool isBetweenAreas51, bool isRequestedTerritory, bool hasEntryEvidence)
        => isBetweenAreas || isBetweenAreas51 || (isRequestedTerritory && hasEntryEvidence);

    private static DadLocalDutyQueuePulse Active(
        DadLocalDutyResolvedContent content,
        DadLocalDutyQueuePulseKind kind,
        DadRunPhase phase,
        DadParticipantState participantState,
        string summary,
        string blockedReason = "")
        => new()
        {
            Kind = kind,
            Phase = phase,
            Status = DadRunStatus.Running,
            ParticipantState = participantState,
            Success = true,
            IsActive = true,
            Summary = summary,
            BlockedReason = blockedReason,
            Blockers = string.IsNullOrWhiteSpace(blockedReason)
                ? []
                :
                [
                    new DadModuleBlockerDto
                    {
                        ModuleId = content.ModuleId,
                        Capability = "RuntimeReadiness",
                        Severity = DadModuleBlockerSeverity.Deferred,
                        Summary = blockedReason,
                    },
                ],
        };

    private DadLocalDutyQueuePulse Failed(
        DadLocalDutyResolvedContent content,
        string reason,
        bool cleanup = true)
    {
        if (cleanup)
            ClearRunState();

        return new DadLocalDutyQueuePulse
        {
            Kind = DadLocalDutyQueuePulseKind.Failed,
            Phase = DadRunPhase.Finalizing,
            Status = DadRunStatus.Failed,
            ParticipantState = DadParticipantState.Failed,
            Success = false,
            IsActive = false,
            Summary = reason,
            FailureReason = reason,
            BlockedReason = reason,
            Blockers =
            [
                new DadModuleBlockerDto
                {
                    ModuleId = content.ModuleId,
                    Capability = "RuntimeReadiness",
                    Severity = DadModuleBlockerSeverity.Failed,
                    Summary = reason,
                },
            ],
        };
    }
}
