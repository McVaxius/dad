using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using dad.Models;
using dad.Services;

namespace dad.Headless;

internal sealed class VirtualClock : TimeProvider
{
    private readonly object gate = new();
    private DateTimeOffset now = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly List<(DateTimeOffset Due, TaskCompletionSource Completion)> delays = [];
    public DateTimeOffset Now
    {
        get { lock (gate) return now; }
        set
        {
            lock (gate)
            {
                if (value < now) throw new InvalidOperationException("Virtual time cannot move backward.");
                now = value;
                foreach (var pending in delays.Where(item => item.Due <= now).ToArray())
                { pending.Completion.TrySetResult(); delays.Remove(pending); }
                delays.RemoveAll(item => item.Completion.Task.IsCompleted);
            }
        }
    }
    public async Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        lock (gate) delays.Add((now + delay, completion));
        await completion.Task.ConfigureAwait(false);
    }
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class RuntimeNode : IDisposable
{
    private readonly VirtualClock clock = new();
    private readonly ConcurrentQueue<string> events = new();
    private readonly ConcurrentQueue<string> unexpected = new();
    private readonly ConcurrentQueue<Action> frameworkWork = new();
    private readonly int frameworkThread = Environment.CurrentManagedThreadId;
    private readonly DadLifecycleUpdateLoop lifecycle;
    private readonly DadConfigurationPersistenceCoordinator persistence;
    private readonly DadDependencyService dependencies;
    private readonly List<DadInstalledPluginMetadata> installed = DadDependencyRules.Requirements.Select(requirement =>
        new DadInstalledPluginMetadata(requirement.AcceptedInternalNames[0], requirement.DisplayName, "99.0.0.0", true, false)).ToList();
    private readonly Configuration configuration;
    private readonly DadPresenceService presence;
    private readonly DadTransportService transport;
    private readonly DadWorkerExecutionService worker;
    private readonly DadCoordinatorService coordinator;
    private readonly DadNpcDutyQueueService npcDutyQueue;
    private readonly VirtualDutyFinder dutyFinder;
    private readonly DadLocalDutyQueueService localDutyQueue;
    private readonly VirtualHelperIpc helpers;
    private readonly VirtualProfileIpc profileIpc;
    private readonly VirtualShoppingIpc shoppingIpc;
    private readonly VirtualAutomationIpc automation;
    private readonly DadTitleMenuReadinessService title;
    private readonly List<Delegate> frameworkUpdates = [];
    private readonly Dictionary<string, List<Delegate>> ipcSubscriptions = [];
    private readonly DadAcquiredCharacter character;
    private readonly VirtualPartyGateway party;
    private readonly DadCharacterIntelligenceService intelligence;
    private readonly DadRosterCatalogService roster;
    private readonly DadProfileDirectoryService profiles;
    private readonly DadSchedulerService scheduler;
    private readonly DadPresetProviderService presets;
    private readonly DadPlannerService planner;
    private readonly DadQueueExecutionService queue;
    private readonly DadWakeTakeoverService wake;
    private readonly DadLocalLifecycleCleanup cleanup;
    private readonly string directory;
    private readonly AutoPartyNode? autoParty;
    private readonly DadAlliancePartyFinderService? allianceService;
    private readonly VirtualAllianceUi? allianceUi;
    private object? controlResult;
    private readonly string? verifierFault;
    private bool safe = true;
    private bool holdAfterAdmission;
    private bool adsSuccess = true;
    private int? durabilityPercent = 100;
    private bool repairBusy;
    private string repairFault = "";
    private int polls;
    private string lastWorkerState = "";
    private string lastWakeState = "";
    private string lastCoordinatorState = "";
    private bool shutdownApplied;

    public RuntimeNode(JsonElement input)
    {
        directory = input.GetProperty("directory").GetString()!;
        // The parent allocates and owns this explicit isolated runtime directory.
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new ArgumentException("An existing isolated runtime directory is required.");
        DadClock.Provider = clock;
        DadClock.Delay = clock.DelayAsync;
        if (input.TryGetProperty("now", out var initialNow)) clock.Now = initialNow.GetDateTimeOffset();
        var index = input.GetProperty("index").GetInt32();
        if (input.TryGetProperty("vermaxionLoaded", out var vermaxionLoaded) && vermaxionLoaded.GetBoolean())
            installed.Add(new("VERMAXION", "Synthetic VERMAXION", "99.0.0.0", true, false));
        verifierFault = input.TryGetProperty("verifierFault", out var fault) && fault.ValueKind == JsonValueKind.String ? fault.GetString() : null;
        var participantInput = input.TryGetProperty("participant", out var supplied) && supplied.ValueKind == JsonValueKind.Object ? supplied : default;
        var participantName = participantInput.ValueKind == JsonValueKind.Object ? participantInput.GetProperty("Name").GetString()! : $"Participant{index}";
        var account = participantInput.ValueKind == JsonValueKind.Object ? participantInput.GetProperty("Account").GetString()! : $"account-{index}";
        configuration = new()
        {
            PluginEnabled = true, RunAsServerDad = index == 0, ClientAccountId = $"lab-{account}",
            ServerListenHost = "127.0.0.1", ServerDadHost = "127.0.0.1",
            ServerListenPort = input.GetProperty("port").GetInt32(), ServerDadPort = input.GetProperty("port").GetInt32(),
            TransportSharedSecret = input.GetProperty("secret").GetString()!,
            CombatRotationMode = input.TryGetProperty("useFrenRider", out var useFrenRider) && useFrenRider.GetBoolean()
                ? DadCombatRotationMode.UseFrenRider : DadCombatRotationMode.DoNothing,
        };
        if (input.TryGetProperty("repairEnabled", out var repairEnabled))
            configuration.PreDutyRepairPolicy.Enabled = repairEnabled.GetBoolean();
        if (input.TryGetProperty("resume", out var resume) && resume.GetBoolean())
        {
            configuration = DadIpcJson.Deserialize<Configuration>(File.ReadAllText(Path.Combine(directory, "configuration.json")))
                ?? throw new InvalidOperationException("Isolated configuration is unreadable.");
            configuration.ServerListenHost = configuration.ServerDadHost = "127.0.0.1";
            configuration.ServerListenPort = configuration.ServerDadPort = input.GetProperty("port").GetInt32();
            configuration.TransportSharedSecret = input.GetProperty("secret").GetString()!;
        }
        automation = new(events.Enqueue);
        profileIpc = new(events.Enqueue);
        shoppingIpc = new(events.Enqueue);
        Plugin.PluginInterface = ExternalProxy.Create<IDalamudPluginInterface>(PluginCall);
        persistence = new(() => Plugin.PluginInterface.SavePluginConfig(configuration), () => clock.Now.UtcDateTime);
        configuration.AttachPersistenceCoordinator(persistence);
        Plugin.Condition = ExternalProxy.Create<ICondition>((method, args) => method.Name == "get_Item"
            ? dutyFinder?.Condition((ConditionFlag)args[0]!) ?? false
            : throw Unexpected(method.Name));
        Plugin.Framework = ExternalProxy.Create<IFramework>(FrameworkCall);
        Plugin.DutyState = ExternalProxy.Create<IDutyState>((method, _) => method.Name.StartsWith("add_") || method.Name.StartsWith("remove_")
            ? null : throw Unexpected(method.Name));
        var log = ExternalProxy.Create<IPluginLog>((method, args) =>
        {
            if (method.ReturnType == typeof(void))
            {
                if (args.OfType<Exception>().FirstOrDefault() is { } exception)
                    events.Enqueue($"log:{method.Name}:{exception.GetType().Name}:{exception.Message}");
                else if (args.OfType<string>().FirstOrDefault() is { } format)
                    events.Enqueue($"log:{method.Name}:{format}: {JsonSerializer.Serialize(args.LastOrDefault())}");
                return null;
            }
            throw Unexpected($"log:{method.Name}");
        });
        var configManager = new ConfigManager(Plugin.PluginInterface, log);
        configManager.EnsureAccountSelected(configuration.ClientAccountId, "Synthetic lab account");
        character = new()
        {
            AccountId = configuration.ClientAccountId, CharacterKey = $"Lab {participantName}@Synthetic",
            CharacterName = $"Lab {participantName}", WorldName = "Synthetic", WorldId = 1,
            ContentId = (ulong)(1000 + index), Source = DadCharacterSource.LocalRuntime,
            Freshness = DadSnapshotFreshness.Live, Readiness = DadReadinessState.Ready,
            CurrentJobId = participantInput.ValueKind == JsonValueKind.Object ? participantInput.GetProperty("JobId").GetUInt32() : 19,
            CurrentLevel = participantInput.ValueKind == JsonValueKind.Object ? participantInput.GetProperty("Level").GetInt32() : 100,
            DataCenterId = 1, DataCenterName = "Synthetic DC", TerritoryId = 1,
        };
        var playerState = ExternalProxy.Create<IPlayerState>((method, _) => method.Name switch
        {
            "get_ContentId" => character.ContentId,
            "get_CurrentWorld" => Activator.CreateInstance(method.ReturnType),
            _ => throw Unexpected($"player:{method.Name}")
        });
        party = new(character.ContentId, events.Enqueue);
        DadWorldLocationRuntime.CurrentObservation = now => WorldObservation(now);
        DadWorldLocationRuntime.WorldObservation = (world, now) => world == 1 ? WorldObservation(now) : null;
        var partyInvite = new InfoProxyPartyInviteGateway(configuration, Plugin.Framework, playerState, null!, Plugin.Condition, log, party);
        var partyTeardown = new DadPartyTeardownService(configuration, null!, playerState, Plugin.Condition, log, party);
        var vermaxion = new DadVermaxionIpcService(Plugin.PluginInterface, log);
        var autoRetainer = new DadAutoRetainerIpcService(Plugin.PluginInterface, log);
        var lifestream = new DadLifestreamIpcService(Plugin.PluginInterface);
        presence = new(configuration, configManager, vermaxion, autoRetainer, lifestream, partyInvite, partyTeardown, new(), null!, log,
            () => new(automation.Surface == "world", character, !safe || dutyFinder?.Stage is "queued" or "confirm" or "duty" or "completed",
                WorldObservation(clock.Now.UtcDateTime), UnsafeConditionOutsideQueue: !safe || dutyFinder?.Stage is "duty" or "completed"));
        dependencies = new(Plugin.PluginInterface, log);
        dependencies.ForceInspect(true);
        presence.ConfigureDependencySnapshotProvider(() => dependencies.Snapshot);
        var local = presence.CurrentParticipant;
        local.Character = character.Clone(); local.ActiveCharacterKey = character.CharacterKey;
        local.ManagedAccountKey = configuration.ClientAccountId;
        local.IsLocalClient = true; local.IsAuthority = index == 0;
        local.IsAvailable = true; local.IsEligibleForRun = true; local.PostArReady = true; local.WorldReadyStable = true;
        local.AutoRetainerAvailable = autoRetainer.Inspect().Available;
        local.State = DadParticipantState.Ready; local.AssignedSlotId = $"Slot{index + 1}";
        local.LastHeartbeatUtc = clock.Now.UtcDateTime;
        var claims = new DadClaimService();
        title = new(Plugin.Framework, null!, Plugin.Condition,
            ExternalProxy.Create<IKeyState>((method, values) =>
            {
                if (method.Name != "set_Item" || (int)values[0]! != 0x1B) throw Unexpected($"key:{method.Name}");
                events.Enqueue($"native:escape:{values[1]}"); return null;
            }), automation.Title);
        var commands = ExternalProxy.Create<ICommandManager>((method, values) => method.Name == "ProcessCommand"
            ? automation.Command((string)values[0]!) : throw Unexpected($"command:{method.Name}"));
        Plugin.CommandManager = commands;
        wake = new(new DadWakeTakeoverTarget(configuration, configManager, presence, autoRetainer, lifestream,
            vermaxion, title, commands, log), () => clock.Now.UtcDateTime,
            diagnostic: message => events.Enqueue($"wake:{message}"));
        vermaxion.ReservationGranted += wake.OnVermaxionReservationGranted;
        autoRetainer.CharacterPostprocessReady += wake.OnCharacterPostprocessReady;
        transport = new(configuration, presence, claims, wake, new DadRouletteRewardProbeService(Plugin.Framework, presence, log), log);
        var xadb = new DadXadbClient(Plugin.PluginInterface, log);
        intelligence = new(configManager, xadb, transport, log, () => character.Clone());
        roster = new(configuration, configManager, xadb, transport, presence, log);
        profiles = new(configuration, configManager, presence, transport, log);
        scheduler = new(configuration, configManager, profiles, intelligence, presence, transport, wake, roster, log);
        dutyFinder = new(() => character.ContentId, () => automation.Surface == "world", events.Enqueue, Unexpected);
        localDutyQueue = new(log, presence.BuildLiveSafetySnapshot, dutyFinder, presence.BuildLiveQueueConfirmationSnapshot);
        npcDutyQueue = new(log, dutyFinder, new VirtualNpcDutyNative(dutyFinder, () => character.CurrentJobId ?? 0, events.Enqueue, Unexpected));
        helpers = new(events.Enqueue);
        var ads = new DadDutySupportAdsService(Plugin.PluginInterface, log);
        var combat = new DadCombatRotationService(configuration, Plugin.PluginInterface, log);
        presence.ConfigureCombatRotationService(combat);
        presence.ConfigureLootGoblinReadinessProvider(() => new DadLootGoblinIpcService(Plugin.PluginInterface).IsReady());
        queue = new DadQueueExecutionService(new(), new(Plugin.PluginInterface), new(Plugin.PluginInterface),
            new(new()), localDutyQueue, npcDutyQueue, ads, combat);
        worker = new(queue, presence, combat, ads, new(ads, presence, log), new(ads, log, () => durabilityPercent is { } percent
            ? DadEquippedDurabilityObservation.ReadableAt(percent) : DadEquippedDurabilityObservation.Unreadable("Synthetic unreadable equipment")), Plugin.Condition, log);
        if (input.TryGetProperty("discordProvider", out var provider))
        {
            IReadOnlyList<DadAutoPartyCrewCandidate> Crew() => DadRuntimeHandlers.ReconcileAutoPartyCrew(
                configuration, roster, intelligence, presence, transport, DadClock.UtcNow).Candidates;
            autoParty = new(configuration, directory, new Uri(provider.GetString()!), events.Enqueue,
                now => DadAutoPartyListingPublicationRules.Build(configuration.AutoParty, Crew(), configuration.PlannerGroups, now),
                Crew, presence, transport, wake, claims, worker, partyInvite, combat, log);
        }
        var registry = new DadModuleRegistry();
        if (input.TryGetProperty("allianceEnabled", out var allianceEnabled) && allianceEnabled.GetBoolean())
        {
            if (autoParty == null) throw new InvalidOperationException("Alliance wiring requires the isolated endpoint adapter.");
            allianceUi = new(events.Enqueue);
            var gateway = new DadAlliancePartyFinderNativeGateway(configuration, Plugin.Framework, Plugin.Condition,
                null!, null!, presence, null!, null!, null!, null!, log, allianceUi);
            allianceService = new(presence, transport, autoParty.Endpoint, gateway, new DadAlliancePfAuditLog(directory),
                _ => "", () => "synthetic-coordinator", log);
            DadRuntimeHandlers.ConfigureAlliance(transport, allianceService);
        }
        presets = new DadPresetProviderService(registry, roster.GetAccountDirectory, dutyCatalogProvider: () =>
        [
            new DadPlannerDutyOption
            {
                ContentFinderConditionId = 4, TerritoryType = 1036, DutyDisplayName = "Synthetic duty",
                QueueSize = 4, JobLevelRequired = 1, AllowUndersized = true,
                SupportsDutySupport = true, SupportsTrust = true,
            },
            new DadPlannerDutyOption
            {
                ContentFinderConditionId = 5, TerritoryType = 1037, DutyDisplayName = "Synthetic command mission",
                QueueSize = 1, JobLevelRequired = 1,
            },
        ]);
        // Substitute only the game-sheet catalog; saved-preset resolution and validation
        // remain production code. No planner or scheduler state is seeded here.
        (typeof(DadPresetProviderService).GetField("plannerRouletteCatalog", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(DadPresetProviderService), "plannerRouletteCatalog"))
            .SetValue(presets, dutyFinder.RouletteCatalog());
        planner = new DadPlannerService(presets, registry, configuration);
        coordinator = new(configuration, configManager, intelligence, roster, presence, transport, claims, new(),
            partyInvite, partyTeardown, queue, worker, planner, log,
            () => configuration.AutoParty.RemoteBindings, autoParty?.Bridge);
        scheduler.ConfigureLevelingMode(BuildLevelingChild, () => coordinator.CancelActiveRun());
        if (autoParty != null)
        {
            autoParty.Service.ConfigureOwnerStop(_ => coordinator.CancelActiveRun());
            scheduler.ConfigureAutoPartyAuthorizationGate(autoParty.Service.EvaluateSchedulerAuthorization);
        }
        cleanup = new(configuration, scheduler, coordinator, wake, claims, worker, queue, presence, log,
            _ => { }, autoParty?.Service, allianceService);
        transport.ConfigureStopAllHandler(cleanup.RunLocalLifecycleCleanup);
        scheduler.ConfigureAdmissionBlocker(() => DadRuntimeHandlers.SchedulerAdmissionBlocker(
            configuration, scheduler, coordinator, false, coordinator.GetLocalResult()));
        var readiness = new DadRuntimeReadinessObserver(presence, intelligence, transport, scheduler, autoRetainer, wake);
        transport.ConfigureRuntimeReadinessHandler(readiness.ObserveRemote);
        presence.ConfigureParticipantResolver(id => transport.CurrentTransport.KnownParticipants.FirstOrDefault(p => p.WorkerSessionId == id));
        DadRuntimeHandlers.ConfigureCore(transport, coordinator, roster, intelligence, presence, profiles, configManager,
            worker, () =>
            {
                if (index > 0 && verifierFault == "stalled-polling") throw new IOException("Injected missing worker status response.");
                Interlocked.Increment(ref polls);
            }, acknowledgement =>
            {
                if (holdAfterAdmission && acknowledgement.Accepted) safe = false;
            });
        var steps = new Dictionary<string, Action>
        {
            ["Dependencies"] = () => dependencies.Update(true),
            ["TransportHeartbeat"] = () => transport.UpdateHeartbeat(presence.BuildSnapshotCopy(), true, false),
            ["WorkerExecution"] = worker.Update,
            ["Coordinator"] = coordinator.Update,
            ["ConfigurationPersistence"] = () => persistence.Update(),
        };
        if (autoParty != null)
        {
            steps["AutoPartyRelayAttach"] = autoParty.Attach;
            steps["AutoPartyEndpoint"] = () => autoParty.Endpoint.Update(true);
            steps["AutoParty"] = () => autoParty.Service.Update(true);
        }
        if (allianceService != null) steps["AlliancePartyFinder"] = allianceService.Update;
        if (input.TryGetProperty("fullLifecycle", out var full) && full.GetBoolean())
        {
            steps["CharacterIntelligence"] = intelligence.Update;
            steps["VermaxionReservation"] = vermaxion.Update;
            steps["Presence"] = () => presence.Update(intelligence.CurrentPool, transport.CurrentTransport.ListenerEndpoint);
            steps["PartyInviteAcceptance"] = partyInvite.UpdateAcceptance;
            steps["WakeTakeover"] = wake.Update;
            steps["RuntimeReadinessEdges"] = readiness.ObserveLocal;
            steps["PendingTakeoverCancellation"] = scheduler.UpdatePendingTakeoverCancellations;
            steps["PendingEarlyAssignmentCancellation"] = scheduler.UpdatePendingEarlyAssignmentCancellations;
            steps["PendingRewardProbeCancellation"] = scheduler.UpdatePendingRewardProbeCancellations;
            steps["RetainedRosterKnowledge"] = () => roster.LearnRetainedTransportKnowledge(intelligence.CurrentPool);
            steps["DeferredRosterPersistence"] = roster.UpdateDeferredPersistence;
            steps["ProfileDirectory"] = profiles.Update;
            steps["SchedulerEnqueue"] = scheduler.TickScheduleEnqueue;
            steps["SchedulerUpdate"] = () => DadRuntimeHandlers.UpdateScheduler(configuration, scheduler,
                coordinator, false, coordinator.GetLocalResult, ResolveGroup, BuildSchedulerPreview, coordinator.StartScheduledTasks);
        }
        lifecycle = new(steps, (_, action) => action());
    }

    public object Execute(JsonElement command)
    {
        controlResult = null;
        switch (command.GetProperty("op").GetString())
        {
            case "tick":
                if (verifierFault == "unexpected-callback")
                    Plugin.PluginInterface.GetIpcSubscriber<object>("Synthetic.UnknownCallback").InvokeFunc();
                clock.Now = command.TryGetProperty("now", out var observedNow) ? observedNow.GetDateTimeOffset()
                    : clock.Now.AddMilliseconds(command.TryGetProperty("milliseconds", out var ms) ? ms.GetInt32() : 250);
                presence.CurrentParticipant.LastHeartbeatUtc = clock.Now.UtcDateTime;
                foreach (var update in frameworkUpdates.ToArray()) update.DynamicInvoke(Plugin.Framework);
                while (frameworkWork.TryDequeue(out var callback)) callback();
                lifecycle.Update();
                var workerState = worker.GetStatus().State.ToString();
                if (lastWorkerState != workerState) { events.Enqueue($"state:worker:{workerState}"); lastWorkerState = workerState; }
                var coordinatorState = $"{coordinator.CurrentResult.Status}/{coordinator.CurrentResult.Phase}";
                if (lastCoordinatorState != coordinatorState) { events.Enqueue($"state:coordinator:{coordinatorState}"); lastCoordinatorState = coordinatorState; }
                break;
            case "wake":
                controlResult = wake.Handle(DadIpcJson.Deserialize<DadWakeTakeoverRequestDto>(command.GetProperty("request").GetRawText())!);
                break;
            case "ar-boundary":
                if (automation.Busy) throw new InvalidOperationException("A busy synthetic AR cannot deliver its completion boundary.");
                foreach (var handler in ipcSubscriptions["AutoRetainer.OnCharacterAdditionalTask"].ToArray()) handler.DynamicInvoke();
                if (automation.TakeReadyCallback())
                    foreach (var handler in ipcSubscriptions["AutoRetainer.OnCharacterReadyForPostprocess"].ToArray()) handler.DynamicInvoke("Dad");
                break;
            case "observe":
                if (command.TryGetProperty("level", out var observedLevel))
                {
                    character.CurrentLevel = observedLevel.GetInt32();
                    character.JobLevels[character.CurrentJobId!.Value] = character.CurrentLevel.Value;
                    character.XadbReady = true; character.SnapshotVersion = 1;
                    character.XadbSnapshotUtc = clock.Now.UtcDateTime; character.SnapshotQuality = "Complete";
                }
                if (command.TryGetProperty("shoppingStage", out var shoppingStage)) shoppingIpc.Stage = shoppingStage.GetString()!;
                if (command.TryGetProperty("shoppingFault", out var shoppingFault)) shoppingIpc.Fault = shoppingFault.GetString()!;
                if (command.TryGetProperty("allianceUi", out var observedAlliance))
                    (allianceUi ?? throw new InvalidOperationException("Alliance UI is not configured.")).Observe(observedAlliance);
                if (command.TryGetProperty("surface", out var surface)) automation.Surface = surface.GetString()!;
                if (command.TryGetProperty("uncertainLogin", out var uncertainLogin)) automation.UncertainLogin = uncertainLogin.GetBoolean();
                if (command.TryGetProperty("automationBusy", out var busy)) automation.Busy = busy.GetBoolean();
                if (command.TryGetProperty("multiMode", out var multi)) automation.MultiMode = multi.GetBoolean();
                if (command.TryGetProperty("contentId", out var observedContent)) character.ContentId = observedContent.GetUInt64();
                if (command.TryGetProperty("characterKey", out var observedCharacter))
                {
                    character.CharacterKey = observedCharacter.GetString()!;
                    character.CharacterName = character.CharacterKey.Split('@')[0];
                }
                if (command.TryGetProperty("safe", out var observedSafe)) safe = observedSafe.GetBoolean();
                if (command.TryGetProperty("holdAfterAdmission", out var hold)) holdAfterAdmission = hold.GetBoolean();
                if (command.TryGetProperty("adsSuccess", out var ads)) adsSuccess = ads.GetBoolean();
                if (command.TryGetProperty("durabilityPercent", out var durability)) durabilityPercent = durability.ValueKind == JsonValueKind.Null ? null : durability.GetInt32();
                if (command.TryGetProperty("repairBusy", out var repairing)) repairBusy = repairing.GetBoolean();
                if (command.TryGetProperty("repairFault", out var repairFailure)) repairFault = repairFailure.GetString()!;
                if (command.TryGetProperty("queueFault", out var queueFault)) dutyFinder.Fault = queueFault.GetString()!;
                if (command.TryGetProperty("stage", out var stage))
                {
                    dutyFinder.Stage = stage.GetString()!;
                }
                if (command.TryGetProperty("helperStage", out var helperStage)) helpers.Stage = helperStage.GetString()!;
                if (command.TryGetProperty("helperReady", out var helperReady)) helpers.Ready = helperReady.GetBoolean();
                if (command.TryGetProperty("helperFault", out var helperFault)) helpers.Fault = helperFault.GetString()!;
                if (command.TryGetProperty("party", out var members))
                    party.Members = DadIpcJson.Deserialize<List<DadPartyMemberSnapshot>>(members.GetRawText())!;
                if (command.TryGetProperty("invitation", out var invitation))
                    party.Invitation = DadIpcJson.Deserialize<DadPendingPartyInvitation>(invitation.GetRawText());
                break;
            case "start":
                var request = DadIpcJson.Deserialize<DadRunRequest>(command.GetProperty("request").GetRawText())!;
                // Runtime-only opaque identities are normally supplied by the planner UI;
                // they are intentionally absent from the public LAN request format.
                if (command.TryGetProperty("sharedIdentities", out var identities))
                {
                    if (identities.GetArrayLength() != request.Orchestration.RequiredRosterCharacters.Count)
                        throw new ArgumentException("Shared identities must match the ordered input roster.");
                    for (var slot = 0; slot < identities.GetArrayLength(); slot++)
                        request.Orchestration.RequiredRosterCharacters[slot].SharedIdentityToken = identities[slot].GetString() ?? "";
                }
                controlResult = coordinator.StartTasks(request);
                break;
            case "save-preset":
                var saved = DadIpcJson.Deserialize<DadPlannerGroup>(command.GetProperty("group").GetRawText())!;
                configuration.PlannerGroups.RemoveAll(g => g.GroupId == saved.GroupId);
                configuration.PlannerGroups.Add(saved); configuration.Save();
                break;
            case "alliance-create":
                var allianceGroup = DadIpcJson.Deserialize<DadPlannerGroup>(command.GetProperty("group").GetRawText())!;
                var alliancePreview = presets.BuildPlannerPreview(roster.BuildCuratedPool(intelligence.CurrentPool),
                    presets.BuildOptionsForGroup(allianceGroup, null), allianceGroup);
                controlResult = new { preview = alliancePreview, status = allianceService!.CreateParty(allianceGroup, alliancePreview) };
                break;
            case "alliance-grab": controlResult = allianceService!.GrabDads(); break;
            case "alliance-stop": allianceService!.Stop("Synthetic operator stop"); controlResult = allianceService.GetStatus(); break;
            case "start-schedule":
                var schedule = DadIpcJson.Deserialize<DadScheduleDefinition>(command.GetProperty("schedule").GetRawText())!;
                configuration.Schedules.RemoveAll(s => s.ScheduleId == schedule.ScheduleId);
                configuration.Schedules.Add(schedule);
                controlResult = scheduler.StartScheduleRun(schedule.ScheduleId, false, "lifecycle-lab");
                break;
            case "start-scheduler":
                var group = DadIpcJson.Deserialize<DadPlannerGroup>(command.GetProperty("group").GetRawText())!;
                configuration.PlannerGroups.RemoveAll(g => g.GroupId == group.GroupId);
                configuration.PlannerGroups.Add(group);
                var preview = BuildSchedulerPreview(group.GroupId)!;
                controlResult = new { preview, admission = scheduler.StartPreset(group, preview, false) };
                break;
            case "seed-routing":
                // Focused reproduction setup at the existing routing boundary. This
                // deliberately makes no claim about scheduler or discovery coverage.
                SeedRouting(DadIpcJson.Deserialize<List<DadParticipantSnapshot>>(command.GetProperty("participants").GetRawText())!);
                break;
            case "snapshot": break;
            case "autoparty": controlResult = (autoParty ?? throw new InvalidOperationException("AutoParty is not configured.")).Execute(command); break;
            case "cancel": coordinator.CancelActiveRun(); break;
            case "stop-all": controlResult = transport.RequestStopAll(new DadStopAllRequest
            {
                OperationId = command.GetProperty("operationId").GetString()!,
                Reason = "Synthetic lifecycle Stop All", RequestedAtUtc = clock.Now.UtcDateTime,
                RequestedByWorkerSessionId = presence.WorkerSessionId,
            }); break;
            case "dependency":
                installed[0] = installed[0] with
                {
                    InternalName = command.GetProperty("internalName").GetString()!,
                    DisplayName = command.GetProperty("displayName").GetString()!,
                };
                dependencies.ForceInspect(true);
                break;
            default: throw Unexpected($"control:{command.GetProperty("op").GetString()}");
        }
        var wakeState = wake.GetActiveStatus();
        var wakeSummary = $"{wakeState?.Phase}:{wakeState?.Summary}";
        if (wakeSummary != lastWakeState) { events.Enqueue($"state:wake:{wakeSummary}"); lastWakeState = wakeSummary; }
        var relayState = autoParty?.Endpoint.RelayStatus?.SafeCode;
        if (relayState != lastRelayState) { events.Enqueue($"state:relay:{relayState}"); lastRelayState = relayState; }
        return Snapshot();
    }

    private string? lastRelayState;

    public object Snapshot() => new
    {
        participant = presence.BuildSnapshotCopy(), worker = worker.GetStatus(), coordinator = coordinator.GetLocalResult(),
        scheduler = scheduler.CurrentState, schedule = configuration.ActiveScheduleRun, wake = wake.GetActiveStatus(), stopAll = transport.LatestStopAllStatus,
        partyMembers = party.Members,
        peers = transport.CurrentTransport.KnownParticipants, polls, events = Drain(events), unexpected = unexpected.ToArray(),
        now = clock.Now, transport = transport.CurrentTransport.Availability,
        unexecutedSteps = lifecycle.UnboundSteps,
        persistedActiveRun = configuration.PersistedActiveRun, historyCount = configuration.RunHistory.Count,
        controlResult, autoParty = autoParty?.Snapshot(), helperRunning = helpers.IsRunning,
        alliance = allianceService?.GetStatus(), allianceReceiver = allianceService?.BuildUiSnapshot(),
        automation = new { automation.Suppressed, automation.MultiMode, automation.Surface },
        dutyFinder = new { dutyFinder.Stage, isUnrestrictedParty = dutyFinder.ObservedUnrestrictedParty,
            dutyFinder.SelectedId, dutyFinder.InterfaceSelectedId, dutyFinder.Fault },
        build = new
        {
            dad = typeof(RuntimeNode).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            protocol = typeof(AutoParty.Contracts.AutoPartyProtocol).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            ecommons = typeof(ECommons.ECommonsMain).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            dalamud = typeof(IDalamudPluginInterface).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            dalamudAssemblyVersion = typeof(IDalamudPluginInterface).Assembly.GetName().Version?.ToString(),
            clientStructs = typeof(FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            protectedData = typeof(System.Security.Cryptography.ProtectedData).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        },
    };

    // Raw XA IPC contract fields; production parsers still own interpretation and merging.
    private object XadbCharacter() => new
    {
        character.CharacterKey, character.CharacterName, character.ContentId, character.WorldId, character.WorldName,
        character.DataCenterId, character.DataCenterName, character.CurrentJobId, character.CurrentLevel,
        character.JobLevels, character.SnapshotVersion, character.SnapshotQuality,
        snapshotUtc = character.XadbSnapshotUtc, isCurrent = true,
    };

    private DadLevelingChildBuild BuildLevelingChild(DadPlannerGroup source, int iteration)
        => DadLevelingRuntime.BuildChild(source, roster.BuildCuratedPool(intelligence.CurrentPool), iteration,
            [new() { JobId = 19, Abbreviation = "PLD", Role = DadPartyRole.Tank, IsFullCombatJob = true }],
            presets, configuration.CompletionActions,
            (preview, pool, group) => DadPlannerRuntimeValidation.Apply(preview, pool, group, planner, queue, presence.BuildLiveSafetySnapshot));

    private DadPlannerGroup? ResolveGroup(string id) => configuration.PlannerGroups.FirstOrDefault(g => g.GroupId == id);
    private DadPlannerRunRequestPreview? BuildSchedulerPreview(string id)
    {
        var group = ResolveGroup(id);
        if (group == null) return null;
        if (group.LevelingMode?.Enabled == true) return DadLevelingRuntime.Preview(BuildLevelingChild(group, 1));
        var pool = roster.BuildCuratedPool(intelligence.CurrentPool);
        var options = presets.BuildOptionsForGroup(group, null);
        return DadPlannerRuntimeValidation.Apply(presets.BuildPlannerRunRequestPreview(pool, options,
                requestedAtUtc: clock.Now.UtcDateTime, selectedGroup: group),
            pool, group, planner, queue, presence.BuildLiveSafetySnapshot);
    }

    private void SeedRouting(List<DadParticipantSnapshot> participants)
    {
        if (!configuration.RunAsServerDad || coordinator.IsBusy) throw new InvalidOperationException("Routing setup requires an idle coordinator.");
        foreach (var participant in participants)
        {
            participant.IsLocalClient = participant.WorkerSessionId == presence.WorkerSessionId;
            participant.RunId = "lab-i305";
            participant.ClaimState = DadClaimState.Granted;
            participant.LeaseState = DadParticipantLeaseState.Granted;
        }
        var orchestration = new DadOrchestrationIntent
        {
            RequirePostArReady = true, InviteAuthority = DadInviteAuthority.PresetLeader,
            PreferredLeaderCharacterKey = participants[0].ActiveCharacterKey,
            PreferredInviterCharacterKey = participants[0].ActiveCharacterKey,
            RosterIntent = new() { ExpectedPartySize = participants.Count, RequireRemoteParticipants = true, RequireExactCharacters = true },
            RequiredRosterCharacters = participants.Select(p => new DadRosterCharacterRef
                { AccountKey = p.ManagedAccountKey, CharacterKey = p.ActiveCharacterKey, ContentId = p.Character.ContentId }).ToList(),
            RequiredAccountKeys = participants.Select(p => p.ManagedAccountKey).ToList(),
            RequiredCharacterKeys = participants.Select(p => p.ActiveCharacterKey).ToList(),
        };
        var module = new DadPlannedModuleExecution { ModuleId = DadModuleId.PremadeDuty, DisplayName = "Synthetic premade duty", ExpectedPartySize = participants.Count, RequiresPeers = true };
        var plan = new DadRunPlan
        {
            Request = new() { RequestId = "lab-i305", RequestedBy = "lifecycle-lab", Orchestration = orchestration,
                PremadeDuty = new() { ContentFinderConditionId = 4, DutyName = "Synthetic duty", Unsynced = true, ExpectedPartySize = participants.Count, Attempts = 1 } },
            Orchestration = orchestration, Modules = [module], RequiredParticipantCount = participants.Count,
            RequiresRemoteParticipants = true, LeaderCharacterKey = participants[0].ActiveCharacterKey.Value,
            InviterCharacterKey = participants[0].ActiveCharacterKey.Value, CompositeModuleId = DadModuleId.PremadeDuty,
        };
        if (!DadRunSlotManifestRules.TryCreate(plan, out var manifest, out var blocker)) throw new InvalidOperationException(blocker);
        for (var i = 0; i < participants.Count; i++) manifest.Slots[i].WorkerSessionId = participants[i].WorkerSessionId;
        Set("activePlan", plan); Set("activeSlotManifest", manifest); Set("activeModuleIndex", 0);
        var active = (List<DadParticipantSnapshot>)Field("activeParticipants").GetValue(coordinator)!;
        active.AddRange(participants);
        var result = coordinator.CurrentResult;
        result.RequestId = plan.Request.RequestId; result.Status = DadRunStatus.Running; result.Phase = DadRunPhase.QueuePreparing;
        result.ModuleId = DadModuleId.PremadeDuty;
        events.Enqueue("setup:production-coordinator-routing-boundary");
    }

    private static FieldInfo Field(string name) => typeof(DadCoordinatorService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(name);
    private void Set(string name, object value) => Field(name).SetValue(coordinator, value);
    private Exception Unexpected(string name) { unexpected.Enqueue(name); return new InvalidOperationException($"Unexpected external request: {name}"); }
    private object? PluginCall(MethodInfo method, object?[] args)
    {
        if (method.Name == "GetPluginConfigDirectory") return directory;
        if (method.Name == "SavePluginConfig") { File.WriteAllText(Path.Combine(directory, "configuration.json"), DadIpcJson.Serialize(args[0])); return null; }
        if (method.Name == "get_InstalledPlugins")
        {
            var contract = method.ReturnType.GetGenericArguments()[0];
            var plugins = Array.CreateInstance(contract, installed.Count);
            for (var i = 0; i < installed.Count; i++)
            {
                var item = installed[i];
                plugins.SetValue(ExternalProxy.Create(contract, (property, _) => property.Name switch
                {
                    "get_InternalName" => item.InternalName,
                    "get_Name" => item.DisplayName,
                    "get_Version" => Version.Parse(item.Version),
                    "get_IsLoaded" => item.IsLoaded,
                    "get_IsOutdated" => item.IsOutdated,
                    _ => throw Unexpected($"metadata:{property.Name}")
                }), i);
            }
            return plugins;
        }
        if (method.Name.StartsWith("add_") || method.Name.StartsWith("remove_")) return null;
        if (method.Name == "GetIpcSubscriber")
        {
            var endpoint = (string)args[0]!;
            return ExternalProxy.Create(method.ReturnType, (call, values) =>
            {
                if (call.Name is "Subscribe" or "Unsubscribe")
                {
                    if (!ipcSubscriptions.TryGetValue(endpoint, out var handlers)) ipcSubscriptions[endpoint] = handlers = [];
                    if (call.Name == "Subscribe") handlers.Add((Delegate)values[0]!); else handlers.Remove((Delegate)values[0]!);
                    return null;
                }
                if (call.Name == "InvokeFunc" && endpoint == "ADS.GetStatusJson")
                    return DadIpcJson.Serialize(new { utilityRunning = repairBusy, utilityTask = "repair", utilityMode = "self", utilityStatus = "Synthetic repair observation" });
                if (call.Name == "InvokeFunc" && endpoint is "ADS.StartShopListPreset" or "ADS.GetShopListPresetStatusJson" or "ADS.CancelShopListPreset")
                    return shoppingIpc.Invoke(endpoint, values);
                if (call.Name == "InvokeFunc" && endpoint == "ADS.StartRepair")
                {
                    if (values is not [string mode] || mode != "self") throw Unexpected("Unsupported synthetic repair mode.");
                    events.Enqueue("ipc:ADS.StartRepair:self");
                    if (repairFault == "uncertain") throw new IOException("Synthetic repair acknowledgement lost.");
                    return repairFault != "rejected";
                }
                if (call.Name == "InvokeFunc" && endpoint == "ADS.PatchConfigurationJson")
                { events.Enqueue("ipc:ADS.PatchConfigurationJson"); return JsonSerializer.Serialize(new { success = adsSuccess, message = "Synthetic ADS response." }); }
                if (call.Name == "InvokeFunc" && (endpoint.StartsWith("MOGTOME.") || endpoint.StartsWith("LootGoblin.")))
                    return helpers.Invoke(endpoint, values);
                if (call.Name == "InvokeFunc" && endpoint.StartsWith("FrenRider.Dad.", StringComparison.Ordinal))
                {
                    try { return profileIpc.Invoke(endpoint, values); }
                    catch (Exception ex) { throw Unexpected($"ipc:{endpoint}:{ex.Message}"); }
                }
                if (call.Name == "InvokeFunc" && endpoint == "XA.Database.IsReady") return true;
                if (call.Name == "InvokeFunc" && endpoint == "XA.Database.GetCharacterSummaryJson") return DadIpcJson.Serialize(XadbCharacter());
                if (call.Name == "InvokeFunc" && endpoint == "XA.Database.GetAccountCharacterListJson")
                    return DadIpcJson.Serialize(new { version = 1, ipcContractVersion = 6, isFullRosterAvailable = true,
                        generatedAtUtc = clock.Now.UtcDateTime, characters = new[] { XadbCharacter() } });
                if (call.Name == "InvokeAction" && endpoint is "XA.Database.Refresh" or "XA.Database.Save")
                { events.Enqueue($"ipc:{endpoint}"); return null; }
                if (endpoint.StartsWith("AutoRetainer.", StringComparison.Ordinal) || endpoint.StartsWith("Lifestream.", StringComparison.Ordinal) || endpoint.StartsWith("VERMAXION.", StringComparison.Ordinal))
                {
                    try { return automation.Invoke(endpoint, call.Name, values); }
                    catch (InvalidOperationException) { throw Unexpected($"ipc:{endpoint}:{call.Name}"); }
                }
                throw Unexpected($"ipc:{endpoint}:{call.Name}");
            });
        }
        throw Unexpected($"plugin:{method.Name}");
    }
    private object? FrameworkCall(MethodInfo method, object?[] args)
    {
        if (method.Name == "add_Update") { frameworkUpdates.Add((Delegate)args[0]!); return null; }
        if (method.Name == "remove_Update") { frameworkUpdates.Remove((Delegate)args[0]!); return null; }
        if (method.Name == "get_IsInFrameworkUpdateThread") return Environment.CurrentManagedThreadId == frameworkThread;
        if (method.Name == "RunOnFrameworkThread")
        {
            if (method.ReturnType.IsGenericType)
                return GetType().GetMethod(nameof(QueueFramework), BindingFlags.NonPublic | BindingFlags.Instance)!
                    .MakeGenericMethod(method.ReturnType.GenericTypeArguments[0]).Invoke(this, [args[0]]);
            return QueueFramework<object?>((Func<object?>)(() => { ((Delegate)args[0]!).DynamicInvoke(); return null; }));
        }
        throw Unexpected($"framework:{method.Name}");
    }
    private Task<T> QueueFramework<T>(Delegate action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        frameworkWork.Enqueue(() =>
        {
            try { completion.SetResult((T)action.DynamicInvoke()!); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }
    private static string[] Drain(ConcurrentQueue<string> queue)
    { var result = new List<string>(); while (queue.TryDequeue(out var item)) result.Add(item); return result.ToArray(); }
    private static DadWorldLocationObservation WorldObservation(DateTime now) => new()
    { WorldId = 1, WorldName = "Synthetic", DataCenterId = 1, DataCenterName = "Synthetic DC", RegionId = 1, RegionName = "Synthetic region", ObservedAtUtc = now };
    public void Dispose() {
        Shutdown();
        allianceService?.Dispose(); autoParty?.Dispose(); localDutyQueue.Dispose(); npcDutyQueue.Dispose(); title.Dispose(); dependencies.Dispose(); wake.Dispose(); profiles.Dispose(); persistence.ForceFlush(); transport.Dispose(); }

    public void Shutdown()
    {
        if (shutdownApplied) return;
        shutdownApplied = true;
        cleanup.RunLocalLifecycleCleanup(new DadStopAllRequest { OperationId = "lab-shutdown", Reason = "Synthetic shutdown" });
        persistence.ForceFlush();
    }
}
