using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace dad.Windows;

internal enum DadUiTone
{
    Neutral,
    Accent,
    Info,
    Success,
    Warning,
    Danger,
}

/// <summary>
/// Shared presentation helpers for DAD windows. This class deliberately owns no
/// configuration or runtime behavior; it only keeps hierarchy, spacing, and state
/// colors consistent across the operator surfaces.
/// </summary>
internal static class DadUi
{
    public static readonly Vector4 Accent = new(0.92f, 0.67f, 0.25f, 1f);
    public static readonly Vector4 Info = new(0.38f, 0.72f, 1f, 1f);
    public static readonly Vector4 Success = new(0.36f, 0.86f, 0.48f, 1f);
    public static readonly Vector4 Warning = new(1f, 0.72f, 0.25f, 1f);
    public static readonly Vector4 Danger = new(1f, 0.38f, 0.36f, 1f);
    public static readonly Vector4 Muted = new(0.62f, 0.64f, 0.68f, 1f);
    public static readonly Vector4 Panel = new(0.08f, 0.085f, 0.105f, 0.88f);
    public static readonly Vector4 Border = new(0.92f, 0.67f, 0.25f, 0.25f);

    public static void Heading(string title, string subtitle)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        if (!string.IsNullOrWhiteSpace(subtitle))
            MutedWrapped(subtitle);
    }

    public static void Section(string title, string? subtitle = null)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        if (!string.IsNullOrWhiteSpace(subtitle))
            MutedWrapped(subtitle);
    }

    public static bool BeginCard(string id, float minimumHeight = 0f)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(12f, 10f));
        ImGui.PushStyleColor(ImGuiCol.TableBorderStrong, Border);
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight, Border);
        var open = ImGui.BeginTable(
            id,
            1,
            ImGuiTableFlags.BordersOuter |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.SizingStretchSame |
            ImGuiTableFlags.NoSavedSettings);
        if (!open)
        {
            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar();
            return false;
        }

        ImGui.TableNextRow(MathF.Max(0f, minimumHeight));
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(Panel));
        ImGui.TableNextColumn();
        return true;
    }

    public static void EndCard()
    {
        ImGui.EndTable();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
    }

    public static void Badge(string text, DadUiTone tone = DadUiTone.Neutral)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ToneColor(tone));
        ImGui.TextUnformatted($"● {text}");
        ImGui.PopStyleColor();
    }

    public static bool Button(string label, DadUiTone tone = DadUiTone.Neutral, Vector2 size = default)
    {
        if (tone == DadUiTone.Neutral)
            return ImGui.Button(label, size);

        var color = ToneColor(tone);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleColor(ImGuiCol.Button, WithAlpha(color, tone == DadUiTone.Danger ? 0.52f : 0.34f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, WithAlpha(color, tone == DadUiTone.Danger ? 0.72f : 0.54f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, WithAlpha(color, 0.82f));
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();
        return clicked;
    }

    public static void KeyValue(string label, string value, float preferredLabelWidth = 150f)
    {
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var labelWidth = MathF.Min(
            MathF.Max(84f, preferredLabelWidth),
            MathF.Max(84f, availableWidth * 0.36f));

        ImGui.TextDisabled(label);
        if (availableWidth > labelWidth + 120f)
        {
            ImGui.SameLine(labelWidth);
            ImGui.TextWrapped(value);
        }
        else
        {
            ImGui.Indent();
            ImGui.TextWrapped(value);
            ImGui.Unindent();
        }
    }

    public static Vector4 ToneColor(DadUiTone tone)
        => tone switch
        {
            DadUiTone.Accent => Accent,
            DadUiTone.Info => Info,
            DadUiTone.Success => Success,
            DadUiTone.Warning => Warning,
            DadUiTone.Danger => Danger,
            _ => Muted,
        };

    public static Vector4 WithAlpha(Vector4 color, float alpha)
        => new(color.X, color.Y, color.Z, alpha);

    private static void MutedWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
