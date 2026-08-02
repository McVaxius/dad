using System.Text.Json;

namespace dad.Models;

public static class DadVermaxionHandoffContract
{
    public const int Version = 2;
    public const int LeaseSeconds = 15;
    public const string ReserveChannel = "VERMAXION.ReserveDadHandoffV2Json";
    public const string ReleaseChannel = "VERMAXION.ReleaseDadHandoffV2Json";
    public const string GrantedChannel = "VERMAXION.DadHandoffGrantedV2Json";
}

public enum DadVermaxionReservationState
{
    NotLoaded = 0,
    Unavailable = 1,
    Pending = 2,
    Granting = 3,
    Granted = 4,
    Released = 5,
    Rejected = 6,
}

public enum DadVermaxionReservationWireFormat
{
    Unavailable = 0,
    CanonicalString = 1,
    LegacyNumeric = 2,
}

public sealed class DadVermaxionReservationRequest
{
    public int Version { get; set; } = DadVermaxionHandoffContract.Version;
    public string OperationToken { get; set; } = string.Empty;
    public string SchedulerRunId { get; set; } = string.Empty;
    public string SlotId { get; set; } = string.Empty;
    public string AccountKey { get; set; } = string.Empty;
    public string CharacterKey { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public int LeaseSeconds { get; set; } = DadVermaxionHandoffContract.LeaseSeconds;
}

public sealed class DadVermaxionReservationStatus
{
    public int Version { get; set; }
    public string OperationToken { get; set; } = string.Empty;
    public string SchedulerRunId { get; set; } = string.Empty;
    public string SlotId { get; set; } = string.Empty;
    public string AccountKey { get; set; } = string.Empty;
    public string CharacterKey { get; set; } = string.Empty;
    public DadVermaxionReservationState State { get; set; } = DadVermaxionReservationState.NotLoaded;
    public DadVermaxionReservationWireFormat WireFormat { get; set; }
    public bool CompatibilityFallbackEligible { get; set; }
    public string VermaxionActivity { get; set; } = string.Empty;
    public string VermaxionState { get; set; } = string.Empty;
    public bool AutoRetainerBusyKnown { get; set; }
    public bool AutoRetainerBusy { get; set; }
    public bool MultiModeKnown { get; set; }
    public bool MultiModeEnabled { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LeaseExpiresUtc { get; set; }
    public string Summary { get; set; } = string.Empty;

    public bool RequiresWait => State is DadVermaxionReservationState.Pending
        or DadVermaxionReservationState.Granting;
    public bool IsGranted => State == DadVermaxionReservationState.Granted;
    public bool IsRejected => State == DadVermaxionReservationState.Rejected;
    public bool UsesLegacyBoundary => State is DadVermaxionReservationState.NotLoaded
        or DadVermaxionReservationState.Unavailable
        or DadVermaxionReservationState.Released;

    public bool IsAuthoritativeFor(string operationToken)
        => IsAuthoritativeFor(operationToken, DateTime.UtcNow);

    public bool IsAuthoritativeFor(string operationToken, DateTime nowUtc)
        => !string.IsNullOrWhiteSpace(operationToken) &&
           !string.IsNullOrWhiteSpace(OperationToken) &&
           string.Equals(OperationToken, operationToken.Trim(), StringComparison.OrdinalIgnoreCase) &&
           State != DadVermaxionReservationState.Rejected &&
           State != DadVermaxionReservationState.Released &&
           (!LeaseExpiresUtc.HasValue || LeaseExpiresUtc.Value > EnsureUtc(nowUtc));

    public DadVermaxionReservationStatus Clone()
        => (DadVermaxionReservationStatus)MemberwiseClone();

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}

public static class DadVermaxionReleaseProofRules
{
    /// <summary>
    /// A synchronous v2 Release response proves cleanup only when it names the exact token and
    /// reports either Released or the provider's no-owner Rejected state. Empty, mismatched, or
    /// unparsable responses remain unproven and must be retried.
    /// </summary>
    public static bool ProvesNoOwnedReservation(
        DadVermaxionReservationStatus? status,
        string? requestedOperationToken)
        => status != null &&
           status.Version == DadVermaxionHandoffContract.Version &&
           status.WireFormat != DadVermaxionReservationWireFormat.Unavailable &&
           status.State is DadVermaxionReservationState.Released or DadVermaxionReservationState.Rejected &&
           !string.IsNullOrWhiteSpace(requestedOperationToken) &&
           !string.IsNullOrWhiteSpace(status.OperationToken) &&
           string.Equals(
               status.OperationToken.Trim(),
               requestedOperationToken.Trim(),
               StringComparison.OrdinalIgnoreCase);
}

public static class DadVermaxionReservationParser
{
    public static DadVermaxionReservationStatus Parse(string? json, DateTime observedAtUtc, string invocationError = "")
    {
        observedAtUtc = EnsureUtc(observedAtUtc);
        if (!string.IsNullOrWhiteSpace(invocationError) || string.IsNullOrWhiteSpace(json))
        {
            return new DadVermaxionReservationStatus
            {
                State = DadVermaxionReservationState.Unavailable,
                CompatibilityFallbackEligible = true,
                ObservedAtUtc = observedAtUtc,
                Summary = string.IsNullOrWhiteSpace(invocationError)
                    ? "VERMAXION v2 reservation IPC is unavailable."
                    : $"VERMAXION v2 reservation IPC is unavailable: {invocationError.Trim()}",
            };
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var versionElement) ||
                !versionElement.TryGetInt32(out var version) ||
                version != DadVermaxionHandoffContract.Version)
            {
                return Unavailable(observedAtUtc, $"Unsupported VERMAXION handoff contract version {versionElement}.");
            }

