using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using dad.Models;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace dad.Services;

public enum DadNpcDutyQueueMode
{
    DutySupport,
    Trust,
}

public enum DadNpcDutyQueuePulseKind
{
    Waiting,
    OpenedDutySupport,
    OpenedTrust,
    SelectedDuty,
    PreparedTrustParty,
    RegisteredForDuty,
    AcceptedQueueConfirm,
    WaitingForQueue,
    DutyEntryTransition,
    EnteredDuty,
    Failed,
    Cancelled,
}

internal enum DadTrustPlayerRole
{
    Unknown,
    Tank,
    Healer,
    Dps,
    Limited,
}

internal enum DadTrustMemberRole
{
    Dps,
    Healer,
    Tank,
    AllRounder,
}

internal readonly record struct DadTrustMemberCandidate(byte Index, string Name, DadTrustMemberRole Role);

public sealed class DadNpcDutyResolvedContent
{
    public DadNpcDutyQueueMode Mode { get; set; } = DadNpcDutyQueueMode.DutySupport;
    public DadModuleId ModuleId => Mode == DadNpcDutyQueueMode.Trust ? DadModuleId.Trust : DadModuleId.DutySupport;
    public string LaneName => Mode == DadNpcDutyQueueMode.Trust ? "Trust" : "Duty Support";
    public uint ContentFinderConditionId { get; set; }
    public uint ContentId { get; set; }
    public uint TerritoryType { get; set; }
    public uint ExVersion { get; set; }
    public uint DawnContentRowId { get; set; }
    public int TrustIndex { get; set; } = -1;
    public byte ClassJobLevelRequired { get; set; }
    public string DutyName { get; set; } = string.Empty;
    public string SheetDutyName { get; set; } = string.Empty;
    public bool HasDutySupportData { get; set; }
    public bool HasTrustData { get; set; }
}

public sealed class DadNpcDutyQueuePulse
{
    public DadNpcDutyQueuePulseKind Kind { get; set; } = DadNpcDutyQueuePulseKind.Waiting;
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

public sealed unsafe class DadNpcDutyQueueService : IDisposable
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
    private DadNpcDutyQueueMode activeMode = DadNpcDutyQueueMode.DutySupport;
    private bool dutyStateSubscribed;
    private bool dutyEntryEvidenceObserved;
    private bool dutyEntryTransitionLogged;
    private bool transientMissingPlayerLogged;
    private bool trustPartyCleared;
    private bool trustPartySelected;

    public DadNpcDutyQueueService(IPluginLog log)
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

    public DadNpcDutyResolvedContent? Resolve(DadDutySupportTask? task, out string blocker)
    {
        if (task == null)
        {
            blocker = "No Duty Support task exists in this request.";
            return null;
        }

        return ResolveCore(
            DadNpcDutyQueueMode.DutySupport,
            task.ContentFinderConditionId,
            task.DutyName,
            out blocker);
    }

    public DadNpcDutyResolvedContent? Resolve(DadTrustTask? task, out string blocker)
    {
        if (task == null)
        {
            blocker = "No Trust task exists in this request.";
            return null;
        }

        return ResolveCore(
            DadNpcDutyQueueMode.Trust,
            task.ContentFinderConditionId,
            task.DutyName,
            out blocker);
    }

    public bool CanSelectTrustPartyForLocalPlayer(out string blocker)
    {
        blocker = string.Empty;
        var role = GetLocalPlayerRole();
        switch (role)
        {
            case DadTrustPlayerRole.Tank:
            case DadTrustPlayerRole.Healer:
            case DadTrustPlayerRole.Dps:
                return true;
            case DadTrustPlayerRole.Limited:
                blocker = "Blue Mage cannot run Trust through Dad's native Trust lane.";
                return false;
            default:
                blocker = "Trust requires a logged-in local combat job so Dad can select compatible NPC roles.";
                return false;
        }
    }

    public DadNpcDutyQueuePulse Pulse(string runId, DadNpcDutyResolvedContent content)
    {
        ResetForNewRun(runId, content.Mode);

        var commonPulse = BuildCommonQueuePulse(content);
        if (commonPulse != null)
            return commonPulse;

        return content.Mode == DadNpcDutyQueueMode.Trust
            ? PulseTrust(content)
            : PulseDutySupport(content);
    }

