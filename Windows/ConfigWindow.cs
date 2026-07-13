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
    private static readonly string[] CompletionKillModes = { "None", "Close game client", "Shut down PC" };
    private static readonly Vector2 MinimumWindowSize = new(700f, 540f);
    private readonly Plugin plugin;
    private string draftCompletionCommands = string.Empty;
    private bool completionDraftInitialized;
    private string draftServerHost = string.Empty;
    private int draftServerPort;
    private bool endpointDraftInitialized;
    private string draftSharedSecret = string.Empty;
    private bool sharedSecretDraftInitialized;
    private IReadOnlyList<DadEndpointHostOption> endpointHostOptions = [];
    private DateTime endpointHostOptionsLoadedUtc = DateTime.MinValue;
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

            if (ImGui.BeginTabItem("Completion & Safety"))
            {
                DrawCompletionSafetyTab(configuration);
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

    // Feature batch A (dadfeatures20260620b): exposes the advanced gate, party-validation override,
    // and completion actions. Legacy kill settings are only shown when Advanced mode is on.
    private void DrawCompletionSafetyTab(Configuration configuration)
    {
        var advanced = configuration.AdvancedModeEnabled;
        if (ImGui.Checkbox("Advanced mode (show legacy options)", ref advanced))
        {
            configuration.AdvancedModeEnabled = advanced;
            configuration.Save();
        }

        DrawStatusRow("Advanced mode", configuration.AdvancedModeEnabled
            ? "On - advanced options visible."
            : "Off. Also toggle with /dad advanced.");

        DrawSectionHeader("Party validation");

        var partyOverride = configuration.PartyValidationOverrideEnabled;
        if (ImGui.Checkbox("Party validation override", ref partyOverride))
        {
            configuration.PartyValidationOverrideEnabled = partyOverride;
            configuration.Save();
        }

        ImGui.TextWrapped("When on, Dad skips runtime connectivity/readiness checks before starting a run. Duplicate-slot checks stay enforced. Default off.");

        DrawSectionHeader("Integrations");

        var questionableBridge = configuration.QuestionableBridgeEnabled;
        if (ImGui.Checkbox("Questionable reflection bridge (AutoDuty/ADS handoff)", ref questionableBridge))
        {
            configuration.QuestionableBridgeEnabled = questionableBridge;
            configuration.Save();
        }

        ImGui.TextWrapped("Disabling restores any patched Questionable values and stops the bridge. Leave on unless it causes issues.");

        DrawSectionHeader("Global completion defaults");
        ImGui.TextWrapped("Used by presets that do not define their own completion actions.");

        var actions = configuration.CompletionActions;

        var playSound = actions.PlaySound;
        if (ImGui.Checkbox("Play sound on completion", ref playSound))
        {
            actions.PlaySound = playSound;
            configuration.Save();
        }

        if (actions.PlaySound)
        {
            var soundId = actions.SoundEffectId;
            if (ImGui.InputInt("Sound effect (1-16)", ref soundId))
            {
                actions.SoundEffectId = Math.Clamp(soundId, 1, 16);
                configuration.Save();
            }
        }

        var runCommands = actions.RunCommands;
        if (ImGui.Checkbox("Run commands on completion", ref runCommands))
        {
            actions.RunCommands = runCommands;
            configuration.Save();
        }

        if (actions.RunCommands)
        {
            if (!completionDraftInitialized)
            {
                draftCompletionCommands = string.Join("\n", actions.Commands);
                completionDraftInitialized = true;
            }

            if (ImGui.InputTextMultiline("Commands (one per line)", ref draftCompletionCommands, 2048, new Vector2(-1f, 90f)))
            {
                var committedSignature = BuildCompletionCommandsSignature(configuration);
                actions.Commands = draftCompletionCommands
                    .Split('\n')
                    .Select(static command => command.Trim())
                    .Where(static command => command.Length > 0)
                    .ToList();
                plugin.QueueDebouncedConfigurationSave(
                    "completion-commands",
                    committedSignature,
                    () => BuildCompletionCommandsSignature(configuration));
            }

            ImGui.TextDisabled("Runs after the run completes. Example: /vmx resume (or any slash command).");
        }

        DrawSectionHeader("Post-run utilities");
        var utilities = actions.Utilities ??= new DadPostRunUtilities();

        var openGearCoffers = utilities.OpenGearCoffers;
        if (ImGui.Checkbox("Open gear coffers", ref openGearCoffers))
        {
            utilities.OpenGearCoffers = openGearCoffers;
            configuration.Save();
        }

        var registerTripleTriad = utilities.RegisterTripleTriadCards;
        if (ImGui.Checkbox("Register Triple Triad cards", ref registerTripleTriad))
        {
            utilities.RegisterTripleTriadCards = registerTripleTriad;
            configuration.Save();
        }

        var sellTripleTriad = utilities.SellTripleTriadCards;
        if (ImGui.Checkbox("Sell Triple Triad cards", ref sellTripleTriad))
        {
            utilities.SellTripleTriadCards = sellTripleTriad;
            configuration.Save();
        }

        var gcHandIn = utilities.GrandCompanyHandInViaAutoRetainer;
        if (ImGui.Checkbox("Grand Company hand-in via AutoRetainer", ref gcHandIn))
        {
            utilities.GrandCompanyHandInViaAutoRetainer = gcHandIn;
            configuration.Save();
        }

        if (utilities.GrandCompanyHandInViaAutoRetainer)
        {
            var gcCommand = utilities.GrandCompanyHandInCommand;
            if (ImGui.InputText("AutoRetainer GC command", ref gcCommand, 128))
            {
                utilities.GrandCompanyHandInCommand = gcCommand.Trim();
                configuration.Save();
            }
        }

        ImGui.Separator();
        if (configuration.AdvancedModeEnabled)
        {
            ImGui.TextUnformatted("Legacy completion kill actions");
            var killMode = (int)actions.KillMode;
            if (ImGui.Combo("On completion", ref killMode, CompletionKillModes, CompletionKillModes.Length))
            {
                actions.KillMode = (DadCompletionKillMode)Math.Clamp(killMode, 0, CompletionKillModes.Length - 1);
                configuration.Save();
            }

            ImGui.TextWrapped("Legacy kill actions are preserved for config compatibility but disabled. Dad will not close the game client or schedule OS shutdown.");
        }
        else if (actions.KillMode != DadCompletionKillMode.None)
        {
            DrawStatusRow("Completion kill action", $"{actions.KillMode} configured but disabled. Enable Advanced mode (/dad advanced) only to view/change it.");
        }

        ImGui.Separator();
        DrawStatusRow("Issue report", "Run /dad report to write an anonymized diagnostic dump for GitHub issues.");
    }

    private static string BuildCompletionCommandsSignature(Configuration configuration)
        => string.Join("\n", configuration.CompletionActions.Commands);

    private void DrawGeneralTab(Configuration configuration)
    {
        EnsureEndpointDraft(configuration);

        var enabled = configuration.PluginEnabled;
        if (ImGui.Checkbox("Plugin enabled", ref enabled))
            plugin.SetPluginEnabled(enabled, printStatus: false);

        var runAsServerDad = configuration.RunAsServerDad;
        if (ImGui.Checkbox("Run as Dad Coordinator", ref runAsServerDad))
        {
            plugin.SetRunAsServerDad(runAsServerDad);
            ResetEndpointDraft(configuration);
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
        if (configuration.DebugUiEnabled)
        {
            DrawStatusRow("Questionable cosmetic", FormatQuestionableCosmeticStatus(bridgeStatus));
            DrawStatusRow("Dad duty IPC probe", FormatDutyIpcProbeStatus(dutyIpcStatus));
            DrawStatusRow("Dad duty IPC run", FormatDutyIpcFailureStatus(dutyIpcStatus));
            DrawStatusRow("Dad duty IPC cleanup", FormatDutyIpcCleanupStatus(dutyIpcStatus));
        }

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
        ImGui.TextUnformatted(configuration.RunAsServerDad ? "Dad Coordinator listener" : "Dad Coordinator connection");
        ImGui.TextWrapped(configuration.RunAsServerDad
            ? "Listen on 127.0.0.1 for same-host clients. Use a LAN interface address and shared secret for multi-host clients."
            : "Enter the Dad Coordinator LAN IP/DNS or 127.0.0.1 for same-host use.");

        ImGui.TextUnformatted(configuration.RunAsServerDad ? "Listen host" : "Dad Coordinator host");
        var comboWidth = ImGui.GetFontSize() * 13f;
        var hostInputWidth = MathF.Max(180f, ImGui.GetContentRegionAvail().X - comboWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SetNextItemWidth(hostInputWidth);
        ImGui.InputText("##dad-endpoint-host-input", ref draftServerHost, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(comboWidth);
        DrawEndpointHostDropdown();
        ImGui.InputInt(configuration.RunAsServerDad ? "Listen port" : "Dad Coordinator port", ref draftServerPort);
        draftServerPort = Math.Clamp(draftServerPort, 1, 65535);

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

        DrawStatusRow("Hub endpoint", FormatText(plugin.TransportService.GetConfiguredAuthorityEndpoint(), "(invalid endpoint)"));
        DrawStatusRow("Connection", plugin.TransportService.CurrentTransport.ConnectionStatus);
        DrawStatusRow("Protocol", plugin.TransportService.CurrentTransport.ProtocolVersion.ToString());
        DrawLanSharedSecretSetup(configuration);

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

        var vermaxionTimeout = configuration.VermaxionHoldTimeoutSeconds;
        if (ImGui.InputInt("VERMAXION hold timeout (s)", ref vermaxionTimeout))
        {
            var committedSignature = BuildWaitPolicySignature(configuration);
            configuration.VermaxionHoldTimeoutSeconds = Math.Max(3600, vermaxionTimeout);
            plugin.QueueDebouncedConfigurationSave(
                "wait-policy",
                committedSignature,
                () => BuildWaitPolicySignature(configuration));
        }

        var autoRetainerBusyTimeout = configuration.AutoRetainerBusyTimeoutSeconds;
        if (ImGui.InputInt("AutoRetainer busy timeout (s)", ref autoRetainerBusyTimeout))
        {
            var committedSignature = BuildWaitPolicySignature(configuration);
            configuration.AutoRetainerBusyTimeoutSeconds = Math.Max(60, autoRetainerBusyTimeout);
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

        var heartbeatInterval = configuration.HeartbeatIntervalSeconds;
        if (ImGui.InputInt("Heartbeat interval (s)", ref heartbeatInterval))
        {
            var committedSignature = BuildWaitPolicySignature(configuration);
            configuration.HeartbeatIntervalSeconds = Math.Max(2, heartbeatInterval);
            plugin.QueueDebouncedConfigurationSave(
                "wait-policy",
                committedSignature,
                () => BuildWaitPolicySignature(configuration));
        }

        var peerCatalogRefreshInterval = configuration.PeerCatalogRefreshIntervalSeconds;
        if (ImGui.InputInt("Peer catalog refresh interval (s)", ref peerCatalogRefreshInterval))
        {
            var committedSignature = BuildWaitPolicySignature(configuration);
            configuration.PeerCatalogRefreshIntervalSeconds = Math.Max(10, peerCatalogRefreshInterval);
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
        if (ImGui.CollapsingHeader("Command reference"))
        {
            ImGui.BulletText("/dad ws -> reset Dad windows to 1,1");
            ImGui.BulletText("/dad j -> jump Dad windows somewhere visible");
            ImGui.BulletText("/dad status -> print the live shell summary to chat");
            ImGui.BulletText("/dad mini -> toggle the compact cached status and Stop-all window");
            ImGui.BulletText("Offline Client Dads automatically show reconnect progress and retry until DAD is disabled");
            ImGui.BulletText("/dad wizard or /dad setup -> open the Dad Setup Wizard");
            ImGui.BulletText("/dad debug, /dad debug on, /dad debug off -> toggle verbose UI diagnostics");
            ImGui.BulletText("/dad krangle -> toggle local operator-name krangling");
            ImGui.BulletText("/dad run or /dad run local -> start a local Sastasha demo");
            ImGui.BulletText("/dad run coordinator -> start a Dad Coordinator Sastasha premade demo");
            ImGui.BulletText("/dad run msq -> start a Dad Coordinator Daily MSQ demo");
            ImGui.BulletText("/dad run commend -> start a Dad Coordinator commendation demo");
            ImGui.BulletText("/dad run planner -> start the current startable Preset Planner request");
            ImGui.BulletText("/dad cancel -> cancel the active orchestration run");
            ImGui.BulletText("Stop all is available in /dad mini and requires a second click within five seconds");

            if (configuration.DebugUiEnabled)
            {
                ImGui.Separator();
                ImGui.TextDisabled("Diagnostics");
                ImGui.BulletText("/dad test planner-groups -> run non-starting planner group IPC diagnostics");
                ImGui.BulletText("/dad test profiles -> profile owner/cache/revision diagnostics");
                ImGui.BulletText("/dad test launch-profiles -> launch path/mapping diagnostics");
                ImGui.BulletText("/dad test workers -> distributed worker diagnostics");
                ImGui.BulletText("/dad test duty-ipc current|territory <id>|cfc <id> -> diagnose Dad duty IPC availability");
            }
        }
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
        ImGui.TextDisabled("Full account tools (merge / delete / forget copies) live in the main window under Crew -> Roster state -> Account tools.");
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
        ImGui.TextWrapped("Select what Dad does for combat when it starts a duty operation.");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use FrenRider is the default: Dad queues first, sends /fr on after confirmed duty entry, then FrenRider owns in-duty behavior, ADS handoff, stop, and exit choices. Normal planner, manual, and scheduler runs do not send disable commands. Only a successful final dad.Duty.Run IPC session sends the five-command cleanup set.");
        ImGui.Separator();

        DrawCombatRotationModeRadio(
            configuration,
            DadCombatRotationMode.ForceCommands,
            "Force BossMod + auto-rotation");
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
        ImGui.TextUnformatted("Roadmap");
        foreach (var item in PluginInfo.Phases)
            ImGui.BulletText(item);

        ImGui.Separator();
        ImGui.TextUnformatted("What Dad verifies");
        foreach (var item in PluginInfo.Tests)
            ImGui.BulletText(item);
    }

    private static void DrawStatusRow(string label, string value)
        => DrawStatusRow(label, value, 180f);

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

    private static void DrawSectionHeader(string title)
    {
        ImGui.Separator();
        ImGui.TextUnformatted(title);
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
        draftServerHost = configuration.RunAsServerDad
            ? configuration.ServerListenHost
            : configuration.ServerDadHost;
        draftServerPort = configuration.RunAsServerDad
            ? configuration.ServerListenPort
            : configuration.ServerDadPort;
        endpointDraftInitialized = true;
    }

    private bool HasPendingEndpointDraftChanges(Configuration configuration)
    {
        var configuredHost = configuration.RunAsServerDad
            ? configuration.ServerListenHost
            : configuration.ServerDadHost;
        var configuredPort = configuration.RunAsServerDad
            ? configuration.ServerListenPort
            : configuration.ServerDadPort;
        return !string.Equals(draftServerHost.Trim(), configuredHost, StringComparison.Ordinal)
               || Math.Clamp(draftServerPort, 1, 65535) != configuredPort;
    }

    private void ApplyEndpointDraft(Configuration configuration)
    {
        var host = string.IsNullOrWhiteSpace(draftServerHost) ? "127.0.0.1" : draftServerHost.Trim();
        var port = Math.Clamp(draftServerPort, 1, 65535);
        if (!HasPendingEndpointDraftChanges(configuration))
            return;

        plugin.ApplyTransportEndpoint(host, port);

        draftServerHost = host;
        draftServerPort = port;
        endpointDraftInitialized = true;
    }

    private void DrawLanSharedSecretSetup(Configuration configuration)
    {
        EnsureSharedSecretDraft(configuration);

        ImGui.Separator();
        ImGui.TextUnformatted("LAN shared secret");
        ImGui.TextWrapped(configuration.RunAsServerDad
            ? "Use this Coordinator Dad as the shared-secret source. Paste the same secret into every Client Dad. Dad never sends the secret over the transport."
            : "Paste the Coordinator Dad's shared secret here. Dad never fetches or sends the secret over the transport.");

        ImGui.SetNextItemWidth(MathF.Min(420f, ImGui.GetContentRegionAvail().X));
        var label = configuration.RunAsServerDad ? "Shared secret" : "Paste shared secret";
        ImGui.InputText(label, ref draftSharedSecret, 128);

        var hasPendingSecretChanges = HasPendingSharedSecretDraftChanges(configuration);
        if (hasPendingSecretChanges)
            ImGui.TextDisabled("Shared secret draft has unapplied changes.");

        if (ImGui.Button("Apply shared secret"))
            ApplySharedSecretDraft(configuration);
        if (!hasPendingSecretChanges)
            ImGui.BeginDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Revert shared secret"))
            ResetSharedSecretDraft(configuration);
        if (!hasPendingSecretChanges)
            ImGui.EndDisabled();

        if (configuration.RunAsServerDad)
        {
            if (ImGui.Button("Generate LAN shared secret"))
                SetSharedSecret(configuration, plugin.GenerateAndApplyTransportSharedSecret());

            ImGui.SameLine();
            if (string.IsNullOrWhiteSpace(configuration.TransportSharedSecret))
                ImGui.BeginDisabled();
            if (ImGui.Button("Copy shared secret"))
            {
                ImGui.SetClipboardText(configuration.TransportSharedSecret);
                plugin.PrintStatus("Copied LAN shared secret.");
            }
            if (string.IsNullOrWhiteSpace(configuration.TransportSharedSecret))
                ImGui.EndDisabled();
        }

        var transport = plugin.TransportService.CurrentTransport;
        DrawStatusRow("Configured endpoint", FormatText(transport.ConfiguredEndpoint, "(none)"));
        DrawStatusRow("Advertised endpoint", FormatText(transport.AdvertisedEndpoint, "(none)"));
        DrawStatusRow("Secret required", transport.SharedSecretRequired ? "Yes" : "No (loopback endpoint)");
        DrawStatusRow("Secret configured", transport.SharedSecretConfigured ? "Yes" : "No");
        if (!string.IsNullOrWhiteSpace(transport.LastAuthOrProtocolError))
            DrawStatusRow("Auth/protocol", transport.LastAuthOrProtocolError);
        DrawStatusRow("Publish epoch", FormatText(transport.HubRosterPublishEpochId, "(none)"));
        DrawStatusRow("Publish generation", transport.HubRosterPublishGeneration.ToString());
        DrawStatusRow("Roster participants", $"{transport.PublishedParticipantCount} published | {transport.KnownParticipantCount} known");
        DrawStatusRow("Transport queues", $"{transport.PendingTransportEventCount} event(s) | {transport.PendingOutboundOperationCount} outbound");
        DrawStatusRow("Last publish", $"{FormatTime(transport.LastRosterPublishUtc)} | {FormatText(transport.LastRosterPublishReason, "(none)")}");
        if (transport.CoalescedRosterPublishCount > 0)
            DrawStatusRow("Coalesced publishes", transport.CoalescedRosterPublishCount.ToString());
        if (!string.IsNullOrWhiteSpace(transport.LastTransportTimeoutSummary))
            DrawStatusRow("Transport timeout", transport.LastTransportTimeoutSummary);
    }

    private void EnsureSharedSecretDraft(Configuration configuration)
    {
        if (sharedSecretDraftInitialized &&
            string.Equals(draftSharedSecret, configuration.TransportSharedSecret, StringComparison.Ordinal))
        {
            return;
        }

        if (!sharedSecretDraftInitialized || !HasPendingSharedSecretDraftChanges(configuration))
            ResetSharedSecretDraft(configuration);
    }

    private void ResetSharedSecretDraft(Configuration configuration)
    {
        draftSharedSecret = configuration.TransportSharedSecret;
        sharedSecretDraftInitialized = true;
    }

    private bool HasPendingSharedSecretDraftChanges(Configuration configuration)
        => !string.Equals(draftSharedSecret.Trim(), configuration.TransportSharedSecret, StringComparison.Ordinal);

    private void ApplySharedSecretDraft(Configuration configuration)
        => SetSharedSecret(configuration, draftSharedSecret.Trim());

    private void SetSharedSecret(Configuration configuration, string sharedSecret)
    {
        sharedSecret = sharedSecret.Trim();
        plugin.SetTransportSharedSecret(sharedSecret);
        ResetSharedSecretDraft(configuration);
    }

    private void DrawEndpointHostDropdown()
    {
        var options = GetEndpointHostOptions();
        var current = options.FirstOrDefault(option =>
            string.Equals(option.Host, draftServerHost.Trim(), StringComparison.OrdinalIgnoreCase));
        var preview = current?.Label ?? "Select IP/host";
        if (!ImGui.BeginCombo("##dad-endpoint-host-options", preview))
            return;

        foreach (var option in options)
        {
            var selected = string.Equals(option.Host, draftServerHost.Trim(), StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable($"{option.Label}##{option.Host}", selected))
                draftServerHost = option.Host;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private IReadOnlyList<DadEndpointHostOption> GetEndpointHostOptions()
    {
        if (endpointHostOptions.Count > 0 &&
            DateTime.UtcNow - endpointHostOptionsLoadedUtc < TimeSpan.FromSeconds(10))
        {
            return endpointHostOptions;
        }

        endpointHostOptions = DadEndpointHostOptions.GetLocalIpv4Options();
        endpointHostOptionsLoadedUtc = DateTime.UtcNow;
        return endpointHostOptions;
    }

    private static string FormatText(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string FormatTime(DateTime? value)
        => value.HasValue ? value.Value.ToLocalTime().ToString("HH:mm:ss") : "never";

    private static string BuildDtrGlyphSignature(Configuration configuration)
        => $"{configuration.DtrIconEnabled}\n{configuration.DtrIconDisabled}";

    private static string BuildWaitPolicySignature(Configuration configuration)
        => $"{configuration.ParticipantReadyTimeoutSeconds}\n{configuration.VermaxionHoldTimeoutSeconds}\n{configuration.AutoRetainerBusyTimeoutSeconds}\n{configuration.AssemblyTimeoutSeconds}\n{configuration.HeartbeatIntervalSeconds}\n{configuration.HeartbeatStaleSeconds}\n{configuration.PeerCatalogRefreshIntervalSeconds}\n{configuration.LeaseDurationSeconds}\n{configuration.CancelAckTimeoutSeconds}";

    private static string BuildCharacterLoadSignature(DadCharacterLoadInstruction instruction)
        => $"{instruction.CommandTemplate}\n{instruction.TimeoutSeconds}";
}
