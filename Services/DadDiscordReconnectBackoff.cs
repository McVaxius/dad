namespace dad.Services;

public sealed class DadDiscordReconnectBackoff
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];
    private int attempt;

    public TimeSpan NextDelay() => Delays[Math.Min(attempt++, Delays.Length - 1)];
    public void Reset() => attempt = 0;
}
