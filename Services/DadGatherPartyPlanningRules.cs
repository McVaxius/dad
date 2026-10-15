using dad.Models;

namespace dad.Services;

internal static class DadGatherPartyPlanningRules
{
    internal const int MaximumQuantity = 999999; // GBR AutoGatherList's native per-item limit.
    internal const int MaximumItems = 512;
    internal const int MaximumClients = DadPlannerSlotRules.MaxSlotNumber;

    public static bool TryValidateTask(DadGatherPartyTask? task, out string error)
    {
        error = string.Empty;
        if (task?.Items == null || task.Allocations == null || task.Items.Count > MaximumItems ||
            !task.Items.Any(item => item is { Enabled: true }))
            error = "Enable at least one valid gathering target within the item limit.";
        else if (task.Items.Any(item => item == null || item.RowId == Guid.Empty ||
                     item.Enabled && (item.ItemId == 0 || item.TargetQuantity is < 1 or > MaximumQuantity)) ||
                 task.Items.Select(item => item.RowId).Distinct().Count() != task.Items.Count ||
                 task.Items.Where(item => item.Enabled).Select(item => item.ItemId).Distinct().Count() != task.Items.Count(item => item.Enabled) ||
                 task.Allocations.Any(allocation => allocation == null))
            error = "Gathering targets need distinct item/row IDs and quantities from 1 to 999999.";
        return string.IsNullOrEmpty(error);
    }

    public static DadGatherPartyPreview Preview(DadGatherPartyTask task, IReadOnlyList<DadGatherWorkerStock> workers)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(workers);
        var blockers = new List<string>();
        var rows = new List<DadGatherItemPreview>();
        if (!TryValidateTask(task, out var taskError)) return new([], [taskError]);
        if (workers.Count == 0 || workers.Count > MaximumClients)
            return new([], ["Select at least one connected gathering client."]);

        var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var characters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var worker in workers)
        {
            if (worker == null || string.IsNullOrWhiteSpace(worker.SlotId) ||
                string.IsNullOrWhiteSpace(worker.WorkerSessionId) || string.IsNullOrWhiteSpace(worker.CharacterKey) ||
                string.IsNullOrWhiteSpace(worker.AccountKey))
            {
                blockers.Add("A selected gathering client has incomplete identity.");
                continue;
            }
            if (!slots.Add(worker.SlotId) || !sessions.Add(worker.WorkerSessionId) ||
                !characters.Add(worker.CharacterKey) || !accounts.Add(worker.AccountKey))
                blockers.Add("Gathering clients must have distinct slots, sessions, characters and accounts.");
            if (!worker.Ready || !worker.InventoryAvailable || worker.CarriedCounts == null || worker.EligibleItems == null)
                blockers.Add($"Gathering client {worker.SlotId} is unavailable or its carried inventory is unreadable.");
        }
        if (blockers.Count > 0)
            return new(rows, blockers);

        var enabled = task.Items.Where(item => item is { Enabled: true }).ToList();
        var allocationKeys = new HashSet<(uint, string)>();
        foreach (var allocation in task.Allocations)
        {
            if (allocation == null || string.IsNullOrWhiteSpace(allocation.SlotId) ||
                !slots.Contains(allocation.SlotId) || !enabled.Any(item => item.ItemId == allocation.ItemId) ||
                allocation.Quantity is < 0 or > MaximumQuantity ||
                !allocationKeys.Add((allocation.ItemId, allocation.SlotId.ToUpperInvariant())))
                blockers.Add("An edited gathering allocation is invalid, duplicated or refers to a missing client/target.");
        }
        if (blockers.Count > 0)
            return new(rows, blockers);

        foreach (var item in enabled)
        {
            var counts = workers.Select(worker => worker.CarriedCounts.GetValueOrDefault(item.ItemId)).ToArray();
            if (counts.Any(count => count < 0 || count > int.MaxValue))
            {
                blockers.Add($"Carried inventory for item {item.ItemId} is invalid.");
                continue;
            }
            var onHand = counts.Sum();
            var remaining = Math.Max(0, item.TargetQuantity - onHand);
            var eligible = workers.Select((worker, index) => (worker, index))
                .Where(entry => entry.worker.EligibleItems.Contains(item.ItemId)).ToArray();
            var quantities = new int[workers.Count];
            var edited = task.Allocations.Where(allocation => allocation.ItemId == item.ItemId).ToArray();
            if (edited.Length > 0)
            {
                foreach (var allocation in edited)
                {
                    var index = Enumerable.Range(0, workers.Count).First(index =>
                        string.Equals(workers[index].SlotId, allocation.SlotId, StringComparison.OrdinalIgnoreCase));
                    quantities[index] = allocation.Quantity;
                    if (allocation.Quantity > 0 && !workers[index].EligibleItems.Contains(item.ItemId))
                        blockers.Add($"Client {allocation.SlotId} cannot gather item {item.ItemId}.");
                }
                if (quantities.Sum(quantity => (long)quantity) != remaining)
                    blockers.Add($"Edited allocations for item {item.ItemId} must total the current deficit {remaining}.");
            }
            else if (remaining > 0 && eligible.Length == 0)
                blockers.Add($"No selected client can gather item {item.ItemId}.");
            else if (remaining > 0)
            {
                var quotient = remaining / eligible.Length;
                var remainder = remaining % eligible.Length;
                for (var index = 0; index < eligible.Length; index++)
                    quantities[eligible[index].index] = checked((int)(quotient + (index < remainder ? 1 : 0)));
            }

            var orders = new List<DadGatherWorkerOrder>();
            for (var index = 0; index < workers.Count; index++)
            {
                var worker = workers[index];
                var target = counts[index] + quantities[index];
                if (quantities[index] > 0 && target > MaximumQuantity)
                    blockers.Add($"Native GBR target for client {worker.SlotId}, item {item.ItemId}, exceeds 999999.");
                orders.Add(new(worker.SlotId, worker.WorkerSessionId, worker.CharacterKey,
                    item.ItemId, counts[index], quantities[index], target, item.PreferredLocationId, worker.AccountKey));
            }
            rows.Add(new(item.RowId, item.ItemId, item.TargetQuantity, onHand, remaining, orders));
        }
        return new(rows, blockers);
    }
}
