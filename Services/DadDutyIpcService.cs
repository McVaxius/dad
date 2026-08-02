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
    public DateTime? LastCleanupUtc { get; set; }
    public string LastCleanupResult { get; set; } = "Not run.";
    public List<string> LastCleanupFailedCommands { get; set; } = [];
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
            LastCleanupUtc = LastCleanupUtc,
            LastCleanupResult = LastCleanupResult,
            LastCleanupFailedCommands = [.. LastCleanupFailedCommands],
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
    private static readonly string[] SuccessfulSessionCleanupCommands =
    [
        "/fr off",
        "/rotation cancel",
        "/vbmai off",
        "/bmrai off",
        "/wrath auto off",
    ];

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly DadPresetProviderService presetProviderService;
    private readonly DadDutySupportExecutor dutySupportExecutor;
    private readonly DadLocalDutyExecutor localDutyExecutor;
    private readonly IPluginLog log;
    private readonly List<Action> disposeActions = [];
    private readonly DadDutyIpcStatus status = new();

    private string dutyMode = "Support";
    private bool unsynced;
    private DadDutyIpcSessionStage sessionStage = DadDutyIpcSessionStage.Stopped;
    private IDadModuleExecutor? activeExecutor;
    private DadPlannerDutyOption? activeDuty;
    private DadDutyIpcRoute activeRoute;
    private bool activeUnsynced;
    private string ownedSessionId = string.Empty;
    private int requestedLoops;
    private int completedLoops;
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
        DadPresetProviderService presetProviderService,
        DadLocalDutyQueueService localDutyQueueService,
        DadNpcDutyQueueService npcDutyQueueService,
        DadDutySupportAdsService dutySupportAdsService,
        DadCombatRotationService combatRotationService,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.presetProviderService = presetProviderService;
        localDutyExecutor = new DadLocalDutyExecutor(localDutyQueueService, combatRotationService);
        dutySupportExecutor = new DadDutySupportExecutor(npcDutyQueueService, dutySupportAdsService, combatRotationService);
        this.log = log;

        EnsureRegistered();
    }

    public void Dispose()
    {
        StopBridgeSession("Dad duty IPC disposed.", clearFailure: true);
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

    public void Update()
    {
        if (sessionStage != DadDutyIpcSessionStage.Running)
            return;

        if (activeExecutor == null)
        {
            StartNextLoop();
            return;
        }

        try
        {
            activeExecutor.Update();
            HandleActiveExecutorStatus();
        }
        catch (Exception ex)
        {
            FailSession(ex.Message);
            log.Warning(ex, "[dad][DutyIpc] Bridge executor update failed.");
        }
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
        StopBridgeSession("Replaced by a new Dad duty IPC run.", clearFailure: true);

        status.LastTerritoryType = territoryType;
        status.LastBareMode = bareMode;
        status.LastMode = BuildModeStatusText();
        status.LastRunId = $"duty-ipc-{Guid.NewGuid():N}";
        status.UpdatedAtUtc = DateTime.UtcNow;

        try
        {
            var route = ResolveRoute();
            var loopCount = Math.Max(1, loops);
            if (!TryResolveDuty(territoryType, route, logAmbiguousSelection: true, out var duty, out var blocker) || duty == null)
            {
                FailSession(blocker);
                log.Warning(
                    "[dad][DutyIpc] Run rejected territory={TerritoryType} route={Route}: {Blocker}",
                    territoryType,
                    route,
                    blocker);
                return;
            }

            ownedSessionId = status.LastRunId;
            activeDuty = duty;
            activeRoute = route;
            activeUnsynced = unsynced;
            requestedLoops = loopCount;
            completedLoops = 0;
            sessionStage = DadDutyIpcSessionStage.Running;
            status.LastFailure = string.Empty;

            log.Information(
                "[dad][DutyIpc] Starting bridge session territory={TerritoryType} cfc={ContentFinderConditionId} route={Route} loops={Loops} bareMode={BareMode}.",
                territoryType,
                duty.ContentFinderConditionId,
                route,
                loopCount,
                bareMode);

            StartNextLoop();
        }
        catch (Exception ex)
        {
            FailSession(ex.Message);
            log.Warning(ex, "[dad][DutyIpc] Run failed for territory {TerritoryType}.", territoryType);
        }
    }

    private bool IsStopped()
        => sessionStage == DadDutyIpcSessionStage.Stopped;

    private void Stop()
        => StopBridgeSession("Stopped by Dad duty IPC.", clearFailure: true);

    private void StartNextLoop()
    {
        if (sessionStage != DadDutyIpcSessionStage.Running)
            return;

        if (activeDuty == null)
        {
            FailSession("Dad duty IPC session has no resolved duty.");
            return;
        }

        try
        {
            var loopNumber = completedLoops + 1;
            var plan = BuildExecutorPlan(activeDuty, activeRoute, loopNumber);
            IDadModuleExecutor executor = activeRoute == DadDutyIpcRoute.DutySupport
                ? dutySupportExecutor
                : localDutyExecutor;
            activeExecutor = executor;
            activeExecutor.Start(plan, BuildLocalParticipants(plan.Request.RequestId));
            HandleActiveExecutorStatus();
        }
        catch (Exception ex)
        {
            FailSession(ex.Message);
            log.Warning(ex, "[dad][DutyIpc] Failed to start bridge executor.");
        }
    }

    private void HandleActiveExecutorStatus()
    {
        if (activeExecutor == null)
            return;

        var executorStatus = activeExecutor.GetStatus();
        status.UpdatedAtUtc = DateTime.UtcNow;

        switch (executorStatus.Status)
        {
            case DadRunStatus.Completed:
                activeExecutor = null;
                completedLoops++;
                if (completedLoops >= requestedLoops)
                    CompleteSession();
                break;
            case DadRunStatus.Cancelled:
                EndSessionWithoutCleanup();
                break;
            case DadRunStatus.Rejected:
            case DadRunStatus.PartialFailure:
            case DadRunStatus.TimedOut:
            case DadRunStatus.Failed:
                FailSession(ResolveExecutorFailure(executorStatus));
                break;
        }
    }

    private void CompleteSession()
    {
        RunSuccessfulSessionCleanup();
        EndSessionWithoutCleanup();
    }

    private void EndSessionWithoutCleanup()
    {
        activeExecutor = null;
        activeDuty = null;
        ownedSessionId = string.Empty;
        requestedLoops = 0;
        completedLoops = 0;
        sessionStage = DadDutyIpcSessionStage.Stopped;
        status.LastFailure = string.Empty;
        status.UpdatedAtUtc = DateTime.UtcNow;
    }

    private void RunSuccessfulSessionCleanup()
    {
        var failedCommands = new List<string>();
        foreach (var command in SuccessfulSessionCleanupCommands)
        {
            try
            {
                if (Plugin.CommandManager.ProcessCommand(command))
                    continue;

                failedCommands.Add(command);
                log.Warning("[dad][DutyIpc] Successful-session cleanup command was rejected: {Command}.", command);
            }
            catch (Exception ex)
            {
                failedCommands.Add(command);
                log.Warning(ex, "[dad][DutyIpc] Successful-session cleanup command threw: {Command}.", command);
            }
        }

        status.LastCleanupUtc = DateTime.UtcNow;
        status.LastCleanupFailedCommands = failedCommands;
        status.LastCleanupResult = failedCommands.Count == 0
            ? "Succeeded."
            : $"Completed with warnings; {failedCommands.Count} command(s) failed.";
        status.UpdatedAtUtc = status.LastCleanupUtc.Value;

        if (failedCommands.Count == 0)
        {
            log.Information(
                "[dad][DutyIpc] Successful final bridge session cleanup sent all commands: {Commands}.",
                string.Join(", ", SuccessfulSessionCleanupCommands));
        }
        else
        {
            log.Warning(
                "[dad][DutyIpc] Successful final bridge session cleanup completed with failed command(s): {Commands}.",
                string.Join(", ", failedCommands));
        }
    }

    private void FailSession(string reason)
    {
        var executor = activeExecutor;
        activeExecutor = null;
        activeDuty = null;
        ownedSessionId = string.Empty;
        requestedLoops = 0;
        completedLoops = 0;
        sessionStage = DadDutyIpcSessionStage.Stopped;
        status.LastFailure = string.IsNullOrWhiteSpace(reason)
            ? "Dad duty IPC executor failed."
            : reason;
        status.UpdatedAtUtc = DateTime.UtcNow;

        if (executor == null)
            return;

        try
        {
            executor.Cancel(status.LastFailure);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[dad][DutyIpc] Failed to cancel rejected bridge executor.");
        }
    }

    private void StopBridgeSession(string reason, bool clearFailure)
    {
        var executor = activeExecutor;
        activeExecutor = null;
        activeDuty = null;
        ownedSessionId = string.Empty;
        requestedLoops = 0;
        completedLoops = 0;
        sessionStage = DadDutyIpcSessionStage.Stopped;
        if (clearFailure)
            status.LastFailure = string.Empty;
        status.UpdatedAtUtc = DateTime.UtcNow;

        if (executor == null)
            return;

        try
        {
            executor.Cancel(reason);
        }
        catch (Exception ex)
        {
            status.LastFailure = ex.Message;
            status.UpdatedAtUtc = DateTime.UtcNow;
            log.Warning(ex, "[dad][DutyIpc] Failed to cancel bridge executor.");
        }
    }

    private static string ResolveExecutorFailure(DadModuleExecutionStatusDto executorStatus)
    {
        if (!string.IsNullOrWhiteSpace(executorStatus.FailureReason))
            return executorStatus.FailureReason;

        if (!string.IsNullOrWhiteSpace(executorStatus.BlockedReason))
            return executorStatus.BlockedReason;

        return string.IsNullOrWhiteSpace(executorStatus.Summary)
            ? "Dad duty IPC executor rejected the run."
            : executorStatus.Summary;
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
        bool runUnsynced)
    {
        var request = new DadRunRequest
        {
            RequestedAtUtc = DateTime.UtcNow,
            RequestedBy = "Dad duty IPC",
            StopPolicy = new DadRunStopPolicy
            {
                Mode = DadPlannerStopMode.AfterRuns,
                AfterRuns = 1,
                SafetyCap = 1,
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
                    : runUnsynced
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
                Attempts = 1,
            };
        }
        else
        {
            request.Dungeon = new DadDungeonTask
            {
                Count = 1,
                Frequency = DadRunRequestOptions.FrequencyPerArRun,
                ContentFinderConditionId = duty.ContentFinderConditionId,
                SelectedDungeon = duty.DutyDisplayName,
                ExecutionPreference = DadRunRequestOptions.TrustThenDutySupport,
                QueueViaLanParty = false,
                Unsynced = runUnsynced,
            };
        }

        return request;
    }

    private DadRunPlan BuildExecutorPlan(
        DadPlannerDutyOption duty,
        DadDutyIpcRoute route,
        int loopNumber)
    {
        var request = BuildRunRequest(duty, route, activeUnsynced);
        request.RequestId = $"{ownedSessionId}-loop-{loopNumber}";
        var moduleId = route == DadDutyIpcRoute.DutySupport
            ? DadModuleId.DutySupport
            : DadModuleId.Duty;
        var displayName = route == DadDutyIpcRoute.DutySupport
            ? "Duty Support"
            : "Local Duty";

        return new DadRunPlan
        {
            Request = request,
            CompositeModuleId = moduleId,
            Orchestration = request.Orchestration,
            Summary = $"{displayName} {duty.DutyDisplayName}",
            RequiredParticipantCount = 1,
            RequiresRemoteParticipants = false,
            Modules =
            [
                new DadPlannedModuleExecution
                {
                    ModuleId = moduleId,
                    DisplayName = displayName,
                    OwnerLabel = "Dad duty IPC",
                    ExpectedPartySize = 1,
                    RequiresPeers = false,
                    Summary = $"{displayName} {duty.DutyDisplayName}",
                },
            ],
        };
    }

    private static IReadOnlyList<DadParticipantSnapshot> BuildLocalParticipants(string runId)
        =>
        [
            new DadParticipantSnapshot
            {
                RunId = runId,
                AuthorityMode = DadAuthorityMode.LocalOnly,
                Role = DadOrchestrationRole.Leader,
                WorkerRole = DadWorkerRole.ClientDad,
                State = DadParticipantState.Ready,
                ClaimState = DadClaimState.Granted,
                LeaseState = DadParticipantLeaseState.Granted,
                IsLocalClient = true,
                IsAuthority = true,
                IsAvailable = true,
                IsEligibleForRun = true,
                PostArReady = true,
                AssignedSlotId = DadPlannerSlotRules.LeaderSlotId,
                StatusText = "Dad duty IPC local participant.",
            },
        ];

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
        provider.RegisterFunc(() => InvokeOnFrameworkThread(func));
        disposeActions.Add(provider.UnregisterFunc);
    }

    private void RegisterFunc<TArg1, TReturn>(string name, Func<TArg1, TReturn> func)
    {
        var provider = pluginInterface.GetIpcProvider<TArg1, TReturn>(name);
        provider.RegisterFunc(argument => InvokeOnFrameworkThread(() => func(argument)));
        disposeActions.Add(provider.UnregisterFunc);
    }

    private void RegisterAction<TReturn>(string name, Action action)
    {
        var provider = pluginInterface.GetIpcProvider<TReturn>(name);
        provider.RegisterAction(() => InvokeOnFrameworkThread(action));
        disposeActions.Add(provider.UnregisterAction);
    }

    private void RegisterAction<TArg1, TArg2, TReturn>(string name, Action<TArg1, TArg2> action)
    {
        var provider = pluginInterface.GetIpcProvider<TArg1, TArg2, TReturn>(name);
        provider.RegisterAction((argument1, argument2) =>
            InvokeOnFrameworkThread(() => action(argument1, argument2)));
        disposeActions.Add(provider.UnregisterAction);
    }

    private void RegisterAction<TArg1, TArg2, TArg3, TReturn>(string name, Action<TArg1, TArg2, TArg3> action)
    {
        var provider = pluginInterface.GetIpcProvider<TArg1, TArg2, TArg3, TReturn>(name);
        provider.RegisterAction((argument1, argument2, argument3) =>
            InvokeOnFrameworkThread(() => action(argument1, argument2, argument3)));
        disposeActions.Add(provider.UnregisterAction);
    }

    private static T InvokeOnFrameworkThread<T>(Func<T> func)
        => Plugin.Framework.IsInFrameworkUpdateThread
            ? func()
            : Plugin.Framework.RunOnFrameworkThread(func).GetAwaiter().GetResult();

    private static void InvokeOnFrameworkThread(Action action)
    {
        if (Plugin.Framework.IsInFrameworkUpdateThread)
        {
            action();
            return;
        }

        Plugin.Framework.RunOnFrameworkThread(action).GetAwaiter().GetResult();
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

internal enum DadDutyIpcSessionStage
{
    Stopped,
    Running,
}

internal static class DadDutyIpcContract
{
    public const string ContentHasPath = "dad.Duty.ContentHasPath";
    public const string SetConfig = "dad.Duty.SetConfig";
    public const string Run = "dad.Duty.Run";
    public const string IsStopped = "dad.Duty.IsStopped";
    public const string Stop = "dad.Duty.Stop";
}
