using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using dad.Models;
using dad.Services;

namespace dad.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private static readonly string[] DtrModes = { "Text only", "Icon + text", "Icon only" };
    private static readonly string[] PreDutyRepairModes =
    {
        "Self",
        "NPC, excluding inns",
        "Nearby NPC, no teleport/inn",
    };
    private const string CommunityDiscordUrl = "https://discord.gg/VsXqydsvpu";
    private static readonly Vector2 MinimumWindowSize = new(700f, 540f);
    private readonly Plugin plugin;
    private readonly DadConnectionEditor connectionEditor;
    private string draftCompletionCommands = string.Empty;
    private bool completionDraftInitialized;
    private string completionCommandValidation = string.Empty;
    private Vector2? pendingPosition;
    private bool resetPositionConditionNextDraw;
    private string pendingDeleteAccountId = string.Empty;

    public ConfigWindow(Plugin plugin) : base($"{PluginInfo.DisplayName} Settings##Config", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        connectionEditor = new DadConnectionEditor(plugin);
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

        DadUi.Heading("DAD SETTINGS", "Everyday setup first; advanced and debug details stay close when you need them.");
        DadUi.Badge(configuration.PluginEnabled ? "DAD enabled" : "DAD paused",
            configuration.PluginEnabled ? DadUiTone.Success : DadUiTone.Warning);
        ImGui.SameLine();
        DadUi.Badge(configuration.RunAsServerDad ? "Coordinator" : "Client", DadUiTone.Info);
        ImGui.SameLine();
        DadUi.Badge(configuration.DebugUiEnabled ? "Debug details shown" : "Everyday view",
            configuration.DebugUiEnabled ? DadUiTone.Warning : DadUiTone.Neutral);
        ImGui.Spacing();

        if (ImGui.BeginTabBar("dad-config-tabs"))
        {
            if (ImGui.BeginTabItem("Core & Connection"))
            {
                DrawGeneralTab(configuration);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Accounts"))
            {
                DrawSchedulerTab(configuration);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Combat"))
            {
                DrawCombatRotationTab(configuration);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Safety & Finish"))
            {
                DrawCompletionSafetyTab(configuration);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("About & Support"))
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
        DadUi.Heading("SAFETY & FINISH", "Set readiness safeguards and what happens after a successful run.");
        if (DadUi.Button("Guide: Create a Preset", DadUiTone.Accent))
            plugin.OpenSetupWizard(DadGuideFlow.FirstPreset);
        ImGui.SameLine();
        ImGui.TextDisabled("Configure per-preset stop and finish rules with live validation.");

        var advanced = configuration.AdvancedModeEnabled;
        if (ImGui.Checkbox("Show advanced controls", ref advanced))
        {
            configuration.AdvancedModeEnabled = advanced;
            configuration.Save();
        }

        DrawStatusRow("Advanced mode", configuration.AdvancedModeEnabled
            ? "On - advanced options visible."
            : "Off. Also toggle with /dad advanced.");

        DrawSectionHeader("Start safeguards");

        var partyOverride = configuration.PartyValidationOverrideEnabled;
        if (ImGui.Checkbox("Skip live party readiness checks (unsafe)", ref partyOverride))
        {
            configuration.PartyValidationOverrideEnabled = partyOverride;
            configuration.Save();
        }

        ImGui.TextWrapped("When on, DAD skips runtime connectivity and readiness checks before starting. Duplicate-slot checks stay enforced. Leave this off for normal play.");

        var promptOverride = configuration.AllowFreshUnprovenPromptApproval;
        if (ImGui.Checkbox("Allow one fresh unproven prompt approval (unsafe)", ref promptOverride))
        {
            configuration.AllowFreshUnprovenPromptApproval = promptOverride;
            configuration.Save();
        }

        ImGui.TextWrapped("Default off. When enabled, DAD may approve one fresh, sole, ready prompt tied to the current command attempt if localized prompt text cannot be proven. Every use is logged as a warning and audit event.");

        DrawSectionHeader("Pre-duty repair");
        configuration.PreDutyRepairPolicy ??= new DadPreDutyRepairPolicy();
        var repairPolicy = configuration.PreDutyRepairPolicy.Normalize();
        var repairEnabled = repairPolicy.Enabled;
        if (ImGui.Checkbox("Repair equipped gear before queue-capable duties", ref repairEnabled))
        {
            repairPolicy.Enabled = repairEnabled;
            configuration.Save();
        }

        ImGui.TextWrapped("When enabled, every assigned worker must prove equipped durability at or above the threshold before it can reach the queue barrier. Blunderville is excluded.");
        ImGui.BeginDisabled(!repairPolicy.Enabled);
        var repairThreshold = repairPolicy.ThresholdPercent;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("Durability threshold", ref repairThreshold, 1, 100, "%d%%"))
        {
            repairPolicy.ThresholdPercent = Math.Clamp(repairThreshold, 1, 100);
            configuration.Save();
        }

        var repairMode = (int)repairPolicy.Mode;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.Combo("Repair route", ref repairMode, PreDutyRepairModes, PreDutyRepairModes.Length))
        {
            repairPolicy.Mode = (DadPreDutyRepairMode)Math.Clamp(repairMode, 0, PreDutyRepairModes.Length - 1);
            configuration.Save();
        }
        ImGui.EndDisabled();
        DrawStatusRow("ADS mode", repairPolicy.AdsMode);

        DrawSectionHeader("Integrations");

        var questionableBridge = configuration.QuestionableBridgeOptInEnabled;
        if (ImGui.Checkbox("Opt in to experimental AutoDuty / ADS handoff through Questionable", ref questionableBridge))
        {
            configuration.QuestionableBridgeOptInEnabled = questionableBridge;
            configuration.Save();
        }

        ImGui.TextWrapped("Off by default, including upgrades from the legacy default-on setting. This compatibility bridge patches Questionable runtime fields and may break after Questionable updates. Disabling restores any patched values DAD still owns.");

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
                if (DadCompletionCommandRules.TryNormalizeCustomCommands(
                        draftCompletionCommands.Split('\n'),
                        out var normalizedCommands,
                        out completionCommandValidation))
                {
                    var committedSignature = BuildCompletionCommandsSignature(configuration);
                    actions.Commands = normalizedCommands;
                    plugin.QueueDebouncedConfigurationSave(
                        "completion-commands",
                        committedSignature,
                        () => BuildCompletionCommandsSignature(configuration));
                }
            }

            ImGui.TextDisabled("Runs after the run completes. Example: /vmx resume (or any slash command).");
            if (!string.IsNullOrWhiteSpace(completionCommandValidation))
                ImGui.TextColored(new Vector4(1f, .35f, .35f, 1f), completionCommandValidation);
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
                if (DadCompletionCommandRules.TryNormalizeGrandCompanyHandInCommand(
                        gcCommand,
                        out var normalizedCommand,
                        out completionCommandValidation))
                {
                    utilities.GrandCompanyHandInCommand = normalizedCommand;
                    configuration.Save();
                }
            }
            ImGui.TextDisabled("Only the exact /ays command root is accepted for this native command.");
            if (!string.IsNullOrWhiteSpace(completionCommandValidation))
                ImGui.TextColored(new Vector4(1f, .35f, .35f, 1f), completionCommandValidation);
        }

        ImGui.Separator();
        if (actions.KillMode != DadCompletionKillMode.None)
        {
            DrawStatusRow("Legacy completion value", $"{actions.KillMode} was loaded for compatibility and is a permanent no-op.");
            if (ImGui.Button("Clear disabled legacy completion value"))
            {
                actions.KillMode = DadCompletionKillMode.None;
                configuration.Save();
            }
        }
        var completionStatus = DadCompletionActionRunner.LastStatus;
        DrawStatusRow("Latest completion action", $"{completionStatus.SafeCode}: {completionStatus.Summary}");

        ImGui.Separator();
        DrawStatusRow("Issue report", "Run /dad report to write an anonymized diagnostic dump for GitHub issues.");
    }

    private static string BuildCompletionCommandsSignature(Configuration configuration)
        => string.Join("\n", configuration.CompletionActions.Commands);

    private void DrawGeneralTab(Configuration configuration)
    {
        DadUi.Heading("CORE & CONNECTION", "Enable DAD, choose this client's role, and connect the crew securely.");
        if (ImGui.BeginTable("dad-settings-role-guides", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
        {
            foreach (var target in new[] { DadGuideFlow.Coordinator, DadGuideFlow.Client })
            {
                var restricted = DadGuideReadiness.TryGetConnectionFlowRestriction(plugin, target, out var restriction);
                var coordinator = target == DadGuideFlow.Coordinator;
                ImGui.TableNextColumn();
                ImGui.BeginDisabled(restricted);
                if (DadUi.BeginCard($"dad-settings-role-{target}", 92f))
                {
                    DadUi.Heading(coordinator ? "COORDINATOR" : "CLIENT", coordinator
                        ? "Own plans, schedules, and crew dispatch."
                        : "Connect this DAD to the configured Coordinator.");
                    if (DadUi.Button($"Open {target} guide##dad-settings-role-guide-{target}", DadUiTone.Accent))
                        plugin.OpenSetupWizard(target);
                    if (restricted && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(restriction);
                    DadUi.EndCard();
                }
                ImGui.EndDisabled();
            }
            ImGui.EndTable();
        }
        DadUi.Section("Core", "The switches most players need for normal runs.");

        var enabled = configuration.PluginEnabled;
        if (ImGui.Checkbox("DAD enabled", ref enabled))
            plugin.SetPluginEnabled(enabled, printStatus: false);

        var runAsServerDad = configuration.RunAsServerDad;
        if (ImGui.Checkbox("This client coordinates the crew", ref runAsServerDad))
        {
            plugin.SetRunAsServerDad(runAsServerDad);
            connectionEditor.Reset(configuration);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Each DAD has one role. Enable this on the DAD that plans and dispatches work; turn it off for a Client. This Settings control is how an already configured DAD changes roles.");

        var localOnly = configuration.LocalOnlyModeEnabled;
        if (ImGui.Checkbox("Keep runs on this client only", ref localOnly))
        {
            configuration.LocalOnlyModeEnabled = localOnly;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("DAD will not route work to connected crew clients while this is enabled.");

        DadUi.Section("Status & privacy", "Choose what DAD shows locally; identity hiding never changes run contracts.");

        var dtr = configuration.DtrBarEnabled;
        if (ImGui.Checkbox("Show DAD in the server info bar (DTR)", ref dtr))
        {
            configuration.DtrBarEnabled = dtr;
            configuration.Save();
            plugin.UpdateDtrBar();
        }

        var krangle = configuration.KrangleOperatorNamesEnabled;
        if (ImGui.Checkbox("Hide operator names inside DAD", ref krangle))
        {
            configuration.KrangleOperatorNamesEnabled = krangle;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Changes local DAD labels only. Saved identities and run contracts stay unchanged.");

        var kranglerPrivacy = configuration.KranglerPrivacyLeaseEnabled;
        if (ImGui.Checkbox("Use Krangler privacy during active DAD-island work", ref kranglerPrivacy))
            plugin.SetKranglerPrivacyLeaseEnabled(kranglerPrivacy);
        ImGui.SameLine();
        ImGui.TextDisabled(plugin.KranglerPrivacyLeaseService.Snapshot.Status);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Runtime-only lease while a registered-island group is actively being formed or run by a direct plan, scheduler job, or authenticated inbound proposal. Idle and LAN-only DAD do not acquire it; DAD never changes Krangler's saved appearance settings.");

        var mode = configuration.DtrBarMode;
        if (ImGui.Combo("Server info display", ref mode, DtrModes, DtrModes.Length))
        {
            configuration.DtrBarMode = mode;
            configuration.Save();
            plugin.UpdateDtrBar();
        }

        var onIcon = configuration.DtrIconEnabled;
        if (ImGui.InputText("Enabled glyph", ref onIcon, 8))
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
        if (ImGui.InputText("Paused glyph", ref offIcon, 8))
        {
            var committedSignature = BuildDtrGlyphSignature(configuration);
            configuration.DtrIconDisabled = offIcon.Length <= 3 ? offIcon : offIcon[..3];
            plugin.QueueDebouncedConfigurationSave(
                "dtr-glyphs",
                committedSignature,
                () => BuildDtrGlyphSignature(configuration),
                plugin.UpdateDtrBar);
        }

        DadUi.Section("Connection & security", configuration.RunAsServerDad
            ? "Choose where crew clients connect to this Coordinator."
            : "Point this client at the Coordinator that owns the crew plan.");
        ImGui.TextUnformatted(configuration.RunAsServerDad ? "Coordinator listener" : "Coordinator connection");
        ImGui.TextWrapped(configuration.RunAsServerDad
            ? "Listen on 127.0.0.1 for same-host clients. Use a LAN interface address and shared secret for multi-host clients."
            : "Enter the Coordinator's LAN IP/DNS, or use 127.0.0.1 when both clients are on this PC.");

        connectionEditor.DrawEndpointFields(configuration, "dad-settings-connection", showApplyActions: true);

        DrawStatusRow("Coordinator endpoint", FormatText(plugin.TransportService.GetConfiguredAuthorityEndpoint(), "(invalid endpoint)"));
        DrawStatusRow("Connection", plugin.TransportService.CurrentTransport.ConnectionStatus);

        ImGui.Spacing();
        ImGui.TextUnformatted("Shared secret");
        ImGui.TextWrapped(configuration.RunAsServerDad
            ? "Use this Coordinator as the source. Paste the same secret into every Client; DAD never sends the secret over the connection."
            : "Paste the Coordinator's shared secret here. DAD never fetches or sends it over the connection.");
        connectionEditor.DrawSharedSecretFields(
            configuration,
            "dad-settings-connection",
            showApplyActions: true,
            showGenerateAndCopy: true);

        var transport = plugin.TransportService.CurrentTransport;
        DrawStatusRow("Configured endpoint", FormatText(transport.ConfiguredEndpoint, "(none)"));
        DrawStatusRow("Advertised endpoint", FormatText(transport.AdvertisedEndpoint, "(none)"));
        DrawStatusRow("Secret required", transport.SharedSecretRequired ? "Yes" : "No (loopback endpoint)");
        DrawStatusRow("Secret configured", transport.SharedSecretConfigured ? "Yes" : "No");
        if (!string.IsNullOrWhiteSpace(transport.LastAuthOrProtocolError))
            DrawStatusRow("Auth/protocol", transport.LastAuthOrProtocolError);
        DadUi.Section("Advanced timing", "The defaults suit normal play; tune these only when clients or plugins need longer waits.");
        var showAdvancedTiming = configuration.AdvancedModeEnabled;
        if (ImGui.Checkbox("Show advanced timing controls", ref showAdvancedTiming))
        {
            configuration.AdvancedModeEnabled = showAdvancedTiming;
            configuration.Save();
        }

        if (configuration.AdvancedModeEnabled)
            DrawWaitPolicyControls(configuration);
        else
            ImGui.TextDisabled("Hidden in everyday view. Enable here or use /dad advanced to reveal every timing control.");

        DadUi.Section("Commands & troubleshooting", "Useful shortcuts stay available without crowding everyday setup.");
        if (ImGui.CollapsingHeader("Command reference"))
        {
            ImGui.BulletText("/dad ws -> reset DAD windows to 1,1");
            ImGui.BulletText("/dad j -> jump DAD windows somewhere visible");
            ImGui.BulletText("/dad status -> print the live shell summary to chat");
            ImGui.BulletText("/dad mini -> toggle the compact cached status and Stop-all window");
            ImGui.BulletText("/dad quick or /dad qp -> send one registered slash command to connected Client DADs");
            ImGui.BulletText("Offline Client DADs automatically show reconnect progress and retry until DAD is disabled");
            ImGui.BulletText("/dad wizard or /dad setup -> open the DAD Setup Guide");
            ImGui.BulletText("/dad debug, /dad debug on, /dad debug off -> toggle verbose UI diagnostics");
            ImGui.BulletText("/dad krangle -> toggle local operator-name hiding");
            ImGui.BulletText("/dad run or /dad run local -> start a local Sastasha demo");
            ImGui.BulletText("/dad run coordinator -> start a Coordinator Sastasha premade demo");
            ImGui.BulletText("/dad run roulette -> start a Coordinator Daily Roulette demo (/dad run msq is a legacy alias)");
            ImGui.BulletText("/dad run commend -> start a Coordinator commendation demo");
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
                ImGui.BulletText("/dad test duty-ipc current|territory <id>|cfc <id> -> diagnose DAD duty IPC availability");
            }
        }
    }

    private void DrawWaitPolicyControls(Configuration configuration)
    {

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
    }

    private void DrawSchedulerTab(Configuration configuration)
    {
        DadUi.Heading("ACCOUNTS", "Teach DAD how to recognize the crew and keep account ownership clear.");
        if (DadUi.Button("Guide: Build the Crew", DadUiTone.Accent))
            plugin.OpenSetupWizard(DadGuideFlow.Crew);
        ImGui.SameLine();
        ImGui.TextDisabled("Refresh ownership and review account mappings step by step.");
        ImGui.TextWrapped("Character permissions and account tools live in the main window under Crew.");

        if (configuration.DebugUiEnabled)
        {
            configuration.CharacterLoadInstruction ??= new DadCharacterLoadInstruction();
            configuration.CharacterLoadInstruction.Normalize();
            DrawSectionHeader("Character launch command (debug scaffolding)");
            var instruction = configuration.CharacterLoadInstruction;
            var loadEnabled = instruction.Enabled;
            if (ImGui.Checkbox("Enable character launch command", ref loadEnabled))
            {
                instruction.Enabled = loadEnabled;
                configuration.Save();
            }

            var loadDryRun = instruction.DryRun;
            if (ImGui.Checkbox("Simulate character launch (dry run)", ref loadDryRun))
            {
                instruction.DryRun = loadDryRun;
                configuration.Save();
            }

            var commandTemplate = instruction.CommandTemplate;
            if (ImGui.InputText("Launch command", ref commandTemplate, 256))
            {
                var committedSignature = BuildCharacterLoadSignature(instruction);
                instruction.CommandTemplate = commandTemplate;
                plugin.QueueDebouncedConfigurationSave(
                    "character-load",
                    committedSignature,
                    () => BuildCharacterLoadSignature(instruction));
            }

            var loadTimeout = instruction.TimeoutSeconds;
            if (ImGui.InputInt("Character launch timeout (s)", ref loadTimeout))
            {
                var committedSignature = BuildCharacterLoadSignature(instruction);
                instruction.TimeoutSeconds = Math.Clamp(loadTimeout, 30, 1800);
                plugin.QueueDebouncedConfigurationSave(
                    "character-load",
                    committedSignature,
                    () => BuildCharacterLoadSignature(instruction));
            }

            DrawStatusRow("Placeholders", "{Character}, {CharacterName}, {World}, {Account}");
        }
        DrawStatusRow("Scheduler state", plugin.SchedulerService.CurrentState.Summary);

        DrawSectionHeader("Crew roster");
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
        if (ImGui.Checkbox("Share hidden and ignored characters with connected DAD clients", ref showHidden))
        {
            configuration.RosterCatalog.ShowHiddenInRoster = showHidden;
            configuration.Save();
        }

        DrawStatusRow("Roster tab", "Account-scoped browser always exposes Active, Hidden, Ignored, Needs update, and All views.");
        DrawStatusRow("Preset slot pickers", "Assigned Active rows only; update-marked rows wait for refresh.");

        var queue = plugin.SchedulerService.GetQueueSnapshot();
        DrawStatusRow("Queue", queue.Summary);

        DrawAccountAliasEditor(configuration);
    }

    private void DrawAccountAliasEditor(Configuration configuration)
    {
        DrawSectionHeader("Account names");
        ImGui.TextDisabled("Full account tools (delete / forget copies) live in the main window under Crew -> Roster state -> Account tools.");
        if (DrawClearAllAccountDataButton("dad-config-clear-all-account-data"))
        {
            DrawDeleteAccountPopup();
            return;
        }

        var accounts = plugin.ConfigManager.GetAllAccounts();
        if (accounts.Count == 0)
        {
            ImGui.TextDisabled("No Dad account configs have been seen on this client.");
            DrawDeleteAccountPopup();
            return;
        }

        if (!ImGui.BeginTable("dad-account-aliases", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
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
        DadUi.Heading("COMBAT HANDOFF", "Choose who takes over combat after DAD confirms duty entry.");
        ImGui.TextWrapped("DAD always owns crew setup and queueing; this decides what happens once the duty begins.");
        ImGui.TextWrapped("Fren Rider and AI Duty Solver are required whenever DAD is enabled, regardless of the combat handoff selected below.");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use FrenRider is the default: Dad queues first, sends /fr on after confirmed duty entry, then FrenRider owns in-duty behavior, ADS handoff, stop, and exit choices. Normal planner, manual, and scheduler runs do not send disable commands. Only a successful final dad.Duty.Run IPC session sends the five-command cleanup set.");
        ImGui.Separator();

        DrawCombatRotationModeRadio(
            configuration,
            DadCombatRotationMode.ForceCommands,
            "Send BossMod and auto-rotation commands");
        DrawCombatRotationModeRadio(
            configuration,
            DadCombatRotationMode.UseFrenRider,
            "Hand combat to FrenRider (recommended)");
        DrawCombatRotationModeRadio(
            configuration,
            DadCombatRotationMode.DoNothing,
            "Leave combat to me");

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
                ImGui.TextWrapped("Dad does not send FrenRider, ADS, or rotation commands in this mode; both plugins remain unconditional DAD readiness requirements. User-owned play and leave behavior is expected.");
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
            DadFrenRiderPluginState.InstalledNotLoaded => "FrenRider installed but not loaded. DAD blocks all new work until it is loaded.",
            _ => "FrenRider not installed or not found. DAD blocks all new work until it is installed and loaded.",
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

    private void DrawAboutTab()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        DadUi.Heading($"{PluginInfo.DisplayName} v{version}", "Build a crew once, then turn repeat duties into a repeatable plan.");
        DadUi.Badge("Crew orchestration", DadUiTone.Accent);
        ImGui.SameLine();
        DadUi.Badge("Multi-client", DadUiTone.Info);
        ImGui.SameLine();
        DadUi.Badge("Safety-first stops", DadUiTone.Success);

        ImGui.Spacing();
        if (DadUi.Button("Open DAD Guide", DadUiTone.Accent))
            plugin.OpenSetupWizard();

        DadUi.Section("What DAD does");
        ImGui.TextWrapped("DAD coordinates connected FFXIV clients: it can coordinate same-account takeover or relog, assemble the party, queue a saved duty plan, and track cleanup. A missing game process must be started manually.");

        DadUi.Section("A typical run", "Most players only need this four-step loop.");
        ImGui.BulletText("Connect each Client to one Coordinator under Core & Connection.");
        ImGui.BulletText("Name accounts and review character ownership under Accounts.");
        if (plugin.Configuration.DebugUiEnabled)
            ImGui.BulletText("Optional launch-profile scaffolding is visible while /dad debug is enabled.");
        ImGui.BulletText("Build a preset in Plan, including duty, crew slots, stop rules, and finish actions.");
        ImGui.BulletText("Run it now or add it to a Schedule; use /dad mini for cached status and guarded Stop all.");

        DadUi.Section("Support & community", "Plugin-specific help belongs with the Dumpster Fire community.");
        ImGui.TextWrapped("For DAD setup help, bug reports, release news, and other Dumpster Fire plugins, join the Discord. Scroll down to the \"The Dumpster Fire\" channel. Please do not take DAD-specific support requests to the official Dalamud Discord.");
        if (DadUi.Button("Support on Ko-fi", DadUiTone.Accent))
            Util.OpenLink(PluginInfo.SupportUrl);
        ImGui.SameLine();
        if (DadUi.Button("Join Dumpster Fire Discord", DadUiTone.Info))
            Util.OpenLink(CommunityDiscordUrl);

        if (plugin.Configuration.DebugUiEnabled)
        {
            DadUi.Section("Developer details", "Visible while /dad debug is enabled.");
            ImGui.TextUnformatted("Roadmap");
            foreach (var item in PluginInfo.Phases)
                ImGui.BulletText(item);

            ImGui.TextUnformatted("What DAD verifies");
            foreach (var item in PluginInfo.Tests)
                ImGui.BulletText(item);
        }
    }

    private static void DrawStatusRow(string label, string value)
        => DrawStatusRow(label, value, 180f);

    private static void DrawStatusRow(string label, string value, float preferredLabelWidth)
        => DadUi.KeyValue(label, value, preferredLabelWidth);

    private static void DrawSectionHeader(string title)
        => DadUi.Section(title);

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
