using dad.Models;

namespace dad.Services;

public sealed class DadQueueExecutionService
{
    private readonly DadLocalDutyExecutor localDutyExecutor;
    private readonly DadPremadeDutyExecutor premadeDutyExecutor;
    private readonly DadMsqExecutor msqExecutor;
    private readonly DadDutySupportExecutor dutySupportExecutor;
    private readonly DadTrustExecutor trustExecutor;
    private readonly DadDailyMsqExecutor dailyMsqExecutor;
    private readonly DadBlundervilleExecutor blundervilleExecutor;
    private readonly DadMogtomeExecutor mogtomeExecutor;
    private readonly DadCommendationExecutor commendationExecutor;
    private readonly DadAstropeExecutor astropeExecutor;
    private readonly DadCustomDutyExecutor customDutyExecutor;
    private IDadModuleExecutor? activeExecutor;

    public DadQueueExecutionService(
        DadModuleRegistry moduleRegistry,
        DadDutyQueueService dutyQueueService,
        DadExternalPluginCapabilityService externalPluginCapabilityService)
    {
        localDutyExecutor = new DadLocalDutyExecutor(
            moduleRegistry,
            plan => plan.Request.Dungeon == null
                ? moduleRegistry.GetCapability(DadModuleId.Duty).Notes
                : dutyQueueService.DescribeDungeonExecutionDeferral(plan.Request.Dungeon));
        premadeDutyExecutor = new DadPremadeDutyExecutor(
            moduleRegistry,
            _ => externalPluginCapabilityService.DescribeDadLanPartyModule());
        msqExecutor = new DadMsqExecutor(
            moduleRegistry,
            _ => moduleRegistry.GetCapability(DadModuleId.Msq).Blockers
                .FirstOrDefault(blocker => blocker.Capability == "CanStartQueue")?.Summary
                 ?? moduleRegistry.GetCapability(DadModuleId.Msq).Notes);
        dutySupportExecutor = new DadDutySupportExecutor(
            moduleRegistry,
            _ => moduleRegistry.GetCapability(DadModuleId.DutySupport).Blockers
                .FirstOrDefault(blocker => blocker.Capability == "CanStartQueue")?.Summary
                 ?? moduleRegistry.GetCapability(DadModuleId.DutySupport).Notes);
        trustExecutor = new DadTrustExecutor(
            moduleRegistry,
            _ => moduleRegistry.GetCapability(DadModuleId.Trust).Blockers
                .FirstOrDefault(blocker => blocker.Capability == "CanStartQueue")?.Summary
                 ?? moduleRegistry.GetCapability(DadModuleId.Trust).Notes);
        dailyMsqExecutor = new DadDailyMsqExecutor(
            moduleRegistry,
            _ => dutyQueueService.DescribeDailyMsqExecutionDeferral());
        blundervilleExecutor = new DadBlundervilleExecutor(
            moduleRegistry,
            _ => moduleRegistry.GetCapability(DadModuleId.Blunderville).Blockers
                .FirstOrDefault(blocker => blocker.Capability == "CanStartQueue")?.Summary
                 ?? moduleRegistry.GetCapability(DadModuleId.Blunderville).Notes);
        mogtomeExecutor = new DadMogtomeExecutor(
            moduleRegistry,
            _ => moduleRegistry.GetCapability(DadModuleId.Mogtome).Blockers
                .FirstOrDefault(blocker => blocker.Capability == "CanStartQueue")?.Summary
                 ?? moduleRegistry.GetCapability(DadModuleId.Mogtome).Notes);
        commendationExecutor = new DadCommendationExecutor(
            moduleRegistry,
            _ => dutyQueueService.DescribeCommendationExecutionDeferral());
        astropeExecutor = new DadAstropeExecutor(
            moduleRegistry,
            _ => dutyQueueService.DescribeAstropeExecutionDeferral());
        customDutyExecutor = new DadCustomDutyExecutor(
            moduleRegistry,
            _ => moduleRegistry.GetCapability(DadModuleId.CustomDuty).Blockers
                .FirstOrDefault(blocker => blocker.Capability == "CanStartQueue")?.Summary
                 ?? moduleRegistry.GetCapability(DadModuleId.CustomDuty).Notes);
    }

    public DadRunStepResultDto ExecuteModule(DadRunPlan plan, DadPlannedModuleExecution module, IReadOnlyList<DadParticipantSnapshot> participants)
    {
        activeExecutor = ResolveExecutor(plan, module);
        return activeExecutor.Start(plan, participants);
    }

    public DadRunStepResultDto UpdateActiveExecutor()
        => activeExecutor?.Update() ?? new DadRunStepResultDto();

    public DadRunStepResultDto CancelActiveExecutor(string reason)
        => activeExecutor?.Cancel(reason) ?? new DadRunStepResultDto();

    public DadModuleExecutionStatusDto GetActiveExecutorStatus()
        => activeExecutor?.GetStatus() ?? new DadModuleExecutionStatusDto();

    private IDadModuleExecutor ResolveExecutor(DadRunPlan plan, DadPlannedModuleExecution module)
        => module.ModuleId switch
        {
            DadModuleId.Duty when plan.Request.Dungeon?.QueueViaLanParty == true => premadeDutyExecutor,
            DadModuleId.Duty => localDutyExecutor,
            DadModuleId.Msq => msqExecutor,
            DadModuleId.DutySupport => dutySupportExecutor,
            DadModuleId.Trust => trustExecutor,
            DadModuleId.PremadeDuty => premadeDutyExecutor,
            DadModuleId.DailyMsq => dailyMsqExecutor,
            DadModuleId.Blunderville => blundervilleExecutor,
            DadModuleId.Mogtome => mogtomeExecutor,
            DadModuleId.Commendation => commendationExecutor,
            DadModuleId.Astrope => astropeExecutor,
            DadModuleId.CustomDuty => customDutyExecutor,
            _ => localDutyExecutor,
        };
}
