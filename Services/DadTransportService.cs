using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using dad.Models;
using Dalamud.Plugin.Services;

namespace dad.Services;

public sealed class DadTransportService : IDisposable
{
    private const string MessageSnapshotRequest = "snapshot-request";
    private const string MessageWakeRequest = "wake-request";
    private const string MessageWakeTakeoverRequest = "wake-takeover-request";
    private const string MessageRouletteRewardProbe = "roulette-reward-probe";
    private const string MessageClaimRequest = "claim-request";
    private const string MessageAssemblyInstruction = "assembly-instruction";
    private const string MessageCharacterLoadCommand = "character-load-command";
    private const string MessageCancelRun = "cancel-run";
    private const string MessageCancelCommand = "cancel-command";
    private const string MessageStatusQuery = "status-query";
    private const string MessageStartRun = "start-run";
    private const string MessageRosterCatalogRequest = "roster-catalog-request";
    private const string MessageRosterAggregateCatalogRequest = "roster-aggregate-catalog-request";
    private const string MessageRosterRefreshCommand = "roster-refresh-command";
    private const string MessageHubRosterPublish = "hub-roster-publish";
    private const string MessageHubRosterPublishRequest = "hub-roster-publish-request";
    private const string MessageProfileCatalogRequest = "profile-catalog-request";
    private const string MessageProfileAggregateCatalogRequest = "profile-aggregate-catalog-request";
    private const string MessageProfileUpdateCommand = "profile-update-command";
    private const string MessageWorkerExecutionCommand = "worker-execution-command";
    private const string MessageWorkerExecutionStatus = "worker-execution-status";
    private const string MessageWorkerExecutionCancel = "worker-execution-cancel";
    private const string MessageStopAll = "stop-all";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OutboundWriteTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxReconnectBackoff = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopAllCleanupPollInterval = TimeSpan.FromMilliseconds(100);
    // B7: cadence to rebuild the local roster-catalog cache on the framework thread, and the max age an
    // inbound peer pull will accept from that cache before falling back to a live (framework-thread) build.
    private static readonly TimeSpan LocalRosterCatalogRebuildInterval = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan LocalRosterCatalogServeTtl = TimeSpan.FromSeconds(30);
    private const int MaxConcurrentConnections = 32;
    private const int MaxConcurrentOutboundOperations = 8;
    private const int MaxConcurrentInboundRequestsPerConnection = 16;
    private const int MaxTransportEventsPerFrame = 64;
    private const int MaxTransportEventBacklog = 2048;

    private readonly Configuration configuration;
    private readonly DadPresenceService presenceService;
    private readonly DadClaimService claimService;
    private readonly DadWakeTakeoverService wakeTakeoverService;
    private readonly DadRouletteRewardProbeService rouletteRewardProbeService;
    private readonly IPluginLog log;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly object roleGate = new();
    private readonly object localParticipantGate = new();
    private readonly DadHubSessionRegistry<DadHubConnection> serverSessions = new();
    private readonly ConcurrentDictionary<string, DisconnectedParticipant> disconnectedParticipants = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DadHubFrame>> pendingRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> operations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CompletedOperation> completedOperations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DadPeerRosterCatalogResponse> rosterCatalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DadProfileCatalogResponse> profileCatalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> profileCatalogOfflineSinceUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DadStopAllStatus> stopAllOperations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object stopAllGate = new();
    private readonly ConcurrentDictionary<string, DateTime> nextRosterRefreshUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> nextProfileRefreshUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly DadBoundedFrameworkEventQueue frameworkCallbacks = new(MaxTransportEventBacklog);
    private readonly DadBoundedFrameworkEventQueue transportEvents = new(MaxTransportEventBacklog);
    private readonly DadDeferredDisposalSemaphore connectionSlots = new(MaxConcurrentConnections, MaxConcurrentConnections);
    private readonly DadDeferredDisposalSemaphore outboundSlots = new(MaxConcurrentOutboundOperations, MaxConcurrentOutboundOperations);
    private readonly DadRosterPublishCoalescer rosterPublishCoalescer = new();
    private readonly DadReadinessHeartbeatCoalescer readinessHeartbeatCoalescer = new();
    private readonly Dictionary<string, long> pendingRuntimeReadinessChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly DadBackgroundTaskObserver backgroundTasks;
    private long pendingOutboundOperations;
    private DateTime lastReconnectLogUtc = DateTime.MinValue;
    private int lastReconnectLogDelaySeconds = -1;
    private bool hubRosterProjectionOversize;
    private string transportRosterSignature = string.Empty;

    private CancellationTokenSource roleCancellation = new();
    private TcpListener? listener;
    private DadHubConnection? clientConnection;
    private DadParticipantSnapshot localParticipant;
    private DadParticipantSnapshot? serverParticipant;
    private DadHubRosterPublish? lastHubRosterPublish;
    private long hubRosterGeneration;
    // B2: most-recent roster projection pushed by the coordinator; clients render peers from this with no pull.
    private IReadOnlyList<DadHubRosterCatalogRow> lastPushedCatalogRows = [];
    // B7: cached local roster-catalog response (built on the framework thread on a cadence) so inbound peer
    // pulls are served off-thread instead of triggering a synchronous XADB fetch + rebuild on the game thread.
    private CachedLocalRosterCatalog? cachedLocalRosterCatalog;
    private DateTime nextLocalRosterCatalogRebuildUtc = DateTime.MinValue;
    private DadHubRosterPublishCursor lastAppliedHubRosterPublish = DadHubRosterPublishCursor.Empty;
    private string hubRosterAuthorityEpochId = Guid.NewGuid().ToString("N");
    private string lastAuthOrProtocolError = string.Empty;
    private DateTime nextHeartbeatUtc = DateTime.MinValue;
    private bool localPluginEnabled;
    private bool localOnlyModeEnabled;
    private bool remoteMutationsAllowed;
    private bool disposed;

    private Func<DadRunResult>? statusProvider;
    private Func<DadRunRequest, DadRunResult>? startRunHandler;
    private Func<DadCancelCommandDto, DadRunResult>? cancelRunHandler;
    private Func<DadAccountRosterCatalog>? rosterCatalogProvider;
    private Func<DadRosterRefreshCommandDto, DadRosterRefreshResultDto>? rosterRefreshHandler;
    private Func<DadProfileCatalog>? profileCatalogProvider;
    private Func<DadProfileUpdateRequest, DadProfileUpdateAck>? profileUpdateHandler;
    private Func<DadWorkerExecutionCommand, DadWorkerExecutionAck>? workerExecutionHandler;
    private Func<DadWorkerExecutionStatus>? workerStatusProvider;
    private Func<DadWorkerExecutionCancel, DadWorkerExecutionAck>? workerCancelHandler;
    private Func<DadStopAllRequest, DadStopAllWorkerResult>? stopAllHandler;
    private Action<DadWorkerSessionId, long>? runtimeReadinessHandler;
    private DadStopAllStatus? latestStopAllStatus;

    public DadTransportService(
        Configuration configuration,
        DadPresenceService presenceService,
        DadClaimService claimService,
        DadWakeTakeoverService wakeTakeoverService,
        DadRouletteRewardProbeService rouletteRewardProbeService,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.presenceService = presenceService;
        this.claimService = claimService;
        this.wakeTakeoverService = wakeTakeoverService;
        this.rouletteRewardProbeService = rouletteRewardProbeService;
        this.log = log;
        backgroundTasks = new DadBackgroundTaskObserver(log, "transport");
        localParticipant = presenceService.BuildSnapshotCopy();

        CurrentTransport = new DadPeerTransportSnapshot
        {
            Availability = "Starting",
            TransportMode = DadTransportMode.ServerHub,
            LocalClientInstanceId = presenceService.ClientInstanceId,
            LocalWorkerSessionId = presenceService.WorkerSessionId,
            ProtocolVersion = DadHubProtocol.CurrentVersion,
            LastRequestStatus = "Dad hub starting.",
        };
        UpdateLanDiagnostics();

        RestartTransport();
    }

    public bool IsReady { get; private set; }

    public DadPeerTransportSnapshot CurrentTransport { get; }

    public DadStopAllStatus? LatestStopAllStatus
    {
        get
        {
            lock (stopAllGate)
                return latestStopAllStatus?.Clone();
        }
    }

    public string GetConfiguredAuthorityEndpoint()
        => configuration.RunAsServerDad
            ? FormatEndpoint(configuration.ServerListenHost, configuration.ServerListenPort)
            : FormatEndpoint(configuration.ServerDadHost, configuration.ServerDadPort);

    public string GetPreferredAuthorityEndpoint()
        => !string.IsNullOrWhiteSpace(CurrentTransport.AuthorityEndpoint)
            ? CurrentTransport.AuthorityEndpoint
            : GetConfiguredAuthorityEndpoint();

