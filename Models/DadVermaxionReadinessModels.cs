using System.Text.Json;

namespace dad.Models;

public enum DadVermaxionReadinessKind
{
    NotLoaded,
    Idle,
    Busy,
    Unavailable,
}

public sealed class DadVermaxionReadinessStatus
{
    public DadVermaxionReadinessKind Kind { get; init; }
    public int ContractVersion { get; init; }
    public bool IsHeld => Kind is DadVermaxionReadinessKind.Busy or DadVermaxionReadinessKind.Unavailable;
    public string Activity { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public DateTime? GeneratedAtUtc { get; init; }
    public DateTime ObservedAtUtc { get; init; } = DateTime.UtcNow;
}

public static class DadVermaxionStatusParser
{
    public const int SupportedVersion = 1;

    public static DadVermaxionReadinessStatus Parse(
        bool pluginLoaded,
        string? json,
        DateTime observedAtUtc,
        string invocationError = "")
    {
        observedAtUtc = EnsureUtc(observedAtUtc);
        if (!pluginLoaded)
        {
            return new DadVermaxionReadinessStatus
            {
                Kind = DadVermaxionReadinessKind.NotLoaded,
                Summary = "VERMAXION is not loaded.",
                ObservedAtUtc = observedAtUtc,
            };
        }

        if (!string.IsNullOrWhiteSpace(invocationError) || string.IsNullOrWhiteSpace(json))
            return Unavailable(observedAtUtc, invocationError);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var versionElement) ||
                !versionElement.TryGetInt32(out var version))
            {
                return Unavailable(observedAtUtc, "VERMAXION status contract version is missing or malformed.");
            }

            if (version != SupportedVersion)
                return Unavailable(observedAtUtc, $"Unsupported VERMAXION status contract version {version}.", version);

            if (!root.TryGetProperty("isBusy", out var busyElement) ||
                busyElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Unavailable(observedAtUtc, "VERMAXION status isBusy is missing or malformed.", version);
            }

            var busy = busyElement.GetBoolean();
            var activity = ReadString(root, "activity");
            var state = ReadString(root, "state");
            var summary = ReadString(root, "summary");
            DateTime? generatedAtUtc = null;
            if (root.TryGetProperty("generatedAtUtc", out var generatedElement) &&
                generatedElement.ValueKind == JsonValueKind.String &&
                generatedElement.TryGetDateTime(out var generated))
            {
                generatedAtUtc = EnsureUtc(generated);
            }

            return new DadVermaxionReadinessStatus
            {
                Kind = busy ? DadVermaxionReadinessKind.Busy : DadVermaxionReadinessKind.Idle,
                ContractVersion = version,
                Activity = string.IsNullOrWhiteSpace(activity) ? busy ? "Automation" : "Idle" : activity,
                State = string.IsNullOrWhiteSpace(state) ? busy ? "Busy" : "Idle" : state,
                Summary = string.IsNullOrWhiteSpace(summary)
                    ? busy ? "VERMAXION reports active automation." : "VERMAXION is idle."
                    : summary,
                GeneratedAtUtc = generatedAtUtc,
                ObservedAtUtc = observedAtUtc,
            };
        }
        catch (Exception ex)
        {
            return Unavailable(observedAtUtc, $"Malformed VERMAXION status JSON: {ex.Message}");
        }
    }

    private static DadVermaxionReadinessStatus Unavailable(DateTime observedAtUtc, string detail, int version = 0)
        => new()
        {
            Kind = DadVermaxionReadinessKind.Unavailable,
            ContractVersion = version,
            Activity = "StatusUnavailable",
            State = "Unavailable",
            Summary = string.IsNullOrWhiteSpace(detail)
                ? "Waiting for VERMAXION status."
                : $"Waiting for VERMAXION status: {detail.Trim()}",
            ObservedAtUtc = observedAtUtc,
        };

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}

public static class DadExternalAutomationRules
{
    public static bool ApplyPostArReadiness(bool basePostArReady, DadVermaxionReadinessStatus status)
        => basePostArReady && !status.IsHeld;
}

public enum DadWakeTimeoutStage
{
    None,
    Participant,
    Vermaxion,
    AutoRetainer,
}

public static class DadWakeStageTimeoutPolicy
{
    public static DadWakeTimeoutStage Classify(DadSchedulerSlotState slot)
        => slot.Ready
            ? DadWakeTimeoutStage.None
            : slot.TakeoverStage == DadWakeTakeoverStage.WaitingForExternalAutomation
                ? DadWakeTimeoutStage.Vermaxion
                : slot.TakeoverStage is DadWakeTakeoverStage.WaitingForAutoRetainer
                    or DadWakeTakeoverStage.AwaitingArHook
                    or DadWakeTakeoverStage.PostprocessOwned
                    or DadWakeTakeoverStage.Prepared
                    ? DadWakeTimeoutStage.AutoRetainer
                    : DadWakeTimeoutStage.Participant;

