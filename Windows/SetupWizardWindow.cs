using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using dad.Models;
using dad.Services;

namespace dad.Windows;

/// <summary>
/// Task-oriented DAD guide. Each workflow edits a private draft, validates the
/// current step, and commits through the existing plugin/service setters on Next.
/// Completed steps stay saved when moving Back; reopening starts from persisted truth.
/// </summary>
public sealed class SetupWizardWindow : Window, IDisposable
{
    private static readonly Vector2 MinimumWindowSize = new(760f, 600f);
    private readonly Plugin plugin;
    private readonly DadConnectionEditor connectionEditor;
    private readonly DadPresetCrewEditor presetCrewEditor;
    private Vector2? pendingPosition;
    private bool resetPositionConditionNextDraw;

    private DadGuideFlow flow = DadGuideFlow.Landing;
    private int stepIndex;
    private int furthestStep;
    private string currentStepId = string.Empty;
    private string validationMessage = string.Empty;
    private string roleRestrictionMessage = string.Empty;

    private bool basicsDraftInitialized;
    private bool draftPluginEnabled;
    private bool draftProfileEnabled;
    private string draftAccountId = string.Empty;

    private DadPresetPlannerOptions presetPlannerDraft = new();
    private string presetDutySearch = string.Empty;
    private string presetName = string.Empty;
    private bool presetCreateNew;
    private DadPlannerGroup? presetCrewDraft;
    private string presetDraftGroupId = string.Empty;
    private DadRunStopPolicy presetStopDraft = new();
    private DadCompletionActions presetCompletionDraft = new();
    private bool presetUseGlobalCompletionDefaults = true;
    private string presetCompletionCommands = string.Empty;

    private readonly HashSet<string> crewOwnershipAssignments = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> crewStagedSkips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DadLaunchProfile> launchProfileDrafts = new(StringComparer.OrdinalIgnoreCase);

    private string scheduleId = string.Empty;
    private string scheduleName = "Dad Schedule";
    private bool scheduleCreateNew;
    private DadScheduleDefinition? scheduleDraft;
    private string scheduleAddGroupId = string.Empty;
    private int scheduleRepeatCount = DadScheduleRules.MinRepeatCount;
    private DadScheduleCadence scheduleCadenceDraft = DadScheduleCadence.Manual;
    private string scheduleStarterStatus = string.Empty;

    private sealed record GuideStep(
        string Title,
        string Controls,
        string Why,
        string Success,
        bool Ready,
        string Blocker,
        string Id = "");

    public SetupWizardWindow(Plugin plugin)
        : base($"{PluginInfo.DisplayName} Guide###SetupWizard", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        connectionEditor = new DadConnectionEditor(plugin);
        presetCrewEditor = new DadPresetCrewEditor(plugin);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumWindowSize,
            MaximumSize = new Vector2(1500f, 1400f),
        };
        Size = new Vector2(920f, 760f);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public void OpenLanding()
    {
        flow = DadGuideFlow.Landing;
        stepIndex = 0;
        furthestStep = 0;
        currentStepId = string.Empty;
        validationMessage = string.Empty;
        roleRestrictionMessage = string.Empty;
        IsOpen = true;
    }

    public void OpenFlow(DadGuideFlow requestedFlow)
    {
        if (requestedFlow == DadGuideFlow.Landing)
        {
            OpenLanding();
            return;
        }

        if (DadGuideReadiness.TryGetConnectionFlowRestriction(plugin, requestedFlow, out var restriction))
        {
            OpenLanding();
            roleRestrictionMessage = restriction;
            plugin.PrintStatus(restriction);
            return;
        }

        flow = requestedFlow;
        stepIndex = 0;
        furthestStep = 0;
        currentStepId = string.Empty;
        validationMessage = string.Empty;
        roleRestrictionMessage = string.Empty;
        InitializeDrafts();
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
        QueuePosition(new Vector2(
            minX + ((float)Random.Shared.NextDouble() * MathF.Max(1f, maxX - minX)),
            minY + ((float)Random.Shared.NextDouble() * MathF.Max(1f, maxY - minY))));
    }

    public void OnDebugUiChanged()
    {
        if (flow != DadGuideFlow.Crew)
            return;
        currentStepId = DadDebugUiRules.ResolveVisibleCrewStep(currentStepId, plugin.Configuration.DebugUiEnabled);
    }

