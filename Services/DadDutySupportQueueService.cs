using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using dad.Models;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace dad.Services;

public enum DadDutySupportQueuePulseKind
{
    Waiting,
    OpenedDutySupport,
    SelectedDuty,
    RegisteredForDuty,
    AcceptedQueueConfirm,
    WaitingForQueue,
    DutyEntryTransition,
    EnteredDuty,
    Failed,
    Cancelled,
}

public sealed class DadDutySupportResolvedContent
{
    public uint ContentFinderConditionId { get; set; }
    public uint ContentId { get; set; }
    public uint TerritoryType { get; set; }
    public uint ExVersion { get; set; }
    public uint DawnContentRowId { get; set; }
    public string DutyName { get; set; } = string.Empty;
    public string SheetDutyName { get; set; } = string.Empty;
    public bool HasDutySupportData { get; set; }
}

public sealed class DadDutySupportQueuePulse
{
    public DadDutySupportQueuePulseKind Kind { get; set; } = DadDutySupportQueuePulseKind.Waiting;
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

public sealed unsafe class DadDutySupportQueueService : IDisposable
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
    private ushort lastDutyCompletedTerritoryId;
    private string activeRunId = string.Empty;
    private bool dutyStateSubscribed;
    private bool dutyEntryEvidenceObserved;
    private bool dutyEntryTransitionLogged;
    private bool transientMissingPlayerLogged;

    public DadDutySupportQueueService(IPluginLog log)
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

