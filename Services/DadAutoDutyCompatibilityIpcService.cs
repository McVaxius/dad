using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadAutoDutyCompatibilityIpcStatus
{
    public bool ConfigEnabled { get; set; }
    public bool Registered { get; set; }
    public bool RealAutoDutyLoaded { get; set; }
    public string RegistrationState { get; set; } = string.Empty;
    public uint LastTerritoryType { get; set; }
    public string LastMode { get; set; } = "Support";
    public string LastRunId { get; set; } = string.Empty;
    public bool LastBareMode { get; set; }
    public string LastFailure { get; set; } = string.Empty;
    public uint LastContentHasPathTerritoryType { get; set; }
    public bool? LastContentHasPathResult { get; set; }
    public int LastContentHasPathCandidateCount { get; set; }
    public int LastContentHasPathCompatibleCandidateCount { get; set; }
    public uint LastContentHasPathSelectedContentFinderConditionId { get; set; }
    public string LastContentHasPathSelectedDutyName { get; set; } = string.Empty;
    public string LastContentHasPathBlocker { get; set; } = string.Empty;
    public DateTime? LastContentHasPathUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DadAutoDutyCompatibilityIpcStatus Clone()
        => new()
        {
            ConfigEnabled = ConfigEnabled,
            Registered = Registered,
            RealAutoDutyLoaded = RealAutoDutyLoaded,
            RegistrationState = RegistrationState,
            LastTerritoryType = LastTerritoryType,
            LastMode = LastMode,
            LastRunId = LastRunId,
            LastBareMode = LastBareMode,
            LastFailure = LastFailure,
            LastContentHasPathTerritoryType = LastContentHasPathTerritoryType,
            LastContentHasPathResult = LastContentHasPathResult,
            LastContentHasPathCandidateCount = LastContentHasPathCandidateCount,
            LastContentHasPathCompatibleCandidateCount = LastContentHasPathCompatibleCandidateCount,
            LastContentHasPathSelectedContentFinderConditionId = LastContentHasPathSelectedContentFinderConditionId,
            LastContentHasPathSelectedDutyName = LastContentHasPathSelectedDutyName,
            LastContentHasPathBlocker = LastContentHasPathBlocker,
            LastContentHasPathUtc = LastContentHasPathUtc,
            UpdatedAtUtc = UpdatedAtUtc,
        };
}

public sealed class DadAutoDutyCompatibilityDiagnostic
{
    public string Query { get; set; } = string.Empty;
    public bool ConfigEnabled { get; set; }
    public bool Registered { get; set; }
    public bool RealAutoDutyLoaded { get; set; }
    public string RegistrationState { get; set; } = string.Empty;
    public string Mode { get; set; } = "Support";
    public string Route { get; set; } = string.Empty;
    public uint TerritoryType { get; set; }
    public uint RequestedContentFinderConditionId { get; set; }
    public string RequestedDutyName { get; set; } = string.Empty;
    public bool? RequestedDutyRouteMatch { get; set; }
    public string RequestedDutyBlocker { get; set; } = string.Empty;
    public bool ContentHasPathResult { get; set; }
    public int CandidateCount { get; set; }
    public int CompatibleCandidateCount { get; set; }
    public uint ContentHasPathSelectedContentFinderConditionId { get; set; }
    public string ContentHasPathSelectedDutyName { get; set; } = string.Empty;
    public string ContentHasPathBlocker { get; set; } = string.Empty;
    public bool RouteAvailable { get; set; }
    public uint RouteContentFinderConditionId { get; set; }
    public string RouteDutyName { get; set; } = string.Empty;
    public string RouteBlocker { get; set; } = string.Empty;
    public string Blocker { get; set; } = string.Empty;
}

public sealed class DadAutoDutyCompatibilityIpcService : IDisposable
{
    private const string AutoDutyInternalName = "AutoDuty";
    private const string AutoDutyDisplayName = "AutoDuty";
    private const string AutoDutyDisplayNameSpaced = "Auto Duty";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Configuration configuration;
    private readonly DadCoordinatorService coordinatorService;
    private readonly DadPresetProviderService presetProviderService;
    private readonly IPluginLog log;
    private readonly List<Action> disposeActions = [];
    private readonly DadAutoDutyCompatibilityIpcStatus status = new();

