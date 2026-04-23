using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using dad.Models;

namespace dad.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private static readonly Vector2 MinimumWindowSize = new(760f, 600f);
    private readonly Plugin plugin;
    private Vector2? pendingPosition;
    private bool resetPositionConditionNextDraw;
    private string selectedCharacterKey = string.Empty;

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

        var canStartLocalDemo = CanStartLocalDemo(profile, localRun);
        var canStartRemoteDemo = canStartLocalDemo &&
                                 !configuration.LocalOnlyModeEnabled &&
                                 plugin.HasServerDadAuthority() &&
                                 !Plugin.IsBusy(authorityRun);

        ImGui.Spacing();
        DrawVisibleRunStatus(runState);
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
                DrawPresetPlannerTab(characterPool);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawVisibleRunStatus(DadVisibleRunState runState)
    {
        DrawStatusRow("Authority view", $"{runState.AuthorityView.StateText} | {runState.AuthorityView.ClientPerspectiveText} | {runState.AuthorityView.FreshnessText}");
        DrawStatusRow("Authority timeline", runState.AuthorityView.TimelineText);
        DrawStatusRow("Local run", FormatRunSnapshot(runState.LocalRun));
        DrawStatusRow("Authority run", FormatRunSnapshot(runState.AuthorityRun));
    }

    private void DrawOverviewTab(DadVisibleRunState runState, CharacterConfig profile)
    {
        var localRun = runState.LocalRun;
        var authorityRun = runState.AuthorityRun;
        var authorityView = runState.AuthorityView;
        var localParticipant = plugin.PresenceService.CurrentParticipant;

        ImGui.TextUnformatted("Live snapshot");
        DrawStatusRow("Authority view", $"{authorityView.StateText} | {authorityView.ClientPerspectiveText}");
        DrawStatusRow("Authority timeline", authorityView.TimelineText);
        DrawStatusRow("Authority freshness", authorityView.FreshnessText);
        DrawStatusRow("Authority owner", authorityView.OwnershipText);
        DrawStatusRow("Authority payload", authorityView.PayloadText);
        DrawStatusRow("IPC ready", plugin.RunCoordinatorService.IsReady ? "Yes" : "No");
        DrawStatusRow("This instance", DadStatusText.FormatWorkerRole(plugin.PresenceService.CurrentParticipant.WorkerRole));
        DrawStatusRow("Authority worker", authorityView.AuthorityWorkerText);
        DrawStatusRow("Authority endpoint", authorityView.AuthorityEndpointText);
        DrawStatusRow("Local-only", localRun.LocalOnlyEnabled ? "Enabled" : "Disabled");
        DrawStatusRow("Local run status", localRun.Summary);
        DrawStatusRow("Local run state", $"{localRun.Status} / {localRun.Phase} / {localRun.ModuleId}");
        DrawStatusRow("Authority run status", authorityRun.Summary);
        DrawStatusRow("Authority run state", $"{authorityRun.Status} / {authorityRun.Phase} / {authorityRun.ModuleId}");
        DrawStatusRow("Local participant state", localParticipant.State.ToString());
        DrawStatusRow("Local claim / lease", $"{localParticipant.ClaimState} / {localParticipant.LeaseState}");
        DrawStatusRow("Local assignment", string.IsNullOrWhiteSpace(localParticipant.AssignedSlotId) ? "(none)" : localParticipant.AssignedSlotId);
        DrawStatusRow("Local participant status", FormatText(localParticipant.StatusText, "(none)"));
        DrawStatusRow("Local participant run", string.IsNullOrWhiteSpace(localParticipant.RunId) ? "(none)" : localParticipant.RunId);
        DrawStatusRow("Local worker / phase", $"{DadStatusText.FormatWorkerRole(localRun.WorkerRole)} / {localRun.Phase}");
        DrawStatusRow("Authority worker / phase", $"{DadStatusText.FormatWorkerRole(authorityRun.WorkerRole)} / {authorityRun.Phase}");
        DrawStatusRow("Authority mode", DadStatusText.FormatAuthorityMode(authorityRun.AuthorityMode));
        DrawStatusRow("Authority transport", authorityRun.TransportMode.ToString());
        DrawStatusRow("Leader client", string.IsNullOrWhiteSpace(authorityRun.LeaderClientInstanceId) ? "(none)" : authorityRun.LeaderClientInstanceId);
        DrawStatusRow("Cancellation", authorityRun.CancellationState.ToString());
        DrawStatusRow("Participants", authorityRun.Participants.Count == 0 ? "0" : authorityRun.Participants.Count.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Task progress", $"{authorityRun.CompletedTaskCount}/{Math.Max(authorityRun.TotalTaskCount, authorityRun.RequestedTaskCount)} complete");
        DrawStatusRow("Active task", string.IsNullOrWhiteSpace(authorityRun.ActiveTaskName) ? "(none)" : $"{authorityRun.ActiveTaskIndex}/{authorityRun.TotalTaskCount} {authorityRun.ActiveTaskName}");
        DrawStatusRow("Task detail", string.IsNullOrWhiteSpace(authorityRun.ActiveTaskStatus) ? "(none)" : authorityRun.ActiveTaskStatus);
        DrawStatusRow("Executor", FormatExecutorStatus(authorityRun.CurrentExecutorStatus));
        if (!string.IsNullOrWhiteSpace(authorityRun.BlockedReason))
            DrawStatusRow("Authority blocker", authorityRun.BlockedReason);
        if (authorityRun.Warnings.Count > 0)
            DrawStatusRow("Authority warnings", string.Join(" | ", authorityRun.Warnings));
        DrawStatusRow("Authority request id", string.IsNullOrWhiteSpace(authorityRun.RequestId) ? "(none)" : authorityRun.RequestId);
        DrawStatusRow("Authority requested by", string.IsNullOrWhiteSpace(authorityRun.RequestedBy) ? "(unknown)" : authorityRun.RequestedBy);
        DrawStatusRow("Account", plugin.ConfigManager.GetCurrentAccount()?.AccountAlias ?? "(waiting for login)");
        DrawStatusRow("Profile", string.IsNullOrWhiteSpace(plugin.ConfigManager.SelectedCharacterKey) ? "(Account default)" : plugin.ConfigManager.SelectedCharacterKey);
        DrawStatusRow("Profile notes", string.IsNullOrWhiteSpace(profile.TargetNotes) ? "(none)" : profile.TargetNotes);

        ImGui.Separator();
        ImGui.TextUnformatted("Bootstrap scope");
        foreach (var item in PluginInfo.Services)
            ImGui.BulletText(item);

        ImGui.Separator();
        ImGui.TextUnformatted("First test pass");
        foreach (var item in PluginInfo.Tests)
            ImGui.BulletText(item);
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
        var authorityParticipant = authorityRun.Participants.FirstOrDefault(static participant => participant.IsAuthority)
                                  ?? participants.FirstOrDefault(candidate =>
                                      string.Equals(candidate.WorkerSessionId, authorityRun.AuthorityWorkerSessionId.ToString(), StringComparison.OrdinalIgnoreCase));

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

        ImGui.Separator();
        ImGui.TextUnformatted("Multiplayer data");
        DrawStatusRow("XADB local", xadbStatus.Availability);
        DrawStatusRow("Last save", FormatTime(xadbStatus.LastSaveUtc));
        DrawStatusRow("Snapshot version", xadbStatus.SnapshotVersion?.ToString(CultureInfo.InvariantCulture) ?? "?");
        DrawStatusRow("Snapshot quality", string.IsNullOrWhiteSpace(xadbStatus.SnapshotQuality) ? "(unknown)" : xadbStatus.SnapshotQuality);
        DrawStatusRow("Peer transport", peerTransport.Availability);
        DrawStatusRow("Connected peers", peerTransport.ConnectedPeerCount.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Last peer request", FormatTime(peerTransport.LastRequestUtc));
        DrawStatusRow("Listener", FormatText(peerTransport.ListenerEndpoint, "(none)"));
        DrawStatusRow("Authority view", $"{authorityView.StateText} | {authorityView.ClientPerspectiveText}");
        DrawStatusRow("Authority freshness", authorityView.FreshnessText);
        DrawStatusRow("Authority timeline", authorityView.TimelineText);
        DrawStatusRow("Local run status", localRun.Summary);
        DrawStatusRow("Authority run status", authorityRun.Summary);
        DrawStatusRow("Authority phase / module", $"{authorityRun.Phase} / {authorityRun.ModuleId}");
        DrawStatusRow("Authority task payload", authorityView.PayloadText);
        DrawStatusRow("Authority", authorityView.AuthorityWorkerText);
        DrawStatusRow("Authority endpoint", authorityView.AuthorityEndpointText);
        DrawStatusRow("Authority mode", DadStatusText.FormatAuthorityMode(authorityRun.AuthorityMode));
        DrawStatusRow("Authority status", DadStatusText.FormatAuthorityStatus(
            authorityParticipant?.WorkerRole ?? peerTransport.AuthorityRole,
            authorityRun.AuthorityWorkerSessionId,
            authorityRun.AuthorityEndpoint,
            authorityRun.AuthorityMode));

        ImGui.Separator();
        ImGui.TextUnformatted("Participant status");
        if (participants.Count == 1 && !participants[0].IsAuthority && !participants[0].IsAvailable)
        {
            ImGui.TextUnformatted("No Dad workers discovered yet.");
        }
        else if (ImGui.BeginTable("dad-participant-status", 10, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Role / owner");
            ImGui.TableSetupColumn("Account");
            ImGui.TableSetupColumn("Active character");
            ImGui.TableSetupColumn("Worker session");
            ImGui.TableSetupColumn("State");
            ImGui.TableSetupColumn("Lease");
            ImGui.TableSetupColumn("Ready");
            ImGui.TableSetupColumn("Available");
            ImGui.TableSetupColumn("Status");
            ImGui.TableSetupColumn("Endpoint");
            ImGui.TableHeadersRow();

            foreach (var participant in participants)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(DadStatusText.FormatParticipantOwner(participant));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatText($"{participant.ManagedAccountAlias} ({participant.ManagedAccountKey})", "(unknown)"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatText(participant.ActiveCharacterKey.ToString(), "(unknown)"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(participant.WorkerSessionId.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(participant.State.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(participant.LeaseState == DadParticipantLeaseState.None ? participant.ClaimState.ToString() : participant.LeaseState.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(participant.PostArReady ? "post-AR ready" : "waiting");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(participant.AvailableCharacterKeys.Count == 0 ? "-" : string.Join(", ", participant.AvailableCharacterKeys.Select(static key => key.ToString())));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatText(participant.StatusText, "(none)"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatText(participant.Endpoint, participant.IsLocalClient ? "(local)" : "(none)"));
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Character pool");

        var selectedCharacter = ResolveSelectedCharacter(characterPool.Characters);
        if (ImGui.BeginTable("dad-character-pool", 9, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Source");
            ImGui.TableSetupColumn("Fresh");
            ImGui.TableSetupColumn("Character");
            ImGui.TableSetupColumn("CID");
            ImGui.TableSetupColumn("Account");
            ImGui.TableSetupColumn("Job/Lvl");
            ImGui.TableSetupColumn("Territory");
            ImGui.TableSetupColumn("Party");
            ImGui.TableSetupColumn("Ready");
            ImGui.TableHeadersRow();

            foreach (var character in characterPool.Characters)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatSource(character.Source));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatFreshness(character));

                ImGui.TableNextColumn();
                if (ImGui.Selectable(character.CharacterKey, string.Equals(selectedCharacterKey, character.CharacterKey, StringComparison.OrdinalIgnoreCase), ImGuiSelectableFlags.SpanAllColumns))
                    selectedCharacterKey = character.CharacterKey;

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatContentId(character.ContentId));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(character.AccountAlias) ? "-" : character.AccountAlias);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatJobAndLevel(character));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(character.TerritoryName) ? "unknown" : character.TerritoryName);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatParty(character));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatReadiness(character.Readiness));
            }

            ImGui.EndTable();
        }

        if (selectedCharacter == null)
            return;

        ImGui.Separator();
        ImGui.TextUnformatted("Selected character detail");
        DrawStatusRow("Identity", $"{selectedCharacter.CharacterKey} | CID {FormatContentId(selectedCharacter.ContentId)} | Account {selectedCharacter.AccountAlias}");
        DrawStatusRow("World", $"{selectedCharacter.WorldName} | {selectedCharacter.DataCenterName}");
        DrawStatusRow("Live", $"{FormatReadiness(selectedCharacter.Readiness)} | {selectedCharacter.TerritoryName} | party {FormatParty(selectedCharacter)}");
        DrawStatusRow("XADB", selectedCharacter.XadbReady
            ? $"{FormatTime(selectedCharacter.XadbSnapshotUtc)} | quality {FormatText(selectedCharacter.SnapshotQuality, "(unknown)")}"
            : "Unavailable");
        DrawStatusRow("Planner", selectedCharacter.Blockers.Count == 0 ? "No blockers recorded." : string.Join(" | ", selectedCharacter.Blockers));
    }

    private void DrawPresetPlannerTab(DadCharacterPool characterPool)
    {
        var plannerOptions = plugin.PlannerOptions;
        var plannerPreview = plugin.BuildPlannerPreview();
        var requestPreview = plugin.BuildPlannerRunRequestPreview();

        if (!ImGui.BeginTable("dad-planner-layout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Lanes", ImGuiTableColumnFlags.WidthFixed, 250f);
        ImGui.TableSetupColumn("Plan", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawPlannerLanePanel(plannerOptions, requestPreview);

        ImGui.TableNextColumn();
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

        ImGui.SameLine();
        if (ImGui.SmallButton("Planner to chat"))
            plugin.PrintStatus(plugin.BuildPlannerSummary());

        ImGui.SameLine();
        if (ImGui.SmallButton("Copy planner summary"))
        {
            ImGui.SetClipboardText(plugin.BuildPlannerSummary());
            plugin.PrintStatus("Copied dad planner summary.");
        }

        ImGui.Separator();
        DrawPlannerLaneInputs(plannerOptions, plannerPreview.LaneDefinition);

        ImGui.Separator();
        DrawStatusRow("Lane", $"{plannerPreview.LaneDefinition.DisplayName} | {plannerPreview.LaneDefinition.MaturityLabel}");
        DrawStatusRow("Next action", plannerPreview.LaneDefinition.NextAction);
        DrawStatusRow("Preset", plannerPreview.DisplayName);
        DrawStatusRow("Operator mode", plannerPreview.OperatorModeLabel);
        DrawStatusRow("Transport", plugin.PresetProviderService.GetTransportOwnerLabel(plannerPreview.TransportOwner));
        DrawStatusRow("Queue authority", plugin.PresetProviderService.GetQueueAuthorityLabel(plannerPreview.QueueAuthority));
        DrawStatusRow("Invite owner", plugin.PresetProviderService.GetInviteAuthorityLabel(plannerPreview.InviteAuthority));
        DrawStatusRow("Account filter", plannerPreview.AccountFilterSummary);
        DrawStatusRow("Roster source", plugin.PresetProviderService.GetRosterSourceLabel(plannerPreview.RosterSource));
        DrawStatusRow("Leader", FormatText(plannerPreview.LeaderStatusText, "(none)"));
        DrawStatusRow("Preview scope", plannerPreview.PreviewScope);
        DrawStatusRow("Validation", $"{FormatReadiness(plannerPreview.ValidationState)} | {plannerPreview.ValidationSummary}");
        DrawStatusRow("Filters", plannerPreview.FilterSummary);
        DrawStatusRow("Summary", plannerPreview.PlannerSummary);
        DrawStatusRow("Planner request", requestPreview.StatusSummary);
        DrawStatusRow("Request id", FormatText(requestPreview.RequestId, "(none)"));
        DrawStatusRow("Request module", requestPreview.ModuleId.ToString());
        DrawStatusRow("Required characters", FormatKeys(requestPreview.RequiredCharacterKeys));
        DrawStatusRow("Required accounts", FormatKeys(requestPreview.RequiredAccountKeys));
        DrawStatusRow("Request queue", requestPreview.QueueAuthority.ToString());
        DrawStatusRow("Expected party size", requestPreview.ExpectedPartySize <= 0 ? "?" : requestPreview.ExpectedPartySize.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Local-only mode", plugin.Configuration.LocalOnlyModeEnabled ? "Enabled" : "Disabled");

        ImGui.BeginDisabled(requestPreview.Request == null);
        if (ImGui.SmallButton("Copy request JSON"))
        {
            ImGui.SetClipboardText(plugin.BuildPlannerRequestJson());
            plugin.PrintStatus("Copied dad planner request JSON.");
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(!requestPreview.CanStart);
        if (ImGui.SmallButton("Start planner run"))
            plugin.StartPlannerRunFromShell();
        ImGui.EndDisabled();

        ImGui.Separator();
        DrawPlannerValidation(plannerPreview, requestPreview);

        ImGui.Separator();
        DrawPlannerFilterCounts(plannerPreview);

        ImGui.Separator();
        DrawPlannerRosterSlots(plannerPreview);

        ImGui.Separator();
        DrawPlannerAvailableCharacters(plannerPreview);
        ImGui.EndTable();
    }

    private void DrawPlannerLanePanel(DadPresetPlannerOptions plannerOptions, DadPlannerRunRequestPreview requestPreview)
    {
        ImGui.TextUnformatted("Plan lanes");
        foreach (var lane in plugin.PresetProviderService.GetPlannerLaneDefinitions())
        {
            var selected = IsSelectedPlannerLane(plannerOptions.ActivityMode, lane.ActivityMode);
            var accent = ParseHexColor(lane.AccentColorHex, selected ? 0.95f : 0.62f);
            var hovered = ParseHexColor(lane.AccentColorHex, 0.82f);
            var active = ParseHexColor(lane.AccentColorHex, 1f);
            ImGui.PushStyleColor(ImGuiCol.Button, accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
            if (ImGui.Button($"{lane.DisplayName}##dad-lane-{lane.ActivityMode}", new Vector2(-1f, 30f)))
                SelectPlannerLane(plannerOptions, lane);
            ImGui.PopStyleColor(3);

            ImGui.TextDisabled($"{lane.MaturityLabel} | party {lane.ExpectedPartySize}");
            ImGui.TextWrapped(lane.NextAction);
            if (selected && !string.IsNullOrWhiteSpace(requestPreview.BlockedReason))
                ImGui.TextWrapped(requestPreview.BlockedReason);
            ImGui.Spacing();
        }
    }

    private void DrawPlannerLaneInputs(DadPresetPlannerOptions plannerOptions, DadPlannerLaneDefinition lane)
    {
        if (lane.RequiresDutySelector)
        {
            ImGui.TextUnformatted("Duty selector");
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

            var dutyUnsynced = plannerOptions.DutyUnsynced;
            if (ImGui.Checkbox("Unsynced", ref dutyUnsynced))
            {
                plannerOptions.DutyUnsynced = dutyUnsynced;
                plugin.SavePlannerOptions();
            }

            ImGui.SameLine();
            var partySize = plannerOptions.DutyExpectedPartySize;
            if (ImGui.InputInt("Expected party size", ref partySize))
            {
                plannerOptions.DutyExpectedPartySize = Math.Clamp(partySize, 1, 8);
                plugin.SavePlannerOptions();
            }
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Mogtome)
        {
            var preset = plannerOptions.MogtomePreset;
            if (ImGui.InputText("MOGTOME preset", ref preset, 128))
            {
                plannerOptions.MogtomePreset = preset;
                plugin.SavePlannerOptions();
            }
        }

        if (lane.ActivityMode == DadPlannerActivityMode.Blunderville)
        {
            var mode = plannerOptions.BlundervilleMode;
            if (ImGui.InputText("Blunderville mode", ref mode, 128))
            {
                plannerOptions.BlundervilleMode = mode;
                plugin.SavePlannerOptions();
            }
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
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(slot.CharacterKey) ? "-" : slot.CharacterKey);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.SelectedSource.HasValue
                ? plugin.PresetProviderService.GetCharacterSourceLabel(slot.SelectedSource.Value)
                : "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatFreshness(slot.SelectedFreshness));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatReadiness(slot.SelectedReadiness));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatText(slot.BlockerSummary, slot.StatusText));
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
                ImGui.TextUnformatted(character.CharacterKey);
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
                ImGui.TextUnformatted(FormatBlockers(character.Blockers));
            }

            ImGui.EndTable();
        }
    }

    private void SelectPlannerLane(DadPresetPlannerOptions plannerOptions, DadPlannerLaneDefinition lane)
    {
        plannerOptions.ActivityMode = lane.ActivityMode;
        plannerOptions.TransportOwner = lane.DefaultTransportOwner;
        plannerOptions.QueueAuthority = lane.DefaultQueueAuthority;
        plannerOptions.DutyExpectedPartySize = Math.Clamp(lane.ExpectedPartySize, 1, 8);
        plugin.SavePlannerOptions();
    }

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

    private DadAcquiredCharacter? ResolveSelectedCharacter(IReadOnlyList<DadAcquiredCharacter> characters)
    {
        if (characters.Count == 0)
        {
            selectedCharacterKey = string.Empty;
            return null;
        }

        if (string.IsNullOrWhiteSpace(selectedCharacterKey) ||
            !characters.Any(character => string.Equals(character.CharacterKey, selectedCharacterKey, StringComparison.OrdinalIgnoreCase)))
        {
            selectedCharacterKey = characters[0].CharacterKey;
        }

        return characters.FirstOrDefault(character => string.Equals(character.CharacterKey, selectedCharacterKey, StringComparison.OrdinalIgnoreCase));
    }

    private static void DrawStatusRow(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine(180f);
        ImGui.TextWrapped(value);
    }

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

    private static string FormatSource(DadCharacterSource source)
        => source switch
        {
            DadCharacterSource.LocalRuntime => "local runtime",
            DadCharacterSource.PeerRuntime => "peer runtime",
            DadCharacterSource.XadbOnly => "XADB only",
            DadCharacterSource.ManualUnresolved => "manual unresolved",
            _ => source.ToString(),
        };

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

    private static string FormatContentId(ulong contentId)
        => contentId == 0 ? "-" : $"0x{contentId:X}";

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

    private static string FormatRunSnapshot(DadRunResult run)
    {
        var requestId = string.IsNullOrWhiteSpace(run.RequestId) ? "(none)" : run.RequestId;
        var taskDetail = string.IsNullOrWhiteSpace(run.ActiveTaskStatus) ? run.Summary : run.ActiveTaskStatus;
        var blocker = string.IsNullOrWhiteSpace(run.BlockedReason) ? string.Empty : $" | Blocker {run.BlockedReason}";
        return $"{run.Status} / {run.Phase} / {run.ModuleId} | {taskDetail}{blocker} | Request {requestId}";
    }

    private static string FormatExecutorStatus(DadModuleExecutionStatusDto status)
    {
        if (status.ModuleId == DadModuleId.None && string.IsNullOrWhiteSpace(status.DisplayName))
            return "(none)";

        var blocker = string.IsNullOrWhiteSpace(status.BlockedReason) ? string.Empty : $" | Blocker {status.BlockedReason}";
        var retry = status.MaxRetryAttempts <= 0 ? string.Empty : $" | Retry {status.RetryAttempt}/{status.MaxRetryAttempts}";
        return $"{status.DisplayName} / {status.Status} / {status.Phase}{retry} | {status.Summary}{blocker}";
    }

    private void DrawPlannerActivityModeSelector(DadPresetPlannerOptions plannerOptions)
    {
        var activityModes = plugin.PresetProviderService.GetPlannerActivityModeOptions().ToArray();
        var currentIndex = Array.IndexOf(activityModes, plannerOptions.ActivityMode);
        currentIndex = currentIndex < 0 ? 0 : currentIndex;
        var preview = plugin.PresetProviderService.GetPlannerActivityModeLabel(activityModes[currentIndex]);
        if (!ImGui.BeginCombo("Activity mode", preview))
            return;

        for (var index = 0; index < activityModes.Length; index++)
        {
            var option = activityModes[index];
            var selected = option == plannerOptions.ActivityMode;
            if (ImGui.Selectable(plugin.PresetProviderService.GetPlannerActivityModeLabel(option), selected))
            {
                plannerOptions.ActivityMode = option;
                plugin.SavePlannerOptions();
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
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

    private static string FormatAccount(DadAcquiredCharacter character)
    {
        if (!string.IsNullOrWhiteSpace(character.AccountAlias) && !string.IsNullOrWhiteSpace(character.AccountId))
            return string.Equals(character.AccountAlias, character.AccountId, StringComparison.OrdinalIgnoreCase)
                ? character.AccountAlias
                : $"{character.AccountAlias} ({character.AccountId})";

        if (!string.IsNullOrWhiteSpace(character.AccountAlias))
            return character.AccountAlias;

        return string.IsNullOrWhiteSpace(character.AccountId) ? "-" : character.AccountId;
    }
}