    public override void Draw()
    {
        ApplyPendingPositionChange();
        if (flow == DadGuideFlow.Landing)
        {
            DrawLanding();
            return;
        }

        var steps = BuildSteps();
        if (steps.Count == 0)
        {
            OpenLanding();
            return;
        }

        SynchronizeStepSelection(steps);
        furthestStep = Math.Clamp(furthestStep, stepIndex, steps.Count - 1);

        DadUi.Heading(FlowTitle(flow), "Save each step with Next. Back keeps completed work; closing drops only this step's unsaved draft.");
        if (DadUi.Button("All guided tasks"))
            OpenLanding();
        ImGui.SameLine();
        DrawExpertButton(flow);
        ImGui.Spacing();

        if (!ImGui.BeginTable(
                "dad-guide-layout",
                2,
                ImGuiTableFlags.SizingStretchProp |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.NoSavedSettings))
        {
            return;
        }

        ImGui.TableSetupColumn("Steps", ImGuiTableColumnFlags.WidthFixed, 205f);
        ImGui.TableSetupColumn("Current step", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawStepRail(steps);
        ImGui.TableNextColumn();
        DrawStep(steps[stepIndex]);
        ImGui.EndTable();
    }

    private void DrawLanding()
    {
        DadUi.Heading("DAD GUIDE", "Choose the job you are trying to finish. Each guide edits the real DAD setup and reports live readiness.");
        ImGui.TextWrapped("DAD coordinates characters, saved presets, connected-client wake/relog, party assembly, and scheduled runs. It waits for a missing game client to be started manually.");
        if (!string.IsNullOrWhiteSpace(roleRestrictionMessage))
        {
            DadUi.Badge("Connection role is already configured", DadUiTone.Warning);
            ImGui.TextWrapped(roleRestrictionMessage);
        }
        ImGui.Spacing();

        var flows = new[]
        {
            DadGuideFlow.Coordinator,
            DadGuideFlow.Client,
            DadGuideFlow.FirstPreset,
            DadGuideFlow.Crew,
            DadGuideFlow.Schedule,
        };
        var useTwoColumns = ImGui.GetContentRegionAvail().X >= ImGui.GetFontSize() * 42f;
        if (ImGui.BeginTable("dad-guide-task-cards", useTwoColumns ? 2 : 1, ImGuiTableFlags.SizingStretchSame))
        {
            foreach (var candidate in flows)
            {
                var progress = DadGuideReadiness.Build(plugin, candidate);
                var restricted = DadGuideReadiness.TryGetConnectionFlowRestriction(plugin, candidate, out var restriction);
                ImGui.TableNextColumn();
                ImGui.BeginDisabled(restricted);
                if (DadUi.BeginCard($"dad-guide-card-{candidate}", 132f))
                {
                    DadUi.Badge(
                        progress.Ready ? "Ready" : $"{progress.Complete}/{progress.Total} ready",
                        progress.Ready ? DadUiTone.Success : DadUiTone.Warning);
                    DadUi.Heading(progress.Title, FlowSummary(candidate));
                    ImGui.TextWrapped(progress.Ready ? "Review or change this setup." : $"Next: {progress.NextAction}");
                    if (DadUi.Button($"Open guide##dad-guide-open-{candidate}", DadUiTone.Accent))
                        OpenFlow(candidate);
                    if (restricted && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(restriction);
                    DadUi.EndCard();
                }
                ImGui.EndDisabled();
            }
            ImGui.EndTable();
        }

        DadUi.Section("Expert workspaces", "Use these after the guided setup when you already know what needs changing.");
        if (DadUi.Button("Plan"))
            plugin.OpenMainTab(DadMainWindowTab.Presets, DadPresetsWindowTab.Planner);
        ImGui.SameLine();
        if (DadUi.Button("Schedules"))
            plugin.OpenMainTab(DadMainWindowTab.Presets, DadPresetsWindowTab.Scheduler);
        ImGui.SameLine();
        if (DadUi.Button("Crew"))
            plugin.OpenMainTab(DadMainWindowTab.Crew);
        ImGui.SameLine();
        if (DadUi.Button("Clients"))
            plugin.OpenMainTab(DadMainWindowTab.Multiplayer);
        ImGui.SameLine();
        if (DadUi.Button("Settings"))
            plugin.OpenConfigUi();
    }

    private void DrawStepRail(IReadOnlyList<GuideStep> steps)
    {
        ImGui.TextDisabled("WORKFLOW");
        ImGui.Separator();
        for (var index = 0; index < steps.Count; index++)
        {
            var state = index < stepIndex ? "Saved" : index == stepIndex ? "Current" : steps[index].Ready ? "Ready" : "Waiting";
            var tone = index < stepIndex || steps[index].Ready
                ? DadUiTone.Success
                : index == stepIndex
                    ? DadUiTone.Accent
                    : DadUiTone.Neutral;
            DadUi.Badge($"{index + 1}. {steps[index].Title}", tone);
            ImGui.TextDisabled(state);
            if (index <= furthestStep && index != stepIndex)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Open##dad-guide-step-{index}"))
                {
                    stepIndex = index;
                    currentStepId = steps[index].Id;
                    validationMessage = string.Empty;
                }
            }
            ImGui.Spacing();
        }
    }

    private void DrawStep(GuideStep step)
    {
        var steps = BuildSteps();
        DadUi.Heading($"{stepIndex + 1}. {step.Title}", step.Controls);
        if (DadUi.BeginCard("dad-guide-explanation", 138f))
        {
            DadUi.KeyValue("What it controls", step.Controls, 132f);
            DadUi.KeyValue("Why DAD needs it", step.Why, 132f);
            DadUi.KeyValue("Success looks like", step.Success, 132f);
            DadUi.KeyValue("Current blocker", step.Ready ? "None for this step." : step.Blocker, 132f);
            DadUi.EndCard();
        }
        ImGui.Spacing();

        DrawStepContent();
        if (!string.IsNullOrWhiteSpace(validationMessage))
        {
            ImGui.Spacing();
            DadUi.Badge("Cannot continue yet", DadUiTone.Danger);
            ImGui.TextWrapped(validationMessage);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (stepIndex > 0)
        {
            if (DadUi.Button("Back"))
            {
                stepIndex--;
                currentStepId = steps[stepIndex].Id;
                validationMessage = string.Empty;
            }
        }
        else if (DadUi.Button("Back to tasks"))
        {
            OpenLanding();
        }

        ImGui.SameLine();
        var final = stepIndex >= steps.Count - 1;
        if (DadUi.Button(final ? "Finish" : "Save and Next", DadUiTone.Accent))
            TryAdvance(steps.Count);
    }

    private void DrawStepContent()
    {
        switch (flow)
        {
            case DadGuideFlow.Coordinator:
            case DadGuideFlow.Client:
                DrawConnectionFlowStep(flow == DadGuideFlow.Coordinator);
                break;
            case DadGuideFlow.FirstPreset:
                DrawPresetStep();
                break;
            case DadGuideFlow.Crew:
                DrawCrewStep();
                break;
            case DadGuideFlow.Schedule:
                DrawScheduleStep();
                break;
        }
    }

    private IReadOnlyList<GuideStep> BuildSteps()
        => flow switch
        {
            DadGuideFlow.Coordinator => BuildConnectionSteps(coordinator: true),
            DadGuideFlow.Client => BuildConnectionSteps(coordinator: false),
            DadGuideFlow.FirstPreset => BuildPresetSteps(),
            DadGuideFlow.Crew => BuildCrewSteps(),
            DadGuideFlow.Schedule => BuildScheduleSteps(),
            _ => [],
        };

    private void SynchronizeStepSelection(IReadOnlyList<GuideStep> steps)
    {
        stepIndex = Math.Clamp(stepIndex, 0, steps.Count - 1);
        if (flow != DadGuideFlow.Crew)
            return;

        if (string.IsNullOrWhiteSpace(currentStepId))
            currentStepId = steps[stepIndex].Id;
        currentStepId = DadDebugUiRules.ResolveVisibleCrewStep(currentStepId, plugin.Configuration.DebugUiEnabled);
        var resolvedIndex = steps
            .Select((step, index) => (step, index))
            .FirstOrDefault(pair => string.Equals(pair.step.Id, currentStepId, StringComparison.Ordinal))
            .index;
        if (resolvedIndex >= 0 && resolvedIndex < steps.Count &&
            string.Equals(steps[resolvedIndex].Id, currentStepId, StringComparison.Ordinal))
        {
            stepIndex = resolvedIndex;
        }
        else
        {
            currentStepId = steps[stepIndex].Id;
        }
    }

    private IReadOnlyList<GuideStep> BuildConnectionSteps(bool coordinator)
    {
        var configuration = plugin.Configuration;
        var profile = plugin.ConfigManager.GetActiveConfig();
        var transport = plugin.TransportService.CurrentTransport;
        var roleReady = configuration.RunAsServerDad == coordinator;
        var savedEndpointReady = coordinator
            ? ValidEndpoint(configuration.ServerListenHost, configuration.ServerListenPort)
            : ValidEndpoint(configuration.ServerDadHost, configuration.ServerDadPort);
        var endpointDraftReady = connectionEditor.ValidateEndpoint(out _) && connectionEditor.ValidateSecurity(out _);
        var endpointReady = stepIndex == 3 ? endpointDraftReady : savedEndpointReady;
        var securityReady = stepIndex == 3
            ? !connectionEditor.DraftRequiresSharedSecret || !string.IsNullOrWhiteSpace(connectionEditor.DraftSharedSecret)
            : !transport.SharedSecretRequired || transport.SharedSecretConfigured;
        var participantCount = Math.Max(transport.PublishedParticipantCount, transport.KnownParticipantCount);
        var connectionReady = coordinator
            ? plugin.TransportService.IsReady && !string.IsNullOrWhiteSpace(transport.ListenerEndpoint) && participantCount > 0
            : (transport.AuthorityRoutable || plugin.HasServerDadAuthority()) && string.IsNullOrWhiteSpace(transport.LastAuthOrProtocolError);

        return
        [
            new GuideStep(
                "Local permission",
                "The global DAD switch and permission for the character logged into this client.",
                "DAD refuses automation unless both gates are on.",
                "DAD is enabled and this character is allowed.",
                draftPluginEnabled && draftProfileEnabled,
                "Turn on both checkboxes, then save the step."),
            new GuideStep(
                "Account ownership",
                "Which DAD account owns this client's character profiles and roster rows.",
                "Preset rows and connected workers use stable account identity.",
                "A saved local account is selected.",
                !string.IsNullOrWhiteSpace(draftAccountId),
                "Select a local account. DAD creates one automatically on first use."),
            new GuideStep(
                "Client role",
                coordinator ? "Makes this instance the one Coordinator that owns plans and dispatches work." : "Makes this instance a Client that accepts work from a Coordinator.",
                "A crew needs exactly one authority; every other DAD instance is a Client.",
                coordinator ? "Role reports Coordinator." : "Role reports Client.",
                roleReady,
                coordinator ? "Confirm the Coordinator role and save." : "Confirm the Client role and save."),
            new GuideStep(
                "Endpoint and security",
                coordinator ? "The listener address, port, and secret Clients use." : "The Coordinator address, port, and matching shared secret.",
                "DAD transports authenticated crew state and commands over this route.",
                coordinator ? "The listener endpoint is valid and LAN routes have a secret." : "The Coordinator endpoint is valid and its LAN secret is present.",
                endpointReady && securityReady,
                "Use 127.0.0.1 for one PC. For LAN use the Coordinator's reachable address and the same non-empty secret everywhere."),
            new GuideStep(
                coordinator ? "Listener and participants" : "Authenticated authority",
                coordinator ? "Live listener readiness and the participants currently published to it." : "Live authenticated discovery of the Coordinator authority.",
                coordinator ? "The Coordinator must be listening before Clients can join, and a participant proves the crew route works." : "A configured address is not enough; the Client must authenticate and discover the authority.",
                coordinator ? "Listener is ready and at least one participant is visible." : "The Coordinator is routable with no authentication/protocol error.",
                connectionReady,
                BuildConnectionVerificationBlocker(coordinator, transport, participantCount)),
            new GuideStep(
                "Review",
                "A final live summary of local gates, role, endpoint, security, and peers.",
                "Review catches a mismatch before you build presets around the wrong client.",
                "Every required connection check is ready.",
                configuration.PluginEnabled && profile.Enabled && roleReady && endpointReady && securityReady && connectionReady,
                DadGuideReadiness.Build(plugin, coordinator ? DadGuideFlow.Coordinator : DadGuideFlow.Client).NextAction),
        ];
    }

    private IReadOnlyList<GuideStep> BuildPresetSteps()
    {
        var selected = plugin.GetSelectedPlannerGroup();
        var snapshot = plugin.GetPlannerUiSnapshot(plugin.GetVisibleRunState());
        var lane = plugin.PresetProviderService.GetPlannerLaneDefinition(presetPlannerDraft.ActivityMode);
        var contentReady = DadLegacyActivityRules.IsCreationActivity(presetPlannerDraft.ActivityMode) &&
                           (!lane.RequiresDutySelector || presetPlannerDraft.DutyContentFinderConditionId > 0);
        if (lane.RequiresRouletteSelector)
            contentReady = !string.IsNullOrWhiteSpace(presetPlannerDraft.RouletteTarget?.Key);
        var crew = presetCrewDraft ?? selected;
        var assignedPrimary = crew?.Slots.Any(static slot =>
            !slot.IsSubstitute && (!slot.RequiredAccountKey.IsEmpty || !slot.RequiredCharacterKey.IsEmpty)) == true;
        var selectedReady = selected != null && string.Equals(selected.GroupId, presetDraftGroupId, StringComparison.OrdinalIgnoreCase);

        return
        [
            new GuideStep(
                "Activity and content",
                "The run family, submode, and exact duty or roulette DAD will execute.",
                "These choices select the runtime contract, party size, queue owner, and compatible content.",
                "A live-capable activity has all required content selected.",
                contentReady,
                lane.RequiresRouletteSelector ? "Choose a roulette." : lane.RequiresDutySelector ? "Search for and select a compatible duty." : "Choose the activity you intend to run."),
            new GuideStep(
                "Name and save",
                "Creates a new saved preset or continues the currently selected one.",
                "Schedules and repeat runs reference a stable saved preset, not an unsaved preview.",
                "A non-empty name is saved and selected.",
                selectedReady,
                "Enter a useful name and save the step."),
            new GuideStep(
                "Assign the crew",
                plugin.Configuration.DebugUiEnabled
                    ? "One inline row per primary or substitute character, including job, role, loot, level seek, wake, and optional launch-profile metadata."
                    : "One inline row per primary or substitute character, including job, role, loot, level seek, and wake/relog policy.",
                "DAD freezes these exact rows before waking clients, assembling the party, and queueing.",
                "At least one primary row has an account or exact character assignment.",
                assignedPrimary,
                "Add a primary slot and assign its account or character. Resolve any missing roster ownership in Build the Crew."),
            new GuideStep(
                "Stop and finish rules",
                "When repeated work stops and which safe completion actions run afterward.",
                "Every loop needs a bounded stop policy; finish actions are snapshotted into the run contract.",
                "The stop rule is normalized and finish behavior is explicit or uses global defaults.",
                selected?.StopPolicy != null,
                "Choose a stop mode and save the step."),
            new GuideStep(
                "Validate",
                "Read-only planner and scheduler readiness for the saved preset.",
                "DAD checks the crew, selected content, and required plugins without starting the Plan.",
                "The scheduler preview can start now or wake the configured crew.",
                selected != null && snapshot.SchedulerPreview.CanStart,
                FormatText(snapshot.SchedulerPreview.BlockedReason, snapshot.RequestPreview.StatusSummary)),
            new GuideStep(
                "Review",
                "The saved activity, crew, stop rule, finish behavior, and readiness.",
                "Finishing this guide saves setup only; it never launches a preset.",
                "The preset is saved and ready for Plan or a Schedule.",
                selected != null && snapshot.SchedulerPreview.CanStart,
                DadGuideReadiness.Build(plugin, DadGuideFlow.FirstPreset).NextAction),
        ];
    }

    private IReadOnlyList<GuideStep> BuildCrewSteps()
    {
        var catalog = plugin.RosterCatalogService.CurrentCatalog;
        var active = catalog.Characters.Where(static row => row.Visibility == DadRosterVisibility.Active).ToList();
        if (plugin.Configuration.DebugUiEnabled)
            EnsureLaunchProfileDrafts();
        var unassigned = active.Count(row =>
            row.AccountKey.IsEmpty &&
            !crewOwnershipAssignments.Contains(DadRosterIdentity.BuildKey(row)));
        var stale = active.Count(static row => row.IsStale || row.NeedsRosterUpdate);
        var steps = new List<GuideStep>
        {
            new GuideStep(
                "Refresh the roster",
                "Collects local runtime/XADB rows and roster catalogs from connected DAD clients.",
                "DAD cannot assign a character it has never learned.",
                "At least one roster row is visible.",
                catalog.Characters.Count > 0,
                "Refresh local roster, then populate connected roster if this is a multi-client crew.",
                DadDebugUiRules.CrewRosterStepId),
            new GuideStep(
                "Account ownership",
                "Maps local characters to the stable DAD account that owns their profile.",
                "Accounts are the durable bridge between roster rows, preset slots, and connected clients.",
                "Every Active local row has an account.",
                active.Count > 0 && unassigned == 0,
                unassigned == 0 ? "Mark at least one row Active." : $"Assign {unassigned} Active row(s) to this client's account.",
                DadDebugUiRules.CrewAccountsStepId),
            new GuideStep(
                "Resolve stale rows",
                "Refreshes Active rows marked stale or needing an update.",
                "The scheduler does not guess from stale ownership or character state.",
                "No Active row is stale or marked Needs update.",
                active.Count > 0 && stale == 0,
                $"Refresh or queue updates for {stale} Active row(s).",
                DadDebugUiRules.CrewCharactersStepId),
        };
        if (plugin.Configuration.DebugUiEnabled)
        {
            steps.Add(new GuideStep(
                "Launch profiles (optional debug scaffolding)",
                "Reviews the stored batch/account metadata that DAD does not currently execute.",
                "This metadata is retained for compatibility and diagnostics; it is never required for Crew readiness.",
                "Optional profile drafts may be saved or left unchanged.",
                true,
                "Optional; continue without configuring a profile.",
                DadDebugUiRules.CrewLaunchProfilesStepId));
        }
        steps.Add(
            new GuideStep(
                "Review",
                "Live account, Active roster, and current roster blockers.",
                "This is the crew foundation used by every preset and schedule.",
                "Roster ownership is current.",
                catalog.Characters.Count > 0 && active.Count > 0 && unassigned == 0 && stale == 0,
                DadGuideReadiness.Build(plugin, DadGuideFlow.Crew).NextAction,
                DadDebugUiRules.CrewReviewStepId));
        return steps;
    }

    private IReadOnlyList<GuideStep> BuildScheduleSteps()
    {
        var snapshot = plugin.SchedulerService.GetScheduleSnapshot();
        var schedule = FindSchedule(snapshot);
        var groups = plugin.Configuration.PlannerGroups;
        var known = groups.Select(static group => group.GroupId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var workingSchedule = scheduleDraft != null &&
                              (schedule == null || string.Equals(scheduleDraft.ScheduleId, schedule.ScheduleId, StringComparison.OrdinalIgnoreCase))
            ? scheduleDraft
            : schedule;
        var identityReady = schedule != null || scheduleCreateNew && !string.IsNullOrWhiteSpace(scheduleName) && groups.Count > 0;
        var entriesValid = workingSchedule != null && workingSchedule.Entries.Count > 0 && workingSchedule.Entries.All(entry => known.Contains(entry.GroupId));
        var lastDryRun = schedule == null ? null : snapshot.RecentResults.FirstOrDefault(result =>
            result.DryRun && string.Equals(result.ScheduleId, schedule.ScheduleId, StringComparison.OrdinalIgnoreCase));
        return
        [
            new GuideStep(
                "Schedule identity",
                "Creates or selects the saved schedule that will own this ordered chain.",
                "Schedule entries and daily-reset history need a stable schedule ID.",
                "A named schedule is selected.",
                identityReady,
                groups.Count == 0 ? "Create a preset first, then return here." : "Create a schedule or select an existing one."),
            new GuideStep(
                "Ordered presets",
                "Adds saved presets, repeat counts, and their exact run order.",
                "The schedule runner executes entries top to bottom and preserves repeat boundaries.",
                "At least one entry exists and every entry still references a saved preset.",
                entriesValid,
                workingSchedule?.Entries.Count > 0 ? "Replace or remove entries whose preset is missing." : "Add at least one saved preset."),
            new GuideStep(
                "Cadence",
                "Chooses manual-only execution or one run per FFXIV daily reset window.",
                "Cadence controls automatic eligibility; it does not bypass Coordinator or active-run guards.",
                "Manual or Daily reset is saved explicitly.",
                schedule != null,
                "Select a schedule first."),
            new GuideStep(
                "Review blockers",
                "Checks role, preset references, active schedule locks, and the ordered execution count.",
                "A dry-run should exercise the same schedule validation without launching the real presets.",
                "Entries are valid and no other schedule is active.",
                entriesValid && !snapshot.ActiveRun.IsActive,
                snapshot.ActiveRun.IsActive ? "Wait for or cancel the active schedule." : "Fix missing or empty entries."),
            new GuideStep(
                "Dry-run",
                "Runs the existing non-launching schedule validation path.",
                "This proves the schedule can resolve every saved preset before daily or manual execution.",
                "The selected schedule has a successful dry-run result.",
                lastDryRun?.Success == true,
                lastDryRun == null ? "Start a dry-run and wait for its result." : FormatText(lastDryRun.BlockedReason, lastDryRun.Summary)),
            new GuideStep(
                "Review",
                "The saved identity, order, repeats, cadence, and last dry-run outcome.",
                "Finishing saves the builder only. Run now remains a deliberate action in Schedules.",
                "The schedule is saved with a successful dry-run.",
                schedule != null && entriesValid && lastDryRun?.Success == true,
                DadGuideReadiness.Build(plugin, DadGuideFlow.Schedule).NextAction),
        ];
    }

    private void DrawConnectionFlowStep(bool coordinator)
    {
        var configuration = plugin.Configuration;
        var profile = plugin.ConfigManager.GetActiveConfig();
        var transport = plugin.TransportService.CurrentTransport;
        switch (stepIndex)
        {
            case 0:
                EnsureBasicsDraft();
                ImGui.Checkbox("DAD enabled", ref draftPluginEnabled);
                ImGui.Checkbox("Allow DAD to automate this character", ref draftProfileEnabled);
                ImGui.TextDisabled("These drafts are applied together when you choose Save and Next.");
                break;
            case 1:
                DrawAccountSelector();
                break;
            case 2:
                DadUi.Badge(coordinator ? "Coordinator" : "Client", DadUiTone.Info);
                ImGui.TextWrapped(coordinator
                    ? "This instance will listen for Clients, own saved schedule execution, assemble parties, and dispatch work."
                    : "This instance will connect to the Coordinator and accept only authenticated work for its owned characters.");
                ImGui.TextDisabled("The role is applied through DAD's reconnect-safe role setter on Next.");
                break;
            case 3:
                ImGui.TextWrapped(coordinator
                    ? "Use 127.0.0.1 when all game clients run on this PC. Use a listed LAN interface when Clients run on another PC."
                    : "Enter the exact Coordinator address. Use 127.0.0.1 only when the Coordinator is on this PC.");
                connectionEditor.DrawEndpointFields(configuration, $"dad-guide-{flow}", showApplyActions: false, compact: true);
                ImGui.Spacing();
                connectionEditor.DrawSharedSecretFields(configuration, $"dad-guide-{flow}", showApplyActions: false, showGenerateAndCopy: false);
                if (coordinator)
                {
                    if (ImGui.Button("Generate secret draft"))
                        connectionEditor.GenerateDraftSharedSecret();
                    ImGui.SameLine();
                    ImGui.TextDisabled("The generated value is not applied until Next.");
                }
                DrawStatusRow("Draft endpoint", connectionEditor.DraftEndpoint);
                DrawStatusRow("Draft security", connectionEditor.DraftRequiresSharedSecret
                    ? string.IsNullOrWhiteSpace(connectionEditor.DraftSharedSecret) ? "LAN secret required and missing" : "LAN secret ready"
                    : "Loopback; secret optional");
                break;
            case 4:
                DrawStatusRow("Role", configuration.RunAsServerDad ? "Coordinator" : "Client");
                DrawStatusRow("Configured endpoint", FormatText(transport.ConfiguredEndpoint, "(none)"));
                DrawStatusRow("Connection", FormatText(transport.ConnectionStatus, transport.Availability));
                DrawStatusRow("Security", transport.SharedSecretRequired
                    ? transport.SharedSecretConfigured ? "Shared secret configured" : "Shared secret missing"
                    : "Loopback; shared secret optional");
                if (coordinator)
                {
                    DrawStatusRow("Listener", FormatText(transport.ListenerEndpoint, "Not listening"));
                    DrawStatusRow("Participants", $"{Math.Max(transport.PublishedParticipantCount, transport.KnownParticipantCount)} visible | {transport.ConnectedPeerCount} peer connection(s)");
                }
                else
                {
                    DrawStatusRow("Authority", $"{transport.AuthorityStatus} | {FormatText(transport.AuthorityEndpoint, "(none)")}");
                    if (!string.IsNullOrWhiteSpace(transport.LastAuthOrProtocolError))
                        DrawStatusRow("Authentication/protocol", transport.LastAuthOrProtocolError);
                }
                if (ImGui.Button("Refresh local roster"))
                    plugin.RefreshCharacterPoolFromShell();
                ImGui.SameLine();
                if (ImGui.Button("Refresh connected roster"))
                    plugin.RequestPeerSnapshotsFromShell();
                break;
            default:
                var progress = DadGuideReadiness.Build(plugin, coordinator ? DadGuideFlow.Coordinator : DadGuideFlow.Client);
                DrawStatusRow("Local gates", $"DAD {(configuration.PluginEnabled ? "enabled" : "disabled")} | character {(profile.Enabled ? "allowed" : "blocked")}");
                DrawStatusRow("Account", FormatText(configuration.ClientAccountId, "(missing)"));
                DrawStatusRow("Role", configuration.RunAsServerDad ? "Coordinator" : "Client");
                DrawStatusRow("Endpoint", FormatText(transport.ConfiguredEndpoint, "(none)"));
                DrawStatusRow("Connection", FormatText(transport.ConnectionStatus, transport.Availability));
                DrawStatusRow("Readiness", progress.Ready ? "Ready" : $"{progress.Complete}/{progress.Total}: {progress.NextAction}");
                break;
        }
    }

    private void DrawPresetStep()
    {
        switch (stepIndex)
        {
            case 0:
                DrawPresetActivityEditor();
                break;
            case 1:
                ImGui.Checkbox("Create a new preset", ref presetCreateNew);
                if (!presetCreateNew && plugin.GetSelectedPlannerGroup() == null)
                    ImGui.TextDisabled("No saved preset is selected. Choose Create a new preset.");
                ImGui.SetNextItemWidth(MathF.Min(420f, ImGui.GetContentRegionAvail().X));
                ImGui.InputText("Preset name", ref presetName, 96);
                ImGui.TextWrapped("Use a name that explains the job, such as 'Daily Main Scenario Roulette' or 'Solo Trust leveling'. Finishing this guide will not run it.");
                ImGui.TextWrapped("Saved Plans can be copied as Base64 clipboard shares. Base64 is not encryption, and finish slash commands are preserved verbatim. Imported crew arrives as anonymous placeholders that must be remapped here before validation or run.");
                break;
            case 2:
                DrawPresetCrewDraft();
                break;
            case 3:
                DrawPresetRulesDraft();
                break;
            case 4:
                DrawPresetValidation();
                break;
            default:
                DrawPresetReview();
                break;
        }
    }

    private void DrawPresetActivityEditor()
    {
        var currentFamily = plugin.PresetProviderService.GetPlannerRunFamilyLabel(presetPlannerDraft.RunFamily);
        ImGui.SetNextItemWidth(MathF.Min(300f, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("Run family", currentFamily))
        {
            foreach (var family in plugin.PresetProviderService.GetPlannerRunFamilies())
            {
                var selected = family == presetPlannerDraft.RunFamily;
                if (ImGui.Selectable(plugin.PresetProviderService.GetPlannerRunFamilyLabel(family), selected))
                {
                    presetPlannerDraft.RunFamily = family;
                    var lane = plugin.PresetProviderService.GetPlannerLaneDefinition(plugin.PresetProviderService.GetDefaultPlannerSubmode(family));
                    ApplyLaneToDraft(lane);
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var currentLane = plugin.PresetProviderService.GetPlannerLaneDefinition(presetPlannerDraft.ActivityMode);
        ImGui.SetNextItemWidth(MathF.Min(360f, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("Activity / submode", currentLane.DisplayName))
        {
            foreach (var lane in plugin.PresetProviderService.GetPlannerSubmodes(presetPlannerDraft.RunFamily))
            {
                var selected = lane.ActivityMode == presetPlannerDraft.ActivityMode;
                if (ImGui.Selectable($"{lane.DisplayName} | {lane.MaturityLabel}", selected))
                    ApplyLaneToDraft(lane);
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        DrawStatusRow("What this lane does", currentLane.Summary);
        DrawStatusRow("Party size", currentLane.ExpectedPartySize.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Maturity", currentLane.MaturityLabel);

        if (presetPlannerDraft.ActivityMode == DadPlannerActivityMode.Msq)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.62f, 0.28f, 1f));
            ImGui.TextWrapped(DadLegacyActivityRules.MsqUnsupportedBlocker);
            ImGui.PopStyleColor();
        }

        if (currentLane.RequiresRouletteSelector)
        {
            var selected = plugin.PresetProviderService.ResolvePlannerRouletteTarget(presetPlannerDraft.RouletteTarget);
            ImGui.SetNextItemWidth(MathF.Min(420f, ImGui.GetContentRegionAvail().X));
            if (ImGui.BeginCombo("Roulette", selected.Option?.DisplayName ?? "Select roulette"))
            {
                foreach (var roulette in plugin.PresetProviderService.GetPlannerRouletteOptions())
                {
                    var isSelected = string.Equals(roulette.Key, presetPlannerDraft.RouletteTarget?.Key, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(roulette.DisplayName, isSelected))
                    {
                        presetPlannerDraft.RouletteTarget = roulette.ToQueueTarget();
                        presetPlannerDraft.DutyUnsynced = false;
                        presetPlannerDraft.DutyExpectedPartySize = DadDailyRoulettePlannerRules.RequiredPartySize;
                    }
                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }
        else if (currentLane.RequiresDutySelector)
        {
            ImGui.SetNextItemWidth(MathF.Min(420f, ImGui.GetContentRegionAvail().X));
            ImGui.InputText("Search duties", ref presetDutySearch, 128);
            var selectedDuty = plugin.PresetProviderService.GetPlannerDutyOption(presetPlannerDraft.DutyContentFinderConditionId);
            ImGui.SetNextItemWidth(MathF.Min(520f, ImGui.GetContentRegionAvail().X));
            if (ImGui.BeginCombo("Duty", selectedDuty?.SelectionLabel ?? "Select a compatible duty"))
            {
                foreach (var duty in plugin.PresetProviderService.SearchPlannerDutyOptions(presetPlannerDraft.ActivityMode, presetDutySearch, 96))
                {
                    var isSelected = duty.ContentFinderConditionId == presetPlannerDraft.DutyContentFinderConditionId;
                    if (ImGui.Selectable(duty.SelectionLabel, isSelected))
                    {
                        presetPlannerDraft.DutyContentFinderConditionId = duty.ContentFinderConditionId;
                        presetPlannerDraft.DutyDisplayName = duty.DutyDisplayName;
                        presetPlannerDraft.DutyExpectedPartySize = Math.Max(1, duty.QueueSize);
                        if (presetPlannerDraft.ActivityMode is DadPlannerActivityMode.DutySupport or DadPlannerActivityMode.Trust)
                            presetPlannerDraft.DutyUnsynced = false;
                        presetDutySearch = duty.DutyDisplayName;
                    }
                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (selectedDuty != null)
                DrawStatusRow("Duty details", selectedDuty.MetadataSummary);
        }
    }

    private void DrawPresetCrewDraft()
    {
        if (presetCrewDraft == null)
        {
            ImGui.TextWrapped("Save the Name step first so DAD has a stable preset to receive crew rows.");
            return;
        }

        var nextSlot = DadPlannerSlotRules.NextPrimarySlotNumber(presetCrewDraft.Slots);
        ImGui.BeginDisabled(nextSlot == 0);
        if (ImGui.Button("Add primary row"))
        {
            presetCrewDraft.Slots.Add(new DadPlannerGroupSlot
            {
                SlotId = DadPlannerSlotRules.FormatSlotId(nextSlot),
                RequiredRole = DadPartyRole.Any,
                AllowSubstitution = false,
            });
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("All rows stay inline and expand with the Guide window's normal scrollbar.");

        var snapshot = plugin.GetPlannerUiSnapshot(plugin.GetVisibleRunState());
        presetCrewEditor.Draw(snapshot, presetCrewDraft, static _ => { }, "dad-guide-preset");
    }

    private void DrawPresetRulesDraft()
    {
        var modes = Enum.GetValues<DadPlannerStopMode>();
        var modeIndex = Array.IndexOf(modes, presetStopDraft.Mode);
        modeIndex = Math.Max(0, modeIndex);
        if (ImGui.Combo("Stop mode", ref modeIndex, modes.Select(static mode => mode.ToString()).ToArray(), modes.Length))
            presetStopDraft.Mode = modes[modeIndex];

        switch (presetStopDraft.Mode)
        {
            case DadPlannerStopMode.TargetLevel:
                var targetLevel = presetStopDraft.TargetLevel;
                if (ImGui.InputInt("Target level", ref targetLevel))
                    presetStopDraft.TargetLevel = targetLevel;
                var targetSafetyCap = presetStopDraft.SafetyCap;
                if (ImGui.InputInt("Safety cap (runs)", ref targetSafetyCap))
                    presetStopDraft.SafetyCap = targetSafetyCap;
                break;
            case DadPlannerStopMode.ItemTarget:
                var itemId = (int)Math.Min(int.MaxValue, presetStopDraft.StopItemId);
                if (ImGui.InputInt("Item ID", ref itemId))
                    presetStopDraft.StopItemId = (uint)Math.Max(0, itemId);
                var targetItemCount = presetStopDraft.StopItemTargetCount;
                if (ImGui.InputInt("Target item count", ref targetItemCount))
                    presetStopDraft.StopItemTargetCount = targetItemCount;
                var itemSafetyCap = presetStopDraft.SafetyCap;
                if (ImGui.InputInt("Safety cap (runs)", ref itemSafetyCap))
                    presetStopDraft.SafetyCap = itemSafetyCap;
                break;
            case DadPlannerStopMode.RestedXpDepleted:
                var restedSafetyCap = presetStopDraft.SafetyCap;
                if (ImGui.InputInt("Safety cap (runs)", ref restedSafetyCap))
                    presetStopDraft.SafetyCap = restedSafetyCap;
                break;
            default:
                var runs = presetStopDraft.AfterRuns;
                if (ImGui.InputInt("Runs", ref runs))
                    presetStopDraft.AfterRuns = runs;
                break;
        }
        presetStopDraft.Normalize();
        DrawStatusRow("Stop preview", presetStopDraft.Describe());

        DadUi.Section("Finish behavior", "Use global defaults or save an explicit preset snapshot.");
        ImGui.Checkbox("Use global finish defaults", ref presetUseGlobalCompletionDefaults);
        ImGui.BeginDisabled(presetUseGlobalCompletionDefaults);
        var playSound = presetCompletionDraft.PlaySound;
        if (ImGui.Checkbox("Play completion sound", ref playSound))
            presetCompletionDraft.PlaySound = playSound;
        if (presetCompletionDraft.PlaySound)
        {
            var soundId = presetCompletionDraft.SoundEffectId;
            if (ImGui.InputInt("Sound effect (1-16)", ref soundId))
                presetCompletionDraft.SoundEffectId = soundId;
        }
        presetCompletionDraft.SoundEffectId = Math.Clamp(presetCompletionDraft.SoundEffectId, 1, 16);
        var runCommands = presetCompletionDraft.RunCommands;
        if (ImGui.Checkbox("Run slash commands", ref runCommands))
            presetCompletionDraft.RunCommands = runCommands;
        if (presetCompletionDraft.RunCommands)
            ImGui.InputTextMultiline("Commands (one per line)", ref presetCompletionCommands, 2048, new Vector2(-1f, 86f));
        var utilities = presetCompletionDraft.Utilities ??= new DadPostRunUtilities();
        var openCoffers = utilities.OpenGearCoffers;
        if (ImGui.Checkbox("Open gear coffers", ref openCoffers))
            utilities.OpenGearCoffers = openCoffers;
        var registerCards = utilities.RegisterTripleTriadCards;
        if (ImGui.Checkbox("Register Triple Triad cards", ref registerCards))
            utilities.RegisterTripleTriadCards = registerCards;
        var sellCards = utilities.SellTripleTriadCards;
        if (ImGui.Checkbox("Sell Triple Triad cards", ref sellCards))
            utilities.SellTripleTriadCards = sellCards;
        var gcHandIn = utilities.GrandCompanyHandInViaAutoRetainer;
        if (ImGui.Checkbox("Grand Company hand-in via AutoRetainer", ref gcHandIn))
            utilities.GrandCompanyHandInViaAutoRetainer = gcHandIn;
        ImGui.EndDisabled();
    }

    private void DrawPresetValidation()
    {
        var snapshot = plugin.GetPlannerUiSnapshot(plugin.GetVisibleRunState());
        var preview = snapshot.RequestPreview;
        DrawStatusRow("Preset", plugin.GetSelectedPlannerGroup()?.DisplayName ?? "(none)");
        DrawStatusRow("Planner", preview.StatusSummary);
        DrawStatusRow("Scheduler", snapshot.SchedulerPreview.StatusSummary);
        var dependencyBlocker = snapshot.SchedulerPreview.Slots
            .Where(static slot => !slot.DependenciesReady)
            .Select(static slot => slot.DependencySummary)
            .FirstOrDefault(static summary => !string.IsNullOrWhiteSpace(summary));
        DrawStatusRow("Startability", !string.IsNullOrWhiteSpace(dependencyBlocker)
            ? "Waiting for required plugins"
            : snapshot.SchedulerPreview.CanStart
            ? snapshot.SchedulerPreview.ReadyToStart ? "Ready now" : "Can wake configured crew"
            : "Blocked");
        if (!string.IsNullOrWhiteSpace(dependencyBlocker) || !snapshot.SchedulerPreview.CanStart)
            DrawStatusRow("First blocker", FormatText(dependencyBlocker, FormatText(snapshot.SchedulerPreview.BlockedReason, preview.ReadinessSummary)));
        ImGui.BeginDisabled(plugin.GetSelectedPlannerGroup() == null);
        string? justValidated = null;
        if (ImGui.Button("Recheck readiness (does not run)"))
            justValidated = plugin.ValidateSelectedPlannerPresetReadOnly();
        ImGui.EndDisabled();
        var selectedGroup = plugin.GetSelectedPlannerGroup();
        var feedback = selectedGroup == null
            ? null
            : plugin.GetPlannerValidationFeedback(snapshot.Generation, selectedGroup.GroupId);
        var feedbackText = justValidated ?? feedback?.Summary;
        if (!string.IsNullOrWhiteSpace(feedbackText))
            ImGui.TextWrapped(feedbackText);
    }

    private void DrawPresetReview()
    {
        var group = plugin.GetSelectedPlannerGroup();
        var snapshot = plugin.GetPlannerUiSnapshot(plugin.GetVisibleRunState());
        if (group == null)
        {
            ImGui.TextWrapped("No saved preset is selected.");
            return;
        }
        DrawStatusRow("Preset", group.DisplayName);
        DrawStatusRow("Activity", plugin.PresetProviderService.GetPlannerLaneDefinition(group.ActivityMode).DisplayName);
        DrawStatusRow("Content", FormatText(group.DutyDisplayName, group.RouletteTarget?.DisplayName ?? "Lane default"));
        DrawStatusRow("Crew rows", $"{group.Slots.Count} total | {DadPlannerSlotRules.CountPrimarySlots(group.Slots)} primary");
        DrawStatusRow("Stop", group.StopPolicy.Describe());
        DrawStatusRow("Finish", DadCompletionActionSnapshots.DescribeSource(group.CompletionActions));
        var dependencyWait = snapshot.SchedulerPreview.Slots.Any(static slot => !slot.DependenciesReady);
        DrawStatusRow("Readiness", dependencyWait
            ? "Waiting for required plugins"
            : snapshot.SchedulerPreview.CanStart
                ? snapshot.SchedulerPreview.ReadyToStart ? "Ready now" : "Saved and wakeable"
                : FormatText(snapshot.SchedulerPreview.BlockedReason, "Blocked"));
        ImGui.TextWrapped("Finish closes the guide. It does not enqueue or start this preset.");
    }

    private void DrawCrewStep()
    {
        var catalog = plugin.RosterCatalogService.CurrentCatalog;
        switch (currentStepId)
        {
            case DadDebugUiRules.CrewRosterStepId:
                if (ImGui.Button("Refresh local roster"))
                    plugin.RefreshCharacterPoolFromShell();
                ImGui.SameLine();
                if (ImGui.Button("Populate connected roster"))
                    plugin.RequestPeerSnapshotsFromShell();
                DrawStatusRow("Roster", FormatText(catalog.Summary, "Not refreshed"));
                DrawStatusRow("Rows", $"{catalog.Characters.Count} character(s) | {catalog.Accounts.Count} account(s)");
                break;
            case DadDebugUiRules.CrewAccountsStepId:
                DrawCrewOwnership(catalog);
                break;
            case DadDebugUiRules.CrewCharactersStepId:
                DrawCrewStaleRows(catalog);
                break;
            case DadDebugUiRules.CrewLaunchProfilesStepId:
                DrawCrewLaunchProfiles(catalog);
                break;
            default:
                var active = catalog.Characters.Where(static row => row.Visibility == DadRosterVisibility.Active).ToList();
                DrawStatusRow("Accounts", catalog.Accounts.Count.ToString(CultureInfo.InvariantCulture));
                DrawStatusRow("Active roster", active.Count.ToString(CultureInfo.InvariantCulture));
                DrawStatusRow("Unassigned", active.Count(static row => row.AccountKey.IsEmpty).ToString(CultureInfo.InvariantCulture));
                DrawStatusRow("Stale / needs update", active.Count(static row => row.IsStale || row.NeedsRosterUpdate).ToString(CultureInfo.InvariantCulture));
                if (plugin.Configuration.DebugUiEnabled)
                    DrawStatusRow("Optional launch-profile scaffolding", $"{plugin.Configuration.LaunchProfiles.Count} stored | {plugin.Configuration.LaunchProfiles.Count(static profile => profile.Enabled)} enabled");
                break;
        }
    }

    private void DrawCrewOwnership(DadAccountRosterCatalog catalog)
    {
        var active = catalog.Characters.Where(static row => row.Visibility == DadRosterVisibility.Active).ToList();
        if (active.Count == 0)
        {
            ImGui.TextWrapped("No Active rows. Refresh the roster, then use the Crew expert editor to activate the characters you want DAD to use.");
            return;
        }

        var account = plugin.ConfigManager.GetCurrentAccount();
        if (ImGui.BeginTable("dad-guide-crew-ownership", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Character");
            ImGui.TableSetupColumn("Saved / draft account");
            ImGui.TableSetupColumn("Source");
            ImGui.TableSetupColumn("Save on Next");
            ImGui.TableHeadersRow();
            foreach (var row in active)
            {
                var identityKey = DadRosterIdentity.BuildKey(row);
                var assignOnNext = crewOwnershipAssignments.Contains(identityKey);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(plugin.KrangleService.FormatCharacterKey(row.CharacterKey.Value));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(assignOnNext && account != null
                    ? $"{account.AccountAlias} [draft]"
                    : row.AccountKey.IsEmpty
                        ? "Unassigned"
                        : FormatText(row.AccountAlias, row.AccountKey.Value));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(IsRemoteRosterRow(row) ? "Connected Client" : "This Client");
                ImGui.TableNextColumn();
                var canAssign = row.AccountKey.IsEmpty && !IsRemoteRosterRow(row) && account != null;
                ImGui.BeginDisabled(!canAssign);
                if (ImGui.Checkbox($"Assign to this account##dad-guide-assign-{identityKey}", ref assignOnNext))
                {
                    if (assignOnNext)
                        crewOwnershipAssignments.Add(identityKey);
                    else
                        crewOwnershipAssignments.Remove(identityKey);
                }
                ImGui.EndDisabled();
                if (!canAssign && row.AccountKey.IsEmpty && IsRemoteRosterRow(row))
                    ImGui.TextDisabled("Assign on that Client");
            }
            ImGui.EndTable();
        }

        ImGui.TextDisabled("Checked ownership changes remain drafts until Save and Next.");
    }

    private void DrawCrewStaleRows(DadAccountRosterCatalog catalog)
    {
        var stale = catalog.Characters
            .Where(static row => row.Visibility == DadRosterVisibility.Active && (row.IsStale || row.NeedsRosterUpdate))
            .ToList();
        var staleKeys = stale
            .Select(DadRosterIdentity.BuildKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        crewStagedSkips.RemoveWhere(key => !staleKeys.Contains(key));
        if (stale.Count == 0)
        {
            DadUi.Badge("No stale Active rows", DadUiTone.Success);
            return;
        }
        if (ImGui.Button("Refresh local roster"))
            plugin.RefreshCharacterPoolFromShell();
        ImGui.SameLine();
        if (ImGui.Button("Queue updates for all stale rows"))
            QueueRosterUpdate(stale);

        ImGui.TextWrapped("Skip is staged as Ignore on Save and Next. Ignored rows are reversible under Crew -> Ignored. Delete removes only DAD's local cached copy; XADB snapshots and remote authoritative data remain untouched.");
        if (!ImGui.BeginTable(
                "dad-guide-stale-rows",
                4,
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.SizingStretchProp |
                ImGuiTableFlags.NoSavedSettings))
        {
            return;
        }

        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Account / source", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Problem", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthStretch, 1.35f);
        ImGui.TableHeadersRow();

        foreach (var row in stale)
        {
            var rowKey = DadRosterIdentity.BuildKey(row);
            var staged = crewStagedSkips.Contains(rowKey);
            var account = row.AccountKey.IsEmpty
                ? "Unassigned"
                : FormatText(row.AccountAlias, row.AccountKey.Value);
            var source = IsRemoteRosterRow(row)
                ? "Connected Client"
                : plugin.PresetProviderService.GetCharacterSourceLabel(row.Source);
            var problem = row.NeedsRosterUpdate && row.IsStale
                ? "Needs update; snapshot is stale"
                : row.NeedsRosterUpdate ? "Needs update" : "Snapshot is stale";

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(plugin.KrangleService.FormatCharacterKey(row.CharacterKey.Value));
            ImGui.TableNextColumn();
            ImGui.TextWrapped($"{account} | {source}");
            ImGui.TableNextColumn();
            ImGui.TextWrapped(staged ? $"{problem} | Ignore on Save and Next" : problem);
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"Queue update##dad-guide-stale-queue-{rowKey}"))
                QueueRosterUpdate([row]);
            ImGui.SameLine();
            if (ImGui.SmallButton($"{(staged ? "Undo" : "Skip")}##dad-guide-stale-skip-{rowKey}"))
            {
                if (staged)
                    crewStagedSkips.Remove(rowKey);
                else
                    crewStagedSkips.Add(rowKey);
            }
            ImGui.SameLine();
            if (DrawCrewDeleteButton(row, rowKey))
                ForgetCrewRosterCopy(row);
        }

        ImGui.EndTable();
        if (crewStagedSkips.Count > 0)
            DadUi.Badge($"{crewStagedSkips.Count} row(s) will move to Ignored on Save and Next", DadUiTone.Warning);
    }

    private void DrawCrewLaunchProfiles(DadAccountRosterCatalog catalog)
    {
        if (ImGui.Button("Import launch batches"))
        {
            plugin.ImportLaunchProfilesFromBootDirectory();
            launchProfileDrafts.Clear();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Imported batches stay read-only; mapping changes save on Next.");
        EnsureLaunchProfileDrafts();
        var profiles = plugin.Configuration.LaunchProfiles
            .Where(profile => launchProfileDrafts.ContainsKey(profile.ProfileId))
            .Select(profile => launchProfileDrafts[profile.ProfileId])
            .ToList();
        if (profiles.Count == 0)
        {
            ImGui.TextWrapped("No launch-profile metadata was found in the existing configured boot directory. DAD does not execute these batch paths.");
            return;
        }

        var accountOptions = plugin.PresetProviderService.GetPlannerAccountOptions();
        if (ImGui.BeginTable("dad-guide-launch-profiles", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Enabled");
            ImGui.TableSetupColumn("Dry-run");
            ImGui.TableSetupColumn("Profile");
            ImGui.TableSetupColumn("Account");
            ImGui.TableSetupColumn("Expected characters");
            ImGui.TableHeadersRow();
            foreach (var launch in profiles)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var enabled = launch.Enabled;
                if (ImGui.Checkbox($"##dad-guide-launch-enabled-{launch.ProfileId}", ref enabled))
                    launch.Enabled = enabled;
                ImGui.TableNextColumn();
                var dryRun = launch.DryRun;
                if (ImGui.Checkbox($"##dad-guide-launch-dry-{launch.ProfileId}", ref dryRun))
                    launch.DryRun = dryRun;
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(launch.DisplayName);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(launch.BatchPath);
                ImGui.TableNextColumn();
                var preview = launch.AccountKey.IsEmpty ? "Select account" : launch.AccountKey.Value;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.BeginCombo($"##dad-guide-launch-account-{launch.ProfileId}", preview))
                {
                    foreach (var option in accountOptions)
                    {
                        var selected = string.Equals(launch.AccountKey.Value, option.AccountKey.Value, StringComparison.OrdinalIgnoreCase);
                        if (ImGui.Selectable(FormatText(option.DisplayName, option.AccountKey.Value), selected))
                            launch.AccountKey = option.AccountKey;
                    }
                    ImGui.EndCombo();
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(launch.ExpectedCharacterKeys.Count == 0
                    ? "(none parsed)"
                    : string.Join(", ", launch.ExpectedCharacterKeys.Select(static key => key.Value)));
            }
            ImGui.EndTable();
        }
    }

    private void DrawScheduleStep()
    {
        var snapshot = plugin.SchedulerService.GetScheduleSnapshot();
        EnsureScheduleSelection(snapshot);
        var schedule = FindSchedule(snapshot);
        switch (stepIndex)
        {
            case 0:
                DrawScheduleIdentity(snapshot, schedule);
                break;
            case 1:
                if (scheduleDraft == null && schedule != null)
                    scheduleDraft = schedule.Clone();
                DrawScheduleEntries(scheduleDraft, snapshot.ActiveRun.IsActive);
                break;
            case 2:
                var daily = scheduleCadenceDraft == DadScheduleCadence.DailyReset;
                ImGui.BeginDisabled(snapshot.ActiveRun.IsActive);
                if (ImGui.Checkbox("Run once at each FFXIV daily reset", ref daily))
                    scheduleCadenceDraft = daily ? DadScheduleCadence.DailyReset : DadScheduleCadence.Manual;
                ImGui.EndDisabled();
                DrawStatusRow("Selected cadence", daily
                    ? $"Daily reset at {DadScheduleRules.DailyResetHourUtc:00}:00 UTC"
                    : "Manual only");
                if (snapshot.ActiveRun.IsActive)
                    ImGui.TextDisabled("Cadence is locked while a schedule is active.");
                ImGui.TextWrapped("Daily mode never bypasses the Coordinator role, active schedule lock, preset validation, wake, or party safety guards.");
                break;
            case 3:
                DrawScheduleBlockerReview(snapshot, schedule);
                break;
            case 4:
                DrawScheduleDryRun(snapshot, schedule);
                break;
            default:
                DrawScheduleReview(snapshot, schedule);
                break;
        }
    }

    private void DrawScheduleIdentity(DadScheduleSnapshot snapshot, DadScheduleDefinition? selectedSchedule)
    {
        ImGui.Checkbox("Create a new schedule", ref scheduleCreateNew);
        if (!scheduleCreateNew && snapshot.Schedules.Count > 0)
        {
            ImGui.SetNextItemWidth(MathF.Min(420f, ImGui.GetContentRegionAvail().X));
            if (ImGui.BeginCombo("Existing schedule", selectedSchedule?.DisplayName ?? "Select schedule"))
            {
                foreach (var candidate in snapshot.Schedules)
                {
                    var selected = string.Equals(candidate.ScheduleId, scheduleId, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(candidate.DisplayName, selected))
                    {
                        scheduleId = candidate.ScheduleId;
                        scheduleName = candidate.DisplayName;
                        scheduleCadenceDraft = candidate.Cadence;
                        scheduleDraft = candidate.Clone();
                    }
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }
        ImGui.SetNextItemWidth(MathF.Min(420f, ImGui.GetContentRegionAvail().X));
        ImGui.InputText("Schedule name", ref scheduleName, 128);
        DrawStatusRow("Saved schedules", snapshot.Schedules.Count.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Available presets", plugin.Configuration.PlannerGroups.Count.ToString(CultureInfo.InvariantCulture));

        ImGui.Spacing();
        ImGui.TextWrapped("Schedules can be shared through the clipboard as Base64. A Schedule bundles each referenced Plan once while preserving entry order and repeats. Base64 is not encryption; imported anonymous crew must be remapped locally in each Plan before validation or run.");
        var mutationBlocker = plugin.GetShareMutationBlocker();
        ImGui.BeginDisabled(!string.IsNullOrWhiteSpace(mutationBlocker));
        if (ImGui.SmallButton("Install Daily MSQ + Leveling starter bundle"))
        {
            var result = plugin.InstallStarterShareBundle();
            scheduleStarterStatus = result.Summary;
            if (result.Success)
            {
                var installed = plugin.Configuration.Schedules.FirstOrDefault(candidate =>
                    string.Equals(candidate.ScheduleId, DadStarterShareBundle.ScheduleId, StringComparison.OrdinalIgnoreCase));
                if (installed != null)
                {
                    scheduleId = installed.ScheduleId;
                    scheduleName = installed.DisplayName;
                    scheduleCadenceDraft = installed.Cadence;
                    scheduleDraft = installed.Clone();
                    scheduleCreateNew = false;
                }
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!string.IsNullOrWhiteSpace(mutationBlocker)
                ? mutationBlocker
                : "Installs only missing stable starter IDs. Existing Plans or Schedule with those IDs are never overwritten.");
        ImGui.EndDisabled();
        if (!string.IsNullOrWhiteSpace(scheduleStarterStatus))
            ImGui.TextDisabled(scheduleStarterStatus);
    }

    private void DrawScheduleEntries(DadScheduleDefinition? schedule, bool locked)
    {
        if (schedule == null)
        {
            ImGui.TextWrapped("Save Schedule identity first.");
            return;
        }
        var groups = plugin.Configuration.PlannerGroups
            .OrderBy(static group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count == 0)
        {
            ImGui.TextWrapped("No saved presets are available. Finish Create a Preset first.");
            return;
        }
        if (string.IsNullOrWhiteSpace(scheduleAddGroupId) || groups.All(group => !string.Equals(group.GroupId, scheduleAddGroupId, StringComparison.OrdinalIgnoreCase)))
            scheduleAddGroupId = groups[0].GroupId;
        scheduleRepeatCount = Math.Clamp(scheduleRepeatCount, DadScheduleRules.MinRepeatCount, DadScheduleRules.MaxRepeatCount);

        ImGui.BeginDisabled(locked);
        DrawSchedulePresetCombo(ref scheduleAddGroupId, groups, "add");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f);
        ImGui.InputInt("Repeat##dad-guide-schedule-add-repeat", ref scheduleRepeatCount);
        scheduleRepeatCount = Math.Clamp(scheduleRepeatCount, DadScheduleRules.MinRepeatCount, DadScheduleRules.MaxRepeatCount);
        ImGui.SameLine();
        if (ImGui.Button("Add preset"))
        {
            var group = groups.First(candidate => string.Equals(candidate.GroupId, scheduleAddGroupId, StringComparison.OrdinalIgnoreCase));
            schedule.Entries.Add(new DadScheduleEntry
            {
                GroupId = group.GroupId,
                PresetName = group.DisplayName,
                RepeatCount = scheduleRepeatCount,
            });
        }
        ImGui.EndDisabled();

        if (schedule.Entries.Count == 0)
        {
            ImGui.TextDisabled("No presets in this schedule yet.");
            return;
        }
        if (!ImGui.BeginTable("dad-guide-schedule-entries", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("#");
        ImGui.TableSetupColumn("Preset");
        ImGui.TableSetupColumn("Repeat");
        ImGui.TableSetupColumn("Order");
        ImGui.TableSetupColumn("Remove");
        ImGui.TableHeadersRow();
        for (var index = 0; index < schedule.Entries.Count; index++)
        {
            var entry = schedule.Entries[index];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((index + 1).ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            var entryGroupId = entry.GroupId;
            ImGui.BeginDisabled(locked);
            if (DrawSchedulePresetCombo(ref entryGroupId, groups, entry.EntryId))
            {
                var group = groups.First(candidate => string.Equals(candidate.GroupId, entryGroupId, StringComparison.OrdinalIgnoreCase));
                entry.GroupId = group.GroupId;
                entry.PresetName = group.DisplayName;
                entry.UpdatedAtUtc = DateTime.UtcNow;
            }
            ImGui.EndDisabled();
            ImGui.TableNextColumn();
            var repeat = entry.RepeatCount;
            ImGui.BeginDisabled(locked);
            ImGui.SetNextItemWidth(88f);
            if (ImGui.InputInt($"##dad-guide-repeat-{entry.EntryId}", ref repeat))
            {
                entry.RepeatCount = Math.Clamp(repeat, DadScheduleRules.MinRepeatCount, DadScheduleRules.MaxRepeatCount);
                entry.UpdatedAtUtc = DateTime.UtcNow;
            }
            ImGui.EndDisabled();
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(locked || index == 0);
            var up = ImGui.SmallButton($"Up##dad-guide-up-{entry.EntryId}");
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(locked || index >= schedule.Entries.Count - 1);
            var down = ImGui.SmallButton($"Down##dad-guide-down-{entry.EntryId}");
            ImGui.EndDisabled();
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(locked);
            var remove = ImGui.SmallButton($"Remove##dad-guide-remove-{entry.EntryId}");
            ImGui.EndDisabled();
            if (up)
            {
                (schedule.Entries[index - 1], schedule.Entries[index]) = (schedule.Entries[index], schedule.Entries[index - 1]);
                break;
            }
            if (down)
            {
                (schedule.Entries[index + 1], schedule.Entries[index]) = (schedule.Entries[index], schedule.Entries[index + 1]);
                break;
            }
            if (remove)
            {
                schedule.Entries.RemoveAt(index);
                break;
            }
        }
        ImGui.EndTable();
        ImGui.TextDisabled("Order and repeat changes remain drafts until Save and Next.");
    }

    private void DrawScheduleBlockerReview(DadScheduleSnapshot snapshot, DadScheduleDefinition? schedule)
    {
        var known = plugin.Configuration.PlannerGroups.Select(static group => group.GroupId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = schedule?.Entries.Count(entry => !known.Contains(entry.GroupId)) ?? 0;
        DrawStatusRow("Role", plugin.Configuration.RunAsServerDad ? "Coordinator - live runs allowed" : "Client - builder/dry-run only; live schedules require Coordinator");
        DrawStatusRow("Schedule", schedule?.DisplayName ?? "(none)");
        DrawStatusRow("Entries", schedule?.Entries.Count.ToString(CultureInfo.InvariantCulture) ?? "0");
        DrawStatusRow("Total executions", schedule?.Entries.Sum(static entry => entry.RepeatCount).ToString(CultureInfo.InvariantCulture) ?? "0");
        DrawStatusRow("Missing presets", missing.ToString(CultureInfo.InvariantCulture));
        DrawStatusRow("Runner", snapshot.ActiveRun.IsActive ? snapshot.ActiveRun.Summary : "Idle");
    }

    private void DrawScheduleDryRun(DadScheduleSnapshot snapshot, DadScheduleDefinition? schedule)
    {
        if (schedule == null)
        {
            ImGui.TextWrapped("No schedule selected.");
            return;
        }
        var canDryRun = schedule.Entries.Count > 0 && !snapshot.ActiveRun.IsActive;
        ImGui.BeginDisabled(!canDryRun);
        if (DadUi.Button("Run dry-run", DadUiTone.Accent))
            plugin.StartScheduleRunFromShell(schedule.ScheduleId, dryRun: true, requestedBy: "guide-scheduler");
        ImGui.EndDisabled();
        var active = snapshot.ActiveRun;
        if (active.IsActive && active.DryRun && string.Equals(active.ScheduleId, schedule.ScheduleId, StringComparison.OrdinalIgnoreCase))
        {
            DrawStatusRow("Dry-run", $"{active.Phase} | {active.Summary}");
            return;
        }
        var result = snapshot.RecentResults.FirstOrDefault(candidate =>
            candidate.DryRun && string.Equals(candidate.ScheduleId, schedule.ScheduleId, StringComparison.OrdinalIgnoreCase));
        DrawStatusRow("Last dry-run", result == null
            ? "None recorded"
            : $"{(result.Success ? "Ready" : "Blocked")} at {FormatTime(result.CompletedAtUtc)} | {FormatText(result.BlockedReason, result.Summary)}");
    }

    private void DrawScheduleReview(DadScheduleSnapshot snapshot, DadScheduleDefinition? schedule)
    {
        if (schedule == null)
        {
            ImGui.TextWrapped("No saved schedule selected.");
            return;
        }
        var dryRun = snapshot.RecentResults.FirstOrDefault(result => result.DryRun && string.Equals(result.ScheduleId, schedule.ScheduleId, StringComparison.OrdinalIgnoreCase));
        DrawStatusRow("Schedule", schedule.DisplayName);
        DrawStatusRow("Order", schedule.Entries.Count == 0 ? "(empty)" : string.Join(" -> ", schedule.Entries.Select(entry => $"{entry.PresetName} x{entry.RepeatCount}")));
        DrawStatusRow("Cadence", schedule.Cadence == DadScheduleCadence.DailyReset ? $"Daily reset at {DadScheduleRules.DailyResetHourUtc:00}:00 UTC" : "Manual only");
        DrawStatusRow("Dry-run", dryRun?.Success == true ? "Ready" : FormatText(dryRun?.BlockedReason, dryRun?.Summary ?? "Not run"));
        ImGui.TextWrapped("Finish closes the guide. Use Schedules to deliberately run, cancel, rename, duplicate, or delete this schedule.");
    }

    private void TryAdvance(int stepCount)
    {
        validationMessage = string.Empty;
        if (!ValidateAndCommitCurrentStep())
            return;
        if (stepIndex >= stepCount - 1)
        {
            IsOpen = false;
            return;
        }
        stepIndex++;
        var steps = BuildSteps();
        if (stepIndex < steps.Count)
            currentStepId = steps[stepIndex].Id;
        furthestStep = Math.Max(furthestStep, stepIndex);
    }

    private bool ValidateAndCommitCurrentStep()
        => flow switch
        {
            DadGuideFlow.Coordinator => CommitConnectionStep(coordinator: true),
            DadGuideFlow.Client => CommitConnectionStep(coordinator: false),
            DadGuideFlow.FirstPreset => CommitPresetStep(),
            DadGuideFlow.Crew => CommitCrewStep(),
            DadGuideFlow.Schedule => CommitScheduleStep(),
            _ => true,
        };

    private bool CommitConnectionStep(bool coordinator)
    {
        var configuration = plugin.Configuration;
        switch (stepIndex)
        {
            case 0:
                EnsureBasicsDraft();
                if (!draftPluginEnabled || !draftProfileEnabled)
                    return Reject("DAD and this character must both be allowed before the connection workflow can continue.");
                plugin.SetPluginEnabled(draftPluginEnabled, printStatus: false);
                var profile = plugin.ConfigManager.GetActiveConfig();
                profile.Enabled = draftProfileEnabled;
                plugin.ConfigManager.SaveCurrentAccount();
                plugin.UpdateDtrBar();
                return true;
            case 1:
                if (string.IsNullOrWhiteSpace(draftAccountId))
                    return Reject("Select a saved local DAD account.");
                plugin.ConfigManager.EnsureAccountSelected(draftAccountId, "DAD client");
                configuration.ClientAccountId = plugin.ConfigManager.CurrentAccountId;
                configuration.LastAccountId = plugin.ConfigManager.CurrentAccountId;
                configuration.Save();
                return true;
            case 2:
                plugin.SetRunAsServerDad(coordinator);
                connectionEditor.Reset(configuration);
                return true;
            case 3:
                if (!connectionEditor.ValidateEndpoint(out var endpointBlocker))
                    return Reject(endpointBlocker);
                if (!connectionEditor.ValidateSecurity(out var securityBlocker))
                    return Reject(securityBlocker);
                connectionEditor.CommitEndpoint(configuration);
                connectionEditor.CommitSharedSecret(configuration);
                return true;
            case 4:
                var transport = plugin.TransportService.CurrentTransport;
                if (coordinator)
                {
                    if (!plugin.TransportService.IsReady || string.IsNullOrWhiteSpace(transport.ListenerEndpoint))
                        return Reject("The Coordinator listener is not ready. Recheck the applied address, DAD enabled state, and connection status.");
                    if (Math.Max(transport.PublishedParticipantCount, transport.KnownParticipantCount) <= 0)
                        return Reject("No participant is visible yet. Connect or refresh at least one DAD participant before review.");
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(transport.LastAuthOrProtocolError))
                        return Reject(transport.LastAuthOrProtocolError);
                    if (!transport.AuthorityRoutable && !plugin.HasServerDadAuthority())
                        return Reject("Authenticated Coordinator authority has not been discovered. Verify host, port, and the exact shared secret.");
                }
                return true;
            default:
                var progress = DadGuideReadiness.Build(plugin, coordinator ? DadGuideFlow.Coordinator : DadGuideFlow.Client);
                return progress.Ready || Reject(progress.NextAction);
        }
    }

    private bool CommitPresetStep()
    {
        switch (stepIndex)
        {
            case 0:
                if (!DadLegacyActivityRules.IsCreationActivity(presetPlannerDraft.ActivityMode))
                    return Reject(DadLegacyActivityRules.MsqUnsupportedBlocker);
                var lane = plugin.PresetProviderService.GetPlannerLaneDefinition(presetPlannerDraft.ActivityMode);
                if (lane.RequiresDutySelector && presetPlannerDraft.DutyContentFinderConditionId == 0)
                    return Reject("Select a compatible duty for this activity.");
                if (lane.RequiresRouletteSelector && string.IsNullOrWhiteSpace(presetPlannerDraft.RouletteTarget?.Key))
                    return Reject("Select a roulette for this activity.");
                plugin.Configuration.PlannerOptions = ClonePlannerOptions(presetPlannerDraft);
                plugin.SavePlannerOptions();
                return true;
            case 1:
                if (string.IsNullOrWhiteSpace(presetName))
                    return Reject("Enter a preset name that will be recognizable in Plan and Schedules.");
                if (presetCreateNew)
                    plugin.ClearPlannerGroupSelection();
                else if (plugin.GetSelectedPlannerGroup() == null)
                    return Reject("No existing preset is selected. Choose Create a new preset.");
                var group = plugin.SaveCurrentPlannerGroup(presetName, out _, out var rejectionReason);
                if (group == null)
                    return Reject(rejectionReason);
                presetName = group.DisplayName;
                presetDraftGroupId = group.GroupId;
                presetCreateNew = false;
                presetCrewDraft = ClonePlannerGroup(group);
                presetStopDraft = group.StopPolicy.Clone();
                presetUseGlobalCompletionDefaults = group.CompletionActions == null;
                presetCompletionDraft = (group.CompletionActions ?? plugin.Configuration.CompletionActions).Clone();
                presetCompletionCommands = string.Join("\n", presetCompletionDraft.Commands);
                return true;
            case 2:
                if (presetCrewDraft == null)
                    return Reject("Save the preset name first.");
                if (!presetCrewDraft.Slots.Any(static slot =>
                        !slot.IsSubstitute && (!slot.RequiredAccountKey.IsEmpty || !slot.RequiredCharacterKey.IsEmpty)))
                    return Reject("Assign at least one primary account or exact character row.");
                var saved = plugin.ResolvePlannerGroup(presetDraftGroupId);
                if (saved == null)
                    return Reject("The saved preset no longer exists. Return to Name and save it again.");
                saved.Slots = presetCrewDraft.Slots.Select(CloneSlot).ToList();
                plugin.TouchPlannerGroup(saved);
                presetCrewDraft = ClonePlannerGroup(saved);
                return true;
            case 3:
                var target = plugin.ResolvePlannerGroup(presetDraftGroupId);
                if (target == null)
                    return Reject("The saved preset no longer exists.");
                presetStopDraft.Normalize();
                target.StopPolicy = presetStopDraft.Clone();
                DadSharedPlanRules.ReconcileStopTarget(target);
                if (presetUseGlobalCompletionDefaults)
                {
                    target.CompletionActions = null;
                }
                else
                {
                    presetCompletionDraft.Commands = presetCompletionCommands
                        .Split('\n')
                        .Select(static command => command.Trim())
                        .Where(static command => command.Length > 0)
                        .ToList();
                    target.CompletionActions = presetCompletionDraft.Clone();
                }
                plugin.TouchPlannerGroup(target);
                plugin.PlannerOptions.StopPolicy = target.StopPolicy.Clone();
                plugin.PlannerOptions.CompletionActions = target.CompletionActions?.Clone();
                plugin.SavePlannerOptions();
                return true;
            case 4:
                plugin.ValidateSelectedPlannerPresetReadOnly();
                var snapshot = plugin.GetPlannerUiSnapshot(plugin.GetVisibleRunState());
                return snapshot.SchedulerPreview.CanStart || Reject(FormatText(snapshot.SchedulerPreview.BlockedReason, snapshot.RequestPreview.StatusSummary));
            default:
                var progress = DadGuideReadiness.Build(plugin, DadGuideFlow.FirstPreset);
                return progress.Ready || Reject(progress.NextAction);
        }
    }

    private bool CommitCrewStep()
    {
        var catalog = plugin.RosterCatalogService.CurrentCatalog;
        var active = catalog.Characters.Where(static row => row.Visibility == DadRosterVisibility.Active).ToList();
        switch (currentStepId)
        {
            case DadDebugUiRules.CrewRosterStepId:
                return catalog.Characters.Count > 0 || Reject("No roster rows are visible yet. Refresh local roster, then populate connected roster if needed.");
            case DadDebugUiRules.CrewAccountsStepId:
                var account = plugin.ConfigManager.GetCurrentAccount();
                if (crewOwnershipAssignments.Count > 0 && account == null)
                    return Reject("The selected local account is no longer available. Return to connection setup and select an account.");
                if (account != null)
                {
                    foreach (var row in active.Where(row =>
                                 row.AccountKey.IsEmpty &&
                                 !IsRemoteRosterRow(row) &&
                                 crewOwnershipAssignments.Contains(DadRosterIdentity.BuildKey(row))))
                    {
                        ChangeRosterAssignment(row, new DadAccountKey(account.AccountId), account.AccountAlias);
                    }
                }
                crewOwnershipAssignments.Clear();
                var savedActive = plugin.RosterCatalogService.CurrentCatalog.Characters
                    .Where(static row => row.Visibility == DadRosterVisibility.Active)
                    .ToList();
                return savedActive.Count > 0 && savedActive.All(static row => !row.AccountKey.IsEmpty) || Reject("Every Active roster row needs account ownership. Connected rows must be assigned on their owning Client.");
            case DadDebugUiRules.CrewCharactersStepId:
                var stagedRows = active
                    .Where(row => crewStagedSkips.Contains(DadRosterIdentity.BuildKey(row)))
                    .ToList();
                if (active.Count - stagedRows.Count < 1)
                    return Reject("At least one Active roster row must remain. Undo one Skip, queue its update, or correct the source data.");

                DadAccountRosterCatalog refreshedCatalog;
                if (stagedRows.Count > 0)
                {
                    var resultJson = plugin.SetRosterVisibilityFromJson(DadIpcJson.Serialize(new DadRosterVisibilityChangeRequest
                    {
                        CharacterRefs = stagedRows.Select(DadRosterIdentity.From).ToList(),
                        Visibility = DadRosterVisibility.Ignored,
                        Reason = "Ignored from Build the Crew guide on Save and Next.",
                    }));
                    refreshedCatalog = DadIpcJson.Deserialize<DadAccountRosterCatalog>(resultJson)
                                       ?? plugin.RosterCatalogService.CurrentCatalog;
                    crewStagedSkips.Clear();
                }
                else
                {
                    refreshedCatalog = plugin.RosterCatalogService.RefreshCatalog(
                        plugin.CharacterIntelligenceService.CurrentPool,
                        new DadRosterRefreshPlan
                        {
                            IncludeHidden = true,
                            IncludeIgnored = true,
                            StaleAfterHours = plugin.Configuration.RosterCatalog.StaleAfterHours,
                        });
                }

                var remainingActive = refreshedCatalog.Characters
                    .Where(static row => row.Visibility == DadRosterVisibility.Active)
                    .ToList();
                return remainingActive.Count > 0 &&
                       remainingActive.All(static row => !row.IsStale && !row.NeedsRosterUpdate) ||
                       Reject("A remaining Active row is stale or still needs an update. Queue or refresh it, or stage Skip and save again.");
            case DadDebugUiRules.CrewLaunchProfilesStepId:
                EnsureLaunchProfileDrafts();
                foreach (var draft in launchProfileDrafts.Values.ToList())
                {
                    var saved = plugin.Configuration.LaunchProfiles.FirstOrDefault(profile =>
                        string.Equals(profile.ProfileId, draft.ProfileId, StringComparison.OrdinalIgnoreCase));
                    if (saved == null || LaunchProfileGuideFieldsEqual(saved, draft))
                        continue;
                    var updateProfile = saved.Clone();
                    updateProfile.Enabled = draft.Enabled;
                    updateProfile.DryRun = draft.DryRun;
                    updateProfile.AccountKey = draft.AccountKey;
                    var ack = plugin.SchedulerService.UpdateLaunchProfile(new DadLaunchProfileUpdateRequest
                    {
                        ExpectedRevision = saved.Revision,
                        Profile = updateProfile,
                    });
                    plugin.PrintStatus(ack.Summary);
                    if (!ack.Accepted)
                    {
                        if (ack.Profile != null)
                            launchProfileDrafts[draft.ProfileId] = ack.Profile.Clone();
                        return Reject(FormatText(ack.Summary, "The launch profile changed elsewhere. Review it and save again."));
                    }
                    if (ack.Profile != null)
                        launchProfileDrafts[draft.ProfileId] = ack.Profile.Clone();
                }
                return true;
            default:
                var readiness = DadGuideReadiness.Build(plugin, DadGuideFlow.Crew);
                return readiness.Ready || Reject(readiness.NextAction);
        }
    }

    private bool CommitScheduleStep()
    {
        var snapshot = plugin.SchedulerService.GetScheduleSnapshot();
        var schedule = FindSchedule(snapshot);
        switch (stepIndex)
        {
            case 0:
                if (plugin.Configuration.PlannerGroups.Count == 0)
                    return Reject("Create at least one saved preset before building a schedule.");
                if (scheduleCreateNew)
                {
                    if (string.IsNullOrWhiteSpace(scheduleName))
                        return Reject("Enter a schedule name.");
                    if (snapshot.ActiveRun.IsActive)
                        return Reject("Schedule identity is locked while a schedule is active.");
                    schedule = plugin.SchedulerService.CreateSchedule(scheduleName);
                    scheduleId = schedule.ScheduleId;
                    scheduleName = schedule.DisplayName;
                    scheduleCadenceDraft = schedule.Cadence;
                    scheduleDraft = schedule.Clone();
                    scheduleCreateNew = false;
                    return true;
                }
                if (schedule == null)
                    return Reject("Select an existing schedule or choose Create a new schedule.");
                if (string.IsNullOrWhiteSpace(scheduleName))
                    return Reject("Enter a schedule name.");
                if (!string.Equals(schedule.DisplayName, scheduleName.Trim(), StringComparison.Ordinal))
                {
                    if (snapshot.ActiveRun.IsActive)
                        return Reject("Schedule identity is locked while a schedule is active.");
                    var renamedDraft = schedule.Clone();
                    renamedDraft.DisplayName = scheduleName;
                    schedule = plugin.SchedulerService.UpdateSchedule(renamedDraft);
                    if (schedule == null)
                        return Reject("The selected schedule could not be renamed. Refresh Schedules and try again.");
                    scheduleName = schedule.DisplayName;
                }
                scheduleDraft = schedule.Clone();
                scheduleCadenceDraft = schedule.Cadence;
                return true;
            case 1:
                if (snapshot.ActiveRun.IsActive)
                    return Reject("Ordered presets are locked while a schedule is active.");
                if (schedule == null || scheduleDraft == null || scheduleDraft.Entries.Count == 0)
                    return Reject("Add at least one saved preset.");
                var known = plugin.Configuration.PlannerGroups.Select(static group => group.GroupId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!scheduleDraft.Entries.All(entry => known.Contains(entry.GroupId)))
                    return Reject("One or more entries reference a missing preset. Replace or remove them.");
                var entriesUpdate = schedule.Clone();
                entriesUpdate.Entries = scheduleDraft.Entries.Select(static entry => entry.Clone()).ToList();
                scheduleDraft = plugin.SchedulerService.UpdateSchedule(entriesUpdate);
                return scheduleDraft != null || Reject("The selected schedule changed before its ordered presets could be saved.");
            case 2:
                if (schedule == null)
                    return Reject("No schedule is selected.");
                if (snapshot.ActiveRun.IsActive)
                    return Reject("Cadence is locked while a schedule is active.");
                var cadenceUpdate = schedule.Clone();
                cadenceUpdate.Cadence = scheduleCadenceDraft;
                scheduleDraft = plugin.SchedulerService.UpdateSchedule(cadenceUpdate);
                return scheduleDraft != null || Reject("The selected schedule changed before cadence could be saved.");
            case 3:
                if (snapshot.ActiveRun.IsActive)
                    return Reject("A schedule is already active. Wait for it or cancel it from Schedules.");
                if (schedule == null || schedule.Entries.Count == 0)
                    return Reject("The selected schedule is empty.");
                return true;
            case 4:
                if (schedule == null)
                    return Reject("No schedule is selected.");
                var dryRun = snapshot.RecentResults.FirstOrDefault(result =>
                    result.DryRun && string.Equals(result.ScheduleId, schedule.ScheduleId, StringComparison.OrdinalIgnoreCase));
                return dryRun?.Success == true || Reject(dryRun == null
                    ? "Run a dry-run and wait for it to finish."
                    : FormatText(dryRun.BlockedReason, dryRun.Summary));
            default:
                var progress = DadGuideReadiness.Build(plugin, DadGuideFlow.Schedule);
                return progress.Ready || Reject(progress.NextAction);
        }
    }

    private void InitializeDrafts()
    {
        var configuration = plugin.Configuration;
        var profile = plugin.ConfigManager.GetActiveConfig();
        basicsDraftInitialized = true;
        draftPluginEnabled = configuration.PluginEnabled;
        draftProfileEnabled = profile.Enabled;
        draftAccountId = configuration.ClientAccountId;
        connectionEditor.Reset(configuration);

        presetPlannerDraft = ClonePlannerOptions(plugin.PlannerOptions);
        var group = plugin.GetSelectedPlannerGroup();
        if (group == null && presetPlannerDraft.ActivityMode == DadPlannerActivityMode.Msq)
        {
            var dutySupport = plugin.PresetProviderService.GetPlannerLaneDefinition(DadPlannerActivityMode.DutySupport);
            presetPlannerDraft.RunFamily = dutySupport.RunFamily;
            presetPlannerDraft.ActivityMode = dutySupport.ActivityMode;
            presetPlannerDraft.TransportOwner = dutySupport.DefaultTransportOwner;
            presetPlannerDraft.QueueAuthority = dutySupport.DefaultQueueAuthority;
            presetPlannerDraft.DutyContentFinderConditionId = 0;
            presetPlannerDraft.DutyDisplayName = string.Empty;
            presetPlannerDraft.DutyExpectedPartySize = dutySupport.ExpectedPartySize;
            presetPlannerDraft.DutyUnsynced = false;
        }
        presetName = group?.DisplayName ?? $"{plugin.PresetProviderService.GetPlannerLaneDefinition(presetPlannerDraft.ActivityMode).DisplayName} Preset";
        presetCreateNew = group == null;
        presetDraftGroupId = group?.GroupId ?? string.Empty;
        presetCrewDraft = group == null ? null : ClonePlannerGroup(group);
        presetStopDraft = (group?.StopPolicy ?? presetPlannerDraft.StopPolicy).Clone();
        presetUseGlobalCompletionDefaults = group?.CompletionActions == null;
        presetCompletionDraft = (group?.CompletionActions ?? plugin.Configuration.CompletionActions).Clone();
        presetCompletionCommands = string.Join("\n", presetCompletionDraft.Commands);
        presetDutySearch = presetPlannerDraft.DutyDisplayName;

        crewOwnershipAssignments.Clear();
        crewStagedSkips.Clear();
        launchProfileDrafts.Clear();
        EnsureLaunchProfileDrafts();

        var scheduleSnapshot = plugin.SchedulerService.GetScheduleSnapshot();
        var schedule = scheduleSnapshot.Schedules.FirstOrDefault();
        scheduleId = schedule?.ScheduleId ?? string.Empty;
        scheduleName = schedule?.DisplayName ?? "Dad Schedule";
        scheduleCreateNew = schedule == null;
        scheduleDraft = schedule?.Clone();
        scheduleCadenceDraft = schedule?.Cadence ?? DadScheduleCadence.Manual;
        scheduleAddGroupId = plugin.Configuration.PlannerGroups.FirstOrDefault()?.GroupId ?? string.Empty;
        scheduleRepeatCount = DadScheduleRules.MinRepeatCount;
        scheduleStarterStatus = string.Empty;
    }

    private void EnsureBasicsDraft()
    {
        if (basicsDraftInitialized)
            return;
        draftPluginEnabled = plugin.Configuration.PluginEnabled;
        draftProfileEnabled = plugin.ConfigManager.GetActiveConfig().Enabled;
        draftAccountId = plugin.Configuration.ClientAccountId;
        basicsDraftInitialized = true;
    }

    private void DrawAccountSelector()
    {
        var accounts = plugin.ConfigManager.GetAllAccounts();
        var selected = accounts.FirstOrDefault(account => string.Equals(account.AccountId, draftAccountId, StringComparison.OrdinalIgnoreCase));
        ImGui.SetNextItemWidth(MathF.Min(480f, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("DAD account", selected == null ? FormatText(draftAccountId, "Select account") : $"{selected.AccountAlias} [{selected.AccountId}]"))
        {
            foreach (var account in accounts)
            {
                var isSelected = string.Equals(account.AccountId, draftAccountId, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{account.AccountAlias} [{account.AccountId}] | {account.Characters.Count} character(s)", isSelected))
                    draftAccountId = account.AccountId;
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        DrawStatusRow("Current runtime account", FormatText(plugin.ConfigManager.CurrentAccountId, "(none)"));
        ImGui.TextWrapped("Account selection is local to this DAD client. Use Crew after connection setup to resolve roster ownership across the full crew.");
    }

    private void ApplyLaneToDraft(DadPlannerLaneDefinition lane)
    {
        presetPlannerDraft.RunFamily = lane.RunFamily;
        presetPlannerDraft.ActivityMode = lane.ActivityMode;
        presetPlannerDraft.TransportOwner = lane.DefaultTransportOwner;
        presetPlannerDraft.QueueAuthority = lane.DefaultQueueAuthority;
        if (presetPlannerDraft.DutyExpectedPartySize <= 0)
            presetPlannerDraft.DutyExpectedPartySize = Math.Clamp(lane.ExpectedPartySize, 1, 48);
    }

    private void ChangeRosterAssignment(DadRosterCharacter row, DadAccountKey accountKey, string alias)
    {
        var resultJson = plugin.ChangeRosterAssignmentFromJson(DadIpcJson.Serialize(new DadRosterAssignmentChangeRequest
        {
            CharacterRef = DadRosterIdentity.From(row),
            AccountKey = accountKey,
            AccountAlias = alias,
            Reason = "Assigned from Build the Crew guide.",
        }));
        var catalog = DadIpcJson.Deserialize<DadAccountRosterCatalog>(resultJson);
        plugin.PrintStatus(catalog?.Summary ?? "Roster assignment updated.");
    }

    private void QueueRosterUpdate(IReadOnlyList<DadRosterCharacter> rows)
    {
        var resultJson = plugin.EnqueueRosterUpdateFromJson(DadIpcJson.Serialize(new DadRosterRefreshPlan
        {
            CharacterRefs = rows.Select(DadRosterIdentity.From).ToList(),
            IncludeHidden = true,
            IncludeIgnored = true,
        }));
        var queue = DadIpcJson.Deserialize<DadSchedulerQueueSnapshot>(resultJson);
        plugin.PrintStatus(queue?.Summary ?? "Roster updates enqueued.");
    }

    private bool DrawCrewDeleteButton(DadRosterCharacter row, string rowKey)
    {
        var supported = plugin.RosterCatalogService.HasLocalRosterCopy(row);
        var modifierHeld = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
        ImGui.BeginDisabled(!supported || !modifierHeld);
        var clicked = ImGui.SmallButton($"Delete##dad-guide-stale-delete-{rowKey}");
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        ImGui.EndDisabled();

        if (hovered)
        {
            ImGui.SetTooltip(!supported
                ? "DAD has no removable local cached copy for this row. Choose Skip or correct the authoritative source data."
                : modifierHeld
                    ? "Delete DAD's local cached copy now. XADB snapshots and remote authoritative data remain untouched."
                    : "Hold Ctrl+Shift to delete only DAD's local cached copy. XADB snapshots and remote authoritative data remain untouched.");
        }

        return clicked;
    }

    private void ForgetCrewRosterCopy(DadRosterCharacter row)
    {
        var rowKey = DadRosterIdentity.BuildKey(row);
        var changed = plugin.RosterCatalogService.ForgetLocalRosterCopy(row);
        plugin.RosterCatalogService.RefreshCatalog(
            plugin.CharacterIntelligenceService.CurrentPool,
            new DadRosterRefreshPlan
            {
                IncludeHidden = true,
                IncludeIgnored = true,
                StaleAfterHours = plugin.Configuration.RosterCatalog.StaleAfterHours,
            });
        crewStagedSkips.Remove(rowKey);

        var account = row.AccountKey.IsEmpty ? "unassigned account" : FormatText(row.AccountAlias, row.AccountKey.Value);
        plugin.PrintStatus(changed
            ? $"Deleted DAD's local cached copy for {plugin.KrangleService.FormatCharacterKey(row.CharacterKey.Value)} on {account}. XADB snapshots and remote authoritative data were untouched."
            : $"No supported local DAD cache copy was found for {plugin.KrangleService.FormatCharacterKey(row.CharacterKey.Value)}.");
    }

    private void EnsureLaunchProfileDrafts()
    {
        var savedIds = plugin.Configuration.LaunchProfiles
            .Select(static profile => profile.ProfileId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var removedId in launchProfileDrafts.Keys.Where(id => !savedIds.Contains(id)).ToList())
            launchProfileDrafts.Remove(removedId);
        foreach (var profile in plugin.Configuration.LaunchProfiles)
        {
            if (!launchProfileDrafts.ContainsKey(profile.ProfileId))
                launchProfileDrafts[profile.ProfileId] = profile.Clone();
        }
    }

    private static bool LaunchProfileGuideFieldsEqual(DadLaunchProfile saved, DadLaunchProfile draft)
        => saved.Enabled == draft.Enabled &&
           saved.DryRun == draft.DryRun &&
           string.Equals(saved.AccountKey.Value, draft.AccountKey.Value, StringComparison.OrdinalIgnoreCase);

    private bool DrawSchedulePresetCombo(ref string groupId, IReadOnlyList<DadPlannerGroup> groups, string suffix)
    {
        var currentGroupId = groupId;
        var selected = groups.FirstOrDefault(group => string.Equals(group.GroupId, currentGroupId, StringComparison.OrdinalIgnoreCase));
        var changed = false;
        ImGui.SetNextItemWidth(MathF.Min(360f, ImGui.GetContentRegionAvail().X));
        if (!ImGui.BeginCombo($"Preset##dad-guide-schedule-preset-{suffix}", selected?.DisplayName ?? "Select preset"))
            return false;
        foreach (var group in groups)
        {
            var isSelected = string.Equals(group.GroupId, currentGroupId, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable($"{group.DisplayName}##dad-guide-{suffix}-{group.GroupId}", isSelected))
            {
                groupId = group.GroupId;
                currentGroupId = group.GroupId;
                changed = true;
            }
            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
        return changed;
    }

    private void EnsureScheduleSelection(DadScheduleSnapshot snapshot)
    {
        var selected = FindSchedule(snapshot);
        if (selected != null)
            return;
        selected = snapshot.Schedules.FirstOrDefault();
        scheduleId = selected?.ScheduleId ?? string.Empty;
        if (selected != null && !scheduleCreateNew)
        {
            scheduleName = selected.DisplayName;
            scheduleCadenceDraft = selected.Cadence;
            scheduleDraft = selected.Clone();
        }
    }

    private DadScheduleDefinition? FindSchedule(DadScheduleSnapshot snapshot)
        => snapshot.Schedules.FirstOrDefault(candidate =>
            string.Equals(candidate.ScheduleId, scheduleId, StringComparison.OrdinalIgnoreCase));

    private void DrawExpertButton(DadGuideFlow currentFlow)
    {
        if (!DadUi.Button("Open expert editor"))
            return;
        switch (currentFlow)
        {
            case DadGuideFlow.Coordinator:
            case DadGuideFlow.Client:
                plugin.OpenConfigUi();
                break;
            case DadGuideFlow.FirstPreset:
                plugin.OpenMainTab(DadMainWindowTab.Presets, DadPresetsWindowTab.Planner);
                break;
            case DadGuideFlow.Crew:
                plugin.OpenMainTab(DadMainWindowTab.Crew);
                break;
            case DadGuideFlow.Schedule:
                plugin.OpenMainTab(DadMainWindowTab.Presets, DadPresetsWindowTab.Scheduler);
                break;
        }
    }

    private bool Reject(string message)
    {
        validationMessage = FormatText(message, "Resolve this step's first blocker before continuing.");
        return false;
    }

    private static DadPresetPlannerOptions ClonePlannerOptions(DadPresetPlannerOptions source)
        => DadIpcJson.Deserialize<DadPresetPlannerOptions>(DadIpcJson.Serialize(source)) ?? new DadPresetPlannerOptions();

    private static DadPlannerGroup ClonePlannerGroup(DadPlannerGroup source)
    {
        var clone = DadSchedulerGroupCloneRules.CloneWithSlots(source, source.Slots ?? []);
        clone.InviteAuthority = source.InviteAuthority;
        return clone;
    }

    private static DadPlannerGroupSlot CloneSlot(DadPlannerGroupSlot source)
        => DadSchedulerGroupCloneRules.CloneSlot(source);

    private bool IsRemoteRosterRow(DadRosterCharacter row)
        => !string.IsNullOrWhiteSpace(row.SourceClientInstanceId) &&
           !string.Equals(row.SourceClientInstanceId, plugin.PresenceService.ClientInstanceId, StringComparison.OrdinalIgnoreCase);

    private static string BuildConnectionVerificationBlocker(
        bool coordinator,
        DadPeerTransportSnapshot transport,
        int participantCount)
    {
        if (!string.IsNullOrWhiteSpace(transport.LastAuthOrProtocolError))
            return transport.LastAuthOrProtocolError;
        if (coordinator)
        {
            if (string.IsNullOrWhiteSpace(transport.ListenerEndpoint))
                return "The listener is not ready. Verify DAD is enabled and the applied host/port is available.";
            if (participantCount <= 0)
                return "No participant is visible. Connect a Client or refresh the local/connected roster.";
            return "Wait for the listener readiness state to update.";
        }
        return "Authenticated authority is not routable. Verify the Coordinator is online and host, port, and secret match exactly.";
    }

    private static string FlowTitle(DadGuideFlow target)
        => target switch
        {
            DadGuideFlow.Coordinator => "SET UP A COORDINATOR",
            DadGuideFlow.Client => "CONNECT A CLIENT",
            DadGuideFlow.FirstPreset => "CREATE A PRESET",
            DadGuideFlow.Crew => "BUILD THE CREW",
            DadGuideFlow.Schedule => "BUILD A SCHEDULE",
            _ => "DAD GUIDE",
        };

    private static string FlowSummary(DadGuideFlow target)
        => target switch
        {
            DadGuideFlow.Coordinator => "Enable the one client that owns plans, schedules, and crew dispatch.",
            DadGuideFlow.Client => "Authenticate a crew client to the Coordinator and verify authority.",
            DadGuideFlow.FirstPreset => "Choose content, assign every inline character field, and validate without running.",
            DadGuideFlow.Crew => "Refresh ownership and fix stale or unassigned roster rows.",
            DadGuideFlow.Schedule => "Order saved presets, set repeats/cadence, and complete a dry-run.",
            _ => string.Empty,
        };

    private static bool ValidEndpoint(string host, int port)
        => !string.IsNullOrWhiteSpace(host) && port is > 0 and <= 65535;

    private static void DrawStatusRow(string label, string value)
        => DadUi.KeyValue(label, value, 170f);

    private static string FormatText(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string FormatTime(DateTime? value)
        => value?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "never";

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
}
