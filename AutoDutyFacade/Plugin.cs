using System.Text.Json;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace AutoDutyFacade;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly List<Action> disposeActions = [];

    public Plugin()
    {
        Register<string>(FacadeContract.FacadePing, Ping);
        Register<uint, bool>(FacadeContract.PublicContentHasPath, ContentHasPath);
        Register<string, string, object>(FacadeContract.PublicSetConfig, SetConfig);
        Register<uint, int, bool, object>(FacadeContract.PublicRun, Run);
        Register<bool>(FacadeContract.PublicIsStopped, IsStopped);
        Register<object>(FacadeContract.PublicStop, Stop);

        CommandManager.AddHandler("/ad", new CommandInfo(OnCommand)
        {
            HelpMessage = "Show Dad-owned AutoDuty facade status.",
        });
        CommandManager.AddHandler("/autoduty", new CommandInfo(OnCommand)
        {
            HelpMessage = "Show Dad-owned AutoDuty facade status.",
        });

        Log.Information("[AutoDutyFacade] Dad-owned AutoDuty facade loaded.");
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler("/ad");
        CommandManager.RemoveHandler("/autoduty");

        foreach (var disposeAction in disposeActions)
        {
            try
            {
                disposeAction();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[AutoDutyFacade] IPC unregister action failed.");
            }
        }

        disposeActions.Clear();
    }

    private static string Ping()
        => $"{FacadeContract.FacadePingResponsePrefix}{typeof(Plugin).Assembly.GetName().Version}";

    private static void OnCommand(string command, string arguments)
        => ChatGui.Print($"[AutoDuty] Dad-owned facade loaded. {GetBackendStatus()} Use /dad status for details.");

    private static bool ContentHasPath(uint territoryType)
    {
        try
        {
            return PluginInterface
                .GetIpcSubscriber<uint, bool>(FacadeContract.BackendContentHasPath)
                .InvokeFunc(territoryType);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[AutoDutyFacade] Dad backend ContentHasPath unavailable for territory {TerritoryType}.", territoryType);
            return false;
        }
    }

    private static object SetConfig(string key, string value)
    {
        try
        {
            return PluginInterface
                .GetIpcSubscriber<string, string, object>(FacadeContract.BackendSetConfig)
                .InvokeFunc(key, value);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[AutoDutyFacade] Dad backend SetConfig unavailable for {Key}.", key);
            return false;
        }
    }

    private static object Run(uint territoryType, int loops, bool bareMode)
    {
        try
        {
            return PluginInterface
                .GetIpcSubscriber<uint, int, bool, object>(FacadeContract.BackendRun)
                .InvokeFunc(territoryType, loops, bareMode);
        }
        catch (Exception ex)
        {
            var message = $"Dad AutoDuty backend unavailable: {ex.Message}";
            Log.Warning(ex, "[AutoDutyFacade] Dad backend Run unavailable for territory {TerritoryType}.", territoryType);
            return JsonSerializer.Serialize(new
            {
                Status = "Rejected",
                Summary = message,
                FailureReason = message,
                RequestedBy = "Dad-owned AutoDuty facade",
            });
        }
    }

    private static bool IsStopped()
    {
        try
        {
            return PluginInterface
                .GetIpcSubscriber<bool>(FacadeContract.BackendIsStopped)
                .InvokeFunc();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[AutoDutyFacade] Dad backend IsStopped unavailable.");
            return true;
        }
    }

    private static object Stop()
    {
        try
        {
            return PluginInterface
                .GetIpcSubscriber<object>(FacadeContract.BackendStop)
                .InvokeFunc();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[AutoDutyFacade] Dad backend Stop unavailable.");
            return false;
        }
    }

    private static string GetBackendStatus()
    {
        try
        {
            return PluginInterface
                .GetIpcSubscriber<string>(FacadeContract.BackendPing)
                .InvokeFunc();
        }
        catch (Exception ex)
        {
            return $"Dad backend unavailable: {ex.Message}.";
        }
    }

    private void Register<TReturn>(string name, Func<TReturn> func)
    {
        var provider = PluginInterface.GetIpcProvider<TReturn>(name);
        provider.RegisterFunc(func);
        disposeActions.Add(provider.UnregisterFunc);
    }

    private void Register<TArg1, TReturn>(string name, Func<TArg1, TReturn> func)
    {
        var provider = PluginInterface.GetIpcProvider<TArg1, TReturn>(name);
        provider.RegisterFunc(func);
        disposeActions.Add(provider.UnregisterFunc);
    }

    private void Register<TArg1, TArg2, TReturn>(string name, Func<TArg1, TArg2, TReturn> func)
    {
        var provider = PluginInterface.GetIpcProvider<TArg1, TArg2, TReturn>(name);
        provider.RegisterFunc(func);
        disposeActions.Add(provider.UnregisterFunc);
    }

    private void Register<TArg1, TArg2, TArg3, TReturn>(string name, Func<TArg1, TArg2, TArg3, TReturn> func)
    {
        var provider = PluginInterface.GetIpcProvider<TArg1, TArg2, TArg3, TReturn>(name);
        provider.RegisterFunc(func);
        disposeActions.Add(provider.UnregisterFunc);
    }
}

internal static class FacadeContract
{
    public const string PublicContentHasPath = "AutoDuty.ContentHasPath";
    public const string PublicSetConfig = "AutoDuty.SetConfig";
    public const string PublicRun = "AutoDuty.Run";
    public const string PublicIsStopped = "AutoDuty.IsStopped";
    public const string PublicStop = "AutoDuty.Stop";

    public const string BackendPing = "Dad.AutoDutyCompat.Ping";
    public const string BackendContentHasPath = "Dad.AutoDutyCompat.ContentHasPath";
    public const string BackendSetConfig = "Dad.AutoDutyCompat.SetConfig";
    public const string BackendRun = "Dad.AutoDutyCompat.Run";
    public const string BackendIsStopped = "Dad.AutoDutyCompat.IsStopped";
    public const string BackendStop = "Dad.AutoDutyCompat.Stop";

    public const string FacadePing = "Dad.AutoDutyFacade.Ping";
    public const string FacadePingResponsePrefix = "Dad.AutoDutyFacade:";
}
