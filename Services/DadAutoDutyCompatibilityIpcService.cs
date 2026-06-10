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
            UpdatedAtUtc = UpdatedAtUtc,
        };
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
        status.LastTerritoryType = territoryType;
        status.UpdatedAtUtc = DateTime.UtcNow;

        try
        {
            var candidates = presetProviderService.GetPlannerDutyOptionsForTerritory(territoryType);
            var hasPath = candidates.Any(IsAutoDutyCompatibleCandidate);
            if (!hasPath)
                status.LastFailure = $"No Dad-compatible duty route found for territory {territoryType}.";

            log.Debug(
                "[dad][AutoDutyCompat] ContentHasPath territory={TerritoryType} result={Result} candidates={CandidateCount}.",
                territoryType,
                hasPath,
                candidates.Count);
            return hasPath;
        }
        catch (Exception ex)
        {
            status.LastFailure = ex.Message;
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
        if (!TryResolveDuty(territoryType, route, out var duty, out var blocker) || duty == null)
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

    private bool TryResolveDuty(
        uint territoryType,
        DadAutoDutyCompatibilityRoute route,
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
            if (candidates.Count > 1)
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
