using AethertekUI;
using Dalamud;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;

namespace dad.Windows;

internal sealed class DadFonts : IDisposable
{
    private readonly IFontHandle[] handles;
    private int generation;
    internal int Generation => System.Threading.Volatile.Read(ref generation);
    internal DadFonts(IFontAtlas atlas, ushort[] ranges,string language)
    {
        handles=DadPresentation.FontSizes.Select((size,index)=>atlas.NewDelegateFontHandle(toolkit=>toolkit.OnPreBuild(build=>
        {
            build.NewImAtlas.TexDesiredWidth=4096;
            build.NewImAtlas.TexDesiredHeight=4096;
            size=DadPresentation.AtlasHeight((UiFontRole)index);
            var config=new SafeFontConfig { SizePx=size, GlyphRanges=ranges };
            build.Font=build.AddFontFromFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),DadPresentation.FontFiles[index]),config);
            build.AddFontFromFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),"seguisym.ttf"),
                new SafeFontConfig { SizePx=size, MergeFont=build.Font, GlyphRanges=ranges });
            // The language selector always displays all nine native names. Host-managed merges cover these too.
            build.AddDalamudAssetFont(DalamudAsset.NotoSansCjkRegular,new SafeFontConfig
            {
                SizePx=size, MergeFont=build.Font, GlyphRanges=ranges,
                // Verified bundled TTC faces: JP=0, KR=1, SC=2, TC=3.
                // Bundled faces share glyph coverage; select the active locale's regional forms.
                FontNo=language switch { "ko"=>1, "zh-Hans" or "zh-CN"=>2, "zh-Hant" or "zh-TW"=>3, _=>0 },
            });
            build.AttachExtraGlyphsForDalamudLanguage(new SafeFontConfig { SizePx=size, MergeFont=build.Font });
            build.AddGameSymbol(new SafeFontConfig { SizePx=size,MergeFont=build.Font });
        }))).ToArray();
        foreach(var handle in handles) handle.ImFontChanged+=FontChanged;
    }
    private void FontChanged(IFontHandle handle,ILockedImFont font) => System.Threading.Interlocked.Increment(ref generation);
    internal bool Ready => handles.All(h=>h.Available && h.LoadException is null);
    internal Exception? LoadException => handles.FirstOrDefault(h=>h.LoadException is not null)?.LoadException;
    internal unsafe void CheckGlyphs(IEnumerable<string> strings, MaterialTextRenderer shapedText)
    {
        var required=strings.ToArray();
        for(var index=0; index<handles.Length; index++)
        {
            using var font=handles[index].Lock();
            shapedText.CheckGlyphs(required, DadPresentation.AtlasHeight((UiFontRole)index) * ImGui.GetIO().FontGlobalScale);
            foreach(var text in required.Select(MaterialText.NativeGlyphText))
                foreach(var character in text.Where(c=>!char.IsControl(c)))
                    if(ImGui.FindGlyphNoFallback(font.ImFont,character).Handle==null)
                        throw new InvalidOperationException("Required UI glyph missing: U+"+((int)character).ToString("X4")+" in "+(UiFontRole)index);
        }
    }
    internal IDisposable Push(UiFontRole role)
    {
        var handle=handles[(int)role];
        if(!handle.Available || handle.LoadException is not null) throw new InvalidOperationException("DAD fonts are not ready.",handle.LoadException);
        return handle.Push();
    }
    public void Dispose() { foreach(var handle in handles) { handle.ImFontChanged-=FontChanged;handle.Dispose(); } }
}
