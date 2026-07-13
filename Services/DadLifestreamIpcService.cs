using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace dad.Services;

public readonly record struct DadLifestreamState(bool Available, bool IsBusy, string Summary);

public sealed class DadLifestreamIpcService
{
    private readonly ICallGateSubscriber<bool> isBusy;

    public DadLifestreamIpcService(IDalamudPluginInterface pluginInterface)
    {
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
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
}
