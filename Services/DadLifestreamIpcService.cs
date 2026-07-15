using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using dad.Models;

namespace dad.Services;

public readonly record struct DadLifestreamState(bool Available, bool IsBusy, string Summary);

public sealed class DadLifestreamIpcService
{
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<string, bool> changeWorld;

    public DadLifestreamIpcService(IDalamudPluginInterface pluginInterface)
    {
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        changeWorld = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
    }

    public DadLifestreamState Inspect()
    {
        try
        {
            return new DadLifestreamState(true, isBusy.InvokeFunc(), "Lifestream busy IPC available.");
        }
        catch (Exception ex)
        {
            // Relog retries fail closed when Lifestream cannot prove that it is idle.
            return new DadLifestreamState(false, true, $"Lifestream busy IPC unavailable: {ex.Message}");
        }
    }

    public DadLifestreamChangeWorldResult ChangeWorld(string worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName))
        {
            return new DadLifestreamChangeWorldResult(
                DadLifestreamChangeWorldOutcome.Uncertain,
                "Lifestream.ChangeWorld destination world is empty.");
        }

        try
        {
            var accepted = changeWorld.InvokeFunc(worldName.Trim());
            return accepted
                ? new DadLifestreamChangeWorldResult(
                    DadLifestreamChangeWorldOutcome.Accepted,
                    $"Lifestream accepted travel to {worldName.Trim()}.")
                : new DadLifestreamChangeWorldResult(
                    DadLifestreamChangeWorldOutcome.ExplicitFalse,
                    $"Lifestream explicitly rejected travel to {worldName.Trim()}.");
        }
        catch (Exception ex)
        {
            // The call may have crossed the IPC boundary before the exception. Never retry uncertainty.
            return new DadLifestreamChangeWorldResult(
                DadLifestreamChangeWorldOutcome.Uncertain,
                $"Lifestream.ChangeWorld IPC uncertainty: {ex.Message}");
        }
    }
}
