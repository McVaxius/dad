using dad.Models;

namespace dad.Services;

// B2: pure, Dalamud-free effective-plan rewrites extracted from DadQueueExecutionService so the
// Attempts -> StopPolicy.AfterRuns mapping is unit-testable. The instance executor resolution stays
// in DadQueueExecutionService; only the request/plan shaping (which depends solely on dad.Models)
// lives here.
internal static class DadEffectivePlanFactory
{
    // B2: map a task-level attempt count onto the coordinator stop-policy loop instead of the
    // executor's one-run-per-request field. Leaves explicit Target* stop modes untouched.
    public static DadRunStopPolicy BuildAttemptStopPolicy(DadRunStopPolicy? source, int attempts)
    {
        var policy = (source ?? new DadRunStopPolicy()).Clone();
        if (policy.Mode == DadPlannerStopMode.AfterRuns)
            policy.AfterRuns = Math.Max(policy.AfterRuns, Math.Max(1, attempts));
        return policy.Normalize();
    }

    public static (DadRunPlan Plan, DadPlannedModuleExecution Module) BuildCustomDutyPlan(
        DadRunPlan plan,
        DadPlannedModuleExecution module)
    {
        var task = plan.Request.CustomDuty!;
        var premade = Math.Max(1, task.ExpectedPartySize) > 1;
        var effectiveModule = new DadPlannedModuleExecution
        {
            ModuleId = premade ? DadModuleId.PremadeDuty : DadModuleId.Duty,
            DisplayName = task.DutyName,
            OwnerLabel = module.OwnerLabel,
            ExpectedPartySize = Math.Max(1, task.ExpectedPartySize),
            RequiresPeers = premade,
            Summary = module.Summary,
        };
        var request = new DadRunRequest
        {
            RequestId = plan.Request.RequestId,
            RequestedAtUtc = plan.Request.RequestedAtUtc,
            RequestedBy = plan.Request.RequestedBy,
            StopPolicy = BuildAttemptStopPolicy(plan.Request.StopPolicy, task.Attempts),
            PreDutyRepairPolicy = (plan.Request.PreDutyRepairPolicy ?? new DadPreDutyRepairPolicy()).Clone(),
            CompletionActions = plan.Request.CompletionActions?.Clone(),
            Orchestration = plan.Request.Orchestration,
        };
        if (premade)
        {
            request.PremadeDuty = new DadPremadeDutyTask
            {
                ContentFinderConditionId = task.ContentFinderConditionId,
                DutyName = task.DutyName,
                ExpectedPartySize = task.ExpectedPartySize,
                Unsynced = task.Unsynced,
                Attempts = 1, // B2: repeats come from StopPolicy.AfterRuns, not the one-run executor field
            };
        }
        else
        {
            request.Dungeon = new DadDungeonTask
            {
                ContentFinderConditionId = task.ContentFinderConditionId,
                SelectedDungeon = task.DutyName,
                Count = 1, // B2: repeats come from StopPolicy.AfterRuns, not the one-run executor field
                Unsynced = task.Unsynced,
                QueueViaLanParty = false,
            };
        }

        return (new DadRunPlan
        {
            Request = request,
            CompositeModuleId = effectiveModule.ModuleId,
            Orchestration = plan.Orchestration,
            Summary = plan.Summary,
            RequiredParticipantCount = plan.RequiredParticipantCount,
            RequiresRemoteParticipants = plan.RequiresRemoteParticipants,
            LeaderCharacterKey = plan.LeaderCharacterKey,
            InviterCharacterKey = plan.InviterCharacterKey,
            Modules = [effectiveModule],
            PlannerWarnings = [..plan.PlannerWarnings],
        }, effectiveModule);
    }

    public static (DadRunPlan Plan, DadPlannedModuleExecution Module) BuildCommendationPlan(
        DadRunPlan plan,
        DadPlannedModuleExecution module)
    {
        var task = plan.Request.Commendation!;
        var effectiveModule = new DadPlannedModuleExecution
        {
            ModuleId = DadModuleId.PremadeDuty,
            DisplayName = task.DutyName,
            OwnerLabel = module.OwnerLabel,
            ExpectedPartySize = 4,
            RequiresPeers = true,
            Summary = module.Summary,
        };
        var request = new DadRunRequest
        {
            RequestId = plan.Request.RequestId,
            RequestedAtUtc = plan.Request.RequestedAtUtc,
            RequestedBy = plan.Request.RequestedBy,
            StopPolicy = BuildAttemptStopPolicy(plan.Request.StopPolicy, task.Attempts),
            PreDutyRepairPolicy = (plan.Request.PreDutyRepairPolicy ?? new DadPreDutyRepairPolicy()).Clone(),
            CompletionActions = plan.Request.CompletionActions?.Clone(),
            Orchestration = plan.Request.Orchestration,
            PremadeDuty = new DadPremadeDutyTask
            {
                ContentFinderConditionId = task.ContentFinderConditionId,
                DutyName = string.IsNullOrWhiteSpace(task.DutyName) ? "Under the Armour" : task.DutyName,
                ExpectedPartySize = 4,
                Attempts = 1, // B2: repeats come from StopPolicy.AfterRuns, not the one-run executor field
            },
        };
        return (new DadRunPlan
        {
            Request = request,
            CompositeModuleId = DadModuleId.PremadeDuty,
            Orchestration = plan.Orchestration,
            Summary = plan.Summary,
            RequiredParticipantCount = plan.RequiredParticipantCount,
            RequiresRemoteParticipants = true,
            LeaderCharacterKey = plan.LeaderCharacterKey,
            InviterCharacterKey = plan.InviterCharacterKey,
            Modules = [effectiveModule],
            PlannerWarnings = [..plan.PlannerWarnings],
        }, effectiveModule);
    }
}
