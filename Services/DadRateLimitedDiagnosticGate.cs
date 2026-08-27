namespace dad.Services;

internal sealed class DadRateLimitedDiagnosticGate
{
    private readonly object gate = new();
    private readonly Dictionary<string, DateTime> nextAllowedUtc = new(StringComparer.Ordinal);

    internal bool ShouldEmit(string safeCode, DateTime nowUtc, TimeSpan interval)
    {
        lock (gate)
        {
            if (nextAllowedUtc.TryGetValue(safeCode, out var next) && nowUtc < next)
                return false;
            nextAllowedUtc[safeCode] = nowUtc + interval;
            return true;
        }
    }
}
