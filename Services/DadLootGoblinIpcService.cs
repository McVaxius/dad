using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace dad.Services;

public sealed class DadLootGoblinIpcService
{
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<string, string> startMapGather;
    private readonly ICallGateSubscriber<string, string> getMapGatherStatus;
    private readonly ICallGateSubscriber<string, string> cancelMapGather;

    public DadLootGoblinIpcService(IDalamudPluginInterface pluginInterface)
    {
        isReady = pluginInterface.GetIpcSubscriber<bool>("LootGoblin.IsReady");
        startMapGather = pluginInterface.GetIpcSubscriber<string, string>("LootGoblin.StartMapGatherJson");
        getMapGatherStatus = pluginInterface.GetIpcSubscriber<string, string>("LootGoblin.GetMapGatherStatusJson");
        cancelMapGather = pluginInterface.GetIpcSubscriber<string, string>("LootGoblin.CancelMapGatherJson");
    }

    public bool IsReady()
    {
        try
        {
            return isReady.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public DadLootGoblinMapGatherStatus Start(string runId)
    {
        var request = new DadLootGoblinMapGatherStartRequest
        {
            RequestId = runId,
            UseConfiguredMap = true,
            RunAfterGather = true,
        };
        try
        {
            return ReadCorrelated(
                startMapGather.InvokeFunc(DadIpcJson.Serialize(request)),
                runId,
                "start");
        }
        catch (Exception ex)
        {
            return DadLootGoblinMapGatherStatus.Failed(
                runId,
                $"LootGoblin start IPC failed ({ex.GetType().Name}).");
        }
    }

    public DadLootGoblinMapGatherStatus GetStatus(string runId)
    {
        try
        {
            return ReadCorrelated(getMapGatherStatus.InvokeFunc(runId), runId, "status");
        }
        catch (Exception ex)
        {
            return DadLootGoblinMapGatherStatus.Failed(
                runId,
                $"LootGoblin status IPC failed ({ex.GetType().Name}).");
        }
    }

    public DadLootGoblinMapGatherStatus Cancel(string runId)
    {
        try
        {
            return ReadCorrelated(cancelMapGather.InvokeFunc(runId), runId, "cancel");
        }
        catch (Exception ex)
        {
            return DadLootGoblinMapGatherStatus.Failed(
                runId,
                $"LootGoblin cancel IPC failed ({ex.GetType().Name}).");
        }
    }

    private static DadLootGoblinMapGatherStatus ReadCorrelated(
        string responseJson,
        string expectedRunId,
        string operation)
    {
        var response = DadIpcJson.Deserialize<DadLootGoblinMapGatherStatus>(responseJson);
        if (response == null)
            return DadLootGoblinMapGatherStatus.Failed(
                expectedRunId,
                $"LootGoblin returned unreadable {operation} status.");
        if (string.IsNullOrWhiteSpace(response.RequestId) ||
            !string.Equals(response.RequestId, expectedRunId, StringComparison.Ordinal))
        {
            return DadLootGoblinMapGatherStatus.Failed(
                expectedRunId,
                $"LootGoblin {operation} response ID did not match the exact DAD run ID.");
        }

        return response;
    }
}

public sealed class DadLootGoblinMapGatherStartRequest
{
    public string RequestId { get; set; } = string.Empty;
    public bool UseConfiguredMap { get; set; }
    public bool RunAfterGather { get; set; }
}

public sealed class DadLootGoblinMapGatherStatus
{
    public string RequestId { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public bool Terminal { get; set; }
    public bool Success { get; set; }
    public string State { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    public static DadLootGoblinMapGatherStatus Failed(string runId, string reason)
        => new()
        {
            RequestId = runId,
            Terminal = true,
            State = "Failed",
            Message = reason,
        };
}
