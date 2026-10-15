using dad.Models;

namespace dad.Services;

public interface IDadGatherClientSession
{
    bool HasOwnership { get; }
    bool TryReadCarriedCount(uint itemId, out long count, out string error);
    bool TryReadActivity(string runId, out bool enabled, out bool waiting, out string error);
    bool TryStart(string runId, IReadOnlyList<DadGatherWorkerOrder> orders, out string error);
    bool TryStop(string runId, out string error);
}

public interface IDadGatherClientService
{
    DadGatherStockReply Probe(DadGatherStockQuery query);
    bool TryCreateSession(out IDadGatherClientSession? session, out string error);
}

internal enum DadGatherWorkerRunState { Idle, Running, Waiting, Completed, Failed, Cancelled }

// One finite worker share. The coordinator owns splitting and transport; this class never
// reallocates, retries native starts, or treats the GBR toggle/status text as a receipt.
internal sealed class DadGatherWorkerRun(IDadGatherClientSession native)
{
    private string runId = string.Empty;
    private DadGatherWorkerOrder[] orders = [];
    private Dictionary<uint, long> counts = [];
    private DateTime nextReadUtc;

    public DadGatherWorkerRunState State { get; private set; }
    public string Summary { get; private set; } = string.Empty;
    public IReadOnlyDictionary<uint, long> Counts => counts;
    public bool IsActive => State is DadGatherWorkerRunState.Running or DadGatherWorkerRunState.Waiting;
    public bool HasOwnership => native.HasOwnership;

    public void Start(string attemptId, IReadOnlyList<DadGatherWorkerOrder> assignedOrders, DateTime nowUtc)
    {
        if (!string.IsNullOrEmpty(runId))
        {
            if (runId != attemptId || !orders.SequenceEqual(assignedOrders))
                throw new InvalidOperationException("This gathering worker already has a different frozen order.");
            return;
        }
        if (string.IsNullOrWhiteSpace(attemptId) || assignedOrders == null || assignedOrders.Count == 0 ||
            assignedOrders.Any(order => order == null || order.ItemId == 0 || order.StartingCount < 0 ||
                order.Quantity < 0 || order.Quantity > DadGatherPartyPlanningRules.MaximumQuantity ||
                order.NativeTarget != order.StartingCount + order.Quantity ||
                order.Quantity > 0 && order.NativeTarget > DadGatherPartyPlanningRules.MaximumQuantity ||
                string.IsNullOrWhiteSpace(order.SlotId) || string.IsNullOrWhiteSpace(order.WorkerSessionId) ||
                string.IsNullOrWhiteSpace(order.CharacterKey)) ||
            assignedOrders.Select(order => order.ItemId).Distinct().Count() != assignedOrders.Count ||
            assignedOrders.Any(order => order.SlotId != assignedOrders[0].SlotId ||
                order.WorkerSessionId != assignedOrders[0].WorkerSessionId || order.CharacterKey != assignedOrders[0].CharacterKey))
            throw new ArgumentException("A gathering worker requires one valid frozen client/item assignment.", nameof(assignedOrders));
        runId = attemptId; // Consume before the external start, including an uncertain response.
        orders = assignedOrders.ToArray();
        counts = orders.ToDictionary(order => order.ItemId, order => order.StartingCount);
        foreach (var order in orders)
        {
            if (!native.TryReadCarriedCount(order.ItemId, out var current, out var stockError) || current != order.StartingCount)
            {
                State = DadGatherWorkerRunState.Failed;
                Summary = string.IsNullOrWhiteSpace(stockError) ? "Carried stock changed after planning. Refresh the plan." : stockError;
                return;
            }
        }
        if (orders.All(order => order.Quantity == 0))
        {
            Complete(DadGatherWorkerRunState.Completed, "This client has no gathering deficit.");
            return;
        }
        if (!native.TryStart(runId, orders, out var error))
        {
            // The native adapter owns cleanup of its failed start. Do not replay uncertain cleanup here.
            State = DadGatherWorkerRunState.Failed;
            Summary = error;
            return;
        }
        State = DadGatherWorkerRunState.Running;
        Summary = "Gathering the fixed allocation.";
        nextReadUtc = nowUtc;
    }

    public void Update(DateTime nowUtc)
    {
        if (!IsActive || nowUtc < nextReadUtc) return;
        nextReadUtc = nowUtc.AddSeconds(1);
        var observed = new Dictionary<uint, long>();
        foreach (var order in orders)
        {
            if (!native.TryReadCarriedCount(order.ItemId, out var count, out var error) || count is < 0 or > int.MaxValue)
            {
                Complete(DadGatherWorkerRunState.Failed, string.IsNullOrWhiteSpace(error) ? "Carried inventory is unreadable." : error);
                return;
            }
            observed[order.ItemId] = count;
        }
        counts = observed;
        if (!native.TryReadActivity(runId, out var enabled, out var waiting, out var activityError))
        {
            Complete(DadGatherWorkerRunState.Failed, activityError);
            return;
        }
        // Whole-node yields can exceed the fixed threshold. Every assigned material must be proved.
        if (orders.All(order => order.Quantity == 0 || counts[order.ItemId] >= order.NativeTarget))
        {
            Complete(DadGatherWorkerRunState.Completed, "Gathering allocation completed and carried stock verified.");
            return;
        }
        if (!enabled)
        {
            Complete(DadGatherWorkerRunState.Failed, "GBR stopped before the fixed carried-stock targets were reached.");
            return;
        }
        State = waiting ? DadGatherWorkerRunState.Waiting : DadGatherWorkerRunState.Running;
        var acquired = orders.Sum(order => Math.Min(order.Quantity, Math.Max(0, counts[order.ItemId] - order.StartingCount)));
        Summary = waiting ? $"Waiting for gathering nodes ({acquired}/{orders.Sum(order => (long)order.Quantity)})."
            : $"Gathering fixed allocation ({acquired}/{orders.Sum(order => (long)order.Quantity)}).";
    }

    public void Cancel(string reason)
    {
        if (State == DadGatherWorkerRunState.Idle && !native.HasOwnership) return;
        if (IsActive || native.HasOwnership)
            Complete(DadGatherWorkerRunState.Cancelled, string.IsNullOrWhiteSpace(reason) ? "Gathering cancelled." : reason);
    }

    private void Complete(DadGatherWorkerRunState terminal, string summary)
    {
        if (native.HasOwnership && !native.TryStop(runId, out var error))
        {
            State = DadGatherWorkerRunState.Failed;
            Summary = summary + " Owned GBR cleanup remains unresolved: " + error;
            return;
        }
        State = terminal;
        Summary = summary;
    }
}
