namespace dad.Models;

internal enum DadTitleSurface
{
    None = 0,
    TitleMenu = 1,
    TitleMovie = 2,
    ConnectingToDataCenter = 3,
    CharacterSelect = 4,
    Ambiguous = 5,
}

internal readonly record struct DadTitleSurfaceSignals(
    bool TitleMenuVisible,
    bool TitleMovieVisible,
    bool ConnectingToDataCenterVisible,
    bool CharacterSelectVisible,
    bool NavigationSurfaceVisible,
    bool DialogSurfaceVisible,
    bool UnknownSurfaceVisible = false);

internal static class DadTitleSurfaceRules
{
    public static DadTitleSurface Classify(DadTitleSurfaceSignals signals)
    {
        if (signals.NavigationSurfaceVisible ||
            signals.DialogSurfaceVisible ||
            signals.UnknownSurfaceVisible)
        {
            return DadTitleSurface.Ambiguous;
        }

        var count = (signals.TitleMenuVisible ? 1 : 0) +
                    (signals.TitleMovieVisible ? 1 : 0) +
                    (signals.ConnectingToDataCenterVisible ? 1 : 0) +
                    (signals.CharacterSelectVisible ? 1 : 0);
        if (count == 0)
            return DadTitleSurface.None;
        if (count != 1)
            return DadTitleSurface.Ambiguous;
        if (signals.TitleMenuVisible)
            return DadTitleSurface.TitleMenu;
        if (signals.TitleMovieVisible)
            return DadTitleSurface.TitleMovie;
        if (signals.ConnectingToDataCenterVisible)
            return DadTitleSurface.ConnectingToDataCenter;
        return DadTitleSurface.CharacterSelect;
    }
}
