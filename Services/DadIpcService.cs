using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadIpcService : IDisposable
{
    private readonly List<Action> disposeActions = [];
    private readonly ICallGateProvider<string, object> runStatusChangedProvider;
    private readonly DadCoordinatorService coordinatorService;
    private readonly DadCharacterIntelligenceService characterIntelligenceService;
    private readonly DadPresenceService presenceService;
    private readonly DadTransportService transportService;
    private readonly DadModuleRegistry moduleRegistry;
    private readonly DadPresetProviderService presetProviderService;
    private readonly Plugin plugin;
    private readonly IPluginLog log;

    public DadIpcService(
        IDalamudPluginInterface pluginInterface,
        Plugin plugin,
        DadCoordinatorService coordinatorService,
        DadCharacterIntelligenceService characterIntelligenceService,
        DadPresenceService presenceService,
        DadTransportService transportService,
        DadModuleRegistry moduleRegistry,
        DadPresetProviderService presetProviderService,
        IPluginLog log)
    {
        this.plugin = plugin;
        this.coordinatorService = coordinatorService;
        this.characterIntelligenceService = characterIntelligenceService;
        this.presenceService = presenceService;
        this.transportService = transportService;
        this.moduleRegistry = moduleRegistry;
        this.presetProviderService = presetProviderService;
        this.log = log;

        Register(pluginInterface, DadIpcContract.IsReady, () => coordinatorService.IsReady);
        Register(pluginInterface, DadIpcContract.GetStatus, () => DadIpcJson.Serialize(coordinatorService.GetLocalResult()));
        Register(pluginInterface, DadIpcContract.GetLeaderStatus, () => DadIpcJson.Serialize(coordinatorService.GetAuthorityAwareResult()));
        Register(pluginInterface, DadIpcContract.GetParticipantStatusSnapshot, () => DadIpcJson.Serialize(
            presenceService.BuildStatusSnapshot(
                transportService.CurrentTransport.KnownParticipants,
                transportService.CurrentTransport.TransportMode,
                coordinatorService.GetLocalResult().LocalOnlyEnabled,
                transportService.CurrentTransport.AuthorityWorkerSessionId,
                transportService.CurrentTransport.AuthorityEndpoint,
                transportService.CurrentTransport.LastRequestStatus)));
        Register(pluginInterface, DadIpcContract.GetLanPartyPresets, () => this.presetProviderService.GetLanPartyPresetsJson());
        Register(pluginInterface, DadIpcContract.GetRosterPreview, () => DadIpcJson.Serialize(
            presetProviderService.BuildPlannerPreview(characterIntelligenceService.CurrentPool)));
        Register(pluginInterface, DadIpcContract.GetPlannerGroups, this.plugin.GetPlannerGroupsJson);
        Register<string, string>(pluginInterface, DadIpcContract.GetPlannerGroupPreview, this.plugin.GetPlannerGroupPreviewJson);
        Register(pluginInterface, DadIpcContract.GetSchedulerPreview, this.plugin.GetSchedulerPreviewJson);
        Register<string, string>(pluginInterface, DadIpcContract.StartSchedulerPreset, this.plugin.StartSchedulerPresetFromJson);
        Register(pluginInterface, DadIpcContract.GetLaunchProfiles, this.plugin.GetLaunchProfilesJson);
        Register(pluginInterface, DadIpcContract.GetModuleCapabilities, () => DadIpcJson.Serialize(moduleRegistry.GetCapabilities()));
        Register(pluginInterface, DadIpcContract.GetSupportedJobHints, () => this.presetProviderService.GetSupportedJobHintsJson());
        Register<string, string>(pluginInterface, DadIpcContract.StartTasks, StartTasksFromJson);
        Register<string, string>(pluginInterface, DadIpcContract.StartRun, StartTasksFromJson);
        Register<string, string>(pluginInterface, DadIpcContract.StartPlannerGroup, this.plugin.StartPlannerGroupFromJson);
        Register(pluginInterface, DadIpcContract.CancelActiveRun, () => DadIpcJson.Serialize(coordinatorService.CancelActiveRun()));

        runStatusChangedProvider = pluginInterface.GetIpcProvider<string, object>(DadIpcContract.OnRunStatusChanged);
        coordinatorService.StatusChanged += OnRunStatusChanged;
    }

    public void Dispose()
    {
        coordinatorService.StatusChanged -= OnRunStatusChanged;

        foreach (var disposeAction in disposeActions)
            disposeAction();
    }

    private string StartTasksFromJson(string json)
    {
        var request = DadIpcJson.Deserialize<DadRunRequest>(json);
        if (request == null)
            return DadIpcJson.Serialize(DadRunResult.Rejected(null, "Unreadable dad task payload."));

        return DadIpcJson.Serialize(coordinatorService.StartTasks(request));
    }

    private void OnRunStatusChanged(DadRunResult result)
    {
        try
        {
            runStatusChangedProvider.SendMessage(DadIpcJson.Serialize(result));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Failed to publish run status change.");
        }
    }

    private void Register<TReturn>(IDalamudPluginInterface pluginInterface, string name, Func<TReturn> func)
    {
        var provider = pluginInterface.GetIpcProvider<TReturn>(name);
        provider.RegisterFunc(func);
        disposeActions.Add(provider.UnregisterFunc);
    }

    private void Register<TArg1, TReturn>(IDalamudPluginInterface pluginInterface, string name, Func<TArg1, TReturn> func)
    {
        var provider = pluginInterface.GetIpcProvider<TArg1, TReturn>(name);
        provider.RegisterFunc(func);
        disposeActions.Add(provider.UnregisterFunc);
    }
}
