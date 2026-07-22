namespace dad.Models;

public enum DadRouletteRewardProbeOperation
{
    Inspect = 0,
    Cancel = 1,
}

public enum DadRouletteRewardProbeOutcome
{
    Pending = 0,
    Received = 1,
    NotReceived = 2,
    Unknown = 3,
}

public sealed class DadRouletteRewardProbeRequestDto
{
    public string OperationId { get; set; } = Guid.NewGuid().ToString("N");
    public DadRouletteRewardProbeOperation Operation { get; set; }
    public string SchedulerRunId { get; set; } = string.Empty;
    public string ScheduleId { get; set; } = string.Empty;
    public string ScheduleRunId { get; set; } = string.Empty;
    public string ScheduleEntryId { get; set; } = string.Empty;
    public string SlotId { get; set; } = string.Empty;
    public DadWorkerSessionId RouteWorkerSessionId { get; set; } = new(string.Empty);
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong CharacterContentId { get; set; }
    public uint RouletteId { get; set; }
    public string RouletteKey { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;

    public DadRouletteRewardProbeRequestDto Clone()
        => new()
        {
            OperationId = OperationId,
            Operation = Operation,
            SchedulerRunId = SchedulerRunId,
            ScheduleId = ScheduleId,
            ScheduleRunId = ScheduleRunId,
            ScheduleEntryId = ScheduleEntryId,
            SlotId = SlotId,
            RouteWorkerSessionId = RouteWorkerSessionId,
            AccountKey = AccountKey,
            CharacterKey = CharacterKey,
            CharacterContentId = CharacterContentId,
            RouletteId = RouletteId,
            RouletteKey = RouletteKey,
            RequestedAtUtc = RequestedAtUtc,
        };
}

public sealed class DadRouletteRewardProbeResultDto
{
    public string OperationId { get; set; } = string.Empty;
    public DadRouletteRewardProbeOperation Operation { get; set; }
    public string SchedulerRunId { get; set; } = string.Empty;
    public string ScheduleId { get; set; } = string.Empty;
    public string ScheduleRunId { get; set; } = string.Empty;
    public string ScheduleEntryId { get; set; } = string.Empty;
    public string SlotId { get; set; } = string.Empty;
    public DadWorkerSessionId RouteWorkerSessionId { get; set; } = new(string.Empty);
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong CharacterContentId { get; set; }
    public uint RouletteId { get; set; }
    public string RouletteKey { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; }
    public DateTime RespondedAtUtc { get; set; } = DateTime.UtcNow;
    public DadRouletteRewardProbeOutcome Outcome { get; set; }
    public int ReceivedRewardCount { get; set; }
    public int MaxRewardCount { get; set; }
    public bool DutyFinderOpenedByDad { get; set; }
    public string Summary { get; set; } = string.Empty;

    public DadRouletteRewardProbeResultDto Clone()
        => (DadRouletteRewardProbeResultDto)MemberwiseClone();

    public static DadRouletteRewardProbeResultDto FromRequest(
        DadRouletteRewardProbeRequestDto request,
        DadRouletteRewardProbeOutcome outcome,
        string summary,
        DateTime respondedAtUtc,
        int receivedRewardCount = 0,
        int maxRewardCount = 0,
        bool dutyFinderOpenedByDad = false)
        => new()
        {
            OperationId = request.OperationId,
            Operation = request.Operation,
            SchedulerRunId = request.SchedulerRunId,
            ScheduleId = request.ScheduleId,
            ScheduleRunId = request.ScheduleRunId,
            ScheduleEntryId = request.ScheduleEntryId,
            SlotId = request.SlotId,
            RouteWorkerSessionId = request.RouteWorkerSessionId,
            AccountKey = request.AccountKey,
            CharacterKey = request.CharacterKey,
            CharacterContentId = request.CharacterContentId,
            RouletteId = request.RouletteId,
            RouletteKey = request.RouletteKey,
            RequestedAtUtc = request.RequestedAtUtc,
            RespondedAtUtc = respondedAtUtc,
            Outcome = outcome,
            ReceivedRewardCount = receivedRewardCount,
            MaxRewardCount = maxRewardCount,
            DutyFinderOpenedByDad = dutyFinderOpenedByDad,
            Summary = summary,
        };
}

public static class DadRouletteRewardProbeIdentityRules
{
    public static readonly TimeSpan MaximumResponseAge = TimeSpan.FromSeconds(10);

