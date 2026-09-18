using dad.Models;
using dad.Services;

namespace dad.Headless;

internal sealed class VirtualShoppingIpc(Action<string> observe)
{
    private DadAdsShopListPresetRequest? request;
    public string Stage { get; set; } = "running";
    public string Fault { get; set; } = "";
    public object Invoke(string endpoint, object?[] values)
    {
        if (endpoint == "ADS.StartShopListPreset" && values is [string json])
        {
            if (request != null && (Fault != "finite" || Stage is not ("completed" or "cancelled")))
                throw new InvalidOperationException("Unexpected repeated shopping dispatch.");
            request = DadIpcJson.Deserialize<DadAdsShopListPresetRequest>(json)!;
            Stage = "running";
            if (request.PresetId != "11111111-1111-1111-1111-111111111111" || request.CompletedRowIds.Count != 0)
                throw new InvalidOperationException("Unexpected shopping preset or completion evidence.");
            observe($"ipc:shopping-start:{request.OperationId}:{request.PresetId}");
            if (Fault == "finite")
            {
                if (!request.SupportsFiniteOrderProgress)
                    throw new InvalidOperationException("Finite order caller omitted progress support.");
                observe($"ipc:shopping-progress:{request.CreditedQuantities?.GetValueOrDefault("22222222-2222-2222-2222-222222222222") ?? 0}");
            }
            if (Fault == "uncertain") throw new IOException("Synthetic start response lost after acceptance.");
            return DadIpcJson.Serialize(new DadAdsShopListStartResponse { Version = 1, Accepted = true,
                OperationId = request.OperationId, PresetId = request.PresetId, Disposition = "accepted" });
        }
        if (request == null || values is not [string operation] || operation != request.OperationId)
            throw new InvalidOperationException("Unexpected shopping IPC correlation.");
        if (endpoint == "ADS.CancelShopListPreset")
        { observe($"ipc:shopping-cancel:{operation}"); Stage = "cancelled"; return true; }
        if (endpoint != "ADS.GetShopListPresetStatusJson") throw new InvalidOperationException($"Unknown shopping IPC {endpoint}.");
        observe($"ipc:shopping-poll:{operation}");
        var done = Stage is "completed" or "cancelled";
        var finite = Fault == "finite";
        var baseline = request.CreditedQuantities?.GetValueOrDefault("22222222-2222-2222-2222-222222222222") ?? 0;
        var credited = Math.Min(60, baseline + (Stage == "completed" ? 20 : Stage == "cancelled" ? 10 : 0));
        var fulfilled = Stage == "completed" && (!finite || credited >= 60);
        return DadIpcJson.Serialize(new DadAdsShopListStatusResponse
        {
            Version = 1, OperationId = Fault == "correlation" && !done ? "wrong-operation" : operation, PresetId = request.PresetId,
            Running = !done, Done = done, Succeeded = done ? Stage == "completed" : null,
            Disposition = Stage == "completed" ? finite ? fulfilled ? "fulfilled" : "partial" : "succeeded" : Stage, StatusMessage = "Synthetic shopping observation",
            CreditedQuantities = finite ? new() { ["22222222-2222-2222-2222-222222222222"] = credited } : null,
            CompletedNonRepeatableRowIds = fulfilled ? ["22222222-2222-2222-2222-222222222222"] : [],
            Rows = [new() { RowId = "22222222-2222-2222-2222-222222222222", ItemId = 1, ItemName = "Synthetic supply",
                Repeatable = false, RefillToAtLeast = finite ? 60 : 10, Outcome = fulfilled ? "purchased" : "pending",
                OwnedQuantity = Stage == "completed" ? 10 : 0, PurchasedQuantity = Stage == "completed" ? finite ? 20 : 10 : 0 }],
        });
    }
}
