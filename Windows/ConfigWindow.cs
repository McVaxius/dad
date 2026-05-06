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
        var account = plugin.ConfigManager.GetCurrentAccount();
        var profile = plugin.ConfigManager.GetActiveConfig();

        if (ImGui.BeginTabBar("dad-config-tabs"))
        {
            if (ImGui.BeginTabItem("General"))
            {
                DrawGeneralTab(configuration);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Profiles"))
            {
                DrawProfilesTab(account, profile);
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
            configuration.DtrIconEnabled = onIcon.Length <= 3 ? onIcon : onIcon[..3];
            configuration.Save();
            plugin.UpdateDtrBar();
        }

        var offIcon = configuration.DtrIconDisabled;
        if (ImGui.InputText("DTR disabled glyph", ref offIcon, 8))
        {
            configuration.DtrIconDisabled = offIcon.Length <= 3 ? offIcon : offIcon[..3];
            configuration.Save();
            plugin.UpdateDtrBar();
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
            configuration.ParticipantReadyTimeoutSeconds = Math.Max(30, readyTimeout);
            configuration.Save();
        }

        var assemblyTimeout = configuration.AssemblyTimeoutSeconds;
        if (ImGui.InputInt("Assembly timeout (s)", ref assemblyTimeout))
        {
            configuration.AssemblyTimeoutSeconds = Math.Max(10, assemblyTimeout);
            configuration.Save();
        }

        var staleTimeout = configuration.HeartbeatStaleSeconds;
        if (ImGui.InputInt("Heartbeat stale threshold (s)", ref staleTimeout))
        {
            configuration.HeartbeatStaleSeconds = Math.Max(3, staleTimeout);
            configuration.Save();
        }

        var leaseDuration = configuration.LeaseDurationSeconds;
        if (ImGui.InputInt("Lease duration (s)", ref leaseDuration))
        {
            configuration.LeaseDurationSeconds = Math.Max(5, leaseDuration);
            configuration.Save();
        }

        var cancelAck = configuration.CancelAckTimeoutSeconds;
        if (ImGui.InputInt("Cancel ack timeout (s)", ref cancelAck))
        {
            configuration.CancelAckTimeoutSeconds = Math.Max(2, cancelAck);
            configuration.Save();
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
        ImGui.BulletText("/dad cancel -> cancel the active orchestration run");
    }

    private void DrawProfilesTab(AccountConfig? account, CharacterConfig profile)
    {
        ImGui.Text($"Current account: {account?.AccountAlias ?? "(waiting for login)"}");

        var label = string.IsNullOrWhiteSpace(plugin.ConfigManager.SelectedCharacterKey)
            ? "(Account default)"
            : plugin.ConfigManager.SelectedCharacterKey;

        if (ImGui.BeginCombo("Character profile", label))
        {
            if (ImGui.Selectable("(Account default)", string.IsNullOrWhiteSpace(plugin.ConfigManager.SelectedCharacterKey)))
                plugin.ConfigManager.SelectedCharacterKey = string.Empty;

            foreach (var key in plugin.ConfigManager.GetSortedCharacterKeys())
            {
                if (ImGui.Selectable(key, key == plugin.ConfigManager.SelectedCharacterKey))
                    plugin.ConfigManager.SelectedCharacterKey = key;
            }

            ImGui.EndCombo();
        }

        var profileEnabled = profile.Enabled;
        if (ImGui.Checkbox("Profile enabled", ref profileEnabled))
        {
            profile.Enabled = profileEnabled;
            plugin.ConfigManager.SaveCurrentAccount();
            plugin.UpdateDtrBar();
        }

        var allowIpcStarts = profile.AllowIpcStarts;
        if (ImGui.Checkbox("Allow VERMAXION IPC starts", ref allowIpcStarts))
        {
            profile.AllowIpcStarts = allowIpcStarts;
            plugin.ConfigManager.SaveCurrentAccount();
        }

        var targetNotes = profile.TargetNotes;
        if (ImGui.InputTextMultiline("Operator notes", ref targetNotes, 512, new Vector2(-1f, 140f)))
        {
            profile.TargetNotes = targetNotes;
            plugin.ConfigManager.SaveCurrentAccount();
        }

        ImGui.TextWrapped("Dad now owns Server Dad authority, Client Dad worker coordination, readiness waits, leases, party assembly, and module routing. VERMAXION remains caller-only. Daily MSQ uses Dad's internal premade lane; commendation and Astrope use Dad's internal aura lane.");
    }

    private void DrawCombatRotationTab(Configuration configuration)
    {
        ImGui.TextWrapped("Select what Dad does when it starts a duty operation. Use FrenRider is the default: Dad sends /fr on once before queue/routing begins, then FrenRider owns in-duty behavior, ADS handoff, stop, and exit choices. Selecting the menu option does not send commands, and Dad does not send a FrenRider disable command.");
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
            DadFrenRiderPluginState.Loaded => "FrenRider installed and loaded. Dad will send /fr on before starting a duty operation.",
            DadFrenRiderPluginState.InstalledNotLoaded => "FrenRider installed but not loaded. Dad will block Use FrenRider duty operations until it is loaded.",
            _ => "FrenRider not installed or not found. Dad will block Use FrenRider duty operations until it is installed and loaded.",
        };
        ImGui.TextColored(
            color,
            statusText);
        ImGui.TextWrapped("Status color uses Dalamud installed-plugin state only: green means installed and loaded, yellow means installed but not loaded, red means not installed/found.");
        ImGui.TextWrapped("Dad does not send a FrenRider disable command on completion or cancel because it cannot know whether the user had FrenRider enabled before this run.");
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
}
