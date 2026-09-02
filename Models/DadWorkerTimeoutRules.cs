namespace dad.Models;

internal static class DadWorkerTimeoutRules
{
    internal static TimeSpan? ResolveTimeout(int timeoutSeconds)
        => timeoutSeconds <= 0
            ? null
            : TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 30, 7200));

    internal static bool HasTimedOut(
        int timeoutSeconds,
        DadModuleId moduleId,
        bool enteredDuty,
        TimeSpan elapsed)
    {
        if (enteredDuty && moduleId is DadModuleId.Duty
                or DadModuleId.Msq
                or DadModuleId.DutySupport
                or DadModuleId.Trust
                or DadModuleId.PremadeDuty
                or DadModuleId.DailyMsq
                or DadModuleId.Mogtome
                or DadModuleId.Commendation
                or DadModuleId.CustomDuty)
        {
            return false;
        }

        var timeout = ResolveTimeout(timeoutSeconds);
        return timeout.HasValue && elapsed >= timeout.Value;
    }
}