    public DadNpcDutyQueuePulse Cancel(string runId, DadNpcDutyQueueMode mode, string reason)
    {
        if (string.Equals(activeRunId, runId, StringComparison.OrdinalIgnoreCase))
            ClearRunState();

        var laneName = FormatLaneName(mode);
        return new DadNpcDutyQueuePulse
        {
            Kind = DadNpcDutyQueuePulseKind.Cancelled,
            Phase = DadRunPhase.Finalizing,
            Status = DadRunStatus.Cancelled,
            ParticipantState = DadParticipantState.Cancelled,
            Success = false,
            IsActive = false,
            Summary = string.IsNullOrWhiteSpace(reason) ? $"{laneName} queue executor cancelled." : reason,
            FailureReason = string.IsNullOrWhiteSpace(reason) ? "Cancelled." : reason,
        };
    }

    public bool IsInRequestedDuty(DadNpcDutyResolvedContent content)
        => Plugin.Condition[ConditionFlag.BoundByDuty] &&
           Plugin.ClientState.TerritoryType == content.TerritoryType;

    public bool HasDutyCompleted(DadNpcDutyResolvedContent content, DateTime runStartedAtUtc)
        => lastDutyCompletedUtc >= runStartedAtUtc &&
           lastDutyCompletedTerritoryId == content.TerritoryType;

    public bool IsQueued()
        => Plugin.Condition[ConditionFlag.InDutyQueue] ||
           Plugin.Condition[ConditionFlag.WaitingForDuty] ||
           Plugin.Condition[ConditionFlag.WaitingForDutyFinder];

    private DadNpcDutyResolvedContent? ResolveCore(
        DadNpcDutyQueueMode mode,
        uint contentFinderConditionId,
        string dutyName,
        out string blocker)
    {
        blocker = string.Empty;
        var laneName = FormatLaneName(mode);
        if (contentFinderConditionId == 0)
        {
            blocker = $"{laneName} task is missing content finder condition id.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(dutyName))
        {
            blocker = $"{laneName} task is missing duty display name.";
            return null;
        }

        var contentFinderSheet = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();
        if (!contentFinderSheet.TryGetRow(contentFinderConditionId, out var condition))
        {
            blocker = $"ContentFinderCondition #{contentFinderConditionId} was not found.";
            return null;
        }

        if (condition.TerritoryType.ValueNullable == null)
        {
            blocker = $"ContentFinderCondition #{contentFinderConditionId} has no territory.";
            return null;
        }

        if (condition.TerritoryType.Value.ExVersion.ValueNullable == null)
        {
            blocker = $"ContentFinderCondition #{contentFinderConditionId} has no expansion data.";
            return null;
        }

        var dawnContentSheet = Plugin.DataManager.GetExcelSheet<DawnContent>();
        var dawnContent = dawnContentSheet.FirstOrDefault(row => row.Content.ValueNullable?.RowId == condition.RowId);
        var hasDawnContent = dawnContent.RowId != 0;
        var hasDutySupportData = false;
        var hasTrustData = false;
        var trustIndex = -1;

        if (hasDawnContent)
        {
            var participableSheet = Plugin.DataManager.GetSubrowExcelSheet<DawnContentParticipable>();
            hasDutySupportData = participableSheet.GetSubrowCount(dawnContent.RowId) > 1;

            if (dawnContent.Unknown13)
            {
                var trustDawnRows = dawnContentSheet
                    .Where(static row => row.RowId != 0 && row.Content.RowId != 0 && row.Unknown13)
                    .ToList();
                var trustOrdinal = trustDawnRows.FindIndex(row => row.Content.ValueNullable?.RowId == condition.RowId);
                hasTrustData = TryGetTrustIndex(
                    trustOrdinal,
                    condition.TerritoryType.Value.ExVersion.Value.RowId,
                    out trustIndex);
            }
        }

        if (mode == DadNpcDutyQueueMode.DutySupport && !hasDutySupportData)
        {
            blocker = $"{dutyName} #{contentFinderConditionId} is not marked as Duty Support content in DawnContent.";
            return null;
        }

        if (mode == DadNpcDutyQueueMode.Trust && !hasTrustData)
        {
            blocker = $"{dutyName} #{contentFinderConditionId} is not marked as Trust content in DawnContent.";
            return null;
        }

        var sheetDutyName = condition.Name.ToString();
        return new DadNpcDutyResolvedContent
        {
            Mode = mode,
            ContentFinderConditionId = condition.RowId,
            ContentId = condition.Content.RowId,
            TerritoryType = condition.TerritoryType.Value.RowId,
            ExVersion = condition.TerritoryType.Value.ExVersion.Value.RowId,
            DawnContentRowId = dawnContent.RowId,
            TrustIndex = trustIndex,
            ClassJobLevelRequired = condition.ClassJobLevelRequired,
            DutyName = dutyName.Trim(),
            SheetDutyName = string.IsNullOrWhiteSpace(sheetDutyName) ? dutyName.Trim() : sheetDutyName,
            HasDutySupportData = hasDutySupportData,
            HasTrustData = hasTrustData,
        };
    }

    private DadNpcDutyQueuePulse? BuildCommonQueuePulse(DadNpcDutyResolvedContent content)
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
            return Active(content, DadNpcDutyQueuePulseKind.EnteredDuty, DadRunPhase.InDutyOrTask, DadParticipantState.Running, $"Entered {content.LaneName} duty {content.DutyName}.");
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
            return Active(content, DadNpcDutyQueuePulseKind.AcceptedQueueConfirm, DadRunPhase.QueueStarting, DadParticipantState.QueuePending, $"Accepted {content.LaneName} commence popup for {content.DutyName}.");
        }

