namespace dad.Services;

public sealed class DadExternalPluginCapabilityService
{
    public bool LanPartyQueueTransportAvailable => false;
    public bool AuraFarmerTransportAvailable => false;

    public string DescribeDadLanPartyModule()
        => "Dad internal premade lane owns premade duty and Daily MSQ orchestration. Guarded live queue execution is still deferred inside Dad.";

    public string DescribeDadAuraFarmerModule()
        => "Dad internal aura lane owns commendation and Astrope orchestration. Guarded live execution is still deferred inside Dad.";

    public string DescribeLanPartyQueueTransport()
        => DescribeDadLanPartyModule();

    public string DescribeAuraFarmerTransport()
        => DescribeDadAuraFarmerModule();
}
