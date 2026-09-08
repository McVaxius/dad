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
            if (request != null) throw new InvalidOperationException("Unexpected repeated shopping dispatch.");
            request = DadIpcJson.Deserialize<DadAdsShopListPresetRequest>(json)!;
            if (request.PresetId != "11111111-1111-1111-1111-111111111111" || request.CompletedRowIds.Count != 0)
                throw new InvalidOperationException("Unexpected shopping preset or completion evidence.");
            observe($"ipc:shopping-start:{request.OperationId}:{request.PresetId}");
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
        return DadIpcJson.Serialize(new DadAdsShopListStatusResponse
        {
            Version = 1, OperationId = Fault == "correlation" && !done ? "wrong-operation" : operation, PresetId = request.PresetId,
            Running = !done, Done = done, Succeeded = done ? Stage == "completed" : null,
            Disposition = Stage == "completed" ? "succeeded" : Stage, StatusMessage = "Synthetic shopping observation",
            CompletedNonRepeatableRowIds = Stage == "completed" ? ["22222222-2222-2222-2222-222222222222"] : [],
            Rows = [new() { RowId = "22222222-2222-2222-2222-222222222222", ItemId = 1, ItemName = "Synthetic supply",
                Repeatable = false, OwnedQuantity = Stage == "completed" ? 10 : 0, PurchasedQuantity = Stage == "completed" ? 10 : 0 }],
        });
    }
}
