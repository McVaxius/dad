using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadQuestionableCompatibilityTests
{
    [Theory]
    [InlineData("AutoManageRotationPluginState", DadCombatRotationMode.UseFrenRider, "True")]
    [InlineData("AutoManageRotationPluginState", DadCombatRotationMode.ForceCommands, "True")]
    [InlineData("AutoManageRotationPluginState", DadCombatRotationMode.DoNothing, "False")]
    [InlineData("AutoManageBossModAISettings", DadCombatRotationMode.UseFrenRider, "True")]
    [InlineData("AutoManageBossModAISettings", DadCombatRotationMode.ForceCommands, "True")]
    [InlineData("AutoManageBossModAISettings", DadCombatRotationMode.DoNothing, "False")]
    public void AutoDutyConfigDeclaresDadCombatOwnership(
        string key,
        DadCombatRotationMode combatRotationMode,
        string expected)
        => Assert.Equal(expected, DadQuestionableAutoDutyConfigResolver.Resolve(key, combatRotationMode));

    [Theory]
    [InlineData("UnknownKey")]
    [InlineData("automanagerotationpluginstate")]
    public void AutoDutyConfigReturnsEmptyForUnknownKeys(string key)
        => Assert.Equal(string.Empty, DadQuestionableAutoDutyConfigResolver.Resolve(
            key,
            DadCombatRotationMode.UseFrenRider));

}