    public static bool IsValid(DadRouletteRewardProbeRequestDto? request)
        => request != null &&
           Enum.IsDefined(request.Operation) &&
           !string.IsNullOrWhiteSpace(request.OperationId) &&
           !string.IsNullOrWhiteSpace(request.SchedulerRunId) &&
           !string.IsNullOrWhiteSpace(request.ScheduleId) &&
           !string.IsNullOrWhiteSpace(request.ScheduleRunId) &&
           !string.IsNullOrWhiteSpace(request.ScheduleEntryId) &&
           !string.IsNullOrWhiteSpace(request.SlotId) &&
           !request.RouteWorkerSessionId.IsEmpty &&
           !request.AccountKey.IsEmpty &&
           !request.CharacterKey.IsEmpty &&
           request.CharacterContentId > 0 &&
           request.RouletteId is > 0 and <= byte.MaxValue &&
           !string.IsNullOrWhiteSpace(request.RouletteKey) &&
           request.RequestedAtUtc != default;

    public static bool Matches(
        DadRouletteRewardProbeRequestDto? request,
        DadRouletteRewardProbeResultDto? result)
        => request != null && result != null &&
           Same(request.OperationId, result.OperationId) &&
           request.Operation == result.Operation &&
           Same(request.SchedulerRunId, result.SchedulerRunId) &&
           Same(request.ScheduleId, result.ScheduleId) &&
           Same(request.ScheduleRunId, result.ScheduleRunId) &&
           Same(request.ScheduleEntryId, result.ScheduleEntryId) &&
           Same(request.SlotId, result.SlotId) &&
           Same(request.RouteWorkerSessionId.Value, result.RouteWorkerSessionId.Value) &&
           Same(request.AccountKey.Value, result.AccountKey.Value) &&
           Same(request.CharacterKey.Value, result.CharacterKey.Value) &&
           request.CharacterContentId == result.CharacterContentId &&
           request.RouletteId == result.RouletteId &&
           Same(request.RouletteKey, result.RouletteKey) &&
           request.RequestedAtUtc == result.RequestedAtUtc;

    public static bool TryValidateResponse(
        DadRouletteRewardProbeRequestDto request,
        DadRouletteRewardProbeResultDto? result,
        DateTime nowUtc,
        out string reason)
    {
        reason = string.Empty;
        if (!IsValid(request))
        {
            reason = "The roulette reward probe request identity is incomplete.";
            return false;
        }
        if (!Matches(request, result))
        {
            reason = "The roulette reward probe response did not echo the exact request identity.";
            return false;
        }

        if (!Enum.IsDefined(result!.Outcome))
        {
            reason = "The roulette reward probe response returned an unknown outcome.";
            return false;
        }

        if (result.RespondedAtUtc < request.RequestedAtUtc ||
            result.RespondedAtUtc > nowUtc ||
            nowUtc - result.RespondedAtUtc > MaximumResponseAge)
        {
            reason = "The roulette reward probe response is stale or has an invalid timestamp.";
            return false;
        }

        if (result.Outcome == DadRouletteRewardProbeOutcome.Pending)
            return true;
        if (result.Outcome == DadRouletteRewardProbeOutcome.Unknown)
            return true;
        if (result.MaxRewardCount <= 0 ||
            result.ReceivedRewardCount < 0 ||
            result.ReceivedRewardCount > result.MaxRewardCount)
        {
            reason = "The roulette reward probe returned invalid received/max reward counts.";
            return false;
        }
        if (result.Outcome == DadRouletteRewardProbeOutcome.Received &&
            result.ReceivedRewardCount != result.MaxRewardCount)
        {
            reason = "The roulette reward probe contradicted its Received outcome.";
            return false;
        }
        if (result.Outcome == DadRouletteRewardProbeOutcome.NotReceived &&
            result.ReceivedRewardCount >= result.MaxRewardCount)
        {
            reason = "The roulette reward probe contradicted its NotReceived outcome.";
            return false;
        }

        return true;
    }

