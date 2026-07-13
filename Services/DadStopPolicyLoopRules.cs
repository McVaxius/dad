using dad.Models;

namespace dad.Services;

public static class DadStopPolicyLoopRules
{
    public static bool IsEligibleModule(DadModuleId moduleId)
        => moduleId is DadModuleId.Duty
            or DadModuleId.Msq
            or DadModuleId.DutySupport
            or DadModuleId.Trust
            or DadModuleId.PremadeDuty
            or DadModuleId.DailyMsq
            or DadModuleId.Mogtome
            or DadModuleId.Commendation
            or DadModuleId.CustomDuty;
}
