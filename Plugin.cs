using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
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
    private static readonly TimeSpan PlannerUiCacheSlowRebuildThreshold = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan PlannerUiCacheSlowRebuildLogCooldown = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DebouncedUiWriteDelay = TimeSpan.FromSeconds(3);

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
    internal InfoProxyPartyInviteGateway PartyInviteGateway { get; }
    internal DadPartyTeardownService PartyTeardownService { get; }
    public DadPresenceService PresenceService { get; }
    public DadVermaxionIpcService VermaxionIpcService { get; }
    public DadAutoRetainerIpcService AutoRetainerIpcService { get; }
    public DadLifestreamIpcService LifestreamIpcService { get; }
    public DadWakeTakeoverService WakeTakeoverService { get; }
    public DadClaimService ClaimService { get; }
    public DadTransportService TransportService { get; }
    public DadCharacterIntelligenceService CharacterIntelligenceService { get; }
    public DadRosterCatalogService RosterCatalogService { get; }
    public DadProfileDirectoryService ProfileDirectoryService { get; }
    public DadKrangleService KrangleService { get; }
    public DadShareService ShareService { get; }
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
    public DadMogtomeIpcService MogtomeIpcService { get; }
    public DadQueueExecutionService QueueExecutionService { get; }
    public DadWorkerExecutionService WorkerExecutionService { get; }
    public DadSchedulerService SchedulerService { get; }
    public DadCoordinatorService RunCoordinatorService { get; }
    public DadDutyIpcService DutyIpcService { get; }
    public DadQuestionableReflectionBridge QuestionableBridge { get; }
    public WindowSystem WindowSystem { get; } = new(PluginInfo.InternalName);
    public string LastIssueReportStatus { get; private set; } = "No Dad issue report generated this session.";
    public string LastIssueReportPath { get; private set; } = string.Empty;
    public DateTime? LastIssueReportUtc { get; private set; }

    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly SetupWizardWindow setupWizardWindow;
    private readonly DadMiniStatusWindow miniStatusWindow;
    private readonly DadClientReconnectWindow clientReconnectWindow;
    private readonly DadIpcService dadIpcService;
    private readonly DadBackgroundTaskObserver backgroundTasks;
    private readonly CancellationTokenSource backgroundCancellation = new();
    private readonly object authorityCacheGate = new();
    private IDtrBarEntry? dtrEntry;
    private DadRunResult? cachedAuthorityRun;
    private string cachedAuthorityEndpoint = string.Empty;
    private DateTime nextAuthorityStatusRefreshUtc = DateTime.MinValue;
    private DateTime suppressRemoteAuthorityRefreshUntilUtc = DateTime.MinValue;
    private DateTime? lastAuthorityRefreshSucceededUtc;
    private DateTime? lastAuthorityRefreshAttemptUtc;
    private bool authorityRefreshInFlight;
    private string lastAuthorityRefreshFailure = string.Empty;
    private string lastLoggedAuthorityEndpointKey = string.Empty;
    private string lastLoggedAuthorityRefreshKey = string.Empty;
    private string lastLoggedAuthorityViewKey = string.Empty;
    private string lastCoordinatorProvenanceDiagnostic = string.Empty;
    private string cachedPlannerPreviewSignature = string.Empty;
    private string cachedPlannerPreviewRequestId = string.Empty;
    private DateTime cachedPlannerPreviewRequestedAtUtc = DateTime.MinValue;
    private DadPlannerUiSnapshot? cachedPlannerUiSnapshot;
    private DadPlannerUiCacheKey? cachedPlannerUiCacheKey;
    private DadPlannerSchedulerCacheKey? cachedPlannerSchedulerCacheKey;
    private DadSchedulerPreview? cachedPlannerSchedulerPreview;
    private long plannerUiCacheGeneration;
    private string plannerUiCacheInvalidationReason = "cold";
    private DateTime lastSlowPlannerUiCacheLogUtc = DateTime.MinValue;
    private long plannerUiCacheHitCount;
    private long plannerUiCacheMissCount;
    private long plannerSchedulerCacheHitCount;
    private long plannerSchedulerCacheMissCount;
    private double plannerUiCacheLastRebuildMilliseconds;
    private double plannerUiCacheMaxRebuildMilliseconds;
    private DateTime plannerUiCacheLastRebuiltAtUtc = DateTime.MinValue;
    private string plannerUiCacheLastRebuildReason = "cold";
    private readonly DadRosterKnowledgeLearningCursor rosterKnowledgeLearningCursor = new();
    private readonly Dictionary<string, DebouncedUiWrite> debouncedUiWrites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> pendingAccountAliasDrafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DadStopAllWorkerResult> localStopAllResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly DadRuntimeReadinessTracker localRuntimeReadinessTracker = new();

    private sealed class DebouncedUiWrite
    {
        public DateTime DueAtUtc { get; init; }
        public Type ValueType { get; init; } = typeof(object);
        public object? Baseline { get; init; }
        public Func<bool> Commit { get; init; } = static () => false;
    }

    public Plugin()
    {
        backgroundTasks = new DadBackgroundTaskObserver(Log, "plugin");
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Configuration.MigrateTransportSettings())
            Configuration.Save();
        EnsureClientAccountId();
        ConfigManager = new ConfigManager(PluginInterface, Log);
        ConfigManager.EnsureAccountSelected(Configuration.ClientAccountId, "Dad client");
        ExternalPluginCapabilityService = new DadExternalPluginCapabilityService();
        XadbClient = new DadXadbClient(PluginInterface, Log);
        VermaxionIpcService = new DadVermaxionIpcService(PluginInterface, Log);
        AutoRetainerIpcService = new DadAutoRetainerIpcService(PluginInterface, Log);
        LifestreamIpcService = new DadLifestreamIpcService(PluginInterface);
        PartyInviteGateway = new InfoProxyPartyInviteGateway(Framework, PlayerState, PartyList, Log);
        PartyTeardownService = new DadPartyTeardownService(PartyList, PlayerState, Condition, Log);
        var requestedJobPreparationGate = new DadRequestedJobPreparationGate();
        var classJobGearsetGateway = new DadClassJobGearsetGateway(Framework);
        PresenceService = new DadPresenceService(
            Configuration,
            ConfigManager,
            VermaxionIpcService,
            AutoRetainerIpcService,
            PartyInviteGateway,
            requestedJobPreparationGate,
            classJobGearsetGateway,
            Log);
        WakeTakeoverService = new DadWakeTakeoverService(
            new DadWakeTakeoverTarget(
                Configuration,
                ConfigManager,
                PresenceService,
                AutoRetainerIpcService,
                LifestreamIpcService,
                VermaxionIpcService,
                CommandManager,
                Log),
            preCommitBudget: TimeSpan.FromSeconds(Configuration.AutoRetainerBusyTimeoutSeconds),
            diagnostic: message => Log.Information("[dad] Wake takeover epoch transition {Diagnostic}.", message));
        VermaxionIpcService.ReservationGranted += WakeTakeoverService.OnVermaxionReservationGranted;
        AutoRetainerIpcService.CharacterPostprocessReady += WakeTakeoverService.OnCharacterPostprocessReady;
        ClaimService = new DadClaimService();
        TransportService = new DadTransportService(Configuration, PresenceService, ClaimService, WakeTakeoverService, Log);
        PresenceService.ConfigureParticipantResolver(workerSessionId =>
            TransportService.CurrentTransport.KnownParticipants
                .SingleOrDefault(participant => string.Equals(
                    participant.WorkerSessionId.Value,
                    workerSessionId.Value,
                    StringComparison.OrdinalIgnoreCase))
                ?.Clone());
        CharacterIntelligenceService = new DadCharacterIntelligenceService(ConfigManager, XadbClient, TransportService, Log);
        RosterCatalogService = new DadRosterCatalogService(Configuration, ConfigManager, XadbClient, TransportService, PresenceService, Log);
        ProfileDirectoryService = new DadProfileDirectoryService(Configuration, ConfigManager, PresenceService, TransportService, Log);
        KrangleService = new DadKrangleService(Configuration);
        ShareService = new DadShareService(forceKrangle: KrangleService.KrangleName);
        ModuleRegistry = new DadModuleRegistry();
        PresetProviderService = new DadPresetProviderService(ModuleRegistry, () => RosterCatalogService.GetAccountDirectory());
        PlannerService = new DadPlannerService(PresetProviderService, ModuleRegistry, Configuration);
        PartyAssemblyService = new DadPartyAssemblyService();
        DutyQueueService = new DadDutyQueueService(ExternalPluginCapabilityService);
        DutySupportAdsService = new DadDutySupportAdsService(PluginInterface, Log);
        LocalDutyQueueService = new DadLocalDutyQueueService(Log, PresenceService.BuildLiveSafetySnapshot);
        NpcDutyQueueService = new DadNpcDutyQueueService(Log);
        CombatRotationService = new DadCombatRotationService(Configuration, PluginInterface, Log);
        MogtomeIpcService = new DadMogtomeIpcService(PluginInterface);
        QueueExecutionService = new DadQueueExecutionService(
            ModuleRegistry,
            MogtomeIpcService,
            DutyQueueService,
            LocalDutyQueueService,
            NpcDutyQueueService,
            DutySupportAdsService,
            CombatRotationService);
        WorkerExecutionService = new DadWorkerExecutionService(
            QueueExecutionService,
            PresenceService,
            CombatRotationService,
            DutySupportAdsService,
            Condition,
            Log);
        SchedulerService = new DadSchedulerService(
            Configuration,
            ConfigManager,
            ProfileDirectoryService,
            CharacterIntelligenceService,
            PresenceService,
            TransportService,
            WakeTakeoverService,
            RosterCatalogService,
            Log);
        TransportService.ConfigureRuntimeReadinessHandler(OnRemoteRuntimeReadinessChanged);
        RunCoordinatorService = new DadCoordinatorService(
            Configuration,
            ConfigManager,
            CharacterIntelligenceService,
            PresenceService,
            TransportService,
            ClaimService,
            PartyAssemblyService,
            PartyInviteGateway,
            PartyTeardownService,
            QueueExecutionService,
            WorkerExecutionService,
            PlannerService,
            Log);
        TransportService.ConfigureAuthorityHandlers(
            () => RunCoordinatorService.GetLocalResult(),
            request => RunCoordinatorService.StartTasks(request),
            _ => RunCoordinatorService.CancelActiveRun());
        TransportService.ConfigureRosterHandlers(
            () => RosterCatalogService.BuildLocalTransportCatalog(
                CharacterIntelligenceService.CurrentPool,
                PresenceService.BuildSnapshotCopy()),
            command => RosterCatalogService.RefreshLocalRosterCharacter(command, PresenceService.BuildSnapshotCopy()));
        TransportService.ConfigureProfileHandlers(
            ProfileDirectoryService.BuildLocalCatalog,
            ConfigManager.ApplyProfileUpdate);
        TransportService.ConfigureWorkerExecutionHandlers(
            WorkerExecutionService.Accept,
            WorkerExecutionService.GetStatus,
            WorkerExecutionService.Cancel);
        TransportService.ConfigureStopAllHandler(StopAllLocal);

        if (!string.IsNullOrWhiteSpace(Configuration.ClientAccountId))
            ConfigManager.CurrentAccountId = Configuration.ClientAccountId;

        ClientState.Login += OnLogin;

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        setupWizardWindow = new SetupWizardWindow(this);
        miniStatusWindow = new DadMiniStatusWindow(this);
        clientReconnectWindow = new DadClientReconnectWindow(this);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(setupWizardWindow);
        WindowSystem.AddWindow(miniStatusWindow);
        WindowSystem.AddWindow(clientReconnectWindow);
        OpenSetupWizardOnce();

        var plannerLaneCount = PresetProviderService.GetPlannerLaneDefinitions().Count();
        var buildVersion = GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        Log.Information("[dad] Planner lane panel enabled with {LaneCount} planner lanes. Build {BuildVersion}.", plannerLaneCount, buildVersion);

        CommandManager.AddHandler(PluginInfo.Command, new CommandInfo(OnCommand)
        {
            HelpMessage = $"Open {PluginInfo.DisplayName}. Use {PluginInfo.Command} mini, {PluginInfo.Command} config, {PluginInfo.Command} wizard, {PluginInfo.Command} debug, {PluginInfo.Command} on, {PluginInfo.Command} off, {PluginInfo.Command} status, {PluginInfo.Command} run planner, {PluginInfo.Command} test profiles, {PluginInfo.Command} test launch-profiles, {PluginInfo.Command} test workers, {PluginInfo.Command} test duty-ipc current, or {PluginInfo.Command} cancel.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;
        backgroundTasks.Track(
            RunAuthorityStatusPollLoopAsync(backgroundCancellation.Token),
            "authority status poll loop");

        SetupDtrBar();
        UpdateDtrBar();

        dadIpcService = new DadIpcService(
            PluginInterface,
            this,
            RunCoordinatorService,
            PresenceService,
            TransportService,
            ModuleRegistry,
            PresetProviderService,
            Log);
        DutyIpcService = new DadDutyIpcService(
            PluginInterface,
            PresetProviderService,
            LocalDutyQueueService,
            NpcDutyQueueService,
            DutySupportAdsService,
            CombatRotationService,
            Log);
        QuestionableBridge = new DadQuestionableReflectionBridge(PluginInterface, Framework, DutyIpcService, Log, () => Configuration.QuestionableBridgeEnabled);

        Log.Information("[dad] Plugin loaded.");
    }

    public void Dispose()
    {
        backgroundCancellation.Cancel();
        backgroundTasks.Dispose();
        PartyInviteGateway.Reset();
        Framework.Update -= OnFrameworkUpdate;
        ClientState.Login -= OnLogin;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        CommandManager.RemoveHandler(PluginInfo.Command);
        // Close/cancel transport work first, without synchronously waiting on the framework thread.
        // Its observer continues consuming late completions with Dalamud logging suppressed.
        TransportService.Dispose();
        FlushDebouncedUiWrites(force: true);
        WindowSystem.RemoveAllWindows();
        QuestionableBridge.Dispose();
        DutyIpcService.Dispose();
        dadIpcService.Dispose();
        ProfileDirectoryService.Dispose();
        LocalDutyQueueService.Dispose();
        NpcDutyQueueService.Dispose();
        AutoRetainerIpcService.CharacterPostprocessReady -= WakeTakeoverService.OnCharacterPostprocessReady;
        VermaxionIpcService.ReservationGranted -= WakeTakeoverService.OnVermaxionReservationGranted;
        WakeTakeoverService.Dispose();
        AutoRetainerIpcService.Dispose();
        VermaxionIpcService.Dispose();
        backgroundCancellation.Dispose();
        dtrEntry?.Remove();
    }

    public void ToggleMainUi() => mainWindow.Toggle();

    public void OpenMainUi() => mainWindow.IsOpen = true;

    public void ToggleMiniStatusUi() => miniStatusWindow.Toggle();

    public void OpenMiniStatusUi() => miniStatusWindow.IsOpen = true;

    public void DisableDadFromReconnectWindow()
    {
        SetPluginEnabled(false);
        clientReconnectWindow.IsOpen = false;
    }

    public void ToggleConfigUi() => configWindow.Toggle();

    public void OpenConfigUi() => configWindow.IsOpen = true;

    public void OpenSetupWizard() => setupWizardWindow.OpenLanding();

    public void OpenSetupWizard(DadGuideFlow flow) => setupWizardWindow.OpenFlow(flow);

    public void OpenMainTab(DadMainWindowTab tab, DadPresetsWindowTab? presetsTab = null)
        => mainWindow.OpenTab(tab, presetsTab);

    public void PrintStatus(string message) => ChatGui.Print($"[{PluginInfo.DisplayName}] {message}");

    public void QueueDebouncedUiWrite<T>(
        string key,
        T committedValue,
        Func<T> getCurrentValue,
        Action<T> commitValue,
        Func<T, T, bool>? areEqual = null)
    {
        key = key?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return;

        var valueType = typeof(T);
        var baseline = debouncedUiWrites.TryGetValue(key, out var existing) && existing.ValueType == valueType
            ? (T)existing.Baseline!
            : committedValue;
        var equals = areEqual ?? EqualityComparer<T>.Default.Equals;

        debouncedUiWrites[key] = new DebouncedUiWrite
        {
            DueAtUtc = DateTime.UtcNow.Add(DebouncedUiWriteDelay),
            ValueType = valueType,
            Baseline = baseline,
            Commit = () =>
            {
                var current = getCurrentValue();
                if (equals(baseline, current))
                    return false;

                commitValue(current);
                return true;
            },
        };
    }

    public void QueueDebouncedConfigurationSave(
        string key,
        string committedSignature,
        Func<string> currentSignature,
        Action? afterSave = null)
    {
        QueueDebouncedUiWrite(
            $"config:{key}",
            committedSignature,
            currentSignature,
            _ =>
            {
                Configuration.Save();
                afterSave?.Invoke();
            },
            static (left, right) => string.Equals(left, right, StringComparison.Ordinal));
    }

    public void QueueDebouncedPlannerOptionsSave(
        string key,
        string committedSignature,
        Func<string> currentSignature)
    {
        QueueDebouncedUiWrite(
            $"planner-options:{key}",
            committedSignature,
            currentSignature,
            _ => SavePlannerOptions(),
            static (left, right) => string.Equals(left, right, StringComparison.Ordinal));
    }

    public void QueueDebouncedPlannerGroupTouch(
        DadPlannerGroup group,
        string key,
        string committedSignature,
        Func<DadPlannerGroup, string> currentSignature)
    {
        var groupId = group.GroupId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(groupId))
            return;

        QueueDebouncedUiWrite(
            $"planner-group:{groupId}:{key}",
            committedSignature,
            () =>
            {
                var currentGroup = ResolvePlannerGroup(groupId);
                return currentGroup == null ? committedSignature : currentSignature(currentGroup);
            },
            _ =>
            {
                var currentGroup = ResolvePlannerGroup(groupId);
                if (currentGroup != null)
                    TouchPlannerGroup(currentGroup);
            },
            static (left, right) => string.Equals(left, right, StringComparison.Ordinal));
    }

    public void QueueDebouncedPlannerGroupSlotTouch(
        DadPlannerGroup group,
        DadPlannerGroupSlot slot,
        string key,
        string committedSignature,
        Func<DadPlannerGroupSlot, string> currentSignature)
    {
        var groupId = group.GroupId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(groupId))
            return;

        QueueDebouncedUiWrite(
            $"planner-group:{groupId}:slot:{key}",
            committedSignature,
            () =>
            {
                var currentGroup = ResolvePlannerGroup(groupId);
                return currentGroup == null || !currentGroup.Slots.Any(candidate => ReferenceEquals(candidate, slot))
                    ? committedSignature
                    : currentSignature(slot);
            },
            _ =>
            {
                var currentGroup = ResolvePlannerGroup(groupId);
                if (currentGroup != null && currentGroup.Slots.Any(candidate => ReferenceEquals(candidate, slot)))
                    TouchPlannerGroup(currentGroup);
            },
            static (left, right) => string.Equals(left, right, StringComparison.Ordinal));
    }

    public void QueueDebouncedLaunchProfileUpdate(
        DadLaunchProfile profile,
        string committedSignature,
        Func<DadLaunchProfile, string> currentSignature,
        Action<string>? setStatus = null)
    {
        var profileId = profile.ProfileId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(profileId))
            return;

        var expectedRevision = profile.Revision;
        QueueDebouncedUiWrite(
            $"launch-profile:{profileId}",
            committedSignature,
            () => currentSignature(profile),
            _ =>
            {
                var ack = SchedulerService.UpdateLaunchProfile(new DadLaunchProfileUpdateRequest
                {
                    ExpectedRevision = expectedRevision,
                    Profile = profile,
                });
                setStatus?.Invoke(ack.Summary);
            },
            static (left, right) => string.Equals(left, right, StringComparison.Ordinal));
    }

    public string GetAccountAliasEditValue(DadAccountKey accountKey, string persistedAlias)
    {
        var key = NormalizeDebouncedAccountKey(accountKey);
        return !string.IsNullOrWhiteSpace(key) && pendingAccountAliasDrafts.TryGetValue(key, out var draft)
            ? draft
            : persistedAlias;
    }

    public void QueueDebouncedAccountAliasEdit(DadAccountKey accountKey, string persistedAlias, string alias)
    {
        var key = NormalizeDebouncedAccountKey(accountKey);
        if (string.IsNullOrWhiteSpace(key))
            return;

        pendingAccountAliasDrafts[key] = alias;
        QueueDebouncedUiWrite(
            $"account-alias:{key}",
            NormalizeAccountAlias(persistedAlias),
            () => pendingAccountAliasDrafts.TryGetValue(key, out var draft)
                ? NormalizeAccountAlias(draft)
                : NormalizeAccountAlias(persistedAlias),
            _ =>
            {
                if (!pendingAccountAliasDrafts.TryGetValue(key, out var draft))
                    return;

                var account = ConfigManager.GetAccount(new DadAccountKey(key));
                if (account == null)
                {
                    pendingAccountAliasDrafts.Remove(key);
                    return;
                }

                var normalized = NormalizeAccountAlias(draft);
                if (string.Equals(NormalizeAccountAlias(account.AccountAlias), normalized, StringComparison.Ordinal))
                {
                    pendingAccountAliasDrafts.Remove(key);
                    return;
                }

                if (ConfigManager.UpdateAccountAlias(new DadAccountKey(key), normalized))
                {
                    RosterCatalogService.RefreshCatalog(CharacterIntelligenceService.CurrentPool, new DadRosterRefreshPlan
                    {
                        IncludeHidden = true,
                        IncludeIgnored = true,
                        StaleAfterHours = (Configuration.RosterCatalog ??= new DadRosterCatalogConfiguration()).StaleAfterHours,
                    });
                }

                pendingAccountAliasDrafts.Remove(key);
            },
            static (left, right) => string.Equals(left, right, StringComparison.Ordinal));
    }

    private void DropDebouncedUiWrite(string key)
    {
        key = key?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(key))
            debouncedUiWrites.Remove(key);
    }

    private void DropDebouncedUiWrites(string prefix)
    {
        prefix ??= string.Empty;
        foreach (var key in debouncedUiWrites.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            debouncedUiWrites.Remove(key);
    }

    private void DropDebouncedAccountAlias(DadAccountKey accountKey)
    {
        var key = NormalizeDebouncedAccountKey(accountKey);
        if (string.IsNullOrWhiteSpace(key))
            return;

        pendingAccountAliasDrafts.Remove(key);
        DropDebouncedUiWrite($"account-alias:{key}");
    }

    private void ClearDebouncedAccountAliases()
    {
        pendingAccountAliasDrafts.Clear();
        DropDebouncedUiWrites("account-alias:");
    }

    private void FlushDebouncedUiWrites(bool force)
    {
        if (debouncedUiWrites.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var pair in debouncedUiWrites.ToArray())
        {
            if (!force && pair.Value.DueAtUtc > now)
                continue;
            if (!debouncedUiWrites.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, pair.Value))
                continue;

            try
            {
                current.Commit();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[dad] Debounced UI write failed for {Key}.", pair.Key);
            }
            finally
            {
                debouncedUiWrites.Remove(pair.Key);
            }
        }
    }

    private static string NormalizeDebouncedAccountKey(DadAccountKey accountKey)
        => accountKey.Value?.Trim() ?? string.Empty;

    private static string NormalizeAccountAlias(string alias)
        => string.IsNullOrWhiteSpace(alias) ? "Account" : alias.Trim();

    private void EnsureClientAccountId()
    {
        var clientAccountId = Configuration.ClientAccountId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(clientAccountId))
        {
            if (!string.Equals(Configuration.ClientAccountId, clientAccountId, StringComparison.Ordinal))
            {
                Configuration.ClientAccountId = clientAccountId;
                Configuration.Save();
            }

            return;
        }

        Configuration.ClientAccountId = $"dad-client-{Guid.NewGuid():N}";
        Configuration.Save();
        Log.Information("[dad] Generated client account id {ClientAccountId}.", Configuration.ClientAccountId);
    }

    public bool DeleteDadAccount(DadAccountKey accountKey)
    {
        var account = ConfigManager.GetAccount(accountKey);
        var resolvedAccountKey = new DadAccountKey(account?.AccountId ?? accountKey.Value);
        if (resolvedAccountKey.IsEmpty)
            return false;

        DropDebouncedAccountAlias(resolvedAccountKey);
        DropDebouncedUiWrites("launch-profile:");
        var deletedConfig = ConfigManager.DeleteAccount(resolvedAccountKey);
        if (account != null && !deletedConfig)
            return false;

        var purgedRoster = RosterCatalogService.PurgeAccount(resolvedAccountKey);
        var clearedLastAccount = false;
        if (string.Equals(Configuration.LastAccountId, resolvedAccountKey.Value, StringComparison.OrdinalIgnoreCase))
        {
            Configuration.LastAccountId = string.Empty;
            clearedLastAccount = true;
        }

        if (clearedLastAccount)
            Configuration.Save();

        if (!deletedConfig && !purgedRoster && !clearedLastAccount)
            return false;

        PresenceService.Update(CharacterIntelligenceService.CurrentPool, TransportService.CurrentTransport.ListenerEndpoint);
        RosterCatalogService.RefreshCatalog(CharacterIntelligenceService.CurrentPool, new DadRosterRefreshPlan
        {
            IncludeHidden = true,
            IncludeIgnored = true,
            StaleAfterHours = Configuration.RosterCatalog.StaleAfterHours,
        });
        return true;
    }

    public bool MergeDadAccountIntoCurrent(DadAccountKey sourceAccountKey)
    {
        var sourceAccount = ConfigManager.GetAccount(sourceAccountKey);
        var targetAccount = ConfigManager.GetCurrentAccount();
        if (sourceAccount == null || targetAccount == null)
            return false;

        var sourceKey = new DadAccountKey(sourceAccount.AccountId);
        var targetKey = new DadAccountKey(targetAccount.AccountId);
        if (sourceKey.IsEmpty || targetKey.IsEmpty ||
            DadRosterIdentity.SameAccount(sourceKey, targetKey))
        {
            return false;
        }

        if (!ConfigManager.MergeAccountInto(sourceKey, targetKey))
            return false;

        DropDebouncedAccountAlias(sourceKey);
        DropDebouncedUiWrites("launch-profile:");
        RosterCatalogService.MergeAccount(sourceKey, targetKey, targetAccount.AccountAlias);
        Configuration.LastAccountId = targetAccount.AccountId;
        Configuration.Save();

        PresenceService.Update(CharacterIntelligenceService.CurrentPool, TransportService.CurrentTransport.ListenerEndpoint);
        RosterCatalogService.RefreshCatalog(CharacterIntelligenceService.CurrentPool, new DadRosterRefreshPlan
        {
            IncludeHidden = true,
            IncludeIgnored = true,
            StaleAfterHours = Configuration.RosterCatalog.StaleAfterHours,
        });
        return true;
    }

    public DadAccountDataClearResult ClearAllDadAccountData()
    {
        ClearDebouncedAccountAliases();
        DropDebouncedUiWrites("launch-profile:");
        var result = ConfigManager.ClearAllAccounts();
        result.Merge(RosterCatalogService.ClearAccountData());

        result.LastAccountIdCleared = !string.IsNullOrWhiteSpace(Configuration.LastAccountId);
        Configuration.LastAccountId = string.Empty;
        if (!string.IsNullOrWhiteSpace(Configuration.ClientAccountId))
            ConfigManager.EnsureAccountSelected(Configuration.ClientAccountId, "Dad client");
        result.PlannerAccountRefsCleared = ClearPlannerOptionsAccountData(Configuration.PlannerOptions);
        result.PlannerGroupSlotRefsCleared = ClearPlannerGroupAccountData(Configuration.PlannerGroups);
        result.LaunchProfileRefsCleared = ClearLaunchProfileAccountData(Configuration.LaunchProfiles);
        result.SchedulerJobsCleared = SchedulerService.ClearAccountData();

        Configuration.Save();
        InvalidatePlannerPreviewCache("account data cleared");

        var pool = CharacterIntelligenceService.RefreshLocalCharacterPool("account-reset", logRefresh: false);
        PresenceService.Update(pool, TransportService.CurrentTransport.ListenerEndpoint);
        RosterCatalogService.RefreshCatalog(pool, new DadRosterRefreshPlan
        {
            IncludeHidden = true,
            IncludeIgnored = true,
            StaleAfterHours = Configuration.RosterCatalog.StaleAfterHours,
        });

        Log.Information("[dad] {Summary}", result.ToStatusMessage());
        return result;
    }

    private static int ClearPlannerOptionsAccountData(DadPresetPlannerOptions options)
    {
        options.IncludedAccountKeys ??= [];
        var cleared = options.IncludedAccountKeys.Count(static key => !key.IsEmpty);
        options.IncludedAccountKeys.Clear();
        return cleared;
    }

    private static int ClearPlannerGroupAccountData(List<DadPlannerGroup> groups)
    {
        groups ??= [];
        var cleared = 0;
        var now = DateTime.UtcNow;
        foreach (var group in groups)
        {
            group.Slots ??= [];
            var groupChanged = false;
            foreach (var slot in group.Slots)
            {
                if (!slot.RequiredAccountKey.IsEmpty)
                {
                    slot.RequiredAccountKey = new DadAccountKey(string.Empty);
                    cleared++;
                    groupChanged = true;
                }

                if (!slot.RequiredCharacterKey.IsEmpty)
                {
                    slot.RequiredCharacterKey = new DadCharacterKey(string.Empty);
                    cleared++;
                    groupChanged = true;
                }

                if (!string.IsNullOrWhiteSpace(slot.LaunchProfileId))
                {
                    slot.LaunchProfileId = string.Empty;
                    cleared++;
                    groupChanged = true;
                }
            }

            if (groupChanged)
                group.UpdatedAtUtc = now;
        }

        return cleared;
    }

    private static int ClearLaunchProfileAccountData(List<DadLaunchProfile> launchProfiles)
    {
        launchProfiles ??= [];
        var cleared = 0;
        foreach (var profile in launchProfiles)
        {
            profile.Normalize();
            if (!profile.AccountKey.IsEmpty)
            {
                profile.AccountKey = new DadAccountKey(string.Empty);
                cleared++;
            }

            cleared += profile.ExpectedCharacterKeys.Count(static key => !key.IsEmpty);
            profile.ExpectedCharacterKeys.Clear();
        }

        return cleared;
    }

    public void ApplyEndpointConfiguration(bool endpointChanged)
    {
        if (!endpointChanged)
            return;

        TransportService.RestartTransport();
        ResetAuthorityCache(clearFreshness: false);
        lock (authorityCacheGate)
            suppressRemoteAuthorityRefreshUntilUtc = DateTime.UtcNow + EndpointApplyAuthorityRefreshSuppression;
    }

    public bool SetRunAsServerDad(bool runAsServerDad)
    {
        if (Configuration.RunAsServerDad == runAsServerDad)
            return false;

        Configuration.RunAsServerDad = runAsServerDad;
        Configuration.Save();
        ApplyTransportRoleConfiguration();
        return true;
    }

    public bool ApplyTransportEndpoint(string host, int port)
    {
        host = NormalizeTransportHost(host);
        port = NormalizeTransportPort(port);
        var endpointChanged = Configuration.RunAsServerDad
            ? !string.Equals(Configuration.ServerListenHost, host, StringComparison.Ordinal) ||
              Configuration.ServerListenPort != port
            : !string.Equals(Configuration.ServerDadHost, host, StringComparison.Ordinal) ||
              Configuration.ServerDadPort != port;
        if (!endpointChanged)
            return false;

        if (Configuration.RunAsServerDad)
        {
            Configuration.ServerListenHost = host;
            Configuration.ServerListenPort = port;
        }
        else
        {
            Configuration.ServerDadHost = host;
            Configuration.ServerDadPort = port;
        }

        Configuration.Save();
        ApplyEndpointConfiguration(endpointChanged: true);
        return true;
    }

    public bool SetTransportSharedSecret(string sharedSecret)
    {
        sharedSecret = (sharedSecret ?? string.Empty).Trim();
        if (string.Equals(Configuration.TransportSharedSecret, sharedSecret, StringComparison.Ordinal))
            return false;

        Configuration.TransportSharedSecret = sharedSecret;
        Configuration.Save();
        ApplyTransportRoleConfiguration();
        return true;
    }

    public string GenerateAndApplyTransportSharedSecret()
    {
        var sharedSecret = GenerateTransportSharedSecret();
        SetTransportSharedSecret(sharedSecret);
        return sharedSecret;
    }

    private static string GenerateTransportSharedSecret()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string NormalizeTransportHost(string host)
        => string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();

    private static int NormalizeTransportPort(int port)
        => Math.Clamp(port, 1, 65535);

    public void ApplyTransportRoleConfiguration()
    {
        TransportService.RestartTransport();
        PresenceService.Update(CharacterIntelligenceService.CurrentPool, string.Empty);
        ResetAuthorityCache(clearFreshness: true);
        lock (authorityCacheGate)
            suppressRemoteAuthorityRefreshUntilUtc = DateTime.UtcNow + EndpointApplyAuthorityRefreshSuppression;
    }

    public DadActivityPreset BuildPlannerPreview()
    {
        var pool = BuildPlannerPool();
        return PresetProviderService.BuildPlannerPreview(pool, PlannerOptions, GetSelectedPlannerGroup());
    }

    public string BuildPlannerSummary()
        => BuildPlannerPreview().PlannerSummary;

    public DadPlannerRunRequestPreview BuildPlannerRunRequestPreview()
    {
        var pool = BuildPlannerPool();
        var selectedGroup = GetSelectedPlannerGroup();
        var plannerPreview = PresetProviderService.BuildPlannerPreview(pool, PlannerOptions, selectedGroup);
        return BuildPlannerRunRequestPreview(pool, PlannerOptions, plannerPreview, selectedGroup, useStableIdentity: true);
    }

    public DadPlannerRunRequestPreview BuildPlannerRunRequestPreview(
        DadPresetPlannerOptions options,
        DadActivityPreset? plannerPreviewOverride = null,
        DadPlannerGroup? selectedGroup = null)
    {
        var pool = BuildPlannerPool();
        var plannerPreview = plannerPreviewOverride ?? PresetProviderService.BuildPlannerPreview(pool, options, selectedGroup);
        return BuildPlannerRunRequestPreview(pool, options, plannerPreview, selectedGroup, useStableIdentity: false);
    }

    private DadPlannerRunRequestPreview BuildPlannerRunRequestPreview(
        DadCharacterPool pool,
        DadPresetPlannerOptions options,
        DadActivityPreset plannerPreview,
        DadPlannerGroup? selectedGroup,
        bool useStableIdentity)
    {
        string? requestId = null;
        DateTime? requestedAtUtc = null;
        if (useStableIdentity)
        {
            var signature = BuildPlannerPreviewSignature(options, plannerPreview);
            var identity = ResolvePlannerPreviewIdentity(signature);
            requestId = identity.RequestId;
            requestedAtUtc = identity.RequestedAtUtc;
        }

        var requestPreview = PresetProviderService.BuildPlannerRunRequestPreview(
            pool,
            options,
            requestId,
            requestedAtUtc,
            plannerPreviewOverride: plannerPreview,
            selectedGroup: selectedGroup,
            completionFallback: Configuration.CompletionActions);
        return ApplyPlannerRuntimeTruth(requestPreview, pool, selectedGroup);
    }

    public string BuildPlannerRequestJson()
    {
        var requestPreview = BuildPlannerRunRequestPreview();
        return requestPreview.RequestJson;
    }

    public void SavePlannerOptions()
    {
        Configuration.Save();
        InvalidatePlannerPreviewCache("planner options saved");
    }

    public string GetShareMutationBlocker()
        => DadShareService.GetMutationBlocker(
            IsBusy(GetVisibleRunState().VisibleRun),
            SchedulerService.CurrentState.IsActive,
            Configuration.ActiveScheduleRun?.IsActive == true);

    public bool TryExportSelectedPlan(out string encoded, out string error)
    {
        var selected = GetSelectedPlannerGroup();
        if (selected == null)
        {
            encoded = string.Empty;
            error = "Select a saved Plan before exporting.";
            return false;
        }

        return ShareService.TryExportPlan(
            selected,
            BuildShareKnownIdentities(),
            Configuration.CompletionActions,
            out encoded,
            out error);
    }

    public bool TryExportSchedule(string scheduleId, out string encoded, out string error)
    {
        var matches = Configuration.Schedules
            .Where(schedule => string.Equals(schedule.ScheduleId, scheduleId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1)
        {
            encoded = string.Empty;
            error = matches.Count == 0
                ? "Select a saved Schedule before exporting."
                : "Schedule ID is duplicated; repair it before export.";
            return false;
        }

        return ShareService.TryExportSchedule(
            matches[0],
            Configuration.PlannerGroups,
            BuildShareKnownIdentities(),
            Configuration.CompletionActions,
            out encoded,
            out error);
    }

    public bool TryDecodeShare(
        string encoded,
        string expectedKind,
        out DadShareEnvelopeDto? envelope,
        out string error)
        => ShareService.TryDecode(encoded, expectedKind, out envelope, out error);

    public DadShareApplyResult ApplyShareImport(DadShareEnvelopeDto envelope)
        => ApplyShareEnvelope(envelope, DadShareApplyMode.ReplaceMatching);

    public DadShareApplyResult InstallStarterShareBundle()
    {
        var blocker = GetShareMutationBlocker();
        if (!string.IsNullOrWhiteSpace(blocker))
            return new DadShareApplyResult { Summary = blocker };
        if (!DadStarterShareBundle.TryCreateEncoded(ShareService, out var encoded, out var error))
            return new DadShareApplyResult { Summary = error };
        if (!ShareService.TryDecode(encoded, DadShareConstants.ScheduleKind, out var envelope, out error) || envelope == null)
            return new DadShareApplyResult { Summary = error };
        return ApplyShareEnvelope(envelope, DadShareApplyMode.SkipExisting);
    }

    public DadShareRenameResult RenamePlanId(string currentId, string requestedId)
    {
        var blocker = GetShareMutationBlocker();
        if (!string.IsNullOrWhiteSpace(blocker))
            return new DadShareRenameResult { Summary = blocker };

        var result = ShareService.RenamePlanId(
            Configuration.PlannerGroups,
            Configuration.Schedules,
            Configuration.SchedulerQueue,
            PlannerOptions,
            currentId,
            requestedId);
        if (!result.Success)
            return result;

        DropDebouncedUiWrites($"planner-group:{currentId}:");
        Configuration.Save();
        InvalidatePlannerPreviewCache("Plan ID changed");
        return result;
    }

    public DadShareRenameResult RenameScheduleId(string currentId, string requestedId)
    {
        var blocker = GetShareMutationBlocker();
        if (!string.IsNullOrWhiteSpace(blocker))
            return new DadShareRenameResult { Summary = blocker };

        var result = ShareService.RenameScheduleId(
            Configuration.Schedules,
            Configuration.SchedulerQueue,
            Configuration.ActiveScheduleRun,
            currentId,
            requestedId);
        if (!result.Success)
            return result;

        Configuration.Save();
        InvalidatePlannerPreviewCache("Schedule ID changed");
        return result;
    }

    private DadShareApplyResult ApplyShareEnvelope(
        DadShareEnvelopeDto envelope,
        DadShareApplyMode mode)
    {
        var blocker = GetShareMutationBlocker();
        if (!string.IsNullOrWhiteSpace(blocker))
            return new DadShareApplyResult { Summary = blocker };

        var result = ShareService.Apply(
            envelope,
            Configuration.PlannerGroups,
            Configuration.Schedules,
            mode);
        if (!result.Success)
            return result;

        var changed = result.AddedPlanCount > 0 ||
                      result.ReplacedPlanCount > 0 ||
                      result.ScheduleAdded ||
                      result.ScheduleReplaced;
        if (!changed)
            return result;

        // Replacement invalidates delayed callbacks that captured the old Plan
        // objects. Leave unrelated account/profile/config drafts alone.
        var transferredPlanIds = envelope.Kind == DadShareConstants.PlanKind
            ? new[] { envelope.Plan!.GroupId }
            : envelope.Plans.Select(static plan => plan.GroupId);
        var replacedPlanIds = mode == DadShareApplyMode.ReplaceMatching
            ? transferredPlanIds.Where(planId => Configuration.PlannerGroups.Any(plan => string.Equals(
                plan.GroupId,
                planId,
                StringComparison.OrdinalIgnoreCase)))
            : [];
        foreach (var planId in replacedPlanIds)
            DropDebouncedUiWrites($"planner-group:{planId}:");
        if (envelope.Kind == DadShareConstants.PlanKind)
            DropDebouncedUiWrites("planner-options:");
        Configuration.PlannerGroups = result.PlannerGroups;
        Configuration.Schedules = result.Schedules;
        if (envelope.Kind == DadShareConstants.PlanKind)
        {
            var selected = Configuration.PlannerGroups.Single(plan =>
                string.Equals(plan.GroupId, result.ResultId, StringComparison.OrdinalIgnoreCase));
            ApplyPlannerGroupDefaults(selected, PlannerOptions);
        }
        else if (mode == DadShareApplyMode.ReplaceMatching &&
                 envelope.Plans.Any(plan => string.Equals(
                     plan.GroupId,
                     PlannerOptions.SelectedPlannerGroupId,
                     StringComparison.OrdinalIgnoreCase)))
        {
            var selected = Configuration.PlannerGroups.Single(plan => string.Equals(
                plan.GroupId,
                PlannerOptions.SelectedPlannerGroupId,
                StringComparison.OrdinalIgnoreCase));
            ApplyPlannerGroupDefaults(selected, PlannerOptions);
        }

        Configuration.Save();
        InvalidatePlannerPreviewCache(mode == DadShareApplyMode.SkipExisting
            ? "starter bundle installed"
            : "share imported");
        return result;
    }

    private IReadOnlyList<DadShareKnownIdentity> BuildShareKnownIdentities()
        => CharacterIntelligenceService.CurrentPool.Characters
            .Select(static character => new DadShareKnownIdentity
            {
                AccountKey = character.AccountId,
                AccountAlias = character.AccountAlias,
                CharacterKey = character.CharacterKey,
                CharacterName = character.CharacterName,
            })
            .ToList();

    public DadCharacterPool BuildPlannerPool()
        => RosterCatalogService.BuildCuratedPool(CharacterIntelligenceService.CurrentPool);

    internal DadPlannerUiSnapshot GetPlannerUiSnapshot(DadVisibleRunState runState)
    {
        var sourcePool = CharacterIntelligenceService.CurrentPool;
        var schedulerRevision = SchedulerService.GetPlannerUiRevision();
        var runRevisionToken = BuildPlannerRunRevisionToken(runState);
        var cacheKey = BuildPlannerUiCacheKey(sourcePool, schedulerRevision.LaunchProfilesToken, runRevisionToken);
        Stopwatch? stopwatch = null;
        var rebuildReason = string.Empty;

        if (cachedPlannerUiSnapshot == null || cachedPlannerUiCacheKey != cacheKey)
        {
            plannerUiCacheMissCount++;
            stopwatch = Stopwatch.StartNew();
            rebuildReason = string.IsNullOrWhiteSpace(plannerUiCacheInvalidationReason)
                ? cachedPlannerUiSnapshot == null ? "cold" : "planner inputs changed"
                : plannerUiCacheInvalidationReason;

            var rosterSnapshot = RosterCatalogService.BuildPlannerRosterSnapshot(sourcePool);
            var pool = rosterSnapshot.CuratedPool;
            var selectedGroup = GetSelectedPlannerGroup();
            var selectedDuty = PresetProviderService.GetPlannerSelectedDuty(PlannerOptions);
            var selectedRoulette = PresetProviderService.GetPlannerSelectedRoulette(PlannerOptions);
            var plannerPreview = PresetProviderService.BuildPlannerPreview(pool, PlannerOptions, selectedGroup);
            var requestPreview = BuildPlannerRunRequestPreview(pool, PlannerOptions, plannerPreview, selectedGroup, useStableIdentity: true);
            var launchProfiles = SchedulerService.GetLaunchProfiles()
                .OrderBy(static profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var groups = Configuration.PlannerGroups
                .OrderBy(static group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static group => group.GroupId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var lanePreviews = Configuration.DebugUiEnabled
                ? BuildPlannerLanePreviews(pool, requestPreview)
                : [];

            cachedPlannerUiSnapshot = new DadPlannerUiSnapshot
            {
                Generation = plannerUiCacheGeneration,
                RebuiltAtUtc = DateTime.UtcNow,
                RebuildReason = rebuildReason,
                CuratedPool = pool,
                PlannerPreview = plannerPreview,
                RequestPreview = requestPreview,
                AccountOptions = rosterSnapshot.AccountOptions,
                LaunchProfiles = launchProfiles,
                PlannerGroups = groups,
                LanePreviews = lanePreviews,
                CharactersByAccountKey = BuildPlannerCharactersByAccountKey(pool),
                SelectedDuty = selectedDuty,
                RouletteOptions = PresetProviderService.GetPlannerRouletteOptions(),
                SelectedRoulette = selectedRoulette.Option,
            };
            cachedPlannerUiCacheKey = cacheKey;
            plannerUiCacheInvalidationReason = string.Empty;
        }
        else
        {
            plannerUiCacheHitCount++;
        }

        var schedulerCacheKey = new DadPlannerSchedulerCacheKey(
            plannerUiCacheGeneration,
            sourcePool.LastUpdatedUtc.Ticks,
            RosterCatalogService.CatalogVersion,
            schedulerRevision.SchedulerToken,
            schedulerRevision.LaunchProfilesToken,
            runRevisionToken);
        if (cachedPlannerSchedulerPreview == null || cachedPlannerSchedulerCacheKey != schedulerCacheKey)
        {
            plannerSchedulerCacheMissCount++;
            stopwatch ??= Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(rebuildReason))
                rebuildReason = "scheduler inputs changed";

            var selectedGroup = GetSelectedPlannerGroup();
            var schedulerRequestPreview = selectedGroup == null
                ? cachedPlannerUiSnapshot.RequestPreview
                : BuildPlannerGroupRunRequestPreview(cachedPlannerUiSnapshot.CuratedPool, selectedGroup.GroupId, null);
            cachedPlannerSchedulerPreview = SchedulerService.BuildPreview(selectedGroup, schedulerRequestPreview);
            cachedPlannerSchedulerCacheKey = schedulerCacheKey;
        }
        else
        {
            plannerSchedulerCacheHitCount++;
        }

        cachedPlannerUiSnapshot.SchedulerPreview = cachedPlannerSchedulerPreview;
        if (stopwatch != null)
        {
            stopwatch.Stop();
            RecordPlannerUiCacheRebuild(stopwatch.Elapsed, rebuildReason);
        }

        return cachedPlannerUiSnapshot;
    }

    private DadPlannerUiCacheKey BuildPlannerUiCacheKey(
        DadCharacterPool pool,
        int launchProfilesToken,
        int runRevisionToken)
        => new(
            plannerUiCacheGeneration,
            Configuration.DebugUiEnabled,
            Configuration.PluginEnabled,
            Configuration.LocalOnlyModeEnabled,
            (int)Configuration.CombatRotationMode,
            RosterCatalogService.CatalogVersion,
            pool.LastUpdatedUtc.Ticks,
            pool.Characters.Count,
            pool.PeerTransport.ConnectedPeerCount,
            pool.PeerTransport.KnownParticipants.Count,
            pool.PeerTransport.LastResponses.Count,
            pool.XadbStatus.SnapshotUtc?.Ticks ?? 0,
            launchProfilesToken,
            runRevisionToken);

    private static int BuildPlannerRunRevisionToken(DadVisibleRunState runState)
    {
        var hash = new HashCode();
        AddPlannerRunRevision(ref hash, runState.LocalRun);
        AddPlannerRunRevision(ref hash, runState.AuthorityRun);
        AddPlannerRunRevision(ref hash, runState.VisibleRun);
        hash.Add(runState.IsRemoteAuthorityView);
        hash.Add(runState.AuthorityView.Kind);
        hash.Add(runState.AuthorityView.HasRemoteAuthority);
        return hash.ToHashCode();
    }

    private static void AddPlannerRunRevision(ref HashCode hash, DadRunResult run)
    {
        hash.Add(run.RequestId, StringComparer.Ordinal);
        hash.Add(run.Status);
        hash.Add(run.Phase);
        hash.Add(run.ModuleId);
        hash.Add(run.CancellationState);
        hash.Add(run.CompletedTaskCount);
        hash.Add(run.ActiveTaskIndex);
        hash.Add(run.TotalTaskCount);
        hash.Add(run.ActiveTaskName, StringComparer.Ordinal);
        hash.Add(run.ActiveTaskStatus, StringComparer.Ordinal);
        hash.Add(run.BlockedReason, StringComparer.Ordinal);
        hash.Add(run.FailureReason, StringComparer.Ordinal);
        hash.Add(run.CompletedAtUtc?.Ticks ?? 0);
        hash.Add(run.CurrentExecutorStatus.ModuleId);
        hash.Add(run.CurrentExecutorStatus.Status);
        hash.Add(run.CurrentExecutorStatus.Phase);
        hash.Add(run.CurrentExecutorStatus.BlockedReason, StringComparer.Ordinal);
        hash.Add(run.Participants.Count);
        hash.Add(run.StepResults.Count);
    }

    private IReadOnlyList<DadPlannerLanePreviewSnapshot> BuildPlannerLanePreviews(
        DadCharacterPool pool,
        DadPlannerRunRequestPreview selectedRequestPreview)
    {
        var previews = new List<DadPlannerLanePreviewSnapshot>();
        foreach (var family in PresetProviderService.GetPlannerRunFamilies())
        {
            var lane = PlannerOptions.RunFamily == family
                ? PresetProviderService.GetPlannerLaneDefinition(PlannerOptions.ActivityMode)
                : PresetProviderService.GetPlannerLaneDefinition(PresetProviderService.GetDefaultPlannerSubmode(family));
            var selected = IsSamePlannerLane(PlannerOptions.ActivityMode, lane.ActivityMode);
            var laneRequestPreview = selected ? selectedRequestPreview : BuildPlannerLaneRequestPreview(pool, lane);
            previews.Add(new DadPlannerLanePreviewSnapshot(lane, selected, laneRequestPreview));
        }

        return previews;
    }

    private DadPlannerRunRequestPreview BuildPlannerLaneRequestPreview(DadCharacterPool pool, DadPlannerLaneDefinition lane)
    {
        var laneOptions = ClonePlannerOptionsForLane(PlannerOptions, lane);
        var lanePreview = PresetProviderService.BuildPlannerPreview(pool, laneOptions);
        return BuildPlannerRunRequestPreview(
            pool,
            laneOptions,
            lanePreview,
            selectedGroup: null,
            useStableIdentity: false);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<DadAcquiredCharacter>> BuildPlannerCharactersByAccountKey(DadCharacterPool pool)
    {
        var map = new Dictionary<string, List<DadAcquiredCharacter>>(StringComparer.OrdinalIgnoreCase);
        foreach (var character in pool.Characters
                     .Where(static character => character.RosterVisibility == DadRosterVisibility.Active && !character.NeedsRosterUpdate)
                     .OrderBy(static character => character.CharacterKey, StringComparer.OrdinalIgnoreCase))
        {
            AddPlannerCharacterKey(map, character.AccountId, character);
            AddPlannerCharacterKey(map, character.AccountAlias, character);
        }

        return map.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<DadAcquiredCharacter>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AddPlannerCharacterKey(
        Dictionary<string, List<DadAcquiredCharacter>> map,
        string accountKey,
        DadAcquiredCharacter character)
    {
        var key = accountKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!map.TryGetValue(key, out var characters))
        {
            characters = [];
            map[key] = characters;
        }

        if (characters.Any(existing =>
                string.Equals(existing.CharacterKey, character.CharacterKey, StringComparison.OrdinalIgnoreCase) &&
                existing.ContentId == character.ContentId))
        {
            return;
        }

        characters.Add(character);
    }

    public void InvalidatePlannerPreviewCache(string reason)
    {
        plannerUiCacheGeneration++;
        plannerUiCacheInvalidationReason = string.IsNullOrWhiteSpace(reason) ? "explicit" : reason.Trim();
        cachedPlannerUiSnapshot = null;
        cachedPlannerUiCacheKey = null;
        cachedPlannerSchedulerPreview = null;
        cachedPlannerSchedulerCacheKey = null;
        InvalidatePlannerPreviewIdentity();
    }

    internal DadPlannerUiCacheStats GetPlannerUiCacheStats()
        => new()
        {
            Generation = plannerUiCacheGeneration,
            HitCount = plannerUiCacheHitCount,
            MissCount = plannerUiCacheMissCount,
            SchedulerHitCount = plannerSchedulerCacheHitCount,
            SchedulerMissCount = plannerSchedulerCacheMissCount,
            LastRebuildMilliseconds = plannerUiCacheLastRebuildMilliseconds,
            MaxRebuildMilliseconds = plannerUiCacheMaxRebuildMilliseconds,
            LastRebuiltAtUtc = plannerUiCacheLastRebuiltAtUtc,
            LastRebuildReason = plannerUiCacheLastRebuildReason,
        };

    private void RecordPlannerUiCacheRebuild(TimeSpan elapsed, string reason)
    {
        plannerUiCacheLastRebuildMilliseconds = elapsed.TotalMilliseconds;
        plannerUiCacheMaxRebuildMilliseconds = Math.Max(plannerUiCacheMaxRebuildMilliseconds, elapsed.TotalMilliseconds);
        plannerUiCacheLastRebuiltAtUtc = DateTime.UtcNow;
        plannerUiCacheLastRebuildReason = string.IsNullOrWhiteSpace(reason) ? "inputs changed" : reason;

        if (elapsed < PlannerUiCacheSlowRebuildThreshold)
            return;

        var now = DateTime.UtcNow;
        if (now - lastSlowPlannerUiCacheLogUtc < PlannerUiCacheSlowRebuildLogCooldown)
            return;

        lastSlowPlannerUiCacheLogUtc = now;
        Log.Debug(
            "[dad] Planner UI cache rebuild took {ElapsedMs} ms ({Reason}, generation {Generation}).",
            elapsed.TotalMilliseconds,
            reason,
            plannerUiCacheGeneration);
    }

    private DadPresetPlannerOptions ClonePlannerOptionsForLane(
        DadPresetPlannerOptions source,
        DadPlannerLaneDefinition lane)
        => new()
        {
            PresetName = source.PresetName,
            RunFamily = lane.RunFamily,
            ActivityMode = lane.ActivityMode,
            ActivityName = lane.DisplayName,
            OperatorMode = source.OperatorMode,
            ConnectedOnly = source.ConnectedOnly,
            SameDatacenterOnly = source.SameDatacenterOnly,
            AllowStaleForPlanning = source.AllowStaleForPlanning,
            TransportOwner = lane.DefaultTransportOwner,
            QueueAuthority = lane.DefaultQueueAuthority,
            InviteAuthority = DadInviteAuthority.PresetLeader,
            DutyContentFinderConditionId = source.DutyContentFinderConditionId,
            DutyDisplayName = source.DutyDisplayName,
            DutyUnsynced = source.DutyUnsynced,
            DutyExpectedPartySize = source.DutyExpectedPartySize,
            RouletteTarget = source.RouletteTarget?.Clone() ?? new DadQueueTarget { Kind = DadQueueTargetKind.Roulette },
            MogtomePreset = source.MogtomePreset,
            MogtomeDutyPolicy = source.MogtomeDutyPolicy,
            RefreshTrustNpcLevels = source.RefreshTrustNpcLevels,
            StopPolicy = source.StopPolicy.Clone(),
            CompletionActions = source.CompletionActions?.Clone(),
            IncludedAccountKeys = [..source.IncludedAccountKeys],
        };

    private static bool IsSamePlannerLane(DadPlannerActivityMode selectedMode, DadPlannerActivityMode laneMode)
        => NormalizePlannerLane(selectedMode) == NormalizePlannerLane(laneMode);

    private static DadPlannerActivityMode NormalizePlannerLane(DadPlannerActivityMode activityMode)
        => activityMode switch
        {
            DadPlannerActivityMode.DutyPremade => DadPlannerActivityMode.PremadeDuty,
            _ => activityMode,
        };

    public DadPlannerGroup? GetSelectedPlannerGroup()
        => ResolvePlannerGroup(PlannerOptions.SelectedPlannerGroupId);

    public DadPlannerGroup? ResolvePlannerGroup(string groupIdOrName)
        // Review M9: use the same ambiguity-rejecting resolver as the IPC path so duplicate
        // GroupIds/DisplayNames return null instead of an arbitrary list-order pick (wrong roster).
        => TryResolvePlannerGroupForIpc(groupIdOrName, out var group, out _) ? group : null;

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
        InvalidatePlannerPreviewCache("planner group selection cleared");
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
        InvalidatePlannerPreviewCache("planner group selected");
        return true;
    }

    public DadPlannerGroup? SaveCurrentPlannerGroup(
        string displayName,
        out bool created,
        out string rejectionReason)
    {
        created = false;
        rejectionReason = string.Empty;

        var selected = GetSelectedPlannerGroup();
        if (selected == null && PlannerOptions.ActivityMode == DadPlannerActivityMode.Msq)
        {
            rejectionReason = DadLegacyActivityRules.MsqUnsupportedBlocker;
            return null;
        }
        DadAcquiredCharacter? localNpcRunner = null;
        if (selected == null &&
            PlannerOptions.ActivityMode is DadPlannerActivityMode.DutySupport
            or DadPlannerActivityMode.Trust
            or DadPlannerActivityMode.DutySupportLeveling
            or DadPlannerActivityMode.TrustLeveling)
        {
            var refreshedPool = CharacterIntelligenceService.RefreshLocalCharacterPool("planner-group-save", logRefresh: false);
            localNpcRunner = refreshedPool.Characters.FirstOrDefault(static character =>
                character.Source == DadCharacterSource.LocalRuntime &&
                character.IsLiveConnected);
            if (localNpcRunner == null)
            {
                rejectionReason = "Cannot save Duty Support/Trust preset: no ready local character is logged in.";
                return null;
            }

            RosterCatalogService.RefreshCatalog(refreshedPool);
        }

        var candidate = BuildPlannerGroupFromCurrentPlanner(
            displayName,
            localNpcRunner,
            includeSlots: selected == null);
        NormalizePlannerGroupForStorage(candidate);
        DadPlannerGroup savedGroup;
        if (selected == null)
        {
            Configuration.PlannerGroups.Add(candidate);
            savedGroup = candidate;
            created = true;
        }
        else
        {
            DadPlannerGroupUpdateRules.ApplyPlannerFields(selected, candidate, DateTime.UtcNow);
            savedGroup = selected;
        }

        PlannerOptions.SelectedPlannerGroupId = savedGroup.GroupId;
        PlannerOptions.StopPolicy = savedGroup.StopPolicy.Clone();
        PlannerOptions.CompletionActions = savedGroup.CompletionActions?.Clone();
        PlannerOptions.RouletteTarget = savedGroup.RouletteTarget?.Clone() ?? new DadQueueTarget { Kind = DadQueueTargetKind.Roulette };
        PlannerOptions.IncludedAccountKeys = DadPlannerSlotRules.NormalizeGroupSlots(savedGroup.Slots)
            .Select(static slot => slot.RequiredAccountKey)
            .Where(static key => !key.IsEmpty)
            .DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Configuration.Save();
        InvalidatePlannerPreviewCache(created ? "planner group created" : "planner group updated");
        return savedGroup;
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
        NormalizePlannerGroupForStorage(duplicate);
        Configuration.PlannerGroups.Add(duplicate);
        PlannerOptions.SelectedPlannerGroupId = duplicate.GroupId;
        Configuration.Save();
        InvalidatePlannerPreviewCache("planner group duplicated");
        return duplicate;
    }

    // Feature batch B: create a reusable template from the selected group (drops character bindings).
    public DadPlannerGroup? CreateTemplateFromSelectedPlannerGroup(string templateName)
    {
        var selected = GetSelectedPlannerGroup();
        if (selected == null)
            return null;

        var template = DadPresetTemplateService.CreateTemplateFrom(selected, templateName, DateTime.UtcNow);
        NormalizePlannerGroupForStorage(template);
        Configuration.PlannerGroups.Add(template);
        PlannerOptions.SelectedPlannerGroupId = template.GroupId;
        Configuration.Save();
        InvalidatePlannerPreviewCache("planner template created");
        return template;
    }

    // Feature batch B: instantiate the selected template into a concrete group, auto-assigning the
    // live roster to slots by role (no per-run character wiring).
    public DadPlannerGroup? InstantiateSelectedPlannerTemplate()
    {
        var selected = GetSelectedPlannerGroup();
        if (selected is not { IsTemplate: true })
            return null;

        var instance = DadPresetTemplateService.Instantiate(selected, BuildPlannerPool(), DateTime.UtcNow);
        NormalizePlannerGroupForStorage(instance);
        Configuration.PlannerGroups.Add(instance);
        PlannerOptions.SelectedPlannerGroupId = instance.GroupId;
        Configuration.Save();
        InvalidatePlannerPreviewCache("planner template instantiated");
        return instance;
    }

    public bool RenameSelectedPlannerGroup(string displayName)
    {
        var selected = GetSelectedPlannerGroup();
        if (selected == null || string.IsNullOrWhiteSpace(displayName))
            return false;

        selected.DisplayName = displayName.Trim();
        selected.UpdatedAtUtc = DateTime.UtcNow;
        Configuration.Save();
        InvalidatePlannerPreviewCache("planner group renamed");
        return true;
    }

    public bool DeleteSelectedPlannerGroup()
    {
        var selected = GetSelectedPlannerGroup();
        if (selected == null)
            return false;

        DropDebouncedUiWrites($"planner-group:{selected.GroupId}:");
        Configuration.PlannerGroups.Remove(selected);
        PlannerOptions.SelectedPlannerGroupId = string.Empty;
        Configuration.Save();
        InvalidatePlannerPreviewCache("planner group deleted");
        return true;
    }

    public void TouchPlannerGroup(DadPlannerGroup group)
    {
        NormalizePlannerGroupForStorage(group);
        group.UpdatedAtUtc = DateTime.UtcNow;
        Configuration.Save();
        InvalidatePlannerPreviewCache("planner group touched");
    }

    public void ReplaceSelectedPlannerGroupSlotsFromCurrentPreview()
    {
        var selected = GetSelectedPlannerGroup();
        if (selected == null)
            return;

        var preview = BuildPlannerPreview();
        selected.Slots = DadPlannerGroupUpdateRules.RefreshSlotsPreservingOperationalSettings(
            selected.Slots,
            BuildPlannerGroupSlotsFromPreview(preview));
        NormalizePlannerGroupForStorage(selected);
        selected.UpdatedAtUtc = DateTime.UtcNow;
        Configuration.Save();
        InvalidatePlannerPreviewCache("planner group slots replaced");
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
            if (!preview.CanStart)
            {
                var blockedReason = string.IsNullOrWhiteSpace(preview.BlockedReason)
                    ? preview.StatusSummary
                    : preview.BlockedReason;
                return DadIpcJson.Serialize(DadRunResult.Rejected(
                    preview.Request,
                    $"Planner group dry run blocked: {blockedReason}"));
            }

            var dryRunSummary = $"Planner group dry run ready: {preview.StatusSummary}";
            return DadIpcJson.Serialize(DadRunResult.FromRequest(preview.Request, DadRunStatus.Idle, dryRunSummary));
        }

        if (!preview.CanStart)
            return DadIpcJson.Serialize(DadRunResult.Rejected(preview.Request, preview.BlockedReason));

        return DadIpcJson.Serialize(RunCoordinatorService.StartTasks(preview.Request));
    }

    public string GetSchedulerPreviewJson()
        => DadIpcJson.Serialize(BuildSchedulerPreview());

    public DadSchedulerPreview BuildSchedulerPreview()
    {
        var selectedGroup = GetSelectedPlannerGroup();
        var requestPreview = selectedGroup == null
            ? BuildPlannerRunRequestPreview()
            : BuildPlannerGroupRunRequestPreview(selectedGroup.GroupId, null);
        return SchedulerService.BuildPreview(selectedGroup, requestPreview);
    }

    public string ValidateSelectedPlannerPresetReadOnly()
    {
        var selectedGroup = GetSelectedPlannerGroup();
        if (selectedGroup == null)
        {
            const string missing = "Select a saved preset before validating it.";
            PrintStatus(missing);
            return missing;
        }

        var pool = BuildPlannerPool();
        var requestPreview = BuildPlannerGroupRunRequestPreview(pool, selectedGroup.GroupId, null);
        var schedulerPreview = SchedulerService.BuildPreview(selectedGroup, requestPreview);
        var schedulerStatus = schedulerPreview.CanStart
            ? schedulerPreview.ReadyToStart
                ? $"ready: {schedulerPreview.StatusSummary}"
                : $"schedulable: {schedulerPreview.StatusSummary}"
            : $"blocked: {schedulerPreview.BlockedReason}";
        var plannerStatus = requestPreview.CanStart
            ? $"ready: {requestPreview.StatusSummary}"
            : requestPreview.CanSchedule
                ? $"schedulable: {requestPreview.ReadinessSummary}"
                : $"blocked: {requestPreview.BlockedReason}";
        var status = $"Validation for preset '{selectedGroup.DisplayName}' (read-only) | Planner {plannerStatus} | Scheduler {schedulerStatus}";
        PrintStatus(status);
        return status;
    }

    private bool CanAdvanceSchedulerQueue()
        => Configuration.RunAsServerDad &&
           (SchedulerService.CurrentState.IsActive || !IsBusy(GetVisibleRunState().VisibleRun));

    public string StartSchedulerPresetFromJson(string json)
    {
        var startRequest = DadIpcJson.Deserialize<DadSchedulerStartRequest>(json);
        if (startRequest == null)
        {
            var fallbackId = (json ?? string.Empty).Trim().Trim('"');
            startRequest = new DadSchedulerStartRequest { GroupId = fallbackId };
        }

        var groupId = string.IsNullOrWhiteSpace(startRequest.GroupId)
            ? PlannerOptions.SelectedPlannerGroupId
            : startRequest.GroupId;
        if (!TryResolvePlannerGroupForIpc(groupId, out var group, out var rejectionReason) || group == null)
        {
            return DadIpcJson.Serialize(DadRunResult.Rejected(null, rejectionReason));
        }

        if (!CanAdvanceSchedulerQueue())
            return DadIpcJson.Serialize(DadRunResult.Rejected(null, "Dad run active; enqueue scheduler preset instead of direct start."));

        var preview = BuildPlannerGroupRunRequestPreview(group.GroupId, new DadPlannerGroupStartRequest
        {
            GroupId = group.GroupId,
            RequestedBy = string.IsNullOrWhiteSpace(startRequest.RequestedBy)
                ? "scheduler"
                : startRequest.RequestedBy.Trim(),
        });
        if (preview.Request != null)
            preview.Request.RequestedBy = string.IsNullOrWhiteSpace(startRequest.RequestedBy)
                ? $"scheduler:{group.DisplayName}"
                : startRequest.RequestedBy.Trim();

        var schedulerRequestedBy = string.IsNullOrWhiteSpace(startRequest.RequestedBy)
            ? "scheduler"
            : startRequest.RequestedBy.Trim();
        var state = SchedulerService.StartPreset(group, preview, startRequest.DryRun, new DadScheduledCrewJob
        {
            JobType = DadSchedulerJobType.ScheduledPreset,
            GroupId = group.GroupId,
            PresetName = group.DisplayName,
            DryRun = startRequest.DryRun,
            RequestedBy = schedulerRequestedBy,
            CreatedAtUtc = DateTime.UtcNow,
        });
        if (!startRequest.DryRun && state.IsActive && CanAdvanceSchedulerQueue())
        {
            SchedulerService.UpdateWithScheduleRepeatBoundary(
                ResolvePlannerGroup,
                BuildSchedulerPlannerPreview,
                StartScheduledPlannerRequest);
            state = SchedulerService.CurrentState;
        }

        return DadIpcJson.Serialize(state.ToRunResult(preview.Request));
    }

    public string GetLaunchProfilesJson()
        => DadIpcJson.Serialize(SchedulerService.GetLaunchProfiles());

    public string GetProfileCatalogJson()
        => DadIpcJson.Serialize(ProfileDirectoryService.GetCatalogs());

    public string GetAccountDirectoryJson()
        => ProfileDirectoryService.GetAccountDirectoryJson();

    public string UpdateProfileFromJson(string json)
    {
        var request = DadIpcJson.Deserialize<DadProfileUpdateRequest>(json);
        return DadIpcJson.Serialize(request == null
            ? new DadProfileUpdateAck { Summary = "Unreadable profile update payload." }
            : ProfileDirectoryService.UpdateProfile(request));
    }

    public string UpdateLaunchProfileFromJson(string json)
    {
        var request = DadIpcJson.Deserialize<DadLaunchProfileUpdateRequest>(json);
        return DadIpcJson.Serialize(!Configuration.RunAsServerDad
            ? new DadLaunchProfileUpdateAck { Summary = "Only Dad Coordinator may update launch profiles." }
            : request == null
            ? new DadLaunchProfileUpdateAck { Summary = "Unreadable launch profile update payload." }
            : SchedulerService.UpdateLaunchProfile(request));
    }

    public string GetWorkerExecutionStatusJson()
        => DadIpcJson.Serialize(WorkerExecutionService.GetStatus());

    public string GetRosterCatalogJson()
        => DadIpcJson.Serialize(RosterCatalogService.RefreshCatalog(CharacterIntelligenceService.CurrentPool, new DadRosterRefreshPlan
        {
            IncludeHidden = Configuration.RosterCatalog.ShowHiddenInRoster,
            IncludeIgnored = Configuration.RosterCatalog.ShowHiddenInRoster,
            LogDiagnostics = true,
            DiagnosticsReason = "json local roster refresh",
        }));

    public string RefreshPeerRosterCatalogJson()
        => DadIpcJson.Serialize(RosterCatalogService.RefreshCatalog(
            CharacterIntelligenceService.CurrentPool,
            DadRosterRefreshPlan.ConnectedDads("json connected roster refresh")));

    public string SetRosterVisibilityFromJson(string json)
    {
        var request = DadIpcJson.Deserialize<DadRosterVisibilityChangeRequest>(json)
                      ?? new DadRosterVisibilityChangeRequest();
        return DadIpcJson.Serialize(RosterCatalogService.SetVisibility(request, CharacterIntelligenceService.CurrentPool));
    }

    public string ChangeRosterAssignmentFromJson(string json)
    {
        var request = DadIpcJson.Deserialize<DadRosterAssignmentChangeRequest>(json)
                      ?? new DadRosterAssignmentChangeRequest();
        return DadIpcJson.Serialize(RosterCatalogService.ChangeAssignment(request, CharacterIntelligenceService.CurrentPool));
    }

    public string EnqueueRosterUpdateFromJson(string json)
    {
        var plan = DadIpcJson.Deserialize<DadRosterRefreshPlan>(json) ?? new DadRosterRefreshPlan();
        plan.CharacterRefs ??= [];
        plan.AccountKeys ??= [];
        plan.CharacterKeys ??= [];
        var catalog = RosterCatalogService.RefreshCatalog(CharacterIntelligenceService.RequestPeerSnapshots(), new DadRosterRefreshPlan
        {
            ForcePeerRefresh = true,
            IncludeHidden = true,
            IncludeIgnored = true,
            CharacterRefs = plan.CharacterRefs.Select(static reference => reference.Clone()).ToList(),
            AccountKeys = [..plan.AccountKeys],
            CharacterKeys = [..plan.CharacterKeys],
            DryRun = plan.DryRun,
        });
        SchedulerService.EnqueueRosterUpdate(plan, catalog);
        if (CanAdvanceSchedulerQueue())
        {
            SchedulerService.UpdateWithScheduleRepeatBoundary(
                ResolvePlannerGroup,
                BuildSchedulerPlannerPreview,
                StartScheduledPlannerRequest);
        }
        return DadIpcJson.Serialize(SchedulerService.GetQueueSnapshot());
    }

    public string GetCrewStatusJson()
        => DadIpcJson.Serialize(new
        {
            roster = RosterCatalogService.CurrentCatalog,
            queue = SchedulerService.GetQueueSnapshot(),
            scheduler = SchedulerService.CurrentState,
        });

    public string GetSchedulerQueueJson()
        => DadIpcJson.Serialize(SchedulerService.GetQueueSnapshot());

    public string GetSchedulesJson()
        => DadIpcJson.Serialize(SchedulerService.GetScheduleSnapshot());

    public string StartScheduleFromJson(string json)
    {
        var request = DadIpcJson.Deserialize<DadScheduleStartRequest>(json);
        if (request == null)
        {
            var fallbackId = (json ?? string.Empty).Trim().Trim('"');
            request = new DadScheduleStartRequest { ScheduleId = fallbackId };
        }

        return DadIpcJson.Serialize(StartScheduleRunFromShell(
            request.ScheduleId,
            request.DryRun,
            string.IsNullOrWhiteSpace(request.RequestedBy) ? "ipc-schedule" : request.RequestedBy.Trim()));
    }

    public string CancelScheduleFromJson(string json)
    {
        var request = DadIpcJson.Deserialize<DadScheduleCancelRequest>(json);
        if (request == null)
        {
            var fallbackId = (json ?? string.Empty).Trim().Trim('"');
            request = new DadScheduleCancelRequest { RunId = fallbackId };
        }

        var cancelled = !string.IsNullOrWhiteSpace(request.RunId) && SchedulerService.CancelScheduleRun(
            request.RunId,
            string.IsNullOrWhiteSpace(request.Reason) ? "Schedule cancelled through IPC." : request.Reason.Trim());
        var snapshot = SchedulerService.GetScheduleSnapshot();
        return DadIpcJson.Serialize(new DadScheduleCancelResult
        {
            RunId = request.RunId,
            Cancelled = cancelled,
            Summary = cancelled
                ? $"Cancelled schedule run {request.RunId}."
                : $"No active schedule run matched {request.RunId}.",
            ActiveRun = snapshot.ActiveRun,
        });
    }

    public DadScheduleRunState StartScheduleRunFromShell(string scheduleId, bool dryRun, string requestedBy)
    {
        var state = SchedulerService.StartScheduleRun(scheduleId, dryRun, requestedBy);
        if (CanAdvanceSchedulerQueue())
        {
            SchedulerService.UpdateWithScheduleRepeatBoundary(
                ResolvePlannerGroup,
                BuildSchedulerPlannerPreview,
                StartScheduledPlannerRequest,
                () => GetVisibleRunState().VisibleRun);
            state = SchedulerService.GetScheduleSnapshot().ActiveRun;
        }

        PrintStatus(state.Summary);
        return state;
    }

    public bool CancelScheduleRunFromShell(string reason)
    {
        var cancelled = SchedulerService.CancelScheduleRun(reason);
        PrintStatus(cancelled ? "Schedule cancelled." : "No active schedule run to cancel.");
        return cancelled;
    }

    public string EnqueueScheduledPresetFromJson(string json)
    {
        var request = DadIpcJson.Deserialize<DadScheduledPresetRequest>(json);
        if (request == null)
        {
            var fallbackId = (json ?? string.Empty).Trim().Trim('"');
            request = new DadScheduledPresetRequest { GroupId = fallbackId };
        }

        var groupId = string.IsNullOrWhiteSpace(request.GroupId)
            ? PlannerOptions.SelectedPlannerGroupId
            : request.GroupId;
        if (!TryResolvePlannerGroupForIpc(groupId, out var group, out var rejectionReason) || group == null)
        {
            return DadIpcJson.Serialize(new DadSchedulerQueueSnapshot
            {
                Summary = rejectionReason,
                ActiveState = SchedulerService.CurrentState,
                PendingJobs = SchedulerService.GetQueueSnapshot().PendingJobs,
            });
        }

        var enqueue = SchedulerService.EnqueueScheduledPresetWithDisposition(group, request);
        if (enqueue.Disposition == DadSchedulerEnqueueDisposition.Added && CanAdvanceSchedulerQueue())
        {
            SchedulerService.UpdateWithScheduleRepeatBoundary(
                ResolvePlannerGroup,
                BuildSchedulerPlannerPreview,
                StartScheduledPlannerRequest);
        }

        var snapshot = SchedulerService.GetQueueSnapshot();
        var disposition = DadSchedulerSubmissionRules.ResolveAfterUpdate(
            enqueue.Disposition,
            enqueue.Job.JobId,
            snapshot);
        snapshot.Summary = DadSchedulerSubmissionRules.BuildFeedback(
            disposition,
            enqueue.Job,
            snapshot);
        return DadIpcJson.Serialize(snapshot);
    }

    public string CancelScheduledJobFromJson(string json)
    {
        var request = DadIpcJson.Deserialize<DadCancelScheduledJobRequest>(json);
        if (request == null)
        {
            var fallbackId = (json ?? string.Empty).Trim().Trim('"');
            request = new DadCancelScheduledJobRequest { JobId = fallbackId };
        }

        var cancelled = SchedulerService.CancelScheduledJob(request.JobId, request.Reason);
        var snapshot = SchedulerService.GetQueueSnapshot();
        snapshot.Summary = cancelled
            ? SchedulerService.IsPendingTakeoverCleanupJob(request.JobId)
                ? $"Cancellation cleanup pending for scheduler Job ID {request.JobId}; retry remains blocked until the wake takeover acknowledges full cleanup."
                : $"Cancelled scheduler Job ID {request.JobId}."
            : $"No active or pending scheduler job matched Job ID {request.JobId}.";
        return DadIpcJson.Serialize(snapshot);
    }

    public int ImportLaunchProfilesFromBootDirectory()
    {
        var imported = SchedulerService.ImportLaunchProfilesFromBootDirectory();
        if (imported > 0)
            InvalidatePlannerPreviewCache("launch profiles imported");
        PrintStatus(imported == 0
            ? $"No new launch profiles found in {Configuration.ClientBootDirectory}."
            : $"Imported {imported} launch profile candidate(s) from {Configuration.ClientBootDirectory}.");
        return imported;
    }

    private DadPlannerRunRequestPreview BuildPlannerGroupRunRequestPreview(
        string groupIdOrName,
        DadPlannerGroupStartRequest? startRequest)
        => BuildPlannerGroupRunRequestPreview(BuildPlannerPool(), groupIdOrName, startRequest);

    private DadPlannerRunRequestPreview BuildPlannerGroupRunRequestPreview(
        DadCharacterPool pool,
        string groupIdOrName,
        DadPlannerGroupStartRequest? startRequest)
    {
        if (!TryResolvePlannerGroupForIpc(groupIdOrName, out var group, out var rejectionReason) || group == null)
        {
            return BuildBlockedPlannerGroupPreview(rejectionReason);
        }

        var options = BuildPlannerOptionsForGroup(group, startRequest);
        var preview = PresetProviderService.BuildPlannerPreview(pool, options, group);
        return ApplyPlannerRuntimeTruth(PresetProviderService.BuildPlannerRunRequestPreview(
            pool,
            options,
            plannerPreviewOverride: preview,
            selectedGroup: group), pool, group);
    }

    private static DadPlannerRunRequestPreview BuildBlockedPlannerGroupPreview(string reason)
    {
        var contractPreview = new DadPlannerRequestContractPreview
        {
            Startability = "Blocked",
            CanStart = false,
            CanSchedule = false,
            StaticBlockers = [reason],
            Blockers = [reason],
        };

        return new DadPlannerRunRequestPreview
        {
            CanStart = false,
            CanSchedule = false,
            StatusSummary = reason,
            BlockedReason = reason,
            StaticBlockers = [reason],
            ContractPreview = contractPreview,
            ContractPreviewJson = DadIpcJson.Serialize(contractPreview),
            ModuleBlockers =
            [
                new DadModuleBlockerDto
                {
                    ModuleId = DadModuleId.None,
                    Capability = "PlannerGroup",
                    Severity = DadModuleBlockerSeverity.Blocked,
                    Summary = reason,
                },
            ],
        };
    }

    private DadPlannerGroup BuildPlannerGroupFromCurrentPlanner(
        string displayName,
        DadAcquiredCharacter? localNpcRunner,
        bool includeSlots)
    {
        var preview = includeSlots && localNpcRunner == null ? BuildPlannerPreview() : null;
        var stopPolicy = includeSlots && localNpcRunner == null
            ? preview!.StopPolicy.Clone().Normalize()
            : PlannerOptions.StopPolicy.Clone().Normalize();
        if (localNpcRunner != null && stopPolicy.Mode == DadPlannerStopMode.TargetLevel)
        {
            stopPolicy.TargetCharacterKey = new DadCharacterKey(localNpcRunner.CharacterKey);
            stopPolicy.TargetCharacterLabel = string.IsNullOrWhiteSpace(localNpcRunner.CharacterName) ||
                                              string.IsNullOrWhiteSpace(localNpcRunner.WorldName)
                ? localNpcRunner.CharacterKey
                : $"{localNpcRunner.CharacterName}@{localNpcRunner.WorldName}";
        }

        var now = DateTime.UtcNow;
        return new DadPlannerGroup
        {
            GroupId = Guid.NewGuid().ToString("N"),
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? $"{PresetProviderService.GetPlannerLaneDefinition(PlannerOptions.ActivityMode).DisplayName} Group"
                : displayName.Trim(),
            RunFamily = PlannerOptions.RunFamily,
            ActivityMode = PlannerOptions.ActivityMode,
            OperatorMode = PlannerOptions.OperatorMode,
            ConnectedOnly = PlannerOptions.ConnectedOnly,
            SameDatacenterOnly = PlannerOptions.SameDatacenterOnly,
            AllowStaleForPlanning = PlannerOptions.AllowStaleForPlanning,
            TransportOwner = PlannerOptions.TransportOwner,
            QueueAuthority = PlannerOptions.QueueAuthority,
            InviteAuthority = DadInviteAuthority.PresetLeader,
            DutyContentFinderConditionId = PlannerOptions.DutyContentFinderConditionId,
            DutyDisplayName = PlannerOptions.DutyDisplayName,
            DutyUnsynced = PlannerOptions.DutyUnsynced,
            DutyExpectedPartySize = PlannerOptions.DutyExpectedPartySize,
            RouletteTarget = PlannerOptions.RouletteTarget?.Clone() ?? new DadQueueTarget { Kind = DadQueueTargetKind.Roulette },
            MogtomePreset = PlannerOptions.MogtomePreset,
            MogtomeDutyPolicy = PlannerOptions.MogtomeDutyPolicy,
            RefreshTrustNpcLevels = PlannerOptions.RefreshTrustNpcLevels,
            StopPolicy = stopPolicy,
            CompletionActions = DadCompletionActionSnapshots.Resolve(PlannerOptions.CompletionActions, Configuration.CompletionActions),
            Slots = !includeSlots
                ? []
                : localNpcRunner == null
                    ? BuildPlannerGroupSlotsFromPreview(preview!)
                    :
                [
                    new DadPlannerGroupSlot
                    {
                        SlotId = DadPlannerSlotRules.LeaderSlotId,
                        RequiredRole = DadPartyRole.Any,
                        RequiredAccountKey = ResolvePlannerAccountKey(localNpcRunner),
                        RequiredCharacterKey = new DadCharacterKey(localNpcRunner.CharacterKey),
                        AllowSubstitution = false,
                    },
                ],
            ScheduleCadenceHours = PlannerOptions.ActivityMode == DadPlannerActivityMode.CustomDuty ? 18 : 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    private static DadPlannerGroupSlot ClonePlannerGroupSlot(DadPlannerGroupSlot source)
        => new()
        {
            SlotId = source.SlotId,
            IsSubstitute = source.IsSubstitute,
            RequiredRole = source.RequiredRole,
            RequiredAccountKey = source.RequiredAccountKey,
            RequiredCharacterKey = source.RequiredCharacterKey,
            RequiredJobId = source.RequiredJobId,
            AdsLootMode = source.AdsLootMode,
            LevelSeekTarget = source.LevelSeekTarget,
            WakePolicy = source.WakePolicy,
            LaunchProfileId = source.LaunchProfileId,
            CharacterLoadInstruction = source.CharacterLoadInstruction?.Clone() ?? new DadCharacterLoadInstruction(),
            SharedIdentity = source.SharedIdentity?.Clone(),
            AllowSubstitution = source.AllowSubstitution,
        };

    private List<DadPlannerGroupSlot> BuildPlannerGroupSlotsFromPreview(DadActivityPreset preview)
    {
        return DadPlannerSlotRules.NormalizeGroupSlots(preview.SelectedCharacters.Select(slot =>
        {
            var character = preview.AvailableCharacters.FirstOrDefault(candidate =>
                string.Equals(candidate.CharacterKey, slot.CharacterKey, StringComparison.OrdinalIgnoreCase) &&
                (slot.RequiredAccountKey.IsEmpty || MatchesPlannerSlotAccount(candidate, slot.RequiredAccountKey)));
            var accountKey = !slot.RequiredAccountKey.IsEmpty
                ? slot.RequiredAccountKey
                : character == null
                    ? new DadAccountKey(string.Empty)
                    : ResolvePlannerAccountKey(character);
            return new DadPlannerGroupSlot
            {
                SlotId = slot.SlotId,
                IsSubstitute = false,
                RequiredRole = slot.RequiredRole,
                RequiredAccountKey = accountKey,
                RequiredCharacterKey = string.IsNullOrWhiteSpace(slot.CharacterKey)
                    ? new DadCharacterKey(string.Empty)
                    : new DadCharacterKey(slot.CharacterKey),
                RequiredJobId = slot.RequiredJobId,
                AdsLootMode = slot.AdsLootMode,
                LevelSeekTarget = slot.LevelSeekTarget,
                WakePolicy = DadSchedulerWakePolicy.LaunchIfOffline,
                AllowSubstitution = false,
            };
        }));
    }

    private static bool MatchesPlannerSlotAccount(DadAcquiredCharacter character, DadAccountKey accountKey)
        => (!string.IsNullOrWhiteSpace(character.AccountId)
            && string.Equals(character.AccountId, accountKey.Value, StringComparison.OrdinalIgnoreCase))
           || (!string.IsNullOrWhiteSpace(character.AccountAlias)
               && string.Equals(character.AccountAlias, accountKey.Value, StringComparison.OrdinalIgnoreCase));

    private DadPlannerGroupSummary BuildPlannerGroupSummary(DadPlannerGroup group)
    {
        var lane = PresetProviderService.GetPlannerLaneDefinition(group.ActivityMode);
        var normalizedSlots = DadPlannerSlotRules.NormalizeGroupSlots(group.Slots);
        var primarySlotCount = normalizedSlots.Count(static slot => !slot.IsSubstitute);
        var requiredAccounts = normalizedSlots
            .Select(static slot => slot.RequiredAccountKey.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var requiredCharacters = normalizedSlots
            .Where(static slot => !slot.RequiredCharacterKey.IsEmpty)
            .Select(static slot => $"{slot.RequiredAccountKey.Value}:{slot.RequiredCharacterKey.Value}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return new DadPlannerGroupSummary
        {
            GroupId = group.GroupId,
            DisplayName = group.DisplayName,
            ActivityMode = group.ActivityMode,
            Lane = lane.DisplayName,
            SlotCount = primarySlotCount,
            RequiredAccountCount = requiredAccounts,
            RequiredCharacterCount = requiredCharacters,
            Summary = $"{lane.DisplayName} | {primarySlotCount} slot(s) | accounts {requiredAccounts} | characters {requiredCharacters}",
        };
    }

    private DadPresetPlannerOptions BuildPlannerOptionsForGroup(DadPlannerGroup group, DadPlannerGroupStartRequest? startRequest)
    {
        var activityMode = ResolvePlannerGroupLane(group.ActivityMode, startRequest?.Lane);
        return new DadPresetPlannerOptions
        {
            PresetName = group.DisplayName,
            SelectedPlannerGroupId = group.GroupId,
            RunFamily = PresetProviderService.GetPlannerRunFamily(activityMode),
            ActivityMode = activityMode,
            ActivityName = PresetProviderService.GetPlannerLaneDefinition(activityMode).DisplayName,
            OperatorMode = group.OperatorMode,
            ConnectedOnly = group.ConnectedOnly,
            SameDatacenterOnly = group.SameDatacenterOnly,
            AllowStaleForPlanning = group.AllowStaleForPlanning,
            TransportOwner = group.TransportOwner,
            QueueAuthority = group.QueueAuthority,
            InviteAuthority = DadInviteAuthority.PresetLeader,
            DutyContentFinderConditionId = startRequest?.DutyContentFinderConditionId ?? group.DutyContentFinderConditionId,
            DutyDisplayName = group.DutyDisplayName,
            DutyUnsynced = group.DutyUnsynced,
            DutyExpectedPartySize = group.DutyExpectedPartySize,
            RouletteTarget = group.RouletteTarget?.Clone() ?? new DadQueueTarget { Kind = DadQueueTargetKind.Roulette },
            MogtomePreset = group.MogtomePreset,
            MogtomeDutyPolicy = group.MogtomeDutyPolicy,
            RefreshTrustNpcLevels = group.RefreshTrustNpcLevels,
            StopPolicy = group.StopPolicy.Clone(),
            CompletionActions = group.CompletionActions?.Clone(),
            IncludedAccountKeys = DadPlannerSlotRules.NormalizeGroupSlots(group.Slots)
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
            RunFamily = source.RunFamily,
            ActivityMode = source.ActivityMode,
            OperatorMode = source.OperatorMode,
            ConnectedOnly = source.ConnectedOnly,
            SameDatacenterOnly = source.SameDatacenterOnly,
            AllowStaleForPlanning = source.AllowStaleForPlanning,
            TransportOwner = source.TransportOwner,
            QueueAuthority = source.QueueAuthority,
            InviteAuthority = DadInviteAuthority.PresetLeader,
            DutyContentFinderConditionId = source.DutyContentFinderConditionId,
            DutyDisplayName = source.DutyDisplayName,
            DutyUnsynced = source.DutyUnsynced,
            DutyExpectedPartySize = source.DutyExpectedPartySize,
            RouletteTarget = source.RouletteTarget?.Clone() ?? new DadQueueTarget { Kind = DadQueueTargetKind.Roulette },
            MogtomePreset = source.MogtomePreset,
            MogtomeDutyPolicy = source.MogtomeDutyPolicy,
            RefreshTrustNpcLevels = source.RefreshTrustNpcLevels,
            StopPolicy = source.StopPolicy.Clone(),
            SharedStopTargetIdentityToken = source.SharedStopTargetIdentityToken,
            CompletionActions = source.CompletionActions?.Clone(),
            Slots = source.Slots.Select(static slot => new DadPlannerGroupSlot
            {
                SlotId = slot.SlotId,
                IsSubstitute = slot.IsSubstitute,
                RequiredRole = slot.RequiredRole,
                RequiredAccountKey = slot.RequiredAccountKey,
                RequiredCharacterKey = slot.RequiredCharacterKey,
                RequiredJobId = slot.RequiredJobId,
                AdsLootMode = slot.AdsLootMode,
                LevelSeekTarget = slot.LevelSeekTarget,
                WakePolicy = slot.WakePolicy,
                LaunchProfileId = slot.LaunchProfileId,
                CharacterLoadInstruction = slot.CharacterLoadInstruction.Clone(),
                SharedIdentity = slot.SharedIdentity?.Clone(),
                AllowSubstitution = slot.AllowSubstitution,
            }).ToList(),
            ScheduleEnabled = source.ScheduleEnabled,
            ScheduleCadenceHours = source.ScheduleCadenceHours,
            NextEligibleTimeUtc = source.NextEligibleTimeUtc,
            ScheduleRequester = source.ScheduleRequester,
            SchedulePriority = source.SchedulePriority,
            MapRunTemplate = source.MapRunTemplate,
            MapMode = source.MapMode,
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
        };

    private static void ApplyPlannerGroupDefaults(DadPlannerGroup group, DadPresetPlannerOptions options)
    {
        options.SelectedPlannerGroupId = group.GroupId;
        options.PresetName = group.DisplayName;
        options.RunFamily = group.RunFamily;
        options.ActivityMode = group.ActivityMode;
        options.OperatorMode = group.OperatorMode;
        options.ConnectedOnly = group.ConnectedOnly;
        options.SameDatacenterOnly = group.SameDatacenterOnly;
        options.AllowStaleForPlanning = group.AllowStaleForPlanning;
        options.TransportOwner = group.TransportOwner;
        options.QueueAuthority = group.QueueAuthority;
        options.InviteAuthority = DadInviteAuthority.PresetLeader;
        options.DutyContentFinderConditionId = group.DutyContentFinderConditionId;
        options.DutyDisplayName = group.DutyDisplayName;
        options.DutyUnsynced = group.DutyUnsynced;
        options.DutyExpectedPartySize = group.DutyExpectedPartySize;
        options.RouletteTarget = group.RouletteTarget?.Clone() ?? new DadQueueTarget { Kind = DadQueueTargetKind.Roulette };
        options.MogtomePreset = group.MogtomePreset;
        options.MogtomeDutyPolicy = group.MogtomeDutyPolicy;
        options.RefreshTrustNpcLevels = group.RefreshTrustNpcLevels;
        options.StopPolicy = group.StopPolicy.Clone();
        options.CompletionActions = group.CompletionActions?.Clone();
        options.IncludedAccountKeys = DadPlannerSlotRules.NormalizeGroupSlots(group.Slots)
            .Select(static slot => slot.RequiredAccountKey)
            .Where(static key => !key.IsEmpty)
            .DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void NormalizePlannerGroupForStorage(DadPlannerGroup group)
    {
        group.InviteAuthority = DadInviteAuthority.PresetLeader;
        group.RouletteTarget ??= new DadQueueTarget { Kind = DadQueueTargetKind.Roulette };
        group.Slots = DadPlannerSlotRules.NormalizeGroupSlots(group.Slots);
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
        => StartDemoRunFromShell("Daily Roulette demo", BuildDailyMsqDemoRequest());

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
            InvalidatePlannerPreviewCache("planner run started");

        return startResult;
    }

    private DadPlannerRunRequestPreview? BuildSchedulerPlannerPreview(string groupId)
    {
        var group = ResolvePlannerGroup(groupId);
        return group == null ? null : BuildPlannerGroupRunRequestPreview(group.GroupId, null);
    }

    private DadRunResult StartScheduledPlannerRequest(
        DadRunRequest request,
        DadScheduleRepeatBoundary repeatBoundary)
    {
        var result = RunCoordinatorService.StartScheduledTasks(request, repeatBoundary);
        PrimeAuthorityCacheFromRun(request, result);
        if (result.Status != DadRunStatus.Rejected)
            InvalidatePlannerPreviewCache("scheduled planner started");
        return result;
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

    private DadPlannerRunRequestPreview ApplyPlannerRuntimeTruth(
        DadPlannerRunRequestPreview requestPreview,
        DadCharacterPool pool,
        DadPlannerGroup? selectedGroup = null)
    {
        if (requestPreview.Request == null)
        {
            RefreshPlannerContractPreview(requestPreview);
            return requestPreview;
        }

        var previewOnly = string.Equals(requestPreview.Request.RequestedBy, "planner-preview", StringComparison.OrdinalIgnoreCase);
        if (!previewOnly)
        {
            var requireLiveReadiness = requestPreview.CanStart || !requestPreview.CanSchedule;
            var allowWakeableCoordinatorLeader = HasWakeableEffectiveCoordinatorSlot(selectedGroup, requestPreview);
            var liveLocalRuntimeTruth = PresenceService.BuildLiveSafetySnapshot();
            LogCoordinatorProvenance("planner-validation", requestPreview.Request, liveLocalRuntimeTruth);
            var plan = PlannerService.BuildPlan(
                requestPreview.Request,
                pool,
                out var rejectionReason,
                requireLiveReadiness,
                allowWakeableCoordinatorLeader,
                liveLocalRuntimeTruth);
            if (plan == null)
            {
                var relaxedPlanBuilt = requireLiveReadiness &&
                                       PlannerService.BuildPlan(
                                           requestPreview.Request,
                                           pool,
                                           out _,
                                           requireLiveReadiness: false,
                                           allowWakeableCoordinatorLeader: allowWakeableCoordinatorLeader,
                                           liveLocalRuntimeTruth: liveLocalRuntimeTruth) != null;
                if (DadPlannerValidationRules.IsStrictRuntimeOnlyFailure(
                        requireLiveReadiness,
                        strictPlanBuilt: false,
                        relaxedPlanBuilt))
                {
                    MergePlannerReadinessBlocker(requestPreview, rejectionReason);
                }
                else
                {
                    MergePlannerPreviewBlocker(requestPreview, rejectionReason);
                }
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

    private static bool HasWakeableEffectiveCoordinatorSlot(
        DadPlannerGroup? selectedGroup,
        DadPlannerRunRequestPreview requestPreview)
    {
        if (selectedGroup == null)
            return false;

        var projected = DadEffectivePlannerGroupProjection.Project(
            selectedGroup,
            requestPreview.PlannerPreview.ActivityMode,
            requestPreview.ExpectedPartySize);
        var bound = DadEffectivePlannerGroupProjection.BindResolvedSchedulerSlots(
            projected,
            requestPreview.PlannerPreview.SelectedCharacters);
        var slotOne = DadPlannerSlotRules.GetPrimaryRows(bound.Slots)
            .FirstOrDefault(static slot =>
                string.Equals(slot.SlotId, DadPlannerSlotRules.LeaderSlotId, StringComparison.OrdinalIgnoreCase));
        return slotOne?.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline;
    }

    private void LogCoordinatorProvenance(
        string boundary,
        DadRunRequest request,
        DadParticipantSnapshot liveTruth)
    {
        var resolved = DadFullPartyExecutionRules.TryResolveActiveCoordinatorCharacter(
            liveTruth,
            out var character,
            out var blocker);
        var diagnostic = string.Join(
            "|",
            boundary,
            liveTruth.WorkerSessionId.Value,
            liveTruth.ClientInstanceId,
            liveTruth.ManagedAccountKey.Value,
            liveTruth.ActiveCharacterKey.Value,
            liveTruth.Character.ContentId,
            liveTruth.WorldReadyStable,
            resolved,
            blocker);
        if (string.Equals(diagnostic, lastCoordinatorProvenanceDiagnostic, StringComparison.Ordinal))
            return;

        lastCoordinatorProvenanceDiagnostic = diagnostic;
        Log.Information(
            "[dad] Coordinator provenance boundary={Boundary} request={RequestId} localWorker={LocalWorkerSessionId} localClient={LocalClientInstanceId} managedAccount={ManagedAccountKey} character={CharacterKey} contentId={ContentId} source={Source} available={Available} worldReadyStable={WorldReadyStable} resolved={Resolved} blocker={Blocker}.",
            boundary,
            request.RequestId,
            liveTruth.WorkerSessionId,
            liveTruth.ClientInstanceId,
            liveTruth.ManagedAccountKey,
            liveTruth.ActiveCharacterKey.IsEmpty ? "(none)" : liveTruth.ActiveCharacterKey.Value,
            liveTruth.Character.ContentId,
            liveTruth.Character.Source,
            liveTruth.IsAvailable,
            liveTruth.WorldReadyStable,
            resolved,
            resolved ? "(none)" : blocker);
    }

    private static void MergePlannerRuntimeStatus(DadPlannerRunRequestPreview requestPreview, DadModuleExecutionStatusDto runtimeStatus)
    {
        MergePlannerModuleBlockers(requestPreview.ModuleBlockers, runtimeStatus.Blockers);

        if (!runtimeStatus.CanStart)
        {
            var decision = DadPlannerValidationRules.EvaluateModuleRuntimeStatus(
                requestPreview.CanSchedule,
                runtimeStatus);
            var reason = decision.Reason;
            if (decision.IsTransientRuntimeReadiness)
            {
                MergePlannerReadinessBlocker(requestPreview, reason);
                return;
            }

            requestPreview.CanStart = false;
            requestPreview.CanSchedule = decision.CanSchedule;
            if (!string.IsNullOrWhiteSpace(reason) &&
                requestPreview.ModuleBlockers.All(existing =>
                    !string.Equals(existing.Summary, reason, StringComparison.OrdinalIgnoreCase)))
            {
                requestPreview.ModuleBlockers.Add(new DadModuleBlockerDto
                {
                    ModuleId = requestPreview.ModuleId,
                    Capability = "PlannerRuntime",
                    Severity = DadModuleBlockerSeverity.Blocked,
                    Summary = reason,
                });
            }

            requestPreview.BlockedReason = DadPlannerValidationRules.BuildBlockedReason(requestPreview);
            requestPreview.StatusSummary = $"Planner request blocked by module runtime: {requestPreview.BlockedReason}";

            return;
        }

        if (requestPreview.CanStart && !string.IsNullOrWhiteSpace(runtimeStatus.Summary))
            requestPreview.StatusSummary = $"Planner request ready to start. {runtimeStatus.Summary}";
    }

    private static void MergePlannerPreviewBlocker(DadPlannerRunRequestPreview requestPreview, string blocker)
    {
        if (string.IsNullOrWhiteSpace(blocker))
            return;

        requestPreview.CanSchedule = false;
        requestPreview.CanStart = false;
        AddPlannerValidationBlocker(requestPreview.StaticBlockers, blocker);

        if (requestPreview.ModuleBlockers.All(existing => !string.Equals(existing.Summary, blocker, StringComparison.OrdinalIgnoreCase)))
        {
            requestPreview.ModuleBlockers.Add(new DadModuleBlockerDto
            {
                ModuleId = requestPreview.ModuleId,
                Capability = "Planner",
                Severity = DadModuleBlockerSeverity.Blocked,
                Summary = blocker,
            });
        }

        requestPreview.BlockedReason = DadPlannerValidationRules.BuildBlockedReason(requestPreview);
        requestPreview.StatusSummary = $"Planner request blocked: {requestPreview.BlockedReason}";
    }

    private static void MergePlannerReadinessBlocker(DadPlannerRunRequestPreview requestPreview, string blocker)
    {
        if (string.IsNullOrWhiteSpace(blocker))
            return;

        requestPreview.CanStart = false;
        AddPlannerValidationBlocker(requestPreview.ReadinessBlockers, blocker);
        requestPreview.ReadinessSummary = $"Waiting for refreshed strict-runtime readiness: {blocker}";
        if (requestPreview.ModuleBlockers.All(existing => !string.Equals(existing.Summary, blocker, StringComparison.OrdinalIgnoreCase)))
        {
            requestPreview.ModuleBlockers.Add(new DadModuleBlockerDto
            {
                ModuleId = requestPreview.ModuleId,
                Capability = "PlannerRuntimeReadiness",
                Severity = DadModuleBlockerSeverity.Blocked,
                Summary = blocker,
            });
        }

        requestPreview.BlockedReason = DadPlannerValidationRules.BuildBlockedReason(requestPreview);
        requestPreview.StatusSummary = requestPreview.CanSchedule
            ? $"Planner request remains schedulable while runtime truth refreshes: {requestPreview.BlockedReason}"
            : $"Planner request remains terminally blocked while retaining readiness detail: {requestPreview.BlockedReason}";
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

    private static void AddPlannerValidationBlocker(List<string> blockers, string blocker)
    {
        if (string.IsNullOrWhiteSpace(blocker) ||
            blockers.Any(existing => string.Equals(existing, blocker, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        blockers.Add(blocker.Trim());
    }

    private static void RefreshPlannerContractPreview(DadPlannerRunRequestPreview requestPreview)
    {
        requestPreview.BlockedReason = requestPreview.CanStart
            ? string.Empty
            : DadPlannerValidationRules.BuildBlockedReason(requestPreview);
        requestPreview.StopPolicy = requestPreview.Request?.StopPolicy.Clone()
                                    ?? requestPreview.PlannerPreview.StopPolicy.Clone();
        requestPreview.ContractPreview.StopPolicy = requestPreview.StopPolicy.Clone();
        requestPreview.ContractPreview.CanStart = requestPreview.CanStart;
        requestPreview.ContractPreview.CanSchedule = requestPreview.CanSchedule;
        requestPreview.ContractPreview.ReadinessSummary = requestPreview.ReadinessSummary;
        requestPreview.ContractPreview.StaticBlockers = [..requestPreview.StaticBlockers];
        requestPreview.ContractPreview.ReadinessBlockers = [..requestPreview.ReadinessBlockers];
        requestPreview.ContractPreview.ScheduleBlockers = [..requestPreview.ScheduleBlockers];
        requestPreview.ContractPreview.Startability = BuildPlannerStartabilityLabel(requestPreview);
        requestPreview.ContractPreview.Blockers = BuildPlannerContractBlockers(requestPreview);
        requestPreview.ContractPreviewJson = DadIpcJson.Serialize(requestPreview.ContractPreview);
    }

    private static string BuildPlannerStartabilityLabel(DadPlannerRunRequestPreview requestPreview)
        => requestPreview.CanStart
            ? "Startable"
            : requestPreview.CanSchedule
                ? "Schedulable"
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
            $"{slot.SlotId}:{slot.RequiredRole}:{slot.AssignmentMode}:{slot.RequiredAccountKey}:{slot.CharacterKey}:{slot.ContentId}:{slot.RequiredJobId?.ToString() ?? "current"}:{slot.AllowSubstitution}:{slot.IsSubstitution}"));
        var selectedCharacters = string.Join(",", plannerPreview.SelectedCharacters
            .Where(static slot => !string.IsNullOrWhiteSpace(slot.CharacterKey))
            .Select(static slot => $"{slot.RequiredAccountKey.Value.Trim()}:{slot.CharacterKey.Trim()}:{slot.ContentId}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));

        return string.Join("|", new[]
        {
            $"family={options.RunFamily}",
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
            $"roulette={options.RouletteTarget?.SchemaVersion ?? 0}:{options.RouletteTarget?.Kind}:{options.RouletteTarget?.RouletteId ?? 0}:{options.RouletteTarget?.Key?.Trim()}:{options.RouletteTarget?.DisplayName?.Trim()}",
            $"mogtome={options.MogtomePreset.Trim()}:{options.MogtomeDutyPolicy.Trim()}",
            $"trustRefresh={options.RefreshTrustNpcLevels}",
            $"stop={plannerPreview.StopPolicy.Mode}:{plannerPreview.StopPolicy.AfterRuns}:{plannerPreview.StopPolicy.TargetLevel}:{plannerPreview.StopPolicy.TargetCharacterKey}:{plannerPreview.StopPolicy.SafetyCap}",
            $"completion={BuildCompletionActionSignature(options.CompletionActions)}",
            "blunderville=emote-run",
            $"leader={plannerPreview.LeaderCharacterKey}",
            $"slots={selectedSlots}",
            $"selected={selectedCharacters}",
        });
    }

    private static string BuildCompletionActionSignature(DadCompletionActions? actions)
    {
        if (actions == null)
            return "global-defaults";

        var utilities = actions.Utilities ?? new DadPostRunUtilities();
        return string.Join(":", new[]
        {
            actions.PlaySound.ToString(),
            actions.SoundEffectId.ToString(CultureInfo.InvariantCulture),
            actions.RunCommands.ToString(),
            string.Join("\n", actions.Commands ?? []),
            actions.KillMode.ToString(),
            utilities.OpenGearCoffers.ToString(),
            utilities.RegisterTripleTriadCards.ToString(),
            utilities.SellTripleTriadCards.ToString(),
            utilities.GrandCompanyHandInViaAutoRetainer.ToString(),
            utilities.GrandCompanyHandInCommand,
        });
    }

    public bool HasServerDadAuthority()
    {
        if (RunCoordinatorService.IsServerDad)
            return TransportService.IsReady && !string.IsNullOrWhiteSpace(TransportService.CurrentTransport.ListenerEndpoint);

        return TransportService.IsReady &&
               !TransportService.CurrentTransport.AuthorityWorkerSessionId.IsEmpty;
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

    public DadMiniStatusSnapshot BuildMiniStatusSnapshot()
    {
        var runState = GetVisibleRunState(forceAuthorityRefresh: false);
        return DadMiniStatusSnapshotBuilder.Build(
            RunCoordinatorService.IsServerDad,
            runState.AuthorityView,
            TransportService.CurrentTransport,
            runState.VisibleRun,
            SchedulerService.GetQueueSnapshot(),
            SchedulerService.GetScheduleSnapshot(),
            WorkerExecutionService.GetStatus(),
            PresenceService.BuildSnapshotCopy(),
            TransportService.LatestStopAllStatus,
            Configuration.RunHistory,
            WakeTakeoverService.GetActiveStatus());
    }

    public DadStopAllStatus RequestStopAll()
        => TransportService.RequestStopAll(new DadStopAllRequest
        {
            OperationId = Guid.NewGuid().ToString("N"),
            RequestedByWorkerSessionId = PresenceService.WorkerSessionId,
            RequestedAtUtc = DateTime.UtcNow,
            Reason = "Stopped from DAD mini window.",
        });

    public void CancelActiveRunFromMini()
        => RunCoordinatorService.CancelActiveRun();

    public bool CancelActiveScheduleFromMini()
        => SchedulerService.CancelScheduleRun("Cancelled from DAD mini window.");

    public bool CancelSchedulerJobFromMini(string jobId)
        => SchedulerService.CancelScheduledJob(jobId, "Cancelled from DAD mini window.");

    private DadStopAllWorkerResult StopAllLocal(DadStopAllRequest request)
    {
        var hasRecordedResult = localStopAllResults.TryGetValue(request.OperationId, out var recorded);
        if (hasRecordedResult && !DadStopAllStatusRules.IsLocalCleanupPending(recorded!))
            return recorded!.Clone();

        try
        {
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Stopped by DAD Stop-all." : request.Reason;
            DadStopAllWorkerResult result;
            DadWakeTakeoverStopAllResult wake;
            if (!hasRecordedResult)
            {
                var suppression = TimeSpan.FromSeconds(Math.Max(2, Configuration.CancelAckTimeoutSeconds));
                var scheduler = SchedulerService.StopAll(reason, suppression);
                RunCoordinatorService.CancelAllLocal(reason);
                wake = WakeTakeoverService.StopAll(reason);
                ClaimService.ReleaseAllClaims();
                WorkerExecutionService.CancelAll(reason);
                QueueExecutionService.CancelAll(reason);
                PresenceService.ResetToIdle();
                result = new DadStopAllWorkerResult
                {
                    OperationId = request.OperationId,
                    WorkerSessionId = PresenceService.WorkerSessionId,
                    CancelledSchedulerJobs = scheduler.PendingJobsCancelled + (scheduler.ActiveJobCancelled ? 1 : 0),
                    Summary = scheduler.Summary,
                };
            }
            else
            {
                // Repeated delivery of the same operation is the cleanup acknowledgement poll. All
                // broad stop mutations already ran; only retry DAD-owned takeover lease release.
                result = recorded!.Clone();
                wake = WakeTakeoverService.StopAll(reason);
            }

            result.UpdatedAtUtc = DateTime.UtcNow;
            result.LocalCleanupCompleted = !wake.CleanupPending;
            result.State = wake.CleanupPending
                ? DadStopAllWorkerState.Expected
                : DadStopAllWorkerState.Acknowledged;
            result.CancelledWakeTakeovers = Math.Max(result.CancelledWakeTakeovers, wake.CancelledCount);
            result.PreservedCommittedTakeovers = Math.Max(
                result.PreservedCommittedTakeovers,
                wake.PreservedCommittedCount);
            result.Partial = result.PreservedCommittedTakeovers > 0;
            var summary = hasRecordedResult
                ? result.Summary
                : $"{result.Summary} {wake.Summary}";
            result.Summary = WithStopAllCleanupState(summary, wake.CleanupPending);
            DadStopAllStatusRules.NormalizeLocalResult(result);
            localStopAllResults[request.OperationId] = result.Clone();
            while (localStopAllResults.Count > 32)
                localStopAllResults.Remove(localStopAllResults.Keys.First());
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[dad] Stop-all {OperationId} local cleanup failed.", request.OperationId);
            var result = hasRecordedResult ? recorded!.Clone() : new DadStopAllWorkerResult();
            result.OperationId = request.OperationId;
            result.WorkerSessionId = PresenceService.WorkerSessionId;
            result.State = DadStopAllWorkerState.Rejected;
            result.UpdatedAtUtc = DateTime.UtcNow;
            result.LocalCleanupCompleted = false;
            result.Partial = true;
            result.Summary = $"Local Stop-all cleanup failed: {ex.Message}";
            localStopAllResults[request.OperationId] = result.Clone();
            return result;
        }
    }

    private static string WithStopAllCleanupState(string summary, bool cleanupPending)
    {
        const string pendingSuffix = " DAD-owned takeover cleanup is pending; acknowledgement will follow after all temporary leases release.";
        summary = (summary ?? string.Empty).Trim();
        if (summary.EndsWith(pendingSuffix.TrimStart(), StringComparison.Ordinal))
            summary = summary[..^pendingSuffix.TrimStart().Length].TrimEnd();
        return cleanupPending ? $"{summary}{pendingSuffix}".Trim() : summary;
    }

    public static bool IsBusy(DadRunResult result)
        => result.Status is DadRunStatus.Queued or DadRunStatus.WaitingForParticipants or DadRunStatus.Running;

    public bool IsRemoteAuthorityView(DadRunResult localRun, DadRunResult authorityRun)
        => BuildAuthorityView(localRun, authorityRun).Kind is not DadAuthorityViewKind.LocalOnly and not DadAuthorityViewKind.NoRemoteAuthority;

    private DadAuthorityViewState BuildAuthorityView(DadRunResult localRun, DadRunResult authorityRun)
    {
        DateTime? lastRefreshSucceededUtc;
        lock (authorityCacheGate)
            lastRefreshSucceededUtc = lastAuthorityRefreshSucceededUtc;

        return DadAuthorityViewBuilder.Build(
            localRun,
            authorityRun,
            TransportService.CurrentTransport,
            PresenceService.WorkerSessionId,
            Configuration.LocalOnlyModeEnabled,
            lastRefreshSucceededUtc,
            DateTime.UtcNow,
            RemoteAuthorityStatusStaleThreshold);
    }

    private async Task RunAuthorityStatusPollLoopAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAuthorityStatusCacheFromBackgroundAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (DadBackgroundTaskObserver.IsExpectedShutdownException(ex))
            {
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[dad] Authority status poll failed.");
            }

            await Task.Delay(RemoteAuthorityStatusRefreshInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshAuthorityStatusCacheFromBackgroundAsync(CancellationToken cancellationToken)
    {
        if (RunCoordinatorService.IsServerDad || !Configuration.PluginEnabled || Configuration.LocalOnlyModeEnabled)
            return;

        var transport = TransportService.CurrentTransport;
        var authorityEndpoint = TransportService.GetPreferredAuthorityEndpoint();
        if (!transport.AuthorityRoutable || transport.AuthorityWorkerSessionId.IsEmpty)
        {
            lock (authorityCacheGate)
            {
                authorityRefreshInFlight = false;
                lastAuthorityRefreshFailure = string.IsNullOrWhiteSpace(transport.ConnectionStatus)
                    ? "Dad Coordinator is offline; reconnecting."
                    : transport.ConnectionStatus;
            }
            return;
        }

        var now = DateTime.UtcNow;
        lock (authorityCacheGate)
        {
            if (!string.Equals(cachedAuthorityEndpoint, authorityEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                cachedAuthorityRun = null;
                cachedAuthorityEndpoint = authorityEndpoint;
                nextAuthorityStatusRefreshUtc = DateTime.MinValue;
            }

            if (now < suppressRemoteAuthorityRefreshUntilUtc || now < nextAuthorityStatusRefreshUtc)
                return;

            nextAuthorityStatusRefreshUtc = now + RemoteAuthorityStatusRefreshInterval;
            authorityRefreshInFlight = true;
            lastAuthorityRefreshAttemptUtc = now;
        }

        DadRunResult? remote = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            remote = await TransportService.QueryAuthorityStatusAsync(cancellationToken).ConfigureAwait(false);
            if (remote == null)
            {
                lock (authorityCacheGate)
                    lastAuthorityRefreshFailure = "Dad Coordinator route became unavailable during status refresh.";
                LogAuthorityRefreshFailure(authorityEndpoint, transport.AuthorityWorkerSessionId);
                return;
            }

            ApplyKnownAuthorityMetadata(remote);
            lock (authorityCacheGate)
            {
                cachedAuthorityRun = remote.Clone();
                cachedAuthorityEndpoint = authorityEndpoint;
                lastAuthorityRefreshSucceededUtc = DateTime.UtcNow;
                lastAuthorityRefreshFailure = string.Empty;
            }

            LogAuthorityRefreshSuccess(remote);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (authorityCacheGate)
                lastAuthorityRefreshFailure = ex.Message;
            LogAuthorityRefreshFailure(authorityEndpoint, transport.AuthorityWorkerSessionId);
        }
        finally
        {
            lock (authorityCacheGate)
                authorityRefreshInFlight = false;
        }
    }

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
        var hasRemoteAuthority = transport.AuthorityRoutable && !transport.AuthorityWorkerSessionId.IsEmpty;
        if (!hasRemoteAuthority)
        {
            ResetAuthorityCache(clearFreshness: true);
            return BuildUnavailableAuthorityResult(
                "Dad Coordinator offline; reconnecting.",
                string.IsNullOrWhiteSpace(transport.ConnectionStatus)
                    ? "No routable Dad Coordinator hub session is connected."
                    : transport.ConnectionStatus,
                authorityEndpoint,
                transport.AuthorityWorkerSessionId,
                transport.AuthorityRole);
        }

        DadRunResult? cached;
        DateTime suppressUntilUtc;
        bool refreshInFlight;
        string refreshFailure;
        lock (authorityCacheGate)
        {
            if (!string.Equals(cachedAuthorityEndpoint, authorityEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                cachedAuthorityRun = null;
                cachedAuthorityEndpoint = authorityEndpoint;
                nextAuthorityStatusRefreshUtc = DateTime.MinValue;
            }

            if (forceRefresh)
                nextAuthorityStatusRefreshUtc = DateTime.MinValue;

            cached = cachedAuthorityRun?.Clone();
            suppressUntilUtc = suppressRemoteAuthorityRefreshUntilUtc;
            refreshInFlight = authorityRefreshInFlight;
            refreshFailure = lastAuthorityRefreshFailure;
        }

        if (!forceRefresh && DateTime.UtcNow < suppressUntilUtc)
        {
            return BuildUnavailableAuthorityResult(
                "Dad Coordinator status refresh deferred.",
                "Dad Coordinator status refresh deferred while endpoint changes settle.",
                authorityEndpoint,
                transport.AuthorityWorkerSessionId,
                transport.AuthorityRole);
        }

        if (cached != null)
            return CloneAuthorityRun(cached);

        return BuildUnavailableAuthorityResult(
            refreshInFlight ? "Dad Coordinator status refresh pending." : "Dad Coordinator status refresh unavailable.",
            refreshInFlight
                ? "Dad Coordinator status refresh is in progress."
                : string.IsNullOrWhiteSpace(refreshFailure)
                    ? "Dad Coordinator status refresh has not completed yet."
                    : refreshFailure,
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
        lock (authorityCacheGate)
        {
            cachedAuthorityRun = null;
            cachedAuthorityEndpoint = string.Empty;
            nextAuthorityStatusRefreshUtc = DateTime.MinValue;
            if (clearFreshness)
            {
                lastAuthorityRefreshSucceededUtc = null;
            }
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
        if (DadCoordinatorService.RequiresServerDadAuthority(request))
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
                LanPartyPreset = "Daily Roulette",
                QueueTarget = new DadQueueTarget
                {
                    Kind = DadQueueTargetKind.Roulette,
                    RouletteId = DadRouletteCatalogProjection.MainScenarioRouletteId,
                    Key = DadRouletteCatalogProjection.BuildCanonicalKey(DadRouletteCatalogProjection.MainScenarioRouletteId),
                    DisplayName = "Main Scenario",
                },
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
            TransportMode = DadTransportMode.ServerHub,
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
        RosterCatalogService.RefreshCatalog(pool, new DadRosterRefreshPlan
        {
            LogDiagnostics = true,
            DiagnosticsReason = "shell character pool refresh",
        });
        PrintStatus($"dad pool refreshed. {pool.LastSummary}");
        return pool;
    }

    public DadCharacterPool SaveLocalCharacterToXadbFromShell()
    {
        var pool = CharacterIntelligenceService.SaveLocalToXadb();
        RosterCatalogService.RefreshCatalog(pool, new DadRosterRefreshPlan
        {
            LogDiagnostics = true,
            DiagnosticsReason = "shell XADB save refresh",
        });
        PrintStatus($"dad XADB save requested. {pool.XadbStatus.LastStatus}");
        return pool;
    }

    public DadCharacterPool RequestPeerSnapshotsFromShell()
    {
        var pool = CharacterIntelligenceService.RequestPeerSnapshots();
        RosterCatalogService.RefreshCatalog(
            pool,
            DadRosterRefreshPlan.ConnectedDads("shell connected roster refresh"));
        PrintStatus($"dad peer snapshot request status: {pool.PeerTransport.LastRequestStatus}");
        return pool;
    }

    public void SetPluginEnabled(bool enabled, bool printStatus = true)
    {
        if (!enabled && Configuration.PluginEnabled)
            WakeTakeoverService.StopAll("DAD disabled by operator.");
        Configuration.PluginEnabled = enabled;
        Configuration.Save();
        TransportService.SetPluginEnabled(enabled);
        InvalidatePlannerPreviewCache("plugin enabled state changed");
        UpdateDtrBar();

        if (printStatus)
            PrintStatus(enabled ? "dad enabled." : "dad disabled.");
    }

    public void SetDebugUiEnabled(bool enabled)
    {
        Configuration.DebugUiEnabled = enabled;
        Configuration.Save();
        InvalidatePlannerPreviewCache("debug UI changed");
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
        setupWizardWindow.ResetToOrigin();
        miniStatusWindow.ResetToOrigin();
        clientReconnectWindow.ResetToOrigin();
        mainWindow.IsOpen = true;
        configWindow.IsOpen = true;
        setupWizardWindow.IsOpen = true;
        miniStatusWindow.IsOpen = true;
        if (!Configuration.RunAsServerDad)
            clientReconnectWindow.IsOpen = true;
        PrintStatus("Reset dad window positions to 1,1.");
    }

    public void JumpWindowsToRandomVisibleLocation()
    {
        mainWindow.QueueRandomVisibleJump();
        configWindow.QueueRandomVisibleJump();
        setupWizardWindow.QueueRandomVisibleJump();
        miniStatusWindow.QueueRandomVisibleJump();
        clientReconnectWindow.QueueRandomVisibleJump();
        mainWindow.IsOpen = true;
        configWindow.IsOpen = true;
        setupWizardWindow.IsOpen = true;
        miniStatusWindow.IsOpen = true;
        if (!Configuration.RunAsServerDad)
            clientReconnectWindow.IsOpen = true;
        PrintStatus("Queued random visible positions for the dad windows.");
    }

    public void OpenExternalLink(string url, string description)
    {
        PrintStatus($"{description}: {url}");
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
        var dutyIpcStatus = DutyIpcService.GetStatus();
        var bridgeStatus = QuestionableBridge.GetStatus();
        PrintStatus(
            $"IPC {(RunCoordinatorService.IsReady ? "ready" : "not ready")} | " +
            $"This instance {DadStatusText.FormatWorkerRole(localRun.WorkerRole)} | " +
            $"Authority view {authorityView.StateText} | " +
            $"Client {authorityView.ClientPerspectiveText} | " +
            $"{authorityView.FreshnessText} | " +
            $"Local-only {(localRun.LocalOnlyEnabled ? "on" : "off")} | " +
            $"Dad duty IPC {FormatDutyIpcStatus(dutyIpcStatus)} | " +
            $"Questionable bridge {FormatQuestionableBridgeStatus(bridgeStatus)} | " +
            $"Debug UI {(Configuration.DebugUiEnabled ? "on" : "off")} | " +
            $"Profile {(profile.Enabled ? "armed" : "off")} | " +
            $"Dad starts {(profile.AllowIpcStarts ? "allowed" : "blocked")} | " +
            $"Pool {characterPool.Characters.Count} row(s) / XADB {characterPool.XadbStatus.Availability} / peers {characterPool.PeerTransport.ConnectedPeerCount}");
        PrintStatus($"Dad duty IPC probe: {FormatDutyIpcProbeStatus(dutyIpcStatus)}");
        PrintStatus($"Dad duty IPC run: {FormatDutyIpcFailureStatus(dutyIpcStatus)}");
        PrintStatus($"Dad duty IPC cleanup: {FormatDutyIpcCleanupStatus(dutyIpcStatus)}");
        PrintStatus($"Questionable runtime bridge: {FormatQuestionableBridgeDetail(bridgeStatus)}");
        if (!string.IsNullOrWhiteSpace(bridgeStatus.LastBlocker))
            PrintStatus($"Questionable runtime bridge blocker: {bridgeStatus.LastBlocker}");
        PrintStatus($"Questionable cosmetic: {bridgeStatus.CosmeticPatchState}");
        if (!string.IsNullOrWhiteSpace(bridgeStatus.CosmeticLastBlocker))
            PrintStatus($"Questionable cosmetic blocker: {bridgeStatus.CosmeticLastBlocker}");
        PrintStatus($"Authority timeline: {FormatOperatorTextForChat(authorityView.TimelineText)}");
        PrintStatus($"Authority owner: {FormatOperatorTextForChat(authorityView.OwnershipText)}");
        PrintStatus($"Local run: {FormatRunStatusForChat(localRun)} | Payload {FormatOperatorTextForChat(FormatTaskPayload(localRun))}");
        PrintStatus($"Authority run: {FormatRunStatusForChat(authorityRun)} | Payload {FormatOperatorTextForChat(authorityView.PayloadText)}");
        PrintStatus($"Planner: {FormatOperatorTextForChat(plannerPreview.PlannerSummary)}");
        PrintStatus($"Planner request: {BuildPlannerRunRequestPreview().StatusSummary}");
    }

    private static string FormatDutyIpcStatus(DadDutyIpcStatus status)
    {
        var state = status.Registered
            ? "registered"
            : string.IsNullOrWhiteSpace(status.RegistrationState)
                ? "not registered"
                : status.RegistrationState;
        return $"{state} | mode {status.LastMode}";
    }

    private static string FormatQuestionableBridgeStatus(DadQuestionableReflectionBridgeStatus status)
        => status.Patched
            ? "patched"
            : status.Pending
                ? "pending"
                : status.QuestionableLoaded
                    ? "blocked"
                    : "not loaded";

    private static string FormatQuestionableBridgeDetail(DadQuestionableReflectionBridgeStatus status)
    {
        var loaded = status.QuestionableLoaded ? "loaded" : "not loaded";
        var running = status.QuestionableRunning ? "running" : "idle";
        var gate = status.DutyGateEnabled.HasValue
            ? status.DutyGateEnabled.Value ? "enabled" : "disabled"
            : "unknown";
        var version = string.IsNullOrWhiteSpace(status.QuestionableVersion) ? "unknown" : status.QuestionableVersion;
        var probe = status.LastProbeUtc?.ToString("O") ?? "never";
        return $"{loaded} | {status.PatchState} | {running} | gate {gate} | version {version} | last probe {probe}";
    }

    private static string FormatDutyIpcProbeStatus(DadDutyIpcStatus status)
    {
        if (!status.LastContentHasPathResult.HasValue)
            return "no ContentHasPath probe yet";

        var territory = status.LastContentHasPathTerritoryType == 0
            ? "none"
            : status.LastContentHasPathTerritoryType.ToString();
        var result = status.LastContentHasPathResult.Value ? "true" : "false";
        var selected = FormatDutyIpcDuty(status.LastContentHasPathSelectedContentFinderConditionId, status.LastContentHasPathSelectedDutyName);
        var blocker = string.IsNullOrWhiteSpace(status.LastContentHasPathBlocker)
            ? "none"
            : status.LastContentHasPathBlocker;
        return $"ContentHasPath({territory})={result} | candidates {status.LastContentHasPathCandidateCount} / compatible {status.LastContentHasPathCompatibleCandidateCount} | selected {selected} | blocker {blocker}";
    }

    private static string FormatDutyIpcFailureStatus(DadDutyIpcStatus status)
    {
        var runId = string.IsNullOrWhiteSpace(status.LastRunId) ? "none" : status.LastRunId;
        var territory = status.LastTerritoryType == 0 ? "none" : status.LastTerritoryType.ToString();
        var failure = string.IsNullOrWhiteSpace(status.LastFailure) ? "none" : status.LastFailure;
        return $"run {runId} | territory {territory} | bareMode {status.LastBareMode} | failure {failure}";
    }

    private static string FormatDutyIpcCleanupStatus(DadDutyIpcStatus status)
    {
        var cleanupUtc = status.LastCleanupUtc?.ToString("O") ?? "never";
        var failedCommands = status.LastCleanupFailedCommands.Count == 0
            ? "none"
            : string.Join(", ", status.LastCleanupFailedCommands);
        return $"{status.LastCleanupResult} | at {cleanupUtc} | failed {failedCommands}";
    }

    private static string FormatDutyIpcDuty(uint contentFinderConditionId, string dutyName)
    {
        if (contentFinderConditionId == 0)
            return "none";

        return string.IsNullOrWhiteSpace(dutyName)
            ? $"#{contentFinderConditionId}"
            : $"#{contentFinderConditionId} {dutyName}";
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
        var routedToServerDad = DadCoordinatorService.RequiresServerDadAuthority(request);
        if (!routedToServerDad)
        {
            return result.Status == DadRunStatus.Rejected
                ? "local request rejected"
                : "local request accepted";
        }

        return result.Status == DadRunStatus.Rejected
            ? "forwarded to Dad Coordinator, rejected"
            : "forwarded to Dad Coordinator, accepted";
    }

    private void PrimeAuthorityCacheFromRun(DadRunRequest request, DadRunResult result)
    {
        if (!DadCoordinatorService.RequiresServerDadAuthority(request) || Configuration.LocalOnlyModeEnabled)
            return;

        ApplyKnownAuthorityMetadata(result);
        if (string.IsNullOrWhiteSpace(result.AuthorityEndpoint) && result.AuthorityWorkerSessionId.IsEmpty)
            return;

        lock (authorityCacheGate)
        {
            cachedAuthorityRun = result.Clone();
            cachedAuthorityEndpoint = result.AuthorityEndpoint;
            nextAuthorityStatusRefreshUtc = DateTime.UtcNow + RemoteAuthorityStatusRefreshInterval;
            lastAuthorityRefreshSucceededUtc = DateTime.UtcNow;
        }
    }

    private void OnCommand(string command, string arguments)
    {
        var trimmed = arguments.Trim();

        if (trimmed.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            ToggleConfigUi();
            return;
        }

        if (trimmed.Equals("mini", StringComparison.OrdinalIgnoreCase))
        {
            ToggleMiniStatusUi();
            return;
        }

        if (trimmed.Equals("wizard", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("setup", StringComparison.OrdinalIgnoreCase))
        {
            OpenSetupWizard();
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

        // Feature batch A: gate for advanced/legacy options.
        if (trimmed.Equals("advanced", StringComparison.OrdinalIgnoreCase))
        {
            Configuration.AdvancedModeEnabled = !Configuration.AdvancedModeEnabled;
            Configuration.Save();
            PrintStatus($"Dad advanced mode {(Configuration.AdvancedModeEnabled ? "enabled - advanced options visible" : "disabled - advanced options hidden")}.");
            return;
        }

        if (trimmed.Equals("advanced on", StringComparison.OrdinalIgnoreCase))
        {
            Configuration.AdvancedModeEnabled = true;
            Configuration.Save();
            PrintStatus("Dad advanced mode enabled - advanced options visible.");
            return;
        }

        if (trimmed.Equals("advanced off", StringComparison.OrdinalIgnoreCase))
        {
            Configuration.AdvancedModeEnabled = false;
            Configuration.Save();
            PrintStatus("Dad advanced mode disabled - advanced options hidden.");
            return;
        }

        // Feature batch A: anonymized diagnostic dump for GitHub issues.
        if (trimmed.Equals("report", StringComparison.OrdinalIgnoreCase))
        {
            GenerateIssueReport();
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

        if (trimmed.Equals("run coordinator", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("run server", StringComparison.OrdinalIgnoreCase))
        {
            StartServerDemoRunFromShell();
            return;
        }

        if (trimmed.Equals("run roulette", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("run msq", StringComparison.OrdinalIgnoreCase))
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

        if (trimmed.Equals("test planner-groups", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("test groups", StringComparison.OrdinalIgnoreCase))
        {
            RunPlannerGroupIpcDiagnosticsFromShell();
            return;
        }

        if (trimmed.Equals("test profiles", StringComparison.OrdinalIgnoreCase))
        {
            RunProfileDiagnosticsFromShell();
            return;
        }

        if (trimmed.Equals("test launch-profiles", StringComparison.OrdinalIgnoreCase))
        {
            RunLaunchProfileDiagnosticsFromShell();
            return;
        }

        if (trimmed.Equals("test workers", StringComparison.OrdinalIgnoreCase))
        {
            RunWorkerDiagnosticsFromShell();
            return;
        }

        if (trimmed.StartsWith("test duty-ipc", StringComparison.OrdinalIgnoreCase))
        {
            RunDutyIpcDiagnosticsFromShell(trimmed["test duty-ipc".Length..].Trim());
            return;
        }

        if (trimmed.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            CancelActiveRunFromShell();
            return;
        }

        ToggleMainUi();
    }

    private void RunProfileDiagnosticsFromShell()
    {
        var catalogs = ProfileDirectoryService.GetCatalogs();
        var accounts = catalogs.Sum(static catalog => catalog.Accounts.Count);
        var characters = catalogs.Sum(static catalog => catalog.Accounts.Sum(static account => account.Characters.Count));
        var offline = catalogs.Count(static catalog => !catalog.OwnerOnline);
        PrintStatus($"Profiles: {catalogs.Count} owner catalog(s), {accounts} account(s), {characters} character profile(s), {offline} offline/read-only catalog(s).");
        foreach (var catalog in catalogs)
        {
            PrintStatus($"Profiles owner {catalog.OwnerClientInstanceId}/{catalog.OwnerWorkerSessionId}: online={catalog.OwnerOnline}, readOnly={catalog.ReadOnly}, accounts={catalog.Accounts.Count}, generated={catalog.GeneratedAtUtc:O}.");
        }
    }

    private void RunLaunchProfileDiagnosticsFromShell()
    {
        var profiles = SchedulerService.GetLaunchProfiles();
        PrintStatus($"Launch profiles: {profiles.Count} candidate(s).");
        foreach (var profile in profiles)
        {
            var pathState = File.Exists(profile.BatchPath) ? "exists" : "missing";
            var allowed = false;
            try
            {
                allowed = Path.GetExtension(profile.BatchPath).Equals(".bat", StringComparison.OrdinalIgnoreCase) &&
                          Path.GetFullPath(profile.BatchPath).StartsWith(
                              Path.GetFullPath(Configuration.ClientBootDirectory) + Path.DirectorySeparatorChar,
                              StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                allowed = false;
            }
            PrintStatus($"Launch {profile.DisplayName} rev {profile.Revision}: account={profile.AccountKey}, enabled={profile.Enabled}, auto={profile.AllowAutoStart}, dry={profile.DryRun}, path={pathState}/{(allowed ? "allowed" : "blocked")}, expected={profile.ExpectedCharacterKeys.Count}.");
        }
    }

    private void RunWorkerDiagnosticsFromShell()
    {
        var status = WorkerExecutionService.GetStatus();
        var transport = TransportService.CurrentTransport;
        var authProtocol = string.IsNullOrWhiteSpace(transport.LastAuthOrProtocolError) ? "(none)" : transport.LastAuthOrProtocolError;
        var publishEpoch = string.IsNullOrWhiteSpace(transport.HubRosterPublishEpochId) ? "(none)" : transport.HubRosterPublishEpochId;
        PrintStatus($"Worker local {status.WorkerSessionId}: run={status.RunId}, role={status.Role}, state={status.State}, terminal={status.IsTerminal}, summary={status.Summary}");
        PrintStatus($"Worker peers: {transport.KnownParticipants.Count} discovered, authority={transport.AuthorityWorkerSessionId}, endpoint={transport.AuthorityEndpoint}.");
        PrintStatus($"LAN transport: configured={transport.ConfiguredEndpoint}, advertised={transport.AdvertisedEndpoint}, secretRequired={transport.SharedSecretRequired}, secretConfigured={transport.SharedSecretConfigured}, authProtocol={authProtocol}, publish={publishEpoch}/{transport.HubRosterPublishGeneration}, published={transport.PublishedParticipantCount}, known={transport.KnownParticipantCount}.");
        foreach (var participant in transport.KnownParticipants)
            PrintStatus($"Worker peer {participant.WorkerSessionId}: {participant.ActiveCharacterKey}, state={participant.State}, heartbeat={participant.LastHeartbeatUtc:O}, route=Dad Coordinator hub.");
    }

    private void RunDutyIpcDiagnosticsFromShell(string arguments)
    {
        var tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        DadDutyIpcDiagnostic diagnostic;
        if (tokens.Length == 0 || tokens[0].Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            diagnostic = DutyIpcService.DiagnoseCurrentTerritory();
        }
        else if (tokens.Length == 2 &&
                 tokens[0].Equals("territory", StringComparison.OrdinalIgnoreCase) &&
                 uint.TryParse(tokens[1], out var territoryType))
        {
            diagnostic = DutyIpcService.DiagnoseTerritory(territoryType);
        }
        else if (tokens.Length == 2 &&
                 tokens[0].Equals("cfc", StringComparison.OrdinalIgnoreCase) &&
                 uint.TryParse(tokens[1], out var contentFinderConditionId))
        {
            diagnostic = DutyIpcService.DiagnoseContentFinderCondition(contentFinderConditionId);
        }
        else
        {
            PrintStatus("Dad duty IPC diagnostics usage: /dad test duty-ipc current | territory <id> | cfc <id>");
            return;
        }

        PrintStatus($"Dad duty IPC diag state: {FormatDutyIpcDiagnosticState(diagnostic)}");
        PrintStatus($"Dad duty IPC diag probe: {FormatDutyIpcDiagnosticProbe(diagnostic)}");
        PrintStatus($"Dad duty IPC diag route: {FormatDutyIpcDiagnosticRoute(diagnostic)}");
        if (!string.IsNullOrWhiteSpace(diagnostic.Blocker))
            PrintStatus($"Dad duty IPC diag blocker: {diagnostic.Blocker}");
    }

    private static string FormatDutyIpcDiagnosticState(DadDutyIpcDiagnostic diagnostic)
    {
        var ipc = diagnostic.Registered
            ? "ipc registered"
            : $"ipc {FormatDutyIpcDiagnosticText(diagnostic.RegistrationState, "not registered")}";
        return $"{diagnostic.Query} | {ipc} | mode {diagnostic.Mode}";
    }

    private static string FormatDutyIpcDiagnosticProbe(DadDutyIpcDiagnostic diagnostic)
    {
        var result = diagnostic.ContentHasPathResult ? "true" : "false";
        var selected = FormatDutyIpcDuty(diagnostic.ContentHasPathSelectedContentFinderConditionId, diagnostic.ContentHasPathSelectedDutyName);
        var blocker = FormatDutyIpcDiagnosticText(diagnostic.ContentHasPathBlocker, "none");
        var requested = diagnostic.RequestedContentFinderConditionId == 0
            ? string.Empty
            : $" | requested {FormatDutyIpcDuty(diagnostic.RequestedContentFinderConditionId, diagnostic.RequestedDutyName)} route {FormatDutyIpcRouteMatch(diagnostic)}";
        return $"territory {diagnostic.TerritoryType} | ContentHasPath={result} | candidates {diagnostic.CandidateCount} / compatible {diagnostic.CompatibleCandidateCount} | selected {selected} | blocker {blocker}{requested}";
    }

    private static string FormatDutyIpcDiagnosticRoute(DadDutyIpcDiagnostic diagnostic)
    {
        var availability = diagnostic.RouteAvailable ? "available" : "blocked";
        var selected = FormatDutyIpcDuty(diagnostic.RouteContentFinderConditionId, diagnostic.RouteDutyName);
        var blocker = FormatDutyIpcDiagnosticText(diagnostic.RouteBlocker, "none");
        return $"{diagnostic.Route} | {availability} | selected {selected} | blocker {blocker}";
    }

    private static string FormatDutyIpcRouteMatch(DadDutyIpcDiagnostic diagnostic)
    {
        if (!diagnostic.RequestedDutyRouteMatch.HasValue)
            return "unknown";

        if (diagnostic.RequestedDutyRouteMatch.Value)
            return "match";

        return $"blocked ({FormatDutyIpcDiagnosticText(diagnostic.RequestedDutyBlocker, "no matching route")})";
    }

    private static string FormatDutyIpcDiagnosticText(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private void RunPlannerGroupIpcDiagnosticsFromShell()
    {
        var checks = new List<(string Name, string Status, string Detail)>();

        checks.Add(CheckBlockedPlannerGroupPreview(
            "missing group preview",
            GetPlannerGroupPreviewJson(string.Empty),
            "required"));
        checks.Add(CheckBlockedPlannerGroupPreview(
            "unknown group preview",
            GetPlannerGroupPreviewJson("__dad_missing_planner_group__"),
            "not found"));

        var groups = PlannerGroups.ToList();
        var selectedGroup = GetSelectedPlannerGroup() ?? groups.FirstOrDefault();
        if (selectedGroup == null)
        {
            checks.Add(("valid group preview", "SKIP", "No saved planner groups."));
            checks.Add(("dry-run DTO start", "SKIP", "No saved planner groups."));
        }
        else
        {
            checks.Add(CheckReadablePlannerGroupPreview(
                $"valid group preview ({selectedGroup.DisplayName})",
                GetPlannerGroupPreviewJson(selectedGroup.GroupId),
                selectedGroup.GroupId));
            checks.Add(CheckPlannerGroupDryRun(
                $"dry-run DTO start ({selectedGroup.DisplayName})",
                StartPlannerGroupFromJson(DadIpcJson.Serialize(new DadPlannerGroupStartRequest
                {
                    GroupId = selectedGroup.GroupId,
                    DryRun = true,
                    RequestedBy = "dad-self-test",
                }))));
        }

        var duplicateName = groups
            .Select(static group => group.DisplayName.Trim())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        checks.Add(string.IsNullOrWhiteSpace(duplicateName)
            ? ("ambiguous name preview", "SKIP", "No duplicate planner group display names.")
            : CheckBlockedPlannerGroupPreview(
                $"ambiguous name preview ({duplicateName})",
                GetPlannerGroupPreviewJson(duplicateName),
                "matches"));

        foreach (var check in checks)
            PrintStatus($"Planner group IPC diag {check.Status}: {check.Name} - {check.Detail}");

        var failedCount = checks.Count(static check => check.Status == "FAIL");
        var skippedCount = checks.Count(static check => check.Status == "SKIP");
        var passedCount = checks.Count(static check => check.Status == "PASS");
        PrintStatus($"Planner group IPC diagnostics done: {passedCount} pass, {failedCount} fail, {skippedCount} skip.");
    }

    private static (string Name, string Status, string Detail) CheckBlockedPlannerGroupPreview(
        string name,
        string previewJson,
        string expectedText)
    {
        var preview = DadIpcJson.Deserialize<DadPlannerRunRequestPreview>(previewJson);
        if (preview == null)
            return (name, "FAIL", "Preview JSON was unreadable.");

        var reasonText = string.Join(" | ", new[]
            {
                preview.StatusSummary,
                preview.BlockedReason,
                preview.ContractPreview.Startability,
            }
            .Concat(preview.ContractPreview.Blockers)
            .Concat(preview.ModuleBlockers.Select(static blocker => blocker.Summary))
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
        var blocked = !preview.CanStart &&
                      !preview.ContractPreview.CanStart &&
                      string.Equals(preview.ContractPreview.Startability, "Blocked", StringComparison.OrdinalIgnoreCase);
        var hasContract = !string.IsNullOrWhiteSpace(preview.ContractPreviewJson) &&
                          preview.ContractPreview.Blockers.Count > 0;
        var hasExpectedReason = reasonText.Contains(expectedText, StringComparison.OrdinalIgnoreCase);
        if (blocked && hasContract && hasExpectedReason)
            return (name, "PASS", preview.StatusSummary);

        return (name, "FAIL", $"Expected blocked contract containing '{expectedText}', got '{reasonText}'.");
    }

    private static (string Name, string Status, string Detail) CheckReadablePlannerGroupPreview(
        string name,
        string previewJson,
        string expectedGroupId)
    {
        var preview = DadIpcJson.Deserialize<DadPlannerRunRequestPreview>(previewJson);
        if (preview == null)
            return (name, "FAIL", "Preview JSON was unreadable.");

        if (string.Equals(preview.PlannerPreview.SelectedPlannerGroupId, expectedGroupId, StringComparison.OrdinalIgnoreCase) &&
            preview.PlannerPreview.UsingPlannerGroup &&
            !string.IsNullOrWhiteSpace(preview.ContractPreviewJson))
        {
            return (name, "PASS", preview.StatusSummary);
        }

        return (name, "FAIL", "Preview did not echo selected group id or contract JSON.");
    }

    private static (string Name, string Status, string Detail) CheckPlannerGroupDryRun(
        string name,
        string resultJson)
    {
        var result = DadIpcJson.Deserialize<DadRunResult>(resultJson);
        if (result == null)
            return (name, "FAIL", "Result JSON was unreadable.");

        if (result.Status is DadRunStatus.Idle or DadRunStatus.Rejected &&
            string.Equals(result.RequestedBy, "dad-self-test", StringComparison.OrdinalIgnoreCase))
        {
            return (name, "PASS", $"{result.Status}: {result.Summary}");
        }

        return (name, "FAIL", $"Expected idle/rejected dry-run result, got {result.Status}: {result.Summary}");
    }

    private void SetupDtrBar()
    {
        dtrEntry = DtrBar.Get(PluginInfo.DisplayName);
        dtrEntry.OnClick = _ => SetPluginEnabled(!Configuration.PluginEnabled, printStatus: false);
    }

    private void OpenSetupWizardOnce()
    {
        if (Configuration.SetupWizardLoaded)
            return;

        setupWizardWindow.OpenLanding();
        Configuration.SetupWizardLoaded = true;
        Configuration.Save();
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

    // Feature batch A: write an anonymized diagnostic dump for GitHub issues (/dad report).
    public void GenerateIssueReport()
    {
        try
        {
            LastIssueReportStatus = "Generating anonymized Dad issue report...";
            var pool = CharacterIntelligenceService.CurrentPool;
            var version = GetType().Assembly.GetName().Version?.ToString() ?? "unknown";
            var lines = new List<string>
            {
                $"# Dad issue report ({DateTime.UtcNow:u})",
                $"{PluginInfo.DisplayName} v{version} — character names / account ids anonymized (numpty0 / acct0 / ...).",
                string.Empty,
                "## Config",
                $"- PluginEnabled={Configuration.PluginEnabled} ServerDad={Configuration.RunAsServerDad} LocalOnly={Configuration.LocalOnlyModeEnabled} Advanced={Configuration.AdvancedModeEnabled} PartyValidationOverride={Configuration.PartyValidationOverrideEnabled} AllowRemoteCmd={Configuration.AllowRemoteCommandExecution}",
                $"- CombatRotationMode={Configuration.CombatRotationMode} DtrBarEnabled={Configuration.DtrBarEnabled}",
                $"- Transport role={(Configuration.RunAsServerDad ? "server" : "client")} listen={Configuration.ServerListenHost}:{Configuration.ServerListenPort} server={Configuration.ServerDadHost}:{Configuration.ServerDadPort} protocol={DadHubProtocol.CurrentVersion} sharedSecretConfigured={!string.IsNullOrWhiteSpace(Configuration.TransportSharedSecret)}",
                $"- RunHistory={Configuration.RunHistory?.Count ?? 0} PlannerGroups={Configuration.PlannerGroups?.Count ?? 0} LaunchProfiles={Configuration.LaunchProfiles?.Count ?? 0}",
                string.Empty,
                "## Transport",
                "```json",
                DadIpcJson.Serialize(TransportService.CurrentTransport),
                "```",
                "## Current run",
                "```json",
                DadIpcJson.Serialize(RunCoordinatorService.GetLocalResult()),
                "```",
                "## Character pool",
                "```json",
                DadIpcJson.Serialize(pool),
                "```",
            };

            var anonymized = DadIssueReport.Anonymize(string.Join("\n", lines), DadIssueReport.BuildAnonymizationMap(pool, Configuration, PresenceService));
            var path = Path.Combine(PluginInterface.ConfigDirectory.FullName, $"dad-issue-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md");
            File.WriteAllText(path, anonymized);
            LastIssueReportPath = path;
            LastIssueReportUtc = DateTime.UtcNow;
            LastIssueReportStatus = $"Dad issue report written: {path}";
            PrintStatus($"Dad issue report written (char names anonymized): {path}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[dad] Failed to generate issue report.");
            LastIssueReportStatus = $"Failed to generate Dad issue report: {ex.Message}";
            LastIssueReportPath = string.Empty;
            LastIssueReportUtc = DateTime.UtcNow;
            PrintStatus("Failed to generate Dad issue report; see /xllog for detail.");
        }
    }

    // Per-frame step isolation (review H7): one faulting service must not throw out of the
    // Dalamud framework tick (which can auto-unsubscribe the handler and silently freeze the plugin).
    private void RunFrameworkStep(string stepName, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[dad] Framework step '{Step}' threw; continuing.", stepName);
        }
    }

    private void ObserveLocalRuntimeReadiness()
    {
        var signature = CaptureLocalRuntimeReadinessSignature();
        if (localRuntimeReadinessTracker.WouldChange(signature))
        {
            // A takeover callback can acquire/release suppression after the normal presence pass.
            // Refresh once on a semantic edge so the immediate heartbeat carries final post-AR truth.
            PresenceService.Update(
                CharacterIntelligenceService.CurrentPool,
                TransportService.CurrentTransport.ListenerEndpoint);
            signature = CaptureLocalRuntimeReadinessSignature();
        }

        if (!localRuntimeReadinessTracker.Observe(signature, out var revision))
            return;

        InvalidatePlannerPreviewCache($"local runtime readiness revision {revision}");
        SchedulerService.WakeForRuntimeReadiness(PresenceService.WorkerSessionId);
        TransportService.NotifyLocalRuntimeReadinessChanged(revision);
    }

    private DadRuntimeReadinessSignature CaptureLocalRuntimeReadinessSignature()
    {
        var participant = PresenceService.BuildSnapshotCopy();
        var autoRetainer = AutoRetainerIpcService.Inspect();
        return DadRuntimeReadinessSignature.Create(
            participant,
            autoRetainer.SuppressionReadable,
            autoRetainer.IsSuppressed,
            autoRetainer.SuppressionOwnedByDad,
            autoRetainer.CharacterPostprocessOwnedByDad,
            WakeTakeoverService.GetActiveStatus());
    }

    private void OnRemoteRuntimeReadinessChanged(DadWorkerSessionId workerSessionId, long revision)
    {
        // Transport applies the heartbeat and refreshes its participant projection before invoking
        // this callback on the framework thread, so the scheduler can consume the edge this tick.
        InvalidatePlannerPreviewCache($"remote runtime readiness revision {revision} ({workerSessionId.Value})");
        SchedulerService.WakeForRuntimeReadiness(workerSessionId);
        CharacterIntelligenceService.RefreshLocalCharacterPool("remote-runtime-readiness", logRefresh: false);
    }

    private void LearnRetainedTransportRosterKnowledge()
    {
        var revision = TransportService.RosterCatalogCacheRevision;
        var pool = CharacterIntelligenceService.CurrentPool;
        if (!rosterKnowledgeLearningCursor.TryAdvance(revision, pool.LastUpdatedUtc))
            return;

        RosterCatalogService.LearnRetainedTransportKnowledge(pool);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        RunFrameworkStep("FlushDebouncedUiWrites", () => FlushDebouncedUiWrites(force: false));
        RunFrameworkStep("UpdateDtrBar", UpdateDtrBar);
        RunFrameworkStep("RuntimeIdentity", () =>
        {
            if (ClientState.IsLoggedIn && ObjectTable.LocalPlayer != null)
            {
                var player = ObjectTable.LocalPlayer;
                ConfigManager.EnsureRuntimeIdentity(
                    player.Name.ToString(),
                    player.HomeWorld.Value.Name.ToString(),
                    Configuration.ClientAccountId);

                if (!string.IsNullOrWhiteSpace(ConfigManager.CurrentAccountId)
                    && !string.Equals(Configuration.LastAccountId, ConfigManager.CurrentAccountId, StringComparison.Ordinal))
                {
                    Configuration.LastAccountId = ConfigManager.CurrentAccountId;
                    Configuration.Save();
                }
            }
        });

        RunFrameworkStep("CharacterIntelligence", () => CharacterIntelligenceService.Update());
        RunFrameworkStep("VermaxionReservation", VermaxionIpcService.Update);
        RunFrameworkStep("Presence", () => PresenceService.Update(CharacterIntelligenceService.CurrentPool, TransportService.CurrentTransport.ListenerEndpoint));
        RunFrameworkStep("PartyInviteAcceptance", PartyInviteGateway.UpdateAcceptance);
        RunFrameworkStep("WakeTakeover", () =>
        {
            if (!Configuration.RunAsServerDad && !TransportService.CurrentTransport.AuthorityRoutable)
                WakeTakeoverService.OnCoordinatorDisconnected();
            WakeTakeoverService.Update();
        });
        RunFrameworkStep("RuntimeReadinessEdges", ObserveLocalRuntimeReadiness);
        RunFrameworkStep("TransportHeartbeat", () => TransportService.UpdateHeartbeat(
            PresenceService.BuildSnapshotCopy(),
            Configuration.PluginEnabled,
            Configuration.LocalOnlyModeEnabled));
        RunFrameworkStep("PendingTakeoverCancellation", SchedulerService.UpdatePendingTakeoverCancellations);
        RunFrameworkStep("PendingEarlyAssignmentCancellation", SchedulerService.UpdatePendingEarlyAssignmentCancellations);
        RunFrameworkStep("RetainedRosterKnowledge", LearnRetainedTransportRosterKnowledge);
        RunFrameworkStep("ClientReconnectWindow", () =>
        {
            var showReconnect = Configuration.PluginEnabled &&
                                !Configuration.RunAsServerDad &&
                                !Configuration.LocalOnlyModeEnabled &&
                                !TransportService.CurrentTransport.AuthorityRoutable;
            clientReconnectWindow.IsOpen = showReconnect;
        });
        RunFrameworkStep("ProfileDirectory", () => ProfileDirectoryService.Update());
        RunFrameworkStep("WorkerExecution", () => WorkerExecutionService.Update());
        RunFrameworkStep("SchedulerEnqueue", () => SchedulerService.TickScheduleEnqueue());
        RunFrameworkStep("SchedulerUpdate", () =>
        {
            if (CanAdvanceSchedulerQueue())
            {
                SchedulerService.UpdateWithScheduleRepeatBoundary(
                    ResolvePlannerGroup,
                    BuildSchedulerPlannerPreview,
                    StartScheduledPlannerRequest,
                    () => GetVisibleRunState().VisibleRun);
            }
        });
        RunFrameworkStep("Coordinator", () => RunCoordinatorService.Update());
        RunFrameworkStep("CompletionActions", () => DadCompletionActionRunner.Update(Configuration, Log));
        RunFrameworkStep("DutyIpc", () => DutyIpcService.Update());
        RunFrameworkStep("DutyIpcRegister", () => DutyIpcService.EnsureRegistered());
    }

    private void OnLogin()
    {
        UpdateDtrBar();
        CharacterIntelligenceService.RefreshLocalCharacterPool("login", logRefresh: false);
        RosterCatalogService.RefreshCatalog(CharacterIntelligenceService.CurrentPool);
        PresenceService.Update(CharacterIntelligenceService.CurrentPool, TransportService.CurrentTransport.ListenerEndpoint);
    }
}

internal sealed class DadPlannerUiSnapshot
{
    public long Generation { get; init; }
    public DateTime RebuiltAtUtc { get; init; } = DateTime.UtcNow;
    public string RebuildReason { get; init; } = string.Empty;
    public DadCharacterPool CuratedPool { get; init; } = new();
    public DadActivityPreset PlannerPreview { get; init; } = new();
    public DadPlannerRunRequestPreview RequestPreview { get; init; } = new();
    public DadSchedulerPreview SchedulerPreview { get; set; } = new();
    public IReadOnlyList<DadRosterAccountOption> AccountOptions { get; init; } = [];
    public IReadOnlyList<DadLaunchProfile> LaunchProfiles { get; init; } = [];
    public IReadOnlyList<DadPlannerGroup> PlannerGroups { get; init; } = [];
    public IReadOnlyList<DadPlannerLanePreviewSnapshot> LanePreviews { get; init; } = [];
    public DadPlannerDutyOption? SelectedDuty { get; init; }
    public IReadOnlyList<DadPlannerRouletteOption> RouletteOptions { get; init; } = [];
    public DadPlannerRouletteOption? SelectedRoulette { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<DadAcquiredCharacter>> CharactersByAccountKey { get; init; } =
        new Dictionary<string, IReadOnlyList<DadAcquiredCharacter>>(StringComparer.OrdinalIgnoreCase);

    public DadPlannerLanePreviewSnapshot? GetLanePreview(DadPlannerActivityMode activityMode)
        => LanePreviews.FirstOrDefault(preview => preview.Lane.ActivityMode == activityMode);

    public IReadOnlyList<DadAcquiredCharacter> GetCharactersForAccount(DadAccountKey accountKey)
        => !accountKey.IsEmpty && CharactersByAccountKey.TryGetValue(accountKey.Value, out var characters)
            ? characters
            : [];
}

internal sealed record DadPlannerLanePreviewSnapshot(
    DadPlannerLaneDefinition Lane,
    bool IsSelected,
    DadPlannerRunRequestPreview RequestPreview);

internal sealed class DadPlannerUiCacheStats
{
    public long Generation { get; init; }
    public long HitCount { get; init; }
    public long MissCount { get; init; }
    public long SchedulerHitCount { get; init; }
    public long SchedulerMissCount { get; init; }
    public double LastRebuildMilliseconds { get; init; }
    public double MaxRebuildMilliseconds { get; init; }
    public DateTime LastRebuiltAtUtc { get; init; }
    public string LastRebuildReason { get; init; } = string.Empty;
}

internal readonly record struct DadPlannerUiCacheKey(
    long Generation,
    bool DebugUiEnabled,
    bool PluginEnabled,
    bool LocalOnlyModeEnabled,
    int CombatRotationMode,
    long CatalogVersion,
    long PoolUpdatedAtTicks,
    int CharacterCount,
    int ConnectedPeerCount,
    int KnownParticipantCount,
    int PeerResponseCount,
    long XadbSnapshotTicks,
    int LaunchProfilesToken,
    int RunRevisionToken);

internal readonly record struct DadPlannerSchedulerCacheKey(
    long Generation,
    long PoolUpdatedAtTicks,
    long CatalogVersion,
    int SchedulerToken,
    int LaunchProfilesToken,
    int RunRevisionToken);
