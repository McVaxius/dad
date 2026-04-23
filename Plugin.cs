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
    public DadPresetProviderService PresetProviderService { get; }
    public DadModuleRegistry ModuleRegistry { get; }
    public DadPlannerService PlannerService { get; }
    public DadPartyAssemblyService PartyAssemblyService { get; }
    public DadDutyQueueService DutyQueueService { get; }
    public DadDutySupportQueueService DutySupportQueueService { get; }
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
        PresetProviderService = new DadPresetProviderService();
        ModuleRegistry = new DadModuleRegistry();
        PlannerService = new DadPlannerService(PresetProviderService, ModuleRegistry);
        PartyAssemblyService = new DadPartyAssemblyService();
        DutyQueueService = new DadDutyQueueService(ExternalPluginCapabilityService);
        DutySupportAdsService = new DadDutySupportAdsService(Log);
        DutySupportQueueService = new DadDutySupportQueueService(Log);
        CombatRotationService = new DadCombatRotationService(Configuration, Log);
        QueueExecutionService = new DadQueueExecutionService(
            ModuleRegistry,
            DutyQueueService,
            ExternalPluginCapabilityService,
            DutySupportQueueService,
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
            HelpMessage = $"Open {PluginInfo.DisplayName}. Use {PluginInfo.Command} config, {PluginInfo.Command} on, {PluginInfo.Command} off, {PluginInfo.Command} krangle, {PluginInfo.Command} ws, {PluginInfo.Command} j, {PluginInfo.Command} status, {PluginInfo.Command} refresh, {PluginInfo.Command} save, {PluginInfo.Command} peers, {PluginInfo.Command} run local, {PluginInfo.Command} run server, {PluginInfo.Command} run msq, {PluginInfo.Command} run commend, {PluginInfo.Command} run planner, or {PluginInfo.Command} cancel. Dad now exposes Server Dad authority, Client Dad workers, sticky local-only mode, krangled operator names, and account-aware readiness/lease status.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;

        SetupDtrBar();
        UpdateDtrBar();

        dadIpcService = new DadIpcService(
            PluginInterface,
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
        DutySupportQueueService.Dispose();
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
        => PresetProviderService.BuildPlannerPreview(CharacterIntelligenceService.CurrentPool, PlannerOptions);

    public string BuildPlannerSummary()
        => PresetProviderService.BuildPlannerSummary(CharacterIntelligenceService.CurrentPool, PlannerOptions);

    public DadPlannerRunRequestPreview BuildPlannerRunRequestPreview()
    {
        var pool = CharacterIntelligenceService.CurrentPool;
        var plannerPreview = PresetProviderService.BuildPlannerPreview(pool, PlannerOptions);
        var signature = BuildPlannerPreviewSignature(PlannerOptions, plannerPreview);
        var identity = ResolvePlannerPreviewIdentity(signature);
        return PresetProviderService.BuildPlannerRunRequestPreview(
            pool,
            PlannerOptions,
            identity.RequestId,
            identity.RequestedAtUtc,
            plannerPreview);
    }

    public string BuildPlannerRequestJson()
    {
        var requestPreview = BuildPlannerRunRequestPreview();
        return requestPreview.RequestJson;
    }

    public void SavePlannerOptions()
        => Configuration.Save();

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
            $"operator={options.OperatorMode}",
            $"transport={options.TransportOwner}",
            $"queue={options.QueueAuthority}",
            $"invite={options.InviteAuthority}",
            $"connected={options.ConnectedOnly}",
            $"datacenter={options.SameDatacenterOnly}",
            $"stale={options.AllowStaleForPlanning}",
            $"accounts={accountKeys}",
            $"duty={options.DutyContentFinderConditionId}:{options.DutyDisplayName.Trim()}:{options.DutyUnsynced}:{options.DutyExpectedPartySize}",
            $"mogtome={options.MogtomePreset.Trim()}",
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
        var blocker = string.IsNullOrWhiteSpace(run.BlockedReason) ? string.Empty : $" | Blocker {run.BlockedReason}";
        return FormatOperatorTextForChat($"{run.Status} / {run.Phase} / {run.ModuleId} | {taskDetail} | Task {taskName}{blocker} | Request {requestId}");
    }

    private static string FormatTaskPayload(DadRunResult run)
        => run.Request?.DescribeRequestedWork() ?? "No active dad task payload.";

    private string FormatOperatorTextForChat(string value)
        => KrangleService.FormatOperatorText(value, CharacterIntelligenceService.CurrentPool);

    private string BuildShellRunSummary(string label, DadRunRequest request, DadRunResult result)
        => $"{label}: {BuildShellRoutingText(request, result)} | Payload {request.DescribeRequestedWork()} | Result {result.Status}/{result.Phase}/{result.ModuleId} | {result.Summary}";

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
