namespace dad.Models;

public sealed class CharacterConfig
{
    public bool Enabled { get; set; } = true;
    public bool AllowIpcStarts { get; set; } = true;
    public string TargetNotes { get; set; } = string.Empty;

    public CharacterConfig Clone() => (CharacterConfig)MemberwiseClone();
}
