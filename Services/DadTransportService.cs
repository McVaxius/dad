using System.Net;
using System.Net.Sockets;
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
    private readonly CancellationTokenSource cancellation = new();
    private TcpListener? listener;
    private Task? acceptLoopTask;
    private DateTime nextHeartbeatWriteUtc = DateTime.MinValue;
    private DateTime lastRegistryWriteWarningUtc = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> lastRegistryReadWarningUtcByPath = new(StringComparer.OrdinalIgnoreCase);
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

    public DadTransportService(Configuration configuration, DadPresenceService presenceService, DadClaimService claimService, IPluginLog log)
    {
        this.configuration = configuration;
        this.presenceService = presenceService;
        this.claimService = claimService;
        this.log = log;
        registryDirectory = Path.Combine(Path.GetTempPath(), "dad-orchestrator", "registry");
        Directory.CreateDirectory(registryDirectory);
        registryFilePath = Path.Combine(registryDirectory, $"{presenceService.ClientInstanceId}.json");

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

    public void Dispose()
    {
        try
        {
            cancellation.Cancel();
            StopListener();
        }
        catch
        {
            // Best-effort shutdown only.
        }

        try
        {
            if (File.Exists(registryFilePath))
                File.Delete(registryFilePath);
        }
        catch
        {
            // Ignore registry cleanup failures.
        }
    }

    public void UpdateHeartbeat(DadParticipantSnapshot localParticipant, bool pluginEnabled, bool localOnlyModeEnabled)
    {
        UpdateLocalAvailability(pluginEnabled, localOnlyModeEnabled);

        if (!remoteMutationsAllowed)
        {
            PauseLocalAdvertisement(BuildLocalUnavailableReason());
            RefreshKnownParticipants();
            return;
        }

        ResumeLocalAdvertisement();

        if (!IsReady || DateTime.UtcNow < nextHeartbeatWriteUtc)
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

        try
        {
            WriteRegistryEntryAtomically(entry);
            nextHeartbeatWriteUtc = DateTime.UtcNow + HeartbeatWriteInterval;
        }
        catch (IOException ex)
        {
            nextHeartbeatWriteUtc = DateTime.UtcNow + HeartbeatWriteInterval;
            if (ShouldLogRegistryWarning(ref lastRegistryWriteWarningUtc))
                log.Warning(ex, "[dad] Transport registry heartbeat collision for {RegistryFilePath}; keeping previous discovery entry.", registryFilePath);
            else
                log.Debug(ex, "[dad] Transport registry heartbeat collision for {RegistryFilePath}.", registryFilePath);
        }
        catch (Exception ex)
        {
            nextHeartbeatWriteUtc = DateTime.UtcNow + HeartbeatWriteInterval;
            if (ShouldLogRegistryWarning(ref lastRegistryWriteWarningUtc))
                log.Warning(ex, "[dad] Failed to write transport registry entry.");
            else
                log.Debug(ex, "[dad] Failed to write transport registry entry.");
        }

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
        => SendEnvelope<string, DadRunResult>(endpoint, MessageStatusQuery, string.Empty);

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
            acceptLoopTask?.Wait(TimeSpan.FromSeconds(1));
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
                var client = await activeListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
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

            var envelope = DadIpcJson.Deserialize<DadTransportEnvelope>(line);
            if (envelope == null)
                return;

            RefreshLocalAvailabilityFromConfiguration();

            var response = envelope.MessageType switch
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
                _ => string.Empty,
            };

            if (!string.IsNullOrWhiteSpace(response))
                await writer.WriteLineAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Transport request handling fault.");
        }
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
    {
        if (!TryParseEndpoint(endpoint, out var host, out var port))
            return default;

        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            if (!connectTask.Wait(SocketTimeout))
                return default;

            using var stream = client.GetStream();
            stream.ReadTimeout = (int)SocketTimeout.TotalMilliseconds;
            stream.WriteTimeout = (int)SocketTimeout.TotalMilliseconds;
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

            var envelope = new DadTransportEnvelope
            {
                MessageType = messageType,
                PayloadJson = DadIpcJson.Serialize(request),
            };

            writer.WriteLine(DadIpcJson.Serialize(envelope));
            var responseJson = reader.ReadLine();
            return string.IsNullOrWhiteSpace(responseJson)
                ? default
                : DadIpcJson.Deserialize<TResponse>(responseJson);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[dad] Transport send failure for {Endpoint} {MessageType}.", endpoint, messageType);
            return default;
        }
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

        var resolved = Dns.GetHostAddresses(trimmedHost);
        var ipv4 = resolved.FirstOrDefault(static address => address.AddressFamily == AddressFamily.InterNetwork);
        return ipv4 ?? resolved.First();
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
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(registryDirectory, "*.json"))
        {
            seenPaths.Add(path);
            TryRefreshCachedRegistryEntry(path);
        }

        TrimExpiredRegistryEntries(now, seenPaths);

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

    private void WriteRegistryEntryAtomically(DadTransportRegistryEntry entry)
    {
        var tempPath = Path.Combine(
            registryDirectory,
            $"{presenceService.ClientInstanceId}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        var payload = DadIpcJson.Serialize(entry);

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(payload);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(registryFilePath))
                File.Replace(tempPath, registryFilePath, null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, registryFilePath);

            cachedRegistryEntriesByPath[registryFilePath] = entry;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort temp cleanup only.
            }
        }
    }

    private void TryRefreshCachedRegistryEntry(string path)
    {
        try
        {
            var entry = ReadRegistryEntry(path);
            if (entry != null)
                cachedRegistryEntriesByPath[path] = entry;
        }
        catch (FileNotFoundException)
        {
            ForgetRegistryPath(path);
        }
        catch (IOException ex)
        {
            if (ShouldLogRegistryWarning(path))
                log.Warning(ex, "[dad] Transport registry read collision for {RegistryFilePath}; keeping cached peer state until heartbeat expires.", path);
            else
                log.Debug(ex, "[dad] Transport registry read collision for {RegistryFilePath}.", path);
        }
        catch (Exception ex)
        {
            if (ShouldLogRegistryWarning(path))
                log.Warning(ex, "[dad] Failed to read transport registry entry {RegistryFilePath}; keeping cached peer state until heartbeat expires.", path);
            else
                log.Debug(ex, "[dad] Failed to read transport registry entry {RegistryFilePath}.", path);
        }
    }

    private static DadTransportRegistryEntry? ReadRegistryEntry(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var json = reader.ReadToEnd();
        return DadIpcJson.Deserialize<DadTransportRegistryEntry>(json);
    }

    private void TrimExpiredRegistryEntries(DateTime now, IReadOnlySet<string> seenPaths)
    {
        var expiredPaths = cachedRegistryEntriesByPath
            .Where(pair => now - pair.Value.HeartbeatUtc > RegistryFreshness)
            .Select(static pair => pair.Key)
            .ToList();

        foreach (var path in expiredPaths)
        {
            cachedRegistryEntriesByPath.Remove(path);
            lastRegistryReadWarningUtcByPath.Remove(path);
        }

        foreach (var path in lastRegistryReadWarningUtcByPath.Keys.Except(seenPaths, StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (!cachedRegistryEntriesByPath.ContainsKey(path))
                lastRegistryReadWarningUtcByPath.Remove(path);
        }
    }

    private void ForgetRegistryPath(string path)
    {
        cachedRegistryEntriesByPath.Remove(path);
        lastRegistryReadWarningUtcByPath.Remove(path);
    }

    private bool ShouldLogRegistryWarning(string path)
    {
        if (!lastRegistryReadWarningUtcByPath.TryGetValue(path, out var lastLoggedUtc))
        {
            lastRegistryReadWarningUtcByPath[path] = DateTime.UtcNow;
            return true;
        }

        if (DateTime.UtcNow - lastLoggedUtc < RegistryCollisionWarningInterval)
            return false;

        lastRegistryReadWarningUtcByPath[path] = DateTime.UtcNow;
        return true;
    }

    private static bool ShouldLogRegistryWarning(ref DateTime lastLoggedUtc)
    {
        if (lastLoggedUtc == DateTime.MinValue || DateTime.UtcNow - lastLoggedUtc >= RegistryCollisionWarningInterval)
        {
            lastLoggedUtc = DateTime.UtcNow;
            return true;
        }

        return false;
    }

    private sealed class DadTransportEnvelope
    {
        public string MessageType { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = string.Empty;
    }

    private sealed class DadTransportRegistryEntry
    {
        public string ClientInstanceId { get; set; } = string.Empty;
        public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
        public string Endpoint { get; set; } = string.Empty;
        public DateTime HeartbeatUtc { get; set; } = DateTime.UtcNow;
        public DadParticipantSnapshot Participant { get; set; } = new();
    }
}
