using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using dad.Models;
using dad.Services;

namespace dad.Windows;

public sealed class SetupWizardWindow : Window, IDisposable
{
    private static readonly Vector2 MinimumWindowSize = new(680f, 560f);
    private static readonly string[] CoordinatorCheckLabels =
    [
        "Dad enabled",
        "Active profile armed",
        "Client account id present",
        "Role is Server Dad",
        "Listener endpoint configured",
        "LAN secret required/configured",
        "Hub ready",
        "Participant count",
    ];
    private static readonly string[] ClientCheckLabels =
    [
        "Dad enabled",
        "Active profile armed",
        "Client account id present",
        "Role is Client Dad",
        "Server Dad endpoint configured",
        "Secret present when required",
        "Authority discovered",
        "Workers visible",
    ];
    private static readonly string[] FirstPresetCheckLabels =
    [
        "Preset setup location",
        "Saved preset count",
        "Selected preset",
        "Assigned slots",
        "Scheduler preview startability",
    ];
    private static readonly string[] RosterLaunchProfileCheckLabels =
    [
        "Local roster refresh status",
        "Connected-Dads roster status",
        "Live account/character counts",
        "Stale/missing blockers",
        "Launch profiles imported/enabled",
    ];

    private readonly Plugin plugin;
    private Vector2? pendingPosition;
    private bool resetPositionConditionNextDraw;
    private WizardRoute route = WizardRoute.Landing;
    private string draftServerHost = string.Empty;
    private int draftServerPort;
    private bool endpointDraftInitialized;
    private bool endpointDraftRole;
    private string draftSharedSecret = string.Empty;
    private bool sharedSecretDraftInitialized;
    private IReadOnlyList<DadEndpointHostOption> endpointHostOptions = [];
    private DateTime endpointHostOptionsLoadedUtc = DateTime.MinValue;

    private sealed record WizardCheck(
        string Section,
        string Label,
        bool Complete,
        string Detail,
        string NextAction,
        bool CountsForReady = true);

    private enum WizardRoute
    {
        Landing,
        Coordinator,
        Client,
        FirstPreset,
        RosterLaunchProfiles,
    }

    private sealed record WizardEntryCard(
        WizardRoute Route,
        string Title,
        string Badge,
        Vector4 BadgeColor,
        string NextAction,
        string Detail);

    public SetupWizardWindow(Plugin plugin) : base($"{PluginInfo.DisplayName} Setup Wizard##SetupWizard", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumWindowSize,
            MaximumSize = new Vector2(1500f, 1400f),
        };
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public void OpenLanding()
    {
        route = WizardRoute.Landing;
        IsOpen = true;
    }

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
        EnsureEndpointDraft(configuration);
        EnsureSharedSecretDraft(configuration);

        var profile = plugin.ConfigManager.GetActiveConfig();
        var runState = plugin.GetVisibleRunState();
        var pool = plugin.CharacterIntelligenceService.CurrentPool;
        var transport = plugin.TransportService.CurrentTransport;
        var catalog = plugin.RosterCatalogService.CurrentCatalog;
        var plannerSnapshot = plugin.GetPlannerUiSnapshot(runState);
        var selectedGroup = plugin.GetSelectedPlannerGroup();
        var checks = BuildChecks(configuration, profile, pool, transport, catalog, plannerSnapshot, selectedGroup);