    private static bool Same(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}

public readonly record struct DadRouletteRewardObservation(
    ulong CharacterContentId,
    uint RouletteId,
    string SelectionFingerprint,
    bool ExactRouletteSelected,
    int ReceivedRewardCount,
    int MaxRewardCount,
    DateTime CapturedAtUtc);

public enum DadRouletteRewardObservationStatus
{
    Waiting = 0,
    Received = 1,
    NotReceived = 2,
    Invalid = 3,
}

public sealed class DadRouletteRewardObservationGate
{
    public static readonly TimeSpan MinimumStableInterval = TimeSpan.FromMilliseconds(250);
    private DadRouletteRewardObservation? previous;

    public DadRouletteRewardObservationStatus Observe(
        DadRouletteRewardObservation observation,
        ulong expectedCharacterContentId,
        uint expectedRouletteId,
        out string reason)
    {
        reason = string.Empty;
        if (observation.CharacterContentId == 0 ||
            observation.CharacterContentId != expectedCharacterContentId ||
            observation.RouletteId == 0 ||
            observation.RouletteId != expectedRouletteId ||
            string.IsNullOrWhiteSpace(observation.SelectionFingerprint) ||
            !observation.ExactRouletteSelected ||
            observation.MaxRewardCount <= 0 ||
            observation.ReceivedRewardCount < 0 ||
            observation.ReceivedRewardCount > observation.MaxRewardCount ||
            observation.CapturedAtUtc == default)
        {
            previous = null;
            reason = "Duty Finder roulette identity or received/max reward counts are invalid.";
            return DadRouletteRewardObservationStatus.Invalid;
        }

        if (!previous.HasValue || !Same(previous.Value, observation))
        {
            previous = observation;
            reason = "Waiting for a second stable exact roulette reward observation.";
            return DadRouletteRewardObservationStatus.Waiting;
        }
        if (observation.CapturedAtUtc - previous.Value.CapturedAtUtc < MinimumStableInterval)
        {
            reason = "Waiting for a second stable exact roulette reward observation.";
            return DadRouletteRewardObservationStatus.Waiting;
        }

        return observation.ReceivedRewardCount == observation.MaxRewardCount
            ? DadRouletteRewardObservationStatus.Received
            : DadRouletteRewardObservationStatus.NotReceived;
    }

    public void Reset() => previous = null;

    private static bool Same(
        DadRouletteRewardObservation left,
        DadRouletteRewardObservation right)
        => left.CharacterContentId == right.CharacterContentId &&
           left.RouletteId == right.RouletteId &&
           string.Equals(left.SelectionFingerprint, right.SelectionFingerprint, StringComparison.Ordinal) &&
           left.ExactRouletteSelected == right.ExactRouletteSelected &&
           left.ReceivedRewardCount == right.ReceivedRewardCount &&
           left.MaxRewardCount == right.MaxRewardCount &&
           right.CapturedAtUtc >= left.CapturedAtUtc;
}

internal readonly record struct DadDirectRouletteRewardObservation(
    ulong CharacterContentId,
    uint RouletteId,
    bool IsComplete,
    DateTime CapturedAtUtc);

internal sealed class DadDirectRouletteRewardObservationGate
{
    public static readonly TimeSpan MinimumStableInterval = TimeSpan.FromMilliseconds(250);
    private DadDirectRouletteRewardObservation? previous;