            if (!TryReadState(root, out var state, out var wireFormat, out var rawState))
                return Unavailable(observedAtUtc, $"Unknown VERMAXION reservation state '{rawState}'.");

            return new DadVermaxionReservationStatus
            {
                Version = version,
                OperationToken = ReadString(root, "operationToken"),
                SchedulerRunId = ReadString(root, "schedulerRunId"),
                SlotId = ReadString(root, "slotId"),
                AccountKey = ReadString(root, "accountKey"),
                CharacterKey = ReadString(root, "characterKey"),
                State = state,
                WireFormat = wireFormat,
                VermaxionActivity = ReadString(root, "vermaxionActivity"),
                VermaxionState = ReadString(root, "vermaxionState"),
                AutoRetainerBusyKnown = ReadBool(root, "autoRetainerBusyKnown"),
                AutoRetainerBusy = ReadBool(root, "autoRetainerBusy"),
                MultiModeKnown = ReadBool(root, "multiModeKnown"),
                MultiModeEnabled = ReadBool(root, "multiModeEnabled"),
                CreatedAtUtc = ReadDate(root, "createdAtUtc"),
                UpdatedAtUtc = ReadDate(root, "updatedAtUtc"),
                LeaseExpiresUtc = ReadNullableDate(root, "leaseExpiresUtc"),
                ObservedAtUtc = observedAtUtc,
                Summary = ReadString(root, "summary"),
            };
        }
        catch (Exception ex)
        {
            return Unavailable(observedAtUtc, $"Malformed VERMAXION reservation JSON: {ex.Message}");
        }
    }

    public static DadVermaxionReservationStatus NotLoaded(DateTime observedAtUtc)
        => new()
        {
            State = DadVermaxionReservationState.NotLoaded,
            ObservedAtUtc = EnsureUtc(observedAtUtc),
            Summary = "VERMAXION is not loaded; DAD will use the AutoRetainer character boundary.",
        };

