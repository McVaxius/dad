using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using dad.Models;

namespace dad.Services;

public readonly record struct DadLifestreamState(
    bool Available,
    bool IsBusy,
    bool CanAutoLogin,
    string Summary);

public sealed class DadLifestreamIpcService
{
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<bool> canAutoLogin;
    private readonly ICallGateSubscriber<string, bool> changeWorld;
    private readonly ICallGateSubscriber<string, string, bool> connectAndLogin;

    public DadLifestreamIpcService(IDalamudPluginInterface pluginInterface)
    {
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        canAutoLogin = pluginInterface.GetIpcSubscriber<bool>("Lifestream.CanAutoLogin");
        changeWorld = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
        connectAndLogin = pluginInterface.GetIpcSubscriber<string, string, bool>("Lifestream.ConnectAndLogin");
    }

    public DadLifestreamState Inspect(bool includeAutoLogin = false)
    {
        try
        {
            var busy = isBusy.InvokeFunc();
            if (!includeAutoLogin)
                return new DadLifestreamState(true, busy, false, "Lifestream busy IPC available.");
            try
            {
                var loginReady = canAutoLogin.InvokeFunc();
                return new DadLifestreamState(
                    true,
                    busy,
                    loginReady,
                    loginReady
                        ? "Lifestream busy and CanAutoLogin IPC are available."
                        : "Lifestream is not currently ready for auto-login.");
            }
            catch (Exception ex)
            {
                // Preserve ordinary world-ready busy proof while title login fails closed.
                return new DadLifestreamState(
                    true,
                    busy,
                    false,
                    $"Lifestream busy IPC available; CanAutoLogin unavailable: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            // Relog retries fail closed when Lifestream cannot prove that it is idle.
            return new DadLifestreamState(false, true, false, $"Lifestream busy IPC unavailable: {ex.Message}");
        }
    }

    public DadLifestreamLoginResult ConnectAndLogin(string characterName, string homeWorld)
    {
        var name = characterName?.Trim() ?? string.Empty;
        var world = homeWorld?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world))
        {
            return new DadLifestreamLoginResult(
                DadLifestreamLoginOutcome.Uncertain,
                "Lifestream.ConnectAndLogin requires an exact character name and home world.");
        }

        try
        {
            var accepted = connectAndLogin.InvokeFunc(name, world);
            return accepted
                ? new DadLifestreamLoginResult(
                    DadLifestreamLoginOutcome.Accepted,
                    $"Lifestream accepted login for {name}@{world}.")
                : new DadLifestreamLoginResult(
                    DadLifestreamLoginOutcome.ExplicitFalse,
                    $"Lifestream explicitly rejected login for {name}@{world}.");
        }
        catch (Exception ex)
        {
            // The call may have crossed the IPC boundary before the exception. Never retry uncertainty.
            return new DadLifestreamLoginResult(
                DadLifestreamLoginOutcome.Uncertain,
                $"Lifestream.ConnectAndLogin IPC uncertainty: {ex.Message}");
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
