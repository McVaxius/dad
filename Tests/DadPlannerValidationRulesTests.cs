using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadPlannerValidationRulesTests
{
    [Fact]
    public void OnlyHardRuntimeReadinessModuleBlockersAreTransient()
    {
        var runtimeOnly = new DadModuleExecutionStatusDto
        {
            CanStart = false,
            BlockedReason = "Currently queued for Ocean Fishing.",
            Blockers =
            [
                new DadModuleBlockerDto
                {
                    Capability = "RuntimeReadiness",
                    Severity = DadModuleBlockerSeverity.Blocked,
                    Summary = "Currently queued for Ocean Fishing.",
                },
                new DadModuleBlockerDto
                {
                    Capability = "RuntimeReadiness",
                    Severity = DadModuleBlockerSeverity.Failed,
                    Summary = "Duty transition is still active.",
                },
            ],
        };

        Assert.True(DadPlannerValidationRules.IsTransientRuntimeReadinessFailure(runtimeOnly));
        var transientDecision = DadPlannerValidationRules.EvaluateModuleRuntimeStatus(
            currentCanSchedule: true,
            runtimeOnly);
        Assert.False(transientDecision.CanStart);
        Assert.True(transientDecision.CanSchedule);
        Assert.True(transientDecision.IsTransientRuntimeReadiness);
        Assert.Equal("Currently queued for Ocean Fishing.", transientDecision.Reason);
        Assert.False(DadPlannerValidationRules.EvaluateModuleRuntimeStatus(
            currentCanSchedule: false,
            runtimeOnly).CanSchedule);

        runtimeOnly.Blockers.Add(new DadModuleBlockerDto
        {
            Capability = "Participants",
            Severity = DadModuleBlockerSeverity.Blocked,
            Summary = "Static participant mismatch.",
        });
        Assert.False(DadPlannerValidationRules.IsTransientRuntimeReadinessFailure(runtimeOnly));
        Assert.False(DadPlannerValidationRules.EvaluateModuleRuntimeStatus(
            currentCanSchedule: true,
            runtimeOnly).CanSchedule);
        Assert.False(DadPlannerValidationRules.IsTransientRuntimeReadinessFailure(new DadModuleExecutionStatusDto
        {
            CanStart = false,
        }));
        Assert.False(DadPlannerValidationRules.IsTransientRuntimeReadinessFailure(new DadModuleExecutionStatusDto
        {
            CanStart = false,
            Blockers =
            [
                new DadModuleBlockerDto
                {
                    Capability = "RuntimeReadiness",
                    Severity = DadModuleBlockerSeverity.Deferred,
                    Summary = "Informational wait.",
                },
            ],
        }));
    }

    [Fact]
    public void ReadinessOnlyBlockerAllowsSchedulingButNotDirectStart()
    {
        var details = DadPlannerValidationRules.Evaluate(
            [],
            ["Character is offline."],
            []);

        Assert.False(details.CanStart);
        Assert.True(details.CanSchedule);
        Assert.Contains("Scheduler can resolve", details.ReadinessSummary);
    }

    [Fact]
    public void StaticOrSchedulerBlockerPreventsScheduling()
    {
        var staticBlocked = DadPlannerValidationRules.Evaluate(
            ["Duty is missing."],
            ["Character is offline."],
            []);
        var policyBlocked = DadPlannerValidationRules.Evaluate(
            [],
            ["Character is offline."],
            ["Already online cannot wake it."]);

        Assert.False(staticBlocked.CanSchedule);
        Assert.False(policyBlocked.CanSchedule);
    }

    [Fact]
    public void StrictSchedulerGateRequiresFreshCanStartPreview()
    {
        var wakeablePreview = new DadPlannerRunRequestPreview
        {
            CanSchedule = true,
            CanStart = false,
            BlockedReason = "Still offline.",
            Request = new DadRunRequest { RequestId = "preview" },
        };
        var strictPreview = new DadPlannerRunRequestPreview
        {
            CanSchedule = true,
            CanStart = true,
            Request = new DadRunRequest { RequestId = "strict" },
        };

        Assert.False(DadPlannerValidationRules.CanStartStrictScheduledRun(true, wakeablePreview, out var blocked));
        Assert.Contains("offline", blocked, StringComparison.OrdinalIgnoreCase);
        Assert.True(DadPlannerValidationRules.CanStartStrictScheduledRun(true, strictPreview, out var ready));
        Assert.Empty(ready);
    }

    [Fact]
    public void StrictRuntimeOnlyFailureWaitsWhileStaticFailureTerminates()
    {
        var transient = new DadPlannerRunRequestPreview
        {
            CanStart = false,
            CanSchedule = true,
            Request = new DadRunRequest { RequestId = "transient" },
            ReadinessBlockers = ["Heartbeat still has the old character."],
            BlockedReason = "Heartbeat still has the old character.",
        };
        var terminal = new DadPlannerRunRequestPreview
        {
            CanStart = false,
            CanSchedule = false,
            Request = new DadRunRequest { RequestId = "terminal" },
            StaticBlockers = ["Duty module is unavailable."],
            BlockedReason = "Duty module is unavailable.",
        };

        Assert.Equal(
            DadStrictPlannerRevalidationDisposition.WaitForRuntimeReadiness,
            DadPlannerValidationRules.EvaluateStrictScheduledRun(true, transient).Disposition);
        Assert.Equal(
            DadStrictPlannerRevalidationDisposition.TerminalRejection,
            DadPlannerValidationRules.EvaluateStrictScheduledRun(true, terminal).Disposition);
        Assert.True(DadPlannerValidationRules.IsStrictRuntimeOnlyFailure(true, false, true));
        Assert.False(DadPlannerValidationRules.IsStrictRuntimeOnlyFailure(true, false, false));
    }

    [Fact]
    public void StrictRevalidationBudgetStartsWhenTheLastSlotBecameReady()
    {
        var schedulerStarted = new DateTime(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);
        var firstReady = schedulerStarted.AddMinutes(9);
        var lastReady = schedulerStarted.AddMinutes(12);
        var slots = new List<DadSchedulerSlotState>
        {
            new() { SlotId = "slot-1", Ready = true, ReadyUtc = firstReady },
            new() { SlotId = "slot-2", Ready = true, ReadyUtc = lastReady },
        };

        var budgetStart = DadPlannerValidationRules.ResolveStrictReadinessBudgetStartUtc(slots, schedulerStarted);

        Assert.Equal(lastReady, budgetStart);
        Assert.False(DadWakePolicyRules.IsParticipantReadyTimedOut(budgetStart, lastReady.AddSeconds(299), 300));
        Assert.True(DadWakePolicyRules.IsParticipantReadyTimedOut(budgetStart, lastReady.AddSeconds(300), 300));
    }

    [Fact]
    public void RefreshedRuntimeTruthCanClaimPlannerStartExactlyOnce()
    {
        var tracker = new DadStrictPlannerRevalidationTracker();
        var waiting = new DadPlannerRunRequestPreview
        {
            CanSchedule = true,
            Request = new DadRunRequest { RequestId = "waiting" },
            ReadinessBlockers = ["Peer heartbeat is catching up."],
        };
        var ready = new DadPlannerRunRequestPreview
        {
            CanStart = true,
            CanSchedule = true,
            Request = new DadRunRequest { RequestId = "ready" },
        };

        Assert.Equal(
            DadStrictPlannerRevalidationDisposition.WaitForRuntimeReadiness,
            DadPlannerValidationRules.EvaluateStrictScheduledRun(true, waiting).Disposition);
        Assert.True(tracker.TryRecordDiagnostic(DadStrictPlannerRevalidationDisposition.WaitForRuntimeReadiness));
        Assert.False(tracker.TryRecordDiagnostic(DadStrictPlannerRevalidationDisposition.WaitForRuntimeReadiness));
        Assert.Equal(
            DadStrictPlannerRevalidationDisposition.ReadyToStart,
            DadPlannerValidationRules.EvaluateStrictScheduledRun(true, ready).Disposition);
        Assert.Equal(
            DadStrictPlannerRevalidationDisposition.WaitForRuntimeReadiness,
            DadPlannerValidationRules.EvaluateStrictScheduledRun(false, ready).Disposition);
        Assert.True(tracker.TryClaimStart());
        Assert.False(tracker.TryClaimStart());
    }

    [Fact]
    public void ReadyTimestampRestartsAfterReadinessIsLostAndRecovered()
    {
        var firstReady = new DateTime(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);
        var recovered = firstReady.AddMinutes(20);
        var previous = new List<DadSchedulerSlotState>
        {
            new() { SlotId = "slot-1", Ready = false, ReadyUtc = null },
        };
        var current = new List<DadSchedulerSlotState>
        {
            new() { SlotId = "slot-1", Ready = true, ReadyUtc = firstReady },
        };

        DadPlannerValidationRules.StampReadyTransitions(current, previous, recovered);

        Assert.Equal(recovered, current[0].ReadyUtc);
    }
}