    private string dutyMode = "Support";
    private bool unsynced;
    private bool lastRunTerminal = true;

    private sealed class DadAutoDutyContentPathProbe
    {
        public uint TerritoryType { get; init; }
        public bool Result { get; init; }
        public int CandidateCount { get; init; }
        public int CompatibleCandidateCount { get; init; }
        public uint SelectedContentFinderConditionId { get; init; }
        public string SelectedDutyName { get; init; } = string.Empty;
        public string Blocker { get; init; } = string.Empty;
    }

    public DadAutoDutyCompatibilityIpcService(
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        DadCoordinatorService coordinatorService,
        DadPresetProviderService presetProviderService,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.configuration = configuration;
        this.coordinatorService = coordinatorService;
        this.presetProviderService = presetProviderService;
        this.log = log;

        coordinatorService.StatusChanged += OnRunStatusChanged;
        UpdateRegistrationState();
    }

    public void Dispose()
    {
        coordinatorService.StatusChanged -= OnRunStatusChanged;
        Unregister();
    }

    public DadAutoDutyCompatibilityIpcStatus GetStatus()
    {
        RefreshStatus();
        return status.Clone();
    }

    public DadAutoDutyCompatibilityDiagnostic DiagnoseCurrentTerritory()
    {
        UpdateRegistrationState();
        if (!Plugin.ClientState.IsLoggedIn)
        {
            return BuildUnavailableDiagnostic(
                "current",
                0,
                0,
                null,
                "Current territory unavailable; log in before running this diagnostic.");
        }

        return DiagnoseTerritory(Plugin.ClientState.TerritoryType, "current", null, 0);
    }

    public DadAutoDutyCompatibilityDiagnostic DiagnoseTerritory(uint territoryType)
    {
        UpdateRegistrationState();
        return DiagnoseTerritory(territoryType, $"territory {territoryType}", null, 0);
    }

    public DadAutoDutyCompatibilityDiagnostic DiagnoseContentFinderCondition(uint contentFinderConditionId)
    {
        UpdateRegistrationState();
        var duty = presetProviderService.GetPlannerDutyOption(contentFinderConditionId);
        if (duty == null)
        {
            return BuildUnavailableDiagnostic(
                $"cfc {contentFinderConditionId}",
                0,
                contentFinderConditionId,
                null,
                $"Dad planner catalog has no ContentFinderCondition row #{contentFinderConditionId}.");
        }

        return DiagnoseTerritory(duty.TerritoryType, $"cfc {contentFinderConditionId}", duty, contentFinderConditionId);
    }

