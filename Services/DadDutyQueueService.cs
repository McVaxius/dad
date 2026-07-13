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

        return task.Unsynced
            ? "Dad-owned unrestricted/unsynced regular Duty Finder execution is enabled for the Local Duty lane."
            : "Dad-owned synced regular Duty Finder execution is enabled for the Local Duty lane.";
    }

    public string DescribeCommendationExecutionDeferral()
        => capabilityService.DescribeAuraFarmerTransport();

    public string DescribeAstropeExecutionDeferral()
        => capabilityService.DescribeAuraFarmerTransport();
}
