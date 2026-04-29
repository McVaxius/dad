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
    public uint ContentFinderConditionId { get; set; }
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

    private readonly IPluginLog log;
    private DateTime nextOpenAttemptUtc = DateTime.MinValue;
    private DateTime nextSelectAttemptUtc = DateTime.MinValue;
    private DateTime nextRegisterAttemptUtc = DateTime.MinValue;
    private DateTime nextConfirmAttemptUtc = DateTime.MinValue;
    private DateTime lastDutyCompletedUtc = DateTime.MinValue;
    private uint lastDutyCompletedTerritoryId;
    private string activeRunId = string.Empty;
    private bool dutyStateSubscribed;
    private bool dutyEntryEvidenceObserved;
    private bool dutyEntryTransitionLogged;
    private bool transientMissingPlayerLogged;
    private bool dutySelectionCleared;
    private bool dutySelectionCallbackSent;

    public DadLocalDutyQueueService(IPluginLog log)
    {
        this.log = log;
        TrySubscribeDutyState();
    }

    public void Dispose()
    {
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
        ResetForNewRun(runId);

        var commonPulse = BuildCommonQueuePulse(content);
        if (commonPulse != null)
            return commonPulse;

        return PulseRegularDuty(content);
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

    public bool IsInRequestedDuty(DadLocalDutyResolvedContent content)
        => Plugin.Condition[ConditionFlag.BoundByDuty] &&
           Plugin.ClientState.TerritoryType == content.TerritoryType;

    public bool HasDutyCompleted(DadLocalDutyResolvedContent content, DateTime runStartedAtUtc)
        => lastDutyCompletedUtc >= runStartedAtUtc &&
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
        var isRequestedTerritory = territoryType == content.TerritoryType;

        if (isBoundByDuty && isRequestedTerritory)
        {
            dutyEntryEvidenceObserved = true;
            return Active(content, DadLocalDutyQueuePulseKind.EnteredDuty, DadRunPhase.InDutyOrTask, DadParticipantState.Running, $"Entered {content.LaneDisplayName} {content.DutyName}.");
        }

        if (IsDutyEntryTransition(isBetweenAreas, isBetweenAreas51, isRequestedTerritory))
            return DutyEntryTransition(content, isLoggedIn, hasLocalPlayer, territoryType, isQueued, isBoundByDuty, isBetweenAreas, isBetweenAreas51);

        if (isBoundByDuty)
        {
            if (!isLoggedIn || !hasLocalPlayer)
                return DutyEntryTransition(content, isLoggedIn, hasLocalPlayer, territoryType, isQueued, true, isBetweenAreas, isBetweenAreas51);

            return Failed(content, $"Already bound by another duty in territory {territoryType}; cannot start {content.DutyName}.");
        }

        if (TryAcceptContentsFinderConfirm(content))
        {
            dutyEntryEvidenceObserved = true;
            return Active(content, DadLocalDutyQueuePulseKind.AcceptedQueueConfirm, DadRunPhase.QueueStarting, DadParticipantState.QueuePending, $"Accepted regular Duty Finder commence popup for {content.DutyName}.");
        }

        if (isQueued && (!isLoggedIn || !hasLocalPlayer))
            return DutyEntryTransition(content, isLoggedIn, hasLocalPlayer, territoryType, true, isBoundByDuty, isBetweenAreas, isBetweenAreas51);

        if (isQueued)
        {
            dutyEntryEvidenceObserved = true;
            return Active(content, DadLocalDutyQueuePulseKind.WaitingForQueue, DadRunPhase.WaitingForQueuePop, DadParticipantState.QueuePending, $"Regular Duty Finder queue active for {content.DutyName}; waiting for commence or duty entry.");
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

            if (contentsFinder->IsUnrestrictedParty != content.Unsynced)
            {
                contentsFinder->IsUnrestrictedParty = content.Unsynced;
                dutySelectionCleared = false;
                dutySelectionCallbackSent = false;
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
                dutySelectionCallbackSent = false;
                return Active(content, DadLocalDutyQueuePulseKind.OpenedDutyFinder, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Opening regular Duty Finder for {content.LaneDisplayName} {content.DutyName}.");
            }

            if (!dutySelectionCleared)
            {
                log.Information("[dad] Clearing regular Duty Finder selection before selecting {DutyName} ({ContentFinderConditionId}).", content.DutyName, content.ContentFinderConditionId);
                FireAddonIntCallback(addonBase, 12, 1);
                dutySelectionCleared = true;
                dutySelectionCallbackSent = false;
                nextSelectAttemptUtc = DateTime.UtcNow + SelectThrottle;
                return Active(content, DadLocalDutyQueuePulseKind.ClearedDutySelection, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Cleared stale Duty Finder selection before choosing {content.DutyName} for {content.LaneDisplayName}.");
            }

            if (agent->InterfaceSub.SelectedDutyId != content.ContentFinderConditionId)
            {
                if (DateTime.UtcNow < nextSelectAttemptUtc)
                    return Active(content, DadLocalDutyQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Waiting for Duty Finder selection to settle for {content.DutyName}.");

                log.Debug("[dad] Selecting regular Duty Finder duty {DutyName} ({ContentFinderConditionId}).", content.DutyName, content.ContentFinderConditionId);
                agent->OpenRegularDuty(content.ContentFinderConditionId);
                nextSelectAttemptUtc = DateTime.UtcNow + SelectThrottle;
                dutySelectionCallbackSent = false;
                return Active(content, DadLocalDutyQueuePulseKind.SelectedDuty, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Selecting regular Duty Finder duty {content.DutyName} for {content.LaneDisplayName}.");
            }

            var addon = (AddonContentsFinder*)addonBase;
            if (!dutySelectionCallbackSent)
            {
                if (DateTime.UtcNow < nextSelectAttemptUtc)
                    return Active(content, DadLocalDutyQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Waiting for Duty Finder selection to settle for {content.DutyName}.");

                if (!TryCheckHighlightedDuty(addonBase, addon))
                    return Active(content, DadLocalDutyQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Waiting for Duty Finder duty list for {content.DutyName}.");

                dutySelectionCallbackSent = true;
                nextSelectAttemptUtc = DateTime.UtcNow + SelectThrottle;
                return Active(content, DadLocalDutyQueuePulseKind.CheckedDuty, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Checked regular Duty Finder duty {content.DutyName} for {content.LaneDisplayName}.");
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

    private void ResetForNewRun(string runId)
    {
        if (string.Equals(activeRunId, runId, StringComparison.OrdinalIgnoreCase))
            return;

        activeRunId = runId;
        nextOpenAttemptUtc = DateTime.MinValue;
        nextSelectAttemptUtc = DateTime.MinValue;
        nextRegisterAttemptUtc = DateTime.MinValue;
        nextConfirmAttemptUtc = DateTime.MinValue;
        dutyEntryEvidenceObserved = false;
        dutyEntryTransitionLogged = false;
        transientMissingPlayerLogged = false;
        dutySelectionCleared = false;
        dutySelectionCallbackSent = false;
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
        if (contentFinderConditionId == 0)
        {
            blocker = $"{laneDisplayName} task is missing content finder condition id.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(dutyName))
        {
            blocker = $"{laneDisplayName} task is missing duty display name.";
            return null;
        }

        var trimmedDutyName = dutyName.Trim();
        var contentFinderSheet = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();
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
        activeRunId = string.Empty;
        nextOpenAttemptUtc = DateTime.MinValue;
        nextSelectAttemptUtc = DateTime.MinValue;
        nextRegisterAttemptUtc = DateTime.MinValue;
        nextConfirmAttemptUtc = DateTime.MinValue;
        dutyEntryEvidenceObserved = false;
        dutyEntryTransitionLogged = false;
        transientMissingPlayerLogged = false;
        dutySelectionCleared = false;
        dutySelectionCallbackSent = false;
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

    private void OnDutyCompleted(object? sender, ushort territoryId)
        => OnDutyCompleted((uint)territoryId);

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

    private static bool TryCheckHighlightedDuty(AtkUnitBase* addonBase, AddonContentsFinder* addon)
    {
        if (addon == null || addon->DutyList == null)
            return false;

        var dutyList = addon->DutyList;
        if (dutyList->Items.Count == 0)
            return false;

        var selectedIndex = dutyList->SelectedItemIndex;
        if (selectedIndex < 0 || selectedIndex >= dutyList->Items.Count)
            selectedIndex = (int)Math.Clamp(addon->SelectedRow, 0, Math.Max(0, dutyList->Items.Count - 1));

        if (selectedIndex < 0 || selectedIndex >= dutyList->Items.Count)
            return false;

        var dutyOrdinal = CountSelectableDutyRowsBefore(dutyList, selectedIndex) + 1;
        FireAddonIntCallback(addonBase, 3, (int)dutyOrdinal);
        return true;
    }

    private static uint CountSelectableDutyRowsBefore(AtkComponentTreeList* dutyList, int selectedIndex)
    {
        var count = 0u;
        for (var index = 0; index < selectedIndex; index++)
        {
            var item = dutyList->GetItem(index);
            if (item == null || item->UIntValues.Count == 0)
                continue;

            var type = item->UIntValues[0];
            if (type is (uint)AtkComponentTreeListItemType.Leaf or (uint)AtkComponentTreeListItemType.LastLeafInGroup)
                count++;
        }

        return count;
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

    private static bool IsDutyEntryTransition(bool isBetweenAreas, bool isBetweenAreas51, bool isRequestedTerritory)
        => isBetweenAreas || isBetweenAreas51 || isRequestedTerritory;

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

    private static DadLocalDutyQueuePulse Failed(DadLocalDutyResolvedContent content, string reason)
        => new()
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
