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
    private static readonly string[] SchedulerBuilderCheckLabels =
    [
        "Dad enabled",
        "Server Dad for daily schedules",
        "Saved Planner presets exist",
        "At least one schedule exists",
        "Selected schedule has entries",
        "Entries reference saved presets",
        "Daily reset mode",
        "Dry-run status",
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
    private string schedulerBuilderScheduleId = string.Empty;
    private string schedulerBuilderScheduleNameBuffer = string.Empty;
    private string schedulerBuilderAddPresetGroupId = string.Empty;
    private int schedulerBuilderRepeatCount = DadScheduleRules.MinRepeatCount;

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
        SchedulerBuilder,
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
        var scheduleSnapshot = plugin.SchedulerService.GetScheduleSnapshot();
        EnsureSchedulerBuilderSelection(scheduleSnapshot);
        var selectedSchedule = FindSelectedSchedulerBuilderSchedule(scheduleSnapshot);
        var checks = BuildChecks(configuration, profile, pool, transport, catalog, plannerSnapshot, selectedGroup, scheduleSnapshot, selectedSchedule);

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
            case WizardRoute.SchedulerBuilder:
                DrawSchedulerBuilderPage(checks, scheduleSnapshot);
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
        DadPlannerGroup? selectedGroup,
        DadScheduleSnapshot scheduleSnapshot,
        DadScheduleDefinition? selectedSchedule)
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
        var savedPresetCount = configuration.PlannerGroups.Count;
        var selectedScheduleEntries = selectedSchedule?.Entries ?? [];
        var knownPresetIds = configuration.PlannerGroups
            .Where(static group => !string.IsNullOrWhiteSpace(group.GroupId))
            .Select(static group => group.GroupId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingScheduleEntryCount = selectedScheduleEntries.Count(entry =>
            string.IsNullOrWhiteSpace(entry.GroupId) || !knownPresetIds.Contains(entry.GroupId));

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

            new("Scheduler Builder", "Server Dad for daily schedules", configuration.RunAsServerDad, configuration.RunAsServerDad ? "This instance is Server Dad." : "This instance is Client Dad; live daily schedule execution requires Server Dad.", "Switch this instance to Server Dad before relying on live daily schedules."),
            new("Scheduler Builder", "Saved Planner presets exist", savedPresetCount > 0, $"{savedPresetCount.ToString(CultureInfo.InvariantCulture)} saved preset(s).", "Open Planner and save at least one preset."),
            new("Scheduler Builder", "At least one schedule exists", scheduleSnapshot.Schedules.Count > 0, $"{scheduleSnapshot.Schedules.Count.ToString(CultureInfo.InvariantCulture)} schedule(s) configured.", "Create a daily schedule from this wizard."),
            new("Scheduler Builder", "Selected schedule has entries", selectedScheduleEntries.Count > 0, selectedSchedule == null ? "No schedule selected." : $"{selectedScheduleEntries.Count.ToString(CultureInfo.InvariantCulture)} entry/entries in '{selectedSchedule.DisplayName}'.", "Add at least one saved preset to the selected schedule."),
            new("Scheduler Builder", "Entries reference saved presets", selectedSchedule != null && selectedScheduleEntries.Count > 0 && missingScheduleEntryCount == 0, selectedSchedule == null ? "No schedule selected." : selectedScheduleEntries.Count == 0 ? "No entries to validate yet." : missingScheduleEntryCount == 0 ? "All entries reference saved presets." : $"{missingScheduleEntryCount.ToString(CultureInfo.InvariantCulture)} entry/entries reference missing presets.", "Remove missing entries or re-add saved presets."),
            new("Scheduler Builder", "Daily reset mode", selectedSchedule?.Cadence == DadScheduleCadence.DailyReset, selectedSchedule == null ? "No schedule selected." : selectedSchedule.Cadence == DadScheduleCadence.DailyReset ? $"Daily reset at {DadScheduleRules.DailyResetHourUtc.ToString("00", CultureInfo.InvariantCulture)}:00 UTC." : "Selected schedule is manual-only.", "Create a daily schedule here or switch cadence in Presets -> Scheduler."),
            new("Scheduler Builder", "Dry-run status", false, BuildSchedulerDryRunDetail(scheduleSnapshot, selectedSchedule), "Run Dry-run to validate before relying on daily mode.", false),
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
            BuildEntryCard(
                WizardRoute.SchedulerBuilder,
                "Build Scheduler",
                checks,
                SchedulerBuilderCheckLabels),
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

    private void DrawSchedulerBuilderPage(IReadOnlyList<WizardCheck> checks, DadScheduleSnapshot snapshot)
    {
        if (!DrawPageHeader("Build Scheduler"))
            return;

        DrawFocusedChecks(SelectChecks(checks, SchedulerBuilderCheckLabels));
        ImGui.Separator();

        var groups = GetSchedulerBuilderPlannerGroups();
        EnsureSchedulerBuilderSelection(snapshot);
        var schedule = FindSelectedSchedulerBuilderSchedule(snapshot);
        var activeRun = snapshot.ActiveRun;
        var activeScheduleLocked = activeRun.IsActive;

        if (!plugin.Configuration.RunAsServerDad)
            DrawMutedNotice("Client Dad can inspect and build schedules here; live daily schedule execution requires Server Dad.");

        DrawStatusRow("Runner", activeRun.IsActive
            ? $"{activeRun.Status} / {activeRun.Phase} | {activeRun.Summary}"
            : FormatText(activeRun.Summary, snapshot.Summary));
        if (activeRun.IsActive)
        {
            DrawStatusRow("Active schedule", FormatText(activeRun.ScheduleName, activeRun.ScheduleId));
            DrawStatusRow("Progress", $"{activeRun.CompletedEntryExecutions.ToString(CultureInfo.InvariantCulture)}/{activeRun.TotalEntryExecutions.ToString(CultureInfo.InvariantCulture)} preset run(s)");
            DrawStatusRow("Entry", $"{(activeRun.CurrentEntryIndex + 1).ToString(CultureInfo.InvariantCulture)} / repeat {activeRun.RepeatIteration.ToString(CultureInfo.InvariantCulture)} / {FormatText(activeRun.CurrentPresetName, activeRun.CurrentGroupId)}");
            if (!string.IsNullOrWhiteSpace(activeRun.BlockedReason))
                DrawStatusRow("Blocker", activeRun.BlockedReason);
        }

        DrawSchedulerBuilderScheduleControls(snapshot, schedule, activeScheduleLocked);

        if (schedule == null)
        {
            DrawMutedNotice("Create a daily schedule, then add saved Planner presets to it.");
            return;
        }

        DrawStatusRow("Cadence", schedule.Cadence == DadScheduleCadence.DailyReset
            ? $"Daily reset at {DadScheduleRules.DailyResetHourUtc.ToString("00", CultureInfo.InvariantCulture)}:00 UTC; next reset {FormatTime(DadScheduleRules.GetNextDailyResetUtc(DateTime.UtcNow))}"
            : "Manual only.");
        DrawStatusRow("Last run", schedule.LastRunCompletedAtUtc.HasValue
            ? $"{schedule.LastRunStatus} at {FormatTime(schedule.LastRunCompletedAtUtc)} | {FormatText(schedule.LastSummary, "(no summary)")}"
            : "(never)");

        DrawSchedulerBuilderAddPreset(schedule, groups, activeScheduleLocked);
        DrawSchedulerBuilderEntries(schedule, groups, activeScheduleLocked);

        ImGui.Separator();
        var canDryRun = schedule.Entries.Count > 0 && !activeScheduleLocked;
        ImGui.BeginDisabled(!canDryRun);
        if (ImGui.SmallButton("Dry-run"))
            plugin.StartScheduleRunFromShell(schedule.ScheduleId, dryRun: true, requestedBy: "wizard-scheduler");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canDryRun)
            ImGui.SetTooltip(activeScheduleLocked
                ? "A schedule is already running."
                : "Add at least one saved preset entry.");
        ImGui.EndDisabled();
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
        var readyChecks = selected
            .Where(static check => check.CountsForReady)
            .ToList();
        var firstBlocker = readyChecks.FirstOrDefault(static check => !check.Complete);
        var completeCount = readyChecks.Count(static check => check.Complete);
        var totalCount = readyChecks.Count;
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

    private void DrawSchedulerBuilderScheduleControls(
        DadScheduleSnapshot snapshot,
        DadScheduleDefinition? schedule,
        bool activeScheduleLocked)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Schedule");

        if (snapshot.Schedules.Count == 0)
        {
            DrawMutedNotice("No schedules configured.");
        }
        else
        {
            ImGui.SetNextItemWidth(MathF.Min(360f, ImGui.GetContentRegionAvail().X));
            if (ImGui.BeginCombo("Schedule##dad-wizard-scheduler-select", schedule == null ? "(none)" : schedule.DisplayName))
            {
                foreach (var candidate in snapshot.Schedules)
                {
                    var selected = string.Equals(candidate.ScheduleId, schedulerBuilderScheduleId, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable($"{candidate.DisplayName}##dad-wizard-schedule-{candidate.ScheduleId}", selected))
                    {
                        schedulerBuilderScheduleId = candidate.ScheduleId;
                        schedulerBuilderScheduleNameBuffer = candidate.DisplayName;
                    }
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }
        }

        ImGui.SetNextItemWidth(MathF.Min(360f, ImGui.GetContentRegionAvail().X));
        ImGui.InputText("Schedule name", ref schedulerBuilderScheduleNameBuffer, 128);

        if (ImGui.SmallButton("Create daily schedule"))
        {
            var created = plugin.SchedulerService.CreateSchedule(schedulerBuilderScheduleNameBuffer);
            created.Cadence = DadScheduleCadence.DailyReset;
            var updated = plugin.SchedulerService.UpdateSchedule(created) ?? created;
            schedulerBuilderScheduleId = updated.ScheduleId;
            schedulerBuilderScheduleNameBuffer = updated.DisplayName;
            plugin.PrintStatus($"Created daily schedule '{updated.DisplayName}' for {DadScheduleRules.DailyResetHourUtc.ToString("00", CultureInfo.InvariantCulture)}:00 UTC reset.");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Open Scheduler"))
            plugin.OpenMainTab(DadMainWindowTab.Presets, DadPresetsWindowTab.Scheduler);

        if (schedule == null)
            return;

        ImGui.SameLine();
        ImGui.BeginDisabled(activeScheduleLocked || schedule.Cadence == DadScheduleCadence.DailyReset);
        if (ImGui.SmallButton("Use daily reset"))
        {
            schedule.Cadence = DadScheduleCadence.DailyReset;
            if (plugin.SchedulerService.UpdateSchedule(schedule) is { } updated)
                plugin.PrintStatus($"Set schedule '{updated.DisplayName}' to daily reset mode.");
        }
        ImGui.EndDisabled();
    }

    private void DrawSchedulerBuilderAddPreset(
        DadScheduleDefinition schedule,
        IReadOnlyList<DadPlannerGroup> groups,
        bool activeScheduleLocked)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Add preset");
        if (groups.Count == 0)
        {
            DrawMutedNotice("No saved Planner presets are available.");
            return;
        }

        EnsureSchedulerBuilderPresetSelection(groups);

        ImGui.BeginDisabled(activeScheduleLocked);
        DrawSchedulerBuilderPresetCombo("Preset", ref schedulerBuilderAddPresetGroupId, groups, "add");

        schedulerBuilderRepeatCount = Math.Clamp(
            schedulerBuilderRepeatCount,
            DadScheduleRules.MinRepeatCount,
            DadScheduleRules.MaxRepeatCount);
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Repeat", ref schedulerBuilderRepeatCount))
        {
            schedulerBuilderRepeatCount = Math.Clamp(
                schedulerBuilderRepeatCount,
                DadScheduleRules.MinRepeatCount,
                DadScheduleRules.MaxRepeatCount);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Add preset"))
        {
            var group = groups.FirstOrDefault(candidate =>
                string.Equals(candidate.GroupId, schedulerBuilderAddPresetGroupId, StringComparison.OrdinalIgnoreCase));
            if (group != null)
            {
                schedule.Entries.Add(new DadScheduleEntry
                {
                    GroupId = group.GroupId,
                    PresetName = group.DisplayName,
                    RepeatCount = schedulerBuilderRepeatCount,
                });
                if (plugin.SchedulerService.UpdateSchedule(schedule) is { } updated)
                    plugin.PrintStatus($"Added '{group.DisplayName}' to schedule '{updated.DisplayName}'.");
            }
        }
        ImGui.EndDisabled();
    }

    private void DrawSchedulerBuilderEntries(
        DadScheduleDefinition schedule,
        IReadOnlyList<DadPlannerGroup> groups,
        bool activeScheduleLocked)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Entries");
        if (schedule.Entries.Count == 0)
        {
            DrawMutedNotice("No presets in this schedule.");
            return;
        }

        if (!ImGui.BeginTable("dad-wizard-scheduler-entries", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("#");
        ImGui.TableSetupColumn("Preset");
        ImGui.TableSetupColumn("Repeat");
        ImGui.TableSetupColumn("Status");
        ImGui.TableSetupColumn("Move");
        ImGui.TableSetupColumn("Remove");
        ImGui.TableHeadersRow();

        var duplicateNames = groups
            .GroupBy(static group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < schedule.Entries.Count; index++)
        {
            var entry = schedule.Entries[index];
            var group = groups.FirstOrDefault(candidate =>
                string.Equals(candidate.GroupId, entry.GroupId, StringComparison.OrdinalIgnoreCase));
            var missingPreset = group == null;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((index + 1).ToString(CultureInfo.InvariantCulture));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(group == null
                ? FormatText(entry.PresetName, FormatText(entry.GroupId, "(missing preset)"))
                : FormatPlannerGroupChoice(group.DisplayName, group.GroupId, duplicateNames));

            ImGui.TableNextColumn();
            var repeat = entry.RepeatCount;
            ImGui.BeginDisabled(activeScheduleLocked);
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputInt($"##dad-wizard-entry-repeat-{entry.EntryId}", ref repeat))
            {
                entry.RepeatCount = Math.Clamp(repeat, DadScheduleRules.MinRepeatCount, DadScheduleRules.MaxRepeatCount);
                entry.UpdatedAtUtc = DateTime.UtcNow;
                plugin.SchedulerService.UpdateSchedule(schedule);
            }
            ImGui.EndDisabled();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(missingPreset ? "Missing preset" : "Saved preset found");

            ImGui.TableNextColumn();
            ImGui.BeginDisabled(activeScheduleLocked || index == 0);
            var moveUp = ImGui.SmallButton($"Up##dad-wizard-entry-up-{entry.EntryId}");
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(activeScheduleLocked || index >= schedule.Entries.Count - 1);
            var moveDown = ImGui.SmallButton($"Down##dad-wizard-entry-down-{entry.EntryId}");
            ImGui.EndDisabled();

            ImGui.TableNextColumn();
            ImGui.BeginDisabled(activeScheduleLocked);
            var remove = ImGui.SmallButton($"Remove##dad-wizard-entry-remove-{entry.EntryId}");
            ImGui.EndDisabled();

            if (moveUp)
            {
                (schedule.Entries[index - 1], schedule.Entries[index]) = (schedule.Entries[index], schedule.Entries[index - 1]);
                plugin.SchedulerService.UpdateSchedule(schedule);
                break;
            }

            if (moveDown)
            {
                (schedule.Entries[index + 1], schedule.Entries[index]) = (schedule.Entries[index], schedule.Entries[index + 1]);
                plugin.SchedulerService.UpdateSchedule(schedule);
                break;
            }

            if (remove)
            {
                schedule.Entries.RemoveAt(index);
                plugin.SchedulerService.UpdateSchedule(schedule);
                break;
            }
        }

        ImGui.EndTable();
    }

    private bool DrawSchedulerBuilderPresetCombo(
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
        if (!ImGui.BeginCombo($"{label}##dad-wizard-scheduler-preset-{idSuffix}", preview))
            return false;

        foreach (var group in groups)
        {
            var selected = string.Equals(group.GroupId, currentGroupId, StringComparison.OrdinalIgnoreCase);
            var choiceLabel = FormatPlannerGroupChoice(group.DisplayName, group.GroupId, duplicateNames);
            if (ImGui.Selectable($"{choiceLabel}##dad-wizard-scheduler-preset-{idSuffix}-{group.GroupId}", selected))
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

    private List<DadPlannerGroup> GetSchedulerBuilderPlannerGroups()
        => plugin.Configuration.PlannerGroups
            .OrderBy(static group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static group => group.GroupId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void EnsureSchedulerBuilderSelection(DadScheduleSnapshot snapshot)
    {
        var selected = FindSelectedSchedulerBuilderSchedule(snapshot);
        if (selected != null)
        {
            if (string.IsNullOrWhiteSpace(schedulerBuilderScheduleNameBuffer))
                schedulerBuilderScheduleNameBuffer = selected.DisplayName;
            return;
        }

        selected = snapshot.Schedules.FirstOrDefault();
        schedulerBuilderScheduleId = selected?.ScheduleId ?? string.Empty;
        if (selected != null)
        {
            schedulerBuilderScheduleNameBuffer = selected.DisplayName;
        }
        else if (string.IsNullOrWhiteSpace(schedulerBuilderScheduleNameBuffer))
        {
            schedulerBuilderScheduleNameBuffer = "Dad Daily Schedule";
        }
    }

    private DadScheduleDefinition? FindSelectedSchedulerBuilderSchedule(DadScheduleSnapshot snapshot)
        => snapshot.Schedules.FirstOrDefault(schedule =>
            string.Equals(schedule.ScheduleId, schedulerBuilderScheduleId, StringComparison.OrdinalIgnoreCase));

    private void EnsureSchedulerBuilderPresetSelection(IReadOnlyList<DadPlannerGroup> groups)
    {
        schedulerBuilderRepeatCount = Math.Clamp(
            schedulerBuilderRepeatCount,
            DadScheduleRules.MinRepeatCount,
            DadScheduleRules.MaxRepeatCount);

        if (groups.Count == 0)
        {
            schedulerBuilderAddPresetGroupId = string.Empty;
            return;
        }

        if (string.IsNullOrWhiteSpace(schedulerBuilderAddPresetGroupId) ||
            groups.All(group => !string.Equals(group.GroupId, schedulerBuilderAddPresetGroupId, StringComparison.OrdinalIgnoreCase)))
        {
            schedulerBuilderAddPresetGroupId = groups[0].GroupId;
        }
    }

    private static string BuildSchedulerDryRunDetail(DadScheduleSnapshot snapshot, DadScheduleDefinition? selectedSchedule)
    {
        if (selectedSchedule == null)
            return "No schedule selected.";

        var activeRun = snapshot.ActiveRun;
        if (activeRun.IsActive &&
            activeRun.DryRun &&
            string.Equals(activeRun.ScheduleId, selectedSchedule.ScheduleId, StringComparison.OrdinalIgnoreCase))
        {
            return $"Dry-run active: {activeRun.Summary}";
        }

        var lastDryRun = snapshot.RecentResults.FirstOrDefault(result =>
            result.DryRun &&
            string.Equals(result.ScheduleId, selectedSchedule.ScheduleId, StringComparison.OrdinalIgnoreCase));
        if (lastDryRun == null)
            return "No dry-run recorded for selected schedule.";

        var outcome = lastDryRun.Success ? "ready" : "blocked";
        return $"Last dry-run {outcome} at {FormatTime(lastDryRun.CompletedAtUtc)}: {FormatText(lastDryRun.BlockedReason, lastDryRun.Summary)}";
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

    private static void DrawMutedNotice(string text)
        => ImGui.TextDisabled(text);

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
