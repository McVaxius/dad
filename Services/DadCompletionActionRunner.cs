using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed record DadCompletionActionStatus(
    string SafeCode,
    string Summary,
    DateTime ObservedAtUtc,
    bool Failed);

// Feature batch A (dadfeatures20260620b): runs operator-chosen actions when a Dad run completes.
// Invoked from DadCoordinatorService.FinalizeRun on successful completion (framework thread).
internal static class DadCompletionActionRunner
{
    private static readonly IDadGameCommandExecutor NativeCommandExecutor = new DadNativeGameCommandExecutor();
    private static readonly TimeSpan NativeCommandAvailabilityTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NativeCommandRetryInterval = TimeSpan.FromMilliseconds(250);

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

    private sealed record PendingStep(
        StepKind Kind,
        string Payload,
        DateTime DeadlineUtc,
        DateTime NextAttemptUtc,
        int Attempt = 0);

    private readonly record struct StepResult(
        bool Completed,
        bool Retry,
        string SafeCode,
        string Summary,
        bool Failed);

    private static readonly Queue<PendingStep> PendingSteps = new();

    public static bool HasPendingWork => PendingSteps.Count > 0;
    public static DadCompletionActionStatus LastStatus { get; private set; } = new(
        "dad-completion-idle",
        "No completion action has run in this session.",
        DateTime.UtcNow,
        false);

    public static void Enqueue(Configuration configuration, IPluginLog log, DadRunRequest? request = null)
    {
        var actions = DadCompletionActionSnapshots.Resolve(request?.CompletionActions, configuration.CompletionActions);

        PendingSteps.Clear();
        var now = DateTime.UtcNow;
        void QueueStep(StepKind kind, string payload = "")
            => PendingSteps.Enqueue(new PendingStep(
                kind,
                payload,
                now + NativeCommandAvailabilityTimeout,
                now));

        if (actions.PlaySound)
            QueueStep(StepKind.Sound, Math.Clamp(actions.SoundEffectId, 1, 16).ToString());

        var utilities = actions.Utilities ?? new DadPostRunUtilities();
        if (utilities.OpenGearCoffers)
            QueueStep(StepKind.GearCoffers);
        if (utilities.RegisterTripleTriadCards)
            QueueStep(StepKind.TripleTriadRegister);
        if (utilities.SellTripleTriadCards)
            QueueStep(StepKind.TripleTriadSell);
        if (utilities.GrandCompanyHandInViaAutoRetainer)
        {
            if (DadCompletionCommandRules.TryNormalizeGrandCompanyHandInCommand(
                    utilities.GrandCompanyHandInCommand,
                    out var grandCompanyCommand,
                    out var grandCompanyFailure))
            {
                QueueStep(StepKind.GrandCompanyHandIn, grandCompanyCommand);
            }
            else
            {
                SetStatus("dad-completion-grand-company-command-invalid", grandCompanyFailure, failed: true);
                log.Warning("[dad] Rejected Grand Company hand-in completion command: {Failure}", grandCompanyFailure);
            }
        }

        if (actions.RunCommands && actions.Commands is { Count: > 0 })
        {
            foreach (var command in actions.Commands)
            {
                if (string.IsNullOrWhiteSpace(command))
                    continue;
                if (!DadCompletionCommandRules.TryNormalizeCustomCommand(command, out var normalized, out var failure))
                {
                    SetStatus("dad-completion-custom-command-invalid", failure, failed: true);
                    log.Warning("[dad] Rejected post-run completion command: {Failure}", failure);
                    continue;
                }
                QueueStep(StepKind.CustomCommand, normalized);
            }
        }

        // Legacy kill actions are preserved in config for compatibility, but self-contained
        // Dad no longer closes the game client or launches OS shutdown commands.
        if (actions.KillMode != DadCompletionKillMode.None)
            QueueStep(StepKind.KillAction, ((int)actions.KillMode).ToString());

        if (PendingSteps.Count > 0)
            log.Information("[dad] Queued {Count} post-run completion action(s).", PendingSteps.Count);
    }

    public static void Update(Configuration configuration, IPluginLog log)
    {
        if (PendingSteps.Count == 0)
            return;

        var now = DateTime.UtcNow;
        if (PendingSteps.Peek().NextAttemptUtc > now)
            return;
        var step = PendingSteps.Dequeue();
        try
        {
            var result = RunStep(step, configuration, log);
            if (result.Retry && now < step.DeadlineUtc)
            {
                PendingSteps.Enqueue(step with
                {
                    Attempt = step.Attempt + 1,
                    NextAttemptUtc = now + NativeCommandRetryInterval,
                });
                SetStatus(result.SafeCode, result.Summary, failed: false);
                return;
            }

            var failed = result.Failed || result.Retry;
            var safeCode = result.Retry ? "dad-completion-native-command-timeout" : result.SafeCode;
            var summary = result.Retry
                ? $"{result.Summary} The bounded UI-module wait expired after {step.Attempt + 1} attempt(s)."
                : result.Summary;
            SetStatus(safeCode, summary, failed);
            if (failed)
                log.Warning("[dad] Completion action failed ({SafeCode}): {Summary}", safeCode, summary);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Post-run completion step {StepKind} failed.", step.Kind);
            SetStatus("dad-completion-step-exception", $"Completion step {step.Kind} failed: {ex.Message}", failed: true);
        }
    }

