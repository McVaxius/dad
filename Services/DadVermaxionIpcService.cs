using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadVermaxionIpcService : IDisposable
{
    private const string StatusChannel = "VERMAXION.GetAutomationStatusJson";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<string> getStatusJson;
    private readonly ICallGateSubscriber<string, string> reserveHandoff;
    private readonly ICallGateSubscriber<string, string> releaseHandoff;
    private readonly ICallGateSubscriber<string, object> grantedHandoff;
    private readonly object gate = new();
    private DadVermaxionReadinessStatus cached = DadVermaxionStatusParser.Parse(false, null, DateTime.UtcNow);
    private DadVermaxionReservationStatus reservation = DadVermaxionReservationParser.NotLoaded(DateTime.UtcNow);
    private DadVermaxionReservationRequest? activeRequest;
    private string remotelySubmittedOperationToken = string.Empty;
    private string pendingReleaseOperationToken = string.Empty;
    private DateTime nextRefreshUtc = DateTime.MinValue;
    private DateTime nextRenewUtc = DateTime.MinValue;
    private bool disposed;

    public DadVermaxionIpcService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        getStatusJson = pluginInterface.GetIpcSubscriber<string>(StatusChannel);
        reserveHandoff = pluginInterface.GetIpcSubscriber<string, string>(DadVermaxionHandoffContract.ReserveChannel);
        releaseHandoff = pluginInterface.GetIpcSubscriber<string, string>(DadVermaxionHandoffContract.ReleaseChannel);
        grantedHandoff = pluginInterface.GetIpcSubscriber<string, object>(DadVermaxionHandoffContract.GrantedChannel);
        grantedHandoff.Subscribe(OnGranted);
    }

    public event Action<DadVermaxionReservationStatus>? ReservationGranted;

    public DadVermaxionReservationStatus Reservation
    {
        get
        {
            lock (gate)
                return reservation.Clone();
        }
    }

    public DadVermaxionReadinessStatus Inspect(bool forceRefresh = false)
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            if (!forceRefresh && now < nextRefreshUtc)
                return cached;

            nextRefreshUtc = now + RefreshInterval;
            var loaded = IsLoaded(now);
            if (!loaded.HasValue)
                return cached;
            if (!loaded.Value)
            {
                ObserveUnloadedProvider(now);
                return cached;
            }

            try
            {
                cached = DadVermaxionStatusParser.Parse(true, getStatusJson.InvokeFunc(), now);
            }
            catch (Exception ex)
            {
                cached = DadVermaxionStatusParser.Parse(true, null, now, ex.Message);
            }

            return cached;
        }
    }

    public DadVermaxionReservationStatus Reserve(DadVermaxionReservationRequest request)
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            if (disposed)
                return DadVermaxionReservationParser.Parse(null, now, "DAD VERMAXION IPC service is disposed.");

            request = Clone(request);
            request.LeaseSeconds = DadVermaxionHandoffContract.LeaseSeconds;
            var loaded = IsLoaded(now);
            if (loaded == false)
            {
                ObserveUnloadedProvider(now);
                return reservation.Clone();
            }

            if (activeRequest == null ||
                !string.Equals(
                    activeRequest.OperationToken,
                    request.OperationToken,
                    StringComparison.OrdinalIgnoreCase))
            {
                remotelySubmittedOperationToken = string.Empty;
            }
            activeRequest = request;

            if (!loaded.HasValue)
            {
                reservation = DadVermaxionReservationParser.Renewing(request, now);
                nextRenewUtc = now + RefreshInterval;
                return reservation.Clone();
            }

            try
            {
                var payload = JsonSerializer.Serialize(request, JsonOptions);
                // The provider may commit before InvokeFunc throws or returns malformed data. Mark
                // remote possibility before crossing IPC and retain it until cleanup is proven.
                remotelySubmittedOperationToken = request.OperationToken;
                reservation = DadVermaxionReservationParser.BindToRequest(
                    DadVermaxionReservationParser.Parse(reserveHandoff.InvokeFunc(payload), now),
                    request);
                if (reservation.IsRejected)
                {
                    activeRequest = null;
                    remotelySubmittedOperationToken = string.Empty;
                    nextRenewUtc = DateTime.MinValue;
                }
                else
                    nextRenewUtc = now + RefreshInterval;
            }
            catch (Exception ex)
            {
                reservation = DadVermaxionReservationParser.Renewing(request, now, ex.Message);
                nextRenewUtc = now + RefreshInterval;
            }

            return reservation.Clone();
        }
    }

    public bool Release(string operationToken)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(operationToken))
                return true;

            operationToken = operationToken.Trim();
            var matchesActiveRequest = activeRequest != null &&
                                       string.Equals(
                                           activeRequest.OperationToken,
                                           operationToken,
                                           StringComparison.OrdinalIgnoreCase);
            var matchesReservation = !string.IsNullOrWhiteSpace(reservation.OperationToken) &&
                                     string.Equals(
                                         reservation.OperationToken,
                                         operationToken,
                                         StringComparison.OrdinalIgnoreCase);
            var releaseOutstanding = string.Equals(
                pendingReleaseOperationToken,
                operationToken,
                StringComparison.OrdinalIgnoreCase);
            var mustRelease = matchesActiveRequest || releaseOutstanding ||
                              matchesReservation && (reservation.IsGranted || reservation.RequiresWait);
            if (!mustRelease)
                return true;

            var remotelySubmitted = string.Equals(
                remotelySubmittedOperationToken,
                operationToken,
                StringComparison.OrdinalIgnoreCase) ||
                matchesReservation && (reservation.IsGranted || reservation.RequiresWait);
            if (!remotelySubmitted)
            {
                CompleteLocalRelease(operationToken, matchesActiveRequest);
                return true;
            }

            var loaded = IsLoaded(DateTime.UtcNow);
            if (loaded == false)
            {
                // An unloaded provider cannot retain its in-memory reservation. This is terminal
                // proof without calling an absent release channel.
                CompleteLocalRelease(operationToken, matchesActiveRequest);
                return true;
            }

            try
            {
                reservation = DadVermaxionReservationParser.Parse(
                    releaseHandoff.InvokeFunc(operationToken),
                    DateTime.UtcNow);
                if (DadVermaxionReleaseProofRules.ProvesNoOwnedReservation(reservation, operationToken))
                {
                    if (matchesActiveRequest)
                        activeRequest = null;
                    if (string.Equals(
                            remotelySubmittedOperationToken,
                            operationToken,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        remotelySubmittedOperationToken = string.Empty;
                    }
                    pendingReleaseOperationToken = string.Empty;
                    nextRenewUtc = DateTime.MinValue;
                    return true;
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[dad][VERMAXION] Failed to release v2 reservation {OperationToken}.", operationToken);
            }

            // A response without exact-token v2 cleanup proof is not enough. Retain the ownership request
            // and an explicit release marker so the next wake-cleanup poll retries IPC rather than
            // acknowledging cancellation from an Unavailable, malformed, or mismatched snapshot.
            pendingReleaseOperationToken = operationToken;
            nextRenewUtc = DateTime.MinValue;
            return false;
        }
    }

    public void Update()
    {
        Inspect();
        DadVermaxionReservationRequest? renewal = null;
        lock (gate)
        {
            if (!disposed && activeRequest != null &&
                !string.Equals(
                    pendingReleaseOperationToken,
                    activeRequest.OperationToken,
                    StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow >= nextRenewUtc)
                renewal = Clone(activeRequest);
        }

        if (renewal != null)
            Reserve(renewal);
    }

    private void OnGranted(string json)
    {
        DadVermaxionReservationStatus parsed;
        lock (gate)
        {
            parsed = DadVermaxionReservationParser.Parse(json, DateTime.UtcNow);
            if (!parsed.IsGranted || activeRequest == null ||
                !string.Equals(activeRequest.OperationToken, parsed.OperationToken, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            reservation = DadVermaxionReservationParser.BindToRequest(parsed, activeRequest);
            nextRenewUtc = DateTime.UtcNow + RefreshInterval;
        }

        ReservationGranted?.Invoke(parsed.Clone());
    }

    private bool? IsLoaded(DateTime now)
    {
        try
        {
            return pluginInterface.InstalledPlugins.Any(static plugin =>
                plugin.IsLoaded &&
                (string.Equals(plugin.InternalName, "VERMAXION", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(plugin.Name, "VERMAXION", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(plugin.Name, "Vermaxion", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad][VERMAXION] Failed to inspect installed plugins.");
            cached = DadVermaxionStatusParser.Parse(true, null, now, $"Installed-plugin inspection failed: {ex.Message}");
            return null;
        }
    }

    private void ObserveUnloadedProvider(DateTime now)
    {
        cached = DadVermaxionStatusParser.Parse(false, null, now);
        reservation = DadVermaxionReservationParser.NotLoaded(now);
        activeRequest = null;
        remotelySubmittedOperationToken = string.Empty;
        pendingReleaseOperationToken = string.Empty;
        nextRenewUtc = DateTime.MinValue;
    }

    private void CompleteLocalRelease(string operationToken, bool matchesActiveRequest)
    {
        if (matchesActiveRequest)
            activeRequest = null;
        if (string.Equals(
                remotelySubmittedOperationToken,
                operationToken,
                StringComparison.OrdinalIgnoreCase))
        {
            remotelySubmittedOperationToken = string.Empty;
        }
        pendingReleaseOperationToken = string.Empty;
        nextRenewUtc = DateTime.MinValue;
        reservation = new DadVermaxionReservationStatus
        {
            Version = DadVermaxionHandoffContract.Version,
            OperationToken = operationToken,
            State = DadVermaxionReservationState.Released,
            ObservedAtUtc = DateTime.UtcNow,
            Summary = "No remotely owned VERMAXION reservation remains for this DAD operation.",
        };
    }

    private static DadVermaxionReservationRequest Clone(DadVermaxionReservationRequest request)
        => new()
        {
            Version = request.Version,
            OperationToken = request.OperationToken,
            SchedulerRunId = request.SchedulerRunId,
            SlotId = request.SlotId,
            AccountKey = request.AccountKey,
            CharacterKey = request.CharacterKey,
            RequestedAtUtc = request.RequestedAtUtc,
            LeaseSeconds = DadVermaxionHandoffContract.LeaseSeconds,
        };

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            if (activeRequest != null)
                Release(activeRequest.OperationToken);
            grantedHandoff.Unsubscribe(OnGranted);
        }
    }
}