    public void UpdateRegistrationState()
    {
        var realAutoDutyLoaded = IsRealAutoDutyLoaded();
        status.ConfigEnabled = configuration.EnableAutoDutyCompatibilityIpc;
        status.RealAutoDutyLoaded = realAutoDutyLoaded;

        if (!configuration.EnableAutoDutyCompatibilityIpc)
        {
            if (status.Registered)
                Unregister();

            status.RegistrationState = "Disabled by Dad config.";
            status.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        if (realAutoDutyLoaded)
        {
            if (status.Registered)
                Unregister();

            status.RegistrationState = "Disabled because real AutoDuty is loaded.";
            status.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        if (status.Registered)
        {
            status.RegistrationState = "Registered.";
            status.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        try
        {
            Register<uint, bool>(DadAutoDutyCompatibilityIpcContract.ContentHasPath, ContentHasPath);
            Register<string, string, object>(DadAutoDutyCompatibilityIpcContract.SetConfig, SetConfig);
            Register<uint, int, bool, object>(DadAutoDutyCompatibilityIpcContract.Run, Run);
            Register<bool>(DadAutoDutyCompatibilityIpcContract.IsStopped, IsStopped);
            Register<object>(DadAutoDutyCompatibilityIpcContract.Stop, Stop);

            status.Registered = true;
            status.RegistrationState = "Registered.";
            status.LastFailure = string.Empty;
            log.Information("[dad][AutoDutyCompat] Registered AutoDuty-compatible IPC shim.");
        }
        catch (Exception ex)
        {
            Unregister();
            status.RegistrationState = "Registration failed.";
            status.LastFailure = ex.Message;
            log.Warning(ex, "[dad][AutoDutyCompat] Failed to register AutoDuty-compatible IPC shim.");
        }
        finally
        {
            status.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private bool ContentHasPath(uint territoryType)
    {
        try
        {
            var probe = EvaluateContentHasPath(territoryType);
            RecordContentHasPathProbe(probe);

            log.Debug(
                "[dad][AutoDutyCompat] ContentHasPath territory={TerritoryType} result={Result} candidates={CandidateCount} compatible={CompatibleCandidateCount} selected={SelectedContentFinderConditionId}:{SelectedDutyName} blocker={Blocker}.",
                territoryType,
                probe.Result,
                probe.CandidateCount,
                probe.CompatibleCandidateCount,
                probe.SelectedContentFinderConditionId,
                probe.SelectedDutyName,
                probe.Blocker);
            return probe.Result;
        }
        catch (Exception ex)
        {
            RecordContentHasPathFailure(territoryType, ex.Message);
            log.Warning(ex, "[dad][AutoDutyCompat] ContentHasPath failed for territory {TerritoryType}.", territoryType);
            return false;
        }
    }

    private object SetConfig(string key, string value)
    {
        var normalizedKey = key.Trim();
        var normalizedValue = value.Trim();

        if (normalizedKey.Equals("Unsynced", StringComparison.OrdinalIgnoreCase))
        {
            if (bool.TryParse(normalizedValue, out var parsed))
                unsynced = parsed;
            else
                status.LastFailure = $"Unable to parse AutoDuty Unsynced config value '{value}'.";
        }
        else if (normalizedKey.Equals("dutyModeEnum", StringComparison.OrdinalIgnoreCase))
        {
            dutyMode = string.IsNullOrWhiteSpace(normalizedValue) ? "Support" : normalizedValue;
        }
        else
        {
            log.Debug(
                "[dad][AutoDutyCompat] Accepted unused AutoDuty.SetConfig {Key}={Value}.",
                normalizedKey,
                normalizedValue);
        }

        status.LastMode = BuildModeStatusText();
        status.UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    private object Run(uint territoryType, int loops, bool bareMode)
    {
        UpdateRegistrationState();

        status.LastTerritoryType = territoryType;
        status.LastBareMode = bareMode;
        status.LastMode = BuildModeStatusText();
        status.UpdatedAtUtc = DateTime.UtcNow;
        lastRunTerminal = false;

        if (!status.Registered)
        {
            var rejected = DadRunResult.Rejected(null, status.RegistrationState);
            status.LastRunId = rejected.RequestId;
            status.LastFailure = status.RegistrationState;
            lastRunTerminal = true;
            return DadIpcJson.Serialize(rejected);
        }

        var route = ResolveRoute();
        var loopCount = Math.Max(1, loops);
        if (!TryResolveDuty(territoryType, route, logAmbiguousSelection: true, out var duty, out var blocker) || duty == null)
        {
            var rejected = DadRunResult.Rejected(null, blocker);
            status.LastRunId = rejected.RequestId;
            status.LastFailure = blocker;
            lastRunTerminal = true;
            log.Warning(
                "[dad][AutoDutyCompat] Run rejected territory={TerritoryType} route={Route}: {Blocker}",
                territoryType,
                route,
                blocker);
            return DadIpcJson.Serialize(rejected);
        }

        var request = BuildRunRequest(duty, route, loopCount);
        log.Information(
            "[dad][AutoDutyCompat] Starting Dad run from AutoDuty.Run territory={TerritoryType} cfc={ContentFinderConditionId} route={Route} loops={Loops} bareMode={BareMode}.",
            territoryType,
            duty.ContentFinderConditionId,
            route,
            loopCount,
            bareMode);

        var result = coordinatorService.StartTasks(request);
        status.LastRunId = result.RequestId;
        if (result.IsTerminal)
        {
            lastRunTerminal = true;
            status.LastFailure = string.IsNullOrWhiteSpace(result.FailureReason)
                ? result.Summary
                : result.FailureReason;
        }
        else
        {
            status.LastFailure = string.Empty;
        }

        return DadIpcJson.Serialize(result);
    }

    private bool IsStopped()
    {
        if (string.IsNullOrWhiteSpace(status.LastRunId))
            return true;

        var run = coordinatorService.GetLocalResult();
        if (string.Equals(run.RequestId, status.LastRunId, StringComparison.OrdinalIgnoreCase) && run.IsTerminal)
            lastRunTerminal = true;

        var leftRequestedTerritory = status.LastTerritoryType == 0 ||
                                     !Plugin.ClientState.IsLoggedIn ||
                                     Plugin.ClientState.TerritoryType != status.LastTerritoryType;
        return lastRunTerminal && leftRequestedTerritory;
    }

    private object Stop()
    {
        if (string.IsNullOrWhiteSpace(status.LastRunId))
        {
            log.Information("[dad][AutoDutyCompat] Stop ignored; no shim-owned run has been started.");
            return true;
        }

        var run = coordinatorService.GetLocalResult();
        if (!Plugin.IsBusy(run) ||
            !string.Equals(run.RequestId, status.LastRunId, StringComparison.OrdinalIgnoreCase))
        {
            log.Information(
                "[dad][AutoDutyCompat] Stop ignored; active Dad run {ActiveRunId} is not shim-owned run {ShimRunId}.",
                string.IsNullOrWhiteSpace(run.RequestId) ? "(none)" : run.RequestId,
                status.LastRunId);
            return true;
        }

        var result = coordinatorService.CancelActiveRun();
        if (string.Equals(result.RequestId, status.LastRunId, StringComparison.OrdinalIgnoreCase) && result.IsTerminal)
            lastRunTerminal = true;

        status.LastFailure = result.Status == DadRunStatus.Cancelled ? string.Empty : result.FailureReason;
        status.UpdatedAtUtc = DateTime.UtcNow;
        return DadIpcJson.Serialize(result);
    }

    private void OnRunStatusChanged(DadRunResult result)
    {
        if (!string.Equals(result.RequestId, status.LastRunId, StringComparison.OrdinalIgnoreCase))
            return;

        if (result.IsTerminal)
        {
            lastRunTerminal = true;
            if (result.Status is DadRunStatus.Failed or DadRunStatus.PartialFailure or DadRunStatus.TimedOut or DadRunStatus.Rejected)
            {
                status.LastFailure = string.IsNullOrWhiteSpace(result.FailureReason)
                    ? result.Summary
                    : result.FailureReason;
            }
        }

        status.UpdatedAtUtc = DateTime.UtcNow;
    }

    private DadAutoDutyCompatibilityRoute ResolveRoute()
        => unsynced || dutyMode.Equals("Regular", StringComparison.OrdinalIgnoreCase)
            ? DadAutoDutyCompatibilityRoute.LocalDuty
            : DadAutoDutyCompatibilityRoute.DutySupport;

    private DadAutoDutyCompatibilityDiagnostic DiagnoseTerritory(
        uint territoryType,
        string query,
        DadPlannerDutyOption? requestedDuty,
        uint requestedContentFinderConditionId)
    {
        DadAutoDutyContentPathProbe probe;
        try
        {
            probe = EvaluateContentHasPath(territoryType);
        }
        catch (Exception ex)
        {
            probe = new DadAutoDutyContentPathProbe
            {
                TerritoryType = territoryType,
                Result = false,
                Blocker = ex.Message,
            };
        }

        var route = ResolveRoute();
        DadPlannerDutyOption? routeDuty;
        string routeBlocker;
        bool routeAvailable;
        try
        {
            routeAvailable = TryResolveDuty(
                territoryType,
                route,
                logAmbiguousSelection: false,
                out routeDuty,
                out routeBlocker);
        }
        catch (Exception ex)
        {
            routeDuty = null;
            routeBlocker = ex.Message;
            routeAvailable = false;
        }
        var requestedRouteMatch = requestedDuty == null
            ? (bool?)null
            : DoesDutyMatchRoute(requestedDuty, route);
        var requestedDutyBlocker = requestedDuty == null || requestedRouteMatch == true
            ? string.Empty
            : BuildRequestedDutyBlocker(requestedDuty, route);

        var blocker = ResolveDiagnosticBlocker(probe, routeAvailable, routeBlocker, requestedDutyBlocker);
        return new DadAutoDutyCompatibilityDiagnostic
        {
            Query = query,
            ConfigEnabled = status.ConfigEnabled,
            Registered = status.Registered,
            RealAutoDutyLoaded = status.RealAutoDutyLoaded,
            RegistrationState = status.RegistrationState,
            Mode = BuildModeStatusText(),
            Route = route.ToString(),
            TerritoryType = territoryType,
            RequestedContentFinderConditionId = requestedContentFinderConditionId,
            RequestedDutyName = requestedDuty?.DutyDisplayName ?? string.Empty,
            RequestedDutyRouteMatch = requestedRouteMatch,
            RequestedDutyBlocker = requestedDutyBlocker,
            ContentHasPathResult = probe.Result,
            CandidateCount = probe.CandidateCount,
            CompatibleCandidateCount = probe.CompatibleCandidateCount,
            ContentHasPathSelectedContentFinderConditionId = probe.SelectedContentFinderConditionId,
            ContentHasPathSelectedDutyName = probe.SelectedDutyName,
            ContentHasPathBlocker = probe.Blocker,
            RouteAvailable = routeAvailable,
            RouteContentFinderConditionId = routeDuty?.ContentFinderConditionId ?? 0,
            RouteDutyName = routeDuty?.DutyDisplayName ?? string.Empty,
            RouteBlocker = routeBlocker,
            Blocker = blocker,
        };
    }

    private DadAutoDutyCompatibilityDiagnostic BuildUnavailableDiagnostic(
        string query,
        uint territoryType,
        uint requestedContentFinderConditionId,
        DadPlannerDutyOption? requestedDuty,
        string blocker)
    {
        var route = ResolveRoute();
        return new DadAutoDutyCompatibilityDiagnostic
        {
            Query = query,
            ConfigEnabled = status.ConfigEnabled,
            Registered = status.Registered,
            RealAutoDutyLoaded = status.RealAutoDutyLoaded,
            RegistrationState = status.RegistrationState,
            Mode = BuildModeStatusText(),
            Route = route.ToString(),
            TerritoryType = territoryType,
            RequestedContentFinderConditionId = requestedContentFinderConditionId,
            RequestedDutyName = requestedDuty?.DutyDisplayName ?? string.Empty,
            RequestedDutyRouteMatch = requestedDuty == null ? (bool?)null : DoesDutyMatchRoute(requestedDuty, route),
            RequestedDutyBlocker = requestedDuty == null ? string.Empty : BuildRequestedDutyBlocker(requestedDuty, route),
            ContentHasPathResult = false,
            ContentHasPathBlocker = blocker,
            RouteAvailable = false,
            RouteBlocker = blocker,
            Blocker = blocker,
        };
    }

    private bool TryResolveDuty(
        uint territoryType,
        DadAutoDutyCompatibilityRoute route,
        bool logAmbiguousSelection,
        out DadPlannerDutyOption? duty,
        out string blocker)
    {
        var candidates = presetProviderService.GetPlannerDutyOptionsForTerritory(territoryType)
            .Where(IsAutoDutyCompatibleCandidate)
            .OrderBy(static option => option.ContentFinderConditionId)
            .ToList();

        duty = route switch
        {
            DadAutoDutyCompatibilityRoute.DutySupport => candidates.FirstOrDefault(static option => option.SupportsDutySupport),
            _ => candidates.FirstOrDefault(option => !unsynced || option.AllowUndersized),
        };

        if (duty != null)
        {
            if (logAmbiguousSelection && candidates.Count > 1)
            {
                log.Information(
                    "[dad][AutoDutyCompat] Territory {TerritoryType} matched {CandidateCount} CFC row(s); selected {DutyName} #{ContentFinderConditionId}.",
                    territoryType,
                    candidates.Count,
                    duty.DutyDisplayName,
                    duty.ContentFinderConditionId);
            }

            blocker = string.Empty;
            return true;
        }

        blocker = route == DadAutoDutyCompatibilityRoute.DutySupport
            ? $"Territory {territoryType} has no Dad Duty Support-compatible ContentFinderCondition row."
            : unsynced
                ? $"Territory {territoryType} has no Dad local duty row that allows undersized/unsynced queueing."
                : $"Territory {territoryType} has no Dad local duty ContentFinderCondition row.";
        return false;
    }

    private DadAutoDutyContentPathProbe EvaluateContentHasPath(uint territoryType)
    {
        var candidates = presetProviderService.GetPlannerDutyOptionsForTerritory(territoryType)
            .OrderBy(static option => option.ContentFinderConditionId)
            .ToList();
        var compatibleCandidates = candidates
            .Where(IsAutoDutyCompatibleCandidate)
            .OrderBy(static option => option.ContentFinderConditionId)
            .ToList();
        var selected = compatibleCandidates.FirstOrDefault();
        return new DadAutoDutyContentPathProbe
        {
            TerritoryType = territoryType,
            Result = selected != null,
            CandidateCount = candidates.Count,
            CompatibleCandidateCount = compatibleCandidates.Count,
            SelectedContentFinderConditionId = selected?.ContentFinderConditionId ?? 0,
            SelectedDutyName = selected?.DutyDisplayName ?? string.Empty,
            Blocker = selected == null
                ? $"No Dad-compatible duty route found for territory {territoryType}."
                : string.Empty,
        };
    }

    private void RecordContentHasPathProbe(DadAutoDutyContentPathProbe probe)
    {
        status.LastTerritoryType = probe.TerritoryType;
        status.LastContentHasPathTerritoryType = probe.TerritoryType;
        status.LastContentHasPathResult = probe.Result;
        status.LastContentHasPathCandidateCount = probe.CandidateCount;
        status.LastContentHasPathCompatibleCandidateCount = probe.CompatibleCandidateCount;
        status.LastContentHasPathSelectedContentFinderConditionId = probe.SelectedContentFinderConditionId;
        status.LastContentHasPathSelectedDutyName = probe.SelectedDutyName;
        status.LastContentHasPathBlocker = probe.Blocker;
        status.LastContentHasPathUtc = DateTime.UtcNow;
        status.LastFailure = probe.Result ? string.Empty : probe.Blocker;
        status.UpdatedAtUtc = status.LastContentHasPathUtc.Value;
    }

    private void RecordContentHasPathFailure(uint territoryType, string blocker)
    {
        status.LastTerritoryType = territoryType;
        status.LastContentHasPathTerritoryType = territoryType;
        status.LastContentHasPathResult = false;
        status.LastContentHasPathCandidateCount = 0;
        status.LastContentHasPathCompatibleCandidateCount = 0;
        status.LastContentHasPathSelectedContentFinderConditionId = 0;
        status.LastContentHasPathSelectedDutyName = string.Empty;
        status.LastContentHasPathBlocker = blocker;
        status.LastContentHasPathUtc = DateTime.UtcNow;
        status.LastFailure = blocker;
        status.UpdatedAtUtc = status.LastContentHasPathUtc.Value;
    }

    private string ResolveDiagnosticBlocker(
        DadAutoDutyContentPathProbe probe,
        bool routeAvailable,
        string routeBlocker,
        string requestedDutyBlocker)
    {
        if (!configuration.EnableAutoDutyCompatibilityIpc)
            return "AutoDuty compatibility IPC disabled by Dad config.";

        if (status.RealAutoDutyLoaded)
            return "Real AutoDuty is loaded; Dad shim disabled to avoid AutoDuty.* collision.";

        if (!status.Registered)
            return string.IsNullOrWhiteSpace(status.RegistrationState)
                ? "AutoDuty compatibility IPC is not registered."
                : status.RegistrationState;

        if (!probe.Result)
            return probe.Blocker;

        if (!routeAvailable)
            return routeBlocker;

        return requestedDutyBlocker;
    }

    private bool DoesDutyMatchRoute(DadPlannerDutyOption option, DadAutoDutyCompatibilityRoute route)
    {
        if (!IsAutoDutyCompatibleCandidate(option))
            return false;

        return route switch
        {
            DadAutoDutyCompatibilityRoute.DutySupport => option.SupportsDutySupport,
            DadAutoDutyCompatibilityRoute.LocalDuty => !unsynced || option.AllowUndersized,
            _ => false,
        };
    }

    private string BuildRequestedDutyBlocker(DadPlannerDutyOption option, DadAutoDutyCompatibilityRoute route)
    {
        if (!IsAutoDutyCompatibleCandidate(option))
            return $"ContentFinderCondition #{option.ContentFinderConditionId} is not Dad-compatible.";

        return route switch
        {
            DadAutoDutyCompatibilityRoute.DutySupport when !option.SupportsDutySupport =>
                $"{option.DutyDisplayName} #{option.ContentFinderConditionId} is not marked as Duty Support content.",
            DadAutoDutyCompatibilityRoute.LocalDuty when unsynced && !option.AllowUndersized =>
                $"{option.DutyDisplayName} #{option.ContentFinderConditionId} does not allow undersized/unsynced queueing.",
            _ => string.Empty,
        };
    }

    private DadRunRequest BuildRunRequest(
        DadPlannerDutyOption duty,
        DadAutoDutyCompatibilityRoute route,
        int loopCount)
    {
        var request = new DadRunRequest
        {
            RequestId = $"autoduty-{Guid.NewGuid():N}",
            RequestedAtUtc = DateTime.UtcNow,
            RequestedBy = "AutoDuty compatibility IPC",
            StopPolicy = new DadRunStopPolicy
            {
                Mode = DadPlannerStopMode.AfterRuns,
                AfterRuns = loopCount,
                SafetyCap = loopCount,
            },
            Orchestration = new DadOrchestrationIntent
            {
                AuthorityMode = DadAuthorityMode.LocalOnly,
                LocalOnlyOverride = true,
                TransportMode = DadTransportMode.LocalOnly,
                QueueAuthority = DadQueueAuthority.LocalOnly,
                ModuleTarget = route == DadAutoDutyCompatibilityRoute.DutySupport ? DadModuleId.DutySupport : DadModuleId.Duty,
                RequirePostArReady = false,
                RosterIntent = new DadRosterIntent
                {
                    ExpectedPartySize = 1,
                    RequireRemoteParticipants = false,
                },
                ExecutionConstraintSummary = route == DadAutoDutyCompatibilityRoute.DutySupport
                    ? "AutoDutyCompatibilityDutySupport"
                    : unsynced
                        ? "AutoDutyCompatibilityLocalDutyUnsynced"
                        : "AutoDutyCompatibilityLocalDuty",
            },
        };

        if (route == DadAutoDutyCompatibilityRoute.DutySupport)
        {
            request.DutySupport = new DadDutySupportTask
            {
                ContentFinderConditionId = duty.ContentFinderConditionId,
                DutyName = duty.DutyDisplayName,
                Attempts = loopCount,
            };
        }
        else
        {
            request.Dungeon = new DadDungeonTask
            {
                Count = loopCount,
                Frequency = DadRunRequestOptions.FrequencyPerArRun,
                ContentFinderConditionId = duty.ContentFinderConditionId,
                SelectedDungeon = duty.DutyDisplayName,
                ExecutionPreference = DadRunRequestOptions.TrustThenDutySupport,
                QueueViaLanParty = false,
                Unsynced = unsynced,
            };
        }

        return request;
    }

    private string BuildModeStatusText()
    {
        var route = ResolveRoute();
        return route == DadAutoDutyCompatibilityRoute.LocalDuty
            ? unsynced
                ? "Regular / Unsynced"
                : "Regular"
            : "Support";
    }

    private void RefreshStatus()
    {
        status.ConfigEnabled = configuration.EnableAutoDutyCompatibilityIpc;
        status.RealAutoDutyLoaded = IsRealAutoDutyLoaded();
        status.RegistrationState = !configuration.EnableAutoDutyCompatibilityIpc
            ? "Disabled by Dad config."
            : status.RealAutoDutyLoaded
                ? "Disabled because real AutoDuty is loaded."
                : status.Registered
                    ? "Registered."
                    : "Not registered.";
        status.LastMode = BuildModeStatusText();
        status.UpdatedAtUtc = DateTime.UtcNow;
    }

    private bool IsRealAutoDutyLoaded()
    {
        try
        {
            return pluginInterface.InstalledPlugins.Any(plugin =>
                plugin.IsLoaded &&
                (string.Equals(plugin.InternalName, AutoDutyInternalName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(plugin.Name, AutoDutyDisplayName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(plugin.Name, AutoDutyDisplayNameSpaced, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad][AutoDutyCompat] Failed to inspect AutoDuty plugin availability.");
            return true;
        }
    }

    private void Register<TReturn>(string name, Func<TReturn> func)
    {
        var provider = pluginInterface.GetIpcProvider<TReturn>(name);
        provider.RegisterFunc(func);
        disposeActions.Add(provider.UnregisterFunc);
    }

    private void Register<TArg1, TReturn>(string name, Func<TArg1, TReturn> func)
    {
        var provider = pluginInterface.GetIpcProvider<TArg1, TReturn>(name);
        provider.RegisterFunc(func);
        disposeActions.Add(provider.UnregisterFunc);
    }

    private void Register<TArg1, TArg2, TReturn>(string name, Func<TArg1, TArg2, TReturn> func)
    {
        var provider = pluginInterface.GetIpcProvider<TArg1, TArg2, TReturn>(name);
        provider.RegisterFunc(func);
        disposeActions.Add(provider.UnregisterFunc);
    }

    private void Register<TArg1, TArg2, TArg3, TReturn>(string name, Func<TArg1, TArg2, TArg3, TReturn> func)
    {
        var provider = pluginInterface.GetIpcProvider<TArg1, TArg2, TArg3, TReturn>(name);
        provider.RegisterFunc(func);
        disposeActions.Add(provider.UnregisterFunc);
    }

    private void Unregister()
    {
        foreach (var disposeAction in disposeActions)
        {
            try
            {
                disposeAction();
            }
            catch (Exception ex)
            {
                log.Debug(ex, "[dad][AutoDutyCompat] IPC unregister action failed.");
            }
        }

        disposeActions.Clear();
        status.Registered = false;
    }

    private static bool IsAutoDutyCompatibleCandidate(DadPlannerDutyOption option)
        => option.ContentFinderConditionId != 0 &&
           (option.SupportsDutySupport || option.SupportsTrust || !string.IsNullOrWhiteSpace(option.DutyDisplayName));
}

internal enum DadAutoDutyCompatibilityRoute
{
    DutySupport,
    LocalDuty,
}

internal static class DadAutoDutyCompatibilityIpcContract
{
    public const string ContentHasPath = "AutoDuty.ContentHasPath";
    public const string SetConfig = "AutoDuty.SetConfig";
    public const string Run = "AutoDuty.Run";
    public const string IsStopped = "AutoDuty.IsStopped";
    public const string Stop = "AutoDuty.Stop";
}
