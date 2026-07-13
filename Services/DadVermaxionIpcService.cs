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
                cached = DadVermaxionStatusParser.Parse(false, null, now);
                reservation = activeRequest == null
                    ? DadVermaxionReservationParser.NotLoaded(now)
                    : DadVermaxionReservationParser.Renewing(activeRequest, now);
                nextRenewUtc = DateTime.MinValue;
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
            activeRequest = request;

            var loaded = IsLoaded(now);
            if (loaded != true)
            {
                reservation = DadVermaxionReservationParser.Renewing(request, now);
                nextRenewUtc = now + RefreshInterval;
                return reservation.Clone();
            }

            try
            {
                var payload = JsonSerializer.Serialize(request, JsonOptions);
                reservation = DadVermaxionReservationParser.BindToRequest(
                    DadVermaxionReservationParser.Parse(reserveHandoff.InvokeFunc(payload), now),
                    request);
                if (reservation.IsRejected)
                {
                    activeRequest = null;
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

            var success = true;
            if (activeRequest != null || reservation.IsGranted || reservation.RequiresWait)
            {
                try
                {
                    reservation = DadVermaxionReservationParser.Parse(
                        releaseHandoff.InvokeFunc(operationToken.Trim()),
                        DateTime.UtcNow);
                    success = reservation.State == DadVermaxionReservationState.Released;
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "[dad][VERMAXION] Failed to release v2 reservation {OperationToken}.", operationToken);
                    success = false;
                }
            }

            if (activeRequest == null || string.Equals(activeRequest.OperationToken, operationToken, StringComparison.OrdinalIgnoreCase))
                activeRequest = null;
            nextRenewUtc = DateTime.MinValue;
            return success;
        }
    }

    public void Update()
    {
        Inspect();
        DadVermaxionReservationRequest? renewal = null;
        lock (gate)
        {
            if (!disposed && activeRequest != null && DateTime.UtcNow >= nextRenewUtc)
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
