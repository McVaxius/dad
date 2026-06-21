using System.Diagnostics;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

// Feature batch A (dadfeatures20260620b): runs operator-chosen actions when a Dad run completes.
// Invoked from DadCoordinatorService.FinalizeRun on successful completion (framework thread).
internal static class DadCompletionActionRunner
{
    public static void Run(Configuration configuration, IPluginLog log)
    {
        var actions = configuration.CompletionActions;
        if (actions == null)
            return;

        if (actions.PlaySound)
        {
            try
            {
                // se.1..se.16 (UIGlobals.PlaySoundEffect, same call ChatAlerts uses; needs unsafe context).
                unsafe
                {
                    FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.PlaySoundEffect((uint)Math.Clamp(actions.SoundEffectId, 1, 16));
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[dad] Completion sound failed.");
            }
        }

        if (actions.RunCommands && actions.Commands is { Count: > 0 })
        {
            foreach (var command in actions.Commands)
            {
                if (string.IsNullOrWhiteSpace(command))
                    continue;

                try
                {
                    Plugin.CommandManager.ProcessCommand(command.Trim());
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "[dad] Completion command failed: {Command}", command);
                }
            }
        }

        // Dangerous shutdown actions are only honored when the operator has explicitly enabled
        // advanced mode (/dad advanced). Hidden + gated per the feature spec.
        if (configuration.AdvancedModeEnabled && actions.KillMode != DadCompletionKillMode.None)
        {
            RunKillAction(actions.KillMode, log);
        }
    }

    private static void RunKillAction(DadCompletionKillMode mode, IPluginLog log)
    {
        try
        {
            switch (mode)
            {
                case DadCompletionKillMode.CloseGameClient:
                    log.Warning("[dad] Completion action: closing game client.");
                    Environment.Exit(0);
                    break;

                case DadCompletionKillMode.ShutDownPc:
                    // Cancelable 60s delay so an operator can abort with `shutdown /a`.
                    log.Warning("[dad] Completion action: scheduling PC shutdown in 60s (cancel with 'shutdown /a').");
                    Process.Start(new ProcessStartInfo("shutdown", "/s /t 60")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Completion kill action failed.");
        }
    }
}
