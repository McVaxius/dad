using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRouletteRewardProbeRulesTests
{
    [Fact]
    public void ExactFreshIdentityAndReceivedCountsAreAccepted()
    {
        var request = Request();
        var now = request.RequestedAtUtc.AddSeconds(2);
        var result = DadRouletteRewardProbeResultDto.FromRequest(
            request,
            DadRouletteRewardProbeOutcome.Received,
            "received",
            now.AddSeconds(-1),
            receivedRewardCount: 1,
            maxRewardCount: 1,
            dutyFinderOpenedByDad: true);

        Assert.True(DadRouletteRewardProbeIdentityRules.IsValid(request));
        Assert.True(DadRouletteRewardProbeIdentityRules.TryValidateResponse(request, result, now, out var reason), reason);
        Assert.True(result.DutyFinderOpenedByDad);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("schedule")]
    [InlineData("slot")]
    [InlineData("route")]
    [InlineData("character")]
    [InlineData("roulette")]
    public void AnyEchoedIdentityMismatchIsRejected(string mismatch)
    {
        var request = Request();
        var now = request.RequestedAtUtc.AddSeconds(1);
        var result = DadRouletteRewardProbeResultDto.FromRequest(
            request,
            DadRouletteRewardProbeOutcome.NotReceived,
            "not received",
            now,
            0,
            1);
        switch (mismatch)
        {
            case "operation": result.OperationId = "other"; break;
            case "schedule": result.ScheduleRunId = "other"; break;
            case "slot": result.SlotId = "Slot9"; break;
            case "route": result.RouteWorkerSessionId = new DadWorkerSessionId("worker-b"); break;
            case "character": result.CharacterContentId++; break;
            case "roulette": result.RouletteId++; break;
        }

        Assert.False(DadRouletteRewardProbeIdentityRules.TryValidateResponse(request, result, now, out var reason));
        Assert.Contains("identity", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StaleAndContradictoryResponsesAreRejected()
    {
        var request = Request();
        var stale = DadRouletteRewardProbeResultDto.FromRequest(
            request,
            DadRouletteRewardProbeOutcome.Received,
            "stale",
            request.RequestedAtUtc.AddSeconds(1),
            1,
            1);
        Assert.False(DadRouletteRewardProbeIdentityRules.TryValidateResponse(
            request,
            stale,
            request.RequestedAtUtc.AddSeconds(20),
            out var staleReason));
        Assert.Contains("stale", staleReason, StringComparison.OrdinalIgnoreCase);

        var contradictory = DadRouletteRewardProbeResultDto.FromRequest(
            request,
            DadRouletteRewardProbeOutcome.Received,
            "contradiction",
            request.RequestedAtUtc.AddSeconds(1),
            0,
            1);
        Assert.False(DadRouletteRewardProbeIdentityRules.TryValidateResponse(
            request,
            contradictory,
            request.RequestedAtUtc.AddSeconds(2),
            out var contradictionReason));
        Assert.Contains("contradicted", contradictionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IncompleteOrUndefinedTypedIdentityIsRejected()
    {
        var missingKey = Request();
        missingKey.RouletteKey = string.Empty;
        Assert.False(DadRouletteRewardProbeIdentityRules.IsValid(missingKey));

        var invalidOperation = Request();
        invalidOperation.Operation = (DadRouletteRewardProbeOperation)999;
        Assert.False(DadRouletteRewardProbeIdentityRules.IsValid(invalidOperation));

        var request = Request();
        var invalidOutcome = DadRouletteRewardProbeResultDto.FromRequest(
            request,
            (DadRouletteRewardProbeOutcome)999,
            "invalid",
            request.RequestedAtUtc.AddSeconds(1),
            0,
            1);
        Assert.False(DadRouletteRewardProbeIdentityRules.TryValidateResponse(
            request,
            invalidOutcome,
            request.RequestedAtUtc.AddSeconds(2),
            out var reason));
        Assert.Contains("unknown outcome", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RewardTruthRequiresTwoStableExactObservations()
    {
        var gate = new DadRouletteRewardObservationGate();
        var at = new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc);
        var first = new DadRouletteRewardObservation(123, 3, "exact:G1", true, 1, 1, at);

        Assert.Equal(
            DadRouletteRewardObservationStatus.Waiting,
            gate.Observe(first, 123, 3, out _));
        Assert.Equal(
            DadRouletteRewardObservationStatus.Waiting,
            gate.Observe(first with { CapturedAtUtc = at.AddMilliseconds(100) }, 123, 3, out _));
        Assert.Equal(
            DadRouletteRewardObservationStatus.Received,
            gate.Observe(first with { CapturedAtUtc = at.AddMilliseconds(300) }, 123, 3, out _));
    }

    [Fact]
    public void ChangedCountsResetStabilityAndExactMismatchIsInvalid()
    {
        var gate = new DadRouletteRewardObservationGate();
        var at = DateTime.UtcNow;
        Assert.Equal(
            DadRouletteRewardObservationStatus.Waiting,
            gate.Observe(new DadRouletteRewardObservation(123, 3, "exact:G1", true, 1, 1, at), 123, 3, out _));
        Assert.Equal(
            DadRouletteRewardObservationStatus.Waiting,
            gate.Observe(new DadRouletteRewardObservation(123, 3, "exact:G1", true, 0, 1, at.AddSeconds(1)), 123, 3, out _));
        Assert.Equal(
            DadRouletteRewardObservationStatus.NotReceived,
            gate.Observe(new DadRouletteRewardObservation(123, 3, "exact:G1", true, 0, 1, at.AddSeconds(2)), 123, 3, out _));
        Assert.Equal(
            DadRouletteRewardObservationStatus.Invalid,
            gate.Observe(new DadRouletteRewardObservation(123, 8, "wrong", true, 1, 1, at.AddSeconds(3)), 123, 3, out var reason));
        Assert.Contains("identity", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DutyFinderOwnershipForbidsNavigationAndCloseOfPreexistingUi()
    {
        Assert.False(DadRouletteRewardProbeUiOwnershipRules.CanNavigate(dutyFinderWasAlreadyOpen: true));
        Assert.True(DadRouletteRewardProbeUiOwnershipRules.CanNavigate(dutyFinderWasAlreadyOpen: false));
        Assert.False(DadRouletteRewardProbeUiOwnershipRules.ShouldClose(dutyFinderOpenedByDad: false));
        Assert.True(DadRouletteRewardProbeUiOwnershipRules.ShouldClose(dutyFinderOpenedByDad: true));
    }

    [Fact]
    public void PreflightEligibilityIsDailyResetDailyRouletteWithCheckedRowsOnly()
    {
        var target = new DadQueueTarget
        {
            Kind = DadQueueTargetKind.Roulette,
            RouletteId = 3,
            Key = "ContentRoulette:3",
        };
        Assert.True(DadDailyRewardPreflightRules.IsEligible(
            DadPlannerActivityMode.DailyRoulette,
            DadScheduleCadence.DailyReset,
            "schedule",
            "run",
            "entry",
            target,
            1));
        Assert.False(DadDailyRewardPreflightRules.IsEligible(
            DadPlannerActivityMode.DailyRoulette,
            DadScheduleCadence.Manual,
            "schedule",
            "run",
            "entry",
            target,
            1));
        Assert.False(DadDailyRewardPreflightRules.IsEligible(
            DadPlannerActivityMode.DailyRoulette,
            DadScheduleCadence.DailyReset,
            "schedule",
            "run",
            "entry",
            target,
            0));
        Assert.False(DadDailyRewardPreflightRules.IsEligible(
            DadPlannerActivityMode.PremadeDuty,
            DadScheduleCadence.DailyReset,
            "schedule",
            "run",
            "entry",
            target,
            1));

        target.Key = string.Empty;
        Assert.False(DadDailyRewardPreflightRules.IsEligible(
            DadPlannerActivityMode.DailyRoulette,
            DadScheduleCadence.DailyReset,
            "schedule",
            "run",
            "entry",
            target,
            1));
    }

    [Theory]
    [InlineData(false, false, DadRouletteRewardProbeOutcome.Pending, DadDailyRewardPreflightDisposition.RunNormally)]
    [InlineData(true, true, DadRouletteRewardProbeOutcome.Pending, DadDailyRewardPreflightDisposition.RunNormally)]
    [InlineData(true, false, DadRouletteRewardProbeOutcome.Unknown, DadDailyRewardPreflightDisposition.RunNormally)]
    [InlineData(true, false, DadRouletteRewardProbeOutcome.NotReceived, DadDailyRewardPreflightDisposition.RunNormally)]
    [InlineData(true, false, DadRouletteRewardProbeOutcome.Pending, DadDailyRewardPreflightDisposition.Wait)]
    public void UncertaintyAndNotReceivedAlwaysRunNormally(
        bool routeAvailable,
        bool timedOut,
        DadRouletteRewardProbeOutcome outcome,
        DadDailyRewardPreflightDisposition expected)
        => Assert.Equal(expected, DadDailyRewardPreflightRules.Resolve(2, 0, routeAvailable, timedOut, outcome));

    [Fact]
    public void OnlyUnanimousReceivedSkips()
    {
        Assert.Equal(
            DadDailyRewardPreflightDisposition.ContinueToNextCheckedSlot,
            DadDailyRewardPreflightRules.Resolve(2, 1, true, false, DadRouletteRewardProbeOutcome.Received));
        Assert.Equal(
            DadDailyRewardPreflightDisposition.SkipEntry,
            DadDailyRewardPreflightRules.Resolve(2, 2, true, false, DadRouletteRewardProbeOutcome.Received));
        Assert.Equal(
            DadDailyRewardPreflightDisposition.Bypass,
            DadDailyRewardPreflightRules.Resolve(0, 0, true, false, null));
        Assert.Equal(
            DadDailyRewardPreflightDisposition.RunNormally,
            DadDailyRewardPreflightRules.Resolve(2, 3, true, false, DadRouletteRewardProbeOutcome.Received));
    }

    [Fact]
    public void TypedProbeAndSchedulerSkipMetadataRoundTrip()
    {
        var request = Request();
        var restored = DadIpcJson.Deserialize<DadRouletteRewardProbeRequestDto>(DadIpcJson.Serialize(request));
        Assert.NotNull(restored);
        var echoed = DadRouletteRewardProbeResultDto.FromRequest(
            restored,
            DadRouletteRewardProbeOutcome.Pending,
            "pending",
            restored.RequestedAtUtc.AddSeconds(1));
        Assert.True(DadRouletteRewardProbeIdentityRules.Matches(request, echoed));

        var job = new DadScheduledCrewJob
        {
            ScheduleCadence = DadScheduleCadence.DailyReset,
        };
        Assert.Equal(DadScheduleCadence.DailyReset, job.Clone().ScheduleCadence);
        var result = new DadScheduledCrewJobResult
        {
            ScheduleCadence = DadScheduleCadence.DailyReset,
            SkipKind = DadSchedulerSkipKind.DailyRouletteReward,
        };
        var resultClone = result.Clone();
        Assert.Equal(DadScheduleCadence.DailyReset, resultClone.ScheduleCadence);
        Assert.Equal(DadSchedulerSkipKind.DailyRouletteReward, resultClone.SkipKind);
    }

    private static DadRouletteRewardProbeRequestDto Request()
        => new()
        {
            OperationId = "probe-a",
            Operation = DadRouletteRewardProbeOperation.Inspect,
            SchedulerRunId = "scheduler-a",
            ScheduleId = "schedule-a",
            ScheduleRunId = "schedule-run-a",
            ScheduleEntryId = "entry-a",
            SlotId = "Slot1",
            RouteWorkerSessionId = new DadWorkerSessionId("worker-a"),
            AccountKey = new DadAccountKey("account-a"),
            CharacterKey = new DadCharacterKey("Character@World"),
            CharacterContentId = 123,
            RouletteId = 3,
            RouletteKey = "ContentRoulette:3",
            RequestedAtUtc = new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc),
        };
}