    public static DadVermaxionReservationStatus Renewing(
        DadVermaxionReservationRequest request,
        DateTime observedAtUtc,
        string detail = "")
    {
        observedAtUtc = EnsureUtc(observedAtUtc);
        return new DadVermaxionReservationStatus
        {
            Version = DadVermaxionHandoffContract.Version,
            OperationToken = request.OperationToken,
            SchedulerRunId = request.SchedulerRunId,
            SlotId = request.SlotId,
            AccountKey = request.AccountKey,
            CharacterKey = request.CharacterKey,
            State = DadVermaxionReservationState.Unavailable,
            CompatibilityFallbackEligible = true,
            VermaxionActivity = "ReservationRenewal",
            VermaxionState = "Unavailable",
            CreatedAtUtc = EnsureUtc(request.RequestedAtUtc),
            UpdatedAtUtc = observedAtUtc,
            ObservedAtUtc = observedAtUtc,
            Summary = string.IsNullOrWhiteSpace(detail)
                ? "VERMAXION reloaded/unavailable; renewing handoff."
                : $"VERMAXION reloaded/unavailable; renewing handoff: {detail.Trim()}",
        };
    }

    public static DadVermaxionReservationStatus BindToRequest(
        DadVermaxionReservationStatus status,
        DadVermaxionReservationRequest request)
    {
        status.OperationToken = string.IsNullOrWhiteSpace(status.OperationToken) ? request.OperationToken : status.OperationToken;
        status.SchedulerRunId = string.IsNullOrWhiteSpace(status.SchedulerRunId) ? request.SchedulerRunId : status.SchedulerRunId;
        status.SlotId = string.IsNullOrWhiteSpace(status.SlotId) ? request.SlotId : status.SlotId;
        status.AccountKey = string.IsNullOrWhiteSpace(status.AccountKey) ? request.AccountKey : status.AccountKey;
        status.CharacterKey = string.IsNullOrWhiteSpace(status.CharacterKey) ? request.CharacterKey : status.CharacterKey;
        status.CreatedAtUtc = status.CreatedAtUtc == DateTime.MinValue ? EnsureUtc(request.RequestedAtUtc) : status.CreatedAtUtc;
        return status;
    }

    private static DadVermaxionReservationStatus Unavailable(DateTime observedAtUtc, string summary)
        => new()
        {
            State = DadVermaxionReservationState.Unavailable,
            ObservedAtUtc = observedAtUtc,
            Summary = summary,
        };

    private static bool TryReadState(
        JsonElement root,
        out DadVermaxionReservationState state,
        out DadVermaxionReservationWireFormat wireFormat,
        out string rawState)
    {
        state = DadVermaxionReservationState.Unavailable;
        wireFormat = DadVermaxionReservationWireFormat.Unavailable;
        rawState = string.Empty;
        if (!root.TryGetProperty("state", out var value))
            return false;

        if (value.ValueKind == JsonValueKind.String)
        {
            rawState = value.GetString()?.Trim() ?? string.Empty;
            state = rawState.ToUpperInvariant() switch
            {
                "PENDING" => DadVermaxionReservationState.Pending,
                "GRANTING" => DadVermaxionReservationState.Granting,
                "GRANTED" => DadVermaxionReservationState.Granted,
                "RELEASED" => DadVermaxionReservationState.Released,
                "REJECTED" => DadVermaxionReservationState.Rejected,
                _ => DadVermaxionReservationState.Unavailable,
            };
            if (state == DadVermaxionReservationState.Unavailable)
                return false;

            wireFormat = DadVermaxionReservationWireFormat.CanonicalString;
            return true;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var numericState))
        {
            rawState = value.GetRawText();
            return false;
        }

        rawState = numericState.ToString(System.Globalization.CultureInfo.InvariantCulture);
        state = numericState switch
        {
            0 => DadVermaxionReservationState.Pending,
            1 => DadVermaxionReservationState.Granting,
            2 => DadVermaxionReservationState.Granted,
            3 => DadVermaxionReservationState.Released,
            4 => DadVermaxionReservationState.Rejected,
            _ => DadVermaxionReservationState.Unavailable,
        };
        if (state == DadVermaxionReservationState.Unavailable)
            return false;

