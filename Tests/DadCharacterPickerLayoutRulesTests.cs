using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadCharacterPickerLayoutRulesTests
{
    [Theory]
    [InlineData(1920f, 1080f, 260f, 420f, 260f, 560f, 280f)]
    [InlineData(360f, 480f, 220f, 296f, 220f, 416f, 144f)]
    [InlineData(5120f, 2160f, 300f, 420f, 300f, 560f, 280f)]
    [InlineData(1920f, 1080f, 5000f, 560f, 560f, 560f, 280f)]
    public void ResolveKeepsNormalNarrowUltrawideAndOversizedInputsBounded(
        float viewportWidth,
        float viewportHeight,
        float tableCellWidth,
        float expectedPopupWidth,
        float expectedComboWidth,
        float expectedPopupMaxHeight,
        float expectedResultsPaneHeight)
    {
        var layout = DadCharacterPickerLayoutRules.Resolve(
            viewportWidth,
            viewportHeight,
            tableCellWidth);

        Assert.Equal(expectedPopupWidth, layout.PopupWidth);
        Assert.Equal(expectedComboWidth, layout.ComboWidth);
        Assert.Equal(expectedPopupMaxHeight, layout.PopupMaxHeight);
        Assert.Equal(expectedResultsPaneHeight, layout.ResultsPaneHeight);
        AssertDimensionsAreFiniteAndPositive(layout);
        Assert.True(layout.PopupWidth <= MathF.Min(
            DadCharacterPickerLayoutRules.MaximumPopupWidth,
            viewportWidth - DadCharacterPickerLayoutRules.ViewportMargin));
        Assert.True(layout.PopupMaxHeight <= MathF.Min(
            DadCharacterPickerLayoutRules.MaximumPopupHeight,
            viewportHeight - DadCharacterPickerLayoutRules.ViewportMargin));
        Assert.True(layout.ComboWidth <= layout.PopupWidth);
        Assert.InRange(
            layout.ResultsPaneHeight,
            DadCharacterPickerLayoutRules.MinimumResultsPaneHeight,
            DadCharacterPickerLayoutRules.MaximumResultsPaneHeight);
    }

    [Fact]
    public void ResolveSanitizesNonFiniteInputs()
    {
        var layout = DadCharacterPickerLayoutRules.Resolve(
            float.NaN,
            float.PositiveInfinity,
            float.NegativeInfinity);

        AssertDimensionsAreFiniteAndPositive(layout);
        Assert.Equal(420f, layout.PopupWidth);
        Assert.Equal(420f, layout.ComboWidth);
        Assert.Equal(560f, layout.PopupMaxHeight);
    }

    private static void AssertDimensionsAreFiniteAndPositive(DadCharacterPickerLayout layout)
    {
        Assert.True(float.IsFinite(layout.PopupWidth) && layout.PopupWidth > 0f);
        Assert.True(float.IsFinite(layout.ComboWidth) && layout.ComboWidth > 0f);
        Assert.True(float.IsFinite(layout.PopupMaxHeight) && layout.PopupMaxHeight > 0f);
        Assert.True(float.IsFinite(layout.ResultsPaneHeight) && layout.ResultsPaneHeight > 0f);
    }
}