    public static void Observe(DadSchedulerSlotState slot, DateTime nowUtc)
    {
        nowUtc = EnsureUtc(nowUtc);
        AccrueCurrent(slot, nowUtc);
        var stage = Classify(slot);
        slot.TimeoutStage = stage;
        slot.TimeoutStageObservedUtc = nowUtc;
        switch (stage)
        {
            case DadWakeTimeoutStage.Vermaxion:
                slot.VermaxionHoldStartedUtc ??= nowUtc;
                break;
            case DadWakeTimeoutStage.AutoRetainer:
                slot.AutoRetainerWaitStartedUtc ??= nowUtc;
                break;
            case DadWakeTimeoutStage.Participant:
                slot.ParticipantWaitStartedUtc ??= nowUtc;
                break;
        }
    }

    public static TimeSpan GetRemaining(
        DadSchedulerSlotState slot,
        DateTime nowUtc,
        int vermaxionTimeoutSeconds,
        int autoRetainerTimeoutSeconds,
        int participantTimeoutSeconds)
    {
        nowUtc = EnsureUtc(nowUtc);
        var budget = GetBudgetSeconds(slot.TimeoutStage, vermaxionTimeoutSeconds, autoRetainerTimeoutSeconds, participantTimeoutSeconds);
        var elapsed = GetElapsedSeconds(slot, slot.TimeoutStage, nowUtc);
        return TimeSpan.FromSeconds(Math.Max(0, budget - elapsed));
    }

    public static bool IsTimedOut(
        DadSchedulerSlotState slot,
        DateTime nowUtc,
        int vermaxionTimeoutSeconds,
        int autoRetainerTimeoutSeconds,
        int participantTimeoutSeconds)
    {
        if (slot.TimeoutStage == DadWakeTimeoutStage.None)
            return false;

        var remaining = GetRemaining(
            slot,
            nowUtc,
            vermaxionTimeoutSeconds,
            autoRetainerTimeoutSeconds,
            participantTimeoutSeconds);
        return remaining <= TimeSpan.Zero;
    }

    public static int GetBudgetSeconds(
        DadWakeTimeoutStage stage,
        int vermaxionTimeoutSeconds,
        int autoRetainerTimeoutSeconds,
        int participantTimeoutSeconds)
        => stage switch
        {
            DadWakeTimeoutStage.Vermaxion => Math.Max(3600, vermaxionTimeoutSeconds <= 0 ? 5400 : vermaxionTimeoutSeconds),
            DadWakeTimeoutStage.AutoRetainer => Math.Max(60, autoRetainerTimeoutSeconds <= 0 ? 1200 : autoRetainerTimeoutSeconds),
            DadWakeTimeoutStage.Participant => Math.Max(30, participantTimeoutSeconds <= 0 ? 300 : participantTimeoutSeconds),
            _ => 0,
        };

    private static void AccrueCurrent(DadSchedulerSlotState slot, DateTime nowUtc)
    {
        if (!slot.TimeoutStageObservedUtc.HasValue || slot.TimeoutStage == DadWakeTimeoutStage.None)
            return;

        var elapsed = Math.Max(0, (nowUtc - EnsureUtc(slot.TimeoutStageObservedUtc.Value)).TotalSeconds);
        switch (slot.TimeoutStage)
        {
            case DadWakeTimeoutStage.Vermaxion:
                slot.VermaxionHoldElapsedSeconds += elapsed;
                break;
            case DadWakeTimeoutStage.AutoRetainer:
                slot.AutoRetainerWaitElapsedSeconds += elapsed;
                break;
            case DadWakeTimeoutStage.Participant:
                slot.ParticipantWaitElapsedSeconds += elapsed;
                break;
        }
    }

    private static double GetElapsedSeconds(DadSchedulerSlotState slot, DadWakeTimeoutStage stage, DateTime nowUtc)
    {
        var accrued = stage switch
        {
            DadWakeTimeoutStage.Vermaxion => slot.VermaxionHoldElapsedSeconds,
            DadWakeTimeoutStage.AutoRetainer => slot.AutoRetainerWaitElapsedSeconds,
            DadWakeTimeoutStage.Participant => slot.ParticipantWaitElapsedSeconds,
            _ => 0,
        };
        if (slot.TimeoutStage != stage || !slot.TimeoutStageObservedUtc.HasValue)
            return accrued;
        return accrued + Math.Max(0, (nowUtc - EnsureUtc(slot.TimeoutStageObservedUtc.Value)).TotalSeconds);
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
