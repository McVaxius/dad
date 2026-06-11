using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadDutyIpcStatus
{
    public bool Registered { get; set; }
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

    public DadDutyIpcStatus Clone()
        => new()
        {
            Registered = Registered,
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

public sealed class DadDutyIpcDiagnostic
{
    public string Query { get; set; } = string.Empty;
    public bool Registered { get; set; }
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

public sealed class DadDutyIpcService : IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly DadCoordinatorService coordinatorService;
    private readonly DadPresetProviderService presetProviderService;
    private readonly IPluginLog log;
    private readonly List<Action> disposeActions = [];
    private readonly DadDutyIpcStatus status = new();

    private string dutyMode = "Support";
    private bool unsynced;
    private string ownedRunId = string.Empty;
    private bool lastRunTerminal = true;
    private DateTime nextRegistrationAttemptUtc = DateTime.MinValue;

    private sealed class DadDutyContentPathProbe
    {
        public uint TerritoryType { get; init; }
        public bool Result { get; init; }
        public int CandidateCount { get; init; }
        public int CompatibleCandidateCount { get; init; }
        public uint SelectedContentFinderConditionId { get; init; }
        public string SelectedDutyName { get; init; } = string.Empty;
        public string Blocker { get; init; } = string.Empty;
    }

    public DadDutyIpcService(
        IDalamudPluginInterface pluginInterface,
        DadCoordinatorService coordinatorService,
        DadPresetProviderService presetProviderService,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.coordinatorService = coordinatorService;
        this.presetProviderService = presetProviderService;
        this.log = log;

        coordinatorService.StatusChanged += OnRunStatusChanged;
        EnsureRegistered();
    }

    public void Dispose()
    {
        coordinatorService.StatusChanged -= OnRunStatusChanged;
        Unregister();
    }

    public DadDutyIpcStatus GetStatus()
    {
        RefreshStatus();
        return status.Clone();
    }

    public void EnsureRegistered()
    {
        if (status.Registered || DateTime.UtcNow < nextRegistrationAttemptUtc)
            return;

        nextRegistrationAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        Register();
    }

    public DadDutyIpcDiagnostic DiagnoseCurrentTerritory()
    {
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

    public DadDutyIpcDiagnostic DiagnoseTerritory(uint territoryType)
    {
        return DiagnoseTerritory(territoryType, $"territory {territoryType}", null, 0);
    }

    public DadDutyIpcDiagnostic DiagnoseContentFinderCondition(uint contentFinderConditionId)
    {
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

    private void Register()
    {
        try
        {
            RegisterFunc<uint, bool>(DadDutyIpcContract.ContentHasPath, ContentHasPath);
            RegisterAction<string, string, object>(DadDutyIpcContract.SetConfig, SetConfig);
            RegisterAction<uint, int, bool, object>(DadDutyIpcContract.Run, Run);
            RegisterFunc<bool>(DadDutyIpcContract.IsStopped, IsStopped);
            RegisterAction<object>(DadDutyIpcContract.Stop, Stop);

            status.Registered = true;
            status.RegistrationState = "Dad duty IPC registered.";
            status.LastFailure = string.Empty;
            log.Information("[dad][DutyIpc] Registered Dad duty IPC.");
        }
        catch (Exception ex)
        {
            Unregister();
            status.RegistrationState = "Registration failed.";
            status.LastFailure = ex.Message;
            log.Warning(ex, "[dad][DutyIpc] Failed to register Dad duty IPC.");
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
                "[dad][DutyIpc] ContentHasPath territory={TerritoryType} result={Result} candidates={CandidateCount} compatible={CompatibleCandidateCount} selected={SelectedContentFinderConditionId}:{SelectedDutyName} blocker={Blocker}.",
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
            log.Warning(ex, "[dad][DutyIpc] ContentHasPath failed for territory {TerritoryType}.", territoryType);
            return false;
        }
    }

    private void SetConfig(string key, string value)
    {
        var normalizedKey = key.Trim();
        var normalizedValue = value.Trim();

        if (normalizedKey.Equals("Unsynced", StringComparison.OrdinalIgnoreCase))
        {
            if (bool.TryParse(normalizedValue, out var parsed))
                unsynced = parsed;
            else
                status.LastFailure = $"Unable to parse Dad duty IPC Unsynced config value '{value}'.";
        }
        else if (normalizedKey.Equals("dutyModeEnum", StringComparison.OrdinalIgnoreCase))
        {
            dutyMode = string.IsNullOrWhiteSpace(normalizedValue) ? "Support" : normalizedValue;
        }
        else
        {
            log.Debug(
                "[dad][DutyIpc] Ignored unknown SetConfig key {Key}={Value}.",
                normalizedKey,
                normalizedValue);
        }

        status.LastMode = BuildModeStatusText();
        status.UpdatedAtUtc = DateTime.UtcNow;
    }

    private void Run(uint territoryType, int loops, bool bareMode)
    {
        status.LastTerritoryType = territoryType;
        status.LastBareMode = bareMode;
        status.LastMode = BuildModeStatusText();
        status.UpdatedAtUtc = DateTime.UtcNow;

        if (!status.Registered)
        {
            status.LastFailure = status.RegistrationState;
            throw new InvalidOperationException(status.RegistrationState);
        }

        var route = ResolveRoute();
        var loopCount = Math.Max(1, loops);
        if (!TryResolveDuty(territoryType, route, logAmbiguousSelection: true, out var duty, out var blocker) || duty == null)
        {
            status.LastFailure = blocker;
            log.Warning(
                "[dad][DutyIpc] Run rejected territory={TerritoryType} route={Route}: {Blocker}",
                territoryType,
                route,
                blocker);
            throw new InvalidOperationException(blocker);
        }

        var request = BuildRunRequest(duty, route, loopCount);
        log.Information(
            "[dad][DutyIpc] Starting Dad run from duty IPC territory={TerritoryType} cfc={ContentFinderConditionId} route={Route} loops={Loops} bareMode={BareMode}.",
            territoryType,
            duty.ContentFinderConditionId,
            route,
            loopCount,
            bareMode);

        DadRunResult result;
        try
        {
            result = coordinatorService.StartTasks(request);
        }
        catch (Exception ex)
        {
            status.LastRunId = request.RequestId;
            status.LastFailure = ex.Message;
            status.UpdatedAtUtc = DateTime.UtcNow;
            throw;
        }

        status.LastRunId = string.IsNullOrWhiteSpace(result.RequestId) ? request.RequestId : result.RequestId;
        if (result.IsTerminal)
        {
            status.LastFailure = string.IsNullOrWhiteSpace(result.FailureReason)
                ? result.Summary
                : result.FailureReason;
            throw new InvalidOperationException(status.LastFailure);
        }
        else
        {
            ownedRunId = status.LastRunId;
            lastRunTerminal = false;
            status.LastFailure = string.Empty;
        }
    }

    private bool IsStopped()
    {
        if (string.IsNullOrWhiteSpace(ownedRunId))
            return true;

        var run = coordinatorService.GetLocalResult();
        if (string.Equals(run.RequestId, ownedRunId, StringComparison.OrdinalIgnoreCase) && run.IsTerminal)
            lastRunTerminal = true;

        return lastRunTerminal;
    }

    private void Stop()
    {
        if (string.IsNullOrWhiteSpace(ownedRunId))
        {
            log.Information("[dad][DutyIpc] Stop ignored; no bridge-owned run has been started.");
            return;
        }

        var run = coordinatorService.GetLocalResult();
        if (!Plugin.IsBusy(run) ||
            !string.Equals(run.RequestId, ownedRunId, StringComparison.OrdinalIgnoreCase))
        {
            log.Information(
                "[dad][DutyIpc] Stop ignored; active Dad run {ActiveRunId} is not bridge-owned run {BridgeRunId}.",
                string.IsNullOrWhiteSpace(run.RequestId) ? "(none)" : run.RequestId,
                ownedRunId);
            return;
        }

        var result = coordinatorService.CancelActiveRun();
        if (string.Equals(result.RequestId, ownedRunId, StringComparison.OrdinalIgnoreCase) && result.IsTerminal)
            lastRunTerminal = true;

        status.LastFailure = result.Status == DadRunStatus.Cancelled ? string.Empty : result.FailureReason;
        status.UpdatedAtUtc = DateTime.UtcNow;
    }

    private void OnRunStatusChanged(DadRunResult result)
    {
        if (!string.Equals(result.RequestId, ownedRunId, StringComparison.OrdinalIgnoreCase))
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

    private DadDutyIpcRoute ResolveRoute()
        => unsynced || dutyMode.Equals("Regular", StringComparison.OrdinalIgnoreCase)
            ? DadDutyIpcRoute.LocalDuty
            : DadDutyIpcRoute.DutySupport;

    private DadDutyIpcDiagnostic DiagnoseTerritory(
        uint territoryType,
        string query,
        DadPlannerDutyOption? requestedDuty,
        uint requestedContentFinderConditionId)
    {
        DadDutyContentPathProbe probe;
        try
        {
            probe = EvaluateContentHasPath(territoryType);
        }
        catch (Exception ex)
        {
            probe = new DadDutyContentPathProbe
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
        return new DadDutyIpcDiagnostic
        {
            Query = query,
            Registered = status.Registered,
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

    private DadDutyIpcDiagnostic BuildUnavailableDiagnostic(
        string query,
        uint territoryType,
        uint requestedContentFinderConditionId,
        DadPlannerDutyOption? requestedDuty,
        string blocker)
    {
        var route = ResolveRoute();
        return new DadDutyIpcDiagnostic
        {
            Query = query,
            Registered = status.Registered,
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
        DadDutyIpcRoute route,
        bool logAmbiguousSelection,
        out DadPlannerDutyOption? duty,
        out string blocker)
    {
        var candidates = presetProviderService.GetPlannerDutyOptionsForTerritory(territoryType)
            .Where(IsDutyIpcCompatibleCandidate)
            .OrderBy(static option => option.ContentFinderConditionId)
            .ToList();

        duty = route switch
        {
            DadDutyIpcRoute.DutySupport => candidates.FirstOrDefault(static option => option.SupportsDutySupport),
            _ => candidates.FirstOrDefault(option => !unsynced || option.AllowUndersized),
        };

        if (duty != null)
        {
            if (logAmbiguousSelection && candidates.Count > 1)
            {
                log.Information(
                    "[dad][DutyIpc] Territory {TerritoryType} matched {CandidateCount} CFC row(s); selected {DutyName} #{ContentFinderConditionId}.",
                    territoryType,
                    candidates.Count,
                    duty.DutyDisplayName,
                    duty.ContentFinderConditionId);
            }

            blocker = string.Empty;
            return true;
        }

        blocker = route == DadDutyIpcRoute.DutySupport
            ? $"Territory {territoryType} has no Dad Duty Support-compatible ContentFinderCondition row."
            : unsynced
                ? $"Territory {territoryType} has no Dad local duty row that allows undersized/unsynced queueing."
                : $"Territory {territoryType} has no Dad local duty ContentFinderCondition row.";
        return false;
    }

    private DadDutyContentPathProbe EvaluateContentHasPath(uint territoryType)
    {
        var candidates = presetProviderService.GetPlannerDutyOptionsForTerritory(territoryType)
            .OrderBy(static option => option.ContentFinderConditionId)
            .ToList();
        var compatibleCandidates = candidates
            .Where(IsDutyIpcCompatibleCandidate)
            .OrderBy(static option => option.ContentFinderConditionId)
            .ToList();
        var selected = compatibleCandidates.FirstOrDefault();
        return new DadDutyContentPathProbe
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

    private void RecordContentHasPathProbe(DadDutyContentPathProbe probe)
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
        status.UpdatedAtUtc = status.LastContentHasPathUtc.Value;
    }

    private string ResolveDiagnosticBlocker(
        DadDutyContentPathProbe probe,
        bool routeAvailable,
        string routeBlocker,
        string requestedDutyBlocker)
    {
        if (!status.Registered)
            return string.IsNullOrWhiteSpace(status.RegistrationState)
                ? "Dad duty IPC is not registered."
                : status.RegistrationState;

        if (!probe.Result)
            return probe.Blocker;

        if (!routeAvailable)
            return routeBlocker;

        return requestedDutyBlocker;
    }

    private bool DoesDutyMatchRoute(DadPlannerDutyOption option, DadDutyIpcRoute route)
    {
        if (!IsDutyIpcCompatibleCandidate(option))
            return false;

        return route switch
        {
            DadDutyIpcRoute.DutySupport => option.SupportsDutySupport,
            DadDutyIpcRoute.LocalDuty => !unsynced || option.AllowUndersized,
            _ => false,
        };
    }

    private string BuildRequestedDutyBlocker(DadPlannerDutyOption option, DadDutyIpcRoute route)
    {
        if (!IsDutyIpcCompatibleCandidate(option))
            return $"ContentFinderCondition #{option.ContentFinderConditionId} is not Dad-compatible.";

        return route switch
        {
            DadDutyIpcRoute.DutySupport when !option.SupportsDutySupport =>
                $"{option.DutyDisplayName} #{option.ContentFinderConditionId} is not marked as Duty Support content.",
            DadDutyIpcRoute.LocalDuty when unsynced && !option.AllowUndersized =>
                $"{option.DutyDisplayName} #{option.ContentFinderConditionId} does not allow undersized/unsynced queueing.",
            _ => string.Empty,
        };
    }

    private DadRunRequest BuildRunRequest(
        DadPlannerDutyOption duty,
        DadDutyIpcRoute route,
        int loopCount)
    {
        var request = new DadRunRequest
        {
            RequestId = $"duty-ipc-{Guid.NewGuid():N}",
            RequestedAtUtc = DateTime.UtcNow,
            RequestedBy = "Dad duty IPC",
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
                ModuleTarget = route == DadDutyIpcRoute.DutySupport ? DadModuleId.DutySupport : DadModuleId.Duty,
                RequirePostArReady = false,
                RosterIntent = new DadRosterIntent
                {
                    ExpectedPartySize = 1,
                    RequireRemoteParticipants = false,
                },
                ExecutionConstraintSummary = route == DadDutyIpcRoute.DutySupport
                    ? "DadDutyIpcDutySupport"
                    : unsynced
                        ? "DadDutyIpcLocalDutyUnsynced"
                        : "DadDutyIpcLocalDuty",
            },
        };

        if (route == DadDutyIpcRoute.DutySupport)
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
        return route == DadDutyIpcRoute.LocalDuty
            ? unsynced
                ? "Regular / Unsynced"
                : "Regular"
            : "Support";
    }

    private void RefreshStatus()
    {
        if (status.Registered)
            status.RegistrationState = "Dad duty IPC registered.";
        else if (string.IsNullOrWhiteSpace(status.RegistrationState))
            status.RegistrationState = "Not registered.";

        status.LastMode = BuildModeStatusText();
        status.UpdatedAtUtc = DateTime.UtcNow;
    }

    private void RegisterFunc<TReturn>(string name, Func<TReturn> func)
    {
        var provider = pluginInterface.GetIpcProvider<TReturn>(name);
        provider.RegisterFunc(func);
        disposeActions.Add(provider.UnregisterFunc);
    }

    private void RegisterFunc<TArg1, TReturn>(string name, Func<TArg1, TReturn> func)
    {
        var provider = pluginInterface.GetIpcProvider<TArg1, TReturn>(name);
        provider.RegisterFunc(func);
        disposeActions.Add(provider.UnregisterFunc);
    }

    private void RegisterAction<TReturn>(string name, Action action)
    {
        var provider = pluginInterface.GetIpcProvider<TReturn>(name);
        provider.RegisterAction(action);
        disposeActions.Add(provider.UnregisterAction);
    }

    private void RegisterAction<TArg1, TArg2, TReturn>(string name, Action<TArg1, TArg2> action)
    {
        var provider = pluginInterface.GetIpcProvider<TArg1, TArg2, TReturn>(name);
        provider.RegisterAction(action);
        disposeActions.Add(provider.UnregisterAction);
    }

    private void RegisterAction<TArg1, TArg2, TArg3, TReturn>(string name, Action<TArg1, TArg2, TArg3> action)
    {
        var provider = pluginInterface.GetIpcProvider<TArg1, TArg2, TArg3, TReturn>(name);
        provider.RegisterAction(action);
        disposeActions.Add(provider.UnregisterAction);
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
                log.Debug(ex, "[dad][DutyIpc] IPC unregister action failed.");
            }
        }

        disposeActions.Clear();
        status.Registered = false;
    }

    private static bool IsDutyIpcCompatibleCandidate(DadPlannerDutyOption option)
        => option.ContentFinderConditionId != 0 &&
           (option.SupportsDutySupport || option.SupportsTrust || !string.IsNullOrWhiteSpace(option.DutyDisplayName));
}

internal enum DadDutyIpcRoute
{
    DutySupport,
    LocalDuty,
}

internal static class DadDutyIpcContract
{
    public const string ContentHasPath = "dad.Duty.ContentHasPath";
    public const string SetConfig = "dad.Duty.SetConfig";
    public const string Run = "dad.Duty.Run";
    public const string IsStopped = "dad.Duty.IsStopped";
    public const string Stop = "dad.Duty.Stop";
}