    private static StepResult RunStep(PendingStep step, Configuration configuration, IPluginLog log)
    {
        switch (step.Kind)
        {
            case StepKind.Sound:
                unsafe
                {
                    FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.PlaySoundEffect(uint.Parse(step.Payload));
                }
                return Completed("dad-completion-sound-played", "Completion sound played.");
            case StepKind.GearCoffers:
                log.Information("[dad] Gear coffer post-run utility is enabled but no guarded item-use executor is active in this build.");
                return Completed("dad-completion-gear-coffers-unavailable", "Gear coffer utility is not active in this build.");
            case StepKind.TripleTriadRegister:
                log.Information("[dad] Triple Triad card registration utility is enabled but no guarded item-use executor is active in this build.");
                return Completed("dad-completion-triple-triad-register-unavailable", "Triple Triad registration is not active in this build.");
            case StepKind.TripleTriadSell:
                log.Information("[dad] Triple Triad card selling utility is enabled but no guarded sell executor is active in this build.");
                return Completed("dad-completion-triple-triad-sell-unavailable", "Triple Triad selling is not active in this build.");
            case StepKind.GrandCompanyHandIn:
                return RunGrandCompanyHandIn(step.Payload, log);
            case StepKind.CustomCommand:
                return RunCustomCommand(step.Payload, log);
            case StepKind.KillAction:
                if (int.TryParse(step.Payload, out var killMode) &&
                    Enum.IsDefined(typeof(DadCompletionKillMode), killMode))
                {
                    RunKillAction((DadCompletionKillMode)killMode, log);
                }
                return Completed("dad-completion-legacy-kill-action-noop", "Legacy completion kill action was ignored.");
            default:
                return Failed("dad-completion-step-unknown", "Unknown completion action step.");
        }
    }

    private static StepResult RunGrandCompanyHandIn(string command, IPluginLog log)
    {
        if (!DadCompletionCommandRules.TryNormalizeGrandCompanyHandInCommand(command, out var normalized, out var failure))
            return Failed("dad-completion-grand-company-command-invalid", failure);
        return RunNativeCommand(normalized, "Grand Company hand-in command", log);
    }

    private static StepResult RunCustomCommand(string command, IPluginLog log)
    {
        if (!DadCompletionCommandRules.TryNormalizeCustomCommand(command, out var normalized, out var failure))
            return Failed("dad-completion-custom-command-invalid", failure);
        try
        {
            if (Plugin.CommandManager.ProcessCommand(normalized))
            {
                log.Information("[dad] Submitted registered-plugin post-run completion command.");
                return Completed("dad-completion-custom-command-submitted", "Registered-plugin completion command submitted.");
            }
            return Failed(
                "dad-completion-custom-command-unregistered",
                "Completion command was not accepted by Dalamud's registered-plugin command manager.");
        }
        catch (Exception exception)
        {
            return Failed("dad-completion-custom-command-failed", $"Registered-plugin completion command failed: {exception.Message}");
        }
    }

    private static StepResult RunNativeCommand(string command, string actionLabel, IPluginLog log)
    {
        if (NativeCommandExecutor.TryExecute(command, out var failure))
        {
            log.Information("[dad] Submitted native {ActionLabel}.", actionLabel);
            return Completed("dad-completion-native-command-submitted", $"Submitted native {actionLabel}.");
        }

        var summary = string.IsNullOrWhiteSpace(failure) ? "Native chat submission failed." : failure;
        return string.Equals(failure, DadNativeGameCommandExecutor.UiModuleUnavailableError, StringComparison.Ordinal)
            ? new StepResult(false, true, "dad-completion-native-ui-wait", summary, false)
            : Failed("dad-completion-native-command-rejected", summary);
    }

    private static void RunKillAction(DadCompletionKillMode mode, IPluginLog log)
    {
        log.Warning("[dad] Completion kill action {Mode} is configured but disabled; no OS process action was taken.", mode);
    }

    private static void SetStatus(string safeCode, string summary, bool failed)
        => LastStatus = new(safeCode, summary, DateTime.UtcNow, failed);

    private static StepResult Completed(string safeCode, string summary)
        => new(true, false, safeCode, summary, false);

    private static StepResult Failed(string safeCode, string summary)
        => new(true, false, safeCode, summary, true);
}
