namespace dad.Models;

public enum DadCharacterSource
{
    LocalRuntime,
    PeerRuntime,
    XadbOnly,
    ManualUnresolved,
}

public sealed class DadRosterCharacter
{
    public string AccountKey { get; set; } = string.Empty;
    public DateTime? LastSnapshotUtc { get; set; }
    public DateTime? LastRuntimeSeenUtc { get; set; }
    public Dictionary<uint, int> JobLevels { get; set; } = [];
    public uint? CurrentJobId { get; set; }
    public string CurrentJobAbbrev { get; set; } = string.Empty;
    public int? CurrentLevel { get; set; }
    public string SnapshotQuality { get; set; } = string.Empty;
    public int? SnapshotVersion { get; set; }
    public bool XadbReady { get; set; }
    public bool IsCurrent { get; set; }
    public DadCharacterSource Source { get; set; } = DadCharacterSource.XadbOnly;
}

public sealed class DadAcquiredCharacter
{
    public Dictionary<uint, int> JobLevels { get; set; } = [];
    public uint? CurrentJobId { get; set; }
    public int? CurrentLevel { get; set; }
}
