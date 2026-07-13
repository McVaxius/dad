using dad.Models;

namespace dad.Services;

internal static class DadPlannerWarningRules
{
    public static List<string> Build(DadRunRequest request, DadCharacterPool pool)
    {
        var warnings = new List<string>();
        if (pool.Characters.Count == 0)
            warnings.Add("Dad character pool is empty at plan time.");

        if (request.Orchestration.LocalOnlyOverride)
            warnings.Add("Local-only mode ignores connected Dad workers until changed.");

        if (request.Dungeon?.QueueViaLanParty == true)
            warnings.Add("Premade dungeon routing stays inside Dad's internal premade lane.");

        if (request.Dungeon is { QueueViaLanParty: false })
            warnings.Add("Local Duty routes through Dad-owned guarded regular Duty Finder queue execution.");

        if (request.DailyMsq != null)
            warnings.Add("Daily Roulette uses Dad's guarded synced four-player queue lane; native Duty Finder eligibility checks still apply at queue time.");

        if (request.PremadeDuty != null)
            warnings.Add("Premade Duty requires Dad Coordinator authority and exact typed party workers.");

        if (request.Mogtome != null)
            warnings.Add("MOGTOME uses the solo DAD-owned helper IPC lane; the helper owns its party coordination.");

        if (request.Msq != null)
            warnings.Add("MSQ solo progression uses selected duty with Trust then Duty Support fallback.");

        if (request.DutySupport != null || request.Trust != null)
            warnings.Add("Duty Support and Trust route through Dad-owned guarded native local NPC duty lanes.");

        if (request.CustomDuty != null)
            warnings.Add("Custom Duty uses typed CFC selection and routes by configured party size.");

        if (request.Squadron != null)
            warnings.Add("Squadron is Dad-owned planning with guarded live callbacks deferred until in-game validation.");

        if (request.VariantVvd != null)
            warnings.Add("Variant/VVD is Dad-owned planning; live queue start is guarded/deferred until callback validation and ADS solving coverage are ready.");

        if (request.Blunderville != null)
            warnings.Add("Blunderville remains Dad-owned but blocks until guarded Gold Saucer callbacks are available.");

        if (request.Commendation != null || request.Astrope != null)
            warnings.Add("Commendation and Astrope remain Dad-owned; AuraFarmer is not required.");

        return warnings;
    }
}
