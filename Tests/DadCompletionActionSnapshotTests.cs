using dad.Models;
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
}
