using System.Globalization;
using System.Text.Json;

namespace dad.Models;

public enum DadPreDutyRepairMode
{
    Self = 0,
    NpcExcludingInns = 1,
    NearbyNpcNoTeleportOrInn = 2,
    NpcWithInnFallback = 3,
    NpcThenInn = 4,
}

public enum DadRepairInnDestination
{
    Automatic = 0,
    Uldah = 1,
    Gridania = 2,
    Limsa = 3,
    Ishgard = 4,
    Crystarium = 5,
    Sharlayan = 6,
    Tuliyollal = 7,
}

public sealed class DadPreDutyRepairPolicy
{
    public const int DefaultThresholdPercent = 75;

    public bool Enabled { get; set; }
    public int ThresholdPercent { get; set; } = DefaultThresholdPercent;
    public DadPreDutyRepairMode Mode { get; set; } = DadPreDutyRepairMode.Self;
    public DadRepairInnDestination InnDestination { get; set; } = DadRepairInnDestination.Automatic;
    public bool RequiresInnCompletion => Mode == DadPreDutyRepairMode.NpcThenInn;
    public bool RequiresInnRepairSchema => Enabled && Mode is DadPreDutyRepairMode.NpcWithInnFallback or DadPreDutyRepairMode.NpcThenInn;

    private static readonly IReadOnlyDictionary<DadRepairInnDestination, string> InnTokens =
        new Dictionary<DadRepairInnDestination, string>
        {
            [DadRepairInnDestination.Uldah] = "uldah",
            [DadRepairInnDestination.Gridania] = "gridania",
            [DadRepairInnDestination.Limsa] = "limsa",
            [DadRepairInnDestination.Ishgard] = "ishgard",
            [DadRepairInnDestination.Crystarium] = "crystarium",
            [DadRepairInnDestination.Sharlayan] = "sharlayan",
            [DadRepairInnDestination.Tuliyollal] = "tuliyollal",
        };

    public DadPreDutyRepairPolicy Normalize()
    {
        ThresholdPercent = Math.Clamp(
            ThresholdPercent <= 0 ? DefaultThresholdPercent : ThresholdPercent,
            1,
            100);
        if (!Enum.IsDefined(Mode))
            Mode = DadPreDutyRepairMode.Self;
        if (!Enum.IsDefined(InnDestination))
            InnDestination = DadRepairInnDestination.Automatic;
        return this;
    }

    public DadPreDutyRepairPolicy Clone()
        => new()
        {
            Enabled = Enabled,
            ThresholdPercent = ThresholdPercent,
            Mode = Mode,
            InnDestination = InnDestination,
        };

    public string AdsMode => Mode switch
    {
        DadPreDutyRepairMode.NpcExcludingInns => "npc-no-inn",
        DadPreDutyRepairMode.NearbyNpcNoTeleportOrInn => "npc-no-teleport-no-inn",
        DadPreDutyRepairMode.NpcWithInnFallback => "npc",
        DadPreDutyRepairMode.NpcThenInn => InnTokens.TryGetValue(InnDestination, out var token)
            ? $"npc-yes-inn-{token}"
            : "npc-yes-inn",
        _ => "self",
    };

