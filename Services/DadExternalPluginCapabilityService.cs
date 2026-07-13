namespace dad.Services;

public sealed class DadExternalPluginCapabilityService
{
    public bool LanPartyQueueTransportAvailable => false;

    public string DescribeDadLanPartyModule()
        => "Dad owns guarded live premade-duty and four-player Daily Roulette queue execution inside the Dad Coordinator.";

    public string DescribeDadAuraFarmerModule()
        => "Dad owns commendation and Astrope orchestration directly. AuraFarmer is not required.";

    public string DescribeLanPartyQueueTransport()
        => DescribeDadLanPartyModule();

    public string DescribeAuraFarmerTransport()
        => DescribeDadAuraFarmerModule();
}
