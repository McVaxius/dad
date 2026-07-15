using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadPreDutyRepairRuntimeService
{
    private static readonly TimeSpan DurabilityPollInterval = TimeSpan.FromSeconds(1);
    private readonly DadDutySupportAdsService adsService;
    private readonly IPluginLog log;
    private DadPreDutyRepairGate gate = new();
    private DadPreDutyRepairPolicy policy = new();
    private DadRunRequest? request;
    private DadModuleId moduleId;
    private DateTime nextPollUtc = DateTime.MinValue;
    private DadPreDutyRepairDecision lastDecision = new(
        DadPreDutyRepairAction.Ready,
        "Pre-duty repair is not active.");

    public DadPreDutyRepairRuntimeService(DadDutySupportAdsService adsService, IPluginLog log)
    {
        this.adsService = adsService;
        this.log = log;
    }

    public bool IsRequired => DadPreDutyRepairRules.IsRequired(policy, moduleId, request);

    public void Begin(DadRunRequest runRequest, DadModuleId module, DateTime nowUtc)
    {
        gate = new DadPreDutyRepairGate();
        request = runRequest;
        policy = (runRequest.PreDutyRepairPolicy ?? new DadPreDutyRepairPolicy()).Clone().Normalize();
        moduleId = module;
        nextPollUtc = DateTime.MinValue;
        lastDecision = !DadPreDutyRepairRules.IsRequired(policy, moduleId, request)
            ? new DadPreDutyRepairDecision(
                DadPreDutyRepairAction.Ready,
                "Pre-duty repair is disabled or does not apply to this module.")
            : new DadPreDutyRepairDecision(
                DadPreDutyRepairAction.Wait,
                "Preparing pre-duty durability proof.");
    }

    public DadPreDutyRepairDecision Update(DateTime nowUtc)
    {
        if (!IsRequired)
            return lastDecision;
        if (nowUtc < nextPollUtc)
            return lastDecision;

        nextPollUtc = nowUtc + DurabilityPollInterval;
        var durability = DadDutySupportAdsService.ReadEquippedDurability();
        var ads = durability.Readable && durability.MinimumConditionPercent < policy.ThresholdPercent
            ? adsService.InspectRepair()
            : DadAdsRepairObservation.Absent("ADS repair truth was not needed for this durability observation.");
        var decision = gate.Evaluate(policy, moduleId, request, durability, ads, nowUtc);
        if (decision.Action == DadPreDutyRepairAction.InvokeAds)
        {
            var invocation = adsService.StartRepair(decision.AdsMode);
            gate.RecordInvocationResult(invocation, nowUtc);
            log.Information(
                "[dad][ADS] Pre-duty repair invocation module={ModuleId} mode={Mode} outcome={Outcome} attempt={Attempt}: {Summary}",
                moduleId,
                decision.AdsMode,
                invocation.Outcome,
                gate.InvocationCount,
                invocation.Summary);
            decision = gate.Evaluate(policy, moduleId, request, durability, ads, nowUtc);
        }

        lastDecision = decision;
        return decision;
    }

    public void Reset()
    {
        gate = new DadPreDutyRepairGate();
        policy = new DadPreDutyRepairPolicy();
        request = null;
        moduleId = DadModuleId.None;
        nextPollUtc = DateTime.MinValue;
        lastDecision = new DadPreDutyRepairDecision(
            DadPreDutyRepairAction.Ready,
            "Pre-duty repair is not active.");
    }
}