    public static bool TryFromAdsMode(string? adsMode, out DadPreDutyRepairPolicy policy)
    {
        policy = new();
        var mode = (adsMode ?? string.Empty).Trim().ToLowerInvariant();
        var parsed = mode switch
        {
            "self" => DadPreDutyRepairMode.Self,
            "npc-no-inn" => DadPreDutyRepairMode.NpcExcludingInns,
            "npc-no-teleport-no-inn" => DadPreDutyRepairMode.NearbyNpcNoTeleportOrInn,
            "npc" => DadPreDutyRepairMode.NpcWithInnFallback,
            "npc-yes-inn" => DadPreDutyRepairMode.NpcThenInn,
            _ => (DadPreDutyRepairMode?)null,
        };
        if (parsed.HasValue)
        {
            policy.Mode = parsed.Value;
            return true;
        }
        const string prefix = "npc-yes-inn-";
        if (!mode.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        foreach (var (destination, token) in InnTokens)
        {
            if (!string.Equals(mode[prefix.Length..], token, StringComparison.Ordinal))
                continue;
            policy.Mode = DadPreDutyRepairMode.NpcThenInn;
            policy.InnDestination = destination;
            return true;
        }
        return false;
    }
}

public static class DadPreDutyRepairRules
{
    public static bool IsRequired(
        DadPreDutyRepairPolicy? policy,
        DadModuleId moduleId,
        DadRunRequest? request = null)
    {
        var normalized = (policy ?? new DadPreDutyRepairPolicy()).Clone().Normalize();
        if (!normalized.Enabled)
            return false;

        if (moduleId == DadModuleId.Mixed)
            return HasQueueCapableChild(request);

        return moduleId is
            DadModuleId.Duty or
            DadModuleId.Msq or
            DadModuleId.DutySupport or
            DadModuleId.Trust or
            DadModuleId.PremadeDuty or
            DadModuleId.DailyMsq or
            DadModuleId.Mogtome or
            DadModuleId.LootGoblin or
            DadModuleId.Commendation or
            DadModuleId.CustomDuty;
    }

    private static bool HasQueueCapableChild(DadRunRequest? request)
        => request != null &&
           (request.Dungeon != null ||
            request.Msq != null ||
            request.DutySupport != null ||
            request.Trust != null ||
            request.PremadeDuty != null ||
            request.DailyMsq != null ||
            request.Mogtome != null ||
            request.LootGoblin != null ||
            request.Commendation != null ||
            request.CustomDuty != null);
}

public readonly record struct DadEquippedDurabilityObservation(
    bool Readable,
    int MinimumConditionPercent,
    string Summary)
{
    public static DadEquippedDurabilityObservation Unreadable(string summary)
        => new(false, 0, summary);

    public static DadEquippedDurabilityObservation ReadableAt(int minimumConditionPercent)
        => new(
            true,
            Math.Clamp(minimumConditionPercent, 0, 100),
            $"Lowest equipped durability is {Math.Clamp(minimumConditionPercent, 0, 100)}%.");
}

public readonly record struct DadAdsRepairObservation(
    bool Available,
    bool Readable,
    bool UtilityRunning,
    bool RepairRunning,
    string UtilityTask,
    string UtilityMode,
    string Summary)
{
    public DateTime? CompletedAtUtc { get; init; }
    public string LastFailure { get; init; } = string.Empty;
    public string LastSuccess { get; init; } = string.Empty;
    public static DadAdsRepairObservation Absent(string summary = "ADS is not loaded.")
        => new(false, false, false, false, string.Empty, string.Empty, summary);

    public static DadAdsRepairObservation Unreadable(string summary)
        => new(true, false, false, false, string.Empty, string.Empty, summary);

    public static DadAdsRepairObservation Idle(string summary = "ADS utility is idle.")
        => new(true, true, false, false, string.Empty, string.Empty, summary);

    public static DadAdsRepairObservation Running(
        bool repair,
        string task,
        string mode,
        string summary = "ADS utility is running.")
        => new(true, true, true, repair, task, mode, summary);
}

public enum DadAdsRepairInvocationOutcome
{
    Accepted = 0,
    ExplicitFalse = 1,
    Uncertain = 2,
}

public readonly record struct DadAdsRepairInvocationResult(
    DadAdsRepairInvocationOutcome Outcome,
    string Summary);

public readonly record struct DadRepairRetrySafetyObservation(bool Readable, bool Safe, string Summary);

public static class DadAdsRepairStatusRules
{
    public static DadAdsRepairObservation Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("utilityRunning", out var running) ||
                running.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return DadAdsRepairObservation.Unreadable("ADS.GetStatusJson omitted readable utilityRunning truth.");
            string Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty : string.Empty;
            var task = Read("utilityTask");
            var mode = Read("utilityMode");
            var status = Read("utilityStatus");
            var observation = running.GetBoolean()
                ? DadAdsRepairObservation.Running(DadPreDutyRepairPolicy.TryFromAdsMode(mode, out _) ||
                    task.Contains("repair", StringComparison.OrdinalIgnoreCase), task, mode, status)
                : DadAdsRepairObservation.Idle(string.IsNullOrWhiteSpace(status) ? "ADS utility is idle." : status);
            return observation with
            {
                LastFailure = Read("utilityLastFailure"), LastSuccess = Read("utilityLastSuccess"),
                CompletedAtUtc = DateTimeOffset.TryParse(Read("utilityCompletedAtUtc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var completed)
                    ? completed.UtcDateTime : null,
            };
        }
        catch (JsonException ex)
        {
            return DadAdsRepairObservation.Unreadable($"ADS repair status JSON is invalid: {ex.Message}");
        }
    }
}

public enum DadPreDutyRepairAction
{
    Ready = 0,
    Wait = 1,
    InvokeAds = 2,
    Reject = 3,
}

public readonly record struct DadPreDutyRepairDecision(
    DadPreDutyRepairAction Action,
    string Summary,
    string AdsMode = "")
{
    public bool IsTerminal => Action is DadPreDutyRepairAction.Ready or DadPreDutyRepairAction.Reject;
}

public sealed class DadPreDutyRepairGate
{
    private bool innRepairObserved;
    public static readonly TimeSpan TruthGracePeriod = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan ExplicitFalseRetryInterval = TimeSpan.FromSeconds(30);
    public const int MaxInvocationAttempts = 3;
    public static readonly TimeSpan RecoverySettlePeriod = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RecoverySettleTimeout = TimeSpan.FromSeconds(30);

