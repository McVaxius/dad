namespace dad.Models;

public enum DadPreDutyRepairMode
{
    Self = 0,
    NpcExcludingInns = 1,
    NearbyNpcNoTeleportOrInn = 2,
}

public sealed class DadPreDutyRepairPolicy
{
    public const int DefaultThresholdPercent = 75;

    public bool Enabled { get; set; }
    public int ThresholdPercent { get; set; } = DefaultThresholdPercent;
    public DadPreDutyRepairMode Mode { get; set; } = DadPreDutyRepairMode.Self;

    public DadPreDutyRepairPolicy Normalize()
    {
        ThresholdPercent = Math.Clamp(
            ThresholdPercent <= 0 ? DefaultThresholdPercent : ThresholdPercent,
            1,
            100);
        if (!Enum.IsDefined(Mode))
            Mode = DadPreDutyRepairMode.Self;
        return this;
    }

    public DadPreDutyRepairPolicy Clone()
        => new()
        {
            Enabled = Enabled,
            ThresholdPercent = ThresholdPercent,
            Mode = Mode,
        };

    public string AdsMode => Mode switch
    {
        DadPreDutyRepairMode.NpcExcludingInns => "npc-no-inn",
        DadPreDutyRepairMode.NearbyNpcNoTeleportOrInn => "npc-no-teleport-no-inn",
        _ => "self",
    };
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
    public static readonly TimeSpan TruthGracePeriod = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan ExplicitFalseRetryInterval = TimeSpan.FromSeconds(30);
    public const int MaxInvocationAttempts = 3;

    private DateTime startedAtUtc = DateTime.MinValue;
    private DateTime adsUnreadableSinceUtc = DateTime.MinValue;
    private DateTime nextAttemptUtc = DateTime.MinValue;
    private bool invocationPending;
    private bool invocationAccepted;
    private bool terminalFailure;
    private int invocationCount;
    private string terminalSummary = string.Empty;

    public int InvocationCount => invocationCount;
    public bool InvocationAccepted => invocationAccepted;
    public bool InvocationPending => invocationPending;
    public bool IsRepairInProgress => invocationPending || invocationAccepted;

    public DadPreDutyRepairDecision Evaluate(
        DadPreDutyRepairPolicy? policy,
        DadModuleId moduleId,
        DadRunRequest? request,
        DadEquippedDurabilityObservation durability,
        DadAdsRepairObservation ads,
        DateTime nowUtc)
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
            return Fail("Pre-duty repair did not prove sufficient durability within the 180-second timeout.");

        if (!durability.Readable)
        {
            return now - startedAtUtc < TruthGracePeriod
                ? Wait("Waiting up to five seconds for readable equipped durability truth.")
                : Fail($"Equipped durability remained unreadable for five seconds. {durability.Summary}".Trim());
        }

        // Durability is deliberately the only success proof. ADS status can only authorize or
        // explain repair work; it can never complete this gate by itself.
        if (durability.MinimumConditionPercent >= normalized.ThresholdPercent)
        {
            return Ready(
                $"Equipped durability is sufficient: {durability.MinimumConditionPercent}% is not below {normalized.ThresholdPercent}%.");
        }

        if (!ads.Available)
            return Fail("Pre-duty repair is required, but ADS is not loaded.");

        if (!ads.Readable)
        {
            if (adsUnreadableSinceUtc == DateTime.MinValue)
                adsUnreadableSinceUtc = now;
            return now - adsUnreadableSinceUtc < TruthGracePeriod
                ? Wait("Waiting up to five seconds for readable ADS utility truth.")
                : Fail($"ADS utility truth remained unreadable for five seconds. {ads.Summary}".Trim());
        }

        adsUnreadableSinceUtc = DateTime.MinValue;
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

        invocationPending = true;
        return new DadPreDutyRepairDecision(
            DadPreDutyRepairAction.InvokeAds,
            $"Invoke ADS.StartRepair('{normalized.AdsMode}') attempt {invocationCount + 1}/{MaxInvocationAttempts}.",
            normalized.AdsMode);
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

    private static DadPreDutyRepairDecision Ready(string summary)
        => new(DadPreDutyRepairAction.Ready, summary);

    private static DadPreDutyRepairDecision Wait(string summary)
        => new(DadPreDutyRepairAction.Wait, summary);

    private static DadPreDutyRepairDecision Reject(string summary)
        => new(DadPreDutyRepairAction.Reject, summary);

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
