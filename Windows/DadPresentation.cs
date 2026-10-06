using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;

namespace dad.Windows;

internal enum UiFontRole { Body, BodyStrong, Title, Caption, Small }

internal static class DadPresentation
{
    // Approved DAD-review-v2 / DAD-compact-review-v1: native chrome; logical pixel measurements.
    internal static bool Compact { get; set; }
    internal static float HeaderHeight => Compact ? 66 : 72;
    internal static float CardHeight => Compact ? 138 : 158;
    internal static float GuideHeight => Compact ? 54 : 60;
    internal static float Gap => Compact ? 10 : 14;
    internal const uint ReferenceAccent = 0xFFAF74;
    internal static readonly float[] FontSizes = [16, 16, 38, 14, 12];
    internal static readonly string[] FontFiles = ["segoeui.ttf", "seguisb.ttf", "segoeuib.ttf", "segoeui.ttf", "segoeui.ttf"];
    internal static float AtlasHeight(UiFontRole role) => FontSizes[(int)role] * 4 / 3;
    internal readonly struct FontScaleScope : IDisposable
    {
        private readonly float previous;
        internal FontScaleScope(float scale)
        {
            previous = ImGuiP.GetCurrentWindow().FontWindowScale;
            ImGui.SetWindowFontScale(previous * scale);
        }
        public void Dispose() => ImGui.SetWindowFontScale(previous);
    }
    internal static Vector4 Rgb(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
    internal static readonly Vector4 Ready = Rgb(0x7DCF6E), Pending = Rgb(0xE8B86C), Error = Rgb(0xEE7788);
    internal static MaterialControlMetrics Controls(float height, float icon = 20)
    {
        var s = MaterialTheme.Metrics.Scale;
        return new() { Height = height * s, Padding = new(12 * s, Math.Max(0, (height * s - ImGui.GetTextLineHeight()) * .5f)),
            Gap = 8 * s, IconSize = icon * s, Rounding = 4 * s, ItemSpacing = new(10 * s, 4 * s), CellPadding = new(16 * s, 8 * s) };
    }
    internal static MaterialTheme Theme(uint accent)
    {
        accent &= 0xFFFFFF;
        var reference = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new Vector3(Rgb(ReferenceAccent).X, Rgb(ReferenceAccent).Y, Rgb(ReferenceAccent).Z)));
        var selected = Rgb(accent);
        var seed = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new(selected.X, selected.Y, selected.Z)));
        var hueShift = seed.Y < .001f ? 0 : seed.Z - reference.Z;
        var chromaScale = seed.Y < .001f ? 0 : seed.Y / reference.Y;
        Vector4 Relative(uint rgb)
        {
            var color = Rgb(rgb);
            if (accent == ReferenceAccent) return color;
            var lch = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new(color.X, color.Y, color.Z)));
            return new(MaterialColor.GamutMap(lch.X, lch.Y * chromaScale, lch.Z + hueShift), 1);
        }
        var palette = new OklchPaletteGenerator().Generate(new(selected.X, selected.Y, selected.Z));
        var background = Relative(0x111F2B);
        var foreground = Relative(0xEBF1FB);
        var primary = Relative(ReferenceAccent);
        var colors = new MaterialColorScheme(palette)
        {
            Background = background, OnBackground = foreground, Surface = Relative(0x152837), OnSurface = foreground,
            SurfaceContainerLowest = Relative(0x11202D), SurfaceContainerLow = Relative(0x152837),
            SurfaceContainer = Relative(0x192E3E), SurfaceContainerHigh = Relative(0x233D53), SurfaceContainerHighest = Relative(0x2C4960),
            SurfaceVariant = Relative(0x284357), OnSurfaceVariant = Relative(0xBFCDE0),
            Outline = Relative(0x40607A), OutlineVariant = Relative(0x284357),
            Primary = primary, OnPrimary = MaterialColor.Contrast(primary, background) >= MaterialColor.Contrast(primary, foreground) ? background : foreground,
            PrimaryContainer = Relative(0x49372D), OnPrimaryContainer = foreground,
            Secondary = Relative(0xFFBC8E), OnSecondary = background, SecondaryContainer = Relative(0x233D53), OnSecondaryContainer = foreground,
            Tertiary = Relative(0xAAC8F1), OnTertiary = background, TertiaryContainer = Relative(0x223D56), OnTertiaryContainer = foreground,
            InverseSurface = foreground, InverseOnSurface = background, InversePrimary = Relative(0x96592D),
        };
        return new(colors) { SurfaceOpacity = 1 };
    }
    internal static void Surface(Vector2 min, Vector2 max, bool raised = false)
    {
        var c = MaterialTheme.Current.Colors;
        MaterialCanvas.Surface(min, max, raised ? c.SurfaceContainerHigh : c.Surface, c.Background, 4 * MaterialTheme.Metrics.Scale);
        Dalamud.Bindings.ImGui.ImGui.GetWindowDrawList().AddRect(min, max, MaterialCanvas.Color(c.OutlineVariant), 4 * MaterialTheme.Metrics.Scale);
    }
}
