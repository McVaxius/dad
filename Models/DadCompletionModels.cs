namespace dad.Models;

// Feature batch A (dadfeatures20260620b): operator-chosen actions to run when a Dad run completes.
public enum DadCompletionKillMode
{
    None = 0,
    CloseGameClient = 1, // dangerous — only honored when AdvancedModeEnabled (/dad advanced)
    ShutDownPc = 2,      // dangerous — only honored when AdvancedModeEnabled; uses a cancelable delay
}

public sealed class DadCompletionActions
{
    // Play a game sound effect (se.1..se.16) when a run completes.
    public bool PlaySound { get; set; } = false;
    public int SoundEffectId { get; set; } = 1;

    // Run operator-supplied slash commands after completion (e.g. resume another tool).
    public bool RunCommands { get; set; } = false;
    public List<string> Commands { get; set; } = [];

    // Dangerous shutdown actions — gated behind AdvancedModeEnabled.
    public DadCompletionKillMode KillMode { get; set; } = DadCompletionKillMode.None;

    public DadCompletionActions Clone()
        => new()
        {
            PlaySound = PlaySound,
            SoundEffectId = SoundEffectId,
            RunCommands = RunCommands,
            Commands = [..Commands],
            KillMode = KillMode,
        };
}
