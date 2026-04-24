using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using dad.Models;
using dad.Services;

namespace dad.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private static readonly Vector2 MinimumWindowSize = new(760f, 600f);
    private readonly Plugin plugin;
    private Vector2? pendingPosition;
    private bool resetPositionConditionNextDraw;
    private string plannerDutySearch = string.Empty;

    private sealed record PlannerLaneCardView(
        DadPlannerLaneDefinition Lane,
        bool IsSelected,
        string MaturityLabel,
        string PartySizeLabel,
        string StartabilityLabel,
        string FirstBlockerLabel,
        int BlockerCount,
        string RuntimeLabel);

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
        var profile = plugin.ConfigManager.GetActiveConfig();
        var runState = plugin.GetVisibleRunState();
        var localRun = runState.LocalRun;
        var authorityRun = runState.AuthorityRun;
        var characterPool = plugin.CharacterIntelligenceService.CurrentPool;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

        ImGui.Text($"{PluginInfo.DisplayName} v{version}");
        ImGui.SameLine(MathF.Max(0f, ImGui.GetWindowWidth() - 150f));
        if (ImGui.SmallButton("\u2661 Ko-fi \u2661"))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.SupportUrl, UseShellExecute = true });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Support development on Ko-fi");

        ImGui.Separator();

        var pluginEnabled = configuration.PluginEnabled;
        if (ImGui.Checkbox("Plugin enabled", ref pluginEnabled))
            plugin.SetPluginEnabled(pluginEnabled, printStatus: false);

        ImGui.SameLine();
        var dtrEnabled = configuration.DtrBarEnabled;
        if (ImGui.Checkbox("DTR Bar", ref dtrEnabled))
        {
            configuration.DtrBarEnabled = dtrEnabled;
            configuration.Save();
            plugin.UpdateDtrBar();
        }

        ImGui.SameLine();
        var profileEnabled = profile.Enabled;
        if (ImGui.Checkbox("Profile armed", ref profileEnabled))
        {
            profile.Enabled = profileEnabled;
            plugin.ConfigManager.SaveCurrentAccount();
            plugin.UpdateDtrBar();
        }

        ImGui.SameLine();
        var allowIpcStarts = profile.AllowIpcStarts;
        if (ImGui.Checkbox("Allow IPC starts", ref allowIpcStarts))
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

        ImGui.SameLine();
        if (ImGui.SmallButton("Settings"))
            plugin.ToggleConfigUi();

        ImGui.SameLine();
        if (ImGui.SmallButton("Status to chat"))
            plugin.PrintStatusReport();

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

        DrawDemoButton("Run local demo", canStartLocalDemo, plugin.StartLocalDemoRunFromShell);

        ImGui.SameLine();
        DrawDemoButton("Run server demo", canStartRemoteDemo, plugin.StartServerDemoRunFromShell);

        ImGui.SameLine();
        DrawDemoButton("Run Daily MSQ demo", canStartRemoteDemo, plugin.StartDailyMsqDemoRunFromShell);

        ImGui.SameLine();
        DrawDemoButton("Run commend demo", canStartRemoteDemo, plugin.StartCommendationDemoRunFromShell);

        ImGui.SameLine();
        ImGui.BeginDisabled(!Plugin.IsBusy(localRun) && !Plugin.IsBusy(authorityRun));
        if (ImGui.SmallButton("Cancel active run"))
            plugin.CancelActiveRunFromShell();
        ImGui.EndDisabled();

        ImGui.TextWrapped(PluginInfo.Summary);
        DrawStatusRow("Krangle", plugin.KrangleService.BuildStatus(characterPool));
        DrawStatusRow("Character pool", characterPool.LastSummary);
        DrawStatusRow("XADB", characterPool.XadbStatus.LastStatus);
        DrawStatusRow("Peer transport", characterPool.PeerTransport.LastRequestStatus);

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

    private void DrawPresetPlannerTab(DadCharacterPool characterPool, DadVisibleRunState runState)
    {
        var plannerOptions = plugin.PlannerOptions;
        var requestPreview = plugin.BuildPlannerRunRequestPreview();
        var plannerPreview = requestPreview.PlannerPreview;
        var plannerLocked = IsPlannerLocked(runState);

        if (!ImGui.BeginTable(
                "dad-planner-layout-v2",
                2,
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
            return;

        ImGui.TableSetupColumn("Lanes", ImGuiTableColumnFlags.WidthFixed, 320f);
        ImGui.TableSetupColumn("Plan", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.BeginDisabled(plannerLocked);
        DrawPlannerLanePanel(characterPool, plannerOptions, requestPreview, runState);
        ImGui.EndDisabled();

        ImGui.TableNextColumn();
        if (plannerLocked)
            DrawMutedNotice("Planner locked. Dad run active. Cancel or wait for final state before editing plan.");

        DrawPlannerLaneSummarySection(plannerPreview, requestPreview, runState);
        DrawPlannerActionStrip(requestPreview, runState, plannerLocked);
        DrawPlannerConfigSection(characterPool, plannerOptions, plannerPreview, plannerLocked);
        DrawPlannerRosterSummarySection(plannerPreview, runState);
        DrawPlannerDetailsSection(plannerOptions, plannerPreview, requestPreview, runState, plannerLocked);
        ImGui.EndTable();
    }

    private void DrawPlannerLanePanel(
        DadCharacterPool characterPool,
        DadPresetPlannerOptions plannerOptions,
        DadPlannerRunRequestPreview requestPreview,
        DadVisibleRunState runState)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(1f, 1f, 1f, 0.03f));
        if (!ImGui.BeginChild("dad-planner-lane-rail", new Vector2(0f, 0f), true))
        {
            ImGui.EndChild();
            ImGui.PopStyleColor();
            return;
        }

        ImGui.TextUnformatted("Planner lanes");
        ImGui.TextDisabled("Operator lane summary cards");
        ImGui.Separator();

        foreach (var lane in plugin.PresetProviderService.GetPlannerLaneDefinitions())
        {
            var laneCard = BuildPlannerLaneCard(characterPool, plannerOptions, requestPreview, runState, lane);
            var accent = ParseHexColor(lane.AccentColorHex, laneCard.IsSelected ? 0.95f : 0.62f);
            var hovered = ParseHexColor(lane.AccentColorHex, 0.82f);
            var active = ParseHexColor(lane.AccentColorHex, 1f);
            ImGui.PushStyleColor(ImGuiCol.Button, accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
            if (ImGui.Button($"{lane.DisplayName}##dad-lane-{lane.ActivityMode}", new Vector2(-1f, 54f)))
                SelectPlannerLane(plannerOptions, lane);
            ImGui.PopStyleColor(3);

            DrawCompactStatusRow("Maturity", laneCard.MaturityLabel);
            DrawCompactStatusRow("Startability", laneCard.StartabilityLabel);
            DrawCompactStatusRow("Party", laneCard.PartySizeLabel);
            DrawCompactStatusRow("Blockers", BuildShortBlockerSummary(laneCard.FirstBlockerLabel, laneCard.BlockerCount));
            DrawCompactStatusRow("Runtime", laneCard.RuntimeLabel);
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawPlannerConfigSection(
        DadCharacterPool characterPool,
        DadPresetPlannerOptions plannerOptions,
        DadActivityPreset plannerPreview,
        bool plannerLocked)
    {
        DrawSectionHeader("Lane Config", "Editable planner inputs plus lane-specific config. Read-only lanes stay explicit.");
        ImGui.BeginDisabled(plannerLocked);
        DrawPlannerOperatorModeSelector(plannerOptions);
        ImGui.SameLine();
        DrawPlannerAccountFilterSelector(characterPool, plannerOptions);

        DrawPlannerTransportOwnerSelector(plannerOptions);
        ImGui.SameLine();
        DrawPlannerQueueAuthoritySelector(plannerOptions);
        ImGui.SameLine();
        DrawPlannerInviteAuthoritySelector(plannerOptions);

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
        DrawPlannerLaneInputs(plannerOptions, plannerPreview.LaneDefinition);
        ImGui.EndDisabled();
    }

    private void DrawPlannerLaneSummarySection(
        DadActivityPreset plannerPreview,
        DadPlannerRunRequestPreview requestPreview,
        DadVisibleRunState runState)
    {
        DrawSectionHeader("Lane Summary", "Selected lane summary, validation state, and current runtime phase.");
        var laneRun = ResolveLaneRuntime(runState, plannerPreview.LaneDefinition);
        var activeRun = GetActiveRun(runState);
        DrawStatusRow("Lane", $"{plannerPreview.LaneDefinition.DisplayName} | {plannerPreview.LaneDefinition.MaturityLabel}");
        DrawStatusRow("Lane summary", plannerPreview.LaneDefinition.Summary);
        DrawStatusRow("Next action", plannerPreview.LaneDefinition.NextAction);
        DrawStatusRow("Preset", plannerPreview.DisplayName);
        DrawStatusRow("Operator mode", plannerPreview.OperatorModeLabel);
        DrawStatusRow("Transport", plugin.PresetProviderService.GetTransportOwnerLabel(plannerPreview.TransportOwner));
        DrawStatusRow("Queue authority", plugin.PresetProviderService.GetQueueAuthorityLabel(plannerPreview.QueueAuthority));
        DrawStatusRow("Invite owner", plugin.PresetProviderService.GetInviteAuthorityLabel(plannerPreview.InviteAuthority));
        DrawStatusRow("Account filter", FormatOperatorText(plannerPreview.AccountFilterSummary, "(none)"));
        DrawStatusRow("Roster source", plugin.PresetProviderService.GetRosterSourceLabel(plannerPreview.RosterSource));
        DrawStatusRow("Leader", FormatOperatorText(plannerPreview.LeaderStatusText, "(none)"));
        DrawStatusRow("Preview scope", plannerPreview.PreviewScope);
        DrawStatusRow("Validation", $"{FormatReadiness(plannerPreview.ValidationState)} | {plannerPreview.ValidationSummary}");
        DrawStatusRow("Filters", plannerPreview.FilterSummary);
        DrawStatusRow("Summary", FormatOperatorText(plannerPreview.PlannerSummary, "(none)"));
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

        DrawStatusRow("Local-only mode", plugin.Configuration.LocalOnlyModeEnabled ? "Enabled" : "Disabled");
        DrawStatusRow("Planner request", requestPreview.StatusSummary);
    }

    private void DrawPlannerActionStrip(
        DadPlannerRunRequestPreview requestPreview,
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

        DrawStatusRow("First blocker", firstBlocker);
        DrawStatusRow("Start reason", requestPreview.CanStart ? FormatText(requestPreview.StatusSummary, "Planner request ready.") : disabledReason);
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

    private void DrawPlannerRosterSummarySection(DadActivityPreset plannerPreview, DadVisibleRunState runState)
    {
        DrawSectionHeader("Planned Roster", "Compact selected-slot and candidate summary. Full tables are in details.");
        var totalSlots = plannerPreview.SelectedCharacters.Count;
        var assignedSlots = plannerPreview.SelectedCharacters.Count(static slot => !string.IsNullOrWhiteSpace(slot.CharacterKey));
        var blockedSlots = plannerPreview.SelectedCharacters.Count(static slot => !string.IsNullOrWhiteSpace(slot.BlockerSummary));
        var readySlots = plannerPreview.SelectedCharacters.Count(static slot => slot.SelectedReadiness == DadReadinessState.Ready);
        var laneRun = ResolveLaneRuntime(runState, plannerPreview.LaneDefinition);

        DrawStatusRow("Slots", $"{assignedSlots}/{Math.Max(1, totalSlots)} assigned | {readySlots} ready | {blockedSlots} with blockers");
        DrawStatusRow("Candidates", $"{plannerPreview.AvailableCharacters.Count} available | {plannerPreview.FilterSummary}");
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
        DadPresetPlannerOptions plannerOptions,
        DadActivityPreset plannerPreview,
        DadPlannerRunRequestPreview requestPreview,
        DadVisibleRunState runState,
        bool plannerLocked)
    {
        DrawSectionHeader("Details", "Collapsed validation, JSON, runtime, roster tables, and debug actions.");

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
            DrawStatusRow("Executor", FormatExecutorStatus(laneRun.CurrentExecutorStatus));
            DrawStatusRow("Active task", string.IsNullOrWhiteSpace(laneRun.ActiveTaskName) ? "(none)" : $"{laneRun.ActiveTaskIndex}/{Math.Max(1, laneRun.TotalTaskCount)} {laneRun.ActiveTaskName}");
            DrawStatusRow("Task detail", FormatText(laneRun.ActiveTaskStatus, laneRun.Summary));
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
            plugin.PrintStatus(plugin.BuildPlannerSummary());

        ImGui.SameLine();
        if (ImGui.SmallButton("Copy planner summary"))
        {
            ImGui.SetClipboardText(plugin.BuildPlannerSummary());
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
            ImGui.SetClipboardText(plugin.BuildPlannerRequestJson());
            plugin.PrintStatus("Copied dad planner request JSON.");
        }
        ImGui.EndDisabled();

        ImGui.BeginDisabled(plannerLocked);
        if (ImGui.SmallButton("Load Local Sastasha test"))
            LoadPlannerTestDuty(plannerOptions, DadPlannerActivityMode.LocalDuty);
        ImGui.SameLine();
        if (ImGui.SmallButton("Load Duty Support Sastasha test"))
            LoadPlannerDutySupportTest(plannerOptions);
        ImGui.EndDisabled();
    }

    private PlannerLaneCardView BuildPlannerLaneCard(
        DadCharacterPool characterPool,
        DadPresetPlannerOptions plannerOptions,
        DadPlannerRunRequestPreview selectedRequestPreview,
        DadVisibleRunState runState,
        DadPlannerLaneDefinition lane)
    {
        var selected = IsSelectedPlannerLane(plannerOptions.ActivityMode, lane.ActivityMode);
        var laneOptions = selected
            ? plannerOptions
            : ClonePlannerOptionsForLane(plannerOptions, lane);
        var lanePreview = selected
            ? selectedRequestPreview.PlannerPreview
            : plugin.PresetProviderService.BuildPlannerPreview(characterPool, laneOptions);
        var laneRequestPreview = selected
            ? selectedRequestPreview
            : plugin.BuildPlannerRunRequestPreview(laneOptions, lanePreview);
        var blockers = laneRequestPreview.ContractPreview.Blockers;
        var laneRun = ResolveLaneRuntime(runState, lane);
        var startabilityLabel = FormatText(laneRequestPreview.ContractPreview.Startability, laneRequestPreview.CanStart ? "Startable" : "Blocked");
        var expectedPartySize = laneRequestPreview.ContractPreview.PartySize;
        var firstBlocker = blockers.FirstOrDefault(static blocker => !string.IsNullOrWhiteSpace(blocker)) ?? string.Empty;

        return new PlannerLaneCardView(
            lane,
            selected,
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
            return "Enable Allow IPC starts before launching planner-driven runs.";

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
            return "Discover or configure Server Dad authority before starting remote lanes.";

        return "Pick a planner lane, verify typed roster coverage, then start from Preset Planner.";
    }

    private void DrawDutySupportRuntimeSection(DadRunResult run)
    {
        if (!HasDutySupportRuntime(run))
            return;

        DrawSectionHeader("Duty Support Runtime", "Live queue, duty entry, in-duty, leave, and stabilization truth from current executor state.");
        DrawDutySupportRuntimeRows(run);
    }

    private void DrawDutySupportRuntimeRows(DadRunResult run)
    {
        if (!HasDutySupportRuntime(run))
            return;

        var status = ResolveDutySupportExecutorStatus(run);
        var summary = ResolveDutySupportSummary(run, status);
        DrawStatusRow("Duty Support", $"{DadOperatorPhaseText.FormatPhaseLabel(run)} | {status.Status} / {status.Phase}");
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
            run.CurrentExecutorStatus.ModuleId == DadModuleId.DutySupport ||
            run.StepResults.Any(static step => step.ModuleId == DadModuleId.DutySupport));

    private static DadModuleExecutionStatusDto ResolveDutySupportExecutorStatus(DadRunResult run)
    {
        if (run.CurrentExecutorStatus.ModuleId == DadModuleId.DutySupport)
            return run.CurrentExecutorStatus;

        return run.StepResults
            .Where(static step => step.ModuleId == DadModuleId.DutySupport)
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
            return "FrenRider";

        if (ContainsAny(summary, "Do Nothing mode", "user owns combat", "user-owned"))
            return "User-owned";

        return "Path pending.";
    }

    private static string BuildDutySupportQueueEntryText(DadModuleExecutionStatusDto status, string summary)
        => status.Phase switch
        {
            DadRunPhase.QueuePreparing or DadRunPhase.QueueStarting or DadRunPhase.WaitingForQueuePop => summary,
            DadRunPhase.InDutyOrTask => "Queue complete. Duty entry confirmed.",
            DadRunPhase.PostRunStabilizing or DadRunPhase.Finalizing => "Queue complete. Duty entry and exit confirmed.",
            _ => "No live queue state.",
        };

    private static string BuildDutySupportEntryAutomationText(DadModuleExecutionStatusDto status, string summary)
    {
        var entryAutomation = ExtractSentence(summary,
            "sent no Duty Support entry command",
            "sent /bmrai on and /rotation auto after Duty Support entry",
            "sent no FrenRider, ADS, or rotation command after duty entry",
            "attempted rotation bootstrap",
            "already requested FrenRider before queue");
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
                DadRunStatus.Completed => "Stabilized.",
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
        if (!string.IsNullOrWhiteSpace(participant.Endpoint))
            parts.Add(participant.IsLocalClient ? "(local)" : participant.Endpoint);
        return string.Join(" | ", parts);
    }

    private static bool HasHardBlocker(IReadOnlyList<DadModuleBlockerDto> blockers)
        => blockers.Any(blocker => blocker.Severity is DadModuleBlockerSeverity.Blocked or DadModuleBlockerSeverity.Failed);

    private static void DrawSectionHeader(string title, string subtitle)
    {
        ImGui.Separator();
        ImGui.TextUnformatted(title);
        if (!string.IsNullOrWhiteSpace(subtitle))
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

    private void DrawPlannerLaneInputs(DadPresetPlannerOptions plannerOptions, DadPlannerLaneDefinition lane)
    {
        if (lane.RequiresDutySelector)
        {
            var selectedDuty = plugin.PresetProviderService.GetPlannerSelectedDuty(plannerOptions);
            var dutyCompatible = selectedDuty == null || IsPlannerDutyCompatible(selectedDuty, lane);
            var dutyLabel = selectedDuty == null
                ? "Select typed duty..."
                : dutyCompatible
                    ? selectedDuty.SelectionLabel
                    : $"Incompatible: {selectedDuty.SelectionLabel}";

            ImGui.TextUnformatted("Typed duty selector");
            DrawStatusRow("Selector source", lane.ActivityMode == DadPlannerActivityMode.DutySupport
                ? "Lumina ContentFinderCondition duties with Duty Support data only."
                : "Lumina ContentFinderCondition duty list.");

            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("Duty", dutyLabel))
            {
                var search = plannerDutySearch;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputText("Search", ref search, 128))
                    plannerDutySearch = search;

                ImGui.Separator();
                if (ImGui.BeginChild($"dad-duty-results-{lane.ActivityMode}", new Vector2(0f, 220f), true))
                {
                    var dutyOptions = plugin.PresetProviderService.SearchPlannerDutyOptions(lane.ActivityMode, plannerDutySearch, 96);
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
                            if (ImGui.Selectable($"{option.SelectionLabel}##dad-duty-{option.ContentFinderConditionId}", isSelected))
                                ApplyPlannerDutySelection(plannerOptions, lane, option);

                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(option.MetadataSummary);

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
                    plannerOptions.DutyExpectedPartySize = Math.Clamp(partySize, 2, 48);
                    plugin.SavePlannerOptions();
                }

                DrawStatusRow("Queue lane", plugin.PresetProviderService.GetQueueAuthorityLabel(plannerOptions.QueueAuthority));
                DrawStatusRow("Authority owner", DadStatusText.FormatAuthorityMode(lane.DefaultAuthorityMode));
                DrawStatusRow("Request shape", "Typed premade request. Queue authority stays explicit; typed party size can be overridden here.");
            }
            else if (lane.ActivityMode is DadPlannerActivityMode.DutySupport or DadPlannerActivityMode.Trust)
            {
                DrawStatusRow("Execution mode", lane.ActivityMode == DadPlannerActivityMode.DutySupport ? "DutySupportOnly" : "TrustOnly");
                DrawStatusRow("Runner count", "1 local runner");
                DrawStatusRow("Request shape", "Solo local lane. Preview forces one local runner and local queue authority.");
            }
            else if (lane.ActivityMode == DadPlannerActivityMode.LocalDuty)
            {
                DrawStatusRow("Execution mode", DadRunRequestOptions.TrustThenDutySupport);
                DrawStatusRow("Run count", "1");
                DrawStatusRow("Frequency", DadRunRequestOptions.FrequencyPerArRun);
                DrawStatusRow("Request shape", "Local duty contract. Preview stays one runner; unsynced applies only to this local lane.");
            }
            else if (lane.ActivityMode == DadPlannerActivityMode.CustomDuty)
            {
                DrawStatusRow("Attempts", "1");
                DrawStatusRow("Request shape", "Typed custom duty contract. Planner keeps this lane local-only for now.");
            }

            if (ImGui.SmallButton("Clear duty selector"))
                ClearPlannerDutySelection(plannerOptions, lane);

            DrawStatusRow("Duty selector state", selectedDuty != null
                ? dutyCompatible
                    ? BuildDutySelectorState(plannerOptions, lane, selectedDuty)
                    : BuildIncompatibleDutyText(selectedDuty, lane)
                : $"{lane.DisplayName} blocks until a typed duty is selected.");
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Mogtome)
        {
            var preset = plannerOptions.MogtomePreset;
            if (ImGui.InputText("MOGTOME preset", ref preset, 128))
            {
                plannerOptions.MogtomePreset = preset;
                plugin.SavePlannerOptions();
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
            DrawStatusRow("MOGTOME preview", "Dad owns request preview. Policy controls helper handoff shape only.");
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Blunderville)
        {
            DrawStatusRow("Attempts", "1");
            DrawStatusRow("Queue owner", plugin.PresetProviderService.GetQueueAuthorityLabel(lane.DefaultQueueAuthority));
            DrawStatusRow("Blunderville mode", "FixedEmoteRun");
            DrawStatusRow("Blunderville policy", "Dad enters, runs configured per-character emote, then fail/leaves per fixed contract.");
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Msq)
        {
            DrawStatusRow("Preset", "MSQ");
            DrawStatusRow("Attempts", "1");
            DrawStatusRow("Expected party size", lane.ExpectedPartySize.ToString(CultureInfo.InvariantCulture));
            DrawStatusRow("MSQ mapping", "Planner surfaces MSQ lane while preserving DailyMsqPremade legacy queue mapping in preview.");
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Commendation)
        {
            DrawStatusRow("Attempts", "1");
            DrawStatusRow("Queue lane", plugin.PresetProviderService.GetQueueAuthorityLabel(lane.DefaultQueueAuthority));
            DrawStatusRow("Commendation policy", "Short duty loop contract. Preview keeps attempt count and queue lane explicit.");
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Astrope)
        {
            DrawStatusRow("Attempts", "1");
            DrawStatusRow("Queue lane", plugin.PresetProviderService.GetQueueAuthorityLabel(lane.DefaultQueueAuthority));
            DrawStatusRow("Valid local time window", new DadTimeWindow().Describe());
            DrawStatusRow("Astrope policy", "Timed farming window stays explicit in preview JSON even before live executor phase.");
        }
    }

    private void DrawPlannerValidation(DadActivityPreset plannerPreview, DadPlannerRunRequestPreview requestPreview)
    {
        ImGui.TextUnformatted("Validation");
        if (plannerPreview.Blockers.Count == 0)
        {
            ImGui.TextUnformatted("Ready.");
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
                ImGui.TextUnformatted(FormatJobAndLevel(character));
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
            plannerOptions.DutyContentFinderConditionId = (uint)Math.Clamp(dutyId, 0, int.MaxValue);
            plugin.SavePlannerOptions();
        }

        var dutyName = plannerOptions.DutyDisplayName;
        if (ImGui.InputText("Duty display name", ref dutyName, 128))
        {
            plannerOptions.DutyDisplayName = dutyName;
            plugin.SavePlannerOptions();
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
            _ => true,
        };

    private static string BuildIncompatibleDutyText(DadPlannerDutyOption duty, DadPlannerLaneDefinition lane)
        => lane.ActivityMode == DadPlannerActivityMode.DutySupport
            ? $"{duty.DutyDisplayName} #{duty.ContentFinderConditionId} is not marked as Duty Support content. Clear it or reselect a Duty Support duty."
            : $"{duty.DutyDisplayName} #{duty.ContentFinderConditionId} is not valid for {lane.DisplayName}. Clear it or reselect a compatible duty.";

    private void SelectPlannerLane(DadPresetPlannerOptions plannerOptions, DadPlannerLaneDefinition lane)
    {
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

    private static string FormatJobAndLevel(DadAcquiredCharacter character)
    {
        if (string.IsNullOrWhiteSpace(character.CurrentJobAbbrev) && !character.CurrentLevel.HasValue)
            return "-";

        if (string.IsNullOrWhiteSpace(character.CurrentJobAbbrev))
            return character.CurrentLevel?.ToString(CultureInfo.InvariantCulture) ?? "-";

        return character.CurrentLevel.HasValue
            ? $"{character.CurrentJobAbbrev} {character.CurrentLevel.Value}"
            : character.CurrentJobAbbrev;
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

    private void DrawPlannerInviteAuthoritySelector(DadPresetPlannerOptions plannerOptions)
    {
        var authorities = Enum.GetValues<DadInviteAuthority>();
        var currentIndex = Array.IndexOf(authorities, plannerOptions.InviteAuthority);
        currentIndex = currentIndex < 0 ? 0 : currentIndex;
        var preview = plugin.PresetProviderService.GetEffectiveInviteAuthorityLabel(plannerOptions);
        if (!ImGui.BeginCombo("Invite owner", preview))
            return;

        for (var index = 0; index < authorities.Length; index++)
        {
            var option = authorities[index];
            var selected = option == plannerOptions.InviteAuthority;
            if (ImGui.Selectable(plugin.PresetProviderService.GetInviteAuthorityLabel(option), selected))
            {
                plannerOptions.InviteAuthority = option;
                plugin.SavePlannerOptions();
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawPlannerAccountFilterSelector(DadCharacterPool characterPool, DadPresetPlannerOptions plannerOptions)
    {
        var accountOptions = plugin.PresetProviderService.GetPlannerAccountOptions(characterPool);
        var preview = plugin.PresetProviderService.GetPlannerAccountFilterLabel(characterPool, plannerOptions);
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
            var label = $"{option.DisplayName} ({option.CharacterCount})";
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

    private string FormatAccount(DadAcquiredCharacter character)
    {
        if (!string.IsNullOrWhiteSpace(character.AccountAlias) && !string.IsNullOrWhiteSpace(character.AccountId))
            return FormatOperatorAccountLabel(character.AccountAlias, character.AccountId);

        if (!string.IsNullOrWhiteSpace(character.AccountAlias))
            return FormatOperatorAccountLabel(character.AccountAlias, string.Empty);

        return string.IsNullOrWhiteSpace(character.AccountId) ? "-" : FormatOperatorAccountLabel("Account", character.AccountId);
    }
}
