using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;

namespace dad.Windows;

// Keep original native IDs, popup identity, editing, focus and navigation behavior.
// Fields use the original ID with an empty native label; other widgets paint translated ink.
internal static class UiGui
{
    internal static void TextUnformatted(string text) => MaterialText.Text(UiText.T(text));
    internal static void TextWrapped(string text) => TextWrappedRaw(UiText.T(text));
    internal static void TextWrappedRaw(string text)
    {
        if (!MaterialText.RequiresShaping(text)) { MaterialText.TextWrapped(text);return; }
        var position=ImGui.GetCursorScreenPos();var clip=ImGui.GetWindowDrawList().GetClipRectMin();
        var bearing=MathF.Ceiling(MaterialTheme.Metrics.Scale);
        if (position.X-bearing<clip.X) ImGui.SetCursorPosX(ImGui.GetCursorPosX()+clip.X+bearing-position.X);
        position=ImGui.GetCursorScreenPos();
        var width=Math.Max(1,ImGui.GetContentRegionAvail().X);
        var size=MaterialText.Measure(text,wrapWidth:width);
        ImGui.Dummy(new Vector2(Math.Min(width,size.X),size.Y));
        if (ImGui.IsItemVisible()) MaterialText.AddText(ImGui.GetWindowDrawList(),position,ImGui.GetColorU32(ImGuiCol.Text),text,width);
    }
    internal static void TextDisabled(string text) { ImGui.PushTextWrapPos(0);MaterialText.TextDisabled(UiText.T(text));ImGui.PopTextWrapPos(); }
    internal static void TextColored(Vector4 color,string text) => MaterialText.TextColored(color,UiText.T(text));
    internal static void BulletText(string text) => MaterialText.BulletText(UiText.T(text));
    internal static void SetTooltip(string text) => MaterialText.SetTooltip(UiText.T(text));
    private static string Visible(string label) => UiText.T(label.Split("##",2)[0]);
    private static void Ink(string label,Vector2 position,Vector2 min,Vector2 max)
    {
        var dl=ImGui.GetWindowDrawList();dl.PushClipRect(min,max,true);
        try { MaterialText.AddText(dl,position,ImGui.GetColorU32(ImGuiCol.Text),label); }
        finally { dl.PopClipRect(); }
    }
    internal static bool Button(string original,Vector2 size=default,string? display=null)
    {
        var translated=display ?? Visible(original);
        size.X=MaterialLayout.FitNextItemWidth(size.X,MathF.Ceiling(MaterialText.Measure(translated).X+ImGui.GetStyle().FramePadding.X*2));
        if (MaterialText.RequiresShaping(translated)) size.Y=Math.Max(size.Y,MaterialText.Measure(translated).Y+2*ImGui.GetStyle().FramePadding.Y);
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);
        var clicked=ImGui.Button(original,size);ImGui.PopStyleColor();
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();
        Ink(translated,min+(max-min-MaterialText.Measure(translated))*.5f,min,max);
        if (MaterialText.Measure(translated).X>max.X-min.X-ImGui.GetStyle().FramePadding.X*2 && ImGui.IsItemHovered()) MaterialText.SetTooltip(translated);
        return clicked;
    }
    internal static bool SmallButton(string label)
    { ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,new Vector2(ImGui.GetStyle().FramePadding.X,0));var click=Button(label);ImGui.PopStyleVar();return click; }
    internal static float IconButtonWidth(string original,string? display=null)
        => MaterialText.Measure(display ?? Visible(original)).X+30*MaterialTheme.Metrics.Scale+2*ImGui.GetStyle().FramePadding.X;
    internal static bool IconButton(string original,MaterialIcon icon,Vector2 size=default,string? display=null)
    {
        var scale=MaterialTheme.Metrics.Scale;var text=display ?? Visible(original);
        size.X=MaterialLayout.FitNextItemWidth(size.X,MathF.Ceiling(IconButtonWidth(original,display)));
        size.Y=Math.Max(size.Y,ImGui.GetFrameHeight());
        if (MaterialText.RequiresShaping(text)) size.Y=Math.Max(size.Y,MaterialText.Measure(text).Y+2*ImGui.GetStyle().FramePadding.Y);
        var foreground=ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);var clicked=ImGui.Button(original,size);ImGui.PopStyleColor();
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();
        foreground.W*=ImGui.GetStyle().Alpha;
        var dl=ImGui.GetWindowDrawList();dl.PushClipRect(min,max,true);
        var x=min.X+Math.Max(ImGui.GetStyle().FramePadding.X,(max.X-min.X-MaterialText.Measure(text).X-30*scale)*.5f);
        MaterialIcons.Draw(icon,new Vector2(x,min.Y+(max.Y-min.Y-20*scale)*.5f),20*scale,foreground);
        MaterialText.AddText(dl,new Vector2(x+30*scale,min.Y+(max.Y-min.Y-(MaterialText.RequiresShaping(text)?MaterialText.Measure(text).Y:ImGui.GetTextLineHeight()))*.5f),MaterialCanvas.Color(foreground),text);
        dl.PopClipRect();return clicked;
    }
    internal static bool Checkbox(string label,ref bool value)
    {
        var translated=Visible(label);using var height=MaterialText.PushLineHeight(translated);
        var padding=ImGui.GetStyle().FramePadding;var gap=ImGui.GetStyle().ItemInnerSpacing;
        var original=label.Split("##",2)[0];
        var textWidth=MaterialText.Measure(translated).X;
        var reservedWidth=MaterialText.RequiresShaping(translated)?MathF.Ceiling(textWidth)+1:textWidth;
        MaterialLayout.FitNextItemWidth(0,ImGui.GetFrameHeight()+gap.X+Math.Max(ImGui.CalcTextSize(original).X,reservedWidth));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing,new Vector2(Math.Max(0,gap.X+reservedWidth-ImGui.CalcTextSize(original).X),gap.Y));
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);var changed=ImGui.Checkbox(label,ref value);ImGui.PopStyleColor();ImGui.PopStyleVar();
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();
        Ink(translated,min+new Vector2(ImGui.GetFrameHeight()+gap.X,MaterialText.RequiresShaping(translated)?(max.Y-min.Y-MaterialText.Measure(translated).Y)*.5f:padding.Y),min,new Vector2(Math.Max(max.X,min.X+ImGui.GetFrameHeight()+gap.X+reservedWidth),max.Y));return changed;
    }
    internal static bool RadioButton(string label,bool selected)
    {
        var translated=Visible(label);using var height=MaterialText.PushLineHeight(translated);
        var original=label.Split("##",2)[0];var gap=ImGui.GetStyle().ItemInnerSpacing;
        MaterialLayout.FitNextItemWidth(0,ImGui.GetFrameHeight()+gap.X+Math.Max(ImGui.CalcTextSize(original).X,MaterialText.Measure(translated).X));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing,new Vector2(Math.Max(0,gap.X+MaterialText.Measure(translated).X-ImGui.CalcTextSize(original).X),gap.Y));
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);var changed=ImGui.RadioButton(label,selected);ImGui.PopStyleColor();ImGui.PopStyleVar();
        var min=ImGui.GetItemRectMin();var p=min+new Vector2(ImGui.GetFrameHeight()+gap.X,MaterialText.RequiresShaping(translated)?(ImGui.GetItemRectMax().Y-min.Y-MaterialText.Measure(translated).Y)*.5f:ImGui.GetStyle().FramePadding.Y);
        Ink(translated,p,min,new Vector2(p.X+MaterialText.Measure(translated).X,ImGui.GetItemRectMax().Y));return changed;
    }
    internal static bool CollapsingHeader(string label,ImGuiTreeNodeFlags flags=ImGuiTreeNodeFlags.None)
        => Header(label,flags,MaterialIcon.None);
    internal static bool IconCollapsingHeader(string label,MaterialIcon icon,ImGuiTreeNodeFlags flags=ImGuiTreeNodeFlags.None)
        => Header(label,flags,icon);
    private static bool Header(string label,ImGuiTreeNodeFlags flags,MaterialIcon icon)
    {
        var scale=MaterialTheme.Metrics.Scale;var translated=Visible(label);using var height=MaterialText.PushLineHeight(translated);
        var padding=ImGui.GetStyle().FramePadding;
        var iconWidth=icon==MaterialIcon.None?0:28*scale;
        MaterialLayout.FitNextItemWidth(0,MaterialText.Measure(translated).X+ImGui.GetFontSize()+padding.X*2+iconWidth);
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);var open=ImGui.CollapsingHeader(label,flags);ImGui.PopStyleColor();
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();
        MaterialIcons.Draw(open?MaterialIcon.ChevronDown:MaterialIcon.ArrowRight,min+padding,ImGui.GetFontSize(),MaterialTheme.Current.Colors.OnSurface,ImGui.GetStyle().Alpha);
        var origin=min+new Vector2(ImGui.GetFontSize()+padding.X*2,MaterialText.RequiresShaping(translated)?(max.Y-min.Y-MaterialText.Measure(translated).Y)*.5f:padding.Y);
        if (icon!=MaterialIcon.None) MaterialIcons.Draw(icon,new Vector2(origin.X,min.Y+(max.Y-min.Y-20*scale)*.5f),20*scale,MaterialTheme.Current.Colors.OnSurface,ImGui.GetStyle().Alpha);
        Ink(translated,origin+new Vector2(iconWidth,0),min,max);
        if (origin.X+iconWidth+MaterialText.Measure(translated).X>max.X && ImGui.IsItemHovered()) MaterialText.SetTooltip(translated);
        return open;
    }
    internal static bool TreeNode(string label)
    {
        var translated=Visible(label);
        if (MaterialText.RequiresShaping(translated)) return MaterialText.TreeNode(label,display:translated);
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);var open=ImGui.TreeNode(label);ImGui.PopStyleColor();
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();
        MaterialIcons.Draw(open?MaterialIcon.ChevronDown:MaterialIcon.ArrowRight,min,ImGui.GetFontSize(),MaterialTheme.Current.Colors.OnSurface,ImGui.GetStyle().Alpha);
        var p=min+new Vector2(ImGui.GetFontSize()+ImGui.GetStyle().FramePadding.X*2,0);
        Ink(Visible(label),p,min,new Vector2(p.X+MaterialText.Measure(Visible(label)).X,max.Y));return open;
    }
    internal static bool BeginTabItem(string label,ImGuiTabItemFlags flags=ImGuiTabItemFlags.None)
    {
        var translated=Visible(label);using var height=MaterialText.PushLineHeight(translated);
        ImGui.SetNextItemWidth(MaterialText.Measure(translated).X+ImGui.GetStyle().FramePadding.X*2+12*MaterialTheme.Metrics.Scale);
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);var open=ImGui.BeginTabItem(label,flags);ImGui.PopStyleColor();
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();Ink(translated,min+(max-min-MaterialText.Measure(translated))*.5f,min,max);return open;
    }
    internal static bool BeginPrimaryTabItem(string label,ImGuiTabItemFlags flags,MaterialIcon icon,float width)
    {
        var scale=MaterialTheme.Metrics.Scale;using var font=UiText.Font(UiFontRole.BodyStrong);
        var height=(DadPresentation.Compact?40:44)*scale;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,new Vector2(12*scale,Math.Max(0,(height-ImGui.GetTextLineHeight())*.5f)));
        ImGui.SetNextItemWidth(MathF.Ceiling(Math.Max(width,MaterialText.Measure(Visible(label)).X+56*scale)));
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);var open=ImGui.BeginTabItem(label,flags);ImGui.PopStyleColor();ImGui.PopStyleVar();
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();var text=Visible(label);var textSize=MaterialText.Measure(text);
        var p=min+new Vector2(Math.Max(4*scale,(max.X-min.X-textSize.X-30*scale)*.5f),(max.Y-min.Y-24*scale)*.5f);
        var color=open?MaterialTheme.Current.Colors.Primary:MaterialTheme.Current.Colors.OnSurface;
        var dl=ImGui.GetWindowDrawList();dl.PushClipRect(min,max,true);MaterialIcons.Draw(icon,p,24*scale,color);
        MaterialText.AddText(dl,p+new Vector2(32*scale,(24*scale-textSize.Y)*.5f),MaterialCanvas.Color(color),text);
        if (open) dl.AddRectFilled(new Vector2(min.X,max.Y-3*scale),max,MaterialCanvas.Color(color));
        dl.PopClipRect();
        if (textSize.X+30*scale>max.X-min.X && ImGui.IsItemHovered()) MaterialText.SetTooltip(text);
        return open;
    }

    internal static bool BeginCombo(string label,string preview,ImGuiComboFlags flags=ImGuiComboFlags.None)
    {
        var translated=UiText.T(preview);
        return OpenCombo(label,translated,flags,MaterialText.Measure(translated).X);
    }
    private static bool OpenCombo(string label,string preview,ImGuiComboFlags flags,float textWidth)
    {
        BeginField(label,textWidth+2*ImGui.GetStyle().FramePadding.X+((flags & ImGuiComboFlags.NoArrowButton)==0?ImGui.GetFrameHeight():0));
        try
        {
            var open=MaterialText.BeginCombo("",preview,flags);
            if (!open) ImGui.PopID();
            return open;
        }
        catch { ImGui.PopID();throw; }
    }
    internal static void EndCombo() { ImGui.EndCombo();ImGui.PopID(); }
    internal static bool Selectable(string label,bool selected=false,ImGuiSelectableFlags flags=ImGuiSelectableFlags.None,Vector2 size=default)
    {
        var origin=ImGui.GetCursorScreenPos();
        size.X=Math.Max(size.X==0?ImGui.GetContentRegionAvail().X:size.X,MaterialText.Measure(Visible(label)).X);
        if (MaterialText.RequiresShaping(Visible(label))) return MaterialText.Selectable(label,selected,flags,size,Visible(label));
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);var changed=ImGui.Selectable(label,selected,flags,size);ImGui.PopStyleColor();
        var min=ImGui.GetItemRectMin();var max=ImGui.GetItemRectMax();Ink(Visible(label),origin,min,max);
        if (MaterialText.Measure(Visible(label)).X>max.X-min.X && ImGui.IsItemHovered()) MaterialText.SetTooltip(Visible(label));return changed;
    }
    private static float BeginField(string label,float minimum=0,bool hasStepButtons=false,float? requestedWidth=null)
    {
        // Capture the caller's pending width before the separate caption consumes NextItemWidth.
        var requested=requestedWidth ?? ImGui.CalcItemWidth();
        minimum=Math.Max(minimum,Math.Max(80*MaterialTheme.Metrics.Scale,MaterialText.Measure("00000000").X+2*ImGui.GetStyle().FramePadding.X));
        if (hasStepButtons) minimum+=2*(ImGui.GetFrameHeight()+ImGui.GetStyle().ItemInnerSpacing.X);
        if (label.Split("##",2)[0].Length>0) MaterialText.Text(Visible(label));
        var width=MaterialLayout.FitNextItemWidth(requested,MathF.Ceiling(minimum));
        ImGui.SetNextItemWidth(width);
        ImGuiP.PushOverrideID(ImGui.GetID(label));
        return width;
    }
    internal static bool InputText(string label,ref string value,int length,ImGuiInputTextFlags flags=ImGuiInputTextFlags.None)
    {
        using var height=MaterialText.PushLineHeight(value);
        BeginField(label,160*MaterialTheme.Metrics.Scale);
        try { return MaterialShapedInput.SingleLine("","",ref value,length,flags); }
        finally { ImGui.PopID(); }
    }
    internal static bool InputTextMultiline(string label,ref string value,int length,Vector2 size,ImGuiInputTextFlags flags=ImGuiInputTextFlags.None)
    {
        size.X=BeginField(label,160*MaterialTheme.Metrics.Scale,requestedWidth:size.X==0?ImGui.CalcItemWidth():size.X);
        try { return MaterialShapedInput.Multiline("",ref value,length,size,flags); }
        finally { ImGui.PopID(); }
    }
    internal static bool InputInt(string label,ref int value,int step=0,int fastStep=0,ImGuiInputTextFlags flags=ImGuiInputTextFlags.None)
    { BeginField(label,hasStepButtons:step>0);var changed=ImGui.InputInt("",ref value,step,fastStep,"%d",flags);ImGui.PopID();return changed; }
    internal static bool SliderInt(string label,ref int value,int min,int max,string format="%d",ImGuiSliderFlags flags=ImGuiSliderFlags.None)
    { BeginField(label);var changed=ImGui.SliderInt("",ref value,min,max,format,flags);ImGui.PopID();return changed; }
    internal static bool Combo(string label,ref int value,string[] options,int count)
    {
        var changed=false;
        var textWidth=options.Take(count).Select(option=>MaterialText.Measure(UiText.T(option)).X).DefaultIfEmpty(0).Max();
        if (OpenCombo(label,value>=0 && value<count?UiText.T(options[value]):string.Empty,ImGuiComboFlags.None,textWidth))
        {
            try
            {
                for(var index=0;index<count;index++)
                {
                    ImGui.PushID(index);
                    try { if (Selectable(options[index],value==index)) { changed=value!=index;value=index; }if (value==index) ImGui.SetItemDefaultFocus(); }
                    finally { ImGui.PopID(); }
                }
            }
            finally { EndCombo(); }
        }
        return changed;
    }
    internal static bool Combo(string label,ref int value,string options)
    { var items=options.Split('\0').Where(v=>v.Length>0).ToArray();return Combo(label,ref value,items,items.Length); }
    internal static bool BeginPopupModal(string original,ImGuiWindowFlags flags)
    {
        ImGui.SetNextWindowSize(new Vector2(520*MaterialTheme.Metrics.Scale,0),ImGuiCond.Always);
        var open=ImGui.BeginPopupModal(original,flags|ImGuiWindowFlags.HorizontalScrollbar);if(open) Title(original.Split("##",2)[0]);return open;
    }
    internal static void Title(string original,string? display=null)
    {
        var translated=display ?? UiText.T(original);
        var brand=original.StartsWith("DAD",StringComparison.Ordinal);
        if (translated==original && !brand) return;
        var style=ImGui.GetStyle();var fontSize=ImGui.GetFontSize();var height=ImGui.GetFrameHeight();
        var flags=ImGuiP.GetCurrentWindow().Flags;
        var collapseLeft=(flags & (ImGuiWindowFlags.NoCollapse|ImGuiWindowFlags.Modal))==0 && style.WindowMenuButtonPosition==ImGuiDir.Left;
        var p=ImGui.GetWindowPos()+new Vector2(style.FramePadding.X+(collapseLeft?fontSize+style.ItemInnerSpacing.X:0),style.FramePadding.Y);
        using var font=UiText.Font(UiFontRole.Body);
        var rightButtons=fontSize+style.FramePadding.X*2;
        if ((flags & (ImGuiWindowFlags.NoCollapse|ImGuiWindowFlags.Modal))==0 && style.WindowMenuButtonPosition==ImGuiDir.Right) rightButtons+=fontSize+style.ItemInnerSpacing.X;
        var edge=ImGui.GetWindowPos()+new Vector2(Math.Max(0,ImGui.GetWindowSize().X-rightButtons),height);
        var dl=ImGui.GetWindowDrawList();dl.PushClipRect(new Vector2(Math.Min(p.X,edge.X),p.Y),edge,false);
        var background=style.Colors[(int)(ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)?ImGuiCol.TitleBgActive:ImGuiCol.TitleBg)];
        var brandWidth=brand?fontSize+style.ItemInnerSpacing.X:0;
        dl.AddRectFilled(p,p+new Vector2(brandWidth+Math.Max(ImGui.CalcTextSize(original).X,MaterialText.Measure(translated).X)*fontSize/ImGui.GetFontSize(),height-style.FramePadding.Y),ImGui.ColorConvertFloat4ToU32(background));
        if (brand)
        {
            Vector2 P(float x,float y) => p+new Vector2(x,y)*fontSize;
            var accent=MaterialTheme.Current.Colors.Primary;
            var ink=MaterialCanvas.Color(accent);
            dl.AddRect(P(.08f,.49f),P(.26f,.91f),ink,fontSize*.03f,ImDrawFlags.None,Math.Max(1,fontSize*.08f));
            dl.PathLineTo(P(.26f,.82f));dl.PathLineTo(P(.41f,.89f));dl.PathLineTo(P(.82f,.89f));
            dl.PathLineTo(P(.88f,.74f));dl.PathLineTo(P(.88f,.42f));dl.PathLineTo(P(.73f,.36f));
            dl.PathLineTo(P(.61f,.39f));dl.PathLineTo(P(.61f,.14f));
            dl.PathBezierCubicCurveTo(P(.61f,.02f),P(.44f,.02f),P(.44f,.14f));
            dl.PathLineTo(P(.44f,.56f));dl.PathLineTo(P(.35f,.43f));
            dl.PathBezierCubicCurveTo(P(.28f,.35f),P(.2f,.42f),P(.24f,.5f));
            dl.PathLineTo(P(.34f,.68f));dl.PathStroke(ink,ImDrawFlags.None,Math.Max(1,fontSize*.08f));
        }
        MaterialText.AddText(dl,ImGui.GetFont(),fontSize,p+new Vector2(brandWidth,0),ImGui.GetColorU32(ImGuiCol.Text),translated);dl.PopClipRect();
    }

    internal static void TableHeadersRow()
    {
        var shapedHeight=Enumerable.Range(0,ImGui.TableGetColumnCount()).Select(index=>UiText.T(ImGui.TableGetColumnName(index)))
            .Where(MaterialText.RequiresShaping).Select(text=>MaterialText.Measure(text).Y).DefaultIfEmpty(0).Max();
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers,shapedHeight);
        for(var index=0;index<ImGui.TableGetColumnCount();index++)
        {
            if(!ImGui.TableSetColumnIndex(index)) continue;
            TableHeader(ImGui.TableGetColumnName(index));
        }
    }
    internal static void TableHeader(string original)
    {
        var translated=UiText.T(original);
        if (MaterialText.RequiresShaping(translated))
        {
            MaterialText.TableHeader(original,translated);
            if (MaterialText.Measure(translated).X>ImGui.GetContentRegionAvail().X && ImGui.IsItemHovered()) MaterialText.SetTooltip(translated);
            return;
        }
        var p=ImGui.GetCursorScreenPos();var width=ImGui.GetContentRegionAvail().X;
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);ImGui.TableHeader(original);ImGui.PopStyleColor();
        var dl=ImGui.GetWindowDrawList();dl.PushClipRect(p,p+new Vector2(Math.Max(1,width),ImGui.GetTextLineHeight()),true);
        MaterialText.AddText(dl,p,ImGui.GetColorU32(ImGuiCol.Text),translated);dl.PopClipRect();
        if(MaterialText.Measure(translated).X>width && ImGui.IsItemHovered()) MaterialText.SetTooltip(translated);
    }
}
