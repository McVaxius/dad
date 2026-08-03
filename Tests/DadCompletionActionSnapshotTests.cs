using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadCompletionActionSnapshotTests
{
    [Fact]
    public void ResolveUsesRequestSnapshotBeforeGlobalDefaults()
    {
        var global = new DadCompletionActions
        {
            PlaySound = true,
            SoundEffectId = 3,
            RunCommands = true,
            Commands = ["/global"],
        };
        var snapshot = new DadCompletionActions
        {
            PlaySound = false,
            SoundEffectId = 7,
            RunCommands = true,
            Commands = ["/preset"],
        };

        var resolved = DadCompletionActionSnapshots.Resolve(snapshot, global);

        Assert.False(resolved.PlaySound);
        Assert.Equal(7, resolved.SoundEffectId);
        Assert.Equal(["/preset"], resolved.Commands);
    }

    [Fact]
    public void ResolveFallsBackToGlobalDefaultsForExistingPreset()
    {
        var global = new DadCompletionActions
        {
            PlaySound = true,
            SoundEffectId = 5,
            Utilities = new DadPostRunUtilities { OpenGearCoffers = true },
        };

        var resolved = DadCompletionActionSnapshots.Resolve(snapshot: null, fallback: global);

        Assert.True(resolved.PlaySound);
        Assert.Equal(5, resolved.SoundEffectId);
        Assert.True(resolved.Utilities.OpenGearCoffers);
    }

    [Fact]
    public void ResolveReturnsIndependentClone()
    {
        var snapshot = new DadCompletionActions
        {
            RunCommands = true,
            Commands = ["/one"],
            Utilities = new DadPostRunUtilities { SellTripleTriadCards = true },
        };

        var resolved = DadCompletionActionSnapshots.Resolve(snapshot, fallback: null);
        resolved.Commands.Add("/two");
        resolved.Utilities.SellTripleTriadCards = false;

        Assert.Equal(["/one"], snapshot.Commands);
        Assert.True(snapshot.Utilities.SellTripleTriadCards);
    }

    [Fact]
    public void ResolveNullInputsReturnsDisabledActions()
    {
        var resolved = DadCompletionActionSnapshots.Resolve(snapshot: null, fallback: null);

        Assert.False(resolved.PlaySound);
        Assert.False(resolved.RunCommands);
        Assert.Empty(resolved.Commands);
        Assert.Equal(DadCompletionKillMode.None, resolved.KillMode);
    }

    [Theory]
    [InlineData("/vmx resume")]
    [InlineData("/dad status")]
    public void CustomCompletionCommandsAcceptSingleRegisteredPluginCommandShape(string command)
    {
        Assert.True(DadCompletionCommandRules.TryNormalizeCustomCommand(command, out var normalized, out var reason));
        Assert.Equal(command, normalized);
        Assert.Empty(reason);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("/dad status\r")]
    [InlineData("/dad\tstatus")]
    [InlineData("/dad\0status")]
    public void CustomCompletionCommandsRejectNonSlashOrControlCharacters(string command)
        => Assert.False(DadCompletionCommandRules.TryNormalizeCustomCommand(command, out _, out _));

    [Theory]
    [InlineData("/ays gc", "/ays gc")]
    [InlineData("  /AYS gc  ", "/AYS gc")]
    public void GrandCompanyCommandsRequireExactAysRoot(string command, string expected)
    {
        Assert.True(DadCompletionCommandRules.TryNormalizeGrandCompanyHandInCommand(
            command,
            out var normalized,
            out var reason));
        Assert.Equal(expected, normalized);
        Assert.Empty(reason);
    }

    [Theory]
    [InlineData("/echo /ays gc")]
    [InlineData("/aysomething gc")]
    [InlineData("/ays gc\n/dad stop")]
    public void GrandCompanyCommandsRejectOtherRootsAndControls(string command)
        => Assert.False(DadCompletionCommandRules.TryNormalizeGrandCompanyHandInCommand(command, out _, out _));

    [Fact]
    public void LegacyCompletionKillEnumValuesRemainDeserializable()
    {
        Assert.Equal(1, (int)DadCompletionKillMode.CloseGameClient);
        Assert.Equal(2, (int)DadCompletionKillMode.ShutDownPc);
    }
}
