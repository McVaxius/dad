namespace dad;

// One timeline per DAD process. Production always uses the system provider; the
// separately compiled headless executable installs its controlled provider.
internal static class DadClock
{
    internal static TimeProvider Provider { get; set; } = TimeProvider.System;
    internal static Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;
    public static DateTime UtcNow => Provider.GetUtcNow().UtcDateTime;
    public static DateTimeOffset OffsetUtcNow => Provider.GetUtcNow();
}
