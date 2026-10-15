using dad.Models;

namespace dad.Services;

internal static class DadGatherStockQueryRules
{
    public static bool TryRead(DadGatherStockQuery query, DadGatherStockReply reply, DateTime nowUtc,
        out DadGatherWorkerStock? stock, out string error)
    {
        stock = null;
        error = reply.Error;
        if (reply.SchemaVersion != DadWorkerCommandSchemaRules.GatheringSchema ||
            reply.RequestId != query.RequestId || reply.RunId != query.RunId || reply.SlotId != query.SlotId ||
            reply.WorkerSessionId != query.WorkerSessionId || reply.AccountKey != query.AccountKey ||
            reply.CharacterKey != query.CharacterKey || reply.ObservedAtUtc < query.RequestedAtUtc.AddSeconds(-5) ||
            reply.ObservedAtUtc > nowUtc.AddSeconds(5) || nowUtc - reply.ObservedAtUtc > TimeSpan.FromMinutes(2))
        {
            error = "Gathering stock response does not match the exact current request/client identity.";
            return false;
        }
        if (!reply.Available || reply.CarriedCounts == null || reply.EligibleItems == null ||
            !query.ItemIds.Order().SequenceEqual(reply.CarriedCounts.Keys.Order()) ||
            reply.CarriedCounts.Values.Any(count => count is < 0 or > int.MaxValue) ||
            reply.EligibleItems.Any(item => !query.ItemIds.Contains(item)))
        {
            if (string.IsNullOrWhiteSpace(error)) error = "Gathering stock is unavailable or incomplete.";
            return false;
        }
        stock = new(query.SlotId, query.WorkerSessionId.Value, query.AccountKey.Value, query.CharacterKey.Value,
            true, true, new Dictionary<uint, long>(reply.CarriedCounts), new HashSet<uint>(reply.EligibleItems));
        return true;
    }
}
