using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadTitleSurfaceRulesTests
{
    [Theory]
    [InlineData(true, false, false, false, (int)DadTitleSurface.TitleMenu)]
    [InlineData(false, true, false, false, (int)DadTitleSurface.TitleMovie)]
    [InlineData(false, false, true, false, (int)DadTitleSurface.ConnectingToDataCenter)]
    [InlineData(false, false, false, true, (int)DadTitleSurface.CharacterSelect)]
    [InlineData(false, false, false, false, (int)DadTitleSurface.None)]
    public void ClassifiesOneExclusiveKnownSurface(
        bool titleMenu,
        bool titleMovie,
        bool connecting,
        bool characterSelect,
        int expected)
    {
        var actual = DadTitleSurfaceRules.Classify(new DadTitleSurfaceSignals(
            titleMenu,
            titleMovie,
            connecting,
            characterSelect,
            NavigationSurfaceVisible: false,
            DialogSurfaceVisible: false));

        Assert.Equal((DadTitleSurface)expected, actual);
    }

    [Fact]
    public void MultipleKnownSurfacesAreAmbiguous()
    {
        var actual = DadTitleSurfaceRules.Classify(new DadTitleSurfaceSignals(
            TitleMenuVisible: true,
            TitleMovieVisible: false,
            ConnectingToDataCenterVisible: false,
            CharacterSelectVisible: true,
            NavigationSurfaceVisible: false,
            DialogSurfaceVisible: false));

        Assert.Equal(DadTitleSurface.Ambiguous, actual);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void AnyNavigationDialogOrUnknownSurfaceIsAmbiguous(
        bool navigation,
        bool dialog,
        bool unknown)
    {
        var actual = DadTitleSurfaceRules.Classify(new DadTitleSurfaceSignals(
            TitleMenuVisible: true,
            TitleMovieVisible: false,
            ConnectingToDataCenterVisible: false,
            CharacterSelectVisible: false,
            NavigationSurfaceVisible: navigation,
            DialogSurfaceVisible: dialog,
            UnknownSurfaceVisible: unknown));

        Assert.Equal(DadTitleSurface.Ambiguous, actual);
    }
}
