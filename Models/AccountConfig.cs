using System.Collections.Generic;

namespace dad.Models;

public sealed class AccountConfig
{
    public string AccountId { get; set; } = string.Empty;
    public string AccountAlias { get; set; } = "Account 1";
    public CharacterConfig DefaultConfig { get; set; } = new();
    public Dictionary<string, CharacterConfig> Characters { get; set; } = new();
}
