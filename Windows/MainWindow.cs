using System.Globalization;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using dad.Models;
using dad.Services;
using Lumina.Excel.Sheets;

namespace dad.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private static readonly Vector2 MinimumWindowSize = new(760f, 600f);
    private static readonly string[] CompletionKillModes = { "None", "Close game client", "Shut down PC" };
    private const string RosterUnassignedAccountFilter = "__unassigned";
    private const string RosterNeedsUpdateFilter = "NeedsUpdate";
    private readonly Plugin plugin;
    private Vector2? pendingPosition;
    private bool resetPositionConditionNextDraw;
    private string plannerDutySearch = string.Empty;
    private DadPlannerActivityMode? cachedPlannerDutySearchMode;
    private string cachedPlannerDutySearchText = string.Empty;
    private IReadOnlyList<DadPlannerDutyOption> cachedPlannerDutySearchResults = [];
    private string plannerGroupNameBuffer = string.Empty;
    private string pendingDeletePlannerGroupId = string.Empty;
    private string pendingDeleteAccountId = string.Empty;
    private string pendingForgetAccountId = string.Empty;
    private string pendingMergeAccountId = string.Empty;
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
    private readonly Dictionary<uint, string> classJobAbbrevCache = new();
    private string selectedProfileOwner = string.Empty;
    private string selectedProfileAccount = string.Empty;
    private string selectedProfileCharacter = string.Empty;
    private long selectedProfileAccountRevision;
    private long selectedProfileRevision;
    private CharacterConfig profileDraft = new();
    private string profileSaveStatus = string.Empty;
    private string draftPlannerCompletionCommands = string.Empty;
    private string plannerCompletionDraftOwner = string.Empty;

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

    public MainWindow(Plugin plugin) : base($"{PluginInfo.DisplayName}##Main", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumWindowSize,
            MaximumSize = new Vector2(1800f, 1600f),
        };
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public void ResetToOrigin() => QueuePosition(new Vector2(1f, 1f));

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
        var debugUi = configuration.DebugUiEnabled;
        var profile = plugin.ConfigManager.GetActiveConfig();
        var runState = plugin.GetVisibleRunState();
        var localRun = runState.LocalRun;
        var authorityRun = runState.AuthorityRun;
        var characterPool = plugin.CharacterIntelligenceService.CurrentPool;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

        ImGui.Text($"{PluginInfo.DisplayName} v{version}");
        ImGui.SameLine(MathF.Max(0f, ImGui.GetWindowWidth() - 150f));
        if (ImGui.SmallButton("\u2661 Ko-fi \u2661"))
        {
            ImGui.SetClipboardText(PluginInfo.SupportUrl);
            plugin.PrintStatus($"Copied Ko-fi URL: {PluginInfo.SupportUrl}");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy Ko-fi URL");

        ImGui.Separator();

        var pluginEnabled = configuration.PluginEnabled;
        if (ImGui.Checkbox("Plugin enabled", ref pluginEnabled))
            plugin.SetPluginEnabled(pluginEnabled, printStatus: false);

        if (debugUi)
        {
            ImGui.SameLine();
            var dtrEnabled = configuration.DtrBarEnabled;
            if (ImGui.Checkbox("DTR Bar", ref dtrEnabled))
            {
                configuration.DtrBarEnabled = dtrEnabled;
                configuration.Save();
                plugin.UpdateDtrBar();
            }
        }

        ImGui.SameLine();
        var profileEnabled = profile.Enabled;
        if (ImGui.Checkbox("Profile armed", ref profileEnabled))
        {
            profile.Enabled = profileEnabled;
            plugin.ConfigManager.SaveCurrentAccount();
            plugin.UpdateDtrBar();
        }

        if (debugUi)
        {
            ImGui.SameLine();
            var allowIpcStarts = profile.AllowIpcStarts;
            if (ImGui.Checkbox("Allow Dad starts", ref allowIpcStarts))
            {
                profile.AllowIpcStarts = allowIpcStarts;
                plugin.ConfigManager.SaveCurrentAccount();
            }

            ImGui.SameLine();
            var localOnlyMode = configuration.LocalOnlyModeEnabled;
            if (ImGui.Checkbox("Local-only mode", ref localOnlyMode))
            {
                configuration.LocalOnlyModeEnabled = localOnlyMode;
                configuration.Save();
            }
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Settings"))
            plugin.ToggleConfigUi();

        if (debugUi)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Status to chat"))
                plugin.PrintStatusReport();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(plugin.KrangleService.Enabled ? "Un-Krangle" : "Krangle Names"))
            plugin.ToggleKrangleOperatorNames();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Toggle Dad's local operator-name krangle display. Run contracts stay raw.");

        var canStartLocalDemo = CanStartLocalDemo(profile, localRun);
        var canStartRemoteDemo = canStartLocalDemo &&
                                 !configuration.LocalOnlyModeEnabled &&
                                 plugin.HasServerDadAuthority() &&
                                 !Plugin.IsBusy(authorityRun);

        ImGui.Spacing();
        DrawActiveRunBanner(runState);
        ImGui.Spacing();

        if (debugUi)
        {
            DrawDemoButton("Run local demo", canStartLocalDemo, plugin.StartLocalDemoRunFromShell);

            ImGui.SameLine();
            DrawDemoButton("Run server demo", canStartRemoteDemo, plugin.StartServerDemoRunFromShell);

            ImGui.SameLine();
            DrawDemoButton("Run Daily MSQ demo", canStartRemoteDemo, plugin.StartDailyMsqDemoRunFromShell);

            ImGui.SameLine();
            DrawDemoButton("Run commend demo", canStartRemoteDemo, plugin.StartCommendationDemoRunFromShell);

            ImGui.SameLine();
        }

        ImGui.BeginDisabled(!Plugin.IsBusy(localRun) && !Plugin.IsBusy(authorityRun));
        if (ImGui.SmallButton("Cancel active run"))
            plugin.CancelActiveRunFromShell();
        ImGui.EndDisabled();

        if (debugUi)
        {
            ImGui.TextWrapped(PluginInfo.Summary);
            DrawStatusRow("Krangle", plugin.KrangleService.BuildStatus(characterPool));
            DrawStatusRow("Duty IPC / Questionable", FormatDutyIpcAndBridgeStatus(plugin.DutyIpcService.GetStatus(), plugin.QuestionableBridge.GetStatus()));
            DrawStatusRow("Character pool", characterPool.LastSummary);
            DrawStatusRow("XADB", characterPool.XadbStatus.LastStatus);
            DrawStatusRow("Peer transport", characterPool.PeerTransport.LastRequestStatus);
        }

        if (ImGui.BeginTabBar("dad-main-tabs"))
        {
            if (ImGui.BeginTabItem("Overview"))
            {
                DrawOverviewTab(runState, profile);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Multiplayer"))
            {
                DrawMultiplayerTab(characterPool, runState);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Crew / Scheduler"))
            {
                DrawCrewSchedulerTab(characterPool, runState);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Preset Planner"))
            {
                DrawPresetPlannerTab(characterPool, runState);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawActiveRunBanner(DadVisibleRunState runState)
    {
        var activeRun = GetActiveRun(runState);
        var plannerLocked = IsPlannerLocked(runState);
        var phase = DadOperatorPhaseText.FormatPhaseLabel(activeRun);
        var module = activeRun.ModuleId == DadModuleId.None ? "No module" : activeRun.ModuleId.ToString();
        var keyStatus = BuildActiveRunKeyStatus(activeRun);

        ImGui.Separator();
        DrawStateBadge("Phase", phase);
        ImGui.SameLine();
        DrawStateBadge("Module", module);
        ImGui.SameLine();
        DrawStateBadge("Status", activeRun.Status.ToString());
        if (plannerLocked)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel active run##top-banner"))
                plugin.CancelActiveRunFromShell();
        }

        ImGui.TextWrapped(keyStatus);
        ImGui.Separator();
    }

    private void DrawOverviewTab(DadVisibleRunState runState, CharacterConfig profile)
    {
        var activeRun = GetActiveRun(runState);
        var localRun = runState.LocalRun;
        var authorityRun = runState.AuthorityRun;
        var authorityView = runState.AuthorityView;
        if (!plugin.Configuration.DebugUiEnabled)
        {
            DrawOverviewCompact(runState, profile);
            return;
        }

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
        DrawStatusRow("Next action", BuildOverviewNextAction(runState, profile));
        DrawStatusRow("Account", FormatOperatorAccountLabel(plugin.ConfigManager.GetCurrentAccount()?.AccountAlias, plugin.ConfigManager.CurrentAccountId));
        DrawStatusRow("Profile", FormatOperatorCharacterKey(plugin.ConfigManager.SelectedCharacterKey, "(Account default)"));
        DrawStatusRow("Profile notes", FormatOperatorText(profile.TargetNotes, "(none)"));
    }

    private void DrawOverviewCompact(DadVisibleRunState runState, CharacterConfig profile)
    {
        var activeRun = GetActiveRun(runState);
        DrawSectionHeader("Run Summary", "Current Dad run state.");
        DrawStatusRow("Operator phase", DadOperatorPhaseText.FormatPhaseLabel(activeRun));
        DrawStatusRow("Visible run", activeRun.Status == DadRunStatus.Idle
            ? "Idle."
            : $"{activeRun.Status} / {activeRun.Phase} / {activeRun.ModuleId}");
        DrawStatusRow("Status", BuildActiveRunKeyStatus(activeRun));
        DrawStatusRow("Duty IPC / Questionable", FormatDutyIpcAndBridgeStatus(plugin.DutyIpcService.GetStatus(), plugin.QuestionableBridge.GetStatus()));

        if (DadOperatorPhaseText.HasBlockingFailure(activeRun))
        {
            var blocker = FormatText(
                string.IsNullOrWhiteSpace(activeRun.BlockedReason) ? activeRun.FailureReason : activeRun.BlockedReason,
                activeRun.Summary);
            DrawStatusRow("Blocker", blocker);
        }

        DrawDutySupportRuntimeSection(activeRun);

        DrawSectionHeader("Next Action", "Operator-facing next step.");
        DrawStatusRow("Next action", BuildOverviewNextAction(runState, profile));
    }

    private void DrawMultiplayerTab(DadCharacterPool characterPool, DadVisibleRunState runState)
    {
        var localRun = runState.LocalRun;
        var authorityRun = runState.AuthorityRun;
        var authorityView = runState.AuthorityView;
        var xadbStatus = characterPool.XadbStatus;
        var peerTransport = characterPool.PeerTransport;
        var localParticipant = plugin.PresenceService.BuildSnapshotCopy();
        var participants = new List<DadParticipantSnapshot> { localParticipant };
        participants.AddRange(peerTransport.KnownParticipants.Select(static participant => participant.Clone()));
        participants = participants
            .OrderBy(static participant => participant.ManagedAccountAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.ActiveCharacterKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var activeRun = GetActiveRun(runState);
        if (!plugin.Configuration.DebugUiEnabled)
        {
            DrawMultiplayerCompact(characterPool, runState, participants, activeRun);
            return;
        }

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
        DrawStatusRow("Listener", FormatText(peerTransport.ListenerEndpoint, "(none)"));
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
            ImGui.TableSetupColumn("Ready");
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
                ImGui.TextUnformatted(participant.PostArReady ? "post-AR ready" : participant.IsEligibleForRun ? "ready" : "waiting");
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
        var readyCount = participants.Count(static participant => participant.IsEligibleForRun);
        var staleCount = participants.Count(IsParticipantStale);
        var assignedCount = participants.Count(static participant => !string.IsNullOrWhiteSpace(participant.AssignedSlotId));

        DrawSectionHeader("Multiplayer Summary", "Current authority and participant readiness.");
        DrawStatusRow("Authority", $"{runState.AuthorityView.StateText} | {runState.AuthorityView.FreshnessText}");
        DrawStatusRow("Visible run", activeRun.Status == DadRunStatus.Idle
            ? "Idle."
            : $"{activeRun.ModuleId} | {DadOperatorPhaseText.FormatPhaseLabel(activeRun)} | {activeRun.Status}");
        DrawStatusRow("Participants", $"{participants.Count} discovered | {readyCount} eligible | {assignedCount} assigned | {staleCount} stale");
        DrawStatusRow("Peers", $"{characterPool.PeerTransport.ConnectedPeerCount} connected | {FormatText(characterPool.PeerTransport.Availability, "(unknown)")}");

        if (!string.IsNullOrWhiteSpace(activeRun.BlockedReason))
            DrawStatusRow(DadOperatorPhaseText.HasBlockingFailure(activeRun) ? "Blocker" : "Runtime note", activeRun.BlockedReason);

        DrawDutySupportRuntimeSection(activeRun);
    }

    private void DrawCrewSchedulerTab(DadCharacterPool characterPool, DadVisibleRunState runState)
    {
        var catalog = plugin.RosterCatalogService.CurrentCatalog;

        if (ImGui.BeginTabBar("dad-crew-scheduler-tabs"))
        {
            if (ImGui.BeginTabItem("Accounts & Profiles"))
            {
                DrawAccountsProfilesSection(characterPool, catalog, runState);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Presets"))
            {
                DrawCrewPresetSection(characterPool, runState);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Queue"))
            {
                DrawCrewQueueSection();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Active Job"))
            {
                DrawCrewActiveJobSection();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawAccountsProfilesSection(
        DadCharacterPool characterPool,
        DadAccountRosterCatalog catalog,
        DadVisibleRunState runState)
    {
        var launchProfiles = plugin.GetPlannerUiSnapshot(runState).LaunchProfiles;
        if (ImGui.CollapsingHeader("Launch profiles", ImGuiTreeNodeFlags.DefaultOpen))
            DrawLaunchProfileEditor(launchProfiles);

        if (ImGui.CollapsingHeader("Account profile tree", ImGuiTreeNodeFlags.DefaultOpen))
            DrawProfileTree(launchProfiles);

        if (ImGui.CollapsingHeader("Roster state", ImGuiTreeNodeFlags.DefaultOpen))
            DrawCrewRosterSection(characterPool, catalog);
    }

    private void DrawLaunchProfileEditor(IReadOnlyList<DadLaunchProfile> profiles)
    {
        if (ImGui.SmallButton("Import Z:\\!ff14clientboot batches"))
            plugin.ImportLaunchProfilesFromBootDirectory();
        ImGui.SameLine();
        ImGui.TextDisabled("Batch files remain read-only; imported profiles default disabled, auto-start off, dry-run on.");

        if (profiles.Count == 0)
        {
            ImGui.TextDisabled("No launch candidates imported.");
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
            ImGui.TextDisabled("No owned account profiles available.");
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

    private void DrawCrewRosterSection(DadCharacterPool characterPool, DadAccountRosterCatalog catalog)
    {
        DrawSectionHeader("Roster Accounts", "Pick an account first. Assigned Active rows feed normal crew slots.");
        if (ImGui.SmallButton("Refresh local roster"))
        {
            catalog = plugin.RosterCatalogService.RefreshCatalog(characterPool, new DadRosterRefreshPlan
            {
                IncludeHidden = true,
                IncludeIgnored = true,
                LogDiagnostics = true,
                DiagnosticsReason = "manual local roster refresh",
            });
            ResetRosterBrowseFilters(catalog, RosterBrowseResetMode.AllRows);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Populate roster from connected Dads"))
        {
            catalog = plugin.RosterCatalogService.RefreshCatalog(characterPool, new DadRosterRefreshPlan
            {
                ForcePeerRefresh = true,
                LiveConnectedOnly = true,
                IncludeHidden = true,
                IncludeIgnored = true,
                LogDiagnostics = true,
                DiagnosticsReason = "manual connected roster refresh",
            });
            ResetRosterBrowseFilters(catalog, RosterBrowseResetMode.AllRows);
        }

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

        var filtered = FilterRosterCharacters(catalog.Characters).ToList();
        TrimRosterSelection(catalog.Characters);
        var selectedFiltered = filtered
            .Where(character => rosterSelectedRows.Contains(BuildRosterSelectionKey(character)))
            .ToList();
        var activeFilters = BuildRosterActiveFilterSummary(catalog);
        DrawStatusRow("Catalog", $"{catalog.Summary} Showing {filtered.Count}/{catalog.Characters.Count} row(s).");
        if (!string.IsNullOrWhiteSpace(activeFilters) && filtered.Count < catalog.Characters.Count)
            DrawStatusRow("Filtered rows", $"{catalog.Characters.Count - filtered.Count} row(s) hidden by filters: {activeFilters}");
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
        DrawStatusRow("Roster counts", BuildRosterStatusCounts(catalog));
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
        var columnCount = showAccountColumn ? 11 : 10;
        if (!ImGui.BeginTable("dad-crew-roster", columnCount, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollX))
            return;

        ImGui.TableSetupColumn("Sel");
        if (showAccountColumn)
            ImGui.TableSetupColumn("Account");
        ImGui.TableSetupColumn("Character");
        ImGui.TableSetupColumn("ContentId");
        ImGui.TableSetupColumn("World/DC");
        ImGui.TableSetupColumn("Snapshot age");
        ImGui.TableSetupColumn("Job/Lvl");
        ImGui.TableSetupColumn("State");
        ImGui.TableSetupColumn("Source");
        ImGui.TableSetupColumn("Blockers");
        ImGui.TableSetupColumn("Actions");
        ImGui.TableHeadersRow();

        foreach (var character in filtered)
        {
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
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(character.ContentId == 0 ? "-" : character.ContentId.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatRosterWorldDc(character));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{FormatRosterFreshness(character)} | {FormatTime(character.LastSnapshotUtc)}");
            ImGui.TableNextColumn();
            DrawJobLevelCell(BuildJobLevelDisplay(
                character.JobLevels,
                character.CurrentJobId,
                character.CurrentJobAbbrev,
                character.CurrentLevel));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatRosterState(character));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatRosterSource(character));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatOperatorText(FormatRosterBlockers(character), "(none)"));
            ImGui.TableNextColumn();
            DrawRosterRowActions(catalog, character, selectionKey);
        }

        ImGui.EndTable();
    }

    private void DrawCrewPresetSection(DadCharacterPool characterPool, DadVisibleRunState runState)
    {
        var plannerSnapshot = plugin.GetPlannerUiSnapshot(runState);
        var plannerPreview = plannerSnapshot.PlannerPreview;
        var requestPreview = plannerSnapshot.RequestPreview;
        var plannerLocked = IsPlannerLocked(runState);

        DrawSectionHeader("Crew Presets", "Saved presets use Active roster rows by default.");
        DrawMutedNotice("Preset and scheduler pickers use assigned Active roster rows only.");

        ImGui.BeginDisabled(plannerLocked);
        DrawPlannerGroupControls(plannerSnapshot, plugin.PlannerOptions, plannerPreview, plannerLocked, debugUi: true);
        ImGui.EndDisabled();

        var selectedGroup = plugin.GetSelectedPlannerGroup();
        ImGui.BeginDisabled(selectedGroup == null || plannerLocked);
        if (ImGui.SmallButton("Enqueue preset"))
        {
            EnqueueSelectedPreset(DadSchedulerJobType.ScheduledPreset, DadMapCrewJobMode.ManualMapReady);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Enqueue map crew"))
        {
            EnqueueSelectedPreset(DadSchedulerJobType.MapCrew, selectedGroup?.MapMode ?? DadMapCrewJobMode.ManualMapReady);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Dry-run preset"))
        {
            EnqueueSelectedPreset(DadSchedulerJobType.ScheduledPreset, DadMapCrewJobMode.ManualMapReady, dryRun: true);
        }
        ImGui.EndDisabled();

        DrawStatusRow("Request", requestPreview.StatusSummary);
        if (selectedGroup != null)
            DrawStatusRow("Selected", $"{selectedGroup.DisplayName} | {selectedGroup.Slots.Count} slot(s)");
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
        DrawSectionHeader("Active Job", "Current orchestration, worker execution, launch/load readiness, and durable results.");
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
        if (queue.ActiveJob?.JobType == DadSchedulerJobType.MapCrew)
            DrawStatusRow("Map crew", $"{queue.ActiveJob.MapMode}{(string.IsNullOrWhiteSpace(queue.ActiveJob.MapRunTemplate) ? string.Empty : $" | {queue.ActiveJob.MapRunTemplate}")}");
        DrawStatusRow("Queue", queue.Summary);
        if (!string.IsNullOrWhiteSpace(state.BlockedReason))
            DrawStatusRow("Blocker", state.BlockedReason);

        if (state.Slots.Count == 0)
        {
            DrawMutedNotice("No active scheduler slot state.");
            DrawRunHistory();
            return;
        }

        if (!ImGui.BeginTable("dad-active-scheduler-slots", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Slot");
        ImGui.TableSetupColumn("Account");
        ImGui.TableSetupColumn("Target");
        ImGui.TableSetupColumn("Active");
        ImGui.TableSetupColumn("Wake");
        ImGui.TableSetupColumn("Launch/load");
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
            ImGui.TextUnformatted($"{slot.WakePolicy} / {slot.RosterVisibility}{(slot.NeedsRosterUpdate ? " / needs update" : string.Empty)}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.LaunchStarted
                ? $"launch {FormatTime(slot.LaunchStartedUtc)}"
                : slot.LoadCommandSentUtc.HasValue
                    ? $"load {FormatTime(slot.LoadCommandSentUtc)}"
                    : FormatText(slot.LaunchProfileName, "-"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.Ready ? "ready" : slot.IsOnline ? "online" : "offline");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatOperatorText(FormatText(slot.BlockedReason, slot.Summary), "(none)"));
        }

        ImGui.EndTable();
        DrawRunHistory();
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
            DrawMergeAccountPopup(catalog);
            DrawDeleteAccountPopup(catalog);
            return;
        }

        var accountOptions = GetRosterAccountToolOptions(catalog);
        if (accountOptions.Count == 0)
        {
            ImGui.TextDisabled("No Dad roster accounts.");
            DrawMergeAccountPopup(catalog);
            DrawDeleteAccountPopup(catalog);
            DrawForgetAccountCopiesPopup(catalog);
            return;
        }

        if (!ImGui.BeginTable("dad-roster-account-tools", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            DrawMergeAccountPopup(catalog);
            DrawDeleteAccountPopup(catalog);
            DrawForgetAccountCopiesPopup(catalog);
            return;
        }

        ImGui.TableSetupColumn("Account key");
        ImGui.TableSetupColumn("Alias");
        ImGui.TableSetupColumn("Config");
        ImGui.TableSetupColumn("Rows");
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
            if (account != null)
            {
                var currentAccountId = plugin.ConfigManager.CurrentAccountId?.Trim() ?? string.Empty;
                var canMerge = !string.IsNullOrWhiteSpace(currentAccountId) &&
                               !string.Equals(currentAccountId, account.AccountId, StringComparison.OrdinalIgnoreCase);
                ImGui.BeginDisabled(!canMerge);
                if (ImGui.SmallButton($"Merge into current##dad-roster-merge-account-{accountId}"))
                {
                    pendingMergeAccountId = account.AccountId;
                    ImGui.OpenPopup("Confirm merge account##dad-roster-merge-account");
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
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
                pendingForgetAccountId = accountId;
                ImGui.OpenPopup("Confirm forget account copies##dad-roster-forget-account");
            }
        }

        ImGui.EndTable();
        DrawMergeAccountPopup(catalog);
        DrawDeleteAccountPopup(catalog);
        DrawForgetAccountCopiesPopup(catalog);
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
        pendingForgetAccountId = string.Empty;
        pendingMergeAccountId = string.Empty;
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

    private void DrawMergeAccountPopup(DadAccountRosterCatalog catalog)
    {
        if (!ImGui.BeginPopup("Confirm merge account##dad-roster-merge-account"))
            return;

        var source = plugin.ConfigManager.GetAccount(new DadAccountKey(pendingMergeAccountId));
        var target = plugin.ConfigManager.GetCurrentAccount();
        if (source == null || target == null)
        {
            ImGui.TextUnformatted("No merge source or current target account selected.");
            if (ImGui.SmallButton("Close"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        var sourceKey = new DadAccountKey(source.AccountId);
        var rowCount = catalog.Characters
            .Where(character => !character.AccountKey.IsEmpty &&
                                DadRosterIdentity.SameAccount(character.AccountKey, sourceKey))
            .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
            .Count();
        ImGui.TextWrapped($"Merge Dad account '{source.AccountAlias}' ({source.AccountId}) into current account '{target.AccountAlias}' ({target.AccountId})?");
        ImGui.TextDisabled($"Moves missing character configs and Dad roster metadata for {rowCount} row(s). Target keeps duplicate character configs. Source config is deleted. XADB snapshots stay untouched.");
        if (ImGui.SmallButton("Merge account"))
        {
            if (plugin.MergeDadAccountIntoCurrent(sourceKey))
            {
                if (MatchesRosterAccountFilterKey(source.AccountId, rosterAccountFilter))
                {
                    rosterAccountFilter = target.AccountId;
                    rosterAccountInitialized = true;
                }

                plugin.PrintStatus($"Merged Dad account '{source.AccountAlias}' into '{target.AccountAlias}'.");
            }

            pendingMergeAccountId = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Cancel"))
        {
            pendingMergeAccountId = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
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

    private void DrawForgetAccountCopiesPopup(DadAccountRosterCatalog catalog)
    {
        if (!ImGui.BeginPopup("Confirm forget account copies##dad-roster-forget-account"))
            return;

        var accountKey = new DadAccountKey(pendingForgetAccountId);
        if (accountKey.IsEmpty)
        {
            ImGui.TextUnformatted("No account selected.");
            if (ImGui.SmallButton("Close"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        var option = GetRosterAccountToolOptions(catalog)
            .FirstOrDefault(candidate => DadRosterIdentity.SameAccount(candidate.AccountKey, accountKey));
        var label = option == null ? accountKey.Value : FormatRosterAccountOption(option);
        var rowCount = catalog.Characters
            .Where(character => !character.AccountKey.IsEmpty &&
                                DadRosterIdentity.SameAccount(character.AccountKey, accountKey))
            .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
            .Count();
        ImGui.TextWrapped($"Forget local Dad roster copies for '{label}'?");
        ImGui.TextDisabled($"Removes local Dad roster metadata for {rowCount} row(s). XADB snapshots and remote Dad data stay untouched.");
        if (DrawCtrlShiftSmallButton(
                "Forget account copies",
                "dad-roster-confirm-forget-account",
                "Click to forget local Dad roster metadata for this remote account. XADB snapshots and remote Dad data stay untouched.",
                "Hold Ctrl+Shift to forget local Dad roster metadata for this remote account. XADB snapshots and remote Dad data stay untouched."))
        {
            var purged = plugin.RosterCatalogService.PurgeAccount(accountKey);
            plugin.RosterCatalogService.RefreshCatalog(plugin.CharacterIntelligenceService.CurrentPool, new DadRosterRefreshPlan
            {
                IncludeHidden = true,
                IncludeIgnored = true,
                StaleAfterHours = plugin.Configuration.RosterCatalog.StaleAfterHours,
            });
            if (MatchesRosterAccountFilterKey(accountKey.Value, rosterAccountFilter))
            {
                rosterAccountFilter = string.Empty;
                rosterAccountInitialized = true;
            }

            plugin.PrintStatus(purged
                ? $"Forgot local Dad roster copies for '{label}'. XADB snapshots and remote Dad data untouched."
                : $"No local Dad roster copies found for '{label}'.");
            pendingForgetAccountId = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Cancel"))
        {
            pendingForgetAccountId = string.Empty;
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

    private void DrawRosterRowActions(DadAccountRosterCatalog catalog, DadRosterCharacter character, string selectionKey)
    {
        if (ImGui.SmallButton($"Activate##dad-roster-active-{selectionKey}"))
            SetRosterVisibility([character], DadRosterVisibility.Active);
        ImGui.SameLine();
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
        ImGui.SameLine();
        DrawRosterAssignCombo(character, selectionKey, catalog);
        ImGui.SameLine();
        var canClear = !character.AccountKey.IsEmpty && !IsRemoteRosterRow(character);
        ImGui.BeginDisabled(!canClear);
        if (ImGui.SmallButton($"Clear assignment##dad-roster-clear-{selectionKey}"))
            ChangeRosterAssignment(new DadRosterAssignmentChangeRequest
            {
                CharacterRef = DadRosterIdentity.From(character),
                ClearAssignment = true,
                Reason = "Cleared from Crew / Scheduler browser.",
            });
        ImGui.EndDisabled();

        if (plugin.RosterCatalogService.HasLocalRosterCopy(character))
        {
            ImGui.SameLine();
            if (DrawCtrlShiftSmallButton(
                    "Forget copy",
                    $"dad-roster-forget-copy-{selectionKey}",
                    "Click to forget this local Dad roster copy. XADB snapshots and remote Dad data stay untouched.",
                    "Hold Ctrl+Shift to forget this local Dad roster copy. XADB snapshots and remote Dad data stay untouched."))
            {
                ForgetRosterCopy(character);
            }
        }
    }

    private void DrawRosterAssignCombo(DadRosterCharacter character, string selectionKey, DadAccountRosterCatalog catalog)
    {
        var localAccountKey = new DadAccountKey(plugin.Configuration.ClientAccountId);
        var localAccount = plugin.ConfigManager.GetAccount(localAccountKey);
        var localOption = GetRosterAccountOptions(catalog)
            .FirstOrDefault(option => DadRosterIdentity.SameAccount(option.AccountKey, localAccountKey));
        var canAssign = !IsRemoteRosterRow(character) && !localAccountKey.IsEmpty && (localAccount != null || localOption != null);
        ImGui.BeginDisabled(!canAssign);
        if (ImGui.BeginCombo($"##dad-roster-assign-{selectionKey}", "Assign account"))
        {
            var accountId = localAccount?.AccountId ?? localOption?.AccountKey.Value ?? string.Empty;
            var accountAlias = localAccount?.AccountAlias ?? localOption?.AccountAlias ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(accountId))
            {
                var label = string.IsNullOrWhiteSpace(accountAlias) ||
                            string.Equals(accountAlias, accountId, StringComparison.OrdinalIgnoreCase)
                    ? accountId
                    : $"{accountAlias} ({accountId})";
                if (ImGui.Selectable(label, false))
                {
                    ChangeRosterAssignment(new DadRosterAssignmentChangeRequest
                    {
                        CharacterRef = DadRosterIdentity.From(character),
                        AccountKey = new DadAccountKey(accountId),
                        AccountAlias = accountAlias,
                        Reason = "Assigned from Crew / Scheduler browser.",
                    });
                }
            }

            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
    }

    private void ChangeRosterAssignment(DadRosterAssignmentChangeRequest request)
    {
        var resultJson = plugin.ChangeRosterAssignmentFromJson(DadIpcJson.Serialize(request));
        var catalog = DadIpcJson.Deserialize<DadAccountRosterCatalog>(resultJson);
        plugin.PrintStatus(catalog?.Summary ?? "Roster assignment updated.");
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
        DadMapCrewJobMode mapMode,
        bool dryRun = false)
    {
        var selectedGroup = plugin.GetSelectedPlannerGroup();
        if (selectedGroup == null)
            return;

        var request = new DadScheduledPresetRequest
        {
            GroupId = selectedGroup.GroupId,
            DryRun = dryRun,
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
        var plannerOptions = plugin.PlannerOptions;
        var plannerSnapshot = plugin.GetPlannerUiSnapshot(runState);
        var requestPreview = plannerSnapshot.RequestPreview;
        var plannerPreview = requestPreview.PlannerPreview;
        var plannerPool = plannerSnapshot.CuratedPool;
        var plannerLocked = IsPlannerLocked(runState);
        var debugUi = plugin.Configuration.DebugUiEnabled;

        if (debugUi)
        {
            if (!ImGui.BeginTable(
                    "dad-planner-layout-v2",
                    2,
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
            {
                return;
            }

            ImGui.TableSetupColumn("Lanes", ImGuiTableColumnFlags.WidthFixed, 320f);
            ImGui.TableSetupColumn("Plan", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(plannerLocked);
            DrawPlannerLanePanel(plannerSnapshot, plannerOptions, runState, debugUi);
            ImGui.EndDisabled();

            ImGui.TableNextColumn();
        }

        if (plannerLocked)
            DrawMutedNotice("Planner locked. Dad run active. Cancel or wait for final state before editing plan.");

        DrawPlannerLaneSummarySection(plannerPreview, requestPreview, runState, debugUi);
        DrawPlannerActionStrip(requestPreview, plannerSnapshot.SchedulerPreview, runState, plannerLocked);
        DrawPlannerConfigSection(plannerSnapshot, plannerOptions, plannerPreview, requestPreview, plannerLocked, debugUi);
        DrawPlannerRosterSummarySection(plannerPreview, runState, debugUi);
        if (debugUi)
            DrawPlannerDetailsSection(plannerSnapshot, plannerOptions, plannerPreview, requestPreview, runState, plannerLocked);
        if (debugUi)
            ImGui.EndTable();
    }

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
        DrawSectionHeader("Plan", "Editable planner inputs plus lane-specific config. Read-only lanes stay explicit.");
        ImGui.BeginDisabled(plannerLocked);
        DrawPlannerGroupControls(plannerSnapshot, plannerOptions, plannerPreview, plannerLocked, debugUi);
        ImGui.Spacing();
        DrawPlannerRunFamilySelector(plannerOptions);
        ImGui.SameLine();
        DrawPlannerSubmodeSelector(plannerOptions, plannerPreview);
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

        DrawPlannerLaneInputs(plannerOptions, plannerPreview.LaneDefinition, plannerSnapshot.SelectedDuty, debugUi);
        ImGui.Spacing();
        DrawPlannerStopPolicyControls(plannerOptions, plannerPreview, requestPreview);
        ImGui.Spacing();
        DrawPlannerCompletionActionsControls(plannerOptions, requestPreview);
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
        bool plannerLocked)
    {
        DrawSectionHeader("Planner Action", "Startability, blocker, and active-run controls for the selected lane.");
        var activeRun = GetActiveRun(runState);
        var blockers = BuildPlannerBlockerList(requestPreview);
        var firstBlocker = blockers.FirstOrDefault() ?? "(none)";
        var disabledReason = plannerLocked
            ? "A Dad run is active. Cancel it or wait for the run to reach a final state before starting another planner request."
            : requestPreview.CanStart
                ? string.Empty
                : firstBlocker == "(none)"
                    ? FormatText(requestPreview.StatusSummary, "Planner request is not startable.")
                    : firstBlocker;

        ImGui.BeginDisabled(plannerLocked || !requestPreview.CanStart);
        if (ImGui.SmallButton("Start planner run"))
            plugin.StartPlannerRunFromShell();
        var startHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        ImGui.EndDisabled();
        if (startHovered && !string.IsNullOrWhiteSpace(disabledReason))
            ImGui.SetTooltip(disabledReason);

        ImGui.SameLine();
        ImGui.BeginDisabled(plannerLocked || !schedulerPreview.CanStart || !requestPreview.PlannerPreview.UsingPlannerGroup);
        if (ImGui.SmallButton("Start scheduler preset"))
        {
            var resultJson = plugin.StartSchedulerPresetFromJson(DadIpcJson.Serialize(new DadSchedulerStartRequest
            {
                GroupId = requestPreview.PlannerPreview.SelectedPlannerGroupId,
            }));
            var result = DadIpcJson.Deserialize<DadRunResult>(resultJson);
            plugin.PrintStatus(result?.Summary ?? "Scheduler preset start requested.");
        }
        var schedulerHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        ImGui.EndDisabled();
        if (schedulerHovered && (!schedulerPreview.CanStart || !requestPreview.PlannerPreview.UsingPlannerGroup))
            ImGui.SetTooltip(requestPreview.PlannerPreview.UsingPlannerGroup
                ? schedulerPreview.BlockedReason
                : "Select a saved preset before using the scheduler.");

        if (plannerLocked)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel active run##planner-action-strip"))
                plugin.CancelActiveRunFromShell();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Cancels the active Dad run visible to this client.");
        }

        ImGui.SameLine();
        DrawStateBadge("Startability", FormatText(requestPreview.ContractPreview.Startability, requestPreview.CanStart ? "Startable" : "Blocked"));
        ImGui.SameLine();
        DrawStateBadge("Blockers", blockers.Count.ToString(CultureInfo.InvariantCulture));
        ImGui.SameLine();
        DrawStateBadge("Active", activeRun.Status == DadRunStatus.Idle
            ? "Idle"
            : $"{activeRun.ModuleId} / {DadOperatorPhaseText.FormatPhaseLabel(activeRun)}");
        ImGui.SameLine();
        DrawStateBadge("Scheduler", schedulerPreview.ReadyToStart ? "Ready" : schedulerPreview.CanStart ? "Can wake" : "Blocked");

        if (firstBlocker != "(none)")
            DrawStatusRow("First blocker", firstBlocker);
        if (plugin.Configuration.DebugUiEnabled)
        {
            DrawStatusRow("Start reason", requestPreview.CanStart ? FormatText(requestPreview.StatusSummary, "Planner request ready.") : disabledReason);
            DrawStatusRow("Stop policy", requestPreview.StopPolicy.Describe());
        }
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
        DrawStatusRow("Preview blockers", requestPreview.ContractPreview.Blockers.Count == 0
            ? "(none)"
            : FormatOperatorText(string.Join(" | ", requestPreview.ContractPreview.Blockers), "(none)"));
        DrawPlannerValidation(plannerPreview, requestPreview);
        ImGui.Spacing();
        DrawPlannerFilterCounts(plannerPreview);
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
            DadPlannerActivityMode.Msq => run.ModuleId is DadModuleId.Msq or DadModuleId.DailyMsq,
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

    private string BuildOverviewNextAction(DadVisibleRunState runState, CharacterConfig profile)
    {
        var activeRun = GetActiveRun(runState);
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
    {
        ImGui.Separator();
        ImGui.TextUnformatted(title);
        if (plugin.Configuration.DebugUiEnabled && !string.IsNullOrWhiteSpace(subtitle))
            ImGui.TextDisabled(subtitle);
    }

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
    }

    private void DrawPlannerStopPolicyControls(
        DadPresetPlannerOptions plannerOptions,
        DadActivityPreset plannerPreview,
        DadPlannerRunRequestPreview requestPreview)
    {
        var stopPolicy = plannerOptions.StopPolicy ??= new DadRunStopPolicy();
        stopPolicy.Normalize();
        DrawStatusRow("Selected subject", FormatText(plannerPreview.StopPolicy.TargetCharacterKey.ToString(), "(selected character pending)"));

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

        DrawStatusRow("Policy preview", requestPreview.StopPolicy.Describe());
    }

    private void DrawPlannerCompletionActionsControls(
        DadPresetPlannerOptions plannerOptions,
        DadPlannerRunRequestPreview requestPreview)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Completion actions");
        DrawStatusRow("Completion source", DadCompletionActionSnapshots.DescribeSource(plannerOptions.CompletionActions));

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
            DrawStatusRow("Effective actions", BuildCompletionActionsSummary(requestPreview.ContractPreview.CompletionActions ?? plugin.Configuration.CompletionActions));
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
                var committedSignature = BuildPlannerCompletionActionSignature(actions);
                actions.Commands = draftPlannerCompletionCommands
                    .Split('\n')
                    .Select(static command => command.Trim())
                    .Where(static command => command.Length > 0)
                    .ToList();
                plugin.QueueDebouncedPlannerOptionsSave(
                    "planner-completion-commands",
                    committedSignature,
                    () => BuildPlannerCompletionActionSignature(plannerOptions.CompletionActions));
            }
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
                utilities.GrandCompanyHandInCommand = gcCommand.Trim();
                plugin.SavePlannerOptions();
            }
        }

        if (plugin.Configuration.AdvancedModeEnabled)
        {
            var killMode = (int)actions.KillMode;
            if (ImGui.Combo("Preset completion action (DANGER)", ref killMode, CompletionKillModes, CompletionKillModes.Length))
            {
                actions.KillMode = (DadCompletionKillMode)Math.Clamp(killMode, 0, CompletionKillModes.Length - 1);
                plugin.SavePlannerOptions();
            }
        }
        else if (actions.KillMode != DadCompletionKillMode.None)
        {
            DrawStatusRow("Preset kill action", $"{actions.KillMode} configured but hidden; enable Advanced mode (/dad advanced) to view/change.");
        }

        DrawStatusRow("Effective actions", BuildCompletionActionsSummary(actions));
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
            enabled.Add(actions.KillMode.ToString());

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
                if (ImGui.BeginChild($"dad-duty-results-{lane.ActivityMode}", new Vector2(popupContentWidth, 220f), true))
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

                    ImGui.EndChild();
                }

                ImGui.EndCombo();
            }

            if (selectedDuty != null)
            {
                DrawStatusRow("Selected duty", dutyCompatible
                    ? selectedDuty.SelectionLabel
                    : $"Incompatible with {lane.DisplayName}: {selectedDuty.SelectionLabel}");
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
                DrawStatusRow("Execution mode", lane.ActivityMode == DadPlannerActivityMode.DutySupport ? "DutySupportOnly" : "TrustOnly");
                DrawStatusRow("Runner count", "1 local runner");
                if (debugUi)
                    DrawStatusRow("Request shape", "Solo local lane. Preview forces one local runner and local queue authority.");
            }
            else if (lane.ActivityMode == DadPlannerActivityMode.LocalDuty)
            {
                DrawStatusRow("Execution mode", "Regular Duty Finder");
                DrawStatusRow("Run count", "1");
                DrawStatusRow("Frequency", DadRunRequestOptions.FrequencyPerArRun);
                if (debugUi)
                    DrawStatusRow("Request shape", "Local duty contract. Preview stays one runner; synced/unsynced applies only to this local lane.");
            }
            else if (lane.ActivityMode == DadPlannerActivityMode.CustomDuty)
            {
                DrawStatusRow("Attempts", "1");
                if (debugUi)
                    DrawStatusRow("Request shape", "Typed custom duty contract. Planner keeps this lane local-only for now.");
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

            DrawStatusRow("Attempts", "1");
            if (debugUi)
                DrawStatusRow("MOGTOME preview", "Dad owns request preview. Policy controls helper handoff shape only.");
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Blunderville)
        {
            DrawStatusRow("Attempts", "1");
            DrawStatusRow("Blunderville mode", "FixedEmoteRun");
            if (debugUi)
            {
                DrawStatusRow("Queue owner", plugin.PresetProviderService.GetQueueAuthorityLabel(lane.DefaultQueueAuthority));
                DrawStatusRow("Blunderville policy", "Dad enters, runs configured per-character emote, then fail/leaves per fixed contract.");
            }
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Msq)
        {
            DrawStatusRow("Preset", "MSQ");
            DrawStatusRow("Attempts", "1");
            DrawStatusRow("Expected party size", lane.ExpectedPartySize.ToString(CultureInfo.InvariantCulture));
            if (debugUi)
                DrawStatusRow("MSQ mapping", "Planner surfaces MSQ lane while preserving DailyMsqPremade legacy queue mapping in preview.");
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Commendation)
        {
            DrawStatusRow("Attempts", "1");
            if (debugUi)
            {
                DrawStatusRow("Queue lane", plugin.PresetProviderService.GetQueueAuthorityLabel(lane.DefaultQueueAuthority));
                DrawStatusRow("Commendation policy", "Short duty loop contract. Preview keeps attempt count and queue lane explicit.");
            }
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Astrope)
        {
            DrawStatusRow("Attempts", "1");
            DrawStatusRow("Valid local time window", new DadTimeWindow().Describe());
            if (debugUi)
            {
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
            : submodes.FirstOrDefault() ?? plugin.PresetProviderService.GetPlannerLaneDefinition(DadPlannerActivityMode.Msq);
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
            DadPlannerActivityMode.DailyMsqPremade => DadPlannerActivityMode.Msq,
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
    {
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var labelWidth = MathF.Min(
            MathF.Max(84f, preferredLabelWidth),
            MathF.Max(84f, availableWidth * 0.36f));

        ImGui.TextDisabled(label);
        if (availableWidth > labelWidth + 120f)
        {
            ImGui.SameLine(labelWidth);
            ImGui.TextWrapped(value);
        }
        else
        {
            ImGui.Indent();
            ImGui.TextWrapped(value);
            ImGui.Unindent();
        }
    }

    private static void DrawStateBadge(string label, string value)
    {
        ImGui.TextDisabled($"{label}:");
        ImGui.SameLine();
        ImGui.TextUnformatted(value);
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

    private static string GetWakePolicyLabel(DadSchedulerWakePolicy policy)
        => policy switch
        {
            DadSchedulerWakePolicy.LaunchIfOffline => "Launch if offline",
            DadSchedulerWakePolicy.LoadCharacterIfOnline => "Load character",
            _ => "Already online",
        };

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

    private void DrawPlannerGroupControls(
        DadPlannerUiSnapshot plannerSnapshot,
        DadPresetPlannerOptions plannerOptions,
        DadActivityPreset plannerPreview,
        bool plannerLocked,
        bool debugUi)
    {
        var selectedGroup = plugin.GetSelectedPlannerGroup();
        var duplicateNames = plannerSnapshot.PlannerGroups
            .GroupBy(static group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preview = selectedGroup == null
            ? "Auto roster"
            : FormatPlannerGroupChoice(selectedGroup.DisplayName, selectedGroup.GroupId, duplicateNames);
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

        ImGui.SetNextItemWidth(MathF.Min(280f, ImGui.GetContentRegionAvail().X));
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
            }
        }
        ImGui.EndDisabled();

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

        selectedGroup = plugin.GetSelectedPlannerGroup();
        if (selectedGroup == null)
        {
            DrawStatusRow("Roster source", "Auto roster from filtered pool.");
            return;
        }

        DrawStatusRow(
            "Selected preset",
            $"{FormatPlannerGroupChoice(selectedGroup.DisplayName, selectedGroup.GroupId, duplicateNames)} | {DadPlannerSlotRules.CountPrimarySlots(selectedGroup.Slots)} slot(s)");
        DrawStatusRow("Preset submode", plugin.PresetProviderService.GetPlannerLaneDefinition(selectedGroup.ActivityMode).DisplayName);

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

        DrawPlannerGroupScheduleControls(selectedGroup);
        if (debugUi && ImGui.SmallButton("Refresh group slots from current planner"))
        {
            plugin.ReplaceSelectedPlannerGroupSlotsFromCurrentPreview();
            plugin.PrintStatus($"Updated preset '{selectedGroup.DisplayName}' slots from current preview.");
        }

        if (debugUi)
            ImGui.SameLine();
        var slotCap = ResolvePlannerGroupSlotCap(selectedGroup, plannerSnapshot.SelectedDuty);
        var nextSlotNumber = DadPlannerSlotRules.NextPrimarySlotNumber(selectedGroup.Slots, slotCap);
        ImGui.BeginDisabled(nextSlotNumber == 0);
        if (ImGui.SmallButton("Add empty slot"))
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
                ? $"All Slot1-Slot{slotCap.ToString(CultureInfo.InvariantCulture)} rows already exist."
                : "Adds the next generated SlotN row.");
        ImGui.EndDisabled();

        DrawPlannerGroupSlotEditor(plannerSnapshot, selectedGroup);
    }

    private int ResolvePlannerGroupSlotCap(DadPlannerGroup group, DadPlannerDutyOption? selectedDuty)
    {
        var lane = plugin.PresetProviderService.GetPlannerLaneDefinition(group.ActivityMode);
        if (!lane.RequiresDutySelector)
            return DadPlannerSlotRules.MaxSlotNumber;

        var fallbackSize = group.DutyExpectedPartySize > 0
            ? group.DutyExpectedPartySize
            : selectedDuty?.QueueSize ?? lane.ExpectedPartySize;
        return Math.Clamp(fallbackSize <= 0 ? 1 : fallbackSize, DadPlannerSlotRules.MinSlotNumber, DadPlannerSlotRules.MaxSlotNumber);
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

    private void DrawPlannerGroupSlotEditor(DadPlannerUiSnapshot plannerSnapshot, DadPlannerGroup group)
    {
        group.Slots = DadPlannerSlotRules.NormalizeGroupSlots(group.Slots);
        if (!ImGui.BeginTable("dad-planner-group-slots", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Slot");
        ImGui.TableSetupColumn("Type");
        ImGui.TableSetupColumn("Role");
        ImGui.TableSetupColumn("Account");
        ImGui.TableSetupColumn("Character");
        ImGui.TableSetupColumn("Wake");
        ImGui.TableSetupColumn("Profile");
        ImGui.TableSetupColumn("Edit");
        ImGui.TableHeadersRow();

        for (var index = 0; index < group.Slots.Count; index++)
        {
            var slot = group.Slots[index];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.SlotId);
            if (DadPlannerSlotRules.IsLeaderSlot(slot.SlotId) && !slot.IsSubstitute && ImGui.IsItemHovered())
                ImGui.SetTooltip("Slot1 is the party leader and inviter for this preset.");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.IsSubstitute ? "Substitute" : "Primary");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(slot.IsSubstitute
                    ? "Substitute rows are tried only if the primary row for this same SlotN does not resolve; rows are tried in UI order."
                    : "Primary rows are resolved before substitute rows for the same SlotN.");

            ImGui.TableNextColumn();
            DrawPlannerGroupRoleCombo(group, slot, index);

            ImGui.TableNextColumn();
            DrawPlannerGroupAccountCombo(plannerSnapshot.CuratedPool, plannerSnapshot.AccountOptions, group, slot, index);

            ImGui.TableNextColumn();
            DrawPlannerGroupCharacterCombo(plannerSnapshot, group, slot, index);

            ImGui.TableNextColumn();
            DrawPlannerGroupWakePolicyCombo(group, slot, index);

            ImGui.TableNextColumn();
            DrawPlannerGroupLaunchProfileCombo(plannerSnapshot.LaunchProfiles, group, slot, index);

            ImGui.TableNextColumn();
            if (!slot.IsSubstitute)
            {
                if (ImGui.SmallButton($"+ Sub##dad-group-slot-sub-{index}"))
                {
                    group.Slots.Insert(FindSubstituteInsertIndex(group.Slots, index), new DadPlannerGroupSlot
                    {
                        SlotId = slot.SlotId,
                        IsSubstitute = true,
                        RequiredRole = slot.RequiredRole,
                        WakePolicy = slot.WakePolicy,
                        AllowSubstitution = false,
                    });
                    plugin.TouchPlannerGroup(group);
                    break;
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Adds an explicit fallback row for this same SlotN. Primary rows are tried first, then substitute rows in UI order.");

                ImGui.SameLine();
            }

            if (ImGui.SmallButton($"Remove##dad-group-slot-remove-{index}"))
            {
                group.Slots.RemoveAt(index);
                plugin.TouchPlannerGroup(group);
                break;
            }
        }

        ImGui.EndTable();
    }

    private static int FindSubstituteInsertIndex(IReadOnlyList<DadPlannerGroupSlot> slots, int primaryIndex)
    {
        var primarySlotId = slots[primaryIndex].SlotId;
        var insertIndex = primaryIndex + 1;
        while (insertIndex < slots.Count &&
               string.Equals(slots[insertIndex].SlotId, primarySlotId, StringComparison.OrdinalIgnoreCase) &&
               slots[insertIndex].IsSubstitute)
        {
            insertIndex++;
        }

        return insertIndex;
    }

    private void DrawPlannerGroupWakePolicyCombo(DadPlannerGroup group, DadPlannerGroupSlot slot, int index)
    {
        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.BeginCombo($"##dad-group-wake-{index}", GetWakePolicyLabel(slot.WakePolicy)))
            return;

        foreach (var policy in Enum.GetValues<DadSchedulerWakePolicy>())
        {
            var selected = policy == slot.WakePolicy;
            if (ImGui.Selectable(GetWakePolicyLabel(policy), selected))
            {
                slot.WakePolicy = policy;
                plugin.TouchPlannerGroup(group);
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawPlannerGroupLaunchProfileCombo(
        IReadOnlyList<DadLaunchProfile> profiles,
        DadPlannerGroup group,
        DadPlannerGroupSlot slot,
        int index)
    {
        var selectedProfile = profiles.FirstOrDefault(profile =>
            string.Equals(profile.ProfileId, slot.LaunchProfileId, StringComparison.OrdinalIgnoreCase));
        var preview = selectedProfile?.DisplayName ?? "Auto";
        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.BeginCombo($"##dad-group-launch-profile-{index}", preview))
            return;

        if (ImGui.Selectable("Auto", string.IsNullOrWhiteSpace(slot.LaunchProfileId)))
        {
            slot.LaunchProfileId = string.Empty;
            plugin.TouchPlannerGroup(group);
        }

        foreach (var profile in profiles)
        {
            var selected = string.Equals(profile.ProfileId, slot.LaunchProfileId, StringComparison.OrdinalIgnoreCase);
            var label = string.IsNullOrWhiteSpace(profile.AccountKey.Value)
                ? profile.DisplayName
                : $"{profile.DisplayName} | {profile.AccountKey}";
            if (ImGui.Selectable(label, selected))
            {
                slot.LaunchProfileId = profile.ProfileId;
                plugin.TouchPlannerGroup(group);
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawPlannerGroupRoleCombo(DadPlannerGroup group, DadPlannerGroupSlot slot, int index)
    {
        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.BeginCombo($"##dad-group-role-{index}", FormatRoleRequirement(slot.RequiredRole)))
            return;

        foreach (var role in Enum.GetValues<DadPartyRole>())
        {
            var selected = role == slot.RequiredRole;
            if (ImGui.Selectable(FormatRoleRequirement(role), selected))
            {
                slot.RequiredRole = role;
                plugin.TouchPlannerGroup(group);
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawPlannerGroupAccountCombo(
        DadCharacterPool characterPool,
        IReadOnlyList<DadRosterAccountOption> accountOptions,
        DadPlannerGroup group,
        DadPlannerGroupSlot slot,
        int index)
    {
        var selectedAccount = accountOptions.FirstOrDefault(option =>
            string.Equals(option.AccountKey.Value, slot.RequiredAccountKey.Value, StringComparison.OrdinalIgnoreCase));
        var preview = slot.RequiredAccountKey.IsEmpty
            ? "(missing)"
            : selectedAccount == null ? slot.RequiredAccountKey.Value : FormatRosterAccountOption(selectedAccount);
        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.BeginCombo($"##dad-group-account-{index}", preview))
            return;

        foreach (var option in accountOptions)
        {
            var selected = string.Equals(slot.RequiredAccountKey.Value, option.AccountKey.Value, StringComparison.OrdinalIgnoreCase);
            var label = $"{FormatRosterAccountOption(option)} ({option.AssignedCharacterCount})";
            if (ImGui.Selectable(label, selected))
            {
                slot.RequiredAccountKey = option.AccountKey;
                if (!slot.RequiredCharacterKey.IsEmpty &&
                    !characterPool.Characters.Any(character =>
                        string.Equals(character.CharacterKey, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase) &&
                        MatchesPlannerGroupAccount(character, option.AccountKey)))
                {
                    slot.RequiredCharacterKey = new DadCharacterKey(string.Empty);
                }

                plugin.TouchPlannerGroup(group);
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawPlannerGroupCharacterCombo(
        DadPlannerUiSnapshot plannerSnapshot,
        DadPlannerGroup group,
        DadPlannerGroupSlot slot,
        int index)
    {
        var needsAccount = slot.RequiredAccountKey.IsEmpty;
        var preview = needsAccount
            ? "Select account first"
            : slot.RequiredCharacterKey.IsEmpty
            ? "Any character"
            : FormatOperatorCharacterKey(slot.RequiredCharacterKey.Value, slot.RequiredCharacterKey.Value);
        ImGui.SetNextItemWidth(-1f);
        ImGui.BeginDisabled(needsAccount);
        if (ImGui.BeginCombo($"##dad-group-character-{index}", preview))
        {
            if (ImGui.Selectable("Any character on account", slot.RequiredCharacterKey.IsEmpty))
            {
                slot.RequiredCharacterKey = new DadCharacterKey(string.Empty);
                plugin.TouchPlannerGroup(group);
            }

            foreach (var character in plannerSnapshot.GetCharactersForAccount(slot.RequiredAccountKey))
            {
                var selected = string.Equals(slot.RequiredCharacterKey.Value, character.CharacterKey, StringComparison.OrdinalIgnoreCase) &&
                               MatchesPlannerGroupAccount(character, slot.RequiredAccountKey);
                var source = plugin.PresetProviderService.GetCharacterSourceLabel(character.Source);
                var world = string.IsNullOrWhiteSpace(character.WorldName) ? string.Empty : $" | {character.WorldName}";
                var label = $"{FormatOperatorCharacterKey(character.CharacterKey, character.CharacterKey)}{world} | {source}";
                if (ImGui.Selectable(label, selected))
                {
                    slot.RequiredCharacterKey = new DadCharacterKey(character.CharacterKey);
                    plugin.TouchPlannerGroup(group);
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
    }

    private static bool MatchesPlannerGroupAccount(DadAcquiredCharacter character, DadAccountKey accountKey)
        => (!string.IsNullOrWhiteSpace(character.AccountId)
            && string.Equals(character.AccountId, accountKey.Value, StringComparison.OrdinalIgnoreCase))
           || (!string.IsNullOrWhiteSpace(character.AccountAlias)
               && string.Equals(character.AccountAlias, accountKey.Value, StringComparison.OrdinalIgnoreCase));

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