        wireFormat = DadVermaxionReservationWireFormat.LegacyNumeric;
        return true;
    }

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static bool ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTime ReadDate(JsonElement root, string name)
        => ReadNullableDate(root, name) ?? DateTime.MinValue;

    private static DateTime? ReadNullableDate(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out var result)
            ? EnsureUtc(result)
            : null;

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}

public readonly record struct DadVermaxionAuthorityView(
    bool Authoritative,
    bool Held,
    string Activity,
    string State,
    string Summary,
    DadVermaxionMutationAuthorization MutationAuthorization,
    DadVermaxionCompatibilityEvidence CompatibilityEvidence);

public enum DadVermaxionMutationAuthorization
{
    None = 0,
    Granted = 1,
    CompatibilityIdle = 2,
}

public readonly record struct DadVermaxionCompatibilityEvidence(
    bool VermaxionIdle,
    bool AutoRetainerReadableIdle,
    bool MultiModeDisabled,
    bool SuppressionReadableAndAvailable)
{
    public bool Complete => VermaxionIdle &&
                            AutoRetainerReadableIdle &&
                            MultiModeDisabled &&
                            SuppressionReadableAndAvailable;

    public static DadVermaxionCompatibilityEvidence Evaluate(
        DadVermaxionReadinessStatus legacyStatus,
        bool autoRetainerAvailable,
        bool autoRetainerBusy,
        bool multiModeEnabled,
        bool suppressionReadable,
        bool autoRetainerSuppressed,
        bool dadOwnsSuppression)
        => new(
            legacyStatus.Kind == DadVermaxionReadinessKind.Idle,
            autoRetainerAvailable && !autoRetainerBusy,
            autoRetainerAvailable && !multiModeEnabled,
            autoRetainerAvailable && suppressionReadable &&
            (!autoRetainerSuppressed || dadOwnsSuppression));
}

public static class DadVermaxionAuthorityRules
{
    public static DadVermaxionAuthorityView Resolve(
        string operationToken,
        DadVermaxionReservationStatus reservation,
        DadVermaxionReadinessStatus legacyStatus,
        DadVermaxionCompatibilityEvidence compatibilityEvidence = default)
    {
        var authoritative = reservation.IsAuthoritativeFor(operationToken);
        if (!authoritative)
        {
            return new DadVermaxionAuthorityView(
                false,
                legacyStatus.IsHeld,
                legacyStatus.Activity,
                legacyStatus.State,
                legacyStatus.Summary,
                DadVermaxionMutationAuthorization.None,
                compatibilityEvidence);
        }

        if (reservation.IsGranted)
        {
            return new DadVermaxionAuthorityView(
                true,
                false,
                string.IsNullOrWhiteSpace(reservation.VermaxionActivity)
                    ? "Reservation"
                    : reservation.VermaxionActivity,
                string.IsNullOrWhiteSpace(reservation.VermaxionState)
                    ? reservation.State.ToString()
                    : reservation.VermaxionState,
                reservation.Summary,
                DadVermaxionMutationAuthorization.Granted,
                compatibilityEvidence);
        }

        if (reservation.State == DadVermaxionReservationState.Unavailable &&
            reservation.CompatibilityFallbackEligible &&
            compatibilityEvidence.Complete)
        {
            return new DadVermaxionAuthorityView(
                true,
                false,
                "CompatibilityHandoff",
                "IdleVerified",
                "Compatibility handoff: VERMAXION idle / AR idle",
                DadVermaxionMutationAuthorization.CompatibilityIdle,
                compatibilityEvidence);
        }

        return new DadVermaxionAuthorityView(
            true,
            true,
            string.IsNullOrWhiteSpace(reservation.VermaxionActivity)
                ? "Reservation"
                : reservation.VermaxionActivity,
            string.IsNullOrWhiteSpace(reservation.VermaxionState)
                ? reservation.State.ToString()
                : reservation.VermaxionState,
            reservation.Summary,
            DadVermaxionMutationAuthorization.None,
            compatibilityEvidence);
    }
}
