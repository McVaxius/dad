namespace dad.Models;

// Feature batch A (dadfeatures20260620b): operator-chosen actions to run when a Dad run completes.
public enum DadCompletionKillMode
{
    None = 0,
    CloseGameClient = 1, // legacy setting preserved but disabled
    ShutDownPc = 2,      // legacy setting preserved but disabled
}

public sealed class DadCompletionActions
{
    // Play a game sound effect (se.1..se.16) when a run completes.
    public bool PlaySound { get; set; } = false;
    public int SoundEffectId { get; set; } = 1;

    // Run operator-supplied slash commands after completion (e.g. resume another tool).
    public bool RunCommands { get; set; } = false;
    public List<string> Commands { get; set; } = [];

    // Legacy kill actions are kept for config compatibility but disabled at runtime.
    public DadCompletionKillMode KillMode { get; set; } = DadCompletionKillMode.None;
    public DadPostRunUtilities Utilities { get; set; } = new();

    public DadCompletionActions Clone()
        => new()
        {
            PlaySound = PlaySound,
            SoundEffectId = SoundEffectId,
            RunCommands = RunCommands,
            Commands = Commands == null ? [] : [..Commands],
            KillMode = KillMode,
            Utilities = (Utilities ?? new DadPostRunUtilities()).Clone(),
        };
}

public static class DadCompletionActionSnapshots
{
    public static DadCompletionActions Resolve(DadCompletionActions? snapshot, DadCompletionActions? fallback)
        => (snapshot ?? fallback ?? new DadCompletionActions()).Clone();

    public static string DescribeSource(DadCompletionActions? snapshot)
        => snapshot == null ? "Global defaults" : "Preset override";
}

public sealed class DadPostRunUtilities
{
    public bool OpenGearCoffers { get; set; }
    public bool RegisterTripleTriadCards { get; set; }
    public bool SellTripleTriadCards { get; set; }
    public bool GrandCompanyHandInViaAutoRetainer { get; set; }
    public string GrandCompanyHandInCommand { get; set; } = "/ays gc";

    public DadPostRunUtilities Clone()
        => new()
        {
            OpenGearCoffers = OpenGearCoffers,
            RegisterTripleTriadCards = RegisterTripleTriadCards,
            SellTripleTriadCards = SellTripleTriadCards,
            GrandCompanyHandInViaAutoRetainer = GrandCompanyHandInViaAutoRetainer,
            GrandCompanyHandInCommand = GrandCompanyHandInCommand,
        };
}
