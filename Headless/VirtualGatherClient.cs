using dad.Models;
using dad.Services;

namespace dad.Headless;

// External GBR observations/actions only. All planning, assignment, transport,
// completion and cancellation decisions remain in the linked production code.
internal sealed class VirtualGatherClient(Func<DadParticipantSnapshot> identity, Action<string> emit) : IDadGatherClientService
{
    private readonly Action<string> record = emit;
    public Dictionary<uint, long> Counts { get; set; } = new() { [42] = 0 };
    public bool Ready { get; set; } = true;
    public bool Waiting { get; set; }
    public bool Running { get; set; }
    public bool StopFails { get; set; }
    private string owner = string.Empty;
    private DadGatherWorkerOrder[] orders = [];

    public object Snapshot() => new { running = Running, waiting = Waiting, ownedRunId = owner, counts = Counts, orders };

    public DadGatherStockReply Probe(DadGatherStockQuery query)
    {
        record($"gather:probe:{query.RunId}:{query.SlotId}");
        var current = identity();
        return new()
        {
            RequestId = query.RequestId, RunId = query.RunId, SlotId = query.SlotId,
            WorkerSessionId = current.WorkerSessionId, AccountKey = current.ManagedAccountKey,
            CharacterKey = current.ActiveCharacterKey, ObservedAtUtc = DadClock.UtcNow,
            Available = Ready && !Running,
            Error = !Ready ? "Synthetic GBR is unavailable." : Running ? "GBR is already running independently." : "",
            CarriedCounts = query.ItemIds.ToDictionary(item => item, item => Counts.GetValueOrDefault(item)),
            EligibleItems = query.ItemIds.Where(item => item == 42).ToHashSet(),
        };
    }

    public bool TryCreateSession(out IDadGatherClientSession? session, out string error)
    {
        error = Ready ? "" : "Synthetic GBR is unavailable.";
        session = Ready ? new Session(this) : null;
        return Ready;
    }

    private sealed class Session(VirtualGatherClient host) : IDadGatherClientSession
    {
        private string runId = string.Empty;
        public bool HasOwnership => !string.IsNullOrEmpty(runId) && host.owner == runId;
        public bool TryReadCarriedCount(uint itemId, out long count, out string error)
        {
            count = host.Counts.GetValueOrDefault(itemId);
            error = host.Ready ? "" : "Synthetic carried inventory is unavailable.";
            return host.Ready;
        }
        public bool TryReadActivity(string expectedRunId, out bool enabled, out bool waiting, out string error)
        {
            enabled = host.Running; waiting = host.Waiting;
            error = HasOwnership && expectedRunId == runId ? "" : "Synthetic native list ownership changed.";
            return string.IsNullOrEmpty(error);
        }
        public bool TryStart(string expectedRunId, IReadOnlyList<DadGatherWorkerOrder> assignedOrders, out string error)
        {
            error = host.Running || !string.IsNullOrEmpty(host.owner) ? "Synthetic GBR is already running." : "";
            if (!string.IsNullOrEmpty(error)) return false;
            runId = host.owner = expectedRunId;
            host.orders = assignedOrders.Where(order => order.Quantity > 0).ToArray();
            host.Running = true;
            host.record($"gather:start:{runId}");
            return true;
        }
        public bool TryStop(string expectedRunId, out string error)
        {
            host.record($"gather:stop-attempt:{expectedRunId}");
            error = expectedRunId != runId || !HasOwnership ? "Synthetic stop has the wrong run identity." :
                host.StopFails ? "Synthetic owned cleanup failed." : "";
            if (!string.IsNullOrEmpty(error)) return false;
            host.record($"gather:stop:{runId}");
            host.Running = false;
            host.owner = string.Empty;
            return true;
        }
    }
}