    public DadDutySupportResolvedContent? Resolve(DadDutySupportTask? task, out string blocker)
    {
        blocker = string.Empty;
        if (task == null)
        {
            blocker = "No Duty Support task exists in this request.";
            return null;
        }

        if (task.ContentFinderConditionId == 0)
        {
            blocker = "Duty Support task is missing content finder condition id.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(task.DutyName))
        {
            blocker = "Duty Support task is missing duty display name.";
            return null;
        }

        var contentFinderSheet = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();
        if (!contentFinderSheet.TryGetRow(task.ContentFinderConditionId, out var condition))
        {
            blocker = $"ContentFinderCondition #{task.ContentFinderConditionId} was not found.";
            return null;
        }

        if (condition.TerritoryType.ValueNullable == null)
        {
            blocker = $"ContentFinderCondition #{task.ContentFinderConditionId} has no territory.";
            return null;
        }

        if (condition.TerritoryType.Value.ExVersion.ValueNullable == null)
        {
            blocker = $"ContentFinderCondition #{task.ContentFinderConditionId} has no expansion data.";
            return null;
        }

        var dawnContentSheet = Plugin.DataManager.GetExcelSheet<DawnContent>();
        var dawnContent = dawnContentSheet.FirstOrDefault(row => row.Content.ValueNullable?.RowId == condition.RowId);
        var hasDawnContent = dawnContent.RowId != 0;
        var hasDutySupportData = false;
        if (hasDawnContent)
        {
            var participableSheet = Plugin.DataManager.GetSubrowExcelSheet<DawnContentParticipable>();
            hasDutySupportData = participableSheet.GetSubrowCount(dawnContent.RowId) > 1;
        }

        if (!hasDutySupportData)
        {
            blocker = $"{task.DutyName} #{task.ContentFinderConditionId} is not marked as Duty Support content in DawnContent.";
            return null;
        }

        var sheetDutyName = condition.Name.ToString();
        return new DadDutySupportResolvedContent
        {
            ContentFinderConditionId = condition.RowId,
            ContentId = condition.Content.RowId,
            TerritoryType = condition.TerritoryType.Value.RowId,
            ExVersion = condition.TerritoryType.Value.ExVersion.Value.RowId,
            DawnContentRowId = dawnContent.RowId,
            DutyName = task.DutyName.Trim(),
            SheetDutyName = string.IsNullOrWhiteSpace(sheetDutyName) ? task.DutyName.Trim() : sheetDutyName,
            HasDutySupportData = true,
        };
    }

    public DadDutySupportQueuePulse Pulse(string runId, DadDutySupportResolvedContent content)
    {
        ResetForNewRun(runId);

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
            return Active(DadDutySupportQueuePulseKind.EnteredDuty, DadRunPhase.InDutyOrTask, DadParticipantState.Running, $"Entered Duty Support duty {content.DutyName}.");
        }

        if (IsDutyEntryTransition(isBetweenAreas, isBetweenAreas51, isRequestedTerritory))
            return DutyEntryTransition(content, isLoggedIn, hasLocalPlayer, territoryType, isQueued, isBoundByDuty, isBetweenAreas, isBetweenAreas51);

        if (isBoundByDuty)
        {
            if (!isLoggedIn || !hasLocalPlayer)
                return DutyEntryTransition(content, isLoggedIn, hasLocalPlayer, territoryType, isQueued, true, isBetweenAreas, isBetweenAreas51);

            return Failed($"Already bound by another duty in territory {territoryType}; cannot start {content.DutyName}.");
        }

        if (TryAcceptContentsFinderConfirm(content.DutyName))
        {
            dutyEntryEvidenceObserved = true;
            return Active(DadDutySupportQueuePulseKind.AcceptedQueueConfirm, DadRunPhase.QueueStarting, DadParticipantState.QueuePending, $"Accepted Duty Support commence popup for {content.DutyName}.");
        }

        if (isQueued && (!isLoggedIn || !hasLocalPlayer))
            return DutyEntryTransition(content, isLoggedIn, hasLocalPlayer, territoryType, true, isBoundByDuty, isBetweenAreas, isBetweenAreas51);

        if (isQueued)
        {
            dutyEntryEvidenceObserved = true;
            return Active(DadDutySupportQueuePulseKind.WaitingForQueue, DadRunPhase.WaitingForQueuePop, DadParticipantState.QueuePending, $"Duty Support queue active for {content.DutyName}; waiting for duty entry.");
        }

        if ((!isLoggedIn || !hasLocalPlayer) && dutyEntryEvidenceObserved)
            return DutyEntryTransition(content, isLoggedIn, hasLocalPlayer, territoryType, false, isBoundByDuty, isBetweenAreas, isBetweenAreas51);

        if (!isLoggedIn || !hasLocalPlayer)
            return Failed("Duty Support queue requires a logged-in local player.");

        try
        {
            var agent = AgentDawnStory.Instance();
            if (agent == null)
                return Failed("AgentDawnStory is unavailable.");

            if (!agent->IsAddonReady())
            {
                var hud = AgentHUD.Instance();
                if (hud == null || !hud->IsMainCommandEnabled(91))
                    return Active(DadDutySupportQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, "Waiting for Duty Support main command to become available.", "Duty Support main command is unavailable.");

                if (DateTime.UtcNow < nextOpenAttemptUtc)
                    return Active(DadDutySupportQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Waiting for Duty Support window for {content.DutyName}.");

                log.Debug("[dad] Opening Duty Support for {DutyName} content {ContentId}.", content.DutyName, content.ContentId);
                RaptureAtkModule.Instance()->OpenDawnStory(content.ContentId);
                nextOpenAttemptUtc = DateTime.UtcNow + OpenThrottle;
                return Active(DadDutySupportQueuePulseKind.OpenedDutySupport, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Opening Duty Support for {content.DutyName}.");
            }

            if (agent->Data->ContentData.ExpansionCount <= content.ExVersion)
                return Failed($"{content.DutyName} requires expansion row {content.ExVersion}, but Duty Support does not report that expansion unlocked.");

            var selectedContentId = agent->Data->ContentData.ContentEntries[agent->Data->ContentData.SelectedContentEntry].ContentFinderConditionId;
            if (selectedContentId != content.ContentFinderConditionId)
            {
                if (DateTime.UtcNow < nextSelectAttemptUtc)
                    return Active(DadDutySupportQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Waiting for Duty Support selection to settle for {content.DutyName}.");

                log.Debug("[dad] Selecting Duty Support {DutyName} content finder {ContentFinderConditionId}.", content.DutyName, content.ContentFinderConditionId);
                RaptureAtkModule.Instance()->OpenDawnStory(content.ContentFinderConditionId);
                nextSelectAttemptUtc = DateTime.UtcNow + SelectThrottle;
                return Active(DadDutySupportQueuePulseKind.SelectedDuty, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Selecting Duty Support duty {content.DutyName}.");
            }

            if (DateTime.UtcNow < nextRegisterAttemptUtc)
                return Active(DadDutySupportQueuePulseKind.Waiting, DadRunPhase.QueueStarting, DadParticipantState.QueuePending, $"Waiting before retrying Duty Support register for {content.DutyName}.");

            log.Information("[dad] Registering Duty Support duty {DutyName} ({ContentFinderConditionId}).", content.DutyName, content.ContentFinderConditionId);
            agent->RegisterForDuty();
            nextRegisterAttemptUtc = DateTime.UtcNow + RegisterThrottle;
            return Active(DadDutySupportQueuePulseKind.RegisteredForDuty, DadRunPhase.QueueStarting, DadParticipantState.QueuePending, $"Registered Duty Support duty {content.DutyName}; waiting for queue state or duty entry.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[dad] Duty Support queue pulse failed for {DutyName} ({ContentFinderConditionId}).", content.DutyName, content.ContentFinderConditionId);
            return Failed($"Duty Support queue pulse failed: {ex.Message}");
        }
    }

    public DadDutySupportQueuePulse Cancel(string runId, string reason)
    {
        if (string.Equals(activeRunId, runId, StringComparison.OrdinalIgnoreCase))
            ClearRunState();

        return new DadDutySupportQueuePulse
        {
            Kind = DadDutySupportQueuePulseKind.Cancelled,
            Phase = DadRunPhase.Finalizing,
            Status = DadRunStatus.Cancelled,
            ParticipantState = DadParticipantState.Cancelled,
            Success = false,
            IsActive = false,
            Summary = string.IsNullOrWhiteSpace(reason) ? "Duty Support queue executor cancelled." : reason,
            FailureReason = string.IsNullOrWhiteSpace(reason) ? "Cancelled." : reason,
        };
    }

    public bool IsInRequestedDuty(DadDutySupportResolvedContent content)
        => Plugin.Condition[ConditionFlag.BoundByDuty] &&
           Plugin.ClientState.TerritoryType == content.TerritoryType;

    public bool HasDutyCompleted(DadDutySupportResolvedContent content, DateTime runStartedAtUtc)
        => lastDutyCompletedUtc >= runStartedAtUtc &&
           lastDutyCompletedTerritoryId == content.TerritoryType;

    public bool IsQueued()
        => Plugin.Condition[ConditionFlag.InDutyQueue] ||
           Plugin.Condition[ConditionFlag.WaitingForDuty] ||
           Plugin.Condition[ConditionFlag.WaitingForDutyFinder];

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
    }

    private void TrySubscribeDutyState()
    {
        try
        {
            Plugin.DutyState.DutyCompleted += OnDutyCompleted;
            dutyStateSubscribed = true;
            log.Debug("[dad] Duty Support queue service subscribed to DutyCompleted.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Duty Support queue service could not subscribe to DutyCompleted; exit fallback remains available.");
        }
    }

    private void OnDutyCompleted(object? sender, ushort territoryId)
    {
        lastDutyCompletedTerritoryId = territoryId;
        lastDutyCompletedUtc = DateTime.UtcNow;
        log.Information("[dad] DutyCompleted observed for territory {TerritoryId}.", territoryId);
    }

    private bool TryAcceptContentsFinderConfirm(string dutyName)
    {
        if (DateTime.UtcNow < nextConfirmAttemptUtc)
            return false;

        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("ContentsFinderConfirm");
            if (addon == null || !addon->IsVisible)
                return false;

            var atkValues = stackalloc AtkValue[1];
            atkValues[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            atkValues[0].Int = 8;

            log.Information("[dad] Accepting Duty Support commence popup for {DutyName}.", dutyName);
            addon->FireCallback(1, atkValues, true);
            nextConfirmAttemptUtc = DateTime.UtcNow + ConfirmThrottle;
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[dad] Failed to accept ContentsFinderConfirm for {DutyName}.", dutyName);
            nextConfirmAttemptUtc = DateTime.UtcNow + ConfirmThrottle;
            return false;
        }
    }

    private DadDutySupportQueuePulse DutyEntryTransition(
        DadDutySupportResolvedContent content,
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
                "[dad] Duty Support duty-entry transition observed for {DutyName}; territory={TerritoryType}, requestedTerritory={RequestedTerritory}, queued={Queued}, boundByDuty={BoundByDuty}, betweenAreas={BetweenAreas}, betweenAreas51={BetweenAreas51}, loggedIn={LoggedIn}, localPlayer={LocalPlayer}.",
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
                "[dad] Suppressing transient missing local player during Duty Support entry for {DutyName}; loggedIn={LoggedIn}, localPlayer={LocalPlayer}, territory={TerritoryType}, requestedTerritory={RequestedTerritory}, queued={Queued}, boundByDuty={BoundByDuty}, betweenAreas={BetweenAreas}, betweenAreas51={BetweenAreas51}.",
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
            DadDutySupportQueuePulseKind.DutyEntryTransition,
            DadRunPhase.WaitingForQueuePop,
            DadParticipantState.QueuePending,
            $"Duty entry transition for {content.DutyName}; waiting for local player/duty truth to settle.");
    }

    private static bool IsDutyEntryTransition(bool isBetweenAreas, bool isBetweenAreas51, bool isRequestedTerritory)
        => isBetweenAreas || isBetweenAreas51 || isRequestedTerritory;

    private static DadDutySupportQueuePulse Active(
        DadDutySupportQueuePulseKind kind,
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
                        ModuleId = DadModuleId.DutySupport,
                        Capability = "RuntimeReadiness",
                        Severity = DadModuleBlockerSeverity.Deferred,
                        Summary = blockedReason,
                    },
                ],
        };

    private static DadDutySupportQueuePulse Failed(string reason)
        => new()
        {
            Kind = DadDutySupportQueuePulseKind.Failed,
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
                    ModuleId = DadModuleId.DutySupport,
                    Capability = "RuntimeReadiness",
                    Severity = DadModuleBlockerSeverity.Failed,
                    Summary = reason,
                },
            ],
        };
}