    private DateTime startedAtUtc = DateTime.MinValue;
    private DateTime adsUnreadableSinceUtc = DateTime.MinValue;
    private DateTime nextAttemptUtc = DateTime.MinValue;
    private bool invocationPending;
    private bool invocationAccepted;
    private bool terminalFailure;
    private int invocationCount;
    private string terminalSummary = string.Empty;
    private DateTime invocationRequestedAtUtc;
    private DateTime? baselineCompletionUtc;
    private bool ownRepairObserved;
    private bool ownRepairContradicted;
    private bool recoveryPending;
    private bool recoveryRetryUsed;
    private DateTime recoveryStartedUtc;
    private DateTime recoveryFailureCompletedUtc;
    private DateTime? recoverySafeSinceUtc;
    private DateTime? lastRecoverySafetyUtc;
    private string lastRepairFailure = string.Empty;

    public int InvocationCount => invocationCount;
    public bool InvocationAccepted => invocationAccepted;
    public bool InvocationPending => invocationPending;
    public bool IsRepairInProgress => invocationPending || invocationAccepted;
    public bool NeedsRecoverySafety => recoveryPending;

    public DadPreDutyRepairDecision Evaluate(
        DadPreDutyRepairPolicy? policy,
        DadModuleId moduleId,
        DadRunRequest? request,
        DadEquippedDurabilityObservation durability,
        DadAdsRepairObservation ads,
        DateTime nowUtc,
        DadRepairRetrySafetyObservation retrySafety = default)
    {
        var normalized = (policy ?? new DadPreDutyRepairPolicy()).Clone().Normalize();
        if (!DadPreDutyRepairRules.IsRequired(normalized, moduleId, request))
            return Ready("Pre-duty repair is disabled or does not apply to this module.");

        var now = EnsureUtc(nowUtc);
        if (startedAtUtc == DateTime.MinValue)
            startedAtUtc = now;

        if (terminalFailure)
            return Reject(terminalSummary);

        if (now - startedAtUtc >= OverallTimeout)
            return Fail((normalized.RequiresInnCompletion
                ? "Pre-duty repair/inn utility did not finish with sufficient durability within the 180-second timeout."
                : "Pre-duty repair did not prove sufficient durability within the 180-second timeout.") + FailureContext());

        if (!durability.Readable)
        {
            return now - startedAtUtc < TruthGracePeriod
                ? Wait("Waiting up to five seconds for readable equipped durability truth.")
                : Fail($"Equipped durability remained unreadable for five seconds. {durability.Summary}".Trim());
        }

        // A returning-to-inn utility still owns movement after durability improves.
        // Legacy repair routes retain their durability-only completion behavior.
        if (normalized.RequiresInnCompletion && ads.Available && ads.Readable && ads.RepairRunning)
            innRepairObserved = true;
        var durabilityReady = durability.MinimumConditionPercent >= normalized.ThresholdPercent;
        var waitForInn = normalized.RequiresInnCompletion && (invocationAccepted || innRepairObserved);
        if (ads.Available && ads.Readable && invocationAccepted)
        {
            if (ads.UtilityRunning)
            {
                if (ads.RepairRunning && DadPreDutyRepairPolicy.TryFromAdsMode(ads.UtilityMode, out var observed) &&
                    observed.Mode == normalized.Mode)
                    ownRepairObserved = true;
                else
                    ownRepairContradicted = true;
            }
            // ADS has no repair operation ID: require observed matching work and a new,
            // explicitly failed completion after our start. Idle alone is never replay proof.
            if (!recoveryPending && !ads.UtilityRunning && ownRepairObserved && !ownRepairContradicted &&
                ads.CompletedAtUtc is { } completed && completed >= invocationRequestedAtUtc && completed <= now &&
                (!baselineCompletionUtc.HasValue || completed > baselineCompletionUtc.Value) &&
                !string.IsNullOrWhiteSpace(ads.LastFailure) && string.IsNullOrWhiteSpace(ads.LastSuccess) &&
                string.Equals(ads.Summary, ads.LastFailure, StringComparison.Ordinal))
            {
                lastRepairFailure = ads.LastFailure;
                if (recoveryRetryUsed)
                    return Fail("ADS repair failed again after the single recovery attempt." + FailureContext());
                recoveryPending = true;
                recoveryStartedUtc = now;
                recoveryFailureCompletedUtc = completed;
            }
        }
        if (durabilityReady && !waitForInn && !recoveryPending)
        {
            return Ready(
                $"Equipped durability is sufficient: {durability.MinimumConditionPercent}% is not below {normalized.ThresholdPercent}%.");
        }

        if (!ads.Available)
            return Fail("Pre-duty repair is required, but ADS is not loaded.");

        if (!ads.Readable)
        {
            recoverySafeSinceUtc = null;
            lastRecoverySafetyUtc = null;
            if (adsUnreadableSinceUtc == DateTime.MinValue)
                adsUnreadableSinceUtc = now;
            return now - adsUnreadableSinceUtc < TruthGracePeriod
                ? Wait("Waiting up to five seconds for readable ADS utility truth.")
                : Fail($"ADS utility truth remained unreadable for five seconds. {ads.Summary}".Trim());
        }

        adsUnreadableSinceUtc = DateTime.MinValue;
        if (recoveryPending)
            return EvaluateRecovery(normalized, durabilityReady, ads, retrySafety, now);
        if (durabilityReady)
        {
            return ads.UtilityRunning
                ? Wait($"Equipped durability is sufficient; waiting for ADS repair/inn utility to finish. {ads.Summary}".Trim())
                : Ready($"Equipped durability is sufficient and ADS repair/inn utility is idle: {durability.MinimumConditionPercent}%.");
        }
        if (ads.UtilityRunning)
        {
            return ads.RepairRunning
                ? Wait($"Adopted the already-running ADS repair ({ads.UtilityMode}); waiting for durability proof.")
                : Wait($"Waiting for unrelated ADS utility '{ads.UtilityTask}' to finish before repair.");
        }

        if (invocationAccepted)
            return Wait("ADS accepted repair; waiting for equipped durability proof without retrying.");
        if (invocationPending)
            return Wait("ADS.StartRepair result is pending; duplicate invocation is not permitted.");
        if (now < nextAttemptUtc)
            return Wait($"ADS explicitly declined repair; waiting until {nextAttemptUtc:O} before the next bounded attempt.");
        if (invocationCount >= MaxInvocationAttempts)
            return Fail("ADS.StartRepair exhausted three explicit-false attempts.");

        return Invoke(normalized, ads, now);
    }

