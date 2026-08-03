using System.Globalization;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using dad.Models;
using dad.Services;
using Lumina.Excel.Sheets;

namespace dad.Windows;

public enum DadMainWindowTab
{
    Overview,
    Presets,
    Crew,
    Multiplayer,
    Status,
}

public enum DadPresetsWindowTab
{
    Planner,
    Scheduler,
    Queue,
    ActiveJob,
}

public sealed class MainWindow : Window, IDisposable
{
    private enum DadStatusWindowTab
    {
        CurrentActivity,
        QueueHistory,
        Readiness,
    }

    private static readonly Vector2 MinimumWindowSize = new(760f, 600f);
    private const string RosterUnassignedAccountFilter = "__unassigned";
    private const string RosterNeedsUpdateFilter = "NeedsUpdate";
    private readonly Plugin plugin;
    private readonly DadPresetCrewEditor presetCrewEditor;
    private Vector2? pendingPosition;
    private bool resetPositionConditionNextDraw;
    private string plannerDutySearch = string.Empty;
    private DadPlannerActivityMode? cachedPlannerDutySearchMode;
    private string cachedPlannerDutySearchText = string.Empty;
    private IReadOnlyList<DadPlannerDutyOption> cachedPlannerDutySearchResults = [];
    private string plannerGroupNameBuffer = string.Empty;
    private bool plannerCrewDetails;
    private string pendingDeletePlannerGroupId = string.Empty;
    private string plannerShareStatus = string.Empty;
    private string plannerShareIdOwner = string.Empty;
    private string plannerShareIdEdit = string.Empty;
    private DadShareEnvelopeDto? pendingPlannerShareImport;
    private DadShareImportPreview? pendingPlannerSharePreview;
    private bool pendingPlannerShareCommandsConfirmed;
    private string selectedScheduleId = string.Empty;
    private string schedulerScheduleNameBuffer = "Dad Schedule";
    private string schedulerAddPresetGroupId = string.Empty;
    private bool addSavedPlanToSchedule;
    private string plannerAttachScheduleId = string.Empty;
    private string pendingDeleteScheduleId = string.Empty;
    private string pendingRetryScheduleRunId = string.Empty;
    private string scheduleShareStatus = string.Empty;
    private string scheduleShareIdOwner = string.Empty;
    private string scheduleShareIdEdit = string.Empty;
    private DadShareEnvelopeDto? pendingScheduleShareImport;
    private DadShareImportPreview? pendingScheduleSharePreview;
    private bool pendingScheduleShareCommandsConfirmed;
    private string pendingDeleteAccountId = string.Empty;
    private string rosterSearch = string.Empty;
    private string rosterAccountFilter = string.Empty;
    private string rosterAssignedFilter = string.Empty;
    private string rosterVisibilityFilter = DadRosterVisibility.Active.ToString();
    private string rosterWorldDcFilter = string.Empty;
    private string rosterSourceFilter = string.Empty;
    private string rosterClientFilter = string.Empty;
    private bool rosterStaleOnly;
    private bool rosterAccountInitialized;
    private readonly HashSet<string> rosterSelectedRows = new(StringComparer.OrdinalIgnoreCase);
    private RosterFilterCacheKey? rosterFilterCacheKey;
    private IReadOnlyList<DadRosterCharacter> rosterFilteredRows = [];
    // B1: last roster-catalog cache revision rendered; when the transport bumps it (a peer pull landed or the
    // coordinator pushed a fresh projection), the roster section re-merges from cache without a second click.
    private long lastRosterCatalogCacheRevision = -1;
    private readonly Dictionary<uint, string> classJobAbbrevCache = new();
    private string selectedProfileOwner = string.Empty;
    private string selectedProfileAccount = string.Empty;
    private string selectedProfileCharacter = string.Empty;
    private long selectedProfileAccountRevision;
    private long selectedProfileRevision;
    private CharacterConfig profileDraft = new();
    private string profileSaveStatus = string.Empty;
    private string draftPlannerCompletionCommands = string.Empty;
    private string plannerCompletionCommandValidation = string.Empty;
    private string plannerCompletionDraftOwner = string.Empty;
    private DadMainWindowTab? pendingMainTab;
    private DadPresetsWindowTab? pendingPresetsTab;
    private DadStatusWindowTab? pendingStatusTab;
    private DadMainWindowTab? deferredMainTab;
    private DadPresetsWindowTab? deferredPresetsTab;
    private DadStatusWindowTab? deferredStatusTab;

    private sealed record PlannerLaneCardView(
        DadPlannerLaneDefinition Lane,
        bool IsSelected,
        string MaturityLabel,
        string PartySizeLabel,
        string StartabilityLabel,
        string FirstBlockerLabel,
        int BlockerCount,
        string RuntimeLabel);

    private sealed record JobLevelDisplay(string Summary, string Tooltip);

    private sealed record JobLevelEntry(uint? JobId, string Abbreviation, int? Level);

    private sealed record RosterFilterCacheKey(
        long CatalogRevision,
        long TransportRevision,
        string Search,
        string Account,
        string Assigned,
        string Visibility,
        string WorldDc,
        string Source,
        string Client,
        bool StaleOnly);

    public MainWindow(Plugin plugin) : base($"{PluginInfo.DisplayName}##Main", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        presetCrewEditor = new DadPresetCrewEditor(plugin);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumWindowSize,
            MaximumSize = new Vector2(1800f, 1600f),
        };
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public void ResetToOrigin() => QueuePosition(new Vector2(1f, 1f));

    public void OpenTab(DadMainWindowTab tab, DadPresetsWindowTab? presetsTab = null)
    {
        NormalizeNavigation(ref tab, ref presetsTab, out var statusTab);
        pendingMainTab = tab;
        pendingPresetsTab = presetsTab;
        pendingStatusTab = statusTab;
        IsOpen = true;
    }

    public void QueueRandomVisibleJump()
    {
        var viewport = ImGui.GetMainViewport();
        var minX = viewport.WorkPos.X + 1f;
        var minY = viewport.WorkPos.Y + 1f;
        var maxX = MathF.Max(minX, viewport.WorkPos.X + viewport.WorkSize.X - MinimumWindowSize.X - 24f);
        var maxY = MathF.Max(minY, viewport.WorkPos.Y + viewport.WorkSize.Y - MinimumWindowSize.Y - 24f);
        var x = minX + ((float)Random.Shared.NextDouble() * MathF.Max(1f, maxX - minX));
        var y = minY + ((float)Random.Shared.NextDouble() * MathF.Max(1f, maxY - minY));
        QueuePosition(new Vector2(x, y));
    }

    private void QueuePosition(Vector2 position)
    {
        pendingPosition = position;
        IsOpen = true;
    }

    private void ApplyPendingPositionChange()
    {
        if (pendingPosition is null)
        {
            if (resetPositionConditionNextDraw)
            {
                PositionCondition = ImGuiCond.FirstUseEver;
                resetPositionConditionNextDraw = false;
            }

            return;
        }

        Position = pendingPosition.Value;
        PositionCondition = ImGuiCond.Always;
        pendingPosition = null;
        resetPositionConditionNextDraw = true;
    }

    public override void Draw()
    {
        ApplyPendingPositionChange();

        var configuration = plugin.Configuration;
        var profile = plugin.ConfigManager.GetActiveConfig();
        var runState = plugin.GetVisibleRunState();
        var activityDisplay = DadActivityDisplaySelector.Select(
            runState,
            plugin.SchedulerService.CurrentState,
            plugin.Configuration.ActiveScheduleRun ?? new DadScheduleRunState());
        var characterPool = plugin.CharacterIntelligenceService.CurrentPool;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

        if (deferredMainTab.HasValue)
        {
            pendingMainTab = deferredMainTab;
            pendingPresetsTab = deferredPresetsTab;
            pendingStatusTab = deferredStatusTab;
            deferredMainTab = null;
            deferredPresetsTab = null;
            deferredStatusTab = null;
        }

        DrawShellHeader(configuration, profile, runState, characterPool, version);
        DrawConfigurationPersistenceWarning();
        DrawActiveRunBanner(runState, activityDisplay.Run);
        ImGui.Spacing();

        if (ImGui.BeginTabBar("dad-main-tabs"))
        {
            if (ImGui.BeginTabItem("Home", BuildMainTabFlags(DadMainWindowTab.Overview)))
            {
                DrawOverviewTab(runState, profile);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Plan", BuildPlanningMainTabFlags(showPlanner: true)))
            {
                DrawPresetPlannerTab(characterPool, runState);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Schedules", BuildPlanningMainTabFlags(showPlanner: false)))
            {
                DrawSchedulesTab(runState);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Crew", BuildMainTabFlags(DadMainWindowTab.Crew)))
            {
                DrawCrewTab(characterPool, runState);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Clients", BuildMainTabFlags(DadMainWindowTab.Multiplayer)))
            {
                DrawMultiplayerTab(characterPool, runState);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Status", BuildMainTabFlags(DadMainWindowTab.Status)))
            {
                DrawStatusTab(characterPool, runState, profile, activityDisplay.Run);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        pendingMainTab = null;
        pendingPresetsTab = null;
        pendingStatusTab = null;
    }

    private void DrawShellHeader(
        Configuration configuration,
        CharacterConfig profile,
        DadVisibleRunState runState,
        DadCharacterPool characterPool,
        string version)
    {
        var localRun = runState.LocalRun;
        var authorityRun = runState.AuthorityRun;
        var role = configuration.RunAsServerDad ? "Coordinator" : "Client";
        var routeReady = configuration.RunAsServerDad || plugin.HasServerDadAuthority();

        if (DadUi.BeginCard("dad-shell-header", 88f))
        {
            DadUi.Heading("DAD CONTROL CENTER", PluginInfo.Summary);
            DadUi.Badge($"v{version}", DadUiTone.Accent);
            ImGui.SameLine();
            DadUi.Badge(role, DadUiTone.Info);
            ImGui.SameLine();
            DadUi.Badge(configuration.PluginEnabled ? "Enabled" : "Paused",
                configuration.PluginEnabled ? DadUiTone.Success : DadUiTone.Neutral);
            ImGui.SameLine();
            DadUi.Badge(profile.Enabled ? "Character allowed" : "Character not allowed",
                profile.Enabled ? DadUiTone.Success : DadUiTone.Warning);
            ImGui.SameLine();
            DadUi.Badge(routeReady ? $"{characterPool.PeerTransport.ConnectedPeerCount} client(s) connected" : "Coordinator unavailable",
                routeReady ? DadUiTone.Success : DadUiTone.Warning);
            DadUi.EndCard();
        }

        var pluginEnabled = configuration.PluginEnabled;
        if (ImGui.Checkbox("DAD enabled", ref pluginEnabled))
            plugin.SetPluginEnabled(pluginEnabled, printStatus: false);

        ImGui.SameLine();
        var profileEnabled = profile.Enabled;
        if (ImGui.Checkbox("Allow DAD to automate this character", ref profileEnabled))
        {
            plugin.ConfigManager.UpdateActiveConfig(active => active.Enabled = profileEnabled);
            plugin.UpdateDtrBar();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Controls whether DAD may include and automate the active character.");

        if (DadUi.Button("Settings", DadUiTone.Accent))
            plugin.ToggleConfigUi();

        ImGui.SameLine();
        if (DadUi.Button("Guide", DadUiTone.Accent))
            plugin.OpenSetupWizard();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open the six guided DAD workflows at any time.");

        ImGui.SameLine();
        if (DadUi.Button(plugin.KrangleService.Enabled ? "Show character names" : "Hide character names"))
            plugin.ToggleKrangleOperatorNames();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Changes local operator labels only. Run contracts keep their real identities.");

        ImGui.SameLine();
        if (DadUi.Button("Support on Ko-fi", DadUiTone.Accent))
            Util.OpenLink(PluginInfo.SupportUrl);

        if (Plugin.IsBusy(localRun) || Plugin.IsBusy(authorityRun))
        {
            ImGui.SameLine();
            if (DadUi.Button("Cancel active run", DadUiTone.Danger))
                plugin.CancelActiveRunFromShell();
        }

    }

    private void DrawActiveRunBanner(
        DadVisibleRunState runState,
        DadRunResult displayRun)
    {
        var activeRun = displayRun;
        var plannerLocked = IsPlannerLocked(runState);
        var phase = DadOperatorPhaseText.FormatPhaseLabel(activeRun);
        var module = activeRun.ModuleId == DadModuleId.None ? "No module" : activeRun.ModuleId.ToString();
        var keyStatus = BuildActiveRunKeyStatus(activeRun);

        if (DadUi.BeginCard("dad-active-run-banner", plannerLocked ? 82f : 66f))
        {
            DrawStateBadge("Phase", phase);
            ImGui.SameLine();
            DrawStateBadge("Module", module);
            ImGui.SameLine();
            DrawStateBadge("Status", activeRun.Status.ToString());
            if (plannerLocked)
            {
                ImGui.SameLine();
                if (DadUi.Button("Cancel active run##top-banner", DadUiTone.Danger))
                    plugin.CancelActiveRunFromShell();
            }

            ImGui.TextWrapped(keyStatus);
            DadUi.EndCard();
        }
    }

    private void DrawConfigurationPersistenceWarning()
    {
        var state = plugin.GetConfigurationPersistenceState();
        if (!state.HasFault)
            return;

        ImGui.Spacing();
        if (!DadUi.BeginCard("dad-configuration-persistence-warning"))
            return;

        ImGui.PushStyleColor(ImGuiCol.Text, DadUi.Warning);
        ImGui.TextWrapped("Configuration save failed. Changes are memory-only until a save succeeds.");
        ImGui.PopStyleColor();
        if (!string.IsNullOrWhiteSpace(state.FailureSummary))
            ImGui.TextDisabled(state.FailureSummary);
        if (state.NextRetryAtUtc.HasValue && !state.IsLatched)
            ImGui.TextDisabled($"Automatic retry scheduled for {state.NextRetryAtUtc.Value.ToLocalTime():T}.");
        if (DadUi.Button("Retry save", DadUiTone.Warning))
            plugin.QueueConfigurationPersistenceRetry();
        DadUi.EndCard();
    }

    private void DrawOverviewTab(DadVisibleRunState runState, CharacterConfig profile)
        => DrawOverviewCompact(runState, profile);

    private void DrawStatusCurrentActivityDetails(
        DadVisibleRunState runState,
        CharacterConfig profile,
        DadRunResult displayRun)
    {
        var activeRun = displayRun;
        var localRun = runState.LocalRun;
        var authorityRun = runState.AuthorityRun;
        var authorityView = runState.AuthorityView;
        var localParticipant = plugin.PresenceService.CurrentParticipant;
        var activeWarnings = activeRun.Warnings
            .Where(static warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var progressTotal = Math.Max(activeRun.TotalTaskCount, activeRun.RequestedTaskCount);

        DrawSectionHeader("Authority Snapshot", "Current control-plane state and DTR-aligned operator phase.");
        DrawStatusRow("Operator phase", DadOperatorPhaseText.FormatPhaseLabel(activeRun));
        DrawStatusRow("Authority view", $"{authorityView.StateText} | {authorityView.ClientPerspectiveText}");
        DrawStatusRow("Authority timeline", authorityView.TimelineText);
        DrawStatusRow("Authority freshness", authorityView.FreshnessText);
        DrawStatusRow("Authority owner", authorityView.OwnershipText);
        DrawStatusRow("Authority worker", authorityView.AuthorityWorkerText);
        DrawStatusRow("Authority endpoint", authorityView.AuthorityEndpointText);
        DrawStatusRow("Authority payload", authorityView.PayloadText);
        DrawStatusRow("This instance", DadStatusText.FormatWorkerRole(plugin.PresenceService.CurrentParticipant.WorkerRole));
        DrawStatusRow("Local-only", localRun.LocalOnlyEnabled ? "Enabled" : "Disabled");
        DrawStatusRow("IPC ready", plugin.RunCoordinatorService.IsReady ? "Yes" : "No");
        DrawStatusRow("Duty IPC / Questionable", FormatDutyIpcAndBridgeStatus(plugin.DutyIpcService.GetStatus(), plugin.QuestionableBridge.GetStatus()));

        DrawSectionHeader("Active Request / Run", "Live request truth from visible authority/local state.");
        if (activeRun.Status == DadRunStatus.Idle)
        {
            DrawMutedNotice("No active Dad request. Planner and runtime are idle.");
        }
        else
        {
            DrawStatusRow("Run status", activeRun.Summary);
            DrawStatusRow("Run state", $"{activeRun.Status} / {activeRun.Phase} / {activeRun.ModuleId}");
            DrawStatusRow("Request id", FormatText(activeRun.RequestId, "(none)"));
            DrawStatusRow("Requested by", FormatText(activeRun.RequestedBy, "(unknown)"));
            DrawStatusRow("Authority mode", DadStatusText.FormatAuthorityMode(activeRun.AuthorityMode));
            DrawStatusRow("Transport", activeRun.TransportMode.ToString());
            DrawStatusRow("Role / worker", $"{activeRun.Role} / {DadStatusText.FormatWorkerRole(activeRun.WorkerRole)}");
            DrawStatusRow("Leader client", FormatText(activeRun.LeaderClientInstanceId, "(none)"));
            DrawStatusRow("Cancellation", activeRun.CancellationState.ToString());
            DrawStatusRow("Payload", activeRun.Request?.DescribeRequestedWork() ?? authorityView.PayloadText);
        }

        DrawSectionHeader("Executor / Task Progress", "Existing runtime executor/task projection only.");
        if (activeRun.Status == DadRunStatus.Idle &&
            activeRun.CurrentExecutorStatus.ModuleId == DadModuleId.None &&
            string.IsNullOrWhiteSpace(activeRun.ActiveTaskName))
        {
            DrawMutedNotice("No active executor or task progress.");
        }
        else
        {
            DrawStatusRow("Task progress", $"{activeRun.CompletedTaskCount}/{Math.Max(1, progressTotal)} complete");
            DrawStatusRow("Active task", string.IsNullOrWhiteSpace(activeRun.ActiveTaskName) ? "(none)" : $"{activeRun.ActiveTaskIndex}/{Math.Max(1, activeRun.TotalTaskCount)} {activeRun.ActiveTaskName}");
            DrawStatusRow("Task detail", FormatText(activeRun.ActiveTaskStatus, activeRun.Summary));
            DrawStatusRow("Executor", FormatExecutorStatus(activeRun.CurrentExecutorStatus));
            DrawStatusRow("Participants", activeRun.Participants.Count.ToString(CultureInfo.InvariantCulture));
            DrawStatusRow("Local participant", $"{localParticipant.State} | {localParticipant.ClaimState} / {localParticipant.LeaseState}");
            DrawStatusRow("Local assignment", string.IsNullOrWhiteSpace(localParticipant.AssignedSlotId) ? "(none)" : localParticipant.AssignedSlotId);
            DrawStatusRow("Local participant status", FormatText(localParticipant.StatusText, "(none)"));
        }

        DrawDutySupportRuntimeSection(activeRun);

        DrawSectionHeader("Blockers And Warnings", "Real runtime blockers first; no fabricated warning state.");
        if (string.IsNullOrWhiteSpace(activeRun.BlockedReason) &&
            string.IsNullOrWhiteSpace(activeRun.FailureReason) &&
            activeWarnings.Count == 0)
        {
            DrawMutedNotice("No active blockers or warnings.");
        }
        else
        {
            if (DadOperatorPhaseText.HasBlockingFailure(activeRun) && !string.IsNullOrWhiteSpace(activeRun.BlockedReason))
                DrawStatusRow("Blocked", activeRun.BlockedReason);
            else if (!string.IsNullOrWhiteSpace(activeRun.BlockedReason))
                DrawStatusRow("Runtime note", activeRun.BlockedReason);
            if (!string.IsNullOrWhiteSpace(activeRun.FailureReason))
                DrawStatusRow("Failure", activeRun.FailureReason);
            if (activeWarnings.Count > 0)
                DrawStatusRow("Warnings", FormatOperatorText(string.Join(" | ", activeWarnings), "(none)"));
        }

        DrawSectionHeader("Operator Next Action", "Single next step aligned with current authority/runtime truth.");
        DrawStatusRow("Next action", BuildOverviewNextAction(runState, profile, displayRun));
        DrawStatusRow("Account", FormatOperatorAccountLabel(plugin.ConfigManager.GetCurrentAccount()?.AccountAlias, plugin.ConfigManager.CurrentAccountId));
        DrawStatusRow("Profile", FormatOperatorCharacterKey(plugin.ConfigManager.SelectedCharacterKey, "(Account default)"));
        DrawStatusRow("Profile notes", FormatOperatorText(profile.TargetNotes, "(none)"));
    }

    private void DrawOverviewCompact(DadVisibleRunState runState, CharacterConfig profile)
    {
        DadUi.Heading("HOME", "Start with a guided task, then open the focused workspace for the job you need to finish.");
        DrawHomeGuidedTasks();

        DrawSectionHeader("Expert shortcuts", "Direct editors for repeat users who already know what they need.");
        DrawHomeExpertShortcuts();
    }

    private void DrawHomeGuidedTasks()
    {
        DrawSectionHeader("Guided tasks", "Six complete workflows with live progress and the first blocker.");
        var flows = new[]
        {
            DadGuideFlow.NameDad,
            DadGuideFlow.Coordinator,
            DadGuideFlow.Client,
            DadGuideFlow.FirstPreset,
            DadGuideFlow.Crew,
            DadGuideFlow.Schedule,
        };
        var useTwoColumns = ImGui.GetContentRegionAvail().X >= ImGui.GetFontSize() * 42f;
        if (!ImGui.BeginTable("dad-home-guided-tasks", useTwoColumns ? 2 : 1, ImGuiTableFlags.SizingStretchSame))
            return;

        foreach (var flow in flows)
        {
            var progress = DadGuideReadiness.Build(plugin, flow);
            var restricted = DadGuideReadiness.TryGetConnectionFlowRestriction(plugin, flow, out var restriction);
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(restricted);
            if (!DadUi.BeginCard($"dad-home-guide-{flow}", 122f))
            {
                ImGui.EndDisabled();
                continue;
            }
            DadUi.Badge(
                progress.Ready ? "Ready" : $"{progress.Complete}/{progress.Total} ready",
                progress.Ready ? DadUiTone.Success : DadUiTone.Warning);
            DadUi.Heading(progress.Title, progress.Ready ? "Review or change the completed setup." : $"Next: {progress.NextAction}");
            if (DadUi.Button($"Open guide##dad-home-guide-open-{flow}", DadUiTone.Accent))
                plugin.OpenSetupWizard(flow);
            if (restricted && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(restriction);
            DadUi.EndCard();
            ImGui.EndDisabled();
        }

        ImGui.EndTable();
    }

    private void DrawHomeExpertShortcuts()
    {
        if (!ImGui.BeginTable("dad-home-quick-actions", 2, ImGuiTableFlags.SizingStretchSame))
            return;

        DrawHomeActionCard(
            "dad-home-plan",
            "Plan a run",
            "Choose the activity, party, jobs, loot rules, and stop condition.",
            "Open Plan",
            () => NavigateWithinMain(DadMainWindowTab.Presets, DadPresetsWindowTab.Planner));
        DrawHomeActionCard(
            "dad-home-schedules",
            "Schedules",
            "Chain saved presets, repeat them, or run them at daily reset.",
            "Open Schedules",
            () => NavigateWithinMain(DadMainWindowTab.Presets, DadPresetsWindowTab.Scheduler));
        DrawHomeActionCard(
            "dad-home-crew",
            "Crew",
            "Review roster health, account ownership, and character permissions.",
            "Manage Crew",
            () => NavigateWithinMain(DadMainWindowTab.Crew));
        DrawHomeActionCard(
            "dad-home-clients",
            "Clients",
            "Check Coordinator routing, connected clients, and readiness.",
            "View Clients",
            () => NavigateWithinMain(DadMainWindowTab.Multiplayer));
        DrawHomeActionCard(
            "dad-home-status",
            "Status",
            "Follow current activity, queue history, detailed readiness, and diagnostics.",
            "Open Status",
            () => NavigateToStatus(DadStatusWindowTab.CurrentActivity));

        ImGui.EndTable();
    }

    private static void DrawHomeActionCard(
        string id,
        string title,
        string detail,
        string buttonLabel,
        System.Action open)
    {
        ImGui.TableNextColumn();
        if (DadUi.BeginCard(id, 112f))
        {
            DadUi.Heading(title, detail);
            if (DadUi.Button($"{buttonLabel}##{id}", DadUiTone.Accent))
                open();
            DadUi.EndCard();
        }
    }

    private void NavigateWithinMain(DadMainWindowTab tab, DadPresetsWindowTab? presetsTab = null)
    {
        NormalizeNavigation(ref tab, ref presetsTab, out var statusTab);
        deferredMainTab = tab;
        deferredPresetsTab = presetsTab;
        deferredStatusTab = statusTab;
    }

    private void NavigateToStatus(DadStatusWindowTab statusTab)
    {
        deferredMainTab = DadMainWindowTab.Status;
        deferredPresetsTab = null;
        deferredStatusTab = statusTab;
    }

    private static void NormalizeNavigation(
        ref DadMainWindowTab tab,
        ref DadPresetsWindowTab? presetsTab,
        out DadStatusWindowTab? statusTab)
    {
        statusTab = tab == DadMainWindowTab.Status ? DadStatusWindowTab.CurrentActivity : null;
        if (tab != DadMainWindowTab.Presets || !presetsTab.HasValue)
            return;

        if (presetsTab == DadPresetsWindowTab.Queue)
        {
            tab = DadMainWindowTab.Status;
            presetsTab = null;
            statusTab = DadStatusWindowTab.QueueHistory;
        }
        else if (presetsTab == DadPresetsWindowTab.ActiveJob)
        {
            tab = DadMainWindowTab.Status;
            presetsTab = null;
            statusTab = DadStatusWindowTab.CurrentActivity;
        }
    }

    private void DrawMultiplayerTab(DadCharacterPool characterPool, DadVisibleRunState runState)
    {
        var participants = BuildVisibleParticipants(characterPool);
        DrawMultiplayerCompact(characterPool, runState, participants, GetActiveRun(runState));
    }

    private List<DadParticipantSnapshot> BuildVisibleParticipants(DadCharacterPool characterPool)
    {
        var participants = new List<DadParticipantSnapshot> { plugin.PresenceService.BuildSnapshotCopy() };
        participants.AddRange(characterPool.PeerTransport.KnownParticipants.Select(static participant => participant.Clone()));
        return participants
            .OrderBy(static participant => participant.ManagedAccountAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.ActiveCharacterKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void DrawStatusReadinessDetails(DadCharacterPool characterPool, DadVisibleRunState runState)
    {
        var localRun = runState.LocalRun;
        var authorityRun = runState.AuthorityRun;
        var authorityView = runState.AuthorityView;
        var xadbStatus = characterPool.XadbStatus;
        var peerTransport = characterPool.PeerTransport;
        var localParticipant = plugin.PresenceService.BuildSnapshotCopy();
        var participants = BuildVisibleParticipants(characterPool);
        var activeRun = GetActiveRun(runState);

        var authorityParticipant = authorityRun.Participants.FirstOrDefault(static participant => participant.IsAuthority)
                                  ?? participants.FirstOrDefault(candidate =>
                                      string.Equals(candidate.WorkerSessionId, authorityRun.AuthorityWorkerSessionId.ToString(), StringComparison.OrdinalIgnoreCase));
        var readyCount = participants.Count(static participant => participant.IsEligibleForRun);
        var postArReadyCount = participants.Count(static participant => participant.PostArReady);
        var staleCount = participants.Count(IsParticipantStale);
        var assignedParticipants = participants
            .Where(static participant => !string.IsNullOrWhiteSpace(participant.AssignedSlotId))
            .OrderBy(static participant => participant.AssignedSlotId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var leaseCount = authorityRun.Leases.Count;
        var partySnapshotCount = participants.Count(static participant => participant.Character.PartyRosterCount.HasValue);

        if (ImGui.SmallButton("Refresh local"))
            plugin.RefreshCharacterPoolFromShell();

        ImGui.SameLine();
        if (ImGui.SmallButton("Save local XADB"))
            plugin.SaveLocalCharacterToXadbFromShell();

        ImGui.SameLine();
        if (ImGui.SmallButton("Request peer snapshots"))
            plugin.RequestPeerSnapshotsFromShell();

        ImGui.SameLine();
        if (ImGui.SmallButton("Copy pool JSON"))
        {
            ImGui.SetClipboardText(plugin.CharacterIntelligenceService.GetCharacterPoolJson());
            plugin.PrintStatus("Copied Dad character pool JSON.");
        }

        DrawSectionHeader("Cancellation And Authority Summary", "Current runtime owner, cancellation state, and visible run truth.");
        DrawStatusRow("Operator phase", DadOperatorPhaseText.FormatPhaseLabel(activeRun));
        DrawStatusRow("Authority view", $"{authorityView.StateText} | {authorityView.ClientPerspectiveText}");
        DrawStatusRow("Authority timeline", authorityView.TimelineText);
        DrawStatusRow("Authority freshness", authorityView.FreshnessText);
        DrawStatusRow("Authority owner", authorityView.OwnershipText);
        DrawStatusRow("Authority status", DadStatusText.FormatAuthorityStatus(
            authorityParticipant?.WorkerRole ?? peerTransport.AuthorityRole,
            authorityRun.AuthorityWorkerSessionId,
            authorityRun.AuthorityEndpoint,
            authorityRun.AuthorityMode));
        DrawStatusRow("Visible run", $"{activeRun.Status} / {activeRun.Phase} / {activeRun.ModuleId}");
        DrawStatusRow("Cancellation", activeRun.CancellationState.ToString());
        DrawStatusRow("Task payload", activeRun.Request?.DescribeRequestedWork() ?? authorityView.PayloadText);
        DrawDutySupportRuntimeSection(activeRun);

        DrawSectionHeader("Readiness And Freshness", "Participant readiness, heartbeat freshness, and snapshot coverage.");
        DrawStatusRow("XADB local", xadbStatus.Availability);
        DrawStatusRow("Last save", FormatTime(xadbStatus.LastSaveUtc));
        DrawStatusRow("Snapshot version", xadbStatus.SnapshotVersion?.ToString(CultureInfo.InvariantCulture) ?? "?");
        DrawStatusRow("Snapshot quality", string.IsNullOrWhiteSpace(xadbStatus.SnapshotQuality) ? "(unknown)" : xadbStatus.SnapshotQuality);
        DrawStatusRow("Peer transport", peerTransport.Availability);
        DrawStatusRow("Connected peers", peerTransport.ConnectedPeerCount.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Last peer request", FormatTime(peerTransport.LastRequestUtc));
        DrawStatusRow("Configured endpoint", FormatText(peerTransport.ConfiguredEndpoint, "(none)"));
        DrawStatusRow("Advertised endpoint", FormatText(peerTransport.AdvertisedEndpoint, "(none)"));
        DrawStatusRow("Listener", FormatText(peerTransport.ListenerEndpoint, "(none)"));
        DrawStatusRow("LAN secret", $"{(peerTransport.SharedSecretRequired ? "required" : "loopback optional")} | configured {(peerTransport.SharedSecretConfigured ? "yes" : "no")}");
        if (!string.IsNullOrWhiteSpace(peerTransport.LastAuthOrProtocolError))
            DrawStatusRow("Auth/protocol", peerTransport.LastAuthOrProtocolError);
        DrawStatusRow("Roster publish", $"epoch {FormatText(peerTransport.HubRosterPublishEpochId, "(none)")} | generation {peerTransport.HubRosterPublishGeneration.ToString(CultureInfo.InvariantCulture)}");
        DrawStatusRow("Hub roster", $"{peerTransport.PublishedParticipantCount.ToString(CultureInfo.InvariantCulture)} published | {peerTransport.KnownParticipantCount.ToString(CultureInfo.InvariantCulture)} known");
        DrawStatusRow("Transport queues", $"{peerTransport.PendingTransportEventCount.ToString(CultureInfo.InvariantCulture)} event(s) | {peerTransport.PendingOutboundOperationCount.ToString(CultureInfo.InvariantCulture)} outbound");
        DrawStatusRow("Last publish", $"{FormatTime(peerTransport.LastRosterPublishUtc)} | {FormatText(peerTransport.LastRosterPublishReason, "(none)")}");
        if (peerTransport.CoalescedRosterPublishCount > 0)
            DrawStatusRow("Coalesced publishes", peerTransport.CoalescedRosterPublishCount.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(peerTransport.LastTransportTimeoutSummary))
            DrawStatusRow("Transport timeout", peerTransport.LastTransportTimeoutSummary);
        DrawStatusRow("Participants discovered", participants.Count.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Eligible for run", readyCount.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Post-AR ready", postArReadyCount.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Stale heartbeats", staleCount.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Assigned slots", assignedParticipants.Count.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Active leases", leaseCount.ToString(CultureInfo.InvariantCulture));

        DrawSectionHeader("Participant Table", "Runtime coordination view of workers, assignments, and current readiness.");
        if (participants.Count == 1 && !participants[0].IsAuthority && !participants[0].IsAvailable)
        {
            DrawMutedNotice("No Dad workers discovered yet.");
        }
        else if (ImGui.BeginTable("dad-participant-status", 10, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Role / owner");
            ImGui.TableSetupColumn("Account");
            ImGui.TableSetupColumn("Character");
            ImGui.TableSetupColumn("Assignment");
            ImGui.TableSetupColumn("Claim / lease");
            ImGui.TableSetupColumn("Eligibility");
            ImGui.TableSetupColumn("Fresh");
            ImGui.TableSetupColumn("Party");
            ImGui.TableSetupColumn("Worker");
            ImGui.TableSetupColumn("Status");
            ImGui.TableHeadersRow();

            foreach (var participant in participants)
            {
                var participantCharacter = ResolveParticipantCharacter(characterPool, participant);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(DadStatusText.FormatParticipantOwner(participant));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorAccountLabel(participant.ManagedAccountAlias, participant.ManagedAccountKey.ToString()));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorCharacterKey(participant.ActiveCharacterKey.ToString(), "(unknown)"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(participant.AssignedSlotId) ? "(unassigned)" : participant.AssignedSlotId);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{participant.ClaimState} / {participant.LeaseState}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(participant.PostArReady ? "post-AR eligible" : participant.IsEligibleForRun ? "eligible / connected" : "waiting");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatParticipantFreshness(participant, participantCharacter));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(participantCharacter == null ? "-" : FormatParty(participantCharacter));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(participant.WorkerSessionId.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorText(FormatParticipantStatus(participant), "(none)"));
            }

            ImGui.EndTable();
        }

        DrawSectionHeader("Assignment / Slot State", "Slot ownership and participant assignment truth from current runtime models.");
        if (assignedParticipants.Count == 0)
        {
            if (activeRun.Status == DadRunStatus.Idle)
                DrawMutedNotice("No active slot assignments.");
            else
                DrawPlaceholderNotice("Placeholder: slot assignment rows will populate when runtime lane state issues assignments.");
        }
        else if (ImGui.BeginTable("dad-participant-assignments", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Slot");
            ImGui.TableSetupColumn("Account");
            ImGui.TableSetupColumn("Character");
            ImGui.TableSetupColumn("Worker");
            ImGui.TableSetupColumn("State");
            ImGui.TableSetupColumn("Claim");
            ImGui.TableSetupColumn("Lease");
            ImGui.TableHeadersRow();

            foreach (var participant in assignedParticipants)
            {
                var participantCharacter = ResolveParticipantCharacter(characterPool, participant);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(participant.AssignedSlotId);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorAccountLabel(participant.ManagedAccountAlias, participant.ManagedAccountKey.ToString()));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorCharacterKey(participant.ActiveCharacterKey.ToString(), "(unknown)"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(participant.WorkerSessionId.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{participant.State} | {FormatParticipantFreshness(participant, participantCharacter)}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(participant.ClaimState.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(participant.LeaseState.ToString());
            }

            ImGui.EndTable();
        }

        DrawSectionHeader("Claim / Lease State", "Authority lease records only. Placeholder rows mark missing backend projections.");
        if (authorityRun.Leases.Count == 0)
        {
            if (activeRun.Status == DadRunStatus.Idle)
                DrawMutedNotice("No active leases.");
            else
                DrawPlaceholderNotice("Placeholder: lease detail appears once this run emits authority lease records.");
        }
        else if (ImGui.BeginTable("dad-lease-state", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Slot");
            ImGui.TableSetupColumn("Account");
            ImGui.TableSetupColumn("Character");
            ImGui.TableSetupColumn("Worker");
            ImGui.TableSetupColumn("Issued");
            ImGui.TableSetupColumn("Expires");
            ImGui.TableSetupColumn("Summary");
            ImGui.TableHeadersRow();

            foreach (var lease in authorityRun.Leases.OrderBy(static lease => lease.SlotId, StringComparer.OrdinalIgnoreCase))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(lease.SlotId);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorAccountLabel("Account", lease.AssignedAccountKey.ToString()));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorCharacterKey(lease.AssignedCharacterKey.ToString(), "(none)"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(lease.OwningWorkerSessionId.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatTime(lease.IssuedUtc));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatTime(lease.ExpiresUtc));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorText(FormatText(lease.Summary, lease.State.ToString()), "(none)"));
            }

            ImGui.EndTable();
        }

        DrawSectionHeader("Party Verification Summary", "Use existing local party truth first; mark missing distributed verification as placeholder.");
        DrawStatusRow("PartyList", Plugin.PartyList.Length.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Local participant", $"{localParticipant.State} | slot {FormatText(localParticipant.AssignedSlotId, "(none)")}");
        DrawStatusRow("Local character party", localParticipant.Character.ContentId == 0
            ? "(unknown)"
            : $"{FormatParty(localParticipant.Character)} | {FormatText(localParticipant.Character.TerritoryName, "unknown")}");
        DrawStatusRow("Worker snapshots with party data", partySnapshotCount.ToString(CultureInfo.InvariantCulture));
        DrawPlaceholderNotice("Placeholder: distributed party verification summary is not implemented yet. Current view uses PartyList/ObjectTable local truth plus worker snapshot party counts only.");
    }

    private void DrawMultiplayerCompact(
        DadCharacterPool characterPool,
        DadVisibleRunState runState,
        IReadOnlyList<DadParticipantSnapshot> participants,
        DadRunResult activeRun)
    {
        var configuration = plugin.Configuration;
        var profile = plugin.ConfigManager.GetActiveConfig();
        var transport = characterPool.PeerTransport;
        var readyCount = participants.Count(static participant => participant.IsEligibleForRun);
        var staleCount = participants.Count(IsParticipantStale);
        var assignedCount = participants.Count(static participant => !string.IsNullOrWhiteSpace(participant.AssignedSlotId));
        var endpoint = configuration.RunAsServerDad
            ? $"{configuration.ServerListenHost}:{configuration.ServerListenPort}"
            : $"{configuration.ServerDadHost}:{configuration.ServerDadPort}";
        var firstBlocker = !configuration.PluginEnabled
            ? "DAD is disabled."
            : !profile.Enabled
                ? "This character is not allowed."
                : transport.SharedSecretRequired && !transport.SharedSecretConfigured
                    ? "The LAN shared secret is missing."
                    : configuration.RunAsServerDad
                        ? string.IsNullOrWhiteSpace(transport.ListenerEndpoint)
                            ? "The Coordinator listener is not ready."
                            : transport.ConnectedPeerCount == 0 ? "No Client is connected yet." : string.Empty
                        : !transport.AuthorityRoutable && !plugin.HasServerDadAuthority()
                            ? FormatText(transport.LastAuthOrProtocolError, "The Coordinator authority is not authenticated or routable.")
                            : string.Empty;
        var nextAction = string.IsNullOrWhiteSpace(firstBlocker)
            ? "Connection is ready. Build or review the crew and preset."
            : firstBlocker;

        DadUi.Heading("CLIENTS", "Understand this client's role, route, security, connected crew, and first required action.");
        if (DadUi.Button(configuration.RunAsServerDad ? "Guide: Coordinator setup" : "Guide: Client connection", DadUiTone.Accent))
            plugin.OpenSetupWizard(configuration.RunAsServerDad ? DadGuideFlow.Coordinator : DadGuideFlow.Client);

        DrawSectionHeader("Connection", "The normal operator view; raw transport counters stay under /dad debug.");
        DrawStatusRow("Role", configuration.RunAsServerDad ? "Coordinator" : "Client");
        DrawStatusRow("Endpoint", endpoint);
        DrawStatusRow("Connection", FormatText(transport.ConnectionStatus, transport.Availability));
        DrawStatusRow("Security", transport.SharedSecretRequired
            ? transport.SharedSecretConfigured ? "LAN secret configured" : "LAN secret missing"
            : "Loopback; secret optional");
        DrawStatusRow("Connected clients", transport.ConnectedPeerCount.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Authority", $"{runState.AuthorityView.StateText} | {runState.AuthorityView.FreshnessText}");
        DrawStatusRow("Visible run", activeRun.Status == DadRunStatus.Idle
            ? "Idle."
            : $"{activeRun.ModuleId} | {DadOperatorPhaseText.FormatPhaseLabel(activeRun)} | {activeRun.Status}");
        DrawStatusRow("Participants", $"{participants.Count} discovered | {readyCount} eligible | {assignedCount} assigned | {staleCount} stale");
        DrawStatusRow("First blocker", FormatText(firstBlocker, "None"));
        DrawStatusRow("Next action", nextAction);

        if (!string.IsNullOrWhiteSpace(activeRun.BlockedReason))
            DrawStatusRow(DadOperatorPhaseText.HasBlockingFailure(activeRun) ? "Blocker" : "Runtime note", activeRun.BlockedReason);

        DrawSectionHeader("Connected crew", "Account, character, and player-facing readiness for each visible participant.");
        if (participants.Count == 0)
        {
            DrawMutedNotice("No participants discovered. Open the connection guide to resolve the first route or security blocker.");
        }
        else if (ImGui.BeginTable("dad-clients-compact", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Account");
            ImGui.TableSetupColumn("Character");
            ImGui.TableSetupColumn("Readiness");
            ImGui.TableSetupColumn("Status");
            ImGui.TableHeadersRow();
            foreach (var participant in participants)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorAccountLabel(participant.ManagedAccountAlias, participant.ManagedAccountKey.Value));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorCharacterKey(participant.ActiveCharacterKey.Value, "(unknown)"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(participant.IsEligibleForRun ? "Eligible" : participant.PostArReady ? "Post-AR ready" : "Waiting");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatText(participant.StatusText, participant.State.ToString()));
            }
            ImGui.EndTable();
        }

        DrawDutySupportRuntimeSection(activeRun);
    }

    private void DrawSchedulesTab(DadVisibleRunState runState)
    {
        DadUi.Heading("SCHEDULES", "Build an ordered chain, choose its cadence, then dry-run or start it deliberately.");
        if (DadUi.Button("Schedule Wizard", DadUiTone.Accent))
            plugin.OpenSetupWizard(DadGuideFlow.Schedule);
        ImGui.SameLine();
        ImGui.TextDisabled("Create, order, set cadence, validate, and dry-run with guidance.");
        DrawScheduleBuilderTab(runState);
    }

    private void DrawStatusTab(
        DadCharacterPool characterPool,
        DadVisibleRunState runState,
        CharacterConfig profile,
        DadRunResult displayRun)
    {
        DadUi.Heading("STATUS", "Follow live work, inspect durable history, and resolve detailed readiness in one place.");
        if (!ImGui.BeginTabBar("dad-status-tabs"))
            return;

        if (ImGui.BeginTabItem("Current Activity", BuildStatusTabFlags(DadStatusWindowTab.CurrentActivity)))
        {
            DrawCurrentActivitySummary(runState, profile, displayRun);
            DrawActiveScheduleStatus(plugin.SchedulerService.GetScheduleSnapshot());
            DrawCrewActiveJobSection();
            if (plugin.Configuration.DebugUiEnabled)
                DrawStatusCurrentActivityDetails(runState, profile, displayRun);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Queue & History", BuildStatusTabFlags(DadStatusWindowTab.QueueHistory)))
        {
            DrawCrewQueueSection();
            DrawRunHistory();
            DrawScheduleRecentResults(plugin.SchedulerService.GetScheduleSnapshot(), string.Empty);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Readiness", BuildStatusTabFlags(DadStatusWindowTab.Readiness)))
        {
            DrawStatusReadiness(characterPool, runState, profile);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawCurrentActivitySummary(
        DadVisibleRunState runState,
        CharacterConfig profile,
        DadRunResult displayRun)
    {
        var activeRun = displayRun;
        DrawSectionHeader("Visible DAD run", "Current authority-aligned run state and the first operator action.");
        DrawStatusRow("Operator phase", DadOperatorPhaseText.FormatPhaseLabel(activeRun));
        DrawStatusRow("Run", activeRun.Status == DadRunStatus.Idle
            ? "Idle"
            : $"{activeRun.Status} / {activeRun.Phase} / {activeRun.ModuleId}");
        DrawStatusRow("Summary", BuildActiveRunKeyStatus(activeRun));
        if (!string.IsNullOrWhiteSpace(activeRun.ActiveTaskName))
            DrawStatusRow("Active task", $"{activeRun.ActiveTaskIndex}/{Math.Max(1, activeRun.TotalTaskCount)} {activeRun.ActiveTaskName} | {FormatText(activeRun.ActiveTaskStatus, activeRun.Summary)}");
        if (DadOperatorPhaseText.HasBlockingFailure(activeRun))
            DrawStatusRow("First blocker", FormatText(activeRun.BlockedReason, activeRun.FailureReason));
        DrawStatusRow("Next action", BuildOverviewNextAction(runState, profile, displayRun));
        DrawDutySupportRuntimeSection(activeRun);
    }

    private void DrawActiveScheduleStatus(DadScheduleSnapshot snapshot)
    {
        var activeRun = snapshot.ActiveRun;
        DrawSectionHeader("Schedule runner", "Live schedule progress and cancellation; terminal history is under Queue & History.");
        if (!activeRun.IsActive)
        {
            DrawMutedNotice(FormatText(activeRun.Summary, "No active schedule run."));
            return;
        }

        DrawStatusRow("Running now", DadScheduleCursorFormatter.Format(activeRun, snapshot.Schedules));
        DrawStatusRow("State", $"{activeRun.Status} / {activeRun.Phase}");
        DrawStatusRow("Progress", $"{activeRun.CompletedEntryExecutions}/{activeRun.TotalEntryExecutions} preset run(s)");
        if (!string.IsNullOrWhiteSpace(activeRun.BlockedReason))
            DrawStatusRow("Blocker", activeRun.BlockedReason);
        if (DadUi.Button("Cancel active schedule", DadUiTone.Danger))
            plugin.CancelScheduleRunFromShell("Schedule cancelled from Status / Current Activity.");
    }

    private void DrawStatusReadiness(
        DadCharacterPool characterPool,
        DadVisibleRunState runState,
        CharacterConfig profile)
    {
        var participants = BuildVisibleParticipants(characterPool);
        DrawMultiplayerCompact(characterPool, runState, participants, GetActiveRun(runState));

        var plannerOptions = plugin.PlannerOptions;
        var plannerSnapshot = plugin.GetPlannerUiSnapshot(runState);
        var requestPreview = plannerSnapshot.RequestPreview;
        var plannerPreview = requestPreview.PlannerPreview;
        var plannerLocked = IsPlannerLocked(runState);
        DrawPlannerLaneSummarySection(plannerPreview, requestPreview, runState, debugUi: true);
        DrawStatusRow(
            "Finish actions",
            BuildCompletionActionsSummary(requestPreview.ContractPreview.CompletionActions ?? plugin.Configuration.CompletionActions));
        DrawPlannerRosterSummarySection(plannerPreview, runState, debugUi: true);
        DrawPlannerValidationSection(plannerPreview, requestPreview);
        DrawSchedulerReadinessDetails(plannerSnapshot.SchedulerPreview);

        if (!plugin.Configuration.DebugUiEnabled)
            return;

        DrawStatusDebugTools(characterPool, runState, profile);
        DrawStatusReadinessDetails(characterPool, runState);
        DrawStatusPlannerAdvancedInputs(plannerSnapshot, plannerOptions, plannerPreview, plannerLocked);
        DrawPlannerDetailsSection(plannerSnapshot, plannerOptions, plannerPreview, requestPreview, runState, plannerLocked);
    }

    private void DrawSchedulerReadinessDetails(DadSchedulerPreview schedulerPreview)
    {
        var state = plugin.SchedulerService.CurrentState;
        DrawSectionHeader("Scheduler readiness", "Per-slot wake, relog, and preparation stages kept out of the task-focused Plan page.");
        DrawStatusRow("Preview", schedulerPreview.StatusSummary);
        DrawStatusRow("State", state.Summary);
        foreach (var slot in state.Slots)
            DrawStatusRow($"{slot.SlotId} stage", FormatSchedulerSlotStage(slot, state));
    }

    private void DrawStatusDebugTools(
        DadCharacterPool characterPool,
        DadVisibleRunState runState,
        CharacterConfig profile)
    {
        var configuration = plugin.Configuration;
        var localRun = runState.LocalRun;
        var authorityRun = runState.AuthorityRun;
        var canStartLocalDemo = CanStartLocalDemo(profile, localRun);
        var canStartRemoteDemo = canStartLocalDemo &&
                                 !configuration.LocalOnlyModeEnabled &&
                                 plugin.HasServerDadAuthority() &&
                                 !Plugin.IsBusy(authorityRun);
        DrawSectionHeader("Debug actions and raw diagnostics", "Shown only while /dad debug is enabled.");
        var dtrEnabled = configuration.DtrBarEnabled;
        if (ImGui.Checkbox("DTR Bar", ref dtrEnabled))
        {
            configuration.DtrBarEnabled = dtrEnabled;
            configuration.Save();
            plugin.UpdateDtrBar();
        }
        ImGui.SameLine();
        var allowIpcStarts = profile.AllowIpcStarts;
        if (ImGui.Checkbox("Allow DAD starts", ref allowIpcStarts))
            plugin.ConfigManager.UpdateActiveConfig(active => active.AllowIpcStarts = allowIpcStarts);
        ImGui.SameLine();
        var localOnlyMode = configuration.LocalOnlyModeEnabled;
        if (ImGui.Checkbox("Local-only mode", ref localOnlyMode))
        {
            configuration.LocalOnlyModeEnabled = localOnlyMode;
            configuration.Save();
        }
        ImGui.SameLine();
        if (DadUi.Button("Status to chat"))
            plugin.PrintStatusReport();

        DrawDemoButton("Run local demo", canStartLocalDemo, plugin.StartLocalDemoRunFromShell);
        ImGui.SameLine();
        DrawDemoButton("Run server demo", canStartRemoteDemo, plugin.StartServerDemoRunFromShell);
        ImGui.SameLine();
        DrawDemoButton("Run Daily Roulette demo", canStartRemoteDemo, plugin.StartDailyMsqDemoRunFromShell);
        ImGui.SameLine();
        DrawDemoButton("Run commend demo", canStartRemoteDemo, plugin.StartCommendationDemoRunFromShell);
        var rouletteDiagnostic = plugin.RouletteRewardProbeService.GetDiagnosticStatus();
        var dadOtherwiseIdle =
            !IsPlannerLocked(runState) &&
            !plugin.SchedulerService.CurrentState.IsActive;
        ImGui.BeginDisabled(!dadOtherwiseIdle || rouletteDiagnostic.IsPending);
        if (DadUi.Button("Log Duty Roulette reward states"))
        {
            if (!plugin.RouletteRewardProbeService.TryStartDiagnostic(dadOtherwiseIdle, out var diagnosticFailure))
                plugin.PrintStatus(diagnosticFailure);
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(rouletteDiagnostic.Summary);
        DrawAlliancePartyFinderDebug();
        var dutyIpc = plugin.DutyIpcService.GetStatus();
        var bridge = plugin.QuestionableBridge.GetStatus();
        DrawStatusRow("Name privacy", plugin.KrangleService.BuildStatus(characterPool));
        DrawStatusRow("Duty IPC / Questionable", FormatDutyIpcAndBridgeStatus(dutyIpc, bridge));
        DrawStatusRow("Duty IPC registration", $"{(dutyIpc.Registered ? "Registered" : FormatText(dutyIpc.RegistrationState, "Not registered"))} | mode {dutyIpc.LastMode}");
        DrawStatusRow("Duty IPC probe", dutyIpc.LastContentHasPathResult.HasValue
            ? $"territory {dutyIpc.LastContentHasPathTerritoryType} | result {dutyIpc.LastContentHasPathResult.Value} | candidates {dutyIpc.LastContentHasPathCandidateCount} / compatible {dutyIpc.LastContentHasPathCompatibleCandidateCount} | blocker {FormatText(dutyIpc.LastContentHasPathBlocker, "(none)")}"
            : "No ContentHasPath probe observed yet.");
        DrawStatusRow("Duty IPC run", $"run {FormatText(dutyIpc.LastRunId, "(none)")} | territory {dutyIpc.LastTerritoryType} | bare mode {dutyIpc.LastBareMode} | failure {FormatText(dutyIpc.LastFailure, "(none)")}");
        DrawStatusRow("Duty IPC cleanup", $"{dutyIpc.LastCleanupResult} | {FormatTime(dutyIpc.LastCleanupUtc)} | failed commands {(dutyIpc.LastCleanupFailedCommands.Count == 0 ? "(none)" : string.Join(", ", dutyIpc.LastCleanupFailedCommands))}");
        DrawStatusRow("Questionable bridge", $"{(bridge.QuestionableLoaded ? "loaded" : "not loaded")} | {bridge.PatchState} | {(bridge.QuestionableRunning ? "running" : "idle")} | blocker {FormatText(bridge.LastBlocker, "(none)")}");
        DrawStatusRow("Questionable cosmetic", $"{bridge.CosmeticPatchState} | blocker {FormatText(bridge.CosmeticLastBlocker, "(none)")}");
        DrawStatusRow("Character pool", characterPool.LastSummary);
        DrawStatusRow("XADB", characterPool.XadbStatus.LastStatus);
        DrawStatusRow("Peer transport", characterPool.PeerTransport.LastRequestStatus);
        DrawStatusRow("Transport protocol", characterPool.PeerTransport.ProtocolVersion.ToString(CultureInfo.InvariantCulture));
    }

    private void DrawAlliancePartyFinderDebug()
    {
        var selectedGroup = plugin.GetSelectedPlannerGroup();
        var plannerPreview = plugin.BuildPlannerPreview();
        var live = plugin.AlliancePartyFinderService.GetStatus();
        var preflight = plugin.AlliancePartyFinderService.Preview(selectedGroup, plannerPreview);
        var display = DadAlliancePartyFinderCreatePreflight.SelectLocalDisplay(live, preflight);

        DrawSectionHeader(
            "Alliance Party Finder",
            "Debug-only preset formation through one private Labyrinth listing. Recruitment closes without queueing or disbanding.");
        DrawStatusRow("Preset", selectedGroup?.DisplayName ?? "(select a concrete preset)");
        DrawStatusRow(
            "Assignments",
            $"A {preflight.Validation.AllianceACount}/8 | B {preflight.Validation.AllianceBCount}/8 | " +
            $"C {preflight.Validation.AllianceCCount}/8 | D {preflight.Validation.AllianceDCount}/8 | " +
            $"E {preflight.Validation.AllianceECount}/8 | F {preflight.Validation.AllianceFCount}/8 | " +
            $"G {preflight.Validation.AllianceGCount}/8 | total {preflight.Validation.TotalCount}");
        DrawStatusRow("Validation", preflight.Validation.Summary);
        DrawStatusRow(
            "Create readiness",
            preflight.CreatePreflightReady
                ? "Ready on this Dad Coordinator."
                : preflight.CreatePreflightBlocker);
        if (display.CreateRejected)
            DrawStatusRow("Last Create attempt", $"Rejected: {display.Summary}");
        DrawStatusRow("Host / state", string.IsNullOrWhiteSpace(display.LeaderName)
            ? display.State.ToString()
            : $"{display.LeaderName} @ {display.LeaderWorld} | {display.State}");
        DrawStatusRow(
            "PF owner handle (diagnostic)",
            display.ListingId == 0
                ? "(none)"
                : display.ListingId.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Private passcode", display.Passcode is >= 1000 and <= 9999
            ? display.Passcode.ToString("0000", CultureInfo.InvariantCulture)
            : "(generated on Create party)");
        if (!string.IsNullOrWhiteSpace(display.CreateStage))
        {
            DrawStatusRow("Create stage", display.CreateStage);
            DrawStatusRow("Create attempt", display.CreateAttempt.ToString(CultureInfo.InvariantCulture));
            DrawStatusRow("Create retry / observation deadline", display.CreateNextRetryUtc.HasValue
                ? display.CreateNextRetryUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
                : "(none)");
            DrawStatusRow("Create elapsed", $"{display.CreateElapsedMilliseconds:N0} ms");
            DrawStatusRow(
                "Create publication",
                $"active status {display.CreateActiveRecruitment} | " +
                $"editor visible {display.CreateEditorVisible} | " +
                $"Submit dispatched {display.CreateSubmitDispatched}");
            DrawStatusRow(
                "Configuration target",
                string.IsNullOrWhiteSpace(display.CreateConfigurationTarget)
                    ? "(none)"
                    : display.CreateConfigurationTarget);
            DrawStatusRow(
                "Observed PF settings",
                string.IsNullOrWhiteSpace(display.CreateObservedSettings)
                    ? "(not sampled)"
                    : display.CreateObservedSettings);
            DrawStatusRow("Last create error", string.IsNullOrWhiteSpace(display.CreateLastError)
                ? "(none)"
                : display.CreateLastError);
        }
        DrawStatusRow("Recruitment", display.Summary);

        var canCreate = preflight.CreatePreflightReady;
        ImGui.BeginDisabled(!canCreate);
        if (DadUi.Button("Create party", DadUiTone.Accent))
        {
            var result = plugin.AlliancePartyFinderService.CreateParty(selectedGroup, plannerPreview);
            plugin.PrintStatus(result.Summary);
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        var canStop = DadAlliancePartyFinderRules.CanStop(live);
        ImGui.BeginDisabled(!canStop);
        if (DadUi.Button("Stop PF", DadUiTone.Danger))
        {
            plugin.AlliancePartyFinderService.Stop(
                "Stopped from the Alliance Party Finder debug controls.");
            plugin.PrintStatus(
                plugin.AlliancePartyFinderService.GetStatus().Summary);
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        var canGrab = DadAlliancePartyFinderRules.CanGrabDads(live);
        ImGui.BeginDisabled(!canGrab);
        if (DadUi.Button("Grab dads", DadUiTone.Accent))
        {
            var result = plugin.AlliancePartyFinderService.GrabDads();
            plugin.PrintStatus(result.Summary);
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (DadUi.Button("Check PFs", DadUiTone.Neutral))
            _ = PrintPartyFinderDiagnosticsAsync();

        foreach (var result in display.Results
                     .OrderBy(static result => result.ExpectedAlliance)
                     .ThenBy(static result => result.TargetCharacterName, StringComparer.OrdinalIgnoreCase))
        {
            DrawStatusRow(
                $"{result.ExpectedAlliance} {FormatText(result.TargetCharacterName, result.TargetCharacterKey.Value)}",
                $"attempt {result.Attempt} | {result.ResultKind}/{result.State} | observed {result.ObservedAlliance} | {result.Summary}");
        }
    }

    private async Task PrintPartyFinderDiagnosticsAsync()
    {
        try
        {
            var message = await plugin.AlliancePartyFinderService
                .CheckPartyFinderDiagnosticsAsync()
                .ConfigureAwait(false);
            await Plugin.Framework
                .RunOnFrameworkThread(() => plugin.PrintStatus(message))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try
            {
                await Plugin.Framework
                    .RunOnFrameworkThread(
                        () => plugin.PrintStatus(
                            $"Party Finder diagnostics failed: {exception.Message}"))
                    .ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private void DrawStatusPlannerAdvancedInputs(
        DadPlannerUiSnapshot plannerSnapshot,
        DadPresetPlannerOptions plannerOptions,
        DadActivityPreset plannerPreview,
        bool plannerLocked)
    {
        if (!ImGui.TreeNode("Advanced planner authority and roster filters"))
            return;

        ImGui.BeginDisabled(plannerLocked);
        DrawPlannerOperatorModeSelector(plannerOptions);
        ImGui.SameLine();
        DrawPlannerAccountFilterSelector(plannerSnapshot.AccountOptions, plannerOptions, plannerPreview.AccountFilterSummary);
        DrawPlannerTransportOwnerSelector(plannerOptions);
        ImGui.SameLine();
        DrawPlannerQueueAuthoritySelector(plannerOptions);

        var selectedGroup = plugin.GetSelectedPlannerGroup();
        if (selectedGroup != null)
        {
            if (ImGui.SmallButton("Refresh group slots from current planner"))
            {
                plugin.ReplaceSelectedPlannerGroupSlotsFromCurrentPreview();
                plugin.PrintStatus($"Updated preset '{selectedGroup.DisplayName}' slots from current preview.");
            }
            DrawPlannerGroupScheduleControls(selectedGroup);
        }

        var connectedOnly = plannerOptions.ConnectedOnly;
        if (ImGui.Checkbox("Connected only", ref connectedOnly))
        {
            plannerOptions.ConnectedOnly = connectedOnly;
            plugin.SavePlannerOptions();
        }
        ImGui.SameLine();
        var sameDatacenterOnly = plannerOptions.SameDatacenterOnly;
        if (ImGui.Checkbox("Same datacenter", ref sameDatacenterOnly))
        {
            plannerOptions.SameDatacenterOnly = sameDatacenterOnly;
            plugin.SavePlannerOptions();
        }
        ImGui.SameLine();
        var allowStale = plannerOptions.AllowStaleForPlanning;
        if (ImGui.Checkbox("Allow stale for planning", ref allowStale))
        {
            plannerOptions.AllowStaleForPlanning = allowStale;
            plugin.SavePlannerOptions();
        }
        ImGui.EndDisabled();
        ImGui.TreePop();
    }

    private void DrawCrewTab(DadCharacterPool characterPool, DadVisibleRunState runState)
    {
        var rosterSnapshot = plugin.RosterCatalogService.GetUiSnapshot();
        var catalog = rosterSnapshot.Catalog;
        var activeRows = catalog.Characters.Where(static row => row.Visibility == DadRosterVisibility.Active).ToList();
        var blockerCount = activeRows.Count(static row => row.AccountKey.IsEmpty || row.IsStale || row.NeedsRosterUpdate);
        DadUi.Heading("CREW", "Manage the accounts and characters DAD can use.");
        if (DadUi.Button("Guide: Build the Crew", DadUiTone.Accent))
            plugin.OpenSetupWizard(DadGuideFlow.Crew);
        ImGui.SameLine();
        DadUi.Badge(blockerCount == 0 && activeRows.Count > 0 ? "Roster ready" : $"{blockerCount} roster blocker(s)",
            blockerCount == 0 && activeRows.Count > 0 ? DadUiTone.Success : DadUiTone.Warning);
        if (plugin.Configuration.DebugUiEnabled)
        {
            var launchProfiles = plugin.GetPlannerUiSnapshot(runState).LaunchProfiles;
            ImGui.SameLine();
            DadUi.Badge($"{launchProfiles.Count(static profile => profile.Enabled)} launch profile(s) enabled",
                launchProfiles.Any(static profile => profile.Enabled) ? DadUiTone.Info : DadUiTone.Neutral);
        }

        if (!ImGui.BeginTabBar("dad-crew-tabs"))
            return;

        if (ImGui.BeginTabItem("Roster"))
        {
            DrawCrewRosterSection(characterPool, rosterSnapshot);
            ImGui.EndTabItem();
        }

        if (plugin.Configuration.DebugUiEnabled && ImGui.BeginTabItem("Character Profiles"))
        {
            var launchProfiles = plugin.GetPlannerUiSnapshot(runState).LaunchProfiles;
            DrawProfileTree(launchProfiles);
            ImGui.EndTabItem();
        }

        if (plugin.Configuration.DebugUiEnabled && ImGui.BeginTabItem("Launch Profiles"))
        {
            var launchProfiles = plugin.GetPlannerUiSnapshot(runState).LaunchProfiles;
            DrawLaunchProfileEditor(launchProfiles);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private ImGuiTabItemFlags BuildMainTabFlags(DadMainWindowTab tab)
        => pendingMainTab == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

    private ImGuiTabItemFlags BuildPlanningMainTabFlags(bool showPlanner)
    {
        if (pendingMainTab != DadMainWindowTab.Presets)
            return ImGuiTabItemFlags.None;

        var requested = pendingPresetsTab ?? DadPresetsWindowTab.Planner;
        var plannerRequested = requested == DadPresetsWindowTab.Planner;
        return plannerRequested == showPlanner ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
    }

    private ImGuiTabItemFlags BuildPresetsTabFlags(DadPresetsWindowTab tab)
        => pendingPresetsTab == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

    private ImGuiTabItemFlags BuildStatusTabFlags(DadStatusWindowTab tab)
        => pendingStatusTab == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

    private void DrawAccountsProfilesSection(
        DadCharacterPool characterPool,
        DadAccountRosterCatalog catalog,
        DadVisibleRunState runState)
    {
        var launchProfiles = plugin.GetPlannerUiSnapshot(runState).LaunchProfiles;
        if (plugin.Configuration.DebugUiEnabled && ImGui.CollapsingHeader("Launch profiles", ImGuiTreeNodeFlags.DefaultOpen))
            DrawLaunchProfileEditor(launchProfiles);

        if (plugin.Configuration.DebugUiEnabled &&
            ImGui.CollapsingHeader("Account profile tree", ImGuiTreeNodeFlags.DefaultOpen))
            DrawProfileTree(launchProfiles);

        if (ImGui.CollapsingHeader("Roster state", ImGuiTreeNodeFlags.DefaultOpen))
            DrawCrewRosterSection(characterPool, plugin.RosterCatalogService.GetUiSnapshot());
    }

    private void DrawLaunchProfileEditor(IReadOnlyList<DadLaunchProfile> profiles)
    {
        if (ImGui.SmallButton("Import launch batches"))
            plugin.ImportLaunchProfilesFromBootDirectory();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Imports FFXIV client boot batch files from the configured boot directory.");
        ImGui.SameLine();
        ImGui.TextDisabled("Batch files remain read-only; imported profiles default disabled, auto-start off, dry-run on.");

        if (profiles.Count == 0)
        {
            ImGui.TextWrapped("No launch-profile metadata is imported. This debug scaffolding stores batch/account metadata, but DAD does not execute batch paths or start a missing game process.");
            if (DadUi.Button("Guide: import and map launch profiles"))
                plugin.OpenSetupWizard(DadGuideFlow.Crew);
            return;
        }

        if (!ImGui.BeginTable("dad-unified-launch-profiles", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("On");
        ImGui.TableSetupColumn("Auto");
        ImGui.TableSetupColumn("Dry");
        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Account");
        ImGui.TableSetupColumn("Timeout");
        ImGui.TableSetupColumn("Batch / expected");
        ImGui.TableHeadersRow();
        foreach (var profile in profiles)
        {
            ImGui.TableNextRow();
            var changed = false;
            ImGui.TableNextColumn();
            var enabled = profile.Enabled;
            if (ImGui.Checkbox($"##launch-on-{profile.ProfileId}", ref enabled))
            {
                profile.Enabled = enabled;
                changed = true;
            }
            ImGui.TableNextColumn();
            var autoStart = profile.AllowAutoStart;
            if (ImGui.Checkbox($"##launch-auto-{profile.ProfileId}", ref autoStart))
            {
                profile.AllowAutoStart = autoStart;
                changed = true;
            }
            ImGui.TableNextColumn();
            var dryRun = profile.DryRun;
            if (ImGui.Checkbox($"##launch-dry-{profile.ProfileId}", ref dryRun))
            {
                profile.DryRun = dryRun;
                changed = true;
            }
            ImGui.TableNextColumn();
            var name = profile.DisplayName;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText($"##launch-name-{profile.ProfileId}", ref name, 128))
            {
                var committedSignature = BuildLaunchProfileEditableSignature(profile);
                profile.DisplayName = name;
                plugin.QueueDebouncedLaunchProfileUpdate(
                    profile,
                    committedSignature,
                    BuildLaunchProfileEditableSignature,
                    status => profileSaveStatus = status);
            }
            ImGui.TableNextColumn();
            var accountKey = profile.AccountKey.Value;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText($"##launch-account-{profile.ProfileId}", ref accountKey, 128))
            {
                var committedSignature = BuildLaunchProfileEditableSignature(profile);
                profile.AccountKey = new DadAccountKey(accountKey);
                plugin.QueueDebouncedLaunchProfileUpdate(
                    profile,
                    committedSignature,
                    BuildLaunchProfileEditableSignature,
                    status => profileSaveStatus = status);
            }
            ImGui.TableNextColumn();
            var timeout = profile.TimeoutSeconds;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputInt($"##launch-timeout-{profile.ProfileId}", ref timeout))
            {
                var committedSignature = BuildLaunchProfileEditableSignature(profile);
                profile.TimeoutSeconds = Math.Clamp(timeout, 30, 1800);
                plugin.QueueDebouncedLaunchProfileUpdate(
                    profile,
                    committedSignature,
                    BuildLaunchProfileEditableSignature,
                    status => profileSaveStatus = status);
            }
            ImGui.TableNextColumn();
            ImGui.TextWrapped($"{profile.BatchPath}\nExpected: {string.Join(", ", profile.ExpectedCharacterKeys.Select(static key => key.Value))}");

            if (changed)
            {
                var ack = plugin.SchedulerService.UpdateLaunchProfile(new DadLaunchProfileUpdateRequest
                {
                    ExpectedRevision = profile.Revision,
                    Profile = profile,
                });
                profileSaveStatus = ack.Summary;
            }
        }
        ImGui.EndTable();
    }

    private void DrawProfileTree(IReadOnlyList<DadLaunchProfile> launchProfiles)
    {
        var catalogs = plugin.ProfileDirectoryService.GetCatalogs();
        if (catalogs.Count == 0)
        {
            ImGui.TextWrapped("No owned character profiles are available yet. Refresh the roster and confirm account ownership so DAD can build the account/profile tree.");
            if (DadUi.Button("Guide: resolve account ownership"))
                plugin.OpenSetupWizard(DadGuideFlow.Crew);
            return;
        }

        foreach (var catalog in catalogs)
        {
            var ownerKey = catalog.OwnerWorkerSessionId.IsEmpty
                ? catalog.OwnerClientInstanceId
                : catalog.OwnerWorkerSessionId.Value;
            var ownerLabel = catalog.OwnerOnline
                ? $"{catalog.OwnerClientInstanceId} (online)"
                : $"{catalog.OwnerClientInstanceId} (offline cache)";
            if (!ImGui.TreeNode($"{ownerLabel}##profile-owner-{ownerKey}"))
                continue;

            foreach (var account in catalog.Accounts)
            {
                if (!ImGui.TreeNode($"{account.AccountAlias} [{account.AccountKey}]##profile-account-{ownerKey}-{account.AccountKey}"))
                    continue;

                DrawProfileSelectable(catalog, account, null, "(Account default)", account.DefaultProfile, account.DefaultProfile.Revision);
                foreach (var character in account.Characters)
                {
                    DrawProfileSelectable(
                        catalog,
                        account,
                        character,
                        character.CharacterKey.Value,
                        character.Profile,
                        character.Revision);
                }

                if (plugin.Configuration.DebugUiEnabled)
                    DrawPrimaryLaunchProfileEditor(catalog, account, launchProfiles);
                ImGui.TreePop();
            }

            ImGui.TreePop();
        }

        if (string.IsNullOrWhiteSpace(selectedProfileAccount))
            return;

        ImGui.Separator();
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(selectedProfileCharacter)
            ? $"Editing account default: {selectedProfileAccount}"
            : $"Editing character: {selectedProfileCharacter}");
        var enabled = profileDraft.Enabled;
        if (ImGui.Checkbox("Profile enabled##unified", ref enabled))
            profileDraft.Enabled = enabled;
        var allowStarts = profileDraft.AllowIpcStarts;
        if (ImGui.Checkbox("Allow Dad starts##unified", ref allowStarts))
            profileDraft.AllowIpcStarts = allowStarts;
        var emote = profileDraft.BlundervilleEmoteCommand;
        if (ImGui.InputText("Blunderville emote##unified", ref emote, 128))
            profileDraft.BlundervilleEmoteCommand = emote;
        var notes = profileDraft.TargetNotes;
        if (ImGui.InputTextMultiline("Operator notes##unified", ref notes, 1024, new Vector2(-1f, 100f)))
            profileDraft.TargetNotes = notes;

        var selectedCatalog = catalogs.FirstOrDefault(catalog =>
            string.Equals(
                catalog.OwnerWorkerSessionId.IsEmpty ? catalog.OwnerClientInstanceId : catalog.OwnerWorkerSessionId.Value,
                selectedProfileOwner,
                StringComparison.OrdinalIgnoreCase));
        var readOnly = selectedCatalog == null || selectedCatalog.ReadOnly || !selectedCatalog.OwnerOnline;
        if (readOnly)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save profile"))
        {
            var ack = plugin.ProfileDirectoryService.UpdateProfile(new DadProfileUpdateRequest
            {
                AccountKey = new DadAccountKey(selectedProfileAccount),
                CharacterKey = new DadCharacterKey(selectedProfileCharacter),
                UpdateAccountDefault = string.IsNullOrWhiteSpace(selectedProfileCharacter),
                ExpectedAccountRevision = selectedProfileAccountRevision,
                ExpectedProfileRevision = selectedProfileRevision,
                Profile = profileDraft.Clone(),
            });
            profileSaveStatus = ack.Summary;
            if (ack.Accepted)
            {
                selectedProfileAccountRevision = ack.AccountRevision;
                selectedProfileRevision = ack.ProfileRevision;
                profileDraft.Revision = ack.ProfileRevision;
            }
        }
        if (readOnly)
            ImGui.EndDisabled();
        if (readOnly)
            ImGui.TextDisabled("Offline remote profiles are read-only.");
        if (!string.IsNullOrWhiteSpace(profileSaveStatus))
            ImGui.TextWrapped(profileSaveStatus);
    }

    private void DrawProfileSelectable(
        DadProfileCatalog catalog,
        DadAccountProfileRecord account,
        DadCharacterProfileRecord? character,
        string label,
        CharacterConfig profile,
        long profileRevision)
    {
        var ownerKey = catalog.OwnerWorkerSessionId.IsEmpty
            ? catalog.OwnerClientInstanceId
            : catalog.OwnerWorkerSessionId.Value;
        var characterKey = character?.CharacterKey.Value ?? string.Empty;
        var selected = string.Equals(selectedProfileOwner, ownerKey, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(selectedProfileAccount, account.AccountKey.Value, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(selectedProfileCharacter, characterKey, StringComparison.OrdinalIgnoreCase);
        if (!ImGui.Selectable($"{label}##profile-{ownerKey}-{account.AccountKey}-{characterKey}", selected))
            return;

        selectedProfileOwner = ownerKey;
        selectedProfileAccount = account.AccountKey.Value;
        selectedProfileCharacter = characterKey;
        selectedProfileAccountRevision = account.Revision;
        selectedProfileRevision = profileRevision;
        profileDraft = profile.Clone();
        profileSaveStatus = string.Empty;
    }

    private void DrawPrimaryLaunchProfileEditor(
        DadProfileCatalog catalog,
        DadAccountProfileRecord account,
        IReadOnlyList<DadLaunchProfile> launchProfiles)
    {
        var localOwner = string.Equals(
            catalog.OwnerWorkerSessionId.Value,
            plugin.PresenceService.WorkerSessionId.Value,
            StringComparison.OrdinalIgnoreCase);
        var profiles = launchProfiles
            .Where(profile => profile.AccountKey.IsEmpty || DadRosterIdentity.SameAccount(profile.AccountKey, account.AccountKey))
            .ToList();
        var current = profiles.FirstOrDefault(profile =>
            string.Equals(profile.ProfileId, account.PrimaryLaunchProfileId, StringComparison.OrdinalIgnoreCase));
        if (ImGui.BeginCombo($"Primary launch profile##{account.AccountKey}", current?.DisplayName ?? "(none)"))
        {
            if (ImGui.Selectable("(none)", string.IsNullOrWhiteSpace(account.PrimaryLaunchProfileId)))
                UpdatePrimaryLaunchProfile(catalog, account, string.Empty, localOwner);
            foreach (var profile in profiles)
            {
                if (ImGui.Selectable(profile.DisplayName, string.Equals(profile.ProfileId, account.PrimaryLaunchProfileId, StringComparison.OrdinalIgnoreCase)))
                    UpdatePrimaryLaunchProfile(catalog, account, profile.ProfileId, localOwner);
            }
            ImGui.EndCombo();
        }
    }

    private void UpdatePrimaryLaunchProfile(
        DadProfileCatalog catalog,
        DadAccountProfileRecord account,
        string profileId,
        bool localOwner)
    {
        if (!localOwner && (!catalog.OwnerOnline || catalog.ReadOnly))
        {
            profileSaveStatus = "Owning Client Dad is offline; launch mapping is read-only.";
            return;
        }

        var ack = plugin.ProfileDirectoryService.UpdateProfile(new DadProfileUpdateRequest
        {
            AccountKey = account.AccountKey,
            ExpectedAccountRevision = account.Revision,
            ExpectedProfileRevision = account.DefaultProfile.Revision,
            UpdatePrimaryLaunchProfile = true,
            PrimaryLaunchProfileId = profileId,
        });
        profileSaveStatus = ack.Summary;
    }

    private unsafe void DrawCrewRosterSection(DadCharacterPool characterPool, DadRosterUiSnapshot rosterSnapshot)
    {
        var catalog = rosterSnapshot.Catalog;
        // B1: complete-then-render. When the transport's roster-catalog cache revision advances (an async peer
        // pull landed, or the coordinator pushed a fresh projection (B2)), re-merge from cache once so the new
        // rows render themselves. Reads cache only (no network pull), so it cannot loop on the revision bump.
        var rosterCacheRevision = plugin.TransportService.RosterCatalogCacheRevision;
        if (lastRosterCatalogCacheRevision < 0)
        {
            lastRosterCatalogCacheRevision = rosterCacheRevision;
        }
        else if (rosterCacheRevision != lastRosterCatalogCacheRevision)
        {
            lastRosterCatalogCacheRevision = rosterCacheRevision;
            plugin.RosterCatalogService.RefreshCatalogFromCache(characterPool, "roster cache revision advanced");
            rosterSnapshot = plugin.RosterCatalogService.GetUiSnapshot();
            catalog = rosterSnapshot.Catalog;
        }

        DrawSectionHeader("Roster Accounts", "Pick an account first. Assigned Active rows feed normal crew slots.");
        if (ImGui.SmallButton("Refresh local roster"))
        {
            plugin.RosterCatalogService.RefreshCatalog(characterPool, new DadRosterRefreshPlan
            {
                IncludeHidden = true,
                IncludeIgnored = true,
                LogDiagnostics = true,
                DiagnosticsReason = "manual local roster refresh",
            });
            rosterSnapshot = plugin.RosterCatalogService.GetUiSnapshot();
            catalog = rosterSnapshot.Catalog;
            ResetRosterBrowseFilters(catalog, RosterBrowseResetMode.AllRows);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Build Connected Crew"))
        {
            plugin.RosterCatalogService.RefreshCatalog(
                characterPool,
                DadRosterRefreshPlan.ConnectedDads("manual connected roster refresh"));
            rosterSnapshot = plugin.RosterCatalogService.GetUiSnapshot();
            catalog = rosterSnapshot.Catalog;
            ResetRosterBrowseFilters(catalog, RosterBrowseResetMode.AllRows);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Builds the Crew view from current connected participants, suppressing this client's mirrored worker row.");

        EnsureRosterAccountSelection(catalog);
        ImGui.SetNextItemWidth(MathF.Min(260f, ImGui.GetContentRegionAvail().X));
        ImGui.InputText("Search", ref rosterSearch, 128);
        ImGui.SameLine();
        DrawRosterAccountSelector(catalog);

        var accountScoped = GetAccountScopedRosterCharacters(catalog.Characters).ToList();
        DrawStatusRow("Selected account", BuildSelectedRosterAccountSummary(catalog, accountScoped));
        DrawRosterVisibilityTabs(accountScoped);

        var showAdvanced = ImGui.CollapsingHeader("Advanced filters / Bulk tools");
        if (showAdvanced)
            DrawRosterAdvancedFilters(catalog);

        var filtered = GetCachedFilteredRosterRows(rosterSnapshot);
        TrimRosterSelection(catalog.Characters);
        var selectedFiltered = filtered
            .Where(character => rosterSelectedRows.Contains(BuildRosterSelectionKey(character)))
            .ToList();
        var activeFilters = BuildRosterActiveFilterSummary(catalog);
        DrawStatusRow("Catalog", $"{catalog.Summary} Showing {filtered.Count}/{catalog.Characters.Count} row(s).");
        if (!string.IsNullOrWhiteSpace(activeFilters) && filtered.Count < catalog.Characters.Count)
            DrawStatusRow("Filtered rows", $"{catalog.Characters.Count - filtered.Count} row(s) hidden by filters: {activeFilters}");
        if (plugin.Configuration.DebugUiEnabled)
        {
            DrawStatusRow("Roster preflight", BuildRosterPreflightStatus(catalog));
            var diagnostics = catalog.SourceDiagnostics;
            DrawStatusRow("XADB rows", $"snapshots {diagnostics.XadbSnapshotRows}, legacy {diagnostics.XadbLegacyRows}, merged {diagnostics.XadbMergedRows}");
            if (diagnostics.XadbMergedRows > diagnostics.LocalXadbAttributedRows)
                DrawStatusRow("XADB attribution", $"Merged rows {diagnostics.XadbMergedRows} exceed local attributed rows {diagnostics.LocalXadbAttributedRows}.");
            DrawStatusRow("XADB DCs", FormatRosterCountBreakdown(diagnostics.XadbDataCenterCounts));
            DrawStatusRow("XADB worlds", FormatRosterCountBreakdown(diagnostics.XadbWorldCounts, 12));
            DrawStatusRow("Local roster", $"XADB attributed {diagnostics.LocalXadbAttributedRows}, known {diagnostics.KnownRosterRows}, runtime {diagnostics.LocalRuntimeRows}, final {diagnostics.FinalLocalRows}");
            if (diagnostics.FinalLocalRows > catalog.Characters.Count)
                DrawStatusRow("Visibility filter", $"{diagnostics.FinalLocalRows - catalog.Characters.Count} local row(s) hidden by catalog visibility filters.");
            DrawStatusRow("Peer catalogs", $"{catalog.SourceDiagnostics.PeerCatalogCount} response(s), {catalog.SourceDiagnostics.PeerFullRosterCount} full roster, {catalog.SourceDiagnostics.PeerFullRosterRows} full-roster row(s)");
            DrawStatusRow("Passive sync", $"cache rev {plugin.TransportService.RosterCatalogCacheRevision}, dropped completions {plugin.TransportService.CurrentTransport.RosterCatalogDroppedCount}");
            DrawStatusRow("Roster counts", BuildRosterStatusCounts(catalog));
        }

        if (!catalog.IsFullRosterAvailable)
            DrawStatusRow("XADB roster", DadXadbClient.RosterIpcMissingWarning);
        if (catalog.Warnings.Count > 0)
            DrawStatusRow("Warnings", string.Join(" | ", catalog.Warnings.Distinct(StringComparer.OrdinalIgnoreCase)));
        DrawStatusRow("Selection", $"{selectedFiltered.Count} selected in current filter.");

        if (showAdvanced)
            DrawRosterBulkTools(filtered, selectedFiltered);

        if (filtered.Count == 0)
        {
            DrawEmptyRosterFilterNotice(catalog);
            return;
        }

        var showAccountColumn = string.IsNullOrWhiteSpace(rosterAccountFilter) ||
                                string.Equals(rosterAccountFilter, RosterUnassignedAccountFilter, StringComparison.OrdinalIgnoreCase);
        var showProvenance = plugin.Configuration.DebugUiEnabled;
        var columnCount = (showAccountColumn ? 8 : 7) + (showProvenance ? 3 : 0);
        var tableFlags = ImGuiTableFlags.Borders |
                         ImGuiTableFlags.RowBg |
                         ImGuiTableFlags.Resizable |
                         ImGuiTableFlags.SizingStretchProp |
                         ImGuiTableFlags.ScrollY;
        if (showProvenance)
            tableFlags |= ImGuiTableFlags.ScrollX;
        if (!ImGui.BeginTable("dad-crew-roster", columnCount, tableFlags, new Vector2(0f, 430f)))
            return;

        ImGui.TableSetupColumn("Sel");
        if (showAccountColumn)
            ImGui.TableSetupColumn("Account");
        ImGui.TableSetupColumn("Character");
        if (showProvenance)
            ImGui.TableSetupColumn("ContentId");
        ImGui.TableSetupColumn("World/DC");
        if (showProvenance)
            ImGui.TableSetupColumn("Snapshot age");
        ImGui.TableSetupColumn("Job/Lvl");
        ImGui.TableSetupColumn("State");
        if (showProvenance)
            ImGui.TableSetupColumn("Source");
        ImGui.TableSetupColumn("Blockers");
        ImGui.TableSetupColumn("Actions");
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(filtered.Count);
        while (clipper.Step())
        {
            for (var rowIndex = clipper.DisplayStart; rowIndex < clipper.DisplayEnd; rowIndex++)
            {
                var character = filtered[rowIndex];
                var selectionKey = BuildRosterSelectionKey(character);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var selected = rosterSelectedRows.Contains(selectionKey);
                if (ImGui.Checkbox($"##dad-roster-select-{selectionKey}", ref selected))
                {
                    if (selected)
                        rosterSelectedRows.Add(selectionKey);
                    else
                        rosterSelectedRows.Remove(selectionKey);
                }
                if (showAccountColumn)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatRosterAccount(character));
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorCharacterKey(character.CharacterKey.Value, "(unknown)"));
                if (showProvenance)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(character.ContentId == 0 ? "-" : character.ContentId.ToString(CultureInfo.InvariantCulture));
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatRosterWorldDc(character));
                if (showProvenance)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{FormatRosterFreshness(character)} | {FormatTime(character.LastSnapshotUtc)}");
                }
                ImGui.TableNextColumn();
                DrawJobLevelCell(BuildJobLevelDisplay(
                    character.JobLevels,
                    character.CurrentJobId,
                    character.CurrentJobAbbrev,
                    character.CurrentLevel));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatRosterState(character));
                if (showProvenance)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatRosterSource(character));
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorText(FormatRosterBlockers(character), "(none)"));
                ImGui.TableNextColumn();
                DrawRosterRowActions(character, selectionKey);
            }
        }
        clipper.End();
        clipper.Destroy();

        ImGui.EndTable();
    }

    private void DrawScheduleBuilderTab(DadVisibleRunState runState)
    {
        var snapshot = plugin.SchedulerService.GetScheduleSnapshot();
        var groups = plugin.Configuration.PlannerGroups
            .OrderBy(static group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static group => group.GroupId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        EnsureScheduleSelection(snapshot);
        var schedule = snapshot.Schedules.FirstOrDefault(candidate =>
            string.Equals(candidate.ScheduleId, selectedScheduleId, StringComparison.OrdinalIgnoreCase));
        var activeRun = snapshot.ActiveRun;
        var activeScheduleLocked = activeRun.IsActive;
        var identityWidth = ImGui.GetContentRegionAvail().X;
        var identityFieldsShareRow = identityWidth >= ImGui.GetFontSize() * 36f;
        var identityActionsShareRow = identityWidth >= ImGui.GetFontSize() * 34f;

        DrawSectionHeader("Schedule", "Select or create the saved schedule, then edit its ordered work below.");
        ImGui.SetNextItemWidth(MathF.Min(280f, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("Schedule", schedule == null ? "(none)" : schedule.DisplayName))
        {
            foreach (var candidate in snapshot.Schedules)
            {
                var selected = string.Equals(candidate.ScheduleId, selectedScheduleId, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(candidate.DisplayName, selected))
                {
                    selectedScheduleId = candidate.ScheduleId;
                    schedulerScheduleNameBuffer = candidate.DisplayName;
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (identityFieldsShareRow)
            ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Min(260f, MathF.Max(120f, ImGui.GetContentRegionAvail().X)));
        ImGui.InputText("Name", ref schedulerScheduleNameBuffer, 128);

        if (ImGui.SmallButton("Create"))
        {
            var created = plugin.SchedulerService.CreateSchedule(schedulerScheduleNameBuffer);
            selectedScheduleId = created.ScheduleId;
            schedulerScheduleNameBuffer = created.DisplayName;
            plugin.PrintStatus($"Created schedule '{created.DisplayName}'.");
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(schedule == null);
        if (ImGui.SmallButton("Rename") && schedule != null)
        {
            schedule.DisplayName = schedulerScheduleNameBuffer;
            var updated = plugin.SchedulerService.UpdateSchedule(schedule);
            if (updated != null)
            {
                schedulerScheduleNameBuffer = updated.DisplayName;
                plugin.PrintStatus($"Renamed schedule to '{updated.DisplayName}'.");
            }
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Duplicate") && schedule != null)
        {
            var duplicate = plugin.SchedulerService.DuplicateSchedule(schedule.ScheduleId, $"{schedule.DisplayName} Copy");
            if (duplicate != null)
            {
                selectedScheduleId = duplicate.ScheduleId;
                schedulerScheduleNameBuffer = duplicate.DisplayName;
                plugin.PrintStatus($"Duplicated schedule '{duplicate.DisplayName}'.");
            }
        }

        if (identityActionsShareRow)
            ImGui.SameLine();
        ImGui.BeginDisabled(activeScheduleLocked && schedule != null &&
                            string.Equals(activeRun.ScheduleId, schedule.ScheduleId, StringComparison.OrdinalIgnoreCase));
        if (ImGui.SmallButton("Delete") && schedule != null)
        {
            pendingDeleteScheduleId = schedule.ScheduleId;
            ImGui.OpenPopup("Confirm delete schedule##dad-delete-schedule");
        }
        ImGui.EndDisabled();
        ImGui.EndDisabled();

        DrawDeleteSchedulePopup(snapshot);
        DrawScheduleShareControls(schedule);

        if (schedule == null)
        {
            DrawMutedNotice(groups.Count == 0
                ? "No schedule can be populated yet because no saved presets exist. Create a preset or open Schedule Wizard for guided next steps."
                : "No schedule selected. Create one here or use Schedule Wizard.");
            if (DadUi.Button("Open Schedule Wizard", DadUiTone.Accent))
                plugin.OpenSetupWizard(DadGuideFlow.Schedule);
            return;
        }

        var knownGroupIds = groups.Select(static group => group.GroupId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingPresetCount = schedule.Entries.Count(entry =>
            string.IsNullOrWhiteSpace(entry.GroupId) || !knownGroupIds.Contains(entry.GroupId));
        var firstBlocker = schedule.Entries.Count == 0
            ? "Add at least one saved preset entry."
            : missingPresetCount > 0
                ? $"{missingPresetCount} entry/entries reference a missing preset."
                : activeScheduleLocked
                    ? "A schedule is already running."
                    : Plugin.IsBusy(runState.VisibleRun)
                        ? "A DAD run is active."
                        : !plugin.Configuration.RunAsServerDad
                            ? "Live schedule execution requires the Coordinator role."
                            : string.Empty;
        var lastDryRun = snapshot.RecentResults.FirstOrDefault(result =>
            result.DryRun && string.Equals(result.ScheduleId, schedule.ScheduleId, StringComparison.OrdinalIgnoreCase));
        var canRunSchedule = plugin.Configuration.RunAsServerDad &&
                              schedule.Entries.Count > 0 &&
                              missingPresetCount == 0 &&
                              !activeScheduleLocked &&
                              !Plugin.IsBusy(runState.VisibleRun);
        var latestFailedRun = snapshot.RecentResults.FirstOrDefault(result =>
            string.Equals(result.ScheduleId, schedule.ScheduleId, StringComparison.OrdinalIgnoreCase) &&
            result.Status == DadScheduleRunStatus.Blocked);
        var retryEligibility = latestFailedRun == null
            ? new DadScheduleRetryResult { Summary = "No failed schedule entry is available to resume." }
            : plugin.SchedulerService.EvaluateFailedEntryRetry(
                new DadScheduleRetryRequest
                {
                    FailedRunId = latestFailedRun.RunId,
                    RequestedBy = "schedule-ui-retry",
                },
                Plugin.IsBusy(runState.VisibleRun));
        var useColumns = ImGui.GetContentRegionAvail().X >= ImGui.GetFontSize() * 66f;
        var skipBadges = DadScheduleSkipBadgeProjection.Build(
            schedule,
            activeRun,
            snapshot.RecentResults,
            plugin.SchedulerService.GetQueueSnapshot().RecentResults);
        if (!ImGui.BeginTable(
                "dad-schedule-workspace",
                useColumns ? 2 : 1,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
        {
            return;
        }

        if (useColumns)
        {
            ImGui.TableSetupColumn("Ordered presets", ImGuiTableColumnFlags.WidthStretch, 1.55f);
            ImGui.TableSetupColumn("Cadence and actions", ImGuiTableColumnFlags.WidthStretch, 1f);
        }

        ImGui.TableNextColumn();
        if (DadUi.BeginCard("dad-schedule-ordered-card"))
        {
            DadUi.Heading("ORDERED PRESETS", "Set the exact order and repeat count for each saved preset.");
            DrawScheduleEntryEditor(
                schedule,
                groups,
                activeScheduleLocked,
                plugin.GetPlannerUiSnapshot(runState),
                skipBadges);
            DadUi.EndCard();
        }

        ImGui.TableNextColumn();
        if (DadUi.BeginCard("dad-schedule-cadence-card"))
        {
            DadUi.Heading("CADENCE & ACTIONS", "Choose when this chain is eligible, then validate or run it.");
            if (activeScheduleLocked)
                DadUi.Badge("Locked while a schedule is active", DadUiTone.Warning);
            if (activeRun.IsActive)
                DrawStatusRow("Running now", DadScheduleCursorFormatter.Format(activeRun, snapshot.Schedules));

            var dailyMode = schedule.Cadence == DadScheduleCadence.DailyReset;
            ImGui.BeginDisabled(activeScheduleLocked);
            if (ImGui.Checkbox("Daily mode", ref dailyMode))
            {
                schedule.Cadence = dailyMode ? DadScheduleCadence.DailyReset : DadScheduleCadence.Manual;
                plugin.SchedulerService.UpdateSchedule(schedule);
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Daily mode runs once per FFXIV daily reset window at 15:00 UTC.");

            DrawStatusRow("Cadence", schedule.Cadence == DadScheduleCadence.DailyReset
                ? $"Daily reset at 15:00 UTC; next {FormatTime(DadScheduleRules.GetNextDailyResetUtc(DateTime.UtcNow))}"
                : "Manual only");
            DrawStatusRow("First blocker", FormatText(firstBlocker, "None"));
            DrawStatusRow("Last dry-run", lastDryRun == null
                ? "Not run"
                : $"{(lastDryRun.Success ? "Ready" : "Blocked")} | {FormatText(lastDryRun.BlockedReason, lastDryRun.Summary)}");

            ImGui.BeginDisabled(schedule.Entries.Count == 0 || missingPresetCount > 0 || activeScheduleLocked);
            if (ImGui.SmallButton("Dry-run"))
                plugin.StartScheduleRunFromShell(schedule.ScheduleId, dryRun: true, requestedBy: "schedule-ui-dry-run");
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!canRunSchedule);
            if (ImGui.SmallButton("Run now"))
                plugin.StartScheduleRunFromShell(schedule.ScheduleId, dryRun: false, requestedBy: "schedule-ui");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canRunSchedule)
                ImGui.SetTooltip(FormatText(firstBlocker, "Schedule is not ready."));
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!activeScheduleLocked ||
                                !string.Equals(activeRun.ScheduleId, schedule.ScheduleId, StringComparison.OrdinalIgnoreCase));
            if (ImGui.SmallButton("Cancel"))
                plugin.CancelScheduleRunFromShell("Schedule cancelled from Schedules.");
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton("Open Status"))
                NavigateToStatus(activeScheduleLocked ? DadStatusWindowTab.CurrentActivity : DadStatusWindowTab.QueueHistory);
            ImGui.BeginDisabled(latestFailedRun == null || !retryEligibility.Eligible);
            if (ImGui.SmallButton("Resume from failed entry") && latestFailedRun != null)
            {
                pendingRetryScheduleRunId = latestFailedRun.RunId;
                ImGui.OpenPopup("Confirm retry failed schedule entry##dad-schedule-retry-confirm");
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(retryEligibility.Summary);
            ImGui.EndDisabled();
            DadUi.EndCard();
        }

        ImGui.EndTable();
        DrawRetryFailedEntryPopup(runState);
    }

    private void DrawRetryFailedEntryPopup(DadVisibleRunState runState)
    {
        if (!ImGui.BeginPopupModal(
                "Confirm retry failed schedule entry##dad-schedule-retry-confirm",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        var request = new DadScheduleRetryRequest
        {
            FailedRunId = pendingRetryScheduleRunId,
            RequestedBy = "schedule-ui-retry",
        };
        var eligibility = plugin.SchedulerService.EvaluateFailedEntryRetry(
            request,
            Plugin.IsBusy(runState.VisibleRun));
        ImGui.TextWrapped("Resume this failed Schedule entry from its persisted entry and repeat cursor?");
        ImGui.TextWrapped("This creates a new Schedule run at the persisted cursor, retains prior history, requires every client and DAD/scheduler lane to be idle, and never replays automatically.");
        DrawStatusRow("Eligibility", eligibility.Summary);

        ImGui.BeginDisabled(!eligibility.Eligible);
        if (ImGui.Button("Resume from failed entry"))
        {
            var retried = plugin.SchedulerService.RetryFailedEntry(
                request,
                Plugin.IsBusy(runState.VisibleRun));
            plugin.PrintStatus(retried.Summary);
            if (retried.Retried)
            {
                pendingRetryScheduleRunId = string.Empty;
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            pendingRetryScheduleRunId = string.Empty;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void EnsureScheduleSelection(DadScheduleSnapshot snapshot)
    {
        var selected = snapshot.Schedules.FirstOrDefault(schedule =>
            string.Equals(schedule.ScheduleId, selectedScheduleId, StringComparison.OrdinalIgnoreCase));
        if (selected != null)
        {
            if (string.IsNullOrWhiteSpace(schedulerScheduleNameBuffer))
                schedulerScheduleNameBuffer = selected.DisplayName;
            return;
        }

        selected = snapshot.Schedules.FirstOrDefault();
        selectedScheduleId = selected?.ScheduleId ?? string.Empty;
        schedulerScheduleNameBuffer = selected?.DisplayName ?? schedulerScheduleNameBuffer;
    }

    private void DrawDeleteSchedulePopup(DadScheduleSnapshot snapshot)
    {
        if (!ImGui.BeginPopupModal("Confirm delete schedule##dad-delete-schedule", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var pending = snapshot.Schedules.FirstOrDefault(schedule =>
            string.Equals(schedule.ScheduleId, pendingDeleteScheduleId, StringComparison.OrdinalIgnoreCase));
        ImGui.TextWrapped(pending == null
            ? "Delete this schedule?"
            : $"Delete schedule '{pending.DisplayName}'?");

        if (ImGui.SmallButton("Delete##dad-confirm-delete-schedule"))
        {
            if (plugin.SchedulerService.DeleteSchedule(pendingDeleteScheduleId))
            {
                selectedScheduleId = string.Empty;
                plugin.PrintStatus("Deleted schedule.");
            }

            pendingDeleteScheduleId = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Cancel"))
        {
            pendingDeleteScheduleId = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawScheduleShareControls(DadScheduleDefinition? schedule)
    {
        var mutationBlocker = plugin.GetShareMutationBlocker();
        var mutationLocked = !string.IsNullOrWhiteSpace(mutationBlocker);

        ImGui.Spacing();
        ImGui.BeginDisabled(schedule == null);
        if (ImGui.SmallButton("Export##dad-share-schedule-export") && schedule != null)
        {
            if (plugin.TryExportSchedule(schedule.ScheduleId, out var encoded, out var error))
            {
                ImGui.SetClipboardText(encoded);
                scheduleShareStatus = $"Copied Schedule '{schedule.DisplayName}' to the clipboard.";
            }
            else
            {
                scheduleShareStatus = error;
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Copies this Schedule and every referenced Plan exactly once. Base64 is transport encoding, not encryption; finish slash commands remain verbatim.");
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(mutationLocked);
        if (ImGui.SmallButton("Import##dad-share-schedule-import"))
        {
            var clipboard = ImGui.GetClipboardText() ?? string.Empty;
            if (plugin.TryDecodeShare(clipboard, DadShareConstants.ScheduleKind, out var envelope, out var error) && envelope != null)
            {
                pendingScheduleShareImport = envelope;
                pendingScheduleSharePreview = plugin.ShareService.BuildImportPreview(
                    envelope,
                    plugin.Configuration.PlannerGroups,
                    plugin.Configuration.Schedules);
                pendingScheduleShareCommandsConfirmed = false;
                scheduleShareStatus = string.Empty;
                ImGui.OpenPopup("Confirm Schedule import##dad-share-schedule-confirm");
            }
            else
            {
                scheduleShareStatus = error;
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(mutationLocked
                ? mutationBlocker
                : "Reads a Schedule share from the clipboard. Matching IDs are replaced after confirmation; imported crew must be remapped locally.");
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(schedule == null || mutationLocked);
        if (ImGui.SmallButton("ID##dad-share-schedule-id") && schedule != null)
        {
            scheduleShareIdOwner = schedule.ScheduleId;
            scheduleShareIdEdit = schedule.ScheduleId;
            ImGui.OpenPopup("Schedule share details##dad-share-schedule-details");
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(mutationLocked ? mutationBlocker : "View, copy, or safely change this Schedule's sharing ID.");
        ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(scheduleShareStatus))
            ImGui.TextDisabled(scheduleShareStatus);

        DrawScheduleShareDetailsPopup(schedule);
        DrawScheduleImportConfirmation();
    }

    private void DrawScheduleShareDetailsPopup(DadScheduleDefinition? schedule)
    {
        if (!ImGui.BeginPopup("Schedule share details##dad-share-schedule-details", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        schedule = plugin.Configuration.Schedules.FirstOrDefault(candidate =>
            string.Equals(candidate.ScheduleId, scheduleShareIdOwner, StringComparison.OrdinalIgnoreCase)) ?? schedule;
        if (schedule == null)
        {
            ImGui.TextDisabled("The Schedule is no longer available.");
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted("Share details");
        var currentId = schedule.ScheduleId;
        ImGui.SetNextItemWidth(310f);
        ImGui.InputText("Current ID##dad-share-schedule-current-id", ref currentId, 33, ImGuiInputTextFlags.ReadOnly);
        if (ImGui.SmallButton("Copy##dad-share-schedule-copy-id"))
        {
            ImGui.SetClipboardText(schedule.ScheduleId);
            scheduleShareStatus = "Copied Schedule ID.";
        }

        ImGui.SetNextItemWidth(310f);
        ImGui.InputText("New ID##dad-share-schedule-new-id", ref scheduleShareIdEdit, 33);
        var mutationBlocker = plugin.GetShareMutationBlocker();
        ImGui.BeginDisabled(!string.IsNullOrWhiteSpace(mutationBlocker));
        if (ImGui.SmallButton("Apply##dad-share-schedule-apply-id"))
        {
            var result = plugin.RenameScheduleId(schedule.ScheduleId, scheduleShareIdEdit);
            scheduleShareStatus = result.Summary;
            if (result.Success)
            {
                selectedScheduleId = result.NewId;
                scheduleShareIdOwner = result.NewId;
                scheduleShareIdEdit = result.NewId;
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrWhiteSpace(mutationBlocker))
            ImGui.SetTooltip(mutationBlocker);
        ImGui.EndDisabled();
        ImGui.TextDisabled("Use a unique canonical lowercase 32-hex GUID.");
        ImGui.EndPopup();
    }

    private void DrawScheduleImportConfirmation()
    {
        if (!ImGui.BeginPopupModal("Confirm Schedule import##dad-share-schedule-confirm", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var preview = pendingScheduleSharePreview;
        if (preview == null || pendingScheduleShareImport == null)
        {
            ImGui.TextDisabled("The decoded Schedule share is no longer available.");
        }
        else
        {
            ImGui.TextWrapped($"Import Schedule '{preview.Name}'?");
            ImGui.TextUnformatted($"ID: {preview.Id}");
            ImGui.TextUnformatted($"Bundled Plans: {preview.BundledPlanCount.ToString(CultureInfo.InvariantCulture)}");
            DrawShareReplacementSummary(preview);
            DrawShareCommandReview(preview, ref pendingScheduleShareCommandsConfirmed);
            ImGui.TextWrapped("Imported crew identities are anonymous placeholders. Remap every row in the Plan crew editor before validation or run.");
            ImGui.TextWrapped("Base64 is not encryption. Finish slash commands are preserved verbatim; review them before running an imported Plan.");
        }

        var mutationBlocker = plugin.GetShareMutationBlocker();
        ImGui.BeginDisabled(preview == null || pendingScheduleShareImport == null ||
                            preview.RequiresCommandConfirmation && !pendingScheduleShareCommandsConfirmed ||
                            !string.IsNullOrWhiteSpace(mutationBlocker));
        if (ImGui.SmallButton("Import##dad-share-schedule-confirm-import"))
        {
            var result = plugin.ApplyShareImport(
                pendingScheduleShareImport!,
                pendingScheduleShareCommandsConfirmed);
            scheduleShareStatus = result.Summary;
            if (result.Success)
            {
                selectedScheduleId = result.ResultId;
                var imported = plugin.Configuration.Schedules.FirstOrDefault(candidate =>
                    string.Equals(candidate.ScheduleId, result.ResultId, StringComparison.OrdinalIgnoreCase));
                schedulerScheduleNameBuffer = imported?.DisplayName ?? schedulerScheduleNameBuffer;
            }
            pendingScheduleShareImport = null;
            pendingScheduleSharePreview = null;
            pendingScheduleShareCommandsConfirmed = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton("Cancel##dad-share-schedule-confirm-cancel"))
        {
            pendingScheduleShareImport = null;
            pendingScheduleSharePreview = null;
            pendingScheduleShareCommandsConfirmed = false;
            ImGui.CloseCurrentPopup();
        }
        if (!string.IsNullOrWhiteSpace(mutationBlocker))
            ImGui.TextDisabled(mutationBlocker);
        ImGui.EndPopup();
    }

    private void DrawScheduleEntryEditor(
        DadScheduleDefinition schedule,
        IReadOnlyList<DadPlannerGroup> groups,
        bool activeScheduleLocked,
        DadPlannerUiSnapshot plannerSnapshot,
        DadScheduleSkipBadgeProjectionResult skipBadges)
    {
        if (groups.Count == 0)
        {
            DrawMutedNotice("No saved presets are available. Create a preset before adding schedule rows.");
            if (DadUi.Button("Guide: Create a Preset"))
                plugin.OpenSetupWizard(DadGuideFlow.FirstPreset);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(schedulerAddPresetGroupId) ||
                groups.All(group => !string.Equals(group.GroupId, schedulerAddPresetGroupId, StringComparison.OrdinalIgnoreCase)))
            {
                schedulerAddPresetGroupId = groups[0].GroupId;
            }

            ImGui.BeginDisabled(activeScheduleLocked);
            DrawSchedulePresetCombo("Add preset", ref schedulerAddPresetGroupId, groups, "add");
            ImGui.SameLine();
            if (ImGui.SmallButton("Add"))
            {
                var group = groups.FirstOrDefault(candidate =>
                    string.Equals(candidate.GroupId, schedulerAddPresetGroupId, StringComparison.OrdinalIgnoreCase));
                if (group != null)
                {
                    schedule.Entries.Add(new DadScheduleEntry
                    {
                        GroupId = group.GroupId,
                        PresetName = group.DisplayName,
                        RepeatCount = 1,
                    });
                    plugin.SchedulerService.UpdateSchedule(schedule);
                }
            }
            ImGui.EndDisabled();
        }

        if (schedule.Entries.Count == 0)
        {
            DrawMutedNotice("No presets in this schedule.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(skipBadges.HistoryNotice))
            DadUi.Badge(skipBadges.HistoryNotice, DadUiTone.Neutral);

        if (!ImGui.BeginTable("dad-schedule-entries", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Order").X + 12f);
        ImGui.TableSetupColumn("Preset", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Repeats", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Repeats 999").X + 16f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Up Down Remove").X + 48f);
        ImGui.TableHeadersRow();

        for (var index = 0; index < schedule.Entries.Count; index++)
        {
            var entry = schedule.Entries[index];
            var group = groups.FirstOrDefault(candidate =>
                string.Equals(candidate.GroupId, entry.GroupId, StringComparison.OrdinalIgnoreCase));
            var levelSeekDisplay = plugin.BuildScheduleLevelSeekDisplay(group, plannerSnapshot);
            ImGui.TableNextRow();
            if (levelSeekDisplay.IsSkipIndicated)
            {
                ImGui.TableSetBgColor(
                    ImGuiTableBgTarget.RowBg0,
                    ImGui.GetColorU32(DadUi.WithAlpha(DadUi.Warning, 0.12f)));
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((index + 1).ToString(CultureInfo.InvariantCulture));

            ImGui.TableNextColumn();
            var entryGroupId = entry.GroupId;
            ImGui.BeginDisabled(activeScheduleLocked);
            if (group == null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, DadUi.Danger);
                ImGui.TextWrapped($"Missing preset: {FormatText(entry.PresetName, entry.GroupId)}");
                ImGui.PopStyleColor();
            }
            if (levelSeekDisplay.IsSkipIndicated)
                ImGui.PushStyleColor(ImGuiCol.Text, DadUi.Warning);
            var presetChanged = DrawSchedulePresetCombo(
                $"##schedule-entry-preset-{entry.EntryId}",
                ref entryGroupId,
                groups,
                entry.EntryId);
            if (levelSeekDisplay.IsSkipIndicated)
                ImGui.PopStyleColor();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) &&
                !string.IsNullOrWhiteSpace(levelSeekDisplay.Tooltip))
            {
                ImGui.SetTooltip(levelSeekDisplay.Tooltip);
            }
            if (presetChanged)
            {
                var selected = groups.FirstOrDefault(candidate =>
                    string.Equals(candidate.GroupId, entryGroupId, StringComparison.OrdinalIgnoreCase));
                if (selected != null)
                {
                    entry.GroupId = selected.GroupId;
                    entry.PresetName = selected.DisplayName;
                    entry.UpdatedAtUtc = DateTime.UtcNow;
                    plugin.SchedulerService.UpdateSchedule(schedule);
                }
            }
            ImGui.EndDisabled();
            if (skipBadges.Badges.TryGetValue(entry.EntryId, out var skipBadge))
            {
                DadUi.Badge(skipBadge.Label, DadUiTone.Warning);
                if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(skipBadge.Tooltip))
                    ImGui.SetTooltip(skipBadge.Tooltip);
            }

            ImGui.TableNextColumn();
            var repeat = entry.RepeatCount;
            ImGui.BeginDisabled(activeScheduleLocked);
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputInt($"##schedule-entry-repeat-{entry.EntryId}", ref repeat))
            {
                entry.RepeatCount = Math.Clamp(repeat, DadScheduleRules.MinRepeatCount, DadScheduleRules.MaxRepeatCount);
                entry.UpdatedAtUtc = DateTime.UtcNow;
                plugin.SchedulerService.UpdateSchedule(schedule);
            }
            ImGui.EndDisabled();

            ImGui.TableNextColumn();
            var changedOrder = false;
            ImGui.BeginDisabled(activeScheduleLocked || index == 0);
            if (ImGui.SmallButton($"Up##schedule-entry-up-{entry.EntryId}"))
            {
                (schedule.Entries[index - 1], schedule.Entries[index]) = (schedule.Entries[index], schedule.Entries[index - 1]);
                plugin.SchedulerService.UpdateSchedule(schedule);
                changedOrder = true;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(activeScheduleLocked || index >= schedule.Entries.Count - 1);
            if (ImGui.SmallButton($"Down##schedule-entry-down-{entry.EntryId}"))
            {
                (schedule.Entries[index + 1], schedule.Entries[index]) = (schedule.Entries[index], schedule.Entries[index + 1]);
                plugin.SchedulerService.UpdateSchedule(schedule);
                changedOrder = true;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(activeScheduleLocked);
            if (ImGui.SmallButton($"Remove##schedule-entry-remove-{entry.EntryId}"))
            {
                schedule.Entries.RemoveAt(index);
                plugin.SchedulerService.UpdateSchedule(schedule);
                changedOrder = true;
            }
            ImGui.EndDisabled();
            if (changedOrder)
                break;
        }

        ImGui.EndTable();
    }

    private bool DrawSchedulePresetCombo(
        string label,
        ref string groupId,
        IReadOnlyList<DadPlannerGroup> groups,
        string idSuffix)
    {
        var currentGroupId = groupId;
        var duplicateNames = groups
            .GroupBy(static group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedGroup = groups.FirstOrDefault(group =>
            string.Equals(group.GroupId, currentGroupId, StringComparison.OrdinalIgnoreCase));
        var preview = selectedGroup == null
            ? "(missing)"
            : FormatPlannerGroupChoice(selectedGroup.DisplayName, selectedGroup.GroupId, duplicateNames);
        var changed = false;
        ImGui.SetNextItemWidth(MathF.Min(360f, ImGui.GetContentRegionAvail().X));
        if (!ImGui.BeginCombo($"{label}##dad-schedule-preset-{idSuffix}", preview))
            return false;

        foreach (var group in groups)
        {
            var selected = string.Equals(group.GroupId, currentGroupId, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(FormatPlannerGroupChoice(group.DisplayName, group.GroupId, duplicateNames), selected))
            {
                groupId = group.GroupId;
                currentGroupId = group.GroupId;
                changed = true;
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
        return changed;
    }

    private void DrawScheduleRecentResults(DadScheduleSnapshot snapshot, string scheduleId)
    {
        ImGui.Separator();
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(scheduleId) ? "Schedule history" : "Recent schedule runs");
        var results = snapshot.RecentResults
            .Where(result => string.IsNullOrWhiteSpace(scheduleId) ||
                             string.Equals(result.ScheduleId, scheduleId, StringComparison.OrdinalIgnoreCase))
            .Take(string.IsNullOrWhiteSpace(scheduleId) ? 20 : 8)
            .ToList();
        if (results.Count == 0)
        {
            DrawMutedNotice("No terminal schedule runs recorded yet.");
            return;
        }

        if (!ImGui.BeginTable("dad-schedule-history", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Done");
        ImGui.TableSetupColumn("Status");
        ImGui.TableSetupColumn("Mode");
        ImGui.TableSetupColumn("Progress");
        ImGui.TableSetupColumn("Summary");
        ImGui.TableHeadersRow();

        foreach (var result in results)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatTime(result.CompletedAtUtc));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(result.Status.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(result.DryRun ? "dry-run" : result.ManualRun ? "manual" : "daily");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{result.CompletedEntryExecutions}/{result.TotalEntryExecutions}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatText(result.BlockedReason, result.Summary));
        }

        ImGui.EndTable();
    }

    private void DrawCrewQueueSection()
    {
        var queue = plugin.SchedulerService.GetQueueSnapshot();
        DrawSectionHeader("Queue", "Dad Coordinator runs one active job; new requests wait behind it.");
        DrawStatusRow("Summary", queue.Summary);
        DrawStatusRow("Active owner", FormatText(queue.ActiveQueueOwner, "(none)"));

        if (queue.PendingJobs.Count == 0)
        {
            DrawMutedNotice("No queued scheduler jobs.");
        }
        else if (ImGui.BeginTable("dad-scheduler-queue", 9, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Type");
            ImGui.TableSetupColumn("Preset");
            ImGui.TableSetupColumn("Owner");
            ImGui.TableSetupColumn("Priority");
            ImGui.TableSetupColumn("Eligible");
            ImGui.TableSetupColumn("Map");
            ImGui.TableSetupColumn("Targets");
            ImGui.TableSetupColumn("Status");
            ImGui.TableSetupColumn("Edit");
            ImGui.TableHeadersRow();

            foreach (var job in queue.PendingJobs)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(job.JobType.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatText(job.PresetName, job.GroupId));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatText(job.RequestedBy, "(scheduler)"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(job.Priority.ToString(CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(job.NextEligibleTimeUtc.HasValue ? FormatTime(job.NextEligibleTimeUtc) : "now");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(job.JobType == DadSchedulerJobType.MapCrew
                    ? $"{job.MapMode}{(string.IsNullOrWhiteSpace(job.MapRunTemplate) ? string.Empty : $" / {job.MapRunTemplate}")}"
                    : "-");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(job.TargetCharacterKeys.Count == 0
                    ? FormatRosterTargets(job.TargetCharacters)
                    : FormatRosterTargets(job.TargetCharacters, plugin.KrangleService.FormatCharacterKeys(job.TargetCharacterKeys)));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatText(job.StatusSummary, job.BlockedReason));
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Cancel##dad-cancel-job-{job.JobId}"))
                    plugin.CancelScheduledJobFromJson(DadIpcJson.Serialize(new DadCancelScheduledJobRequest
                    {
                        JobId = job.JobId,
                        Reason = "Cancelled from Crew / Scheduler queue.",
                    }));
            }

            ImGui.EndTable();
        }

        DrawSchedulerRecentResults(queue);
    }

    private void DrawSchedulerRecentResults(DadSchedulerQueueSnapshot queue)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Recent terminal jobs");
        if (queue.RecentResults.Count == 0)
        {
            DrawMutedNotice("No terminal scheduler history recorded yet.");
            return;
        }

        if (!ImGui.BeginTable("dad-scheduler-history", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Done");
        ImGui.TableSetupColumn("Type");
        ImGui.TableSetupColumn("Preset");
        ImGui.TableSetupColumn("Owner");
        ImGui.TableSetupColumn("Phase");
        ImGui.TableSetupColumn("Result");
        ImGui.TableSetupColumn("Summary");
        ImGui.TableHeadersRow();

        foreach (var result in queue.RecentResults.Take(12))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatTime(result.CompletedAtUtc));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(result.JobType.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatText(result.PresetName, result.GroupId));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatText(result.RequestedBy, "(scheduler)"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(result.FinalPhase.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(result.Success ? "success" : "blocked");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatText(result.BlockedReason, result.Summary));
        }

        ImGui.EndTable();
    }

    private void DrawCrewActiveJobSection()
    {
        var state = plugin.SchedulerService.CurrentState;
        var queue = plugin.SchedulerService.GetQueueSnapshot();
        var run = plugin.RunCoordinatorService.GetLocalResult();
        var worker = plugin.WorkerExecutionService.GetStatus();
        DrawSectionHeader("Active Job", "Current orchestration, worker execution, typed wake takeover readiness, and durable results.");
        DrawStatusRow("Run", $"{run.Status} / {run.Phase} / {run.ModuleId}");
        DrawStatusRow("Run summary", run.Summary);
        DrawStatusRow("Worker", $"{worker.Role} / {worker.State} / {worker.Summary}");
        DrawStatusRow("Leases", run.Leases.Count == 0
            ? "(none)"
            : string.Join(" | ", run.Leases.Select(static lease => $"{lease.SlotId}:{lease.State}@{lease.ExpiresUtc:HH:mm:ss}")));
        DrawStatusRow("State", $"{state.JobType} | {state.Phase}");
        DrawStatusRow("Summary", state.Summary);
        DrawStatusRow("Job", FormatText(state.JobId, "(none)"));
        DrawStatusRow("Owner", FormatText(state.RequestedBy, "(scheduler)"));
        DrawStatusRow("Preset", FormatText(state.PresetName, "(none)"));
        if (!string.IsNullOrWhiteSpace(state.ScheduleRunId))
            DrawStatusRow("Schedule", $"entry {state.ScheduleEntryIndex + 1}, repeat {state.ScheduleRepeatIteration} | {state.ScheduleRunId}");
        if (queue.ActiveJob?.JobType == DadSchedulerJobType.MapCrew)
            DrawStatusRow("Map crew", $"{queue.ActiveJob.MapMode}{(string.IsNullOrWhiteSpace(queue.ActiveJob.MapRunTemplate) ? string.Empty : $" | {queue.ActiveJob.MapRunTemplate}")}");
        DrawStatusRow("Queue", queue.Summary);
        if (!string.IsNullOrWhiteSpace(state.BlockedReason))
            DrawStatusRow("Blocker", state.BlockedReason);

        if (state.Slots.Count == 0)
        {
            DrawMutedNotice("No active scheduler slot state.");
            return;
        }

        if (!ImGui.BeginTable("dad-active-scheduler-slots", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Slot");
        ImGui.TableSetupColumn("Account");
        ImGui.TableSetupColumn("Target");
        ImGui.TableSetupColumn("Active");
        ImGui.TableSetupColumn("Wake");
        ImGui.TableSetupColumn("Takeover");
        ImGui.TableSetupColumn("Ready");
        ImGui.TableSetupColumn("Status");
        ImGui.TableHeadersRow();

        foreach (var slot in state.Slots)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.SlotId);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.RequiredAccountKey.Value);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatOperatorCharacterKey(slot.RequiredCharacterKey.Value, "(any)"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatOperatorCharacterKey(slot.ActiveCharacterKey.Value, "(offline)"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{DadDebugUiRules.FormatWakePolicy(slot.WakePolicy, plugin.Configuration.DebugUiEnabled)} / {slot.RosterVisibility}{(slot.NeedsRosterUpdate ? " / needs update" : string.Empty)}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline
                ? slot.TakeoverStage == DadWakeTakeoverStage.Ready && !slot.Ready
                    ? "heartbeat revalidation failed"
                    : $"{slot.TakeoverStatus} / {slot.TakeoverStage}{(slot.RelogIssued ? " / relog sent" : string.Empty)}"
                : slot.WakePolicy == DadSchedulerWakePolicy.LoadCharacterIfOnline
                    ? "stub / no commands"
                    : "none");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.Ready
                ? "ready"
                : slot.IsOnline && !slot.CorrectCharacter
                    ? $"mismatch: active {FormatOperatorCharacterKey(slot.ActiveCharacterKey.Value, "unknown")}"
                : slot.IsOnline
                    ? "target online / not ready"
                    : slot.ClientConnected
                        ? "client connected / character offline"
                        : "client missing");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatOperatorText(FormatText(slot.BlockedReason, slot.Summary), "(none)"));
        }

        ImGui.EndTable();
    }

    private void DrawRunHistory()
    {
        var history = plugin.Configuration.RunHistory ?? [];
        ImGui.Separator();
        ImGui.TextUnformatted("Run history");
        if (history.Count == 0)
        {
            ImGui.TextDisabled("No durable run results.");
            return;
        }

        if (!ImGui.BeginTable("dad-run-history", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            return;
        ImGui.TableSetupColumn("Completed");
        ImGui.TableSetupColumn("Module");
        ImGui.TableSetupColumn("Status");
        ImGui.TableSetupColumn("Request");
        ImGui.TableSetupColumn("Summary");
        ImGui.TableHeadersRow();
        foreach (var result in history)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(result.CompletedAtUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(result.ModuleId.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(result.Status.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(result.RequestId);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(result.Summary);
        }
        ImGui.EndTable();
    }

    private void DrawRosterAccountSelector(DadAccountRosterCatalog catalog)
    {
        var options = GetRosterAccountOptions(catalog);
        var unassignedCount = catalog.Characters.Count(static character => character.AccountKey.IsEmpty);
        var accountOptionCount = options.Count;

        if (!string.IsNullOrWhiteSpace(rosterAccountFilter) &&
            !string.Equals(rosterAccountFilter, RosterUnassignedAccountFilter, StringComparison.OrdinalIgnoreCase) &&
            options.All(option => !MatchesRosterAccountOptionFilter(option, rosterAccountFilter)))
        {
            rosterAccountFilter = string.Empty;
        }

        var selected = options.FirstOrDefault(option => MatchesRosterAccountOptionFilter(option, rosterAccountFilter));
        var preview = string.Equals(rosterAccountFilter, RosterUnassignedAccountFilter, StringComparison.OrdinalIgnoreCase)
            ? $"Unassigned ({unassignedCount})"
            : selected == null
            ? $"All accounts ({accountOptionCount})"
            : $"{FormatRosterAccountOption(selected)} ({selected.AssignedCharacterCount})";

        ImGui.SetNextItemWidth(MathF.Min(280f, ImGui.GetContentRegionAvail().X));
        if (!ImGui.BeginCombo("Account", preview))
            return;

        if (ImGui.Selectable($"All accounts ({accountOptionCount})", string.IsNullOrWhiteSpace(rosterAccountFilter)))
        {
            rosterAccountFilter = string.Empty;
            rosterAccountInitialized = true;
        }
        if (string.IsNullOrWhiteSpace(rosterAccountFilter))
            ImGui.SetItemDefaultFocus();

        if (ImGui.Selectable($"Unassigned ({unassignedCount})", string.Equals(rosterAccountFilter, RosterUnassignedAccountFilter, StringComparison.OrdinalIgnoreCase)))
        {
            rosterAccountFilter = RosterUnassignedAccountFilter;
            rosterAccountInitialized = true;
        }
        if (string.Equals(rosterAccountFilter, RosterUnassignedAccountFilter, StringComparison.OrdinalIgnoreCase))
            ImGui.SetItemDefaultFocus();

        foreach (var option in options)
        {
            var isSelected = MatchesRosterAccountOptionFilter(option, rosterAccountFilter);
            if (ImGui.Selectable($"{FormatRosterAccountOption(option)} ({option.AssignedCharacterCount})", isSelected))
            {
                rosterAccountFilter = BuildRosterAccountFilterKey(option);
                rosterAccountInitialized = true;
            }
            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private enum RosterBrowseResetMode
    {
        AllRows,
        LocalAccount,
        KeepSelectedAccount,
    }

    private void ResetRosterBrowseFilters(DadAccountRosterCatalog catalog, RosterBrowseResetMode mode)
    {
        rosterSearch = string.Empty;
        rosterAssignedFilter = string.Empty;
        rosterVisibilityFilter = string.Empty;
        rosterWorldDcFilter = string.Empty;
        rosterSourceFilter = string.Empty;
        rosterClientFilter = string.Empty;
        rosterStaleOnly = false;
        rosterSelectedRows.Clear();

        switch (mode)
        {
            case RosterBrowseResetMode.LocalAccount:
                rosterAccountFilter = string.Empty;
                rosterAccountInitialized = false;
                EnsureRosterAccountSelection(catalog);
                break;
            case RosterBrowseResetMode.KeepSelectedAccount:
                EnsureRosterAccountSelection(catalog);
                break;
            default:
                rosterAccountFilter = string.Empty;
                rosterAccountInitialized = true;
                break;
        }
    }

    private List<DadRosterAccountOption> GetRosterAccountOptions(DadAccountRosterCatalog catalog)
    {
        var options = new Dictionary<string, DadRosterAccountOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in catalog.Accounts.Count > 0 ? catalog.Accounts : plugin.RosterCatalogService.GetAccountDirectory())
        {
            if (source.AccountKey.IsEmpty)
                continue;

            var accountKey = (source.AccountKey.Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(accountKey))
                continue;

            var candidate = source.Clone();
            candidate.AccountKey = new DadAccountKey(accountKey);
            candidate.AccountAlias = candidate.AccountAlias?.Trim() ?? string.Empty;
            candidate.DisplayName = ResolveRosterAccountDisplayName(candidate);
            candidate.SourceClientInstanceId = string.Empty;

            if (!options.TryGetValue(accountKey, out var existing))
            {
                options[accountKey] = candidate;
                continue;
            }

            if (ShouldUseRosterAccountDisplayName(existing, candidate))
            {
                existing.AccountAlias = candidate.AccountAlias;
                existing.DisplayName = candidate.DisplayName;
            }

            if (existing.SourceWorkerSessionId.IsEmpty)
                existing.SourceWorkerSessionId = candidate.SourceWorkerSessionId;
            existing.IsLocal |= candidate.IsLocal;
            existing.AssignedCharacterCount = Math.Max(existing.AssignedCharacterCount, candidate.AssignedCharacterCount);
        }

        foreach (var option in options.Values)
        {
            var count = catalog.Characters
                .Where(character => !character.AccountKey.IsEmpty &&
                                    DadRosterIdentity.SameAccount(character.AccountKey, option.AccountKey))
                .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
                .Count();
            option.AssignedCharacterCount = Math.Max(option.AssignedCharacterCount, count);
        }

        return options.Values
            .OrderByDescending(static option => option.IsLocal)
            .ThenBy(static option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static option => option.AccountKey.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void EnsureRosterAccountSelection(DadAccountRosterCatalog catalog)
    {
        var options = GetRosterAccountOptions(catalog);
        var hasValidFilter = string.IsNullOrWhiteSpace(rosterAccountFilter) ||
                             string.Equals(rosterAccountFilter, RosterUnassignedAccountFilter, StringComparison.OrdinalIgnoreCase) ||
                             options.Any(option => MatchesRosterAccountOptionFilter(option, rosterAccountFilter));
        if (!hasValidFilter)
        {
            rosterAccountFilter = string.Empty;
            rosterAccountInitialized = false;
        }

        if (rosterAccountInitialized)
            return;

        var currentAccountId = plugin.Configuration.ClientAccountId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(currentAccountId))
            currentAccountId = plugin.ConfigManager.CurrentAccountId?.Trim() ?? string.Empty;
        var preferred = options.FirstOrDefault(option =>
                            option.IsLocal &&
                            !string.IsNullOrWhiteSpace(currentAccountId) &&
                            string.Equals(option.AccountKey.Value, currentAccountId, StringComparison.OrdinalIgnoreCase))
                        ?? options.FirstOrDefault(static option => option.IsLocal)
                        ?? options.FirstOrDefault(option =>
                            !string.IsNullOrWhiteSpace(currentAccountId) &&
                            string.Equals(option.AccountKey.Value, currentAccountId, StringComparison.OrdinalIgnoreCase))
                        ?? (options.Count == 1 ? options[0] : null);
        if (preferred != null)
        {
            rosterAccountFilter = BuildRosterAccountFilterKey(preferred);
            rosterAccountInitialized = true;
            return;
        }

        if (options.Count > 0 && !string.IsNullOrWhiteSpace(currentAccountId))
            rosterAccountInitialized = true;
    }

    private IEnumerable<DadRosterCharacter> GetAccountScopedRosterCharacters(IEnumerable<DadRosterCharacter> characters)
    {
        var accountFilter = rosterAccountFilter.Trim();
        return characters.Where(character => string.IsNullOrWhiteSpace(accountFilter) ||
                                             string.Equals(accountFilter, RosterUnassignedAccountFilter, StringComparison.OrdinalIgnoreCase) && character.AccountKey.IsEmpty ||
                                             MatchesRosterAccountFilter(character, accountFilter));
    }

    private string BuildSelectedRosterAccountSummary(DadAccountRosterCatalog catalog, IReadOnlyList<DadRosterCharacter> accountScoped)
    {
        var options = GetRosterAccountOptions(catalog);
        var selected = options.FirstOrDefault(option => MatchesRosterAccountOptionFilter(option, rosterAccountFilter));
        var label = string.Equals(rosterAccountFilter, RosterUnassignedAccountFilter, StringComparison.OrdinalIgnoreCase)
            ? "Unassigned"
            : string.IsNullOrWhiteSpace(rosterAccountFilter)
                ? "All accounts"
                : selected == null ? rosterAccountFilter : FormatRosterAccountOption(selected);
        var active = accountScoped.Count(static character => character.Visibility == DadRosterVisibility.Active);
        var hidden = accountScoped.Count(static character => character.Visibility == DadRosterVisibility.Hidden);
        var ignored = accountScoped.Count(static character => character.Visibility == DadRosterVisibility.Ignored);
        var needsUpdate = accountScoped.Count(static character => character.NeedsRosterUpdate);
        var stale = accountScoped.Count(static character => character.IsStale);
        return $"{label}: {accountScoped.Count} row(s), {active} active, {hidden} hidden, {ignored} ignored, {needsUpdate} need update, {stale} stale.";
    }

    private void DrawRosterVisibilityTabs(IReadOnlyList<DadRosterCharacter> accountScoped)
    {
        DrawRosterVisibilityTab("Active", DadRosterVisibility.Active.ToString(), accountScoped.Count(static character => character.Visibility == DadRosterVisibility.Active));
        ImGui.SameLine();
        DrawRosterVisibilityTab("Hidden", DadRosterVisibility.Hidden.ToString(), accountScoped.Count(static character => character.Visibility == DadRosterVisibility.Hidden));
        ImGui.SameLine();
        DrawRosterVisibilityTab("Ignored", DadRosterVisibility.Ignored.ToString(), accountScoped.Count(static character => character.Visibility == DadRosterVisibility.Ignored));
        ImGui.SameLine();
        DrawRosterVisibilityTab("Needs update", RosterNeedsUpdateFilter, accountScoped.Count(static character => character.NeedsRosterUpdate));
        ImGui.SameLine();
        DrawRosterVisibilityTab("All", string.Empty, accountScoped.Count);
    }

    private void DrawRosterVisibilityTab(string label, string value, int count)
    {
        var selected = string.IsNullOrWhiteSpace(value)
            ? string.IsNullOrWhiteSpace(rosterVisibilityFilter)
            : string.Equals(rosterVisibilityFilter, value, StringComparison.OrdinalIgnoreCase);
        if (ImGui.RadioButton($"{label} ({count})", selected))
            rosterVisibilityFilter = value;
    }

    private void DrawEmptyRosterFilterNotice(DadAccountRosterCatalog catalog)
    {
        if (catalog.Characters.Count == 0)
        {
            DrawMutedNotice("No roster characters available.");
            return;
        }

        var filters = BuildRosterActiveFilterSummary(catalog);
        if (string.IsNullOrWhiteSpace(filters))
        {
            DrawMutedNotice("No roster characters match current filters.");
            return;
        }

        DrawMutedNotice($"No roster characters match current filters: {filters}.");
        if (ImGui.SmallButton("Show all rows"))
            ResetRosterBrowseFilters(catalog, RosterBrowseResetMode.AllRows);
    }

    private void DrawRosterAdvancedFilters(DadAccountRosterCatalog catalog)
    {
        if (ImGui.SmallButton("Browse all accounts"))
        {
            rosterAccountFilter = string.Empty;
            rosterAccountInitialized = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Use local account"))
        {
            rosterAccountInitialized = false;
            EnsureRosterAccountSelection(catalog);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Copy roster JSON"))
        {
            ImGui.SetClipboardText(DadIpcJson.Serialize(catalog));
            plugin.PrintStatus("Copied Dad roster catalog JSON.");
        }

        DrawRosterAssignmentSelector(catalog);
        ImGui.SameLine();
        DrawRosterWorldDcSelector(catalog);
        ImGui.SameLine();
        DrawRosterSourceSelector(catalog);
        ImGui.SameLine();
        DrawRosterClientSelector(catalog);
        ImGui.SameLine();
        ImGui.Checkbox("Stale only", ref rosterStaleOnly);
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear secondary filters"))
        {
            rosterSearch = string.Empty;
            rosterAssignedFilter = string.Empty;
            rosterVisibilityFilter = DadRosterVisibility.Active.ToString();
            rosterWorldDcFilter = string.Empty;
            rosterSourceFilter = string.Empty;
            rosterClientFilter = string.Empty;
            rosterStaleOnly = false;
        }

        DrawRosterAccountTools(catalog);
    }

    private void DrawRosterAccountTools(DadAccountRosterCatalog catalog)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Account tools");
        if (DrawClearAllAccountDataButton("dad-roster-clear-all-account-data"))
        {
            rosterAccountFilter = string.Empty;
            rosterAssignedFilter = string.Empty;
            rosterSelectedRows.Clear();
            rosterAccountInitialized = false;
            DrawDeleteAccountPopup(catalog);
            return;
        }

        var accountOptions = GetRosterAccountToolOptions(catalog);
        if (accountOptions.Count == 0)
        {
            ImGui.TextDisabled("No Dad roster accounts.");
            DrawDeleteAccountPopup(catalog);
            return;
        }

        if (!ImGui.BeginTable("dad-roster-account-tools", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            DrawDeleteAccountPopup(catalog);
            return;
        }

        ImGui.TableSetupColumn("Account key");
        ImGui.TableSetupColumn("Alias");
        ImGui.TableSetupColumn("Config");
        ImGui.TableSetupColumn("Rows");
        ImGui.TableSetupColumn("Characters");
        ImGui.TableSetupColumn("Actions");
        ImGui.TableHeadersRow();

        foreach (var option in accountOptions)
        {
            var accountKey = option.AccountKey;
            var accountId = accountKey.Value?.Trim() ?? string.Empty;
            var account = plugin.ConfigManager.GetAccount(accountKey);
            var rowCount = catalog.Characters
                .Where(character => !character.AccountKey.IsEmpty &&
                                    DadRosterIdentity.SameAccount(character.AccountKey, accountKey))
                .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
                .Count();

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(accountId);
            ImGui.TableNextColumn();
            if (account != null)
            {
                var alias = plugin.GetAccountAliasEditValue(accountKey, account.AccountAlias);
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputText($"##dad-roster-account-alias-{accountId}", ref alias, 96))
                    plugin.QueueDebouncedAccountAliasEdit(accountKey, account.AccountAlias, alias);
            }
            else
            {
                ImGui.TextUnformatted(ResolveRosterAccountDisplayName(option));
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(account == null ? "copy only" : "local");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(rowCount.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            var characterLabels = GetRosterAccountCharacterLabels(catalog, accountKey, account);
            ImGui.TextWrapped(characterLabels.Count == 0 ? "(none)" : string.Join(", ", characterLabels));
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"Show in roster##dad-roster-show-account-{accountId}"))
                ShowAccountInRoster(accountKey);
            ImGui.SameLine();
            if (account != null)
            {
                if (DrawCtrlShiftSmallButton(
                        "Delete",
                        $"dad-roster-delete-account-{accountId}",
                        "Click to delete this local Dad account config and Dad roster metadata. XADB snapshots stay untouched.",
                        "Hold Ctrl+Shift to delete this local Dad account config and Dad roster metadata. XADB snapshots stay untouched."))
                {
                    pendingDeleteAccountId = account.AccountId;
                    ImGui.OpenPopup("Confirm delete account##dad-roster-delete-account");
                }
            }
            else if (DrawCtrlShiftSmallButton(
                         "Forget account copies",
                         $"dad-roster-forget-account-{accountId}",
                         "Click to forget local Dad roster metadata for this remote account. XADB snapshots and remote Dad data stay untouched.",
                         "Hold Ctrl+Shift to forget local Dad roster metadata for this remote account. XADB snapshots and remote Dad data stay untouched."))
            {
                ForgetAccountCopies(accountKey, ResolveRosterAccountDisplayName(option));
            }
        }

        ImGui.EndTable();
        DrawDeleteAccountPopup(catalog);
    }

    private IReadOnlyList<string> GetRosterAccountCharacterLabels(
        DadAccountRosterCatalog catalog,
        DadAccountKey accountKey,
        AccountConfig? account)
        => (account == null ? Enumerable.Empty<string>() : account.Characters.Keys)
            .Concat(catalog.Characters
                .Where(character => DadRosterIdentity.SameAccount(character.AccountKey, accountKey))
                .Select(static character => character.CharacterKey.Value))
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(key => FormatOperatorCharacterKey(key, "(unknown)"))
            .ToList();

    private void ShowAccountInRoster(DadAccountKey accountKey)
    {
        var state = DadRosterBrowseFilterState.ShowAccount(accountKey);
        rosterSearch = state.Search;
        rosterAccountFilter = state.Account;
        rosterAssignedFilter = state.Assigned;
        rosterVisibilityFilter = state.Visibility;
        rosterWorldDcFilter = state.WorldDc;
        rosterSourceFilter = state.Source;
        rosterClientFilter = state.Client;
        rosterStaleOnly = state.StaleOnly;
        rosterSelectedRows.Clear();
        rosterAccountInitialized = true;
    }

    private void ForgetAccountCopies(DadAccountKey accountKey, string label)
    {
        var purged = plugin.ForgetDadAccountCopies(accountKey);
        if (MatchesRosterAccountFilterKey(accountKey.Value, rosterAccountFilter))
        {
            rosterAccountFilter = string.Empty;
            rosterAccountInitialized = true;
        }
        rosterSelectedRows.Clear();
        plugin.PrintStatus(purged
            ? $"Forgot local Dad roster copies for '{label}'. XADB snapshots and remote Dad data untouched."
            : $"No local Dad roster copies found for '{label}'.");
    }

    private List<DadRosterAccountOption> GetRosterAccountToolOptions(DadAccountRosterCatalog catalog)
    {
        var options = GetRosterAccountOptions(catalog);
        foreach (var account in plugin.ConfigManager.GetAllAccounts())
        {
            var accountKey = new DadAccountKey(account.AccountId);
            if (accountKey.IsEmpty ||
                options.Any(option => DadRosterIdentity.SameAccount(option.AccountKey, accountKey)))
            {
                continue;
            }

            options.Add(new DadRosterAccountOption
            {
                AccountKey = accountKey,
                AccountAlias = account.AccountAlias,
                DisplayName = string.IsNullOrWhiteSpace(account.AccountAlias)
                    ? account.AccountId
                    : account.AccountAlias.Trim(),
                IsLocal = true,
                OwnerOnline = true,
                AssignedCharacterCount = account.Characters.Count,
            });
        }

        return options
            .OrderByDescending(static option => option.IsLocal)
            .ThenBy(static option => ResolveRosterAccountDisplayName(option), StringComparer.OrdinalIgnoreCase)
            .ThenBy(static option => option.AccountKey.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool DrawClearAllAccountDataButton(string id)
    {
        var enabled = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
        ImGui.BeginDisabled(!enabled);
        var clicked = ImGui.SmallButton($"Clear all account data##{id}");
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(enabled
                ? "Click to clear Dad account data. XADB snapshots stay untouched."
                : "Hold Ctrl+Shift to enable. Deletes Dad account configs and clears roster/planner account assignments. XADB snapshots stay untouched.");
        }

        if (!clicked)
            return false;

        pendingDeleteAccountId = string.Empty;
        var result = plugin.ClearAllDadAccountData();
        plugin.PrintStatus(result.ToStatusMessage());
        return true;
    }

    private static bool DrawCtrlShiftSmallButton(
        string label,
        string id,
        string enabledTooltip,
        string disabledTooltip)
    {
        var enabled = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
        ImGui.BeginDisabled(!enabled);
        var clicked = ImGui.SmallButton($"{label}##{id}");
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(enabled ? enabledTooltip : disabledTooltip);

        return clicked;
    }

    private void DrawDeleteAccountPopup(DadAccountRosterCatalog catalog)
    {
        if (!ImGui.BeginPopup("Confirm delete account##dad-roster-delete-account"))
            return;

        var account = plugin.ConfigManager.GetAccount(new DadAccountKey(pendingDeleteAccountId));
        if (account == null)
        {
            ImGui.TextUnformatted("No account selected.");
            if (ImGui.SmallButton("Close"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        var rowCount = catalog.Characters
            .Where(character => !character.AccountKey.IsEmpty &&
                                DadRosterIdentity.SameAccount(character.AccountKey, new DadAccountKey(account.AccountId)))
            .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
            .Count();
        ImGui.TextWrapped($"Delete Dad account '{account.AccountAlias}' ({account.AccountId})?");
        ImGui.TextDisabled($"Removes local Dad config plus Dad roster metadata for {rowCount} row(s). XADB snapshots stay untouched.");
        if (DrawCtrlShiftSmallButton(
                "Delete account",
                "dad-roster-confirm-delete-account",
                "Click to delete this local Dad account config and Dad roster metadata. XADB snapshots stay untouched.",
                "Hold Ctrl+Shift to delete this local Dad account config and Dad roster metadata. XADB snapshots stay untouched."))
        {
            if (plugin.DeleteDadAccount(new DadAccountKey(account.AccountId)))
            {
                if (MatchesRosterAccountFilterKey(account.AccountId, rosterAccountFilter))
                {
                    rosterAccountFilter = string.Empty;
                    rosterAccountInitialized = false;
                }

                plugin.PrintStatus($"Deleted Dad account '{account.AccountAlias}' ({account.AccountId}).");
            }

            pendingDeleteAccountId = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Cancel"))
        {
            pendingDeleteAccountId = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawRosterBulkTools(IReadOnlyList<DadRosterCharacter> filtered, IReadOnlyList<DadRosterCharacter> selectedFiltered)
    {
        if (filtered.Count == 0)
            return;

        var allFilteredSelected = filtered.All(character => rosterSelectedRows.Contains(BuildRosterSelectionKey(character)));
        if (ImGui.Checkbox("Select filtered", ref allFilteredSelected))
        {
            foreach (var character in filtered)
            {
                var key = BuildRosterSelectionKey(character);
                if (allFilteredSelected)
                    rosterSelectedRows.Add(key);
                else
                    rosterSelectedRows.Remove(key);
            }
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear selection"))
            rosterSelectedRows.Clear();

        ImGui.BeginDisabled(selectedFiltered.Count == 0);
        if (ImGui.SmallButton("Activate selected"))
            SetRosterVisibility(selectedFiltered, DadRosterVisibility.Active);
        ImGui.SameLine();
        if (ImGui.SmallButton("Hide selected"))
            SetRosterVisibility(selectedFiltered, DadRosterVisibility.Hidden);
        ImGui.SameLine();
        if (ImGui.SmallButton("Ignore selected"))
            SetRosterVisibility(selectedFiltered, DadRosterVisibility.Ignored);
        ImGui.SameLine();
        if (ImGui.SmallButton("Mark update selected"))
            SetRosterVisibility(selectedFiltered, DadRosterVisibility.NeedsUpdate);
        ImGui.SameLine();
        if (ImGui.SmallButton("Queue selected update"))
            QueueRosterUpdate(selectedFiltered, dryRun: false);
        ImGui.SameLine();
        if (ImGui.SmallButton("Dry-run selected update"))
            QueueRosterUpdate(selectedFiltered, dryRun: true);
        ImGui.EndDisabled();
    }

    private void DrawRosterAssignmentSelector(DadAccountRosterCatalog catalog)
    {
        var assigned = catalog.Characters.Count(static character => !character.AccountKey.IsEmpty);
        var unassigned = catalog.Characters.Count - assigned;
        var preview = rosterAssignedFilter switch
        {
            "assigned" => $"Assigned ({assigned})",
            "unassigned" => $"Unassigned ({unassigned})",
            _ => $"Assigned state ({catalog.Characters.Count})",
        };

        ImGui.SetNextItemWidth(MathF.Min(220f, ImGui.GetContentRegionAvail().X));
        if (!ImGui.BeginCombo("Assigned", preview))
            return;

        if (ImGui.Selectable($"Any ({catalog.Characters.Count})", string.IsNullOrWhiteSpace(rosterAssignedFilter)))
            rosterAssignedFilter = string.Empty;
        if (ImGui.Selectable($"Assigned ({assigned})", string.Equals(rosterAssignedFilter, "assigned", StringComparison.OrdinalIgnoreCase)))
            rosterAssignedFilter = "assigned";
        if (ImGui.Selectable($"Unassigned ({unassigned})", string.Equals(rosterAssignedFilter, "unassigned", StringComparison.OrdinalIgnoreCase)))
            rosterAssignedFilter = "unassigned";
        ImGui.EndCombo();
    }

    private void DrawRosterVisibilitySelector(DadAccountRosterCatalog catalog)
    {
        var preview = string.IsNullOrWhiteSpace(rosterVisibilityFilter)
            ? $"Any visibility ({catalog.Characters.Count})"
            : rosterVisibilityFilter;
        ImGui.SetNextItemWidth(MathF.Min(220f, ImGui.GetContentRegionAvail().X));
        if (!ImGui.BeginCombo("Visibility", preview))
            return;

        if (ImGui.Selectable($"Any ({catalog.Characters.Count})", string.IsNullOrWhiteSpace(rosterVisibilityFilter)))
            rosterVisibilityFilter = string.Empty;
        foreach (var visibility in Enum.GetValues<DadRosterVisibility>())
        {
            var value = visibility.ToString();
            var count = visibility == DadRosterVisibility.NeedsUpdate
                ? catalog.Characters.Count(static character => character.NeedsRosterUpdate)
                : catalog.Characters.Count(character => character.Visibility == visibility);
            var selected = string.Equals(rosterVisibilityFilter, value, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable($"{value} ({count})", selected))
                rosterVisibilityFilter = value;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawRosterWorldDcSelector(DadAccountRosterCatalog catalog)
    {
        var options = catalog.Characters
            .SelectMany(static character => new[]
            {
                string.IsNullOrWhiteSpace(character.DataCenterName) ? string.Empty : $"dc:{character.DataCenterName}",
                string.IsNullOrWhiteSpace(character.WorldName) ? string.Empty : $"world:{character.WorldName}",
            })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(rosterWorldDcFilter) &&
            options.All(option => !string.Equals(option, rosterWorldDcFilter, StringComparison.OrdinalIgnoreCase)))
        {
            rosterWorldDcFilter = string.Empty;
        }

        var preview = string.IsNullOrWhiteSpace(rosterWorldDcFilter)
            ? "Any world/DC"
            : FormatRosterWorldDcFilter(rosterWorldDcFilter);
        ImGui.SetNextItemWidth(MathF.Min(220f, ImGui.GetContentRegionAvail().X));
        if (!ImGui.BeginCombo("World/DC", preview))
            return;

        if (ImGui.Selectable("Any world/DC", string.IsNullOrWhiteSpace(rosterWorldDcFilter)))
            rosterWorldDcFilter = string.Empty;
        foreach (var option in options)
        {
            var selected = string.Equals(option, rosterWorldDcFilter, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(FormatRosterWorldDcFilter(option), selected))
                rosterWorldDcFilter = option;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawRosterSourceSelector(DadAccountRosterCatalog catalog)
    {
        var preview = string.IsNullOrWhiteSpace(rosterSourceFilter) ? "Any source" : rosterSourceFilter;
        ImGui.SetNextItemWidth(MathF.Min(200f, ImGui.GetContentRegionAvail().X));
        if (!ImGui.BeginCombo("Source", preview))
            return;

        if (ImGui.Selectable("Any source", string.IsNullOrWhiteSpace(rosterSourceFilter)))
            rosterSourceFilter = string.Empty;
        foreach (var source in Enum.GetValues<DadCharacterSource>())
        {
            var value = source.ToString();
            var count = catalog.Characters.Count(character => character.Source == source);
            var selected = string.Equals(rosterSourceFilter, value, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable($"{value} ({count})", selected))
                rosterSourceFilter = value;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawRosterClientSelector(DadAccountRosterCatalog catalog)
    {
        var options = catalog.Characters
            .Select(static character => character.SourceClientInstanceId)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(rosterClientFilter) &&
            options.All(option => !string.Equals(option, rosterClientFilter, StringComparison.OrdinalIgnoreCase)))
        {
            rosterClientFilter = string.Empty;
        }

        var preview = string.IsNullOrWhiteSpace(rosterClientFilter)
            ? "Any client"
            : FormatRosterClient(rosterClientFilter);
        ImGui.SetNextItemWidth(MathF.Min(200f, ImGui.GetContentRegionAvail().X));
        if (!ImGui.BeginCombo("Client", preview))
            return;

        if (ImGui.Selectable("Any client", string.IsNullOrWhiteSpace(rosterClientFilter)))
            rosterClientFilter = string.Empty;
        foreach (var option in options)
        {
            var selected = string.Equals(option, rosterClientFilter, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(FormatRosterClient(option), selected))
                rosterClientFilter = option;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private IReadOnlyList<DadRosterCharacter> GetCachedFilteredRosterRows(DadRosterUiSnapshot snapshot)
    {
        var key = new RosterFilterCacheKey(
            snapshot.CatalogRevision,
            snapshot.TransportRevision,
            rosterSearch,
            rosterAccountFilter,
            rosterAssignedFilter,
            rosterVisibilityFilter,
            rosterWorldDcFilter,
            rosterSourceFilter,
            rosterClientFilter,
            rosterStaleOnly);
        if (Equals(key, rosterFilterCacheKey))
            return rosterFilteredRows;

        rosterFilterCacheKey = key;
        rosterFilteredRows = FilterRosterCharacters(snapshot.Catalog.Characters).ToList();
        return rosterFilteredRows;
    }

    private IEnumerable<DadRosterCharacter> FilterRosterCharacters(IEnumerable<DadRosterCharacter> characters)
    {
        var search = rosterSearch.Trim();
        var accountFilter = rosterAccountFilter.Trim();
        var assignedFilter = rosterAssignedFilter.Trim();
        var visibilityFilter = rosterVisibilityFilter.Trim();
        var worldDcFilter = rosterWorldDcFilter.Trim();
        var sourceFilter = rosterSourceFilter.Trim();
        var clientFilter = rosterClientFilter.Trim();
        return characters
            .Where(character => !rosterStaleOnly || character.IsStale)
            .Where(character => string.IsNullOrWhiteSpace(accountFilter) ||
                                string.Equals(accountFilter, RosterUnassignedAccountFilter, StringComparison.OrdinalIgnoreCase) && character.AccountKey.IsEmpty ||
                                MatchesRosterAccountFilter(character, accountFilter))
            .Where(character => string.IsNullOrWhiteSpace(assignedFilter) ||
                                string.Equals(assignedFilter, "assigned", StringComparison.OrdinalIgnoreCase) && !character.AccountKey.IsEmpty ||
                                string.Equals(assignedFilter, "unassigned", StringComparison.OrdinalIgnoreCase) && character.AccountKey.IsEmpty)
            .Where(character => string.IsNullOrWhiteSpace(visibilityFilter) ||
                                string.Equals(visibilityFilter, RosterNeedsUpdateFilter, StringComparison.OrdinalIgnoreCase) && character.NeedsRosterUpdate ||
                                string.Equals(character.Visibility.ToString(), visibilityFilter, StringComparison.OrdinalIgnoreCase))
            .Where(character => string.IsNullOrWhiteSpace(worldDcFilter) ||
                                MatchesRosterWorldDcFilter(character, worldDcFilter))
            .Where(character => string.IsNullOrWhiteSpace(sourceFilter) ||
                                string.Equals(character.Source.ToString(), sourceFilter, StringComparison.OrdinalIgnoreCase))
            .Where(character => string.IsNullOrWhiteSpace(clientFilter) ||
                                string.Equals(character.SourceClientInstanceId, clientFilter, StringComparison.OrdinalIgnoreCase))
            .Where(character => string.IsNullOrWhiteSpace(search) ||
                                character.CharacterKey.Value.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                character.CharacterName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                character.ContentId.ToString(CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                character.AccountKey.Value.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                character.AccountAlias.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                character.WorldName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                character.DataCenterName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                FormatRosterBlockers(character).Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static character => character.Visibility)
            .ThenByDescending(static character => character.NeedsRosterUpdate)
            .ThenBy(static character => character.AccountKey.IsEmpty)
            .ThenBy(static character => character.AccountAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static character => character.AccountKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static character => character.CharacterKey.Value, StringComparer.OrdinalIgnoreCase);
    }

    private string BuildRosterPreflightStatus(DadAccountRosterCatalog catalog)
    {
        if (!catalog.IsFullRosterAvailable)
            return $"{DadXadbClient.RosterIpcMissingWarning} Rows {catalog.XadbPayloadRowCount}, roster v{catalog.Version}, contract v{FormatNullableInt(catalog.XadbContractVersion)}.";

        return $"Full XADB roster IPC available. Payload rows {catalog.XadbPayloadRowCount}, roster v{catalog.Version}, contract v{FormatNullableInt(catalog.XadbContractVersion)}.";
    }

    private string BuildRosterActiveFilterSummary(DadAccountRosterCatalog catalog)
    {
        var parts = new List<string>();
        var selectedAccount = GetRosterAccountOptions(catalog)
            .FirstOrDefault(option => MatchesRosterAccountOptionFilter(option, rosterAccountFilter));

        if (string.Equals(rosterAccountFilter, RosterUnassignedAccountFilter, StringComparison.OrdinalIgnoreCase))
            parts.Add("account Unassigned");
        else if (!string.IsNullOrWhiteSpace(rosterAccountFilter))
            parts.Add($"account {(selectedAccount == null ? rosterAccountFilter : FormatRosterAccountOption(selectedAccount))}");
        if (!string.IsNullOrWhiteSpace(rosterVisibilityFilter))
            parts.Add($"visibility {rosterVisibilityFilter}");
        if (!string.IsNullOrWhiteSpace(rosterAssignedFilter))
            parts.Add($"assigned {rosterAssignedFilter}");
        if (!string.IsNullOrWhiteSpace(rosterWorldDcFilter))
            parts.Add($"world/DC {FormatRosterWorldDcFilter(rosterWorldDcFilter)}");
        if (!string.IsNullOrWhiteSpace(rosterSourceFilter))
            parts.Add($"source {rosterSourceFilter}");
        if (!string.IsNullOrWhiteSpace(rosterClientFilter))
            parts.Add($"client {FormatRosterClient(rosterClientFilter)}");
        if (rosterStaleOnly)
            parts.Add("stale only");
        if (!string.IsNullOrWhiteSpace(rosterSearch))
            parts.Add($"search \"{rosterSearch.Trim()}\"");

        return string.Join(", ", parts);
    }

    private static string FormatNullableInt(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "?";

    private static string FormatRosterCountBreakdown(IReadOnlyDictionary<string, int> counts, int limit = 8)
    {
        if (counts.Count == 0)
            return "-";

        var ordered = counts
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var visible = ordered.Take(Math.Max(1, limit))
            .Select(static pair => $"{pair.Key}: {pair.Value}");
        var suffix = ordered.Count > limit ? $", +{ordered.Count - limit}" : string.Empty;
        return string.Join(", ", visible) + suffix;
    }

    private static string BuildRosterAccountFilterKey(DadRosterAccountOption option)
        => option.AccountKey.Value ?? string.Empty;

    private static bool MatchesRosterAccountOptionFilter(DadRosterAccountOption option, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return false;

        var accountKey = NormalizeRosterAccountFilterKey(filter);
        return string.Equals(option.AccountKey.Value, accountKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesRosterAccountFilter(DadRosterCharacter character, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || character.AccountKey.IsEmpty)
            return false;

        var accountKey = NormalizeRosterAccountFilterKey(filter);
        return string.Equals(character.AccountKey.Value, accountKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesRosterAccountFilterKey(string accountKey, string filter)
    {
        if (string.IsNullOrWhiteSpace(accountKey) || string.IsNullOrWhiteSpace(filter))
            return false;

        return string.Equals(accountKey.Trim(), NormalizeRosterAccountFilterKey(filter), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRosterAccountFilterKey(string filter)
    {
        var value = (filter ?? string.Empty).Trim();
        var separatorIndex = value.IndexOf('|', StringComparison.Ordinal);
        return separatorIndex < 0 ? value : value[(separatorIndex + 1)..].Trim();
    }

    private static bool ShouldUseRosterAccountDisplayName(DadRosterAccountOption existing, DadRosterAccountOption candidate)
    {
        var existingName = ResolveRosterAccountDisplayName(existing);
        var candidateName = ResolveRosterAccountDisplayName(candidate);
        if (string.IsNullOrWhiteSpace(candidateName))
            return false;

        var existingIsKey = string.IsNullOrWhiteSpace(existingName) ||
                            string.Equals(existingName, existing.AccountKey.Value, StringComparison.OrdinalIgnoreCase);
        var candidateIsKey = string.Equals(candidateName, candidate.AccountKey.Value, StringComparison.OrdinalIgnoreCase);
        return existingIsKey && !candidateIsKey || candidate.IsLocal && !existing.IsLocal && !candidateIsKey;
    }

    private static string ResolveRosterAccountDisplayName(DadRosterAccountOption option)
    {
        if (!string.IsNullOrWhiteSpace(option.AccountAlias))
            return option.AccountAlias.Trim();

        if (!string.IsNullOrWhiteSpace(option.DisplayName))
            return option.DisplayName.Trim();

        return option.AccountKey.Value ?? string.Empty;
    }

    private static string FormatRosterAccountOption(DadRosterAccountOption option)
    {
        var accountKey = option.AccountKey.Value?.Trim() ?? string.Empty;
        var displayName = ResolveRosterAccountDisplayName(option);
        var onlineSuffix = option.OwnerOnline ? string.Empty : " [offline]";
        if (string.IsNullOrWhiteSpace(displayName))
            return (string.IsNullOrWhiteSpace(accountKey) ? "(account)" : accountKey) + onlineSuffix;

        if (string.IsNullOrWhiteSpace(accountKey) ||
            string.Equals(displayName, accountKey, StringComparison.OrdinalIgnoreCase))
        {
            return displayName + onlineSuffix;
        }

        return $"{displayName} ({accountKey}){onlineSuffix}";
    }

    private static string FormatRosterTargets(IReadOnlyList<DadRosterCharacterRef> targets, string fallback = "-")
    {
        var labels = targets
            .Where(static target => target is { IsEmpty: false })
            .Select(static target =>
            {
                var character = target.CharacterKey.IsEmpty
                    ? target.ContentId == 0 ? "(any)" : $"cid:{target.ContentId}"
                    : target.CharacterKey.Value;
                return target.AccountKey.IsEmpty ? character : $"{target.AccountKey.Value}:{character}";
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return labels.Count == 0 ? fallback : string.Join(", ", labels);
    }

    private static string BuildRosterStatusCounts(DadAccountRosterCatalog catalog)
    {
        var active = catalog.Characters.Count(static character => character.Visibility == DadRosterVisibility.Active);
        var hidden = catalog.Characters.Count(static character => character.Visibility == DadRosterVisibility.Hidden);
        var ignored = catalog.Characters.Count(static character => character.Visibility == DadRosterVisibility.Ignored);
        var needsUpdate = catalog.Characters.Count(static character => character.NeedsRosterUpdate);
        var unassigned = catalog.Characters.Count(static character => character.AccountKey.IsEmpty);
        var stale = catalog.Characters.Count(static character => character.IsStale);
        return $"active {active}, hidden {hidden}, ignored {ignored}, needs-update {needsUpdate}, unassigned {unassigned}, stale {stale}";
    }

    private void SetRosterVisibility(IReadOnlyList<DadRosterCharacter> characters, DadRosterVisibility visibility)
    {
        if (characters.Count == 0)
            return;

        plugin.SetRosterVisibilityFromJson(DadIpcJson.Serialize(new DadRosterVisibilityChangeRequest
        {
            CharacterRefs = characters.Select(DadRosterIdentity.From).ToList(),
            Visibility = visibility,
            Reason = visibility == DadRosterVisibility.NeedsUpdate
                ? "Marked for update from Crew / Scheduler roster."
                : $"Bulk {visibility} from Crew / Scheduler roster.",
        }));
    }

    private void QueueRosterUpdate(IReadOnlyList<DadRosterCharacter> characters, bool dryRun)
    {
        if (characters.Count == 0)
            return;

        var resultJson = plugin.EnqueueRosterUpdateFromJson(DadIpcJson.Serialize(new DadRosterRefreshPlan
        {
            CharacterRefs = characters.Select(DadRosterIdentity.From).ToList(),
            DryRun = dryRun,
            IncludeHidden = true,
            IncludeIgnored = true,
        }));
        var queue = DadIpcJson.Deserialize<DadSchedulerQueueSnapshot>(resultJson);
        plugin.PrintStatus(queue?.Summary ?? "Roster update enqueued.");
    }

    private void DrawRosterRowActions(DadRosterCharacter character, string selectionKey)
    {
        if (ImGui.SmallButton($"Activate##dad-roster-active-{selectionKey}"))
            SetRosterVisibility([character], DadRosterVisibility.Active);
        ImGui.SameLine();
        if (ImGui.SmallButton($"More...##dad-roster-more-{selectionKey}"))
            ImGui.OpenPopup($"dad-roster-more-popup-{selectionKey}");

        if (ImGui.BeginPopup($"dad-roster-more-popup-{selectionKey}"))
        {
            if (ImGui.SmallButton($"Hide##dad-roster-hide-{selectionKey}"))
                SetRosterVisibility([character], DadRosterVisibility.Hidden);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Ignore##dad-roster-ignore-{selectionKey}"))
                SetRosterVisibility([character], DadRosterVisibility.Ignored);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Mark update##dad-roster-update-{selectionKey}"))
                SetRosterVisibility([character], DadRosterVisibility.NeedsUpdate);

            if (ImGui.SmallButton($"Queue update##dad-roster-queue-{selectionKey}"))
                QueueRosterUpdate([character], dryRun: false);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Dry-run update##dad-roster-dry-update-{selectionKey}"))
                QueueRosterUpdate([character], dryRun: true);

            if (plugin.RosterCatalogService.HasLocalRosterCopy(character))
            {
                if (DrawCtrlShiftSmallButton(
                        "Forget copy",
                        $"dad-roster-forget-copy-{selectionKey}",
                        "Click to forget this local Dad roster copy. XADB snapshots and remote Dad data stay untouched.",
                        "Hold Ctrl+Shift to forget this local Dad roster copy. XADB snapshots and remote Dad data stay untouched."))
                {
                    ForgetRosterCopy(character);
                }
            }

            ImGui.EndPopup();
        }
    }

    private void ForgetRosterCopy(DadRosterCharacter character)
    {
        var selectionKey = BuildRosterSelectionKey(character);
        var changed = plugin.RosterCatalogService.ForgetLocalRosterCopy(character);
        plugin.RosterCatalogService.RefreshCatalog(plugin.CharacterIntelligenceService.CurrentPool, new DadRosterRefreshPlan
        {
            IncludeHidden = true,
            IncludeIgnored = true,
            StaleAfterHours = plugin.Configuration.RosterCatalog.StaleAfterHours,
        });
        rosterSelectedRows.Remove(selectionKey);

        var label = FormatRosterAccount(character);
        plugin.PrintStatus(changed
            ? $"Forgot local Dad roster copy for {FormatOperatorCharacterKey(character.CharacterKey.Value, "(unknown)")} on {label}. XADB snapshots and remote Dad data untouched."
            : $"No local Dad roster copy found for {FormatOperatorCharacterKey(character.CharacterKey.Value, "(unknown)")} on {label}.");
    }

    private void TrimRosterSelection(IEnumerable<DadRosterCharacter> characters)
    {
        var currentKeys = characters
            .Select(BuildRosterSelectionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        rosterSelectedRows.RemoveWhere(key => !currentKeys.Contains(key));
    }

    private bool IsRemoteRosterRow(DadRosterCharacter character)
        => !string.IsNullOrWhiteSpace(character.SourceClientInstanceId) &&
           !string.Equals(character.SourceClientInstanceId, plugin.PresenceService.ClientInstanceId, StringComparison.OrdinalIgnoreCase);

    private static string BuildRosterSelectionKey(DadRosterCharacter character)
        => DadRosterIdentity.BuildKey(character);

    private static bool MatchesRosterWorldDcFilter(DadRosterCharacter character, string filter)
    {
        if (filter.StartsWith("dc:", StringComparison.OrdinalIgnoreCase))
            return string.Equals(character.DataCenterName, filter[3..], StringComparison.OrdinalIgnoreCase);

        if (filter.StartsWith("world:", StringComparison.OrdinalIgnoreCase))
            return string.Equals(character.WorldName, filter[6..], StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static string FormatRosterWorldDcFilter(string filter)
    {
        if (filter.StartsWith("dc:", StringComparison.OrdinalIgnoreCase))
            return $"DC: {filter[3..]}";

        if (filter.StartsWith("world:", StringComparison.OrdinalIgnoreCase))
            return $"World: {filter[6..]}";

        return filter;
    }

    private string FormatRosterClient(string clientInstanceId)
    {
        if (string.IsNullOrWhiteSpace(clientInstanceId))
            return "(unknown)";

        if (string.Equals(clientInstanceId, plugin.PresenceService.ClientInstanceId, StringComparison.OrdinalIgnoreCase))
            return "This Dad";

        return clientInstanceId.Length <= 8 ? clientInstanceId : clientInstanceId[..8];
    }

    private void EnqueueSelectedPreset(
        DadSchedulerJobType jobType,
        DadMapCrewJobMode mapMode)
    {
        var selectedGroup = plugin.GetSelectedPlannerGroup();
        if (selectedGroup == null)
            return;

        var request = new DadScheduledPresetRequest
        {
            GroupId = selectedGroup.GroupId,
            DryRun = false,
            RequestedBy = "crew-ui",
            Priority = selectedGroup.SchedulePriority,
            Enabled = true,
            CadenceHours = selectedGroup.ScheduleCadenceHours,
            NextEligibleTimeUtc = selectedGroup.NextEligibleTimeUtc,
            JobType = jobType,
            MapMode = mapMode,
            MapRunTemplate = selectedGroup.MapRunTemplate,
        };
        var queueJson = plugin.EnqueueScheduledPresetFromJson(DadIpcJson.Serialize(request));
        var queue = DadIpcJson.Deserialize<DadSchedulerQueueSnapshot>(queueJson);
        plugin.PrintStatus(queue?.Summary ?? $"Queued preset '{selectedGroup.DisplayName}'.");
    }

    private static string FormatRosterAccount(DadRosterCharacter character)
    {
        if (character.AccountKey.IsEmpty)
            return "Unassigned";

        if (string.IsNullOrWhiteSpace(character.AccountAlias) ||
            string.Equals(character.AccountAlias, character.AccountKey.Value, StringComparison.OrdinalIgnoreCase))
        {
            return character.AccountKey.Value;
        }

        return $"{character.AccountAlias} ({FormatText(character.AccountKey.Value, "?")})";
    }

    private static string FormatRosterFreshness(DadRosterCharacter character)
    {
        if (character.LastRuntimeSeenUtc.HasValue)
            return FormatRelativeAge(character.LastRuntimeSeenUtc);

        if (character.LastSnapshotUtc.HasValue)
            return character.IsStale ? $"stale {FormatRelativeAge(character.LastSnapshotUtc)}" : FormatRelativeAge(character.LastSnapshotUtc);

        return "unknown";
    }

    private static string FormatRosterWorldDc(DadRosterCharacter character)
    {
        var world = FormatText(character.WorldName, "-");
        return string.IsNullOrWhiteSpace(character.DataCenterName)
            ? world
            : $"{world} / {character.DataCenterName}";
    }

    private static string FormatRosterState(DadRosterCharacter character)
        => character.NeedsRosterUpdate
            ? $"{character.Visibility} | needs update"
            : character.Visibility.ToString();

    private string FormatRosterSource(DadRosterCharacter character)
    {
        var source = plugin.PresetProviderService.GetCharacterSourceLabel(character.Source);
        if (string.IsNullOrWhiteSpace(character.SourceClientInstanceId))
            return source;

        return $"{source} | {FormatRosterClient(character.SourceClientInstanceId)}";
    }

    private static string FormatRosterBlockers(DadRosterCharacter character)
    {
        var blockers = character.Blockers
            .Concat(character.Warnings)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return blockers.Count == 0 ? string.Empty : string.Join(" | ", blockers);
    }

    private static string FormatRosterMap(DadRosterCharacter character)
    {
        if (!string.IsNullOrWhiteSpace(character.MapEligibilitySummary))
            return character.MapEligibilitySummary;

        return character.MapEligible switch
        {
            true => "eligible",
            false => "blocked",
            _ => "unknown",
        };
    }

    private void DrawPresetPlannerTab(DadCharacterPool characterPool, DadVisibleRunState runState)
    {
        DadUi.Heading("PLAN", "Choose a saved preset, configure the run, resolve blockers, then start through the scheduler-backed path.");
        var plannerOptions = plugin.PlannerOptions;
        var plannerSnapshot = plugin.GetPlannerUiSnapshot(runState);
        var requestPreview = plannerSnapshot.RequestPreview;
        var plannerPreview = requestPreview.PlannerPreview;
        var crewTools = plugin.BuildCrewToolsSnapshot(plannerSnapshot);
        var plannerLocked = IsPlannerLocked(runState) ||
                            crewTools.Formation.IsActive ||
                            crewTools.StandaloneDisbandActive;
        var selectedGroup = plugin.GetSelectedPlannerGroup();
        var levelingEnabled = selectedGroup?.LevelingMode?.Enabled == true;

        if (DadUi.BeginCard("dad-plan-crew-tools-card"))
        {
            DadUi.Heading(
                "CREW TOOLS",
                "Prepare the selected preset through the normal scheduler gates, then form or deliberately disband without queueing.");
            DrawStatusRow("Selected preset", crewTools.SelectedPresetName);
            DrawStatusRow(
                "Resolved mode",
                $"{FormatCrewFormationMode(crewTools.ResolvedMode)} | effective {crewTools.ResolvedPresetName}");
            DrawStatusRow("Live state", crewTools.LiveState);
            DrawStatusRow(
                "First blocker",
                string.IsNullOrWhiteSpace(crewTools.FirstBlocker)
                    ? "(none)"
                    : crewTools.FirstBlocker);

            ImGui.BeginDisabled(!crewTools.CanCreateGroup);
            if (DadUi.Button("Create group", DadUiTone.Accent))
                plugin.StartCrewFormationFromPlanner();
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!crewTools.CanDisband);
            if (DadUi.Button("Disband", DadUiTone.Danger))
                plugin.RequestCrewToolsDisband();
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) &&
                !crewTools.CanDisband)
            {
                ImGui.SetTooltip(crewTools.Formation.IsActive
                    ? "Disband becomes available only for the exact regular Crew Formation run held at GroupReady."
                    : crewTools.DisbandSummary);
            }
            DadUi.EndCard();
        }

        if (DadUi.Button("Open Batch Preset Wizard", DadUiTone.Accent))
            plugin.TogglePresetBatchWizardUi();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Build a non-mutating rotating-account/anchor preview, then append generated Plans and Schedules atomically.");

        if (plannerLocked)
            DrawMutedNotice("Planner locked. Dad run active. Cancel or wait for final state before editing plan.");

        if (DadUi.BeginCard("dad-plan-review-card"))
        {
            DadUi.Heading("REVIEW & RUN", "Check the first blocker, validate, then deliberately start or cancel.");
            DrawPlannerActionStrip(requestPreview, plannerSnapshot.SchedulerPreview, runState, plannerLocked, plannerSnapshot.Generation);
            DadUi.EndCard();
        }

        ImGui.BeginDisabled(plannerLocked);
        if (DadUi.BeginCard("dad-plan-identity-card"))
        {
            DrawPlannerGroupIdentityControls(plannerSnapshot, plannerPreview, plannerLocked);
            DadUi.EndCard();
        }

        DrawSectionHeader("Activity and content", "Choose the activity, submode, duty, and required modifiers.");
        var activityFieldsShareRow = ImGui.GetContentRegionAvail().X >= ImGui.GetFontSize() * 36f;
        ImGui.BeginDisabled(levelingEnabled);
        DrawPlannerRunFamilySelector(plannerOptions);
        if (activityFieldsShareRow)
            ImGui.SameLine();
        DrawPlannerSubmodeSelector(plannerOptions, plannerPreview);
        ImGui.EndDisabled();
        if (levelingEnabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Disable Leveling Mode before changing Run family or Submode.");
        ImGui.BeginDisabled(levelingEnabled);
        DrawPlannerLaneInputs(plannerOptions, plannerPreview.LaneDefinition, plannerSnapshot.SelectedDuty, debugUi: false);
        ImGui.EndDisabled();
        if (levelingEnabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Leveling Mode selects duty from its ordered threshold table. The saved fixed duty and sync settings are preserved.");
        DrawLevelingModeControls(plannerSnapshot, selectedGroup);

        DrawSectionHeader("Crew", "Every primary and substitute stays on one full-width row.");
        DrawPlannerGroupCrewControls(plannerSnapshot, plannerPreview, debugUi: false);

        var useRuleColumns = ImGui.GetContentRegionAvail().X >= ImGui.GetFontSize() * 66f;
        if (ImGui.BeginTable(
                "dad-plan-rule-cards",
                useRuleColumns ? 2 : 1,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableNextColumn();
            if (DadUi.BeginCard("dad-plan-stop-card"))
            {
                DadUi.Heading("STOP", "Bound the run with one explicit stopping rule.");
                ImGui.BeginDisabled(levelingEnabled);
                DrawPlannerStopPolicyControls(plannerOptions, plannerPreview, requestPreview);
                ImGui.EndDisabled();
                if (levelingEnabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Leveling Mode owns the plan goal and runs one frozen child at a time. The saved ordinary stop policy is preserved.");
                DadUi.EndCard();
            }

            ImGui.TableNextColumn();
            if (DadUi.BeginCard("dad-plan-finish-card"))
            {
                DadUi.Heading("FINISH", "Choose the safe actions taken after successful completion.");
                DrawPlannerCompletionActionsControls(plannerOptions, requestPreview);
                DadUi.EndCard();
            }
            ImGui.EndTable();
        }

        if (selectedGroup != null && DadUi.Button("Save activity and rules to selected preset", DadUiTone.Accent))
        {
            var saved = plugin.SaveCurrentPlannerGroup(
                string.IsNullOrWhiteSpace(plannerGroupNameBuffer) ? selectedGroup.DisplayName : plannerGroupNameBuffer,
                out _,
                out var rejectionReason);
            plugin.PrintStatus(saved == null
                ? rejectionReason
                : $"Saved activity and rules to preset '{saved.DisplayName}'.");
        }
        if (selectedGroup != null && ImGui.IsItemHovered())
            ImGui.SetTooltip("Crew rows save through their inline controls. This saves the activity, duty, stop rule, and finish rule selected above.");
        ImGui.EndDisabled();
    }

    private static string FormatCrewFormationMode(DadCrewFormationMode mode)
        => mode switch
        {
            DadCrewFormationMode.RegularParty => "Regular party",
            DadCrewFormationMode.AlliancePartyFinder => "Alliance PF Create → Grab",
            _ => "Unavailable",
        };

    private void DrawPlannerLanePanel(
        DadPlannerUiSnapshot plannerSnapshot,
        DadPresetPlannerOptions plannerOptions,
        DadVisibleRunState runState,
        bool debugUi)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(1f, 1f, 1f, 0.03f));
        if (!ImGui.BeginChild("dad-planner-lane-rail", new Vector2(0f, 0f), true))
        {
            ImGui.EndChild();
            ImGui.PopStyleColor();
            return;
        }

        ImGui.TextUnformatted("Run families");
        if (debugUi)
            ImGui.TextDisabled("Family cards; submode selected in plan.");
        ImGui.Separator();

        foreach (var family in plugin.PresetProviderService.GetPlannerRunFamilies())
        {
            var lane = ResolveFamilyPreviewLane(plannerOptions, family);
            var lanePreview = plannerSnapshot.GetLanePreview(lane.ActivityMode);
            if (lanePreview == null)
                continue;

            var laneCard = BuildPlannerLaneCard(lanePreview, runState);
            var accent = ParseHexColor(lane.AccentColorHex, laneCard.IsSelected ? 0.95f : 0.62f);
            var hovered = ParseHexColor(lane.AccentColorHex, 0.82f);
            var active = ParseHexColor(lane.AccentColorHex, 1f);
            var familyLabel = plugin.PresetProviderService.GetPlannerRunFamilyLabel(family);
            ImGui.PushStyleColor(ImGuiCol.Button, accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
            if (ImGui.Button($"{familyLabel}##dad-family-{family}", new Vector2(-1f, debugUi ? 54f : 38f)))
                SelectPlannerFamily(plannerOptions, family);
            ImGui.PopStyleColor(3);

            if (debugUi)
            {
                DrawCompactStatusRow("Submode", lane.DisplayName);
                DrawCompactStatusRow("Maturity", laneCard.MaturityLabel);
                DrawCompactStatusRow("Startability", laneCard.StartabilityLabel);
                DrawCompactStatusRow("Party", laneCard.PartySizeLabel);
                DrawCompactStatusRow("Blockers", BuildShortBlockerSummary(laneCard.FirstBlockerLabel, laneCard.BlockerCount));
                DrawCompactStatusRow("Runtime", laneCard.RuntimeLabel);
            }
            else
            {
                ImGui.TextDisabled(lane.DisplayName);
                ImGui.SameLine();
                ImGui.TextDisabled(laneCard.MaturityLabel);
                ImGui.SameLine();
                ImGui.TextColored(GetStartabilityColor(laneCard.StartabilityLabel, laneCard.BlockerCount), laneCard.StartabilityLabel);
                DrawCompactStatusRow("Blockers", laneCard.BlockerCount.ToString(CultureInfo.InvariantCulture));
                DrawCompactStatusRow("Runtime", BuildRuntimeBadge(laneCard.RuntimeLabel));
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawPlannerConfigSection(
        DadPlannerUiSnapshot plannerSnapshot,
        DadPresetPlannerOptions plannerOptions,
        DadActivityPreset plannerPreview,
        DadPlannerRunRequestPreview requestPreview,
        bool plannerLocked,
        bool debugUi)
    {
        DrawSectionHeader("1. Select, create, and name the preset", "Choose the saved identity before configuring what it will do.");
        ImGui.BeginDisabled(plannerLocked);
        var selectedGroup = plugin.GetSelectedPlannerGroup();
        var levelingEnabled = selectedGroup?.LevelingMode?.Enabled == true;
        DrawPlannerGroupIdentityControls(plannerSnapshot, plannerPreview, plannerLocked);

        DrawSectionHeader("2. Choose activity, submode, and duty", "These inputs select the runtime contract and compatible content.");
        ImGui.Spacing();
        ImGui.BeginDisabled(levelingEnabled);
        DrawPlannerRunFamilySelector(plannerOptions);
        ImGui.SameLine();
        DrawPlannerSubmodeSelector(plannerOptions, plannerPreview);
        ImGui.EndDisabled();
        if (levelingEnabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Disable Leveling Mode before changing Run family or Submode.");
        ImGui.Spacing();
        DrawPlannerQueueAuthoritySelector(plannerOptions);
        ImGui.Spacing();
        if (debugUi)
        {
            DrawPlannerOperatorModeSelector(plannerOptions);
            ImGui.SameLine();
            DrawPlannerAccountFilterSelector(plannerSnapshot.AccountOptions, plannerOptions, plannerPreview.AccountFilterSummary);

            DrawPlannerTransportOwnerSelector(plannerOptions);

            var connectedOnly = plannerOptions.ConnectedOnly;
            if (ImGui.Checkbox("Connected only", ref connectedOnly))
            {
                plannerOptions.ConnectedOnly = connectedOnly;
                plugin.SavePlannerOptions();
            }

            ImGui.SameLine();
            var sameDatacenterOnly = plannerOptions.SameDatacenterOnly;
            if (ImGui.Checkbox("Same datacenter", ref sameDatacenterOnly))
            {
                plannerOptions.SameDatacenterOnly = sameDatacenterOnly;
                plugin.SavePlannerOptions();
            }

            ImGui.SameLine();
            var allowStale = plannerOptions.AllowStaleForPlanning;
            if (ImGui.Checkbox("Allow stale for planning", ref allowStale))
            {
                plannerOptions.AllowStaleForPlanning = allowStale;
                plugin.SavePlannerOptions();
            }

            ImGui.Spacing();
        }

        ImGui.BeginDisabled(levelingEnabled);
        DrawPlannerLaneInputs(plannerOptions, plannerPreview.LaneDefinition, plannerSnapshot.SelectedDuty, debugUi);
        ImGui.EndDisabled();
        DrawLevelingModeControls(plannerSnapshot, selectedGroup);

        DrawSectionHeader("3. Assign the crew", "Every primary and substitute character stays on one inline row with all operational fields visible.");
        DrawPlannerGroupCrewControls(plannerSnapshot, plannerPreview, debugUi);

        DrawSectionHeader("4. Configure stop and finish rules", "Bound the run, then choose the safe actions taken after successful completion.");
        ImGui.Spacing();
        ImGui.BeginDisabled(levelingEnabled);
        DrawPlannerStopPolicyControls(plannerOptions, plannerPreview, requestPreview);
        ImGui.EndDisabled();
        ImGui.Spacing();
        DrawPlannerCompletionActionsControls(plannerOptions, requestPreview);
        if (selectedGroup != null)
        {
            ImGui.Spacing();
            if (DadUi.Button("Save activity and rules to selected preset", DadUiTone.Accent))
            {
                var saved = plugin.SaveCurrentPlannerGroup(
                    string.IsNullOrWhiteSpace(plannerGroupNameBuffer) ? selectedGroup.DisplayName : plannerGroupNameBuffer,
                    out _,
                    out var rejectionReason);
                plugin.PrintStatus(saved == null
                    ? rejectionReason
                    : $"Saved activity and rules to preset '{saved.DisplayName}'.");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Crew rows save through their inline controls. This saves the activity, duty, stop rule, and finish rule selected above.");
        }
        ImGui.EndDisabled();
    }

    private void DrawPlannerLaneSummarySection(
        DadActivityPreset plannerPreview,
        DadPlannerRunRequestPreview requestPreview,
        DadVisibleRunState runState,
        bool debugUi)
    {
        DrawSectionHeader("Summary", "Selected lane summary, validation state, and current runtime phase.");
        var laneRun = ResolveLaneRuntime(runState, plannerPreview.LaneDefinition);
        var activeRun = GetActiveRun(runState);
        DrawStatusRow("Run family", $"{plannerPreview.RunFamilyId} | {plannerPreview.LaneDefinition.MaturityLabel}");
        DrawStatusRow("Submode", plannerPreview.LaneDefinition.DisplayName);
        if (debugUi)
            DrawStatusRow("Lane summary", plannerPreview.LaneDefinition.Summary);
        DrawStatusRow("Preset", plannerPreview.UsingPlannerGroup ? plannerPreview.SelectedPlannerGroupName : "Auto roster");
        DrawStatusRow("Stop condition", plannerPreview.StopPolicy.Describe());
        DrawStatusRow("Validation", $"{FormatReadiness(plannerPreview.ValidationState)} | {plannerPreview.ValidationSummary}");
        if (debugUi)
            DrawStatusRow("Summary", FormatOperatorText(plannerPreview.PlannerSummary, "(none)"));

        if (debugUi)
        {
            DrawStatusRow("Next action", plannerPreview.LaneDefinition.NextAction);
            DrawStatusRow("Operator mode", plannerPreview.OperatorModeLabel);
            DrawStatusRow("Transport", plugin.PresetProviderService.GetTransportOwnerLabel(plannerPreview.TransportOwner));
            DrawStatusRow("Queue authority", plugin.PresetProviderService.GetQueueAuthorityLabel(plannerPreview.QueueAuthority));
            DrawStatusRow("Inviter", plugin.PresetProviderService.GetInviteAuthorityLabel(plannerPreview.InviteAuthority));
            DrawStatusRow("Account filter", FormatOperatorText(plannerPreview.AccountFilterSummary, "(none)"));
            DrawStatusRow("Roster source", plugin.PresetProviderService.GetRosterSourceLabel(plannerPreview.RosterSource));
            DrawStatusRow("Leader", FormatOperatorText(plannerPreview.LeaderStatusText, "(none)"));
            DrawStatusRow("Preview scope", plannerPreview.PreviewScope);
            DrawStatusRow("Filters", plannerPreview.FilterSummary);
        }

        if (laneRun.Status != DadRunStatus.Idle)
        {
            DrawStatusRow("Runtime phase", DadOperatorPhaseText.FormatPhaseLabel(laneRun));
            DrawStatusRow("Runtime status", laneRun.Summary);
        }
        else if (activeRun.Status != DadRunStatus.Idle)
        {
            DrawStatusRow("Live lane", $"{activeRun.ModuleId} | {DadOperatorPhaseText.FormatPhaseLabel(activeRun)} | {activeRun.Summary}");
        }
        else
        {
            DrawStatusRow("Runtime phase", "No live runtime for selected lane.");
        }

        if (debugUi)
            DrawStatusRow("Local-only mode", plugin.Configuration.LocalOnlyModeEnabled ? "Enabled" : "Disabled");
        if (debugUi)
            DrawStatusRow("Planner request", requestPreview.StatusSummary);
    }

    private void DrawPlannerActionStrip(
        DadPlannerRunRequestPreview requestPreview,
        DadSchedulerPreview schedulerPreview,
        DadVisibleRunState runState,
        bool plannerLocked,
        long snapshotGeneration)
    {
        var blockers = BuildPlannerBlockerList(requestPreview);
        var dependencyBlocker = schedulerPreview.Slots
            .Where(static slot => !slot.DependenciesReady)
            .Select(static slot => slot.DependencySummary)
            .FirstOrDefault(static summary => !string.IsNullOrWhiteSpace(summary));
        var firstBlocker = dependencyBlocker ?? blockers.FirstOrDefault() ??
                           (schedulerPreview.CanStart ? string.Empty : schedulerPreview.BlockedReason);
        var selectedGroup = plugin.GetSelectedPlannerGroup();
        var queue = plugin.SchedulerService.GetQueueSnapshot();
        var cancellationCleanupJob = selectedGroup == null
            ? null
            : plugin.SchedulerService.GetPendingTakeoverCleanupJob(selectedGroup.GroupId);
        var existingSchedulerJob = selectedGroup == null
            ? null
            : queue.ActiveJob is { } activeJob &&
              string.Equals(activeJob.GroupId, selectedGroup.GroupId, StringComparison.OrdinalIgnoreCase)
                ? activeJob
                : queue.PendingJobs.FirstOrDefault(job =>
                    string.Equals(job.GroupId, selectedGroup.GroupId, StringComparison.OrdinalIgnoreCase))
                  ?? cancellationCleanupJob;
        var cancellationCleanupPending = cancellationCleanupJob != null &&
                                         existingSchedulerJob != null &&
                                         string.Equals(
                                             cancellationCleanupJob.JobId,
                                             existingSchedulerJob.JobId,
                                             StringComparison.OrdinalIgnoreCase);
        var runEnabled = selectedGroup != null && schedulerPreview.CanStart && existingSchedulerJob == null;
        var runButtonWidth = -1f;

        DrawStatusRow("Readiness", !string.IsNullOrWhiteSpace(dependencyBlocker)
            ? "Waiting for required plugins"
            : schedulerPreview.CanStart
            ? schedulerPreview.ReadyToStart ? "Ready now" : "Ready to wake and prepare the saved crew"
            : "Blocked");
        DrawStatusRow("First blocker", FormatText(firstBlocker, "None"));

        ImGui.BeginDisabled(selectedGroup == null);
        string? justValidated = null;
        if (ImGui.SmallButton("Recheck readiness (does not run)"))
            justValidated = plugin.ValidateSelectedPlannerPresetReadOnly();
        var validateHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        ImGui.EndDisabled();
        if (validateHovered && selectedGroup == null)
            ImGui.SetTooltip("Select a saved preset before validating it.");
        var feedback = selectedGroup == null
            ? null
            : plugin.GetPlannerValidationFeedback(snapshotGeneration, selectedGroup.GroupId);
        var feedbackText = justValidated ?? feedback?.Summary;
        ImGui.SameLine();

        ImGui.BeginDisabled(!runEnabled);
        if (ImGui.Button("Run preset — wake, relog, group, start", new Vector2(runButtonWidth, 0f)))
            EnqueueSelectedPreset(DadSchedulerJobType.ScheduledPreset, DadMapCrewJobMode.ManualMapReady);
        var runPresetHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        ImGui.EndDisabled();
        if (runPresetHovered)
        {
            var runTooltip = selectedGroup == null
                ? "Select a saved preset before running it."
                : existingSchedulerJob != null
                    ? $"This preset already has an active or pending scheduler job. Phase {(cancellationCleanupPending ? "Cancellation cleanup" : ResolveSchedulerJobPhase(existingSchedulerJob, queue))}; Job ID {existingSchedulerJob.JobId}."
                    : schedulerPreview.CanStart
                        ? schedulerPreview.StatusSummary
                        : schedulerPreview.BlockedReason;
            ImGui.SetTooltip(FormatText(runTooltip, "Scheduler preview is blocked."));
        }
        if (!string.IsNullOrWhiteSpace(feedbackText))
            ImGui.TextWrapped(feedbackText);
        if (existingSchedulerJob != null)
        {
            var phase = cancellationCleanupPending
                ? "Cancellation cleanup"
                : ResolveSchedulerJobPhase(existingSchedulerJob, queue);
            if (!cancellationCleanupPending)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel scheduler job##planner-existing-job"))
                {
                    var responseJson = plugin.CancelScheduledJobFromJson(DadIpcJson.Serialize(new DadCancelScheduledJobRequest
                    {
                        JobId = existingSchedulerJob.JobId,
                        Reason = $"Operator cancelled preset '{selectedGroup!.DisplayName}' from the planner.",
                    }));
                    var response = DadIpcJson.Deserialize<DadSchedulerQueueSnapshot>(responseJson);
                    plugin.PrintStatus(response?.Summary ?? $"Cancelled scheduler Job ID {existingSchedulerJob.JobId}.");
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Cancel phase {phase}, Job ID {existingSchedulerJob.JobId}. Temporary Dad-owned takeover state will be released without starting party or queue work.");
            }
            DrawStatusRow("Existing scheduler job", $"{phase} | Job ID {existingSchedulerJob.JobId}");
        }

        if (plannerLocked)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel active run##planner-action-strip"))
                plugin.CancelActiveRunFromShell();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Cancels the active Dad run visible to this client.");
        }

        if (ImGui.SmallButton("Open Status"))
            NavigateToStatus(DadStatusWindowTab.Readiness);

        if (plugin.Configuration.AdvancedModeEnabled && ImGui.TreeNode("Advanced / specialized actions"))
        {
            ImGui.BeginDisabled(plannerLocked || !requestPreview.CanStart);
            if (ImGui.SmallButton("Start planner run (online participants only)"))
                plugin.StartPlannerRunFromShell();
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.BeginDisabled(selectedGroup == null);
            if (ImGui.SmallButton("Prepare map crew"))
                EnqueueSelectedPreset(DadSchedulerJobType.MapCrew, selectedGroup?.MapMode ?? DadMapCrewJobMode.ManualMapReady);
            ImGui.EndDisabled();
            ImGui.TreePop();
        }

    }

    private static string ResolveSchedulerJobPhase(
        DadScheduledCrewJob job,
        DadSchedulerQueueSnapshot queue)
        => queue.ActiveJob != null &&
           string.Equals(queue.ActiveJob.JobId, job.JobId, StringComparison.OrdinalIgnoreCase)
            ? queue.ActiveState.Phase.ToString()
            : "Pending";

    private string FormatSchedulerSlotStage(DadSchedulerSlotState slot, DadSchedulerPresetState state)
    {
        var stage = slot.TakeoverStatus == DadWakeTakeoverStatus.Blocked || !string.IsNullOrWhiteSpace(slot.BlockedReason)
            ? "Blocked"
            : slot.Ready
                ? "Ready"
                : slot.TakeoverStage switch
                {
                    DadWakeTakeoverStage.WaitingForClient or DadWakeTakeoverStage.None => slot.ClientConnected
                        ? "Client connected — character offline/relogging"
                        : "Client missing",
                    DadWakeTakeoverStage.WaitingForPostArReady or DadWakeTakeoverStage.WaitingForAutoRetainer or DadWakeTakeoverStage.AwaitingArHook => "Waiting for AutoRetainer character postprocess",
                    DadWakeTakeoverStage.PostprocessOwned or DadWakeTakeoverStage.Prepared =>
                        slot.VermaxionReservationState == DadVermaxionReservationState.Unavailable &&
                        string.Equals(slot.ExternalAutomationActivity, "CompatibilityHandoff", StringComparison.OrdinalIgnoreCase)
                            ? "Compatibility handoff: VERMAXION idle / AR idle"
                            : "AR handoff acquired — waiting for crew",
                    DadWakeTakeoverStage.ResetCommitted => "Coordinated reset scheduled",
                    DadWakeTakeoverStage.ResetVerified => "Reset verified — waiting for crew",
                    DadWakeTakeoverStage.RelogCommitted => "Coordinated relog scheduled",
                    DadWakeTakeoverStage.WaitingForExternalAutomation => FormatVermaxionWaitLabel(slot),
                    DadWakeTakeoverStage.DisablingMultiMode or DadWakeTakeoverStage.ResetIssued or DadWakeTakeoverStage.VerifyingTakeover => "Resetting",
                    DadWakeTakeoverStage.RelogIssued or DadWakeTakeoverStage.WaitingForCharacter => "Relog issued",
                    DadWakeTakeoverStage.Blocked => "Blocked",
                    _ => slot.TakeoverStage.ToString(),
                };
        var remaining = DadWakeStageTimeoutPolicy.GetRemaining(
            slot,
            DateTime.UtcNow,
            plugin.Configuration.VermaxionHoldTimeoutSeconds,
            plugin.Configuration.AutoRetainerBusyTimeoutSeconds,
            plugin.Configuration.ParticipantReadyTimeoutSeconds);
        var timeout = stage is "Ready" or "Blocked"
            ? string.Empty
            : slot.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline
                ? $" | elapsed {FormatSchedulerElapsed(DateTime.UtcNow - state.StartedAtUtc)} | no timeout; cancel to stop"
                : $" | timeout {Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds))}s";
        var summary = FormatText(slot.BlockedReason, slot.Summary);
        return string.IsNullOrWhiteSpace(summary)
            ? $"{stage}{timeout}"
            : $"{stage}{timeout} | {summary}";
    }

    private static string FormatSchedulerElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private static string FormatVermaxionWaitLabel(DadSchedulerSlotState slot)
    {
        var detail = string.Join(
            "/",
            new[] { slot.ExternalAutomationActivity, slot.ExternalAutomationState }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(detail)
            ? "Waiting for VERMAXION status"
            : $"Waiting for VERMAXION — {detail}";
    }

    private void DrawPlannerRequestContractSection(
        DadActivityPreset plannerPreview,
        DadPlannerRunRequestPreview requestPreview)
    {
        DrawSectionHeader("Request Contract", "Typed preview contract is primary operator truth. Raw start contract stays secondary/debug.");
        DrawStatusRow("Preview lane", FormatText(requestPreview.ContractPreview.Lane, plannerPreview.LaneDefinition.DisplayName));
        DrawStatusRow("Authority mode", DadStatusText.FormatAuthorityMode(requestPreview.ContractPreview.AuthorityMode));
        DrawStatusRow("Request id", FormatText(requestPreview.ContractPreview.RequestId, requestPreview.RequestId));
        DrawStatusRow("Request module", requestPreview.ContractPreview.ModuleId.ToString());
        DrawStatusRow("Task config", FormatOperatorText(FormatPlannerTaskConfig(requestPreview.ContractPreview.TaskConfig), "(none)"));
        DrawStatusRow("Stop policy", requestPreview.ContractPreview.StopPolicy.Describe());
        DrawStatusRow("Required characters", plugin.KrangleService.FormatCharacterKeys(requestPreview.ContractPreview.RequiredCharacterKeys));
        DrawStatusRow("Required accounts", FormatOperatorAccountKeys(requestPreview.ContractPreview.RequiredAccountKeys));
        DrawStatusRow("Request queue", plugin.PresetProviderService.GetQueueAuthorityLabel(requestPreview.ContractPreview.QueueAuthority));
        DrawStatusRow("Expected party size", requestPreview.ContractPreview.PartySize <= 0 ? "?" : requestPreview.ContractPreview.PartySize.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Startability", FormatText(requestPreview.ContractPreview.Startability, requestPreview.CanStart ? "Startable" : "Blocked"));
        DrawStatusRow("Scheduler", requestPreview.ContractPreview.CanSchedule ? "Schedulable" : "Blocked");
        DrawStatusRow("Readiness", FormatText(requestPreview.ContractPreview.ReadinessSummary, "(none)"));

        var contractPreviewJson = string.IsNullOrWhiteSpace(requestPreview.ContractPreviewJson)
            ? requestPreview.StatusSummary
            : requestPreview.ContractPreviewJson;
        ImGui.InputTextMultiline("Preview JSON (typed contract)", ref contractPreviewJson, 16384, new Vector2(-1f, 220f), ImGuiInputTextFlags.ReadOnly);

        if (ImGui.TreeNode("Raw request JSON (secondary/debug)"))
        {
            var requestJson = string.IsNullOrWhiteSpace(requestPreview.RequestJson)
                ? requestPreview.StatusSummary
                : requestPreview.RequestJson;
            ImGui.InputTextMultiline("Request JSON (raw start contract)", ref requestJson, 8192, new Vector2(-1f, 160f), ImGuiInputTextFlags.ReadOnly);
            ImGui.TreePop();
        }
    }

    private void DrawPlannerValidationSection(DadActivityPreset plannerPreview, DadPlannerRunRequestPreview requestPreview)
    {
        DrawSectionHeader("Validation And Blockers", "Planner blockers first, module blockers second, filter exclusions after.");
        DrawStatusRow("Static blockers", requestPreview.StaticBlockers.Count == 0
            ? "(none)"
            : FormatOperatorText(string.Join(" | ", requestPreview.StaticBlockers), "(none)"));
        DrawStatusRow("Live readiness", requestPreview.ReadinessBlockers.Count == 0
            ? "(none)"
            : FormatOperatorText(string.Join(" | ", requestPreview.ReadinessBlockers), "(none)"));
        DrawStatusRow("Scheduler blockers", requestPreview.ScheduleBlockers.Count == 0
            ? "(none)"
            : FormatOperatorText(string.Join(" | ", requestPreview.ScheduleBlockers), "(none)"));
        DrawStatusRow("Preview blockers", requestPreview.ContractPreview.Blockers.Count == 0
            ? "(none)"
            : FormatOperatorText(string.Join(" | ", requestPreview.ContractPreview.Blockers), "(none)"));
        DrawPlannerValidation(plannerPreview, requestPreview);
        if (plugin.Configuration.DebugUiEnabled)
        {
            ImGui.Spacing();
            DrawPlannerFilterCounts(plannerPreview);
        }
    }

    private void DrawPlannerRosterSummarySection(DadActivityPreset plannerPreview, DadVisibleRunState runState, bool debugUi)
    {
        DrawSectionHeader("Planned Roster", "Compact selected-slot and candidate summary. Full tables are in details.");
        var totalSlots = plannerPreview.SelectedCharacters.Count;
        var assignedSlots = plannerPreview.SelectedCharacters.Count(static slot => !string.IsNullOrWhiteSpace(slot.CharacterKey));
        var blockedSlots = plannerPreview.SelectedCharacters.Count(static slot => !string.IsNullOrWhiteSpace(slot.BlockerSummary));
        var readySlots = plannerPreview.SelectedCharacters.Count(static slot => slot.SelectedReadiness == DadReadinessState.Ready);
        var laneRun = ResolveLaneRuntime(runState, plannerPreview.LaneDefinition);

        DrawStatusRow("Slots", $"{assignedSlots}/{Math.Max(1, totalSlots)} assigned | {readySlots} ready | {blockedSlots} with blockers");
        DrawStatusRow("Candidates", debugUi
            ? $"{plannerPreview.AvailableCharacters.Count} available | {plannerPreview.FilterSummary}"
            : $"{plannerPreview.AvailableCharacters.Count} available");
        DrawStatusRow("Leader", FormatOperatorText(plannerPreview.LeaderStatusText, "(none)"));
        DrawStatusRow("Runtime participants", laneRun.Status == DadRunStatus.Idle
            ? "No live participant snapshot for selected lane."
            : $"{laneRun.Participants.Count} participant(s) | {DadOperatorPhaseText.FormatPhaseLabel(laneRun)}");

        var firstBlockedSlot = plannerPreview.SelectedCharacters
            .FirstOrDefault(static slot => !string.IsNullOrWhiteSpace(slot.BlockerSummary));
        if (firstBlockedSlot != null)
            DrawStatusRow("First roster blocker", $"{firstBlockedSlot.SlotId}: {firstBlockedSlot.BlockerSummary}");
    }

    private void DrawPlannerDetailsSection(
        DadPlannerUiSnapshot plannerSnapshot,
        DadPresetPlannerOptions plannerOptions,
        DadActivityPreset plannerPreview,
        DadPlannerRunRequestPreview requestPreview,
        DadVisibleRunState runState,
        bool plannerLocked)
    {
        DrawSectionHeader("Details", "Collapsed validation, JSON, runtime, roster tables, and debug actions.");

        if (ImGui.TreeNode("Planner cache"))
        {
            var cacheStats = plugin.GetPlannerUiCacheStats();
            DrawStatusRow("Generation", cacheStats.Generation.ToString(CultureInfo.InvariantCulture));
            DrawStatusRow("Preview cache", $"{cacheStats.HitCount} hit / {cacheStats.MissCount} miss");
            DrawStatusRow("Scheduler cache", $"{cacheStats.SchedulerHitCount} hit / {cacheStats.SchedulerMissCount} miss");
            DrawStatusRow("Last rebuild", $"{cacheStats.LastRebuildMilliseconds:F2} ms | {cacheStats.LastRebuildReason}");
            DrawStatusRow("Max rebuild", $"{cacheStats.MaxRebuildMilliseconds:F2} ms");
            DrawStatusRow("Snapshot", $"{plannerSnapshot.RebuiltAtUtc:O} | generation {plannerSnapshot.Generation}");
            ImGui.TreePop();
        }

        if (ImGui.TreeNode("Validation, blockers, and filter counts"))
        {
            DrawPlannerValidationSection(plannerPreview, requestPreview);
            ImGui.TreePop();
        }

        if (ImGui.TreeNode("Request contract and JSON"))
        {
            DrawPlannerRequestContractSection(plannerPreview, requestPreview);
            ImGui.TreePop();
        }

        if (ImGui.TreeNode("Runtime timeline and executor detail"))
        {
            DrawPlannerExecutionTimelineSection(runState, plannerPreview, requestPreview);
            ImGui.TreePop();
        }

        if (ImGui.TreeNode("Full roster and available characters"))
        {
            DrawPlannerRosterSection(plannerPreview, runState);
            ImGui.TreePop();
        }

        if (ImGui.TreeNode("Export, test loaders, and raw duty fallback"))
        {
            DrawPlannerControlsSection(plannerOptions, requestPreview, plannerLocked);
            DrawPlannerDutyDebugFallback(plannerOptions, plannerPreview.LaneDefinition, plannerLocked);
            ImGui.TreePop();
        }
    }

    private void DrawPlannerRosterSection(DadActivityPreset plannerPreview, DadVisibleRunState runState)
    {
        DrawSectionHeader("Roster / Participants", "Planned slots first. Runtime participants appear only when this lane is live.");
        var laneRun = ResolveLaneRuntime(runState, plannerPreview.LaneDefinition);
        if (laneRun.Status == DadRunStatus.Idle || laneRun.Participants.Count == 0)
        {
            DrawMutedNotice("No runtime participant snapshot for selected lane.");
        }
        else if (ImGui.BeginTable("dad-planner-runtime-participants", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Owner");
            ImGui.TableSetupColumn("Account");
            ImGui.TableSetupColumn("Character");
            ImGui.TableSetupColumn("Slot");
            ImGui.TableSetupColumn("State");
            ImGui.TableSetupColumn("Claim / lease");
            ImGui.TableSetupColumn("Status");
            ImGui.TableHeadersRow();

            foreach (var participant in laneRun.Participants.OrderBy(static participant => participant.AssignedSlotId, StringComparer.OrdinalIgnoreCase))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(DadStatusText.FormatParticipantOwner(participant));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorAccountLabel(participant.ManagedAccountAlias, participant.ManagedAccountKey.ToString()));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorCharacterKey(participant.ActiveCharacterKey.ToString(), "(unknown)"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatText(participant.AssignedSlotId, "(unassigned)"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(participant.State.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{participant.ClaimState} / {participant.LeaseState}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorText(FormatParticipantStatus(participant), "(none)"));
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        DrawPlannerRosterSlots(plannerPreview);
        ImGui.Spacing();
        DrawPlannerAvailableCharacters(plannerPreview);
    }

    private void DrawPlannerExecutionTimelineSection(
        DadVisibleRunState runState,
        DadActivityPreset plannerPreview,
        DadPlannerRunRequestPreview requestPreview)
    {
        DrawSectionHeader("Execution Timeline", "Real executor state first. Explicit placeholders mark deferred runtime surfaces.");
        var laneRun = ResolveLaneRuntime(runState, plannerPreview.LaneDefinition);
        var activeRun = GetActiveRun(runState);
        var capability = plugin.ModuleRegistry.GetCapability(requestPreview.ModuleId);

        if (laneRun.Status != DadRunStatus.Idle)
        {
            DrawStatusRow("Operator phase", DadOperatorPhaseText.FormatPhaseLabel(laneRun));
            DrawStatusRow("Run status", $"{laneRun.Status} / {laneRun.Phase} / {laneRun.ModuleId}");
            DrawStatusRow("Summary", laneRun.Summary);
            DrawStatusRow("Stop progress", FormatText(laneRun.StopProgress.Summary, laneRun.Request?.StopPolicy.Describe() ?? "(none)"));
            DrawStatusRow("Executor", FormatExecutorStatus(laneRun.CurrentExecutorStatus));
            DrawStatusRow("Active task", string.IsNullOrWhiteSpace(laneRun.ActiveTaskName) ? "(none)" : $"{laneRun.ActiveTaskIndex}/{Math.Max(1, laneRun.TotalTaskCount)} {laneRun.ActiveTaskName}");
            DrawStatusRow("Task detail", FormatText(laneRun.ActiveTaskStatus, laneRun.Summary));
            DrawLocalDutyRuntimeRows(laneRun);
            DrawDutySupportRuntimeRows(laneRun);

            if (laneRun.StepResults.Count == 0)
            {
                DrawMutedNotice("No step results recorded yet for this active lane.");
            }
            else if (ImGui.BeginTable("dad-planner-step-results", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Time");
                ImGui.TableSetupColumn("Step");
                ImGui.TableSetupColumn("State");
                ImGui.TableSetupColumn("Participant");
                ImGui.TableSetupColumn("Summary");
                ImGui.TableHeadersRow();

                foreach (var step in laneRun.StepResults.OrderBy(static step => step.ReportedAtUtc))
                {
                    var stepState = step.Success
                        ? "Success"
                        : step.Deferred
                            ? "Deferred"
                            : step.TimedOut
                                ? "Timed out"
                                : "Failed";

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatTime(step.ReportedAtUtc));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatText(step.StepName, "(none)"));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(stepState);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(step.ParticipantState.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatOperatorText(FormatText(step.Summary, step.BlockedReason), "(none)"));
                }

                ImGui.EndTable();
            }
        }
        else if (activeRun.Status != DadRunStatus.Idle)
        {
            DrawMutedNotice($"Selected lane has no live runtime state. Current active lane: {activeRun.ModuleId} | {DadOperatorPhaseText.FormatPhaseLabel(activeRun)}");
        }
        else
        {
            DrawMutedNotice("No live execution for selected lane yet.");
        }

        if (!capability.CanExecuteLiveQueue)
            DrawPlaceholderNotice($"Placeholder: live executor phase timeline is not implemented for {plannerPreview.LaneDefinition.DisplayName} yet.");
        if (requestPreview.ModuleId == DadModuleId.DutySupport)
        {
            DrawStatusRow("Retry loop", capability.CanRequeue
                ? "Enabled"
                : "Single-run only. Requeue/retry loop is not enabled.");
        }
        else if (!capability.CanStartQueue || !capability.CanRequeue)
        {
            DrawPlaceholderNotice("Placeholder: queue/retry detail is deferred until this lane emits runtime queue telemetry.");
        }
        if (!capability.CanTrackCompletion)
            DrawPlaceholderNotice("Placeholder: per-lane completion/result widget waits on backend completion projection.");
    }

    private void DrawPlannerControlsSection(
        DadPresetPlannerOptions plannerOptions,
        DadPlannerRunRequestPreview requestPreview,
        bool plannerLocked)
    {
        DrawSectionHeader("Export And Test Controls", "Developer/operator diagnostics only. Start stays in the action strip.");
        if (ImGui.SmallButton("Planner to chat"))
            plugin.PrintStatus(requestPreview.PlannerPreview.PlannerSummary);

        ImGui.SameLine();
        if (ImGui.SmallButton("Copy planner summary"))
        {
            ImGui.SetClipboardText(requestPreview.PlannerPreview.PlannerSummary);
            plugin.PrintStatus("Copied dad planner summary.");
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(requestPreview.ContractPreviewJson));
        if (ImGui.SmallButton("Copy preview JSON"))
        {
            ImGui.SetClipboardText(requestPreview.ContractPreviewJson);
            plugin.PrintStatus("Copied dad planner preview contract JSON.");
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(requestPreview.Request == null);
        if (ImGui.SmallButton("Copy request JSON"))
        {
            ImGui.SetClipboardText(requestPreview.RequestJson);
            plugin.PrintStatus("Copied dad planner request JSON.");
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.SmallButton("Write issue report"))
            plugin.GenerateIssueReport();

        DrawStatusRow("Issue report", plugin.LastIssueReportStatus);
        if (!string.IsNullOrWhiteSpace(plugin.LastIssueReportPath))
        {
            if (ImGui.SmallButton("Copy report path"))
            {
                ImGui.SetClipboardText(plugin.LastIssueReportPath);
                plugin.PrintStatus("Copied dad issue report path.");
            }
        }

        ImGui.BeginDisabled(plannerLocked);
        if (ImGui.SmallButton("Load Local Sastasha test"))
            LoadPlannerTestDuty(plannerOptions, DadPlannerActivityMode.LocalDuty);
        ImGui.SameLine();
        if (ImGui.SmallButton("Load Duty Support Sastasha test"))
            LoadPlannerDutySupportTest(plannerOptions);
        ImGui.EndDisabled();
    }

    private PlannerLaneCardView BuildPlannerLaneCard(
        DadPlannerLanePreviewSnapshot lanePreview,
        DadVisibleRunState runState)
    {
        var lane = lanePreview.Lane;
        var laneRequestPreview = lanePreview.RequestPreview;
        var blockers = laneRequestPreview.ContractPreview.Blockers;
        var laneRun = ResolveLaneRuntime(runState, lane);
        var startabilityLabel = FormatText(laneRequestPreview.ContractPreview.Startability, laneRequestPreview.CanStart ? "Startable" : "Blocked");
        var expectedPartySize = laneRequestPreview.ContractPreview.PartySize;
        var firstBlocker = blockers.FirstOrDefault(static blocker => !string.IsNullOrWhiteSpace(blocker)) ?? string.Empty;

        return new PlannerLaneCardView(
            lane,
            lanePreview.IsSelected,
            lane.MaturityLabel,
            expectedPartySize <= 0 ? "?" : expectedPartySize.ToString(CultureInfo.InvariantCulture),
            startabilityLabel,
            firstBlocker,
            blockers.Count,
            laneRun.Status == DadRunStatus.Idle
                ? "Idle"
                : $"{DadOperatorPhaseText.FormatPhaseLabel(laneRun)} | {laneRun.Status} / {laneRun.Phase}");
    }

    private static DadPresetPlannerOptions ClonePlannerOptionsForLane(
        DadPresetPlannerOptions source,
        DadPlannerLaneDefinition lane)
        => new()
        {
            PresetName = source.PresetName,
            RunFamily = lane.RunFamily,
            ActivityMode = lane.ActivityMode,
            ActivityName = lane.DisplayName,
            OperatorMode = source.OperatorMode,
            ConnectedOnly = source.ConnectedOnly,
            SameDatacenterOnly = source.SameDatacenterOnly,
            AllowStaleForPlanning = source.AllowStaleForPlanning,
            TransportOwner = lane.DefaultTransportOwner,
            QueueAuthority = lane.DefaultQueueAuthority,
            InviteAuthority = source.InviteAuthority,
            DutyContentFinderConditionId = source.DutyContentFinderConditionId,
            DutyDisplayName = source.DutyDisplayName,
            DutyUnsynced = source.DutyUnsynced,
            DutyExpectedPartySize = source.DutyExpectedPartySize,
            RouletteTarget = source.RouletteTarget?.Clone() ?? new DadQueueTarget { Kind = DadQueueTargetKind.Roulette },
            MogtomePreset = source.MogtomePreset,
            MogtomeDutyPolicy = source.MogtomeDutyPolicy,
            RefreshTrustNpcLevels = source.RefreshTrustNpcLevels,
            StopPolicy = source.StopPolicy.Clone(),
            CompletionActions = source.CompletionActions?.Clone(),
            IncludedAccountKeys = [..source.IncludedAccountKeys],
        };

    private static DadRunResult GetActiveRun(DadVisibleRunState runState)
        => runState.VisibleRun.Status != DadRunStatus.Idle
            ? runState.VisibleRun
            : runState.AuthorityRun.Status != DadRunStatus.Idle
                ? runState.AuthorityRun
                : runState.LocalRun;

    private DadRunResult ResolveLaneRuntime(DadVisibleRunState runState, DadPlannerLaneDefinition lane)
    {
        var candidates = new[] { runState.VisibleRun, runState.AuthorityRun, runState.LocalRun };
        return candidates.FirstOrDefault(candidate => IsRuntimeMatchForPlannerLane(candidate, lane))
               ?? DadRunResult.Idle();
    }

    private static bool IsRuntimeMatchForPlannerLane(DadRunResult run, DadPlannerLaneDefinition lane)
    {
        if (run.Status == DadRunStatus.Idle)
            return false;

        return lane.ActivityMode switch
        {
            DadPlannerActivityMode.Msq => run.ModuleId == DadModuleId.Msq,
            DadPlannerActivityMode.DailyRoulette => run.ModuleId == DadModuleId.DailyMsq,
            DadPlannerActivityMode.DutySupport => run.ModuleId == DadModuleId.DutySupport,
            DadPlannerActivityMode.Trust => run.ModuleId == DadModuleId.Trust,
            DadPlannerActivityMode.PremadeDuty => run.ModuleId == DadModuleId.PremadeDuty,
            DadPlannerActivityMode.Blunderville => run.ModuleId == DadModuleId.Blunderville,
            DadPlannerActivityMode.Mogtome => run.ModuleId == DadModuleId.Mogtome,
            DadPlannerActivityMode.Commendation => run.ModuleId == DadModuleId.Commendation,
            DadPlannerActivityMode.Astrope => run.ModuleId == DadModuleId.Astrope,
            DadPlannerActivityMode.LocalDuty => run.ModuleId == DadModuleId.Duty,
            DadPlannerActivityMode.CustomDuty => run.ModuleId == DadModuleId.CustomDuty,
            _ => run.ModuleId == lane.ModuleId,
        };
    }

    private string BuildOverviewNextAction(
        DadVisibleRunState runState,
        CharacterConfig profile,
        DadRunResult? displayRun = null)
    {
        var activeRun = displayRun ?? GetActiveRun(runState);
        if (!plugin.Configuration.PluginEnabled)
            return "Enable Dad plugin before using planner or runtime authority.";

        if (!profile.Enabled)
            return "Arm current profile before starting Dad work.";

        if (!profile.AllowIpcStarts)
            return "Enable Allow Dad starts before launching planner-driven runs.";

        if (activeRun.Status != DadRunStatus.Idle)
        {
            if (DadOperatorPhaseText.HasBlockingFailure(activeRun))
            {
                var blocker = !string.IsNullOrWhiteSpace(activeRun.BlockedReason)
                    ? activeRun.BlockedReason
                    : !string.IsNullOrWhiteSpace(activeRun.FailureReason)
                        ? activeRun.FailureReason
                        : activeRun.Summary;
                return $"Resolve blocker: {blocker}";
            }

            if (activeRun.Status is DadRunStatus.Completed or DadRunStatus.Cancelled)
                return "Review final summary, then return to Preset Planner for the next lane.";

            return activeRun.Phase switch
            {
                DadRunPhase.Planning or DadRunPhase.RoutingModules => "Keep planner locked while Dad validates and routes the selected request.",
                DadRunPhase.DiscoveringParticipants or DadRunPhase.WaitingForReadiness or DadRunPhase.ClaimingSlots or DadRunPhase.AssemblingParty
                    => "Wait for participants and slot claims to settle, or cancel stale workers.",
                DadRunPhase.QueuePreparing or DadRunPhase.QueueStarting or DadRunPhase.WaitingForQueuePop
                    => "Observe queue state and authority ownership; cancel only if queue truth diverges.",
                DadRunPhase.InDutyOrTask when activeRun.ModuleId == DadModuleId.Blunderville
                    => "Monitor task progress. Runtime authority owns the active Blunderville task.",
                DadRunPhase.InDutyOrTask
                    => "Monitor duty progress. Runtime authority owns in-duty execution now.",
                _ => "Wait for Dad to finish the current phase or cancel if operator intent changed.",
            };
        }

        if (plugin.Configuration.LocalOnlyModeEnabled)
            return "Switch off local-only mode before testing remote-party lanes.";

        if (runState.AuthorityView.Kind == DadAuthorityViewKind.NoRemoteAuthority)
            return "Discover or configure Dad Coordinator authority before starting remote lanes.";

        return "Pick a planner lane, verify typed roster coverage, then start from Preset Planner.";
    }

    private void DrawDutySupportRuntimeSection(DadRunResult run)
    {
        if (HasLocalDutyRuntime(run))
        {
            DrawSectionHeader("Local Duty Runtime", "Live regular Duty Finder queue, entry, in-duty, exit, and stabilization truth from current executor state.");
            DrawLocalDutyRuntimeRows(run);
        }

        if (!HasDutySupportRuntime(run))
            return;

        var label = ResolveNpcDutyLabel(run);
        DrawSectionHeader($"{label} Runtime", "Live NPC duty queue, entry, in-duty, leave, and stabilization truth from current executor state.");
        DrawDutySupportRuntimeRows(run);
    }

    private void DrawLocalDutyRuntimeRows(DadRunResult run)
    {
        if (!HasLocalDutyRuntime(run))
            return;

        var status = ResolveLocalDutyExecutorStatus(run);
        var summary = ResolveDutySupportSummary(run, status);
        DrawStatusRow("Local Duty", $"{DadOperatorPhaseText.FormatPhaseLabel(run)} | {status.Status} / {status.Phase}");
        DrawStatusRow("Path", FormatOperatorText(DetectDutySupportPath(summary), "(none)"));
        DrawStatusRow("Queue / entry", FormatOperatorText(BuildLocalDutyQueueEntryText(status, summary), "(none)"));
        DrawStatusRow("Duty observation", FormatOperatorText(BuildLocalDutyObservationText(status, summary), "(none)"));
        DrawStatusRow("Leave / exit", FormatOperatorText(BuildDutySupportLeaveText(run, status, summary), "(none)"));
        DrawStatusRow("Stabilize", FormatOperatorText(BuildDutySupportStabilizeText(run, status, summary), "(none)"));
        DrawStatusRow("Current summary", FormatOperatorText(summary, "(none)"));
    }

    private void DrawDutySupportRuntimeRows(DadRunResult run)
    {
        if (!HasDutySupportRuntime(run))
            return;

        var status = ResolveDutySupportExecutorStatus(run);
        var summary = ResolveDutySupportSummary(run, status);
        var label = ResolveNpcDutyLabel(run, status);
        DrawStatusRow(label, $"{DadOperatorPhaseText.FormatPhaseLabel(run)} | {status.Status} / {status.Phase}");
        DrawStatusRow("Path", FormatOperatorText(DetectDutySupportPath(summary), "(none)"));
        DrawStatusRow("Queue / entry", FormatOperatorText(BuildDutySupportQueueEntryText(status, summary), "(none)"));
        DrawStatusRow("Entry automation", FormatOperatorText(BuildDutySupportEntryAutomationText(status, summary), "(none)"));
        DrawStatusRow("Leave / exit", FormatOperatorText(BuildDutySupportLeaveText(run, status, summary), "(none)"));
        DrawStatusRow("Stabilize", FormatOperatorText(BuildDutySupportStabilizeText(run, status, summary), "(none)"));
        DrawStatusRow("Current summary", FormatOperatorText(summary, "(none)"));
    }

    private static bool HasDutySupportRuntime(DadRunResult run)
        => run.Status != DadRunStatus.Idle &&
           (run.ModuleId == DadModuleId.DutySupport ||
            run.ModuleId == DadModuleId.Trust ||
            run.CurrentExecutorStatus.ModuleId == DadModuleId.DutySupport ||
            run.CurrentExecutorStatus.ModuleId == DadModuleId.Trust ||
            run.StepResults.Any(static step => step.ModuleId is DadModuleId.DutySupport or DadModuleId.Trust));

    private static bool HasLocalDutyRuntime(DadRunResult run)
        => run.Status != DadRunStatus.Idle &&
           (run.ModuleId == DadModuleId.Duty ||
            run.CurrentExecutorStatus.ModuleId == DadModuleId.Duty ||
            run.StepResults.Any(static step => step.ModuleId == DadModuleId.Duty));

    private static DadModuleExecutionStatusDto ResolveLocalDutyExecutorStatus(DadRunResult run)
    {
        if (run.CurrentExecutorStatus.ModuleId == DadModuleId.Duty)
            return run.CurrentExecutorStatus;

        return run.StepResults
            .Where(static step => step.ModuleId == DadModuleId.Duty)
            .OrderByDescending(static step => step.ReportedAtUtc)
            .Select(static step => step.ExecutorStatus)
            .FirstOrDefault()
            ?? run.CurrentExecutorStatus;
    }

    private static DadModuleExecutionStatusDto ResolveDutySupportExecutorStatus(DadRunResult run)
    {
        if (run.CurrentExecutorStatus.ModuleId is DadModuleId.DutySupport or DadModuleId.Trust)
            return run.CurrentExecutorStatus;

        return run.StepResults
            .Where(static step => step.ModuleId is DadModuleId.DutySupport or DadModuleId.Trust)
            .OrderByDescending(static step => step.ReportedAtUtc)
            .Select(static step => step.ExecutorStatus)
            .FirstOrDefault()
            ?? run.CurrentExecutorStatus;
    }

    private static string ResolveDutySupportSummary(DadRunResult run, DadModuleExecutionStatusDto status)
        => string.IsNullOrWhiteSpace(status.Summary)
            ? FormatText(string.IsNullOrWhiteSpace(run.ActiveTaskStatus) ? run.Summary : run.ActiveTaskStatus, "(none)")
            : status.Summary;

    private static string DetectDutySupportPath(string summary)
    {
        if (ContainsAny(summary, "Force Commands mode", "ADS"))
            return "ADS force commands";

        if (ContainsAny(summary, "Use FrenRider mode", "FrenRider"))
            return "FrenRider (after-entry /fr on)";

        if (ContainsAny(summary, "Do Nothing mode", "user owns combat", "user-owned"))
            return "User-owned";

        return "Path pending.";
    }

    private static string ResolveNpcDutyLabel(DadRunResult run)
        => ResolveNpcDutyLabel(run, run.CurrentExecutorStatus);

    private static string ResolveNpcDutyLabel(DadRunResult run, DadModuleExecutionStatusDto status)
    {
        var moduleId = status.ModuleId != DadModuleId.None
            ? status.ModuleId
            : run.ModuleId;
        if (moduleId == DadModuleId.Trust ||
            run.StepResults.Any(static step => step.ModuleId == DadModuleId.Trust))
        {
            return "Trust";
        }

        return "Duty Support";
    }

    private static string BuildDutySupportQueueEntryText(DadModuleExecutionStatusDto status, string summary)
    {
        if (status.Status == DadRunStatus.Failed || !string.IsNullOrWhiteSpace(status.FailureReason))
            return $"Blocked: {FormatText(status.FailureReason, FormatText(status.BlockedReason, summary))}";

        return status.Phase switch
        {
            DadRunPhase.QueuePreparing or DadRunPhase.QueueStarting or DadRunPhase.WaitingForQueuePop => $"Queueing: {summary}",
            DadRunPhase.InDutyOrTask => "Queue complete. Duty entry confirmed.",
            DadRunPhase.PostRunStabilizing or DadRunPhase.Finalizing => "Queue complete. Duty entry and exit confirmed.",
            _ => "No live queue state.",
        };
    }

    private static string BuildLocalDutyQueueEntryText(DadModuleExecutionStatusDto status, string summary)
    {
        if (status.Status == DadRunStatus.Failed || !string.IsNullOrWhiteSpace(status.FailureReason))
            return $"Blocked: {FormatText(status.FailureReason, FormatText(status.BlockedReason, summary))}";

        return status.Phase switch
        {
            DadRunPhase.QueuePreparing or DadRunPhase.QueueStarting or DadRunPhase.WaitingForQueuePop => $"Queueing: {summary}",
            DadRunPhase.InDutyOrTask => "Regular Duty Finder queue complete. Duty entry confirmed.",
            DadRunPhase.PostRunStabilizing or DadRunPhase.Finalizing => "Regular Duty Finder entry and exit confirmed.",
            _ => "No live queue state.",
        };
    }

    private static string BuildLocalDutyObservationText(DadModuleExecutionStatusDto status, string summary)
        => status.Phase switch
        {
            DadRunPhase.InDutyOrTask => summary,
            DadRunPhase.PostRunStabilizing or DadRunPhase.Finalizing => "Duty observation complete.",
            DadRunPhase.QueuePreparing or DadRunPhase.QueueStarting or DadRunPhase.WaitingForQueuePop => "Pending duty entry.",
            _ => "Not started.",
        };

    private static string BuildDutySupportEntryAutomationText(DadModuleExecutionStatusDto status, string summary)
    {
        var entryAutomation = ExtractSentence(summary,
            "sent no Duty Support entry command",
            "sent no Trust entry command",
            "sent /bmrai on and /rotation auto after Duty Support entry",
            "sent /fr on after duty entry",
            "could not send /fr on after duty entry",
            "failed to send /fr on after duty entry",
            "sent no FrenRider, ADS, or rotation command after duty entry",
            "sent no FrenRider, ADS, or rotation command after Trust entry",
            "attempted rotation bootstrap");
        if (!string.IsNullOrWhiteSpace(entryAutomation))
            return entryAutomation;

        return status.Phase switch
        {
            DadRunPhase.QueuePreparing or DadRunPhase.QueueStarting or DadRunPhase.WaitingForQueuePop => "Pending duty entry.",
            DadRunPhase.InDutyOrTask or DadRunPhase.PostRunStabilizing or DadRunPhase.Finalizing => "Duty entered.",
            _ => "Not started.",
        };
    }

    private static string BuildDutySupportLeaveText(DadRunResult run, DadModuleExecutionStatusDto status, string summary)
    {
        if (ContainsAny(summary,
                "leave blocked",
                "leave requested",
                "waiting for FrenRider or user to leave",
                "waiting for user-owned duty exit",
                "waiting for duty exit"))
        {
            return summary;
        }

        return status.Phase switch
        {
            DadRunPhase.InDutyOrTask => "Waiting for DutyCompleted or duty exit.",
            DadRunPhase.PostRunStabilizing => "Duty exit confirmed.",
            DadRunPhase.Finalizing when run.Status == DadRunStatus.Completed => "Duty exit confirmed.",
            DadRunPhase.Finalizing when run.Status == DadRunStatus.Cancelled => "Cancelled before final duty exit confirmation.",
            _ => "Not started.",
        };
    }

    private static string BuildDutySupportStabilizeText(DadRunResult run, DadModuleExecutionStatusDto status, string summary)
        => status.Phase == DadRunPhase.PostRunStabilizing
            ? summary
            : run.Status switch
            {
                DadRunStatus.Completed => "Done. Duty exit and post-run stabilization completed.",
                DadRunStatus.Cancelled => "Cancelled.",
                DadRunStatus.Failed or DadRunStatus.PartialFailure or DadRunStatus.TimedOut => run.Summary,
                _ => "Not started.",
            };

    private static string ExtractSentence(string value, params string[] markers)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sentences = value.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return sentences.FirstOrDefault(sentence => markers.Any(marker => sentence.Contains(marker, StringComparison.OrdinalIgnoreCase)))
               ?? string.Empty;
    }

    private static bool ContainsAny(string value, params string[] markers)
        => markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private DadAcquiredCharacter? ResolveParticipantCharacter(DadCharacterPool characterPool, DadParticipantSnapshot participant)
    {
        if (!string.IsNullOrWhiteSpace(participant.Character.CharacterKey) || participant.Character.ContentId != 0)
            return participant.Character;

        var activeCharacterKey = participant.ActiveCharacterKey.ToString();
        return characterPool.Characters.FirstOrDefault(character =>
            (!string.IsNullOrWhiteSpace(activeCharacterKey) &&
             string.Equals(character.CharacterKey, activeCharacterKey, StringComparison.OrdinalIgnoreCase))
            || (participant.Character.ContentId != 0 && character.ContentId == participant.Character.ContentId));
    }

    private static bool IsParticipantStale(DadParticipantSnapshot participant)
        => DateTime.UtcNow - participant.LastHeartbeatUtc > TimeSpan.FromSeconds(30);

    private string FormatParticipantFreshness(DadParticipantSnapshot participant, DadAcquiredCharacter? character)
    {
        if (character != null && (!string.IsNullOrWhiteSpace(character.CharacterKey) || character.ContentId != 0))
            return FormatFreshness(character);

        var age = DateTime.UtcNow - participant.LastHeartbeatUtc;
        if (age <= TimeSpan.FromSeconds(10))
            return "live";
        if (age <= TimeSpan.FromSeconds(30))
            return "recent";

        return "stale";
    }

    private string FormatParticipantStatus(DadParticipantSnapshot participant)
    {
        var parts = new List<string> { participant.State.ToString() };
        if (!string.IsNullOrWhiteSpace(participant.StatusText))
            parts.Add(participant.StatusText);
        if (participant.AvailableCharacterKeys.Count > 0)
            parts.Add($"avail {plugin.KrangleService.FormatCharacterKeys(participant.AvailableCharacterKeys)}");
        if (!participant.IsLocalClient && !participant.WorkerSessionId.IsEmpty)
            parts.Add("Dad Coordinator hub");
        return string.Join(" | ", parts);
    }

    private static bool HasHardBlocker(IReadOnlyList<DadModuleBlockerDto> blockers)
        => blockers.Any(blocker => blocker.Severity is DadModuleBlockerSeverity.Blocked or DadModuleBlockerSeverity.Failed);

    private void DrawSectionHeader(string title, string subtitle)
        => DadUi.Section(title, plugin.Configuration.DebugUiEnabled ? subtitle : null);

    private static void DrawPlaceholderNotice(string text)
    {
        var placeholderText = text.StartsWith("Placeholder:", StringComparison.OrdinalIgnoreCase)
            ? text
            : $"Placeholder: {text}";
        ImGui.TextDisabled(placeholderText);
    }

    private static void DrawMutedNotice(string text)
        => ImGui.TextDisabled(text);

    private void DrawPlannerRunFamilySelector(DadPresetPlannerOptions plannerOptions)
    {
        var currentLabel = plugin.PresetProviderService.GetPlannerRunFamilyLabel(plannerOptions.RunFamily);
        ImGui.SetNextItemWidth(MathF.Min(220f, ImGui.GetContentRegionAvail().X));
        if (!ImGui.BeginCombo("Run family", currentLabel))
            return;

        foreach (var family in plugin.PresetProviderService.GetPlannerRunFamilies())
        {
            var selected = plannerOptions.RunFamily == family;
            if (ImGui.Selectable(plugin.PresetProviderService.GetPlannerRunFamilyLabel(family), selected))
                SelectPlannerFamily(plannerOptions, family);
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawPlannerSubmodeSelector(DadPresetPlannerOptions plannerOptions, DadActivityPreset plannerPreview)
    {
        var submodes = plugin.PresetProviderService.GetPlannerSubmodes(plannerOptions.RunFamily);
        var currentLabel = plannerPreview.LaneDefinition.DisplayName;
        ImGui.SetNextItemWidth(MathF.Min(260f, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("Submode", currentLabel))
        {
            foreach (var lane in submodes)
            {
                var selected = IsSelectedPlannerLane(plannerOptions.ActivityMode, lane.ActivityMode);
                if (ImGui.Selectable(lane.DisplayName, selected))
                    SelectPlannerLane(plannerOptions, lane);
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (plannerOptions.ActivityMode == DadPlannerActivityMode.Msq)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.62f, 0.28f, 1f));
            ImGui.TextWrapped(DadLegacyActivityRules.MsqUnsupportedBlocker);
            ImGui.PopStyleColor();
        }
    }

    private void DrawPlannerStopPolicyControls(
        DadPresetPlannerOptions plannerOptions,
        DadActivityPreset plannerPreview,
        DadPlannerRunRequestPreview requestPreview)
    {
        var stopPolicy = plannerOptions.StopPolicy ??= new DadRunStopPolicy();
        stopPolicy.Normalize();

        var modeLabel = plugin.PresetProviderService.GetPlannerStopModeLabel(stopPolicy.Mode);
        ImGui.SetNextItemWidth(MathF.Min(260f, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("Stop condition", modeLabel))
        {
            foreach (var mode in new[] { DadPlannerStopMode.AfterRuns, DadPlannerStopMode.TargetLevel, DadPlannerStopMode.ItemTarget, DadPlannerStopMode.RestedXpDepleted })
            {
                var selected = stopPolicy.Mode == mode;
                if (ImGui.Selectable(plugin.PresetProviderService.GetPlannerStopModeLabel(mode), selected))
                {
                    stopPolicy.Mode = mode;
                    stopPolicy.Normalize();
                    plugin.SavePlannerOptions();
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (stopPolicy.Mode == DadPlannerStopMode.TargetLevel)
        {
            var targetLevel = stopPolicy.TargetLevel;
            if (ImGui.InputInt("Target level", ref targetLevel))
            {
                var committedSignature = BuildPlannerStopPolicySignature(stopPolicy);
                stopPolicy.TargetLevel = Math.Clamp(targetLevel, 1, 999);
                plugin.QueueDebouncedPlannerOptionsSave(
                    "stop-policy",
                    committedSignature,
                    () => BuildPlannerStopPolicySignature(stopPolicy));
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "The bottom target applies only to the first selected primary character when that row is blank. That row overrides it when set; other nonblank row targets are additive, and all must be proven. Any reads the loaded character's live current job/level; a specific job reads that job's ledger.");
            }

            var safetyCap = stopPolicy.SafetyCap;
            if (ImGui.InputInt("Safety cap", ref safetyCap))
            {
                var committedSignature = BuildPlannerStopPolicySignature(stopPolicy);
                stopPolicy.SafetyCap = Math.Clamp(safetyCap, 1, 200);
                plugin.QueueDebouncedPlannerOptionsSave(
                    "stop-policy",
                    committedSignature,
                    () => BuildPlannerStopPolicySignature(stopPolicy));
            }
        }
        else if (stopPolicy.Mode == DadPlannerStopMode.ItemTarget)
        {
            // Feature batch A: stop when an inventory item reaches a target count.
            var itemId = (int)stopPolicy.StopItemId;
            if (ImGui.InputInt("Target item id", ref itemId))
            {
                var committedSignature = BuildPlannerStopPolicySignature(stopPolicy);
                stopPolicy.StopItemId = (uint)Math.Max(0, itemId);
                plugin.QueueDebouncedPlannerOptionsSave(
                    "stop-policy",
                    committedSignature,
                    () => BuildPlannerStopPolicySignature(stopPolicy));
            }

            var targetCount = stopPolicy.StopItemTargetCount;
            if (ImGui.InputInt("Target count", ref targetCount))
            {
                var committedSignature = BuildPlannerStopPolicySignature(stopPolicy);
                stopPolicy.StopItemTargetCount = Math.Clamp(targetCount, 1, 99999);
                plugin.QueueDebouncedPlannerOptionsSave(
                    "stop-policy",
                    committedSignature,
                    () => BuildPlannerStopPolicySignature(stopPolicy));
            }

            var itemSafetyCap = stopPolicy.SafetyCap;
            if (ImGui.InputInt("Safety cap", ref itemSafetyCap))
            {
                var committedSignature = BuildPlannerStopPolicySignature(stopPolicy);
                stopPolicy.SafetyCap = Math.Clamp(itemSafetyCap, 1, 200);
                plugin.QueueDebouncedPlannerOptionsSave(
                    "stop-policy",
                    committedSignature,
                    () => BuildPlannerStopPolicySignature(stopPolicy));
            }

            ImGui.TextDisabled("Stops when your inventory count of the item id reaches the target (safety cap still bounds runs).");
        }
        else if (stopPolicy.Mode == DadPlannerStopMode.RestedXpDepleted)
        {
            var restedSafetyCap = stopPolicy.SafetyCap;
            if (ImGui.InputInt("Safety cap", ref restedSafetyCap))
            {
                var committedSignature = BuildPlannerStopPolicySignature(stopPolicy);
                stopPolicy.SafetyCap = Math.Clamp(restedSafetyCap, 1, 200);
                plugin.QueueDebouncedPlannerOptionsSave(
                    "stop-policy",
                    committedSignature,
                    () => BuildPlannerStopPolicySignature(stopPolicy));
            }

            ImGui.TextDisabled("Stops when the local HUD rested-XP value reads zero; safety cap still bounds runs.");
        }
        else
        {
            var afterRuns = stopPolicy.AfterRuns;
            if (ImGui.InputInt("Run count", ref afterRuns))
            {
                var committedSignature = BuildPlannerStopPolicySignature(stopPolicy);
                stopPolicy.AfterRuns = Math.Clamp(afterRuns, 1, 200);
                plugin.QueueDebouncedPlannerOptionsSave(
                    "stop-policy",
                    committedSignature,
                    () => BuildPlannerStopPolicySignature(stopPolicy));
            }
        }

    }

    private void DrawPlannerCompletionActionsControls(
        DadPresetPlannerOptions plannerOptions,
        DadPlannerRunRequestPreview requestPreview)
    {
        var hasOverride = plannerOptions.CompletionActions != null;
        if (ImGui.Checkbox("Override completion defaults for this preset", ref hasOverride))
        {
            plannerOptions.CompletionActions = hasOverride
                ? DadCompletionActionSnapshots.Resolve(null, plugin.Configuration.CompletionActions)
                : null;
            draftPlannerCompletionCommands = plannerOptions.CompletionActions == null
                ? string.Empty
                : string.Join("\n", plannerOptions.CompletionActions.Commands);
            plannerCompletionDraftOwner = BuildPlannerCompletionDraftOwner(plannerOptions);
            plugin.SavePlannerOptions();
        }

        var actions = plannerOptions.CompletionActions;
        if (actions == null)
        {
            ImGui.TextDisabled("This preset uses the global defaults from Settings > Completion & Safety.");
            return;
        }

        var playSound = actions.PlaySound;
        if (ImGui.Checkbox("Play sound on preset completion", ref playSound))
        {
            actions.PlaySound = playSound;
            plugin.SavePlannerOptions();
        }

        if (actions.PlaySound)
        {
            var soundId = actions.SoundEffectId;
            if (ImGui.InputInt("Preset sound effect (1-16)", ref soundId))
            {
                actions.SoundEffectId = Math.Clamp(soundId, 1, 16);
                plugin.SavePlannerOptions();
            }
        }

        var runCommands = actions.RunCommands;
        if (ImGui.Checkbox("Run preset commands on completion", ref runCommands))
        {
            actions.RunCommands = runCommands;
            plugin.SavePlannerOptions();
        }

        if (actions.RunCommands)
        {
            var draftOwner = BuildPlannerCompletionDraftOwner(plannerOptions);
            if (!string.Equals(plannerCompletionDraftOwner, draftOwner, StringComparison.Ordinal))
            {
                draftPlannerCompletionCommands = string.Join("\n", actions.Commands ?? []);
                plannerCompletionDraftOwner = draftOwner;
            }

            if (ImGui.InputTextMultiline("Preset commands (one per line)", ref draftPlannerCompletionCommands, 2048, new Vector2(-1f, 90f)))
            {
                if (DadCompletionCommandRules.TryNormalizeCustomCommands(
                        draftPlannerCompletionCommands.Split('\n'),
                        out var normalizedCommands,
                        out plannerCompletionCommandValidation))
                {
                    var committedSignature = BuildPlannerCompletionActionSignature(actions);
                    actions.Commands = normalizedCommands;
                    plugin.QueueDebouncedPlannerOptionsSave(
                        "planner-completion-commands",
                        committedSignature,
                        () => BuildPlannerCompletionActionSignature(plannerOptions.CompletionActions));
                }
            }
            if (!string.IsNullOrWhiteSpace(plannerCompletionCommandValidation))
                ImGui.TextColored(new Vector4(1f, .35f, .35f, 1f), plannerCompletionCommandValidation);
        }

        ImGui.TextUnformatted("Post-run utilities");
        var utilities = actions.Utilities ??= new DadPostRunUtilities();

        var openGearCoffers = utilities.OpenGearCoffers;
        if (ImGui.Checkbox("Open preset gear coffers", ref openGearCoffers))
        {
            utilities.OpenGearCoffers = openGearCoffers;
            plugin.SavePlannerOptions();
        }

        var registerTripleTriad = utilities.RegisterTripleTriadCards;
        if (ImGui.Checkbox("Register preset Triple Triad cards", ref registerTripleTriad))
        {
            utilities.RegisterTripleTriadCards = registerTripleTriad;
            plugin.SavePlannerOptions();
        }

        var sellTripleTriad = utilities.SellTripleTriadCards;
        if (ImGui.Checkbox("Sell preset Triple Triad cards", ref sellTripleTriad))
        {
            utilities.SellTripleTriadCards = sellTripleTriad;
            plugin.SavePlannerOptions();
        }

        var gcHandIn = utilities.GrandCompanyHandInViaAutoRetainer;
        if (ImGui.Checkbox("Preset Grand Company hand-in via AutoRetainer", ref gcHandIn))
        {
            utilities.GrandCompanyHandInViaAutoRetainer = gcHandIn;
            plugin.SavePlannerOptions();
        }

        if (utilities.GrandCompanyHandInViaAutoRetainer)
        {
            var gcCommand = utilities.GrandCompanyHandInCommand;
            if (ImGui.InputText("Preset AutoRetainer GC command", ref gcCommand, 128))
            {
                if (DadCompletionCommandRules.TryNormalizeGrandCompanyHandInCommand(
                        gcCommand,
                        out var normalizedCommand,
                        out plannerCompletionCommandValidation))
                {
                    utilities.GrandCompanyHandInCommand = normalizedCommand;
                    plugin.SavePlannerOptions();
                }
            }
            ImGui.TextDisabled("Only the exact /ays command root is accepted for this native command.");
            if (!string.IsNullOrWhiteSpace(plannerCompletionCommandValidation))
                ImGui.TextColored(new Vector4(1f, .35f, .35f, 1f), plannerCompletionCommandValidation);
        }

        if (actions.KillMode != DadCompletionKillMode.None)
        {
            DrawStatusRow("Legacy preset completion value", $"{actions.KillMode} was loaded for compatibility and is a permanent no-op.");
            if (ImGui.Button("Clear disabled preset completion value"))
            {
                actions.KillMode = DadCompletionKillMode.None;
                plugin.SavePlannerOptions();
            }
        }

    }

    private static string BuildPlannerCompletionDraftOwner(DadPresetPlannerOptions plannerOptions)
        => $"{plannerOptions.SelectedPlannerGroupId}|{plannerOptions.ActivityMode}|{plannerOptions.CompletionActions != null}";

    private static string BuildPlannerCompletionActionSignature(DadCompletionActions? actions)
    {
        if (actions == null)
            return "global-defaults";

        var utilities = actions.Utilities ?? new DadPostRunUtilities();
        return string.Join("|", new[]
        {
            actions.PlaySound.ToString(),
            actions.SoundEffectId.ToString(CultureInfo.InvariantCulture),
            actions.RunCommands.ToString(),
            string.Join("\n", actions.Commands ?? []),
            actions.KillMode.ToString(),
            utilities.OpenGearCoffers.ToString(),
            utilities.RegisterTripleTriadCards.ToString(),
            utilities.SellTripleTriadCards.ToString(),
            utilities.GrandCompanyHandInViaAutoRetainer.ToString(),
            utilities.GrandCompanyHandInCommand,
        });
    }

    private static string BuildCompletionActionsSummary(DadCompletionActions actions)
    {
        var enabled = new List<string>();
        if (actions.PlaySound)
            enabled.Add($"sound se.{Math.Clamp(actions.SoundEffectId, 1, 16)}");
        var commandCount = actions.Commands?.Count ?? 0;
        if (actions.RunCommands && commandCount > 0)
            enabled.Add($"{commandCount} command(s)");
        if (actions.Utilities?.OpenGearCoffers == true)
            enabled.Add("open coffers");
        if (actions.Utilities?.RegisterTripleTriadCards == true)
            enabled.Add("register cards");
        if (actions.Utilities?.SellTripleTriadCards == true)
            enabled.Add("sell cards");
        if (actions.Utilities?.GrandCompanyHandInViaAutoRetainer == true)
            enabled.Add("GC hand-in");
        if (actions.KillMode != DadCompletionKillMode.None)
            enabled.Add($"legacy {actions.KillMode} value disabled (no-op)");

        return enabled.Count == 0 ? "No completion actions enabled." : string.Join(", ", enabled);
    }

    private void DrawPlannerLaneInputs(
        DadPresetPlannerOptions plannerOptions,
        DadPlannerLaneDefinition lane,
        DadPlannerDutyOption? selectedDuty,
        bool debugUi)
    {
        if (lane.ActivityMode is DadPlannerActivityMode.DutySupportLeveling or DadPlannerActivityMode.TrustLeveling)
        {
            DrawStatusRow("Auto selector", selectedDuty == null
                ? "No eligible duty found for the current local job/level."
                : selectedDuty.SelectionLabel);
            if (debugUi)
                DrawStatusRow("Runner count", "1 local runner");
            if (lane.ActivityMode == DadPlannerActivityMode.TrustLeveling)
            {
                var refreshTrustLevels = plannerOptions.RefreshTrustNpcLevels;
                if (ImGui.Checkbox("Refresh Trust NPC levels before queue", ref refreshTrustLevels))
                {
                    plannerOptions.RefreshTrustNpcLevels = refreshTrustLevels;
                    plugin.SavePlannerOptions();
                }
            }

            if (debugUi)
                DrawStatusRow("Request shape", "Solo local auto-level lane. Dad selects the highest eligible NPC duty at preview/start time.");
        }

        if (lane.ActivityMode is DadPlannerActivityMode.Squadron or DadPlannerActivityMode.VariantVvd)
        {
            DrawStatusRow("Executor", "Guarded deferred until in-game callback validation is complete.");
            if (lane.ActivityMode == DadPlannerActivityMode.VariantVvd)
            {
                var partySize = Math.Clamp(plannerOptions.DutyExpectedPartySize <= 0
                    ? selectedDuty?.QueueSize ?? lane.ExpectedPartySize
                    : plannerOptions.DutyExpectedPartySize, 1, 4);
                if (ImGui.InputInt("Expected party size", ref partySize))
                {
                    var committedSignature = plannerOptions.DutyExpectedPartySize.ToString(CultureInfo.InvariantCulture);
                    plannerOptions.DutyExpectedPartySize = Math.Clamp(partySize, 1, 4);
                    plugin.QueueDebouncedPlannerOptionsSave(
                        "variant-party-size",
                        committedSignature,
                        () => plannerOptions.DutyExpectedPartySize.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        if (lane.RequiresRouletteSelector)
        {
            var resolution = plugin.PresetProviderService.GetPlannerSelectedRoulette(plannerOptions);
            var selectedRoulette = resolution.Option;
            var rouletteLabel = selectedRoulette == null
                ? "Select roulette..."
                : selectedRoulette.IsAvailable
                    ? $"{selectedRoulette.DisplayName} #{selectedRoulette.RouletteId}"
                    : $"Unavailable: {selectedRoulette.DisplayName} #{selectedRoulette.RouletteId}";

            ImGui.TextUnformatted("Daily Roulette selector");
            if (debugUi)
                DrawStatusRow("Selector source", "Lumina ContentRoulette rows: Duty Finder, non-PvP, exactly one four-member party.");

            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("Roulette", rouletteLabel))
            {
                foreach (var option in plugin.PresetProviderService.GetPlannerRouletteOptions())
                {
                    var isSelected = selectedRoulette?.IsAvailable == true &&
                        selectedRoulette.RouletteId == option.RouletteId;
                    if (ImGui.Selectable($"{option.DisplayName} #{option.RouletteId}##dad-roulette-{option.RouletteId}", isSelected))
                        ApplyPlannerRouletteSelection(plannerOptions, lane, option);
                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }

            if (selectedRoulette != null)
            {
                if (debugUi)
                {
                    DrawStatusRow("Selected roulette", $"{selectedRoulette.DisplayName} #{selectedRoulette.RouletteId}");
                    DrawStatusRow("Roulette selector state", selectedRoulette.IsAvailable
                        ? "Available | synced | fixed four-Dad party"
                        : selectedRoulette.UnavailableReason);
                }
            }

            if (debugUi)
            {
                DrawStatusRow("Expected party size", DadDailyRoulettePlannerRules.RequiredPartySize.ToString(CultureInfo.InvariantCulture));
                DrawStatusRow("Queue mode", "Synced only; unrestricted party is forced off for registration and restored afterward.");
            }
        }

        if (lane.RequiresDutySelector)
        {
            var dutyCompatible = selectedDuty == null || IsPlannerDutyCompatible(selectedDuty, lane);
            var dutyLabel = selectedDuty == null
                ? "Select typed duty..."
                : dutyCompatible
                    ? selectedDuty.SelectionLabel
                    : $"Incompatible: {selectedDuty.SelectionLabel}";

            ImGui.TextUnformatted("Typed duty selector");
            if (debugUi)
            {
                DrawStatusRow("Selector source", lane.ActivityMode switch
                {
                    DadPlannerActivityMode.DutySupport => "Lumina ContentFinderCondition duties with Duty Support data only.",
                    DadPlannerActivityMode.Trust => "Lumina ContentFinderCondition duties with native Trust data only.",
                    _ => "Lumina ContentFinderCondition duty list.",
                });
            }

            var dutyPopupWidth = ResolvePlannerDutyPopupWidth();
            var dutyPopupHeight = MathF.Max(260f, ImGui.GetMainViewport().WorkSize.Y * 0.70f);
            ImGui.SetNextItemWidth(-1f);
            ImGui.SetNextWindowSizeConstraints(
                new Vector2(dutyPopupWidth, 120f),
                new Vector2(dutyPopupWidth, dutyPopupHeight));
            if (ImGui.BeginCombo("Duty", dutyLabel))
            {
                var popupContentWidth = MathF.Max(1f, dutyPopupWidth - (ImGui.GetStyle().WindowPadding.X * 2f));
                var search = plannerDutySearch;
                ImGui.SetNextItemWidth(popupContentWidth);
                if (ImGui.InputText("Search", ref search, 128))
                    plannerDutySearch = search;

                ImGui.Separator();
                var dutyResultsVisible = ImGui.BeginChild(
                    $"dad-duty-results-{lane.ActivityMode}",
                    new Vector2(popupContentWidth, 220f),
                    true);
                if (dutyResultsVisible)
                {
                    var dutyOptions = GetCachedPlannerDutySearchResults(lane.ActivityMode);
                    if (dutyOptions.Count == 0)
                    {
                        ImGui.TextDisabled("No duties matched current search.");
                    }
                    else
                    {
                        foreach (var option in dutyOptions)
                        {
                            var isSelected = selectedDuty != null
                                && option.ContentFinderConditionId == selectedDuty.ContentFinderConditionId;
                            var selectableWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
                            if (ImGui.Selectable($"{option.SelectionLabel}##dad-duty-{option.ContentFinderConditionId}", isSelected, ImGuiSelectableFlags.None, new Vector2(selectableWidth, 0f)))
                                ApplyPlannerDutySelection(plannerOptions, lane, option);

                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(BuildPlannerDutyOptionTooltip(option));

                            if (isSelected)
                                ImGui.SetItemDefaultFocus();
                        }
                    }
                }
                ImGui.EndChild();

                ImGui.EndCombo();
            }

            if (selectedDuty != null)
            {
                if (debugUi || !dutyCompatible)
                {
                    DrawStatusRow("Selected duty", dutyCompatible
                        ? selectedDuty.SelectionLabel
                        : $"Incompatible with {lane.DisplayName}: {selectedDuty.SelectionLabel}");
                }
                if (debugUi)
                    DrawStatusRow("Duty metadata", selectedDuty.MetadataSummary);
                if (!dutyCompatible)
                {
                    DrawStatusRow("Duty selector state", BuildIncompatibleDutyText(selectedDuty, lane));
                    if (ImGui.SmallButton("Clear incompatible duty"))
                        ClearPlannerDutySelection(plannerOptions, lane);
                    ImGui.SameLine();
                    ImGui.TextDisabled("Reselect from the Duty combo above.");
                }
            }

            if (lane.ActivityMode is DadPlannerActivityMode.PremadeDuty or DadPlannerActivityMode.LocalDuty)
            {
                var dutyUnsynced = plannerOptions.DutyUnsynced;
                if (ImGui.Checkbox("Unsynced", ref dutyUnsynced))
                {
                    plannerOptions.DutyUnsynced = dutyUnsynced;
                    plugin.SavePlannerOptions();
                }
            }

            if (lane.ActivityMode == DadPlannerActivityMode.PremadeDuty)
            {
                var partySize = Math.Max(2, plannerOptions.DutyExpectedPartySize <= 0
                    ? selectedDuty?.QueueSize ?? lane.ExpectedPartySize
                    : plannerOptions.DutyExpectedPartySize);
                if (ImGui.InputInt("Expected party size", ref partySize))
                {
                    var committedSignature = plannerOptions.DutyExpectedPartySize.ToString(CultureInfo.InvariantCulture);
                    plannerOptions.DutyExpectedPartySize = Math.Clamp(partySize, 2, 48);
                    plugin.QueueDebouncedPlannerOptionsSave(
                        "duty-party-size",
                        committedSignature,
                        () => plannerOptions.DutyExpectedPartySize.ToString(CultureInfo.InvariantCulture));
                }

                if (debugUi)
                {
                    DrawStatusRow("Queue lane", plugin.PresetProviderService.GetQueueAuthorityLabel(plannerOptions.QueueAuthority));
                    DrawStatusRow("Authority owner", DadStatusText.FormatAuthorityMode(lane.DefaultAuthorityMode));
                    DrawStatusRow("Request shape", "Typed premade request. Queue authority stays explicit; typed party size can be overridden here.");
                }
            }
            else if (lane.ActivityMode is DadPlannerActivityMode.DutySupport or DadPlannerActivityMode.Trust)
            {
                if (debugUi)
                {
                    DrawStatusRow("Execution mode", lane.ActivityMode == DadPlannerActivityMode.DutySupport ? "DutySupportOnly" : "TrustOnly");
                    DrawStatusRow("Runner count", "1 local runner");
                    DrawStatusRow("Request shape", "Solo local lane. Preview forces one local runner and local queue authority.");
                }
            }
            else if (lane.ActivityMode == DadPlannerActivityMode.LocalDuty)
            {
                if (debugUi)
                {
                    DrawStatusRow("Execution mode", "Regular Duty Finder");
                    DrawStatusRow("Run count", "1");
                    DrawStatusRow("Frequency", DadRunRequestOptions.FrequencyPerArRun);
                    DrawStatusRow("Request shape", "Local duty contract. Preview stays one runner; synced/unsynced applies only to this local lane.");
                }
            }
            else if (lane.ActivityMode == DadPlannerActivityMode.CustomDuty)
            {
                if (debugUi)
                {
                    DrawStatusRow("Attempts", "1");
                    DrawStatusRow("Request shape", "Typed custom duty contract. Planner keeps this lane local-only for now.");
                }
            }

            if (ImGui.SmallButton("Clear duty selector"))
                ClearPlannerDutySelection(plannerOptions, lane);

            if (debugUi)
            {
                DrawStatusRow("Duty selector state", selectedDuty != null
                    ? dutyCompatible
                        ? BuildDutySelectorState(plannerOptions, lane, selectedDuty)
                        : BuildIncompatibleDutyText(selectedDuty, lane)
                    : $"{lane.DisplayName} blocks until a typed duty is selected.");
            }
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Mogtome)
        {
            var preset = plannerOptions.MogtomePreset;
            if (ImGui.InputText("MOGTOME preset", ref preset, 128))
            {
                var committedSignature = plannerOptions.MogtomePreset;
                plannerOptions.MogtomePreset = preset;
                plugin.QueueDebouncedPlannerOptionsSave(
                    "mogtome-preset",
                    committedSignature,
                    () => plannerOptions.MogtomePreset);
            }

            var policies = plugin.PresetProviderService.GetMogtomeDutyPolicies().ToArray();
            var currentPolicyIndex = Array.IndexOf(policies, plannerOptions.MogtomeDutyPolicy);
            currentPolicyIndex = currentPolicyIndex < 0 ? 0 : currentPolicyIndex;
            var preview = plugin.PresetProviderService.GetMogtomeDutyPolicyLabel(policies[currentPolicyIndex]);
            if (ImGui.BeginCombo("MOGTOME duty policy", preview))
            {
                for (var index = 0; index < policies.Length; index++)
                {
                    var policy = policies[index];
                    var selected = index == currentPolicyIndex;
                    if (ImGui.Selectable(plugin.PresetProviderService.GetMogtomeDutyPolicyLabel(policy), selected))
                    {
                        plannerOptions.MogtomeDutyPolicy = policy;
                        plugin.SavePlannerOptions();
                    }

                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }

            if (debugUi)
            {
                DrawStatusRow("Attempts", "1");
                DrawStatusRow("MOGTOME preview", "Dad owns request preview. Policy controls helper handoff shape only.");
            }
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Blunderville)
        {
            if (debugUi)
            {
                DrawStatusRow("Attempts", "1");
                DrawStatusRow("Blunderville mode", "FixedEmoteRun");
                DrawStatusRow("Queue owner", plugin.PresetProviderService.GetQueueAuthorityLabel(lane.DefaultQueueAuthority));
                DrawStatusRow("Blunderville policy", "Dad enters, runs configured per-character emote, then fail/leaves per fixed contract.");
            }
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Msq)
        {
            DrawStatusRow("MSQ Story", "Unsupported compatibility value; select another activity explicitly.");
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Commendation)
        {
            if (debugUi)
            {
                DrawStatusRow("Attempts", "1");
                DrawStatusRow("Queue lane", plugin.PresetProviderService.GetQueueAuthorityLabel(lane.DefaultQueueAuthority));
                DrawStatusRow("Commendation policy", "Short duty loop contract. Preview keeps attempt count and queue lane explicit.");
            }
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Astrope)
        {
            if (debugUi)
            {
                DrawStatusRow("Attempts", "1");
                DrawStatusRow("Valid local time window", new DadTimeWindow().Describe());
                DrawStatusRow("Queue lane", plugin.PresetProviderService.GetQueueAuthorityLabel(lane.DefaultQueueAuthority));
                DrawStatusRow("Astrope policy", "Timed farming window stays explicit in preview JSON even before live executor phase.");
            }
        }
    }

    private IReadOnlyList<DadPlannerDutyOption> GetCachedPlannerDutySearchResults(DadPlannerActivityMode activityMode)
    {
        if (cachedPlannerDutySearchMode == activityMode &&
            string.Equals(cachedPlannerDutySearchText, plannerDutySearch, StringComparison.Ordinal))
        {
            return cachedPlannerDutySearchResults;
        }

        cachedPlannerDutySearchMode = activityMode;
        cachedPlannerDutySearchText = plannerDutySearch;
        cachedPlannerDutySearchResults = plugin.PresetProviderService.SearchPlannerDutyOptions(activityMode, plannerDutySearch, 96);
        return cachedPlannerDutySearchResults;
    }

    private static float ResolvePlannerDutyPopupWidth()
    {
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var viewportWidth = ImGui.GetMainViewport().WorkSize.X;
        var maxWidth = MathF.Max(160f, viewportWidth - 64f);
        return Math.Clamp(availableWidth <= 0f ? 360f : availableWidth, 160f, maxWidth);
    }

    private static string BuildPlannerDutyOptionTooltip(DadPlannerDutyOption option)
        => string.IsNullOrWhiteSpace(option.MetadataSummary)
            ? option.SelectionLabel
            : $"{option.SelectionLabel}\n{option.MetadataSummary}";

    private void DrawPlannerValidation(DadActivityPreset plannerPreview, DadPlannerRunRequestPreview requestPreview)
    {
        ImGui.TextUnformatted("Validation");
        if (plannerPreview.Blockers.Count == 0)
        {
            ImGui.TextUnformatted("No planner roster blockers.");
        }
        else
        {
            foreach (var blocker in plannerPreview.Blockers)
                ImGui.BulletText(blocker);
        }

        if (requestPreview.ModuleBlockers.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Module blockers");
            foreach (var blocker in requestPreview.ModuleBlockers)
                ImGui.BulletText($"{blocker.ModuleId} / {blocker.Capability}: {blocker.Summary}");
        }

        if (plannerPreview.Notes.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Operator notes");
            foreach (var note in plannerPreview.Notes)
                ImGui.BulletText(note);
        }
    }

    private static void DrawPlannerFilterCounts(DadActivityPreset plannerPreview)
    {
        ImGui.TextUnformatted("Filter counts");
        DrawStatusRow("Candidates kept", $"{plannerPreview.FilterStats.CandidatesAfterFilters}/{Math.Max(1, plannerPreview.FilterStats.TotalCandidates)}");
        DrawStatusRow("Connected filter", plannerPreview.FilterStats.ExcludedByConnectedFilter.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Stale filter", plannerPreview.FilterStats.ExcludedByStaleFilter.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Datacenter filter", plannerPreview.FilterStats.ExcludedByDatacenterFilter.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Account filter", plannerPreview.FilterStats.ExcludedByAccountFilter.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Local-only isolation", plannerPreview.FilterStats.ExcludedByLocalOnlyIsolation.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Peer readiness", plannerPreview.FilterStats.ExcludedByPeerEligibility.ToString(CultureInfo.InvariantCulture));
    }

    private void DrawPlannerRosterSlots(DadActivityPreset plannerPreview)
    {
        ImGui.TextUnformatted("Roster slots");
        if (!ImGui.BeginTable("dad-roster-slots", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Slot");
        ImGui.TableSetupColumn("Requirement");
        ImGui.TableSetupColumn("Assignment");
        ImGui.TableSetupColumn("Assigned character");
        ImGui.TableSetupColumn("Source");
        ImGui.TableSetupColumn("Freshness");
        ImGui.TableSetupColumn("Ready");
        ImGui.TableSetupColumn("Blockers");
        ImGui.TableHeadersRow();

        foreach (var slot in plannerPreview.SelectedCharacters)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.SlotId);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatRoleRequirement(slot.RequiredRole));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatText(slot.AssignmentSummary, "-"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatOperatorCharacterKey(slot.CharacterKey, "-"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.SelectedSource.HasValue
                ? plugin.PresetProviderService.GetCharacterSourceLabel(slot.SelectedSource.Value)
                : "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatFreshness(slot.SelectedFreshness));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatReadiness(slot.SelectedReadiness));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatOperatorText(FormatText(slot.BlockerSummary, slot.StatusText), "(none)"));
        }

        ImGui.EndTable();
    }

    private void DrawPlannerAvailableCharacters(DadActivityPreset plannerPreview)
    {
        ImGui.TextUnformatted("Available characters");
        if (plannerPreview.AvailableCharacters.Count == 0)
        {
            ImGui.TextUnformatted("No characters matched current planner filters.");
        }
        else if (ImGui.BeginTable("dad-available-characters", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Character");
            ImGui.TableSetupColumn("Job/Lvl");
            ImGui.TableSetupColumn("Account");
            ImGui.TableSetupColumn("Source");
            ImGui.TableSetupColumn("Freshness");
            ImGui.TableSetupColumn("Ready");
            ImGui.TableSetupColumn("Blockers");
            ImGui.TableHeadersRow();

            foreach (var character in plannerPreview.AvailableCharacters)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorCharacterKey(character.CharacterKey, "-"));
                ImGui.TableNextColumn();
                DrawJobLevelCell(BuildJobLevelDisplay(
                    character.JobLevels,
                    character.CurrentJobId,
                    character.CurrentJobAbbrev,
                    character.CurrentLevel));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatAccount(character));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(plugin.PresetProviderService.GetCharacterSourceLabel(character.Source));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatFreshness(character));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatReadiness(character.Readiness));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatOperatorText(FormatBlockers(character.Blockers), "(none)"));
            }

            ImGui.EndTable();
        }
    }

    private void ApplyPlannerDutySelection(
        DadPresetPlannerOptions plannerOptions,
        DadPlannerLaneDefinition lane,
        DadPlannerDutyOption duty)
    {
        plannerOptions.RunFamily = lane.RunFamily;
        plannerOptions.ActivityMode = lane.ActivityMode;
        plannerOptions.TransportOwner = lane.DefaultTransportOwner;
        plannerOptions.QueueAuthority = lane.DefaultQueueAuthority;
        plannerOptions.DutyContentFinderConditionId = duty.ContentFinderConditionId;
        plannerOptions.DutyDisplayName = duty.DutyDisplayName;
        plannerOptions.DutyExpectedPartySize = Math.Max(1, duty.QueueSize);
        if (lane.ActivityMode is DadPlannerActivityMode.DutySupport or DadPlannerActivityMode.Trust)
            plannerOptions.DutyUnsynced = false;

        plannerDutySearch = duty.DutyDisplayName;
        plugin.SavePlannerOptions();
        plugin.PrintStatus($"Selected Dad planner duty: {duty.DutyDisplayName} #{duty.ContentFinderConditionId} for {lane.DisplayName}.");
    }

    private void ApplyPlannerRouletteSelection(
        DadPresetPlannerOptions plannerOptions,
        DadPlannerLaneDefinition lane,
        DadPlannerRouletteOption roulette)
    {
        plannerOptions.RunFamily = lane.RunFamily;
        plannerOptions.ActivityMode = lane.ActivityMode;
        plannerOptions.TransportOwner = DadTransportOwner.LanParty;
        plannerOptions.QueueAuthority = DadQueueAuthority.Leader;
        plannerOptions.DutyUnsynced = false;
        plannerOptions.DutyExpectedPartySize = DadDailyRoulettePlannerRules.RequiredPartySize;
        plannerOptions.RouletteTarget = roulette.ToQueueTarget();
        plugin.SavePlannerOptions();
        plugin.PrintStatus($"Selected Daily Roulette: {roulette.DisplayName} #{roulette.RouletteId}.");
    }

    private void ClearPlannerDutySelection(DadPresetPlannerOptions plannerOptions, DadPlannerLaneDefinition lane)
    {
        plannerOptions.DutyContentFinderConditionId = 0;
        plannerOptions.DutyDisplayName = string.Empty;
        plannerOptions.DutyUnsynced = false;
        plannerOptions.DutyExpectedPartySize = lane.ExpectedPartySize;
        plugin.SavePlannerOptions();
        plugin.PrintStatus("Cleared Dad planner duty selector.");
    }

    private void DrawPlannerDutyDebugFallback(
        DadPresetPlannerOptions plannerOptions,
        DadPlannerLaneDefinition lane,
        bool plannerLocked)
    {
        DrawSectionHeader("Raw Duty Fallback", "Developer fallback for malformed catalog/selector state. Prefer the typed duty combo.");
        if (!lane.RequiresDutySelector)
        {
            DrawMutedNotice("Selected lane does not use a duty selector.");
            return;
        }

        ImGui.BeginDisabled(plannerLocked);
        var dutyId = unchecked((int)Math.Min(plannerOptions.DutyContentFinderConditionId, int.MaxValue));
        if (ImGui.InputInt("Content finder condition id", ref dutyId))
        {
            var committedSignature = BuildPlannerRawDutyFallbackSignature(plannerOptions);
            plannerOptions.DutyContentFinderConditionId = (uint)Math.Clamp(dutyId, 0, int.MaxValue);
            plugin.QueueDebouncedPlannerOptionsSave(
                "raw-duty-fallback",
                committedSignature,
                () => BuildPlannerRawDutyFallbackSignature(plannerOptions));
        }

        var dutyName = plannerOptions.DutyDisplayName;
        if (ImGui.InputText("Duty display name", ref dutyName, 128))
        {
            var committedSignature = BuildPlannerRawDutyFallbackSignature(plannerOptions);
            plannerOptions.DutyDisplayName = dutyName;
            plugin.QueueDebouncedPlannerOptionsSave(
                "raw-duty-fallback",
                committedSignature,
                () => BuildPlannerRawDutyFallbackSignature(plannerOptions));
        }

        ImGui.EndDisabled();
    }

    private static string BuildDutySelectorState(
        DadPresetPlannerOptions plannerOptions,
        DadPlannerLaneDefinition lane,
        DadPlannerDutyOption duty)
    {
        if (lane.ActivityMode == DadPlannerActivityMode.PremadeDuty)
        {
            var expectedPartySize = Math.Max(2, plannerOptions.DutyExpectedPartySize <= 0
                ? duty.QueueSize
                : plannerOptions.DutyExpectedPartySize);
            return $"{duty.SelectionLabel} | {duty.MetadataSummary} | request party {expectedPartySize} | {(plannerOptions.DutyUnsynced ? "unsynced" : "synced")}";
        }

        if (lane.ActivityMode == DadPlannerActivityMode.LocalDuty)
            return $"{duty.SelectionLabel} | {duty.MetadataSummary} | local solo request | {(plannerOptions.DutyUnsynced ? "unsynced" : "synced")}";

        return $"{duty.SelectionLabel} | {duty.MetadataSummary} | local solo request";
    }

    private static bool IsPlannerDutyCompatible(DadPlannerDutyOption duty, DadPlannerLaneDefinition lane)
        => lane.ActivityMode switch
        {
            DadPlannerActivityMode.DutySupport => duty.SupportsDutySupport,
            DadPlannerActivityMode.Trust => duty.SupportsTrust,
            _ => true,
        };

    private static string BuildIncompatibleDutyText(DadPlannerDutyOption duty, DadPlannerLaneDefinition lane)
        => lane.ActivityMode switch
        {
            DadPlannerActivityMode.DutySupport => $"{duty.DutyDisplayName} #{duty.ContentFinderConditionId} is not marked as Duty Support content. Clear it or reselect a Duty Support duty.",
            DadPlannerActivityMode.Trust => $"{duty.DutyDisplayName} #{duty.ContentFinderConditionId} is not marked as Trust content. Clear it or reselect a Trust duty.",
            _ => $"{duty.DutyDisplayName} #{duty.ContentFinderConditionId} is not valid for {lane.DisplayName}. Clear it or reselect a compatible duty.",
        };

    private DadPlannerLaneDefinition ResolveFamilyPreviewLane(DadPresetPlannerOptions plannerOptions, DadPlannerRunFamily family)
    {
        if (plannerOptions.RunFamily == family)
            return plugin.PresetProviderService.GetPlannerLaneDefinition(plannerOptions.ActivityMode);

        return plugin.PresetProviderService.GetPlannerLaneDefinition(plugin.PresetProviderService.GetDefaultPlannerSubmode(family));
    }

    private void SelectPlannerFamily(DadPresetPlannerOptions plannerOptions, DadPlannerRunFamily family)
    {
        plannerOptions.RunFamily = family;
        var submodes = plugin.PresetProviderService.GetPlannerSubmodes(family);
        var lane = submodes.Any(candidate => IsSelectedPlannerLane(plannerOptions.ActivityMode, candidate.ActivityMode))
            ? plugin.PresetProviderService.GetPlannerLaneDefinition(plannerOptions.ActivityMode)
            : submodes.FirstOrDefault() ?? plugin.PresetProviderService.GetPlannerLaneDefinition(DadPlannerActivityMode.DutySupport);
        SelectPlannerLane(plannerOptions, lane);
    }

    private void SelectPlannerLane(DadPresetPlannerOptions plannerOptions, DadPlannerLaneDefinition lane)
    {
        plannerOptions.RunFamily = lane.RunFamily;
        plannerOptions.ActivityMode = lane.ActivityMode;
        plannerOptions.TransportOwner = lane.DefaultTransportOwner;
        plannerOptions.QueueAuthority = lane.DefaultQueueAuthority;
        if (plannerOptions.DutyContentFinderConditionId == 0 && plannerOptions.DutyExpectedPartySize <= 0)
            plannerOptions.DutyExpectedPartySize = Math.Clamp(lane.ExpectedPartySize, 1, 48);
        plugin.SavePlannerOptions();
    }

    private void LoadPlannerTestDuty(DadPresetPlannerOptions plannerOptions, DadPlannerActivityMode activityMode)
    {
        var lane = plugin.PresetProviderService.GetPlannerLaneDefinition(activityMode);
        plannerOptions.RunFamily = lane.RunFamily;
        plannerOptions.ActivityMode = lane.ActivityMode;
        plannerOptions.TransportOwner = lane.DefaultTransportOwner;
        plannerOptions.QueueAuthority = lane.DefaultQueueAuthority;

        var testDuty = plugin.PresetProviderService.GetPlannerDutyOption(4);
        if (testDuty != null)
        {
            ApplyPlannerDutySelection(plannerOptions, lane, testDuty);
        }
        else
        {
            plannerOptions.DutyContentFinderConditionId = 4;
            plannerOptions.DutyDisplayName = "Sastasha";
            plannerOptions.DutyExpectedPartySize = Math.Clamp(lane.ExpectedPartySize, 1, 48);
        }

        plannerOptions.DutyUnsynced = lane.ActivityMode == DadPlannerActivityMode.LocalDuty;
        plugin.SavePlannerOptions();
        plugin.PrintStatus($"Loaded Dad planner test duty: {plannerOptions.DutyDisplayName} #{plannerOptions.DutyContentFinderConditionId} for {lane.DisplayName}.");
    }

    private void LoadPlannerDutySupportTest(DadPresetPlannerOptions plannerOptions)
    {
        var lane = plugin.PresetProviderService.GetPlannerLaneDefinition(DadPlannerActivityMode.DutySupport);
        plannerOptions.RunFamily = lane.RunFamily;
        plannerOptions.ActivityMode = lane.ActivityMode;
        plannerOptions.OperatorMode = DadPlannerOperatorMode.RemotePartyPlan;
        plannerOptions.TransportOwner = lane.DefaultTransportOwner;
        plannerOptions.QueueAuthority = lane.DefaultQueueAuthority;

        var testDuty = plugin.PresetProviderService.GetPlannerDutyOption(4);
        if (testDuty != null)
            ApplyPlannerDutySelection(plannerOptions, lane, testDuty);
        else
        {
            plannerOptions.DutyContentFinderConditionId = 4;
            plannerOptions.DutyDisplayName = "Sastasha";
            plannerOptions.DutyExpectedPartySize = 1;
        }

        plannerOptions.DutyUnsynced = false;
        plugin.SavePlannerOptions();
        plugin.PrintStatus($"Loaded Dad Duty Support test: {plannerOptions.DutyDisplayName} #{plannerOptions.DutyContentFinderConditionId}.");
    }

    private static bool IsPlannerLocked(DadVisibleRunState runState)
        => Plugin.IsBusy(runState.LocalRun) || Plugin.IsBusy(runState.AuthorityRun) || Plugin.IsBusy(runState.VisibleRun);

    private static bool IsSelectedPlannerLane(DadPlannerActivityMode selectedMode, DadPlannerActivityMode laneMode)
        => NormalizePlannerLane(selectedMode) == NormalizePlannerLane(laneMode);

    private static DadPlannerActivityMode NormalizePlannerLane(DadPlannerActivityMode activityMode)
        => activityMode switch
        {
            DadPlannerActivityMode.DutyPremade => DadPlannerActivityMode.PremadeDuty,
            _ => activityMode,
        };

    private static Vector4 ParseHexColor(string hex, float alpha)
    {
        var value = hex.Trim().TrimStart('#');
        if (value.Length != 6 || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return new Vector4(0.5f, 0.5f, 0.5f, alpha);

        var r = ((rgb >> 16) & 0xFF) / 255f;
        var g = ((rgb >> 8) & 0xFF) / 255f;
        var b = (rgb & 0xFF) / 255f;
        return new Vector4(r, g, b, alpha);
    }

    private static void DrawStatusRow(string label, string value)
        => DrawStatusRow(label, value, 180f);

    private static void DrawCompactStatusRow(string label, string value)
        => DrawStatusRow(label, value, 92f);

    private static void DrawStatusRow(string label, string value, float preferredLabelWidth)
        => DadUi.KeyValue(label, value, preferredLabelWidth);

    private static void DrawStateBadge(string label, string value)
        => DadUi.Badge($"{label}: {value}", ResolveBadgeTone(value));

    private static DadUiTone ResolveBadgeTone(string value)
    {
        if (value.Contains("Blocked", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Rejected", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("TimedOut", StringComparison.OrdinalIgnoreCase))
        {
            return DadUiTone.Danger;
        }

        if (value.Contains("Ready", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Enabled", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Connected", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Completed", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Startable", StringComparison.OrdinalIgnoreCase))
        {
            return DadUiTone.Success;
        }

        if (value.Contains("Wait", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Stale", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Warning", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Partial", StringComparison.OrdinalIgnoreCase))
        {
            return DadUiTone.Warning;
        }

        return value.Contains("Idle", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("None", StringComparison.OrdinalIgnoreCase)
            ? DadUiTone.Neutral
            : DadUiTone.Info;
    }

    private static Vector4 GetStartabilityColor(string startabilityLabel, int blockerCount)
    {
        if (blockerCount > 0 ||
            startabilityLabel.Contains("Blocked", StringComparison.OrdinalIgnoreCase) ||
            startabilityLabel.Contains("Failed", StringComparison.OrdinalIgnoreCase))
        {
            return new Vector4(1f, 0.45f, 0.35f, 1f);
        }

        if (startabilityLabel.Contains("Preview", StringComparison.OrdinalIgnoreCase))
            return new Vector4(0.45f, 0.75f, 1f, 1f);

        if (startabilityLabel.Contains("Start", StringComparison.OrdinalIgnoreCase) ||
            startabilityLabel.Contains("Ready", StringComparison.OrdinalIgnoreCase))
        {
            return new Vector4(0.35f, 0.95f, 0.45f, 1f);
        }

        return new Vector4(1f, 0.85f, 0.25f, 1f);
    }

    private static string BuildRuntimeBadge(string runtimeLabel)
        => runtimeLabel.StartsWith("Idle", StringComparison.OrdinalIgnoreCase)
            ? "Idle"
            : runtimeLabel;

    private static string BuildShortBlockerSummary(string firstBlocker, int blockerCount)
    {
        if (blockerCount <= 0)
            return "None";

        return blockerCount == 1
            ? firstBlocker
            : $"{blockerCount}: {firstBlocker}";
    }

    private static List<string> BuildPlannerBlockerList(DadPlannerRunRequestPreview requestPreview)
    {
        var blockers = new List<string>();
        if (!string.IsNullOrWhiteSpace(requestPreview.BlockedReason))
            blockers.Add(requestPreview.BlockedReason);

        blockers.AddRange(requestPreview.ContractPreview.Blockers
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker)));
        blockers.AddRange(requestPreview.PlannerPreview.Blockers
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker)));
        blockers.AddRange(requestPreview.ModuleBlockers
            .Select(static blocker => blocker.Summary)
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker)));

        return blockers
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildActiveRunKeyStatus(DadRunResult run)
    {
        if (run.Status == DadRunStatus.Idle)
            return "No active Dad run. Planner can be edited.";

        if (!string.IsNullOrWhiteSpace(run.BlockedReason))
            return DadOperatorPhaseText.HasBlockingFailure(run)
                ? $"Blocked: {run.BlockedReason}"
                : $"Runtime note: {run.BlockedReason}";

        if (!string.IsNullOrWhiteSpace(run.FailureReason))
            return $"Failure: {run.FailureReason}";

        return FormatText(string.IsNullOrWhiteSpace(run.ActiveTaskStatus) ? run.Summary : run.ActiveTaskStatus, "(none)");
    }

    private static string FormatPlannerTaskConfig(object? taskConfig)
        => taskConfig == null
            ? string.Empty
            : DadIpcJson.Serialize(taskConfig);

    private bool CanStartLocalDemo(CharacterConfig profile, DadRunResult localRun)
        => plugin.Configuration.PluginEnabled &&
           profile.Enabled &&
           profile.AllowIpcStarts &&
           !Plugin.IsBusy(localRun);

    private static void DrawDemoButton(string label, bool enabled, Func<DadRunResult> startDemo)
    {
        ImGui.BeginDisabled(!enabled);
        if (ImGui.SmallButton(label))
            startDemo();
        ImGui.EndDisabled();
    }

    private static string FormatFreshness(DadAcquiredCharacter character)
    {
        if (character.Source == DadCharacterSource.XadbOnly)
            return FormatRelativeAge(character.XadbSnapshotUtc);

        return character.Freshness switch
        {
            DadSnapshotFreshness.Live => "live",
            DadSnapshotFreshness.Recent => "recent",
            DadSnapshotFreshness.Stale => "stale",
            _ => "unknown",
        };
    }

    private static string FormatReadiness(DadReadinessState readiness)
        => readiness switch
        {
            DadReadinessState.Ready => "yes",
            DadReadinessState.Deferred => "deferred",
            DadReadinessState.Blocked => "blocked",
            DadReadinessState.Unavailable => "no",
            DadReadinessState.Stale => "stale",
            _ => "unknown",
        };

    private static string FormatFreshness(DadSnapshotFreshness freshness)
        => freshness switch
        {
            DadSnapshotFreshness.Live => "live",
            DadSnapshotFreshness.Recent => "recent",
            DadSnapshotFreshness.Stale => "stale",
            _ => "-",
        };

    private void DrawJobLevelCell(JobLevelDisplay display)
    {
        ImGui.TextUnformatted(display.Summary);
        if (!string.IsNullOrWhiteSpace(display.Tooltip) &&
            !string.Equals(display.Tooltip, "-", StringComparison.Ordinal) &&
            ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(display.Tooltip);
        }
    }

    private JobLevelDisplay BuildJobLevelDisplay(
        IReadOnlyDictionary<uint, int> jobLevels,
        uint? currentJobId,
        string currentJobAbbrev,
        int? currentLevel)
    {
        var entries = BuildJobLevelEntries(jobLevels, currentJobId, currentJobAbbrev, currentLevel);
        var labels = entries
            .Select(FormatJobLevelEntry)
            .Where(static label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (labels.Count == 0)
            return new JobLevelDisplay("-", string.Empty);

        var visibleCount = Math.Min(3, labels.Count);
        var summary = string.Join(", ", labels.Take(visibleCount));
        if (labels.Count > visibleCount)
            summary = $"{summary} +{labels.Count - visibleCount}";

        return new JobLevelDisplay(summary, string.Join(", ", labels));
    }

    private List<JobLevelEntry> BuildJobLevelEntries(
        IReadOnlyDictionary<uint, int> jobLevels,
        uint? currentJobId,
        string currentJobAbbrev,
        int? currentLevel)
    {
        var entries = new List<JobLevelEntry>();
        var hasCurrent = currentJobId.HasValue ||
                         !string.IsNullOrWhiteSpace(currentJobAbbrev) ||
                         currentLevel.HasValue;
        if (hasCurrent)
        {
            var resolvedLevel = currentLevel;
            if (currentJobId.HasValue && jobLevels.TryGetValue(currentJobId.Value, out var knownLevel))
                resolvedLevel = knownLevel;

            entries.Add(new JobLevelEntry(
                currentJobId,
                ResolveJobAbbrev(currentJobId, currentJobAbbrev),
                resolvedLevel));
        }

        entries.AddRange(jobLevels
            .Where(pair => pair.Key != 0 &&
                           pair.Value > 0 &&
                           (!currentJobId.HasValue || pair.Key != currentJobId.Value))
            .Select(pair => new JobLevelEntry(pair.Key, ResolveClassJobAbbrev(pair.Key), pair.Value))
            .OrderByDescending(static entry => entry.Level ?? 0)
            .ThenBy(static entry => entry.Abbreviation, StringComparer.OrdinalIgnoreCase));
        return entries;
    }

    private string ResolveJobAbbrev(uint? jobId, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(fallback))
            return fallback.Trim();

        return jobId.HasValue ? ResolveClassJobAbbrev(jobId.Value) : string.Empty;
    }

    private string ResolveClassJobAbbrev(uint jobId)
    {
        if (jobId == 0)
            return string.Empty;

        if (classJobAbbrevCache.TryGetValue(jobId, out var cached))
            return cached;

        var resolved = $"Job {jobId.ToString(CultureInfo.InvariantCulture)}";
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
            if (sheet != null && sheet.TryGetRow(jobId, out var classJob))
            {
                var abbreviation = classJob.Abbreviation.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(abbreviation))
                    resolved = abbreviation;
            }
        }
        catch
        {
            // UI fallback is good enough when Lumina is not ready during startup.
        }

        classJobAbbrevCache[jobId] = resolved;
        return resolved;
    }

    private static string FormatJobLevelEntry(JobLevelEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Abbreviation))
            return entry.Level?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        return entry.Level.HasValue
            ? $"{entry.Abbreviation} {entry.Level.Value.ToString(CultureInfo.InvariantCulture)}"
            : entry.Abbreviation;
    }

    private static string FormatParty(DadAcquiredCharacter character)
    {
        if (!character.PartyRosterCount.HasValue)
            return "?/?";

        var visible = character.VisiblePartyCount.HasValue
            ? character.VisiblePartyCount.Value.ToString(CultureInfo.InvariantCulture)
            : "?";

        return $"{character.PartyRosterCount.Value}/{visible}";
    }

    private static string FormatTime(DateTime? utc)
        => utc.HasValue
            ? utc.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : "(none)";

    private static string FormatRelativeAge(DateTime? utc)
    {
        if (!utc.HasValue)
            return "unknown";

        var age = DateTime.UtcNow - utc.Value;
        if (age <= TimeSpan.FromMinutes(1))
            return "live";
        if (age < TimeSpan.FromHours(1))
            return $"{Math.Max(1, (int)Math.Round(age.TotalMinutes))}m";
        if (age < TimeSpan.FromDays(1))
            return $"{Math.Max(1, (int)Math.Round(age.TotalHours))}h";

        return $"{Math.Max(1, (int)Math.Round(age.TotalDays))}d";
    }

    private static string FormatBlockers(IReadOnlyList<string> blockers)
        => blockers.Count == 0 ? "No blockers." : string.Join(" | ", blockers);

    private static string FormatRoleRequirement(DadPartyRole role)
        => role == DadPartyRole.Dps ? "DPS" : role.ToString();

    private static string FormatKeys<T>(IReadOnlyList<T> keys)
        => keys.Count == 0 ? "(none)" : string.Join(", ", keys.Select(static key => key?.ToString()));

    private static string FormatText(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private string FormatOperatorCharacterKey(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return plugin.KrangleService.FormatCharacterKey(value);
    }

    private string FormatOperatorAccountLabel(string? alias, string? accountKey)
        => plugin.KrangleService.FormatAccountLabel(alias, accountKey);

    private string FormatOperatorAccountKeys(IReadOnlyList<DadAccountKey> keys)
    {
        if (keys.Count == 0)
            return "(none)";

        return string.Join(", ", keys.Select(key => plugin.KrangleService.Enabled
            ? plugin.KrangleService.FormatAccountLabel("Account", key.Value)
            : key.ToString()));
    }

    private string FormatOperatorText(string? value, string fallback)
        => plugin.KrangleService.FormatOperatorText(FormatText(value, fallback), plugin.CharacterIntelligenceService.CurrentPool);

    private string FormatRunSnapshot(DadRunResult run)
    {
        var requestId = string.IsNullOrWhiteSpace(run.RequestId) ? "(none)" : run.RequestId;
        var taskDetail = string.IsNullOrWhiteSpace(run.ActiveTaskStatus) ? run.Summary : run.ActiveTaskStatus;
        var blocker = string.IsNullOrWhiteSpace(run.BlockedReason)
            ? string.Empty
            : $" | {(DadOperatorPhaseText.HasBlockingFailure(run) ? "Blocked" : "Note")} {run.BlockedReason}";
        return FormatOperatorText($"{run.Status} / {run.Phase} / {run.ModuleId} | {taskDetail}{blocker} | Request {requestId}", "(none)");
    }

    private string FormatExecutorStatus(DadModuleExecutionStatusDto status)
    {
        if (status.ModuleId == DadModuleId.None && string.IsNullOrWhiteSpace(status.DisplayName))
            return "(none)";

        var blockerLabel = HasHardBlocker(status.Blockers) ? "Blocker" : "Note";
        var blocker = string.IsNullOrWhiteSpace(status.BlockedReason) ? string.Empty : $" | {blockerLabel} {status.BlockedReason}";
        var retry = status.MaxRetryAttempts <= 0 ? string.Empty : $" | Retry {status.RetryAttempt}/{status.MaxRetryAttempts}";
        return FormatOperatorText($"{status.DisplayName} / {status.Status} / {status.Phase}{retry} | {status.Summary}{blocker}", "(none)");
    }

    private void DrawPlannerGroupIdentityControls(
        DadPlannerUiSnapshot plannerSnapshot,
        DadActivityPreset plannerPreview,
        bool plannerLocked)
    {
        var identityWidth = ImGui.GetContentRegionAvail().X;
        var identityFieldsShareRow = identityWidth >= ImGui.GetFontSize() * 36f;
        var templateActionSharesRow = identityWidth >= ImGui.GetFontSize() * 42f;
        var selectedGroup = plugin.GetSelectedPlannerGroup();
        var duplicateNames = plannerSnapshot.PlannerGroups
            .GroupBy(static group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preview = selectedGroup == null
            ? "Auto roster"
            : FormatPlannerGroupChoice(selectedGroup.DisplayName, selectedGroup.GroupId, duplicateNames);
        ImGui.SetNextItemWidth(MathF.Min(220f, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("Preset", preview))
        {
            var autoSelected = selectedGroup == null;
            if (ImGui.Selectable("Auto roster", autoSelected))
                plugin.ClearPlannerGroupSelection();
            if (autoSelected)
                ImGui.SetItemDefaultFocus();

            foreach (var group in plannerSnapshot.PlannerGroups)
            {
                var selected = selectedGroup != null &&
                               string.Equals(group.GroupId, selectedGroup.GroupId, StringComparison.OrdinalIgnoreCase);
                var choiceLabel = FormatPlannerGroupChoice(group.DisplayName, group.GroupId, duplicateNames);
                if (ImGui.Selectable($"{choiceLabel}##planner-group-{group.GroupId}", selected))
                {
                    plugin.SelectPlannerGroup(group.GroupId);
                    plannerGroupNameBuffer = group.DisplayName;
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (string.IsNullOrWhiteSpace(plannerGroupNameBuffer))
            plannerGroupNameBuffer = selectedGroup?.DisplayName ?? $"{plannerPreview.LaneDefinition.DisplayName} Preset";

        if (identityFieldsShareRow)
            ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Min(240f, MathF.Max(120f, ImGui.GetContentRegionAvail().X)));
        ImGui.InputText("Preset name", ref plannerGroupNameBuffer, 96);

        ImGui.BeginDisabled(plannerLocked);
        var saveLabel = selectedGroup == null ? "Create preset" : "Update selected preset";
        if (ImGui.SmallButton(saveLabel))
        {
            var group = plugin.SaveCurrentPlannerGroup(
                plannerGroupNameBuffer,
                out var created,
                out var rejectionReason);
            if (group == null)
            {
                plugin.PrintStatus(rejectionReason);
            }
            else
            {
                plannerGroupNameBuffer = group.DisplayName;
                plugin.PrintStatus($"{(created ? "Created" : "Updated")} preset '{group.DisplayName}'.");
                if (addSavedPlanToSchedule)
                {
                    var attachment = plugin.AttachSavedPlanToSchedule(plannerAttachScheduleId, group);
                    plugin.PrintStatus(attachment.Summary);
                }
            }
        }
        ImGui.EndDisabled();

        var schedules = plugin.Configuration.Schedules ?? [];
        if (!schedules.Any(schedule => string.Equals(
                schedule.ScheduleId,
                plannerAttachScheduleId,
                StringComparison.OrdinalIgnoreCase)))
        {
            plannerAttachScheduleId = schedules.FirstOrDefault()?.ScheduleId ?? string.Empty;
        }
        var attachmentMutationBlocker = plugin.GetShareMutationBlocker();
        var attachmentLocked = schedules.Count == 0 || !string.IsNullOrWhiteSpace(attachmentMutationBlocker);
        ImGui.SameLine();
        ImGui.BeginDisabled(attachmentLocked);
        ImGui.Checkbox("Add saved Plan to Schedule", ref addSavedPlanToSchedule);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && attachmentLocked)
        {
            ImGui.SetTooltip(schedules.Count == 0
                ? "Create a Schedule before attaching saved Plans."
                : attachmentMutationBlocker);
        }
        if (addSavedPlanToSchedule)
        {
            ImGui.SameLine();
            var selectedAttachSchedule = schedules.FirstOrDefault(schedule => string.Equals(
                schedule.ScheduleId,
                plannerAttachScheduleId,
                StringComparison.OrdinalIgnoreCase));
            ImGui.BeginDisabled(attachmentLocked);
            ImGui.SetNextItemWidth(MathF.Min(220f, MathF.Max(120f, ImGui.GetContentRegionAvail().X)));
            if (ImGui.BeginCombo(
                    "##planner-save-attach-schedule",
                    selectedAttachSchedule?.DisplayName ?? "(select Schedule)"))
            {
                foreach (var candidate in schedules)
                {
                    var selected = string.Equals(
                        candidate.ScheduleId,
                        plannerAttachScheduleId,
                        StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(candidate.DisplayName, selected))
                        plannerAttachScheduleId = candidate.ScheduleId;
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(selectedGroup == null || plannerLocked);
        if (ImGui.SmallButton("Rename"))
        {
            if (plugin.RenameSelectedPlannerGroup(plannerGroupNameBuffer))
                plugin.PrintStatus($"Renamed preset to '{plannerGroupNameBuffer}'.");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Duplicate"))
        {
            var group = plugin.DuplicateSelectedPlannerGroup(plannerGroupNameBuffer);
            if (group != null)
            {
                plannerGroupNameBuffer = group.DisplayName;
                plugin.PrintStatus($"Duplicated preset '{group.DisplayName}'.");
            }
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Delete"))
        {
            pendingDeletePlannerGroupId = selectedGroup?.GroupId ?? string.Empty;
            ImGui.OpenPopup("Confirm delete preset##dad-delete-preset");
        }

        // Feature batch B: save the current preset as a reusable, character-agnostic template.
        if (templateActionSharesRow)
            ImGui.SameLine();
        if (ImGui.SmallButton("Save as template"))
        {
            var template = plugin.CreateTemplateFromSelectedPlannerGroup(plannerGroupNameBuffer);
            if (template != null)
            {
                plannerGroupNameBuffer = template.DisplayName;
                plugin.PrintStatus($"Saved template '{template.DisplayName}' (character bindings cleared).");
            }
        }
        ImGui.EndDisabled();

        DrawDeletePresetPopup(plannerPreview);
        DrawPlannerShareControls(selectedGroup);

        selectedGroup = plugin.GetSelectedPlannerGroup();
        if (selectedGroup == null)
            return;

        // Feature batch B: a template can be instantiated against the live roster (auto-assign by role).
        if (selectedGroup.IsTemplate)
        {
            DrawStatusRow("Preset kind", "Template — not bound to specific characters.");
            if (ImGui.SmallButton("Instantiate template (auto-assign roster by role)"))
            {
                var instance = plugin.InstantiateSelectedPlannerTemplate();
                if (instance != null)
                {
                    plannerGroupNameBuffer = instance.DisplayName;
                    var assigned = DadPresetTemplateService.CountAssignedSlots(instance);
                    plugin.PrintStatus($"Created instance '{instance.DisplayName}' with {assigned}/{instance.Slots.Count} slot(s) auto-assigned by role.");
                }
            }
        }
    }

    private void DrawPlannerGroupCrewControls(
        DadPlannerUiSnapshot plannerSnapshot,
        DadActivityPreset plannerPreview,
        bool debugUi)
    {
        var selectedGroup = plugin.GetSelectedPlannerGroup();
        if (selectedGroup == null)
        {
            DrawMutedNotice("Create or select a saved preset in step 1 before assigning inline crew rows.");
            if (DadUi.Button("Guide: Create a Preset"))
                plugin.OpenSetupWizard(DadGuideFlow.FirstPreset);
            return;
        }

        if (debugUi && ImGui.SmallButton("Refresh group slots from current planner"))
        {
            plugin.ReplaceSelectedPlannerGroupSlotsFromCurrentPreview();
            plugin.PrintStatus($"Updated preset '{selectedGroup.DisplayName}' slots from current preview.");
        }

        DrawPlannerGroupSlotCapacityNotice(selectedGroup, plannerPreview);

        var nextSlotNumber = DadPlannerSlotRules.NextPrimarySlotNumber(selectedGroup.Slots);
        ImGui.BeginDisabled(nextSlotNumber == 0);
        if (ImGui.SmallButton("Add slot"))
        {
            selectedGroup.Slots.Add(new DadPlannerGroupSlot
            {
                SlotId = DadPlannerSlotRules.FormatSlotId(nextSlotNumber),
                RequiredRole = DadPartyRole.Any,
                AllowSubstitution = false,
            });
            plugin.TouchPlannerGroup(selectedGroup);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(nextSlotNumber == 0
                ? $"All Slot1-Slot{DadPlannerSlotRules.MaxSlotNumber.ToString(CultureInfo.InvariantCulture)} rows already exist."
                : "Adds the next generated SlotN row.");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.Checkbox("Details##dad-planner-crew-details", ref plannerCrewDetails);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show stable account IDs beside account aliases for this session.");

        DrawPlannerGroupSlotEditor(plannerSnapshot, selectedGroup);

        if (debugUi)
        {
            ImGui.Spacing();
            if (ImGui.TreeNode("Preset scheduling hints"))
            {
                DrawPlannerGroupScheduleControls(selectedGroup);
                ImGui.TreePop();
            }
        }
    }

    private void DrawLevelingModeControls(
        DadPlannerUiSnapshot plannerSnapshot,
        DadPlannerGroup? group)
    {
        if (group == null)
            return;

        var options = group.LevelingMode ?? new DadLevelingModeOptions();
        var plannerOptions = plugin.PlannerOptions;
        var supported = DadLevelingModeActivationRules.TryNormalizeSupportedDraft(
            plannerOptions.RunFamily,
            plannerOptions.ActivityMode,
            out _,
            out var childLane);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Leveling Mode");
        var enabled = options.Enabled;
        ImGui.BeginDisabled(!supported && !enabled);
        if (ImGui.Checkbox("Enable Leveling Mode##dad-leveling-mode", ref enabled))
        {
            var result = plugin.SetPlannerGroupLevelingMode(group, enabled);
            if (!result.Accepted)
                plugin.PrintStatus(result.Summary);
            options = group.LevelingMode ?? options;
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(supported
                ? "Loops immutable one-run children, rotating eligible jobs and selecting duty from the ordered threshold table."
                : DadLevelingModeActivationRules.ValidLaneSummary);
        }

        if (!options.Enabled)
        {
            ImGui.TextDisabled("Disabled. Fixed job, fixed duty, Level seek, and ordinary stop policy remain unchanged.");
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.72f, 0.25f, 1f));
        ImGui.TextWrapped("Leveling Mode overrides fixed jobs, fixed duty, Level seek, and ordinary stop policy while enabled; their saved values are not deleted.");
        ImGui.PopStyleColor();

        var goal = options.GoalLevel;
        ImGui.SetNextItemWidth(130f);
        if (ImGui.InputInt("Plan goal", ref goal))
        {
            options.GoalLevel = Math.Clamp(goal, 1, 999);
            plugin.TouchPlannerGroup(group);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A slot is complete only when every unlocked eligible full combat job for its selected role reaches this level.");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(190f);
        var orderLabel = options.JobOrder == DadLevelingJobOrder.HighestBelowGoal
            ? "Highest below goal"
            : "Lowest first";
        if (ImGui.BeginCombo("Job order", orderLabel))
        {
            foreach (var order in Enum.GetValues<DadLevelingJobOrder>())
            {
                var label = order == DadLevelingJobOrder.HighestBelowGoal
                    ? "Highest below goal"
                    : "Lowest first";
                var selected = options.JobOrder == order;
                if (ImGui.Selectable(label, selected))
                {
                    options.JobOrder = order;
                    plugin.TouchPlannerGroup(group);
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var dutyOptions = supported
            ? plugin.PresetProviderService.SearchPlannerDutyOptions(childLane, string.Empty, 4096)
            : [];
        var removeIndex = -1;
        var moveFrom = -1;
        var moveTo = -1;
        if (ImGui.BeginTable(
                "dad-leveling-thresholds",
                4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("Minimum level", ImGuiTableColumnFlags.WidthFixed, 115f);
            ImGui.TableSetupColumn("Duty", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Requirement", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableHeadersRow();

            for (var index = 0; index < options.DutyThresholds.Count; index++)
            {
                var threshold = options.DutyThresholds[index];
                if (threshold == null)
                    continue;
                ImGui.PushID($"dad-leveling-threshold-{index}");
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var minimum = threshold.MinimumLevel;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputInt("##minimum", ref minimum))
                {
                    threshold.MinimumLevel = Math.Clamp(minimum, 1, 999);
                    plugin.TouchPlannerGroup(group);
                }

                ImGui.TableNextColumn();
                var selectedDuty = plugin.PresetProviderService.GetPlannerDutyOption(threshold.ContentFinderConditionId);
                var dutyLabel = selectedDuty?.SelectionLabel
                                ?? (threshold.ContentFinderConditionId == 0
                                    ? "Select duty"
                                    : $"Unavailable #{threshold.ContentFinderConditionId}");
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.BeginCombo("##duty", dutyLabel))
                {
                    foreach (var duty in dutyOptions)
                    {
                        var selected = duty.ContentFinderConditionId == threshold.ContentFinderConditionId;
                        if (ImGui.Selectable(duty.SelectionLabel, selected))
                        {
                            threshold.ContentFinderConditionId = duty.ContentFinderConditionId;
                            threshold.DutyDisplayName = duty.DutyDisplayName;
                            plugin.TouchPlannerGroup(group);
                        }
                        if (selected)
                            ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
                if (ImGui.IsItemHovered() && selectedDuty != null)
                    ImGui.SetTooltip(selectedDuty.MetadataSummary);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(selectedDuty == null
                    ? "unknown"
                    : $"Lv. {selectedDuty.JobLevelRequired}");

                ImGui.TableNextColumn();
                ImGui.BeginDisabled(index == 0);
                if (ImGui.SmallButton("Up"))
                {
                    moveFrom = index;
                    moveTo = index - 1;
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(index >= options.DutyThresholds.Count - 1);
                if (ImGui.SmallButton("Down"))
                {
                    moveFrom = index;
                    moveTo = index + 1;
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                    removeIndex = index;
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        if (moveFrom >= 0 && moveTo >= 0)
        {
            (options.DutyThresholds[moveFrom], options.DutyThresholds[moveTo]) =
                (options.DutyThresholds[moveTo], options.DutyThresholds[moveFrom]);
            plugin.TouchPlannerGroup(group);
        }
        if (removeIndex >= 0)
        {
            options.DutyThresholds.RemoveAt(removeIndex);
            plugin.TouchPlannerGroup(group);
        }

        if (ImGui.SmallButton("Add duty threshold"))
        {
            var defaultDuty = options.DutyThresholds.Count == 0
                ? dutyOptions
                    .OrderBy(static duty => duty.JobLevelRequired)
                    .ThenBy(static duty => duty.ContentFinderConditionId)
                    .FirstOrDefault()
                : null;
            var minimum = options.DutyThresholds.Count == 0
                ? Math.Max(1, defaultDuty?.JobLevelRequired ?? 1)
                : Math.Clamp(options.DutyThresholds[^1].MinimumLevel + 1, 1, 999);
            defaultDuty ??= dutyOptions
                .Where(duty => duty.JobLevelRequired <= minimum)
                .OrderByDescending(static duty => duty.JobLevelRequired)
                .ThenBy(static duty => duty.ContentFinderConditionId)
                .FirstOrDefault();
            options.DutyThresholds.Add(new DadLevelingDutyThreshold
            {
                MinimumLevel = minimum,
                ContentFinderConditionId = defaultDuty?.ContentFinderConditionId ?? 0,
                DutyDisplayName = defaultDuty?.DutyDisplayName ?? string.Empty,
            });
            plugin.TouchPlannerGroup(group);
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Each row remains active until the next threshold; rows must be strictly increasing.");

        var compilation = plugin.BuildLevelingModeCompilation(group, plannerSnapshot.CuratedPool);
        var color = compilation.Status switch
        {
            DadLevelingCompilationStatus.Ready => new Vector4(0.42f, 0.88f, 0.5f, 1f),
            DadLevelingCompilationStatus.Complete => new Vector4(0.42f, 0.78f, 1f, 1f),
            _ => new Vector4(1f, 0.45f, 0.35f, 1f),
        };
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(compilation.Summary);
        ImGui.PopStyleColor();
        if (compilation.SelectedDuty != null)
            ImGui.TextDisabled($"Selected threshold: party minimum {compilation.PartyMinimumLevel} -> {compilation.SelectedDuty.DutyDisplayName} #{compilation.SelectedDuty.ContentFinderConditionId} (synced).");
        foreach (var slot in compilation.Slots)
            ImGui.BulletText(slot.Summary);
    }

    private static void DrawPlannerGroupSlotCapacityNotice(
        DadPlannerGroup group,
        DadActivityPreset plannerPreview)
    {
        if (!string.Equals(
                plannerPreview.SelectedPlannerGroupId,
                group.GroupId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var savedSlotCount = DadPlannerSlotRules.CountPrimarySlots(group.Slots);
        var effectiveSlotCount = plannerPreview.SelectedCharacters
            .Select(static slot => DadPlannerSlotRules.NormalizeStrictSlotId(slot.SlotId))
            .Where(static slotId => !string.IsNullOrWhiteSpace(slotId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (savedSlotCount == effectiveSlotCount)
            return;

        var notice = group.ActivityMode == DadPlannerActivityMode.Msq
            ? $"MSQ Story is retained only for compatibility and is unsupported. Select another activity explicitly; Daily Roulette -> Main Scenario remains separate. {savedSlotCount} saved row(s) remain intact until then."
            : $"{plannerPreview.LaneDefinition.DisplayName} currently uses {effectiveSlotCount} of {savedSlotCount} saved slots. Saved rows remain available when this preset is used with a larger party lane.";
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.78f, 0.3f, 1f));
        ImGui.TextWrapped(notice);
        ImGui.PopStyleColor();
    }

    private static string FormatPlannerGroupChoice(
        string displayName,
        string groupId,
        IReadOnlySet<string> duplicateNames)
    {
        if (!duplicateNames.Contains(displayName))
            return displayName;

        var normalizedId = groupId?.Trim() ?? string.Empty;
        var shortId = normalizedId[..Math.Min(8, normalizedId.Length)];
        return $"{displayName} [{shortId}]";
    }

    private void DrawPlannerGroupScheduleControls(DadPlannerGroup group)
    {
        var enabled = group.ScheduleEnabled;
        if (ImGui.Checkbox("Schedule enabled", ref enabled))
        {
            group.ScheduleEnabled = enabled;
            plugin.TouchPlannerGroup(group);
        }

        ImGui.SameLine();
        var priority = group.SchedulePriority;
        ImGui.SetNextItemWidth(100f);
        if (ImGui.InputInt("Priority", ref priority))
        {
            var committedSignature = BuildPlannerGroupScheduleSignature(group);
            group.SchedulePriority = Math.Clamp(priority, -100, 100);
            plugin.QueueDebouncedPlannerGroupTouch(
                group,
                "schedule",
                committedSignature,
                BuildPlannerGroupScheduleSignature);
        }

        var cadence = group.ScheduleCadenceHours <= 0 ? 18 : group.ScheduleCadenceHours;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Cadence (h)", ref cadence))
        {
            var committedSignature = BuildPlannerGroupScheduleSignature(group);
            group.ScheduleCadenceHours = Math.Clamp(cadence, 0, 24 * 30);
            plugin.QueueDebouncedPlannerGroupTouch(
                group,
                "schedule",
                committedSignature,
                BuildPlannerGroupScheduleSignature);
        }

        DrawPlannerGroupMapModeCombo(group);
    }

    private void DrawPlannerGroupMapModeCombo(DadPlannerGroup group)
    {
        ImGui.SetNextItemWidth(MathF.Min(180f, ImGui.GetContentRegionAvail().X));
        if (!ImGui.BeginCombo("Map mode", group.MapMode.ToString()))
            return;

        foreach (var mode in Enum.GetValues<DadMapCrewJobMode>())
        {
            var selected = mode == group.MapMode;
            if (ImGui.Selectable(mode.ToString(), selected))
            {
                group.MapMode = mode;
                plugin.TouchPlannerGroup(group);
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawDeletePresetPopup(DadActivityPreset plannerPreview)
    {
        if (!ImGui.BeginPopup("Confirm delete preset##dad-delete-preset"))
            return;

        var pending = plugin.ResolvePlannerGroup(pendingDeletePlannerGroupId);
        if (pending == null)
        {
            ImGui.TextUnformatted("No preset selected.");
            if (ImGui.SmallButton("Close"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextWrapped($"Delete preset '{pending.DisplayName}' with {pending.Slots.Count} slot(s)?");
        ImGui.TextDisabled("Selection clears and Dad config saves immediately.");
        if (ImGui.SmallButton("Delete preset"))
        {
            if (plugin.SelectPlannerGroup(pending.GroupId) && plugin.DeleteSelectedPlannerGroup())
            {
                plannerGroupNameBuffer = $"{plannerPreview.LaneDefinition.DisplayName} Preset";
                plugin.PrintStatus($"Deleted preset '{pending.DisplayName}'.");
            }

            pendingDeletePlannerGroupId = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Cancel"))
        {
            pendingDeletePlannerGroupId = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawPlannerShareControls(DadPlannerGroup? selectedGroup)
    {
        var mutationBlocker = plugin.GetShareMutationBlocker();
        var mutationLocked = !string.IsNullOrWhiteSpace(mutationBlocker);

        ImGui.Spacing();
        ImGui.BeginDisabled(selectedGroup == null);
        if (ImGui.SmallButton("Export##dad-share-plan-export"))
        {
            if (plugin.TryExportSelectedPlan(out var encoded, out var error))
            {
                ImGui.SetClipboardText(encoded);
                plannerShareStatus = $"Copied Plan '{selectedGroup!.DisplayName}' to the clipboard.";
            }
            else
            {
                plannerShareStatus = error;
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Copies this Plan with anonymous account tokens and forced character krangling. Base64 is transport encoding, not encryption; finish slash commands remain verbatim.");
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(mutationLocked);
        if (ImGui.SmallButton("Import##dad-share-plan-import"))
        {
            var clipboard = ImGui.GetClipboardText() ?? string.Empty;
            if (plugin.TryDecodeShare(clipboard, DadShareConstants.PlanKind, out var envelope, out var error) && envelope != null)
            {
                pendingPlannerShareImport = envelope;
                pendingPlannerSharePreview = plugin.ShareService.BuildImportPreview(
                    envelope,
                    plugin.Configuration.PlannerGroups,
                    plugin.Configuration.Schedules);
                pendingPlannerShareCommandsConfirmed = false;
                plannerShareStatus = string.Empty;
                ImGui.OpenPopup("Confirm Plan import##dad-share-plan-confirm");
            }
            else
            {
                plannerShareStatus = error;
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(mutationLocked
                ? mutationBlocker
                : "Reads a Plan share from the clipboard. A matching ID is fully replaced after confirmation; imported crew must be remapped locally.");
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(selectedGroup == null || mutationLocked);
        if (ImGui.SmallButton("ID##dad-share-plan-id") && selectedGroup != null)
        {
            plannerShareIdOwner = selectedGroup.GroupId;
            plannerShareIdEdit = selectedGroup.GroupId;
            ImGui.OpenPopup("Plan share details##dad-share-plan-details");
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(mutationLocked ? mutationBlocker : "View, copy, or safely change this Plan's sharing ID.");
        ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(plannerShareStatus))
            ImGui.TextDisabled(plannerShareStatus);

        DrawPlannerShareDetailsPopup(selectedGroup);
        DrawPlannerImportConfirmation();
    }

    private void DrawPlannerShareDetailsPopup(DadPlannerGroup? selectedGroup)
    {
        if (!ImGui.BeginPopup("Plan share details##dad-share-plan-details", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        selectedGroup = plugin.ResolvePlannerGroup(plannerShareIdOwner) ?? selectedGroup;
        if (selectedGroup == null)
        {
            ImGui.TextDisabled("The Plan is no longer available.");
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted("Share details");
        var currentId = selectedGroup.GroupId;
        ImGui.SetNextItemWidth(310f);
        ImGui.InputText("Current ID##dad-share-plan-current-id", ref currentId, 33, ImGuiInputTextFlags.ReadOnly);
        if (ImGui.SmallButton("Copy##dad-share-plan-copy-id"))
        {
            ImGui.SetClipboardText(selectedGroup.GroupId);
            plannerShareStatus = "Copied Plan ID.";
        }

        ImGui.SetNextItemWidth(310f);
        ImGui.InputText("New ID##dad-share-plan-new-id", ref plannerShareIdEdit, 33);
        var mutationBlocker = plugin.GetShareMutationBlocker();
        ImGui.BeginDisabled(!string.IsNullOrWhiteSpace(mutationBlocker));
        if (ImGui.SmallButton("Apply##dad-share-plan-apply-id"))
        {
            var result = plugin.RenamePlanId(selectedGroup.GroupId, plannerShareIdEdit);
            plannerShareStatus = result.Summary;
            if (result.Success)
            {
                plannerShareIdOwner = result.NewId;
                plannerShareIdEdit = result.NewId;
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrWhiteSpace(mutationBlocker))
            ImGui.SetTooltip(mutationBlocker);
        ImGui.EndDisabled();
        ImGui.TextDisabled("Use a unique canonical lowercase 32-hex GUID.");
        ImGui.EndPopup();
    }

    private void DrawPlannerImportConfirmation()
    {
        if (!ImGui.BeginPopupModal("Confirm Plan import##dad-share-plan-confirm", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var preview = pendingPlannerSharePreview;
        if (preview == null || pendingPlannerShareImport == null)
        {
            ImGui.TextDisabled("The decoded Plan share is no longer available.");
        }
        else
        {
            ImGui.TextWrapped($"Import Plan '{preview.Name}'?");
            ImGui.TextUnformatted($"ID: {preview.Id}");
            ImGui.TextUnformatted($"Bundled Plans: {preview.BundledPlanCount.ToString(CultureInfo.InvariantCulture)}");
            DrawShareReplacementSummary(preview);
            DrawShareCommandReview(preview, ref pendingPlannerShareCommandsConfirmed);
            ImGui.TextWrapped("Imported crew identities are anonymous placeholders. Remap every row in the Plan crew editor before validation or run.");
            ImGui.TextWrapped("Base64 is not encryption. Finish slash commands are preserved verbatim; review them before running the imported Plan.");
        }

        var mutationBlocker = plugin.GetShareMutationBlocker();
        ImGui.BeginDisabled(preview == null || pendingPlannerShareImport == null ||
                            preview.RequiresCommandConfirmation && !pendingPlannerShareCommandsConfirmed ||
                            !string.IsNullOrWhiteSpace(mutationBlocker));
        if (ImGui.SmallButton("Import##dad-share-plan-confirm-import"))
        {
            var result = plugin.ApplyShareImport(
                pendingPlannerShareImport!,
                pendingPlannerShareCommandsConfirmed);
            plannerShareStatus = result.Summary;
            if (result.Success)
            {
                var imported = plugin.ResolvePlannerGroup(result.ResultId);
                plannerGroupNameBuffer = imported?.DisplayName ?? plannerGroupNameBuffer;
            }
            pendingPlannerShareImport = null;
            pendingPlannerSharePreview = null;
            pendingPlannerShareCommandsConfirmed = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton("Cancel##dad-share-plan-confirm-cancel"))
        {
            pendingPlannerShareImport = null;
            pendingPlannerSharePreview = null;
            pendingPlannerShareCommandsConfirmed = false;
            ImGui.CloseCurrentPopup();
        }
        if (!string.IsNullOrWhiteSpace(mutationBlocker))
            ImGui.TextDisabled(mutationBlocker);
        ImGui.EndPopup();
    }

    private static void DrawShareReplacementSummary(DadShareImportPreview preview)
    {
        if (preview.ReplacementIds.Count == 0)
        {
            ImGui.TextDisabled("No matching IDs will be replaced.");
            return;
        }

        ImGui.TextWrapped("Matching IDs will be fully replaced:");
        var useScroll = preview.ReplacementIds.Count > 8;
        if (useScroll)
            ImGui.BeginChild("dad-share-replacement-ids", new Vector2(410f, ImGui.GetTextLineHeightWithSpacing() * 8f), true);
        foreach (var replacementId in preview.ReplacementIds)
            ImGui.BulletText(replacementId);
        if (useScroll)
            ImGui.EndChild();
    }

    private static void DrawShareCommandReview(
        DadShareImportPreview preview,
        ref bool confirmed)
    {
        if (!preview.RequiresCommandConfirmation)
            return;

        ImGui.Separator();
        ImGui.TextWrapped("Imported completion commands (shown verbatim):");
        var useScroll = preview.Commands.Count > 6;
        if (useScroll)
            ImGui.BeginChild("dad-share-command-preview", new Vector2(560f, ImGui.GetTextLineHeightWithSpacing() * 9f), true);
        foreach (var command in preview.Commands)
        {
            ImGui.TextDisabled($"{command.PlanName} | {command.CommandKind}");
            ImGui.TextUnformatted(command.Command);
            ImGui.Spacing();
        }
        if (useScroll)
            ImGui.EndChild();
        ImGui.Checkbox("I reviewed every imported command shown above", ref confirmed);
    }

    private void DrawPlannerGroupSlotEditor(DadPlannerUiSnapshot plannerSnapshot, DadPlannerGroup group)
    {
        presetCrewEditor.Draw(
            plannerSnapshot,
            group,
            plugin.TouchPlannerGroup,
            "dad-planner-group",
            plannerCrewDetails);
    }

    private void DrawPlannerOperatorModeSelector(DadPresetPlannerOptions plannerOptions)
    {
        var operatorModes = plugin.PresetProviderService.GetPlannerOperatorModeOptions().ToArray();
        var currentIndex = Array.IndexOf(operatorModes, plannerOptions.OperatorMode);
        currentIndex = currentIndex < 0 ? 0 : currentIndex;
        var preview = plugin.PresetProviderService.GetPlannerOperatorModeLabel(operatorModes[currentIndex]);
        if (!ImGui.BeginCombo("Operator mode", preview))
            return;

        for (var index = 0; index < operatorModes.Length; index++)
        {
            var option = operatorModes[index];
            var selected = option == plannerOptions.OperatorMode;
            if (ImGui.Selectable(plugin.PresetProviderService.GetPlannerOperatorModeLabel(option), selected))
            {
                plannerOptions.OperatorMode = option;
                plugin.SavePlannerOptions();
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawPlannerTransportOwnerSelector(DadPresetPlannerOptions plannerOptions)
    {
        var owners = Enum.GetValues<DadTransportOwner>();
        var currentIndex = Array.IndexOf(owners, plannerOptions.TransportOwner);
        currentIndex = currentIndex < 0 ? 0 : currentIndex;
        var preview = plugin.PresetProviderService.GetTransportOwnerLabel(owners[currentIndex]);
        if (!ImGui.BeginCombo("Transport owner", preview))
            return;

        for (var index = 0; index < owners.Length; index++)
        {
            var option = owners[index];
            var selected = option == plannerOptions.TransportOwner;
            if (ImGui.Selectable(plugin.PresetProviderService.GetTransportOwnerLabel(option), selected))
            {
                plannerOptions.TransportOwner = option;
                plugin.SavePlannerOptions();
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawPlannerQueueAuthoritySelector(DadPresetPlannerOptions plannerOptions)
    {
        var authorities = Enum.GetValues<DadQueueAuthority>();
        var currentIndex = Array.IndexOf(authorities, plannerOptions.QueueAuthority);
        currentIndex = currentIndex < 0 ? 0 : currentIndex;
        var preview = plugin.PresetProviderService.GetQueueAuthorityLabel(authorities[currentIndex]);
        if (!ImGui.BeginCombo("Queue authority", preview))
            return;

        for (var index = 0; index < authorities.Length; index++)
        {
            var option = authorities[index];
            var selected = option == plannerOptions.QueueAuthority;
            if (ImGui.Selectable(plugin.PresetProviderService.GetQueueAuthorityLabel(option), selected))
            {
                plannerOptions.QueueAuthority = option;
                plugin.SavePlannerOptions();
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawPlannerAccountFilterSelector(
        IReadOnlyList<DadRosterAccountOption> accountOptions,
        DadPresetPlannerOptions plannerOptions,
        string preview)
    {
        if (!ImGui.BeginCombo("Account filter", preview))
            return;

        var anySelected = plannerOptions.IncludedAccountKeys.Count == 0;
        if (ImGui.Selectable("Any account", anySelected, ImGuiSelectableFlags.DontClosePopups))
        {
            plannerOptions.IncludedAccountKeys.Clear();
            plugin.SavePlannerOptions();
        }
        if (anySelected)
            ImGui.SetItemDefaultFocus();

        foreach (var option in accountOptions)
        {
            var selected = plannerOptions.IncludedAccountKeys.Any(key =>
                string.Equals(key.Value, option.AccountKey.Value, StringComparison.OrdinalIgnoreCase));
            var label = $"{FormatRosterAccountOption(option)} ({option.AssignedCharacterCount})";
            if (ImGui.Selectable(label, selected, ImGuiSelectableFlags.DontClosePopups))
            {
                TogglePlannerAccountFilter(plannerOptions, option.AccountKey);
                plugin.SavePlannerOptions();
            }
        }

        ImGui.EndCombo();
    }

    private static void TogglePlannerAccountFilter(DadPresetPlannerOptions plannerOptions, DadAccountKey accountKey)
    {
        var existingIndex = plannerOptions.IncludedAccountKeys.FindIndex(key =>
            string.Equals(key.Value, accountKey.Value, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
            plannerOptions.IncludedAccountKeys.RemoveAt(existingIndex);
        else
            plannerOptions.IncludedAccountKeys.Add(accountKey);
    }

    private static string BuildLaunchProfileEditableSignature(DadLaunchProfile profile)
        => $"{profile.DisplayName}\n{profile.AccountKey.Value}\n{profile.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)}";

    private static string BuildPlannerStopPolicySignature(DadRunStopPolicy stopPolicy)
        => $"{stopPolicy.Mode}\n{stopPolicy.AfterRuns.ToString(CultureInfo.InvariantCulture)}\n{stopPolicy.TargetLevel.ToString(CultureInfo.InvariantCulture)}\n{stopPolicy.SafetyCap.ToString(CultureInfo.InvariantCulture)}\n{stopPolicy.StopItemId.ToString(CultureInfo.InvariantCulture)}\n{stopPolicy.StopItemTargetCount.ToString(CultureInfo.InvariantCulture)}";

    private static string BuildPlannerRawDutyFallbackSignature(DadPresetPlannerOptions plannerOptions)
        => $"{plannerOptions.DutyContentFinderConditionId.ToString(CultureInfo.InvariantCulture)}\n{plannerOptions.DutyDisplayName}";

    private static string BuildPlannerGroupScheduleSignature(DadPlannerGroup group)
        => $"{group.SchedulePriority.ToString(CultureInfo.InvariantCulture)}\n{group.ScheduleCadenceHours.ToString(CultureInfo.InvariantCulture)}\n{group.ScheduleRequester}\n{group.MapRunTemplate}";

    private static string FormatDutyIpcAndBridgeStatus(
        DadDutyIpcStatus dutyIpc,
        DadQuestionableReflectionBridgeStatus bridge)
    {
        var state = dutyIpc.Registered ? "IPC registered" : dutyIpc.RegistrationState;
        var bridgeState = bridge.Patched
            ? "runtime patched"
            : bridge.Pending
                ? "runtime pending"
                : bridge.QuestionableLoaded
                    ? "runtime blocked"
                    : "runtime not loaded";
        var cosmeticState = bridge.CosmeticPatched
            ? "cosmetic patched"
            : bridge.QuestionableLoaded
                ? "cosmetic blocked"
                : "cosmetic not loaded";
        var cosmeticBlocker = string.IsNullOrWhiteSpace(bridge.CosmeticLastBlocker)
            ? "(none)"
            : bridge.CosmeticLastBlocker;
        var territory = dutyIpc.LastTerritoryType == 0 ? "(none)" : dutyIpc.LastTerritoryType.ToString(CultureInfo.InvariantCulture);
        var runId = string.IsNullOrWhiteSpace(dutyIpc.LastRunId) ? "(none)" : dutyIpc.LastRunId;
        var failure = string.IsNullOrWhiteSpace(dutyIpc.LastFailure) ? "(none)" : dutyIpc.LastFailure;
        var cleanupUtc = dutyIpc.LastCleanupUtc?.ToString("O") ?? "(never)";
        var cleanupFailed = dutyIpc.LastCleanupFailedCommands.Count == 0
            ? "(none)"
            : string.Join(", ", dutyIpc.LastCleanupFailedCommands);
        return $"{state} | {bridgeState} | {cosmeticState} | cosmetic blocker {cosmeticBlocker} | territory {territory} | mode {dutyIpc.LastMode} | bareMode {dutyIpc.LastBareMode} | run {runId} | failure {failure} | cleanup {dutyIpc.LastCleanupResult} @ {cleanupUtc} | cleanup failed {cleanupFailed}";
    }

    private string FormatAccount(DadAcquiredCharacter character)
    {
        if (!string.IsNullOrWhiteSpace(character.AccountAlias) && !string.IsNullOrWhiteSpace(character.AccountId))
            return FormatOperatorAccountLabel(character.AccountAlias, character.AccountId);

        if (!string.IsNullOrWhiteSpace(character.AccountAlias))
            return FormatOperatorAccountLabel(character.AccountAlias, string.Empty);

        return string.IsNullOrWhiteSpace(character.AccountId) ? "-" : FormatOperatorAccountLabel("Account", character.AccountId);
    }
}