    public DadRouletteRewardObservationStatus Observe(
        DadDirectRouletteRewardObservation observation,
        ulong expectedCharacterContentId,
        uint expectedRouletteId,
        out string reason)
    {
        if (observation.CharacterContentId == 0 ||
            observation.CharacterContentId != expectedCharacterContentId ||
            observation.RouletteId == 0 ||
            observation.RouletteId != expectedRouletteId ||
            observation.CapturedAtUtc == default)
        {
            previous = null;
            reason = "Direct roulette reward identity is invalid.";
            return DadRouletteRewardObservationStatus.Invalid;
        }

        if (!previous.HasValue || !Same(previous.Value, observation))
        {
            previous = observation;
            reason = "Waiting for a second stable direct native roulette reward read.";
            return DadRouletteRewardObservationStatus.Waiting;
        }

        if (observation.CapturedAtUtc - previous.Value.CapturedAtUtc < MinimumStableInterval)
        {
            reason = "Waiting for a second stable direct native roulette reward read.";
            return DadRouletteRewardObservationStatus.Waiting;
        }

        reason = observation.IsComplete
            ? "Two stable direct native reads report the frozen roulette reward received."
            : "Two stable direct native reads report the frozen roulette reward not received.";
        return observation.IsComplete
            ? DadRouletteRewardObservationStatus.Received
            : DadRouletteRewardObservationStatus.NotReceived;
    }

    private static bool Same(
        DadDirectRouletteRewardObservation left,
        DadDirectRouletteRewardObservation right)
        => left.CharacterContentId == right.CharacterContentId &&
           left.RouletteId == right.RouletteId &&
           left.IsComplete == right.IsComplete &&
           right.CapturedAtUtc >= left.CapturedAtUtc;
}

public static class DadRouletteRewardProbeUiOwnershipRules
{
    public static bool CanNavigate(bool dutyFinderWasAlreadyOpen) => !dutyFinderWasAlreadyOpen;

    public static bool ShouldClose(bool dutyFinderOpenedByDad) => dutyFinderOpenedByDad;
}

public enum DadDailyRewardPreflightDisposition
{
    Bypass = 0,
    Wait = 1,
    ContinueToNextCheckedSlot = 2,
    SkipEntry = 3,
    RunNormally = 4,
}

public static class DadDailyRewardPreflightRules
{
    public static bool IsEligible(
        DadPlannerActivityMode activityMode,
        DadScheduleCadence scheduleCadence,
        string? scheduleId,
        string? scheduleRunId,
        string? scheduleEntryId,
        DadQueueTarget? target,
        int checkedSlotCount)
        => activityMode == DadPlannerActivityMode.DailyRoulette &&
           scheduleCadence == DadScheduleCadence.DailyReset &&
           !string.IsNullOrWhiteSpace(scheduleId) &&
           !string.IsNullOrWhiteSpace(scheduleRunId) &&
           !string.IsNullOrWhiteSpace(scheduleEntryId) &&
           target is { Kind: DadQueueTargetKind.Roulette, RouletteId: > 0 and <= byte.MaxValue } &&
           !string.IsNullOrWhiteSpace(target.Key) &&
           checkedSlotCount > 0;

    public static DadDailyRewardPreflightDisposition Resolve(
        int checkedSlotCount,
        int receivedSlotCount,
        bool routeAvailable,
        bool timedOut,
        DadRouletteRewardProbeOutcome? latestOutcome)
    {
        if (checkedSlotCount <= 0)
            return DadDailyRewardPreflightDisposition.Bypass;
        if (receivedSlotCount < 0 || receivedSlotCount > checkedSlotCount)
            return DadDailyRewardPreflightDisposition.RunNormally;
        if (!routeAvailable || timedOut)
            return DadDailyRewardPreflightDisposition.RunNormally;
        if (!latestOutcome.HasValue || latestOutcome == DadRouletteRewardProbeOutcome.Pending)
            return DadDailyRewardPreflightDisposition.Wait;
        if (latestOutcome != DadRouletteRewardProbeOutcome.Received)
            return DadDailyRewardPreflightDisposition.RunNormally;
        if (receivedSlotCount == checkedSlotCount)
            return DadDailyRewardPreflightDisposition.SkipEntry;
        return DadDailyRewardPreflightDisposition.ContinueToNextCheckedSlot;
    }
}
