using System.Collections.Generic;

namespace dad.Models;

public sealed class AccountConfig
{
    public int SchemaVersion { get; set; } = 2;
    public long Revision { get; set; } = 1;
    public string AccountId { get; set; } = string.Empty;
    public string AccountAlias { get; set; } = "Account 1";
    public string PrimaryLaunchProfileId { get; set; } = string.Empty;
    public CharacterConfig DefaultConfig { get; set; } = new();
    public Dictionary<string, CharacterConfig> Characters { get; set; } = new();
}