        if (isQueued && (!isLoggedIn || !hasLocalPlayer))
            return DutyEntryTransition(content, isLoggedIn, hasLocalPlayer, territoryType, true, isBoundByDuty, isBetweenAreas, isBetweenAreas51);

        if (isQueued)
        {
            dutyEntryEvidenceObserved = true;
            return Active(content, DadNpcDutyQueuePulseKind.WaitingForQueue, DadRunPhase.WaitingForQueuePop, DadParticipantState.QueuePending, $"{content.LaneName} queue active for {content.DutyName}; waiting for duty entry.");
        }

        if ((!isLoggedIn || !hasLocalPlayer) && dutyEntryEvidenceObserved)
            return DutyEntryTransition(content, isLoggedIn, hasLocalPlayer, territoryType, false, isBoundByDuty, isBetweenAreas, isBetweenAreas51);

        if (!isLoggedIn || !hasLocalPlayer)
            return Failed(content, $"{content.LaneName} queue requires a logged-in local player.");

        return null;
    }

    private DadNpcDutyQueuePulse PulseDutySupport(DadNpcDutyResolvedContent content)
    {
        try
        {
            var agent = AgentDawnStory.Instance();
            if (agent == null)
                return Failed(content, "AgentDawnStory is unavailable.");

            if (!agent->IsAddonReady())
            {
                var hud = AgentHUD.Instance();
                if (hud == null || !hud->IsMainCommandEnabled(91))
                    return Active(content, DadNpcDutyQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, "Waiting for Duty Support main command to become available.", "Duty Support main command is unavailable.");

                if (DateTime.UtcNow < nextOpenAttemptUtc)
                    return Active(content, DadNpcDutyQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Waiting for Duty Support window for {content.DutyName}.");

                log.Debug("[dad] Opening Duty Support for {DutyName} content {ContentId}.", content.DutyName, content.ContentId);
                RaptureAtkModule.Instance()->OpenDawnStory(content.ContentId);
                nextOpenAttemptUtc = DateTime.UtcNow + OpenThrottle;
                return Active(content, DadNpcDutyQueuePulseKind.OpenedDutySupport, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Opening Duty Support for {content.DutyName}.");
            }

            if (agent->Data->ContentData.ExpansionCount <= content.ExVersion)
                return Failed(content, $"{content.DutyName} requires expansion row {content.ExVersion}, but Duty Support does not report that expansion unlocked.");

            var selectedContentId = agent->Data->ContentData.ContentEntries[agent->Data->ContentData.SelectedContentEntry].ContentFinderConditionId;
            if (selectedContentId != content.ContentFinderConditionId)
            {
                if (DateTime.UtcNow < nextSelectAttemptUtc)
                    return Active(content, DadNpcDutyQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Waiting for Duty Support selection to settle for {content.DutyName}.");

                log.Debug("[dad] Selecting Duty Support {DutyName} content finder {ContentFinderConditionId}.", content.DutyName, content.ContentFinderConditionId);
                RaptureAtkModule.Instance()->OpenDawnStory(content.ContentFinderConditionId);
                nextSelectAttemptUtc = DateTime.UtcNow + SelectThrottle;
                return Active(content, DadNpcDutyQueuePulseKind.SelectedDuty, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Selecting Duty Support duty {content.DutyName}.");
            }

            if (DateTime.UtcNow < nextRegisterAttemptUtc)
                return Active(content, DadNpcDutyQueuePulseKind.Waiting, DadRunPhase.QueueStarting, DadParticipantState.QueuePending, $"Waiting before retrying Duty Support register for {content.DutyName}.");

            log.Information("[dad] Registering Duty Support duty {DutyName} ({ContentFinderConditionId}).", content.DutyName, content.ContentFinderConditionId);
            agent->RegisterForDuty();
            nextRegisterAttemptUtc = DateTime.UtcNow + RegisterThrottle;
            return Active(content, DadNpcDutyQueuePulseKind.RegisteredForDuty, DadRunPhase.QueueStarting, DadParticipantState.QueuePending, $"Registered Duty Support duty {content.DutyName}; waiting for queue state or duty entry.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[dad] Duty Support queue pulse failed for {DutyName} ({ContentFinderConditionId}).", content.DutyName, content.ContentFinderConditionId);
            return Failed(content, $"Duty Support queue pulse failed: {ex.Message}");
        }
    }

    private DadNpcDutyQueuePulse PulseTrust(DadNpcDutyResolvedContent content)
    {
        try
        {
            var agent = AgentDawn.Instance();
            if (agent == null)
                return Failed(content, "AgentDawn is unavailable.");

            if (!agent->IsAddonReady())
            {
                var hud = AgentHUD.Instance();
                if (hud == null || !hud->IsMainCommandEnabled(82))
                    return Active(content, DadNpcDutyQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, "Waiting for Trust main command to become available.", "Trust main command is unavailable.");

                if (DateTime.UtcNow < nextOpenAttemptUtc)
                    return Active(content, DadNpcDutyQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Waiting for Trust window for {content.DutyName}.");

                log.Debug("[dad] Opening Trust for {DutyName} content finder {ContentFinderConditionId}.", content.DutyName, content.ContentFinderConditionId);
                RaptureAtkModule.Instance()->OpenDawn(content.ContentFinderConditionId);
                nextOpenAttemptUtc = DateTime.UtcNow + OpenThrottle;
                return Active(content, DadNpcDutyQueuePulseKind.OpenedTrust, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Opening Trust for {content.DutyName}.");
            }

            var requiredExpansionIndex = content.ExVersion > 2 ? content.ExVersion - 2 : 0;
            if (agent->Data->ContentData.ExpansionCount < requiredExpansionIndex)
                return Failed(content, $"{content.DutyName} requires Trust expansion row {content.ExVersion}, but Trust does not report that expansion unlocked.");

            if (agent->SelectedContentId != content.DawnContentRowId)
            {
                if (DateTime.UtcNow < nextSelectAttemptUtc)
                    return Active(content, DadNpcDutyQueuePulseKind.Waiting, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Waiting for Trust selection to settle for {content.DutyName}.");

                log.Debug("[dad] Selecting Trust {DutyName} content finder {ContentFinderConditionId} dawn row {DawnContentRowId}.", content.DutyName, content.ContentFinderConditionId, content.DawnContentRowId);
                RaptureAtkModule.Instance()->OpenDawn(content.ContentFinderConditionId);
                nextSelectAttemptUtc = DateTime.UtcNow + SelectThrottle;
                return Active(content, DadNpcDutyQueuePulseKind.SelectedDuty, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Selecting Trust duty {content.DutyName}.");
            }

            if (!trustPartyCleared)
            {
                agent->Data->PartyData.ClearParty();
                agent->UpdateAddon();
                trustPartyCleared = true;
                return Active(content, DadNpcDutyQueuePulseKind.PreparedTrustParty, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, $"Cleared current Trust party selection for {content.DutyName}.");
            }

            if (!trustPartySelected)
            {
                if (!TrySelectTrustParty(agent, content, out var trustPartySummary, out var trustPartyFailure))
                    return Failed(content, trustPartyFailure);

                agent->UpdateAddon();
                trustPartySelected = true;
                return Active(content, DadNpcDutyQueuePulseKind.PreparedTrustParty, DadRunPhase.QueuePreparing, DadParticipantState.QueuePending, trustPartySummary);
            }

            if (DateTime.UtcNow < nextRegisterAttemptUtc)
                return Active(content, DadNpcDutyQueuePulseKind.Waiting, DadRunPhase.QueueStarting, DadParticipantState.QueuePending, $"Waiting before retrying Trust register for {content.DutyName}.");

            log.Information("[dad] Registering Trust duty {DutyName} ({ContentFinderConditionId}).", content.DutyName, content.ContentFinderConditionId);
            agent->RegisterForDuty();
            nextRegisterAttemptUtc = DateTime.UtcNow + RegisterThrottle;
            return Active(content, DadNpcDutyQueuePulseKind.RegisteredForDuty, DadRunPhase.QueueStarting, DadParticipantState.QueuePending, $"Registered Trust duty {content.DutyName}; waiting for queue state or duty entry.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[dad] Trust queue pulse failed for {DutyName} ({ContentFinderConditionId}).", content.DutyName, content.ContentFinderConditionId);
            return Failed(content, $"Trust queue pulse failed: {ex.Message}");
        }
    }

    private bool TrySelectTrustParty(
        AgentDawn* agent,
        DadNpcDutyResolvedContent content,
        out string summary,
        out string failure)
    {
        summary = string.Empty;
        failure = string.Empty;
        var playerRole = GetLocalPlayerRole();
        if (playerRole is DadTrustPlayerRole.Unknown or DadTrustPlayerRole.Limited)
        {
            failure = playerRole == DadTrustPlayerRole.Limited
                ? "Blue Mage cannot run Trust through Dad's native Trust lane."
                : "Trust requires a logged-in local combat job so Dad can select compatible NPC roles.";
            return false;
        }

        var availableMembers = GetAvailableTrustMembers(agent, content);
        if (!TryBuildTrustParty(playerRole, availableMembers, out var selectedMembers, out failure))
            return false;

        var currentMembers = agent->Data->MemberData.GetMembers(agent->Data->MemberData.CurrentMembersIndex);
        foreach (var member in selectedMembers.OrderBy(static member => member.Role).ThenBy(static member => member.Index))
        {
            var entry = currentMembers[member.Index];
            agent->Data->PartyData.AddMember(member.Index, &entry);
        }

        summary = $"Selected Trust party for {content.DutyName}: {string.Join(", ", selectedMembers.Select(static member => member.Name))}.";
        return true;
    }

    private static List<DadTrustMemberCandidate> GetAvailableTrustMembers(
        AgentDawn* agent,
        DadNpcDutyResolvedContent content)
    {
        var currentMembers = agent->Data->MemberData.GetMembers(agent->Data->MemberData.CurrentMembersIndex);
        var candidates = BuildTrustCandidates(content);
        var available = new List<DadTrustMemberCandidate>();
        foreach (var candidate in candidates)
        {
            var entry = currentMembers[candidate.Index];
            if (entry.MemberId == 0 || entry.Level < content.ClassJobLevelRequired)
                continue;

            available.Add(candidate);
        }

        return available;
    }

    private static IReadOnlyList<DadTrustMemberCandidate> BuildTrustCandidates(DadNpcDutyResolvedContent content)
    {
        var candidates = new List<DadTrustMemberCandidate>
        {
            new(0, "Alphinaud", DadTrustMemberRole.Healer),
            new(1, "Alisaie", DadTrustMemberRole.Dps),
            new(2, "Thancred", DadTrustMemberRole.Tank),
            new(3, "Urianger", DadTrustMemberRole.Healer),
            new(4, "Y'shtola", DadTrustMemberRole.Dps),
        };

        candidates.Add(content.ExVersion == 3
            ? new DadTrustMemberCandidate(5, "Ryne", DadTrustMemberRole.Dps)
            : new DadTrustMemberCandidate(5, "Estinien", DadTrustMemberRole.Dps));

        candidates.Add(new DadTrustMemberCandidate(6, "G'raha Tia", DadTrustMemberRole.AllRounder));

        if (content.TerritoryType is >= 1097 and <= 1164)
            candidates.Add(new DadTrustMemberCandidate(7, "Zero", DadTrustMemberRole.Dps));

        if (content.ExVersion == 5)
            candidates.Add(new DadTrustMemberCandidate(7, "Krile", DadTrustMemberRole.Dps));

        return candidates;
    }

    private static bool TryBuildTrustParty(
        DadTrustPlayerRole playerRole,
        IReadOnlyList<DadTrustMemberCandidate> availableMembers,
        out List<DadTrustMemberCandidate> selectedMembers,
        out string failure)
    {
        selectedMembers = [];
        failure = string.Empty;

        var neededRoles = playerRole switch
        {
            DadTrustPlayerRole.Tank => new[] { DadTrustMemberRole.Healer, DadTrustMemberRole.Dps, DadTrustMemberRole.Dps },
            DadTrustPlayerRole.Healer => [DadTrustMemberRole.Tank, DadTrustMemberRole.Dps, DadTrustMemberRole.Dps],
            DadTrustPlayerRole.Dps => [DadTrustMemberRole.Tank, DadTrustMemberRole.Healer, DadTrustMemberRole.Dps],
            _ => [],
        };

        foreach (var role in neededRoles)
        {
            if (TryAddTrustMemberRole(role, availableMembers, selectedMembers))
                continue;

            if (TryAddTrustMemberRole(DadTrustMemberRole.AllRounder, availableMembers, selectedMembers))
                continue;

            failure = $"Trust cannot select a full NPC party for current player role {playerRole}; missing {role}.";
            return false;
        }

        return selectedMembers.Count == 3;
    }

    private static bool TryAddTrustMemberRole(
        DadTrustMemberRole role,
        IReadOnlyList<DadTrustMemberCandidate> availableMembers,
        List<DadTrustMemberCandidate> selectedMembers)
    {
        var member = availableMembers.FirstOrDefault(candidate =>
            candidate.Role == role &&
            selectedMembers.All(selected => selected.Index != candidate.Index));
        if (string.IsNullOrWhiteSpace(member.Name))
            return false;

        selectedMembers.Add(member);
        return true;
    }

    private void ResetForNewRun(string runId, DadNpcDutyQueueMode mode)
    {
        if (string.Equals(activeRunId, runId, StringComparison.OrdinalIgnoreCase) && activeMode == mode)
            return;

        activeRunId = runId;
        activeMode = mode;
        nextOpenAttemptUtc = DateTime.MinValue;
        nextSelectAttemptUtc = DateTime.MinValue;
        nextRegisterAttemptUtc = DateTime.MinValue;
        nextConfirmAttemptUtc = DateTime.MinValue;
        dutyEntryEvidenceObserved = false;
        dutyEntryTransitionLogged = false;
        transientMissingPlayerLogged = false;
        trustPartyCleared = false;
        trustPartySelected = false;
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
        trustPartyCleared = false;
        trustPartySelected = false;
    }

    private void TrySubscribeDutyState()
    {
        try
        {
            Plugin.DutyState.DutyCompleted += OnDutyCompleted;
            dutyStateSubscribed = true;
            log.Debug("[dad] NPC duty queue service subscribed to DutyCompleted.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] NPC duty queue service could not subscribe to DutyCompleted; exit fallback remains available.");
        }
    }

    private void OnDutyCompleted(Dalamud.Game.DutyState.IDutyStateEventArgs args)
        => OnDutyCompleted(args.TerritoryType.RowId);

    private void OnDutyCompleted(uint territoryId)
    {
        lastDutyCompletedTerritoryId = territoryId;
        lastDutyCompletedUtc = DateTime.UtcNow;
        log.Information("[dad] DutyCompleted observed for territory {TerritoryId}.", territoryId);
    }

    private bool TryAcceptContentsFinderConfirm(DadNpcDutyResolvedContent content)
    {
        if (DateTime.UtcNow < nextConfirmAttemptUtc)
            return false;

        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("ContentsFinderConfirm");
            if (addon == null || !addon->IsVisible)
                return false;

            var atkValues = stackalloc AtkValue[1];
            atkValues[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
            atkValues[0].Int = 8;

            log.Information("[dad] Accepting {LaneName} commence popup for {DutyName}.", content.LaneName, content.DutyName);
            addon->FireCallback(1, atkValues, true);
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

    private DadNpcDutyQueuePulse DutyEntryTransition(
        DadNpcDutyResolvedContent content,
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
                "[dad] {LaneName} duty-entry transition observed for {DutyName}; territory={TerritoryType}, requestedTerritory={RequestedTerritory}, queued={Queued}, boundByDuty={BoundByDuty}, betweenAreas={BetweenAreas}, betweenAreas51={BetweenAreas51}, loggedIn={LoggedIn}, localPlayer={LocalPlayer}.",
                content.LaneName,
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
                "[dad] Suppressing transient missing local player during {LaneName} entry for {DutyName}; loggedIn={LoggedIn}, localPlayer={LocalPlayer}, territory={TerritoryType}, requestedTerritory={RequestedTerritory}, queued={Queued}, boundByDuty={BoundByDuty}, betweenAreas={BetweenAreas}, betweenAreas51={BetweenAreas51}.",
                content.LaneName,
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
            DadNpcDutyQueuePulseKind.DutyEntryTransition,
            DadRunPhase.WaitingForQueuePop,
            DadParticipantState.QueuePending,
            $"Duty entry transition for {content.DutyName}; waiting for local player/duty truth to settle.");
    }

    private static bool TryGetTrustIndex(int ordinal, uint exVersion, out int trustIndex)
    {
        trustIndex = ordinal switch
        {
            < 0 => -1,
            _ => exVersion switch
            {
                3 => ordinal,
                4 => ordinal - 11,
                5 => ordinal - 22,
                _ => -1,
            },
        };
        return trustIndex >= 0;
    }

    private static DadTrustPlayerRole GetLocalPlayerRole()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || !player.ClassJob.IsValid)
            return DadTrustPlayerRole.Unknown;

        return player.ClassJob.RowId switch
        {
            1 or 3 or 19 or 21 or 32 or 37 => DadTrustPlayerRole.Tank,
            6 or 24 or 28 or 33 or 40 => DadTrustPlayerRole.Healer,
            36 => DadTrustPlayerRole.Limited,
            2 or 4 or 5 or 7 or 26 or 29 or 20 or 22 or 23 or 25 or 27 or 30 or 31 or 34 or 35 or 38 or 39 or 41 or 42 => DadTrustPlayerRole.Dps,
            _ => DadTrustPlayerRole.Unknown,
        };
    }

    private static bool IsDutyEntryTransition(bool isBetweenAreas, bool isBetweenAreas51, bool isRequestedTerritory)
        => isBetweenAreas || isBetweenAreas51 || isRequestedTerritory;

    private static string FormatLaneName(DadNpcDutyQueueMode mode)
        => mode == DadNpcDutyQueueMode.Trust ? "Trust" : "Duty Support";

    private static DadNpcDutyQueuePulse Active(
        DadNpcDutyResolvedContent content,
        DadNpcDutyQueuePulseKind kind,
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

    private static DadNpcDutyQueuePulse Failed(DadNpcDutyResolvedContent content, string reason)
        => new()
        {
            Kind = DadNpcDutyQueuePulseKind.Failed,
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
