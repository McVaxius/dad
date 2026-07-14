using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using dad.Models;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace dad.Services;

public sealed unsafe class DadDutySupportAdsService
{
    private const string AdsInternalName = "ADS";
    private const string AdsDisplayName = "AI Duty Solver";
    private const string AdsOutsideCommand = "/ads outside";
    private const string AdsLeaveCommand = "/ads leave";
    private const string AdsStopCommand = "/ads stop";
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<string, string> patchConfiguration;

    public DadDutySupportAdsService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        patchConfiguration = pluginInterface.GetIpcSubscriber<string, string>("ADS.PatchConfigurationJson");
    }

    public string MissingAdsBlocker => "ADS is not loaded; cannot run Duty Support automation after queue";

    public bool IsAdsLoaded()
    {
        try
        {
            return Plugin.PluginInterface.InstalledPlugins.Any(plugin =>
                plugin.IsLoaded &&
                (string.Equals(plugin.InternalName, AdsInternalName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(plugin.Name, AdsDisplayName, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad][ADS] Failed to inspect installed plugins.");
            return false;
        }
    }

    public bool TryArmOutside(out string failureReason)
        => TrySendCommand(AdsOutsideCommand, "arm ADS outside ownership", out failureReason);

    public bool TryLeave(out string failureReason)
        => TrySendCommand(AdsLeaveCommand, "request ADS duty leave", out failureReason);

    public bool TryStop(out string failureReason)
        => TrySendCommand(AdsStopCommand, "stop ADS ownership", out failureReason);

    public bool TryPatchConfiguration(DadAdsLootMode? mode, out string failureReason)
    {
        // Installed-plugin metadata is diagnostic only. A successful invocation of the
        // required endpoint is the readiness proof, including during plugin-list lag.
        var installedMetadataReportsLoaded = IsAdsLoaded();

        try
        {
            var response = patchConfiguration.InvokeFunc(DadAdsConfigurationPatchRules.BuildPatchJson(mode));
            if (DadAdsConfigurationPatchRules.TryEvaluateReadiness(
                    installedMetadataReportsLoaded,
                    response,
                    invocationFailure: null,
                    out failureReason))
            {
                if (!installedMetadataReportsLoaded)
                {
                    log.Warning(
                        "[dad][ADS] ADS.PatchConfigurationJson succeeded while installed-plugin metadata reported ADS unloaded; accepting responsive IPC as readiness proof.");
                }
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad][ADS] Required configuration patch failed before queue mutation.");
            return DadAdsConfigurationPatchRules.TryEvaluateReadiness(
                installedMetadataReportsLoaded,
                responseJson: null,
                invocationFailure: ex.Message,
                out failureReason);
        }
    }

    public bool IsLeaveBlocked(out string blocker)
    {
        if (Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Plugin.Condition[ConditionFlag.WatchingCutscene])
        {
            blocker = "cutscene";
            return true;
        }

        if (Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Plugin.Condition[ConditionFlag.Occupied33] ||
            Plugin.Condition[ConditionFlag.Occupied39])
        {
            blocker = "occupied transition";
            return true;
        }

        blocker = string.Empty;
        return false;
    }

    public bool TryObserveLeaveEvidence(out string evidence)
    {
        if (Plugin.Condition[ConditionFlag.BetweenAreas])
        {
            evidence = "ConditionFlag.BetweenAreas";
            return true;
        }

        if (Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            evidence = "ConditionFlag.BetweenAreas51";
            return true;
        }

        if (IsAddonVisible("SelectYesno"))
        {
            evidence = "SelectYesno visible";
            return true;
        }

        evidence = string.Empty;
        return false;
    }

    private bool TrySendCommand(string command, string actionLabel, out string failureReason)
    {
        if (!IsAdsLoaded())
        {
            failureReason = MissingAdsBlocker;
            log.Warning("[dad][ADS] {ActionLabel} blocked: ADS is not loaded.", actionLabel);
            return false;
        }

        try
        {
            if (Plugin.CommandManager.ProcessCommand(command))
            {
                failureReason = string.Empty;
                log.Information("[dad][ADS] Sent {Command} to {ActionLabel}.", command, actionLabel);
                return true;
            }

            failureReason = $"ADS command failed: {command}";
            log.Warning("[dad][ADS] {ActionLabel} failed because command manager rejected {Command}.", actionLabel, command);
            return false;
        }
        catch (Exception ex)
        {
            failureReason = $"ADS command failed: {command} ({ex.Message})";
            log.Warning(ex, "[dad][ADS] {ActionLabel} threw while sending {Command}.", actionLabel, command);
            return false;
        }
    }

    private static bool IsAddonVisible(string addonName)
    {
        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(addonName);
            return addon != null && addon->IsVisible;
        }
        catch
        {
            return false;
        }
    }
}
