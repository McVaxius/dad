namespace dad.Services;

internal sealed record DadKranglerPrivacyLeaseSnapshot(
    bool Desired,
    bool LeaseActive,
    bool OwnedByThisProcess,
    string SafeCode,
    string Status,
    string Error)
{
    public static DadKranglerPrivacyLeaseSnapshot Disabled { get; } = new(
        false,
        false,
        false,
        "dad-krangler-privacy-disabled",
        "Krangler privacy lease is off.",
        string.Empty);
}

internal sealed class DadKranglerPrivacyLeaseController : IDisposable
{
    private const int MaximumResponseCharacters = 16 * 1024;

    private readonly object gate = new();
    private readonly string token;
    private readonly Func<string, string> acquire;
    private readonly Func<string, string> release;
    private readonly Func<string, string> getStatus;
    private readonly Action<Exception, string> logFailure;
    private bool desired;
    private bool reconcileRequested = true;
    private bool mayOwnLease;
    private bool disposed;
    private DadKranglerPrivacyLeaseSnapshot snapshot = DadKranglerPrivacyLeaseSnapshot.Disabled;

    internal DadKranglerPrivacyLeaseController(
        Func<string, string> acquire,
        Func<string, string> release,
        Func<string, string> getStatus,
        Action<Exception, string>? logFailure = null,
        string? processToken = null)
    {
        this.acquire = acquire ?? throw new ArgumentNullException(nameof(acquire));
        this.release = release ?? throw new ArgumentNullException(nameof(release));
        this.getStatus = getStatus ?? throw new ArgumentNullException(nameof(getStatus));
        this.logFailure = logFailure ?? ((_, _) => { });
        token = string.IsNullOrWhiteSpace(processToken)
            ? Guid.NewGuid().ToString("N")
            : processToken;
    }

    internal DadKranglerPrivacyLeaseSnapshot Snapshot
    {
        get
        {
            lock (gate)
                return snapshot;
        }
    }

    internal void SetDesired(bool value)
    {
        lock (gate)
        {
            if (disposed || desired == value)
                return;

            var wasDesired = desired;
            desired = value;
            reconcileRequested = value;
            if (value)
            {
                snapshot = snapshot with
                {
                    Desired = true,
                    SafeCode = "dad-krangler-privacy-reconcile-pending",
                    Status = "Checking Krangler privacy lease.",
                    Error = string.Empty,
                };
                return;
            }

            if (wasDesired || mayOwnLease)
                ReleaseCore();
            else
                snapshot = DadKranglerPrivacyLeaseSnapshot.Disabled;
        }
    }

    internal void RequestReconcile()
    {
        lock (gate)
        {
            if (disposed || !desired)
                return;
            reconcileRequested = true;
            snapshot = snapshot with
            {
                Desired = true,
                SafeCode = "dad-krangler-privacy-reconcile-pending",
                Status = "Krangler plugin state changed; checking privacy lease.",
                Error = string.Empty,
            };
        }
    }

    internal void Update()
    {
        lock (gate)
        {
            if (disposed || !desired || !reconcileRequested)
                return;
            reconcileRequested = false;
            ReconcileCore();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            if (desired || mayOwnLease)
                ReleaseCore();
            desired = false;
            reconcileRequested = false;
            mayOwnLease = false;
            disposed = true;
            snapshot = DadKranglerPrivacyLeaseSnapshot.Disabled;
        }
    }

