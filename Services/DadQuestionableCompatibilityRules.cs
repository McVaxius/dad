namespace dad.Services;

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
