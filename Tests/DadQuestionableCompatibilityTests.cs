using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadQuestionableCompatibilityTests
{
    [Theory]
    [InlineData(false, false, (int)DadQuestionableCosmeticAdapter.LegacyPluginInfo)]
    [InlineData(true, true, (int)DadQuestionableCosmeticAdapter.CurrentPluginProviderRequirement)]
    [InlineData(true, false, (int)DadQuestionableCosmeticAdapter.Incompatible)]
    [InlineData(false, true, (int)DadQuestionableCosmeticAdapter.Incompatible)]
    public void CosmeticAdapterSelectionRequiresACompleteModel(
        bool provider,
        bool requirement,
        int expected)
        => Assert.Equal(expected, (int)DadQuestionableCosmeticAdapterSelector.Select(provider, requirement));

    [Fact]
    public void RuntimeWarningIsShownOncePerCharacterLoadAndResettable()
    {
        var gate = new DadQuestionableRuntimeWarningGate();

        Assert.True(gate.TryConsume());
        Assert.False(gate.TryConsume());
        gate.Reset();
        Assert.True(gate.TryConsume());
    }

    [Fact]
    public void RuntimeOwnershipAndBothCosmeticAdaptersRemainExplicit()
    {
        var source = ReadRepositorySource("Services", "DadQuestionableReflectionBridge.cs");
        foreach (var field in new[] { "_contentHasPath", "_getConfig", "_setConfig", "_run", "_isStopped", "_stop" })
            Assert.Contains($"\"{field}\"", source, StringComparison.Ordinal);
        Assert.Contains("PluginInfoTypeName", source, StringComparison.Ordinal);
        Assert.Contains("PluginProviderTypeName", source, StringComparison.Ordinal);
        Assert.Contains("PluginRequirementTypeName", source, StringComparison.Ordinal);
        Assert.Contains("_recommendedPlugins", source, StringComparison.Ordinal);
    }

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

    [Fact]
    public void CosmeticFailureCannotConsumeTheRuntimeWarning()
    {
        var source = ReadRepositorySource("Services", "DadQuestionableReflectionBridge.cs");
        var cosmetic = Slice(source, "private void MaintainCosmeticPatch", "private IExposedPlugin? FindLoadedQuestionable");
        var runtime = Slice(source, "private void MaintainRuntimeBridge", "private void MaintainCosmeticPatch");

        Assert.DoesNotContain("runtimeWarningGate", cosmetic, StringComparison.Ordinal);
        Assert.Contains("runtimeWarningGate.TryConsume()", runtime, StringComparison.Ordinal);
        Assert.Contains("without affecting runtime routing", cosmetic, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source[start..end];
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dad.csproj")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the DAD repository root.");
        return File.ReadAllText(Path.Combine([root, .. pathParts]));
    }
}
