using AethertekUI;
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
    public static Vector4 Accent => MaterialTheme.Current.Colors.Primary;
    public static readonly Vector4 Info = new(0.38f, 0.72f, 1f, 1f);
    public static readonly Vector4 Success = new(0.36f, 0.86f, 0.48f, 1f);
    public static readonly Vector4 Warning = new(1f, 0.72f, 0.25f, 1f);
    public static readonly Vector4 Danger = new(1f, 0.38f, 0.36f, 1f);
    public static Vector4 Muted => MaterialTheme.Current.Colors.OnSurfaceVariant;
    public static Vector4 Panel => MaterialTheme.Current.Colors.SurfaceContainerLow;
    public static Vector4 Border => MaterialTheme.Current.Colors.OutlineVariant;

    public static void Heading(string title, string subtitle)
    {
        using (UiText.Font(UiFontRole.BodyStrong))
        {
            ImGui.PushStyleColor(ImGuiCol.Text,MaterialTheme.Current.Colors.OnSurface);
            UiGui.TextUnformatted(title);
            ImGui.PopStyleColor();
        }
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            using var font = UiText.Font(UiFontRole.Caption);
            MutedWrapped(subtitle);
        }
    }

    public static void IconHeading(string title,string subtitle,MaterialIcon icon,bool inlineSubtitle=false)
    {
        var scale=MaterialTheme.Metrics.Scale;
        var origin=ImGui.GetCursorScreenPos();
        float titleWidth, subtitleWidth;
        using (UiText.Font(UiFontRole.BodyStrong)) titleWidth=MaterialText.Measure(UiText.T(title)).X;
        using (UiText.Font(UiFontRole.Caption)) subtitleWidth=MaterialText.Measure(UiText.T(subtitle)).X;
        var inline=inlineSubtitle && ImGui.GetContentRegionAvail().X>=titleWidth+32*scale+ImGui.GetStyle().ItemSpacing.X+subtitleWidth;
        using (UiText.Font(UiFontRole.BodyStrong))
        {
            MaterialLayout.FitNextItemWidth(0,titleWidth+32*scale);
            origin=ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(24*scale,ImGui.GetTextLineHeight()));
        }
        MaterialIcons.Draw(icon,origin,24*scale,MaterialTheme.Current.Colors.OnSurface,ImGui.GetStyle().Alpha);
        ImGui.SameLine();ImGui.BeginGroup();Heading(title,string.Empty);
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            if (inline) ImGui.SameLine();
            using var font=UiText.Font(UiFontRole.Caption);
            MutedWrapped(subtitle);
        }
        ImGui.EndGroup();
    }

    public static void Caption(string text)
    {
        using var font=UiText.Font(UiFontRole.Caption);
        var translated=UiText.T(text);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX()+CaptionInset(translated));
        MaterialText.TextDisabled(translated);
    }

    public static float CaptionWidth(string text)
    {
        using var font=UiText.Font(UiFontRole.Caption);
        var translated=UiText.T(text);
        return CaptionInset(translated)+MaterialText.Measure(translated).X;
    }

    private static unsafe float CaptionInset(string text)
    {
        if (text.Length==0) return 0;
        if (MaterialText.RequiresShaping(text)) return MathF.Ceiling(ImGui.GetFontSize()/DadPresentation.AtlasHeight(UiFontRole.Caption));
        var font=ImGui.GetFont();
        var glyph=ImGui.FindGlyphNoFallback(font,text[0]);
        return glyph.Handle==null?0:MathF.Ceiling(Math.Max(0,-glyph.Handle->X0*ImGui.GetFontSize()/font.FontSize));
    }

    public static void Section(string title, string? subtitle = null)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        using (UiText.Font(UiFontRole.BodyStrong))
        {
            ImGui.PushStyleColor(ImGuiCol.Text,MaterialTheme.Current.Colors.OnSurface);
            UiGui.TextUnformatted(title);
            ImGui.PopStyleColor();
        }
        if (!string.IsNullOrWhiteSpace(subtitle))
            MutedWrapped(subtitle);
    }

    public static bool BeginCard(string id, float minimumHeight = 0f)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(DadPresentation.Compact?8:12,DadPresentation.Compact?6:10)*MaterialTheme.Metrics.Scale);
        var open = ImGui.BeginTable(
            id,
            1,
            ImGuiTableFlags.SizingStretchSame |
            ImGuiTableFlags.PadOuterX |
            ImGuiTableFlags.NoSavedSettings);
        if (!open)
        {
            ImGui.PopStyleVar();
            return false;
        }

        ImGui.TableNextRow(ImGuiTableRowFlags.None,MathF.Max(0f, minimumHeight)*MaterialTheme.Metrics.Scale);
        ImGui.TableNextColumn();
        return true;
    }

    public static void EndCard()
    {
        var table=ImGuiP.GetCurrentTable();
        if (table.IsInsideRow) ImGuiP.TableEndRow(table);
        ImGuiP.TablePushBackgroundChannel();
        DadPresentation.Surface(table.OuterRect.Min,new Vector2(table.OuterRect.Max.X,Math.Max(table.OuterRect.Max.Y,table.RowPosY2)));
        ImGuiP.TablePopBackgroundChannel();
        ImGui.EndTable();
        ImGui.PopStyleVar();
    }

    public static void Badge(string text,DadUiTone tone=DadUiTone.Neutral)
        => BadgeCore(text,tone,MaterialIcon.None);
    public static void IconBadge(string text,MaterialIcon icon,DadUiTone tone=DadUiTone.Neutral)
        => BadgeCore(text,tone,icon);
    private static void BadgeCore(string text,DadUiTone tone,MaterialIcon icon)
    {
        var scale=MaterialTheme.Metrics.Scale;var translated=UiText.T(text);
        var color=ToneColor(tone);var fill=WithAlpha(color,.10f);var border=WithAlpha(color,.38f);
        MaterialLayout.FitNextItemWidth(0,BadgeWidth(text)*MaterialTheme.Metrics.Scale);
        var textHeight=MaterialText.RequiresShaping(translated)?MaterialText.Measure(translated).Y:ImGui.GetTextLineHeight();
        MaterialStatus.Badge("",new MaterialControlAppearance(fill,color,border,4),new Vector2(BadgeWidth(text),Math.Max(28,(textHeight+8*scale)/scale)));
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();
        var origin=min+new Vector2(MaterialTheme.Metrics.Gap,(max.Y-min.Y-20*scale)*.5f);
        if (icon==MaterialIcon.None) ImGui.GetWindowDrawList().AddCircleFilled(origin+new Vector2(10,10)*scale,4*scale,MaterialCanvas.Color(color));
        else MaterialIcons.Draw(icon,origin,20*scale,color,ImGui.GetStyle().Alpha);
        color.W*=ImGui.GetStyle().Alpha;
        MaterialText.AddText(ImGui.GetWindowDrawList(),new Vector2(origin.X+28*scale,min.Y+(max.Y-min.Y-textHeight)*.5f),MaterialCanvas.Color(color),translated);
    }

    public static float BadgeWidth(string text) => (MaterialText.Measure(UiText.T(text)).X+28*MaterialTheme.Metrics.Scale+2*MaterialTheme.Metrics.Gap)/MaterialTheme.Metrics.Scale;

    public static bool IconButton(string label,MaterialIcon icon,DadUiTone tone=DadUiTone.Neutral,Vector2 size=default,string? display=null)
    {
        if (tone==DadUiTone.Neutral) return UiGui.IconButton(label,icon,size,display);
        var color=ToneColor(tone);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,4*MaterialTheme.Metrics.Scale);
        ImGui.PushStyleColor(ImGuiCol.Button,WithAlpha(color,tone==DadUiTone.Danger?.52f:.34f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered,WithAlpha(color,tone==DadUiTone.Danger?.72f:.54f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,WithAlpha(color,.82f));
        var clicked=UiGui.IconButton(label,icon,size,display);
        ImGui.PopStyleColor(3);ImGui.PopStyleVar();return clicked;
    }

    public static bool Button(string label, DadUiTone tone = DadUiTone.Neutral, Vector2 size = default)
    {
        if (tone == DadUiTone.Neutral)
            return UiGui.Button(label, size);

        var color = ToneColor(tone);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,4*MaterialTheme.Metrics.Scale);
        ImGui.PushStyleColor(ImGuiCol.Button, WithAlpha(color, tone == DadUiTone.Danger ? 0.52f : 0.34f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, WithAlpha(color, tone == DadUiTone.Danger ? 0.72f : 0.54f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, WithAlpha(color, 0.82f));
        var clicked = UiGui.Button(label, size);
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();
        return clicked;
    }

    public static void KeyValue(string label, string value, float preferredLabelWidth = 150f, bool translateValue = true)
    {
        var displayedValue = translateValue ? UiText.T(value) : value;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var scale=MaterialTheme.Metrics.Scale;
        var labelWidth=Math.Max(preferredLabelWidth*scale,MaterialText.Measure(UiText.T(label)).X);
        var start=ImGui.GetCursorPosX();
        MaterialText.TextDisabled(UiText.T(label));
        if (availableWidth >= labelWidth+ImGui.GetStyle().ItemSpacing.X+160*scale)
        {
            ImGui.SameLine(start+labelWidth+ImGui.GetStyle().ItemSpacing.X);
            UiGui.TextWrappedRaw(displayedValue);
        }
        else
        {
            ImGui.Indent();
            UiGui.TextWrappedRaw(displayedValue);
            ImGui.Unindent();
        }
    }

    public static float ButtonWidth(string label) => (MaterialText.Measure(UiText.T(label.Split("##",2)[0])).X+ImGui.GetStyle().FramePadding.X*2)/MaterialTheme.Metrics.Scale;
    public static float IconButtonWidth(string label) => UiGui.IconButtonWidth(label)/MaterialTheme.Metrics.Scale;

    public static void SameLineIfFits(float logicalWidth)
    {
        var right=ImGui.GetCursorScreenPos().X+ImGui.GetContentRegionAvail().X;
        if (ImGui.GetItemRectMax().X+ImGui.GetStyle().ItemSpacing.X+logicalWidth*MaterialTheme.Metrics.Scale<=right) ImGui.SameLine();
    }

    public static bool Action(string id,string label)
    {
        // Retain the previous helper's native ID while allowing the design's shorter visible label.
        var translated=UiText.T(label);
        return UiGui.Button(id,new Vector2(MaterialText.Measure(translated).X+24*MaterialTheme.Metrics.Scale,(DadPresentation.Compact?38:44)*MaterialTheme.Metrics.Scale),translated);
    }
    public static bool IconAction(string id,string label,MaterialIcon icon,float logicalWidth=0)
        => UiGui.IconButton(id,icon,new Vector2(logicalWidth*MaterialTheme.Metrics.Scale,(DadPresentation.Compact?38:44)*MaterialTheme.Metrics.Scale),UiText.T(label));

    public static bool Tile(string id,string title,string detail,MaterialIcon icon,float logicalHeight,bool emphasized=false)
    {
        var scale=MaterialTheme.Metrics.Scale;
        var colors=MaterialTheme.Current.Colors;
        float titleWidth,titleHeight;
        using (UiText.Font(UiFontRole.BodyStrong))
        { var titleSize=MaterialText.Measure(UiText.T(title));titleWidth=titleSize.X;titleHeight=titleSize.Y; }
        var width=MaterialLayout.FitNextItemWidth(-1,MathF.Ceiling(titleWidth+110*scale));
        var detailWidth=Math.Max(1,width-110*scale);
        float detailHeight;
        using (UiText.Font(UiFontRole.Caption)) detailHeight=MaterialText.Measure(UiText.T(detail),false,detailWidth).Y;
        var detailOffset=MaterialText.RequiresShaping(UiText.T(title))?Math.Max(22*scale,titleHeight):22*scale;
        var height=Math.Max(logicalHeight*scale,16*scale+detailOffset+detailHeight);
        var size=new Vector2(width,height);
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Button,emphasized?colors.PrimaryContainer:colors.SurfaceContainer);
        ImGui.PushStyleColor(ImGuiCol.Border,emphasized?colors.Primary:colors.OutlineVariant);
        var clicked=ImGui.Button(id,size);
        ImGui.PopStyleColor(3);
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();var dl=ImGui.GetWindowDrawList();
        dl.PushClipRect(min,max,true);
        var alpha=ImGui.GetStyle().Alpha;
        var iconSize=icon switch { MaterialIcon.Cart=>38f,MaterialIcon.Edit=>40f,_=>30f };
        MaterialIcons.Draw(icon,min+new Vector2(icon==MaterialIcon.Cart?18:16,(height/scale-iconSize)*.5f)*scale,iconSize*scale,Accent,alpha);
        var textOrigin=min+new Vector2(76,8)*scale;
        using (UiText.Font(UiFontRole.BodyStrong)) MaterialText.AddText(dl,textOrigin,MaterialCanvas.Color(WithAlpha(colors.OnSurface,alpha)),UiText.T(title));
        using (UiText.Font(UiFontRole.Caption))
            MaterialText.AddText(dl,ImGui.GetFont(),ImGui.GetFontSize(),textOrigin+new Vector2(0,detailOffset),MaterialCanvas.Color(WithAlpha(colors.OnSurfaceVariant,alpha)),UiText.T(detail),detailWidth);
        MaterialIcons.Draw(MaterialIcon.ArrowRight,new Vector2(max.X-30*scale,min.Y+(height/scale-18)*.5f*scale),18*scale,colors.OnSurfaceVariant,alpha);
        dl.PopClipRect();return clicked;
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
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        UiGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