    public bool IsWorkerOnline(DadWorkerSessionId workerSessionId)
    {
        if (workerSessionId.IsEmpty)
            return false;

        if (string.Equals(workerSessionId.Value, presenceService.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase))
            return true;

        if (configuration.RunAsServerDad)
            return serverSessions.TryGet(workerSessionId, out var connection) && connection is { IsRoutable: true };

        if (clientConnection is not { IsRoutable: true })
            return false;

        if (string.Equals(serverParticipant?.WorkerSessionId.Value, workerSessionId.Value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return CurrentTransport.KnownParticipants.Any(participant =>
            participant.State != DadParticipantState.Stale &&
            string.Equals(
                participant.WorkerSessionId.Value,
                workerSessionId.Value,
                StringComparison.OrdinalIgnoreCase));
    }

    public void RestartListener() => RestartTransport();

    public void SetPluginEnabled(bool enabled)
    {
        localPluginEnabled = enabled;
        RestartTransport();
    }

    public void RestartTransport()
    {
        if (disposed)
            return;

        CancellationTokenSource previous;
        lock (roleGate)
        {
            previous = roleCancellation;
            roleCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
            listener?.Stop();
            listener = null;
            CloseAllConnections("Transport configuration changed.");
            disconnectedParticipants.Clear();
            rosterCatalogs.Clear();
            profileCatalogs.Clear();
            profileCatalogOfflineSinceUtc.Clear();
            completedOperations.Clear();
            lastHubRosterPublish = null;
            lastPushedCatalogRows = [];
            transportRosterSignature = string.Empty;
            Interlocked.Increment(ref CurrentTransport.TransportRevision);
            cachedLocalRosterCatalog = null;
            nextLocalRosterCatalogRebuildUtc = DateTime.MinValue;
            lastAppliedHubRosterPublish = DadHubRosterPublishCursor.Empty;
            rosterPublishCoalescer.Reset();
            frameworkCallbacks.Clear();
            transportEvents.Clear();
            lastAuthOrProtocolError = string.Empty;
            if (configuration.RunAsServerDad)
            {
                hubRosterAuthorityEpochId = Guid.NewGuid().ToString("N");
                Interlocked.Exchange(ref hubRosterGeneration, 0);
            }
            IsReady = false;
            CurrentTransport.ListenerEndpoint = string.Empty;
            CurrentTransport.AdvertisedEndpoint = string.Empty;
            CurrentTransport.Availability = "Starting";
            CurrentTransport.LastRequestStatus = configuration.RunAsServerDad
                ? "Starting Dad Coordinator listener."
                : "Connecting to Dad Coordinator.";
            UpdateLanDiagnostics();
        }

        previous.Cancel();
        previous.Dispose();
        if (!configuration.PluginEnabled)
        {
            CurrentTransport.Availability = "Disabled";
            CurrentTransport.ConnectionStatus = "DAD disabled; coordinator reconnect is stopped.";
            CurrentTransport.LastRequestStatus = CurrentTransport.ConnectionStatus;
            CurrentTransport.AuthorityRoutable = false;
            CurrentTransport.AuthorityWorkerSessionId = new DadWorkerSessionId(string.Empty);
            CurrentTransport.NextReconnectUtc = null;
            return;
        }
        Track(
            configuration.RunAsServerDad
                ? RunServerAsync(roleCancellation.Token)
                : RunClientReconnectLoopAsync(roleCancellation.Token),
            configuration.RunAsServerDad ? "server listener" : "client reconnect loop");
    }

    public void ConfigureAuthorityHandlers(
        Func<DadRunResult> statusProvider,
        Func<DadRunRequest, DadRunResult> startRunHandler,
        Func<DadCancelCommandDto, DadRunResult> cancelRunHandler)
    {
        this.statusProvider = statusProvider;
        this.startRunHandler = startRunHandler;
        this.cancelRunHandler = cancelRunHandler;
    }

    public void ConfigureRosterHandlers(
        Func<DadAccountRosterCatalog> rosterCatalogProvider,
        Func<DadRosterRefreshCommandDto, DadRosterRefreshResultDto> rosterRefreshHandler)
    {
        this.rosterCatalogProvider = rosterCatalogProvider;
        this.rosterRefreshHandler = rosterRefreshHandler;
    }

    public void ConfigureProfileHandlers(
        Func<DadProfileCatalog> profileCatalogProvider,
        Func<DadProfileUpdateRequest, DadProfileUpdateAck> profileUpdateHandler)
    {
        this.profileCatalogProvider = profileCatalogProvider;
        this.profileUpdateHandler = profileUpdateHandler;
    }

    public void ConfigureWorkerExecutionHandlers(
        Func<DadWorkerExecutionCommand, DadWorkerExecutionAck> workerExecutionHandler,
        Func<DadWorkerExecutionStatus> workerStatusProvider,
        Func<DadWorkerExecutionCancel, DadWorkerExecutionAck> workerCancelHandler)
    {
        this.workerExecutionHandler = workerExecutionHandler;
        this.workerStatusProvider = workerStatusProvider;
        this.workerCancelHandler = workerCancelHandler;
    }

    public void ConfigureStopAllHandler(Func<DadStopAllRequest, DadStopAllWorkerResult> stopAllHandler)
        => this.stopAllHandler = stopAllHandler;

    public void ConfigureRuntimeReadinessHandler(Action<DadWorkerSessionId, long> handler)
        => runtimeReadinessHandler = handler;

    public void NotifyLocalRuntimeReadinessChanged(long revision)
        => readinessHeartbeatCoalescer.MarkPending(revision);

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        // Suppress all late completion logging before cancellation starts. The observer still
        // consumes task exceptions, but cannot call Dalamud logging after plugin teardown.
        backgroundTasks.Dispose();
        lifetimeCancellation.Cancel();
        roleCancellation.Cancel();
        listener?.Stop();
        CloseAllConnections("Dad transport disposed.");
        foreach (var pending in pendingRequests.Values)
            pending.TrySetCanceled();

        // These semaphores reject new work immediately and dispose their physical handles only
        // after pre-existing waiters/sessions/sends release their lifetime leases.
        connectionSlots.Dispose();
        outboundSlots.Dispose();
    }

    public void UpdateHeartbeat(
        DadParticipantSnapshot participant,
        bool pluginEnabled,
        bool localOnlyModeEnabled)
    {
        localPluginEnabled = pluginEnabled;
        this.localOnlyModeEnabled = localOnlyModeEnabled;
        remoteMutationsAllowed = pluginEnabled && !localOnlyModeEnabled;

        var snapshot = participant.Clone();
        snapshot.Endpoint = string.Empty;
        if (!remoteMutationsAllowed)
            MarkSnapshotUnavailable(snapshot, BuildLocalUnavailableReason());

        lock (localParticipantGate)
            localParticipant = snapshot;

        DrainTransportEvents();
        DrainFrameworkCallbacks();
        SweepDisconnectedParticipants();
        SweepCompletedOperations();
        PruneOfflineProfileCatalogs(DateTime.UtcNow);

        var now = DateTime.UtcNow;
        if (!pluginEnabled)
        {
            RefreshTransportSnapshot();
            return;
        }

        if (!configuration.RunAsServerDad &&
            clientConnection is { IsRoutable: true } liveConnection &&
            DadReconnectPolicy.IsInboundStale(
                liveConnection.LastFrameReceivedUtc,
                now,
                GetHeartbeatStaleThreshold()))
        {
            var reason = $"Dad Coordinator sent no frames for {GetHeartbeatStaleThreshold().TotalSeconds:F0}s; reconnecting.";
            CurrentTransport.LastTransportTimeoutSummary = reason;
            CurrentTransport.LastDisconnectReason = reason;
            CurrentTransport.LastDisconnectedUtc = now;
            liveConnection.Close();
        }

        if (readinessHeartbeatCoalescer.TryCapture(now, nextHeartbeatUtc, out var readinessTicket))
        {
            nextHeartbeatUtc = now + GetHeartbeatInterval();
            var delivered = localOnlyModeEnabled;
            if (configuration.RunAsServerDad)
            {
                var connections = serverSessions.Snapshot().Where(static connection => connection.IsRoutable).ToList();
                foreach (var connection in connections)
                    Track(SendHeartbeatAsync(connection, snapshot, roleCancellation.Token), "server heartbeat");
                delivered = true; // With no clients, the next hello already carries the latest snapshot.
                MarkHubRosterDirty(
                    readinessTicket.HasReadinessEdge
                        ? "Dad Coordinator runtime readiness changed."
                        : "Dad Coordinator heartbeat.",
                    fast: readinessTicket.HasReadinessEdge);
            }
            else if (clientConnection is { IsRoutable: true } connection)
            {
                Track(SendHeartbeatAsync(connection, snapshot, roleCancellation.Token), "client heartbeat");
                delivered = true;
            }

            if (delivered)
                readinessHeartbeatCoalescer.Acknowledge(readinessTicket);
        }

        if (configuration.RunAsServerDad)
            RefreshRemoteCatalogCaches();

        RebuildLocalRosterCatalogCacheIfDue(now);
        FlushHubRosterPublishIfDue(now);
        RefreshTransportSnapshot();
        FlushRuntimeReadinessChanges();
    }

    public DadPeerTransportSnapshot RequestSnapshots(DadPeerSnapshotRequest request)
    {
        RefreshLocalMutationState();
        if (configuration.RunAsServerDad)
        {
            MarkHubRosterDirty("Dad Coordinator snapshot request.", fast: true);
        }
        else if (!localOnlyModeEnabled)
        {
            RequestHubRosterPublish(BuildLocalTransportSnapshot());
        }

        RefreshTransportSnapshot();
        CurrentTransport.LastRequestUtc = DateTime.UtcNow;
        if (!IsHubRosterFallbackStatus(CurrentTransport.LastRequestStatus))
        {
            CurrentTransport.LastRequestStatus = CurrentTransport.LastResponses.Count == 0
                ? "No connected Dad workers."
                : $"Read {CurrentTransport.LastResponses.Count} worker snapshot(s) from Dad Coordinator hub sessions.";
        }

        return CurrentTransport;
    }

    public DadParticipantReadyDto? SendWakeRequest(DadParticipantSnapshot participant, DadWakeRequestDto request)
        => TryRequest<DadWakeRequestDto, DadParticipantReadyDto>(
            participant.WorkerSessionId,
            MessageWakeRequest,
            request,
            $"wake:{request.RunId}:{participant.WorkerSessionId.Value}:{request.AssignedSlotId}");

    public DadWakeTakeoverResultDto? SendWakeTakeoverRequest(
        DadParticipantSnapshot participant,
        DadWakeTakeoverRequestDto request)
        => TryRequest<DadWakeTakeoverRequestDto, DadWakeTakeoverResultDto>(
            participant.WorkerSessionId,
            MessageWakeTakeoverRequest,
            request,
            $"wake-takeover:{participant.WorkerSessionId.Value}:{DadWakePolicyRules.BuildOperationKey(request)}");

    public DadRouletteRewardProbeResultDto? SendRouletteRewardProbe(
        DadParticipantSnapshot participant,
        DadRouletteRewardProbeRequestDto request)
    {
        RefreshLocalMutationState();
        if (string.Equals(
                participant.WorkerSessionId.Value,
                presenceService.WorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            return remoteMutationsAllowed
                ? rouletteRewardProbeService.Handle(request)
                : DadRouletteRewardProbeResultDto.FromRequest(
                    request,
                    DadRouletteRewardProbeOutcome.Unknown,
                    BuildRemoteMutationRejectedReason("roulette reward probe"),
                    DateTime.UtcNow);
        }

        return TryRequest<DadRouletteRewardProbeRequestDto, DadRouletteRewardProbeResultDto>(
            participant.WorkerSessionId,
            MessageRouletteRewardProbe,
            request,
            $"roulette-reward:{participant.WorkerSessionId.Value}:{request.OperationId}:{request.Operation}");
    }

    public DadClaimDecisionDto? RequestClaim(DadParticipantSnapshot participant, DadClaimRequestDto request)
        => TryRequest<DadClaimRequestDto, DadClaimDecisionDto>(
            participant.WorkerSessionId,
            MessageClaimRequest,
            request,
            $"claim:{request.RunId}:{participant.WorkerSessionId.Value}:{request.SlotId}");

    public DadRunStepResultDto? SendAssemblyInstruction(
        DadParticipantSnapshot participant,
        DadAssemblyInstructionDto instruction)
        => TryRequest<DadAssemblyInstructionDto, DadRunStepResultDto>(
            participant.WorkerSessionId,
            MessageAssemblyInstruction,
            instruction,
            $"assembly:{instruction.RunId}:{participant.WorkerSessionId.Value}:{instruction.SlotId}:{instruction.InstructionKind}");

    public DadCharacterLoadResultDto? SendCharacterLoadCommand(
        DadParticipantSnapshot participant,
        DadCharacterLoadCommandDto command)
        => TryRequest<DadCharacterLoadCommandDto, DadCharacterLoadResultDto>(
            participant.WorkerSessionId,
            MessageCharacterLoadCommand,
            command,
            $"character-load:{participant.WorkerSessionId.Value}:{command.CommandId}");

    public DadRunResult? QueryAuthorityStatus(string endpoint)
    {
        var target = ResolveAuthorityWorkerSessionId();
        return target.IsEmpty
            ? null
            : TryRequest<string, DadRunResult>(
                target,
                MessageStatusQuery,
                string.Empty,
                $"authority-status:{target.Value}");
    }

    public async Task<DadRunResult?> QueryAuthorityStatusAsync(CancellationToken cancellationToken)
    {
        var target = ResolveAuthorityWorkerSessionId();
        if (target.IsEmpty || clientConnection is not { IsRoutable: true })
            return null;

        var response = await SendRequestAsync(
                target,
                MessageStatusQuery,
                DadIpcJson.Serialize(string.Empty),
                cancellationToken)
            .ConfigureAwait(false);
        if (response.Kind == DadHubFrameKind.Error)
            throw new DadHubProtocolException(response.ErrorCode, response.ErrorMessage);
        return DadIpcJson.Deserialize<DadRunResult>(response.PayloadJson);
    }

    public DadRunResult? SendStartRunCommand(string endpoint, DadRunRequest request)
    {
        var target = ResolveAuthorityWorkerSessionId();
        if (target.IsEmpty)
            return null;

        var result = TryRequest<DadRunRequest, DadRunResult>(
            target,
            MessageStartRun,
            request,
            $"start-run:{request.RequestId}");
        return result ?? DadRunResult.FromRequest(
            request,
            DadRunStatus.Queued,
            "Forwarded run to Dad Coordinator; awaiting authority status.");
    }

    public DadRunResult? SendCancelCommand(string endpoint, DadCancelCommandDto command)
    {
        var target = ResolveAuthorityWorkerSessionId();
        if (target.IsEmpty)
            return null;

        var result = TryRequest<DadCancelCommandDto, DadRunResult>(
            target,
            MessageCancelCommand,
            command,
            $"cancel-command:{command.RunId}");
        return result ?? new DadRunResult
        {
            RequestId = command.RunId,
            Status = DadRunStatus.Running,
            CancellationState = DadRunCancellationState.Requested,
            AuthorityWorkerSessionId = target,
            AuthorityEndpoint = GetPreferredAuthorityEndpoint(),
            Summary = "Forwarded cancellation to Dad Coordinator; awaiting authority status.",
        };
    }

    public DadStopAllStatus RequestStopAll(DadStopAllRequest request)
    {
        RefreshLocalMutationState();
        NormalizeStopAllRequest(request);
        lock (stopAllGate)
        {
            if (stopAllOperations.TryGetValue(request.OperationId, out var recorded))
                return recorded.Clone();
        }

        log.Information("[dad] Stop-all submitted: operation {OperationId} by {WorkerSessionId}.",
            request.OperationId,
            request.RequestedByWorkerSessionId);

        if (configuration.RunAsServerDad)
            return BeginCoordinatorStopAll(request);

        var target = ResolveAuthorityWorkerSessionId();
        if (!localOnlyModeEnabled && !target.IsEmpty && CanQueueOperation(target))
        {
            var pending = new DadStopAllStatus
            {
                OperationId = request.OperationId,
                RequestedByWorkerSessionId = request.RequestedByWorkerSessionId,
                SubmittedAtUtc = request.RequestedAtUtc,
                UpdatedAtUtc = DateTime.UtcNow,
                Summary = "Stop-all forwarded to Dad Coordinator; awaiting authority acknowledgement.",
            };
            RecordStopAllStatus(pending);
            QueueForwardStopAll(request, target);
            return pending.Clone();
        }

        var local = InvokeLocalStopAll(request);
        var fallback = new DadStopAllStatus
        {
            OperationId = request.OperationId,
            RequestedByWorkerSessionId = request.RequestedByWorkerSessionId,
            SubmittedAtUtc = request.RequestedAtUtc,
            UpdatedAtUtc = DateTime.UtcNow,
            RemotePropagationAvailable = false,
            Partial = true,
            Summary = "Local DAD work stopped; Dad Coordinator propagation was unavailable.",
            LocalResult = local,
        };
        DadStopAllStatusRules.FinalizeFromWorkers(fallback, DateTime.UtcNow);
        RecordStopAllStatus(fallback);
        if (DadStopAllStatusRules.IsLocalCleanupPending(local))
            QueueStopAllLocal(request);
        else
            LogStopAllFinal(fallback);
        return fallback.Clone();
    }

    public IReadOnlyList<DadPeerRosterCatalogResponse> RequestRosterCatalogs(DadRosterRefreshPlan request)
        => RequestAggregateRosterCatalogs(request).Responses;

    public DadAggregateRosterCatalogResponse RequestAggregateRosterCatalogs(DadRosterRefreshPlan request)
    {
        RefreshLocalMutationState();
        request ??= new DadRosterRefreshPlan();
        request.PlanId = string.IsNullOrWhiteSpace(request.PlanId)
            ? Guid.NewGuid().ToString("N")
            : request.PlanId;

        DadAggregateRosterCatalogResponse aggregate;
        if (configuration.RunAsServerDad)
        {
            aggregate = BuildServerRosterAggregate(
                request,
                requestingWorkerSessionId: new DadWorkerSessionId(string.Empty),
                includeRequester: true);
        }
        else
        {
            aggregate = BuildClientRosterAggregate(request);
        }

        CurrentTransport.LastRequestUtc = DateTime.UtcNow;
        CurrentTransport.LastRequestStatus = aggregate.Summary;
        return aggregate;
    }

    // B1: monotonic revision the roster UI polls; advances whenever a fresh peer catalog (pull response or
    // pushed projection) lands so the merged catalog can re-render itself without a manual click.
    public long RosterCatalogCacheRevision => Interlocked.Read(ref CurrentTransport.RosterCatalogCacheRevision);
    public long TransportRevision => Interlocked.Read(ref CurrentTransport.TransportRevision);
    public long ProfileCatalogCacheRevision => Interlocked.Read(ref CurrentTransport.ProfileCatalogCacheRevision);

    // B1: already-cached peer catalog responses (no network pull), used by the UI to re-merge when the
    // revision advances. Excludes the local worker's own response.
    public IReadOnlyList<DadPeerRosterCatalogResponse> GetCachedRosterCatalogResponses()
        => rosterCatalogs.Values
            .Where(response => !string.Equals(
                response.WorkerSessionId.Value,
                presenceService.WorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

    // B2: peer catalog responses reconstructed from the coordinator's pushed projection so a client renders
    // peers (and the coordinator) with no pull at all. Empty on the coordinator (it builds the projection).
    public IReadOnlyList<DadPeerRosterCatalogResponse> GetPushedPeerCatalogResponses()
        => DadHubRosterCatalogProjection.BuildPeerCatalogResponses(lastPushedCatalogRows, presenceService.WorkerSessionId);

    public bool PurgeAccountCaches(DadAccountKey accountKey)
    {
        var rosterChanged = false;
        foreach (var pair in rosterCatalogs.ToList())
        {
            var response = pair.Value;
            var removedCharacters = response.Catalog.Characters.RemoveAll(character => DadRosterIdentity.SameAccount(character.AccountKey, accountKey));
            var removedAccounts = response.Catalog.Accounts.RemoveAll(account => DadRosterIdentity.SameAccount(account.AccountKey, accountKey));
            var removedVisibility = response.Catalog.Visibility.RemoveAll(record => DadRosterIdentity.SameAccount(record.AccountKey, accountKey));
            rosterChanged |= removedCharacters + removedAccounts + removedVisibility > 0;
        }

        var profileChanged = false;
        foreach (var pair in profileCatalogs.ToList())
        {
            var removed = pair.Value.Catalog.Accounts.RemoveAll(account => DadRosterIdentity.SameAccount(account.AccountKey, accountKey));
            profileChanged |= removed > 0;
        }

        var retainedParticipants = CurrentTransport.KnownParticipants
            .Where(participant => !DadRosterIdentity.SameAccount(participant.ManagedAccountKey, accountKey) &&
                                  !DadRosterIdentity.SameAccount(
                                      DadRosterIdentity.ResolveAccountKey(
                                          participant.Character.AccountId,
                                          participant.Character.AccountAlias),
                                      accountKey))
            .ToList();
        if (retainedParticipants.Count != CurrentTransport.KnownParticipants.Count)
        {
            SetTransportRoster(retainedParticipants);
            rosterChanged = true;
        }

        var pushedRows = lastPushedCatalogRows
            .Where(row => !DadRosterIdentity.SameAccount(row.AccountKey, accountKey))
            .ToList();
        if (pushedRows.Count != lastPushedCatalogRows.Count)
        {
            lastPushedCatalogRows = pushedRows;
            rosterChanged = true;
        }

        if (lastHubRosterPublish != null)
        {
            var removed = lastHubRosterPublish.CatalogRows.RemoveAll(row => DadRosterIdentity.SameAccount(row.AccountKey, accountKey));
            rosterChanged |= removed > 0;
        }

        if (rosterChanged)
        {
            Interlocked.Increment(ref CurrentTransport.RosterCatalogCacheRevision);
            InvalidateLocalRosterCatalogCache();
            if (configuration.RunAsServerDad)
                MarkHubRosterDirty($"Purged cached account {accountKey}.", fast: true);
        }

        if (profileChanged)
            Interlocked.Increment(ref CurrentTransport.ProfileCatalogCacheRevision);

        return rosterChanged || profileChanged;
    }

    // B5: a local level/active-job-level change happened; promptly republish (server) or request a publish
    // (client) and invalidate the cached local catalog so the projection picks up the new job levels. The
    // existing coalescer throttles bursts from multi-level gains.
    public void NotifyLocalRosterChanged(string reason)
    {
        RefreshLocalMutationState();
        if (configuration.RunAsServerDad)
            MarkHubRosterDirty(reason, fast: true);
        else if (!localOnlyModeEnabled)
            RequestHubRosterPublish(BuildLocalTransportSnapshot());

        InvalidateLocalRosterCatalogCache();
    }

    public DadAggregateProfileCatalogResponse RequestAggregateProfileCatalogs(string requestId)
    {
        RefreshLocalMutationState();
        requestId = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId;

        var aggregate = configuration.RunAsServerDad
            ? BuildServerProfileAggregate(
                requestId,
                requestingWorkerSessionId: new DadWorkerSessionId(string.Empty),
                includeRequester: true)
            : BuildClientProfileAggregate(requestId);

        CurrentTransport.LastRequestUtc = DateTime.UtcNow;
        CurrentTransport.LastRequestStatus = aggregate.Summary;
        return aggregate;
    }

    public DadRosterRefreshResultDto? SendRosterRefreshCommand(
        DadParticipantSnapshot participant,
        DadRosterRefreshCommandDto command)
        => TryRequest<DadRosterRefreshCommandDto, DadRosterRefreshResultDto>(
            participant.WorkerSessionId,
            MessageRosterRefreshCommand,
            command,
            $"roster-refresh:{participant.WorkerSessionId.Value}:{command.CommandId}");

    public IReadOnlyList<DadProfileCatalogResponse> RequestProfileCatalogs(string requestId)
        => RequestAggregateProfileCatalogs(requestId).Responses;

    public DadProfileUpdateAck? SendProfileUpdate(
        DadWorkerSessionId ownerWorkerSessionId,
        DadProfileUpdateRequest request,
        Action<DadProfileUpdateAck>? completed = null)
    {
        var key = $"profile-update:{ownerWorkerSessionId.Value}:{request.RequestId}";
        var immediate = TryTakeCompleted<DadProfileUpdateAck>(key);
        if (immediate != null)
            return immediate;

        if (operations.ContainsKey(key))
        {
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                Summary = "Profile update is awaiting Client Dad acknowledgement.",
            };
        }

        QueueOperation(
            key,
            ownerWorkerSessionId,
            MessageProfileUpdateCommand,
            request,
            completed);
        return new DadProfileUpdateAck
        {
            RequestId = request.RequestId,
            Summary = "Profile update queued through Dad Coordinator hub.",
        };
    }

    public DadWorkerExecutionAck? SendWorkerExecutionCommand(
        DadParticipantSnapshot participant,
        DadWorkerExecutionCommand command)
    {
        var operationKey = $"worker-command:{participant.WorkerSessionId.Value}:{command.CommandId}";
        var acknowledgement = TryRequest<DadWorkerExecutionCommand, DadWorkerExecutionAck>(
            participant.WorkerSessionId,
            MessageWorkerExecutionCommand,
            command,
            operationKey);
        return DadWorkerStatusPollingRules.SelectCommandAcknowledgement(acknowledgement, command);
    }

    public DadWorkerExecutionStatus? GetWorkerExecutionStatus(
        DadParticipantSnapshot participant,
        DadWorkerExecutionCommand command,
        DadWorkerExecutionStatus? cachedStatus)
    {
        var operationKey = $"worker-status:{participant.WorkerSessionId.Value}:{command.CommandId}";
        var liveStatus = TryRequest<string, DadWorkerExecutionStatus>(
            participant.WorkerSessionId,
            MessageWorkerExecutionStatus,
            command.RunId,
            operationKey);
        return DadWorkerStatusPollingRules.SelectRemoteStatus(
            liveStatus,
            cachedStatus,
            command,
            operations.ContainsKey(operationKey),
            ResolveConnection(participant.WorkerSessionId) is { IsRoutable: true });
    }

    public DadWorkerExecutionAck? SendWorkerExecutionCancel(
        DadParticipantSnapshot participant,
        DadWorkerExecutionCancel command)
        => TryRequest<DadWorkerExecutionCancel, DadWorkerExecutionAck>(
            participant.WorkerSessionId,
            MessageWorkerExecutionCancel,
            command,
            $"worker-cancel:{participant.WorkerSessionId.Value}:{command.RunId}");

    public List<DadCancelAckDto> BroadcastCancel(
        DadCancelCommandDto command,
        IEnumerable<DadParticipantSnapshot> participants)
    {
        var acks = new List<DadCancelAckDto>();
        foreach (var participant in participants.Where(static participant => !participant.IsLocalClient))
        {
            var ack = TryRequest<DadCancelCommandDto, DadCancelAckDto>(
                participant.WorkerSessionId,
                MessageCancelRun,
                command,
                $"cancel-run:{participant.WorkerSessionId.Value}:{command.RunId}");
            if (ack != null)
                acks.Add(ack);
        }

        return acks;
    }

    public DadCancelAckDto? SendCancelRun(
        DadParticipantSnapshot participant,
        DadCancelCommandDto command)
        => TryRequest<DadCancelCommandDto, DadCancelAckDto>(
            participant.WorkerSessionId,
            MessageCancelRun,
            command,
            $"cancel-run:{participant.WorkerSessionId.Value}:{command.RunId}");

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            var bindAddress = await ResolveAddressAsync(configuration.ServerListenHost, cancellationToken).ConfigureAwait(false);
            try
            {
                DadHubProtocol.RequireSharedSecretForAddress(bindAddress, configuration.TransportSharedSecret);
            }
            catch (DadHubProtocolException ex)
            {
                SetTransportAuthOrProtocolError("LAN Dad Coordinator requires a shared secret");
                log.Warning("[dad] {Code}: {Message}", ex.Code, ex.Message);
                return;
            }

            var port = NormalizePort(configuration.ServerListenPort);
            var activeListener = new TcpListener(bindAddress, port);
            activeListener.Start();
            lock (roleGate)
                listener = activeListener;

            var endpoint = (IPEndPoint)activeListener.LocalEndpoint;
            var advertisedHost = GetAdvertisedHost(configuration.ServerListenHost, endpoint.Address);
            CurrentTransport.ListenerEndpoint = FormatEndpoint(advertisedHost, endpoint.Port);
            CurrentTransport.AuthorityEndpoint = CurrentTransport.ListenerEndpoint;
            CurrentTransport.AuthorityWorkerSessionId = presenceService.WorkerSessionId;
            CurrentTransport.AuthorityRole = DadWorkerRole.ServerDad;
            CurrentTransport.AuthorityStatus = DadStatusText.FormatAuthorityStatus(
                DadWorkerRole.ServerDad,
                presenceService.WorkerSessionId,
                CurrentTransport.ListenerEndpoint,
                DadAuthorityMode.ServerDad);
            CurrentTransport.Availability = "Ready";
            CurrentTransport.ConnectionStatus = $"Dad Coordinator listening on {CurrentTransport.ListenerEndpoint}.";
            CurrentTransport.LastRequestStatus = CurrentTransport.ConnectionStatus;
            CurrentTransport.AuthorityRoutable = true;
            ClearTransportAuthOrProtocolError();
            UpdateLanDiagnostics();
            IsReady = true;

            while (!cancellationToken.IsCancellationRequested)
            {
                var connectionLease = await connectionSlots.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
                if (connectionLease == null)
                    break;
                try
                {
                    var client = await activeListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    Track(HandleServerClientWithReleaseAsync(client, connectionLease, cancellationToken), "server client session");
                }
                catch
                {
                    connectionLease.Dispose();
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex) when (DadBackgroundTaskObserver.IsExpectedShutdownException(ex))
        {
        }
        catch (Exception ex)
        {
            SetTransportError($"Dad Coordinator listener failed: {ex.Message}");
            log.Error(ex, "[dad] Dad Coordinator listener failed.");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
                IsReady = false;
        }
    }

    private async Task HandleServerClientWithReleaseAsync(
        TcpClient client,
        DadDeferredDisposalSemaphoreLease connectionLease,
        CancellationToken serverCancellation)
    {
        try
        {
            await HandleServerClientAsync(client, serverCancellation).ConfigureAwait(false);
        }
        finally
        {
            connectionLease.Dispose();
        }
    }

    private async Task HandleServerClientAsync(TcpClient client, CancellationToken serverCancellation)
    {
        DadHubConnection? connection = null;
        try
        {
            client.NoDelay = true;
            connection = new DadHubConnection(client, serverCancellation, DadHubHandshakeRole.Server);
            var helloFrame = await ReadWithTimeoutAsync(connection.Stream, ConnectTimeout, connection.Cancellation.Token)
                .ConfigureAwait(false);
            if (helloFrame == null)
                throw new DadHubProtocolException("hello-missing", "Client Dad closed before sending hello.");

            if (helloFrame.ProtocolVersion != DadHubProtocol.CurrentVersion)
            {
                var message = $"Dad hub protocol {helloFrame.ProtocolVersion} is incompatible; expected {DadHubProtocol.CurrentVersion}.";
                RecordAuthOrProtocolError(new DadHubProtocolException("protocol-mismatch", message));
                await SendFrameAsync(
                    connection,
                    DadHubProtocol.CreateError(
                        presenceService.WorkerSessionId,
                        helloFrame.SourceWorkerSessionId,
                        helloFrame.CorrelationId,
                        "protocol-mismatch",
                        message,
                        configuration.TransportSharedSecret),
                    "protocol-mismatch",
                    connection.Cancellation.Token).ConfigureAwait(false);
                return;
            }

            try
            {
                DadHubProtocol.ValidateFrame(helloFrame, configuration.TransportSharedSecret);
            }
            catch (DadHubProtocolException ex)
            {
                RecordAuthOrProtocolError(ex);
                await SendFrameAsync(
                    connection,
                    DadHubProtocol.CreateError(
                        presenceService.WorkerSessionId,
                        helloFrame.SourceWorkerSessionId,
                        helloFrame.CorrelationId,
                        ex.Code,
                        ex.Message,
                        configuration.TransportSharedSecret),
                    ex.Code,
                    connection.Cancellation.Token).ConfigureAwait(false);
                return;
            }

            if (helloFrame.Kind != DadHubFrameKind.Hello)
                throw new DadHubProtocolException("hello-missing", "First Dad hub frame must be hello.");

            var hello = DadIpcJson.Deserialize<DadHubHello>(helloFrame.PayloadJson)
                        ?? throw new DadHubProtocolException("hello-invalid", "Client Dad hello payload is invalid.");
            if (hello.WorkerSessionId.IsEmpty ||
                !string.Equals(
                    hello.WorkerSessionId.Value,
                    helloFrame.SourceWorkerSessionId.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new DadHubProtocolException("hello-invalid", "Client Dad hello worker session does not match frame source.");
            }

            connection.WorkerSessionId = hello.WorkerSessionId;
            connection.RemoteWorkerSessionId = hello.WorkerSessionId;
            connection.ClientInstanceId = hello.ClientInstanceId;
            connection.Participant = DadHubParticipants.PrepareRemote(hello.Participant, DateTime.UtcNow);
            connection.ObserveRuntimeReadiness(connection.Participant, out _);
            connection.LastHeartbeatUtc = DateTime.UtcNow;

            var ack = new DadHubHello
            {
                ClientInstanceId = presenceService.ClientInstanceId,
                WorkerSessionId = presenceService.WorkerSessionId,
                BuildVersion = GetBuildVersion(),
                Participant = GetLocalParticipant(),
            };
            await SendFrameAsync(
                connection,
                DadHubProtocol.CreateFrame(
                    DadHubFrameKind.HelloAck,
                    presenceService.WorkerSessionId,
                    hello.WorkerSessionId,
                    "hello",
                    helloFrame.CorrelationId,
                    DadIpcJson.Serialize(ack),
                    configuration.TransportSharedSecret),
                "hello-ack",
                connection.Cancellation.Token).ConfigureAwait(false);

            connection.MarkHandshakeReady();
            RegisterServerSession(connection);
            MarkHubRosterDirty($"Client Dad {connection.WorkerSessionId} connected.", fast: true);
            await RunConnectionReaderAsync(connection, isServerSide: true).ConfigureAwait(false);
        }
        catch (DadHubProtocolException ex)
        {
            log.Warning("[dad] Rejected Client Dad connection: {Code}: {Message}", ex.Code, ex.Message);
            CurrentTransport.LastRequestStatus = $"Rejected Client Dad: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (DadBackgroundTaskObserver.IsExpectedShutdownException(ex))
        {
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[dad] Client Dad session ended.");
        }
        finally
        {
            if (connection != null)
                MarkServerSessionDisconnected(connection);
            else
                client.Dispose();
        }
    }

    private async Task RunClientReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            DadHubConnection? activeConnection = null;
            try
            {
                var host = NormalizeHost(configuration.ServerDadHost);
                var port = NormalizePort(configuration.ServerDadPort);
                var displayAttempt = attempt + 1;
                CurrentTransport.ReconnectAttempt = displayAttempt;
                CurrentTransport.NextReconnectUtc = null;
                CurrentTransport.ConnectionStatus = $"Connecting to Dad Coordinator at {FormatEndpoint(host, port)} (attempt {displayAttempt}).";
                var address = await ResolveAddressAsync(host, cancellationToken).ConfigureAwait(false);
                try
                {
                    DadHubProtocol.RequireSharedSecretForAddress(address, configuration.TransportSharedSecret);
                }
                catch (DadHubProtocolException ex)
                {
                    SetTransportAuthOrProtocolError("LAN connection requires W's shared secret");
                    log.Warning("[dad] {Code}: {Message}", ex.Code, ex.Message);
                    throw;
                }

                using var client = new TcpClient(address.AddressFamily) { NoDelay = true };
                await client.ConnectAsync(address, port, cancellationToken)
                    .AsTask()
                    .WaitAsync(ConnectTimeout, cancellationToken)
                    .ConfigureAwait(false);

                var connection = new DadHubConnection(client, cancellationToken, DadHubHandshakeRole.Client)
                {
                    WorkerSessionId = presenceService.WorkerSessionId,
                    ClientInstanceId = presenceService.ClientInstanceId,
                };
                activeConnection = connection;

                var hello = new DadHubHello
                {
                    ClientInstanceId = presenceService.ClientInstanceId,
                    WorkerSessionId = presenceService.WorkerSessionId,
                    BuildVersion = GetBuildVersion(),
                    Participant = GetLocalParticipant(),
                };
                var correlationId = Guid.NewGuid().ToString("N");
                await SendFrameAsync(
                    connection,
                    DadHubProtocol.CreateFrame(
                        DadHubFrameKind.Hello,
                        presenceService.WorkerSessionId,
                        new DadWorkerSessionId(string.Empty),
                        "hello",
                        correlationId,
                        DadIpcJson.Serialize(hello),
                        configuration.TransportSharedSecret),
                    "hello",
                    connection.Cancellation.Token).ConfigureAwait(false);

                var response = await ReadWithTimeoutAsync(
                        connection.Stream,
                        ConnectTimeout,
                        connection.Cancellation.Token)
                    .ConfigureAwait(false);
                if (response == null)
                    throw new DadHubProtocolException("hello-missing", "Dad Coordinator closed before hello acknowledgement.");
                if (response.Kind == DadHubFrameKind.Error)
                    throw new DadHubProtocolException(response.ErrorCode, response.ErrorMessage);

                DadHubProtocol.ValidateFrame(response, configuration.TransportSharedSecret);
                if (response.Kind != DadHubFrameKind.HelloAck)
                    throw new DadHubProtocolException("hello-invalid", "Dad Coordinator did not return hello acknowledgement.");

                var serverHello = DadIpcJson.Deserialize<DadHubHello>(response.PayloadJson)
                                  ?? throw new DadHubProtocolException("hello-invalid", "Dad Coordinator hello payload is invalid.");
                if (serverHello.WorkerSessionId.IsEmpty ||
                    !string.Equals(serverHello.WorkerSessionId.Value, response.SourceWorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(response.TargetWorkerSessionId.Value, presenceService.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(response.CorrelationId, correlationId, StringComparison.Ordinal))
                {
                    throw new DadHubProtocolException("hello-invalid", "Dad Coordinator hello acknowledgement identity or correlation is invalid.");
                }

                serverParticipant = DadHubParticipants.PrepareRemote(serverHello.Participant, DateTime.UtcNow);
                connection.RemoteWorkerSessionId = serverHello.WorkerSessionId;
                connection.Participant = serverParticipant.Clone();
                connection.ObserveRuntimeReadiness(connection.Participant, out _);
                connection.LastHeartbeatUtc = DateTime.UtcNow;
                connection.MarkHandshakeReady();
                clientConnection = connection;
                attempt = 0;
                nextHeartbeatUtc = DateTime.MinValue;
                IsReady = true;
                CurrentTransport.Availability = "Ready";
                CurrentTransport.ConnectionStatus = $"Connected to Dad Coordinator at {FormatEndpoint(host, port)}.";
                CurrentTransport.LastRequestStatus = CurrentTransport.ConnectionStatus;
                CurrentTransport.AuthorityRoutable = true;
                CurrentTransport.LastConnectedUtc = DateTime.UtcNow;
                CurrentTransport.LastInboundFrameUtc = connection.LastFrameReceivedUtc;
                CurrentTransport.NextReconnectUtc = null;
                lastReconnectLogDelaySeconds = -1;
                ClearTransportAuthOrProtocolError();
                RefreshTransportSnapshot();
                log.Information("[dad] Connected to Dad Coordinator {WorkerSessionId} at {Endpoint}.",
                    serverHello.WorkerSessionId,
                    FormatEndpoint(host, port));

                await RunConnectionReaderAsync(connection, isServerSide: false).ConfigureAwait(false);
                CurrentTransport.LastDisconnectReason = "Dad Coordinator closed the connection.";
            }
            catch (DadHubProtocolException ex)
            {
                if (IsAuthOrProtocolException(ex))
                    SetTransportAuthOrProtocolError(ex.Message);
                else
                    SetTransportError($"{ex.Code}: {ex.Message}");
                CurrentTransport.LastDisconnectReason = CurrentTransport.ConnectionStatus;
                log.Warning("[dad] Dad Coordinator connection rejected: {Code}: {Message}", ex.Code, ex.Message);
            }
            catch (TimeoutException)
            {
                SetTransportError("Timed out connecting to Dad Coordinator.");
                CurrentTransport.LastDisconnectReason = CurrentTransport.ConnectionStatus;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                SetTransportError("Dad Coordinator connection was closed; reconnecting.");
                CurrentTransport.LastDisconnectReason = CurrentTransport.ConnectionStatus;
            }
            catch (Exception ex) when (DadBackgroundTaskObserver.IsExpectedShutdownException(ex))
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                SetTransportError($"Dad Coordinator connection ended: {ex.Message}");
                CurrentTransport.LastDisconnectReason = CurrentTransport.ConnectionStatus;
            }
            catch (Exception ex)
            {
                SetTransportError($"Dad Coordinator connection failed: {ex.Message}");
                CurrentTransport.LastDisconnectReason = CurrentTransport.ConnectionStatus;
                log.Debug(ex, "[dad] Dad Coordinator connection failed.");
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                    IsReady = false;
                if (activeConnection != null && ReferenceEquals(clientConnection, activeConnection))
                {
                    clientConnection.Close();
                    clientConnection = null;
                }
                CurrentTransport.AuthorityRoutable = false;
                CurrentTransport.AuthorityWorkerSessionId = new DadWorkerSessionId(string.Empty);
                CurrentTransport.LastDisconnectedUtc = DateTime.UtcNow;
                RefreshTransportSnapshot();
            }

            attempt++;
            if (!DadReconnectPolicy.ShouldContinue(configuration.PluginEnabled, cancellationToken.IsCancellationRequested))
                return;
            var backoff = DadReconnectPolicy.GetBackoff(attempt, MaxReconnectBackoff);
            var backoffSeconds = backoff.TotalSeconds;
            CurrentTransport.ReconnectAttempt = attempt;
            CurrentTransport.NextReconnectUtc = DateTime.UtcNow.AddSeconds(backoffSeconds);
            CurrentTransport.ConnectionStatus = $"Disconnected; reconnecting in {backoffSeconds:F0}s.";
            LogReconnectTransition(attempt, (int)backoffSeconds, CurrentTransport.LastDisconnectReason);
            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void LogReconnectTransition(int attempt, int delaySeconds, string reason)
    {
        var now = DateTime.UtcNow;
        if (delaySeconds == lastReconnectLogDelaySeconds && now - lastReconnectLogUtc < TimeSpan.FromMinutes(1))
            return;

        lastReconnectLogDelaySeconds = delaySeconds;
        lastReconnectLogUtc = now;
        log.Information(
            "[dad] Dad Coordinator reconnect attempt {Attempt} scheduled in {DelaySeconds}s. Last reason: {Reason}",
            attempt,
            delaySeconds,
            string.IsNullOrWhiteSpace(reason) ? "connection unavailable" : reason);
    }

    private async Task RunConnectionReaderAsync(DadHubConnection connection, bool isServerSide)
    {
        while (!connection.Cancellation.IsCancellationRequested)
        {
            var frame = await DadHubProtocol.ReadFrameAsync(connection.Stream, connection.Cancellation.Token)
                .ConfigureAwait(false);
            if (frame == null)
                return;

            DadHubProtocol.ValidateFrame(frame, configuration.TransportSharedSecret);
            connection.MarkFrameReceived(DateTime.UtcNow);
            if (!isServerSide)
                CurrentTransport.LastInboundFrameUtc = connection.LastFrameReceivedUtc;
            var expectedSource = isServerSide
                ? connection.WorkerSessionId
                : connection.RemoteWorkerSessionId;
            if (!expectedSource.IsEmpty &&
                !string.Equals(
                    frame.SourceWorkerSessionId.Value,
                    expectedSource.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new DadHubProtocolException(
                    "session-mismatch",
                    $"Frame source {frame.SourceWorkerSessionId} does not match connected session {expectedSource}.");
            }

            switch (frame.Kind)
            {
                case DadHubFrameKind.Heartbeat:
                    HandleHeartbeat(connection, frame, isServerSide);
                    break;
                case DadHubFrameKind.Notification:
                    HandleNotification(frame, isServerSide);
                    break;
                case DadHubFrameKind.Request:
                    if (connection.TryBeginInboundRequest())
                    {
                        Track(
                            HandleInboundRequestObservedAsync(connection, frame, isServerSide),
                            $"inbound:{frame.MessageType}:{frame.CorrelationId}");
                    }
                    else
                    {
                        Track(
                            SendInboundBusyErrorAsync(connection, frame),
                            $"inbound-busy:{frame.MessageType}:{frame.CorrelationId}");
                    }
                    break;
                case DadHubFrameKind.Response:
                case DadHubFrameKind.Error:
                    if (pendingRequests.TryRemove(frame.CorrelationId, out var pending))
                        pending.TrySetResult(frame);
                    break;
                default:
                    throw new DadHubProtocolException("unexpected-frame", $"Unexpected Dad hub frame kind {frame.Kind}.");
            }
        }
    }

    private async Task HandleInboundRequestObservedAsync(
        DadHubConnection connection,
        DadHubFrame frame,
        bool isServerSide)
    {
        try
        {
            await HandleInboundRequestAsync(connection, frame, isServerSide).ConfigureAwait(false);
        }
        finally
        {
            connection.EndInboundRequest();
        }
    }

    private async Task SendInboundBusyErrorAsync(DadHubConnection connection, DadHubFrame request)
    {
        var response = DadHubProtocol.CreateError(
            presenceService.WorkerSessionId,
            request.SourceWorkerSessionId,
            request.CorrelationId,
            "inbound-busy",
            "Dad hub inbound request limit reached; retry this operation.",
            configuration.TransportSharedSecret);
        await SendFrameAsync(connection, response, request.MessageType, connection.Cancellation.Token).ConfigureAwait(false);
    }

    private void HandleHeartbeat(DadHubConnection connection, DadHubFrame frame, bool isServerSide)
    {
        var heartbeat = DadIpcJson.Deserialize<DadHubHeartbeat>(frame.PayloadJson);
        if (heartbeat == null)
            return;

        QueueTransportEvent(
            () =>
            {
                var now = DateTime.UtcNow;
                connection.LastHeartbeatUtc = now;
                connection.Participant = DadHubParticipants.PrepareRemote(heartbeat.Participant, now);
                var readinessChanged = connection.ObserveRuntimeReadiness(connection.Participant, out var readinessRevision);
                if (readinessChanged)
                {
                    var workerSessionId = connection.RemoteWorkerSessionId.IsEmpty
                        ? connection.Participant.WorkerSessionId
                        : connection.RemoteWorkerSessionId;
                    RecordRuntimeReadinessChange(workerSessionId, readinessRevision);
                }
                if (isServerSide)
                {
                    disconnectedParticipants.TryRemove(connection.WorkerSessionId.Value, out _);
                    MarkHubRosterDirty(
                        readinessChanged
                            ? $"Client Dad {connection.WorkerSessionId} runtime readiness changed."
                            : $"Client Dad {connection.WorkerSessionId} heartbeat.",
                        fast: readinessChanged);
                }
                else
                {
                    serverParticipant = connection.Participant.Clone();
                }
            },
            "heartbeat");
    }

    private void HandleNotification(DadHubFrame frame, bool isServerSide)
    {
        if (isServerSide)
            return;

        if (string.Equals(frame.MessageType, MessageStopAll, StringComparison.Ordinal))
        {
            var status = DadIpcJson.Deserialize<DadStopAllStatus>(frame.PayloadJson);
            if (status != null)
                QueueTransportEvent(() => RecordStopAllStatus(status), "stop-all-status");
            return;
        }

        if (!string.Equals(frame.MessageType, MessageHubRosterPublish, StringComparison.Ordinal))
            return;

        var publish = DadIpcJson.Deserialize<DadHubRosterPublish>(frame.PayloadJson);
        if (publish == null)
            return;

        ApplyHubRosterPublish(publish);
    }

    private async Task HandleInboundRequestAsync(
        DadHubConnection origin,
        DadHubFrame request,
        bool isServerSide)
    {
        DadHubFrame response;
        try
        {
            if (isServerSide && string.Equals(request.MessageType, MessageHubRosterPublishRequest, StringComparison.Ordinal))
            {
                response = HandleHubRosterPublishRequest(origin, request);
            }
            else if (isServerSide &&
                !request.TargetWorkerSessionId.IsEmpty &&
                !string.Equals(
                    request.TargetWorkerSessionId.Value,
                    presenceService.WorkerSessionId.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                response = await ForwardRequestAsync(request, origin.Cancellation.Token).ConfigureAwait(false);
            }
            else if (TryServeCachedRosterCatalog(request, out var cachedRosterJson))
            {
                // B7: serve the peer's catalog pull from the off-thread cache instead of marshaling a
                // synchronous XADB fetch + rebuild onto the Dalamud framework thread (the residual hitch).
                response = DadHubProtocol.CreateFrame(
                    DadHubFrameKind.Response,
                    presenceService.WorkerSessionId,
                    request.SourceWorkerSessionId,
                    request.MessageType,
                    request.CorrelationId,
                    cachedRosterJson,
                    configuration.TransportSharedSecret);
            }
            else
            {
                var responseJson = await Plugin.Framework
                    .RunOnFrameworkThread(() => DispatchRequest(request.MessageType, request.PayloadJson))
                    .ConfigureAwait(false);
                response = DadHubProtocol.CreateFrame(
                    DadHubFrameKind.Response,
                    presenceService.WorkerSessionId,
                    request.SourceWorkerSessionId,
                    request.MessageType,
                    request.CorrelationId,
                    responseJson,
                    configuration.TransportSharedSecret);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Dad hub request {MessageType} failed.", request.MessageType);
            response = DadHubProtocol.CreateError(
                presenceService.WorkerSessionId,
                request.SourceWorkerSessionId,
                request.CorrelationId,
                "request-failed",
                ex.Message,
                configuration.TransportSharedSecret);
        }

        await SendFrameAsync(origin, response, request.MessageType, origin.Cancellation.Token).ConfigureAwait(false);
    }

    private DadHubFrame HandleHubRosterPublishRequest(DadHubConnection origin, DadHubFrame request)
    {
        var heartbeat = DadIpcJson.Deserialize<DadHubHeartbeat>(request.PayloadJson);
        if (heartbeat != null)
        {
            var now = DateTime.UtcNow;
            origin.LastHeartbeatUtc = now;
            origin.Participant = DadHubParticipants.PrepareRemote(heartbeat.Participant, now);
            disconnectedParticipants.TryRemove(origin.WorkerSessionId.Value, out _);
        }

        var reason = $"Client Dad {origin.WorkerSessionId} requested roster publish.";
        MarkHubRosterDirty(reason, fast: true);
        var publish = CreateHubRosterPublish(reason)
                      ?? throw new DadHubProtocolException(
                          "roster-publish-disabled",
                          "Dad Coordinator roster publish is disabled in local-only mode.");
        return DadHubProtocol.CreateFrame(
            DadHubFrameKind.Response,
            presenceService.WorkerSessionId,
            request.SourceWorkerSessionId,
            request.MessageType,
            request.CorrelationId,
            DadIpcJson.Serialize(publish),
            configuration.TransportSharedSecret);
    }

    private async Task<DadHubFrame> ForwardRequestAsync(
        DadHubFrame request,
        CancellationToken cancellationToken)
    {
        if (!serverSessions.TryGet(request.TargetWorkerSessionId, out var target) || target is not { IsRoutable: true })
        {
            return DadHubProtocol.CreateError(
                presenceService.WorkerSessionId,
                request.SourceWorkerSessionId,
                request.CorrelationId,
                "worker-offline",
                $"Worker session {request.TargetWorkerSessionId} is not connected.",
                configuration.TransportSharedSecret);
        }

        var forwarded = await SendRequestFrameAsync(
                target,
                request.TargetWorkerSessionId,
                request.MessageType,
                request.PayloadJson,
                cancellationToken)
            .ConfigureAwait(false);
        forwarded.CorrelationId = request.CorrelationId;
        forwarded.SourceWorkerSessionId = presenceService.WorkerSessionId;
        forwarded.TargetWorkerSessionId = request.SourceWorkerSessionId;
        forwarded.Auth = DadHubProtocol.ComputeAuth(forwarded, configuration.TransportSharedSecret);
        return forwarded;
    }

    private string DispatchRequest(string messageType, string payloadJson)
    {
        RefreshLocalMutationState();

        return messageType switch
        {
            MessageSnapshotRequest => DadIpcJson.Serialize(HandleSnapshotRequest(payloadJson)),
            MessageWakeRequest => DadIpcJson.Serialize(HandleWakeRequest(payloadJson)),
            MessageWakeTakeoverRequest => DadIpcJson.Serialize(HandleWakeTakeoverRequest(payloadJson)),
            MessageRouletteRewardProbe => DadIpcJson.Serialize(HandleRouletteRewardProbe(payloadJson)),
            MessageClaimRequest => DadIpcJson.Serialize(HandleClaimRequest(payloadJson)),
            MessageAssemblyInstruction => DadIpcJson.Serialize(HandleAssemblyInstruction(payloadJson)),
            MessageCharacterLoadCommand => DadIpcJson.Serialize(HandleCharacterLoadCommand(payloadJson)),
            MessageCancelRun => DadIpcJson.Serialize(HandleCancelRun(payloadJson)),
            MessageCancelCommand => DadIpcJson.Serialize(HandleCancelCommand(payloadJson)),
            MessageStatusQuery => DadIpcJson.Serialize(HandleStatusQuery()),
            MessageStartRun => DadIpcJson.Serialize(HandleStartRun(payloadJson)),
            MessageRosterCatalogRequest => DadIpcJson.Serialize(HandleRosterCatalogRequest(payloadJson)),
            MessageRosterAggregateCatalogRequest => DadIpcJson.Serialize(HandleAggregateRosterCatalogRequest(payloadJson)),
            MessageRosterRefreshCommand => DadIpcJson.Serialize(HandleRosterRefreshCommand(payloadJson)),
            MessageProfileCatalogRequest => DadIpcJson.Serialize(HandleProfileCatalogRequest(payloadJson)),
            MessageProfileAggregateCatalogRequest => DadIpcJson.Serialize(HandleAggregateProfileCatalogRequest(payloadJson)),
            MessageProfileUpdateCommand => DadIpcJson.Serialize(HandleProfileUpdateCommand(payloadJson)),
            MessageWorkerExecutionCommand => DadIpcJson.Serialize(HandleWorkerExecutionCommand(payloadJson)),
            MessageWorkerExecutionStatus => DadIpcJson.Serialize(HandleWorkerExecutionStatus()),
            MessageWorkerExecutionCancel => DadIpcJson.Serialize(HandleWorkerExecutionCancel(payloadJson)),
            MessageStopAll => DadIpcJson.Serialize(HandleStopAllRequest(payloadJson)),
            _ => throw new InvalidOperationException($"Unsupported Dad hub message type '{messageType}'."),
        };
    }

    private DadStopAllStatus HandleStopAllRequest(string payloadJson)
    {
        var request = DadIpcJson.Deserialize<DadStopAllRequest>(payloadJson) ?? new DadStopAllRequest();
        NormalizeStopAllRequest(request);
        if (configuration.RunAsServerDad)
            return BeginCoordinatorStopAll(request);

        var local = InvokeLocalStopAll(request);
        var response = new DadStopAllStatus
        {
            OperationId = request.OperationId,
            RequestedByWorkerSessionId = request.RequestedByWorkerSessionId,
            SubmittedAtUtc = request.RequestedAtUtc,
            UpdatedAtUtc = DateTime.UtcNow,
            Partial = local.Partial,
            Summary = local.Summary,
            LocalResult = local,
        };
        DadStopAllStatusRules.FinalizeFromWorkers(response, DateTime.UtcNow);
        RecordStopAllStatus(response, preserveCoordinatorMatrix: true);
        return response;
    }

    private DadRouletteRewardProbeResultDto HandleRouletteRewardProbe(string payloadJson)
    {
        var request = DadIpcJson.Deserialize<DadRouletteRewardProbeRequestDto>(payloadJson)
                      ?? new DadRouletteRewardProbeRequestDto();
        if (!remoteMutationsAllowed)
        {
            return DadRouletteRewardProbeResultDto.FromRequest(
                request,
                DadRouletteRewardProbeOutcome.Unknown,
                BuildRemoteMutationRejectedReason("roulette reward probe"),
                DateTime.UtcNow);
        }

        return rouletteRewardProbeService.Handle(request);
    }

    private void RefreshLocalMutationState()
    {
        localPluginEnabled = configuration.PluginEnabled;
        localOnlyModeEnabled = configuration.LocalOnlyModeEnabled;
        remoteMutationsAllowed = localPluginEnabled && !localOnlyModeEnabled;
    }

    private DadPeerSnapshotResponse HandleSnapshotRequest(string payloadJson)
    {
        var request = DadIpcJson.Deserialize<DadPeerSnapshotRequest>(payloadJson) ?? new DadPeerSnapshotRequest();
        var snapshot = BuildLocalTransportSnapshot();
        return new DadPeerSnapshotResponse
        {
            RequestId = request.RequestId,
            RespondedAtUtc = DateTime.UtcNow,
            ClientInstanceId = presenceService.ClientInstanceId,
            ProcessId = Environment.ProcessId,
            Character = snapshot.Character.Clone(),
            Participant = snapshot,
            XadbReady = snapshot.Character.XadbReady,
            Warnings = remoteMutationsAllowed ? [] : [BuildLocalUnavailableReason()],
        };
    }

    private DadParticipantReadyDto HandleWakeRequest(string payloadJson)
    {
        var request = DadIpcJson.Deserialize<DadWakeRequestDto>(payloadJson) ?? new DadWakeRequestDto();
        return remoteMutationsAllowed
            ? presenceService.HandleWakeRequest(request)
            : BuildRejectedWakeResponse(request, BuildRemoteMutationRejectedReason("remote wake request"));
    }

    private DadWakeTakeoverResultDto HandleWakeTakeoverRequest(string payloadJson)
    {
        var request = DadIpcJson.Deserialize<DadWakeTakeoverRequestDto>(payloadJson)
                      ?? new DadWakeTakeoverRequestDto();
        return wakeTakeoverService.Handle(request);
    }

    private DadClaimDecisionDto HandleClaimRequest(string payloadJson)
    {
        var request = DadIpcJson.Deserialize<DadClaimRequestDto>(payloadJson) ?? new DadClaimRequestDto();
        if (!remoteMutationsAllowed)
            return BuildRejectedClaimDecision(request, BuildRemoteMutationRejectedReason("remote claim request"));

        var decision = claimService.TryClaimLocal(request, presenceService.BuildSnapshotCopy());
        presenceService.ApplyClaimState(
            request.RunId,
            decision.ClaimState,
            decision.LeaseState,
            decision.Lease,
            decision.Reason);
        return decision;
    }

    private DadRunStepResultDto HandleAssemblyInstruction(string payloadJson)
    {
        var instruction = DadIpcJson.Deserialize<DadAssemblyInstructionDto>(payloadJson)
                          ?? new DadAssemblyInstructionDto();
        return remoteMutationsAllowed
            ? presenceService.HandleAssemblyInstruction(instruction)
            : BuildRejectedAssemblyResult(
                instruction,
                BuildRemoteMutationRejectedReason("remote assembly instruction"));
    }

    private DadCharacterLoadResultDto HandleCharacterLoadCommand(string payloadJson)
    {
        var command = DadIpcJson.Deserialize<DadCharacterLoadCommandDto>(payloadJson)
                      ?? new DadCharacterLoadCommandDto();
        if (!remoteMutationsAllowed)
        {
            return new DadCharacterLoadResultDto
            {
                CommandId = command.CommandId,
                Accepted = false,
                DryRun = command.DryRun,
                Summary = BuildRemoteMutationRejectedReason("remote character-load command"),
                Snapshot = BuildLocalTransportSnapshot(),
            };
        }

        if (command.DryRun)
        {
            return new DadCharacterLoadResultDto
            {
                CommandId = command.CommandId,
                Accepted = true,
                DryRun = true,
                Summary = $"Dry-run character-load command accepted: {command.Command}",
                Snapshot = presenceService.BuildSnapshotCopy(),
            };
        }

        if (!configuration.AllowRemoteCommandExecution)
        {
            return new DadCharacterLoadResultDto
            {
                CommandId = command.CommandId,
                Summary = "Remote command execution is disabled.",
                Snapshot = presenceService.BuildSnapshotCopy(),
            };
        }

        var accepted = !string.IsNullOrWhiteSpace(command.Command) &&
                       Plugin.CommandManager.ProcessCommand(command.Command);
        return new DadCharacterLoadResultDto
        {
            CommandId = command.CommandId,
            Accepted = accepted,
            Summary = accepted
                ? $"Sent character-load command for {command.CharacterKey}: {command.Command}"
                : $"Character-load command rejected: {command.Command}",
            Snapshot = presenceService.BuildSnapshotCopy(),
        };
    }

    private DadCancelAckDto HandleCancelRun(string payloadJson)
    {
        var command = DadIpcJson.Deserialize<DadCancelCommandDto>(payloadJson) ?? new DadCancelCommandDto();
        if (!remoteMutationsAllowed)
            return BuildRejectedCancelAck(command, BuildRemoteMutationRejectedReason("remote cancel broadcast"));

        claimService.ReleaseClaims(command.RunId);
        return presenceService.HandleCancelRun(command);
    }

    private DadRunResult HandleCancelCommand(string payloadJson)
    {
        var command = DadIpcJson.Deserialize<DadCancelCommandDto>(payloadJson) ?? new DadCancelCommandDto();
        if (!remoteMutationsAllowed)
            return DadRunResult.Rejected(null, BuildRemoteMutationRejectedReason("remote cancel command"));

        return cancelRunHandler?.Invoke(command)
               ?? DadRunResult.Rejected(null, "Dad Coordinator cancel handler unavailable.");
    }

    private DadRunResult HandleStatusQuery()
    {
        var result = statusProvider?.Invoke()
                     ?? DadRunResult.Rejected(null, "Dad Coordinator status unavailable.");
        if (remoteMutationsAllowed)
            return result;

        var unavailable = result.Clone();
        var reason = BuildLocalUnavailableReason();
        unavailable.LocalOnlyEnabled = localOnlyModeEnabled;
        unavailable.BlockedReason = reason;
        unavailable.Summary = reason;
        return unavailable;
    }

    private DadRunResult HandleStartRun(string payloadJson)
    {
        var request = DadIpcJson.Deserialize<DadRunRequest>(payloadJson) ?? new DadRunRequest();
        if (!configuration.RunAsServerDad)
            return DadRunResult.Rejected(request, "Only Dad Coordinator accepts remote run starts.");
        if (!remoteMutationsAllowed)
            return DadRunResult.Rejected(request, BuildRemoteMutationRejectedReason("remote start command"));

        return startRunHandler?.Invoke(request)
               ?? DadRunResult.Rejected(request, "Dad Coordinator start handler unavailable.");
    }

    private DadPeerRosterCatalogResponse HandleRosterCatalogRequest(string payloadJson)
    {
        var request = DadIpcJson.Deserialize<DadRosterRefreshPlan>(payloadJson) ?? new DadRosterRefreshPlan();
        return BuildLocalRosterCatalogResponse(request);
    }

    private DadAggregateRosterCatalogResponse HandleAggregateRosterCatalogRequest(string payloadJson)
    {
        var request = DadIpcJson.Deserialize<DadAggregateRosterCatalogRequest>(payloadJson)
                      ?? new DadAggregateRosterCatalogRequest();
        request.Plan ??= new DadRosterRefreshPlan();
        request.Plan.PlanId = string.IsNullOrWhiteSpace(request.Plan.PlanId)
            ? request.RequestId
            : request.Plan.PlanId;
        request.RequestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? request.Plan.PlanId
            : request.RequestId;
        return BuildServerRosterAggregate(
            request.Plan,
            request.RequestingWorkerSessionId,
            request.IncludeRequester);
    }

    private DadPeerRosterCatalogResponse BuildLocalRosterCatalogResponse(DadRosterRefreshPlan request)
    {
        var catalog = request.LiveConnectedOnly
            ? BuildLocalLiveConnectedRosterCatalog()
            : rosterCatalogProvider?.Invoke() ?? new DadAccountRosterCatalog
            {
                Summary = "Dad roster catalog provider unavailable.",
                Warnings = ["Dad roster catalog provider unavailable."],
            };
        catalog.SourceClientInstanceId = presenceService.ClientInstanceId;
        catalog.SourceWorkerSessionId = presenceService.WorkerSessionId;
        return new DadPeerRosterCatalogResponse
        {
            RequestId = request.PlanId,
            RespondedAtUtc = DateTime.UtcNow,
            ClientInstanceId = presenceService.ClientInstanceId,
            WorkerSessionId = presenceService.WorkerSessionId,
            Catalog = catalog,
            Warnings = remoteMutationsAllowed ? [] : [BuildLocalUnavailableReason()],
        };
    }

    // B2/B7: return the cached local roster-catalog response WITHOUT building (the build is heavy XADB work
    // and the publish projection can run on the inbound socket thread via HandleHubRosterPublishRequest).
    // The cache is rebuilt only on the framework-thread cadence (RebuildLocalRosterCatalogCacheIfDue), which
    // runs immediately before each publish flush, so this is fresh on the normal publish path.
    private DadPeerRosterCatalogResponse? GetCachedLocalRosterCatalogResponse()
        => cachedLocalRosterCatalog?.Response;

    // B7: rebuild the local roster-catalog cache on a cadence (framework thread) when a peer could actually
    // pull this node, so the XADB fetch happens off the inbound request path. No-op when idle.
    private void RebuildLocalRosterCatalogCacheIfDue(DateTime nowUtc)
    {
        if (nowUtc < nextLocalRosterCatalogRebuildUtc || rosterCatalogProvider == null)
            return;

        var hasPeers = configuration.RunAsServerDad
            ? serverSessions.Snapshot().Any(static connection => connection.IsRoutable)
            : clientConnection is { IsRoutable: true };
        if (!hasPeers)
            return;

        nextLocalRosterCatalogRebuildUtc = nowUtc + LocalRosterCatalogRebuildInterval;
        try
        {
            var response = BuildLocalRosterCatalogResponse(new DadRosterRefreshPlan
            {
                IncludeHidden = true,
                IncludeIgnored = true,
            });
            cachedLocalRosterCatalog = new CachedLocalRosterCatalog { Response = response, BuiltAtUtc = nowUtc };
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[dad] Failed to rebuild local roster catalog cache.");
        }
    }

    // B5/B7: force the next framework tick to rebuild the cache so a level-up is reflected promptly.
    private void InvalidateLocalRosterCatalogCache()
    {
        cachedLocalRosterCatalog = null;
        nextLocalRosterCatalogRebuildUtc = DateTime.MinValue;
    }

    // B7: serve a standard roster-catalog pull from the off-thread cache (no synchronous XADB fetch + rebuild
    // on the game thread). Returns false (so the caller falls back to the live framework-thread build) when
    // no fresh cache exists or the request needs live-connected data.
    private bool TryServeCachedRosterCatalog(DadHubFrame request, out string responseJson)
    {
        responseJson = string.Empty;
        if (!string.Equals(request.MessageType, MessageRosterCatalogRequest, StringComparison.Ordinal))
            return false;

        var cached = cachedLocalRosterCatalog;
        if (cached == null || DateTime.UtcNow - cached.BuiltAtUtc > LocalRosterCatalogServeTtl)
            return false;

        var plan = DadIpcJson.Deserialize<DadRosterRefreshPlan>(request.PayloadJson) ?? new DadRosterRefreshPlan();
        if (plan.LiveConnectedOnly)
            return false;

        responseJson = DadIpcJson.Serialize(new DadPeerRosterCatalogResponse
        {
            RequestId = string.IsNullOrWhiteSpace(plan.PlanId) ? cached.Response.RequestId : plan.PlanId,
            RespondedAtUtc = cached.Response.RespondedAtUtc,
            ClientInstanceId = cached.Response.ClientInstanceId,
            WorkerSessionId = cached.Response.WorkerSessionId,
            Catalog = cached.Response.Catalog,
            Warnings = cached.Response.Warnings,
        });
        return true;
    }

    private DadAccountRosterCatalog BuildLocalLiveConnectedRosterCatalog()
    {
        var local = BuildLocalTransportSnapshot();
        local.IsLocalClient = true;
        local.IsAuthority = configuration.RunAsServerDad;
        local.WorkerRole = configuration.RunAsServerDad
            ? DadWorkerRole.ServerDad
            : DadWorkerRole.ClientDad;

        return DadRosterTransportCatalogRuntime.BuildLiveConnectedCatalog(new DadPeerTransportSnapshot
        {
            LocalClientInstanceId = presenceService.ClientInstanceId,
            LocalWorkerSessionId = presenceService.WorkerSessionId,
            KnownParticipants = [local],
        });
    }

    private DadRosterRefreshResultDto HandleRosterRefreshCommand(string payloadJson)
    {
        var command = DadIpcJson.Deserialize<DadRosterRefreshCommandDto>(payloadJson)
                      ?? new DadRosterRefreshCommandDto();
        if (!remoteMutationsAllowed)
        {
            return new DadRosterRefreshResultDto
            {
                CommandId = command.CommandId,
                AccountKey = command.AccountKey,
                CharacterKey = command.CharacterKey,
                ContentId = command.ContentId,
                Summary = BuildRemoteMutationRejectedReason("remote roster-refresh command"),
                Snapshot = BuildLocalTransportSnapshot(),
            };
        }

        return rosterRefreshHandler?.Invoke(command) ?? new DadRosterRefreshResultDto
        {
            CommandId = command.CommandId,
            AccountKey = command.AccountKey,
            CharacterKey = command.CharacterKey,
            ContentId = command.ContentId,
            Summary = "Dad roster refresh handler unavailable.",
            Snapshot = BuildLocalTransportSnapshot(),
        };
    }

    private DadProfileCatalogResponse HandleProfileCatalogRequest(string payloadJson)
    {
        var requestId = DadIpcJson.Deserialize<string>(payloadJson) ?? string.Empty;
        return BuildLocalProfileCatalogResponse(requestId);
    }

    private DadAggregateProfileCatalogResponse HandleAggregateProfileCatalogRequest(string payloadJson)
    {
        var request = DadIpcJson.Deserialize<DadAggregateProfileCatalogRequest>(payloadJson)
                      ?? new DadAggregateProfileCatalogRequest();
        request.RequestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? Guid.NewGuid().ToString("N")
            : request.RequestId;
        return BuildServerProfileAggregate(
            request.RequestId,
            request.RequestingWorkerSessionId,
            request.IncludeRequester);
    }

    private DadProfileCatalogResponse BuildLocalProfileCatalogResponse(string requestId)
    {
        var catalog = profileCatalogProvider?.Invoke() ?? new DadProfileCatalog { ReadOnly = true };
        catalog.OwnerClientInstanceId = presenceService.ClientInstanceId;
        catalog.OwnerWorkerSessionId = presenceService.WorkerSessionId;
        catalog.OwnerEndpoint = string.Empty;
        catalog.OwnerOnline = remoteMutationsAllowed;
        catalog.ReadOnly = !remoteMutationsAllowed;
        return new DadProfileCatalogResponse
        {
            RequestId = requestId,
            Success = profileCatalogProvider != null,
            Summary = profileCatalogProvider == null
                ? "Dad profile catalog provider unavailable."
                : $"Returned {catalog.Accounts.Count} owned account profile(s).",
            Catalog = catalog,
        };
    }

    private DadProfileUpdateAck HandleProfileUpdateCommand(string payloadJson)
    {
        var request = DadIpcJson.Deserialize<DadProfileUpdateRequest>(payloadJson)
                      ?? new DadProfileUpdateRequest();
        if (!remoteMutationsAllowed)
        {
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                Summary = BuildRemoteMutationRejectedReason("remote profile update"),
            };
        }

        return profileUpdateHandler?.Invoke(request) ?? new DadProfileUpdateAck
        {
            RequestId = request.RequestId,
            Summary = "Dad profile update handler unavailable.",
        };
    }

    private DadWorkerExecutionAck HandleWorkerExecutionCommand(string payloadJson)
    {
        var command = DadIpcJson.Deserialize<DadWorkerExecutionCommand>(payloadJson)
                      ?? new DadWorkerExecutionCommand();
        if (!remoteMutationsAllowed)
        {
            return new DadWorkerExecutionAck
            {
                CommandId = command.CommandId,
                RunId = command.RunId,
                WorkerSessionId = presenceService.WorkerSessionId,
                Summary = BuildRemoteMutationRejectedReason("worker execution command"),
            };
        }

        return workerExecutionHandler?.Invoke(command) ?? new DadWorkerExecutionAck
        {
            CommandId = command.CommandId,
            RunId = command.RunId,
            WorkerSessionId = presenceService.WorkerSessionId,
            Summary = "Dad worker execution handler unavailable.",
        };
    }

    private DadWorkerExecutionStatus HandleWorkerExecutionStatus()
        => workerStatusProvider?.Invoke() ?? new DadWorkerExecutionStatus
        {
            WorkerSessionId = presenceService.WorkerSessionId,
            Summary = "Dad worker execution status unavailable.",
        };

    private DadWorkerExecutionAck HandleWorkerExecutionCancel(string payloadJson)
    {
        var command = DadIpcJson.Deserialize<DadWorkerExecutionCancel>(payloadJson)
                      ?? new DadWorkerExecutionCancel();
        if (!remoteMutationsAllowed)
        {
            return new DadWorkerExecutionAck
            {
                RunId = command.RunId,
                WorkerSessionId = presenceService.WorkerSessionId,
                Summary = BuildLocalUnavailableReason(),
            };
        }

        return workerCancelHandler?.Invoke(command) ?? new DadWorkerExecutionAck
        {
            RunId = command.RunId,
            WorkerSessionId = presenceService.WorkerSessionId,
            Summary = "Dad worker execution cancel handler unavailable.",
        };
    }

    private TResponse? TryRequest<TRequest, TResponse>(
        DadWorkerSessionId targetWorkerSessionId,
        string messageType,
        TRequest request,
        string operationKey)
    {
        var completed = TryTakeCompleted<TResponse>(operationKey);
        if (completed != null)
            return completed;

        if (operations.ContainsKey(operationKey) || !CanQueueOperation(targetWorkerSessionId))
            return default;

        QueueOperation<TRequest, TResponse>(
            operationKey,
            targetWorkerSessionId,
            messageType,
            request,
            completed: null);
        return default;
    }

    private TResponse? TryTakeCompleted<TResponse>(string operationKey)
    {
        if (!completedOperations.TryRemove(operationKey, out var completed))
            return default;

        return DadIpcJson.Deserialize<TResponse>(completed.PayloadJson);
    }

    private DadAggregateRosterCatalogResponse BuildClientRosterAggregate(DadRosterRefreshPlan plan)
    {
        if (!localOnlyModeEnabled)
        {
            RequestHubRosterPublish(BuildLocalTransportSnapshot());
            RefreshTransportSnapshot();
        }

        var aggregate = CreateRosterAggregate(plan.PlanId);
        aggregate.Responses.Add(BuildLocalRosterCatalogResponse(plan));

        var target = ResolveAuthorityWorkerSessionId();
        if (target.IsEmpty)
        {
            AddWarning(aggregate.Warnings, "Dad Coordinator is not connected; roster catalog refresh returned local catalog only.");
            FinalizeRosterAggregate(aggregate, expectedCatalogCount: 1);
            return aggregate;
        }

        QueueCoordinatorRosterAggregateRefresh(target, plan);
        AddCachedRosterResponses(aggregate, skipWorkerId: presenceService.WorkerSessionId.Value);

        var expectedRemoteCount = Math.Max(1, EstimateExpectedRemoteRosterCatalogCount(skipWorkerId: presenceService.WorkerSessionId.Value));
        var pendingCount = Math.Max(0, expectedRemoteCount - (aggregate.Responses.Count - 1));
        if (pendingCount > 0)
        {
            aggregate.PendingCatalogCount += pendingCount;
            AddWarning(aggregate.Warnings, "Dad Coordinator roster catalog refresh is queued; showing cached connected-Dad catalog data.");
        }

        FinalizeRosterAggregate(aggregate, expectedCatalogCount: 1 + expectedRemoteCount);
        return aggregate;
    }

    private DadAggregateRosterCatalogResponse BuildServerRosterAggregate(
        DadRosterRefreshPlan plan,
        DadWorkerSessionId requestingWorkerSessionId,
        bool includeRequester)
    {
        MarkHubRosterDirty("Dad Coordinator roster catalog aggregate requested.", fast: false);
        var aggregate = CreateRosterAggregate(plan.PlanId);
        var skipRequester = !requestingWorkerSessionId.IsEmpty && !includeRequester
            ? requestingWorkerSessionId.Value
            : string.Empty;

        if (!string.Equals(skipRequester, presenceService.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase))
            aggregate.Responses.Add(BuildLocalRosterCatalogResponse(plan));

        var targets = serverSessions.Snapshot()
            .Where(static connection => connection.IsRoutable)
            .Where(connection => string.IsNullOrWhiteSpace(skipRequester) ||
                                 !string.Equals(connection.WorkerSessionId.Value, skipRequester, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var connection in targets)
            QueueRosterCatalogRefresh(connection, force: plan.ForcePeerRefresh, CloneRosterRefreshPlan(plan));

        AddCachedRosterResponses(
            aggregate,
            targets.Select(static connection => connection.WorkerSessionId.Value).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var targetResponses = aggregate.Responses.Count -
                              (string.Equals(skipRequester, presenceService.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        var pendingCount = Math.Max(0, targets.Count - targetResponses);
        if (pendingCount > 0)
        {
            aggregate.PendingCatalogCount += pendingCount;
            AddWarning(aggregate.Warnings, $"{pendingCount} connected Dad roster catalog refresh(es) are pending; showing cached catalog data.");
        }

        MarkHubRosterDirty("Dad Coordinator roster catalog aggregate queued.", fast: false);
        FinalizeRosterAggregate(
            aggregate,
            (string.Equals(skipRequester, presenceService.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) ? 0 : 1) + targets.Count);
        return aggregate;
    }

    private DadAggregateProfileCatalogResponse BuildClientProfileAggregate(string requestId)
    {
        var aggregate = CreateProfileAggregate(requestId);
        aggregate.Responses.Add(BuildLocalProfileCatalogResponse(requestId));

        var target = ResolveAuthorityWorkerSessionId();
        if (target.IsEmpty)
        {
            AddWarning(aggregate.Warnings, "Dad Coordinator is not connected; profile catalog refresh returned local catalog only.");
            FinalizeProfileAggregate(aggregate, expectedCatalogCount: 1);
            return aggregate;
        }

        QueueCoordinatorProfileAggregateRefresh(target, requestId);
        AddCachedProfileResponses(aggregate, skipWorkerId: presenceService.WorkerSessionId.Value);

        var expectedRemoteCount = Math.Max(1, EstimateExpectedRemoteProfileCatalogCount(skipWorkerId: presenceService.WorkerSessionId.Value));
        var pendingCount = Math.Max(0, expectedRemoteCount - (aggregate.Responses.Count - 1));
        if (pendingCount > 0)
        {
            aggregate.PendingCatalogCount += pendingCount;
            AddWarning(aggregate.Warnings, "Dad Coordinator profile catalog refresh is queued; showing cached connected-Dad profile data.");
        }

        FinalizeProfileAggregate(aggregate, expectedCatalogCount: 1 + expectedRemoteCount);
        return aggregate;
    }

    private DadAggregateProfileCatalogResponse BuildServerProfileAggregate(
        string requestId,
        DadWorkerSessionId requestingWorkerSessionId,
        bool includeRequester)
    {
        var aggregate = CreateProfileAggregate(requestId);
        var skipRequester = !requestingWorkerSessionId.IsEmpty && !includeRequester
            ? requestingWorkerSessionId.Value
            : string.Empty;

        if (!string.Equals(skipRequester, presenceService.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase))
            aggregate.Responses.Add(BuildLocalProfileCatalogResponse(requestId));

        var targets = serverSessions.Snapshot()
            .Where(static connection => connection.IsRoutable)
            .Where(connection => string.IsNullOrWhiteSpace(skipRequester) ||
                                 !string.Equals(connection.WorkerSessionId.Value, skipRequester, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var connection in targets)
            QueueProfileCatalogRefresh(connection, force: true, requestId);

        AddCachedProfileResponses(
            aggregate,
            targets.Select(static connection => connection.WorkerSessionId.Value).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var targetResponses = aggregate.Responses.Count -
                              (string.Equals(skipRequester, presenceService.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        var pendingCount = Math.Max(0, targets.Count - targetResponses);
        if (pendingCount > 0)
        {
            aggregate.PendingCatalogCount += pendingCount;
            AddWarning(aggregate.Warnings, $"{pendingCount} connected Dad profile catalog refresh(es) are pending; showing cached profile data.");
        }

        FinalizeProfileAggregate(
            aggregate,
            (string.Equals(skipRequester, presenceService.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) ? 0 : 1) + targets.Count);
        return aggregate;
    }

    private void QueueCoordinatorRosterAggregateRefresh(
        DadWorkerSessionId target,
        DadRosterRefreshPlan plan)
    {
        var key = $"catalog-roster-aggregate:{target.Value}";
        if (operations.ContainsKey(key))
            return;

        var request = new DadAggregateRosterCatalogRequest
        {
            RequestId = plan.PlanId,
            RequestingWorkerSessionId = presenceService.WorkerSessionId,
            IncludeRequester = false,
            Plan = CloneRosterRefreshPlan(plan),
        };
        QueueOperation<DadAggregateRosterCatalogRequest, DadAggregateRosterCatalogResponse>(
            key,
            target,
            MessageRosterAggregateCatalogRequest,
            request,
            response =>
            {
                CacheRosterAggregateResponse(response, excludeLocal: true);
                // B1: signal "fresh peer catalog landed" so the client roster UI re-merges from cache (no second click).
                Interlocked.Increment(ref CurrentTransport.RosterCatalogCacheRevision);
                CurrentTransport.LastRequestStatus = response.Summary;
            });
    }

    private void QueueCoordinatorProfileAggregateRefresh(
        DadWorkerSessionId target,
        string requestId)
    {
        var key = $"catalog-profile-aggregate:{target.Value}";
        if (operations.ContainsKey(key))
            return;

        QueueOperation<DadAggregateProfileCatalogRequest, DadAggregateProfileCatalogResponse>(
            key,
            target,
            MessageProfileAggregateCatalogRequest,
            new DadAggregateProfileCatalogRequest
            {
                RequestId = requestId,
                RequestingWorkerSessionId = presenceService.WorkerSessionId,
                IncludeRequester = false,
            },
            response =>
            {
                CacheProfileAggregateResponse(response, excludeLocal: true);
                CurrentTransport.LastRequestStatus = response.Summary;
            });
    }

    private void CacheRosterAggregateResponse(DadAggregateRosterCatalogResponse response, bool excludeLocal)
    {
        var cacheTarget = CreateRosterAggregate(response.RequestId);
        MergeRosterAggregate(cacheTarget, response, excludeLocal);
    }

    private void CacheProfileAggregateResponse(DadAggregateProfileCatalogResponse response, bool excludeLocal)
    {
        var cacheTarget = CreateProfileAggregate(response.RequestId);
        MergeProfileAggregate(cacheTarget, response, excludeLocal);
    }

    private void AddCachedRosterResponses(
        DadAggregateRosterCatalogResponse aggregate,
        string skipWorkerId)
    {
        foreach (var response in rosterCatalogs.Values.OrderBy(static response => response.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(skipWorkerId) &&
                string.Equals(response.WorkerSessionId.Value, skipWorkerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            aggregate.Responses.Add(response);
        }
    }

    private void AddCachedRosterResponses(
        DadAggregateRosterCatalogResponse aggregate,
        ISet<string> targetWorkerIds)
    {
        foreach (var workerId in targetWorkerIds.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (rosterCatalogs.TryGetValue(workerId, out var response))
                aggregate.Responses.Add(response);
        }
    }

    private void AddCachedProfileResponses(
        DadAggregateProfileCatalogResponse aggregate,
        string skipWorkerId)
    {
        foreach (var response in profileCatalogs.Values.OrderBy(static response => response.Catalog.OwnerWorkerSessionId.Value, StringComparer.OrdinalIgnoreCase))
        {
            var workerId = response.Catalog.OwnerWorkerSessionId.Value;
            if (!string.IsNullOrWhiteSpace(skipWorkerId) &&
                string.Equals(workerId, skipWorkerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsWorkerOnline(response.Catalog.OwnerWorkerSessionId))
                continue;

            aggregate.Responses.Add(response);
        }
    }

    private void AddCachedProfileResponses(
        DadAggregateProfileCatalogResponse aggregate,
        ISet<string> targetWorkerIds)
    {
        foreach (var workerId in targetWorkerIds.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (profileCatalogs.TryGetValue(workerId, out var response) &&
                IsWorkerOnline(response.Catalog.OwnerWorkerSessionId))
                aggregate.Responses.Add(response);
        }
    }

    private int EstimateExpectedRemoteRosterCatalogCount(string skipWorkerId)
        => CurrentTransport.KnownParticipants
            .Where(participant => participant.State != DadParticipantState.Stale)
            .Select(static participant => participant.WorkerSessionId.Value)
            .Where(workerId => !string.IsNullOrWhiteSpace(workerId))
            .Where(workerId => string.IsNullOrWhiteSpace(skipWorkerId) ||
                               !string.Equals(workerId, skipWorkerId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private int EstimateExpectedRemoteProfileCatalogCount(string skipWorkerId)
        => EstimateExpectedRemoteRosterCatalogCount(skipWorkerId);

    private static DadAggregateRosterCatalogResponse CreateRosterAggregate(string requestId)
        => new()
        {
            RequestId = requestId,
            RespondedAtUtc = DateTime.UtcNow,
        };

    private static DadAggregateProfileCatalogResponse CreateProfileAggregate(string requestId)
        => new()
        {
            RequestId = requestId,
            RespondedAtUtc = DateTime.UtcNow,
        };

    private void MergeRosterAggregate(
        DadAggregateRosterCatalogResponse target,
        DadAggregateRosterCatalogResponse source,
        bool excludeLocal)
    {
        foreach (var response in source.Responses)
        {
            var mergedResponse = response;
            if (excludeLocal &&
                DadRosterTransportCatalogRuntime.IsRequesterCatalogResponse(
                    response,
                    presenceService.WorkerSessionId,
                    presenceService.ClientInstanceId))
            {
                continue;
            }

            if (excludeLocal)
                mergedResponse = DadRosterTransportCatalogRuntime.WithoutRequesterCatalogRows(
                    response,
                    presenceService.WorkerSessionId,
                    presenceService.ClientInstanceId);

            target.Responses.Add(mergedResponse);
            rosterCatalogs[mergedResponse.WorkerSessionId.Value] = mergedResponse;
        }

        foreach (var warning in source.Warnings)
            AddWarning(target.Warnings, warning);
    }

    private void MergeProfileAggregate(
        DadAggregateProfileCatalogResponse target,
        DadAggregateProfileCatalogResponse source,
        bool excludeLocal)
    {
        foreach (var response in source.Responses)
        {
            if (excludeLocal &&
                string.Equals(response.Catalog.OwnerWorkerSessionId.Value, presenceService.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            target.Responses.Add(response);
            var workerId = response.Catalog.OwnerWorkerSessionId.Value;
            if (!string.IsNullOrWhiteSpace(workerId))
            {
                profileCatalogs[workerId] = response;
                profileCatalogOfflineSinceUtc.TryRemove(workerId, out _);
                Interlocked.Increment(ref CurrentTransport.ProfileCatalogCacheRevision);
            }
        }

        foreach (var warning in source.Warnings)
            AddWarning(target.Warnings, warning);
    }

    private static void FinalizeRosterAggregate(DadAggregateRosterCatalogResponse aggregate, int expectedCatalogCount)
    {
        aggregate.ExpectedCatalogCount = Math.Max(0, expectedCatalogCount);
        aggregate.RespondedCatalogCount = aggregate.Responses.Count;
        aggregate.Complete = aggregate.RespondedCatalogCount >= aggregate.ExpectedCatalogCount &&
                             aggregate.PendingCatalogCount == 0 &&
                             aggregate.TimedOutCatalogCount == 0;
        aggregate.Summary = BuildAggregateCatalogSummary(
            "roster",
            aggregate.ExpectedCatalogCount,
            aggregate.RespondedCatalogCount,
            aggregate.PendingCatalogCount,
            aggregate.TimedOutCatalogCount);
    }

    private static void FinalizeProfileAggregate(DadAggregateProfileCatalogResponse aggregate, int expectedCatalogCount)
    {
        aggregate.ExpectedCatalogCount = Math.Max(0, expectedCatalogCount);
        aggregate.RespondedCatalogCount = aggregate.Responses.Count;
        aggregate.Complete = aggregate.RespondedCatalogCount >= aggregate.ExpectedCatalogCount &&
                             aggregate.PendingCatalogCount == 0 &&
                             aggregate.TimedOutCatalogCount == 0;
        aggregate.Summary = BuildAggregateCatalogSummary(
            "profile",
            aggregate.ExpectedCatalogCount,
            aggregate.RespondedCatalogCount,
            aggregate.PendingCatalogCount,
            aggregate.TimedOutCatalogCount);
    }

    private static string BuildAggregateCatalogSummary(
        string catalogKind,
        int expectedCatalogCount,
        int respondedCatalogCount,
        int pendingCatalogCount,
        int timedOutCatalogCount)
    {
        if (expectedCatalogCount == 0)
            return $"No Dad {catalogKind} catalogs were expected.";

        var baseText = respondedCatalogCount >= expectedCatalogCount &&
                       pendingCatalogCount == 0 &&
                       timedOutCatalogCount == 0
            ? $"Read all {respondedCatalogCount}/{expectedCatalogCount} Dad {catalogKind} catalog(s)."
            : $"Read partial Dad {catalogKind} catalogs: {respondedCatalogCount}/{expectedCatalogCount}.";
        var details = new List<string>();
        if (pendingCatalogCount > 0)
            details.Add($"{pendingCatalogCount} pending");
        if (timedOutCatalogCount > 0)
            details.Add($"{timedOutCatalogCount} timed out");
        return details.Count == 0
            ? baseText
            : $"{baseText} {string.Join(", ", details)}.";
    }

    private static DadRosterRefreshPlan CloneRosterRefreshPlan(DadRosterRefreshPlan source)
        => new()
        {
            PlanId = source.PlanId,
            RequestedAtUtc = source.RequestedAtUtc,
            ForcePeerRefresh = source.ForcePeerRefresh,
            LiveConnectedOnly = source.LiveConnectedOnly,
            IncludeHidden = source.IncludeHidden,
            IncludeIgnored = source.IncludeIgnored,
            StaleAfterHours = source.StaleAfterHours,
            CharacterRefs = source.CharacterRefs.Select(static reference => new DadRosterCharacterRef
            {
                AccountKey = reference.AccountKey,
                CharacterKey = reference.CharacterKey,
                ContentId = reference.ContentId,
                RequiredJobId = reference.RequiredJobId,
                AdsLootMode = reference.AdsLootMode,
            }).ToList(),
            AccountKeys = [..source.AccountKeys],
            CharacterKeys = [..source.CharacterKeys],
            DryRun = source.DryRun,
            LogDiagnostics = source.LogDiagnostics,
            DiagnosticsReason = source.DiagnosticsReason,
        };

    private static void AddWarning(ICollection<string> warnings, string warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
            return;

        if (warnings.All(existing => !string.Equals(existing, warning, StringComparison.OrdinalIgnoreCase)))
            warnings.Add(warning);
    }

    private DadStopAllStatus BeginCoordinatorStopAll(DadStopAllRequest request)
    {
        lock (stopAllGate)
        {
            if (stopAllOperations.TryGetValue(request.OperationId, out var recorded))
                return recorded.Clone();
        }

        var targets = serverSessions.Snapshot()
            .Where(static connection => connection.IsRoutable && !connection.WorkerSessionId.IsEmpty)
            .Select(static connection => connection.WorkerSessionId)
            .DistinctBy(static worker => worker.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var local = InvokeLocalStopAll(request);
        var status = new DadStopAllStatus
        {
            OperationId = request.OperationId,
            RequestedByWorkerSessionId = request.RequestedByWorkerSessionId,
            SubmittedAtUtc = request.RequestedAtUtc,
            UpdatedAtUtc = DateTime.UtcNow,
            LocalResult = local,
            Partial = local.Partial,
            Workers = targets.Select(worker => new DadStopAllWorkerResult
            {
                OperationId = request.OperationId,
                WorkerSessionId = worker,
                State = DadStopAllWorkerState.Expected,
                UpdatedAtUtc = DateTime.UtcNow,
                Summary = "Awaiting Stop-all acknowledgement.",
            }).ToList(),
        };
        DadStopAllStatusRules.FinalizeFromWorkers(status, DateTime.UtcNow);
        RecordStopAllStatus(status);

        if (DadStopAllStatusRules.IsLocalCleanupPending(local))
            QueueStopAllLocal(request);
        foreach (var target in targets)
            QueueStopAllWorker(request, target);

        if (status.IsFinal)
            LogStopAllFinal(status);
        return status.Clone();
    }

    private void QueueStopAllLocal(DadStopAllRequest request)
    {
        var operationKey = $"stop-all-local:{request.OperationId}";
        if (!operations.TryAdd(operationKey, Task.CompletedTask))
            return;

        var task = RunStopAllLocalAsync(operationKey, request, roleCancellation.Token);
        operations[operationKey] = task;
        Track(task, operationKey);
    }

    private void QueueStopAllWorker(DadStopAllRequest request, DadWorkerSessionId target)
    {
        var operationKey = $"stop-all-fanout:{request.OperationId}:{target.Value}";
        if (!operations.TryAdd(operationKey, Task.CompletedTask))
            return;

        var task = RunStopAllWorkerAsync(operationKey, request, target, roleCancellation.Token);
        operations[operationKey] = task;
        Track(task, operationKey);
    }

    private void QueueForwardStopAll(DadStopAllRequest request, DadWorkerSessionId target)
    {
        var operationKey = $"stop-all-forward:{request.OperationId}";
        if (!operations.TryAdd(operationKey, Task.CompletedTask))
            return;
        var task = RunForwardStopAllAsync(operationKey, request, target, roleCancellation.Token);
        operations[operationKey] = task;
        Track(task, operationKey);
    }

    private async Task RunStopAllLocalAsync(
        string operationKey,
        DadStopAllRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(2, configuration.CancelAckTimeoutSeconds));
            var deadlineUtc = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadlineUtc)
            {
                await Task.Delay(StopAllCleanupPollInterval, cancellationToken).ConfigureAwait(false);
                var local = await Plugin.Framework
                    .RunOnFrameworkThread(() => InvokeLocalStopAll(request))
                    .ConfigureAwait(false);
                if (DadStopAllStatusRules.IsLocalCleanupPending(local))
                    continue;

                UpdateStopAllLocal(request.OperationId, local);
                return;
            }

            UpdateStopAllLocal(request.OperationId, BuildStopAllTimeoutResult(
                request.OperationId,
                presenceService.WorkerSessionId,
                "Local DAD-owned takeover cleanup did not finish before the Stop-all acknowledgement timeout."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            try
            {
                await Plugin.Framework.RunOnFrameworkThread(() => UpdateStopAllLocal(
                    request.OperationId,
                    new DadStopAllWorkerResult
                    {
                        OperationId = request.OperationId,
                        WorkerSessionId = presenceService.WorkerSessionId,
                        State = DadStopAllWorkerState.Rejected,
                        UpdatedAtUtc = DateTime.UtcNow,
                        Partial = true,
                        Summary = $"Local Stop-all cleanup acknowledgement failed: {ex.Message}",
                    })).ConfigureAwait(false);
            }
            catch (Exception callbackException) when (DadBackgroundTaskObserver.IsExpectedShutdownException(callbackException))
            {
            }
        }
        finally
        {
            operations.TryRemove(operationKey, out _);
        }
    }

    private async Task RunForwardStopAllAsync(
        string operationKey,
        DadStopAllRequest request,
        DadWorkerSessionId target,
        CancellationToken cancellationToken)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(2, configuration.CancelAckTimeoutSeconds));
            var response = await SendRequestAsync(
                    target,
                    MessageStopAll,
                    DadIpcJson.Serialize(request),
                    cancellationToken,
                    timeout)
                .ConfigureAwait(false);
            if (response.Kind == DadHubFrameKind.Error)
                throw new DadHubProtocolException(response.ErrorCode, response.ErrorMessage);
            var status = DadIpcJson.Deserialize<DadStopAllStatus>(response.PayloadJson)
                         ?? throw new DadHubProtocolException("invalid-response", "Dad Coordinator returned an invalid Stop-all result.");
            await Plugin.Framework.RunOnFrameworkThread(() => ApplyForwardedStopAllStatus(status)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            try
            {
                await Plugin.Framework.RunOnFrameworkThread(() => ApplyForwardedStopAllFailure(request, ex.Message)).ConfigureAwait(false);
            }
            catch (Exception callbackException) when (DadBackgroundTaskObserver.IsExpectedShutdownException(callbackException))
            {
            }
        }
        finally
        {
            operations.TryRemove(operationKey, out _);
        }
    }

    private async Task RunStopAllWorkerAsync(
        string operationKey,
        DadStopAllRequest request,
        DadWorkerSessionId target,
        CancellationToken cancellationToken)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(2, configuration.CancelAckTimeoutSeconds));
            var deadlineUtc = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadlineUtc)
            {
                var remaining = deadlineUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;

                var response = await SendRequestAsync(
                        target,
                        MessageStopAll,
                        DadIpcJson.Serialize(request),
                        cancellationToken,
                        remaining)
                    .ConfigureAwait(false);
                if (response.Kind == DadHubFrameKind.Error)
                    throw new DadHubProtocolException(response.ErrorCode, response.ErrorMessage);

                var aggregate = DadIpcJson.Deserialize<DadStopAllStatus>(response.PayloadJson);
                var worker = aggregate?.LocalResult ?? new DadStopAllWorkerResult
                {
                    OperationId = request.OperationId,
                    WorkerSessionId = target,
                    State = DadStopAllWorkerState.Rejected,
                    Partial = true,
                    Summary = "Client Dad returned an invalid Stop-all acknowledgement.",
                };
                worker.WorkerSessionId = target;
                DadStopAllStatusRules.NormalizeLocalResult(worker);
                if (!DadStopAllStatusRules.IsLocalCleanupPending(worker))
                {
                    UpdateStopAllWorker(request.OperationId, worker);
                    return;
                }

                await Task.Delay(StopAllCleanupPollInterval, cancellationToken).ConfigureAwait(false);
            }

            UpdateStopAllWorker(request.OperationId, BuildStopAllTimeoutResult(
                request.OperationId,
                target,
                "Client DAD-owned takeover cleanup did not finish before the Stop-all acknowledgement timeout."));
        }
        catch (DadHubProtocolException ex) when (string.Equals(ex.Code, "request-timeout", StringComparison.OrdinalIgnoreCase))
        {
            UpdateStopAllWorker(request.OperationId, new DadStopAllWorkerResult
            {
                OperationId = request.OperationId,
                WorkerSessionId = target,
                State = DadStopAllWorkerState.TimedOut,
                UpdatedAtUtc = DateTime.UtcNow,
                Summary = ex.Message,
            });
        }
        catch (Exception ex) when (!IsWorkerOnline(target))
        {
            UpdateStopAllWorker(request.OperationId, new DadStopAllWorkerResult
            {
                OperationId = request.OperationId,
                WorkerSessionId = target,
                State = DadStopAllWorkerState.Disconnected,
                UpdatedAtUtc = DateTime.UtcNow,
                Summary = $"Client Dad disconnected before acknowledging Stop-all: {ex.Message}",
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            UpdateStopAllWorker(request.OperationId, new DadStopAllWorkerResult
            {
                OperationId = request.OperationId,
                WorkerSessionId = target,
                State = DadStopAllWorkerState.Rejected,
                UpdatedAtUtc = DateTime.UtcNow,
                Summary = ex.Message,
            });
        }
        finally
        {
            operations.TryRemove(operationKey, out _);
        }
    }

    private static DadStopAllWorkerResult BuildStopAllTimeoutResult(
        string operationId,
        DadWorkerSessionId workerSessionId,
        string summary)
        => new()
        {
            OperationId = operationId,
            WorkerSessionId = workerSessionId,
            State = DadStopAllWorkerState.TimedOut,
            UpdatedAtUtc = DateTime.UtcNow,
            Partial = true,
            Summary = summary,
        };

    private void UpdateStopAllLocal(string operationId, DadStopAllWorkerResult local)
    {
        DadStopAllStatus? final = null;
        DadStopAllStatus? updated = null;
        lock (stopAllGate)
        {
            if (!stopAllOperations.TryGetValue(operationId, out var status) ||
                !DadStopAllStatusRules.IsLocalCleanupPending(status.LocalResult))
            {
                return;
            }

            DadStopAllStatusRules.NormalizeLocalResult(local);
            status.LocalResult = local.Clone();
            status.UpdatedAtUtc = DateTime.UtcNow;
            log.Information("[dad] Stop-all {OperationId} local cleanup: {State}.",
                operationId,
                local.State);
            DadStopAllStatusRules.FinalizeFromWorkers(status, DateTime.UtcNow);
            stopAllOperations[operationId] = status;
            latestStopAllStatus = status.Clone();
            updated = status.Clone();
            if (status.IsFinal)
                final = status.Clone();
        }

        if (updated != null)
            BroadcastStopAllStatus(updated);
        if (final != null)
            LogStopAllFinal(final);
    }

    private void UpdateStopAllWorker(string operationId, DadStopAllWorkerResult worker)
    {
        DadStopAllStatus? final = null;
        DadStopAllStatus? updated = null;
        lock (stopAllGate)
        {
            if (!stopAllOperations.TryGetValue(operationId, out var status))
                return;
            var index = status.Workers.FindIndex(candidate => string.Equals(
                candidate.WorkerSessionId.Value,
                worker.WorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase));
            if (index < 0 || status.Workers[index].State != DadStopAllWorkerState.Expected)
                return;

            status.Workers[index] = worker.Clone();
            status.UpdatedAtUtc = DateTime.UtcNow;
            log.Information("[dad] Stop-all {OperationId} worker {WorkerSessionId}: {State}.",
                operationId,
                worker.WorkerSessionId,
                worker.State);
            DadStopAllStatusRules.FinalizeFromWorkers(status, DateTime.UtcNow);
            stopAllOperations[operationId] = status;
            latestStopAllStatus = status.Clone();
            updated = status.Clone();
            if (status.IsFinal)
                final = status.Clone();
        }

        if (updated != null)
            BroadcastStopAllStatus(updated);
        if (final != null)
            LogStopAllFinal(final);
    }

    private void BroadcastStopAllStatus(DadStopAllStatus status)
    {
        if (!configuration.RunAsServerDad)
            return;
        var payload = DadIpcJson.Serialize(status);
        foreach (var connection in serverSessions.Snapshot().Where(static connection => connection.IsRoutable))
        {
            Track(
                SendFrameAsync(
                    connection,
                    DadHubProtocol.CreateFrame(
                        DadHubFrameKind.Notification,
                        presenceService.WorkerSessionId,
                        connection.WorkerSessionId,
                        MessageStopAll,
                        status.OperationId,
                        payload,
                        configuration.TransportSharedSecret),
                    MessageStopAll,
                    roleCancellation.Token),
                $"stop-all-status:{status.OperationId}:{connection.WorkerSessionId.Value}");
        }
    }

    private void LogStopAllFinal(DadStopAllStatus status)
        => log.Information("[dad] Stop-all {OperationId} final: {Summary}", status.OperationId, status.Summary);

    private DadStopAllWorkerResult InvokeLocalStopAll(DadStopAllRequest request)
    {
        var result = stopAllHandler?.Invoke(request) ?? new DadStopAllWorkerResult
        {
            OperationId = request.OperationId,
            WorkerSessionId = presenceService.WorkerSessionId,
            State = DadStopAllWorkerState.Rejected,
            Summary = "Local Stop-all handler is unavailable.",
        };
        result.OperationId = request.OperationId;
        result.WorkerSessionId = presenceService.WorkerSessionId;
        result.UpdatedAtUtc = DateTime.UtcNow;
        DadStopAllStatusRules.NormalizeLocalResult(result);
        log.Information("[dad] Stop-all {OperationId} local completion: {State}; {Summary}",
            request.OperationId,
            result.State,
            result.Summary);
        return result;
    }

    private void ApplyForwardedStopAllStatus(DadStopAllStatus status)
        => RecordStopAllStatus(status);

    private void ApplyForwardedStopAllFailure(DadStopAllRequest request, string failure)
    {
        var local = InvokeLocalStopAll(request);
        var fallback = new DadStopAllStatus
        {
            OperationId = request.OperationId,
            RequestedByWorkerSessionId = request.RequestedByWorkerSessionId,
            SubmittedAtUtc = request.RequestedAtUtc,
            UpdatedAtUtc = DateTime.UtcNow,
            RemotePropagationAvailable = false,
            Partial = true,
            Summary = $"Local DAD work stopped; Dad Coordinator propagation failed: {failure}",
            LocalResult = local,
        };
        DadStopAllStatusRules.FinalizeFromWorkers(fallback, DateTime.UtcNow);
        RecordStopAllStatus(fallback);
        if (DadStopAllStatusRules.IsLocalCleanupPending(local))
            QueueStopAllLocal(request);
        else
            LogStopAllFinal(fallback);
    }

    private void RecordStopAllStatus(DadStopAllStatus status, bool preserveCoordinatorMatrix = false)
    {
        lock (stopAllGate)
        {
            if (preserveCoordinatorMatrix &&
                stopAllOperations.TryGetValue(status.OperationId, out var existing) &&
                !existing.IsFinal)
            {
                existing.LocalResult = status.LocalResult.Clone();
                existing.UpdatedAtUtc = DateTime.UtcNow;
                stopAllOperations[status.OperationId] = existing;
                latestStopAllStatus = existing.Clone();
                return;
            }

            var clone = status.Clone();
            stopAllOperations[clone.OperationId] = clone;
            latestStopAllStatus = clone.Clone();
        }
    }

    private void NormalizeStopAllRequest(DadStopAllRequest request)
    {
        request.OperationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId.Trim();
        request.RequestedByWorkerSessionId = request.RequestedByWorkerSessionId.IsEmpty
            ? presenceService.WorkerSessionId
            : request.RequestedByWorkerSessionId;
        request.RequestedAtUtc = request.RequestedAtUtc == default ? DateTime.UtcNow : request.RequestedAtUtc;
        request.Reason = string.IsNullOrWhiteSpace(request.Reason) ? "Stopped by operator." : request.Reason.Trim();
    }

    private void QueueOperation<TRequest, TResponse>(
        string operationKey,
        DadWorkerSessionId targetWorkerSessionId,
        string messageType,
        TRequest request,
        Action<TResponse>? completed)
    {
        if (!CanQueueOperation(targetWorkerSessionId))
            return;

        if (!operations.TryAdd(operationKey, Task.CompletedTask))
            return;

        var operationCancellation = roleCancellation.Token;
        var task = RunOperationAsync(
            operationKey,
            targetWorkerSessionId,
            messageType,
            DadIpcJson.Serialize(request),
            completed,
            operationCancellation);
        operations[operationKey] = task;
        Track(task, $"{messageType}:{targetWorkerSessionId.Value}");
    }

    private async Task RunOperationAsync<TResponse>(
        string operationKey,
        DadWorkerSessionId targetWorkerSessionId,
        string messageType,
        string payloadJson,
        Action<TResponse>? completed,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            var response = await SendRequestAsync(
                    targetWorkerSessionId,
                    messageType,
                    payloadJson,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.Kind == DadHubFrameKind.Error)
                throw new DadHubProtocolException(response.ErrorCode, response.ErrorMessage);

            if (completed == null)
            {
                completedOperations[operationKey] = new CompletedOperation
                {
                    PayloadJson = response.PayloadJson,
                    CompletedAtUtc = DateTime.UtcNow,
                };
            }
            if (completed != null)
            {
                var typed = DadIpcJson.Deserialize<TResponse>(response.PayloadJson);
                if (typed != null &&
                    !frameworkCallbacks.Enqueue(() => completed(typed)))
                {
                    CurrentTransport.LastTransportTimeoutSummary = $"{messageType} completion callback dropped because the framework queue is full.";
                    // B6: surface the drop and mark the publish dirty so the populate path re-issues the
                    // refresh instead of silently losing the result.
                    Interlocked.Increment(ref CurrentTransport.RosterCatalogDroppedCount);
                    MarkHubRosterDirty(CurrentTransport.LastTransportTimeoutSummary, fast: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (DadBackgroundTaskObserver.IsExpectedShutdownException(ex))
        {
        }
        catch (Exception) when (!CanQueueOperation(targetWorkerSessionId))
        {
            // Reconnect polling is intentionally quiet. Once the handshake is no longer routable,
            // status/catalog/wake callers will coalesce on their next normal framework update.
        }
        catch (Exception ex)
        {
            CurrentTransport.LastRequestStatus = $"{messageType} failed: {ex.Message}";
            log.Debug(ex, "[dad] Hub operation {MessageType} failed for {WorkerSessionId}.", messageType, targetWorkerSessionId);
        }
        finally
        {
            operations.TryRemove(operationKey, out _);
        }
    }

    private async Task<DadHubFrame> SendRequestAsync(
        DadWorkerSessionId targetWorkerSessionId,
        string messageType,
        string payloadJson,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        var connection = ResolveConnection(targetWorkerSessionId)
                         ?? throw new DadHubProtocolException(
                             "worker-offline",
                             $"Worker session {targetWorkerSessionId} is not connected.");
        return await SendRequestFrameAsync(
                connection,
                targetWorkerSessionId,
                messageType,
                payloadJson,
                cancellationToken,
                requestTimeout)
            .ConfigureAwait(false);
    }

    private async Task<DadHubFrame> SendRequestFrameAsync(
        DadHubConnection connection,
        DadWorkerSessionId targetWorkerSessionId,
        string messageType,
        string payloadJson,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<DadHubFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingRequests.TryAdd(correlationId, completion))
            throw new InvalidOperationException("Dad hub correlation id collision.");

        try
        {
            await SendFrameAsync(
                connection,
                DadHubProtocol.CreateFrame(
                    DadHubFrameKind.Request,
                    presenceService.WorkerSessionId,
                    targetWorkerSessionId,
                    messageType,
                    correlationId,
                    payloadJson,
                    configuration.TransportSharedSecret),
                messageType,
                cancellationToken).ConfigureAwait(false);

            return await completion.Task.WaitAsync(requestTimeout ?? RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new DadHubProtocolException(
                "request-timeout",
                $"{messageType} timed out waiting for {targetWorkerSessionId}.");
        }
        finally
        {
            pendingRequests.TryRemove(correlationId, out _);
        }
    }

    private async Task SendFrameAsync(
        DadHubConnection connection,
        DadHubFrame frame,
        string operationName,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref pendingOutboundOperations);
        UpdateTransportQueueDiagnostics();
        try
        {
            using var outboundLease = await outboundSlots.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
            if (outboundLease == null)
                throw new OperationCanceledException("Dad transport is shutting down.", cancellationToken);
            await connection.SendAsync(frame, OutboundWriteTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (DadHubProtocolException ex) when (string.Equals(ex.Code, "write-timeout", StringComparison.OrdinalIgnoreCase))
        {
            RecordTransportTimeout(connection, $"{operationName} write timed out after {OutboundWriteTimeout.TotalSeconds:F0}s.");
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref pendingOutboundOperations);
            UpdateTransportQueueDiagnostics();
        }
    }

    private void RecordTransportTimeout(DadHubConnection connection, string summary)
    {
        var worker = connection.WorkerSessionId.IsEmpty ? connection.RemoteWorkerSessionId : connection.WorkerSessionId;
        CurrentTransport.LastTransportTimeoutSummary = worker.IsEmpty
            ? summary
            : $"{summary} Closed {worker}.";
        MarkHubRosterDirty(CurrentTransport.LastTransportTimeoutSummary, fast: true);
        if (configuration.RunAsServerDad && !connection.WorkerSessionId.IsEmpty)
            MarkServerSessionDisconnected(connection);
        else
            connection.Close();
    }

    private DadHubConnection? ResolveConnection(DadWorkerSessionId targetWorkerSessionId)
    {
        if (configuration.RunAsServerDad)
            return serverSessions.TryGet(targetWorkerSessionId, out var session) && session is { IsRoutable: true }
                ? session
                : null;

        return clientConnection is { IsRoutable: true } connection ? connection : null;
    }

    private bool CanQueueOperation(DadWorkerSessionId targetWorkerSessionId)
        => DadHubTransportRouting.CanQueue(
            targetWorkerSessionId,
            ResolveConnection(targetWorkerSessionId) is { IsRoutable: true });

    private DadWorkerSessionId ResolveAuthorityWorkerSessionId()
    {
        if (configuration.RunAsServerDad)
            return presenceService.WorkerSessionId;

        if (clientConnection is not { IsRoutable: true })
            return new DadWorkerSessionId(string.Empty);

        if (serverParticipant != null && !serverParticipant.WorkerSessionId.IsEmpty)
            return serverParticipant.WorkerSessionId;

        return CurrentTransport.AuthorityWorkerSessionId;
    }

    private void RegisterServerSession(DadHubConnection connection)
    {
        var existing = serverSessions.Register(connection.WorkerSessionId, connection);
        if (existing != null)
        {
            existing.Replaced = true;
            existing.Close();
        }
        disconnectedParticipants.TryRemove(connection.WorkerSessionId.Value, out _);
        nextRosterRefreshUtc[connection.WorkerSessionId.Value] = DateTime.MinValue;
        nextProfileRefreshUtc[connection.WorkerSessionId.Value] = DateTime.MinValue;
        CurrentTransport.LastRequestStatus = $"Client Dad {connection.WorkerSessionId} connected.";
        log.Information(
            "[dad] Client Dad connected: {WorkerSessionId} ({ClientInstanceId}).",
            connection.WorkerSessionId,
            connection.ClientInstanceId);

        // B3: immediately pull the freshly connected peer's catalog/profile so the next publish/populate
        // includes it instead of waiting for the slow periodic reconcile. The completion bumps the cache
        // revision (B1) and marks the publish dirty (B2), so all clients get the updated projection.
        QueueRosterCatalogRefresh(connection, force: true, new DadRosterRefreshPlan
        {
            IncludeHidden = true,
            IncludeIgnored = true,
        });
        QueueProfileCatalogRefresh(connection, force: true, Guid.NewGuid().ToString("N"));
        MarkHubRosterDirty($"Client Dad {connection.WorkerSessionId} connected; pulling roster catalog.", fast: true);
    }

    private void MarkServerSessionDisconnected(DadHubConnection connection)
    {
        connection.Close();
        if (connection.Replaced)
            return;

        if (!QueueTransportEvent(() => ApplyServerSessionDisconnected(connection), "disconnect"))
            ApplyServerSessionDisconnected(connection);
    }

    private void ApplyServerSessionDisconnected(DadHubConnection connection)
    {
        if (serverSessions.RemoveIfCurrent(connection.WorkerSessionId, connection))
        {
            disconnectedParticipants[connection.WorkerSessionId.Value] = new DisconnectedParticipant
            {
                Participant = connection.Participant.Clone(),
                DisconnectedAtUtc = DateTime.UtcNow,
                LastHeartbeatUtc = connection.LastHeartbeatUtc,
            };
            CurrentTransport.LastRequestStatus = $"Client Dad {connection.WorkerSessionId} disconnected.";
            MarkHubRosterDirty(CurrentTransport.LastRequestStatus, fast: true);
        }
    }

    private async Task SendHeartbeatAsync(
        DadHubConnection connection,
        DadParticipantSnapshot participant,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendFrameAsync(
                connection,
                DadHubProtocol.CreateFrame(
                    DadHubFrameKind.Heartbeat,
                    presenceService.WorkerSessionId,
                    connection.RemoteWorkerSessionId,
                    "heartbeat",
                    string.Empty,
                    DadIpcJson.Serialize(new DadHubHeartbeat
                    {
                        SentAtUtc = DateTime.UtcNow,
                        Participant = participant,
                    }),
                    configuration.TransportSharedSecret),
                "heartbeat",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (DadBackgroundTaskObserver.IsExpectedShutdownException(ex))
        {
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[dad] Client Dad heartbeat failed.");
            if (configuration.RunAsServerDad)
                MarkServerSessionDisconnected(connection);
            else
                connection.Close();
        }
    }

    private void MarkHubRosterDirty(string reason, bool fast)
    {
        if (!configuration.RunAsServerDad || localOnlyModeEnabled)
            return;

        rosterPublishCoalescer.MarkDirty(reason, fast, DateTime.UtcNow);
        UpdateTransportQueueDiagnostics();
    }

    private void RecordRuntimeReadinessChange(DadWorkerSessionId workerSessionId, long revision)
    {
        if (workerSessionId.IsEmpty)
            return;

        if (!pendingRuntimeReadinessChanges.TryGetValue(workerSessionId.Value, out var current) || revision > current)
            pendingRuntimeReadinessChanges[workerSessionId.Value] = revision;
    }

    private void FlushRuntimeReadinessChanges()
    {
        if (pendingRuntimeReadinessChanges.Count == 0)
            return;

        var changes = pendingRuntimeReadinessChanges.ToList();
        pendingRuntimeReadinessChanges.Clear();
        foreach (var (workerSessionId, revision) in changes)
        {
            try
            {
                runtimeReadinessHandler?.Invoke(new DadWorkerSessionId(workerSessionId), revision);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[dad] Runtime readiness edge {Revision} for {WorkerSessionId} failed during framework dispatch.", revision, workerSessionId);
            }
        }
    }

    private void FlushHubRosterPublishIfDue(DateTime nowUtc)
    {
        if (!rosterPublishCoalescer.TryFlush(nowUtc, out var reason))
            return;

        var publish = CreateHubRosterPublish(reason);
        if (publish != null)
            Track(BroadcastHubRosterPublishAsync(publish, roleCancellation.Token), "hub roster publish");
    }

    private DadHubRosterPublish? CreateHubRosterPublish(string status)
    {
        RefreshLocalMutationState();
        if (!configuration.RunAsServerDad || localOnlyModeEnabled)
            return null;

        var publish = BuildHubRosterPublish();
        lastHubRosterPublish = publish.Clone();
        ApplyHubRosterPublishToTransport(publish);
        if (!string.IsNullOrWhiteSpace(status))
            CurrentTransport.LastRequestStatus = status;
        CurrentTransport.LastRosterPublishReason = status;
        CurrentTransport.LastRosterPublishUtc = publish.PublishedAtUtc;
        CurrentTransport.CoalescedRosterPublishCount = rosterPublishCoalescer.CoalescedCount;
        return publish;
    }

    private DadHubRosterPublish BuildHubRosterPublish()
    {
        var now = DateTime.UtcNow;
        var staleAfter = GetHeartbeatStaleThreshold();
        var authorityEndpoint = !string.IsNullOrWhiteSpace(CurrentTransport.ListenerEndpoint)
            ? CurrentTransport.ListenerEndpoint
            : FormatEndpoint(configuration.ServerListenHost, configuration.ServerListenPort);
        var coordinator = GetLocalParticipant();
        coordinator.Endpoint = authorityEndpoint;
        coordinator.IsLocalClient = false;
        coordinator.IsAuthority = true;
        coordinator.WorkerRole = DadWorkerRole.ServerDad;
        coordinator.AuthorityMode = DadAuthorityMode.ServerDad;
        if (!remoteMutationsAllowed)
            MarkSnapshotUnavailable(coordinator, BuildLocalUnavailableReason());

        var clients = serverSessions.Snapshot()
            .Where(static connection => connection.IsRoutable)
            .Select(connection =>
            {
                var participant = DadHubParticipants.PrepareRemoteWithStaleState(
                    connection.Participant,
                    connection.LastHeartbeatUtc,
                    now,
                    staleAfter,
                    "Client Dad heartbeat timed out.");
                participant.IsAuthority = false;
                if (participant.WorkerRole == DadWorkerRole.None)
                    participant.WorkerRole = DadWorkerRole.ClientDad;
                return participant;
            })
            .ToList();

        var disconnected = disconnectedParticipants.Values
            .Select(disconnectedParticipant =>
            {
                var participant = DadHubParticipants.PrepareRemoteWithStaleState(
                    disconnectedParticipant.Participant,
                    disconnectedParticipant.LastHeartbeatUtc,
                    disconnectedParticipant.DisconnectedAtUtc,
                    now,
                    staleAfter,
                    "Client Dad disconnected.");
                DadHubParticipants.MarkDisconnected(participant, "Client Dad disconnected.");
                participant.IsAuthority = false;
                if (participant.WorkerRole == DadWorkerRole.None)
                    participant.WorkerRole = DadWorkerRole.ClientDad;
                return participant;
            })
            .ToList();

        var participants = new List<DadParticipantSnapshot> { coordinator };
        participants.AddRange(clients);
        participants.AddRange(disconnected);
        participants = SortParticipants(participants);

        var publish = new DadHubRosterPublish
        {
            Generation = Interlocked.Increment(ref hubRosterGeneration),
            AuthorityEpochId = hubRosterAuthorityEpochId,
            PublishedAtUtc = now,
            AuthorityEndpoint = authorityEndpoint,
            AuthorityWorkerSessionId = presenceService.WorkerSessionId,
            CoordinatorParticipant = coordinator,
            ClientParticipants = SortParticipants(clients),
            DisconnectedParticipants = SortParticipants(disconnected),
            Participants = participants,
            CatalogRows = BuildHubRosterCatalogRows(),
        };
        TrimHubRosterCatalogRowsIfOversize(publish);
        return publish;
    }

    // B2: project the coordinator's own catalog (so it appears in clients' listings) plus every cached peer
    // catalog into the compact row form. The coordinator's own rows come from the B7 cache to avoid re-running
    // the heavy XADB fetch on every publish.
    private List<DadHubRosterCatalogRow> BuildHubRosterCatalogRows()
    {
        var responses = new List<DadPeerRosterCatalogResponse>();
        var local = GetCachedLocalRosterCatalogResponse();
        if (local != null)
            responses.Add(local);
        responses.AddRange(rosterCatalogs.Values);
        return DadHubRosterCatalogProjection.BuildCatalogRows(responses);
    }

    // B2: measure the signed outer frame, not only the inner publish JSON. The outer JSON escapes the inner
    // payload and can be materially larger. If the projection does not fit, fall back to participants-only
    // (the manual pull stays available as a debug fallback).
    private void TrimHubRosterCatalogRowsIfOversize(DadHubRosterPublish publish)
    {
        if (publish.CatalogRows.Count == 0)
            return;

        var payloadJson = DadIpcJson.Serialize(publish);
        var targets = serverSessions.Snapshot()
            .Where(static connection => connection.IsRoutable)
            .Select(static connection => connection.WorkerSessionId)
            .ToList();
        if (targets.Count == 0)
            targets.Add(presenceService.WorkerSessionId);

        if (targets.All(target => DadHubProtocol.GetSerializedFrameByteCount(
                DadHubProtocol.CreateFrame(
                    DadHubFrameKind.Notification,
                    presenceService.WorkerSessionId,
                    target,
                    MessageHubRosterPublish,
                    string.Empty,
                    payloadJson,
                    configuration.TransportSharedSecret)) <= DadHubProtocol.MaxFrameBytes))
        {
            if (hubRosterProjectionOversize)
                log.Information("[dad] Hub roster projection fits the frame budget again.");
            hubRosterProjectionOversize = false;
            return;
        }

        if (!hubRosterProjectionOversize)
        {
            log.Warning(
                "[dad] Hub roster projection ({Rows} row(s)) exceeds the frame budget; publishing participants only.",
                publish.CatalogRows.Count);
        }
        hubRosterProjectionOversize = true;
        publish.CatalogRows = [];
    }

    private async Task BroadcastHubRosterPublishAsync(
        DadHubRosterPublish publish,
        CancellationToken cancellationToken)
    {
        var payloadJson = DadIpcJson.Serialize(publish);
        var sends = serverSessions.Snapshot()
            .Where(static connection => connection.IsRoutable)
            .Select(connection => SendHubRosterPublishAsync(connection, payloadJson, cancellationToken))
            .ToList();
        if (sends.Count == 0)
            return;

        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    private async Task SendHubRosterPublishAsync(
        DadHubConnection connection,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendFrameAsync(
                connection,
                DadHubProtocol.CreateFrame(
                    DadHubFrameKind.Notification,
                    presenceService.WorkerSessionId,
                    connection.WorkerSessionId,
                    MessageHubRosterPublish,
                    string.Empty,
                    payloadJson,
                    configuration.TransportSharedSecret),
                MessageHubRosterPublish,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (DadBackgroundTaskObserver.IsExpectedShutdownException(ex))
        {
        }
        catch (DadHubProtocolException ex) when (string.Equals(ex.Code, "frame-too-large", StringComparison.OrdinalIgnoreCase))
        {
            CurrentTransport.LastRequestStatus =
                $"Dad Coordinator roster publish was too large for {connection.WorkerSessionId}; connection preserved.";
            log.Warning(ex, "[dad] Hub roster publish was too large for {WorkerSessionId}; preserving the connection.", connection.WorkerSessionId);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[dad] Hub roster publish failed for {WorkerSessionId}.", connection.WorkerSessionId);
            MarkServerSessionDisconnected(connection);
        }
    }

    private void RequestHubRosterPublish(DadParticipantSnapshot participant)
    {
        if (clientConnection is not { IsRoutable: true })
            return;

        var target = ResolveAuthorityWorkerSessionId();
        if (target.IsEmpty)
            return;

        var key = $"hub-roster-publish:{target.Value}";
        if (operations.ContainsKey(key))
        {
            CurrentTransport.LastRequestStatus = "Dad Coordinator roster publish request is pending.";
            return;
        }

        QueueOperation<DadHubHeartbeat, DadHubRosterPublish>(
            key,
            target,
            MessageHubRosterPublishRequest,
            new DadHubHeartbeat
            {
                SentAtUtc = DateTime.UtcNow,
                Participant = participant,
            },
            ApplyHubRosterPublish);
        CurrentTransport.LastRequestStatus = "Dad Coordinator roster publish request queued; using cached roster until it arrives.";
    }

    private void ApplyHubRosterPublish(DadHubRosterPublish publish)
    {
        RefreshLocalMutationState();
        if (configuration.RunAsServerDad || localOnlyModeEnabled)
            return;

        if (!DadHubRosterPublishCursor.ShouldApply(publish, lastAppliedHubRosterPublish))
            return;

        lastAppliedHubRosterPublish = DadHubRosterPublishCursor.FromPublish(publish);
        lastHubRosterPublish = publish.Clone();
        serverParticipant = publish.CoordinatorParticipant.Clone();
        // B2: cache the pushed catalog projection so the client renders peers (and the coordinator) without a
        // pull, and bump the cache revision (B1) so the roster UI re-merges from it.
        lastPushedCatalogRows = publish.CatalogRows.Select(static row => row.Clone()).ToList();
        Interlocked.Increment(ref CurrentTransport.RosterCatalogCacheRevision);
        RefreshTransportSnapshot();
        CurrentTransport.LastRequestStatus = $"Dad Coordinator published roster generation {publish.Generation} with {DadHubRosterPublishRuntime.CountPublishedParticipants(publish)} participant(s).";
    }

    private void ApplyHubRosterPublishToTransport(DadHubRosterPublish publish)
    {
        var participants = DadHubRosterPublishRuntime.BuildParticipantView(
            publish,
            GetLocalParticipant(),
            presenceService.WorkerSessionId,
            presenceService.ClientInstanceId);
        foreach (var participant in participants.Where(static participant => participant.IsLocalClient))
        {
            participant.Endpoint = publish.AuthorityEndpoint;
            participant.IsAuthority = true;
            participant.WorkerRole = DadWorkerRole.ServerDad;
        }

        SetTransportRoster(participants);
        CurrentTransport.AuthorityEndpoint = publish.AuthorityEndpoint;
        CurrentTransport.AuthorityWorkerSessionId = publish.AuthorityWorkerSessionId;
        CurrentTransport.AuthorityRole = DadWorkerRole.ServerDad;
        CurrentTransport.ConnectedPeerCount = serverSessions.Snapshot().Count(static connection => connection.IsRoutable);
        UpdateLanDiagnostics();
    }

    private bool TryBuildPublishedClientRoster(
        DateTime now,
        out List<DadParticipantSnapshot> participants,
        out string warning)
    {
        participants = [];
        warning = string.Empty;
        if (lastHubRosterPublish == null)
        {
            if (clientConnection is { IsRoutable: true })
                warning = "Dad Coordinator roster publish is not available yet; using local self plus coordinator only.";
            return false;
        }

        if (!DadHubRosterPublishRuntime.IsFresh(lastHubRosterPublish, now, GetHubRosterPublishStaleAfter()))
        {
            warning = "Dad Coordinator roster publish is stale; using local self plus coordinator only.";
            return false;
        }

        participants = DadHubRosterPublishRuntime.BuildParticipantView(
            lastHubRosterPublish,
            GetLocalParticipant(),
            presenceService.WorkerSessionId,
            presenceService.ClientInstanceId);
        return participants.Count > 0;
    }

    private TimeSpan GetHeartbeatInterval()
        => TimeSpan.FromSeconds(Math.Max(2, configuration.HeartbeatIntervalSeconds));

    private TimeSpan GetHeartbeatStaleThreshold()
    {
        var configuredStaleSeconds = Math.Max(3, configuration.HeartbeatStaleSeconds);
        var heartbeatMinimumSeconds = Math.Max(2, configuration.HeartbeatIntervalSeconds) * 3;
        return TimeSpan.FromSeconds(Math.Max(configuredStaleSeconds, heartbeatMinimumSeconds));
    }

    private TimeSpan GetPeerCatalogRefreshInterval()
        // B4: slow full-reconcile cadence. Keep the 10 s floor (so a misconfig can't spam pulls) and add a
        // 120 s ceiling (so the safety-net reconcile can't drift arbitrarily slow). Fast deltas come from
        // on-connect (B3) and level-up (B5) forced pulls; this is just the periodic backstop (default 60 s).
        => TimeSpan.FromSeconds(Math.Clamp(configuration.PeerCatalogRefreshIntervalSeconds, 10, 120));

    private TimeSpan GetHubRosterPublishStaleAfter()
        => TimeSpan.FromSeconds(Math.Max(6, GetHeartbeatStaleThreshold().TotalSeconds));

    private void SetTransportRoster(IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var sorted = SortParticipants(participants);
        var signature = BuildTransportRosterSignature(sorted);
        var semanticChange = !string.Equals(signature, transportRosterSignature, StringComparison.Ordinal);
        CurrentTransport.KnownParticipants = sorted;
        CurrentTransport.KnownParticipantCount = sorted.Count;
        CurrentTransport.LastResponses = DadHubRosterPublishRuntime.BuildSnapshotResponses(sorted);
        if (semanticChange)
        {
            transportRosterSignature = signature;
            Interlocked.Increment(ref CurrentTransport.TransportRevision);
        }
    }

    private static string BuildTransportRosterSignature(IEnumerable<DadParticipantSnapshot> participants)
        => string.Join(
            "\n",
            participants.Select(BuildParticipantTransportSignature));

    private static string BuildParticipantTransportSignature(DadParticipantSnapshot participant)
    {
        var character = participant.Character;
        return string.Join(
            "|",
            participant.WorkerSessionId.Value,
            participant.ClientInstanceId,
            participant.ProcessId,
            participant.DiscordApplicationId,
            participant.AutoPartyEndpointFingerprint,
            participant.AutoPartyPairingHealth,
            participant.AuthorityMode,
            participant.Role,
            participant.WorkerRole,
            participant.State,
            participant.IsLocalClient,
            participant.IsAuthority,
            participant.IsAvailable,
            participant.IsEligibleForRun,
            participant.ManagedAccountKey.Value,
            participant.ManagedAccountAlias,
            participant.ActiveCharacterKey.Value,
            character.CharacterKey,
            character.ContentId,
            character.CharacterName,
            character.WorldId,
            character.WorldName,
            character.DataCenterId,
            character.DataCenterName,
            character.AccountId,
            character.AccountAlias,
            character.Source,
            character.Freshness,
            character.CurrentJobId,
            character.CurrentJobAbbrev,
            character.CurrentLevel,
            character.Readiness,
            character.RosterVisibility,
            character.NeedsRosterUpdate,
            character.SnapshotQuality,
            character.SnapshotVersion,
            character.XadbReady,
            character.MapEligible,
            character.MapEligibilitySummary,
            string.Join(",", character.JobLevels.OrderBy(static pair => pair.Key).Select(static pair => $"{pair.Key}:{pair.Value}")),
            string.Join("\u001f", character.Blockers),
            string.Join("\u001f", participant.Warnings));
    }

    private static List<DadParticipantSnapshot> SortParticipants(IEnumerable<DadParticipantSnapshot> participants)
        => participants
            .Select(static participant => participant.Clone())
            .DistinctBy(BuildTransportParticipantKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static participant => participant.ManagedAccountAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.ActiveCharacterKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string BuildTransportParticipantKey(DadParticipantSnapshot participant)
    {
        if (!participant.WorkerSessionId.IsEmpty)
            return $"worker:{participant.WorkerSessionId.Value}";
        if (!string.IsNullOrWhiteSpace(participant.ClientInstanceId))
            return $"client:{participant.ClientInstanceId.Trim()}";
        return $"character:{participant.ActiveCharacterKey.Value}";
    }

    private static bool IsHubRosterFallbackStatus(string status)
        => status.Contains("roster publish", StringComparison.OrdinalIgnoreCase) &&
           status.Contains("using local self plus coordinator only", StringComparison.OrdinalIgnoreCase);

    private void RefreshRemoteCatalogCaches()
    {
        // B4: periodic full reconcile, throttled per-worker by GetPeerCatalogRefreshInterval (the slow safety
        // net). Connect (B3) and level-up (B5) drive fast forced deltas; this keeps the coordinator's
        // rosterCatalogs cache fresh so the B2 push projection stays accurate without manual tickling.
        foreach (var connection in serverSessions.Snapshot().Where(static connection => connection.IsRoutable))
        {
            QueueRosterCatalogRefresh(connection, force: false, new DadRosterRefreshPlan
            {
                IncludeHidden = true,
                IncludeIgnored = true,
            });
            QueueProfileCatalogRefresh(connection, force: false, Guid.NewGuid().ToString("N"));
        }
    }

    private void QueueRosterCatalogRefresh(
        DadHubConnection connection,
        bool force,
        DadRosterRefreshPlan request)
    {
        var workerId = connection.WorkerSessionId.Value;
        var throttled = nextRosterRefreshUtc.TryGetValue(workerId, out var nextRefresh) &&
                        DateTime.UtcNow < nextRefresh;
        if (!force && throttled)
            return;

        nextRosterRefreshUtc[workerId] = DateTime.UtcNow + GetPeerCatalogRefreshInterval();
        var key = $"catalog-roster:{workerId}";
        var operationInFlight = operations.ContainsKey(key);
        if (DadRosterRefreshDedupe.DecideRosterRefresh(force, throttled, operationInFlight) != DadRosterRefreshDispatch.Queue)
        {
            // B6: a forced (user-driven) request must not no-op while a periodic op is in flight. The in-flight
            // op already writes rosterCatalogs + bumps RosterCatalogCacheRevision (B1), so the UI re-renders
            // when it lands; record that we coalesced onto it instead of dropping the request.
            if (force && operationInFlight)
                CurrentTransport.LastRequestStatus = $"Roster refresh for {workerId} is already in flight; reusing it.";
            return;
        }

        request.PlanId = string.IsNullOrWhiteSpace(request.PlanId)
            ? Guid.NewGuid().ToString("N")
            : request.PlanId;
        QueueOperation<DadRosterRefreshPlan, DadPeerRosterCatalogResponse>(
            key,
            connection.WorkerSessionId,
            MessageRosterCatalogRequest,
            request,
            response =>
            {
                rosterCatalogs[workerId] = response;
                // B1: signal "fresh peer catalog landed" so any open roster UI re-merges from cache (no second click).
                Interlocked.Increment(ref CurrentTransport.RosterCatalogCacheRevision);
                MarkHubRosterDirty($"Client Dad {connection.WorkerSessionId} roster catalog response.", fast: false);
            });
    }

    private void QueueProfileCatalogRefresh(
        DadHubConnection connection,
        bool force,
        string requestId)
    {
        var workerId = connection.WorkerSessionId.Value;
        if (!force &&
            nextProfileRefreshUtc.TryGetValue(workerId, out var nextRefresh) &&
            DateTime.UtcNow < nextRefresh)
        {
            return;
        }

        nextProfileRefreshUtc[workerId] = DateTime.UtcNow + GetPeerCatalogRefreshInterval();
        var key = $"catalog-profile:{workerId}";
        if (operations.ContainsKey(key))
            return;

        QueueOperation<string, DadProfileCatalogResponse>(
            key,
            connection.WorkerSessionId,
            MessageProfileCatalogRequest,
            requestId,
            response =>
            {
                response.Catalog.OwnerWorkerSessionId = connection.WorkerSessionId;
                response.Catalog.OwnerEndpoint = string.Empty;
                response.Catalog.OwnerOnline = true;
                response.Catalog.ReadOnly = false;
                response.Catalog.GeneratedAtUtc = DateTime.UtcNow;
                profileCatalogs[workerId] = response;
                profileCatalogOfflineSinceUtc.TryRemove(workerId, out _);
                Interlocked.Increment(ref CurrentTransport.ProfileCatalogCacheRevision);
            });
    }

    private void RefreshTransportSnapshot()
    {
        RefreshLocalMutationState();
        var now = DateTime.UtcNow;
        var staleAfter = GetHeartbeatStaleThreshold();
        var participants = new List<DadParticipantSnapshot>();
        var rosterFallbackWarning = string.Empty;

        if (configuration.RunAsServerDad)
        {
            var configuredListenerEndpoint = FormatEndpoint(
                configuration.ServerListenHost,
                configuration.ServerListenPort);
            if (string.IsNullOrWhiteSpace(CurrentTransport.ListenerEndpoint) || !IsReady)
                CurrentTransport.ListenerEndpoint = configuredListenerEndpoint;
            var local = GetLocalParticipant();
            local.Endpoint = CurrentTransport.ListenerEndpoint;
            local.IsLocalClient = true;
            local.IsAuthority = true;
            if (!remoteMutationsAllowed)
                MarkSnapshotUnavailable(local, BuildLocalUnavailableReason());
            participants.Add(local);

            foreach (var connection in serverSessions.Snapshot().Where(static connection => connection.IsRoutable))
            {
                var participant = DadHubParticipants.PrepareRemoteWithStaleState(
                    connection.Participant,
                    connection.LastHeartbeatUtc,
                    now,
                    staleAfter,
                    "Client Dad heartbeat timed out.");
                participants.Add(participant);
            }

            foreach (var disconnected in disconnectedParticipants.Values)
            {
                var participant = DadHubParticipants.PrepareRemoteWithStaleState(
                    disconnected.Participant,
                    disconnected.LastHeartbeatUtc,
                    disconnected.DisconnectedAtUtc,
                    now,
                    staleAfter,
                    "Client Dad disconnected.");
                DadHubParticipants.MarkDisconnected(participant, "Client Dad disconnected.");
                participants.Add(participant);
            }

            CurrentTransport.AuthorityEndpoint = CurrentTransport.ListenerEndpoint;
            CurrentTransport.AuthorityWorkerSessionId = presenceService.WorkerSessionId;
            CurrentTransport.AuthorityRole = DadWorkerRole.ServerDad;
            CurrentTransport.AuthorityRoutable = IsReady;
            CurrentTransport.LastInboundFrameUtc = null;
        }
        else
        {
            CurrentTransport.ListenerEndpoint = string.Empty;
            CurrentTransport.AuthorityEndpoint = FormatEndpoint(
                configuration.ServerDadHost,
                configuration.ServerDadPort);
            CurrentTransport.AuthorityRole = DadWorkerRole.ServerDad;
            CurrentTransport.AuthorityWorkerSessionId = new DadWorkerSessionId(string.Empty);
            CurrentTransport.AuthorityRoutable = clientConnection is { IsRoutable: true };
            CurrentTransport.LastInboundFrameUtc = clientConnection?.LastFrameReceivedUtc;

            if (CurrentTransport.AuthorityRoutable &&
                !localOnlyModeEnabled &&
                TryBuildPublishedClientRoster(now, out var publishedParticipants, out rosterFallbackWarning))
            {
                participants.AddRange(publishedParticipants);
                var publish = lastHubRosterPublish;
                if (publish != null)
                {
                    CurrentTransport.AuthorityEndpoint = string.IsNullOrWhiteSpace(publish.AuthorityEndpoint)
                        ? CurrentTransport.AuthorityEndpoint
                        : publish.AuthorityEndpoint;
                    CurrentTransport.AuthorityWorkerSessionId = publish.AuthorityWorkerSessionId;
                }

                var authority = participants.FirstOrDefault(participant =>
                                    !CurrentTransport.AuthorityWorkerSessionId.IsEmpty &&
                                    string.Equals(
                                        participant.WorkerSessionId.Value,
                                        CurrentTransport.AuthorityWorkerSessionId.Value,
                                        StringComparison.OrdinalIgnoreCase)) ??
                                participants.FirstOrDefault(static participant => participant.IsAuthority);
                if (authority != null)
                {
                    serverParticipant = authority.Clone();
                    CurrentTransport.AuthorityWorkerSessionId = authority.WorkerSessionId;
                }
            }
            else
            {
                var local = GetLocalParticipant();
                local.Endpoint = string.Empty;
                local.IsLocalClient = true;
                local.IsAuthority = false;
                if (!remoteMutationsAllowed)
                    MarkSnapshotUnavailable(local, BuildLocalUnavailableReason());
                participants.Add(local);

                if (serverParticipant != null)
                {
                    var participant = DadHubParticipants.PrepareRemoteWithStaleState(
                        serverParticipant,
                        clientConnection?.LastHeartbeatUtc ?? serverParticipant.LastHeartbeatUtc,
                        now,
                        clientConnection is { IsRoutable: true } ? TimeSpan.MaxValue : staleAfter,
                        "Dad Coordinator connection lost.");

                    participants.Add(participant);
                    if (CurrentTransport.AuthorityRoutable)
                        CurrentTransport.AuthorityWorkerSessionId = participant.WorkerSessionId;
                }
            }
        }

        SetTransportRoster(participants);
        CurrentTransport.ConnectedPeerCount = configuration.RunAsServerDad
            ? serverSessions.Snapshot().Count(static connection => connection.IsRoutable)
            : CurrentTransport.KnownParticipants.Count(static participant =>
                !participant.IsLocalClient && participant.State != DadParticipantState.Stale);
        CurrentTransport.TransportMode = localOnlyModeEnabled
            ? DadTransportMode.LocalOnly
            : DadTransportMode.ServerHub;
        CurrentTransport.AuthorityStatus = !CurrentTransport.AuthorityRoutable || CurrentTransport.AuthorityWorkerSessionId.IsEmpty
            ? "Authority not connected."
            : DadStatusText.FormatAuthorityStatus(
                CurrentTransport.AuthorityRole,
                CurrentTransport.AuthorityWorkerSessionId,
                CurrentTransport.AuthorityEndpoint,
                DadAuthorityMode.ServerDad);
        if (!string.IsNullOrWhiteSpace(rosterFallbackWarning))
            CurrentTransport.LastRequestStatus = rosterFallbackWarning;
        UpdateLanDiagnostics();
    }

    private void SweepDisconnectedParticipants()
    {
        var retention = TimeSpan.FromSeconds(Math.Max(15, GetHeartbeatStaleThreshold().TotalSeconds * 3));
        foreach (var pair in disconnectedParticipants)
        {
            if (DateTime.UtcNow - pair.Value.DisconnectedAtUtc >= retention)
                disconnectedParticipants.TryRemove(pair.Key, out _);
        }
    }

    private void PruneOfflineProfileCatalogs(DateTime nowUtc)
    {
        var staleAfter = GetHeartbeatStaleThreshold();
        foreach (var pair in profileCatalogs.ToList())
        {
            var workerSessionId = pair.Value.Catalog.OwnerWorkerSessionId;
            if (IsWorkerOnline(workerSessionId))
            {
                profileCatalogOfflineSinceUtc.TryRemove(pair.Key, out _);
                continue;
            }

            var offlineSince = profileCatalogOfflineSinceUtc.GetOrAdd(pair.Key, nowUtc);
            if (nowUtc - offlineSince < staleAfter)
                continue;

            if (profileCatalogs.TryRemove(pair.Key, out _))
                Interlocked.Increment(ref CurrentTransport.ProfileCatalogCacheRevision);
            profileCatalogOfflineSinceUtc.TryRemove(pair.Key, out _);
        }
    }

    private void SweepCompletedOperations()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(30);
        foreach (var pair in completedOperations)
        {
            if (pair.Value.CompletedAtUtc < cutoff)
                completedOperations.TryRemove(pair.Key, out _);
        }
    }

    private HashSet<string> CurrentOnlineWorkerIds()
        => serverSessions.Snapshot()
            .Where(static connection => connection.IsRoutable)
            .Select(static connection => connection.WorkerSessionId.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private DadParticipantSnapshot GetLocalParticipant()
    {
        lock (localParticipantGate)
            return localParticipant.Clone();
    }

    private DadParticipantSnapshot BuildLocalTransportSnapshot()
    {
        var snapshot = presenceService.BuildSnapshotCopy();
        snapshot.Endpoint = string.Empty;
        if (!remoteMutationsAllowed)
            MarkSnapshotUnavailable(snapshot, BuildLocalUnavailableReason());
        return snapshot;
    }

    private static void MarkSnapshotUnavailable(DadParticipantSnapshot snapshot, string reason)
    {
        snapshot.IsAvailable = false;
        snapshot.IsEligibleForRun = false;
        snapshot.StatusText = reason;
        snapshot.Character.Readiness = DadReadinessState.Unavailable;
        if (snapshot.Character.Blockers.All(blocker => !string.Equals(blocker, reason, StringComparison.OrdinalIgnoreCase)))
            snapshot.Character.Blockers.Add(reason);
        if (snapshot.Warnings.All(warning => !string.Equals(warning, reason, StringComparison.OrdinalIgnoreCase)))
            snapshot.Warnings.Add(reason);
    }

    private string BuildLocalUnavailableReason()
    {
        if (!localPluginEnabled)
            return "dad is disabled; remote actions unavailable.";
        if (localOnlyModeEnabled)
            return "dad is in local-only mode; remote actions unavailable.";
        return string.Empty;
    }

    private string BuildRemoteMutationRejectedReason(string action)
    {
        if (!localPluginEnabled)
            return $"dad is disabled; rejected {action}.";
        if (localOnlyModeEnabled)
            return $"dad is in local-only mode; rejected {action}.";
        return string.Empty;
    }

    private DadParticipantReadyDto BuildRejectedWakeResponse(DadWakeRequestDto request, string reason)
    {
        var snapshot = BuildLocalTransportSnapshot();
        return new DadParticipantReadyDto
        {
            RunId = request.RunId,
            WorkerSessionId = presenceService.WorkerSessionId,
            CharacterKey = snapshot.ActiveCharacterKey,
            State = snapshot.State,
            PostArReady = snapshot.PostArReady,
            AcceptedAssignment = false,
            BlockerSummary = reason,
            StatusText = reason,
            Snapshot = snapshot,
        };
    }

    private DadClaimDecisionDto BuildRejectedClaimDecision(DadClaimRequestDto request, string reason)
    {
        var snapshot = BuildLocalTransportSnapshot();
        var lease = request.Lease?.Clone() ?? new DadParticipantLeaseRecord
        {
            RunId = request.RunId,
            SlotId = request.SlotId,
            AssignedAccountKey = request.RequiredAccountKey,
            AssignedCharacterKey = request.RequiredCharacterKey,
            OwningWorkerSessionId = snapshot.WorkerSessionId,
        };
        lease.State = DadParticipantLeaseState.Denied;
        lease.Summary = reason;
        return new DadClaimDecisionDto
        {
            RunId = request.RunId,
            WorkerSessionId = presenceService.WorkerSessionId,
            ClaimState = DadClaimState.Denied,
            LeaseState = DadParticipantLeaseState.Denied,
            CharacterKey = snapshot.ActiveCharacterKey,
            Reason = reason,
            Lease = lease,
            Snapshot = snapshot,
        };
    }

    private DadRunStepResultDto BuildRejectedAssemblyResult(
        DadAssemblyInstructionDto instruction,
        string reason)
        => new()
        {
            RunId = instruction.RunId,
            ModuleId = instruction.ModuleId,
            StepName = "Assembly",
            ParticipantState = BuildLocalTransportSnapshot().State,
            Deferred = true,
            Summary = reason,
            FailureReason = reason,
            BlockedReason = reason,
        };

    private DadCancelAckDto BuildRejectedCancelAck(DadCancelCommandDto command, string reason)
        => new()
        {
            RunId = command.RunId,
            WorkerSessionId = presenceService.WorkerSessionId,
            CancellationState = command.CancellationState,
            Summary = reason,
            Snapshot = BuildLocalTransportSnapshot(),
        };

    private void DrainFrameworkCallbacks()
    {
        frameworkCallbacks.Drain(
            MaxTransportEventsPerFrame,
            ex => log.Warning(ex, "[dad] Hub completion callback failed."));
        UpdateTransportQueueDiagnostics();
    }

    private bool QueueTransportEvent(Action action, string reason)
    {
        if (!transportEvents.Enqueue(action))
        {
            CurrentTransport.LastTransportTimeoutSummary = $"Dropped transport event '{reason}' because the framework queue is full.";
            MarkHubRosterDirty(CurrentTransport.LastTransportTimeoutSummary, fast: true);
            return false;
        }

        UpdateTransportQueueDiagnostics();
        return true;
    }

    private void DrainTransportEvents()
    {
        transportEvents.Drain(
            MaxTransportEventsPerFrame,
            ex => log.Warning(ex, "[dad] Transport event callback failed."));
        UpdateTransportQueueDiagnostics();
    }

    private void UpdateTransportQueueDiagnostics()
    {
        CurrentTransport.PendingTransportEventCount = transportEvents.Count + frameworkCallbacks.Count;
        CurrentTransport.PendingOutboundOperationCount = Math.Max(0, (int)Interlocked.Read(ref pendingOutboundOperations));
        CurrentTransport.CoalescedRosterPublishCount = rosterPublishCoalescer.CoalescedCount + transportEvents.DroppedCount + frameworkCallbacks.DroppedCount;
        if (!string.IsNullOrWhiteSpace(rosterPublishCoalescer.LastPublishReason))
            CurrentTransport.LastRosterPublishReason = rosterPublishCoalescer.LastPublishReason;
        if (rosterPublishCoalescer.LastPublishUtc.HasValue)
            CurrentTransport.LastRosterPublishUtc = rosterPublishCoalescer.LastPublishUtc;
    }

    private void CloseAllConnections(string reason)
    {
        foreach (var connection in serverSessions.Snapshot())
            connection.Close();
        serverSessions.Clear();
        clientConnection?.Close();
        clientConnection = null;
        CurrentTransport.ConnectionStatus = reason;
    }

    private void Track(Task task, string operationName)
        => backgroundTasks.Track(task, operationName);

    private void SetTransportError(string message)
    {
        IsReady = false;
        CurrentTransport.Availability = $"Unavailable: {message}";
        CurrentTransport.ConnectionStatus = message;
        CurrentTransport.LastRequestStatus = message;
        UpdateLanDiagnostics();
    }

    private void SetTransportAuthOrProtocolError(string message)
    {
        lastAuthOrProtocolError = message;
        SetTransportError(message);
    }

    private void ClearTransportAuthOrProtocolError()
    {
        lastAuthOrProtocolError = string.Empty;
        UpdateLanDiagnostics();
    }

    private void RecordAuthOrProtocolError(DadHubProtocolException exception)
    {
        if (!IsAuthOrProtocolException(exception))
            return;

        lastAuthOrProtocolError = exception.Message;
        CurrentTransport.LastRequestStatus = exception.Message;
        UpdateLanDiagnostics();
    }

    private static bool IsAuthOrProtocolException(DadHubProtocolException exception)
        => exception.Code.StartsWith("authentication-", StringComparison.OrdinalIgnoreCase) ||
           exception.Code.Equals("protocol-mismatch", StringComparison.OrdinalIgnoreCase);

    private void UpdateLanDiagnostics()
    {
        CurrentTransport.ConfiguredEndpoint = GetConfiguredAuthorityEndpoint();
        CurrentTransport.AdvertisedEndpoint = configuration.RunAsServerDad
            ? FirstNonEmpty(CurrentTransport.ListenerEndpoint, CurrentTransport.ConfiguredEndpoint)
            : FirstNonEmpty(CurrentTransport.AuthorityEndpoint, CurrentTransport.ConfiguredEndpoint);
        CurrentTransport.SharedSecretConfigured = !string.IsNullOrWhiteSpace(configuration.TransportSharedSecret);
        CurrentTransport.SharedSecretRequired = IsSharedSecretRequiredForConfiguredEndpoint();
        CurrentTransport.LastAuthOrProtocolError = lastAuthOrProtocolError;
        CurrentTransport.KnownParticipantCount = CurrentTransport.KnownParticipants.Count;
        UpdateTransportQueueDiagnostics();

        if (lastHubRosterPublish != null)
        {
            CurrentTransport.HubRosterPublishEpochId = lastHubRosterPublish.AuthorityEpochId;
            CurrentTransport.HubRosterPublishGeneration = lastHubRosterPublish.Generation;
            CurrentTransport.PublishedParticipantCount = DadHubRosterPublishRuntime.CountPublishedParticipants(lastHubRosterPublish);
        }
        else
        {
            CurrentTransport.HubRosterPublishEpochId = configuration.RunAsServerDad ? hubRosterAuthorityEpochId : string.Empty;
            CurrentTransport.HubRosterPublishGeneration = configuration.RunAsServerDad ? Volatile.Read(ref hubRosterGeneration) : 0;
            CurrentTransport.PublishedParticipantCount = 0;
        }
    }

    private bool IsSharedSecretRequiredForConfiguredEndpoint()
    {
        var host = configuration.RunAsServerDad
            ? configuration.ServerListenHost
            : configuration.ServerDadHost;
        return IsHostLikelyNonLoopback(host);
    }

    private static bool IsHostLikelyNonLoopback(string host)
    {
        var normalized = NormalizeHost(host).Trim();
        if (normalized.StartsWith("[", StringComparison.Ordinal) &&
            normalized.EndsWith("]", StringComparison.Ordinal) &&
            normalized.Length > 2)
        {
            normalized = normalized[1..^1];
        }

        if (string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        return !IPAddress.TryParse(normalized, out var address) ||
               DadHubProtocol.RequiresSharedSecret(address);
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static async Task<DadHubFrame?> ReadWithTimeoutAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => await DadHubProtocol.ReadFrameAsync(stream, cancellationToken)
            .WaitAsync(timeout, cancellationToken)
            .ConfigureAwait(false);

    private static async Task<IPAddress> ResolveAddressAsync(
        string host,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeHost(host);
        if (string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        if (IPAddress.TryParse(normalized, out var address))
            return address;

        var addresses = await Dns.GetHostAddressesAsync(normalized, cancellationToken).ConfigureAwait(false);
        return addresses.FirstOrDefault(static candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault()
               ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    private static string GetAdvertisedHost(string configuredHost, IPAddress boundAddress)
    {
        var host = NormalizeHost(configuredHost);
        if (host is "0.0.0.0" or "::" or "[::]")
            return Dns.GetHostName();
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback.ToString()
            : host;
    }

    private static string NormalizeHost(string host)
        => string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();

    private static int NormalizePort(int port)
        => port is > 0 and <= 65535 ? port : 4647;

    private static string FormatEndpoint(string host, int port)
    {
        var normalizedHost = NormalizeHost(host);
        var normalizedPort = NormalizePort(port);
        return normalizedHost.Contains(':')
            ? $"[{normalizedHost}]:{normalizedPort}"
            : $"{normalizedHost}:{normalizedPort}";
    }

    private static string GetBuildVersion()
        => typeof(DadTransportService).Assembly.GetName().Version?.ToString() ?? "unknown";

    private sealed class DadHubConnection
    {
        private readonly TcpClient client;
        private readonly SemaphoreSlim writeGate = new(1, 1);
        private readonly DadHubHandshakeState handshake;
        private readonly DadInboundRequestGate inboundRequestGate = new(MaxConcurrentInboundRequestsPerConnection);
        private readonly DadRuntimeReadinessTracker runtimeReadinessTracker = new();

        public DadHubConnection(
            TcpClient client,
            CancellationToken parentCancellation,
            DadHubHandshakeRole handshakeRole)
        {
            this.client = client;
            handshake = new DadHubHandshakeState(handshakeRole);
            Stream = client.GetStream();
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentCancellation);
        }

        public NetworkStream Stream { get; }
        public CancellationTokenSource Cancellation { get; }
        public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
        public DadWorkerSessionId RemoteWorkerSessionId { get; set; } = new(string.Empty);
        public string ClientInstanceId { get; set; } = string.Empty;
        public DadParticipantSnapshot Participant { get; set; } = new();
        public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastFrameReceivedUtc { get; private set; } = DateTime.UtcNow;
        public bool Replaced { get; set; }
        public bool IsOpen => !Cancellation.IsCancellationRequested && client.Connected;
        public bool IsRoutable => DadHubTransportRouting.IsRoutable(IsOpen, handshake);

        public void MarkHandshakeReady()
            => handshake.MarkReadyAfterHelloAck();

        public void MarkFrameReceived(DateTime receivedAtUtc)
            => LastFrameReceivedUtc = receivedAtUtc;

        public bool ObserveRuntimeReadiness(DadParticipantSnapshot participant, out long revision)
            => runtimeReadinessTracker.Observe(DadRuntimeReadinessSignature.Create(participant), out revision);

        public bool TryBeginInboundRequest()
            => inboundRequestGate.TryEnter();

        public void EndInboundRequest()
            => inboundRequestGate.Exit();

        public async Task SendAsync(
            DadHubFrame frame,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (!handshake.CanSend(frame.Kind))
            {
                throw new DadHubProtocolException(
                    "handshake-not-ready",
                    $"Dad hub frame {frame.Kind} cannot be sent before HelloAck completes.");
            }

            var acquired = false;
            try
            {
                using var queueCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    Cancellation.Token);
                await writeGate.WaitAsync(queueCancellation.Token).ConfigureAwait(false);
                acquired = true;
                using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    Cancellation.Token);
                if (timeout > TimeSpan.Zero)
                    writeCancellation.CancelAfter(timeout);
                await DadHubProtocol.WriteFrameAsync(Stream, frame, writeCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                    !Cancellation.IsCancellationRequested)
            {
                throw new DadHubProtocolException(
                    "write-timeout",
                    $"Dad hub write timed out after {timeout.TotalSeconds:F0}s.");
            }
            finally
            {
                if (acquired)
                    writeGate.Release();
            }
        }

        public void Close()
        {
            try
            {
                Cancellation.Cancel();
                client.Dispose();
            }
            catch
            {
            }
        }
    }

    private sealed class DisconnectedParticipant
    {
        public DadParticipantSnapshot Participant { get; set; } = new();
        public DateTime LastHeartbeatUtc { get; set; }
        public DateTime DisconnectedAtUtc { get; set; }
    }

    private sealed class CompletedOperation
    {
        public string PayloadJson { get; set; } = string.Empty;
        public DateTime CompletedAtUtc { get; set; }
    }

    // B7: immutable snapshot of the local roster catalog response plus the time it was built, so inbound
    // peer pulls can be served from it without re-running the heavy XADB fetch on the framework thread.
    private sealed class CachedLocalRosterCatalog
    {
        public DadPeerRosterCatalogResponse Response { get; init; } = new();
        public DateTime BuiltAtUtc { get; init; }
    }
}
