using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public enum DadFrenRiderPluginState
{
    NotInstalled,
    InstalledNotLoaded,
    Loaded,
}

public sealed class DadCombatRotationService(Configuration configuration, IPluginLog log)
{
    private const string FrenRiderInternalName = "FrenRider";
    private const string FrenRiderDisplayName = "Fren Rider";

    public const string FrenRiderEnableCommand = "/fr on";
    public const string BossModRotationCommand = "/bmrai on";
    public const string AutoRotationCommand = "/rotation auto";

    private static readonly string[] BootstrapCommands =
    [
        BossModRotationCommand,
        AutoRotationCommand,
    ];

    public DadCombatRotationMode CombatRotationMode => configuration.CombatRotationMode;

    public string MissingFrenRiderBlocker => "FrenRider is not loaded; Dad cannot enable FrenRider before queueing a duty operation.";

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

    public bool TryPrepareFrenRiderForDutyOperation(DadModuleId moduleId, out string summary)
    {
        if (!IsFrenRiderLoaded())
        {
            summary = MissingFrenRiderBlocker;
            log.Warning("[dad][CombatRotation] {Summary}", summary);
            return false;
        }

        if (!TrySendCommand(FrenRiderEnableCommand))
        {
            summary = $"FrenRider enable command failed before Dad queue start: {FrenRiderEnableCommand}.";
            log.Warning("[dad][CombatRotation] {Summary}", summary);
            return false;
        }

        summary = $"Use FrenRider mode sent {FrenRiderEnableCommand} before Dad started {FormatDutyOperation(moduleId)}.";
        log.Information("[dad][CombatRotation] {Summary}", summary);
        return true;
    }

    public bool TryApplyDutySupportEntryMode(
        DadCombatRotationMode mode,
        out string summary,
        out bool shouldFailRun)
    {
        shouldFailRun = false;

        switch (mode)
        {
            case DadCombatRotationMode.UseFrenRider:
                summary = "Use FrenRider mode already requested FrenRider before queue; Dad sent no Duty Support entry command.";
                log.Information("[dad][CombatRotation] {Summary}", summary);
                return true;
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
    {
        try
        {
            if (Plugin.CommandManager.ProcessCommand(command))
                return true;

            log.Warning("[dad][CombatRotation] Command manager rejected {Command}.", command);
            return false;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad][CombatRotation] Command threw during Duty Support bootstrap: {Command}.", command);
            return false;
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
            DadModuleId.DailyMsq => "a Daily MSQ duty operation",
            DadModuleId.Blunderville => "a Blunderville operation",
            DadModuleId.Mogtome => "a MOGTOME duty operation",
            DadModuleId.Commendation => "a commendation duty operation",
            DadModuleId.Astrope => "an Astrope duty operation",
            DadModuleId.CustomDuty => "a custom duty operation",
            _ => "a Dad duty operation",
        };
}
