namespace dad.Models;

public enum DadParticipantQueueAction
{
    ObserveQueueAndAreaTruth,
    AcceptCommence,
    OpenDutyFinder,
    SelectDuty,
    RegisterDuty,
    AlterSyncSettings,
}

public static class DadParticipantQueueFollowThroughRules
{
    public static bool IsObserveAcceptOnlyLane(DadRunPlan plan, DadPlannedModuleExecution module)
        => plan.RequiredParticipantCount > 1 &&
           module.ModuleId != DadModuleId.Mogtome &&
           (module.ModuleId == DadModuleId.PremadeDuty ||
            module.ModuleId == DadModuleId.DailyMsq && plan.Request.DailyMsq != null ||
            module.ModuleId == DadModuleId.Duty && plan.Request.Dungeon?.QueueViaLanParty == true ||
            module.ModuleId == DadModuleId.CustomDuty && plan.Request.CustomDuty?.ExpectedPartySize > 1 ||
            module.ModuleId == DadModuleId.Commendation && plan.Request.Commendation != null);

    public static bool IsAllowed(DadParticipantQueueAction action)
        => action is DadParticipantQueueAction.ObserveQueueAndAreaTruth or DadParticipantQueueAction.AcceptCommence;
}
