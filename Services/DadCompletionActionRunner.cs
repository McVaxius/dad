using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

// Feature batch A (dadfeatures20260620b): runs operator-chosen actions when a Dad run completes.
// Invoked from DadCoordinatorService.FinalizeRun on successful completion (framework thread).
internal static class DadCompletionActionRunner
{
    private static readonly IDadGameCommandExecutor NativeCommandExecutor = new DadNativeGameCommandExecutor();

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

    public static void Enqueue(Configuration configuration, IPluginLog log, DadRunRequest? request = null)
    {
        var actions = DadCompletionActionSnapshots.Resolve(request?.CompletionActions, configuration.CompletionActions);

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

        // Legacy kill actions are preserved in config for compatibility, but self-contained
        // Dad no longer closes the game client or launches OS shutdown commands.
        if (actions.KillMode != DadCompletionKillMode.None)
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
                RunNativeCommand(step.Payload, "post-run completion command", log);
                break;
            case StepKind.KillAction:
                if (int.TryParse(step.Payload, out var killMode) &&
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

        RunNativeCommand(trimmed, "Grand Company hand-in command", log);
    }

    private static void RunNativeCommand(string command, string actionLabel, IPluginLog log)
    {
        if (NativeCommandExecutor.TryExecute(command, out var failure))
        {
            log.Information("[dad] Submitted native {ActionLabel}.", actionLabel);
            return;
        }

        log.Warning(
            "[dad] Rejected {ActionLabel}: {Failure}",
            actionLabel,
            string.IsNullOrWhiteSpace(failure) ? "native chat submission failed" : failure);
    }

    private static void RunKillAction(DadCompletionKillMode mode, IPluginLog log)
    {
        log.Warning("[dad] Completion kill action {Mode} is configured but disabled; no OS process action was taken.", mode);
    }
}
