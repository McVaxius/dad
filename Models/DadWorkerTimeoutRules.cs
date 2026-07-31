namespace dad.Models;

internal static class DadWorkerTimeoutRules
{
    internal static TimeSpan? ResolveTimeout(int timeoutSeconds)
        => timeoutSeconds <= 0
            ? null
            : TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 30, 7200));
}
