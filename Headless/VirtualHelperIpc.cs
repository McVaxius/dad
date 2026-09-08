using System.Text.Json;

namespace dad.Headless;

// Models only the external helper's wire responses. DAD's adapters and executors
// still own request construction, correlation, admission, polling and cancellation.
internal sealed class VirtualHelperIpc(Action<string> observe)
{
    public string Stage { get; set; } = "running";
    public bool Ready { get; set; } = true;
    public string Fault { get; set; } = "";
    private string runId = "";
    private string activeHelper = "";
    private string role = "";
    private int attempts;
    public bool IsRunning => activeHelper.Length != 0 && Stage == "running";

    public object Invoke(string endpoint, object?[] values)
    {
        observe($"ipc:{endpoint}");
        if (endpoint is "MOGTOME.IsReady" or "LootGoblin.IsReady") return Ready;
        switch (endpoint)
        {
            case "MOGTOME.StartRun":
            case "LootGoblin.StartMapGatherJson":
                using (var request = JsonDocument.Parse((string)values[0]!))
                {
                    if (activeHelper.Length != 0 && Stage == "running")
                        throw new InvalidOperationException("Unexpected duplicate helper start while work is active.");
                    activeHelper = endpoint.Split('.')[0];
                    runId = request.RootElement.GetProperty(activeHelper == "MOGTOME" ? "dadRunId" : "requestId").GetString()!;
                    if (string.IsNullOrWhiteSpace(runId)) throw new InvalidOperationException("Helper requires an exact run identity.");
                    if (activeHelper == "MOGTOME")
                    {
                        role = request.RootElement.GetProperty("role").GetString()!;
                        attempts = request.RootElement.GetProperty("attemptLimit").GetInt32();
                    }
                    else if (!request.RootElement.GetProperty("useConfiguredMap").GetBoolean() ||
                             !request.RootElement.GetProperty("runAfterGather").GetBoolean())
                        throw new InvalidOperationException("Unexpected LootGoblin map request.");
                    Stage = "running";
                    observe($"helper:start:{activeHelper}:{runId}");
                }
                if (Fault == "uncertain-start") throw new IOException("Synthetic response lost after helper accepted work.");
                break;
            case "MOGTOME.GetRunStatus":
                RequireActive("MOGTOME", runId);
                break;
            case "LootGoblin.GetMapGatherStatusJson":
                RequireActive("LootGoblin", (string)values[0]!);
                break;
            case "MOGTOME.StopRun":
                using (var request = JsonDocument.Parse((string)values[0]!))
                    RequireActive("MOGTOME", request.RootElement.GetProperty("dadRunId").GetString()!);
                Stage = "cancelled";
                break;
            case "LootGoblin.CancelMapGatherJson":
                RequireActive("LootGoblin", (string)values[0]!);
                Stage = "cancelled";
                break;
            default: throw new InvalidOperationException($"Unexpected helper IPC: {endpoint}");
        }
        var responseId = Fault == "mismatched-id" && endpoint is "MOGTOME.StartRun" or "LootGoblin.StartMapGatherJson"
            ? "synthetic-unrelated-run" : runId;
        if (activeHelper == "MOGTOME")
            return JsonSerializer.Serialize(new
            {
                schemaVersion = 1, dadRunId = responseId, ready = Ready, accepted = true, dadOwned = true,
                isRunning = Stage == "running", isTerminal = Stage != "running", success = Stage == "completed",
                role, attemptLimit = attempts, completedAttempts = Stage == "completed" ? attempts : 0,
                engineState = Stage == "running" ? "InDuty" : Stage,
                summary = $"Synthetic helper {Stage}.", failureReason = "",
            });
        return JsonSerializer.Serialize(new
        {
            requestId = responseId, accepted = true, terminal = Stage != "running", success = Stage == "completed",
            state = Stage == "cancelled" ? "Cancelled" : Stage, message = $"Synthetic helper {Stage}.",
        });
    }

    private void RequireActive(string helper, string requestedRunId)
    {
        if (activeHelper != helper || runId != requestedRunId)
            throw new InvalidOperationException("Unexpected helper request for a different active run.");
    }
}
