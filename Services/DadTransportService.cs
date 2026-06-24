using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Services;
using dad.Models;

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
    private const string MessageRosterRefreshCommand = "roster-refresh-command";
    private const string MessageProfileCatalogRequest = "profile-catalog-request";
    private const string MessageProfileUpdateCommand = "profile-update-command";
    private const string MessageWorkerExecutionCommand = "worker-execution-command";
    private const string MessageWorkerExecutionStatus = "worker-execution-status";
    private const string MessageWorkerExecutionCancel = "worker-execution-cancel";
    private static readonly TimeSpan RegistryFreshness = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HeartbeatWriteInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SocketTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StaleHeartbeatThreshold = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RegistryCollisionWarningInterval = TimeSpan.FromSeconds(30);

    private readonly Configuration configuration;
    private readonly DadPresenceService presenceService;
    private readonly DadClaimService claimService;
    private readonly IPluginLog log;
    private readonly string registryDirectory;
    private readonly string registryFilePath;
    private readonly Dictionary<string, DadTransportRegistryEntry> cachedRegistryEntriesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly DadRegistryWorker registryWorker;
    private readonly CancellationTokenSource cancellation = new();
    private const int MaxConcurrentClients = 32;       // Review L4
    private const int MaxConcurrentRecurringPeerCalls = 4;
    private const int MaxRequestChars = 256 * 1024;    // Review M2
    private readonly SemaphoreSlim clientSlots = new(MaxConcurrentClients, MaxConcurrentClients);
    private readonly SemaphoreSlim recurringPeerSlots = new(MaxConcurrentRecurringPeerCalls, MaxConcurrentRecurringPeerCalls);
    private readonly ConcurrentDictionary<Task, byte> activeClientTasks = new();
    private readonly ConcurrentDictionary<string, byte> activeRecurringEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? listener;
    private Task? acceptLoopTask;
    private DateTime nextHeartbeatWriteUtc = DateTime.MinValue;
    private static readonly TimeSpan AuthorityQueryFailureBackoff = TimeSpan.FromSeconds(10); // Review H1/M4
    private string lastFailedAuthorityEndpoint = string.Empty;
    private DateTime nextAuthorityQueryRetryUtc = DateTime.MinValue;
    private bool localAdvertisementActive;
    private bool localAdvertisementInitialized;
    private bool localPluginEnabled;
    private bool localOnlyModeEnabled;
    private bool remoteMutationsAllowed;
    private string localAdvertisementPauseReason = string.Empty;
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

    public DadTransportService(Configuration configuration, DadPresenceService presenceService, DadClaimService claimService, IPluginLog log)
    {
        this.configuration = configuration;
        this.presenceService = presenceService;
        this.claimService = claimService;
        this.log = log;
        // Review M3: use the per-user plugin config directory (shared across this user's game instances
        // for multibox discovery, but not world-readable like %TEMP%) instead of the shared temp root.
        registryDirectory = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "orchestrator-registry");
        Directory.CreateDirectory(registryDirectory);
        registryFilePath = Path.Combine(registryDirectory, $"{presenceService.ClientInstanceId}.json");
        registryWorker = new DadRegistryWorker(registryDirectory, registryFilePath, presenceService.ClientInstanceId, log);

        CurrentTransport = new DadPeerTransportSnapshot
        {
            Availability = "Starting",
            TransportMode = DadTransportMode.LocalhostHybrid,
            DiscoveryDirectory = registryDirectory,
            LocalClientInstanceId = presenceService.ClientInstanceId,
            LocalWorkerSessionId = presenceService.WorkerSessionId,
        };

        StartListener();
    }

    public bool IsReady { get; private set; }

    public DadPeerTransportSnapshot CurrentTransport { get; private set; }

    public string GetConfiguredAuthorityEndpoint()
        => TryBuildConfiguredAuthorityEndpoint(out var endpoint) ? endpoint : string.Empty;

    public string GetPreferredAuthorityEndpoint()
    {
        var configuredEndpoint = GetConfiguredAuthorityEndpoint();
        return !string.IsNullOrWhiteSpace(configuredEndpoint)
            ? configuredEndpoint
            : CurrentTransport.AuthorityEndpoint;
    }

    public void RestartListener()
    {
        StopListener();
        StartListener();
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
        try
        {
            cancellation.Cancel();
            StopListener();
            // Review M11: drain in-flight client handlers (bounded) so they don't run against disposed services.
            try { Task.WaitAll(activeClientTasks.Keys.ToArray(), TimeSpan.FromSeconds(2)); }
            catch { /* best-effort drain */ }
        }
        catch
        {
            // Best-effort shutdown only.
        }

        try { registryWorker.Dispose(); } catch { /* ignore */ }

        // Review M11: dispose owned synchronization primitives.
        try { cancellation.Dispose(); } catch { /* ignore */ }
        try { clientSlots.Dispose(); } catch { /* ignore */ }
        try { recurringPeerSlots.Dispose(); } catch { /* ignore */ }
    }

    public void UpdateHeartbeat(DadParticipantSnapshot localParticipant, bool pluginEnabled, bool localOnlyModeEnabled)
    {
        UpdateLocalAvailability(pluginEnabled, localOnlyModeEnabled);
        registryWorker.EnsureReadScheduled();

        if (!remoteMutationsAllowed)
        {
            PauseLocalAdvertisement(BuildLocalUnavailableReason());
            RefreshKnownParticipants();
            return;
        }

        ResumeLocalAdvertisement();
        var now = DateTime.UtcNow;

        if (!IsReady || now < nextHeartbeatWriteUtc)
        {
            RefreshKnownParticipants();
            return;
        }

        var entry = new DadTransportRegistryEntry
        {
            ClientInstanceId = presenceService.ClientInstanceId,
            WorkerSessionId = presenceService.WorkerSessionId,
            Endpoint = CurrentTransport.ListenerEndpoint,
            HeartbeatUtc = DateTime.UtcNow,
            Participant = localParticipant.Clone(),
        };

        cachedRegistryEntriesByPath[registryFilePath] = entry.Clone();
        registryWorker.QueueHeartbeat(entry);
        nextHeartbeatWriteUtc = now + HeartbeatWriteInterval;

        RefreshKnownParticipants();
    }

    private void UpdateLocalAvailability(bool pluginEnabled, bool localOnlyModeEnabled)
    {
        localPluginEnabled = pluginEnabled;
        this.localOnlyModeEnabled = localOnlyModeEnabled;
        remoteMutationsAllowed = pluginEnabled && !localOnlyModeEnabled;
    }

    private void RefreshLocalAvailabilityFromConfiguration()
        => UpdateLocalAvailability(configuration.PluginEnabled, configuration.LocalOnlyModeEnabled);

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

    private void PauseLocalAdvertisement(string reason)
    {
        CurrentTransport.Availability = $"Paused: {reason}";
        CurrentTransport.TransportMode = localOnlyModeEnabled ? DadTransportMode.LocalOnly : DadTransportMode.LocalhostHybrid;
        CurrentTransport.LastRequestStatus = $"Local advertisement paused: {reason}";

        var reasonChanged = !string.Equals(localAdvertisementPauseReason, reason, StringComparison.Ordinal);
        if (localAdvertisementActive)
        {
            log.Information("[dad] Local transport advertisement paused ({Reason}); registry entry remains for stale-peer detection.", reason);
            localAdvertisementActive = false;
        }
        else if (localAdvertisementInitialized && reasonChanged)
        {
            log.Information("[dad] Local transport advertisement pause reason changed ({Reason}); registry entry remains for stale-peer detection.", reason);
        }

        localAdvertisementInitialized = true;
        localAdvertisementPauseReason = reason;
    }

    private void ResumeLocalAdvertisement()
    {
        if (localAdvertisementActive)
            return;

        if (localAdvertisementInitialized)
            log.Information("[dad] Local transport advertisement resumed; heartbeat will refresh immediately.");

        localAdvertisementActive = true;
        localAdvertisementInitialized = true;
        localAdvertisementPauseReason = string.Empty;
        if (IsReady)
        {
            CurrentTransport.Availability = "Ready";
            CurrentTransport.TransportMode = DadTransportMode.LocalhostHybrid;
            CurrentTransport.LastRequestStatus = "Dad transport ready.";
        }
        nextHeartbeatWriteUtc = DateTime.MinValue;
    }

    public DadPeerTransportSnapshot RequestSnapshots(DadPeerSnapshotRequest request)
    {
        RefreshKnownParticipants();
        var responses = new List<DadPeerSnapshotResponse>();
        foreach (var participant in CurrentTransport.KnownParticipants.ToList())
        {
            var response = SendEnvelope<DadPeerSnapshotRequest, DadPeerSnapshotResponse>(participant.Endpoint, MessageSnapshotRequest, request);
            if (response == null)
                continue;

            responses.Add(response);
        }

        CurrentTransport.LastRequestUtc = DateTime.UtcNow;
        CurrentTransport.LastResponses = responses;
        CurrentTransport.ConnectedPeerCount = responses.Count;
        CurrentTransport.LastRequestStatus = responses.Count == 0
            ? "No remote Dad workers discovered."
            : $"Received {responses.Count} snapshot response(s) from discovered workers.";
        RefreshKnownParticipants();
        ApplySnapshotResponsesToKnownParticipants(responses);
        return CurrentTransport;
    }

    public DadParticipantReadyDto? SendWakeRequest(DadParticipantSnapshot participant, DadWakeRequestDto request)
        => SendEnvelope<DadWakeRequestDto, DadParticipantReadyDto>(participant.Endpoint, MessageWakeRequest, request);

    public DadClaimDecisionDto? RequestClaim(DadParticipantSnapshot participant, DadClaimRequestDto request)
        => SendEnvelope<DadClaimRequestDto, DadClaimDecisionDto>(participant.Endpoint, MessageClaimRequest, request);

    public DadRunStepResultDto? SendAssemblyInstruction(DadParticipantSnapshot participant, DadAssemblyInstructionDto instruction)
        => SendEnvelope<DadAssemblyInstructionDto, DadRunStepResultDto>(participant.Endpoint, MessageAssemblyInstruction, instruction);

    public DadCharacterLoadResultDto? SendCharacterLoadCommand(DadParticipantSnapshot participant, DadCharacterLoadCommandDto command)
        => SendEnvelope<DadCharacterLoadCommandDto, DadCharacterLoadResultDto>(participant.Endpoint, MessageCharacterLoadCommand, command);

    public DadRunResult? QueryAuthorityStatus(string endpoint)
    {
        // Review H1/M4: this is polled from the framework thread (~1/sec via the DTR refresh). SendEnvelope
        // blocks up to the socket timeout, so after a failure back off before hammering a dead peer every tick.
        if (string.Equals(endpoint, lastFailedAuthorityEndpoint, StringComparison.OrdinalIgnoreCase) &&
            DateTime.UtcNow < nextAuthorityQueryRetryUtc)
        {
            return null;
        }

        var result = SendEnvelope<string, DadRunResult>(endpoint, MessageStatusQuery, string.Empty);
        if (result == null)
        {
            lastFailedAuthorityEndpoint = endpoint;
            nextAuthorityQueryRetryUtc = DateTime.UtcNow + AuthorityQueryFailureBackoff;
        }
        else if (string.Equals(endpoint, lastFailedAuthorityEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            lastFailedAuthorityEndpoint = string.Empty;
            nextAuthorityQueryRetryUtc = DateTime.MinValue;
        }

        return result;
    }

    public Task<DadRunResult?> QueryAuthorityStatusAsync(string endpoint, CancellationToken cancellationToken = default)
        => SendRecurringEnvelopeAsync<string, DadRunResult>(
            endpoint,
            MessageStatusQuery,
            string.Empty,
            cancellationToken);

    internal Task<DadRecurringTransportResult<DadRunResult>> QueryAuthorityStatusPollAsync(
        string endpoint,
        CancellationToken cancellationToken = default)
        => SendRecurringEnvelopeResultAsync<string, DadRunResult>(
            endpoint,
            MessageStatusQuery,
            string.Empty,
            cancellationToken);

    public DadRunResult? SendStartRunCommand(string endpoint, DadRunRequest request)
        => SendEnvelope<DadRunRequest, DadRunResult>(endpoint, MessageStartRun, request);

    public DadRunResult? SendCancelCommand(string endpoint, DadCancelCommandDto command)
        => SendEnvelope<DadCancelCommandDto, DadRunResult>(endpoint, MessageCancelCommand, command);

    public IReadOnlyList<DadPeerRosterCatalogResponse> RequestRosterCatalogs(DadRosterRefreshPlan request)
    {
        RefreshKnownParticipants();
        var responses = new List<DadPeerRosterCatalogResponse>();
        foreach (var participant in CurrentTransport.KnownParticipants.ToList())
        {
            var response = SendEnvelope<DadRosterRefreshPlan, DadPeerRosterCatalogResponse>(
                participant.Endpoint,
                MessageRosterCatalogRequest,
                request);
            if (response == null)
                continue;

            responses.Add(response);
        }

        CurrentTransport.LastRequestUtc = DateTime.UtcNow;
        CurrentTransport.LastRequestStatus = responses.Count == 0
            ? "No remote Dad roster catalogs discovered."
            : $"Received {responses.Count} roster catalog response(s) from discovered workers.";
        return responses;
    }

    public DadRosterRefreshResultDto? SendRosterRefreshCommand(
        DadParticipantSnapshot participant,
        DadRosterRefreshCommandDto command)
        => SendEnvelope<DadRosterRefreshCommandDto, DadRosterRefreshResultDto>(
            participant.Endpoint,
            MessageRosterRefreshCommand,
            command);

    public IReadOnlyList<DadProfileCatalogResponse> RequestProfileCatalogs(string requestId)
    {
        RefreshKnownParticipants();
        var responses = RequestProfileCatalogsAsync(requestId, GetKnownParticipantsSnapshot(), cancellation.Token)
            .GetAwaiter()
            .GetResult();

        RecordProfileCatalogRefreshResult(responses.Count);
        return responses;
    }

    public async Task<IReadOnlyList<DadProfileCatalogResponse>> RequestProfileCatalogsAsync(
        string requestId,
        IReadOnlyList<DadParticipantSnapshot> participants,
        CancellationToken cancellationToken = default)
    {
        var tasks = participants
            .Where(static participant => !participant.IsLocalClient && !string.IsNullOrWhiteSpace(participant.Endpoint))
            .Select(participant => RequestProfileCatalogAsync(requestId, participant, cancellationToken))
            .ToList();
        if (tasks.Count == 0)
            return [];

        var responses = await Task.WhenAll(tasks).ConfigureAwait(false);
        return responses
            .Where(static response => response is { Success: true })
            .Select(static response => response!)
            .ToList();
    }

    internal void RecordProfileCatalogRefreshResult(int responseCount)
    {
        CurrentTransport.LastRequestUtc = DateTime.UtcNow;
        CurrentTransport.LastRequestStatus = responseCount == 0
            ? "No remote Dad profile catalogs discovered."
            : $"Received {responseCount} profile catalog response(s).";
    }

    public DadProfileUpdateAck? SendProfileUpdate(string endpoint, DadProfileUpdateRequest request)
        => SendEnvelope<DadProfileUpdateRequest, DadProfileUpdateAck>(
            endpoint,
            MessageProfileUpdateCommand,
            request);

    public DadWorkerExecutionAck? SendWorkerExecutionCommand(
        DadParticipantSnapshot participant,
        DadWorkerExecutionCommand command)
        => SendEnvelope<DadWorkerExecutionCommand, DadWorkerExecutionAck>(
            participant.Endpoint,
            MessageWorkerExecutionCommand,
            command);

    public DadWorkerExecutionStatus? GetWorkerExecutionStatus(DadParticipantSnapshot participant)
        => SendEnvelope<string, DadWorkerExecutionStatus>(
            participant.Endpoint,
            MessageWorkerExecutionStatus,
            participant.RunId);

    public DadWorkerExecutionAck? SendWorkerExecutionCancel(
        DadParticipantSnapshot participant,
        DadWorkerExecutionCancel command)
        => SendEnvelope<DadWorkerExecutionCancel, DadWorkerExecutionAck>(
            participant.Endpoint,
            MessageWorkerExecutionCancel,
            command);

    public List<DadCancelAckDto> BroadcastCancel(DadCancelCommandDto command, IEnumerable<DadParticipantSnapshot> participants)
    {
        var acks = new List<DadCancelAckDto>();
        foreach (var participant in participants.Where(static participant => !participant.IsLocalClient))
        {
            var ack = SendEnvelope<DadCancelCommandDto, DadCancelAckDto>(participant.Endpoint, MessageCancelRun, command);
            if (ack != null)
                acks.Add(ack);
        }

        return acks;
    }

    private void StartListener()
    {
        try
        {
            var bindAddress = ResolveBindAddress(configuration.TransportBindHost);
            var bindPort = Math.Clamp(configuration.TransportBindPort, 0, 65535);
            listener = new TcpListener(bindAddress, bindPort);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var advertisedHost = GetAdvertisedListenerHost(configuration.TransportBindHost, endpoint.Address);
            CurrentTransport.ListenerEndpoint = FormatEndpoint(advertisedHost, endpoint.Port);
            CurrentTransport.Availability = "Ready";
            CurrentTransport.LastRequestStatus = $"Dad transport ready on {CurrentTransport.ListenerEndpoint}.";
            IsReady = true;

            // Review C2(b): loud warning when listening on a non-loopback interface without a shared secret —
            // anyone on the network could send commands. Set Configuration.TransportSharedSecret to require auth.
            if (!IPAddress.IsLoopback(bindAddress) && string.IsNullOrEmpty(configuration.TransportSharedSecret))
            {
                log.Warning(
                    "[dad] Transport bound to NON-LOOPBACK {Endpoint} without a shared secret — unauthenticated peers can drive this client. Set a TransportSharedSecret.",
                    CurrentTransport.ListenerEndpoint);
                CurrentTransport.LastRequestStatus += " (WARNING: non-loopback bind without shared secret.)";
            }

            var activeListener = listener;
            acceptLoopTask = Task.Run(() => AcceptLoopAsync(activeListener, cancellation.Token), cancellation.Token);
        }
        catch (Exception ex)
        {
            IsReady = false;
            CurrentTransport.ListenerEndpoint = string.Empty;
            CurrentTransport.Availability = $"Unavailable: {ex.Message}";
            CurrentTransport.LastRequestStatus = "Failed to start Dad transport listener.";
            log.Error(ex, "[dad] Failed to start Dad transport listener.");
        }
    }

    private void StopListener()
    {
        var activeListener = listener;
        listener = null;

        try
        {
            activeListener?.Stop();
            // Review M13: don't block the framework thread waiting for the accept loop on a bind change —
            // Stop()/cancellation makes AcceptTcpClientAsync throw and the loop exits on its own.
        }
        catch
        {
            // Best-effort shutdown only.
        }
        finally
        {
            acceptLoopTask = null;
        }
    }

    private async Task AcceptLoopAsync(TcpListener activeListener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Review L4: bound concurrent client handlers (backpressure against connection floods).
                await clientSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                TcpClient client;
                try
                {
                    client = await activeListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    clientSlots.Release();
                    throw;
                }

                // Review M11: track the handler so Dispose can drain in-flight requests.
                var clientTask = HandleClientWithReleaseAsync(client, cancellationToken);
                activeClientTasks[clientTask] = 0;
                _ = clientTask.ContinueWith(t => activeClientTasks.TryRemove(t, out _), TaskScheduler.Default);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[dad] Transport accept loop fault.");
            }
        }
    }

    private async Task HandleClientWithReleaseAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            client.Dispose();
            clientSlots.Release();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        try
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
                return;

            // Review M2: cap inbound payload size before deserializing (DoS hardening).
            if (line.Length > MaxRequestChars)
            {
                log.Warning("[dad] Rejected oversized transport request ({Length} chars).", line.Length);
                return;
            }

            var envelope = DadIpcJson.Deserialize<DadTransportEnvelope>(line);
            if (envelope == null)
                return;

            // Review C2(b): reject unauthenticated peers when a shared secret is configured.
            if (!VerifyEnvelopeAuth(envelope))
            {
                log.Warning("[dad] Rejected transport request with invalid/missing auth ({MessageType}).", envelope.MessageType);
                return;
            }

            // Review C1/C3/C5/H5/H6: every inbound handler mutates coordinator/presence/claim/worker
            // state and/or touches game memory. Run the whole dispatch on the Dalamud framework thread
            // so that state is single-threaded; the socket thread only does I/O and blocks on the result.
            var response = await Plugin.Framework.RunOnFrameworkThread(() => DispatchEnvelope(envelope))
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(response))
                await writer.WriteLineAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Transport request handling fault.");
        }
    }

    // Runs on the Dalamud framework thread (see HandleClientAsync). Returns the serialized response.
    private string DispatchEnvelope(DadTransportEnvelope envelope)
    {
        RefreshLocalAvailabilityFromConfiguration();

        return envelope.MessageType switch
        {
            MessageSnapshotRequest => DadIpcJson.Serialize(HandleSnapshotRequest(envelope.PayloadJson)),
            MessageWakeRequest => DadIpcJson.Serialize(HandleWakeRequest(envelope.PayloadJson)),
            MessageClaimRequest => DadIpcJson.Serialize(HandleClaimRequest(envelope.PayloadJson)),
            MessageAssemblyInstruction => DadIpcJson.Serialize(HandleAssemblyInstruction(envelope.PayloadJson)),
            MessageCharacterLoadCommand => DadIpcJson.Serialize(HandleCharacterLoadCommand(envelope.PayloadJson)),
            MessageCancelRun => DadIpcJson.Serialize(HandleCancelRun(envelope.PayloadJson)),
            MessageCancelCommand => DadIpcJson.Serialize(HandleCancelCommand(envelope.PayloadJson)),
            MessageStatusQuery => DadIpcJson.Serialize(HandleStatusQuery()),
            MessageStartRun => DadIpcJson.Serialize(HandleStartRun(envelope.PayloadJson)),
            MessageRosterCatalogRequest => DadIpcJson.Serialize(HandleRosterCatalogRequest(envelope.PayloadJson)),
            MessageRosterRefreshCommand => DadIpcJson.Serialize(HandleRosterRefreshCommand(envelope.PayloadJson)),
            MessageProfileCatalogRequest => DadIpcJson.Serialize(HandleProfileCatalogRequest(envelope.PayloadJson)),
            MessageProfileUpdateCommand => DadIpcJson.Serialize(HandleProfileUpdateCommand(envelope.PayloadJson)),
            MessageWorkerExecutionCommand => DadIpcJson.Serialize(HandleWorkerExecutionCommand(envelope.PayloadJson)),
            MessageWorkerExecutionStatus => DadIpcJson.Serialize(HandleWorkerExecutionStatus()),
            MessageWorkerExecutionCancel => DadIpcJson.Serialize(HandleWorkerExecutionCancel(envelope.PayloadJson)),
            _ => string.Empty,
        };
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
            XadbReady = presenceService.CurrentParticipant.Character.XadbReady,
            Warnings = remoteMutationsAllowed ? [] : [BuildLocalUnavailableReason()],
        };
    }

    private DadParticipantReadyDto HandleWakeRequest(string payloadJson)
    {
        var request = DadIpcJson.Deserialize<DadWakeRequestDto>(payloadJson) ?? new DadWakeRequestDto();
        if (!remoteMutationsAllowed)
            return BuildRejectedWakeResponse(request, BuildRemoteMutationRejectedReason("remote wake request"));

        return presenceService.HandleWakeRequest(request);
    }

    private DadClaimDecisionDto HandleClaimRequest(string payloadJson)
    {
        var request = DadIpcJson.Deserialize<DadClaimRequestDto>(payloadJson) ?? new DadClaimRequestDto();
        if (!remoteMutationsAllowed)
            return BuildRejectedClaimDecision(request, BuildRemoteMutationRejectedReason("remote claim request"));

        var decision = claimService.TryClaimLocal(request, presenceService.BuildSnapshotCopy());
        presenceService.ApplyClaimState(request.RunId, decision.ClaimState, decision.LeaseState, decision.Lease, decision.Reason);
        return decision;
    }

    private DadRunStepResultDto HandleAssemblyInstruction(string payloadJson)
    {
        var instruction = DadIpcJson.Deserialize<DadAssemblyInstructionDto>(payloadJson) ?? new DadAssemblyInstructionDto();
        if (!remoteMutationsAllowed)
            return BuildRejectedAssemblyResult(instruction, BuildRemoteMutationRejectedReason("remote assembly instruction"));

        return presenceService.HandleAssemblyInstruction(instruction);
    }

    private DadCharacterLoadResultDto HandleCharacterLoadCommand(string payloadJson)
    {
        var command = DadIpcJson.Deserialize<DadCharacterLoadCommandDto>(payloadJson) ?? new DadCharacterLoadCommandDto();
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

        // Review C2: do NOT execute a peer-supplied raw command string unless the operator has explicitly
        // opted in. This closes the remote-arbitrary-command-execution hole (default secure).
        if (!configuration.AllowRemoteCommandExecution)
        {
            log.Warning("[dad] Rejected remote character-load command (AllowRemoteCommandExecution is off).");
            return new DadCharacterLoadResultDto
            {
                CommandId = command.CommandId,
                Accepted = false,
                DryRun = false,
                Summary = "Remote command execution is disabled (enable it in Dad settings to allow this).",
                Snapshot = presenceService.BuildSnapshotCopy(),
            };
        }

        var accepted = false;
        try
        {
            accepted = !string.IsNullOrWhiteSpace(command.Command) &&
                       Plugin.CommandManager.ProcessCommand(command.Command);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Character-load command failed: {Command}", command.Command);
        }

        return new DadCharacterLoadResultDto
        {
            CommandId = command.CommandId,
            Accepted = accepted,
            DryRun = false,
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

        if (cancelRunHandler == null)
            return DadRunResult.Rejected(null, "Server Dad cancel handler unavailable.");

        return cancelRunHandler(command);
    }

    private DadRunResult HandleStatusQuery()
    {
        var result = statusProvider?.Invoke() ?? DadRunResult.Rejected(null, "Server Dad status unavailable.");
        if (remoteMutationsAllowed)
            return result;

        var unavailable = result.Clone();
        var reason = BuildLocalUnavailableReason();
        unavailable.LocalOnlyEnabled = localOnlyModeEnabled;
        unavailable.AuthorityMode = localOnlyModeEnabled ? DadAuthorityMode.LocalOnly : unavailable.AuthorityMode;
        unavailable.BlockedReason = reason;
        unavailable.Summary = reason;
        if (unavailable.Warnings.All(warning => !string.Equals(warning, reason, StringComparison.OrdinalIgnoreCase)))
            unavailable.Warnings.Add(reason);
        return unavailable;
    }

    private DadRunResult HandleStartRun(string payloadJson)
    {
        var request = DadIpcJson.Deserialize<DadRunRequest>(payloadJson) ?? new DadRunRequest();
        if (!remoteMutationsAllowed)
            return DadRunResult.Rejected(request, BuildRemoteMutationRejectedReason("remote start command"));

        if (startRunHandler == null)
            return DadRunResult.Rejected(request, "Server Dad start handler unavailable.");

        return startRunHandler(request);
    }

    private DadPeerRosterCatalogResponse HandleRosterCatalogRequest(string payloadJson)
    {
        var request = DadIpcJson.Deserialize<DadRosterRefreshPlan>(payloadJson) ?? new DadRosterRefreshPlan();
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
        var command = DadIpcJson.Deserialize<DadRosterRefreshCommandDto>(payloadJson) ?? new DadRosterRefreshCommandDto();
        if (!remoteMutationsAllowed)
        {
            return new DadRosterRefreshResultDto
            {
                CommandId = command.CommandId,
                AccountKey = command.AccountKey,
                CharacterKey = command.CharacterKey,
                ContentId = command.ContentId,
                Accepted = false,
                DryRun = command.DryRun,
                Summary = BuildRemoteMutationRejectedReason("remote roster-refresh command"),
                Snapshot = BuildLocalTransportSnapshot(),
            };
        }

        if (rosterRefreshHandler == null)
        {
            return new DadRosterRefreshResultDto
            {
                CommandId = command.CommandId,
                AccountKey = command.AccountKey,
                CharacterKey = command.CharacterKey,
                ContentId = command.ContentId,
                Accepted = false,
                DryRun = command.DryRun,
                Summary = "Dad roster refresh handler unavailable.",
                Snapshot = BuildLocalTransportSnapshot(),
            };
        }

        return rosterRefreshHandler(command);
    }

    private DadProfileCatalogResponse HandleProfileCatalogRequest(string payloadJson)
    {
        var requestId = DadIpcJson.Deserialize<string>(payloadJson) ?? string.Empty;
        var catalog = profileCatalogProvider?.Invoke() ?? new DadProfileCatalog
        {
            ReadOnly = true,
        };
        catalog.OwnerClientInstanceId = presenceService.ClientInstanceId;
        catalog.OwnerWorkerSessionId = presenceService.WorkerSessionId;
        catalog.OwnerEndpoint = CurrentTransport.ListenerEndpoint;
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
        var request = DadIpcJson.Deserialize<DadProfileUpdateRequest>(payloadJson) ?? new DadProfileUpdateRequest();
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
        var command = DadIpcJson.Deserialize<DadWorkerExecutionCommand>(payloadJson) ?? new DadWorkerExecutionCommand();
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
        var command = DadIpcJson.Deserialize<DadWorkerExecutionCancel>(payloadJson) ?? new DadWorkerExecutionCancel();
        if (!remoteMutationsAllowed)
        {
            // Review L1: every other mutating handler enforces this gate; cancel must too.
            return new DadWorkerExecutionAck
            {
                RunId = command.RunId,
                WorkerSessionId = presenceService.WorkerSessionId,
                Accepted = false,
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

    private DadParticipantSnapshot BuildLocalTransportSnapshot()
    {
        var snapshot = presenceService.BuildSnapshotCopy();
        if (remoteMutationsAllowed)
            return snapshot;

        MarkSnapshotUnavailable(snapshot, BuildLocalUnavailableReason());
        return snapshot;
    }

    private void MarkSnapshotUnavailable(DadParticipantSnapshot snapshot, string reason)
    {
        snapshot.IsAvailable = false;
        snapshot.IsEligibleForRun = false;
        snapshot.StatusText = reason;
        snapshot.AuthorityMode = localOnlyModeEnabled ? DadAuthorityMode.LocalOnly : snapshot.AuthorityMode;
        snapshot.Character.Readiness = DadReadinessState.Unavailable;
        if (snapshot.Character.Blockers.All(blocker => !string.Equals(blocker, reason, StringComparison.OrdinalIgnoreCase)))
            snapshot.Character.Blockers.Add(reason);
        if (snapshot.Warnings.All(warning => !string.Equals(warning, reason, StringComparison.OrdinalIgnoreCase)))
            snapshot.Warnings.Add(reason);
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
        snapshot.ClaimState = DadClaimState.Denied;
        snapshot.LeaseState = DadParticipantLeaseState.Denied;
        var lease = request.Lease?.Clone() ?? new DadParticipantLeaseRecord
        {
            RunId = request.RunId,
            SlotId = request.SlotId,
            AssignedAccountKey = request.RequiredAccountKey,
            AssignedCharacterKey = request.RequiredCharacterKey,
            OwningWorkerSessionId = snapshot.WorkerSessionId,
            IssuedUtc = DateTime.UtcNow,
            RenewedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow,
        };
        lease.State = DadParticipantLeaseState.Denied;
        lease.Summary = reason;

        return new DadClaimDecisionDto
        {
            RunId = request.RunId,
            WorkerSessionId = presenceService.WorkerSessionId,
            Granted = false,
            ClaimState = DadClaimState.Denied,
            LeaseState = DadParticipantLeaseState.Denied,
            CharacterKey = snapshot.ActiveCharacterKey,
            Reason = reason,
            Lease = lease,
            Snapshot = snapshot,
        };
    }

    private DadRunStepResultDto BuildRejectedAssemblyResult(DadAssemblyInstructionDto instruction, string reason)
    {
        var snapshot = BuildLocalTransportSnapshot();
        return new DadRunStepResultDto
        {
            RunId = instruction.RunId,
            ModuleId = instruction.ModuleId,
            StepName = "Assembly",
            ParticipantState = snapshot.State,
            Success = false,
            Deferred = true,
            Summary = reason,
            FailureReason = reason,
            BlockedReason = reason,
        };
    }

    private DadCancelAckDto BuildRejectedCancelAck(DadCancelCommandDto command, string reason)
    {
        return new DadCancelAckDto
        {
            RunId = command.RunId,
            WorkerSessionId = presenceService.WorkerSessionId,
            CancellationState = command.CancellationState,
            Acknowledged = false,
            Summary = reason,
            Snapshot = BuildLocalTransportSnapshot(),
        };
    }

    private TResponse? SendEnvelope<TRequest, TResponse>(string endpoint, string messageType, TRequest request)
        => SendEnvelopeAsync<TRequest, TResponse>(endpoint, messageType, request, cancellation.Token)
            .GetAwaiter()
            .GetResult();

    private async Task<TResponse?> SendRecurringEnvelopeAsync<TRequest, TResponse>(
        string endpoint,
        string messageType,
        TRequest request,
        CancellationToken cancellationToken)
    {
        var result = await SendRecurringEnvelopeResultAsync<TRequest, TResponse>(
            endpoint,
            messageType,
            request,
            cancellationToken).ConfigureAwait(false);
        return result.Response;
    }

    private async Task<DadRecurringTransportResult<TResponse>> SendRecurringEnvelopeResultAsync<TRequest, TResponse>(
        string endpoint,
        string messageType,
        TRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return DadRecurringTransportResult<TResponse>.Skipped();

        if (!activeRecurringEndpoints.TryAdd(endpoint, 0))
            return DadRecurringTransportResult<TResponse>.Skipped();

        try
        {
            await recurringPeerSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var response = await SendEnvelopeAsync<TRequest, TResponse>(
                    endpoint,
                    messageType,
                    request,
                    cancellationToken).ConfigureAwait(false);
                return DadRecurringTransportResult<TResponse>.Completed(response);
            }
            finally
            {
                recurringPeerSlots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return DadRecurringTransportResult<TResponse>.Skipped();
        }
        finally
        {
            activeRecurringEndpoints.TryRemove(endpoint, out _);
        }
    }

    private async Task<DadProfileCatalogResponse?> RequestProfileCatalogAsync(
        string requestId,
        DadParticipantSnapshot participant,
        CancellationToken cancellationToken)
    {
        var response = await SendRecurringEnvelopeAsync<string, DadProfileCatalogResponse>(
            participant.Endpoint,
            MessageProfileCatalogRequest,
            requestId,
            cancellationToken).ConfigureAwait(false);
        if (response == null)
            return null;

        response.Catalog.OwnerEndpoint = participant.Endpoint;
        response.Catalog.OwnerOnline = true;
        response.Catalog.ReadOnly = false;
        return response;
    }

    private async Task<TResponse?> SendEnvelopeAsync<TRequest, TResponse>(
        string endpoint,
        string messageType,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseEndpoint(endpoint, out var host, out var port))
            return default;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SocketTimeout);
            var token = timeout.Token;
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, token).ConfigureAwait(false);

            await using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

            var payloadJson = DadIpcJson.Serialize(request);
            var envelope = new DadTransportEnvelope
            {
                MessageType = messageType,
                PayloadJson = payloadJson,
                Auth = ComputeEnvelopeAuth(messageType, payloadJson), // Review C2(b)
            };

            await writer.WriteLineAsync(DadIpcJson.Serialize(envelope).AsMemory(), token).ConfigureAwait(false);
            var responseJson = await reader.ReadLineAsync(token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(responseJson)
                ? default
                : DadIpcJson.Deserialize<TResponse>(responseJson);
        }
        catch (OperationCanceledException ex)
        {
            log.Debug(ex, "[DAD] Transport send timed out/cancelled for {Endpoint} {MessageType}.", endpoint, messageType);
            return default;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[dad] Transport send failure for {Endpoint} {MessageType}.", endpoint, messageType);
            return default;
        }
    }

    // Review C2(b): HMAC-SHA256 over the envelope when a shared secret is configured (empty = auth disabled).
    private string ComputeEnvelopeAuth(string messageType, string payloadJson)
    {
        var secret = configuration.TransportSharedSecret;
        if (string.IsNullOrEmpty(secret))
            return string.Empty;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{messageType}\n{payloadJson}"));
        return Convert.ToBase64String(hash);
    }

    private bool VerifyEnvelopeAuth(DadTransportEnvelope envelope)
    {
        if (string.IsNullOrEmpty(configuration.TransportSharedSecret))
            return true;

        var expected = ComputeEnvelopeAuth(envelope.MessageType, envelope.PayloadJson);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(envelope.Auth ?? string.Empty));
    }

    private bool TryBuildConfiguredAuthorityEndpoint(out string endpoint)
    {
        endpoint = string.Empty;
        var host = configuration.AuthorityTargetHost.Trim();
        var port = Math.Clamp(configuration.AuthorityTargetPort, 0, 65535);
        if (string.IsNullOrWhiteSpace(host) || port <= 0)
            return false;

        endpoint = FormatEndpoint(host, port);
        return true;
    }

    private static IPAddress ResolveBindAddress(string host)
    {
        var trimmedHost = host.Trim();
        if (string.IsNullOrWhiteSpace(trimmedHost) || string.Equals(trimmedHost, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;

        if (IPAddress.TryParse(trimmedHost, out var ipAddress))
            return ipAddress;

        // Review M10: DNS can throw (SocketException) or return nothing — don't let that crash StartListener
        // (which would surface only a generic "failed to start"); fall back to loopback instead.
        try
        {
            var resolved = Dns.GetHostAddresses(trimmedHost);
            var ipv4 = resolved.FirstOrDefault(static address => address.AddressFamily == AddressFamily.InterNetwork);
            return ipv4 ?? resolved.FirstOrDefault() ?? IPAddress.Loopback;
        }
        catch
        {
            return IPAddress.Loopback;
        }
    }

    private static string GetAdvertisedListenerHost(string configuredHost, IPAddress boundAddress)
    {
        var host = configuredHost.Trim();
        if (string.IsNullOrWhiteSpace(host))
            return boundAddress.ToString();

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback.ToString();

        if (host is "0.0.0.0" or "::" or "[::]")
            return Dns.GetHostName();

        return host;
    }

    private static bool TryParseEndpoint(string endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        var trimmed = endpoint.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracket = trimmed.IndexOf(']');
            if (closingBracket <= 1 || closingBracket >= trimmed.Length - 2 || trimmed[closingBracket + 1] != ':')
                return false;

            host = trimmed[1..closingBracket];
            return int.TryParse(trimmed[(closingBracket + 2)..], out port);
        }

        var separatorIndex = trimmed.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex >= trimmed.Length - 1)
            return false;

        host = trimmed[..separatorIndex];
        return int.TryParse(trimmed[(separatorIndex + 1)..], out port);
    }

    private static string FormatEndpoint(string host, int port)
    {
        var trimmedHost = host.Trim();
        return trimmedHost.Contains(':')
            ? $"[{trimmedHost}]:{port}"
            : $"{trimmedHost}:{port}";
    }

    private void RefreshKnownParticipants()
    {
        var now = DateTime.UtcNow;
        registryWorker.EnsureReadScheduled();
        if (registryWorker.TryConsumeLatestSnapshot(out var registrySnapshot))
        {
            foreach (var (path, entry) in registrySnapshot.Entries)
                cachedRegistryEntriesByPath[path] = entry.Clone();

            TrimExpiredRegistryEntries(now, registrySnapshot.SeenPaths);
        }
        else
        {
            TrimExpiredRegistryEntries(now, null);
        }

        var peers = new List<DadParticipantSnapshot>();
        foreach (var entry in cachedRegistryEntriesByPath.Values)
        {
            if (string.Equals(entry.WorkerSessionId, presenceService.WorkerSessionId.ToString(), StringComparison.Ordinal) ||
                now - entry.HeartbeatUtc > RegistryFreshness)
            {
                continue;
            }

            var participant = entry.Participant.Clone();
            participant.Endpoint = entry.Endpoint;
            participant.IsLocalClient = false;
            participant.LastHeartbeatUtc = entry.HeartbeatUtc;
            participant.State = now - entry.HeartbeatUtc > StaleHeartbeatThreshold
                ? DadParticipantState.Stale
                : participant.State;
            if (participant.State == DadParticipantState.Stale)
            {
                participant.LeaseState = DadParticipantLeaseState.Stale;
                participant.ClaimState = DadClaimState.Stale;
            }

            peers.Add(participant);
        }

        CurrentTransport.KnownParticipants = peers
            .OrderBy(static participant => participant.ManagedAccountAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.ActiveCharacterKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        CurrentTransport.ConnectedPeerCount = peers.Count;

        var authorityParticipant = ResolveAuthorityParticipant(peers);
        if (authorityParticipant != null)
        {
            CurrentTransport.AuthorityWorkerSessionId = authorityParticipant.WorkerSessionId;
            CurrentTransport.AuthorityEndpoint = authorityParticipant.Endpoint;
            CurrentTransport.AuthorityRole = authorityParticipant.WorkerRole;
            CurrentTransport.AuthorityStatus = DadStatusText.FormatAuthorityStatus(
                authorityParticipant.WorkerRole,
                authorityParticipant.WorkerSessionId,
                authorityParticipant.Endpoint,
                DadAuthorityMode.ServerDad);
        }
        else if (presenceService.CurrentParticipant.WorkerRole == DadWorkerRole.ServerDad)
        {
            CurrentTransport.AuthorityWorkerSessionId = presenceService.WorkerSessionId;
            CurrentTransport.AuthorityEndpoint = CurrentTransport.ListenerEndpoint;
            CurrentTransport.AuthorityRole = DadWorkerRole.ServerDad;
            CurrentTransport.AuthorityStatus = DadStatusText.FormatAuthorityStatus(
                DadWorkerRole.ServerDad,
                presenceService.WorkerSessionId,
                CurrentTransport.ListenerEndpoint,
                DadAuthorityMode.ServerDad);
        }
        else
        {
            CurrentTransport.AuthorityWorkerSessionId = new DadWorkerSessionId(string.Empty);
            CurrentTransport.AuthorityEndpoint = string.Empty;
            CurrentTransport.AuthorityRole = DadWorkerRole.None;
            CurrentTransport.AuthorityStatus = "Authority not discovered.";
        }
    }

    public IReadOnlyList<DadParticipantSnapshot> GetKnownParticipantsSnapshot()
    {
        RefreshKnownParticipants();
        return CurrentTransport.KnownParticipants
            .Select(static participant => participant.Clone())
            .ToList();
    }

    private void ApplySnapshotResponsesToKnownParticipants(IReadOnlyList<DadPeerSnapshotResponse> responses)
    {
        if (responses.Count == 0)
            return;

        var peers = CurrentTransport.KnownParticipants.ToList();
        foreach (var response in responses)
        {
            var participant = response.Participant.Clone();
            var existingIndex = peers.FindIndex(peer =>
                string.Equals(peer.WorkerSessionId, participant.WorkerSessionId.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(peer.ClientInstanceId, response.ClientInstanceId, StringComparison.OrdinalIgnoreCase));

            if (existingIndex >= 0)
            {
                var existing = peers[existingIndex];
                if (string.IsNullOrWhiteSpace(participant.Endpoint))
                    participant.Endpoint = existing.Endpoint;
                participant.LastHeartbeatUtc = existing.LastHeartbeatUtc;
                participant.IsLocalClient = false;
                peers[existingIndex] = participant;
                continue;
            }

            participant.IsLocalClient = false;
            peers.Add(participant);
        }

        CurrentTransport.KnownParticipants = peers
            .OrderBy(static participant => participant.ManagedAccountAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.ActiveCharacterKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        CurrentTransport.ConnectedPeerCount = CurrentTransport.KnownParticipants.Count;
    }

    private static DadParticipantSnapshot? ResolveAuthorityParticipant(IEnumerable<DadParticipantSnapshot> peers)
        => peers
            .Where(static participant => participant.WorkerRole == DadWorkerRole.ServerDad)
            .OrderBy(static participant => participant.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private void TrimExpiredRegistryEntries(DateTime now, IReadOnlySet<string>? seenPaths)
    {
        var expiredPaths = cachedRegistryEntriesByPath
            .Where(pair => now - pair.Value.HeartbeatUtc > RegistryFreshness)
            .Select(static pair => pair.Key)
            .ToList();

        foreach (var path in expiredPaths)
            cachedRegistryEntriesByPath.Remove(path);

        if (seenPaths == null)
            return;

        foreach (var path in cachedRegistryEntriesByPath.Keys.Except(seenPaths, StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (!cachedRegistryEntriesByPath.TryGetValue(path, out var entry) ||
                now - entry.HeartbeatUtc > RegistryFreshness)
            {
                cachedRegistryEntriesByPath.Remove(path);
            }
        }
    }

    private sealed class DadTransportEnvelope
    {
        public string MessageType { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = string.Empty;
        public string Auth { get; set; } = string.Empty; // Review C2(b): HMAC over MessageType+PayloadJson
    }

}

internal readonly record struct DadRecurringTransportResult<TResponse>(bool Sent, TResponse? Response)
{
    public static DadRecurringTransportResult<TResponse> Skipped()
        => new(false, default);

    public static DadRecurringTransportResult<TResponse> Completed(TResponse? response)
        => new(true, response);
}
