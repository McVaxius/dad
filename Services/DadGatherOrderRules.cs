using dad.Models;

namespace dad.Services;

internal static class DadGatherOrderRules
{
    public static bool TryValidate(DadRunRequest request, IReadOnlyList<DadParticipantSnapshot> participants, out string error)
    {
        error = string.Empty;
        var task = request.GatherParty;
        var orders = request.GatherOrders;
        if (task == null || request.GetConfiguredTaskCount() != 1 || task.Items == null ||
            task.Items.Count > DadGatherPartyPlanningRules.MaximumItems || participants.Count is < 1 or > DadGatherPartyPlanningRules.MaximumClients ||
            participants.Any(participant => !string.IsNullOrWhiteSpace(participant.RegisteredIslandId)))
            return Fail("Gathering requires one finite task and valid directly connected workers.", out error);
        var items = task.Items.Where(item => item is { Enabled: true }).ToArray();
        if (items.Length == 0 || items.Any(item => item.ItemId == 0 || item.TargetQuantity is < 1 or > DadGatherPartyPlanningRules.MaximumQuantity) ||
            items.Select(item => item.ItemId).Distinct().Count() != items.Length ||
            orders == null || orders.Count != items.Length * participants.Count || orders.Any(order => order == null))
            return Fail("Gathering is missing its exact frozen item/client allocation.", out error);
        foreach (var participant in participants)
        {
            var assigned = orders.Where(order => order.SlotId == participant.AssignedSlotId).ToArray();
            if (assigned.Length != items.Length || assigned.Select(order => order.ItemId).Distinct().Count() != items.Length ||
                assigned.Any(order => order.WorkerSessionId != participant.WorkerSessionId.Value ||
                    order.CharacterKey != participant.ActiveCharacterKey.Value || order.AccountKey != participant.ManagedAccountKey.Value ||
                    !items.Any(item => item.ItemId == order.ItemId && item.PreferredLocationId == order.PreferredLocationId) ||
                    order.StartingCount is < 0 or > int.MaxValue || order.Quantity is < 0 or > DadGatherPartyPlanningRules.MaximumQuantity ||
                    order.NativeTarget != order.StartingCount + order.Quantity ||
                    order.Quantity > 0 && order.NativeTarget > DadGatherPartyPlanningRules.MaximumQuantity))
                return Fail("Gathering orders do not match the exact frozen account/character/session/slot and material targets.", out error);
        }
        foreach (var item in items)
        {
            var assigned = orders.Where(order => order.ItemId == item.ItemId).ToArray();
            if (assigned.Sum(order => (long)order.Quantity) != Math.Max(0, item.TargetQuantity - assigned.Sum(order => order.StartingCount)))
                return Fail("Gathering shares do not total the frozen combined carried-stock deficit.", out error);
        }
        return true;
    }

    private static bool Fail(string message, out string error) { error = message; return false; }
}
