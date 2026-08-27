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
