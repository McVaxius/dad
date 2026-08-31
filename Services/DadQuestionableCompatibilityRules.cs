namespace dad.Services;

internal enum DadQuestionableCosmeticAdapter
{
    Incompatible = 0,
    LegacyPluginInfo = 1,
    CurrentPluginProviderRequirement = 2,
}

internal static class DadQuestionableCosmeticAdapterSelector
{
    internal static DadQuestionableCosmeticAdapter Select(
        bool pluginProviderPresent,
        bool pluginRequirementPresent)
        => (pluginProviderPresent, pluginRequirementPresent) switch
        {
            (false, false) => DadQuestionableCosmeticAdapter.LegacyPluginInfo,
            (true, true) => DadQuestionableCosmeticAdapter.CurrentPluginProviderRequirement,
            _ => DadQuestionableCosmeticAdapter.Incompatible,
        };
}

internal static class DadQuestionableAutoDutyConfigResolver
{
    internal static string Resolve(string key, DadCombatRotationMode combatRotationMode)
        => key switch
        {
            "AutoManageRotationPluginState" or "AutoManageBossModAISettings" => combatRotationMode switch
            {
                DadCombatRotationMode.UseFrenRider or DadCombatRotationMode.ForceCommands => "True",
                DadCombatRotationMode.DoNothing => "False",
                _ => string.Empty,
            },
            _ => string.Empty,
        };
}

internal sealed class DadQuestionableRuntimeWarningGate
{
    private bool consumed;

    internal bool TryConsume()
    {
        if (consumed)
            return false;
        consumed = true;
        return true;
    }

    internal void Reset()
        => consumed = false;
}