    private void ReconcileCore()
    {
        var requestJson = BuildRequest();
        DadKranglerPrivacyLeaseResponse? status;
        try
        {
            status = ReadResponse(getStatus(requestJson));
        }
        catch (Exception exception)
        {
            RecordFailure(exception, "status");
            return;
        }

        if (status == null)
        {
            RecordInvalidResponse("status");
            return;
        }
        if (IsEffectiveOwnedLease(status))
        {
            mayOwnLease = true;
            snapshot = FromResponse(status, true, "dad-krangler-privacy-owned");
            return;
        }

        DadKranglerPrivacyLeaseResponse? acquired;
        try
        {
            acquired = ReadResponse(acquire(requestJson));
        }
        catch (Exception exception)
        {
            RecordFailure(exception, "acquire");
            return;
        }

        if (acquired == null)
        {
            RecordInvalidResponse("acquire");
            return;
        }

        mayOwnLease = IsEffectiveOwnedLease(acquired);
        snapshot = FromResponse(
            acquired,
            mayOwnLease,
            mayOwnLease ? "dad-krangler-privacy-acquired" : "dad-krangler-privacy-acquire-rejected");
    }

    private void ReleaseCore()
    {
        try
        {
            var response = ReadResponse(release(BuildRequest()));
            mayOwnLease = false;
            snapshot = response == null
                ? new DadKranglerPrivacyLeaseSnapshot(
                    false,
                    false,
                    false,
                    "dad-krangler-privacy-release-response-invalid",
                    "Krangler privacy lease release returned an invalid response.",
                    "Invalid IPC response.")
                : FromResponse(response, false, "dad-krangler-privacy-released") with
                {
                    Desired = false,
                    LeaseActive = false,
                    OwnedByThisProcess = false,
                };
        }
        catch (Exception exception)
        {
            mayOwnLease = false;
            RecordFailure(exception, "release", desiredState: false);
        }
    }

    private string BuildRequest()
        => DadIpcJson.Serialize(new DadKranglerPrivacyLeaseRequest { Token = token });

    private static DadKranglerPrivacyLeaseResponse? ReadResponse(string? json)
        => string.IsNullOrWhiteSpace(json) || json.Length > MaximumResponseCharacters
            ? null
            : DadIpcJson.DeserializeRaw<DadKranglerPrivacyLeaseResponse>(json);

    private static bool IsEffectiveOwnedLease(DadKranglerPrivacyLeaseResponse response)
        => response.Ok &&
           response.LeaseActive &&
           response.OwnedByRequester &&
           response.NamePrivacyActive &&
           response.ChatPrivacyActive &&
           response.IncludesSelf;

    private DadKranglerPrivacyLeaseSnapshot FromResponse(
        DadKranglerPrivacyLeaseResponse response,
        bool owned,
        string fallbackSafeCode)
        => new(
            desired,
            response.LeaseActive,
            owned,
            string.IsNullOrWhiteSpace(response.Code) ? fallbackSafeCode : $"dad-krangler-{response.Code}",
            string.IsNullOrWhiteSpace(response.Status)
                ? owned ? "DAD owns Krangler name/chat privacy for this process." : "Krangler privacy lease is unavailable."
                : response.Status,
            response.Error ?? string.Empty);

    private void RecordInvalidResponse(string operation)
    {
        mayOwnLease = false;
        snapshot = new DadKranglerPrivacyLeaseSnapshot(
            desired,
            false,
            false,
            $"dad-krangler-privacy-{operation}-response-invalid",
            $"Krangler privacy lease {operation} returned an invalid response.",
            "Invalid IPC response.");
    }

    private void RecordFailure(Exception exception, string operation, bool? desiredState = null)
    {
        logFailure(exception, operation);
        snapshot = new DadKranglerPrivacyLeaseSnapshot(
            desiredState ?? desired,
            false,
            false,
            $"dad-krangler-privacy-{operation}-unavailable",
            "Krangler privacy integration is unavailable.",
            exception.GetType().Name);
    }

    private sealed class DadKranglerPrivacyLeaseRequest
    {
        public string Token { get; set; } = string.Empty;
    }

    private sealed class DadKranglerPrivacyLeaseResponse
    {
        public bool Ok { get; set; }
        public string Code { get; set; } = string.Empty;
        public bool LeaseActive { get; set; }
        public bool OwnedByRequester { get; set; }
        public bool NamePrivacyActive { get; set; }
        public bool ChatPrivacyActive { get; set; }
        public bool IncludesSelf { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }
}
