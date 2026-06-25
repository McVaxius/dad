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
    private const string MessageProfileCatalogRequest = "profile-catalog-request";
    private const string MessageProfileAggregateCatalogRequest = "profile-aggregate-catalog-request";
    private const string MessageProfileUpdateCommand = "profile-update-command";
    private const string MessageWorkerExecutionCommand = "worker-execution-command";
    private const string MessageWorkerExecutionStatus = "worker-execution-status";
    private const string MessageWorkerExecutionCancel = "worker-execution-cancel";

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CatalogRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxReconnectBackoff = TimeSpan.FromSeconds(10);
    private const int MaxConcurrentConnections = 32;

    private readonly Configuration configuration;
    private readonly DadPresenceService presenceService;
    private readonly DadClaimService claimService;
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
    private readonly ConcurrentDictionary<string, DadWorkerExecutionAck> workerCommandAcks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> nextRosterRefreshUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> nextProfileRefreshUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Action> frameworkCallbacks = new();
    private readonly SemaphoreSlim connectionSlots = new(MaxConcurrentConnections, MaxConcurrentConnections);
    private readonly DadBackgroundTaskObserver backgroundTasks;

    private CancellationTokenSource roleCancellation = new();
    private TcpListener? listener;
    private DadHubConnection? clientConnection;
    private DadParticipantSnapshot localParticipant;
    private DadParticipantSnapshot? serverParticipant;
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

    public DadTransportService(
        Configuration configuration,
        DadPresenceService presenceService,
        DadClaimService claimService,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.presenceService = presenceService;
        this.claimService = claimService;
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

        RestartTransport();
    }

    public bool IsReady { get; private set; }

    public DadPeerTransportSnapshot CurrentTransport { get; }

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

        return configuration.RunAsServerDad
            ? serverSessions.TryGet(workerSessionId, out var connection) && connection is { IsOpen: true }
            : clientConnection is { IsOpen: true } &&
              string.Equals(serverParticipant?.WorkerSessionId.Value, workerSessionId.Value, StringComparison.OrdinalIgnoreCase);
    }

    public void RestartListener() => RestartTransport();

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
            completedOperations.Clear();
            workerCommandAcks.Clear();
            IsReady = false;
            CurrentTransport.Availability = "Starting";
            CurrentTransport.LastRequestStatus = configuration.RunAsServerDad
                ? "Starting Server Dad listener."
                : "Connecting to Server Dad.";
        }

        previous.Cancel();
        previous.Dispose();
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

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        lifetimeCancellation.Cancel();
        roleCancellation.Cancel();
        listener?.Stop();
        CloseAllConnections("Dad transport disposed.");
        foreach (var pending in pendingRequests.Values)
            pending.TrySetCanceled();

        lifetimeCancellation.Dispose();
        roleCancellation.Dispose();
        backgroundTasks.Dispose();
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

        DrainFrameworkCallbacks();
        SweepDisconnectedParticipants();
        SweepCompletedOperations();

        if (DateTime.UtcNow >= nextHeartbeatUtc)
        {
            nextHeartbeatUtc = DateTime.UtcNow + HeartbeatInterval;
            if (configuration.RunAsServerDad)
            {
                foreach (var connection in serverSessions.Snapshot().Where(static connection => connection.IsOpen))
                    Track(SendHeartbeatAsync(connection, snapshot, roleCancellation.Token), "server heartbeat");
            }
            else if (clientConnection is { IsOpen: true } connection)
            {
                Track(SendHeartbeatAsync(connection, snapshot, roleCancellation.Token), "client heartbeat");
            }
        }

        if (configuration.RunAsServerDad)
            RefreshRemoteCatalogCaches();

        RefreshTransportSnapshot();
    }

    public DadPeerTransportSnapshot RequestSnapshots(DadPeerSnapshotRequest request)
    {
        RefreshTransportSnapshot();
        CurrentTransport.LastRequestUtc = DateTime.UtcNow;
        CurrentTransport.LastRequestStatus = CurrentTransport.LastResponses.Count == 0
            ? "No connected Dad workers."
            : $"Read {CurrentTransport.LastResponses.Count} worker snapshot(s) from Server Dad hub sessions.";
        return CurrentTransport;
    }

    public DadParticipantReadyDto? SendWakeRequest(DadParticipantSnapshot participant, DadWakeRequestDto request)
        => TryRequest<DadWakeRequestDto, DadParticipantReadyDto>(
            participant.WorkerSessionId,
            MessageWakeRequest,
            request,
            $"wake:{request.RunId}:{participant.WorkerSessionId.Value}:{request.AssignedSlotId}");

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
            "Forwarded run to Server Dad; awaiting authority status.");
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
            Summary = "Forwarded cancellation to Server Dad; awaiting authority status.",
        };
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
            Summary = "Profile update queued through Server Dad hub.",
        };
    }

    public DadWorkerExecutionAck? SendWorkerExecutionCommand(
        DadParticipantSnapshot participant,
        DadWorkerExecutionCommand command)
    {
        var operationKey = $"worker-command:{participant.WorkerSessionId.Value}:{command.CommandId}";
        var result = TryTakeCompleted<DadWorkerExecutionAck>(operationKey);
        if (result == null && !operations.ContainsKey(operationKey))
        {
            QueueOperation<DadWorkerExecutionCommand, DadWorkerExecutionAck>(
                operationKey,
                participant.WorkerSessionId,
                MessageWorkerExecutionCommand,
                command,
                ack => workerCommandAcks[BuildWorkerRunKey(participant.WorkerSessionId, command.RunId)] = ack);
        }

        if (result != null)
            workerCommandAcks[BuildWorkerRunKey(participant.WorkerSessionId, command.RunId)] = result;

        return result ?? new DadWorkerExecutionAck
        {
            CommandId = command.CommandId,
            RunId = command.RunId,
            WorkerSessionId = participant.WorkerSessionId,
            Accepted = true,
            Summary = "Worker command queued through Server Dad hub.",
            Status = new DadWorkerExecutionStatus
            {
                CommandId = command.CommandId,
                RunId = command.RunId,
                WorkerSessionId = participant.WorkerSessionId,
                Role = command.Role,
                State = DadWorkerExecutionState.Accepted,
                UpdatedAtUtc = DateTime.UtcNow,
                Summary = "Awaiting Client Dad acknowledgement.",
            },
        };
    }

    public DadWorkerExecutionStatus? GetWorkerExecutionStatus(DadParticipantSnapshot participant)
    {
        var runKey = BuildWorkerRunKey(participant.WorkerSessionId, participant.RunId);
        if (workerCommandAcks.TryGetValue(runKey, out var ack) && !ack.Accepted)
        {
            var failed = ack.Status.Clone();
            failed.RunId = participant.RunId;
            failed.WorkerSessionId = participant.WorkerSessionId;
            failed.State = DadWorkerExecutionState.Failed;
            failed.IsTerminal = true;
            failed.Success = false;
            failed.FailureReason = ack.Summary;
            failed.Summary = ack.Summary;
            failed.UpdatedAtUtc = DateTime.UtcNow;
            return failed;
        }

        return TryRequest<string, DadWorkerExecutionStatus>(
            participant.WorkerSessionId,
            MessageWorkerExecutionStatus,
            participant.RunId,
            $"worker-status:{participant.WorkerSessionId.Value}:{participant.RunId}");
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
                SetTransportError("Server Dad requires a shared secret for non-loopback listeners.");
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
            CurrentTransport.ConnectionStatus = $"Server Dad listening on {CurrentTransport.ListenerEndpoint}.";
            CurrentTransport.LastRequestStatus = CurrentTransport.ConnectionStatus;
            IsReady = true;

            while (!cancellationToken.IsCancellationRequested)
            {
                await connectionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var client = await activeListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    Track(HandleServerClientWithReleaseAsync(client, cancellationToken), "server client session");
                }
                catch
                {
                    connectionSlots.Release();
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
            SetTransportError($"Server Dad listener failed: {ex.Message}");
            log.Error(ex, "[dad] Server Dad listener failed.");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
                IsReady = false;
        }
    }

    private async Task HandleServerClientWithReleaseAsync(
        TcpClient client,
        CancellationToken serverCancellation)
    {
        try
        {
            await HandleServerClientAsync(client, serverCancellation).ConfigureAwait(false);
        }
        finally
        {
            connectionSlots.Release();
        }
    }

    private async Task HandleServerClientAsync(TcpClient client, CancellationToken serverCancellation)
    {
        DadHubConnection? connection = null;
        try
        {
            client.NoDelay = true;
            connection = new DadHubConnection(client, serverCancellation);
            var helloFrame = await ReadWithTimeoutAsync(connection.Stream, ConnectTimeout, connection.Cancellation.Token)
                .ConfigureAwait(false);
            if (helloFrame == null)
                throw new DadHubProtocolException("hello-missing", "Client Dad closed before sending hello.");

            if (helloFrame.ProtocolVersion != DadHubProtocol.CurrentVersion)
            {
                await connection.SendAsync(
                    DadHubProtocol.CreateError(
                        presenceService.WorkerSessionId,
                        helloFrame.SourceWorkerSessionId,
                        helloFrame.CorrelationId,
                        "protocol-mismatch",
                        $"Dad hub protocol {helloFrame.ProtocolVersion} is incompatible; expected {DadHubProtocol.CurrentVersion}.",
                        configuration.TransportSharedSecret),
                    connection.Cancellation.Token).ConfigureAwait(false);
                return;
            }

            try
            {
                DadHubProtocol.ValidateFrame(helloFrame, configuration.TransportSharedSecret);
            }
            catch (DadHubProtocolException ex)
            {
                await connection.SendAsync(
                    DadHubProtocol.CreateError(
                        presenceService.WorkerSessionId,
                        helloFrame.SourceWorkerSessionId,
                        helloFrame.CorrelationId,
                        ex.Code,
                        ex.Message,
                        configuration.TransportSharedSecret),
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
            connection.LastHeartbeatUtc = DateTime.UtcNow;
            RegisterServerSession(connection);

            var ack = new DadHubHello
            {
                ClientInstanceId = presenceService.ClientInstanceId,
                WorkerSessionId = presenceService.WorkerSessionId,
                BuildVersion = GetBuildVersion(),
                Participant = GetLocalParticipant(),
            };
            await connection.SendAsync(
                DadHubProtocol.CreateFrame(
                    DadHubFrameKind.HelloAck,
                    presenceService.WorkerSessionId,
                    hello.WorkerSessionId,
                    "hello",
                    helloFrame.CorrelationId,
                    DadIpcJson.Serialize(ack),
                    configuration.TransportSharedSecret),
                connection.Cancellation.Token).ConfigureAwait(false);

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
                var address = await ResolveAddressAsync(host, cancellationToken).ConfigureAwait(false);
                try
                {
                    DadHubProtocol.RequireSharedSecretForAddress(address, configuration.TransportSharedSecret);
                }
                catch (DadHubProtocolException ex)
                {
                    SetTransportError("Client Dad requires a shared secret for non-loopback Server Dad connections.");
                    log.Warning("[dad] {Code}: {Message}", ex.Code, ex.Message);
                    return;
                }

                using var client = new TcpClient(address.AddressFamily) { NoDelay = true };
                await client.ConnectAsync(address, port, cancellationToken)
                    .AsTask()
                    .WaitAsync(ConnectTimeout, cancellationToken)
                    .ConfigureAwait(false);

                var connection = new DadHubConnection(client, cancellationToken)
                {
                    WorkerSessionId = presenceService.WorkerSessionId,
                    ClientInstanceId = presenceService.ClientInstanceId,
                };
                activeConnection = connection;
                clientConnection = connection;

                var hello = new DadHubHello
                {
                    ClientInstanceId = presenceService.ClientInstanceId,
                    WorkerSessionId = presenceService.WorkerSessionId,
                    BuildVersion = GetBuildVersion(),
                    Participant = GetLocalParticipant(),
                };
                var correlationId = Guid.NewGuid().ToString("N");
                await connection.SendAsync(
                    DadHubProtocol.CreateFrame(
                        DadHubFrameKind.Hello,
                        presenceService.WorkerSessionId,
                        new DadWorkerSessionId(string.Empty),
                        "hello",
                        correlationId,
                        DadIpcJson.Serialize(hello),
                        configuration.TransportSharedSecret),
                    connection.Cancellation.Token).ConfigureAwait(false);

                var response = await ReadWithTimeoutAsync(
                        connection.Stream,
                        ConnectTimeout,
                        connection.Cancellation.Token)
                    .ConfigureAwait(false);
                if (response == null)
                    throw new DadHubProtocolException("hello-missing", "Server Dad closed before hello acknowledgement.");
                if (response.Kind == DadHubFrameKind.Error)
                    throw new DadHubProtocolException(response.ErrorCode, response.ErrorMessage);

                DadHubProtocol.ValidateFrame(response, configuration.TransportSharedSecret);
                if (response.Kind != DadHubFrameKind.HelloAck)
                    throw new DadHubProtocolException("hello-invalid", "Server Dad did not return hello acknowledgement.");

                var serverHello = DadIpcJson.Deserialize<DadHubHello>(response.PayloadJson)
                                  ?? throw new DadHubProtocolException("hello-invalid", "Server Dad hello payload is invalid.");
                serverParticipant = DadHubParticipants.PrepareRemote(serverHello.Participant, DateTime.UtcNow);
                connection.RemoteWorkerSessionId = serverHello.WorkerSessionId;
                connection.Participant = serverParticipant.Clone();
                connection.LastHeartbeatUtc = DateTime.UtcNow;
                attempt = 0;
                nextHeartbeatUtc = DateTime.MinValue;
                IsReady = true;
                CurrentTransport.Availability = "Ready";
                CurrentTransport.ConnectionStatus = $"Connected to Server Dad at {FormatEndpoint(host, port)}.";
                CurrentTransport.LastRequestStatus = CurrentTransport.ConnectionStatus;
                RefreshTransportSnapshot();

                await RunConnectionReaderAsync(connection, isServerSide: false).ConfigureAwait(false);
            }
            catch (DadHubProtocolException ex)
            {
                SetTransportError($"{ex.Code}: {ex.Message}");
                log.Warning("[dad] Server Dad connection rejected: {Code}: {Message}", ex.Code, ex.Message);
            }
            catch (TimeoutException)
            {
                SetTransportError("Timed out connecting to Server Dad.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (DadBackgroundTaskObserver.IsExpectedShutdownException(ex))
            {
                return;
            }
            catch (Exception ex)
            {
                SetTransportError($"Server Dad connection failed: {ex.Message}");
                log.Debug(ex, "[dad] Server Dad connection failed.");
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
                RefreshTransportSnapshot();
            }

            attempt++;
            var backoffSeconds = Math.Min(MaxReconnectBackoff.TotalSeconds, Math.Pow(2, Math.Min(attempt - 1, 4)));
            CurrentTransport.ReconnectAttempt = attempt;
            CurrentTransport.ConnectionStatus = $"Disconnected; reconnecting in {backoffSeconds:F0}s.";
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
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
                case DadHubFrameKind.Request:
                    await HandleInboundRequestAsync(connection, frame, isServerSide).ConfigureAwait(false);
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

    private void HandleHeartbeat(DadHubConnection connection, DadHubFrame frame, bool isServerSide)
    {
        var heartbeat = DadIpcJson.Deserialize<DadHubHeartbeat>(frame.PayloadJson);
        if (heartbeat == null)
            return;

        var now = DateTime.UtcNow;
        connection.LastHeartbeatUtc = now;
        connection.Participant = DadHubParticipants.PrepareRemote(heartbeat.Participant, now);
        if (isServerSide)
        {
            disconnectedParticipants.TryRemove(connection.WorkerSessionId.Value, out _);
        }
        else
        {
            serverParticipant = connection.Participant.Clone();
        }
    }

    private async Task HandleInboundRequestAsync(
        DadHubConnection origin,
        DadHubFrame request,
        bool isServerSide)
    {
        DadHubFrame response;
        try
        {
            if (isServerSide &&
                !request.TargetWorkerSessionId.IsEmpty &&
                !string.Equals(
                    request.TargetWorkerSessionId.Value,
                    presenceService.WorkerSessionId.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                response = await ForwardRequestAsync(request, origin.Cancellation.Token).ConfigureAwait(false);
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

        await origin.SendAsync(response, origin.Cancellation.Token).ConfigureAwait(false);
    }

    private async Task<DadHubFrame> ForwardRequestAsync(
        DadHubFrame request,
        CancellationToken cancellationToken)
    {
        if (!serverSessions.TryGet(request.TargetWorkerSessionId, out var target) || target is not { IsOpen: true })
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
            _ => throw new InvalidOperationException($"Unsupported Dad hub message type '{messageType}'."),
        };
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
               ?? DadRunResult.Rejected(null, "Server Dad cancel handler unavailable.");
    }

    private DadRunResult HandleStatusQuery()
    {
        var result = statusProvider?.Invoke()
                     ?? DadRunResult.Rejected(null, "Server Dad status unavailable.");
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
            return DadRunResult.Rejected(request, "Only Server Dad accepts remote run starts.");
        if (!remoteMutationsAllowed)
            return DadRunResult.Rejected(request, BuildRemoteMutationRejectedReason("remote start command"));

        return startRunHandler?.Invoke(request)
               ?? DadRunResult.Rejected(request, "Server Dad start handler unavailable.");
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
        var catalog = rosterCatalogProvider?.Invoke() ?? new DadAccountRosterCatalog
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

        if (targetWorkerSessionId.IsEmpty || operations.ContainsKey(operationKey))
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
        var aggregate = CreateRosterAggregate(plan.PlanId);
        aggregate.Responses.Add(BuildLocalRosterCatalogResponse(plan));

        var target = ResolveAuthorityWorkerSessionId();
        if (target.IsEmpty)
        {
            AddWarning(aggregate.Warnings, "Server Dad is not connected; roster catalog refresh returned local catalog only.");
            FinalizeRosterAggregate(aggregate, expectedCatalogCount: 1);
            return aggregate;
        }

        var request = new DadAggregateRosterCatalogRequest
        {
            RequestId = plan.PlanId,
            RequestingWorkerSessionId = presenceService.WorkerSessionId,
            IncludeRequester = false,
            Plan = CloneRosterRefreshPlan(plan),
        };
        var serverAggregate = TryDirectRequest<DadAggregateRosterCatalogRequest, DadAggregateRosterCatalogResponse>(
            target,
            MessageRosterAggregateCatalogRequest,
            request,
            out var error);
        if (serverAggregate == null)
        {
            AddWarning(aggregate.Warnings, string.IsNullOrWhiteSpace(error)
                ? "Server Dad aggregate roster catalog refresh did not return a response."
                : error);
            FinalizeRosterAggregate(aggregate, expectedCatalogCount: 1);
            return aggregate;
        }

        MergeRosterAggregate(aggregate, serverAggregate, excludeLocal: true);
        aggregate.PendingCatalogCount += serverAggregate.PendingCatalogCount;
        aggregate.TimedOutCatalogCount += serverAggregate.TimedOutCatalogCount;
        FinalizeRosterAggregate(aggregate, expectedCatalogCount: 1 + serverAggregate.ExpectedCatalogCount);
        return aggregate;
    }

    private DadAggregateRosterCatalogResponse BuildServerRosterAggregate(
        DadRosterRefreshPlan plan,
        DadWorkerSessionId requestingWorkerSessionId,
        bool includeRequester)
    {
        var aggregate = CreateRosterAggregate(plan.PlanId);
        var skipRequester = !requestingWorkerSessionId.IsEmpty && !includeRequester
            ? requestingWorkerSessionId.Value
            : string.Empty;

        if (!string.Equals(skipRequester, presenceService.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase))
            aggregate.Responses.Add(BuildLocalRosterCatalogResponse(plan));

        var targets = serverSessions.Snapshot()
            .Where(static connection => connection.IsOpen)
            .Where(connection => string.IsNullOrWhiteSpace(skipRequester) ||
                                 !string.Equals(connection.WorkerSessionId.Value, skipRequester, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var tasks = targets
            .Select(connection => RequestRosterCatalogDirectAsync(connection.WorkerSessionId, CloneRosterRefreshPlan(plan)))
            .ToList();
        WaitForAggregateTasks(tasks);

        AddRosterTaskResponses(aggregate, tasks);
        FinalizeRosterAggregate(aggregate, aggregate.Responses.Count + targets.Count - tasks.Count(static task => task.IsCompletedSuccessfully && task.Result != null));
        return aggregate;
    }

    private DadAggregateProfileCatalogResponse BuildClientProfileAggregate(string requestId)
    {
        var aggregate = CreateProfileAggregate(requestId);
        aggregate.Responses.Add(BuildLocalProfileCatalogResponse(requestId));

        var target = ResolveAuthorityWorkerSessionId();
        if (target.IsEmpty)
        {
            AddWarning(aggregate.Warnings, "Server Dad is not connected; profile catalog refresh returned local catalog only.");
            FinalizeProfileAggregate(aggregate, expectedCatalogCount: 1);
            return aggregate;
        }

        var request = new DadAggregateProfileCatalogRequest
        {
            RequestId = requestId,
            RequestingWorkerSessionId = presenceService.WorkerSessionId,
            IncludeRequester = false,
        };
        var serverAggregate = TryDirectRequest<DadAggregateProfileCatalogRequest, DadAggregateProfileCatalogResponse>(
            target,
            MessageProfileAggregateCatalogRequest,
            request,
            out var error);
        if (serverAggregate == null)
        {
            AddWarning(aggregate.Warnings, string.IsNullOrWhiteSpace(error)
                ? "Server Dad aggregate profile catalog refresh did not return a response."
                : error);
            FinalizeProfileAggregate(aggregate, expectedCatalogCount: 1);
            return aggregate;
        }

        MergeProfileAggregate(aggregate, serverAggregate, excludeLocal: true);
        aggregate.PendingCatalogCount += serverAggregate.PendingCatalogCount;
        aggregate.TimedOutCatalogCount += serverAggregate.TimedOutCatalogCount;
        FinalizeProfileAggregate(aggregate, expectedCatalogCount: 1 + serverAggregate.ExpectedCatalogCount);
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
            .Where(static connection => connection.IsOpen)
            .Where(connection => string.IsNullOrWhiteSpace(skipRequester) ||
                                 !string.Equals(connection.WorkerSessionId.Value, skipRequester, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var tasks = targets
            .Select(connection => RequestProfileCatalogDirectAsync(connection.WorkerSessionId, requestId))
            .ToList();
        WaitForAggregateTasks(tasks);

        AddProfileTaskResponses(aggregate, tasks);
        FinalizeProfileAggregate(aggregate, aggregate.Responses.Count + targets.Count - tasks.Count(static task => task.IsCompletedSuccessfully && task.Result != null));
        return aggregate;
    }

    private async Task<DadPeerRosterCatalogResponse?> RequestRosterCatalogDirectAsync(
        DadWorkerSessionId workerSessionId,
        DadRosterRefreshPlan plan)
    {
        var frame = await SendRequestAsync(
                workerSessionId,
                MessageRosterCatalogRequest,
                DadIpcJson.Serialize(plan),
                roleCancellation.Token)
            .ConfigureAwait(false);
        if (frame.Kind == DadHubFrameKind.Error)
            throw new DadHubProtocolException(frame.ErrorCode, frame.ErrorMessage);

        return DadIpcJson.Deserialize<DadPeerRosterCatalogResponse>(frame.PayloadJson);
    }

    private async Task<DadProfileCatalogResponse?> RequestProfileCatalogDirectAsync(
        DadWorkerSessionId workerSessionId,
        string requestId)
    {
        var frame = await SendRequestAsync(
                workerSessionId,
                MessageProfileCatalogRequest,
                DadIpcJson.Serialize(requestId),
                roleCancellation.Token)
            .ConfigureAwait(false);
        if (frame.Kind == DadHubFrameKind.Error)
            throw new DadHubProtocolException(frame.ErrorCode, frame.ErrorMessage);

        return DadIpcJson.Deserialize<DadProfileCatalogResponse>(frame.PayloadJson);
    }

    private TResponse? TryDirectRequest<TRequest, TResponse>(
        DadWorkerSessionId targetWorkerSessionId,
        string messageType,
        TRequest request,
        out string error)
    {
        error = string.Empty;
        try
        {
            var frame = SendRequestAsync(
                    targetWorkerSessionId,
                    messageType,
                    DadIpcJson.Serialize(request),
                    roleCancellation.Token)
                .GetAwaiter()
                .GetResult();
            if (frame.Kind == DadHubFrameKind.Error)
            {
                error = $"{messageType} failed: {frame.ErrorMessage}";
                return default;
            }

            return DadIpcJson.Deserialize<TResponse>(frame.PayloadJson);
        }
        catch (Exception ex) when (!DadBackgroundTaskObserver.IsExpectedShutdownException(ex))
        {
            error = $"{messageType} failed: {ex.Message}";
            log.Debug(ex, "[dad] Direct hub request {MessageType} failed for {WorkerSessionId}.", messageType, targetWorkerSessionId);
            return default;
        }
    }

    private static void WaitForAggregateTasks<TResponse>(IReadOnlyList<Task<TResponse?>> tasks)
    {
        if (tasks.Count == 0)
            return;

        try
        {
            Task.WaitAll(tasks.Cast<Task>().ToArray(), RequestTimeout + TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            // Individual task faults are converted into aggregate warnings below.
        }
    }

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

    private void AddRosterTaskResponses(
        DadAggregateRosterCatalogResponse aggregate,
        IReadOnlyList<Task<DadPeerRosterCatalogResponse?>> tasks)
    {
        foreach (var task in tasks)
        {
            if (!task.IsCompleted)
            {
                aggregate.PendingCatalogCount++;
                AddWarning(aggregate.Warnings, "Roster catalog refresh is still pending for one connected Dad.");
                continue;
            }

            if (task.IsCanceled)
            {
                aggregate.TimedOutCatalogCount++;
                AddWarning(aggregate.Warnings, "Roster catalog refresh was cancelled for one connected Dad.");
                continue;
            }

            if (task.IsFaulted)
            {
                var message = task.Exception?.GetBaseException().Message ?? "Roster catalog refresh failed for one connected Dad.";
                if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                    aggregate.TimedOutCatalogCount++;
                AddWarning(aggregate.Warnings, message);
                continue;
            }

            if (task.Result == null)
            {
                AddWarning(aggregate.Warnings, "Roster catalog refresh returned an empty response for one connected Dad.");
                continue;
            }

            aggregate.Responses.Add(task.Result);
            rosterCatalogs[task.Result.WorkerSessionId.Value] = task.Result;
        }
    }

    private void AddProfileTaskResponses(
        DadAggregateProfileCatalogResponse aggregate,
        IReadOnlyList<Task<DadProfileCatalogResponse?>> tasks)
    {
        foreach (var task in tasks)
        {
            if (!task.IsCompleted)
            {
                aggregate.PendingCatalogCount++;
                AddWarning(aggregate.Warnings, "Profile catalog refresh is still pending for one connected Dad.");
                continue;
            }

            if (task.IsCanceled)
            {
                aggregate.TimedOutCatalogCount++;
                AddWarning(aggregate.Warnings, "Profile catalog refresh was cancelled for one connected Dad.");
                continue;
            }

            if (task.IsFaulted)
            {
                var message = task.Exception?.GetBaseException().Message ?? "Profile catalog refresh failed for one connected Dad.";
                if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                    aggregate.TimedOutCatalogCount++;
                AddWarning(aggregate.Warnings, message);
                continue;
            }

            if (task.Result == null)
            {
                AddWarning(aggregate.Warnings, "Profile catalog refresh returned an empty response for one connected Dad.");
                continue;
            }

            aggregate.Responses.Add(task.Result);
            var workerId = task.Result.Catalog.OwnerWorkerSessionId.Value;
            if (!string.IsNullOrWhiteSpace(workerId))
                profileCatalogs[workerId] = task.Result;
        }
    }

    private void MergeRosterAggregate(
        DadAggregateRosterCatalogResponse target,
        DadAggregateRosterCatalogResponse source,
        bool excludeLocal)
    {
        foreach (var response in source.Responses)
        {
            if (excludeLocal &&
                string.Equals(response.WorkerSessionId.Value, presenceService.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            target.Responses.Add(response);
            rosterCatalogs[response.WorkerSessionId.Value] = response;
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
                profileCatalogs[workerId] = response;
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
            IncludeHidden = source.IncludeHidden,
            IncludeIgnored = source.IncludeIgnored,
            StaleAfterHours = source.StaleAfterHours,
            CharacterRefs = source.CharacterRefs.Select(static reference => new DadRosterCharacterRef
            {
                AccountKey = reference.AccountKey,
                CharacterKey = reference.CharacterKey,
                ContentId = reference.ContentId,
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

    private void QueueOperation<TRequest, TResponse>(
        string operationKey,
        DadWorkerSessionId targetWorkerSessionId,
        string messageType,
        TRequest request,
        Action<TResponse>? completed)
    {
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
                if (typed != null)
                    frameworkCallbacks.Enqueue(() => completed(typed));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (DadBackgroundTaskObserver.IsExpectedShutdownException(ex))
        {
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
        CancellationToken cancellationToken)
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
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DadHubFrame> SendRequestFrameAsync(
        DadHubConnection connection,
        DadWorkerSessionId targetWorkerSessionId,
        string messageType,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<DadHubFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingRequests.TryAdd(correlationId, completion))
            throw new InvalidOperationException("Dad hub correlation id collision.");

        try
        {
            await connection.SendAsync(
                DadHubProtocol.CreateFrame(
                    DadHubFrameKind.Request,
                    presenceService.WorkerSessionId,
                    targetWorkerSessionId,
                    messageType,
                    correlationId,
                    payloadJson,
                    configuration.TransportSharedSecret),
                cancellationToken).ConfigureAwait(false);

            return await completion.Task.WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
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

    private DadHubConnection? ResolveConnection(DadWorkerSessionId targetWorkerSessionId)
    {
        if (configuration.RunAsServerDad)
            return serverSessions.TryGet(targetWorkerSessionId, out var session) && session is { IsOpen: true }
                ? session
                : null;

        return clientConnection is { IsOpen: true } connection ? connection : null;
    }

    private DadWorkerSessionId ResolveAuthorityWorkerSessionId()
    {
        if (configuration.RunAsServerDad)
            return presenceService.WorkerSessionId;

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
    }

    private void MarkServerSessionDisconnected(DadHubConnection connection)
    {
        connection.Close();
        if (connection.Replaced)
            return;

        if (serverSessions.RemoveIfCurrent(connection.WorkerSessionId, connection))
        {
            disconnectedParticipants[connection.WorkerSessionId.Value] = new DisconnectedParticipant
            {
                Participant = connection.Participant.Clone(),
                DisconnectedAtUtc = DateTime.UtcNow,
                LastHeartbeatUtc = connection.LastHeartbeatUtc,
            };
            CurrentTransport.LastRequestStatus = $"Client Dad {connection.WorkerSessionId} disconnected.";
        }
    }

    private async Task SendHeartbeatAsync(
        DadHubConnection connection,
        DadParticipantSnapshot participant,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendAsync(
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
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (DadBackgroundTaskObserver.IsExpectedShutdownException(ex))
        {
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[dad] Client Dad heartbeat failed.");
            connection.Close();
        }
    }

    private void RefreshRemoteCatalogCaches()
    {
        foreach (var connection in serverSessions.Snapshot().Where(static connection => connection.IsOpen))
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
        if (!force &&
            nextRosterRefreshUtc.TryGetValue(workerId, out var nextRefresh) &&
            DateTime.UtcNow < nextRefresh)
        {
            return;
        }

        nextRosterRefreshUtc[workerId] = DateTime.UtcNow + CatalogRefreshInterval;
        var key = $"catalog-roster:{workerId}";
        if (operations.ContainsKey(key))
            return;

        request.PlanId = string.IsNullOrWhiteSpace(request.PlanId)
            ? Guid.NewGuid().ToString("N")
            : request.PlanId;
        QueueOperation<DadRosterRefreshPlan, DadPeerRosterCatalogResponse>(
            key,
            connection.WorkerSessionId,
            MessageRosterCatalogRequest,
            request,
            response => rosterCatalogs[workerId] = response);
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

        nextProfileRefreshUtc[workerId] = DateTime.UtcNow + CatalogRefreshInterval;
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
                profileCatalogs[workerId] = response;
            });
    }

    private void RefreshTransportSnapshot()
    {
        var now = DateTime.UtcNow;
        var staleAfter = TimeSpan.FromSeconds(Math.Max(3, configuration.HeartbeatStaleSeconds));
        var participants = new List<DadParticipantSnapshot>();

        if (configuration.RunAsServerDad)
        {
            RefreshLocalMutationState();
            var local = GetLocalParticipant();
            local.Endpoint = CurrentTransport.ListenerEndpoint;
            local.IsLocalClient = true;
            local.IsAuthority = true;
            if (!remoteMutationsAllowed)
                MarkSnapshotUnavailable(local, BuildLocalUnavailableReason());
            participants.Add(local);

            foreach (var connection in serverSessions.Snapshot())
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
                if (participant.State != DadParticipantState.Stale)
                    participant.StatusText = "Client Dad connection lost; waiting for reconnect.";
                participants.Add(participant);
            }

            CurrentTransport.ListenerEndpoint = FormatEndpoint(
                configuration.ServerListenHost,
                configuration.ServerListenPort);
            CurrentTransport.AuthorityEndpoint = CurrentTransport.ListenerEndpoint;
            CurrentTransport.AuthorityWorkerSessionId = presenceService.WorkerSessionId;
            CurrentTransport.AuthorityRole = DadWorkerRole.ServerDad;
        }
        else if (serverParticipant != null)
        {
            var participant = DadHubParticipants.PrepareRemoteWithStaleState(
                serverParticipant,
                clientConnection?.LastHeartbeatUtc ?? serverParticipant.LastHeartbeatUtc,
                now,
                clientConnection is { IsOpen: true } ? TimeSpan.MaxValue : staleAfter,
                "Server Dad connection lost.");

            participants.Add(participant);
            CurrentTransport.ListenerEndpoint = string.Empty;
            CurrentTransport.AuthorityEndpoint = FormatEndpoint(
                configuration.ServerDadHost,
                configuration.ServerDadPort);
            CurrentTransport.AuthorityWorkerSessionId = participant.WorkerSessionId;
            CurrentTransport.AuthorityRole = DadWorkerRole.ServerDad;
        }

        CurrentTransport.KnownParticipants = participants
            .DistinctBy(static participant => participant.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static participant => participant.ManagedAccountAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.ActiveCharacterKey.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        CurrentTransport.ConnectedPeerCount = configuration.RunAsServerDad
            ? serverSessions.Snapshot().Count(static connection => connection.IsOpen)
            : clientConnection is { IsOpen: true } ? 1 : 0;
        CurrentTransport.LastResponses = CurrentTransport.KnownParticipants
            .Select(static participant => new DadPeerSnapshotResponse
            {
                RespondedAtUtc = participant.LastHeartbeatUtc,
                ClientInstanceId = participant.ClientInstanceId,
                ProcessId = participant.ProcessId,
                Character = participant.Character.Clone(),
                Participant = participant.Clone(),
                XadbReady = participant.Character.XadbReady,
            })
            .ToList();
        CurrentTransport.TransportMode = localOnlyModeEnabled
            ? DadTransportMode.LocalOnly
            : DadTransportMode.ServerHub;
        CurrentTransport.AuthorityStatus = CurrentTransport.AuthorityWorkerSessionId.IsEmpty
            ? "Authority not connected."
            : DadStatusText.FormatAuthorityStatus(
                CurrentTransport.AuthorityRole,
                CurrentTransport.AuthorityWorkerSessionId,
                CurrentTransport.AuthorityEndpoint,
                DadAuthorityMode.ServerDad);
    }

    private void SweepDisconnectedParticipants()
    {
        var retention = TimeSpan.FromSeconds(Math.Max(15, configuration.HeartbeatStaleSeconds * 3));
        foreach (var pair in disconnectedParticipants)
        {
            if (DateTime.UtcNow - pair.Value.DisconnectedAtUtc >= retention)
                disconnectedParticipants.TryRemove(pair.Key, out _);
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
            .Where(static connection => connection.IsOpen)
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
        while (frameworkCallbacks.TryDequeue(out var callback))
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[dad] Hub completion callback failed.");
            }
        }
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
    }

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

    private static string BuildWorkerRunKey(DadWorkerSessionId workerSessionId, string runId)
        => $"{workerSessionId.Value}|{runId}";

    private sealed class DadHubConnection
    {
        private readonly TcpClient client;
        private readonly SemaphoreSlim writeGate = new(1, 1);

        public DadHubConnection(TcpClient client, CancellationToken parentCancellation)
        {
            this.client = client;
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
        public bool Replaced { get; set; }
        public bool IsOpen => !Cancellation.IsCancellationRequested && client.Connected;

        public async Task SendAsync(DadHubFrame frame, CancellationToken cancellationToken)
        {
            await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DadHubProtocol.WriteFrameAsync(Stream, frame, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
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
}
