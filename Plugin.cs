using System.Diagnostics;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using dad.Models;
using dad.Services;
using dad.Windows;

namespace dad;

public sealed class Plugin : IDalamudPlugin
{
    private static readonly TimeSpan RemoteAuthorityStatusRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RemoteAuthorityStatusStaleThreshold = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan EndpointApplyAuthorityRefreshSuppression = TimeSpan.FromSeconds(1.5);

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }
    public ConfigManager ConfigManager { get; }
    public DadExternalPluginCapabilityService ExternalPluginCapabilityService { get; }
    public DadXadbClient XadbClient { get; }
    public DadPresenceService PresenceService { get; }
    public DadClaimService ClaimService { get; }
    public DadTransportService TransportService { get; }
    public DadCharacterIntelligenceService CharacterIntelligenceService { get; }
    public DadKrangleService KrangleService { get; }
    public DadPresetPlannerOptions PlannerOptions => Configuration.PlannerOptions;
    public IReadOnlyList<DadPlannerGroup> PlannerGroups => Configuration.PlannerGroups;
    public DadPresetProviderService PresetProviderService { get; }
    public DadModuleRegistry ModuleRegistry { get; }
    public DadPlannerService PlannerService { get; }
    public DadPartyAssemblyService PartyAssemblyService { get; }
    public DadDutyQueueService DutyQueueService { get; }
    public DadLocalDutyQueueService LocalDutyQueueService { get; }
    public DadNpcDutyQueueService NpcDutyQueueService { get; }
    public DadDutySupportAdsService DutySupportAdsService { get; }
    public DadCombatRotationService CombatRotationService { get; }
    public DadQueueExecutionService QueueExecutionService { get; }
    public DadCoordinatorService RunCoordinatorService { get; }
    public WindowSystem WindowSystem { get; } = new(PluginInfo.InternalName);

    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly DadIpcService dadIpcService;
    private IDtrBarEntry? dtrEntry;
    private DadRunResult? cachedAuthorityRun;
    private string cachedAuthorityEndpoint = string.Empty;
    private DateTime nextAuthorityStatusRefreshUtc = DateTime.MinValue;
    private DateTime suppressRemoteAuthorityRefreshUntilUtc = DateTime.MinValue;
    private DateTime? lastAuthorityRefreshSucceededUtc;
    private string lastLoggedAuthorityEndpointKey = string.Empty;
    private string lastLoggedAuthorityRefreshKey = string.Empty;
    private string lastLoggedAuthorityViewKey = string.Empty;
    private string cachedPlannerPreviewSignature = string.Empty;
    private string cachedPlannerPreviewRequestId = string.Empty;
    private DateTime cachedPlannerPreviewRequestedAtUtc = DateTime.MinValue;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        ConfigManager = new ConfigManager(PluginInterface, Log);
        ExternalPluginCapabilityService = new DadExternalPluginCapabilityService();
        XadbClient = new DadXadbClient(PluginInterface, Log);
        PresenceService = new DadPresenceService(Configuration, ConfigManager, Log);
        ClaimService = new DadClaimService();
        TransportService = new DadTransportService(Configuration, PresenceService, ClaimService, Log);
        CharacterIntelligenceService = new DadCharacterIntelligenceService(ConfigManager, XadbClient, TransportService, Log);
        KrangleService = new DadKrangleService(Configuration);
        ModuleRegistry = new DadModuleRegistry();
        PresetProviderService = new DadPresetProviderService(ModuleRegistry);
        PlannerService = new DadPlannerService(PresetProviderService, ModuleRegistry);
        PartyAssemblyService = new DadPartyAssemblyService();
        DutyQueueService = new DadDutyQueueService(ExternalPluginCapabilityService);
        DutySupportAdsService = new DadDutySupportAdsService(Log);
        LocalDutyQueueService = new DadLocalDutyQueueService(Log);
        NpcDutyQueueService = new DadNpcDutyQueueService(Log);
        CombatRotationService = new DadCombatRotationService(Configuration, Log);
        QueueExecutionService = new DadQueueExecutionService(
            ModuleRegistry,
            DutyQueueService,
            LocalDutyQueueService,
            NpcDutyQueueService,
            DutySupportAdsService,
            CombatRotationService);
        RunCoordinatorService = new DadCoordinatorService(
            Configuration,
            ConfigManager,
            CharacterIntelligenceService,
            PresenceService,
            TransportService,
            ClaimService,
            PartyAssemblyService,
            QueueExecutionService,
            PlannerService,
            Log);
        TransportService.ConfigureAuthorityHandlers(
            () => RunCoordinatorService.GetLocalResult(),
            request => RunCoordinatorService.StartTasks(request),
            _ => RunCoordinatorService.CancelActiveRun());

        if (!string.IsNullOrWhiteSpace(Configuration.LastAccountId))
            ConfigManager.CurrentAccountId = Configuration.LastAccountId;

        ClientState.Login += OnLogin;

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);

        var plannerLaneCount = PresetProviderService.GetPlannerLaneDefinitions().Count();
        var buildVersion = GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        Log.Information("[dad] Planner lane panel enabled with {LaneCount} planner lanes. Build {BuildVersion}.", plannerLaneCount, buildVersion);

        CommandManager.AddHandler(PluginInfo.Command, new CommandInfo(OnCommand)
        {
            HelpMessage = $"Open {PluginInfo.DisplayName}. Use {PluginInfo.Command} config, {PluginInfo.Command} debug, {PluginInfo.Command} on, {PluginInfo.Command} off, {PluginInfo.Command} krangle, {PluginInfo.Command} ws, {PluginInfo.Command} j, {PluginInfo.Command} status, {PluginInfo.Command} refresh, {PluginInfo.Command} save, {PluginInfo.Command} peers, {PluginInfo.Command} run local, {PluginInfo.Command} run server, {PluginInfo.Command} run msq, {PluginInfo.Command} run commend, {PluginInfo.Command} run planner, or {PluginInfo.Command} cancel. Dad now exposes Server Dad authority, Client Dad workers, sticky local-only mode, krangled operator names, and account-aware readiness/lease status.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;

        SetupDtrBar();
        UpdateDtrBar();

        dadIpcService = new DadIpcService(
            PluginInterface,
            this,
            RunCoordinatorService,
            CharacterIntelligenceService,
            PresenceService,
            TransportService,
            ModuleRegistry,
            PresetProviderService,
            Log);

        Log.Information("[dad] Plugin loaded.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ClientState.Login -= OnLogin;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        CommandManager.RemoveHandler(PluginInfo.Command);
        WindowSystem.RemoveAllWindows();
        dadIpcService.Dispose();
        LocalDutyQueueService.Dispose();
        NpcDutyQueueService.Dispose();
        TransportService.Dispose();
        dtrEntry?.Remove();
    }

    public void ToggleMainUi() => mainWindow.Toggle();

    public void ToggleConfigUi() => configWindow.Toggle();

    public void PrintStatus(string message) => ChatGui.Print($"[{PluginInfo.DisplayName}] {message}");

    public void ApplyEndpointConfiguration(bool bindChanged, bool authorityTargetChanged)
    {
        if (!bindChanged && !authorityTargetChanged)
            return;

        if (bindChanged)
            TransportService.RestartListener();

        if (bindChanged || authorityTargetChanged)
            ResetAuthorityCache(clearFreshness: false);

        suppressRemoteAuthorityRefreshUntilUtc = DateTime.UtcNow + EndpointApplyAuthorityRefreshSuppression;
    }

    public DadActivityPreset BuildPlannerPreview()
        => PresetProviderService.BuildPlannerPreview(CharacterIntelligenceService.CurrentPool, PlannerOptions, GetSelectedPlannerGroup());

    public string BuildPlannerSummary()
        => BuildPlannerPreview().PlannerSummary;

    public DadPlannerRunRequestPreview BuildPlannerRunRequestPreview()
    {
        var pool = CharacterIntelligenceService.CurrentPool;
        var selectedGroup = GetSelectedPlannerGroup();
        var plannerPreview = PresetProviderService.BuildPlannerPreview(pool, PlannerOptions, selectedGroup);
        var signature = BuildPlannerPreviewSignature(PlannerOptions, plannerPreview);
        var identity = ResolvePlannerPreviewIdentity(signature);
        var requestPreview = PresetProviderService.BuildPlannerRunRequestPreview(
            pool,
            PlannerOptions,
            identity.RequestId,
            identity.RequestedAtUtc,
            plannerPreview,
            selectedGroup);
        return ApplyPlannerRuntimeTruth(requestPreview, pool);
    }

    public DadPlannerRunRequestPreview BuildPlannerRunRequestPreview(
        DadPresetPlannerOptions options,
        DadActivityPreset? plannerPreviewOverride = null,
        DadPlannerGroup? selectedGroup = null)
    {
        var pool = CharacterIntelligenceService.CurrentPool;
        var plannerPreview = plannerPreviewOverride ?? PresetProviderService.BuildPlannerPreview(pool, options, selectedGroup);
        var requestPreview = PresetProviderService.BuildPlannerRunRequestPreview(
            pool,
            options,
            plannerPreviewOverride: plannerPreview,
            selectedGroup: selectedGroup);
        return ApplyPlannerRuntimeTruth(requestPreview, pool);
    }

    public string BuildPlannerRequestJson()
    {
        var requestPreview = BuildPlannerRunRequestPreview();
        return requestPreview.RequestJson;
    }

    public void SavePlannerOptions()
        => Configuration.Save();

    public DadPlannerGroup? GetSelectedPlannerGroup()
        => ResolvePlannerGroup(PlannerOptions.SelectedPlannerGroupId);

    public DadPlannerGroup? ResolvePlannerGroup(string groupIdOrName)
    {
        var key = groupIdOrName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return null;

        return Configuration.PlannerGroups.FirstOrDefault(group =>
                   string.Equals(group.GroupId, key, StringComparison.OrdinalIgnoreCase))
               ?? Configuration.PlannerGroups.FirstOrDefault(group =>
                   string.Equals(group.DisplayName, key, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryResolvePlannerGroupForIpc(
        string groupIdOrName,
        out DadPlannerGroup? group,
        out string rejectionReason)
    {
        group = null;
        var key = groupIdOrName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            rejectionReason = "Planner group id or name is required.";
            return false;
        }

        var idMatches = Configuration.PlannerGroups
            .Where(candidate => string.Equals(candidate.GroupId, key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (idMatches.Count == 1)
        {
            group = idMatches[0];
            rejectionReason = string.Empty;
            return true;
        }

        if (idMatches.Count > 1)
        {
            rejectionReason = $"Planner group id '{key}' matched multiple groups; repair saved Dad planner groups before starting.";
            return false;
        }

        var nameMatches = Configuration.PlannerGroups
            .Where(candidate => string.Equals(candidate.DisplayName, key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (nameMatches.Count == 1)
        {
            group = nameMatches[0];
            rejectionReason = string.Empty;
            return true;
        }

        rejectionReason = nameMatches.Count > 1
            ? $"Planner group name '{key}' matches {nameMatches.Count} groups; use the stable GroupId instead."
            : $"Planner group '{key}' was not found.";
        return false;
    }

    public void ClearPlannerGroupSelection()
    {
        PlannerOptions.SelectedPlannerGroupId = string.Empty;
        Configuration.Save();
        InvalidatePlannerPreviewIdentity();
    }

    public bool SelectPlannerGroup(string groupIdOrName)
    {
        var group = ResolvePlannerGroup(groupIdOrName);
        if (group == null)
        {
            ClearPlannerGroupSelection();
            return false;
        }

        ApplyPlannerGroupDefaults(group, PlannerOptions);
        Configuration.Save();
        InvalidatePlannerPreviewIdentity();
        return true;
    }

    public DadPlannerGroup SaveCurrentPlannerAsGroup(string displayName)
    {
        var group = BuildPlannerGroupFromCurrentPlanner(displayName);
        Configuration.PlannerGroups.Add(group);
        PlannerOptions.SelectedPlannerGroupId = group.GroupId;
        Configuration.Save();
        InvalidatePlannerPreviewIdentity();
        return group;
    }

    public DadPlannerGroup? DuplicateSelectedPlannerGroup(string displayName)
    {
        var selected = GetSelectedPlannerGroup();
        if (selected == null)
            return null;

        var duplicate = ClonePlannerGroup(selected);
        duplicate.GroupId = Guid.NewGuid().ToString("N");
        duplicate.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? $"{selected.DisplayName} Copy"
            : displayName.Trim();
        duplicate.CreatedAtUtc = DateTime.UtcNow;
        duplicate.UpdatedAtUtc = duplicate.CreatedAtUtc;
        Configuration.PlannerGroups.Add(duplicate);
        PlannerOptions.SelectedPlannerGroupId = duplicate.GroupId;
        Configuration.Save();
        InvalidatePlannerPreviewIdentity();
        return duplicate;
    }

    public bool RenameSelectedPlannerGroup(string displayName)
    {
        var selected = GetSelectedPlannerGroup();
        if (selected == null || string.IsNullOrWhiteSpace(displayName))
            return false;

        selected.DisplayName = displayName.Trim();
        selected.UpdatedAtUtc = DateTime.UtcNow;
        Configuration.Save();
        InvalidatePlannerPreviewIdentity();
        return true;
    }

    public bool DeleteSelectedPlannerGroup()
    {
        var selected = GetSelectedPlannerGroup();
        if (selected == null)
            return false;

        Configuration.PlannerGroups.Remove(selected);
        PlannerOptions.SelectedPlannerGroupId = string.Empty;
        Configuration.Save();
        InvalidatePlannerPreviewIdentity();
        return true;
    }

    public void TouchPlannerGroup(DadPlannerGroup group)
    {
        group.UpdatedAtUtc = DateTime.UtcNow;
        Configuration.Save();
        InvalidatePlannerPreviewIdentity();
    }

    public void ReplaceSelectedPlannerGroupSlotsFromCurrentPreview()
    {
        var selected = GetSelectedPlannerGroup();
        if (selected == null)
            return;

        var preview = BuildPlannerPreview();
        selected.Slots = BuildPlannerGroupSlotsFromPreview(preview);
        selected.UpdatedAtUtc = DateTime.UtcNow;
        Configuration.Save();
        InvalidatePlannerPreviewIdentity();
    }

    public IReadOnlyList<DadPlannerGroupSummary> GetPlannerGroupSummaries()
        => Configuration.PlannerGroups
            .Select(BuildPlannerGroupSummary)
            .OrderBy(static summary => summary.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public string GetPlannerGroupsJson()
        => DadIpcJson.Serialize(new
        {
            groups = Configuration.PlannerGroups,
            summaries = GetPlannerGroupSummaries(),
        });

    public string GetPlannerGroupPreviewJson(string groupIdOrName)
        => DadIpcJson.Serialize(BuildPlannerGroupRunRequestPreview(groupIdOrName, null));

    public string StartPlannerGroupFromJson(string json)
    {
        var startRequest = DadIpcJson.Deserialize<DadPlannerGroupStartRequest>(json);
        if (startRequest == null)
        {
            var fallbackId = (json ?? string.Empty).Trim().Trim('"');
            startRequest = new DadPlannerGroupStartRequest { GroupId = fallbackId };
        }

        var preview = BuildPlannerGroupRunRequestPreview(startRequest.GroupId, startRequest);
        if (preview.Request != null && !string.IsNullOrWhiteSpace(startRequest.RequestedBy))
            preview.Request.RequestedBy = startRequest.RequestedBy.Trim();

        if (preview.Request == null)
            return DadIpcJson.Serialize(DadRunResult.Rejected(null, preview.StatusSummary));

        if (startRequest.DryRun)
        {
            var dryRunStatus = preview.CanStart ? DadRunStatus.Idle : DadRunStatus.Rejected;
            var dryRunSummary = preview.CanStart
                ? $"Planner group dry run ready: {preview.StatusSummary}"
                : $"Planner group dry run blocked: {preview.BlockedReason}";
            return DadIpcJson.Serialize(DadRunResult.FromRequest(preview.Request, dryRunStatus, dryRunSummary));
        }

        if (!preview.CanStart)
            return DadIpcJson.Serialize(DadRunResult.Rejected(preview.Request, preview.BlockedReason));

        return DadIpcJson.Serialize(RunCoordinatorService.StartTasks(preview.Request));
    }

    private DadPlannerRunRequestPreview BuildPlannerGroupRunRequestPreview(
        string groupIdOrName,
        DadPlannerGroupStartRequest? startRequest)
    {
        if (!TryResolvePlannerGroupForIpc(groupIdOrName, out var group, out var rejectionReason) || group == null)
        {
            return new DadPlannerRunRequestPreview
            {
                CanStart = false,
                StatusSummary = rejectionReason,
                BlockedReason = rejectionReason,
            };
        }

        var options = BuildPlannerOptionsForGroup(group, startRequest);
        var pool = CharacterIntelligenceService.CurrentPool;
        var preview = PresetProviderService.BuildPlannerPreview(pool, options, group);
        return ApplyPlannerRuntimeTruth(PresetProviderService.BuildPlannerRunRequestPreview(
            pool,
            options,
            plannerPreviewOverride: preview,
            selectedGroup: group), pool);
    }

    private DadPlannerGroup BuildPlannerGroupFromCurrentPlanner(string displayName)
    {
        var preview = BuildPlannerPreview();
        return new DadPlannerGroup
        {
            GroupId = Guid.NewGuid().ToString("N"),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? $"{preview.LaneDefinition.DisplayName} Group" : displayName.Trim(),
            ActivityMode = PlannerOptions.ActivityMode,
            OperatorMode = PlannerOptions.OperatorMode,
            ConnectedOnly = PlannerOptions.ConnectedOnly,
            SameDatacenterOnly = PlannerOptions.SameDatacenterOnly,
            AllowStaleForPlanning = PlannerOptions.AllowStaleForPlanning,
            TransportOwner = PlannerOptions.TransportOwner,
            QueueAuthority = PlannerOptions.QueueAuthority,
            InviteAuthority = PlannerOptions.InviteAuthority,
            DutyContentFinderConditionId = PlannerOptions.DutyContentFinderConditionId,
            DutyDisplayName = PlannerOptions.DutyDisplayName,
            DutyUnsynced = PlannerOptions.DutyUnsynced,
            DutyExpectedPartySize = PlannerOptions.DutyExpectedPartySize,
            MogtomePreset = PlannerOptions.MogtomePreset,
            MogtomeDutyPolicy = PlannerOptions.MogtomeDutyPolicy,
            Slots = BuildPlannerGroupSlotsFromPreview(preview),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
    }

    private List<DadPlannerGroupSlot> BuildPlannerGroupSlotsFromPreview(DadActivityPreset preview)
    {
        return preview.SelectedCharacters.Select(slot =>
        {
            var character = preview.AvailableCharacters.FirstOrDefault(candidate =>
                string.Equals(candidate.CharacterKey, slot.CharacterKey, StringComparison.OrdinalIgnoreCase));
            var accountKey = !slot.RequiredAccountKey.IsEmpty
                ? slot.RequiredAccountKey
                : character == null
                    ? new DadAccountKey(string.Empty)
                    : ResolvePlannerAccountKey(character);
            return new DadPlannerGroupSlot
            {
                SlotId = slot.SlotId,
                RequiredRole = slot.RequiredRole,
                RequiredAccountKey = accountKey,
                RequiredCharacterKey = string.IsNullOrWhiteSpace(slot.CharacterKey)
                    ? new DadCharacterKey(string.Empty)
                    : new DadCharacterKey(slot.CharacterKey),
                AllowSubstitution = slot.AllowSubstitution,
            };
        }).ToList();
    }

    private DadPlannerGroupSummary BuildPlannerGroupSummary(DadPlannerGroup group)
    {
        var lane = PresetProviderService.GetPlannerLaneDefinition(group.ActivityMode);
        var requiredAccounts = group.Slots
            .Select(static slot => slot.RequiredAccountKey.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var requiredCharacters = group.Slots
            .Select(static slot => slot.RequiredCharacterKey.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return new DadPlannerGroupSummary
        {
            GroupId = group.GroupId,
            DisplayName = group.DisplayName,
            ActivityMode = group.ActivityMode,
            Lane = lane.DisplayName,
            SlotCount = group.Slots.Count,
            RequiredAccountCount = requiredAccounts,
            RequiredCharacterCount = requiredCharacters,
            Summary = $"{lane.DisplayName} | {group.Slots.Count} slot(s) | accounts {requiredAccounts} | characters {requiredCharacters}",
        };
    }

    private DadPresetPlannerOptions BuildPlannerOptionsForGroup(DadPlannerGroup group, DadPlannerGroupStartRequest? startRequest)
    {
        var activityMode = ResolvePlannerGroupLane(group.ActivityMode, startRequest?.Lane);
        return new DadPresetPlannerOptions
        {
            PresetName = group.DisplayName,
            SelectedPlannerGroupId = group.GroupId,
            ActivityMode = activityMode,
            ActivityName = PresetProviderService.GetPlannerLaneDefinition(activityMode).DisplayName,
            OperatorMode = group.OperatorMode,
            ConnectedOnly = group.ConnectedOnly,
            SameDatacenterOnly = group.SameDatacenterOnly,
            AllowStaleForPlanning = group.AllowStaleForPlanning,
            TransportOwner = group.TransportOwner,
            QueueAuthority = group.QueueAuthority,
            InviteAuthority = group.InviteAuthority,
            DutyContentFinderConditionId = startRequest?.DutyContentFinderConditionId ?? group.DutyContentFinderConditionId,
            DutyDisplayName = group.DutyDisplayName,
            DutyUnsynced = group.DutyUnsynced,
            DutyExpectedPartySize = group.DutyExpectedPartySize,
            MogtomePreset = group.MogtomePreset,
            MogtomeDutyPolicy = group.MogtomeDutyPolicy,
            IncludedAccountKeys = group.Slots
                .Select(static slot => slot.RequiredAccountKey)
                .Where(static key => !key.IsEmpty)
                .DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private DadPlannerActivityMode ResolvePlannerGroupLane(DadPlannerActivityMode fallback, string? lane)
    {
        if (string.IsNullOrWhiteSpace(lane))
            return fallback;

        var trimmed = lane.Trim();
        if (Enum.TryParse<DadPlannerActivityMode>(trimmed, ignoreCase: true, out var parsed))
            return parsed;

        return PresetProviderService.GetPlannerLaneDefinitions()
            .FirstOrDefault(definition =>
                string.Equals(definition.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase))
            ?.ActivityMode ?? fallback;
    }

    private static DadPlannerGroup ClonePlannerGroup(DadPlannerGroup source)
        => new()
        {
            GroupId = source.GroupId,
            DisplayName = source.DisplayName,
            ActivityMode = source.ActivityMode,
            OperatorMode = source.OperatorMode,
            ConnectedOnly = source.ConnectedOnly,
            SameDatacenterOnly = source.SameDatacenterOnly,
            AllowStaleForPlanning = source.AllowStaleForPlanning,
            TransportOwner = source.TransportOwner,
            QueueAuthority = source.QueueAuthority,
            InviteAuthority = source.InviteAuthority,
            DutyContentFinderConditionId = source.DutyContentFinderConditionId,
            DutyDisplayName = source.DutyDisplayName,
            DutyUnsynced = source.DutyUnsynced,
            DutyExpectedPartySize = source.DutyExpectedPartySize,
            MogtomePreset = source.MogtomePreset,
            MogtomeDutyPolicy = source.MogtomeDutyPolicy,
            Slots = source.Slots.Select(static slot => new DadPlannerGroupSlot
            {
                SlotId = slot.SlotId,
                RequiredRole = slot.RequiredRole,
                RequiredAccountKey = slot.RequiredAccountKey,
                RequiredCharacterKey = slot.RequiredCharacterKey,
                AllowSubstitution = slot.AllowSubstitution,
            }).ToList(),
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
        };

    private static void ApplyPlannerGroupDefaults(DadPlannerGroup group, DadPresetPlannerOptions options)
    {
        options.SelectedPlannerGroupId = group.GroupId;
        options.PresetName = group.DisplayName;
        options.ActivityMode = group.ActivityMode;
        options.OperatorMode = group.OperatorMode;
        options.ConnectedOnly = group.ConnectedOnly;
        options.SameDatacenterOnly = group.SameDatacenterOnly;
        options.AllowStaleForPlanning = group.AllowStaleForPlanning;
        options.TransportOwner = group.TransportOwner;
        options.QueueAuthority = group.QueueAuthority;
        options.InviteAuthority = group.InviteAuthority;
        options.DutyContentFinderConditionId = group.DutyContentFinderConditionId;
        options.DutyDisplayName = group.DutyDisplayName;
        options.DutyUnsynced = group.DutyUnsynced;
        options.DutyExpectedPartySize = group.DutyExpectedPartySize;
        options.MogtomePreset = group.MogtomePreset;
        options.MogtomeDutyPolicy = group.MogtomeDutyPolicy;
        options.IncludedAccountKeys = group.Slots
            .Select(static slot => slot.RequiredAccountKey)
            .Where(static key => !key.IsEmpty)
            .DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DadAccountKey ResolvePlannerAccountKey(DadAcquiredCharacter character)
        => !string.IsNullOrWhiteSpace(character.AccountId)
            ? new DadAccountKey(character.AccountId)
            : !string.IsNullOrWhiteSpace(character.AccountAlias)
                ? new DadAccountKey(character.AccountAlias)
                : new DadAccountKey(string.Empty);

    public string ToggleKrangleOperatorNames()
    {
        var status = KrangleService.Toggle(CharacterIntelligenceService.CurrentPool);
        PrintStatus(status);
        return status;
    }

    public DadRunResult StartDemoRunFromShell()
        => StartLocalDemoRunFromShell();

    public DadRunResult StartLocalDemoRunFromShell()
        => StartDemoRunFromShell("Local demo", BuildLocalSastashaDemoRequest());

    public DadRunResult StartServerDemoRunFromShell()
        => StartDemoRunFromShell("Server demo", BuildServerSastashaDemoRequest());

    public DadRunResult StartDailyMsqDemoRunFromShell()
        => StartDemoRunFromShell("Daily MSQ demo", BuildDailyMsqDemoRequest());

    public DadRunResult StartCommendationDemoRunFromShell()
        => StartDemoRunFromShell("Commendation demo", BuildCommendationDemoRequest());

    public DadRunResult StartPlannerRunFromShell()
    {
        var requestPreview = BuildPlannerRunRequestPreview();
        if (!requestPreview.CanStart || requestPreview.Request == null)
        {
            var result = DadRunResult.Rejected(null, requestPreview.StatusSummary);
            PrintStatus(result.Summary);
            return result;
        }

        var startResult = StartDemoRunFromShell("Planner run", requestPreview.Request);
        if (startResult.Status != DadRunStatus.Rejected)
            InvalidatePlannerPreviewIdentity();

        return startResult;
    }

    private (string RequestId, DateTime RequestedAtUtc) ResolvePlannerPreviewIdentity(string signature)
    {
        if (!string.Equals(cachedPlannerPreviewSignature, signature, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(cachedPlannerPreviewRequestId))
        {
            cachedPlannerPreviewSignature = signature;
            cachedPlannerPreviewRequestId = Guid.NewGuid().ToString("N");
            cachedPlannerPreviewRequestedAtUtc = DateTime.UtcNow;
        }

        return (cachedPlannerPreviewRequestId, cachedPlannerPreviewRequestedAtUtc);
    }

    private void InvalidatePlannerPreviewIdentity()
    {
        cachedPlannerPreviewSignature = string.Empty;
        cachedPlannerPreviewRequestId = string.Empty;
        cachedPlannerPreviewRequestedAtUtc = DateTime.MinValue;
    }

    private DadPlannerRunRequestPreview ApplyPlannerRuntimeTruth(DadPlannerRunRequestPreview requestPreview, DadCharacterPool pool)
    {
        if (requestPreview.Request == null)
        {
            RefreshPlannerContractPreview(requestPreview);
            return requestPreview;
        }

        var previewOnly = string.Equals(requestPreview.Request.RequestedBy, "planner-preview", StringComparison.OrdinalIgnoreCase);
        if (!previewOnly)
        {
            var plan = PlannerService.BuildPlan(requestPreview.Request, pool, out var rejectionReason);
            if (plan == null)
            {
                MergePlannerPreviewBlocker(requestPreview, rejectionReason);
            }
            else
            {
                var runtimeStatus = QueueExecutionService.PreviewModuleStart(plan);
                MergePlannerRuntimeStatus(requestPreview, runtimeStatus);
            }
        }

        RefreshPlannerContractPreview(requestPreview);
        return requestPreview;
    }

    private static void MergePlannerRuntimeStatus(DadPlannerRunRequestPreview requestPreview, DadModuleExecutionStatusDto runtimeStatus)
    {
        MergePlannerModuleBlockers(requestPreview.ModuleBlockers, runtimeStatus.Blockers);

        if (!runtimeStatus.CanStart)
        {
            if (requestPreview.CanStart || string.IsNullOrWhiteSpace(requestPreview.BlockedReason))
            {
                var reason = string.IsNullOrWhiteSpace(runtimeStatus.BlockedReason)
                    ? string.IsNullOrWhiteSpace(runtimeStatus.FailureReason)
                        ? runtimeStatus.Summary
                        : runtimeStatus.FailureReason
                    : runtimeStatus.BlockedReason;
                requestPreview.CanStart = false;
                requestPreview.BlockedReason = reason;
                requestPreview.StatusSummary = $"Planner request blocked by runtime readiness: {reason}";
            }

            return;
        }

        if (requestPreview.CanStart && !string.IsNullOrWhiteSpace(runtimeStatus.Summary))
            requestPreview.StatusSummary = $"Planner request ready to start. {runtimeStatus.Summary}";
    }

    private static void MergePlannerPreviewBlocker(DadPlannerRunRequestPreview requestPreview, string blocker)
    {
        if (string.IsNullOrWhiteSpace(blocker))
            return;

        if (requestPreview.CanStart || string.IsNullOrWhiteSpace(requestPreview.BlockedReason))
        {
            requestPreview.CanStart = false;
            requestPreview.BlockedReason = blocker;
            requestPreview.StatusSummary = $"Planner request blocked: {blocker}";
        }

        if (requestPreview.ModuleBlockers.All(existing => !string.Equals(existing.Summary, blocker, StringComparison.OrdinalIgnoreCase)))
        {
            requestPreview.ModuleBlockers.Add(new DadModuleBlockerDto
            {
                ModuleId = requestPreview.ModuleId,
                Capability = "PlannerRuntime",
                Severity = DadModuleBlockerSeverity.Blocked,
                Summary = blocker,
            });
        }
    }

    private static void MergePlannerModuleBlockers(List<DadModuleBlockerDto> target, IReadOnlyList<DadModuleBlockerDto> source)
    {
        foreach (var blocker in source)
        {
            if (target.Any(existing =>
                    existing.ModuleId == blocker.ModuleId &&
                    string.Equals(existing.Capability, blocker.Capability, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Summary, blocker.Summary, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            target.Add(blocker.Clone());
        }
    }

    private static void RefreshPlannerContractPreview(DadPlannerRunRequestPreview requestPreview)
    {
        requestPreview.ContractPreview.CanStart = requestPreview.CanStart;
        requestPreview.ContractPreview.Startability = BuildPlannerStartabilityLabel(requestPreview);
        requestPreview.ContractPreview.Blockers = BuildPlannerContractBlockers(requestPreview);
        requestPreview.ContractPreviewJson = DadIpcJson.Serialize(requestPreview.ContractPreview);
    }

    private static string BuildPlannerStartabilityLabel(DadPlannerRunRequestPreview requestPreview)
        => requestPreview.CanStart
            ? "Startable"
            : string.Equals(requestPreview.Request?.RequestedBy, "planner-preview", StringComparison.OrdinalIgnoreCase)
                ? "PreviewOnly"
                : "Blocked";

    private static List<string> BuildPlannerContractBlockers(DadPlannerRunRequestPreview requestPreview)
    {
        var blockers = new List<string>();
        if (!string.IsNullOrWhiteSpace(requestPreview.BlockedReason))
            blockers.Add(requestPreview.BlockedReason);

        blockers.AddRange(requestPreview.PlannerPreview.Blockers);
        blockers.AddRange(requestPreview.ModuleBlockers
            .Select(static blocker => blocker.Summary)
            .Where(static summary => !string.IsNullOrWhiteSpace(summary)));
        return blockers
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildPlannerPreviewSignature(DadPresetPlannerOptions options, DadActivityPreset plannerPreview)
    {
        var accountKeys = string.Join(",", options.IncludedAccountKeys
            .Select(static key => key.Value.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));
        var selectedSlots = string.Join(",", plannerPreview.SelectedCharacters.Select(static slot =>
            $"{slot.SlotId}:{slot.RequiredRole}:{slot.AssignmentMode}:{slot.CharacterKey}:{slot.AllowSubstitution}:{slot.IsSubstitution}"));
        var selectedCharacters = string.Join(",", plannerPreview.SelectedCharacters
            .Select(static slot => slot.CharacterKey.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));

        return string.Join("|", new[]
        {
            $"activity={options.ActivityMode}",
            $"group={options.SelectedPlannerGroupId.Trim()}:{plannerPreview.SelectedPlannerGroupId.Trim()}",
            $"operator={options.OperatorMode}",
            $"transport={options.TransportOwner}",
            $"queue={options.QueueAuthority}",
            $"invite={options.InviteAuthority}",
            $"connected={options.ConnectedOnly}",
            $"datacenter={options.SameDatacenterOnly}",
            $"stale={options.AllowStaleForPlanning}",
            $"accounts={accountKeys}",
            $"duty={options.DutyContentFinderConditionId}:{options.DutyDisplayName.Trim()}:{options.DutyUnsynced}:{options.DutyExpectedPartySize}",
            $"mogtome={options.MogtomePreset.Trim()}:{options.MogtomeDutyPolicy.Trim()}",
            "blunderville=emote-run",
            $"leader={plannerPreview.LeaderCharacterKey}",
            $"slots={selectedSlots}",
            $"selected={selectedCharacters}",
        });
    }

    public bool HasServerDadAuthority()
    {
        if (RunCoordinatorService.IsServerDad)
            return TransportService.IsReady && !string.IsNullOrWhiteSpace(TransportService.CurrentTransport.ListenerEndpoint);

        return !string.IsNullOrWhiteSpace(TransportService.GetPreferredAuthorityEndpoint());
    }

    public DadVisibleRunState GetVisibleRunState(bool forceAuthorityRefresh = false)
    {
        LogAuthorityTransportChanges();
        var localRun = RunCoordinatorService.GetLocalResult();
        var authorityRun = GetAuthorityRunForUi(forceAuthorityRefresh);
        var authorityView = BuildAuthorityView(localRun, authorityRun);
        var isRemoteAuthorityView = authorityView.Kind is not DadAuthorityViewKind.LocalOnly and not DadAuthorityViewKind.NoRemoteAuthority;
        var visibleRun = localRun.Status != DadRunStatus.Idle ? localRun : authorityView.PreferredRun;
        var runState = new DadVisibleRunState(localRun, authorityRun, visibleRun, isRemoteAuthorityView, authorityView);
        LogVisibleRunStateTransition(runState);
        return runState;
    }

    public static bool IsBusy(DadRunResult result)
        => result.Status is DadRunStatus.Queued or DadRunStatus.WaitingForParticipants or DadRunStatus.Running;

    public bool IsRemoteAuthorityView(DadRunResult localRun, DadRunResult authorityRun)
        => BuildAuthorityView(localRun, authorityRun).Kind is not DadAuthorityViewKind.LocalOnly and not DadAuthorityViewKind.NoRemoteAuthority;

    private DadAuthorityViewState BuildAuthorityView(DadRunResult localRun, DadRunResult authorityRun)
        => DadAuthorityViewBuilder.Build(
            localRun,
            authorityRun,
            TransportService.CurrentTransport,
            PresenceService.WorkerSessionId,
            Configuration.LocalOnlyModeEnabled,
            lastAuthorityRefreshSucceededUtc,
            DateTime.UtcNow,
            RemoteAuthorityStatusStaleThreshold);

    private DadRunResult GetAuthorityRunForUi(bool forceRefresh)
    {
        var localRun = RunCoordinatorService.GetLocalResult();
        if (RunCoordinatorService.IsServerDad || !Configuration.PluginEnabled || Configuration.LocalOnlyModeEnabled)
        {
            ResetAuthorityCache(clearFreshness: true);
            return localRun;
        }

        var transport = TransportService.CurrentTransport;
        var authorityEndpoint = TransportService.GetPreferredAuthorityEndpoint();
        var hasRemoteAuthority = !string.IsNullOrWhiteSpace(authorityEndpoint) || !transport.AuthorityWorkerSessionId.IsEmpty;
        if (!hasRemoteAuthority)
        {
            ResetAuthorityCache(clearFreshness: true);
            return BuildUnavailableAuthorityResult(
                "No Server Dad authority discovered.",
                "No Server Dad authority discovered from peer registry and no authority target is configured.",
                authorityEndpoint,
                transport.AuthorityWorkerSessionId,
                transport.AuthorityRole);
        }

        if (!string.Equals(cachedAuthorityEndpoint, authorityEndpoint, StringComparison.OrdinalIgnoreCase))
            cachedAuthorityRun = null;

        if (!forceRefresh &&
            cachedAuthorityRun != null &&
            string.Equals(cachedAuthorityEndpoint, authorityEndpoint, StringComparison.OrdinalIgnoreCase) &&
            DateTime.UtcNow < nextAuthorityStatusRefreshUtc)
        {
            return CloneAuthorityRun(cachedAuthorityRun);
        }

        if (!forceRefresh && DateTime.UtcNow < suppressRemoteAuthorityRefreshUntilUtc)
        {
            return BuildUnavailableAuthorityResult(
                "Server Dad status refresh deferred.",
                "Server Dad status refresh deferred while endpoint changes settle.",
                authorityEndpoint,
                transport.AuthorityWorkerSessionId,
                transport.AuthorityRole);
        }

        var remote = string.IsNullOrWhiteSpace(authorityEndpoint)
            ? null
            : TransportService.QueryAuthorityStatus(authorityEndpoint);
        if (remote != null)
        {
            ApplyKnownAuthorityMetadata(remote);
            cachedAuthorityRun = remote.Clone();
            cachedAuthorityEndpoint = authorityEndpoint;
            nextAuthorityStatusRefreshUtc = DateTime.UtcNow + RemoteAuthorityStatusRefreshInterval;
            lastAuthorityRefreshSucceededUtc = DateTime.UtcNow;
            LogAuthorityRefreshSuccess(remote);
            return remote;
        }

        nextAuthorityStatusRefreshUtc = DateTime.UtcNow + RemoteAuthorityStatusRefreshInterval;
        LogAuthorityRefreshFailure(authorityEndpoint, transport.AuthorityWorkerSessionId);
        if (cachedAuthorityRun != null &&
            string.Equals(cachedAuthorityEndpoint, authorityEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            return CloneAuthorityRun(cachedAuthorityRun);
        }

        return BuildUnavailableAuthorityResult(
            "Server Dad status unavailable.",
            "Server Dad status query failed.",
            authorityEndpoint,
            transport.AuthorityWorkerSessionId,
            transport.AuthorityRole);
    }

    private DadRunResult CloneAuthorityRun(DadRunResult authorityRun)
    {
        var clone = authorityRun.Clone();
        ApplyKnownAuthorityMetadata(clone);
        return clone;
    }

    private void ResetAuthorityCache(bool clearFreshness)
    {
        cachedAuthorityRun = null;
        cachedAuthorityEndpoint = string.Empty;
        nextAuthorityStatusRefreshUtc = DateTime.MinValue;
        if (clearFreshness)
        {
            lastAuthorityRefreshSucceededUtc = null;
        }
    }

    private DadRunResult BuildUnavailableAuthorityResult(
        string summary,
        string blockedReason,
        string authorityEndpoint,
        DadWorkerSessionId authorityWorkerSessionId,
        DadWorkerRole authorityRole)
    {
        var unavailable = DadRunResult.Idle();
        unavailable.WorkerRole = authorityRole;
        unavailable.AuthorityMode = DadAuthorityMode.ServerDad;
        unavailable.AuthorityEndpoint = authorityEndpoint;
        unavailable.AuthorityWorkerSessionId = authorityWorkerSessionId;
        unavailable.Summary = summary;
        unavailable.ActiveTaskStatus = summary;
        unavailable.BlockedReason = blockedReason;
        if (!string.IsNullOrWhiteSpace(blockedReason))
            unavailable.Warnings.Add(blockedReason);
        return unavailable;
    }

    private void ApplyKnownAuthorityMetadata(DadRunResult authorityRun)
    {
        if (string.IsNullOrWhiteSpace(authorityRun.AuthorityEndpoint))
            authorityRun.AuthorityEndpoint = TransportService.GetPreferredAuthorityEndpoint();
        if (authorityRun.AuthorityWorkerSessionId.IsEmpty)
            authorityRun.AuthorityWorkerSessionId = TransportService.CurrentTransport.AuthorityWorkerSessionId;
        if (authorityRun.WorkerRole == DadWorkerRole.None && !authorityRun.AuthorityWorkerSessionId.IsEmpty)
            authorityRun.WorkerRole = TransportService.CurrentTransport.AuthorityRole != DadWorkerRole.None
                ? TransportService.CurrentTransport.AuthorityRole
                : DadWorkerRole.ServerDad;
    }

    private DadRunResult StartDemoRunFromShell(string label, DadRunRequest request)
    {
        var result = RunCoordinatorService.StartTasks(request);
        PrimeAuthorityCacheFromRun(request, result);
        PrintStatus(BuildShellRunSummary(label, request, result));
        if (RequiresServerDadAuthority(request))
        {
            var authorityView = GetVisibleRunState(forceAuthorityRefresh: false).AuthorityView;
            PrintStatus($"Authority: {authorityView.TimelineText} | {authorityView.FreshnessText}");
        }

        return result;
    }

    private static DadRunRequest BuildLocalSastashaDemoRequest()
        => new()
        {
            RequestedBy = "shell",
            Orchestration = new DadOrchestrationIntent
            {
                LocalOnlyOverride = true,
                RosterIntent = new DadRosterIntent
                {
                    ExpectedPartySize = 1,
                    RequireRemoteParticipants = false,
                },
            },
            Dungeon = new DadDungeonTask
            {
                Count = 1,
                Frequency = DadRunRequestOptions.FrequencyPerArRun,
                ContentFinderConditionId = 4,
                SelectedDungeon = "Sastasha",
                ExecutionPreference = DadRunRequestOptions.TrustThenDutySupport,
            },
        };

    private static DadRunRequest BuildServerSastashaDemoRequest()
        => new()
        {
            RequestedBy = "shell",
            Orchestration = BuildServerDadPartyIntent(),
            Dungeon = new DadDungeonTask
            {
                Count = 1,
                Frequency = DadRunRequestOptions.FrequencyPerArRun,
                ContentFinderConditionId = 4,
                SelectedDungeon = "Sastasha",
                ExecutionPreference = DadRunRequestOptions.TrustThenDutySupport,
                QueueViaLanParty = true,
            },
        };

    private static DadRunRequest BuildDailyMsqDemoRequest()
        => new()
        {
            RequestedBy = "shell",
            Orchestration = BuildServerDadPartyIntent(),
            DailyMsq = new DadDailyMsqTask
            {
                LanPartyPreset = "Daily MSQ",
            },
        };

    private static DadRunRequest BuildCommendationDemoRequest()
        => new()
        {
            RequestedBy = "shell",
            Orchestration = BuildServerDadPartyIntent(),
            Commendation = new DadCommendationTask
            {
                Attempts = 1,
            },
        };

    private static DadOrchestrationIntent BuildServerDadPartyIntent()
        => new()
        {
            LocalOnlyOverride = false,
            AuthorityMode = DadAuthorityMode.ServerDad,
            TransportMode = DadTransportMode.LocalhostHybrid,
            QueueAuthority = DadQueueAuthority.Leader,
            RosterIntent = new DadRosterIntent
            {
                ExpectedPartySize = 4,
                RequireRemoteParticipants = true,
            },
        };

    public DadRunResult CancelActiveRunFromShell()
    {
        var result = RunCoordinatorService.CancelActiveRun();
        PrintStatus(result.Summary);
        return result;
    }

    public DadCharacterPool RefreshCharacterPoolFromShell()
    {
        var pool = CharacterIntelligenceService.RefreshLocalCharacterPool("shell");
        PrintStatus($"dad pool refreshed. {pool.LastSummary}");
        return pool;
    }

    public DadCharacterPool SaveLocalCharacterToXadbFromShell()
    {
        var pool = CharacterIntelligenceService.SaveLocalToXadb();
        PrintStatus($"dad XADB save requested. {pool.XadbStatus.LastStatus}");
        return pool;
    }

    public DadCharacterPool RequestPeerSnapshotsFromShell()
    {
        var pool = CharacterIntelligenceService.RequestPeerSnapshots();
        PrintStatus($"dad peer snapshot request status: {pool.PeerTransport.LastRequestStatus}");
        return pool;
    }

    public void SetPluginEnabled(bool enabled, bool printStatus = true)
    {
        Configuration.PluginEnabled = enabled;
        Configuration.Save();
        UpdateDtrBar();

        if (printStatus)
            PrintStatus(enabled ? "dad enabled." : "dad disabled.");
    }

    public void SetDebugUiEnabled(bool enabled)
    {
        Configuration.DebugUiEnabled = enabled;
        Configuration.Save();
        PrintStatus(enabled
            ? "dad debug UI enabled. Verbose planner/runtime diagnostics are visible."
            : "dad debug UI disabled. Operator UI is compact.");
    }

    public void ToggleDebugUi()
        => SetDebugUiEnabled(!Configuration.DebugUiEnabled);

    public void ResetWindowPositions()
    {
        mainWindow.ResetToOrigin();
        configWindow.ResetToOrigin();
        mainWindow.IsOpen = true;
        configWindow.IsOpen = true;
        PrintStatus("Reset dad window positions to 1,1.");
    }

    public void JumpWindowsToRandomVisibleLocation()
    {
        mainWindow.QueueRandomVisibleJump();
        configWindow.QueueRandomVisibleJump();
        mainWindow.IsOpen = true;
        configWindow.IsOpen = true;
        PrintStatus("Queued random visible positions for the dad windows.");
    }

    public void OpenExternalLink(string url, string description)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            PrintStatus($"Opened {description}.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[dad] Failed to open {Description}.", description);
            PrintStatus($"Failed to open {description}.");
        }
    }

    public void PrintStatusReport()
    {
        var profile = ConfigManager.GetActiveConfig();
        var runState = GetVisibleRunState(forceAuthorityRefresh: true);
        var localRun = runState.LocalRun;
        var authorityRun = runState.AuthorityRun;
        var authorityView = runState.AuthorityView;
        var characterPool = CharacterIntelligenceService.CurrentPool;
        var plannerPreview = BuildPlannerPreview();
        PrintStatus(
            $"IPC {(RunCoordinatorService.IsReady ? "ready" : "not ready")} | " +
            $"This instance {DadStatusText.FormatWorkerRole(localRun.WorkerRole)} | " +
            $"Authority view {authorityView.StateText} | " +
            $"Client {authorityView.ClientPerspectiveText} | " +
            $"{authorityView.FreshnessText} | " +
            $"Local-only {(localRun.LocalOnlyEnabled ? "on" : "off")} | " +
            $"Debug UI {(Configuration.DebugUiEnabled ? "on" : "off")} | " +
            $"Profile {(profile.Enabled ? "armed" : "off")} | " +
            $"IPC starts {(profile.AllowIpcStarts ? "allowed" : "blocked")} | " +
            $"Pool {characterPool.Characters.Count} row(s) / XADB {characterPool.XadbStatus.Availability} / peers {characterPool.PeerTransport.ConnectedPeerCount}");
        PrintStatus($"Authority timeline: {FormatOperatorTextForChat(authorityView.TimelineText)}");
        PrintStatus($"Authority owner: {FormatOperatorTextForChat(authorityView.OwnershipText)}");
        PrintStatus($"Local run: {FormatRunStatusForChat(localRun)} | Payload {FormatOperatorTextForChat(FormatTaskPayload(localRun))}");
        PrintStatus($"Authority run: {FormatRunStatusForChat(authorityRun)} | Payload {FormatOperatorTextForChat(authorityView.PayloadText)}");
        PrintStatus($"Planner: {FormatOperatorTextForChat(plannerPreview.PlannerSummary)}");
        PrintStatus($"Planner request: {BuildPlannerRunRequestPreview().StatusSummary}");
    }

    private string FormatRunStatusForChat(DadRunResult run)
    {
        var requestId = string.IsNullOrWhiteSpace(run.RequestId) ? "(none)" : run.RequestId;
        var taskName = string.IsNullOrWhiteSpace(run.ActiveTaskName)
            ? "(none)"
            : $"{run.ActiveTaskIndex}/{run.TotalTaskCount} {run.ActiveTaskName}";
        var taskDetail = string.IsNullOrWhiteSpace(run.ActiveTaskStatus) ? run.Summary : run.ActiveTaskStatus;
        var blocker = string.IsNullOrWhiteSpace(run.BlockedReason)
            ? string.Empty
            : $" | {(DadOperatorPhaseText.HasBlockingFailure(run) ? "Blocked" : "Note")} {run.BlockedReason}";
        var operatorPhase = DadOperatorPhaseText.GetPhaseLabel(run);
        var phasePrefix = string.IsNullOrWhiteSpace(operatorPhase)
            ? string.Empty
            : $"DAD: {operatorPhase} | ";
        return FormatOperatorTextForChat($"{phasePrefix}{run.Status} / {run.Phase} / {run.ModuleId} | {taskDetail} | Task {taskName}{blocker} | Request {requestId}");
    }

    private static string FormatTaskPayload(DadRunResult run)
        => run.Request?.DescribeRequestedWork() ?? "No active dad task payload.";

    private string FormatOperatorTextForChat(string value)
        => KrangleService.FormatOperatorText(value, CharacterIntelligenceService.CurrentPool);

    private string BuildShellRunSummary(string label, DadRunRequest request, DadRunResult result)
    {
        var operatorPhase = DadOperatorPhaseText.GetPhaseLabel(result);
        var phaseText = string.IsNullOrWhiteSpace(operatorPhase)
            ? $"{result.Status}/{result.Phase}/{result.ModuleId}"
            : $"DAD: {operatorPhase} | {result.Status}/{result.Phase}/{result.ModuleId}";
        return $"{label}: {BuildShellRoutingText(request, result)} | Payload {request.DescribeRequestedWork()} | Result {phaseText} | {result.Summary}";
    }

    private static string BuildShellRoutingText(DadRunRequest request, DadRunResult result)
    {
        var routedToServerDad = RequiresServerDadAuthority(request);
        if (!routedToServerDad)
        {
            return result.Status == DadRunStatus.Rejected
                ? "local request rejected"
                : "local request accepted";
        }

        return result.Status == DadRunStatus.Rejected
            ? "forwarded to Server Dad, rejected"
            : "forwarded to Server Dad, accepted";
    }

    private void PrimeAuthorityCacheFromRun(DadRunRequest request, DadRunResult result)
    {
        if (!RequiresServerDadAuthority(request) || Configuration.LocalOnlyModeEnabled)
            return;

        ApplyKnownAuthorityMetadata(result);
        if (string.IsNullOrWhiteSpace(result.AuthorityEndpoint) && result.AuthorityWorkerSessionId.IsEmpty)
            return;

        cachedAuthorityRun = result.Clone();
        cachedAuthorityEndpoint = result.AuthorityEndpoint;
        nextAuthorityStatusRefreshUtc = DateTime.UtcNow + RemoteAuthorityStatusRefreshInterval;
        lastAuthorityRefreshSucceededUtc = DateTime.UtcNow;
    }

    private void OnCommand(string command, string arguments)
    {
        var trimmed = arguments.Trim();

        if (trimmed.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            ToggleConfigUi();
            return;
        }

        if (trimmed.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            SetPluginEnabled(true);
            return;
        }

        if (trimmed.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            SetPluginEnabled(false);
            return;
        }

        if (trimmed.Equals("debug", StringComparison.OrdinalIgnoreCase))
        {
            ToggleDebugUi();
            return;
        }

        if (trimmed.Equals("debug on", StringComparison.OrdinalIgnoreCase))
        {
            SetDebugUiEnabled(true);
            return;
        }

        if (trimmed.Equals("debug off", StringComparison.OrdinalIgnoreCase))
        {
            SetDebugUiEnabled(false);
            return;
        }

        if (trimmed.Equals("ws", StringComparison.OrdinalIgnoreCase))
        {
            ResetWindowPositions();
            return;
        }

        if (trimmed.Equals("j", StringComparison.OrdinalIgnoreCase))
        {
            JumpWindowsToRandomVisibleLocation();
            return;
        }

        if (trimmed.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            PrintStatusReport();
            return;
        }

        if (trimmed.Equals("krangle", StringComparison.OrdinalIgnoreCase))
        {
            ToggleKrangleOperatorNames();
            return;
        }

        if (trimmed.Equals("refresh", StringComparison.OrdinalIgnoreCase))
        {
            RefreshCharacterPoolFromShell();
            return;
        }

        if (trimmed.Equals("save", StringComparison.OrdinalIgnoreCase))
        {
            SaveLocalCharacterToXadbFromShell();
            return;
        }

        if (trimmed.Equals("peers", StringComparison.OrdinalIgnoreCase))
        {
            RequestPeerSnapshotsFromShell();
            return;
        }

        if (trimmed.Equals("run", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("run local", StringComparison.OrdinalIgnoreCase))
        {
            StartLocalDemoRunFromShell();
            return;
        }

        if (trimmed.Equals("run server", StringComparison.OrdinalIgnoreCase))
        {
            StartServerDemoRunFromShell();
            return;
        }

        if (trimmed.Equals("run msq", StringComparison.OrdinalIgnoreCase))
        {
            StartDailyMsqDemoRunFromShell();
            return;
        }

        if (trimmed.Equals("run commend", StringComparison.OrdinalIgnoreCase))
        {
            StartCommendationDemoRunFromShell();
            return;
        }

        if (trimmed.Equals("run planner", StringComparison.OrdinalIgnoreCase))
        {
            StartPlannerRunFromShell();
            return;
        }

        if (trimmed.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            CancelActiveRunFromShell();
            return;
        }

        ToggleMainUi();
    }

    private void SetupDtrBar()
    {
        dtrEntry = DtrBar.Get(PluginInfo.DisplayName);
        dtrEntry.OnClick = _ => SetPluginEnabled(!Configuration.PluginEnabled, printStatus: false);
    }

    public void UpdateDtrBar()
    {
        if (dtrEntry == null)
            return;

        dtrEntry.Shown = Configuration.DtrBarEnabled;
        if (!Configuration.DtrBarEnabled)
            return;

        var runState = GetVisibleRunState();
        var glyph = Configuration.PluginEnabled ? Configuration.DtrIconEnabled : Configuration.DtrIconDisabled;
        var stateText = GetDtrStateText(runState);

        var textOnly = $"DAD: {stateText}";
        var iconAndText = $"{glyph} DAD: {stateText}";

        dtrEntry.Text = Configuration.DtrBarMode switch
        {
            1 => new SeString(new TextPayload(iconAndText)),
            2 => new SeString(new TextPayload(glyph)),
            _ => new SeString(new TextPayload(textOnly)),
        };

        dtrEntry.Tooltip = new SeString(new TextPayload($"{PluginInfo.DisplayName} {stateText}: {runState.AuthorityView.TimelineText} {runState.AuthorityView.FreshnessText} Click to toggle."));
    }

    private string GetDtrStateText(DadVisibleRunState runState)
    {
        if (!Configuration.PluginEnabled)
            return "Off";

        return runState.AuthorityView.DtrText;
    }

    private void LogAuthorityTransportChanges()
    {
        var transport = TransportService.CurrentTransport;
        var worker = transport.AuthorityWorkerSessionId.IsEmpty ? "(none)" : transport.AuthorityWorkerSessionId.ToString();
        var discoveredEndpoint = string.IsNullOrWhiteSpace(transport.AuthorityEndpoint) ? "(none)" : transport.AuthorityEndpoint;
        var preferredEndpoint = string.IsNullOrWhiteSpace(TransportService.GetPreferredAuthorityEndpoint()) ? "(none)" : TransportService.GetPreferredAuthorityEndpoint();
        var key = $"{worker}|{discoveredEndpoint}|{preferredEndpoint}|{transport.AuthorityRole}";
        if (string.Equals(lastLoggedAuthorityEndpointKey, key, StringComparison.OrdinalIgnoreCase))
            return;

        lastLoggedAuthorityEndpointKey = key;
        Log.Information("[dad] Authority transport changed: {Worker} discovered @ {DiscoveredEndpoint} | target {PreferredEndpoint} ({Role})",
            worker,
            discoveredEndpoint,
            preferredEndpoint,
            DadStatusText.FormatWorkerRole(transport.AuthorityRole));
    }

    private void LogAuthorityRefreshSuccess(DadRunResult authorityRun)
    {
        var key = $"ok|{authorityRun.RequestId}|{authorityRun.Status}|{authorityRun.Phase}|{authorityRun.ModuleId}|{authorityRun.AuthorityWorkerSessionId}|{authorityRun.AuthorityEndpoint}";
        if (string.Equals(lastLoggedAuthorityRefreshKey, key, StringComparison.OrdinalIgnoreCase))
            return;

        lastLoggedAuthorityRefreshKey = key;
        Log.Information("[dad] Remote status refresh succeeded: {Status}/{Phase}/{Module} request {RequestId} via {Worker} @ {Endpoint}",
            authorityRun.Status,
            authorityRun.Phase,
            authorityRun.ModuleId,
            string.IsNullOrWhiteSpace(authorityRun.RequestId) ? "(none)" : authorityRun.RequestId,
            authorityRun.AuthorityWorkerSessionId.IsEmpty ? "(none)" : authorityRun.AuthorityWorkerSessionId.ToString(),
            string.IsNullOrWhiteSpace(authorityRun.AuthorityEndpoint) ? "(none)" : authorityRun.AuthorityEndpoint);
    }

    private void LogAuthorityRefreshFailure(string authorityEndpoint, DadWorkerSessionId authorityWorkerSessionId)
    {
        var key = $"fail|{authorityWorkerSessionId}|{authorityEndpoint}";
        if (string.Equals(lastLoggedAuthorityRefreshKey, key, StringComparison.OrdinalIgnoreCase))
            return;

        lastLoggedAuthorityRefreshKey = key;
        Log.Warning("[dad] Remote status refresh failed for {Worker} @ {Endpoint}; cached authority snapshot will be marked stale until refresh succeeds.",
            authorityWorkerSessionId.IsEmpty ? "(none)" : authorityWorkerSessionId.ToString(),
            string.IsNullOrWhiteSpace(authorityEndpoint) ? "(none)" : authorityEndpoint);
    }

    private void LogVisibleRunStateTransition(DadVisibleRunState runState)
    {
        var trackedRequest = runState.AuthorityRun.Request?.RequestedBy == "shell"
            ? runState.AuthorityRun
            : runState.LocalRun.Request?.RequestedBy == "shell"
                ? runState.LocalRun
                : null;
        if (trackedRequest == null)
            return;

        var key = $"{trackedRequest.RequestId}|{runState.AuthorityView.Kind}|{trackedRequest.Status}|{trackedRequest.Phase}|{trackedRequest.ModuleId}|{runState.AuthorityView.PayloadText}";
        if (string.Equals(lastLoggedAuthorityViewKey, key, StringComparison.OrdinalIgnoreCase))
            return;

        lastLoggedAuthorityViewKey = key;
        Log.Information("[dad] Visible run state -> {Kind}: {Timeline} | Freshness {Freshness}",
            runState.AuthorityView.Kind,
            runState.AuthorityView.TimelineText,
            runState.AuthorityView.FreshnessText);
    }

    private static bool RequiresServerDadAuthority(DadRunRequest request)
    {
        if (request.Orchestration.LocalOnlyOverride)
            return false;

        if (request.Orchestration.RosterIntent.RequireRemoteParticipants ||
            request.Orchestration.RosterIntent.ExpectedPartySize > 1)
        {
            return true;
        }

        return request.Dungeon?.QueueViaLanParty == true ||
               request.Msq != null ||
               request.PremadeDuty != null ||
               request.DailyMsq != null ||
               request.Mogtome != null ||
               request.Commendation != null ||
               request.Astrope != null;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        UpdateDtrBar();
        if (ClientState.IsLoggedIn && ObjectTable.LocalPlayer != null)
        {
            var player = ObjectTable.LocalPlayer;
            ConfigManager.EnsureAccountSelected(PlayerState.ContentId, player.Name.ToString());
            ConfigManager.EnsureCharacterExists(player.Name.ToString(), player.HomeWorld.Value.Name.ToString());

            if (!string.IsNullOrWhiteSpace(ConfigManager.CurrentAccountId)
                && !string.Equals(Configuration.LastAccountId, ConfigManager.CurrentAccountId, StringComparison.Ordinal))
            {
                Configuration.LastAccountId = ConfigManager.CurrentAccountId;
                Configuration.Save();
            }
        }

        CharacterIntelligenceService.Update();
        PresenceService.Update(CharacterIntelligenceService.CurrentPool, TransportService.CurrentTransport.ListenerEndpoint);
        TransportService.UpdateHeartbeat(
            PresenceService.BuildSnapshotCopy(),
            Configuration.PluginEnabled,
            Configuration.LocalOnlyModeEnabled);
        RunCoordinatorService.Update();
    }

    private void OnLogin()
    {
        UpdateDtrBar();
        CharacterIntelligenceService.RefreshLocalCharacterPool("login", logRefresh: false);
        PresenceService.Update(CharacterIntelligenceService.CurrentPool, TransportService.CurrentTransport.ListenerEndpoint);
    }
}
