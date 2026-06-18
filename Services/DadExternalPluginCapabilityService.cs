namespace dad.Services;

public sealed class DadExternalPluginCapabilityService
{
    public bool LanPartyQueueTransportAvailable => false;

    public string DescribeDadLanPartyModule()
        => "Dad internal premade lane owns premade duty and Daily MSQ orchestration. Guarded live queue execution is still deferred inside Dad.";

    public string DescribeDadAuraFarmerModule()
        => "Dad owns commendation and Astrope orchestration directly. AuraFarmer is not required.";

    public string DescribeLanPartyQueueTransport()
        => DescribeDadLanPartyModule();

    public string DescribeAuraFarmerTransport()
        => DescribeDadAuraFarmerModule();
}
