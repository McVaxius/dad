using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

// B2: a task-level Attempts count must drive the coordinator stop-policy loop (StopPolicy.AfterRuns),
// not the executor's one-run-per-request field (PremadeDuty.Attempts / Dungeon.Count). These tests pin
// that the effective-plan rewrite maps Attempts -> AfterRuns and resets the executor run-count to 1, and
// that the executors still block when their run-count field is > 1.
public sealed class DadAttemptStopPolicyTests
{
    [Fact]
    public void CustomDutyPremadeRoutesAttemptsIntoStopPolicyAndKeepsExecutorRunCountAtOne()
    {
        var plan = new DadRunPlan
        {
            Request = new DadRunRequest
            {
                CustomDuty = new DadCustomDutyTask
                {
                    ContentFinderConditionId = 1000,
                    DutyName = "Test Premade Duty",
                    ExpectedPartySize = 4,
                    Attempts = 5,
                },
                StopPolicy = new DadRunStopPolicy { Mode = DadPlannerStopMode.AfterRuns, AfterRuns = 1 },
            },
        };

        var (effectivePlan, effectiveModule) = DadEffectivePlanFactory.BuildCustomDutyPlan(
            plan,
            new DadPlannedModuleExecution { ModuleId = DadModuleId.CustomDuty });

        Assert.Equal(DadModuleId.PremadeDuty, effectiveModule.ModuleId);
        Assert.Equal(1, effectivePlan.Request.PremadeDuty!.Attempts);          // (a) executor run-count is 1, not N
        Assert.True(effectivePlan.Request.StopPolicy.AfterRuns >= 5);          // (a) stop policy AfterRuns >= N
        Assert.Equal(5, effectivePlan.Request.StopPolicy.AfterRuns);
        Assert.False(ExecutorBlocksRepeatRun(effectivePlan));                  // rewritten plan no longer trips the guard
    }

    [Fact]
    public void CustomDutyLocalRoutesAttemptsIntoStopPolicyAndKeepsDungeonCountAtOne()
    {
        var plan = new DadRunPlan
        {
            Request = new DadRunRequest
            {
                CustomDuty = new DadCustomDutyTask
                {
                    ContentFinderConditionId = 2000,
                    DutyName = "Test Local Duty",
                    ExpectedPartySize = 1,
                    Attempts = 4,
                },
            },
        };

        var (effectivePlan, effectiveModule) = DadEffectivePlanFactory.BuildCustomDutyPlan(
            plan,
            new DadPlannedModuleExecution { ModuleId = DadModuleId.CustomDuty });

        Assert.Equal(DadModuleId.Duty, effectiveModule.ModuleId);
        Assert.Equal(1, effectivePlan.Request.Dungeon!.Count);                 // (a) executor run-count is 1, not N
        Assert.Equal(4, effectivePlan.Request.StopPolicy.AfterRuns);          // (a) stop policy AfterRuns >= N
        Assert.False(ExecutorBlocksRepeatRun(effectivePlan));
    }

    [Fact]
    public void CommendationRoutesAttemptsIntoStopPolicyAndKeepsExecutorRunCountAtOne()
    {
        var plan = new DadRunPlan
        {
            Request = new DadRunRequest
            {
                Commendation = new DadCommendationTask
                {
                    ContentFinderConditionId = 3000,
                    DutyName = "Under the Armour",
                    Attempts = 3,
                },
                StopPolicy = new DadRunStopPolicy { Mode = DadPlannerStopMode.AfterRuns, AfterRuns = 1 },
            },
        };

        var (effectivePlan, effectiveModule) = DadEffectivePlanFactory.BuildCommendationPlan(
            plan,
            new DadPlannedModuleExecution { ModuleId = DadModuleId.Commendation });

        Assert.Equal(DadModuleId.PremadeDuty, effectiveModule.ModuleId);
        Assert.Equal(1, effectivePlan.Request.PremadeDuty!.Attempts);          // (a) executor run-count is 1, not N
        Assert.True(effectivePlan.Request.StopPolicy.AfterRuns >= 3);          // (a) stop policy AfterRuns >= N
        Assert.Equal(3, effectivePlan.Request.StopPolicy.AfterRuns);
        Assert.False(ExecutorBlocksRepeatRun(effectivePlan));
    }

    [Fact]
    public void ExecutorsStillBlockWhenRunCountFieldExceedsOne()
    {
        // (b) The one-run guard is intentional; a plan whose executor run-count field is > 1 must still block.
        var blockedPremade = new DadRunPlan
        {
            Request = new DadRunRequest { PremadeDuty = new DadPremadeDutyTask { Attempts = 3 } },
        };
        var blockedLocal = new DadRunPlan
        {
            Request = new DadRunRequest { Dungeon = new DadDungeonTask { Count = 3 } },
        };

        Assert.True(ExecutorBlocksRepeatRun(blockedPremade));
        Assert.True(ExecutorBlocksRepeatRun(blockedLocal));
    }

    [Fact]
    public void BuildAttemptStopPolicyKeepsTheLargerAfterRunsAndDefaultsNullSource()
    {
        Assert.Equal(4, DadEffectivePlanFactory.BuildAttemptStopPolicy(null, 4).AfterRuns);
        Assert.Equal(
            10,
            DadEffectivePlanFactory.BuildAttemptStopPolicy(
                new DadRunStopPolicy { Mode = DadPlannerStopMode.AfterRuns, AfterRuns = 10 }, 3).AfterRuns);
    }

    [Fact]
    public void BuildAttemptStopPolicyLeavesTargetStopModesUntouched()
    {
        var source = new DadRunStopPolicy
        {
            Mode = DadPlannerStopMode.TargetLevel,
            TargetLevel = 90,
            AfterRuns = 1,
        };

        var result = DadEffectivePlanFactory.BuildAttemptStopPolicy(source, 7);

        Assert.Equal(DadPlannerStopMode.TargetLevel, result.Mode);
        Assert.Equal(90, result.TargetLevel);
        Assert.Equal(1, result.AfterRuns); // not bumped: AfterRuns only governs the AfterRuns stop mode
    }

    // Mirrors the executors' intentional one-run guard: DadPremadeDutyExecutor (PremadeDuty.Attempts > 1 ||
    // Dungeon.Count > 1) and DadLocalDutyExecutor (Dungeon.Count > 1). Replicated locally because those
    // executors carry Dalamud dependencies and cannot be linked into the test assembly.
    private static bool ExecutorBlocksRepeatRun(DadRunPlan plan)
        => plan.Request.PremadeDuty?.Attempts > 1 || plan.Request.Dungeon?.Count > 1;
}
