using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using dad.Models;

namespace dad.Services;

public sealed class DadMogtomeIpcService
{
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<string, string> startRun;
    private readonly ICallGateSubscriber<string> getRunStatus;
    private readonly ICallGateSubscriber<string, string> stopRun;

    public DadMogtomeIpcService(IDalamudPluginInterface pluginInterface)
    {
        isReady = pluginInterface.GetIpcSubscriber<bool>("MOGTOME.IsReady");
        startRun = pluginInterface.GetIpcSubscriber<string, string>("MOGTOME.StartRun");
        getRunStatus = pluginInterface.GetIpcSubscriber<string>("MOGTOME.GetRunStatus");
        stopRun = pluginInterface.GetIpcSubscriber<string, string>("MOGTOME.StopRun");
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

    public DadMogtomeRunStatus Start(
        DadRunPlan plan,
        DadWorkerExecutionRole role)
    {
        var task = plan.Request.Mogtome ?? new DadMogtomeTask();
        var request = new DadMogtomeRunRequest
        {
            DadRunId = plan.Request.RequestId,
            Role = role.ToString(),
            Preset = task.Preset,
            DutyPolicy = task.DutyPolicy,
            AttemptLimit = Math.Max(1, task.Attempts),
        };
        try
        {
            return DadIpcJson.Deserialize<DadMogtomeRunStatus>(
                       startRun.InvokeFunc(DadIpcJson.Serialize(request)))
                   ?? DadMogtomeRunStatus.Failed("MOGTOME returned unreadable start status.");
        }
        catch (Exception ex)
        {
            return DadMogtomeRunStatus.Failed($"MOGTOME start IPC failed ({ex.GetType().Name}).");
        }
    }

    public DadMogtomeRunStatus GetStatus()
    {
        try
        {
            return DadIpcJson.Deserialize<DadMogtomeRunStatus>(getRunStatus.InvokeFunc())
                   ?? DadMogtomeRunStatus.Failed("MOGTOME returned unreadable status.");
        }
        catch (Exception ex)
        {
            return DadMogtomeRunStatus.Failed($"MOGTOME status IPC failed ({ex.GetType().Name}).");
        }
    }

    public DadMogtomeRunStatus Stop(string runId, string reason)
    {
        try
        {
            return DadIpcJson.Deserialize<DadMogtomeRunStatus>(stopRun.InvokeFunc(DadIpcJson.Serialize(new
                   {
                       schemaVersion = 1,
                       dadRunId = runId,
                       reason,
                   })))
                   ?? DadMogtomeRunStatus.Failed("MOGTOME returned unreadable stop status.");
        }
        catch (Exception ex)
        {
            return DadMogtomeRunStatus.Failed($"MOGTOME stop IPC failed ({ex.GetType().Name}).");
        }
    }
}

public sealed class DadMogtomeRunRequest
{
    public int SchemaVersion { get; set; } = 1;
    public string DadRunId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Preset { get; set; } = string.Empty;
    public string DutyPolicy { get; set; } = string.Empty;
    public int AttemptLimit { get; set; } = 1;
}

public sealed class DadMogtomeRunStatus
{
    public int SchemaVersion { get; set; } = 1;
    public string DadRunId { get; set; } = string.Empty;
    public bool Ready { get; set; }
    public bool Accepted { get; set; }
    public bool DadOwned { get; set; }
    public bool IsRunning { get; set; }
    public bool IsTerminal { get; set; }
    public bool Success { get; set; }
    public string Role { get; set; } = string.Empty;
    public int AttemptLimit { get; set; }
    public int CompletedAttempts { get; set; }
    public string EngineState { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;

    public static DadMogtomeRunStatus Failed(string reason)
        => new()
        {
            IsTerminal = true,
            Summary = reason,
            FailureReason = reason,
        };
}
