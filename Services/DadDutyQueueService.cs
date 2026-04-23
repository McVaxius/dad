using dad.Models;

namespace dad.Services;

public sealed class DadDutyQueueService
{
    private readonly DadExternalPluginCapabilityService capabilityService;

    public DadDutyQueueService(DadExternalPluginCapabilityService capabilityService)
    {
        this.capabilityService = capabilityService;
    }

    public string DescribeDungeonExecutionDeferral(DadDungeonTask task)
    {
        if (task.QueueViaLanParty)
            return capabilityService.DescribeLanPartyQueueTransport();

        if (task.Unsynced)
            return "Unsynced live duty execution is deferred until Dad has a guarded queue executor.";

        return "Direct Duty Finder execution is deferred until Dad's guarded queue executor is enabled.";
    }

    public string DescribeDailyMsqExecutionDeferral()
        => capabilityService.DescribeLanPartyQueueTransport();

    public string DescribeCommendationExecutionDeferral()
        => capabilityService.DescribeAuraFarmerTransport();

    public string DescribeAstropeExecutionDeferral()
        => capabilityService.DescribeAuraFarmerTransport();
}