    private DadPreDutyRepairDecision Invoke(DadPreDutyRepairPolicy policy, DadAdsRepairObservation ads, DateTime now)
    {
        invocationPending = true;
        invocationRequestedAtUtc = now;
        baselineCompletionUtc = ads.CompletedAtUtc;
        ownRepairObserved = false;
        ownRepairContradicted = false;
        return new DadPreDutyRepairDecision(
            DadPreDutyRepairAction.InvokeAds,
            recoveryRetryUsed
                ? $"Invoke the single ADS repair recovery attempt ('{policy.AdsMode}')." + FailureContext()
                : $"Invoke ADS.StartRepair('{policy.AdsMode}') attempt {invocationCount + 1}/{MaxInvocationAttempts}.",
            policy.AdsMode);
    }

    private DadPreDutyRepairDecision EvaluateRecovery(DadPreDutyRepairPolicy policy, bool durabilityReady,
        DadAdsRepairObservation ads, DadRepairRetrySafetyObservation safety, DateTime now)
    {
        if (now - recoveryStartedUtc >= RecoverySettleTimeout)
            return Fail("ADS repair recovery could not prove safe travel/UI settlement within 30 seconds." + FailureContext());
        if (!ads.UtilityRunning && (ads.CompletedAtUtc != recoveryFailureCompletedUtc ||
            !string.Equals(ads.LastFailure, lastRepairFailure, StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(ads.LastSuccess)))
            return Fail("ADS completion changed during repair recovery; ownership is uncertain, no retry was sent." + FailureContext());
        if (ads.UtilityRunning || !safety.Readable || !safety.Safe)
        {
            recoverySafeSinceUtc = null;
            lastRecoverySafetyUtc = null;
            return Wait($"ADS repair recovery is waiting for idle travel and closed UI. {safety.Summary}".Trim() + FailureContext());
        }
        if (!lastRecoverySafetyUtc.HasValue || now < lastRecoverySafetyUtc.Value ||
            now - lastRecoverySafetyUtc.Value > TimeSpan.FromSeconds(2))
            recoverySafeSinceUtc = now;
        lastRecoverySafetyUtc = now;
        recoverySafeSinceUtc ??= now;
        if (now - recoverySafeSinceUtc.Value < RecoverySettlePeriod)
            return Wait("ADS repair recovery requires five continuous seconds of safe travel/UI settlement." + FailureContext());
        recoveryPending = false;
        invocationAccepted = false;
        ownRepairObserved = false;
        if (durabilityReady)
            return Ready("Equipped durability is sufficient after safe repair recovery; no second repair is needed.");
        recoveryRetryUsed = true;
        return Invoke(policy, ads, now);
    }

    public void RecordInvocationResult(DadAdsRepairInvocationResult result, DateTime nowUtc)
    {
        if (!invocationPending || terminalFailure || invocationAccepted)
            return;

        invocationPending = false;
        invocationCount++;
        switch (result.Outcome)
        {
            case DadAdsRepairInvocationOutcome.Accepted:
                invocationAccepted = true;
                break;
            case DadAdsRepairInvocationOutcome.ExplicitFalse:
                if (recoveryRetryUsed)
                {
                    Fail("ADS declined the single repair recovery attempt." + FailureContext());
                    break;
                }
                nextAttemptUtc = EnsureUtc(nowUtc) + ExplicitFalseRetryInterval;
                if (invocationCount >= MaxInvocationAttempts)
                {
                    terminalFailure = true;
                    terminalSummary = "ADS.StartRepair exhausted three explicit-false attempts.";
                }
                break;
            default:
                terminalFailure = true;
                terminalSummary = string.IsNullOrWhiteSpace(result.Summary)
                    ? "ADS.StartRepair ended with uncertain IPC acceptance; no retry is permitted."
                    : $"ADS.StartRepair ended with uncertain IPC acceptance; no retry is permitted. {result.Summary}";
                break;
        }
    }

    private DadPreDutyRepairDecision Fail(string summary)
    {
        terminalFailure = true;
        terminalSummary = summary;
        return Reject(summary);
    }

    private string FailureContext() => string.IsNullOrWhiteSpace(lastRepairFailure)
        ? string.Empty : $" ADS failure: {lastRepairFailure}";

    private static DadPreDutyRepairDecision Ready(string summary)
        => new(DadPreDutyRepairAction.Ready, summary);

    private static DadPreDutyRepairDecision Wait(string summary)
        => new(DadPreDutyRepairAction.Wait, summary);

    private static DadPreDutyRepairDecision Reject(string summary)
        => new(DadPreDutyRepairAction.Reject, summary);

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
