using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public enum DadFrenRiderPluginState
{
    NotInstalled,
    InstalledNotLoaded,
    Loaded,
}

public sealed class DadCombatRotationService(
    Configuration configuration,
    IDalamudPluginInterface pluginInterface,
    IPluginLog log)
{
    private const string FrenRiderInternalName = "FrenRider";
    private const string FrenRiderDisplayName = "Fren Rider";

    public const string FrenRiderEnableCommand = "/fr on";
    public const string FrenRiderConfigureAndEnableChannel = "FrenRider.Dad.ConfigureAndEnable";
    public const string BossModRotationCommand = "/bmrai on";
    public const string AutoRotationCommand = "/rotation auto";

    private static readonly string[] BootstrapCommands =
    [
        BossModRotationCommand,
        AutoRotationCommand,
    ];

    private readonly DadFrenRiderEntryEnableGate frenRiderEntryEnableGate = new();
    private readonly ICallGateSubscriber<string, bool> frenRiderConfigureAndEnable =
        pluginInterface.GetIpcSubscriber<string, bool>(FrenRiderConfigureAndEnableChannel);

    public DadCombatRotationMode CombatRotationMode => configuration.CombatRotationMode;

    public string MissingFrenRiderBlocker => "FrenRider is not loaded; Dad cannot enable FrenRider after duty entry.";

    public bool IsFrenRiderLoaded()
        => GetFrenRiderPluginState() == DadFrenRiderPluginState.Loaded;

    public DadFrenRiderPluginState GetFrenRiderPluginState()
    {
        try
        {
            var installed = false;
            foreach (var plugin in Plugin.PluginInterface.InstalledPlugins)
            {
                if (!string.Equals(plugin.InternalName, FrenRiderInternalName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(plugin.Name, FrenRiderDisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (plugin.IsLoaded)
                    return DadFrenRiderPluginState.Loaded;

                installed = true;
            }

            return installed
                ? DadFrenRiderPluginState.InstalledNotLoaded
                : DadFrenRiderPluginState.NotInstalled;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad][CombatRotation] Failed to inspect FrenRider plugin availability.");
            return DadFrenRiderPluginState.NotInstalled;
        }
    }

    internal DadFrenRiderEntryEnableStatus TryEnableFrenRiderAfterDutyEntry(
        string runId,
        DadModuleId moduleId,
        DateTime now,
        out string summary)
    {
        var result = frenRiderEntryEnableGate.Apply(
            runId,
            FormatDutyOperation(moduleId),
            FrenRiderEnableCommand,
            now,
            () => SendCommand(FrenRiderEnableCommand),
            out summary);

        switch (result)
        {
            case DadFrenRiderEntryEnableStatus.Sent:
                log.Information("[dad][CombatRotation] {Summary}", summary);
                break;
            case DadFrenRiderEntryEnableStatus.PendingRetry:
                log.Debug("[dad][CombatRotation] {Summary}", summary);
                break;
            case DadFrenRiderEntryEnableStatus.Failed:
                log.Warning("[dad][CombatRotation] {Summary}", summary);
                break;
        }

        return result;
    }

    internal DadFrenRiderEntryEnableStatus TryEnableFrenRiderAfterGroupReady(
        string runId,
        DadModuleId moduleId,
        DateTime now,
        out string summary)
    {
        var result = frenRiderEntryEnableGate.ApplyAtBoundary(
            runId,
            FormatDutyOperation(moduleId),
            FrenRiderEnableCommand,
            "after exact group formation",
            now,
            () => SendCommand(FrenRiderEnableCommand),
            out summary);

        switch (result)
        {
            case DadFrenRiderEntryEnableStatus.Sent:
                log.Information("[dad][CombatRotation] {Summary}", summary);
                break;
            case DadFrenRiderEntryEnableStatus.PendingRetry:
                log.Debug("[dad][CombatRotation] {Summary}", summary);
                break;
            case DadFrenRiderEntryEnableStatus.Failed:
                log.Warning("[dad][CombatRotation] {Summary}", summary);
                break;
        }

        return result;
    }

    internal DadFrenRiderCommandResult TryConfigureAndEnableParticipant(string nameAtWorld)
    {
        try
        {
            if (frenRiderConfigureAndEnable.InvokeFunc(nameAtWorld))
                return DadFrenRiderCommandResult.Success();

            var failure = $"FrenRider rejected {FrenRiderConfigureAndEnableChannel} for '{nameAtWorld}'";
            log.Warning("[dad][CombatRotation] {Failure}.", failure);
            return DadFrenRiderCommandResult.Failure(failure);
        }
        catch (Exception ex)
        {
            var failure = $"{FrenRiderConfigureAndEnableChannel} failed for '{nameAtWorld}' ({ex.GetType().Name}: {ex.Message})";
            log.Warning(ex, "[dad][CombatRotation] {Failure}.", failure);
            return DadFrenRiderCommandResult.Failure(failure);
        }
    }

    public bool TryApplyDutySupportEntryMode(
        DadCombatRotationMode mode,
        string runId,
        out string summary,
        out bool shouldFailRun)
    {
        shouldFailRun = false;

        switch (mode)
        {
            case DadCombatRotationMode.UseFrenRider:
                var result = TryEnableFrenRiderAfterDutyEntry(
                    runId,
                    DadModuleId.DutySupport,
                    DateTime.UtcNow,
                    out summary);
                shouldFailRun = result == DadFrenRiderEntryEnableStatus.Failed;
                return result is DadFrenRiderEntryEnableStatus.Sent or DadFrenRiderEntryEnableStatus.AlreadySent;
            case DadCombatRotationMode.ForceCommands:
                return TryBootstrapForceCommands(out summary);
            case DadCombatRotationMode.DoNothing:
                summary = "Do Nothing mode selected; Dad sent no FrenRider, ADS, or rotation command after duty entry.";
                log.Information("[dad][CombatRotation] {Summary}", summary);
                return true;
            default:
                summary = $"Unknown combat rotation mode {mode}; Dad sent no entry command.";
                log.Warning("[dad][CombatRotation] {Summary}", summary);
                return true;
        }
    }

    private bool TryBootstrapForceCommands(out string summary)
    {
        var failedCommands = new List<string>();
        foreach (var command in BootstrapCommands)
        {
            if (!TrySendCommand(command))
                failedCommands.Add(command);
        }

        if (failedCommands.Count == 0)
        {
            summary = "Force Commands mode sent /bmrai on and /rotation auto after Duty Support entry.";
            log.Information(
                "[dad][CombatRotation] Sent Duty Support rotation bootstrap commands: {Commands}.",
                string.Join(", ", BootstrapCommands));
            return true;
        }

        summary = $"Force Commands mode attempted rotation bootstrap; failed command(s): {string.Join(", ", failedCommands)}.";
        log.Warning("[dad][CombatRotation] {Summary}", summary);
        return false;
    }

    private bool TrySendCommand(string command)
        => SendCommand(command).Succeeded;

    private DadFrenRiderCommandResult SendCommand(string command)
    {
        try
        {
            if (Plugin.CommandManager.ProcessCommand(command))
                return DadFrenRiderCommandResult.Success();

            var failure = $"Command manager rejected {command}";
            log.Warning("[dad][CombatRotation] {Failure}.", failure);
            return DadFrenRiderCommandResult.Failure(failure);
        }
        catch (Exception ex)
        {
            var failure = $"Command threw while processing {command}: {ex.Message}";
            log.Warning(ex, "[dad][CombatRotation] {Failure}.", failure);
            return DadFrenRiderCommandResult.Failure(failure);
        }
    }

    private static string FormatDutyOperation(DadModuleId moduleId)
        => moduleId switch
        {
            DadModuleId.Duty => "a duty operation",
            DadModuleId.Msq => "an MSQ duty operation",
            DadModuleId.DutySupport => "a Duty Support operation",
            DadModuleId.Trust => "a Trust operation",
            DadModuleId.PremadeDuty => "a premade duty operation",
            DadModuleId.DailyMsq => "a Daily Roulette operation",
            DadModuleId.Blunderville => "a Blunderville operation",
            DadModuleId.Mogtome => "a MOGTOME duty operation",
            DadModuleId.Commendation => "a commendation duty operation",
            DadModuleId.Astrope => "an Astrope duty operation",
            DadModuleId.CustomDuty => "a custom duty operation",
            _ => "a Dad duty operation",
        };
}