        switch (route)
        {
            case WizardRoute.Coordinator:
                DrawCoordinatorPage(configuration, profile, transport, checks);
                break;
            case WizardRoute.Client:
                DrawClientPage(configuration, profile, transport, checks);
                break;
            case WizardRoute.FirstPreset:
                DrawFirstPresetPage(checks);
                break;
            case WizardRoute.RosterLaunchProfiles:
                DrawRosterLaunchProfilePage(checks);
                break;
            default:
                route = WizardRoute.Landing;
                DrawLanding(checks);
                break;
        }
    }

    private List<WizardCheck> BuildChecks(
        Configuration configuration,
        CharacterConfig profile,
        DadCharacterPool pool,
        DadPeerTransportSnapshot transport,
        DadAccountRosterCatalog catalog,
        DadPlannerUiSnapshot plannerSnapshot,
        DadPlannerGroup? selectedGroup)
    {
        var activeRoster = catalog.Characters
            .Where(static character => character.Visibility == DadRosterVisibility.Active)
            .ToList();
        var staleOrMissing = activeRoster.Count(static character =>
            character.AccountKey.IsEmpty || character.IsStale || character.NeedsRosterUpdate);
        var primarySlots = selectedGroup == null
            ? 0
            : selectedGroup.Slots.Count(static slot => !slot.IsSubstitute);
        var assignedSlots = selectedGroup == null
            ? 0
            : selectedGroup.Slots.Count(static slot =>
                !slot.IsSubstitute &&
                (!slot.RequiredAccountKey.IsEmpty || !slot.RequiredCharacterKey.IsEmpty));
        var enabledLaunchProfiles = configuration.LaunchProfiles.Count(static profile => profile.Enabled);
        var schedulerPreview = plannerSnapshot.SchedulerPreview;
        var roleCountsCoordinator = configuration.RunAsServerDad;
        var roleCountsClient = !configuration.RunAsServerDad;
        var hasLocalSnapshot = pool.Characters.Count > 0 ||
                               pool.XadbStatus.IsReady ||
                               pool.XadbStatus.SnapshotUtc.HasValue;
        var hasLocalRoster = catalog.Characters.Count > 0 ||
                             catalog.SourceDiagnostics.FinalLocalRows > 0 ||
                             catalog.SourceDiagnostics.LocalRuntimeRows > 0 ||
                             catalog.SourceDiagnostics.LocalXadbAttributedRows > 0;
        var connectedRosterRows = catalog.SourceDiagnostics.PeerFullRosterRows +
                                  catalog.SourceDiagnostics.PeerCatalogCount +
                                  transport.KnownParticipantCount;
        var participantCount = Math.Max(
            transport.PublishedParticipantCount,
            Math.Max(transport.KnownParticipantCount, transport.KnownParticipants.Count));
        var workersVisible = transport.KnownParticipants.Count > 0 || transport.KnownParticipantCount > 0;
        var authorityDiscovered = plugin.HasServerDadAuthority() ||
                                  !transport.AuthorityWorkerSessionId.IsEmpty ||
                                  !string.IsNullOrWhiteSpace(transport.AuthorityEndpoint);

        return
        [
            new("Basics", "Dad enabled", configuration.PluginEnabled, configuration.PluginEnabled ? "Dad is enabled." : "Dad is disabled.", "Enable Dad."),
            new("Basics", "Active profile armed", profile.Enabled, profile.Enabled ? "Current profile is armed." : "Current profile is not armed.", "Arm the active profile."),
            new("Basics", "Client account id present", !string.IsNullOrWhiteSpace(configuration.ClientAccountId), FormatText(configuration.ClientAccountId, "(missing)"), "Open Settings and select or create a Dad account."),
            new("Basics", "Local runtime/XADB snapshot available", hasLocalSnapshot, $"{pool.Characters.Count.ToString(CultureInfo.InvariantCulture)} live row(s); XADB {pool.XadbStatus.Availability}; snapshot {FormatTime(pool.XadbStatus.SnapshotUtc)}", "Refresh local roster or save the current character to XADB."),

            new("Coordinator Dad", "Role is Server Dad", configuration.RunAsServerDad, configuration.RunAsServerDad ? "This instance is the Dad Coordinator." : "This instance is a Client Dad.", "Switch this instance to Dad Coordinator.", roleCountsCoordinator),
            new("Coordinator Dad", "Listener endpoint configured", HasValidEndpoint(configuration.ServerListenHost, configuration.ServerListenPort), $"{configuration.ServerListenHost}:{configuration.ServerListenPort.ToString(CultureInfo.InvariantCulture)}", "Configure and apply the Dad Coordinator listener endpoint.", roleCountsCoordinator),
            new("Coordinator Dad", "LAN secret required/configured", !transport.SharedSecretRequired || transport.SharedSecretConfigured, transport.SharedSecretRequired ? (transport.SharedSecretConfigured ? "Required and configured." : "Required but missing.") : "Not required for loopback endpoint.", "Generate or apply a LAN shared secret.", roleCountsCoordinator),
            new("Coordinator Dad", "Hub ready", plugin.TransportService.IsReady && !string.IsNullOrWhiteSpace(transport.ListenerEndpoint), $"{transport.Availability}; {transport.ConnectionStatus}", "Enable Dad and verify the listener endpoint.", roleCountsCoordinator),
            new("Coordinator Dad", "Participant count", participantCount > 0, $"{participantCount.ToString(CultureInfo.InvariantCulture)} participant(s) visible; {transport.ConnectedPeerCount.ToString(CultureInfo.InvariantCulture)} peer connection(s)", "Populate connected roster or connect Client Dads.", roleCountsCoordinator),

            new("Client Dads", "Role is Client Dad", !configuration.RunAsServerDad, configuration.RunAsServerDad ? "This instance is the Dad Coordinator." : "This instance is a Client Dad.", "Switch this instance to Client Dad.", roleCountsClient),
            new("Client Dads", "Server Dad endpoint configured", HasValidEndpoint(configuration.ServerDadHost, configuration.ServerDadPort), $"{configuration.ServerDadHost}:{configuration.ServerDadPort.ToString(CultureInfo.InvariantCulture)}", "Configure and apply the Server Dad endpoint.", roleCountsClient),
            new("Client Dads", "Secret present when required", !transport.SharedSecretRequired || transport.SharedSecretConfigured, transport.SharedSecretRequired ? (transport.SharedSecretConfigured ? "Required and configured." : "Required but missing.") : "Not required for loopback endpoint.", "Paste and apply W's LAN shared secret.", roleCountsClient),
            new("Client Dads", "Authority discovered", authorityDiscovered, $"{transport.AuthorityStatus}; endpoint {FormatText(transport.AuthorityEndpoint, "(none)")}", "Verify the Server Dad endpoint and shared secret.", roleCountsClient),
            new("Client Dads", "Workers visible", workersVisible, $"{transport.KnownParticipantCount.ToString(CultureInfo.InvariantCulture)} known participant(s); {transport.LastRequestStatus}", "Populate connected roster or wait for Dad Coordinator discovery.", roleCountsClient),

            new("Roster", "Local roster refresh status", hasLocalRoster, FormatText(catalog.Summary, "Roster catalog not refreshed."), "Refresh local roster."),
            new("Roster", "Connected-Dads roster status", connectedRosterRows > 0, $"{catalog.SourceDiagnostics.PeerCatalogCount.ToString(CultureInfo.InvariantCulture)} peer catalog(s), {catalog.SourceDiagnostics.PeerFullRosterRows.ToString(CultureInfo.InvariantCulture)} peer row(s), {transport.KnownParticipantCount.ToString(CultureInfo.InvariantCulture)} transport participant(s)", "Populate connected roster."),
            new("Roster", "Live account/character counts", catalog.Accounts.Count > 0 && catalog.Characters.Count > 0, $"{catalog.Accounts.Count.ToString(CultureInfo.InvariantCulture)} account(s), {catalog.Characters.Count.ToString(CultureInfo.InvariantCulture)} character row(s)", "Refresh local roster and verify account ownership."),
            new("Roster", "Stale/missing blockers", activeRoster.Count > 0 && staleOrMissing == 0, staleOrMissing == 0 ? "No stale or missing Active roster blockers." : $"{staleOrMissing.ToString(CultureInfo.InvariantCulture)} Active row(s) are stale, missing account ownership, or need refresh.", "Refresh affected roster rows or fix account assignment."),

            new("Presets", "Preset setup location", true, "Preset setup is consolidated under Planner; there is no separate presets tab.", "Open Planner.", false),
            new("Presets", "Saved preset count", configuration.PlannerGroups.Count > 0, $"{configuration.PlannerGroups.Count.ToString(CultureInfo.InvariantCulture)} saved preset(s).", "Open Planner and create or import a saved preset."),
            new("Presets", "Selected preset", selectedGroup != null, selectedGroup == null ? "No saved preset selected." : selectedGroup.DisplayName, "Open Planner and select a saved preset."),
            new("Presets", "Assigned slots", selectedGroup != null && primarySlots > 0 && assignedSlots > 0, $"{assignedSlots.ToString(CultureInfo.InvariantCulture)}/{Math.Max(1, primarySlots).ToString(CultureInfo.InvariantCulture)} primary slot(s) assigned.", "Open Planner and assign accounts or characters to preset slots."),
            new("Presets", "Launch profiles imported/enabled", configuration.LaunchProfiles.Count > 0 && enabledLaunchProfiles > 0, $"{configuration.LaunchProfiles.Count.ToString(CultureInfo.InvariantCulture)} imported; {enabledLaunchProfiles.ToString(CultureInfo.InvariantCulture)} enabled.", "Import launch profiles and enable the needed profiles."),
            new("Presets", "Scheduler preview startability", schedulerPreview.CanStart || schedulerPreview.ReadyToStart, schedulerPreview.ReadyToStart ? "Ready to start." : FormatText(schedulerPreview.BlockedReason, schedulerPreview.StatusSummary), "Open Planner and fix the first scheduler/planner blocker."),
        ];
    }

    private void DrawLanding(IReadOnlyList<WizardCheck> checks)
    {
        ImGui.TextUnformatted("Dad Setup Wizard");
        ImGui.Separator();

        var cards = new[]
        {
            BuildEntryCard(
                WizardRoute.Coordinator,
                "Coordinator Dad",
                checks,
                CoordinatorCheckLabels),
            BuildEntryCard(
                WizardRoute.Client,
                "Client Dad",
                checks,
                ClientCheckLabels),
            BuildEntryCard(
                WizardRoute.FirstPreset,
                "Make First Preset",
                checks,
                FirstPresetCheckLabels),
            BuildEntryCard(
                WizardRoute.RosterLaunchProfiles,
                "Roster / Launch Profiles",
                checks,
                RosterLaunchProfileCheckLabels),
        };

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing;
        var useTwoColumns = availableWidth >= 620f;
        var cardWidth = useTwoColumns
            ? MathF.Max(280f, (availableWidth - spacing.X) * 0.5f)
            : availableWidth;
        var cardSize = new Vector2(cardWidth, 122f);

        for (var i = 0; i < cards.Length; i++)
        {
            if (useTwoColumns && i % 2 == 1)
                ImGui.SameLine();

            DrawEntryCard(cards[i], cardSize);
        }
    }

    private void DrawCoordinatorPage(
        Configuration configuration,
        CharacterConfig profile,
        DadPeerTransportSnapshot transport,
        IReadOnlyList<WizardCheck> checks)
    {
        if (!DrawPageHeader("Coordinator Dad"))
            return;

        DrawFocusedChecks(SelectChecks(checks, CoordinatorCheckLabels, forceCountsForReady: true));
        ImGui.Separator();
        DrawBasicsActions(profile);
        DrawCoordinatorActions(configuration);
    }

    private void DrawClientPage(
        Configuration configuration,
        CharacterConfig profile,
        DadPeerTransportSnapshot transport,
        IReadOnlyList<WizardCheck> checks)
    {
        if (!DrawPageHeader("Client Dad"))
            return;

        DrawFocusedChecks(SelectChecks(checks, ClientCheckLabels, forceCountsForReady: true));
        ImGui.Separator();
        DrawBasicsActions(profile);
        DrawClientActions(configuration);
    }

    private void DrawFirstPresetPage(IReadOnlyList<WizardCheck> checks)
    {
        if (!DrawPageHeader("Make First Preset"))
            return;

        DrawFocusedChecks(SelectChecks(checks, FirstPresetCheckLabels));
        ImGui.Separator();
        DrawPresetActions();
    }

    private void DrawRosterLaunchProfilePage(IReadOnlyList<WizardCheck> checks)
    {
        if (!DrawPageHeader("Roster / Launch Profiles"))
            return;

        DrawFocusedChecks(SelectChecks(checks, RosterLaunchProfileCheckLabels));
        ImGui.Separator();
        DrawRosterLaunchProfileActions();
    }

    private bool DrawPageHeader(string title)
    {
        if (ImGui.SmallButton("< Back"))
        {
            route = WizardRoute.Landing;
            return false;
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(title);
        ImGui.Separator();
        return true;
    }

    private void DrawEntryCard(WizardEntryCard card, Vector2 size)
    {
        if (!ImGui.BeginChild($"dad-wizard-card-{card.Route}", size, true))
        {
            ImGui.EndChild();
            return;
        }

        ImGui.TextUnformatted(card.Title);
        ImGui.TextColored(card.BadgeColor, card.Badge);
        ImGui.SameLine();
        ImGui.TextDisabled(card.Detail);
        ImGui.TextWrapped(card.NextAction);

        var buttonWidth = MathF.Max(120f, ImGui.GetContentRegionAvail().X);
        ImGui.SetCursorPosY(MathF.Max(ImGui.GetCursorPosY(), size.Y - 34f));
        if (ImGui.Button($"Open##dad-wizard-open-{card.Route}", new Vector2(buttonWidth, 26f)))
            route = card.Route;
        ImGui.EndChild();
    }

    private static WizardEntryCard BuildEntryCard(
        WizardRoute route,
        string title,
        IReadOnlyList<WizardCheck> checks,
        IReadOnlyList<string> labels)
    {
        var selected = SelectChecks(checks, labels);
        var firstBlocker = selected.FirstOrDefault(static check => !check.Complete);
        var completeCount = selected.Count(static check => check.Complete);
        var totalCount = Math.Max(1, selected.Count);
        var ready = firstBlocker == null;
        var badge = ready ? "READY" : completeCount == 0 ? "START" : "NEXT";
        var detail = $"{completeCount.ToString(CultureInfo.InvariantCulture)}/{totalCount.ToString(CultureInfo.InvariantCulture)}";
        var color = ready
            ? new Vector4(0.35f, 0.9f, 0.45f, 1f)
            : new Vector4(1f, 0.72f, 0.25f, 1f);

        return new WizardEntryCard(
            route,
            title,
            badge,
            color,
            firstBlocker?.NextAction ?? "Review this setup flow.",
            detail);
    }

    private static List<WizardCheck> SelectChecks(
        IReadOnlyList<WizardCheck> checks,
        IReadOnlyList<string> labels,
        bool forceCountsForReady = false)
    {
        var selected = new List<WizardCheck>(labels.Count);
        foreach (var label in labels)
        {
            var check = checks.FirstOrDefault(candidate =>
                string.Equals(candidate.Label, label, StringComparison.Ordinal));
            if (check != null)
                selected.Add(forceCountsForReady ? check with { CountsForReady = true } : check);
        }

        return selected;
    }

    private static void DrawFocusedChecks(IReadOnlyList<WizardCheck> checks)
    {
        foreach (var check in checks)
            DrawCheck(check);
    }

    private void DrawBasicsActions(CharacterConfig profile)
    {
        ImGui.BeginDisabled(plugin.Configuration.PluginEnabled);
        if (ImGui.SmallButton("Enable Dad"))
            plugin.SetPluginEnabled(true);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(profile.Enabled);
        if (ImGui.SmallButton("Arm profile"))
        {
            profile.Enabled = true;
            plugin.ConfigManager.SaveCurrentAccount();
            plugin.UpdateDtrBar();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.SmallButton("Settings"))
            plugin.ToggleConfigUi();

        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh local roster##basics"))
            plugin.RefreshCharacterPoolFromShell();
    }

    private void DrawCoordinatorActions(Configuration configuration)
    {
        ImGui.BeginDisabled(configuration.RunAsServerDad);
        if (ImGui.SmallButton("Use this as Coordinator Dad"))
        {
            plugin.SetRunAsServerDad(true);
            ResetEndpointDraft(configuration);
        }
        ImGui.EndDisabled();

        if (!configuration.RunAsServerDad)
            return;

        DrawEndpointEditor(configuration);
        DrawSharedSecretEditor(configuration, plugin.TransportService.CurrentTransport, serverMode: true);
    }

    private void DrawClientActions(Configuration configuration)
    {
        ImGui.BeginDisabled(!configuration.RunAsServerDad);
        if (ImGui.SmallButton("Use this as Client Dad"))
        {
            plugin.SetRunAsServerDad(false);
            ResetEndpointDraft(configuration);
        }
        ImGui.EndDisabled();

        if (configuration.RunAsServerDad)
            return;

        DrawEndpointEditor(configuration);
        DrawSharedSecretEditor(configuration, plugin.TransportService.CurrentTransport, serverMode: false);
    }

    private void DrawRosterLaunchProfileActions()
    {
        if (ImGui.SmallButton("Refresh local roster"))
            plugin.RefreshCharacterPoolFromShell();
        ImGui.SameLine();
        if (ImGui.SmallButton("Populate connected roster"))
            plugin.RequestPeerSnapshotsFromShell();
        ImGui.SameLine();
        if (ImGui.SmallButton("Import launch profiles"))
            plugin.ImportLaunchProfilesFromBootDirectory();
        ImGui.SameLine();
        if (ImGui.SmallButton("Open Crew"))
            plugin.OpenMainTab(DadMainWindowTab.Crew);
    }

    private void DrawPresetActions()
    {
        if (ImGui.SmallButton("Open Planner"))
            plugin.OpenMainTab(DadMainWindowTab.Presets, DadPresetsWindowTab.Planner);
    }

    private void DrawEndpointEditor(Configuration configuration)
    {
        EnsureEndpointDraft(configuration);
        ImGui.TextUnformatted(configuration.RunAsServerDad ? "Listener endpoint" : "Server Dad endpoint");

        var comboWidth = 220f;
        var hostInputWidth = MathF.Max(180f, ImGui.GetContentRegionAvail().X - comboWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SetNextItemWidth(hostInputWidth);
        ImGui.InputText("##dad-wizard-endpoint-host", ref draftServerHost, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(comboWidth);
        DrawEndpointHostDropdown();

        ImGui.SetNextItemWidth(140f);
        ImGui.InputInt("Port##dad-wizard-endpoint-port", ref draftServerPort);
        draftServerPort = Math.Clamp(draftServerPort, 1, 65535);

        if (ImGui.SmallButton("Apply endpoint"))
        {
            plugin.ApplyTransportEndpoint(draftServerHost, draftServerPort);
            ResetEndpointDraft(configuration);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Revert endpoint"))
            ResetEndpointDraft(configuration);
    }

    private void DrawSharedSecretEditor(
        Configuration configuration,
        DadPeerTransportSnapshot transport,
        bool serverMode)
    {
        EnsureSharedSecretDraft(configuration);
        ImGui.TextUnformatted("LAN shared secret");
        ImGui.SetNextItemWidth(MathF.Min(440f, ImGui.GetContentRegionAvail().X));
        ImGui.InputText(serverMode ? "Shared secret##dad-wizard-secret" : "Paste shared secret##dad-wizard-secret", ref draftSharedSecret, 128);

        if (ImGui.SmallButton("Apply shared secret"))
        {
            plugin.SetTransportSharedSecret(draftSharedSecret);
            ResetSharedSecretDraft(configuration);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Revert secret"))
            ResetSharedSecretDraft(configuration);

        if (serverMode)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Generate"))
            {
                draftSharedSecret = plugin.GenerateAndApplyTransportSharedSecret();
                ResetSharedSecretDraft(configuration);
            }
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(configuration.TransportSharedSecret));
        if (ImGui.SmallButton("Copy"))
        {
            ImGui.SetClipboardText(configuration.TransportSharedSecret);
            plugin.PrintStatus("Copied LAN shared secret.");
        }
        ImGui.EndDisabled();

        DrawStatusRow("Secret status", transport.SharedSecretRequired
            ? transport.SharedSecretConfigured ? "Required and configured." : "Required but missing."
            : "Not required for loopback endpoint.");
    }

    private void DrawEndpointHostDropdown()
    {
        var options = GetEndpointHostOptions();
        var current = options.FirstOrDefault(option =>
            string.Equals(option.Host, draftServerHost.Trim(), StringComparison.OrdinalIgnoreCase));
        var preview = current?.Label ?? "Select IP/host";
        if (!ImGui.BeginCombo("##dad-wizard-endpoint-host-options", preview))
            return;

        foreach (var option in options)
        {
            var selected = string.Equals(option.Host, draftServerHost.Trim(), StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable($"{option.Label}##dad-wizard-{option.Host}", selected))
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

    private void EnsureEndpointDraft(Configuration configuration)
    {
        if (endpointDraftInitialized && endpointDraftRole == configuration.RunAsServerDad)
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
        endpointDraftRole = configuration.RunAsServerDad;
        endpointDraftInitialized = true;
    }

    private void EnsureSharedSecretDraft(Configuration configuration)
    {
        if (sharedSecretDraftInitialized &&
            string.Equals(draftSharedSecret, configuration.TransportSharedSecret, StringComparison.Ordinal))
        {
            return;
        }

        if (!sharedSecretDraftInitialized ||
            string.Equals(draftSharedSecret.Trim(), configuration.TransportSharedSecret, StringComparison.Ordinal))
        {
            ResetSharedSecretDraft(configuration);
        }
    }

    private void ResetSharedSecretDraft(Configuration configuration)
    {
        draftSharedSecret = configuration.TransportSharedSecret;
        sharedSecretDraftInitialized = true;
    }

    private static void DrawCheck(WizardCheck check)
    {
        var status = !check.CountsForReady ? "INFO" : check.Complete ? "OK" : "TODO";
        var color = !check.CountsForReady
            ? new Vector4(0.55f, 0.65f, 0.85f, 1f)
            : check.Complete
                ? new Vector4(0.35f, 0.9f, 0.45f, 1f)
                : new Vector4(1f, 0.72f, 0.25f, 1f);
        ImGui.TextColored(color, status);
        ImGui.SameLine(58f);
        ImGui.TextUnformatted(check.Label);
        ImGui.SameLine(240f);
        ImGui.TextWrapped(check.Detail);
        if (check.CountsForReady && !check.Complete && ImGui.IsItemHovered())
            ImGui.SetTooltip(check.NextAction);
    }

    private static void DrawStatusRow(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine(180f);
        ImGui.TextWrapped(value);
    }

    private static bool HasValidEndpoint(string host, int port)
        => !string.IsNullOrWhiteSpace(host) && port is > 0 and <= 65535;

    private static string FormatText(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string FormatTime(DateTime? value)
        => value?.ToString("u", CultureInfo.InvariantCulture) ?? "(never)";
}
