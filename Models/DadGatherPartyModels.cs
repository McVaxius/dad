namespace dad.Models;

public sealed class DadGatherPartyTask
{
    public string Name { get; set; } = "Gathering list";
    public List<DadGatherItemTarget> Items { get; set; } = [];
    public List<DadGatherAllocation> Allocations { get; set; } = [];

    public DadGatherPartyTask Clone() => new()
    {
        Name = Name,
        Items = Items.Select(item => item.Clone()).ToList(),
        Allocations = Allocations.Select(allocation => allocation.Clone()).ToList(),
    };
}

public sealed class DadGatherItemTarget
{
    public Guid RowId { get; set; } = Guid.NewGuid();
    public uint ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int TargetQuantity { get; set; } = 1;
    public uint PreferredLocationId { get; set; }
    public bool Enabled { get; set; } = true;

    public DadGatherItemTarget Clone() => (DadGatherItemTarget)MemberwiseClone();
}

public sealed class DadGatherAllocation
{
    // Preset slot identity survives a worker reconnect; the dispatched order also freezes its session.
    public string SlotId { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public int Quantity { get; set; }

    public DadGatherAllocation Clone() => (DadGatherAllocation)MemberwiseClone();
}

public sealed record DadGatherWorkerStock(
    string SlotId,
    string WorkerSessionId,
    string AccountKey,
    string CharacterKey,
    bool Ready,
    bool InventoryAvailable,
    IReadOnlyDictionary<uint, long> CarriedCounts,
    IReadOnlySet<uint> EligibleItems);

public sealed record DadGatherWorkerOrder(
    string SlotId,
    string WorkerSessionId,
    string CharacterKey,
    uint ItemId,
    long StartingCount,
    int Quantity,
    long NativeTarget,
    uint PreferredLocationId,
    string AccountKey = "");

public sealed record DadGatherItemPreview(
    Guid RowId,
    uint ItemId,
    long Target,
    long OnHand,
    long Remaining,
    IReadOnlyList<DadGatherWorkerOrder> Orders);

public sealed record DadGatherPartyPreview(
    IReadOnlyList<DadGatherItemPreview> Items,
    IReadOnlyList<string> Blockers)
{
    public bool CanStart => Blockers.Count == 0;
    public bool Fulfilled => CanStart && Items.All(item => item.Remaining == 0);
}

public sealed record DadGatherImportIssue(int Line, string Message);

public sealed record DadGatherImportResult(DadGatherPartyTask Task, IReadOnlyList<DadGatherImportIssue> Issues)
{
    public bool Succeeded => Issues.Count == 0;
}

public sealed class DadGatherStockQuery
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = string.Empty;
    public string SlotId { get; set; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public List<uint> ItemIds { get; set; } = [];
}

public sealed class DadGatherStockReply
{
    public int SchemaVersion { get; set; } = 4;
    public string RequestId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string SlotId { get; set; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public DateTime ObservedAtUtc { get; set; }
    public bool Available { get; set; }
    public string Error { get; set; } = string.Empty;
    public Dictionary<uint, long> CarriedCounts { get; set; } = [];
    public HashSet<uint> EligibleItems { get; set; } = [];

    public static DadGatherStockReply Reject(DadGatherStockQuery query, string error) => new()
    {
        RequestId = query.RequestId, RunId = query.RunId, SlotId = query.SlotId,
        WorkerSessionId = query.WorkerSessionId, AccountKey = query.AccountKey,
        CharacterKey = query.CharacterKey, ObservedAtUtc = DateTime.UtcNow, Error = error,
    };
}
