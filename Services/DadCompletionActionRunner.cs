using System.Diagnostics;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

// Feature batch A (dadfeatures20260620b): runs operator-chosen actions when a Dad run completes.
// Invoked from DadCoordinatorService.FinalizeRun on successful completion (framework thread).
internal static class DadCompletionActionRunner
{
    private enum StepKind
    {
        Sound,
        GearCoffers,
        TripleTriadRegister,
        TripleTriadSell,
        GrandCompanyHandIn,
        CustomCommand,
        KillAction,
    }

    private sealed record PendingStep(StepKind Kind, string Payload = "");

    private static readonly Queue<PendingStep> PendingSteps = new();

    public static bool HasPendingWork => PendingSteps.Count > 0;

    public static void Enqueue(Configuration configuration, IPluginLog log)
    {
        var actions = configuration.CompletionActions;
        if (actions == null)
            return;

        PendingSteps.Clear();
        if (actions.PlaySound)
            PendingSteps.Enqueue(new PendingStep(StepKind.Sound, Math.Clamp(actions.SoundEffectId, 1, 16).ToString()));

        var utilities = actions.Utilities ?? new DadPostRunUtilities();
        if (utilities.OpenGearCoffers)
            PendingSteps.Enqueue(new PendingStep(StepKind.GearCoffers));
        if (utilities.RegisterTripleTriadCards)
            PendingSteps.Enqueue(new PendingStep(StepKind.TripleTriadRegister));
        if (utilities.SellTripleTriadCards)
            PendingSteps.Enqueue(new PendingStep(StepKind.TripleTriadSell));
        if (utilities.GrandCompanyHandInViaAutoRetainer)
            PendingSteps.Enqueue(new PendingStep(StepKind.GrandCompanyHandIn, utilities.GrandCompanyHandInCommand));

        if (actions.RunCommands && actions.Commands is { Count: > 0 })
        {
            foreach (var command in actions.Commands)
            {
                if (string.IsNullOrWhiteSpace(command))
                    continue;

                PendingSteps.Enqueue(new PendingStep(StepKind.CustomCommand, command.Trim()));
            }
        }

        // Dangerous shutdown actions are only honored when the operator has explicitly enabled
        // advanced mode (/dad advanced). Hidden + gated per the feature spec.
        if (configuration.AdvancedModeEnabled && actions.KillMode != DadCompletionKillMode.None)
            PendingSteps.Enqueue(new PendingStep(StepKind.KillAction, ((int)actions.KillMode).ToString()));

        if (PendingSteps.Count > 0)
            log.Information("[dad] Queued {Count} post-run completion action(s).", PendingSteps.Count);
    }

    public static void Update(Configuration configuration, IPluginLog log)
    {
        if (PendingSteps.Count == 0)
            return;

        var step = PendingSteps.Dequeue();
        try
        {
            RunStep(step, configuration, log);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Post-run completion step {StepKind} failed.", step.Kind);
        }
    }

    private static void RunStep(PendingStep step, Configuration configuration, IPluginLog log)
    {
        switch (step.Kind)
        {
            case StepKind.Sound:
                unsafe
                {
                    FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.PlaySoundEffect(uint.Parse(step.Payload));
                }
                break;
            case StepKind.GearCoffers:
                log.Information("[dad] Gear coffer post-run utility is enabled but no guarded item-use executor is active in this build.");
                break;
            case StepKind.TripleTriadRegister:
                log.Information("[dad] Triple Triad card registration utility is enabled but no guarded item-use executor is active in this build.");
                break;
            case StepKind.TripleTriadSell:
                log.Information("[dad] Triple Triad card selling utility is enabled but no guarded sell executor is active in this build.");
                break;
            case StepKind.GrandCompanyHandIn:
                RunGrandCompanyHandIn(step.Payload, log);
                break;
            case StepKind.CustomCommand:
                Plugin.CommandManager.ProcessCommand(step.Payload);
                break;
            case StepKind.KillAction:
                if (configuration.AdvancedModeEnabled &&
                    int.TryParse(step.Payload, out var killMode) &&
                    Enum.IsDefined(typeof(DadCompletionKillMode), killMode))
                {
                    RunKillAction((DadCompletionKillMode)killMode, log);
                }
                break;
        }
    }

    private static void RunGrandCompanyHandIn(string command, IPluginLog log)
    {
        var trimmed = command?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            log.Information("[dad] Grand Company hand-in utility skipped: no AutoRetainer command configured.");
            return;
        }

        if (!trimmed.StartsWith("/", StringComparison.Ordinal) || trimmed.Contains('\n') || trimmed.Contains('\r'))
        {
            log.Warning("[dad] Grand Company hand-in utility skipped invalid command.");
            return;
        }

        Plugin.CommandManager.ProcessCommand(trimmed);
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
