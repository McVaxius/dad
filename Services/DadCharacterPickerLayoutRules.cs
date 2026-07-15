namespace dad.Services;

internal readonly record struct DadCharacterPickerLayout(
    float PopupWidth,
    float ComboWidth,
    float PopupMaxHeight,
    float ResultsPaneHeight);

/// <summary>
/// Resolves the character-picker dimensions without depending on Dalamud or ImGui.
/// The viewport cap wins over the preferred and minimum widths on narrow displays.
/// </summary>
internal static class DadCharacterPickerLayoutRules
{
    internal const float PreferredPopupWidth = 420f;
    internal const float MinimumPopupWidth = 320f;
    internal const float MaximumPopupWidth = 560f;
    internal const float MaximumPopupHeight = 560f;
    internal const float ViewportMargin = 64f;
    internal const float MinimumResultsPaneHeight = 140f;
    internal const float MaximumResultsPaneHeight = 280f;

    private const float MinimumPositiveDimension = 1f;

    internal static DadCharacterPickerLayout Resolve(
        float viewportWidth,
        float viewportHeight,
        float tableCellWidth)
    {
        var safeViewportWidth = PositiveFiniteOr(
            viewportWidth,
            PreferredPopupWidth + ViewportMargin);
        var safeViewportHeight = PositiveFiniteOr(
            viewportHeight,
            MaximumPopupHeight + ViewportMargin);
        var safeTableCellWidth = PositiveFiniteOr(tableCellWidth, PreferredPopupWidth);

        var viewportWidthCap = MathF.Max(
            MinimumPositiveDimension,
            safeViewportWidth - ViewportMargin);
        var popupWidthCap = MathF.Min(MaximumPopupWidth, viewportWidthCap);
        var popupWidthFloor = MathF.Min(MinimumPopupWidth, popupWidthCap);
        var requestedPopupWidth = MathF.Max(PreferredPopupWidth, safeTableCellWidth);
        var popupWidth = Math.Clamp(requestedPopupWidth, popupWidthFloor, popupWidthCap);
        var comboWidth = Math.Clamp(
            safeTableCellWidth,
            MinimumPositiveDimension,
            popupWidth);

        var popupMaxHeight = MathF.Min(
            MaximumPopupHeight,
            MathF.Max(MinimumPositiveDimension, safeViewportHeight - ViewportMargin));
        var resultsPaneHeight = Math.Clamp(
            safeViewportHeight * 0.30f,
            MinimumResultsPaneHeight,
            MaximumResultsPaneHeight);

        return new DadCharacterPickerLayout(
            popupWidth,
            comboWidth,
            popupMaxHeight,
            resultsPaneHeight);
    }

    private static float PositiveFiniteOr(float value, float fallback)
        => float.IsFinite(value) && value > 0f ? value : fallback;
}
