using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using dad.Models;
using dad.Services;

namespace dad.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private static readonly string[] DtrModes = { "Text only", "Icon + text", "Icon only" };
    private static readonly Vector2 MinimumWindowSize = new(700f, 540f);
    private readonly Plugin plugin;
    private string draftTransportBindHost = string.Empty;
    private int draftTransportBindPort;
    private string draftAuthorityTargetHost = string.Empty;
    private int draftAuthorityTargetPort;
    private bool endpointDraftInitialized;
    private Vector2? pendingPosition;
    private bool resetPositionConditionNextDraw;
    private string pendingDeleteAccountId = string.Empty;
    private string pendingMergeAccountId = string.Empty;

    public ConfigWindow(Plugin plugin) : base($"{PluginInfo.DisplayName} Settings##Config", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumWindowSize,
            MaximumSize = new Vector2(1600f, 1400f),
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

        if (ImGui.BeginTabBar("dad-config-tabs"))
        {
            if (ImGui.BeginTabItem("General"))
            {
                DrawGeneralTab(configuration);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Scheduler Settings"))
            {
                DrawSchedulerTab(configuration);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Combat Rotation"))
            {
                DrawCombatRotationTab(configuration);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("About"))
            {
                DrawAboutTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawGeneralTab(Configuration configuration)
    {
        EnsureEndpointDraft(configuration);

        var enabled = configuration.PluginEnabled;
        if (ImGui.Checkbox("Plugin enabled", ref enabled))
            plugin.SetPluginEnabled(enabled, printStatus: false);

        var runAsServerDad = configuration.RunAsServerDad;
        if (ImGui.Checkbox("Run as Server Dad", ref runAsServerDad))
        {
            configuration.RunAsServerDad = runAsServerDad;
            configuration.Save();
        }

        var localOnly = configuration.LocalOnlyModeEnabled;
        if (ImGui.Checkbox("Sticky local-only mode", ref localOnly))
        {
            configuration.LocalOnlyModeEnabled = localOnly;
            configuration.Save();
        }

        DrawStatusRow("Debug UI", configuration.DebugUiEnabled ? "Enabled via /dad debug." : "Disabled. Use /dad debug to show verbose diagnostics.");
        var dutyIpcStatus = plugin.DutyIpcService.GetStatus();
        var bridgeStatus = plugin.QuestionableBridge.GetStatus();
        DrawStatusRow("Dad duty IPC", FormatDutyIpcRegistrationStatus(dutyIpcStatus));
        DrawStatusRow("Questionable runtime bridge", FormatQuestionableBridgeStatus(bridgeStatus));
        DrawStatusRow("Questionable cosmetic", FormatQuestionableCosmeticStatus(bridgeStatus));
        DrawStatusRow("Dad duty IPC probe", FormatDutyIpcProbeStatus(dutyIpcStatus));
        DrawStatusRow("Dad duty IPC run", FormatDutyIpcFailureStatus(dutyIpcStatus));
        DrawStatusRow("Dad duty IPC cleanup", FormatDutyIpcCleanupStatus(dutyIpcStatus));

        var dtr = configuration.DtrBarEnabled;
        if (ImGui.Checkbox("Show DTR bar entry", ref dtr))
        {
            configuration.DtrBarEnabled = dtr;
            configuration.Save();
            plugin.UpdateDtrBar();
        }

        var krangle = configuration.KrangleOperatorNamesEnabled;
        if (ImGui.Checkbox("Krangle operator names", ref krangle))
        {
            configuration.KrangleOperatorNamesEnabled = krangle;
            configuration.Save();
        }

        var mode = configuration.DtrBarMode;
        if (ImGui.Combo("DTR mode", ref mode, DtrModes, DtrModes.Length))
        {
            configuration.DtrBarMode = mode;
            configuration.Save();
            plugin.UpdateDtrBar();
        }

        var onIcon = configuration.DtrIconEnabled;
        if (ImGui.InputText("DTR enabled glyph", ref onIcon, 8))
        {
            var committedSignature = BuildDtrGlyphSignature(configuration);
            configuration.DtrIconEnabled = onIcon.Length <= 3 ? onIcon : onIcon[..3];
            plugin.QueueDebouncedConfigurationSave(
                "dtr-glyphs",
                committedSignature,
                () => BuildDtrGlyphSignature(configuration),
                plugin.UpdateDtrBar);
        }

        var offIcon = configuration.DtrIconDisabled;
        if (ImGui.InputText("DTR disabled glyph", ref offIcon, 8))
        {
            var committedSignature = BuildDtrGlyphSignature(configuration);
            configuration.DtrIconDisabled = offIcon.Length <= 3 ? offIcon : offIcon[..3];
            plugin.QueueDebouncedConfigurationSave(
                "dtr-glyphs",
                committedSignature,
                () => BuildDtrGlyphSignature(configuration),
                plugin.UpdateDtrBar);
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Transport endpoints");
        ImGui.TextWrapped("Blank bind host with port 0 preserves the current loopback/ephemeral listener. Blank authority target preserves peer-discovered Server Dad authority.");

        ImGui.InputText("Transport bind host", ref draftTransportBindHost, 128);
        ImGui.InputInt("Transport bind port", ref draftTransportBindPort);
        ImGui.InputText("Authority target host", ref draftAuthorityTargetHost, 128);
        ImGui.InputInt("Authority target port", ref draftAuthorityTargetPort);

        draftTransportBindPort = Math.Clamp(draftTransportBindPort, 0, 65535);
        draftAuthorityTargetPort = Math.Clamp(draftAuthorityTargetPort, 0, 65535);

        var hasPendingEndpointDraftChanges = HasPendingEndpointDraftChanges(configuration);
        if (hasPendingEndpointDraftChanges)
            ImGui.TextDisabled("Endpoint draft has unapplied changes.");

        if (ImGui.Button("Apply endpoint changes"))
            ApplyEndpointDraft(configuration);
        if (!hasPendingEndpointDraftChanges)
            ImGui.BeginDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Revert endpoint draft"))
            ResetEndpointDraft(configuration);
        if (!hasPendingEndpointDraftChanges)
            ImGui.EndDisabled();

        DrawStatusRow("Listener endpoint", FormatText(plugin.TransportService.CurrentTransport.ListenerEndpoint, "(listener unavailable)"));
        DrawStatusRow("Configured authority target", FormatText(plugin.TransportService.GetConfiguredAuthorityEndpoint(), "(peer discovery)"));
        DrawStatusRow("Effective authority target", FormatText(plugin.TransportService.GetPreferredAuthorityEndpoint(), "(none)"));

        ImGui.Separator();
        ImGui.TextUnformatted("Wait policy");

        var readyTimeout = configuration.ParticipantReadyTimeoutSeconds;
        if (ImGui.InputInt("Participant ready timeout (s)", ref readyTimeout))
        {
            var committedSignature = BuildWaitPolicySignature(configuration);
            configuration.ParticipantReadyTimeoutSeconds = Math.Max(30, readyTimeout);
            plugin.QueueDebouncedConfigurationSave(
                "wait-policy",
                committedSignature,
                () => BuildWaitPolicySignature(configuration));
        }

        var assemblyTimeout = configuration.AssemblyTimeoutSeconds;
        if (ImGui.InputInt("Assembly timeout (s)", ref assemblyTimeout))
        {
            var committedSignature = BuildWaitPolicySignature(configuration);
            configuration.AssemblyTimeoutSeconds = Math.Max(10, assemblyTimeout);
            plugin.QueueDebouncedConfigurationSave(
                "wait-policy",
                committedSignature,
                () => BuildWaitPolicySignature(configuration));
        }

        var staleTimeout = configuration.HeartbeatStaleSeconds;
        if (ImGui.InputInt("Heartbeat stale threshold (s)", ref staleTimeout))
        {
            var committedSignature = BuildWaitPolicySignature(configuration);
            configuration.HeartbeatStaleSeconds = Math.Max(3, staleTimeout);
            plugin.QueueDebouncedConfigurationSave(
                "wait-policy",
                committedSignature,
                () => BuildWaitPolicySignature(configuration));
        }

        var leaseDuration = configuration.LeaseDurationSeconds;
        if (ImGui.InputInt("Lease duration (s)", ref leaseDuration))
        {
            var committedSignature = BuildWaitPolicySignature(configuration);
            configuration.LeaseDurationSeconds = Math.Max(5, leaseDuration);
            plugin.QueueDebouncedConfigurationSave(
                "wait-policy",
                committedSignature,
                () => BuildWaitPolicySignature(configuration));
        }

        var cancelAck = configuration.CancelAckTimeoutSeconds;
        if (ImGui.InputInt("Cancel ack timeout (s)", ref cancelAck))
        {
            var committedSignature = BuildWaitPolicySignature(configuration);
            configuration.CancelAckTimeoutSeconds = Math.Max(2, cancelAck);
            plugin.QueueDebouncedConfigurationSave(
                "wait-policy",
                committedSignature,
                () => BuildWaitPolicySignature(configuration));
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Window commands");
        ImGui.BulletText("/dad ws -> reset both windows to 1,1");
        ImGui.BulletText("/dad j -> jump both windows somewhere visible");
        ImGui.BulletText("/dad status -> print the live shell summary to chat");
        ImGui.BulletText("/dad debug, /dad debug on, /dad debug off -> toggle verbose UI diagnostics");
        ImGui.BulletText("/dad krangle -> toggle local operator-name krangling");
        ImGui.BulletText("/dad run or /dad run local -> start a local Sastasha demo");
        ImGui.BulletText("/dad run server -> start a Server Dad Sastasha premade demo");
        ImGui.BulletText("/dad run msq -> start a Server Dad Daily MSQ demo");
        ImGui.BulletText("/dad run commend -> start a Server Dad commendation demo");
        ImGui.BulletText("/dad run planner -> start the current startable Preset Planner request");
        ImGui.BulletText("/dad test planner-groups -> run non-starting planner group IPC diagnostics");
        ImGui.BulletText("/dad test profiles -> profile owner/cache/revision diagnostics");
        ImGui.BulletText("/dad test launch-profiles -> launch path/mapping diagnostics");
        ImGui.BulletText("/dad test workers -> distributed worker diagnostics");
        ImGui.BulletText("/dad test duty-ipc current|territory <id>|cfc <id> -> diagnose Dad duty IPC availability");
        ImGui.BulletText("/dad cancel -> cancel the active orchestration run");
    }

    private void DrawSchedulerTab(Configuration configuration)
    {
        configuration.CharacterLoadInstruction ??= new DadCharacterLoadInstruction();
        configuration.CharacterLoadInstruction.Normalize();

        ImGui.TextWrapped("Account, character, and launch profile mapping lives under Crew / Scheduler -> Accounts & Profiles.");
        ImGui.TextUnformatted("Character load command");
        var instruction = configuration.CharacterLoadInstruction;
        var loadEnabled = instruction.Enabled;
        if (ImGui.Checkbox("Enable command template", ref loadEnabled))
        {
            instruction.Enabled = loadEnabled;
            configuration.Save();
        }

        var loadDryRun = instruction.DryRun;
        if (ImGui.Checkbox("Dry-run character load", ref loadDryRun))
        {
            instruction.DryRun = loadDryRun;
            configuration.Save();
        }

        var commandTemplate = instruction.CommandTemplate;
        if (ImGui.InputText("Command template", ref commandTemplate, 256))
        {
            var committedSignature = BuildCharacterLoadSignature(instruction);
            instruction.CommandTemplate = commandTemplate;
            plugin.QueueDebouncedConfigurationSave(
                "character-load",
                committedSignature,
                () => BuildCharacterLoadSignature(instruction));
        }

        var loadTimeout = instruction.TimeoutSeconds;
        if (ImGui.InputInt("Load timeout (s)", ref loadTimeout))
        {
            var committedSignature = BuildCharacterLoadSignature(instruction);
            instruction.TimeoutSeconds = Math.Clamp(loadTimeout, 30, 1800);
            plugin.QueueDebouncedConfigurationSave(
                "character-load",
                committedSignature,
                () => BuildCharacterLoadSignature(instruction));
        }

        DrawStatusRow("Placeholders", "{Character}, {CharacterName}, {World}, {Account}");
        DrawStatusRow("Scheduler state", plugin.SchedulerService.CurrentState.Summary);

        ImGui.Separator();
        ImGui.TextUnformatted("Roster catalog");
        configuration.RosterCatalog ??= new DadRosterCatalogConfiguration();
        var staleHours = configuration.RosterCatalog.StaleAfterHours;
        if (ImGui.InputInt("Roster stale after (h)", ref staleHours))
        {
            var committedSignature = configuration.RosterCatalog.StaleAfterHours.ToString();
            configuration.RosterCatalog.StaleAfterHours = Math.Clamp(staleHours, 1, 24 * 90);
            plugin.QueueDebouncedConfigurationSave(
                "roster-stale-hours",
                committedSignature,
                () => configuration.RosterCatalog.StaleAfterHours.ToString());
        }

        var showHidden = configuration.RosterCatalog.ShowHiddenInRoster;
        if (ImGui.Checkbox("Include hidden/ignored in roster IPC export", ref showHidden))
        {
            configuration.RosterCatalog.ShowHiddenInRoster = showHidden;
            configuration.Save();
        }

        DrawStatusRow("Roster tab", "Account-scoped browser always exposes Active, Hidden, Ignored, Needs update, and All views.");
        DrawStatusRow("Preset slot pickers", "Assigned Active rows only; update-marked rows wait for refresh.");

        var queue = plugin.SchedulerService.GetQueueSnapshot();
        DrawStatusRow("Queue", queue.Summary);

        ImGui.Separator();
        DrawAccountAliasEditor(configuration);
    }

    private void DrawAccountAliasEditor(Configuration configuration)
    {
        ImGui.TextUnformatted("Dad account aliases");
        if (DrawClearAllAccountDataButton("dad-config-clear-all-account-data"))
        {
            DrawMergeAccountPopup();
            DrawDeleteAccountPopup();
            return;
        }

        var accounts = plugin.ConfigManager.GetAllAccounts();
        if (accounts.Count == 0)
        {
            ImGui.TextDisabled("No Dad account configs have been seen on this client.");
            DrawMergeAccountPopup();
            DrawDeleteAccountPopup();
            return;
        }

        if (!ImGui.BeginTable("dad-account-aliases", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            DrawMergeAccountPopup();
            DrawDeleteAccountPopup();
            return;
        }

        ImGui.TableSetupColumn("Account key");
        ImGui.TableSetupColumn("Alias");
        ImGui.TableSetupColumn("Characters");
        ImGui.TableSetupColumn("Actions");
        ImGui.TableHeadersRow();

        foreach (var account in accounts)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(account.AccountId);
            ImGui.TableNextColumn();
            var alias = plugin.GetAccountAliasEditValue(new DadAccountKey(account.AccountId), account.AccountAlias);
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText($"##dad-account-alias-{account.AccountId}", ref alias, 96))
                plugin.QueueDebouncedAccountAliasEdit(new DadAccountKey(account.AccountId), account.AccountAlias, alias);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(account.Characters.Count.ToString());
            ImGui.TableNextColumn();
            var currentAccountId = plugin.ConfigManager.CurrentAccountId?.Trim() ?? string.Empty;
            var canMerge = !string.IsNullOrWhiteSpace(currentAccountId) &&
                           !string.Equals(currentAccountId, account.AccountId, StringComparison.OrdinalIgnoreCase);
            ImGui.BeginDisabled(!canMerge);
            if (ImGui.SmallButton($"Merge into current##dad-config-merge-account-{account.AccountId}"))
            {
                pendingMergeAccountId = account.AccountId;
                ImGui.OpenPopup("Confirm merge account##dad-config-merge-account");
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (DrawCtrlShiftSmallButton(
                    "Delete",
                    $"dad-config-delete-account-{account.AccountId}",
                    "Click to delete this local Dad account config and Dad roster metadata. XADB snapshots stay untouched.",
                    "Hold Ctrl+Shift to delete this local Dad account config and Dad roster metadata. XADB snapshots stay untouched."))
            {
                pendingDeleteAccountId = account.AccountId;
                ImGui.OpenPopup("Confirm delete account##dad-config-delete-account");
            }
        }

        ImGui.EndTable();
        DrawMergeAccountPopup();
        DrawDeleteAccountPopup();
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

    private void DrawMergeAccountPopup()
    {
        if (!ImGui.BeginPopup("Confirm merge account##dad-config-merge-account"))
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

        ImGui.TextWrapped($"Merge Dad account '{source.AccountAlias}' ({source.AccountId}) into current account '{target.AccountAlias}' ({target.AccountId})?");
        ImGui.TextDisabled("Moves missing character configs and Dad roster metadata. Target keeps duplicate character configs. Source config is deleted. XADB snapshots stay untouched.");
        if (ImGui.SmallButton("Merge account"))
        {
            if (plugin.MergeDadAccountIntoCurrent(new DadAccountKey(source.AccountId)))
                plugin.PrintStatus($"Merged Dad account '{source.AccountAlias}' into '{target.AccountAlias}'.");

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

    private void DrawDeleteAccountPopup()
    {
        if (!ImGui.BeginPopup("Confirm delete account##dad-config-delete-account"))
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

        ImGui.TextWrapped($"Delete Dad account '{account.AccountAlias}' ({account.AccountId})?");
        ImGui.TextDisabled("Removes local Dad config and Dad roster metadata. XADB snapshots stay untouched.");
        if (DrawCtrlShiftSmallButton(
                "Delete account",
                "dad-config-confirm-delete-account",
                "Click to delete this local Dad account config and Dad roster metadata. XADB snapshots stay untouched.",
                "Hold Ctrl+Shift to delete this local Dad account config and Dad roster metadata. XADB snapshots stay untouched."))
        {
            if (plugin.DeleteDadAccount(new DadAccountKey(account.AccountId)))
                plugin.PrintStatus($"Deleted Dad account '{account.AccountAlias}' ({account.AccountId}).");

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

    private void DrawCombatRotationTab(Configuration configuration)
    {
        ImGui.TextWrapped("Select what Dad does when it starts a duty operation. Use FrenRider is the default: Dad queues first, sends /fr on after confirmed duty entry, then FrenRider owns in-duty behavior, ADS handoff, stop, and exit choices. Normal planner, manual, and scheduler runs do not send disable commands. Only a successful final dad.Duty.Run IPC session sends the five-command cleanup set.");
        ImGui.Separator();

        DrawCombatRotationModeRadio(
            configuration,
            DadCombatRotationMode.ForceCommands,
            "Force \"BMRAI ON\" and \"ROTATION AUTO\"");
        DrawCombatRotationModeRadio(
            configuration,
            DadCombatRotationMode.UseFrenRider,
            "Use FrenRider (default)");
        DrawCombatRotationModeRadio(
            configuration,
            DadCombatRotationMode.DoNothing,
            "Do nothing; leave it up to user");

        ImGui.Separator();
        switch (configuration.CombatRotationMode)
        {
            case DadCombatRotationMode.UseFrenRider:
                DrawFrenRiderModeStatus();
                break;
            case DadCombatRotationMode.ForceCommands:
                DrawForceCommandsModeStatus();
                break;
            case DadCombatRotationMode.DoNothing:
                ImGui.TextWrapped("Dad queues duty operations only. It does not send FrenRider, ADS, or rotation commands; user-owned play and leave behavior is expected.");
                break;
        }
    }

    private static void DrawCombatRotationModeRadio(
        Configuration configuration,
        DadCombatRotationMode mode,
        string label)
    {
        if (ImGui.RadioButton(label, configuration.CombatRotationMode == mode))
        {
            configuration.CombatRotationMode = mode;
            configuration.Save();
        }
    }

    private void DrawFrenRiderModeStatus()
    {
        var frenRiderState = plugin.CombatRotationService.GetFrenRiderPluginState();
        var color = frenRiderState switch
        {
            DadFrenRiderPluginState.Loaded => new Vector4(0.35f, 0.95f, 0.45f, 1f),
            DadFrenRiderPluginState.InstalledNotLoaded => new Vector4(1f, 0.85f, 0.25f, 1f),
            _ => new Vector4(1f, 0.45f, 0.35f, 1f),
        };
        var statusText = frenRiderState switch
        {
            DadFrenRiderPluginState.Loaded => "FrenRider installed and loaded. Dad will send /fr on after confirmed duty entry.",
            DadFrenRiderPluginState.InstalledNotLoaded => "FrenRider installed but not loaded. Dad will block Use FrenRider duty operations until it is loaded.",
            _ => "FrenRider not installed or not found. Dad will block Use FrenRider duty operations until it is installed and loaded.",
        };
        ImGui.TextColored(
            color,
            statusText);
        ImGui.TextWrapped("Status color uses Dalamud installed-plugin state only: green means installed and loaded, yellow means installed but not loaded, red means not installed/found.");
        ImGui.TextWrapped("Normal Dad run completion and every cancel/stop/failure path leave combat automation unchanged. Only successful final dad.Duty.Run IPC completion sends /fr off, /rotation cancel, /vbmai off, /bmrai off, and /wrath auto off.");
    }

    private static void DrawForceCommandsModeStatus()
    {
        ImGui.TextWrapped("Compatibility mode. Dad preserves the current Dad+ADS flow: /ads outside before queue, fixed rotation commands after entry, and /ads leave after DutyCompleted. Rotation command failures are warning-only.");
        ImGui.TextUnformatted("Fixed commands after entry");
        ImGui.BulletText(DadCombatRotationService.BossModRotationCommand);
        ImGui.BulletText(DadCombatRotationService.AutoRotationCommand);
    }

    private static void DrawAboutTab()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        ImGui.Text($"{PluginInfo.DisplayName} v{version}");
        ImGui.TextWrapped(PluginInfo.Summary);

        ImGui.Separator();
        ImGui.TextUnformatted("Planned phases");
        foreach (var item in PluginInfo.Phases)
            ImGui.BulletText(item);

        ImGui.Separator();
        ImGui.TextUnformatted("Operator checks");
        foreach (var item in PluginInfo.Tests)
            ImGui.BulletText(item);
    }

    private static void DrawStatusRow(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine(220f);
        ImGui.TextWrapped(value);
    }

    private static string FormatDutyIpcRegistrationStatus(DadDutyIpcStatus status)
    {
        var state = status.Registered ? "Registered" : status.RegistrationState;
        return $"{FormatText(state, "Not registered")} | mode {status.LastMode}";
    }

    private static string FormatQuestionableBridgeStatus(DadQuestionableReflectionBridgeStatus status)
    {
        var loaded = status.QuestionableLoaded ? "loaded" : "not loaded";
        var running = status.QuestionableRunning ? "running" : "idle";
        var gate = status.DutyGateEnabled.HasValue
            ? status.DutyGateEnabled.Value ? "enabled" : "disabled"
            : "unknown";
        var version = FormatText(status.QuestionableVersion, "unknown");
        var blocker = FormatText(status.LastBlocker, "(none)");
        return $"{loaded} | {status.PatchState} | {running} | gate {gate} | version {version} | blocker {blocker}";
    }

    private static string FormatQuestionableCosmeticStatus(DadQuestionableReflectionBridgeStatus status)
    {
        var blocker = FormatText(status.CosmeticLastBlocker, "(none)");
        return $"{status.CosmeticPatchState} | blocker {blocker}";
    }

    private static string FormatDutyIpcProbeStatus(DadDutyIpcStatus status)
    {
        if (!status.LastContentHasPathResult.HasValue)
            return "No Questionable ContentHasPath probe observed yet.";

        var territory = status.LastContentHasPathTerritoryType == 0 ? "(none)" : status.LastContentHasPathTerritoryType.ToString();
        var result = status.LastContentHasPathResult.Value ? "true" : "false";
        var selected = FormatDutyIpcDuty(status.LastContentHasPathSelectedContentFinderConditionId, status.LastContentHasPathSelectedDutyName);
        var blocker = FormatText(status.LastContentHasPathBlocker, "(none)");
        return $"ContentHasPath({territory})={result} | candidates {status.LastContentHasPathCandidateCount} / compatible {status.LastContentHasPathCompatibleCandidateCount} | selected {selected} | blocker {blocker}";
    }

    private static string FormatDutyIpcFailureStatus(DadDutyIpcStatus status)
    {
        var runId = string.IsNullOrWhiteSpace(status.LastRunId) ? "(none)" : status.LastRunId;
        var territory = status.LastTerritoryType == 0 ? "(none)" : status.LastTerritoryType.ToString();
        var failure = string.IsNullOrWhiteSpace(status.LastFailure) ? "(none)" : status.LastFailure;
        return $"run {runId} | territory {territory} | bareMode {status.LastBareMode} | failure {failure}";
    }

    private static string FormatDutyIpcCleanupStatus(DadDutyIpcStatus status)
    {
        var cleanupUtc = status.LastCleanupUtc?.ToString("O") ?? "(never)";
        var failedCommands = status.LastCleanupFailedCommands.Count == 0
            ? "(none)"
            : string.Join(", ", status.LastCleanupFailedCommands);
        return $"{status.LastCleanupResult} | at {cleanupUtc} | failed {failedCommands}";
    }

    private static string FormatDutyIpcDuty(uint contentFinderConditionId, string dutyName)
    {
        if (contentFinderConditionId == 0)
            return "(none)";

        return string.IsNullOrWhiteSpace(dutyName)
            ? $"#{contentFinderConditionId}"
            : $"#{contentFinderConditionId} {dutyName}";
    }

    private void EnsureEndpointDraft(Configuration configuration)
    {
        if (endpointDraftInitialized)
            return;

        ResetEndpointDraft(configuration);
    }

    private void ResetEndpointDraft(Configuration configuration)
    {
        draftTransportBindHost = configuration.TransportBindHost;
        draftTransportBindPort = configuration.TransportBindPort;
        draftAuthorityTargetHost = configuration.AuthorityTargetHost;
        draftAuthorityTargetPort = configuration.AuthorityTargetPort;
        endpointDraftInitialized = true;
    }

    private bool HasPendingEndpointDraftChanges(Configuration configuration)
    {
        return !string.Equals(draftTransportBindHost.Trim(), configuration.TransportBindHost, StringComparison.Ordinal)
               || Math.Clamp(draftTransportBindPort, 0, 65535) != configuration.TransportBindPort
               || !string.Equals(draftAuthorityTargetHost.Trim(), configuration.AuthorityTargetHost, StringComparison.Ordinal)
               || Math.Clamp(draftAuthorityTargetPort, 0, 65535) != configuration.AuthorityTargetPort;
    }

    private void ApplyEndpointDraft(Configuration configuration)
    {
        var bindHost = draftTransportBindHost.Trim();
        var bindPort = Math.Clamp(draftTransportBindPort, 0, 65535);
        var authorityTargetHost = draftAuthorityTargetHost.Trim();
        var authorityTargetPort = Math.Clamp(draftAuthorityTargetPort, 0, 65535);

        var bindChanged = !string.Equals(bindHost, configuration.TransportBindHost, StringComparison.Ordinal)
                          || bindPort != configuration.TransportBindPort;
        var authorityTargetChanged = !string.Equals(authorityTargetHost, configuration.AuthorityTargetHost, StringComparison.Ordinal)
                                     || authorityTargetPort != configuration.AuthorityTargetPort;
        if (!bindChanged && !authorityTargetChanged)
            return;

        configuration.TransportBindHost = bindHost;
        configuration.TransportBindPort = bindPort;
        configuration.AuthorityTargetHost = authorityTargetHost;
        configuration.AuthorityTargetPort = authorityTargetPort;
        configuration.Save();

        draftTransportBindHost = bindHost;
        draftTransportBindPort = bindPort;
        draftAuthorityTargetHost = authorityTargetHost;
        draftAuthorityTargetPort = authorityTargetPort;
        endpointDraftInitialized = true;

        plugin.ApplyEndpointConfiguration(bindChanged, authorityTargetChanged);
    }

    private static string FormatText(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string BuildDtrGlyphSignature(Configuration configuration)
        => $"{configuration.DtrIconEnabled}\n{configuration.DtrIconDisabled}";

    private static string BuildWaitPolicySignature(Configuration configuration)
        => $"{configuration.ParticipantReadyTimeoutSeconds}\n{configuration.AssemblyTimeoutSeconds}\n{configuration.HeartbeatStaleSeconds}\n{configuration.LeaseDurationSeconds}\n{configuration.CancelAckTimeoutSeconds}";

    private static string BuildCharacterLoadSignature(DadCharacterLoadInstruction instruction)
        => $"{instruction.CommandTemplate}\n{instruction.TimeoutSeconds}";
}
